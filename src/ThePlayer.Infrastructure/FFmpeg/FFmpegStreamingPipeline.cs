using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Domain.Broadcasting;
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
/// Parameterised by the FFmpeg arguments and the frame reader, so Phase 5 adds a third mode by
/// supplying different arguments and a second <see cref="IFrameReader"/> rather than by editing
/// anything here.
/// </para>
/// </remarks>
public sealed class FFmpegStreamingPipeline(
    IOptions<FFmpegOptions> options,
    ILogger<FFmpegStreamingPipeline> logger) : IFramePipeline
{
    private readonly FFmpegOptions _options = options.Value;

    public async Task<IFrameStream> StartAsync(
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        CancellationToken cancellationToken = default)
    {
        if (plan.RequiresConversion)
        {
            // Phase 3 supplies the encoder arguments. Until then the coordinator rejects such a
            // plan long before it reaches here, so this is a guard rather than a user-facing path.
            throw new NotSupportedException(
                "This pipeline copies bytes; conversion arrives in Phase 3.");
        }

        var arguments = BuildArguments(address, format);
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

        var stream = new FFmpegFrameStream(process, format, address, logger);

        try
        {
            await stream.InitialiseAsync(_options.PipelineStartTimeout, cancellationToken);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Builds the copy-through command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>aud=insert</c> gives the reader a frame boundary to split on without parsing slice
    /// headers. <c>dump_extra=freq=keyframe</c> is <b>not</b> optional: copying from an MP4 leaves
    /// the parameter sets in the container's <c>avcC</c> box, so without it the elementary stream
    /// contains IDR frames and no SPS at all, and WebCodecs cannot configure a decoder. It also
    /// makes late joiners work, since a client attaching mid-stream gets parameter sets at the next
    /// keyframe with no special handling.
    /// </para>
    /// <para>
    /// <c>-an</c> because this project is video only, and an audio stream on stdout would corrupt
    /// the elementary stream the reader is parsing.
    /// </para>
    /// </remarks>
    private string BuildArguments(MediaAddress address, VideoFormat format)
    {
        var input = address.Kind == MediaAddressKind.Rtsp
            // TCP for the same reason ffprobe uses it: a camera on wifi drops UDP packets, and a
            // stream with holes in it produces frames that will not decode.
            ? $"-rtsp_transport tcp -timeout {(long)_options.RtspConnectTimeout.TotalMilliseconds * 1000}"

            // -re paces a file at real time. Without it FFmpeg reads as fast as the disk allows and
            // a viewer receives the whole file in a few seconds.
            : "-re";

        var filter = format.Codec == VideoCodec.H265 ? "hevc_metadata" : "h264_metadata";
        var container = format.Codec == VideoCodec.H265 ? "hevc" : "h264";

        return $"-hide_banner -loglevel error {input} -i \"{address.ToFFmpegInput()}\" " +
               $"-an -c:v copy -bsf:v {filter}=aud=insert,dump_extra=freq=keyframe " +
               $"-f {container} -";
    }
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
    private readonly Process _process;
    private readonly VideoFormat _format;
    private readonly MediaAddress _address;
    private readonly ILogger _logger;
    private readonly IAsyncEnumerator<ReadFrame> _frames;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<EncodedFrame> _buffered = [];
    private readonly Task _stderrPump;

    private StreamInitialisation? _initialisation;
    private long _frameIndex;
    private bool _exhausted;

    public FFmpegFrameStream(Process process, VideoFormat format, MediaAddress address, ILogger logger)
    {
        _process = process;
        _format = format;
        _address = address;
        _logger = logger;

        var reader = new CompressedFrameReader(format.Codec);
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
    /// Reads until the stream describes itself, buffering whatever arrives in the meantime.
    /// </summary>
    /// <remarks>
    /// The codec string comes from the parameter sets, and <c>dump_extra</c> puts those in front of
    /// every keyframe - so this returns as soon as the first keyframe arrives, which for a live
    /// camera is the first GOP boundary rather than the first byte.
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

                if (_frames.Current.SequenceParameterSet is not { } sps)
                {
                    continue;
                }

                _initialisation = new StreamInitialisation(
                    CodecStringBuilder.Build(_format.Codec, sps),
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

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (line.Length > 0)
                {
                    _logger.LogWarning("[ffmpeg] {Line}", _address.Scrub(line));
                }
            }
        }
        catch (Exception)
        {
            // The pipe closes when the process exits. Nothing useful to report.
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
