using ThePlayer.Application.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Playback;

namespace ThePlayer.Application.Tests.Broadcasting;

/// <summary>
/// The negotiation table, one test per row.
/// <para>
/// This is the payoff from making the planner a pure function: deciding whether an H.265 camera
/// gets converted for a particular browser is otherwise only observable by pointing at a real
/// camera with a real browser attached. Here it is a plain assertion.
/// </para>
/// </summary>
public class BroadcastPlannerTests
{
    private readonly BroadcastPlanner _planner = new();

    private static readonly HardwareCapabilities Nvidia = new(
        "8.0",
        [AccelerationProfile.Known.Single(p => p.Kind == AccelerationKind.Nvidia), AccelerationProfile.Software],
        [VideoCodec.H264, VideoCodec.H265]);

    private static VideoFormat Format(VideoCodec codec) => new(codec, 1920, 1080, 25, Duration: null);

    private static ClientDecodeSupport Client(params VideoCodec[] decodable) =>
        new(WebCodecsAvailable: true,
            decodable.Select(codec => new CodecSupport(codec, Supported: true, HardwareAccelerated: true)).ToList());

    public class TheNegotiationTable : BroadcastPlannerTests
    {
        [Fact]
        public void H264_to_a_client_that_decodes_H264_passes_through()
        {
            var plan = _planner.Plan(
                Format(VideoCodec.H264), PlaybackMode.ClientDecoded, Client(VideoCodec.H264), Nvidia);

            plan.RequiresConversion.Should().BeFalse();
            plan.OutputCodec.Should().Be(VideoCodec.H264);
            plan.Acceleration.Should().BeNull("nothing is being encoded");
        }

        [Fact]
        public void H265_to_a_client_that_decodes_H265_passes_through_untouched()
        {
            // The row the whole design exists for. A capable client means the server does no codec
            // work at all - the difference between a busy GPU and an idle one.
            var plan = _planner.Plan(
                Format(VideoCodec.H265), PlaybackMode.ClientDecoded, Client(VideoCodec.H265), Nvidia);

            plan.RequiresConversion.Should().BeFalse();
            plan.OutputCodec.Should().Be(VideoCodec.H265);
        }

        [Fact]
        public void H265_to_a_client_that_cannot_decode_H265_is_converted()
        {
            var plan = _planner.Plan(
                Format(VideoCodec.H265), PlaybackMode.ClientDecoded, Client(VideoCodec.H264), Nvidia);

            plan.RequiresConversion.Should().BeTrue();
            plan.OutputCodec.Should().Be(VideoCodec.H264);
            plan.Acceleration!.Kind.Should().Be(AccelerationKind.Nvidia);
        }

        [Fact]
        public void H264_over_WebRTC_passes_through()
        {
            var plan = _planner.Plan(
                Format(VideoCodec.H264), PlaybackMode.ServerAssisted, Client(VideoCodec.H264), Nvidia);

            plan.RequiresConversion.Should().BeFalse();
        }

        [Fact]
        public void H265_over_WebRTC_is_always_converted_even_for_a_capable_client()
        {
            // WebRTC negotiates codecs in SDP and browsers refuse H.265 there, so what the client
            // can decode is irrelevant on this path. Being able to decode H.265 does not help if
            // the transport will not carry it.
            var plan = _planner.Plan(
                Format(VideoCodec.H265), PlaybackMode.ServerAssisted, Client(VideoCodec.H265), Nvidia);

            plan.RequiresConversion.Should().BeTrue();
            plan.OutputCodec.Should().Be(VideoCodec.H264);
        }

        [Theory]
        [InlineData(VideoCodec.H264)]
        [InlineData(VideoCodec.H265)]
        [InlineData(VideoCodec.Av1)]
        public void Full_server_decoding_always_converts_to_pictures(VideoCodec source)
        {
            var plan = _planner.Plan(
                Format(source), PlaybackMode.ServerDecoded, Client(VideoCodec.H264), Nvidia);

            plan.RequiresConversion.Should().BeTrue();
            plan.OutputCodec.Should().Be(VideoCodec.Mjpeg);
        }
    }

