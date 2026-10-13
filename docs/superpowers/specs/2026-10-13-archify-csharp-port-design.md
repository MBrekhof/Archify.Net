# Archify C# port — design

Date: 2026-10-13. Status: approved by Martin (design in chat, review findings settled);
reviewed by a Claude Opus subagent (`2026-10-13-spec-review-opus.md`, 2 blockers +
11 should-fix, all folded in below).

## Goal

Port the machine-side of [tt-a1i/archify](https://github.com/tt-a1i/archify) v3.1.0
(upstream commit `c21c31ff`, local checkout `C:\projects\archify`) to C#/.NET 10 as a
class library first, with a CLI and MCP server on top. The reader-side viewer runtime
stays JavaScript and is embedded verbatim.

Decisions taken (Martin, 2026-10-13):

- Deliverable: library first (`Archify.Core`), then CLI, then MCP server. Rationale: the
  library is the unit the renderers already form; it lets .NET apps (XAF, duetGPT,
  ContextBoard) render diagrams in-process, which Node archify cannot offer.
- Viewer stays JS: `viewer/*.js`, `viewer.css` and the generated `assets/template.html`
  are copied verbatim. No Blazor/WASM rewrite.
- Parity bar: **strict byte parity** of rendered HTML against upstream's Node output for
  valid inputs, with one named exemption (CJK wrapping, below). Diagnostics parity is by
  `code`, not message text.
- Cuts: `atomic-output.mjs` hardening → temp file + `File.Move(overwrite: true)`;
  `update-awareness` dropped (verified: it gates nothing); hand-rolled CDP driver →
  Playwright for .NET; remote brand capture → built-in marks only; CLI commands
  `compare`, `brands`, `migrate`, `preview` out (table under CLI).
- CJK word wrapping: `StringInfo` graphemes + space/punctuation word breaks; Han/CJK
  wrapping is **outside the byte-parity guarantee**. Upgrade path if a CJK user appears:
  P/Invoke `ubrk_*` from the OS `icu.dll` (which .NET already loads).
- Review: Codex is unavailable; a Claude Opus subagent reviews spec, plan and each phase's
  diff instead (same gates, different reviewer).

## Upstream shape (measured 2026-10-13)

| Part | Lines | Treatment |
|---|---|---|
| `bin/` CLI, finalize, visual-check | 12 079 | port (minus cuts) |
| `renderers/` 10 types + `shared/` | 30 033 | port, file-for-file |
| `scripts/check-render-output.mjs` + `shared/svg-path-analysis.mjs` | 1 462 + 135 | port — the artifact checker; spawned by `validate`, `check`, `deliver`, `finalize` |
| `shared/portable-path.mjs` | 408 | port — the `portablePath` schema keyword |
| `recipes/scenarios.mjs` | 455 | port (`guide`) |
| `delta/`, `migrations/`, other `scripts/` | ~4 700 | out |
| `viewer/` JS + CSS → `assets/template.html` (785 KB) | 12 500 | copy verbatim |
| `test/` | 80 837 | parity suite replaces most; diagnostic-code cases and `golden.mjs` `expectFailure` cases ported |
| `schemas/*.json` | 11 files, JSON Schema 2020-12 | embedded resource |
| `examples/*.json` | 24 diagram JSONs, 15 with goldens | parity oracle (see Testing) |

Upstream has **no runtime dependencies**; `ajv` and `simple-icons` are dev-time only
(validators and the brand catalog are generated into `.mjs` files).

Verified facts that make byte parity realistic:

- Rendered HTML embeds no timestamps, hashes, random values or paths (`Date.now`/pid
  appear only in the temp candidate filename). `deliver` writes the same HTML bytes as
  `render`; provenance goes to sidecar files.
- Text fitting is a flat model (`widthFactor 0.6 × textUnits × fontSize`), no font metrics.
- Template injection is marker-based (`<!-- ARCHIFY:SVG_SLOT_START -->` … `_END`,
  `CARDS_SLOT_*`, `SOURCE_EVIDENCE_DATA`, `I18N_DATA`, 125 `{{i18n:…}}` tokens).
- The 15 packaged goldens equal a fresh Node render after newline normalisation
  (`test/golden.mjs`).

## Repository layout

```
C:\projects\arc#hify\
  Archify.sln
  Directory.Build.props              net10.0, nullable, implicit usings, TreatWarningsAsErrors,
                                     InvariantGlobalization=false (ICU needed: localeCompare, time zones)
  .gitattributes                     upstream/** -text ; tests/goldens/** -text  (LF bytes preserved)
  src/Archify.Core/                  the port (library)
  src/Archify.Cli/                   dotnet tool `archify`
  src/Archify.Mcp/                   C# MCP server (phase 6)
  tests/Archify.Core.Tests/          xUnit: parity suite + diagnostic-code cases + Js unit tests
  tests/goldens/                     Node-minted HTML for examples/fixtures lacking an upstream golden
  tests/fixtures/                    synthetic inputs: brand mark, zh-CN locale, DST timeline,
                                     no quality profile, source evidence (git repo fixture)
  upstream/                          verbatim copy from archify/archify@3.1.0:
    UPSTREAM.md                      commit c21c31ff, date, copy procedure
    schemas/  examples/  locales/  brand-marks/  references/  assets/template.html
    generated-brand-marks.json       the 107-entry array lifted from generated-brand-marks.mjs
    viewer/                          source of template.html (kept for diffing future upstream)
  docs/superpowers/specs/            this file + review
  SESSION_HANDOFF.md
```

`upstream/` is data, never edited by hand. Everything under it needed at runtime is an
`EmbeddedResource` of `Archify.Core`. Updating to a newer archify = re-copy `upstream/`,
re-run the parity suite, port the deltas.

The `#` in the repo path becomes `%23` in every `file://` URL (Playwright gates).

Task state is board-only (ContextBoard project `arc#hify`, id 46; no `TODO.md`).

## Core API (`Archify.Core`)

```csharp
public static class Archify
{
    public static IReadOnlyList<Diagnostic> Validate(DiagramType type, JsonNode diagram, RenderOptions? options = null);
    public static RenderResult Render(DiagramType type, JsonNode diagram, RenderOptions? options = null);
    public static CheckResult Check(string html, CheckOptions? options = null);   // check-render-output port
    public static TypeRecommendation Guide(string scenario);                       // recipes/scenarios
}

public sealed record RenderResult(string Html, string Svg, string Cards,
    IReadOnlyList<Diagnostic> Diagnostics, LayoutReport? Layout);   // Layout: architecture, class, erd, timeline, tree, waterfall, workflow only
public sealed record Diagnostic(string Code, string Message, string? Path, Severity Severity);
public sealed record RenderOptions(
    QualityProfile? Quality = null,          // precedence: explicit > meta.quality_profile > standard+advisory
    string? RepoRoot = null,                 // ARCHIFY_REPO_ROOT
    DiagnosticFormat Diagnostics = DiagnosticFormat.Json);   // ARCHIFY_DIAGNOSTIC_FORMAT
public enum DiagramType { Architecture, Workflow, Sequence, Dataflow, Lifecycle, Erd, Class, Tree, Timeline, Waterfall }
```

`Validate` mirrors upstream `validate`: schema + authored output path + cross-collection
relationship ids + engineering profile, then a render into memory and the artifact
checker — same gate order, same codes.

**Input semantics.** The IR is `System.Text.Json.Nodes.JsonNode` end-to-end, not DTOs
(upstream treats it as loosely-typed JSON gated by schema; DTOs would reorder keys). On
load: numbers are normalised to `double` (JS has one number type; `1.0`/`1e2` must not keep
raw text), duplicate keys keep the last value (as `JSON.parse`), key order is insertion
order. `meta.output` is schema-required, so in-memory callers supply it too.

**State model.** Upstream keeps module-global state (registered locale catalogs, brand
resolution cache) and renderers read `process.argv`/env at module top level. The port
replaces that with one `RenderContext` per call (locale catalog, brand resolver, options,
diagnostics sink) threaded through every renderer function — required for in-process,
multi-threaded use. Env vars (`ARCHIFY_QUALITY_PROFILE`, `ARCHIFY_REPO_ROOT`,
`ARCHIFY_DIAGNOSTIC_FORMAT`) exist only in the CLI, mapped onto `RenderOptions`.

`Render` throws nothing for bad input: it returns `Diagnostics` and an empty `Html`.
Exceptions are reserved for programmer error. Diagnostic code families are upstream's:
`schema/<keyword>`, `input/`, `layout/`, `brand/`, `relationship/`, `engineering/`,
`composition/`, `legend/`, `<type>/`, `internal/`.

## Parity layer: `Js`

One static class (plus two collection types), the only place JavaScript semantics live.
Every renderer formats, sorts, trims and serialises through it; nothing else does. Each
member carries `// ponytail: V8 parity` and a unit test with values captured from Node.

| Member | Mirrors | Why .NET differs |
|---|---|---|
| `Js.Sort<T>(list, Comparison<T>)` | `Array.prototype.sort(cmp)` | `List.Sort` is unstable; must be stable (comparators are key-based, so any stable sort matches) |
| `Js.SortDefault(list)` | `Array.prototype.sort()` | string sort by UTF-16 code units; .NET default is culture-aware. Note `Object.entries(o).sort()` sorts by `"key,value"` string form |
| `Js.Num(double)` | `Number→string` | `-0`→`"0"`; `1e21`/`1e-7` exponent thresholds; shortest round-trip |
| `Js.Round(double)` | `Math.round` | half toward +∞, not banker's |
| `Js.ToFixed(d, n)`, `Js.ToPrecision(d, n)` | same | .NET `F`/`G` round differently |
| `Js.ToLocaleStringEnUs(d, maxSig?, maxFrac?)` | `toLocaleString('en-US', …)` | waterfall duration labels, grouping commas |
| `Js.Hypot(a, b)` | `Math.hypot` | V8 scales + compensated sum; `double.Hypot` may differ in the last bit — port V8's algorithm |
| `Js.Stringify(JsonNode, indent?)` | `JSON.stringify` | STJ escapes `<>&'+` and non-ASCII; V8 does not |
| `Js.LocaleCompare(a, b)` | `localeCompare` | ICU collation; 3 call sites |
| `Js.Trim`, `Js.IsSpace` | `trim()`, regex `\s` | JS includes U+FEFF, excludes U+0085; .NET the reverse |
| `Js.ReplaceFirst(s, pat, fn)` | `String.replace(string, fn)` | .NET `Replace` replaces all |
| `Js.EscapeHtml` | `i18n.mjs` `escapeHtml` | `& < > " '` only, not `WebUtility.HtmlEncode` |
| `Js.TextUnits(string)` | `textUnits` in `utils.mjs` | code-point / East-Asian width rules |
| `Js.Graphemes(string)` | `Intl.Segmenter(grapheme)` | `StringInfo.GetTextElementEnumerator`; tested against Node |
| `Js.WordBreaks(string)` | `Intl.Segmenter(word)` | space/punctuation breaks; **CJK exempt from parity** (see decisions) |
| `JsMap<K,V>`, `JsSet<T>` | `Map`, `Set` | insertion order survives delete + re-add (`render-class.mjs:516`, `render-erd.mjs:751`, `workflow-compiler.mjs:714, 2374`); `Dictionary`/`HashSet` reuse freed slots. Keys by reference identity where upstream keys by object |

Rules: never enumerate a `Dictionary`/`HashSet`; never format a number with `ToString()`;
output is UTF-8 **without BOM** (`new UTF8Encoding(false)`); `Math.log10` and `**` are
used as-is (low risk, parity suite will tell).

**Time zones** (timeline): `Intl.DateTimeFormat` with IANA ids via `formatToParts`. Port
accepts IANA ids only (`TimeZoneInfo` on Windows would also accept `"China Standard Time"`,
which V8 rejects → `timeline/invalid-timezone` parity). A DST-crossing fixture
(`America/New_York`) is mandatory; both upstream timelines use `Asia/Shanghai`.

## Rendering pipeline

Mirrors upstream file-for-file: one `.cs` per `.mjs`, same base name, same function names
(PascalCased), same order of operations. Renderer bodies that upstream runs at module
top level become a `Render(RenderContext)` method per type. This keeps an Opus review of
"does `Routing.cs` do what `routing.mjs` does" a side-by-side read, and future upstream
deltas portable.

```
JsonNode → load (normalise numbers; register locale into RenderContext)
        → Validator: JsonSchema.Net 2020-12 + portablePath keyword + authored output path
                     + cross-collection ids + engineering profile
        → BrandMarks.Resolve (embedded 107-entry array; architecture, workflow, sequence,
                              dataflow, lifecycle; remote {url, sha256} → brand/capture-unavailable)
        → <Type>Renderer.Render(ctx) → Svg + Cards + LayoutReport?   (renderers call i18n
                              themselves: svgAccessibleText, focusNodeAttrs, legends)
        → TemplateWriter.Apply(template, title, subtitle, svg, cards, locale, visualPreset,
                               sourceEvidence, i18n catalog from ctx) → Html
        → ArtifactChecker.Check(html) (strict; = check-render-output)
        → (CLI only) temp file + File.Move(overwrite: true), sidecars
```

SVG is built with `StringBuilder` in upstream's attribute order. No `XDocument`/`XmlWriter`
— they reorder attributes and re-escape text, which breaks byte parity.

**Validation details.** `generated-validators.mjs` (ajv standalone) is not ported; the
embedded schemas run through JsonSchema.Net with these overrides: `minLength`/`maxLength`
count **code points** (ajv `ucs2length`; JsonSchema.Net counts graphemes); the three
patterns where .NET `Regex` diverges are pre-checked in code with JS semantics
(`[\uD800-\uDFFF]` lone surrogates, `\d` ASCII-only, `[^/]{256}` in code points); the
synthetic `portablePath` keyword is a ported `PortablePath.Validate`. Codes are
`schema/<keyword>`. "Parity by code" = the **set** of `(code, instancePath)` pairs per
input matches upstream; order and count under `anyOf`/`not` are not compared.

**Source evidence** (`--repo-root`): `repository-evidence.mjs` spawns `git`; the port does
the same via `Process`. Needs its own fixture repo under `tests/fixtures/`.

## CLI (`Archify.Cli`, phase 5)

`dotnet tool` named `archify`. Upstream has 16 commands:

| In (12) | Out (4) | Why |
|---|---|---|
| `render validate check deliver finalize browser-check visual-check guide inspect examples doctor demo` | `compare` | architecture base/head diff = `delta/` (1.3k lines), sole consumer |
| | `brands` | catalog query + remote capture; built-in marks need no command |
| | `migrate` | workflow v1→v2; no v1 files here |
| | `preview` | local HTTP server + watcher; `open-artifact` goes with it |

Flags as upstream: `--json`, `--summary`, `--quality`, `--repo-root`, `--out-dir`,
`--require-provenance`. Receipt JSON keeps upstream's shape (`schemaVersion: 1`,
`command`, `status`, `receiptId`, `gates`, `evidence.summaryReceipt`) minus the `update`
block.

