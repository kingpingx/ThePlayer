using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ThePlayer.Application.Broadcasting;
using ThePlayer.Application.Monitoring;
using ThePlayer.Application.Security;
using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
using ThePlayer.Domain.Monitoring;
using ThePlayer.Domain.Playback;

namespace ThePlayer.Application.Tests.Monitoring;

/// <summary>
/// Sampling, fan-out, and the rule that a cost which was not measured is never reported as zero.
/// </summary>
public class MetricsCollectorTests
{
    private readonly ISystemMetricsReader _system = Substitute.For<ISystemMetricsReader>();
    private readonly IGpuMetricsReader _gpu = Substitute.For<IGpuMetricsReader>();
    private readonly IProcessMetricsReader _processes = Substitute.For<IProcessMetricsReader>();
    private readonly IMediaInspector _inspector = Substitute.For<IMediaInspector>();
    private readonly IMediaServer _mediaServer = Substitute.For<IMediaServer>();
    private readonly IHardwareInspector _hardware = Substitute.For<IHardwareInspector>();
    private readonly IFramePipeline _framePipeline = Substitute.For<IFramePipeline>();
    private readonly IPublishingPipeline _publishingPipeline = Substitute.For<IPublishingPipeline>();

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly BroadcastCoordinator _coordinator;
    private readonly MetricsCollector _collector;

    public MetricsCollectorTests()
    {
        _system.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new SystemUtilisation(12.5, 8_000_000_000, 16_000_000_000));

        _gpu.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(GpuUtilisation.Available(21, 99, 45, 176 * 1024 * 1024));

        _processes.Read(Arg.Any<IReadOnlyCollection<int>>())
            .Returns(new Dictionary<int, ProcessUtilisation>());

        _inspector.InspectAsync(Arg.Any<MediaAddress>(), Arg.Any<CancellationToken>())
            .Returns(new VideoFormat(VideoCodec.H264, 1920, 1080, 25, Duration: null));

        _hardware.InspectAsync(Arg.Any<CancellationToken>())
            .Returns(new HardwareCapabilities("8.0", [AccelerationProfile.Software], [VideoCodec.H264]));

