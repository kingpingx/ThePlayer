import { Injectable, signal } from '@angular/core';

/** What the client half of the cost comparison looks like right now. */
export interface PlaybackStats {
  /** Frames actually rendered in the last second. */
  fps: number;

  /** Mean wall time from handing a chunk to the decoder to a frame coming back, in milliseconds. */
  decodeMs: number;

  /** Frames the server dropped for us, or that arrived unusable. */
  dropped: number;

  /**
   * How many decoded frames are waiting. The number that makes a leak visible: a missed
   * `VideoFrame.close()` shows up here as a figure that climbs and never comes down.
   */
  queueDepth: number;

  /** Bytes received per second. */
  bitrate: number;
}

const EMPTY: PlaybackStats = { fps: 0, decodeMs: 0, dropped: 0, queueDepth: 0, bitrate: 0 };

/**
 * Rolling playback statistics, sampled once a second.
 *
 * Half of the comparison this project exists to make - the other half is the server's CPU and GPU,
 * which Phase 4 adds. Kept in a service rather than a component so that the diagnostics overlay in
 * Phase 5 can read the same numbers without the player having to hand them around.
 */
@Injectable({ providedIn: 'root' })
export class PlaybackStatsService {
  readonly stats = signal<PlaybackStats>(EMPTY);

  private frames = 0;
  private bytes = 0;
  private decodeTotalMs = 0;
  private decodeSamples = 0;
  private dropped = 0;
  private queueDepth = 0;
  private timer: ReturnType<typeof setInterval> | null = null;

  start(): void {
    this.reset();
    this.timer ??= setInterval(() => this.sample(), 1000);
  }

  stop(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }

    this.reset();
    this.stats.set(EMPTY);
  }

  recordFrame(bytes: number, decodeMs: number): void {
    this.frames++;
    this.bytes += bytes;
    this.decodeTotalMs += decodeMs;
    this.decodeSamples++;
  }

  recordDropped(count = 1): void {
    this.dropped += count;
  }

  setQueueDepth(depth: number): void {
    this.queueDepth = depth;
  }

  private sample(): void {
    this.stats.set({
      fps: this.frames,
      decodeMs: this.decodeSamples > 0
        ? Math.round((this.decodeTotalMs / this.decodeSamples) * 100) / 100
        : 0,
      dropped: this.dropped,
      queueDepth: this.queueDepth,
      bitrate: this.bytes,
    });

    // Counts are per-second; dropped and queue depth are cumulative and current respectively, so
    // they deliberately survive the reset.
    this.frames = 0;
    this.bytes = 0;
    this.decodeTotalMs = 0;
    this.decodeSamples = 0;
  }

  private reset(): void {
    this.frames = 0;
    this.bytes = 0;
    this.decodeTotalMs = 0;
    this.decodeSamples = 0;
    this.dropped = 0;
    this.queueDepth = 0;
  }
}
