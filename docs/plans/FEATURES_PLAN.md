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
- PnC: NeMo BERT (12L, 768), WordPiece, windows 128 tokens / margin 32 / stride 62; labels punct `O , . ?`, capit `O U`; punct from last subword, capit from head subword. Opt-in via request flag.
- Diarization: Sortformer v2 4-speaker offline (`nvidia/diar_streaming_sortformer_4spk-v2`, CC-BY-4.0), single forward pass, no clustering/streaming; speaker per segment = mean-argmax of frame probs. Reuse `VadSegmenter` for per-speaker segmentation.
- Stable Audio: `lsb/stable-audio-3-small-music-onnx` (int4, 4 ONNX graphs), new `POST /v1/audio/music`, new `SERVER_MODE` value(s), own gRPC service in the existing `SharpAudio.Worker` executable with a separate "music" worker pool key. Use `number_conditioner.onnx` (not the `.npz`). Pingpong sampler, 8 steps, no CFG, 44.1 kHz stereo.
- Gate: after every step stop and ask permission.
- Comments: one short line, only what code cannot show. No extra markdown beyond wiki/plan/memory files.

## Status tracker

| Step | Phase | Name | Status |
|---|---|---|---|
| 0 | Scaffold | Plan + memory + agent file | Done (awaiting acceptance) |
| 1 | VAD | `GetProbabilities` on `ISileroVadEngine` | Not started |
| 2 | VAD | `VadOptions` + `VadSegmenter` | Not started |
| 3 | VAD | `VadEndpointer` | Not started |
| 4 | VAD | Offline wiring (engine-independent VAD spans) | Not started |
| 5 | VAD | Streaming endpointer wiring + frontend + docs | Not started |
| 6 | PnC | ONNX export spike | Not started |
| 7 | PnC | `WordPieceTokenizer` | Not started |
| 8 | PnC | `PncEngine` (windowing + label apply) | Not started |
| 9 | PnC | Worker/proto/endpoint wiring (`punctuate`) | Not started |
| 10 | PnC | Frontend + docs + integration test | Not started |
| 11 | Diar | Sortformer ONNX export spike + parity | Not started |
| 12 | Diar | 128-mel feature extractor reuse | Not started |
| 13 | Diar | `SortformerEngine` | Not started |
| 14 | Diar | `DiarSegmenter` + `AssignSpeakers` | Not started |
| 15 | Diar | Worker/proto/endpoint wiring (`diarize`) | Not started |
| 16 | Diar | Frontend + docs + integration test | Not started |
| 17 | Music | ORT load spike + IO names | Not started |
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

## Step specs

### Step 0 - Scaffold
Create this file, `FEATURES_MEMORY.md`, the builder agent file, a repo-memory pointer, and a link line in `LLM_WIKI.md`. No code.

### Phase 1 - VAD (NeMo.cpp `src/asr/vad/`)
- **1**: add `float[] GetProbabilities(float[] samples)` to `ISileroVadEngine` (one prob per 512-sample window at 16 kHz, 256 at 8 kHz; LSTM state carried). Fake-engine unit tests.
- **2**: `VadOptions` (onset 0.5, offset 0.3, padOnsetMs 200, padOffsetMs 200, minDurationOffMs 500, minDurationOnMs) and pure `VadSegmenter.Segment(probs, frameMs, options)` -> `SpeechSpan(start,end)` list: binarize w/ hysteresis -> pad -> merge gaps < minOff -> drop spans < minOn. Unit tests.
- **3**: pure `VadEndpointer`: fires once when `now - lastSpeech >= stopHistoryEouMs` (default 800), re-arms only when lastSpeech advances, `Force()` fires immediately, per-request override. Unit tests.
- **4**: for engines without native VAD (Whisper): run Silero + `VadSegmenter`, transcribe each span, shift timestamps back. Add Silero asset + `vad` options to `config.json`, request fields for onset/offset (optional). Hook in `AsrTranscriberRouter` and worker service (check `transcription.proto`).
- **5**: use `VadEndpointer` in Nemotron `StreamingSession` to commit segments alongside `SegmentBoundaryPolicy`; frontend toggle; wiki update.

### Phase 2 - PnC (NeMo.cpp `src/asr/pnc/`)
- **6**: `tools/pnc/export_pnc_onnx.py` exports model.onnx, `vocab.txt`, `punct_label_ids.csv`, `capit_label_ids.csv`; parity vs NeMo; record model name + tensor names in memory. User hosts the files and gives URL.
- **7**: `WordPieceTokenizer` (lowercase, whitespace split, greedy longest-match, `##`, `[CLS]`/`[SEP]`/`[UNK]`, max word 200 chars) + golden tests from Python.
- **8**: `PncEngine` (ORT session pool pattern) with sliding windows 128/margin 32/stride 62 (no margin at first-start / last-end), merge, apply labels per word; unit tests with fake logits.
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

## Reference files

- VAD: `src/SharpAudio.Api/Services/SileroVadEngine.cs`, `SileroVadGate.cs`, `SegmentBoundaryPolicy.cs`, `SegmentMerger.cs`
- ASR engines/routing: `NemotronAsrEngine.cs`, `WhisperAsrEngine.cs`, `AsrTranscriberRouter.cs`, `src/SharpAudio.Worker.Asr/Services/WorkerTranscriptionService.cs`
- Contracts: `Models/TranscriptionResult.cs`, `Contracts/AudioTranscriptionRequest.cs`, `Protos/transcription.proto`
- Multi-session ONNX pattern: `SupertonicTtsEngine.cs`; routing: `TtsSynthesizerRouter.cs`; workers: `WorkerProcessManager.cs`
- NeMo.cpp refs: `out_refs/NeMo-Speech.cpp/src/asr/{vad,pnc,diar}/`, `conversion/{vad,pnc,diarization}.py`
- Stable Audio: `out_refs/stable-audio-3-small-music-onnx/` (README, `config.json`, `onnx/`, `tokenizer/`)
