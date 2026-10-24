# PORT-009: the archify 3.2.0 explainer in C# — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** port archify 3.2.0's explainer (a Markdown or JSON draft turned into a conclusion-first, one-page HTML answer)
to C#, with pages byte-identical to Node's, and give the CLI and the MCP server its commands (`validate`, `render`,
`deliver`, `finalize`, `check`, `browser-check`, `visual-check` on `explainer`, plus `patch explainer`).

**Architecture:**
- `src/Archify.Net.Core/Explainer/` gets one C# file per upstream module (`archify/explainer/*.mjs`), ported from the
  tag source. Small modules are folded into the file that uses them (layout into the renderer; revision and page-bundle
  into `ExplainerBundle`). `ExplainerCharts` (PORT-008) is reused as is.
- Upstream runs native diagrams, the checker and the renderer as Node child processes and hands the prepared bundle over
  through `ARCHIFY_EXPLAINER_*` environment variables. The port runs all of it in process: native children through
  `ArchifyNet.Render` and `ArtifactChecker`, the bundle as a `JsonObject` passed by reference. Only `git` stays a child
  process, as in phase 4.
- `reader.js` and `reader.css` are embedded verbatim, the way the viewer template is. Image export (PNG, WebP, JPG via an
  SVG `foreignObject` and a canvas) and Print / PDF (`window.print()`) live entirely in `reader.js`, so the CLI needs no
  headless browser for them; the only browser work is the explainer browser gate (Task 8), on the existing Playwright.
- Every expected value is minted by Node from upstream at `v3.2.0`; the C# side only compares.

**Tech Stack:** .NET 10, C# 14, xUnit, `System.IO.Compression` (BCL), Microsoft.Playwright 1.63.0 (already in the CLI),
Node 24 for capture.

**Spec:**
- Card PORT-009 (CARD-2359) body, and Martin's decision of 2026-10-22 to port the explainer whole.
- Upstream surface at `v3.2.0` (`208acba8`): `archify/explainer/*.mjs`, `reader.js`, `reader.css`,
  `archify/renderers/explainer/render-explainer.mjs`, `archify/schemas/explainer.schema.json`,
  `archify/references/explainer-authoring.md`, `archify/examples/delivery.explainer.json`, the explainer parts of
  `archify/bin/archify.mjs`, `archify/bin/finalize.mjs` and `archify/bin/visual-check.mjs`, `docs/cases/explainer/`, and the 15
  `test/explainer-*.test.mjs` files.
