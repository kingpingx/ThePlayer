using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Playback;

namespace ThePlayer.Domain.Broadcasting;

/// <summary>Where a broadcast is in its life.</summary>
public enum BroadcastState
{
    /// <summary>The pipeline is being set up; no viewer can play yet.</summary>
    Starting,

    Live,

    /// <summary>A file reached its last frame. Cameras never reach this.</summary>
    Ended,

    Failed,
}

/// <summary>
/// The decision about how to deliver one stream: what the server will do to it, and with what.
/// </summary>
/// <remarks>
/// Produced by <c>BroadcastPlanner</c> as a pure function of the media, the requested mode, and
/// what the client said it can decode. Separating the decision from the running pipeline is what
/// makes the negotiation table a set of plain unit tests instead of something only observable by
/// watching a camera.
/// </remarks>
/// <param name="Mode">The playback mode this plan serves.</param>
/// <param name="OutputCodec">What the client will ultimately receive.</param>
/// <param name="RequiresConversion">
/// Whether the server must decode and re-encode. False means the bytes pass through untouched,
/// which is the difference between a busy GPU and an idle one.
/// </param>
/// <param name="Acceleration">Which engine does the conversion. Null when none is needed.</param>
public sealed record BroadcastPlan(
    PlaybackMode Mode,
    VideoCodec OutputCodec,
    bool RequiresConversion,
    AccelerationProfile? Acceleration)
{
    /// <summary>
    /// The identity of a broadcast: an address plus everything about how it is being delivered.
    /// </summary>
    /// <remarks>
    /// Two viewers share a pipeline only when they want the same bytes. One client that can decode
    /// H.265 and one that cannot are watching the same camera but need different output, so they
    /// get different keys and therefore different pipelines.
    /// <para>
    /// Built from the address <em>fingerprint</em>, never the address itself, so a camera password
    /// cannot end up as a dictionary key or in a diagnostic dump of what is live.
    /// </para>
    /// </remarks>
    public string KeyFor(string addressFingerprint) =>
        $"{addressFingerprint}-{Mode}-{OutputCodec}{(RequiresConversion ? "-converted" : string.Empty)}"
            .ToLowerInvariant();
}

/// <summary>
/// One pipeline currently running, shared by everyone watching it.
/// </summary>
/// <remarks>
/// <para>
/// Ephemeral by design. Nothing here is persisted: the address arrives on each request, and since
/// it carries a password we actively do not want to store it. A broadcast exists only while it is
/// running, starts when the first viewer asks, and stops shortly after the last one leaves.
/// </para>
/// <para>
/// Viewers are held as bare ids rather than as objects. Everyone attached to a broadcast shares
/// its plan by definition - that is what the key means - so a viewer has no state of its own worth
/// modelling.
/// </para>
/// <para>
/// Not thread-safe on its own. <c>BroadcastCoordinator</c> owns every instance and serialises
/// access to it.
/// </para>
/// </remarks>
public sealed class Broadcast
{
    private readonly HashSet<string> _viewerIds = [];

    public Broadcast(
        string key,
        MediaAddress address,
        VideoFormat format,
        BroadcastPlan plan,
        string mediaServerPath)
    {
        Key = key;
        Address = address;
        Format = format;
        Plan = plan;
        MediaServerPath = mediaServerPath;
        State = BroadcastState.Starting;
    }

    public string Key { get; }

    /// <summary>The upstream. Renders redacted, so logging it is safe.</summary>
    public MediaAddress Address { get; }

    public VideoFormat Format { get; }

    public BroadcastPlan Plan { get; }

    /// <summary>The path this stream is published under on the edge server.</summary>
    public string MediaServerPath { get; }

    public BroadcastState State { get; private set; }

    public string? FailureReason { get; private set; }

    public int ViewerCount => _viewerIds.Count;

    /// <summary>
    /// When the last viewer left, or null while someone is still watching.
    /// <para>
    /// A broadcast lingers briefly after emptying so that a page refresh re-attaches to the
    /// running pipeline instead of tearing down an FFmpeg process and immediately rebuilding it.
    /// </para>
    /// </summary>
    public DateTimeOffset? EmptySince { get; private set; }

    public bool IsPlayable => State == BroadcastState.Live;

    public void MarkLive()
    {
        State = BroadcastState.Live;
        FailureReason = null;
    }

    /// <summary>A file reaching its last frame. Not an error.</summary>
    public void MarkEnded() => State = BroadcastState.Ended;

    public void MarkFailed(string reason)
    {
        State = BroadcastState.Failed;
        FailureReason = reason;
    }

    public void Attach(string viewerId, DateTimeOffset now)
    {
        _viewerIds.Add(viewerId);
        EmptySince = null;
    }

    /// <returns><c>true</c> if that was the last viewer, and the linger countdown has begun.</returns>
    public bool Detach(string viewerId, DateTimeOffset now)
    {
        if (!_viewerIds.Remove(viewerId) || _viewerIds.Count > 0)
        {
            return false;
        }

        EmptySince = now;
        return true;
    }

    public bool HasViewer(string viewerId) => _viewerIds.Contains(viewerId);

    /// <summary>Whether this broadcast has been empty long enough to shut down.</summary>
    public bool ShouldStop(DateTimeOffset now, TimeSpan linger) =>
        EmptySince is { } since && now - since >= linger;
}
