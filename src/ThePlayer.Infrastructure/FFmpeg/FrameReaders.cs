using System.Runtime.CompilerServices;
using ThePlayer.Domain.Media;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>One whole picture as it came off the wire, before a timestamp is attached to it.</summary>
/// <param name="Payload">The complete access unit, start codes included.</param>
/// <param name="IsKeyframe">Whether a decoder can start here.</param>
/// <param name="SequenceParameterSet">
/// The SPS NAL carried by this access unit, without its start code, or <c>null</c> if it carried
/// none. Only the first one matters - it is what the codec string is derived from.
/// </param>
public readonly record struct ReadFrame(byte[] Payload, bool IsKeyframe, byte[]? SequenceParameterSet);

/// <summary>
/// Splits a raw byte stream into whole pictures.
/// </summary>
/// <remarks>
/// An interface because Phase 5 adds a second implementation that splits MJPEG on SOI/EOI markers.
/// Two implementations with a real substitution point is the bar this codebase sets for an
/// abstraction; the pipeline that drives it is parameterised by the reader and the FFmpeg
/// arguments, so a third mode is new arguments plus a new reader and no edit to existing code.
/// </remarks>
public interface IFrameReader
{
    /// <summary>Reads until the source ends. Each element is exactly one whole picture.</summary>
    IAsyncEnumerable<ReadFrame> ReadAsync(Stream source, CancellationToken cancellationToken = default);
}

/// <summary>
/// Splits an Annex-B elementary stream into access units, one per picture.
/// </summary>
/// <remarks>
/// <para>
/// Frame boundaries are found by asking FFmpeg to insert Access Unit Delimiters
/// (<c>h264_metadata=aud=insert</c>) and splitting on those, which makes this a scan for one NAL
/// type rather than a slice-header parser. Against a 750-frame file that produced exactly 750
/// access units.
/// </para>
/// <para>
/// <b>Both start-code lengths must be accepted.</b> In the same stream the AUD arrives with a
/// four-byte start code (<c>00 00 00 01</c>) while every other NAL uses three (<c>00 00 01</c>).
/// A reader that scans only for four-byte codes finds the delimiters and misses the parameter sets
/// and every slice.
/// </para>
/// </remarks>
public sealed class CompressedFrameReader : IFrameReader
{
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>
    /// A single picture larger than this means the stream is not what we think it is - most likely
    /// no delimiters were inserted, so the whole thing is accumulating as one access unit. Failing
    /// is far kinder than growing a buffer until the process dies.
    /// </summary>
    private const int MaxAccessUnitBytes = 32 * 1024 * 1024;

    private readonly VideoCodec _codec;

    public CompressedFrameReader(VideoCodec codec)
    {
        if (codec is not (VideoCodec.H264 or VideoCodec.H265))
        {
            throw new ArgumentOutOfRangeException(
                nameof(codec),
                codec,
                "Annex-B framing applies to H.264 and H.265 only.");
        }

        _codec = codec;
    }

    public async IAsyncEnumerable<ReadFrame> ReadAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[ReadChunkBytes * 2];
        var length = 0;

        // Offsets of start codes found in buffer[0..length], oldest first. A NAL runs from one
        // start code to the next, so a NAL is complete only once the following code is known.
        var starts = new List<StartCode>();
        var scanned = 0;

        var accessUnit = new MemoryStream();
        var state = new AccessUnitState();

        while (true)
        {
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = await source.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
            FindStartCodes(buffer, ref scanned, length, starts);

            // Every NAL except the last one found is complete.
            while (starts.Count >= 2)
            {
                var frame = Consume(buffer, starts[0], starts[1].Offset, accessUnit, state);
                starts.RemoveAt(0);

                if (frame is { } complete)
                {
                    yield return complete;
                }
            }

            Compact(buffer, ref length, ref scanned, starts);
        }

        // End of stream: the final NAL runs to the end of what we have.
        if (starts.Count > 0)
        {
            var frame = Consume(buffer, starts[0], length, accessUnit, state);
            if (frame is { } complete)
            {
                yield return complete;
            }
        }

