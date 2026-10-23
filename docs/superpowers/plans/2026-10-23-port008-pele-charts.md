# PORT-008: Pele 1.3.0 chart rendering in C# — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port Pele 1.3.0's six chart types and archify 3.2.0's `explainer/charts.mjs` wrapper to C#, with SVG and
error output byte-identical to Node, so that PORT-009 (the explainer type) can bake charts without Node.

**Architecture:**
- `Archify.Net.Core/Pele/` mirrors Pele's `src/` tree, file for file: core, text, svg, diagrams/common, and one folder
  per type. Port from the TypeScript at tag `1.3.0`, never from the minified vendor files.
- `Js/JsMath.cs` reproduces V8's `Math.sin/cos/acos/atan2/log10` bit for bit, because .NET's differ by 1 ulp on
  1–18 % of inputs.
- `Explainer/ExplainerCharts.cs` ports `charts.mjs`: `compileExplainerChart`, `chartFromData` and `pieLegend`.
- Node mints every expected byte through upstream `charts.mjs`; the C# side only compares.

**Tech Stack:** .NET 10, C# (Core: no NuGet; System.Text.Json is BCL), xUnit, Node 24 for capture.

**Spec:**
- Card PORT-008 (CARD-2358) body.
- The research report `docs/superpowers/specs/2026-10-23-port008-pele-research.md`, committed with this plan. Its
  section numbers (§n) are cited below.
