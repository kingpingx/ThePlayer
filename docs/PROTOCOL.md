# Wire protocol

This document is the contract between the server and the Angular player. It is frozen here on
purpose: the backend is expected to be rewritten in Go, and the player should not have to change
when that happens. Types in `ThePlayer.Api/Contracts.cs` mirror this document, and exist separately
from the domain so that an internal rename is not a breaking API change.

Everything is JSON, camel-cased, over HTTP. Three transports:

| Transport | Carries | Why |
|---|---|---|
| REST | control — start and stop watching, health | request/response |
| WebSocket | binary video frames | codec-agnostic, unlike WebRTC's SDP negotiation |
| Server-Sent Events | server CPU/GPU metrics | one-way, periodic, free reconnection |

---

## Status: Phase 2

`GET /api/health`, `POST /api/watch`, `DELETE /api/watch/{viewerId}`, `GET /api/broadcasts` and
`WS /ws/frames/{viewerId}` all exist. The metrics stream is specified below as it will be built, so
this document and the code stay in step rather than diverging.

---

## `GET /api/health`

Returns `200` when the server can actually play something, `503` when it cannot. The status code
carries the verdict so container and uptime probes need not parse the body; the body explains why
either way.

```json
{
  "healthy": true,
  "environment": "Development",
  "mediaServer": {
    "state": "Running",
    "version": "v1.20.1",
    "restartCount": 0,
    "error": null
  },
  "hardware": {
    "ffmpegVersion": "8.0-full_build-www.gyan.dev",
    "hasHardwareAcceleration": true,
    "preferredProfile": "NVIDIA NVENC/NVDEC",
    "profiles": [
      {
        "kind": "Nvidia",
        "name": "NVIDIA NVENC/NVDEC",
        "isHardware": true,
        "encoder": "h264_nvenc",
        "decodeAccelerator": "cuda"
      }
    ],
    "decodableCodecs": ["H264", "H265", "Vp8", "Vp9", "Av1", "Mjpeg"]
  }
}
```

