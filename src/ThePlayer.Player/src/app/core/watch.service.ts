import { Injectable, inject } from '@angular/core';
import { environment } from '../../environments/environment';
import { ClientCapabilitiesService } from './client-capabilities.service';
import { PlaybackMode, ProblemDetails, WatchResponse } from './models';

/** A failure the API described well enough to show to a user. */
export class WatchError extends Error {
  constructor(
    readonly title: string,
    override readonly message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

/**
 * Starting and stopping a watch.
 *
 * Owns the viewer id for the current watch, because the one thing that must not be forgotten is the
 * detach - a leaked viewer keeps an FFmpeg process alive for the whole linger window and, if the
 * page is reloaded enough times, several of them.
 */
@Injectable({ providedIn: 'root' })
export class WatchService {
  private readonly capabilities = inject(ClientCapabilitiesService);
  private viewerId: string | null = null;

  async start(address: string, mode: PlaybackMode): Promise<WatchResponse> {
    await this.stop();

    const clientDecodeSupport = await this.capabilities.probe();

    const response = await fetch(`${environment.apiBase}/api/watch`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ address, mode, clientDecodeSupport }),
    });

    const body = await response.json();

    if (!response.ok) {
      const problem = body as ProblemDetails;
      throw new WatchError(
        problem.title ?? 'Could not play that',
        problem.detail ?? response.statusText,
        response.status,
      );
    }

    const watch = body as WatchResponse;
    this.viewerId = watch.viewerId;
    return watch;
  }

  /** Detaches. Safe to call when nothing is being watched. */
  async stop(): Promise<void> {
    const viewerId = this.viewerId;
    if (!viewerId) {
      return;
    }

    this.viewerId = null;

    try {
      await fetch(`${environment.apiBase}/api/watch/${viewerId}`, {
        method: 'DELETE',

        // keepalive so the request still lands when the tab is closing, which is the case that
        // matters most - it is what stops a refresh leaving a pipeline running.
        keepalive: true,
      });
    } catch {
      // The server lingers a broadcast and sweeps it anyway, so a failed detach costs a few
      // seconds of pipeline rather than a leak. Not worth surfacing.
    }
  }

  /** Absolute URL for a transport path the API handed back. */
  socketUrl(path: string): string {
    if (path.startsWith('ws://') || path.startsWith('wss://')) {
      return path;
    }

    const base = environment.apiBase || globalThis.location.origin;
    const url = new URL(path, base);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    return url.toString();
  }
}
