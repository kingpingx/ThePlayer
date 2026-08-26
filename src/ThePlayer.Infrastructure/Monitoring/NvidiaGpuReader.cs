using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Domain.Monitoring;
using ThePlayer.Infrastructure.FFmpeg;

namespace ThePlayer.Infrastructure.Monitoring;

/// <summary>Where <c>nvidia-smi</c> lives and how patient to be with it.</summary>
public sealed class GpuMetricsOptions
{
    public const string SectionName = "Gpu";

    /// <summary>Path to <c>nvidia-smi</c>. A bare name is resolved against PATH.</summary>
    public string NvidiaSmiPath { get; set; } = "nvidia-smi";

    /// <summary>
    /// How long one query may take before it is abandoned.
    /// <para>
    /// Short, and deliberately shorter than the sampling interval: a query that has not answered
    /// within a couple of seconds will not produce a useful reading for <em>this</em> sample, and
    /// letting it run would queue readings behind each other until the feed is reporting the past.
    /// </para>
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether to look for an NVIDIA GPU at all. Set false to report GPU metrics as unsupported
    /// without spending a process per sample discovering it.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Reads NVIDIA utilisation by running <c>nvidia-smi</c>, once per sample.
/// </summary>
/// <remarks>
/// <para>
/// <b>One process per sample, and that is a correction rather than an oversight.</b> The obvious
/// design is a single long-lived <c>nvidia-smi --loop-ms=1000</c>, and it does not work: on the
/// hardware this was built against the loop modes return one good sample and then
/// <c>[Unknown Error]</c> for every field, forever. Repeated single-shot invocation is completely
/// stable. It costs a process spawn per second - roughly 30ms of CPU, which a monitor honestly
/// ought to declare - and it is worth it for readings that are correct.
/// </para>
/// <para>
/// <b><c>[Unknown Error]</c> is unavailability, never zero.</b> That substitution is the single
/// most important line in this file: an idle GPU and a failed query produce the same panel if a
/// failure is reported as a number, and the whole point of this feed is that its numbers can be
/// believed.
/// </para>
/// <para>
/// The encode and decode engines are queried separately because that split is what makes the three
/// playback modes legible. Overall utilisation cannot tell them apart.
/// </para>
/// </remarks>
public sealed class NvidiaGpuReader(
    FFmpegCommandRunner runner,
    IOptions<GpuMetricsOptions> options,
    ILogger<NvidiaGpuReader> logger) : IGpuMetricsReader
{
    /// <summary>
    /// What the tool prints instead of a number when it cannot answer. Matched case-insensitively
    /// and by prefix, because the exact bracketed text varies: <c>[N/A]</c> on a card that does not
    /// expose an engine, <c>[Unknown Error]</c> when the query itself failed.
    /// </summary>
    private const char UnavailableMarker = '[';

    private const string Query =
        "--query-gpu=utilization.gpu,utilization.encoder,utilization.decoder,memory.used " +
        "--format=csv,noheader,nounits";

    private readonly GpuMetricsOptions _options = options.Value;

    /// <summary>
    /// Set once the tool has been found to be absent, so a machine without an NVIDIA card does not
    /// spend a failed process spawn every second discovering that again.
    /// </summary>
    private GpuUtilisation? _settledUnavailable;

    public async Task<GpuUtilisation> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return GpuUtilisation.Unavailable(
                GpuAvailability.NotSupported,
                "GPU metrics are switched off in this deployment's configuration.");
        }

        if (_settledUnavailable is { } settled)
        {
            return settled;
        }

