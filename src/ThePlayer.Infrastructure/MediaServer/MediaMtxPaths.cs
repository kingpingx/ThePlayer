using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThePlayer.Application;
using ThePlayer.Domain.Media;
using ThePlayer.Infrastructure.FFmpeg;

namespace ThePlayer.Infrastructure.MediaServer;

/// <summary>
/// Publishes streams on MediaMTX over its control API.
/// </summary>
/// <remarks>
/// <para>
/// Registration happens over the API rather than by writing paths into <c>mediamtx.yml</c>,
/// specifically so that camera credentials stay in MediaMTX's memory instead of landing on disk in
/// a file that might be committed or baked into a container image.
/// </para>
/// <para>
/// Cameras and files are published differently. MediaMTX can pull an RTSP source itself, but it
/// cannot read a file, so a file is published by having MediaMTX run FFmpeg on demand to push it
/// in. Both are on-demand: nothing connects to the camera, and no FFmpeg starts, until a viewer
/// actually asks.
/// </para>
/// </remarks>
public sealed class MediaMtxPaths(
    IHttpClientFactory httpClientFactory,
    IOptions<MediaMtxOptions> mediaMtxOptions,
    IOptions<FFmpegOptions> ffmpegOptions,
    ILogger<MediaMtxPaths> logger) : IMediaServer
{
    private readonly MediaMtxOptions _options = mediaMtxOptions.Value;
    private readonly FFmpegOptions _ffmpeg = ffmpegOptions.Value;

    public async Task PublishAsync(
        string path,
        MediaAddress address,
        CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(nameof(MediaMtxPaths));

        // Remove first so the call is idempotent. A path can survive a crash of this process, and
        // "add" against an existing name is an error rather than an update.
        await RemoveAsync(path, cancellationToken);

        var configuration = BuildPathConfiguration(address);

        using var response = await client.PostAsJsonAsync(
            $"{_options.ApiBaseUrl}/v3/config/paths/add/{path}",
            configuration,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // The body echoes the configuration we just sent, which for a camera contains the
            // password. Scrub before it reaches a log or an exception message.
            throw new MediaServerException(
                $"MediaMTX refused to publish '{path}': {response.StatusCode}. {address.Scrub(body)}");
        }

        logger.LogDebug("Published {Address} on MediaMTX path '{Path}'.", address, path);
    }

    public async Task<Uri> ReservePublishPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(nameof(MediaMtxPaths));

        // Same idempotence as PublishAsync, and for a second reason here: a path left over from a
        // previous run may still be configured to pull a source, which would make it refuse the
        // publisher we are about to point at it.
        await RemoveAsync(path, cancellationToken);

        // An empty configuration is a path with no source of its own. MediaMTX defaults it to
        // "publisher", meaning it waits for someone to push - which is exactly what the transcoder
        // is about to do.
        using var response = await client.PostAsJsonAsync(
            $"{_options.ApiBaseUrl}/v3/config/paths/add/{path}",
            new { },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            throw new MediaServerException(
                $"MediaMTX refused to reserve '{path}' for publishing: {response.StatusCode}. {body}");
        }

        var target = new Uri($"{_options.RtspBaseUrl}/{path}");
        logger.LogDebug("Reserved MediaMTX path '{Path}' for publishing at {Target}.", path, target);

        return target;
    }

    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(MediaMtxPaths));

            using var response = await client.DeleteAsync(
                $"{_options.ApiBaseUrl}/v3/config/paths/delete/{path}",
                cancellationToken);

            // Not found is the normal case for a path that was never there.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                logger.LogDebug(
                    "MediaMTX returned {Status} removing path '{Path}'.",
                    response.StatusCode,
                    path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Removal is cleanup. Failing it should never fail the operation that triggered it.
            logger.LogDebug(ex, "Could not remove MediaMTX path '{Path}'.", path);
        }
    }

    public Uri WhepUrlFor(string path) => new($"{_options.WebRtcBaseUrl}/{path}/whep");

    /// <summary>
    /// Builds the path configuration MediaMTX expects, which differs by address kind.
    /// </summary>
    private object BuildPathConfiguration(MediaAddress address)
    {
        if (address.Kind == MediaAddressKind.Rtsp)
        {
            return new
            {
                source = address.ToFFmpegInput(),
                sourceOnDemand = true,

                // How long the camera connection is held after the last reader leaves. Kept short
                // because BroadcastCoordinator does the real linger; this is just a backstop.
                sourceOnDemandCloseAfter = "10s",
            };
        }

        return new
        {
            runOnDemand = BuildFilePublishCommand(address),
            runOnDemandRestart = false,
            runOnDemandCloseAfter = "10s",
        };
    }

    /// <summary>
    /// The FFmpeg command MediaMTX runs to push a local file in as if it were a live source.
    /// </summary>
    /// <remarks>
    /// <c>-re</c> paces the file at real time. Without it FFmpeg pushes the whole file as fast as
    /// it can read it, which overruns the server's buffers and plays back at many times speed.
    /// <para>
    /// <c>-c copy</c> means no conversion, which is all Phase 1 promises. Phase 3 replaces this
    /// with a real encoder when the source codec is not one the browser can take.
    /// </para>
    /// <para>
    /// <c>$MTX_PATH</c> and <c>$RTSP_PORT</c> are substituted by MediaMTX, so the command does not
    /// have to know its own path or port.
    /// </para>
    /// </remarks>
    private string BuildFilePublishCommand(MediaAddress address) =>
        $"{_ffmpeg.FFmpegPath} -hide_banner -loglevel error -re -i \"{address.ToFFmpegInput()}\" " +
        $"-c copy -f rtsp rtsp://127.0.0.1:$RTSP_PORT/$MTX_PATH";
}

/// <summary>Raised when the edge server refuses an operation. Messages are always scrubbed.</summary>
public sealed class MediaServerException(string message) : Exception(message);
