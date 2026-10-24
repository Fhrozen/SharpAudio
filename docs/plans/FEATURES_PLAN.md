# Features Plan: VAD + PnC, Diarization, Stable Audio Music

> Git-tracked, step-by-step plan so any agent can resume. Progress log, gotchas and discovered
> facts live in [FEATURES_MEMORY.md](FEATURES_MEMORY.md). Read [../LLM_WIKI.md](../LLM_WIKI.md)
> first for architecture. Agent: [.github/agents/sharp-audio-features-builder.agent.md](../../.github/agents/sharp-audio-features-builder.agent.md).

## How to use

1. Read `FEATURES_MEMORY.md` (current step, decisions, gotchas), then this file.
2. Find the first step whose Status is not `Accepted`.
3. `Done (awaiting acceptance)` -> stop and ask the user to accept. `Not started` -> do that step only.
4. Build/test: `./dotnet.sh build SharpAudio.slnx`, `./dotnet.sh test tests/SharpAudio.Api.Tests`.
5. Update the matching `docs/wiki/*.md` (+ `LLM_WIKI.md` index if new topic), this status table,
   and `FEATURES_MEMORY.md`. Report, then **stop and ask permission to continue**.

## Decisions locked in (do not re-litigate without asking)

- Runtime: one-off Python ONNX export (kept in `tools/`) + `Microsoft.ML.OnnxRuntime`; no native ggml/P-Invoke. The user hosts exported ONNX files and supplies the URLs.
- VAD: keep existing Silero (`SileroVadEngine`/`SileroVadGate`); port NeMo.cpp binarizer (onset/offset hysteresis), pad, gap-merge, min-duration, and end-of-utterance endpointer as pure C#. No mel feature masking.
- PnC: `1-800-BAD-CODE/punctuation_fullstop_truecase_english` (HF, Apache-2.0, already ONNX, 6L/512, SentencePiece unigram 32k); four heads — punctuation per subword, sentence boundary detection, true-case per character, punctuation-conditioned embeddings; `max_length` 256 with overlapping windows (overlap 16 default, 32 in the model config); labels read from config, never hardcoded; input must be pre-lowercased and unpunctuated. Opt-in via request flag.
- Diarization: Sortformer v2 4-speaker offline (`nvidia/diar_streaming_sortformer_4spk-v2`, CC-BY-4.0), single forward pass, no clustering/streaming; speaker per segment = mean-argmax of frame probs. Reuse `VadSegmenter` for per-speaker segmentation.
- Stable Audio: `lsb/stable-audio-3-small-music-onnx` (int4, 4 ONNX graphs), new `POST /v1/audio/music`, new `SERVER_MODE` value(s), own gRPC service in the existing `SharpAudio.Worker` executable with a separate "music" worker pool key. Use `number_conditioner.onnx` (not the `.npz`). Pingpong sampler, 8 steps, no CFG, 44.1 kHz stereo.
- Gate: after every step stop and ask permission.
- Comments: one short line, only what code cannot show. No extra markdown beyond wiki/plan/memory files.

## Status tracker

| Step | Phase | Name | Status |
|---|---|---|---|
| 0 | Scaffold | Plan + memory + agent file | Accepted |
| 1 | VAD | `GetProbabilities` on `ISileroVadEngine` | Done (awaiting acceptance) |
| 2 | VAD | `VadOptions` + `VadSegmenter` | Done (awaiting acceptance) |
| 3 | VAD | `VadEndpointer` | Done (awaiting acceptance) |
| 4 | VAD | Offline wiring (engine-independent VAD spans) | Done (awaiting acceptance) |
| 5 | VAD | Streaming endpointer wiring + frontend + docs | Done (awaiting acceptance) |
| 6 | PnC | Load published ONNX, record IO names + four heads | Accepted |
| 7 | PnC | `ITokenizer` + unigram SentencePiece tokenizer | Accepted |
| 8 | PnC | `PncEngine` (four heads, overlap windows, label apply) | Accepted |
| 9 | PnC | Worker/proto/endpoint wiring (`punctuate`) | Accepted |
| 10 | PnC | Frontend + docs + integration test | Not started |
| 11 | Diar | Sortformer ONNX export spike + parity | Not started |
| 12 | Diar | 128-mel feature extractor reuse | Not started |
| 13 | Diar | `SortformerEngine` | Not started |
| 14 | Diar | `DiarSegmenter` + `AssignSpeakers` | Not started |
| 15 | Diar | Worker/proto/endpoint wiring (`diarize`) | Not started |
| 16 | Diar | Frontend + docs + integration test | Not started |
| 17 | Music | ORT load spike + IO names | Done (awaiting acceptance) |
| 18 | Music | Gemma tokenizer | Not started |
| 19 | Music | Schedule + pingpong sampler + RNG | Not started |
| 20 | Music | `StableAudioEngine` | Not started |
| 21 | Music | Catalog/config/model cache | Not started |
| 22 | Music | `IMusicGenerator` + pooling + router + options | Not started |
| 23 | Music | `music.proto` + worker service + manager key | Not started |
| 24 | Music | `SERVER_MODE` + `POST /v1/audio/music` + encoding | Not started |
| 25 | Music | Docker targets/env + license notices | Not started |
| 26 | Music | Frontend panel + attribution | Not started |
| 27 | Music | Final tests + wiki + wrap-up | Not started |
| 28 | Multilingual PnC | Other tokenizers behind `ITokenizer` (BPE, WordPiece) | Not started |
| 29 | Multilingual PnC | Per-language label sets + ACRONYM rule | Not started |
| 30 | Multilingual PnC | Per-model windowing params + punctuation-only branch | Not started |
| 31 | Multilingual PnC | oliverguhr ONNX export spike + parity | Not started |
| 32 | Multilingual PnC | Language -> model selection + frontend + test | Not started |

