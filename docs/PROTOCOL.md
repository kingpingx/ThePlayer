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

## Status: Phase 0

Only `GET /api/health` exists today. The rest is specified here as it is built, phase by phase, so
that this document and the code stay in step rather than diverging.

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

## Planned — Phase 1

### `POST /api/watch`

Starts or joins a broadcast. The client reports what it can decode, and that drives whether the
server converts the stream at all.

```jsonc
// request
{
  "address": "rtsp://user:pass@192.168.1.64:554/stream",
  "mode": "ClientDecoded",
  "clientDecodeSupport": {
    "webCodecs": true,
    "codecs": [ { "codec": "H265", "supported": true, "hardwareAccelerated": true } ]
  }
}
```

```jsonc
// response
{
  "viewerId": "8f14e45fceea167a",
  "mode": "ClientDecoded",
  "format": { "codec": "H265", "width": 1920, "height": 1080, "frameRate": 25, "live": true },
  "converted": false,              // true when the server had to transcode
  "modeAvailability": [ … ],       // per mode: offerable, and if not, why not
  "transport": {                   // shape depends on mode
    "kind": "WebSocket",
    "url": "/ws/frames/8f14e45fceea167a"
  }
}
```

The request address **is never echoed back.** Responses carry the redacted form only.

### `DELETE /api/watch/{viewerId}`

Detaches. The broadcast lingers briefly after the last viewer leaves — default 10 seconds,
configurable — so a page refresh does not tear down and rebuild the FFmpeg process.

---

## Planned — Phase 2

### `WS /ws/frames/{viewerId}`

One JSON text message, then binary frames.

```jsonc
// first message - configures VideoDecoder
{
  "type": "init",
  "codec": "hev1.1.6.L93.B0",     // RFC 6381
  "width": 1920,
  "height": 1080,
  "frameRate": 25
}
```

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
