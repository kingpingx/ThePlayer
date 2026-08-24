using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Domain.Media;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>Where the FFmpeg tools live and how long to wait for them.</summary>
public sealed class FFmpegOptions
{
    public const string SectionName = "FFmpeg";

    /// <summary>Path to the ffmpeg executable. A bare name is resolved against PATH.</summary>
    public string FFmpegPath { get; set; } = "ffmpeg";

    /// <summary>Path to the ffprobe executable. A bare name is resolved against PATH.</summary>
    public string FFprobePath { get; set; } = "ffprobe";

    /// <summary>How long a one-shot command (version, probe, capability query) may take.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long to wait for an RTSP host to answer before giving up on it.
    /// <para>
    /// Kept well below <see cref="CommandTimeout"/> so that the common failure - a typo in the
    /// camera's IP - reports back in a few seconds rather than making someone stare at a spinner
    /// for the full command timeout.
    /// </para>
    /// </summary>
    public TimeSpan RtspConnectTimeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a streaming pipeline may take to produce its first decodable frame.
    /// <para>
    /// Separate from <see cref="CommandTimeout"/> because it bounds something different: not how
    /// long a command may run, but how long to wait for a stream to describe itself. A camera with
    /// a long GOP does not emit parameter sets until its next keyframe, so this has to allow for a
    /// keyframe interval rather than a round trip.
    /// </para>
    /// </summary>
    public TimeSpan PipelineStartTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whether to confirm each hardware encoder by actually encoding a frame with it at startup.
    /// <para>
    /// On by default, and worth the second or so it costs. An FFmpeg build lists every encoder it
    /// was compiled with, whether or not this machine has the device to run it - a Windows build
    /// advertises <c>h264_vaapi</c> on a laptop with no VA-API at all. Without this check,
    /// "detected acceleration" means "compiled in", and the first real stream is where you find
    /// out the difference.
    /// </para>
    /// </summary>
    public bool VerifyEncoders { get; set; } = true;
}

/// <summary>The outcome of a command that ran to completion. Output is already scrubbed.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// FFmpeg writes almost everything to stderr, so a failure message may be in either stream.
    /// </summary>
    public string CombinedOutput => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : $"{StandardOutput}{Environment.NewLine}{StandardError}".Trim();
}

/// <summary>
/// Runs an external tool and returns what it printed, with credentials removed.
/// </summary>
/// <remarks>
/// <para>
/// Two things here are easy to get wrong and expensive when they are:
/// </para>
/// <para>
/// <b>Both output streams must be drained concurrently.</b> A child process blocks once a pipe
/// buffer fills, so reading stdout to the end before touching stderr deadlocks any command that is
/// chatty on stderr - which FFmpeg always is.
/// </para>
/// <para>
/// <b>Output must be scrubbed before it is returned.</b> FFmpeg echoes the input URL when a
/// connection fails, so a wrong password produces a message containing the right one. Callers pass
/// the <see cref="MediaAddress"/> they used, and every byte the process printed goes through
/// <see cref="MediaAddress.Scrub"/> on the way out. Nothing downstream has to remember to do it.
/// </para>
/// </remarks>
public sealed class FFmpegCommandRunner(
    IOptions<FFmpegOptions> options,
    ILogger<FFmpegCommandRunner> logger)
{
    private readonly FFmpegOptions _options = options.Value;

    public string FFmpegPath => _options.FFmpegPath;

    public string FFprobePath => _options.FFprobePath;

    /// <summary>
    /// Runs a command to completion and captures its output.
    /// </summary>
    /// <param name="executable">The tool to run.</param>
    /// <param name="arguments">Arguments, already quoted as needed.</param>
    /// <param name="scrubWith">
    /// The address whose credentials must not appear in the result, or <c>null</c> when the command
    /// involves no address at all (a version query, for instance).
    /// </param>
    public async Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        MediaAddress? scrubWith = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CommandTimeout);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // Almost always "the tool is not installed or not on PATH". Say that plainly rather
            // than letting a Win32Exception surface from somewhere deep in a pipeline.
            throw new ExternalToolNotFoundException(executable, ex);
        }

        // Start both reads before awaiting either. See the remarks above - doing this in sequence
        // deadlocks on any command that fills the stderr buffer.
        var readStandardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var readStandardError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await readStandardOutput;
            var standardError = await readStandardError;

            return new ProcessResult(
                process.ExitCode,
                Scrub(standardOutput, scrubWith),
                Scrub(standardError, scrubWith));
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process, executable);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                $"'{Path.GetFileName(executable)}' did not finish within {_options.CommandTimeout.TotalSeconds:0}s.");
        }
    }

    /// <summary>
    /// Every path out of this class goes through here, so there is exactly one place to get
    /// redaction right.
    /// </summary>
    private static string Scrub(string text, MediaAddress? address) =>
        address is null ? text : address.Scrub(text);

    private void KillQuietly(Process process, string executable)
    {
        try
        {
            if (!process.HasExited)
            {
                // entireProcessTree matters: FFmpeg can spawn helpers, and orphaned children go on
                // holding the camera connection open.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            // The process may have exited between the check and the kill, which is fine and not
            // worth failing the caller over.
            logger.LogDebug(ex, "Could not kill {Executable} after cancellation.", executable);
        }
    }
}

/// <summary>
/// Raised when an external tool cannot be started, which in practice means it is not installed or
/// not on PATH.
/// </summary>
public sealed class ExternalToolNotFoundException(string executable, Exception innerException)
    : Exception(
        $"Could not start '{executable}'. Check that it is installed and on PATH, " +
        $"or set its full path in configuration.",
        innerException)
{
    public string Executable { get; } = executable;
}
