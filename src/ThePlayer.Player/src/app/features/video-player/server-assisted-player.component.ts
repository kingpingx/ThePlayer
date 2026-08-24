import {
  Component,
  ElementRef,
  OnDestroy,
  input,
  output,
  viewChild,
} from '@angular/core';

/** ICE gathering can hang on a slow interface. Play with what we have rather than waiting forever. */
const ICE_TIMEOUT_MS = 3000;

/**
 * Plays a stream through WebRTC and a plain `<video>` element.
 *
 * A port of the WHEP harness at `wwwroot/index.html`, which stays in the repository as a
 * dependency-free fallback and as the reference for this component.
 *
 * Note that the client still decodes here - every WebRTC `<video>` does, on the GPU. The mode is
 * called *assisted* because the server's job is to convert the stream into something the browser's
 * built-in pipeline handles easily, not because decoding moves anywhere.
 */
@Component({
  selector: 'app-server-assisted-player',
  standalone: true,
  template: `
    <video #video class="surface" autoplay playsinline muted></video>
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
export class ServerAssistedPlayerComponent implements OnDestroy {
  readonly whepUrl = input.required<string>();
  readonly failed = output<string>();

  private readonly videoRef = viewChild.required<ElementRef<HTMLVideoElement>>('video');
  private connection: RTCPeerConnection | null = null;

  async ngOnInit(): Promise<void> {
    try {
      await this.play();
    } catch (error) {
      this.failed.emit((error as Error).message);
    }
  }

  ngOnDestroy(): void {
    this.connection?.close();
    this.connection = null;
  }

  private async play(): Promise<void> {
    const connection = new RTCPeerConnection({ iceServers: [] });
    this.connection = connection;

    connection.addTransceiver('video', { direction: 'recvonly' });
    connection.ontrack = (event) => {
      this.videoRef().nativeElement.srcObject = event.streams[0];
    };

    await connection.setLocalDescription(await connection.createOffer());
    await this.waitForIce(connection);

    const response = await fetch(this.whepUrl(), {
      method: 'POST',
      headers: { 'Content-Type': 'application/sdp' },
      body: connection.localDescription?.sdp ?? '',
    });

    if (!response.ok) {
      throw new Error(`The WebRTC handshake failed (${response.status}).`);
    }

    await connection.setRemoteDescription({ type: 'answer', sdp: await response.text() });
  }

  /**
   * Waits for ICE gathering rather than trickling.
   *
   * WHEP supports trickle over PATCH, but waiting costs a fraction of a second on a LAN and removes
   * a whole class of ordering bugs.
   */
  private waitForIce(connection: RTCPeerConnection): Promise<void> {
    if (connection.iceGatheringState === 'complete') {
      return Promise.resolve();
    }

    return new Promise((resolve) => {
      const finish = () => {
        clearTimeout(timer);
        connection.removeEventListener('icegatheringstatechange', onChange);
        resolve();
      };

      const onChange = () => {
        if (connection.iceGatheringState === 'complete') {
          finish();
        }
      };

      const timer = setTimeout(finish, ICE_TIMEOUT_MS);
      connection.addEventListener('icegatheringstatechange', onChange);
    });
  }
}
