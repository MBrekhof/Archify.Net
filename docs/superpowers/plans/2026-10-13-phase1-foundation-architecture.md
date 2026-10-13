# Archify C# Port — Phase 1: Foundation + Architecture Renderer

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Archify.Render(DiagramType.Architecture, json)` produces HTML byte-identical to upstream Node archify 3.1.0 for every architecture example, on a foundation (Js parity layer, RenderContext, i18n, validator, template writer, shared geometry, brand marks, artifact checker) that phases 2–6 build on without rework.

**Architecture:** One C# file per upstream `.mjs`, same base name, same function names PascalCased, same order of operations. Upstream's module-global state (diagnostic recorder, registered locale catalogs, brand cache) becomes one `RenderContext` per call. All JavaScript semantics (number formatting, sorting, JSON escaping, trimming, Map/Set order) live in a single `Js` static class + `JsMap`/`JsSet`; renderers never call `ToString()`, `List.Sort`, `Dictionary` enumeration or `string.Trim()` directly. Correctness = the parity suite: rendered HTML equals Node's bytes.

**Tech Stack:** .NET 10 (SDK 10.0.401), C# 14, `System.Text.Json.Nodes`, `JsonSchema.Net 9.4.0`, xUnit 2.9.3 + `xunit.runner.visualstudio 3.1.5` + `Microsoft.NET.Test.Sdk 17.14.1`. Node 24 only as a dev-time tool to mint goldens and fixtures from upstream (`C:\projects\archify`).

**Spec:** `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (+ review `2026-10-13-spec-review-opus.md`). Upstream checkout: `C:\projects\archify` at commit `c21c31ff`; skill package root `C:\projects\archify\archify\` — referred to below as `UP/`.

## Global Constraints

- Target `net10.0`; `Nullable` + `ImplicitUsings` enabled; `TreatWarningsAsErrors` true; `InvariantGlobalization` **false** (ICU needed for `localeCompare` and IANA time zones).
- **Strict byte parity** of `RenderResult.Html` with upstream for valid inputs. Only exemption: CJK word wrapping (phase 2+).
- Never format a number with `ToString()`/interpolation in renderer code — `Js.Num`. Never `List.Sort` — `Js.Sort`/`Js.SortDefault`. Never enumerate `Dictionary`/`HashSet` — `JsMap`/`JsSet`. Never `string.Trim()` on authored text — `Js.Trim`. Never `WebUtility.HtmlEncode` — `Js.EscapeHtml`.
- SVG/HTML built with `StringBuilder`/interpolated strings in upstream attribute order. No `XDocument`, `XmlWriter`, `HtmlAgilityPack`.
- Output text is UTF-8 **without BOM** (`new UTF8Encoding(false)`), LF line endings as upstream emits them.
- `upstream/` and `tests/goldens/` are byte-exact copies: `.gitattributes` marks them `-text`. Never hand-edit.
- Diagnostics are returned, never thrown, from the public `Archify.*` API; codes are upstream's (`schema/<keyword>`, `input/…`, `layout/…`, `brand/…`, `relationship/…`, `engineering/…`, `composition/…`, `legend/…`, `architecture/…`, `output/…`, `internal/…`). Message text is not parity-checked.
- No new NuGet dependency beyond `JsonSchema.Net` in `Archify.Net.Core`.
- Every `Js` member carries `// ponytail: V8 parity` and a fixture-backed test.
- Commits: message in a temp file + `git commit -F`; stage exact files (never `-a`, `-A`, `.`); trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Task state on ContextBoard project `Archify.Net` (id 46); no `TODO.md`.

## Review Focus

Spec-implied inputs no task's parity golden exercises; each has a pinned test in the owning task:

1. **Authored number with raw text `1.0` / `1e2`** → must behave as JS `1` / `100` everywhere, including serialised output. Test in Task 4 (`DiagramLoader` normalisation) and Task 3 (`Js.Num`).
2. **Duplicate JSON keys** in authored input → last value wins (as `JSON.parse`), no exception. Test in Task 4.
3. **`meta.quality_profile` absent and no explicit option** → `data-quality-profile="standard" data-quality-gates="advisory"`, no gating. Test in Task 9 (no-profile fixture golden).
4. **Label containing `$&`, `<`, `&`, `'` and a U+FEFF/U+0085 edge** → escaped as upstream (`esc` only `&<>"'`; `$&` literal; JS `trim` semantics). Tests in Task 3 (`Js.Trim`, `Js.ReplaceFirst`) and Task 5 (`TemplateWriter` with `$&` in title).
5. **Remote brand `{url, sha256}`** → `brand/capture-unavailable` diagnostic, no network call, render still succeeds for built-in marks. Test in Task 8.

---

## File Structure

```
Archify.Net.sln
Directory.Build.props
.gitattributes
src/Archify.Net.Core/Archify.Net.Core.csproj              embeds upstream/** as resources (LogicalName = path with '/')
src/Archify.Net.Core/Archify.cs                        public facade: Validate / Render / Check
src/Archify.Net.Core/DiagramType.cs                    enum + Name() ("architecture"…) + collections map
src/Archify.Net.Core/RenderOptions.cs                  RenderOptions, QualityProfile, DiagnosticFormat
src/Archify.Net.Core/RenderResult.cs                   RenderResult, CheckResult, Diagnostic, Severity, LayoutReport
src/Archify.Net.Core/RenderContext.cs                  per-call state: DiagnosticSink, LocaleCatalog, options, caches
src/Archify.Net.Core/Resources.cs                      embedded resource reader (Text(path), Json(path))
src/Archify.Net.Core/Js/Js.cs                          V8 parity helpers
src/Archify.Net.Core/Js/JsMap.cs, JsSet.cs             insertion-ordered map/set
src/Archify.Net.Core/Js/JsonNodeExt.cs                 J.Str/J.Num/J.Arr/J.Obj/J.Bool accessors (double-only numbers)
src/Archify.Net.Core/Shared/Diagnostics.cs             ← UP/renderers/shared/diagnostics.mjs
src/Archify.Net.Core/Shared/I18n.cs                    ← i18n.mjs
src/Archify.Net.Core/Shared/Utils.cs                   ← utils.mjs (Esc, RenderDefinitions, RenderSemanticSigil, TextUnits, RenderCards)
src/Archify.Net.Core/Shared/TemplateWriter.cs          ← utils.mjs applyTemplate + cli.mjs writeDiagram (html assembly only)
src/Archify.Net.Core/Shared/PortablePath.cs            ← portable-path.mjs
src/Archify.Net.Core/Shared/OutputPath.cs              ← output-path.mjs
src/Archify.Net.Core/Shared/SchemaValidator.cs         ← validator.mjs (+ generated-validators wrapper semantics)
src/Archify.Net.Core/Shared/EngineeringProfiles.cs     ← engineering-profiles.mjs
src/Archify.Net.Core/Shared/DiagramLoader.cs           ← cli.mjs loadDiagram gates (in-memory), validateRelationshipIds, validateCrossCollectionContracts
src/Archify.Net.Core/Shared/SvgAttrs.cs                ← cli.mjs svgRootAttrs, svgAccessibleText, animateAttr, focusNodeAttrs, focusNodeTitle, focusEdgeAttrs
src/Archify.Net.Core/Shared/Geometry.cs                ← geometry.mjs
src/Archify.Net.Core/Shared/SpatialGrid.cs             ← spatial-grid.mjs
src/Archify.Net.Core/Shared/RouteQuality.cs            ← route-quality.mjs
src/Archify.Net.Core/Shared/AutomaticLabels.cs         ← automatic-labels.mjs
src/Archify.Net.Core/Shared/TextFit.cs                 ← text-fit.mjs
src/Archify.Net.Core/Shared/Legend.cs                  ← legend.mjs
src/Archify.Net.Core/Shared/NodeCardStyle.cs           ← node-card-style.mjs
src/Archify.Net.Core/Shared/LayoutReport.cs            ← layout-report.mjs
src/Archify.Net.Core/Shared/DesktopReadability.cs      ← desktop-readability.mjs
src/Archify.Net.Core/Shared/BrandMarks.cs              ← brand-marks.mjs (offline subset)
src/Archify.Net.Core/Shared/SvgPathAnalysis.cs         ← svg-path-analysis.mjs
src/Archify.Net.Core/Shared/ArtifactChecker.cs         ← UP/scripts/check-render-output.mjs
src/Archify.Net.Core/Architecture/Grid.cs              ← architecture/grid.mjs
src/Archify.Net.Core/Architecture/Routing.cs           ← architecture/routing.mjs
src/Archify.Net.Core/Architecture/Labels.cs            ← architecture/labels.mjs (2 lines)
src/Archify.Net.Core/Architecture/ArchitectureRenderer.cs ← architecture/render-architecture.mjs
tests/Archify.Net.Core.Tests/Archify.Net.Core.Tests.csproj
tests/Archify.Net.Core.Tests/Fixtures.cs               paths to repo root, upstream/, goldens, fixtures
tests/Archify.Net.Core.Tests/JsTests.cs
tests/Archify.Net.Core.Tests/DiagramLoaderTests.cs
tests/Archify.Net.Core.Tests/I18nTests.cs
tests/Archify.Net.Core.Tests/TemplateWriterTests.cs
tests/Archify.Net.Core.Tests/ValidationTests.cs
tests/Archify.Net.Core.Tests/GeometryTests.cs
tests/Archify.Net.Core.Tests/BrandMarkTests.cs
tests/Archify.Net.Core.Tests/ParityTests.cs
tests/Archify.Net.Core.Tests/ArtifactCheckerTests.cs
tests/fixtures/js/*.json                           Node-captured expected values
tests/fixtures/diagrams/*.json                     synthetic inputs (no-profile, remote brand)
tests/goldens/*.html                               Node-minted goldens for examples lacking one
tests/goldens/checker/*.json                       Node checker output per golden
tools/capture-fixtures.mjs                         dev-time Node script (reads UP/, writes tests/fixtures + tests/goldens)
upstream/UPSTREAM.md
upstream/{schemas,examples,locales,brand-marks,references,viewer}/…, upstream/assets/template.html,
upstream/generated-brand-marks.json
```

---

### Task 1: Scaffold, upstream copy, embedded resources

**Files:**
- Create: `Archify.Net.sln`, `Directory.Build.props`, `.gitattributes`, `src/Archify.Net.Core/Archify.Net.Core.csproj`, `src/Archify.Net.Core/Resources.cs`, `src/Archify.Net.Core/DiagramType.cs`, `tests/Archify.Net.Core.Tests/Archify.Net.Core.Tests.csproj`, `tests/Archify.Net.Core.Tests/Fixtures.cs`, `tests/Archify.Net.Core.Tests/ResourcesTests.cs`, `upstream/UPSTREAM.md`, `upstream/**` (copied)

**Interfaces:**
- Produces: `Archify.Resources.Text(string path)` → `string` (UTF-8, path like `"assets/template.html"`, `"schemas/common.schema.json"`, `"locales/en.json"`, `"generated-brand-marks.json"`); `Archify.Resources.Exists(string path)`; `Archify.DiagramType` enum with `.Name()` extension (`"architecture"`…) and `DiagramTypes.Parse(string)`; test helper `Fixtures.RepoRoot`, `Fixtures.Upstream(string rel)`, `Fixtures.Golden(string name)`, `Fixtures.Fixture(string rel)`.

- [ ] **Step 1: `.gitattributes` first** (before any upstream byte lands)

```
# Byte-exact oracles and upstream data: never normalise line endings.
upstream/**        -text
tests/goldens/**   -text
tests/fixtures/**  -text
*.cs               text eol=crlf
*.csproj           text eol=crlf
*.md               text
```

- [ ] **Step 2: Copy upstream** (Bash; `UP=/c/projects/archify/archify`, `R=/c/projects/Archify.Net`)

```bash
cd "/c/projects/Archify.Net" && mkdir -p upstream/assets
cp -r /c/projects/archify/archify/schemas /c/projects/archify/archify/examples /c/projects/archify/archify/locales /c/projects/archify/archify/brand-marks /c/projects/archify/archify/references upstream/
cp /c/projects/archify/archify/assets/template.html upstream/assets/
cp -r /c/projects/archify/viewer upstream/viewer
cp /c/projects/archify/archify/LICENSE upstream/LICENSE
# Lift the brand array to pure JSON (array literal between the first '[' after 'BRAND_MARKS' and the closing ']);')
node -e "const s=require('fs').readFileSync('/c/projects/archify/archify/renderers/shared/generated-brand-marks.mjs','utf8');const a=s.indexOf('Object.freeze(')+'Object.freeze('.length;const b=s.lastIndexOf(']);')+1;require('fs').writeFileSync('upstream/generated-brand-marks.json',s.slice(a,b));"
node -e "const a=JSON.parse(require('fs').readFileSync('upstream/generated-brand-marks.json','utf8'));if(a.length!==107)throw new Error('expected 107 marks, got '+a.length);console.log('ok',a.length)"
sha256sum /c/projects/archify/archify/assets/template.html upstream/assets/template.html
```
Expected: `ok 107`; the two sha256 lines are identical.

- [ ] **Step 3: `upstream/UPSTREAM.md`**

```markdown
# Upstream snapshot
Source: https://github.com/tt-a1i/archify — commit c21c31ff (release 3.1.0), local checkout C:\projects\archify, copied 2026-10-13.
Contents are verbatim data: schemas/, examples/, locales/, brand-marks/, references/, assets/template.html, viewer/, LICENSE (MIT),
generated-brand-marks.json (the array literal from renderers/shared/generated-brand-marks.mjs).
Never edit by hand. To update: re-copy per the plan Task 1 Step 2, re-run the parity suite, port deltas.
```

