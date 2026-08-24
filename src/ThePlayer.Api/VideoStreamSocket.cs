using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ThePlayer.Application.Broadcasting;
using ThePlayer.Domain.Media;

namespace ThePlayer.Api;

/// <summary>
/// Serves compressed frames to one viewer over a WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// A raw socket rather than WebRTC because the transport has to be codec-agnostic. WebRTC
/// negotiates codecs in SDP and browsers refuse H.265 there, so a feed heading for a
/// <c>&lt;video&gt;</c> element must be transcoded. Here the bytes are opaque: a client that
/// reports it can decode the source gets the original bytes and the server does no codec work
/// at all.
/// </para>
/// <para>
/// The wire format is frozen in <c>docs/PROTOCOL.md</c>: one JSON text message, then one binary
/// message per access unit with a nine-byte header.
/// </para>
/// </remarks>
public static class VideoStreamSocket
{
    /// <summary>Bit 0 of the flags byte.</summary>
    private const byte KeyframeFlag = 0x01;

    private const int HeaderBytes = 9;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Handles <c>GET /ws/frames/{viewerId}</c>, upgrading to a WebSocket and streaming until the
    /// client leaves or the broadcast ends.
    /// </summary>
    public static async Task HandleAsync(
        HttpContext context,
        string viewerId,
        BroadcastCoordinator coordinator,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(VideoStreamSocket));

        if (!context.WebSockets.IsWebSocketRequest)
        {
            // Someone browsed to the URL. Say so plainly rather than returning an opaque 400.
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsync("This endpoint serves video frames over a WebSocket.", cancellationToken);
            return;
        }

        // Resolved before the upgrade, so an unknown viewer gets an HTTP status it can act on
        // rather than a socket that opens and immediately closes.
        var subscription = await coordinator.SubscribeAsync(viewerId, cancellationToken);
        if (subscription is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(
                "No frame stream for that viewer. Call POST /api/watch first.",
                cancellationToken);
            return;
        }

        try
        {
            using (subscription)
            using (var socket = await context.WebSockets.AcceptWebSocketAsync())
            {
                await StreamAsync(socket, subscription, viewerId, logger, cancellationToken);
            }
        }
        finally
        {
            // The socket closing is the more reliable signal that a viewer has gone. DELETE
            // /api/watch is sent with keepalive and usually lands, but "usually" leaves a viewer
            // pinned to a broadcast that then never empties, never lingers out, and never stops.
            // Detaching is idempotent, so both paths firing is fine.
            await coordinator.DetachAsync(viewerId, CancellationToken.None);
        }
    }

    private static async Task StreamAsync(
        WebSocket socket,
        FrameSubscription subscription,
        string viewerId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A browser that navigates away does not necessarily send a close frame, and a send to a
        // half-open socket can block indefinitely. Watching for the client's close in parallel is
        // what makes the writer notice.
        var watchingForClose = WatchForCloseAsync(socket, closed);

        try
        {
            await SendInitialisationAsync(socket, subscription.Initialisation, closed.Token);

            var buffer = Array.Empty<byte>();
            var sent = 0L;

            await foreach (var frame in subscription.Frames.ReadAllAsync(closed.Token))
            {
                var required = HeaderBytes + frame.Length;
                if (buffer.Length < required)
                {
                    buffer = new byte[Math.Max(required, 64 * 1024)];
                }

                WriteHeader(buffer, frame);
                frame.Payload.Span.CopyTo(buffer.AsSpan(HeaderBytes));

                await socket.SendAsync(
                    buffer.AsMemory(0, required),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    closed.Token);

                sent++;
            }

            logger.LogInformation("Frame socket for viewer {ViewerId} ended after {Count} frames.", viewerId, sent);

            await CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, "Stream ended");
        }
        catch (OperationCanceledException)
        {
            // The client left, or the server is shutting down. Both are ordinary.
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Frame socket for viewer {ViewerId} closed abruptly.", viewerId);
        }
        finally
        {
            await closed.CancelAsync();
            await watchingForClose;
        }
    }

    /// <summary>
    /// The first message: what <c>VideoDecoder.configure()</c> needs, as JSON text.
    /// </summary>
    private static async Task SendInitialisationAsync(
        WebSocket socket,
        StreamInitialisation initialisation,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "init",
                codec = initialisation.Codec,
                width = initialisation.Width,
                height = initialisation.Height,
                frameRate = initialisation.FrameRate,
            },
            JsonOptions);

        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>
    /// Nine bytes: one of flags, then the timestamp as a big-endian unsigned 64-bit microsecond
    /// count. Big-endian because that is what <c>DataView.getBigUint64</c> reads by default.
    /// </summary>
    private static void WriteHeader(byte[] buffer, EncodedFrame frame)
    {
        buffer[0] = frame.IsKeyframe ? KeyframeFlag : (byte)0;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(1, 8), (ulong)frame.TimestampMicroseconds);
    }

    /// <summary>
    /// Reads from the socket purely to notice when the client goes away. Frames only ever flow
    /// outward, so anything received is either a close frame or noise.
    /// </summary>
    private static async Task WatchForCloseAsync(WebSocket socket, CancellationTokenSource closed)
    {
        var scratch = new byte[256];

        try
        {
            while (socket.State == WebSocketState.Open && !closed.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(scratch, closed.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Any failure here means the socket is gone, which is exactly what we are watching for.
        }
        finally
        {
            await closed.CancelAsync();
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, description, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Closing a socket the client already dropped is not worth reporting.
        }
    }
}
