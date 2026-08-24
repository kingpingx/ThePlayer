# Status

Where the project actually is, and where to pick it up.

[ROADMAP.md](ROADMAP.md) says what each phase *will* build, in build-ready detail. This file says
what is **built**, what is **not**, and what the next hour of work should be. Update it when a phase
lands.

*Last verified: 25 August 2026, against `39f5ca2`.*

---

## Where things stand

**Three phases done, one slice of a fourth.** An RTSP feed or a video file plays in a browser, and
you can choose whether the browser or the server does the decoding. An H.265 stream reaches a
browser that can decode it with the server doing **no codec work at all** — which is the thing the
project exists to demonstrate, and it works today.

| | Phase | Tag | State |
|---|---|---|---|
| ✅ | 0 · Foundation | `v0.1.0` | Done |
| ✅ | 1 · First pixels — H.264 over WebRTC | `v0.2.0` | Done |
| ✅ | 2 · Client-side decoding | `v0.3.0` | Done |
| 🟡 | 6 · Deployment *(partial)* | — | Container, Fly config and address policy done. **Auth not done.** |
| ⬜ | 3 · Conversion | `v0.4.0` | Not started — **the next phase** |
| ⬜ | 4 · Server metrics | `v0.5.0` | Not started |
| ⬜ | 5 · Full server decoding | `v0.6.0` | Not started |
| ⬜ | 6 · Hardening *(rest)* | `v1.0.0` | Auth, process lifetime, docs |

**141 tests, all passing** — 131 backend (Domain 38, Application 65, Infrastructure 18,
Architecture 5, Api 5) and 10 in the player.

Roughly 5,100 lines of C# across four projects, plus an 18-file Angular workspace.

### What still returns `501`

Two paths, and they are the honest measure of what is left:

```
ServerDecoded            → Phase 5
anything needing conversion → Phase 3
```

Everything else plays.

---

## Picking it up from cold

```bash
# One-time, if tools/bin is empty
./tools/fetch-mediamtx.ps1          # Windows
./tools/fetch-mediamtx.sh           # Linux / macOS

dotnet build
dotnet test                          # expect 131 passing

# Terminal 1 - the API. Launches and supervises MediaMTX itself.
dotnet run --project src/ThePlayer.Api          # http://localhost:5172

# Terminal 2 - the player
cd src/ThePlayer.Player && npm install && npm start   # http://localhost:4200
```

Then paste a full path to a video file, or an RTSP URL, and press Play.

**Confirm it is really working**, rather than merely running:

```bash
curl http://localhost:5172/api/health        # healthy:true, MediaMTX Running, profiles listed
curl http://localhost:5172/api/broadcasts    # what is live, and how many are watching each
```

The value to look for on a watch response is **`converted: false`** — it means the server is copying
bytes and its codec cost is zero. On an H.265 source in `ClientDecoded` mode, that is the whole
thesis in one field.

> **Windows gotcha.** Killing the API can leave MediaMTX holding ports 8554/8889/9997. The
> supervisor adopts a survivor on the next start rather than fighting it, so this is a nuisance
> rather than a failure — but `taskkill /F /IM mediamtx.exe` clears it. The real fix is a Job Object,
> in Phase 6.

---

## What each finished phase actually delivers

**Phase 0 — Foundation.** Four projects with the dependency rule enforced by a test rather than by
review. `MediaAddress`, the only type that holds a credential and the only one that can remove it
from text. MediaMTX supervised as a child process. Hardware detection that **verifies** each encoder
by actually encoding with it, because an FFmpeg build lists everything it was compiled with whether
or not the device exists.

**Phase 1 — First pixels.** `POST /api/watch`, the planner that decides what the server will do to a
stream, and reference-counted broadcasts so two tabs on one camera share one FFmpeg process. H.264
plays over WebRTC.

**Phase 2 — Client-side decoding.** The frame socket, an Annex-B reader, codec strings derived from
the stream's own parameter sets, and one pipeline fanned out to many viewers with drop-oldest
backpressure. Plus the Angular player. **H.265 now plays without the server touching it.**

**Phase 6, partially — Deployment.** A container carrying everything, `fly.toml`, and `AddressGuard`,
which bounds what the server will dial. Verified by building and running the image, not by
inspection.

---

## What is left

### Phase 3 — Conversion · `v0.4.0` · **next**

