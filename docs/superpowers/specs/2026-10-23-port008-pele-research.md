# PORT-008 research: porting Pele 1.3.0 chart rendering to C#

Read-only research, 2026-10-22. Upstream: archify v3.2.0 at `C:\projects\archify`. Pele source at tag 1.3.0,
downloaded to the scratchpad (`…\scratchpad\pele-src\pele-1.3.0`, not committed anywhere).

**Headline.** The readable TypeScript source is public at tag `1.3.0`. Building it with the pinned esbuild
reproduces the vendored files **byte for byte** (123 of 123 outputs match). About **5,900 source lines** need porting
(1,670 shared + 4,670 across the six types, minus dead code), or about 6,350 if front-matter YAML is in scope. The
top risk is that **.NET `Math.Sin/Cos/Acos/Atan2/Log10` differ from V8's in the last bit** for 1–18 % of inputs.
That difference was measured, not assumed (§4.1).

---

## 1. Source availability

| Question | Answer | Evidence |
|---|---|---|
| npm name | `pele` (versions 1.0.0 … 1.8.0, latest 1.8.0) | `https://registry.npmjs.org/pele` |
| GitHub tag 1.3.0 | Yes. Commit `35a83130d8c8a2b6718429e8f577d96d01f41544`, release published 2026-10-09T22:55:37Z | `api.github.com/repos/obsidianmd/pele/tags`, `/releases` |
| Readable source | Yes. TypeScript in `src/` (no generated parsers; every lexer and parser is hand-written TS) | `codeload.github.com/obsidianmd/pele/tar.gz/refs/tags/1.3.0` |
| npm tarball sha256 | `8c55d4f2335367c31e77550dfbbd00c99533c432b9323aa37730188c0a355da5`, **matches** the vendor README | `sha256sum pele-1.3.0.tgz`; `archify/explainer/vendor/pele/README.md:3` |
| npm integrity | `sha512-Jz3SW2OY…W0vuA==`, shasum `dab001c1…9e61`, 2 registry signatures | registry `versions["1.3.0"].dist` |
| Provenance | SLSA v1 attestation. Built by `.github/workflows/release.yml` at `refs/tags/1.3.0`, resolved dependency `git+https://github.com/obsidianmd/pele@refs/tags/1.3.0` with gitCommit `35a83130…` (the tag commit) | `registry.npmjs.org/-/npm/v1/attestations/pele@1.3.0` |
| Vendored = tarball | All 21 vendored `.js` files and `LICENSE` are byte-identical to `package/dist/esm/…` in the tarball | `cmp` of every file |
| Source = vendored build | **Verified.** `scripts/build.mjs` options (esbuild 0.28.2 from `package-lock.json`, `bundle, esm, es2022, minify, charset utf8, legalComments none, splitting, chunkNames chunks/[hash]`), rebuilt from the tag source, gives **123/123 outputs identical** to the npm `dist/esm`, chunk hashes included | scratchpad `esb\rebuild.mjs` (absWorkingDir = source root) |
| Provenance notes in archify | `vendor/pele/README.md` (version, tarball name, sha256, "unmodified" subset list), `vendor/pele/LICENSE` (MIT, © 2026 kepano), `THIRD_PARTY_NOTICES.md:65-70` | files cited |

What I could not verify: nothing material. The npm tarball itself ships no source (only `.d.ts` and the minified
`dist`), so the source-to-build link rests on my rebuild plus the SLSA attestation, and both agree.

Note on churn: Pele had 15 releases between 2026-10-06 and 2026-10-20 (1.3.0 → 1.8.0 in 11 days). Every upstream
archify re-pin of Pele will mean a diff-port. Pin to 1.3.0 and plan re-pins as their own cards.

Licence: the C# port is a derivative of MIT code, so it must carry Pele's copyright and permission notice, for
example in a NOTICE entry next to archify's.

## 2. Surface used by archify

Only `archify/explainer/charts.mjs` imports the vendor directory (`grep` over the repo). `bin/archify.mjs:6168` merely
checks that `vendor/pele/core.js` exists (the `doctor` command).

**Pele exports called** (`charts.mjs:1-9`): `register(pie, xychart, quadrantChart, sankey, radar, venn)`, then
`detectType(text)`, `render(text, options)`, and `PeleError` (used with `instanceof`, `.code`, `.message` and `.line`).
`parse`, `mount`, `enableZoom`, `forgetTextWidths`, `supports` and `registered` are unused.

**Options** (`charts.mjs:28`):
`{ padding: 8, fontSize: 13, links: false, images: false, config: { xyChart: { width: 480, height: 260, showDataLabel: true }, quadrantChart: { chartWidth: 400, chartHeight: 400 } } }`.
No `measurer`, `fontFamily`, `maxWidth`, `responsive`, `idPrefix` or `icons` is passed. Consequences:
- The measurer is `defaultMeasurer()`, which in Node has no canvas, so it falls back to **`metricsMeasurer`, the
  Arial table** (`src/text/measurer.ts:68-80,113-121`). Text width is deterministic.
