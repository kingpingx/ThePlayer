using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>
/// Turns a <see cref="BroadcastPlan"/> and an <see cref="AccelerationProfile"/> into an FFmpeg
/// command line.
/// </summary>
/// <remarks>
/// <para>
/// A separate class from either pipeline because conversion feeds two different destinations that
/// must otherwise agree exactly: <see cref="ForFrameStream"/> writes an elementary stream to
/// stdout for the frame socket, <see cref="ForRtspPublish"/> pushes into MediaMTX for WebRTC. Same
/// input handling, same encoder settings, different muxer. Building them in one place is what
/// stops the two paths drifting into producing subtly different video.
/// </para>
/// <para>
/// Pure and static: no process, no options object, no I/O. Every decision this phase makes about
/// how to drive FFmpeg is therefore a plain assertion on a string rather than something only
/// observable by pointing at a camera and watching a GPU.
/// </para>
/// </remarks>
public static class FFmpegArgumentBuilder
{
    /// <summary>
    /// B-frames off, everywhere. They reorder output and add at least one frame of latency, which
    /// buys nothing on a live feed that is rendered on arrival.
    /// </summary>
    private const string LatencySettings = "-bf 0";

    /// <summary>
    /// How much video a client that arrives mid-stream may have to wait through before the next
    /// keyframe lets its decoder start.
    /// </summary>
    private static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(2);

    /// <summary>Used when the source did not report a usable frame rate.</summary>
    private const int AssumedFrameRate = 25;

    /// <summary>
    /// The JPEG encoder. Software, and deliberately so.
    /// </summary>
    /// <remarks>
    /// Hardware MJPEG encoders exist on some engines and are not worth reaching for here. The point
    /// of this mode is that the <em>server</em> pays for everything, and the JPEG encode is the
    /// cheap half of that bill next to the decode - which does still run on the device.
    /// </remarks>
    private const string PictureEncoder = "mjpeg";

    /// <summary>
    /// The command line for an elementary stream on stdout, read by the frame socket.
    /// </summary>
    /// <param name="profile">
    /// The engine to convert with, overriding the plan's own choice - this is what makes runtime
    /// fallback possible. Ignored when the plan requires no conversion.
    /// </param>
    /// <param name="pictures">
    /// Quality and size for the picture path. Ignored unless the plan delivers MJPEG.
    /// </param>
    public static string ForFrameStream(
        MediaAddress address,
        VideoFormat format,
        BroadcastPlan plan,
        AccelerationProfile? profile,
        TimeSpan rtspConnectTimeout,
        PictureOptions? pictures = null)
    {
        var codec = DeliveredCodec(plan);

        if (codec == VideoCodec.Mjpeg)
        {
            // No bitstream filter, because JPEG images delimit themselves: FF D8 opens one and
            // FF D9 closes it. Nothing has to be injected to find the boundaries.
            return string.Join(
                ' ',
                Preamble,
                Input(address, plan, profile, rtspConnectTimeout),
                Pictures(format, profile, pictures ?? new PictureOptions()),
                "-f mjpeg -");
        }

        return string.Join(
            ' ',
            Preamble,
            Input(address, plan, profile, rtspConnectTimeout),
            Video(format, plan, profile),

            // aud=insert gives the reader a frame boundary to split on without parsing slice
            // headers. dump_extra=freq=keyframe is not optional: copying from an MP4 leaves the
            // parameter sets in the container's avcC box, and an encoder writes them once at the
            // head of the stream, so without this a client attaching mid-stream - or at all, from
            // a file - sees IDR frames and no SPS and cannot configure a decoder.
            $"-bsf:v {MetadataFilterFor(codec)}=aud=insert,dump_extra=freq=keyframe",
            $"-f {ContainerFor(codec)} -");
    }

