# Archify.Net

A C# port of [archify](https://github.com/tt-a1i/archify) 3.1.0, pinned at upstream commit `c21c31ff`.
It renders architecture diagrams from JSON into standalone HTML with inline SVG, and checks the result.
The correctness bar is byte parity: the HTML it writes is byte-identical to upstream's for the same input.
The reader-side viewer stays JavaScript and is embedded verbatim from `upstream/`.

.NET 10, C# 14, no NuGet dependencies in the library.

## Contents

- [Status](#status)
- [Quick start](#quick-start)
- [Library surface](#library-surface)
- [Diagram types](#diagram-types)
- [Build and test](#build-and-test)
- [How parity is tested](#how-parity-is-tested)
- [Repository layout](#repository-layout)
- [Updating fixtures from upstream](#updating-fixtures-from-upstream)
- [Design documents](#design-documents)
- [Known deviations from upstream](#known-deviations-from-upstream)
- [Credits and license](#credits-and-license)

## Status

The port is built library first (`Archify.Net.Core`), then a CLI, then an MCP server.

| Phase | Scope | Status |
|---|---|---|
| 1 | Foundation, validation gates, architecture renderer, artifact checker | Done |
| 2 | erd/grid, sequence, lifecycle, class, tree, timeline, waterfall renderers | Done |
| 3 | workflow and dataflow renderers | Done |
| 4 | erd renderer and repository evidence | Planned |
| 5 | CLI, receipts, sidecars, lock and journal, Playwright browser and visual gates | Planned |
| 6 | MCP server and SKILL.md | Planned |

Phase 1 parity results:

- All 6 architecture goldens are byte-identical to upstream.
- All 20 checker reports on the goldens equal upstream's, and so do the 11 committed checker issue-path probes.
- The option-level showcase profile on the no-profile fixture equals upstream's environment-driven showcase output.
- The ajv-oracle schema battery (14 cases) and all Js parity fixtures pass.

Phase 2 parity results:

- All 26 phase-2 parity rows are byte-identical: the 10 upstream goldens, 2 Node-minted example goldens and 14 fixture goldens.
- The erd banded grid equals upstream's `erd/grid.mjs` on every row of its fixture, and the checker report on every new golden equals upstream's.
- Every phase-2 renderer's failure receipts equal upstream's diagnostics.
- The class, tree, timeline and waterfall layout reports equal upstream's `--layout-json`, and the nine golden.mjs schema cases pass.

Phase 3 parity results:

- 38 phase-3 rows of `ParityTests.Render_is_byte_identical_to_upstream` (14 dataflow, 24 workflow), 70 rows in all. Every
  phase-3 golden is byte-identical to upstream's except `first-draft.mixed-port-recovery`, which differs only in one Han
  label's word break (see the word-break deviation below). That case is a separate fact that pins the difference.
- Among them are the two upstream goldens (`dataflow-product-analytics.html`, `workflow-agent-tool-call-rendered.html`)
  and the Node-minted `event-stream`, `holiday-planning` (zh-CN), `incident-response` and `release-delivery`.
- 24 workflow receipts equal upstream's `--layout-json` (`WorkflowReceiptTests`), plus one for the quality option.
- 20 failure receipts equal upstream's diagnostics: 14 workflow (`wf-*`) and 6 dataflow (`df-*`), supported fixes included.
- Fixtures against Node: `word-breaks` 1601 rows, `label-capacity` 24 (14 label sizes, 10 blocked-label wraps),
  `joint-label-routing` 9 (plus the unchanged-input fact) and `workflow-model` 220 rows over the workflow model functions.
- A workflow render returns the compiler receipt as `RenderResult.Layout`, which is upstream's `--layout-json` output.
  A dataflow render returns `null` there.

The full suite (`dotnet test`) runs 2348 tests.

## Quick start

Reference `src/Archify.Net.Core/Archify.Net.Core.csproj` (there is no NuGet package yet), then:

```csharp
using System.Text.Json.Nodes;
using Archify.Net;

var diagram = JsonNode.Parse(File.ReadAllText("web-app.architecture.json"))!;

var result = ArchifyNet.Render(DiagramType.Architecture, diagram);
if (result.Html.Length == 0)
{
    // Input or renderer problems never throw: they come back as diagnostics with upstream's codes.
    foreach (var d in result.Diagnostics)
        Console.Error.WriteLine($"{d.Severity} {d.Code} {d.Path}: {d.Message}");
    return 1;
}

File.WriteAllText("web-app.html", result.Html);

// The artifact checker upstream runs on a written file, here on the HTML string.
var check = ArchifyNet.Check(result.Html);
Console.WriteLine(check.Ok ? "check ok" : check.Report.ToJsonString());
return 0;
```

`DiagramTypes.Parse("sequence")` maps upstream's type names to the enum. Example inputs for every type are under
`upstream/examples/` (`<name>.<type>.json`).

## Library surface

`Archify.Net.Core` exposes one static facade, `ArchifyNet`, in namespace `Archify.Net`:

- `Render(type, diagram, options)` runs the load gates and the renderer. It returns a `RenderResult`: `Html`, `Svg`,
  `Cards`, `Diagnostics` and `Layout` (the layout report upstream prints with `--layout-json`).
- `Check(html, options)` runs the artifact checker. Its `Report` JSON equals upstream's
  `scripts/check-render-output.mjs` minus `file`; `Ok` mirrors the report's `ok`.
- `Validate(type, diagram, options)` renders, checks, and returns the render diagnostics followed by the checker's.

`RenderOptions(Quality, RepoRoot)` selects the quality profile (`Standard` or `Showcase`, upstream's environment-driven
profile as an option) and the repository root for evidence. `CheckOptions(RequireProvenance)` is reserved for phase 5.

Input and validation problems come back as `Diagnostic` records with upstream's codes, messages, subjects, evidence
and supported fixes. Any other renderer error comes back as an `internal/unclassified` diagnostic whose evidence names
the exception type, as upstream's JSON-mode boundary does. The facade throws only `NotSupportedException`:

- From `Render` and `Validate` for a diagram type that lands in phase 4: erd.
- From `Check` when `RequireProvenance` is set. Provenance checks land in phase 5.

The facade is named `ArchifyNet`, not `Archify`: a class of that name inside namespace `Archify.Net` cannot be named
from consumer code.

## Diagram types

| Type | Upstream renderer | Port | Phase |
|---|---|---|---|
| architecture | `renderers/architecture` | `src/Archify.Net.Core/Architecture` | 1 |
| sequence | `renderers/sequence` | `src/Archify.Net.Core/Sequence` | 2 |
| lifecycle | `renderers/lifecycle` | `src/Archify.Net.Core/Lifecycle` | 2 |
| class | `renderers/class` | `src/Archify.Net.Core/Class` | 2 |
| tree | `renderers/tree` | `src/Archify.Net.Core/Tree` | 2 |
| timeline | `renderers/timeline` | `src/Archify.Net.Core/Timeline` | 2 |
| waterfall | `renderers/waterfall` | `src/Archify.Net.Core/Waterfall` | 2 |
| workflow | `renderers/workflow` | `src/Archify.Net.Core/Workflow` | 3 |
| dataflow | `renderers/dataflow` | `src/Archify.Net.Core/Dataflow` | 3 |
| erd | `renderers/erd` | banded grid only (`src/Archify.Net.Core/Erd/Grid.cs`) | 4 |

## Build and test

From the repo root:

```
dotnet test
```

The parity suite reads the goldens and fixtures committed under `tests/` and `upstream/`. It needs no network and no Node.
Warnings are errors (`Directory.Build.props`). The time-zone tests assume Windows with ICU (Windows 10 1903 or later);
two facts fail elsewhere by design (see the time-zone deviations below).

## How parity is tested

Every expected value in the suite was produced by upstream, never written by hand:

- **Rendered goldens** (`tests/goldens/*-rendered.html`): upstream's own goldens plus goldens minted by running
  upstream on example inputs and on targeted fixture inputs. `ParityTests.Render_is_byte_identical_to_upstream`
  renders each input and compares bytes.
- **Checker reports** (`tests/goldens/checker/*.json`): upstream's checker run over each golden.
  `ArtifactCheckerTests` asserts the port's report equals it.
- **Failure receipts** (`tests/fixtures/diagrams/*.expected.json`): upstream's diagnostics for inputs that fail a
  renderer's own checks, one per diagnostic code. The receipt theories compare code order, message, severity,
  subject, evidence and supported fixes.
- **Layout reports**: upstream's `--layout-json` per golden input for class, tree, timeline and waterfall.
- **Js fixtures** (`tests/fixtures/js/*.json`): V8's answers for the JavaScript semantics the renderers lean on:
  number formatting, sorting, grapheme segmentation, text wrapping, `Date` parsing and time-zone conversion.
  The `Js` layer in `src/Archify.Net.Core/Js` reproduces them, and `JsTests`, `JsTextTests` and `JsTimeTests` pin it.
- **Schema battery**: the JSON Schemas in `upstream/schemas` are embedded and enforced by a port of the ajv subset
  upstream uses; `ValidationTests` checks the error set against ajv's.

## Repository layout

```
src/Archify.Net.Core/        the library
  Archify.cs                 ArchifyNet facade, renderer dispatch
  Shared/                    loader, validation gates, diagnostics, geometry, routing, brand marks, i18n, template writer, checker
  Js/                        V8-compatible number, string, sort, text segmentation and time helpers
  <Type>/                    one renderer per diagram type
  Erd/Grid.cs                the erd banded grid, shared by class now and erd in phase 4
tests/Archify.Net.Core.Tests xUnit suite
tests/goldens/               upstream-rendered HTML and checker reports
tests/fixtures/              diagram inputs, receipts, layout reports, Js fixtures, checker probes
upstream/                    verbatim copy of the upstream skill package files the port embeds or tests against
                             (schemas, locales, viewer, template, brand marks, references, examples)
tools/capture-fixtures.mjs   dev-time fixture minting from upstream (Node)
docs/superpowers/            design spec, phase plans, rulings and plan reviews
```

`upstream/UPSTREAM.md` records the pinned commit and how the copy was taken. The library embeds the schemas,
locales, HTML template and brand-mark catalog from `upstream/` as resources, so the built DLL is self-contained.

## Updating fixtures from upstream

`tools/capture-fixtures.mjs` is a dev-time tool that mints the goldens, receipts, layout reports and Js fixtures
from upstream. It needs Node 24 and an upstream repo checkout at `C:\projects\archify` on commit `c21c31ff`. It reads
the skill package root inside it, `C:\projects\archify\archify`. It is deterministic: a re-run on the pinned commit
rewrites every file byte-identically. CI never runs it; the test suite only reads what it committed.

To add a parity case, add the input mutation to the script, run it from the repo root with `node
tools/capture-fixtures.mjs`, and add the matching row to `ParityTests`.

## Design documents

- Design spec: `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md`, with its review next to it.
- Phase 1 plan: `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md`, plus its review and the
  phase-1 rulings (`2026-10-13-phase1-rulings.md`).
- Phase 2 plan: `docs/superpowers/plans/2026-10-14-phase2-renderers.md`, plus its review.
- Phase 3 plan: `docs/superpowers/plans/2026-10-14-phase3-workflow-dataflow.md`.
- `SESSION_HANDOFF.md`: current state, carried items and the next phase.

## Known deviations from upstream

- `Render` throws `NotSupportedException` for the one diagram type that lands in phase 4: erd. `Validate` throws it too once the input passes the load gates.
- `Check` throws `NotSupportedException` when `RequireProvenance` is set.
- `Validate` returns the render diagnostics followed by the checker's. On a check failure upstream `validate` returns only the checker's.
- Schema errors come in a different order when integer-like keys follow named keys in the authored JSON. The set of errors is the same.
- Brand diagnostics come in a different order when a brand is pinned as `{url, sha256}`. Upstream appends capture failures after the synchronous problems. The set is the same.
- A URL with whitespace in its userinfo part is treated as no URL. WHATWG percent-encodes the whitespace instead.
- `DiagramLoader.Parse` stops at nesting depth 64. `JSON.parse` has no limit.
- A raw lone surrogate inside a C# input string becomes U+FFFD. The escaped form `\udXXX` survives parsing, but rendering it fails with `internal/unclassified`.
- *Word breaks:* `Js.WordBreaks` matches V8's `Intl.Segmenter` word granularity (UAX #29) except in three places. (a) Han, Hiragana, Katakana, Thai, Lao, Khmer and Myanmar runs split per grapheme, where ICU uses a dictionary. This shows in sequence notes and in wrapped dataflow labels of the automatic showcase layout; `first-draft.mixed-port-recovery` differs from upstream in exactly one such label. (b) V8 splits inside some grapheme clusters that the port keeps whole: an Arabic number sign (U+0600, Prepend), a jamo next to a Hangul syllable, and a ZWJ before an emoji that follows a non-emoji. (c) The port assumes a non-POSIX host locale; under a POSIX/`LANG=C` default V8 splits `api.example.com` at the dots. Every other text path (tree, class, timeline wrapping; zh-CN locale) is byte-exact.
- *Time zones before the OS zone table's history:* conversions use the operating system's zone data, not Node's bundled tzdata. (a) Before a zone's first tzdata transition (local mean time, roughly pre-1900) the port shows the base offset — a `Asia/Shanghai` event at `0001-01-01T00:00:00Z` is labelled `UTC+08:06` by upstream and `UTC+08:00` by the port. (b) Before the OS table's first adjustment rule the rule is extrapolated backwards — `Pacific/Chatham`/`Australia/Lord_Howe` in 1970 read +13:45/+11:00 instead of +12:45/+10:00. UTC and fixed offsets are exact for every year; named zones are exact from the table's history onward (2026 transitions pinned by fixture).
- *`Date.parse` outside the schema shape:* strings the timeline schema rejects (`2026-10-01 10:00:00Z`, offset-less timestamps) are `NaN` in the port; unreachable through `Render` because the schema gate runs first.
- *Time zone ids the OS cannot resolve:* `meta.timezone` ids that V8's ICU accepts but the host's ICU list or `TimeZoneInfo` cannot resolve are rejected with `timeline/invalid-timezone`. The set varies with the OS build. On Windows with ICU 72.1 it includes `CET`, `EET`, `MET`, `WET`, `America/Ciudad_Juarez`, `Antarctica/Troll`, `Asia/Urumqi`, `Asia/Kashgar`, `Antarctica/Vostok`, `America/Coyhaique` and the `SystemV/*` legacy ids (card TZ-001).
- *Time zones by host:* Windows with `icu.dll` matches IANA ids case-insensitively, as V8 does. Off Windows only exact-case IANA ids are accepted. On Windows without ICU (before Windows 10 1903, Server 2019, or `System.Globalization.UseNls=true`) only UTC and fixed offsets are accepted (card TZ-002).
- A host with `InvariantGlobalization` on cannot use the library: the `Js` static constructor throws (card GLOB-001).
- A valid-JSON root that is not an object reports `input/json-parse`. Upstream reports `schema/type` at `/`.
- `Js.Sort` equals V8 only for consistent comparators. A comparator that returns NaN diverges.
- The remote brand form `{url, sha256}` reports `brand/capture-unavailable` and makes no network call.
- On a failed render `Layout` is `null`. Upstream emits a partial layout report.
- `Diagnostic.Path` throws `InvalidOperationException` when `subject.path` is not a string. Upstream reads `undefined`.
- A forwarded checker diagnostic without a message gets `""` as its message. Upstream omits the key.
- The unpinned-URL hint text can differ from WHATWG `url.href` for IDN hosts, `|`, `%7e`, and backslashes in query or fragment.
- `Check` hashes the UTF-8 encoding of the string it is given. A lone surrogate hashes differently from Node's raw file bytes.

## Credits and license

Archify is by tt-a1i, building on work by Cocoon AI, and is MIT-licensed. The files under `upstream/` are copied from it
unchanged, and its license is kept at `upstream/LICENSE`. Archify.Net is MIT-licensed as well; see `LICENSE`, which
carries the upstream copyright notices alongside the port's.