**Goal:** H.265 plays *everywhere*. Converted for clients that need it, passed through untouched for
clients that do not.

The negotiation is already built and unit-tested — `BroadcastPlanner` decides *when* to convert
today, and its answer is simply rejected. This phase makes those plans executable.

Where to start:

1. `FFmpegArgumentBuilder` — turn a `BroadcastPlan` plus an `AccelerationProfile` into a command
   line. Separate from either pipeline, because conversion feeds two different destinations.
2. `MediaMtxPublishingPipeline` — decode and re-encode, publish to MediaMTX over RTSP, for
   `ServerAssisted`.
3. Extend `FFmpegStreamingPipeline` — same encoder settings, Annex-B on stdout, for `ClientDecoded`.
   Its `NotSupportedException` guard on `plan.RequiresConversion` is where this lands.
4. Encoder fallback — detection proves an encoder *exists*, not that a session can be opened now.
   NVENC has a concurrent-session limit, so catch the open failure and drop to the next profile.
5. Delete the `RequiresConversion` branch of `RejectIfNotYetImplemented`.

`-hwaccel_output_format cuda` is the flag that matters: without it every frame is copied out of GPU
memory and back, which costs more than the encode. [ROADMAP.md](ROADMAP.md#phase-3--video-conversion--v040)
has the verified measurements.

### Phase 4 — Server metrics · `v0.5.0`

CPU and GPU in the browser over SSE, so toggling a mode visibly moves the cost between machines.
Note the correction already recorded: `nvidia-smi --loop-ms` returns one good sample then
`[Unknown Error]` forever, so it is **one process per sample**.

### Phase 5 — Full server decoding · `v0.6.0`

Small by design. `FFmpegStreamingPipeline` is already parameterised by its arguments and an
`IFrameReader`, so this is new arguments plus a `JpegPictureReader` — no existing class edited.

### Phase 6 — The rest of hardening · `v1.0.0`

**Auth is the one that matters** and is the reason this is not simply "deployable". Also: killing
child processes with the parent (Job Object / `PR_SET_PDEATHSIG`), and the docs.

---

## Debt carried forward

Known, deliberate, and written down rather than discovered later.

| | Where it is documented |
|---|---|
| **No authentication.** Anyone who reaches the API can start a broadcast, and from Phase 4 read the host's CPU and GPU load. | [SECURITY.md](SECURITY.md), [DEPLOYMENT.md](DEPLOYMENT.md) |
| **DNS rebinding.** `AddressGuard` resolves a name; FFmpeg resolves it again. A name that changes its answer between the two slips through. | [SECURITY.md](SECURITY.md) |
| **Orphaned children.** A hard kill of the API leaves MediaMTX running. Mitigated by adoption, not fixed. | [ARCHITECTURE.md](ARCHITECTURE.md) |
| **FFmpeg command lines are visible** to other processes of the same user, credentials included. | [SECURITY.md](SECURITY.md) |
| **WebRTC on Fly is unverified.** Docker Desktop NATs on Windows, which breaks ICE locally by design, so it needs a real deploy plus the `WebRtcAdditionalHosts` secret. | [DEPLOYMENT.md](DEPLOYMENT.md) |
| **Five members are declared and never called** — `Broadcast.MarkFailed`, `IsPlayable`, `HasViewer`, `ClientDecodeSupport.CanDecodeInHardware`, `VideoCodecNames.ToProbeString`. Two are seams for later phases; three turned out to be unnecessary. | [EXECUTION.md](EXECUTION.md) §4 |
| **Timestamps are synthesised** from the inspected frame rate, not read from the stream. Fine for live playback; the upgrade path is MPEG-TS output. | [PROTOCOL.md](PROTOCOL.md) |

Still deliberately out of scope: transport controls for files, audio, recording, WebTransport, and
multiple cameras on one page. [README](../README.md#roadmap) says why for each.

---

## Loose ends you can close in minutes

- **Publish the GitHub releases.** All three tags are pushed but have no release objects. Actions →
  *Release* → *Run workflow* backfills them from the CHANGELOG. Future `v*` tags publish themselves.
- **Deploy to Fly**, if the demo is wanted — [DEPLOYMENT.md](DEPLOYMENT.md) has the four commands,
  including the ICE-host secret that WebRTC fails silently without.
- **Cut `v0.4.0`** when Phase 3 lands: add its CHANGELOG section, tag, push. The release publishes
  itself.
