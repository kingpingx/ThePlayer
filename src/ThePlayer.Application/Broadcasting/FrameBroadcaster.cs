using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ThePlayer.Domain.Media;

namespace ThePlayer.Application.Broadcasting;

/// <summary>
/// Fans one pipeline out to every viewer watching it, and never lets a slow viewer hold up the rest.
/// </summary>
/// <remarks>
/// <para>
/// Each subscriber gets its own bounded queue with <see cref="BoundedChannelFullMode.DropOldest"/>,
/// so a client on a bad connection loses its own oldest frames instead of stalling the pipeline
/// that feeds everyone else. After a drop the subscriber is marked out of sync and skipped until
/// the next keyframe, so it never renders from a broken reference chain - and its queue is emptied
/// at that point, so it resumes at the live edge rather than replaying stale frames.
/// </para>
/// <para>
/// Concrete rather than behind a port: pure in-process logic with nothing to substitute.
/// </para>
/// </remarks>
public sealed class FrameBroadcaster : IAsyncDisposable
{
    /// <summary>
    /// How many frames a viewer may fall behind before frames start being dropped.
    /// </summary>
    /// <remarks>
    /// About two seconds at 25fps. Large enough to ride out a scheduling hiccup, small enough that
    /// a client which genuinely cannot keep up is cut back to the live edge quickly rather than
    /// drifting further behind.
    /// </remarks>
    private const int QueueCapacity = 60;

    private readonly IFrameStream _stream;
    private readonly ILogger _logger;
    private readonly string _key;
    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;

    public FrameBroadcaster(IFrameStream stream, string key, ILogger logger)
    {
        _stream = stream;
        _key = key;
        _logger = logger;
        _pump = PumpAsync();
    }

    /// <summary>What a client needs to configure a decoder before the first frame arrives.</summary>
    public StreamInitialisation Initialisation => _stream.Initialisation;

    /// <summary>Whether the pipeline has run out of frames - a file that reached its last one.</summary>
    public bool HasEnded { get; private set; }

    /// <summary>Starts delivering frames to a viewer.</summary>
    /// <returns>The queue to read from. Completes when the broadcast ends or the viewer detaches.</returns>
    public ChannelReader<EncodedFrame> Subscribe(string viewerId)
    {
        var subscriber = new Subscriber(Channel.CreateBounded<EncodedFrame>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            }));

        // A viewer attaching mid-stream cannot decode until the next keyframe, and the parameter
        // sets it needs ride in front of that keyframe. So it starts out of sync by definition.
        subscriber.OutOfSync = true;

        _subscribers[viewerId] = subscriber;
        _logger.LogDebug("Viewer {ViewerId} subscribed to frames for {Key}.", viewerId, _key);

        return subscriber.Channel.Reader;
    }

    /// <summary>Stops delivering frames to a viewer. Safe to call twice.</summary>
    public void Unsubscribe(string viewerId)
    {
        if (_subscribers.TryRemove(viewerId, out var subscriber))
        {
            subscriber.Channel.Writer.TryComplete();
            _logger.LogDebug("Viewer {ViewerId} unsubscribed from frames for {Key}.", viewerId, _key);
        }
    }

    public int SubscriberCount => _subscribers.Count;

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var frame in _stream.FramesAsync(_lifetime.Token))
            {
                foreach (var (viewerId, subscriber) in _subscribers)
                {
                    Deliver(viewerId, subscriber, frame);
                }
            }

            HasEnded = true;
            _logger.LogInformation("Frame stream for {Key} ended.", _key);
        }
        catch (OperationCanceledException)
        {
            // Torn down. Expected.
        }
        catch (Exception ex)
        {
            HasEnded = true;
            _logger.LogError(ex, "Frame pump for {Key} failed.", _key);
        }
        finally
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Channel.Writer.TryComplete();
            }
        }
    }

    private void Deliver(string viewerId, Subscriber subscriber, EncodedFrame frame)
    {
        if (subscriber.OutOfSync)
        {
            if (!frame.IsKeyframe)
            {
                return;
            }

            // Caught up. Throw away anything still queued so the viewer resumes at the live edge
            // rather than working through frames it can no longer decode in order.
            while (subscriber.Channel.Reader.TryRead(out _))
            {
            }

            subscriber.OutOfSync = false;
            _logger.LogDebug("Viewer {ViewerId} resynchronised at a keyframe.", viewerId);
        }
        else if (subscriber.Channel.Reader.Count >= QueueCapacity)
        {
            // The write below would silently drop the oldest frame, breaking the reference chain
            // for everything already queued. Mark it and wait for a keyframe instead.
            subscriber.OutOfSync = true;
            _logger.LogDebug(
                "Viewer {ViewerId} fell {Capacity} frames behind; skipping to the next keyframe.",
                viewerId,
                QueueCapacity);

            return;
        }

        subscriber.Channel.Writer.TryWrite(frame);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();

        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Channel.Writer.TryComplete();
        }

        _subscribers.Clear();

        await _pump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default);

        await _stream.DisposeAsync();
        _lifetime.Dispose();
    }

    /// <summary>One viewer's queue, and whether it is currently able to decode what it is sent.</summary>
    private sealed class Subscriber(Channel<EncodedFrame> channel)
    {
        public Channel<EncodedFrame> Channel { get; } = channel;

        /// <summary>
        /// Set while the viewer cannot decode the frames being produced - it has just attached, or
        /// it fell behind. Cleared at the next keyframe.
        /// </summary>
        public bool OutOfSync { get; set; }
    }
}
