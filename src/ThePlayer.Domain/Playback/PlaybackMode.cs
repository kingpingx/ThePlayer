using ThePlayer.Domain.Media;

namespace ThePlayer.Domain.Playback;

/// <summary>
/// Where decoding happens, ordered by how much codec work the <em>server</em> does.
/// </summary>
/// <remarks>
/// <para>
/// A WebRTC <c>&lt;video&gt;</c> element always decodes in the client, on the GPU. So
/// <see cref="ServerAssisted"/> is not "the client does not decode" - it does. It is called
/// <em>assisted</em> because the server's job there is to convert the stream into something the
/// client's built-in pipeline handles easily. Only <see cref="ServerDecoded"/> genuinely removes
/// video decoding from the client.
/// </para>
/// <para>
/// <see cref="ClientDecoded"/> earns its place by deleting the conversion, not by enabling GPU
/// decoding: WebRTC negotiates codecs in SDP and most browsers refuse H.265 there, so an H.265
/// feed must otherwise be transcoded. Over a WebSocket the transport is codec-agnostic and a
/// capable client gets the original bytes, dropping the server's codec cost to nothing.
/// </para>
/// </remarks>
public enum PlaybackMode
{
    /// <summary>Server copies bytes; the client decodes the original codec via WebCodecs.</summary>
    ClientDecoded,

    /// <summary>Server converts only if it must; the client decodes easy H.264 via WebRTC.</summary>
    ServerAssisted,

    /// <summary>Server decodes fully and sends pictures; the client decodes no video at all.</summary>
    ServerDecoded,
}

/// <summary>
/// Whether a client can decode one codec, and whether it would do so in hardware.
/// </summary>
/// <param name="Codec">The codec asked about.</param>
/// <param name="Supported">Whether the client can decode it at all.</param>
/// <param name="HardwareAccelerated">
/// Whether a hardware decoder is available. Probed separately from support, because a
/// software-only decoder is <em>supported</em> but a poor choice for a 1080p feed - and the
/// difference is worth telling the user about rather than silently accepting.
/// </param>
public sealed record CodecSupport(VideoCodec Codec, bool Supported, bool HardwareAccelerated);

/// <summary>
/// What a client reported it can decode, from probing <c>VideoDecoder.isConfigSupported()</c>.
/// </summary>
/// <remarks>
/// This is what makes conversion a negotiated outcome rather than a fixed policy: the same H.265
/// camera is passed through untouched to a client that can decode it, and converted for one that
/// cannot.
/// </remarks>
public sealed record ClientDecodeSupport(bool WebCodecsAvailable, IReadOnlyList<CodecSupport> Codecs)
{
    /// <summary>A client that reported nothing - an old browser, or a request that omitted the probe.</summary>
    public static ClientDecodeSupport None { get; } = new(WebCodecsAvailable: false, []);

    public bool CanDecode(VideoCodec codec) =>
        WebCodecsAvailable && Codecs.Any(entry => entry.Codec == codec && entry.Supported);

    public bool CanDecodeInHardware(VideoCodec codec) =>
        WebCodecsAvailable &&
        Codecs.Any(entry => entry.Codec == codec && entry.Supported && entry.HardwareAccelerated);
}

/// <summary>
/// Whether a mode can be offered for a particular stream, and if not, why not.
/// </summary>
/// <remarks>
/// The reason is the point. A greyed-out control with no explanation is worse than no control, so
/// every unavailable mode carries a sentence the UI can show as-is.
/// </remarks>
/// <param name="Mode">The mode described.</param>
/// <param name="Available">Whether it can be used for this stream on this client.</param>
/// <param name="Reason">Why not, in plain language. Null when available.</param>
public sealed record ModeAvailability(PlaybackMode Mode, bool Available, string? Reason)
{
    public static ModeAvailability Yes(PlaybackMode mode) => new(mode, Available: true, Reason: null);

    public static ModeAvailability No(PlaybackMode mode, string reason) => new(mode, Available: false, reason);
}
