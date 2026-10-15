# Session handoff — Archify.Net

Updated 2026-10-15 (phase 4 closed).

## What this repo is

C# port of [tt-a1i/archify](https://github.com/tt-a1i/archify) 3.1.0 (upstream commit `c21c31ff`, local
checkout: repo root `C:\projects\archify`, skill package root `C:\projects\archify\archify`). Library first (`Archify.Net.Core`), then CLI, then MCP server. The reader-side
viewer stays JavaScript and is embedded verbatim. Correctness bar: rendered HTML byte-identical to upstream.

- Spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ Opus spec review next to it)
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (+ Opus plan review);
  phase-1 rulings: `docs/superpowers/plans/2026-10-13-phase1-rulings.md`
- Phase 2 plan: `docs/superpowers/plans/2026-10-14-phase2-renderers.md` (+ Opus plan review)
- Phase 3 plan: `docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md`
- Phase 4 plan: `docs/superpowers/plans/2026-10-15-phase4-erd-evidence.md`
- Task state: **board-only**, ContextBoard project 46. Phase 1 is `PORT-001` (CARD-2166), phase 2 is `PORT-002`
  (CARD-2167), phase 3 is `PORT-003` (CARD-2168), phase 4 is `PORT-004` (CARD-2169); phases 5 and 6 are `PORT-005` and `PORT-006`. No `TODO.md` here.
- `master`, remote `origin` = https://github.com/MBrekhof/Archify.Net (private, `gh` account `MBrekhof`).

## State at end of phase 4

- Phase 4 ran as 7 tasks: commits `17a8750` through `884f358`, plus the docs commit that updates README and this file.
- `ArchifyNet.Render` now covers all ten types (`src/Archify.Net.Core/Archify.cs`); `Renderers.For` has no throwing arm. Only
  `Check` with `RequireProvenance` still throws `NotSupportedException` (phase 5).
- Added in phase 4: the erd renderer (`src/Archify.Net.Core/Erd`, reusing `ErdGrid`), `RepositoryLocation` and the Js URI helpers
  (Node-pinned), and repository source evidence via git (`RenderOptions.RepoRoot`, `RepositoryEvidence.Verify` in the load gates,
  git on PATH).
- Parity: 16 erd goldens byte-identical, 16 erd layout reports and 11 erd failure receipts equal upstream's; 25 evidence scenarios
  plus the git-absent receipt equal upstream's, 13 evidence render rows byte-identical, `RepositoryLocation` 211 fixture rows, Js URI 71.
  The fixture repo is a git fast-import stream (`tests/fixtures/evidence/evidence-repo.fi`) with a pinned HEAD.
- `dotnet test` from the repo root: 2753 passed, 0 failed, no warnings.
- Task reviews were done by agy (Gemini); implementers were Claude subagents (opus, Task 7 sonnet). Fable was unavailable.

## Known deviations

Listed in `README.md` under "Known deviations from upstream". Phases 2 to 4 add the items below, also restated in the XML remarks
of `ArchifyNet.Render`:

- Word breaks: `Js.WordBreaks` matches V8's `Intl.Segmenter` word granularity except that Han, Hiragana, Katakana, Thai,
  Lao, Khmer and Myanmar runs split per grapheme (ICU uses a dictionary; visible in sequence notes and wrapped
  automatic-showcase dataflow labels); V8 splits inside grapheme clusters the port keeps whole (a Prepend such as U+0600 before a space,
  a jamo next to a Hangul syllable) while the port splits across clusters V8 joins (a ZWJ before a pictograph after a letter,
  UAX #29 WB3c: `"a‍😀"` is one word in V8, two in the port; a Format/Prepend-led cluster after a letter: `"a؀b"`); and a non-POSIX host locale is assumed (under
  `LANG=C` V8 splits `api.example.com` at the dots).
- Time zones use the OS zone table: base offset before a zone's first transition (`Asia/Shanghai` at year 1 is
  `UTC+08:00`, upstream `UTC+08:06`), and the first adjustment rule extrapolated backwards (`Pacific/Chatham`,
  `Australia/Lord_Howe` in 1970).
- `Date.parse` outside the timeline schema's timestamp shape is NaN; the schema gate makes it unreachable.
- erd: an entity with neither `row`/`col` nor `pos` whose relationships carry labels fails with `internal/unclassified` as upstream
  does, but the evidence names the .NET exception type where upstream names the JS `TypeError`.
- Evidence: `repository-evidence/root-identity-indeterminate` is unreachable (root identity via `git rev-parse --show-prefix`).
- Evidence: Node's spawn error strings are synthesised (`spawnSync git ENOENT`, `spawnSync git ENOBUFS`); an errno outside the
  port's table prints its number.