- [ ] **Step 4: `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>false</InvariantGlobalization>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: `src/Archify.Net.Core/Archify.Net.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Archify.Net</RootNamespace>
    <AssemblyName>Archify.Net.Core</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="JsonSchema.Net" Version="9.4.0" />
  </ItemGroup>
  <ItemGroup>
    <!-- Embedded with forward-slash logical names: "schemas/common.schema.json", "assets/template.html", ... -->
    <EmbeddedResource Include="..\..\upstream\schemas\*.json" LogicalName="schemas/%(Filename)%(Extension)" />
    <EmbeddedResource Include="..\..\upstream\locales\*.json" LogicalName="locales/%(Filename)%(Extension)" />
    <EmbeddedResource Include="..\..\upstream\assets\template.html" LogicalName="assets/template.html" />
    <EmbeddedResource Include="..\..\upstream\generated-brand-marks.json" LogicalName="generated-brand-marks.json" />
    <EmbeddedResource Include="..\..\upstream\brand-marks\catalog.json" LogicalName="brand-marks/catalog.json" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: `Resources.cs`**

```csharp
using System.Reflection;
using System.Text;

namespace Archify.Net;

public static class Resources
{
    static readonly Assembly Asm = typeof(Resources).Assembly;
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static bool Exists(string path) => Asm.GetManifestResourceInfo(path) is not null;

    public static string Text(string path)
    {
        using var s = Asm.GetManifestResourceStream(path)
            ?? throw new FileNotFoundException($"Embedded resource {path} missing");
        using var r = new StreamReader(s, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        return r.ReadToEnd();
    }
}
```

- [ ] **Step 7: `DiagramType.cs`**

```csharp
namespace Archify.Net;

public enum DiagramType { Architecture, Workflow, Sequence, Dataflow, Lifecycle, Erd, Class, Tree, Timeline, Waterfall }

public static class DiagramTypes
{
    public static string Name(this DiagramType t) => t.ToString().ToLowerInvariant();
    public static DiagramType Parse(string name) => name switch
    {
        "architecture" => DiagramType.Architecture, "workflow" => DiagramType.Workflow, "sequence" => DiagramType.Sequence,
        "dataflow" => DiagramType.Dataflow, "lifecycle" => DiagramType.Lifecycle, "erd" => DiagramType.Erd,
        "class" => DiagramType.Class, "tree" => DiagramType.Tree, "timeline" => DiagramType.Timeline, "waterfall" => DiagramType.Waterfall,
        _ => throw new ArgumentException($"unknown diagram type \"{name}\"", nameof(name)),
    };
    // cli.mjs SEMANTIC_COLLECTIONS / RELATIONSHIP_COLLECTIONS
    public static string SemanticCollection(this DiagramType t) => t switch
    {
        DiagramType.Architecture => "components", DiagramType.Workflow => "nodes", DiagramType.Sequence => "participants",
        DiagramType.Dataflow => "nodes", DiagramType.Lifecycle => "states", DiagramType.Erd => "entities", DiagramType.Tree => "nodes",
        DiagramType.Class => "types", DiagramType.Timeline => "events", DiagramType.Waterfall => "spans", _ => throw new UnreachableException(),
    };
    public static string? RelationshipCollection(this DiagramType t) => t switch
    {
        DiagramType.Architecture => "connections", DiagramType.Workflow => "edges", DiagramType.Sequence => "messages",
        DiagramType.Dataflow => "flows", DiagramType.Lifecycle => "transitions", DiagramType.Erd => "relationships",
        DiagramType.Class => "relationships", _ => null,
    };
}
```

- [ ] **Step 8: Test project**

`tests/Archify.Net.Core.Tests/Archify.Net.Core.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><IsPackable>false</IsPackable><RootNamespace>Archify.Net.Tests</RootNamespace></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
  <ItemGroup><ProjectReference Include="..\..\src\Archify.Net.Core\Archify.Net.Core.csproj" /></ItemGroup>
</Project>
```

`Fixtures.cs`:
```csharp
namespace Archify.Net.Tests;

public static class Fixtures
{
    // tests/Archify.Net.Core.Tests/bin/<cfg>/net10.0 → repo root is 5 levels up
    public static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    public static string Upstream(string rel) => Path.Combine(RepoRoot, "upstream", rel.Replace('/', Path.DirectorySeparatorChar));
    public static string Golden(string name) => Path.Combine(RepoRoot, "tests", "goldens", name);
    public static string Fixture(string rel) => Path.Combine(RepoRoot, "tests", "fixtures", rel.Replace('/', Path.DirectorySeparatorChar));
    public static byte[] Bytes(string path) => File.ReadAllBytes(path);
    public static string Lf(string path) => File.ReadAllText(path, new System.Text.UTF8Encoding(false));
}
```

`ResourcesTests.cs`:
```csharp
using Xunit;
namespace Archify.Net.Tests;

public class ResourcesTests
{
    [Fact]
    public void Template_resource_equals_upstream_file_bytes()
    {
        var embedded = Resources.Text("assets/template.html");
        var onDisk = Fixtures.Lf(Fixtures.Upstream("assets/template.html"));
        Assert.Equal(onDisk, embedded);
        Assert.Contains("<!-- ARCHIFY:SVG_SLOT_START -->", embedded);
    }

    [Theory]
    [InlineData("schemas/common.schema.json")] [InlineData("schemas/architecture.schema.json")]
    [InlineData("locales/manifest.json")] [InlineData("locales/en.json")] [InlineData("generated-brand-marks.json")]
    public void Named_resources_exist(string path) => Assert.True(Resources.Exists(path));

    [Fact]
    public void DiagramType_names_round_trip()
    {
        foreach (var t in Enum.GetValues<DiagramType>()) Assert.Equal(t, DiagramTypes.Parse(t.Name()));
        Assert.Equal("components", DiagramType.Architecture.SemanticCollection());
        Assert.Null(DiagramType.Tree.RelationshipCollection());
    }
}
```

- [ ] **Step 9: Solution, build, test**

```powershell
dotnet new sln -n Archify
dotnet sln add src/Archify.Net.Core/Archify.Net.Core.csproj tests/Archify.Net.Core.Tests/Archify.Net.Core.Tests.csproj
dotnet test
```
Expected: `Passed! - Failed: 0, Passed: 7`. Check the unpiped exit code is 0.

- [ ] **Step 10: Verify line endings survived**: `git add -N . ; git diff --stat --ignore-cr-at-eol -- upstream | tail -1` must show nothing CR-related; `file upstream/assets/template.html` → "with LF line terminators" (not CRLF). Also `sha256sum` again as in Step 2.

- [ ] **Step 11: Commit** — `git add .gitattributes Archify.Net.sln Directory.Build.props src tests upstream` then `git commit -F <msgfile>` with `chore: scaffold Archify.Net.Core + tests, snapshot upstream 3.1.0 data (c21c31ff)`.

---

### Task 2: Node fixture capture (dev-time oracle minting)

**Files:**
- Create: `tools/capture-fixtures.mjs`, `tests/fixtures/js/numbers.json`, `tests/fixtures/js/sort.json`, `tests/fixtures/js/stringify.json`, `tests/fixtures/js/text-units.json`, `tests/fixtures/js/geometry.json`, `tests/fixtures/js/strings.json`, `tests/fixtures/diagrams/web-app.no-profile.architecture.json`, `tests/fixtures/diagrams/remote-brand.architecture.json`, `tests/goldens/*.html` (architecture: `production-deployment`, `brand-aware-delivery`, `checkout-platform.base`, `checkout-platform.head`, `web-app.no-profile`), `tests/goldens/checker/*.json`

**Interfaces:**
- Produces: JSON fixture files consumed by Tasks 3, 7, 9, 10. Format per file documented in the script header. Re-runnable: `node tools/capture-fixtures.mjs` (needs `C:\projects\archify` checkout; CI never runs it).

- [ ] **Step 1: Write `tools/capture-fixtures.mjs`**

```js
#!/usr/bin/env node
// Dev-time only. Captures V8 behaviour and upstream renderer output as fixtures for the C# parity suite.
// Usage: node tools/capture-fixtures.mjs   (from repo root; needs C:\projects\archify at c21c31ff)
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const UP = 'C:/projects/archify/archify';
const ROOT = path.resolve(fileURLToPath(import.meta.url), '..', '..');
const out = (rel) => path.join(ROOT, 'tests', rel);
const write = (rel, value) => { fs.mkdirSync(path.dirname(out(rel)), { recursive: true }); fs.writeFileSync(out(rel), JSON.stringify(value, null, 2) + '\n'); };
const imp = (rel) => import(pathToFileURL(path.join(UP, rel)).href);

// --- numbers: Number→string, Math.round, toFixed, toPrecision, en-US toLocaleString ---
const nums = [0, -0, 1, -1, 0.1, 0.5, 1.5, 2.5, -2.5, 0.49999999999999994, 1.005, 123.456, 1e21, 1e20, 999999999999999999999,
  1e-7, 0.000001, 0.0000001, 123456789.123, 5e-324, 1.7976931348623157e308, 100, 33.333333333333336, 0.30000000000000004,
  3.3, 7.1, 12.75, 0.1 + 0.2, 1 / 3, 2 / 3, 1234.5678, 0.00012345, 98765.4321, 1234567.891];
write('fixtures/js/numbers.json', nums.map((n) => ({
  value: Object.is(n, -0) ? '-0' : n,           // -0 cannot survive JSON; C# test reconstructs it
  str: String(n), round: Math.round(n), fixed2: n.toFixed(2), fixed6: n.toFixed(6), prec3: n.toPrecision(3),
  loc3sig: n.toLocaleString('en-US', { maximumSignificantDigits: 3 }),
  loc2frac: n.toLocaleString('en-US', { maximumFractionDigits: 2 }),
})));

// --- sort: default (string) sort and stability ---
const words = ['b', 'a', 'B', 'A', 'ä', 'z', '10', '9', '1', 'node-10', 'node-9', 'Zebra', 'apple', '', ' ', 'é', 'e'];
write('fixtures/js/sort.json', {
  defaultSorted: [...words].sort(),
  entriesSorted: Object.entries({ b: 2, a: 10, 'a,1': 0, c: '1', 10: 'x', 9: 'y' }).sort(),   // "key,value" string form
  localeCompare: words.map((a) => words.map((b) => a.localeCompare(b))),
  stable: [{ k: 1, v: 'a' }, { k: 0, v: 'b' }, { k: 1, v: 'c' }, { k: 0, v: 'd' }, { k: 1, v: 'e' }].sort((x, y) => x.k - y.k).map((o) => o.v),
});

// --- stringify: escaping of control chars, <>&, non-ASCII, lone surrogates, nesting, indent ---
const samples = ['plain', 'a"b\\c', '\u0000\u001f\u007f', '<script>&\'', 'é漢字😀', '\ud800 lone', 'line\nbreak\ttab\r\b\f',
  { b: 1, a: [1.0, 1e2, -0, 0.1, null, true], 10: 'x', 2: 'y', nested: { z: 'ok', y: [] } }];
write('fixtures/js/stringify.json', samples.map((s) => ({ input: s, compact: JSON.stringify(s), indented: JSON.stringify(s, null, 2) })));

// --- strings: trim, \s, replace-first with $& ---
const ws = ['\ufeffx\ufeff', '\u0085x\u0085', ' x ', '\u00a0x\u00a0', '\u2028x', '\tx\n', '\u3000x'];
write('fixtures/js/strings.json', { trim: ws.map((s) => [s, s.trim()]), isSpaceRegex: ws.map((s) => [s, /^\s/.test(s)]),
  replaceFirst: ['a-b-c'.replace('-', () => '$&'), 'a-b-c'.replace('-', '$&$&')] });

// --- text units ---
const { textUnits, renderSemanticSigil, renderDefinitions } = await imp('renderers/shared/utils.mjs');
const texts = ['', 'ascii', 'Ünïcödé', '漢字', 'ｶﾅ halfwidth', '한글', '✅ done', '⚡', 'e\ufe0f', '漢\ufe0e', '😀', '👨‍👩‍👧', '\ufe0f', 'a\u200db'];
write('fixtures/js/text-units.json', texts.map((t) => ({ text: t, units: textUnits(t) })));
write('fixtures/js/sigils.json', { defs: renderDefinitions(), defsExtra: renderDefinitions('<x/>'),
  sigils: ['frontend', 'backend', 'database', 'cloud', 'security', 'messagebus', 'external', 'start', 'decision', 'unknown'].map((k) =>
    ({ kind: k, svg: renderSemanticSigil(k, { x: 10.5, y: 3 }), iconClock: renderSemanticSigil(k, { x: 0, y: 0, icon: 'clock' }), none: renderSemanticSigil(k, { x: 0, y: 0, icon: 'none' }) })) });

// --- geometry: pure functions with representative inputs (signatures from geometry.mjs:28-84, 1351, 1818-1839) ---
const g = await imp('renderers/shared/geometry.mjs');
const tf = await imp('renderers/shared/text-fit.mjs');
const rects = [{ x: 0, y: 0, width: 100, height: 50 }, { x: 50, y: 25, width: 100, height: 50 }, { x: 200, y: 0, width: 10, height: 10 }];
const segs = [[[0, 0], [300, 60]], [[10, 10], [10, 40]], [[-5, 25], [150, 25]], [[0, 0], [0, 0]], [[100, 0], [100, 50]]];
const polylines = [[[0, 0], [100, 0], [100, 100]], [[10, 10], [10, 10], [50, 10], [50, 90.5]], [[0, 0], [33.3333, 66.6667], [100, 0]],
  [[0, 0], [40, 0], [40, 40], [80, 40], [80, 80]], [[12.345678, 0.1], [12.345678, 99.9]]];
write('fixtures/js/geometry.json', {
  rectsOverlap: rects.flatMap((a) => rects.flatMap((b) => [[a, b, 0, g.rectsOverlap(a, b)], [a, b, 10, g.rectsOverlap(a, b, 10)]])),
  segmentIntersectsRect: segs.flatMap((s) => rects.flatMap((r) => [[s, r, 0, g.segmentIntersectsRect(s, r)], [s, r, 4, g.segmentIntersectsRect(s, r, 4)]])),
  segmentRectClearance: segs.flatMap((s) => rects.map((r) => [s, r, g.segmentRectClearance(s, r)])),
  polylinePath: polylines.map((p) => [p, g.polylinePath(p)]),
  roundedPath: polylines.flatMap((p) => [[p, 8, g.roundedPath(p, 8)], [p, 3.5, g.roundedPath(p, 3.5)]]),
  normalizeRoutePoints: polylines.map((p) => [p, g.normalizeRoutePoints(p)]),
  routePointsValue: polylines.map((p) => [p, g.routePointsValue(p)]),
  fittedNodeFontSize: texts.map((t) => [t, tf.fittedNodeFontSize(t, 120, 9, 6), tf.minimumNodeTextWidth(t, 6), tf.availableNodeTextWidth(120)]),
});
// shortestOrthogonalGridRoute / labelPoint / createRouter take large option objects; architecture goldens exercise them end to end.

// --- goldens for examples without one + synthetic fixtures (architecture only in phase 1) ---
const render = (type, input, output) => execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), input, output], { stdio: 'inherit' });
const EX = path.join(UP, 'examples');
fs.mkdirSync(out('goldens/checker'), { recursive: true });
const noProfile = JSON.parse(fs.readFileSync(path.join(EX, 'web-app.architecture.json'), 'utf8'));
delete noProfile.meta.quality_profile; noProfile.meta.output = 'web-app.no-profile.html';
write('fixtures/diagrams/web-app.no-profile.architecture.json', noProfile);
const remoteBrand = JSON.parse(fs.readFileSync(path.join(EX, 'brand-aware-delivery.architecture.json'), 'utf8'));
remoteBrand.components[0].brand = { url: 'https://example.invalid/logo.svg', sha256: 'a'.repeat(64) };
remoteBrand.meta.output = 'remote-brand.html';
write('fixtures/diagrams/remote-brand.architecture.json', remoteBrand);
const targets = [
  ['architecture', path.join(EX, 'production-deployment.architecture.json'), 'production-deployment-rendered.html'],
  ['architecture', path.join(EX, 'brand-aware-delivery.architecture.json'), 'brand-aware-delivery-rendered.html'],
  ['architecture', path.join(EX, 'checkout-platform.base.architecture.json'), 'checkout-platform.base-rendered.html'],
  ['architecture', path.join(EX, 'checkout-platform.head.architecture.json'), 'checkout-platform.head-rendered.html'],
  ['architecture', out('fixtures/diagrams/web-app.no-profile.architecture.json'), 'web-app.no-profile-rendered.html'],
];
for (const [type, input, golden] of targets) render(type, input, out(`goldens/${golden}`));
// Node checker output for every golden (upstream 15 + ours); strip the absolute `file` so it is path-independent.
const upstreamGoldens = fs.readdirSync(EX).filter((f) => f.endsWith('.html')).map((f) => path.join(EX, f));
for (const html of [...upstreamGoldens, ...targets.map(([, , g]) => out(`goldens/${g}`))]) {
  const res = JSON.parse(execFileSync(process.execPath, [path.join(UP, 'scripts/check-render-output.mjs'), html], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
  delete res.file;
  write(`goldens/checker/${path.basename(html, '.html')}.json`, res);
}
// Remote brand: upstream has no "offline" switch; `example.invalid` never resolves, so upstream reports
// brand/capture-unavailable ("could not reproduce the pinned capture"). Capture that receipt as the C# oracle.
try {
  execFileSync(process.execPath, [path.join(UP, 'renderers/architecture/render-architecture.mjs'), out('fixtures/diagrams/remote-brand.architecture.json'), out('goldens/remote-brand.html')],
    { env: { ...process.env, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
  throw new Error('remote brand unexpectedly rendered — the fixture URL must not resolve');
} catch (e) { if (!e.stderr) throw e; write('fixtures/diagrams/remote-brand.expected.json', JSON.parse(String(e.stderr))); }
console.log('fixtures captured');
```

