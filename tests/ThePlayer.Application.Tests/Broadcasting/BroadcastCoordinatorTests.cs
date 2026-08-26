using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ThePlayer.Application.Broadcasting;
using ThePlayer.Application.Security;
using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Playback;

namespace ThePlayer.Application.Tests.Broadcasting;

/// <summary>
/// Reference counting and lifetime: one pipeline shared by many viewers, and shut down only after
/// the last one has been gone for a while.
/// </summary>
/// <remarks>
/// Time is injected, so linger is tested by advancing a fake clock rather than by sleeping. A test
/// that sleeps for the real linger duration is both slow and flaky, and would tempt whoever hits
/// the flake into shortening the linger rather than fixing the test.
/// </remarks>
public class BroadcastCoordinatorTests
{
    private const string H264File = "/videos/clip.mp4";
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(10);

    private readonly IMediaInspector _inspector = Substitute.For<IMediaInspector>();
    private readonly IMediaServer _mediaServer = Substitute.For<IMediaServer>();
    private readonly IHardwareInspector _hardware = Substitute.For<IHardwareInspector>();
    private readonly IFramePipeline _framePipeline = Substitute.For<IFramePipeline>();
    private readonly IPublishingPipeline _publishingPipeline = Substitute.For<IPublishingPipeline>();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private readonly BroadcastCoordinator _coordinator;

    public BroadcastCoordinatorTests()
    {
        _inspector.InspectAsync(Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>())
            .Returns(new VideoFormat(VideoCodec.H264, 1920, 1080, 25, Duration: null));

        _hardware.InspectAsync(Arg.Any<CancellationToken>())
            .Returns(new HardwareCapabilities("8.0", [AccelerationProfile.Software], [VideoCodec.H264]));

        _mediaServer.WhepUrlFor(Arg.Any<string>())
            .Returns(callInfo => new Uri($"http://localhost:8889/{callInfo.Arg<string>()}/whep"));

        _mediaServer.ReservePublishPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new Uri($"rtsp://127.0.0.1:8554/{callInfo.Arg<string>()}"));