| Field | Notes |
|---|---|
| `healthy` | MediaMTX is usable **and** FFmpeg was found. Hardware acceleration is a performance concern, not a health one — software encoding still plays video. |
| `mediaServer.state` | `NotStarted` · `Starting` · `Running` · `Restarting` · `Failed` |
| `mediaServer.restartCount` | A climbing number is the signal that something is wrong even while `state` reads `Running`. |
| `mediaServer.error` | Already scrubbed of credentials. |
| `hardware.profiles` | Only profiles **verified by actually encoding with them**, best first. See [ARCHITECTURE.md](ARCHITECTURE.md#detection-never-assumption). |

`ffmpegVersion` is spelled out explicitly rather than derived: the default camel-case policy turns
`FFmpegVersion` into `fFmpegVersion`, which is not a name anyone should have to bind to.

---

## `POST /api/watch`

Starts a broadcast, or joins one already running. The client reports what it can decode, and that
drives whether the server converts the stream at all.

```jsonc
// request
{
  "address": "rtsp://user:pass@192.168.1.64:554/stream",   // or "D:/videos/clip.mp4"
  "mode": "ServerAssisted",                                 // ClientDecoded · ServerAssisted · ServerDecoded
  "clientDecodeSupport": {                                  // optional; omitting it rules out ClientDecoded
    "webCodecs": true,
    "codecs": [
      { "codec": "H264", "supported": true, "hardwareAccelerated": true },
      { "codec": "H265", "supported": true, "hardwareAccelerated": true }
    ]
  }
}
```

```jsonc
// 200
{
  "viewerId": "f35d7ffedacb495f",
  "mode": "ServerAssisted",
  "format": {
    "codec": "H264", "width": 1280, "height": 720,
    "frameRate": 25, "live": false, "durationSeconds": 30
  },
  "converted": false,
  "modeAvailability": [
    { "mode": "ClientDecoded",  "available": true,  "reason": null },
    { "mode": "ServerAssisted", "available": true,  "reason": null },
    { "mode": "ServerDecoded",  "available": true,  "reason": null }
  ],
  "transport": {
    "kind": "WebRtc",
    "url": "http://127.0.0.1:8889/f5b0da8732474e24-serverassisted-h264/whep"
  }
}
```

| Field | Notes |
|---|---|
| `converted` | The most interesting number in the system. `false` means the server is copying bytes and its codec cost is zero. |
| `format.live` | `true` for a camera, `false` for a file — a file reaches a last frame and ends. |
| `modeAvailability` | Every mode, whether it can be used for *this* stream on *this* client, and if not, a sentence the UI can show as-is. |
| `transport.kind` | `WebRtc` → POST an SDP offer to `url`. `WebSocket` → open a frame socket at `url`. |

**The address is never echoed back.** Not in the response, not in errors, not in
`GET /api/broadcasts`.

### Errors

All failures are RFC 7807 problem documents whose `detail` is written to be shown to a user, and is
always scrubbed of credentials.

| Status | When |
|---|---|
| `400` | The address or mode could not be parsed, or the mode is unavailable for this stream and client. |
| `501` | A valid request this phase cannot serve yet — `detail` names the phase that will. |
| `502` | The upstream could not be read: unreachable, wrong credentials, or not a video. |

```jsonc
// 502 - note both the address and FFmpeg's echo of it are redacted
{
  "title": "Could not read the stream",
  "status": 502,
  "detail": "rtsp://admin:***@192.168.1.64:554/stream did not respond."
}
```

## `DELETE /api/watch/{viewerId}`

Detaches. Always `204`, even for a viewer id that does not exist — a closing tab may send both a
beacon and a socket close, and the second is not an error.

The broadcast is not torn down immediately. It lingers after its last viewer leaves (default 10
seconds, configurable) so that a page refresh re-attaches to the running pipeline instead of
tearing down an FFmpeg process and immediately rebuilding it.

## `GET /api/broadcasts`

What is live right now — a diagnostic surface. Addresses are redacted, which matters here more than
anywhere else: this would otherwise be the easiest place in the system to read a camera password.

```jsonc
[
  {
    "key": "f5b0da8732474e24-serverassisted-h264",
    "address": "rtsp://admin:***@192.168.1.64:554/stream",
    "state": "Live",                  // Starting · Live · Ended · Failed
    "mode": "ServerAssisted",
    "converted": false,
    "viewers": 2,                     // one pipeline, two tabs
    "format": { "codec": "H264", "width": 1280, "height": 720, "frameRate": 25, "live": true }
  }
]
```

### How broadcasts are shared

The key is `{address fingerprint}-{mode}-{output codec}[-converted]`. Two viewers wanting the same
bytes share one pipeline; two clients needing *different* output — one that can decode H.265 and
one that cannot — get their own pipelines from the same upstream.

The fingerprint is a truncated SHA-256 of the address, never the address itself, so a password
cannot become a dictionary key or appear in this listing.

---

## `WS /ws/frames/{viewerId}`

One JSON text message, then binary frames. Offered when `transport.kind` is `WebSocket`.

Resolved before the upgrade, so a viewer id that is unknown - or one watching a WebRTC mode - gets
`404` rather than a socket that opens and immediately closes.

```jsonc
// first message - configures VideoDecoder
{
  "type": "init",
  "codec": "hev1.1.6.L93.90",     // RFC 6381, derived from the stream's own parameter sets
  "width": 1920,
  "height": 1080,
  "frameRate": 25
}
```

Each binary message that follows carries a nine-byte header:

```
byte  0     flags        bit 0 = keyframe
bytes 1-8   timestamp    microseconds, big-endian u64
bytes 9+    payload      Annex-B access unit
```

Big-endian because that is what `DataView.getBigUint64` reads by default.

The payload is Annex-B with start codes, and no `description` is sent - which is the form
`VideoDecoder` expects when its config carries none. Parameter sets are inlined in front of every
keyframe instead (`dump_extra=freq=keyframe`), so a client attaching mid-stream can configure a
decoder at the next keyframe with no special handling.

Each subsequent binary message is **one complete access unit** — never a partial frame, never two.
Frame boundaries are found by asking FFmpeg to insert Access Unit Delimiters
(`-bsf:v h264_metadata=aud=insert`) and splitting on those, rather than parsing slice headers.

> **Known simplification.** Raw elementary-stream output carries no presentation timestamps, so
> timestamps are synthesised monotonically from the inspected frame rate. That is sufficient for
> live playback where frames render on arrival. Reading real PTS values from an MPEG-TS output is
> the upgrade path if A/V sync or seeking is ever needed.

A slow client has its oldest frames dropped rather than stalling the pipeline, and resumes at the
next keyframe so it never renders from a broken reference chain.

---

## Planned — Phase 4

### `GET /api/metrics/stream` — Server-Sent Events

SSE rather than a WebSocket: the flow is one-way, text and periodic, and `EventSource` gives
automatic reconnection for free where a WebSocket needs hand-written ping/pong. It also keeps the
frame WebSocket dedicated to binary video, so a metrics hiccup cannot disturb playback.

```jsonc
{
  "cpuPercent": 12.4,
  "memoryUsedBytes": 8_100_000_000,
  "gpu": {
    "availability": "Available",   // or NotSupported · ToolMissing · NoPermission
    "overallPercent": 41,
    "encoderPercent": 38,          // NVENC - the number that proves conversion is happening
    "decoderPercent": 35,          // NVDEC
    "memoryUsedBytes": 1_200_000_000,
    "unavailableReason": null
  },
  "broadcasts": [
    { "fingerprint": "8f14e45fceea167a", "cpuPercent": 61.2, "memoryBytes": 190_000_000 }
  ]
}
```

Encoder and decoder utilisation are reported **separately** because that split is what makes the
three modes legible: `ServerAssisted` on an H.265 feed lights up both, `ClientDecoded` shows zero
on both.

GPU metrics are NVIDIA-only. Intel and AMD report `availability` with a reason rather than a
plausible-looking wrong number; CPU and per-broadcast metrics work everywhere.

---

## Not built

**WebTransport.** WebSocket over TCP is fine on a LAN but suffers head-of-line blocking on lossy
networks, where a single lost packet delays every frame behind it. WebTransport over HTTP/3 is the
better long-term transport for the client-decode path. Noted as the upgrade path, not built.