- The port spec `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (## Testing, R12) sets the parity bar.

## Global Constraints

- Byte-identical output. For each chart type, `ExplainerCharts.Compile(text)` equals upstream
  `compileExplainerChart(text)` field for field: `svg`, `width`, `height`, `type`, or `error`, `line`, `example`.
  `ExplainerCharts.FromData` equals `chartFromData`.
- Pele pin: `pele` 1.3.0. Tag commit `35a83130d8c8a2b6718429e8f577d96d01f41544`; npm tarball sha256
  `8c55d4f2335367c31e77550dfbbd00c99533c432b9323aa37730188c0a355da5`. Upstream archify pin: v3.2.0 (`208acba8`).
- Read Pele source from the checkout `C:\projects\pele`, detached at `1.3.0` (Task 1 creates it). Use read-only git:
  `git -C C:\projects\pele show 1.3.0:src/...`. Never check out another commit there.
- No NuGet dependency in Core. Use `Js.*` for JS semantics: `Js.Num`, `Js.Round`, `Js.ToFixed`, `Js.ToNumber`,
  `Js.ParseFloat`, `Js.Sort`, `Js.SortDefault`, `JsMap`, `JsSet`, `Js.IsSpace`, `Js.Trim`, `Js.IsHan` and `Js.Hypot`.
  Use `JsMath.*` for every transcendental function on the chart path; `System.Math.Sin/Cos/Acos/Atan2/Log10` are banned
  under `Pele/`. Task 1 adds a test that greps for them.
- JS regex classes are hand-written (§4.6). Never use .NET `\s`, `\w` or `.` where JS semantics differ, and never use
  `RegexOptions.ECMAScript`.
- Diagnostics from `render()` are returned, not thrown, at the public edge; only `ExplainerCharts` catches
  `PeleException`, and no other exception type is swallowed (charts.mjs rethrows non-Pele errors). Exception, as
  upstream: `charts.mjs:25` runs `detectType` (and so `preprocess`) *outside* its `try`, so a front-matter YAML error
  propagates out of `compileExplainerChart`. `ExplainerCharts.Compile` lets that `PeleException` propagate; it is
  therefore `public sealed class PeleException : Exception { string? Code; int? Line; }` (review B1).
- `System.Math` is banned under `Pele/` and in `ExplainerCharts` for `Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Exp|Log|Log10|
  Pow|Round|Truncate`: use `JsMath.*` for transcendentals and `**`, `Js.Round` for `Math.round` (JS rounds half up,
  .NET half to even), `Math.Floor`/`Math.Ceiling`/`Math.Sqrt`/`Math.Abs`/`Math.Min`/`Math.Max` are allowed (exact).
  Write `x ** 2` as `x * x` (fdlibm special-cases y = 2; 0 of 2,000,000 random x differ).
- JS regex anchors: with the `m` flag, JS `^`/`$` match at `\n`, `\r`, U+2028 and U+2029; .NET `Multiline` only at `\n`.
  Hand-write such regexes as line scanners or use `(?<=\A|[\n\r  ])` / `(?=[\n\r  ]|\z)` with
  `RegexOptions.None` (review M6). The same care for every `\s`, `\S`, `\w`, `\b` and `.`: grep each ported regex for them.
- JSON: every `JSON.parse` site uses `JsonDocument` with `MaxDepth` large enough not to trip before V8 (V8 parses 20,000
  nested levels), and builds objects from `EnumerateObject` with last-wins value at first-occurrence position; never
  `JsonNode.AsObject()` enumeration (throws on duplicate keys) (review M5).
- Files are UTF-8 without BOM, LF line endings. Pele's placeholder characters (`ﬂ°`, `¶ß`) appear literally in source.
- Commits use `git commit -F <file>` with an exact `git add` list, ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `tools/capture-fixtures.mjs` stays deterministic: two consecutive runs give the same hash over
  `tests/fixtures` + `tests/goldens`. Fuzz inputs use a seeded PRNG (mulberry32, fixed seed per type), never `Math.random`.
- No environment variables as configuration.
- Out of scope:
  - The explainer type itself: page embedding `<figure class="chart">`, `sources.mjs`, validate line offsets, the
    "not UTF-8" message and the CLI. These are PORT-009.
  - Pele's DOM code: zoom, mount, window, the canvas measurer, the icon *resolver* and the `fontFamily` theme path,
    which archify never reaches (§2, §4.6). The icon *syntax* in labels is reached and is ported (Task 3, review M2).

## Review Focus

Inputs no single task's seeds obviously cover, most likely to bite first. Each one gets a seed in the named task.

1. **A comment line or front matter above a syntax error.** The reported `line` counts the pre-processed text (§2, §4.9).
   Seeds in Task 4 (front matter) and Task 5 (`%%` comment), for pie and for one Jison type.
2. **CRLF input and a leading BOM.** Pele normalises CRLF in `preprocess`. Seed CRLF for every type in its own task;
   seed a BOM in the `FromData` cases (Task 5).
3. **Han labels and surrogate pairs.** Covers the pie legend `（n%）`, the rotated CJK axis-title rewrite, and an emoji
   label: a high surrogate measures as wide, a low surrogate as 0 (§4.3). Seeds in Task 5 (pie) and Task 6 (xychart).
4. **Number-format edges.** Values ≥ 1e21, |n| ≥ 1e11 in `num()`, negative zero, and `0.1+0.2`-style sums in pie
   shares and xychart linear axes. Seeds in Task 3 (the `num` unit table), Task 5 (pie shares) and Task 6 (xychart).
5. **Empty and whitespace-only bodies.** A bare type line such as `pie`, `xychart-beta` or `venn-beta`. Seed one per
   type in its own task; each must match Node's result, whether that is an error or an empty chart.

---

### Task 1: V8 math parity (gate), `Js.ParseInt`, Pele checkout

**Files:**
- Create: `src/Archify.Net.Core/Js/JsMath.cs`, `tools/v8-math/gen.mjs`, `tools/v8-math/README.md`
- Modify: `src/Archify.Net.Core/Js/Js.cs` (add `ParseInt`)
- Test: `tests/Archify.Net.Core.Tests/JsMathTests.cs`, fixture `tests/fixtures/js/v8-math.json`

**Interfaces:**
- Produces: `internal static class JsMath { double Sin(double); double Cos(double); double Acos(double);
  double Atan2(double y, double x); double Log10(double); double Pow(double x, double y); }`. Every call is
  bit-identical to V8 in Node 24. `Pow` is JS `**` / `Math.pow` (review M1: `Math.Pow(10, 126)` is 1 ulp off V8 and
  `xychart/ticks.ts:7,9` reaches it).
- Produces: `internal static double Js.ParseInt(string s, int radix = 10)`, with JS `parseInt` semantics: JS-whitespace
  trim, sign, `0x` prefix when radix is 16 or 0, the digits prefix, and NaN when there are no digits.

- [ ] **Step 1: Clone Pele.** `git clone https://github.com/obsidianmd/pele C:\projects\pele`, then
  `git -C C:\projects\pele checkout --detach 1.3.0`. Assert `git -C C:\projects\pele rev-parse HEAD` prints
  `35a83130d8c8a2b6718429e8f577d96d01f41544`.