- The port spec `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (## Testing, R12) sets the parity bar.
- `docs/deviations.md`, "PORT-009 note": `ExplainerCharts.Compile` lets a front-matter `PeleException` propagate, and
  the CLI must handle it as upstream does.

## Global Constraints

- **Byte parity.** A page from `ExplainerRender` equals upstream `renderExplainer` byte for byte, except each
  `htmlGzip` payload (Decision 1): the test compares the inflated payloads instead, and everything else byte for byte.
  Every parse, validate, prepare, check and patch result serialises (`Js.Stringify(x, 2)`) to Node's golden.
  Native child HTML is not re-pinned here: the native suites pin it. The capture writes each child `html` as
  `"sha256:<hex>"` and each `htmlGzip` as `gz-sha256:<hex of the inflated bytes>`, and the C# side applies the same
  tokenisation to its own output before comparing (Task 1).
- **Fixed goldens (Decisions 2 and 3).** The capture also runs a corrected oracle: upstream's own modules with the two
  decided fixes written in, loaded through a source shim as `wfProbe` and the venn Q1 grid already do. The fixes are
  render.mjs:38 `|| 'zh-CN'` → `|| 'en'`, and the diagrams.mjs:270 lane label, which becomes `Branch ${row}` unless the
  locale starts with `zh`, with the locale passed in at parse.mjs:208. Whenever the corrected output differs from Node's,
  the capture writes it beside the Node golden as `<name>.fixed.<ext>`. The Node golden stays. One comparer rule
  covers every affected row, with no row pinned by hand: the port is compared with `<name>.fixed.<ext>` when it
  exists, otherwise with the Node golden. `Fixed_goldens_differ_only_by_the_decided_fixes` checks that each fixed
  golden's draft has no locale or a shorthand branch lane, and that Node's golden carries `lang="zh-CN"` or `分支`
  where the fixed one does not.
- **Upstream defects are fixed, not mirrored.** This covers archify and anything archify copies on purpose. Keep the
  Node golden. Add a known-deviation row with a `-reference` row or a fixed golden, and a dedicated fact, and list it in
  `docs/deviations.md`.
- **Upstream issues (Martin, 2026-10-24).** An upstream issue is filed only for a bug on input someone would actually
  write, where the defect originates. Our product choices (the default locale `en`), platform differences (the gzip
  stream) and contrived edge cases get a `docs/deviations.md` line and no issue. The lane label (Decision 2) gets an
  issue.
- **Leaner process (Martin, 2026-10-24).**
  - A review finding about a contrived input ("fails late", an edge case nobody would write) gets a card and is parked.
    It is not fixed in more review rounds. Wrong output on realistic input is still fixed.
  - Cosmetic, test and tooling minors go to a card, not to a final-wave commit.
  - A family of findings that keeps producing cases is closed with one generic check.
  - All parked items go on one new card (the next free `PORT-` id), created in Task 9.
- **Pins.** Upstream `v3.2.0`, commit `208acba8d412335185b2056fedf54b3405df43fb`, checkout `C:\projects\archify` (skill
  root `C:\projects\archify\archify`). Read upstream with `git -C C:\projects\archify show v3.2.0:<path>`; never check
  out another commit there.
- **No NuGet dependency in Core**, and no new package anywhere. Gzip is `GZipStream` with
  `ZLibCompressionOptions { CompressionLevel = 9 }`.
- **JS semantics through the existing helpers.**
  - Use `Js.*` (`Num`, `Round`, `ToNumber`, `String`, `Stringify`, `Sort`, `Trim`, `IsSpace`, `IsHan`, `LocaleCompare`,
    `EncodeUriComponent`) and `JsMap`/`JsSet` for insertion-ordered `Map`/`Set`.
  - Never use `System.Math` transcendentals, `Pow` or `Round`. `Math.round` is `Js.Round`
    (`Math.round(nativeWidth)`, `Math.round(chart.width)`, the `Math.round(issue.scale*100)` message).
  - A number printed through `escape(value)` or a template literal is `Js.Num`.
- **JS regex classes are hand-written** (as in PORT-008). Never use `RegexOptions.ECMAScript`; `JsRegex.Translate` stays
  for schema patterns only.
  - `\s` is `Js.IsSpace` and `\d` is `[0-9]`. Outside `/iu`, `\w` and `\b` are ASCII.
  - `\p{Script=Han}` is `Js.IsHan`. `\p{Script=Latin}`, `\p{L}` and `\p{N}` are new range tables (Task 1), pinned
    against V8 for every code point.
  - Without the `m` flag, `^` and `$` are input anchors: `$` is `\z`.
  - `/iu` (the writing rules) is Unicode simple case folding. Use `RegexOptions.IgnoreCase | CultureInvariant`, and
    pin `ſ`/`K` (U+017F, U+212A) in the writing table. A difference there is contrived: card it.
- **Strings.** Source-map `start`/`end` and every `slice`/`indexOf` are UTF-16 indices, as C# strings are.
  `Array.from(s).length` counts code points. `toLowerCase` is JS full case mapping: `İ` (U+0130) becomes `i̇`, unlike
  `ToLowerInvariant`; pin it in the shorthand table. `normalize('NFKC')` is `string.Normalize(NormalizationForm.FormKC)`.
- **Key order.** A JSON-format draft is parsed into V8 property order: integer-like keys first, ascending, then
  insertion order; a duplicate key takes the last value at its first position. One mechanism does this
  (`ExplainerJson.Parse`, reusing `ExplainerCharts`' array-index test), so every later `Object.keys`, `Object.entries`
  and `JSON.stringify` over author keys is right without per-site care.
- **Hashing and bytes.** SHA-256 runs over UTF-8; a lone surrogate encodes as `EF BF BD`, as Node's does. Base64 is
  `Convert.ToBase64String`. `TextDecoder('utf-8', {fatal:true})` is a throwing `UTF8Encoding(false, true)`.
  `readFileSync(p, 'utf8')` replaces invalid bytes with U+FFFD and keeps a BOM.
- **Verbatim embedding.**
  - `upstream/explainer/reader.js` and `reader.css` become Core resources `explainer/reader.js` and `explainer/reader.css`.
  - The two page functions of `explainer/browser-check.mjs`, `measureNativePreviews` (lines 13-25) and
    `measurePublicHtml` (lines 30-65), become `upstream/explainer-gate/*.js`, CLI resources `explainer-gate/*.js`.
    `UPSTREAM.md` records the ranges, as it does for `viewer-gate`.
  - The short inline `evaluate` expressions of `browser-check.mjs` are copied into C# string literals, verbatim, with
    their line numbers.
- **Diagnostics.**
  - Each diagnostic is built in upstream's key order: `code, severity, message, subject, evidence, supportedFixes`, with
    `subject` keys in the order each module writes them.
  - Inside Core, `throwDiagnosticError` is `ArchifyDiagnosticException`. At the public facade
    (`ArchifyNet.RenderExplainer`) problems come back as diagnostics, not exceptions.
  - The one exception that crosses on purpose is the front-matter `PeleException` (PORT-008 note). Core lets it out of
    `ExplainerValidator` and `ExplainerPrepare`. Each CLI command reports it as that command's upstream catch does
    (Task 7, Review Focus 1).
- **No environment variables as configuration.** The CLI reads no `ARCHIFY_*` variable, `ARCHIFY_EXPLAINER_PREPARED`,
  `ARCHIFY_EXPLAINER_SNAPSHOT_INPUT` and `ARCHIFY_EXPLAINER_PAGE_DIR` included. `ARCHIFY_*` in the capture scripts' Node
  children is the existing oracle mechanism and stays.
- **Browser.** Use the C# Playwright that the CLI already uses (`PlaywrightInspector`), with no CDP-only code path.
- **Files and commits.**
  - Files are UTF-8 without BOM, with LF line endings.
  - Each commit has an exact `git add` list, `git commit -F <file>`, and the trailer
    `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never use `-a`, `-A` or `.`.
- **Capture determinism.** Running `node tools/capture-fixtures.mjs` and `node tools/capture-cli-fixtures.mjs` twice
  gives the same hash over `tests/fixtures` and `tests/goldens`.
- **Reviews.** Each task is reviewed by agy (Gemini). Fable reviews this plan and the whole branch (standing order).
  Branch `port-009-explainer` from master `ddc86b3`.
- **Out of scope.** `preview` (not ported) and its explainer message; the update check; and atomic-output (the
  phase-5 deviations D1-D10 apply to explainer outputs as they stand).

## Review Focus

1. **A ```` ```mermaid ```` fence whose front matter is not valid YAML.** `compileExplainerChart` throws from inside
   `validateExplainer` (validate.mjs:119), so each command reports it through its own catch:
   - `validate --json`: stage `input`, `input/read` "Input could not be read: Front matter is not valid YAML. …", exit 1;
   - `deliver --json`: `Could not read delivery input "…": …` with the same diagnostic, exit 1;
   - `finalize --json`: `finalize/runtime`, all four gates `not-run`, `browserCheck {requested:false}`, exit 1;
   - `render`: upstream has no explainer branch in `commandRender` (archify.mjs:4271-4287) and runs the renderer without
     JSON diagnostics, so the boundary rethrows and Node prints a raw stack (exit 1, empty stdout). The port prints the
     `PeleException` message to stderr and exits 1: a stated deviation, compared by exit code and the first stderr line
     only;
   - `patch`: `explainer/patch-runtime`.

   Task 3 pins the throw (a thrown row). Task 7 pins one CLI fixture per command, and Task 8 pins finalize.
2. **A malformed JSON-format draft.** The line comes from V8's `JSON.parse` message (parse.mjs:78-80). Node 24's V8
   gives `at position N (line L column C)` for the structural errors: "Expected double-quoted property name",
   "Expected property name or '}'", "Expected ',' or '}' after property value", "Unterminated string",
   "Unexpected non-whitespace character after JSON" and their kin. It gives no position for "Unexpected end of JSON
   input" or for any "Unexpected token 'x', "…" is not valid JSON" (a BOM, `[1,]`, `{"a":}`), which therefore report
   line 1. A BOM before `{` is still format `json` (JS `trimStart` strips U+FEFF). `ExplainerJson.Parse` is written
   from this list, and Task 2 pins about 10 drafts, one per message kind.
3. **CRLF and mixed line endings.** These touch the source-map offsets, the fence and `@diff` literals (`\r?\n$`
   trimming), the diff `ending` of each row, and patch's `slice(range.start, range.end)` on a CRLF draft. Seeds in
   Task 2 (parse), Task 3 (diff) and Task 6 (patch).
4. **Non-ASCII author text.**
   - Shorthand actor ids: `inferredId` applies NFKC, then `toLowerCase`, then the slug (`İstanbul`, `Straße`,
     full-width `Ａ`, Han labels falling back to `actor-<hash>`).
   - Reading units in the writing lint: Han, Latin with `’`, decimals.
   - `contentSize` counts code points (emoji) in the layout weight.
   - Seeds in Tasks 2, 3 and 5.
5. **Page composition branches.**
   - A panel with one diagram and steps that target it (the guided layout).
   - A native canvas wider than 1000 (span 12).
   - Comparisons with 2 and 3 data columns (`columns.length + 1 > 3` goes wide, layout.mjs:9).
   - Snippets whose longest line is 48, 49, 72 and 73 characters (the `> 48` and `> 72` tiers, layout.mjs:12).
   - An English page against a zh page, and each of the four styles.
   - Seeds in Task 5's `pages` and `layout` groups.

---

### Task 1: upstream copy, the explainer type, fixture pipeline, Unicode tables

**Files:**
- Copy from v3.2.0, verbatim: `upstream/schemas/explainer.schema.json`, `upstream/examples/delivery.explainer.json`,
  `upstream/references/explainer-authoring.md`, `upstream/explainer/reader.js` and `upstream/explainer/reader.css`.
- Create: `upstream/explainer-gate/measure-native-previews.js` and `measure-public-html.js` (the line ranges in Global
  Constraints, with no trailing newline).
- Modify: `upstream/UPSTREAM.md` (lift the "Not copied until PORT-009" sentence; add the new files and the ranges).
- Copy into `skill/archify/`: `references/explainer-authoring.md` (the substitutions below),
  `schemas/explainer.schema.json` and `examples/delivery.explainer.json`.
- Modify: `src/Archify.Net.Core/Archify.Net.Core.csproj` (embed `explainer/reader.js` and `explainer/reader.css`) and
  `src/Archify.Net.Cli/Archify.Net.Cli.csproj` (embed `explainer-gate/*.js`).
- Modify: `src/Archify.Net.Core/DiagramType.cs`, `src/Archify.Net.Core/Archify.cs` and `src/Archify.Net.Cli/Args.cs`.
- Modify: `src/Archify.Net.Core/Js/JsText.cs` (Unicode tables).
- Modify: `tools/capture-fixtures.mjs` (explainer section skeleton, Unicode table). Create:
  `tools/explainer-seeds/README.md`.
- Test: `tests/Archify.Net.Core.Tests/ExplainerParityTests.cs` and `JsTextTests.cs`. Fixture:
  `tests/fixtures/js/unicode-props.json`.
- Modify: the tests that enumerate `upstream/examples/*.json` (`CliSeamTests:110`, `ValidationTests:8`,
  `RepositoryEvidenceTests:304`; `JsTests:65` and `ArtifactCheckerTests:8` already filter) and
  `tests/Archify.Net.Mcp.Tests/SkillPackageTests.cs` (the second substitution).

**Interfaces:**
- Produces `DiagramType.Explainer`, with name `explainer`, parsed by `DiagramTypes.Parse`.
  - `SchemaValidator` loads `explainer.schema.json` through its existing `Enum.GetValues` loop.
  - `SemanticCollection` throws `ArgumentOutOfRangeException` for it.
  - `ArchifyNet.Render`, `Layout` and `Validate` throw `NotSupportedException("use ArchifyNet.RenderExplainer")` for it.
    That is the facade's one documented exception type.
  - `Args.DiagramType` still rejects `explainer` with today's message until Task 7, so no CLI path changes in this task.
- Produces `Js.IsLatin(int cp)`, `Js.IsLetter(int cp)` (`\p{L}`) and `Js.IsNumber(int cp)` (`\p{N}`) in `JsText.cs`.
  Each is a sorted range table like `HanRanges`.
- Produces the fixture layout. Later tasks only add groups to it:
  - Seeds: `tools/explainer-seeds/<group>.json`, a JSON array of `{name, call, args, files?, repo?}`.
    - `call` names the upstream function: `parseExplainer`, `parseInline`, `renderInline`, `parseExplainerDiff`,
      `lintExplainerWriting`, `validateExplainer`, `compileExplainerDiagram`, `resolveExplainerSources`,
      `prepareExplainerDiagrams`, `prepareExplainer`, `readPreparedBundle`, `layoutExplainerPanels`, `renderExplainer`,
      `checkExplainerHtml`, `preparePanelPatch`.
    - `readPreparedBundle` is a two-step seed: `{draft, mutate, read}`. The runner prepares `draft` (draft A), applies
      `mutate` (one of `path`, `bytes`, `quality`, `writingMode`, `document`, `dependency`), then reads the bundle
      against `read` (draft B and its options).
    - `checkExplainerHtml` and `preparePanelPatch` seeds name their input page as a draft plus `tamper`, a list of
      exact `{from, to}` string replacements. Each side renders the page itself and applies the same replacements, so
      no input page is stored.
    - `files` maps a relative path to `{text}` or `{base64}`, or to `{zeros: n}` for size limits. These are written into
      the seed's directory.
    - `repo: true` adds the phase-4 evidence repository (`tests/fixtures/evidence/evidence-repo.fi`, pinned HEAD) as
      `repoRoot`.
  - Goldens: `tests/goldens/explainer/<group>/<name>.json`, which is `JSON.stringify(result, null, 2) + "\n"`. A page is
    `tests/goldens/explainer/pages/<name>.html`. A `<name>.fixed.json` or `.fixed.html` beside it comes from the
    corrected oracle (Global Constraints, Fixed goldens). The seed runner takes a `--oracle node|fixed` switch, and the
    capture runs both and writes the fixed file only when it differs.
  - A thrown row is `{"thrown": {"name", "message", "code"}}`. `code` is the `archifyDiagnostics` array when present.
  - Tokens in goldens:
    - `<DIR>` is the seed's directory, raw and JSON-escaped, as the CLI fixtures do.
    - `<REPO>` is the evidence repository.
    - `<TMP>` is each `archify-explainer-*` temp path: the native child's checker `file`, and the
      `nativeDiagrams[].receipt.file` of the checker.
    - A prepared bundle's `digest` is replaced by `<digest>`. The C# side asserts that its own digest is
      `sha256(Js.Stringify(bundle minus digest))`. The page's own `digest` is path-free and is compared as is.
    - Each native child `html` string in a bundle becomes `"sha256:<hex>"` (its `bytes` stays), and each
      `"htmlGzip":"<base64>"` becomes `"htmlGzip":"gz-sha256:<hex of the inflated bytes>"`. This happens at mint time
      in the capture, so a page golden is about 0.1 MB and a prepare row about 50 KB.
    - A check report's `artifact` (`sha256`, `bytes`) becomes `<artifact>`: the input page's bytes differ by the gzip
      deviation. The same holds for patch's `expectedArtifact`.
- Produces `ExplainerParityTests`:
  - `Matches_node(string group, string name)` over `Targets()`, a hand-listed set of groups that each task extends, as
    `ChartParityTests` does.
  - `static string NormalizePage(string html)`: each `"htmlGzip":"<base64>"` becomes `"htmlGzip":"gz-sha256:<hex>"`,
    the SHA-256 of the gunzipped bytes. It is the capture's rule, and a no-op on an already tokenised golden. Expected
    and actual pages are compared after it.
  - `static JsonNode TokeniseChildren(JsonNode result)`: the capture's child-`html` rule, applied to the port's output.

- [ ] **Step 1: Copy upstream.** Copy the files above with `git show v3.2.0:<path>`. Into the skill copy of
  `explainer-authoring.md`, apply the existing `node bin/archify.mjs` → `archify` substitution and a second one,
  `node archify/bin/archify.mjs` → `archify`; the reference uses that spelling at lines 259 and 271. Teach
  `SkillPackageTests.SkillReferences_MatchUpstreamWithSingleMechanicalSubstitution` both substitutions. Rename it
  `…WithMechanicalSubstitutions`.
- [ ] **Step 2: Write the failing tests.**
  - `JsTextTests.Unicode_properties_match_V8` compares the four predicates and `Js.IsSpace` with the fixture ranges, over
    every code point from 0 to 0x10FFFF.
  - `ExplainerParityTests.Reader_assets_are_embedded_verbatim` asserts that the resources equal the `upstream/explainer`
    files byte for byte.
  - `DiagramTypesTests`-style facts:
    - `DiagramTypes.Parse("explainer") == DiagramType.Explainer`;
    - `ArchifyNet.Render(DiagramType.Explainer, …)` throws `NotSupportedException`;
    - `archify validate explainer x` still answers `cli/unknown-diagram-type` (removed in Task 7).
- [ ] **Step 3: Mint.**
  - Add the explainer section header and the `unicode-props.json` table (ranges per property, read from V8 with
    `/\p{Script=Latin}/u` and friends).
  - Add the seed runner: write the `files`, call the named upstream function through
    `file:///C:/projects/archify/archify/explainer/<module>.mjs`, tokenise and write the golden.
  - Assert in the capture that every seed name is unique in its group.
- [ ] **Step 4: Port.** Implement the enum arm, the facade guard, the three tables and the resources. Make the example
  enumerations skip `*.explainer.json` where the test feeds a native renderer.
- [ ] **Step 5: Run.** Run `dotnet test --filter "Category!=Browser"`. Expect PASS, with no change in totals except the new
  facts. Run the capture twice and expect the same hash.
- [ ] **Step 6: Commit.** `feat(explainer): upstream copy, explainer type and fixture pipeline (PORT-009)`.

### Task 2: the parser and the diagram shorthand

**Files:**
- Create: `src/Archify.Net.Core/Explainer/ExplainerJson.cs` (V8-order parse, V8 error position).
- Create: `ExplainerParser.cs` (parse.mjs).
- Create: `ExplainerShorthand.cs` (diagrams.mjs:1-352: `compileExplainerDiagram`, `rankedPlacement`, `workflowWidth`,
  `rethrowNative`, `inferredId`).
- Seeds: `tools/explainer-seeds/parse.json` and `shorthand.json`.
- Test: `ExplainerParityTests` (`Targets()` adds `parse`, `shorthand`).

**Interfaces:**
- Produces `internal static JsonNode? ExplainerJson.Parse(string text, out int? errorPosition)`.
  - It returns the V8-ordered document, or null with V8's error position.
  - The position is null where V8's message has none ("Unexpected end of JSON input").
- Produces `internal static ExplainerParse ExplainerParser.Parse(string sourceText, string? inputPath = null,
  string? output = null)`.
  - `sealed record ExplainerParse(JsonObject? Document, JsonObject SourceMap, string Format, string SourceText,
    IReadOnlyList<Diagnostic> Diagnostics)`, whose `ToJson()` has the keys `document, sourceMap, format, sourceText,
    diagnostics`.
  - Source-map entries are `{line, column, start, end}`.
- Produces `internal static JsonObject ExplainerShorthand.Compile(string diagramType, IReadOnlyList<ShorthandRow> rows,
  ShorthandOptions options)`, which throws `ArchifyDiagnosticException`.
  - `sealed record ShorthandRow(IReadOnlyList<string> Cells, int Line)`.
  - `sealed record ShorthandOptions(string? Title, string? Id, int Line = 1, string Quality = "showcase",
    string? Locale = null)`.
  - `Locale` exists for Decision 2. The parser passes `document.meta.locale` at the point where it passes
    `quality_profile` (parse.mjs:208). A null locale is `en` (Decision 3), so it gets `Branch N`.
- Consumes `SchemaValidator.Validate`, `Utils.TextUnits`, `TextFit.MinimumNodeTextWidth` and
  `Utils.SemanticSigilFootprint`.

- [ ] **Step 1: Seed.**
  - `parse.json` takes the inputs of `test/explainer-parser.test.mjs` (17 tests), the four
    `docs/cases/explainer/*.archify.md` drafts and `delivery.explainer.json`.
  - It also takes every `fail(...)` site of parse.mjs (each `explainer/parse-<component>` message, one row each), Review
    Focus 2 (about 10 malformed drafts, one per V8 message kind listed there, plus a duplicate key, `[]` and `null`)
    and Review Focus 3 (CRLF, `\r` alone, a CRLF fence).
  - Further rows:
    - `||`-prefixed rows (copied line numbers);
    - a callout followed by a paragraph;
    - `{#id span=7}` headings;
    - a block id equal to its panel id;
    - the `output` option with `a\b/c.html`;
    - a diagram `source:<id>` cell;
    - a checklist mixed with a plain item;
    - comparison `{good:Win}` cells with trailing citations.
  - `shorthand.json` takes the inputs of `test/explainer-diagrams.test.mjs` for sequence (actors, inferred ids, phases),
    architecture (column centring, size), workflow (lanes: single, stair, wrapped; branch rows; width-driven wrapping;
    a `?` decision), tree, timeline and erd.
  - It also takes Review Focus 4 (one `İstanbul` row for the case mapping, `Straße`, `Ａpi`, Han labels) and each `fail`
    reason of diagrams.mjs:1-352.
  - Decision 2 rows: `workflow-branch-zh` (`@meta locale=zh-CN`), equal to Node, and `workflow-branch-en`
    (`@meta locale=en`) and `workflow-branch-default` (no locale), which get fixed goldens.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Port.** Port `parse.mjs` literally; its regexes follow the Global Constraints. Port `scan` (the JSON
  source-map pass) over the original text with `Js.IsSpace`-class skipping. `stable(kind, value)` is
  `kind + "-" + sha256(UTF-8 value)[..12]`.
- [ ] **Step 4: Run.** Expect PASS for both groups, plus a fact `Branch_lane_label_is_the_stated_deviation`: the `-zh` row
  equals Node, and the `-en` and `-default` rows equal their fixed goldens. Then run the Core suite.
- [ ] **Step 5: Commit.** `feat(explainer): parser and diagram shorthand (PORT-009)`.

### Task 3: inline language, diff, writing lint and validation

**Files:**
- Create in `src/Archify.Net.Core/Explainer/`: `ExplainerInline.cs` (inline.mjs), `ExplainerDiff.cs` (diff.mjs),
  `ExplainerWriting.cs` (writing.mjs) and `ExplainerValidator.cs` (validate.mjs).
- Create `ExplainerSources.cs`, holding for now only `ContainsSecret` (sources.mjs:19-34), which diff needs.
- Modify: `src/Archify.Net.Core/Shared/SchemaValidator.cs` (expose the raw ajv error list).
- Seeds: `inline.json`, `diff.json`, `writing.json`, `validate.json`.

**Interfaces:**
- Produces `internal static IReadOnlyList<SchemaError> SchemaValidator.Errors(DiagramType type, JsonNode data)`.
  - `SchemaError(string Keyword, string InstancePath, JsonObject Params, string Message)` is today's private `Error`,
    made internal.
  - It returns the list `Validate` builds, the `portablePath` post-check entry included, in ajv `allErrors` order.
- Produces `ExplainerInline`:
  - `Parse(string text, InlineOptions o) → InlineParse(JsonArray Tokens, JsonArray References, List<Diagnostic> Diagnostics)`.
  - `Render(string text, InlineContext ctx) → string`, which throws `ArchifyDiagnosticException` on any diagnostic.
  - `EscapeHtml(string?)`.
  - `InlineContext` carries what render.mjs:49 passes: `JsonObject? Document`, `JsonObject? Sources`,
    `JsonObject? Diagrams`, `string? PanelId`, `string? BlockId`, `JsonArray? Footnotes` and
    `JsMap<string,int> Citations`. The last two keep first-use numbering across calls.
- Produces `ExplainerDiff.Parse(string text, DiffOptions o) → DiffParse(JsonArray Rows, JsonObject Counts,
  List<Diagnostic> Diagnostics, string Format)`.
- Produces `ExplainerWriting.Lint(IReadOnlyList<WritingEntry> entries, string mode = "warn", string locale = "auto",
  JsonObject? sentenceLimits = null) → List<Diagnostic>`.
  - Upstream's `TypeError` throws are `ArgumentException` with the same message.
- Produces `ExplainerValidator.Validate(JsonObject document, JsonObject? sourceMap, string? inputPath,
  string writingMode = "warn") → (bool Ok, List<Diagnostic> Diagnostics)`.
  - It lets a front-matter `PeleException` from `ExplainerCharts.Compile` propagate (Review Focus 1).

- [ ] **Step 1: Seed.**
  - The inputs of `explainer-inline`, `-diff`, `-writing` and `-validation.test.mjs`.
  - For inline:
    - every `problem` code;
    - `[](source:x)` with an empty label;
    - nested emphasis at depth 6;
    - escaped delimiters;
    - `https://u:p@x` and a URL with a space;
    - `typical:` with and without a footnote context;
    - citation numbering across three calls.
  - For diff:
    - every `explainer/diff-*` code;
    - a `\ No newline` marker on a repeated row;
    - `+++` inside an unfinished hunk;
    - quoted file names;
    - a secret on a `.env` path and in a `.js` file;
    - CRLF rows (Review Focus 3).
  - For writing:
    - each rule in zh and en;
    - `scope-length`, `verdict-emphasis`, `paragraph-length`, `de-chain` and `sentence-length` at their bounds;
    - URL masking with a trailing `).`;
    - offset ties sorted by `localeCompare`;
    - all three modes;
    - one `ſ` folding row (a difference goes to the parked card).
  - For validate:
    - every `schema-<keyword>` example branch, including a bad `meta.output`;
    - `reserved-id` (`board`, `explainer-x`, a `source-` collision);
    - every reference and target code;
    - a chart with text and data;
    - a chart error with a `line` offset;
    - a front-matter chart (a thrown row);
    - a native spec with a schema error;
    - writing in `strict`.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Port** the four modules and `ContainsSecret`. The rules' `pattern` sources are hand-translated once into
  static `Regex` fields, and a new `Match` loop runs per entry, as upstream's per-entry `RegExp` does.
- [ ] **Step 4: Run.** Expect PASS for the four groups, then run the Core suite.
- [ ] **Step 5: Commit.** `feat(explainer): inline language, diff, writing lint and validation (PORT-009)`.

### Task 4: resources — sources, native diagrams, the prepared bundle

**Files:**
- Modify: `src/Archify.Net.Core/Explainer/ExplainerSources.cs` (`Resolve`: `safePath`, `readContained`, `imageMime`,
  `deriveRepository`, repository excerpts, data charts).
- Create: `ExplainerDiagrams.cs` (diagrams.mjs:354-517: `readSpec`, `semanticOf`, `changesPreview`, `Prepare`,
  `ValidateTarget`).
- Create: `ExplainerPrepare.cs` (prepare.mjs).
- Modify: `src/Archify.Net.Core/Evidence/GitRunner.cs` only if the byte call with `maxBuffer` is missing.
- Seeds: `prepare.json`.

**Interfaces:**
- `sealed record ResourceOptions(string? RepoRoot, string? BaseDir, JsonObject? SourceMap, string? SourcePath,
  string Quality)`.
- `ExplainerSources.Resolve(JsonObject document, ResourceOptions o) → JsonObject` returns
  `{sources, assets, charts, dependencies, repository}`.
- `ExplainerDiagrams.Prepare(JsonObject document, ResourceOptions o) → JsonObject` returns `{diagrams, dependencies}`.
  - Each native child renders through `ArchifyNet.Render(type, spec, new RenderOptions(quality, repoRoot))` and is
    checked by `ArchifyNet.Check(html).Report`.
  - The child's checker `file` is a path the port names but does not write:
    `Path.Join(Path.GetTempPath(), "archify-explainer-diagram-<8 hex>", "diagram.html")` (a stated deviation; tokenised
    `<TMP>`).
  - The renderer's diagnostics feed `rethrowNative` (Task 2) with the reasons `diagram-render` and `diagram-check`.
  - The 32 MiB artifact limit is kept. There is no 120 s timeout.
- `ExplainerDiagrams.ValidateTarget(JsonObject diagrams, JsonObject target, TargetOptions o)`.
- `sealed record PrepareOptions(string InputPath, string? BaseDir = null, string? RepoRoot = null,
  string? Quality = null, string WritingMode = "warn", string? Output = null)`.
- `ExplainerPrepare.ParseValidated(string sourceText, string? inputPath, string? output, string writingMode) →
  ExplainerParse`.
- `ExplainerPrepare.Prepare(string sourceText, PrepareOptions o) → JsonObject` returns the bundle with its `digest`.
- `ExplainerPrepare.AssertDependencies(JsonObject bundle)`.
- `ExplainerPrepare.CheckBundleSize(JsonObject bundle)` is writePreparedBundle's 64 MiB `resource-limit`, without the
  file.
- `ExplainerPrepare.ReadPrepared(JsonObject bundle, string sourceText, string inputPath, ReadPreparedOptions o) →
  JsonObject` is readPreparedBundle on an in-memory bundle (a deep clone, as the JSON round trip makes one).
  - `ReadPreparedOptions(string ExpectedInputPath, bool CheckDependencies = true, string? Quality = null,
    string? WritingMode = null)`.
- `ExplainerPrepare.Rebind(JsonObject bundle, ExplainerParse parsed) → JsonObject`.
- `ExplainerPrepare.ValidatePreparedTargets(JsonObject document, JsonObject diagrams, ResourceOptions o)`.

- [ ] **Step 1: Seed.** Use `explainer-sources.test.mjs` and the resource cases of `explainer-diagrams.test.mjs`, plus:
  - images: PNG, JPEG, GIF and WebP from 1-pixel bytes; an extension that does not match its magic bytes; 8 MiB + 1
    (`zeros`); a 32 MiB budget overrun over five images;
  - `safePath` rejections (`../x`, `a//b`, `.git/x`, `C:x`, `x?y`) and `secret-path` (`.env`, `id_rsa.pub`,
    `prod.pem`);
  - data charts: CSV, JSON, a non-UTF-8 file (`The data file is not UTF-8 text.`), a wrong extension, and a chart error;
  - a diagram `source=` file, an inline spec, both of them, a type mismatch, invalid JSON, `changes` marks with a bad
    target, and a target node or route that is absent from the SVG;
  - with `repo: true`: a code block, a cited-only source excerpt, a revision mismatch, a missing file, a line out of
    range, secret content and non-UTF-8 content;
  - `quality` standard and showcase;
  - `readPreparedBundle` on a bundle that does not belong to the draft: each `prepared-mismatch` reason, plus
    `dependency-changed` after a frozen file changes.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Port.** `readContained` and `readSpec` refuse a reparse point at any path component
  (`FileAttributes.ReparsePoint`; no seed, it needs Developer Mode). Before and after the read they compare size and
  last-write time only. `// ponytail:` Node also compares `dev`+`ino` (sources.mjs:111-113, 146-157;
  diagrams.mjs:372-381). A same-size, same-time swap of the file during the read is not caught; `asset-changed` and
  `diagram-source-changed` are race-only codes no fixture reaches. The upgrade path is a `GetFileInformationByHandle`
  file id. Put the file-id race and the unseeded reparse-point refusal on the parked card.
- [ ] **Step 4: Run.** Expect PASS, plus `Prepared_digest_is_self_consistent`. Then run the Core suite.
- [ ] **Step 5: Commit.** `feat(explainer): sources, native diagrams and the prepared bundle (PORT-009)`.

### Task 5: the page — render, layout, revision, page bundle, facade

**Files:**
- Create: `src/Archify.Net.Core/Explainer/ExplainerRender.cs` (render.mjs and layout.mjs) and `ExplainerBundle.cs`
  (revision.mjs and page-bundle.mjs).
- Create: `ExplainerPipeline.cs`, the public entry points for the CLI.
- Modify: `src/Archify.Net.Core/Archify.cs` (`ArchifyNet.RenderExplainer`).
- Create: `src/Archify.Net.Core/ExplainerOptions.cs`.
- Seeds: `layout.json`, `pages.json`; the `cases` goldens (Step 1).

**Interfaces:**
- `internal static JsonArray ExplainerRender.Layout(JsonObject document, JsonObject diagrams)` returns
  layoutExplainerPanels' rows.
- `internal static string ExplainerRender.Render(JsonObject document, RenderExplainerOptions o)`.
  - The options mirror render.mjs:37: `PreparedBundle`, `SourceText`, `SourceMap`, `SourcePath`, `PreparedSources`,
    `PreparedDiagrams`, `Diagnostics` and `PageDirectory`.
- `internal static class ExplainerBundle`:
  - `string Revision(JsonObject args)`;
  - `JsonObject Pack(JsonObject bundle)`: `html` becomes `htmlGzip` (GZip level 9, base64), in key position;
  - `JsonObject Unpack(JsonObject bundle)`;
  - `string Digest(JsonObject bundle)`;
  - `JsonNode Stable(JsonNode)`.
- `public static class ExplainerPipeline` wraps the internal calls in public signatures. It takes and returns
  `JsonObject` bundles and throws `ArchifyDiagnosticException` or `PeleException`:
  - `Prepare(string sourceText, PrepareOptions o)`;
  - `ReadPrepared(...)`;
  - `AssertDependencies(...)`;
  - `CheckBundleSize(...)`;
  - `string Render(JsonObject bundle, string pageDirectory)`, which is render-explainer.mjs:21:
    `renderExplainer(bundle.document, {...bundle, preparedBundle: bundle, pageDirectory})`;
  - `string Revision(JsonObject bundle)`.

  `PrepareOptions` and `ReadPreparedOptions` become public.
- `public static RenderResult ArchifyNet.RenderExplainer(string sourceText, ExplainerOptions options)` does prepare and
  then render.
  - Any failure comes back as `Error` and `Diagnostics`, through the existing `RendererFailure` mapping: an unclassified
    exception becomes `internal/unclassified`.
  - `Svg` and `Cards` are `""`, and `Layout` is null.
  - `public sealed record ExplainerOptions(string InputPath, QualityProfile? Quality = null, string? RepoRoot = null,
    string WritingMode = "warn", string? PageDirectory = null)`.

- [ ] **Step 1: Seed.**
  - `layout.json` takes the inputs of `explainer-layout.test.mjs`, plus explicit spans, a row that shrinks to minimums,
    a spacer row, weights, a diagram wider than 1000, snippets at 48, 49, 72 and 73 characters, and comparisons with 2
    and 3 data columns (Review Focus 5).
  - The `cases` goldens: the capture runs upstream v3.2.0's `bin/archify.mjs deliver explainer <case>.archify.md
    <case>.html --quality standard` (no `--repo-root`) in a fresh directory holding the draft, for `first-rental`,
    `p99-tail-latency`, `tls-first-visit` and `finalize-delivery`. It tokenises the page and writes
    `tests/goldens/explainer/cases/<case>.html`. The drafts come from
    `git show v3.2.0:docs/cases/explainer/<case>.archify.md`. The test compares
    `ExplainerPipeline.Render(ExplainerPipeline.Prepare(draft, standard quality, no repo root), draft directory)` with
    them through `NormalizePage`. Task 9 Step 3b reuses these goldens.
  - `pages.json` takes `delivery.explainer.json` and the `explainer-renderer.test.mjs` inputs.
  - It also covers:
    - one page per block type;
    - an English page, a zh page and a page with no locale (Decision 3);
    - all four `style` values;
    - typical footnotes;
    - citations in reading order;
    - a metric over its limit, a limit-only metric and a `0.1 + 0.2`-style overflow;
    - comparison assessments with and without a verdict;
    - a checklist;
    - an image;
    - `code` with `repo: true`;
    - a diff;
    - a fence chart and a data chart;
    - guided steps;
    - `changes` marks;
    - `nodeSources`;
    - a scope with ` · ` parts.
  - Any diagram page covers the scoped native CSS (`@media`, `:root`, `[data-theme=dark]` come from the viewer
    template). Keep at most 6 page goldens with diagrams. A tokenised page is about 0.1 MB.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Port** render.mjs, layout.mjs, revision.mjs and page-bundle.mjs. `portableBundle` uses
  `Path.GetRelativePath`, with upstream's cross-volume rule: a relative result that is still absolute becomes `null`.
- [ ] **Step 4: Run.**
  - Expect PASS for `layout`, `cases` and `pages` through `NormalizePage`.
  - Add `Gzip_payload_is_the_stated_deviation`. On one rendered page, each port payload inflates to the child HTML whose
    SHA-256 the manifest names, and its header is GZip with XFL `02` (level 9). It does not assert that the port's
    bytes differ from Node's; nothing guarantees that.
  - Add `Default_locale_is_en_the_stated_deviation`. A page from a draft without `@meta locale` has `lang="en"`, the
    English controls and the English footnote heading. It equals its fixed golden, and Node's golden is kept beside it.
    Every page, layout or patch row whose draft has no locale goes through the same rule.
  - Add `RenderExplainer_returns_diagnostics_not_exceptions`.
  - Run the Core suite.
- [ ] **Step 5: Commit.** `feat(explainer): page rendering and the library facade (PORT-009)`.

### Task 6: the explainer checker and panel patch

**Files:**
- Create: `src/Archify.Net.Core/Explainer/ExplainerCheck.cs` (check.mjs) and `ExplainerPatch.cs` (patch.mjs).
- Modify: `src/Archify.Net.Core/Archify.cs` (`Check` routes explainer HTML) and `ExplainerPipeline.cs` (`Patch`,
  `IsExplainerHtml`).
- Seeds: `check.json`, `patch.json`.

**Interfaces:**
- `internal static bool ExplainerCheck.IsExplainerHtml(string html)`.
- `internal static (List<ScannedElement> Elements, List<ScannedScript> Scripts) ExplainerCheck.Scan(string html)`.
- `internal static JsonObject ExplainerCheck.Check(byte[] html, string file = "<html>", bool checkNative = true)`.
  - Each native child goes through `ArtifactChecker.Check(child bytes)`, with `file` inserted at key index 1, set to a
    named but unwritten `<tmp>\archify-explainer-check-<6 chars>\child.html` (tokenised `<TMP>`).
- `ArchifyNet.Check(byte[])` returns `ExplainerCheck.Check(...)` minus `file` when `IsExplainerHtml` is true, as
  check-render-output.mjs:44-48 does.
- `ExplainerPatch.Prepare(PatchRequest r) → PatchResult(JsonObject Bundle, string SourceText, string SourcePath,
  string PreviousRevision, string PreviousSourceSha256, string PanelId)`.
  - `PatchRequest(string ArtifactPath, string PanelId, string ReplacementPath, string? RepoRoot, string? Quality,
    string? WritingMode, JsonObject? ExpectedArtifact)`.

- [ ] **Step 1: Seed.**
  - `check.json` takes the inputs of `explainer-check.test.mjs` and a clean page, then one tampering per issue code of
    check.mjs:
    - a duplicate id, an `on*` attribute, an external script, an iframe, a non-data image and an unresolved anchor;
    - a raw `<` in a payload, invalid JSON and a payload occurring twice;
    - a bundle digest mismatch, a source hash mismatch, a document hash mismatch, a revision mismatch and a canonical
      id missing;
    - a corrupt native payload, a nested explainer and a native check failure (a child at the other quality profile);
    - a dependency hash mismatch, a duplicate dependency and asset and source bindings.
  - `patch.json` takes the inputs of `explainer-patch.test.mjs`, plus:
    - a Markdown panel and a JSON panel;
    - a CRLF draft (Review Focus 3);
    - a replacement that renames the panel id;
    - a replacement that touches another panel (`scope`);
    - a missing panel and an invalid UTF-8 replacement;
    - an artifact that changed after validation;
    - retained image, data chart and diagram `source=` inputs;
    - a quality change, which re-prepares every diagram;
    - output: the bundle golden plus one page golden.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Port** check.mjs and patch.mjs.
- [ ] **Step 4: Run.** Expect PASS. Then run the Core suite, including `ArtifactCheckerTests`, which is unchanged for
  native pages.
- [ ] **Step 5: Commit.** `feat(explainer): artifact checker and panel patch (PORT-009)`.

### Task 7: CLI — the type, usage, validate, render, deliver, check, doctor, patch

**Files:**
- Modify: `src/Archify.Net.Cli/Cli.cs`:
  - usage: the `patch` line after `deliver`, `, explainer` in Types, and the writing block, as at
    archify.mjs:2192-2225;
  - `patch` dispatch, and `patch` in the `--json` argument-receipt list (archify.mjs:7284).
- Modify: `src/Archify.Net.Cli/Args.cs`: the types list gains `explainer`, and `--writing` is accepted for real.
- Modify: `src/Archify.Net.Cli/Commands/Validate.cs`, `Render.cs`, `Deliver.cs`, `Check.cs` and `Doctor.cs`.
- Create: `src/Archify.Net.Cli/Commands/Patch.cs`.
- Modify: `src/Archify.Net.Cli/Delivery/DeliveryStore.cs`: the provenance `explainer` key and `type: "explainer"`.
- Modify: `tests/Archify.Net.Cli.Tests/CliTestHost.cs`, where `UsageEdit` stops stripping the explainer lines; the four
  unported commands stay.
- Modify: `tools/capture-cli-fixtures.mjs` (an `explainer` group). Test: `tests/Archify.Net.Cli.Tests/ExplainerCommandTests.cs`.

**Interfaces:**
- `ValidateCommand.Run(string[] args, CliIo io, string command = "validate", JsonObject? prepared = null)` and
  `DeliverCommand.Run(string[] args, CliIo io, DeliveryStore? store = null, JsonObject? prepared = null)`.
  - `prepared` is upstream's `ARCHIFY_EXPLAINER_PREPARED`: when it is set, prepareExplainerInput takes the
    `ReadPrepared` branch (archify.mjs:2423-2425).
  - Finalize (Task 8) and patch pass it.
- The explainer branches, by upstream line:
  - prepareExplainerInput: the UTF-8 round trip makes `explainer/source-encoding` (archify.mjs:2416-2426);
  - validate: archify.mjs:7003-7010 and :7073-7095;
  - deliver: :4483-4506, :4859-4873, :4890, the receipt key `explainer: {preparedSha256, dependencies}` at :5017, and
    `AssertDependencies` before publish at :5120;
  - check: the family mismatch at :5636 and `checkerDiagnostics`' explainer arm at :2723;
  - doctor: the explainer row of the type loop and the `Explainer runtime and reader assets` line at :6168-6170, checked
    against the embedded resources;
  - render: render-explainer.mjs, which reads with `'utf8'` and does no UTF-8 check, as upstream (`page` directory =
    the output's directory). A front-matter `PeleException` is the stated deviation of Review Focus 1: the message on
    stderr, exit 1.
- `PatchCommand.Run(string[] args, CliIo io)` follows `commandPatch` (archify.mjs:5304-5350).
  - The original is checked with `ArchifyNet.Check`.
  - `ExplainerPipeline.Patch` builds the bundle.
  - The patched draft goes to a temp `patched-draft.json`, and the bundle is rebound with `inputPath` set to it.
  - `DeliverCommand.Run([... "deliver", "explainer", input, output, "--quality", q, "--writing", w, ("--repo-root", r),
    "--json"], captured io, prepared: bundle)` is called in process.
  - Then the receipt is merged as at :5341 and written as `--json`, a `patched <id> <output>` line, or formatted
    diagnostics.
- CLI fixture masks, added to the existing tokeniser:
  - `explainer.preparedSha256` → `<sha256>`, because the bundle digest covers absolute paths;
  - `<TMP>` for `archify-explainer-*` temp paths;
  - `artifact.sha256` and `artifact.bytes` of an explainer page → `<artifact>`, because of the gzip deviation. The
    delivered HTML is compared through `NormalizePage` with the page the fixture tokenised at mint time, under the
    fixed-golden rule (the capture mints both oracles for a draft without a locale).

- [ ] **Step 1: Mint.** Mint the `explainer` group:
  - every test of `explainer-cli.test.mjs` that runs without an injected environment variable;
  - `validate`, `deliver` and `render` on the four cases;
  - `check` and `check --require-provenance` on a delivered page, and a provenance family mismatch (a native receipt
    beside an explainer page);
  - front matter (Review Focus 1) for `validate`, `deliver`, `render` and `patch`;
  - `--writing warn|strict|off`, the `--writing=off` form, `--writing` with a native type, and `--writing` twice;
  - an invalid-UTF-8 draft for `deliver` (`explainer/source-encoding`) and for `render` (accepted);
  - `patch` success (`--json` and human), `cli/usage`, a target that does not exist, an original artifact that fails
    its check, and `--writing strict`;
  - `doctor`, `--help` and `patch --help`.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Implement** the branches above.
  - A `PeleException` from `ExplainerPipeline.Prepare` reaches each command's existing input-stage catch. The message
    and the `inputDiagnostic` shape (`input/read`, since it is not a `SyntaxError`) are as archify.mjs:2646-2661.
  - In `render` the port writes the exception's message to stderr and exits 1. The `render` front-matter fixture
    compares only the exit code and the first stderr line (Node's is the stack's first line, which names the file;
    compare the `PeleError: …` message part). Add `Render_front_matter_error_is_the_stated_deviation`.
- [ ] **Step 4: Run.** Run the Cli tests with `Category!=Browser`. Expect PASS. `DispatchTests` (usage) and
  `DoctorCommandTests` (row count) change only by the upstream lines.
- [ ] **Step 5: Commit.** `feat(cli): explainer validate, render, deliver, check, doctor and patch (PORT-009)`.

### Task 8: finalize and the explainer browser gate

**Files:**
- Modify: `src/Archify.Net.Cli/Finalize/Finalize.cs` and `Commands/Finalize.cs`.
- Modify: `src/Archify.Net.Cli/Browser/IBrowserInspector.cs`, `PlaywrightInspector.cs` and `BrowserEvidence.cs`.
- Create: `src/Archify.Net.Cli/Browser/ExplainerBrowser.cs` (browser-check.mjs: `inspectExplainerBrowser`,
  `keyboardTraversal`, `inspectNativeViewer`, `explainerObservation`, `explainerObservationDiagnostics`,
  `repairExample`).
- Modify: `tools/capture-cli-fixtures.mjs` (explainer finalize and Browser rows). Test:
  `tests/Archify.Net.Cli.Tests/ExplainerBrowserTests.cs`.

**Interfaces:**
- `IBrowserInspector` gains:
  - `Task<JsonObject> InspectPublicHtmlAsync(PublicHtmlOptions o)`, which is `inspectPublicHtml`;
  - `Task<JsonNode?> EvaluateAsync(string expression)`, which uses the current page;
  - `Task PressAsync(string key)`;
  - `Task<JsonObject> InspectNativeViewerAsync()`;
  - `Task ResetPageAsync()`.
- `PublicHtmlOptions(string ArtifactPath, int Width, int Height, string Theme, bool Javascript = true,
  string Media = "screen", bool Keyboard = true, string? ScreenshotPath = null)`.
- On Playwright:
  - a new page per reset;
  - a context with `JavaScriptEnabled = false` for no-JS runs;
  - `EmulateMediaAsync` (media, `prefers-color-scheme`, `ReducedMotion.Reduce`);
  - `Keyboard.PressAsync("Tab" | "ArrowRight" | "Enter" | "Escape" | " ")`;
  - the srcdoc viewer frame found among `page.Frames` (its parent is the main frame, and its URL starts with
    `about:srcdoc`);
  - console errors from `page.Console` and `page.PageError`.
- `ExplainerBrowser.InspectAsync(IBrowserInspector b, PublicHtmlOptions o) → Task<JsonObject>` orchestrates as
  browser-check.mjs:148-186. The C# code waits with the same loop bounds (30 × 100 ms; the fonts loop up to 5 s).
- In `BrowserEvidence`, for explainer HTML (visual-check.mjs:2466-2482, 2587, 2672-2695):
  - viewports `(1280,900)` and `(1440,900)`;
  - `family`, `viewportPolicy` and `containment.policy`;
  - the five extra receipt groups;
  - explainer diagnostics;
  - `ok` is false when there are any diagnostics.
- In `Finalize` (finalize.mjs):
  - `needsBrowser = type != explainer || --browser-check` (:746), and the not-requested stage record (:795);
  - `validBrowserEvidence`' explainer arm (:147-158);
  - `finalize/browser-family-mismatch` (:305);
  - `--writing` passed to the stages (:516);
  - the diagnostics location mapping (:802-806);
  - `AssertDependencies` around the deliver stage (:849, :898);
  - the prepared bundle handed to the in-process stages through `prepared:` (Task 7). The quality defaults to showcase
    (archify.mjs:5925).

- [ ] **Step 1: Mint.**
  - Without a browser:
    - `finalize explainer` default: 4 gates, `browser-check: not-run`, `browserCheck {requested:false}`;
    - `--browser-check` with Chromium unavailable: skipped, exit 2;
    - a front-matter failure;
    - `--writing strict`.
  - Browser category, upstream driven with `ARCHIFY_CHROME` at the Playwright headless shell, as phase 5 does:
    - `browser-check` on two pages (a case with a diagram and step links; a text page with typical footnotes), and
      `visual-check` on the case only (it is `browser-check` plus screenshots);
    - one page that fails a reader check, built as upstream's test builds it: a rendered page with corrupted CSS
      (explainer-browser.test.mjs:49), applied as a `tamper` replacement;
    - `finalize explainer --browser-check` on a case.
  - Comparison bar (Decision 4): only verdicts. For each receipt that is `ok`, `status`, the `status` of each group
    (`containment`, `readability`, `viewerChrome`, `themeStates`, `commonQa`, `interactions`, `references`,
    `noJavaScript`, `print`) and the ordered list of diagnostic codes. For finalize it is the gates and
    `browserCheck`. No measured field is compared, so no masks are needed. `ExplainerBrowserTests.Verdicts_match_node`
    extracts that projection from both receipts and compares the two.
- [ ] **Step 2: Run.** Expect FAIL.
- [ ] **Step 3: Implement.**
  - First, before anything else: confirm that `page.EvaluateAsync` works in a context with
    `JavaScriptEnabled = false`. The whole no-JS group depends on it. Write it as the first fact,
    `Evaluate_runs_with_page_javascript_disabled`.
  - Port the measurement and observation code literally, since the verdicts derive from it. Measured values that differ
    from upstream's are not compared or chased (Decision 4). A verdict or diagnostic code that differs is a bug to fix.
- [ ] **Step 4: Run.** Run the Cli tests with the Browser category included. Expect PASS.
- [ ] **Step 5: Commit.** `feat(cli): explainer finalize and browser gate (PORT-009)`.

### Task 9: MCP, skill package, docs, the merge gate

**Files:**
- Modify: `src/Archify.Net.Mcp/ArchifyTools.cs`. The type descriptions list `explainer` and accept `.md` drafts.
  `archify_validate` and `archify_finalize` gain `writing?`, and `archify_finalize` gains `browserCheck?`. Its
  description says that an explainer runs the browser check only on request. A new tool:
  `archify_patch(artifact, panelId, replacement, output, quality?, repoRoot?, writing?, cwd?)`.
- Tests: `tests/Archify.Net.Mcp.Tests/McpToolCatalogTests.cs` (5 tools), `McpPipelineToolTests.cs` (explainer render,
  finalize and patch) and `SkillPackageTests.cs` (`ExpectedTools`).
- Modify: `skill/archify/SKILL.md`, re-derived from v3.2.0's `SKILL.md` (the explainer description, the mode rules
  `[archify explainer: …]`, the Output lines). Keep the `## Archify.Net differences` section, and add two lines to it:
  - `patch explainer` maps to `archify patch` or `archify_patch`;
  - pages carry a different gzip byte stream, a reader-invisible deviation.
- Modify: `README.md` (status row, the Charts paragraph, commands table, MCP table), `docs/status.md` (a PORT-009
  section), `docs/deviations.md` (the PORT-009 and PORT-008 note lines become the list below) and `SESSION_HANDOFF.md`.

- [ ] **Step 1: Test first.** Update the catalog and skill tests, and add the MCP explainer rows. Run them and expect FAIL.
- [ ] **Step 2: Implement** the tool changes and the SKILL.md re-derivation. Run the Mcp tests and expect PASS.
- [ ] **Step 3: Docs.** Write the deviations, each with the test that pins it:
  - the gzip payload (Decision 1);
  - the branch lane label (Decision 2);
  - the prepared bundle in process: readPreparedBundle's `prepared-mismatch` reasons (`draft-path-mismatch`,
    `draft-or-bundle-digest-mismatch`, `quality-profile-mismatch`, `writing-mode-mismatch`,
    `normalized-document-mismatch`) need a bundle that does not belong to the draft. Upstream reaches them through a
    hand-made `ARCHIFY_EXPLAINER_PREPARED`; the port reaches them only through `ExplainerPipeline.ReadPrepared`, pinned
    by Core rows in Task 4;
  - native children run in process with no 120 s timeout;
  - the checker `file` paths of children are named but not written;
  - `render explainer` on a front-matter error prints the message and exits 1, where Node prints a raw stack (Task 7);
  - `readContained`/`readSpec` compare size and last-write time, not the file id (Task 4);
  - the default locale is `en` for a draft without `@meta locale` (Decision 3, Martin's product choice). It is a stated
    deviation only, with no upstream issue, pinned by `Default_locale_is_en_the_stated_deviation`;
  - the explainer browser gate is compared with upstream by verdicts, group statuses and diagnostic codes only
    (Decision 4, `Verdicts_match_node`).

  File upstream issues by the Global Constraints rule: Decision 2, plus any fix found in Tasks 2-8 for input someone
  would actually write. There is none for the gzip stream (a platform difference), Decision 3 (a product choice) or
  contrived cases; those get only their `docs/deviations.md` line. Create the parked-items card, and cite it in
  `docs/deviations.md` under "Known leftovers". Put on it upstream's `node archify/bin/archify.mjs` spelling in
  `explainer-authoring.md` (the other references write `node bin/archify.mjs`; Task 1 substitutes both).
- [ ] **Step 3b: Showcase on GitHub Pages (Martin, 2026-10-24).**
  - Our Pages serve the repo root of `master` (https://mbrekhof.github.io/Archify.Net/).
  - Render upstream's four explainer cases with the port's CLI from their `.archify.md` sources:
    `first-rental`, `p99-tail-latency`, `tls-first-visit` and `finalize-delivery`, from
    `git -C C:\projects\archify show v3.2.0:docs/cases/explainer/<case>.archify.md`. Use
    `archify deliver explainer <case>.archify.md <case>.html --quality standard`, with no `--repo-root`. That is how
    upstream's committed pages were made (`bundle.quality` is `standard` in all four, and `finalize-delivery` has no
    excerpts); `finalize` would default to showcase.
  - Commit the drafts and the pages under `showcase/explainer/`, without the `.delivery.json` sidecars.
  - Add `Showcase_pages_match_upstream`: it compares each committed showcase page with the Task 5 `cases` golden
    (minted from v3.2.0) through `NormalizePage`. Do not compare with upstream's committed
    `docs/cases/explainer/*.html`: those pages came from an older build.
  - Record that finding in `docs/status.md` and README. Each committed native child carries
    `<meta name="generator" content="archify 3.1.1-dev.1">` where v3.2.0 writes `archify 3.2.0`; every other child byte
    is equal. So the children's SHA-256, the manifest, the bundle digest and `data-revision` differ from a v3.2.0
    render.
  - Add a short `showcase/explainer/index.html` that links the four pages. Link it from `README.md`, next to
    upstream's live copies at https://tt-a1i.github.io/archify/cases/explainer/.
  - After the push, check each URL with `curl -s -o /dev/null -w "%{http_code}"` and expect 200 once Pages has built.
- [ ] **Step 4: Run.**
  - Run the full `dotnet test` at the repo root, with the Browser category. Expect 0 failed and 0 warnings; the only
    skip is the PORT-008 `Dev` math test.
  - Run both captures once more. Expect `git status --porcelain tests/` to be empty.
  - Record the totals in README, status and handoff.
- [ ] **Step 5: Commit.** `docs: PORT-009 explainer (PORT-009)`. Then the Fable whole-branch review, settled; then
  fast-forward master and push. Do not deploy.

---

## Decisions (Martin, 2026-10-24)

Martin answered "1 b, 2 b, 3 b, 4 b". The tasks above are written to these answers.

1. **The page's gzip payload: (b).**
   - Pages use .NET `GZipStream` at level 9. The tests compare the inflated payloads and the rest of the page byte for
     byte. It is a stated deviation with no upstream issue (a platform difference).
   - Why: Node's zlib is Chromium's fork (`1.3.1-470d3a2`). Its deflate stream is reproducible across machines up to
     the header's OS byte: upstream's committed macOS payloads equal Windows Node 24's from byte 10 on. .NET's zlib-ng
     emits a different, slightly larger stream (207,794 against 206,599 bytes for `first-rental`'s child), and both
     inflate to the same bytes. No digest covers the payload, and `patch`, `check` and the reader all inflate it.
   - Rejected: (a), porting Chromium's deflate (about 2k lines), and (c), the unpacked form (pages about 3× larger).
2. **The workflow shorthand lane label: (b).**
   - A page whose locale does not start with `zh` gets `Branch N` where upstream writes `分支 N` (diagrams.mjs:270). A
     draft without a locale counts as `en` (Decision 3).
   - Keep the Node golden, and compare the port with the fixed golden.
   - Pinned by `Branch_lane_label_is_the_stated_deviation` (Task 2). Filed upstream (Task 9).
3. **The default locale: (b), `en`.**
   - A draft without `@meta locale` renders as `en` where upstream defaults to `zh-CN` (render.mjs:38). This is Martin's
     product choice: a stated deviation, with no upstream issue.
   - Every row whose draft has no locale is compared with its fixed golden through the one comparer rule (Global
     Constraints, Fixed goldens). The Node goldens are kept.
   - Pinned by `Default_locale_is_en_the_stated_deviation` (Task 5).
   - The four showcase drafts all set a locale, so the showcase is unaffected.
4. **The bar for the explainer browser gate: (b).**
   - Only verdicts, group statuses and diagnostic codes are compared with upstream's receipts
     (`Verdicts_match_node`, Task 8).
   - Full-receipt equality and the driver-field fallback are dropped.

No decision was needed on image export or PDF: both live in the verbatim `reader.js`. No new dependency is needed.

---

## Self-review notes (Claude, 2026-10-24)

- **Coverage.**
  - Panels and block types: Tasks 2, 3 and 5.
  - The four styles in light and dark: Task 5 (`pages`), and Task 8 (the browser themes).
  - Export and print: the verbatim reader, pinned by `Reader_assets_are_embedded_verbatim` and by the Browser-category
    print and no-JS groups.
  - `finalize explainer`: Task 8. `patch explainer`: Tasks 6 and 7.
  - CLI: Tasks 7 and 8. MCP and skill: Task 9. The PORT-009 note on front matter: Review Focus 1, Tasks 3, 7 and 8.
- **Ordering.** Each task's groups depend only on earlier tasks: the parser calls the shorthand (Task 2), validate needs
  inline, diff and writing (Task 3), prepare needs both, and render needs prepare. The CLI waits for Core.
- **Size.**
  - About 8k C# lines: Core about 5k, CLI about 1.3k, MCP about 0.1k, tests about 1.5k.
  - About 550 Node-minted rows in 12 seed groups, about 30 page goldens (4 cases plus about 26 pages; tokenised, about
    3 MB), about 50 CLI fixtures and about 4 browser receipts.
- **Plan review (Fable, 2026-10-24): ready after fixes, 0 Blocker, 3 Major, 10 Minor**
  (`.superpowers/sdd/2026-10-24-port009-explainer/plan-review-fable.md`). All of them, and every listed cut, are folded
  into the tasks above:
  - M1: case goldens minted from v3.2.0 by `deliver --quality standard`, and the showcase compared with them; the
    generator-version finding goes to status and README.
  - M2: `render` front matter is a stated deviation.
  - M3: child HTML and gzip payloads are tokenised at mint time.
  - The minors, in place: m1 the Decision 1 rationale, m3 the V8 message kinds, m4 size + mtime + reparse point, m5
    the seed shapes, m6 `--writing=off`, m7 the layout boundaries, m8 `InlineContext`, m9 the paths, m10 no-JS
    evaluate first.
  - 9 tasks. Implementers on opus; Task 9 on sonnet.
