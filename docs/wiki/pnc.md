# PnC — punctuation, capitalization and sentence boundaries

Post-processing applied to ASR text when `punctuate` is set. Nothing is hardcoded:
the model, tokenizer, labels and windowing params all come from the model entry in
`src/SharpAudio.Api/config.json`.

## Model

`1-800-BAD-CODE/punctuation_fullstop_truecase_english` (HF, Apache-2.0), shipped as
ONNX (`punct_cap_seg_en.onnx`, 210 MB) so there is no export step. 6-layer / 512-dim
transformer, SentencePiece unigram vocab 32000. Four heads:

- punctuation — one prediction per subword
- sentence boundary detection — one bool per subword (the plan's "shifted right by one
  for casing" is NeMo.cpp's rule for its BERT model; this model's reference applies
  casing from the case head directly and SBD at the end of the subword)
- true-case — one prediction per character (`N` per token, `N` = max subword length)
- punctuation-conditioned embeddings

`max_length` 256 (512 positional embeddings), applied as overlapping subsegments
(overlap 16 default, 32 in the model config). Labels from `config.yaml`:
`post_labels` `<NULL>`, `<ACRONYM>`, `.`, `,`, `?` — the ACRONYM class predicts
per-character periods. Input must be pre-lowercased and unpunctuated.

## Tokenizer

`Services/Pnc/ITokenizer.cs` is the abstraction; `UnigramSentencePieceTokenizer` is
the first implementation (other tokenizers, e.g. xlm-roberta BPE, come later behind
the same interface). `Encode` returns `TokenWord` groups — one per input word — with
piece ids only (BOS/EOS are added by the pipeline, not the tokenizer), and each
`TokenPiece` keeps its text so per-character casing and the ACRONYM rule can map
characters back to subwords.

Semantics (verified against `sentencepiece`, see `tools/pnc/spike_tokenizer.py`):

- normalization is space-run collapse + trim only: no NFKC, no case folding, no
  accent stripping, so `café` and `CASE` tokenize as written. Pre-lowercasing and
  punctuation stripping are the engine's job (step 8), because the model was trained
  on lowercase unpunctuated text — the tokenizer stays a faithful mirror of the
  reference
- split on ASCII space; each word scored with a per-word Viterbi/DP over a byte-level
  piece trie with `▁` (U+2581) prepended
- maximize total score; ties resolve to the first piece in vocab order
- special pieces (`<unk>`, `<s>`, `</s>`, `<pad>`) are vocabulary markers, so they are
  kept out of the trie and can never match input text: literal `<s>` segments as
  `▁ <unk> s <unk>`
- byte pieces are only candidates when `ByteFallback` is on (off matches the reference
  for this model, whose vocab has no byte pieces); a byte with no matching piece is
  unknown (score 0, consumes one byte) and consecutive unknowns collapse into one
  `<unk>`
- `MaxPieceLength` is the longest piece with special pieces excluded, since the
  per-character heads pad each token to that length
- `SentencePieceModelReader` reads the `.model` protobuf directly (no Python). The
  `type` field is sparse in this file — only four entries carry one, and the
  TrainerModel enum values conflict with the runtime `SentencePiece` enum — so
  special pieces are located by name and the type is used only to tell byte pieces
  apart from normal ones

Performance: the trie is a flat node list with children allocated lazily (most nodes
are leaves), piece byte lengths are precomputed, and the DP scratch buffers are reused
across words within one `Encode` call.

## ONNX graph facts (step 6 spike)

`punct_cap_seg_en.onnx` (opset 17, 716 nodes, no external data) has one input and four
outputs, and the argmax is already inside the graph — C# does not compute it:

- input: `input_ids` int64 `(batch, seq)`
- outputs: `pre_preds` int64 `(batch, seq)`, `post_preds` int64 `(batch, seq)`,
  `cap_preds` bool `(batch, seq, 16)`, `seg_preds` bool `(batch, seq)`

