/*
 * The wire contract, as frozen in docs/PROTOCOL.md.
 *
 * Written against the document rather than generated from the C#, deliberately: the backend is
 * expected to be rewritten in Go, and these types are what must survive that. If a rename in the
 * server breaks this file, the protocol changed and the document should have changed with it.
 */

export type PlaybackMode = 'ClientDecoded' | 'ServerAssisted' | 'ServerDecoded';

export type VideoCodecName = 'H264' | 'H265' | 'Vp8' | 'Vp9' | 'Av1' | 'Mjpeg' | 'Unknown';

/** What one codec probe found. */
export interface CodecSupport {
  codec: VideoCodecName;
  supported: boolean;

  /**
   * Probed separately from `supported`, because a software-only decoder is supported but a poor
   * choice for a 1080p feed - and that is worth telling someone rather than silently accepting.
   */
  hardwareAccelerated: boolean;
}

export interface ClientDecodeSupport {
  webCodecs: boolean;
  codecs: CodecSupport[];
}

export interface WatchRequest {
  address: string;
  mode: PlaybackMode;
  clientDecodeSupport?: ClientDecodeSupport;
}

export interface VideoFormat {
  codec: VideoCodecName;
  width: number;
  height: number;
  frameRate: number;

  /** True for a camera. A file reaches a last frame and the broadcast ends. */
  live: boolean;
  durationSeconds?: number;
}

/** Whether a mode can be used for this stream on this client, and if not, why not. */
export interface ModeAvailability {
  mode: PlaybackMode;
  available: boolean;

  /** Written to be shown as-is. A greyed control with no explanation is worse than no control. */
  reason: string | null;
}

export interface Transport {
  kind: 'WebRtc' | 'WebSocket';

  /** A WHEP endpoint to POST an SDP offer to, or the path of a frame socket. */
  url: string;
}

export interface WatchResponse {
  viewerId: string;
  mode: PlaybackMode;
  format: VideoFormat;

  /**
   * Whether the server is decoding and re-encoding. The most interesting number in the system:
   * false means it is copying bytes and its codec cost is zero.
   */
  converted: boolean;
  modeAvailability: ModeAvailability[];
  transport: Transport;
}

/** RFC 7807. `detail` is always written to be shown to a user, and never contains credentials. */
export interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
}

/** The first message on a frame socket - what `VideoDecoder.configure()` needs. */
export interface StreamInitialisation {
  type: 'init';
  codec: string;
  width: number;
  height: number;
  frameRate: number;
}

export const PLAYBACK_MODES: readonly PlaybackMode[] = [
  'ClientDecoded',
  'ServerAssisted',
  'ServerDecoded',
];

/** What the mode toggle shows. The enum names are precise but not what anyone would say out loud. */
export const MODE_LABELS: Record<PlaybackMode, string> = {
  ClientDecoded: 'Decode on client',
  ServerAssisted: 'Decode on server (assisted)',
  ServerDecoded: 'Decode on server (full)',
};

export const MODE_BLURBS: Record<PlaybackMode, string> = {
  ClientDecoded: 'Server copies bytes. Your GPU decodes the original codec via WebCodecs.',
  ServerAssisted: 'Server converts only if it must. Your GPU decodes H.264 via WebRTC.',
  ServerDecoded: 'Server decodes fully and sends pictures. Your GPU decodes no video at all.',
};
