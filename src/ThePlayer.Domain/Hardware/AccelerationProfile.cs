using ThePlayer.Domain.Media;

namespace ThePlayer.Domain.Hardware;

/// <summary>Which vendor's video engine a profile drives.</summary>
public enum AccelerationKind
{
    /// <summary>No hardware engine - libx264 on the CPU. Always available, always slowest.</summary>
    Software,
    Nvidia,
    IntelQuickSync,
    AmdAmf,

    /// <summary>The Linux-generic path, used by Intel and AMD drivers there.</summary>
    VaApi,
}

/// <summary>
/// One usable way to decode and encode video on this machine, expressed as the FFmpeg arguments
/// that select it.
/// <para>
/// Acceleration is detected at startup, never assumed, because the same build has to run on an
/// NVIDIA laptop and on a VAAPI-only Linux box. Holding the arguments as data rather than
/// branching on the vendor inside the pipelines is what keeps one pipeline implementation working
/// across all of them.
/// </para>
/// </summary>
/// <param name="Kind">Which vendor engine this is.</param>
/// <param name="DisplayName">Human-readable name, shown by <c>/api/health</c>.</param>
/// <param name="DecodeAccelerator">
/// The value for FFmpeg's <c>-hwaccel</c>, or <c>null</c> to decode on the CPU.
/// </param>
/// <param name="H264Encoder">The FFmpeg encoder that produces H.264 on this engine.</param>
/// <param name="Preference">Ranking order - lower is preferred. Ties are not expected.</param>
public sealed record AccelerationProfile(
    AccelerationKind Kind,
    string DisplayName,
    string? DecodeAccelerator,
    string H264Encoder,
    int Preference)
{
    public bool IsHardware => Kind != AccelerationKind.Software;

    public override string ToString() => DisplayName;

    /// <summary>
    /// Every profile this system knows how to drive, best first.
    /// <para>
    /// This is a catalogue of knowledge, not a detection result: it says what each engine would be
    /// called if present. The hardware inspector intersects this list with what the installed
    /// FFmpeg actually reports, so a profile only becomes usable once its encoder is confirmed.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AccelerationProfile> Known { get; } =
    [
        new(AccelerationKind.Nvidia, "NVIDIA NVENC/NVDEC", "cuda", "h264_nvenc", Preference: 0),
        new(AccelerationKind.IntelQuickSync, "Intel Quick Sync", "qsv", "h264_qsv", Preference: 1),
        new(AccelerationKind.AmdAmf, "AMD AMF", "d3d11va", "h264_amf", Preference: 2),
        new(AccelerationKind.VaApi, "VA-API", "vaapi", "h264_vaapi", Preference: 3),

        // The floor. libx264 is present in every practical FFmpeg build, so this profile is what
        // makes "there is always something to fall back to" true.
        new(AccelerationKind.Software, "Software (libx264)", null, "libx264", Preference: 99),
    ];

    /// <summary>The always-available fallback, used when no hardware engine is confirmed.</summary>
    public static AccelerationProfile Software { get; } =
        Known.Single(profile => profile.Kind == AccelerationKind.Software);
}

/// <summary>
/// What one machine can actually do, as reported by inspecting the installed FFmpeg.
/// </summary>
/// <param name="FFmpegVersion">The version string FFmpeg reported, for display and support questions.</param>
/// <param name="Profiles">Confirmed usable profiles, best first. Never empty - software is always present.</param>
/// <param name="DecodableCodecs">Codecs this machine can decode at all, by any means.</param>
public sealed record HardwareCapabilities(
    string FFmpegVersion,
    IReadOnlyList<AccelerationProfile> Profiles,
    IReadOnlyList<VideoCodec> DecodableCodecs)
{
    /// <summary>The profile to use unless configuration says otherwise.</summary>
    public AccelerationProfile Preferred => Profiles[0];

    public bool HasHardwareAcceleration => Profiles.Any(profile => profile.IsHardware);
}
