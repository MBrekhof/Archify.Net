# Archify C# Port — Phase 3: workflow + dataflow renderers

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ArchifyNet.Render(type, json)` produces HTML byte-identical to upstream Node archify 3.1.0 for `workflow` and `dataflow`: 2 upstream goldens, 4 Node-minted example goldens and every upstream phase-3 regression fixture (28 inputs from `C:\projects\archify\test\fixtures`, all of which render cleanly under Node), plus failure receipts per diagnostic code, the workflow compiler receipt (upstream's `--layout-json` output) per input, and the fixtures that pin the new `Js.WordBreaks` and the two dataflow helper modules.

**Architecture:** As phases 1–2: one C# file per upstream `.mjs` (same base name, PascalCased functions, same statement order), JS semantics only through `Js`/`JsMap`/`JsSet`. Two shapes are new:
- **Dataflow** is a `DataflowCompiler` (`compileDataflow` = `build(rebalancePorts)` + `attempt` + one retry) plus two pure helper classes (`LabelCapacity`, `JointLabelRouting`). `DataflowRenderer : IDiagramRenderer` is the 23-line `render-dataflow.mjs`.
- **Workflow** is one 5.9k-line closure upstream (`compileWorkflowInternal`, workflow-compiler.mjs:1362-5644), re-entered recursively for fix discovery. It becomes a **per-compile class `WorkflowCompilation`** whose constructor runs the closure's top-level statements in source order and whose `Run()` is the driver at :5609, split across `partial` files by cluster so each task owns whole files. A canonical model class `WorkflowDoc` replaces the spread copies JSON nodes cannot express. `WorkflowCompiler.Compile` (= `compileWorkflow`) is the internal seam every task tests through: it returns the same receipt upstream prints with `--layout-json`.

**Tech Stack:** .NET 10, C# 14, `System.Text.Json.Nodes`, no runtime NuGet packages; xUnit 2.9.3. Node 24 (dev-time only) mints every expected value from upstream (`C:\projects\archify` @ `c21c31ff`).

**Spec:** `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (porting-order step 3; `Js` table; the CJK decision). Templates and settled rulings: the phase-1 plan + `2026-10-13-phase1-rulings.md`, the phase-2 plan `2026-10-14-phase2-renderers.md` (its Global Constraints and Scene conventions apply unchanged unless overridden below). Upstream surveys this plan is built from (git-ignored, on disk): `.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/survey-workflow.md` and `survey-dataflow.md` — every porting rule below cites upstream lines; the surveys hold the complete hazard lists and every implementer reads the survey for their type in full before starting. `UP/` = `C:\projects\archify\archify\`; `T/` = `C:\projects\archify\test\`. Board card: `PORT-003` (CARD-2168).

## Global Constraints

Every phase-1/phase-2 global constraint applies verbatim (phase-2 plan lines 15-29: net10.0, strict byte parity, `Js.Num`/`Js.Sort`/`JsMap`/`JsSet`/`Js.Trim`/`Js.EscapeHtml`/`Js.ToNumber`, `||` vs `??` literally, no LINQ `Max`/`Min` on possibly empty sequences, ECMAScript regexes, StringBuilder SVG, UTF-8 without BOM + LF, `upstream/` and `tests/goldens/` never hand-edited, diagnostics returned not thrown, no NuGet dependency, every `Js` member carries `// ponytail: V8 parity` and a fixture-backed test, commits via `git commit -F` with an exact `git add` list, board-only task state). The phase-2 Scene conventions (plan line 38) apply. Additions and overrides for phase 3:

- **R1 — `Js.WordBreaks` becomes a UAX #29 word-boundary subset over grapheme clusters** (Task 2). Upstream `label-capacity.mjs:32-33` feeds *any* flow label to `Intl.Segmenter(undefined, {granularity:'word'})`; phase 2's per-grapheme rule splits Latin words (`clickstre|am events`). The new rule: WB3–WB13b over `Js.Graphemes` — **no** word-level WB15/16 (`Js.Graphemes` already pairs regional indicators per GB12/13, so `🇩🇪🇫🇷` is two clusters and must stay two words), Format/Extend clusters riding on the previous cluster (WB4) **except** clusters containing U+200B/U+200C/U+200D and except after a Newline or WSegSpace cluster (uax29proto.mjs:35-38; .NET reports U+200B as `Format`, but V8 splits `a​b` into three), classes from general category plus the explicit lists in the prototype `.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/uax29proto.mjs`, **Hangul as its own class** (ICU tailoring: Hangul never joins Latin, digits or MidLetter), and Han/Hiragana/Katakana(incl. `ー`,`ｰ`)/Thai/Lao/Khmer/Myanmar **one segment per grapheme**. Measured against V8: 0 mismatches on 6,286 strings without a dictionary script. Sequence is unaffected (its only input is a Han+Mark run, still per grapheme).
  **Stated deviation (extends the phase-2 CJK exemption; README + `ArchifyNet.Render` XML remarks, Task 12):** ICU segments Han, Kana, Thai, Lao, Khmer and Myanmar with a dictionary; the port splits them per grapheme. Observable in phase 3 only where a blocked automatic-showcase dataflow label containing those scripts is wrapped onto two lines (`mixed-port-recovery`: V8 `已持有：处理|完成或失败`, port `已持有：处|理完成或失败`). Upstream's word segmenter also follows the host default locale; the port assumes a non-POSIX locale (a `LANG=C` POSIX host splits `api.example.com` at `.`).
- **R2 — `Box.Cx`/`Box.Cy` become storable** (Task 6). Workflow stores `cx = layout.colXs[col]` and derives `x = cx - width/2` (workflow-compiler.mjs:989, 999-1001); recomputing `X + Width/2` differs in ~4.5% of two-decimal widths (`252.6` → `252.59999999999997`), which moves anchors. `Box` keeps the computed value unless a renderer sets it:
  ```csharp
  double? cx, cy;
  public double Cx { get => cx ?? X + Width / 2; set => cx = value; }
  public double Cy { get => cy ?? Y + Height / 2; set => cy = value; }
  ```
  No phase-1/2 renderer sets them, so their goldens cannot change (the full suite proves it).
- **R3 — `LabelRect.Lines`** (`IReadOnlyList<string>? Lines { get; set; }`, Task 3): label-capacity.mjs:35/:71 spreads `{lines, width, height}` into a rect and dataflow-compiler.mjs:1485-1488 renders `rect?.lines`. `Clone()` (MemberwiseClone) shares the list, which matches the JS spread (the array is never mutated).
- **R4 — quality profile for both types: one resolved value, used everywhere.** Upstream passes `process.env.ARCHIFY_QUALITY_PROFILE || meta.quality_profile` into the compiler (render-workflow.mjs:18, render-dataflow.mjs:17). The port passes `qualityProfile = ctx.Options.Quality?.Name() ?? meta.Str("quality_profile")`.
  - **Workflow** rewrites `meta.quality_profile` to the resolved value on its quality-resolved copy (workflow-compiler.mjs:1382-1386) before anything reads it, so **every** later `meta.quality_profile` read in `WorkflowCompilation` — including the ones phase 2 would have kept meta-only (2088, 2903, 5358, 5697, 5808, 5891) — sees the resolved value, and every gate takes it with `profileIsAuthoritative: true`. Nested compiles pass what upstream passes (`resolvedQualityProfile` at :1596; **`authoredQualityProfile`** at :1099; `options.qualityProfile` at :5845/:5873/:5899).
  - **Dataflow** uses the resolved value for `automaticShowcase` (dataflow-compiler.mjs:83-86) and `gateProfile` (the same value) for the six gates (:485, :495, :505, :514, :540, :548). There is no other quality read.
  This **overrides** phase 2's "direct meta reads stay meta-only" for these two types (upstream's test `T/dataflow-automatic-layout.test.mjs:337-378` shows the env switch changes the whole layout). Pinned by Review Focus 2 and 3.
