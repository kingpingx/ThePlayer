using Microsoft.Extensions.Options;
using ThePlayer.Application.Monitoring;

namespace ThePlayer.Api;

/// <summary>
/// Drives <see cref="MetricsCollector.SampleAsync"/> on a timer.
/// </summary>
/// <remarks>
/// <para>
/// The timer lives here rather than inside the collector for the same reason the broadcast sweep's
/// does: a test can then call the sample directly instead of waiting on wall time for one to
/// happen.
/// </para>
/// <para>
/// The collector does nothing when nobody is listening, so this loop ticks on an idle server
/// without spending a process spawn on a GPU reading nobody asked for.
/// </para>
/// </remarks>
public sealed class MetricsSampler(
    MetricsCollector collector,
    IOptions<MetricsOptions> options,
    ILogger<MetricsSampler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Interval;

        if (interval <= TimeSpan.Zero)
        {
            logger.LogWarning("Metrics interval is not positive; the metrics feed will not sample.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await collector.SampleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad sample must not end the loop, or the feed goes quiet for the life of the
                // server and looks exactly like a machine doing nothing.
                logger.LogError(ex, "Metrics sample failed.");
            }

            if (!await DelayAsync(interval, stoppingToken))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Waits <paramref name="interval"/> <em>after</em> the work, rather than ticking on a fixed
    /// schedule.
    /// </summary>
    /// <remarks>
    /// <see cref="PeriodicTimer"/> was the obvious choice and is the wrong one here. It keeps a
    /// fixed rate, so a sample that overruns its interval - which happens whenever nvidia-smi is
    /// slow to start - leaves a tick already due, and the next iteration fires immediately. The
    /// observed result was pairs of readings under a tenth of a second apart, and a process spawned
    /// for each. Sleeping after the work instead guarantees a minimum gap between samples, which is
    /// what the cost of sampling actually depends on.
    /// </remarks>
    /// <returns><c>false</c> when the host is shutting down.</returns>
    private static async Task<bool> DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(interval, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
