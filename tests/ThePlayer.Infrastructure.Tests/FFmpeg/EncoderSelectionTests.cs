using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Playback;
using ThePlayer.Infrastructure.FFmpeg;

namespace ThePlayer.Infrastructure.Tests.FFmpeg;

/// <summary>
/// Runtime encoder fallback: which engines get tried, and when trying another one is worth the
/// wait.
/// </summary>
/// <remarks>
/// The case that motivates all of this is the fourth simultaneous broadcast on a consumer NVIDIA
/// card, where NVENC refuses at <c>avcodec_open2</c> having passed every startup check. Reproducing
/// that for real means four browser tabs and the right hardware; here it is a string and a list.
/// </remarks>
public class EncoderSelectionTests
{
    private static readonly AccelerationProfile Nvidia =
        AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.Nvidia);

    private static readonly AccelerationProfile QuickSync =
        AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.IntelQuickSync);

    private static readonly AccelerationProfile Software = AccelerationProfile.Software;

    private static HardwareCapabilities Machine(params AccelerationProfile[] profiles) =>
        new("8.0", profiles, [VideoCodec.H264, VideoCodec.H265]);

    private static BroadcastPlan Converting(AccelerationProfile? preferred) =>
        new(PlaybackMode.ClientDecoded, VideoCodec.H264, RequiresConversion: true, preferred);

    public class ChoosingCandidates : EncoderSelectionTests
    {
        [Fact]
        public void A_plan_that_needs_no_encoder_has_nothing_to_choose()
        {
            var plan = new BroadcastPlan(
                PlaybackMode.ClientDecoded, VideoCodec.H265, RequiresConversion: false, Acceleration: null);

            EncoderSelection.Candidates(plan, Machine(Nvidia, Software)).Should().BeEmpty();
        }

        [Fact]
        public void The_plans_own_choice_comes_first()
        {
            var candidates = EncoderSelection.Candidates(
                Converting(Nvidia),
                Machine(Nvidia, QuickSync, Software));

            candidates.Should().HaveCount(3);
            candidates[0].Should().Be(Nvidia);
        }

        [Fact]
        public void Only_engines_ranked_below_it_follow()
        {
            // A downgrade, not a retry. Trying a better engine after a worse one failed would be
            // trying something the planner had already ruled out.
            var candidates = EncoderSelection.Candidates(
                Converting(QuickSync),
                Machine(Nvidia, QuickSync, Software));

            candidates.Should().Equal(QuickSync, Software);
            candidates.Should().NotContain(Nvidia);
        }

        [Fact]
        public void Software_alone_leaves_nowhere_to_fall_back_to()
        {
            var candidates = EncoderSelection.Candidates(Converting(Software), Machine(Software));

            candidates.Should().ContainSingle().Which.Should().Be(Software);
        }

        [Fact]
        public void A_plan_naming_no_engine_gets_the_whole_ranking()
        {
            var candidates = EncoderSelection.Candidates(
                Converting(preferred: null),
                Machine(Nvidia, Software));

            candidates.Should().Equal(Nvidia, Software);
        }

        [Fact]
        public void Candidates_come_back_in_ranking_order_however_they_were_detected()
        {
            var candidates = EncoderSelection.Candidates(
                Converting(preferred: null),
                Machine(Software, QuickSync, Nvidia));

            candidates.Should().Equal(Nvidia, QuickSync, Software);
        }
    }

    public class RecognisingAnEncoderFailure : EncoderSelectionTests
    {
        [Fact]
        public void The_encoder_naming_itself_is_the_clearest_signal()
        {
            const string output = "[h264_nvenc @ 000001f2] No capable devices found";

            EncoderSelection.LooksLikeEncoderFailure(output, Nvidia).Should().BeTrue();
        }

        [Fact]
        public void The_session_limit_is_the_case_this_exists_for()
        {
            const string output =
                "[h264_nvenc @ 000001f2] OpenEncodeSessionEx failed: out of memory (10): (no details)";

            EncoderSelection.LooksLikeEncoderFailure(output, Nvidia).Should().BeTrue();
        }

        [Fact]
        public void The_generic_wrapper_counts_even_without_the_encoder_name()
        {
            const string output = "Error while opening encoder for output stream #0:0";

            EncoderSelection.LooksLikeEncoderFailure(output, QuickSync).Should().BeTrue();
        }

        [Fact]
        public void A_hardware_format_mismatch_counts()
        {
            // The decode fell back to software and handed the encoder frames it cannot take.
            // Another engine may well not have that problem.
            const string output = "Impossible to convert between the formats supported by the filter";

            EncoderSelection.LooksLikeEncoderFailure(output, QuickSync).Should().BeTrue();
        }

        [Fact]
        public void An_encoder_that_is_no_longer_in_this_FFmpeg_build_counts()
        {
            // Detection cached its answer at startup; the binary can be replaced afterwards.
            // Dropping to software is exactly the right response, so this has to be recognised.
            const string output =
                "[vost#0:0 @ 000001990818d240] Unknown encoder 'h264_qsv'\n" +
                "[vost#0:0 @ 000001990818d240] Error selecting an encoder\n" +
                "Error opening output files: Encoder not found";

            EncoderSelection.LooksLikeEncoderFailure(output, QuickSync).Should().BeTrue();
        }

        [Fact]
        public void The_wording_a_real_QSV_refusal_produces_counts()
        {
            // Captured from FFmpeg 8.0 rather than imagined. The second line spells the component
            // prefix differently from the first, which is why the bare encoder name is matched.
            const string output =
                "[h264_qsv @ 000001902cb47400] Current resolution is unsupported\n" +
                "[vost#0:0/h264_qsv @ 000001902cb47100] [enc:h264_qsv @ 000001902caf89c0] " +
                "Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.\n" +
                "[vf#0:0 @ 000001902cb48040] Task finished with error code: -40 (Function not implemented)";

            EncoderSelection.LooksLikeEncoderFailure(output, QuickSync).Should().BeTrue();
        }

        [Fact]
        public void An_unreachable_camera_does_not()
        {
            // The distinction that stops a fallback being pointless: this fails identically on
            // every engine, so walking the ranking would spend one connection timeout per profile
            // to arrive at exactly the same answer.
            const string output = "rtsp://192.0.2.10:554/stream: Connection timed out";

            EncoderSelection.LooksLikeEncoderFailure(output, Nvidia).Should().BeFalse();
        }

        [Fact]
        public void A_missing_file_does_not()
        {
            const string output = "D:\\videos\\clip.mp4: No such file or directory";

            EncoderSelection.LooksLikeEncoderFailure(output, Nvidia).Should().BeFalse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Silence_is_not_taken_as_an_encoder_failure(string? output)
        {
            // Guessing "encoder" here would make every quiet failure walk the whole ranking.
            EncoderSelection.LooksLikeEncoderFailure(output, Nvidia).Should().BeFalse();
        }
    }
}
