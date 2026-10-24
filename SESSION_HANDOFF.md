# Session handoff — Archify.Net

Updated 2026-10-24: PORT-009 (the explainer type) is implemented on branch `port-009-explainer` (Tasks 1 to 9) and waits
for the controller's close-out: upstream issues, board cards, the Fable whole-branch review, the merge of master and the push,
and the Pages URL checks. PORT-008 was merged before it. Then PORT-013 (the parked PORT-009 items) and PORT-012.
Martin's standing order for the arc: each branch Fable-reviewed (plan and branch), then merge and push. No deploy.

## What this repo is

C# port of [tt-a1i/archify](https://github.com/tt-a1i/archify) 3.2.0 (upstream tag `v3.2.0`, commit `208acba8`, local
checkout: repo root `C:\projects\archify`, skill package root `C:\projects\archify\archify`). Library first (`Archify.Net.Core`), then CLI, then MCP server. The reader-side
viewer stays JavaScript and is embedded verbatim. Correctness bar: rendered HTML byte-identical to upstream.

- Spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ Opus spec review next to it)
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (+ Opus plan review);
  phase-1 rulings: `docs/superpowers/plans/2026-10-13-phase1-rulings.md`
- Phase 2 plan: `docs/superpowers/plans/2026-10-14-phase2-renderers.md` (+ Opus plan review)
- Phase 3 plan: `docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md`
- Phase 4 plan: `docs/superpowers/plans/2026-10-15-phase4-erd-evidence.md`
- Phase 5 plan: `docs/superpowers/plans/2026-10-15-phase5-cli.md`
- Phase 6 plan: `docs/superpowers/plans/2026-10-15-phase6-mcp.md`
- Task state: **board-only**, ContextBoard project 46. Phase 1 is `PORT-001` (CARD-2166), phase 2 is `PORT-002`
  (CARD-2167), phase 3 is `PORT-003` (CARD-2168), phase 4 is `PORT-004` (CARD-2169); phases 5 and 6 are `PORT-005` and `PORT-006`. No `TODO.md` here.
- `master`, remote `origin` = https://github.com/MBrekhof/Archify.Net (private, `gh` account `MBrekhof`).

## State: PORT-007 (2026-10-22/23)

- Card PORT-007 (CARD-2357). Plan: `docs/superpowers/plans/2026-10-22-port007-upstream-320.md`. Ledger with every ruling:
  `.superpowers/sdd/2026-10-22-port007-upstream-320/progress.md`.
- Branch `port-007-upstream-320`, commits from `a8d8cd5` (plan) through `4c8d38a` (branch-review fixes) and the handoff after it. The tasks:
  - re-pin and re-mint;
  - timeline DST ticks and ms labels;
  - waterfall `timeNumber` and axis labels;
  - erd trunk bridges;
  - migration `semanticChecks` and git-value trim;
  - CLI `browserCheck`, `--browser-check` and `--writing`;
  - docs;
  - two waves of review minors.
- Test totals at `4c8d38a` (`dotnet test`, Browser category included): **3993 passed** (Core 3016, Cli 964, Mcp 13),
  0 failed, 0 warnings.
- Reviews:
  - Tasks 2–3 were reviewed by agy (Gemini). Tasks 4–6 were reviewed by Claude subagents after the Gemini quota ran out.
    That fallback was Martin's choice, 2026-10-22.
  - A Claude whole-branch review gave "with fixes": 1 Important, 7 Minor, all fixed in `4c8d38a`.
  - The Fable merge gate (2026-10-23) said "Ready to merge: Yes", 0 Critical/Important, 2 Minor, both fixed before the merge.
- The explainer type and its files are excluded until PORT-009 (see `docs/deviations.md`, `upstream/UPSTREAM.md`).
  `skill/archify/SKILL.md` keeps its 3.1.0 wording apart from the `--out-dir` sentences; PORT-009 re-derives it.

## State: PORT-008 (2026-10-23/24)

