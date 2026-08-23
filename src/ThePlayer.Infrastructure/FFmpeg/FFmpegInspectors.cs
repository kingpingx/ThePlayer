using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>
/// Works out what this machine can do with video by asking the installed FFmpeg what it supports.
/// </summary>
/// <remarks>
/// <para>
/// Detection, never assumption. The same build has to run on an NVIDIA laptop and a VAAPI-only
/// Linux box, so the answer comes from <c>ffmpeg -hwaccels</c> and <c>ffmpeg -encoders</c> at
/// startup rather than from a compile-time guess or the presence of a GPU.
/// </para>
/// <para>
/// A profile is only reported as usable when <em>both</em> halves are confirmed: the encoder is
/// listed, and the decode accelerator is listed. A machine with an NVIDIA card but an FFmpeg built
/// without NVENC has no usable NVIDIA profile, and saying otherwise would produce a pipeline that
/// fails at the first frame instead of at startup.
/// </para>
/// </remarks>
public sealed class FFmpegHardwareInspector(
    FFmpegCommandRunner runner,
    IOptions<FFmpegOptions> options,
    ILogger<FFmpegHardwareInspector> logger) : IHardwareInspector
{
    private readonly FFmpegOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HardwareCapabilities? _cached;

    /// <summary>Codecs worth asking about. Anything else is reported as unknown rather than guessed.</summary>
    private static readonly (VideoCodec Codec, string DecoderName)[] CodecsOfInterest =
    [
        (VideoCodec.H264, "h264"),
        (VideoCodec.H265, "hevc"),
        (VideoCodec.Vp8, "vp8"),
        (VideoCodec.Vp9, "vp9"),
        (VideoCodec.Av1, "av1"),
        (VideoCodec.Mjpeg, "mjpeg"),
    ];

    public async Task<HardwareCapabilities> InspectAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate: several requests can arrive before the first completes.
            _cached ??= await DetectAsync(cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HardwareCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        var ffmpeg = runner.FFmpegPath;

        var version = ReadVersion(
            (await runner.RunAsync(ffmpeg, "-hide_banner -version", cancellationToken: cancellationToken))
                .StandardOutput);

        var accelerators = ReadAccelerators(
            (await runner.RunAsync(ffmpeg, "-hide_banner -hwaccels", cancellationToken: cancellationToken))
                .StandardOutput);

        var encoders = ReadToolNames(
            (await runner.RunAsync(ffmpeg, "-hide_banner -encoders", cancellationToken: cancellationToken))
                .StandardOutput);

        var decoders = ReadToolNames(
            (await runner.RunAsync(ffmpeg, "-hide_banner -decoders", cancellationToken: cancellationToken))
                .StandardOutput);

        var advertised = AccelerationProfile.Known
            .Where(profile => IsAdvertised(profile, accelerators, encoders))
            .OrderBy(profile => profile.Preference)
            .ToList();

        var profiles = _options.VerifyEncoders
            ? await KeepOnlyWorkingAsync(advertised, cancellationToken)
            : advertised;

        if (profiles.Count == 0)
        {
            // libx264 is missing from this FFmpeg build. Everything still works, just slower and
            // via whatever generic encoder FFmpeg picks, so this is a warning rather than a failure.
            logger.LogWarning(
                "No encoder from the known profile list was found in this FFmpeg build. " +
                "Falling back to software and letting FFmpeg choose an encoder.");
            profiles.Add(AccelerationProfile.Software);
        }

        var decodable = CodecsOfInterest
            .Where(entry => decoders.Contains(entry.DecoderName))
            .Select(entry => entry.Codec)
            .ToList();

        logger.LogInformation(
            "FFmpeg {Version}. Acceleration: {Profiles}. Decodes: {Codecs}.",
            version,
            string.Join(", ", profiles.Select(profile => profile.DisplayName)),
            string.Join(", ", decodable));

        return new HardwareCapabilities(version, profiles, decodable);
    }

    /// <summary>
    /// Whether this FFmpeg build claims to support the profile. Both halves must be present: an
    /// encoder without its accelerator, or an accelerator without its encoder, is not a pipeline.
    /// </summary>
    /// <remarks>
    /// "Claims" is the operative word. This only reads the build's capability lists, which say
    /// nothing about whether the hardware is actually present - see <see cref="KeepOnlyWorkingAsync"/>.
    /// </remarks>
    private static bool IsAdvertised(
        AccelerationProfile profile,
        IReadOnlySet<string> accelerators,
        IReadOnlySet<string> encoders)
    {
        if (!encoders.Contains(profile.H264Encoder))
        {
            return false;
        }

        return profile.DecodeAccelerator is null || accelerators.Contains(profile.DecodeAccelerator);
    }

    /// <summary>
    /// Encodes a handful of tiny frames with each advertised encoder and keeps only the ones that
    /// succeed.
    /// </summary>
    /// <remarks>
    /// This is the difference between "FFmpeg was compiled with NVENC" and "this machine can
    /// encode with NVENC". A stock Windows build advertises <c>h264_vaapi</c> and <c>h264_amf</c>
    /// on hardware that has neither, so without this step the health endpoint reports four
    /// hardware profiles on a machine that has one. Probes run concurrently and the whole check
    /// costs about a second, once, at startup.
    /// </remarks>
    private async Task<List<AccelerationProfile>> KeepOnlyWorkingAsync(
        IReadOnlyList<AccelerationProfile> candidates,
        CancellationToken cancellationToken)
    {
        var probes = candidates.Select(async profile =>
        {
            // Software always works; spending a process on proving it is waste.
            if (!profile.IsHardware)
            {
                return (Profile: profile, Works: true);
            }

            return (Profile: profile, Works: await EncoderWorksAsync(profile, cancellationToken));
        });

        var results = await Task.WhenAll(probes);

        foreach (var (profile, works) in results.Where(result => !result.Works))
        {
            logger.LogDebug(
                "{Encoder} is compiled into this FFmpeg but no usable device was found; ignoring it.",
                profile.H264Encoder);
        }

        return results
            .Where(result => result.Works)
            .Select(result => result.Profile)
            .OrderBy(profile => profile.Preference)
            .ToList();
    }

    private async Task<bool> EncoderWorksAsync(
        AccelerationProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(
                runner.FFmpegPath,
                BuildProbeArguments(profile),
                cancellationToken: cancellationToken);

            return result.Succeeded;
        }
        catch (Exception)
        {
            // A timeout or a hard failure both mean the same thing here: do not offer this profile.
            return false;
        }
    }

    /// <summary>
    /// Encodes a two-frame test pattern to nowhere. VA-API is the awkward one - it needs an
    /// explicit device and frames uploaded to it, and on a machine without <c>/dev/dri</c> that
    /// fails, which is exactly the answer we want.
    /// </summary>
    private static string BuildProbeArguments(AccelerationProfile profile)
    {
        const string source = "-f lavfi -i testsrc2=size=128x128:rate=2:duration=1";
        const string common = "-hide_banner -loglevel error";

        return profile.Kind == AccelerationKind.VaApi
            ? $"{common} -vaapi_device /dev/dri/renderD128 {source} " +
              $"-vf format=nv12,hwupload -c:v {profile.H264Encoder} -f null -"
            : $"{common} {source} -c:v {profile.H264Encoder} -f null -";
    }

    /// <summary>
    /// Pulls the version from the first line, which reads
    /// <c>ffmpeg version 8.0-full_build-www.gyan.dev Copyright ...</c>.
    /// </summary>
    private static string ReadVersion(string output)
    {
        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstLine))
        {
            return "unknown";
        }

        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var versionIndex = Array.IndexOf(parts, "version");

        return versionIndex >= 0 && versionIndex + 1 < parts.Length
            ? parts[versionIndex + 1]
            : firstLine;
    }

    /// <summary>
    /// <c>-hwaccels</c> prints a heading and then one method per line.
    /// </summary>
    private static HashSet<string> ReadAccelerators(string output)
    {
        var names = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1) // "Hardware acceleration methods:"
            .Where(line => line.Length > 0 && !line.Contains(' '));

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>-encoders</c> and <c>-decoders</c> print a flags column, then the name, then a
    /// description: <c> V....D h264_nvenc    NVIDIA NVENC H.264 encoder</c>. Everything before the
    /// <c>------</c> separator is a legend and is skipped.
    /// </summary>
    private static HashSet<string> ReadToolNames(string output)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pastHeader = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (!pastHeader)
            {
                pastHeader = line.Contains("------", StringComparison.Ordinal);
                continue;
            }

            // The flags column is not indented consistently across builds, so split on whitespace
            // and take the second token rather than slicing at a fixed offset.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                names.Add(parts[1]);
            }
        }

        return names;
    }
}
