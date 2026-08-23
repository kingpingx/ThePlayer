# Architecture

## Layers

Clean Architecture, dependency rule pointing strictly inward.

```
                 ┌──────────────────────────────┐
                 │           Api                │  ASP.NET Core, wires everything
                 └──────────┬──────────┬────────┘
                            │          │
                            ▼          ▼
          ┌──────────────────────┐   ┌───────────────────────────┐
          │    Application       │◀──│      Infrastructure       │
          │  use cases + ports   │   │  FFmpeg, MediaMTX, sockets│
          └──────────┬───────────┘   └───────────────────────────┘
                     │
                     ▼
          ┌──────────────────────┐
          │       Domain         │  rules and types, zero dependencies
          └──────────────────────┘
```

The arrow from Infrastructure **into** Application is the one that matters. Infrastructure
*implements* interfaces that Application *owns*. Without that inversion, the use cases would depend
on FFmpeg and MediaMTX directly, and swapping either — or rewriting the backend in Go, which is
planned — would mean rewriting the logic too.

`ThePlayer.Architecture.Tests` asserts all of this against the compiled assemblies. Clean
Architecture decays quietly: one convenient `using` in the wrong layer compiles fine and is
invisible in a diff. A red build is a better guard than review.

The strictest rule is that **Domain references nothing but the base class library** — no logging
abstraction, no JSON attributes, nothing. It stays reasonable-about, instantly testable, and
translatable without dragging a dependency graph along.

---

## Two conventions

### One file per concept, not per type

Small types that only exist as part of a larger idea share a file with that idea.

```
Media/MediaAddress.cs      MediaAddress · MediaAddressKind
Media/VideoFormat.cs       VideoFormat · VideoCodec · VideoCodecNames
Hardware/AccelerationProfile.cs   AccelerationProfile · AccelerationKind · HardwareCapabilities
Monitoring/SystemHealth.cs        SystemHealth · MediaServerStatus · MediaServerState
```

What matters for SOLID is that the **types** are distinct. `VideoCodec` being its own type is the
design; giving it its own file is ceremony. The split rule:

- **Group** — records, enums and thin interfaces that are only meaningful together and carry no
  behaviour of their own.
- **Separate** — anything with real behaviour that will grow: planners, coordinators, pipelines,
  parsers, readers.

This deviates from the "one public type per file" convention that StyleCop's SA1402 enforces. That
is deliberate. If StyleCop is ever added, configure SA1402 off rather than churning the layout to
satisfy it.

### Ports only where there is a boundary

A type goes in `Ports.cs` when it crosses a **process or platform boundary** — an external binary,
a child process, an OS-specific counter. That is the whole test.

Everything else is concrete: `BroadcastPlanner`, `BroadcastCoordinator`, `FrameBroadcaster`,
`HealthReporter`. They are pure in-process logic, directly testable, with nothing to substitute.

> An interface with exactly one implementation and no boundary to cross is indirection, not
> dependency inversion.

Ports are also added **when the phase that needs them arrives**, not declared up front against
implementations that do not exist yet.

---

## Keeping the model small

Several types were removed during design because they were not earning their keep. A value object
justifies itself by enforcing an invariant or stopping two parameters being swapped; these did
neither.

| Removed | Why | Now |
|---|---|---|
| `CpuUsage`, `MemoryUsage` | wrappers around one number, no invariant | fields on `ResourceSnapshot` |
| `Viewer` | viewers of one broadcast share its plan by definition, so a viewer is only an id | a set of ids inside `Broadcast` |
| `BroadcastKey` | a typed wrapper over a dictionary-key string | `MediaAddress.Fingerprint` |
| `ConversionAction` | restated what the mode and output codec already say | fields on `BroadcastPlan` |
| `CredentialScrubber` | scrubbing needs the secret, and one type already holds it | `MediaAddress.Scrub` |
| `IFrameBroadcaster`, `IBroadcastRegistry` | ports over pure in-process logic | concrete classes |
| `StartWatchingHandler`, `StopWatchingHandler` | two one-method classes | `BroadcastCoordinator.Attach/Detach` |
| `ModeAvailabilityService` | the decision `BroadcastPlanner` already makes | `BroadcastPlanner` |