**Sidecars** (names bounded by the `sidecar-path.mjs` hashing rule, ported):
`<stem>.delivery.json` (sha256 + receiptId; `check --require-provenance` needs it),
`<stem>.delivery-pending.json` (journal), directory lock, `<stem>.browser-check.json`,
`<stem>.finalize.json`, `<stem>.finalize-summary.json`. The lock and the pending journal
**survive** the `atomic-output` cut — `finalize` inspects them (`finalize.mjs:327-350`);
only the symlink/TOCTOU binding checks go.

`finalize` = validate → deliver → `check --require-provenance` → `browser-check
--require-provenance`, stopping at the first failing gate. `browser-check` uses Playwright
for .NET (Chromium): page loads, zero console errors, viewer boots, SVG slot present.
`visual-check` captures via Playwright screenshots.

## MCP server + skill (phase 6)

`Archify.Mcp` on the C# MCP SDK (see `csharp-mcp-server-generator` skill) exposing
`validate`, `render`, `finalize`, `guide`. `SKILL.md` adapted from upstream: same type
router, authoring references and output rules; `node bin/archify.mjs …` becomes
`archify …` / MCP tools; update-awareness and brand-capture sections removed.

## Testing

1. **ParityTests** (the spec of correctness). The list is driven by upstream's `TARGETS`
   table (`scripts/render-examples.mjs:12-28`) — golden names do not follow JSON stems
   (`web-app.architecture.json` → `web-app-rendered.html`, `payments.class.json` →
   `class-payments-rendered.html`; three goldens lack the `-rendered` suffix). For each
   target: `Render(json).Html` equals the golden **byte-for-byte**. The 9 JSONs without an
   upstream golden (incl. `production-deployment.architecture.json`) get goldens minted
   once with the upstream renderer into `tests/goldens/` (committed; Node is dev-time
   only). Synthetic fixtures, Node-minted the same way: brand mark, zh-CN locale, DST
   timeline, unset quality profile, source evidence. The repo-root `examples/*.html`
   (`rag-pipeline`, `maka-architecture`, …) are **not** oracles. A failing test prints the
   first differing line ±3 lines.
