# Known deviations from upstream

Back to [`README.md`](../README.md).

## Core library, renderers and repository evidence

- The explainer type of upstream 3.2.0 (one-page answers with build-time Pele charts) is not ported until PORT-009. The charts came first, in PORT-008 (see [Pele charts](#pele-charts-port-008)). `upstream/` leaves out `schemas/explainer.schema.json`, `examples/delivery.explainer.json` and `references/explainer-authoring.md`, and `explainer` is an unknown diagram type. `skill/archify/SKILL.md` keeps its 3.1.0 wording except the `--out-dir` sentences; PORT-009 re-derives it against 3.2.0.
- `Check` throws `NotSupportedException` when `RequireProvenance` is set: provenance is checked by `archify check --require-provenance`.
- `Validate` returns the render diagnostics followed by the checker's. On a check failure upstream `validate` returns only the checker's.
- Schema errors come in a different order when integer-like keys follow named keys in the authored JSON. The set of errors is the same.
- Brand diagnostics come in a different order when a brand is pinned as `{url, sha256}`. Upstream appends capture failures after the synchronous problems. The set is the same.
- A URL with whitespace in its userinfo part is treated as no URL. WHATWG percent-encodes the whitespace instead.
- `DiagramLoader.Parse` stops at nesting depth 64. `JSON.parse` has no limit.
- A raw lone surrogate inside a C# input string becomes U+FFFD. The escaped form `\udXXX` survives parsing, but rendering it fails with `internal/unclassified`.
- *Word breaks:* `Js.WordBreaks` matches V8's `Intl.Segmenter` word granularity (UAX #29) except in three places. (a) Han, Hiragana, Katakana, Thai, Lao, Khmer and Myanmar runs split per grapheme, where ICU uses a dictionary. This shows in sequence notes and in wrapped dataflow labels of the automatic showcase layout; `first-draft.mixed-port-recovery` differs from upstream in exactly one such label. (b) The grapheme-cluster handling differs in both directions. V8 splits inside clusters the port keeps whole: a Prepend (e.g. U+0600) before a space, and a jamo next to a Hangul syllable. The port splits across clusters V8 joins: a ZWJ before a pictograph after a letter (UAX #29 WB3c; `"a‍😀"` is one word in V8, two in the port) and a Format/Prepend-led cluster after a letter (`"a؀b"` is one word in V8, `["a","؀b"]` in the port). (c) The port assumes a non-POSIX host locale; under a POSIX/`LANG=C` default V8 splits `api.example.com` at the dots. Every other text path (tree, class, timeline wrapping; zh-CN locale) is byte-exact.
- *Time zones before the OS zone table's history:* conversions use the operating system's zone data, not Node's bundled tzdata. (a) Before a zone's first tzdata transition (local mean time, roughly pre-1900) the port shows the base offset — a `Asia/Shanghai` event at `0001-01-01T00:00:00Z` is labelled `UTC+08:06` by upstream and `UTC+08:00` by the port. (b) Before the OS table's first adjustment rule the rule is extrapolated backwards — `Pacific/Chatham`/`Australia/Lord_Howe` in 1970 read +13:45/+11:00 instead of +12:45/+10:00. UTC and fixed offsets are exact for every year; named zones are exact from the table's history onward (2026 transitions pinned by fixture). Past 9999-12-31 the zone's last rule continues, daylight saving included, as in V8 (year-10000 rows pinned by fixture).
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
- *Shared with upstream, not a deviation:* a workflow node with an extreme `width` or `height` (around `1e26` and up) makes
  the compile take minutes, and from about `1e30` it never returns. Upstream behaves the same way. The trigger is two automatic
  edges between the same pair of nodes. The cause is the port-candidate probe loop (`automaticPortCandidates`): its 12px
  steps vanish in floating-point rounding at that magnitude. There is no timeout. Card PERF-003 holds the options.

## CLI (phase 5)

Commands and configuration:

- The CLI reads no `ARCHIFY_*` environment variable (upstream honours `ARCHIFY_QUALITY_PROFILE`, `ARCHIFY_REPO_ROOT`,
  `ARCHIFY_CHROME` and others). Use `--quality` and `--repo-root`.
- `compare`, `brands`, `migrate` and `preview` are not ported. They answer `Unknown command` with exit 2.
- Help is upstream's usage text minus those four commands, plus an `archify install-browser` line (a command upstream does not have).
- The explainer type is withheld until PORT-009: no explainer usage lines (`archify patch`, `explainer` in Types, the
  `--writing` block), no `patch` command, and no `--writing` acceptance. `--writing` is parsed as upstream parses it, but a valid
  value answers `cli/unsupported-option` with any diagram type (`explainer` itself answers `cli/unknown-diagram-type`), and the unknown-type message lists the ten diagram types only.
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

The four upstream examples listed in [`docs/status.md`](status.md#phase-5--cli-receipts-sidecars-lock-and-journal-playwright-browser-and-visual-gates) fail `finalize` in both implementations; this is card UPSTREAM-001
(CARD-2181), not a port defect.

## Pele charts (PORT-008)

The six chart types archify 3.2.0 draws with its vendored Pele 1.3.0 (pie, xychart, quadrantChart, sankey, radar, venn)
are ported in `src/Archify.Net.Core/Pele`. Each entry below says what differs, why, and which test pins it. Test names
are in `tests/Archify.Net.Core.Tests/ChartParityTests.cs` unless another file is named. Row names are `group/name`
seeds from `tools/chart-seeds/`. A deviation row is left out of `Matches_node` and pinned by its own fact.

### Platform and engine limits

- *pow depends on the OS:* V8 13.6 sends `**` and `Math.pow` to the C library's `pow`. Windows Node (UCRT) and Linux
  Node (glibc) disagree, for example on `10 ** 210`. The port matches Windows Node 24.12.0, which mints the goldens:
  `10 ** p` for integer p in [-330, 310] comes from a table captured there, and other bases use .NET's `Math.Pow`.
  Every power Pele reaches matches. Random bases with random exponents can differ in the last digit; Pele never computes them.
  Pinned by `JsMathTests.Matches_V8_bit_for_bit("pow")`; the `pow-sample` rows of the full run are informational.
- *JSON depth 256 in data files:* a chart data file nested deeper than 256 levels reports `The data file is not valid
  JSON.`. V8 parses any depth; the port's recursive parse would overflow the stack (about 1 KB per level). Pinned by
  `data/json-deep-unread-column` (300 levels) and `Deep_data_json_is_the_stated_deviation`.
- *Directive nesting 256:* a `%%{init}%%` directive whose JSON nests deeper than 256 levels is ignored as invalid JSON,
  with the same cap and reason. Pinned by `PeleCoreTests.Deep_directive_is_the_stated_deviation` (300 levels).
- *The `merge` nesting cap:* because of that cap, the config `merge` never recurses deeper than 256 levels. V8 merged
  20,000 levels. Same pin.
- *Lone surrogates:* a raw lone surrogate in directive text, inside a C# string, becomes U+FFFD (the text goes through
  UTF-8). A UTF-8 file cannot hold one, so only a string input (the library or MCP) reaches it. Not seeded. An escaped
  `\ud800` in a data file is kept, as in V8, and that row is compared.
- *No icon resolver:* icon names are stripped from labels, as upstream does when it has no resolver. The output does
  not differ.
- *Not ported:* Pele's canvas measurer (with `fontFamily` and `RE_FAMILIES`), `turnToFit`/`tighten`, zoom and mount.
  archify never reaches them.

### Fixed upstream defects

Each fix keeps the Node golden. A `-reference` row is the same chart written so that upstream already gives the
corrected result, and the fact asserts the port equals it.

- *treeView redirect (UPSTREAM-003, [archify#1070](https://github.com/tt-a1i/archify/issues/1070)):* `charts.mjs` keys
  its native-type table by `treeview`, but Pele detects `treeView`, so upstream never redirects a `treeView-beta` fence
  to the native tree shorthand. The port keys it `treeView`. Pinned by `wrapper/native-treeview` and
  `TreeView_redirect_is_the_stated_deviation`.
- *xychart clamped y range ([pele#56](https://github.com/obsidianmd/pele/issues/56)):* upstream checks an explicit y
  range for `lo !== hi` before it clamps both ends to ±1e300. A range such as `1e301 --> 1e302` then collapses, and the
  chart draws 100 ticks at `NaN`. The port compares the clamped ends and falls back to the data, as `5 --> 5` does.
  Pinned by `xychart/pow-clamped-equal`, `-negative` and `Clamped_equal_y_range_is_the_stated_deviation`.
- *quadrantChart phantom box ([pele#57](https://github.com/obsidianmd/pele/issues/57)):* an empty quadrant or point
  label reserves a zero-size box, and point labels are pushed away from empty space. The port reserves a box only for a
  label with text; a radius-0 dot keeps its box, because the circle is drawn. Pinned by
  `quadrantChart/phantom-empty-quadrant-label`, `phantom-empty-point-label`, `stress-points` and
  `Empty_label_reserves_no_space_is_the_stated_deviation`.
- *quadrantChart Q1-Q3 ([mermaid#8525](https://github.com/mermaid-js/mermaid/issues/8525),
  [#8526](https://github.com/mermaid-js/mermaid/issues/8526),
  [#8527](https://github.com/mermaid-js/mermaid/issues/8527)):* Mermaid grammar slips that Pele copies.
  - Q1: the comment rule `[^}]%%` eats the character before `%%`, so `radius: 5%%x` loses the 5. The port lets only
    whitespace go with the comment (`\s%%`); `}%%` stays an error.
  - Q2: the coordinate rule `0(.\d+)?` has an unescaped dot, so `0x1` reads 1 and `0,5` drops the point silently. The
    port escapes the dot, and such input is a lexical error, as `[2, 0.5]` is.
  - Q3: keywords fire anywhere and without a word boundary, so `quadrant-1 Move to quadrant-2` fails. The port reads a
    keyword only as a whole word that leads a statement, and a statement in point form (`: [x, y]`) is a point.
  - Pinned by `QuadrantQuirkRows` in `Grammar_slips_are_the_stated_deviations`, and by the fuzz list.
- *xychart X1-X4 ([mermaid#8528](https://github.com/mermaid-js/mermaid/issues/8528),
  [#8529](https://github.com/mermaid-js/mermaid/issues/8529),
  [#8530](https://github.com/mermaid-js/mermaid/issues/8530),
  [#8531](https://github.com/mermaid-js/mermaid/issues/8531)), plus Q1 as above:*
  - X1: `HORIZONTAL` in any case but lower draws vertical. The port compares case-insensitively.
  - X2: `vertical`/`horizontal` are keywords anywhere, so `title verticality` fails. The port takes the orientation
    only as a whole word right after the chart keyword.
  - X3: a quoting slip breaks the markdown-string rules, so `` "`md`" `` draws its backticks. The port reads markdown
    strings as the quadrant grammar does. A malformed markdown string is a lexical error (Martin's decision).
  - X4: unquoted text drops the whitespace between its tokens, so `title Sales Report` draws `SalesReport`. The port
    keeps the whitespace as written; a gap holding a line break becomes one space (Martin's decision).
  - Pinned by `XyChartQuirkRows` in `Grammar_slips_are_the_stated_deviations`, and by the fuzz list.
- *sankey S1, blanks at a row's edge ([mermaid#8535](https://github.com/mermaid-js/mermaid/issues/8535),
  [pele#58](https://github.com/obsidianmd/pele/issues/58)):* Mermaid's per-row trim has no `m` flag, so it does nothing,
  and a tab indent, a trailing blank after the header or a blank row of spaces is a parse error. The port trims blanks
  at each row's edge outside quotes. A blank run that is a whole field still reads as in Node. Pinned by the S1 rows of
  `SankeyQuirkRows` (with `render-rejects-5`) in `Grammar_slips_are_the_stated_deviations`.
- *sankey S2, undecoded labels ([pele#59](https://github.com/obsidianmd/pele/issues/59)):* a node label writes its
  name, prefix and suffix with entities undecoded, while the band's `<title>` decodes them. The port decodes the label
  and measures the decoded text. Pinned by the S2 rows of `SankeyQuirkRows`.
- *sankey S3, non-ASCII names:* Pele 1.3.0 accepts only ASCII names (RFC 4180 TEXTDATA), so `Café` is a parse error.
  Mermaid has the same defect, reported many times: [#6822](https://github.com/mermaid-js/mermaid/issues/6822),
  [#7996](https://github.com/mermaid-js/mermaid/issues/7996), [#6155](https://github.com/mermaid-js/mermaid/issues/6155),
  [#7528](https://github.com/mermaid-js/mermaid/issues/7528). Pele 1.9.0 fixed it
  ([pele#8](https://github.com/obsidianmd/pele/issues/8), [pele#9](https://github.com/obsidianmd/pele/pull/9)), and the
  port uses the 1.9.0 alphabet and its invalid-XML name check. Pinned by `SankeyUnicodeRows` and
  `Sankey_unicode_names_and_unreadable_values_are_the_stated_deviations`.
- *sankey S4, strict numbers ([mermaid#8536](https://github.com/mermaid-js/mermaid/issues/8536),
  [pele#60](https://github.com/obsidianmd/pele/issues/60)):* upstream reads a value with `parseFloat`, so `12abc` is
  12 and an unreadable value draws as a 0-flow link. The port accepts only a whole decimal number; anything else is the
  error `Sankey diagrams need a number for each link's value, and "<v>" on line <n> is not one.`. `1e999` stays
  Infinity. Pinned by `SankeyValueRows` and the same fact.
- *Error-line remap ([mermaid#8537](https://github.com/mermaid-js/mermaid/issues/8537),
  [pele#61](https://github.com/obsidianmd/pele/issues/61), with the end-of-input comment there;
  [archify#1074](https://github.com/tt-a1i/archify/issues/1074) for `pieLegend`):* upstream counts an error's line in
  the text the diagram parsed, after front matter, comment lines, directives and leading blank lines are removed (and
  sankey's blank lines). archify's `pieLegend` also drops line breaks. At the end of the input Node reports the line after
  the last, which comes from the newline Pele appends. The port reports the line as written in the fence, for every
  type, and an error at the end of the input on the last content line. Pinned by `LineRemapRows`, the `lines` group,
  `Error_lines_count_the_fence_as_written`, `Every_error_line_holds_the_error` and the `L1` fuzz cases.
- *radar RA1 ([mermaid#8539](https://github.com/mermaid-js/mermaid/issues/8539),
  [pele#62](https://github.com/obsidianmd/pele/issues/62)):* a name that starts with `circle`, `polygon`, `true`,
  `false` or `title` (`circles`, `trueNorth`) is a parse error, because those terminals do not give way to a longer
  name as every keyword does. The port lets them give way, in name positions only; keyword glue such as
  `graticule polygonaxis` reads as in Node. Pinned by `RadarBoundaryRows` and `Radar_name_boundaries_are_the_stated_deviation`.
- *radar RA2 ([mermaid#8540](https://github.com/mermaid-js/mermaid/issues/8540),
  [pele#63](https://github.com/obsidianmd/pele/issues/63)):* the `Missing entry for axis` message writes the label with
  Mermaid's internal entity placeholders (`ﬂ°°35¶ßx`). The port names the axis as the chart draws it. Pinned by
  `RadarQuirkRows`.
- *venn Q1 ([mermaid#8541](https://github.com/mermaid-js/mermaid/issues/8541),
  [pele#64](https://github.com/obsidianmd/pele/issues/64)):* the same comment rule as quadrantChart Q1. The port lets
  only whitespace go with the comment. A one-character token right before `%%`, which upstream silently eats, is now
  read normally. So `set A%% note` keeps the set A, and `venn-beta-%% x`, which Node draws, is an error. Pinned by the
  probe grid (`Venn_q1_grid_matches_the_patched_upstream`) and `VennQuirkRows`.
- *venn VN1 ([mermaid#8542](https://github.com/mermaid-js/mermaid/issues/8542),
  [pele#65](https://github.com/obsidianmd/pele/issues/65)):* a keyword followed by `-` and more of a name
  (`set-theory`, `text-books`) fails. The port reads it as a name, unless the `-` starts a number. Pinned by
  `VennSwapRows` (`Venn_stand_in_rows_are_the_stated_deviation`) and `VennQuirkRows`.
- *venn VN2 ([mermaid#8543](https://github.com/mermaid-js/mermaid/issues/8543),
  [pele#66](https://github.com/obsidianmd/pele/issues/66)):* a bare `title` takes the next line as its text, and that
  statement is lost. In the port a bare `title` is a parse error, as `title` at the end of the text already is upstream.
  Pinned by `VennQuirkRows`.
- *venn VN3 ([mermaid#8544](https://github.com/mermaid-js/mermaid/issues/8544),
  [pele#67](https://github.com/obsidianmd/pele/issues/67)):* `unknown set identifier` names ids with internal
  placeholders and is cut at a line break. The port names each id as the chart draws it and writes a line break as
  `#10;`/`#13;`. Pinned by `VennMessageRows` and `Venn_line_break_in_an_unknown_id_is_written_as_its_code`.
- *XML-invalid characters (UPSTREAM-008, (upstream issue to follow)):* U+FFFE, U+FFFF and lone surrogate halves in any
  label, title or id reach the SVG, which is then not well-formed XML. Pele 1.9.0 still writes them. The port's escaper
  drops them, in every type, as it already drops C0 controls; the layout does not move. Pinned by `fixes/xml-*`,
  `radar/labels-xml-invalid-fffe`, `venn/id-xml-invalid`, `venn/label-xml-invalid-fffe` and
  `Xml_invalid_characters_are_dropped`.
- *pie invalid-value message (PORT-011, (upstream issue to follow)):* the `"<label>" has invalid value` message writes
  the label with internal placeholders and is cut at a line break. The port names the label as drawn, a line break as
  `#10;`/`#13;`. Pinned by `fixes/message-pie-*`, `Pie_invalid_value_names_the_label_as_drawn` and pie fuzz case 702.
- *radar overflow near 1e308 (PORT-011, (upstream issue to follow)):* a value whose `radius * (value - min)` overflows
  is drawn at the centre. The port divides first when the product overflows, so it draws the value at its true radius;
  every other value is computed as upstream. Pinned by `radar/values-overflow-radius`, `fixes/overflow-radar-half` and
  `Radar_overflow_is_the_stated_deviation`.

### Mirrored on purpose (grammar design, not defects)

- radar: a name that starts with a digit (`3d`) fails; an axis named exactly `circle`, `polygon`, `true`, `false` or
  `title` fails, as a reserved word; `title`, `accTitle:` and `accDescr:` fire mid-line, as in Mermaid's Langium grammar.
- Every type: `&#9829;` in a label is drawn `&♥`, as upstream draws it (core's Mermaid entity encoding).
- venn: a title cannot hold `#` or `;` (`title C# basics` is a parse error), and a numeric text id takes no label
  (`text A 5["l"]` is an error). Both come from `venn.jison`.

### Pele tests the port fails by design

Pele's own test "rejects what Mermaid rejects" fails in the port for two of its inputs: `sankey/render-rejects-5`, a
blank row of spaces (S1), and `sankey/render-rejects-6`, "rejects Café" (S3). The port draws both.

### Test method notes

- *Fuzz:* each type's fuzz corpus is Pele's own `random` and `mutator` over the type's seeds. Pele's oracle skips a
  mutated input over 2,000 characters before it compares. The capture applies that bound to the seeds: a seed over
  2,000 characters stays out of the corpus and is compared by its own row. Fuzz cases that differ by a stated
  deviation are listed by index in `FuzzDeviations` and checked by `Fuzz_deviations_are_the_stated_quirks`; the port's
  output for them is pinned in `tests/fixtures/charts/<type>.fuzz-deviations.json`. Re-mint both when a type's seeds
  change.
- *venn Q1 grid:* `tests/fixtures/charts/venn-q1-grid.json` holds 1,386 probes (every printable ASCII character and a
  few others before `%%`, in each lexer position). `node tools/chart-seeds/venn-q1-grid.mjs` regenerates it; run it
  after `tools/chart-seeds/venn-oracle.mjs` changes.

### PORT-009 note

`ExplainerCharts` exists, but no CLI path reaches it until PORT-009 brings the explainer type. Upstream's
`compileExplainerChart` throws on a front-matter YAML error (`charts.mjs` runs `detectType` outside its `try`), and
`ExplainerCharts.Compile` lets that `PeleException` propagate. So the CLI must handle it as upstream's top-level
handler does.

### Known leftovers (PORT-012)

Card PORT-012 collects the test and tooling minors of PORT-008 and these parked contrived-input cases:

- radar RA2: a line break in an axis label is written raw in the `Missing entry for axis` message, so the message is
  cut there;
- radar overflow when `value - min` itself overflows (`min` near -1e308, a value near 1e308): the point is still drawn
  at the centre;
- error messages (JSON strings, not SVG) can still carry U+FFFE or lone surrogates from a label;
- the venn test oracle's allowance for a styled line could accept a token that spans the dropped `;` on the right line.
