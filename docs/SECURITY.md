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

## Residual risks

Two things are **not** solved. They are listed here rather than papered over.

### Process arguments are visible

The FFmpeg child process is started with the credentialled URL on its command line, which any
process running as the same user can read (`ps`, Task Manager, `wmic`). FFmpeg has no clean way to
pass RTSP credentials out of band.

*Mitigation:* run the server as a dedicated user. On a shared machine, assume anyone with an
account can read the camera password.

### The server connects wherever it is told

Addresses come from the UI, so anyone who can reach the API can make the server open a connection
to an arbitrary host — a server-side request forgery surface. On a LAN tool this is acceptable;
across a network boundary it is not.

*Mitigation:* the API-key auth added in Phase 6. This is why that phase matters more than it
otherwise would. If the server is ever exposed beyond a trusted network, address allow-listing
should be added on top.

---

## Environment posture

| | Development | Staging | Production |
|---|---|---|---|
| Auth | off | off | **required** |
| CORS | open to `localhost:4200` | same-origin | same-origin |
| Diagnostics | open | open | behind auth |
| Metrics stream | open | open | behind auth |
| Logging | Debug | Information | Warning, structured |

Server resource metrics are information disclosure — they describe the host's hardware and load —
so `/api/metrics/stream` follows the same split as everything else rather than being open by
default.
