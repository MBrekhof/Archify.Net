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
  2,000 characters (Pele's oracle skips a mutated input over that length; the capture applies that bound to the seeds so oversized stress rows stay in their own rows), and writes all `count` results as one array.
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
  verbatim, the inputs of Pele's `tests/compat/<type>/*` and `tests/render/<type>*.test.ts` (with a sample of the oversized and hostile-payload render cases; each `name` says which), and
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
    and the port's output for them is pinned in `tests/fixtures/charts/xychart.fuzz-deviations.json` (`node tools/chart-seeds/xychart-fuzz-check.mjs` checks them against a patched copy of upstream's xychart chunk; `node tools/chart-seeds/pie-fuzz-check.mjs` does the same for pie).

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
  run the fact twice to re-mint it after the seeds change (`node tools/chart-seeds/quadrant-fuzz-check.mjs` checks them against a patched copy of upstream's quadrant chunk).

- `sankey` (Task 8): archify's tests have no sankey fence and charts.mjs passes no `sankey` config, so `archify-*`,
  `fm-sankey-*` and `directive-config` pin the defaults and the type's own object. `corpus-docs-0` is the corpus entry with
  `config:` front matter. Beyond the list above: `spec-*` (the energy file under both keywords, the `__proto__` ids and the
  issue #7528 sample of `sankey.spec.ts`, built by hand because the spec builds them in code), `oracle-*` (the corpus
  additions of `tests/sankey/oracle.test.ts`, and `oracle-reference-*`, Mermaid's own preparation of the corpus where it
  differs), `layout-*` (the models of `tests/sankey/layout.test.ts` written as chart text), `render-hostile-*` (each
  template of `sankey-security.test.ts` with two of its payloads), `render-extreme-*`, `stress-*` (the shapes of
  `tests/sankey/stress.test.ts` at about 4,000 characters, plus the limit case verbatim), cycles, quoted names with `""`,
  keywords, values, options, titles, error lines, CRLF/CR/BOM and empty bodies. `quirk-rows-*` and `quirk-entity-*`, and
  the existing rows that `ChartParityTests.SankeyQuirkRows` names, are the upstream slips the port fixes: S1 the blanks at
  a row's edges (Mermaid's `prepareTextForParsing` has no `m` flag) and S2 entities written undecoded in a label. Each
  one's `-reference` row is the same chart written so that Node already reads it the corrected way. The `rows-*`,
  `stress-rows-*` and `entity-*` controls still equal Node. Fix round 1 adds Martin's rulings: S3 (`quirk-unicode-*`,
  names outside ASCII, with Pele 1.9.0's alphabet), checked against `-reference` rows with ASCII stand-ins that the test
  swaps back, and S4 (`quirk-value-*`, a value that is not a whole decimal number is an error naming its line), whose
  expected error text is pinned in the test. The `unicode-*` and `values-*` controls still equal Node, and
  `layout-tie-break-*` pins the layout's index tie-break. As for quadrantChart, the differing fuzz cases are listed in
  `FuzzDeviations`, and the port's output for them is pinned in `tests/fixtures/charts/sankey.fuzz-deviations.json`.
  `node tools/chart-seeds/sankey-fuzz-check.mjs` checks each pinned case's attribution against Node, with the input
  corrected by hand for its stated quirk; run it after a re-mint.
