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

// ---------------------------------------------------------------------------------------------
// Watching
// ---------------------------------------------------------------------------------------------

/// <summary>Request for <c>POST /api/watch</c>.</summary>
/// <param name="Address">An RTSP URL or a full path to a video file. May contain credentials.</param>
/// <param name="Mode">ClientDecoded · ServerAssisted · ServerDecoded.</param>
/// <param name="ClientDecodeSupport">
/// What this browser reported it can decode. Optional, but omitting it rules out client-side
/// decoding - the server will not guess on a client's behalf.
/// </param>
public sealed record WatchRequest(
    string Address,
    string Mode,
    ClientDecodeSupportRequest? ClientDecodeSupport);

/// <summary>The result of probing <c>VideoDecoder.isConfigSupported()</c> in the browser.</summary>
public sealed record ClientDecodeSupportRequest(
    bool WebCodecs,
    IReadOnlyList<CodecSupportRequest> Codecs);

/// <param name="Codec">H264 · H265 · Vp8 · Vp9 · Av1 · Mjpeg.</param>
/// <param name="Supported">Whether the browser can decode it at all.</param>
/// <param name="HardwareAccelerated">Whether it would do so in hardware.</param>
public sealed record CodecSupportRequest(string Codec, bool Supported, bool HardwareAccelerated);

/// <summary>Response for <c>POST /api/watch</c>. Never echoes the address.</summary>
public sealed record WatchResponse(
    string ViewerId,
    string Mode,
    VideoFormatResponse Format,
    bool Converted,
    IReadOnlyList<ModeAvailabilityResponse> ModeAvailability,
    TransportResponse Transport);

/// <param name="Live">
/// True for a camera, false for a file. A file reaches a last frame and the broadcast ends.
/// </param>
public sealed record VideoFormatResponse(
    string Codec,
    int Width,
    int Height,
    double FrameRate,
    bool Live,
    double? DurationSeconds);

/// <summary>Whether a mode can be used for this stream, and if not, why not.</summary>
public sealed record ModeAvailabilityResponse(string Mode, bool Available, string? Reason);

/// <param name="Kind">WebRtc or WebSocket.</param>
/// <param name="Url">
/// For WebRtc, the WHEP endpoint to POST an SDP offer to. For WebSocket, the frame socket path.
/// </param>
public sealed record TransportResponse(string Kind, string Url);

/// <summary>One live broadcast, for <c>GET /api/broadcasts</c>. Addresses are redacted.</summary>
public sealed record BroadcastSummary(
    string Key,
    string Address,
    string State,
    string Mode,
    bool Converted,
    int Viewers,
    VideoFormatResponse Format);
