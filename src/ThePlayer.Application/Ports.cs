using ThePlayer.Domain.Broadcasting;
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

    /// <summary>
    /// Declares a path with no source of its own and returns where to publish into it.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="PublishAsync"/>, for the streams the edge server cannot fetch
    /// for itself. A camera it can pull; a camera whose codec has to be converted first it cannot,
    /// so this process runs the transcoder and pushes the result in. Also idempotent.
    /// </remarks>
    /// <returns>The RTSP URL a publisher should push to.</returns>
    Task<Uri> ReservePublishPathAsync(string path, CancellationToken cancellationToken = default);

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
/// Produces a stream of whole compressed frames from an address, for the modes that deliver video
/// over a socket rather than through the edge server.
/// </summary>
/// <remarks>
/// A port because it spawns and owns a child FFmpeg process. Distinct from <see cref="IMediaServer"/>
/// on purpose: that hands a stream to MediaMTX and steps out of the media path entirely, whereas
/// this one keeps the bytes flowing through this process so they can be framed and fanned out.
/// </remarks>
public interface IFramePipeline
{
    /// <summary>
    /// Starts the pipeline and waits until it has seen enough of the stream to describe it.
    /// </summary>
    /// <remarks>
    /// It returns only once parameter sets have arrived, because the RFC 6381 codec string is
    /// derived from them and a client cannot configure a decoder without it. Frames read while
    /// waiting are buffered, not discarded.
    /// </remarks>
    /// <exception cref="MediaInspectionException">
    /// The stream could not be started or could not be described. Always safe to show a user.
    /// </exception>
    Task<IFrameStream> StartAsync(
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        CancellationToken cancellationToken = default);
}

/// <summary>A running pipeline: how to configure a decoder for it, and its frames.</summary>
public interface IFrameStream : IAsyncDisposable
{
    /// <summary>What a client needs to configure a decoder. Known before the first frame is served.</summary>
    StreamInitialisation Initialisation { get; }

    /// <summary>
    /// The child process doing the work, or <c>null</c> if it has already gone.
    /// </summary>
    /// <remarks>
    /// Exposed so that what this broadcast costs can be measured, which is the comparison the
    /// project exists to make. An id rather than the <c>Process</c> itself: the metrics reader has
    /// no business holding a handle it might dispose out from under the pipeline that owns it.
    /// </remarks>
    int? ProcessId { get; }

    /// <summary>
    /// Every frame, in order, starting with any buffered during <see cref="IFramePipeline.StartAsync"/>.
    /// </summary>
    /// <remarks>Enumerable once. The broadcaster is the only caller, and it fans out to viewers.</remarks>
    IAsyncEnumerable<EncodedFrame> FramesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts a stream and pushes the result into the edge server, for the WebRTC modes that cannot
/// simply pass bytes through.
/// </summary>
/// <remarks>
/// A separate port from <see cref="IFramePipeline"/> even though both run FFmpeg, because what
/// they promise differs. That one yields frames into this process and describes the stream well
/// enough to configure a decoder; this one hands the video to something else entirely and promises
/// only that video is flowing. Sharing an interface would mean one of the two returning members
/// its callers must know not to use.
/// </remarks>
public interface IPublishingPipeline
{
    /// <summary>
    /// Starts converting, and returns once video is actually reaching
    /// <paramref name="target"/> - not merely once the process has been launched.
    /// </summary>
    /// <remarks>
    /// The distinction matters: a viewer told to connect to a path that no publisher ever reached
    /// sees an indefinite black screen with no error anywhere. Waiting for the first frame turns
    /// that into a failure that can be reported.
    /// </remarks>
    /// <exception cref="MediaInspectionException">
    /// The source could not be read, or no engine on this machine could encode it. Always safe to
    /// show a user.
    /// </exception>
    Task<IPublishedStream> StartAsync(
        MediaAddress address,
        BroadcastPlan plan,
        VideoFormat format,
        Uri target,
        CancellationToken cancellationToken = default);
}

/// <summary>A conversion currently feeding the edge server.</summary>
public interface IPublishedStream : IAsyncDisposable
{
    /// <summary>The transcoder's process id, or <c>null</c> once it has exited.</summary>
    int? ProcessId { get; }

    /// <summary>
    /// The engine that is actually doing the work, which is not necessarily the one the plan
    /// asked for - see <c>EncoderFallbackLog</c>.
    /// </summary>
    AccelerationProfile Acceleration { get; }

    /// <summary>
    /// Whether the transcoder has stopped: the file reached its end, or the process died.
    /// </summary>
    /// <remarks>
    /// Read by the sweep. Without it a dead transcoder leaves a broadcast registered and playable
    /// on paper, and every later viewer joins a path with nothing publishing to it.
    /// </remarks>
    bool HasEnded { get; }
}

/// <summary>
/// Reads what the whole machine is doing - CPU and memory.
/// </summary>
/// <remarks>
/// A port because the answer comes from the operating system by a different route on each one:
/// <c>GetSystemTimes</c> on Windows, <c>/proc</c> on Linux. Stateful by nature - CPU is a rate, so
/// a reading is the difference between two samples and the first one has nothing to subtract from.
/// </remarks>
public interface ISystemMetricsReader
{
    /// <summary>
    /// Takes a reading. Never throws: a machine whose counters cannot be read still has to serve
    /// video, and the snapshot says so rather than failing the request that asked for it.
    /// </summary>
    Task<SystemUtilisation> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads GPU utilisation, split by engine.
/// </summary>
/// <remarks>
/// A port for two reasons rather than one: it shells out to a vendor tool, and there is a real
/// second implementation - the null object used on every machine without an NVIDIA card, which
/// reports unavailability <em>with its reason</em> instead of zeroes.
/// </remarks>
public interface IGpuMetricsReader
{
    /// <summary>
    /// Takes a reading. Never throws - an unreadable GPU is reported as unavailable, because the
    /// health of the metrics feed must not depend on the health of the thing it measures.
    /// </summary>
    Task<GpuUtilisation> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads what individual processes are costing.
/// </summary>
/// <remarks>
/// Stateful like <see cref="ISystemMetricsReader"/>, and for the same reason: CPU is processor time
/// divided by wall time, so it exists only between two readings. Processes that have gone away are
/// forgotten rather than reported as idle.
/// </remarks>
public interface IProcessMetricsReader
{
    /// <summary>
    /// Reads every process id given, in one pass. Ids that no longer exist are absent from the
    /// result rather than present with zeroes.
    /// </summary>
    IReadOnlyDictionary<int, ProcessUtilisation> Read(IReadOnlyCollection<int> processIds);
}

/// <summary>
/// Turns a host name into the addresses it points at.
/// </summary>
/// <remarks>
/// A port because DNS is a network boundary, and because the address policy is far easier to test
/// against a resolver that answers however the test needs than against whatever the machine running
/// the suite happens to resolve.
/// </remarks>
public interface IAddressResolver
{
    /// <summary>Every address the host resolves to. Empty when it resolves to nothing.</summary>
    Task<IReadOnlyList<System.Net.IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default);
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
