---
wiki: asr-engines
topic: ASR engines (Whisper + Nemotron), VAD, audio input, ASR worker wiring
covers:
  - src/SharpAudio.Core/Models/**
  - src/SharpAudio.Core/Contracts/**
  - src/SharpAudio.Core/Options/**
  - src/SharpAudio.Core/Protos/transcription.proto
  - src/SharpAudio.Core/Services/**
  - src/SharpAudio.Worker.Asr/**
  - frontend/src/audio-worklets/**
sections:
  domain-model-and-abstractions:
    - src/SharpAudio.Core/Models/AsrModelDefinition.cs
    - src/SharpAudio.Core/Models/TranscriptionResult.cs
    - src/SharpAudio.Core/Services/IAsrModelCatalog.cs
    - src/SharpAudio.Core/Services/AsrModelCatalog.cs
    - src/SharpAudio.Core/Services/IAsrTranscriber.cs
    - src/SharpAudio.Core/Services/IStreamingTranscriptionSession.cs
    - src/SharpAudio.Core/Contracts/AudioTranscriptionRequest.cs
    - src/SharpAudio.Core/Contracts/AsrModelDefinitionResponse.cs
  whisper-engine:
    - src/SharpAudio.Core/Services/WhisperAsrEngine.cs
    - src/SharpAudio.Core/Services/WhisperAsrTranscriber.cs
    - src/SharpAudio.Core/Services/WhisperStreamingSession.cs
    - src/SharpAudio.Core/Services/WavAudioUtils.cs
  nemotron-engine:
    - src/SharpAudio.Core/Services/ByteRingBuffer.cs
    - src/SharpAudio.Core/Services/NemotronAsrEngine.cs
    - src/SharpAudio.Core/Services/NemotronFeatureExtractor.cs
    - src/SharpAudio.Core/Services/NemotronVocabulary.cs
    - src/SharpAudio.Core/Services/NemotronLanguages.cs
    - src/SharpAudio.Core/Services/NemotronAsrTranscriber.cs
  universal-audio-input-format-support:
    - src/SharpAudio.Core/Services/AudioFormatConverter.cs
  vad-support:
    - src/SharpAudio.Core/Services/SileroVadEngine.cs
    - src/SharpAudio.Core/Services/SileroVadGate.cs
    - src/SharpAudio.Core/Services/VadWindowStream.cs
    - src/SharpAudio.Core/Services/VadSegmenter.cs
    - src/SharpAudio.Core/Services/VadEndpointer.cs
    - src/SharpAudio.Core/Services/VadSpanTranscriber.cs
    - src/SharpAudio.Core/Services/VadPipeline.cs
    - src/SharpAudio.Core/Options/VadOptions.cs
    - src/SharpAudio.Core/Models/SpeechSpan.cs
    - src/SharpAudio.Core/Models/SpanTranscription.cs
  worker-process-di-wiring-and-server-mode:
    - src/SharpAudio.Core/Services/AsrWorkerProxyTranscriber.cs
    - src/SharpAudio.Core/Services/AsrTranscriberRouter.cs
    - src/SharpAudio.Core/Options/AsrWorkerOptions.cs
    - src/SharpAudio.Core/Options/AsrStreamingOptions.cs
    - src/SharpAudio.Core/Protos/transcription.proto
    - src/SharpAudio.Worker.Asr/Program.cs
    - src/SharpAudio.Worker.Asr/Services/WorkerTranscriptionService.cs
  rest-streaming-endpoints:
    - src/SharpAudio.Core/Services/SegmentBoundaryPolicy.cs
  vad-and-timestamp-segments:
    - src/SharpAudio.Core/Services/SegmentMerger.cs
    - src/SharpAudio.Core/Models/TranscriptionSegment.cs
updated: 2026-11-06
---

# SharpAudio Wiki — ASR Engines (Whisper + Nemotron)

> Linked from [docs/LLM_WIKI.md](../LLM_WIKI.md). Covers the full ASR-side architecture: domain
> model/catalog/contracts, the Whisper and Nemotron engines (including their bugfix narratives),
> universal audio format support, VAD support, worker process/DI wiring, REST + streaming
> endpoints, and the current known issues/roadmap. For the TTS-side equivalents, see
> [docs/wiki/backend-architecture.md](backend-architecture.md). For the phase-by-phase build log
> this section summarizes, see [docs/ASR_IMPLEMENTATION_PLAN.md](../ASR_IMPLEMENTATION_PLAN.md)
> and [docs/ASR_IMPLEMENTATION_HISTORY.md](../ASR_IMPLEMENTATION_HISTORY.md).

## Domain model and abstractions
Parallel abstraction to the TTS one, added alongside it (not replacing it):
- `Models/AsrModelDefinition.cs`: `Name`, `DisplayName`, `Description`, `Engine` (`"whisper"` |
  `"nemotron-3.5"`), `ModelPath`, `Assets` (`IReadOnlyList<ModelAsset>`, shared type with TTS),
  `SupportedLanguages`, `SupportsLanguageAutoDetect`, `SupportsVad`, `SupportsStreaming`.
- `Services/IAsrModelCatalog.cs` + `AsrModelCatalog.cs` (mirrors `ModelCatalog`): loads the
  `"asrModels"` array from the same `config.json` (same `MODEL_CONFIG_PATH` env var), falls back to
  hardcoded defaults (`whisper-base`, `nemotron-3.5`) if config is missing.
- `Services/IAsrTranscriber.cs`: `TranscribeAsync(AsrModelDefinition model, string modelDirectory,
  AudioTranscriptionRequest request, byte[] audioBytes, CancellationToken ct) ->
  Task<TranscriptionResult>` — the ASR equivalent of `ITtsSynthesizer`.
- `Services/IIdleTracking.cs` — the single idle-tracking contract shared with the TTS side (see
  [backend-architecture](backend-architecture.md#core-abstractions)); ASR transcribers implement it directly
  or through `IdleTrackingAggregate`.
- `Models/TranscriptionResult.cs`: `Text`, `DetectedLanguage`, `ProcessingTimeSeconds`,
  `AudioDurationSeconds`, `CharacterCount`, computed `Rtf`.
- `Contracts/AudioTranscriptionRequest.cs` (`Model`, `Language?`, `ResponseFormat`, `EnableVad` —
  the file itself is passed as a separate `byte[]`), `Contracts/AsrModelDefinitionResponse.cs`,
  `Contracts/ServerInfoResponse.cs` (`{ TtsEnabled, AsrEnabled }`, backs `GET /api/server-info`).
- `IModelCache` gained a second overload: `EnsureModelAsync(AsrModelDefinition, ct)`, sharing the
  same per-model-name locking as the TTS overload (see [docs/wiki/testing.md](testing.md)).
- `config.json` has a sibling top-level `"asrModels"` array (same shape philosophy as `"models"`):
  `whisper-base` (engine `"whisper"`, single GGML asset) and `nemotron-3.5` (engine
  `"nemotron-3.5"`, encoder/decoder/joint ONNX + audio processor config + tokenizer + silero VAD,
  from `onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4`).

## Whisper engine
- NuGet: `Whisper.net` + `Whisper.net.Runtime` (added to `SharpAudio.Api.csproj`; native whisper.cpp
  binaries ship inside `Whisper.net.Runtime`).
- `Services/WhisperAsrEngine.cs`: wraps one GGML model file via `WhisperFactory.FromPath(...)`;
  `TranscribeAsync(byte[] wavBytes, string? language, ct)` resamples to 16kHz mono via
  `WavAudioUtils.ResampleToMono16kWav` (Whisper.net requires exactly 16kHz, no internal
  resampling), builds a processor (`.CreateBuilder().WithLanguage(language ?? "auto").Build()`),
  feeds the WAV to `processor.ProcessAsync(...)` (an `IAsyncEnumerable<SegmentData>`), concatenates
  `segment.Text` across all segments, and picks up `segment.Language` as the detected language when
  auto-detecting. Runs are serialised per engine with a `SemaphoreSlim`, because a pooled engine is
  shared by concurrent requests and the VAD gate no longer covers inference.
- `Services/WhisperAsrTranscriber.cs`: implements `IAsrTranscriber` + `IIdleTracking` (its two pools —
  Whisper engines and VAD engines — are merged by `IdleTrackingAggregate`),
  pools one `WhisperAsrEngine` per `"{model.Name}:{modelDirectory}"` key (mirrors
  `KokoroTtsSynthesizer`'s pooling pattern exactly). Rejects any model whose `Engine` isn't
  `"whisper"`.
- `Services/WavAudioUtils.cs`: shared helper — reads a RIFF/WAV header (no external audio library)
  for duration metrics, `ReadMonoFloat`/resampling, `ResampleToMono16kWav`. It is also the repo's
  single RIFF writer (`WrapPcmAsWav`, static chunk markers): TTS paths and the TTS worker wrap PCM
  with it instead of hand-rolling a header, and `GetPcmDurationSeconds` covers raw PCM.
- Streaming: `WhisperStreamingSession` implements `IStreamingTranscriptionSession` via a
  buffer-and-periodically-re-transcribe approach (no true incremental decode API in Whisper.net).

## Nemotron engine
Cache-aware streaming FastConformer-RNNT via raw `Microsoft.ML.OnnxRuntime` (3 chained sessions:
encoder/decoder/joint), mirrors `SupertonicTtsEngine`'s multi-session-in-one-class pattern. Exact
tensor contract was recovered via a throwaway C# spike (`InferenceSession.InputMetadata`/
`OutputMetadata`) — full details in
[docs/ASR_IMPLEMENTATION_HISTORY.md#phase-3](../ASR_IMPLEMENTATION_HISTORY.md#phase-3--nemotron-engine-spike--implementation).

- **Encoder** (24 layers, hidden=1024): consumes fixed-size log-mel chunks (128 mels;
  `chunk_samples` raw audio per chunk from `genai_config.json`, prefixed with 9 cached lookback
  frames), cache-aware via `cache_last_channel`/`cache_last_time`/`cache_last_channel_len` tensors
  threaded chunk-to-chunk, conditioned by a `lang_id` integer resolved via
  `Services/NemotronLanguages.cs`'s fixed lookup table (default `101` = auto-detect). Emits ~7
  encoded frames/chunk.
- **Decoder/predictor**: LSTM-based (2 layers, hidden=640), not attention-based. Its raw output is
  (batch, hidden, seq) and must be transposed before the joint step.
- **Joint**: combines one encoder frame + one decoder step, argmax over 13088 vocab entries; RNNT
  greedy decode loop (`RunRnntGreedyDecode`, up to `max_symbols_per_step` emissions per encoder
  frame, stops on `blank_id`). Both steps run on `TensorWorkspace`, and predictor LSTM state is
  committed only when a token is emitted — a blank leaves the state unchanged.
- `Services/NemotronVocabulary.cs`: `vocab.txt` (id-per-line) is the token→text decoder
  (SentencePiece `▁` convention); `Decode(tokenIds, out detectedLanguageTag)` also extracts the
  leading `<xx-XX>` auto-detect tag the model emits.
- `Services/NemotronFeatureExtractor.cs`: self-contained **streaming** log-mel spectrogram (own
  radix-2 FFT + Slaney-scale mel filterbank + Hann window centered within the FFT frame), stateful
  across chunks (carries `nFft/2` samples of left-context audio + the previous chunk's trailing
  log-mel frames).
- **All hyperparameters are read from `genai_config.json`** (not `audio_processor_config.json`,
  which is missing several needed fields).
- `Services/NemotronAsrEngine.cs` also owns VAD gating (see below) and the streaming session
  (`NemotronAsrEngine.StreamingSession`, true cache-aware incremental decode, carries encoder cache
  + LSTM predictor state across `ProcessChunkAsync` calls).
- `Services/ByteRingBuffer.cs`: the streaming session's pending-audio buffer. Each incoming chunk is
  appended once and consumed with a copy into the session's scratch span plus an O(1) advance, so there
  is no per-chunk array allocation and no shifting of the bytes left over — the previous
  `List<byte>` `GetRange(...).ToArray()` + `RemoveRange(...)` pattern did both on every chunk. The
  buffer grows by doubling when an arrival exceeds its capacity. Word-start token indices are pushed as
  tokens are decoded, so the commit boundary is read from the end of that stack instead of rescanning
  every pending token on each update.
- `TensorWorkspace` (nested in `NemotronAsrEngine.cs`): the per-owner set of persistent ONNX buffers,
  tensors and input lists for one decode pipeline. Before this, every chunk rebuilt six input tensors
  and every decode step rebuilt three more, and each encoder run copied its cache outputs into fresh
  arrays — hundreds of allocations per second of audio. The workspace allocates once and reuses the
  arrays its tensors wrap (`DenseTensor<T>` has no span constructor, so each wrapped array must be
  exactly the shape size; `audio_signal` is rebuilt only when the frame count changes), resolves named
  outputs to result positions once from `OutputMetadata`, and copies outputs into the arrays already
  being read. One workspace per owner — a streaming session or a single batch call — never shared
  across threads, matching the rule that a session owns its own VAD engine.

### Bugfix history (Nemotron)
1. **Feature-extraction/lang_id bug** (root-caused via real-audio cosine-similarity diagnostics
   comparing encoder output for real speech vs. silence — proved the encoder was ignoring audio
   content): `lang_id` was resolved from `vocab.txt`'s `<en-US>` tag *line index* (2947) instead of
   the small fixed integer (0) the model's language embedding actually expects. Also fixed:
   config source (`genai_config.json` not `audio_processor_config.json`, wrong `log_eps`), mel
   scale (Slaney not HTK), Hann window centering, and true per-chunk streaming framing (not a
   whole-utterance-at-once STFT). **Result**: WER 100% → ~1-2% on paragraphs, near-perfect on a
   20-turn conversation. Full narrative:
   [ASR_IMPLEMENTATION_HISTORY.md#phase-3](../ASR_IMPLEMENTATION_HISTORY.md#phase-3--nemotron-engine-spike--implementation).
2. **Wrong auto-detect default**: `NemotronLanguages.Resolve` defaulted unset/unrecognized language
   to lang_id `0` (English) instead of `101` (the model's real auto-detect slot per HuggingFace's
   own `transformers` docs, `default_prompt_id=101`). Since the frontend sends no `language` by
   default, non-English audio got wrong conditioning → the decoder emitted mostly blank → empty
   transcript. Fixed: default is now `101`; the model's own emitted `<xx-XX>` tag is surfaced as
   `DetectedLanguage` instead of being discarded. Full narrative:
   [ASR_IMPLEMENTATION_HISTORY.md#phase-10](../ASR_IMPLEMENTATION_HISTORY.md#phase-10--nemotron-auto-detect-language-fix).

## Universal audio input format support
`/v1/audio/transcriptions` doesn't assume WAV input: `Program.cs` calls
`Services/AudioFormatConverter.cs`'s `ToPcm16WavAsync(byte[], CancellationToken)` unconditionally
right after reading the uploaded file, before either engine sees the bytes. Shells out to `ffmpeg`
(`-i pipe:0 -vn -ac 1 -acodec pcm_s16le -f wav pipe:1`), so any ffmpeg-decodable format (FLAC, MP3,
OGG, WEBM, M4A, WAV, ...) works. `ffmpeg` is installed in the `Dockerfile`'s `runtime-asr`/
`runtime-all` stages (not `runtime-tts`) and in `Dockerfile.tests`. If ffmpeg is missing or the
input is undecodable, the endpoint returns `400 invalid_request` instead of a 500.

Uploads that are already PCM16 mono 16 kHz with header sizes matching the buffer
(`WavAudioUtils.IsPcm16Mono16kWav`) are returned unchanged, so no process is spawned — process
startup costs more than the decode itself for a short clip, and the frontend records in exactly that
format. A file with placeholder chunk sizes is deliberately not accepted by the fast path. ffmpeg's
output is read into an `ArrayPool` buffer that grows by doubling, which replaces the `MemoryStream`
buffer plus its `ToArray()` copy with one final array.

**Real bug found during verification**: ffmpeg can't seek on a non-seekable stdout pipe, so it
writes a placeholder `0xFFFFFFFF` for the RIFF/`data` chunk sizes instead of the real ones —
`WavAudioUtils`'s strict parser rejected every converted file as a result. This wasn't caught by
plain unit tests (skip when ffmpeg isn't on PATH) — only caught by building `Dockerfile.tests` into
a real image and running the tests inside it. Fixed: `AudioFormatConverter` patches the RIFF/data
chunk size fields itself once the true output length is known. Full narrative:
[ASR_IMPLEMENTATION_HISTORY.md#phase-11](../ASR_IMPLEMENTATION_HISTORY.md#phase-11--universal-audio-input-format-support-ffmpeg).

## VAD support
`AsrModelDefinition`/`AsrModelDefinitionResponse` have `SupportsVad` (`nemotron-3.5` and
`whisper-base`: true; `whisper-base` now lists a `silero_vad.onnx` asset alongside its GGML file). `Services/SileroVadEngine.cs` (behind an `ISileroVadEngine` interface, for
unit-testability without real weights) wraps the bundled `silero_vad.onnx` asset — `(2,1,128)` LSTM
state carried across windowed calls. `Services/SileroVadGate.cs` implements a consecutive-silence
chunk-gating policy (thresholds from `genai_config.json`'s `vad` section). Wired via
`Contracts/AudioTranscriptionRequest.cs`'s `EnableVad` (form field `use_vad`) →
`NemotronAsrEngine.Transcribe(..., enableVad)`: when enabled, `RunEncoderChunks` calls
`SileroVadGate.ShouldDropChunk` per raw-audio chunk and `continue`s past mel/encoder/decoder work
entirely for gated (silent) chunks — a skipped chunk doesn't advance the streaming feature
extractor's left-context/mel-cache state either.

For engines with no native VAD, `Services/VadSpanTranscriber.cs` (pure policy, no ONNX) runs the
Silero engine over the whole buffer, segments with `VadSegmenter`, transcribes each span through
an injected span callback (so the policy is testable without weights), and shifts span-local
timestamps back onto the whole-audio timeline: segment ids are renumbered sequentially across
spans, texts are joined with single spaces, and the first detected language wins. Padding that
runs past the end of the audio is clamped to the samples available. The span result is
`Models/SpanTranscription.cs` (`Text`, `DetectedLanguage`, `Segments`), which is what lets a span-level
transcription be merged back into a whole-audio `TranscriptionResult`. `Services/VadPipeline.cs` is the
single wiring for that path, used by both `WhisperAsrTranscriber` (in-process) and
`WorkerTranscriptionService` (the ASR worker): base options from DI, per-request onset/offset
overrides applied in one place, `WavAudioUtils.ReadMonoFloat` at 16 kHz, `WavAudioUtils.EncodeMono16kWav`
per span, one pooled `ISileroVadEngine` per VAD file path, and a `SemaphoreSlim` gate that covers
segmentation only — the stateful step, since the LSTM state is carried across calls — so spans are
transcribed outside it and one request's inference overlaps with the next request's VAD run. The
pipeline owns its own pool, so a VAD engine is never evicted by the single-active ASR engine, and
`WorkerServiceBase`'s health check now reports the ASR model rather than the VAD.
Frontend: `AsrModel.supportsVad` gates a `.vad-toggle` checkbox in the ASR panel.

`ISileroVadEngine` also exposes per-frame probabilities for segmenting and diarization:
`GetProbabilities(float[])` returns one probability per 32 ms frame (512 samples at 16 kHz, 256 at
8 kHz), `FlushProbabilities()` zero-pads the trailing partial frame, `FrameSamples`/
`FrameDurationMs` let callers time segments from frame counts, and `Reset()` clears both LSTM state
and the carried remainder. Windowing lives in `Services/VadWindowStream.cs` (pure logic, no ONNX),
which carries a partial window across calls so streaming callers get the same windows as a
whole-buffer call; `SileroVadEngine` delegates to it. `TryNextWindow(samples, out window, out
consumed)` is the allocation-free form — it returns a span aliasing the stream's own buffer, so the
caller must consume it before asking for the next window, and advance past the `consumed` samples it
took: re-passing the same span would replay the same audio. `CompleteWindows` materialises each
window for callers that need to collect them. Unit tests cover the stream and the
whole-buffer/streaming equivalence via a fake engine — engine-level probability tests would need the
model file and are gated. Mixing `ContainsSpeech` (per-call zero-padding) with `GetProbabilities` on
one engine instance corrupts the LSTM state, so a caller uses one path per utterance.

`SileroVadEngine` itself is single-owner: its input tensors, LSTM state and context buffers are
allocated once in the constructor and reused for every window, and the `output`/`stateN` result
positions are resolved from `OutputMetadata` once instead of being searched by name per call.
`Reset()` clears the buffers rather than reallocating them.

Per-frame probabilities are turned into spans by `Services/VadSegmenter.cs` (pure policy, no ONNX):
`Segment(probabilities, frameMs, VadOptions)` binarizes with hysteresis (onset opens a span, offset
closes it — the frame that drops below offset is excluded), pads both ends (`PadOnsetMs`/
`PadOffsetMs`, clamped to the audio), merges gaps shorter than `MinDurationOffMs`, and drops spans
shorter than `MinDurationOnMs`, returning `Models/SpeechSpan.cs` timestamps in seconds. Thresholds
are per-call options, so diarization (Phase 3) reuses the same segmenter with its own values.
`Options/VadOptions.cs` is a new appsettings section (`VadOptions`, registered in `Program.cs`),
and `WithOverrides(onset, offset)` applies the per-request `vad_onset`/`vad_offset` form fields
(`null` keeps the configured defaults). The worker path resolves the same values through
`AsrWorkerProxyTranscriber`, which also maps proto segments back into `TranscriptionResult`.

Trailing silence is turned into an end-of-utterance signal by `Services/VadEndpointer.cs` (pure
policy, no ONNX, mirroring NeMo.cpp's `VadEndpointer`): `Poll(nowMs, lastSpeechMs)` fires exactly
once when trailing silence first reaches `StopHistoryEouMs` (default 800 ms) and latches until
speech advances again, so one long pause produces one endpoint; `Force()` fires immediately
regardless of the threshold, `Reset()` disarms, and `SetStopHistoryEouMs(ms)` is the per-request
override (`<= 0` clears it). `EnableEndpointing` (default false) keeps endpointing opt-in so existing
segment-boundary behaviour is unchanged.

Streaming wiring (`NemotronAsrEngine.StreamingSession`): `SileroVadGate` exposes `LastChunkHadSpeech`, the
decision of the most recent `ShouldDropChunk` call, so endpointing reuses the gate's per-chunk speech
judgement instead of running the model twice. After each chunk the session advances `_audioElapsedMs`
(chunk duration), records `lastSpeechMs` when the chunk had speech, and polls the endpointer; when it
fires and the segment has pending text, that text is committed as a segment-final update with
`IsEndpointFinal` set. Endpoint commits take the whole pending range (the utterance ended, so the last
word is complete) rather than the mid-word-safe boundary used by time/punctuation commits. Endpointing
only works with VAD on, since it needs the gate's decision. `IStreamingTranscriptionSession.SampleRate`
and `ProcessChunkAsync` are unchanged; `StreamingUpdate` gained the optional `IsEndpointFinal` field.

## Worker process, DI wiring and SERVER_MODE
- **`SERVER_MODE`** env var (`tts` (default) | `asr` | `both`) parsed at the top of `Program.cs`
  into `ttsEnabled`/`asrEnabled` booleans. The pre-existing TTS registration block is wrapped in
  `if (ttsEnabled)` **with no internal changes** (byte-identical when `SERVER_MODE` unset). A
  parallel `if (asrEnabled)` block mirrors it for ASR.
- **Two independent worker types** run simultaneously: TTS's `WorkerProcessManager` stays a plain
  (non-keyed) singleton; ASR's is a **keyed singleton** (`AddKeyedSingleton<WorkerProcessManager>
  ("asr", ...)`) with its own `WorkerOptions` (mapped from `AsrWorkerOptions`,
  `Options/AsrWorkerOptions.cs`) — distinct port range (`50151+` vs TTS's `50051+`) and executable
  path (`./worker-asr/SharpAudio.Worker.Asr`).
- **`SharpAudio.Worker.Asr`** (mirrors `SharpAudio.Worker.Tts`): same CLI args, Kestrel-HTTP/2 startup
  pattern. `Services/WorkerTranscriptionService.cs` mirrors `WorkerSynthesisService`'s
  single-active-engine-with-lock pattern, routing by `request.Engine`.
- **`Protos/transcription.proto`** (own `csharp_namespace = "SharpAudio.Worker.Asr.Grpc"`):
  `WorkerTranscription` service with `Transcribe`/`HealthCheck` unary RPCs, plus `TranscribeStream`
  - a bidirectional streaming RPC (`stream TranscribeStreamChunk` in, `stream TranscribeStreamUpdate`
  out) used for live transcription in worker mode. The first request message must set `config`
  (`StreamConfig`: model_name/engine/model_path/language/use_vad/segment_seconds); every subsequent
  message sets `audio_chunk` (raw PCM16 mono bytes). `WorkerTranscriptionService.TranscribeStream`
  creates the right engine's `IStreamingTranscriptionSession` (same routing as the unary RPC) and
  streams back `{ text, is_final, is_segment_final }` updates - `text` is always scoped to the
  CURRENT segment only (never the whole conversation), `is_segment_final` marks a mid-stream
  segment commit, `is_final` marks the true end of the call.
- **`Services/AsrWorkerProxyTranscriber.cs`** (mirrors `WorkerProxySynthesizer`): implements
  `IAsrTranscriber`, resolves its `WorkerProcessManager` via `[FromKeyedServices("asr")]`.
  `CreateStreamingSessionAsync` opens a duplex `TranscribeStream` call on the pooled worker channel
  and wraps it in a private `WorkerStreamingSession` adapter implementing
  `IStreamingTranscriptionSession` (`ProcessChunkAsync` writes a chunk then drains buffered updates
  from a background read-loop task, collapsing consecutive in-progress updates to the latest one
  but forwarding every segment-commit exactly once; `FinishAsync` completes the request stream and
  returns the final `is_final` update's text) - a drop-in for the same interface the in-process
  engines implement, so the WS handler in `Program.cs` doesn't need to know which mode is active.
- **`Services/AsrTranscriberRouter.cs`** (in-process mode, mirrors `TtsSynthesizerRouter`): routes
  by `model.Engine == "nemotron-3.5"` else defaults to Whisper.
- Warmup is the shared generic **`Services/ModelWarmupService.cs`** (`ModelWarmupService<T>`, see
  [backend-architecture.md](backend-architecture.md)): `Program.cs` registers one instance per task type
  over the ASR catalog, so there is no ASR-specific warmup class. Idle monitoring is likewise the shared
  **`Services/IdleMonitorService.cs`** (no ASR-specific subclass): `Program.cs` registers one instance per
  task type over an `IdleTrackingAggregate` of the ASR router plus the PnC pool, and it reuses the same
  `ModelIdleMonitorOptions`/`MODEL_IDLE_TIMEOUT_SECONDS` config as TTS.

## REST + streaming endpoints
- `GET /api/server-info` (always mapped) → `{ ttsEnabled, asrEnabled }`.
- `GET /api/asr-models` (only if `asrEnabled`) → list of `AsrModelDefinitionResponse`.
- `POST /v1/audio/transcriptions` (only if `asrEnabled`, OpenAI-compatible): multipart/form-data
  (`file`, `model`, `language?`, `response_format?`, `min_segment_duration?`, `use_vad?`), returns
  `{ text }` JSON by default, or `{ text, language, duration, segments: [{id,start,end,text}] }`
  when `response_format=verbose_json` (see "VAD/timestamp segments" below), with metrics in
  response headers (`X-Processing-Time`, `X-Audio-Duration`, `X-RTF`, `X-Character-Count`).
- `GET /v1/audio/transcriptions/stream` (only if `asrEnabled`, WebSocket upgrade): works in both
  in-process and worker mode - query params `model` (required)/`language`/`use_vad`/
  `enable_endpointing` (nemotron-3.5 with `use_vad` only; defaults to `VadOptions.EnableEndpointing`, a
  request value overrides it, and it is ignored without VAD) and
  `segment_seconds` (optional, default 10, clamped to `[AsrStreamingOptions.MinSegmentSeconds,
  MaxSegmentSeconds]` = `[5,30]`); binary PCM16 16kHz mono frames in, JSON frames out. Every
  outbound message is scoped to the CURRENT segment only, never the whole conversation, so message
  size stays bounded regardless of utterance length: `{"type":"partial","text":...}` as the
  in-progress segment's text updates, `{"type":"segment","text":...}` once a segment is committed
  (time limit, sentence-ending punctuation - see `SegmentBoundaryPolicy` - or trailing silence reaching
  `VadOptions.StopHistoryEouMs`, which additionally sets `"endpoint_final":true` on that message), and
  `{"type":"final","text":...}` (the trailing, not-yet-committed segment) once the session ends. A
  `{"type":"end"}` text frame (or socket close) triggers the final message. Resolved via
  `IAsrTranscriber.CreateStreamingSessionAsync(...)`, which hides the in-process-vs-worker-mode
  distinction behind a duplex gRPC call in worker mode (see `AsrWorkerProxyTranscriber` below).
  Worker mode carries the flag on `StreamConfig.enable_endpointing` plus `stop_history_eou_ms` (the API's
  configured threshold, forwarded so both modes behave identically), and `TranscribeStreamUpdate` carries
  `is_endpoint_final`.
  Nemotron's cache-aware encoder/decoder state is never reset at a segment boundary (only the
  emitted/decoded text window is bounded); Whisper's retained PCM buffer IS trimmed down to a
  ~0.5s overlap at each commit, since its per-pass cost is a real function of buffer size.
  Two commit-quality safeguards in `NemotronAsrEngine.StreamingSession`: (1) the segment's time
  budget only starts counting once real speech (the first decoded token) appears, so leading
  silence before the user starts talking can't eat into it and force a premature commit right
  after the first word or two; (2) a commit boundary is never allowed to land mid-word - a raw
  SentencePiece continuation token (no leading `▁` marker; see `NemotronVocabulary.IsWordStart`)
  at the end of the pending range is held back for the next segment instead of being split off.
- `GET /v1/models` merges `IModelCatalog`/`IAsrModelCatalog` results.
- `/api/models` and `/v1/audio/speech` are wrapped in `if (ttsEnabled)` with zero internal changes.

## Live transcription (frontend)
`frontend/src/components/LiveTranscription.vue`: mic capture via `getUserMedia`, tab/system audio
via `getDisplayMedia`, resampled to 16kHz PCM16 in an `AudioWorkletNode`
(`frontend/src/audio-worklets/pcm-capture-processor.js`), streamed over the WebSocket endpoint
above (including a `segment_seconds` query param sourced from a numeric input next to the VAD
toggle in `App.vue`, default 10, and an endpointing checkbox shown only when the model supports both
VAD and streaming). Maintains two pieces of state: `committedLines` (one entry per
`segment`/`final` message, each timestamped client-side at receipt time and rendered on its own
line, high-contrast/`#e5e5e5`) and `liveText` (the in-progress `partial` segment, rendered in gray/
`#888888` right after the committed lines) - gives a visual distinction between already-committed
and still-being-transcribed text, with no backend timestamp support needed). Endpoint-driven commits
are marked with a `⌁` glyph after the line. Wired into `App.vue`'s
ASR panel behind `v-if="selectedAsrModel?.supportsStreaming"`.

## VAD and timestamp segments
`Models/TranscriptionSegment.cs` (`Id`, `Start`, `End`, `Text`) + `TranscriptionResult.Segments`.
Requested via `response_format=verbose_json` (+ optional `min_segment_duration`, default `0.5s`) -
mirrors OpenAI's `verbose_json`/`timestamp_granularities` naming, decoupled from the `use_vad`
checkbox (matches OpenAI's independent fields), though VAD is what makes Nemotron's segments
meaningful:
- **Whisper**: segments come directly from Whisper.net's own per-segment `Start`/`End`/`Text` -
  always computed, no extra cost.
- **Nemotron**: `RunEncoderChunks` tracks contiguous "active audio" regions (maximal runs of
  non-VAD-dropped chunks when `use_vad=true`, or one region spanning the whole clip when it's
  `false`); `RunRnntGreedyDecode` additionally returns a per-encoder-frame token count so each
  region's frame range can be sliced out of the flat token stream and decoded into its own segment
  text.
- `Services/SegmentMerger.cs`: segments shorter than `min_segment_duration` are merged into a
  neighboring segment (extends its time range + appends text) rather than dropped, so no
  transcribed text is ever silently lost.
- **Known limitation**: segments are only populated when running ASR in-process
  (`AsrWorkerOptions__Enabled=false`) - the worker gRPC protocol (`transcription.proto`) doesn't
  carry segment data yet, so worker-mode responses return an empty `segments` array even with
  `response_format=verbose_json`.
- Frontend: a standalone "Show timestamps" checkbox (independent of the VAD checkbox) + a
  "min segment duration" number input in the offline ASR panel; `TranscriptionResult.vue` renders
  a timestamped row list instead of the flat text block when segments are present.

## Known issues and roadmap
- **Segment timestamps only work in-process** (`AsrWorkerOptions__Enabled=false`) -
  `transcription.proto`'s `TranscribeResponse` doesn't carry segment data yet, so worker-mode
  `verbose_json` responses return an empty `segments` array. Extending the proto to carry segments
  isn't scheduled; it would follow the same pattern as `TranscribeStream`'s `StreamConfig`/
  `TranscribeStreamUpdate` messages.
- Live transcription's worker-mode path (`AsrWorkerProxyTranscriber.CreateStreamingSessionAsync`) is
  covered by an opt-in integration test
  (`AsrCircularTests.Streaming_WorkerMode_TranscribesOverWebSocket`) gated on both real model
  weights (`ASR_MODEL_TESTS=1`) and a published `SharpAudio.Worker.Asr` executable being present next
  to the API output - it isn't exercised by a plain `./dotnet.sh test` run in this dev environment
  and should be validated against a real Docker image before being fully trusted in production.

## ASR env var table
| Env var | Purpose | Default |
|---|---|---|
| `SERVER_MODE` | `tts`\|`asr`\|`both` — which task type(s) this instance serves | `tts` |
| `AsrWorkerOptions__Enabled` | ASR worker-mode vs in-process (**must be `false` for live streaming
  to work today**) | true |
| `AsrWorkerOptions__ExecutablePath` | ASR worker binary path | `./worker-asr/SharpAudio.Worker.Asr` |
| `AsrWorkerOptions__PortRangeStart` | first ASR gRPC port to try | 50151 |
| `AsrWorkerOptions__IdleTimeoutSeconds` | ASR worker self-termination timeout | 60 |

## Adding a new ASR engine
1. Add a `{Engine}AsrEngine.cs` (owns model session(s), pooled per model+directory — mirror
   `WhisperAsrEngine` for a single-session wrapper or `NemotronAsrEngine` for a multi-session
   chain) and a `{Engine}AsrTranscriber.cs` implementing `IAsrTranscriber`/
   `IIdleTracking` (+ `IStreamingTranscriptionSession` if streaming should be
   supported).
2. Add routing for the new `Engine` string in `AsrTranscriberRouter` and
   `Worker.Asr/Services/WorkerTranscriptionService.cs`.
3. Add model entries to `config.json`'s `"asrModels"` array (and `AsrModelCatalog`'s hardcoded
   defaults).
4. Register the transcriber singleton in `Program.cs` (in-process branch).
