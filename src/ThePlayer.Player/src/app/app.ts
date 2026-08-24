import { Component, computed, inject, signal } from '@angular/core';
import { AddressBarComponent } from './features/address-bar/address-bar.component';
import { CapabilityPanelComponent } from './features/capability-panel/capability-panel.component';
import { ClientDecodedPlayerComponent } from './features/video-player/client-decoded-player.component';
import { DecodeModeToggleComponent } from './features/video-player/decode-mode-toggle.component';
import { ServerAssistedPlayerComponent } from './features/video-player/server-assisted-player.component';
import { PlaybackStatsService } from './core/playback-stats.service';
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
    ServerAssistedPlayerComponent,
  ],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly watchService = inject(WatchService);
  protected readonly statsService = inject(PlaybackStatsService);

  protected readonly mode = signal<PlaybackMode>('ClientDecoded');
  protected readonly watch = signal<WatchResponse | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<{ title: string; detail: string } | null>(null);
  protected readonly note = signal<string | null>(null);
  protected readonly address = signal('');

  protected readonly stats = this.statsService.stats;

  protected readonly isClientDecoded = computed(() => this.watch()?.transport.kind === 'WebSocket');

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
