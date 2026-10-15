# Cleanup batch 1: GLOB-001, CLEAN-001, PERF-001 (threads), TEST-001

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** close four board cards, each without changing any rendered byte:
- GLOB-001: Archify.Net.Core no longer dies in a host with invariant globalization.
- CLEAN-001: the duplicated JavaScript-semantics helpers live once, in `Js`.
- PERF-001: GitRunner holds no thread-pool threads for blocking pipe I/O.
- TEST-001: the remaining phase-2 probe-only cases get Node-minted goldens.

**Architecture:**
- Three of the tasks are behaviour-neutral refactors: Tasks 1-3 change no output, and the 3727-test suite plus every golden
  must stay byte-identical.
- Task 4 adds fixtures only.

**Tech stack:** .NET 10, xUnit, Node 24 for fixture capture (upstream `C:\projects\archify` at c21c31ff).

**Spec:** `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md`; the parity bar is in its "## Testing" section.
The cards themselves are the requirements; their bodies are quoted in each task.

## Global constraints

- Phase-2 plan lines 15-29 still apply:
  - no NuGet dependency in Core;
  - `Js.*` for JS semantics;
  - UTF-8 without BOM, LF line endings;
  - diagnostics are returned, not thrown;
  - commits use `git commit -F` with an exact `git add` list.
