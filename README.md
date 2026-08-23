# ThePlayer

Play any RTSP feed or video file in Chrome — whatever codec it happens to be in — and choose
**where the decoding happens**.

Give it an address (`rtsp://user:pass@camera/stream`, or a path to a video file) and it plays in the
browser. An H.265 feed plays in a browser that cannot decode H.265. A browser that *can* decode
H.265 gets the original bytes untouched, and the server's conversion cost drops to zero.

> **Status:** Phase 0 (`v0.1.0`) — foundation. The solution, the credential-safe address type,
> MediaMTX supervision and the health endpoint are in place. Video does not play yet; that is
> Phase 1. See [Roadmap](#roadmap).

---

## The idea

Three pipelines, differing in how much codec work the **server** does:

| | `ClientDecoded` | `ServerAssisted` | `ServerDecoded` |
|---|---|---|---|
| **UI label** | Decode on client | Decode on server (assisted) | Decode on server (full) |
| Server decodes | no | only when converting | **yes, always** |
| Server encodes | no | only when converting | yes, to JPEG |
| **Client decodes** | **yes** — the original codec | **yes** — easy H.264 | **no** |
| Transport | WebSocket → WebCodecs | WebRTC | WebSocket |
| Renders to | `<canvas>` | `<video>` | `<canvas>` |
| Server GPU load | almost none | high when converting | highest |
| Bandwidth | low | lowest | 5–10× higher |

`ServerAssisted` **still decodes in the client** — every WebRTC `<video>` does, on the GPU. It is
called *assisted* because the server's job is to convert the stream into something the client's
built-in pipeline handles easily. Only `ServerDecoded` genuinely removes video decoding from the
client.

So why build `ClientDecoded` at all, if WebRTC already decodes on the client GPU?

**Because it deletes the conversion.** WebRTC negotiates codecs in SDP, and most browsers will not
accept H.265 there — so an H.265 feed *must* be transcoded, burning server GPU. Over a WebSocket
the transport is codec-agnostic: a client that reports H.265 support gets the original bytes and
the server does no codec work at all. The client capability probe therefore drives **conversion**,
not just rendering.

The player shows both halves of that trade live — what this browser can decode, and what the
server's CPU and GPU are doing right now — so the cost moving between them is visible rather than
asserted.

---

## Getting started

### Prerequisites

| | Needed for | Check |
|---|---|---|
| .NET SDK 8 | the backend | `dotnet --version` |
| FFmpeg + ffprobe | all codec work | `ffmpeg -version` |
| Node 22 LTS | the Angular player (Phase 1+) | `node --version` |

Node 20.9 and earlier will **not** work — no current Angular CLI accepts it (Angular 19 needs
`≥ 20.11.1`, Angular 22 needs `≥ 22.22.3`).

```powershell
winget install OpenJS.NodeJS.LTS
```

### Run it

```bash
# 1. Fetch the MediaMTX binary (not committed; downloaded per platform)
./tools/fetch-mediamtx.ps1      # Windows
./tools/fetch-mediamtx.sh       # Linux / macOS

# 2. Start the server - it launches and supervises MediaMTX itself
dotnet run --project src/ThePlayer.Api

# 3. Confirm it came up
curl http://localhost:5172/api/health
```

A healthy response reports the MediaMTX process, the FFmpeg version, and the acceleration
profiles **confirmed working on this machine**:

```json
{
  "healthy": true,
  "environment": "Development",
  "mediaServer": { "state": "Running", "version": "v1.20.1", "restartCount": 0, "error": null },
  "hardware": {
    "ffmpegVersion": "8.0",
    "hasHardwareAcceleration": true,
    "preferredProfile": "NVIDIA NVENC/NVDEC",
    "profiles": [ { "kind": "Nvidia", "encoder": "h264_nvenc", "decodeAccelerator": "cuda" } ],
    "decodableCodecs": ["H264", "H265", "Vp8", "Vp9", "Av1", "Mjpeg"]
  }
}
```

**Confirmed** is meant literally. An FFmpeg build lists every encoder it was compiled with,
regardless of whether this machine has the device to run it — a stock Windows build advertises
`h264_vaapi` on hardware that has no VA-API at all. So each hardware encoder is verified at startup
by actually encoding a couple of frames with it, and only the ones that succeed are offered. Set
`FFmpeg:VerifyEncoders` to `false` to skip that (roughly a second, once).

### Test it

```bash
dotnet test                                              # everything
dotnet test tests/ThePlayer.Architecture.Tests           # the dependency rule alone
```

---

## How it is put together

Clean Architecture, dependency rule pointing strictly inward:

```
        Api  ────────────┬─────────────▶  Infrastructure
         │               │                      │
         └──────────▶ Application ◀─────────────┘
                         │
                         ▼
                      Domain          (zero package references)
```

The arrow from Infrastructure *into* Application is the important one: Infrastructure **implements**
interfaces that Application **owns**. That inversion is what lets FFmpeg and MediaMTX be swapped,
faked in tests, or rewritten in Go without the use cases noticing.

| Project | Holds |
|---|---|
| `ThePlayer.Domain` | Types and rules. No packages at all — enforced by a test. |
| `ThePlayer.Application` | Use cases and the ports Infrastructure must satisfy. |
| `ThePlayer.Infrastructure` | FFmpeg, MediaMTX, sockets, OS counters. |
| `ThePlayer.Api` | ASP.NET Core host. The only place DI is configured. |
| `ThePlayer.Player` | Angular client *(Phase 1)*. |

Two conventions worth knowing before reading the code:

- **One file per concept, not per type.** Small records and enums live with the idea they belong
  to; anything with real behaviour gets its own file. This deviates from StyleCop's SA1402 on
  purpose.
- **Ports only where there is a boundary.** `BroadcastPlanner` and `FrameBroadcaster` are concrete
  classes, not interfaces. An interface with one implementation and nothing to substitute is
  indirection, not dependency inversion.

More in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

---

## Credentials

RTSP addresses arrive with the password inside the URL, so keeping it out of logs is a design
concern rather than a code-review one. `MediaAddress` is the only type that holds a credential, and
[docs/SECURITY.md](docs/SECURITY.md) lists every leak path and what closes it — including the one
most easily missed:

> FFmpeg echoes the input URL when a connection fails, so **a wrong password produces a log line
> containing the right one**. Every byte of process output is scrubbed before it is logged.

A test asserts a deliberately wrong password appears in no log line, no API response, and no
client-facing error.

Two residual risks are documented rather than papered over: the FFmpeg command line is visible to
other processes of the same user, and the server will connect to whatever address it is given.

---

## Roadmap

| Phase | | Tag |
|---|---|---|
| 0 | Foundation — solution, `MediaAddress`, MediaMTX supervision, health, CI | `v0.1.0` |
| 1 | First pixels — H.264 RTSP feeds and files play via WebRTC | `v0.2.0` |
| 2 | Client-side decoding and the capability panel | `v0.3.0` |
| 3 | H.265 conversion and hardware detection | `v0.4.0` |
| 4 | Server CPU and GPU live in the browser | `v0.5.0` |
| 5 | Full server decoding and the three-way comparison | `v0.6.0` |
| 6 | Docker, auth, docs | `v1.0.0` |

**Deliberately out of scope:** transport controls for files (WebRTC cannot seek at all, so a scrub
bar would work in one mode and not the others), audio, recording, and WebTransport.

## Documentation

- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — layering, conventions, and why the model is as small as it is
- [PROTOCOL.md](docs/PROTOCOL.md) — the wire contract, frozen so the backend can be replaced
- [SECURITY.md](docs/SECURITY.md) — credential handling and the risks that remain
- [CHANGELOG.md](CHANGELOG.md) — what each tag contains
