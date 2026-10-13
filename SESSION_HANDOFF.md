# Session handoff — Archify.Net

Updated 2026-10-13 (planning session; no code yet).

## What this repo is

C# port of [tt-a1i/archify](https://github.com/tt-a1i/archify) 3.1.0 (upstream commit `c21c31ff`, local
checkout `C:\projects\archify`). Library first (`Archify.Net.Core`), then CLI, then MCP server. The reader-side
viewer stays JavaScript and is embedded verbatim. Correctness bar: rendered HTML byte-identical to upstream.

- Spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ Opus spec review next to it)
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (+ Opus plan review)
- Task state: **board-only** — ContextBoard project id 46 (created as `arc#hify`, to be renamed `Archify.Net` in
  the UI). Cards `PORT-001`…`PORT-006` = CARD-2166…2171, dependency-chained, all unclaimed. No `TODO.md` here.

## Pending from Martin (settled in chat 2026-10-13 unless noted)

- Rename the folder before the next session: PowerShell, `Rename-Item -Path "C:\projects\arc#hify" -NewName
  "Archify.Net"`. Plan Task 1 Step 0 refuses to run while the path contains `#`. The auto-memory directory key
  changes with the path, so nothing was written to memory this session — this file is the carry-over.
- Rename board project #46 in the ContextBoard UI (no MCP tool for it).
- **Execution method for PORT-001 not yet chosen**: subagent-driven (recommended in chat) or native.

## Decisions taken (all in the spec)

Library first; viewer stays JS; strict byte parity (CJK word-wrap exempt); cuts: atomic-output hardening,
update-awareness, hand-rolled CDP (→ Playwright), remote brand capture (built-in 107 marks only), CLI commands
compare/brands/migrate/preview. After the plan review (applied as my recommendation, Martin informed):
in-house JSON Schema 2020-12 evaluator with an ajv oracle instead of JsonSchema.Net (OSMF fee EULA + four
semantic divergences); library output-path check is lexical, `path-semantics`/`sidecar-path` → phase 5.
Reviewer: Codex unavailable → Claude Opus subagent reviews spec, plan, and each phase diff.

## Next step

Martin picks the execution method → execute PORT-001 per the plan (`superpowers:subagent-driven-development`
or `superpowers:executing-plans`). First concrete action in the renamed repo is Task 1 Step 1 (`.gitattributes`,
`-text` lines last) before any upstream byte is copied.

## Git

`master`, 7 docs-only commits, no remote yet. Latest: `ca9d77f`.