- **R5 — `RenderResult.Layout`:** workflow returns `new LayoutReport(receipt)` on success, where `receipt` is the compiler's receipt (render-workflow.mjs:30-34 prints exactly that for `--layout-json`); phase 5's CLI prints `Js.Stringify(layout.Report, 2)`. Dataflow has no `--layout-json` → `null`. A failed render keeps `Layout = null` (existing README deviation).
- **R6 — workflow statement order and re-entrancy.** `WorkflowCompilation` takes a `WorkflowDoc` and options as parameters and **never reads `ctx.Diagram`/`ctx.Meta`** (nested compiles run on clones). It shares `ctx.Diagnostics` (suppression depth) and `ctx.Brands`. Its constructor runs workflow-compiler.mjs:1370-5059's top-level statements **in source order**: `automaticPorts = automaticPortSpread(...)` (:4466) runs during construction, fills `readableSideCache` for every v2 edge and lazily inserts scene obstacles into the grid before any route exists; grid item order (nodes → scene items on first `sceneLabelObstacles()` → routes/labels in registration order) is observable through `readableFreeSpacePlans`' obstacle list. Fields that upstream declares later (`LABEL_RECT_CACHE` :5130, `obstacleSequence` :5060) are never read earlier.
- **R7 — `WorkflowDoc`, the canonical model** (Task 6): `{ JsonObject Meta; List<JsonObject> Lanes, Nodes, Edges, Phases, Groups; JsonArray? MainPath; JsonObject? SemanticChecks; JsonArray? Cards; int SchemaVersion; bool? AbsoluteGeometryPolicy }`. A `JsonObject` cannot sit in two `JsonArray`s, but a `List<JsonObject>` can hold the authored references, which is what upstream's `{...workflow, nodes: sorted}` does (:732-738). `sourceIndexes` (:1419-1423) key on those references. `Clone()` = upstream `cloneWorkflow` = `JSON.parse(JSON.stringify(...))` → `JsonNode.Parse(Js.Stringify(doc.ToJson()))` (drops `undefined`, turns `-0` into `0`); the `AbsoluteGeometryPolicy` flag is upstream's Symbol side channel (workflow-migration-geometry.mjs:8, 27) and is copied explicitly where upstream copies own Symbols (:1588-1590, :5870). Brand marks are **not** copied: upstream's brand Symbols die in the JSON clone, so fix-verification compiles see no brands; `ctx.Brands.BrandMarkFor(clonedNode)` misses naturally.
- **R8 — `automaticNodeWidths`** (module-level `WeakMap` keyed by node object, :170, rewritten per `canonicalReadableWorkflow` :709-715, read by `authoredNodeWidth` :181 and `measureNode` :987): a `ConditionalWeakTable<JsonObject, StrongBox<double>>` owned by `WorkflowCompiler` for one top-level `Compile` call and passed to nested compiles. Never a field on `WorkflowDoc`. The `readableMigrationProvidesCapacity` → `createReadableLayout({...workflow, schema_version: 2})` path (:1093) reads it for nodes never canonicalised as v2 → miss → `?? 92`.
- **R9 — identity, not value, wherever upstream compares objects.** `===` on edges, routes, label rects, point arrays and result arrays (workflow survey §3 "Object identity"; dataflow survey §4 "Identity compares") → `ReferenceEquals` (or class types). C# `record` `==` is value equality and is **wrong** there. Point identity at workflow-compiler.mjs:4765 (`alternativeStart === start`) → compare candidate indices (`i == 0 && j == 0`), not `Pt` values. `repairLabelRoutes` must return the **same list instance** when nothing changed (dataflow-compiler.mjs:1365).
- **R10 — exceptions as control flow are ported literally.** `WorkflowLayoutFeedback : Exception` (:114-121) unwinds out of a compile and is caught at :5732/:5756; `readableAutomaticRoute` catches **every** non-feedback exception into `firstFailure` (:4784-4787, :4820-4825) and rethrows only when no plan exists (:4932) — port as `catch (Exception)` the same way; `clearStraightAutomaticPlan` swallows feedback and diagnostic errors (:2367-2371).
- **R11 — diagnostics recording.** Workflow: `ArchifyNet`'s pipeline already calls the renderer inside the facade's sink; `WorkflowRenderer` wraps `Compile` in `ctx.Diagnostics.WithIsolatedRecording` (render-workflow.mjs:16) and on `!ok` re-records the compile's diagnostics and throws `ThrowDiagnosticError(error ?? "Workflow compilation failed.", diagnostics)` (:37). **A successful render publishes no compile diagnostics** (warnings live only in the receipt). Dataflow: each `attempt` is isolated and rewrites `archifyDiagnostics` to what that attempt recorded (dataflow-compiler.mjs:1579-1587); the retry rethrows the **initial** error when `attempt(true)` returns null (:1589-1604).
- **R12 — every expected value comes from Node.** New fixture inputs are copied from `T/fixtures` or derived by a documented edit of an upstream example inside `tools/capture-fixtures.mjs`; a crafted input that Node refuses or that misses its target code is a fixture bug (fix the edit, never the oracle) and the capture script **throws** on it. The script stays deterministic and re-runnable: a second run must leave `git status` unchanged.

## Review Focus

Spec-implied inputs that no upstream *example* exercises (verified against the 6 examples: none has a `brand`, `meta.viewBox` on workflow, a `standard` profile, a quality option override, a wrapped flow label, or a Latin word-wrapped label), each pinned in its owning task:

1. **A blocked automatic-showcase flow label in Latin script** (`"clickstream events"`, `"save / copy result"`, `"api.example.com sync"`) → wraps at V8's word boundaries, never inside a word. `tests/fixtures/js/label-capacity.json` rows (upstream test scenes, T/dataflow-label-capacity.test.mjs:5-60) in Task 3, plus `word-breaks.json` in Task 2.
2. **Dataflow with `meta.quality_profile: "standard"` rendered with `RenderOptions(Quality: Showcase)`** → the automatic layout switches on exactly as `ARCHIFY_QUALITY_PROFILE=showcase` does under Node. Fixture golden `five-stage.option-showcase-rendered.html` (minted with the env var), Task 5.
3. **Workflow example (`showcase`) rendered with `RenderOptions(Quality: Standard)`** → every gate and every direct quality read sees `standard` (R4). Fixture golden `agent-tool-call.option-standard-rendered.html` (minted with `ARCHIFY_QUALITY_PROFILE=standard`), Task 11.
4. **Workflow and dataflow nodes with built-in brand marks** → brand rail, label fit and `data-node-brand-*` passport equal Node; fix-verification compiles see no brands (R7). Fixture goldens `holiday-planning.brand-rendered.html` and `product-analytics.brand-rendered.html`, Tasks 11 and 4.
5. **Workflow inputs that fail with fixable problems** — duplicate node id, unknown edge endpoint, unknown lane, overlapping nodes, a single conflicting pin, too small a `meta.viewBox` → the (code, path) set and `supportedFixes` (from verified recompiles) equal Node's. Failure receipts, Tasks 7, 9, 10.

---

## File Structure

```
src/Archify.Net.Core/Js/JsText.cs                       WordBreaks rewritten (R1) + small script-range tables
src/Archify.Net.Core/Shared/Geometry.cs                 Box.Cx/Cy storable (R2); LabelRect.Lines (R3)
src/Archify.Net.Core/Dataflow/LabelCapacity.cs          ← UP/renderers/dataflow/label-capacity.mjs (81)
src/Archify.Net.Core/Dataflow/JointLabelRouting.cs      ← UP/renderers/dataflow/joint-label-routing.mjs (165)
src/Archify.Net.Core/Dataflow/DataflowCompiler.cs       ← dataflow-compiler.mjs clusters A, D–G, J–L, O-width, P + attempt/retry (Task 4)
src/Archify.Net.Core/Dataflow/DataflowCompiler.Automatic.cs  ← clusters B, C, H, I, M, N, O-joint (partial class, Task 5)
src/Archify.Net.Core/Dataflow/DataflowRenderer.cs       ← render-dataflow.mjs (23)
src/Archify.Net.Core/Workflow/WorkflowDoc.cs            canonical model (R7)
src/Archify.Net.Core/Workflow/WorkflowModel.cs          ← workflow-compiler.mjs:80-859 (clusters A, B, C: helpers, createReadableLayout, canonicalisation, semantic contract)
src/Archify.Net.Core/Workflow/WorkflowScene.cs          ← :860-1063 (cluster D: legend footprint, lane geometry, node measurement, step indexes)
src/Archify.Net.Core/Workflow/WorkflowMigrationGeometry.cs ← workflow-migration-geometry.mjs (157)
src/Archify.Net.Core/Workflow/WorkflowPlacementRepair.cs   ← workflow-placement-repair.mjs (94)
src/Archify.Net.Core/Workflow/WorkflowCompiler.cs       public-facing internal seam: Compile + feedback wrappers (cluster O)
src/Archify.Net.Core/Workflow/WorkflowCompilation.cs    partial: preamble + scene state (G), validateWorkflow (I1), route primitives (J1, J2), routeVia/edgeSides (K3 v1, K4), pathFor (K8), canvas (L), SVG (M), receipt + driver (N)
src/Archify.Net.Core/Workflow/WorkflowCompilation.Routing.cs   partial: F, H4, J3, K1, K2, K4-v2, K5, K6 (readable-v2 automatic routing)
src/Archify.Net.Core/Workflow/WorkflowCompilation.Inputs.cs    partial: validateReadableInputsBeforeRouting (I2)
src/Archify.Net.Core/Workflow/WorkflowCompilation.Pins.cs      partial: H1, H2, H3, H5 rest, K3 readablePresetVia, K7, E (legacy capacity)
src/Archify.Net.Core/Workflow/WorkflowRenderer.cs       ← render-workflow.mjs (47)
src/Archify.Net.Core/Archify.cs                         Renderers.For gains two arms
tests/Archify.Net.Core.Tests/JsTextTests.cs             + word-breaks fixture facts
tests/Archify.Net.Core.Tests/DataflowHelperTests.cs     label-capacity + joint-label-routing fixtures
tests/Archify.Net.Core.Tests/WorkflowModelTests.cs      shim-export fixtures (layout, canonical order, semantic diagnostics, measurement, MG, PR)
tests/Archify.Net.Core.Tests/WorkflowReceiptTests.cs    compiler receipts vs Node
tests/Archify.Net.Core.Tests/ParityTests.cs             Targets() rows + receipt theories
tests/Archify.Net.Core.Tests/ValidationTests.cs         + the two golden.mjs workflow cases (Task 11)
tests/fixtures/js/word-breaks.json, label-capacity.json, joint-label-routing.json, workflow-model.json
tests/fixtures/diagrams/phase3-manifest.json            one row per phase-3 input: features, coverage, owning task (Task 1)
tests/fixtures/diagrams/<phase-3 inputs>                copied/derived inputs (Task 1)
tests/fixtures/workflow/<name>.receipt.json             Node compileWorkflow receipts (Task 1)
tests/goldens/<name>-rendered.html + checker/<name>.json
tools/capture-fixtures.mjs                              phase-3 blocks (Tasks 1, 2, 3, 6)
README.md, SESSION_HANDOFF.md                           Task 12
```

Shared-module gaps: **none** (both surveys checked every import by name; `SpatialGrid`, `RouteQuality.ShortestOrthogonalGridRoute` and every geometry gate with its workflow/dataflow-only options already exist). The gaps are the four local modules above plus R2/R3.

`joinRoutePoints` (phase-1 carried item, phase-2 handoff): the identity memo (geometry.mjs:1327-1374) is **unobservable** in both renderers — no point array is mutated after normalisation, and no code compares normalised arrays by identity (workflow survey §3 last section, dataflow survey §4). `Geometry.JoinRoutePoints`/`NormalizeRoutePoints` stay as they are; Task 8 adds one belt-and-braces fact (`NormalizeRoutePoints(JoinRoutePoints(a, via, b))` equals `JoinRoutePoints(a, via, b)` over the 17 candidate shapes of a receipt run) and the handoff closes the carried item.

---

### Task 1: Phase-3 inputs, goldens, receipts and the manifest (`tools/capture-fixtures.mjs`)

**Files:**
- Modify: `tools/capture-fixtures.mjs` (two new blocks: inputs+targets **before** the render loop at :350; receipts **after** the waterfall block, before the checker-dir loop at :545)
- Create (by running it): `tests/fixtures/diagrams/*` phase-3 inputs, `tests/fixtures/diagrams/phase3-manifest.json`, `tests/fixtures/workflow/*.receipt.json`, failure receipts `*.workflow.expected.json` / `*.dataflow.expected.json`, goldens + checker JSONs