## Step specs

### Step 0 - Scaffold
Create this file, `FEATURES_MEMORY.md`, the builder agent file, a repo-memory pointer, and a link line in `LLM_WIKI.md`. No code.

### Phase 1 - VAD (NeMo.cpp `src/asr/vad/`)
- **1**: add `float[] GetProbabilities(float[] samples)` to `ISileroVadEngine` (one prob per 512-sample window at 16 kHz, 256 at 8 kHz; LSTM state carried). Fake-engine unit tests.
- **2**: `VadOptions` (onset 0.5, offset 0.3, padOnsetMs 200, padOffsetMs 200, minDurationOffMs 500, minDurationOnMs) and pure `VadSegmenter.Segment(probs, frameMs, options)` -> `SpeechSpan(start,end)` list: binarize w/ hysteresis -> pad -> merge gaps < minOff -> drop spans < minOn. Unit tests.
- **3**: pure `VadEndpointer`: fires once when `now - lastSpeech >= stopHistoryEouMs` (default 800), re-arms only when lastSpeech advances, `Force()` fires immediately, per-request override. Unit tests.
- **4**: for engines without native VAD (Whisper): run Silero + `VadSegmenter`, transcribe each span, shift timestamps back. Add Silero asset + `vad` options to `config.json`, request fields for onset/offset (optional). Hook in `AsrTranscriberRouter` and worker service (check `transcription.proto`).
- **5**: use `VadEndpointer` in Nemotron `StreamingSession` to commit segments alongside `SegmentBoundaryPolicy`; frontend toggle; wiki update.

