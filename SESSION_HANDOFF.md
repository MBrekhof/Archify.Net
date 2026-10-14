# Session handoff — Archify.Net

Updated 2026-10-14 (end of phase 1).

## What this repo is

C# port of [tt-a1i/archify](https://github.com/tt-a1i/archify) 3.1.0 (upstream commit `c21c31ff`, local
checkout: repo root `C:\projects\archify`, skill package root `C:\projects\archify\archify`). Library first (`Archify.Net.Core`), then CLI, then MCP server. The reader-side
viewer stays JavaScript and is embedded verbatim. Correctness bar: rendered HTML byte-identical to upstream.

- Spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ Opus spec review next to it)
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (+ Opus plan review)
- Task state: **board-only**, ContextBoard project 46. Phase 1 is `PORT-001` (CARD-2166); phases 2 to 6 are
  `PORT-002` to `PORT-006`. No `TODO.md` here.

## State at end of phase 1

- Phase 1 ran as 11 tasks: commits `1f18dd7` through `f4aa31a`, plus the docs commit that adds README and this file.
- The facade is `ArchifyNet.Render`, `ArchifyNet.Check` and `ArchifyNet.Validate` in `src/Archify.Net.Core/Archify.cs`.
- Parity: 6/6 architecture goldens byte-identical; 20/20 checker reports equal upstream; 11 committed checker
  issue-path probes equal; option-level showcase on the no-profile fixture equals upstream's env-showcase output.
- The ajv-oracle schema battery (14 cases) and all Js fixtures pass.
- `Validate` maps checker failures with a port of upstream `checkerDiagnostics`. Its output equals upstream's on
  every failing checker probe, pinned by SHA-256 in `ParityTests`.
- `dotnet test` from the repo root at commit `a02d62b`: 217 passed, 0 failed, no warnings.
- `master`, no remote yet.

## Known deviations

Listed in `README.md` under "Known deviations from upstream". The CJK word-wrap exemption is not yet reachable.
`Render` throws `NotSupportedException` for the nine types that land in phases 2 to 4.

## Dev-time tooling

`tools/capture-fixtures.mjs` mints fixtures from upstream. It needs Node 24 and the upstream repo `C:\projects\archify` at
`c21c31ff`; it reads the skill package root `C:\projects\archify\archify`. CI never runs it; the test suite only reads what it committed.

## Next phase

`PORT-002`: erd/grid, sequence, lifecycle, class, tree, timeline and waterfall renderers. Plan:
`docs/superpowers/plans/2026-10-14-phase2-renderers.md`, with its Opus review folded in and committed next to it
(`2a56657`).

## Resume point (2026-10-14 19:00, session restarted mid-PORT-002)

- PORT-001 (phase 1) is in Review on the board; final state commit `23de573` + rulings doc `bbc8aa1` (228 tests then).
- PORT-002 (phase 2) is in progress on `master`: Tasks 1-8 complete and reviewed; Task 9 (timeline) implemented at
  `f798a38` (4/4 goldens, 7 receipts, layout-json 4/4, 336 tests) and **awaiting its task review**. Tasks 10 (waterfall) and
  11 (docs/handoff) remain. Plan: `docs/superpowers/plans/2026-10-14-phase2-renderers.md`.
- SDD ledger (git-ignored, on disk): `.superpowers/sdd/2026-10-14-phase2-renderers/progress.md` — every ruling,
  deferred minor and carried item; briefs `task-N-brief.md`, reports `task-N-report.md`, reviews `task-N-review.md`,
  review package for Task 9 already built: `review-ce02c99..f798a38.code.diff`. Resume = dispatch the Task 9 review.
- Reviewer change requested by Martin: Google Antigravity CLI (`agy`) replaces the Opus task reviewer. Not usable yet
  at restart: `agy` is not on PATH in Claude's shells and its log said "You are not logged into Antigravity"; the npm
  `gemini` 0.64 CLI refuses (individual tier deprecated). Needed: Martin logs in, `agy --help` shows the headless flags,
  and Martin says whether it takes over from Task 9's review or from the phase-2 final review.
- Cards minted this phase: TZ-001 (CARD-2172, ICU ids .NET cannot resolve), TZ-002 (CARD-2173, Windows without ICU),
  GLOB-001 (CARD-2174, Js static ctor under InvariantGlobalization).