- With no `maxWidth`, `fitWidth`, `capWidth`, `tighten` and `turnToFit` return the natural size on the first draw
  (`src/diagrams/common/fit-width.ts:15,36`). The redraw loops are dead code for archify.
- `responsive` defaults to true, which writes `max-width:100%;height:auto;` (`src/svg/root.ts:55`).
- `config` is merged as `{ ...extra, ...pre.config }` (`src/core.ts:80`), a **shallow** spread. A chart's front
  matter `config: { xyChart: … }` replaces archify's whole `xyChart` object rather than merging into it.

**Pre-processing** (`charts.mjs:15-21`, `pieLegend`): for `pie` text, it drops `showData` and rewrites each
`"label" : value` to `"label · value (share%)"`, or `（share%）` for a label containing Han characters. The share is
`Math.round(Number(value)/total*100)`. Values keep their source spelling (`${value}` is the regex capture).

**Error handling** (`charts.mjs:25-36`):
- A detected type in `NATIVE` (flowchart, sequence, class, er, state, timeline, gantt, mindmap, treeview) gives a
  fixed redirect message plus an `example`.
- `PeleError` with `code === 'unsupported-diagram'` gives the fixed "A chart fence supports …" message.
- Any other `PeleError` gives **`error.message.split('\n')[0]` and `line: error.line`**. Only the first line of the
  message survives: `Parse error on line N:` / `Lexical error on line N. Unrecognized text.` / the semantic message.
- Non-`PeleError` exceptions are rethrown.
- `validate.mjs:119` adds `chart.line - 1` to the diagnostic's source line. `error.line` counts lines in Pele's
  **pre-processed** text: front matter cut, `%%` comment lines removed, `trimStart()` applied (`src/preprocess.ts:22-24,164-168`).
  A comment line above an error therefore shifts the reported line, and the port has to reproduce that shift.

**SVG post-processing** (`charts.mjs:30`): one regex turns a rotated CJK axis title
`<g transform="translate(x,y) rotate(-90)"><text x="0" y="…" text-anchor="middle">…Han…</text></g>` into
`<text … writing-mode="vertical-rl">`. It returns `{ svg, width, height, type }`.

**Data charts, and the 3.2.0 blank-data rule** (`charts.mjs:41-70`, `chartFromData`):
- Input is UTF-8 decoded with `fatal: true` and a BOM strip. The caller turns a throw into "The data file is not
  UTF-8 text." (`sources.mjs:311`).
- JSON is read as an array of objects. CSV uses a hand-rolled quoted-cell splitter, and rows come from
  `Object.fromEntries(header→cell)`.
- Errors: missing column (`Columns: …` listed in `new Set(rows.flatMap(Object.keys))` order), a row cap of 12 for pie
  and 60 otherwise, then for each y column the first non-finite row:
  `Column "c" is empty in data row N. A blank is missing data, not zero: …` (blank = undefined/null/whitespace,
  checked **before** `Number('')` can turn a blank into 0) or `Column "c" has a non-numeric value in data row N: v.`
  Numbers are `Number(String(v).replace(/,/g,''))`.
- Output: chart text `pie\n  "x" : v…` or `xychart-beta\n  x-axis [...]\n  y-axis "unit"\n  bar|line "col" [v, …]`,
  with values printed via JS `String(number)`.
- JS subtlety: `Object.keys` puts integer-like keys ("2024", "7") first in ascending order. A CSV header such as
  `2024` therefore moves to the front of the "Columns:" list. `fromEntries` keeps the last of two duplicate headers.

**Embedding** (`render.mjs:77`): `<figure class="chart" style="--native-width:${Math.round(width)}px">` + the SVG with
`width` and `height` stripped from the root tag + an optional `<figcaption>`. This belongs to the explainer (PORT-009),
not PORT-008.

## 3. Size and shape per chart type

Chunk-to-source mapping from the esbuild metafile of the byte-identical rebuild. Bytes are minified output; LOC are
`wc -l` of the TS source (comments included).

**Shared (core.js closure plus common chunks)**

