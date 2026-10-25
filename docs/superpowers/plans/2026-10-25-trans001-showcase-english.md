# TRANS-001: English showcase explainers

Card TRANS-001 (CARD-2530): "Explainer pages: 3 of 4 pages are in Chinese, should be translated to English. Same for other
examples pages."

## Scope

- `showcase/explainer/first-rental`, `p99-tail-latency` and `finalize-delivery` are Chinese (`@meta locale=zh-CN`).
  `tls-first-visit` is English already.
- "Other examples pages": the pages GitHub Pages serves from `upstream/examples/*.html` (linked from the README type table)
  show English text. Their only CJK characters are two comments in the viewer's embedded script, which is upstream's
  verbatim JS (no visible text). Not changed: `upstream/` is a pinned copy of upstream and parity rests on it.
- Out of scope: `upstream/examples/holiday-planning.workflow.json`, `delivery.explainer.json` and their copies in
  `skill/archify/examples/` (example inputs, not pages; `SkillPackageTests` pins them to upstream), and the
  `cases` seed group (upstream's Chinese drafts stay the parity input for the port).

## Steps

1. Translate the three drafts in place. Keep every heading anchor `{#id}`, block id, node id, lane id, source id, table
   shape, status tag kind (`{bad:…}`, `{caution:…}`, `{good:…}`) and numbers. Change `@meta locale=zh-CN` to `locale=en`.
   `finalize-delivery` keeps its `repo_url`, `repo_revision` and `@source` paths and line ranges.
2. Add the seed group `tools/explainer-seeds/showcase.json`: four seeds `{name, call: "deliver", showcase: true,
   draft: {text, file}}`, `text` being the committed `showcase/explainer/<name>.archify.md`.
   `run.mjs`'s `deliver` pins `text` to that repo file when `showcase` is set, and to `v3.2.0:docs/cases/explainer/` otherwise.
   The rest of the call is unchanged: upstream's own `bin/archify.mjs deliver explainer … --quality standard`.
3. Re-mint with `node tools/capture-fixtures.mjs` (the full capture, so the fixed oracle writes any `.fixed.html`; Decision 2
   applies if a shorthand workflow gets a branch lane on an en page). Expect `git status tests/` to show only the new
   `tests/goldens/explainer/showcase/` files.
4. Test side (`ExplainerParityTests`): add `"showcase"` to `Groups` (so `Matches_node` covers the group), and point
   `Showcase_pages_match_upstream` at `Expected("showcase", name)`. The C# `deliver` arm needs no change.
5. Re-render the pages with the port: `archify deliver explainer <case>.archify.md <case>.html --quality standard` in
   `showcase/explainer/`, no `.delivery.json` sidecars committed.
6. Docs: `showcase/explainer/index.html` (all four English, drop the language notes), `README.md` showcase paragraph
   (drafts translated from upstream's cases; upstream's own copies are the Chinese originals), `docs/status.md` showcase and
   browser-gate bullets (the browser verdicts there are of upstream's four case drafts, not of the showcase pages),
   `tools/explainer-seeds/README.md` (the `showcase` group), `SESSION_HANDOFF.md`.

## Checks

- `dotnet test` (Browser category included), 0 failed, totals updated in `docs/status.md`.
- `archify finalize explainer <case>.archify.md <case>.html --quality standard` on each translated draft passes the deliver and
  check gates (a longer English label must not break a fit or readability gate that the Chinese draft passed).
- Second capture run gives the same goldens (no diff).
- Self-review list: every block id unchanged (diff ids between old and new drafts), `locale=en` in all four, the seed text
  equals the file byte for byte (LF endings, trailing newline), no `.delivery.json` in `git status`.

## Review

No external review: Martin, 2026-10-25, "no gemini review at this moment". Claude's self-review list above was run instead.
