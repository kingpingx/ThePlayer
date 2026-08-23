# Roadmap — Phases 2 to 6

Where the project is going, and the technical detail needed to build it.

Phases 0 and 1 are done (`v0.1.0`, `v0.2.0`): the solution, the credential-safe `MediaAddress`,
MediaMTX supervision, verified hardware detection, the watch endpoints, reference-counted
broadcasts, and H.264 playback over WebRTC.

Everything below has been checked against the real toolchain on this machine rather than assumed.
Where a measurement contradicted the original design, the design changed — those places are marked
**Verified** and say what was actually observed.

---

## Prerequisite, still outstanding

```powershell
winget install OpenJS.NodeJS.LTS     # → Node 22.x
```

Node 20.9.0 is below what any current Angular CLI accepts (Angular 19 needs `≥ 20.11.1`, Angular 22
needs `≥ 22.22.3`). **Phase 2 cannot start without this** — it is the phase that scaffolds the
player. The WHEP harness at `/` stands in until then, and remains afterwards as a dependency-free
fallback.

---

# Phase 2 — Client-side decoding and the capability panel · `v0.3.0`

**Goal:** the server stops touching the video. FFmpeg copies bytes, the browser decodes them with
its own GPU through WebCodecs, and the UI shows what it can decode and why each mode is offered.

This is the largest phase. It adds a second transport, a second player, and the entire Angular
workspace.

## What gets built

| Component | Project | Job |
|---|---|---|
| `FFmpegStreamingPipeline` | Infrastructure | Spawn FFmpeg, read stdout, publish frames |
| `IFrameReader`, `CompressedFrameReader` | Infrastructure | Split the byte stream into whole access units |
| `CodecStringBuilder` | Infrastructure | Derive the RFC 6381 string from parameter sets |
| `FrameBroadcaster` | Application | One pipeline → many clients, bounded, drop-oldest |
| `VideoStreamSocket` | Api | `WS /ws/frames/{viewerId}` |
| Angular workspace | Player | Everything below |

## Key technical decisions

### Frame boundaries without a codec parser

FFmpeg is asked to insert Access Unit Delimiters, so splitting the stream is a scan for one NAL
type rather than a slice-header parser:

```
-c:v copy -bsf:v "h264_metadata=aud=insert,dump_extra=freq=keyframe" -f h264 -
```

H.265 uses `hevc_metadata` with the same options.

**Verified.** Against a 750-frame file this produced exactly **750 access units** — one AUD per
frame, no ambiguity.

### `dump_extra` is not optional

**Verified, and this was nearly missed.** With AUD insertion alone the elementary stream contained
15 IDR frames and **zero SPS/PPS**. Copying from an MP4 leaves parameter sets in the container's
`avcC` box, so they never reach the elementary stream — and WebCodecs cannot configure a decoder
without them. Adding `dump_extra=freq=keyframe` puts SPS and PPS in front of every keyframe:

```
before:  {slice: 735, IDR: 15, SEI: 1, AUD: 750}                    ← undecodable
after:   {slice: 735, IDR: 15, SEI: 1, AUD: 750, SPS: 15, PPS: 15}  ← decodable
```

This also makes late joiners work: a client attaching mid-stream gets parameter sets at the next
keyframe without any special handling.

### Start codes come in two lengths

**Verified.** In the same stream the AUD used a 4-byte start code (`00 00 00 01`) while every other
NAL used 3 bytes (`00 00 01`). A reader scanning only for 4-byte codes finds the AUDs and misses
SPS, PPS and every slice. `CompressedFrameReader` must accept both.

### Deriving the codec string

For H.264 this is three bytes. The SPS NAL header is one byte, and `profile_idc`,
`constraint_flags` and `level_idc` follow immediately:

```
SPS payload 42 c0 1f   →   codec string "avc1.42c01f"
```

**Verified** against the test stream.

