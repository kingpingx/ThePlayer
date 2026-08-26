import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ServerDecodedPlayerComponent } from './server-decoded-player.component';
import { PlaybackStatsService } from '../../core/playback-stats.service';

/**
 * The third player: no `VideoDecoder`, whole JPEG images, `createImageBitmap`.
 *
 * A real JPEG is decoded here rather than a mocked one. The thing most likely to break this
 * component is the framing — an off-by-nine in the header, or a `Blob` built over the wrong slice —
 * and a fake decoder would accept all of those happily.
 */
describe('ServerDecodedPlayerComponent', () => {
  /** A 2×2 red JPEG. Small enough to inline, real enough for the browser to decode. */
  const JPEG_BASE64 =
    '/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a' +
    'HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAACAAIBAREA/8QAHwAAAQUBAQEB' +
    'AQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1Fh' +
    'ByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZ' +
    'WmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXG' +
    'x8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/APn+v//Z';

  let fixture: ComponentFixture<ServerDecodedPlayerComponent>;
  let stats: PlaybackStatsService;
  let socket: FakeSocket;

  /** Stands in for the browser's WebSocket so a test can hand the component messages. */
  class FakeSocket {
    binaryType = '';
    onmessage: ((event: { data: unknown }) => void) | null = null;
    onerror: (() => void) | null = null;
    onclose: ((event: { code: number }) => void) | null = null;
    closed = false;

    close(): void {
      this.closed = true;
    }

    send(data: unknown): void {
      this.onmessage?.({ data });
    }
  }

  beforeEach(async () => {
    socket = new FakeSocket();
    (globalThis as unknown as { WebSocket: unknown }).WebSocket = function () {
      return socket;
    };

    await TestBed.configureTestingModule({
      imports: [ServerDecodedPlayerComponent],
    }).compileComponents();

    stats = TestBed.inject(PlaybackStatsService);
    fixture = TestBed.createComponent(ServerDecodedPlayerComponent);
    fixture.componentRef.setInput('socketPath', '/ws/frames/abc');
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
  });

  function init(width = 2, height = 2): void {
    socket.send(JSON.stringify({ type: 'init', codec: 'mjpeg', width, height, frameRate: 25 }));
  }

  /** One binary message in the wire format: flags, big-endian microseconds, then the payload. */
  function picture(timestampUs: number, bytes: Uint8Array): ArrayBuffer {
    const message = new Uint8Array(9 + bytes.length);
    const view = new DataView(message.buffer);

    message[0] = 0x01;
    view.setBigUint64(1, BigInt(timestampUs));
    message.set(bytes, 9);

    return message.buffer;
  }

  function jpeg(): Uint8Array {
    return Uint8Array.from(atob(JPEG_BASE64), (c) => c.charCodeAt(0));
  }

  it('sizes the canvas from the init message', () => {
    init(640, 480);

    const canvas = fixture.nativeElement.querySelector('canvas') as HTMLCanvasElement;
    expect(canvas.width).toBe(640);
    expect(canvas.height).toBe(480);
  });

  it('decodes a real picture and counts it', async () => {
    init();
    socket.send(picture(40_000, jpeg()));

    await settle();

    expect(stats.stats().fps).toBe(0, 'the sampler has not ticked yet');
    expect(statsInternals().frames).toBe(1);
  });

  it('drops a picture that will not decode without failing the stream', async () => {
    // Every image is independent, so one bad picture costs exactly that picture — there is no
    // reference chain to repair and no reason to tear anything down.
    let failure: string | null = null;
    fixture.componentInstance.failed.subscribe((message: string) => (failure = message));

    init();
    socket.send(picture(40_000, new Uint8Array([0xff, 0xd8, 0x00, 0x01, 0xff, 0xd9])));

    await settle();

    expect(failure).toBeNull();
    expect(statsInternals().dropped).toBe(1);
  });

  it('never steps backwards when pictures finish out of order', async () => {
    // createImageBitmap is asynchronous, so two can be in flight at once. Drawing the older one
    // last would rewind the picture by a frame — rare, and easily mistaken for a decoder fault.
    init();
    socket.send(picture(80_000, jpeg()));
    await settle();

    socket.send(picture(40_000, jpeg()));
    await settle();

    expect(statsInternals().dropped).toBe(1, 'the late arrival was counted rather than drawn');
  });

  it('reports the end of a file rather than an error', () => {
    let ended = false;
    fixture.componentInstance.ended.subscribe(() => (ended = true));

    socket.onclose?.({ code: 1000 });

    expect(ended).toBeTrue();
  });

  it('closes the socket when it goes away', () => {
    fixture.destroy();

    expect(socket.closed).toBeTrue();
  });

  /** Lets the pending createImageBitmap promises resolve. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 10; i++) {
      await Promise.resolve();
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
  }

  /** The counters the service accumulates between samples. */
  function statsInternals(): { frames: number; dropped: number } {
    const service = stats as unknown as { frames: number; dropped: number };
    return { frames: service.frames, dropped: service.dropped };
  }
});