- Every golden stays byte-identical, and the full `dotnet test` (Browser included) stays green with zero warnings.
- No environment variables as configuration.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (or the implementing model's trailer).

## Review focus

1. A helper merged in Task 2 must not change NaN, -0 or null handling at any call site. The variants differ in signature
   (`double?` vs `double`), and the merged version must agree with every one of them.
2. Under ICU, `CompareInfo.Invariant` must sort every string the renderers compare the same way as `en`. Prove it with a
   test over the ids that actually reach `LocaleCompare`, not just a claim.
3. GitRunner's stdin/stdout/stderr ordering, the maxBuffer kill and the EPIPE handling must not change. Only the threads
   carrying them change.

---

### Task 1: GLOB-001, `Js.LocaleCompare` without a culture lookup

**Card:** `Js.cs:191` calls `CultureInfo.GetCultureInfo("en")` in the static constructor. In a host with
`InvariantGlobalization=true` this throws `CultureNotFoundException`, the type initializer fails, and every `Js` member
fails with it.

**Ruling:**
- ICU's `en` collation is CLDR root with no tailoring, so under ICU `CultureInfo.InvariantCulture.CompareInfo` orders
  strings exactly as `en` does.
- Replace the static field with `CompareInfo.Invariant` (`CultureInfo.InvariantCulture.CompareInfo`), so nothing throws in
  any host.
- In an invariant-globalization host, the invariant CompareInfo compares ordinally. That host-only difference becomes a
  stated deviation:
  - one line in the README "Known deviations";
  - one line in the `LocaleCompare` doc comment.
- This beats card options (a)+(c): no member fails at all, and under ICU behaviour is identical.

**Amendment from the agy plan review (blocker):** `Js.cs:191` is not the only culture lookup.
- Three more static fields throw in an invariant host, and agy confirmed it by running Step 2's command:
  - `BrandMarks.cs:19` `EnUs = CultureInfo.GetCultureInfo("en-US")`;
  - `PortablePath.cs:39`, the same field;
  - `src/Archify.Net.Cli/Paths/PathSemantics.cs:34`, the same field.
- All three only call `.ToLower(EnUs)` / `.ToUpper(EnUs)` to mimic V8's locale-independent `toLowerCase()` /
  `toUpperCase()`. `Js.ToLowerCase` / `Js.ToUpperCase` (`JsText.cs`) already implement that without `CultureInfo`.

**Ruling:**
- Replace each `.ToLower(EnUs)` / `.ToUpper(EnUs)` with `Js.ToLowerCase` / `Js.ToUpperCase`, and delete the three fields.
- First read each site and confirm it models a JS `toLowerCase`/`toUpperCase` call; cite the upstream line in the report.
- If a site turns out to model something else, stop and report it; do not swap it.
- Full case mapping (for example `İ` → `i̇`, two code units) can differ from .NET's simple mapping. Where it does, the swap is a
  parity fix toward V8: add one test per site with such a character, and take the expected value from `node -e`.
- After the change, `git grep -n "GetCultureInfo\|new CultureInfo(" -- src/` must return nothing but `InvariantCulture`
  uses.

**Files:**
- `src/Archify.Net.Core/Js/Js.cs`
- `src/Archify.Net.Core/Shared/BrandMarks.cs`
- `src/Archify.Net.Core/Shared/PortablePath.cs`
- `src/Archify.Net.Cli/Paths/PathSemantics.cs`
- `tests/Archify.Net.Core.Tests/JsTests.cs` (or the existing Js test file)
- `tests/Archify.Net.Cli.Tests/InvariantGlobalizationTests.cs`
- `README.md`

- [ ] **Step 1, the equality test.** A Core test asserts that for every pair from a fixed list, `en` and invariant agree in
  sign. The list covers:
  - ASCII upper and lower case;
  - digits;
  - `-`, `_`, `.`;
  - accented Latin (`é`, `É`, `ø`);
  - CJK;
  - the real ids `LocaleCompare` sees: architecture node ids from `upstream/examples/*.architecture.json`, workflow ids, and
    brand ids from `upstream/brand-marks`.

  This test uses `CultureInfo.GetCultureInfo("en")` inside the test only.
- [ ] **Step 2, the invariant-host repro test (Cli tests).**
  - Copy `Archify.Net.Cli.runtimeconfig.json` from the test's output directory to a temp file, and add
    `"configProperties": { "System.Globalization.Invariant": true }` under `runtimeOptions`.
  - Run `dotnet exec --runtimeconfig <temp> <Archify.Net.Cli.dll> render architecture <copy of web-app.architecture.json> <out.html>`.
  - Before the fix it must fail: TypeInitializationException in stderr, exit code not 0. Confirm that and record it.
  - After the fix: exit 0, and the HTML exists.
  - Byte equality with the golden is NOT required, because ordinal ordering may differ. Assert only success, and note
    why.
- [ ] **Step 3:** make the four changes and the doc comment, and add the README deviation line.
  - `CompareInfo` has no static `Invariant` property. Use `CultureInfo.InvariantCulture.CompareInfo`.
  - The brand ids for the Step 1 list come from `BrandMarks.ListBrandMarks()` / `upstream/brand-marks/catalog.json`.
- [ ] **Step 4:** run the full `dotnet test`. Every golden must stay identical.
- [ ] **Commit:** `fix(core): Js.LocaleCompare uses the invariant CompareInfo — no throw in invariant-globalization hosts (GLOB-001)`.

### Task 2: CLEAN-001, one copy of each JS helper, in `Js`

**Card:** `Or` (JS `||` on numbers), `MaxOf`/`MinOf`, `Coord`/`PtOf` (JSON point) and `BrandMarks.JsString` (JS
`String(x)`) are copied across renderers.

**Survey (at 9718931):**
- **`Or`** has six copies.
  - These take `double?`: `WorkflowModel.cs:50` (internal), `DataflowCompiler.cs:99`, `Geometry.cs:903`.
  - These take `double`: `JointLabelRouting.cs:44`, `Erd/Grid.cs:184`, `LifecycleRenderer.cs:206`.
  - All six have the same semantics: return the fallback when the first value is null, 0 or NaN. Note that `-0 == 0` is
    true in C#, matching JS's falsy -0.
  - `ArtifactChecker.cs:1705` `Or(string?, string?)` is string `||`. It is **out of scope**: leave it.
- **`MaxOf`** has seven copies, and **`MinOf`** two, in:
  - `WorkflowModel.cs:85`;
  - `DataflowCompiler.cs:100` and `:106`;
  - `LifecycleRenderer.cs:207` and `:213`;
  - `SequenceRenderer.cs:137`;
  - `TimelineRenderer.cs:307`, `TreeRenderer.cs:218` and `WaterfallRenderer.cs:217`.

  The three last copies short-circuit on NaN. That is equivalent, because .NET's `Math.Max`/`Math.Min` return NaN when
  either argument is NaN, as JS `Math.max/min` does. The merged version keeps the simple loop.
- **`PtOf`/`Coord(JsonNode?)`** are identical in `WorkflowCompilation.cs:88-89` and `DataflowCompiler.cs:119-120`.
  - `Coord(Pt, int axis)` (Routing, ErdRenderer, AutomaticLabels, Geometry) is **out of scope**: it is not JS semantics.
- **`BrandMarks.JsString`** is the general JS `String(x)` helper, with 53 call sites.

**Amendments from the agy plan review:**
- **Missed numeric copies.** Merge these too:
  - `WorkflowModel.cs:52` `Lor(double, double)`, the same `||` semantics;
  - `ArtifactChecker.cs:1707` `Or0(double?)`, which becomes `Js.Or(v, 0)`;
  - `ArtifactChecker.cs:1726-1738` `JsMin`/`JsMax`, which become `Js.MinOf`/`Js.MaxOf` with the default seed.
- **The three "String(x)" helpers differ, so do not merge them blindly:**
  - `BrandMarks.JsString` JSON-serialises arrays and objects;
  - `SchemaValidator.cs:139-146` `JsString` is true JS `String()`: `"[object Object]"`, and arrays comma-joined with
    null → `""`;
  - `Geometry.cs:119-127` `JsStr` gives `""` for arrays and `"[object Object]"` for objects;
  - and `WorkflowModel.cs:55-64` warns that `JsString` must not be used where JS calls `String()` on a non-primitive.

  **Ruling:** behaviour-neutral, so every call site keeps exactly its current semantics. Move them into `Js` as named helpers
  whose doc comments state the difference:
  - `Js.String(JsonNode?)` = true JS `String()` (the SchemaValidator body);
  - `Js.PrimitiveString(JsonNode?)` = the BrandMarks body (exact for primitives, JSON for arrays and objects, used only where
    the value is known primitive or where the current code relies on that);
  - Geometry's `JsStr` stays local unless one of the two above reproduces it exactly. Check this and say which in the report.

  Deduplicate only exact semantic copies. Add tests that pin the difference: an array and an object through each helper.

**The new API, in `src/Archify.Net.Core/Js/Js.cs` or the Js partial that holds number helpers:**

```csharp
// JS `x || y` on numbers: x when truthy (not undefined/null, 0, -0 or NaN), else y.
public static double Or(double? x, double y) => x is { } v && v != 0 && !double.IsNaN(v) ? v : y;
// Math.max(seed, ...values) / Math.min: NaN anywhere gives NaN (Math.Max/Min propagate it).
public static double MaxOf(IEnumerable<double> values, double seed = double.NegativeInfinity) { var m = seed; foreach (var v in values) m = Math.Max(m, v); return m; }
public static double MinOf(IEnumerable<double> values, double seed = double.PositiveInfinity) { var m = seed; foreach (var v in values) m = Math.Min(m, v); return m; }
// JS String(x) for a JSON value.
public static string String(JsonNode? value) => /* moved body of BrandMarks.JsString */;
```

`PtOf`/`Coord(JsonNode?)` move to `public static class J` in `src/Archify.Net.Core/Js/JsonNodeExt.cs`, which already
holds `Num`/`Str`, as `J.PtOf(JsonNode?)` and `J.Coord(JsonNode?)`.

- [ ] **Step 1:** add the helpers, with a test per helper in the Js test file:
  - `Or`: null, 0, -0, NaN, a negative number, a positive number;
  - `MaxOf`/`MinOf`: empty with a seed, NaN inside, `-0` vs `0`;
  - `String`: null, true, numbers via `Js.Num`, strings, arrays/objects as upstream's `String()`. Take the expected values
    from the existing `BrandMarks.JsString` tests or behaviour.
- [ ] **Step 2:** replace every copy and call site, then delete the local copies.
  - Use mcpRoslyn `find_references` on each old symbol to make sure none is left.
  - `BrandMarks.JsString` is removed. Its callers use `Js.String`.
- [ ] **Step 3:** run the full `dotnet test`. Every golden must stay identical, with zero warnings.
- [ ] **Commit:** `refactor(core): one Js copy of Or, MaxOf/MinOf, String and the JSON point reader (CLEAN-001)`.

### Task 3: PERF-001, GitRunner off the thread pool (no timeout)

**Card:** each `GitRunner.Run` holds two pool threads blocking on pipes (`Task.Run` for stdin and for stderr), plus the
caller, so concurrent evidence renders in a server starve the pool. The card's optional timeout would be a new deviation
from upstream (spawnSync has none), and the card says to discuss its diagnostic code first.

**Ruling:**
- Fix the thread use only. Start the stdin writer and the stderr drain with
  `Task.Factory.StartNew(..., CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)`, which
  gives each a dedicated thread, never a pool thread.
- The caller's thread keeps draining stdout, exactly as now.
- **Out of scope:** the timeout. A new card, PERF-002 "GitRunner timeout knob: needs Martin's call on the diagnostic code",
  carries it to Martin.

**Files:** `src/Archify.Net.Core/Evidence/GitRunner.cs`, the existing evidence tests, and `README.md`. README lines 189-191
say "each git call holds thread-pool threads while it runs". Change that to dedicated threads; the no-timeout sentence
stays.

- [ ] **Step 1:** the change, with a `// ponytail:` note that names the remaining ceiling: the caller still blocks; an
  async API is the upgrade if a server needs one.
- [ ] **Step 2, a check that the change holds.** Add an internal seam `internal static Func<Action, Task> StartBlocking`,
  defaulting to the LongRunning factory, so a test can assert that both helper tasks went through it.
  - Prefer the simpler check if there is one: inside the started action, record `Thread.CurrentThread.IsThreadPoolThread`
    via an internal test hook, and assert `false` for both.
- [ ] **Step 3:** run the full `dotnet test`. The evidence goldens and receipts must stay identical.
- [ ] **Commit:** `perf(core): GitRunner pipe helpers on dedicated threads, not the pool (PERF-001)`.

### Task 4: TEST-001, Node goldens for the remaining phase-2 probe-only cases

**Card:** phase-2 Task 11's report (`.superpowers/sdd/2026-10-14-phase2-renderers/task-11-report.md`, the rows marked
"P only") lists 17 cases verified only by ad-hoc probes. The phase-2 final fix wave pinned 7 of them; this task pins the
remaining 10 or so.

