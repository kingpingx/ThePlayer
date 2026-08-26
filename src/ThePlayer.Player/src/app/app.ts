import { Component, computed, inject, signal } from '@angular/core';
import { AddressBarComponent } from './features/address-bar/address-bar.component';
import { CapabilityPanelComponent } from './features/capability-panel/capability-panel.component';
import { ClientDecodedPlayerComponent } from './features/video-player/client-decoded-player.component';
import { DecodeModeToggleComponent } from './features/video-player/decode-mode-toggle.component';
import { ResourceMonitorComponent } from './features/resource-monitor/resource-monitor.component';
import { ServerAssistedPlayerComponent } from './features/video-player/server-assisted-player.component';
import { ServerDecodedPlayerComponent } from './features/video-player/server-decoded-player.component';
import { PlaybackStatsService } from './core/playback-stats.service';
import { ServerMetricsService } from './core/server-metrics.service';
import { PlaybackMode, WatchResponse } from './core/models';
import { WatchError, WatchService } from './core/watch.service';

/**
 * The shell: an address, a mode, a player, and the two halves of the cost of the choice.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    AddressBarComponent,
    CapabilityPanelComponent,
    ClientDecodedPlayerComponent,
    DecodeModeToggleComponent,
    ResourceMonitorComponent,
    ServerAssistedPlayerComponent,
    ServerDecodedPlayerComponent,
  ],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly watchService = inject(WatchService);
  private readonly serverMetrics = inject(ServerMetricsService);
  protected readonly statsService = inject(PlaybackStatsService);

  protected readonly mode = signal<PlaybackMode>('ClientDecoded');
  protected readonly watch = signal<WatchResponse | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<{ title: string; detail: string } | null>(null);
  protected readonly note = signal<string | null>(null);
  protected readonly address = signal('');

  protected readonly stats = this.statsService.stats;

  /** Both socket modes, which is what the transport says. Which of the two is the mode's business. */
  protected readonly isSocketMode = computed(() => this.watch()?.transport.kind === 'WebSocket');

  /**
   * The mode where the browser decodes no video at all.
   *
   * Distinguished by mode rather than by the codec string in the init message, because the shell
   * has to choose a component before the socket has said anything.
   */
  protected readonly isServerDecoded = computed(() => this.watch()?.mode === 'ServerDecoded');

  /** The client half of the comparison only means something where the client is decoding. */
  protected readonly isClientDecoded = computed(
    () => this.isSocketMode() && !this.isServerDecoded(),
  );

  /** The one fact worth putting in front of someone: is the server doing codec work right now? */
  protected readonly converted = computed(() => this.watch()?.converted ?? false);

  protected async onPlay(address: string): Promise<void> {
    this.address.set(address);
    await this.start(address, this.mode());
  }

  protected async onModeChange(mode: PlaybackMode): Promise<void> {
    this.mode.set(mode);

    // Switching mode mid-stream is the whole demonstration - the picture continues while the cost
    // moves between the two machines - so it re-watches rather than waiting to be asked again.
    if (this.address()) {
      await this.start(this.address(), mode);
    }
  }

  protected async onStop(): Promise<void> {
    await this.watchService.stop();

    // The server samples only while something is listening, so leaving this open would have it
    // spawn a process every second to measure a machine that is no longer doing anything.
    this.serverMetrics.stop();

    this.watch.set(null);
    this.note.set(null);
  }

  protected onPlayerFailed(message: string): void {
    this.error.set({ title: 'Playback failed', detail: message });
    void this.onStop();
  }

  protected onEnded(): void {
    this.note.set('The file reached its last frame.');
  }

  private async start(address: string, mode: PlaybackMode): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.note.set(null);
    this.watch.set(null);

    try {
      this.watch.set(await this.watchService.start(address, mode));

      // Only once something is playing. The server samples while it has a listener, so opening
      // this earlier would ask it to measure an idle machine for as long as the tab stays open.
      this.serverMetrics.start();
    } catch (failure) {
      const problem = failure as WatchError;

      this.error.set({
        title: problem.title ?? 'Could not play that',
        detail: problem.message,
      });
    } finally {
      this.busy.set(false);
    }
  }
}
