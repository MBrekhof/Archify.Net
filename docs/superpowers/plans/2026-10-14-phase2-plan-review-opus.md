# Phase 2 plan review (Opus), 2026-10-14

Plan under review: `docs/superpowers/plans/2026-10-14-phase2-renderers.md` (cited as `plan:<line>`).
Upstream: `C:\projects\archify` @ `c21c31ff` (`UP/` = `archify/`). V8 = Node v24.12.0, ICU 77.1, Unicode 16.0, machine zone Europe/Amsterdam.
.NET probes ran under `dotnet fsi` on this machine (Windows 11, ICU via `icu.dll`).

Every claim below comes from a probe or from file:line. None comes from memory. Probes used:

- `wc -l` on the seven upstream files.
- `node -e` batteries for `Intl.Segmenter`, the `\p{Script=Han}` range enumeration, `Date.parse`, `Intl.DateTimeFormat` id acceptance and `formatToParts` offsets.
- A `dotnet fsi` script for `TimeZoneInfo.TryConvertIanaIdToWindowsId`, `TryFindSystemTimeZoneById` and `GetUtcOffset`.
- Scratch copies of the plan's Task 1 fixtures, and of each pinned diagnostic mutation, rendered through the upstream renderers with `ARCHIFY_DIAGNOSTIC_FORMAT=json`. The scripts are in the session scratchpad and nothing was written to the repo.
- `grep` over the six renderers, `erd/grid.mjs`, `geometry.mjs`, `test/golden.mjs` and our `src/Archify.Net.Core/**`.

Severity follows the phase-1 review:

- **Blocker:** a task cannot finish as written, or it produces wrong bytes and its own test does not catch them.
- **Should-fix:** a wrong or misleading detail that will cost a debugging round, a spec rule the plan drops, or a decision still open.
- **Nice-to-have:** minor.

---

## Blockers

### B1. Task 2: `WordBreaks` as specified fails its own Latin assertion, and it never sees Latin text anyway
- **Plan:** `plan:283` defines `WordBreaks` like this:
  - a run of letters, digits, marks, `'` or `_` is one segment;
  - each other punctuation or symbol rune is its own segment;
  - each Han rune is its own segment.

  `plan:302-310` asserts exact equality with `Intl.Segmenter(word)` on every non-Han row.
- **Evidence (V8):** `node -e` on the fixture's own rows gives:

  | Row | V8 word segments | Plan's rule gives |
  |---|---|---|
  | `URL_NOTE` | `…,"api.example.com","/",…` (WB6/WB7 join letters across `.`) | `"api",".","example",".","com"` |
  | family emoji `👨‍👩‍👧` | `["👨‍👩‍👧"]` (WB3c) | 5 segments (ZWJ is `Cf`, so it becomes its own segment) |
  | flags `🇩🇪🇫🇷` | `["🇩🇪","🇫🇷"]` | 4 segments |
  | `a\u200db` | `["a‍b"]` (WB4) | `["a","\u200d","b"]` |

  So `WordBreaks_match_Intl_Segmenter_outside_Han_and_concatenate_everywhere` fails on four rows.
- **Why the assertion is pointless anyway:** `render-sequence.mjs:203-205` calls the word segmenter only on tokens matched by `\p{Script=Han}[\p{Script=Han}\p{Mark}]*`. Its input is always a run of Han ideographs plus marks. ICU word-break fidelity on Latin is never exercised.
- **Fix:**
  1. Specify `WordBreaks(s)` as "input is a Han+Mark run; one segment per extended grapheme cluster", which is `Js.Graphemes(s)`. That keeps a mark on its ideograph, and V8 gives `["漢︎","字"]` for `漢\ufe0e字`, so the fallback even matches ICU there.
  2. Change the test to assert concatenation only. Optionally also assert `WordBreaks(t) == Graphemes(t)` for every `hanTokens` entry that `HasHan`.
  3. Drop the "words exact for Latin rows" clause from Exit (`plan:775`).

### B2. Task 3: the `ZoneWall` fixture test cannot pass on Windows (22 of 119 rows), and Step 4's explanation is wrong
- **Plan:**
  - `plan:130-139` crosses all 7 `wallZones` with all 17 `instants`, including `0001`, `0099`, `0000`, `1970` and `1969`.
  - `plan:373-381` asserts every row.
  - `plan:389` says "none of the fixture instants are pre-1970 except the two UTC rows and `1969-12-31`". That is false, because `flatMap` gives every zone every instant.
- **Evidence:**

  | Zone | V8 (tzdata via ICU), years 0000/0001/0099 | V8, 1970 | .NET `GetUtcOffset` (Windows table) |
  |---|---|---|---|
  | Asia/Shanghai | +08:05:43 (LMT) | +08:00 | +08:00 |
  | America/New_York | −04:56:02 | −05:00 | −05:00 |
  | Europe/Berlin | +00:53:28 | +01:00 | +01:00 |
  | Asia/Kolkata | +05:53:28 | +05:30 | +05:30 |
  | Pacific/Chatham | +12:13:48 | **+12:45** | **+13:45** for 1970 and year 1 |
  | Australia/Lord_Howe | +10:36:20 | **+10:00** | **+11:00** for 1970 and year 1 |

  - 18 LMT rows (6 zones × 3 ancient instants) fail.
  - 4 rows fail for Chatham and Lord Howe in 1969 and 1970. Windows applies its earliest adjustment rule, with DST in January, to every earlier year.
  - The 2026 rows and the 9999 row agree for all zones.
