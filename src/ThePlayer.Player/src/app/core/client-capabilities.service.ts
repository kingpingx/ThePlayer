import { Injectable } from '@angular/core';
import { ClientDecodeSupport, CodecSupport, VideoCodecName } from './models';

/**
 * The codecs worth asking about, with a conservative RFC 6381 string for each.
 *
 * These are baselines used only to ask "could this browser decode this family of codec at all?".
 * The precise string for a live stream is derived by the server from the stream's own parameter
 * sets and arrives in the socket's init message.
 */
const PROBES: ReadonlyArray<readonly [VideoCodecName, string]> = [
  ['H264', 'avc1.42001f'],
  ['H265', 'hev1.1.6.L93.B0'],
  ['Vp8', 'vp8'],
  ['Vp9', 'vp09.00.10.08'],
  ['Av1', 'av01.0.04M.08'],
];

/**
 * Wraps the WebCodecs API so the rest of the app never touches it directly.
 *
 * The indirection earns its keep in tests: a fake can present a browser that decodes H.265, one
 * that does not, one with software-only support, and one with no WebCodecs at all - and the panel
 * and the mode toggle can each be asserted to show the right state and the right reason. None of
 * that is reachable through the real API, which reports whatever the machine running the test
 * happens to support.
 */
@Injectable({ providedIn: 'root' })
export class ClientCapabilitiesService {
  private cached: ClientDecodeSupport | null = null;

  /** Whether this browser has WebCodecs at all. Old browsers and some embedded views do not. */
  get hasWebCodecs(): boolean {
    return typeof globalThis.VideoDecoder === 'function';
  }

  /**
   * Probes every codec of interest. Cached - the answer cannot change while the page is open, and
   * it is asked for on every watch request.
   */
  async probe(): Promise<ClientDecodeSupport> {
    if (this.cached) {
      return this.cached;
    }

    const webCodecs = this.hasWebCodecs;
    const codecs: CodecSupport[] = [];

    for (const [codec, codecString] of PROBES) {
      codecs.push(await this.probeOne(codec, codecString, webCodecs));
    }

    this.cached = { webCodecs, codecs };
    return this.cached;
  }

  private async probeOne(
    codec: VideoCodecName,
    codecString: string,
    webCodecs: boolean,
  ): Promise<CodecSupport> {
    if (!webCodecs) {
      return { codec, supported: false, hardwareAccelerated: false };
    }

    const config: VideoDecoderConfig = {
      codec: codecString,
      codedWidth: 1280,
      codedHeight: 720,
    };

    try {
      const plain = await VideoDecoder.isConfigSupported(config);
      if (plain.supported !== true) {
        return { codec, supported: false, hardwareAccelerated: false };
      }

      // Asked separately, because "supported" covers a software decoder that would melt a laptop
      // on a 1080p feed.
      const hardware = await VideoDecoder.isConfigSupported({
        ...config,
        hardwareAcceleration: 'prefer-hardware',
      });

      return { codec, supported: true, hardwareAccelerated: hardware.supported === true };
    } catch {
      // An unrecognised codec string throws rather than returning false. Treating that as
      // "no decoder" is the safe direction: claiming support we do not have produces a stream
      // that cannot be played.
      return { codec, supported: false, hardwareAccelerated: false };
    }
  }
}
