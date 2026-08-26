using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application.Broadcasting;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Application.Monitoring;

/// <summary>How often the machine is measured, and how patient the feed is with a slow client.</summary>
public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>
    /// How often to take a reading.
    /// <para>
    /// One second is what the numbers are worth: CPU and GPU utilisation are already averages over
    /// the sampling window, and a faster feed reports noise rather than detail. It is also the
    /// budget - a GPU reading costs a process spawn, so this interval is what that costs per second.
    /// </para>
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many snapshots a listener may fall behind before the oldest are dropped.
    /// <para>
    /// Small on purpose. A metrics client that cannot keep up wants the <em>current</em> reading,
    /// not a backlog of stale ones delivered late - unlike video, where dropping frames is a loss.
    /// </para>
    /// </summary>
    public int QueueCapacity { get; set; } = 4;
}

/// <summary>
/// Measures the machine on a timer and hands the same reading to everyone listening.
/// </summary>
/// <remarks>
/// <para>
/// One sample, many clients. Ten open tabs cost one reading per interval, not ten - which matters
/// more here than it looks, because a GPU reading costs a process spawn and sampling per request
/// would make the monitor's own cost visible in the numbers it reports.
/// </para>
/// <para>
/// <b>Sampling stops when nobody is listening.</b> There is no point paying a process spawn a
/// second to measure a machine for an audience of nobody, and a server left running overnight with
/// no browser attached should be idle.
/// </para>
/// <para>
/// Concrete rather than behind a port: in-process fan-out with nothing to substitute. The three
/// readers it composes are ports, because each one crosses into the operating system.
/// </para>
/// </remarks>
public sealed class MetricsCollector(
    ISystemMetricsReader systemMetrics,
    IGpuMetricsReader gpuMetrics,
    IProcessMetricsReader processMetrics,
    BroadcastCoordinator coordinator,
    IOptions<MetricsOptions> options,
    TimeProvider clock,
    ILogger<MetricsCollector> logger)
{
    private readonly ConcurrentDictionary<Guid, Channel<ResourceSnapshot>> _listeners = new();
    private readonly int _queueCapacity = Math.Max(1, options.Value.QueueCapacity);

    /// <summary>The most recent reading, or null before the first one.</summary>
    /// <remarks>
    /// Handed to a client the moment it connects, so a monitor shows numbers immediately rather
    /// than an empty panel for up to a whole interval. It is at most one interval old, which is
    /// the same age as the reading it would otherwise have waited for.
    /// </remarks>
    public ResourceSnapshot? Latest { get; private set; }

    /// <summary>Whether anything is listening. The sampler asks before doing any work.</summary>
    public bool HasListeners => !_listeners.IsEmpty;

    /// <summary>
    /// Starts receiving readings.
    /// </summary>
    /// <returns>
    /// A queue of snapshots, and the handle to release it with. Disposing the subscription is what
    /// eventually lets sampling stop, so it is not optional.
    /// </returns>
    public MetricsSubscription Subscribe()
    {
        var id = Guid.NewGuid();

        var channel = Channel.CreateBounded<ResourceSnapshot>(
            new BoundedChannelOptions(_queueCapacity)
            {
                // The newest reading is the only one worth having. A metrics client that stalls
                // should resume at "now", not replay a minute of history nobody wanted.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        _listeners[id] = channel;
        logger.LogDebug("Metrics listener {Id} attached ({Count} listening).", id, _listeners.Count);

        // Whatever is already known, immediately - an empty panel for a second reads as broken.
        if (Latest is { } latest)
        {
            channel.Writer.TryWrite(latest);
        }

        return new MetricsSubscription(channel.Reader, () => Release(id));
    }

    private void Release(Guid id)
    {
        if (_listeners.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
            logger.LogDebug("Metrics listener {Id} detached ({Count} left).", id, _listeners.Count);
        }
    }

    /// <summary>
    /// Takes one reading and hands it to every listener. Driven by a timer in the host rather than
    /// by one of its own, so a test can call it deliberately instead of waiting on wall time.
    /// </summary>
    /// <remarks>
    /// Never throws. A metrics feed that dies because a counter was briefly unreadable is worse
    /// than one reporting that the counter is unreadable, and this runs on a loop that must not be
    /// the thing that stops.
    /// </remarks>
    public async Task SampleAsync(CancellationToken cancellationToken = default)
    {
        if (!HasListeners)
        {
            return;
        }

        var snapshot = await TakeSnapshotAsync(cancellationToken);
        Latest = snapshot;

        foreach (var channel in _listeners.Values)
        {
            // DropOldest, so this never blocks and never fails for a slow listener.
            channel.Writer.TryWrite(snapshot);
        }
    }

    private async Task<ResourceSnapshot> TakeSnapshotAsync(CancellationToken cancellationToken)
    {
        // The GPU reading spawns a process and the system reading does not, so they overlap rather
        // than queueing - otherwise the snapshot is as slow as the slowest reader by construction.
        var systemTask = ReadSystemAsync(cancellationToken);
        var gpuTask = ReadGpuAsync(cancellationToken);

        var running = await coordinator.RunningAsync(cancellationToken);
        var costs = MeasureBroadcasts(running);

        var system = await systemTask;
        var gpu = await gpuTask;

        return new ResourceSnapshot(
            clock.GetUtcNow(),
            system.CpuPercent,
            system.MemoryUsedBytes,
            system.MemoryTotalBytes,
            gpu,
            costs);
    }

    /// <summary>
    /// Attributes cost to each live broadcast, and says so plainly when it cannot.
    /// </summary>
    /// <remarks>
    /// The unattributable case is not a gap - it is the finding. A pass-through
    /// <c>ServerAssisted</c> stream has no process of ours behind it, so there is nothing to
    /// measure precisely because nothing is being spent. Reporting zero would say the same number
    /// as a broken measurement.
    /// </remarks>
    private IReadOnlyList<BroadcastCost> MeasureBroadcasts(IReadOnlyList<RunningBroadcast> running)
    {
        var processIds = running
            .Select(broadcast => broadcast.ProcessId)
            .OfType<int>()
            .Distinct()
            .ToList();

        var measured = processIds.Count == 0
            ? new Dictionary<int, ProcessUtilisation>()
            : (IReadOnlyDictionary<int, ProcessUtilisation>)processMetrics.Read(processIds);

        return running.Select(broadcast =>
        {
            if (broadcast.ProcessId is not { } pid)
            {
                return new BroadcastCost(
                    broadcast.Key,
                    broadcast.Mode,
                    broadcast.Converted,
                    CpuPercent: null,
                    MemoryBytes: null,
                    "The edge server is relaying this stream directly, so no process here is " +
                    "doing work that could be measured.");
            }

            if (!measured.TryGetValue(pid, out var utilisation))
            {
                return new BroadcastCost(
                    broadcast.Key,
                    broadcast.Mode,
                    broadcast.Converted,
                    CpuPercent: null,
                    MemoryBytes: null,
                    "The process serving this broadcast has gone away.");
            }

            return new BroadcastCost(
                broadcast.Key,
                broadcast.Mode,
                broadcast.Converted,
                utilisation.CpuPercent,
                utilisation.MemoryBytes,
                UnavailableReason: null);
        })
        .ToList();
    }

    private async Task<SystemUtilisation> ReadSystemAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await systemMetrics.ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read system metrics.");
            return SystemUtilisation.Unknown;
        }
    }

    private async Task<GpuUtilisation> ReadGpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await gpuMetrics.ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read GPU metrics.");

            return GpuUtilisation.Unavailable(
                GpuAvailability.ToolMissing,
                "The GPU could not be read on this sample.");
        }
    }
}

/// <summary>One client's attachment to the metrics feed.</summary>
/// <param name="Snapshots">Readings, newest-wins. Completes when the subscription is released.</param>
/// <param name="Release">Detaches. Sampling stops once the last listener has gone.</param>
public sealed record MetricsSubscription(
    ChannelReader<ResourceSnapshot> Snapshots,
    Action Release) : IDisposable
{
    public void Dispose() => Release();
}