- [ ] **Step 2: Write the harness.** Port the research harness
  (`C:\Users\marti\AppData\Local\Temp\claude\C--Projects-Archify-Net\529c4f19-8022-4043-af39-0cfc0e269184\scratchpad\mathcheck\gen.mjs`
  and `check.cs`; rewrite them if they are gone)
  into `tools/v8-math/gen.mjs`.
  - Mode `--fixture` writes `tests/fixtures/js/v8-math.json`: about 20,000 cases per function as hex bit patterns
    `{fn, in: [hex…], out: hex}`. The cases are:
    - every chart angle `2πi/n − π/2` for n ≤ 64 and `kπ/4`;
    - a seeded random sample over [−4π, 4π], plus large arguments (1e5, 1e10, 1e300) for `rem_pio2`;
    - acos over [−1, 1] plus the endpoints;
    - atan2 over all four quadrants, plus ±0 and ±∞;
    - log10 over (0, 1e12), plus powers of ten and their `nextafter` neighbours, subnormals and `1 ± ulp`;
    - pow: `10 ** p` for every integer p in [−330, 310], `0.99 ** k` for k in 0..64, `x ** 2` samples, and a seeded
      random sample of (x, y) including negative x with integer y, ±0, ±∞ and NaN;
    - explicit fdlibm branch triggers, since a uniform sample misses them: sin/cos `|x| < 2^-27`, acos `|x| < 2^-57`
      and `1 − 2^-53`, atan2 `|y/x| > 2^60` and `< 2^-60` with every sign/zero/∞ combination, `rem_pio2` at
      `k · 2^20 · π/2` boundaries (review minor).
  - Mode `--full N` writes N seeded cases to a scratch file for a dev-time run (`dotnet test --filter JsMathFull`),
    marked `[Trait("Category","Dev")]` and skipped unless the file exists. A skipped dev test is the one allowed skip.
    Record that in README.md.
- [ ] **Step 3: Write the test.** Add `JsMathTests.Matches_V8_bit_for_bit` (a theory over the fixture; compare with
  `BitConverter.DoubleToInt64Bits`). Add `Pele_sources_use_JsMath_only`, which greps `src/Archify.Net.Core/Pele/**/*.cs`
  and `src/Archify.Net.Core/Explainer/*.cs` for
  `Math\.(Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Exp|Log|Log10|Pow|Round|Truncate)\(` (not preceded by `Js`) and expects
  no hits; it passes trivially until Task 3 adds files, which is intended. Add a `Js.ParseInt` table minted by
  Node: `"08"`, `" 42px"`, `"-0"`, `"0x1f"` with radix 16, `""`, `"abc"`, `"1e3"` and `"  +7.9"`.
- [ ] **Step 4: Run it.** Run `dotnet test tests/Archify.Net.Core.Tests --filter JsMath`. Expect FAIL: JsMath does not
  exist yet.
- [ ] **Step 5: Port the math.**
  - First try V8 `src/base/ieee754.cc` at the V8 version Node 24 ships (`node -p process.versions.v8`). That means
    `sin`, `cos`, `acos`, `atan2`, `log10` and `pow` (V8's `ieee754::pow`, the fdlibm `e_pow.c` derivative that
    `Math.pow` and `**` use), with `__kernel_sin`, `__kernel_cos`, `__ieee754_rem_pio2` and `__kernel_rem_pio2`.
    Whether Node's gyp build uses fdlibm or glibc trig is unverified (review); fdlibm first stands.
  - If sin/cos still differ, port V8's `third_party/glibc` sin/cos (the `v8_use_libm_trig_functions` build), then
    re-run. Port the C literally: hex constants via `BitConverter.Int64BitsToDouble`, word access via `GET_HIGH_WORD`
    equivalents.
- [ ] **Step 6: Run the gate.** Expect PASS, with 0 differences on the fixture and 0 on a 1,000,000-case `--full` dev
  run. Record both counts in the report.
  - **Gate:** if no variant reaches 0, stop. Report BLOCKED with the differing functions, a count per function and five
    sample inputs. The controller then takes the radar/venn deviation question to Martin.
- [ ] **Step 7: Commit.** `feat(js): V8-identical sin/cos/acos/atan2/log10 and parseInt (PORT-008)`.

### Task 2: chart fixture pipeline

