import { Component, inject, signal } from '@angular/core';
import { ClientCapabilitiesService } from '../../core/client-capabilities.service';
import { ClientDecodeSupport, VideoCodecName } from '../../core/models';

const CODEC_LABELS: Partial<Record<VideoCodecName, string>> = {
  H264: 'H.264',
  H265: 'H.265',
  Vp8: 'VP8',
  Vp9: 'VP9',
  Av1: 'AV1',
};

/**
 * What this browser can decode, and whether it would do so in hardware.
 *
 * One half of the trade the project exists to show; Phase 4 adds the other, which is what the
 * server's CPU and GPU are doing at the same moment. Shown even before anything is playing, because
 * it is what explains why some modes are offered and others are not.
 */
@Component({
  selector: 'app-capability-panel',
  standalone: true,
  template: `
    <section class="panel">
      <h2>This browser</h2>

      @if (support(); as caps) {
        @if (!caps.webCodecs) {
          <p class="none">
            No WebCodecs API, so this browser cannot decode a stream directly. Server-side modes
            still work.
          </p>
        } @else {
          <ul>
            @for (codec of caps.codecs; track codec.codec) {
              <li [class.no]="!codec.supported">
                <span class="tick">{{ codec.supported ? '✓' : '✗' }}</span>
                <b>{{ label(codec.codec) }}</b>
                <span class="how">
                  {{ codec.supported
                      ? (codec.hardwareAccelerated ? 'hardware' : 'software only')
                      : 'no decoder' }}
                </span>
              </li>
            }
          </ul>
        }
      } @else {
        <p class="none">Probing…</p>
      }
    </section>
  `,
  styles: [`
    .panel { border: 1px solid #d0d5dd; border-radius: 8px; padding: 0.75rem 1rem; }
    h2 { font-size: 0.95rem; margin: 0 0 0.5rem; }
    ul { list-style: none; margin: 0; padding: 0; }
    li { display: grid; grid-template-columns: 1.2rem 4.5rem 1fr; gap: 0.4rem; padding: 0.15rem 0; }
    li.no { opacity: 0.55; }
    .tick { color: #067647; }
    li.no .tick { color: #b42318; }
    .how { color: #475467; font-size: 0.85rem; }
    .none { color: #475467; font-size: 0.9rem; margin: 0; }
  `],
})
export class CapabilityPanelComponent {
  private readonly capabilities = inject(ClientCapabilitiesService);
  protected readonly support = signal<ClientDecodeSupport | null>(null);

  constructor() {
    void this.capabilities.probe().then((result) => this.support.set(result));
  }

  protected label(codec: VideoCodecName): string {
    return CODEC_LABELS[codec] ?? codec;
  }
}