### Phase 2 - PnC (`1-800-BAD-CODE/punctuation_fullstop_truecase_english`)
- **6**: load the published `punct_cap_seg_en.onnx` (210 MB, no export step) in ORT and record IO names/shapes/dtypes plus the four heads (punct per subword, SBD, per-character true-case, punct-conditioned embeddings). No `tools/pnc/export_pnc_onnx.py`.
- **7**: `ITokenizer` + `UnigramSentencePieceTokenizer` (protobuf `.model` reader, per-word DP max-score with vocab-order tie-break, byte pieces skipped, unk runs merged, normalization = space-collapse + trim only, no case folding) + golden tests vs Python. Parity confirmed on the real model file.
- **8**: `PncEngine` (ORT session pool pattern): `max_length` 256, overlapping subsegments (overlap 16 default / 32 in the model config), four heads, per-character casing, ACRONYM rule, SBD applied at the last character of each subword (step 6 corrected the earlier "shifted right by one for casing", which was NeMo.cpp's rule for its BERT model), labels from config, argmax already inside the ONNX graph; input pre-lowercased and unpunctuated; unit tests with fake logits.
- **9**: `punctuate` form field, `config.json` asset, proto fields; run inside ASR worker after transcription, updating `Text` and segments.
- **10**: frontend checkbox, `docs/wiki/asr-engines.md`, gated integration test.

### Phase 3 - Diarization (NeMo.cpp `src/asr/diar/`)
- **11**: export Sortformer v2 offline (features -> `(T,4)` probs @ 80 ms) to ONNX in `tools/diar/`; parity vs NeMo `diarize`; record IO names and preprocessor normalization. User hosts files.
- **12**: reuse/extend the 128-mel extractor from `NemotronAsrEngine` (16 kHz, 25 ms win, 10 ms hop); tests.
- **13**: `SortformerEngine` (ORT) -> frame probs; max duration cap (default 600 s) with clear error.
- **14**: `DiarSegmenter` reusing `VadSegmenter` per speaker (onset 0.641, offset 0.561, padOnset 0.229 s, padOffset 0.079 s, minGap 0.296 s, minOn 0.511 s) + pure `AssignSpeakers(segments, probs)` mean-argmax; tests.
- **15**: `diarize` form field; `Speaker` on `TranscriptionSegment`; proto + worker; force segments when `diarize=true`.
- **16**: frontend speaker labels, wiki, gated integration test.

### Phase 4 - Stable Audio music
- **17**: gated spike: load 4 ONNX files in ORT 1.20.1 (MatMulNBits, GatherBlockQuantized, multi-file external data); dump input/output names/shapes/dtypes to memory. Bump ORT only if needed (ask first).
- **18**: Gemma tokenizer from `tokenizer.json` (try `Microsoft.ML.Tokenizers`; fallback small custom BPE with byte fallback); max 256 tokens; golden tests.
- **19**: LogSNRShift schedule (anchor -6.2, end 2.0, rate 0), pingpong step, seeded Gaussian RNG; pure + tests. `T_lat = ceil((s+6)*44100/8192)*2`.
- **20**: `StableAudioEngine`: text encoder -> number conditioner -> DiT loop -> decoder; stereo float 44.1 kHz.
- **21**: `MusicModelDefinition`, `musicModels` in `config.json`, `MusicModelCatalog`, cache assets from HF repo.
- **22**: `IMusicGenerator`, pooling wrapper, router, `MusicWorkerOptions` (max duration default 30 s, hard cap 384 s).
- **23**: `music.proto`, worker gRPC service in `SharpAudio.Worker`, "music" `WorkerProcessManager` key.
- **24**: `SERVER_MODE` values, `POST /v1/audio/music` (prompt, model, duration, steps, seed, response_format), stereo WAV/mp3 encoding, `/v1/models` + `ServerInfo`.
- **25**: Dockerfile target/env; ship `LICENSE.md`, `LICENSE_GEMMA.md`, `NOTICE`; "Powered by Stability AI" attribution.
- **26**: frontend music panel with attribution.
- **27**: final tests, wiki, `LLM_WIKI.md` update.

### Phase 5 - Multilingual PnC (deferred, depends on Phase 2)

> **Annotation:** Phase C now uses `1-800-BAD-CODE/punctuation_fullstop_truecase_english`, so Phase 5 is about
> **additional languages**, not the first model. Candidates: `oliverguhr/fullstop-punctuation-multilang-large`
> (MIT, xlm-roberta-large, EN/DE/FR/IT, punctuation only - no capitalization head),
> `1-800-BAD-CODE/xlm-roberta_punctuation_fullstop_truecase`,
> `oliverguhr/fullstop-punctuation-multilingual-sonar-base`.
> The swap must be an asset/config change only, so tokenizer, label sets and windowing params are config-driven
> from the start (that design lands in Phase C).

- **28**: additional `ITokenizer` implementations (xlm-roberta BPE: vocab 250,002, vocab/merges from assets,
  `add_prefix_space` parity with Python `tokenizers`); golden tests. `ITokenizer` and the unigram
  implementation already exist from step 7.
- **29**: per-language `PncLabelSet` loaded per model from an asset; case head optional (null when the model has
  no case head). No hardcoded labels - the config-driven design comes from Phase C.
- **30**: generalize `PncEngine` windowing to per-model params (NeMo 128/32/62; oliverguhr overlap 32 with
  BOS/EOS, max_length 514); application branches to punctuation-only when there is no capit head.
- **31**: export spike oliverguhr -> ONNX (single head, argmax in C#); parity vs `deepmultilingualpunctuation`.
  Blocked until the user hosts the URL.
- **32**: language -> model selection in `AsrModelCatalog` / `config.json` (`en` -> NeMo English, otherwise the
  multilingual asset); frontend, wiki, gated integration test for EN/DE/FR/IT.

## Reference files

- VAD: `src/SharpAudio.Api/Services/SileroVadEngine.cs`, `SileroVadGate.cs`, `SegmentBoundaryPolicy.cs`, `SegmentMerger.cs`
- ASR engines/routing: `NemotronAsrEngine.cs`, `WhisperAsrEngine.cs`, `AsrTranscriberRouter.cs`, `src/SharpAudio.Worker.Asr/Services/WorkerTranscriptionService.cs`
- Contracts: `Models/TranscriptionResult.cs`, `Contracts/AudioTranscriptionRequest.cs`, `Protos/transcription.proto`
- Multi-session ONNX pattern: `SupertonicTtsEngine.cs`; routing: `TtsSynthesizerRouter.cs`; workers: `WorkerProcessManager.cs`
- NeMo.cpp refs: `out_refs/NeMo-Speech.cpp/src/asr/{vad,pnc,diar}/`, `conversion/{vad,pnc,diarization}.py`
- Stable Audio: `out_refs/stable-audio-3-small-music-onnx/` (README, `config.json`, `onnx/`, `tokenizer/`)
