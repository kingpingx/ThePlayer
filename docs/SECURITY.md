# Security

## The shape of the problem

RTSP cameras authenticate with credentials embedded in the URL:

```
rtsp://admin:hunter2@192.168.1.64:554/Streaming/Channels/101
```

That address is typed into a browser, sent to the server, handed to FFmpeg, registered with
MediaMTX, and mentioned in error messages along the way. Every one of those is a place a password
can escape. Treating this as something to catch in code review does not work — it needs to be
structurally difficult to leak.

So exactly one type holds a credential: **`MediaAddress`**. Nothing else in the system ever sees a
password, and the paths out of that type are deliberately few and deliberately awkward to use by
accident.

---

## The five leak paths, and what closes each

### 1. Printing the address

`ToString()` returns the redacted form, so string interpolation, log templates and concatenation
are all safe by default:

```csharp
logger.LogInformation("Connecting to {Address}", address);
// → Connecting to rtsp://admin:***@192.168.1.64:554/Streaming/Channels/101
```

Getting the real thing takes a deliberate call to `ToFFmpegInput()`, which exists at exactly one
call site: the FFmpeg argument builder.

That method returns the address **verbatim** rather than re-serialising a parsed `Uri`. Re-encoding
risks changing the percent-escaping and handing FFmpeg a subtly different password than the one
that was typed — a bug that presents as "the camera rejects my correct password".

### 2. FFmpeg's own output

**This is the one most easily missed.** FFmpeg echoes the input URL when a connection fails:

```
[rtsp @ 0x55f1c0] method DESCRIBE failed: 401 Unauthorized
rtsp://admin:hunter2@192.168.1.64:554/Streaming/Channels/101: Server returned 401
```

So a **wrong** password produces a log line containing the **right** one — and if that error is
forwarded to the client, it lands in the browser console too.

`FFmpegCommandRunner` scrubs both output streams against the address before returning anything.
Callers cannot forget, because there is no path through the runner that skips it.

Scrubbing covers the escaped and decoded forms of the password and the whole `user:password`
segment, longest first — FFmpeg may echo the URL exactly as given, while other tooling prints a
decoded value.

### 3. MediaMTX configuration on disk

MediaMTX takes stream sources in its YAML config, which would write the camera password to a file
that might be committed, backed up, or baked into a container image.

Paths are therefore registered over the **control API at runtime**, never written to the config
file. The generated `mediamtx.yml` declares `paths: {}` and nothing else. Credentials live in
MediaMTX's memory for as long as the stream runs.

### 4. API responses and diagnostics

The address never travels back to a client. Responses and diagnostic listings carry the redacted
form, so a password cannot reach the browser console, a screenshot, or a support ticket.

### 5. Dedupe keys and diagnostic dumps

Broadcasts are keyed by `MediaAddress.Fingerprint` — the first 8 bytes of a SHA-256 of the address,
hex-encoded. Stable enough to deduplicate two viewers of the same camera, and reveals nothing.
Using the raw address as a dictionary key would put the password into every diagnostic dump of
what is currently live.

---

## Enforced by tests, not discipline

`ThePlayer.Domain.Tests` covers redaction, scrubbing and fingerprinting directly, including:

- an FFmpeg-style error line that echoes the input, asserting the password is gone **and** that the
  `401 Unauthorized` diagnostic survives — a scrubber that destroys the message's usefulness would
  quietly get switched off
- parse failures never echoing the input, which is easy to regress by adding a helpful-looking
  `'{input}' is not a valid address` message

From Phase 1, an integration test runs a real broadcast against a deliberately wrong password and
asserts it appears in no captured log line, no API response, and no client-facing error.

---

## Where the server will connect

A server that dials whatever address it is handed is a request-forgery surface: anyone who can
reach the API can make it open a connection to an arbitrary host and read the outcome back out of
the error message. On a laptop that is the entire point — you want it to reach the camera on your
desk. On a host anyone can find, it is a way to map the network the server sits inside.

`AddressGuard` decides, before anything connects, whether this deployment will touch an address at
all. It runs ahead of inspection, so a refused address never reaches ffprobe and nothing about the
target leaks back — not even how long it took to fail.

```jsonc
"AddressPolicy": {
  "AllowPrivateNetworks": false,        // refuse RFC 1918, loopback, link-local, CGNAT, multicast
  "AllowedFileRoots": [ "/app/samples" ] // and refuse file paths outside these
}
```

Both default to permissive, because the development default has to be "the camera on your desk
works". `appsettings.Production.json` turns them on.

Three details worth knowing:

- **A literal address is checked without a lookup**, so a hostile literal never reaches the
  resolver either.
- **Every answer must pass.** A name resolving to one public address and one private one is
  refused — that combination is the shape an attacker would choose, not an accident.
- **A failed lookup is a refusal, not a pass.** Failing open would make the control a formality:
  break DNS, reach anything.

The refusal names neither the resolved address nor the credentials. Saying *"that maps to
10.1.2.3"* would answer the exact question the probe was asking.

`169.254.169.254` is in the blocked set for a specific reason: it is the cloud metadata endpoint,
and on most hosts it hands out credentials to anyone who asks.

---

## Residual risks

Three things are **not** solved. They are listed here rather than papered over.

### Process arguments are visible

