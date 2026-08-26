import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ResourceMonitorComponent } from './resource-monitor.component';
import { ServerMetricsService } from '../../core/server-metrics.service';
import { ServerMetrics } from '../../core/models';

/**
 * The discipline this panel exists to keep: a figure that was never measured is shown as a reason,
 * never as a zero. An idle GPU and a failed query produce the same number, so rendering one for the
 * other would quietly make the panel lie in exactly the situation it is there to catch.
 */
describe('ResourceMonitorComponent', () => {
  let fixture: ComponentFixture<ResourceMonitorComponent>;
  let service: ServerMetricsService;

  const READING: ServerMetrics = {
    takenAt: '2026-08-26T12:00:00Z',
    cpuPercent: 12.5,
    memoryUsedBytes: 8_000_000_000,
    memoryTotalBytes: 16_000_000_000,
    gpu: {
      availability: 'Available',
      overallPercent: 21,
      encoderPercent: 99,
      decoderPercent: 45,
      memoryUsedBytes: 176 * 1024 * 1024,
      unavailableReason: null,
    },
    broadcasts: [],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ResourceMonitorComponent],
    }).compileComponents();

    service = TestBed.inject(ServerMetricsService);
    fixture = TestBed.createComponent(ResourceMonitorComponent);
  });

  function render(metrics: ServerMetrics | null): HTMLElement {
    service.metrics.set(metrics);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the encoder figure, which is what proves the server is converting', () => {
    const element = render(READING);

    expect(element.textContent).toContain('Encode');
    expect(element.textContent).toContain('99%');
  });

  it('explains an unavailable GPU instead of showing zeroes', () => {
    const element = render({
      ...READING,
      gpu: {
        availability: 'ToolMissing',
        overallPercent: null,
        encoderPercent: null,
        decoderPercent: null,
        memoryUsedBytes: null,
        unavailableReason: 'nvidia-smi was not found.',
      },
    });

    expect(element.textContent).toContain('nvidia-smi was not found.');
    expect(element.textContent).not.toContain('Encode');
  });

  it('distinguishes an idle GPU from an unreadable one', () => {
    // The pair of assertions that matter together: zeroes are rendered as figures, because on an
    // available GPU they are a measurement rather than the absence of one.
    const element = render({
      ...READING,
      gpu: { ...READING.gpu, overallPercent: 0, encoderPercent: 0, decoderPercent: 0 },
    });

    expect(element.textContent).toContain('Encode');
    expect(element.textContent).toContain('0%');
  });

  it('says a stream costs nothing here rather than reporting zero', () => {
    // A pass-through ServerAssisted stream is relayed by the edge server, so there is no process to
    // measure — precisely because nothing is being spent.
    const element = render({
      ...READING,
      broadcasts: [
        {
          key: 'abc-serverassisted-h264',
          mode: 'ServerAssisted',
          converted: false,
          cpuPercent: null,
          memoryBytes: null,
          unavailableReason: 'The edge server is relaying this stream directly.',
        },
      ],
    });

    expect(element.textContent).toContain('no cost here');
    expect(element.textContent).not.toContain('% CPU');
  });

  it('shows what a converting stream costs', () => {
    const element = render({
      ...READING,
      broadcasts: [
        {
          key: 'abc-clientdecoded-h264-converted',
          mode: 'ClientDecoded',
          converted: true,
          cpuPercent: 61.2,
          memoryBytes: 190_000_000,
          unavailableReason: null,
        },
      ],
    });

    expect(element.textContent).toContain('converting');
    expect(element.textContent).toContain('61.2% CPU');
  });

  it('waits rather than showing a fabricated CPU figure on the first reading', () => {
    // CPU is a rate, so the first reading has nothing to subtract from and the server sends null.
    const element = render({ ...READING, cpuPercent: null });

    expect(element.textContent).toContain('measuring…');
  });

  it('says nothing has arrived yet before the first reading', () => {
    const element = render(null);

    expect(element.textContent).toContain('Waiting for the first reading');
  });
});
