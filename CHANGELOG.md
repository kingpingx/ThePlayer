# Changelog

All notable changes to ThePlayer. Versions correspond to the phases in the
[README roadmap](README.md#roadmap).

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## Unreleased

### Phase 6: Deployment and hardening

The last phase. What was left was not features but the two things that decide whether this can run
anywhere other than the machine that built it: nothing may outlive the server, and not everyone may
use it.

#### Added

- **API key authentication.** Off by default, required in Production, and the host **refuses to
  start** if it is required with no keys configured — that is one missing environment variable away
  at any time, and every other symptom of it looks like a client problem. Several keys are accepted
  so one can be rotated without a window where neither works, and the comparison is fixed-time over
  SHA-256 digests so neither the key nor its length leaks through timing.
- **Child processes now die with the server.** A Job Object with `KILL_ON_JOB_CLOSE` on Windows,
  `setpriv --pdeathsig` on Linux. This was a known gap since Phase 0: a `taskkill /F` left MediaMTX
  holding ports 8554, 8889 and 9997, and the supervisor's adoption of a survivor was a recovery
  rather than a fix. Verified by killing a running server with two FFmpeg processes and MediaMTX
  alive, and finding none of them afterwards.
- **`docker-compose.yml`** — Linux, host networking, and the NVIDIA device reservation. Host
  networking because WebRTC needs to advertise an address a browser can reach, and behind a bridge
  the container's own view of itself is not one.
- **[HARDWARE.md](docs/HARDWARE.md)** — what the machine needs, what it does without, and the one
  container-toolkit step that GPU encoding and GPU metrics share. They succeed or fail together, so
  documenting them apart would have been two chances to do half of it.
- **An API key panel in the player**, which appears only after the server has actually answered
  `401`. A local deployment demands nothing, so asking everyone for a credential that mostly does
  not exist would be the wrong default.

#### Changed

- **`GET /api/health` answers everyone, but says less to an anonymous caller** — `{healthy,
  environment}` rather than the FFmpeg build, every acceleration profile on the machine and its
  recent failures. Probes keep working; the hardware inventory stops being public.
- The frame socket is authorised by the viewer id in its path rather than by the key. A browser
  cannot set a header on a `WebSocket`, and the id is already a capability only an authorised watch
  call can mint — a better fit than putting the long-lived secret in a second URL.

### Phase 5: Full server decoding and the three-way comparison

The third mode, and the only one where the browser decodes no video at all. The server decodes every
frame and sends whole JPEG images; `createImageBitmap` unpacks them onto a canvas.

With it the comparison the project exists to make is complete, and it inverts as it should — the
mode that is cheapest for the client is the one the server pays most for, and the page shows both
halves at once.

**A small phase, and deliberately so.** Phase 2 built the pipeline parameterised by its FFmpeg
arguments and its frame reader, so the third mode arrived as new arguments plus a second
`IFrameReader` rather than as a branch through anything that already existed. That is the payoff
from merging the two frame pipelines during planning rather than writing two near-duplicates.

#### Added

- **`JpegPictureReader`** — splits an MJPEG stream on its own markers. Far simpler than Annex-B,
  because JPEG images are self-delimiting: `FF D8` opens one and `FF D9` closes it, so nothing has
  to be injected to find boundaries. It does have to track whether it is inside a scan, where those
  same bytes occur as data — getting that wrong produces images whose lower half is grey.
- **`server-decoded-player`** — no `VideoDecoder` anywhere in it. It does keep the same discipline
  the WebCodecs path needs: an `ImageBitmap` holds memory the collector will not reclaim, so every
  one is closed in a `finally`.
- **`PictureOptions`** — `Quality` and `MaxWidth`. MJPEG has no inter-frame compression, and the
  measured cost on a 720p clip is 8.2 Mbps against 1.5 for the same feed passed through. On a LAN
  that buys a client that decodes nothing; over a WAN it is why the mode needs a scale filter.
- The diagnostics overlay now labels the client's own cost per mode, so "unpack" and "decode" are
  not confused for each other.

#### Changed

- **`IFrameReader` gained `Describe`.** The pipeline used to wait for a parameter set and derive an
  RFC 6381 string itself — an Annex-B assumption living in a class that is meant to know nothing
  about codecs. Readers answer it now, because the two answer it from genuinely different places.
- The picture path decodes on the device and encodes on the CPU: `-hwaccel` without
  `-hwaccel_output_format`, since the JPEG encoder is software and device frames would fail at the
  first picture. A profile therefore selects the decoder in this mode and nothing else — its H.264
  tuning is not applied, because `-preset p1` handed to `mjpeg` is an error rather than a no-op.
- **`RejectIfNotYetImplemented` is gone**, and with it the `501` from `POST /api/watch`. There is no
  longer a plan the planner can produce that the coordinator refuses.

### Phase 4: Server CPU and GPU in the client

The browser now shows what the choice costs the machine at the other end, live. Toggle a mode on an
H.265 feed and the encoder bar is what moves.

This is the phase that makes the previous three legible. "The server is copying bytes" has been true
since Phase 2 and provable only by reading a boolean; it is now a GPU encoder reading at zero beside
a converted stream lighting both engines, from the same camera, at the same moment.

#### Added

- **`GET /api/metrics/stream`** — Server-Sent Events. One-way, text and periodic, which is what SSE
  is for, and `EventSource` reconnects by itself where a WebSocket needs hand-written ping and pong.
  It also keeps the frame socket dedicated to binary video, so a metrics hiccup cannot disturb
  playback.
- **`MetricsCollector`** — one sample fanned out to every listener. Ten open tabs cost one reading,
  not ten, which matters because a GPU reading costs a process spawn: sampling per request would
  make the monitor's own cost visible in the numbers it reports. **Sampling stops entirely when
  nobody is listening.**
- **`NvidiaGpuReader`** — encode and decode engines read separately, because that split is what
  makes the three modes tell each other apart. Overall utilisation cannot.
- **`SystemMetricsReader`** — `GetSystemTimes` on Windows, `/proc/stat` on Linux.
- **`ProcessMetricsReader`** — what each broadcast costs. The comparison the project exists to make:
  not "the machine is busy" but "*this* stream costs 0.1% passed through and 0.4% converted".
- **`resource-monitor`** — beside the capability panel, so what this browser can do and what the
  server is paying for it sit next to each other.

#### Changed

- **Nullable is the contract, not defensive typing.** An idle GPU and a failed query produce the
  same number if failure is reported as zero, so a figure that was not measured is `null` beside a
  reason written to be shown as-is. That applies to `cpuPercent` on a feed's first reading — a rate
  needs two samples — as much as to a machine with no NVIDIA card.
- **`[Unknown Error]` from `nvidia-smi` is unavailability, never zero.** One process per sample,
  which is worse than the streaming design originally planned and is what works: the loop modes
  return one good sample and then fail every field forever.
- The metrics sampler sleeps *after* its work rather than using `PeriodicTimer`. A fixed rate meant
  a sample that overran left a tick already due, so the next fired immediately — observed as pairs
  of readings a tenth of a second apart, each paying for a process spawn.

#### Fixed

- **Per-broadcast CPU read `0.0%` for a transcode that was running.** On Windows with a Chocolatey
  FFmpeg the process this server starts is a shim that launches the real one as its child and then
  idles, so measuring the process we started measured the shim. Cost is now summed over the process
  *tree* — which was always the right unit, since `Kill(entireProcessTree: true)` has been used for
  the same reason since Phase 1. Cost is measured the way it is killed.

  Worth recording how it was found: every unit test passed, and `0.0%` for a cheap real-time
  transcode is entirely plausible. It only surfaced by measuring the same thing a second way,
  through `Win32_Process`, and comparing.

### Phase 3: Video conversion

H.265 now plays **everywhere** — converted for clients that need it, passed through untouched for
clients that do not. The planner has decided when to convert since Phase 1; this is the release
where the answer is carried out instead of rejected. `POST /api/watch` no longer returns `501` for
anything but `ServerDecoded`.

The interesting part is what did *not* change: a browser that can decode H.265 still gets the
original bytes and the server still does no codec work. Both streams can run from one camera at the
same time, on separate pipelines, and `/api/broadcasts` shows them side by side.

#### Added

- **`FFmpegArgumentBuilder`** — one place that turns a plan and an acceleration profile into a
  command line, for both destinations. Conversion feeds two of them: an elementary stream on stdout
  for the frame socket, and RTSP into MediaMTX for WebRTC. Same input handling and the same encoder
  settings either way, so the two paths cannot drift into producing different video.
- **`MediaMtxPublishingPipeline`** — MediaMTX will pull a camera but it will not re-encode one, so a
  converted stream is transcoded here and pushed back in. It is our child process rather than
  MediaMTX's `runOnDemand` deliberately: a transcoder MediaMTX owns writes its failures into
  MediaMTX's log, where an encoder that will not open is indistinguishable from a camera that is
  offline, and the fallback below needs to tell those apart.
- **Runtime encoder fallback.** Startup detection proves an encoder *exists*; it does not prove a
  session can be opened now. NVENC allows three to eight concurrent sessions on a consumer card, so
  the fourth broadcast fails at `avcodec_open2` long after the health check has finished saying
  NVENC is available. A refused engine drops to the next one down the ranking, and the last resort
  is libx264, which is present in every practical FFmpeg build.
- **`encoderFallbacks` in `GET /api/health`** — because a machine that has quietly dropped to
  libx264 is otherwise indistinguishable from one that is merely slow, and the profile list goes on
  advertising an engine nothing can actually open. Empty on a healthy host.
- Acceleration profiles carry their own encoder arguments, so `-hwaccel_output_format` and the
  vendor presets live beside the encoder name rather than in a switch inside each pipeline. That
  flag is the one that matters: without it every decoded frame is copied out of GPU memory and back
  in, which costs more than the encode.

#### Changed

- The keyframe interval is derived from the source frame rate — about two seconds — rather than
  fixed. A fixed `-g 30` means one second of join latency on a 30fps feed and three on a 10fps one.
- B-frames are disabled on every engine. They reorder output and add at least one frame of latency
  for nothing on a feed rendered on arrival.
- `BroadcastCoordinator` shuts down transcoders with their broadcasts, and treats one that has died
  the way it already treated a file that ran out: the broadcast ends rather than being handed to
  the next viewer, who would otherwise get a WHEP URL for a path nobody is publishing to.

#### Fixed

- The frame reader was built from the *source* codec rather than the delivered one. Harmless while
  the server only ever copied bytes, and wrong the moment it converted: an H.264 stream parsed as
  HEVC yields no parameter sets, so a converted stream failed with "ended before it produced a
  decodable frame" while FFmpeg sat there having converted it perfectly well. Both ends of the pipe
  now ask `FFmpegArgumentBuilder.DeliveredCodec` rather than working it out separately.

### Deployment

Pulled forward from Phase 6 so that Phase 2 can be shown to someone who is not sitting at your
keyboard. Authentication is still Phase 6 and is still missing.

### Added

- **`AddressGuard`** - decides whether this deployment will connect to an address at all, before
  anything tries to. A server that dials whatever it is handed can be used to map the network it
  sits inside; `AddressPolicy` refuses private, loopback, link-local, CGNAT and multicast ranges,
  and file paths outside a configured root. Off by default, on in Production.
- **`Dockerfile`** and **`fly.toml`** - one container carrying the API, the Angular player, FFmpeg
  and MediaMTX, with two sample clips generated at build time so there is something to play.
- **[DEPLOYMENT.md](docs/DEPLOYMENT.md)** - and the constraint that decides the host: WebRTC media
  is UDP, and most platform-as-a-service hosts route one HTTP port and no UDP, so `ServerAssisted`
  cannot work on them.
- **Release workflow** - tagging `v*` publishes a GitHub release with notes taken from this file,
  so a phase is described once.

### Changed

- `MediaAddress` exposes `Host`, so policy can be decided without pulling the URL apart again next
  to the credentials.
- A refused address is `403`, not `400`: the request is well formed, this deployment just will not
  connect to it.

## [0.3.0] — Phase 2: Client-side decoding

The server stops touching the video. FFmpeg copies bytes, the browser decodes them with its own GPU
through WebCodecs, and an H.265 feed reaches a capable browser with the server's codec cost at zero.

### Added

- **`WS /ws/frames/{viewerId}`** - the frame socket. One JSON `init` message carrying what
  `VideoDecoder.configure()` needs, then one binary message per access unit with a nine-byte header
  (flags, then a big-endian microsecond timestamp). Frozen in `docs/PROTOCOL.md`.
- **`CompressedFrameReader`** - splits an Annex-B elementary stream into whole access units by
  scanning for the delimiters FFmpeg is asked to insert, rather than parsing slice headers.
- **`CodecStringBuilder`** - derives the RFC 6381 codec string from the stream's own parameter sets.
  H.264 is three bytes; H.265 needs a bit reader, emulation-prevention stripping, and the
  compatibility flags emitted in reverse bit order.
- **`FFmpegStreamingPipeline`** - a long-lived FFmpeg child process, parameterised by its arguments
  and a frame reader so that Phase 5 adds a mode without editing it.
- **`FrameBroadcaster`** - one pipeline fanned out to many viewers, each with a bounded drop-oldest
  queue. A viewer that falls behind is skipped until the next keyframe and its queue emptied, so it
  never renders from a broken reference chain and never stalls anyone else.
- Integration tests over media generated by FFmpeg at test time, asserting one access unit per
  picture against real encoder output.

### Changed

- `BroadcastCoordinator` starts a frame pipeline for the socket modes instead of publishing to
  MediaMTX, and does it **outside** its lock - waiting for a keyframe can take seconds, and holding
  the lock across that would stall every other viewer. A lost start race is discarded.
- `RejectIfNotYetImplemented` no longer rejects `ClientDecoded`. What remains is conversion
  (Phase 3) and full server decoding (Phase 5).

### Fixed

Two bugs in the frame reader, both caught by testing against real FFmpeg output rather than
synthetic data:

- A stream opens with its parameter sets *before* the first delimiter. Those were being emitted as a
  picture of their own - inventing a frame with nothing to decode, and stripping the first real
  keyframe of the sets a decoder needs. They are now carried into the keyframe that follows.
- `00 00 00 01` is a four-byte start code whose last three bytes are themselves a valid three-byte
  one. A rescan resuming *inside* a start code found the phantom and split one NAL in two, quietly
  shortening a picture by a byte. Only reads that land mid-code trigger it, so it survived 64 KB
  reads and appeared immediately under 7-byte ones.

## [0.2.0] — Phase 1: First pixels

H.264 cameras and video files now play in a browser over WebRTC. One FFmpeg process serves every
viewer of the same stream, and stops shortly after the last one leaves.

### Added

- **`POST /api/watch`, `DELETE /api/watch/{viewerId}`, `GET /api/broadcasts`.** See
  [PROTOCOL.md](docs/PROTOCOL.md).
- **`FFmpegMediaInspector`** — ffprobe against a camera or file, reporting codec, size, frame rate
  and whether it ends. A duration means a file; its absence means a live feed, and that one
  distinction separates the two everywhere downstream.
- **`BroadcastPlanner`** — the full negotiation table as a pure function of
  `(format, mode, client support, hardware)`. Every row is a unit test rather than something only
  observable by pointing at a real camera. Also produces per-mode availability with a reason for
  each unavailable mode.
- **`BroadcastCoordinator`** — reference-counted broadcasts. Viewers wanting the same bytes share a
  pipeline; clients needing different output get their own from the same upstream. A broadcast
  lingers 10 seconds after emptying so a page refresh does not rebuild the pipeline.
- **`MediaMtxPaths`** — publishes over the control API. Cameras are pulled by MediaMTX directly;
  files are pushed in by an on-demand FFmpeg, since MediaMTX cannot read a file itself. Both are
  on-demand: nothing connects until a viewer asks.
- **A WHEP harness at `/`** — a dependency-free page that probes the browser's codec support, calls
  the API and plays the result. It exists because the Angular player is blocked on a Node upgrade,
  and its handshake is the reference the Angular component will port.
- **34 Application tests** covering the negotiation table, key derivation, reference counting,
  linger, and the difference between an unavailable mode and an unbuilt one.

### Fixed

- **An unreachable camera returned a stack trace.** `TimeoutException` escaped the endpoint's catch
  list, so a typo in an IP produced a 500 with file paths and internals in the body. Now a clean
  `502` with a one-line reason.
- **That failure took 20 seconds.** ffprobe had no RTSP timeout, so it hung until the outer command
  timeout. Now ~6s via `-timeout`. Worth noting `-rw_timeout`, which looks like the right option,
  is ignored by the RTSP demuxer.
- **`TestSupport` was run as a test project** by `dotnet test`, reporting a spurious "testhost
  process exited with error" beside the real results.
- **`.gitignore` silently excluded a source folder.** `tests/**/media/` was intended for generated
  test video but also matched `tests/**/Media/`, since git matches case-insensitively on Windows —
  which quietly kept `MediaAddressTests.cs` out of the Phase 0 commit.

### Security

- `UseExceptionHandler` with `AddProblemDetails` outside Development, so an unhandled exception can
  never reach a client as a stack trace. This system's exception messages can carry an upstream
  address, and paths and internals are exactly what an attacker would like to read.
- Verified against a live server: an RTSP URL with a deliberately wrong password produced **zero**
  occurrences of that password across the whole server log, and the `502` body showed both our
  redacted form and FFmpeg's echo of the URL scrubbed independently.

### Known limitations

- **Only `ServerAssisted` with an H.264 source plays.** Other modes return `501` naming the phase
  that delivers them: client-side decoding in Phase 2, conversion in Phase 3, full server decoding
  in Phase 5.
- **The Angular player is still not scaffolded** — Node 20.9 remains below what any current Angular
  CLI accepts. The WHEP harness stands in.
- **Files play straight through.** No seek, pause or scrub bar; WebRTC has no seek concept, so the
  feature would behave differently per mode.

---

## [0.1.0] — Phase 0: Foundation

The scaffolding, the credential-safe address type, MediaMTX supervision, and a health endpoint that
reports what this machine can actually do. No video plays yet — that is Phase 1.

### Added

- **Solution structure.** Four source projects on Clean Architecture (`Domain`, `Application`,
  `Infrastructure`, `Api`), five test projects, and a shared `TestSupport` library. One
  `TargetFramework` in `Directory.Build.props`, so retargeting is a one-line change.
- **`MediaAddress`** — the only type that holds a credential. Parses RTSP URLs and file paths,
  validates them, and keeps the password apart from the display form. `ToString()` is always safe
  to print; `Scrub()` removes the credentials from text produced elsewhere. 38 tests cover it.
- **`MediaMtxSupervisor`** — finds the MediaMTX binary, generates its config, starts it, waits for
  its control API, and restarts it with exponential backoff. Adopts an already-running instance
  rather than failing to bind against it.
- **`FFmpegHardwareInspector`** — detects acceleration profiles and **verifies each one by actually
  encoding with it**, so a compiled-in encoder with no device behind it is not reported as
  available.
- **`FFmpegCommandRunner`** — process supervision with concurrent stream draining and automatic
  credential scrubbing on the way out.
- **`GET /api/health`** — MediaMTX state, FFmpeg version, verified acceleration profiles, and
  decodable codecs. `200` when usable, `503` when not.
- **`ThePlayer.Architecture.Tests`** — asserts the dependency rule against the compiled assemblies,
  including that `Domain` references nothing outside the base class library.
- **`tools/fetch-mediamtx.ps1` / `.sh`** — downloads the right MediaMTX build per platform.
- **Three environments** — Development, Staging and Production, with distinct logging and CORS.
- **Documentation** — [ARCHITECTURE.md](docs/ARCHITECTURE.md), [PROTOCOL.md](docs/PROTOCOL.md) and
  [SECURITY.md](docs/SECURITY.md).

### Security

- FFmpeg echoes the input URL when a connection fails, so a **wrong** password would otherwise
  produce a log line containing the **right** one. Every byte of process output is scrubbed before
  it is logged or surfaced, and there is no path through the runner that skips it.
- MediaMTX paths are registered over its control API rather than written into its YAML config, so
  camera credentials never touch disk.
- Parse failures never echo the input, since the input may itself be a credentialled URL.
- Two residual risks are documented rather than papered over: the FFmpeg command line is readable
  by other processes of the same user, and the server will connect to whatever address it is given.
  See [SECURITY.md](docs/SECURITY.md#residual-risks).

### Known limitations

- **The Angular player is not scaffolded.** Node 20.9 is below what any current Angular CLI accepts
  (Angular 19 needs `≥ 20.11.1`, Angular 22 needs `≥ 22.22.3`). Install Node 22 LTS and it lands in
  Phase 1.
- **Children are not killed on a hard parent exit.** A force-kill or crash leaves MediaMTX running.
  The supervisor adopts a survivor on the next start, so this is a nuisance rather than a failure;
  the real fix needs a Windows Job Object or `PR_SET_PDEATHSIG` and is scheduled for Phase 6.
