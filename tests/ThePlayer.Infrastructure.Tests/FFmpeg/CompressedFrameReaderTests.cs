using ThePlayer.Domain.Media;
using ThePlayer.Infrastructure.FFmpeg;
using ThePlayer.TestSupport;

namespace ThePlayer.Infrastructure.Tests.FFmpeg;

/// <summary>
/// Splitting an elementary stream into whole pictures, against streams FFmpeg actually produced.
/// </summary>
/// <remarks>
/// The count is the assertion that matters. A reader that is off by one picture, or that merges two,
/// still produces plausible-looking output - it just decodes to a mess in the browser, a long way
/// from here.
/// </remarks>
public class CompressedFrameReaderTests(GeneratedMedia media) : IClassFixture<GeneratedMedia>
{
    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public async Task Every_access_unit_is_one_picture(VideoCodec codec)
    {
        var frames = await ReadAllAsync(codec);

        frames.Should().HaveCount(
            GeneratedMedia.ExpectedFrames,
            "one access unit delimiter is inserted per picture");
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public async Task Keyframes_are_found_where_the_encoder_put_them(VideoCodec codec)
    {
        var frames = await ReadAllAsync(codec);

        frames.Count(frame => frame.IsKeyframe).Should().Be(
            GeneratedMedia.ExpectedKeyframes,
            "the clip is ten seconds at one keyframe per second");

        frames[0].IsKeyframe.Should().BeTrue("a stream has to start somewhere a decoder can start");
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public async Task Parameter_sets_arrive_with_the_first_keyframe(VideoCodec codec)
    {
        var frames = await ReadAllAsync(codec);

        frames[0].SequenceParameterSet.Should().NotBeNull(
            "dump_extra puts SPS and PPS in front of every keyframe, which is what makes a late " +
            "joiner able to configure a decoder");

        // The whole point of capturing it: it has to be readable by the thing that consumes it.
        var act = () => CodecStringBuilder.Build(codec, frames[0].SequenceParameterSet!);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public async Task Every_frame_starts_with_a_start_code(VideoCodec codec)
    {
        var frames = await ReadAllAsync(codec);

        frames.Should().AllSatisfy(frame =>
        {
            // Annex-B, because that is the form WebCodecs expects when the config carries no
            // description - and this project deliberately sends none.
            var head = frame.Payload.AsSpan(0, 4);
            var threeByte = head[0] == 0 && head[1] == 0 && head[2] == 1;
            var fourByte = head[0] == 0 && head[1] == 0 && head[2] == 0 && head[3] == 1;

            (threeByte || fourByte).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Both_start_code_lengths_are_handled()
    {
        // Not a hypothetical. In one stream the delimiter uses a four-byte start code while the
        // slices use three, so a reader that scans for only one length finds the delimiters and
        // misses every parameter set - or the other way round.
        var bytes = await File.ReadAllBytesAsync(media.H264AnnexB);

        CountStartCodes(bytes, 3).Should().BeGreaterThan(0);
        CountStartCodes(bytes, 4).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_stream_split_across_awkward_reads_produces_the_same_frames()
    {
        // The scanner rescans three bytes back on each pass because a start code can straddle two
        // reads. A pathologically small chunk size is how that gets exercised.
        var whole = await ReadAllAsync(VideoCodec.H264);
        var dribbled = await ReadAllAsync(VideoCodec.H264, chunkSize: 7);

        dribbled.Should().HaveCount(whole.Count);
        dribbled.Select(f => f.Payload.Length).Should().Equal(whole.Select(f => f.Payload.Length));
    }

    [Fact]
    public void A_codec_without_Annex_B_framing_is_refused()
    {
        var act = () => new CompressedFrameReader(VideoCodec.Mjpeg);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private async Task<List<ReadFrame>> ReadAllAsync(VideoCodec codec, int? chunkSize = null)
    {
        var path = codec == VideoCodec.H264 ? media.H264AnnexB : media.H265AnnexB;

        await using var file = File.OpenRead(path);
        Stream source = chunkSize is { } size ? new DribblingStream(file, size) : file;

        var frames = new List<ReadFrame>();

        await foreach (var frame in new CompressedFrameReader(codec).ReadAsync(source))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static int CountStartCodes(byte[] data, int length)
    {
        var count = 0;

        for (var i = 0; i + length <= data.Length; i++)
        {
            var isThree = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1;
            var isFour = i + 3 < data.Length &&
                         data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1;

            if (length == 3 && isThree)
            {
                count++;
            }
            else if (length == 4 && isFour)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Hands back a few bytes at a time, however much was asked for.</summary>
    private sealed class DribblingStream(Stream inner, int chunkSize) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, chunkSize));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