- `radar` (Task 9): archify's tests have no radar fence and charts.mjs passes no `radar` config, so `archify-*`,
  `fm-radar-*`, `fm-other-key` and `directive-config*` pin the defaults and the type's own object. `corpus-docs-2` is the
  corpus entry with `config:` front matter. Beyond the list above: `spec-*` (Mermaid's `radar.spec.ts`, the ticks loop
  as three rows), `compat-*` (Mermaid's parser tests; `compat-error-*` are the six skipped error-message tests' inputs),
  `oracle-*` (the corpus additions of `tests/radar/oracle.test.ts`), `render-*` (`radar.test.ts`, the degenerate inputs
  as `render-degenerate-*`), `render-hostile-*` (each template of `radar-security.test.ts` with two of its payloads),
  `stress-*` (the shapes of `tests/radar/stress.test.ts` at about 4,000 characters, plus the limit case verbatim), both
  model errors (`model-*`, `render-missing-entry`, `render-axes-before-curves`), `graticule-*`, `{axis: v}` entries
  (`entries-*`), values, options (`options-*`), every render option of `config.radar` (`config-*`), titles, `accDescr`
  blocks, labels (entities, `<br>`, markup, escapes, Han, emoji, U+FFFE), names, keywords, comments, errors, CRLF/CR/BOM and
  empty bodies. `quirk-boundary-*` and `oracle-23` are RA1 (a name that starts with `circle`, `polygon`, `true`, `false`
  or `title` fails upstream) and `quirk-message-*` are RA2 (a "Missing entry" message writes the label's entity codes as
  parsed), the upstream slips the port fixes. An RA1 row's `-reference` writes each such name with a stand-in first
  letter of the same width (c/k, p/b, t/f), which `ChartParityTests.Radar_name_boundaries_are_the_stated_deviation` swaps
  back; an RA2 row's `-reference` writes the label as the chart draws it. The `boundary-*`, `glue-*` (an option value
  glued to the next keyword or statement, which draws upstream and is not a name) and `message-*` controls still equal
  Node. As for quadrantChart, the differing fuzz cases are listed in `FuzzDeviations`, and the port's output for them is
  pinned in `tests/fixtures/charts/radar.fuzz-deviations.json`. `node tools/chart-seeds/radar-fuzz-check.mjs` checks each
  pinned case's attribution against Node, with the input corrected by hand for its stated quirk; run it after a re-mint.
- `venn` (Task 10): archify's tests have no venn fence and charts.mjs passes no `venn` config, so `archify-*`, `fm-venn-*`,
  `fm-title-*` and `directive-*` pin the defaults, the type's own object and the front-matter title. Beyond the list above:
  `spec-*` (Mermaid's `venn.spec.ts`; `spec-renderer-*` are the models of the skipped `vennRenderer.spec.ts` written as
  text), `oracle-*` (the `SEEDS` of `tests/venn/oracle.test.ts`), `render-*` (`venn.test.ts`; the geometry tests that call
  layout functions directly have no text, except the subsets of "lays out the same circles every time"),
  `render-hostile-*` (each template of `venn-security.test.ts` with two of its payloads), and `stress-*` (the shapes of
  `tests/venn/stress.test.ts` at about 4,000 characters; the layout cases at their own counts where they fit the
  50,000-character limit: 2,000 sets, 400, 300 in a chain, 60 with every pair, one union of 300, 40 unions of 60; and the
  limit case verbatim). The type's own cases cover header forms, CRLF/CR/BOM, empty bodies, 1 to 9 sets and the grid
  layout past 64, disjoint, nested, identical and coincident circles, zero, negative, overflowing and tiny sizes, the
  shrinking triangle, every overlap path (lens, nested, none, 3 to 5 sets), labels, ids, texts and the indent rule, styles,
  titles, comments and every error site. `text requires set` (parser.ts) cannot be reached: an indented `text` is only
  read as one after a set or union, which sets the current sets, and `error-text-requires-set` is the nearest input.
  `quirk-*` rows, and the existing rows that `ChartParityTests.VennQuirkRows` names, are the upstream slips the port fixes:
  Q1 the `[^}]%%` comment rule (`quirk-comment-*`), VN1 a keyword with a dash and more of a name after it
  (`quirk-keyword-dash-*`), VN2 a bare `title` taking the next line (`quirk-title-*`), and VN3 the "unknown set identifier"
  message writing ids undecoded (`quirk-message-*`). Each one's `-reference` row is the same chart written so that Node
  already reads it the corrected way. Fix round 1 adds `quirk-comment-header-*` and `quirk-comment-statement-end-comma` (a
  one-character token after the header or a finished statement, then `%%`: upstream eats it and draws, the port reports
  it; after the header a `-` is VN1 too) and `quirk-message-{line-break, carriage-return, raw-line-break}` (a line break in
  an unknown id is written as `#10;`/`#13;`, so the message keeps every id; their expected messages are pinned in the
  test, as no Node input writes them). The VN1 rows and `quirk-comment-string` write each such name with a stand-in of the
  same advance (s/z, t/f, u/a, v/k, S/K, and a zero-width U+200B before `%%`), which
  `ChartParityTests.Venn_stand_in_rows_are_the_stated_deviation` swaps back. The `comment-*`, `keyword-*`, `title-*` and
  `message-*` controls still equal Node. As for quadrantChart, the differing fuzz cases are listed in `FuzzDeviations`,
  and the port's output for them is pinned in `tests/fixtures/charts/venn.fuzz-deviations.json`.
  `node tools/chart-seeds/venn-fuzz-check.mjs` checks them against upstream itself with each fix written into a copy of
  its vendored venn chunk (`venn-oracle.mjs`), and checks that the fixes change no other fuzz case; run it after a
  re-mint. Q1 is also closed generically (fix round 2): `node tools/chart-seeds/venn-q1-grid.mjs` writes
  `tests/fixtures/charts/venn-q1-grid.json`, every printable ASCII character and tab, NBSP, U+3000 and é placed directly
  before `%%` in each of venn's lexer positions, with upstream's result as is, with Q1 alone and with all four fixes (a
  drawn SVG as its SHA-256). `ChartParityTests.Venn_q1_grid_matches_the_patched_upstream` checks the port against the
  patched results. The capture does not write this file; run the script after changing `venn-oracle.mjs`.