**Interfaces:**
- Produces: every input, golden, receipt and the manifest Tasks 4–11 consume. Row names below are the contract; the owning task of each golden is decided by the manifest rule (Step 4), not guessed.

- [ ] **Step 1: Copy the 28 upstream regression inputs** (all render under Node; checked 2026-10-14). Prefix by source folder so names never collide with examples:

```js
// --- phase 3: upstream regression inputs (C:\projects\archify\test\fixtures), copied verbatim, renamed by folder ---
const TF = 'C:/projects/archify/test/fixtures';
const phase3Copies = [
  ['workflow', 'automatic-routing-node-border-clearance.workflow.json', 'border-clearance.workflow.json'],
  ['workflow', 'v1-workflow-700x400.workflow.json', 'v1-700x400.workflow.json'],
  ['workflow', 'v1-workflow-explicit-coordinates.workflow.json', 'v1-explicit-coordinates.workflow.json'],
  ['workflow', 'workflow-dense-automatic.json', 'dense-automatic.workflow.json'],
  ['workflow', 'workflow-fanin-label-recovery.json', 'fanin-label-recovery.workflow.json'],
  ['workflow', 'workflow-free-space.json', 'free-space.workflow.json'],
  ['workflow', 'workflow-return-arrival.json', 'return-arrival.workflow.json'],
  ['workflow', 'workflow-route-text-recovery.json', 'route-text-recovery.workflow.json'],
  ['workflow', 'workflow-routing-clarity.workflow.json', 'routing-clarity.workflow.json'],
  ['workflow', 'workflow-shared-trunks.workflow.json', 'shared-trunks.workflow.json'],
  ['workflow', 'v1-baseline/agent-tool-call.workflow.json', 'v1-baseline.agent-tool-call.workflow.json'],
  // release-luna.workflow.json is upstream's NEGATIVE fixture (7 workflow/node-overlap, T/architecture-first-draft.test.mjs:261-269):
  // not a golden — it is the `wf-release-luna` failure receipt in Step 6.
  ...['agent-tool-call', 'automatic-fanout', 'automatic-port-slot', 'customer-refund', 'incident-response']
    .map((n) => ['workflow', `workflow-first-draft/${n}.workflow.json`, `first-draft.${n}.workflow.json`]),
  ...['order-overflow', 'order-reflow', 'three-lane-remainder']
    .map((n) => ['workflow', `workflow-viewport/${n}.workflow.json`, `viewport.${n}.workflow.json`]),
  ['dataflow', 'dataflow-bidirectional-labels.json', 'bidirectional-labels.dataflow.json'],
  ['dataflow', 'dataflow-label-visibility.json', 'label-visibility.dataflow.json'],
  ['dataflow', 'v1-baseline/product-analytics.dataflow.json', 'v1-baseline.product-analytics.dataflow.json'],
  ...['joint-label-routing', 'mixed-port-recovery', 'ml-features', 'rejected-grid-recovery', 'vertical-label-corridor', 'vertical-label-neighbour']
    .map((n) => ['dataflow', `dataflow-first-draft/${n}.dataflow.json`, `first-draft.${n}.dataflow.json`]),
];
for (const [, from, to] of phase3Copies) fs.copyFileSync(path.join(TF, from), out(`fixtures/diagrams/${to}`));
```

`v1-baseline/event-stream.dataflow.json` is byte-identical to the example (survey) — not copied. `fs.copyFileSync` keeps upstream bytes; these files are inputs, so CRLF/LF does not matter to parity, but do not reformat them.

- [ ] **Step 2: Derived inputs** (each from an upstream example or test scene, one documented edit; `write()` re-serialises with 2-space indent):

```js
const readEx = (f) => JSON.parse(fs.readFileSync(path.join(EX, f), 'utf8'));
// Inline scenes from upstream tests, copied verbatim into the fixture tree so C# can read them.
// T/dataflow-automatic-layout.test.mjs:355-361 fiveStageDiagram, :27-51 pipeline(); T/v1-compatibility.test.mjs:180-203 legacy narrow dataflow.
// Copy each object literal exactly as the test builds it (read the test file; do not paraphrase) into these three writes:
write('fixtures/diagrams/five-stage.dataflow.json', FIVE_STAGE);              // meta.quality_profile 'standard', no viewBox
write('fixtures/diagrams/pipeline.dataflow.json', PIPELINE);                  // showcase, no viewBox
write('fixtures/diagrams/legacy-narrow.dataflow.json', LEGACY_NARROW);
// Brand fixtures (Review Focus 4): built-in marks from the embedded catalog (upstream/brand-marks/catalog.json ids).
const pa = readEx('product-analytics.dataflow.json');
pa.nodes[0].brand = 'react'; pa.nodes[6].brand = 'snowflake'; pa.meta.output = 'product-analytics.brand.html';
write('fixtures/diagrams/product-analytics.brand.dataflow.json', pa);
const hp = readEx('holiday-planning.workflow.json');
hp.nodes[1].brand = 'notion'; hp.nodes[2].brand = 'wechat'; hp.meta.output = 'holiday-planning.brand.html';   // nodes[0] 'today' fails the brand top rail (92px node)
write('fixtures/diagrams/holiday-planning.brand.workflow.json', hp);
```

All four ids exist in `upstream/brand-marks/catalog.json` and both inputs render under Node (checked 2026-10-14).

- [ ] **Step 3: Targets** (`targets.push(...)` before the render loop; the checker loop then mints every checker JSON):

```js
const p3 = (type, name) => [type, out(`fixtures/diagrams/${name}.${type}.json`), `${name}-rendered.html`];
targets.push(
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'holiday-planning-rendered.html'],
  ['workflow', path.join(EX, 'incident-response.workflow.json'), 'incident-response-rendered.html'],
  ['workflow', path.join(EX, 'release-delivery.workflow.json'), 'release-delivery-rendered.html'],
  ['dataflow', path.join(EX, 'event-stream.dataflow.json'), 'event-stream-rendered.html'],
  ...phase3Copies.map(([type, , to]) => p3(type, to.replace(`.${type}.json`, ''))),
  p3('dataflow', 'five-stage'), p3('dataflow', 'pipeline'), p3('dataflow', 'legacy-narrow'),
  p3('dataflow', 'product-analytics.brand'), p3('workflow', 'holiday-planning.brand'),
);
// Quality-option goldens (Review Focus 2, 3): minted with the env var upstream reads; C# renders the same input with RenderOptions.
const renderEnv = (type, input, output, env) => execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), input, output], { stdio: 'inherit', env: { ...cleanEnv, ...env } });
renderEnv('dataflow', out('fixtures/diagrams/five-stage.dataflow.json'), out('goldens/five-stage.option-showcase-rendered.html'), { ARCHIFY_QUALITY_PROFILE: 'showcase' });
renderEnv('workflow', path.join(EX, 'agent-tool-call.workflow.json'), out('goldens/agent-tool-call.option-standard-rendered.html'), { ARCHIFY_QUALITY_PROFILE: 'standard' });
```

Also add the two option goldens to the checker loop's list (append them to the array the loop iterates). The upstream goldens `workflow-agent-tool-call-rendered.html` and `dataflow-product-analytics.html` already sit in `upstream/examples/`.

- [ ] **Step 4: Manifest with ownership and coverage.** For every workflow and dataflow input of Steps 1–3 (examples included), write one row to `fixtures/diagrams/phase3-manifest.json`: `{ name, type, input, golden, schemaVersion, viewBox: bool, qualityProfile, locale, pins: bool, coverage: [...], task }`.
  - `pins` = any relation carries `via`, `labelAt`, `channelX`, `channelY`, `route` (preset), `fromSide`, `toSide`, `labelDx`, `labelDy` or `labelSegment`.
  - `coverage` (workflow only) comes from a **coverage-probe compile**: read workflow-compiler.mjs's text (normalise `\r\n` to `\n`), rewrite its relative import specifiers to absolute URLs (`.replace(/from '(\.[^']+)'/g, (_, rel) => \`from '${new URL(rel, wfFileUrl).href}'\`)`), and insert `globalThis.__wfcov?.add('<tag>');` **after** each anchor below — every anchor ends with the function's opening `{\n` (or is a whole statement), so the insertion lands inside the body, never in a parameter list. Assert each anchor occurs **exactly once** before replacing. Import the result through a `data:text/javascript;base64,…` URL (no temp file, no `--experimental-vm-modules`), and call `compileWorkflow({ workflow, qualityProfile: workflow.meta?.quality_profile, sourceEvidence: null })` on a `JSON.parse`d copy. Anchors (copy the exact text from workflow-compiler.mjs, including the full parameter list): `controlled` → `function readableControlledRoute(edge, from, to) {\n`; `preset` → `function readablePresetVia(edge, from, to, start, end, fromSide, toSide) {\n`; `sizing` → after the statement `widthOverrides.set(node.id, width);\n`; `endpoint-repairs`, `internal-detour`, `free-space` → the opening line of `automaticEndpointRepairs`, `readableInternalDetourPlans`, `readableFreeSpacePlans` (information for reviewers only). **Do not** probe functions that run on every compile (`validateReadablePairwisePinConflicts`, `enforceLegacyColumnCapacity`, `hasAbsoluteRoutePins`): they would tag every input. The probe is used **only** to assign ownership and to tell reviewers which branches a row exercises, never as an expected value. Fix-discovery recompiles also hit these functions; that superset is acceptable (say so in the manifest's header field `coverageNote`).
  - `task` rule (first match wins): dataflow with `viewBox` → **4**; dataflow without → **5**; workflow `schemaVersion: 1` → **7**; workflow v2 option/brand fixtures (`agent-tool-call.option-standard`, `holiday-planning.brand`) → **11**; workflow v2 with `pins` or a `controlled`/`preset` tag → **10**; other workflow v2 → **8**.
  The example rows: `agent-tool-call` (upstream golden, v2, presets) → 10; `holiday-planning` → 8. Expected split (agy probe, 2026-10-14): 5 v1 rows → 7, 16 unpinned v2 rows → 8, the pinned ones (`agent-tool-call`, `shared-trunks`, `route-text-recovery`, …) → 10. A materially different split means the rule or the probe is wrong — stop and report.

