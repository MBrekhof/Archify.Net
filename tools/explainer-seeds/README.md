# Explainer seeds (PORT-009)

Inputs for the explainer parity suite. `node tools/capture-fixtures.mjs` (explainer section, at the end) runs
`tools/explainer-seeds/run.mjs` twice, as children with the scrubbed environment, and writes the goldens.
`tests/Archify.Net.Core.Tests/ExplainerParityTests.cs` compares the port with them. Node decides every expected value;
nothing here is hand-written output.

## Seeds

`<group>.json` is a JSON array of seeds `{name, call, args, files?, repo?}`:

- `call` names the upstream function, imported from `file:///C:/projects/archify/archify/explainer/<module>.mjs`:
  `parseExplainer`, `parseInline`, `renderInline`, `parseExplainerDiff`, `lintExplainerWriting`, `validateExplainer`,
  `compileExplainerDiagram`, `resolveExplainerSources`, `prepareExplainerDiagrams`, `validateExplainerDiagramTarget`,
  `prepareExplainer`, `readPreparedBundle`, `layoutExplainerPanels`, `renderExplainer`, `checkExplainerHtml`,
  `preparePanelPatch`.
- `args` is the argument list, awaited as `call(...args)`. `<DIR>` (the seed's directory) and `<REPO>` (the evidence
  repository) are replaced in every string of it.
- `files` maps a path relative to the seed's directory to `{text}`, `{base64}` or `{zeros: n}` (n zero bytes, for size
  limits). `{base64, zeros: n}` is the decoded bytes followed by n zero bytes (an image header padded past a limit).
  They are written before the call.
- `repo: true` builds the phase-4 evidence repository (`tests/fixtures/evidence/evidence-repo.fi`, HEAD
  `77eb757055fa2c17900278114600cfe23c3957d1`), commits `tests/fixtures/evidence/explainer-repo.fi` on top (a secret,
  CRLF, Latin-1 and NUL file; HEAD `f691b4b9368318ae74b3c16e3bd96e9d33788e34`) and adds the origin
  `https://github.com/example/evidence`. It is set as `repoRoot` on the last argument, which must be an options object.
  A draft-based seed gets it in the draft's prepare options instead. `repo: {origin}` builds the same repository with
  that origin instead (`null`: no origin at all), for the `deriveRepository` branches: no origin, a local path, a
  credentialed, non-`git` SSH or query origin, and a non-provider host (`link_mode: local-only`). The C# side builds
  each with `EvidenceRepo.CreateExplainer(origin)`.

Each seed runs with its own fresh directory as the working directory, so relative paths resolve inside it.

A **draft** is `{text, file = 'draft.archify.md', options?}`. The runner writes `text` to `<DIR>/<file>` and prepares it
as `render-explainer.mjs` does: `prepareExplainer(text, {...options, inputPath})`. Three calls take one:

- `renderExplainer`: `{name, call, draft}` renders the prepared draft with
  `renderExplainer(bundle.document, {...bundle, preparedBundle: bundle, pageDirectory: <DIR>})`. The golden is the page.
- `checkExplainerHtml` and `preparePanelPatch`: `page: {draft, tamper?, file = 'page.html'}` renders that page, applies
  `tamper` (a list of exact `{from, to}` replacements, each `from` occurring exactly once), and writes it to
  `<DIR>/<file>`. For `checkExplainerHtml` the page text is prepended to `args`; `preparePanelPatch` names the file in its
  options (`artifactPath: "<DIR>/page.html"`). No input page is stored: each side renders it itself.
