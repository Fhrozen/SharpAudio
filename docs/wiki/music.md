---
wiki: music
topic: Music generation — project layout, gRPC contract, domain model, worker scaffold, DI wiring
covers:
  - src/SharpAudio.Worker.Music/**
  - src/SharpAudio.Core/Protos/music.proto
  - src/SharpAudio.Core/Models/**
  - src/SharpAudio.Core/Contracts/**
  - src/SharpAudio.Core/Options/**
  - src/SharpAudio.Core/Services/**
sections:
  project-layout:
    - src/SharpAudio.Worker.Music/SharpAudio.Worker.Music.csproj
  grpc-contract:
    - src/SharpAudio.Core/Protos/music.proto
  domain-model-and-abstractions:
    - src/SharpAudio.Core/Models/MusicModelDefinition.cs
    - src/SharpAudio.Core/Models/MusicGenerationResult.cs
    - src/SharpAudio.Core/Contracts/MusicGenerationRequest.cs
    - src/SharpAudio.Core/Services/IMusicGenerator.cs
  worker-scaffold:
    - src/SharpAudio.Worker.Music/Program.cs
    - src/SharpAudio.Worker.Music/Services/WorkerMusicService.cs
  worker-process-and-di-wiring:
    - src/SharpAudio.Core/Options/MusicWorkerOptions.cs
    - src/SharpAudio.Core/Services/MusicWorkerProxyGenerator.cs
updated: 2026-10-25
---

# SharpAudio Wiki — Music Generation

> Linked from [docs/LLM_WIKI.md](../LLM_WIKI.md). Music generation is a task family of its own: its
> own executable, its own gRPC service, its own port range and its own worker pool. Only the
> scaffolding exists so far — the sampler and the ONNX graphs are a later phase.

## Project layout

`src/SharpAudio.Worker.Music/SharpAudio.Worker.Music.csproj` is a `Microsoft.NET.Sdk.Web` executable
with `Grpc.AspNetCore` and a `ProjectReference` to `SharpAudio.Core`, exactly like the TTS and ASR
worker projects. Contract, models and the worker bootstrap all come from Core; the project itself is
`Program.cs` plus one gRPC service.

The model is `lsb/stable-audio-3-small-music-onnx` (int4, four ONNX graphs), so a music model is a
directory of graphs rather than a single file — that is why `MusicModelDefinition.ModelPath` names the
bundle directory.

## gRPC contract

`src/SharpAudio.Core/Protos/music.proto` defines `service WorkerMusic` with `Generate` and the shared
`HealthCheck` from `common.proto`. Field numbers are append-only: `GenerateRequest` uses 1–3 for model
identity and 10–14 for generation parameters (prompt, negative prompt, duration, steps, seed), and
`GenerateResponse` uses 1–7. The pingpong sampler with 8 steps and no classifier-free guidance is the
default; output is 44.1 kHz stereo.

## Domain model and abstractions

`IMusicGenerator` is the task interface the API endpoint will depend on, so the endpoint can be built
before any engine exists. `MusicGenerationRequest` is the parsed JSON body, `MusicGenerationResult`
carries the audio plus the timings used for metrics, and `MusicModelDefinition` follows the same shape
as the ASR and TTS definitions.

## Worker scaffold

`Program.cs` parses the worker arguments with the music port range (50251) and runs `WorkerHost`, and
`WorkerMusicService` currently implements only `HealthCheck` — generation answers
`StatusCode.Unimplemented` until the sampler lands. The health check is real because the API needs a
live pool to route to: worker registration, port allocation and idle shutdown are all exercised by the
scaffold alone.

## Worker process and DI wiring

`MusicWorkerOptions` is the music config section: its own executable, its own port range (50251) and a
60 s startup timeout. Music is the only task where the *response* carries the bulk of the payload, so
`MaxReceiveMessageBytes` (100 MB) is the large limit and `MaxSendMessageBytes` stays at 10 MB.

`MusicWorkerProxyGenerator` resolves the keyed `WorkerProcessManager` and `WorkerChannelCache` for the
`music` key and calls `Generate` through the cached client with the usual single retry on
`Unavailable`. There is no in-process implementation to fall back to yet, so the proxy is the whole
path — `Enabled = false` is not a supported configuration for this task until an engine exists.