- **It reaches rendered bytes for valid input.** Upstream renders a Shanghai-zoned event at `0001-01-01T00:00:00Z` with the label `UTC+08:06`; the port will print `UTC+08:00`.
- **Fix:**
  1. Split the battery: assert UTC for every year, and every zone for the 2026 and 9999 instants.
  2. Keep the other rows in the fixture with `"guaranteed": false`. Either assert them as the documented deviation, meaning the C# value equals the OS-table value, or skip them.
  3. Correct `plan:389`.
  4. Fix the deviation text in S1.

### B3. Task 3: the `DateParse` fixture test cannot pass; two rows are outside the schema shape but V8 parses them
- **Plan:** `plan:343` says "anything else → `NaN`". `plan:359-362` asserts every row of `dates.json`. `plan:123` includes `'2026-10-01T10:00:00'` and `'2026-10-01 10:00:00Z'`.
- **Evidence (V8):**
  - `Date.parse('2026-10-01T10:00:00')` is `1790841600000`. V8 reads it as **local** time (Amsterdam here, 08:00Z), so the fixture value changes with the capture machine's zone.
  - `Date.parse('2026-10-01 10:00:00Z')` is `1790848800000`, from V8's legacy fallback parser.
  - `'2026-3-01T10:00:00Z'` is `NaN`, so that row is fine.
  - Both strings are rejected before the renderer runs. The schema pattern at `timeline.schema.json:128` requires `T` and an offset. `test/golden.mjs:192` is exactly the no-offset case (`schema` failure at `/events/0/at`).
- **Fix:** keep the rows for information, but write them with `"inSchema": false`, and assert only the `inSchema: true` rows. Never put a local-time string in a contract fixture: it makes `node tools/capture-fixtures.mjs` non-reproducible across machines, and the Step 3 guard (`plan:264`) would flag an `M` on the next run elsewhere.

---

## Should-fix

### S1. The time-zone deviation is misstated and recorded only where no user reads it
- **Plan:** `plan:34` scopes the deviation to "US zones before 1987, `Asia/Shanghai` 1986–1991 DST". `plan:762` records it only in `SESSION_HANDOFF.md`.
- **Evidence:** B2's table. The real scope has two parts:
  - **(a)** every zone, for instants before its first tzdata transition (LMT, roughly before 1900): the port shows the Windows base offset.
  - **(b)** any year before the Windows table's first adjustment rule: the port extrapolates that rule backwards (Chatham and Lord Howe in 1970).
- **Fix:** restate the deviation with those two cases, and give the Shanghai year-1 `UTC+08:06` versus `UTC+08:00` example. Put it in README "Known deviations from upstream", next to the CJK exemption, and in the XML doc on `Archify.Render`.
- **Assessment of the default itself:** OS table rather than embedded tzdata is the right call. Embedding tzdata or P/Invoking `ucal_*` is a lot of code for pre-1970 timelines. The default is fine. The statement of it is what needs fixing.

### S2. Offset time zones (`+08:00`) render upstream but would be rejected by the port; case-variant handling needs the extension unconditionally
- **Evidence:**
  - V8 accepts `"+08:00"` (`new Intl.DateTimeFormat('en-US', {timeZone:'+08:00'})` does not throw).
  - Upstream renders `payment-incident.timeline.json` with `timezone: "+08:00"`, and the header reads `+08:00 (UTC+08:00)`.
  - .NET rejects it: `TryConvertIanaIdToWindowsId("+08:00")` is false, and so is `TryFindSystemTimeZoneById`.
  - The plan's battery (`plan:127-128`) lacks the id, so valid input would fail with `timeline/invalid-timezone`.
- **Case variants:** `TryConvertIanaIdToWindowsId("asia/shanghai")` is **false**, but `TryFindSystemTimeZoneById("asia/shanghai")` is **true**. The plan's "extend only if the fixture shows…" (`plan:344`) is therefore required, not optional.
- **The rest of the battery matches the plan's rule:**
  - V8 accepts `EST`, `PST`, `Etc/GMT+8`, `asia/shanghai`, `America/new_york`, `Asia/Calcutta` and `US/Eastern`.
  - V8 rejects `China Standard Time`, `Eastern Standard Time`, `Mars/Olympus`, `""` and `UTC+8`.
  - .NET `Find` accepts `China Standard Time`, so the guard against Windows display ids is needed.
- **Fix:**
  1. Make the `/`-or-3–4-capitals `TryFindSystemTimeZoneById` branch unconditional.
  2. Add `+08:00`, `-05:30`, `Asia/Calcutta` and `US/Eastern` to the battery.
  3. Accept `^[+-]\d{2}:\d{2}$` as a fixed-offset zone in `IsIanaTimeZone` and `ZoneWall`. Check V8's other accepted spellings (`+0800`, `+08`) in the same battery.
