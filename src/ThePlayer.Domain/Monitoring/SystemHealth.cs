using ThePlayer.Domain.Hardware;

namespace ThePlayer.Domain.Monitoring;

/// <summary>Where the MediaMTX child process is in its lifecycle.</summary>
public enum MediaServerState
{
    /// <summary>The supervisor has not tried to start it yet.</summary>
    NotStarted,

    /// <summary>Launched, but not yet answering its control API.</summary>
    Starting,

    Running,

    /// <summary>It exited and the supervisor is bringing it back.</summary>
    Restarting,

    /// <summary>It could not be started, and retrying will not help without intervention.</summary>
    Failed,
}

/// <summary>
/// What the supervisor knows about the MediaMTX process right now.
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="Version">The version MediaMTX reported, once it is answering.</param>
/// <param name="RestartCount">
/// How many times it has been restarted since the server started. A climbing number is the signal
/// that something is wrong even though the state reads <see cref="MediaServerState.Running"/>.
/// </param>
/// <param name="LastError">Why the last start or run failed, already scrubbed of any credentials.</param>
public sealed record MediaServerStatus(
    MediaServerState State,
    string? Version,
    int RestartCount,
    string? LastError)
{
    public static MediaServerStatus NotStarted { get; } =
        new(MediaServerState.NotStarted, Version: null, RestartCount: 0, LastError: null);

    public bool IsUsable => State == MediaServerState.Running;
}

/// <summary>
/// The answer to "is this server working, and what can it do?" - what <c>/api/health</c> returns.
/// </summary>
/// <param name="MediaServer">State of the MediaMTX child process.</param>
/// <param name="Hardware">FFmpeg version and the acceleration profiles detected on this machine.</param>
/// <param name="Environment">Which named configuration is active: Development, Staging or Production.</param>
/// <param name="EncoderFallbacks">
/// Times a broadcast could not use the encoder it asked for, most recent first.
/// <para>
/// Detection at startup proves an encoder exists; it does not prove a session can be opened later.
/// Without this a machine that has quietly dropped to libx264 looks identical to one that is simply
/// slow, and the profile list above goes on claiming an engine nothing can actually use.
/// </para>
/// </param>
public sealed record SystemHealth(
    MediaServerStatus MediaServer,
    HardwareCapabilities Hardware,
    string Environment,
    IReadOnlyList<EncoderFallbackRecord> EncoderFallbacks)
{
    /// <summary>
    /// Healthy means the pieces needed to actually play something are present: the edge server is
    /// running and FFmpeg was found. Hardware acceleration is a performance concern, not a health
    /// one - software encoding still plays video.
    /// </summary>
    public bool IsHealthy => MediaServer.IsUsable && Hardware.Profiles.Count > 0;
}

/// <summary>One time an encoder that was detected at startup would not open when it was needed.</summary>
/// <param name="FailedProfile">The engine that was tried, by display name.</param>
/// <param name="FailedEncoder">The FFmpeg encoder that refused.</param>
/// <param name="ReplacementProfile">
/// What ran instead, or <c>null</c> when there was nothing further down the ranking and the
/// broadcast failed outright.
/// </param>
/// <param name="Reason">What FFmpeg said, already scrubbed of credentials.</param>
/// <param name="At">When it happened.</param>
public sealed record EncoderFallbackRecord(
    string FailedProfile,
    string FailedEncoder,
    string? ReplacementProfile,
    string Reason,
    DateTimeOffset At);
