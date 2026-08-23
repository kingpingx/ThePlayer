using ThePlayer.Application.Broadcasting;

namespace ThePlayer.Api;

/// <summary>
/// Drives <see cref="BroadcastCoordinator.SweepAsync"/> on a timer, shutting down broadcasts that
/// have been empty past their linger window.
/// </summary>
/// <remarks>
/// The timer lives here rather than inside the coordinator so that tests can advance a fake clock
/// and call the sweep directly, instead of waiting on wall time for something to expire.
/// </remarks>
public sealed class BroadcastSweeper(
    BroadcastCoordinator coordinator,
    ILogger<BroadcastSweeper> logger) : BackgroundService
{
    /// <summary>
    /// Frequent enough that a broadcast stops soon after its linger elapses, cheap enough to
    /// ignore: the sweep is a dictionary scan over a handful of entries.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await coordinator.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad sweep must not kill the loop, or nothing is ever cleaned up again.
                logger.LogError(ex, "Broadcast sweep failed.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