        _mediaServer.WhepUrlFor(Arg.Any<string>())
            .Returns(callInfo => new Uri($"http://localhost:8889/{callInfo.Arg<string>()}/whep"));

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
            new AddressGuard(
                Substitute.For<IAddressResolver>(),
                Options.Create(new AddressPolicyOptions()),
                NullLogger<AddressGuard>.Instance),
            Options.Create(new BroadcastOptions()),
            _clock,
            NullLogger<BroadcastCoordinator>.Instance);

        _collector = new MetricsCollector(
            _system,
            _gpu,
            _processes,
            _coordinator,
            Options.Create(new MetricsOptions()),
            _clock,
            NullLogger<MetricsCollector>.Instance);
    }

    /// <summary>A pipeline with no process behind it, which is the coordinator's own fake too.</summary>
    private sealed class SilentFrameStream : IFrameStream
    {
        public StreamInitialisation Initialisation { get; } = new("avc1.42c01f", 1920, 1080, 25);

        public int? ProcessId => null;

        public async IAsyncEnumerable<EncodedFrame> FramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MediaAddress Address(string path = "/videos/clip.mp4") =>
        MediaAddress.Parse(OperatingSystem.IsWindows() ? $"D:{path.Replace('/', '\\')}" : path);

    public class Sampling : MetricsCollectorTests
    {
        [Fact]
        public async Task Nothing_is_measured_when_nobody_is_listening()
        {
            // The reason this matters is not tidiness: a GPU reading costs a process spawn, so an
            // idle server with no browser attached would otherwise spawn one every second forever.
            await _collector.SampleAsync();

            await _system.DidNotReceive().ReadAsync(Arg.Any<CancellationToken>());
            await _gpu.DidNotReceive().ReadAsync(Arg.Any<CancellationToken>());
            _collector.Latest.Should().BeNull();
        }

        [Fact]
        public async Task One_listener_makes_it_measure()
        {
            using var subscription = _collector.Subscribe();

            await _collector.SampleAsync();

            await _system.Received(1).ReadAsync(Arg.Any<CancellationToken>());
            await _gpu.Received(1).ReadAsync(Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task Ten_listeners_still_cost_one_reading()
        {
            // The whole point of a collector rather than sampling per request.
            var subscriptions = Enumerable.Range(0, 10).Select(_ => _collector.Subscribe()).ToList();

            try
            {
                await _collector.SampleAsync();

                await _gpu.Received(1).ReadAsync(Arg.Any<CancellationToken>());
            }
            finally
            {
                subscriptions.ForEach(subscription => subscription.Dispose());
            }
        }

        [Fact]
        public async Task Every_listener_gets_the_same_reading()
        {
            using var first = _collector.Subscribe();
            using var second = _collector.Subscribe();

            await _collector.SampleAsync();

            var a = await first.Snapshots.ReadAsync();
            var b = await second.Snapshots.ReadAsync();

            a.Should().BeSameAs(b, "one sample is fanned out, not taken twice");
        }

        [Fact]
        public async Task Releasing_the_last_listener_stops_the_sampling()
        {
            var subscription = _collector.Subscribe();
            await _collector.SampleAsync();

            subscription.Dispose();
            await _collector.SampleAsync();

            await _gpu.Received(1).ReadAsync(Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task A_new_listener_is_handed_the_last_reading_immediately()
        {
            // Otherwise the panel is blank for up to a whole interval, which reads as broken rather
            // than as waiting - and the reading it would have waited for is this same one.
            using var first = _collector.Subscribe();
            await _collector.SampleAsync();

            using var late = _collector.Subscribe();

            late.Snapshots.TryRead(out var snapshot).Should().BeTrue();
            snapshot.Should().BeSameAs(_collector.Latest);
        }

        [Fact]
        public async Task A_reader_that_throws_does_not_take_the_sample_with_it()
        {
            // This runs on a loop that must not be the thing that stops. A feed reporting one
            // unreadable counter is far better than a feed that ended an hour ago.
            _gpu.ReadAsync(Arg.Any<CancellationToken>())
                .Returns<Task<GpuUtilisation>>(_ => throw new InvalidOperationException("driver gone"));

            using var subscription = _collector.Subscribe();

            var act = async () => await _collector.SampleAsync();

            await act.Should().NotThrowAsync();
            _collector.Latest!.Gpu.IsAvailable.Should().BeFalse();
            _collector.Latest.CpuPercent.Should().Be(12.5, "the other readers still answered");
        }

        [Fact]
        public async Task The_reading_carries_the_moment_it_was_taken()
        {
            using var subscription = _collector.Subscribe();

            await _collector.SampleAsync();

            _collector.Latest!.TakenAt.Should().Be(_clock.GetUtcNow());
        }
    }

    public class AttributingCost : MetricsCollectorTests
    {
        private async Task<string> StartClientDecodedAsync()
        {
            var capable = new ClientDecodeSupport(
                WebCodecsAvailable: true,
                [new CodecSupport(VideoCodec.H264, Supported: true, HardwareAccelerated: true)]);

            await _coordinator.AttachAsync(Address(), PlaybackMode.ClientDecoded, capable);

            return (await _coordinator.ListAsync()).Single().Key;
        }

        [Fact]
        public async Task A_live_broadcast_appears_in_the_reading()
        {
            var key = await StartClientDecodedAsync();

            using var subscription = _collector.Subscribe();
            await _collector.SampleAsync();

            var cost = _collector.Latest!.Broadcasts.Should().ContainSingle().Subject;
            cost.Key.Should().Be(key);
            cost.Mode.Should().Be(PlaybackMode.ClientDecoded);
        }

        [Fact]
        public async Task A_broadcast_with_no_process_behind_it_reports_no_figure_and_a_reason()
        {
            // Not a gap in the instrumentation - it is the finding. A pass-through ServerAssisted
            // stream is relayed by the edge server, so there is nothing here to measure precisely
            // because nothing here is being spent. Zero would say the same as a broken reading.
            await _coordinator.AttachAsync(Address(), PlaybackMode.ServerAssisted, ClientDecodeSupport.None);

            using var subscription = _collector.Subscribe();
            await _collector.SampleAsync();

            var cost = _collector.Latest!.Broadcasts.Should().ContainSingle().Subject;
            cost.CpuPercent.Should().BeNull();
            cost.MemoryBytes.Should().BeNull();
            cost.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task Nothing_is_asked_of_the_process_reader_when_no_broadcast_owns_one()
        {
            await _coordinator.AttachAsync(Address(), PlaybackMode.ServerAssisted, ClientDecodeSupport.None);

            using var subscription = _collector.Subscribe();
            await _collector.SampleAsync();

            _processes.DidNotReceive().Read(Arg.Any<IReadOnlyCollection<int>>());
        }

        [Fact]
        public async Task A_reading_with_no_broadcasts_is_an_empty_list_rather_than_absent()
        {
            using var subscription = _collector.Subscribe();
            await _collector.SampleAsync();

            _collector.Latest!.Broadcasts.Should().BeEmpty();
        }
    }
}
