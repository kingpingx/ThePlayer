using ThePlayer.Domain.Playback;

namespace ThePlayer.Domain.Monitoring;

/// <summary>Whether GPU figures can be read on this machine, and if not, why not.</summary>
/// <remarks>
/// An enum rather than a bare flag because the four answers call for four different things from
/// whoever reads them: install a driver tool, run with more privilege, accept that this vendor is
/// not supported, or look at the numbers.
/// </remarks>
public enum GpuAvailability
{
    /// <summary>Figures below are real.</summary>
    Available,

    /// <summary>There is no NVIDIA GPU here. Intel and AMD are not supported - see the reason.</summary>
    NotSupported,

    /// <summary><c>nvidia-smi</c> could not be found or would not start.</summary>
    ToolMissing,

    /// <summary>The tool ran and refused, which on Linux usually means the container lacks the device.</summary>
    NoPermission,
}

/// <summary>
/// What the GPU is doing, or why that could not be established.
/// </summary>
/// <remarks>
/// <para>
/// The encode and decode engines are reported <em>separately</em>, and that split is the whole
/// reason this type exists in this shape. Overall utilisation cannot distinguish the three playback
/// modes from one another; the engines can. A converted H.265 feed lights both, because the server
/// is decoding and re-encoding. The same feed passed through lights neither.
/// </para>
/// <para>
/// Every figure is nullable and every unavailable state carries a sentence, because the failure
/// this design is most concerned with is not a missing number but a plausible one: an idle GPU and
/// a failed query look identical if the failure is reported as zero.
/// </para>
/// </remarks>
/// <param name="Availability">Whether the figures mean anything.</param>
/// <param name="OverallPercent">Utilisation across all engines.</param>
/// <param name="EncoderPercent">NVENC. The number that proves conversion is happening.</param>
/// <param name="DecoderPercent">NVDEC.</param>
/// <param name="MemoryUsedBytes">Device memory in use, across every process on the card.</param>
/// <param name="UnavailableReason">Written to be shown to a user. Null when available.</param>
public sealed record GpuUtilisation(
    GpuAvailability Availability,
    double? OverallPercent,
    double? EncoderPercent,
    double? DecoderPercent,
    long? MemoryUsedBytes,
    string? UnavailableReason)
{
    public bool IsAvailable => Availability == GpuAvailability.Available;

    public static GpuUtilisation Available(
        double overallPercent,
        double encoderPercent,
        double decoderPercent,
        long memoryUsedBytes) =>
        new(
            GpuAvailability.Available,
            overallPercent,
            encoderPercent,
            decoderPercent,
            memoryUsedBytes,
            UnavailableReason: null);

    /// <summary>No figures, and a sentence saying why. Never zeroes.</summary>
    public static GpuUtilisation Unavailable(GpuAvailability availability, string reason) =>
        new(availability, null, null, null, null, reason);
}

/// <summary>
/// What one broadcast is costing the server.
/// </summary>
/// <remarks>
/// <para>
/// The most interesting figure in the system, and the comparison the project exists to make. Not
/// "the machine is busy" but "<em>this</em> stream costs 2% on the client path and 61% on the
/// server path".
/// </para>
/// <para>
/// The cost is nullable for a reason that is itself part of the argument. A pass-through
/// <c>ServerAssisted</c> stream has no process of ours behind it at all - the edge server pulls the
/// camera and relays it - so there is nothing to measure, and saying <c>0</c> would be claiming a
/// measurement that was never taken.
/// </para>
/// </remarks>
/// <param name="Key">The broadcast this describes.</param>
/// <param name="Mode">Which of the three paths it is being served on.</param>
/// <param name="Converted">Whether the server is doing codec work for it.</param>
/// <param name="CpuPercent">Share of one machine's worth of CPU, or null if unattributable.</param>
/// <param name="MemoryBytes">Resident memory of the process serving it, or null.</param>
/// <param name="UnavailableReason">Why there is no figure. Null when there is one.</param>
public sealed record BroadcastCost(
    string Key,
    PlaybackMode Mode,
    bool Converted,
    double? CpuPercent,
    long? MemoryBytes,
    string? UnavailableReason);

/// <summary>
/// One reading of what this machine is doing, taken at one moment and sent to every listener.
/// </summary>
/// <remarks>
/// Sampled on a timer and fanned out, rather than sampled per request: ten open tabs should cost
/// one reading, not ten. Sampling stops entirely when nobody is listening.
/// </remarks>
/// <param name="TakenAt">When this reading was taken, so a client can tell a stalled feed from an idle one.</param>
/// <param name="CpuPercent">System-wide CPU, across all cores, as a percentage of one machine.</param>
/// <param name="MemoryUsedBytes">System memory in use.</param>
/// <param name="MemoryTotalBytes">System memory installed, so the used figure means something.</param>
/// <param name="Gpu">What the GPU is doing, or why that is not known.</param>
/// <param name="Broadcasts">What each live broadcast is costing.</param>
public sealed record ResourceSnapshot(
    DateTimeOffset TakenAt,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    GpuUtilisation Gpu,
    IReadOnlyList<BroadcastCost> Broadcasts);

/// <summary>System-wide CPU and memory, with no opinion about what is running.</summary>
/// <remarks>
/// Separate from <see cref="ResourceSnapshot"/> because the reader that produces it knows nothing
/// about broadcasts, and should not have to be handed a list of them to answer a question about
/// the machine.
/// </remarks>
/// <param name="CpuPercent">Null on the first reading: a rate needs two samples to exist.</param>
public sealed record SystemUtilisation(
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes)
{
    public static SystemUtilisation Unknown { get; } = new(null, null, null);
}

/// <summary>What one process is costing, as measured between two readings.</summary>
/// <param name="CpuPercent">
/// Share of one machine's worth of CPU - already divided by core count, so 100 means every core is
/// saturated by this process rather than one of them.
/// </param>
public sealed record ProcessUtilisation(double? CpuPercent, long? MemoryBytes);
