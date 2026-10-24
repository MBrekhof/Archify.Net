# Parity status by phase

Summary table in [`README.md`](../README.md#status).

The full suite (`dotnet test`) runs 7384 tests: 6334 in `Archify.Net.Core.Tests`, 1034 in `Archify.Net.Cli.Tests` and 16 in `Archify.Net.Mcp.Tests`.

## Phase 1 — Foundation, validation gates, architecture renderer, artifact checker

- All 6 architecture goldens are byte-identical to upstream.
- All 20 checker reports on the goldens equal upstream's, and so do the 11 committed checker issue-path probes.
- The option-level showcase profile on the no-profile fixture equals upstream's environment-driven showcase output.
- The ajv-oracle schema battery (14 cases) and all Js parity fixtures pass.

## Phase 2 — erd/grid, sequence, lifecycle, class, tree, timeline, waterfall renderers

- All 26 phase-2 parity rows are byte-identical: the 10 upstream goldens, 2 Node-minted example goldens and 14 fixture goldens.
- The erd banded grid equals upstream's `erd/grid.mjs` on every row of its fixture, and the checker report on every new golden equals upstream's.
- Every phase-2 renderer's failure receipts equal upstream's diagnostics.
- The class, tree, timeline and waterfall layout reports equal upstream's `--layout-json`, and the nine golden.mjs schema cases pass.

## Phase 3 — workflow and dataflow renderers

- 38 phase-3 rows of `ParityTests.Render_is_byte_identical_to_upstream` (14 dataflow, 24 workflow), 70 rows in all. Every
  phase-3 golden is byte-identical to upstream's except `first-draft.mixed-port-recovery`, which differs only in one Han
  label's word break (see the word-break deviation in [`deviations.md`](deviations.md)). That case is a separate fact that pins the difference.
- Among them are the two upstream goldens (`dataflow-product-analytics.html`, `workflow-agent-tool-call-rendered.html`)
  and the Node-minted `event-stream`, `holiday-planning` (zh-CN), `incident-response` and `release-delivery`.
- 24 workflow receipts equal upstream's `--layout-json` (`WorkflowReceiptTests`), plus one for the quality option.
- 20 failure receipts equal upstream's diagnostics: 14 workflow (`wf-*`) and 6 dataflow (`df-*`), supported fixes included.
- Fixtures against Node: `word-breaks` 1601 rows, `label-capacity` 24 (14 label sizes, 10 blocked-label wraps),
  `joint-label-routing` 9 (plus the unchanged-input fact) and `workflow-model` 220 rows over the workflow model functions.
- A workflow render returns the compiler receipt as `RenderResult.Layout`, which is upstream's `--layout-json` output.
  A dataflow render returns `null` there.

## Phase 4 — erd renderer and repository evidence

- 16 erd rows of `ParityTests.Render_is_byte_identical_to_upstream` are byte-identical: the two upstream examples
  (`erd-orders`, `subscription-billing`) and 14 Node-minted goldens (three first-draft diagrams, i18n in five locales,
  three label-capacity variants, and `orders` with lanes, seven domains and the standard crossing profile).
- 16 erd layout reports equal upstream's `--layout-json` (`ErdTests`).
- 11 erd failure receipts equal upstream's diagnostics. An entity left unplaced by a labelled relationship fails with
  `internal/unclassified`; that one is pinned by code only (see the erd deviation in [`deviations.md`](deviations.md)).
- Repository evidence: 25 scenarios (`RepositoryEvidenceTests.Verify_matches_upstream`) equal the receipts and payloads
  Node produced against the fixture repo (`tests/fixtures/evidence/evidence-repo.fi`, a git fast-import stream pinned to
  one HEAD), plus the git-absent receipt and 8 integration facts.
- 13 evidence render rows (`ParityTests.Evidence_render_is_byte_identical_to_upstream`) cover per-type goldens with
  source badges and payloads, and one workflow failure receipt carries the evidence.
- `RepositoryLocation` equals `parseRepositoryRemote` on 213 fixture rows, and the Js URI helpers equal V8 on 71 rows
  (`js-uri.json`).

## Phase 5 — CLI, receipts, sidecars, lock and journal, Playwright browser and visual gates

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

## Phase 6 — MCP server and SKILL.md

- `src/Archify.Net.Mcp` is the `archify-mcp` stdio MCP server (`ModelContextProtocol` 2.2.0), exposing `archify_guide`,
  `archify_validate`, `archify_render` and `archify_finalize` in-process over `Archify.Net.Cli`.
- `skill/archify/` is the self-contained agent skill package (`SKILL.md`, the 6 supported `references/*.md` files with
  `node bin/archify.mjs` replaced by `archify`, the 12 `schemas/*` files and the 32 `examples/**/*.json` files).
- `tests/Archify.Net.Mcp.Tests` tests the 4-tool catalog, `archify_guide`, `archify_validate` (including `--layout-json`),
  `archify_render` (byte-identical HTML output), `archify_finalize` with real headless Chromium and with Chromium unavailable,
  the `StdioClientTransport` subprocess smoke test and `skill/archify/` parity.

## PORT-007 — re-pin to upstream 3.2.0 (explainer excluded)

- `upstream/` and every fixture and golden are re-minted from tag `v3.2.0` (commit `208acba8`); both capture scripts are
  deterministic across two runs.
- New parity rows: timeline wall-clock ticks across DST (New York fall-back and spring-forward hours, Lord Howe's 30-minute
  shift, a round trip) and millisecond clock labels; waterfall `timeNumber` precision (a V8 table plus epoch offsets, a crowded
  axis, an endpoint shift and zero durations); erd trunk bridges blocked by an unrelated route, a wide route and an earlier
  bridge, bridges that survive nearby strokes (vertical and horizontal), and a horizontal blocked bridge; the new `ar` locale
  catalog; the `schema_version: 1` migration fallback with `semanticChecks`; git values that keep trailing spaces.
- CLI: finalize receipts and summaries carry `browserCheck`, `--browser-check` is accepted, and `--writing` is parsed as
  upstream parses it (see the explainer entry in [`deviations.md`](deviations.md)).

## PORT-008 — Pele charts

- `ExplainerCharts` (`src/Archify.Net.Core/Explainer`, over the Pele port in `src/Archify.Net.Core/Pele`) is compared
  byte for byte with archify 3.2.0's `explainer/charts.mjs` and its vendored Pele 1.3.0. Every expected value is a golden
  Node minted (`node tools/capture-fixtures.mjs`, charts section); two capture runs give the same hash.
- `ChartParityTests`: 2226/2226 at `2c68fd8`. That is 2191 `Matches_node` rows (the seeds equal to Node, plus one fuzz
  row per type) and 35 facts and theory rows that pin the stated deviations and the test method.
- Per group:

| Group | Seeds | Equal to Node | Deviation rows, each pinned by a fact | Fuzz cases | Fuzz cases that differ by a stated deviation |
|---|---|---|---|---|---|
| data | 79 | 78 | 1 | | |
| wrapper | 22 | 20 | 2 | | |
| pie | 116 | 113 | 3 | 1,000 | 79 |
| xychart | 468 | 410 | 58 | 1,000 | 80 |
| quadrantChart | 431 | 382 | 49 | 1,000 | 69 |
| sankey | 342 | 279 | 63 | 1,000 | 155 |
| radar | 473 | 444 | 29 | 1,000 | 74 |
| venn | 484 | 432 | 52 | 3,000 | 119 |
| lines | 73 | 10 | 63 | | |
| fixes | 59 | 17 | 42 | | |

- `data` is `chartFromData`, `wrapper` the fence-type redirects, `lines` the error-line remap and `fixes` the final wave's
  rows. A deviation row's fact compares it with a `-reference` row, a pinned message or a pinned output; see the
  "Pele charts (PORT-008)" section of [`deviations.md`](deviations.md#pele-charts-port-008). Each differing fuzz case is
  listed with its reason and its port output is pinned.
- `JsMathTests`: sin, cos, acos, atan2 and log10 equal V8 bit for bit on the fixture and on a 1,000,000-case run; pow
  equals Windows Node on the domain Pele reaches.
- The venn Q1 probe grid (1,386 probes) equals upstream with the Q1 fix written in.
- Test totals at `2c68fd8` (`dotnet test`, Browser category included): Core 5288, Cli 964, Mcp 13, 0 failed, 0 warnings.
- Re-pin note: Pele upstream is now at 1.9.0, while archify 3.2.0 vendors 1.3.0. A future archify re-pin of Pele is its
  own card.

## PORT-009 — the explainer type

- Ported: parser, validator, preparation, renderer, checker and panel patch (`src/Archify.Net.Core/Explainer`); the CLI's
  `validate`, `deliver`, `render`, `check`, `patch` and `finalize` on explainer drafts and the explainer browser gate
  (`src/Archify.Net.Cli`); the `archify_patch` MCP tool and the `writing` / `browserCheck` parameters; `skill/archify/SKILL.md`
  re-derived from the 3.2.0 skill (the explainer description, the `[archify explainer: ...]` mode rules and Output lines).
- Parity: every expected value is a golden Node minted from upstream 3.2.0 (`tools/explainer-seeds`, `node
  tools/capture-fixtures.mjs`, and `node tools/capture-cli-fixtures.mjs` for the CLI). Pages are compared byte for byte except
  their gzip payloads, which are compared inflated (Decision 1). Stated deviations, fixed upstream defects and the parked cases
  (card PORT-013, CARD-2523) are in [`deviations.md`](deviations.md#explainer-port-009). Both captures give the same hash on a
  second run, and `git status --porcelain tests/` is empty after a re-capture.
- Showcase: `showcase/explainer/` holds the four upstream cases (`first-rental`, `p99-tail-latency`, `tls-first-visit`,
  `finalize-delivery`) rendered by the port with `archify deliver explainer <case>.archify.md <case>.html --quality standard`,
  served by GitHub Pages (https://mbrekhof.github.io/Archify.Net/showcase/explainer/). `Showcase_pages_match_upstream` compares
  each page with the cases golden minted from v3.2.0. Upstream's own committed `docs/cases/explainer/*.html` are not the
  reference: they came from an older build, and each native child carries `<meta name="generator" content="archify 3.1.1-dev.1">`
  where v3.2.0 writes `archify 3.2.0`. So their children's SHA-256, the manifest, the bundle digest and `data-revision` differ from
  a v3.2.0 render; every other child byte is equal.
- Browser gate: compared with upstream by verdicts, group statuses and diagnostic codes (`Verdicts_match_node`). Of the showcase
  drafts, `tls-first-visit` passes `finalize explainer --browser-check`; `first-rental`, `p99-tail-latency` and
  `finalize-delivery` fail it on `explainer/browser-readability` (small workflow text) in the port; not compared with Node for these runs.
- Test totals (`dotnet test`, Browser category included): Core 6334, Cli 1034, Mcp 16, 0 failed, 0 warnings. Without the
  Browser category: Core 6334, Cli 961, Mcp 13.