2. **Diagnostic-code tests**: `golden.mjs` `expectFailure` cases and upstream
   `test/*.test.mjs` bad-input cases; assert on the `(code, path)` set.
3. **`Js` unit tests**: values captured from Node for each helper, incl. `JsMap` delete +
   re-add order.
4. **Browser gate** (phase 5): Playwright for .NET, one example per type.
5. **Review gates**: Opus subagent reviews the implementation plan, then each phase's diff
   against the upstream `.mjs` files; self-review (case list) before each hand-off per
   global CLAUDE.md.

## Porting order (one board card each; each ends green on its parity slice)

1. **Foundation + architecture.** Scaffold, `.gitattributes`, `upstream/` copy, `Js` +
   `JsMap`/`JsSet`, `RenderContext`, i18n, validator (+ `portable-path`), brand marks
   (embedded array), `TemplateWriter`, artifact checker (`check-render-output` +
   `svg-path-analysis` + `desktop-readability`), shared: `geometry`, `route-quality`,
   `path-semantics`, `legend`, `text-fit`, `utils`, `automatic-labels`, `spatial-grid`,
   `node-card-style`, `layout-report`, `diagnostics`, `engineering-profiles`,
   `output-path`, `sidecar-path`; architecture: `routing`, `labels`, `grid`,
   `render-architecture`. Exit: `web-app`, `production-deployment` (minted) and
   `brand-aware-delivery` (minted) byte-identical; Node-captured `Js` tests green.
2. **erd/grid, sequence, lifecycle, class, tree, timeline, waterfall.** `erd/grid.mjs`
   first (class imports it; class and erd also import `architecture/routing`). Exit:
   their 8 goldens + DST and zh-CN fixtures.
3. **workflow** (compiler 5.9k + placement repair + migration geometry) and **dataflow**
   (compiler + joint-label-routing + label-capacity). Shares only `shared/` with phase 1.
4. **erd** (`render-erd`) + `repository-evidence` + source-evidence fixture.
5. **CLI**: 12 commands, receipts, sidecars, lock/journal, Playwright gates.
6. **MCP server + SKILL.md.**

## Out of scope

Viewer rewrite; `atomic-output` symlink/TOCTOU hardening; update awareness; hand-rolled
CDP; remote brand capture and `brands`; `compare`/`delta`; `migrate`; `preview` and
`open-artifact`; WebM/export tooling on the machine side (viewer-side export stays, it is
in the JS); website/docs/gallery builders; multi-language READMEs; exact ICU word
breaking for CJK. Each returns only if a use shows up.