    /// <summary>
    /// The command line for pushing converted video into MediaMTX, which then serves it over
    /// WebRTC.
    /// </summary>
    /// <remarks>
    /// <c>-progress pipe:1</c> is the readiness signal, and the reason this returns something the
    /// pipeline can watch rather than something it must guess about. FFmpeg prints
    /// <c>frame=&lt;n&gt;</c> to stdout as it works, so the first frame counted is proof that the
    /// encoder opened <em>and</em> that video is flowing - which is exactly what has to be true
    /// before a viewer is told to connect.
    /// </remarks>
    /// <param name="target">The RTSP URL to publish to, reserved on the edge server beforehand.</param>
    public static string ForRtspPublish(
        MediaAddress address,
        VideoFormat format,
        BroadcastPlan plan,
        AccelerationProfile? profile,
        TimeSpan rtspConnectTimeout,
        Uri target) =>
        string.Join(
            ' ',
            Preamble,
            "-progress pipe:1 -nostats",
            Input(address, plan, profile, rtspConnectTimeout),
            Video(format, plan, profile),

            // TCP for the same reason the input uses it, and because MediaMTX accepts an
            // interleaved publisher on the port it already has open.
            $"-f rtsp -rtsp_transport tcp {target.AbsoluteUri}");

    private const string Preamble = "-hide_banner -loglevel error";

    /// <summary>
    /// Everything up to and including <c>-i</c>: how to reach the source, and whether the decoder
    /// runs on a device.
    /// </summary>
    /// <remarks>
    /// Order matters here in a way FFmpeg will not warn about. <c>-hwaccel</c> is an <em>input</em>
    /// option: placed after <c>-i</c> it is silently ignored and the decode quietly happens on the
    /// CPU, which looks like a slow machine rather than a bug.
    /// </remarks>
    private static string Input(
        MediaAddress address,
        BroadcastPlan plan,
        AccelerationProfile? profile,
        TimeSpan rtspConnectTimeout)
    {
        var parts = new List<string>(5);

        if (plan.RequiresConversion && profile is not null)
        {
            if (profile.DeviceArguments is { Length: > 0 } device)
            {
                parts.Add(device);
            }

            if (profile.DecodeAccelerator is { Length: > 0 } accelerator)
            {
                parts.Add($"-hwaccel {accelerator}");

                // Keeping frames on the device is right when a device encoder consumes them, and
                // wrong when a software one does. The JPEG encoder is software, so asking for
                // device frames here produces "Impossible to convert between the formats" at the
                // first picture rather than a faster pipeline.
                if (profile.DecodeOutputFormat is { Length: > 0 } outputFormat &&
                    plan.OutputCodec != VideoCodec.Mjpeg)
                {
                    parts.Add($"-hwaccel_output_format {outputFormat}");
                }
            }
        }

        parts.Add(address.Kind == MediaAddressKind.Rtsp
            // TCP for the same reason ffprobe uses it: a camera on wifi drops UDP packets, and a
            // stream with holes in it produces frames that will not decode.
            ? $"-rtsp_transport tcp -timeout {(long)rtspConnectTimeout.TotalMilliseconds * 1000}"

            // -re paces a file at real time. Without it FFmpeg reads as fast as the disk allows
            // and a viewer receives the whole file in a few seconds.
            : "-re");

        parts.Add($"-i \"{address.ToFFmpegInput()}\"");

        return string.Join(' ', parts);
    }