        // Only if it actually held a picture. A stream can end with a trailing delimiter, and
        // emitting that as a frame would hand the client an access unit with nothing to decode.
        if (accessUnit.Length > 0 && state.HasPicture)
        {
            yield return new ReadFrame(accessUnit.ToArray(), state.Keyframe, state.ParameterSet);
        }
    }

    /// <summary>
    /// Adds one NAL to the current access unit, and emits the previous one when a delimiter says
    /// the picture has changed.
    /// </summary>
    private ReadFrame? Consume(
        byte[] buffer,
        StartCode start,
        int end,
        MemoryStream accessUnit,
        AccessUnitState state)
    {
        var payloadStart = start.Offset + start.Length;
        if (payloadStart >= end)
        {
            // A start code with nothing after it. Malformed, but not worth failing over.
            return null;
        }

        var kind = Classify(buffer, payloadStart, end);
        ReadFrame? completed = null;

        // A delimiter closes the picture in hand - but only if there is one. A stream opens with
        // its parameter sets *before* the first delimiter, and those belong to the keyframe that
        // follows rather than forming a picture of their own. Emitting them separately would both
        // invent a frame with nothing to decode and strip the first real keyframe of the sets a
        // decoder needs to configure itself.
        if (kind == NalKind.AccessUnitDelimiter && state.HasPicture)
        {
            completed = new ReadFrame(accessUnit.ToArray(), state.Keyframe, state.ParameterSet);
            accessUnit.SetLength(0);
            state.Reset();
        }

        if (accessUnit.Length + (end - start.Offset) > MaxAccessUnitBytes)
        {
            throw new InvalidDataException(
                $"A single picture exceeded {MaxAccessUnitBytes / (1024 * 1024)} MB. " +
                "The stream is most likely missing access unit delimiters.");
        }

        accessUnit.Write(buffer, start.Offset, end - start.Offset);

        switch (kind)
        {
            case NalKind.SequenceParameterSet when state.ParameterSet is null:
                state.ParameterSet = buffer[payloadStart..end];
                break;

            case NalKind.Keyframe:
                state.Keyframe = true;
                state.HasPicture = true;
                break;

            case NalKind.Slice:
                state.HasPicture = true;
                break;
        }

        return completed;
    }

    private NalKind Classify(byte[] buffer, int payloadStart, int end)
    {
        if (_codec == VideoCodec.H264)
        {
            // H.264: one header byte, type in the low five bits.
            return (buffer[payloadStart] & 0x1F) switch
            {
                9 => NalKind.AccessUnitDelimiter,
                7 => NalKind.SequenceParameterSet,
                5 => NalKind.Keyframe,

                // 1..4 are the other coded slice types. They carry a picture but cannot be started
                // from, which is exactly the distinction the broadcaster needs.
                >= 1 and <= 4 => NalKind.Slice,
                _ => NalKind.Other,
            };
        }

        // H.265: two header bytes, type in bits 1..6 of the first.
        if (payloadStart + 1 >= end)
        {
            return NalKind.Other;
        }

        var type = (buffer[payloadStart] >> 1) & 0x3F;

        return type switch
        {
            35 => NalKind.AccessUnitDelimiter,
            33 => NalKind.SequenceParameterSet,

            // 16..23 are the IRAP types - BLA, IDR and CRA. Any of them is a point a decoder can
            // start from, which is all "keyframe" has to mean here.
            >= 16 and <= 23 => NalKind.Keyframe,

            // The rest of 0..31 are the non-IRAP coded slices.
            <= 31 => NalKind.Slice,
            _ => NalKind.Other,
        };
    }

    /// <summary>
    /// Scans for <c>00 00 01</c> and <c>00 00 00 01</c>, recording where each NAL begins.
    /// </summary>
    /// <remarks>
    /// Scanning restarts three bytes back from the end of the previous pass, because a start code
    /// can straddle two reads.
    /// </remarks>
    private static void FindStartCodes(byte[] buffer, ref int scanned, int length, List<StartCode> starts)
    {
        var index = Math.Max(0, scanned - 3);

        while (index + 2 < length)
        {
            if (buffer[index] != 0 || buffer[index + 1] != 0)
            {
                index++;
                continue;
            }

            if (buffer[index + 2] == 1)
            {
                AddIfNew(starts, new StartCode(index, 3));
                index += 3;
                continue;
            }

            // The four-byte form. Checked second because its first three bytes are not themselves
            // a start code.
            if (buffer[index + 2] == 0 && index + 3 < length && buffer[index + 3] == 1)
            {
                AddIfNew(starts, new StartCode(index, 4));
                index += 4;
                continue;
            }

            index++;
        }

        scanned = length;
    }

    /// <summary>
    /// Records a start code, unless it overlaps the one before it.
    /// </summary>
    /// <remarks>
    /// The overlap check is not paranoia. <c>00 00 00 01</c> is a four-byte start code, and its
    /// last three bytes are themselves a valid three-byte one. A single forward pass steps over the
    /// whole thing, but a rescan that resumes <em>inside</em> it finds the phantom - splitting one
    /// NAL into a one-byte fragment and a remainder, and quietly shortening the picture by a byte.
    /// Only a read that happens to land mid-code triggers it, which is why it survives large reads
    /// and appears immediately under small ones.
    /// </remarks>
    private static void AddIfNew(List<StartCode> starts, StartCode candidate)
    {
        if (starts.Count > 0 && candidate.Offset < starts[^1].Offset + starts[^1].Length)
        {
            return;
        }

        starts.Add(candidate);
    }

    /// <summary>Drops consumed bytes from the front of the buffer and rebases every offset.</summary>
    private static void Compact(byte[] buffer, ref int length, ref int scanned, List<StartCode> starts)
    {
        // With no start code in hand nothing has been consumed, so only the bytes that cannot
        // possibly begin one are safe to drop - keeping the last three, since a code can straddle
        // the boundary between two reads.
        var consumedTo = starts.Count > 0 ? starts[0].Offset : Math.Max(0, length - 3);
        if (consumedTo == 0)
        {
            return;
        }

        Buffer.BlockCopy(buffer, consumedTo, buffer, 0, length - consumedTo);
        length -= consumedTo;
        scanned = Math.Max(0, scanned - consumedTo);

        for (var i = 0; i < starts.Count; i++)
        {
            starts[i] = starts[i] with { Offset = starts[i].Offset - consumedTo };
        }
    }

    private readonly record struct StartCode(int Offset, int Length);

    /// <summary>What has been seen so far of the access unit currently being accumulated.</summary>
    private sealed class AccessUnitState
    {
        /// <summary>Whether a coded slice has arrived. Parameter sets alone are not a picture.</summary>
        public bool HasPicture { get; set; }

        public bool Keyframe { get; set; }

        /// <summary>
        /// Reported on the access unit that carried it, then cleared - later frames report null
        /// rather than repeating the same bytes at every keyframe.
        /// </summary>
        public byte[]? ParameterSet { get; set; }

        public void Reset()
        {
            HasPicture = false;
            Keyframe = false;
            ParameterSet = null;
        }
    }

    private enum NalKind
    {
        Other,
        AccessUnitDelimiter,
        SequenceParameterSet,

        /// <summary>A coded slice that a decoder can start from.</summary>
        Keyframe,

        /// <summary>A coded slice that depends on earlier pictures.</summary>
        Slice,
    }
}