The FFmpeg child process is started with the credentialled URL on its command line, which any
process running as the same user can read (`ps`, Task Manager, `wmic`). FFmpeg has no clean way to
pass RTSP credentials out of band.

*Mitigation:* run the server as a dedicated user. On a shared machine, assume anyone with an
account can read the camera password.

### A name can change its answer between the check and the connection

`AddressGuard` resolves a host name, and then FFmpeg resolves it again. A name that answers with a
public address for the first lookup and a private one for the second slips through the gap — DNS
rebinding, and it defeats address filtering by design rather than by accident.

*Mitigation:* none built. Closing it means pinning the address the guard approved and handing
FFmpeg that instead of the name, which changes what is passed to every pipeline. Worth doing before
this is exposed to an untrusted audience rather than a demo one.

### The key is a bearer token in a browser

Authentication exists as of Phase 6, and it is an API key rather than anything with sessions or
identities. That is the right size for this — one operator, one deployment — but it has two
consequences worth stating rather than discovering.

**It lives in `localStorage`.** Anything with script access to the page can read it, which is the
same exposure a session cookie without `HttpOnly` has. The alternative is retyping it on every
reload, and the practical result of *that* is people choosing short keys.

**Two of the endpoints accept it in the query string.** `EventSource` and `WebSocket` cannot set
headers at all, so a header-only scheme would leave the metrics stream either unreachable from a
browser or unprotected. Query strings reach proxy logs and browser history, so this is a concession
rather than a design.

*The upgrade path, not built:* a short-lived ticket issued by an authenticated request and spent on
the stream. It removes the long-lived secret from the URL without needing a session layer. Worth
doing before this faces an audience that is not you.

---

## Environment posture

| | Development | Staging | Production |
|---|---|---|---|
| Auth | off | off | **required** |
| CORS | open to `localhost:4200` | same-origin | same-origin |
| Diagnostics | open | open | behind auth |
| Metrics stream | open | open | behind auth |
| Logging | Debug | Information | Warning, structured |

`ApiKey:Required` is what turns it on, and `appsettings.Production.json` sets it. Keys come from the
environment — `ApiKey__Keys__0` — never from a committed file. More than one is accepted so a key
can be rotated without a window where neither the old nor the new one works.

**The host refuses to start** if a key is required and none is configured. That is one missing
environment variable away at any time, and the symptom otherwise — every request refused — reads
like a client problem from every angle except the server's.

### What is guarded, and what is not

| | Guarded | Why |
|---|---|---|
| `POST /api/watch`, `DELETE /api/watch/{id}`, `GET /api/broadcasts` | Yes | `fetch` can set a header |
| `GET /api/metrics/stream` | Yes | Header, or `?key=` where `EventSource` cannot set one |
| `GET /api/health` | **Reachable, reduced** | A liveness probe that needs a secret ends up switched off |
| `WS /ws/frames/{viewerId}` | **By capability** | See below |
| The player itself | No | A 401 in place of the page that could ask for a key helps nobody |

`/api/health` answers everyone with `{healthy, environment}` and nothing else. The full body
inventories the FFmpeg build, every acceleration profile on the machine, and what has recently
failed on it — which is not something to hand an anonymous caller on a public host.

The frame socket is authorised by the viewer id in its path rather than by the key. A browser cannot
put a header on a `WebSocket` either, and that id is already a capability: 64 bits of randomness,
minted only by an authorised watch call. Using it as the credential is a better fit than putting the
long-lived secret into a second URL.

Server resource metrics are information disclosure — they describe the host's hardware and load —
so `/api/metrics/stream` follows the same split as everything else rather than being open by
default. Two things about it are worth stating plainly rather than leaving to be discovered:

- **What it tells an anonymous caller.** Core count, installed memory, GPU model class by inference,
  and the machine's load second by second. It carries no address, not even a redacted one — the
  broadcast key is deliberately the only identifier in it — but "how many streams are running and
  how hard is this box working" is legible from it, and that is a reconnaissance signal.
- **What it costs them.** Sampling runs only while someone is listening, so an open connection makes
  the server spawn `nvidia-smi` once a second. That is bounded per host rather than per listener —
  ten connections still cost one sample — which makes it a poor amplification vector, but it is not
  nothing, and it is one more reason the honest posture for a public deployment is `AddressPolicy`
  on and the instance private.

---

## What conversion costs, and who can spend it

Before Phase 3 an unauthenticated `POST /api/watch` could open a camera connection and copy bytes.
It can now start a **transcode**, which is a materially larger thing to hand a stranger: an encode
session on the host's GPU, or several cores of libx264 if there is no GPU to take it.

Nothing currently caps how many run at once. The runtime fallback makes a machine at its session
limit keep *working* — it drops to the next engine and eventually to software — but that is a
correctness mechanism, not a limit: it degrades gracefully all the way down to a host doing twenty
software transcodes and serving none of them well.

Two things follow, and both belong with authentication in Phase 6:

- **Auth first.** A concurrency cap on an open endpoint only decides how quickly an anonymous
  caller exhausts the host, not whether they can.
- **Then a cap**, per host rather than per viewer — viewers already share a pipeline when they want
  the same bytes, so the number worth bounding is distinct plans, not requests.

Until then this is another reason the honest posture for a public deployment is `AddressPolicy` on
and the instance private.
