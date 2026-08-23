using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Application.Monitoring;

/// <summary>
/// Assembles the answer to <c>/api/health</c> from the pieces that know their own state.
/// </summary>
/// <remarks>
/// Concrete rather than behind a port: it performs no I/O of its own, it only composes two ports
/// that do. That also makes it trivially testable with fakes for both.
/// </remarks>
public sealed class HealthReporter(
    IMediaServerSupervisor mediaServer,
    IHardwareInspector hardwareInspector,
    string environmentName)
{
    public async Task<SystemHealth> GetAsync(CancellationToken cancellationToken = default)
    {
        // Hardware inspection is cached by the implementation, so this is cheap to call per request.
        var hardware = await hardwareInspector.InspectAsync(cancellationToken);

        return new SystemHealth(mediaServer.Status, hardware, environmentName);
    }
}