**Files:**
- Modify: `tools/capture-fixtures.mjs` (new `charts` section)
- Create: `tools/chart-seeds/README.md`, `tools/chart-seeds/wrapper.json`
- Create: `tests/Archify.Net.Core.Tests/ChartParityTests.cs`
- Create: `src/Archify.Net.Core/Explainer/ExplainerCharts.cs` (stub)

**Interfaces:**
- Produces the fixture layout:
  - Inputs: `tools/chart-seeds/<group>.json`, a JSON array of `{name, text}`. For data cases it is
    `{name, file, bytes (base64), spec: {kind, x, y, unit}}`.
  - Goldens: `tests/goldens/charts/<group>/<name>.json`, holding `JSON.stringify(result, null, 2) + "\n"` of
    `compileExplainerChart(text)` or `chartFromData(...)`.
  - Fuzz: `tools/chart-seeds/<type>.fuzz.json`, `{seed, count, fragments: [...]}`. The capture script ports Pele's
    `random` and `mutator` (`tests/support/corpus.ts:34-57`: insert fragment, delete, splice from another corpus entry,
    duplicate, replace) literally, uses the group's own seeds as the corpus, and writes all results of one type as
    *one* golden array `tests/goldens/charts/<type>/fuzz.json` (`[{input, result}]`), so `count` can be 1,000
    (venn 3,000) without thousands of files (review M7). `ChartParityTests` compares it element by element, with
    the index in the failure message.
  - Thrown rows: when `compileExplainerChart` throws (the front-matter case, review B1), the capture records
    `{"thrown": {"name", "message", "code", "line"}}`; the C# side compares the propagated `PeleException` with it.
- Produces `ChartParityTests.Targets()`, a hand-listed set of groups. A group is compared only once it is listed, and
  each type task adds its group.
- Produces the stub `internal static class ExplainerCharts` with:
  - `static ChartResult Compile(string text)`
  - `static ChartData FromData(byte[] bytes, string filePath, ChartDataSpec spec)`
  - `sealed record ChartResult(string? Svg, double? Width, double? Height, string? Type, string? Error, int? Line, string? Example)`
  - `sealed record ChartDataSpec(string Kind, string X, IReadOnlyList<string> Y, string? Unit)`; `Unit` is
    truthiness-tested as upstream (`charts.mjs:69`): `""` behaves as absent (review M8c)
  - `sealed record ChartData(string? Text, string? Error, bool NotUtf8)`

  `NotUtf8` is set where `TextDecoder` with `fatal: true` would throw. The message for that case belongs to PORT-009.
  Each record has a `ToJson()` that serialises like `JSON.stringify(result, null, 2)`: upstream's key order, absent keys
  omitted, numbers via `Js.Num`.

- [ ] **Step 1: Add the capture section.** It imports `file:///C:/projects/archify/archify/explainer/charts.mjs`, reads
  every `tools/chart-seeds/*.json`, writes the goldens, and asserts that every seed name is unique within its group.
- [ ] **Step 2: Seed the wrapper group.** `wrapper.json` gets:
  - the 9 NATIVE redirects, one per type in `NATIVE`;
  - empty text, and `"hello"` (undetected);
  - one unsupported but detected type, such as `gitGraph`;
  - the archify test inputs from `test/explainer-charts.test.mjs` (§6).

  The data group `data.json` gets:
  - every `chartFromData` branch: invalid JSON, a non-array, an array containing a non-object, CSV with fewer than 2 lines, a
    missing column (with an integer-like header such as `2024` to pin `Object.keys` order), a duplicate header, the pie
    12-row cap and the 60-row cap (one over each);
  - blank undefined, blank null, blank whitespace, a non-numeric value, commas in numbers (`"1,200"`), quoted cells with
    `""`, a BOM-prefixed file;
  - a non-UTF-8 byte sequence: the capture records `{notUtf8: true}` where TextDecoder throws;
  - pie output, bar output, line output, with and without a unit, and `unit: ""` (no `y-axis` line, review M8c);
  - ragged rows and missing cells (review M4): CSV `k,v,w\nA,1\nB,2,3` with `x=w` (`"undefined"` in the axis), CSV
    `k,v\nA,1,9` with `x=zz` (a header key `"undefined"` in `Columns:`), JSON `[{"k":"A","v":1},{"v":2}]`;
  - a double BOM (`TextDecoder` strips one, the regex a second, review M8b) and a JSON `"\ud800"` lone-surrogate cell
    (pinned as a deviation if System.Text.Json cannot return it, review M5c);
  - `Js.String` of JSON values: `[1]` (value 1), `{"a":1}` (`[object Object]` in the error), `true` (non-numeric).
