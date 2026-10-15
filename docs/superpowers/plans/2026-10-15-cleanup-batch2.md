# Cleanup batch 2: TZ-003, TEST-003 (erd + evidence), TEST-002 (workflow)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** close three board cards:
- TZ-003: `Js.ZoneWall` keeps DST for instants past `DateTime.MaxValue`.
- TEST-003: the phase-4 erd and repository-evidence paths that no Node-pinned input reaches get one.
- TEST-002: the phase-3 workflow paths that no Node-pinned input reaches get one.

**Architecture:**
- Task 1 is a one-line behaviour fix plus fixture rows.
- Tasks 2-4 add inputs to `tools/capture-fixtures.mjs`, mint goldens and receipts with Node, and add C# rows. They change no
  source file unless a new row exposes a parity bug. Such a bug is fixed in the same task, with the failing row as its test.

**Tech stack:** .NET 10, xUnit, Node 24 for capture (upstream `C:\projects\archify` at c21c31ff), git 2.52.

**Spec:** `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md`, "## Testing" (R12: Node is the oracle). The cards
are the requirements. Three surveys back this plan, all in the git-ignored workspace `.superpowers/sdd/2026-10-15-cleanup-batch2/`:
- `survey-tz.md` §5.4 (TZ-003);
- `survey-test3.md` (TEST-003): inputs, upstream file:line, upstream outcomes;
- `survey-test2.md` plus `survey-test2-inputs/*.json` (TEST-002): 38 inputs and their upstream outcomes.

## Global constraints

- Phase-2 plan lines 15-29 still apply:
  - no NuGet dependency in Core;
  - `Js.*` for JS semantics;
  - UTF-8 without BOM, LF;
  - diagnostics are returned, not thrown;
  - commits use `git commit -F` with an exact `git add` list.
- **No existing fixture or golden changes**, except `tests/fixtures/js/time-zones.json` in Task 1. Run the capture, then check
  `git status --porcelain tests/`: only new files (and that one file in Task 1) may appear.