The candidates are `lc-no-cards`, `cl-no-methods-attrs`, `cl-no-relationships`, `cl-dup-type-id`, `tr-single-node`,
`tl-no-lanes`, `tl-single-event`, `wf-unit-s`, `wf-no-service` and `wf-single-span`, plus whichever of `tl-fixed-plus`,
`tl-fixed-minus-half` and `tl-fixed-compact` the fix wave did not pin.

**Amendment from the agy plan review:**
- The probe ids are not in the repo. The fix wave (commit `47d59b0`) named its files `<example>.<mutation>.<type>.json`
  (`capture-fixtures.mjs:319-330`, `ParityTests.cs:38-43`; `sq-dup-participant` is pinned in `sequenceFaults` at
  `capture-fixtures.mjs:575` / `ParityTests.cs:395`). `tl-fixed-minus-half` is already pinned as
  `payment-incident.minus-half.timeline.json`.
- **The exact 12 to add** are 11 render goldens and 1 fault receipt.

| Probe | Base example | Mutation | File |
|---|---|---|---|
| lc-no-cards | `agent-run.lifecycle.json` | delete `cards` | `agent-run.no-cards.lifecycle.json` |
| cl-no-methods-attrs | `payments.class.json` | delete `methods` and `attributes` from every type | `payments.no-members.class.json` |
| cl-no-relationships | `payments.class.json` | delete `relationships` | `payments.no-relationships.class.json` |
| tr-single-node | `payment-platform.tree.json` | keep only the root, with no children | `payment-platform.single-node.tree.json` |
| tl-fixed-plus | `payment-incident.timeline.json` | `meta.timezone: "+08:00"` | `payment-incident.plus.timeline.json` |
| tl-fixed-compact | `payment-incident.timeline.json` | `meta.timezone: "+0800"` | `payment-incident.compact.timeline.json` |
| tl-no-lanes | `payment-incident.timeline.json` | delete `lanes` (and any event `lane` reference, if the schema then requires it; mirror what the probe report says) | `payment-incident.no-lanes.timeline.json` |
| tl-single-event | `payment-incident.timeline.json` | keep the first event only | `payment-incident.single-event.timeline.json` |
| wf-unit-s | `incomplete-trace.waterfall.json` | `unit: "s"` (scale values if the report says so) | `incomplete-trace.unit-s.waterfall.json` |
| wf-no-service | `checkout-request.waterfall.json` | delete every `service` | `checkout-request.no-service.waterfall.json` |
| wf-single-span | `checkout-request.waterfall.json` | keep one span (the root) | `checkout-request.single-span.waterfall.json` |
| cl-dup-type-id (fault) | `payments.class.json` | a second type with the first type's id | `classFaults` row `duplicate-type-id` (`capture-fixtures.mjs:~616`, `ParityTests.cs:~427`) |