`MediaAddress.Scrub` is the one worth dwelling on: the type that holds the password is the type
that removes it from text. The FFmpeg runner scrubs through the object that actually knows the
secret, instead of a helper being handed it — which means there is no way to construct a scrubber
that has forgotten what to look for.

---

## There is no "video source" entity

Nothing here is owned, stored, or has a lifecycle we manage. The address arrives from the UI on
each request; it is never persisted — and since it contains a password, persisting it is actively
undesirable. So there is no entity, no repository, no database and no CRUD layer.

**`MediaAddress`** — a value object. Where the media is, validated on construction, holding
credentials apart from the display form.

**`Broadcast`** — an ephemeral in-memory object representing one pipeline *currently running*. It
starts when the first viewer asks and stops shortly after the last one leaves. Viewers wanting the
same address in the same output format share one: a single FFmpeg process feeding many clients, not
one per tab.

*On the name:* not `Restream`, because you do not *re*-stream a local file; and `Stream` alone
collides with `System.IO.Stream`, which this codebase uses constantly for pipes and sockets.

---

## Detection, never assumption

The same build has to run on an NVIDIA laptop and a VAAPI-only Linux box, so nothing about the host
is decided at compile time.

`FFmpegHardwareInspector` reads `ffmpeg -hwaccels` and `-encoders`, then — and this is the part
that is easy to skip — **verifies each hardware encoder by actually encoding a couple of frames
with it**. An FFmpeg build lists every encoder it was compiled with regardless of whether the
device exists: a stock Windows build advertises `h264_vaapi` and `h264_amf` on hardware that has
neither. Without verification, "detected acceleration" means "compiled in", and the first real
stream is where the difference surfaces.

Each `AccelerationProfile` carries its FFmpeg arguments as data. Holding them as data rather than
branching on the vendor inside the pipelines is what keeps one pipeline implementation working
across every backend.

---

## Process supervision

`MediaMtxSupervisor` finds the binary, writes a minimal config, starts it, waits for its control
API to answer, and restarts it with exponential backoff when it exits.

Three decisions worth noting:

**A missing binary is reported, not thrown.** The API still starts and `/api/health` still answers,
because "MediaMTX is not installed, run `tools/fetch-mediamtx`" is exactly the diagnosis the health
endpoint exists to deliver. Crashing at startup would hide it.

**Both output streams are drained.** An undrained pipe eventually blocks the child process, and
forwarding MediaMTX's output into the log is the only way its own startup errors are ever visible.
The same applies to every FFmpeg process — reading stdout to the end before touching stderr
deadlocks any command that is chatty on stderr, which FFmpeg always is.

**A surviving process is adopted, not fought.** A force-kill or crash leaves MediaMTX holding the
RTSP, WebRTC and API ports. Starting a second copy would fail to bind and restart-loop against a
confusing error, so the supervisor detects the survivor and monitors it instead.

Killing children on a *hard* parent exit needs a Windows Job Object or `PR_SET_PDEATHSIG` on Linux.
That lands in Phase 6 with the rest of the shutdown hardening.

---

## Testing

| Project | Kind | Needs FFmpeg? | Covers |
|---|---|---|---|
| `ThePlayer.Domain.Tests` | unit | no | address parsing, redaction, scrubbing, fingerprinting |
| `ThePlayer.Application.Tests` | unit | no | planner rules, coordinator reference counting |
| `ThePlayer.Architecture.Tests` | unit | no | the dependency rule itself |
| `ThePlayer.Infrastructure.Tests` | integration | yes | frame readers, argument building, metrics parsing |
| `ThePlayer.Api.Tests` | integration | no | endpoints and transports, with ports faked |
| `ThePlayer.TestSupport` | library | — | builders, fixtures, generated test media |

`ThePlayer.Api.Tests` fakes the two outward-facing ports rather than booting real dependencies.
That is the point, not a shortcut: it lets the tests assert what the endpoint does when MediaMTX is
**broken**, which is the case that actually matters and which a working server would never
reproduce on demand.

Test media is **generated by FFmpeg at test time** rather than committed — short H.264 and H.265
files, and a `testsrc2` feed published to an ephemeral MediaMTX. CI needs no camera and no binary
assets, and the H.265 conversion path is exercised on every run.