- Evidence: the `root-unreadable` message names the whole requested root where Node names the first missing path component.
- Evidence: three non-ASCII hosts WHATWG URL accepts are rejected (`IdnMapping` hyphen and label-length rules): a label starting
  or ending with `-` after IDNA, and a label longer than 63 octets.
- Evidence tests: the `subdirectory-root` receipt is Windows-only (path separator).

Zone acceptance differs by host as well: ids the OS ICU list or `TimeZoneInfo` cannot resolve are rejected
(card TZ-001), and Windows without ICU accepts only UTC and fixed offsets (card TZ-002). Card GLOB-001 records that
the Js static constructor throws in a host with `InvariantGlobalization` on.

## Carried into later phases

- **`joinRoutePoints` identity memo: closed.** Upstream's memo (geometry.mjs:1327-1374) is unobservable in workflow and
  dataflow, and `GeometryTests.JoinRoutePoints_output_is_a_fixed_point_of_NormalizeRoutePoints` pins the property that makes it so
  on 6 hand-built shapes (the plan asked for 17 candidate shapes); the closure rests on no code comparing normalised arrays by identity.
- **`gateProfile` sites are review-checked only.** No phase-2 fixture makes a shared geometry gate fail, so a
  meta-only port would pass the same goldens. The phase-2 plan's Tasks 5 (sequence) and 7 (class) list the sites.
- **Erd entity width: closed.** `ErdGrid.ResolvedEntityWidth` takes a `GridEntity` only; the erd renderer uses
  `GridEntity.From(entity, 0)` there, and 16 erd goldens and layout reports confirm it.
- **Phase-3 coverage gaps (card TEST-002):**
  - Task 8: route-order retries and the sizing loop ARE pinned (`route-text-recovery`: 2 route-order retries + 1 sizing recompile
    in its top-level compile; golden and receipt byte-identical). Still reached by no workflow row: layout-feedback rounds,
    internal-detour plans and the tie-break offsets (`pairOrdinal*9`, outside-right growth), plus the pin-conflict throwers and
    repair hints listed below.
  - Task 10: most H1/H5 pin-conflict throwers (label-route, label-label, H2 crossing/corridor, `labelAt`, negative pins,
    pinned-route checks, placed-label/rhythm evidence, authored-side failure) have no Node receipt; they are verified by reading only.
  - Task 11: `addVerifiedCrossingRepair` and `addVerifiedPlacementRepair` are reached by no captured input (no route-order
    code in any fixture).
- **Non-empty `Layout.Report.diagnostics` is unpinned (phase 5).** No fixture pins its JSON form (all success receipts have `[]`;
  `WorkflowRenderer.cs:27-32` rebuilds six keys, never `suppresses`). Upstream's failure receipt order is `{…, diagnostics, ok:false}`
  while the port's failure receipt (`WorkflowCompilation.Svg.cs:487-489`) has no `diagnostics` key: phase 5's `--layout-json` must
  follow upstream's order (card TEST-002 gains a standard-profile warning fixture).

- **Phase-4 coverage gaps (card TEST-003, the controller mints it):**
  - Task 2: the erd trunk snap, the trunk bridge (`renderers/erd` :893-895) and the coordinate-undefined delete (:750-752) are
    reached by no input.
  - Task 3: erd validation branches reached by no fixture: duplicate attribute, non-finite width, entity overlap, unknown
    source/target endpoint, too short, both references messages, label over entity.
  - Task 5: the per-file fallback after a failed `cat-file` batch, and `object-unavailable` (partial clone), are ported and untested.

## Dev-time tooling

`tools/capture-fixtures.mjs` mints fixtures from upstream. It needs Node 24 and the upstream repo `C:\projects\archify` at
`c21c31ff`; it reads the skill package root `C:\projects\archify\archify`. CI never runs it; the test suite only reads what it committed.
The SDD ledgers (git-ignored, on disk) are `.superpowers/sdd/2026-10-14-phase2-renderers/progress.md` and
`.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/progress.md` and
`.superpowers/sdd/2026-10-15-phase4-erd-evidence/progress.md`: every ruling and the deferred minors.

## Next phase

`PORT-005`: the CLI, receipts, sidecars, lock and journal, and the Playwright browser and visual gates.

## Phase 4 closed (2026-10-15)

- All 7 phase-4 tasks passed their agy reviews. The final whole-branch review and the push are the controller's.
- The phase-4 ledger with every ruling: `.superpowers/sdd/2026-10-15-phase4-erd-evidence/progress.md`.