- **The capture runs twice per task**, and the two runs must be byte-identical (hash the `tests/` tree).
- The full `dotnet test` (Browser included) stays green with zero warnings.
- No environment variables as configuration. `ARCHIFY_*` variables in the *capture script's Node children* are the existing
  oracle mechanism and are fine.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (or the implementing model's trailer).

## Review focus

1. **A row that passes vacuously.** Every new receipt row must carry the target code the survey names. Every new clean
   golden must actually reach its branch: the capture asserts the outcome, and Task 2 asserts geometry for the snap.
2. **Machine-dependent fixtures.** Absolute temp paths, git versions and the partial clone's `file://` URL must not leak into a
   committed file. Reuse the evidence tokeniser and the existing `<ROOT>` check.
3. **Damaged-repo recipes are built identically on both sides.** Node (capture) and C# (test) must write the same bytes, or a
   test that reaches a different branch than the fixture recorded is vacuous.
4. **Quality profile.** Several TEST-002 inputs only reach their branch at a given profile. The fixture must carry that
   profile, so the C# row compiles at the same one.

---

### Task 1: TZ-003, `ZoneWall` keeps DST past `DateTime.MaxValue`

**Card:** `JsTime.cs:121-122` uses `z.BaseUtcOffset` for instants outside `[MinMs, MaxMs]`, and that ignores DST.
- Example: Australia/Sydney at `+010000-01-01T00:00Z` is +10:00 in the port and +11:00 in V8.
- Affected: 28 ids on Windows, 18 on Linux (survey-tz §5.4).

**Ruling:**
- Clamp the lookup instant into the `DateTime` range: `z.GetUtcOffset(DateTime.UnixEpoch.AddMilliseconds(Math.Clamp(ms, MinMs, MaxMs)))`.
- The wall clock is still built from the real `floorMs`; only the offset lookup is clamped.
- Below `MinMs` the result is unchanged: the registry and tzdata have no rule at year 1, so `GetUtcOffset(MinValue)` is the
  base offset.
- Leave TZ-001's ICU question alone. That card waits on Martin.

**Files:**
- `src/Archify.Net.Core/Js/JsTime.cs`
- `tools/capture-fixtures.mjs:161-179`
- `tests/fixtures/js/time-zones.json` (regenerated)
- `tests/Archify.Net.Core.Tests/JsTimeTests.cs`

- [ ] **Step 1, the fixture rows.** In `capture-fixtures.mjs`:
  - append `'Australia/Sydney'` to `wallZones`;
  - append `'+010000-01-01T00:00:00Z'` to `instants`.

  Do not touch `guaranteedWall`. The year-10000 rows are `>= 2026`, so they are guaranteed. Regenerate the file by running
  the capture.
- [ ] **Step 2: confirm the new rows fail.** Run `ZoneWall_matches_formatToParts_on_every_guaranteed_row` before the fix.
  - It must fail on Sydney, Chatham and Lord_Howe at year 10000.
  - Record the failing rows in the report.
- [ ] **Step 3, the fix.** Replace `ZoneOffsetMs`'s ternary with the clamp. Update its `ponytail:` comment to say that the
  offset is clamped into the `DateTime` range, and that past `MaxValue` the last rule continues as V8's does.
- [ ] **Step 4: run the full `dotnet test`.**
  - The JsTime tests are green.
  - No timeline golden changes: none uses a year past 9999.
- [ ] **Commit:** `fix(core): ZoneWall keeps DST past DateTime.MaxValue — clamp the offset lookup, not BaseUtcOffset (TZ-003)`.

### Task 2: TEST-003 erd, trunk snap/bridge/delete and the `validateEr` branches

**Inputs:** survey-test3.md §1-§3 has each input verbatim, with its upstream outcome. Build each with the survey's helpers
(`pk`, `wide`, `rel`, `scene`), copied into `capture-fixtures.mjs` beside the phase-4 erd block (`:479-551`).

**Clean scenes:** written with `erdWrite(name, doc)`, so they get a golden, a checker report and a layout report automatically.

| name | survey | reaches |
|---|---|---|
| `erd-trunk-snap` | §1 `trunk-snap` (hub_b width **190**) | snap with `near.coordinate !== candidate`, plus 2 bridges |
| `erd-trunk-bridge` | §1, upstream `T/erd-rendering.test.mjs:176-200` scene verbatim | bridge |
| `erd-trunk-all-clash` | §1 `trunk-all-clash` | coordinate-undefined delete (no emphasis trunk) |

**Capture assertions.** The capture throws if an outcome differs from the survey, so a later upstream change cannot silently
empty a row:
- `erd-trunk-snap`'s rendered HTML contains the trunk path at x=256 that the survey names (`256,108;256,612`).
- `erd-trunk-all-clash`'s HTML contains no `a-emphasis` trunk.

Read the HTML for the exact attribute spelling, and assert on that spelling.

**Fault rows:** added to `erdFaults` (`:871`). Each edit first runs `delete d.meta.views`, which is upstream's `cloneWithoutViews`
and the survey's base. Each row then makes the survey's edit (§2).

| name | target codes | edit |
|---|---|---|
| `erd-duplicate-attribute` | `layout/constraint` | `d.entities[0].attributes.push({ name: 'id', type: 'bigint' })` |
| `erd-unknown-source` | `layout/constraint` | `relationships[0].from = 'ghost'; relationships[0].id = 'ghost_link'` |
| `erd-unknown-target` | `layout/constraint` | the same edit on `.to` |
| `erd-ref-unknown-entity` | `layout/constraint` | `entities[1].attributes[1].references = 'nobody.id'` |
| `erd-ref-unknown-attribute` | `layout/constraint` | `entities[1].attributes[1].references = 'customer.nope'` |
| `erd-too-short` | `layout/constraint` | `d.layout.gapX = 10` (17px and 20px: real distances) |
| `erd-entity-overlap` | `layout/constraint`, `erd/domain-not-drawn` | `e = entities[1]`: delete `row`, `col`, `tag`; `e.pos = [60, 100]` |
| `erd-label-over-entity` | `layout/constraint` | replace the whole doc with survey §2's label-corridor scene, verbatim |

**Width-infinite (schema parity):** survey §3 shows the renderer check is dead upstream.
- Pin the schema gate instead. Write `erd-width-infinite.erd.json` as **raw text**: `orders.erd.json` with
  `"width": 1e400` on `entities[0]`. `JSON.stringify` cannot produce `1e400`, so patch the text with an asserted single anchor.
- Capture its receipt like the other faults. The target is `schema/type`.
- **The C# loader's result is unverified.** System.Text.Json may give `Infinity` (then the C# validator must say `schema/type`
  like ajv's `isFinite`), or it may refuse the token (a different code).
- If the C# receipt differs, that is a parity bug. Fix it in the loader or validator, with this row as the test, and say so in
  the report.

**C# rows:**
- Add the 3 scenes to `ParityTests.Targets()`. The erd block is at `ParityTests.cs:101-117`.
- Add the 9 faults as `InlineData` on the erd fault theory (`ErdTests.cs:44-54`).
- The layout reports are picked up automatically from `tests/fixtures/erd/*.layout.json` (`ErdTests.cs:12`).

- [ ] **Step 1:** add the scenes, the faults and the raw-text fixture to the capture, with the assertions above.
- [ ] **Step 2:** run the capture twice: identical, and only new files.
- [ ] **Step 3:** add the C# rows and run them.
  - Each must pass, or the failure is a parity bug. Fix and report it; never edit a fixture to match.
  - The handoff claims these paths were ported faithfully, so a failure is unexpected and must be reported in full.
- [ ] **Step 4: mutation check (proves the rows are not vacuous).** Make each change temporarily, run the new rows, then
  revert it. Each change must turn at least one row red. Record which row.
  1. `ErdRenderer.cs:533-538`: make the snap use `candidate` always.
  2. `ErdRenderer.cs:541-546`: skip the delete.
  3. `ErdRenderer.cs:943`: skip `last.high = segment.high`.
- [ ] **Step 5:** run the full `dotnet test`.
- [ ] **Commit:** `test(core): Node-pinned inputs for the erd trunk and validateEr branches (TEST-003)`.

### Task 3: TEST-003 evidence, per-file fallback and `object-unavailable`

**Inputs:** survey-test3.md §4. The fixture repo is built by `fast-import` from `evidence-repo.fi`, as **loose objects**, so one
object file can be damaged directly. The router blob is `9e396e4ede13a17cccc016bf524c97bd8e2efee4`, at
`.git/objects/9e/396e4ede13a17cccc016bf524c97bd8e2efee4`. Git writes it read-only, so clear that attribute first. Inflated, it is
`blob 54\0` followed by 54 content bytes.

**New root kinds.** Add them to both `evidenceJobs` (capture, `:1101-1107`) and `Verify_matches_upstream`'s switch
(`RepositoryEvidenceTests.cs:47-52`). Implement each kind once per side, in a helper. On the C# side the helpers live in
`EvidenceRepo.cs`.

| kind | recipe | survey |
|---|---|---|
| `partial` | Source repo: `config uploadpack.allowFilter true`. Then `git clone --quiet --filter=blob:none --no-checkout <file URL of source> <new temp>`, then `remote set-url origin <scenario origin>`. The root is the clone. | §4a |
| `bad-type` | Inflate the router blob, replace its `blob ` header with `bogus `, and write it back as a zlib stream (any valid deflate output works). | §4b |
| `truncated` | Hand-build a zlib **stored** stream: `78 01`, then a final stored-block header `01 LEN_lo LEN_hi ~LEN_lo ~LEN_hi` for the full inflated length (8 + 54 = 62), then the inflated bytes. Keep only the first 2 + 5 + 9 + 10 = 26 bytes. | §4c recipe 1 |

**Notes on the recipes:**
- `truncated` is built **by hand**, not with a zlib library at level 0. The bytes are then identical in Node and .NET by
  construction.
- `bad-type` can use each side's own deflate. Git only inflates it, so the compressed bytes may differ.
- The `partial` file URL is the source path as a `file:///` URL: `pathToFileURL` in Node, `new Uri(path).AbsoluteUri` in C#.
  The clone needs no network.
- Every git call uses the scrubbed `gitEnv` / `EvidenceRepo.Git`.
- Delete the source repo and the clone afterwards. Both are under the temp root, and `evidenceTemp` / `EvidenceRepo.Delete`
  already handle read-only files.

**Tokenising:** the receipts may name the clone's root. Tokenise against the root the scenario passes (for `partial`, the
clone), and check that the existing absolute-temp-path check (`:1120`) still passes.