- [ ] **Step 3: Write the test.** `ChartParityTests.Matches_node(group, name)` compares
  `ExplainerCharts.Compile(text).ToJson()` (or `FromData(...).ToJson()`) with the golden string, byte for byte. Its
  `Targets()` lists `"data"` now. `PendingUntil` sets (Task 5) are applied by filtering those rows out of the theory
  data in `Targets()`, not by `Skip=` (xunit 2.9.3 has no dynamic skip). `"wrapper"` is listed in Task 5, because its expected outputs need pie and xychart.
- [ ] **Step 4: Port `chartFromData`.** It has no Pele dependency (§2):
  - `Object.keys` order: integer-like keys ascending first, then insertion order.
  - `Object.fromEntries`: the last duplicate wins, but its position is the first occurrence.
  - The CSV splitter, the row caps and the blank rule.
  - `Number(String(v).replace(/,/g, ''))` via `Js.ToNumber`; `String(v)` of JSON values via `Js.String`.
  - Values printed with `Js.Num`.
  - JSON parse with duplicate keys: the last wins, at the first key's position (Global Constraints, JSON).
  - A missing cell is JS `undefined`: `String()` gives `"undefined"`, `blank()` treats it as blank; a cell beyond the
    header gets the string key `"undefined"` (review M4).
  - Strip up to two leading BOMs: one as `TextDecoder` does, one by the regex (review M8b).
- [ ] **Step 5: Run the data rows.** Expect PASS. Run the capture twice and expect the same hash.
- [ ] **Step 6: Commit.** `feat(charts): fixture pipeline and chartFromData (PORT-008)`.

### Task 3: Pele core

**Files:** create under `src/Archify.Net.Core/Pele/`, one C# file per TS module. Port from the tag source:
- `PeleException.cs` (`errors.ts`): public (Global Constraints, B1); `Code`, `Line` and the `locate` and
  `syntaxError` helpers.
- `Detect.cs` (`detect.ts`): all 33 detectors, in order.
- `Preprocess.cs` (`preprocess.ts`): CRLF normalisation, the HTML-attribute rewrite, `%%{init}%%` directives through
  System.Text.Json with `merge` (skipping `__proto__`, `constructor` and `prototype`), comment removal and `trimStart`.
  Front matter is only detected here; Task 4 parses it. `directives()` reads matches from
  `text.trim().replace(/'/g, '"')` but removes them by running the same regex on the *original* text
  (`preprocess.ts:104,122`): port both passes. `RE_COMMENT` (`:16`, `gm`) needs JS line-terminator anchors.
  Config values keep non-finite numbers (`.inf`/`.nan` from YAML reach `typeof === 'number'` checks).
- `Text/Label.cs` also ports `pushIcons`, `RE_ICON` (`(fa[bklrs]?):fa-([\w-]+)`, JS `\w`), `RE_TAG` and the
  resolver-absent branches of `icons.ts:21-29`: with no resolver the icon name is removed together with the space after
  it, or the preceding text is `trimEnd`-ed (`label.ts:50-73`, review M2).
- `Text/Entities.cs`, `Text/Chars.cs` (`util/chars.ts`), `Text/Measurer.cs` (metrics table and rules, §4.3),
  `Text/Label.cs` (`layoutLabel`).
- `Svg/Builder.cs` (`num`, `esc`, `escText`, attribute builder), `Svg/Theme.cs` (`resolveStyle`, `seriesColor`,
  `fontStyle`, `ROOT_STYLE`, `RADIUS`, `CRITICAL`, `classNames`),
  `Svg/Root.cs` (`svgDocument`, the `rounded()` rx rewrite).
- `Diagrams/Common/Tokens.cs` (tokenizer and Reader; port the `first`-table `starting`/`every` dispatch at
  `tokens.ts:48-63,93` literally; `accDescrValue` at `:192-193` needs JS line-terminator anchors), `FitWidth.cs`
  (natural size only, §2), `Caseless.cs`.
