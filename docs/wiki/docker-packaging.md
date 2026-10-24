---
wiki: docker-packaging
topic: Docker packaging, image stages and dev/build scripts
covers:
  - Dockerfile
  - Dockerfile.tests
  - docker/**
  - dotnet.sh
  - dotnet.ps1
  - generate-cert.sh
sections:
  image-stages:
    - Dockerfile
    - Dockerfile.tests
    - docker/mkcert/Dockerfile
    - docker/mkcert/entrypoint.sh
  build-and-dev-scripts:
    - dotnet.sh
    - dotnet.ps1
    - generate-cert.sh
updated: 2026-10-24
---

# SharpAudio Wiki — Docker Packaging

> Linked from [docs/LLM_WIKI.md](../LLM_WIKI.md).

## Image stages

Multi-stage `Dockerfile`, 3 selectable final images sharing a common `runtime-base` stage:
`docker build --target runtime-tts|runtime-asr|runtime-all -t <tag> .` (omitting `--target` builds
`runtime-all`, the last/default stage). `backend-build` publishes `SharpAudio.Api`,
`SharpAudio.Worker`, and `SharpAudio.Worker.Asr` with `-r linux-x64 --self-contained false`.
`runtime-base` installs `libespeak-ng1`/`espeak-ng-data` (Kokoro) + `libgomp1` (Whisper.net's
native `ggml-cpu` library needs OpenMP). `runtime-asr`/`runtime-all` additionally set
`ENV LD_LIBRARY_PATH=/app/runtimes/linux-x64:/app/worker-asr/runtimes/linux-x64` — **required** for
Whisper.net's native libs to load at all (see "Known fixes" below), and install `ffmpeg` (universal
audio input format support — see [docs/wiki/asr-engines.md](asr-engines.md)). `docker-compose.yml`
passes through `SERVER_MODE` + `AsrWorkerOptions__*` env vars and has commented example services
for TTS-only/ASR-only deployments using `target: runtime-tts`/`runtime-asr`. It also maps an
optional HTTPS port (`HOST_HTTPS_PORT:HTTPS_PORT`, both default `5769`) and mounts `./certs:/certs:ro`
plus `Kestrel__Certificates__Default__Path`/`Password` env vars (only meaningful once `HTTPS_PORT`
is set) - see `./generate-cert.sh` and the "HTTPS / TLS" section of `docs/CONFIGURATION.md` for the
self-signed LAN-hostname cert flow; `Program.cs` auto-redirects HTTP → HTTPS once `HTTPS_PORT` is set.

Used Docker's native `--target <stage>` mechanism (not a build `ARG` + `FROM runtime-${ARG}`
trick) — the standard, better-supported idiom for "one Dockerfile, multiple selectable final
images".

## Build and dev scripts

- `dotnet.sh` — the Docker-based `dotnet` wrapper this repo standardizes on, since there is no local
  `dotnet` CLI in the dev environment. It runs the SDK image with the workspace mounted and passes
  arguments straight through, so `./dotnet.sh build SharpAudio.slnx` behaves like `dotnet build`.
  `dotnet.ps1` is the Windows equivalent.
- `generate-cert.sh` — produces the self-signed LAN-hostname cert that `docker-compose.yml` mounts at
  `/certs`; pairs with the `Kestrel__Certificates__Default__*` env vars described above.
- `docker/mkcert/` — an alternative cert source: a small image that runs `mkcert` and emits the
  CA + leaf files the compose service expects, for teams that already trust `mkcert` rather than a
  hand-generated self-signed cert.
- `Dockerfile.tests` — the image `tests/run-tests.sh` builds and runs inside, which is the only place
  the ffmpeg and native-library fixes above are actually exercised.

## Known fixes

1. **Whisper.net native library load failure** (`"Cannot load the library... PInvokeError:
   Success"`): `Whisper.net.Runtime` ships native `.so` files under `runtimes/<rid>/` instead of the
   standard `runtimes/<rid>/native/` layout, so sibling dependencies (`libggml-*.so`) aren't found
   without help. Fixed via the `LD_LIBRARY_PATH` `ENV` above (installing `libgomp1` alone was
   necessary but insufficient). Confirmed fixed in both in-process and full worker mode.
2. **Whisper.net requires exactly 16kHz mono input, no internal resampling**: `WhisperAsrEngine`
   calls `WavAudioUtils.ResampleToMono16kWav(wavBytes)` before handing audio to Whisper.net
   (mirrors what Nemotron's pipeline already did). Without this, any non-16kHz source (e.g. Kokoro
   at 24kHz, Supertonic at 22050Hz) throws `NotSupportedWaveException`.
3. **ffmpeg stdout-pipe WAV headers**: piping ffmpeg's WAV output through a non-seekable
   `pipe:1` makes it write placeholder `0xFFFFFFFF` RIFF/data chunk sizes — see
   [docs/wiki/asr-engines.md](asr-engines.md)'s "Universal audio input format support" section for
   the fix (`AudioFormatConverter` patches the header itself).

## Full detail
See [docs/ASR_IMPLEMENTATION_HISTORY.md#phase-6](../ASR_IMPLEMENTATION_HISTORY.md#phase-6--dockercompose-packaging)
for the complete validation log (exact commands run, both in-process and full-worker-mode test
results).
