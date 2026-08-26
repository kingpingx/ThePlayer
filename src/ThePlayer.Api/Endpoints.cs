using ThePlayer.Application;
using ThePlayer.Application.Broadcasting;
using ThePlayer.Application.Security;
using ThePlayer.Application.Monitoring;
using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Monitoring;
using ThePlayer.Domain.Playback;

namespace ThePlayer.Api;

/// <summary>
/// The HTTP surface. Endpoints translate between the wire contract and the application layer and
/// do nothing else - no orchestration, no decisions.
/// </summary>
public static class Endpoints
{
    public static void MapThePlayerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", async (
                HttpContext context,
                HealthReporter reporter,
                ApiKeyGuard guard,
                CancellationToken cancellationToken) =>
            {
                var health = await reporter.GetAsync(cancellationToken);

                // Reachable without a key, because a liveness probe that needs a secret is a
                // liveness probe that ends up switched off. What it *says* still depends on one:
                // the full body inventories the machine's hardware and its recent failures, and
                // that is not something to hand an anonymous caller on a public host.
                object body = Authorised(context, guard)
                    ? ToResponse(health)
                    : new BriefHealthResponse(health.IsHealthy, health.Environment);

                // 200 when usable, 503 when not, so probes work without parsing the body at all.
                return health.IsHealthy
                    ? Results.Ok(body)
                    : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("GetHealth")
            .Produces<HealthResponse>()
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/watch", StartWatchingAsync)
            .WithName("StartWatching")
            .Produces<WatchResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        app.MapDelete("/api/watch/{viewerId}", async (
                string viewerId,
                BroadcastCoordinator coordinator,
                CancellationToken cancellationToken) =>
            {
                await coordinator.DetachAsync(viewerId, cancellationToken);

                // Always 204, even for an unknown viewer. A browser closing a tab may send both a
                // beacon and a socket close, and the second one is not an error.
                return Results.NoContent();
            })
            .WithName("StopWatching");

        // Not a minimal-API result: the handler upgrades the connection and then owns it for the
        // life of the stream, so it writes to the response itself rather than returning one.
        app.MapGet("/ws/frames/{viewerId}", (
                string viewerId,
                HttpContext context,
                BroadcastCoordinator coordinator,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            VideoStreamSocket.HandleAsync(context, viewerId, coordinator, loggerFactory, cancellationToken))
            .WithName("StreamFrames")
            .ExcludeFromDescription();

        // Not a minimal-API result either: the handler holds the response open and writes to it
        // for the life of the connection.
        app.MapGet("/api/metrics/stream", (
                HttpContext context,
                MetricsCollector collector,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            MetricsStream.HandleAsync(context, collector, loggerFactory, cancellationToken))
            .WithName("StreamMetrics")
            .ExcludeFromDescription();

        app.MapGet("/api/broadcasts", async (
                BroadcastCoordinator coordinator,
                CancellationToken cancellationToken) =>
            {
                var live = await coordinator.ListAsync(cancellationToken);
                return Results.Ok(live.Select(ToSummary).ToList());
            })
            .WithName("ListBroadcasts")
            .Produces<IReadOnlyList<BroadcastSummary>>();
    }

    private static async Task<IResult> StartWatchingAsync(
        WatchRequest request,
        BroadcastCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!MediaAddress.TryParse(request.Address, out var address, out var addressError))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid address", addressError);
        }

        if (!Enum.TryParse<PlaybackMode>(request.Mode, ignoreCase: true, out var mode))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Unknown mode",
                $"'{request.Mode}' is not a playback mode. Expected one of: " +
                $"{string.Join(", ", Enum.GetNames<PlaybackMode>())}.");
        }

        try
        {
            var ticket = await coordinator.AttachAsync(
                address,
                mode,
                ToClientSupport(request.ClientDecodeSupport),
                cancellationToken);

            return Results.Ok(ToResponse(ticket));
        }
        catch (AddressNotAllowedException ex)
        {
            // 403 rather than 400: the request is well formed and understood, this deployment just
            // will not connect to that address.
            return Problem(StatusCodes.Status403Forbidden, "Address not allowed", ex.Message);
        }
        catch (MediaInspectionException ex)
        {
            // The upstream is the problem, not the request. 502 says so, and the message is
            // already scrubbed of credentials by the inspector.
            return Problem(StatusCodes.Status502BadGateway, "Could not read the stream", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // The planner rejected this mode for this stream and client - the reason is written
            // to be shown to a user.
            return Problem(StatusCodes.Status400BadRequest, "Mode not available", ex.Message);
        }
    }

    private static IResult Problem(int statusCode, string title, string? detail) =>
        Results.Problem(detail: detail, title: title, statusCode: statusCode);

    /// <summary>
    /// Whether this caller has earned the detailed health body.
    /// </summary>
    /// <remarks>
    /// Asked here rather than in the middleware because health is the one endpoint that answers
    /// either way. A deployment demanding no key at all authorises everyone, which is the same
    /// answer it gives everywhere else.
    /// </remarks>
    private static bool Authorised(HttpContext context, ApiKeyGuard guard)
    {
        if (!guard.Required)
        {
            return true;
        }

        var presented = context.Request.Headers.TryGetValue(ApiKeyGuard.HeaderName, out var header) && header.Count > 0
            ? header[0]
            : context.Request.Query[ApiKeyGuard.QueryName].FirstOrDefault();

        return guard.Accepts(presented);
    }

    private static ClientDecodeSupport ToClientSupport(ClientDecodeSupportRequest? request)
    {
        if (request is null)
        {
            return ClientDecodeSupport.None;
        }

        var codecs = request.Codecs
            .Select(codec => new
            {
                Parsed = Enum.TryParse<VideoCodec>(codec.Codec, ignoreCase: true, out var value)
                    ? value
                    : VideoCodec.Unknown,
                codec.Supported,
                codec.HardwareAccelerated,
            })
            // A codec name we do not recognise is dropped rather than treated as decodable. Being
            // wrong in that direction produces a stream the client cannot play.
            .Where(entry => entry.Parsed != VideoCodec.Unknown)
            .Select(entry => new CodecSupport(entry.Parsed, entry.Supported, entry.HardwareAccelerated))
            .ToList();

        return new ClientDecodeSupport(request.WebCodecs, codecs);
    }

    private static WatchResponse ToResponse(WatchTicket ticket) => new(
        ViewerId: ticket.ViewerId,
        Mode: ticket.Mode.ToString(),
        Format: ToResponse(ticket.Format),
        Converted: ticket.Converted,
        ModeAvailability: ticket.Availability
            .Select(entry => new ModeAvailabilityResponse(
                entry.Mode.ToString(),
                entry.Available,
                entry.Reason))
            .ToList(),
        Transport: ticket.WhepUrl is { } whep
            ? new TransportResponse("WebRtc", whep.ToString())
            : new TransportResponse("WebSocket", ticket.FrameSocketPath ?? string.Empty));

    private static VideoFormatResponse ToResponse(VideoFormat format) => new(
        Codec: format.Codec.ToString(),
        Width: format.Width,
        Height: format.Height,
        FrameRate: format.FrameRate,
        Live: format.IsLive,
        DurationSeconds: format.Duration?.TotalSeconds);

    private static BroadcastSummary ToSummary(Broadcast broadcast) => new(
        Key: broadcast.Key,
        // Display, never the raw address - this endpoint is a diagnostic surface and would
        // otherwise be the easiest place in the system to read a camera password.
        Address: broadcast.Address.Display,
        State: broadcast.State.ToString(),
        Mode: broadcast.Plan.Mode.ToString(),
        Converted: broadcast.Plan.RequiresConversion,
        Viewers: broadcast.ViewerCount,
        Format: ToResponse(broadcast.Format));

    private static HealthResponse ToResponse(SystemHealth health) => new(
        Healthy: health.IsHealthy,
        Environment: health.Environment,
        MediaServer: new MediaServerHealth(
            State: health.MediaServer.State.ToString(),
            Version: health.MediaServer.Version,
            RestartCount: health.MediaServer.RestartCount,
            Error: health.MediaServer.LastError),
        Hardware: new HardwareHealth(
            FFmpegVersion: health.Hardware.FFmpegVersion,
            HasHardwareAcceleration: health.Hardware.HasHardwareAcceleration,
            PreferredProfile: health.Hardware.Preferred.DisplayName,
            Profiles: health.Hardware.Profiles
                .Select(profile => new AccelerationProfileInfo(
                    Kind: profile.Kind.ToString(),
                    Name: profile.DisplayName,
                    IsHardware: profile.IsHardware,
                    Encoder: profile.H264Encoder,
                    DecodeAccelerator: profile.DecodeAccelerator))
                .ToList(),
            DecodableCodecs: health.Hardware.DecodableCodecs
                .Select(codec => codec.ToString())
                .ToList()),
        EncoderFallbacks: health.EncoderFallbacks
            .Select(fallback => new EncoderFallbackInfo(
                FailedProfile: fallback.FailedProfile,
                FailedEncoder: fallback.FailedEncoder,
                ReplacementProfile: fallback.ReplacementProfile,
                Reason: fallback.Reason,
                At: fallback.At))
            .ToList());
}