**Scenarios:** these go into `evidenceScenarios` (`:1067`). `GH_ORIGIN`, the architecture type and the expected codes come from
the survey.

| name | root | sources (`evArch(GH, …)`) | expected |
|---|---|---|---|
| `object-unavailable-partial` | `partial` | `ROUTER_STORE` | `['repository-evidence/object-unavailable', 'repository-evidence/object-unavailable']` |
| `object-unavailable-path-only` | `partial` | `[{ path: 'src/router.js' }]` | `['repository-evidence/object-unavailable']` |
| `batch-check-fallback` | `bad-type` | `[{ path: 'src/router.js' }, { path: 'src/store.js', line: 9 }]` | `['repository-evidence/object-unavailable', 'repository-evidence/line-out-of-range']` |
| `content-batch-fallback` | `truncated` | `ROUTER_STORE` | `['repository-evidence/file-unreadable']` |
| `content-batch-path-only` | `truncated` | `[{ path: 'src/router.js' }]` | `'payload'` |

**Not pinned, on purpose:**
- **The "header claims 999 bytes" recipe (§4c.2).** Its clean pass depends on git 2.52's `show` tolerating the size mismatch,
  so it would tie the suite to one git version.
- **The 70 MiB `ENOBUFS` content-batch recipe (§4c.3), as a fixture.** Pin it instead as a **C#-only fact** beside
  `A_blob_over_16_MiB_is_ENOBUFS_and_stops_the_loop` (`RepositoryEvidenceTests.cs:71`), with the survey's upstream outcome as the
  expected value (exit 0, payload). Build the repo with `git add` and `commit`, as that fact does: one 14 MiB file of `'a'`
  with a newline every 80 bytes, committed at 5 paths `a.txt`…`e.txt`. Cite `{path, line: 1}` for all five, split 3 + 2 over
  `components[0]` and `components[1]`. Assert the verdict is a payload with 5 verified sources.
  - **Why not Node-minted:** the result is a clean payload whose content is the C# port's own reading of 5 files the test
    itself wrote. The upstream oracle adds nothing beyond "exit 0", which the survey recorded.

