# Archify C# Port — Phase 2: erd/grid + sequence, lifecycle, class, tree, timeline, waterfall renderers

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ArchifyNet.Render(type, json)` produces HTML byte-identical to upstream Node archify 3.1.0 for `sequence`, `lifecycle`, `class`, `tree`, `timeline` and `waterfall` (10 upstream goldens + 2 example goldens minted from upstream + 7 synthetic fixture goldens = 19 parity rows), on the phase-1 foundation unchanged except for the `Js` members these renderers need (`Graphemes`, `WordBreaks`, Han detection, the JS-whitespace trims and splits, `DateParse`, IANA time-zone wall clock) and the `erd/grid.mjs` port that class (now) and erd (phase 4) share.

**Architecture:** As phase 1: one C# file per upstream `.mjs`, same base name, same function names PascalCased, same order of operations. Each `render-<type>.mjs` module body becomes `<Type>Renderer : IDiagramRenderer` with a private `Scene` class whose constructor runs the module-level statements in source order and whose methods are the module's functions (the shape `Architecture/ArchitectureRenderer.cs` already has). `Renderers.For(type)` dispatches. Module globals (`process.argv --layout-json`, `process.env`) do not exist: the layout report is always built and returned as `LayoutReport` for class/tree/timeline/waterfall and is `null` for sequence/lifecycle (upstream has no `--layout-json` there). JavaScript semantics stay inside `Js` (now a `partial` class across three files).

**Tech Stack:** .NET 10, C# 14, `System.Text.Json.Nodes`, no runtime NuGet packages; xUnit 2.9.3. Node 24 only as a dev-time tool to mint goldens and fixtures from upstream (`C:\projects\archify`).

**Spec:** `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (porting-order step 2; `Js` table rows `Graphemes`/`WordBreaks`; the **Time zones** paragraph; the CJK decision). Phase-1 plan (template and settled rulings): `docs/superpowers/plans/2026-10-13-phase1-foundation-architecture.md`. Upstream checkout `C:\projects\archify` at `c21c31ff`; skill package root `C:\projects\archify\archify\` = `UP/`. Board card: `PORT-002`.

## Global Constraints

Reused verbatim from phase 1 (they all still apply):

- Target `net10.0`; `Nullable` + `ImplicitUsings` enabled; `TreatWarningsAsErrors` true; `InvariantGlobalization` **false** (ICU needed for `localeCompare` and IANA time zones).
- **Strict byte parity** of `RenderResult.Html` with upstream for valid inputs. Only exemption: CJK word wrapping (phase 2+).
- Never format a number with `ToString()`/interpolation in renderer code — `Js.Num`. Never `List.Sort` — `Js.Sort` (comparator returns `double`, as JS comparators return fractional deltas) / `Js.SortDefault`. JS string `<`/`>` in a comparator is `string.CompareOrdinal`. Never enumerate `Dictionary`/`HashSet` — `JsMap`/`JsSet`. Never `string.Trim()` on authored text — `Js.Trim`. Never `WebUtility.HtmlEncode` — `Js.EscapeHtml`. `Number(x)`/`parseFloat(x)` on strings — `Js.ToNumber`/`Js.ParseFloat`.
- JS `||` is truthiness (`0`, `''`, `NaN`, `null`, `undefined` fall through); JS `??` is null/undefined only. Port each literally (`item.labelSegment ?? 1` keeps `0`; `item.labelDx || 0` does not). `Math.max()`/`Math.min()` of an empty list are ∓Infinity — never LINQ `Max()`/`Min()` on a possibly empty sequence. `Number.isInteger(d)` = `double.IsFinite(d) && d == Math.Floor(d)`.
- Ported regexes use `RegexOptions.ECMAScript | RegexOptions.CultureInvariant` where the pattern allows it, ASCII classes otherwise; a JS `$` without the `m` flag becomes `\z` (.NET `$` also matches before a trailing `\n`); a JS `^` without `m` matches only at position 0.
- SVG/HTML built with `StringBuilder`/interpolated strings in upstream attribute order. No `XDocument`, `XmlWriter`, `HtmlAgilityPack`.
- Output text is UTF-8 **without BOM** (`new UTF8Encoding(false)`), LF line endings as upstream emits them.
- `upstream/` and `tests/goldens/` are byte-exact copies: `.gitattributes` marks them `-text`. Never hand-edit.
- Diagnostics are returned, never thrown, from the public `Archify.*` API; codes are upstream's. Message text is not parity-checked; the **set** of `(code, instancePath)` pairs is.
- `Archify.Net.Core` has **no NuGet dependencies**.
- Every `Js` member carries `// ponytail: V8 parity` and a fixture-backed test.
- Commits: message in a temp file + `git commit -F`; stage exact files (never `-a`, `-A`, `.`) — every task below ends with its exact `git add` list; end the message with the attribution trailer the session prescribes.
- Task state on ContextBoard project `Archify.Net` (id 46); no `TODO.md`.

New in phase 2:

