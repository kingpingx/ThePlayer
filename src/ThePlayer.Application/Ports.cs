using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Monitoring;

namespace ThePlayer.Application;

/*
 * The ports Infrastructure must satisfy.
 *
 * Everything here crosses a process or platform boundary - an external binary, a child process,
 * an OS-specific counter. That is the test for whether something belongs in this file. Pure
 * in-process logic (BroadcastPlanner, BroadcastCoordinator, FrameBroadcaster) is deliberately
 * concrete: an interface with one implementation and no boundary to cross is indirection, not
 * dependency inversion.
 *
 * Ports are added as the phase that needs them arrives, rather than declared up front against
 * implementations that do not exist yet.
 */

/// <summary>
/// Discovers what this machine can do with video, by asking the installed FFmpeg.
/// </summary>
/// <remarks>
/// Separate from media inspection on purpose: this asks about the <em>machine</em>, that asks about
/// a <em>stream</em>. Keeping them apart means a test needing a fake stream inspector does not also
/// have to describe an imaginary GPU.
/// </remarks>
public interface IHardwareInspector
{
    /// <summary>
    /// Runs once at startup and caches. Returns the software profile alone if no hardware engine
    /// is confirmed; throws only if FFmpeg itself cannot be found or run.
    /// </summary>
    Task<HardwareCapabilities> InspectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the lifetime of the MediaMTX child process: finding the binary, launching it, and
/// bringing it back when it exits.
/// </summary>
public interface IMediaServerSupervisor
{
    /// <summary>
    /// The current state, safe to read from any thread. Never throws - a failed server is
    /// reported as <see cref="MediaServerState.Failed"/> with a reason, because the health
    /// endpoint has to be able to describe a broken server without breaking itself.
    /// </summary>
    MediaServerStatus Status { get; }
}
