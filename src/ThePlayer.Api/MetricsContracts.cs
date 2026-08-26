using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Api;

/*
 * The metrics half of the wire contract, kept beside the rest in docs/PROTOCOL.md.
 *
 * Separate file from Contracts.cs only because it is a self-contained feed with its own transport;
 * the same rule applies - these types exist apart from the domain so an internal rename is not a
 * breaking API change.
 */

/// <summary>One reading, as one SSE <c>data:</c> line.</summary>
/// <param name="TakenAt">
/// When the reading was taken. Lets a client tell a stalled feed from an idle machine, which look
/// identical if all you have is a row of zeroes.
/// </param>
public sealed record MetricsResponse(
    DateTimeOffset TakenAt,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    GpuMetricsResponse Gpu,
    IReadOnlyList<BroadcastCostResponse> Broadcasts);

/// <summary>
/// What the GPU is doing, or why that is not known.
/// </summary>
/// <remarks>
/// Every figure is nullable and every unavailable state carries a reason, deliberately. An idle GPU
/// and a failed query look identical if failure is reported as zero, and this feed is only worth
/// having if its numbers can be believed.
/// </remarks>
/// <param name="Availability">Available · NotSupported · ToolMissing · NoPermission.</param>
/// <param name="EncoderPercent">NVENC. The number that proves conversion is happening.</param>
/// <param name="DecoderPercent">NVDEC.</param>
public sealed record GpuMetricsResponse(
    string Availability,
    double? OverallPercent,
    double? EncoderPercent,
    double? DecoderPercent,
    long? MemoryUsedBytes,
    string? UnavailableReason);

/// <summary>
/// What one live broadcast is costing.
/// </summary>
/// <remarks>
/// Carries no address, not even a redacted one. This feed is polled every second and is the last
/// place a camera URL should be repeated; the key is enough to line a row up against
/// <c>GET /api/broadcasts</c>.
/// </remarks>
/// <param name="CpuPercent">
/// Share of one machine's worth of CPU - already divided by core count, so it can be compared with
/// the system figure above rather than exceeding it.
/// </param>
/// <param name="UnavailableReason">
/// Why there is no figure. Non-null for a pass-through <c>ServerAssisted</c> stream, which has no
/// process here to measure - not a gap in the instrumentation but the point being made.
/// </param>
public sealed record BroadcastCostResponse(
    string Key,
    string Mode,
    bool Converted,
    double? CpuPercent,
    long? MemoryBytes,
    string? UnavailableReason);

/// <summary>Domain to wire. One direction only - nothing is ever parsed back.</summary>
public static class MetricsContracts
{
    public static MetricsResponse ToResponse(ResourceSnapshot snapshot) => new(
        TakenAt: snapshot.TakenAt,
        CpuPercent: snapshot.CpuPercent,
        MemoryUsedBytes: snapshot.MemoryUsedBytes,
        MemoryTotalBytes: snapshot.MemoryTotalBytes,
        Gpu: new GpuMetricsResponse(
            Availability: snapshot.Gpu.Availability.ToString(),
            OverallPercent: snapshot.Gpu.OverallPercent,
            EncoderPercent: snapshot.Gpu.EncoderPercent,
            DecoderPercent: snapshot.Gpu.DecoderPercent,
            MemoryUsedBytes: snapshot.Gpu.MemoryUsedBytes,
            UnavailableReason: snapshot.Gpu.UnavailableReason),
        Broadcasts: snapshot.Broadcasts
            .Select(cost => new BroadcastCostResponse(
                Key: cost.Key,
                Mode: cost.Mode.ToString(),
                Converted: cost.Converted,
                CpuPercent: cost.CpuPercent,
                MemoryBytes: cost.MemoryBytes,
                UnavailableReason: cost.UnavailableReason))
            .ToList());
}
