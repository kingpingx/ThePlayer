import { Component, input, output, signal } from '@angular/core';

/**
 * Where the stream is.
 *
 * The address may carry a password (`rtsp://user:pass@camera/stream`), so it is sent once and never
 * echoed back - not in the watch response, not in errors, not in `GET /api/broadcasts`. Nothing
 * here stores it either: no history, no autocomplete, no local storage.
 */
@Component({
  selector: 'app-address-bar',
  standalone: true,
  template: `
    <form class="bar" (submit)="submit($event)">
      <input
        class="address"
        type="text"
        name="address"
        autocomplete="off"
        spellcheck="false"
        placeholder="rtsp://user:pass@192.168.1.64:554/stream  —  or  D:\\videos\\clip.mp4"
        [value]="value()"
        (input)="value.set($any($event.target).value)" />

      <button type="submit" [disabled]="busy() || value().trim().length === 0">
        {{ busy() ? 'Working…' : 'Play' }}
      </button>

      @if (playing()) {
        <button type="button" class="stop" (click)="stopped.emit()">Stop</button>
      }
    </form>
  `,
  styles: [`
    .bar { display: flex; gap: 0.5rem; }
    .address { flex: 1; padding: 0.55rem 0.7rem; border: 1px solid #d0d5dd; border-radius: 6px; font: inherit; }
    button { padding: 0.55rem 1.1rem; border-radius: 6px; border: 1px solid #7f56d9; background: #7f56d9; color: #fff; font: inherit; cursor: pointer; }
    button:disabled { opacity: 0.5; cursor: not-allowed; }
    .stop { background: #fff; color: #b42318; border-color: #fda29b; }
  `],
})
export class AddressBarComponent {
  readonly busy = input(false);
  readonly playing = input(false);
  readonly play = output<string>();
  readonly stopped = output<void>();

  protected readonly value = signal('');

  protected submit(event: Event): void {
    event.preventDefault();

    const address = this.value().trim();
    if (address.length > 0) {
      this.play.emit(address);
    }
  }
}
