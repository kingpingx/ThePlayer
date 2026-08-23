# Changelog

All notable changes to ThePlayer. Versions correspond to the phases in the
[README roadmap](README.md#roadmap).

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
