# Archify.Net

A C# port of [archify](https://github.com/tt-a1i/archify) 3.1.0, pinned at upstream commit `c21c31ff`.
It renders architecture diagrams from JSON into standalone HTML with inline SVG, and checks the result.
The correctness bar is byte parity: the HTML it writes is byte-identical to upstream's for the same input.
The reader-side viewer stays JavaScript and is embedded verbatim from `upstream/`.

.NET 10, C# 14, no NuGet dependencies in the library.

## Library surface

`Archify.Net.Core` exposes one static facade, `ArchifyNet`, in namespace `Archify.Net`:

- `Render(type, diagram, options)` runs the load gates and the renderer. It returns the HTML, SVG, cards, diagnostics and layout report.
- `Check(html)` runs the artifact checker. Its report JSON equals upstream's `scripts/check-render-output.mjs` minus `file`.
- `Validate(type, diagram, options)` renders, checks, and returns the render diagnostics followed by the checker's.

Input and validation problems come back as diagnostics with upstream's codes, messages, subjects, evidence and fixes.
Any other renderer error comes back as an `internal/unclassified` diagnostic whose evidence names the exception type, as upstream's JSON-mode boundary does.
The facade throws only `NotSupportedException`:

- From `Render` and `Validate` for a diagram type that lands in phases 2 to 4.
- From `Check` when `RequireProvenance` is set. Provenance checks land in phase 5.

## Build and test

From the repo root:

```
dotnet test
```

The parity suite reads the goldens and fixtures committed under `tests/` and `upstream/`. It needs no network and no Node.

`tools/capture-fixtures.mjs` is a dev-time tool that mints those fixtures from upstream. It needs Node 24 and an
upstream repo checkout at `C:\projects\archify` on commit `c21c31ff`. It reads the skill package root inside it,
`C:\projects\archify\archify`. CI never runs it.

## Phases

| Phase | Scope | Status |
|---|---|---|
| 1 | Foundation, validation gates, architecture renderer, artifact checker | Done |
| 2 | erd/grid, sequence, lifecycle, class, tree, timeline, waterfall renderers | Planned |
| 3 | workflow and dataflow renderers | Planned |
| 4 | erd renderer and repository evidence | Planned |
| 5 | CLI, receipts, sidecars, lock and journal, Playwright browser and visual gates | Planned |
| 6 | MCP server and SKILL.md | Planned |

Phase 1 parity results:

- All 6 architecture goldens are byte-identical to upstream.
- All 20 checker reports on the goldens equal upstream's, and so do the 11 committed checker issue-path probes.
- The option-level showcase profile on the no-profile fixture equals upstream's environment-driven showcase output.
- The ajv-oracle schema battery (14 cases) and all Js parity fixtures pass.

## Known deviations from upstream

- `Render` throws `NotSupportedException` for the nine diagram types that land in phases 2 to 4. `Validate` throws it too once the input passes the load gates.
- `Check` throws `NotSupportedException` when `RequireProvenance` is set.
- `Validate` returns the render diagnostics followed by the checker's. On a check failure upstream `validate` returns only the checker's.
- Schema errors come in a different order when integer-like keys follow named keys in the authored JSON. The set of errors is the same.
- Brand diagnostics come in a different order when a brand is pinned as `{url, sha256}`. Upstream appends capture failures after the synchronous problems. The set is the same.
- A URL with whitespace in its userinfo part is treated as no URL. WHATWG percent-encodes the whitespace instead.
- `DiagramLoader.Parse` stops at nesting depth 64. `JSON.parse` has no limit.
- A raw lone surrogate inside a C# input string becomes U+FFFD. The escaped form `\udXXX` survives parsing, but rendering it fails with `internal/unclassified`.
- CJK word wrapping is outside the byte-parity guarantee. Phase 1 cannot reach it yet.
- A valid-JSON root that is not an object reports `input/json-parse`. Upstream reports `schema/type` at `/`.
- `Js.Sort` equals V8 only for consistent comparators. A comparator that returns NaN diverges.
- The remote brand form `{url, sha256}` reports `brand/capture-unavailable` and makes no network call.
- On a failed render `Layout` is `null`. Upstream emits a partial layout report.
- `Diagnostic.Path` throws `InvalidOperationException` when `subject.path` is not a string. Upstream reads `undefined`.
- A forwarded checker diagnostic without a message gets `""` as its message. Upstream omits the key.
- The unpinned-URL hint text can differ from WHATWG `url.href` for IDN hosts, `|`, `%7e`, and backslashes in query or fragment.
- `Check` hashes the UTF-8 encoding of the string it is given. A lone surrogate hashes differently from Node's raw file bytes.
- Time zones and `Intl` segmentation arrive in phase 2 with their own stated deviations.
