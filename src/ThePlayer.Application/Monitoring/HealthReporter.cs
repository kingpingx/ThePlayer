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
    EncoderFallbackLog encoderFallbacks,
    string environmentName)
{
    public async Task<SystemHealth> GetAsync(CancellationToken cancellationToken = default)
    {
        // Hardware inspection is cached by the implementation, so this is cheap to call per request.
        var hardware = await hardwareInspector.InspectAsync(cancellationToken);

        // Reported beside the profile list on purpose. The list says what was detected at startup;
        // this says what has since refused to run, and reading either one without the other gives
        // a picture of the machine that is out of date in a way nothing else here would reveal.
        return new SystemHealth(
            mediaServer.Status,
            hardware,
            environmentName,
            encoderFallbacks.Recent);
    }
}
