# Review: archify C# port design spec, checked against upstream `c21c31ff`

Reviewer: Claude Opus subagent, 2026-10-13. Read-only review. I did not render the Node examples myself, because that would write files. Upstream's `test/golden.mjs` already asserts that a fresh render matches the committed goldens, after newline normalization.

## Blockers

### 1. The artifact checker is cut, but `check`, `validate`, `deliver` and `finalize` all depend on it
- **Spec:** the "`delta/`, `recipes/`, `migrations/`, `scripts/`" row ports `recipes` only ("rest out"). Yet the API still exposes `Check(string html)`.
- **Evidence:** `scripts/check-render-output.mjs` (1,462 lines) is spawned at `bin/archify.mjs:3607, 4847, 5491, 6517, 6950`. Even `validate` renders to a temp file and runs it. It imports `svg-path-analysis.mjs`, `geometry.mjs` and `desktop-readability.mjs`.
- **Fix:** put `check-render-output.mjs` and `svg-path-analysis.mjs` in scope. It belongs no later than phase 5, and earlier if `Validate` should match upstream `validate`.

### 2. `Intl.Segmenter` word-breaking affects the output, and .NET has no equivalent
- **Spec:** the `Intl.*` risk is described as "8 call sites, timeline/waterfall formatting".
- **Evidence:**
  - Word-granularity ICU segmentation splits Han text in sequence notes (`render-sequence.mjs:197-206`) and dataflow label wrap candidates (`label-capacity.mjs:32-33`).
  - Grapheme segmentation is used at `dataflow-compiler.mjs:1398` and `render-sequence.mjs:179`.
  - Waterfall does not use `Intl` at all; it uses `Number.toLocaleString`. The 8 hits are 5 Segmenter (one in `path-semantics`), 1 feature check and 2 timeline `DateTimeFormat`.
- **Fix:** decide this now:
  - (a) P/Invoke ICU `ubrk_*` from the OS `icu.dll`, which .NET itself loads; or
  - (b) declare CJK note and label wrapping outside the strict byte-parity guarantee.
  - Grapheme segmentation can use `StringInfo`, but test it against Node.
  - No existing golden contains CJK, so the parity suite cannot catch this.

## Should-fix

### 3. The default quality profile is wrong
- **Spec:** `RenderOptions(QualityProfile Quality = QualityProfile.Showcase …)`.
- **Evidence:**
  - With no profile, upstream emits `data-quality-profile="standard"` plus `data-quality-gates="advisory"` (`cli.mjs:367-370`).
  - `showcase` also turns on gating (`render-architecture.mjs:904`, `geometry.mjs:1305`).
  - Precedence is CLI `--quality` (passed via the `ARCHIFY_QUALITY_PROFILE` env var, `bin/archify.mjs:2393`), then `meta.quality_profile`.
  - All 23 diagram examples set `"quality_profile": "showcase"`, so the parity suite cannot catch this.
- **Fix:** use `QualityProfile? Quality = null` with precedence explicit > meta > advisory, and add a golden with no profile.

### 4. The parity oracle mapping is wrong, and coverage has gaps
- **Spec:** "for every `examples/*.json` with a `*-rendered.html`".
- **Evidence:**
  - Golden names do not follow the JSON stems (`web-app.architecture.json` → `web-app-rendered.html`, `payments.class.json` → `class-payments-rendered.html`).
  - Three goldens have no `-rendered` suffix: `sequence-cache-miss-request.html`, `dataflow-product-analytics.html`, `lifecycle-agent-run.html`.
  - The authoritative mapping is the TARGETS table in `scripts/render-examples.mjs:12-28` and GOLDEN in `test/golden.mjs`. `render:examples` writes to the repo-root `examples/`, which is the dev golden directory.
  - `production-deployment.architecture.json`, which is phase 1's exit criterion, has no golden.
  - Nine diagram JSON files have no golden: `async-job-roundtrip.sequence`, `brand-aware-delivery.architecture`, `checkout-platform.base.architecture`, `checkout-platform.head.architecture`, `deployment-release.lifecycle`, `event-stream.dataflow`, `holiday-planning.workflow`, `incident-response.workflow`, `production-deployment.architecture`, `release-delivery.workflow`.
  - Not covered by any golden:
    - brand marks (zero goldens contain `data-brand-mark="`);
    - non-`en` locales (only `holiday-planning.workflow.json` is zh-CN, and it has no golden);
    - CJK text;
    - DST time zones (both timelines use Asia/Shanghai);
    - source evidence (no example has `meta.repository`);
    - an unset quality profile.
  - Upstream compares only after newline normalization (`golden.mjs:38-40, 100-104`) and relies on `.gitattributes` `* text=auto eol=lf`. `arc#hify` has no git repo or `.gitattributes` yet, and `core.autocrlf=true` would turn the copied template and goldens into CRLF.
  - The repo-root `examples/` also has extra HTML that is not golden-tested (`rag-pipeline.html`, `maka-architecture.html`, `archify-repo*.html`, `web-app.html`, `sequence-cache-miss.html`, `workflow-agent-tool-call.html`). Do not use these as oracles.
