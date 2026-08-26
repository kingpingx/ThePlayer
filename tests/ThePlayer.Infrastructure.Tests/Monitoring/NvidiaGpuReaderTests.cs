using ThePlayer.Domain.Monitoring;
using ThePlayer.Infrastructure.Monitoring;

namespace ThePlayer.Infrastructure.Tests.Monitoring;

/// <summary>
/// Parsing what <c>nvidia-smi</c> prints.
/// </summary>
/// <remarks>
/// The rows below are captured output, not invented. The one that matters is the failure row: the
/// tool answers with <c>[Unknown Error]</c> rather than an error code, so a parser that is merely
/// lenient turns a broken query into an idle GPU — the plausible-looking wrong number this whole
/// design exists to avoid.
/// </remarks>
public class NvidiaGpuReaderTests
{
    [Fact]
    public void A_normal_row_becomes_figures()
    {
        // Captured under an NVENC transcode: both engines lit, which is what conversion looks like.
        var reading = NvidiaGpuReader.Parse("21, 99, 45, 176");

        reading.Should().NotBeNull();
        reading!.Availability.Should().Be(GpuAvailability.Available);
        reading.OverallPercent.Should().Be(21);
        reading.EncoderPercent.Should().Be(99);
        reading.DecoderPercent.Should().Be(45);
        reading.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public void An_idle_card_reports_zeroes_and_stays_available()
    {
        // The counterpart to the test below, and the reason it matters: this row and a failed
        // query must not produce the same object.
        var reading = NvidiaGpuReader.Parse("0, 0, 0, 0");

        reading!.Availability.Should().Be(GpuAvailability.Available);
        reading.EncoderPercent.Should().Be(0);
    }

    [Fact]
    public void Memory_is_reported_in_bytes_though_the_tool_prints_mebibytes()
    {
        var reading = NvidiaGpuReader.Parse("21, 99, 45, 176");

        reading!.MemoryUsedBytes.Should().Be(176L * 1024 * 1024);
    }

    [Fact]
    public void An_unknown_error_is_unavailability_and_never_zero()
    {
        // The single most important assertion in this file. Reporting this as an idle GPU would
        // mean a broken query and a quiet machine look identical on the page.
        const string row = "[Unknown Error], [Unknown Error], [Unknown Error], [Unknown Error]";

        var reading = NvidiaGpuReader.Parse(row);

        reading.Should().NotBeNull();
        reading!.IsAvailable.Should().BeFalse();
        reading.OverallPercent.Should().BeNull();
        reading.EncoderPercent.Should().BeNull();
        reading.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void One_bad_field_rejects_the_whole_row()
    {
        // When the tool starts failing it fails every field at once, so a row mixing a real number
        // with an error is not half a reading - it is a reading that cannot be trusted at all.
        var reading = NvidiaGpuReader.Parse("21, [N/A], 45, 176");

        reading!.IsAvailable.Should().BeFalse();
        reading.OverallPercent.Should().BeNull("a partial row invites trusting the wrong figure");
    }

    [Fact]
    public void A_card_that_reports_no_encoder_engine_is_not_treated_as_idle()
    {
        // [N/A] on the encoder column means "this card does not expose that", which is a different
        // fact from "the encoder is doing nothing".
        var reading = NvidiaGpuReader.Parse("14, [N/A], [N/A], 512");

        reading!.IsAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("21, 99")]
    public void Output_that_is_not_a_row_produces_nothing_at_all(string output)
    {
        // Distinct from the unavailable case on purpose: null means "this parser did not recognise
        // the output", which the caller reports differently from "the tool said it could not tell".
        NvidiaGpuReader.Parse(output).Should().BeNull();
    }

    [Fact]
    public void Trailing_whitespace_and_blank_lines_are_tolerated()
    {
        NvidiaGpuReader.Parse("\n  21, 99, 45, 176  \n\n")!.OverallPercent.Should().Be(21);
    }
}