- **CJK exemption, precisely.** Upstream wraps sequence notes with `Intl.Segmenter` (render-sequence.mjs:179 grapheme, :201/:205 word). The word segmenter runs **only on tokens that contain Han** (`/\p{Script=Han}/u`, lines 197 and 204); Latin notes never reach it. Therefore: the Latin/emoji/combining-mark note-wrap path **is** byte-parity-checked (fixture golden in Task 5); `Js.WordBreaks` (whose only input is a Han+Mark run) and any rendered **sequence note** that depends on it is outside the byte-parity guarantee; `Js.Graphemes` is parity-checked for every script the fixture carries. The exemption is **that narrow**: tree label wrapping (render-tree.mjs:126, a code-unit character class, no `Intl`), class member wrapping, timeline title wrapping and every zh-CN locale rendering are byte-exact and are pinned by fixtures (Tasks 2, 8, 9). The only ban is: no Han text inside a sequence `note` of any golden or fixture golden (every golden already carries the template's 47-character Han language picker, so a blanket no-Han rule is unmeetable).
- **Time zones.** `meta.timezone` accepts what V8's `Intl.DateTimeFormat` accepts and nothing else, decided by `Js.IsIanaTimeZone` (fixture-pinned battery, Task 3): IANA ids in any letter case (`asia/shanghai` is valid in both engines), legacy links (`Asia/Calcutta`, `US/Eastern`, `EST`, `Etc/GMT+8`) and fixed offsets `±HH:MM`; a Windows display id such as `"China Standard Time"` must yield `timeline/invalid-timezone` exactly as V8 does. The authored spelling is kept in the header (render-timeline.mjs:427), so no case-folding of ids anywhere. Wall-clock fields come from `Js.ZoneWall` (= upstream `wall()`, render-timeline.mjs:116-119): offset from `TimeZoneInfo` for instants inside `DateTime`'s range, `BaseUtcOffset` outside it, then proleptic-Gregorian civil arithmetic — so year `0000` (1 BC, which upstream's `era: 'short'` branch maps to year `0`) formats without a `DateTime`. **Stated deviation (two cases, restated verbatim in the README "Known deviations from upstream" and in the XML doc of `ArchifyNet.Render`, Task 11):** .NET on Windows converts with the OS zone table (registry adjustment rules), not Node's bundled tzdata. **(a)** For instants before a zone's first tzdata transition (local mean time, roughly before 1900) the port shows the zone's base offset: upstream labels a `Asia/Shanghai` event at `0001-01-01T00:00:00Z` `UTC+08:06` (LMT +08:05:43), the port prints `UTC+08:00`. **(b)** For years before the OS table's first adjustment rule the port extrapolates that rule backwards: `Pacific/Chatham` and `Australia/Lord_Howe` in 1970 read +13:45/+11:00 (DST applied) where upstream reads +12:45/+10:00. UTC is exact for every year; every battery zone is exact for 2026 and 9999. The DST fixture pins the 2026 `America/New_York` transitions. Upgrade path if it bites: embed tzdata or P/Invoke ICU `ucal_*`.
- **Quality profile (settled in phase 1, Ruling 1 / S5; unchanged):** a shared geometry gate that upstream reads through `qualityProfileForGate` (geometry.mjs:1302-1306, `process.env.ARCHIFY_QUALITY_PROFILE || profile`) receives `gateProfile = ctx.Options.Quality?.Name() ?? meta.quality_profile` from the renderer. Those gates are `CleanCrossingProblems`, `CleanAmbiguousCorridorProblems`, `CleanBorderRunProblems`, `CleanRouteRhythmProblems`, `CleanLabelRouteClearanceProblems`, `CleanLabelCanvasContainmentProblems`. Every **direct** `meta?.quality_profile` read in a renderer stays meta-only. `SvgAttrs.SvgRootAttrs(ctx)` already applies the option. Per-renderer site lists are in each task.
- **`LayoutReport`:** non-null only for class, tree, timeline, waterfall (upstream `buildLayoutReport` at render-class.mjs:821, render-tree.mjs:392, render-timeline.mjs:533, render-waterfall.mjs:348); sequence and lifecycle have no `--layout-json` and return `null`.
- **Brand marks:** `BrandMarkResolver.Collection` maps only architecture/workflow/sequence/dataflow/lifecycle (brand-marks.mjs:10-16); for class/tree/timeline/waterfall `PrepareDiagramBrandMarks` is a no-op, matching upstream's plain `loadDiagram`. Sequence and lifecycle resolve a node's mark **once from the authored `JsonObject`** (`ctx.Brands.BrandMarkFor(authored)`) and store it on the measured record; upstream carries it through object spreads as a symbol property, the port carries it explicitly (phase-1 `Box.Brand` rule).
- **Scene conventions** (so every renderer reads alike): module-level literal constants → `const`/`static class Layout`; module-level values computed from the diagram → `readonly` fields assigned in the `Scene` constructor in source order; functions → methods; `throwDiagnosticProblems(prefix, problems, { code, subject, diagnostics })` → `throw ctx.Diagnostics.ThrowDiagnosticProblems(prefix, problems, code, subject, details)`; `i18nText(locale, key, values)` → `ctx.Locale.Translate(locale, key, values)`; `svgRootAttrs(meta)` → `SvgAttrs.SvgRootAttrs(ctx)`; `svgAccessibleText(meta, kind)` → `SvgAttrs.SvgAccessibleText(ctx, kind)`; `focusEdgeAttrs(from, to, label, index, id)` → `SvgAttrs.FocusEdgeAttrs(from, to, label, Js.Num(index), id)` (`label` may be `undefined` → `null`); `writeDiagram({...})` → the `Render` tuple; `x.cards` → `ctx.Diagram.Arr("cards")`.

## Review Focus

Spec-implied inputs no upstream golden exercises (verified: none of the 12 phase-2 example inputs contains a `note`, a `brand`, a `locale`, `layout.direction: "right"`, `status: "incomplete"`, a CJK character, or a time zone other than `Asia/Shanghai`; the only non-ASCII is an en dash in `archify-dev-activity.timeline.json`); each has a pinned test in the owning task:

1. **Sequence note with a URL, an unbroken 160-character token and a long sentence** → wraps exactly as Node (grapheme fallback, punctuation breaks, `trimEnd`). Fixture golden `note-wrap-rendered.html`, Task 5.
2. **`meta.timezone` = `"China Standard Time"` (a Windows id)** → render fails with `timeline/invalid-timezone`, as V8 rejects it; IANA ids render. Tests in Task 3 (`IsIanaTimeZone` battery) and Task 9.
3. **Timeline spanning the 2026 `America/New_York` DST transitions** (spring-forward gap, fall-back repeated hour, multi-day with a disclosed break) → ticks and labels on the display zone's wall clock equal Node. Fixture golden `dst-new-york-rendered.html`, Task 9.
4. **Waterfall with `status: "incomplete"`/`"error"`/`"cancelled"`, unit `us` and sub-0.01 durations** → `number()`'s three formatting branches and the µs label equal Node. Fixture golden `incomplete-trace-rendered.html`, Task 10.
5. **Sequence participants with built-in brand marks** → brand rail, label-fit width and `data-node-brand-*` passport equal Node. Fixture golden `brand-participants-rendered.html`, Task 5.

---

## File Structure

```
src/Archify.Net.Core/Js/Js.cs                      becomes `public static partial class Js` (one-word change)
src/Archify.Net.Core/Js/JsText.cs                  partial Js: Graphemes, WordBreaks, IsHan/HasHan/IsMark, TrimStart/TrimEnd, the JS-whitespace splits
                                                   (SpaceTokens, HanTokens, SplitOnSpaces, SplitKeepingSpaces, CjkWrapTokens, SplitSignature, SplitAfterCommaSpace, SplitKeepingCommas)
src/Archify.Net.Core/Js/JsTime.cs                  partial Js: DateParse, IsIanaTimeZone, ZoneWall, WallClock ← Date.parse / Intl.DateTimeFormat formatToParts
src/Archify.Net.Core/Archify.Net.Core.csproj       + InternalsVisibleTo("Archify.Net.Core.Tests") (Task 3)
src/Archify.Net.Core/Erd/Grid.cs                   class ErdGrid ← UP/renderers/erd/grid.mjs (150) — class `ErdGrid` because Architecture/Grid.cs already owns `Grid`
src/Archify.Net.Core/Sequence/SequenceRenderer.cs  ← UP/renderers/sequence/render-sequence.mjs (1105)
src/Archify.Net.Core/Lifecycle/LifecycleRenderer.cs← UP/renderers/lifecycle/render-lifecycle.mjs (1134)
src/Archify.Net.Core/Class/ClassRenderer.cs        ← UP/renderers/class/render-class.mjs (1095)
src/Archify.Net.Core/Tree/TreeRenderer.cs          ← UP/renderers/tree/render-tree.mjs (573)
src/Archify.Net.Core/Timeline/TimelineRenderer.cs  ← UP/renderers/timeline/render-timeline.mjs (559)
src/Archify.Net.Core/Waterfall/WaterfallRenderer.cs← UP/renderers/waterfall/render-waterfall.mjs (374)
src/Archify.Net.Core/Archify.cs                    Renderers.For gains six arms
tests/Archify.Net.Core.Tests/JsTextTests.cs        segmenter fixture tests
tests/Archify.Net.Core.Tests/JsTimeTests.cs        dates + time-zone fixture tests
tests/Archify.Net.Core.Tests/ErdGridTests.cs       erd-grid fixture tests
tests/Archify.Net.Core.Tests/ValidationTests.cs    + the nine golden.mjs:182-199 schema cases (Task 4)
tests/Archify.Net.Core.Tests/JsTests.cs            + waterfall number() fixture fact (Task 10)
tests/Archify.Net.Core.Tests/ParityTests.cs        Targets() gains 19 rows; per-type diagnostic facts
tests/fixtures/js/segmenter.json, text-wrap.json, dates.json, time-zones.json, waterfall-number.json, erd-grid.json
tests/fixtures/diagrams/note-wrap.sequence.json, brand-participants.sequence.json, deployment-release.notes.lifecycle.json,
                        payment-platform.right.tree.json, dst-new-york.timeline.json, payment-incident.zh-CN.timeline.json,
                        incomplete-trace.waterfall.json
tests/goldens/async-job-roundtrip-rendered.html, deployment-release-rendered.html, note-wrap-rendered.html,
              brand-participants-rendered.html, deployment-release.notes-rendered.html, payment-platform.right-rendered.html,
              dst-new-york-rendered.html, payment-incident.zh-CN-rendered.html, incomplete-trace-rendered.html
tests/goldens/checker/<each new golden>.json
tools/capture-fixtures.mjs                         extended (re-runnable)
README.md                                          "Known deviations from upstream" section (Task 11)
```

Shared-module gaps: **none.** Every `../shared/*.mjs` import of the seven upstream files (utils, cli, diagnostics, legend, text-fit incl. `nodeLabelLayout`, i18n, geometry incl. `componentFill`/`arrowClassMap`/`edgeLabelAccent`/`properSegmentIntersection`/`collectAmbiguousCorridors`/`legacyDefault*Side`/`suggestComponentSeparation`/`suggestLabelObstacleFix`, brand-marks, desktop-readability incl. `minimumReadableSourceTextPx`, automatic-labels, layout-report `connectionPath`) and `../architecture/routing.mjs` `createRouter` already exists in `src/Archify.Net.Core/Shared/*.cs` / `Architecture/Routing.cs` (verified by name). `erd/grid.mjs` is the only unported module.

`joinRoutePoints` (phase-1 carried item a): grep of `UP/renderers`, `UP/scripts`, `UP/bin` finds callers only in `renderers/workflow/workflow-compiler.mjs` (lines 39, 2267, 3892, 4022, 4240, 4621, 4779, 4802). **No phase-2 renderer calls it**, and no phase-2 renderer calls `normalizeRoutePoints` on an already-normalised array, so the identity memo is unobservable in this phase. The memo port moves to the phase-3 (workflow) plan with a fixture there; `Geometry.JoinRoutePoints`/`NormalizeRoutePoints` stay as they are.

`Js.Sort` (carried item b): every phase-2 comparator was read. Numeric keys come from schema-required numbers (`message.y`, `event.t` after the `Date.parse` validity gate, `span.start`, `a.ideal`, `excessPx`) or from `Map.get` on keys guaranteed present (render-sequence.mjs:612 iterates the same map's keys; render-lifecycle.mjs:176 sorts sources that `downByTarget` restricted to depth-0 states, :215 `routeCost` is a finite sum). None can see `NaN`/`undefined`. Three default sorts (`[a,b].sort()` at render-lifecycle.mjs:308, `[...cycle].sort()` at render-tree.mjs:98 and render-waterfall.mjs:97) are `Js.SortDefault`.

---

### Task 1: Extend `tools/capture-fixtures.mjs` — every phase-2 fixture and golden in one re-runnable change

**Files:**
- Modify: `tools/capture-fixtures.mjs` (append before the final `console.log`)
- Create (by running it): `tests/fixtures/js/{segmenter,text-wrap,dates,time-zones,waterfall-number,erd-grid}.json`, `tests/fixtures/diagrams/{note-wrap.sequence,brand-participants.sequence,deployment-release.notes.lifecycle,payment-platform.right.tree,dst-new-york.timeline,payment-incident.zh-CN.timeline,incomplete-trace.waterfall}.json`, 9 goldens in `tests/goldens/`, 9 checker JSONs in `tests/goldens/checker/`

**Interfaces:**
- Produces: fixture JSON consumed by Tasks 2, 3, 4 and parity goldens consumed by Tasks 5–10. The existing `targets` array and the checker loop are extended, so one run mints everything (phase-1 goldens are re-rendered and must come out byte-identical — the Step 3 guard checks that).

- [ ] **Step 1: Insert the capture code** between the phase-1 `const targets = [...]` declaration (capture-fixtures.mjs:122-128) and the `for (const [type, input, golden] of targets) render(...)` loop at :129 — the block needs `EX` (:113) and must `targets.push(...)` before that loop so the existing checker loop (:131-137) also writes the new goldens' checker JSON:

```js
// --- Intl.Segmenter (sequence notes): graphemes for every script, words for Han tokens. `han` marks the parity-exempt rows. ---
const graphemeSeg = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
const wordSeg = new Intl.Segmenter(undefined, { granularity: 'word' });
const LONG_NOTE = 'this note explains the whole retry and backoff policy in a lot of detail so it is very long';
const URL_NOTE = 'see https://api.example.com/v2/users/profile/settings/notifications/email-preferences';
const segTexts = ['', 'ascii', 'e\u0301', 'aaaaaaaaaaae\u0301', 'na\u00efve caf\u00e9', '\u{1F468}\u200D\u{1F469}\u200D\u{1F467}', '\u{1F1E9}\u{1F1EA}\u{1F1EB}\u{1F1F7}', 'x'.repeat(40),
  URL_NOTE, 'file-edit seq +4', '\u2705 done', 'a\u200db', 'Ünïcödé', '한글', 'ｶﾅ halfwidth',
  // Han (parity-exempt): Node's dictionary segmentation is recorded for information; the C# test asserts only concatenation on these rows.
  '成功或抛错都会', 'inner.run 成功或抛错都会 close；外层 init 失败发生在 try 之前。', '评分：会话 ID 匹配 +4、匹配 file-edit seq +4、非空事件 +1；取最高且大于 0 的结果。', '漢\ufe0e字'];
write('fixtures/js/segmenter.json', segTexts.map((text) => ({
  text, han: /\p{Script=Han}/u.test(text),
  graphemes: [...graphemeSeg.segment(text)].map((s) => s.segment),
  words: [...wordSeg.segment(text)].map((s) => s.segment),
  // render-sequence.mjs:196 and :203 tokenizers, so the C# tokenizer (Js.IsSpace/IsHan/IsMark based) is pinned too
  tokens: text.match(/\s+|\S+/gu) || [],
  hanTokens: text.match(/\s+|\p{Script=Han}[\p{Script=Han}\p{Mark}]*|[^\s\p{Script=Han}]+/gu) || [],
})));

// --- Date.parse on the timeline schema's ISO shape (timeline.schema.json:128) plus V8 range edges. `inSchema: false` rows are
// information only (V8's legacy parser accepts them; the schema rejects them before any renderer runs, golden.mjs:192). Never put a
// string without an offset here: V8 reads it as the capture machine's LOCAL time and the fixture stops being reproducible. ---
const AT_SHAPE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,3})?)?(Z|[+-]\d{2}:\d{2})$/;   // timeline.schema.json:128 verbatim
const dateStrings = ['2026-10-01T10:05:00+08:00', '2026-10-01T10:05Z', '2026-10-01T10:05:00.5Z', '2026-10-01T10:05:00.123+05:30', '2026-10-01T10:05:00.12Z',
  '2026-02-30T00:00:00Z', '2026-02-29T00:00:00Z', '2024-02-29T00:00:00Z', '2026-13-01T00:00:00Z', '2026-00-10T00:00:00Z', '2026-10-32T00:00:00Z', '2026-10-00T00:00:00Z',
  '2026-10-01T24:00:00Z', '2026-10-01T24:00:01Z', '2026-10-01T23:60:00Z', '2026-10-01T23:59:60Z', '2026-10-01T10:00:00+24:00', '2026-10-01T10:00:00+23:59',
  '2026-10-01T10:00:00+14:00', '2026-10-01T10:00:00-12:00', '2026-10-01T10:00:00-00:00', '0000-01-01T00:00:00Z', '0001-01-01T00:00:00Z', '0099-01-01T00:00:00Z',
  '0100-01-01T00:00:00Z', '1969-12-31T23:59:59.999Z', '9999-12-31T23:59:59Z', '2026-10-01 10:00:00Z', '2026-3-01T10:00:00Z'];
write('fixtures/js/dates.json', dateStrings.map((s) => ({ s, inSchema: AT_SHAPE.test(s), ms: String(Date.parse(s)) })));

// --- Intl.DateTimeFormat: zone-id acceptance and wall-clock parts (render-timeline.mjs:82, :113-119) ---
// Acceptance battery: IANA ids, case variants, legacy links, fixed offsets in V8's accepted and rejected spellings, Windows display ids.
const zoneIds = ['UTC', 'Etc/UTC', 'GMT', 'Asia/Shanghai', 'America/New_York', 'Europe/Berlin', 'Asia/Kolkata', 'Pacific/Chatham', 'Australia/Lord_Howe',
  'asia/shanghai', 'America/new_york', 'Asia/Calcutta', 'US/Eastern', 'EST', 'PST', 'Etc/GMT+8', '+08:00', '-05:30', '+0800', '+08', '+08:00:00', '+8:00',
  'China Standard Time', 'Eastern Standard Time', 'Mars/Olympus', '', 'UTC+8'];
const zoneValid = zoneIds.map((id) => { try { new Intl.DateTimeFormat('en-US', { timeZone: id }); return { id, valid: true }; } catch { return { id, valid: false }; } });
const wallZones = ['UTC', 'Asia/Shanghai', 'America/New_York', 'Europe/Berlin', 'Asia/Kolkata', 'Pacific/Chatham', 'Australia/Lord_Howe', '+08:00', '-05:30', '+0800', '+08'];
const instants = ['2026-03-08T06:59:59Z', '2026-03-08T07:00:00Z', '2026-03-08T07:00:00.750Z', '2026-11-01T05:59:59Z', '2026-11-01T06:00:00Z', '2026-11-01T06:30:00Z',
  '2026-10-01T02:00:00Z', '2026-10-04T16:00:00Z', '2026-04-05T02:30:00Z', '2026-01-01T00:00:00Z', '2026-12-31T23:59:59Z', '1970-01-01T00:00:00Z', '1969-12-31T23:59:59Z',
  '0001-01-01T00:00:00Z', '0099-01-01T00:00:00Z', '0000-01-01T00:00:00Z', '9999-12-31T23:59:59Z'].map((s) => Date.parse(s));
function wallParts(timezone, ms) {   // verbatim render-timeline.mjs:113-119
  const partsFormat = new Intl.DateTimeFormat('en-US', { timeZone: timezone, hourCycle: 'h23', era: 'short', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const parts = Object.fromEntries(partsFormat.formatToParts(new Date(ms)).map((part) => [part.type, part.value]));
  return { y: parts.era === 'BC' ? String(1 - Number(parts.year)) : parts.year, mo: parts.month, d: parts.day, h: parts.hour, mi: parts.minute, s: parts.second };
}
// `guaranteed`: rows the port must reproduce exactly — UTC and fixed offsets for every year, every zone for 2026 and 9999. The other
// rows (LMT years 0000/0001/0099, and 1969/1970 for zones whose OS rule is extrapolated backwards) are the stated deviation and are
// kept for information only.
const guaranteedWall = (zone, ms) => zone === 'UTC' || /^[+-]\d{2}(:?\d{2})?$/.test(zone) || ms >= Date.parse('2026-01-01T00:00:00Z');
write('fixtures/js/time-zones.json', { valid: zoneValid,
  wall: wallZones.flatMap((zone) => instants.map((ms) => ({ zone, ms, guaranteed: guaranteedWall(zone, ms), parts: wallParts(zone, ms) }))) });

// --- waterfall number(): render-waterfall.mjs:131-138 copied verbatim and evaluated by V8 ---
function wfNumber(value) {
  if (value !== 0 && Math.abs(value) < 0.01) {
    return Math.abs(value) < 1e-6 ? Number(value.toPrecision(3)).toString()
      : value.toLocaleString('en-US', { maximumSignificantDigits: 3 });
  }
  const rounded = Math.abs(value) > Number.MAX_VALUE / 100 ? value : Math.round(value * 100) / 100;
  return rounded.toLocaleString('en-US', { maximumFractionDigits: 2 });
}
const wfValues = [0, 0.004, 0.0049, 0.0051, 0.00999, 0.01, 0.0000005, 0.00000049, 1e-7, 1.234e-9, 0.125, 0.5, 1, 1.005, 12.345, 12.355, 99.995, 100, 1234.5, 1234567.891, 4.2e6, -0.004, -12.345, 1.7e307, Number.MAX_VALUE];
write('fixtures/js/waterfall-number.json', wfValues.map((value) => ({ value, text: wfNumber(value) })));

// --- JS whitespace trims and the five regex splits/tokenizers the tree, class and timeline wrappers use. Each regex is copied verbatim
// from its renderer and evaluated by V8; the C# members (Task 2) reproduce them with explicit JS-whitespace classes. ---
const wsTexts = ['﻿x﻿', '\u0085x\u0085', ' x ', ' x ', ' x ', '\tx\n', '　x', '', '   ', 'a b', 'a b'];
const wrapTexts = ['Payment platform', '  two  spaces  ', 'trailing ', ' leading', 'a b c', '', 'x', '支付平台核心服务', '결제 플랫폼 코어', 'ＡＢＣ　ｄｅｆ', 'mixed 漢字 text 한글 ok',
  'URL https://api.example.com/v2/users', 'tab\tsep\nline', 'éé combining', '⺀鿿가힯豈﫿＀￯ edges', '😀 emoji 😀'];
const signatures = ['calculate(amount: Money, currency: Currency): Money', 'process(order: Order, customer: Customer, options: ProcessingOptions): Result<Receipt>',
  'veryLongMethodNameWithoutParametersAtAll(): void', 'noParens', 'a(b)', 'f(x: int, y: int)', 'g(): ', 'h(p: Map<string, List<int>>, q: string): Map<string, int>'];
const parameterLists = ['a: int, b: string', 'a,b', 'a,  b, c', 'single', '', 'x: Map<string, int>, y', 'a:b:c, d'];
write('fixtures/js/text-wrap.json', {
  trim: wsTexts.map((s) => ({ s, trimStart: s.trimStart(), trimEnd: s.trimEnd() })),
  splitOnSpaces: wrapTexts.map((s) => ({ s, parts: s.split(/\s+/) })),                                   // render-tree.mjs:201
  splitKeepingSpaces: wrapTexts.map((s) => ({ s, parts: s.split(/(\s+)/) })),                            // render-timeline.mjs:282
  cjkWrapTokens: wrapTexts.map((s) => ({ s, tokens: s.match(/[⺀-鿿가-힯豈-﫿＀-￯]|[^\s⺀-鿿가-힯豈-﫿＀-￯]+\s*|\s+/g) || [s] })),   // render-tree.mjs:126
  splitSignature: signatures.map((s) => ({ s, parts: s.split(/(?<=\(|, )|(?=\): )/) })),                 // render-class.mjs:176
  splitAfterCommaSpace: signatures.map((s) => ({ s, parts: s.split(/(?<=, )/) })),                       // render-class.mjs:181
  splitKeepingCommas: parameterLists.map((s) => ({ s, parts: s.split(/(,\s*)/), isComma: s.split(/(,\s*)/).map((p) => /^,\s*$/.test(p)) })),   // render-class.mjs:884-885
});

// --- erd/grid.mjs: pure module, importable. Entities as the class renderer passes them (measured width/height, row/col, optional pos). ---
const eg = await imp('renderers/erd/grid.mjs');
const classGrid = { origin: [32, 40], gapX: 128, gapY: 84, entityW: 180 };
const gridSets = [
  [{ id: 'a', row: 0, col: 0, width: 180, height: 120 }, { id: 'b', row: 0, col: 1, width: 240, height: 100 }, { id: 'c', row: 1, col: 0.5, width: 300, height: 80 }],
  [{ id: 'a', row: 0, col: 0, width: 180, height: 60 }, { id: 'b', row: 2, col: 2, width: 180, height: 60 }, { id: 'p', pos: [10, 20], width: 180, height: 60 }],
  [{ id: 'wide', row: 0, col: 0.5, width: 400, height: 60 }, { id: 'n', row: 0, col: 1, width: 100, height: 60 }],
  [{ id: 'x', row: 1, col: 1.5, width: 180, height: 60 }],
  [],
];
const mapEntries = (m) => [...m.entries()];
write('fixtures/js/erd-grid.json', {
  defaults: eg.DEFAULT_ER_GRID,
  erGridLayout: [{ layout: undefined, entities: [{ row: 0, col: 0 }] }, { layout: undefined, entities: [{ pos: [1, 2] }] }, { layout: { mode: 'grid', gapX: 10 }, entities: [] }, { layout: { mode: 'free' }, entities: [] }]
    .map((er) => ({ er, grid: eg.erGridLayout(er) })),
  entityHeight: [{ attributes: [] }, { attributes: [1, 2, 3] }, {}].map((entity) => ({ entity, height: eg.entityHeight(entity, eg.DEFAULT_ER_GRID) })),
  banded: gridSets.flatMap((entities) => [null, new Map([[0, 200]])].map((columnGaps) => {
    const bands = eg.bandedLayout(entities, classGrid, { columnGaps });
    return { entities, columnGaps: columnGaps ? mapEntries(columnGaps) : null,
      bands: { columnX: mapEntries(bands.columnX), rowY: mapEntries(bands.rowY), widths: mapEntries(bands.widths), heights: mapEntries(bands.heights), totalWidth: bands.totalWidth, totalHeight: bands.totalHeight },
      positions: entities.map((entity) => eg.resolveEntityPos(entity, classGrid, bands).map((v) => (Number.isNaN(v) ? 'NaN' : v))) };
  })),
  placement: (() => {
    const er = { entities: [{ id: 'a', row: 0, col: 0 }, { id: 'b', row: 0, col: 0 }, { id: 'c', row: -1, col: 0 }, { id: 'd' }, { id: 'e', row: 1.5, col: 0 }, { id: 'f', pos: [1, 2] }] };
    const grid = eg.erGridLayout(er); const problems = [];
    const bands = eg.bandedLayout(er.entities.map((e) => ({ ...e, height: 50 })), grid);
    eg.validateErGridPlacement(er, grid, bands, problems);
    const noGrid = []; eg.validateErGridPlacement({ entities: [{ id: 'z' }] }, null, null, noGrid);
    return { withGrid: problems, noGrid };
  })(),
});

// --- phase-2 synthetic fixtures (every one must render under upstream; a refusal is a fixture bug, fix the fixture, never the oracle) ---
const ex = (f) => JSON.parse(fs.readFileSync(path.join(EX, f), 'utf8'));
// Latin note wrap (sequence-note-wrap.test.mjs inputs, standard profile so the showcase-only note gates stay off). No Han: the CJK path is parity-exempt.
const noteWrap = {
  schema_version: 1, diagram_type: 'sequence',
  meta: { title: 'Note wrap', output: 'note-wrap.html', column_fit: 'spread' },
  participants: ['client', 'api', 'db', 'cache', 'queue'].map((id) => ({ id, type: 'backend', label: id.toUpperCase() })),
  messages: [
    { id: 'request', from: 'client', to: 'api', y: 200, label: 'request', note: LONG_NOTE },
    { id: 'link', from: 'client', to: 'api', y: 420, label: 'docs', note: URL_NOTE },
    // second token: 60 two-code-unit clusters, so a char/Rune loop in place of Js.Graphemes cuts differently and fails the golden
    { id: 'token', from: 'client', to: 'api', y: 640, label: 'token', note: `${'x'.repeat(160)} ${'é'.repeat(60)}` },
    { id: 'accent', from: 'api', to: 'client', y: 900, label: 'reply', variant: 'return', note: 'na\u00efve caf\u00e9 aaaaaaaaaaae\u0301 r\u00e9sum\u00e9 \u2705 done' },
    { id: 'short', from: 'api', to: 'client', y: 960, label: 'cached', variant: 'return', note: 'cached for 5 minutes' },
  ],
  activations: [{ participant: 'api', from: 190, to: 980 }],
};
write('fixtures/diagrams/note-wrap.sequence.json', noteWrap);
// Built-in brand marks on the two participants with the shortest labels (brand-marks.mjs top-rail rule needs label room).
const branded = ex('cache-miss-request.sequence.json');
branded.meta.output = 'brand-participants.html';
const byLabel = [...branded.participants].sort((a, b) => a.label.length - b.label.length || branded.participants.indexOf(a) - branded.participants.indexOf(b));
byLabel[0].brand = 'redis'; byLabel[1].brand = 'postgresql';
write('fixtures/diagrams/brand-participants.sequence.json', branded);
// Lifecycle transition notes: `transition.label || transition.note` (render-lifecycle.mjs:1048, 1065) and the note font path.
const notes = ex('deployment-release.lifecycle.json');
notes.meta.output = 'deployment-release.notes.html';
notes.transitions[0].note = 'next free runner';   // 'picked up by the next free runner' pushes the lifecycle to 1404px and upstream refuses it with lifecycle/too-wide
const unlabeled = notes.transitions.find((t) => t.label && t.from !== notes.mainPath[0]); delete unlabeled.label; unlabeled.note = 'automatic';
write('fixtures/diagrams/deployment-release.notes.lifecycle.json', notes);
// Tree drawn rightward (render-tree.mjs:29 `direction === 'right'`; both examples are downward).
const right = ex('payment-platform.tree.json'); right.meta.output = 'payment-platform.right.html'; right.layout = { ...(right.layout || {}), direction: 'right' };
write('fixtures/diagrams/payment-platform.right.tree.json', right);
// DST: America/New_York spring-forward (2026-03-08 02:00 EST → 03:00 EDT) and fall-back (2026-11-01 02:00 EDT → 01:00 EST), multi-day, one disclosed break.
const dst = {
  schema_version: 1, diagram_type: 'timeline',
  meta: { title: 'Daylight saving transitions', output: 'dst-new-york.html', evidence: 'illustrative', timezone: 'America/New_York', quality_profile: 'showcase' },
  lanes: [{ id: 'clock', label: 'Clock' }, { id: 'ops', label: 'Operations' }],
  events: [
    { id: 'before_gap', at: '2026-03-08T01:30:00-05:00', title: 'Last EST half hour', lane: 'clock', kind: 'change' },
    { id: 'after_gap', at: '2026-03-08T03:30:00-04:00', title: 'First EDT half hour', lane: 'clock', kind: 'change' },
    { id: 'noon', at: '2026-03-08T12:00:00-04:00', title: 'Noon check', detail: 'Dashboards reviewed after the shift.', lane: 'ops', kind: 'action' },
    { id: 'eve', at: '2026-10-31T23:30:00-04:00', title: 'Evening before fall-back', lane: 'ops', kind: 'alert' },
    { id: 'first_130', at: '2026-11-01T01:30:00-04:00', title: 'First 01:30 (EDT)', lane: 'clock', kind: 'change' },
    { id: 'second_130', at: '2026-11-01T01:30:00-05:00', title: 'Second 01:30 (EST)', lane: 'clock', kind: 'recovery' },
    { id: 'morning', at: '2026-11-01T09:00:00-05:00', title: 'Morning review', lane: 'ops', kind: 'action' },
  ],
};
write('fixtures/diagrams/dst-new-york.timeline.json', dst);
// zh-CN locale (spec "zh-CN fixture"; upstream renders it cleanly). Exercises the bundled catalog in legend labels, the evidence chip,
// `timeline.axis` and `timeline.compressed` — none of it touches Intl.Segmenter, so it is fully byte-exact.
const zh = ex('payment-incident.timeline.json'); zh.meta.output = 'payment-incident.zh-CN.html'; zh.meta.locale = 'zh-CN';
write('fixtures/diagrams/payment-incident.zh-CN.timeline.json', zh);
// Waterfall: µs unit, incomplete/error/cancelled statuses, sub-0.01 durations for number()'s three branches.
const incomplete = {
  schema_version: 1, diagram_type: 'waterfall',
  meta: { title: 'Incomplete trace', output: 'incomplete-trace.html', evidence: 'measured', unit: 'us' },
  spans: [
    { id: 'root', name: 'handle request', start: 0, end: 1250.5, service: 'edge' },
    { id: 'auth', name: 'verify token', start: 0.004, duration: 0.0049, parent: 'root', service: 'auth' },
    { id: 'fast', name: 'cache probe', start: 1, duration: 0.0000005, parent: 'root', service: 'edge' },
    { id: 'db', name: 'query orders', start: 12.345, end: 812.345, parent: 'root', service: 'db', status: 'error' },
    { id: 'retry', name: 'retry query', start: 820, duration: 300.25, parent: 'root', service: 'db', status: 'cancelled' },
    { id: 'hang', name: 'await webhook', start: 900, parent: 'root', service: 'edge', status: 'incomplete' },
  ],
};
write('fixtures/diagrams/incomplete-trace.waterfall.json', incomplete);
// Phase-2 golden targets (appended to the phase-1 array so the checker loop covers them)
targets.push(
  ['sequence', path.join(EX, 'async-job-roundtrip.sequence.json'), 'async-job-roundtrip-rendered.html'],
  ['lifecycle', path.join(EX, 'deployment-release.lifecycle.json'), 'deployment-release-rendered.html'],
  ['sequence', out('fixtures/diagrams/note-wrap.sequence.json'), 'note-wrap-rendered.html'],
  ['sequence', out('fixtures/diagrams/brand-participants.sequence.json'), 'brand-participants-rendered.html'],
  ['lifecycle', out('fixtures/diagrams/deployment-release.notes.lifecycle.json'), 'deployment-release.notes-rendered.html'],
  ['tree', out('fixtures/diagrams/payment-platform.right.tree.json'), 'payment-platform.right-rendered.html'],
  ['timeline', out('fixtures/diagrams/dst-new-york.timeline.json'), 'dst-new-york-rendered.html'],
  ['timeline', out('fixtures/diagrams/payment-incident.zh-CN.timeline.json'), 'payment-incident.zh-CN-rendered.html'],
  ['waterfall', out('fixtures/diagrams/incomplete-trace.waterfall.json'), 'incomplete-trace-rendered.html'],
);
```
(The `remoteBrand` capture at the end of the file is untouched.) The plan reviewer rendered the synthetic fixtures through upstream on 2026-10-14: all render once the lifecycle note reads `'next free runner'`; the URL note breaks after `/`, the ASCII token breaks by grapheme, `redis`/`postgresql` appear, the DST header reads `America/New_York (UTC-05:00)` with both `01:30:00 UTC-04:00` and `01:30:00 UTC-05:00` and one break, the waterfall shows `µs` and `data-waterfall-total="1250.5"`; zh-CN on `payment-incident.timeline` renders 846 Han characters. Only the combining-accent note token was added after that run.

- [ ] **Step 2: Run** `node tools/capture-fixtures.mjs` from the repo root → `fixtures captured`. If upstream refuses a synthetic fixture (non-zero exit with a diagnostic on stderr), the fixture is wrong, not upstream: for `note-wrap` double the message `y` spacing and the activation range (if only the combining-accent token is refused, shorten it to 40 repeats); for `brand-participants` move the brands to the next-shortest labels; for `deployment-release.notes` shorten the note text further (a longer note widens the main path past `lifecycle/too-wide`'s 1395px); for `dst-new-york` shorten titles. Never edit the renderers or an existing golden.

- [ ] **Step 3: Guards.**
  - `git status --porcelain tests/goldens tests/fixtures upstream` lists only **new** files (`??`); any `M` on a phase-1 golden or fixture means the upstream checkout moved — stop and report.
  - `node -e "const f=require('./tests/fixtures/js/time-zones.json');console.log(JSON.stringify(f.valid.filter(v=>!v.valid).map(v=>v.id)))"` → must contain `"China Standard Time"`, `"Eastern Standard Time"`, `"Mars/Olympus"`, `""`, `"UTC+8"` and must **not** contain `"+08:00"`, `"asia/shanghai"`, `"Asia/Calcutta"`, `"US/Eastern"`. Record the full `valid` list in the commit message body (it is the contract Task 3 implements).
  - `node -e "const w=require('./tests/fixtures/js/time-zones.json').wall;console.log(w.filter(x=>x.zone==='America/New_York'&&x.ms===Date.parse('2026-03-08T07:00:00Z'))[0].parts.h, w.filter(x=>x.guaranteed).length, w.length)"` → `03 …` (spring forward: 07:00Z is 03:00 EDT); the guaranteed count is below the total.
  - `node -e "const d=require('./tests/fixtures/js/dates.json');console.log(JSON.stringify(d.filter(x=>!x.inSchema).map(x=>x.s)))"` → exactly `["2026-10-01 10:00:00Z","2026-3-01T10:00:00Z"]`.
  - `node -e "const t=require('./tests/fixtures/js/text-wrap.json');console.log(JSON.stringify(t.splitKeepingSpaces[1].parts),t.cjkWrapTokens[7].tokens.length)"` → `["","  ","two","  ","spaces","  ",""] 8` (one token per ideograph).
  - `node -e "const s=require('./tests/fixtures/js/segmenter.json');console.log(s.find(x=>x.text==='aaaaaaaaaaae\u0301').graphemes.length)"` → `12`.
  - `ls tests/goldens/*.html | wc -l` → 14; `ls tests/goldens/checker/*.json | wc -l` → 29.

- [ ] **Step 4: Commit** — `git add tools/capture-fixtures.mjs tests/fixtures tests/goldens` → `test: capture segmenter, date, time-zone and erd-grid fixtures; mint phase-2 goldens from upstream`.

---

### Task 2: `Js.Graphemes`, `Js.WordBreaks`, Han/Mark detection (`JsText.cs`)

**Files:**
- Modify: `src/Archify.Net.Core/Js/Js.cs` (`public static class Js` → `public static partial class Js`)
- Create: `src/Archify.Net.Core/Js/JsText.cs`, `tests/Archify.Net.Core.Tests/JsTextTests.cs`

**Interfaces:**
- Produces (all `public static` on `Archify.Net.Js`):
  - `List<string> Graphemes(string s)` — extended grapheme clusters, = `Intl.Segmenter(undefined, { granularity: 'grapheme' })`. Implementation: `StringInfo.GetTextElementEnumerator(s)` (UAX #29 since .NET 5).
  - `List<string> WordBreaks(string s)` — stand-in for `Intl.Segmenter(undefined, { granularity: 'word' })`. Its **only** upstream input is a Han+Mark run (render-sequence.mjs:203-205), so it is specified for that input alone: one segment per extended grapheme cluster, i.e. `=> Graphemes(s)` — a mark stays on its ideograph, and for `漢︎字` ICU gives the same `["漢︎","字"]`. ICU's dictionary-based Han word segmentation is **not** reproduced (`// ponytail: per-grapheme fallback, parity-exempt per spec; P/Invoke ubrk_* from icu.dll if a CJK user appears`). It is never held to ICU on Latin text (WB6/WB7 letter joins across `.`, ZWJ sequences, flag pairs) because no caller passes Latin.
  - `bool IsHan(int codePoint)` — Unicode `Script=Han` by range table (2E80–2E99, 2E9B–2EF3, 2F00–2FD5, 3005, 3007, 3021–3029, 3038–303B, 3400–4DBF, 4E00–9FFF, F900–FA6D, FA70–FAD9, 16FE2–16FE3, 16FF0–16FF1, 20000–2A6DF, 2A700–2B739, 2B740–2B81D, 2B820–2CEA1, 2CEB0–2EBE0, 2EBF0–2EE5D, 2F800–2FA1D, 30000–3134A, 31350–323AF — equals V8's `\p{Script=Han}` by full enumeration, plan review); `bool HasHan(string s)`; `bool IsMark(int codePoint)` (`UnicodeCategory` Mn/Mc/Me; U+FE0E is a mark, U+200D is not). .NET `Regex` has no `\p{Script=…}`, so the two sequence regexes become code: `List<string> SpaceTokens(string s)` (= `s.match(/\s+|\S+/gu) || []`, split on `Js.IsSpace` runs by code point) and `List<string> HanTokens(string s)` (= `/\s+|\p{Script=Han}[\p{Script=Han}\p{Mark}]*|[^\s\p{Script=Han}]+/gu`).
  - `string TrimStart(string s)`, `string TrimEnd(string s)` — one-sided `Js.Trim` (same `IsSpace` table; used by sequence `:221`, class `:159-160`, tree `:130-136`, timeline `:286-297`).
  - The JS-whitespace class as a .NET pattern fragment: `const string WsClass = "[\t\n\v\f\r    -     　﻿]"` (ES WhiteSpace + LineTerminator = `Js.IsSpace`; **not** .NET `\s`, which includes U+0085 and excludes U+FEFF). Members built on it, each equal to one upstream regex call:
    - `string[] SplitOnSpaces(string s)` = `s.split(/\s+/)` (render-tree.mjs:201; keeps the empty strings JS leaves at the ends);
    - `string[] SplitKeepingSpaces(string s)` = `s.split(/(\s+)/)` (render-timeline.mjs:282; the captured separators are interleaved, `"  two  spaces  "` → `["","  ","two","  ","spaces","  ",""]`);
    - `List<string> CjkWrapTokens(string s)` = `s.match(/[⺀-鿿가-힯豈-﫿＀-￯]|[^\s⺀-鿿가-힯豈-﫿＀-￯]+\s*|\s+/g) || [s]` (render-tree.mjs:126 — a BMP **code-unit** class with no `u` flag, so a non-BMP emoji is two units and falls in the "other" run; port the regex verbatim with `\s` replaced by `WsClass`, under default `RegexOptions` + `CultureInvariant`);
    - `string[] SplitSignature(string s)` = `s.split(/(?<=\(|, )|(?=\): )/)` (render-class.mjs:176) and `string[] SplitAfterCommaSpace(string s)` = `s.split(/(?<=, )/)` (:181) — **lookbehind throws under `RegexOptions.ECMAScript`**; these two run under default options (the Global Constraints regex rule yields to that), empty-match split semantics as JS (`"a(b)"` → `["a(","b)"]`);
    - `string[] SplitKeepingCommas(string s)` = `s.split(/(,\s*)/)` (:884) and `bool IsCommaPart(string part)` = `/^,\s*$/.test(part)` (:885), both with `WsClass`.
    Every member is pinned by `tests/fixtures/js/text-wrap.json` (Task 1), never by a C# literal.

- [ ] **Step 1: Failing tests** (`JsTextTests.cs`)

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class JsTextTests
{
    static JsonArray Fx => (JsonArray)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/segmenter.json")))!;
    static string[] Strs(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    [Fact] public void Graphemes_match_Intl_Segmenter_for_every_script()
    {   // Not CJK-exempt: grapheme clustering is UAX #29 in both engines; Han rows included on purpose.
        foreach (var e in Fx) Assert.Equal(Strs(e!["graphemes"]), Js.Graphemes(e["text"]!.GetValue<string>()));
    }
    [Fact] public void WordBreaks_concatenate_and_fall_back_to_graphemes_on_Han_runs()
    {   // Parity-exempt by spec: the only upstream input is a Han+Mark run; `words` (ICU's dictionary segmentation) is recorded for information.
        foreach (var e in Fx)
        {
            var text = e!["text"]!.GetValue<string>();
            Assert.Equal(text, string.Concat(Js.WordBreaks(text)));
            foreach (var token in Js.HanTokens(text).Where(Js.HasHan)) Assert.Equal(Js.Graphemes(token), Js.WordBreaks(token));
        }
    }
    [Fact] public void Tokenizers_match_the_note_wrap_regexes()
    {
        foreach (var e in Fx)
        {
            var text = e!["text"]!.GetValue<string>();
            Assert.Equal(Strs(e["tokens"]), Js.SpaceTokens(text));
            Assert.Equal(Strs(e["hanTokens"]), Js.HanTokens(text));
            Assert.Equal(e["han"]!.GetValue<bool>(), Js.HasHan(text));
        }
    }
    [Fact] public void IsHan_and_IsMark_edges()
    {
        Assert.True(Js.IsHan(0x4E00)); Assert.True(Js.IsHan(0x3007)); Assert.True(Js.IsHan(0x20000)); Assert.False(Js.IsHan(0x3042)); Assert.False(Js.IsHan('a'));
        Assert.True(Js.IsMark(0x0301)); Assert.True(Js.IsMark(0xFE0E)); Assert.False(Js.IsMark(0x200D));   // ZWJ is Cf, not a mark
    }

    static readonly JsonObject Wrap = (JsonObject)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/text-wrap.json")))!;
    [Fact] public void TrimStart_TrimEnd_match_V8()
    {
        foreach (var e in Wrap["trim"]!.AsArray()) { var s = e!["s"]!.GetValue<string>(); Assert.Equal(e["trimStart"]!.GetValue<string>(), Js.TrimStart(s)); Assert.Equal(e["trimEnd"]!.GetValue<string>(), Js.TrimEnd(s)); }
    }
    [Fact] public void Tree_and_timeline_splits_match_V8()
    {
        foreach (var e in Wrap["splitOnSpaces"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitOnSpaces(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["splitKeepingSpaces"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitKeepingSpaces(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["cjkWrapTokens"]!.AsArray()) Assert.Equal(Strs(e!["tokens"]), Js.CjkWrapTokens(e["s"]!.GetValue<string>()));
    }
    [Fact] public void Class_signature_splits_match_V8()
    {
        foreach (var e in Wrap["splitSignature"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitSignature(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["splitAfterCommaSpace"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitAfterCommaSpace(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["splitKeepingCommas"]!.AsArray())
        {
            var parts = Js.SplitKeepingCommas(e!["s"]!.GetValue<string>());
            Assert.Equal(Strs(e["parts"]), parts);
            Assert.Equal(e["isComma"]!.AsArray().Select(x => x!.GetValue<bool>()), parts.Select(Js.IsCommaPart));
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet test --filter JsTextTests` → compile failure. Expected.
- [ ] **Step 3: Implement `JsText.cs`** per Interfaces. `SpaceTokens`/`HanTokens` iterate `Rune`s (`s.EnumerateRunes()`), classify with `IsSpace(rune.Value)`, `IsHan`, `IsMark`, and emit runs exactly as the regex alternation would (whitespace run | Han + following Han/Mark | run of neither-space-nor-Han). Lone surrogates: `EnumerateRunes` yields U+FFFD for them — acceptable (schema forbids lone surrogates in authored text). `Graphemes` of `""` → empty list. The split members use `Regex.Split` **only** where .NET's semantics equal JS's (no captures, or captures interleaved identically — verify against the fixture rows with empty leading/trailing parts); otherwise implement as a `Regex.Matches` walk that emits the between-match slices and, for `(…)` groups, the captures.
- [ ] **Step 4: Run** → pass. If `Graphemes` disagrees on a flag or family-emoji row, the .NET ICU version is older than Node's — report the row; do not special-case. A `CjkWrapTokens` disagreement on the emoji row means the class was compiled over runes instead of UTF-16 units.
- [ ] **Step 5: Commit** — `git add src/Archify.Net.Core/Js/Js.cs src/Archify.Net.Core/Js/JsText.cs tests/Archify.Net.Core.Tests/JsTextTests.cs` → `feat(core): Js text helpers — graphemes, Han tokenizers, JS-whitespace trims and the tree/class/timeline splits (V8 fixtures)`.

---

### Task 3: `Js.DateParse`, `Js.IsIanaTimeZone`, `Js.ZoneWall` (`JsTime.cs`)

**Files:**
- Create: `src/Archify.Net.Core/Js/JsTime.cs`, `tests/Archify.Net.Core.Tests/JsTimeTests.cs`
- Modify: `src/Archify.Net.Core/Archify.Net.Core.csproj` — add `<ItemGroup><InternalsVisibleTo Include="Archify.Net.Core.Tests" /></ItemGroup>` (none exists today; the tests below call `internal` members, and Task 10 relies on it too)

**Interfaces:**
- Produces:
  - `double DateParse(string s)` — V8 `Date.parse` for the shape timeline.schema.json:128 admits (`YYYY-MM-DDTHH:MM[:SS[.fff]](Z|±HH:MM)`). Fields are validated as V8 does (the `inSchema: true` rows of `dates.json` are the contract; expected: month 1–12, day 1–31 with calendar overflow allowed (`02-30` → March 2), hour ≤ 24 with 24 only at `24:00[:00[.000]]`, minute/second ≤ 59, offset hour ≤ 23 and minute ≤ 59, one to three fraction digits). Strings outside the schema shape return `NaN` — a **documented simplification**, not parity: V8's legacy parser accepts `2026-10-01 10:00:00Z` and reads offset-less strings as machine-local time, but the schema rejects both before any renderer runs (golden.mjs:192), so the renderer can never observe the difference. Epoch ms via `DaysFromCivil(y, m, d) * 86_400_000 + …` so year `0000` and `9999` work without `DateTime`.
  - `bool IsIanaTimeZone(string id)` — true iff any of: (1) `TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)`; (2) `id` contains `/` or is 3–4 upper-case ASCII letters, **and** `TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)` (this branch is **unconditional**: it is what accepts `asia/shanghai`, `America/new_york`, `Asia/Calcutta`, `US/Eastern`, `EST`; the `/`-or-capitals guard is what keeps `"China Standard Time"` out, because `TryFindSystemTimeZoneById` accepts Windows display ids); (3) `id` matches `^[+-]\d{2}(:?\d{2})?$` with hours ≤ 23 and minutes ≤ 59 — a fixed-offset zone in the three spellings V8 accepts, `+08:00`, `+0800` and `+08` (the plan re-review verified all three; `+08:00:00` and `+8:00` are rejected, as the battery pins); upstream renders `timezone: "+08:00"` with the header `+08:00 (UTC+08:00)`; (4) `id` is `"UTC"`. The `time-zones.json` `valid` battery is the contract. The authored spelling is never changed (render-timeline.mjs:427 prints it).
  - `readonly record struct WallClock(string Y, string Mo, string D, string H, string Mi, string S)`; `WallClock ZoneWall(string timeZone, double ms)` — = `wall(ms)` (render-timeline.mjs:116-119): `floorMs = Math.Floor(ms / 1000) * 1000`; offset = for a fixed-offset id the parsed `±HH[:MM]`/`±HHMM` (same regex as rule (3); `+08` means `+08:00`); otherwise `TimeZoneInfo.FindSystemTimeZoneById(timeZone).GetUtcOffset(DateTime.UnixEpoch.AddMilliseconds(floorMs))` when `floorMs` lies within `DateTime.MinValue..MaxValue`, else `BaseUtcOffset`; `local = floorMs + offsetMs`; `days = FloorDiv(local, 86_400_000)`; `(y, m, d) = CivilFromDays(days)`; `Y = Js.Num(y)` (so year 0 → `"0"`, matching upstream's `String(1 - Number(parts.year))` for 1 BC), `Mo/D/H/Mi/S` two-digit with leading zero. `TimeZoneInfo` throws for an unknown id — callers gate with `IsIanaTimeZone` first (the renderer does, render-timeline.mjs:81-86).
  - `internal static long DaysFromCivil(long y, int m, int d)` / `internal static (long y, int m, int d) CivilFromDays(long z)` — Howard Hinnant's proleptic Gregorian algorithms (the same arithmetic V8's `MakeDay` performs).

- [ ] **Step 1: Failing tests** (`JsTimeTests.cs`)

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class JsTimeTests
{
    static JsonNode Fx(string f) => JsonNode.Parse(File.ReadAllText(Fixtures.Fixture($"js/{f}")))!;

    [Fact] public void DateParse_matches_V8_on_the_schema_shape_and_its_edges()
    {   // inSchema:false rows are V8's legacy-parser results; the schema rejects those strings before any renderer runs, so they are not asserted.
        foreach (var e in Fx("dates.json").AsArray().Where(e => e!["inSchema"]!.GetValue<bool>()))
            Assert.Equal(e!["ms"]!.GetValue<string>(), Js.Num(Js.DateParse(e["s"]!.GetValue<string>())));
    }
    [Fact] public void Out_of_schema_strings_are_NaN()   // the documented simplification, pinned so nobody "improves" it into local-time parsing
    {
        Assert.True(double.IsNaN(Js.DateParse("2026-10-01T10:00:00"))); Assert.True(double.IsNaN(Js.DateParse("2026-10-01 10:00:00Z")));
    }
    [Fact] public void IsIanaTimeZone_matches_Intl_DateTimeFormat_acceptance()
    {
        foreach (var e in Fx("time-zones.json")["valid"]!.AsArray())
            Assert.True(e!["valid"]!.GetValue<bool>() == Js.IsIanaTimeZone(e["id"]!.GetValue<string>()), $"zone id \"{e["id"]}\"");
    }
    [Fact] public void Windows_display_ids_are_rejected_like_V8()   // Review Focus 2
    {
        Assert.False(Js.IsIanaTimeZone("China Standard Time")); Assert.False(Js.IsIanaTimeZone("Eastern Standard Time"));
        Assert.True(Js.IsIanaTimeZone("Asia/Shanghai")); Assert.True(Js.IsIanaTimeZone("asia/shanghai")); Assert.True(Js.IsIanaTimeZone("America/New_York"));
        Assert.True(Js.IsIanaTimeZone("UTC")); Assert.True(Js.IsIanaTimeZone("+08:00")); Assert.True(Js.IsIanaTimeZone("-05:30"));
        Assert.True(Js.IsIanaTimeZone("+0800")); Assert.True(Js.IsIanaTimeZone("+08")); Assert.False(Js.IsIanaTimeZone("+08:00:00")); Assert.False(Js.IsIanaTimeZone("+8:00"));
    }
    [Fact] public void ZoneWall_matches_formatToParts_on_every_guaranteed_row()
    {   // guaranteed = UTC and fixed offsets for every year, every zone for 2026/9999. The other rows are the stated OS-zone-table deviation (LMT
        // before ~1900, pre-rule years for Chatham/Lord Howe) and are kept in the fixture for information only.
        var rows = Fx("time-zones.json")["wall"]!.AsArray();
        Assert.Contains(rows, r => !r!["guaranteed"]!.GetValue<bool>());   // the fixture still carries the deviation rows
        foreach (var e in rows.Where(r => r!["guaranteed"]!.GetValue<bool>()))
        {
            var p = e!["parts"]!; var w = Js.ZoneWall(e["zone"]!.GetValue<string>(), e["ms"]!.D());
            Assert.Equal((p["y"]!.GetValue<string>(), p["mo"]!.GetValue<string>(), p["d"]!.GetValue<string>(), p["h"]!.GetValue<string>(), p["mi"]!.GetValue<string>(), p["s"]!.GetValue<string>()),
                (w.Y, w.Mo, w.D, w.H, w.Mi, w.S));
        }
    }
    [Fact] public void ZoneWall_year_zero_in_utc_and_fixed_offset() { Assert.Equal("0", Js.ZoneWall("UTC", Js.DateParse("0000-01-01T00:00:00Z")).Y); Assert.Equal(("08", "00"), (Js.ZoneWall("+08:00", 0).H, Js.ZoneWall("+08:00", 0).Mi)); }
    [Fact] public void Civil_round_trip() { foreach (var z in new long[] { -719468, -1, 0, 1, 19999, 2932896 }) { var (y, m, d) = Js.CivilFromDays(z); Assert.Equal(z, Js.DaysFromCivil(y, m, d)); } }
}
```
`Js.CivilFromDays`/`DaysFromCivil` are `internal`; the csproj `InternalsVisibleTo` item in this task's Files list makes them visible to the test project (verified absent today — no phase-1 test reaches an internal member).

- [ ] **Step 2: Run** → compile failure.
- [ ] **Step 3: Implement `JsTime.cs`.** `DateParse`: one `Regex` built from the schema pattern with capture groups, `RegexOptions.ECMAScript | CultureInvariant`, `$` → `\z`; then the range checks listed in Interfaces; `ms = DaysFromCivil * 86_400_000 + h*3_600_000 + mi*60_000 + s*1000 + frac − offsetMs` (fraction `.5` = 500 ms, `.12` = 120 ms). Return `NaN` on any violation. `ZoneWall` per Interfaces, `FloorDiv` for negative epochs.
- [ ] **Step 4: Run** `dotnet test --filter JsTimeTests` → pass. A `ZoneWall` mismatch on a guaranteed row means `FindSystemTimeZoneById` resolved a different zone or the civil arithmetic is off — print the resolved `Id`/`StandardName` and the computed `days` before touching anything else. The non-guaranteed rows (every zone × years 0000/0001/0099 = LMT; `Pacific/Chatham` and `Australia/Lord_Howe` × 1969/1970 = extrapolated OS rule) are expected to differ; do not add them to the assertion and do not special-case them in code.
- [ ] **Step 5: Commit** — `git add src/Archify.Net.Core/Js/JsTime.cs src/Archify.Net.Core/Archify.Net.Core.csproj tests/Archify.Net.Core.Tests/JsTimeTests.cs` → `feat(core): Js.DateParse, V8-equivalent time-zone id acceptance and formatToParts wall clock (ICU fixtures; OS zone table deviation stated)`.

---

### Task 4: `erd/grid.mjs` → `Erd/Grid.cs` (`ErdGrid`)

**Files:**
- Create: `src/Archify.Net.Core/Erd/Grid.cs`, `tests/Archify.Net.Core.Tests/ErdGridTests.cs`

**Interfaces:**
- Produces (namespace `Archify.Net`; class named `ErdGrid` because `Grid` is the architecture grid):
  - `sealed record ErGrid(string Mode = "grid", double OriginX = 32, double OriginY = 40, double GapX = 56, double GapY = 44, double EntityW = 240, double HeaderH = 30, double RowH = 20)`; `static ErGrid ErdGrid.DefaultErGrid` (= `DEFAULT_ER_GRID`, grid.mjs:5-22); `static ErGrid ErdGrid.Merge(ErGrid defaults, JsonObject raw)` (= `{ ...DEFAULT_ER_GRID, ...raw }`: each present numeric key overrides; `origin` is a 2-array).
  - `sealed record GridEntity(string? Id, JsonArray? Pos, double? Row, double? Col, double? Width, double Height)` + `static GridEntity From(JsonObject source, double height, double? width = null)` — `Row`/`Col`/`Width` read with `J.Num` (absent → `null`, which plays the role of `undefined` in `Number.isInteger`/`Number.isFinite` checks); `Width` is the **measured** width the caller passes (render-class.mjs:231-233 spreads `width` over the type before `bandedLayout`), falling back to `source.width`.
  - `sealed record Bands(JsMap<double, double> ColumnX, JsMap<double, double> RowY, JsMap<double, double> Widths, JsMap<double, double> Heights, double TotalWidth, double TotalHeight)`.
  - `ErGrid? ErGridLayout(JsonObject er)` (grid.mjs:24-34), `double ResolvedEntityWidth(GridEntity entity, ErGrid? grid)` (:40-42), `double EntityHeight(JsonObject entity, ErGrid grid)` (:46-49; `attributes` must be a `JsonArray` to count, else 0 rows → `Math.max(1, …)`), `Bands BandedLayout(IReadOnlyList<GridEntity> entities, ErGrid grid, JsMap<double, double>? columnGaps = null)` (:61-100 — `gridColumn(col)` = `Number.isInteger(col * 2) && col >= 0`; `Math.max(-1, ...heights.keys())` = `-1` when empty; `columnGaps?.get(col) || 0`), `Pt ResolveEntityPos(GridEntity entity, ErGrid? grid, Bands? bands)` (:102-120; returns `new Pt(double.NaN, double.NaN)` where upstream returns `[NaN, NaN]`; an authored `pos` of length 2 wins unconditionally), `void ValidateErGridPlacement(JsonObject er, ErGrid? grid, Bands? bands, List<string> problems)` (:122-150; messages verbatim, numbers through `Js.Num`).

- [ ] **Step 1: Failing tests** (`ErdGridTests.cs`)

```csharp
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ErdGridTests
{
    static readonly JsonObject Fx = (JsonObject)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/erd-grid.json")))!;
    static readonly ErGrid ClassGrid = new(OriginX: 32, OriginY: 40, GapX: 128, GapY: 84, EntityW: 180);
    static GridEntity Ent(JsonNode n) => GridEntity.From(n.AsObject(), n["height"]?.D() ?? 0);
    static string Map(JsMap<double, double> m) => Js.Stringify(new JsonArray(m.Entries.Select(e => (JsonNode)new JsonArray(e.Key, e.Value)).ToArray()));
    static string PtJson(Pt p) => Js.Stringify(new JsonArray(double.IsNaN(p.X) ? "NaN" : p.X, double.IsNaN(p.Y) ? "NaN" : p.Y));

    [Fact] public void Defaults_match_upstream()
    {
        var d = Fx["defaults"]!; var g = ErdGrid.DefaultErGrid;
        Assert.Equal((d["gapX"]!.D(), d["gapY"]!.D(), d["entityW"]!.D(), d["headerH"]!.D(), d["rowH"]!.D()), (g.GapX, g.GapY, g.EntityW, g.HeaderH, g.RowH));
    }
    [Fact] public void ErGridLayout_cases()
    {
        foreach (var c in Fx["erGridLayout"]!.AsArray())
        {
            var grid = ErdGrid.ErGridLayout(c!["er"]!.AsObject());
            if (c["grid"] is null) Assert.Null(grid); else { Assert.NotNull(grid); Assert.Equal(c["grid"]!["gapX"]!.D(), grid!.GapX); }
        }
    }
    [Fact] public void EntityHeight_cases() { foreach (var c in Fx["entityHeight"]!.AsArray()) Assert.Equal(c!["height"]!.D(), ErdGrid.EntityHeight(c["entity"]!.AsObject(), ErdGrid.DefaultErGrid)); }
    [Fact] public void BandedLayout_and_ResolveEntityPos_match_upstream()
    {
        foreach (var c in Fx["banded"]!.AsArray())
        {
            var entities = c!["entities"]!.AsArray().Select(e => Ent(e!)).ToList();
            JsMap<double, double>? gaps = null;
            if (c["columnGaps"] is JsonArray ga) { gaps = new(); foreach (var p in ga) gaps.Set(p![0]!.D(), p[1]!.D()); }
            var b = ErdGrid.BandedLayout(entities, ClassGrid, gaps);
            var e = c["bands"]!;
            Assert.Equal(Js.Stringify(e["columnX"]), Map(b.ColumnX)); Assert.Equal(Js.Stringify(e["rowY"]), Map(b.RowY));
            Assert.Equal(Js.Stringify(e["widths"]), Map(b.Widths)); Assert.Equal(Js.Stringify(e["heights"]), Map(b.Heights));
            Assert.Equal((e["totalWidth"]!.D(), e["totalHeight"]!.D()), (b.TotalWidth, b.TotalHeight));
            var positions = c["positions"]!.AsArray();
            for (var i = 0; i < entities.Count; i++) Assert.Equal(Js.Stringify(positions[i]), PtJson(ErdGrid.ResolveEntityPos(entities[i], ClassGrid, b)));
        }
    }
    [Fact] public void ValidateErGridPlacement_messages_match_upstream()
    {
        var p = Fx["placement"]!;
        var er = new JsonObject { ["entities"] = new JsonArray(
            new JsonObject { ["id"] = "a", ["row"] = 0, ["col"] = 0 }, new JsonObject { ["id"] = "b", ["row"] = 0, ["col"] = 0 }, new JsonObject { ["id"] = "c", ["row"] = -1, ["col"] = 0 },
            new JsonObject { ["id"] = "d" }, new JsonObject { ["id"] = "e", ["row"] = 1.5, ["col"] = 0 }, new JsonObject { ["id"] = "f", ["pos"] = new JsonArray(1, 2) }) };
        er = DiagramLoader.Normalize(er);
        var grid = ErdGrid.ErGridLayout(er)!;
        var bands = ErdGrid.BandedLayout(er["entities"]!.AsArray().Select(e => GridEntity.From(e!.AsObject(), 50)).ToList(), grid);
        var problems = new List<string>(); ErdGrid.ValidateErGridPlacement(er, grid, bands, problems);
        Assert.Equal(p["withGrid"]!.AsArray().Select(x => x!.GetValue<string>()), problems);
        var noGrid = new List<string>(); ErdGrid.ValidateErGridPlacement(new JsonObject { ["entities"] = new JsonArray(new JsonObject { ["id"] = "z" }) }, null, null, noGrid);
        Assert.Equal(p["noGrid"]!.AsArray().Select(x => x!.GetValue<string>()), noGrid);
    }
}
```

- [ ] **Step 2: Run** → compile failure. **Step 3: Port** grid.mjs line-for-line (JS `Map` → `JsMap<double,double>`; the `for (const [col, column] of columnX) columnX.set(col, column + leftInset)` loop updates in place — `JsMap.Set` on an existing key keeps position, matching `Map.set`). **Step 4: Run** → pass. **Step 5: Commit** — `git add src/Archify.Net.Core/Erd/Grid.cs tests/Archify.Net.Core.Tests/ErdGridTests.cs` → `feat(core): erd banded grid (shared by class and erd) — fixture-exact`.

- [ ] **Step 6: Port the nine `golden.mjs` schema-failure cases** (spec Testing §2; `UP/test/golden.mjs:182-199` — nine `expectFailure` calls for tree, class, timeline and waterfall; phase 1 ported only the sequence `schema_version` case). No new code — the validator exists — so this is a theory appended to `tests/Archify.Net.Core.Tests/ValidationTests.cs`, reusing its `Example`/`Fails` helpers; assert the `schema/<keyword>` code **and** the instance path golden.mjs names:

```csharp
    public static IEnumerable<object[]> GoldenSchemaCases() =>
    [   // golden.mjs line, type, example, mutation name, expected code, expected instance path
        ["182 tree node without a label", "tree", "payment-platform.tree.json", "schema/required", "/nodes/1"],
        ["184 unknown tree direction", "tree", "payment-platform.tree.json", "schema/enum", "/layout/direction"],
        ["186 type without a kind", "class", "payments.class.json", "schema/required", "/types/0"],
        ["188 unknown relationship kind", "class", "payments.class.json", "schema/enum", "/relationships/0/kind"],
        ["190 unknown member visibility", "class", "payments.class.json", "schema/enum", "/types/0/methods/0/visibility"],
        ["192 timeline timestamp without an offset", "timeline", "payment-incident.timeline.json", "schema/pattern", "/events/0/at"],
        ["194 timeline without evidence status", "timeline", "payment-incident.timeline.json", "schema/required", "/meta"],
        ["196 negative span start", "waterfall", "checkout-request.waterfall.json", "schema/minimum", "/spans/0/start"],
        ["198 waterfall without evidence status", "waterfall", "checkout-request.waterfall.json", "schema/required", "/meta"],
    ];

    [Theory, MemberData(nameof(GoldenSchemaCases))]
    public void Golden_schema_failure_cases(string name, string type, string file, string code, string path)
    {
        var d = Example(file);
        switch (name[..3])
        {
            case "182": d["nodes"]![1]!.AsObject().Remove("label"); break;
            case "184": d["layout"] = new JsonObject { ["direction"] = "up" }; break;
            case "186": d["types"]![0]!.AsObject().Remove("kind"); break;
            case "188": d["relationships"]![0]!["kind"] = "uses"; break;
            case "190": d["types"]![0]!["methods"]![0]!["visibility"] = "internal"; break;
            case "192": d["events"]![0]!["at"] = "2026-10-01T10:00:00"; break;
            case "194": case "198": d["meta"]!.AsObject().Remove("evidence"); break;
            case "196": d["spans"]![0]!["start"] = -1; break;
        }
        var ex = Fails(DiagramTypes.Parse(type), DiagramLoader.Normalize(d));
        Assert.Contains(ex.Diagnostics, x => x.Code == code && x.Path == path);
    }
```
Before running, confirm each expected keyword against the schema (`upstream/schemas/<type>.schema.json`): `minimum` vs `exclusiveMinimum` for `spans[].start`, and whether `types[0].methods[0]` exists in `payments.class.json` (pick the first type that has a method otherwise). The `(code, path)` pair is the contract; the mutation is only a way to reach it. Run `dotnet test --filter ValidationTests` → the nine new theories pass. Commit — `git add tests/Archify.Net.Core.Tests/ValidationTests.cs` → `test(core): port golden.mjs schema-failure cases for tree, class, timeline, waterfall`.

---

### Task 5: Sequence renderer — 4 goldens

**Files:**
- Create: `src/Archify.Net.Core/Sequence/SequenceRenderer.cs` ← `UP/renderers/sequence/render-sequence.mjs` (1105 lines)
- Modify: `src/Archify.Net.Core/Archify.cs` (`DiagramType.Sequence => new SequenceRenderer()`), `tests/Archify.Net.Core.Tests/ParityTests.cs`

**Interfaces:**
- Consumes: `Js.Graphemes`, `Js.WordBreaks`, `Js.HasHan`, `Js.SpaceTokens`, `Js.HanTokens` (Task 2); `Legend.*`, `Geometry.*`, `TextFit.*`, `BrandMarks.*`, `DesktopReadability.*`, `SvgAttrs.*`, `Utils.*` (phase 1).
- Produces: `public sealed class SequenceRenderer : IDiagramRenderer` returning `(svg, ctx.Diagram.Arr("cards"), null)`.

Porting rules specific to this file:
- Goldens: `upstream/examples/sequence-cache-miss-request.html` (the only one in `TARGETS` without a `-rendered` suffix), `tests/goldens/async-job-roundtrip-rendered.html`, `tests/goldens/note-wrap-rendered.html`, `tests/goldens/brand-participants-rendered.html`.
- Brand: `loadDiagramWithBrandMarks` (line 19) = the facade's `PrepareDiagramBrandMarks`. Call sites `brandTopRailProblem(participant, …)` (139, 582), `renderBrandMark(participant, { x, y })` (938), `brandLabelFitWidth(participant, …)` (939), `brandMetadataFor(participant)` (944) take the **resolved mark**. Line 139 runs inside `participantSpreadGeometry()` over the **authored** participants (`asArray(sequence.participants)`, :126) before the measured map exists (:350) — call `ctx.Brands.BrandMarkFor(authoredParticipant)` directly there. The measured participant record built at :350 carries `Brand = ctx.Brands.BrandMarkFor(authored)` for the other four sites. `renderBrandMark` for a node without a mark returns `''` upstream — port that short-circuit in the caller (`Brand is null ? "" : BrandMarks.RenderBrandMark(...)`).
- Quality profile: direct meta reads at lines 67 (`unitWidth`), 117 (`readableMessages`), 846 (segment border-run check), 919 (legend room) stay **meta-only**. Gate arguments `profile: sequence.meta?.quality_profile` at 682 (`cleanCrossingProblems`), 691 (`cleanAmbiguousCorridorProblems`), 701 (`cleanBorderRunProblems`), 710 (`cleanRouteRhythmProblems`), 752 (`cleanLabelRouteClearanceProblems`), 760 (`cleanLabelCanvasContainmentProblems`) become `gateProfile` (option ?? meta), exactly as `ArchitectureRenderer.cs:946-985` does.
- Note wrapping (lines 178-248): `NOTE_BREAK_AFTER` set; `wrapNoteText` → `Js.SpaceTokens(text)` for the original pass; `if (!Js.HasHan(text)) return original;` then the Han pass with `Js.HanTokens`, `Js.WordBreaks(token)` for Han tokens, the closing-punctuation merge `^[、。，；：！？）】》」』]+$`, and `compact.Count < original.Count ? compact : original`. `wrapNoteWords`: `/^\s+$/u.test(word)` = every rune `Js.IsSpace`; `line.trimEnd()` = `Js.TrimEnd` (Task 2, fixture-pinned); the per-grapheme loop `for (const { segment } of noteSegmenter.segment(word))` = `foreach (var segment in Js.Graphemes(word))`; `noteBreakIndex(piece)` indexes **UTF-16 units** (`piece[index - 1]`) — keep `string` indexing, not runes.
- Sorts: 484 (`a - b` numbers), 612 (unknown-endpoint suggestions), 724 (`a.y - b.y`), 818 (`a.message.y - b.message.y`) → `Js.Sort`.
- `Math.round` → `Js.Round` (9 sites incl. `toFixed`); every `${number}` → `Js.Num`; `message.label ?? null` in subjects → `JsonValue`/null literally.
- Diagnostics: codes `sequence/unknown-endpoint`, `sequence/self-message-unsupported`, `sequence/segment-message-border-run`, `sequence/segment-label-overlap`, `sequence/note-overlap`, `sequence/note-canvas-limit`; the final `throwDiagnosticProblems('Sequence layout validation failed', problems, { subject, diagnostics })` (925) uses the default code `layout/constraint`.
- `validateSequence()` runs before `renderSvg()` (1096-1102): `Render` = `scene.ValidateSequence(); return (scene.RenderSvg(), cards, null);`.

- [ ] **Step 1: Parity rows + a diagnostic fact** (append to `ParityTests.Targets()` and the class):

```csharp
        ["sequence", Fixtures.Upstream("examples/cache-miss-request.sequence.json"), Fixtures.Upstream("examples/sequence-cache-miss-request.html")],
        ["sequence", Fixtures.Upstream("examples/async-job-roundtrip.sequence.json"), Fixtures.Golden("async-job-roundtrip-rendered.html")],
        ["sequence", Fixtures.Fixture("diagrams/note-wrap.sequence.json"), Fixtures.Golden("note-wrap-rendered.html")],
        ["sequence", Fixtures.Fixture("diagrams/brand-participants.sequence.json"), Fixtures.Golden("brand-participants-rendered.html")],
```
```csharp
    [Fact] public void Sequence_unknown_endpoint_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/cache-miss-request.sequence.json")));
        d["messages"]![0]!["to"] = "nobody";
        var r = ArchifyNet.Render(DiagramType.Sequence, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "sequence/unknown-endpoint" && x.Path == "/messages/0/to");
    }
```

- [ ] **Step 2: Run** `dotnet test --filter ParityTests` → the four sequence rows fail with `NotSupportedException` (expected). **Step 3: Port** per the rules above; register in `Renderers.For`. **Step 4: Iterate** on `FirstDifference` until the four rows pass; then the full suite. A difference inside a `<tspan>` of the note-wrap golden is a `Js.Graphemes`/tokenizer issue — fix in `JsText.cs` and re-run `JsTextTests`. **Step 5: Commit** — `git add src/Archify.Net.Core/Sequence src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): sequence renderer — four goldens byte-identical (Latin note wrap, brand rails)`.

---

### Task 6: Lifecycle renderer — 3 goldens

**Files:**
- Create: `src/Archify.Net.Core/Lifecycle/LifecycleRenderer.cs` ← `UP/renderers/lifecycle/render-lifecycle.mjs` (1134 lines)
- Modify: `Archify.cs` (`DiagramType.Lifecycle => new LifecycleRenderer()`), `ParityTests.cs`

**Interfaces:**
- Consumes: `TextFit.NodeLabelLayout`, `Utils.SemanticSigilFootprint`/`SourceBadgeFootprint`, `Geometry.SegmentRectClearance`/`RoundedPath`/`RoutePointsValue`/`ArrowClassMap`/`EdgeLabelAccent`, `BrandMarks.*`, `Legend.*`.
- Produces: `LifecycleRenderer : IDiagramRenderer` returning `(svg, cards, null)`.

Porting rules:
- Goldens: `upstream/examples/lifecycle-agent-run.html`, `tests/goldens/deployment-release-rendered.html`, `tests/goldens/deployment-release.notes-rendered.html`.
- No `--layout-json`, no `quality_profile` read anywhere in the file (verified by grep) → `gateProfile` is not needed; `SvgAttrs.SvgRootAttrs(ctx)` alone carries the option.
- Brand: `loadDiagramWithBrandMarks` (19); `states = new Map(states.map(s => [s.id, { ...s }]))` (59) — the copy must carry `Brand = ctx.Brands.BrandMarkFor(authoredState)`; call sites `brandLabelFitWidth(state, …)` (69), `brandMarkFor(state)` (80, 904, 1001 → `state.Brand is not null`), `brandTopRailProblem(state, …)` (899), `renderBrandMark(state, …)` (1013), `brandMetadataFor(state)` (1025).
- `sourceEvidence?.nodes?.[state.id]?.length` (80, 1001) → `ctx.SourceEvidence` is `null` in this phase: the expression is `false`/`0`; port it as a helper `SourceBadgeFor(id)` that reads `ctx.SourceEvidence?["nodes"]?[id]` so phase 4 needs no edit here.
- Sorts: 176, 215, 220, 299, 559, 615, 633, 644, 697, 742-746, 931 → `Js.Sort` (`a - b || c - d` chains: compute the first delta, fall through when it is `0`, as JS `||` on numbers does — note `-0 || x` is `x`); 308 `[from, to].sort()` → `Js.SortDefault`.
- `focusEdgeAttrs(..., transition.label || transition.note, index, transition.id)` (1048, 1065): JS `||` on strings — empty label falls through to the note; `null` when both are absent/empty.
- `exitSignature` joins with `'\u0000'` (164) — keep the NUL separator in the `JsMap<string,…>` key.
- Diagnostics: `lifecycle/unknown-main-path-state`, `lifecycle/unknown-endpoint`, `lifecycle/too-wide`, `lifecycle/state-text-too-long`, `lifecycle/repeated-main-path-state`, `lifecycle/main-path-gap`, `lifecycle/label-unplaced`, `lifecycle/duplicate-state`, `lifecycle/crowded-side-transitions`.

- [ ] **Step 1: Parity rows + fact** (golden.mjs:151-153 case)

```csharp
        ["lifecycle", Fixtures.Upstream("examples/agent-run.lifecycle.json"), Fixtures.Upstream("examples/lifecycle-agent-run.html")],
        ["lifecycle", Fixtures.Upstream("examples/deployment-release.lifecycle.json"), Fixtures.Golden("deployment-release-rendered.html")],
        ["lifecycle", Fixtures.Fixture("diagrams/deployment-release.notes.lifecycle.json"), Fixtures.Golden("deployment-release.notes-rendered.html")],
```
```csharp
    [Fact] public void Lifecycle_main_path_gap_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/agent-run.lifecycle.json")));
        var t = d["transitions"]!.AsArray(); var gap = t.First(x => x!.Str("from") == "queued" && x.Str("to") == "planning"); t.Remove(gap);
        var r = ArchifyNet.Render(DiagramType.Lifecycle, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "lifecycle/main-path-gap" && x.Path == "/mainPath/0");
    }
```
- [ ] **Step 2: Run** → three rows fail. **Step 3: Port**; register. **Step 4: Iterate** to green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Lifecycle/LifecycleRenderer.cs src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): lifecycle renderer — three goldens byte-identical (notes, brand-aware states)`.

---

### Task 7: Class renderer — 2 goldens

**Files:**
- Create: `src/Archify.Net.Core/Class/ClassRenderer.cs` ← `UP/renderers/class/render-class.mjs` (1095 lines)
- Modify: `Archify.cs` (`DiagramType.Class => new ClassRenderer()`), `ParityTests.cs`

**Interfaces:**
- Consumes: `ErdGrid.BandedLayout`/`ResolveEntityPos`/`GridEntity` (Task 4); `Routing.CreateRouter` + `Router.PathFor`/`ConnectionEndpointSide` (phase 1); `AutomaticLabels.PlaceAutomaticLabels`; `LayoutReport.ConnectionPath` (= `connectionPath as relationshipPath`, line 46); `Legend.RelationshipLegendObstacles`; the geometry imports at lines 25-43.
- Produces: `ClassRenderer : IDiagramRenderer` returning `(svg, cards, new LayoutReport(scene.BuildLayoutReport()))`.

Porting rules:
- Goldens: `upstream/examples/class-payments-rendered.html`, `upstream/examples/class-payment-processors-rendered.html` (both use half columns `col: 1.5` / `2.5`, so `ErdGrid`'s half-column branch is exercised end to end).
- Grid: `grid = { origin, gapX, gapY, typeW, typeMaxW, ...(cd.layout || {}) }` (62-69); `bandedLayout(measuredTypes, { ...grid, entityW: grid.typeW })` (234) → `ErdGrid.BandedLayout(measured.Select(t => GridEntity.From(t.Source, t.Height, t.Width)).ToList(), new ErGrid(OriginX: …, GapX: …, GapY: …, EntityW: grid.TypeW))`; `resolveEntityPos(type, …, bands)` (238) likewise; `bands.heights.get(type.row) ?? type.height` (239) → `Bands.Heights.Has(row) ? Get(row) : height`.
- `createRouter` takes `Map<id, measured type>` — build `JsMap<string, Box>` from the measured types (`Box.Source` = authored type, `X/Y/Width/Height` measured), as architecture does.
- Bus routing (lines ~460-524): `busPaths`/`busGroups` are `Map`s keyed by the **relationship object** — `JsMap<JsonObject, List<Pt>>` / `JsMap<JsonObject, List<JsonObject>>` keyed by the authored `JsonObject` reference. Lines 516-517 only delete (no re-add, and nothing iterates these maps), so the order that matters is `rejected` (:496, :514), a `Set` of group arrays iterated in insertion order — `JsSet<List<JsonObject>>` keyed by reference.
- Text (`fillLines`/`wrapMember` :149-188, `memberColumns` :881-889): `text.split(/(?<=\(|, )|(?=\): )/)` → `Js.SplitSignature`; `.split(/(?<=, )/)` → `Js.SplitAfterCommaSpace`; `String(member.parameters).split(/(,\s*)/)` → `Js.SplitKeepingCommas` with `/^,\s*$/.test(part)` → `Js.IsCommaPart`; `part.split(':')` is an ordinal `string.Split(':')`; `trimEnd`/`trimStart` → `Js.TrimEnd`/`Js.TrimStart`; `Array.from(current)` → runes with `textUnits` per rune string (all Task 2, fixture-pinned — the lookbehind patterns do not compile under `RegexOptions.ECMAScript`, which is why they live in `Js`).
- `Math.hypot` → `Js.Hypot` at :476 (bus conflict), :554 (label segment choice — reaches label coordinates), :731 (diagnostic text). `Math.round`: 6 sites → `Js.Round`; the file has no `toFixed`.
- Literal tables iterated with `Object.keys/entries/values` (`NOTATION` :290, `GLYPH` :317, `KIND_TONE` :1037) → ordered arrays in source order, never a `Dictionary`.
- Quality profile: the single `profile: cd.meta?.quality_profile` (777) feeds `cleanEndpointSideProblems` (779 — ignores it), `cleanCrossingProblems` (794), `cleanAmbiguousCorridorProblems` (795), `cleanRouteRhythmProblems` (796), `cleanLabelRouteClearanceProblems` (810) → pass `gateProfile` to the five; no other profile read exists in the file.
- `layoutJsonMode` (49-50): always build the report; `validateClassDiagram()` runs first (1082), so a failing diagram returns no report.
- `focusEdgeAttrs(from, to, relationship.label, index, relationship.id)` (988, 995): key = `Js.Num(index)`.
- Sorts: none with comparators besides geometry internals (verified: no `.sort(` in render-class.mjs).
- Stereotype glyphs `«abstract»` etc. (96-101) are literal non-ASCII — keep the exact code points; `VISIBILITY_GLYPH` (103).
- Diagnostics: `layout/constraint` (813), `layout/marker-capacity`, `class/title-text-capacity`, `class/realization-target`, `class/inheritance-kind`, `class/inheritance-cycle`.

- [ ] **Step 1: Parity rows + fact** (golden.mjs:186-191 are schema cases, ported as `ValidationTests` theories in Task 4 Step 6; here pin a renderer-level code — the reviewer reproduced `class/inheritance-cycle @ /relationships` with exactly this mutation)

```csharp
        ["class", Fixtures.Upstream("examples/payments.class.json"), Fixtures.Upstream("examples/class-payments-rendered.html")],
        ["class", Fixtures.Upstream("examples/payment-processors.class.json"), Fixtures.Upstream("examples/class-payment-processors-rendered.html")],
```
```csharp
    [Fact] public void Class_inheritance_cycle_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-processors.class.json")));
        var rel = d["relationships"]!.AsArray().First(x => x!.Str("kind") == "inheritance")!.AsObject();
        d["relationships"]!.AsArray().Add(new JsonObject { ["from"] = rel["to"]!.DeepClone(), ["to"] = rel["from"]!.DeepClone(), ["kind"] = "inheritance" });
        var r = ArchifyNet.Render(DiagramType.Class, DiagramLoader.Normalize(d));
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "class/inheritance-cycle");
    }
```
(Read render-class.mjs's `class/inheritance-cycle` branch before trusting the mutation: if the renderer reports `class/inheritance-kind` first for a type that now has two parents, flip the test to that code — the point is one pinned renderer-level code, not a specific mutation.)

- [ ] **Step 2: Run** → two rows fail. **Step 3: Port**; register. **Step 4: Iterate** to green; full suite. **Step 5: Commit** — `git add src/Archify.Net.Core/Class/ClassRenderer.cs src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): class renderer — two goldens byte-identical (banded grid, bus routing, layout report)`.

---

### Task 8: Tree renderer — 3 goldens

**Files:**
- Create: `src/Archify.Net.Core/Tree/TreeRenderer.cs` ← `UP/renderers/tree/render-tree.mjs` (573 lines)
- Modify: `Archify.cs` (`DiagramType.Tree => new TreeRenderer()`), `ParityTests.cs`

**Interfaces:**
- Consumes: `Geometry.RoundedPath`/`RoutePointsValue`/`AsArray`, `NodeTextFit.WidthFactor`, `SvgAttrs.FocusEdgeAttrs(from, to, null, key, null)`.
- Produces: `TreeRenderer : IDiagramRenderer` returning `(svg, cards, new LayoutReport(report))`.

Porting rules:
- Goldens: `upstream/examples/tree-payment-platform-rendered.html`, `upstream/examples/tree-archify-repository-rendered.html` (has one `collapsed: true` node), `tests/goldens/payment-platform.right-rendered.html` (`layout.direction: "right"`, line 29-30).
- No profile reads, no brand. `focusEdgeAttrs(edge.from, edge.to, undefined, edge.index)` (494) → label `null`, key `Js.Num(edge.index)`, id `null` — this is carried item (d): `FocusEdgeAttrs` with an undefined label emits no `data-edge-label` (`SvgAttrs.cs:83` already handles null/empty).
- `[...cycle].sort().join('|')` (98) → `Js.SortDefault`.
- Text — byte-exact, **not** CJK-exempt (no `Intl` in this file): `wrapText` (:123-145) tokenizes with `String(text).match(/[⺀-鿿가-힯豈-﫿＀-￯]|[^\s…]+\s*|\s+/g) || [text]` → `Js.CjkWrapTokens(text)` (Task 2; one token per CJK/Hangul/fullwidth **code unit**, no `u` flag); `.trimEnd()`/`.trim()`/`.trimStart()` (:130-136) → `Js.TrimEnd`/`Js.Trim`/`Js.TrimStart`; `Array.from(current)` (:137) → runes with `textUnits` per rune string; `String(node.label).split(/\s+/)` (:201) → `Js.SplitOnSpaces` (empty strings at the ends included — `Math.max(...)` over `textUnits("") * advance = 0` is harmless but must be present). Tree has exactly one `split`; the capture-group split belongs to timeline.
- Diagnostics: `tree/structure` (the `throwDiagnosticProblems` code), `tree/root-count`, `tree/missing-parent`, `tree/duplicate-id`, `tree/cycle`, `layout/constraint`.

- [ ] **Step 1: Parity rows + fact**

```csharp
        ["tree", Fixtures.Upstream("examples/payment-platform.tree.json"), Fixtures.Upstream("examples/tree-payment-platform-rendered.html")],
        ["tree", Fixtures.Upstream("examples/archify-repository.tree.json"), Fixtures.Upstream("examples/tree-archify-repository-rendered.html")],
        ["tree", Fixtures.Fixture("diagrams/payment-platform.right.tree.json"), Fixtures.Golden("payment-platform.right-rendered.html")],
```
```csharp
    [Fact] public void Tree_missing_parent_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-platform.tree.json")));
        d["nodes"]![1]!["parent"] = "ghost";
        var r = ArchifyNet.Render(DiagramType.Tree, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "tree/missing-parent" && x.Path == "/nodes/1/parent");
    }
```
- [ ] **Step 2: Run** → three rows fail. **Step 3: Port**; register. **Step 4: Iterate** to green; `r.Layout!.Report["direction"]` equals `"right"` for the fixture (add that assertion to the fact above or a sibling). **Step 5: Commit** — `git add src/Archify.Net.Core/Tree/TreeRenderer.cs src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): tree renderer — three goldens byte-identical (down and right, collapsed branch)`.

---

### Task 9: Timeline renderer — 4 goldens, DST and zh-CN fixtures

**Files:**
- Create: `src/Archify.Net.Core/Timeline/TimelineRenderer.cs` ← `UP/renderers/timeline/render-timeline.mjs` (559 lines)
- Modify: `Archify.cs` (`DiagramType.Timeline => new TimelineRenderer()`), `ParityTests.cs`

**Interfaces:**
- Consumes: `Js.DateParse`, `Js.IsIanaTimeZone`, `Js.ZoneWall` (Task 3); `Legend.ResolveLegend`/`LegendFootprint`/`MeasureLegend`/`RenderLegend`; `Utils.TextUnits`; `NodeTextFit.WidthFactor`.
- Produces: `TimelineRenderer : IDiagramRenderer` returning `(svg, cards, new LayoutReport(report))`.

Porting rules:
- Goldens: `upstream/examples/timeline-payment-incident-rendered.html`, `upstream/examples/timeline-archify-dev-activity-rendered.html` (both `Asia/Shanghai`; the second has 20 events and disclosed breaks), `tests/goldens/dst-new-york-rendered.html`, `tests/goldens/payment-incident.zh-CN-rendered.html` (the spec's zh-CN fixture: bundled-catalog legend labels, evidence chip, `timeline.axis`, `timeline.compressed`; `DiagramLoader.Load` registers the locale; nothing on this path is segmenter-dependent, so it is byte-exact).
- Input checks (61-110): `validCalendarDate` (67-72) literally (`timestamp.slice(0,10).split('-').map(Number)` → `Js.ToNumber`); `new Intl.DateTimeFormat('en-US', { timeZone })` try/catch (81-86) → `if (!Js.IsIanaTimeZone(timezone)) fail("timeline/invalid-timezone", …)`; `Number.isFinite(Date.parse(event.at))` → `double.IsFinite(Js.DateParse(at))`; `timezone = tl.meta.timezone || 'UTC'` (73) is JS `||` (empty string → `UTC`; the schema's `minLength: 1` makes that unreachable for valid input, port it anyway). Final `throwDiagnosticProblems('Timeline validation failed', problems, { code: 'timeline/input', subject: { diagramType: 'timeline' }, diagnostics: details })` (109).
- Wall clock: `wall(ms)` → `Js.ZoneWall(timezone, ms)`; `zoneOffset(ms)` (121-127) = `Js.DaysFromCivil(+w.Y, +w.Mo, +w.D) * 86_400_000 + h*3_600_000 + mi*60_000 + s*1000 − Math.Floor(ms / 1000) * 1000` (same arithmetic as upstream's `setUTCFullYear`/`setUTCHours` on a zeroed `Date`; `+w.y` on `"0"` or `"99"` is the literal year, as `setUTCFullYear` treats it); `offsetLabel` (128-133) with `padStart(2,'0')` on integers → `Js.Num(x).PadLeft(2, '0')`.
- `showSeconds` (144): `new Date(t).getUTCSeconds() !== 0 || getUTCMilliseconds() !== 0` = `FloorMod(t, 60_000) != 0`.
- Events: `{ ...event, index, t: Date.parse(event.at) }` sorted by `a.t - b.t || a.index - b.index` (136-138) → `Js.Sort`; `times = [...new Set(events.map(e => e.t))]` → `JsSet<double>`; gaps/breaks sorts (192, 197, 213) → `Js.Sort`; `segments.find(...) ?? segments[0]` (251).
- Ticks (255-273): `Math.ceil((segment.start + offset) / interval) * interval - offset` — plain double arithmetic; `TICKS.find(c => c * scale >= minTickPx) ?? TICKS.at(-1)`.
- `wrapTitle` (280-299): `.split(/(\s+)/)` → `Js.SplitKeepingSpaces` (Task 2, the only capture-group whitespace split in phase 2); `.trim()`/`.trimStart()` → `Js.Trim`/`Js.TrimStart` (Task 2); `Array.from(current)` → runes; `textUnits(chars[cut])` per rune string.
- Fixed-offset zones: `timezone === 'UTC' ? 'UTC' : \`${timezone} (${offsetLabel(first)})\`` (427) prints `+08:00 (UTC+08:00)` for `meta.timezone: "+08:00"` — `Js.ZoneWall` handles the offset id, nothing else changes.
- Literal tables: `KIND_TONE` is iterated with `Object.keys` (:366) and `Object.values` (:499) — an ordered array of `(kind, tone)` pairs in source order (`default, change, alert, action, recovery`), never a `Dictionary`.
- Legend: `resolveLegend(undefined, catalog, new Set(events.map(kindOf))).filter((entry, _, all) => all.length > 1)` (365-368) — the filter drops the legend entirely when only one kind is present.
- Render: `data-timeline-ms="${card.t}"` → `Js.Num(card.t)` (an integer ms value far below 1e21 — plain digits); `data-break-ms="${gap.length}"`; `${wall(first).y}-${wall(first).mo}-${wall(first).d}` header date; `timezone === 'UTC' ? 'UTC' : \`${timezone} (${offsetLabel(first)})\`` (427) keeps the authored spelling; `renderEvent` passport `{ kind: tone, sublabel: card.detail, context }` — omit `sublabel` when `detail` is absent (phase-1 `FocusNodeAttrs` rule: strings or omitted key); `i18nText(locale, 'timeline.compressed', { count })` → `ctx.Locale.Translate` with `Js.Num(count)` in the values map.
- Layout report (533-545): `scalePxPerMinute: Math.round(scale * MINUTE * 1000) / 1000` → `Js.Round`; arrays of `JsonObject`s in upstream key order.
- Diagnostics: `timeline/invalid-timezone`, `timeline/duplicate-lane-id`, `timeline/duplicate-id`, `timeline/invalid-timestamp`, `timeline/unknown-lane`, wrapper `timeline/input`.

- [ ] **Step 1: Parity rows + facts** (Review Focus 2 and 3; upstream test `timeline: historical years retain the actual display-zone UTC offset` for years `0000`–`2026` in UTC)

```csharp
        ["timeline", Fixtures.Upstream("examples/payment-incident.timeline.json"), Fixtures.Upstream("examples/timeline-payment-incident-rendered.html")],
        ["timeline", Fixtures.Upstream("examples/archify-dev-activity.timeline.json"), Fixtures.Upstream("examples/timeline-archify-dev-activity-rendered.html")],
        ["timeline", Fixtures.Fixture("diagrams/dst-new-york.timeline.json"), Fixtures.Golden("dst-new-york-rendered.html")],
        ["timeline", Fixtures.Fixture("diagrams/payment-incident.zh-CN.timeline.json"), Fixtures.Golden("payment-incident.zh-CN-rendered.html")],
```
```csharp
    [Fact] public void Timeline_windows_zone_id_is_rejected_like_V8()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-incident.timeline.json")));
        d["meta"]!["timezone"] = "China Standard Time";
        var r = ArchifyNet.Render(DiagramType.Timeline, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "timeline/invalid-timezone" && x.Path == "/meta/timezone");
    }
    [Theory] [InlineData("0000")] [InlineData("0001")] [InlineData("0099")] [InlineData("0100")] [InlineData("2026")]
    public void Timeline_historical_years_keep_utc_offset(string year)   // UP/test/timeline-rendering.test.mjs "historical years"
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-incident.timeline.json")));
        d["meta"]!["timezone"] = "UTC";
        d["events"] = new JsonArray(new JsonObject { ["id"] = "event", ["at"] = $"{year}-01-01T00:00:00Z", ["title"] = "Historical event", ["lane"] = "release" });
        var r = ArchifyNet.Render(DiagramType.Timeline, d);
        Assert.DoesNotContain(r.Diagnostics, x => x.Severity == Severity.Error);
        Assert.Contains("UTC+00:00", r.Svg);
        Assert.DoesNotMatch(@"UTC\+\d{3,}:", r.Svg);
    }
    [Fact] public void Timeline_dst_fixture_crosses_both_transitions()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/dst-new-york.timeline.json")));
        var r = ArchifyNet.Render(DiagramType.Timeline, d);
        Assert.Contains("America/New_York (UTC-05:00)", r.Svg);                      // header uses the first event's offset (EST)
        Assert.Contains("01:30:00 UTC-04:00", r.Svg); Assert.Contains("01:30:00 UTC-05:00", r.Svg);   // the repeated hour, both offsets
        Assert.True(r.Layout!.Report["breaks"]!.AsArray().Count >= 1);
    }
```
(The three `Assert.Contains` strings come from `renderHeader`/`renderEvent` formats at lines 427 and 451; if the golden shows a different spelling, align the assertion with the golden — the golden is the oracle.)

- [ ] **Step 2: Run** → four rows fail. **Step 3: Port**; register. **Step 4: Iterate** to green; full suite. A tick-position difference only in the DST golden is `zoneOffset` arithmetic (segment-start offset, line 267) — check `Js.ZoneWall` against the fixture row for that instant before touching the renderer. A difference only in the zh-CN golden is catalog resolution (`ctx.Locale.Translate` with the registered `zh-CN`), not timeline logic. **Step 5: Commit** — `git add src/Archify.Net.Core/Timeline/TimelineRenderer.cs src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/ParityTests.cs` → `feat(core): timeline renderer — four goldens byte-identical incl. America/New_York DST and zh-CN fixtures`.

---

### Task 10: Waterfall renderer — 3 goldens

**Files:**
- Create: `src/Archify.Net.Core/Waterfall/WaterfallRenderer.cs` ← `UP/renderers/waterfall/render-waterfall.mjs` (374 lines)
- Modify: `Archify.cs` (`DiagramType.Waterfall => new WaterfallRenderer()`), `ParityTests.cs`, `tests/Archify.Net.Core.Tests/JsTests.cs` (one fact)

**Interfaces:**
- Consumes: `Js.ToLocaleStringEnUs` (both option shapes), `Js.ToPrecision`, `Js.ToNumber`, `Js.Num`, `Js.Round`; `Legend.*`; `SvgAttrs.FocusEdgeAttrs(parent, id, null, key, null)`.
- Produces: `WaterfallRenderer : IDiagramRenderer` returning `(svg, cards, new LayoutReport(report))`; `internal static string WaterfallRenderer.Number(double value)` (the `number()` helper, exposed for its fixture test).

Porting rules:
- Goldens: `upstream/examples/waterfall-checkout-request-rendered.html`, `upstream/examples/waterfall-example-rebuild-rendered.html`, `tests/goldens/incomplete-trace-rendered.html`.
- `number(value)` (131-138) → `Number(value.toPrecision(3)).toString()` = `Js.Num(Js.ToNumber(Js.ToPrecision(value, 3)))`; the two `toLocaleString` shapes are exactly the ones `Js.ToLocaleStringEnUs` supports; `Number.MAX_VALUE / 100` = `double.MaxValue / 100`.
- `unit = wf.meta.unit || 'ms'` (30), `unitLabel = unit === 'us' ? 'µs' : unit` (32) — U+00B5 literal.
- Timing checks (54-105): `Number.isFinite(span.end)` on a possibly absent key → `J.Num` null-check + `double.IsFinite`; `span.end ?? null` in evidence; `[...cycle].sort().join('|')` (97) → `Js.SortDefault`; `[...list].sort(byStart)` in the row visitor (125, `a.start - b.start || a.index - b.index`) → `Js.Sort`; the `throwDiagnosticProblems('Waterfall validation failed', problems, { diagnostics: details })` at 170 passes **no** `code`/`subject` → defaults.
- `t0 = Math.min(...raw.map(s => s.start))` (109) — schema requires ≥1 span (`minItems`), so the empty-list Infinity case is unreachable; port with an explicit `Infinity` seed anyway.
- `tickStep` (176-184): `10 ** Math.floor(Math.log10(target))` → `Math.Pow(10, Math.Floor(Math.Log10(target)))` (Global Constraints: `log10`/`**` as-is); `NICE = [1, 2, 2.5, 5]`; `xOf` rounds to 2 decimals via `Js.Round(v * 100) / 100`.
- `focusEdgeAttrs(row.parent, row.id, undefined, row.index)` (271) → label `null`, key `Js.Num(row.index)`.
- `services` order of first appearance (152) → `JsSet<string>`; `serviceOf` (153-154) → `JsMap<string, string?>` — note `row.service ?? (parent ? serviceOf.get(parent) : undefined)` keeps an explicit `undefined` entry; `serviceTone(undefined)` → `'external'`.
- `data-waterfall-total="${wall}"` → `Js.Num(wall)`; `${number(t0)} → ${number(t1)} ${unitLabel}` (239) has a literal U+2192 arrow.
- Layout report (348-365): keys `unit`, `viewBox`, `wall`, `ticks`, … in upstream order.
- Diagnostics: `waterfall/duplicate-id`, `waterfall/invalid-timing` (two sites), `waterfall/missing-parent`, `waterfall/cycle`, wrapper `waterfall/input`.

- [ ] **Step 1: Parity rows + facts**

```csharp
        ["waterfall", Fixtures.Upstream("examples/checkout-request.waterfall.json"), Fixtures.Upstream("examples/waterfall-checkout-request-rendered.html")],
        ["waterfall", Fixtures.Upstream("examples/example-rebuild.waterfall.json"), Fixtures.Upstream("examples/waterfall-example-rebuild-rendered.html")],
        ["waterfall", Fixtures.Fixture("diagrams/incomplete-trace.waterfall.json"), Fixtures.Golden("incomplete-trace-rendered.html")],
```
```csharp
    [Fact] public void Waterfall_cycle_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/checkout-request.waterfall.json")));
        var spans = d["spans"]!.AsArray(); spans[0]!["parent"] = spans[1]!.Str("id");   // root now points at its own child
        var r = ArchifyNet.Render(DiagramType.Waterfall, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "waterfall/cycle");
    }
```
and in `JsTests.cs` (the helper is `internal`; Task 3's `InternalsVisibleTo` csproj item makes it visible):
```csharp
    [Fact] public void Waterfall_number_matches_V8()
    {
        foreach (var e in Load("waterfall-number.json")) Assert.Equal(e!["text"]!.GetValue<string>(), WaterfallRenderer.Number(e["value"]!.GetValue<double>()));
    }
```
- [ ] **Step 2: Run** → rows fail. **Step 3: Port**; register. **Step 4: Iterate** to green; full suite. A last-digit difference in a duration label is `Js.ToLocaleStringEnUs` — fix in `Js.cs`, re-run `JsTests`. **Step 5: Commit** — `git add src/Archify.Net.Core/Waterfall/WaterfallRenderer.cs src/Archify.Net.Core/Archify.cs tests/Archify.Net.Core.Tests/ParityTests.cs tests/Archify.Net.Core.Tests/JsTests.cs` → `feat(core): waterfall renderer — three goldens byte-identical (µs unit, incomplete and error spans)`.

---

### Task 11: Docs, handoff, review hand-off

**Files:**
- Modify: `README.md` (phase table + a "Known deviations from upstream" section; create the file if phase-1 Task 11 has not landed yet), `SESSION_HANDOFF.md`, `docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md` (status line), `src/Archify.Net.Core/Archify.cs` (XML doc on `Render`; the `NotSupportedException` message now names phases 3–4)

- [ ] **Step 1: Full suite** `dotnet test` → green, exit code 0 (unpiped). Record the counts: parity rows (phase 1: 6 + grid fixture; phase 2: 19), `JsTextTests`, `JsTimeTests`, `ErdGridTests`, the nine golden.mjs schema theories.
- [ ] **Step 2: Checker JSONs.** If `ArtifactCheckerTests` (phase-1 Task 10) exists, run it: every new golden must have `tests/goldens/checker/<name>.json` and the report must match. If it does not exist yet, state in the handoff that the 9 new checker JSONs are minted and waiting for that task.
- [ ] **Step 3: Deviations where a user reads them.** `README.md` section **"Known deviations from upstream"**, one entry each, next to the CJK exemption:
  - *CJK word breaks in sequence notes:* upstream segments Han runs with ICU's dictionary (`Intl.Segmenter` word granularity); the port breaks per grapheme. Only a sequence `note` containing Han is affected; every other text path (tree, class, timeline wrapping; zh-CN locale) is byte-exact.
  - *Time zones before the OS zone table's history:* conversions use the operating system's zone data, not Node's bundled tzdata. (a) Before a zone's first tzdata transition (local mean time, roughly pre-1900) the port shows the base offset — a `Asia/Shanghai` event at `0001-01-01T00:00:00Z` is labelled `UTC+08:06` by upstream and `UTC+08:00` by the port. (b) Before the OS table's first adjustment rule the rule is extrapolated backwards — `Pacific/Chatham`/`Australia/Lord_Howe` in 1970 read +13:45/+11:00 instead of +12:45/+10:00. UTC and fixed offsets are exact for every year; named zones are exact from the table's history onward (2026 transitions pinned by fixture).
  - *`Date.parse` outside the schema shape:* strings the timeline schema rejects (`2026-10-01 10:00:00Z`, offset-less timestamps) are `NaN` in the port; unreachable through `Render` because the schema gate runs first.
  Put the same three sentences (condensed) in the `/// <remarks>` of `ArchifyNet.Render` in `Archify.cs`.
- [ ] **Step 4: `SESSION_HANDOFF.md`** — phase-2 state: the 19 parity rows, the fixture set, the three deviations above (pointer to the README section), the `joinRoutePoints` memo deferral to phase 3 with the workflow-compiler line list, the note that `gateProfile` sites are review-checked only (no phase-2 fixture makes a shared gate fail, so a meta-only port would pass the same goldens — Tasks 5 and 7 list the sites for the reviewer), and the next-phase pointer (PORT-003 workflow + dataflow; PORT-004 erd reuses `ErdGrid`). No card lanes.
- [ ] **Step 5: Spec status line** → "Phase 2 implemented, commit <sha from `git log -1 --format=%h`>". README phase table row 2 → done.
- [ ] **Step 6: Self-review before the hand-off** (global CLAUDE.md rule): list, per renderer, the cases the port must handle — `||` vs `??` sites, empty collections (`Math.max(...[])`), absent optional keys (`detail`, `note`, `sublabel`, `service`, `parent`, `layout`), repeated ids, half columns, the DST repeated hour, a fixed-offset `meta.timezone`, `unit` absent — and point each at a golden, fixture or fact. A case with neither gets a test first.
- [ ] **Step 7: Commit** — `git add README.md SESSION_HANDOFF.md docs/superpowers/specs/2026-10-13-archify-csharp-port-design.md src/Archify.Net.Core/Archify.cs` → `docs: phase-2 handoff, known deviations; Renderers.For message names phases 3–4`. Then hand the phase-2 diff (`git diff 72398a3..HEAD -- src tests tools docs`) to the reviewer subagent the controller designates, with the brief: side-by-side each new `.cs` with its `.mjs`; flag any `ToString()`, `List.Sort`, `Dictionary`/`HashSet` enumeration, `string.Trim`, `string.Split` on authored text, or `DateTime` arithmetic in renderer code; confirm `upstream/` and the phase-1 goldens are untouched (`git log --stat -- tests/goldens upstream`); check each quality-profile site against the lists in Tasks 5 and 7; verify the three Han-related decisions are labelled parity-exempt and nothing else is. Settle findings per the global rules (reproduce before filing; a dismissal needs the test for the input the reviewer describes).

---

## Exit (matches card PORT-002, with one correction)

- **Upstream goldens byte-identical — 10, not 8.** `UP/scripts/render-examples.mjs:12-28` `TARGETS` has two rows each for tree, class, timeline and waterfall plus one each for sequence and lifecycle; the card's "8" undercounts by two. Files: `sequence-cache-miss-request.html`, `lifecycle-agent-run.html`, `tree-payment-platform-rendered.html`, `tree-archify-repository-rendered.html`, `class-payments-rendered.html`, `class-payment-processors-rendered.html`, `timeline-payment-incident-rendered.html`, `timeline-archify-dev-activity-rendered.html`, `waterfall-checkout-request-rendered.html`, `waterfall-example-rebuild-rendered.html`.
- **Node-minted example goldens byte-identical:** `async-job-roundtrip-rendered.html`, `deployment-release-rendered.html`.
- **Fixture goldens byte-identical:** `note-wrap`, `brand-participants`, `deployment-release.notes`, `payment-platform.right`, `dst-new-york` (America/New_York DST), `payment-incident.zh-CN` (the spec's zh-CN fixture), `incomplete-trace`. Total parity rows this phase: 10 + 2 + 7 = **19**.
- **Checker JSONs** minted for all 9 new goldens; equal to `ArchifyNet.Check` once phase-1 Task 10 lands.
- **`Js` fixtures green:** `segmenter.json` (graphemes exact for every row incl. Han; `WordBreaks` concatenation + per-grapheme on Han runs — the spec's CJK exemption), `text-wrap.json` (trims and the five tree/class/timeline splits exact), `dates.json` (every in-schema row), `time-zones.json` (acceptance battery exact incl. `"China Standard Time"` rejected and `+08:00` accepted; wall clock exact on every guaranteed row), `waterfall-number.json`, `erd-grid.json`.
- **Pinned facts green:** one renderer-level diagnostic code per type; the nine golden.mjs schema cases; the Windows-zone-id rejection; historical years in UTC; DST fixture assertions.
- **Deviations stated where users read them:** README "Known deviations from upstream" + `ArchifyNet.Render` remarks (CJK note breaks; OS zone table, two cases with the `UTC+08:06` example; out-of-schema `Date.parse`).
- **Review:** the reviewer subagent's pass over the phase-2 diff settled (findings reproduced or dismissed with a test), per the spec's review gate.