- **Fix:**
  - Drive the parity list from the TARGETS table.
  - Mint goldens for the JSON files that lack one, using the renderer script directly, as `render-examples.mjs` does.
  - Add Node-minted synthetic fixtures for each uncovered path listed above.
  - Add `upstream/** -text` (or `eol=lf`) before the first commit.

### 5. `Map`/`Set` iteration order is missing from the `Js` layer
- **Evidence:** renderers construct 337 `Map`/`Set` objects. Entries are deleted and later re-added at `render-class.mjs:516-517`, `render-erd.mjs:751`, and `workflow-compiler.mjs:714, 2374`. A C# `Dictionary`/`HashSet` reuses freed slots after a delete, so its enumeration order diverges from JS insertion order.
- **Fix:** require `OrderedDictionary<TKey,TValue>` (.NET 9+) and an insertion-ordered set, and forbid enumerating `Dictionary`/`HashSet`. Port maps keyed by object identity with reference equality, never with value-equality records or tuples.

### 6. The `Js` helper table is incomplete

These all reach the output:
- **`.sort()` with no comparator** compares UTF-16 code units, but C# string sort is culture-aware. Sites: `render-architecture.mjs:796`, `render-lifecycle.mjs:308`, `render-tree.mjs:98`, `render-waterfall.mjs:97`, `i18n.mjs:96, 105`. At `workflow-compiler.mjs:251`, `Object.entries(...).sort()` orders the pairs by their `"key,value"` string form. Add `Js.SortDefault` (ordinal).
- **`toFixed(6)`** at `workflow-migration-geometry.mjs:69` feeds geometry. (`toFixed(2)` at `render-architecture.mjs:644` is message text only.)
- **Waterfall `number()`** (`render-waterfall.mjs:131-137`) uses `toPrecision(3)` and `toLocaleString('en-US', {maximumSignificantDigits:3 | maximumFractionDigits:2})`, with grouping commas. This formats every duration label.
- **`Math.hypot`** in `roundedPath` (`geometry.mjs:1849-1857`) feeds `Q` path coordinates. V8's algorithm (scaling plus compensated sum) can differ from `double.Hypot` in the last bit on diagonal segments. Axis-aligned segments are exact in both. `Math.log10` and `**` (`render-waterfall.mjs:179`) carry the same kind of risk (low).
- **`String.replace(string, fn)`** replaces the first match only; C# `Replace` replaces all. See `utils.mjs:188-200`. Each template placeholder occurs once today, but port it as replace-first.
- **`trim()` and regex `\s`** treat whitespace differently: JS includes U+FEFF and excludes U+0085, and .NET is the reverse. Sites: `cli.mjs:402, 417` (attribute dropping), `utils.mjs:184` (subtitle), `render-sequence.mjs:197, 203`, `dataflow-compiler.mjs:1399`.
- **`escapeHtml`** (`i18n.mjs:6-10`, `& < > " '` only) is not `WebUtility.HtmlEncode`.
- **Output encoding:** write UTF-8 without a BOM (`Encoding.UTF8` emits one).
- **Comparator sort:** any stable sort gives identical results while comparators are consistent (all comparators sampled are key-difference based). Note in the spec that `Js.Sort` must be stable, not that it must replicate V8's TimSort.

### 7. Timeline time zones
- **Evidence:** `render-timeline.mjs:82, 113-125` uses `Intl.DateTimeFormat` with an IANA zone, through `formatToParts` and a zone-offset computation. .NET's `TimeZoneInfo` on Windows also accepts Windows IDs such as `"China Standard Time"`, which V8 rejects. That breaks parity for the `timeline/invalid-timezone` diagnostic. The tzdata version may also differ.
- **Fix:** accept IANA IDs only, and add a golden that crosses a DST transition (for example America/New_York).