**Steps:**
- [ ] **Step 1:** add the three root-kind helpers on both sides. Then add one small C# fact per kind that proves the damage
  took:
  - `partial`: `cat-file --batch-all-objects --batch-check` lists no blob;
  - `bad-type`: `cat-file -t 9e396e…` exits non-zero;
  - `truncated`: `cat-file -p 9e396e…` exits non-zero.

  `EvidenceRepo.Git` throws on a non-zero exit; assert that it throws.
- [ ] **Step 2:** add the five scenarios to the capture, run it twice (identical, only new files), and add nothing else in C#:
  `Verify_matches_upstream` enumerates `scenarios.json`.
- [ ] **Step 3:** run the new rows. A failure is a parity bug: fix it in `RepositoryEvidence.cs`/`GitRunner.cs` and report it.
- [ ] **Step 4:** add the `ENOBUFS` content-batch fact.
- [ ] **Step 5: mutation check.** Make each change temporarily, run the new rows, and revert. Each change must turn a row red.
  1. Make `RepositoryEvidence.cs`'s prefetch ignore a null batch result: treat it as "all present".
  2. Make the per-file `show` fallback after a null content batch return "no content".
- [ ] **Step 6:** run the full `dotnet test`.
- [ ] **Commit:** `test(core): Node-pinned evidence receipts for object-unavailable and both per-file fallbacks (TEST-003)`.