    public class BroadcastKeys : BroadcastPlannerTests
    {
        [Fact]
        public void Identical_plans_share_a_key_so_viewers_share_a_pipeline()
        {
            var one = _planner.Plan(Format(VideoCodec.H264), PlaybackMode.ServerAssisted, Client(VideoCodec.H264), Nvidia);
            var two = _planner.Plan(Format(VideoCodec.H264), PlaybackMode.ServerAssisted, Client(VideoCodec.H264), Nvidia);

            one.KeyFor("abc").Should().Be(two.KeyFor("abc"));
        }

        [Fact]
        public void Clients_needing_different_output_get_different_keys()
        {
            // Same camera, two browsers: one takes H.265, the other cannot. They are watching the
            // same thing but need different bytes, so they must not share a pipeline.
            var capable = _planner.Plan(
                Format(VideoCodec.H265), PlaybackMode.ClientDecoded, Client(VideoCodec.H265), Nvidia);
            var limited = _planner.Plan(
                Format(VideoCodec.H265), PlaybackMode.ClientDecoded, Client(VideoCodec.H264), Nvidia);

            capable.KeyFor("abc").Should().NotBe(limited.KeyFor("abc"));
        }

        [Fact]
        public void Different_addresses_get_different_keys()
        {
            var plan = _planner.Plan(Format(VideoCodec.H264), PlaybackMode.ServerAssisted, Client(VideoCodec.H264), Nvidia);

            plan.KeyFor("aaa").Should().NotBe(plan.KeyFor("bbb"));
        }
    }

    public class ModeAvailabilityRules : BroadcastPlannerTests
    {
        [Fact]
        public void A_browser_with_no_WebCodecs_cannot_decode_client_side_and_is_told_why()
        {
            var availability = _planner.Availability(Format(VideoCodec.H264), ClientDecodeSupport.None);

            var clientSide = availability.Single(entry => entry.Mode == PlaybackMode.ClientDecoded);
            clientSide.Available.Should().BeFalse();
            clientSide.Reason.Should().Contain("WebCodecs");
        }

        [Fact]
        public void The_server_side_modes_stay_available_without_WebCodecs()
        {
            // They work with any client, which is the entire reason they exist.
            var availability = _planner.Availability(Format(VideoCodec.H265), ClientDecodeSupport.None);

            availability.Where(entry => entry.Mode != PlaybackMode.ClientDecoded)
                .Should().OnlyContain(entry => entry.Available);
        }

        [Fact]
        public void A_client_that_decodes_neither_the_source_nor_H264_is_told_which_codecs_failed()
        {
            var availability = _planner.Availability(Format(VideoCodec.H265), Client(VideoCodec.Vp8));

            var clientSide = availability.Single(entry => entry.Mode == PlaybackMode.ClientDecoded);
            clientSide.Available.Should().BeFalse();
            clientSide.Reason.Should().Contain("H.265").And.Contain("H.264");
        }

        [Fact]
        public void A_client_that_decodes_only_H264_can_still_use_client_side_decoding()
        {
            // Available, but the server pays to convert - which is exactly what the resource
            // monitor will show, and why the mode is still worth offering.
            var availability = _planner.Availability(Format(VideoCodec.H265), Client(VideoCodec.H264));

            availability.Single(entry => entry.Mode == PlaybackMode.ClientDecoded)
                .Available.Should().BeTrue();
        }

        [Fact]
        public void An_unidentifiable_codec_makes_every_mode_unavailable()
        {
            var availability = _planner.Availability(Format(VideoCodec.Unknown), Client(VideoCodec.H264));

            availability.Should().OnlyContain(entry => !entry.Available);
            availability.Should().OnlyContain(entry => entry.Reason!.Contains("could not be identified"));
        }

        [Fact]
        public void Planning_an_unavailable_mode_fails_with_the_same_reason_the_UI_would_show()
        {
            var act = () => _planner.Plan(
                Format(VideoCodec.H265), PlaybackMode.ClientDecoded, Client(VideoCodec.Vp8), Nvidia);

            act.Should().Throw<InvalidOperationException>().WithMessage("*H.265*");
        }
    }
}