- [ ] **Step 5: Workflow receipts.** For every workflow input (examples + copies + derived), write `tests/fixtures/workflow/<name>.receipt.json` = `JSON.stringify(compiled.receipt, null, 2) + '\n'` where `compiled` comes from the **real** module (no probe) called exactly as render-workflow.mjs:8-20 does: obtain the diagram via `const { diagram, sourceEvidence } = await loadDiagramWithBrandMarks({ rendererDir: path.join(UP, 'renderers/workflow'), diagramType: 'workflow', defaultExample: 'agent-tool-call.workflow.json', argv: ['node', 'render-workflow.mjs', inputPath] })` — it is **async** (cli.mjs `export async function`), and `loadDiagram` reads the input from `argv[2]` (cli.mjs:47-52). Set `process.env.ARCHIFY_DIAGNOSTIC_FORMAT = 'json'` **before** the first `import()` of any upstream renderer module in this block (diagnostics.mjs:4 reads it once at module load; the earlier blocks spawn child processes and pass it per call, so set it here and restore the previous value after the block). `loadDiagram` also installs a process-level diagnostic boundary (cli.mjs:50); if it swallows a capture-script error, run this block in a child `node` process instead. `qualityProfile: env.ARCHIFY_QUALITY_PROFILE || workflow.meta?.quality_profile` with the env stripped (or `standard` for the option fixture), `sourceEvidence` as returned. Assert the receipt text equals what `node render-workflow.mjs <input> --layout-json` prints for one example (agent-tool-call) — this proves the in-process call is the CLI's.

- [ ] **Step 6: Failure receipts** — one fault per diagnostic code; each must make upstream **fail** with the named code present in the receipt's `diagnostics`, or the script throws `fault <name>: expected <code>`. Pattern as the sequence block (`ARCHIFY_DIAGNOSTIC_FORMAT=json`, stderr JSON → `<name>.<type>.expected.json`), plus the target code check:

```js
const TF = 'C:/projects/archify/test/fixtures';   // already declared in Step 1 — reuse it
const p3Faults = [   // [type, source input, name, target code, edit] — every row checked under Node 2026-10-14 (code present, path as noted)
  // workflow v2 without pins (holiday-planning, zh-CN): verified fixes recompile the whole document, so these faults must not need
  // Task 10's pin/preset code. lanes calendar makeup; nodes today@calendar:0 mid_autumn@calendar:1 national_day@calendar:3
  // year_end@calendar:5 makeup_work@makeup:3; edges 0 today>mid_autumn 1 mid_autumn>national_day 2 national_day>year_end 3 national_day>makeup_work
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-duplicate-node-id', 'workflow/duplicate-node-id', (d) => { d.nodes[2].id = d.nodes[1].id; }],          // /nodes/2/id
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-duplicate-lane-id', 'workflow/duplicate-lane-id', (d) => { d.lanes[1].id = d.lanes[0].id; }],          // /lanes/1/id
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-unknown-edge-endpoint', 'workflow/unknown-edge-endpoint', (d) => { d.edges[2].to = 'year_ned'; }],      // /edges/2/to
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-unknown-node-lane', 'workflow/unknown-node-lane', (d) => { d.nodes[4].lane = 'makeups'; }],            // /nodes/4/lane
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-node-overlap', 'workflow/node-overlap', (d) => { d.nodes[4].lane = 'calendar'; }],                      // /nodes/4
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-viewbox-capacity', 'workflow/viewbox-capacity', (d) => { d.meta.viewBox = [700, 360]; }],             // /meta/viewBox (schema minimum width is 700)
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-sublabel-readability', 'workflow/sublabel-readability',
    (d) => { for (const n of d.nodes) n.width = 280; d.nodes[1].width = 92; d.nodes[1].sublabel = '1234567890123456789012'; }],                                         // /nodes/1/sublabel
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-semantic-reference', 'workflow/semantic-node-reference', (d) => { d.semanticChecks = { allowedRoots: ['nobody'] }; }],   // /semanticChecks/allowedRoots/0
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-required-edge', 'workflow/required-edge', (d) => { d.semanticChecks = { requiredEdges: [{ from: 'today', to: 'year_end' }] }; }], // /semanticChecks/requiredEdges/0
  // workflow v2 with pins/presets (agent-tool-call) — Task 10
  ['workflow', path.join(EX, 'agent-tool-call.workflow.json'), 'wf-preset-conflict', 'workflow/route-preset-conflict', (d) => { d.edges[0].route = 'drop'; }],              // no path
  ['workflow', path.join(EX, 'agent-tool-call.workflow.json'), 'wf-single-pin-conflict', 'workflow/explicit-pin-conflict', (d) => { d.edges[1].via = [[300, 60]]; }],     // /edges/1/via (single-pin rule)
  // workflow v1
  ['workflow', path.join(EX, 'incident-response.workflow.json'), 'wf-unknown-main-path', 'workflow/unknown-endpoint', (d) => { d.mainPath[3] = 'ghost'; }],               // /mainPath/3
  ['workflow', path.join(TF, 'v1-workflow-explicit-coordinates.workflow.json'), 'wf-v1-column-capacity', 'workflow/column-capacity', (d) => { d.nodes[2].col = 1; d.nodes[3].col = 2; }], // no path; + suppresses
  ['workflow', path.join(TF, 'workflow-first-draft/release-luna.workflow.json'), 'wf-release-luna', 'workflow/node-overlap', () => {}],                                    // upstream's own negative fixture, unedited
  // dataflow (product-analytics, viewBox 1080x520)
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-unknown-endpoint', 'dataflow/unknown-endpoint', (d) => { d.flows[2].to = 'consnt'; }],                 // /flows/2/to
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-node-above-area', 'dataflow/node-above-area', (d) => { d.nodes[3].yOffset = -400; }],
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-node-below-area', 'dataflow/node-below-area', (d) => { d.nodes[9].row = 4; d.nodes[9].yOffset = 300; }],        // requiredViewBoxFitsViewport false
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-node-below-area-raised', 'dataflow/node-below-area', (d) => { d.nodes[9].row = 2; d.nodes[9].yOffset = 160; }], // requiredViewBoxFitsViewport true
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-duplicate-node', 'layout/constraint', (d) => { d.nodes[4].id = d.nodes[3].id; }],
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-pinned-crossing', 'composition/proper-crossing',
    (d) => { delete d.flows[4].route; d.flows[4].via = [[610, 157], [610, 320], [660, 320], [660, 157]]; }],
];
```

