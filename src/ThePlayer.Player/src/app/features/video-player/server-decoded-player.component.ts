import {
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { PlaybackStatsService } from '../../core/playback-stats.service';
import { StreamInitialisation } from '../../core/models';
import { WatchService } from '../../core/watch.service';

const HEADER_BYTES = 9;

/**
 * Plays a stream the server decoded entirely: whole JPEG images over the same frame socket.
 *
 * The third mode, and the far end of the trade. `ClientDecoded` asks the browser to do all the
 * decoding and the server none; this asks the opposite, and the browser runs no `VideoDecoder` at
 * all — `createImageBitmap` unpacks a self-contained picture.
 *
 * It shares the frame socket with `ClientDecoded` rather than needing a transport of its own,
 * because the protocol was never about a codec: one JSON init message, then one binary message per
 * picture. What changes is only what is inside them.
 *
 * The cost moved rather than vanished. MJPEG has no inter-frame compression, so this is the mode
 * that is cheap on the client, expensive on the server, and expensive on the network — which is
 * exactly what the monitor beside it should show.
 */
@Component({
  selector: 'app-server-decoded-player',
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
export class ServerDecodedPlayerComponent implements OnInit, OnDestroy {
  readonly socketPath = input.required<string>();
  readonly failed = output<string>();
  readonly ended = output<void>();

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly stats = inject(PlaybackStatsService);
  private readonly watch = inject(WatchService);

  private socket: WebSocket | null = null;
  private context: CanvasRenderingContext2D | null = null;
  private pending = 0;
  private closed = false;

  /**
   * The timestamp of the newest picture drawn.
   *
   * `createImageBitmap` is asynchronous, so two pictures can be in flight and finish out of order.
   * Without this the canvas would occasionally step backwards by a frame — rare, and exactly the
   * kind of thing that reads as a decoder fault rather than a race.
   */
  private newestDrawn = -1;

  ngOnInit(): void {
    this.connect();
  }

  ngOnDestroy(): void {
    this.teardown();
  }

  private connect(): void {
    const socket = new WebSocket(this.watch.socketUrl(this.socketPath()));
    socket.binaryType = 'arraybuffer';
    this.socket = socket;

    this.stats.start();

    socket.onmessage = (event) => {
      if (typeof event.data === 'string') {
        this.configure(JSON.parse(event.data) as StreamInitialisation);
        return;
      }

      void this.draw(event.data as ArrayBuffer);
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

  /**
   * There is no decoder to configure — only a canvas to size.
   *
   * `init.codec` reads `mjpeg`, which is the one value on this socket that is not an RFC 6381
   * string. Nothing here needs to parse it: the mode already told the shell which player to build.
   */
  private configure(init: StreamInitialisation): void {
    const canvas = this.canvasRef().nativeElement;
    canvas.width = init.width;
    canvas.height = init.height;
    this.context = canvas.getContext('2d');
  }

  private async draw(message: ArrayBuffer): Promise<void> {
    const view = new DataView(message);
    const timestamp = Number(view.getBigUint64(1));
    const picture = new Blob([new Uint8Array(message, HEADER_BYTES)], { type: 'image/jpeg' });

    const startedAt = performance.now();
    this.pending++;
    this.stats.setQueueDepth(this.pending);

    let bitmap: ImageBitmap;

    try {
      bitmap = await createImageBitmap(picture);
    } catch {
      // A picture that will not decode is one lost frame, not a broken stream: every image is
      // independent, so the next one is unaffected and there is no reference chain to repair.
      this.pending = Math.max(0, this.pending - 1);
      this.stats.setQueueDepth(this.pending);
      this.stats.recordDropped();
      return;
    }

    try {
      if (this.closed) {
        return;
      }

      // "Decode time" here is the unpack, and it is the number the comparison turns on: it should
      // be a fraction of what WebCodecs costs in the client-decoded mode, because the hard work
      // already happened on the server.
      this.stats.recordFrame(message.byteLength - HEADER_BYTES, performance.now() - startedAt);

      if (timestamp >= this.newestDrawn) {
        this.newestDrawn = timestamp;
        this.context?.drawImage(bitmap, 0, 0);
      } else {
        // An older picture that finished late. Counted rather than drawn, so the overlay shows it.
        this.stats.recordDropped();
      }
    } finally {
      // The same discipline the WebCodecs path needs, and for the same reason: an ImageBitmap holds
      // memory the garbage collector will not reclaim on its own.
      bitmap.close();
      this.pending = Math.max(0, this.pending - 1);
      this.stats.setQueueDepth(this.pending);
    }
  }

  private teardown(): void {
    this.closed = true;
    this.socket?.close();
    this.socket = null;
    this.context = null;
    this.stats.stop();
  }
}
