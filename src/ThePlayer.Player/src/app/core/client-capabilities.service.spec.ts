import { TestBed } from '@angular/core/testing';
import { ClientCapabilitiesService } from './client-capabilities.service';

/**
 * The reason `VideoDecoder` is wrapped in an injectable at all.
 *
 * These tests present four browsers that this machine is not: one that decodes H.265 in hardware,
 * one that decodes it only in software, one that cannot decode it, and one with no WebCodecs API.
 * None of that is reachable through the real API, which reports whatever the machine running the
 * suite happens to support - so without the seam, the capability panel and the mode toggle could
 * only ever be tested in the one configuration the developer is sitting at.
 */
describe('ClientCapabilitiesService', () => {
  const realDecoder = (globalThis as Record<string, unknown>)['VideoDecoder'];

  /** Stands in for `VideoDecoder`, answering however the test says a browser would. */
  function fakeBrowser(answer: (config: VideoDecoderConfig) => boolean): void {
    (globalThis as Record<string, unknown>)['VideoDecoder'] = Object.assign(
      function VideoDecoder() {},
      {
        isConfigSupported: (config: VideoDecoderConfig) =>
          Promise.resolve({ supported: answer(config), config }),
      },
    );
  }

  function noWebCodecs(): void {
    delete (globalThis as Record<string, unknown>)['VideoDecoder'];
  }

  afterEach(() => {
    if (realDecoder) {
      (globalThis as Record<string, unknown>)['VideoDecoder'] = realDecoder;
    } else {
      noWebCodecs();
    }
  });

  function service(): ClientCapabilitiesService {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});
    return TestBed.inject(ClientCapabilitiesService);
  }

  it('reports hardware support when the browser prefers hardware and gets it', async () => {
    fakeBrowser(() => true);

    const support = await service().probe();
    const h265 = support.codecs.find((codec) => codec.codec === 'H265');

    expect(support.webCodecs).toBeTrue();
    expect(h265?.supported).toBeTrue();
    expect(h265?.hardwareAccelerated).toBeTrue();
  });

  it('separates software-only support from hardware support', async () => {
    // Supported in general, but the hardware-preferring probe comes back no. A 1080p feed on a
    // software decoder is worth telling someone about rather than silently accepting.
    fakeBrowser((config) => config.hardwareAcceleration !== 'prefer-hardware');

    const support = await service().probe();
    const h265 = support.codecs.find((codec) => codec.codec === 'H265');

    expect(h265?.supported).toBeTrue();
    expect(h265?.hardwareAccelerated).toBeFalse();
  });

  it('reports a browser that cannot decode H.265 at all', async () => {
    fakeBrowser((config) => !config.codec.startsWith('hev1'));

    const support = await service().probe();

    expect(support.codecs.find((codec) => codec.codec === 'H265')?.supported).toBeFalse();
    expect(support.codecs.find((codec) => codec.codec === 'H264')?.supported).toBeTrue();
  });

  it('reports no support at all when the browser has no WebCodecs', async () => {
    noWebCodecs();

    const support = await service().probe();

    expect(support.webCodecs).toBeFalse();
    expect(support.codecs.every((codec) => !codec.supported)).toBeTrue();
  });

  it('treats a codec string the browser rejects outright as unsupported', async () => {
    // An unrecognised codec string throws rather than returning false. Claiming support we do not
    // have would produce a stream that cannot be played, so the safe direction is "no".
    (globalThis as Record<string, unknown>)['VideoDecoder'] = Object.assign(
      function VideoDecoder() {},
      { isConfigSupported: () => Promise.reject(new TypeError('unrecognised codec')) },
    );

    const support = await service().probe();

    expect(support.webCodecs).toBeTrue();
    expect(support.codecs.every((codec) => !codec.supported)).toBeTrue();
  });
});
