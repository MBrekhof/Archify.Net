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
- [Command-line tool](#command-line-tool)
- [MCP server and skill](#mcp-server-and-skill)
- [Diagram types](#diagram-types)
- [Source evidence](#source-evidence)
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
| 4 | erd renderer and repository evidence | Done |
| 5 | CLI, receipts, sidecars, lock and journal, Playwright browser and visual gates | Done |
| 6 | MCP server and SKILL.md | Done |

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

Phase 4 parity results:

- 16 erd rows of `ParityTests.Render_is_byte_identical_to_upstream` are byte-identical: the two upstream examples
  (`erd-orders`, `subscription-billing`) and 14 Node-minted goldens (three first-draft diagrams, i18n in five locales,
  three label-capacity variants, and `orders` with lanes, seven domains and the standard crossing profile).
- 16 erd layout reports equal upstream's `--layout-json` (`ErdTests`).
- 11 erd failure receipts equal upstream's diagnostics. An entity left unplaced by a labelled relationship fails with
  `internal/unclassified`; that one is pinned by code only (see the erd deviation below).
- Repository evidence: 25 scenarios (`RepositoryEvidenceTests.Verify_matches_upstream`) equal the receipts and payloads
  Node produced against the fixture repo (`tests/fixtures/evidence/evidence-repo.fi`, a git fast-import stream pinned to
  one HEAD), plus the git-absent receipt and 8 integration facts.
- 13 evidence render rows (`ParityTests.Evidence_render_is_byte_identical_to_upstream`) cover per-type goldens with
  source badges and payloads, and one workflow failure receipt carries the evidence.
- `RepositoryLocation` equals `parseRepositoryRemote` on 213 fixture rows, and the Js URI helpers equal V8 on 71 rows
  (`js-uri.json`).

Phase 5 parity results:

- The `archify` CLI (`src/Archify.Net.Cli`) reproduces upstream's commands, messages, receipts and exit codes. Its expected values
  are fixtures minted by running upstream's `bin/archify.mjs` (`tools/capture-cli-fixtures.mjs`), compared after masking
  paths, receipt ids, timestamps and the Chromium executable path.
- `browser-check` and `visual-check` run upstream's measurement scripts, embedded verbatim, in Playwright Chromium. The real-browser
  receipts equal upstream's on all ten diagram types.
- `finalize` (deliver, check, browser-check) was run on all 25 upstream examples at both quality profiles, 50 runs. The C# outcome
  equals Node's on every run: exit code, `ok`, `failedStage`, gates, diagnostic codes and the artifact sha256.
- **Upstream itself fails 4 of the 25 examples** (`async-job-roundtrip`, `event-stream`, `incident-response`, `release-delivery`).
  At `--quality standard` they fail browser-check with `viewer/viewport-overflow`; at `--quality showcase` (the default) they fail
  deliver with `composition/viewport-height`. The port fails them in the same places. So 21 of 25 pass at each quality, in C#
  and in Node. This is tracked as card UPSTREAM-001 (CARD-2181).

Phase 6 results:

- `src/Archify.Net.Mcp` is the `archify-mcp` stdio MCP server (`ModelContextProtocol` 2.2.0), exposing `archify_guide`,
  `archify_validate`, `archify_render` and `archify_finalize` in-process over `Archify.Net.Cli`.
- `skill/archify/` is the self-contained agent skill package (`SKILL.md`, the 6 supported `references/*.md` files with
  `node bin/archify.mjs` replaced by `archify`, the 12 `schemas/*` files and the 32 `examples/**/*.json` files).
- `tests/Archify.Net.Mcp.Tests` tests the 4-tool catalog, `archify_guide`, `archify_validate` (including `--layout-json`),
  `archify_render` (byte-identical HTML output), `archify_finalize` with real headless Chromium and with Chromium unavailable,
  the `StdioClientTransport` subprocess smoke test and `skill/archify/` parity.

The full suite (`dotnet test`) runs 3727 tests: 2782 in `Archify.Net.Core.Tests`, 932 in `Archify.Net.Cli.Tests` and 13 in `Archify.Net.Mcp.Tests`.

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
profile as an option) and the repository root for evidence (see [Source evidence](#source-evidence)).
`CheckOptions(RequireProvenance)` is not implemented in the library: the provenance checks live in the CLI (`archify check --require-provenance`).

Input and validation problems come back as `Diagnostic` records with upstream's codes, messages, subjects, evidence
and supported fixes. Any other renderer error comes back as an `internal/unclassified` diagnostic whose evidence names
the exception type, as upstream's JSON-mode boundary does. All ten diagram types render. The facade throws only
`NotSupportedException`, from `Check` when `RequireProvenance` is set (use the CLI for provenance checks).

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
| erd | `renderers/erd` | `src/Archify.Net.Core/Erd` | 4 |

All ten types are ported.

## Source evidence

A diagram can cite the code it describes. Pass the root of a local clone:

```csharp
var result = ArchifyNet.Render(DiagramType.Architecture, diagram, new RenderOptions(RepoRoot: @"C:\src\my-repo"));
```

- The diagram needs `meta.repository` (`url` and a 40-hex `revision`, optionally `provider` and `link_mode`) and
  `sources` on its nodes.
- `git` must be on PATH. The port runs it as a child process, as upstream does. `Render` with `RepoRoot` runs git
  synchronously with no timeout (as upstream), so it can block if git stalls (for example a repo on a stalled network
  share), and each git call holds thread-pool threads while it runs.
- Before rendering, the load gates verify the clone against the diagram: the origin remote identifies the same repository
  as `meta.repository.url`, the revision exists in the clone, and every cited file and line range exists at that revision.
  A failure comes back as a `repository-evidence/*` diagnostic with upstream's code and message.
- The verified payload lands in the HTML template. The SVG changes only by the source badges in dataflow, lifecycle,
  tree and workflow diagrams.
- Without `RepoRoot`, or for a diagram with no `meta.repository` and no sources, no `git` process starts.

## Command-line tool

`src/Archify.Net.Cli` is the `archify` dotnet tool (net10.0). It is a port of upstream's `bin/archify.mjs`, with the deviations
listed under [Known deviations](#known-deviations-from-upstream).

Install (there is no NuGet feed yet; pack it first):

```
dotnet pack src/Archify.Net.Cli -o nupkg
dotnet tool install --global Archify.Net.Cli --add-source nupkg
archify install-browser
```

`archify install-browser` downloads the pinned Playwright Chromium headless shell. Only `browser-check`, `visual-check` and
`finalize` need it; `archify doctor` reports whether it is installed.

Commands, each with one example (`<type>` is one of architecture, workflow, sequence, dataflow, lifecycle, erd, tree, class,
timeline, waterfall; `archify --help` prints the full usage):

| Command | Example |
|---|---|
| `render` | `archify render architecture web-app.json web-app.html` |
| `deliver` | `archify deliver architecture web-app.json web-app.html --json` |
| `finalize` | `archify finalize architecture web-app.json web-app.html --receipt receipt.json` |
| `validate` | `archify validate architecture web-app.json --layout-json` |
| `inspect` | `archify inspect architecture web-app.json` |
| `check` | `archify check web-app.html --require-provenance` |
| `browser-check` | `archify browser-check web-app.html --summary` |
| `visual-check` | `archify visual-check web-app.html --out-dir evidence` |
| `guide` | `archify guide "payment flow" --lang en` |
| `examples` | `archify examples` (renders 15 example diagrams to HTML in `./examples/`) |
| `doctor` | `archify doctor` |
| `install-browser` | `archify install-browser` |
| `demo` | `archify demo demo-out` (writes `demo-out/archify-demo.html`) |

`--quality standard|showcase` overrides the diagram's `meta.quality_profile`. Without it, `finalize` uses showcase; every other
command uses the diagram's `meta.quality_profile`, else standard. `--repo-root <path>` is the other flag; the CLI reads no
`ARCHIFY_*` environment variable.
`deliver` writes the artifact plus a `<name>.delivery.json` provenance sidecar; `finalize` runs deliver, check and browser-check
and writes a receipt.

Exit codes, as upstream's: `0` success; `1` the command ran and failed (diagnostics, a failed gate, a crash); `2` the command
could not run or was refused (usage and argument errors, an unknown command, an unsupported option, Chromium unavailable).
One exception, as upstream (A:5633-5635): `browser-check` and `visual-check` exit `1` on an unknown option or a wrong number of
arguments (`--out-dir` without a value still exits `2`).
`--json` prints the receipt on stdout; without it the diagnostics go to stderr.

## MCP server and skill

`src/Archify.Net.Mcp` is the `archify-mcp` stdio MCP server (`ModelContextProtocol` 2.2.0). It dispatches its four tools in-process through `Archify.Net.Cli`.

Pack and install both dotnet tools (and the Chromium headless shell for `archify_finalize`):

```
dotnet pack src/Archify.Net.Cli -o nupkg
dotnet pack src/Archify.Net.Mcp -o nupkg
dotnet tool install --global Archify.Net.Cli --add-source nupkg
dotnet tool install --global Archify.Net.Mcp --add-source nupkg
archify install-browser
```

Register the server with Claude Code (`--` before the command is required; `--transport stdio` may be written before the name since `stdio` is the default):

```bash
claude mcp add archify -- archify-mcp
```

The default scope is `local`; pass `--scope user` to register the server for all of the user's projects.

For MCP clients that use a `mcpServers` JSON configuration:

```json
{
  "mcpServers": {
    "archify": {
      "command": "archify-mcp",
      "args": []
    }
  }
}
```

Tools exposed by `archify-mcp`:

| Tool | Parameters | Description |
|---|---|---|
| `archify_guide` | `query?`, `lang?` (`"en"` \| `"zh"`) | Recommend a diagram type and scenario recipe for a plain-language scenario or question, or list all scenario recipes when `query` is omitted. |
| `archify_validate` | `type`, `input`, `layoutJson?`, `quality?`, `repoRoot?`, `cwd?` | Validate a JSON specification against schema, layout and composition rules, or emit the resolved layout geometry report (`--layout-json`) when `layoutJson` is `true`. |
| `archify_render` | `type`, `input`, `output?`, `quality?`, `repoRoot?`, `cwd?` | Render a JSON specification directly to a standalone HTML file and return the resolved output HTML path written by the CLI. |
| `archify_finalize` | `type`, `input`, `output`, `receipt?`, `outDir?`, `quality?`, `repoRoot?`, `candidateSha256?`, `cwd?` | Run `validate`, `deliver`, strict `check` and headless Chromium `browser-check`, returning the compact JSON finalize receipt. |

CLI exit code `0` maps to `IsError = false`; exit codes `1` and `2` map to `IsError = true` with the full JSON receipt or diagnostic text in the tool result content.

To install the self-contained agent skill (`SKILL.md`, `references/`, `schemas/`, `examples/`), copy `skill/archify/` into `~/.claude/skills/archify` (or `<repo>/.claude/skills/archify`).

## Build and test

From the repo root:

```
dotnet test
```

Tests in category `Browser` need Chromium (`archify install-browser`) and take about 2.5 minutes. `dotnet test --filter
Category!=Browser` skips them. A test that needs git or Chromium fails loudly, naming the fix, when it is missing. The real-browser
references were minted with Chromium headless shell 1243 on Windows fonts. The real-filesystem tests need Windows Developer Mode
(file symlinks) and 8.3 short names enabled on `%TEMP%`.

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
- **Workflow compiler receipts** (`tests/fixtures/workflow/*.receipt.json`): upstream's `--layout-json` for workflow inputs.
  Failure receipts also compare `suppresses`.
- **Js fixtures** (`tests/fixtures/js/*.json`): V8's answers for the JavaScript semantics the renderers lean on:
  number formatting, sorting, grapheme segmentation, text wrapping, `Date` parsing and time-zone conversion, plus the
  phase-3 helpers (word-breaks, label-capacity, joint-label-routing, workflow-model).
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
  Erd/                       the erd renderer; Grid.cs, the banded grid, is shared with class
  Evidence/                  repository evidence verifier, remote URL parser, git runner
src/Archify.Net.Cli/         the archify dotnet tool: dispatch, commands, delivery (lock, journal, sidecars), browser gates, finalize
src/Archify.Net.Mcp/         the archify-mcp stdio MCP server (archify_guide, archify_validate, archify_render, archify_finalize)
skill/archify/               self-contained agent skill package (SKILL.md, references/, schemas/, examples/)
tests/Archify.Net.Core.Tests xUnit suite
tests/Archify.Net.Cli.Tests  xUnit suite for the CLI (fixtures in tests/fixtures/cli)
tests/Archify.Net.Mcp.Tests  xUnit suite for the MCP server and skill package
tests/goldens/               upstream-rendered HTML and checker reports
tests/fixtures/              diagram inputs, receipts, layout reports, Js fixtures, checker probes
upstream/                    verbatim copy of the upstream skill package files the port embeds or tests against
                             (schemas, locales, viewer, template, brand marks, references, examples)
tools/capture-fixtures.mjs   dev-time fixture minting from upstream (Node)
tools/capture-cli-fixtures.mjs  dev-time CLI fixture minting: runs upstream's bin/archify.mjs, records args, exit, stdout, stderr
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
- Phase 4 plan: `docs/superpowers/plans/2026-10-15-phase4-erd-evidence.md`.
- Phase 5 plan: `docs/superpowers/plans/2026-10-15-phase5-cli.md`.
- Phase 6 plan: `docs/superpowers/plans/2026-10-15-phase6-mcp.md`.
- `SESSION_HANDOFF.md`: current state, carried items and the next step.

## Known deviations from upstream

- `Check` throws `NotSupportedException` when `RequireProvenance` is set: provenance is checked by `archify check --require-provenance`.
- `Validate` returns the render diagnostics followed by the checker's. On a check failure upstream `validate` returns only the checker's.
- Schema errors come in a different order when integer-like keys follow named keys in the authored JSON. The set of errors is the same.
- Brand diagnostics come in a different order when a brand is pinned as `{url, sha256}`. Upstream appends capture failures after the synchronous problems. The set is the same.
- A URL with whitespace in its userinfo part is treated as no URL. WHATWG percent-encodes the whitespace instead.
- `DiagramLoader.Parse` stops at nesting depth 64. `JSON.parse` has no limit.
- A raw lone surrogate inside a C# input string becomes U+FFFD. The escaped form `\udXXX` survives parsing, but rendering it fails with `internal/unclassified`.
- *Word breaks:* `Js.WordBreaks` matches V8's `Intl.Segmenter` word granularity (UAX #29) except in three places. (a) Han, Hiragana, Katakana, Thai, Lao, Khmer and Myanmar runs split per grapheme, where ICU uses a dictionary. This shows in sequence notes and in wrapped dataflow labels of the automatic showcase layout; `first-draft.mixed-port-recovery` differs from upstream in exactly one such label. (b) The grapheme-cluster handling differs in both directions. V8 splits inside clusters the port keeps whole: a Prepend (e.g. U+0600) before a space, and a jamo next to a Hangul syllable. The port splits across clusters V8 joins: a ZWJ before a pictograph after a letter (UAX #29 WB3c; `"a‍😀"` is one word in V8, two in the port) and a Format/Prepend-led cluster after a letter (`"a؀b"` is one word in V8, `["a","؀b"]` in the port). (c) The port assumes a non-POSIX host locale; under a POSIX/`LANG=C` default V8 splits `api.example.com` at the dots. Every other text path (tree, class, timeline wrapping; zh-CN locale) is byte-exact.
- *Time zones before the OS zone table's history:* conversions use the operating system's zone data, not Node's bundled tzdata. (a) Before a zone's first tzdata transition (local mean time, roughly pre-1900) the port shows the base offset — a `Asia/Shanghai` event at `0001-01-01T00:00:00Z` is labelled `UTC+08:06` by upstream and `UTC+08:00` by the port. (b) Before the OS table's first adjustment rule the rule is extrapolated backwards — `Pacific/Chatham`/`Australia/Lord_Howe` in 1970 read +13:45/+11:00 instead of +12:45/+10:00. UTC and fixed offsets are exact for every year; named zones are exact from the table's history onward (2026 transitions pinned by fixture).
- *`Date.parse` outside the schema shape:* strings the timeline schema rejects (`2026-10-01 10:00:00Z`, offset-less timestamps) are `NaN` in the port; unreachable through `Render` because the schema gate runs first.
- *Time zone ids the OS cannot resolve:* `meta.timezone` ids that V8's ICU accepts but the host's ICU list or `TimeZoneInfo` cannot resolve are rejected with `timeline/invalid-timezone`. The set varies with the OS build. On Windows with ICU 72.1 it includes `CET`, `EET`, `MET`, `WET`, `America/Ciudad_Juarez`, `Antarctica/Troll`, `Asia/Urumqi`, `Asia/Kashgar`, `Antarctica/Vostok`, `America/Coyhaique` and the `SystemV/*` legacy ids (card TZ-001).
- *Time zones by host:* Windows with `icu.dll` matches IANA ids case-insensitively, as V8 does. Off Windows only exact-case IANA ids are accepted. On Windows without ICU (before Windows 10 1903, Server 2019, or `System.Globalization.UseNls=true`) only UTC and fixed offsets are accepted (card TZ-002).
- With `InvariantGlobalization` on, `localeCompare` orderings (unknown-component suggestions, brand suggestions, workflow placement repair ties) compare ordinally instead of by ICU root collation (card GLOB-001).
- A valid-JSON root that is not an object reports `input/json-parse`. Upstream reports `schema/type` at `/`.
- `Js.Sort` equals V8 only for consistent comparators. A comparator that returns NaN diverges.
- The remote brand form `{url, sha256}` reports `brand/capture-unavailable` and makes no network call.
- On a failed render `Layout` is `null`. Upstream emits a partial layout report.
- `Diagnostic.Path` throws `InvalidOperationException` when `subject.path` is not a string. Upstream reads `undefined`.
- A forwarded checker diagnostic without a message gets `""` as its message. Upstream omits the key.
- The unpinned-URL hint text can differ from WHATWG `url.href` for IDN hosts, `|`, `%7e`, and backslashes in query or fragment.
- `Check` hashes the UTF-8 encoding of the string it is given. A lone surrogate hashes differently from Node's raw file bytes.
- *erd, unplaced entity:* an entity with neither `row`/`col` nor `pos` whose relationships carry labels fails with `internal/unclassified`, as upstream does. The `internal/unclassified` message and evidence carry the .NET exception where upstream has `Cannot read properties of undefined (reading '0')` / `TypeError`.
- *Evidence, root identity:* `repository-evidence/root-identity-indeterminate` is unreachable. The port compares the root with `git rev-parse --show-prefix`; upstream reaches that code only through a mocked stat.
- *Evidence, spawn errors:* Node's spawn error strings are synthesised (`spawnSync git ENOENT`, `spawnSync git ENOBUFS`). An errno outside the port's table prints its number.
- *Evidence, `root-unreadable`:* the message names the whole requested root where Node names the first missing path component.
- *Evidence, `realRoot`:* the port resolves only the final link of `RepoRoot`. When the root is reached through a junctioned or symlinked parent directory, `repoRoot` in diagnostic subjects and the paths in the `not a Git repository` and `root-not-top-level` messages differ from Node's; the verdict is the same.
- *Evidence, non-ASCII hosts:* the port rejects five hosts WHATWG URL accepts, because `IdnMapping` enforces the hyphen, label-length and non-empty-label rules. By input: `https://-bücher.example/x/y`, `https://bücher-.example/x/y`, `https://ü` + 70 `a` + `.example/x/y` (a 71-character label), `https://bücher..example/o/r` and `https://xn--bcher-kva..example/o/r` (empty label).
- *Evidence tests:* the `subdirectory-root` receipt is Windows-only (its path separator).

### CLI (phase 5)

Commands and configuration:

- The CLI reads no `ARCHIFY_*` environment variable (upstream honours `ARCHIFY_QUALITY_PROFILE`, `ARCHIFY_REPO_ROOT`,
  `ARCHIFY_CHROME` and others). Use `--quality` and `--repo-root`.
- `compare`, `brands`, `migrate` and `preview` are not ported. They answer `Unknown command` with exit 2.
- Help is upstream's usage text minus those four commands, plus an `archify install-browser` line (a command upstream does not have).
- `archify examples` writes to `./examples/` in the current directory (upstream writes into the installed skill folder). It checks
  an example's `meta.output` against the current directory, where upstream checks against the skill root.
- `archify doctor` checks the .NET runtime, the embedded resources, git on PATH and the Playwright Chromium install, in upstream's
  `[ok|missing|invalid]` format. It looks for Chromium in Playwright's default location only and ignores `PLAYWRIGHT_BROWSERS_PATH`,
  so it reports `[missing]` for a relocated browser store.
- `deliver --open` does not open anything. It reports upstream's `unsupported` fallback (`{requested:true, status:"unsupported",
  target, method:null}`) and its stderr notice.
- No `update` block in the `deliver` and `finalize` receipts, and no update-notice lines.
- Hand-ported dispatch, no System.CommandLine: messages, check order and exit codes are upstream's.

Browser gates:

- Pinned Playwright Chromium (headless shell) only; there is no system Chrome or Chromium fallback. Launch flags are Playwright's
  defaults, not upstream's list (measured metrics are identical on the real-browser receipts). Timeouts are Playwright's defaults.
- When Chromium is missing, `browser-check`, `visual-check` and `finalize` report upstream's "Chrome unavailable" result (status
  `skipped`, exit 2, `viewer/chrome-unavailable`), but the error and fix text name `archify install-browser` where upstream names
  `ARCHIFY_CHROME`. The receipt keeps upstream's `chrome{status,executable}` key names; `executable` is Playwright's path.
- `visual-check` PNG bytes depend on the browser build and are not golden-compared.
- Staged screenshots live in a private temp directory, not beside the evidence. In place of upstream's staging directory, an
  exclusive probe file `.archify-evidence-<guid>.tmp` is created and deleted in the evidence directory before the browser starts, so
  a directory where files cannot be created is refused there, as upstream refuses it.

Atomic output is not ported (all of upstream's `atomic-output.mjs`, `owned-temp-directory.mjs` and `recover-output.mjs`). Artifacts and
sidecars are written to a temp file and swapped in with `File.Replace` or `File.Move`, with a short retry on sharing violations.
The directory lock (`.archify-delivery-lock.json`) and the pending journal are kept. The visible effects:

- A target, sidecar or lock replaced by another process during a delivery is not detected: the last writer wins (D1). The lock still
  stops cooperating archify processes.
- Hard-linked artifacts and sidecars are accepted, in `deliver`, in plain `check`, in `browser-check`, `visual-check` and `finalize`
  (D2): `output/target-hardlinked` and `delivery/provenance-hardlink-unsupported` never occur. A replacement with identical bytes passes
  the re-inspection.
- `render`, `deliver` and `finalize` onto an existing directory report `output/write` or `delivery/commit`, where upstream reports
  `output/target-not-regular-file` (D3).
- No rollback recovery directories and no `*-recovery-required` codes (D4). A commit failure after the HTML was replaced leaves the
  journal, so `check` fails closed, and the backup file is named in the `delivery/commit` evidence. A lock whose content write fails
  is reported as `delivery/lock-acquire`; upstream records a failed lock initialisation.
- `deliver` creates only the directory lock, not the legacy per-artifact lock (D5). `check` and `finalize` still read the legacy
  `<name>.delivery-lock.json`. Legacy migration paths (raw-head and legacy-extension journals and sidecars) are not looked for.
- Temp and backup files are `.archify-delivery-<guid>.tmp` and `.bak` in the target's directory (upstream: a staging directory, and
  a dot-file beside the target) (D6). `render` writes `.archify-render-<pid>-<guid>.tmp` beside the target.
- `visual-check` and `browser-check` publish evidence without a staging directory: no `committed-with-warning`,
  `viewer/evidence-cleanup-incomplete` or `publication` key. Same-kind replacement of the evidence directory between preflight and
  commit is not caught.
- `finalize` stage records hold `command: ["archify", …args]` instead of upstream's `[node.exe, archify.mjs, …]`, the stages run in
  process, a failed stage's `error.message` is `Command failed: archify <args>\n<stderr>`, and there is no `signal` key (D8).
  `finalize/receipt-publication` cannot arise; a failed receipt write is `finalize/runtime`.
- The `ARCHIFY_CHECK_MAX_BUFFER` case (`artifact/check-output-limit`) does not exist: the checker runs in process (D10).

Diagnostics and messages:

- A full-mode `internal/unclassified` diagnostic names the user's input in `subject.input`; upstream names its deleted temp snapshot.
- A valid-JSON root that is not an object (`[]`, `null`) reports stage `input` in the CLI receipt; upstream reports stage `render`.
- Messages that come from the OS or V8 are .NET's: `input/json-parse` reasons, `output/write`, `input/read` (Node's text is reproduced
  for ENOENT, EISDIR and EACCES only). Tests compare them by code and subject.
- A realpath failure other than ENOENT/ENOTDIR (EACCES, a symlink loop) in `check` crashes upstream with a stack; the port reports
  `inspection-failed` or reads through (unverified against Node).
- An input path such as `x.json.` (trailing dot or space) reports a trimmed `subject.input`.
- Off Windows, hard-link alias detection is lost and file identity falls back to resolved-path equality. Only the build is checked
  off Windows.

Windows path ceilings:

- A volume mount point (a junction to `\\?\Volume{…}`) counts as a link, and path semantics rejects its target as
  `windows-namespace-unsupported`; upstream's libuv treats a mount point as a directory.
- The native path calls (realpath, stat, lstat) give a fully qualified path of 260 characters or more the `\\?\` (or `\\?\UNC\`)
  prefix, as Node's `toNamespacedPath` does, so they work beyond MAX_PATH without `LongPathsEnabled`. Shorter paths keep their plain
  spelling, where Node namespaces every path. Unverified end to end: the development machine has `LongPathsEnabled=1`.

The four upstream examples listed under Phase 5 parity results fail `finalize` in both implementations; this is card UPSTREAM-001
(CARD-2181), not a port defect.

## Credits and license

Archify is by tt-a1i, building on work by Cocoon AI, and is MIT-licensed. The files under `upstream/` are copied from it
unchanged, and its license is kept at `upstream/LICENSE`. Archify.Net is MIT-licensed as well; see `LICENSE`, which
carries the upstream copyright notices alongside the port's.
