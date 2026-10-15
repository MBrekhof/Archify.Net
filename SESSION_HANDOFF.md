# Session handoff — Archify.Net

Updated 2026-10-15 (phase 3 closed).

## What this repo is

C# port of [tt-a1i/archify](https://github.com/tt-a1i/archify) 3.1.0 (upstream commit `c21c31ff`, local
checkout: repo root `C:\projects\archify`, skill package root `C:\projects\archify\archify`). Library first (`Archify.Net.Core`), then CLI, then MCP server. The reader-side
viewer stays JavaScript and is embedded verbatim. Correctness bar: rendered HTML byte-identical to upstream.

- Spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ Opus spec review next to it)
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (+ Opus plan review);
  phase-1 rulings: `docs/superpowers/plans/2026-10-13-phase1-rulings.md`
- Phase 2 plan: `docs/superpowers/plans/2026-10-14-phase2-renderers.md` (+ Opus plan review)
- Phase 3 plan: `docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md`
- Task state: **board-only**, ContextBoard project 46. Phase 1 is `PORT-001` (CARD-2166), phase 2 is `PORT-002`
  (CARD-2167), phase 3 is `PORT-003` (CARD-2168); phases 4 to 6 are `PORT-004` to `PORT-006`. No `TODO.md` here.
- `master`, remote `origin` = https://github.com/MBrekhof/Archify.Net (private, `gh` account `MBrekhof`).

## State at end of phase 3

- Phase 3 ran as 12 tasks: commits `b1b1539` through `962b18c`, plus the docs commit that updates README and this file.
- `ArchifyNet.Render` now covers every type except erd (`src/Archify.Net.Core/Archify.cs`). Erd throws
  `NotSupportedException` (phase 4).
- Added in phase 3: the dataflow compiler (`src/Archify.Net.Core/Dataflow`, authored-viewBox and automatic-showcase layouts,
  label capacity and joint label routing), the workflow compiler (`src/Archify.Net.Core/Workflow`, schema v1 and readable-v2
  automatic routing, typed input diagnostics with verified fixes, pins, route presets, conflict diagnostics, repair hints,
  brand marks), and `Js.WordBreaks` (UAX #29 word segmentation measured against V8).
- A workflow render returns the compiler receipt as `RenderResult.Layout` (upstream's `--layout-json`; phase 5's CLI needs it).
  Dataflow returns `null`.
- Parity: 38 phase-3 rows of `ParityTests.Render_is_byte_identical_to_upstream` (14 dataflow, 24 workflow), all
  byte-identical except `first-draft.mixed-port-recovery`, which differs only in one Han label's word break (pinned by its own
  fact). 24 workflow receipts equal upstream's `--layout-json`; 20 failure receipts (14 `wf-*`, 6 `df-*`) equal upstream's
  diagnostics. Fixtures against Node: `word-breaks` 1601 rows, `label-capacity` 24, `joint-label-routing` 9, `workflow-model` 220.
- `dotnet test` from the repo root: 2348 passed, 0 failed, no warnings.
- Task reviews were done by agy (Google Antigravity CLI, Gemini); implementers were Claude subagents. Fable hit its limit at
  Task 8, so Tasks 8 and 10 ran on opus.

## Known deviations

Listed in `README.md` under "Known deviations from upstream". Phases 2 and 3 add three, also restated in the XML remarks
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

Zone acceptance differs by host as well: ids the OS ICU list or `TimeZoneInfo` cannot resolve are rejected
(card TZ-001), and Windows without ICU accepts only UTC and fixed offsets (card TZ-002). Card GLOB-001 records that
the Js static constructor throws in a host with `InvariantGlobalization` on.

## Carried into later phases

- **`joinRoutePoints` identity memo: closed.** Upstream's memo (geometry.mjs:1327-1374) is unobservable in workflow and
  dataflow, and `GeometryTests.JoinRoutePoints_output_is_a_fixed_point_of_NormalizeRoutePoints` pins the property that makes it so
  on 6 hand-built shapes (the plan asked for 17 candidate shapes); the closure rests on no code comparing normalised arrays by identity.
- **`gateProfile` sites are review-checked only.** No phase-2 fixture makes a shared geometry gate fail, so a
  meta-only port would pass the same goldens. The phase-2 plan's Tasks 5 (sequence) and 7 (class) list the sites.
- **Erd entity width → phase 4.** `ErdGrid.ResolvedEntityWidth` takes a `GridEntity` only; the erd renderer needs
  `GridEntity.From(entity, 0)` there.
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

## Dev-time tooling

`tools/capture-fixtures.mjs` mints fixtures from upstream. It needs Node 24 and the upstream repo `C:\projects\archify` at
`c21c31ff`; it reads the skill package root `C:\projects\archify\archify`. CI never runs it; the test suite only reads what it committed.
The SDD ledgers (git-ignored, on disk) are `.superpowers/sdd/2026-10-14-phase2-renderers/progress.md` and
`.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/progress.md`: every ruling and the deferred minors.

## Next phase

`PORT-004`: the erd renderer, which reuses `ErdGrid`, and repository evidence.

## Phase 3 closed (2026-10-15)

- All 12 phase-3 tasks passed their agy reviews. The final whole-branch review and the push are the controller's.
- The phase-3 ledger with every ruling: `.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/progress.md`.
