using ThePlayer.Application.Monitoring;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Api;

/// <summary>
/// The HTTP surface. Endpoints translate between the wire contract and the application layer and
/// do nothing else - no orchestration, no decisions.
/// </summary>
public static class Endpoints
{
    public static void MapThePlayerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", async (HealthReporter reporter, CancellationToken cancellationToken) =>
            {
                var health = await reporter.GetAsync(cancellationToken);

                // 200 when usable, 503 when not, so container and uptime probes work without
                // parsing the body. The body explains *why* either way.
                return health.IsHealthy
                    ? Results.Ok(ToResponse(health))
                    : Results.Json(ToResponse(health), statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("GetHealth")
            .Produces<HealthResponse>()
            .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);
    }

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
                .ToList()));
}
