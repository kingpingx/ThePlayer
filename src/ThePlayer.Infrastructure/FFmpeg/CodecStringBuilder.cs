using System.Globalization;
using System.Text;
using ThePlayer.Domain.Media;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>
/// Derives the RFC 6381 codec string a browser needs to configure a decoder, from the parameter
/// sets carried in the stream itself.
/// </summary>
/// <remarks>
/// <para>
/// Derived rather than guessed. <c>VideoCodecNames.ToProbeString</c> exists to ask "could this
/// browser decode this family of codec at all?"; this produces the exact profile and level of the
/// stream in hand, which is what <c>VideoDecoder.configure()</c> is actually checked against.
/// </para>
/// <para>
/// Every failure path throws. A wrong level makes <c>configure()</c> reject the stream, and a
/// silently guessed string turns a clear error into a mystery - so a stream whose parameter sets
/// cannot be read fails here, where the reason is still in hand.
/// </para>
/// </remarks>
public static class CodecStringBuilder
{
    /// <summary>
    /// Builds the codec string for a stream from its sequence parameter set.
    /// </summary>
    /// <param name="codec">Which codec the parameter set belongs to.</param>
    /// <param name="sequenceParameterSet">
    /// The SPS NAL including its header byte(s), without a start code.
    /// </param>
    /// <exception cref="InvalidDataException">The parameter set could not be read.</exception>
    public static string Build(VideoCodec codec, ReadOnlySpan<byte> sequenceParameterSet) => codec switch
    {
        VideoCodec.H264 => BuildAvc(sequenceParameterSet),
        VideoCodec.H265 => BuildHevc(sequenceParameterSet),
        _ => throw new InvalidDataException($"No codec string is defined for {codec}."),
    };

    /// <summary>
    /// H.264 is three bytes. The SPS NAL header is one byte, and <c>profile_idc</c>,
    /// <c>constraint_flags</c> and <c>level_idc</c> follow it immediately.
    /// </summary>
    /// <remarks>SPS payload <c>42 c0 1f</c> becomes <c>avc1.42c01f</c>.</remarks>
    private static string BuildAvc(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 4)
        {
            throw new InvalidDataException(
                $"An H.264 sequence parameter set needs at least 4 bytes; got {sps.Length}.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"avc1.{sps[1]:x2}{sps[2]:x2}{sps[3]:x2}");
    }

    /// <summary>
    /// H.265 is genuinely harder: <c>profile_tier_level</c> sits inside the SPS behind a two-byte
    /// NAL header and eight bits of preamble, and its 96 bits have to be read a bit at a time.
    /// </summary>
    /// <remarks>
    /// Two complications, both easy to miss. Emulation-prevention bytes must be stripped from the
    /// RBSP before anything is read, and the 32 compatibility flags are emitted in <em>reverse</em>
    /// bit order in the codec string.
    /// </remarks>
    private static string BuildHevc(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 3)
        {
            throw new InvalidDataException(
                $"An H.265 sequence parameter set needs at least 3 bytes; got {sps.Length}.");
        }

        // Skip the two-byte NAL header, then undo the emulation prevention that protects the RBSP.
        var rbsp = RemoveEmulationPrevention(sps[2..]);
        var reader = new BitReader(rbsp);

        try
        {
            reader.Skip(4);                             // sps_video_parameter_set_id
            reader.Skip(3);                             // sps_max_sub_layers_minus1
            reader.Skip(1);                             // sps_temporal_id_nesting_flag

            var profileSpace = reader.Read(2);
            var tierFlag = reader.Read(1);
            var profileIdc = reader.Read(5);
            var compatibilityFlags = reader.ReadUInt32();

            // 48 bits: the four source/packing flags, 43 reserved, and one more.
            var constraints = new byte[6];
            for (var i = 0; i < constraints.Length; i++)
            {
                constraints[i] = (byte)reader.Read(8);
            }

            var levelIdc = reader.Read(8);

            return Format(profileSpace, tierFlag, profileIdc, compatibilityFlags, constraints, levelIdc);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                "The H.265 sequence parameter set ended before profile_tier_level was complete.",
                ex);
        }
    }

    private static string Format(
        uint profileSpace,
        uint tierFlag,
        uint profileIdc,
        uint compatibilityFlags,
        byte[] constraints,
        uint levelIdc)
    {
        var text = new StringBuilder("hev1.");

        // A non-zero profile space is spelled as a letter: 1 -> A, 2 -> B, 3 -> C.
        if (profileSpace > 0)
        {
            text.Append((char)('A' + profileSpace - 1));
        }

        text.Append(profileIdc.ToString(CultureInfo.InvariantCulture));
        text.Append('.');
        text.Append(ReverseBits(compatibilityFlags).ToString("x", CultureInfo.InvariantCulture));
        text.Append('.');
        text.Append(tierFlag == 0 ? 'L' : 'H');
        text.Append(levelIdc.ToString(CultureInfo.InvariantCulture));

        // Trailing zero constraint bytes are omitted, so Main profile ends "...L93.B0" rather than
        // carrying five redundant ".00" groups.
        var last = constraints.Length - 1;
        while (last >= 0 && constraints[last] == 0)
        {
            last--;
        }

        for (var i = 0; i <= last; i++)
        {
            text.Append('.');
            text.Append(constraints[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// Strips the <c>0x03</c> bytes inserted after any <c>00 00</c> pair so that a payload can
    /// never contain something that looks like a start code.
    /// </summary>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> source)
    {
        var output = new byte[source.Length];
        var written = 0;
        var zeroes = 0;

        foreach (var value in source)
        {
            if (zeroes >= 2 && value == 0x03)
            {
                zeroes = 0;
                continue;
            }

            output[written++] = value;
            zeroes = value == 0 ? zeroes + 1 : 0;
        }

        return output[..written];
    }

    /// <summary>
    /// Reverses all 32 bits. The compatibility flags are written most-significant-first in the
    /// bitstream but least-significant-first in the codec string, so Main profile's
    /// <c>0x60000000</c> becomes <c>6</c>.
    /// </summary>
    private static uint ReverseBits(uint value)
    {
        uint reversed = 0;

        for (var i = 0; i < 32; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }

    /// <summary>
    /// Reads big-endian bit fields out of an RBSP. Deliberately minimal - the only fields this
    /// codebase needs are fixed-width, so there is no exp-Golomb decoding here.
    /// </summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public void Skip(int count) => _bit += count;

        public uint Read(int count)
        {
            uint value = 0;

            for (var i = 0; i < count; i++)
            {
                var index = _bit >> 3;
                if (index >= _data.Length)
                {
                    throw new InvalidDataException("Ran past the end of the bitstream.");
                }

                var bit = (_data[index] >> (7 - (_bit & 7))) & 1;
                value = (value << 1) | (uint)bit;
                _bit++;
            }

            return value;
        }

        /// <summary>Reads 32 bits, which <see cref="Read"/> cannot return without overflowing.</summary>
        public uint ReadUInt32() => (Read(16) << 16) | Read(16);
    }
}
