# Execution and flow

How ThePlayer actually runs, and what every file in the repository is for.

Read this after the [README](../README.md) and before the code. It answers three questions the other
documents deliberately leave alone: what talks to what, what happens between `dotnet run` and a frame
appearing in a browser, and which file to open when you want to change something.

- [ARCHITECTURE.md](ARCHITECTURE.md) argues *why* the layers are shaped this way. This document shows
  *what* they do at runtime.
- [PROTOCOL.md](PROTOCOL.md) is the frozen wire contract. This document is the implementation behind it.
- [ROADMAP.md](ROADMAP.md) is what has not been built yet. Part 4 here lists the seams already left
  for it.

**Contents**

| | |
|---|---|
| [0 · Orientation](#0--orientation) | processes, ports, and who does what |
| [1 · Architecture](#1--architecture) | layers, ports and adapters, where each concern lives |
| [2 · Execution and flow](#2--execution-and-flow) | startup · playback · sharing · teardown · health · failure |
| [3 · Every file](#3--every-file-and-what-it-is-for) | file-by-file tour of the whole repository |
| [4 · Declared but not wired up](#4--declared-but-not-yet-wired-up) | seams left for later phases |

---

## 0 · Orientation

Three programs are involved, and it is worth being clear about which does what:

- **`ThePlayer.Api`** — an ASP.NET Core app. It **decides and coordinates**. It never touches a video
  frame.
- **MediaMTX** — a Go media server, launched and supervised as a child process. It does the
  RTSP → WebRTC repackaging.
- **FFmpeg / ffprobe** — invoked as short-lived child processes. All codec work happens here.

```
   Browser                ThePlayer.Api (.NET, :5172)            child processes
   ───────                ───────────────────────────            ───────────────
      │   HTTP  /api/watch        │  spawns + supervises  ┌─────────────────────┐
      ├──────────────────────────▶│──────────────────────▶│  MediaMTX           │
      │                           │   HTTP :9997 control  │  :8554  rtsp        │
      │   HTTP  POST SDP (WHEP)   │                       │  :8889  whep        │
      ├───────────────────────────┼──────────────────────▶│  :8189  udp media   │
      │                           │  runs ffprobe/ffmpeg  └──────────┬──────────┘
      │◀══════════════════════════┼══════════════════════════════════┘  RTSP
      │   WebRTC media, UDP :8189 │                             ▲     (camera, or a
      │   — the API is NOT here — │                             └───── file pushed in
      │                           │                                    by ffmpeg -re)
```

The line to notice is the bottom one. Once playback starts, **media flows from MediaMTX straight to
the browser over UDP, and the .NET API is out of the path entirely.** It handled one HTTP request and
then stepped aside.

### Ports

| Port | Who listens | For |
|---|---|---|
| `5172` | ThePlayer.Api | REST + the WHEP harness at `/` |
| `8554` | MediaMTX | RTSP — where a file is pushed in |
| `8889` | MediaMTX | WebRTC signalling (WHEP) |
| `8189/udp` | MediaMTX | WebRTC media |
| `9997` | MediaMTX | control API — how paths are registered |

All five are configurable in [`appsettings.json`](../src/ThePlayer.Api/appsettings.json). They are
listed together because they are what collides when a MediaMTX process survives a crash — see
[step 7 of startup](#21--startup-dotnet-run).

---

## 1 · Architecture

### 1.1 The layers

Four projects. The dependency rule points strictly inward, and one arrow carries the whole design:

```
   ┌───────────────┐   Api is the only project that
   │      Api      │   knows every other one exists
   └───┬───────┬───┘
       │       │
       ▼       ▼
 ┌───────────┐ │  ┌──────────────────┐
 │Application│◀┼──│  Infrastructure  │  ◀── this arrow is the design:
 └─────┬─────┘ │  └──────────────────┘      Infrastructure IMPLEMENTS interfaces
       │       │     FFmpeg, MediaMTX       that Application OWNS (Ports.cs)
       ▼       ▼
 ┌─────────────────┐
 │      Domain     │  zero package references — enforced by DependencyRuleTests.cs
 └─────────────────┘
```

Without that inversion the use cases would depend on FFmpeg and MediaMTX directly, and swapping
either — or rewriting the backend in Go, which is planned — would mean rewriting the logic too.

### 1.2 Ports and adapters

Four interfaces cross a process or platform boundary. Each has exactly one implementation today:

```
  Application/Ports.cs              implemented by (Infrastructure)
  ────────────────────              ──────────────────────────────
  IMediaInspector          ◀────────  FFmpegMediaInspector.cs    ffprobe → VideoFormat
  IHardwareInspector       ◀────────  FFmpegInspectors.cs        ffmpeg -encoders, then a real probe
  IMediaServer             ◀────────  MediaMtxPaths.cs           control API: publish / remove
  IMediaServerSupervisor   ◀────────  MediaMtxServer.cs          owns the child process
```

Everything else — `BroadcastPlanner`, `BroadcastCoordinator`, `HealthReporter`, `BroadcastSweeper` —
is concrete, because it crosses no boundary. An interface with one implementation and nothing to
substitute is indirection, not dependency inversion. The reasoning is in
[ARCHITECTURE.md](ARCHITECTURE.md#ports-only-where-there-is-a-boundary).

### 1.3 Where each concern lives

| Layer | Holds | Representative files |
|---|---|---|
| **Domain** | Types and rules. No packages at all. No I/O, ever. | [`MediaAddress.cs`](../src/ThePlayer.Domain/Media/MediaAddress.cs), [`Broadcast.cs`](../src/ThePlayer.Domain/Broadcasting/Broadcast.cs) |
| **Application** | Use cases, and the ports Infrastructure must satisfy. | [`BroadcastCoordinator.cs`](../src/ThePlayer.Application/Broadcasting/BroadcastCoordinator.cs), [`Ports.cs`](../src/ThePlayer.Application/Ports.cs) |
| **Infrastructure** | Child processes, external binaries, OS specifics. | [`FFmpegCommandRunner.cs`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegCommandRunner.cs), [`MediaMtxServer.cs`](../src/ThePlayer.Infrastructure/MediaServer/MediaMtxServer.cs) |
| **Api** | HTTP surface. The only place DI is configured. | [`Endpoints.cs`](../src/ThePlayer.Api/Endpoints.cs), [`ServiceRegistration.cs`](../src/ThePlayer.Api/ServiceRegistration.cs) |

---

## 2 · Execution and flow

Six paths. Every step names the file that performs it.

### 2.1 Startup (`dotnet run`)

| # | File | What happens |
|---|---|---|
| 1 | [`Program.cs`](../src/ThePlayer.Api/Program.cs) | `CreateBuilder` → `AddThePlayer(configuration, environment)` |
| 2 | [`ServiceRegistration.cs`](../src/ThePlayer.Api/ServiceRegistration.cs) | Binds the `FFmpeg`, `MediaMtx` and `Broadcasts` sections with `ValidateOnStart()` — a bad setting fails at boot, not halfway through playing something |
| 3 | [`ServiceRegistration.cs:52-55`](../src/ThePlayer.Api/ServiceRegistration.cs#L52-L55) | `MediaMtxSupervisor` is registered **three times as one instance** — as itself, as `IMediaServerSupervisor`, and as a hosted service. That is what makes the status the supervisor *writes* the status `/api/health` *reads* |
| 4 | [`Program.cs:9-20`](../src/ThePlayer.Api/Program.cs#L9-L20) | CORS in Development (the Angular dev server is a different origin); `UseExceptionHandler` everywhere else — this system's exception messages can carry an upstream address, so a stack trace must never reach a client |
| 5 | [`Program.cs:24-27`](../src/ThePlayer.Api/Program.cs#L24-L27) | Static files (the WHEP harness at `/`), then `MapThePlayerEndpoints()` |
| 6 | [`MediaMtxServer.cs`](../src/ThePlayer.Infrastructure/MediaServer/MediaMtxServer.cs) `ResolveBinaryPath` | Binary lookup, in order: `MediaMtx:BinaryPath` → walk **up** from `AppContext.BaseDirectory` looking for `tools/bin/mediamtx[.exe]` → `PATH`. The walk exists because in development the binary sits at the repo root while the app runs from `bin/Debug/net8.0` |
| 7 | `MediaMtxServer.cs:107-114` | **Missing binary sets `Failed` and returns — it does not throw.** The API must still start and `/api/health` must still answer, because "MediaMTX is not installed, run `tools/fetch-mediamtx`" is exactly the diagnosis that endpoint exists to deliver |
| 8 | `MediaMtxServer.cs` `IsApiRespondingAsync` | If the control API already answers, **adopt the survivor** and just monitor it. Starting a second copy would fail to bind ports 8554/8889/9997 and restart-loop against a confusing error |
| 9 | `MediaMtxServer.cs` `WriteConfigFileAsync` | Writes `mediamtx-runtime/mediamtx.yml`: api/rtsp/webrtc on, hls/rtmp/srt off, and `paths: {}` **deliberately empty** — paths are registered over the API at runtime so camera credentials never touch disk |
| 10 | `MediaMtxServer.cs` `PumpAsync` | Both stdout and stderr drained concurrently. An undrained pipe eventually blocks the child; forwarding its output is also the only way MediaMTX's own startup errors are ever visible. This is also where the version banner is scraped, since the v3 API exposes no version of its own |
| 11 | `MediaMtxServer.cs` `WaitForApiAsync` | Readiness = `GET /v3/config/global/get` polled every 250 ms, up to `StartTimeout` (15 s) → state `Running` |
| 12 | `MediaMtxServer.cs` `ExecuteAsync` | On unexpected exit: `2^n` second backoff capped at 30 s, up to `MaxRestarts` (10). A climbing `restartCount` is the signal that something is wrong *even while* `state` reads `Running` |
| 13 | [`BroadcastSweeper.cs`](../src/ThePlayer.Api/BroadcastSweeper.cs) | The second hosted service starts: a 2 s `PeriodicTimer` driving `BroadcastCoordinator.SweepAsync` |
| 14 | [`FFmpegInspectors.cs`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegInspectors.cs) | **Not run at boot.** Hardware inspection is lazy and memoised behind a `SemaphoreSlim`; it fires on the first `/api/health` or the first watch, and costs about a second, once |

### 2.2 Playback — Play to first frame

```
 index.html     Endpoints.cs   BroadcastCoordinator  BroadcastPlanner  MediaMtxPaths   MediaMTX
     │               │                 │                    │               │             │
     │POST /api/watch│                 │                    │               │             │
     ├──────────────▶│ TryParse ──────▶│                    │               │             │
     │               │                 │ ffprobe (outside the lock)         │             │
     │               │                 ├───────────────────▶│               │             │
     │               │                 │◀─── BroadcastPlan ─┤               │             │
     │               │                 ├──── PublishAsync ─────────────────▶│────────────▶│
     │◀─WatchTicket──┤◀────────────────┤                    │               │             │
     │                                                                                    │
     │ POST SDP offer  (WHEP, straight to :8889) ────────────────────────────────────────▶│
     │◀═══════════ WebRTC media, UDP :8189 — the .NET API is out of the path ═════════════╡
```

| # | File | Member | What happens |
|---|---|---|---|
| 1 | [`index.html`](../src/ThePlayer.Api/wwwroot/index.html#L108-L127) | `probeCapabilities` | Calls `VideoDecoder.isConfigSupported()` **twice per codec** — once plain, once with `hardwareAcceleration: 'prefer-hardware'`. That second call is what separates "software only" from "hardware", which is worth telling the user about rather than silently accepting |
| 2 | `index.html` | form `submit` | `POST /api/watch` with `{address, mode, clientDecodeSupport}` |
| 3 | [`Endpoints.cs:69`](../src/ThePlayer.Api/Endpoints.cs#L69) | `StartWatchingAsync` | `MediaAddress.TryParse` → `400` on failure. The error text describes the *shape* of the problem and never echoes the input, because the input may contain a password |
| 4 | [`Endpoints.cs:133`](../src/ThePlayer.Api/Endpoints.cs#L133) | `ToClientSupport` | A codec name the server does not recognise is **dropped, not treated as decodable**. Being wrong in that direction produces a stream the client cannot play |
| 5 | [`BroadcastCoordinator.cs:96-99`](../src/ThePlayer.Application/Broadcasting/BroadcastCoordinator.cs#L96-L99) | `AttachAsync` | Inspection runs **before the lock is taken**. It talks to the camera and can take seconds; holding the lock across it would serialise every viewer in the system behind one slow device |
| 6 | [`FFmpegMediaInspector.cs`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegMediaInspector.cs#L68-L87) | `BuildArguments` | ffprobe with `-rtsp_transport tcp` (cameras on wifi drop UDP and a half-probed stream reports nonsense) and `-timeout` in **microseconds**. Note `-rw_timeout`, the option that looks like it should do this, is ignored by the RTSP demuxer |
| 7 | `BroadcastCoordinator.cs:248-280` | `GetFormatAsync` | The result is cached for `FormatCacheDuration` (5 min) per address fingerprint, so a popular stream is not re-probed for every viewer |
| 8 | [`BroadcastPlanner.cs:41-73`](../src/ThePlayer.Application/Broadcasting/BroadcastPlanner.cs#L41-L73) | `Plan` | A **pure function** of `(format, mode, clientSupport, hardware)` → passthrough or convert-to-X. No I/O, no state, nothing injected — which is what turns the negotiation table into plain unit tests |
| 9 | `BroadcastPlanner.cs:82-128` | `Availability` | Separately returns **every** mode with a human sentence for the unavailable ones. A greyed-out control with no explanation is worse than no control at all |
| 10 | [`BroadcastCoordinator.cs:304-319`](../src/ThePlayer.Application/Broadcasting/BroadcastCoordinator.cs#L304-L319) | `RejectIfNotYetImplemented` | Phase 1 serves `ServerAssisted` passthrough only. Anything else throws → `501` naming the phase that will deliver it |
| 11 | [`Broadcast.cs:55`](../src/ThePlayer.Domain/Broadcasting/Broadcast.cs#L55) | `BroadcastPlan.KeyFor` | Key = `{fingerprint}-{mode}-{codec}[-converted]`. Built from the SHA-256 fingerprint, never the address, so a password cannot become a dictionary key |
| 12 | `BroadcastCoordinator.cs:113-124` | `AttachAsync` | Key already present → `Attach`, share the running pipeline, return |
| 13 | [`BroadcastCoordinator.cs:127-135`](../src/ThePlayer.Application/Broadcasting/BroadcastCoordinator.cs#L127-L135) | `AttachAsync` | Otherwise publish to MediaMTX **before** the broadcast becomes visible, so a viewer never sees one it cannot yet play. If publishing throws, nothing has been registered |
| 14 | [`MediaMtxPaths.cs:99-139`](../src/ThePlayer.Infrastructure/MediaServer/MediaMtxPaths.cs#L99-L139) | `BuildPathConfiguration` | RTSP → `sourceOnDemand: true`, MediaMTX pulls the camera itself. A **file** → `runOnDemand`, because MediaMTX cannot read files: it runs FFmpeg to push the file in as if it were live. `-re` paces it at real time — without it FFmpeg pushes as fast as it can read and playback runs many times too fast |
| 15 | `MediaMtxPaths.cs:46` | `PublishAsync` | `RemoveAsync` first, so publish is idempotent: a path can survive a crash of this process, and "add" against an existing name is an error rather than an update |
| 16 | `BroadcastCoordinator.cs:282-297` | `TicketFor` | Returns `transport: {kind: "WebRtc", url: ".../whep"}` for `ServerAssisted`, or a `/ws/frames/{viewerId}` path for the socket modes |
| 17 | [`index.html`](../src/ThePlayer.Api/wwwroot/index.html#L161-L181) | `playWhep` | `recvonly` transceiver → `createOffer` → **await ICE gathering with a 3 s cap** (a camera on a slow interface can leave gathering pending forever) → POST the SDP as `application/sdp` → apply the answer |

From step 17 onward the API is idle. Media flows MediaMTX → browser over UDP 8189.

### 2.3 How broadcasts are shared

The key is what decides whether two viewers share one FFmpeg process:

```
   tab A  (can decode H.265) ─┐
                              ├─▶ key  abc123-clientdecoded-h265            ─▶ pipeline 1
   tab B  (can decode H.265) ─┘        same key → Attach, viewers = 2

   tab C  (cannot)            ──▶ key  abc123-clientdecoded-h264-converted  ─▶ pipeline 2
                                        same camera, different bytes
```

Two viewers wanting the same bytes share one pipeline. Two clients needing *different* output get
their own pipelines from the same upstream — which is exactly right, and falls out of the key rather
than needing any special handling.

### 2.4 Teardown

```
   last viewer leaves                     BroadcastSweeper ticks every 2s
          │                                  │      │      │      │      │
          ▼                                  ▼      ▼      ▼      ▼      ▼
   EmptySince = now  ├────────── Linger, 10s ──────────────┤  ShouldStop → RemoveAsync
                     │                                     │
                     └── a refresh landing anywhere in here re-attaches
                         to the pipeline that is still running
```

| # | File | What happens |
|---|---|---|
| 1 | [`index.html`](../src/ThePlayer.Api/wwwroot/index.html) `stop()` | `DELETE /api/watch/{viewerId}` with `keepalive: true`, so the request still lands if the tab is closing. Also wired to `pagehide` |
| 2 | [`Endpoints.cs:40-51`](../src/ThePlayer.Api/Endpoints.cs#L40-L51) | Always `204`, even for a viewer id that does not exist — a closing tab may send both a beacon and a socket close, and the second is not an error |
| 3 | `BroadcastCoordinator.cs:157-182` `DetachAsync` | Removes the viewer→broadcast mapping under the lock |
| 4 | [`Broadcast.cs:149-158`](../src/ThePlayer.Domain/Broadcasting/Broadcast.cs#L149-L158) `Detach` | Stamps `EmptySince` **only when that was the last viewer** |
| 5 | [`BroadcastSweeper.cs`](../src/ThePlayer.Api/BroadcastSweeper.cs) | 2 s tick → `SweepAsync`. One bad sweep is logged and swallowed, or nothing would ever be cleaned up again |
| 6 | `Broadcast.cs:163-164` `ShouldStop` | True once `now - EmptySince >= Linger` (10 s) |
| 7 | `BroadcastCoordinator.cs:220-231` `SweepAsync` | Unpublishes **outside the lock** — the broadcasts are already unreachable, so nothing can attach in the meantime |

The linger window is the point of the whole path: without it, a page refresh tears down an FFmpeg
process and immediately rebuilds it, costing a reconnection to the camera and several seconds of
black screen.

### 2.5 Health

| # | File | What happens |
|---|---|---|
| 1 | [`Endpoints.cs:19-31`](../src/ThePlayer.Api/Endpoints.cs#L19-L31) | `GET /api/health` → `HealthReporter.GetAsync` |
| 2 | [`HealthReporter.cs`](../src/ThePlayer.Application/Monitoring/HealthReporter.cs) | Composes two ports and performs no I/O of its own |
| 3 | `MediaMtxServer.cs:96` | `Status` is a `Volatile.Read` of a field the supervisor loop writes — read from request threads, written from the background loop |
| 4 | `FFmpegInspectors.cs:45-63` | `InspectAsync` returns the memoised result, so this is cheap per request |
| 5 | [`SystemHealth.cs:57-61`](../src/ThePlayer.Domain/Monitoring/SystemHealth.cs#L57-L61) | `IsHealthy` = MediaMTX usable **and** at least one profile found → `200`, otherwise `503`. The status code carries the verdict so container and uptime probes need not parse the body |

Hardware acceleration is deliberately **not** a health input. A machine that can only encode with
libx264 still plays video, and reporting it unhealthy would page someone for nothing.

### 2.6 When it fails

| Status | Raised by | When |
|---|---|---|
| `400` | `Endpoints.cs:69` / `:74` | The address or mode could not be parsed |
| `400` | `Endpoints.cs:99` ← `BroadcastPlanner.Plan` | The mode is unavailable for this stream on this client |
| `501` | `Endpoints.cs:105` ← `RejectIfNotYetImplemented` | Valid request this phase cannot serve; `detail` names the phase that will |
| `502` | `Endpoints.cs:93` ← `FFmpegMediaInspector` | The upstream could not be read: unreachable, wrong credentials, or not a video |

All four are RFC 7807 problem documents whose `detail` is written to be shown to a user as-is, and is
always scrubbed of credentials. [`FFmpegMediaInspector.Explain`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegMediaInspector.cs#L194-L220)
does the translation — including mapping FFmpeg's `-138` (its `ETIMEDOUT`) to "did not respond",
because "Error number -138 occurred" tells a user nothing, and a typo in a camera's IP is the single
most common way this fails.

**Why a password never reaches a log line:**

```
  camera URL ──▶ MediaAddress ──┬─▶ ToFFmpegInput()  ──▶ ffprobe / ffmpeg    the one way out
   (has pass)   holds _secrets  ├─▶ Display   "***"  ──▶ logs, API, errors
                                └─▶ Scrub(text)      ──▶ every byte of process output
```

The non-obvious leak: **FFmpeg echoes the input URL when a connection fails, so a wrong password
produces a message containing the right one.**
[`FFmpegCommandRunner.RunAsync`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegCommandRunner.cs#L97-L167)
therefore passes *every byte* of both streams through `MediaAddress.Scrub` before returning, so
nothing downstream has to remember to. Full treatment in [SECURITY.md](SECURITY.md).

---

## 3 · Every file, and what it is for

In dependency order. The last column is the thing worth knowing that the filename does not tell you.

### `ThePlayer.Domain` — types and rules, zero package references

| File | Lines | What it is | Worth knowing |
|---|---|---|---|
| [`Media/MediaAddress.cs`](../src/ThePlayer.Domain/Media/MediaAddress.cs) | 320 | `MediaAddress` · `MediaAddressKind`. The only type in the system that holds a credential | `_secrets` holds the escaped form, the plain form *and* the whole user-info segment, **ordered longest-first** so `user:pass` is replaced before `pass`. `ToFFmpegInput()` returns the raw string rather than re-serialising a parsed `Uri` — re-serialising risks changing the percent-encoding and handing FFmpeg a subtly different password than was typed. `Fingerprint` = first 8 bytes of SHA-256, hex |
| [`Media/VideoFormat.cs`](../src/ThePlayer.Domain/Media/VideoFormat.cs) | 79 | `VideoFormat` · `VideoCodec` · `VideoCodecNames` | `Duration is null` is the one field that distinguishes a camera from a file everywhere downstream |
| [`Playback/PlaybackMode.cs`](../src/ThePlayer.Domain/Playback/PlaybackMode.cs) | 83 | The three modes · `CodecSupport` · `ClientDecodeSupport` · `ModeAvailability` | Carries the doc comment explaining why `ClientDecoded` exists at all, given that WebRTC already decodes on the client GPU. `ClientDecodeSupport.None` is what a request that omitted the probe becomes |
| [`Broadcasting/Broadcast.cs`](../src/ThePlayer.Domain/Broadcasting/Broadcast.cs) | 165 | `BroadcastState` · `BroadcastPlan` (+ `KeyFor`) · the live `Broadcast` | Not thread-safe on its own; `BroadcastCoordinator` owns every instance and serialises access. Viewers are held as bare ids because everyone attached shares the plan by definition |
| [`Hardware/AccelerationProfile.cs`](../src/ThePlayer.Domain/Hardware/AccelerationProfile.cs) | 86 | `AccelerationKind` · `AccelerationProfile` · `HardwareCapabilities` | `Known` is a **catalogue of knowledge, not a detection result** — it says what each engine *would* be called if present. Software sits at `Preference: 99` and is what makes "there is always a fallback" true |
| [`Monitoring/SystemHealth.cs`](../src/ThePlayer.Domain/Monitoring/SystemHealth.cs) | 62 | `MediaServerState` · `MediaServerStatus` · `SystemHealth` | `RestartCount` exists so a flapping server is visible while `State` still reads `Running` |

### `ThePlayer.Application` — use cases and the ports

| File | Lines | What it is | Worth knowing |
|---|---|---|---|
| [`Ports.cs`](../src/ThePlayer.Application/Ports.cs) | 99 | The four interfaces Infrastructure implements, plus `MediaInspectionException` | The file comment states the rule: a type belongs here only if it crosses a **process or platform boundary**. Ports are added when the phase that needs them arrives, not declared up front |
| [`Broadcasting/BroadcastPlanner.cs`](../src/ThePlayer.Application/Broadcasting/BroadcastPlanner.cs) | 149 | The negotiation table: what the server will do to a stream, and which modes can be offered | `WebRtcSafeCodec = H264` is the single constant encoding the whole "browsers refuse H.265 in SDP" judgement. Pure — no I/O, no state, nothing injected |
| [`Broadcasting/BroadcastCoordinator.cs`](../src/ThePlayer.Application/Broadcasting/BroadcastCoordinator.cs) | 320 | `BroadcastOptions` · `WatchTicket` · the registry of everything live | One `SemaphoreSlim` guards three dictionaries, and is held for bookkeeping only — never across an inspection or a publish |
| [`Monitoring/HealthReporter.cs`](../src/ThePlayer.Application/Monitoring/HealthReporter.cs) | 24 | Assembles `/api/health` | Concrete rather than a port: it performs no I/O of its own, it only composes two ports that do |

### `ThePlayer.Infrastructure` — external processes and OS specifics

| File | Lines | What it is | Worth knowing |
|---|---|---|---|
| [`FFmpeg/FFmpegCommandRunner.cs`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegCommandRunner.cs) | 200 | `FFmpegOptions` · `ProcessResult` · the process runner · `ExternalToolNotFoundException` | Both reads are **started before either is awaited** — draining stdout to the end before touching stderr deadlocks any command that is chatty on stderr, which FFmpeg always is. `Kill(entireProcessTree: true)` because orphaned FFmpeg children go on holding the camera connection open |
| [`FFmpeg/FFmpegInspectors.cs`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegInspectors.cs) | 281 | `FFmpegHardwareInspector` — what this *machine* can do | A profile needs **both** halves advertised (encoder *and* accelerator), and then must survive actually encoding a 128×128 two-frame `testsrc2` to `-f null -`. Without that last step, "detected acceleration" means "compiled in". VA-API is the awkward probe: it needs an explicit `/dev/dri/renderD128` and an `hwupload`, which correctly fails on a machine with no `/dev/dri` |
| [`FFmpeg/FFmpegMediaInspector.cs`](../src/ThePlayer.Infrastructure/FFmpeg/FFmpegMediaInspector.cs) | 250 | `IMediaInspector` — what this *stream* is | Prefers `avg_frame_rate` over `r_frame_rate`, because the latter is the container's nominal rate and can be wildly optimistic on a variable-rate camera — and Phase 2 synthesises timestamps from this number. An RTSP address is always treated as live regardless of any duration it reports |
| [`MediaServer/MediaMtxPaths.cs`](../src/ThePlayer.Infrastructure/MediaServer/MediaMtxPaths.cs) | 143 | `IMediaServer` — registers paths over the control API | The failure body echoes the configuration just sent, which for a camera contains the password — so it is scrubbed before it becomes an exception message. `RemoveAsync` never fails the caller: removal is cleanup |
| [`MediaServer/MediaMtxServer.cs`](../src/ThePlayer.Infrastructure/MediaServer/MediaMtxServer.cs) | 457 | `MediaMtxOptions` + `MediaMtxSupervisor` | The largest file in the repo; [§2.1](#21--startup-dotnet-run) is its walkthrough. `Supervise: false` switches it to monitor-only, for when MediaMTX runs in a container or by hand |

### `ThePlayer.Api` — the host

| File | Lines | What it is | Worth knowing |
|---|---|---|---|
| [`Program.cs`](../src/ThePlayer.Api/Program.cs) | 36 | Pipeline order and `app.Run()` | `public partial class Program` at the bottom exists so `WebApplicationFactory<Program>` can boot the real host in tests — top-level statements otherwise produce an internal entry point |
| [`ServiceRegistration.cs`](../src/ThePlayer.Api/ServiceRegistration.cs) | 93 | The composition root — every concrete type is chosen here and nowhere else | The three-way registration of `MediaMtxSupervisor` ([§2.1 step 3](#21--startup-dotnet-run)). `TimeProvider.System` is injected so linger and cache expiry can be tested by advancing a fake clock instead of sleeping |
| [`Endpoints.cs`](../src/ThePlayer.Api/Endpoints.cs) | 198 | The four endpoints and the mapping to and from `Contracts.cs` | Translation only — no orchestration, no decisions. The exception→status table lives here, and `ToSummary` returns `Address.Display`, never the raw address |
| [`Contracts.cs`](../src/ThePlayer.Api/Contracts.cs) | 108 | The wire DTOs | Separate from Domain so an internal rename is not a breaking API change. `[JsonPropertyName("ffmpegVersion")]` is explicit because the default camel-case policy turns `FFmpegVersion` into `fFmpegVersion` |
| [`BroadcastSweeper.cs`](../src/ThePlayer.Api/BroadcastSweeper.cs) | 56 | The 2 s timer driving `SweepAsync` | The timer lives here rather than inside the coordinator so tests can advance a fake clock and call the sweep directly, instead of waiting on wall time |
| [`wwwroot/index.html`](../src/ThePlayer.Api/wwwroot/index.html) | 256 | A dependency-free WHEP client: capability probe, watch call, handshake, teardown | Not throwaway scaffolding — its handshake is what `server-assisted-player.component.ts` will do, so it is the **reference** for that port, and stays afterwards as a fallback |
| `appsettings*.json` | — | Base + Development / Staging / Production | Every timeout, port and linger value in this document is here |
| `Properties/launchSettings.json` | — | Dev profiles | `http` → 5172, `https` → 7035 + 5172 |

### Tests

| Project | Kind | Needs FFmpeg? | Covers |
|---|---|---|---|
| [`ThePlayer.Domain.Tests`](../tests/ThePlayer.Domain.Tests) | unit | no | Address parsing, redaction, scrubbing, fingerprinting |
| [`ThePlayer.Application.Tests`](../tests/ThePlayer.Application.Tests) | unit | no | Planner rules, coordinator reference counting and linger |
| [`ThePlayer.Architecture.Tests`](../tests/ThePlayer.Architecture.Tests) | unit | no | The dependency rule itself, via NetArchTest. The only project allowed to reference all four layers — and it fails with the *names* of offending types, because "the architecture test failed" is a frustrating thing to be handed by CI |
| [`ThePlayer.Infrastructure.Tests`](../tests/ThePlayer.Infrastructure.Tests) | integration | **yes** | Frame readers, argument building, metrics parsing |
| [`ThePlayer.Api.Tests`](../tests/ThePlayer.Api.Tests) | integration | no | Endpoints over real HTTP with the two outward-facing ports faked — which is the point, not a shortcut: it lets the tests assert what happens when MediaMTX is **broken** |
| [`ThePlayer.TestSupport`](../tests/ThePlayer.TestSupport) | library | — | Builders and fixtures. Sets `IsTestProject=false`, or `dotnet test` tries to run it as a test project and reports a confusing "testhost process exited" alongside the real results |

Test media is generated by FFmpeg at test time rather than committed, so CI needs no camera and no
binary assets.

### Tooling and CI

| File | What it is | Worth knowing |
|---|---|---|
| [`Directory.Build.props`](../Directory.Build.props) | Shared build settings | `TargetFramework` lives here and nowhere else — retargeting to .NET 10 is a one-line change. `TreatWarningsAsErrors` is on. The shared test stack is conditioned on `$(MSBuildProjectName.EndsWith('.Tests'))`, which is why `TestSupport` is not named `*.Tests` |
| [`tools/fetch-mediamtx.ps1`](../tools/fetch-mediamtx.ps1) · [`.sh`](../tools/fetch-mediamtx.sh) | Downloads MediaMTX into `tools/bin` | Deliberately a script rather than an app-startup download: a network fetch during boot is fragile, hard to diagnose, and surprising in an air-gapped or container environment. The supervisor only ever looks for a binary that is already there |
| [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) | Two-tier CI | Fast tier (no external tools) fails in under a minute; then a Linux **and** Windows matrix, which is what catches path separators, executable suffixes and hardware assumptions. CI runners have no GPU, so encoder verification finds only the software profile — proving the fallback works |
| [`ThePlayer.sln`](../ThePlayer.sln) | Solution | Ten projects under two solution folders: four in `src/`, six in `tests/` |

---

## 4 · Declared but not yet wired up

Real seams left for later phases. Listed so nobody hunts for callers that do not exist — each of
these currently appears exactly once in the tree, at its own declaration:

| Member | File | Waiting on |
|---|---|---|
| `Broadcast.MarkEnded` | `Broadcasting/Broadcast.cs` | Phase 5 — a file reaching its last frame moves the broadcast to `Ended` |
| `Broadcast.MarkFailed` | `Broadcasting/Broadcast.cs` | Phase 3+ — a pipeline that dies mid-stream |
| `Broadcast.IsPlayable` | `Broadcasting/Broadcast.cs` | Phase 2 — the frame socket refusing a broadcast that is not yet `Live` |
| `Broadcast.HasViewer` | `Broadcasting/Broadcast.cs` | Phase 2 — authorising a frame socket against its viewer id |
| `ClientDecodeSupport.CanDecodeInHardware` | `Playback/PlaybackMode.cs` | Phase 2 — the capability panel distinguishing hardware from software support |
| `VideoCodecNames.ToProbeString` | `Media/VideoFormat.cs` | Phase 2 — server-side capability probing |

`BroadcastState.Ended` is likewise only ever declared, for the same reason as `MarkEnded`. And
`Broadcast.Attach` takes a `now` parameter it does not currently use — `EmptySince` is simply
cleared; the parameter is there for symmetry with `Detach`.

Everything else in the repository is on a live code path today.