H.265 is genuinely harder and is the fiddliest work in this phase. `profile_tier_level` sits inside
the SPS behind a 2-byte NAL header and 8 bits of preamble, and its 96 bits have to be read
bit-by-bit. Two complications: emulation-prevention bytes (`0x03`) must be stripped from the RBSP
first, and the 32 compatibility flags are emitted in reverse bit order in the codec string.

**Plan:** a focused bit reader, roughly 80 lines, with unit tests against captured SPS bytes from
both an H.264 and an H.265 stream. If parsing fails, fail loudly rather than guessing a plausible
string — a wrong level makes `configure()` reject the stream, and a silent guess turns a clear
error into a mystery.

### WebSocket framing

One JSON text message, then binary frames. Each binary message is one complete access unit with a
9-byte header:

```
byte  0     flags        bit 0 = keyframe
bytes 1-8   timestamp    microseconds, big-endian u64
bytes 9+    payload      Annex-B access unit
```

The init message carries what `VideoDecoder.configure()` needs:

```jsonc
{ "type": "init", "codec": "avc1.42c01f", "width": 1280, "height": 720, "frameRate": 25 }
```

### Timestamps are synthesised

Raw elementary-stream output carries no presentation timestamps, so they are generated
monotonically from the inspected frame rate. Sufficient for live playback where frames render on
arrival. Reading real PTS from an MPEG-TS output is the documented upgrade path if A/V sync or
seeking is ever needed — recorded in `PROTOCOL.md`, not built.

### Backpressure

`FrameBroadcaster` gives each client a bounded `Channel<T>` with `BoundedChannelFullMode.DropOldest`,
so a slow client can never stall the pipeline that feeds everyone else. After a drop the client is
marked out-of-sync and skipped until the next keyframe, so it never renders from a broken reference
chain.

## The Angular workspace

```
src/ThePlayer.Player/
  src/app/core/
    client-capabilities.service.ts    probes VideoDecoder; wraps it so tests can fake a browser
    watch.service.ts                  POST /api/watch, DELETE on teardown
    playback-stats.service.ts         fps, decode time, dropped frames, queue depth
    models.ts                         the protocol types from PROTOCOL.md
  src/app/features/
    address-bar/                      paste an RTSP URL or file path
    capability-panel/                 what this browser can decode, and why
    video-player/
      server-assisted-player.component.ts   WebRTC → <video>   (ports wwwroot/index.html)
      client-decoded-player.component.ts    WebCodecs → <canvas>
      decode-mode-toggle.component.ts       greyed options carry their reason
  src/environments/                   development · staging · production
  proxy.conf.json                     /api and /ws → localhost:5172 during development
```

`client-capabilities.service` wraps `VideoDecoder` behind an injectable, so unit tests can present a
browser that decodes H.265, one that does not, one with software-only support, and one with no
WebCodecs at all — and assert the panel and toggle each show the right state and reason.

Rendering uses `canvas.getContext('2d').drawImage(videoFrame, …)`, which accepts a `VideoFrame`
directly. Every frame must be `.close()`d — WebCodecs frames hold GPU memory and are not garbage
collected, so a missed close leaks until the decoder stalls. This is the single easiest thing to get
wrong in the player.

## Also in this phase

Remove the `ClientDecoded` branch of `RejectIfNotYetImplemented`, and extend the credential-leak
test to cover a live broadcast: assert a deliberately wrong password appears in no log line, no API
response, and no client-facing error.

## Verification

- Toggle client ↔ server decoding on a live stream; video continues in both.
- Task Manager shows server CPU/GPU fall to near zero on the client path.
- The capability panel matches `chrome://gpu` under "Video Acceleration Information".
- A faked browser with no H.265 support makes the toggle explain itself rather than going dead.
- Three tabs share one FFmpeg process; throttling one drops its frames without affecting the others.

## Risks

| Risk | Mitigation |
|---|---|
| H.265 codec-string parsing is wrong | Unit tests against captured SPS bytes; fail loudly on parse failure |
| Leaked `VideoFrame`s stall the decoder | `try/finally` close on every frame; a queue-depth stat that makes a leak visible |
| Synthesised timestamps drift on a variable-rate camera | Frame rate is read from `avg_frame_rate`, not the container's nominal rate |

