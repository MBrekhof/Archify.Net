# Chart seeds (PORT-008)

Inputs for the chart parity suite. `node tools/capture-fixtures.mjs` (charts section, at the end) runs each seed through
upstream archify 3.2.0 `explainer/charts.mjs` (`C:\projects\archify`, which uses its vendored Pele 1.3.0) and writes the
goldens. `tests/Archify.Net.Core.Tests/ChartParityTests.cs` compares `ExplainerCharts` with them byte for byte. Node decides
every expected value; nothing here is hand-written output.

## Files

- `<group>.json`: a JSON array of seeds. A seed is one of:
  - `{name, text}`: the golden is `compileExplainerChart(text)`.
  - `{name, file, bytes, spec: {kind, x, y, unit?}}`: `bytes` is the data file's content in base64, and the golden is
    `chartFromData(bytes, file, spec)`. Where `TextDecoder` (`fatal: true`) throws, the golden is `{"notUtf8": true}`: the
    message for that case is the caller's (`sources.mjs:311`, PORT-009).
- `<type>.fuzz.json`: `{seed, count, fragments}`. The capture runs Pele's `random` and `mutator`
  (`tests/support/corpus.ts:34-57`, copied verbatim into the capture script) over the group's own `text` seeds of at most
  2,000 characters (Pele's oracle compares only inputs up to that length), and writes all `count` results as one array.
  `seed` is `0x5EED0000` plus the type's index in charts.mjs's register order (`CHART_TYPES` in the capture: pie 0,
  xychart 1, quadrantChart 2, sankey 3, radar 4, venn 5), and `fragments` is the type's `FRAGMENTS` from Pele's
  `tests/<type>/oracle.test.ts`. The capture refuses any other seed.

Goldens:

- `tests/goldens/charts/<group>/<name>.json` holds `JSON.stringify(result, null, 2) + "\n"`.
- `tests/goldens/charts/<type>/fuzz.json` holds `[{input, result}]`. The test compares it element by element and names
  the index that differs.
- A `PeleError` thrown out of `compileExplainerChart` (front matter: `charts.mjs:25` runs `detectType` outside its
  `try`) is recorded as `{"thrown": {"name", "message", "code", "line"}}`. The C# side compares the `PeleException` that
  `ExplainerCharts.Compile` lets propagate.

The capture deletes `tests/goldens/charts/` before writing it. It refuses a seed name that is not `[\w.-]+`, that repeats
within its group ignoring case (the goldens live on Windows), or that is `fuzz`.

## Groups

A group is compared only once `ChartParityTests.Groups` lists it, and each type task adds its own. Rows that wait on a
later task go into `PendingUntil`, and rows that differ by a stated deviation go into `KnownDeviations`. Both sets are
filtered out of the theory data. A deviation row is pinned by its own fact.

- `data`: every `chartFromData` branch (Task 2).
- `wrapper`: the `NATIVE` redirects, undetected and unsupported text, and archify's `test/explainer-charts.test.mjs`
  inputs. Compared from Task 5 (its xychart rows from Task 6), and `native-treeview` is the UPSTREAM-003 deviation (the
  port redirects `treeView-beta`).
- `<type>` (`pie`, then one per type task): archify's inputs for the type, Pele's `tests/corpus/<type>-docs.json`
  verbatim, the inputs of Pele's `tests/compat/<type>/*` and `tests/render/<type>*.test.ts` (each `name` says which), and
  the type's own edge cases. The capture refuses a type group that has no drawn result or no error result.
- `xychart` (Task 6) adds, beyond that list: `db-*` (the calls of Pele's `xychartDb.spec.ts` written as chart text),
  `oracle-*` (the corpus additions of `tests/xychart/oracle.test.ts`), `render-hostile-*` (each template of
  `xychart-security.test.ts` with two of its payloads), and `stress-*` (the shapes of `tests/xychart/stress.test.ts` at
  about 4,000 characters, since archify keeps Pele's 50,000-character source limit, plus that limit case verbatim). The
  `oracle-5`/`oracle-6` seeds use the text of the grammar's broken markdown-string rule as `upstream/xychart.jison:51-53`
  reads it across its quotes (`pushState`, `MD_STR`). The fuzz fragments are Pele's `FRAGMENTS` without the three made
  from that text: the port reads markdown strings (X3), so the text is a plain backtick to it.
  - Task 6 follow-up: `quirk-*` rows, and the existing rows that `ChartParityTests.XyChartQuirkRows` names, are the Mermaid
    grammar slips the port fixes: Q1 the `[^}]%%` comment rule, X1 the case-sensitive orientation, X2 orientation words
    as keywords anywhere, X3 the broken markdown-string rules, X4 unquoted text losing its spaces. Each one's `-reference`
    row is the same chart written so that Node already reads it the corrected way. The `comment-*`, `orientation-*` and
    `md-*` controls still equal Node. As for quadrantChart, the differing fuzz cases are listed in `FuzzDeviations`,
    and the port's output for them is pinned in `tests/fixtures/charts/xychart.fuzz-deviations.json`.

- `quadrantChart` (Task 7): archify's tests have no quadrant fence, so `archify-*` and `fm-quadrant-*` pin archify's own
  `quadrantChart: {chartWidth: 400, chartHeight: 400}` and the front matter that replaces it. Beyond the list above:
  `db-*` (the `parseStyles` calls of Pele's `quadrantDb.spec.ts` written as chart text), `oracle-*` (the corpus additions
  of `tests/quadrant/oracle.test.ts`), `render-hostile-*` (each template of `quadrant-security.test.ts` with two of its
  payloads), `render-extreme-*`, `stress-*` (the shapes of `tests/quadrant/stress.test.ts` at about 4,000 characters,
  plus the limit case verbatim), every style-validation error of `quadrant/db.ts` (`style-*`, `class-*`), coordinates
  (`coord-*`), label placement including the 300-point limit of the free-place search (`place-*`), and layout options
  (`opt-*`). `phantom-empty-quadrant-label`, `phantom-empty-point-label` and `stress-points` are the deviation where
  an empty label reserves no space (the port fixes it). The `-reference` and `-control` rows are the Node outputs that
  the fixed placement is checked against. `quirk-*` rows, and the existing rows that `ChartParityTests.QuadrantQuirkRows`
  names, are the Mermaid grammar slips the port fixes (Q1 the `[^}]%%` comment rule, Q2 the coordinate's unescaped
  dot, Q3 keywords that fire anywhere). Each one's `-reference` row is the same input written so that Node already
  reads it the corrected way. The `keyword-*` controls that are not in that list still equal Node. The fuzz cases that
  differ for these reasons are listed by index in `FuzzDeviations`. What the port gives for each of them is pinned in
  `tests/fixtures/charts/quadrantChart.fuzz-deviations.json`. That file is port output, not a Node golden: delete it and
  run the fact twice to re-mint it after the seeds change.

Pele's tests, and the corpus and inputs copied from them, are MIT licensed (Pele 1.3.0; its compat specs are ported from
Mermaid, also MIT, as their file headers say).

`data.json` is generated from readable literals, so its `name`s carry the meaning. To add a data seed, base64 the file's
bytes, for example `Buffer.from(text).toString('base64')`.