- Card PORT-008 (CARD-2358). Research: `docs/superpowers/specs/2026-10-23-port008-pele-research.md`. Plan:
  `docs/superpowers/plans/2026-10-23-port008-pele-charts.md`.
- Branch `port-008-pele-charts`, cut from master `09b4424`. Commits from `8e59b93` (plan) through `2c68fd8` (final
  wave), plus the docs commit after it. The work:
  - V8-identical `sin/cos/acos/atan2/log10` and `parseInt` (`7e0ef94`); `pow` uses a `10 ** p` table from Windows Node
    (Martin's ruling, option C);
  - the chart fixture pipeline and `chartFromData` (`57ecc7f`), the Pele core (`84a09be`) and front-matter YAML
    (`8b56183`);
  - one task per type: pie, xychart, quadrantChart, sankey, radar, venn, each with its review rounds;
  - the error-line remap for every type (`5f61449`, `57787fc`);
  - the final wave, part A (`2c68fd8`): XML-invalid characters, the pie message placeholder leak, the radar overflow.
- Parity at `2c68fd8`: ChartParity 2226/2226. Test totals (`dotnet test`, Browser category included): Core 5288,
  Cli 964, Mcp 13, 0 failed, 0 warnings. Per-group counts are in `docs/status.md`.
- Fixed upstream defects, each a stated deviation with a test and an upstream issue: `docs/deviations.md`, "Pele charts
  (PORT-008)".
- Martin (2026-10-24): contrived-input ("fails late") findings now get a card and are parked, not fixed in more rounds.
  Wrong output on realistic input is still fixed. The final wave's parts B and C went to PORT-012.
- Pele upstream is at 1.9.0. A future archify re-pin of Pele is its own card.

## State: PORT-009 (2026-10-24)

- Plan: `docs/superpowers/plans/2026-10-24-port009-explainer.md`. SDD ledger and every task report:
  `.superpowers/sdd/2026-10-24-port009-explainer/` (Task 9: `task-9-report.md`).
- Branch `port-009-explainer`, cut from master `ddc86b3`; nine tasks: Core (parser, validator, preparation, renderer, checker,
  patch), CLI (`validate`, `deliver`, `render`, `check`, `patch`, `finalize`), the explainer browser gate, then Task 9: the
  `archify_patch` tool, `writing` and `browserCheck` parameters, SKILL.md re-derived from 3.2.0, README, status, deviations and
  the showcase.
- Showcase: `showcase/explainer/` (four drafts, four pages, `index.html`), committed without `.delivery.json` sidecars.
  Pages serve from `master`, so the URLs answer 200 only after the merge and push (https://mbrekhof.github.io/Archify.Net/showcase/explainer/).
- Test totals (`dotnet test`, Browser category included): Core 6334, Cli 1034, Mcp 16, 0 failed, 0 warnings.
- Decisions (Martin, 2026-10-24): gzip payload is .NET's stream; branch lane label `Branch N` for non-zh locales (filed
  upstream); default locale `en` (no issue); the explainer browser gate is compared by verdicts only.
- Open: `patch` embeds a relative path to the deleted temporary `patched-draft.json` (mirrored from upstream; Martin's decision
  pending). `viewer-target` is a stated platform difference of the headless shell, not an upstream defect. Parked items: card
  PORT-013 (CARD-2523), listed in `docs/deviations.md`.

## Next

1. PORT-009 close-out (controller): upstream issues (lane label, heading line terminators, patch payload message), board
   cards, the Fable whole-branch review, settle it, fast-forward master, push, check the showcase URLs.
2. PORT-013: the parked PORT-009 cases. PORT-012: the PORT-008 test and tooling minors and parked cases.
3. Queued after PORT-009 (Martin, 2026-10-23, "fix, report as with the previous one"): UPSTREAM-002 (CARD-2182) and
   PERF-003 (CARD-2186), each fixed on its own branch as a known deviation with a test.

## State at end of phase 6

- Phase 6 (`PORT-006`) status: **Done**. Claude ran the exit demonstration headless in Claude Code against the packed tools; the record is
  `.superpowers/sdd/2026-10-15-phase6-mcp/task-6-exit-report.md`.
  - Run 1: a `claude -p` session with `--mcp-config` called `archify_render` on `web-app.architecture.json`. The HTML is
    byte-identical to `upstream/examples/web-app-rendered.html`.
  - Run 2: a fresh session that followed `skill/archify/SKILL.md` wrote a leave-approval workflow and called
    `archify_finalize` once. Every gate passed at showcase, with 0 diagnostics.
  - `src/Archify.Net.Mcp` (`archify-mcp` dotnet tool, `ModelContextProtocol` 2.2.0) exposes the 4 tools `archify_guide`,
    `archify_validate`, `archify_render` and `archify_finalize` in-process over `Archify.Net.Cli`.
  - `skill/archify/` is the self-contained agent skill package (`SKILL.md` with `## Archify.Net differences`, the 6 supported
    `references/*.md` files with `node bin/archify.mjs` replaced by `archify`, the 12 `schemas/*` files and the 32
    `examples/**/*.json` files).
  - `tests/Archify.Net.Mcp.Tests` tests the 4-tool catalog, `archify_guide`, `archify_validate` (including `--layout-json`),
    `archify_render`, `archify_finalize` (real Chromium and `NoChromium`), the `StdioClientTransport` subprocess smoke test and
    `SkillPackageTests`.
- Phase 6 commits (from `git log`):
  - `90327f0 docs: phase-6 plan (MCP server + skill), written by agy, Claude-reviewed`
  - `17abea8 feat(mcp): Archify.Net.Mcp server with the 4-tool catalog (validate, render, finalize, guide)`
  - `85f158f test(mcp): end-to-end render, finalize (Chromium and no-Chromium) and stdio subprocess tests`
  - `8ec3691 feat(skill): self-contained skill/archify package (SKILL.md, references, schemas, examples) with parity tests`
  - `11d20ea docs: phase 6 — MCP server and skill (exit demonstration pending)`
  - plus the docs commit that records the exit demonstration.
- Test totals at the end of the 2026-10-15 cleanup (`dotnet test` from the repo root, including the `Browser` category):
  **3885 passed, 0 failed, 0 skipped, 0 warnings**. Phase 6 itself closed at 3727.
  - `Archify.Net.Core.Tests`: 2937 passed
  - `Archify.Net.Cli.Tests`: 935 passed
  - `Archify.Net.Mcp.Tests`: 13 passed
  - `dotnet test --filter Category!=Browser` skips the Chromium-backed tests when running without a browser.
- Implementer: Gemini (agy); task reviews by Claude Opus 5.5. SDD workspace: `.superpowers/sdd/2026-10-15-phase6-mcp/`.

## State at end of phase 5

- Phase 5 (`PORT-005`) is done: the `archify` dotnet tool, `src/Archify.Net.Cli` (13 commands: render, deliver, finalize, validate,
  inspect, check, browser-check, visual-check, guide, examples, doctor, install-browser, demo). Tests: `tests/Archify.Net.Cli.Tests`.
  Commits `7eb87b6` through `568906c` (Tasks 1 to 12), plus the docs commit that updates README and this file.
- `dotnet test` from the repo root after the final fix wave: 3714 passed, 0 failed, no warnings (Core 2782, Cli 932; before the
  wave 3681 = 2772 + 909). The Cli total includes the `Browser` category (needs
  `archify install-browser`, about 2.5 min in all); `dotnet test --filter Category!=Browser` skips it.
- Exit criterion: `finalize` on all 25 upstream examples at both quality profiles (50 runs) gives the same outcome in C# as in Node.
  21 of 25 pass at each quality; upstream itself fails `async-job-roundtrip`, `event-stream`, `incident-response` and
  `release-delivery` (at standard in browser-check, `viewer/viewport-overflow`; at showcase in deliver, `composition/viewport-height`).
  Tracked as card UPSTREAM-001 (CARD-2181).
- Packaging: `dotnet pack` failed at first because warnings-as-errors turned NU5111 (Microsoft.Playwright's `.ps1` helpers) into an
  error; fixed with `<NoWarn>NU5111</NoWarn>` in `Archify.Net.Cli.csproj`. Then pack, `dotnet tool install --tool-path`, `--help`,
  `demo`, `doctor` and `validate` all ran with exit 0.
- Deviations: `docs/deviations.md`, "CLI (phase 5)".
- Reviews: implementers were Claude subagents (opus; Task 13 sonnet); task reviews by agy (Gemini). Task 12's agy review and the final
  whole-branch review are the controller's. Ledger with every ruling: `.superpowers/sdd/2026-10-15-phase5-cli/progress.md`.

### Final fix wave (done)

The wave's 21 items and their outcome: `.superpowers/sdd/2026-10-15-phase5-cli/final-fix-wave-report.md`. In short:

- Fixed, each with a test: deliver with an explicit `""` output; one shared `Receipts` class for the UUID v4, sha256 and `.html?`
  patterns, the identity and the JSON readers (the UUID pattern now ends in `\z`); JSON.parse semantics for user files through Core's
  `DiagramLoader.ParseNode`; the browser gates' catch-all; an unwritable evidence directory refused before the browser; LF-only crash
  text; `Args.RepoRoot`; the Core provenance message; a lone surrogate in argv read as Node reads it (U+FFFD); `GitOnPath`'s PATHEXT;
  `\\?` for long native paths; and the test gaps of Tasks 2, 8, 9, 10 and 11.
- Found wrong: an `--out-dir` that is a regular file is not upstream's `viewer/evidence-write`. Upstream throws an indeterminate
  target sidecar namespace (a crash on the command line, like final review m7); the port does the same, now pinned by a Node fixture.
- Card candidates from the final review: m7 (an indeterminate sidecar namespace crashes `check`/`browser-check` in a read-only
  directory, as upstream does).

## State at end of phase 4

- Phase 4 ran as 7 tasks: commits `17a8750` through `884f358`, plus the docs commit that updates README and this file.
- `ArchifyNet.Render` now covers all ten types (`src/Archify.Net.Core/Archify.cs`); `Renderers.For` has no throwing arm. Only
  `Check` with `RequireProvenance` still throws `NotSupportedException` (phase 5).
- Added in phase 4: the erd renderer (`src/Archify.Net.Core/Erd`, reusing `ErdGrid`), `RepositoryLocation` and the Js URI helpers
  (Node-pinned), and repository source evidence via git (`RenderOptions.RepoRoot`, `RepositoryEvidence.Verify` in the load gates,
  git on PATH).
- Parity: 16 erd goldens byte-identical, 16 erd layout reports and 11 erd failure receipts equal upstream's; 25 evidence scenarios
  plus the git-absent receipt equal upstream's, 13 evidence render rows byte-identical, `RepositoryLocation` 213 fixture rows, Js URI 71.
  The fixture repo is a git fast-import stream (`tests/fixtures/evidence/evidence-repo.fi`) with a pinned HEAD.
- `dotnet test` from the repo root: 2772 passed, 0 failed, no warnings (phase 4's earlier note of 2753 was stale).
- Task reviews were done by agy (Gemini); implementers were Claude subagents (opus, Task 7 sonnet). Fable was unavailable.

## Known deviations

Listed in `docs/deviations.md`. Phases 2 to 4 add the items below, also restated in the XML remarks
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
  does, but the message and evidence carry the .NET exception where upstream has `Cannot read properties of undefined (reading '0')` / `TypeError`.
- Evidence: `repository-evidence/root-identity-indeterminate` is unreachable (root identity via `git rev-parse --show-prefix`).
- Evidence: Node's spawn error strings are synthesised (`spawnSync git ENOENT`, `spawnSync git ENOBUFS`); an errno outside the
  port's table prints its number.
- Evidence: the `root-unreadable` message names the whole requested root where Node names the first missing path component.
- Evidence: `realRoot` resolves only the final link of `RepoRoot`; through a junctioned or symlinked parent, `repoRoot` in subjects
  and the paths in the `not a Git repository` and `root-not-top-level` messages differ from Node's (same verdict).
- Evidence: `Render` with `RepoRoot` runs git synchronously with no timeout (as upstream) and holds thread-pool threads per call.
- Evidence: five non-ASCII hosts WHATWG URL accepts are rejected (`IdnMapping` hyphen, label-length, empty-label rules), by input:
  `https://-bücher.example/x/y`, `https://bücher-.example/x/y`, `https://ü` + 70 `a` + `.example/x/y` (71-character label),
  `https://bücher..example/o/r`, `https://xn--bcher-kva..example/o/r`.
- Evidence tests: the `subdirectory-root` receipt is Windows-only (path separator).

Zone acceptance differs by host as well: ids the OS ICU list or `TimeZoneInfo` cannot resolve are rejected
(card TZ-001), and Windows without ICU accepts only UTC and fixed offsets (card TZ-002). Since GLOB-001 (`9f70132`), a host with
`InvariantGlobalization` on works. Its only difference: the three localeCompare tie-breaks sort ordinally.

## Carried into later phases

- **`joinRoutePoints` identity memo: closed.** Upstream's memo (geometry.mjs:1327-1374) is unobservable in workflow and
  dataflow, and `GeometryTests.JoinRoutePoints_output_is_a_fixed_point_of_NormalizeRoutePoints` pins the property that makes it so
  on 6 hand-built shapes (the plan asked for 17 candidate shapes); the closure rests on no code comparing normalised arrays by identity.
- **`gateProfile` sites are review-checked only.** No phase-2 fixture makes a shared geometry gate fail, so a
  meta-only port would pass the same goldens. The phase-2 plan's Tasks 5 (sequence) and 7 (class) list the sites.
- **Erd entity width: closed.** `ErdGrid.ResolvedEntityWidth` takes a `GridEntity` only; the erd renderer uses
  `GridEntity.From(entity, 0)` there, and 16 erd goldens and layout reports confirm it.
- **Phase-3/4 coverage gaps: closed by cleanup batch 2** (TEST-002 `3563f82`, TEST-003 `0ce0060` + `4c57360`):
  - 33 `t2-*` workflow inputs pin the layout-feedback rounds, internal detours, pin-conflict throwers, placed-label and rhythm
    evidence, and both verified repairs. `pairOrdinal*9` and outside-right growth were already pinned by older goldens.
  - The erd trunk snap, bridge and delete, and every `validateEr` branch, are pinned. The non-finite-width check is dead
    upstream (the schema rejects it first), so `erd-width-infinite` pins the schema gate instead.
  - The evidence `object-unavailable` (partial clone) and both per-file fallbacks are pinned through damaged fixture repos
    (`EvidenceRepo.MakePartialClone`/`MakeBadType`/`MakeTruncated`).
  - TEST-004 `6f09f8a` pins the last reachable ones: the route-order-retry feedback catch, the 16px interior turn, the
    explicit-pin arm of `throwDiagnosticError`, and both sides of the dataflow `repairLabelRoutes` >80 guard (81 → 49
    diagnostics, 80 → 40). The unreachable ones carry their argument beside the C# code:
    - `throwDiagnosticProblems` (`WorkflowCompilation.Validation.cs`);
    - `readablePresetVia`'s success branch (`WorkflowCompilation.Pins.cs`);
    - `throwDiagnosticError`'s `solver-budget-exhausted` arm, not found (`WorkflowCompilation.Svg.cs`).
- **Non-empty `Layout.Report.diagnostics`: closed.** Phase 5 pins it: the `tests/fixtures/cli/layout` failure fixtures carry
  non-empty `diagnostics`, and `workflow-measured-failure` pins upstream's `{…, diagnostics, ok:false}` order. `t2-xrep-standard`
  pins a success receipt with a warning.

## Dev-time tooling

`tools/capture-fixtures.mjs` mints fixtures from upstream. It needs Node 24 and the upstream repo `C:\projects\archify` at
`v3.2.0` (`208acba8`); it reads the skill package root `C:\projects\archify\archify`. CI never runs it; the test suite only reads what it committed.
The SDD ledgers (git-ignored, on disk) are `.superpowers/sdd/2026-10-14-phase2-renderers/progress.md`,
`.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/progress.md`,
`.superpowers/sdd/2026-10-15-phase4-erd-evidence/progress.md`,
`.superpowers/sdd/2026-10-15-phase5-cli/progress.md` and
`.superpowers/sdd/2026-10-15-phase6-mcp/`, `.superpowers/sdd/2026-10-15-cleanup-batch1/` and
`.superpowers/sdd/2026-10-15-cleanup-batch2/` (with the TZ, TEST-002 and TEST-003 surveys), `.superpowers/sdd/2026-10-15-test004/`
and `.superpowers/sdd/2026-10-15-perf003/`: every ruling and the deferred minors.

## Other open cards

The open cards on the board (project 46) beyond the PORT-008/009/012 chain above are:

- **Waiting on Martin:**
  - UPSTREAM-001: four upstream examples fail upstream's own gates. Report it upstream, or fix our copies of the examples?
  - PERF-002: an optional GitRunner timeout. It would be a new deviation from upstream, so it needs a decision on the
    diagnostic code.
  - TZ-001: the 19 ids V8 accepts and the port rejects. A survey found that ICU `ucal_*` P/Invoke against Windows' `icu.dll` would fix
    them plus LMT and history. But Windows' ICU zone data is frozen at tzdata 2022g, so Africa/Cairo 2023+, America/Nuuk 2024+ and
    America/Asuncion 2025+ would go wrong, while Chihuahua, Ojinaga and Casablanca would go right. Options on the card:
    - (a) ICU on Windows;
    - (b) hybrid;
    - (c) keep as a stated deviation.

    Linux keeps `TimeZoneInfo` either way. Survey:
    `.superpowers/sdd/2026-10-15-cleanup-batch2/survey-tz.md`.
- **Parked:** TZ-002 (pre-1903 Windows / Server 2019 only; TZ-001's ICU route would cover `UseNls`). Revisit when a user
  runs such a host.
- **Done in cleanup batch 1** (plan `docs/superpowers/plans/2026-10-15-cleanup-batch1.md`, all four reviewed by agy):
  - GLOB-001 `9f70132`;
  - CLEAN-001 `2ee032b`;
  - PERF-001 `19a178f` (thread use only);
  - TEST-001 `ac72624` (12 more Node goldens and receipts).
- **Done in cleanup batch 2** (plan `docs/superpowers/plans/2026-10-15-cleanup-batch2.md`, each task reviewed by agy):
  - TZ-003 `5ab0465` (ZoneWall keeps DST past `DateTime.MaxValue`);
  - TEST-003 `0ce0060` (erd) and `4c57360` (evidence);
  - TEST-002 `3563f82` (workflow).
  - PERF-003: **a huge node `width`/`height` hangs upstream and the port alike.** Two automatic edges between the same
    pair, with a width of 1e30, never return. The cause is `automaticPortCandidates`' `offset += 12` probe loop, at
    upstream :4492 and C# `WorkflowCompilation.Routing.cs:1113`. Options on the card:
    - (a) report it upstream;
    - (b) a minimal loop guard (a deviation);
    - (c) a schema cap.

    The recommendation is (a) + (b). Report and inputs: `.superpowers/sdd/2026-10-15-perf003/`. Decided since: fix in the
    port and report upstream, queued after PORT-009 with UPSTREAM-002 (see "Next").
- **Done after batch 2:** TEST-004 `6f09f8a` (plan `docs/superpowers/plans/2026-10-15-test004.md`).
- **Ready to work:** the chain under "Next" above. The rest waits on Martin, or is parked.

## Phase 4 closed (2026-10-15)

- All 7 phase-4 tasks passed their agy reviews. The final whole-branch review and the push are the controller's.
- The phase-4 ledger with every ruling: `.superpowers/sdd/2026-10-15-phase4-erd-evidence/progress.md`.
