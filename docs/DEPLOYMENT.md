# Deployment

Running ThePlayer somewhere other than a development machine.

> This is a slice of Phase 6 pulled forward so the Phase 2 work can be shown to someone who is not
> sitting at your keyboard. What is **not** here is authentication — see
> [Where it is honest to host this](#where-it-is-honest-to-host-this).

---

## The constraint that decides the host

Three modes, two transports, and they do not have the same needs:

| Mode | Transport | Needs |
|---|---|---|
| `ClientDecoded` | WebSocket | One HTTP port. That is all. |
| `ServerAssisted` | WebRTC | One HTTP port **plus UDP ingress** for media, and an ICE host a browser can reach. |
| `ServerDecoded` | WebSocket | One HTTP port *(Phase 5)*. |

**Most platform-as-a-service hosts route a single HTTP port and no UDP at all.** On one of those,
the WebRTC handshake succeeds and then no video ever arrives — the mode looks available and quietly
fails, which is worse than not offering it.

| Host | `ClientDecoded` | `ServerAssisted` | Why |
|---|---|---|---|
| **Fly.io** | ✅ | ✅ | UDP services are supported, and an app can hold a dedicated IPv4 |
| Render, Railway, App Runner | ✅ | ❌ | One HTTP port, no UDP ingress |
| A VPS | ✅ | ✅ | You own the firewall |
| Local Docker | ✅ | ⚠️ | Works on Linux; Docker Desktop NATs on Windows and macOS, which breaks ICE |

There is also a browser constraint that catches people out: **WebCodecs requires a secure context**.
Served over plain HTTP to anything but `localhost`, `window.VideoDecoder` is undefined and
`ClientDecoded` disappears entirely — the capability panel reports no WebCodecs API and the mode
greys out. Any real deployment needs HTTPS.

---

## Locally, with Docker

```bash
docker build -t theplayer .
docker run --rm -p 8080:8080 -p 8189:8189/udp theplayer
#   http://localhost:8080
```

`localhost` is a secure context, so client-side decoding works here without a certificate.

The image bundles two generated sample clips, so there is something to play without a camera:

```
/app/samples/h264-sample.mp4
/app/samples/h265-sample.mp4
```

Paste one of those paths into the address bar. The H.265 one is the interesting case: on a browser
that can decode H.265 it plays with `converted: false`, meaning the server did no codec work at all.

---

## On Fly.io

```bash
fly launch --no-deploy --copy-config    # once; keeps the fly.toml in this repo
fly ips allocate-v4                     # WebRTC needs a dedicated IPv4, not a shared one
fly deploy
```

Then the step that is easy to miss and breaks WebRTC silently if skipped — MediaMTX has to advertise
an address the browser can actually reach, and by default it advertises the one it sees from inside
the machine:

```bash
fly secrets set MediaMtx__WebRtcAdditionalHosts__0=$(fly ips list --json | jq -r '.[0].Address')
```

The double underscore is how .NET reads nested configuration keys from the environment; the trailing
`__0` addresses the first element of the array.

Check it came up:

```bash
curl https://<your-app>.fly.dev/api/health
```

`healthy: true` means MediaMTX is running and FFmpeg was found. Hardware acceleration will report
software-only — there is no GPU on a Fly shared VM — and since Phase 3 that is no longer academic:
anything the browser cannot decode is converted on the CPU, and `encoderFallbacks` in the same
response is where a machine that has quietly dropped to libx264 says so.

---

## Where it is honest to host this

**There is no authentication.** Anyone who finds the URL can start a broadcast, and from Phase 4
read the host's CPU and GPU load. That is a Phase 6 deliverable and it is not built.

What *is* built is a bound on where the server will connect. A server that dials whatever address it
is handed can be used to map the network it sits inside; `AddressPolicy` stops that:

```jsonc
"AddressPolicy": {
  "AllowPrivateNetworks": false,
  "AllowedFileRoots": [ "/app/samples" ]
}
```

Both are on in `appsettings.Production.json`. They refuse RFC 1918, loopback, link-local, CGNAT and
multicast addresses — including `169.254.169.254`, the cloud metadata endpoint that hands out
credentials to anyone who asks — and refuse file paths outside the bundled samples.

So: **a public demo is defensible; a private instance is not yet possible.** If the URL should not
be public, put it behind whatever your host offers (Fly's `--private` networking, Cloudflare Access,
a proxy with basic auth) until the Phase 6 API key lands. [SECURITY.md](SECURITY.md) has the full
picture, including the DNS-rebinding gap that address filtering does not close.

---

## What is still missing from a real deployment

Honest gaps, all of them Phase 6:

- **Authentication.** As above.
- **Killing children with the parent.** A hard kill of the API leaves MediaMTX holding its ports. In
  a container the whole thing goes down together, so this bites far less here than it does natively —
  but a Windows Job Object and `PR_SET_PDEATHSIG` are still the real fix.
- **DNS rebinding.** Address filtering resolves a name, and FFmpeg resolves it again. See
  [SECURITY.md](SECURITY.md).
- **No GPU.** Fine for a browser that can decode the source, which still costs the server nothing.
  For one that cannot, conversion on a shared VM is libx264 on a couple of cores, and it will show —
  one 1080p transcode is about all such a machine has in it.