`cap_preds` is padded to 16 per token, which is `MaxPieceLength` for this vocab. The
output names are stable and are what `PncEngine` should read rather than positional
indexes. `pre_preds`/`post_preds` are label ids indexing `config.yaml`; `cap_preds` and
`seg_preds` are plain booleans with no label list.

Windowing and label application (from the reference `punctuators` pipeline, which the C#
engine reimplements): `max_length` 256 minus 2 for BOS/EOS gives 254 tokens per segment;
segments overlap by `overlap` (16 default), the first segment has no overlap and later
ones start at `previous stop - overlap`; each segment is sent as `[BOS] + ids + [EOS]`
and predictions are read from `[1:length-1]`. When building the output text, `overlap / 2`
is dropped at each end of a segment except the first and last. Per subword, per character:
the pre-label is inserted before the first character, casing is applied per character, the
post-label is appended after the last character, ACRONYM appends a period after every
character, and SBD ends the sentence at the last character. The word space comes from the
`▁` prefix.

The graph is verified end to end for one window: `hello world the quick brown fox jumped
over the lazy dog` -> `Hello world, the quick brown Fox jumped over the lazy dog.`, which
matches the reference pipeline exactly (`tools/pnc/spike_onnx_io.py` dumps IO names/shapes,
`tools/pnc/spike_forward.py` applies the labels and prints per-token predictions). Feeding
punctuation in echoes it (`hello, world.` -> `Hello,, World.,`), confirming that
pre-cleaning belongs to the engine.

## Engine (step 8)

`Services/Pnc/` is split so the interesting logic is pure and testable without ONNX:

- `PncModelConfig` — labels, `MaxLength`, `Overlap`, `PadId`, `UnkId`, the `<NULL>` and
  `<ACRONYM>` label names; `PreLabel`/`PostLabel` return null for the null label
- `PncConfigReader` — the small YAML subset the model configs use (scalar keys, flow
  lists). Quote-aware so a label that is itself a comma survives; unquoted empty slots
  are separators. No YAML dependency is added
- `PncInputCleaner` — lowercasing and punctuation/symbol stripping, which the reference
  pipeline does not do; the model was trained on lowercase unpunctuated text, so this is
  the engine's responsibility
- `PncWindowPlanner` — `maxTokens = MaxLength - 2`, first window has no overlap, later
  windows start at `previous stop - overlap`
- `IPncPredictor` / `OnnxPncPredictor` — builds `[BOS] + ids + [EOS]` padded to
  `MaxLength`, reads the four outputs by name, slices `[1:length-1]`
- `PncCollector` — pure text assembly from predictions plus piece text
- `PncEngine` — clean, encode, plan, predict per window, collect

Collector rules, verified against the reference `punctuators` implementation:

- `overlap / 2` is dropped at the start of every window except the first, and at the end
  of every window except the last
- a piece beginning with the word-start marker emits a space when output is non-empty;
  `char_start` is 1 for those pieces and 0 otherwise
- the pre label is applied at `char_start`, so it also applies to word-initial pieces
- `cap_preds` is indexed by the absolute character index inside the piece string, so the
  marker keeps its own slot
- ACRONYM appends a period after every character of the piece; otherwise the post label
  is appended after the last character. The literal `<ACRONYM>` label is never written to
  the output
- SBD flushes the sentence at the last character, after the post label has been written,
  so post punctuation lands inside the sentence it belongs to
- byte-fallback pieces (`<0xNN>`) would be emitted literally by the collector; the shipped
  vocab has no byte pieces, so this is a documented limitation

## Tests

`tests/SharpAudio.Api.Tests/UnigramSentencePieceTokenizerTests.cs` covers the rules
with a hand-built vocab, including word grouping, special pieces never matching text
and byte fallback on/off (byte fallback cannot be checked against the real model
because that vocab has no byte pieces). `tests/SharpAudio.Api.IntegrationTests/PncTokenizerGoldenTests.cs`
holds golden cases generated from the Python reference and is gated by
`Support/PncModelTestGate.cs` (`PNC_TOKENIZER_MODEL` env var, skipped when unset), so
the model file is never committed.
