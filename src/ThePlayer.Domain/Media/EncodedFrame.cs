namespace ThePlayer.Domain.Media;

/// <summary>
/// One complete compressed picture, ready to hand to a client decoder.
/// </summary>
/// <remarks>
/// <para>
/// A frame is never partial and never two: for H.264 and H.265 this is exactly one access unit in
/// Annex-B form, start codes included. Annex-B is what <c>VideoDecoder</c> expects when its config
/// carries no <c>description</c>, which is why parameter sets are inlined into the stream rather
/// than passed out of band - see <c>CompressedFrameReader</c>.
/// </para>
/// <para>
/// <see cref="Payload"/> is a <see cref="ReadOnlyMemory{T}"/> rather than an array because one
/// frame is fanned out to every viewer of a broadcast at once. Nothing may mutate it.
/// </para>
/// </remarks>
/// <param name="Payload">The access unit, in Annex-B form.</param>
/// <param name="IsKeyframe">
/// Whether a decoder can start here. A client that has fallen behind waits for the next one rather
/// than resuming from a broken reference chain.
/// </param>
/// <param name="TimestampMicroseconds">
/// Presentation time. Synthesised from the inspected frame rate - raw elementary-stream output
/// carries no timestamps of its own. See <c>docs/PROTOCOL.md</c> for the upgrade path.
/// </param>
public sealed record EncodedFrame(
    ReadOnlyMemory<byte> Payload,
    bool IsKeyframe,
    long TimestampMicroseconds)
{
    public int Length => Payload.Length;
}

/// <summary>
/// Everything a client needs before it can configure a decoder - the first message on a frame
/// socket.
/// </summary>
/// <param name="Codec">
/// An RFC 6381 codec string such as <c>avc1.42c01f</c> or <c>hev1.1.6.L93.B0</c>, derived from the
/// parameter sets in the stream itself rather than guessed from the codec name.
/// </param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="FrameRate">Frames per second, as inspected.</param>
public sealed record StreamInitialisation(
    string Codec,
    int Width,
    int Height,
    double FrameRate);
