# Features Memory (agent handoff log)

> Update at the end of every step. Keep it short and factual. Source of truth for step status is
> [FEATURES_PLAN.md](FEATURES_PLAN.md).

## Current state

- Last completed step: **0** (scaffold) - awaiting user acceptance.
- Next step: **1** (`GetProbabilities` on `ISileroVadEngine`) once step 0 is accepted.

## Pending user inputs

- Hosted URLs for exported PnC ONNX bundle (needed from step 6/9).
- Hosted URLs for exported Sortformer ONNX (needed from step 11/13).

## Discovered facts

- Silero VAD already exists in C# (Nemotron only): `ISileroVadEngine { Reset(); ContainsSpeech(float[], float) }`, `SileroVadGate.ShouldDropChunk`. No per-window probabilities exposed yet.
- No PnC or diarization exists. `TranscriptionSegment` has no speaker field; `AudioTranscriptionRequest` has `EnableVad`, `IncludeSegments`, `MinSegmentDurationSeconds`.
- Engines routed by `model.Engine` string (`AsrTranscriberRouter`, `WorkerTranscriptionService`); config in `src/SharpAudio.Api/config.json` (`models`, `asrModels`).
- NeMo.cpp PnC/Sortformer ship GGUF only; ONNX export needed (decision locked).
- Sortformer v2: 4 speakers, 80 ms frames, 16 kHz mono, 128 mels (25 ms/10 ms), Q8 GGUF ~147 MB.
- Stable Audio: HF `lsb/stable-audio-3-small-music-onnx`; graphs `text_encoder_q4` (3 chunks), `dit_q4` (4 chunks), `decoder_q4` (1 chunk), `number_conditioner`; tensor names NOT documented (step 17). Latent `(1,256,T_lat)`, cross-attn `(1,257,768)`, global cond `(1,768)`, local-add `(1,257,T_lat)`, padding mask `(1,T_lat)`, decoder out `(1,2,T_lat*4096)`. Licenses: Stability Community License + Gemma Terms; attribution required.
- Reference demo with full pipeline: https://github.com/lsb/stable-audio-3-small-music-onnx

## Gotchas

- No local `dotnet`; use `./dotnet.sh`.
- Do not merge features across steps; stop after each step.

## Step log

| Step | Date | Summary | Files touched |
|---|---|---|---|
| 0 | 2026-10-15 | Created plan, memory, agent file, wiki link, repo-memory pointer | docs/plans/*, .github/agents/sharp-audio-features-builder.agent.md, docs/LLM_WIKI.md |
