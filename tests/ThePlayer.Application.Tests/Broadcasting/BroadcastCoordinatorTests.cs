using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ThePlayer.Application.Broadcasting;
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

        _coordinator = new BroadcastCoordinator(
            _inspector,
            _mediaServer,
            _hardware,
            new BroadcastPlanner(),
            Options.Create(new BroadcastOptions { Linger = Linger }),
            _clock,
            NullLogger<BroadcastCoordinator>.Instance);
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
        [InlineData(PlaybackMode.ClientDecoded)]
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
        public async Task A_stream_needing_conversion_names_the_phase_that_will_deliver_it()
        {
            _inspector.InspectAsync(Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>())
                .Returns(new VideoFormat(VideoCodec.H265, 1920, 1080, 25, Duration: null));

            var act = async () => await AttachAsync();

            (await act.Should().ThrowAsync<NotSupportedException>())
                .Which.Message.Should().Contain("Phase 3");
        }
    }
}
