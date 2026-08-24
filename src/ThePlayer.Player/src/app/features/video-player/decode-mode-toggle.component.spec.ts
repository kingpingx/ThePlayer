import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DecodeModeToggleComponent } from './decode-mode-toggle.component';
import { ModeAvailability } from '../../core/models';

/**
 * A dead control with no explanation is worse than no control at all, so the reason the server sent
 * has to reach the screen.
 */
describe('DecodeModeToggleComponent', () => {
  let fixture: ComponentFixture<DecodeModeToggleComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DecodeModeToggleComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(DecodeModeToggleComponent);
    fixture.componentRef.setInput('selected', 'ServerAssisted');
  });

  function render(availability: ModeAvailability[]): HTMLElement {
    fixture.componentRef.setInput('availability', availability);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('offers every mode when they are all available', () => {
    const element = render([
      { mode: 'ClientDecoded', available: true, reason: null },
      { mode: 'ServerAssisted', available: true, reason: null },
      { mode: 'ServerDecoded', available: true, reason: null },
    ]);

    const disabled = element.querySelectorAll('input:disabled');
    expect(disabled.length).toBe(0);
  });

  it('explains itself rather than going dead when a mode is unavailable', () => {
    const reason = 'This browser reports no decoder for H.265 or H.264.';

    const element = render([
      { mode: 'ClientDecoded', available: false, reason },
      { mode: 'ServerAssisted', available: true, reason: null },
      { mode: 'ServerDecoded', available: true, reason: null },
    ]);

    expect(element.querySelectorAll('input:disabled').length).toBe(1);
    expect(element.textContent).toContain(reason);
  });

  it('emits the mode that was picked', () => {
    const element = render([
      { mode: 'ClientDecoded', available: true, reason: null },
      { mode: 'ServerAssisted', available: true, reason: null },
      { mode: 'ServerDecoded', available: true, reason: null },
    ]);

    // Collected rather than assigned to a single variable: TypeScript narrows a `let` written
    // only inside a callback down to its initial type, and the comparison stops compiling.
    const picked: string[] = [];
    fixture.componentInstance.modeChange.subscribe((mode) => picked.push(mode));

    const first = element.querySelector('input') as HTMLInputElement;
    first.dispatchEvent(new Event('change'));

    expect(picked).toEqual(['ClientDecoded']);
  });
});