- **Default 1 (case variants):** the right call, and with the extension there is no deviation to record. Upstream prints the authored spelling (`render-timeline.mjs:427`), and so will the port.

### S3. Task 8 describes the wrong tokenizer for tree; tree CJK wrapping is deterministic and not exempt
- **Plan:** `plan:615` says "`String(text).split(/(\s+)/)` → `Js.SplitKeepingSpaces`", with "8 sites".
- **Evidence:**
  - `render-tree.mjs:126` tokenizes with `String(text).match(/[\u2E80-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF\uFF00-\uFFEF]|[^\s\u2E80-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF\uFF00-\uFFEF]+\s*|\s+/g) || [text]`. That is one token per CJK or Hangul code unit, with no `u` flag.
  - `:201` uses `split(/\s+/)`, which keeps no separators and leaves `""` at the ends.
  - Tree has exactly one `split` call. The capture-group split exists only in timeline (`render-timeline.mjs:282`).
- **Why it matters:** this path uses no `Intl` and can be byte-exact. A port that follows the plan wraps CJK tree labels differently, and no golden catches it because every tree golden is ASCII.
- **Fix:** port `:126` literally. It is a BMP code-unit class, so .NET `Regex` can run it, with `\s` replaced by the `Js.IsSpace` set. Port `:201` as a `Js.IsSpace`-run split. Pin both with Node-captured rows in a fixture: a CJK label, a fullwidth label and a Hangul label through `wrapText`. Move `SplitKeepingSpaces` to timeline (see S5).

### S4. Task 7 omits class's text-wrapping, regex-split and `Math.hypot` sites
- **Evidence (`render-class.mjs`):**
  - **Signature wrapper.** `fillLines`/`wrapMember` (`:149-185`) split with lookbehind and lookahead: `text.split(/(?<=\(|, )|(?=\): )/)` and `.split(/(?<=, )/)`. They also use `trimEnd`/`trimStart` and `Array.from`.
  - **Parameter columns.** `memberColumns` (`:884-886`) uses `String(member.parameters).split(/(,\s*)/)`. That is a capture-group split with JS `\s`.
  - **Hypot.** `Math.hypot` appears at `:476` (bus conflict), `:554` (label segment choice, which reaches label coordinates) and `:731` (diagnostic).
  - **Rounding count.** The file has 6 `Math.round` sites and 0 `toFixed`, not "9 sites → `Js.Round`/`Js.ToFixed`" (`plan:574`).
- **Trap:** the Global Constraint at `plan:21` prefers `RegexOptions.ECMAScript`, but .NET throws on lookbehind under `ECMAScript`. These two patterns need default options with explicit `Js.IsSpace`-based classes.
- **Fix:** add a "Text" bullet and a "`Math.hypot` → `Js.Hypot`" bullet to Task 7's porting rules. Add one fixture row: a method signature with `NBSP` after a comma and a parameter longer than the line, captured from `wrapMember` in Node.

### S5. Js helper ownership and order: `TrimStart` arrives in Task 9 but Tasks 7 and 8 need it; `TrimEnd` and `SplitKeepingSpaces` have no Node fixture
- **Evidence:**
  - `Js.cs` has neither `TrimEnd` nor `TrimStart` (grep).
  - The plan adds `TrimEnd` in Task 5 with "no new fixture needed" (`plan:486`), `SplitKeepingSpaces` in Task 8 with an inline C# literal (`plan:615`), and `TrimStart` in Task 9 (`plan:656`).
  - Class (`:160`) and tree (`:135`) both call `trimStart` before Task 9.
  - The Global Constraint at `plan:27` requires every `Js` member to carry a fixture-backed test.
- **Fix:** add `TrimEnd`, `TrimStart`, `SplitKeepingSpaces` (and the tree tokenizer from S3) to Task 2. Capture their V8 output in `segmenter.json` or `strings.json`, using the existing `ws` whitespace set (U+FEFF, U+0085, NBSP, U+2028).

### S6. The spec's zh-CN fixture is missing, and the plan's no-Han rule cannot be met as worded
- **Spec:** `spec:290-292` sets the step-2 exit as "their 8 goldens + DST **and zh-CN** fixtures". `spec:76-77` lists the zh-CN locale fixture. The plan has none (File Structure `plan:70-75`; Exit `plan:769-777`).
- **Plan:** `plan:33` says "No golden or fixture golden may contain Han text".
- **Evidence:**
  - Every existing golden contains 47 Han characters from the template's language picker (`web-app-rendered.html`, `sequence-cache-miss-request.html` and `timeline-payment-incident-rendered.html` each count 47), so the rule as worded cannot be met.
  - Upstream renders zh-CN cleanly: `meta.locale = "zh-CN"` on `payment-incident.timeline`, `checkout-request.waterfall` and `payment-platform.tree` renders with 846, 933 and 1011 Han characters in the SVG.
  - None of those paths touches `Intl.Segmenter`, so they are fully byte-exact.