        try
        {
            var result = await runner.RunAsync(
                _options.NvidiaSmiPath,
                Query,
                scrubWith: null,
                cancellationToken,
                _options.QueryTimeout);

            if (!result.Succeeded)
            {
                return Settle(Interpret(result.CombinedOutput));
            }

            return Parse(result.StandardOutput)
                   ?? GpuUtilisation.Unavailable(
                       GpuAvailability.NoPermission,
                       "nvidia-smi answered, but not with figures this build understands.");
        }
        catch (ExternalToolNotFoundException)
        {
            return Settle(GpuUtilisation.Unavailable(
                GpuAvailability.ToolMissing,
                "nvidia-smi was not found. GPU metrics need the NVIDIA driver tools; " +
                "CPU and per-broadcast figures are unaffected."));
        }
        catch (TimeoutException)
        {
            // Not settled: a single slow query is a bad moment, not a verdict on the machine.
            logger.LogDebug("nvidia-smi did not answer within {Timeout}.", _options.QueryTimeout);

            return GpuUtilisation.Unavailable(
                GpuAvailability.ToolMissing,
                $"nvidia-smi did not answer within {_options.QueryTimeout.TotalSeconds:0}s.");
        }
    }

    /// <summary>
    /// Remembers a verdict that will not change, so the cost of asking is paid once.
    /// </summary>
    /// <remarks>
    /// Only for answers about the <em>machine</em> - no card, no tool. A failed query on a machine
    /// that does have a card is deliberately not settled, because it may well answer next second.
    /// </remarks>
    private GpuUtilisation Settle(GpuUtilisation verdict)
    {
        if (verdict.Availability is GpuAvailability.ToolMissing or GpuAvailability.NotSupported)
        {
            _settledUnavailable = verdict;
            logger.LogInformation("GPU metrics unavailable: {Reason}", verdict.UnavailableReason);
        }

        return verdict;
    }

    /// <summary>Turns a failed invocation into the most useful of the four answers.</summary>
    private static GpuUtilisation Interpret(string output)
    {
        if (output.Contains("NVIDIA-SMI has failed", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("no devices were found", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("couldn't communicate with the NVIDIA driver", StringComparison.OrdinalIgnoreCase))
        {
            return GpuUtilisation.Unavailable(
                GpuAvailability.NotSupported,
                "No NVIDIA GPU was found. Intel and AMD utilisation is not read by this build - " +
                "CPU and per-broadcast figures are unaffected.");
        }

        if (output.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
        {
            return GpuUtilisation.Unavailable(
                GpuAvailability.NoPermission,
                "nvidia-smi refused: this process is not permitted to read the GPU. In a " +
                "container this usually means the device was not passed through.");
        }

        return GpuUtilisation.Unavailable(
            GpuAvailability.NoPermission,
            $"nvidia-smi failed: {FirstLine(output)}");
    }

    /// <summary>
    /// Parses one CSV row: <c>21, 99, 45, 176</c> - overall, encoder, decoder, memory in MiB.
    /// </summary>
    /// <remarks>
    /// A row where <em>any</em> field is bracketed is rejected outright rather than partially
    /// accepted. When the tool starts failing it fails every field at once, and a snapshot mixing
    /// one real number with three absent ones invites the reader to trust the wrong thing.
    /// </remarks>
    /// <returns>
    /// The reading, or <c>null</c> if this is not output the parser recognises - which the caller
    /// reports differently from "the tool said it could not tell".
    /// </returns>
    public static GpuUtilisation? Parse(string output)
    {
        var row = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Length > 0);

        if (row is null)
        {
            return null;
        }

        var fields = row.Split(',', StringSplitOptions.TrimEntries);

        if (fields.Length < 4)
        {
            return null;
        }

        if (fields.Any(field => field.Length == 0 || field[0] == UnavailableMarker))
        {
            return GpuUtilisation.Unavailable(
                GpuAvailability.NoPermission,
                "nvidia-smi returned no usable figures for this sample. On some drivers the " +
                "query reports errors under load; the next sample usually succeeds.");
        }

        if (!TryPercent(fields[0], out var overall) ||
            !TryPercent(fields[1], out var encoder) ||
            !TryPercent(fields[2], out var decoder) ||
            !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMib))
        {
            return null;
        }

        return GpuUtilisation.Available(overall, encoder, decoder, memoryMib * 1024 * 1024);
    }

    private static bool TryPercent(string field, out double value) =>
        double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string FirstLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no output";
}

/// <summary>
/// The GPU reader for every machine that is not NVIDIA: reports unavailability, with the reason.
/// </summary>
/// <remarks>
/// <para>
/// A null object rather than a null reference, so nothing downstream has to test for absence. The
/// reason is the point: <c>intel_gpu_top</c> is Linux-only and usually needs elevated privilege,
/// <c>rocm-smi</c> is Linux-only, and Windows exposes GPU engine data only through per-process
/// counters that do not decompose into encode and decode. Saying that plainly is better than a
/// greyed-out panel, and far better than a plausible zero.
/// </para>
/// <para>
/// Registered when GPU support is switched off. <see cref="NvidiaGpuReader"/> handles the more
/// common case of a machine that simply has no NVIDIA card, because that can only be discovered by
/// asking.
/// </para>
/// </remarks>
public sealed class UnavailableGpuReader(string reason) : IGpuMetricsReader
{
    private readonly GpuUtilisation _answer =
        GpuUtilisation.Unavailable(GpuAvailability.NotSupported, reason);

    public Task<GpuUtilisation> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_answer);
}