- `lines` (the core line remap after Task 8, UPSTREAM-007): upstream counts an error's line in the text the diagram
  parsed, so front matter, `%%` comment lines, directives, leading blank lines, sankey's blank lines and rows of blanks,
  and archify's pie legend (`showData` on a later line, blanks after a slice's value) move it up; the port counts the
  fence as written. Each row is an error after removed lines in pie, xychart, quadrantChart, sankey, radar or venn (CRLF, lone CR,
  multi-line and mid-line directives, indented front matter, entity codes and HTML attributes before the error, EOF), and
  `ChartParityTests.LineRemapRows` holds its line, written by hand; `control-*` rows still equal Node. It is not a type
  group, so it adds nothing to any fuzz corpus. Fuzz cases that differ only by the remap are `L1` in `FuzzDeviations`, and
  the sankey checker puts Node's error and the S4 value on the input's line with its own map. venn never fails on the
  EOF token (Pele's appended line break is a NEWLINE token first), so its `control-venn-error-then-*` rows are controls,
  and `venn-error-at-text-end-after-comment` is an error on that appended line break, reported on the last content line.
- `fixes` (the final wave, after Task 10): rows for three upstream defects the port fixes, kept out of the type groups so
  that they add nothing to any fuzz corpus. `xml-*` (UPSTREAM-008): U+FFFE, U+FFFF and lone surrogate halves, typed or as
  a code, in every place each type writes text; `ChartParityTests.Xml_invalid_characters_are_dropped` checks that the port
  gives Node's output without them, and `xml-control-*` (an emoji, U+FFFD, U+FFFC, U+10FFFF, `&#xFFFE;`) still equal Node.
  `message-pie-*` (PORT-011): the pie "has invalid value" message names the label as drawn; each row's `-reference` is
  the label written that way, and the `control-*` rows equal Node. `overflow-radar-*` (PORT-011): a radar value near
  1e308; the `-reference` rows are the same charts scaled down, and `overflow-radar-control-large` equals Node.

Pele's tests, and the corpus and inputs copied from them, are MIT licensed (Pele 1.3.0; its compat specs are ported from
Mermaid, also MIT, as their file headers say).

`data.json` is generated from readable literals, so its `name`s carry the meaning. To add a data seed, base64 the file's
bytes, for example `Buffer.from(text).toString('base64')`.