- **Fix:**
  1. Narrow the rule to "no Han text in a sequence `note`". That is the only parity-exempt path, and default 4's scope is otherwise right.
  2. Add a `payment-incident.zh-CN.timeline.json` fixture golden. Timeline exercises `i18nText(locale, 'timeline.compressed', {count})` and the localized legend.
  3. Optionally add a zh-CN tree, which also pins S3's CJK tokenizer for the translated chrome.

### S7. golden.mjs `expectFailure` cases for tree, class, timeline and waterfall are not ported
- **Spec:** `spec:267-268` says to port the `golden.mjs` `expectFailure` cases and assert on the `(code, path)` set.
- **Evidence:**
  - `test/golden.mjs:182-199` has eight schema cases: tree label and direction; class kind, relationship kind and visibility; timeline no-offset and evidence; waterfall negative start and evidence.
  - Phase 1 ported only the sequence `schema_version` case (`ValidationTests.cs:26`).
  - `plan:578` waves the class cases off as "already caught by the validator", but no test shows that.
- **Fix:** add the eight as `ValidationTests` theory rows asserting `schema/<keyword>` and the instance path that `golden.mjs` names. They cost nothing: the validator already exists. The lifecycle case at `:151` is covered by the plan's Task 6 fact. The probe confirms `lifecycle/main-path-gap @ /mainPath/0`.

### S8. Commit hygiene: no stage lists on most tasks, a hidden `.csproj` edit, and a model name
- **Stage lists:** Tasks 3, 4, 6, 7, 8, 9, 10 and 11 end with "Commit → message" and no `git add` list (`plan:390, 468, 552, 597, 635, 700, 751, 765`). The global rule requires exact files.
- **`InternalsVisibleTo`:** it is absent (grep finds none in `src/`). Tests call internal `Js.CivilFromDays`/`DaysFromCivil` (Task 3) and `WaterfallRenderer.Number` (Task 10). So Task 3 must modify `src/Archify.Net.Core/Archify.Net.Core.csproj`, or an `AssemblyInfo.cs`, and stage it. Neither appears in its Files list (`plan:338-339`).
- **Model name:** `plan:28` hard-codes the trailer `Claude Fable 5.1`. The brief says no model names. Phase-1 review N10 flagged the same thing.
- **Fix:** give every task an exact `git add` line. Add the csproj to Task 3's Files. Replace the trailer with "the attribution trailer the session prescribes".

### S9. Parity-row count: 18, not 20
- **Evidence:** the rows the plan writes are sequence 4 + lifecycle 3 + class 2 + tree 3 + timeline 3 + waterfall 3 = 18. The Goal (`plan:5`) also sums to 18 (10 + 2 + 6).
- **Fix:** `plan:69`, `plan:760` and `plan:762` say 20; change them to 18, or 19 with S6's zh-CN golden.

### S10. Task 1: upstream refuses the lifecycle notes fixture, and Step 2 has no remedy for it
- **Evidence:** rendering the plan's `deployment-release.notes.lifecycle.json` (`plan:209-213`) fails with `lifecycle/too-wide @ /mainPath`: "The lifecycle is 1404px wide … readable … only up to 1395px". The other five synthetic fixtures render, and each exercises its claim:
  - the URL breaks after `/`;
  - the 160-character token breaks by grapheme;
  - the brand ids `redis` and `postgresql` appear;
  - the DST header reads `America/New_York (UTC-05:00)`, and both `01:30:00 UTC-04:00` and `01:30:00 UTC-05:00` appear, with one break;
  - the waterfall shows `µs` and `data-waterfall-total="1250.5"`.
- **Fix:** set `transitions[0].note = 'next free runner'` (verified to render). Add that remedy to Step 2's list (`plan:261`).

---

## Nice-to-have

- **N1.** Review Focus (`plan:42`) says no example contains a non-ASCII character. `archify-dev-activity.timeline.json` contains U+2013. The rest of that sentence is verified: no note, brand, locale, `direction: "right"` or `"incomplete"`; time zones are only Asia/Shanghai; every example is `showcase`.
- **N2.** The `brandTopRailProblem` call at `render-sequence.mjs:139` runs on **authored** participants (`:126`), inside the column-fit helper. The measured `participants` map is built later (`:350`). State that this call site uses `ctx.Brands.BrandMarkFor(authoredParticipant)` directly (`plan:484`).
- **N3.** Class bus maps: `render-class.mjs:516-517` only delete, with no re-add, and nothing iterates `busPaths`/`busGroups`. `JsMap` is harmless. The order that matters is `rejected` (a `Set` of group arrays, `:496`, `:514`): use `JsSet<List<JsonObject>>`, keyed by reference.
- **N4.** The note-wrap fixture never makes the grapheme fallback cut beside a multi-code-point cluster, because the 160-character token is ASCII. Add a note token such as `'e\u0301'.repeat(60)`. Then a C# `char` or `Rune` loop in place of `Js.Graphemes` would fail the golden.
- **N5.** Task 10's sort list omits `render-waterfall.mjs:125` `[...list].sort(byStart)`, which should be `Js.Sort`.
- **N6.** No phase-2 test can tell `gateProfile` from a meta-only port. With `ARCHIFY_QUALITY_PROFILE=showcase`, upstream still renders the no-profile note-wrap fixture, so the gates found nothing, and a passing gate emits no bytes. Only a gate-failing fixture would pin it. State that the sites are review-checked only, or add one failing case.
- **N7.** Task 1 Step 1 contradicts itself. `plan:97` says to insert above `console.log` and move `targets`. `plan:259` says to place the block between the `targets` declaration and the render loop. Only the second works, because the block needs `EX` (`capture-fixtures.mjs:113`) and must push before the loop at `:129`. Keep `plan:259` and delete the other instruction.
- **N8.** The File Structure omits `tests/Archify.Net.Core.Tests/JsTests.cs`, which Task 10 modifies.
- **N9.** Class `Object.keys(NOTATION)`, `Object.entries(GLYPH)` and `Object.values(KIND_TONE)` (`:290, :317, :1037`), plus timeline `:366, :499`, iterate literal tables with string keys. Port them as ordered arrays in source order.

