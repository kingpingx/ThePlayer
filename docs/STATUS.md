# Status

Where the project actually is, and where to pick it up.

[ROADMAP.md](ROADMAP.md) says what each phase *will* build, in build-ready detail. This file says
what is **built**, what is **not**, and what the next hour of work should be. Update it when a phase
lands.

*Last verified: 27 August 2026, on the branch that lands Phase 5.*

---

## Where things stand

**Six phases done, one slice of a seventh.** An RTSP feed or a video file plays in a browser
whatever codec it is in; the browser decodes everything, something, or nothing at all, as you
choose; and the page shows what each choice costs at both ends while it happens.

Phase 5 finished the set. All three modes on one address, one reading of the metrics feed:

```
cpu 15.9%  gpu 2%  enc 3%  dec 3%
  | ClientDecoded copying:     0.1% cpu     <- server does no codec work
  | ClientDecoded converting:  0.5% cpu     <- hardware transcode
  | ServerDecoded converting:  2.4% cpu     <- decode plus a CPU JPEG encode
```

The cost profile inverts, which is the whole argument: the mode cheapest for the client is the one
the server pays most for. It is now something to look at rather than a claim.

| | Phase | Tag | State |
|---|---|---|---|
| ✅ | 0 · Foundation | `v0.1.0` | Done |
| ✅ | 1 · First pixels — H.264 over WebRTC | `v0.2.0` | Done |
| ✅ | 2 · Client-side decoding | `v0.3.0` | Done |
| ✅ | 3 · Conversion | `v0.4.0` | Done — **not yet tagged** |
| ✅ | 4 · Server metrics | `v0.5.0` | Done — **not yet tagged** |
| ✅ | 5 · Full server decoding | `v0.6.0` | Done — **not yet tagged** |
| 🟡 | 6 · Deployment *(partial)* | — | Container, Fly config and address policy done. **Auth not done.** |
| ⬜ | 6 · Hardening *(rest)* | `v1.0.0` | Auth, process lifetime, docs |

**261 tests, all passing** — 238 backend (Domain 38, Application 96, Infrastructure 94,
Architecture 5, Api 5) and 23 in the player.

Roughly 8,580 lines of C# across four projects, plus a 27-file Angular workspace.

### Nothing returns `501` any more

Every mode the planner can offer is now served. The only refusal left is the planner's own — "this
browser has no decoder for that" — which is permanent, actionable, and a `400`.

---

## Picking it up from cold

