# Hardware

What this needs from a machine, what it does without, and how to tell which you have.

**Nothing here is required.** Every mode works on a laptop with no GPU at all — conversion falls
back to libx264 and the resource monitor says why the GPU figures are missing. This document is
about making it *fast*, and about the one setup step that decides both halves of that.

---

## The short version

```bash
curl -s localhost:5172/api/health -H "X-Api-Key: $KEY" | jq .hardware
```

That is the answer for the machine you are on. Everything below is how to change it.

| What it says | What it means |
|---|---|
| `hasHardwareAcceleration: false` | Encoding is on the CPU. Works; costs about a core per 1080p stream. |
| A profile listed | That engine was **verified by actually encoding with it**, not merely found. |
| `encoderFallbacks` not empty | Something was detected at startup and refused later. See below. |

---

## Detection is never assumption

An FFmpeg build lists every encoder it was *compiled* with, whether or not this machine has the
device to run it. A stock Windows build advertises `h264_vaapi` and `h264_amf` on hardware that has
neither.

So at startup each advertised profile is confirmed by encoding a two-frame 128×128 test pattern with
it. Anything that fails is not offered. That check costs about a second, once, and it is the
difference between "detected acceleration" meaning *compiled in* and meaning *usable*.

It can be turned off — `FFmpeg:VerifyEncoders: false` — which is occasionally useful for seeing what
a build claims. Expect the claims to be wrong.

### And detection has a shelf life

Verifying an encoder proves it exists. It does not prove a session can be opened an hour later.
NVENC allows three to eight concurrent sessions on a consumer GeForce card, so the fourth
simultaneous broadcast fails at `avcodec_open2` while `/api/health` is still advertising NVENC.

When that happens the broadcast drops to the next engine down the ranking and keeps playing, and
`encoderFallbacks` in the health response records what failed and what replaced it. A machine that
has quietly dropped to libx264 is otherwise indistinguishable from one that is merely slow.

---

## The ranking

Best first. The first one confirmed on the machine is the one used.

| Engine | Encoder | Decode | Notes |
|---|---|---|---|
| NVIDIA | `h264_nvenc` | `cuda` | Frames stay on the device — see below |
| Intel Quick Sync | `h264_qsv` | `qsv` | Frames stay on the device |
| AMD | `h264_amf` | `d3d11va` | Frames come **back** to system memory, deliberately |
| VA-API | `h264_vaapi` | `vaapi` | Linux; needs `/dev/dri/renderD128` |
| Software | `libx264` | — | Always present. The floor, not a failure |

### Keeping frames on the device

```
-hwaccel cuda -hwaccel_output_format cuda -i <input> -c:v h264_nvenc
```

`-hwaccel_output_format` is the flag that matters. Without it every decoded frame is copied out of
GPU memory and back in, which costs more than the encode does.

Two profiles deliberately omit it, and both are correct:

- **AMD**, because the AMF encoder takes frames from system memory. Asking D3D11VA to keep them on
  the device produces `Impossible to convert between the formats` at the first frame.
- **Full server decoding**, in any mode, because the JPEG encoder is software. The decode still runs
  on the GPU there — it is the expensive half.

---

## NVIDIA in a container

GPU encoding and GPU metrics need exactly the same thing, which is why they are one setup step
rather than two: **the NVIDIA Container Toolkit**, and device visibility.

```bash
# Ubuntu / Debian
curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
  | sudo gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
curl -s -L https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
  | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
  | sudo tee /etc/apt/sources.list.d/nvidia-container-toolkit.list

sudo apt-get update && sudo apt-get install -y nvidia-container-toolkit
sudo nvidia-ctk runtime configure --runtime=docker
sudo systemctl restart docker
```

Then check the container can see the card before blaming the application:

```bash
docker run --rm --gpus all nvidia/cuda:12.4.0-base-ubuntu22.04 nvidia-smi
```

`docker-compose.yml` requests `[gpu, video, utility]`. All three are needed and each for a different
reason — `video` is what exposes NVENC and NVDEC, and `utility` is what puts `nvidia-smi` in the
container. Ask for `gpu` alone and encoding works while the resource monitor reports the GPU as
unavailable, which is a confusing place to end up.

Without the toolkit the container still runs. It encodes with libx264 and says so.

---

## GPU metrics are NVIDIA-only

Stated plainly because the alternative is a panel that looks broken on half the machines it runs on.

| Vendor | Why not |
|---|---|
| Intel | `intel_gpu_top` is Linux-only and usually needs elevated privilege |
| AMD | `rocm-smi` is Linux-only |
| Windows, any vendor | Per-process counters only, and they do not decompose into encode and decode |

Those machines get an availability state and a sentence explaining it. **CPU and per-broadcast
figures work everywhere**, and per-broadcast cost is the more interesting number anyway — it is the
one that says what *this stream* costs rather than what the machine is doing.

### The cost of reading them

One `nvidia-smi` process per sample, once a second, while somebody is watching the feed. That is
roughly 30ms of CPU a second, and it is worse than the design originally intended: a single
long-lived `nvidia-smi --loop-ms` returns one good sample and then `[Unknown Error]` for every field,
forever. Repeated single-shot invocation is stable, so that is what runs.

Sampling stops entirely when the last listener disconnects.

---

## Windows stays native

Docker Desktop for Windows translates container traffic, and WebRTC ICE does not survive it — the
container's view of its own address is not one a browser can reach. The `ServerAssisted` mode would
be the casualty, which is half the point of the project.

So the supported Windows path is `dotnet run`, and `docker-compose.yml` targets Linux with host
networking. This is a real limitation rather than an oversight, and it is documented here rather
than worked around badly.

The one Windows-specific thing worth knowing is now fixed: a `taskkill /F` of the server used to
leave MediaMTX holding ports 8554, 8889 and 9997. Children are now bound to a Job Object with
`KILL_ON_JOB_CLOSE`, so the kernel takes them down with the server no matter how it dies.

---

## LAN access

WebRTC needs the server to advertise an address the client can reach. On a single machine that is
automatic; across a network it is not.

```jsonc
"MediaMtx": {
  "WebRtcAdditionalHosts": [ "192.168.1.50" ]   // this host, as the LAN sees it
}
```

Without it the browser gathers candidates that go nowhere, and the symptom is the worst kind: the
signalling succeeds, the page reports no error, and no video ever arrives. `docker-compose.yml`
sidesteps it with host networking; a bridged container or a different port mapping does not.