---

## Verified correct (30)

1. Line counts: grid 150, sequence 1105, lifecycle 1134, class 1095, tree 573, timeline 559, waterfall 374 (`plan:58-64`).
2. `render-examples.mjs:12-28` TARGETS gives exactly 10 goldens for these six types, with the plan's file names. The card's "8" is the undercount (`plan:771`). `sequence-cache-miss-request.html` and `lifecycle-agent-run.html` lack the `-rendered` suffix.
3. The 12 example inputs and the two golden-less examples (`async-job-roundtrip`, `deployment-release`) are as stated. All goldens exist under `upstream/examples/`.
4. Every `../shared/*` import of the seven files exists by name in C#:
   - `Utils`: `Esc`, `RenderDefinitions`, `RenderSemanticSigil`, `TextUnits`, `SemanticSigilFootprint`, `SourceBadgeFootprint`.
   - `SvgAttrs`: `AnimateAttr`, `Focus*`, `SvgRootAttrs`, `SvgAccessibleText`.
   - `ThrowDiagnosticProblems`.
   - `Legend`: `LegendFootprint`, `MeasureLegend`, `ResolveLegend`, `RenderLegend`, `RelationshipLegendObstacles`.
   - `Geometry`: `ComponentFill`, `ArrowClassMap`, `RectsOverlap`, the 8 `Clean*Problems`, `RoutePointsValue`, `AsArray`, `IsFinitePoint`, `EdgeLabelAccent`, `SegmentRectClearance`, `RoundedPath`, `CollectAmbiguousCorridors`, `LegacyDefaultFrom/ToSide`, `ProperSegmentIntersection`, `SegmentIntersectsRect`, `SuggestComponentSeparation`, `SuggestLabelObstacleFix`.
   - `TextFit`/`NodeTextFit` (`WidthFactor`, `HorizontalPadding`), including `NodeLabelLayout`.
   - `BrandMarks`: the four helpers, plus `BrandMarkFor` on the resolver.
   - `DesktopReadability`: the `DESKTOP_READER_DIAGRAM_WIDTH`, `MIN_PROJECTED_NODE_TEXT_PX` and `MinimumReadableSourceTextPx` constants.
   - `Routing.CreateRouter`, `Router.PathFor`, `ConnectionEndpointSide`, `InferredSides`, `PlaceAutomaticLabels`, `LayoutReport.ConnectionPath`.

   No shared module is missing.
5. `joinRoutePoints` callers are only `workflow-compiler.mjs:39, 2267, 3892, 4022, 4240, 4621, 4779, 4802`. No phase-2 `render-*.mjs` calls `normalizeRoutePoints`, and the memo (`geometry.mjs:1327`) is a pure identity cache.
6. The `IsHan` range table (`plan:284`) equals V8's `\p{Script=Han}` exactly, from a full code-point enumeration. U+FE0E is `\p{Mark}`, and U+200D is not.
7. `qualityProfileForGate` is at `geometry.mjs:1302-1306`. The six gate functions the plan names are the only callers (`:642, 878, 973, 1118, 1162, 1277`). Only `workflow-compiler.mjs` passes `profileIsAuthoritative: true`.
8. Sequence meta-only reads are at `:67, 117, 846, 919`, and gate profile arguments at `:682, 691, 701, 710, 752, 760`. `ArchitectureRenderer.cs:107` builds `gateProfile` as `Quality?.Name() ?? meta`.
9. Class has one `profile:` at `:777`, feeding `cleanEndpointSideProblems` (`:779`), crossing (`:794`), ambiguous corridor (`:795`), rhythm (`:796`) and label clearance (`:810`). Lifecycle, tree, timeline and waterfall read no quality profile.
10. `buildLayoutReport` is at class `:821`, tree `:392` (it includes `direction`), timeline `:533` and waterfall `:348`. Sequence and lifecycle have no `--layout-json`.
11. Brand `COLLECTIONS` is at `brand-marks.mjs:10-16`, and C# `BrandMarkResolver.Collection` matches it. Class, tree, timeline and waterfall have no brand.
12. Sorts: sequence `:484, 612, 724, 818`; lifecycle `:176, 215, 220, 299, 559, 615, 633, 644, 697, 742-746, 931`; default sorts lifecycle `:308`, tree `:98`, waterfall `:97`; class has no `.sort(`; timeline `:138, 192, 197, 213`.
13. Lifecycle `:176` cannot see `NaN`: `downByTarget` admits only depth-0 sources (`:158`).
14. Diagnostic code sets per renderer equal the plan's lists (Tasks 5–10), and so do the wrapper codes: `timeline/input :109`, `waterfall/input :105`, defaults at waterfall `:170` and sequence `:925`, and `tree/structure :108`.
15. All six pinned diagnostic facts reproduce upstream exactly:
    - `sequence/unknown-endpoint @ /messages/0/to`
    - `lifecycle/main-path-gap @ /mainPath/0`
    - `class/inheritance-cycle @ /relationships` (the mutation works as written)
    - `tree/missing-parent @ /nodes/1/parent`
    - `timeline/invalid-timezone @ /meta/timezone`
    - `waterfall/cycle @ /spans/0/parent`