        _publishingPipeline.StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Any<BroadcastPlan>(),
                Arg.Any<VideoFormat>(),
                Arg.Any<Uri>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new RunningPublisher());

        _framePipeline.StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Any<BroadcastPlan>(),
                Arg.Any<VideoFormat>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new SilentFrameStream());

        _coordinator = new BroadcastCoordinator(
            _inspector,
            _mediaServer,
            _hardware,
            _framePipeline,
            _publishingPipeline,
            new BroadcastPlanner(),

            // Development defaults: the guard lets everything through, so these tests stay about
            // reference counting rather than about policy. AddressGuardTests covers the policy.
            new AddressGuard(
                Substitute.For<IAddressResolver>(),
                Options.Create(new AddressPolicyOptions()),
                NullLogger<AddressGuard>.Instance),
            Options.Create(new BroadcastOptions { Linger = Linger }),
            _clock,
            NullLogger<BroadcastCoordinator>.Instance);
    }

    /// <summary>
    /// A pipeline that starts cleanly and then produces nothing. The coordinator's job is
    /// reference counting, not decoding, so a stream that never yields keeps these tests about
    /// lifetime rather than about frames.
    /// </summary>
    private sealed class SilentFrameStream : IFrameStream
    {
        public StreamInitialisation Initialisation { get; } = new("avc1.42c01f", 1920, 1080, 25);

        /// <summary>No real process behind this one, which is what a metrics reader must survive.</summary>
        public int? ProcessId => null;

        public async IAsyncEnumerable<EncodedFrame> FramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A transcoder that started cleanly and keeps running. Like <see cref="SilentFrameStream"/>,
    /// it produces nothing: no frame of a published stream passes through this process anyway, so
    /// there is nothing to fake beyond "still alive" and "shut down when told".
    /// </summary>
    private sealed class RunningPublisher : IPublishedStream
    {
        public AccelerationProfile Acceleration => AccelerationProfile.Software;

        public int? ProcessId => null;

        public bool HasEnded { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            HasEnded = true;
            return ValueTask.CompletedTask;
        }
    }

    private static MediaAddress Address(string path = H264File) =>
        MediaAddress.Parse(OperatingSystem.IsWindows() ? $"D:{path.Replace('/', '\\')}" : path);

    private Task<WatchTicket> AttachAsync(MediaAddress? address = null) =>
        _coordinator.AttachAsync(
            address ?? Address(),
            PlaybackMode.ServerAssisted,
            ClientDecodeSupport.None);

    public class Attaching : BroadcastCoordinatorTests
    {
        [Fact]
        public async Task The_first_viewer_starts_a_broadcast_and_publishes_it()
        {
            var ticket = await AttachAsync();

            ticket.ViewerId.Should().NotBeNullOrEmpty();
            ticket.WhepUrl.Should().NotBeNull("ServerAssisted is delivered over WebRTC");
            ticket.Converted.Should().BeFalse("an H.264 source needs no conversion");

            await _mediaServer.Received(1).PublishAsync(
                Arg.Any<string>(), Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_second_viewer_of_the_same_thing_joins_rather_than_starting_another()
        {
            // The whole "one FFmpeg process feeding many tabs" promise. If this regresses, the
            // symptom is a camera being opened once per viewer - which many cameras refuse.
            await AttachAsync();
            await AttachAsync();

            var live = await _coordinator.ListAsync();
            live.Should().ContainSingle().Which.ViewerCount.Should().Be(2);

            await _mediaServer.Received(1).PublishAsync(
                Arg.Any<string>(), Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task Viewers_get_distinct_ids()
        {
            var first = await AttachAsync();
            var second = await AttachAsync();

            first.ViewerId.Should().NotBe(second.ViewerId);
        }

        [Fact]
        public async Task Different_addresses_get_their_own_broadcasts()
        {
            await AttachAsync(Address("/videos/one.mp4"));
            await AttachAsync(Address("/videos/two.mp4"));

            (await _coordinator.ListAsync()).Should().HaveCount(2);
        }

        [Fact]
        public async Task Inspection_is_reused_across_viewers_of_the_same_address()
        {
            // Inspection connects to the camera. Doing it per viewer is slow and rude to the device.
            await AttachAsync();
            await AttachAsync();

            await _inspector.Received(1).InspectAsync(Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_publish_failure_leaves_nothing_behind()
        {
            _mediaServer.PublishAsync(Arg.Any<string>(), Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("edge server said no")));

            var act = async () => await AttachAsync();

            await act.Should().ThrowAsync<InvalidOperationException>();
            (await _coordinator.ListAsync()).Should()
                .BeEmpty("a viewer must never see a broadcast it cannot play");
        }
    }

    public class DetachingAndLingering : BroadcastCoordinatorTests
    {
        [Fact]
        public async Task A_broadcast_survives_its_last_viewer_leaving()
        {
            // So that a page refresh re-attaches instead of tearing down an FFmpeg process and
            // immediately rebuilding it.
            var ticket = await AttachAsync();

            await _coordinator.DetachAsync(ticket.ViewerId);
            await _coordinator.SweepAsync();

            (await _coordinator.ListAsync()).Should().ContainSingle()
                .Which.ViewerCount.Should().Be(0);
        }

        [Fact]
        public async Task It_is_stopped_once_the_linger_has_elapsed()
        {
            var ticket = await AttachAsync();
            await _coordinator.DetachAsync(ticket.ViewerId);

            _clock.Advance(Linger + TimeSpan.FromSeconds(1));
            await _coordinator.SweepAsync();

            (await _coordinator.ListAsync()).Should().BeEmpty();
            await _mediaServer.Received(1).RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_viewer_returning_within_the_linger_keeps_the_pipeline_alive()
        {
            var first = await AttachAsync();
            await _coordinator.DetachAsync(first.ViewerId);

            _clock.Advance(TimeSpan.FromSeconds(5));
            await AttachAsync();

            _clock.Advance(Linger + TimeSpan.FromSeconds(1));
            await _coordinator.SweepAsync();

            (await _coordinator.ListAsync()).Should().ContainSingle(
                "the linger countdown resets when someone re-attaches");
            await _mediaServer.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_broadcast_with_viewers_left_is_never_swept()
        {
            var first = await AttachAsync();
            await AttachAsync();

            await _coordinator.DetachAsync(first.ViewerId);
            _clock.Advance(TimeSpan.FromHours(1));
            await _coordinator.SweepAsync();

            (await _coordinator.ListAsync()).Should().ContainSingle()
                .Which.ViewerCount.Should().Be(1);
        }

        [Fact]
        public async Task Detaching_an_unknown_viewer_is_not_an_error()
        {
            // A closing tab can send both a beacon and a socket close; the second is not a fault.
            var act = async () => await _coordinator.DetachAsync("never-existed");

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task Detaching_twice_is_not_an_error()
        {
            var ticket = await AttachAsync();

            await _coordinator.DetachAsync(ticket.ViewerId);
            var act = async () => await _coordinator.DetachAsync(ticket.ViewerId);

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task A_failure_unpublishing_does_not_break_the_sweep()
        {
            _mediaServer.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("edge server unreachable")));

            var ticket = await AttachAsync();
            await _coordinator.DetachAsync(ticket.ViewerId);
            _clock.Advance(Linger + TimeSpan.FromSeconds(1));

            var act = async () => await _coordinator.SweepAsync();

            await act.Should().NotThrowAsync("one bad cleanup must not stop everything else being cleaned up");
            (await _coordinator.ListAsync()).Should().BeEmpty();
        }
    }

    public class PhaseLimits : BroadcastCoordinatorTests
    {
        [Theory]
        [InlineData(PlaybackMode.ServerDecoded)]
        public async Task Modes_that_are_not_built_yet_say_so_rather_than_failing_obscurely(PlaybackMode mode)
        {
            // A capable client, so the request gets past availability and reaches the phase limit.
            // Passing ClientDecodeSupport.None here would fail for a different and legitimate
            // reason - the mode being genuinely unavailable rather than merely unbuilt.
            var capable = new ClientDecodeSupport(
                WebCodecsAvailable: true,
                [new CodecSupport(VideoCodec.H264, Supported: true, HardwareAccelerated: true)]);

            var act = async () => await _coordinator.AttachAsync(Address(), mode, capable);

            (await act.Should().ThrowAsync<NotSupportedException>())
                .Which.Message.Should().Contain("Phase");
        }

        [Fact]
        public async Task Client_side_decoding_of_a_stream_the_browser_understands_works()
        {
            // Phase 2. The browser decodes H.264 itself, so the server copies bytes and hands back
            // a socket to read them from rather than a WHEP URL.
            var capable = new ClientDecodeSupport(
                WebCodecsAvailable: true,
                [new CodecSupport(VideoCodec.H264, Supported: true, HardwareAccelerated: true)]);

            var ticket = await _coordinator.AttachAsync(Address(), PlaybackMode.ClientDecoded, capable);

            ticket.Converted.Should().BeFalse("the browser can already decode this codec");
            ticket.FrameSocketPath.Should().Be($"/ws/frames/{ticket.ViewerId}");
            ticket.WhepUrl.Should().BeNull("client-side decoding does not go through the edge server");

            await _mediaServer.DidNotReceive().PublishAsync(
                Arg.Any<string>(), Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_file_that_played_to_the_end_is_restarted_rather_than_joined()
        {
            // The pipeline for a file stops when the file does. Watching it again has to start it
            // again - joining the finished broadcast would hand the second viewer a stream that
            // never produces a frame, which looks exactly like a hang.
            var capable = Capable();

            var first = await _coordinator.AttachAsync(Address(), PlaybackMode.ClientDecoded, capable);
            await WaitForPipelineToEndAsync();

            var second = await _coordinator.AttachAsync(Address(), PlaybackMode.ClientDecoded, capable);

            second.ViewerId.Should().NotBe(first.ViewerId);

            await _framePipeline.Received(2).StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Any<BroadcastPlan>(),
                Arg.Any<VideoFormat>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_finished_pipeline_is_swept_even_though_a_viewer_never_detached()
        {
            // A client that dies without detaching used to pin its broadcast forever: viewers never
            // reached zero, so the linger countdown never started and the dead broadcast went on
            // being handed to everyone who asked for the same thing.
            await _coordinator.AttachAsync(Address(), PlaybackMode.ClientDecoded, Capable());
            await WaitForPipelineToEndAsync();

            // No detach at all. The sweep notices the pipeline ran out and starts the countdown.
            await _coordinator.SweepAsync();
            _clock.Advance(Linger + TimeSpan.FromSeconds(1));
            await _coordinator.SweepAsync();

            (await _coordinator.ListAsync()).Should().BeEmpty();
        }

        private static ClientDecodeSupport Capable() => new(
            WebCodecsAvailable: true,
            [new CodecSupport(VideoCodec.H264, Supported: true, HardwareAccelerated: true)]);

        /// <summary>
        /// The fake stream yields nothing, so its pump finishes almost at once - but "almost" is
        /// not "before the next line", and asserting on a race is how a suite starts flaking.
        /// </summary>
        private async Task WaitForPipelineToEndAsync()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await _coordinator.SweepAsync();

                var live = await _coordinator.ListAsync();
                if (live.Count == 0 || live[0].State == BroadcastState.Ended)
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new InvalidOperationException("The fake pipeline never reported that it had ended.");
        }

        [Fact]
        public async Task A_frame_socket_is_only_offered_to_a_viewer_that_asked_for_one()
        {
            var subscription = await _coordinator.SubscribeAsync("nobody");

            subscription.Should().BeNull("an unknown viewer id must not open a stream");
        }

        [Fact]
        public async Task An_unavailable_mode_fails_differently_from_an_unbuilt_one()
        {
            // Worth distinguishing: "your browser cannot do this" is permanent and the user can
            // act on it, while "this phase does not do it yet" is temporary and they cannot.
            var act = async () => await _coordinator.AttachAsync(
                Address(), PlaybackMode.ClientDecoded, ClientDecodeSupport.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().NotBeOfType<NotSupportedException>();
        }

        [Fact]
        public async Task A_stream_the_browser_cannot_decode_is_converted_rather_than_refused()
        {
            // Phase 3. An H.265 file and a browser that only does H.264: the planner says convert,
            // and unlike every phase before this one the answer is now carried out.
            _inspector.InspectAsync(Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>())
                .Returns(new VideoFormat(VideoCodec.H265, 1920, 1080, 25, Duration: null));

            var ticket = await _coordinator.AttachAsync(Address(), PlaybackMode.ClientDecoded, Capable());

            ticket.Converted.Should().BeTrue("the browser reported no H.265 decoder");
            ticket.FrameSocketPath.Should().Be($"/ws/frames/{ticket.ViewerId}");

            await _framePipeline.Received(1).StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Is<BroadcastPlan>(plan =>
                    plan.RequiresConversion && plan.OutputCodec == VideoCodec.H264),
                Arg.Any<VideoFormat>(),
                Arg.Any<CancellationToken>());
        }
    }

    /// <summary>
    /// Conversion for the WebRTC mode, which is the half of Phase 3 that MediaMTX cannot do for
    /// itself. It will pull a camera; it will not re-encode one.
    /// </summary>
    public class ConvertingForWebRtc : BroadcastCoordinatorTests
    {
        private Task<WatchTicket> AttachToH265Async()
        {
            _inspector.InspectAsync(Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>())
                .Returns(new VideoFormat(VideoCodec.H265, 1920, 1080, 25, Duration: null));

            return _coordinator.AttachAsync(
                Address(),
                PlaybackMode.ServerAssisted,
                ClientDecodeSupport.None);
        }

        [Fact]
        public async Task A_converted_stream_is_pushed_in_rather_than_pulled()
        {
            var ticket = await AttachToH265Async();

            ticket.Converted.Should().BeTrue("H.265 cannot be relied on over WebRTC");
            ticket.WhepUrl.Should().NotBeNull("it is still played through the edge server");

            await _mediaServer.Received(1).ReservePublishPathAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>());

            await _publishingPipeline.Received(1).StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Is<BroadcastPlan>(plan => plan.RequiresConversion),
                Arg.Any<VideoFormat>(),
                Arg.Any<Uri>(),
                Arg.Any<CancellationToken>());

            // The pull path would have been wrong here: MediaMTX would fetch H.265 and hand it
            // straight to a browser that cannot play it.
            await _mediaServer.DidNotReceive().PublishAsync(
                Arg.Any<string>(), Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_stream_that_needs_no_conversion_still_costs_this_process_nothing()
        {
            // The thesis, stated as a test. An H.264 source in this mode must not start a
            // transcoder: MediaMTX pulls it and serves it, and no FFmpeg of ours is involved.
            await AttachAsync();

            await _publishingPipeline.DidNotReceive().StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Any<BroadcastPlan>(),
                Arg.Any<VideoFormat>(),
                Arg.Any<Uri>(),
                Arg.Any<CancellationToken>());

            await _mediaServer.DidNotReceive().ReservePublishPathAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task Two_viewers_of_the_same_converted_stream_share_one_transcoder()
        {
            // Sharing matters more here than anywhere else: a second transcoder means a second
            // encode session, and on a consumer GPU there are only a handful of those.
            var first = await AttachToH265Async();
            var second = await AttachToH265Async();

            second.ViewerId.Should().NotBe(first.ViewerId);
            (await _coordinator.ListAsync()).Should().ContainSingle().Which.ViewerCount.Should().Be(2);

            await _publishingPipeline.Received(1).StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Any<BroadcastPlan>(),
                Arg.Any<VideoFormat>(),
                Arg.Any<Uri>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task The_transcoder_is_shut_down_with_the_broadcast()
        {
            // A transcoder that outlives its broadcast holds an encode session and a camera
            // connection open for nothing, and nothing else in the system would ever reclaim it.
            var publisher = new RunningPublisher();

            _publishingPipeline.StartAsync(
                    Arg.Any<MediaAddress>(),
                    Arg.Any<BroadcastPlan>(),
                    Arg.Any<VideoFormat>(),
                    Arg.Any<Uri>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => publisher);

            var ticket = await AttachToH265Async();
            await _coordinator.DetachAsync(ticket.ViewerId);

            publisher.DisposeCount.Should().Be(0, "the broadcast lingers before it stops");

            _clock.Advance(Linger + TimeSpan.FromSeconds(1));
            await _coordinator.SweepAsync();

            publisher.DisposeCount.Should().Be(1);
            (await _coordinator.ListAsync()).Should().BeEmpty();

            await _mediaServer.Received().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_transcoder_that_died_does_not_leave_a_broadcast_others_can_join()
        {
            // Without this a viewer arriving after the transcoder stopped is handed a WHEP URL for
            // a path nothing is publishing to - an indefinite black screen with no error anywhere.
            var dead = new RunningPublisher();
            await dead.DisposeAsync();

            _publishingPipeline.StartAsync(
                    Arg.Any<MediaAddress>(),
                    Arg.Any<BroadcastPlan>(),
                    Arg.Any<VideoFormat>(),
                    Arg.Any<Uri>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => dead);

            var first = await AttachToH265Async();
            var second = await AttachToH265Async();

            second.ViewerId.Should().NotBe(first.ViewerId);

            await _publishingPipeline.Received(2).StartAsync(
                Arg.Any<MediaAddress>(),
                Arg.Any<BroadcastPlan>(),
                Arg.Any<VideoFormat>(),
                Arg.Any<Uri>(),
                Arg.Any<CancellationToken>());
        }
    }
}
