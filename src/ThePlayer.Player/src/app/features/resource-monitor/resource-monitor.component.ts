import { Component, computed, inject } from '@angular/core';
import { ServerMetricsService } from '../../core/server-metrics.service';

/**
 * What the server is doing right now — the other half of the trade.
 *
 * The capability panel says what this browser can do; this says what the choice costs the machine
 * at the other end. Toggle a mode on an H.265 feed and the encoder bar is what moves.
 *
 * Every figure here can be absent, and absent is rendered as a reason rather than as a zero. That
 * is the whole discipline of this panel: an idle GPU and a failed query produce the same number,
 * so a number is only shown when one was actually measured.
 */
@Component({
  selector: 'app-resource-monitor',
  standalone: true,
  template: `
    <section class="panel">
      <h2>
        This server
        @if (state() === 'reconnecting') {
          <span class="state">reconnecting…</span>
        } @else if (state() === 'connecting') {
          <span class="state">connecting…</span>
        }
      </h2>

      @if (metrics(); as m) {
        <div class="rows">
          <div class="row">
            <span class="name">CPU</span>
            @if (m.cpuPercent !== null) {
              <span class="bar"><i [style.width.%]="m.cpuPercent"></i></span>
              <b>{{ m.cpuPercent }}%</b>
            } @else {
              <span class="absent">measuring…</span>
            }
          </div>

          <div class="row">
            <span class="name">Memory</span>
            @if (memoryPercent() !== null) {
              <span class="bar"><i [style.width.%]="memoryPercent()"></i></span>
              <b>{{ gib(m.memoryUsedBytes) }} / {{ gib(m.memoryTotalBytes) }} GiB</b>
            } @else {
              <span class="absent">not available</span>
            }
          </div>
        </div>

        <h3>GPU</h3>

        @if (m.gpu.availability === 'Available') {
          <div class="rows">
            <div class="row">
              <span class="name">Overall</span>
              <span class="bar"><i [style.width.%]="m.gpu.overallPercent ?? 0"></i></span>
              <b>{{ m.gpu.overallPercent }}%</b>
            </div>

            <!-- The two that matter. Conversion lights both; passthrough lights neither. -->
            <div class="row" [class.hot]="(m.gpu.encoderPercent ?? 0) > 0">
              <span class="name">Encode</span>
              <span class="bar"><i [style.width.%]="m.gpu.encoderPercent ?? 0"></i></span>
              <b>{{ m.gpu.encoderPercent }}%</b>
            </div>

            <div class="row" [class.hot]="(m.gpu.decoderPercent ?? 0) > 0">
              <span class="name">Decode</span>
              <span class="bar"><i [style.width.%]="m.gpu.decoderPercent ?? 0"></i></span>
              <b>{{ m.gpu.decoderPercent }}%</b>
            </div>

            <div class="row">
              <span class="name">Memory</span>
              <span class="bar"></span>
              <b>{{ mib(m.gpu.memoryUsedBytes) }} MiB</b>
            </div>
          </div>
        } @else {
          <p class="absent reason">{{ m.gpu.unavailableReason }}</p>
        }

        @if (m.broadcasts.length > 0) {
          <h3>Per stream</h3>

          <ul class="streams">
            @for (b of m.broadcasts; track b.key) {
              <li>
                <span class="mode">
                  {{ b.mode }}
                  <em>{{ b.converted ? 'converting' : 'copying bytes' }}</em>
                </span>

                @if (b.cpuPercent !== null) {
                  <b>{{ b.cpuPercent }}% CPU</b>
                  <span class="how">{{ mib(b.memoryBytes) }} MiB</span>
                } @else {
                  <span class="absent" [title]="b.unavailableReason ?? ''">no cost here</span>
                }
              </li>
            }
          </ul>
        }

        <p class="taken">as of {{ takenAt() }}</p>
      } @else {
        <p class="absent">Waiting for the first reading…</p>
      }
    </section>
  `,
  styles: [`
    .panel { border: 1px solid #d0d5dd; border-radius: 8px; padding: 0.75rem 1rem; }
    h2 { font-size: 0.95rem; margin: 0 0 0.5rem; display: flex; justify-content: space-between; }
    h3 { font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.04em;
         color: #475467; margin: 0.85rem 0 0.35rem; font-weight: 600; }
    .state { color: #b54708; font-weight: 400; font-size: 0.8rem; }
    .rows { display: grid; gap: 0.3rem; }
    .row { display: grid; grid-template-columns: 4.5rem 1fr auto; gap: 0.5rem; align-items: center; }
    .name { color: #475467; font-size: 0.85rem; }
    .bar { background: #eaecf0; border-radius: 3px; height: 8px; overflow: hidden; }
    .bar i { display: block; height: 100%; background: #667085; transition: width 0.4s ease; }
    .row.hot .bar i { background: #b54708; }
    .row b { font-variant-numeric: tabular-nums; font-size: 0.85rem; min-width: 3.5rem;
             text-align: right; }
    .absent { color: #475467; font-size: 0.85rem; margin: 0; }
    .reason { line-height: 1.35; }
    .streams { list-style: none; margin: 0; padding: 0; display: grid; gap: 0.3rem; }
    .streams li { display: grid; grid-template-columns: 1fr auto auto; gap: 0.5rem;
                  align-items: baseline; font-size: 0.85rem; }
    .mode em { color: #475467; font-style: normal; display: block; font-size: 0.78rem; }
    .streams b { font-variant-numeric: tabular-nums; }
    .how { color: #475467; font-size: 0.78rem; }
    .taken { color: #98a2b3; font-size: 0.75rem; margin: 0.6rem 0 0; }
  `],
})
export class ResourceMonitorComponent {
  private readonly service = inject(ServerMetricsService);

  protected readonly metrics = this.service.metrics;
  protected readonly state = this.service.state;

  protected readonly memoryPercent = computed(() => {
    const m = this.metrics();

    if (!m || m.memoryUsedBytes === null || !m.memoryTotalBytes) {
      return null;
    }

    return Math.round((100 * m.memoryUsedBytes) / m.memoryTotalBytes);
  });

  /** The reading's own timestamp, so a frozen feed is visible as a clock that stopped. */
  protected readonly takenAt = computed(() => {
    const at = this.metrics()?.takenAt;
    return at ? new Date(at).toLocaleTimeString() : '';
  });

  protected gib(bytes: number | null): string {
    return bytes === null ? '—' : (bytes / 1024 ** 3).toFixed(1);
  }

  protected mib(bytes: number | null): string {
    return bytes === null ? '—' : Math.round(bytes / 1024 ** 2).toString();
  }
}
