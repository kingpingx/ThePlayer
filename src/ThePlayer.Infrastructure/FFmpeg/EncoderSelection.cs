using ThePlayer.Domain.Broadcasting;
using ThePlayer.Domain.Hardware;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>
/// Which engines to try for a conversion, and how to tell that one has refused.
/// </summary>
/// <remarks>
/// <para>
/// Both conversion pipelines need exactly this and nothing else of each other, so it lives here
/// rather than in a base class they would otherwise share. Pure functions over strings and lists,
/// which is what makes the interesting case - the fourth NVENC session on a consumer card - a unit
/// test rather than something you can only reproduce by opening four browser tabs.
/// </para>
/// </remarks>
public static class EncoderSelection
{
    /// <summary>
    /// Phrases that mean "the encoder would not open", regardless of which encoder it was.
    /// </summary>
    /// <remarks>
    /// FFmpeg always follows a specific complaint with one of these, so matching them catches
    /// vendor failures whose exact wording changes between driver versions.
    /// </remarks>
    private static readonly string[] GenericEncoderFailures =
    [
        "error while opening encoder",
        "error initializing output stream",
        "cannot load nvcuda",
        "no capable devices found",
        "openencodesessionex failed",
        "device creation failed",
        "failed to set value",

        // The encoder is gone entirely - FFmpeg was replaced by a build without it after detection
        // had already cached its answer. Dropping to software is exactly the right response.
        "unknown encoder",
        "encoder not found",
        "error selecting an encoder",

        // The hardware-format mismatch: the decode fell back to software and handed the encoder
        // frames it cannot take, or the reverse. Another engine may well not have the problem.
        "impossible to convert between the formats",
        "function not implemented",
    ];

    /// <summary>
    /// The engines to try, in order, starting with the one the plan chose.
    /// </summary>
    /// <remarks>
    /// Everything ranked <em>below</em> the plan's choice follows it, which is what makes the
    /// fallback a downgrade rather than a retry. A profile that is no longer in the detected set -
    /// a plan built before a re-detection, in principle - falls back to the whole ranking rather
    /// than to nothing.
    /// </remarks>
    /// <returns>Empty when the plan requires no conversion; there is nothing to choose.</returns>
    public static IReadOnlyList<AccelerationProfile> Candidates(
        BroadcastPlan plan,
        HardwareCapabilities hardware)
    {
        if (!plan.RequiresConversion)
        {
            return [];
        }

        var ranked = hardware.Profiles.OrderBy(profile => profile.Preference).ToList();

        if (plan.Acceleration is not { } preferred)
        {
            return ranked;
        }

        var start = ranked.FindIndex(profile => profile.Kind == preferred.Kind);

        return start < 0
            ? [preferred, .. ranked]
            : ranked.Skip(start).ToList();
    }

    /// <summary>
    /// Whether this FFmpeg output is an engine that could not do the job, as opposed to a source
    /// that could not be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction is what stops a fallback being pointless. An unreachable camera fails
    /// identically on every engine, so retrying down the ranking would spend one connection
    /// timeout per profile before reporting the same thing - turning a six-second failure into
    /// half a minute of spinner. A refused encoder, by contrast, is exactly what the next profile
    /// down exists for.
    /// </para>
    /// <para>
    /// The unit being judged is the whole profile, decoder included, even though the name says
    /// encoder. A hardware decoder that will not take a stream is as good a reason to try the next
    /// engine as an encoder that will not open, and in practice the two arrive together: a profile
    /// refusing an unusual resolution complains about its decoder several lines before its encoder
    /// gets a turn.
    /// </para>
    /// </remarks>
    public static bool LooksLikeEncoderFailure(string? output, AccelerationProfile profile)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            // No output at all says nothing either way, and guessing "encoder" here would make
            // every unreachable camera walk the whole ranking.
            return false;
        }

        // Naming the encoder is the strongest signal there is, and the bare name is matched rather
        // than a bracketed prefix because FFmpeg spells that prefix several ways for the same
        // failure - "[h264_qsv @ ...]" on one line and "[vost#0:0/h264_qsv @ ...]" on the next.
        // Nothing else we log mentions the encoder, since the command line is never echoed here.
        if (output.Contains(profile.H264Encoder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GenericEncoderFailures.Any(phrase =>
            output.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
}