### 8. Schema validation semantics differ
- **Spec:** "map ajv's `instancePath` + `keyword` to upstream's `input/…` codes".
- **Evidence:**
  - The codes are actually `schema/${keyword}` (`validator.mjs:70`).
  - Each generated validator is wrapped in a synthetic `portablePath` keyword that calls `validatePortablePath` from `portable-path.mjs` (408 lines) (`generate-validators.mjs:62-80`).
  - ajv runs as `Ajv2020` with `allErrors`, `strict`, `u`-flag regexes, and code-point `minLength` (`ucs2length` inlined, lines 15-19 and 31-43).
  - JsonSchema.Net 9.4.0 (in the local NuGet cache) references `StringInfo.LengthInTextElements`, i.e. it counts graphemes.
  - .NET `Regex` diverges on three patterns:
    - `[\uD800-\uDFFF]` (`common.schema.json:38`) matches each half of a valid surrogate pair, so any emoji in `meta.output` would be rejected;
    - `\d` (`timeline.schema.json:128`) accepts non-ASCII digits;
    - `[^/]{256}` (`common.schema.json:41`) counts UTF-16 units rather than code points.
  - The schemas use `$ref` into `common.schema.json`, `if`/`then`, `anyOf`/`oneOf`/`not`/`allOf`, `propertyNames`, `contains`, `multipleOf: 0.5` and `exclusiveMinimum`. They use no `format`, no `unevaluated*` and no custom keywords other than the `portablePath` wrapper.
  - Error lists under `anyOf`/`not` will differ in count and order.
- **Fix:**
  - Port `portable-path.mjs`.
  - Define "parity by code" precisely (for example, the set of codes per path).
  - Override the length and regex semantics, or pre-check those patterns.
  - Port the negative cases from `test/golden.mjs` (`expectFailure`).
  - Note that `meta.output` is schema-required, so in-memory API callers must supply it.

### 9. The pipeline and state model are under-specified
- **Evidence:**
  - `loadDiagram` (`cli.mjs:79-85`) also runs `validateAuthoredOutputPath`, relationship-id checks (`validateCrossCollectionContracts`) and `validateEngineeringProfile`, and registers the locale before rendering.
  - Renderers use i18n themselves: `svgAccessibleText` (`cli.mjs:377`), `focusNodeAttrs` (line 409) and legends. It is not only a `TemplateWriter` step. `localizeTemplate` also resolves through the per-document catalog that `registerLocale` installs (`i18n.mjs:322-324`).
  - State is module-global: `registerLocale` catalogs, `BUNDLED_CATALOGS` and `RESOLVED_BY_NODE` (`brand-marks.mjs:19`).
  - Renderers are top-level scripts reading `process.argv` and the env (`render-architecture.mjs:52-54`; `render-timeline.mjs` runs entirely at module level), so "same function names" has nothing to map to for their bodies.
  - Env-var inputs are `ARCHIFY_QUALITY_PROFILE`, `ARCHIFY_REPO_ROOT` and `ARCHIFY_DIAGNOSTIC_FORMAT` (`bin/archify.mjs:2391-2397`), plus `ARCHIFY_BRAND_*`.
- **Fix:** specify a per-render context object (needed for in-process thread safety), map the env vars to `RenderOptions`, and make `LayoutReport` nullable (only architecture, class, erd, timeline, tree, waterfall and workflow build one).

### 10. Brand marks are mis-scoped
- **Spec:** brand marks come from an embedded `catalog.json`, ship in phase 4 with erd, and need an extraction script.
- **Evidence:**
  - `catalog.json` holds only simple-icons slugs (`"simpleIcon": "googlegemini"`).
  - The 107 marks (id, title, category, aliases, domains, viewBox, hex, path, provenance) live as a pure JSON literal inside `Object.freeze([...])` in `generated-brand-marks.mjs`.
  - Brands are used by architecture, workflow, sequence, dataflow and lifecycle (`brand-marks.mjs:10-16`), not by erd.
  - A digest-pinned `{url, sha256}` brand is fetched over the network at render time (`brand-marks.mjs:596-616`), with an SSRF guard and `ARCHIFY_BRAND_*` env knobs.
  - There is also an `archify brands` / `brands capture` command (`bin:6107`; `references/brand-marks.md:12, 30`).
- **Fix:**
  - Copy the generated array as JSON; no extraction script and no simple-icons are needed.
  - Move brand marks to phase 1, because architecture needs them.
  - Decide whether remote capture and the `brands` command are ported, or whether remote brands always return `brand/capture-unavailable`.