| Chunk | Bytes | Source modules (bytes in output) | LOC | Needed? |
|---|---:|---|---:|---|
| `XTDUIGUI.js` | 5,037 | errors (668), text/entities (2,441), text/icons (187), text/metrics (394), text/measurer (1,282) | 52+72+29+6+126 | yes. Canvas measurer and icons are inert, about 30 LOC |
| `YAYTP6L5.js` | 15,746 | detect (1,200), preprocess (2,976), core (1,776), zoom (6,781), mount (2,635), window (177) | 77+220+144 (+424+302+11) | detect/preprocess/core only. zoom/mount/window are DOM, skip |
| `EJ5KOXYD.js` | 7,664 | util/yaml | 467 | only for `---` front matter in a chart fence (§5) |
| `GUJTEFZK.js` | 511 | util/chars | 44 | yes |
| `IZOKQ2GT.js` | 8,651 | svg/builder (2,000), svg/theme (2,281), svg/root (1,159), text/label (3,048) | 123+137+67+233 | yes, the SVG writer and label layout |
| `3DKXF73K.js` | 3,429 | diagrams/common/tokens (Chevrotain-style tokenizer + Reader) | 263 | pie, radar |
| `DKTLEL5E.js` | 838 | diagrams/common/fit-width | 66 | all but venn (degenerate, §2) |
| `RVARI57O.js` | 159 | diagrams/common/caseless | 10 | xychart, quadrant |

Shared total: about **1,670 LOC** without YAML (2,140 with it), roughly 24.6 KB minified without YAML.

**Per type** (entry file plus its own chunk; every entry also imports the shared chunks listed)

| Type | Entry → own chunk | Own bytes | Shared chunks used | Source files: LOC |
|---|---|---:|---|---|
| pie | `pie.js` → `MSKIJHB2.js` | 5,132 | DKTLEL5E, 3DKXF73K, IZOKQ2GT, XTDUIGUI | index 10, model 33, parser 135, render 172 = **350** |
| xychart | `xychart.js` → `L4LY553T.js` | 14,810 | RVARI57O, DKTLEL5E, GUJTEFZK, IZOKQ2GT, XTDUIGUI | db 133, index 16, lexer 292, parser 113, render 417, ticks 29, tokens 73 = **1,073** |
| quadrantChart | `quadrantChart.js` → `42UTT654.js` | 11,384 | RVARI57O, DKTLEL5E, GUJTEFZK, IZOKQ2GT, XTDUIGUI | db 148, index 15, lexer 366, parser 141, render 199, tokens 89 = **958** |
| sankey | `sankey.js` → `UPTXNAXN.js` | 9,468 | DKTLEL5E, GUJTEFZK, IZOKQ2GT, XTDUIGUI | db 44, index 15, layout 302, lexer 117, parser 77, render 169 = **724** |
| radar | `radar.js` → `UKSIWDDN.js` | 7,382 | DKTLEL5E, 3DKXF73K, IZOKQ2GT, XTDUIGUI | index 10, model 78, parser 282, render 181 = **551** |
| venn | `venn.js` → `EEIX5ZYH.js` | 14,177 | GUJTEFZK, IZOKQ2GT, XTDUIGUI | db 65, index 15, layout 346, lexer 244, parser 112, render 202, types 27 = **1,011** |

Types total **4,667 LOC**. Grand total ≈ **5,900 LOC** of TS, plus `charts.mjs` (70 lines). Expect the C# to come out
similar in size: the TS is dense and comment-light, and most of it is char-code lexing that maps 1:1.

No d3 or other third-party library is bundled. The sankey layout (`sankey/layout.ts`) and the venn layout
(`venn/layout.ts`) are Pele's own code.

## 4. Hard parts for byte-identical C#

### 4.1 Transcendental math: the top risk (measured)

I ran a harness on Node 24.12.0 / V8 against .NET 10.0.402 `System.Math` (scratchpad `mathcheck\gen.mjs` +
`check.cs`, 1,008,467 cases), comparing results bit for bit:

| fn | random inputs differing | exact chart angles differing |
|---|---|---|
| `Math.sin` | 4,639 / 204,168 (2.3 %) | 122 / 4,168 (pie/radar/venn angles `2πi/n − π/2`, `kπ/4`) |
| `Math.cos` | 4,720 / 204,168 (2.3 %) | 80 / 4,168 |
| `Math.acos` | 1,796 / 200,000 (0.9 %) | n/a |
| `Math.atan2` | 35,345 / 200,000 (**17.7 %**) | n/a |
| `Math.log10` | 4,980 / 200,100 (2.5 %) | 0 / 100 at 1·2·5×10^p |
| `Math.pow` | 0 / 31 (`10**p` for p=−12…12, `0.99**0…5`) | — |

Example: V8 gives `sin(0.7853981633974483) = 0.7071067811865475`, .NET gives `…476`. All differences are 1 ulp. V8
ships its own libm (`src/base/ieee754.cc`, an fdlibm port; newer builds may switch sin/cos to a glibc-derived copy under
`v8_use_libm_trig_functions`). Which of the two Node 24 uses is **unverified**: `process.config` does not expose the
flag. Either way V8's output is platform-independent, so it can be reproduced.

Where it bites:
- pie `point()`/labels (`pie/render.ts:22,81,117,120`) and radar unit vectors (`radar/render.ts:74`) feed `num()`
  (2-decimal rounding). A 1-ulp error flips output only at an exact `.xx5` boundary, which is rare, but `narrow`
  (`pie/render.ts:81`) compares `chord < width + 6`.
