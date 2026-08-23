using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Domain.Media;

namespace ThePlayer.Infrastructure.FFmpeg;

/// <summary>
/// Finds out what is at an address by running ffprobe against it.
/// </summary>
/// <remarks>
/// Separate from <see cref="FFmpegHardwareInspector"/> on purpose: that one asks about the
/// <em>machine</em>, this one asks about a <em>stream</em>. A test needing a fake stream should not
/// also have to describe an imaginary GPU.
/// </remarks>
public sealed class FFmpegMediaInspector(
    FFmpegCommandRunner runner,
    IOptions<FFmpegOptions> options,
    ILogger<FFmpegMediaInspector> logger) : IMediaInspector
{
    private readonly FFmpegOptions _options = options.Value;

    public async Task<VideoFormat> InspectAsync(
        MediaAddress address,
        CancellationToken cancellationToken = default)
    {
        // A file that is not there produces a far clearer message here than it would after being
        // handed to ffprobe, and costs nothing to check.
        if (address.Kind == MediaAddressKind.File && !File.Exists(address.ToFFmpegInput()))
        {
            throw new MediaInspectionException($"No file at {address.Display}.");
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(
                runner.FFprobePath,
                BuildArguments(address),
                scrubWith: address,
                cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            // An unreachable host that never refuses the connection. Translating it here keeps the
            // failure inside the one exception type callers handle, rather than surfacing an
            // infrastructure exception - and a stack trace - from an HTTP endpoint.
            throw new MediaInspectionException($"{address.Display} did not respond.");
        }
        catch (ExternalToolNotFoundException ex)
        {
            throw new MediaInspectionException(ex.Message, ex);
        }

        if (!result.Succeeded)
        {
            // Already scrubbed by the runner, so this is safe to surface. Which matters: the most
            // common cause is a wrong password, and the most natural thing to print is the URL.
            throw new MediaInspectionException(Explain(address, result.StandardError));
        }

        var format = Parse(result.StandardOutput, address);
        logger.LogInformation("{Address} is {Format}.", address, format);
        return format;
    }

    private string BuildArguments(MediaAddress address)
    {
        var options = string.Empty;

        if (address.Kind == MediaAddressKind.Rtsp)
        {
            // -rtsp_transport tcp: cameras on wifi drop UDP packets, and a half-probed stream
            //   reports nonsense. TCP is slower to set up and far likelier to give a right answer.
            //
            // -timeout: without it, a host that silently drops packets - a typo in the IP, the
            //   usual case - hangs until the outer command timeout. Note that -rw_timeout, the
            //   option that looks like it should do this, is ignored by the RTSP demuxer.
            //   The value is in microseconds.
            var timeoutMicroseconds = (long)_options.RtspConnectTimeout.TotalMilliseconds * 1000;
            options = $"-rtsp_transport tcp -timeout {timeoutMicroseconds}";
        }

        return $"-v error -print_format json -show_streams -show_format " +
               $"-select_streams v:0 {options} -i \"{address.ToFFmpegInput()}\"";
    }

    private static VideoFormat Parse(string json, MediaAddress address)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            throw new MediaInspectionException($"{address.Display} has no video stream.");
        }

        var stream = streams[0];

        var codec = VideoCodecNames.FromFFprobeName(ReadString(stream, "codec_name"));
        var width = ReadInt(stream, "width");
        var height = ReadInt(stream, "height");

        if (width == 0 || height == 0)
        {
            throw new MediaInspectionException($"{address.Display} reported no frame size.");
        }

        return new VideoFormat(
            codec,
            width,
            height,
            ReadFrameRate(stream),
            ReadDuration(root, address));
    }

    /// <summary>
    /// ffprobe reports frame rate as a rational string such as <c>25/1</c> or <c>30000/1001</c>.
    /// </summary>
    /// <remarks>
    /// <c>avg_frame_rate</c> is preferred over <c>r_frame_rate</c>: the latter is the container's
    /// nominal rate, which for a variable-rate camera can be wildly optimistic. Timestamps in
    /// client-decode mode are synthesised from this number, so a wrong one shows up as playback
    /// drifting.
    /// </remarks>
    private static double ReadFrameRate(JsonElement stream)
    {
        foreach (var property in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            var value = ReadString(stream, property);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var parts = value.Split('/');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], out var numerator) ||
                !double.TryParse(parts[1], out var denominator) ||
                denominator == 0 ||
                numerator == 0)
            {
                continue;
            }

            return Math.Round(numerator / denominator, 3);
        }

        // A live stream may genuinely not know yet. 25 is a defensible guess and only affects
        // synthesised timestamps, not whether anything plays.
        return 25;
    }

    /// <summary>
    /// A duration means the media ends; its absence means a live feed. That one distinction is
    /// what separates a camera from a file everywhere downstream.
    /// </summary>
    private static TimeSpan? ReadDuration(JsonElement root, MediaAddress address)
    {
        // RTSP feeds sometimes report a bogus duration. The address kind is the more reliable
        // signal, so a camera is always treated as live.
        if (address.Kind == MediaAddressKind.Rtsp)
        {
            return null;
        }

        if (!root.TryGetProperty("format", out var format))
        {
            return null;
        }

        var duration = ReadString(format, "duration");

        return double.TryParse(duration, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;

    /// <summary>
    /// Turns ffprobe's output into something worth showing a user. The text is already scrubbed;
    /// this only tries to name the likely cause rather than printing a stack of codec noise.
    /// </summary>
    private static string Explain(MediaAddress address, string standardError)
    {
        if (Mentions(standardError, "401", "Unauthorized"))
        {
            return $"{address.Display} rejected the credentials.";
        }

        if (Mentions(standardError, "Connection refused", "No route to host", "Network is unreachable"))
        {
            return $"Could not reach {address.Display}.";
        }

        // -138 is FFmpeg's ETIMEDOUT. It surfaces for a host that accepts nothing and refuses
        // nothing - a typo in the camera's IP, which is the single most common way this fails -
        // and "Error number -138 occurred" tells a user nothing at all.
        if (Mentions(standardError, "timed out", "timeout", "Error number -138"))
        {
            return $"{address.Display} did not respond.";
        }

        if (Mentions(standardError, "Invalid data found", "could not find codec"))
        {
            return $"{address.Display} is not a video this server can read.";
        }

        return $"Could not read {address.Display}: {LastMeaningfulLine(standardError, address)}";
    }

    private static bool Mentions(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The last line of ffprobe's output, with the leading "&lt;address&gt;: " prefix removed.
    /// </summary>
    /// <remarks>
    /// ffprobe prefixes its final error with the input URL, and the caller has already named the
    /// address - so without this the message reads "Could not read X: X: reason".
    /// </remarks>
    private static string LastMeaningfulLine(string standardError, MediaAddress address)
    {
        var line = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => candidate.Length > 0);

        if (line is null)
        {
            return "ffprobe gave no reason.";
        }

        // The URL in ffprobe's output has already been scrubbed, so match against the scrubbed
        // form rather than the original.
        var scrubbedInput = address.Scrub(address.ToFFmpegInput());
        return line.StartsWith($"{scrubbedInput}: ", StringComparison.Ordinal)
            ? line[(scrubbedInput.Length + 2)..]
            : line;
    }
}
