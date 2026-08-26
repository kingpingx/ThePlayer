using Microsoft.Extensions.Time.Testing;
using ThePlayer.Application.Monitoring;
using ThePlayer.Domain.Hardware;

namespace ThePlayer.Application.Tests.Monitoring;

/// <summary>
/// What makes a silent downgrade visible.
/// </summary>
/// <remarks>
/// Without this record a machine that has quietly dropped to libx264 is indistinguishable from one
/// that is merely slow, and the profile list in <c>/api/health</c> goes on advertising an engine
/// nothing can actually open.
/// </remarks>
public class EncoderFallbackLogTests
{
    private static readonly AccelerationProfile Nvidia =
        AccelerationProfile.Known.Single(profile => profile.Kind == AccelerationKind.Nvidia);

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T12:00:00Z"));

    private EncoderFallbackLog Log() => new(_clock);

    [Fact]
    public void A_machine_where_nothing_has_failed_reports_nothing()
    {
        Log().Recent.Should().BeEmpty();
    }

    [Fact]
    public void A_downgrade_records_what_failed_and_what_replaced_it()
    {
        var log = Log();

        log.Record(Nvidia, AccelerationProfile.Software, "[h264_nvenc] No capable devices found");

        var entry = log.Recent.Should().ContainSingle().Subject;
        entry.FailedProfile.Should().Be(Nvidia.DisplayName);
        entry.FailedEncoder.Should().Be("h264_nvenc");
        entry.ReplacementProfile.Should().Be(AccelerationProfile.Software.DisplayName);
        entry.At.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public void Running_out_of_engines_is_recorded_with_no_replacement()
    {
        var log = Log();

        log.Record(AccelerationProfile.Software, replacement: null, "libx264 not found");

        log.Recent.Should().ContainSingle().Which.ReplacementProfile.Should().BeNull();
    }

    [Fact]
    public void The_most_recent_failure_comes_first()
    {
        // Someone reading this after the fact wants the last thing that happened, not the first.
        var log = Log();

        log.Record(Nvidia, AccelerationProfile.Software, "first");
        _clock.Advance(TimeSpan.FromMinutes(1));
        log.Record(Nvidia, AccelerationProfile.Software, "second");

        log.Recent[0].Reason.Should().Be("second");
    }

    [Fact]
    public void Only_the_line_that_explains_it_is_kept()
    {
        // FFmpeg follows the specific complaint with a generic wrapper. Storing everything would
        // put a paragraph per entry into a response that is polled.
        var log = Log();

        log.Record(
            Nvidia,
            AccelerationProfile.Software,
            "[h264_nvenc @ 000001f2] No capable devices found\nError initializing output stream 0:0");

        log.Recent[0].Reason.Should().Be("[h264_nvenc @ 000001f2] No capable devices found");
    }

    [Fact]
    public void The_line_naming_the_encoder_wins_over_the_first_one()
    {
        // Captured from a real NVENC refusal. Taking the first line blames the decoder for a
        // fallback the encoder caused - a diagnostic that sends whoever reads it the wrong way.
        var log = Log();

        log.Record(
            Nvidia,
            AccelerationProfile.Software,
            """
            [hevc @ 00000272bf33d200] Video width 32 not within range from 144 to 8192
            [hevc @ 00000272bf33d200] Failed setup for format cuda: hwaccel initialisation returned error.
            [h264_nvenc @ 00000272bf333fc0] InitializeEncoder failed: invalid param (8): Frame Dimension less than the minimum supported value.
            [vf#0:0 @ 00000272bf373a00] Task finished with error code: -22 (Invalid argument)
            """);

        log.Recent[0].Reason.Should().StartWith("[h264_nvenc");
        log.Recent[0].Reason.Should().Contain("InitializeEncoder failed");
    }

    [Fact]
    public void An_output_that_never_names_the_encoder_still_says_something()
    {
        var log = Log();

        log.Record(Nvidia, AccelerationProfile.Software, "Error while opening encoder for output stream #0:0");

        log.Recent[0].Reason.Should().Be("Error while opening encoder for output stream #0:0");
    }

    [Fact]
    public void An_encoder_that_said_nothing_still_produces_a_readable_entry()
    {
        var log = Log();

        log.Record(Nvidia, AccelerationProfile.Software, string.Empty);

        log.Recent[0].Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_very_long_complaint_is_truncated()
    {
        var log = Log();

        log.Record(Nvidia, AccelerationProfile.Software, new string('x', 1000));

        log.Recent[0].Reason.Length.Should().BeLessThan(400);
    }

    [Fact]
    public void The_log_is_bounded()
    {
        // A camera that fails every reconnection would otherwise grow this without limit, and the
        // health endpoint would slowly become a log file.
        var log = Log();

        for (var i = 0; i < 200; i++)
        {
            log.Record(Nvidia, AccelerationProfile.Software, $"attempt {i}");
        }

        log.Recent.Should().HaveCountLessThan(50);
        log.Recent[0].Reason.Should().Be("attempt 199", "the newest is always kept");
    }
}
