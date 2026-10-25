# SharpAudio — LLM Wiki

A condensed, agent-optimized architecture reference for SharpAudio. This file is meant to let an
agent ramp up quickly without re-discovering the codebase from scratch.

**This file is a short summary/index.** Full topic-level detail lives in `docs/wiki/*.md` (linked
below) — read those only when you need the low-level detail for the topic you're working on.

## Keeping this doc updated

**Rule: update the relevant `docs/wiki/*.md` detail file (and this file's one-line summary/link if
the topic itself is new) as part of any change that affects architecture, config surface,
endpoints, or conventions — do this alongside the code change, not as a deferred separate pass.**
If you add a new engine, endpoint, env var, or worker type, update the matching detail file in the
same PR/session.

## Wiki headers (agent convention)

Every `docs/wiki/*.md` detail file starts with a YAML frontmatter block. It is the machine-readable
source of truth for which source files a wiki file documents, and which section documents each one:

```yaml
---
wiki: asr-engines            # slug, must equal the filename
topic: ASR engines (Whisper + Nemotron)
covers:                      # scope globs — where this wiki file's files live
  - src/SharpAudio.Core/Services/Whisper*.cs
sections:                    # anchor slug -> the files that anchor documents
  domain-model-and-abstractions:
    - src/SharpAudio.Core/Models/AsrModelDefinition.cs
  whisper-engine:
    - src/SharpAudio.Core/Services/WhisperAsrEngine.cs
updated: 2026-10-24          # date of the last real content change, bumped on edit
---
```

Rules:

1. **Anchor slug** = the heading text, lowercased, with runs of non-alphanumeric characters replaced
   by a single hyphen (`## VAD support` → `vad-support`). Every slug in `sections:` must be an actual
   heading in that wiki file.
2. **Every mapped source file carries a one-line header** naming the wiki file and anchor that
   documents it, as the first line of the file (after a shebang, if any):
   - `.cs`, `.proto`, `.ts`, `.js`: `// wiki: asr-engines#whisper-engine`
   - `.vue`, `.csproj`: `<!-- wiki: frontend#components -->`
   - `Dockerfile`, `.sh`, `.ps1`: `# wiki: docker-packaging#image-stages`
3. **Headers are only stamped in folders containing more than one mapped source file.** Single-file
   folders are mapped by the wiki `sections:` entry alone. Every `Program.cs` is the explicit
   exception — it always carries a header, because bare-name `Program.cs` is ambiguous across
   `SharpAudio.Api` and the `SharpAudio.Worker.*` executables.
4. Mapped file types: `.cs`, `.proto`, `.vue`, `.ts`, `.js`, `.sh`, `.ps1`, `.csproj`, and files named
   `Dockerfile`. `.csproj` is mapped because each project file is the build contract of a wiki-mapped
   project (the `sections:` entry it is listed under is the one its header must match). `.json`,
   `.yml`, `.md`, `out_refs/` and `tools/` (throwaway spike scripts) are not mapped — they cannot
   carry a header, or are not this repo's shipped source.
5. **Ownership is exactly one wiki file per source file**, recorded in `sections:`. `covers` declares
   scope and may overlap between wiki files, so `sections:` is the authoritative mapping the test
   checks.
6. **When you edit a mapped file**, update the wiki section that documents it and bump `updated`.
   When you add a file, add it to `covers` + `sections` (and its header, if rule 3 applies). When you
   rename or move a file, update its `sections` entry and header together.
7. `tools/wiki/apply-headers.sh` stamps headers from `sections:`; `tools/wiki/wiki-check.sh` reports
   unmapped files, stale `updated` dates and header/anchor mismatches. Both are also enforced by
   `tests/SharpAudio.Api.Tests/WikiHeaderCoverageTests.cs`, which runs in CI.

## What this project is

SharpAudio is a .NET 10 REST API (OpenAI-compatible) for Text-to-Speech and Speech-to-Text (ASR),
with a Vue 3 frontend and Docker packaging. Each model runs inference in an isolated, disposable OS
worker process for memory isolation by default (spawned on demand, killed when idle), with an
in-process mode also available. Which task type(s) (`tts`/`asr`/`both`) a given running instance
serves is controlled by the `SERVER_MODE` env var (default `tts`, preserving pre-ASR behavior).

## How this doc is organized

| File | What's in it |
|---|---|
| [docs/wiki/backend-architecture.md](wiki/backend-architecture.md) | Request flow, worker-mode vs in-process mode, worker process lifecycle, gRPC contract (TTS), core abstractions (`ITtsSynthesizer`/`IModelCatalog`/`IModelCache`), model definition/`config.json` schema, TTS engines (Kokoro/Supertonic), TTS env var/endpoint tables, "Adding a new TTS engine" |
| [docs/wiki/asr-engines.md](wiki/asr-engines.md) | ASR domain model/abstractions, Whisper engine, Nemotron engine (incl. bugfix history), universal audio format support (ffmpeg), VAD support, ASR worker process/DI wiring, REST + streaming endpoints, live transcription frontend, known issues/roadmap, ASR env var table, "Adding a new ASR engine" |
| [docs/wiki/pnc.md](wiki/pnc.md) | PnC model (`1-800-BAD-CODE/punctuation_fullstop_truecase_english`), four heads, `ITokenizer` + unigram SentencePiece semantics, golden-test gate |
| [docs/wiki/frontend.md](wiki/frontend.md) | Vue app structure, `App.vue` state/behavior, component table, types, build/dev, capability discovery |
| [docs/wiki/worker-core.md](wiki/worker-core.md) | `SharpAudio.Core`: the shared library every task worker exe links against, project layout, shared `common.proto` health contract, worker bootstrap, service base, single-active engine pool, task descriptor, `SERVER_MODE` values, dependency graph |
| [docs/wiki/music.md](wiki/music.md) | Music generation (Stable Audio): project layout, `music.proto` contract, domain model, worker scaffold, worker process/DI wiring |
| [docs/wiki/diarization.md](wiki/diarization.md) | Speaker diarization (Sortformer v2): project layout, `diarization.proto` contract, domain model, worker scaffold, worker process/DI wiring, why it is its own executable |
| [docs/wiki/docker-packaging.md](wiki/docker-packaging.md) | Multi-stage `Dockerfile`, selectable image targets, known native-library/packaging fixes |
| [docs/wiki/testing.md](wiki/testing.md) | Unit/integration test layout, opt-in circular TTS→ASR tests, known `ModelCache` race-condition bugfix, latency benchmarks |

