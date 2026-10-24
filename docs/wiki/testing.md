---
wiki: testing
topic: Test layout, gates and how to run tests
covers:
  - tests/**
sections:
  unit-tests:
    - tests/SharpAudio.Api.Tests/AsrModelCatalogTests.cs
    - tests/SharpAudio.Api.Tests/AsrTranscriberRouterTests.cs
    - tests/SharpAudio.Api.Tests/ByteRingBufferTests.cs
    - tests/SharpAudio.Api.Tests/EnginePoolTests.cs
    - tests/SharpAudio.Api.Tests/IdleTrackingAggregateTests.cs
    - tests/SharpAudio.Api.Tests/KokoroMetadataTests.cs
    - tests/SharpAudio.Api.Tests/ModelCatalogTests.cs
    - tests/SharpAudio.Api.Tests/NemotronLanguagesTests.cs
    - tests/SharpAudio.Api.Tests/NemotronVocabularyTests.cs
    - tests/SharpAudio.Api.Tests/PncCollectorTests.cs
    - tests/SharpAudio.Api.Tests/PncConfigReaderTests.cs
    - tests/SharpAudio.Api.Tests/PncEngineTests.cs
    - tests/SharpAudio.Api.Tests/PncInputCleanerTests.cs
    - tests/SharpAudio.Api.Tests/PncModelCatalogTests.cs
    - tests/SharpAudio.Api.Tests/PncTextApplierTests.cs
    - tests/SharpAudio.Api.Tests/PncWindowPlannerTests.cs
    - tests/SharpAudio.Api.Tests/PunctuatingTranscriberTests.cs
    - tests/SharpAudio.Api.Tests/SegmentMergerTests.cs
    - tests/SharpAudio.Api.Tests/SherpaOnnxTtsSynthesizerTests.cs
    - tests/SharpAudio.Api.Tests/SileroVadGateTests.cs
    - tests/SharpAudio.Api.Tests/SileroVadProbabilitiesTests.cs
    - tests/SharpAudio.Api.Tests/SingleActiveEnginePoolTests.cs
    - tests/SharpAudio.Api.Tests/SpeechEndpointTests.cs
    - tests/SharpAudio.Api.Tests/TextSanitizerTests.cs
    - tests/SharpAudio.Api.Tests/UnigramSentencePieceTokenizerTests.cs
    - tests/SharpAudio.Api.Tests/VadEndpointerTests.cs
    - tests/SharpAudio.Api.Tests/VadSegmenterTests.cs
    - tests/SharpAudio.Api.Tests/VadSpanTranscriberTests.cs
    - tests/SharpAudio.Api.Tests/VadWindowStreamTests.cs
    - tests/SharpAudio.Api.Tests/WavAudioUtilsTests.cs
    - tests/SharpAudio.Api.Tests/WikiHeaderCoverageTests.cs
  benchmarks:
    - tests/SharpAudio.Api.Benchmarks/Program.cs
  integration-tests:
    - tests/SharpAudio.Api.IntegrationTests/AudioFormatConverterTests.cs
    - tests/SharpAudio.Api.IntegrationTests/JapaneseConcurrencyTests.cs
    - tests/SharpAudio.Api.IntegrationTests/ModelHealthTests.cs
    - tests/SharpAudio.Api.IntegrationTests/SpeechSynthesisTests.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/AsrModelTestGate.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/AsrTestCorpus.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/FfmpegTestEncoder.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/FfmpegTestGate.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/PncModelTestGate.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/WavTestUtils.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/WordErrorRate.cs
    - tests/SharpAudio.Api.IntegrationTests/Support/WorkerAsrExecutableGate.cs
  circular-tts-asr-model-tests:
    - tests/SharpAudio.Api.IntegrationTests/AsrCircularTests.cs
  pnc-tokenizer-golden-test:
    - tests/SharpAudio.Api.IntegrationTests/PncTokenizerGoldenTests.cs
  running-tests:
    - tests/run-tests.sh
updated: 2026-11-06
---

# SharpAudio Wiki — Testing

> Linked from [docs/LLM_WIKI.md](../LLM_WIKI.md).

## Unit tests

- `tests/SharpAudio.Api.Tests`: unit tests (`ModelCatalogTests`, `KokoroMetadataTests`,
  `SherpaOnnxTtsSynthesizerTests`, `SpeechEndpointTests`, `TextSanitizerTests`,
  `AsrModelCatalogTests`, `AsrTranscriberRouterTests`, `NemotronLanguagesTests`,
  `NemotronVocabularyTests`, `SileroVadGateTests`). The ASR router test instantiates the real
  `AsrTranscriberRouter`/`WhisperAsrTranscriber`/`NemotronAsrTranscriber` with a bogus model
  directory — no real weights needed, since each engine fails fast in a distinguishable way
  (Whisper's own `FileNotFoundException` vs. Nemotron's `InferenceSession` failing to open a
  missing `encoder.onnx`), which is enough to prove routing went to the right engine.
  `SileroVadGateTests` uses a fake `ISileroVadEngine` to unit-test the consecutive-silence gating
  policy without needing real Silero weights. `VadWindowStreamTests` covers the pure windowing logic
  (whole-buffer vs streaming carry-over, zero-padded flush, 32 ms frames at 8k/16k, `Reset`), and
  `SileroVadProbabilitiesTests` asserts whole-buffer and streaming agree through a fake engine —
  engine-level probability tests would need `silero_vad.onnx` and are gated. `VadSegmenterTests`
  covers the pure hysteresis/padding/gap-merge/min-duration policy with hand-written probability
  lists, so no weights are needed. `VadEndpointerTests` covers the pure trailing-silence policy
  (fires once per silence run, re-arms only when speech advances, `Force`, per-request override,
  `Reset`) with hand-written timelines. `SileroVadGateTests` additionally covers `LastChunkHadSpeech`, the
  per-chunk decision the streaming session feeds to the endpointer. `VadSpanTranscriberTests` covers span slicing, timestamp
  shifting, sequential segment ids across spans, clamping at the end of the audio and per-utterance
  `Reset` through a fake engine plus an injected span callback, so no weights are needed.
  `PncModelCatalogTests`, `PncTextApplierTests` and `PunctuatingTranscriberTests` cover the PnC
  wiring with a fake engine, catalog, model cache and engine pool, so no ONNX file is needed.
- `WikiHeaderCoverageTests` enforces the wiki-header convention described in
  [docs/LLM_WIKI.md](../LLM_WIKI.md): every mapped source file is owned by exactly one wiki file,
  every `sections:` entry points at a real heading, and each file's `// wiki:` header agrees with
  its `sections:` mapping. It parses the frontmatter line-by-line (no YAML dependency) and skips
  gracefully when `docs/wiki` isn't reachable from the test output directory.

## Integration tests

- `tests/SharpAudio.Api.IntegrationTests`: full-stack tests via Docker (`SpeechSynthesisTests`,
  `ModelHealthTests` — including a `SERVER_MODE`-aware `/api/server-info` check,
  `JapaneseConcurrencyTests`, `AudioFormatConverterTests` — FLAC/MP3 round-trip, gated on ffmpeg's
  presence).

## Benchmarks

`tests/SharpAudio.Api.Benchmarks` is a BenchmarkDotNet console project (not a test project, so it
is not part of `tests/run-tests.sh`). It measures the per-request hot paths that do **not** call a
model — WAV framing, VAD windowing, text sanitization, tokenizer encoding, the PnC window planner
and the pending-buffer shape — so regressions in allocation-heavy helpers are visible without
gpu/model access.

```bash
./dotnet.sh run --project tests/SharpAudio.Api.Benchmarks -c Release -- --filter '*'
```

Baseline numbers and the confirmed quadratic shape are recorded in the session notes; the point of
the harness is to re-measure after each refactor in the plan.

## Running tests

- Run: `dotnet test tests/SharpAudio.Api.Tests`, or `./tests/run-tests.sh all` /
  `docker compose -f docker-compose.test.yml up --abort-on-container-exit` for integration.
- Frontend: `cd frontend && pnpm install && pnpm run build` (runs `vue-tsc --noEmit` then `vite
  build`); no pnpm preinstalled in the dev container - install via `npm install -g pnpm` first.
- Wiki convention: `WikiHeaderCoverageTests` checks that every mapped source file is owned by exactly
  one `docs/wiki/*.md` `sections:` entry, that every anchor is a real heading, and that each file's
  one-line `wiki:` header matches. Staleness of the `updated` date needs git history, so it is checked
  locally by `bash tools/wiki/wiki-check.sh` (`./tests/run-tests.sh wiki`); headers are regenerated by
  `bash tools/wiki/apply-headers.sh`. See [docs/LLM_WIKI.md](../LLM_WIKI.md).

## Circular TTS→ASR model tests
`tests/SharpAudio.Api.IntegrationTests/AsrCircularTests.cs` synthesizes real audio via Supertonic-3
(`test_text.md` corpus — short/long samples + a long multi-speaker conversation script) and feeds
it into Whisper/Nemotron via the real HTTP endpoints, checking transcription quality (word error
rate) and crash-resistance on long multi-chunk audio. **Opt-in only**: gated by
`AsrModelTestGate`/`ASR_MODEL_TESTS=1` env var (via `Xunit.SkippableFact`, shows as Skipped by
default — zero cost, no downloads, when not enabled) and tagged `[Trait("Category",
"AsrModelTests")]`; CI's `ci-tests.yml` explicitly excludes this category. Run locally with
`ASR_MODEL_TESTS=1 dotnet test ... --filter "Category=AsrModelTests"` or `./tests/run-tests.sh
asr-model-tests`. See
[docs/ASR_IMPLEMENTATION_HISTORY.md#phase-8](../ASR_IMPLEMENTATION_HISTORY.md#phase-8--tests) for
full design notes, including the real concurrency bug this suite found and fixed in `ModelCache`
(see below). Also covers non-WAV uploads (`Offline_NonWavUpload_TranscribesSuccessfully`) and VAD
(`Offline_NemotronWithVad_TranscribesWithoutCrashing`).

## PnC tokenizer golden test

`tests/SharpAudio.Api.IntegrationTests/PncTokenizerGoldenTests.cs` checks the C#
`UnigramSentencePieceTokenizer` against golden cases generated from the Python
reference (`tools/pnc/spike_tokenizer.py`). Gated by `Support/PncModelTestGate.cs`
(`PNC_TOKENIZER_MODEL` env var, skipped when unset) so the model file is not
committed. See [docs/wiki/pnc.md](pnc.md).

## ModelCache concurrent-download race
`ModelCache.EnsureModelAsync` serializes per-model downloads via a
`ConcurrentDictionary<string, SemaphoreSlim>` keyed by model name. Previously, a background
warmup service (`ModelWarmupService<T>`) racing an on-demand request for the
same not-yet-cached model could write the same asset file concurrently, corrupting it / causing
intermittent 500s — this affected TTS models too, not just ASR, it just hadn't been exercised
before the circular ASR tests were added.