- Read each "P only" row of the task-11 report for the exact mutation it probed; the report's wording wins over this table.
- If a mutated input fails upstream's validation, which the report says it did not, record what upstream does and pin that
  instead.

**First:** confirm against `capture-fixtures.mjs` that none of the 12 is already pinned.

**Files:** `tools/capture-fixtures.mjs`, the new files under `tests/fixtures/`, and `tests/Archify.Net.Core.Tests/ParityTests.cs`
(or the failure-receipt test class for the two failure cases).

- [ ] **Step 1:** follow the pattern of the phase-2 final fix wave's commit. Find it with
  `git log --oneline --grep "fix wave" -- tools/capture-fixtures.mjs`. Add each mutated input, derived from a bundled
  example, to the capture script.
  - Render cases produce `*-rendered.html` goldens.
  - The two failure cases (`cl-dup-type-id`; `sq-dup-participant` if it is not already pinned) produce receipts.
- [ ] **Step 2:** run the capture twice and prove both runs are identical. No existing fixture may change.
- [ ] **Step 3:** add the rows to `ParityTests.Targets()` and the failure-receipt tests. Run them: they must pass on the
  first run (the cases were probe-verified), and any failure is a real parity bug to report.
- [ ] **Step 4:** run the full `dotnet test`.
- [ ] **Commit:** `test(core): Node goldens for the phase-2 probe-only cases (TEST-001)`.

## Exit

- The four cards close with commit SHAs.
- PERF-002 is minted for the timeout decision.
- Every golden is unchanged, and the full suite is green with zero warnings.