### Source file lookup (wiki file → anchors)

Use this to find which section to update for a file you're editing; the per-file `// wiki:` header
gives the same answer directly.

| Wiki file | Scope | Anchors |
|---|---|---|
| [backend-architecture.md](wiki/backend-architecture.md) | `src/SharpAudio.Api` TTS + core plumbing, `SharpAudio.Worker.Tts` | `request-flow`, `worker-mode-vs-in-process-mode`, `worker-process-lifecycle`, `grpc-contract`, `worker-entry-points`, `core-abstractions`, `model-definition-and-config`, `model-cache-and-warmup`, `engines`, `config-and-env-vars`, `endpoints`, `text-sanitization`, `adding-a-new-tts-engine`, `build-tooling-note` |
| [asr-engines.md](wiki/asr-engines.md) | ASR domain/engines/VAD, `SharpAudio.Worker.Asr`, `transcription.proto` | `domain-model-and-abstractions`, `whisper-engine`, `nemotron-engine`, `bugfix-history-nemotron`, `universal-audio-input-format-support`, `vad-support`, `worker-process-di-wiring-and-server-mode`, `rest-streaming-endpoints`, `vad-and-timestamp-segments`, `asr-env-var-table`, `adding-a-new-asr-engine` |
| [pnc.md](wiki/pnc.md) | `Services/Pnc`, `Models/PncModelDefinition.cs` | `model`, `tokenizer`, `onnx-graph-facts`, `engine`, `wiring` |
| [frontend.md](wiki/frontend.md) | `frontend/` | `app-vue`, `components`, `audio-worklets`, `types`, `build-dev`, `entry-point-and-presets`, `capability-discovery` |
| [docker-packaging.md](wiki/docker-packaging.md) | `Dockerfile`, `docker/`, root scripts | `image-stages`, `known-fixes`, `build-and-dev-scripts` |
| [worker-core.md](wiki/worker-core.md) | `src/SharpAudio.Core` | `core-project-layout`, `shared-grpc-contract`, `worker-bootstrap`, `worker-service-base`, `single-active-engine-pool`, `worker-task-descriptor`, `server-mode-values` |
| [music.md](wiki/music.md) | `src/SharpAudio.Worker.Music`, `Protos/music.proto`, music model/contract/options/service files | `project-layout`, `grpc-contract`, `domain-model-and-abstractions`, `worker-scaffold`, `worker-process-and-di-wiring` |
| [diarization.md](wiki/diarization.md) | `src/SharpAudio.Worker.Diar`, `Protos/diarization.proto`, diarization model/service/options files | `project-layout`, `grpc-contract`, `domain-model-and-abstractions`, `worker-scaffold`, `worker-process-and-di-wiring` |
| [testing.md](wiki/testing.md) | `tests/` | `unit-tests`, `integration-tests`, `running-tests`, `circular-tts-asr-model-tests`, `pnc-tokenizer-golden-test`, `modelcache-concurrent-download-race`, `benchmarks` |

For the ASR feature's phase-by-phase build log (what was done, why, and what's next), see
[docs/ASR_IMPLEMENTATION_PLAN.md](ASR_IMPLEMENTATION_PLAN.md) (condensed summary + status tracker)
and [docs/ASR_IMPLEMENTATION_HISTORY.md](ASR_IMPLEMENTATION_HISTORY.md) (full unabridged detail per
completed phase).

In-progress work (VAD/PnC from NeMo.cpp, Sortformer diarization, Stable Audio music) is tracked in
[docs/plans/FEATURES_PLAN.md](plans/FEATURES_PLAN.md) with the agent handoff log in
[docs/plans/FEATURES_MEMORY.md](plans/FEATURES_MEMORY.md).

## Quick architecture summary
```
HTTP request → Program.cs minimal API endpoint → IModelCatalog (validate model) →
IModelCache (ensure model files downloaded) → ITtsSynthesizer/IAsrTranscriber (do the work) → response
```
Worker mode (default, `WorkerOptions.Enabled`/`AsrWorkerOptions.Enabled` = `true`): a
`WorkerProcessManager` spawns/pools a separate OS process (`SharpAudio.Worker.Tts`/`SharpAudio.Worker.Asr`)
per model, communicating over gRPC. In-process mode: engines run as singletons in the API process
itself, routed by a `*Router` class, with idle engines released periodically. See
[docs/wiki/backend-architecture.md](wiki/backend-architecture.md) for the full detail.

## Build tooling note
No local `dotnet` CLI in this dev environment — use `./dotnet.sh <args>` (Docker-based wrapper) for
build/test/publish, e.g. `./dotnet.sh build SharpAudio.slnx`, `./dotnet.sh test
tests/SharpAudio.Api.Tests`.