- `readPreparedBundle` is two-step: `{name, call, draft, mutate?, read?}`. The runner prepares `draft` (draft A), writes
  its bundle with upstream's `writePreparedBundle(<DIR>, bundle)`, applies `mutate`, and calls
  `readPreparedBundle(file, read.text ?? draft.text, <DIR>/<read.file> or draft A's path, read.options)`. `read.text` with
  `read.file` is also written there. `mutate` has at most one key:
  - `path`: the bundle's `sourcePath` becomes `<DIR>/<path>`;
  - `quality`, `writingMode`: that bundle field is set (`writingMode: null` deletes it);
  - `document`: `{from, to}` replacements over `JSON.stringify(bundle.document)`;
  - `signed`: `{from, to}` replacements over `JSON.stringify(bundle)` (a frozen dependency's identity, say);
  - `bytes`: `{from, to}` replacements over the written file's text;
  - `dependency`: a `files` map applied after writing (an entry `null` deletes that file).

  `path`, `quality`, `writingMode`, `document` and `signed` recompute the bundle's digest (prepare.mjs:14), so the read
  reaches the check after it; `bytes` and `dependency` do not. The C# side reads the bundle in memory
  (`ExplainerPrepare.ReadPrepared`), so `bytes` must leave valid JSON.

`deliver` (the `cases` group) is `{name, call: "deliver", draft: {text, file}}`: the runner checks that `text` is
`v3.2.0:docs/cases/explainer/<name>.archify.md`, writes it to `<DIR>/<file>` and runs the oracle's own
`bin/archify.mjs deliver explainer <file> <name>.html --quality standard` there, with no `--repo-root`. The golden is
`<name>.html`. The C# side renders `ExplainerPipeline.Render(ExplainerPipeline.Prepare(text, standard), <DIR>)`.

Two shapes prepare the arguments of a generic call:

- `parsed: <text>` (with `validateExplainer`): the runner parses the text first (`parseExplainer(text, {inputPath:
  args[0].inputPath})`, which must yield a document) and calls `validateExplainer(document, {sourceMap, ...args[0]})`.
- `texts: [...]` (with `renderInline`): one call per text with the same context object `args[0]`, as render.mjs shares
  footnotes and citations across fields. The golden is `{html: [...], footnotes, citations: [[id, number], ...]}`.

The capture refuses a seed name that is not `[\w.-]+`, that ends in `.fixed`, or that repeats within its group ignoring
case (the goldens live on Windows).

## Goldens

- `tests/goldens/explainer/<group>/<name>.json` is `JSON.stringify(result, null, 2) + "\n"`, after the tokens below.
- A `renderExplainer` or `deliver` page is `<group>/<name>.html`, as returned (packed children tokenised as below).
- A thrown error is `{"thrown": {"name", "message", "code"}}`. `code` is `error.archifyDiagnostics` when present (or the
  `diagnostics` that inline.mjs:120 attaches), otherwise `error.code`.
- `<name>.fixed.json` or `.fixed.html` beside a golden comes from the corrected oracle: a copy of the skill with
  Decisions 2 and 3 written in (render.mjs:38 `|| 'zh-CN'` becomes `|| 'en'`; the diagrams.mjs:270 branch lane is
  `Branch ${row}` unless the locale starts with `zh`, the locale passed in at parse.mjs:208). It is written only when it
  differs from Node's golden, which stays. The test compares the port with the `.fixed` file when it exists. Each
  replacement must occur exactly once in upstream, or the run fails.

Tokens, applied by the runner and by the C# side to its own output:

- `<DIR>`, `<REPO>`: the seed's directory and the evidence repository, raw, JSON-escaped and with forward slashes.
- `<TMP>`: each `archify-explainer-*` directory under the temp directory (the native child's checker `file`, the
  checker's `nativeDiagrams[].receipt.file`); the rest of the path stays. Any other temp path left in an output fails
  the run.
- A prepared bundle's (`kind: "archify-explainer-prepared"`) `digest` becomes `<digest>`. The C# side asserts that its
  own digest is `sha256(Js.Stringify(bundle minus digest))`. A page's own digest is path-free and compared as is.
- In every `diagrams` map, each child's `html` becomes `"sha256:<hex>"` (its `bytes` stays). Each `htmlGzip`, as a key
  or as `"htmlGzip":"<base64>"` inside a string or a page, becomes `gz-sha256:<hex of the inflated bytes>`.
- An `artifact` or `expectedArtifact` object (`sha256`, `bytes`) becomes `"<artifact>"`.

The capture deletes `tests/goldens/explainer/` before writing it.

## Groups

A group is compared only once `ExplainerParityTests.Groups` lists it; each task adds its own. A row that differs from
Node by a stated deviation is listed in `ExplainerParityTests.KnownDeviations` and pinned by its own fact instead.

- `parse` (Task 2): `parseExplainer(text, {inputPath, output}?)`, the upstream parser tests, the four case drafts, the
  delivery example, every `fail` site of parse.mjs, the V8 `JSON.parse` message kinds, and CRLF and lone-CR drafts.
- `shorthand` (Task 2): `compileExplainerDiagram(type, rows, {id, title, line, quality, locale})` with rows as
  `{cells, line}` (the shape the parser passes; the port takes no text rows), and three `parseExplainer` drafts for the
  lane label of Decision 2 (`workflow-branch-zh`, `-en`, `-default`).
- `inline` (Task 3): `parseInline(text, options?)` and `renderInline(text, context?)` (`parse-*` and `render-*` rows of
  the same input), every problem code, link targets and URLs, typical-value footnotes, citations across three calls.
- `diff` (Task 3): `parseExplainerDiff(text, options?)`, the upstream diff tests (the git test as a literal), every
  `explainer/diff-*` code, quoted file names, CRLF and lone-CR rows, and the secret scanner.
- `writing` (Task 3): `lintExplainerWriting(entries, options?)`, each rule in zh and en, the structural rules at their
  bounds, URL masking, offset ties, the three modes and the TypeErrors (thrown rows `{name: "TypeError", message}`).
- `validate` (Task 3): `validateExplainer(document, options?)` or the `parsed` shape: each `schema-<keyword>` example,
  reserved and duplicate IDs, references and diagram targets, charts (a front-matter PeleError is a thrown row), native
  specs, and writing modes.
- `prepare` (Task 4): `resolveExplainerSources` (`src-*`: repository code and cited excerpts with `repo: true`, path and
  secret-name guards, the four image formats and their limits, CSV and JSON chart data), `prepareExplainerDiagrams`
  (`dia-*`: `source=` files and inline specs, their guards, change marks, schema, render and checker failures, both
  quality profiles), `validateExplainerDiagramTarget` (`target-*`, against a hand-written semantic graph),
  `prepareExplainer` (`prep-*`, whole drafts and targets absent from the SVG) and `readPreparedBundle` (`read-*`, every
  `prepared-mismatch` reason and `dependency-changed`). The native child's checker `file` is a `<TMP>` path.
- `layout` (Task 5): `layoutExplainerPanels(document, diagrams?)`, the upstream layout tests, explicit spans, a row that
  shrinks to minimums, a spacer row, weights (code points, identity keys skipped), diagrams wider than 1000 (viewBox or
  canvas width), snippets at 48, 49, 72 and 73 characters, and comparisons with 2 and 3 data columns.
- `pages` (Task 5): `renderExplainer`, either a `draft` (an en, a zh and a no-locale page, the four styles, every block
  type, `code` with `repo: true`, guided steps, `changes`, `nodeSources`, a wide architecture, `delivery.explainer.json`)
  or `args: [document, options]` (the upstream renderer tests' documents with hand-written resources, the cross-volume and
  portable-path rows, and each thrown error). A page without a locale has a `.fixed.html` golden (Decision 3).
- `cases` (Task 5): the four v3.2.0 showcase drafts through upstream's `deliver` (see `deliver` above).
