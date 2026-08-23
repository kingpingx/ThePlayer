namespace ThePlayer.Domain.Media;

/// <summary>The video codecs this system recognises.</summary>
public enum VideoCodec
{
    Unknown,
    H264,
    H265,
    Vp8,
    Vp9,
    Av1,
    Mjpeg,
}

/// <summary>
/// What a piece of media actually is, as reported by inspecting it.
/// </summary>
/// <param name="Codec">The codec the media is encoded with.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="FrameRate">Frames per second. Used to synthesise timestamps for WebCodecs.</param>
/// <param name="Duration">
/// How long the media runs, or <c>null</c> for a live feed. This is the one field that
/// distinguishes a camera from a file everywhere downstream.
/// </param>
public sealed record VideoFormat(
    VideoCodec Codec,
    int Width,
    int Height,
    double FrameRate,
    TimeSpan? Duration)
{
    /// <summary>A live feed runs until stopped; a file reaches a last frame and ends.</summary>
    public bool IsLive => Duration is null;

    public override string ToString() =>
        $"{Codec} {Width}x{Height} @ {FrameRate:0.##}fps{(IsLive ? " (live)" : $" ({Duration})")}";
}

/// <summary>
/// Translates between codec names as different tools spell them.
/// </summary>
/// <remarks>
/// Kept beside <see cref="VideoFormat"/> because it exists only to build one: FFprobe reports
/// codecs by short name, and the browser needs an RFC 6381 string to configure a decoder.
/// </remarks>
public static class VideoCodecNames
{
    /// <summary>Maps an FFprobe <c>codec_name</c> to a <see cref="VideoCodec"/>.</summary>
    public static VideoCodec FromFFprobeName(string? codecName) => codecName?.ToLowerInvariant() switch
    {
        "h264" or "avc" or "avc1" => VideoCodec.H264,
        "hevc" or "h265" or "hvc1" or "hev1" => VideoCodec.H265,
        "vp8" => VideoCodec.Vp8,
        "vp9" => VideoCodec.Vp9,
        "av1" => VideoCodec.Av1,
        "mjpeg" => VideoCodec.Mjpeg,
        _ => VideoCodec.Unknown,
    };

    /// <summary>
    /// A conservative RFC 6381 codec string, suitable for <c>VideoDecoder.isConfigSupported</c>
    /// when the exact profile and level are not yet known.
    /// </summary>
    /// <remarks>
    /// These are the widely supported baselines used for capability probing. The precise string
    /// for a live stream is derived from its parameter sets once frames are flowing; this is the
    /// value used to ask "could this browser decode this family of codec at all?".
    /// </remarks>
    public static string? ToProbeString(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "avc1.42001f",
        VideoCodec.H265 => "hev1.1.6.L93.B0",
        VideoCodec.Vp8 => "vp8",
        VideoCodec.Vp9 => "vp09.00.10.08",
        VideoCodec.Av1 => "av01.0.04M.08",
        _ => null,
    };
}