- **venn** is the worst case. `lensArea` uses `acos` (`venn/layout.ts:55-56`) inside a 48-step bisection
  (`:68-73`). `findSpot` hill-climbs with `cos/sin` and ties at `r*1e-9` (`:122-145`). The ring placement uses
  `cos/sin` (`:223-224`), followed by up to 300 relaxation rounds (`:237-268`). `regionPath` sorts corners by `atan2`
  and picks arcs by `atan2/cos/sin` (`:317-339`). A 1-ulp flip in a bisection or a comparison can move a label or
  change an arc.
- xychart `tickStep` uses `Math.floor(Math.log10(raw))` (`xychart/ticks.ts:6`). A 1-ulp-low result just below a
  power of ten changes the tick step and therefore every tick label.

Mitigation: port V8's `ieee754` sin, cos, acos, atan2 and log10 (with `__kernel_sin/cos`, `__ieee754_rem_pio2`), about
600–800 LOC of well-known fdlibm C. Then run this same 1 M-case harness until it reports 0 differences. If fdlibm
still differs, try the glibc variant. Do this **first**: it decides whether radar and venn can be byte-identical at all.

### 4.2 Number formatting
- `num()` (`svg/builder.ts:30-44`): `Math.round(v*100)` (JS half-up toward +∞) plus integer→string with a 2-digit
  table, falling back to `String(n/100)` at |n| ≥ 1e11. Port with `Js.Round` + `Js.Num`.
- `String(number)` / template `${number}`: xychart tick and value labels `String(value)` (`xychart/render.ts:139,189,192,203,292`),
  linear x positions `` `${x.min + i*step}` `` (`xychart/db.ts:102,104`), pie `` `${label} [${value}]` `` (`pie/render.ts:64`, reached only when
  pieLegend left `showData` in), sankey `${rounded(value)}` (`sankey/render.ts:102,142`, `rounded = Math.round(v*100)/100`),
  `textOption` (`:26`), pie `r="${radius}"` (`:125`). Port with `Js.Num`.
- `toFixed(0)` for pie percentages (`pie/render.ts:77,82,114`): use `Js.ToFixed`.
- xychart `tickStep` returns the negative inverse for steps below 1 so that tick values come from division
  (`ticks.ts:9-18`). Port it literally, with no "simplification" to multiplication.
- Root `width`/`height` are interpolated raw (`root.ts:62`), but every type rounds them first (`Math.ceil` or
  `Math.round`), so they are always integers.
- Number parsing: `Number()` (pie, xychart, quadrant, radar values), `parseFloat` (sankey `parser.ts:71`, venn
  `parser.ts:51`, theme px `theme.ts:112`), `parseInt` (quadrant radius / stroke-width `db.ts:63`, `render.ts:142`).
  `Js.ToNumber` and `Js.ParseFloat` exist. A `Js.ParseInt` (prefix digits, NaN when there are none) is missing.

### 4.3 Text width
A table, not a canvas. Two packed base-36 strings of 95 printable-ASCII Arial advances in 1/1000 em
(`text/metrics.ts:3-6`). Rules (`measurer.ts:46-61`): per **UTF-16 code unit** `c`:
- `<32` → 0;
- `<127` → table value;
- zero-width ranges 0x300–36f, 0x200b–f, 0xdc00–dfff (low surrogates), 0xfe00–f, 0xfeff → 0;
- "wide" ranges (includes high surrogates 0xd800–dbff) → 1000;
- everything else → 580.

Result is `units*size/1000`. C# `char` iteration matches this exactly. About 60 LOC. The existing
`Utils.TextUnits` / `TextFit` heuristics are archify's own and **cannot be reused**.

### 4.4 Sorting
- sankey `sort()` (`sankey/layout.ts:43-57`): insertion sort for ≤8 items, `Array.prototype.sort` above that. The
  comparators break ties by index (`|| a - b`, `:226-228`), so any correct sort gives the same order. `Js.Sort` is fine.
- venn `normalizeIds` / `ensurePairwiseSubsets`: default `.sort()` compares UTF-16 code units (`venn/db.ts:11`,
  `layout.ts:28,34`). Use `Js.SortDefault`.
- venn `corners.sort` by `atan2` difference (`layout.ts:317`) **can tie**, so it needs a stable sort. `Js.Sort` is a
  stable `OrderBy`, so it qualifies. It also depends on §4.1.

