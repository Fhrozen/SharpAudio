---
description: Wiki header convention for mapped source files
applyTo: "{src,frontend,tests}/**,docs/wiki/**"
---

# Wiki header rule

Every mapped source file in this repo carries a one-line wiki header as its first line (after a
shebang, if any), naming the `docs/wiki/*.md` file and section that documents it. Full convention:
`docs/LLM_WIKI.md` ("Wiki headers (agent convention)").

When you create, rename, move, or edit a mapped file:

1. Keep the header in sync with the wiki `sections:` mapping. If the file moves to a different
   section, update the header and the `sections:` entry together.
2. A new mapped file must be added to that wiki file's `covers` and `sections`, and gets a header if
   its folder holds more than one mapped file (single-file folders are mapped by `sections:` alone).
   `Program.cs` is the exception and always gets a header.
3. Update the wiki section that documents the change and bump that wiki file's `updated` date.
4. The header is a machine-readable mapping, not a comment about the code. It is the one exception to
   the "one short line, only what code cannot show" rule, and it must stay exactly one line.

Header syntax by type:

- `.cs`, `.proto`, `.ts`, `.js`: `// wiki: <slug>#<anchor>`
- `.vue`, `.csproj`: `<!-- wiki: <slug>#<anchor> -->`
- `Dockerfile`, `.sh`, `.ps1`: `# wiki: <slug>#<anchor>`

Do not add headers to `.json`, `.yml`, `.md`, `out_refs/` or `tools/`.

Anchor slugs are the heading text, lowercased, with runs of non-alphanumeric characters replaced by a
single hyphen (`## VAD support` -> `vad-support`). Every slug in `sections:` must be an actual heading
in that wiki file.

Regenerate all headers with `bash tools/wiki/apply-headers.sh`; verify with
`bash tools/wiki/wiki-check.sh` (or `./tests/run-tests.sh wiki`). The same checks run in CI as
`tests/SharpAudio.Api.Tests/WikiHeaderCoverageTests.cs`.
