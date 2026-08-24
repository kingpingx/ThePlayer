import {
  Component,
  ElementRef,
  OnDestroy,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { PlaybackStatsService } from '../../core/playback-stats.service';
import { StreamInitialisation } from '../../core/models';
import { WatchService } from '../../core/watch.service';

/** Bit 0 of the flags byte. See docs/PROTOCOL.md. */
const KEYFRAME_FLAG = 0x01;
const HEADER_BYTES = 9;

/**
 * Plays a stream the browser decodes itself: frames over a WebSocket, WebCodecs, a canvas.
 *
 * This is the mode the project exists to make possible. The transport is codec-agnostic, so an
 * H.265 feed reaches a browser that can decode H.265 as the original bytes and the server does no
 * codec work at all - where WebRTC would have forced a transcode because SDP negotiation refuses
 * H.265 before anything could intervene.
 */
@Component({
  selector: 'app-client-decoded-player',
  standalone: true,
  template: `
    <canvas #canvas class="surface"></canvas>
  `,
  styles: [`
    .surface {
      display: block;
      width: 100%;
      height: auto;
      background: #000;
      border-radius: 6px;
    }
  `],
})
export class ClientDecodedPlayerComponent implements OnDestroy {
  readonly socketPath = input.required<string>();
  readonly failed = output<string>();
  readonly ended = output<void>();

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly stats = inject(PlaybackStatsService);
  private readonly watch = inject(WatchService);

  private socket: WebSocket | null = null;
  private decoder: VideoDecoder | null = null;
  private context: CanvasRenderingContext2D | null = null;
  private pending = 0;
  private submittedAt = new Map<number, number>();
  private configured = false;

  ngOnInit(): void {
    this.connect();
  }

  ngOnDestroy(): void {
    this.teardown();
  }

  private connect(): void {
    const url = this.watch.socketUrl(this.socketPath());
    const socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    this.socket = socket;

    this.stats.start();

    socket.onmessage = (event) => {
      if (typeof event.data === 'string') {
        this.configure(JSON.parse(event.data) as StreamInitialisation);
        return;
      }

      this.decode(event.data as ArrayBuffer);
    };

    socket.onerror = () => this.failed.emit('The frame socket failed.');
    socket.onclose = (event) => {
      // 1000 is the server saying the stream ended - a file reaching its last frame. Anything
      // else is a fault worth showing.
      if (event.code === 1000) {
        this.ended.emit();
      } else if (event.code !== 1005) {
        this.failed.emit(`The frame socket closed (${event.code}).`);
      }
    };
  }

  private configure(init: StreamInitialisation): void {
    const canvas = this.canvasRef().nativeElement;
    canvas.width = init.width;
    canvas.height = init.height;
    this.context = canvas.getContext('2d');

    const decoder = new VideoDecoder({
      output: (frame) => this.render(frame),
      error: (error) => this.failed.emit(`The decoder failed: ${error.message}`),
    });

    // No `description`, which is what makes the bitstream Annex-B by definition - and why the
    // server inlines parameter sets in front of every keyframe rather than passing them here.
    decoder.configure({
      codec: init.codec,
      codedWidth: init.width,
      codedHeight: init.height,
      optimizeForLatency: true,
    });

    this.decoder = decoder;
    this.configured = true;
  }

  private decode(message: ArrayBuffer): void {
    if (!this.configured || !this.decoder) {
      // Frames before the init message would have nowhere to go. The server sends init first, so
      // this only happens if a socket is reused, but dropping is the safe response either way.
      this.stats.recordDropped();
      return;
    }

    const view = new DataView(message);
    const isKeyframe = (view.getUint8(0) & KEYFRAME_FLAG) !== 0;
    const timestamp = Number(view.getBigUint64(1));
    const payload = new Uint8Array(message, HEADER_BYTES);

    this.submittedAt.set(timestamp, performance.now());
    this.pending++;
    this.stats.setQueueDepth(this.pending);

    try {
      this.decoder.decode(new EncodedVideoChunk({
        type: isKeyframe ? 'key' : 'delta',
        timestamp,
        data: payload,
      }));
    } catch (error) {
      this.pending--;
      this.submittedAt.delete(timestamp);
      this.stats.recordDropped();
      this.failed.emit(`Could not decode a frame: ${(error as Error).message}`);
    }
  }

  /**
   * Draws one frame and closes it.
   *
   * The `finally` is the single most important line in this component. A `VideoFrame` holds GPU
   * memory and is not garbage collected, so a missed close leaks until the decoder stalls - and it
   * stalls silently, looking like a stream that simply stopped. `PlaybackStatsService.queueDepth`
   * is what makes such a leak visible before it gets that far.
   */
  private render(frame: VideoFrame): void {
    try {
      const submitted = this.submittedAt.get(frame.timestamp);
      if (submitted !== undefined) {
        this.submittedAt.delete(frame.timestamp);
        this.stats.recordFrame(frame.codedWidth * frame.codedHeight, performance.now() - submitted);
      }

      this.context?.drawImage(frame, 0, 0);
    } finally {
      frame.close();
      this.pending = Math.max(0, this.pending - 1);
      this.stats.setQueueDepth(this.pending);
    }
  }

  private teardown(): void {
    this.socket?.close();
    this.socket = null;

    if (this.decoder && this.decoder.state !== 'closed') {
      this.decoder.close();
    }

    this.decoder = null;
    this.submittedAt.clear();
    this.pending = 0;
    this.configured = false;
    this.stats.stop();
  }
}
