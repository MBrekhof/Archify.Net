# Parity status by phase

Summary table in [`README.md`](../README.md#status).

The full suite (`dotnet test`) runs 3993 tests: 3016 in `Archify.Net.Core.Tests`, 964 in `Archify.Net.Cli.Tests` and 13 in `Archify.Net.Mcp.Tests`.

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
