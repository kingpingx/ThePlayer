using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application.Security;
using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Playback;

namespace ThePlayer.Application.Broadcasting;

/// <summary>How broadcasts are shared and when they shut down.</summary>
public sealed class BroadcastOptions
{
    public const string SectionName = "Broadcasts";

    /// <summary>
    /// How long a broadcast stays alive after its last viewer leaves.
    /// <para>
    /// Without this, a page refresh tears down an FFmpeg process and immediately rebuilds it,
    /// which costs a reconnection to the camera and several seconds of black screen.
    /// </para>
    /// </summary>
    public TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long an inspection result is reused for the same address.
    /// <para>
    /// Inspection connects to the camera, so repeating it for every viewer of a popular stream is
    /// both slow and rude to the device.
    /// </para>
    /// </summary>
    public TimeSpan FormatCacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>What a viewer needs in order to start playing.</summary>
/// <param name="ViewerId">Opaque handle, used to detach later.</param>
/// <param name="Mode">The mode actually being served.</param>
/// <param name="Format">What the upstream turned out to be.</param>
/// <param name="Converted">
/// Whether the server is decoding and re-encoding. The single most interesting number in the
/// system - it is the difference between a busy GPU and an idle one.
/// </param>
/// <param name="Availability">Every mode and whether it could be used, with reasons.</param>
/// <param name="WhepUrl">Where to POST an SDP offer, for WebRTC modes.</param>
/// <param name="FrameSocketPath">Where to open a frame socket, for WebSocket modes.</param>
public sealed record WatchTicket(
    string ViewerId,
    PlaybackMode Mode,
    VideoFormat Format,
    bool Converted,
    IReadOnlyList<ModeAvailability> Availability,
    Uri? WhepUrl,
    string? FrameSocketPath);

/// <summary>One viewer's attachment to a running frame pipeline.</summary>
/// <param name="Initialisation">What the client needs to configure a decoder.</param>
/// <param name="Frames">The queue to read from. Completes when the broadcast ends.</param>
/// <param name="Release">Detaches this viewer from the pipeline.</param>
public sealed record FrameSubscription(
    StreamInitialisation Initialisation,
    ChannelReader<EncodedFrame> Frames,
    Action Release) : IDisposable
{
    public void Dispose() => Release();
}

/// <summary>
/// Owns every running broadcast: starts one when the first viewer asks, hands later viewers the
/// one already running, and stops it shortly after the last viewer leaves.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole "one pipeline, many clients" story. Two tabs on the same camera in the same
/// mode share a single FFmpeg process. Two clients needing <em>different</em> output - one that can
/// decode H.265 and one that cannot - get different plans, therefore different keys, therefore
/// their own pipelines from the same upstream.
/// </para>
/// <para>
/// Concrete, and holding its registry as private state rather than behind a port: it is
/// in-process bookkeeping with nothing to substitute.
/// </para>
/// </remarks>
public sealed class BroadcastCoordinator(
    IMediaInspector inspector,
    IMediaServer mediaServer,
    IHardwareInspector hardwareInspector,
    IFramePipeline framePipeline,
    BroadcastPlanner planner,
    AddressGuard addressGuard,
    IOptions<BroadcastOptions> options,
    TimeProvider clock,
    ILogger<BroadcastCoordinator> logger)
{
    private readonly BroadcastOptions _options = options.Value;

    /// <summary>Guards every field below. Held only for bookkeeping, never across I/O.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<string, Broadcast> _broadcasts = [];
    private readonly Dictionary<string, string> _viewerToBroadcast = [];
    private readonly Dictionary<string, CachedFormat> _formats = [];

    /// <summary>
    /// The frame pipelines, for the modes that deliver over a socket. Kept beside the broadcasts
    /// rather than inside them so that <c>Broadcast</c> stays a Domain type with no idea that
    /// channels or FFmpeg exist.
    /// </summary>
    private readonly Dictionary<string, FrameBroadcaster> _frameBroadcasters = [];

    private readonly record struct CachedFormat(VideoFormat Format, DateTimeOffset ExpiresAt);

    /// <summary>Starts watching, or joins whoever is already watching the same thing.</summary>
    public async Task<WatchTicket> AttachAsync(
        MediaAddress address,
        PlaybackMode mode,
        ClientDecodeSupport clientSupport,
        CancellationToken cancellationToken = default)
    {
        // Before anything connects. A refused address never reaches ffprobe, so nothing about the
        // target - not even how long it took to fail - leaks back to whoever asked.
        await addressGuard.EnsureAllowedAsync(address, cancellationToken);

        // Inspection talks to the camera and can take seconds, so it happens before the lock is
        // taken. Holding the lock across it would serialise every viewer in the system behind one
        // slow device.
        var format = await GetFormatAsync(address, cancellationToken);
        var hardware = await hardwareInspector.InspectAsync(cancellationToken);

        var availability = planner.Availability(format, clientSupport);
        var plan = planner.Plan(format, mode, clientSupport, hardware);

        RejectIfNotYetImplemented(plan);

        var key = plan.KeyFor(address.Fingerprint);
        var viewerId = Guid.NewGuid().ToString("n")[..16];

        // Fast path: something is already running for this exact plan.
        FrameBroadcaster? finished = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_broadcasts.TryGetValue(key, out var running))
            {
                if (HasFinished(key, running))
                {
                    // A file that already played to its last frame. Watching it again means
                    // starting it again, not joining a broadcast with nothing left to deliver.
                    _broadcasts.Remove(key);
                    _frameBroadcasters.Remove(key, out finished);
                }
                else
                {
                    return Join(running, viewerId, availability);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        await DiscardAsync(finished);

        // Starting a pipeline is I/O, and a frame pipeline waits for a keyframe before it can
        // describe the stream - seconds, on a camera with a long GOP. Doing that under the lock
        // would stall every other viewer in the system, so it happens outside and the race is
        // settled afterwards.
        var started = await StartPipelineAsync(key, address, plan, format, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_broadcasts.TryGetValue(key, out var raced))
            {
                // Someone else finished first. Theirs is already registered and may already have
                // viewers, so ours is the one that goes.
                logger.LogDebug("Discarding a duplicate pipeline for {Key}; another viewer won.", key);
                await DiscardAsync(started);

                return Join(raced, viewerId, availability);
            }

            var broadcast = new Broadcast(key, address, format, plan, mediaServerPath: key);
            broadcast.MarkLive();

            if (started is not null)
            {
                _frameBroadcasters[key] = started;
            }

            broadcast.Attach(viewerId, clock.GetUtcNow());
            _broadcasts[key] = broadcast;
            _viewerToBroadcast[viewerId] = key;

            logger.LogInformation(
                "Started broadcast {Key} for {Address} ({Format}, converted={Converted}).",
                key,
                address,
                format,
                plan.RequiresConversion);

            return TicketFor(broadcast, viewerId, availability);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings a broadcast into being: published on the edge server for WebRTC, or a frame pipeline
    /// of our own for the socket modes.
    /// </summary>
    /// <returns>The broadcaster to register, or <c>null</c> for the WebRTC path.</returns>
    private async Task<FrameBroadcaster?> StartPipelineAsync(
        string key,
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        CancellationToken cancellationToken)
    {
        if (plan.Mode == PlaybackMode.ServerAssisted)
        {
            // MediaMTX pulls the source itself and serves it over WebRTC; this process never sees
            // a frame. Publishing is idempotent, so a lost race needs no undoing.
            await mediaServer.PublishAsync(key, address, cancellationToken);
            return null;
        }

        var stream = await framePipeline.StartAsync(address, plan, format, cancellationToken);
        return new FrameBroadcaster(stream, key, logger);
    }

    private static async Task DiscardAsync(FrameBroadcaster? broadcaster)
    {
        if (broadcaster is not null)
        {
            await broadcaster.DisposeAsync();
        }
    }

    private WatchTicket Join(Broadcast broadcast, string viewerId, IReadOnlyList<ModeAvailability> availability)
    {
        broadcast.Attach(viewerId, clock.GetUtcNow());
        _viewerToBroadcast[viewerId] = broadcast.Key;

        logger.LogInformation(
            "Viewer {ViewerId} joined broadcast {Key} ({Count} watching).",
            viewerId,
            broadcast.Key,
            broadcast.ViewerCount);

        return TicketFor(broadcast, viewerId, availability);
    }

    /// <summary>
    /// Attaches a viewer to the frame stream it was promised, for the socket modes.
    /// </summary>
    /// <returns>
    /// The subscription, or <c>null</c> if this viewer id is unknown or is not on a socket mode -
    /// which is the socket endpoint's cue to refuse the connection.
    /// </returns>
    public async Task<FrameSubscription?> SubscribeAsync(
        string viewerId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_viewerToBroadcast.TryGetValue(viewerId, out var key) ||
                !_frameBroadcasters.TryGetValue(key, out var broadcaster))
            {
                return null;
            }

            return new FrameSubscription(
                broadcaster.Initialisation,
                broadcaster.Subscribe(viewerId),
                () => broadcaster.Unsubscribe(viewerId));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops watching. The broadcast is not torn down immediately - see
    /// <see cref="BroadcastOptions.Linger"/>.
    /// </summary>
    public async Task DetachAsync(string viewerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_viewerToBroadcast.Remove(viewerId, out var key) ||
                !_broadcasts.TryGetValue(key, out var broadcast))
            {
                // Detaching twice, or after a sweep already removed the broadcast. Not an error -
                // a browser closing a tab may well send both a beacon and a socket close.
                return;
            }

            if (_frameBroadcasters.TryGetValue(key, out var broadcaster))
            {
                broadcaster.Unsubscribe(viewerId);
            }

            if (broadcast.Detach(viewerId, clock.GetUtcNow()))
            {
                logger.LogInformation(
                    "Broadcast {Key} has no viewers; stopping in {Linger}.",
                    key,
                    _options.Linger);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Shuts down broadcasts that have been empty past the linger window. Driven by a timer in
    /// the host rather than by a timer of its own, so tests can advance it deliberately.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        List<Broadcast> expired;
        var pipelines = new List<FrameBroadcaster>();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A pipeline that ran out of frames - a file reaching its last one. The broadcast is
            // still registered and still has viewers, but there is nothing more to send, so its
            // countdown starts here rather than waiting for viewers that may never detach.
            foreach (var (key, broadcaster) in _frameBroadcasters)
            {
                if (broadcaster.HasEnded && _broadcasts.TryGetValue(key, out var ended))
                {
                    ended.MarkEnded(now);
                }
            }

            expired = _broadcasts.Values
                .Where(broadcast => broadcast.ShouldStop(now, _options.Linger))
                .ToList();

            foreach (var broadcast in expired)
            {
                _broadcasts.Remove(broadcast.Key);

                if (_frameBroadcasters.Remove(broadcast.Key, out var broadcaster))
                {
                    pipelines.Add(broadcaster);
                }
            }

            foreach (var expiredKey in _formats
                .Where(entry => entry.Value.ExpiresAt <= now)
                .Select(entry => entry.Key)
                .ToList())
            {
                _formats.Remove(expiredKey);
            }
        }
        finally
        {
            _gate.Release();
        }

        // Tearing down is I/O, so it happens outside the lock. The broadcasts are already
        // unreachable, so nothing can attach to them in the meantime.
        foreach (var broadcaster in pipelines)
        {
            try
            {
                await broadcaster.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not stop a frame pipeline cleanly.");
            }
        }

        foreach (var broadcast in expired.Where(b => b.Plan.Mode == PlaybackMode.ServerAssisted))
        {
            try
            {
                await mediaServer.RemoveAsync(broadcast.MediaServerPath, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not unpublish {Key}.", broadcast.Key);
            }
        }

        foreach (var broadcast in expired)
        {
            logger.LogInformation("Stopped broadcast {Key}.", broadcast.Key);
        }
    }

    /// <summary>What is live right now. Addresses are redacted.</summary>
    public async Task<IReadOnlyList<Broadcast>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _broadcasts.Values.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether a registered broadcast has nothing left to give. Called under the lock.
    /// </summary>
    /// <remarks>
    /// Checks the pipeline as well as the recorded state, because the sweep is what transcribes one
    /// into the other and a viewer can arrive between two sweeps.
    /// </remarks>
    private bool HasFinished(string key, Broadcast broadcast) =>
        broadcast.IsFinished ||
        (_frameBroadcasters.TryGetValue(key, out var broadcaster) && broadcaster.HasEnded);

    private async Task<VideoFormat> GetFormatAsync(MediaAddress address, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_formats.TryGetValue(address.Fingerprint, out var cached) && cached.ExpiresAt > now)
            {
                return cached.Format;
            }
        }
        finally
        {
            _gate.Release();
        }

        var format = await inspector.InspectAsync(address, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Two viewers arriving together may both have inspected. Harmless - the results agree,
            // and the cost is one extra ffprobe rather than a correctness problem.
            _formats[address.Fingerprint] = new CachedFormat(format, now + _options.FormatCacheDuration);
        }
        finally
        {
            _gate.Release();
        }

        return format;
    }

    private WatchTicket TicketFor(
        Broadcast broadcast,
        string viewerId,
        IReadOnlyList<ModeAvailability> availability) =>
        new(
            viewerId,
            broadcast.Plan.Mode,
            broadcast.Format,
            broadcast.Plan.RequiresConversion,
            availability,
            WhepUrl: broadcast.Plan.Mode == PlaybackMode.ServerAssisted
                ? mediaServer.WhepUrlFor(broadcast.MediaServerPath)
                : null,
            FrameSocketPath: broadcast.Plan.Mode == PlaybackMode.ServerAssisted
                ? null
                : $"/ws/frames/{viewerId}");

    /// <summary>
    /// Rejects a plan this phase cannot execute - loudly, with the phase named - rather than
    /// letting a viewer attach to a pipeline that will never produce a frame.
    /// </summary>
    /// <remarks>
    /// Phase 2 added the frame socket, so client-side decoding of a stream the browser already
    /// understands now works. What remains is conversion (Phase 3) and full server decoding
    /// (Phase 5).
    /// </remarks>
    private static void RejectIfNotYetImplemented(BroadcastPlan plan)
    {
        if (plan.Mode == PlaybackMode.ServerDecoded)
        {
            throw new NotSupportedException(
                "Full server decoding arrives in Phase 5. Use ClientDecoded or ServerAssisted for now.");
        }

        if (plan.RequiresConversion)
        {
            throw new NotSupportedException(
                "This stream has to be converted before this client can play it, and conversion " +
                "arrives in Phase 3. A source your browser can already decode plays today.");
        }
    }
}