    /// <summary>
    /// What to do with the video: nothing at all, or decode and re-encode on the chosen engine.
    /// </summary>
    /// <remarks>
    /// <c>-an</c> in both branches because this project is video only. On the stdout path an audio
    /// stream would corrupt the elementary stream the reader is parsing; on the RTSP path it would
    /// be carried all the way to a browser that has nowhere to put it.
    /// </remarks>
    private static string Video(VideoFormat format, BroadcastPlan plan, AccelerationProfile? profile)
    {
        if (!plan.RequiresConversion)
        {
            return "-an -c:v copy";
        }

        if (profile is null)
        {
            throw new ArgumentNullException(
                nameof(profile),
                "A plan that requires conversion has to name the engine that will do it.");
        }

        var parts = new List<string>(5) { "-an", $"-c:v {profile.H264Encoder}" };

        if (profile.EncoderArguments is { Length: > 0 } tuning)
        {
            parts.Add(tuning);
        }

        parts.Add(LatencySettings);
        parts.Add($"-g {KeyframeIntervalInFrames(format)}");

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Decode on the device where there is one, then encode independent JPEG images on the CPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vendor tuning a profile carries is for its H.264 encoder and means nothing to this one -
    /// <c>-preset p1 -tune ll</c> handed to <c>mjpeg</c> is an error, not a no-op. So the profile
    /// is used for the <em>decode</em> half here and nothing else, which is also the honest
    /// description of what it is doing in this mode.
    /// </para>
    /// <para>
    /// <c>-bf</c> and <c>-g</c> are absent because they have nothing to say: every JPEG is
    /// independent, so there are no B-frames to disable and no group of pictures to bound.
    /// </para>
    /// </remarks>
    private static string Pictures(VideoFormat format, AccelerationProfile? profile, PictureOptions options)
    {
        var parts = new List<string>(4) { "-an", $"-c:v {PictureEncoder}", $"-q:v {options.Quality}" };

        // Scaling is the only real defence against what this mode costs on a network. MJPEG has no
        // inter-frame compression at all, so it runs five to ten times the bandwidth of H.264 at
        // comparable quality - fine on a LAN, not fine over a WAN.
        if (options.MaxWidth > 0 && format.Width > options.MaxWidth)
        {
            // -2 keeps the aspect ratio and rounds to an even height, which the encoder requires.
            parts.Add($"-vf scale={options.MaxWidth}:-2");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The GOP length, derived from the source frame rate rather than fixed.
    /// </summary>
    /// <remarks>
    /// A fixed <c>-g 30</c> means one second of join latency on a 30fps feed and three on a 10fps
    /// one. Deriving it keeps the thing a viewer actually experiences - how long a black screen
    /// lasts before the first keyframe arrives - the same across sources.
    /// </remarks>
    private static int KeyframeIntervalInFrames(VideoFormat format)
    {
        var rate = format.FrameRate > 0 ? format.FrameRate : AssumedFrameRate;
        return Math.Max(1, (int)Math.Round(rate * KeyframeInterval.TotalSeconds));
    }

    /// <summary>
    /// The codec that will come out of FFmpeg, which is not always what went in.
    /// </summary>
    /// <remarks>
    /// Public because the reader on the other end of the pipe has to agree with the muxer at this
    /// end, and having each work it out separately is how they came to disagree in the first
    /// place: a converted stream read as HEVC yields no parameter sets, and the pipeline then
    /// reports that the source ended before it produced a decodable frame while FFmpeg sits there
    /// having converted it perfectly well. One function, both callers.
    /// </remarks>
    public static VideoCodec DeliveredCodec(BroadcastPlan plan) => plan.OutputCodec switch
    {
        VideoCodec.H264 or VideoCodec.H265 or VideoCodec.Mjpeg => plan.OutputCodec,

        // Everything else would need a muxer and a reader that do not exist. Guessing here would
        // produce a stream the frame reader silently fails to split.
        _ => throw new ArgumentOutOfRangeException(
            nameof(plan),
            plan.OutputCodec,
            "Only H.264, H.265 and MJPEG can be carried over the frame socket."),
    };

    /// <summary>The bitstream filter that inserts access unit delimiters, named per codec.</summary>
    private static string MetadataFilterFor(VideoCodec codec) =>
        codec == VideoCodec.H265 ? "hevc_metadata" : "h264_metadata";

    private static string ContainerFor(VideoCodec codec) =>
        codec == VideoCodec.H265 ? "hevc" : "h264";
}
