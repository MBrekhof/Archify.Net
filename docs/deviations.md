# Known deviations from upstream

Back to [`README.md`](../README.md).

## Core library, renderers and repository evidence

- The explainer type of upstream 3.2.0 (one-page answers with build-time Pele charts) is not ported until PORT-009 (the charts come first, in PORT-008). `upstream/` leaves out `schemas/explainer.schema.json`, `examples/delivery.explainer.json` and `references/explainer-authoring.md`, and `explainer` is an unknown diagram type.
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