- [ ] **Step 2: Run it**: `node tools/capture-fixtures.mjs` → `fixtures captured`. Then `ls tests/goldens tests/goldens/checker tests/fixtures/js` — 5 HTML goldens, 20 checker JSONs, 7 js fixture files. `tests/fixtures/diagrams/remote-brand.expected.json` must contain a diagnostic with `"code": "brand/capture-unavailable"` (brand-marks.mjs `prepareDiagramBrandMarks`, the "could not reproduce the pinned capture" branch). If it contains `brand/unknown` instead, the fixture mutated the wrong field — `components[0]` must carry `brand: { url, sha256 }`.

- [ ] **Step 3: Sanity**: `node -e "const a=require('./tests/fixtures/js/numbers.json');console.log(a.find(x=>x.value===1e21).str, a.find(x=>x.value===2.5).round, a.find(x=>x.value===1.005).fixed2)"` → `1e+21 3 1.00`.

- [ ] **Step 4: Commit** — `git add tools/capture-fixtures.mjs tests/fixtures tests/goldens` → `test: capture V8 fixtures and mint architecture goldens from upstream`.

---

### Task 3: `Js` parity layer + `JsMap`/`JsSet`

**Files:**
- Create: `src/Archify.Net.Core/Js/Js.cs`, `src/Archify.Net.Core/Js/JsMap.cs`, `src/Archify.Net.Core/Js/JsSet.cs`, `tests/Archify.Net.Core.Tests/JsTests.cs`

**Interfaces:**
- Produces (all `public static` in `Archify.Js`):
  - `string Num(double v)` — V8 `Number.prototype.toString()`
  - `double Round(double v)` — `Math.round`
  - `string ToFixed(double v, int digits)`, `string ToPrecision(double v, int precision)`
  - `string ToLocaleStringEnUs(double v, int? maximumSignificantDigits = null, int? maximumFractionDigits = null)`
  - `double Hypot(double a, double b)`
  - `void Sort<T>(List<T> list, Comparison<T> cmp)` (stable), `void SortDefault(List<string> list)` (UTF-16 ordinal)
  - `int LocaleCompare(string a, string b)` (`CompareInfo` for `en`, `CompareOptions.None`; fixture decides sign only)
  - `string Stringify(JsonNode? v, int indent = 0)`
  - `string Trim(string s)`, `bool IsSpace(int codePoint)`
  - `string ReplaceFirst(string s, string search, Func<string, string> replacement)`
  - `string EscapeHtml(string? s)` (`& < > " '`)
  - `IEnumerable<string> Graphemes(string s)` (`StringInfo`), `IEnumerable<string> WordBreaks(string s)` (space/punct; CJK exempt)
  - `sealed class JsMap<TKey,TValue>` : `Set/Get/Has/Delete/Keys/Values/Entries/Count` insertion-ordered incl. delete+re-add; `sealed class JsSet<T>` : `Add/Has/Delete/Count`, enumerable in insertion order.

