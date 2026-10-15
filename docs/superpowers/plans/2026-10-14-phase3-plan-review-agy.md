# Phase 3 plan review (agy / Gemini 4 Argon), 2026-10-14

Review of `docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md` before execution, verdict "Ready after fixes".
Settlement: B1-B7, S1-S2 and N1 were applied to the plan before commit b1b1539, after Claude re-ran every fault edit and the
brand variants under Node. Parts of B1, B3, B4 and B7 quote text that was never in the plan (`Fixtures/inputs/`,
`wfd-*` names, anchors and example names that do not exist); those parts were rejected, the substance of each kept.

## Blockers

### B1. Task 1 Step 1 copies an intentional negative fixture (`release-luna.workflow.json`) into `Fixtures/inputs/` and doubles file extensions on `wfd-*`, `wfv-*`, `dfd-*`
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L106-L133) (and counts at [lines 5, 39](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L5)).
- **Evidence:**
  1. `test/fixtures/workflow-first-draft/` in upstream contains **6** `.json` files, making `phase3Copies` 29 files (20 workflow + 9 dataflow) rather than 28. The 6th file, `test/fixtures/workflow-first-draft/release-luna.workflow.json`, is an intentional negative fixture asserted to fail with exit status `1` and 7 `workflow/node-overlap` diagnostics in [architecture-first-draft.test.mjs](file:///C:/projects/archify/test/architecture-first-draft.test.mjs#L261-L269). When copied into `Fixtures/inputs/`, Step 3's render loop in [capture-fixtures.mjs](file:///C:/projects/Archify.Net/tools/capture-fixtures.mjs#L69-L81) runs `render-workflow.mjs` on it and aborts with `FAIL render ...` (`process.exit(1)`).
  2. All files in `test/fixtures/workflow-first-draft/`, `test/fixtures/workflow-viewport/`, and `test/fixtures/dataflow-first-draft/` already end in `.workflow.json` or `.dataflow.json`. Calling `f.replace(/\.json$/, '.workflow.json')` and `f.replace(/\.json$/, '.dataflow.json')` at [lines 125, 127, 129](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L124-L129) produces doubled suffixes (`wfd-automatic-port-slot.workflow.workflow.json`, `dfd-ml-features.dataflow.dataflow.json`). Step 6 (`p3Faults` at [lines 206, 208, 211, 212](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L206-L212)) then tries to open `Fixtures/inputs/wfd-automatic-port-slot.workflow.json` (single suffix) and crashes with `ENOENT`.
- **Fix:** Match Phase 2's convention in [capture-fixtures.mjs](file:///C:/projects/Archify.Net/tools/capture-fixtures.mjs#L52-L59) and exclude `release-luna.workflow.json` from `Fixtures/inputs/`:
  ```js
  ...fs.readdirSync(`${UP}/test/fixtures/workflow-first-draft`)
    .filter(f => f.endsWith('.json') && f !== 'release-luna.workflow.json').sort()
    .map(f => [`test/fixtures/workflow-first-draft/${f}`, `wfd-${f}`]),
  ...fs.readdirSync(`${UP}/test/fixtures/workflow-viewport`).filter(f => f.endsWith('.json')).sort()
    .map(f => [`test/fixtures/workflow-viewport/${f}`, `wfv-${f}`]),
  ...fs.readdirSync(`${UP}/test/fixtures/dataflow-first-draft`).filter(f => f.endsWith('.json')).sort()
    .map(f => [`test/fixtures/dataflow-first-draft/${f}`, `dfd-${f}`]),
  ```
  And in Step 6 (`p3Faults`), read `release-luna.workflow.json` directly from `${UP}/test/fixtures/workflow-first-draft/release-luna.workflow.json` (no mutation needed; it already emits 7 `workflow/node-overlap` errors). Also update the example count at [line 5](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L5) from `9` to `6` (`archify/examples` has 4 workflow + 2 dataflow examples: `agent-tool-call`, `holiday-planning`, `incident-response`, `release-delivery`, `event-stream`, `product-analytics`).

### B2. Task 1 Step 2 uses 3 non-existent brand IDs and places a brand on `holiday-planning.nodes[0]` (`"today"`), which fails upstream layout validation
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L149-L157).
- **Evidence:**
  1. Probing `C:\projects\archify\archify\brand-marks\catalog.json` shows `'snowflake'` exists, but `'chrome'`, `'google-calendar'`, and `'slack'` do not exist in `marks[].id`.
  2. Even with valid catalog IDs, setting `hp.nodes[0].brand` (`id: "today"`, `label: "今天 9月22日"`) causes `render-workflow.mjs` in Step 3 to exit `1`:
     `Workflow layout validation failed: - Node "today" brand top rail leaves 44px for its label, but "今天 9月22日" needs ~65px at the 9px legible minimum — widen the node or shorten the label.`
     This happens because [`automaticNodeWidth`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L171-L179) does not widen nodes for `brand`, leaving `nodes[0].width = 92`, which fails [`brandTopRailProblem`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L1027). In `holiday-planning.workflow.json`, `nodes[1]` (`mid_autumn`, `"中秋节"`), `nodes[2]` (`national_day`, `"国庆节"`), and `nodes[4]` (`makeup_work`, widened to `148px` by its sublabel) fit a brand mark without widening `node.width`.
