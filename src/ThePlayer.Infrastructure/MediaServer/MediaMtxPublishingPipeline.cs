using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Application.Monitoring;
using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Infrastructure.FFmpeg;
using ThePlayer.Infrastructure.Processes;

namespace ThePlayer.Infrastructure.MediaServer;

/// <summary>
/// Converts a stream with FFmpeg and pushes the result into MediaMTX over RTSP, for the WebRTC
/// mode when the source codec is not one a browser will take.
/// </summary>
/// <remarks>
/// <para>
/// MediaMTX can pull an RTSP camera by itself, and does for anything that passes through
/// untouched - that path is the whole point of <c>ServerAssisted</c> and this process never sees a
/// frame of it. What MediaMTX cannot do is convert, so when the planner says an encoder is needed
/// this process runs one and publishes the output back in.
/// </para>
/// <para>
/// <b>Why not <c>runOnDemand</c>.</b> MediaMTX will happily run an FFmpeg command line of our
/// choosing, which would be less code. But then the transcoder is MediaMTX's child: its stderr
/// goes into MediaMTX's log, its exit code is not ours to read, and an encoder that refuses to
/// open is indistinguishable from a camera that is offline. Runtime fallback needs to see that
/// failure, so this owns the process.
/// </para>
/// </remarks>
public sealed class MediaMtxPublishingPipeline(
    IOptions<FFmpegOptions> options,
    IHardwareInspector hardwareInspector,
    EncoderFallbackLog fallbackLog,
    IChildProcessGuard guard,
    ILogger<MediaMtxPublishingPipeline> logger) : IPublishingPipeline
{
    private readonly FFmpegOptions _options = options.Value;

    public async Task<IPublishedStream> StartAsync(
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        Uri target,
        CancellationToken cancellationToken = default)
    {
        var hardware = await hardwareInspector.InspectAsync(cancellationToken);

        var candidates = plan.RequiresConversion
            ? EncoderSelection.Candidates(plan, hardware)

            // A pass-through publish needs no engine, but the loop below still wants one entry to
            // run. Software names an encoder that is never invoked, since -c:v copy wins.
            : [AccelerationProfile.Software];

        if (candidates.Count == 0)
        {
            throw new MediaInspectionException(
                $"{address.Display} has to be converted, and this machine reported no encoder to do it with.");
        }

        for (var attempt = 0; attempt < candidates.Count; attempt++)
        {
            var profile = candidates[attempt];
            var next = attempt + 1 < candidates.Count ? candidates[attempt + 1] : null;

            var started = await TryStartAsync(address, plan, format, target, profile, cancellationToken);

            if (started.Stream is { } stream)
            {
                return stream;
            }

            // Only an encoder that refused is worth another attempt. A camera that is unreachable
            // fails the same way on every engine, so walking the ranking would spend one
            // connection timeout per profile to arrive at the same answer.
            var encoderRefused = plan.RequiresConversion &&
                EncoderSelection.LooksLikeEncoderFailure(started.Diagnostics, profile);

            if (encoderRefused && next is not null)
            {
                fallbackLog.Record(profile, next, started.Diagnostics);

                logger.LogWarning(
                    "{Encoder} would not open for {Address}; falling back to {Next}. {Reason}",
                    profile.H264Encoder,
                    address,
                    next.DisplayName,
                    started.Diagnostics);

                continue;
            }

            if (encoderRefused)
            {
                // Nothing left below this one. Recorded anyway: "the last engine on the machine
                // failed too" is the most useful thing the health endpoint could carry.
                fallbackLog.Record(profile, replacement: null, started.Diagnostics);
            }

            throw new MediaInspectionException(
                $"Could not deliver {address.Display} over WebRTC. {Explain(started.Diagnostics)}");
        }

        throw new MediaInspectionException(
            $"Could not convert {address.Display} for playback on any engine this machine has.");
    }

    /// <summary>The outcome of one attempt: a running publisher, or why there is not one.</summary>
    private readonly record struct Attempt(PublishedStream? Stream, string Diagnostics);

    /// <summary>
    /// Starts FFmpeg and waits until it has actually counted a frame.
    /// </summary>
    /// <remarks>
    /// Launching the process proves nothing - it exits a second later if the encoder will not
    /// open, and by then a viewer has already been handed a WHEP URL for a path nobody is
    /// publishing to. The first <c>frame=</c> line from <c>-progress</c> is the earliest moment at
    /// which "video is reaching MediaMTX" is true rather than hoped for.
    /// </remarks>
    private async Task<Attempt> TryStartAsync(
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        Uri target,
        AccelerationProfile profile,
        CancellationToken cancellationToken)
    {
        var arguments = FFmpegArgumentBuilder.ForRtspPublish(
            address,
            format,
            plan,
            plan.RequiresConversion ? profile : null,
            _options.RtspConnectTimeout,
            target);

        logger.LogDebug("Starting publishing pipeline: ffmpeg {Arguments}", address.Scrub(arguments));

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.FFmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        guard.Prepare(process.StartInfo);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new MediaInspectionException(
                $"Could not start '{_options.FFmpegPath}'. Check that it is installed and on PATH.",
                ex);
        }

        // Bound to this server's lifetime before anything else happens to it, so a hard kill in
        // the next moment does not leave it holding a camera connection.
        guard.Adopt(process);

        var stream = new PublishedStream(process, profile, address, logger);

        try
        {
            if (await stream.WaitForFirstFrameAsync(_options.PipelineStartTimeout, cancellationToken))
            {
                logger.LogInformation(
                    "Publishing {Address} to {Target} using {Profile}.",
                    address,
                    target,
                    plan.RequiresConversion ? profile.DisplayName : "no conversion");

                return new Attempt(stream, string.Empty);
            }

            var diagnostics = stream.Diagnostics;
            await stream.DisposeAsync();
            return new Attempt(null, diagnostics);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Turns FFmpeg's output into something worth showing, without pretending to have an
    /// explanation when it printed nothing.
    /// </summary>
    private static string Explain(string diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics)
            ? "FFmpeg stopped without saying why."
            : diagnostics.Trim();
}

/// <summary>
/// One running transcoder, feeding the edge server.
/// </summary>
/// <remarks>
/// Deliberately thin next to <c>FFmpegFrameStream</c>: no frames pass through this process, so
/// there is nothing to parse, buffer or fan out. Its whole job is to know whether the child is
/// still alive and to make sure it does not outlive the broadcast.
/// </remarks>
internal sealed class PublishedStream : IPublishedStream
{
    /// <summary>Enough for the several lines FFmpeg emits when an encoder will not open.</summary>
    private const int MaxDiagnosticBytes = 4096;

    private readonly Process _process;
    private readonly MediaAddress _address;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _diagnostics = new();

    /// <summary>
    /// Completed with <c>true</c> at the first counted frame, <c>false</c> if the process exits
    /// before there is one.
    /// </summary>
    private readonly TaskCompletionSource<bool> _firstFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _progressPump;
    private readonly Task _stderrPump;

    public PublishedStream(Process process, AccelerationProfile profile, MediaAddress address, ILogger logger)
    {
        _process = process;
        _address = address;
        _logger = logger;
        Acceleration = profile;

        _progressPump = PumpProgressAsync();
        _stderrPump = PumpStandardErrorAsync();
    }

    public AccelerationProfile Acceleration { get; }

    /// <summary>The transcoder, while it is still alive. Null once disposed - see the frame stream.</summary>
    public int? ProcessId
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public bool HasEnded
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Already disposed. Gone counts as ended.
                return true;
            }
        }
    }

    /// <summary>What FFmpeg complained about, scrubbed and capped.</summary>
    public string Diagnostics
    {
        get
        {
            lock (_diagnostics)
            {
                return _diagnostics.ToString();
            }
        }
    }

    /// <summary>
    /// Waits for proof that video is flowing.
    /// </summary>
    /// <returns><c>false</c> if the process stopped first, or nothing arrived within the timeout.</returns>
    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Watching the process as well as the progress stream: an encoder that refuses to open
        // closes stdout without ever printing a frame count, and only the exit says so.
        var exited = _process.WaitForExitAsync(CancellationToken.None);
        var expired = Task.Delay(timeout, cancellationToken);

        var finished = await Task.WhenAny(_firstFrame.Task, exited, expired);

        if (finished == _firstFrame.Task && await _firstFrame.Task)
        {
            return true;
        }

        // Either the process stopped or nothing arrived in time. Let the pumps catch up first -
        // without stderr the caller has to decide whether to fall back with nothing to decide on.
        await SettleAsync();

        cancellationToken.ThrowIfCancellationRequested();

        if (finished == expired)
        {
            _logger.LogWarning(
                "{Address} produced no frames within {Timeout:0}s of starting the transcoder.",
                _address,
                timeout.TotalSeconds);
        }

        return false;
    }

    /// <summary>
    /// Reads the <c>-progress</c> stream, which prints <c>frame=&lt;n&gt;</c> as work is done.
    /// </summary>
    /// <remarks>
    /// It keeps reading long after the first frame. An undrained pipe eventually blocks the child,
    /// and progress output continues for the life of the broadcast.
    /// </remarks>
    private async Task PumpProgressAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (!line.StartsWith("frame=", StringComparison.Ordinal))
                {
                    continue;
                }

                if (int.TryParse(line.AsSpan("frame=".Length).Trim(), out var frames) && frames > 0)
                {
                    _firstFrame.TrySetResult(true);
                }
            }
        }
        catch (Exception)
        {
            // The pipe closes when the process exits, which the exit watcher already handles.
        }
        finally
        {
            _firstFrame.TrySetResult(false);
        }
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                // FFmpeg echoes the input URL on failure, so a wrong password would otherwise put
                // the right one in the log.
                var scrubbed = _address.Scrub(line);

                lock (_diagnostics)
                {
                    if (_diagnostics.Length < MaxDiagnosticBytes)
                    {
                        _diagnostics.AppendLine(scrubbed);
                    }
                }

                _logger.LogWarning("[ffmpeg] {Line}", scrubbed);
            }
        }
        catch (Exception)
        {
            // The pipe closes when the process exits. Nothing useful to report.
        }
    }

    /// <summary>Bounded wait for the output pumps to catch up with a process that has stopped.</summary>
    private async Task SettleAsync()
    {
        try
        {
            await Task.WhenAll(_progressPump, _stderrPump)
                .WaitAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }
        catch (Exception)
        {
            // Whatever the pumps managed to collect is still there.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _firstFrame.TrySetResult(false);

        try
        {
            if (!_process.HasExited)
            {
                // entireProcessTree matters: FFmpeg can spawn helpers, and an orphan goes on
                // holding both the camera connection and the publishing slot on MediaMTX.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop the publishing pipeline cleanly.");
        }

        await Task.WhenAll(_progressPump, _stderrPump)
            .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default);

        _process.Dispose();
        _lifetime.Dispose();
    }
}
