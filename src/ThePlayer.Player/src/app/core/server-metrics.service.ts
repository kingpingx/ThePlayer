import { Injectable, signal } from '@angular/core';
import { environment } from '../../environments/environment';
import { ServerMetrics } from './models';

/** Whether the feed is delivering, and if not, what it is doing about it. */
export type FeedState = 'idle' | 'connecting' | 'live' | 'reconnecting';

/**
 * The server half of the cost comparison, over Server-Sent Events.
 *
 * `PlaybackStatsService` is the other half. Keeping them apart matters: one is measured in this
 * browser and the other on the server, and a panel that mixed them would invite the reader to think
 * they came from the same place.
 *
 * The connection is opened only while something is watching it. The server stops sampling when its
 * last listener goes, so a page that leaves this open is asking the server to spawn a process every
 * second on its behalf.
 */
@Injectable({ providedIn: 'root' })
export class ServerMetricsService {
  readonly metrics = signal<ServerMetrics | null>(null);
  readonly state = signal<FeedState>('idle');

  private source: EventSource | null = null;

  start(): void {
    if (this.source) {
      return;
    }

    this.state.set('connecting');
    const source = new EventSource(`${environment.apiBase}/api/metrics/stream`);
    this.source = source;

    source.onmessage = (event) => {
      try {
        this.metrics.set(JSON.parse(event.data) as ServerMetrics);
        this.state.set('live');
      } catch {
        // A malformed frame is not worth tearing the feed down for - the next one is a second
        // away, and EventSource has already proved the connection works.
      }
    };

    // EventSource reconnects by itself, which is most of why this is SSE and not a WebSocket.
    // `onerror` fires on the way into that retry, so it reports rather than repairs.
    source.onerror = () => {
      this.state.set(source.readyState === EventSource.CLOSED ? 'idle' : 'reconnecting');
    };
  }

  stop(): void {
    this.source?.close();
    this.source = null;
    this.metrics.set(null);
    this.state.set('idle');
  }
}
