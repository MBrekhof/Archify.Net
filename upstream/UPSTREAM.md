# Upstream snapshot
Source: https://github.com/tt-a1i/archify — commit c21c31ff (release 3.1.0), local checkout C:\projects\archify, copied 2026-10-13.
Contents are verbatim data: schemas/, examples/, locales/, brand-marks/, references/, assets/template.html, viewer/, LICENSE (MIT),
generated-brand-marks.json (the array literal from renderers/shared/generated-brand-marks.mjs),
recipes/scenarios.json (`JSON.stringify(SCENARIO_RECIPES, null, 2)` from recipes/scenarios.mjs, minted by
tools/capture-cli-fixtures.mjs on 2026-10-15),
viewer-gate/settle.js and viewer-gate/metrics.js (the values of the two template literals that bin/visual-check.mjs passes to
`evaluate`: settle = line 1603 col 42 through line 1633 col 8, metrics = line 1635 col 58 through line 1799 col 8, 1-based and
inclusive, from `(function () {` to `})()`; no escapes or substitutions inside, no trailing newline; copied 2026-10-15).
Never edit by hand. To update: re-copy per the plan Task 1 Step 2, re-run the parity suite, port deltas.