16. The historical-years theory holds: years `0000`, `0001`, `0099`, `0100` and `2026` in UTC render, contain `UTC+00:00` and contain no three-digit hour.
17. V8 `Date.parse` field rules hold as the plan describes them:
    - February 30 becomes March 2, and February 29 in 2026 becomes March 1;
    - month 0 or 13 and day 0 or 32 give NaN;
    - `24:00` is accepted and `24:00:01` is NaN;
    - minute or second 60 is NaN;
    - offset `+24:00` is NaN, while `+23:59` and `-00:00` are accepted.

    Year 0000 and 9999 values match proleptic arithmetic.
18. Timeline line references match: `validCalendarDate :67-72`, `|| 'UTC' :73`, the zone try/catch at `:81-86`, `partsFormat`/`wall :113-119`, `zoneOffset`/`offsetLabel :121-133`, sort `:136-138`, `showSeconds :144`, the authored spelling in the header at `:427`, and the context with `offsetLabel` at `:451`.
19. Waterfall: `number()` at `:131-138` is copied verbatim. Also correct: `unit || 'ms'` at `:30`, `µs` at `:32`, `Math.min(...)` at `:109`, `serviceOf` keeping an explicit undefined at `:154`, the `→` at `:239`, and `focusEdgeAttrs(..., undefined, row.index)` at `:271`.
20. Note wrap `:178-248`: `noteBreakIndex` indexes UTF-16 units (`:190`), `trimEnd` is at `:221`, the grapheme loop at `:236`, and the closing-punctuation merge at `:206`.
21. `erd/grid.mjs` exports, line ranges and semantics match Task 4: defaults `:5-22`, `gridColumn` `:57`, `Math.max(-1, ...)` `:93`, in-place `columnX.set` `:89`, and authored `pos` winning `:103`. `JsMap.Set` updates in place and does not bump the linked-list version, so iterating while setting is safe.
22. The class grid follows `:62-69`. `bandedLayout(measuredTypes, {...grid, entityW: grid.typeW})` is at `:234`, `resolveEntityPos` at `:238`, and `heights.get(row) ?? height` at `:239`.
23. `SvgAttrs.FocusEdgeAttrs(string from, string to, string? label, string key, string? id)` omits `data-edge-label` for a null or empty label (`SvgAttrs.cs:83`).
24. `DiagnosticSink.ThrowDiagnosticProblems(prefix, problems, code = "layout/constraint", subject, details)` matches the Scene convention (`plan:38`).
25. `RenderContext` exposes `Diagram`, `Meta`, `Diagnostics`, `Locale`, `Brands`, `Options` and `SourceEvidence`. `IDiagramRenderer.Render` returns `(string Svg, JsonArray? Cards, LayoutReport? Layout)`. `Renderers.For` is the one switch. `LayoutReport(JsonObject)` exists.
26. `DiagramLoader.Parse` and `Normalize` return `JsonObject`. `Archify.Render` deep-clones its input and catches only `ArchifyDiagnosticException`, so Step 2's expected `NotSupportedException` failures are real. `DiagramTypes.Parse` knows all ten names.
27. `J.Num`/`Str`/`Arr`/`D`, `JsMap.Entries`, `Pt(double X, double Y)` and `Js.Stringify` support the test code as written. There are no type-name collisions for `ErGrid`, `GridEntity`, `Bands`, `ErdGrid` or `WallClock`.
28. The Task 1 guard arithmetic holds: 5 + 8 = 13 HTML goldens, and 20 + 8 = 28 checker JSONs. The grapheme count of `aaaaaaaaaaae\u0301` is 12. At `2026-03-08T07:00:00Z`, New York shows hour `03`.
29. Lifecycle: the `states` copy is at `:59`, the NUL separators at `:164`/`:177`, `label || note` at `:1048`/`:1065`, and the brand and evidence sites at `:69, 80, 899, 904, 1001, 1013, 1025`.
30. Tree: `direction` at `:29`, and `focusEdgeAttrs(edge.from, edge.to, undefined, edge.index)` at `:494`.