### 4.5 Map/Set iteration order
Insertion order is load-bearing in pie `sections` (`pie/model.ts:14-23`, first label wins), quadrant `classes`,
sankey node `lookup` (node index = first appearance), and venn `sizes` (circle order = order the ids are first seen,
`layout.ts:156-171`), `overlaps`, `wanted`, `stated`, `regions` and `styles` (`venn/render.ts:42-93`). Use `JsMap`
(already in `Js/JsMap.cs`, insertion-ordered with JS set/delete semantics). JSON-string keys
(`JSON.stringify(ids)`, `venn/render.ts:27`, `layout.ts:28`) are internal only, so any injective key works.

### 4.6 Regex semantics
Most lexers are hand-written char-code scanners, which port cleanly. The regexes that remain:
- `\s` in JS includes U+FEFF and excludes U+0085. .NET `\s` is the reverse. Affected: `preprocess.ts:16`
  (`RE_COMMENT`), `:17` (`RE_DIRECTIVE`), `:31`; the detectors `detect.ts:37-72` (`^\s*pie` etc.);
  `label.ts:23,162` (`<br\s*\/?>`, `/\s*\S+\s*|\s+/g` word wrap); `tokens.ts:126,144,151`.
- `\w` is ASCII-only in JS (non-`u` regex) and Unicode in .NET. Affected: `tokens.ts` `ID` in radar (`radar/parser.ts:96`
  `/[\w]([-\w]*\w)?/y`), `preprocess.ts:18` (`RE_HTML_TAG`), `entities.ts:37`, `label.ts:111`.
- `.` excludes `\r \u2028 \u2029` in JS but only `\n` in .NET (`RE_DIRECTIVE` `(?!}%{2}).`, `tokens.ts:126`
  `[^\n\r]`).
- The `/y` sticky flag maps to `\G` with `Match(input, at)`.
- `RegexOptions.ECMAScript` does **not** fix these: it narrows `\s` too far (drops U+00A0, U+2028). Hand-write the
  classes, or reuse `util/chars.ts` equivalents (`Js.IsSpace` already matches JS `\s`, `Js.cs:291`).
- `\p{L}\p{N}` with the `u` flag (`theme.ts:18`) is reached only when `fontFamily` is passed, which archify never
  does, so skip it. `\p{Script=Han}` in `charts.mjs:20,30` maps to the existing `Js.IsHan` (`JsText.cs:133`).

### 4.7 Unicode / strings
- `String.prototype.trim`, `trimStart` and `trimEnd` (JS whitespace set): use `Js.Trim`, `JsText.TrimStart`/`TrimEnd`.
- `String.fromCodePoint` / entity decoding (`entities.ts`): straightforward.
- Mermaid placeholder chars `ﬂ°`, `¶ß` (`preprocess.ts:218`, `entities.ts:38-39`): literal BMP characters, so the
  `.cs` file must be saved as UTF-8.
- `esc()` drops XML-illegal control characters (`builder.ts:8-15`).

### 4.8 Absent, as checked
No `Math.random` and no `Date` (gantt only). No `Intl`, `toLocaleString` or `localeCompare`. No `toPrecision`.
Nothing seeded. (`grep` over every chart-path file, §scratchpad.) `Math.sqrt`, `floor`, `ceil`, `min`, `max` and
`abs` are exact in both runtimes. `Math.pow` matched in all 31 cases used (§4.1). Two-argument `Math.hypot` (venn,
7 sites) has an existing V8-faithful `Js.Hypot` (`Js.cs:136-145`).

### 4.9 Other traps
- **Error line numbers** come from the comment-stripped, trimmed text (§2). The port must run the same
  `preprocess()` and compute `locate()` on that string (`errors.ts:25-37`).
- **JSON in `%%{init: …}%%` directives** (`preprocess.ts:102-123`) uses `JSON.parse`. Invalid JSON is silently
  ignored. Use System.Text.Json (BCL, no NuGet), but check three things: duplicate keys (JS: last wins), depth (V8 has
  no 64 limit), and `__proto__` / `constructor` / `prototype` being skipped by `merge` (`:20,92`).
- **RangeError → "Diagram is nested too deeply."** (`core.ts:43-50`): C# cannot catch a stack overflow. The chart
  path's recursion is only `merge()` of config and the YAML parser (which has its own `MAX_DEPTH`), so cap depth
  explicitly.
- **Module-level lexer state** in radar (`radar/parser.ts:49-50,81`, reset at `:167-168`) and the pie scanner closure
  (`pie/parser.ts:42-65`) are memoizations that look unsafe but behave correctly. Port them as per-call locals.
- **Quadrant** draws points in **reverse** order (`quadrant/render.ts:189`). Its label placement is a greedy
  first-free search over collision boxes (`:155-181`), which is deterministic.

## 5. Input syntax

Every type takes Mermaid-compatible text, preceded by Pele's shared `preprocess()` (CRLF normalisation, HTML-attribute
quote rewrite, `---` YAML front matter → `title`/`config`, `%%{init}%%` directives → config, `%%` comment lines
removed) and `encodeEntities` (`#name;` → placeholders). The lexers mimic Mermaid's generated Jison or Langium lexers
token for token, and Pele's tests check them against Mermaid (§6).