- `Pele.cs` (`core.ts`): `Register` is replaced by a fixed table of the six types; `DetectType`; `Render(text, options)`
  with the limits (50,000-character source, 4,000,000-character output, nesting depth) and the shallow config spread
  `{ ...extra, ...pre.config }`. `parse` receives `encodeEntities(source) + "\n"` (`core.ts:82`). `responsive` comes
  from `config[diagram.Section].useMaxWidth !== false` (`core.ts:123-125`). The 50,000 limit applies to the text
  `charts.mjs` passes, i.e. after `pieLegend` rewrote it. The nesting cap on `merge` is a deviation from V8 (which
  handled 20,000 levels); record it for Task 11.

**Interfaces:**
- Produces `internal static class Pele` with:
  - `string? DetectType(string text)`
  - `PeleRenderResult Render(string text, PeleOptions options)`, which throws `PeleException`
  - `PeleRenderResult(string Svg, double Width, double Height, string Type)`
  - `PeleOptions`, holding archify's option object from charts.mjs:28 as typed fields plus a `JsonObject Config`
- Produces `internal interface IPeleDiagram { string Id; string? Section; PeleRenderResult Render(PreprocessedInput, RenderContext) }`
  (`Section` is the config key, e.g. `xyChart`, read for `useMaxWidth`),
  one per type. This interface stands in for `register(...)`: six implementations, chosen by `Detect`.

- [ ] **Step 1: Write the core tests.** Mint unit tables through the capture script:
  - `num()` over about 200 values, including ≥1e11, −0, .xx5 boundaries and NaN;
  - `esc` and `escText` over control characters and entities;
  - the measurer over ASCII, Han, emoji (surrogates), combining marks and U+FEFF;
  - `detectType` over every detector, plus near-misses;
  - `preprocess` over comments, directives (valid, invalid and duplicate-key JSON, single-quoted `%%{init: {'a':1}}%%`,
    100-level nesting), CRLF, a `%%` comment after a U+2028 line, `\r`-only line ends, and the HTML-attribute rewrite,
    recording `{text, config, title}`;
  - `layoutLabel` over wrapping, `<br/>`, and `fa:fa-x` at the start, middle and end of a label;
  - `encodeEntities` and `decodeEntities`.

  Each table is a JSON fixture under `tests/fixtures/js/pele-*.json`, minted from `C:\projects\pele` source via the
  vendored build. Call the module through the vendored `core.js` exports where they are exported. Where they are not,
  build a test entry with the pinned esbuild into the scratchpad and import that, and name the build command in the
  capture script.
- [ ] **Step 2: Run the tests.** Expect FAIL.
- [ ] **Step 3: Port the modules listed above.**
- [ ] **Step 4: Run the tests again.** Expect PASS.
- [ ] **Step 5: Commit.** `feat(pele): core — detect, preprocess, text, svg, tokens (PORT-008)`.

### Task 4: front-matter YAML (Martin, 2026-10-22: port it)

**Files:** create `src/Archify.Net.Core/Pele/Util/Yaml.cs` (`util/yaml.ts`, 467 LOC); modify `Preprocess.cs` to parse
front matter (`title` and `config`, merged as upstream merges them).

- [ ] **Step 1: Write the tests.** Add a YAML table fixture (`pele-yaml.json`) covering:
  - every error message at `util/yaml.ts:42,167,190,191,196,205,239,251,349,383,425,428,464`, wrapped as
    `Front matter is not valid YAML. YAML: …`;
  - `MAX_DEPTH`;
  - scalars, flow and block collections, quoted strings, and duplicate keys;
  - escapes (review M3): `util/yaml.ts:162` `String.fromCodePoint(parseInt(hex, 16))` has no range check, so
    `"\uZZZZ"` gives `Front matter is not valid YAML. Invalid code point NaN` and `"\U00110000"` gives
    `… Invalid code point 1114112`. Port the check (NaN, < 0, > 0x10FFFF, non-integer → that message with
    `Js.Num(n)`; lone surrogates are accepted). Seed both, a short tail `"\u41"` (→ `A`), and `\x`/`\u`/`\U` at
    lengths 2/4/8;
  - `title` (`preprocess.ts:156`): JS truthiness, then `String()` — `title: [1, 2]` → `1,2`, `{}` → `[object Object]`;
    `0`, `""`, `false`, `null` are skipped.

  Add `Compile`-level rows to `wrapper.json`: a front-matter YAML error, which *throws* through
  `compileExplainerChart` (a thrown row, Task 2; review B1), and `config: xyChart: useMaxWidth: false`, which drops
  `max-width:100%;height:auto;` (review M8a; its golden lands with the wrapper group in Task 5/6).

  Add `preprocess` rows with front matter, plus one front matter above a later syntax error, so that the reported line
  shift is pinned (Review Focus 1). The chart-level effect is pinned in Tasks 8 and 9, through Pele's radar and sankey
  corpus entries that carry `config:`.