---

## The four planner defaults

1. **Case-variant IANA ids:** right. With S2's unconditional `TryFindSystemTimeZoneById` branch, .NET accepts what V8 accepts, so no deviation results. Offset ids need adding (S2).
2. **Year-0000 timestamps:** right. Civil arithmetic without `DateTime` reproduces upstream for UTC (the theory in item 16).
3. **OS zone table versus Node tzdata:** acceptable as the default. The scope must be restated (B2, S1) and stated where users read it.
4. **Han per-ideograph breaks exempt, Latin note-wrap parity-checked:** the right scope for the sequence segmenter path. `WordBreaks` must not be held to ICU on Latin (B1). The blanket no-Han rule must shrink to sequence notes, so zh-CN and tree CJK stay byte-exact (S3, S6).

## Verdict

The plan's upstream facts are accurate: 30 checked items hold, including every shared-import name, every sort and profile site, and every diagnostic code. Its structure follows the phase-1 template well. It is **not ready to execute** as written.

- **Three blockers** are tests that cannot pass on this machine:
  - `WordBreaks`' Latin assertion against a rule set that ICU contradicts;
  - `ZoneWall` asserting LMT and pre-rule rows that the Windows zone table cannot produce;
  - `DateParse` asserting V8's off-schema and local-time parses.

  Each is a small edit to the fixture or the test, not a design change.
- **The should-fix items** fall into four groups:
  - **Missed byte-exact paths:** the tree CJK tokenizer, and class signature wrapping with `Math.hypot`.
  - **Spec items dropped:** the zh-CN fixture and the `golden.mjs` schema cases.
  - **Deviation:** the time-zone deviation's wrong scope and placement, and the offset-id gap.
  - **Hygiene:** stage lists, the csproj edit, the row count, and the one fixture upstream refuses.

Fold B1–B3 and S1–S10 into the plan, then execute.

---

## Re-review (2026-10-14, after the fold-in)

Scope: the folded `2026-10-14-phase2-renderers.md` (uncommitted, 911 lines), against B1–B3 and S1–S10 above. Line numbers below are the folded plan's.

How it was checked:
- **Task 1, run verbatim.** A scratch harness extracted the plan's Task 1 code block (`plan:106-297`) and ran it as written, with `write`/`out` redirected to the scratchpad. Every pushed target was then rendered through the upstream renderer. Nothing was written to the repo.
- **zh-CN render.** The harness ran `node renderers/timeline/render-timeline.mjs <scratch>/tests/fixtures/diagrams/payment-incident.zh-CN.timeline.json <scratch>/tests/goldens/payment-incident.zh-CN-rendered.html` (from `C:\projects\archify\archify`, with no `ARCHIFY_*` variables). That fixture is `payment-incident.timeline.json` with `meta.locale = "zh-CN"`. It renders with exit 0 and 846 Han characters in the SVG.
- **Wall-clock rows.** A `dotnet fsi` script compared `TimeZoneInfo` offsets with V8's on every guaranteed named-zone row.
- **Schema cases.** The nine new schema cases ran through upstream's `generated-validators.mjs`.

### Verdicts