| Type | Grammar style | Statements | Lexer + parser LOC |
|---|---|---|---|
| pie | Langium (shared `tokenize` + `Reader`) | `pie [showData]`, `title`, `accTitle:`, `accDescr: / { }`, `"label" : number` (number pattern `-?\d+(\.\d+)?` with Chevrotain quirks, `pie/parser.ts:40-71`) | 135 |
| xychart | Jison emulated by a state-machine lexer (9 states) | `xychart[-beta] [vertical\|horizontal]`, `title`, `x-axis [title] [cats…] \| min --> max`, `y-axis [title] [min --> max]`, `line\|bar [title] [n, …]`, acc*, `;` separators. It reproduces a Mermaid quoting bug that makes markdown strings unreadable (`lexer.ts:26-33`) | 292 + 113 |
| quadrantChart | Jison emulated (states incl. `md_string`, `class_name`, `point_start`) | `quadrantChart`, `title`, `x-axis a [--> b]`, `y-axis`, `quadrant-1..4`, points `Label[:::class]: [x, y] [styles]`, `classDef`, acc* | 366 + 141 |
| sankey | Jison CSV | `sankey[-beta]` then `source,target,value` rows with `"…"` quoting and `""` escapes | 117 + 77 |
| radar | Langium | `radar-beta[:]`, `axis id["label"], …`, `curve id["label"]{v, …}` or `{axis: v, …}`, `showLegend`, `ticks`, `max`, `min`, `graticule circle\|polygon`, acc*, title | 282 |
| venn | Jison emulated (case-insensitive, indent-sensitive `text`) | `venn-beta`, `title`, `set id["label"][:size]`, `union a,b["label"][:size]`, `text`, `style targets k:v,…` | 244 + 112 |

Detection (`detect.ts:37-72`) has 33 ordered regexes. All of them are needed, because archify maps the non-chart
types to its NATIVE message or to "not supported".

**PeleError messages on the chart path** (`grep` of `PeleError(|syntaxError(|fail(|invalid(` over the closure):
- **Core / limits (5):**
  - `Diagram is nested too deeply.`
  - `Diagram source is longer than the limit of 50000 characters.`
  - `No diagram type detected for the given text.` (unsupported-diagram)
  - `Diagram type "X" is not supported yet.` (unsupported-diagram)
  - `Diagram output is longer than the limit of 4000000 characters.`

  Sources: `core.ts:47,69,74,78,133`.
- **Front matter (1 template wrapping about 13 YAML messages):** `Front matter is not valid YAML. YAML: …`
  (`preprocess.ts:153`; `util/yaml.ts:42,167,190,191,196,205,239,251,349,383,425,428,464`).
- **Syntax (2 heads, the only part archify keeps):** `Parse error on line N:` + snippet + detail, and
  `Lexical error on line N. Unrecognized text.` + snippet (`errors.ts:39-52`). Detail templates:
  - Chevrotain style, pie/radar: `Expecting X but found Y.` (`tokens.ts:259-262`).
  - Jison style, xychart/quadrant/sankey/venn: `Expecting X, got 'T'`.
  - Expected-lists: pie 3 (`pie/parser.ts:98,131`, `tokens.ts:255`), xychart 4 (`parser.ts:26,33,109`),
    quadrant 5 (`parser.ts:44,52,61,80,138`), sankey 4 (`parser.ts:45,51,53,76`), radar 4 (`parser.ts:182,207,273,276`),
    venn 5 (`parser.ts:28,33,64,81,96`).
- **Semantic (10):**
  - pie: `"L" has invalid value: V. Negative values are not allowed in pie charts. All slice values must be >= 0.`
    (`pie/model.ts:17`, prints V via `String(number)`)
  - xychart: `No Plot to render, please provide a plot with some data` (`xychart/render.ts:59`)
  - quadrant: `value for S V is invalid, please use a valid number|hex code|number of pixels (eg. 10px)` and
    `style named K is not supported.` (`quadrant/db.ts:42,74`)
  - sankey: `Sankey diagrams cannot have circular links, and "id" is part of one.` (`sankey/layout.ts:112`)
  - radar: `Axes must be populated before curves for reference entries` and `Missing entry for axis L`
    (`radar/model.ts:47,54`)
  - venn: `unknown set identifier: a, b`, `union requires multiple identifiers` and `text requires set`
    (`venn/db.ts:50`, `venn/parser.ts:54,107`)

Archify's own messages on top of these (`charts.mjs`): the NATIVE redirect (9 types), the supported-list message,
blank cell, non-numeric cell, invalid JSON, not an array of objects, CSV needs a header, missing column, row cap,
plus "not UTF-8" in `sources.mjs:311`.