Each row writes its edited input to `fixtures/diagrams/<name>.<type>.json` and the stderr receipt to `fixtures/diagrams/<name>.<type>.expected.json`. The script throws if a row renders or if its receipt lacks the target code. `df-node-below-area` and `-raised` differ in the serialised `evidence.requiredViewBoxFitsViewport` (upstream's local variable is `raisedCanvasFits`, dataflow-compiler.mjs:323/:363); assert that too. Owning tasks: `wf-unknown-main-path` → 7; `wf-viewbox-capacity`, `wf-sublabel-readability` → 8; the other holiday-planning rows → 9; `wf-preset-conflict`, `wf-single-pin-conflict`, `wf-v1-column-capacity` (its migration fix recompiles a mapped v2 candidate that keeps the authored pins), `wf-release-luna` → 10; `df-*` → 4.

- [ ] **Step 7: Run** `node tools/capture-fixtures.mjs` from the repo root, then run it **again** and confirm `git status --short` shows no change from the second run (determinism). Run `dotnet test` → still 364 passed (no C# change yet; the new goldens are not referenced).
- [ ] **Step 8: Commit** — `git add tools/capture-fixtures.mjs tests/fixtures/diagrams tests/fixtures/workflow tests/goldens` (check `git status` first: only new files plus `capture-fixtures.mjs` may appear; any modified phase-1/2 golden is a bug — stop) → `test(fixtures): phase-3 inputs, goldens, compiler receipts, failure receipts and manifest`.

---

### Task 2: `Js.WordBreaks` — UAX #29 word boundaries over graphemes (R1)

**Files:**
- Modify: `src/Archify.Net.Core/Js/JsText.cs` (replace `WordBreaks`; add range tables), `tools/capture-fixtures.mjs` (word-breaks block), `tests/Archify.Net.Core.Tests/JsTextTests.cs`
- Create: `tests/fixtures/js/word-breaks.json`

**Interfaces:**
- Produces: `public static List<string> WordBreaks(string s)` — segments whose concatenation is `s`; same signature as today (callers: `SequenceRenderer` only; Task 3's `LabelCapacity` next).

- [ ] **Step 1: Capture block** (append to the phase-3 input block):

```js
// --- Intl.Segmenter word granularity (label-capacity.mjs:32-33) on every string a phase-3 label can carry + a battery. `dictionary`
// rows (Han/Kana/Thai/Lao/Khmer/Myanmar) are parity-exempt: C# asserts concatenation and per-grapheme splitting of those runs only. ---
const wordSeg3 = new Intl.Segmenter(undefined, { granularity: 'word' });
const DICT = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Thai}\p{Script=Lao}\p{Script=Khmer}\p{Script=Myanmar}\u30FC\uFF70]/u;
const battery = JSON.parse(fs.readFileSync('C:/projects/Archify.Net/.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/battery-strings.json', 'utf8'));
```

The battery file is git-ignored; copy its contents into the script as a literal array instead (the script must run from a clean clone). Then collect every string value under `upstream/examples/*.{workflow,dataflow}.json` and the phase-3 inputs of Task 1 (labels, sublabels, stage labels), add the battery, dedupe, sort (`.sort()`), and write `fixtures/js/word-breaks.json` as `[{ text, dictionary, words }]` with `words = [...wordSeg3.segment(text)].map((s) => s.segment)`.

- [ ] **Step 2: Failing tests** (append to `JsTextTests`):

```csharp
    public static IEnumerable<object[]> WordRows() =>
        JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("js/word-breaks.json")))!.AsArray()
            .Select(r => new object[] { r!["text"]!.GetValue<string>(), r["dictionary"]!.GetValue<bool>(), Js.Stringify(r["words"]) });

    [Theory, MemberData(nameof(WordRows))]
    public void WordBreaks_match_Intl_Segmenter_outside_dictionary_scripts(string text, bool dictionary, string v8Words)
    {
        var words = Js.WordBreaks(text);
        Assert.Equal(text, string.Concat(words));
        if (!dictionary) Assert.Equal(v8Words, Js.Stringify(new JsonArray([.. words.Select(w => (JsonNode?)JsonValue.Create(w))])));
    }

    [Fact] public void WordBreaks_split_dictionary_runs_per_grapheme()
    {
        Assert.Equal(["已", "持", "有", "：", "处", "理"], Js.WordBreaks("已持有：处理"));
        Assert.Equal(["clickstream", " ", "events"], Js.WordBreaks("clickstream events"));
        Assert.Equal(["Archify", "로"], Js.WordBreaks("Archify로"));   // Hangul never joins Latin (ICU tailoring)
    }
```

Run `dotnet test --filter JsTextTests` → the theory fails on Latin rows.
- [ ] **Step 3: Implement** by porting `uax29proto.mjs` (83 lines; read it in full) to C#: class lookup per grapheme cluster (first code point's class); a cluster of only `Mn`/`Me`/`Cf` attaches to the previous one (WB4) unless it contains U+200B/U+200C/U+200D or the previous cluster is Newline/WSegSpace — exactly the prototype's condition at :35-38; then the WB5–WB13b decisions exactly as the prototype orders them (:46-61). **No regional-indicator rule** at word level (graphemes already pair flags). Use `Js.Graphemes`, `CharUnicodeInfo.GetUnicodeCategory(int)` (the `int` overload — code points, not chars), `Js.IsHan`, and explicit range arrays for Hiragana `3041-309F`, Katakana `30A0-30FF, 31F0-31FF, 32D0-32FE, 3300-3357, FF66-FF9F` (plus `ー` U+30FC, `ｰ` U+FF70), Thai `0E00-0E7F`, Lao `0E80-0EFF`, Khmer `1780-17FF, 19E0-19FF`, Myanmar `1000-109F, A9E0-A9FF, AA60-AA7F`, Hebrew letters `05D0-05EA, 05EF-05F2, FB1D-FB4F`, Hangul `1100-11FF, 3130-318F, A960-A97F, AC00-D7A3, D7B0-D7FF, FFA0-FFDC`. Take every list and range from the prototype where it differs from this text — the prototype is what was measured. Keep the comment `// ponytail: V8 parity — UAX #29 subset; dictionary scripts per grapheme (stated deviation)`.
- [ ] **Step 4: Run** `dotnet test --filter "JsTextTests|ParityTests"` → green (sequence goldens unchanged); then the full suite.
- [ ] **Step 5: Commit** — `git add src/Archify.Net.Core/Js/JsText.cs tests/Archify.Net.Core.Tests/JsTextTests.cs tests/fixtures/js/word-breaks.json tools/capture-fixtures.mjs` → `feat(js): WordBreaks as a UAX #29 subset (V8-identical outside dictionary scripts)`.

---

### Task 3: Dataflow helper modules — `LabelCapacity`, `JointLabelRouting`, `LabelRect.Lines`

**Files:**
- Create: `src/Archify.Net.Core/Dataflow/LabelCapacity.cs` ← `UP/renderers/dataflow/label-capacity.mjs` (81), `src/Archify.Net.Core/Dataflow/JointLabelRouting.cs` ← `joint-label-routing.mjs` (165), `tests/Archify.Net.Core.Tests/DataflowHelperTests.cs`, `tests/fixtures/js/label-capacity.json`, `tests/fixtures/js/joint-label-routing.json`
- Modify: `src/Archify.Net.Core/Shared/Geometry.cs` (R3), `tools/capture-fixtures.mjs`

**Interfaces:**
- Consumes: `Js.WordBreaks` (Task 2), `Utils.TextUnits`, `Geometry.RectsOverlap`, `Geometry.SegmentRectClearanceWithin` (`double?`), and the shared geometry the joint router imports (`AutomaticPortRhythmBridge`, `CollectAmbiguousCorridors`, `CollectBorderRuns`, `CollectRouteRhythmIssues`, `FrameBorderSegments`, `NormalizeRoutePoints`, `ProperSegmentIntersection`, `RouteHonorsEndpointSides`, `SegmentIntersectsRect`, `RouteQuality.ShortestOrthogonalGridRoute`).
- Produces: `internal static class LabelCapacity { const double FlowLabelHeight; (double Width, double Height) FlowLabelSize(JsonObject flow); List<LabelRect> WrapBlockedFlowLabels(...) }` and `internal static class JointLabelRouting { List<LabelRect> RepairLabelRoutes(...) }` with parameter lists mirroring the upstream option objects one-to-one (read the modules; name each parameter after its JS property). `RepairLabelRoutes` returns **the input list instance** when nothing changed (R9).

- [ ] **Step 1: Capture block.** `label-capacity.json`: `flowLabelSize` on a battery (classified/unclassified, Latin, Han, emoji, long), and `wrapBlockedFlowLabels` on the scenes of `T/dataflow-label-capacity.test.mjs:5-60` copied verbatim (the module is exported, import it directly). `joint-label-routing.json`: `repairLabelRoutes` input and output on the two inputs that reach it (`first-draft.joint-label-routing`, `first-draft.rejected-grid-recovery`), captured with the `trace-hooks.mjs` load-hook technique (copy the 20-line hook into the capture script; it rewrites dataflow-compiler.mjs **in memory** to push the call's arguments and result to `globalThis.__df`). Serialise relations as `{id, from, to, pins}` and replace `sideFor` with a `relationIndex → {fromSide, toSide}` table.
- [ ] **Step 2: Failing tests** in `DataflowHelperTests`: one theory per fixture row comparing every output field (lines, x, y, width, height, label relation id) with `Js.Num`-stringified numbers.
- [ ] **Step 3: Port.** Rules: `wordSegmenter ||=` lazy segmenter → a call to `Js.WordBreaks`; boundaries are cumulative UTF-16 offsets of the segments after the first (`[...segment(text)].slice(1).map(({index}) => …)`, :33); candidates keep lines with a non-blank `Js.Trim` and narrower than the plate, stable-sorted by width (`Js.Sort`), first 8 (:35-37); `probes++ >= 64` is post-increment (:52); `seen` keys are `$"{Js.Num(x)},{Js.Num(y)},{Js.Num(w)},{Js.Num(h)}"` (:47); `Math.round(Math.max(34, units*4.9+12)*10)/10` → `Js.Round` (:8); `segmentRectClearanceWithin` `null` → `?? 0` (:26; joint :13-15 `Math.min(null, x)` = 0); empty `Math.min` spreads at joint :103 and :153 are `+Infinity` — seed `double.PositiveInfinity`; route identity `route !== own` (:64, :101) / `route === own` (:122) → `ReferenceEquals`; `JSON.stringify(points)` keys (:67, :69, :115) → a `Js.Num`-joined key; number Sets (:71) → `JsSet<double>`; sorts :110 (three-key `||` chain), :125 (length desc), label-capacity :37, :63 → `Js.Sort`.
- [ ] **Step 4: Run** `dotnet test --filter DataflowHelperTests` → green; full suite.
- [ ] **Step 5: Commit** — `git add src/Archify.Net.Core/Dataflow/LabelCapacity.cs src/Archify.Net.Core/Dataflow/JointLabelRouting.cs src/Archify.Net.Core/Shared/Geometry.cs tests/Archify.Net.Core.Tests/DataflowHelperTests.cs tests/fixtures/js/label-capacity.json tests/fixtures/js/joint-label-routing.json tools/capture-fixtures.mjs` → `feat(core): dataflow label-capacity and joint label routing (Node-pinned)`.

---

### Task 4: Dataflow compiler, fixed geometry — upstream golden, event-stream, viewBox fixtures, failure receipts

**Files:**
- Create: `src/Archify.Net.Core/Dataflow/DataflowCompiler.cs`, `src/Archify.Net.Core/Dataflow/DataflowCompiler.Automatic.cs` (stubs only), `src/Archify.Net.Core/Dataflow/DataflowRenderer.cs`
- Modify: `src/Archify.Net.Core/Archify.cs` (`DiagramType.Dataflow => new DataflowRenderer()`), `tests/Archify.Net.Core.Tests/ParityTests.cs`

**Interfaces:**
- Consumes: Task 3 helpers; `Legend`, `TextFit`, `BrandMarks`, `DesktopReadability`, `AutomaticLabels.PlaceAutomaticLabels`, `RouteQuality`, `Geometry` gates (all phase 1).
- Produces: `internal sealed partial class DataflowCompiler(RenderContext ctx, JsonObject dataflow, string? qualityProfile)` with `string Compile()` (= `compileDataflow`, returns the SVG or throws the diagnostic exception) and the cluster methods Task 5 fills in a second `partial` file; `public sealed class DataflowRenderer : IDiagramRenderer` returning `(svg, ctx.Diagram.Arr("cards"), null)`.

Porting rules (dataflow survey §1, §2, §4, §5 in full):
- Structure: `compileDataflow` = `build(rebalancePorts)` (:61-1577) + `attempt` (:1579-1587, `ctx.Diagnostics.WithIsolatedRecording`, `RendererFailure` rewrite) + retry (:1589-1604). `build` is a fresh `Build` object per attempt (closure state). The automatic clusters B, C, H, I, M, N, O-joint and the `automaticShowcase` branches of F/J/K are **declared** in this task as methods in `DataflowCompiler.Automatic.cs` that throw `NotImplementedException("Task 5")`; every caller already sits behind `if (automaticShowcase)`, so the fixed-geometry rows never reach them. Cluster L (two-line repair, :1103-1152) runs **always** and is ported here.
- `automaticShowcase` (:84-86) and the gates use the resolved profile (R4). `svgRootAttrs(dataflow.meta)` → `SvgAttrs.SvgRootAttrs(ctx)`.
- `nodes = new Map(entries)` (:146) → `JsMap` (duplicate id: first position, last value); `nodes.size !== length` (:253).
- `viewBox` aliases the authored `meta.viewBox` (:97); in-place writes only when not authored.
- Label copies `{ ...other.relation, labelAt: [lx, ly] }` (:1119, :1180, :1301) → `relation.DeepClone()` + `labelAt`; the label at `index` keeps the original flow because `rect.relation === flow` (:1092, :1193, :1244, :1483) and `resolvedLabelPoints` key on it (R9).
- `autoHeightInset` (:235, :1436) → a dataflow-local `JsMap<string, double>` by node id, not a `Box` field.
- Stage header wrap (:1384-1420) → `Js.SpaceTokens` + `Js.Graphemes`.
- `stageTop`/`stageBottom` (:1354, :1372) write `compositionFrames[index].height = height` in place during validation, so the border-run gate sees real stage heights; C# `Frame` is an immutable `record` — replace the element: `compositionFrames[index] = compositionFrames[index] with { Height = height }`.
- Brand: measured node `Brand = ctx.Brands.BrandMarkFor(authored)`; sites :376, :1433, :1441, :1452-1453. sourceEvidence (:1441) → `ctx.SourceEvidence?["nodes"]?[id]` (null until phase 4, as `LifecycleRenderer.SourceBadgeFor`).
- Sorts :196, :420-425, :635, :800, :902, :914, :966, :967, :1221 → `Js.Sort`; boolean subtraction `(db>0)-(da>0)` → `(db > 0 ? 1 : 0) - (da > 0 ? 1 : 0)`.
- Diagnostic codes: `dataflow/node-above-area` (:278), `dataflow/node-below-area` (:355, both `raisedCanvasFits` branches, three `extent` texts :344-348), `dataflow/unknown-endpoint` (:427, `path /flows/i/from|to`, sorted `availableNodeIds` :420-425); everything else `layout/constraint` via `throwDiagnosticProblems('Data-flow layout validation failed', …)`.

- [ ] **Step 1: Parity rows + receipts.** In `ParityTests.Targets()`, add `["dataflow", Fixtures.Upstream("examples/product-analytics.dataflow.json"), Fixtures.Upstream("examples/dataflow-product-analytics.html")]`, `["dataflow", Fixtures.Upstream("examples/event-stream.dataflow.json"), Fixtures.Golden("event-stream-rendered.html")]`, and one row per manifest entry with `task == 4` (the manifest is the list; write the rows out literally, no runtime manifest read). Add `[Theory] Dataflow_fault_diagnostics_match_upstream_receipt` with `InlineData` for the six `df-*` faults, using the existing `AssertReceipt` helper.
- [ ] **Step 2: Run** → rows fail with `NotSupportedException`. **Step 3: Port** per the rules; register. **Step 4: Iterate** on `FirstDifference` until green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Dataflow/DataflowCompiler.cs src/Archify.Net.Core/Dataflow/DataflowCompiler.Automatic.cs src/Archify.Net.Core/Dataflow/DataflowRenderer.cs src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): dataflow renderer (fixed geometry) — goldens byte-identical, failure receipts`.

---

### Task 5: Dataflow automatic showcase layout

**Files:**
- Modify: `src/Archify.Net.Core/Dataflow/DataflowCompiler.Automatic.cs` (replace the Task 4 stubs), `DataflowCompiler.cs` (only where a showcase branch lives inline in F/J/K), `tests/Archify.Net.Core.Tests/ParityTests.cs`

**Interfaces:**
- Consumes: Task 3 `LabelCapacity.WrapBlockedFlowLabels`, `JointLabelRouting.RepairLabelRoutes`; `Geometry.AlignAutomaticFacingPorts`, `AutomaticPortRhythmBridge`, `AutomaticPortSpread`; `RouteQuality.ShortestOrthogonalGridRoute`.
- Produces: no new API; the stubs become the clusters B (:157-201, port rebalance, `return null` :201), C (:206-240 auto node height), H (:765-926 route repair), I (:928-1006 port reorder), M (:1159-1330 label/corridor co-repair), N (:1335-1345 wrap), O-joint (:1350-1380).

Porting rules: dataflow survey §2 rows B–O and §4 in full. `JSON.stringify(points)` at :903 (`!==` decides `repaired`) and :1213 (Map key, first position wins) → `Js.Num`-joined keys; Number Set :789 → `JsSet<double>`; `groups` :943 keyed `${id}\0${side}` (keep the NUL); `Object.values(sides)` :222 iterates left, right; `proposal.labels !== resolvedLabelRects` :1365 is list identity.

- [ ] **Step 1: Rows.** One `Targets()` row per manifest entry with `task == 5` **except** `first-draft.mixed-port-recovery`, plus `["dataflow", Fixtures.Fixture("diagrams/five-stage.dataflow.json"), Fixtures.Golden("five-stage-rendered.html")]`. The option row needs `RenderOptions`, so it is a fact:

```csharp
    [Fact] public void Dataflow_quality_option_switches_the_automatic_layout_like_the_env_var()   // Review Focus 2
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/five-stage.dataflow.json")));
        var r = ArchifyNet.Render(DiagramType.Dataflow, d, new RenderOptions(Quality: QualityProfile.Showcase));
        Assert.Equal(Fixtures.Lf(Fixtures.Golden("five-stage.option-showcase-rendered.html")), r.Html);
    }

    [Fact] public void Mixed_port_recovery_differs_from_upstream_only_in_the_dictionary_wrapped_label()   // R1 deviation
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/first-draft.mixed-port-recovery.dataflow.json")));
        var port = ArchifyNet.Render(DiagramType.Dataflow, d).Html.Split('\n');
        var node = Fixtures.Lf(Fixtures.Golden("first-draft.mixed-port-recovery-rendered.html")).Split('\n');
        Assert.Equal(node.Length, port.Length);
        var diff = Enumerable.Range(0, node.Length).Where(i => node[i] != port[i]).ToList();
        Assert.Single(diff);
        Assert.Contains("已持有", node[diff[0]]);
    }
```

If `Assert.Single` fails because the line split puts the two `<tspan>`s on separate lines, assert instead that every differing line contains `已持有` or `完成或失败` (and that there are ≤ 2) — the rule is "only that label differs".
- [ ] **Step 2: Run** → the rows fail with `NotImplementedException("Task 5")`. **Step 3: Port.** **Step 4: Iterate** to green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Dataflow tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): dataflow automatic showcase layout — first-draft goldens byte-identical`.

---

### Task 6: Workflow model — canonical doc, readable rank layout, scene measurement, migration geometry, placement repair

**Files:**
- Create: `src/Archify.Net.Core/Workflow/WorkflowDoc.cs`, `WorkflowModel.cs`, `WorkflowScene.cs`, `WorkflowMigrationGeometry.cs`, `WorkflowPlacementRepair.cs`, `tests/Archify.Net.Core.Tests/WorkflowModelTests.cs`, `tests/fixtures/js/workflow-model.json`
- Modify: `src/Archify.Net.Core/Shared/Geometry.cs` (R2), `tools/capture-fixtures.mjs`

**Interfaces:**
- Produces (exact names later tasks use):
  - `internal sealed class WorkflowDoc` (R7) with `static WorkflowDoc From(JsonObject authored)`, `WorkflowDoc Clone()`, `JsonObject ToJson()`, `bool? AbsoluteGeometryPolicy`.
  - `internal sealed class WorkflowLayoutFeedback(string kind, JsonObject? rankGapMinimums, double? laneGapMin) : Exception` (:114-121).
  - `internal sealed class WorkflowWidths` wrapping the R8 `ConditionalWeakTable` (`double? Get(JsonObject node)`, `Set`, `Remove`).
  - `internal static class WorkflowModel` with `ReadableLayout CreateReadableLayout(WorkflowDoc w, JsonObject? feedback, WorkflowWidths widths)` (returns a record holding every field of :642-660; `channelLabelEdgeKeys` a `JsSet<string>`), `WorkflowDoc CanonicalReadableWorkflow(WorkflowDoc w, WorkflowWidths widths, JsMap<string,double>? widthOverrides)`, `List<Diagnostic> SemanticContractDiagnostics(WorkflowDoc w)`, `string StableValueKey(JsonNode? v)`, `double CompareWorkflowEdges(JsonObject a, JsonObject b)`, plus the A helpers (`HasAbsoluteWorkflowPins`, `HasVerticalStack`, `UsesIndependentLaneMeasurement`, `AutomaticNodeWidth`, `AuthoredNodeWidth`, `AuthoredNodeHeight`, `WorkflowLabelWidth`, `ReadableGroupBounds`, `CreateLegacyLayout`, `HardRouteSegmentMinimum`).
  - `internal static class WorkflowScene` with `ResolveWorkflowLegendFootprint`, `CreateWorkflowLaneGeometry`, `MeasureWorkflowNodes` (returns `JsMap<string, Box>` with `Cx`/`Cy` **stored**, R2), `WorkflowNodeTextProblems`, `CreateWorkflowStepIndexes`.
  - `internal static class WorkflowMigrationGeometry` (6 functions, MG) and `internal static class WorkflowPlacementRepair.ProposeWorkflowPlacement`.

Porting rules: workflow survey §2 clusters A–D, §3, §4 in full. `Object.entries(rankGapMinimums).sort()` (:251) → ordinal sort on `key + "," + Js.Num(value)`; `stableValueKey` (:686-692) is order-observable — exact `JSON.stringify` (`Js.Quote`, `Js.Num`, ordinal key order); sorts :395, :717-731, :658-659 → `Js.Sort`; PR:81 `localeCompare` → `Js.LocaleCompare`; `mappedNumber` = `Js.ToNumber(Js.ToFixed(v, 6))` (MG:68-70); `mapExplicitCoordinates` mutates its argument (a clone); `structuredClone` (PR:49) = `DeepClone()` (keeps `-0`, unlike the JSON clone); `nodeContext` joins with `" › "` (U+203A, :931); unknown lane → NaN geometry, not an exception (:950-955); `Number(node.yOffset) || 0` → `Js.ToNumber`-style NaN→0 (:153, :227-228, :291, :557, :609).

- [ ] **Step 1: Shim-export capture block.** Read workflow-compiler.mjs's text, rewrite `'../shared/` and `'./workflow-` import specifiers to absolute `file:///C:/projects/archify/archify/renderers/...` URLs, append `export { createReadableLayout, canonicalReadableWorkflow, semanticContractDiagnostics, automaticNodeWidth, stableValueKey, compareWorkflowEdges, resolveWorkflowLegendFootprint, createWorkflowLaneGeometry, measureWorkflowNodes, createWorkflowStepIndexes };`, write to `os.tmpdir()`, `import()` it. Upstream stays untouched. Write `fixtures/js/workflow-model.json` with, for each of the 4 examples and every manifest workflow input: `canonical` (node/edge/lane/phase/group ids in canonical order), `semantic` (`[code, path]` list), `layout` (every field of the `createReadableLayout(canonicalReadableWorkflow(w), feedback)` result, Sets → arrays in insertion order) for `feedback` = `undefined` and one `rankGapMinimums` object, `measured` (per node `id, x, y, width, height, cx, cy` + `laneTop`/`autoHeight`/`legendY`). Call `canonicalReadableWorkflow` **before** `createReadableLayout` (it fills the WeakMap). MG rows: mapper outputs at sample x (inside, outside both ends, values that exercise `toFixed(6)` rounding), `planningWorkflow`/`intrinsicWorkflow` outputs, `createMappedWorkflowCandidate(...).changedCoordinates`. PR rows: `proposeWorkflowPlacement` on an eligible and an ineligible input with mixed-case ids.
- [ ] **Step 2: Failing tests** in `WorkflowModelTests`: one theory per fixture section, comparing `Js.Stringify` of the C# result with the fixture node (numbers via `Js.Num`).
- [ ] **Step 3: Port**, R2 in `Geometry.cs`. **Step 4: Run** `dotnet test --filter WorkflowModelTests` → green; full suite (R2 must leave every phase-1/2 golden green). **Step 5: Commit** — `git add src/Archify.Net.Core/Workflow src/Archify.Net.Core/Shared/Geometry.cs tests/Archify.Net.Core.Tests/WorkflowModelTests.cs tests/fixtures/js/workflow-model.json tools/capture-fixtures.mjs` → `feat(core): workflow model — canonical doc, rank layout, measurement, migration geometry (Node-pinned)`.

---

### Task 7: Workflow schema v1 end to end — compilation skeleton, validation, v1 routing, SVG, renderer

**Files:**
- Create: `src/Archify.Net.Core/Workflow/WorkflowCompilation.cs`, `WorkflowCompiler.cs`, `WorkflowRenderer.cs`, `tests/Archify.Net.Core.Tests/WorkflowReceiptTests.cs`
- Modify: `src/Archify.Net.Core/Archify.cs` (`DiagramType.Workflow => new WorkflowRenderer()`), `tests/Archify.Net.Core.Tests/ParityTests.cs`

**Interfaces:**
- Consumes: Task 6 (all of it).
- Produces:
  - `internal static class WorkflowCompiler { public static WorkflowCompileResult Compile(RenderContext ctx, WorkflowDoc workflow, string? qualityProfile, JsonObject? sourceEvidence) }` (= `compileWorkflow`, :5917) and `internal sealed record WorkflowCompileResult(bool Ok, string? Svg, JsonObject Receipt, string? Error, IReadOnlyList<Diagnostic> Diagnostics)`. The receipt's `diagnostics` key is **not** in `Receipt` (C# `Diagnostic` has no JSON form); `Diagnostics` carries them.
  - `internal sealed partial class WorkflowCompilation` (R6): constructor = :1370-5059 top-level statements in order; `Run()` = :5609-5644. Later tasks add `partial` files; members they need from this file are `protected`-free `internal`.
  - Cluster O wrappers in `WorkflowCompiler` (`CompileWithRouteOrderFeedback`, `CompileWithLayoutFeedback`, `CompileWorkflowWithFeedback`) are ported **in Task 8** — here `Compile` calls `new WorkflowCompilation(...).Run()` once.
  - `WorkflowRenderer : IDiagramRenderer` (R5, R11): `qualityProfile = ctx.Options.Quality?.Name() ?? meta.quality_profile`; returns `(svg, cards, new LayoutReport(receipt))`.

Porting rules: workflow survey clusters G, I1 (`validateWorkflow` :2693-3037), J1, J2, K3 `routeVia` v1 branches, K4 v1 label point + legacy `edgeSides`, F legacy (`legacyAutomaticOneBendSides`), K8 non-readable branch, L, M (including `readableSublabelMinimum` :5356-5361, which `renderNode` calls), N, and **E** (`createLegacyCapacityRepair` :1064-1237): `enforceLegacyColumnCapacity()` runs inside `validateWorkflow` (:2733) on **every** compile and checks v1 documents, so it cannot be a stub; its failure path (`readableMigrationProvidesCapacity`, recursive over MG projections, :1092-1147) reaches v2 code and is pinned by `wf-v1-column-capacity` in Task 10. v2-only call sites throw `NotImplementedException("Task 8")` (readable routing, canvas, node text), `("Task 9")` (input diagnostics), `("Task 10")` (pin/preset conflict throwers, controlled and preset routes) — no task-7 row reaches them. Every `schema_version === 2` branch (~45 sites) is a `SchemaVersion == 2` test on the `WorkflowDoc`. `acceptsFix` and its recursion (:1582-1601, cache key `Js.Stringify(candidate)`, cap 128) are ported here: they are needed by the v1 `unknown-endpoint` fixes. `routeVia` returns **`edge.via` itself** (:4315) — the C# route holds the same `JsonArray`-derived points, never mutated. SVG: `renderEdgePath(edge, index)` uses the **canonical** index (:5449, :5457); `String(index+1).padStart(2,'0')` (:450, :534, :1534, :5320); every number via `Js.Num`; `focusNodeAttrs(…, meta.locale)`; `svgRootAttrs(workflow.meta, resolvedQualityProfile)` → `SvgAttrs.SvgRootAttrs(ctx)` for the top-level compile (nested SVG is discarded). Diagnostics: workflow survey §7 table; indexes are **authored** indexes via `sourceIndexes` except `mainPath` (canonical, :1613-1616).

- [ ] **Step 1: Rows.** `Targets()`: one row per manifest entry with `task == 7` (includes `incident-response`, `release-delivery`, `v1-700x400`, `v1-explicit-coordinates`, `v1-baseline.agent-tool-call`, `border-clearance` if v1 — the manifest decides). `WorkflowReceiptTests`:

```csharp
    static JsonObject WithoutDiagnostics(JsonObject receipt) { var r = (JsonObject)receipt.DeepClone(); r.Remove("diagnostics"); return r; }

    public static void AssertReceiptEquals(string name, WorkflowCompileResult result)
    {
        var node = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"workflow/{name}.receipt.json")))!.AsObject();
        Assert.Equal(Js.Stringify(WithoutDiagnostics(node), 2), Js.Stringify(WithoutDiagnostics(result.Receipt), 2));
        var codes = node["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).ToList();
        Assert.Equal(codes, result.Diagnostics.Select(d => d.Code).ToList());
    }

    // Mirrors ArchifyNet.Render (Archify.cs:46-55): normalise, load gates, brand preparation, then the compiler — exactly what
    // render-workflow.mjs hands compileWorkflow after loadDiagramWithBrandMarks.
    public static WorkflowCompileResult CompileFixture(string inputPath, RenderOptions? options = null)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(inputPath));
        var root = DiagramLoader.Normalize(d.DeepClone().AsObject());
        var ctx = new RenderContext(DiagramType.Workflow, root, options ?? new RenderOptions());
        DiagramLoader.Load(DiagramType.Workflow, root, ctx);
        ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Workflow, root, ctx.Diagnostics);
        var profile = ctx.Options.Quality?.Name() ?? root["meta"]!["quality_profile"]?.GetValue<string>();
        return WorkflowCompiler.Compile(ctx, WorkflowDoc.From(root), profile, null);
    }
```

`DiagramLoader.Normalize`/`Load` are `internal` today (the facade calls them); if one is not visible to the test project, use `InternalsVisibleTo` (already set) or make the helper call `ArchifyNet.Render` for success rows and read `r.Layout!.Report`. `[Theory] Workflow_receipt_equals_upstream_layout_json` with one `InlineData(name, inputPath)` per task-7 row. `ParityTests`: `[Theory] Workflow_fault_diagnostics_match_upstream_receipt` with `wf-unknown-main-path` (`wf-v1-column-capacity` needs cluster E → Task 10).
- [ ] **Step 2: Run** → fail. **Step 3: Port.** **Step 4: Iterate** to green (goldens first: they are byte truth; the receipt rows localise a difference to columns/nodes/edges/labels); full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Workflow src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/WorkflowReceiptTests.cs tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): workflow v1 end to end — v1 goldens and receipts byte-identical`.

---

### Task 8: Workflow readable-v2 automatic path — routing, search expansions, canvas, node text, feedback — v2 automatic goldens

**Files:**
- Create: `src/Archify.Net.Core/Workflow/WorkflowCompilation.Routing.cs`
- Modify: `WorkflowCompilation.cs` (replace the `"Task 8"` stubs), `WorkflowCompiler.cs` (cluster O :5646-5848), `tests/Archify.Net.Core.Tests/ParityTests.cs`, `WorkflowReceiptTests.cs`

**Interfaces:**
- Consumes: Tasks 6–7.
- Produces: the readable automatic router (F readable :1267-1352, H4 `clearStraightAutomaticPlan` :2336-2377, J3 feasibility :3402-3749, K1 cost/candidates :3750-4041, K2 `readableAutomaticVia` :4042-4204, K4 v2 label point, K5 endpoint repairs / internal detour / free space :4470-4691, K6 `readableAutomaticRoute` :4693-4934, K8 readable branch, the H5 driver skeleton for automatic edges: reserve straight → `routeFirst` feedback → all edges), L readable branches (`finalizeReadableViewBox` :5154-5312, throws `viewbox-capacity` / origin), M `ReadableSublabelMinimum` + `ValidateReadableNodeText` (:5358-5377, `sublabel-readability`), and `WorkflowCompiler.CompileWithRouteOrderFeedback` / `CompileWithLayoutFeedback` / `CompileWorkflowWithFeedback` (:5667-5848, the sublabel sizing loop included). `validateReadableInputsBeforeRouting` (I2) stays a stub that **returns without problems** (`"Task 9"` comment): it only throws on invalid input, and every task-8 row is valid. Also in this task, because they run on **every** v2 compile even when no edge is pinned: H2 `validateReadablePairwisePinConflicts` (:2060-2192, called unconditionally at :2690) and the pure H1/H3 predicates it and the router call (`hasAbsoluteRoutePins` :1872, `hasAuthoredRouteAssertions` :1735, `presentChannelPins` :2202, `validateReadableRouteControls` :2206, `endpointSideIsHonored` :2239, `routeMatchesPresetFamily` :2279, `routeContainsChannelPin` :2322, `corridorTopologyMatches`). The conflict **throwers** they reach on a hit (`throwExplicitPinConflict`, `throwReadableLabelRoutePinConflict`, `throwReadableLabelLabelPinConflict`, the `verified*Alternatives` fix builders) stay `NotImplementedException("Task 10")` — unreachable without pins. This is the largest task (~2,000 upstream lines, one subsystem); it is one task because no smaller cut yields a receipt — the driver builds the receipt only after routing, validation, viewBox finalisation and node-text checks all ran.

Porting rules: workflow survey §2 J3/K1–K6/O and §3 in full. `compareCost` (:3969-3976) lexicographic over 15 dims with `(x || 0)`; **ties are real** and resolved by `Js.Sort` stability (`ordinalOffset = pairOrdinal*9` collides across side pairs) — never replace with a key-based `OrderBy` on a non-unique key. `readableSideCache` stores `null` and distinguishes `has` from `get` (:1275 vs :4450) → `JsMap<JsonObject, SideChoice?>` with `Has`; save/delete-or-restore at :2360-2375. `pathCache`/`nodeRoutes`/`laterArrivalCache` iteration order matters (insertion order). `cachedSceneLabelObstacles` inserts into the grid on first call (R6). Outside-right search: ≤7 probes × ≤53 bisections on the `Math.ceil(x*1000)/1000` grid (:4095-4166). Point identity at :4765 (R9). `firstFailure` catch-all (R10). `Math.round` at :3961, :3963 → `Js.Round`; `Math.hypot` → `Js.Hypot`. Plus the `joinRoutePoints` fact from the File Structure note.

- [ ] **Step 1: Rows.** `Targets()`: one row per manifest entry with `task == 8` (includes the `holiday-planning` example golden, zh-CN, and the first-draft/viewport/regression v2 inputs). Full-receipt `InlineData` for the same rows. Fault receipts `wf-viewbox-capacity`, `wf-sublabel-readability`. Iterate on the receipt rows first when a golden differs: the receipt localises the difference to columns, nodes, edges or labels.
- [ ] **Step 2: Run** → fail. **Step 3: Port.** **Step 4: Iterate** to green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Workflow tests/Archify.Net.Core.Tests/ParityTests.cs tests/Archify.Net.Core.Tests/WorkflowReceiptTests.cs` → `feat(core): workflow readable-v2 automatic path — v2 goldens and receipts byte-identical`.