- [ ] **Step 2: Run the tests.** Expect FAIL.
- [ ] **Step 3: Port `util/yaml.ts`.**
- [ ] **Step 4: Run the tests.** Expect PASS.
- [ ] **Step 5: Commit.** `feat(pele): front-matter YAML (PORT-008)`.

### Tasks 5–10: one chart type each

Every type task follows the same steps; each task's own block below lists only what differs.

- [ ] **Step 1: Seed.** Create `tools/chart-seeds/<type>.json` and `<type>.fuzz.json` with:
  1. archify's test inputs for the type (§6);
  2. Pele `tests/corpus/<type>-docs.json`, verbatim;
  3. the input strings from Pele `tests/compat/<type>/*.spec.ts` and `tests/render/<type>*.test.ts`, copied by hand as
     literals with the spec file and test name in `name`;
  4. the Review Focus seeds this task owns (CRLF, the empty body, and the type's own edge cases);
  5. fuzz: the type's `FRAGMENTS` from `tests/<type>/oracle.test.ts`, seed `0x5EED0000 + typeIndex`, 1,000 cases
     through the ported `mutator` over this group's seeds (Task 2 fuzz layout, review M7).

  Pele's tests are MIT, so add a note to `tools/chart-seeds/README.md`. Mint the goldens, and add `"<type>"` to
  `ChartParityTests.Targets()`. In the capture script, assert that the group contains at least one success and at least
  one `error` result, so the group cannot pass vacuously.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Port.** Port `C:\projects\pele\src\diagrams\<type>\*.ts` into `src/Archify.Net.Core/Pele/Diagrams/<Type>/`,
  one file per TS file, and register the type in `Pele.cs`.
- [ ] **Step 4: Run.** Expect PASS for the group and for every earlier group, then run the full Core suite. Run the capture
  twice and expect an identical hash.
- [ ] **Step 5: Commit.** `feat(pele): <type> (PORT-008)`.

**Task 5: pie and the `charts.mjs` wrapper.** Port `compileExplainerChart` into `ExplainerCharts.Compile`, covering:
- the NATIVE table and its messages;
- the supported-types message;
- first-line error plus `line` plus `example`;
- `pieLegend`: its regexes with JS `\s`, the `u` flag and JS multiline anchors (it runs on the raw text *before*
  `preprocess` normalises `\r`, review M6); `Math.round` via `Js.Round`; `Number` via `Js.ToNumber`; Han via
  `Js.IsHan`;
- the CJK rotated-title rewrite.

Add `"wrapper"` to `Targets()`. Its xychart rows are expected to fail until Task 6, so list those row names in a
`PendingUntil("xychart")` set. Task 6 removes that set. Pie seeds add:
- a `%%` comment line above a syntax error (Review Focus 1);
- a Han label with `showData`, and an emoji label;
- shares that round to 101 % in total;
- a negative value (semantic error);
- duplicate labels (first wins, §4.5);
- `\r`-only line ends (`pie\r"a" : 1\r"b" : 1`) and a U+2028 line separator (review M6);
- `fa:fa-car` at the start, middle and end of a label (`Cars · 1 (100%)`, review M2);
- 6,000 rows, which hit the 50,000-character limit after the `pieLegend` rewrite.

Pie's `tests/corpus/pie-docs.json` entry 2 carries `config: pie:` front matter, so Task 5 needs Task 4 (the order holds).

**Task 6: xychart.** Uses `JsMath.Log10` in `ticks.ts`; port `tickStep`'s negative-inverse branch literally (§4.2).
Seeds add:
- the rotated CJK y-axis title;
- values ≥ 1e21;
- a linear x-axis whose step yields `0.30000000000000004`-style labels;
- `horizontal`;
- `No Plot to render`;
- values whose span puts `10 ** power` at p = 126 and other ±300 extremes (`JsMath.Pow`, review M1).

Remove the wrapper's `PendingUntil("xychart")` set.

**Task 7: quadrantChart.** The lexer has 9 states including `md_string`. Points are drawn in reverse order; label
placement is the greedy first-free search (§4.9). Port quadrant's `parseInt` uses through `Js.ParseInt`. Seeds add
every style-validation error at `quadrant/db.ts:42,74`.

**Task 8: sankey.** Port the CSV lexer and the layout: topological depth, the cycle error, the bisection, 6 settle
passes and `Math.pow(0.99, pass)` via `JsMath.Pow` (covered by the Task 1 fixture). The sort is insertion sort up to 8 items, `Js.Sort` above (§4.4). Seeds add:
- a cycle;
- quoted names with `""`;
- the Pele corpus entry with `config:` front matter.

**Task 9: radar.** Uses the Langium-style tokens on the shared Reader, with JS `\w` hand-written. Lexer state is
per-call locals, not module state (§4.9). Uses `JsMath.Sin/Cos`. Seeds add:
- both model errors;
- `graticule polygon`;
- `{axis: v}` entries;
- the corpus entry with `config:`.

**Task 10: venn.** Port the layout in full: lens bisection with `JsMath.Acos`, ring relaxation, the `findSpot` hill
climb, and the `regionPath` atan2 sort, which must be stable via `Js.Sort`. The default `.sort()` maps to
`Js.SortDefault`. `Math.min(r1, r2) ** 2` and `ids.length ** 2` are written `x * x`. Fuzz count is 3,000 for this
type, the most numerically sensitive. Seeds add the three semantic errors,
plus 4- and 5-set inputs.

### Task 11: notices and docs

**Files:** `THIRD_PARTY_NOTICES.md` (or the port's equivalent; create it if absent): Pele's MIT notice, © 2026 kepano.
Also `docs/deviations.md`, `docs/status.md`, `README.md`, `SESSION_HANDOFF.md`.

- [ ] **Step 1: Add the notices.**
  - The Pele notice.
  - Deviations: Pele's canvas measurer, zoom and mount are not ported (archify never reaches them). There is no icon
    resolver; icon names are stripped from labels as upstream does without one. A lone-surrogate `\ud800` JSON escape,
    if System.Text.Json cannot return it (pinned by the Task 2 row). The `merge` nesting cap (V8 handled 20,000 levels).
    `ExplainerCharts` exists but no CLI path reaches it until PORT-009; note for PORT-009 that upstream's
    `compileExplainerChart` throws on a front-matter YAML error, so the CLI must match upstream's top-level handler.
  - Status: a PORT-008 section with the parity counts per group.
  - The re-pin note: Pele upstream is at 1.8.0. A future archify re-pin of Pele is its own card.
- [ ] **Step 2: Run the full suite.** Run `dotnet test` at the repo root and expect 0 failed and 0 warnings. The only
  allowed skip is the `Dev` math test. Run the capture once more and expect `git status --porcelain tests/` to be empty.
- [ ] **Step 3: Commit.** `docs: PORT-008 Pele charts parity (PORT-008)`. Then get the whole-branch Fable review
  (Martin, 2026-10-23), settle it, fast-forward master and push.

---

## Self-review notes (Claude, 2026-10-23)

- **Coverage.** The card's bar is "SVG byte-identical per type, errors, blank-data rule", met by Tasks 5–10 and Task 2.
  "Prefer published source" is met by the Global Constraints and Task 1 Step 1. YAML is Task 4, per Martin's decision.
- **The gate.** Task 1 can stop the card: if V8 math cannot be matched, radar and venn are not byte-identical. That is
  Martin's decision, not a ruling.
- **Ordering.** Tasks 5–10 depend on Tasks 1–4: pie, radar and sankey corpus entries use front matter. The wrapper group waits for xychart, through the `PendingUntil` set rather than a red row.
- **Size.** About 6–7k C# lines, 11 tasks; implementers are opus. Fixture volume is roughly 1,200 golden JSON files
  (a few MB). The determinism check covers them.
- **Plan review (Fable, 2026-10-23): ready after fixes, 1 Blocker, 8 Major, 15 Minor**
  (`.superpowers/sdd/2026-10-23-port008-pele-charts/plan-review-fable.md`). All accepted and folded into the tasks above
  (B1 thrown front-matter errors; M1 `JsMath.Pow` and the wider `System.Math` ban; M2 icon syntax; M3 YAML code points;
  M4 ragged data rows; M5 JSON parse options; M6 JS line-terminator anchors; M7 ported mutator, 1,000 cases, one fuzz
  file per type; M8 `useMaxWidth`, double BOM, `unit: ""`; minors in place). `ExplainerCharts` stays in Core.