### 11. What `finalize`/`check` need from the cut modules
- **Evidence:**
  - Finalize runs validate → deliver → `check --require-provenance` → `browser-check --require-provenance` (`finalize.mjs:22, 503-506`).
  - `check` requires the artifact-bound `.delivery.json` (sha256 plus `receiptId`, `archify.mjs:1903`).
  - Finalize also inspects `.delivery-pending.json` and the directory lock (`finalize.mjs:327-350`).
  - Sidecar names are hash-bounded by `sidecar-path.mjs` (`finalize.mjs:512-515`). The full set is `.delivery.json`, `.delivery-pending.json`, the lock, `.browser-check.json`, `.finalize.json` and `.finalize-summary.json`.
  - The spec names only `<stem>.finalize-summary.json`.
- **Fix:** list every sidecar and its shape, and state whether the lock and journal survive the `atomic-output` cut. The CLI command list also omits `inspect`, `compare`, `brands`, `examples` and `migrate`; state each one as in or out.

### 12. Input semantics of `JsonNode`
- **Evidence:**
  - `JSON.parse` keeps the last of duplicate keys; `JsonObject` is expected to throw (I have not tested this).
  - Authored numbers like `1.0` or `1e2` keep their raw text in `JsonNode`, whereas JS normalizes them.
  - The claim that `JsonObject` preserves insertion order "as V8 does" is not quite right, because V8 moves integer-like keys first. It is harmless today only because the schema id/`propertyNames` patterns forbid such keys.
- **Fix:** normalize numbers to `double` on load, specify duplicate-key behaviour, and qualify the ordering claim.

### 13. Porting order: phase dependencies are missing
- **Evidence:**
  - `render-class.mjs` imports `erd/grid.mjs` and `architecture/routing.mjs`, so class (phase 2) depends on erd code that is scheduled for phase 4. `render-erd.mjs` also imports `architecture/routing.mjs`.
  - Architecture also imports `automatic-labels`, `spatial-grid`, `desktop-readability`, `node-card-style`, `layout-report`, `diagnostics`, `engineering-profiles`, `brand-marks` and its own `labels.mjs`/`grid.mjs`. Phase 1 lists none of these.
- **Fix:** add them to phase 1 and move `erd/grid.mjs` before class.

## Notes

- **Diagnostic code families:** no `render/…` family exists. The real ones are `schema/`, `input/`, `layout/`, `brand/`, `relationship/`, `engineering/`, `composition/`, `legend/`, `<type>/` and `internal/`.
- **Upstream dependencies:** `ajv` and `simple-icons` are devDependencies only (`archify/package.json`); the upstream runtime has no dependencies.
- **Example counts:** there are 24 diagram JSON files and 15 goldens, not "40+".
- **Globalization:** `InvariantGlobalization` and NLS mode must stay off (needed for `localeCompare`, segmentation and time zones). Watch this when trimming the dotnet tool.
- **Source evidence:** it spawns `git` (`repository-evidence.mjs:25, 57`) and needs its own fixture.
- **`#` in the repo path:** `C:\projects\arc#hify` puts `#` into `file://` URLs for the Playwright gates, so they must be escaped as `%23`. I have not tested this.

## Verified

- **No varying output:** render HTML contains no timestamps, hashes, random values or paths. `Date.now`/pid appear only in the temp candidate name (`cli.mjs:171`), and the goldens contain no paths or ISO timestamps.
- **`deliver` HTML:** deliver renders the specification snapshot with the same renderer as `render`; provenance goes to sidecars, not into the HTML.
- **`render`:** it is a renderer spawn with env vars only (`bin/archify.mjs:4233-4249`), the same as `render-examples.mjs`.
- **Text fitting:** the flat 0.6 × units × font-size model is confirmed (`text-fit.mjs:24-44`).
- **Template markers:** all markers are present once in `assets/template.html`; there are 125 `{{i18n:…}}` tokens.
- **`localeCompare`:** exactly 3 call sites.
- **Update awareness:** it does not gate anything (returns `unavailable` when disabled; `finalize.mjs:642` omits it then), so dropping it is safe.
- **Workflow compiler:** it shares only `shared/` modules with architecture, so phase 3 placement is fine.
- **Goldens:** all 15 packaged and dev goldens are identical after newline normalization.
- **Line counts:** `bin/` totals 12,079 lines, as the spec says.