| Item | Verdict | Evidence |
|---|---|---|
| B1 `WordBreaks` | **ADDRESSED** | `plan:325` (Han+Mark input only, `=> Graphemes`), `plan:352-360` (concatenation, plus per-grapheme on Han runs; no Latin ICU assertion), Exit `plan:907` |
| B2 `ZoneWall` rows | **ADDRESSED** | `plan:150-155` (`guaranteed` flag), `plan:453-464` (asserts guaranteed rows only and checks that deviation rows exist), `plan:473`. Probe: 123 of 153 rows are guaranteed; **72/72 named-zone guaranteed rows match .NET** (2026 + 9999, including Chatham and Lord Howe DST); the fixed-offset rows give years `"1"`/`"0"` at 08:00 |
| B3 `DateParse` rows | **ADDRESSED** | `plan:124-133` (local-time string removed, `inSchema` flag), `plan:417` (stated simplification), `plan:433-441`. The guard at `plan:307` holds: `["2026-10-01 10:00:00Z","2026-3-01T10:00:00Z"]` |
| S1 deviation scope and placement | **ADDRESSED** | `plan:34` (cases a and b, with the `UTC+08:06` example), Task 11 Step 3 `plan:889-893` (README section and `Archify.Render` remarks), Exit `plan:909` |
| S2 offset and case-variant ids | **ADDRESSED, one gap** | `plan:418` makes the `/`-or-capitals branch unconditional and adds a fixed-offset rule (3); `plan:138` adds `+08:00`, `-05:30`, `+0800`, `+08`, `+08:00:00`, `+8:00`, `Asia/Calcutta`, `US/Eastern`. **Gap:** V8 marks `+0800` and `+08` **valid** (probe), but rule (3) is written as `^[+-]\d{2}:\d{2}$`. `plan:418` also says "accept exactly the spellings the fixture marks valid", so the instruction contradicts itself. See R1 |
| S3 tree tokenizer | **ADDRESSED** | `plan:331` (`CjkWrapTokens`, code-unit class), `plan:739`, fixture `plan:181`; the guard at `plan:308` holds (8 tokens) |
| S4 class text and `Math.hypot` | **ADDRESSED** | `plan:692-694` (splits, trims, runes, `Js.Hypot` at :476/:554/:731, 6 `Math.round`, ordered literal tables), with members in `plan:332-333` |
| S5 helper ownership and order | **ADDRESSED** | All trims and splits are in Task 2 (`plan:327-334`) and pinned by `text-wrap.json` (`plan:169-185`). Tasks 5, 7, 8 and 9 reference Task 2. `WsClass` (`plan:328`) decodes to exactly V8's `\s` set (probe over U+0000–U+FFFF) |
| S6 zh-CN, Han rule | **ADDRESSED** | `plan:33` (ban narrowed to sequence notes), `plan:268-271` and `:295` (fixture and target), Task 9 row `plan:794`; renders upstream (command above) |
| S7 `golden.mjs` schema cases | **ADDRESSED** | Task 4 Step 6 `plan:554-589`. All nine `(code, path)` pairs match ajv exactly, including `schema/minimum@/spans/0/start` and `schema/required@/meta`, and `payments.class.json` `types[0].methods[0]` exists. The `Example`/`Fails` helpers exist (`ValidationTests.cs:20-21`). Nine is the right count; the original review's "eight" was an undercount |
| S8 commit hygiene | **ADDRESSED** | Every task has an exact `git add` (`plan:312, 405, 474, 552, 589, 632, 673, 721, 759, 827, 878, 897`). The trailer has no model name (`plan:28`). The csproj is in Task 3's Files and staged (`plan:413, 474`); `<InternalsVisibleTo Include="Archify.Net.Core.Tests" />` matches the default test assembly name (the test csproj sets no `AssemblyName`) |
| S9 row count | **ADDRESSED** | 19 everywhere (`plan:5, 73, 887, 894, 905`). Actual rows: sequence 4 + lifecycle 3 + class 2 + tree 3 + timeline 4 + waterfall 3 = 19. Guards 14 HTML / 29 checker = 5+9 / 20+9 |
| S10 lifecycle notes fixture | **ADDRESSED** | `plan:246` (`'next free runner'`), remedy in Step 2 `plan:301`; renders upstream (harness) |

All nine harness targets render upstream. The note-wrap golden includes the new `e\u0301` × 60 token: 120 code units, which wraps over 4 lines. Every fixture key the tests read is written by the capture block:
- `segmenter.json`: `text`, `han`, `graphemes`, `tokens`, `hanTokens`;
- `text-wrap.json`: `trim{s,trimStart,trimEnd}`, `splitOnSpaces`/`splitKeepingSpaces{s,parts}`, `cjkWrapTokens{s,tokens}`, `splitSignature`/`splitAfterCommaSpace{s,parts}`, `splitKeepingCommas{s,parts,isComma}`;
- `dates.json`: `inSchema`, `ms`;
- `time-zones.json`: `valid[{id,valid}]`, `wall[{zone,ms,guaranteed,parts}]`.

Task order after the helper move is sound. No task uses a `Js` member before Task 2 or Task 3 creates it.

### New issues introduced by the fold-in (all small; none blocks)

- **R1 (should-fix):** rule (3) at `plan:418`, and `ZoneWall`'s offset parsing at `plan:419`, must accept `±HH`, `±HHMM` and `±HH:MM`. V8 accepts all three and rejects `+08:00:00` and `+8:00`. Written as `^[+-]\d{2}(:?\d{2})?$` with hours ≤ 23 and minutes ≤ 59, `IsIanaTimeZone_matches_Intl_DateTimeFormat_acceptance` passes. As written today, it fails on `+0800` and `+08`.
- **R2 (stale text):** Task 1 Files (`plan:98`) still says "append before the final `console.log`", "8 goldens" and "8 checker JSONs". It lists neither `text-wrap.json` nor `payment-incident.zh-CN.timeline.json`. Step 1 (`plan:103`) and the guard (`plan:310`) are correct.
- **R3 (stale text):** `plan:469` ("add `InternalsVisibleTo` only if…") and `plan:871` ("if Task 3 added `InternalsVisibleTo`, otherwise make `Number` `public`") contradict `plan:413`, which now adds it unconditionally. Delete both hedges.
- **R4 (count text):** `plan:71` and `plan:554` say "eight" schema cases, while the theory lists nine rows and `plan:589/887/908` say nine. The Task 9 heading (`plan:763`) says "3 goldens", but the task now has 4.

### Verdict

**Ready to execute.** All three blockers and all ten should-fix items are addressed, and the fold-in's concrete claims reproduce: every fixture renders, every guard holds, the guaranteed wall rows match .NET, and the schema pairs match ajv. R1 is the only item that would fail a test (Task 3's acceptance battery on `+0800`/`+08`), and it is a one-regex edit. R2–R4 are stale wording. The planner can fix all four in place before Task 1, or the Task 3 implementer can apply R1 when the battery first runs.