- **Fix:** Use valid catalog IDs and attach the workflow brand marks to `nodes[1]` and `nodes[2]` (or `nodes[4]`):
  ```js
  pa.nodes[0].brand = 'react'; pa.nodes[6].brand = 'snowflake';
  writeJson(path.join(FIX, 'inputs/product-analytics.brand.dataflow.json'), pa);
  const hp = JSON.parse(fs.readFileSync(`${EX}/holiday-planning.workflow.json`, 'utf8'));
  hp.nodes[1].brand = 'notion'; hp.nodes[2].brand = 'wechat';
  writeJson(path.join(FIX, 'inputs/holiday-planning.brand.workflow.json'), hp);
  ```

### B3. Task 1 Step 4's workflow coverage probe crashes on 4 non-existent anchors, injects `SyntaxError`s into parameter lists, uses unavailable `vm.SourceTextModule`, and assigns all workflows to Task 10
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L169-L185).
- **Evidence:**
  1. **Missing anchors:** 4 of the 8 needles at [lines 174–177](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L174-L177) (`'function throwViewBoxCapacityDiagnostic('`, `'function readableSublabelProblems('`, `'function throwShortEdgeDiagnostic('`, and `'if (workflow.schema_version === 2) {\n    layout = buildReadableLayout();'`) do not appear in [workflow-compiler.mjs](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs) (the actual functions are [`finalizeReadableViewBox`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5253), [`validateReadableNodeText`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5363), [`validateReadableInputsBeforeRouting`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L3038), and [`createReadableLayout`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L1425)). `assert(wfSrc.split(needle).length === 2, needle)` immediately throws `AssertionError`.
  2. **Invalid JS injection:** Replacing `'function readableMigrationProvidesCapacity('` with `needle + '__hit.add(11);'` produces `function readableMigrationProvidesCapacity(__hit.add(11);from, to, requiredClearance) {` — injecting a statement inside the formal parameter list (`SyntaxError`).
  3. **`vm.SourceTextModule` is `undefined`:** Running `node -e "import('node:vm').then(vm => console.log(vm.SourceTextModule))"` under Node v24.12.0 prints `undefined` unless `--experimental-vm-modules` is passed, whereas [line 231](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L231) runs `node tools/capture-fixtures.mjs` with no flags.
  4. **Over-broad probe placement:** [`enforceLegacyColumnCapacity()`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L2733) is called unconditionally on every v1 and v2 workflow (returning early at `:1179` on v2 and `:1190` on valid v1), and [`hasAbsoluteRoutePins(edge)`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L2383) is called on every edge of every v2 workflow. Probing at the top of those functions adds `10` to `hit` for 100% of workflow inputs, so `Math.max(7, ...hit)` assigns every workflow input to Task 10 and leaves `ParityTests(7)`, `ParityTests(8)`, and `ParityTests(9)` with 0 rows.
