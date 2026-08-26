using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Playback;
using ThePlayer.Infrastructure.FFmpeg;

namespace ThePlayer.Infrastructure.Tests.FFmpeg;

/// <summary>
/// The command lines Phase 3 produces.
/// </summary>
/// <remarks>
/// Worth testing as strings, unglamorous as that is. Every decision here fails silently when it is
/// wrong: a missing <c>-hwaccel_output_format</c> looks like a slow machine, a misplaced
/// <c>-hwaccel</c> is ignored outright, and a missing <c>dump_extra</c> produces a stream that
/// plays from the start and never for anyone who joins late. None of those raise an error
/// anywhere - they just make the thing worse in a way that is hard to attribute.
/// </remarks>
public class FFmpegArgumentBuilderTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);

    private static readonly VideoFormat H265 = new(VideoCodec.H265, 1920, 1080, 25, Duration: null);

    private static readonly AccelerationProfile Nvidia =
        AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.Nvidia);

    private static readonly AccelerationProfile Amd =
        AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.AmdAmf);

    private static readonly AccelerationProfile VaApi =
        AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.VaApi);

    private static MediaAddress File() =>
        MediaAddress.Parse(OperatingSystem.IsWindows() ? @"D:\videos\clip.mp4" : "/videos/clip.mp4");

    private static MediaAddress Camera() =>
        MediaAddress.Parse("rtsp://operator:hunter2@192.0.2.10:554/stream");

    private static BroadcastPlan PassThrough(PlaybackMode mode = PlaybackMode.ClientDecoded) =>
        new(mode, VideoCodec.H265, RequiresConversion: false, Acceleration: null);

    private static BroadcastPlan Converting(
        AccelerationProfile profile,
        PlaybackMode mode = PlaybackMode.ClientDecoded) =>
        new(mode, VideoCodec.H264, RequiresConversion: true, profile);

    public class PassingThrough : FFmpegArgumentBuilderTests
    {
        [Fact]
        public void Copies_rather_than_encoding()
        {
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, PassThrough(), profile: null, ConnectTimeout);

            arguments.Should().Contain("-c:v copy");
            arguments.Should().NotContain("-hwaccel");
        }

        [Fact]
        public void Repeats_the_parameter_sets_at_every_keyframe()
        {
            // The difference between a stream a late joiner can decode and one they cannot.
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, PassThrough(), profile: null, ConnectTimeout);

            arguments.Should().Contain("dump_extra=freq=keyframe");
            arguments.Should().Contain("aud=insert");
        }

        [Fact]
        public void Names_the_bitstream_filter_after_the_codec_that_is_passing_through()
        {
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, PassThrough(), profile: null, ConnectTimeout);

            arguments.Should().Contain("hevc_metadata", "an H.265 stream keeps its own filter");
            arguments.Should().Contain("-f hevc -");
        }
    }

    public class Converting_ : FFmpegArgumentBuilderTests
    {
        [Fact]
        public void Keeps_decoded_frames_on_the_device()
        {
            // The single most expensive flag in the project to omit: without it every frame is
            // copied out of GPU memory and back in, which costs more than the encode.
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, Converting(Nvidia), Nvidia, ConnectTimeout);

            arguments.Should().Contain("-hwaccel cuda");
            arguments.Should().Contain("-hwaccel_output_format cuda");
            arguments.Should().Contain("-c:v h264_nvenc");
        }

        [Fact]
        public void Selects_the_device_before_the_input()
        {
            // -hwaccel is an input option. After -i it is silently ignored and the decode quietly
            // happens on the CPU, which looks like a slow machine rather than a bug.
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, Converting(Nvidia), Nvidia, ConnectTimeout);

            arguments.IndexOf("-hwaccel", StringComparison.Ordinal)
                .Should().BeLessThan(arguments.IndexOf(" -i ", StringComparison.Ordinal));
        }

        [Fact]
        public void Leaves_frames_in_system_memory_for_an_engine_that_wants_them_there()
        {
            // AMF decodes through D3D11VA but its encoder takes system-memory frames, so this
            // profile deliberately sets an accelerator and no output format.
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, Converting(Amd), Amd, ConnectTimeout);

            arguments.Should().Contain("-hwaccel d3d11va");
            arguments.Should().NotContain("-hwaccel_output_format");
        }

        [Fact]
        public void Passes_the_device_argument_an_engine_needs()
        {
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, Converting(VaApi), VaApi, ConnectTimeout);

            arguments.Should().Contain("-vaapi_device /dev/dri/renderD128");
        }

        [Fact]
        public void Disables_B_frames()
        {
            // They reorder output and add at least one frame of latency, for nothing on a feed
            // that is rendered on arrival.
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, Converting(Nvidia), Nvidia, ConnectTimeout);

            arguments.Should().Contain("-bf 0");
        }

        [Theory]
        [InlineData(25, 50)]
        [InlineData(30, 60)]
        [InlineData(10, 20)]
        public void Sets_a_keyframe_interval_of_two_seconds_whatever_the_frame_rate(
            double frameRate,
            int expectedGop)
        {
            // A fixed -g would make join latency depend on the camera: one second at 30fps, three
            // at 10. Deriving it keeps what a viewer experiences constant.
            var format = H265 with { FrameRate = frameRate };

            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), format, Converting(Nvidia), Nvidia, ConnectTimeout);

            arguments.Should().Contain($"-g {expectedGop}");
        }

        [Fact]
        public void Falls_back_to_an_assumed_rate_when_the_source_reported_none()
        {
            var format = H265 with { FrameRate = 0 };

            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), format, Converting(Nvidia), Nvidia, ConnectTimeout);

            arguments.Should().Contain("-g 50", "25fps is assumed rather than dividing by zero");
        }

        [Fact]
        public void Emits_H264_framing_even_though_the_source_was_H265()
        {
            // The output codec, not the input, decides how the elementary stream is framed. Using
            // the source codec here would hand the reader hevc framing for an H.264 stream.
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, Converting(Nvidia), Nvidia, ConnectTimeout);

            arguments.Should().Contain("h264_metadata");
            arguments.Should().Contain("-f h264 -");
            arguments.Should().NotContain("hevc_metadata");
        }

        [Fact]
        public void The_delivered_codec_is_the_one_the_arguments_actually_mux()
        {
            // The frame reader is built from this, and the muxer from the same call. They agreed
            // by coincidence until conversion existed: an H.264 stream read as HEVC yields no
            // parameter sets, and the pipeline then blames the source for ending early while
            // FFmpeg sits there having converted it perfectly well.
            var plan = Converting(Nvidia);

            var delivered = FFmpegArgumentBuilder.DeliveredCodec(plan);
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, plan, Nvidia, ConnectTimeout);

            delivered.Should().Be(VideoCodec.H264);
            arguments.Should().Contain("-f h264 -");
        }

        [Fact]
        public void A_pass_through_delivers_what_it_was_given()
        {
            FFmpegArgumentBuilder.DeliveredCodec(PassThrough()).Should().Be(VideoCodec.H265);
        }

        [Fact]
        public void Refuses_a_plan_whose_engine_is_missing()
        {
            var act = () => FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, Converting(Nvidia), profile: null, ConnectTimeout);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Refuses_to_frame_a_codec_that_has_no_annex_b()
        {
            // MJPEG is Phase 5 and needs a different reader. Guessing here would produce a stream
            // the frame reader silently fails to split.
            var plan = new BroadcastPlan(
                PlaybackMode.ServerDecoded,
                VideoCodec.Mjpeg,
                RequiresConversion: true,
                AccelerationProfile.Software);

            var act = () => FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, plan, AccelerationProfile.Software, ConnectTimeout);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    public class Inputs : FFmpegArgumentBuilderTests
    {
        [Fact]
        public void A_camera_is_read_over_TCP_with_a_timeout()
        {
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                Camera(), H265, PassThrough(), profile: null, ConnectTimeout);

            arguments.Should().Contain("-rtsp_transport tcp");
            arguments.Should().Contain("-timeout 6000000", "FFmpeg counts this one in microseconds");
            arguments.Should().NotContain("-re", "a live feed already arrives at its own pace");
        }

        [Fact]
        public void A_file_is_paced_at_real_time()
        {
            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                File(), H265, PassThrough(), profile: null, ConnectTimeout);

            arguments.Should().Contain("-re");
        }

        [Fact]
        public void The_credentials_are_present_for_FFmpeg_and_removable_afterwards()
        {
            // They have to be in the command line - FFmpeg has no other way to authenticate - so
            // what matters is that the address can take them back out of anything derived from it.
            var camera = Camera();

            var arguments = FFmpegArgumentBuilder.ForFrameStream(
                camera, H265, PassThrough(), profile: null, ConnectTimeout);

            arguments.Should().Contain("hunter2");
            camera.Scrub(arguments).Should().NotContain("hunter2");
        }
    }

    public class PublishingToTheEdgeServer : FFmpegArgumentBuilderTests
    {
        private static readonly Uri Target = new("rtsp://127.0.0.1:8554/abc123-serverassisted-h264-converted");

        [Fact]
        public void Muxes_to_RTSP_at_the_reserved_path()
        {
            var arguments = FFmpegArgumentBuilder.ForRtspPublish(
                Camera(), H265, Converting(Nvidia, PlaybackMode.ServerAssisted), Nvidia, ConnectTimeout, Target);

            arguments.Should().Contain("-f rtsp");
            arguments.Should().Contain(Target.AbsoluteUri);
        }

        [Fact]
        public void Asks_for_progress_so_a_first_frame_can_be_waited_for()
        {
            // Launching the process proves nothing: it exits a second later if the encoder will
            // not open, by which time a viewer has been handed a WHEP URL for a path nobody is
            // publishing to. The frame count is the earliest honest readiness signal there is.
            var arguments = FFmpegArgumentBuilder.ForRtspPublish(
                Camera(), H265, Converting(Nvidia, PlaybackMode.ServerAssisted), Nvidia, ConnectTimeout, Target);

            arguments.Should().Contain("-progress pipe:1");
        }

        [Fact]
        public void Uses_the_same_encoder_settings_as_the_socket_path()
        {
            // Two destinations, one set of decisions. If these drift, the same camera looks
            // different depending on which mode a viewer happened to pick.
            var plan = Converting(Nvidia, PlaybackMode.ServerAssisted);

            var published = FFmpegArgumentBuilder.ForRtspPublish(
                Camera(), H265, plan, Nvidia, ConnectTimeout, Target);

            var streamed = FFmpegArgumentBuilder.ForFrameStream(
                Camera(), H265, plan, Nvidia, ConnectTimeout);

            foreach (var setting in new[] { "-hwaccel cuda", "-hwaccel_output_format cuda", "-c:v h264_nvenc", "-preset p1", "-tune ll", "-bf 0", "-g 50" })
            {
                published.Should().Contain(setting);
                streamed.Should().Contain(setting);
            }
        }

        [Fact]
        public void Carries_no_bitstream_filter()
        {
            // RTP announces parameter sets in the SDP, so the Annex-B framing the frame socket
            // needs would be pure overhead here.
            var arguments = FFmpegArgumentBuilder.ForRtspPublish(
                Camera(), H265, Converting(Nvidia, PlaybackMode.ServerAssisted), Nvidia, ConnectTimeout, Target);

            arguments.Should().NotContain("-bsf:v");
        }
    }
}