---

### Task 9: Workflow v2 typed input diagnostics with verified fixes

**Files:**
- Create: `src/Archify.Net.Core/Workflow/WorkflowCompilation.Inputs.cs`
- Modify: `WorkflowCompilation.cs` (the `"Task 9"` stub), `tests/Archify.Net.Core.Tests/ParityTests.cs`

**Interfaces:**
- Produces: I2 `ValidateReadableInputsBeforeRouting` (:3038-3258): duplicate lane/node ids, unknown edge endpoints and lanes, node overlaps (all pairs at once), each with fixes verified by recompiling candidates through `acceptsFix` (`.filter(acceptsFix).slice(0,3)` evaluates **every** candidate before slicing, :2958-2961, :3115-3118). Sort by `sourceIndexes` identity lookup (:3047-3055). `invalid-node-column` and `non-finite-node-geometry` are dead after the schema gate — port them, no test.

- [ ] **Step 1: Rows.** `Workflow_fault_diagnostics_match_upstream_receipt` gains `wf-duplicate-node-id`, `wf-duplicate-lane-id`, `wf-unknown-edge-endpoint`, `wf-unknown-node-lane`, `wf-node-overlap`, `wf-semantic-reference`, `wf-required-edge` (Review Focus 5). The existing `AssertReceipt` compares codes, messages, severities, subjects, evidence and `supportedFixes`, so the verified-fix lists are pinned.
- [ ] **Step 2: Run** → fail. **Step 3: Port.** **Step 4: Iterate** to green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Workflow tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): workflow typed input diagnostics with verified fixes (receipts match Node)`.

---

### Task 10: Workflow pins, route presets, pin conflicts, legacy column capacity — agent-tool-call golden

**Files:**
- Create: `src/Archify.Net.Core/Workflow/WorkflowCompilation.Pins.cs`
- Modify: `WorkflowCompilation*.cs` (remaining `"Task 10"` stubs), `ParityTests.cs`, `WorkflowReceiptTests.cs`

**Interfaces:**
- Produces: H1 fix verifiers and conflict throwers (:1641-2059, minus the predicates Task 8 ported), the remaining H3 pieces, H5 rest (:2378-2692), K3 `readablePresetVia` (:4205-4303), K7 controlled routes (:4936-5057), N `absoluteGeometrySizingDiagnostic`. (H2 and the pure predicates landed in Task 8; E landed in Task 7 — this task makes its failure path green.)

Porting rules: workflow survey §2 E/H/K3/K7, §7. **`subject.path` of `workflow/explicit-pin-conflict` exists only when exactly one conflicting pin remains** (:1849-1855) and that count depends on verified removal sets from recompiles — the fault receipts pin it. `combinations` is recursive (:1681-1692). `edge.bias ?? 0.5` and `channelX/Y ??` keep 0 (:3995, :4213, :4328-4344, :4360). `readableMigrationProvidesCapacity` passes `authoredQualityProfile` (R4). `workflow.edges.indexOf(edge)` sites (survey §3 identity list) → reference search on the canonical list.

- [ ] **Step 1: Rows.** `Targets()`: `["workflow", Fixtures.Upstream("examples/agent-tool-call.workflow.json"), Fixtures.Upstream("examples/workflow-agent-tool-call-rendered.html")]` and one row per manifest entry with `task == 10`; full-receipt `InlineData` for the same rows; fault receipts `wf-preset-conflict`, `wf-single-pin-conflict`, `wf-v1-column-capacity`, `wf-release-luna`. `workflow/column-capacity` carries `suppresses` (:1224-1228), which `AssertReceipt` does not compare today: extend `AssertReceipt` to compare `suppresses` when the expected diagnostic has it (and require `Diagnostic.Suppresses` to be null when it does not).
- [ ] **Step 2: Run** → fail. **Step 3: Port.** **Step 4: Iterate** to green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Workflow tests/Archify.Net.Core.Tests/ParityTests.cs tests/Archify.Net.Core.Tests/WorkflowReceiptTests.cs` → `feat(core): workflow pins, presets and conflict diagnostics — agent-tool-call golden byte-identical`.

