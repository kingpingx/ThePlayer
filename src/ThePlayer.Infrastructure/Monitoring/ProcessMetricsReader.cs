using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ThePlayer.Application;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Infrastructure.Monitoring;

/// <summary>
/// What individual processes are costing, measured between readings.
/// </summary>
/// <remarks>
/// <para>
/// The most interesting number the system produces, and the cheapest to get right: processor time
/// divided by elapsed wall time, divided again by the core count. The arithmetic is identical on
/// Windows and Linux, and <c>Process.TotalProcessorTime</c> supplies the input on both - so unlike
/// <see cref="SystemMetricsReader"/> nothing in this file is platform-specific. Working out which
/// processes to add up is, and that lives in <see cref="ProcessTree"/>.
/// </para>
/// <para>
/// Dividing by <see cref="Environment.ProcessorCount"/> is the decision worth naming. It makes 100
/// mean "every core on this machine is saturated by this one process" rather than "one core is",
/// which is the reading that can be compared against the system-wide figure beside it. Without it a
/// transcode on an eight-core box reports 400% and sits above a system figure of 50%, which is true
/// and useless.
/// </para>
/// <para>
/// A process that has exited is <b>absent</b> from the result rather than present with zeroes,
/// because those are different facts and only one of them is a measurement.
/// </para>
/// <para>
/// What is measured is the process <em>tree</em>, not the process. On Windows with a Chocolatey
/// FFmpeg the process this server starts is a shim that immediately launches the real one as its
/// child and then idles, so measuring only the root reported <c>0.0%</c> for a transcode that was
/// saturating a GPU. See <see cref="ProcessTree"/>.
/// </para>
/// </remarks>
public sealed class ProcessMetricsReader(TimeProvider clock, ILogger<ProcessMetricsReader> logger)
    : IProcessMetricsReader
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Sample> _previous = [];

    public IReadOnlyDictionary<int, ProcessUtilisation> Read(IReadOnlyCollection<int> processIds)
    {
        var now = clock.GetUtcNow();
        var results = new Dictionary<int, ProcessUtilisation>(processIds.Count);
        var current = new Dictionary<int, Sample>(processIds.Count);

        foreach (var id in processIds)
        {
            if (Measure(id, now) is not { } sample)
            {
                continue;
            }

            current[id] = sample;
            results[id] = new ProcessUtilisation(CpuPercent: null, sample.MemoryBytes);
        }

        lock (_gate)
        {
            foreach (var (id, sample) in current)
            {
                if (_previous.TryGetValue(id, out var previous))
                {
                    results[id] = results[id] with { CpuPercent = Compare(previous, sample) };
                }
            }

            // Rebuilt rather than updated, so a process that has gone is forgotten. Keeping it
            // would leak an entry per broadcast for the lifetime of the server, and would make a
            // recycled process id inherit the processor time of whatever held it before.
            _previous.Clear();

            foreach (var (id, sample) in current)
            {
                _previous[id] = sample;
            }
        }

        return results;
    }

    private static double? Compare(Sample previous, Sample current)
    {
        var elapsed = (current.TakenAt - previous.TakenAt).TotalSeconds;
        var processorSeconds = (current.ProcessorTime - previous.ProcessorTime).TotalSeconds;

        if (elapsed <= 0 || processorSeconds < 0)
        {
            // The clock did not move, or the process was replaced under its own id. Neither is a
            // measurement worth reporting.
            return null;
        }

        var percent = 100d * processorSeconds / (elapsed * Environment.ProcessorCount);
        return Math.Clamp(Math.Round(percent, 1), 0, 100);
    }

    /// <summary>
    /// Sums the root and everything descended from it.
    /// </summary>
    /// <remarks>
    /// A subtree where <em>no</em> member could be read produces null - the whole thing has gone.
    /// A subtree where some member vanished mid-walk is still a reading, just of what is left,
    /// which is the honest answer while a pipeline is shutting down.
    /// </remarks>
    private Sample? Measure(int processId, DateTimeOffset now)
    {
        var processorTime = TimeSpan.Zero;
        long memory = 0;
        var read = 0;

        foreach (var id in ProcessTree.Descendants(processId))
        {
            if (MeasureOne(id) is not { } part)
            {
                continue;
            }

            processorTime += part.ProcessorTime;
            memory += part.MemoryBytes;
            read++;
        }

        return read == 0 ? null : new Sample(now, processorTime, memory);
    }

    private (TimeSpan ProcessorTime, long MemoryBytes)? MeasureOne(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            // Reading either property on a process that exited between the call above and here
            // throws, which is ordinary during a teardown rather than exceptional.
            return (process.TotalProcessorTime, process.WorkingSet64);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Gone. Absent from the sum, which is what the caller turns into "no figure".
            return null;
        }
        catch (Exception ex)
        {
            // On Linux a process owned by another user is readable; on Windows it may not be.
            // Either way this is a missing figure, not a reason to fail the whole snapshot.
            logger.LogDebug(ex, "Could not read metrics for process {ProcessId}.", processId);
            return null;
        }
    }

    private readonly record struct Sample(DateTimeOffset TakenAt, TimeSpan ProcessorTime, long MemoryBytes);
}
