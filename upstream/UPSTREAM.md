# Upstream snapshot
Source: https://github.com/tt-a1i/archify — tag v3.2.0, commit 208acba8d412335185b2056fedf54b3405df43fb (release 3.2.0), local
checkout C:\projects\archify, copied 2026-10-22 (previously commit c21c31ff, release 3.1.0, copied 2026-10-13).
Contents are verbatim data: schemas/, examples/, locales/, brand-marks/, references/, assets/template.html, viewer/, LICENSE (MIT),
generated-brand-marks.json (the array literal from renderers/shared/generated-brand-marks.mjs),
recipes/scenarios.json (`JSON.stringify(SCENARIO_RECIPES, null, 2)` from recipes/scenarios.mjs, minted by
tools/capture-cli-fixtures.mjs),
viewer-gate/settle.js and viewer-gate/metrics.js (the values of the two template literals that bin/visual-check.mjs passes to
`evaluate`: at v3.2.0 settle = line 1677 col 42 through line 1707 col 8, metrics = line 1709 col 58 through line 1873 col 8
(at c21c31ff: 1603-1633 and 1635-1799), 1-based and inclusive, from `(function () {` to `})()`; no escapes or substitutions
inside, no trailing newline; unchanged from c21c31ff to v3.2.0).
Not copied until PORT-009 (explainer): schemas/explainer.schema.json, examples/delivery.explainer.json,
references/explainer-authoring.md.
Never edit by hand. To update: re-copy per the phase-1 plan Task 1 Step 2 (and the PORT-007 plan Task 1), re-run the parity
suite, port deltas.
