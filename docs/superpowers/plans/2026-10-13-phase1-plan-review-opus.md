# Phase 1 plan review (Opus), 2026-10-13

Plan under review: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md` (cited as `plan:<line>`).
Upstream: `C:\projects\archify` @ `c21c31ff` (`UP/` = `archify/`). V8 = Node v24.12.0. .NET SDK 10.0.401 / runtime 10.0.12.
JsonSchema.Net 9.4.0 was probed by loading the cached DLLs into `dotnet fsi` and evaluating the real upstream schemas. ajv was probed through
`UP/renderers/shared/generated-validators.mjs`. Every claim below comes from a probe or from file:line. None comes from memory.

Severity: **blocker** = a task cannot finish as written, or it produces wrong bytes and its own test does not catch them. **should-fix** = a wrong or
misleading detail that will cost a debugging round, or a decision that is still open. **note** = minor.

---

## Blockers

### B1. Task 2 capture script crashes: segment helpers take `{start, end}`, not `[p1, p2]`
- **Plan:** `plan:394-395` calls `g.segmentIntersectsRect(s, r)` and `g.segmentRectClearance(s, r)` with `s = [[0,0],[300,60]]`. `plan:1119` states "`segment = [p1, p2]`". The Task 7 C# test passes a tuple `Seg(...)`.
- **Evidence:** `geometry.mjs:45-56` does `const [a, b] = [segment.start, segment.end]; if (pointInBox(a, box) ...`, so with an array `a` is `undefined` and the call throws a TypeError. `geometry.mjs:62-66` (`segmentRectClearance`) returns `null` for array input (`!Array.isArray(start)`). The Task 7 C# test would then call `.D()` on null. Every upstream caller passes objects, for example `routing.mjs:96` `segmentIntersectsRect({ start: points[index], end: points[index + 1] }, ...)`.
- **Fix:** in the capture script use `const segs = [...].map(([start, end]) => ({ start, end }))`. In C#, define `readonly record struct Segment(Pt Start, Pt End)` (Task 7 Produces) and have `GeometryTests.Seg` read `n["start"]` / `n["end"]`. Correct the `plan:1119` sentence.

### B2. Task 5 golden-reassembly test cannot pass: rendered goldens contain no slot markers
- **Plan:** `plan:935-953` cuts `SvgSlot`/`CardsSlot` out of `examples/web-app-rendered.html` and asserts that both are non-empty.
- **Evidence:** `grep -c "ARCHIFY:SVG_SLOT\|ARCHIFY:CARDS_SLOT"` returns `examples/web-app-rendered.html:0` and `assets/template.html:4`. `applyTemplate` replaces the whole marker block with the SVG (`utils.mjs:196-197`), and `renderSvg()` starts at `      <svg viewBox=...` with no marker (`render-architecture.mjs:1150`). The golden's SVG starts at line 5162.
- **Fix:** keep the oracle idea and change the extraction:
  1. Build `cards` from the input, since they need no extraction: `Utils.RenderCards(diagram["cards"])`.
  2. Apply the template once with sentinel strings, for example `"\u0001SVG\u0001"` and `"\u0001CARDS\u0001"`.
  3. Split that result at the sentinels into `prefix | mid | suffix`.
  4. Assert `golden.StartsWith(prefix)`, `golden.EndsWith(suffix)`, and that the text between the SVG and the cards equals `mid`.
  5. Take `svg = golden[prefix.Length .. golden.IndexOf(mid, prefix.Length)]`, then assert `Apply(..., svg, cards, ...) == golden`.

### B3. Task 1 Step 2: `node -e` with Git Bash `/c/...` paths gets ENOENT on Windows
- **Plan:** `plan:133-134` `readFileSync('/c/projects/archify/archify/renderers/shared/generated-brand-marks.mjs')`.
- **Evidence:** probe `node -e "require('fs').readFileSync('/c/projects/archify/archify/package.json')"` fails with `ENOENT C:\c\projects\archify\archify\package.json`. Node does not translate Git Bash paths. This is the same trap the global CLAUDE.md records for Python.
- **Fix:** use `C:/projects/archify/archify/renderers/shared/generated-brand-marks.mjs` inside both `node -e` strings. With that path the extraction itself works: `ok 107`, keys `id,title,category,aliases,domains,viewBox,hex,path,provenance`, and `aliyun` resolves to `alibaba-cloud`.

### B4. Brand marks keyed by node reference never resolve inside the architecture renderer
- **Plan:** `plan:1140` `ResolvedBrand? BrandMarkFor(JsonObject node)` is keyed by node reference (`JsMap<JsonObject, ResolvedBrand>`).
- **Evidence:** the renderer measures components by copying them: `render-architecture.mjs:95` `return { ...c, x, y, width: w, height: h, cx, cy }` and `:98` `new Map(asArray(arch.components).map((c) => [c.id, measureComponent(c)]))`. Brand helpers are then called with that copy: `:582` `brandTopRailProblem(c, ...)`, `:1095-1097` `renderBrandMark(c, ...)`, `brandLabelFitWidth(c, ...)`, `brandMetadataFor(c)`. Upstream still works because `prepareDiagramBrandMarks` stores the mark as an own enumerable symbol property (`brand-marks.mjs:503,511` `node[RESOLVED_MARK] = resolved`), object spread copies it, and `brandMarkFor` reads `node?.[RESOLVED_MARK]` first (`:536`). A reference-keyed C# map misses on every measured component, so `brand-aware-delivery` renders with no brand marks.
- **Fix:** have the C# measured-component type carry `ResolvedBrand? Brand`, filled from `ctx.Brands.BrandMarkFor(sourceNode)` inside `MeasureComponent`. Make the `BrandMarks.*` render helpers take the `ResolvedBrand?` rather than looking it up by reference. State in Task 7 and Task 9 that every upstream `{...node}` copy must carry the brand forward.

### B5. Task 6 schema-validation design does not work on JsonSchema.Net 9.4.0
All of the following are probe results against the real schemas.
- **(a) Length keywords cannot be fixed by post-filtering.** `plan:990` says to "post-filter JsonSchema.Net length errors and recompute". Probe: `components[0].brand` set to 17 copies of the family emoji (85 code points, 17 graphemes) gives JsonSchema.Net `valid=true`. The `maxLength: 80` branch of `anyOf` passed on its grapheme count, so no error exists to filter. ajv gives `maxLength, pattern, anyOf, type, oneOf` at `/components/0/brand`. Inside `anyOf`/`oneOf`/`not` the combinator result is already decided by then.
- **(b) `$` divergence, a fourth regex case missing from spec and plan.** JsonSchema.Net compiles `pattern` with `RegexOptions.Compiled | ECMAScript` (probe on `PatternKeyword.ValidateKeywordValue`). .NET `$` still matches before a trailing `\n`. Probe: `components[0].id = "abc\n"` gives JsonSchema.Net `valid=true`, while upstream gives `schema/pattern@/components/0/id`. Every `^…$` pattern is affected: `id`, `locale`, translations `propertyNames`, `sha256`, brand `^[^\r\n]+$`.
- **(c) Surrogate class rejects valid emoji.** Probe: `meta.output = "out/😀.html"` gives JsonSchema.Net `valid=false`, key `not` at `/allOf/2` (`[\uD800-\uDFFF]` matches each half of the pair). Upstream accepts it (`emoji out: OK`). The plan's `Emoji_in_output_path…` test (`plan:1039-1045`) only asserts "no `schema/pattern`", so it passes over this divergence.
- **(d) Valid documents report errors in List output.** For every valid example, List output contains 7 `key=pattern` entries at `/meta/output` (`…/allOf/1..7/not`). These are the inner failures of passing `not` subschemas. Mapping every `Errors` entry to a diagnostic would make all 25 examples fail.
- **(e) The keyword is the `Errors` dictionary key, not the last `EvaluationPath` segment.** `plan:1052` says the opposite. Probe: `size [0,60]` gives `eval=…/size/prefixItems/0 key=exclusiveMinimum`.
- **(f) API mismatches.**
  - `JsonSchema.Evaluate` takes a `JsonElement`. The only overload is `Evaluate(JsonElement, EvaluationOptions)`.
  - `EvaluationOptions` has no `SchemaRegistry` property. The registry lives on `BuildOptions.SchemaRegistry`.
  - There is no `GetId()`.
  - `JsonSchema.FromText(text, new BuildOptions { SchemaRegistry = reg })` registers by `$id` automatically, resolves `$ref` lazily (building `architecture` before `common` works), and keeps instance registries isolated (`Global` stays empty).
  - JsonSchema.Net emits applicator errors (`properties`, `items`, `prefixItems`) and empty-key false-schema entries that ajv does not. Examples:
    - `additionalProperties` produces an extra `inst=/bogus key=""`.
    - `items:false` reports `/size/0` where ajv reports `items@/components/0/size`.
    - `propertyNames` reports `/meta/translations/Bad.key` where ajv reports `pattern` + `propertyNames` at `/meta/translations`.
- **Fix:** rewrite Task 6 Step 3 around the following.
  - **(i) Replace the built-in keyword handlers.** Use a `Dialect` derived from `Dialect.Draft202012` with `pattern`, `minLength` and `maxLength` replaced by custom `IKeywordHandler`s (the interface is public: `Name`, `ValidateKeywordValue`, `BuildSubschemas`, `Evaluate`).
    - `minLength`/`maxLength` count code points.
    - `pattern` translates JS `u`-flag source to .NET: `$` outside a class becomes `(?!\n)$` or `\z`, `[\uD800-\uDFFF]` becomes a lone-surrogate check, `\d` is ASCII (already true under ECMAScript).
    - Then `not`/`anyOf`/`oneOf` decide correctly. Drop the separate "pre-check" layer.
  - **(ii) Read results with `OutputFormat.Hierarchical`.** Walk the tree and descend only into nodes with `IsValid == false`. Set `IncludeApplicatorErrors = false`, drop empty-key entries, and map the cases where ajv reports at the parent instance (`items:false`, `propertyNames`).
  - **(iii) Add an ajv oracle.** Task 2 should capture `validate.errors.map(e => [e.keyword, e.instancePath])` for a battery of mutations (the 8 ValidationTests cases plus brand-85cp, `abc\n` id, emoji output, `items:false`, `propertyNames`, `oneOf` brand object, `not` output). Task 6 then asserts set equality. The spec's parity bar is "the set of `(code, instancePath)` pairs". No task currently tests that.
  - **(iv) Mark (i) and (ii) as unverified until that oracle test is green.** Dialect replacement was not exercised end to end in this review.

### B6. STJ replaces lone surrogates with U+FFFD: Task 3 Stringify test fails, loader diverges
- **Plan:** `plan:383-385` stringify sample `'\ud800 lone'`; `Js.Quote` lone-surrogate branch (`plan:735-737`); `DiagramLoader.Parse` built on `JsonNode.Parse` (`plan:854`).
- **Evidence:** probe `JsonNode.Parse("{\"a\":\"\\ud800x\"}")["a"].GetValue<string>()[0]` is `0xFFFD`, and `\udc00` gives `0xFFFD` too. `SerializeToElement` gives `"\uFFFDx"`. V8 `JSON.parse` keeps `0xd800`. Two consequences:
  - The fixture `input` reaches C# as `"\uFFFD lone"`, so `Js.Stringify` prints a literal U+FFFD and the expected `"\ud800 lone"` fails.
  - Upstream rejects a lone surrogate in `meta.output` (probe: `output/meta-path-syntax`), but C# would see U+FFFD and accept it.
- **Fix:** `DiagramLoader.Parse` walks `Utf8JsonReader` itself. For a string token with `ValueIsEscaped`, it unescapes `reader.ValueSpan` by hand, keeping unpaired `\uD800-\uDFFF` as raw `char`s. Build the `JsonObject` from that, and handle duplicates there too (see S4). Load the Task 3 `stringify.json` and `strings.json` fixtures through `DiagramLoader.Parse`, not `JsonNode.Parse`. Add one loader test asserting `"\ud800"` survives and is rejected for `meta.output` with `output/meta-path-syntax`.

### B7. `Js.ToLocaleStringEnUs` rounds the wrong digits and drops the `-0` sign
- **Plan:** `plan:640-668`. It rounds the exact binary value (`ScaledHalfUp`) and returns `neg && n != 0 ? "-" + … : …`. The Step 4 hint (`plan:752`) says "adjust rounding mode".
- **Evidence (V8):** Intl rounds the shortest round-trip decimal, not the exact binary value.

  | Input | V8 | Plan |
  |---|---|---|
  | `1.005` with `{maximumSignificantDigits:3}` | `"1.01"` | `"1"` |
  | `1.005` with `{maximumFractionDigits:2}` | `"1.01"` | `"1"` |
  | `2.345` frac2 | `"2.35"` | |
  | `0.125` frac2 | `"0.13"` | |
  | `2**60` frac2 | `"1,152,921,504,606,847,000"` (exact value is …846,976) | |
  | `1.7976931348623157e308` frac2 | `"179,769,313,486,231,570,000,000,…"` | |
  | `-0` sig3 and frac2 | `"-0"` | |
  | `-0.004` frac2 | `"-0"` | |

  The fixture (`plan:369`) contains `1.005`, `-0` and `1.7976931348623157e308`, so `ToLocaleString_enUS_matches_V8` fails, and the hint sends the implementer to the wrong knob.
- **Fix:**
  1. Start from `ShortestDigits(|v|)` (digits, n).
  2. Round that decimal string half-expand to `sig` significant digits, or to `f` fraction digits.
  3. Pad with zeros to the integer position.
  4. Strip trailing fraction zeros and group with `,`.
  5. Prefix `-` whenever `double.IsNegative(v)` (this covers -0).

  Replace the Step 4 hint with "the digit source is the shortest decimal, not the exact double".

### B8. Four compile errors under `TreatWarningsAsErrors`
- `plan:234` `throw new UnreachableException()`: `System.Diagnostics` is not among the implicit usings (CS0246). Add `using System.Diagnostics;` or use `ArgumentOutOfRangeException`.
- `plan:624` `var neg = …` is never read: CS0219 becomes an error. Delete the line.
- `plan:1140` `record ResolvedBrand(BrandMark Mark, string Kind = "preset", string Status = "preset", string SourceUrl)`: an optional parameter comes before a required one (CS1737). Move `SourceUrl` second.
- `plan:902` `Assert.Equal(1, r.Override.PlaceholderMismatches.Count)`: xunit.analyzers xUnit2013 is a warning, so the build fails. Use `Assert.Single(...)`.

### B9. Task 4 cannot compile: it references types from Tasks 5, 7 and 8
- **Plan:** `plan:801` gives `RenderContext` a `Locale` (`LocaleCatalog`, Task 5) and `Brands` (`BrandMarkResolver`, Task 8). `plan:803` gives `RenderResult` a `LayoutReport? Layout` ("Task 7"). Task 5's Files list does not say it modifies `RenderContext.cs`, although Task 6 uses `ctx.Locale.Register`.
- **Fix:** Task 4 creates `RenderContext` without `Locale`/`Brands` and declares `public sealed class LayoutReport { }` as a stub. Task 5 adds "Modify `RenderContext.cs`: add `Locale`". Task 7 fills `LayoutReport`. Task 8 already adds `Brands`.

### B10. Two Task 6 and Task 5 test expectations contradict upstream
- **`MinLength_counts_code_points_not_graphemes`** (`plan:1033-1038`) expects a `schema/*` code for `meta.output = "x/" + 256×'a' + ".html"`. Upstream runs `validateAuthoredOutputPath` before the schema (`cli.mjs:79-81`), and portable-path rejects segments over 255 UTF-16 units (`portable-path.mjs:196`). Probe: `output/meta-path-syntax@/meta/output`. The test fails.
  - Fix: assert `output/meta-path-syntax`. Move code-point length coverage to a field that only the schema checks, such as the 85-code-point brand string from B5, expecting `{maxLength, pattern, anyOf, type, oneOf}` at `/components/0/brand`.
  - Note that the spec's `[^/]{256}` and `[\uD800-\uDFFF]` cases on `meta.output` can never be reached through `Load`, because portable-path fires first.
- **`Emoji_in_output_path…`** must assert that `Load` succeeds with no diagnostics (upstream: OK).
- **`Translations_override_then_bundled_then_en`** (`plan:900`): `es.json` `legend.architecture.frontend` is `"Frontend"`, the same as `en`, so the `NotEqual` assertion fails. Use `legend.architecture.database` (`"Base de datos"`).

---

## Should-fix

### S1. `Js.Sort` takes `Comparison<T>` (int), but upstream comparators return fractional doubles
- **Plan:** `plan:460`, `plan:688`.
- **Evidence:** `routing.mjs:908-922` returns `leftDistance - rightDistance` over Manhattan distances of fractional coordinates. The same shape appears at `routing.mjs:1019-1029`, `routing.mjs:830` (`Math.abs(a - preferred) - …`), `render-architecture.mjs:303-309` (area delta) and `automatic-labels.mjs:93`. Truncating a delta such as `0.5` to `(int)0` makes the elements compare equal, which changes route order and therefore the SVG. `geometry.mjs:1741-1743` compares strings with `<`/`>`, which is UTF-16 ordinal.
- **Fix:** use `Js.Sort<T>(List<T>, Func<T,T,double> cmp)` with V8 semantics: `NaN` counts as `0`, otherwise `Math.Sign`. Add a rule that JS string `<`/`>` is `string.CompareOrdinal`. Add a fixture case with fractional comparator results.

### S2. `Js.Hypot` is not V8's algorithm
- **Plan:** `plan:676-686` accumulates the compensation and returns `sqrt(sum - comp) * max`.
- **Evidence:** a simulation of the plan's code against `Math.hypot` over 200 000 random 2-decimal pairs differed in 2 275 cases, for example `(168.75, 880.93)`: plan `896.9471709080753`, V8 `896.9471709080751`. V8's loop (`summand = n*n - compensation; preliminary = sum + summand; compensation = (preliminary - sum) - summand; sum = preliminary`, then `sqrt(sum) * max`) reduces for 2 arguments to `Math.Sqrt(x*x + y*y) * max`, with `x = |a|/max` and `y = |b|/max`. That form differed in 0 of 200 000 cases. It feeds `roundedPath` `Q` coordinates (`geometry.mjs:1849-1857`).
- **Fix:** implement that form: Infinity first, then NaN, then `max == 0 → 0`. Add `hypot` pairs (diagonal, fractional) to `numbers.json`. No current fixture tests `Js.Hypot` directly, which breaks the Global Constraint that every `Js` member has a fixture-backed test.

### S3. `Js.ToFixed` drops the sign of a negative value that rounds to zero
- **Evidence:** `plan:628` `v < 0 && n != 0 ? "-" + s : s`. V8 `(-0.001).toFixed(2)` is `"-0.00"` and `(-0).toFixed(2)` is `"0.00"`. The plan's own comment on the same line says the sign is kept.
- **Fix:** `v < 0 ? "-" + s : s`. Add `-0.001` and `-0.5` to the numbers fixture, because nothing negative and small is there now, so the bug is silent.

### S4. Loader and number normalisation details are wrong as written
- `JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = true })` still throws `ArgumentException: An item with the same key has already been added` when the object is accessed (probe on 10.0.12). The fallback is therefore mandatory. Fold it into the B6 reader.
- `plan:854` says `JsonValue.Create(node.GetValue<double>())`. Probe: `JsonValue.Create(3).GetValue<double>()` throws `A value of type 'System.Int32' cannot be converted to a 'System.Double'`. `Normalize` and `J.D` must switch on the boxed CLR type (`int`, `long`, `decimal`, `double`, `float`, `JsonElement`). Callers that build `JsonObject`s in C# (Task 7 `LabelPoint` test `["labelDx"] = 3`, Task 6 `new JsonArray(0, 60)`) hit this path.

### S5. Quality-profile precedence differs per call site
- **Plan:** `plan:872` applies "precedence `ctx.Options.Quality` > `meta.quality_profile`" generally.
- **Evidence:**
  - Env (= option) wins over meta only in `svgRootAttrs` (`cli.mjs:367`) and in the arrowhead-collision gate (`render-architecture.mjs:904`).
  - Every `clean*Problems({ profile: arch.meta?.quality_profile })` call reads **meta only** (`render-architecture.mjs:884, 898, 929, 938, 949`), and so does `enforcesBoundaryTitleComposition = Boolean(arch.meta?.quality_profile)` (`:99`).
- **Fix:** list the two option-aware sites explicitly and port every other `profile:` argument literally from meta. Add a test: no-profile input with `Quality: Showcase` must still pass layout gates exactly as upstream with `ARCHIFY_QUALITY_PROFILE=showcase`.

### S6. `validateAuthoredOutputPath` needs `path-semantics`; the plan cuts it and contradicts itself
- **Evidence:** `output-path.mjs:323-365` calls `canonicalFuturePath` (which uses `resolvePhysicalLocation` from `path-semantics.mjs`, 1 017 lines, with real symlink resolution) and `pathIsInside` (`containedBy`), both relative to `cwd`. `plan:989` says "port now", and the Task 6 Step 3 text says "port it there [phase 5], not now". The spec's phase-1 list includes `path-semantics` and `sidecar-path`. Neither appears in the plan's File Structure.
- **Fix, a decision for the lead:** pick one of the two options below and amend the spec to match.
  - **Option 1 (recommended): library uses a lexical check.** `Archify.Validate`/`Render` run portable-path, the `.html` extension check and a lexical containment check. Lexical containment cannot fail after portable-path has rejected `..` and absolute paths. They do no filesystem resolution and take no `cwd`. `path-semantics`, `resolveOutputPath` and `sidecar-path` move to phase 5 with the CLI.
  - **Option 2:** port `path-semantics` now and state which `cwd` the library uses.

### S7. i18n details wrong in Tasks 5 and 6
- `localeDiagnostics` codes are `i18n/invalid-translation`, `i18n/locale-fallback` and `i18n/translation-coverage` (`i18n.mjs:241, 257, 271`), not `locale/…` as `plan:972` says. Add `i18n/` to the code families in the Global Constraints.
- `placeholderMismatches` entries are objects `{ key, expected: string[] (default-sorted), actual: string[] | null }` (`i18n.mjs:98, 105`), not `IReadOnlyList<string>`. They go into diagnostic evidence.
- Upstream registers the locale only when `meta.locale` is truthy (`cli.mjs:33-35`). Task 6 `Load` must keep that guard.

### S8. Router and geometry types are underspecified and mismatch upstream
- `createRouter(components, connections, opts)` takes a `Map<string, {x, y, width, height, cx, cy}>` (`routing.mjs:75`, used as `components.get(id)`). `plan:1191` says `IReadOnlyList<Component>`.
- No task defines `Component`.
- `Grid.GridLayout(JsonObject)` (method) and `GridLayout` (type) inside `Grid` collide.
- `Rect(X, Y, Width, Height)` loses `cx`/`cy` and the payload fields that upstream rects carry (label rects carry `relation`, for example).
- **Fix:** define in Task 7 one `Box` record (x, y, width, height, cx, cy, id, plus the brand from B4) and `JsMap<string, Box>` as the router input. Name the grid type `GridSpec`.

### S9. `FindBrandMark` behaviour is incompletely specified
- **Evidence:** `brand-marks.mjs:27-41, 63-79`.
  - Lookup keys come from id, **title** and aliases. Each key yields three forms (raw `toLocaleLowerCase('en-US')`, `[\s_]+`→`-`, `[\s_.-]+`→``). The first mark in catalog order wins.
  - An HTTP(S) value resolves by **domain**, with the longest suffix winning.
  - `\s` here is JS whitespace, so use `Js.IsSpace`, not .NET `\s`.
- **Fix:** paste these rules into Task 8.

### S10. Review Focus 5 contradicts upstream and Task 8
- **Plan:** `plan:35` says "render still succeeds for built-in marks".
- **Evidence:** any entry in `unknown` makes `prepareDiagramBrandMarks` call `throwDiagnosticError` (`brand-marks.mjs:514-527`), so the render fails. Task 8's test correctly expects the throw. The probe confirms upstream behaviour: exit 1, a stderr JSON receipt with `brand/capture-unavailable`, and a fast failure on `getaddrinfo ENOTFOUND example.invalid`.
- **Fix:** reword to "render fails with `brand/capture-unavailable`; no network call".

### S11. JsonSchema.Net 9.4.0 ships under a maintenance-fee EULA (needs Martin's decision)
- **Evidence:** `jsonschema.net.nuspec` has `<license type="file">OSMFEULA.txt</license>` and `requireLicenseAcceptance=true`. The EULA requires a monthly fee from revenue-generating users with annual gross revenue of US$10 000 or more who use the **binary release**. The source stays MIT; self-compiling is exempt. The DLL also references `Json.More` 3.0.0 transitively.
- **Why it matters:** the spec names XAF, duetGPT and ContextBoard as consumers.
- **Decision for Martin:** accept the fee, build json-everything from source, or validate without it. B5 already requires custom keyword handlers, which shrinks the share of the library the port actually uses.

### S12. The checker port (Task 10) lacks the JS parsing and regex rules its byte-compared report needs
- **Evidence:** `check-render-output.mjs` relies on:
  - `Number(...)` at `:595-596, 844, 1050, 1064`;
  - `Number.parseFloat(...)` at `:1172, 1214, 1246` (prefix parse: `"12px"` → 12, `""` → NaN);
  - `/gi` regexes with `\b` at `:108, 666-669, 902-1220`.

  .NET `double.Parse` differs from both JS parsers, and .NET `IgnoreCase`/`\b` are culture- and Unicode-aware.
- **Fix:** add `Js.ToNumber(string)` (JS trim, `""`→0, hex/`Infinity`) and `Js.ParseFloat(string)` with fixtures. Add a rule that ported regexes use `RegexOptions.ECMAScript | RegexOptions.CultureInvariant` (or explicit ASCII classes) and that a JS `$` without `m` translates to `\z`-style end of input.

### S13. `.gitattributes` order makes `upstream/**/*.md` text, so CRLF on checkout
- **Evidence:** gitattributes uses last-match-wins. `*.md text` (`plan:121`) comes after `upstream/** -text` (`plan:116`). `core.autocrlf=true` is set system-wide on this machine (`C:/Program Files/Git/etc/gitconfig`). `upstream/references/*.md`, `viewer/README.md` and `brand-marks/README.md` would therefore materialise as CRLF.
- **Fix:** put the `upstream/**`, `tests/goldens/**` and `tests/fixtures/**` `-text` lines **last**.

### S14. `dotnet new sln -n Archify` creates `Archify.slnx`
- **Evidence:** `dotnet new sln --help` on SDK 10.0.401 reports `Default: slnx`. The File Structure says `Archify.Net.sln`.
- **Fix:** `dotnet new sln -n Archify.Net --format sln`, or adopt `.slnx` and change the docs.

### S15. Precondition missing: repo rename and branch
- **Evidence:** the plan hard-codes `/c/projects/Archify.Net`, which does not exist. The repo is `C:\projects\arc#hify` on branch `master`.
- **Fix:** add a Step 0 to Task 1: rename the folder (or state the path), then branch per global rule. The `#` in the old path also breaks file-URI tooling.

### S16. Task 2 renderer spawns inherit `ARCHIFY_*` environment variables
- **Evidence:** `execFileSync(..., { stdio: 'inherit' })` (`plan:404`) passes the developer's environment through. Upstream's CLI always builds the renderer environment explicitly (`bin/archify.mjs:2391-2397` `rendererEnv`). A stray `ARCHIFY_QUALITY_PROFILE` or `ARCHIFY_DIAGNOSTIC_FORMAT` changes a golden.
- **Fix:** pass `env: { ...cleanEnv }` with `ARCHIFY_QUALITY_PROFILE`, `ARCHIFY_REPO_ROOT` and `ARCHIFY_DIAGNOSTIC_FORMAT` deleted. For the remote-brand run, set only `ARCHIFY_DIAGNOSTIC_FORMAT=json`. Give the checker spawn `maxBuffer: 64 * 1024 * 1024` as upstream does (`archify.mjs:2251`). The current receipts are about 3 KB, so this is cheap insurance.

### S17. Phase-1 `Js` scope breaks its own fixture rule
- `Js.Graphemes` and `Js.WordBreaks` have no phase-1 fixture and no phase-1 caller in architecture.
- `Js.Hypot` has no fixture (S2).
- **Fix:** move `Graphemes`/`WordBreaks` to phase 2, where the spec puts their fixtures. Add a hypot fixture now.

---

## Notes

- **N1.** `examples/` has **25** diagram JSONs, not 24 (`plan:1054` says "24 example theories"). The spec repeats both counts ("24 … 15 with goldens", "9 JSONs without golden"), so it needs the same fix: 25, 15, 10.
- **N2.** Task 2's Files list omits `tests/fixtures/js/sigils.json` and `tests/fixtures/diagrams/remote-brand.expected.json`. Step 2 counts 7 js fixtures, which includes sigils.
- **N3.** Name the checker report keys explicitly: `ok, file, artifact {sha256, bytes}, checks[], composition`, plus a conditional `diagnostics` (`check-render-output.mjs:441-444`). All 15 upstream goldens check `ok`, rc=0, 9 checks, about 3 KB.
- **N4.** `compositionFixes` reads a `COMPOSITION_FIXES` table (`bin/archify.mjs:2727-2730`). Copy it alongside `COMPOSITION_CHECKS` and `CHECK_FIXES`.
- **N5.** `focusEdgeAttrs(…, key, id)` receives the numeric `index` (`render-architecture.mjs:1067, 1077`), so format it with `Js.Num`. `animateAttr`'s step can be `undefined` (`componentSteps.get`) and then becomes `0`. The `int step` signature works only if callers map a missing value to 0.
- **N6.** The spec's `Diagnostic(Code, Message, Path, Severity)` differs from the plan's richer record. Update the spec.
- **N7.** `JsMap.Set` on an existing key must update the value in place without moving it (Map semantics). This matters for `new Map(components)` with duplicate ids.
- **N8.** The stringify fixture's `[1.0, 1e2, -0]` and its integer keys are already normalised by `JSON.stringify` when the fixture is written, so they test nothing. `sort.json` `entriesSorted` is captured but no test consumes it.
- **N9.** `RenderContext.TextUnitsCache` has no observable effect (YAGNI). `RenderOptions.DiagnosticFormat` has no effect in the library, because recording is always on.
- **N10.** The commit trailer `Claude Fable 5.1` is hard-coded. It should match whichever model executes the task.
- **N11.** Upstream builds the layout report only in `--layout-json` mode, and in that mode it also emits it on failure (`render-architecture.mjs:1178-1192`). Returning `Layout` on every success is fine. A failed render returns `Layout = null`, which differs from upstream's partial report. That is acceptable, but state it.

---

## Byte-parity traps that reach the SVG (item 6)

| Construct | Site | Port rule |
|---|---|---|
| Fractional comparator results | `routing.mjs:830, 908-922, 1019-1029`; `render-architecture.mjs:303-309`; `automatic-labels.mjs:93, 150` | `Func<T,T,double>`, stable sort (S1) |
| String `<`/`>` in comparator | `geometry.mjs:1741-1743` | `string.CompareOrdinal` |
| `Math.hypot` | `geometry.mjs:1849-1850` (`roundedPath` → `Q` coords) | V8 form (S2) |
| Spread copy carries the `RESOLVED_MARK` symbol | `render-architecture.mjs:95` | B4 |
| `||` truthiness, where `0`/`''`/`NaN` fall through | `conn.width \|\| …`; `b.kind \|\| 'boundary'`; `meta.visual_preset \|\| 'classic'` (`cli.mjs:242, 363`); `meta.subtitle \|\| translate` (`cli.mjs:377`); `item.labelDx \|\| 0` (`geometry.mjs:1873`); `conn.variant \|\| 'default'` | JS truthiness helper. Not `??` |
| `??` where `0` must survive | `item.labelSegment ?? 1` (`geometry.mjs:1876`) | `??` with a null/missing check |
| JS `trim()` in attribute filters | `cli.mjs:402-427` | `Js.Trim` |
| Number interpolation incl. `-0` | every `${coord}`, `--step:${index}`, `data-composition-frame-id="${index}"` | `Js.Num` |
| Env vs meta quality profile | `cli.mjs:367`, `render-architecture.mjs:904` vs `:99, 884-949` | S5 |
| `Number.isInteger` on doubles | `grid.mjs:24, 42` | `d == Math.Floor(d) && double.IsFinite(d)` |
| `Number.isFinite(step)`, `Math.floor`, `Math.min(12, …)` | `cli.mjs:385` | handle undefined/NaN as 0 |
| `Map` duplicate keys | `render-architecture.mjs:98` | first position, last value (N7) |
| Regex `^` without `m` | `render-architecture.mjs:1074` `edge.replace(/^        /, …)` | first line only |
| `Math.min/max(...[])` → ±Infinity | guarded in `route-quality.mjs:123, 132`; `routing.mjs:1049-1051` (`scenePoints`) | never LINQ `Max()` on a possibly empty sequence |

Constructs that reach only messages, not bytes: `toFixed` (`render-architecture.mjs:644`), `localeCompare` (`:811`), default `.sort()` (`:796`), `Object.entries` (`:735`). There is no `for…in`, no `Map` delete-then-iterate, and no `Intl` in the phase-1 render path. `Object.entries`/`Object.keys` act only on objects with non-integer keys.

---

## Verified

- `applyTemplate` (`utils.mjs:150-201`): signature, the five sentinel checks, function replacers, `serializeScriptJson`, and `SUBTITLE_SLOT_RE` with the `m` flag, all as the plan describes. Probe: title `Cost $& $' $$` produces `<h1>Cost $&amp; $&#39; $$</h1>`.
- `renderCards` output equals the plan's expected string exactly (probe `true`). `renderDefinitions(extra)`, `renderSemanticSigil(kind, {x, y, size, icon})` and `textUnits` match the plan's signatures.
- `svgRootAttrs(meta, explicitQualityProfile)` (`cli.mjs:360`), `validateRelationshipIds`/`validateCrossCollectionContracts` (`cli.mjs:331-357`, code `relationship/duplicate-id`, probe confirmed), the `SEMANTIC_COLLECTIONS`/`RELATIONSHIP_COLLECTIONS` tables, and the brand `COLLECTIONS`.
- `registerLocale`, `resolveCatalog` report field names (`i18n.mjs:177-195`), case-insensitive bundled tag, and manifest order `en, zh-CN, zh-TW, es, ko`. `en.json` has 486 keys and none are integer-like.
- `prepareDiagramBrandMarks` codes and messages. The remote-brand probe with `ARCHIFY_DIAGNOSTIC_FORMAT=json` writes a JSON receipt to stderr on the top-level-await rejection, with exit 1 and a near-instant failure.
- `gridLayout`/`resolveComponentPos`/`validateGridPlacement` (`grid.mjs`). `createRouter` option names and defaults (`routing.mjs:75-90`, plus `minimumTerminalSegmentPx` and `readabilityBottomMargin`). `checkerDiagnostics` (`bin/archify.mjs:2685-2725`) matches the plan's description.
- All render-architecture imports (`:1-47`) are covered by Tasks 4–8. Legend needs a `LocaleCatalog` parameter (`legend.mjs:213`).
- Schema codes from the probe: `schema/additionalProperties@/`, `schema/required@/`, `schema/pattern@/nodes/0/id`, `schema/exclusiveMinimum@/components/0/size/0`, `schema/enum@/cards/0/dot`, `schema/minItems@/components` (Task 9 bad-input), `schema/pattern@/components/0/id` for `1bad` (Task 11). All 25 examples validate in both ajv and JsonSchema.Net, judged by `IsValid`.
- `brand-aware-delivery` components: 0 = `request` has no brand, 1 = `claude`, and the rest are presets. The Task 2 and Task 8 mutations target real components, and the remote-brand object passes the schema.
- 15 `.html` goldens. The `render-examples.mjs` TARGETS list contains only `web-app` for architecture. Plan Task 9 covers all 5 architecture JSONs plus the no-profile fixture. All goldens and the template are LF on disk (`i/lf w/lf`, `* text=auto eol=lf` upstream).
- `generated-brand-marks` extraction (with Windows paths): 107 entries.
- V8 vs plan `Js`:
  - **Correct:** `Num` layout (1e21 → `1e+21`, 1e20 integer form, `1e-7`, `0.000001`, `1.23e-18`, `5e-324`; `"R"` gives `1E+21`/`1E-05`/`1.2345678901234568E+17`/`5E-324`, which the parser handles).
  - **Correct:** `Round` for 2.5→3, -2.5→-2, 0.49999999999999994→0, -0.4→-0, 2^52+1.
  - **Correct:** `ToFixed` for 1.005→`1.00`, 2.5(0)→`3`, -2.5(0)→`-3`, 1e21→`1e+21`, 1e20(2).
  - **Wrong:** `ToFixed` negative-to-zero, see S3.
  - **Correct:** `ToPrecision` for 0.000001234→`0.00000123`, 999.5→`1.00e+3`, 123456→`1.23e+5`, 99.95→`100`, -0→`0.00`, 5e-324→`4.94e-324`, 1.797e308→`1.80e+308`.
  - **Correct:** `Quote`/`Stringify` (lone `\ud800`/`\udc00` lowercase escapes, U+2028/2029 and U+007F literal).
  - **Correct:** `SortDefault` ordinal order `["", " ", "1", "10", "9", "A", "B", "Zebra", "a", "apple", "b", "e", "node-10", "node-9", "z", "ä", "é"]`.
  - **Correct:** `LocaleCompare` with the `en` `CompareInfo`, whose 17×17 sign matrix is identical to V8.
  - **Correct:** `Trim`/`IsSpace` rules.
- JsonSchema.Net 9.4.0 supports `prefixItems`, `items: false`, `propertyNames`, `const`, `if` and `multipleOf` (no build or evaluation failures across all 11 schemas). `$ref` to `common.schema.json#/$defs/id` resolves through the `$id` auto-registered by `FromText` into the `BuildOptions` registry, in any build order.
