# Session handoff — Archify.Net

Updated 2026-10-14 (end of phase 1).

## What this repo is

C# port of [tt-a1i/archify](https://github.com/tt-a1i/archify) 3.1.0 (upstream commit `c21c31ff`, local
checkout `C:\projects\archify`). Library first (`Archify.Net.Core`), then CLI, then MCP server. The reader-side
viewer stays JavaScript and is embedded verbatim. Correctness bar: rendered HTML byte-identical to upstream.

- Spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ Opus spec review next to it)
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (+ Opus plan review)
- Task state: **board-only**, ContextBoard project 46. Phase 1 is `PORT-001` (CARD-2166); phases 2 to 6 are
  `PORT-002` to `PORT-006`. No `TODO.md` here.

## State at end of phase 1

- Phase 1 ran as 11 tasks: commits `1f18dd7` through `f4aa31a`, plus the docs commit that adds README and this file.
- The facade is `Archify.Render`, `Archify.Check` and `Archify.Validate` in `src/Archify.Net.Core/Archify.cs`.
- Parity: 6/6 architecture goldens byte-identical; 20/20 checker reports equal upstream; 11 committed checker
  issue-path probes equal; option-level showcase on the no-profile fixture equals upstream's env-showcase output.
- The ajv-oracle schema battery (14 cases) and all Js fixtures pass.
- `Validate` maps checker failures with a port of upstream `checkerDiagnostics`. Its output equals upstream's on
  every failing checker probe, pinned by SHA-256 in `ParityTests`.
- `dotnet test` from the repo root: 217 passed, 0 failed, no warnings.
- `master`, no remote yet.

## Known deviations

Listed in `README.md` under "Known deviations from upstream". The CJK word-wrap exemption is not yet reachable.
`Render` throws `NotSupportedException` for the nine types that land in phases 2 to 4.

## Dev-time tooling

`tools/capture-fixtures.mjs` mints fixtures from upstream. It needs Node 24 and `C:\projects\archify` at
`c21c31ff`. CI never runs it; the test suite only reads what it committed.

## Next phase

`PORT-002`: erd/grid, sequence, lifecycle, class, tree, timeline and waterfall renderers. Plan:
`docs/superpowers/plans/2026-10-14-phase2-renderers.md`, with its Opus review next to it. The plan is being
revised after that review and is not committed yet.
