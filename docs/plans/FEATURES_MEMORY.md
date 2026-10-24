# Features Memory (agent handoff log)

> Update at the end of every step. Keep it short and factual. Source of truth for step status is
> [FEATURES_PLAN.md](FEATURES_PLAN.md).

## Current state

- Last completed step: **9** (PnC wiring: catalog, engine pool, applier, decorator,
  `punctuate` form field, proto fields 25-27, worker application), accepted by the user on
  2026-10-25; the user asked to stop there.
  Step 8 was accepted by the user on 2026-10-25 (with the verified collector rule); steps
  0, 6, 7 accepted; 17, 1, 2, 3, 4, 5 accepted or awaiting acceptance.
- Phase C model decision (user): **`1-800-BAD-CODE/punctuation_fullstop_truecase_english`** - already ONNX
  on HF, so the export spike is gone and step 6 is just recording IO names. WordPiece is dropped; the
  tokenizer is unigram SentencePiece behind `ITokenizer` (user chose "ITokenizer + SentencePiece/unigram
  first"), so the multilingual phase is now additional languages only.
- Next step: **10** (frontend `punctuate` checkbox, `docs/wiki/asr-engines.md`, gated integration
  test). User stopped after step 9, so permission is needed before starting it.
- Step 7 revision (user-requested, 2026-10-25): pieces grouped per word, special pieces can never match
  input text, `MaxPieceLength` excludes specials, byte-fallback support, performance work, normalization
  ownership decided (tokenizer stays parity-only; the engine pre-lowercases and strips punctuation in
  step 8). 119 unit tests, gated golden test 2/2 (13 cases + word grouping), python reference 34/34.
- Spike script: `tools/music/spike_ort_load.py` (+ `tools/music/requirements.txt`).

## Pending user inputs

- Hosted URLs for exported Sortformer ONNX (needed from step 11/13).
- PnC no longer needs a hosted export: the HF model is already ONNX. Downloading it (210 MB) needs the
  user's permission.

## Discovered facts

- Silero VAD already exists in C# (Nemotron only): `ISileroVadEngine { Reset(); ContainsSpeech(float[], float) }`, `SileroVadGate.ShouldDropChunk`. Per-window probabilities now exposed via `GetProbabilities`/`FlushProbabilities`/`FrameSamples`/`FrameDurationMs`, windowed by `Services/VadWindowStream.cs` (pure logic, carries a partial window across calls).
- No PnC or diarization exists. `TranscriptionSegment` has no speaker field; `AudioTranscriptionRequest` has `EnableVad`, `IncludeSegments`, `MinSegmentDurationSeconds`.
- Engines routed by `model.Engine` string (`AsrTranscriberRouter`, `WorkerTranscriptionService`); config in `src/SharpAudio.Api/config.json` (`models`, `asrModels`).
- NeMo.cpp PnC/Sortformer ship GGUF only; ONNX export needed for Sortformer (decision locked). PnC now uses
  the published HF ONNX instead, so no export step.
- Sortformer v2: 4 speakers, 80 ms frames, 16 kHz mono, 128 mels (25 ms/10 ms), Q8 GGUF ~147 MB.
- Stable Audio: HF `lsb/stable-audio-3-small-music-onnx`; graphs `text_encoder_q4` (3 chunks), `dit_q4` (4 chunks), `decoder_q4` (1 chunk), `number_conditioner`; tensor names NOT documented (step 17). Latent `(1,256,T_lat)`, cross-attn `(1,257,768)`, global cond `(1,768)`, local-add `(1,257,T_lat)`, padding mask `(1,T_lat)`, decoder out `(1,2,T_lat*4096)`. Licenses: Stability Community License + Gemma Terms; attribution required.
- Reference demo with full pipeline: https://github.com/lsb/stable-audio-3-small-music-onnx (404 - not public, so sampler semantics must be derived from `config.json`).

### Step 6 PnC ONNX facts (punct_cap_seg_en.onnx, ORT 1.20.1 CPU EP)

- HF files (all six downloaded to `out_refs/punctuation_fullstop_truecase_english/`):
  `config.yaml` 302 B, `pipeline.py` 883 B, `requirements.txt` 11 B, `README.md` 16.8 KB,
  `spe_32k_lc_en.model` 587,902 B, `punct_cap_seg_en.onnx` 209,532,928 B. No export step
  needed, and nothing is committed to the repo.
- Graph: opset `ai.onnx:17`, 716 nodes, graph name `torch_jit`, no external data.
- Single input `input_ids` int64 with dynamic batch/seq dims. Four outputs, **already
  argmaxed inside the graph** — C# does not compute argmax:
  `pre_preds` int64 `(batch, seq)`, `post_preds` int64 `(batch, seq)`, `cap_preds` bool
  `(batch, seq, 16)`, `seg_preds` bool `(batch, seq)`. Dummy batch-1 forward at 256 tokens
  gives `(1,256)`, `(1,256)`, `(1,256,16)`, `(1,256)`.
- `cap_preds` dim 16 is a fixed padding equal to `MaxPieceLength` for this vocab (max piece
  length 16), so C# must read the padded rows and ignore ids beyond the piece length.
- Labels from `config.yaml`: `pre_labels: ["<NULL>", "¿"]`, `post_labels: ["<NULL>",
  "<ACRONYM>", ".", ",", "?"]`. `pre_labels` are applied by the collector even though the
  config comment says they are not used. Cap and seg have no label list — they are booleans.
- Windowing (reference `punctuators`): `max_length` 256 minus 2 for BOS/EOS = 254 tokens per
  segment; first segment has no overlap, later ones start at `previous stop - overlap`
  (default 16, model config 32); each segment sent as `[BOS] + ids + [EOS]`, batch padded
  with `pad_id`, predictions sliced `[1:length-1]`.
- Collector rules: drop `overlap / 2` at the start (unless first segment) and at the end
  (unless last); per token per character — pre-label before the first char, cap per char,
  post-label after the last char, ACRONYM appends a period after every char, SBD finalizes
  the sentence at the last char. Word space comes from the `▁` prefix. Note this contradicts
  the locked plan bullet "SBD shifted right by one for casing" (a NeMo.cpp fact); step 8
  must use the verified rule.
- Input must be pre-lowercased and unpunctuated: feeding punctuation in echoes it
  (`hello, world.` -> `Hello,, World.,`), so pre-cleaning is the engine's job.
- Parity: the C#-mirrored label application reproduces the reference pipeline exactly for
  `hello world the quick brown fox jumped over the lazy dog` -> `Hello world, the quick
  brown Fox jumped over the lazy dog.`; other reference outputs: `i live in the u.s.a. and
  it is a radio comedy programme` -> `I live in the U.s.a. and it is a radio comedy
  programme.`, `mr smith went to washington dc on tuesday` -> `Mr. Smith went to Washington,
  D.C. on Tuesday.`
- `punctuators` pulls ~50 packages including torch, so the C# engine reimplements the
  dataset windowing and collector rather than depending on it.
- Spike scripts: `tools/pnc/spike_onnx_io.py` (IO names/shapes/dtypes + dummy forward) and
  `tools/pnc/spike_forward.py` (encode, forward, apply labels). `onnx.GraphProto` has no
  `external_data` attribute and no `opset` field (use `ModelProto.opset_import`); ORT
  `io.type` returns `tensor(int64)`/`tensor(float)`/`tensor(bool)`, not bare names.

### Step 8 engine facts (collector rules verified against `punctuators`)

- Dataset windowing: `maxTokens = max_length - 2` (254); first window has no overlap;
  `adjusted = start - (inputIndex == 0 ? 0 : overlap)`; `stop = min(adjusted + maxTokens,
  ids.Count)`; segment = `[BOS] + ids + [EOS]` padded with `pad_id`; predictions sliced
  `[1:length-1]`.
- Collector window trimming: `overlap / 2` dropped at the start of every window except the
  first and at the end of every window except the last.
- Per piece: space before the piece when it starts with `▁` and output is non-empty;
  `charStart = 1` for those pieces else 0; **pre label applies at `charStart`**, so it also
  applies to word-initial pieces (the earlier "before the first character" wording was
  incomplete).
- `cap_preds` is indexed by the absolute character index inside the piece string, so the
  `▁` marker keeps its own slot.
- ACRONYM appends a period after every character; otherwise the post label is appended at
  the last character. The literal `<ACRONYM>` string is never written to output.
- **SBD flush happens after the post label is written**, so post punctuation lands inside
  the sentence it belongs to. Confirmed empirically with ORT 1.20.1 + sentencepiece +
  punctuators on `hello world this is a test it works fine`.
- `config.yaml` has no `pad_id`/`unk_id` keys; the reader defaults them to 0, and defaults
  `max_length` 256 / `overlap` 16 (model config says 32, reference `infer()` default 16).
- Flow-list parsing must be quote-aware: a label that is itself a comma (`","`) was being
  dropped by naive `Split(',')`.
- Byte-fallback pieces (`<0xNN>`) would be emitted literally by the collector; the shipped
  vocab has no byte pieces, so this stays a documented limitation.
- `IPncPredictor` keeps ORT out of the pure logic, so unit tests run with fake predictions.

### Step 7 tokenizer facts
- Model: `1-800-BAD-CODE/punctuation_fullstop_truecase_english` (Apache-2.0), 6L/512 transformer, SentencePiece
  unigram vocab 32000 (unk 0, bos 1, eos 2), published ONNX `punct_cap_seg_en.onnx` (210 MB).
- Four heads: punctuation (per subword), SBD, true-case (per character, N predictions per token where N = max
  subword length), punctuation-conditioned embeddings. SBD is shifted right by one for casing. ACRONYM class
  predicts per-character periods. `max_length` 256 (512 positional embeddings), overlapping subsegments
  (overlap default 16, dataset default 32). Input must be pre-lowercased and unpunctuated.
- Labels from `config.yaml`: `post_labels` `["<NULL>", "<ACRONYM>", ".", ",", "?"]`; `pre_labels` are not
  actually used. Nothing hardcoded.
- Tokenizer semantics (parity with `sentencepiece`, 34/34 corpus + 400 randomized cases, and now 13 golden
  cases in C#): normalization = collapse ASCII space runs + trim only (no NFKC, no case folding, no accent
  stripping); split on ASCII space; per-word DP with `\u2581` prepended; maximize total score; ties -> first
  in vocab order; no matching piece -> unk (score 0, consumes one char); consecutive unknowns collapse into
  one unk; `Encode` returns pieces grouped per word (BOS/EOS added by the pipeline).
- Special pieces (`<unk> <s> </s> <pad>`) are kept out of the trie so they can never match input text:
  literal `<s>` encodes as `\u2581 <unk> s <unk>`, which `sentencepiece` confirms. Byte pieces are
  candidates only when `ByteFallback` is true and are not inserted into the trie (a byte piece can collide
  with a normal piece on the same first byte); this vocab has no byte pieces, so byte fallback is covered
  by unit tests only. A byte piece consumes 1 byte, not the length of its `<0xNN>` name.
- `MaxPieceLength` excludes special pieces and counts a byte piece as 1 (16 in the real vocab).
- Performance shape: flat `List<TrieNode>` with lazily allocated `Dictionary<byte, int>` children (most
  nodes are leaves), precomputed per-piece byte lengths, and `best`/`choice` DP buffers reused across
  words in one call.
- The `.model` protobuf `type` field is sparse in this file (only four entries carry one) and the TrainerModel
  enum values conflict with the runtime `SentencePiece` enum, so special pieces are located by name and the
  type is used only to skip pieces that can never match text.
- Golden test gate: `tests/SharpAudio.Api.IntegrationTests/Support/PncModelTestGate.cs` (env
  `PNC_TOKENIZER_MODEL`), mirroring `AsrModelTestGate`. Model file is not committed; run with
  `PNC_TOKENIZER_MODEL=/tmp/spe_32k_lc_en.model`.
- Spike: `tools/pnc/spike_tokenizer.py` (reference implementation the C# port is checked against).

### Step 17 tensor names (ORT 1.20.1 CPU EP - all 4 graphs load AND run)

| Graph | opset | inputs | outputs |
|---|---|---|---|
| `text_encoder_q4` | 21 | `input_ids` (1,256) int64, `attention_mask` (1,256) int64 | `last_hidden_state` (1,256,768) float |
| `dit_q4` | 21 | `x` (1,256,t_lat), `t` (1) float, `cross_attn_cond` (1,257,768), `global_embed` (1,768), `local_add_cond` (1,257,t_lat), `padding_mask` (1,t_lat) bool | `out` (1,256,t_lat) float |
| `decoder_q4` | 21 | `latents` (1,256,t_lat) float | `audio` (1,2,8192*s1) float |
| `number_conditioner` | 20 | `seconds` (1) float | `embedding` (1,1,768) float |

- `MatMulNBits` x84 / x184 / x25 and `GatherBlockQuantized` x1 (text encoder embedding table) execute on the CPU EP in 1.20.1 - no ORT bump needed.
- Decoder output dim is the symbolic `8192*s1`; measured `t_lat=16 -> 65536`, i.e. output length = `t_lat * 4096` (`t_lat = 2*s1`).
- `number_conditioner` returns `(1,1,768)`: squeeze to `(1,768)` for `global_embed`, and append as token 257 to the
  `(1,256,768)` text encoder output to build `cross_attn_cond` `(1,257,768)`.
- Open for step 19/20: what `t` means (raw `t` in [0,1] vs logsnr) and `padding_mask` polarity. `padding_mode: "learned"`
  in `config.json` means `attention_mask` is meaningful, not decorative.

## Gotchas

- No local `dotnet`; use `./dotnet.sh`.
- Do not merge features across steps; stop after each step.
- Mixing `ContainsSpeech` (per-call zero-padding) and `GetProbabilities` (carry-over) on one engine instance corrupts the LSTM state: pick one path per utterance.
- No local `silero_vad.onnx`, so engine-level probability tests are gated; `VadWindowStream` + a fake engine cover the logic.
- `VadSegmenter` semantics: the frame that drops below `Offset` is excluded from the span (end exclusive), padding is
  clamped to the audio bounds, gaps shorter than `MinDurationOffMs` merge, spans shorter than `MinDurationOnMs` drop.
  Options are per-call so diarization passes its own thresholds (106 unit tests pass).
- `VadSegmenter` padding can push a span past the end of the buffer, so `VadSpanTranscriber.Slice` clamps to
  the audio length.
- One VAD engine instance is shared across requests, so span transcription runs behind a `SemaphoreSlim(1,1)`
  gate in both `WhisperAsrTranscriber` and `WorkerTranscriptionService`; `VadEndpointer` fires once per
  silence run and re-arms only when `lastSpeechMs` advances.
- `SileroVadGate.LastChunkHadSpeech` is the per-chunk decision the streaming session feeds the endpointer,
  so endpointing does not run the model twice on the same chunk. Endpoint commits take the whole pending
  range (the utterance ended, so the last word is complete); time/punctuation commits still hold back a
  possibly-continuing word.
- `transcription.proto`: VAD request fields are 20-24 (`TranscribeRequest`) and response `segments = 5`;
  audio fields stay at 10-12 so published field numbers are not renumbered. `StreamConfig` gained
  `enable_endpointing = 7` and `stop_history_eou_ms = 8` (the API's configured threshold, forwarded so
  in-process and worker mode behave identically); `TranscribeStreamUpdate` gained `is_endpoint_final = 4`.
- C# collection expressions need an explicit target type (`var pieces = [...]` is CS9176);
  `Assert.Equal(["a","b"], seq.Select(...))` is ambiguous - compare joined strings.
- ORT in this project: `Microsoft.ML.OnnxRuntime.OnnxValue` does not exist (use
  `NamedOnnxValue`), `Tensor<T>` has no `TensorData` (use `Tensor.ToArray()`), and
  `CreateFromTensor` needs a `DenseTensor<T>` rather than a Span.
- `System.Text` (`StringBuilder`) is not in implicit usings; `params` cannot follow an
  optional parameter - use a static fixture array plus a defaulted factory method.
- New test files need `namespace SharpAudio.Api.Tests;` to match the existing convention.
- Unit tests are now 160 (15 new step 9 tests); integration baseline unchanged (3 pre-existing
  `ModelHealthTests` failures for models not on disk).
- Testability: `IPncEnginePool` (extends `IDisposable`) and `IPncEngine : IDisposable` exist so
  the decorator can be unit tested with fakes; `PncModelCatalog` takes an optional config path.
  `PncEngine.Apply` is guarded by a lock because the tokenizer reuses its DP buffers.
- PnC models live in a separate `pncModels` config section and catalog, so `/v1/models` and
  `ModelHealthTests` are unaffected. Streaming is left unpunctuated (partial text), documented.

## Step log

| Step | Date | Summary | Files touched |
|---|---|---|---|
| 0 | 2026-10-15 | Created plan, memory, agent file, wiki link, repo-memory pointer | docs/plans/*, .github/agents/sharp-audio-features-builder.agent.md, docs/LLM_WIKI.md |
| 17 | 2026-10-23 | ORT load + dummy-forward spike for the 4 music graphs; IO names/shapes/dtypes recorded; no ORT bump needed | tools/music/spike_ort_load.py, tools/music/requirements.txt, docs/plans/FEATURES_MEMORY.md, docs/plans/FEATURES_PLAN.md |
| 1 | 2026-10-23 | Per-frame VAD probabilities: `VadWindowStream` (whole-buffer + streaming carry-over, zero-padded flush), interface extended, gate fake updated, tests added | src/SharpAudio.Api/Services/VadWindowStream.cs, src/SharpAudio.Api/Services/SileroVadEngine.cs, tests/SharpAudio.Api.Tests/VadWindowStreamTests.cs, tests/SharpAudio.Api.Tests/SileroVadProbabilitiesTests.cs, tests/SharpAudio.Api.Tests/SileroVadGateTests.cs, docs/wiki/asr-engines.md |
| 2 | 2026-10-23 | `VadOptions` appsettings section + pure `VadSegmenter` (hysteresis, padding, gap merge, min-duration) -> `SpeechSpan`; tests added | src/SharpAudio.Api/Options/VadOptions.cs, src/SharpAudio.Api/Services/VadSegmenter.cs, src/SharpAudio.Api/Models/SpeechSpan.cs, src/SharpAudio.Api/Program.cs, src/SharpAudio.Api/appsettings.json, tests/SharpAudio.Api.Tests/VadSegmenterTests.cs, docs/wiki/asr-engines.md |
| 3 | 2026-10-23 | `VadEndpointer` mirroring NeMo.cpp (hysteresis + stop-history, fire-once, re-arm, Force/Reset); tests added. Streaming wiring deferred to step 5 | src/SharpAudio.Api/Services/VadEndpointer.cs, tests/SharpAudio.Api.Tests/VadEndpointerTests.cs, docs/wiki/asr-engines.md |
| 4 | 2026-10-23 | Engine-independent VAD spans: `VadSpanTranscriber` + `SpanTranscription`, Whisper VAD path, `WithOverrides` request overrides, `EncodeMono16kWav`, proto VAD fields + response segments, worker/proxy wiring, `silero_vad.onnx` asset, `SupportsVad` true for Whisper; tests + docs | src/SharpAudio.Api/Services/VadSpanTranscriber.cs, src/SharpAudio.Api/Models/SpanTranscription.cs, src/SharpAudio.Api/Services/WhisperAsrTranscriber.cs, src/SharpAudio.Api/Services/WavAudioUtils.cs, src/SharpAudio.Api/Services/AsrWorkerProxyTranscriber.cs, src/SharpAudio.Api/Services/AsrModelCatalog.cs, src/SharpAudio.Api/Program.cs, src/SharpAudio.Api/config.json, src/SharpAudio.Api/Protos/transcription.proto, src/SharpAudio.Worker.Asr/Services/WorkerTranscriptionService.cs, tests/SharpAudio.Api.Tests/VadSpanTranscriberTests.cs, tests/SharpAudio.Api.Tests/SileroVadProbabilitiesTests.cs, tests/SharpAudio.Api.Tests/SileroVadGateTests.cs, tests/SharpAudio.Api.Tests/AsrModelCatalogTests.cs, docs/wiki/asr-engines.md, docs/wiki/testing.md, docs/API.md, docs/MODELS.md |
| 5 | 2026-10-23 | Endpointing wired into Nemotron `StreamingSession`: gate exposes `LastChunkHadSpeech`, session tracks `_audioElapsedMs`/`_lastSpeechMs`, polls `VadEndpointer` per chunk and commits on endpoint (`CommitSegment(isEndpointFinal)`), `StreamingUpdate.IsEndpointFinal`; `enableEndpointing` threaded through `IAsrTranscriber`/router/proxy/worker (Whisper ignores it), proto `StreamConfig` 7-8 + update field 4, `enable_endpointing` query param (request overrides `VadOptions.EnableEndpointing`, needs VAD), frontend checkbox + `⌁` marker, gate test added (106 pass) | src/SharpAudio.Api/Services/SileroVadGate.cs, src/SharpAudio.Api/Services/NemotronAsrEngine.cs, src/SharpAudio.Api/Services/NemotronAsrTranscriber.cs, src/SharpAudio.Api/Services/WhisperAsrTranscriber.cs, src/SharpAudio.Api/Services/AsrTranscriberRouter.cs, src/SharpAudio.Api/Services/AsrWorkerProxyTranscriber.cs, src/SharpAudio.Api/Services/IStreamingTranscriptionSession.cs, src/SharpAudio.Api/Services/IAsrTranscriber.cs, src/SharpAudio.Api/Protos/transcription.proto, src/SharpAudio.Worker.Asr/Services/WorkerTranscriptionService.cs, src/SharpAudio.Api/Program.cs, tests/SharpAudio.Api.Tests/SileroVadGateTests.cs, frontend/src/types.ts, frontend/src/App.vue, frontend/src/components/LiveTranscription.vue, docs/wiki/asr-engines.md, docs/wiki/frontend.md, docs/wiki/testing.md |
| 6 | 2026-10-25 | Downloaded the published HF ONNX (209.5 MB) + tokenizer + config into `out_refs/` (not committed); recorded graph facts (opset 17, 716 nodes, one `input_ids` int64 input, four already-argmaxed outputs incl. `cap_preds` bool padded to 16 = `MaxPieceLength`), windowing and collector rules, label lists; verified label application reproduces the reference pipeline; spike only, no C# production code | tools/pnc/spike_onnx_io.py, tools/pnc/spike_forward.py, docs/plans/FEATURES_MEMORY.md, docs/plans/FEATURES_PLAN.md, docs/wiki/pnc.md |
| 7 | 2026-10-25 | `ITokenizer` + `UnigramSentencePieceTokenizer` (protobuf `.model` reader, per-word DP max-score with vocab-order tie-break, unk runs merged, name-based special ids); revised after the first pass was not accepted: pieces grouped per word (`TokenWord`), special pieces never match text, `MaxPieceLength` excludes specials, byte-fallback flag, normalization ownership, lazy-children flat trie + reused DP buffers; spike corpus 34/34, 119 unit tests, gated golden test 2/2 (13 cases + word grouping); plan/memory/wiki updated for the Phase C model decision | src/SharpAudio.Api/Services/Pnc/ITokenizer.cs, src/SharpAudio.Api/Services/Pnc/SentencePieceModelReader.cs, src/SharpAudio.Api/Services/Pnc/UnigramSentencePieceTokenizer.cs, tests/SharpAudio.Api.Tests/UnigramSentencePieceTokenizerTests.cs, tests/SharpAudio.Api.IntegrationTests/Support/PncModelTestGate.cs, tests/SharpAudio.Api.IntegrationTests/PncTokenizerGoldenTests.cs, tools/pnc/spike_tokenizer.py, docs/wiki/testing.md, docs/plans/FEATURES_PLAN.md, docs/plans/FEATURES_MEMORY.md |
| 8 | 2026-10-25 | `PncEngine` over pure pieces: `PncModelConfig`, `PncConfigReader` (quote-aware flow lists so a comma label survives), `PncInputCleaner` (engine-owned pre-lowercasing/punctuation strip), `PncWindowPlanner` (`MaxLength - 2`, first window unoverlapped), `IPncPredictor` + `OnnxPncPredictor` (reads the four outputs by name, slices `[1:length-1]`), `PncCollector` (overlap-half trimming, pre label at `charStart`, cap indexed by absolute char index, ACRONYM per-char periods, post label written before the SBD flush); collector rules verified against `punctuators` and confirmed empirically with ORT 1.20.1; 26 new unit tests with fake predictions (145 total), integration baseline unchanged | src/SharpAudio.Api/Services/Pnc/PncModelConfig.cs, src/SharpAudio.Api/Services/Pnc/PncConfigReader.cs, src/SharpAudio.Api/Services/Pnc/PncInputCleaner.cs, src/SharpAudio.Api/Services/Pnc/PncWindowPlanner.cs, src/SharpAudio.Api/Services/Pnc/OnnxPncPredictor.cs, src/SharpAudio.Api/Services/Pnc/PncCollector.cs, src/SharpAudio.Api/Services/Pnc/PncEngine.cs, tests/SharpAudio.Api.Tests/PncConfigReaderTests.cs, tests/SharpAudio.Api.Tests/PncInputCleanerTests.cs, tests/SharpAudio.Api.Tests/PncWindowPlannerTests.cs, tests/SharpAudio.Api.Tests/PncCollectorTests.cs, tests/SharpAudio.Api.Tests/PncEngineTests.cs, docs/wiki/pnc.md, docs/plans/FEATURES_PLAN.md, docs/plans/FEATURES_MEMORY.md |
| 9 | 2026-10-25 | PnC wiring: `PncModelDefinition` + `IPncModelCatalog`/`PncModelCatalog` (reads `pncModels`, optional config path, empty name -> default), `IPncEnginePool`/`PncEnginePool` (lazy per-model engine, last-access, `TryReleaseEngine`), `PncTextApplier` (join sentences, punctuate text and each segment keeping Id/Start/End), `PunctuatingTranscriber` decorator (in-process), worker application after segment merge (worker mode) so it never runs twice, `punctuate`/`pnc_model` form fields, proto fields 25-27, `IModelCache` PnC overload, `config.json` `pncModels` entry, streaming left unpunctuated; 15 new unit tests with fakes (160 total), integration baseline unchanged | src/SharpAudio.Api/Models/PncModelDefinition.cs, src/SharpAudio.Api/Services/Pnc/IPncModelCatalog.cs, src/SharpAudio.Api/Services/Pnc/PncModelCatalog.cs, src/SharpAudio.Api/Services/Pnc/PncEnginePool.cs, src/SharpAudio.Api/Services/Pnc/PncTextApplier.cs, src/SharpAudio.Api/Services/Pnc/PunctuatingTranscriber.cs, src/SharpAudio.Api/Services/Pnc/PncEngine.cs, src/SharpAudio.Api/Contracts/AudioTranscriptionRequest.cs, src/SharpAudio.Api/Services/IModelCache.cs, src/SharpAudio.Api/Services/ModelCache.cs, src/SharpAudio.Api/Services/AsrWorkerProxyTranscriber.cs, src/SharpAudio.Api/Protos/transcription.proto, src/SharpAudio.Api/Program.cs, src/SharpAudio.Worker.Asr/Program.cs, src/SharpAudio.Worker.Asr/Services/WorkerTranscriptionService.cs, src/SharpAudio.Api/config.json, tests/SharpAudio.Api.Tests/PncModelCatalogTests.cs, tests/SharpAudio.Api.Tests/PncTextApplierTests.cs, tests/SharpAudio.Api.Tests/PunctuatingTranscriberTests.cs, tests/SharpAudio.Api.Tests/SpeechEndpointTests.cs, docs/wiki/pnc.md, docs/wiki/backend-architecture.md, docs/wiki/testing.md, docs/API.md, docs/CONFIGURATION.md, docs/plans/FEATURES_PLAN.md, docs/plans/FEATURES_MEMORY.md |
