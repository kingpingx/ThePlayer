using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    BroadcastPlanner planner,
    IOptions<BroadcastOptions> options,
    TimeProvider clock,
    ILogger<BroadcastCoordinator> logger)
{
    private readonly BroadcastOptions _options = options.Value;

    /// <summary>Guards every field below. Held only for bookkeeping, never across an inspection.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<string, Broadcast> _broadcasts = [];
    private readonly Dictionary<string, string> _viewerToBroadcast = [];
    private readonly Dictionary<string, CachedFormat> _formats = [];

    private readonly record struct CachedFormat(VideoFormat Format, DateTimeOffset ExpiresAt);

    /// <summary>Starts watching, or joins whoever is already watching the same thing.</summary>
    public async Task<WatchTicket> AttachAsync(
        MediaAddress address,
        PlaybackMode mode,
        ClientDecodeSupport clientSupport,
        CancellationToken cancellationToken = default)
    {
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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_broadcasts.TryGetValue(key, out var existing))
            {
                existing.Attach(viewerId, clock.GetUtcNow());
                _viewerToBroadcast[viewerId] = key;

                logger.LogInformation(
                    "Viewer {ViewerId} joined broadcast {Key} ({Count} watching).",
                    viewerId,
                    key,
                    existing.ViewerCount);

                return TicketFor(existing, viewerId, availability);
            }

            var broadcast = new Broadcast(key, address, format, plan, mediaServerPath: key);

            // Publishing before the broadcast is visible means a viewer never sees a broadcast it
            // cannot yet play. If this throws, nothing has been registered.
            await mediaServer.PublishAsync(broadcast.MediaServerPath, address, cancellationToken);
            broadcast.MarkLive();

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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            expired = _broadcasts.Values
                .Where(broadcast => broadcast.ShouldStop(now, _options.Linger))
                .ToList();

            foreach (var broadcast in expired)
            {
                _broadcasts.Remove(broadcast.Key);
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

        // Unpublishing is I/O, so it happens outside the lock. The broadcasts are already
        // unreachable, so nothing can attach to them in the meantime.
        foreach (var broadcast in expired)
        {
            try
            {
                await mediaServer.RemoveAsync(broadcast.MediaServerPath, cancellationToken);
                logger.LogInformation("Stopped broadcast {Key}.", broadcast.Key);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not unpublish {Key}.", broadcast.Key);
            }
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
    /// Phase 1 delivers WebRTC passthrough only. Rejecting a plan we cannot execute - loudly, with
    /// the phase named - beats letting a viewer attach to a pipeline that will never produce a
    /// frame.
    /// </summary>
    private static void RejectIfNotYetImplemented(BroadcastPlan plan)
    {
        if (plan.Mode != PlaybackMode.ServerAssisted)
        {
            throw new NotSupportedException(
                $"{plan.Mode} is not implemented yet. Client-side decoding arrives in Phase 2 and " +
                "full server decoding in Phase 5. Use ServerAssisted for now.");
        }

        if (plan.RequiresConversion)
        {
            throw new NotSupportedException(
                "This stream needs converting to H.264 before a browser can play it, and " +
                "conversion arrives in Phase 3. An H.264 source plays today.");
        }
    }
}
