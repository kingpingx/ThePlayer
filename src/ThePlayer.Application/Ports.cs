using ThePlayer.Domain.Hardware;
using ThePlayer.Domain.Media;
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
/// Finds out what is actually at an address - codec, size, frame rate, and whether it ends.
/// </summary>
public interface IMediaInspector
{
    /// <summary>
    /// Inspects a camera or file. Connects to the upstream, so it is slow and can fail for all the
    /// ordinary network reasons.
    /// </summary>
    /// <exception cref="MediaInspectionException">
    /// The address could not be read. The message is safe to show a user - it never contains the
    /// credentials from the address.
    /// </exception>
    Task<VideoFormat> InspectAsync(MediaAddress address, CancellationToken cancellationToken = default);
}

/// <summary>
/// The edge server that repackages a stream for browsers - publishing a source under a path and
/// serving it over WebRTC.
/// </summary>
public interface IMediaServer
{
    /// <summary>
    /// Publishes an upstream under <paramref name="path"/>, on demand: nothing connects to the
    /// camera until a viewer actually asks for it.
    /// </summary>
    /// <remarks>
    /// Registration goes over the control API rather than into a config file, so the credentials
    /// in <paramref name="address"/> stay in memory and never touch disk.
    /// </remarks>
    Task PublishAsync(string path, MediaAddress address, CancellationToken cancellationToken = default);

    /// <summary>Removes a published path. Safe to call for a path that is already gone.</summary>
    Task RemoveAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>The WHEP endpoint a browser posts its SDP offer to for this path.</summary>
    Uri WhepUrlFor(string path);
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

/// <summary>
/// Raised when an address cannot be read: unreachable, wrong credentials, not a video file, or a
/// format we cannot make sense of.
/// </summary>
/// <remarks>
/// The message is written to be shown to a user and is always scrubbed of credentials, because the
/// most common cause is a wrong password and the most common thing to say about it is the URL.
/// </remarks>
public sealed class MediaInspectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