---

# Phase 3 — Video conversion · `v0.4.0`

**Goal:** H.265 plays everywhere. Converted for clients that need it, passed through untouched for
clients that do not.

## What gets built

| Component | Job |
|---|---|
| `MediaMtxPublishingPipeline` | Decode + re-encode → publish to MediaMTX over RTSP |
| `FFmpegArgumentBuilder` | Turn a `BroadcastPlan` and an `AccelerationProfile` into a command line |
| Encoder fallback | Drop to the next profile when the preferred one fails at runtime |

`BroadcastPlanner` already implements the full negotiation table and is already unit-tested — this
phase makes the plans it produces executable. `RejectIfNotYetImplemented` loses its conversion
branch.

## Key technical decisions

### Keep frames on the GPU

```
-hwaccel cuda -hwaccel_output_format cuda -i <input> -c:v h264_nvenc -preset p1 -tune ll
```

`-hwaccel_output_format cuda` is the important flag: without it every frame is copied out of GPU
memory and back in, which costs more than the encode.

**Verified.** A full H.265 → H.264 transcode on the T550 produced stable readings of:

```
gpu 21%   encoder 99%   decoder 45%   176 MB
```

Both engines lit, which is exactly what Phase 4 will display.

### Latency tuning

`-preset p1 -tune ll -bf 0 -g 30`. B-frames are disabled deliberately: they reorder output and add
at least one frame of latency for no benefit on a live feed.

### Runtime fallback

Detection at startup proves an encoder *exists*; it does not prove a session can be opened right
now. NVENC has a concurrent-session limit — 3 to 8 on consumer GeForce cards, unlimited on
professional cards like the T550 — so the fourth simultaneous broadcast on a consumer GPU fails at
`avcodec_open2`, not at startup.

The pipeline therefore catches an encoder-open failure, logs which profile failed and why, and
retries with the next profile down the ranking. A machine that has silently fallen back to
`libx264` is visible in `/api/health` rather than merely slow.

### Two conversion targets

Conversion feeds two different destinations, which is why `FFmpegArgumentBuilder` is a separate
class from either pipeline:

- **`ServerAssisted`** → re-encode and publish to MediaMTX over RTSP, played via WHEP.
- **`ClientDecoded`** → re-encode straight to Annex-B on stdout, played through the Phase 2 frame
  socket. Same encoder settings, different output muxer.

## Verification

- An H.265 file plays in `ServerAssisted` (converted) and in `ClientDecoded` (passthrough on a
  browser that reports H.265, converted on one that does not).
- Forcing the probe to report no H.265 flips the planner to conversion — and `converted` in the
  watch response says so.
- Overriding the profile ranking to software confirms a graceful `libx264` fallback.
- `nvidia-smi` shows encoder utilisation rise when and only when `converted` is true.

---

# Phase 4 — Server CPU and GPU in the client · `v0.5.0`

**Goal:** the browser shows what the server is doing, live. Toggle a mode and watch the cost move.

## What gets built

| Component | Job |
|---|---|
| `SystemMetricsReaders` | Windows performance counters · Linux `/proc/stat` |
| `NvidiaGpuReader` | `nvidia-smi`, including the encoder/decoder split |
| `UnavailableGpuReader` | Null Object for non-NVIDIA hardware |
| `ProcessMetricsReader` | Per-broadcast CPU, no platform code |
| `MetricsCollector` | Sample on a timer, fan one snapshot to every client |
| `GET /api/metrics/stream` | Server-Sent Events |
| `resource-monitor` | Angular, beside the client's own decode stats |

## Key technical decisions

### Single-shot `nvidia-smi`, not streaming — a correction

The original design called for one long-lived `nvidia-smi --loop-ms=1000` process, to avoid
spawning one per second.

**Verified, and it does not work.** On this GPU the loop modes return a good first sample and then
fail:

