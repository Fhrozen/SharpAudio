---
wiki: diarization
topic: Speaker diarization — project layout, gRPC contract, domain model, worker scaffold, DI wiring
covers:
  - src/SharpAudio.Worker.Diar/**
  - src/SharpAudio.Core/Protos/diarization.proto
  - src/SharpAudio.Core/Models/**
  - src/SharpAudio.Core/Services/**
  - src/SharpAudio.Core/Options/**
sections:
  project-layout:
    - src/SharpAudio.Worker.Diar/SharpAudio.Worker.Diar.csproj
  grpc-contract:
    - src/SharpAudio.Core/Protos/diarization.proto
  domain-model-and-abstractions:
    - src/SharpAudio.Core/Models/DiarModelDefinition.cs
    - src/SharpAudio.Core/Models/SpeakerSegment.cs
    - src/SharpAudio.Core/Models/DiarizationResult.cs
    - src/SharpAudio.Core/Services/IDiarizer.cs
  worker-scaffold:
    - src/SharpAudio.Worker.Diar/Program.cs
    - src/SharpAudio.Worker.Diar/Services/WorkerDiarService.cs
  worker-process-and-di-wiring:
    - src/SharpAudio.Core/Options/DiarWorkerOptions.cs
    - src/SharpAudio.Core/Services/DiarWorkerProxyDiarizer.cs
updated: 2026-10-25
---

# SharpAudio Wiki — Diarization

> Linked from [docs/LLM_WIKI.md](../LLM_WIKI.md). Diarization assigns a speaker index to each span of
> an audio. Only the scaffolding exists so far — the Sortformer engine is a later phase.

## Why diarization is its own executable

A `diarize=true` transcription request needs the ASR engine *and* Sortformer loaded at the same time.
`WorkerServiceBase` holds a `SingleActiveEnginePool`, which can only hold one engine per worker
process, so hosting Sortformer inside `Worker.Asr` would evict the ASR engine on every diarize
request. A separate executable keeps the two pools independent.

The model is `nvidia/diar_streaming_sortformer_4spk-v2` (CC-BY-4.0), run as a single forward pass. A
segment's speaker is the argmax of the mean frame probabilities, and the existing `VadSegmenter` is
reused per speaker (onset 0.641, offset 0.561, padOnset 0.229 s, padOffset 0.079 s, minGap 0.296 s,
minOn 0.511 s).

## Project layout

`src/SharpAudio.Worker.Diar/SharpAudio.Worker.Diar.csproj` is a `Microsoft.NET.Sdk.Web` executable
with `Grpc.AspNetCore` and a `ProjectReference` to `SharpAudio.Core`, like the other worker projects.

## gRPC contract

`src/SharpAudio.Core/Protos/diarization.proto` defines `service WorkerDiarization` with `Diarize` and
the shared `HealthCheck`. `speaker_count = 0` means "detect it", capped by whatever the model can
separate. Field numbers are append-only.

The ASR contract gained `speaker = 5` on `TranscriptionSegmentProto` (see
[asr-engines](asr-engines.md#vad-and-timestamp-segments)) so a diarized transcription can carry the
speaker per segment; the C# `TranscriptionSegment` mapping is done by the engine phase.

## Domain model and abstractions

`IDiarizer` is the task interface, `SpeakerSegment` is one speaker turn in audio time, and
`DiarizationResult` carries the segments plus timings. `DiarModelDefinition.MaxSpeakers` is a property
of the model (4 for Sortformer v2), not of the request.

## Worker scaffold

`Program.cs` parses the worker arguments with the diarization port range (50351) and runs `WorkerHost`;
`WorkerDiarService` implements only `HealthCheck` for now and answers `Unimplemented` for `Diarize`.

## Worker process and DI wiring

`DiarWorkerOptions` is the diarization config section with its own executable and port range (50351).
Here the *request* carries the audio, so `MaxSendMessageBytes` is the large limit and the response is a
segment list. `MaxDurationSeconds` (600 s default) caps requestable length because a single forward
pass costs grow with duration.

`DiarWorkerProxyDiarizer` resolves the keyed `WorkerProcessManager` and `WorkerChannelCache` for the
`diar` key. As with music, there is no in-process implementation yet, so the proxy is the whole path.
