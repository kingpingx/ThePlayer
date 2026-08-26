using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ThePlayer.Application.Monitoring;

namespace ThePlayer.Api;

/// <summary>
/// <c>GET /api/metrics/stream</c> — server resource readings, as Server-Sent Events.
/// </summary>
/// <remarks>
/// <para>
/// SSE rather than a WebSocket because the flow is one-way, text and periodic, which is what SSE is
/// for - and <c>EventSource</c> reconnects by itself where a WebSocket needs hand-written ping and
/// pong. It also keeps the frame socket dedicated to binary video, so a metrics hiccup cannot
/// disturb playback.
/// </para>
/// <para>
/// Written here rather than as a minimal-API result for the same reason as the frame socket: the
/// handler owns the response for the life of the connection instead of returning one.
/// </para>
/// </remarks>
public static class MetricsStream
{
    /// <summary>
    /// Serialisation is fixed here rather than taken from the host's configuration, because the
    /// shape is frozen in <c>docs/PROTOCOL.md</c> and a global JSON setting changed for some other
    /// reason should not silently rename fields a client is bound to.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How often to write a comment line when no reading has arrived.
    /// </summary>
    /// <remarks>
    /// A proxy that sees nothing on a connection will eventually close it. A comment is ignored by
    /// <c>EventSource</c> and costs two bytes, which is a cheaper way to stay alive than letting
    /// the client rediscover the endpoint every minute.
    /// </remarks>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static async Task HandleAsync(
        HttpContext context,
        MetricsCollector collector,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(MetricsStream));

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        // Buffering a stream whose entire purpose is to be current would defeat it, and a reverse
        // proxy is the most likely place for that to happen silently.
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = collector.Subscribe();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await NextAsync(subscription, cancellationToken);

                if (snapshot is null)
                {
                    // Nothing arrived within the keep-alive window. Say so in a way the client
                    // ignores, and go back to waiting.
                    await WriteAsync(context, ": keep-alive\n\n", cancellationToken);
                    continue;
                }

                var payload = JsonSerializer.Serialize(MetricsContracts.ToResponse(snapshot), Json);
                await WriteAsync(context, $"data: {payload}\n\n", cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away, which is how this connection always ends.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The metrics stream ended unexpectedly.");
        }
    }

    /// <summary>
    /// The next reading, or <c>null</c> if the keep-alive window elapsed first.
    /// </summary>
    /// <remarks>
    /// A completed channel also returns null and the loop writes a comment, which is correct: the
    /// collector completes a subscription only when it is released, and that happens on the way out
    /// of this method's own <c>using</c>.
    /// </remarks>
    private static async Task<Domain.Monitoring.ResourceSnapshot?> NextAsync(
        MetricsSubscription subscription,
        CancellationToken cancellationToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(KeepAlive);

        try
        {
            return await subscription.Snapshots.ReadAsync(window.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(HttpContext context, string text, CancellationToken cancellationToken)
    {
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
