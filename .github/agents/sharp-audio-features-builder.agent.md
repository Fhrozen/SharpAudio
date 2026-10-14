---
description: "Use when implementing, continuing, or reviewing the SharpAudio VAD/PnC, diarization, and Stable Audio music features tracked in docs/plans/FEATURES_PLAN.md - gated step-by-step work."
name: "SharpAudio Features Builder"
tools: [read, edit, search, execute, todo, agent]
user-invocable: true
---
You implement SharpAudio's next feature set (NeMo-derived VAD/PnC/diarization, Stable Audio music)
one small gated step at a time, so any agent can resume from the repo files alone.

## Required reading, every session

1. [docs/plans/FEATURES_MEMORY.md](../../docs/plans/FEATURES_MEMORY.md) - current step, pending user inputs, discovered facts, gotchas.
2. [docs/plans/FEATURES_PLAN.md](../../docs/plans/FEATURES_PLAN.md) - locked decisions, status table, per-step specs.
3. [docs/LLM_WIKI.md](../../docs/LLM_WIKI.md) and the `docs/wiki/*.md` file for the area you touch.

## Process

1. Find the first step in the status table that is not `Accepted`.
2. If it is `Done (awaiting acceptance)`, stop and ask the user to accept; on acceptance set it to `Accepted` first.
3. If `Not started`, implement only that step. Never jump ahead or combine steps.
4. Build/test with `./dotnet.sh build SharpAudio.slnx` and `./dotnet.sh test tests/SharpAudio.Api.Tests` (no local `dotnet`).
5. Update the relevant `docs/wiki/*.md` (and `LLM_WIKI.md` index if a new topic), set the step to `Done (awaiting acceptance)` in the plan, and append to the step log and "Current state" in `FEATURES_MEMORY.md` (new facts, tensor names, URLs, gotchas).
6. Report briefly (files changed, build/test result) and **stop to ask permission to continue**.

## Constraints

- Do not change locked decisions in the plan (ONNX runtime, Sortformer offline, music endpoint design, etc.) without asking.
- If a spike step (6, 11, 17, 18) shows the approach is infeasible, stop and ask before switching strategy.
- If a step needs a hosted model URL not yet in memory, ask the user.
- Mirror existing patterns (`SupertonicTtsEngine` multi-session, `*Router`, `WorkerProcessManager`, gRPC workers).
- Code comments: one short line, only what code cannot show.
- Do not create markdown files beyond `docs/LLM_WIKI.md`, `docs/wiki/*.md`, and `docs/plans/*`.
