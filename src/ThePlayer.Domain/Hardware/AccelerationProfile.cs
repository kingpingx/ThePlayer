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
/// <param name="DecodeOutputFormat">
/// The value for FFmpeg's <c>-hwaccel_output_format</c>, or <c>null</c> to let decoded frames land
/// in system memory.
/// <para>
/// This is the single most expensive flag in the project to get wrong. With it, a decoded frame
/// stays in device memory and is handed straight to the encoder; without it every frame is copied
/// out of the GPU and back in, which costs more than the encode itself. It is separate from
/// <see cref="DecodeAccelerator"/> because they are not the same claim: AMF decodes through
/// D3D11VA but its encoder wants frames in system memory, so that profile sets the accelerator and
/// leaves this null on purpose.
/// </para>
/// </param>
/// <param name="EncoderArguments">
/// Vendor-specific encoder tuning - presets and rate control, which are spelled differently by
/// every engine. Settings that mean the same thing everywhere (<c>-bf</c>, <c>-g</c>) are applied
/// by the argument builder instead, so they cannot drift between profiles.
/// </param>
/// <param name="DeviceArguments">
/// Anything that must appear before the input to select a device, or <c>null</c>. Only VA-API
/// needs this, and needing it is why a VA-API machine without <c>/dev/dri</c> fails detection.
/// </param>
public sealed record AccelerationProfile(
    AccelerationKind Kind,
    string DisplayName,
    string? DecodeAccelerator,
    string H264Encoder,
    int Preference,
    string? DecodeOutputFormat = null,
    string EncoderArguments = "",
    string? DeviceArguments = null)
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
        new(
            AccelerationKind.Nvidia,
            "NVIDIA NVENC/NVDEC",
            DecodeAccelerator: "cuda",
            H264Encoder: "h264_nvenc",
            Preference: 0,
            DecodeOutputFormat: "cuda",
            EncoderArguments: "-preset p1 -tune ll"),

        new(
            AccelerationKind.IntelQuickSync,
            "Intel Quick Sync",
            DecodeAccelerator: "qsv",
            H264Encoder: "h264_qsv",
            Preference: 1,
            DecodeOutputFormat: "qsv",
            EncoderArguments: "-preset veryfast"),

        // No output format: the AMF encoder takes frames from system memory, so asking D3D11VA to
        // keep them on the device produces "Impossible to convert between the formats" at the
        // first frame rather than a faster pipeline.
        new(
            AccelerationKind.AmdAmf,
            "AMD AMF",
            DecodeAccelerator: "d3d11va",
            H264Encoder: "h264_amf",
            Preference: 2,
            DecodeOutputFormat: null,
            EncoderArguments: "-usage lowlatency -quality speed"),

        new(
            AccelerationKind.VaApi,
            "VA-API",
            DecodeAccelerator: "vaapi",
            H264Encoder: "h264_vaapi",
            Preference: 3,
            DecodeOutputFormat: "vaapi",
            EncoderArguments: string.Empty,
            DeviceArguments: "-vaapi_device /dev/dri/renderD128"),

        // The floor. libx264 is present in every practical FFmpeg build, so this profile is what
        // makes "there is always something to fall back to" true.
        new(
            AccelerationKind.Software,
            "Software (libx264)",
            DecodeAccelerator: null,
            H264Encoder: "libx264",
            Preference: 99,
            DecodeOutputFormat: null,
            EncoderArguments: "-preset veryfast -tune zerolatency"),
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