```
$ nvidia-smi --query-gpu=utilization.gpu,utilization.encoder,utilization.decoder -l 1
0, 0, 0
[Unknown Error], [Unknown Error], [Unknown Error]
[Unknown Error], [Unknown Error], [Unknown Error]
```

`--loop-ms=500` behaves the same way. Single-shot invocation, repeated, is completely stable:

```
$ nvidia-smi --query-gpu=... --format=csv,noheader,nounits    # under NVENC load, four samples
21, 99, 45, 176
21, 99, 45, 176
21, 99, 45, 176
21, 99, 45, 176
```

So: **one process per sample**, at a configurable interval defaulting to one second. That costs a
process spawn per second — roughly 30 ms of CPU — which is a real cost, honestly worse than the
original design, and worth it for readings that are correct. The interval is configurable, and
sampling stops entirely when no client is listening to the SSE stream.

The reader must also treat `[Unknown Error]` as *unavailable*, not as zero. Reporting an idle GPU
when the query failed is exactly the plausible-looking wrong number this design is meant to avoid.

### Per-broadcast cost is the interesting number

System-wide CPU tells you the machine is busy. Per-broadcast CPU tells you *this stream costs 2% on
the client path and 61% on the server path*, which is the comparison the whole project exists to
make. It is also the easier of the two: `Process.TotalProcessorTime` deltas over elapsed wall time
work identically on Windows and Linux with no platform code at all.

### SSE, not WebSocket

The flow is one-way, text and periodic — what SSE is for — and `EventSource` gives automatic
reconnection where a WebSocket needs hand-written ping/pong. It also keeps the frame socket
dedicated to binary video, so a metrics hiccup cannot disturb playback.

One collector samples and fans the same snapshot to every connected client: ten tabs cost one
sample, not ten.

### GPU support is NVIDIA-only, stated plainly

`intel_gpu_top` is Linux-only and usually needs elevated privileges; `rocm-smi` is Linux-only;
Windows exposes GPU engine data only through awkward per-process performance counters. On
non-NVIDIA hardware the UI shows GPU metrics as unavailable **with the reason**. CPU and
per-broadcast metrics work everywhere.

## Verification

- The monitor tracks Task Manager and `nvidia-smi` under load.
- Toggling `ServerAssisted` → `ClientDecoded` on an H.265 feed drops encoder and decoder to zero,
  live in the browser.
- Killing `nvidia-smi` mid-run degrades the UI to "unavailable" with a reason rather than freezing
  on a stale number.
- Ten open tabs still produce one sample per interval.

---

# Phase 5 — Full server decoding and the three-way comparison · `v0.6.0`

**Goal:** the third mode — the only one where the client decodes no video at all — and the diagnostics
overlay that makes all three comparable.

## What gets built

| Component | Job |
|---|---|
| `JpegPictureReader` | Second `IFrameReader`, splitting on SOI/EOI |
| `server-decoded-player` | `createImageBitmap` → canvas |
| Diagnostics overlay | Client and server numbers side by side, all three modes |

**This is a small phase, by design.** Phase 2 built `FFmpegStreamingPipeline` parameterised by
FFmpeg arguments and a frame reader, so a third mode is new arguments plus a second `IFrameReader` —
no existing class edited. That is the payoff from merging the two frame pipelines during planning
rather than writing two near-duplicates.

## Key technical decisions

### JPEG framing

```
-f mjpeg -q:v 5
```

JPEG images are self-delimiting: `FF D8` starts one, `FF D9` ends it. No delimiter injection needed,
and no parameter sets — every picture is independent, so there is no reference chain to break and a
late joiner can start at any frame.

### Bandwidth is the cost

MJPEG has no inter-frame compression, so expect 5–10× the bandwidth of H.264 at comparable quality.
`-q:v` and an optional `-vf scale` are exposed in configuration, because on a LAN the tradeoff is
acceptable and over a WAN it is not.

### The overlay is the point

