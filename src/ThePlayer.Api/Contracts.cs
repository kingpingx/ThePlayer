using System.Text.Json.Serialization;

namespace ThePlayer.Api;

/*
 * The wire contract. These types exist separately from the domain on purpose: docs/PROTOCOL.md
 * freezes this shape, the Angular player is written against it, and a future Go backend has to
 * reproduce it exactly. Serialising domain records directly would make every internal rename a
 * breaking API change.
 */

/// <summary>Response for <c>GET /api/health</c>.</summary>
public sealed record HealthResponse(
    bool Healthy,
    string Environment,
    MediaServerHealth MediaServer,
    HardwareHealth Hardware);

/// <summary>State of the MediaMTX child process.</summary>
public sealed record MediaServerHealth(
    string State,
    string? Version,
    int RestartCount,
    string? Error);

/// <summary>What this machine can do with video.</summary>
public sealed record HardwareHealth(
    // Without this the default camel-case policy emits "fFmpegVersion", because it only lowers
    // the first character of an already-capitalised acronym. The wire contract is frozen in
    // docs/PROTOCOL.md and consumed by Angular, so it gets a name chosen rather than derived.
    [property: JsonPropertyName("ffmpegVersion")] string FFmpegVersion,
    bool HasHardwareAcceleration,
    string PreferredProfile,
    IReadOnlyList<AccelerationProfileInfo> Profiles,
    IReadOnlyList<string> DecodableCodecs);

/// <summary>One confirmed-usable acceleration profile.</summary>
public sealed record AccelerationProfileInfo(
    string Kind,
    string Name,
    bool IsHardware,
    string Encoder,
    string? DecodeAccelerator);