- **Fix:** Instrument statement boundaries that only execute when a task's feature is active, rewrite relative imports, and import via a `data:text/javascript;base64,...` URL (which requires no CLI flags):
  ```js
  const wfFileUrl = pathToFileURL(`${UP}/archify/renderers/workflow/workflow-compiler.mjs`);
  let wfSrc = fs.readFileSync(wfFileUrl, 'utf8').replace(/\r\n/g, '\n');
  for (const [t, needle] of [
    [10, 'function readableControlledRoute(edge, from, to) {\n'],
    [10, 'function readablePresetVia(edge, from, to, start, end, fromSide, toSide) {\n'],
    [9,  'widthOverrides.set(node.id, width);\n'],
    [8,  'function createReadableLayout(workflow, layoutFeedback = {}) {\n'],
  ]) {
    assert.equal(wfSrc.split(needle).length, 2, needle);
    wfSrc = wfSrc.replace(needle, `${needle}globalThis.__wfHit?.add(${t});\n`);
  }
  wfSrc = wfSrc.replace(/from '(\.[^']+)'/g, (_, rel) => `from '${new URL(rel, wfFileUrl).href}'`);
  const { compileWorkflow: compileWfProbed } = await import(
    `data:text/javascript;base64,${Buffer.from(wfSrc).toString('base64')}`
  );
  function workflowTaskFor(inputPath) {
    globalThis.__wfHit = new Set();
    compileWfProbed({ workflow: JSON.parse(fs.readFileSync(inputPath, 'utf8')) });
    return Math.max(7, ...globalThis.__wfHit);
  }
  ```
  Verified under Node across all 24 valid workflow inputs: this assigns the 5 `v1` inputs to Task `7`, the 16 unpinned `v2` inputs (including `holiday-planning.brand`) to Task `8`, `workflow-route-text-recovery` to Task `9`, and `agent-tool-call` + `workflow-shared-trunks` to Task `10`.