| | measured on | shows |
|---|---|---|
| Client | browser | fps, decode time per frame, dropped frames, queue depth, bitrate |
| Server | host | CPU, GPU, encoder/decoder split, this broadcast's own CPU |

Cycling the three modes should show the cost profile invert: server GPU highest on `ServerDecoded`,
near zero on `ClientDecoded`, client decode time doing the opposite and reaching zero only on
`ServerDecoded`.

## Verification

- All three modes on one address show the fully inverted cost profile.
- Three or more tabs share one pipeline; a throttled tab drops frames without affecting the others.
- A file reaching its last frame moves the broadcast to `Ended` and the player says so.

---

# Phase 6 — Deployment and hardening · `v1.0.0`

**Goal:** run it somewhere other than a development machine, safely.

## What gets built

| Area | Work |
|---|---|
| Containers | `docker-compose.yml` for Linux, NVIDIA Container Toolkit, host networking |
| Process lifetime | Job Object (Windows) / `PR_SET_PDEATHSIG` (Linux) |
| Auth | API key, required in Production |
| LAN access | `webrtcAdditionalHosts` for ICE candidates clients can actually reach |
| Docs | README, `HARDWARE.md`, release notes per tag |

## Key technical decisions

### Kill children with the parent

**A known gap since Phase 0.** A force-kill or crash of the server leaves MediaMTX running and
holding ports 8554, 8889 and 9997. The supervisor already adopts a survivor on the next start, which
makes this a nuisance rather than a failure — but the real fix is a Windows **Job Object** with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, and `PR_SET_PDEATHSIG` on Linux. Both guarantee children die
with the parent even on an unhandled termination that runs no cleanup.

This covers FFmpeg pipelines and `nvidia-smi` as well as MediaMTX.

### Windows stays native

Docker Desktop for Windows NATs container traffic, which makes WebRTC ICE painful: the container's
view of its own address is not one a client can reach. `docker-compose.yml` targets Linux with host
networking; the documented Windows path stays native. This is a real limitation, documented rather
than worked around.

### GPU in a container

`nvidia-smi` inside a container needs the NVIDIA Container Toolkit and device visibility — the same
prerequisite NVENC already has. So GPU metrics and GPU encoding succeed or fail together, and
`HARDWARE.md` documents them as one setup step rather than two.

### Auth matters more here than it looks

The server connects to whatever address it is given, which is a server-side request forgery surface.
On a LAN that is acceptable. Exposed further it is not, which is why the API key is a Phase 6
deliverable rather than a nice-to-have. The metrics endpoint moves behind it too — server resource
metrics describe the host's hardware and load.

## Verification

- `docker compose up` on Linux serves all three modes to a remote browser on the LAN, with the
  resource monitor still reporting GPU from inside the container.
- The Windows native path still works.
- Killing the server — including with `taskkill /F` — leaves no orphaned FFmpeg, MediaMTX or
  `nvidia-smi` processes.

---

# Still out of scope

Called out so these stay your decisions rather than my omissions.

- **Transport controls for files** — no seek, pause or scrub bar. WebRTC has no seek concept, so the
  feature would work in one mode and not the others. A real file player is a phase of its own.
- **Audio** — video only. `ClientDecoded` and `ServerDecoded` have no audio path; audio needs a
  parallel `AudioDecoder` pipeline and a second transport stream. `ServerAssisted` carries audio for
  free over WebRTC, so the modes would be inconsistent — which is the actual reason it is deferred.
- **Recording** — nothing is written to disk.
- **WebTransport** — WebSocket over TCP is fine on a LAN but suffers head-of-line blocking on lossy
  networks, where one lost packet delays every frame behind it. WebTransport over HTTP/3 is the
  better long-term transport for the client-decode path. Noted in `PROTOCOL.md`.
- **Multiple cameras on one page** — the architecture supports it (broadcasts are already shared and
  reference-counted); only the UI assumes one player at a time.

Say the word on any of these and I will scope it as an additional phase.
