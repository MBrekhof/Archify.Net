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

- From `Render` and `Validate` for a diagram type that lands in phases 3 and 4: workflow, dataflow and erd.
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
| 2 | erd/grid, sequence, lifecycle, class, tree, timeline, waterfall renderers | Done |
| 3 | workflow and dataflow renderers | Planned |
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

## Known deviations from upstream

- `Render` throws `NotSupportedException` for the three diagram types that land in phases 3 and 4: workflow, dataflow and erd. `Validate` throws it too once the input passes the load gates.
- `Check` throws `NotSupportedException` when `RequireProvenance` is set.
- `Validate` returns the render diagnostics followed by the checker's. On a check failure upstream `validate` returns only the checker's.
- Schema errors come in a different order when integer-like keys follow named keys in the authored JSON. The set of errors is the same.
- Brand diagnostics come in a different order when a brand is pinned as `{url, sha256}`. Upstream appends capture failures after the synchronous problems. The set is the same.
- A URL with whitespace in its userinfo part is treated as no URL. WHATWG percent-encodes the whitespace instead.
- `DiagramLoader.Parse` stops at nesting depth 64. `JSON.parse` has no limit.
- A raw lone surrogate inside a C# input string becomes U+FFFD. The escaped form `\udXXX` survives parsing, but rendering it fails with `internal/unclassified`.
- *CJK word breaks in sequence notes:* upstream segments Han runs with ICU's dictionary (`Intl.Segmenter` word granularity); the port breaks per grapheme. Only a sequence `note` containing Han is affected; every other text path (tree, class, timeline wrapping; zh-CN locale) is byte-exact.
- *Time zones before the OS zone table's history:* conversions use the operating system's zone data, not Node's bundled tzdata. (a) Before a zone's first tzdata transition (local mean time, roughly pre-1900) the port shows the base offset — a `Asia/Shanghai` event at `0001-01-01T00:00:00Z` is labelled `UTC+08:06` by upstream and `UTC+08:00` by the port. (b) Before the OS table's first adjustment rule the rule is extrapolated backwards — `Pacific/Chatham`/`Australia/Lord_Howe` in 1970 read +13:45/+11:00 instead of +12:45/+10:00. UTC and fixed offsets are exact for every year; named zones are exact from the table's history onward (2026 transitions pinned by fixture).
- *`Date.parse` outside the schema shape:* strings the timeline schema rejects (`2026-10-01 10:00:00Z`, offset-less timestamps) are `NaN` in the port; unreachable through `Render` because the schema gate runs first.
- *Time zone ids the OS cannot resolve:* `meta.timezone` ids that V8's ICU accepts but the host's ICU list or `TimeZoneInfo` cannot resolve are rejected with `timeline/invalid-timezone`. The set varies with the OS build. On Windows with ICU 72.1 it includes `CET`, `EET`, `MET`, `WET`, `America/Ciudad_Juarez`, `Antarctica/Troll`, `Asia/Urumqi`, `Asia/Kashgar`, `Antarctica/Vostok`, `America/Coyhaique` and the `SystemV/*` legacy ids (card TZ-001).
- *Time zones by host:* Windows with `icu.dll` matches IANA ids case-insensitively, as V8 does. Off Windows only exact-case IANA ids are accepted. On Windows without ICU (before Windows 10 1903, Server 2019, or `System.Globalization.UseNls=true`) only UTC and fixed offsets are accepted (card TZ-002).
- A valid-JSON root that is not an object reports `input/json-parse`. Upstream reports `schema/type` at `/`.
- `Js.Sort` equals V8 only for consistent comparators. A comparator that returns NaN diverges.
- The remote brand form `{url, sha256}` reports `brand/capture-unavailable` and makes no network call.
- On a failed render `Layout` is `null`. Upstream emits a partial layout report.
- `Diagnostic.Path` throws `InvalidOperationException` when `subject.path` is not a string. Upstream reads `undefined`.
- A forwarded checker diagnostic without a message gets `""` as its message. Upstream omits the key.
- The unpinned-URL hint text can differ from WHATWG `url.href` for IDN hosts, `|`, `%7e`, and backslashes in query or fragment.
- `Check` hashes the UTF-8 encoding of the string it is given. A lone surrogate hashes differently from Node's raw file bytes.
