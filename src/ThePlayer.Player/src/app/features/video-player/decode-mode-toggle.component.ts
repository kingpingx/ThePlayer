import { Component, input, output } from '@angular/core';
import { MODE_BLURBS, MODE_LABELS, ModeAvailability, PLAYBACK_MODES, PlaybackMode } from '../../core/models';

/**
 * Picks where decoding happens.
 *
 * Every unavailable option carries the sentence the server sent with it. A greyed-out control with
 * no explanation is worse than no control at all: the user is left to guess whether it is their
 * browser, the stream, or a bug.
 */
@Component({
  selector: 'app-decode-mode-toggle',
  standalone: true,
  template: `
    <fieldset class="modes">
      <legend>Where does decoding happen?</legend>

      @for (mode of modes; track mode) {
        @let state = availabilityFor(mode);
        <label class="mode" [class.unavailable]="state && !state.available">
          <input
            type="radio"
            name="mode"
            [value]="mode"
            [checked]="mode === selected()"
            [disabled]="!!state && !state.available"
            (change)="modeChange.emit(mode)" />

          <span class="body">
            <span class="label">{{ labels[mode] }}</span>
            <span class="blurb">{{ state?.available === false ? state?.reason : blurbs[mode] }}</span>
          </span>
        </label>
      }
    </fieldset>
  `,
  styles: [`
    .modes { border: 1px solid #d0d5dd; border-radius: 8px; padding: 0.75rem 1rem 1rem; }
    legend { font-weight: 600; padding: 0 0.35rem; }
    .mode { display: flex; gap: 0.6rem; align-items: flex-start; padding: 0.4rem 0; cursor: pointer; }
    .mode.unavailable { cursor: not-allowed; opacity: 0.55; }
    .body { display: flex; flex-direction: column; }
    .label { font-weight: 600; }
    .blurb { font-size: 0.85rem; color: #475467; }
  `],
})
export class DecodeModeToggleComponent {
  readonly selected = input.required<PlaybackMode>();

  /** Empty until the first watch - nothing is known about a stream nobody has asked for yet. */
  readonly availability = input<ModeAvailability[]>([]);
  readonly modeChange = output<PlaybackMode>();

  protected readonly modes = PLAYBACK_MODES;
  protected readonly labels = MODE_LABELS;
  protected readonly blurbs = MODE_BLURBS;

  protected availabilityFor(mode: PlaybackMode): ModeAvailability | undefined {
    return this.availability().find((entry) => entry.mode === mode);
  }
}