### Task 4: TEST-002 workflow, layout feedback, internal detour, pin conflicts and verified repairs

**Inputs:** the 38 files in `.superpowers/sdd/2026-10-15-cleanup-batch2/survey-test2-inputs/`. Their content is also inlined in
survey-test2.md's appendix.

**Ruling: commit them as fixture inputs.** Copy them to `tests/fixtures/diagrams/t2-<name>.workflow.json`. The capture reads
them from there, the same way `phase3Copies` (`:374`) reads upstream's test fixtures. They come from upstream tests or from
crafting, and are not derivable from a bundled example.

**Duplicates dropped:** the survey's content is identical for these pairs, so keep only the first of each:
- `lg6` = `authored-side`;
- `labellabel2036` = `label-label`;
- `placed-label-route` = `preset-placed`;
- `labelroute2056` = `label-route`.

`label-route` reaches its branch only inside a caught probe, so skip it. `orthogonal` is already pinned by
`wf-single-pin-conflict`, so skip it.

**Quality profile:**
- The survey ran 11 inputs at `--quality showcase` and 12 at `--quality standard` (survey-test2 "Every input runs with…").
- Give each fixture that profile as `meta.quality_profile`. The fixture then compiles at that profile on both sides with no
  option plumbing.
- **After the capture, check each receipt's outcome against the survey's table** (code plus check name). If moving the
  profile into meta changes an outcome, use the `erdFaults`-style env column (`ARCHIFY_QUALITY_PROFILE`) for that row instead.
  On the C# side that means `RenderOptions`, as `Workflow_fault_*` rows already do where needed.

**Rows:**

| fixture name | outcome (survey) | kind |
|---|---|---|
| `t2-rg-100` | OK, after one rank-gap feedback round | clean: golden + receipt via `p3Rows` |
| `t2-detour` | OK, an `internal-detour` plan selected | clean |
| `t2-lg6` | `workflow/explicit-pin-conflict` [readable route feasibility with authored endpoint sides], after a lane-gap round | fault |
| `t2-fb-exhaust` | `workflow/solver-budget-exhausted` | fault |
| `t2-labelroute2077` | `workflow/explicit-pin-conflict` [explicit label-route clearance] | fault |
| `t2-labellabel2036`, `t2-labellabel2084` | [explicit label-label clearance] | fault |
| `t2-route-crossing` / `t2-route-corridor` | [explicit route-route crossing] / [… corridor clearance] | fault |
| `t2-labelat-origin` / `-node` / `-legend` / `-scene` | [viewBox-origin containment] / [edge-label node / legend / lane/phase/group clearance] | fault |
| `t2-neg-via`, `t2-neg-channel` | [viewBox-origin containment] | fault |
| `t2-fin-wide-1e308` | [finite route coordinates] | fault (see below) |
| `t2-nonzero`, `t2-stub`, `t2-channel`, `t2-preset2562`, `t2-preset2218`, `t2-perpendicular`, `t2-pin-node`, `t2-pin-legend`, `t2-pin-scene-label`, `t2-pin-frame-border` | the survey's per-path check | fault |
| `t2-placed-label-label` / `-route-label` / `-label-route` | `workflow/route-preset-conflict` [placed edge-label clearance], `conflictType` label-label / route-label / label-route | fault |
| `t2-preset-rhythm` | `workflow/route-preset-conflict` [readable segment rhythm] | fault |
| `t2-xrep` | `composition/proper-crossing` with `evidence.verifiedRepair` | fault |
| `t2-prep` | `composition/proper-crossing`, `verifiedRepair.kind` = `optional-node-placement` | fault |

