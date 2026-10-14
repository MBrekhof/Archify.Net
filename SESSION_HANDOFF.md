# Session handoff — Archify.Net

Updated 2026-10-14 (end of phase 2).

## What this repo is

C# port of [tt-a1i/archify](https://github.com/tt-a1i/archify) 3.1.0 (upstream commit `c21c31ff`, local
checkout: repo root `C:\projects\archify`, skill package root `C:\projects\archify\archify`). Library first (`Archify.Net.Core`), then CLI, then MCP server. The reader-side
viewer stays JavaScript and is embedded verbatim. Correctness bar: rendered HTML byte-identical to upstream.

- Spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ Opus spec review next to it)
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (+ Opus plan review);
  phase-1 rulings: `docs/superpowers/plans/2026-10-13-phase1-rulings.md`
- Phase 2 plan: `docs/superpowers/plans/2026-10-14-phase2-renderers.md` (+ Opus plan review)
- Task state: **board-only**, ContextBoard project 46. Phase 1 is `PORT-001` (CARD-2166), phase 2 is `PORT-002`
  (CARD-2167); phases 3 to 6 are `PORT-003` to `PORT-006`. No `TODO.md` here.
- `master`, no remote yet.

## State at end of phase 2

- Phase 2 ran as 11 tasks: commits `d6c41e7` through `43d9c40`, plus the docs commit that updates README and this
  file. The phase-1 facade rename `23de573` and rulings doc `bbc8aa1` land in between.
- `ArchifyNet.Render` now covers architecture, sequence, lifecycle, class, tree, timeline and waterfall
  (`src/Archify.Net.Core/Archify.cs`). Workflow, dataflow and erd throw `NotSupportedException` (phases 3 and 4).
- Shared in phase 2: the erd banded grid (`src/Archify.Net.Core/Erd/Grid.cs`, used by class, reused by erd in
  phase 4), the Js text helpers (graphemes, Han tokenizers, splits) and the Js time layer (`Js/JsTime.cs`).
- Parity, 20 phase-2 rows of `ParityTests.Render_is_byte_identical_to_upstream`, all byte-identical:
  - 10 upstream goldens: sequence and lifecycle 1 each; tree, class, timeline and waterfall 2 each.
  - 2 Node-minted example goldens: `async-job-roundtrip`, `deployment-release`.
  - 8 fixture goldens: `note-wrap`, `brand-participants`, `deployment-release.notes`, `payment-platform.right`,
    `payment-platform.wrap` (added in Task 8 for the CJK/Hangul and in-word cut branches), `dst-new-york`,
    `payment-incident.zh-CN`, `incomplete-trace`.
- Checker: the 10 new goldens each have `tests/goldens/checker/<name>.json`; `ArtifactCheckerTests` enumerates
  all 30 and every report equals upstream's.
- Failure receipts equal upstream's diagnostics: sequence 8, lifecycle 9, class 7, tree 5, timeline 7, waterfall 6.
  Layout reports equal upstream's `--layout-json` for class 2, tree 3, timeline 4, waterfall 3.
- Fixtures under `tests/fixtures/js`: `segmenter.json`, `text-wrap.json`, `dates.json`, `time-zones.json`,
  `waterfall-number.json`, `erd-grid.json` (`JsTextTests` 9, `JsTimeTests` 10, `ErdGridTests` 5, all green).
  Diagram fixtures, receipts and layout JSONs are under `tests/fixtures/diagrams`.
- Pinned facts: one renderer-level diagnostic code per type, the nine golden.mjs schema cases, the Windows zone
  id rejection, historical years in UTC, both 2026 `America/New_York` DST transitions (repeated 01:30 hour included).
- `dotnet test` from the repo root at commit `43d9c40`: 350 passed, 0 failed, no warnings.
- Task reviews from Task 9 on were done by agy (Google Antigravity CLI, Gemini) instead of Opus.

## Known deviations

Listed in `README.md` under "Known deviations from upstream". Phase 2 adds three, also restated in the XML remarks
of `ArchifyNet.Render`:

- CJK word breaks in sequence notes: Han runs break per grapheme, not by ICU's word dictionary. Only a sequence
  `note` containing Han is affected.
- Time zones use the OS zone table: base offset before a zone's first transition (`Asia/Shanghai` at year 1 is
  `UTC+08:00`, upstream `UTC+08:06`), and the first adjustment rule extrapolated backwards (`Pacific/Chatham`,
  `Australia/Lord_Howe` in 1970).
- `Date.parse` outside the timeline schema's timestamp shape is NaN; the schema gate makes it unreachable.

Zone acceptance differs by host as well: ids the OS ICU list or `TimeZoneInfo` cannot resolve are rejected
(card TZ-001), and Windows without ICU accepts only UTC and fixed offsets (card TZ-002). Card GLOB-001 records that
the Js static constructor throws in a host with `InvariantGlobalization` on.

## Carried into later phases

- **`joinRoutePoints` identity memo → phase 3.** Its only callers are in `renderers/workflow/workflow-compiler.mjs`
  (lines 39, 2267, 3892, 4022, 4240, 4621, 4779, 4802). No phase-2 renderer calls it, so the memo is unobservable
  until workflow lands. The phase-3 plan ports it with a fixture.
- **`gateProfile` sites are review-checked only.** No phase-2 fixture makes a shared geometry gate fail, so a
  meta-only port would pass the same goldens. The phase-2 plan's Tasks 5 (sequence) and 7 (class) list the sites.
- **Erd entity width → phase 4.** `ErdGrid.ResolvedEntityWidth` takes a `GridEntity` only; the erd renderer needs
  `GridEntity.From(entity, 0)` there.

## Dev-time tooling

`tools/capture-fixtures.mjs` mints fixtures from upstream. It needs Node 24 and the upstream repo `C:\projects\archify` at
`c21c31ff`; it reads the skill package root `C:\projects\archify\archify`. CI never runs it; the test suite only reads what it committed.
The SDD ledger for phase 2 (git-ignored, on disk) is `.superpowers/sdd/2026-10-14-phase2-renderers/progress.md`:
every ruling and the deferred minors that go to the phase-2 final review.

## Next phase

`PORT-003`: workflow and dataflow renderers, including the `joinRoutePoints` memo above. `PORT-004`: the erd renderer,
which reuses `ErdGrid`, and repository evidence.