Scope decision needed: YAML front matter (467 LOC) and `%%{init}%%` JSON are reachable from a ```` ```mermaid ```` fence
(`parse.mjs:253` passes the fence literal as chart text). Leaving them out means a deviation entry plus a clear error.

## 6. Upstream tests

**archify** (`C:\projects\archify\test\`): only `explainer-charts.test.mjs` (79 lines, 6 tests). The other match,
`readme-showcase.test.mjs`, is about a README image. Inputs usable as fixture seeds:
- `xychart-beta\n  x-axis ["Before", "After"]\n  bar [80, 600]` (`:20,47`), which asserts `class="pele pele-xychart"`
  and a `<text …>600</text>` data label.
- `flowchart LR\n  A --> B`: NATIVE redirect, line 10 (`:29-31`).
- `pie title Share\n  "A" : abc`: syntax error located at line 11 (`:32-34`).
- `xychart-beta\n  x-axis ["前", "后"]\n  y-axis "延迟（毫秒）" 0 --> 700\n  bar [80, 600]`: the CJK `writing-mode` rewrite (`:38-40`).
- `pie\n  "A" : 1` (`:42`).
- `pie showData\n  "Viewer" : 0.8\n  "字体" : 0.2`: `Viewer · 0.8 (80%)`, `字体 · 0.2（20%）` (`:49-52`).
- CSV `day,p50,p99\nMon,78,82\nTue,79,600` with `kind=bar x=day y=p50,p99 unit=ms`, plus a missing column `p95` (`:58-68`).
- Blank-data rule: `day,p99\nMon,80\nTue,`, JSON `p99:null`, JSON key absent → `empty in data row 2`. Pie CSV
  `k,v\nA,60\nB,40` (`:73-78`).

**Pele's own corpus** (tag 1.3.0, `tests/`, MIT, reusable as fixture inputs):
- `tests/corpus/<type>-docs.json`: Mermaid docs examples, as arrays of source strings. pie 2, quadrant 3, radar 3,
  sankey 8, venn 8, xychart 8 (**32**). They include front matter and `config:` blocks (radar, sankey).
- `tests/compat/<type>/*.spec.ts`: Mermaid's own parser specs, adapted. Case counts: pie 14+6, xychart 58+6,
  quadrant 24+4, sankey 5 (+ `upstream/energy.csv`), radar 20+5, venn 17+6. They cover the accept/reject edges and
  are a good source of error-case inputs.
- `tests/render/<type>.test.ts` + `-security.test.ts` (render assertions: escaping, hostile input). Case counts:
  pie 11, xychart 20+6, quadrant 15+6, sankey 18+8, radar 13+5, venn 28+6.
- `tests/<type>/oracle.test.ts` (fuzz against Mermaid with a `FRAGMENTS` list + a seeded `mutator`/`random` from
  `tests/support/corpus.js`) and `stress.test.ts` (large inputs). The FRAGMENTS lists plus a seeded mutator in
  `capture-fixtures.mjs` would give a few hundred deterministic fuzz fixtures per type, captured through `charts.mjs`.

Harvesting these inputs into `tools/capture-fixtures.mjs` and minting the SVG/error outputs through upstream
`charts.mjs` keeps the port's convention: Node decides the expected bytes and Pele's tests supply only inputs.

## 7. Existing port pieces

| Need | In the port | Where |
|---|---|---|
| `Number#toString` | `Js.Num` (shortest round-trip, ES layout) | `src/Archify.Net.Core/Js/Js.cs:13-45` |
| `Math.round` | `Js.Round` (half-up, −0 kept) | `Js.cs:47-53` |
| `toFixed` | `Js.ToFixed` (exact BigInteger) | `Js.cs:74-82` |
| `Math.hypot` (2 args) | `Js.Hypot` (V8 algorithm) | `Js.cs:136-145` |
| `Number()`, `parseFloat` | `Js.ToNumber`, `Js.ParseFloat` | `Js.cs:184-217` |
| `parseInt` | **missing** (small) | — |
| stable comparator sort, default sort | `Js.Sort`, `Js.SortDefault` | `Js.cs:174-182` |
| JS whitespace / trim | `Js.IsSpace`, `Js.Trim`, `TrimStart`, `TrimEnd` | `Js.cs:291-293`, `JsText.cs:162-163` |
| Han detection | `Js.IsHan`, `HasHan` | `JsText.cs:133,138` |
| insertion-ordered Map/Set | `JsMap`, `JsSet` | `Js/JsMap.cs`, `Js/JsSet.cs` |
| `String(jsonValue)` (for data-chart messages) | `Js.String`, `PrimitiveString` | `Js.cs:154-172` |
| `JSON.stringify` | `Js.Stringify` | `Js.cs:222` |
| V8 sin/cos/acos/atan2/log10 | **missing.** No renderer so far needed trig (no `Math.Sin/Cos` in the port) | — |
| Pele Arial metrics measurer | **missing**, new 60 LOC (archify's `Utils.TextUnits` is a different model) | `Shared/TextFit.cs` (not reusable) |
| SVG writer | none needed. Pele builds strings, and `num`/`esc`/`labelSvg` port as-is (`Shared/SvgAttrs.cs` is archify's own) | — |
| Fixture pipeline | `tools/capture-fixtures.mjs` + `ParityTests.cs`. No chart or explainer fixtures yet; explainer itself is PORT-009 (`src/Archify.Net.Cli/Args.cs:95-97`) | — |

---

## Suggested task split

The order is by risk: settle the math first, because it decides feasibility for radar and venn. Sizes are rough C#
LOC plus effort (S ≤ ½ day, M ≈ 1 day, L ≈ 2 days).

1. **V8 math parity spike** (M, about 700 LOC). Port V8 `base/ieee754.cc` sin, cos, acos, atan2, log10 (+
   `rem_pio2`, kernels) into `Js.Math`. Turn the scratchpad harness (`mathcheck\gen.mjs` + `check.cs`, 1 M cases)
   into a test, and iterate until it reports 0 differences. If fdlibm misses, try the glibc variant. **Gate:** if no
   variant reaches 0, stop and decide on venn/radar deviations before going further.
2. **Fixture capture** (S–M). Extend `tools/capture-fixtures.mjs` to mint `{input → {svg,width,height,type} | {error,line,example}}`
   through upstream `charts.mjs` (`compileExplainerChart`, and `chartFromData` for CSV/JSON). Seeds: archify's 6
   tests, Pele's 32 corpus docs, the compat-spec inputs, and seeded FRAGMENTS fuzz per type. Also capture the full
   `PeleError.message` (stricter than archify needs).
3. **Pele core** (L, about 1,100 LOC). `PeleError` + `locate`/`syntaxError`, `detect` (33 regexes, JS `\s`),
   `preprocess` (comments, HTML-attr rewrite, directives via System.Text.Json, front-matter *detection*),
   `encodeEntities`/`decodeEntities`, chars, metrics measurer, `num`/`esc`/`escText`, `labelSvg`/span styles,
   `layoutLabel` (wrap, inline `<b>`/markdown), `svgDocument` + `rounded()` rx rewrite, `resolveStyle`/`seriesColor`,
   the common Chevrotain `tokenize`/`Reader`, `caseless`, `fitWidth` (degenerate). Unit tests on `num` and the
   measurer. Parity-gated via pie in task 4.
4. **pie + `charts.mjs` wrapper** (M, about 400 LOC). The pie parser/model/render, `compileExplainerChart` (NATIVE
   map, `pieLegend` with Han `（）`, CJK `writing-mode` rewrite, error first-line + line), and `chartFromData` (blank-data
   rule, CSV splitter, JS `Object.keys` integer-key order, row caps). The first end-to-end parity green.
5. **xychart** (L, about 1,000 LOC). State-machine lexer, parser, db, ticks (needs `Js.Log10` from task 1), render
   (bars, lines with clipping, data labels, legend). Archify's data charts all land here, so it has the most
   user-facing weight.
6. **quadrantChart** (M–L, about 900 LOC). Lexer (9 states, MD_STR), parser, style validation errors, render with
   greedy label placement. No trig.
7. **sankey** (M, about 700 LOC). CSV lexer/parser, layout (topological depth, cycle error, bisection claim, 6 settle
   passes, `Math.pow(0.99, pass)`), render. Pure arithmetic, so it is low risk once `num`/`Js.Num` are in.
8. **radar** (M, about 550 LOC). Langium-style tokens on the shared `Reader`, model errors, Catmull-Rom render.
   Needs task 1's sin/cos.
9. **venn** (L, about 1,000 LOC). Case-insensitive, indent-aware lexer, parser, db, the layout (lens bisection,
   ring relaxation, `findSpot` hill climb, `regionPath` with atan2 sort), render. The highest numeric sensitivity, so
   fuzz-heavy fixtures. Needs task 1 (acos/atan2/sin/cos).
10. **Front-matter YAML** (M, about 470 LOC), *optional, needs Martin's decision*. Port `util/yaml.ts` + `merge()`
    for `---` fences, or record a deviation and reject front matter in chart fences. Pele's corpus uses front matter
    for radar and sankey configs, so leaving it out loses those fixtures.
11. **Notice + docs** (S). Pele MIT notice in the port's third-party notices, `docs/deviations.md` entries (canvas
    measurer omitted, zoom/mount omitted, any YAML decision), and the re-pin note (Pele is at 1.8.0 upstream).

Total ≈ 6–7 k C# LOC across 9–11 tasks. Tasks 4–9 are independent once 1–3 are in, so they can run in parallel.