### B4. Task 1 Step 5 omits `await` on `loadDiagramWithBrandMarks` and reads non-existent `c.layout.W` / `c.layout.H` / `c.layout.colXs` on `compileWorkflow`'s return value
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L187-L197).
- **Evidence:**
  1. [`loadDiagramWithBrandMarks`](file:///C:/projects/archify/archify/renderers/shared/cli.mjs#L107-L111) is `export async function loadDiagramWithBrandMarks(options)`. Calling it without `await` at [line 189](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L189) destructures `diagram` from a `Promise`, passing `workflow: undefined` into `compileWorkflow`.
  2. [`compileWorkflow`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5617-L5618) returns `{ ok: true, svg, receipt }` where [`measuredPlanReceipt`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5541-L5569) returns `{ contract, viewBox, requiredViewBox, columns, nodes, edges, labels, diagnostics }`. There is no `c.layout` property (`c.layout.W` throws `TypeError: Cannot read properties of undefined (reading 'W')`), and when `c.ok === true`, diagnostics live at `c.receipt.diagnostics` (`c.diagnostics` is `undefined`).
  3. Also, [`shared/diagnostics.mjs:4`](file:///C:/projects/archify/archify/renderers/shared/diagnostics.mjs#L4) evaluates `const DIAGNOSTIC_MODE = process.env.ARCHIFY_DIAGNOSTIC_FORMAT === 'json'` once at module import time; `process.env.ARCHIFY_DIAGNOSTIC_FORMAT = 'json'` must be set at the top of `tools/capture-fixtures.mjs` before importing `workflow-compiler.mjs`.
- **Fix:** Set `process.env.ARCHIFY_DIAGNOSTIC_FORMAT = 'json'` before dynamic imports, `await loadDiagramWithBrandMarks(...)`, and write `c.receipt.viewBox`, `c.receipt.columns`, and `c.ok ? c.receipt.diagnostics : c.diagnostics`:
  ```js
  process.env.ARCHIFY_DIAGNOSTIC_FORMAT = 'json';
  const { compileWorkflow } = await import(pathToFileURL(`${UP}/archify/renderers/workflow/workflow-compiler.mjs`).href);
  const { loadDiagramWithBrandMarks } = await import(pathToFileURL(`${UP}/archify/renderers/shared/cli.mjs`).href);
  for (const e of manifest.filter(e => e.type === 'workflow')) {
    const inputPath = path.join(FIX, e.input);
    const { diagram: workflow } = await loadDiagramWithBrandMarks({
      rendererDir: `${UP}/archify/renderers/workflow`, diagramType: 'workflow',
      defaultExample: 'incident-response.workflow.json', argv: ['node', 'render-workflow.mjs', inputPath],
    });
    const c = compileWorkflow({ workflow });
    writeJson(path.join(FIX, 'receipts', e.name + '.workflow.receipt.json'), {
      ok: c.ok,
      diagnostics: c.ok ? c.receipt.diagnostics : c.diagnostics,
      ...(c.ok ? { layout: { viewBox: c.receipt.viewBox, colXs: c.receipt.columns } } : {}),
    });
  }
  ```

### B5. 6 of the 19 fault mutations in Task 1 Step 6 (`p3Faults`) fail to trigger their target diagnostic or differentiating evidence
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L199-L229), Task 6 Step 3 ([lines 352–353](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L352-L353)), Task 9 Step 4 ([lines 460–462](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L460-L462)), Task 10 Step 3 ([lines 479–481](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L479-L481)), Task 11 Step 3 ([lines 498–500](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L498-L500)).
- **Evidence (verified by running each mutation through upstream `render-workflow.mjs` / `render-dataflow.mjs` with `ARCHIFY_DIAGNOSTIC_FORMAT=json`):**
  1. `wf-viewbox-capacity` ([line 200](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L200)): `d.meta.viewBox = [480, 360]` is rejected by `workflow.schema.json` (`viewBox[0]` has `minimum: 700`), emitting `schema/minimum` at `/meta/viewBox/0` instead of `workflow/viewbox-capacity`.
  2. `wf-sublabel-readability` ([line 201](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L201)): `d.nodes[1].sublabel = '中秋节连休三天，节后调休上班，记得提前安排行程和车票'; d.nodes[1].width = 96;` fails earlier inside `validateWorkflow()` ([`workflowNodeTextProblems`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L1035-L1037)) with `layout/constraint` because the sublabel exceeds `availableNodeTextWidth(96)` even at the 6px floor, and `holiday-planning`'s 802px canvas has [`readableSublabelMinimum() === 6`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5356-L5361) (`802 <= DESKTOP_READER_DIAGRAM_WIDTH (1346)`).
  3. `wf-preset-conflict` ([line 205](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L205)): `d.edges[8].channelY = 40` on `agent-tool-call.workflow.json` (`edges[8].route === "outside-right"`) is rejected in [`validateReadableRouteControls`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L2206-L2223) with `workflow/explicit-pin-conflict` (`invariant: "route preset compatibility"`), never reaching [`readablePresetVia`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L4280).
  4. `wf-v1-column-capacity` ([line 210](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L210)): `for (const n of d.nodes) if (n.col >= 3) n.col = 3;` on `incident-response.workflow.json` stacks same-lane nodes onto column 3; [`enforceLegacyColumnCapacity`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L1183) skips `from.col === to.col`, so it emits 9 `layout/constraint` errors and never emits `workflow/column-capacity`.
  5. `df-node-below-area` & `df-node-below-area-raised` ([lines 214–215, 221](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L214-L215)): First, the serialized evidence field in [`dataflow-compiler.mjs:363`](file:///C:/projects/archify/archify/renderers/dataflow/dataflow-compiler.mjs#L363) is `evidence.requiredViewBoxFitsViewport` (not `evidence.raisedCanvasFits`, which is the local variable name at `:323`). Second, both mutations place `d.nodes[9]` at `row = 4` (`rowYs[4] = 584`), where `requiredViewBoxHeight` is `584 + 60 + 58 + 74 = 776 > maximumViewBoxHeight (696)`, so **both** emit `requiredViewBoxFitsViewport: false`.
  6. `df-pinned-crossing` ([line 217](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L217)): `d.flows[4].via = [[560, 120], [560, 420]]` on `product-analytics.dataflow.json` leaves `d.flows[4].route = "straight"` and places `x = 560` to the left of `consent`'s right anchor (`586, 157`), producing `clean-flow/edge-through-node`, `composition/label-route-clearance`, and `layout/constraint`, with **no** `composition/proper-crossing`.
- **Fix:** Replace those 6 entries in `p3Faults` (all verified under Node to emit the exact target code, fixes, and evidence) and update [line 221](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L221) to refer to `evidence.requiredViewBoxFitsViewport`:
  ```js
  ['wf-viewbox-capacity',    'holiday-planning.workflow.json',    d => { d.meta.viewBox = [700, 360]; }],
  ['wf-sublabel-readability','holiday-planning.workflow.json',    d => { for (const n of d.nodes) n.width = 280; d.nodes[1].width = 92; d.nodes[1].sublabel = '1234567890123456789012'; }],
  ['wf-preset-conflict',     'agent-tool-call.workflow.json',     d => { d.edges[0].route = 'drop'; }],
  ['wf-v1-column-capacity',  'v1-workflow-explicit-coordinates.workflow.json', d => { d.nodes[2].col = 1; d.nodes[3].col = 2; }],
  ['df-node-below-area',     'product-analytics.dataflow.json',   d => { d.nodes[9].row = 4; d.nodes[9].yOffset = 300; }],
  ['df-node-below-area-raised','product-analytics.dataflow.json', d => { d.nodes[9].row = 2; d.nodes[9].yOffset = 160; }],
  ['df-pinned-crossing',     'product-analytics.dataflow.json',   d => { delete d.flows[4].route; d.flows[4].via = [[610, 157], [610, 320], [660, 320], [660, 157]]; }],
  ```

### B6. Task 2 Step 3's `Js.WordBreaks` specification contradicts `uax29proto.mjs` on `U+200B` (`Cf` in .NET) and WB15/16 (`RI × RI`)
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L19) and [lines 271–278](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L271-L278).
- **Evidence:**
  1. [uax29proto.mjs:35-38](file:///C:/projects/Archify.Net/.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/uax29proto.mjs#L35-L38) attaches trailing `Cf` clusters to the preceding cluster only when `!/[\u200b\u200c\u200d]/.test(segment) && !['NL', 'WS'].includes(out.at(-1).c)`. In .NET, `CharUnicodeInfo.GetUnicodeCategory(0x200B)` (`ZWSP`) is `UnicodeCategory.Format` (`Cf`). If `U+200B` is treated as WB4 `Format` or attached to `'NL'`/`'WS'`, `"a\u200bb"` in `battery-strings.json` merges into `["a\u200bb"]` instead of V8's `["a", "\u200b", "b"]`, failing `WordSegmenter_MatchesV8_OnNonDictionaryBattery`.
  2. [uax29proto.mjs:46-61](file:///C:/projects/Archify.Net/.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/uax29proto.mjs#L46-L61) intentionally has **no** `RI × RI` (WB15/16) branch because [`Js.Graphemes`](file:///C:/projects/Archify.Net/src/Archify.Net.Core/Js/JsText.cs#L156-L162) (GB12/13) already pairs Regional Indicators into 2-indicator grapheme clusters (`"🇩🇪🇫🇷"` → graphemes `["🇩🇪", "🇫🇷"]`). Adding a word-level `p == RI && n == RI => join = true` over grapheme clusters merges adjacent flag emojis `"🇩🇪🇫🇷"` into a single word `"🇩🇪🇫🇷"`, breaking the battery test.
- **Fix:** In Task 2 Step 3 ([line 273](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L273)) and R1 ([line 19](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L19)):
  1. Specify that WB4 attachment applies to a grapheme cluster only when all of its runes are `Mn`/`Me`/`Cf` **excluding** `U+200B`, `U+200C`, `U+200D` (or when it contains `U+200D` after a base rune within the cluster, already grouped by `Js.Graphemes`), and only when the preceding cluster's class is not `NL` or `WS`.
  2. Remove `+ WB15/16` from the word-boundary join list (note that `Js.Graphemes` already enforces GB12/13 per flag pair, so adjacent `RI` grapheme clusters must not be joined in `WordBreaks`).

### B7. Task 7/8/9 boundary splits break `ParityTests(7)` and `ParityTests(8)` + `WorkflowReceiptTests(8)`
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L75) and [lines 364, 421, 433, 450](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L364).
- **Evidence:**
  1. **Task 7 (`enforceLegacyColumnCapacity`):** [Line 364](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L364) states: `v2-only call sites throw NotImplementedException("Task 8") (readable routing), ("Task 9") (input diagnostics), ("Task 10") (pins, presets, legacy capacity E)`. However, [`enforceLegacyColumnCapacity()`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L1178-L1190) is called unconditionally inside [`validateWorkflow()` at line 2733](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L2733) and runs on **`schema_version === 1`** (`if (workflow.schema_version !== 1) return;`). If it throws `NotImplementedException("Task 10")` when called in Task 7, all 5 `v1` workflow fixtures fail in Task 7.
  2. **Task 8 vs Task 9 (`finalizeReadableViewBox` & `readableSublabelMinimum`):** The file structure table ([line 75](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L75)) and Task 9 Step 2 ([line 450](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L450)) defer Cluster I (`5154–5311`: [`measuredContentBounds`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5154), [`measuredPlanFootprint`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5242), [`finalizeReadableViewBox`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5253)) and Cluster J (`5353–5399`: [`readableSublabelMinimum`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5356), [`validateReadableNodeText`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5363)) to Task 9. Yet [`finalizeReadableViewBox()`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5258-L5281) is what updates `viewBox` and `requiredViewBox` from `measuredPlanFootprint()` for every implicit-viewBox v2 workflow! Without `5154–5282` in Task 8, `workflow-dense-automatic.workflow.json` (`initialViewBox [802, 786]` vs `finalViewBox [814, 786]`), `workflow-return-arrival.workflow.json` (`[1153, 1276]` vs `[1192, 1276]`), and `wfv-order-overflow.workflow.json` (`[837, 786]` vs `[872, 786]`) fail both `ParityTests(8)` and `WorkflowReceiptTests(8)`. Likewise, [`renderNode()`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5406) (ported in Task 7) calls `readableSublabelMinimum()` (`5356–5361`), and `Run()` (`5608–5618`) calls `validateReadableInputsBeforeRouting()` (`:5609`) and `validateReadableNodeText()` (`:5613`) on every v2 compile.
  3. **Task 8 Step 6 example list ([line 433](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L433)):** Claims `ParityTests(8)` covers `agent-tool-call, customer-support-escalation, pull-request-review, security-incident-lifecycle`. The last three files do not exist in `archify/examples`, and `agent-tool-call.workflow.json` has `route: "outside-right"`, explicit `fromSide`/`toSide`, and `labelDy: 34` (so it enters `readableControlledRoute` and `readablePresetVia`, which are ported in Task 10; only `wfd-agent-tool-call.workflow.json` is unpinned and runs in Task 8).
- **Fix:**
  1. In Task 7 Step 1 ([line 364](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L364)), make `enforceLegacyColumnCapacity()`, `validateReadableInputsBeforeRouting()`, and `validateReadableNodeText()` no-op stubs (or have `enforceLegacyColumnCapacity()` run the clearance check `1179–1190` and only throw `NotImplementedException("Task 10")` when `actualSignedClearance < requiredDirectClearance`), and port `readableSublabelMinimum()` (`5356–5361`) alongside `renderNode()`.
  2. In Task 8, port `measuredContentBounds`, `measuredPlanFootprint`, and the non-error path of `finalizeReadableViewBox` (`5154–5282`) so implicit-viewBox v2 workflows compute their final `viewBox` and `requiredViewBox` in Task 8, leaving only the diagnostic throws in `finalizeReadableViewBox` (`5261–5278`, `5283–5310`), `validateReadableNodeText` (`5363–5399`), `validateReadableInputsBeforeRouting` (`3038–3255`), `absoluteGeometrySizingDiagnostic` (`5571–5606`), and the sublabel-widening loop (`5819–5846`) for Task 9.
  3. Update Task 8 Step 6 ([line 433](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L433)) to list `holiday-planning`, `holiday-planning.brand`, `wfd-agent-tool-call`, and the unpinned v2 regression fixtures, and list `agent-tool-call` under Task 10 Step 4.

---

## Should-fix

### S1. Task 5: `DataflowCompiler.StageTop` / `StageBottom` mutate `compositionFrames[index].height` in place, but C# `Frame` is a `readonly record struct`
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L20-L24) (R2) and [lines 322–329](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L322-L329).
- **Evidence:** In [`dataflow-compiler.mjs:96-102`](file:///C:/projects/archify/archify/renderers/dataflow/dataflow-compiler.mjs#L96-L102), `compositionFrames` is initialized with `height: 0`. Then during [`validateDataflow()`](file:///C:/projects/archify/archify/renderers/dataflow/dataflow-compiler.mjs#L387-L388) (before `renderSvg()`), [`stageHeaderBounds`](file:///C:/projects/archify/archify/renderers/dataflow/dataflow-compiler.mjs#L1377) calls [`stageTop`](file:///C:/projects/archify/archify/renderers/dataflow/dataflow-compiler.mjs#L1354) and [`stageBottom`](file:///C:/projects/archify/archify/renderers/dataflow/dataflow-compiler.mjs#L1372), which mutate `compositionFrames[index].height = height` in place so [`cleanBorderRunProblems`](file:///C:/projects/archify/archify/renderers/dataflow/dataflow-compiler.mjs#L505) sees the actual stage frame height instead of `0`. In C#, [`Frame`](file:///C:/projects/Archify.Net/src/Archify.Net.Core/Shared/Geometry.cs#L11) is a `readonly record struct`, so `compositionFrames[index].Height = height` will not compile.
- **Fix:** Note in Task 5 Step 2 ([line 327](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L327)) that `StageTop(index)` and `StageBottom(index)` must update `compositionFrames[index] = compositionFrames[index] with { Height = height };`.

### S2. Task 8 requires H2/H3 predicate helpers (`hasAbsoluteRoutePins`, `presentChannelPins`, `routeMatchesPresetFamily`, `routeContainsChannelPin`, `endpointSideIsHonored`) during automatic routing
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L417-L423) and [line 472](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L472).
- **Evidence:** In [`workflow-compiler.mjs`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs), [`validateReadablePinnedGeometry`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L2383), [`readableCandidateIsFeasible`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L4148), [`pathFor`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L5087-L5120), and [`validateReadablePairwisePinConflicts`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L2075) call pure predicates from Clusters H1–H3 (`hasAbsoluteRoutePins` at `:1872`, `hasAuthoredRouteAssertions` at `:1735`, `validateReadableRouteControls` at `:2206`, `presentChannelPins` at `:2202`, `endpointSideIsHonored` at `:2239`, `routeMatchesPresetFamily` at `:2279`, `routeContainsChannelPin` at `:2322`) even when every edge is automatic (they return `false` / `[]` / `true`).
- **Fix:** State explicitly in Task 8 Step 3 ([line 421](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L421)) that those pure predicate helpers are included in Task 8 (or return `false`/`true` on unpinned edges), while the pin/preset conflict diagnostic builders and `readableControlledRoute` / `readablePresetVia` remain in Task 10.

---

## Nice-to-have

### N1. `ParityTests.AssertReceipt` does not assert `Diagnostic.Suppresses`
- **Plan:** [2026-10-14-phase3-workflow-dataflow.md](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L479-L481).
- **Evidence:** [`enforceLegacyColumnCapacity`](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs#L1224-L1228) emits `suppresses: ['workflow/short-edge', 'clean-flow/endpoint-side-direction', 'workflow/label-node-overlap']` on `workflow/column-capacity`. [`Diagnostic`](file:///C:/projects/Archify.Net/src/Archify.Net.Core/Shared/Diagnostics.cs#L17) already has `IReadOnlyList<string>? Suppresses = null`, but [`ParityTests.AssertReceipt`](file:///C:/projects/Archify.Net/tests/Archify.Net.Core.Tests/ParityTests.cs#L416-L430) only checks `code`, `severity`, `message`, `subject`, `evidence`, and `supportedFixes`.
- **Fix:** In Task 10 Step 3, either add a 3-line check in `ParityTests.AssertReceipt` for `e.TryGetProperty("suppresses", ...)` or assert `Suppresses` in the `wf-v1-column-capacity` fact.

---

## Strengths
- **Rulings R1–R4 are well-verified against upstream:** We independently ran [uax29proto.mjs](file:///C:/projects/Archify.Net/.superpowers/sdd/2026-10-14-phase3-workflow-dataflow/uax29proto.mjs) against `battery-strings.json` + `v8-baseline.json` (confirming 0 mismatches across all 6,286 non-dictionary strings and 0 code-point differences between `\p{Script=Hebrew}` / `\p{Script=Hangul}` and the explicit range tables at [line 273](file:///C:/projects/Archify.Net/docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md#L273)), verified that `Box.Cx`/`Box.Cy` storable properties do not affect any Phase 1/2 renderer, and confirmed R4's distinction between `qualityProfile` and `diagram.meta?.quality_profile` across both compilers.
- **Clean partial-class decomposition of `WorkflowCompiler`:** Splitting the 5,921-line closure in [workflow-compiler.mjs](file:///C:/projects/archify/archify/renderers/workflow/workflow-compiler.mjs) into a private `WorkflowCompilation` instance across 6 focused partial-class files (`WorkflowCompiler.cs`, `.Layout.cs`, `.Routing.cs`, `.InputDiagnostics.cs`, `.PinRepair.cs`, `.Svg.cs`) plus `WorkflowMigrationGeometry.cs` and `WorkflowPlacementRepair.cs` preserves JS closure semantics without a single 6,000-line C# file.
- **Thorough unit-test scaffolding for isolated sub-algorithms:** Dedicated unit tests for `DataflowLabelCapacityTests`, `JointLabelRoutingTests`, `WorkflowPlacementRepairTests`, `WorkflowMigrationGeometryTests`, and `WorkflowReceiptTests` test the complex geometric solvers before full SVG parity runs.

---

## Verdict

**Plan quality:** Ready after fixes

The architectural design, partial-class structure, and rulings R1–R4 are sound, but executing Task 1 (`tools/capture-fixtures.mjs`), Task 2 (`Js.WordBreaks`), and the Task 7/8/9 boundary checks verbatim will fail immediately due to the concrete fixture, probe, and task-split bugs documented in B1–B7. Applying the targeted fixes in B1–B7 and S1–S2 makes the plan directly executable end-to-end.
