# PERF-003: huge node width/height hangs the workflow compile — plan

Card PERF-003 (CARD-2186). Upstream issue: [tt-a1i/archify#1072](https://github.com/tt-a1i/archify/issues/1072), still
open and unfixed on `main` and `dev` (checked 2026-11-09). Branch `perf-003-probe-skip`, cut from `master` `81c5261`.

## Root cause (reproduced, spike `.superpowers/sdd/2026-10-15-perf003/report.md`)

`automaticPortCandidates` (upstream v3.2.0 `archify/renderers/workflow/workflow-compiler.mjs:4470`, port
`src/Archify.Net.Core/Workflow/WorkflowCompilation.Routing.cs:1089`) probes port positions along a node side:

```js
for (let offset = 0; offset <= extent / 2 - 16; offset += 12) {
  for (const sign of [direction, -direction]) { point[axis] = center[axis] + sign * offset; if (clear(point) && !dup) push }
  if (candidates.length >= 2) return candidates.slice(0, 2);
}
```

On a node whose `width` (or `height`, for left/right sides) is around 1e25 and up, the side centre is so large that
`center + 12k` rounds back to the same double for millions of `k`. Every such step tests the same blocked point, so the
loop runs `extent / 24` times. 1e25 takes seconds, 1e26 minutes; from about 2^57 on `offset += 12` no longer changes
`offset`, and the loop never ends. Trigger: two automatic edges between the same pair (the second edge finds the first
edge's endpoint at the side centre). Fix discovery recompiles per candidate repair, multiplying the cost. Through PORT-009
the same input embedded in an explainer (`@diagram workflow source=…`) hangs `deliver`/`finalize explainer` too: the
port renders native diagrams in process with no timeout, where upstream's 120 s child timeout fails the stage.

Upstream timings measured 2026-11-09 on the spike's two-edge input (`render-workflow.mjs`, v3.2.0):

| width | outcome | time |
|---|---|---|
| 1e16, 1e18 | renders | < 1 s |
| 1e19, 1e21–1e24 | `workflow/solver-budget-exhausted` | < 1 s |
| 1e20 | `layout/constraint` | < 1 s |
| 1e25 | `workflow/solver-budget-exhausted` | 6 s |
| 1e26 | (spike, via `validate`) `workflow/node-overlap` | ~100 s |
| 1e30, 1e154, 1e308 | never returns | — |

## The fix: skip the no-op steps (output-preserving)

The card's option (b), "stop the loop once `center ± offset` no longer moves the point", would change output wherever
upstream does terminate: at 1e22 upstream walks ~87k no-op steps and *then* finds clear points about 1e6 px from the
centre; stopping early returns `[preferred]` instead. So this plan takes a stricter form of (b):

**A step whose two rounded probe points equal the previous step's points is a no-op.** Its points are either not clear
(rejected) or already in `candidates` (rejected as duplicates), and the `>= 2` check gives the same answer as after the
previous step. So skipping it cannot change the result. After each step that does not return, jump `offset` to the first
later multiple of 12 at which either rounded probe point differs from the current one.

- `fl(c + s·o)` is monotone in `o`, so "differs from the point at the current offset" is monotone, and the first moving
  step is found by exponential then binary search over the step index `k` (`offset = 12k`). Cost: O(log(extent)) per
  call instead of O(extent).
- Common case first: if the very next step (`offset + 12`) already moves, no search runs; ordinary diagrams take the
  same path as today with one extra comparison per step.
- If no step up to the bound `extent / 2 - 16` moves, the jump lands past the bound and the loop ends, returning
  `candidates` or `[preferred]` exactly as upstream would after finishing.
- Doubles: the search stops when the midpoint equals either end (adjacent doubles with a gap > 1), so it terminates for
  any finite input. A non-finite centre (NaN) makes every comparison "moved", so no skip happens and behaviour is unchanged.
- The jump lives in a static helper `NextProbeOffset(double center, int sign, double offset, double limit)` (centre
  coordinate on the probe axis, `sign` = `direction`), so the unit test can drive it without a compile.
- `ponytail:` comment at the jump naming the reason (upstream steps 12 px; at 1e25+ those steps vanish in rounding).

**Where the output can differ from upstream:** only on inputs where upstream's loop would run past offset 2^55 (≈ 3.6e16,
about 3e15 iterations — years of run time) or never end. There upstream's `offset += 12` rounds or stops changing; the
port uses exact `12k`. Every input that terminates upstream in practical time gives byte-identical output. Stated in
`docs/deviations.md` (replaces the "Shared with upstream, not a deviation" bullet, lines 36–39).

Considered and not reproduced: probes that *move* but stay blocked (a huge occupied clearance needs a huge edge `width`,
which the schema allows). Upstream rendered a 1e16-wide node with edge widths 1e12, 1e13 and 1e15 in under a second
(2026-11-09), so the skip stays limited to steps whose points do not move.

Not taken: a schema cap on `width`/`height` (option c) — a new validation rule upstream doesn't have, and unnecessary.

## Tests (written first, red before the fix where they can be)

1. **Parity rows (Node goldens from v3.2.0, via `tools/capture-fixtures.mjs`)**, each the spike's two-edge input with one
   width, minted with `render-workflow.mjs` like the existing `t2-*`/`t4-*` rows:
   - `perf3-wide-1e18` → rendered HTML golden (exercises a few skipped steps per call: at 5e17 the double spacing is 64);
   - `perf3-wide-1e20` (`layout/constraint`), `perf3-wide-1e22` (`workflow/solver-budget-exhausted`, ~87k skipped steps
     per call), `perf3-wide-1e25` (6 s upstream) → failure receipts in `Workflow_fault_diagnostics_match_upstream_receipt`.
   These pass before the fix too (the port is a 1:1 port today, 1e25 just slower); they guard that the fix keeps parity.
2. **Hang rows (dedicated tests of the corrected result; red = timeout before the fix):** width `1e30` (the spike's
   minimal input) and height `1e308` on a left/right edge (seed 8244 shape) each finish within 10 s with a failure
   receipt (the code is whatever the exact-arithmetic loop yields; the test pins it after the first green run and states
   it is the port's own result, no Node golden exists). Run on a worker thread with a timeout so a regression fails
   instead of hanging the suite.
3. **Explainer row:** a draft with `@diagram workflow source=wide.workflow.json` beside the 1e30 spec. `deliver`
   finishes within 10 s with the `diagram-render` failure.
4. **Unit test of the skip helper** on a synthetic probe: centre 5e21, one occupied coordinate at the centre; the
   returned candidates equal those of a brute-force run of the original loop (feasible at that magnitude: ~87k steps).
5. Full suite: `dotnet test` (Browser category included) stays green: Core 6341, Cli 1037, Mcp 17 before this branch.

## Docs

- `docs/deviations.md`: replace lines 36–39 with the deviation above; add under "Core library…".
- `README.md` / `docs/status.md`: test totals.
- Card PERF-003 conclusion cites the commit. No upstream comment on #1072 without Martin's go (outward action).

## Gemini plan review (2026-11-09) and how it was settled

Review: the no-op claim holds (Q1) and output is identical wherever upstream finishes (Q3). Findings against the code:

1. *Fractional bisection midpoint* (Critical in the review, against the plan's wording): the code bisects over multiples of 12
   (`lo + 12·floor((hi−lo)/24)`), so the midpoint is never fractional. Pinned by the 5e17 and 2^58 brute-force rows.
2. *No move up to a bound ≥ 2^56; `offset + 12 == offset`*: the code doubles past the bound and returns a value above it.
   Pinned by `Huge_offsets_advance` (the reviewer's three inputs).
3. *Single-sign check*: the code tests both `center + o` and `center − o`; the redundant `sign` parameter is removed. Pinned by
   the power-of-two rows (`center − offset` moves first, at 24 not 36).
4. *Non-finite centre or bound* (valid): `NextProbeOffset` returns NaN, which ends the loop for any bound. Pinned by
   `Non_finite_walks_end` and `Non_finite_nodes_return` (`width: 1e309`; two 1.5e308 nodes side by side).
5. *Missing `WorkflowReceiptTests` row for `perf3-wide-1e18`* (valid): added.
6. *Huge edge `width` with a huge node* (reproduced on the port: killed at 60 s): contrived, parked as card PERF-004.
7. *Wording minors*: deviations entry rewritten without the `12k` claim.

## Self-review: cases the change must handle

| case | covered by |
|---|---|
| ordinary node widths (no skip ever) | entire existing parity suite (every workflow golden) |
| few skipped steps | `perf3-wide-1e18` golden |
| many skipped steps, terminates upstream | `perf3-wide-1e22`, `perf3-wide-1e25` receipts; unit test 4 |
| upstream never terminates | hang rows 1e30 (width) and 1e308 (height, vertical side) |
| both search directions (`direction` = ±1) | counterpart left vs right of the centre in unit test 4 |
| bound reached without a moving step | unit test 4 variant with occupied far side, small extent |
| all eight call sites (`:1244`, `:1245`, `:1320`, `:1321`, `:1364`, `:1365`, `:1387`, `:1388`) | one function changed; callers untouched |
| explainer reach | explainer row |
