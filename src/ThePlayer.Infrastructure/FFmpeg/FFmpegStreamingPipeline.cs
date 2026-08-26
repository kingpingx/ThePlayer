using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Application.Monitoring;
using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>
/// Runs FFmpeg as a long-lived child process and turns its stdout into whole frames.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="FFmpegCommandRunner"/>, which runs one-shot commands and returns what
/// they printed. This one never ends on its own: it streams until the source runs out or the
/// broadcast is torn down, so its output is consumed incrementally rather than captured.
/// </para>
/// <para>
/// Parameterised by the FFmpeg arguments and the frame reader, which is what let the third mode
/// arrive as different arguments and a second <see cref="IFrameReader"/> rather than as a branch
/// through this class. Nothing here knows which of the three it is running.
/// </para>
/// <para>
/// Since Phase 3 it also converts. A plan that needs an encoder is attempted on the engine the
/// planner chose and then, if that engine will not open, on each one below it in the ranking.
/// </para>
/// </remarks>
public sealed class FFmpegStreamingPipeline(
    IOptions<FFmpegOptions> options,
    IOptions<PictureOptions> pictureOptions,
    IHardwareInspector hardwareInspector,
    EncoderFallbackLog fallbackLog,
    ILogger<FFmpegStreamingPipeline> logger) : IFramePipeline
{
    private readonly FFmpegOptions _options = options.Value;
    private readonly PictureOptions _pictures = pictureOptions.Value;

    public async Task<IFrameStream> StartAsync(
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        CancellationToken cancellationToken = default)
    {
        if (!plan.RequiresConversion)
        {
            return await StartOnceAsync(address, plan, format, profile: null, cancellationToken);
        }

        var hardware = await hardwareInspector.InspectAsync(cancellationToken);
        var candidates = EncoderSelection.Candidates(plan, hardware);

        for (var attempt = 0; attempt < candidates.Count; attempt++)
        {
            var profile = candidates[attempt];
            var next = attempt + 1 < candidates.Count ? candidates[attempt + 1] : null;

            try
            {
                return await StartOnceAsync(address, plan, format, profile, cancellationToken);
            }
            catch (EncoderRefusedException refused) when (next is not null)
            {
                fallbackLog.Record(profile, next, refused.Diagnostics);

                logger.LogWarning(
                    "{Encoder} would not open for {Address}; falling back to {Next}. {Reason}",
                    profile.H264Encoder,
                    address,
                    next.DisplayName,
                    refused.Diagnostics);
            }
            catch (EncoderRefusedException refused)
            {
                // Nothing left below this one. Recorded anyway: "the last engine on the machine
                // failed too" is the most useful thing the health endpoint could carry.
                fallbackLog.Record(profile, replacement: null, refused.Diagnostics);
                throw refused.AsInspectionFailure(address);
            }
        }

        // Only reachable if detection reported no profiles at all, which it is written never to do.
        throw new MediaInspectionException(
            $"{address.Display} has to be converted, and this machine reported no encoder to do it with.");
    }

    /// <summary>One attempt, on one engine.</summary>
    /// <exception cref="EncoderRefusedException">
    /// The stream failed to start in a way that points at the encoder, so trying the next engine
    /// down is worth doing. Anything else - an unreachable camera, a malformed parameter set - is
    /// left as the <see cref="MediaInspectionException"/> it already is, because it would fail
    /// identically on every engine and retrying would only multiply the wait.
    /// </exception>
    private async Task<IFrameStream> StartOnceAsync(
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        AccelerationProfile? profile,
        CancellationToken cancellationToken)
    {
        var arguments = FFmpegArgumentBuilder.ForFrameStream(
            address,
            format,
            plan,
            profile,
            _options.RtspConnectTimeout,
            _pictures);

        logger.LogDebug("Starting frame pipeline: ffmpeg {Arguments}", address.Scrub(arguments));

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

        // Both asked of the same pair of functions rather than worked out again here, so the
        // reader at this end of the pipe cannot disagree with the muxer at the other.
        var delivered = FFmpegArgumentBuilder.DeliveredCodec(plan);

        var stream = new FFmpegFrameStream(
            process,
            format,
            FrameReaders.For(delivered),
            address,
            logger);

        try
        {
            await stream.InitialiseAsync(_options.PipelineStartTimeout, cancellationToken);
            return stream;
        }
        catch (MediaInspectionException) when (
            profile is not null && EncoderSelection.LooksLikeEncoderFailure(stream.Diagnostics, profile))
        {
            var diagnostics = stream.Diagnostics;
            await stream.DisposeAsync();
            throw new EncoderRefusedException(profile, diagnostics);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// An engine that is installed and was verified at startup but would not open a session now.
/// </summary>
/// <remarks>
/// Internal, and never reaches a caller: it exists only to carry "try the next profile" from the
/// attempt that failed up to the loop that can act on it, which is a different thing from the
/// user-facing <see cref="MediaInspectionException"/> it becomes if the ranking runs out.
/// </remarks>
internal sealed class EncoderRefusedException(AccelerationProfile profile, string diagnostics)
    : Exception($"{profile.H264Encoder} could not be opened. {diagnostics}")
{
    public AccelerationProfile Profile { get; } = profile;

    /// <summary>What FFmpeg printed, already scrubbed of credentials.</summary>
    public string Diagnostics { get; } = diagnostics;

    /// <summary>
    /// The version of this a user should see, once there is no engine left to try.
    /// </summary>
    public MediaInspectionException AsInspectionFailure(MediaAddress address) =>
        new(
            $"{address.Display} has to be converted, and no encoder on this machine would " +
            $"accept it. The last one tried was {Profile.H264Encoder}: {Diagnostics}",
            this);
}

/// <summary>
/// One running FFmpeg process, presented as a stream of frames.
/// </summary>
/// <remarks>
/// Timestamps are synthesised monotonically from the inspected frame rate, because raw
/// elementary-stream output carries none. That is sufficient for live playback where frames render
/// on arrival; reading real presentation timestamps from an MPEG-TS output is the documented
/// upgrade path if A/V sync or seeking is ever wanted.
/// </remarks>
internal sealed class FFmpegFrameStream : IFrameStream
{
    /// <summary>
    /// How much of stderr to keep for diagnosis.
    /// </summary>
    /// <remarks>
    /// Enough for the several lines FFmpeg emits when an encoder will not open, and bounded because
    /// a stream that logs a warning per frame would otherwise grow this for the life of a broadcast.
    /// </remarks>
    private const int MaxDiagnosticBytes = 4096;

    private readonly Process _process;
    private readonly VideoFormat _format;

    /// <summary>
    /// Splits the output into pictures, and says when it has seen enough to describe the stream.
    /// </summary>
    /// <remarks>
    /// Chosen from what FFmpeg was asked to <em>produce</em>, never from what went in. Getting that
    /// wrong fails in a way that points nowhere useful: an H.264 stream read as HEVC yields no
    /// parameter sets, so the pipeline reports that the source "ended before it produced a
    /// decodable frame" while FFmpeg sits there having converted it perfectly well.
    /// </remarks>
    private readonly IFrameReader _reader;

    private readonly MediaAddress _address;
    private readonly ILogger _logger;
    private readonly IAsyncEnumerator<ReadFrame> _frames;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<EncodedFrame> _buffered = [];
    private readonly Task _stderrPump;
    private readonly StringBuilder _diagnostics = new();

    private StreamInitialisation? _initialisation;
    private long _frameIndex;
    private bool _exhausted;

    public FFmpegFrameStream(
        Process process,
        VideoFormat format,
        IFrameReader reader,
        MediaAddress address,
        ILogger logger)
    {
        _process = process;
        _format = format;
        _reader = reader;
        _address = address;
        _logger = logger;

        _frames = reader
            .ReadAsync(process.StandardOutput.BaseStream, _lifetime.Token)
            .GetAsyncEnumerator(_lifetime.Token);

        // An undrained stderr pipe eventually blocks the child, and FFmpeg is never quiet. Every
        // line is scrubbed: FFmpeg echoes the input URL on failure, so a wrong password would
        // otherwise put the right one in the log.
        _stderrPump = PumpStandardErrorAsync();
    }

    public StreamInitialisation Initialisation =>
        _initialisation ?? throw new InvalidOperationException("The pipeline has not been started.");

    /// <summary>
    /// The FFmpeg process, while it is still alive.
    /// </summary>
    /// <remarks>
    /// Reading <c>Id</c> on an exited process is fine; reading it after <c>Dispose</c> throws. The
    /// metrics reader is on a timer and the pipeline can be torn down between two ticks, so this
    /// answers null rather than making every caller guard.
    /// </remarks>
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

    /// <summary>
    /// What FFmpeg complained about, scrubbed and capped.
    /// </summary>
    /// <remarks>
    /// The pipeline reads this to decide whether a failed start is worth retrying on another
    /// engine. A stream that fails because the camera is unreachable says so here, and says the
    /// same thing on every engine - which is exactly the case a fallback must not walk.
    /// </remarks>
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
    /// Reads until the stream describes itself, buffering whatever arrives in the meantime.
    /// </summary>
    /// <remarks>
    /// <b>When</b> a stream can describe itself is the reader's business, not this method's. An
    /// Annex-B stream waits for parameter sets, which <c>dump_extra</c> puts in front of every
    /// keyframe - on a live camera that is the first GOP boundary rather than the first byte. A
    /// picture stream is described by its first picture, because every picture is independent.
    /// </remarks>
    public async Task InitialiseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            while (await _frames.MoveNextAsync())
            {
                var frame = Wrap(_frames.Current);
                _buffered.Add(frame);

                if (_reader.Describe(_frames.Current) is not { } codec)
                {
                    continue;
                }

                // Dimensions and frame rate still come from the source: conversion changes the
                // codec, never the picture. The codec string, though, describes what the client
                // will actually be handed.
                _initialisation = new StreamInitialisation(
                    codec,
                    _format.Width,
                    _format.Height,
                    _format.FrameRate);

                _logger.LogInformation(
                    "Frame pipeline for {Address} is {Codec}, {Width}x{Height}.",
                    _address,
                    _initialisation.Codec,
                    _initialisation.Width,
                    _initialisation.Height);

                return;
            }

            _exhausted = true;

            // An encoder that refused to open ends the stream here, before a single frame, with
            // the reason sitting in stderr. Waiting for the pump to catch up means the caller sees
            // that reason rather than an empty string and gives up on a fallback it could have made.
            await SettleStandardErrorAsync();

            throw new MediaInspectionException(
                $"{_address.Display} ended before it produced a decodable frame.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new MediaInspectionException(
                $"{_address.Display} produced no parameter sets within {timeout.TotalSeconds:0}s.");
        }
        catch (InvalidDataException ex)
        {
            // A malformed parameter set. Fail loudly rather than guessing a codec string that
            // configure() would reject for reasons nobody could trace back to here.
            throw new MediaInspectionException(
                $"Could not read the video format of {_address.Display}: {ex.Message}",
                ex);
        }
    }

    public async IAsyncEnumerable<EncodedFrame> FramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var frame in _buffered)
        {
            yield return frame;
        }

        _buffered.Clear();

        if (_exhausted)
        {
            yield break;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

        while (true)
        {
            ReadFrame current;

            try
            {
                if (!await _frames.MoveNextAsync())
                {
                    break;
                }

                current = _frames.Current;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // Torn down, or the process went away. Either way there are no more frames, and
                // that is not an error worth propagating into every viewer.
                break;
            }

            yield return Wrap(current);
        }

        _logger.LogDebug("Frame pipeline for {Address} reached the end of its stream.", _address);
    }

    private EncodedFrame Wrap(ReadFrame frame)
    {
        var rate = _format.FrameRate > 0 ? _format.FrameRate : 25;
        var timestamp = (long)(_frameIndex++ * 1_000_000d / rate);

        return new EncodedFrame(frame.Payload, frame.IsKeyframe, timestamp);
    }

    /// <summary>
    /// Gives the stderr pump a moment to finish once stdout has closed.
    /// </summary>
    /// <remarks>
    /// Bounded rather than awaited outright: on a healthy stream the pump runs for the life of the
    /// broadcast, so waiting for it to complete would be waiting forever.
    /// </remarks>
    private async Task SettleStandardErrorAsync()
    {
        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }
        catch (Exception)
        {
            // Timed out, or the pump ended badly. Whatever it managed to collect is still there.
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

                var scrubbed = _address.Scrub(line);
                Remember(scrubbed);
                _logger.LogWarning("[ffmpeg] {Line}", scrubbed);
            }
        }
        catch (Exception)
        {
            // The pipe closes when the process exits. Nothing useful to report.
        }
    }

    private void Remember(string line)
    {
        lock (_diagnostics)
        {
            if (_diagnostics.Length >= MaxDiagnosticBytes)
            {
                return;
            }

            _diagnostics.AppendLine(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();

        try
        {
            if (!_process.HasExited)
            {
                // entireProcessTree matters: FFmpeg can spawn helpers, and an orphan goes on
                // holding the camera connection open.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop the FFmpeg frame pipeline cleanly.");
        }

        await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default);

        try
        {
            await _frames.DisposeAsync();
        }
        catch (Exception)
        {
            // Disposing a cancelled enumerator can surface the cancellation. Already shutting down.
        }

        _process.Dispose();
        _lifetime.Dispose();
    }
}