**Mechanics:**
- **Faults** go through a new array beside `p3Faults` (`:819`), with the same loop shape: the upstream renderer with
  `ARCHIFY_DIAGNOSTIC_FORMAT=json`.
  - The capture asserts each target code **and** the check name in the message or evidence. Read one receipt for the field
    that carries `[...]`, then assert on that field.
  - Receipts: `tests/fixtures/diagrams/<name>.workflow.expected.json`.
  - C#: `InlineData` on `Workflow_fault_diagnostics_match_upstream_receipt` (`ParityTests.cs:192`).
- **Clean** inputs: append them to `p3Rows` (`:789`). That gives a golden, a checker report, and
  `fixtures/workflow/<name>.receipt.json`. Then add `ParityTests.Targets()` rows and the receipt rows the existing
  `WorkflowReceiptTests` uses.
  - Check how `p3Rows` is enumerated by the manifest test: a new row may need an owning-task value. Use `"TEST-002"`.
- **`t2-fin-wide-1e308`**: its evidence serialises non-finite coordinates. A mismatch here is a real parity finding about
  Infinity/null serialisation, not a fixture problem. If it cannot be matched without a design change, take the row out,
  report it, and the controller puts it on a card.

**The card's other items:**
- **Non-empty `Layout.Report.diagnostics`.** Find one standard-profile v2 workflow that renders with warnings in its success
  receipt.
  - Try first: `t2-xrep` and `t2-route-crossing` at `quality_profile: "standard"`, as separate clean rows named `-standard`.
  - Keep the first input whose receipt has `"diagnostics": [` with at least one entry, as a clean row.
  - If none of them works within 5 tries, report it; do not search further.
- **Out of scope, for the controller to card:**
  - the route-order-retry feedback catch (upstream :5732, C# `WorkflowCompiler.cs:198`), "not found" by the survey;
  - the `16px interior turn` arm of :2514;
  - `throwDiagnosticProblems` :2728 and `throwDiagnosticError` :5265;
  - `readablePresetVia`'s success branch, argued unreachable;
  - the dataflow `repairLabelRoutes` early return (`joint-label-routing.mjs:26`). The `metrics` it writes reach no receipt,
    so only an input whose repair would otherwise fire can observe it.

**Steps:**
- [ ] **Step 1:** copy the inputs, set `meta.quality_profile` where the survey says, and add the arrays and assertions to the
  capture.
- [ ] **Step 2:** run the capture twice: identical, and only new files. Check every outcome against the table.
- [ ] **Step 3:** add the C# rows and run them. A failure is a parity bug: fix it with the row as its test, and report it.
- [ ] **Step 4:** the non-empty-diagnostics row.
- [ ] **Step 5: mutation check.** Make each change temporarily, run the new rows, and revert. Each change must turn a row
  red.
  1. Remove the rank-gap feedback consumption: `WorkflowModel.cs:190`, where `rankGapMinimums` is read.
  2. Make `addVerifiedCrossingRepair` (`WorkflowCompiler.cs:40`) return without adding evidence.
  3. Drop internal-detour plans (`WorkflowCompilation.Routing.cs:1233`).
- [ ] **Step 6:** run the full `dotnet test`.
- [ ] **Commit:** `test(core): Node-pinned workflow inputs for layout feedback, detours, pin conflicts and verified repairs (TEST-002)`.

## Exit

- TZ-003, TEST-003 and TEST-002 close with their commit SHAs.
- The TEST-002 card body gets the survey's corrections: `pairOrdinal*9` and outside-right growth were already pinned.
- TEST-004 is minted for the out-of-scope paths in Task 4, plus any row a task had to drop.
- Every pre-existing golden is unchanged, and the full suite is green with zero warnings.
