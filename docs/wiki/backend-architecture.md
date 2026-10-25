---
wiki: backend-architecture
topic: Backend architecture — request flow, worker processes, core abstractions, TTS engines
covers:
  - src/SharpAudio.Api/Program.cs
  - src/SharpAudio.Api/SharpAudio.Api.csproj
  - src/SharpAudio.Core/Models/**
  - src/SharpAudio.Core/Contracts/**
  - src/SharpAudio.Core/Options/**
  - src/SharpAudio.Core/Protos/synthesis.proto
  - src/SharpAudio.Core/Services/**
  - src/SharpAudio.Worker.Tts/**
sections:
  request-flow:
    - src/SharpAudio.Api/Program.cs
  worker-mode-vs-in-process-mode:
    - src/SharpAudio.Core/Services/TtsSynthesizerRouter.cs
    - src/SharpAudio.Core/Services/WorkerProxySynthesizer.cs
    - src/SharpAudio.Core/Services/KokoroTtsSynthesizer.cs
    - src/SharpAudio.Core/Services/SupertonicTtsSynthesizer.cs
    - src/SharpAudio.Core/Services/SynthesisFallback.cs
  worker-process-lifecycle:
    - src/SharpAudio.Core/Services/WorkerProcessManager.cs
    - src/SharpAudio.Core/Services/IdleMonitor.cs
  grpc-contract:
    - src/SharpAudio.Core/Protos/synthesis.proto
    - src/SharpAudio.Worker.Tts/Services/WorkerSynthesisService.cs
  worker-entry-points:
    - src/SharpAudio.Worker.Tts/Program.cs
    - src/SharpAudio.Worker.Tts/SharpAudio.Worker.Tts.csproj
  core-abstractions:
    - src/SharpAudio.Core/Services/EnginePool.cs
    - src/SharpAudio.Core/Services/ITtsSynthesizer.cs
    - src/SharpAudio.Core/Services/IModelCatalog.cs
    - src/SharpAudio.Core/Services/IModelCache.cs
    - src/SharpAudio.Core/Services/IEngineWarmer.cs
    - src/SharpAudio.Core/Services/IIdleTracking.cs
    - src/SharpAudio.Core/Services/IdleTrackingAggregate.cs
    - src/SharpAudio.Core/Services/IdleMonitorService.cs
    - src/SharpAudio.Core/Services/ModelCatalogBase.cs
    - src/SharpAudio.Core/Services/ModelCatalogConfigReader.cs
    - src/SharpAudio.Core/Models/SynthesisResult.cs
  model-definition-and-config:
    - src/SharpAudio.Core/Models/IModelDefinition.cs
    - src/SharpAudio.Core/Models/TtsModelDefinition.cs
    - src/SharpAudio.Core/Models/ModelAsset.cs
    - src/SharpAudio.Core/Services/ModelCatalog.cs
    - src/SharpAudio.Core/Services/KokoroMetadata.cs
    - src/SharpAudio.Core/Services/SupertonicMetadata.cs
  model-cache-and-warmup:
    - src/SharpAudio.Core/Services/ModelCache.cs
    - src/SharpAudio.Core/Services/ModelWarmupService.cs
    - src/SharpAudio.Core/Models/IVoiceDownloadable.cs
  engines:
    - src/SharpAudio.Core/Services/KokoroTtsEngine.cs
    - src/SharpAudio.Core/Services/SupertonicTtsEngine.cs
    - src/SharpAudio.Core/Services/Phonemization/EspeakWrapper.cs
  config-and-env-vars:
    - src/SharpAudio.Core/Options/WorkerOptions.cs
    - src/SharpAudio.Core/Options/ModelCacheOptions.cs
    - src/SharpAudio.Core/Options/ModelIdleMonitorOptions.cs
  endpoints:
    - src/SharpAudio.Core/Contracts/OpenAiSpeechRequest.cs
    - src/SharpAudio.Core/Contracts/ModelDefinitionResponse.cs
    - src/SharpAudio.Core/Contracts/ErrorResponse.cs
    - src/SharpAudio.Core/Contracts/ServerInfoResponse.cs
  text-sanitization:
    - src/SharpAudio.Core/Services/TextSanitizer.cs
  build-tooling-note:
    - src/SharpAudio.Api/SharpAudio.Api.csproj
updated: 2026-11-06
---

# SharpAudio Wiki — Backend Architecture (TTS baseline)

> Linked from [docs/LLM_WIKI.md](../LLM_WIKI.md). Covers the pre-ASR / TTS-side architecture:
> request flow, worker-mode vs in-process mode, the worker process lifecycle, the gRPC contract,
> core abstractions, the model definition/config schema, the TTS engines themselves, and the
> TTS-only env var/endpoint tables. For the ASR-side equivalents, see
> [docs/wiki/asr-engines.md](asr-engines.md).

## Request flow
```
HTTP request → Program.cs minimal API endpoint → IModelCatalog (validate model) →
IModelCache (ensure model files downloaded) → ITtsSynthesizer (do the work) → response
```

Per-connection allocation in `Program.cs` is deliberately flat, because streaming and upload requests are
the paths that repeat:
- The WebSocket streaming loop (`RunStreamingTranscriptionAsync`) rents **one `ArrayPool<byte>` buffer for
  the life of the connection** and grows it by doubling only when a single message exceeds the current
  capacity, so steady-state frames allocate nothing. Sessions receive a slice of it, which is safe only
  because `IStreamingTranscriptionSession` copies what it needs during the call (see
  [asr-engines](asr-engines.md#domain-model-and-abstractions)).
- Sends reuse one `ArrayBufferWriter<byte>` plus one `Utf8JsonWriter` per connection (`Clear()` + `Reset()`
  per message) instead of allocating a `SerializeToUtf8Bytes` array per reply.
- Uploads read into an exact-size `new byte[file.Length]` with a bounded loop, replacing a `MemoryStream`
  that grew by reallocation and then copied itself again with `ToArray()` — two buffers per upload.

## Worker-mode vs in-process mode
Controlled by `WorkerOptions.Enabled` (config section `WorkerOptions`, default `true`):
- **Worker mode (default)**: `WorkerProcessManager` (singleton + `IHostedService`) spawns/pools a
  separate `SharpAudio.Worker.Tts` OS process per model; `WorkerProxySynthesizer` is registered as
  `ITtsSynthesizer` and talks to the worker over gRPC.
- **In-process mode**: `KokoroTtsSynthesizer` + `SupertonicTtsSynthesizer` singletons are
  registered, and `TtsSynthesizerRouter` (registered as `ITtsSynthesizer`) routes each request to
  the right one by `model.Engine`. `IdleMonitorService` (hosted service, one instance per task type) periodically
  releases idle in-process engines through `IIdleTracking`.
- Every TTS path returns the same silence response when an engine cannot produce audio:
  `SynthesisFallback.Silence(model, request, startTime, sampleRate)` (duration = input length / 30
  chars-per-second, clamped 0.5-8 s). WAV output is produced by the repo's single RIFF writer,
  `WavAudioUtils.WrapPcmAsWav` — no synthesizer builds its own header.

## Worker process lifecycle
- Pools **one process per model**, keyed by `modelKey = "{model.Engine}:{model.Name}"`.
- Spawns via `ProcessStartInfo(FileName = WorkerOptions.ExecutablePath, Arguments = "--port {p}
  --model-key \"{k}\" --idle-timeout {t}")`.
- Waits for the worker to print `READY:{port}` to stdout before considering it up. The `READY:(\d+)`
  pattern is `[GeneratedRegex]` because it runs on every output line, not just the handshake.
- Ports allocated starting at `WorkerOptions.PortRangeStart` (default 50051), up to
  `MaxPortAttempts` (default 100).
- Worker self-terminates after `IdleTimeoutSeconds` (default 60) of inactivity (see
  `Core/Services/IdleMonitor.cs`, which every worker project reaches through the shared
  `SharpAudio.Core` library rather than a ProjectReference to the API).
- Communication is gRPC over HTTP/2 with per-task message-size limits from `WorkerTaskOptions`
  (TTS: `MaxReceiveMessageSize` 100MB / `MaxSendMessageSize` 10MB for large audio payloads; the
  limits are directional per task — see [music.md](music.md) and [diarization.md](diarization.md)).
- Its constructor takes a plain `WorkerTaskOptions` value (not `IOptions<WorkerOptions>`), so multiple
  independently-configured instances (one per task type — TTS, ASR, music, diarization) can be
  constructed manually in `Program.cs` and registered via keyed DI.

## gRPC contract
- `service WorkerSynthesis { rpc Synthesize(...); rpc HealthCheck(...); }`
- `SynthesizeRequest`: model_name, engine, model_path, voices_path, tokens_path,
  voice_file_extension, supported_languages, speakers, input, voice, response_format, speed,
  language.
- `SynthesizeResponse`: audio_bytes, content_type, file_name, processing_time_seconds,
  audio_duration_seconds, character_count.
- `SharpAudio.Worker.Tts.csproj` has a `ProjectReference` to `SharpAudio.Api.csproj` and includes the same
  `.proto` file (`GrpcServices="Server"`) — this is how the worker reuses `KokoroTtsEngine` /
  `SupertonicTtsEngine`, which physically live under `src/SharpAudio.Core/Services/`.
- `Worker/Services/WorkerSynthesisService.cs` implements the gRPC service: routes by
  `request.Engine` string (`"kokoro"` vs `"supertonic"`/`"supertonic-3"`), lazily creates and pools
  the engine, and reports activity to `IdleMonitor` on every call.

## Worker entry points

`src/SharpAudio.Worker.Tts/Program.cs` is the TTS worker's entry point: it parses `--port`, `--model-key`
and `--idle-timeout` (the exact arguments `WorkerProcessManager` passes), builds a minimal gRPC host
serving `WorkerSynthesisService`, and prints `READY:{port}` to stdout so the API can consider the
worker up. It has no HTTP endpoints and no DI beyond the gRPC service and `IdleMonitor` — the ASR
counterpart is `src/SharpAudio.Worker.Asr/Program.cs` (see [asr-engines.md](asr-engines.md)).

## Core abstractions
- `ITtsSynthesizer.SynthesizeAsync(TtsModelDefinition model, string modelDirectory,
  OpenAiSpeechRequest request, CancellationToken ct) -> Task<SynthesisResult>` — the single
  synthesis contract, implemented by `WorkerProxySynthesizer`, `TtsSynthesizerRouter`,
  `KokoroTtsSynthesizer`, `SupertonicTtsSynthesizer`.
- `IIdleTracking { GetLoadedEngines(); TryReleaseEngine(key); }` — the single idle-tracking contract for
  every engine owner (TTS, ASR, PnC), so `IdleMonitorService` needs no per-task interface. It replaced the
  former `IIdleTrackingSynthesizer` / `IIdleTrackingTranscriber` pair, which were identical.
- `IdleTrackingAggregate` — combines several `IIdleTracking` sources into one view (release is tried in
  source order, disposal forwards to each disposable source). The routers and `PunctuatingTranscriber` are
  thin wrappers over it, so merge/dispose logic exists once.
- `EnginePool<T>` (`src/SharpAudio.Core/Services/EnginePool.cs`) — the repo's single lazily-built
  engine pool, used by `KokoroTtsSynthesizer`, `SupertonicTtsSynthesizer`, `WhisperAsrTranscriber`,
  `NemotronAsrTranscriber` and `PncEnginePool`. Engines are keyed by `"{model.Name}:{modelDirectory}"`
  (PnC uses `"{model.Engine}:{model.Name}"`), built outside the pool lock so a slow model load does not
  block other callers, timestamped for idle monitoring, individually releasable via
  `TryReleaseEngine(key)`, and disposed on `Dispose()`. `T` is only constrained to `class`, so
  non-disposable engine contracts (e.g. `ISileroVadEngine`) work too. The worker-side variant is
  `SingleActiveEnginePool` (see [worker-core.md](worker-core.md)), which additionally evicts other
  kinds when a new model is loaded.
- `IModelLookup<T> { GetSupportedModels(); TryGetModel(name, out model); }` — the lookup contract every
  task catalog exposes (`IModelCatalog`, `IAsrModelCatalog`, `IPncModelCatalog`), where `T` is an
  `IModelDefinition`.
- `ModelCatalogBase<T>` — the storage every catalog shares: a case-insensitive name index built once at
  construction, with no per-call array copy. A catalog supplies its own config section, defaults and
  extras (PnC overrides `TryGetModel` to fall back to its default model) and inherits the rest.
- `ModelCatalogConfigReader` — the config-file reading every catalog shares: `MODEL_CONFIG_PATH` (else
  `config.json` next to the executable), case-insensitive JSON, and the same failure behaviour. Each
  catalog picks the section it owns (`models`, `asrModels`, `pncModels`).
- `IModelCache.EnsureModelAsync(TtsModelDefinition model, CancellationToken ct) -> Task<string>` —
  downloads/verifies a model's asset files into `MODEL_CACHE_DIR/{model.Name}/`, returns the local
  directory path. Has a second overload for `AsrModelDefinition` (see asr-engines.md) and a third for
  `PncModelDefinition` (see pnc.md). Uses a
  per-model-name `SemaphoreSlim` internally to serialize concurrent ensure-calls for the same model
  (fixes a real concurrent-download race — see [docs/wiki/testing.md](testing.md)).
- `IModelCache.IsModelEnsured(model)` — whether a model has been fully ensured at least once; warmup
  uses it so an engine is never built on top of a partial download.
- `IEngineWarmer<T> { Task<bool> WarmUpAsync(T model, string modelDirectory, CancellationToken ct); }`
  — the engine-building capability every task's implementation already has, exposed as an interface so
  startup can prepare engines as well as files. The implementations are the same services that serve
  requests (`KokoroTtsSynthesizer`, `SupertonicTtsSynthesizer`, `WhisperAsrTranscriber`,
  `NemotronAsrTranscriber`, both routers, and the two worker proxies); each returns `false` for an
  engine it does not handle, and a failed engine build is allowed to throw so the warmup service logs
  it and continues. A worker proxy warms by pre-spawning the child process for the model — a worker
  has no catalog, only `--model-key`, so it cannot prepare anything on its own.
- **Strategy/Factory pattern**: `TtsSynthesizerRouter` picks an engine by `model.Engine` string
  (`SupertonicMetadata.IsSupertonic3Engine(...)` else falls back to Kokoro). This is the pattern to
  mirror when adding new task types/engines.
- **Decorator pattern**: `PunctuatingTranscriber` wraps `IAsrTranscriber` and applies PnC after the
  ASR pass when the request asks for it, so the router, the worker proxy and idle monitoring keep
  working unchanged (see [docs/wiki/pnc.md](pnc.md)). In worker mode the same application happens in
  the ASR worker instead, so it is never applied twice.

## Model definition and config
```csharp
public sealed class TtsModelDefinition
{
    public string Name { get; init; }              // "kokoro-q4", "supertonic-3"
    public string DisplayName { get; init; }
    public string Description { get; init; }
    public string Engine { get; init; }             // "kokoro", "supertonic-3"
    public string ModelPath { get; init; }           // relative path within model cache dir
    public string? VoicesPath { get; init; }
    public string? VoicesBaseUrl { get; init; }
    public string VoiceFileExtension { get; init; }
    public string? TokensPath { get; init; }
    public IReadOnlyList<ModelAsset> Assets { get; init; }   // ONNX/asset files to download (shared type)
    public IReadOnlyList<string> SupportedLanguages { get; init; }
    public IReadOnlyList<string> Speakers { get; init; }
}
```
`ModelCatalog` loads from `MODEL_CONFIG_PATH` env var, or falls back to
`{AppContext.BaseDirectory}/config.json`, top-level `"models"` array; falls back to hardcoded
defaults if the file is missing. `ApplyCanonicalMetadata()` enriches entries with languages/speakers
per engine (`KokoroMetadata`, `SupertonicMetadata`). `ModelAsset` (`Url`, `RelativePath`) is a
generic asset descriptor shared by both TTS and ASR model definitions.

The shape the three task definitions have in common — `Name`, `DisplayName`, `Description`, `Engine`,
`Assets`, `SupportedLanguages` — is `Models/IModelDefinition.cs`, which is what `ModelCatalogBase<T>`
is constrained to. Task-specific definitions add their own paths on top of it (`VoicesPath` for TTS,
`SupportsVad` for ASR, `OnnxPath` for PnC), so a catalog, a cache or a warmup service can work against
the shared shape instead of duplicating per-task code.

## Model cache and warmup

`ModelCache` implements `IModelCache`: it resolves `MODEL_CACHE_DIR/{model.Name}/`, downloads each
`ModelAsset` that is missing from disk, and returns the local directory. Downloads for the same model
name are serialized by a per-name `SemaphoreSlim` (see
[testing.md](testing.md#modelcache-concurrent-download-race)). A model that has been fully ensured
once is memoized in a `ConcurrentDictionary`, so the per-request path is a dictionary lookup instead
of a stat per asset; the trade-off is that cache files deleted after the first ensure are not noticed
again for the life of the process. Only *complete* ensures are recorded, so a partial failure is
retried on the next call, and an `IHttpClientFactory` client is created only when something is
actually missing — a warm cache never builds one. `IModelCache` has one method taking an
`IModelDefinition`, so TTS, ASR and PnC no longer need three overloads; the TTS-only voice download
hangs off the `IVoiceDownloadable` hook, which `TtsModelDefinition` implements, and the missing-voice
list is computed up front so a warm voice cache likewise needs no client.
`ModelWarmupService<T>` is the hosted service that pre-ensures and, when a warmer is supplied,
pre-builds each model's engine at startup, so the first request pays for neither. It is generic over
the catalog, so one implementation serves every task type — `Program.cs` registers
`ModelWarmupService<TtsModelDefinition>` and `ModelWarmupService<AsrModelDefinition>`, each with the
warmer DI resolves for that task (a router in-process, a worker proxy in worker mode).
Warmup is the only place the process prepares every model at once: models run concurrently, bounded by
`ModelCacheOptions.WarmupConcurrency` (4), which is safe because `ModelCache` locks per model name.
A model whose ensure failed is never warmed, a failed engine build is logged without stopping the
rest of startup, and cancellation stops the pass without logging the skipped models as failures.
`WarmEngines` (default true) turns engine warming off; `RunAsync` is public so tests can run a pass
without a host.

## Engines
- **Kokoro** (`KokoroTtsEngine.cs`/`KokoroTtsSynthesizer.cs`): single ONNX session via
  `Microsoft.ML.OnnxRuntime`, phonemization via `EspeakWrapper` (wraps espeak-ng native lib),
  24000 Hz output, pooled per model+directory through the shared `EnginePool<T>`.
- **Supertonic-3** (`SupertonicTtsEngine.cs`/`SupertonicTtsSynthesizer.cs`): **4 chained ONNX
  sessions** (text_encoder, duration_predictor, vector_estimator/denoiser, vocoder), iterative
  flow-matching denoising loop, 22050 Hz output, config read from `tts.json` inside the model dir.
  **This multi-session-in-one-class pattern is the template for any future ONNX-chain engine** (see
  Nemotron in [docs/wiki/asr-engines.md](asr-engines.md)).
  Its text preprocessing runs on every request, so the patterns are `[GeneratedRegex]` and the seven
  punctuation-spacing fixes are one pass (` ([,.!?;:?'])` → the mark). `ChunkText` builds each chunk in
  a `StringBuilder` instead of concatenating sentences.
- **espeak phonemization** (`Phonemization/EspeakWrapper.cs`): the whitespace and trailing-punctuation
  patterns are `[GeneratedRegex]` and phonemes are accumulated in a `StringBuilder`. The espeak lock
  stays **static** — `espeak_TextToPhonemes()` returns a pointer into a process-global buffer that a
  second call can reallocate, so a per-instance lock would protect nothing.

## Config and env vars
| Env var | Purpose | Default |
|---|---|---|
| `HTTP_PORT` / `HTTPS_PORT` | Kestrel listen ports | 8080 / (none) |
| `MODEL_CACHE_DIR` | Model download cache dir | `{TempPath}/sharp-audio-cache` |
| `MODEL_CONFIG_PATH` | Path to `config.json` | `{BaseDirectory}/config.json` |
| `ModelCache__WarmupConcurrency` | models startup warmup may prepare at the same time | 4 |
| `ModelCache__WarmEngines` | warm engines/workers as well as downloading files | true |
| `MODEL_IDLE_TIMEOUT_SECONDS` | In-process engine idle release timeout | 60 |
| `ESPEAK_DATA_DIR` | espeak-ng phoneme data dir (Kokoro) | — |
| `WorkerOptions__Enabled` | worker-mode vs in-process | true |
| `WorkerOptions__ExecutablePath` | TTS worker binary path | `./SharpAudio.Worker.Tts` |
| `WorkerOptions__PortRangeStart` | first gRPC port to try | 50051 |
| `WorkerOptions__IdleTimeoutSeconds` | worker self-termination timeout | 60 |
| `ASPNETCORE_ENVIRONMENT` | environment name | Production |
| `SERVER_MODE` | `tts`\|`asr`\|`both` — which task type(s) this instance serves | `tts` |

See [docs/wiki/asr-engines.md](asr-engines.md) for the ASR-specific env vars
(`AsrWorkerOptions__*`).

## Endpoints
| Route | Method | Purpose |
|---|---|---|
| `/health` | GET | health check |
| `/api/server-info` | GET | `{ ttsEnabled, asrEnabled }`, reflects `SERVER_MODE` |
| `/api/models` | GET | list TTS models w/ languages/speakers/metadata (only if `ttsEnabled`) |
| `/v1/models` | GET | OpenAI-compatible model listing, merges TTS+ASR catalogs |
| `/v1/audio/speech` | POST | OpenAI-compatible TTS synthesis (only if `ttsEnabled`); returns audio
  bytes + metrics in response headers (`X-Processing-Time`, `X-Chars-Per-Second`, `X-RTF`,
  `X-Audio-Duration`, `X-Character-Count`) |
| `/swagger` | GET | interactive API docs |

See [docs/wiki/asr-engines.md](asr-engines.md) for the ASR-specific endpoints
(`/api/asr-models`, `/v1/audio/transcriptions`, `/v1/audio/transcriptions/stream`).

Static frontend files are served from `wwwroot` (built by the frontend and copied in at Docker
build time); `MapFallbackToFile("/index.html")` handles SPA routing.

## Text sanitization

`TextSanitizer` normalizes inbound text before it reaches an engine or the PnC pass: it strips
control characters, collapses runs of whitespace, and trims the string to the configured length limit.
It is applied at the endpoint boundary, so engines and tokenizers always see clean input. Full rule-by-rule
detail, including the character classes and the reason each rule exists, is in
[docs/TEXT_SANITIZATION.md](../TEXT_SANITIZATION.md).

The six patterns are `[GeneratedRegex]` (source-generated, no interpretation or static `Regex` cache
lookup per request). That moved the benchmark from 34.66 µs to 33.37 µs on the ~2.6 KB markdown input;
allocated memory is unchanged at 138.35 KB because the cost is the `string.Replace` chain, not the
regex engine — the remaining hot spot is the chain itself, not pattern compilation.

## Adding a new TTS engine
1. Add a `{Engine}TtsEngine.cs` (owns ONNX session(s), pooled per model+directory — mirror
   `KokoroTtsEngine` for single-session or `SupertonicTtsEngine` for multi-session chains) and a
   `{Engine}TtsSynthesizer.cs` implementing `ITtsSynthesizer`/`IIdleTracking` (usually by holding an
   `EnginePool<T>`, which already implements it).
2. Add routing for the new `Engine` string in `TtsSynthesizerRouter` and
   `Worker/Services/WorkerSynthesisService.cs`.
3. Add model entries to `config.json` `"models"` array.
4. Register the synthesizer singleton in `Program.cs` (in-process branch).

## Build tooling note
No local `dotnet` CLI in this dev environment — use `./dotnet.sh <args>` (Docker-based wrapper) for
build/test/publish, e.g. `./dotnet.sh build SharpAudio.slnx`, `./dotnet.sh test
tests/SharpAudio.Api.Tests`.
