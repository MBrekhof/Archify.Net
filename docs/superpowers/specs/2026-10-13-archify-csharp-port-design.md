# Archify C# port — design

Date: 2026-10-13. Status: approved by Martin in conversation; awaiting review.

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
  valid inputs. Diagnostics parity is by `code`, not message text.
- Cuts (agreed): `atomic-output.mjs` hardening → temp file + `File.Move(overwrite: true)`;
  `update-awareness` dropped entirely; `browser-check`/`visual-check` hand-rolled CDP
  driver → Playwright for .NET.
- Review: Codex is unavailable; a Claude Opus subagent reviews spec, plan and each phase's
  diff instead (same gates, different reviewer).

## Upstream shape (measured 2026-10-13)

| Part | Lines | Treatment |
|---|---|---|
| `bin/` CLI, finalize, visual-check | 12 079 | port (minus cuts) |
| `renderers/` 10 types + `shared/` | 30 033 | port, file-for-file |
| `delta/`, `recipes/`, `migrations/`, `scripts/` | ~5 700 | `recipes/scenarios` ported (guide); rest out |
| `viewer/` JS + CSS → `assets/template.html` (785 KB) | 12 500 | copy verbatim |
| `test/` | 80 837 | parity suite replaces most; diagnostic-code cases ported selectively |
| `schemas/*.json` | 11 files, JSON Schema 2020-12 | embedded resource |
| `examples/*.json` + `*-rendered.html` | 40+ | parity oracle |

Upstream runtime deps: `ajv` (schema validation) and `simple-icons` (brand-mark catalog,
compiled at build time into `generated-brand-marks.mjs`). Nothing else.

Verified facts that make byte parity realistic:

- Rendered HTML embeds no timestamps, hashes or random values.
- Text fitting is a flat model (`widthFactor 0.6 × textUnits × fontSize`), no font metrics.
- Template injection is marker-based (`<!-- ARCHIFY:SVG_SLOT_START -->` … `_END`,
  `CARDS_SLOT_*`, `SOURCE_EVIDENCE_DATA`, `I18N_DATA`, `{{i18n:…}}` tokens).

## Repository layout

```
C:\projects\arc#hify\
  Archify.sln
  Directory.Build.props              net10.0, nullable, implicit usings, TreatWarningsAsErrors
  src/Archify.Core/                  the port (library)
  src/Archify.Cli/                   dotnet tool `archify`
  src/Archify.Mcp/                   C# MCP server (phase 6)
  tests/Archify.Core.Tests/          xUnit: parity suite + ported diagnostic cases
  tests/goldens/                     Node-rendered HTML for examples lacking a *-rendered.html
  upstream/                          verbatim copy from archify/archify@3.1.0:
    UPSTREAM.md                      commit c21c31ff, date, copy procedure
    schemas/  examples/  locales/  brand-marks/  references/  assets/template.html
    viewer/                          source of template.html (kept for diffing future upstream)
  docs/superpowers/specs/            this file
  SESSION_HANDOFF.md
```

`upstream/` is data, never edited by hand. Everything under it needed at runtime is an
`EmbeddedResource` of `Archify.Core`. Updating to a newer archify = re-copy `upstream/`,
re-run the parity suite, port the deltas.

Task state is board-only (ContextBoard project `arc#hify`; no `TODO.md`).

## Core API (`Archify.Core`)

```csharp
public static class Archify
{
    public static IReadOnlyList<Diagnostic> Validate(DiagramType type, JsonNode diagram);
    public static RenderResult Render(DiagramType type, JsonNode diagram, RenderOptions? options = null);
    public static CheckResult Check(string html);               // strict artifact check
    public static TypeRecommendation Guide(string scenario);     // recipes/scenarios
}

public sealed record RenderResult(string Html, string Svg, string Cards,
    IReadOnlyList<Diagnostic> Diagnostics, LayoutReport Layout, DeliveryReceipt Receipt);
public sealed record Diagnostic(string Code, string Message, string? Path, Severity Severity);
public sealed record RenderOptions(QualityProfile Quality = QualityProfile.Showcase,
    string? Locale = null, string? RepoRoot = null, BrandMarkPolicy Brands = BrandMarkPolicy.Catalog);
public enum DiagramType { Architecture, Workflow, Sequence, Dataflow, Lifecycle, Erd, Class, Tree, Timeline, Waterfall }
```

The diagram IR is `System.Text.Json.Nodes.JsonNode` end-to-end, not DTOs. Upstream treats
the IR as loosely-typed JSON gated by schema; DTOs would be ten more files to keep in
lockstep and would reorder keys. `JsonObject` preserves insertion order, as V8 does.

`Render` throws nothing for bad input: it returns `Diagnostics` with upstream's codes
(`input/…`, `render/…`, `erd/domain-not-drawn`, …) and an empty `Html`. Exceptions are
reserved for programmer error.

## Parity layer: `Js`

One static class, the only place JavaScript semantics live. Every renderer formats
numbers, sorts and serialises through it; nothing else does.

