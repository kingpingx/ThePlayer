using ThePlayer.Domain.Media;
using ThePlayer.Infrastructure.FFmpeg;

namespace ThePlayer.Infrastructure.Tests.FFmpeg;

/// <summary>
/// The codec string a browser is asked to configure a decoder with.
/// </summary>
/// <remarks>
/// Every parameter set here was captured from a real stream produced by the same FFmpeg command the
/// pipeline runs, rather than hand-built. A synthetic SPS would prove the parser self-consistent
/// and nothing more.
/// </remarks>
public class CodecStringBuilderTests
{
    /// <summary>
    /// Captured from <c>testsrc2</c> encoded with libx264 - the SPS NAL, header byte included, with
    /// no start code.
    /// </summary>
    private static readonly byte[] H264Sps = Convert.FromHexString(
        "6742c00dda0507ec0440000003004000000c83c50aa8");

    /// <summary>
    /// The same, from libx265. Main profile, level 2.0 - and it carries three emulation-prevention
    /// bytes, which is exactly why the parser has to strip them before reading a single field.
    /// </summary>
    private static readonly byte[] H265Sps = Convert.FromHexString(
        "42010101600000030090000003000003003ca00a080f1659");

    [Fact]
    public void H264_is_three_bytes_read_straight_out_of_the_parameter_set()
    {
        // profile_idc 0x42, constraint flags 0xc0, level_idc 0x0d - bytes 1..3 of the SPS.
        CodecStringBuilder.Build(VideoCodec.H264, H264Sps).Should().Be("avc1.42c00d");
    }

    [Fact]
    public void H265_is_read_bit_by_bit_out_of_profile_tier_level()
    {
        // Main profile (1), compatibility flags 0x60000000 reversed to 6, main tier (L),
        // level_idc 0x3c = 60 = level 2.0, and one non-zero constraint byte.
        CodecStringBuilder.Build(VideoCodec.H265, H265Sps).Should().Be("hev1.1.6.L60.90");
    }

    [Fact]
    public void A_truncated_H264_parameter_set_fails_rather_than_guessing()
    {
        // The alternative is a plausible-looking string that makes configure() reject the stream
        // for reasons nobody can trace back to here.
        var act = () => CodecStringBuilder.Build(VideoCodec.H264, H264Sps.AsSpan(0, 3).ToArray());

        act.Should().Throw<InvalidDataException>().WithMessage("*at least 4 bytes*");
    }

    [Fact]
    public void A_truncated_H265_parameter_set_fails_rather_than_guessing()
    {
        // Long enough to start parsing, too short to finish profile_tier_level.
        var act = () => CodecStringBuilder.Build(VideoCodec.H265, H265Sps.AsSpan(0, 8).ToArray());

        act.Should().Throw<InvalidDataException>().WithMessage("*profile_tier_level*");
    }

    [Theory]
    [InlineData(VideoCodec.Vp9)]
    [InlineData(VideoCodec.Mjpeg)]
    [InlineData(VideoCodec.Unknown)]
    public void A_codec_with_no_defined_string_fails(VideoCodec codec)
    {
        var act = () => CodecStringBuilder.Build(codec, H264Sps);

        act.Should().Throw<InvalidDataException>();
    }
}
