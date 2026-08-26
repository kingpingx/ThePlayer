using ThePlayer.Infrastructure.FFmpeg;
using ThePlayer.TestSupport;

namespace ThePlayer.Infrastructure.Tests.FFmpeg;

/// <summary>
/// Splitting an MJPEG stream into whole images, against a stream FFmpeg actually produced.
/// </summary>
/// <remarks>
/// The count is the assertion that matters, exactly as it is for the compressed reader. A reader
/// that merges two images or truncates one still produces plausible-looking output, and the symptom
/// turns up in a browser a long way from here - usually as a picture whose lower half is grey.
/// </remarks>
public class JpegPictureReaderTests(GeneratedMedia media) : IClassFixture<GeneratedMedia>
{
    private const byte Marker = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;

    [Fact]
    public async Task Every_image_is_one_picture()
    {
        var frames = await ReadAllAsync();

        frames.Should().HaveCount(
            GeneratedMedia.ExpectedFrames,
            "the clip is ten seconds at 25fps and MJPEG encodes every frame");
    }

    [Fact]
    public async Task Each_picture_is_a_complete_JPEG()
    {
        // The bug this catches is a reader that finds boundaries by scanning for FF D9 without
        // tracking whether it is inside a scan, where those bytes can occur as data.
        var frames = await ReadAllAsync();

        foreach (var frame in frames)
        {
            frame.Payload.Should().HaveCountGreaterThan(4);

            frame.Payload[0].Should().Be(Marker);
            frame.Payload[1].Should().Be(StartOfImage, "every picture opens with SOI");

            frame.Payload[^2].Should().Be(Marker);
            frame.Payload[^1].Should().Be(EndOfImage, "and closes with EOI, with nothing after it");
        }
    }

    [Fact]
    public async Task No_bytes_are_lost_between_pictures()
    {
        // Concatenating what came out must reproduce what went in. An off-by-one that drops the
        // last byte of every image would still pass the marker checks above.
        var frames = await ReadAllAsync();
        var rejoined = frames.SelectMany(frame => frame.Payload).ToArray();

        rejoined.Should().Equal(await File.ReadAllBytesAsync(media.Mjpeg));
    }

    [Fact]
    public async Task Every_picture_is_independently_decodable()
    {
        // Not a detail: it is why this mode needs no keyframe wait, why a late joiner can start
        // anywhere, and why dropping a frame costs exactly that frame.
        var frames = await ReadAllAsync();

        frames.Should().OnlyContain(frame => frame.IsKeyframe);
        frames.Should().OnlyContain(frame => frame.SequenceParameterSet == null);
    }

    [Fact]
    public async Task The_first_picture_describes_the_stream()
    {
        var reader = new JpegPictureReader();
        var frames = await ReadAllAsync();

        reader.Describe(frames[0]).Should().Be("mjpeg");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task The_split_does_not_depend_on_how_the_bytes_arrive(int chunkSize)
    {
        // A marker can straddle a read boundary, and a reader that scans each chunk in isolation
        // misses those - rarely enough to look like a flaky decoder rather than a bug.
        await using var source = new ChunkedStream(await File.ReadAllBytesAsync(media.Mjpeg), chunkSize);

        var frames = new List<ReadFrame>();
        await foreach (var frame in new JpegPictureReader().ReadAsync(source))
        {
            frames.Add(frame);
        }

        frames.Should().HaveCount(GeneratedMedia.ExpectedFrames);
    }

    [Fact]
    public async Task A_stream_that_ends_mid_picture_does_not_yield_a_truncated_one()
    {
        // Half an image is worse than no image: it decodes to something, and what it decodes to
        // looks like a rendering bug rather than a truncated download.
        var whole = await File.ReadAllBytesAsync(media.Mjpeg);
        var complete = await ReadAllAsync();
        var truncated = whole[..(whole.Length - complete[^1].Payload.Length / 2)];

        await using var source = new MemoryStream(truncated);

        var frames = new List<ReadFrame>();
        await foreach (var frame in new JpegPictureReader().ReadAsync(source))
        {
            frames.Add(frame);
        }

        frames.Should().HaveCount(GeneratedMedia.ExpectedFrames - 1);
    }

    [Fact]
    public async Task Nothing_before_the_first_start_marker_is_kept()
    {
        // A client can join a stream mid-picture. Emitting the tail of one as though it were a
        // whole image would hand it something no decoder accepts.
        var whole = await File.ReadAllBytesAsync(media.Mjpeg);
        await using var source = new MemoryStream(whole[64..]);

        var frames = new List<ReadFrame>();
        await foreach (var frame in new JpegPictureReader().ReadAsync(source))
        {
            frames.Add(frame);
        }

        frames.Should().HaveCount(GeneratedMedia.ExpectedFrames - 1, "the opening picture was cut into");
        frames[0].Payload[1].Should().Be(StartOfImage);
    }

    private async Task<List<ReadFrame>> ReadAllAsync()
    {
        await using var source = File.OpenRead(media.Mjpeg);

        var frames = new List<ReadFrame>();
        await foreach (var frame in new JpegPictureReader().ReadAsync(source))
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Hands out a fixed number of bytes per read, to force boundaries anywhere.</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(chunkSize, count), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