| Member | Mirrors | Why .NET differs |
|---|---|---|
| `Js.Sort<T>(IList<T>, Comparison<T>)` | `Array.prototype.sort` | `List.Sort` is unstable; V8's is stable |
| `Js.Num(double)` | `Number.prototype.toString` | `-0` → `"0"`; `1e21`/`1e-7` exponent thresholds; shortest round-trip |
| `Js.Round(double)` | `Math.round` | half toward +∞, not banker's |
| `Js.Stringify(JsonNode, indent?)` | `JSON.stringify` | STJ escapes `<>&'+` and non-ASCII; V8 does not |
| `Js.LocaleCompare(a, b)` | `String.prototype.localeCompare` | ICU collation; 3 call sites |
| `Js.TextUnits(string)` | `textUnits` in `utils.mjs` | code-point / East-Asian width rules |

Each member carries `// ponytail: V8 parity` and one unit test with values taken from Node.

**Known risk: `Intl.*`** (8 call sites, timeline/waterfall formatting). .NET ICU and V8 ICU
may format a locale differently. The parity suite surfaces it in phase 2. Fix if needed:
a fixed-format table for the five locales upstream ships (en, es, ko, zh-CN, zh-TW), not a
dependency.

## Rendering pipeline

Mirrors upstream file-for-file: one `.cs` per `.mjs`, same base name, same function names
(PascalCased), same order of operations. This is deliberate: it makes a review of
"does `Routing.cs` do what `routing.mjs` does" a side-by-side read, and keeps future
upstream deltas portable.

```
JsonNode → Validator (JsonSchema.Net, 2020-12; + cross-collection id checks from cli.mjs)
        → BrandMarks.Resolve (embedded catalog.json; simple-icons SVG paths pre-extracted)
        → <Type>Renderer.Render → Svg + Cards + LayoutReport
        → TemplateWriter.Apply(template, title, subtitle, svg, cards, locale, visualPreset,
                               sourceEvidence, i18n) → Html
        → (CLI only) write temp file, File.Move(overwrite: true)
```

SVG is built with `StringBuilder` in upstream's attribute order. No `XDocument`/`XmlWriter`
— they reorder attributes and re-escape text, which breaks byte parity.

`generated-validators.mjs` (ajv standalone) is not ported; validation runs against the
embedded schema files at runtime. Schema-violation diagnostics map ajv's `instancePath` +
`keyword` to upstream's `input/…` codes.

## CLI (`Archify.Cli`, phase 5)

`dotnet tool` named `archify`. Commands: `validate | render | check | deliver | finalize |
guide | doctor | demo`. Receipt JSON keeps upstream's shape (`schemaVersion: 1`,
`command`, `status`, `receiptId`, `gates`, `evidence.summaryReceipt`,
`<stem>.finalize-summary.json`) minus the `update` block. `--json`, `--quality`,
`--repo-root`, `--out-dir` as upstream. `browser-check` gate uses Playwright for .NET
(Chromium): page loads, zero console errors, viewer boots, SVG slot present.
`visual-check` captures via Playwright screenshots.

## MCP server + skill (phase 6)

`Archify.Mcp` on the C# MCP SDK (see `csharp-mcp-server-generator` skill) exposing
`validate`, `render`, `finalize`, `guide`. `SKILL.md` adapted from upstream: same type
router, authoring references and output rules; `node bin/archify.mjs …` becomes
`archify …` / MCP tools; update-awareness section removed.

## Testing

1. **ParityTests** (the spec of correctness): for every `upstream/examples/*.json` with a
   `*-rendered.html`, `Render(json).Html` must equal the file byte-for-byte. Examples
   without rendered HTML get goldens minted once with `node bin/archify.mjs render` into
   `tests/goldens/` (committed; Node is a dev-time tool, never a runtime dependency).
   A failing parity test prints the first differing line and ±3 lines of context.
2. **Diagnostic-code tests**: ported from upstream `test/*.test.mjs` cases that feed bad
   input; assert on `code` only.
3. **`Js` unit tests**: values captured from Node for each helper.
4. **Browser gate** (phase 5): Playwright for .NET, one example per type.
5. **Review gates**: Opus subagent reviews this spec, then the implementation plan, then
   each phase's diff against the upstream `.mjs` files; self-review (case list) before each
   hand-off per global CLAUDE.md.

## Porting order (one board card each; each ends green on its parity slice)

1. Scaffold, `Js`, `TemplateWriter`, i18n, validator, shared geometry/routing/
   route-quality/path-semantics/legend/text-fit, architecture renderer →
   `web-app.architecture.json` and `production-deployment.architecture.json` byte-identical.
2. sequence, lifecycle, class, tree, timeline, waterfall (self-contained, ~5k lines).
3. workflow (compiler 5.9k + placement repair + migration geometry) and dataflow (~1.9k).
4. erd + brand marks (catalog extraction script replaces `generate-brand-marks.mjs`).
5. CLI + receipts + Playwright gates.
6. MCP server + SKILL.md.

## Out of scope

Viewer rewrite; `atomic-output` hardening; update awareness; hand-rolled CDP;
`delta` (architecture diff); `migrate` (workflow v1→v2); `preview` server; `open-artifact`;
WebM/export tooling on the machine side (viewer-side export stays, it is in the JS);
website/docs/gallery builders; multi-language READMEs. `delta`/`migrate` return if a use
shows up.