- [ ] **Step 1: Write failing fixture tests** (`JsTests.cs`)

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class JsTests
{
    static JsonArray Load(string f) => (JsonArray)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture($"js/{f}")))!;
    static double Val(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s == "-0" ? -0.0 : n.GetValue<double>();

    [Fact] public void Num_matches_V8() { foreach (var e in Load("numbers.json")) Assert.Equal(e!["str"]!.GetValue<string>(), Js.Num(Val(e["value"]!))); }
    [Fact] public void Round_matches_V8() { foreach (var e in Load("numbers.json")) Assert.Equal(e!["round"]!.GetValue<double>(), Js.Round(Val(e["value"]!))); }
    [Fact] public void ToFixed_matches_V8() { foreach (var e in Load("numbers.json")) { var v = Val(e!["value"]!); Assert.Equal(e["fixed2"]!.GetValue<string>(), Js.ToFixed(v, 2)); Assert.Equal(e["fixed6"]!.GetValue<string>(), Js.ToFixed(v, 6)); } }
    [Fact] public void ToPrecision_matches_V8() { foreach (var e in Load("numbers.json")) Assert.Equal(e!["prec3"]!.GetValue<string>(), Js.ToPrecision(Val(e["value"]!), 3)); }
    [Fact] public void ToLocaleString_enUS_matches_V8() { foreach (var e in Load("numbers.json")) { var v = Val(e!["value"]!);
        Assert.Equal(e["loc3sig"]!.GetValue<string>(), Js.ToLocaleStringEnUs(v, maximumSignificantDigits: 3));
        Assert.Equal(e["loc2frac"]!.GetValue<string>(), Js.ToLocaleStringEnUs(v, maximumFractionDigits: 2)); } }

    [Fact] public void SortDefault_is_utf16_ordinal()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/sort.json")))!;
        var words = new List<string> { "b", "a", "B", "A", "ä", "z", "10", "9", "1", "node-10", "node-9", "Zebra", "apple", "", " ", "é", "e" };
        Js.SortDefault(words);
        Assert.Equal(fx["defaultSorted"]!.AsArray().Select(x => x!.GetValue<string>()), words);
    }
    [Fact] public void Sort_is_stable()
    {
        var items = new List<(int k, string v)> { (1, "a"), (0, "b"), (1, "c"), (0, "d"), (1, "e") };
        Js.Sort(items, (x, y) => x.k - y.k);
        Assert.Equal(new[] { "b", "d", "a", "c", "e" }, items.Select(i => i.v));
    }
    [Fact] public void LocaleCompare_sign_matches_V8()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/sort.json")))!;
        var words = new[] { "b", "a", "B", "A", "ä", "z", "10", "9", "1", "node-10", "node-9", "Zebra", "apple", "", " ", "é", "e" };
        var m = fx["localeCompare"]!.AsArray();
        for (var i = 0; i < words.Length; i++) for (var j = 0; j < words.Length; j++)
            Assert.Equal(Math.Sign(m[i]![j]!.GetValue<int>()), Math.Sign(Js.LocaleCompare(words[i], words[j])));
    }
    [Fact] public void Stringify_matches_V8()
    {
        foreach (var e in Load("stringify.json"))
        {
            var input = e!["input"]?.DeepClone();
            Assert.Equal(e["compact"]!.GetValue<string>(), Js.Stringify(input));
            Assert.Equal(e["indented"]!.GetValue<string>(), Js.Stringify(input, 2));
        }
    }
    [Fact] public void Trim_and_IsSpace_match_V8()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/strings.json")))!;
        foreach (var p in fx["trim"]!.AsArray()) Assert.Equal(p![1]!.GetValue<string>(), Js.Trim(p[0]!.GetValue<string>()));
        foreach (var p in fx["isSpaceRegex"]!.AsArray()) Assert.Equal(p![1]!.GetValue<bool>(), Js.IsSpace(char.ConvertToUtf32(p[0]!.GetValue<string>(), 0)));
        Assert.Equal(fx["replaceFirst"]![0]!.GetValue<string>(), Js.ReplaceFirst("a-b-c", "-", _ => "$&"));
    }
    [Fact] public void EscapeHtml_only_five_chars() => Assert.Equal("&amp;&lt;&gt;&quot;&#39;+/é", Js.EscapeHtml("&<>\"'+/é"));
    [Fact] public void JsMap_keeps_insertion_order_across_delete_and_readd()
    {
        var m = new JsMap<string, int>(); m.Set("a", 1); m.Set("b", 2); m.Set("c", 3); m.Delete("a"); m.Set("a", 4);
        Assert.Equal(new[] { "b", "c", "a" }, m.Keys);
        var s = new JsSet<int>(); s.Add(3); s.Add(1); s.Add(2); s.Delete(3); s.Add(3);
        Assert.Equal(new[] { 1, 2, 3 }, s);
    }
    [Fact] public void Num_handles_raw_json_text_variants()
    {
        Assert.Equal("1", Js.Num(JsonNode.Parse("1.0")!.GetValue<double>()));
        Assert.Equal("100", Js.Num(JsonNode.Parse("1e2")!.GetValue<double>()));
    }
}
```

- [ ] **Step 2: Run** `dotnet test --filter JsTests` → compile failure (`Js` missing). Expected.

- [ ] **Step 3: Implement `Js.cs`** — key algorithms:

```csharp
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// ponytail: V8 parity — the only file allowed to know how JavaScript formats, sorts and escapes.
public static class Js
{
    // Number.prototype.toString(): shortest round-trip digits, then ES2023 §6.1.6.1.20 layout.
    public static string Num(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        if (v == 0) return "0";                                  // covers -0
        var neg = v < 0; if (neg) v = -v;
        var (digits, n) = ShortestDigits(v);                     // digits "123", n = position of decimal point (1e(n-1) ≤ v < 1e n)
        var k = digits.Length;
        string s;
        if (k <= n && n <= 21) s = digits + new string('0', n - k);
        else if (0 < n && n <= 21) s = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) s = "0." + new string('0', -n) + digits;
        else { var e = n - 1; var mant = k == 1 ? digits : digits[0] + "." + digits[1..]; s = mant + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e); }
        return neg ? "-" + s : s;
    }
    // "R" gives shortest round-trip in .NET Core 3.0+; parse mantissa/exponent out of its "E" form.
    static (string digits, int n) ShortestDigits(double v)
    {
        var r = v.ToString("R", CultureInfo.InvariantCulture);     // e.g. "1.5", "1E+21", "5E-324", "123456789.123"
        var ePos = r.IndexOf('E');
        var mant = ePos < 0 ? r : r[..ePos];
        var exp = ePos < 0 ? 0 : int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture);
        var dot = mant.IndexOf('.');
        var intPart = dot < 0 ? mant : mant[..dot];
        var frac = dot < 0 ? "" : mant[(dot + 1)..];
        var digits = (intPart + frac).TrimStart('0');
        var leadingZeros = (intPart + frac).Length - digits.Length;
        var n = intPart.Length + exp - leadingZeros;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) { digits = "0"; n = 1; }
        return (digits, n);
    }

    public static double Round(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v;
        var f = Math.Floor(v);
        var r = v - f >= 0.5 ? f + 1 : f;          // exact: 0.49999999999999994 → 0
        return r == 0 && v < 0 ? -0.0 : r;
    }

    // toFixed / toPrecision / toLocaleString work on the EXACT decimal value of the double (BigInteger ratio),
    // so 1.005.toFixed(2) === "1.00" (the double is 1.00499999…) exactly as V8 does.
    // Exact value of a finite double: v = m · 2^e → (numerator, denominator).
    static (BigInteger num, BigInteger den) Exact(double v)
    {
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(v));
        var e = (int)((bits >> 52) & 0x7FF); var m = bits & 0xFFFFFFFFFFFFFL;
        if (e == 0) e = 1; else m |= 1L << 52;
        e -= 1075;
        return e >= 0 ? (new BigInteger(m) << e, BigInteger.One) : (new BigInteger(m), BigInteger.One << -e);
    }
    // n = round(v · 10^f) with ties → larger n (ES toFixed/toPrecision: "if there are two such n, pick the larger n").
    static BigInteger ScaledHalfUp(double v, int f)
    {
        var (num, den) = Exact(v);
        if (f >= 0) num *= BigInteger.Pow(10, f); else den *= BigInteger.Pow(10, -f);
        var q = BigInteger.DivRem(num, den, out var rem);
        return rem * 2 >= den ? q + 1 : q;
    }
    public static string ToFixed(double v, int f)
    {   // ES2023 §21.1.3.3
        if (double.IsNaN(v)) return "NaN";
        if (Math.Abs(v) >= 1e21 || double.IsInfinity(v)) return Num(v);
        var neg = v < 0 || (v == 0 && double.IsNegative(v) && false);  // (-0).toFixed(2) === "0.00": no sign
        var n = ScaledHalfUp(v, f);
        var s = n.ToString(CultureInfo.InvariantCulture);
        if (f > 0) { s = s.PadLeft(f + 1, '0'); s = s[..^f] + "." + s[^f..]; }
        return v < 0 && n != 0 ? "-" + s : s;                 // (-0.001).toFixed(2) === "-0.00" in V8: sign kept when v < 0
    }
    public static string ToPrecision(double v, int p)
    {   // ES2023 §21.1.3.5
        if (double.IsNaN(v) || double.IsInfinity(v)) return Num(v);
        if (v == 0) return p == 1 ? "0" : "0." + new string('0', p - 1);
        var neg = v < 0; var a = Math.Abs(v);
        var e = (int)Math.Floor(Math.Log10(a));                 // may be off by one near powers of ten → correct below
        var n = ScaledHalfUp(a, p - 1 - e);
        if (n >= BigInteger.Pow(10, p)) { e++; n = ScaledHalfUp(a, p - 1 - e); }
        else if (n < BigInteger.Pow(10, p - 1)) { e--; n = ScaledHalfUp(a, p - 1 - e); }
        var digits = n.ToString(CultureInfo.InvariantCulture);
        string s;
        if (e < -6 || e >= p) s = (p == 1 ? digits : digits[0] + "." + digits[1..]) + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e);
        else if (e == p - 1) s = digits;
        else if (e >= 0) s = digits[..(e + 1)] + "." + digits[(e + 1)..];
        else s = "0." + new string('0', -(e + 1)) + digits;
        return neg ? "-" + s : s;
    }
    public static string ToLocaleStringEnUs(double v, int? maximumSignificantDigits = null, int? maximumFractionDigits = null)
    {   // Intl.NumberFormat('en-US', …): default roundingMode "halfExpand" (= half away from zero), minimumFractionDigits 0,
        // grouping ",", trailing fraction zeros stripped. Only the two option shapes upstream uses are supported.
        if (double.IsNaN(v)) return "NaN"; if (double.IsInfinity(v)) return v > 0 ? "∞" : "-∞";
        var neg = v < 0; var a = Math.Abs(v);
        int f;                                                    // number of fraction digits to round to
        if (maximumSignificantDigits is int sig)
        {
            if (a == 0) return "0";
            var e = (int)Math.Floor(Math.Log10(a));
            var probe = ScaledHalfUp(a, sig - 1 - e);
            if (probe >= BigInteger.Pow(10, sig)) e++;
            f = Math.Max(0, sig - 1 - e);
            if (sig - 1 - e < 0) { var n0 = ScaledHalfUp(a, sig - 1 - e) * BigInteger.Pow(10, e + 1 - sig); return (neg ? "-" : "") + Group(n0.ToString(CultureInfo.InvariantCulture)); }
        }
        else f = maximumFractionDigits ?? 3;                     // Intl default maximumFractionDigits = 3
        var n = ScaledHalfUp(a, f);
        var s = n.ToString(CultureInfo.InvariantCulture).PadLeft(f + 1, '0');
        var intPart = s[..^f]; var frac = f == 0 ? "" : s[^f..].TrimEnd('0');
        var result = Group(intPart) + (frac.Length > 0 ? "." + frac : "");
        return neg && n != 0 ? "-" + result : result;
    }
    static string Group(string intDigits)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < intDigits.Length; i++) { if (i > 0 && (intDigits.Length - i) % 3 == 0) sb.Append(','); sb.Append(intDigits[i]); }
        return sb.ToString();
    }

    public static double Hypot(double a, double b)
    {   // V8 (ieee754::hypot): scale by max, compensated sum, sqrt.
        a = Math.Abs(a); b = Math.Abs(b);
        if (double.IsInfinity(a) || double.IsInfinity(b)) return double.PositiveInfinity;
        if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
        var max = Math.Max(a, b); if (max == 0) return 0;
        var x = a / max; var y = b / max;
        double sum = 0, comp = 0;
        foreach (var t in new[] { x * x, y * y }) { var pre = sum; sum += t; comp += (sum - pre) - t; }   // Kahan as in V8 MathHypot
        return Math.Sqrt(sum - comp) * max;
    }

    public static void Sort<T>(List<T> list, Comparison<T> cmp)
    {   // stable: LINQ OrderBy is stable; comparators are key-based so any stable sort matches V8 TimSort.
        var sorted = list.OrderBy(x => x, Comparer<T>.Create(cmp)).ToList();
        list.Clear(); list.AddRange(sorted);
    }
    public static void SortDefault(List<string> list) => Sort(list, string.CompareOrdinal);   // UTF-16 code units
    static readonly CompareInfo En = CultureInfo.GetCultureInfo("en").CompareInfo;
    public static int LocaleCompare(string a, string b) => En.Compare(a, b, CompareOptions.None);

    public static string Stringify(JsonNode? v, int indent = 0) { var sb = new StringBuilder(); Write(sb, v, indent, 0); return sb.ToString(); }
    static void Write(StringBuilder sb, JsonNode? v, int indent, int depth)
    {   // JSON.stringify: objects in insertion order (integer-like keys would come first in V8; the schemas forbid them),
        // numbers via Num, non-finite → "null", strings via Quote. indent > 0 → ES "gap" layout.
        switch (v)
        {
            case null: sb.Append("null"); return;
            case JsonArray arr:
                if (arr.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (indent > 0) sb.Append('\n').Append(' ', indent * (depth + 1));
                    Write(sb, arr[i], indent, depth + 1);
                }
                if (indent > 0) sb.Append('\n').Append(' ', indent * depth);
                sb.Append(']'); return;
            case JsonObject obj:
                if (obj.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{'); var first = true;
                foreach (var (key, val) in obj)
                {
                    if (!first) sb.Append(','); first = false;
                    if (indent > 0) sb.Append('\n').Append(' ', indent * (depth + 1));
                    sb.Append(Quote(key)).Append(':'); if (indent > 0) sb.Append(' ');
                    Write(sb, val, indent, depth + 1);
                }
                if (indent > 0) sb.Append('\n').Append(' ', indent * depth);
                sb.Append('}'); return;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s)) { sb.Append(Quote(s)); return; }
                if (val.TryGetValue<bool>(out var b)) { sb.Append(b ? "true" : "false"); return; }
                var d = val.GetValue<double>();                 // DiagramLoader normalised every number to double
                sb.Append(double.IsFinite(d) ? Num(d) : "null"); return;
        }
    }
    // ES2019 well-formed JSON.stringify: " \ and control chars escaped (short forms for \b \f \n \r \t), lone surrogates as \udxxx,
    // everything else — including < > & and non-ASCII — literal.
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break; case '\f': sb.Append("\\f"); break; case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break; case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); }
                    else if (char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // WhiteSpace + LineTerminator per ES: TAB VT FF SP NBSP ZWNBSP(U+FEFF) USP(Zs) LF CR LS PS. Not U+0085.
    public static bool IsSpace(int cp) => cp is 0x09 or 0x0B or 0x0C or 0x20 or 0xA0 or 0xFEFF or 0x0A or 0x0D or 0x2028 or 0x2029
        || (cp != 0x85 && cp <= 0xFFFF && CharUnicodeInfo.GetUnicodeCategory((char)cp) == UnicodeCategory.SpaceSeparator);
    public static string Trim(string s) { int i = 0, j = s.Length; while (i < j && IsSpace(s[i])) i++; while (j > i && IsSpace(s[j - 1])) j--; return s[i..j]; }
    public static string ReplaceFirst(string s, string search, Func<string, string> replacement)
    { var at = s.IndexOf(search, StringComparison.Ordinal); return at < 0 ? s : string.Concat(s.AsSpan(0, at), replacement(search), s.AsSpan(at + search.Length)); }
    public static string EscapeHtml(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    public static IEnumerable<string> Graphemes(string s) { var e = StringInfo.GetTextElementEnumerator(s); while (e.MoveNext()) yield return e.GetTextElement(); }
    // Intl.Segmenter(word) stand-in: a segment is a maximal run of one class — letters/digits/connectors, whitespace, or a single other
    // code point. ICU additionally splits CJK runs by dictionary; this does not (spec: CJK wrapping is parity-exempt). Phase 2 fixtures pin Latin behaviour.
    public static IEnumerable<string> WordBreaks(string s)
    {
        static int Class(Rune r) => Rune.IsLetterOrDigit(r) || r.Value == '_' || r.Value == '\'' ? 0 : IsSpace(r.Value) ? 1 : 2;
        var start = 0; var runes = s.EnumerateRunes().ToList(); var pos = 0;
        for (var i = 0; i < runes.Count; i++)
        {
            var len = runes[i].Utf16SequenceLength;
            var breakHere = i > 0 && (Class(runes[i]) != Class(runes[i - 1]) || Class(runes[i]) == 2);
            if (breakHere) { yield return s[start..pos]; start = pos; }
            pos += len;
        }
        if (start < s.Length) yield return s[start..];
    }
}
```
`JsMap<TKey,TValue>`: `Dictionary<TKey, LinkedListNode<KeyValuePair<TKey,TValue>>>` + `LinkedList` — delete removes the node, re-add appends. `JsSet<T>` wraps `JsMap<T, byte>`. Keys compared with `EqualityComparer<TKey>.Default`; callers key by `string` or by reference objects — never by value-tuples/records (upstream keys Maps by object identity).

- [ ] **Step 4: Run** `dotnet test --filter JsTests` → all pass. If `ToLocaleString_enUS_matches_V8` fails on a half case, the fixture is the truth: adjust rounding mode (Intl default `halfExpand`), not the fixture.

- [ ] **Step 5: Commit** — `git add src/Archify.Net.Core/Js tests/Archify.Net.Core.Tests/JsTests.cs` → `feat(core): Js parity layer (V8 number formatting, stable sort, stringify, trim, JsMap/JsSet)`.

---

### Task 4: Diagnostics, RenderContext, DiagramLoader (JSON in-memory load)

**Files:**
- Create: `src/Archify.Net.Core/RenderOptions.cs`, `src/Archify.Net.Core/RenderResult.cs`, `src/Archify.Net.Core/RenderContext.cs`, `src/Archify.Net.Core/Shared/Diagnostics.cs`, `src/Archify.Net.Core/Js/JsonNodeExt.cs`, `src/Archify.Net.Core/Shared/DiagramLoader.cs` (load + normalise only; validation gates added in Task 6), `tests/Archify.Net.Core.Tests/DiagramLoaderTests.cs`

**Interfaces:**
- Produces:
  - `record Diagnostic(string Code, Severity Severity, string Message, JsonObject Subject, JsonObject Evidence, IReadOnlyList<string> SupportedFixes, IReadOnlyList<string>? Suppresses)`; `enum Severity { Error, Warning }`
  - `sealed class ArchifyDiagnosticException(string message, IReadOnlyList<Diagnostic> diagnostics) : Exception` — the internal `error.archifyDiagnostics` carrier; never escapes `Archify.*` facade.
  - `sealed class DiagnosticSink` (← diagnostics.mjs): `Record(Diagnostic)`, `T WithRecordingSuppressed<T>(Func<T>)`, `T WithIsolatedRecording<T>(Func<T>)`, `Exception ThrowDiagnosticError(string message, IEnumerable<Diagnostic>)` (returns the exception for `throw`), `Exception ThrowDiagnosticProblems(string prefix, IEnumerable<string> problems, string code = "layout/constraint", JsonObject? subject = null, IEnumerable<Diagnostic>? details = null)`, `IReadOnlyList<Diagnostic> Recorded`, `RendererFailure(Exception) → (string error, IReadOnlyList<Diagnostic> diagnostics)` (= `rendererFailure` without `schemaVersion/ok/source` envelope; facade adds). Recording is always on (upstream json mode — the path receipts come from).
  - `sealed class RenderContext(DiagramType type, JsonObject diagram, RenderOptions options)` with `Type`, `Diagram`, `Meta` (`Diagram["meta"]`), `Diagnostics` (sink), `Locale` (`LocaleCatalog`, Task 5), `Options`, `TextUnitsCache` (`Dictionary<string,int>`, bounded 4096 like utils.mjs), `Brands` (`BrandMarkResolver`, Task 8), `SourceEvidence` (`JsonNode?`, always `null` until phase 4's repository evidence).
  - `record RenderOptions(QualityProfile? Quality = null, string? RepoRoot = null, DiagnosticFormat DiagnosticFormat = DiagnosticFormat.Json)`; `enum QualityProfile { Standard, Showcase }`; `enum DiagnosticFormat { Text, Json }`.
  - `record RenderResult(string Html, string Svg, string Cards, IReadOnlyList<Diagnostic> Diagnostics, LayoutReport? Layout)`; `record CheckResult(bool Ok, JsonObject Report)`; `sealed class LayoutReport` (Task 7).
  - `static class J` (`JsonNodeExt.cs`): `string? Str(this JsonNode? n, string key)`, `double? Num(this JsonNode? n, string key)`, `JsonArray? Arr(this JsonNode? n, string key)`, `JsonObject? Obj(this JsonNode? n, string key)`, `bool? Bool(this JsonNode? n, string key)`, `IEnumerable<JsonNode?> AsArrayOrEmpty(this JsonNode? n)` (= `asArray`), `double D(this JsonNode n)` (any numeric JsonValue → double).
  - `static class DiagramLoader`: `JsonObject Parse(string json)` (duplicate keys → last wins; numbers → double), `JsonObject Normalize(JsonObject)`.

- [ ] **Step 1: Failing tests** (`DiagramLoaderTests.cs`)

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class DiagramLoaderTests
{
    [Fact] public void Duplicate_keys_last_wins()
    {
        var o = DiagramLoader.Parse("{\"meta\":{\"title\":\"a\",\"title\":\"b\"}}");
        Assert.Equal("b", o["meta"]!["title"]!.GetValue<string>());
    }
    [Fact] public void Numbers_normalised_to_double_text()
    {
        var o = DiagramLoader.Parse("{\"size\":[1.0,1e2],\"n\":-0,\"big\":1e21}");
        Assert.Equal("[1,100]", Js.Stringify(o["size"]));
        Assert.Equal("1e+21", Js.Stringify(o["big"]));
        Assert.Equal("0", Js.Stringify(o["n"]));   // JSON.stringify(-0) === "0"
    }
    [Fact] public void Key_order_is_insertion_order()
    {
        var o = DiagramLoader.Parse("{\"b\":1,\"a\":2,\"c\":{\"z\":1,\"y\":2}}");
        Assert.Equal("{\"b\":1,\"a\":2,\"c\":{\"z\":1,\"y\":2}}", Js.Stringify(o));
    }
    [Fact] public void Sink_dedupes_exact_repeats_and_keeps_first_by_message()
    {
        var sink = new DiagnosticSink();
        var d = new Diagnostic("layout/x", Severity.Error, "m", new JsonObject { ["id"] = "a" }, new JsonObject(), [], null);
        sink.Record(d); sink.Record(d); sink.Record(d with { Subject = new JsonObject { ["id"] = "b" } });
        Assert.Equal(2, sink.Recorded.Count);
        var ex = Assert.IsType<ArchifyDiagnosticException>(sink.ThrowDiagnosticProblems("Layout failed", ["m", "other"]));
        Assert.Equal("Layout failed:\n- m\n- other", ex.Message);
        Assert.Equal(["layout/x", "layout/constraint"], ex.Diagnostics.Select(x => x.Code));   // "m" reuses the recorded structured entry
    }
    [Fact] public void Isolated_recording_does_not_leak()
    {
        var sink = new DiagnosticSink();
        sink.WithIsolatedRecording(() => { sink.Record(new Diagnostic("x/y", Severity.Error, "inner", new(), new(), [], null)); return 0; });
        Assert.Empty(sink.Recorded);
    }
}
```

- [ ] **Step 2: Run** → compile failure. Expected.

- [ ] **Step 3: Implement.** `DiagramLoader.Parse`: `JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = true })` — if `.NET 10`'s `JsonObject` still throws on duplicate materialisation (the test tells you), fall back to `JsonDocument.Parse` + recursive rebuild that overwrites on duplicate. `Normalize`: walk; every numeric `JsonValue` → `JsonValue.Create(node.GetValue<double>())` (then `Js.Num` prints `1.0` as `1`). `Diagnostics.cs` is a line-for-line port of `UP/renderers/shared/diagnostics.mjs:1-120` (`normalizedDiagnostic` → `Diagnostic.Normalize(...)` with defaults `internal/unclassified`, severity error, message fallback `"Archify could not classify this failure."`, dedupe key `$"{code} {message} {Js.Stringify(subject)}"`); `installRendererDiagnosticBoundary` is **not** ported (process-level; the facade catches `ArchifyDiagnosticException` instead). `RendererFailure`: attached diagnostics non-empty → `Recorded.Count > 0 ? Recorded : attached`; else one `internal/unclassified` with `evidence.errorName = ex.GetType().Name`.

- [ ] **Step 4: Run** `dotnet test --filter DiagramLoaderTests` → pass.

- [ ] **Step 5: Commit** → `feat(core): diagnostics sink, RenderContext, JsonNode loader with V8 number semantics`.

---

### Task 5: I18n, Utils, TemplateWriter — oracle: re-assemble the golden

**Files:**
- Create: `src/Archify.Net.Core/Shared/I18n.cs`, `src/Archify.Net.Core/Shared/Utils.cs`, `src/Archify.Net.Core/Shared/TemplateWriter.cs`, `src/Archify.Net.Core/Shared/SvgAttrs.cs`, `tests/Archify.Net.Core.Tests/I18nTests.cs`, `tests/Archify.Net.Core.Tests/TemplateWriterTests.cs`

**Interfaces:**
- Produces:
  - `sealed class LocaleCatalog` (← i18n.mjs; per-context instance replacing `DOCUMENT_CATALOGS`): `static string DefaultLocale = "en"`; `static IReadOnlyList<string> SupportedLocales`; `LocaleReport Register(string? locale, JsonObject? translations)` (= `registerLocale`; `record LocaleReport(string? Locale, string ResolvedLocale, string? BundledLocale, bool Fallback, int TotalKeys, int TranslatedKeys, double Coverage, IReadOnlyList<string> FallbackKeys, OverrideReport Override)` with `record OverrideReport(int SuppliedKeys, int AppliedKeys, IReadOnlyList<string> UnknownKeys, IReadOnlyList<string> PlaceholderMismatches)` — field names from i18n.mjs:181-197); `IReadOnlyList<Diagnostic> LocaleDiagnostics(DiagramType type, JsonObject meta)`; `string ResolveLocale(string? locale)`; `string Translate(string? locale, string key, IReadOnlyDictionary<string,string>? values = null)` (throws `InvalidOperationException` "Missing Archify i18n message …" as upstream); `string TranslateCount(string? locale, string key, int count, ...)`; `JsonObject ViewerCatalog(string? locale)` (keys starting `viewer.`); `string LocalizeTemplate(string template, string? locale)`; static `string FormatMessage(string template, IReadOnlyDictionary<string,string>? values)`; static `ResolveCatalog(...)` pure.
  - `static class Utils`: `string Esc(string?)` (= `Js.EscapeHtml`), `string RenderDefinitions(string extra = "")`, `string RenderSemanticSigil(string kind, double x, double y, double size = 11, string? icon = null)`, `int TextUnits(string? text, RenderContext? ctx = null)` (cache lives on ctx when given), consts `SemanticSigilInset = 6, SemanticSigilSize = 11, SemanticSigilFootprint = 17, SourceBadgeFootprint = 38`, `string RenderCards(JsonArray? cards)`.
  - `static class TemplateWriter`: `string Apply(string template, string title, string? subtitle, string svg, string cards, string? locale, string visualPreset, JsonNode? sourceEvidence, LocaleCatalog catalog)` — exact port of `applyTemplate` (utils.mjs:150-200) incl. `serializeScriptJson` (`Js.Stringify` + `\u003c \u003e \u0026` replacements) and the five placeholder checks (throw `InvalidOperationException` with upstream messages).
  - `static class SvgAttrs` (← cli.mjs:360-429): `string SvgRootAttrs(RenderContext ctx)` (`data-quality-profile`, advisory flag — precedence `ctx.Options.Quality` > `meta.quality_profile`), `string SvgAccessibleText(RenderContext ctx, string kind)`, `string AnimateAttr(JsonObject meta, string kind, int step)`, `string FocusNodeAttrs(string id, string label, JsonObject? metadata, string? locale, RenderContext ctx)`, `string FocusNodeTitle(string label, JsonObject? metadata)`, `string FocusEdgeAttrs(string from, string to, string? label, string key, string? id)`.

- [ ] **Step 1: Failing tests**

`I18nTests.cs`:
```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class I18nTests
{
    [Fact] public void Supported_locales_match_manifest() => Assert.Equal(new[] { "en", "zh-CN", "zh-TW", "es", "ko" }, LocaleCatalog.SupportedLocales);
    [Fact] public void Unknown_locale_falls_back_to_en_and_reports()
    {
        var c = new LocaleCatalog();
        var r = c.Register("xx-YY", null);
        Assert.True(r.Fallback); Assert.Equal("en", c.ResolveLocale("xx-YY"));
    }
    [Fact] public void Case_insensitive_bundled_tag() { var c = new LocaleCatalog(); c.Register("zh-cn", null); Assert.Equal("zh-CN", c.ResolveLocale("zh-cn")); }
    [Fact] public void Translations_override_then_bundled_then_en()
    {
        var c = new LocaleCatalog();
        var enLegend = c.Translate("en", "legend.architecture.frontend");
        var es = JsonNode.Parse(Resources.Text("locales/es.json"))!.AsObject();
        c.Register("es", new JsonObject { ["page.title"] = "Mapa {title}" });
        Assert.Equal("Mapa X", c.Translate("es", "page.title", new Dictionary<string, string> { ["title"] = "X" }));   // override wins
        Assert.Equal(es["legend.architecture.frontend"]!.GetValue<string>(), c.Translate("es", "legend.architecture.frontend")); // bundled
        Assert.NotEqual(enLegend, c.Translate("es", "legend.architecture.frontend"));
        var r = c.Register("es", new JsonObject { ["page.title"] = "no placeholder" });                               // {title} missing
        Assert.Equal(1, r.Override.PlaceholderMismatches.Count);
    }
    [Fact] public void Missing_key_throws_like_upstream() { var c = new LocaleCatalog(); var ex = Assert.Throws<InvalidOperationException>(() => c.Translate("en", "no.such.key")); Assert.StartsWith("Missing Archify i18n message", ex.Message); }
    [Fact] public void Sigils_and_definitions_match_fixture()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/sigils.json")))!;
        Assert.Equal(fx["defs"]!.GetValue<string>(), Utils.RenderDefinitions());
        Assert.Equal(fx["defsExtra"]!.GetValue<string>(), Utils.RenderDefinitions("<x/>"));
        foreach (var s in fx["sigils"]!.AsArray())
        {
            var k = s!["kind"]!.GetValue<string>();
            Assert.Equal(s["svg"]!.GetValue<string>(), Utils.RenderSemanticSigil(k, 10.5, 3));
            Assert.Equal(s["iconClock"]!.GetValue<string>(), Utils.RenderSemanticSigil(k, 0, 0, icon: "clock"));
            Assert.Equal("", Utils.RenderSemanticSigil(k, 0, 0, icon: "none"));
        }
    }
    [Fact] public void TextUnits_matches_fixture()
    {
        foreach (var e in JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/text-units.json")))!.AsArray())
            Assert.Equal(e!["units"]!.GetValue<int>(), Utils.TextUnits(e["text"]!.GetValue<string>()));
    }
}
```

`TemplateWriterTests.cs` — the oracle: cut the SVG and cards slots out of the upstream golden, re-apply, expect identical bytes:
```csharp
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
namespace Archify.Net.Tests;

public class TemplateWriterTests
{
    static readonly Regex SvgSlot = new("      <!-- ARCHIFY:SVG_SLOT_START -->[\\s\\S]*?      <!-- ARCHIFY:SVG_SLOT_END -->");
    static readonly Regex CardsSlot = new("    <!-- ARCHIFY:CARDS_SLOT_START -->[\\s\\S]*?    <!-- ARCHIFY:CARDS_SLOT_END -->");

    [Theory]
    [InlineData("web-app.architecture.json", "web-app-rendered.html")]
    [InlineData("cache-miss-request.sequence.json", "sequence-cache-miss-request.html")]
    [InlineData("orders.erd.json", "erd-orders-rendered.html")]
    public void Reassembling_golden_slots_reproduces_golden(string input, string golden)
    {
        var html = Fixtures.Lf(Fixtures.Upstream($"examples/{golden}"));
        var diagram = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream($"examples/{input}")));
        var meta = diagram["meta"]!.AsObject();
        var svg = SvgSlot.Match(html).Value; var cards = CardsSlot.Match(html).Value;
        Assert.False(string.IsNullOrEmpty(svg)); Assert.False(string.IsNullOrEmpty(cards));
        var catalog = new LocaleCatalog(); catalog.Register(meta.Str("locale"), meta.Obj("translations"));
        var result = TemplateWriter.Apply(Resources.Text("assets/template.html"), meta.Str("title")!, meta.Str("subtitle"),
            svg, cards, meta.Str("locale"), meta.Str("visual_preset") ?? "classic", null, catalog);
        Assert.Equal(html, result);
    }

    [Fact] public void Dollar_patterns_in_title_are_literal()
    {
        var catalog = new LocaleCatalog();
        var r = TemplateWriter.Apply(Resources.Text("assets/template.html"), "Cost $& $' $$", null, "      <!-- ARCHIFY:SVG_SLOT_START -->\n      <svg/>\n      <!-- ARCHIFY:SVG_SLOT_END -->", "    <!-- ARCHIFY:CARDS_SLOT_START -->\n    <!-- ARCHIFY:CARDS_SLOT_END -->", null, "classic", null, catalog);
        Assert.Contains("<h1>Cost $&amp; $&#39; $$</h1>", r);
    }

    [Fact] public void RenderCards_matches_upstream_layout()
    {
        var cards = (JsonArray)JsonNode.Parse("[{\"title\":\"T\",\"dot\":\"frontend\",\"items\":[\"a<b\",\"c\"]}]")!;
        Assert.Equal("    <!-- Info Cards -->\n    <div class=\"cards\">\n      <div class=\"card\">\n        <div class=\"card-header\">\n          <div class=\"card-dot frontend\"></div>\n          <h3>T</h3>\n        </div>\n        <ul>\n          <li>a&lt;b</li>\n          <li>c</li>\n        </ul>\n      </div>\n    </div>", Utils.RenderCards(cards));
    }
}
```

- [ ] **Step 2: Run** → compile failure.

- [ ] **Step 3: Port** `UP/renderers/shared/i18n.mjs` (328 lines) → `I18n.cs`: manifest/catalog loading from `Resources` (validate `LOCALE_TAG` `^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$`, `CATALOG_FILE`), `CANONICAL_KEYS` from `en.json` in file order, `classifyTranslations` (placeholder-mismatch detection), `resolveCatalog`, `bundledLocaleFor` (case-insensitive), `localeDiagnostics` (codes `locale/…` — copy exactly from lines 231-292), `formatMessage` regex `\{([a-zA-Z0-9_]+)\}`. `Utils.cs` ← utils.mjs lines 1-149 + 201-260 verbatim strings (the `renderDefinitions` block and `SIGIL_SHAPE` map are copy-paste; keep the exact whitespace inside template literals including the 12-space indents). `TextUnits`: `FULLWIDTH_RE` becomes a sorted range table over code points (ranges copied from utils.mjs:206); iterate by code point (`Rune`), apply VS rules. `TemplateWriter.Apply` ← utils.mjs:150-200 using `Regex` with the three exact patterns (`SUBTITLE_SLOT_RE` is multiline `^([ \t]*)<p class="subtitle">\[Subtitle description\]</p>[ \t]*(\r?\n)?`) and `Js.ReplaceFirst` for the string placeholders. `SvgAttrs.cs` ← cli.mjs:360-429.

- [ ] **Step 4: Run** `dotnet test --filter "I18nTests|TemplateWriterTests"` → pass. The reassembly test failing means a byte differs in template handling — diff the first differing line (print index) before touching anything.

- [ ] **Step 5: Commit** → `feat(core): i18n catalogs, utils, template writer — golden slot reassembly is byte-exact`.

---

### Task 6: Validation gates (schema, portable path, output path, engineering profile, cross-collection ids)

**Files:**
- Create: `src/Archify.Net.Core/Shared/PortablePath.cs`, `src/Archify.Net.Core/Shared/OutputPath.cs`, `src/Archify.Net.Core/Shared/SchemaValidator.cs`, `src/Archify.Net.Core/Shared/EngineeringProfiles.cs`, `tests/Archify.Net.Core.Tests/ValidationTests.cs`
- Modify: `src/Archify.Net.Core/Shared/DiagramLoader.cs` (add `Load(DiagramType, JsonObject, RenderContext)` running the gates in cli.mjs:79-85 order)

**Interfaces:**
- Produces:
  - `static class PortablePath`: `void Validate(string value, string profile = "output")` throws `PortablePathException(reason)` (← portable-path.mjs:72); `ValidateSet(...)`.
  - `static class OutputPath`: `void ValidateAuthoredOutputPath(string raw, string cwd)`; `string Resolve(OutputRequest)` (CLI use; phase 5 — port now, since `validateAuthoredOutputPath` shares helpers).
  - `static class SchemaValidator`: `void Validate(DiagramType type, JsonObject data, DiagnosticSink sink)` — JsonSchema.Net `EvaluationOptions { OutputFormat = List, RequireFormatValidation = false }`, `SchemaRegistry` preloaded with all 11 embedded schemas by `$id`; maps each error to `Diagnostic("schema/<keyword>", …)` as validator.mjs:38-83 (`annotatedPath`, `supportedFixes` table); then the `portablePath` wrapper on `meta.output` (code `schema/portablePath`). Pre-checks with JS regex semantics for `common.schema.json` patterns at lines 38 and 41 and `timeline.schema.json:128`; `minLength/maxLength` counted in code points (override JsonSchema.Net's grapheme count by evaluating those two keywords ourselves on string instances — simplest: post-filter JsonSchema.Net length errors and recompute).
  - `static class EngineeringProfiles`: `void Validate(DiagramType, JsonObject, DiagnosticSink)` (← engineering-profiles.mjs:147).
  - `DiagramLoader.Load(DiagramType type, JsonObject diagram, RenderContext ctx)`: `validateAuthoredOutputPath` (if `meta.output` present) → `SchemaValidator.Validate` → `ctx.Locale.Register` + record `LocaleDiagnostics` → `ValidateCrossCollectionContracts` (= `validateRelationshipIds`, cli.mjs:331-359, code `relationship/duplicate-id`… copy exactly) → `EngineeringProfiles.Validate`. Returns nothing; throws `ArchifyDiagnosticException`.

- [ ] **Step 1: Failing tests**

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ValidationTests
{
    public static IEnumerable<object[]> AllExamples() =>
        Directory.GetFiles(Fixtures.Upstream("examples"), "*.json").Where(f => !f.Contains("locales")).Select(f => new object[] { Path.GetFileName(f) });

    [Theory, MemberData(nameof(AllExamples))]
    public void Every_upstream_example_validates(string file)
    {
        var json = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream($"examples/{file}")));
        var type = DiagramTypes.Parse(json["diagram_type"]!.GetValue<string>());
        var ctx = new RenderContext(type, json, new RenderOptions());
        DiagramLoader.Load(type, json, ctx);                        // must not throw
        Assert.DoesNotContain(ctx.Diagnostics.Recorded, d => d.Severity == Severity.Error);
    }

    static JsonObject Example(string f) => DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream($"examples/{f}")));
    static ArchifyDiagnosticException Fails(DiagramType t, JsonObject d) =>
        Assert.Throws<ArchifyDiagnosticException>(() => DiagramLoader.Load(t, d, new RenderContext(t, d, new RenderOptions())));

    // Mirrors UP/test/golden.mjs expectFailure cases (lines 129-200) — assert on code only.
    [Fact] public void Extra_property_rejected() { var d = Example("agent-tool-call.workflow.json"); d["bogus"] = 1; Assert.Contains("schema/additionalProperties", Fails(DiagramType.Workflow, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Missing_schema_version() { var d = Example("cache-miss-request.sequence.json"); d.Remove("schema_version"); Assert.Contains("schema/required", Fails(DiagramType.Sequence, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Node_id_starting_with_digit() { var d = Example("agent-tool-call.workflow.json"); d["nodes"]![0]!["id"] = "1bad"; var codes = Fails(DiagramType.Workflow, d).Diagnostics.Select(x => x.Code).ToList(); Assert.Contains("schema/pattern", codes); }
    [Fact] public void Zero_component_width() { var d = Example("web-app.architecture.json"); d["components"]![0]!["size"] = new JsonArray(0, 60); Assert.Contains("schema/exclusiveMinimum", Fails(DiagramType.Architecture, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Card_dot_outside_enum() { var d = Example("agent-tool-call.workflow.json"); d["cards"]![0]!["dot"] = "purple"; Assert.Contains("schema/enum", Fails(DiagramType.Workflow, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Duplicate_relationship_id_rejected()
    {
        var d = Example("web-app.architecture.json"); var conns = d["connections"]!.AsArray();
        conns[0]!["id"] = "dup"; conns[1]!["id"] = "dup";
        Assert.Contains(Fails(DiagramType.Architecture, d).Diagnostics, x => x.Code.StartsWith("relationship/"));
    }
    [Fact] public void MinLength_counts_code_points_not_graphemes()
    {
        var d = Example("web-app.architecture.json"); d["meta"]!["title"] = "👨‍👩‍👧";   // 5 code points, 1 grapheme; minLength 1 must pass either way
        DiagramLoader.Load(DiagramType.Architecture, d, new RenderContext(DiagramType.Architecture, d, new RenderOptions()));
        d["meta"]!["output"] = "x/" + new string('a', 256) + ".html";                  // [^/]{256} segment limit in code points
        Assert.Contains(Fails(DiagramType.Architecture, d).Diagnostics, x => x.Code.StartsWith("schema/"));
    }
    [Fact] public void Emoji_in_output_path_is_not_a_lone_surrogate()
    {
        var d = Example("web-app.architecture.json"); d["meta"]!["output"] = "out/😀.html";
        // common.schema.json:38 rejects lone surrogates only; a valid pair must pass the regex pre-check (portablePath may still object — accept either no error or a schema/portablePath code, never schema/pattern).
        try { DiagramLoader.Load(DiagramType.Architecture, d, new RenderContext(DiagramType.Architecture, d, new RenderOptions())); }
        catch (ArchifyDiagnosticException ex) { Assert.DoesNotContain("schema/pattern", ex.Diagnostics.Select(x => x.Code)); }
    }
}
```
Before implementing, confirm each expected keyword by reading the matching schema snippet (`exclusiveMinimum` for size — `grep -n "size" UP/schemas/architecture.schema.json`); if upstream uses `minimum` with `exclusiveMinimum: 0` the code is still `schema/exclusiveMinimum` (ajv reports the failing keyword). Fix the test expectation from the schema, not the other way round.

- [ ] **Step 2: Run** → compile failure.

- [ ] **Step 3: Implement.** Schema registry: for each embedded `schemas/*.json`, `JsonSchema.FromText` then `SchemaRegistry.Global.Register(new Uri(schema.GetId()), schema)` — use an **instance** registry on `EvaluationOptions.SchemaRegistry`, not `Global` (thread safety, test isolation). Error → diagnostic mapping: `instancePath` = JSON Pointer from `EvaluationResults.InstanceLocation` (`/components/0/size/0`), keyword = `EvaluationResults.EvaluationPath` last segment, `error.params` approximated from the keyword + schema value (`Evidence.keyword`, `Evidence.expected`); `supportedFixes` table copied. Length keywords: after evaluation, drop `minLength/maxLength` errors and re-run a code-point count against the schema's limit (walk the schema at `EvaluationPath` for the limit). Regex pre-check: three patterns evaluated with hand-written checks (lone surrogate scan; ASCII `\d`; segment code-point count) **before** JsonSchema.Net; emit `schema/pattern` with the same `instancePath`. `PortablePath.cs` ← portable-path.mjs line-for-line (reason codes preserved). `OutputPath.cs` ← output-path.mjs:1-440 (uses `path-semantics` only for `resolvePhysicalLocation` in the CLI path — port `resolve` only as far as `validateAuthoredOutputPath` needs; leave a `// phase 5: resolveOutputPath` note on the rest **and port it there**, not now).

- [ ] **Step 4: Run** `dotnet test --filter ValidationTests` → 24 example theories + 8 facts pass.

- [ ] **Step 5: Commit** → `feat(core): validation gates — schema (JsonSchema.Net 2020-12 + JS regex/length semantics), portable path, cross-collection ids, engineering profiles`.

---

### Task 7: Shared geometry layer

**Files:**
- Create: `Shared/Geometry.cs`, `Shared/SpatialGrid.cs`, `Shared/RouteQuality.cs`, `Shared/AutomaticLabels.cs`, `Shared/TextFit.cs`, `Shared/Legend.cs`, `Shared/NodeCardStyle.cs`, `Shared/LayoutReport.cs`, `Shared/DesktopReadability.cs` (all under `src/Archify.Net.Core/`), `tests/Archify.Net.Core.Tests/GeometryTests.cs`

**Interfaces:**
- Produces (names = upstream exports, PascalCased; types: points are `double[2]` as `Pt` readonly record struct `(double X, double Y)`, rects `Rect(double X, double Y, double Width, double Height)`):
  - `Geometry`: every export listed in `geometry.mjs` (`AsArray`, `IsFinitePoint`, `RectsOverlap`, `SegmentIntersectsRect`, `SegmentRectClearance`, `SegmentRectClearanceWithin`, `SegmentRectIntersectionLength`, `CollectLabelRouteClearance`, `MinimumLabelRouteClearance`, `RouteHonorsEndpointSides`, `CleanEndpointSideProblems`, `CleanFlowProblems`, `ForwardCollinearAnalysisSegments`, `SourceSegmentIndexAtPoint`, `PolylineBendContacts`, `CleanCrossingProblems`, `CollectAmbiguousCorridors`, `CollectArrowheadCollisions`, `CleanAmbiguousCorridorProblems`, `CollectBorderRuns`, `CleanBorderRunProblems`, `RouteBudgetMetrics`, `CollectRouteRhythmIssues`, `CleanRouteRhythmProblems`, `CleanLabelRouteClearanceProblems`, `CollectLabelCanvasOverflow`, `DescribeLabelCanvasOverflow`, `CleanLabelCanvasContainmentProblems`, `JoinRoutePoints`, `NormalizeRoutePoints`, `FrameBorderSegments`, `ProperSegmentIntersection`, `Anchor`, `PortHasCornerClearance`, `AlignAutomaticFacingPorts`, `AutomaticPortRhythmBridge`, `AutomaticPortSpread`, `DefaultFromSide`, `DefaultToSide`, `LegacyDefaultFromSide`, `LegacyDefaultToSide`, `ChosenSide`, `PolylinePath`, `RoutePointsValue`, `AuthoredStraightRouteAttrs`, `RoundedPath`, `LabelPoint`, `ComponentFill`, `ComponentText`, `ArrowClassMap`, `VariantAccent`, `EdgeLabelAccent`, `FormatRect`, `SuggestLabelObstacleFix`, `SuggestLabelPairFix`, `ComponentSeparationOptions`, `SuggestComponentSeparation`). Functions that call `recordDiagnostic` take a `DiagnosticSink` parameter.
  - `RouteQuality.ShortestOrthogonalGridRoute(...)`, `RouteQuality.CleanRouteDetourProblems(...)`.
  - `SpatialGrid.Create(...)`; `AutomaticLabels.PlaceAutomaticLabels(...)`, `AutomaticLabels.ReservedLabelRect(...)`.
  - `TextFit.FittedNodeFontSize(string text, double width, double preferred, double minimum)`, `MinimumNodeTextWidth(string text, double minimum)`, `AvailableNodeTextWidth(...)`, `NodeTextFit.WidthFactor = 0.6`, `HorizontalPadding = 8`.
  - `Legend.ResolveLegend(...)`, `LegendFootprint`, `MeasureLegend`, `RenderLegend`, `RelationshipLegendObstacles`.
  - `NodeCardStyle.NODE_CARD_ATTR`, `NodeCardStyle.Style(...)`; `LayoutReport` (+ `ComponentBox`, `BoundaryBox`, `ConnectionPath` builders); `DesktopReadability` constants and functions listed in check-render-output.mjs:9-19 import.

- [ ] **Step 1: Failing fixture tests** (`GeometryTests.cs`) — one `[Fact]` per fixture key in `tests/fixtures/js/geometry.json`:

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class GeometryTests
{
    static readonly JsonObject Fx = (JsonObject)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/geometry.json")))!;
    static Rect R(JsonNode n) => new(n["x"]!.D(), n["y"]!.D(), n["width"]!.D(), n["height"]!.D());
    static Pt P(JsonNode n) => new(n[0]!.D(), n[1]!.D());
    static List<Pt> Poly(JsonNode n) => n.AsArray().Select(p => P(p!)).ToList();

    static (Pt a, Pt b) Seg(JsonNode n) => (P(n[0]!), P(n[1]!));

    [Fact] public void RectsOverlap() { foreach (var c in Fx["rectsOverlap"]!.AsArray()) Assert.Equal(c![3]!.GetValue<bool>(), Geometry.RectsOverlap(R(c[0]!), R(c[1]!), c[2]!.D())); }
    [Fact] public void SegmentIntersectsRect() { foreach (var c in Fx["segmentIntersectsRect"]!.AsArray()) Assert.Equal(c![3]!.GetValue<bool>(), Geometry.SegmentIntersectsRect(Seg(c[0]!), R(c[1]!), c[2]!.D())); }
    [Fact] public void SegmentRectClearance() { foreach (var c in Fx["segmentRectClearance"]!.AsArray()) Assert.Equal(c![2]!.D(), Geometry.SegmentRectClearance(Seg(c[0]!), R(c[1]!)), 12); }
    [Fact] public void PolylinePath_bytes() { foreach (var c in Fx["polylinePath"]!.AsArray()) Assert.Equal(c![1]!.GetValue<string>(), Geometry.PolylinePath(Poly(c[0]!))); }
    [Fact] public void RoundedPath_bytes() { foreach (var c in Fx["roundedPath"]!.AsArray()) Assert.Equal(c![2]!.GetValue<string>(), Geometry.RoundedPath(Poly(c[0]!), c[1]!.D())); }
    [Fact] public void NormalizeRoutePoints() { foreach (var c in Fx["normalizeRoutePoints"]!.AsArray()) Assert.Equal(Js.Stringify(c![1]), Js.Stringify(Geometry.NormalizeRoutePoints(Poly(c[0]!)).ToJson())); }
    [Fact] public void RoutePointsValue() { foreach (var c in Fx["routePointsValue"]!.AsArray()) Assert.Equal(c![1]!.GetValue<string>(), Geometry.RoutePointsValue(Poly(c[0]!))); }
    [Fact] public void LabelPoint_matches_upstream_rules()
    {   // geometry.mjs:1868-1880, ported literally; no fixture needed, the arithmetic is the spec
        var two = new List<Pt> { new(0, 0), new(100, 50) };
        Assert.Equal(new Pt(50, -10), Geometry.LabelPoint(new JsonObject(), two));
        Assert.Equal(new Pt(53, -6), Geometry.LabelPoint(new JsonObject { ["labelDx"] = 3, ["labelDy"] = 4 }, two));
        var three = new List<Pt> { new(0, 0), new(100, 0), new(100, 100) };
        Assert.Equal(new Pt(100, 40), Geometry.LabelPoint(new JsonObject(), three));                      // labelSegment defaults to 1
        Assert.Equal(new Pt(50, -10), Geometry.LabelPoint(new JsonObject { ["labelSegment"] = 0 }, three));
        Assert.Equal(new Pt(7, 8), Geometry.LabelPoint(new JsonObject { ["labelAt"] = new JsonArray(7, 8) }, three));
    }
    [Fact] public void TextFit()
    {
        foreach (var c in Fx["fittedNodeFontSize"]!.AsArray())
        {
            var t = c![0]!.GetValue<string>();
            Assert.Equal(c[1]!.D(), TextFit.FittedNodeFontSize(t, 120, 9, 6));
            Assert.Equal(c[2]!.D(), TextFit.MinimumNodeTextWidth(t, 6));
            Assert.Equal(c[3]!.D(), TextFit.AvailableNodeTextWidth(120));
        }
    }
}
```
`ToJson()` is a tiny test-side extension turning `List<Pt>` into a `JsonArray` of `[x, y]` arrays (the shape upstream returns). Signatures are upstream's: `rectsOverlap(a, b, gap = 0)`, `segmentIntersectsRect(segment, rect, gap = 0)` with `segment = [p1, p2]`, `segmentRectClearance(segment, rect)`, `roundedPath(points, radius)`, `labelPoint(item, points)`.

- [ ] **Step 2: Run** → compile failure.

- [ ] **Step 3: Port, in dependency order**: `SpatialGrid` (66) → `Geometry` (2073; `Math.round` → `Js.Round`, `Math.hypot` → `Js.Hypot`, every `${number}` → `Js.Num`, `.toFixed(2)` in message text → `Js.ToFixed`, `.sort(` → `Js.Sort`, `Map`/`Set` → `JsMap`/`JsSet`) → `RouteQuality` (670) → `AutomaticLabels` (290) → `TextFit` (105; `Math.floor(fitted*10)/10` stays `Math.Floor`) → `Legend` (227) → `NodeCardStyle`, `LayoutReport`, `DesktopReadability`. Keep upstream comments where they explain a constant. Function signatures take the JS "options object" as a small `record` when upstream destructures one.

- [ ] **Step 4: Run** `dotnet test --filter GeometryTests` → pass. `RoundedPath_bytes` is the `Js.Hypot`/`Js.Num` acid test: a 1-ulp difference shows up as a different last digit — fix in `Js`, not by loosening the test.

- [ ] **Step 5: Commit** → `feat(core): shared geometry, routing quality, labels, legend, text fit (fixture-exact)`.

---

### Task 8: Brand marks (built-in only)

**Files:**
- Create: `src/Archify.Net.Core/Shared/BrandMarks.cs`, `tests/Archify.Net.Core.Tests/BrandMarkTests.cs`
- Modify: `src/Archify.Net.Core/RenderContext.cs` (add `Brands` resolver)

**Interfaces:**
- Produces:
  - `static class BrandMarks` (catalog + SVG): `IReadOnlyList<BrandMark> ListBrandMarks()`, `BrandMark? FindBrandMark(string idOrAlias)` (id, then aliases, case-insensitive as upstream `findBrandMark`), `IReadOnlyList<string> Suggestions(string input)`, `JsonObject? BrandMetadataFor(ResolvedBrand? mark)`, `double BrandLabelFitWidth(...)`, `string? BrandTopRailProblem(...)`, `string RenderBrandMark(ResolvedBrand mark, ...)` — exact ports of brand-marks.mjs:640-706 and the lookup helpers. `record BrandMark(string Id, string Title, string Category, string[] Aliases, string[] Domains, double ViewBox, string Hex, string Path, JsonObject Provenance)` from `Resources.Text("generated-brand-marks.json")`; `record ResolvedBrand(BrandMark Mark, string Kind = "preset", string Status = "preset", string SourceUrl)` (= `{ ...preset, kind: 'preset', status: 'preset', sourceUrl: preset.provenance.source }`).
  - `sealed class BrandMarkResolver` (per `RenderContext`; replaces `RESOLVED_BY_NODE` + the `RESOLVED_MARK` symbol): `void PrepareDiagramBrandMarks(DiagramType type, JsonObject diagram, DiagnosticSink sink)` — synchronous port of `prepareDiagramBrandMarks` (brand-marks.mjs, quoted above in this task) with one substitution: the `typeof node.brand === 'object'` branch never calls `remoteBrand`; it pushes `` `/${collection}/${index}/brand could not reproduce the pinned capture: remote capture is not available in Archify.Net.Core` `` so the existing code mapping yields **`brand/capture-unavailable`**. The other three outcomes keep upstream's messages and codes: `brand/unpinned-url`, `brand/unknown` (with `Suggestions`), and preset resolution. `ResolvedBrand? BrandMarkFor(JsonObject node)` keyed by node reference (`JsMap<JsonObject, ResolvedBrand>`; `JsonNode` has reference equality). Collections: `architecture→components, workflow→nodes, sequence→participants, dataflow→nodes, lifecycle→states` (brand-marks.mjs:10-16).

- [ ] **Step 1: Failing tests**

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class BrandMarkTests
{
    [Fact] public void Catalog_has_107_marks_and_aliases_resolve()
    {
        Assert.Equal(107, BrandMarks.ListBrandMarks().Count);
        Assert.Equal("alibaba-cloud", BrandMarks.FindBrandMark("aliyun")!.Id);
        Assert.Null(BrandMarks.FindBrandMark("no-such-brand"));
    }
    [Fact] public void Remote_brand_is_reported_not_fetched()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/remote-brand.architecture.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/remote-brand.expected.json")))!;
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        var upstreamCodes = expected["diagnostics"]!.AsArray().Select(x => x!["code"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("brand/capture-unavailable", upstreamCodes);
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics));
        Assert.Equal(upstreamCodes, ex.Diagnostics.Select(x => x.Code).ToHashSet());
    }
    [Fact] public void Unknown_and_unpinned_brands_report_upstream_codes()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/brand-aware-delivery.architecture.json")));
        d["components"]![0]!["brand"] = "not-a-real-brand"; d["components"]![1]!["brand"] = "https://example.com/logo.svg";
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics));
        Assert.Equal(new HashSet<string> { "brand/unknown", "brand/unpinned-url" }, ex.Diagnostics.Select(x => x.Code).ToHashSet());
    }
}
```

- [ ] **Step 2: Run** → compile failure.
- [ ] **Step 3: Port** `brand-marks.mjs`: catalog/lookup helpers, `prepareDiagramBrandMarks`, `brandMarkFor`, `brandMetadataFor`, `brandLabelFitWidth`, `brandTopRailProblem`, `renderBrandMark`. **Not ported**: `normalizeBrandSourceUrl`, `isPrivateBrandAddress`, `validateUrlShape`, `captureBrandReference`, `remoteBrand`, `mapConcurrent`, `captureTimeoutMilliseconds` and every `ARCHIFY_BRAND_*` env read — all exist only for network capture. `asUrl` **is** ported (the unpinned-URL detection needs it).
- [ ] **Step 4: Run** → pass. **Step 5: Commit** → `feat(core): built-in brand marks; remote capture reports instead of fetching`.

---

### Task 9: Architecture renderer + `Archify.Render` facade — byte parity

**Files:**
- Create: `src/Archify.Net.Core/Architecture/Grid.cs`, `Routing.cs`, `Labels.cs`, `ArchitectureRenderer.cs`, `src/Archify.Net.Core/Archify.cs`, `tests/Archify.Net.Core.Tests/ParityTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `public static RenderResult Archify.Render(DiagramType type, JsonNode diagram, RenderOptions? options = null)`; internal `interface IDiagramRenderer { (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx); }`; internal `static class Renderers { static IDiagramRenderer For(DiagramType type) }` (phase 1: `Architecture` → `new ArchitectureRenderer()`, every other type → `throw new NotSupportedException($"{type.Name()} renderer lands in phase 2–4")`); `ArchitectureRenderer : IDiagramRenderer`; `Grid.GridLayout(JsonObject arch)`, `Grid.ResolveComponentPos(JsonObject component, GridLayout grid)`, `Grid.ValidateGridPlacement(...)`, `Grid.DefaultGrid`; `Routing.CreateRouter(IReadOnlyList<Component> components, IReadOnlyList<JsonObject> connections, RouterOptions options)` returning `sealed class Router` whose public methods are the closure's returned functions (routing.mjs:75-…); `RouterOptions` is a record with the destructured defaults at routing.mjs:76-90 (`Frames`, `InteriorSegmentPx = 16`, `MicroSegmentPx = 8`, `LabelRectFor`, `DistinctAutomaticPorts = false`, `PreferReadableRoutes = false`, `SideFor`, `MaxPortSpacing`, `PreferredCandidates`, `CompositionFloors = true`, `CrossingFreeGridFirst = false`, `ComponentGapPx = 0`, … copy the full list).

- [ ] **Step 1: Parity test**

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ParityTests
{
    // Oracle list = UP/scripts/render-examples.mjs TARGETS (upstream goldens live in upstream/examples)
    // + goldens we minted in tests/goldens. Phase 1 covers architecture; later phases add rows.
    public static IEnumerable<object[]> Targets() =>
    [
        ["architecture", Fixtures.Upstream("examples/web-app.architecture.json"), Fixtures.Upstream("examples/web-app-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/production-deployment.architecture.json"), Fixtures.Golden("production-deployment-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/brand-aware-delivery.architecture.json"), Fixtures.Golden("brand-aware-delivery-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/checkout-platform.base.architecture.json"), Fixtures.Golden("checkout-platform.base-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/checkout-platform.head.architecture.json"), Fixtures.Golden("checkout-platform.head-rendered.html")],
        ["architecture", Fixtures.Fixture("diagrams/web-app.no-profile.architecture.json"), Fixtures.Golden("web-app.no-profile-rendered.html")],
    ];

    [Theory, MemberData(nameof(Targets))]
    public void Render_is_byte_identical_to_upstream(string type, string input, string golden)
    {
        var diagram = DiagramLoader.Parse(Fixtures.Lf(input));
        var result = Archify.Render(DiagramTypes.Parse(type), diagram);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Severity.Error));
        var expected = Fixtures.Lf(golden);
        if (expected != result.Html) Assert.Fail(FirstDifference(expected, result.Html));
        Assert.Equal(Fixtures.Bytes(golden), new System.Text.UTF8Encoding(false).GetBytes(result.Html));   // BOM/encoding guard
    }

    [Fact] public void No_profile_fixture_is_advisory()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.no-profile.architecture.json")));
        var r = Archify.Render(DiagramType.Architecture, d);
        Assert.Contains("data-quality-profile=\"standard\" data-quality-gates=\"advisory\"", r.Svg);
        var showcase = Archify.Render(DiagramType.Architecture, d, new RenderOptions(Quality: QualityProfile.Showcase));
        Assert.Contains("data-quality-profile=\"showcase\"", showcase.Svg);
        Assert.DoesNotContain("advisory", showcase.Svg);
    }

    [Fact] public void Bad_input_returns_diagnostics_not_exceptions()
    {
        var d = DiagramLoader.Parse("{\"schema_version\":1,\"diagram_type\":\"architecture\",\"meta\":{\"title\":\"x\",\"output\":\"x.html\"},\"components\":[]}");
        var r = Archify.Render(DiagramType.Architecture, d);
        Assert.Equal("", r.Html);
        Assert.NotEmpty(r.Diagnostics);
    }

    internal static string FirstDifference(string expected, string actual)
    {
        var el = expected.Split('\n'); var al = actual.Split('\n');
        for (var i = 0; i < Math.Max(el.Length, al.Length); i++)
        {
            var e = i < el.Length ? el[i] : "<EOF>"; var a = i < al.Length ? al[i] : "<EOF>";
            if (e == a) continue;
            var ctx = string.Join('\n', Enumerable.Range(Math.Max(0, i - 3), Math.Min(7, el.Length - Math.Max(0, i - 3))).Select(j => $"{j + 1,6}: {el[j]}"));
            return $"First difference at line {i + 1}\nexpected: {e}\nactual:   {a}\n--- expected context ---\n{ctx}";
        }
        return "lengths differ only in trailing content";
    }
}
```

- [ ] **Step 2: Run** → compile failure.

- [ ] **Step 3: Port** `architecture/grid.mjs` (62) → `Grid.cs`; `architecture/routing.mjs` (1469) → `Routing.cs` — the `createRouter` closure becomes `sealed class Router` holding the captured state as fields; `labels.mjs` (2 lines) → `Labels.cs`; `render-architecture.mjs` (1204) → `ArchitectureRenderer.Render(ctx)`: lines 52-54 (argv/env) are replaced by `ctx`; the top-level flow from line 56 to the final `writeDiagram(...)` call becomes the method body in the same order; the `--layout-json` branch becomes `LayoutReport` on the result. **Upstream `writeDiagram` prints `outPath`; the port returns the HTML.** Keep every literal string byte-exact (indentation inside SVG template literals matters).

  `Archify.cs`:
```csharp
using System.Text.Json.Nodes;
namespace Archify.Net;

public static class Archify
{
    public static RenderResult Render(DiagramType type, JsonNode diagram, RenderOptions? options = null)
    {
        var root = DiagramLoader.Normalize(diagram.DeepClone().AsObject());
        var ctx = new RenderContext(type, root, options ?? new RenderOptions());
        try
        {
            DiagramLoader.Load(type, root, ctx);                                   // Task 6 gates
            ctx.Brands.PrepareDiagramBrandMarks(type, root, ctx.Diagnostics);      // Task 8
            var renderer = Renderers.For(type);                                    // phase 1: Architecture only; others throw NotSupportedException("phase N")
            var (svg, cards, layout) = renderer.Render(ctx);
            var meta = root["meta"]!.AsObject();
            var cardsHtml = Utils.RenderCards(cards);
            var html = TemplateWriter.Apply(Resources.Text("assets/template.html"), meta.Str("title")!, meta.Str("subtitle"), svg, cardsHtml,
                meta.Str("locale"), meta.Str("visual_preset") ?? "classic", ctx.SourceEvidence, ctx.Locale);
            return new RenderResult(html, svg, cardsHtml, ctx.Diagnostics.Recorded, layout);
        }
        catch (ArchifyDiagnosticException ex)
        {
            var (_, diagnostics) = ctx.Diagnostics.RendererFailure(ex);
            return new RenderResult("", "", "", diagnostics, null);
        }
    }
}
```
  (`ctx.SourceEvidence` is `null` in phase 1; `repository-evidence` lands in phase 4.)

- [ ] **Step 4: Run** `dotnet test --filter ParityTests` → iterate on `FirstDifference` output until all 6 targets pass. Rules while iterating: a difference in a number's last digit → `Js` bug (fix there, re-run `JsTests`); an attribute order difference → you reordered an interpolation; a whitespace difference → a template literal lost its indentation. Never edit a golden.

- [ ] **Step 5: Full suite** `dotnet test` → all green, exit code 0.

- [ ] **Step 6: Commit** → `feat(core): architecture renderer — six goldens byte-identical to upstream 3.1.0`.

---

### Task 10: Artifact checker (`check-render-output`)

**Files:**
- Create: `src/Archify.Net.Core/Shared/SvgPathAnalysis.cs`, `src/Archify.Net.Core/Shared/ArtifactChecker.cs`, `tests/Archify.Net.Core.Tests/ArtifactCheckerTests.cs`
- Modify: `src/Archify.Net.Core/Archify.cs` (add `Check`)

**Interfaces:**
- Produces: `public static CheckResult Archify.Check(string html, CheckOptions? options = null)`; `record CheckOptions(bool RequireProvenance = false)` (provenance sidecar part is phase 5 — in phase 1 `RequireProvenance` must be `false` or `Check` throws `NotSupportedException`); `CheckResult(bool Ok, JsonObject Report)` where `Report` has upstream's keys minus `file`: `ok, artifact {sha256, bytes}, checks [...], composition {...}` + whatever `check-render-output.mjs:441-444` prints. `SvgPathAnalysis.AnalyzeSvgPath(string d)`.

- [ ] **Step 1: Failing test**

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ArtifactCheckerTests
{
    public static IEnumerable<object[]> Goldens() =>
        Directory.GetFiles(Fixtures.Upstream("examples"), "*.html").Select(f => new object[] { f })
        .Concat(Directory.GetFiles(Path.Combine(Fixtures.RepoRoot, "tests", "goldens"), "*.html").Select(f => new object[] { f }));

    [Theory, MemberData(nameof(Goldens))]
    public void Check_report_equals_node_checker(string html)
    {
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Golden($"checker/{Path.GetFileNameWithoutExtension(html)}.json")))!;
        var result = Archify.Check(Fixtures.Lf(html));
        Assert.Equal(expected["ok"]!.GetValue<bool>(), result.Ok);
        var e = Js.Stringify(expected, 2); var a = Js.Stringify(result.Report, 2);
        if (e != a) Assert.Fail(ParityTests.FirstDifference(e, a));
    }
}
```

- [ ] **Step 2: Run** → compile failure.
- [ ] **Step 3: Port** `svg-path-analysis.mjs` (135) then `check-render-output.mjs` (1462) → `ArtifactChecker.Check(string html)`: `process.argv`/`fs` head (lines 20-40) replaced by the string parameter; `sha256` over `UTF8NoBom.GetBytes(html)`; the `checks` array and `composition` object built as `JsonObject`s in the same key order; final `console.log(JSON.stringify({...}))` becomes the returned `Report` (drop `file`). Regex-based HTML/SVG scanning stays regex-based (upstream parses with regexes — do not introduce an HTML parser; byte parity of the report depends on the same matching).
- [ ] **Step 4: Run** → 20 theories pass. **Step 5: Commit** → `feat(core): artifact checker — report JSON equals upstream checker on every golden`.

---

### Task 11: `Archify.Validate`, docs, handoff

**Files:**
- Modify: `src/Archify.Net.Core/Archify.cs` (add `Validate`), `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (status line), `tests/Archify.Net.Core.Tests/ParityTests.cs` (add `Validate` test)
- Create: `README.md`, `SESSION_HANDOFF.md`

**Interfaces:**
- Produces: `public static IReadOnlyList<Diagnostic> Archify.Validate(DiagramType type, JsonNode diagram, RenderOptions? options = null)` — upstream `validate` semantics: run `Render` (which runs the load gates); if it produced HTML, run `Check` and, when `!Ok`, map the report with a port of `checkerDiagnostics` (`bin/archify.mjs`, function `checkerDiagnostics`): every `composition.issues[]` entry with `severity: 'error'` → its own `code` (subject `{relationship}` or `{check: 'composition', nodeId?}`, evidence = remaining issue fields, fixes from `compositionFixes`); every failed `checks[]` entry not in `COMPOSITION_CHECKS` → `artifact/<name with _ → ->` (message = first truthy `details[]` entry or `Final artifact failed <name>.`), except `svg_path_data`, which forwards the checker's own `artifact/svg-path-malformed` / `artifact/svg-path-unsupported` diagnostics; nothing classified → single `artifact/check-failed`. Copy `COMPOSITION_CHECKS`, `CHECK_FIXES` and `compositionFixes` from the same file. Returns render diagnostics ∪ checker diagnostics.

- [ ] **Step 1: Test** (append to `ParityTests.cs`)
```csharp
    [Fact] public void Validate_passes_every_architecture_target_and_fails_bad_input()
    {
        foreach (var t in Targets()) Assert.Empty(Archify.Validate(DiagramTypes.Parse((string)t[0]), DiagramLoader.Parse(Fixtures.Lf((string)t[1]))).Where(d => d.Severity == Severity.Error));
        var bad = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/web-app.architecture.json"))); bad["components"]![0]!["id"] = "1bad";
        Assert.Contains(Archify.Validate(DiagramType.Architecture, bad), d => d.Code == "schema/pattern");
    }
```
- [ ] **Step 2: Implement; run full suite** → green.
- [ ] **Step 3: `README.md`** (short): what this is, upstream pin, how to build/test, that `tools/capture-fixtures.mjs` is dev-time only, phase table with status.
- [ ] **Step 4: `SESSION_HANDOFF.md`**: state at end of phase 1, parity numbers (6/6 architecture, 20/20 checker), known deviations (CJK exemption not yet reachable), next phase pointer. No card lanes.
- [ ] **Step 5: Spec status** → "Phase 1 implemented, commit <sha from git log>".
- [ ] **Step 6: Commit** → `docs: README, handoff; Archify.Validate facade`. Then Opus review of the whole phase-1 diff against `UP/` (reviewer prompt: side-by-side each `.cs` with its `.mjs`; flag any `ToString()`, `List.Sort`, `Dictionary` enumeration, `string.Trim` in renderer code; confirm goldens untouched via `git log -- tests/goldens upstream`).

---

## Phases 2–6 (separate plans, one board card each — summary only)

| Phase | Card title | Exit |
|---|---|---|
| 2 | `PORT-002: erd/grid + sequence, lifecycle, class, tree, timeline, waterfall renderers` | TARGETS rows for those types byte-identical (8 upstream goldens) + Node-minted goldens for `async-job-roundtrip.sequence`, `deployment-release.lifecycle`; DST timeline fixture (`America/New_York`); `Js.Graphemes`/`WordBreaks` fixtures; zh-CN fixture via `holiday-planning.workflow` waits for phase 3 |
| 3 | `PORT-003: workflow (compiler, placement repair, migration geometry) + dataflow` | 2 upstream goldens + minted `event-stream.dataflow`, `holiday-planning.workflow` (zh-CN), `incident-response.workflow`, `release-delivery.workflow` |
| 4 | `PORT-004: erd renderer + repository evidence` | 2 erd goldens; source-evidence fixture repo (spawns `git`) |
| 5 | `PORT-005: CLI (12 commands), receipts, sidecars, lock/journal, Playwright browser/visual gates` | `archify finalize` on every example passes; receipt JSON shape matches upstream minus `update` |
| 6 | `PORT-006: MCP server + SKILL.md` | tools `validate/render/finalize/guide`; SKILL.md adapted |

Dependencies: 2 → 1; 3 → 1; 4 → 2 (class/erd grid), 1; 5 → 1–4; 6 → 5.