---

### Task 11: Workflow repair hints, option and brand fixtures, golden.mjs cases

**Files:**
- Modify: `WorkflowCompiler.cs` (`addVerifiedCrossingRepair` :5850-5886, `addVerifiedPlacementRepair` :5888-5915), `ParityTests.cs`, `ValidationTests.cs`, `WorkflowReceiptTests.cs`

Porting rules: both repair functions **mutate** the result's diagnostic objects and `compilerFailure` shares one `diagnostics` array with `receipt.diagnostics` (:663-669, :5878-5882, :5903-5913); C# `Diagnostic` is an immutable record — replace the element in **both** lists (`Diagnostics` and whatever the receipt references). `JSON.stringify(removals)` dedupe (:5866) → value-equality key.

- [ ] **Step 1: Rows.** `Targets()`: manifest rows with `task == 11` that render with default options (`holiday-planning.brand`). Fact for Review Focus 3:

```csharp
    [Fact] public void Workflow_quality_option_reaches_every_gate_like_the_env_var()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/agent-tool-call.workflow.json")));
        var r = ArchifyNet.Render(DiagramType.Workflow, d, new RenderOptions(Quality: QualityProfile.Standard));
        Assert.Equal(Fixtures.Lf(Fixtures.Golden("agent-tool-call.option-standard-rendered.html")), r.Html);
    }
```

