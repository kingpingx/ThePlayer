using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Application.Monitoring;

/// <summary>
/// Remembers when a broadcast could not use the encoder it asked for.
/// </summary>
/// <remarks>
/// <para>
/// Startup detection proves an encoder <em>exists</em>; it does not prove a session can be opened
/// right now. NVENC allows three to eight concurrent sessions on a consumer card, so the fourth
/// broadcast fails at <c>avcodec_open2</c> long after the health endpoint has finished reporting
/// that NVENC is available.
/// </para>
/// <para>
/// A machine that has quietly dropped to libx264 is otherwise indistinguishable from a slow one.
/// This is what makes the difference visible in <c>/api/health</c> instead of only in a CPU graph.
/// </para>
/// <para>
/// Concrete rather than behind a port: in-process state with nothing to substitute. Bounded,
/// because a camera that fails every reconnection would otherwise grow this without limit.
/// </para>
/// </remarks>
public sealed class EncoderFallbackLog(TimeProvider clock)
{
    /// <summary>
    /// How many to keep. Enough to show a pattern - the same profile failing repeatedly - without
    /// turning the health response into a log file.
    /// </summary>
    private const int Capacity = 20;

    private readonly Queue<EncoderFallbackRecord> _events = new(Capacity);
    private readonly object _gate = new();

    /// <summary>Most recent first. Empty on a machine where nothing has ever fallen back.</summary>
    public IReadOnlyList<EncoderFallbackRecord> Recent
    {
        get
        {
            lock (_gate)
            {
                return _events.Reverse().ToList();
            }
        }
    }

    /// <summary>Records a downgrade. <paramref name="replacement"/> is null when there was none left.</summary>
    public void Record(AccelerationProfile failed, AccelerationProfile? replacement, string reason)
    {
        var entry = new EncoderFallbackRecord(
            failed.DisplayName,
            failed.H264Encoder,
            replacement?.DisplayName,
            Summarise(reason, failed.H264Encoder),
            clock.GetUtcNow());

        lock (_gate)
        {
            if (_events.Count == Capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(entry);
        }
    }

    /// <summary>
    /// Keeps the one line that says what went wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storing the whole output would put a paragraph per entry into a response that is polled, so
    /// one line has to stand for the failure - and which line is not obvious. FFmpeg emits seven or
    /// eight, and the first is often about something else entirely: a hardware profile that cannot
    /// take a stream usually complains about its <em>decoder</em> before its encoder ever gets a
    /// turn, which produces an entry blaming a component that was not the reason for the fallback.
    /// </para>
    /// <para>
    /// So the line naming the encoder wins when there is one. Observed on a real refusal, where
    /// the first line read "Video width 32 not within range" from the decoder and the useful one,
    /// four lines later, read "InitializeEncoder failed: Frame Dimension less than the minimum
    /// supported value."
    /// </para>
    /// </remarks>
    private static string Summarise(string reason, string encoder)
    {
        var lines = reason
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return "The encoder could not be opened, and FFmpeg said nothing about why.";
        }

        var summary =
            lines.FirstOrDefault(line => line.Contains(encoder, StringComparison.OrdinalIgnoreCase))
            ?? lines[0];

        return summary.Length <= 300 ? summary : $"{summary[..297]}...";
    }
}
