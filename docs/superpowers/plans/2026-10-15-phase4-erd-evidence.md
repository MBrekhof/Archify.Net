# Archify C# Port — Phase 4: erd renderer + repository evidence

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ArchifyNet.Render(DiagramType.Erd, json)` produces HTML byte-identical to upstream archify 3.1.0 (2 upstream goldens + Node-minted first-draft, label-capacity, i18n and derived goldens), and `RenderOptions.RepoRoot` turns on **repository source evidence** for all ten diagram types exactly as upstream's `--repo-root` does: the same verified payload in the template, the same source-badge geometry, and the same `repository-evidence/*` diagnostics.

**Architecture:** Erd follows the phase-2 renderer shape (`ErdRenderer : IDiagramRenderer` with a private `Scene`, upstream statement order, `ClassRenderer` is its closest sibling). Repository evidence is three new units:
- `RepositoryLocation`, a pure WHATWG-faithful remote-URL parser (upstream `repository-location.mjs`);
- `GitRunner`, a byte-exact `git` process layer;
- `RepositoryEvidence.Verify`, upstream `verifyRepositoryEvidence`, called at the end of `DiagramLoader.Load`.

Its result fills `RenderContext.SourceEvidence`, which every renderer already reads.

**Tech Stack:** .NET 10, C# 14, `System.Text.Json.Nodes`, `System.Diagnostics.Process`, no runtime NuGet packages. Tests use xUnit 2.9.3. Node 24 and git (dev-time and test-time) are used to mint expected values from upstream `C:\projects\archify` @ `c21c31ff`.

**Spec:** `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (porting order step 4; the **Source evidence** paragraph). Inherited rules: the phase-2 plan's Global Constraints and Scene conventions, and the phase-3 plan's R9-R12. Surveys this plan is built from (on disk, git-ignored; every implementer reads the one for their task **in full**):
- `.superpowers/sdd/2026-10-15-phase4-erd-evidence/survey-erd.md`
- `.superpowers/sdd/2026-10-15-phase4-erd-evidence/survey-evidence.md`

`UP/` = `C:\projects\archify\archify\`, `T/` = `C:\projects\archify\test\`. Board card: `PORT-004` (CARD-2169).

## Global Constraints

These carry over verbatim:
- Phase-2 plan lines 15-29: net10.0; strict byte parity; `Js.Num`, `Js.Sort`, `JsMap`, `JsSet`, `Js.Trim`, `Js.EscapeHtml`, `Js.ToNumber`; `||` vs `??` ported literally; no LINQ `Max`/`Min` on possibly empty sequences; ECMAScript regexes; StringBuilder for SVG; UTF-8 without BOM, LF line endings; `upstream/` and `tests/goldens/` are never hand-edited; diagnostics are returned, not thrown; no NuGet dependency; every `Js` member carries `// ponytail: V8 parity` and a fixture-backed test; commit with `git commit -F` and an exact `git add` list; task state lives on the board.
- Phase-2 Scene conventions.
- Phase-3 R9 (reference identity where upstream compares objects), R10 (exceptions as control flow ported literally), R11 (diagnostic recording), R12 (every expected value comes from Node; the capture script throws on a fixture that misses its target and stays re-runnable).

New in phase 4:

- **P1 — erd quality profile follows the phase-2 rule.** There are no direct `quality_profile` reads. The four gate calls (render-erd.mjs:1277, :1286, :1295, :1325) take `gateProfile = ctx.Options.Quality?.Name() ?? meta.quality_profile`, exactly as ClassRenderer.cs:156. This is the first phase where an input pins `gateProfile`: a standard document whose authored routes cross renders under `standard` and fails with `composition/proper-crossing` under the Showcase option (Review Focus 5).
- **P2 — unplaced erd entity.** An entity with neither row/col nor `pos` crashes upstream with a JS `TypeError` inside `labelPoint` (geometry.mjs:1879), which comes out as `internal/unclassified` with `evidence.errorName: "TypeError"`. The port reports `internal/unclassified` too, but its evidence names the .NET exception type.
  - **Stated deviation** (README + XML remarks, Task 7): `internal/unclassified` evidence carries the .NET exception name.
  - The same input **without relationship labels** does not crash upstream. NaN geometry instead yields `erd/domain-not-drawn` (`no-grid`, `off-canvas`) plus a `layout/constraint` grid problem. That Node receipt is pinned in full, because NaN comparisons are on the parity path.
- **P3 — gaps closed in shared code**, all additive and default-neutral (survey-erd §1):
  - `LegendEntry` gains `double Lead = 0`.
  - `Geometry.LabelPoint` gains an overload with a `labelSegment` override. Never write `labelSegment` onto the authored `JsonObject`: `AutomaticLabels` treats key presence as pinned.
  - `TreeRenderer.TrailingBlanks` moves to `Utils`, and both renderers use it.
  - `Renderers.For` gains the erd arm.

  Every phase-1/2/3 golden stays byte-identical.
- **P4 — `RenderOptions.RepoRoot` replaces `ARCHIFY_REPO_ROOT`**; `""` counts as null (JS falsy).
  - `RepositoryEvidence.Verify(type, diagram, repoRoot, sink)` runs at the end of `DiagramLoader.Load`, after `EngineeringProfiles.Validate` and before `PrepareDiagramBrandMarks`, which is upstream's gate order (cli.mjs:85, :107-111).
  - It returns null and spawns **nothing** when the diagram has no `meta.repository` and no non-empty `sources`, so no existing golden can change.
  - `RenderContext.SourceEvidence` gets an `internal` setter.
- **P5 — root identity without porting `path-semantics.mjs`.** After `rev-parse --show-toplevel`, the port runs `git -C <realRoot> rev-parse --show-prefix`, and an empty prefix means the root is the top level. Git resolves junctions, case and 8.3 names itself.
  - **Stated deviation:** `repository-evidence/root-identity-indeterminate` is unreachable in the port. Upstream reaches it only through a mocked `fs.statSync` (T/repository-evidence.test.mjs:192-209).
  - `realRoot` is used for diagnostic text only: `Path.GetFullPath(input)` with trailing separators trimmed and the final directory link resolved.
- **P6 — `GitRunner` mirrors `spawnSync` exactly** (survey-evidence §8):
  - file `git` through `ArgumentList` (never `cmd /c`); every call prefixed `--no-replace-objects -C <root>`;
  - inherited environment plus `GIT_NO_LAZY_FETCH=1` and `GIT_ALLOW_PROTOCOL=""` for the child (upstream's isolation of the child process, not configuration of the port);
  - stdout read as **bytes**, stderr drained and discarded, stdin written **concurrently** with reading stdout for `cat-file --batch`;
  - maxBuffer 16 MiB (`runGit`) and 64 MiB (batch); it trips only when output is **strictly greater** than the limit;
  - no timeout.
  - Node's spawn error strings are **synthesised**: `spawnSync git ENOENT` (`Win32Exception.NativeErrorCode == 2`, or the Unix equivalent) and `spawnSync git ENOBUFS`. Any other start failure becomes `spawnSync git <errno-name>` from a small table.
  - **Stated deviation:** an errno outside that table prints its numeric code.
- **P7 — `RepositoryLocation` is a hand-written authority parser, not `System.Uri`** (survey-evidence §7).
  - It covers: scheme lowercasing; userinfo; host lowercasing, IDNA (`IdnMapping`) and IPv4 shorthand for special schemes; default-port dropping; the ssh opaque host (case and `:22` kept); and the invalid-port → null case.
  - The `Js` layer gains `EncodeUriComponent` (it keeps `A-Za-z0-9-_.!~*'()` and emits uppercase hex) and a strict `DecodeUriComponent` (returns null on a bad escape or invalid UTF-8). It also gains `ToLowerCase`, matching V8 full-Unicode lowercasing: `İ` (U+0130) becomes `i̇` (U+0069 U+0307).
  - All of these are pinned by a Node-minted table (Task 4).
  - Any row the port cannot match is a **stated deviation** listed by input, never a silent skip.
- **P8 — the evidence fixture repo is a checked-in `git fast-import` stream**, `tests/fixtures/evidence/evidence-repo.fi` (survey-evidence §5).
  - Tests and the capture script build the repo the same way:
    1. `git init --template=` in a temp directory;
    2. `git fast-import --quiet < evidence-repo.fi`;
    3. `git remote add origin <url per scenario>`;
    4. assert `rev-parse HEAD` equals a pinned constant.

    The child git processes of this **test helper** run with `GIT_*` scrubbed and `GIT_CONFIG_NOSYSTEM=1` / `GIT_CONFIG_GLOBAL=NUL` (or `/dev/null`).
  - Diagnostic receipts containing the temp root are compared after replacing the root with the token `<ROOT>`, in both its OS-separator and forward-slash spellings.
  - Tests need `git` on PATH. A test that cannot find git **fails** with a clear message; it is never silently skipped.

## Review Focus

1. **An erd entity with neither row/col nor `pos`** has two outcomes:
   - with labelled relationships: `internal/unclassified`, as upstream (P2);
   - without labels: Node's full `erd/domain-not-drawn` + `layout/constraint` receipt.

   Task 3, faults `erd-unplaced-labelled` and `erd-unplaced-unlabelled`.
2. **Source evidence on a narrow lifecycle node** → `TextFit.NodeLabelLayout`'s `source` compact-leading fallback (text-fit.mjs:80-96, never run in C#) equals Node. Task 6 golden `lifecycle.evidence-narrow`.
3. **Repository URLs in every spelling upstream accepts** give identity, canonical URL, label and hrefs equal to Node:
   - SCP `git@host:owner/repo.git`;
   - `ssh://git@GitHub.com:22/…`;
   - uppercase scheme or host;
   - `%`-encoded and CJK segments;
   - an IDN host;
   - `127.1`;
   - a credential-bearing origin, which must be redacted.

   Task 4 table `repository-location.json`.
4. **The repo root is wrong or missing.** Each case gives the same code, subject, evidence and fixes as Node (root tokenised):
   - not a git repo;
   - a subdirectory of the repo;
   - no `origin` remote;
   - origin mismatch;
   - a missing revision;
   - `git` absent from PATH;
   - one blob over 16 MiB (ENOBUFS stops the loop).

   Task 5.
5. **Erd with `quality_profile: "standard"`, two crossing authored `via` routes, rendered with `RenderOptions(Quality: Showcase)`** fails with `composition/proper-crossing`, exactly as `ARCHIFY_QUALITY_PROFILE=showcase` does. The same input without the option renders the standard golden. Task 3.

---

## File Structure

```
src/Archify.Net.Core/Erd/ErdRenderer.cs              ← UP/renderers/erd/render-erd.mjs (1566); partial if > ~1,800 lines (ErdRenderer.Svg.cs)
src/Archify.Net.Core/Shared/Legend.cs                LegendEntry.Lead (P3)
src/Archify.Net.Core/Shared/Geometry.cs              LabelPoint labelSegment overload (P3)
src/Archify.Net.Core/Shared/Utils.cs                 TrailingBlanks moved from TreeRenderer (P3)
src/Archify.Net.Core/Js/JsUri.cs                     partial Js: EncodeUriComponent, DecodeUriComponent, ToLowerCase (P7)
src/Archify.Net.Core/Evidence/RepositoryLocation.cs  ← UP/renderers/shared/repository-location.mjs (71)
src/Archify.Net.Core/Evidence/GitRunner.cs           runGit + readBatchObjects process layer (P6)
src/Archify.Net.Core/Evidence/RepositoryEvidence.cs  ← UP/renderers/shared/repository-evidence.mjs (393)
src/Archify.Net.Core/Shared/DiagramLoader.cs         Verify call at the end of Load (P4)
src/Archify.Net.Core/RenderContext.cs                SourceEvidence internal setter (P4)
src/Archify.Net.Core/Archify.cs                      Renderers.For erd arm; XML remarks (Task 7)
tests/Archify.Net.Core.Tests/ErdTests.cs             layout reports, receipts, the gateProfile and domain-not-drawn facts
tests/Archify.Net.Core.Tests/RepositoryLocationTests.cs, JsUriTests.cs
tests/Archify.Net.Core.Tests/EvidenceRepo.cs         test helper: builds the fixture repo from the .fi stream (P8)
tests/Archify.Net.Core.Tests/RepositoryEvidenceTests.cs  payload + receipt + integration tests
tests/Archify.Net.Core.Tests/ParityTests.cs          Targets() rows
tests/fixtures/evidence/evidence-repo.fi             the fixture repo stream
tests/fixtures/js/repository-location.json, js-uri.json
tests/fixtures/diagrams/<erd inputs, faults>, tests/fixtures/erd/<name>.layout.json
tests/fixtures/evidence/<scenario>.payload.json | .expected.json
tests/goldens/<new goldens> + checker/<…>.json
tools/capture-fixtures.mjs                           phase-4 blocks (Tasks 1, 4, 5, 6)
README.md, SESSION_HANDOFF.md                        Task 7
```

---

### Task 1: Erd fixtures — inputs, goldens, layout reports, failure receipts (`tools/capture-fixtures.mjs`)

**Files:** Modify `tools/capture-fixtures.mjs` (phase-4 block: inputs and `targets.push` before the render loop, receipts and layout JSON after the phase-3 receipt block); create the files it mints.

**Interfaces:** Produces every erd input, golden, layout report and failure receipt that Tasks 2-3 consume.

- [ ] **Step 1: Copy inputs.**
  - `T/fixtures/erd-first-draft/{blog,shop,warehouse}.erd.json` → `tests/fixtures/diagrams/first-draft.<name>.erd.json`, byte-for-byte with `fs.copyFileSync`.
  - Inline scenes, copied verbatim from the tests into `write()` calls:
    - `T/erd-label-capacity.test.mjs:15-41` `fixture()` and `verticalFixture()` → `label-capacity.erd.json` (as authored) and `label-capacity.showcase.erd.json` (with `meta.quality_profile` flipped as that test does), plus `label-capacity-vertical.erd.json`;
    - `T/erd-i18n.test.mjs:12-25` → `i18n.<locale>.erd.json` for `en`, `zh-CN`, `zh-TW`, `es`, `ko`.

  Read each test, and when a literal comes from a helper, run the helper in Node and write its result. Say how each file was derived in the commit message.
- [ ] **Step 2: Derived inputs.** These are documented edits of `UP/examples/orders.erd.json`. Every edit must be schema-valid:
  - entities use `label`, `tag`, `row`/`col` or `pos`, and attributes `{name, type, key: 'pk'|'fk'|'uk'}`;
  - relationships require `fromCardinality`/`toCardinality` ∈ `one|many`;
  - there is no top-level `domains` and no `lane` property;
  - delete `meta.views` whenever entities are added, removed or renamed (survey-erd §6).

  First guesses below come from agy's Node probe (plan review 2026-10-15); iterate under Node if one misses:
  - `orders.lanes`: `d.relationships.push({ id: 'customer_primary_order', from: 'customer', to: 'order', fromCardinality: 'one', toCardinality: 'one', toOptional: true, identifying: false, variant: 'dashed' })`. Two relationships share a corridor with distinct marker styles, so they get lane offsets (survey-erd §8, probe `lanes`).
  - `orders.seven-domains`: for `i = 3..5` push entities `d{i}_a`/`d{i}_b` tagged `d{i}` at `row: i-1`, cols 0/1, each with `[{ name: 'id', type: 'bigint', key: 'pk' }]`. That gives 4 existing tags + 3 new = 7 domains, so the 7th hue wraps to `database`. Delete `meta.views`.
  - `orders.crossing-standard`: `d.meta.quality_profile = 'standard'`, then push `{ id: 'cross_a', from: 'customer', to: 'product', fromCardinality: 'one', toCardinality: 'many', via: [[330, 120], [330, 260]] }` and `{ id: 'cross_b', from: 'inventory', to: 'order', fromCardinality: 'many', toCardinality: 'one', via: [[200, 226], [450, 226]] }`. It must render under `standard` and fail under `ARCHIFY_QUALITY_PROFILE=showcase` with `composition/proper-crossing`.
- [ ] **Step 3: Targets.**
  - `targets.push` for the three first-draft inputs, the label-capacity inputs that render, the five i18n inputs, `orders.lanes`, `orders.seven-domains` and `orders.crossing-standard`.
  - The two upstream goldens already sit in `upstream/examples/` (`erd-orders-rendered.html`, `subscription-billing-rendered.html`).
  - Write `--layout-json` stdout for every clean input (examples included) to `tests/fixtures/erd/<name>.layout.json`, the same way the class layout loop does (tools/capture-fixtures.mjs ≈:543-560).
- [ ] **Step 4: Failure receipts.** Use the phase-3 `p3Faults` pattern: the script throws if a fault renders or its receipt lacks the target code. Write `tests/fixtures/diagrams/<name>.erd.json` and `<name>.erd.expected.json` for each row below. Each row is a first guess at the edit; iterate under Node until it hits its code, and record any changed edit in the commit message.

  | name | edit of `orders.erd.json` (unless noted) | target code |
  |---|---|---|
  | `erd-domain-scattered` | `d.entities[5].row = 1; d.entities[5].col = 3;` | `erd/domain-not-drawn` (`scattered`) |
  | `erd-domain-pos` | `delete d.entities[5].row; delete d.entities[5].col; d.entities[5].pos = [760, 44];` | `erd/domain-not-drawn` (`no-grid`) |
  | `erd-domain-off-canvas` | `d.layout.origin = [5, 44];` | `erd/domain-not-drawn` (`off-canvas`) |
  | `erd-domain-band-covers` | `d.layout.gapY = 16;` | `erd/domain-not-drawn` (`band-covers-entity`) |
  | `erd-header-capacity` | `d.entities[0].label = 'CUSTOMER_ACCOUNT_DIRECTORY_RECORD_LONG';` | `erd/header-text-capacity` |
  | `erd-attribute-capacity` | `d.entities[0].attributes[1].name = 'primary_verified_contact_email_address';` | `erd/attribute-text-capacity` |
  | `erd-marker-capacity` | `d.entities[0].attributes = [{ name: 'id', type: 'bigint', key: 'pk' }]; for (let i = 0; i < 4; i++) d.relationships.push({ id: 'extra_' + i, from: 'order', to: 'customer', fromCardinality: 'many', toCardinality: 'one' });` | `layout/marker-capacity` |
  | `erd-duplicate-entity` | `d.entities.push({ ...structuredClone(d.entities[0]), row: 2 });` | `layout/constraint` |
  | `erd-self-loop` | `d.relationships.push({ from: 'customer', to: 'customer', fromCardinality: 'one', toCardinality: 'many' });` | `layout/constraint` |
  | `erd-legend-overflow` | `d.meta.viewBox = [980, 480];` | `legend/vertical-overflow` |
  | `erd-unplaced-labelled` | `delete d.entities[0].row; delete d.entities[0].col;` — `customer` is the endpoint of the one labelled relationship (`placed by`); unplacing `product` instead reaches no `labelPoint` and gives the unlabelled receipt | `internal/unclassified` |
  | `erd-unplaced-unlabelled` | the same, plus `for (const r of d.relationships) delete r.label;` | `layout/constraint` + `erd/domain-not-drawn` (Node's full receipt) |
  | `erd-crossing-showcase` | the `orders.crossing-standard` edit, rendered with `ARCHIFY_QUALITY_PROFILE=showcase` | `composition/proper-crossing` |

  The edits come from agy's Node probe in the plan review (2026-10-15), which ran all 13 rows. Where this differs from survey-erd §6, the probe wins. The capture script still asserts every row's code, so a wrong edit fails loudly.

- [ ] **Step 5: Run the capture twice**; the second run changes nothing. Then `dotnet test` (current total, all green; new goldens add checker rows that must pass).
- [ ] **Step 6: Commit.** Run `git add tools/capture-fixtures.mjs tests/fixtures/diagrams tests/fixtures/erd tests/goldens`, check that `git status` shows no modified phase-1/2/3 file, and commit with message `test(fixtures): phase-4 erd inputs, goldens, layout reports and failure receipts`.

---

### Task 2: Erd scene and layout report

**Files:**
- Create `src/Archify.Net.Core/Erd/ErdRenderer.cs` and `tests/Archify.Net.Core.Tests/ErdTests.cs`.
- Modify `Shared/Legend.cs`, `Shared/Geometry.cs`, `Shared/Utils.cs` and `Tree/TreeRenderer.cs` (P3), plus `Archify.cs` (erd arm).

**Interfaces:**
- Consumes `ErdGrid` (`Erd/Grid.cs`; use `GridEntity.From(entity, 0)` for `ResolvedEntityWidth` on authored entities, which closes the SESSION_HANDOFF carried item), `Routing.CreateRouter` with `PreferredCandidates`, `CompositionFloors=false` and `MinimumTerminalSegmentPx`, `AutomaticLabels`, `Legend`, and `LayoutReport.EntityBox`/`ConnectionPath`.
- Produces `public sealed class ErdRenderer : IDiagramRenderer` returning `(svg, cards, new LayoutReport(report))`. In this task the SVG is a stub, `renderSvg` throws `NotImplementedException("Task 3")`, and `validateEr` is a stub that returns. The facts below therefore call an internal `ErdRenderer.BuildLayoutReport(RenderContext)` seam, so they do not need validation or SVG.

Porting rules: read survey-erd §2-§3 and §9 in full. Clusters :57-1020 and :1337-1351 in upstream statement order.
- `preferredCandidates` closes over `trunkAssignments` and `laneOffsets`, so both are **instance fields** read lazily.
- Map/Set order and the :751 delete (no re-add) follow §3.
- `[from, to].sort` and the string comparator with `\0` follow §3.
- Port the literal quirks at :821-823, :894, :841-844 and :993 as they are.
- `via: []` counts as authored at :648 but not at :175.
- Gates get `routableRelationships` with **nulls** for self-loops, so authored indexes stay put.

This is the first time the router's `preferredCandidates`, `compositionFloors:false` and `minimumTerminalSegmentPx` paths meet a golden. When a route differs, check those Routing.cs paths against routing.mjs before suspecting erd code. Any Routing.cs change must keep every phase-1-3 golden byte-identical.

- [ ] **Step 1: Failing facts.** Add a `[Theory] Erd_layout_report_equals_upstream_layout_json` over every `tests/fixtures/erd/*.layout.json`, asserting `Js.Stringify(report, 2) + "\n"` equals the file.
- [ ] **Step 2:** Run the facts and confirm they fail.
- [ ] **Step 3:** Port the clusters.
- [ ] **Step 4:** Make the facts pass, then run the full suite.
- [ ] **Step 5: Commit.** Stage the files above and commit with message `feat(core): erd scene and layout report (equals upstream --layout-json)`.

---

### Task 3: Erd validation, SVG and goldens

**Files:**
- Modify `Erd/ErdRenderer.cs` (replace the Task 2 stubs; add a partial `ErdRenderer.Svg.cs` if the file grows past ~1,800 lines).
- Modify `ParityTests.cs` and `ErdTests.cs`.

Porting rules:
- Read survey-erd §3, §6 and §9 (output whitespace).
- `validateEr` (:1022-1335) matches typed details to problems by message, as upstream does.
- `layout/marker-capacity`'s path uses the entity **id** (`/entities/${entityId}`).
- The authored list and the `entities` Map are both iterated, each where upstream uses it.
- SVG cluster :1353-1551: the trailing-blank strip via `Utils.TrailingBlanks`, `.filter(Boolean).join('\n')`, entities joined with `'\n\n'`, the cardinality defs prefix only when a marker is used, and the legend `lead` divider.
- Legend errors are thrown inside `renderSvg` (unfit `'error'` when `meta.legend` is authored).

- [ ] **Step 1: Rows and facts.**
  - Golden rows:
    - `["erd", Upstream("examples/orders.erd.json"), Upstream("examples/erd-orders-rendered.html")]`
    - `["erd", Upstream("examples/subscription-billing.erd.json"), Upstream("examples/subscription-billing-rendered.html")]`
    - one row per Task 1 golden.
  - `[Theory] Erd_fault_diagnostics_match_upstream_receipt` uses `AssertReceipt` for every `erd-*` fault except `erd-unplaced-labelled` and `erd-crossing-showcase`. `erd-domain-scattered` is the card's `erd/domain-not-drawn` diagnostic-code test.
  - For `erd-unplaced-labelled`, assert the code `internal/unclassified` only (P2).
  - Review Focus 5 fact:

    ```csharp
    [Fact] public void Erd_quality_option_reaches_the_gates_like_the_env_var()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/orders.crossing-standard.erd.json")));
        Assert.Equal(Fixtures.Lf(Fixtures.Golden("orders.crossing-standard-rendered.html")), ArchifyNet.Render(DiagramType.Erd, d).Html);
        var r = ArchifyNet.Render(DiagramType.Erd, d, new RenderOptions(Quality: QualityProfile.Showcase));
        Assert.Equal("", r.Html);
        ParityTests.AssertReceipt(JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/erd-crossing-showcase.erd.expected.json")))!, r);
    }
    ```

    `ParityTests.AssertReceipt` is `private` today, so make it `internal` in this task. Task 5 adds a diagnostics-list overload.
- [ ] **Step 2:** Run → fail. **Step 3:** Port. **Step 4:** Iterate to green with `FirstDifference`, then run the full suite.
- [ ] **Step 5: Commit.** Commit with message `feat(core): erd renderer — goldens byte-identical, diagnostics match upstream`.

---

### Task 4: `RepositoryLocation` and the `Js` URI helpers (pure)

**Files:**
- Create `Js/JsUri.cs`, `Evidence/RepositoryLocation.cs`, `JsUriTests.cs`, `RepositoryLocationTests.cs`, `tests/fixtures/js/js-uri.json` and `tests/fixtures/js/repository-location.json`.
- Modify `tools/capture-fixtures.mjs`.

**Interfaces:**
- `Js.EncodeUriComponent(string)`, `Js.DecodeUriComponent(string) → string?` (null where JS throws), and `Js.ToLowerCase(string)`.
- `internal static class RepositoryLocation` with:
  - `Parse(string value, bool authored, string? provider) → ParsedRemote?`, which carries `Identity`, `Url`, `Provider`, `Protocol`, `Path`, `Endpoint` and `PathKind`;
  - `Redact(string)`, `SourceHref(...)` and `TreeHref(...)`, each mirroring repository-location.mjs one to one.

Porting rules: survey-evidence §1c and §7, and P7.

- [ ] **Step 1: Capture block.** Import `UP/renderers/shared/repository-location.mjs` directly; it is pure. Write rows for:
  - every URL spelling in `T/repository-evidence.test.mjs:211-482`;
  - the survey's §7 probe rows: IDN, `127.1`, `:443`/`:80`, ssh `:22` and upper-case host, `[::1]`, a trailing-dot host, an invalid port, `%zz`, `%2F`, CJK segments;
  - `constructor`/`__proto__` hosts;
  - credentials.

  Each `parseRepositoryRemote` row records `authored` true and false and every provider hint. Add `redactRepositoryRemote` rows and `repositorySourceHref`/`repositoryTreeHref` rows (github, gitee, gitlab nested, `.md` with a line, `endLine === line`).

  For `js-uri.json`, record `encodeURIComponent` / `decodeURIComponent` (throw → `null`) / `toLowerCase` on a battery covering ASCII punctuation, `!*'()~`, CJK, emoji, `%`-escapes (valid, invalid, overlong, surrogate), `İ`, `ß` and `ǅ`.
- [ ] **Step 2:** Failing theories. **Step 3:** Implement. **Step 4:** Green, then the full suite.
- [ ] **Step 5: Commit** with message `feat(evidence): RepositoryLocation and Js URI helpers (Node-pinned)`.

---

### Task 5: `GitRunner`, `RepositoryEvidence.Verify`, the fixture repo, and the loader wiring

**Files:**
- Create `Evidence/GitRunner.cs`, `Evidence/RepositoryEvidence.cs`, `tests/fixtures/evidence/evidence-repo.fi`, `tests/Archify.Net.Core.Tests/EvidenceRepo.cs` and `RepositoryEvidenceTests.cs`.
- Modify `Shared/DiagramLoader.cs`, `RenderContext.cs` and `tools/capture-fixtures.mjs`.

**Interfaces:**
- `internal static class RepositoryEvidence { JsonObject? Verify(DiagramType type, JsonObject diagram, string? repoRoot, DiagnosticSink sink) }`: the upstream statement order of survey-evidence §1b. It throws through the sink exactly as upstream's `throwDiagnosticError` does.
- `internal sealed class GitRunner` (P6).
- Test helper `EvidenceRepo.Create(string originUrl) → (string Root, string Head)` (P8).

Content of the fixture repo:
- `src/router.js` (3 lines) and `src/store.js` (1 line), the upstream helper's files (T/helpers/repository-evidence-fixture.mjs:15-40);
- a `docs/readme.md` with 5 lines, for gitlab's `?plain=1`;
- a `src/中文 # router.js` (2 lines), for path encoding (T/repository-evidence.test.mjs:465-482);
- a submodule-free tree;
- a fixed author/committer (`Archify Fixture <fixture@example.invalid>`), dates `1767225600 +0000`, and the message `evidence fixture`.

Write the `.fi` with LF line endings and exact `data <n>` byte counts. `.gitattributes` already marks `tests/fixtures/**` `-text`; check that it covers `.fi`.

- [ ] **Step 1: Fixture stream and helper.**
  1. Write `evidence-repo.fi`.
  2. Build it once and record HEAD as `EvidenceRepo.PinnedHead`.
  3. Add a fact that the stream builds to that HEAD twice, in two temp directories.
- [ ] **Step 2: Capture block.** It builds the same repo from the same `.fi`.
  - Run the verification in a **child `node` process** with `ARCHIFY_DIAGNOSTIC_FORMAT=json` in its environment. The capture script has already loaded `diagnostics.mjs` through `geometry.mjs`, and that module reads the variable once at import (diagnostics.mjs:4), so setting it in-process does nothing.
  - The child script imports `verifyRepositoryEvidence` and `withIsolatedDiagnosticRecording` from `UP/renderers/shared/`, runs `withIsolatedDiagnosticRecording(() => verifyRepositoryEvidence(type, diagram, root))`, and prints either `{ payload }` or, in `catch (e)`, `{ diagnostics: e.archifyDiagnostics }`.
  - **Root tokenisation happens on parsed string values, never on raw JSON text.** Replace the root in its OS-separator form (`realRoot`, e.g. `C:\…\x`) and its forward-slash form (git's `--show-toplevel`, e.g. `C:/…/x`) with `<ROOT>` in every string. The C# test does exactly the same.

  For each scenario it writes `tests/fixtures/evidence/<name>.payload.json` (success) or `<name>.expected.json` (diagnostics):

  | scenario | setup | outcome |
  |---|---|---|
  | `github-web` | `meta.repository = {url:'https://github.com/example/evidence-repo', revision: HEAD}`, origin `git@github.com:example/evidence-repo.git`, sources on `components[0]` (router 1-3 "Request router", store 1) | payload |
  | `gitee-web`, `gitlab-nested-web`, `local-only` | the provider rotation of T/repository-evidence-types.test.mjs:261-302 | payload |
  | `cjk-path` | source `src/中文 # router.js` line 2 | payload |
  | `md-gitlab-plain` | gitlab, `docs/readme.md` line 2 | payload |
  | `repository-required`, `root-required`, `url-invalid-local-path`, `provider-invalid`, `links-unsupported` | pure | receipt |
  | `not-a-repo` | root = an empty temp dir | `git-command` |
  | `subdirectory-root` | root = `<repo>/src` (create the directory) | `root-not-top-level` |
  | `no-origin` | origin removed | `git-command` |
  | `origin-mismatch` | origin `git@github.com:example/other.git` | `origin-mismatch` |
  | `revision-unavailable` | revision `0`×40 | `revision-unavailable` |
  | `missing-then-escape` | sources `[src/nope.js, ../x]` | `[file-missing, path-escape]` in that order (T/…:37-49) |
  | `two-out-of-range` | lines beyond EOF on router and store | two `line-out-of-range` (T/…:51-78) |
  | `line-required`, `line-range-invalid`, `source-required` | | receipt |

- [ ] **Step 3: Failing tests.**
  - For each scenario, call `RepositoryEvidence.Verify` against `EvidenceRepo.Create(...)`.
    - On success, compare the payload with `Js.Stringify` equality.
    - On failure, catch the diagnostic exception and compare its diagnostics with `ParityTests.AssertReceipt(expected, IReadOnlyList<Diagnostic>)` after the same string-value tokenisation. `ParityTests.AssertReceipt` is private today and takes a `RenderResult`: make it `internal` and add this overload, with the `RenderResult` overload delegating to it.
  - Plus C#-only integration facts:
    - **git absent:** start with a PATH that excludes git (a `GitRunner` test seam taking the executable path is acceptable) → `git-unavailable` with `Could not run Git: spawnSync git ENOENT`;
    - **ENOBUFS:** a 17 MiB blob added to a temp clone, with a `line` on it → `[…, git-unavailable "…ENOBUFS"]`, and the loop stops (T/…:93-112);
    - **replace refs:** a `git replace` on the commit does not change the verdict;
    - **dirty work tree:** edits in the work tree do not change the verdict;
    - **path-only sources** never run `cat-file --batch` or `show` (assert via `GIT_TRACE` to a temp file, as T/…:80-91).
- [ ] **Step 4: Wire it.**
  - Call `Verify` at the end of `DiagramLoader.Load` and set `ctx.SourceEvidence`.
  - Add a fact that a diagram without evidence spawns no git process, by injecting a `GitRunner` that throws on use.
  - Confirm the workflow compile threads `SourceEvidence` into nested compiles as upstream does (survey-evidence §3).
- [ ] **Step 5:** Green, then the full suite. Every existing golden must stay byte-identical, because none of them has evidence.
- [ ] **Step 6: Commit** with message `feat(evidence): repository source evidence via git (payloads and receipts match upstream)`.

---

### Task 6: Evidence goldens for all ten types, including badge geometry

**Files:** Modify `tools/capture-fixtures.mjs`, `ParityTests.cs`, and `RepositoryEvidenceTests.cs` if a helper is needed.

- [ ] **Step 1: Capture block.**
  - For each type, take the type's first example plus `meta.repository` (github, `revision: EvidenceRepo HEAD`) plus `sources` on the collection's first node (T/repository-evidence-types.test.mjs:19-30 shape). Render it with `ARCHIFY_REPO_ROOT=<fixture clone>` into `tests/goldens/<type>.evidence-rendered.html`, and write the input to `tests/fixtures/diagrams/<type>.evidence.<type>.json`.
  - Add `lifecycle.evidence-narrow`: a lifecycle example whose first state has `sources` and a label long enough, on a narrow node, to reach the compact-leading fallback (text-fit.mjs:80-96). Iterate the edit under an instrumented copy, as in phase 3, until the branch is reached, and say how in the commit.
  - Add one workflow input whose fix-verification recompile runs while evidence is present. Clone the phase-3 fault `wf-node-overlap` (its fixes are verified by recompiles; `wf-duplicate-node-id` has none). Add `meta.repository` (github, fixture HEAD) and `nodes[0].sources = [{ path: 'src/router.js', line: 1 }]`, capture its receipt with `ARCHIFY_REPO_ROOT` set to the fixture clone, and in C# render it with `RenderOptions(RepoRoot: …)`. Without `meta.repository` and a root, evidence fails first with `repository-required` / `root-required`, and the recompile path is never reached.
- [ ] **Step 2: Rows.** A `[Theory]` renders each input with `new RenderOptions(RepoRoot: EvidenceRepo.Create(origin).Root)` and compares it with the golden. The payload contains no paths, so the temp root does not leak into the HTML.
- [ ] **Step 3:** Run → fail where badges or payloads differ. **Step 4:** Fix the renderer badge sites (DataflowCompiler.cs:124, LifecycleRenderer.cs:235/434/1390, TreeRenderer.cs:383, WorkflowCompilation.Svg.cs:233, `TextFit` source branches) until green, then run the full suite.
- [ ] **Step 5: Commit** with message `test(evidence): per-type evidence goldens — badges and payload byte-identical`.

---

### Task 7: Docs, handoff, deviations

**Files:** `README.md`, `SESSION_HANDOFF.md`, `src/Archify.Net.Core/Archify.cs` (XML remarks).

- Status: phase 4 Done; the diagram-types table lists erd as ported.
- Phase-4 parity results with measured counts.
- A "Source evidence" section covering `RenderOptions.RepoRoot`, git on PATH, and what is verified.
- Known deviations:
  - P2 (`internal/unclassified` evidence names the .NET exception);
  - P5 (`root-identity-indeterminate` unreachable);
  - P6 (synthesised errno strings; an unknown errno prints its number);
  - any P7 URL rows the port could not match.
- Handoff: close the erd-entity-width carried item, list coverage gaps as a TEST card the controller mints, and point "Next phase" at PORT-005.
- `dotnet test` green. Commit with message `docs: phase 4 — erd and repository evidence`.

## Exit (matches card PORT-004)

- `erd-orders` and `subscription-billing` goldens are byte-identical.
- The source-evidence fixture repo lives under `tests/fixtures` (`evidence-repo.fi`), with Node-minted goldens for all ten types.
- `erd/domain-not-drawn` has a diagnostic-code test (`erd-domain-scattered` and three more reasons).
- `dotnet test` is green with no warnings.
