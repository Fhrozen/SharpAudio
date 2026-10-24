# SharpAudio Wiki — Testing

> Linked from [docs/LLM_WIKI.md](../LLM_WIKI.md).

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
- `tests/SharpAudio.Api.IntegrationTests`: full-stack tests via Docker (`SpeechSynthesisTests`,
  `ModelHealthTests` — including a `SERVER_MODE`-aware `/api/server-info` check,
  `JapaneseConcurrencyTests`, `AudioFormatConverterTests` — FLAC/MP3 round-trip, gated on ffmpeg's
  presence).
- Run: `dotnet test tests/SharpAudio.Api.Tests`, or `./tests/run-tests.sh all` /
  `docker compose -f docker-compose.test.yml up --abort-on-container-exit` for integration.
- Frontend: `cd frontend && pnpm install && pnpm run build` (runs `vue-tsc --noEmit` then `vite
  build`); no pnpm preinstalled in the dev container - install via `npm install -g pnpm` first.

## Circular TTS→ASR model tests (opt-in, real models, not run in CI)
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

## PnC tokenizer golden test (opt-in, real model file, not run in CI)
`tests/SharpAudio.Api.IntegrationTests/PncTokenizerGoldenTests.cs` checks the C#
`UnigramSentencePieceTokenizer` against golden cases generated from the Python
reference (`tools/pnc/spike_tokenizer.py`). Gated by `Support/PncModelTestGate.cs`
(`PNC_TOKENIZER_MODEL` env var, skipped when unset) so the model file is not
committed. See [docs/wiki/pnc.md](pnc.md).

## Known bug fixed: ModelCache concurrent-download race
`ModelCache.EnsureModelAsync` (both Tts/Asr overloads) serializes per-model downloads via a
`ConcurrentDictionary<string, SemaphoreSlim>` keyed by model name. Previously, a background
warmup service (`ModelWarmupService`/`AsrModelWarmupService`) racing an on-demand request for the
same not-yet-cached model could write the same asset file concurrently, corrupting it / causing
intermittent 500s — this affected TTS models too, not just ASR, it just hadn't been exercised
before the circular ASR tests were added.