```bash
# One-time, if tools/bin is empty
./tools/fetch-mediamtx.ps1          # Windows
./tools/fetch-mediamtx.sh           # Linux / macOS

dotnet build
dotnet test                          # expect 238 passing

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

The value to look for on a watch response is **`converted`**. On an H.265 source, `false` means the
server is copying bytes and its codec cost is zero; `true` means it is doing the work so the browser
does not have to. Since Phase 3 both answers play, which is what makes the comparison honest.

Worth doing once, because it is the whole demonstration in two requests: watch the same H.265 file
twice in `ClientDecoded`, once claiming H.265 support and once not. Two broadcasts appear, two
pipelines run, and the keys differ by a single suffix. Then watch the cost of each:

```bash
curl -N http://localhost:5172/api/metrics/stream
```

The GPU encoder figure is the one to watch. It stays at zero for the passed-through stream and rises
for the converted one — and if it reads `null` with a reason rather than `0`, that is the feed
saying it could not measure, which is a different thing and deliberately looks different.

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
backpressure. Plus the Angular player. **H.265 plays without the server touching it.**

**Phase 3 — Conversion.** `FFmpegArgumentBuilder`, which turns a plan and an engine into a command
line for both destinations — stdout for the frame socket, RTSP into MediaMTX for WebRTC — so the two
cannot drift into producing different video. `MediaMtxPublishingPipeline`, because MediaMTX will
pull a camera but will not re-encode one. Runtime encoder fallback, because detection proves an
encoder *exists* and not that a session can be opened now. **H.265 plays everywhere.**

**Phase 4 — Server metrics.** `GET /api/metrics/stream` over SSE, one sample fanned out to every
listener and no sampling at all when nobody is watching. GPU encode and decode read separately,
because that split is what tells the three modes apart. Every figure nullable, because an idle GPU
and a failed query are different facts that produce the same number if you let them.

**Phase 5 — Full server decoding.** `JpegPictureReader`, a `server-decoded-player` with no
`VideoDecoder` in it, and `PictureOptions` for the bandwidth. A small phase because Phase 2's
pipeline was parameterised by its arguments and its reader, so the third mode edited neither.

**Phase 6, partially — Deployment.** A container carrying everything, `fly.toml`, and `AddressGuard`,
which bounds what the server will dial. Verified by building and running the image, not by
inspection.

---

## What is left

### Phase 6 — The rest of hardening · `v1.0.0` · **next, and all that is left**

**Auth is the one that matters** and is the reason this is not simply "deployable". Also: killing
child processes with the parent (Job Object / `PR_SET_PDEATHSIG`), and the docs.

---

## Debt carried forward

Known, deliberate, and written down rather than discovered later.

| | Where it is documented |
|---|---|
| **No authentication.** Anyone who reaches the API can start a broadcast — and since Phase 3, start an encode on the host's GPU — and from Phase 4 read its CPU and GPU load. | [SECURITY.md](SECURITY.md), [DEPLOYMENT.md](DEPLOYMENT.md) |
| **Conversion is unbounded.** Nothing caps how many transcodes run at once. The fallback keeps a machine at its session limit *working*, by dropping to software, but a host asked for twenty conversions will accept all twenty and grind. | Here. A concurrency limit belongs with auth in Phase 6 |
| **DNS rebinding.** `AddressGuard` resolves a name; FFmpeg resolves it again. A name that changes its answer between the two slips through. | [SECURITY.md](SECURITY.md) |
| **Orphaned children.** A hard kill of the API leaves MediaMTX running. Mitigated by adoption, not fixed. | [ARCHITECTURE.md](ARCHITECTURE.md) |
| **FFmpeg command lines are visible** to other processes of the same user, credentials included. | [SECURITY.md](SECURITY.md) |
| **WebRTC on Fly is unverified.** Docker Desktop NATs on Windows, which breaks ICE locally by design, so it needs a real deploy plus the `WebRtcAdditionalHosts` secret. | [DEPLOYMENT.md](DEPLOYMENT.md) |
| **GPU metrics are NVIDIA-only.** Intel and AMD machines get an availability state and a reason instead of figures — `intel_gpu_top` and `rocm-smi` are Linux-only and usually need privilege, and Windows exposes no per-engine split. CPU and per-broadcast figures work everywhere. | [PROTOCOL.md](PROTOCOL.md) |
| **A GPU reading costs a process spawn per second** while anyone watches the feed, because `nvidia-smi --loop-ms` returns one good sample and then fails forever. Sampling stops when the last listener leaves, which is the only thing bounding it. | [ROADMAP.md](ROADMAP.md) |
| **`IPublishedStream.Acceleration` is still uncalled.** Phase 4 reports what a broadcast costs but not which engine is spending it, so a host that fell back to libx264 shows a flat GPU graph with the explanation only in `/api/health`. | [EXECUTION.md](EXECUTION.md) §4 |
| **MJPEG costs 5.5× the bandwidth**, measured: 8.2 Mbps against 1.5 for the same 720p feed passed through. `PictureOptions.MaxWidth` is the mitigation and is off by default, so the honest number is what a deployment sees first. | [ROADMAP.md](ROADMAP.md), [PROTOCOL.md](PROTOCOL.md) |
| **The NVENC session limit is untested on real hardware.** The fallback was exercised end to end, but by making NVENC refuse a 32×32 stream — the development machine is a T550, which is professional silicon with no session cap. On a consumer GeForce the fourth concurrent broadcast is the real case. | Here |
| **Five members are declared and never called** — `Broadcast.MarkFailed`, `IsPlayable`, `HasViewer`, `ClientDecodeSupport.CanDecodeInHardware`, `VideoCodecNames.ToProbeString` — plus `IPublishedStream.Acceleration`, a seam Phase 4 will read. | [EXECUTION.md](EXECUTION.md) §4 |
| **Timestamps are synthesised** from the inspected frame rate, not read from the stream. Fine for live playback; the upgrade path is MPEG-TS output. | [PROTOCOL.md](PROTOCOL.md) |

Still deliberately out of scope: transport controls for files, audio, recording, WebTransport, and
multiple cameras on one page. [README](../README.md#roadmap) says why for each.

---

## Loose ends you can close in minutes

- **Cut `v0.4.0`, `v0.5.0` and `v0.6.0`.** Phases 3 to 5 have landed and each has its CHANGELOG
  section written — tag and push, and the releases publish themselves.
- **Publish the earlier GitHub releases.** All three tags are pushed but have no release objects.
  Actions → *Release* → *Run workflow* backfills them from the CHANGELOG.
- **Deploy to Fly**, if the demo is wanted — [DEPLOYMENT.md](DEPLOYMENT.md) has the four commands,
  including the ICE-host secret that WebRTC fails silently without. Note that conversion there is
  libx264 on shared cores, so the passthrough case is the one worth showing.