`ValidationTests`: the two golden.mjs workflow cases — legacy `meta.views` is accepted and ignored at render level (golden.mjs:135-146: render with `meta.views` set equals the golden without it), and `nodes[0].col = 7` fails with a schema error whose message contains `<= 5` (golden.mjs:147). If any manifest coverage row tags `crossing-repair` or `placement-repair`, add its receipt to the full-receipt theory here (the hint text lives in `supportedFixes`, compared through `AssertReceipt`-style field checks: extend `AssertReceiptEquals` to compare `supportedFixes` per diagnostic).
- [ ] **Step 2–4:** run → fail → port → green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Workflow tests/Archify.Net.Core.Tests/ParityTests.cs tests/Archify.Net.Core.Tests/ValidationTests.cs tests/Archify.Net.Core.Tests/WorkflowReceiptTests.cs` → `feat(core): workflow repair hints, quality option and brand fixtures`.

---

### Task 12: Docs, handoff, deviations

**Files:** `README.md` (status table: phase 3 Done; diagram-types table: workflow, dataflow ported; phase-3 parity results with the row counts the suite actually has; Known deviations: the R1 dictionary-script word breaks and host-locale assumption, replacing the phase-2 CJK bullet's scope; the R5 `Layout` note for workflow), `src/Archify.Net.Core/Archify.cs` (XML remarks: R1 deviation; drop "workflow, dataflow" from the `NotSupportedException` text — only erd remains), `SESSION_HANDOFF.md` (phase-3 state, close the `joinRoutePoints` carried item with the Task 8 fact, counts).

- [ ] **Step 1:** edit. **Step 2:** `dotnet test` → all green, no warnings; record the total. **Step 3: Commit** — `git add README.md SESSION_HANDOFF.md src/Archify.Net.Core/Archify.cs` → `docs: phase 3 — README parity results and deviations, handoff`.

## Exit (matches card PORT-003)

- `dataflow-product-analytics.html` and `workflow-agent-tool-call-rendered.html` (upstream goldens) plus Node-minted `event-stream`, `holiday-planning` (zh-CN), `incident-response`, `release-delivery` are byte-identical — the card's exit list.
- Beyond the card: every phase-3 regression fixture except `first-draft.mixed-port-recovery` is byte-identical; that one differs only in its dictionary-wrapped label (R1). Every workflow receipt equals Node's `--layout-json`; every failure receipt equals Node's diagnostics; `word-breaks`, `label-capacity`, `joint-label-routing` and `workflow-model` fixtures pass.
- `dotnet test`: green, no warnings.
