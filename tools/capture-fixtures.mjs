#!/usr/bin/env node
// Dev-time only. Captures V8 behaviour and upstream renderer output as fixtures for the C# parity suite.
// Usage: node tools/capture-fixtures.mjs   (from repo root; needs C:\projects\archify at tag v3.2.0, commit 208acba8)
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import zlib from 'node:zlib';

const UP = 'C:/projects/archify/archify';
const ROOT = path.resolve(fileURLToPath(import.meta.url), '..', '..');
const out = (rel) => path.join(ROOT, 'tests', rel);
const write = (rel, value) => { fs.mkdirSync(path.dirname(out(rel)), { recursive: true }); fs.writeFileSync(out(rel), JSON.stringify(value, null, 2) + '\n'); };
const imp = (rel) => import(pathToFileURL(path.join(UP, rel)).href);
// Upstream's CLI builds the renderer environment explicitly (bin/archify.mjs rendererEnv); a stray ARCHIFY_* in the
// developer's shell would change a golden. Strip them for every spawn below.
const cleanEnv = { ...process.env };
for (const k of Object.keys(cleanEnv)) if (k.startsWith('ARCHIFY_')) delete cleanEnv[k];

// --- numbers: Number→string, Math.round, toFixed, toPrecision, en-US toLocaleString, hypot ---
const nums = [0, -0, 1, -1, 0.1, 0.5, 1.5, 2.5, -2.5, -0.001, -0.5, -0.004, 0.125, 2.345, 2.355, 999.95, 0.49999999999999994, 1.005,
  123.456, 1e21, 1e20, 2 ** 60, 999999999999999999999, 1e-7, 0.000001, 0.0000001, 0.000001234, 123456789.123, 5e-324,
  1.7976931348623157e308, 100, 999.5, 123456, 99.95, 33.333333333333336, 0.30000000000000004, 3.3, 7.1, 12.75, 0.1 + 0.2,
  1 / 3, 2 / 3, 1234.5678, 0.00012345, 98765.4321, 1234567.891, 1e6];
write('fixtures/js/numbers.json', nums.map((n) => ({
  value: Object.is(n, -0) ? '-0' : n,           // -0 cannot survive JSON; C# test reconstructs it
  str: String(n), round: Math.round(n), fixed2: n.toFixed(2), fixed6: n.toFixed(6), prec3: n.toPrecision(3),
  loc3sig: n.toLocaleString('en-US', { maximumSignificantDigits: 3 }),
  loc2frac: n.toLocaleString('en-US', { maximumFractionDigits: 2 }),
})));
const hypotPairs = [[3, 4], [168.75, 880.93], [0.1, 0.2], [1e-200, 1e-200], [1e200, 1e200], [0, 0], [5, 0], [12.345, 67.891], [1, 1], [100.5, 0.25]];
write('fixtures/js/hypot.json', hypotPairs.map(([a, b]) => ({ a, b, hypot: Math.hypot(a, b), str: String(Math.hypot(a, b)) })));
// JS Number(string) and Number.parseFloat(string) (check-render-output.mjs uses both)
const numStrings = ['12px', '', ' 42 ', '0x1f', 'Infinity', '-Infinity', '1e3', '.5', '5.', 'abc', '1_000', '\u00a012', '12\u2028', '+3', '-0', '1e', '0b101', '  -7.25e-1foo'];
write('fixtures/js/to-number.json', numStrings.map((s) => ({ s, number: String(Number(s)), parseFloat: String(Number.parseFloat(s)) })));

// --- sort: default (string) sort, stability, fractional comparators ---
const words = ['b', 'a', 'B', 'A', 'ä', 'z', '10', '9', '1', 'node-10', 'node-9', 'Zebra', 'apple', '', ' ', 'é', 'e'];
write('fixtures/js/sort.json', {
  words,
  defaultSorted: [...words].sort(),
  localeCompare: words.map((a) => words.map((b) => a.localeCompare(b))),
  stable: [{ k: 1, v: 'a' }, { k: 0, v: 'b' }, { k: 1, v: 'c' }, { k: 0, v: 'd' }, { k: 1, v: 'e' }].sort((x, y) => x.k - y.k).map((o) => o.v),
  // routing.mjs comparators return fractional deltas (e.g. 0.5); truncating them to int would make elements compare equal.
  fractional: [{ k: 1.25, v: 'a' }, { k: 0.75, v: 'b' }, { k: 1.0, v: 'c' }, { k: 0.5, v: 'd' }, { k: NaN, v: 'e' }].sort((x, y) => x.k - y.k).map((o) => o.v),
});

// --- stringify: escaping of control chars, <>&, non-ASCII, U+2028, nesting, indent. (Lone surrogates: C#-side literal test, JSON cannot carry them.) ---
const samples = ['plain', 'a"b\\c', '\u0000\u001f\u007f', '<script>&\'', 'é漢字😀', 'line\nbreak\ttab\r\b\f', 'ls\u2028ps\u2029',
  { b: 1, a: [0.1, null, true, false], nested: { z: 'ok', y: [], w: {} } }];
write('fixtures/js/stringify.json', samples.map((s) => ({ input: s, compact: JSON.stringify(s), indented: JSON.stringify(s, null, 2) })));

// --- strings: trim, \s, replace-first with $& ---
const ws = ['\ufeffx\ufeff', '\u0085x\u0085', ' x ', '\u00a0x\u00a0', '\u2028x', '\tx\n', '\u3000x'];
write('fixtures/js/strings.json', { trim: ws.map((s) => [s, s.trim()]), isSpaceRegex: ws.map((s) => [s, /^\s/.test(s)]),
  replaceFirst: ['a-b-c'.replace('-', () => '$&'), 'a-b-c'.replace('-', '$&$&')] });

// --- schema errors: ajv oracle (keyword, instancePath) sets for a battery of mutations, via upstream's generated validators ---
const validators = await imp('renderers/shared/generated-validators.mjs');
const EXD = path.join(UP, 'examples');
const load = (f) => JSON.parse(fs.readFileSync(path.join(EXD, f), 'utf8'));
const mutations = [
  ['workflow', 'agent-tool-call.workflow.json', 'extra-property', (d) => { d.nodes[0].colour = 'red'; }],
  ['sequence', 'cache-miss-request.sequence.json', 'missing-schema-version', (d) => { delete d.schema_version; }],
  ['workflow', 'agent-tool-call.workflow.json', 'id-digit', (d) => { d.nodes[0].id = '1user'; }],
  ['workflow', 'agent-tool-call.workflow.json', 'id-trailing-newline', (d) => { d.nodes[0].id = 'abc\n'; }],
  ['architecture', 'web-app.architecture.json', 'size-zero', (d) => { d.components[0].size = [0, 60]; }],
  ['architecture', 'web-app.architecture.json', 'size-three-items', (d) => { d.components[0].size = [10, 20, 30]; }],
  ['workflow', 'agent-tool-call.workflow.json', 'card-dot-enum', (d) => { d.cards[0].dot = 'purple'; }],
  ['architecture', 'web-app.architecture.json', 'brand-85-codepoints', (d) => { d.components[0].brand = '👨‍👩‍👧'.repeat(17); }],
  ['architecture', 'web-app.architecture.json', 'brand-object-bad', (d) => { d.components[0].brand = { url: 'not a url', sha256: 'xyz' }; }],
  ['architecture', 'web-app.architecture.json', 'translations-bad-key', (d) => { d.meta.locale = 'es'; d.meta.translations = { 'Bad.key': 'x' }; }],
  ['architecture', 'web-app.architecture.json', 'output-emoji', (d) => { d.meta.output = 'out/😀.html'; }],
  ['architecture', 'web-app.architecture.json', 'output-dotdot', (d) => { d.meta.output = '../x.html'; }],
  ['architecture', 'web-app.architecture.json', 'components-empty', (d) => { d.components = []; }],
  ['architecture', 'web-app.architecture.json', 'valid', () => {}],
];
write('fixtures/js/schema-errors.json', mutations.map(([type, file, name, mutate]) => {
  const d = load(file); mutate(d);
  const ok = validators[type](d);
  return { type, name, input: d, valid: ok, errors: ok ? [] : validators[type].errors.map((e) => [e.keyword, e.instancePath]).sort() };
}));

// --- text units ---
const { textUnits, renderSemanticSigil, renderDefinitions } = await imp('renderers/shared/utils.mjs');
const texts = ['', 'ascii', 'Ünïcödé', '漢字', 'ｶﾅ halfwidth', '한글', '✅ done', '⚡', 'e\ufe0f', '漢\ufe0e', '😀', '👨‍👩‍👧', '\ufe0f', 'a\u200db'];
write('fixtures/js/text-units.json', texts.map((t) => ({ text: t, units: textUnits(t) })));
write('fixtures/js/sigils.json', { defs: renderDefinitions(), defsExtra: renderDefinitions('<x/>'),
  sigils: ['frontend', 'backend', 'database', 'cloud', 'security', 'messagebus', 'external', 'start', 'decision', 'unknown'].map((k) =>
    ({ kind: k, svg: renderSemanticSigil(k, { x: 10.5, y: 3 }), iconClock: renderSemanticSigil(k, { x: 0, y: 0, icon: 'clock' }), none: renderSemanticSigil(k, { x: 0, y: 0, icon: 'none' }) })) });

// --- geometry: pure functions with representative inputs (signatures from geometry.mjs:28-84, 1351, 1818-1839) ---
const g = await imp('renderers/shared/geometry.mjs');
const tf = await imp('renderers/shared/text-fit.mjs');
const rects = [{ x: 0, y: 0, width: 100, height: 50 }, { x: 50, y: 25, width: 100, height: 50 }, { x: 200, y: 0, width: 10, height: 10 }];
// segment helpers take { start, end } objects (geometry.mjs:45-66), never [p1, p2] arrays
const segs = [[[0, 0], [300, 60]], [[10, 10], [10, 40]], [[-5, 25], [150, 25]], [[0, 0], [0, 0]], [[100, 0], [100, 50]]].map(([start, end]) => ({ start, end }));
const polylines = [[[0, 0], [100, 0], [100, 100]], [[10, 10], [10, 10], [50, 10], [50, 90.5]], [[0, 0], [33.3333, 66.6667], [100, 0]],
  [[0, 0], [40, 0], [40, 40], [80, 40], [80, 80]], [[12.345678, 0.1], [12.345678, 99.9]]];
write('fixtures/js/geometry.json', {
  rectsOverlap: rects.flatMap((a) => rects.flatMap((b) => [[a, b, 0, g.rectsOverlap(a, b)], [a, b, 10, g.rectsOverlap(a, b, 10)]])),
  segmentIntersectsRect: segs.flatMap((s) => rects.flatMap((r) => [[s, r, 0, g.segmentIntersectsRect(s, r)], [s, r, 4, g.segmentIntersectsRect(s, r, 4)]])),
  segmentRectClearance: segs.flatMap((s) => rects.map((r) => [s, r, g.segmentRectClearance(s, r)])),
  polylinePath: polylines.map((p) => [p, g.polylinePath(p)]),
  roundedPath: polylines.flatMap((p) => [[p, 8, g.roundedPath(p, 8)], [p, 3.5, g.roundedPath(p, 3.5)]]),
  normalizeRoutePoints: polylines.map((p) => [p, g.normalizeRoutePoints(p)]),
  routePointsValue: polylines.map((p) => [p, g.routePointsValue(p)]),
  fittedNodeFontSize: texts.map((t) => [t, tf.fittedNodeFontSize(t, 120, 9, 6), tf.minimumNodeTextWidth(t, 6), tf.availableNodeTextWidth(120)]),
});
// shortestOrthogonalGridRoute / labelPoint / createRouter take large option objects; architecture goldens exercise them end to end.

// --- goldens for examples without one + synthetic fixtures (architecture only in phase 1) ---
const render = (type, input, output) => execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), input, output], { stdio: 'inherit', env: cleanEnv });
const EX = path.join(UP, 'examples');
fs.mkdirSync(out('goldens/checker'), { recursive: true });
const noProfile = JSON.parse(fs.readFileSync(path.join(EX, 'web-app.architecture.json'), 'utf8'));
delete noProfile.meta.quality_profile; noProfile.meta.output = 'web-app.no-profile.html';
write('fixtures/diagrams/web-app.no-profile.architecture.json', noProfile);
const remoteBrand = JSON.parse(fs.readFileSync(path.join(EX, 'brand-aware-delivery.architecture.json'), 'utf8'));
remoteBrand.components[0].brand = { url: 'https://example.invalid/logo.svg', sha256: 'a'.repeat(64) };
remoteBrand.meta.output = 'remote-brand.html';
write('fixtures/diagrams/remote-brand.architecture.json', remoteBrand);
const targets = [
  ['architecture', path.join(EX, 'production-deployment.architecture.json'), 'production-deployment-rendered.html'],
  ['architecture', path.join(EX, 'brand-aware-delivery.architecture.json'), 'brand-aware-delivery-rendered.html'],
  ['architecture', path.join(EX, 'checkout-platform.base.architecture.json'), 'checkout-platform.base-rendered.html'],
  ['architecture', path.join(EX, 'checkout-platform.head.architecture.json'), 'checkout-platform.head-rendered.html'],
  ['architecture', out('fixtures/diagrams/web-app.no-profile.architecture.json'), 'web-app.no-profile-rendered.html'],
];
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
  'China Standard Time', 'Eastern Standard Time', 'Mars/Olympus', '', 'UTC+8', 'utc', 'est', 'japan', 'etc/utc', 'america/new_york'];
const zoneValid = zoneIds.map((id) => { try { new Intl.DateTimeFormat('en-US', { timeZone: id }); return { id, valid: true }; } catch { return { id, valid: false }; } });
const wallZones = ['UTC', 'Asia/Shanghai', 'America/New_York', 'Europe/Berlin', 'Asia/Kolkata', 'Pacific/Chatham', 'Australia/Lord_Howe', '+08:00', '-05:30', '+0800', '+08', 'Australia/Sydney'];
const instants = ['2026-03-08T06:59:59Z', '2026-03-08T07:00:00Z', '2026-03-08T07:00:00.750Z', '2026-11-01T05:59:59Z', '2026-11-01T06:00:00Z', '2026-11-01T06:30:00Z',
  '2026-10-01T02:00:00Z', '2026-10-04T16:00:00Z', '2026-04-05T02:30:00Z', '2026-01-01T00:00:00Z', '2026-12-31T23:59:59Z', '1970-01-01T00:00:00Z', '1969-12-31T23:59:59Z',
  '0001-01-01T00:00:00Z', '0099-01-01T00:00:00Z', '0000-01-01T00:00:00Z', '9999-12-31T23:59:59Z', '+010000-01-01T00:00:00Z'].map((s) => Date.parse(s));
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
// --- waterfall timeNumber(): render-waterfall.mjs v3.2.0 :198-204 copied verbatim; the closed-over timeResolution is a parameter.
// MAX_VALUE at resolution 2e307 rounds to Infinity (toPrecision(3)) and takes the value.toString() fallback. ---
function wfTimeNumber(value, timeResolution) {
  const ordinary = wfNumber(value);
  if (Math.abs(value) < 1e21 && Math.abs(Number(ordinary.replaceAll(',', '')) - value) <= timeResolution / 100) return ordinary;
  const digits = value === 0 ? 1 : Math.min(17, Math.max(1, Math.floor(Math.log10(Math.abs(value))) - Math.floor(Math.log10(timeResolution)) + 2));
  const rounded = Number(value.toPrecision(digits));
  return Number.isFinite(rounded) ? rounded.toString() : value.toString();
}
const wfTimeValues = [0, 1e21, -1e21, 1700000000123.4, 1700000000123.456, 0.000123, 123456.789, 0.0091, 0.009104, -12.345, 9.99e20, 1e16 + 2,
  Number.MAX_VALUE, -Number.MAX_VALUE];
const wfTimeResolutions = [1, 0.5, 0.001, 1e-9, 4e-6, 2e307];
write('fixtures/js/waterfall-time-number.json',
  wfTimeValues.flatMap((value) => wfTimeResolutions.map((resolution) => ({ value, resolution, text: wfTimeNumber(value, resolution) }))));

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
// Tree text wrap (render-tree.mjs:123-145, :201): CJK and Hangul break per code unit, a word wider than 1.5 x nodeMaxW is cut
// inside itself (the Array.from loop), runs of spaces and a trailing space reach split(/\s+/) and the trims.
const wrap = ex('payment-platform.tree.json'); wrap.meta.output = 'payment-platform.wrap.html';
wrap.nodes[0].label = '支付平台核心服务能力分解与治理结构总览';
wrap.nodes[2].sublabel = '결제 처리 서비스 모음 및 정산 파이프라인 전체';
wrap.nodes[8].label = 'Refund   handling and  chargeback  disputes ';
wrap.nodes[9].label = 'TransactionReconciliationAndSettlementOrchestratorService';
write('fixtures/diagrams/payment-platform.wrap.tree.json', wrap);
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
// 3.2.0 wallClockTicks (render-timeline.mjs:261-276) and clockLabel's millisecond fraction (:150-154). Each input's branch is
// asserted after the layout reports below.
const tlProbe = (name, timezone, ats, width) => ({
  schema_version: 1, diagram_type: 'timeline',
  meta: { title: name, output: `${name}.html`, evidence: 'illustrative', timezone },
  layout: { width, breaks: 'none' },
  events: ats.map((at, i) => ({ id: `e${i}`, at, title: `Event ${i}` })),
});
const tickProbes = [
  // one segment whose ends are both EST; only the day probes see EDT, so the summer ticks need the second offset
  tlProbe('dst-roundtrip-segment', 'America/New_York', ['2026-02-15T00:00:00-05:00', '2026-11-15T00:00:00-05:00'], 600),
  tlProbe('dst-fall-back-hour', 'America/New_York', ['2026-11-01T04:30:00Z', '2026-11-01T07:30:00Z'], 800),     // repeated 01:00-02:00, 30 min tick
  tlProbe('dst-spring-forward-hour', 'America/New_York', ['2026-03-08T06:00:00Z', '2026-03-08T09:00:00Z'], 800), // skipped 02:00-03:00
  // seconds shown: a pre-1970 fraction (ms -250 -> .750), .250, and a whole second that prints no fraction
  tlProbe('ms-clock-label', 'UTC', ['1969-12-31T23:59:59.750Z', '1970-01-01T00:00:05.250Z', '1970-01-01T00:00:08Z'], 600),
  // 30-minute DST shift at an hourly tick (a 15 or 30 minute tick stays aligned across a 30-minute shift, so 3.1.0 would agree)
  tlProbe('dst-lord-howe', 'Australia/Lord_Howe', ['2026-04-05T00:00:00+11:00', '2026-04-05T03:00:00+10:30'], 480),
];
for (const d of tickProbes) write(`fixtures/diagrams/${d.meta.title}.timeline.json`, d);
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
// Schema-valid fallback sites no other golden reaches (TEST-001, final-review I-1): each is one committed input with one edit.
const noUnit = structuredClone(incomplete); noUnit.meta.output = 'incomplete-trace.no-unit.html'; delete noUnit.meta.unit;   // `wf.meta.unit || 'ms'` (render-waterfall.mjs), us -> ms observable
write('fixtures/diagrams/incomplete-trace.no-unit.waterfall.json', noUnit);
const wfWidth = ex('checkout-request.waterfall.json'); wfWidth.meta.output = 'checkout-request.width.html'; wfWidth.layout = { width: 500 };   // `wf.layout?.width ?? 760`
write('fixtures/diagrams/checkout-request.width.waterfall.json', wfWidth);
// 3.2.0 timeNumber (render-waterfall.mjs:195-204) and axisLabels (:240-267) at epoch-millisecond scale. Each input's branch is
// asserted after the layout reports below. The schema's layout.width minimum is 360.
const wfAxisProbe = (name, width, spans) => ({
  schema_version: 1, diagram_type: 'waterfall',
  meta: { title: name, output: `${name}.html`, evidence: 'illustrative', unit: 'ms' },
  ...(width ? { layout: { width } } : {}),
  spans: spans.map(([start, end], i) => ({ id: `s${i}`, name: `Span ${i}`, start, end })),
});
const axisProbes = [
  // 0.5 ms spans; .456 needs the precision branch; the zero-length span must stay out of timeResolution
  wfAxisProbe('epoch-offsets', undefined, [[1700000000123.4, 1700000000123.9], [1700000000123.456, 1700000000123.956], [1700000000123.4, 1700000000123.4]]),
  wfAxisProbe('crowded-axis', 360, [[1700000000123.4, 1700000000123.9], [1700000000123.9, 1700000000124.4], [1700000000124.4, 1700000000124.9]]),   // middle label omitted
  wfAxisProbe('endpoint-shift', 360, [[1700000000123.06, 1700000000124.44]]),   // two ticks whose labels overlap: the first shifts left
  wfAxisProbe('zero-duration', undefined, [[1700000000123.456, 1700000000123.456]]),   // no positive duration: timeResolution is tickStep (1)
];
for (const d of axisProbes) write(`fixtures/diagrams/${d.meta.title}.waterfall.json`, d);
const noTz = ex('payment-incident.timeline.json'); noTz.meta.output = 'payment-incident.no-timezone.html'; delete noTz.meta.timezone;   // `tl.meta.timezone || 'UTC'`
write('fixtures/diagrams/payment-incident.no-timezone.timeline.json', noTz);
const halfTz = ex('payment-incident.timeline.json'); halfTz.meta.output = 'payment-incident.minus-half.html'; halfTz.meta.timezone = '-05:30';   // fixed offset at render level
write('fixtures/diagrams/payment-incident.minus-half.timeline.json', halfTz);
const lcBare = ex('agent-run.lifecycle.json'); lcBare.meta.output = 'agent-run.no-sublabel-tag.html';   // every golden state carries both: the other baseline tuples
for (const s of lcBare.states) { delete s.sublabel; delete s.tag; }
write('fixtures/diagrams/agent-run.no-sublabel-tag.lifecycle.json', lcBare);
const clBare = ex('payments.class.json'); clBare.meta.output = 'payments.no-sublabel.html'; for (const t of clBare.types) delete t.sublabel;
write('fixtures/diagrams/payments.no-sublabel.class.json', clBare);
// TEST-001 batch 2: the remaining phase-2 "P only" probes, with the probe script's edits (task-11 report).
const probeGoldens = [   // [type, example, mutation, edit]
  ['lifecycle', 'agent-run.lifecycle.json', 'no-cards', (d) => { delete d.cards; }],
  ['class', 'payments.class.json', 'no-members', (d) => { for (const t of d.types) { delete t.methods; delete t.attributes; } }],
  ['class', 'payments.class.json', 'no-relationships', (d) => { delete d.relationships; }],
  ['tree', 'payment-platform.tree.json', 'single-node', (d) => { d.nodes = [d.nodes.find((n) => n.parent === undefined)]; }],   // `Math.max` over one
  ['timeline', 'payment-incident.timeline.json', 'plus', (d) => { d.meta.timezone = '+08:00'; }],
  ['timeline', 'payment-incident.timeline.json', 'compact', (d) => { d.meta.timezone = '+08'; }],
  ['timeline', 'payment-incident.timeline.json', 'no-lanes', (d) => { delete d.lanes; for (const e of d.events) delete e.lane; }],
  ['timeline', 'payment-incident.timeline.json', 'single-event', (d) => { d.events = [d.events[0]]; }],
  ['waterfall', 'checkout-request.waterfall.json', 'unit-s', (d) => { d.meta.unit = 's'; }],
  ['waterfall', 'checkout-request.waterfall.json', 'no-service', (d) => { for (const s of d.spans) delete s.service; }],
  ['waterfall', 'checkout-request.waterfall.json', 'single-span', (d) => { d.spans = [d.spans[0]]; }],   // `Math.min/max` over one
];
const probeName = (type, example, mutation) => `${example.slice(0, -`.${type}.json`.length)}.${mutation}`;   // payments.no-members
for (const [type, example, mutation, edit] of probeGoldens) {
  const d = ex(example); const name = probeName(type, example, mutation);
  d.meta.output = `${name}.html`; edit(d);
  write(`fixtures/diagrams/${name}.${type}.json`, d);
}
// Phase-2 golden targets (appended to the phase-1 array so the checker loop covers them)
targets.push(
  ['sequence', path.join(EX, 'async-job-roundtrip.sequence.json'), 'async-job-roundtrip-rendered.html'],
  ['lifecycle', path.join(EX, 'deployment-release.lifecycle.json'), 'deployment-release-rendered.html'],
  ['sequence', out('fixtures/diagrams/note-wrap.sequence.json'), 'note-wrap-rendered.html'],
  ['sequence', out('fixtures/diagrams/brand-participants.sequence.json'), 'brand-participants-rendered.html'],
  ['lifecycle', out('fixtures/diagrams/deployment-release.notes.lifecycle.json'), 'deployment-release.notes-rendered.html'],
  ['tree', out('fixtures/diagrams/payment-platform.right.tree.json'), 'payment-platform.right-rendered.html'],
  ['tree', out('fixtures/diagrams/payment-platform.wrap.tree.json'), 'payment-platform.wrap-rendered.html'],
  ['timeline', out('fixtures/diagrams/dst-new-york.timeline.json'), 'dst-new-york-rendered.html'],
  ...tickProbes.map(({ meta: { title } }) => ['timeline', out(`fixtures/diagrams/${title}.timeline.json`), `${title}-rendered.html`]),
  ['timeline', out('fixtures/diagrams/payment-incident.zh-CN.timeline.json'), 'payment-incident.zh-CN-rendered.html'],
  ['waterfall', out('fixtures/diagrams/incomplete-trace.waterfall.json'), 'incomplete-trace-rendered.html'],
  ['waterfall', out('fixtures/diagrams/incomplete-trace.no-unit.waterfall.json'), 'incomplete-trace.no-unit-rendered.html'],
  ['waterfall', out('fixtures/diagrams/checkout-request.width.waterfall.json'), 'checkout-request.width-rendered.html'],
  ...axisProbes.map(({ meta: { title } }) => ['waterfall', out(`fixtures/diagrams/${title}.waterfall.json`), `${title}-rendered.html`]),
  ['timeline', out('fixtures/diagrams/payment-incident.no-timezone.timeline.json'), 'payment-incident.no-timezone-rendered.html'],
  ['timeline', out('fixtures/diagrams/payment-incident.minus-half.timeline.json'), 'payment-incident.minus-half-rendered.html'],
  ['lifecycle', out('fixtures/diagrams/agent-run.no-sublabel-tag.lifecycle.json'), 'agent-run.no-sublabel-tag-rendered.html'],
  ['class', out('fixtures/diagrams/payments.no-sublabel.class.json'), 'payments.no-sublabel-rendered.html'],
  ...probeGoldens.map(([type, example, mutation]) => [type, out(`fixtures/diagrams/${probeName(type, example, mutation)}.${type}.json`), `${probeName(type, example, mutation)}-rendered.html`]),
);
// --- phase 3: upstream regression inputs (C:\projects\archify\test\fixtures), copied verbatim, renamed by folder ---
const TF = 'C:/projects/archify/test/fixtures';
const phase3Copies = [
  ['workflow', 'automatic-routing-node-border-clearance.workflow.json', 'border-clearance.workflow.json'],
  ['workflow', 'v1-workflow-700x400.workflow.json', 'v1-700x400.workflow.json'],
  ['workflow', 'v1-workflow-explicit-coordinates.workflow.json', 'v1-explicit-coordinates.workflow.json'],
  ['workflow', 'workflow-dense-automatic.json', 'dense-automatic.workflow.json'],
  ['workflow', 'workflow-fanin-label-recovery.json', 'fanin-label-recovery.workflow.json'],
  ['workflow', 'workflow-free-space.json', 'free-space.workflow.json'],
  ['workflow', 'workflow-return-arrival.json', 'return-arrival.workflow.json'],
  ['workflow', 'workflow-route-text-recovery.json', 'route-text-recovery.workflow.json'],
  ['workflow', 'workflow-routing-clarity.workflow.json', 'routing-clarity.workflow.json'],
  ['workflow', 'workflow-shared-trunks.workflow.json', 'shared-trunks.workflow.json'],
  ['workflow', 'v1-baseline/agent-tool-call.workflow.json', 'v1-baseline.agent-tool-call.workflow.json'],
  // release-luna.workflow.json is upstream's NEGATIVE fixture (7 workflow/node-overlap, T/architecture-first-draft.test.mjs:261-269):
  // not a golden — it is the `wf-release-luna` failure receipt below.
  ...['agent-tool-call', 'automatic-fanout', 'automatic-port-slot', 'customer-refund', 'incident-response']
    .map((n) => ['workflow', `workflow-first-draft/${n}.workflow.json`, `first-draft.${n}.workflow.json`]),
  ...['order-overflow', 'order-reflow', 'three-lane-remainder']
    .map((n) => ['workflow', `workflow-viewport/${n}.workflow.json`, `viewport.${n}.workflow.json`]),
  ['dataflow', 'dataflow-bidirectional-labels.json', 'bidirectional-labels.dataflow.json'],
  ['dataflow', 'dataflow-label-visibility.json', 'label-visibility.dataflow.json'],
  ['dataflow', 'v1-baseline/product-analytics.dataflow.json', 'v1-baseline.product-analytics.dataflow.json'],
  ...['joint-label-routing', 'mixed-port-recovery', 'ml-features', 'rejected-grid-recovery', 'vertical-label-corridor', 'vertical-label-neighbour']
    .map((n) => ['dataflow', `dataflow-first-draft/${n}.dataflow.json`, `first-draft.${n}.dataflow.json`]),
];
for (const [, from, to] of phase3Copies) fs.copyFileSync(path.join(TF, from), out(`fixtures/diagrams/${to}`));
// Inline scenes from upstream tests. pipeline() and fiveStageDiagram() are copied verbatim from T/dataflow-automatic-layout.test.mjs:27-51
// and :355-361; the legacy narrow dataflow literal verbatim from T/v1-compatibility.test.mjs:180-203.
function pipeline() {
  return {
    schema_version: 1, diagram_type: 'dataflow',
    meta: { title: 'Request processing', output: 'diagram.html', quality_profile: 'showcase' },
    stages: ['Input', 'Prepare', 'Execute', 'Decode', 'Output'].map(label => ({ label })),
    nodes: [
      { id: 'input', type: 'frontend', label: 'Configuration', stage: 0, row: 0 },
      { id: 'prepare', type: 'backend', label: 'Invocation', stage: 1, row: 0 },
      { id: 'task', type: 'backend', label: 'Task', stage: 2, row: 0 },
      { id: 'provider', type: 'external', label: 'Provider', stage: 2, row: 2 },
      { id: 'bytes', type: 'external', label: 'Log bytes', stage: 3, row: 2 },
      { id: 'decoded', type: 'backend', label: 'Answer and status', stage: 3, row: 0,
        sublabel: 'success / failed / timeout / cancelled' },
      { id: 'output', type: 'backend', label: 'Ordered results', stage: 4, row: 0,
        sublabel: 'complete / partial / failed' },
      { id: 'archive', type: 'database', label: 'Archive', stage: 4, row: 2 },
    ],
    flows: [
      ['input', 'prepare', 'configuration'], ['prepare', 'task', 'task input'],
      ['task', 'provider', 'command'], ['provider', 'bytes', 'log bytes'],
      ['bytes', 'decoded', 'decode'], ['task', 'decoded', 'timeout / cancellation'],
      ['decoded', 'output', 'answer'], ['output', 'archive', 'save mode'],
    ].map(([from, to, label], index) => ({ id: `f${index}`, from, to, label })),
  };
}
function fiveStageDiagram(profile = 'standard') {
  const diagram = pipeline();
  diagram.meta.quality_profile = profile;
  diagram.nodes = diagram.stages.map((_, stage) => ({ id: `n${stage}`, type: 'backend', label: 'Node', stage, row: 0 }));
  diagram.flows = diagram.nodes.slice(1).map((node, index) => ({ id: `f${index}`, from: `n${index}`, to: node.id, label: 'd' }));
  return diagram;
}
const LEGACY_NARROW = {
  schema_version: 1,
  diagram_type: 'dataflow',
  meta: { title: 'Legacy narrow dataflow', viewBox: [423, 720] },
  stages: [{ label: 'Input' }, { label: 'Output' }],
  nodes: [
    { id: 'in_0', type: 'backend', label: 'In 0', stage: 0, row: 0 },
    { id: 'out_0', type: 'database', label: 'Out 0', stage: 1, row: 0 },
    { id: 'in_1', type: 'backend', label: 'In 1', stage: 0, row: 1 },
    { id: 'out_1', type: 'backend', label: 'Out 1', stage: 1, row: 1 },
    { id: 'in_2', type: 'backend', label: 'In 2', stage: 0, row: 2 },
    { id: 'out_2', type: 'backend', label: 'Out 2', stage: 1, row: 2 },
    { id: 'in_3', type: 'backend', label: 'In 3', stage: 0, row: 3 },
    { id: 'out_3', type: 'backend', label: 'Out 3', stage: 1, row: 3 },
  ],
  flows: ['emphasis', 'security', 'dashed', 'default'].map((variant, index) => ({
    from: `in_${index}`,
    to: `out_${index}`,
    label: variant,
    variant,
    route: 'straight',
  })),
};
write('fixtures/diagrams/five-stage.dataflow.json', fiveStageDiagram());   // meta.quality_profile 'standard', no viewBox
write('fixtures/diagrams/pipeline.dataflow.json', pipeline());             // showcase, no viewBox
// The test's render() (T/v1-compatibility.test.mjs:17) adds the schema-required meta.output before rendering; do the same.
write('fixtures/diagrams/legacy-narrow.dataflow.json', { ...LEGACY_NARROW, meta: { ...LEGACY_NARROW.meta, output: 'dataflow-v1-compatibility.html' } });
// Brand fixtures: built-in marks from the embedded catalog (upstream/brand-marks/catalog.json ids).
const pa = ex('product-analytics.dataflow.json');
pa.nodes[0].brand = 'react'; pa.nodes[6].brand = 'snowflake'; pa.meta.output = 'product-analytics.brand.html';
write('fixtures/diagrams/product-analytics.brand.dataflow.json', pa);
const hp = ex('holiday-planning.workflow.json');
hp.nodes[1].brand = 'notion'; hp.nodes[2].brand = 'wechat'; hp.meta.output = 'holiday-planning.brand.html';   // nodes[0] 'today' fails the brand top rail (92px node)
write('fixtures/diagrams/holiday-planning.brand.workflow.json', hp);
// Phase-3 golden targets
const p3 = (type, name) => [type, out(`fixtures/diagrams/${name}.${type}.json`), `${name}-rendered.html`];
const p3Targets = [
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'holiday-planning-rendered.html'],
  ['workflow', path.join(EX, 'incident-response.workflow.json'), 'incident-response-rendered.html'],
  ['workflow', path.join(EX, 'release-delivery.workflow.json'), 'release-delivery-rendered.html'],
  ['dataflow', path.join(EX, 'event-stream.dataflow.json'), 'event-stream-rendered.html'],
  ...phase3Copies.map(([type, , to]) => p3(type, to.replace(`.${type}.json`, ''))),
  p3('dataflow', 'five-stage'), p3('dataflow', 'pipeline'), p3('dataflow', 'legacy-narrow'),
  p3('dataflow', 'product-analytics.brand'), p3('workflow', 'holiday-planning.brand'),
  // TEST-002 (survey-test2): committed inputs; t2-xrep-standard is the standard-profile success whose receipt carries a warning.
  p3('workflow', 't2-rg-100'), p3('workflow', 't2-detour'), p3('workflow', 't2-xrep-standard'),
];
targets.push(...p3Targets);
// --- phase 4: erd inputs. Upstream first-draft fixtures copied verbatim; inline scenes from upstream tests; documented edits of orders.erd.json. ---
const erdNames = [];   // every clean erd input written below, by its fixture name (golden `<name>-rendered.html`, layout report `<name>.layout.json`)
for (const n of ['blog', 'shop', 'warehouse']) {
  fs.copyFileSync(path.join(TF, `erd-first-draft/${n}.erd.json`), out(`fixtures/diagrams/first-draft.${n}.erd.json`));
  erdNames.push(`first-draft.${n}`);
}
// rows(), fixture() and verticalFixture() verbatim from T/erd-label-capacity.test.mjs:13-41.
const rows = count => Array.from({ length: count }, (_, index) => ({ name: `field_${index}`, type: 'TEXT' }));
function fixture(quality = 'standard') {
  return {
    schema_version: 1, diagram_type: 'erd', meta: { title: 'Branch labels', locale: 'en', quality_profile: quality },
    layout: { mode: 'grid', gapX: 72, gapY: 40, entityW: 260 },
    entities: [
      { id: 'parent', label: 'Parent', row: 1, col: 0, attributes: rows(9) },
      { id: 'category', label: 'Category', row: 3, col: 0, attributes: rows(12) },
      { id: 'item', label: 'Item', row: 0, col: 1, width: 290, attributes: rows(28) },
    ],
    relationships: [
      { id: 'parent_link', from: 'item', to: 'parent', label: 'parentId', fromCardinality: 'many', toCardinality: 'one' },
      { id: 'category_link', from: 'item', to: 'category', label: 'categoryId', fromCardinality: 'many', toCardinality: 'one' },
    ],
  };
}
function verticalFixture(quality = 'standard') {
  const document = fixture(quality);
  document.entities = [
    { id: 'parent', label: 'Parent', row: 0, col: 0, attributes: rows(3) },
    { id: 'category', label: 'Category', row: 2, col: 0, attributes: rows(3) },
    { id: 'item', label: 'Item', row: 1, col: 0, width: 290, attributes: rows(3) },
  ];
  document.relationships[0].from = 'parent';
  document.relationships[0].to = 'item';
  return document;
}
// i18n fixture() verbatim from T/erd-i18n.test.mjs:12-25, called as its locale loop does (:77-78, `fixture(undefined, locale)`).
function i18nFixture(translations, locale = 'zh-CN') {
  return {
    schema_version: 1,
    diagram_type: 'erd',
    meta: {
      title: 'Authored data model', locale, output: 'diagram.html',
      ...(translations === undefined ? {} : { translations }),
    },
    entities: [{
      id: 'users', label: 'Authored users', pos: [80, 80],
      attributes: [{ name: 'user_id', type: 'int', key: 'pk' }],
    }],
  };
}
const erdWrite = (name, d) => { write(`fixtures/diagrams/${name}.erd.json`, d); erdNames.push(name); };
erdWrite('label-capacity', fixture());
erdWrite('label-capacity.showcase', fixture('showcase'));   // the tests' `for (const quality of ['standard', 'showcase'])`
erdWrite('label-capacity-vertical', verticalFixture());
for (const locale of ['en', 'zh-CN', 'zh-TW', 'es', 'ko', 'ar']) erdWrite(`i18n.${locale}`, i18nFixture(undefined, locale));
// Derived from orders.erd.json; each reaches a branch no clean upstream input does (survey-erd §8).
const orders = () => ex('orders.erd.json');
const erdCrossing = (d) => {   // two authored `via` routes that cross: clean under standard, composition/proper-crossing under showcase
  d.meta.quality_profile = 'standard';
  d.relationships.push({ id: 'cross_a', from: 'customer', to: 'product', fromCardinality: 'one', toCardinality: 'many', via: [[330, 120], [330, 260]] },
    { id: 'cross_b', from: 'inventory', to: 'order', fromCardinality: 'many', toCardinality: 'one', via: [[200, 226], [450, 226]] });
};
const lanesDoc = orders();   // a second customer->order link with another marker style: laneOffsets.size 2
lanesDoc.relationships.push({ id: 'customer_primary_order', from: 'customer', to: 'order', fromCardinality: 'one', toCardinality: 'one', toOptional: true, identifying: false, variant: 'dashed' });
erdWrite('orders.lanes', lanesDoc);
const sevenDoc = orders(); delete sevenDoc.meta.views;   // 4 tags + d3..d5 = 7 domains: the 7th hue wraps to `database`
for (let i = 3; i <= 5; i++) for (const [s, col] of [['a', 0], ['b', 1]]) {
  sevenDoc.entities.push({ id: `d${i}_${s}`, label: `d${i}_${s}`, tag: `d${i}`, row: i - 1, col, attributes: [{ name: 'id', type: 'bigint', key: 'pk' }] });
}
erdWrite('orders.seven-domains', sevenDoc);
const crossingDoc = orders(); erdCrossing(crossingDoc);
erdWrite('orders.crossing-standard', crossingDoc);
// TEST-003: trunk snap / bridge / coordinate-undefined delete (survey-test3 §1); helpers verbatim from the survey.
const tpk = [{ name: 'id', type: 'bigint', key: 'pk' }];
const twide = [...tpk, { name: 'a', type: 'bigint' }, { name: 'b', type: 'bigint' }, { name: 'c', type: 'bigint' }];
const trel = (id, from, to, extra = {}) => ({ id, from, to, fromCardinality: 'many', toCardinality: 'one', ...extra });
const tscene = (title, entities, relationships) => ({ schema_version: 1, diagram_type: 'erd', meta: { title, locale: 'en' },
  layout: { mode: 'grid', origin: [40, 80], gapX: 56, gapY: 72, entityW: 200 }, entities, relationships });
erdWrite('erd-trunk-snap', tscene('Snapped trunks', [
  { id: 'hub_a', label: 'hub_a', row: 0, col: 0, width: 200, attributes: twide },
  { id: 'hub_b', label: 'hub_b', row: 1, col: 0, width: 190, attributes: twide },
  { id: 'east_a1', label: 'east_a1', row: 0, col: 1, width: 200, attributes: tpk },
  { id: 'east_a2', label: 'east_a2', row: 1, col: 1, width: 200, attributes: tpk },
  { id: 'east_b1', label: 'east_b1', row: 2, col: 1, width: 200, attributes: tpk },
  { id: 'east_b2', label: 'east_b2', row: 3, col: 1, width: 200, attributes: tpk },
], [trel('a1_hub', 'east_a1', 'hub_a'), trel('a2_hub', 'east_a2', 'hub_a'), trel('b1_hub', 'east_b1', 'hub_b'), trel('b2_hub', 'east_b2', 'hub_b')]));
// T/erd-rendering.test.mjs:176-200 verbatim (the scene; the test's render call is not needed).
erdWrite('erd-trunk-bridge', {
  schema_version: 1, diagram_type: 'erd', meta: { title: 'Bundled fan-in', locale: 'en' },
  layout: { mode: 'grid', origin: [40, 80], gapX: 56, gapY: 72, entityW: 200 },
  entities: [
    { id: 'hub', label: 'hub', row: 0, col: 0, width: 200, attributes: twide },
    { id: 'east_a', label: 'east_a', row: 0, col: 1, width: 200, attributes: tpk },
    { id: 'east_b', label: 'east_b', row: 1, col: 1, width: 200, attributes: tpk },
  ],
  relationships: [trel('a_hub', 'east_a', 'hub'), trel('b_hub', 'east_b', 'hub')],
});
erdWrite('erd-trunk-all-clash', tscene('Three styles', [
  { id: 'hub_a', label: 'hub_a', row: 0, col: 0, width: 200, attributes: twide },
  { id: 'hub_b', label: 'hub_b', row: 1, col: 0, width: 200, attributes: twide },
  { id: 'hub_c', label: 'hub_c', row: 2, col: 0, width: 200, attributes: twide },
  ...[1, 2, 3, 4, 5, 6].map((n, i) => ({ id: `east_${n}`, label: `east_${n}`, row: i, col: 1, width: 200, attributes: tpk })),
], [trel('r1', 'east_1', 'hub_a'), trel('r2', 'east_2', 'hub_a'),
  trel('r3', 'east_3', 'hub_b', { identifying: false }), trel('r4', 'east_4', 'hub_b', { identifying: false }),
  trel('r5', 'east_5', 'hub_c', { variant: 'emphasis', label: 'five' }), trel('r6', 'east_6', 'hub_c', { variant: 'emphasis', label: 'six' })]));
// PORT-007 (3.2.0): a trunk bridge must not touch an unrelated route or an earlier bridge. fixture() verbatim from
// T/erd-trunk-bridges.test.mjs:14-50 (v3.2.0); each scenario is one the test renders.
function bridgeFixture({ horizontal = false, conflict, dashed = false, offset = 0, width } = {}) {
  const table = (id, pos, rows = 3) => ({
    id, label: id, pos, width: rows * 20 + 36,
    attributes: Array.from({ length: rows }, (_, index) => ({ name: `f${index}`, type: 'int' })),
  });
  const document = {
    schema_version: 1, diagram_type: 'erd',
    meta: { title: 'Independent relationship beside a shared parent', locale: 'en', quality_profile: 'showcase', legend: { mode: 'hidden' } },
    entities: [table('hub', [40, 80], 4), table('a', [240, 0]), table('b', [240, 280]), table('c', [440, 0]), table('d', [440, 280])],
    relationships: [
      { id: 'a_hub', from: 'a', to: 'hub', fromCardinality: 'many', toCardinality: 'one', fromSide: 'left', toSide: 'right' },
      { id: 'b_hub', from: 'b', to: 'hub', fromCardinality: 'many', toCardinality: 'one', fromSide: 'left', toSide: 'right' },
    ],
  };
  if (conflict) {
    const x = conflict === 'crossing' ? 164 : 172 + offset;
    const [top, bottom] = width > 1.5 || conflict === 'touch' ? [130, 146] : [128, 148];
    document.relationships.push({
      id: 'c_d', from: 'c', to: 'd', fromCardinality: 'many', toCardinality: 'one',
      fromSide: 'left', toSide: 'left',
      via: [[400, 48], [400, top], [x, top], [x, bottom], [400, bottom], [400, 328]],
      ...(dashed ? { identifying: false } : {}),
      ...(width === undefined ? {} : { width }),
    });
  }
  if (horizontal) {
    document.entities.forEach(entity => { entity.pos.reverse(); });
    document.relationships.forEach(relationship => {
      const sides = { left: 'top', right: 'bottom' };
      relationship.fromSide = sides[relationship.fromSide];
      relationship.toSide = sides[relationship.toSide];
      relationship.via?.forEach(point => point.reverse());
    });
  }
  return document;
}
erdWrite('erd-bridge-blocked-route', bridgeFixture({ conflict: 'crossing', dashed: true }));   // c_d crosses the 126..150 gap
// width 4 at offset 2.5: blocked only through (1.5 + 4) / 2 = 2.75; the same offset at the default 1.5 still bridges.
erdWrite('erd-bridge-wide-route', bridgeFixture({ conflict: 'near', offset: 2.5, width: 4 }));
// The test's "bridges survive when nearby strokes remain separate" pair (vertical): just past each half-width sum, so one trunk stays.
erdWrite('erd-bridge-near-clear', bridgeFixture({ conflict: 'near', offset: 2.85, width: 4 }));
erdWrite('erd-bridge-near-clear-thin', bridgeFixture({ conflict: 'near', offset: 1.6 }));
// The same pair horizontal (the test loops over both orientations).
erdWrite('erd-bridge-near-clear-h', bridgeFixture({ conflict: 'near', offset: 2.85, width: 4, horizontal: true }));
erdWrite('erd-bridge-near-clear-thin-h', bridgeFixture({ conflict: 'near', offset: 1.6, horizontal: true }));
// The test's horizontal overlap scenario: c_d runs along the bus inside the gap, so the bridge is rejected on axis 1.
erdWrite('erd-bridge-blocked-route-h', bridgeFixture({ conflict: 'overlap', horizontal: true }));
// Same-axis bridges in one cluster. m sends two relationships, so both ends are spread and alignAutomaticFacingPorts
// keeps m's slots (247.2, 271.2) 1.2px off the hub's (246, 270): runs a | m1 | m2 | b leave gaps A 222..246,
// B 247.2..270, C 271.2..294. B lies within 1.5 of the taken A and is rejected by the bridged check alone (every
// relationship is a cluster member, so no route conflicts); C is taken only because the rejected B was not appended.
// Standard profile: showcase fails the 1.2px interior segments as composition/micro-segment.
{
  const doc = bridgeFixture();
  doc.meta.quality_profile = 'standard';
  doc.entities = doc.entities.filter((entity) => ['hub', 'a', 'b'].includes(entity.id));
  doc.entities.find((entity) => entity.id === 'hub').pos = [40, 200];
  doc.entities.find((entity) => entity.id === 'b').pos = [240, 480];
  doc.entities.push({ ...doc.entities.find((entity) => entity.id === 'a'), id: 'm', label: 'm', pos: [240, 211.2] });
  doc.relationships.splice(1, 0, ...['m1_hub', 'm2_hub'].map((id) => (
    { id, from: 'm', to: 'hub', fromCardinality: 'many', toCardinality: 'one', fromSide: 'left', toSide: 'right' })));
  erdWrite('erd-bridge-blocked-bridge', doc);
}
targets.push(...erdNames.map((name) => p3('erd', name)));
// Quality-option goldens: minted with the env var upstream reads; C# renders the same input with RenderOptions.
const renderEnv = (type, input, output, env) => execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), input, output], { stdio: 'inherit', env: { ...cleanEnv, ...env } });
const optionGoldens = [   // [type, input, golden, quality]
  ['dataflow', out('fixtures/diagrams/five-stage.dataflow.json'), 'five-stage.option-showcase-rendered.html', 'showcase'],
  ['workflow', path.join(EX, 'agent-tool-call.workflow.json'), 'agent-tool-call.option-standard-rendered.html', 'standard'],
];
for (const [type, input, golden, quality] of optionGoldens) renderEnv(type, input, out(`goldens/${golden}`), { ARCHIFY_QUALITY_PROFILE: quality });
for (const [type, input, golden] of targets) render(type, input, out(`goldens/${golden}`));
// TEST-003 outcomes (survey-test3 §1): a later upstream change must not silently empty these rows.
{
  const html = (n) => fs.readFileSync(out(`goldens/${n}-rendered.html`), 'utf8');
  if (!html('erd-trunk-snap').includes('data-composition-points="256,108;256,612"')) throw new Error('erd-trunk-snap: no merged trunk at x=256');
  if (/data-er-trunk[^>]*a-emphasis|a-emphasis[^>]*data-er-trunk/.test(html('erd-trunk-all-clash'))) throw new Error('erd-trunk-all-clash: emphasis trunk drawn');
  if (!html('erd-trunk-all-clash').includes('data-er-trunk')) throw new Error('erd-trunk-all-clash: no trunks at all');
  // PORT-007: the rejected bridge splits the one 3.1.0 trunk (172,48;172,328) into two.
  const trunks = (n) => [...html(n).matchAll(/<path data-er-trunk=""[^>]*data-composition-points="([^"]+)"/g)].map((m) => m[1]).join(' | ');
  for (const n of ['erd-bridge-blocked-route', 'erd-bridge-wide-route']) {
    if (trunks(n) !== '172,48;172,126 | 172,150;172,328') throw new Error(`${n}: trunks ${trunks(n)}, expected the bridge 126..150 rejected`);
  }
  if (!/data-edge-id="c_d"[^>]*stroke-width="4"/.test(html('erd-bridge-wide-route'))) throw new Error('erd-bridge-wide-route: c_d not drawn at width 4');
  for (const n of ['erd-bridge-near-clear', 'erd-bridge-near-clear-thin']) {
    if (trunks(n) !== '172,48;172,328') throw new Error(`${n}: trunks ${trunks(n)}, expected the bridge to survive (one trunk)`);
  }
  for (const n of ['erd-bridge-near-clear-h', 'erd-bridge-near-clear-thin-h']) {
    if (trunks(n) !== '48,172;328,172') throw new Error(`${n}: trunks ${trunks(n)}, expected the bridge to survive (one trunk)`);
  }
  // 3.1.0 (checked once by rendering this input from c21c31ff): one trunk 48,172;328,172.
  if (trunks('erd-bridge-blocked-route-h') !== '48,172;126,172 | 150,172;328,172') throw new Error(`erd-bridge-blocked-route-h: trunks ${trunks('erd-bridge-blocked-route-h')}, expected the bridge 126..150 rejected`);
  // 3.1.0: one trunk 172,48;172,528. Appending the rejected B would also reject C: three trunks.
  if (trunks('erd-bridge-blocked-bridge') !== '172,48;172,247.2 | 172,270;172,528') throw new Error(`erd-bridge-blocked-bridge: trunks ${trunks('erd-bridge-blocked-bridge')}, expected only B rejected`);
}
// Node checker output for every golden (upstream 15 + ours); strip the absolute `file` so it is path-independent.
// The standard-profile crossing golden renders, but its report fails `orthogonal_arrows`: the authored `via` routes leave and
// enter their ports on diagonal segments. `relationship_crossings` passes.
const checkerFails = new Set(['orders.crossing-standard-rendered']);
const upstreamGoldens = fs.readdirSync(EX).filter((f) => f.endsWith('.html')).map((f) => path.join(EX, f));
function writeCheckerReport(html) {
  const expectFail = checkerFails.has(path.basename(html, '.html'));
  let stdout;
  try {
    stdout = execFileSync(process.execPath, [path.join(UP, 'scripts/check-render-output.mjs'), html],
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: cleanEnv, maxBuffer: 64 * 1024 * 1024 });   // upstream's checker buffer
  } catch (e) { if (!expectFail || e.status !== 1 || !e.stdout) throw e; stdout = e.stdout; }
  const res = JSON.parse(stdout);
  if (res.ok === expectFail) throw new Error(`checker ${path.basename(html)}: ok ${res.ok}, expected ${!expectFail}`);
  delete res.file;
  write(`goldens/checker/${path.basename(html, '.html')}.json`, res);
}
for (const html of [...upstreamGoldens, ...[...targets, ...optionGoldens].map(([, , g]) => out(`goldens/${g}`))]) writeCheckerReport(html);
// Remote brand: upstream has no "offline" switch; `example.invalid` never resolves (fast ENOTFOUND), so upstream reports
// brand/capture-unavailable ("could not reproduce the pinned capture") and the render FAILS. Capture that receipt as the C# oracle.
try {
  execFileSync(process.execPath, [path.join(UP, 'renderers/architecture/render-architecture.mjs'), out('fixtures/diagrams/remote-brand.architecture.json'), out('goldens/remote-brand.html')],
    { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
  throw new Error('remote brand unexpectedly rendered — the fixture URL must not resolve');
} catch (e) { if (!e.stderr) throw e; write('fixtures/diagrams/remote-brand.expected.json', JSON.parse(String(e.stderr))); }
// Sequence failure receipts: the sequence goldens are all clean, so these faults (one per validateSequence branch the
// Task 5 review probed) pin the diagnostic codes, subjects, evidence and fixes. Each input is the upstream cache-miss
// example with one edit; upstream must FAIL (exit 1, JSON receipt on stderr) or the row is skipped and reported.
const sequenceFaults = [
  ['label-overlap', (d) => { d.messages[3].y = 210; }],                                                   // two labels share y 210
  ['self-message', (d) => { d.messages[2].to = 'api'; }],
  ['note-overlap', (d) => { d.messages[4].note = 'cache lookup by tenant and user id with a fallback to the primary store when the entry is stale or missing entirely'; }],
  ['note-canvas-limit', (d) => { d.messages[11].note = 'final render step with a long explanatory note that keeps wrapping inside the narrow lane gap until it runs past the canvas bottom edge for sure, really, many many words more'; d.messages[11].from = 'user'; d.messages[11].to = 'web'; }],
  ['segment-border-run', (d) => { d.segments[1].from = 270; }],
  ['segment-label-wide', (d) => { d.segments[2].label = 'R'.repeat(220); }],
  ['legend-room', (d) => { d.meta.viewBox = [1080, 500]; d.segments.pop(); d.activations = d.activations.filter((a) => a.to < 440); d.messages = d.messages.filter((m) => m.y < 420); }],
  ['participant-overflow', (d) => { d.participants[2].label = 'API Gateway Request Handler Service Layer'; d.participants[2].sublabel = 'handles every inbound request and fans out to downstream services'; }],
  ['duplicate-participant', (d) => { d.participants[6].id = d.participants[5].id; }],                     // `participants.size !== length` (TEST-001)
];
for (const [name, fault] of sequenceFaults) {
  const diagram = JSON.parse(fs.readFileSync(path.join(EX, 'cache-miss-request.sequence.json'), 'utf8'));
  fault(diagram);
  diagram.meta.output = `${name}.html`;
  write(`fixtures/diagrams/${name}.sequence.json`, diagram);
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/sequence/render-sequence.mjs'), out(`fixtures/diagrams/${name}.sequence.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
    console.warn(`sequence fault ${name} unexpectedly rendered — no receipt written`);
    fs.rmSync(out(`goldens/${name}.html`), { force: true });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; write(`fixtures/diagrams/${name}.sequence.expected.json`, JSON.parse(String(e.stderr))); }
}
// Lifecycle failure receipts: one injected fault per diagnostic code validateStructure/validateLayout can emit.
const lifecycleFaults = [
  ['unknown-main-path-state', (d) => { d.mainPath[2] = 'ghost'; }],
  ['unknown-endpoint', (d) => { d.transitions[4].to = 'nowhere'; }],
  ['duplicate-state', (d) => { d.states.push({ ...d.states[5] }); }],
  ['repeated-main-path-state', (d) => { d.mainPath.push('planning'); }],
  ['main-path-gap', (d) => { d.transitions.splice(0, 1); }],
  ['state-text-too-long', (d) => { d.states[1].sublabel = 'x'.repeat(60); d.states[2].label = 'Executing every queued tool call'; }],
  ['crowded-side-transitions', (d) => { for (let i = 0; i < 6; i += 1) d.transitions.push({ from: 'approval', to: 'failed', label: `trigger ${i}` }); }],
  ['too-wide', (d) => { for (let i = 0; i < 4; i += 1) d.transitions[i].label = `${d.transitions[i].label} once every precondition has been verified`; }],
  ['label-unplaced', (d) => { d.transitions[8].label = 'retry after the exponential backoff window has fully elapsed and budget remains'; }],
];
for (const [name, fault] of lifecycleFaults) {
  const diagram = JSON.parse(fs.readFileSync(path.join(EX, 'agent-run.lifecycle.json'), 'utf8'));
  fault(diagram);
  diagram.meta.output = `${name}.html`;
  write(`fixtures/diagrams/${name}.lifecycle.json`, diagram);
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/lifecycle/render-lifecycle.mjs'), out(`fixtures/diagrams/${name}.lifecycle.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
    console.warn(`lifecycle fault ${name} unexpectedly rendered — no receipt written`);
    fs.rmSync(out(`goldens/${name}.html`), { force: true });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; write(`fixtures/diagrams/${name}.lifecycle.expected.json`, JSON.parse(String(e.stderr))); }
}
// Class failure receipts: one injected fault per diagnostic code validateClassDiagram (and the module-load placement
// gate) can emit, from the payments example. `unplaced-type` reaches the "Class diagram placement failed" throw at
// render-class.mjs:248; the rest reach the validation throw at :813 with their typed detail.
const classFaults = [
  ['realization-target', (d) => { d.types[1].kind = 'class'; }],                                         // both realizations now target a class
  ['inheritance-kind', (d) => { d.relationships[2].kind = 'inheritance'; }],                             // class -> interface as inheritance
  ['inheritance-cycle', (d) => { d.relationships.push({ from: 'card_gateway', to: 'wallet_gateway', kind: 'inheritance' }, { from: 'wallet_gateway', to: 'card_gateway', kind: 'inheritance' }); }],
  ['title-text-capacity', (d) => { d.types[2].label = 'OrderAggregateRootEntity'; d.types[2].width = 120; }],
  ['marker-capacity', (d) => { for (let i = 0; i < 4; i += 1) d.relationships.push({ from: 'order', to: 'card_gateway', kind: 'dependency' }); }],
  ['unknown-type', (d) => { d.relationships[0].to = 'ghost'; }],
  ['unplaced-type', (d) => { delete d.types[0].row; }],
  ['duplicate-type-id', (d) => { d.types[4].id = d.types[3].id; d.relationships = d.relationships.filter((r) => r.from !== d.types[4].id && r.to !== d.types[4].id); }],   // probe cl-dup-type-id (TEST-001)
];
for (const [name, fault] of classFaults) {
  const diagram = JSON.parse(fs.readFileSync(path.join(EX, 'payments.class.json'), 'utf8'));
  fault(diagram);
  diagram.meta.output = `${name}.html`;
  write(`fixtures/diagrams/${name}.class.json`, diagram);
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/class/render-class.mjs'), out(`fixtures/diagrams/${name}.class.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
    console.warn(`class fault ${name} unexpectedly rendered — no receipt written`);
    fs.rmSync(out(`goldens/${name}.html`), { force: true });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; write(`fixtures/diagrams/${name}.class.expected.json`, JSON.parse(String(e.stderr))); }
}
// Class layout reports (`--layout-json` stdout) for the two upstream class goldens: the C# LayoutReport must stringify
// to these bytes (same keys, same order, same numbers).
for (const [name, input] of [['class-payments', 'payments.class.json'], ['class-payment-processors', 'payment-processors.class.json']]) {
  const stdout = execFileSync(process.execPath, [path.join(UP, 'renderers/class/render-class.mjs'), path.join(EX, input), out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  fs.writeFileSync(out(`fixtures/diagrams/${name}.layout.json`), stdout);
}
// Tree failure receipts: payment-platform.tree.json with one fault each, one per code render-tree.mjs can emit
// (validateStructure's four tree/* codes and the canvas check's layout/constraint).
const treeFaults = [
  ['root-count', (d) => { delete d.nodes[1].parent; }],                                                   // orders becomes a second root
  ['missing-parent', (d) => { d.nodes[1].parent = 'ghost'; }],
  ['duplicate-id', (d) => { d.nodes[4].id = 'cancel_order'; }],                                           // create_order renamed onto its sibling
  ['cycle', (d) => { d.nodes[1].parent = 'create_order'; }],                                              // orders <-> create_order, reached twice
  ['canvas-constraint', (d) => { d.meta.viewBox = [400, 240]; }],
];
for (const [name, fault] of treeFaults) {
  const diagram = JSON.parse(fs.readFileSync(path.join(EX, 'payment-platform.tree.json'), 'utf8'));
  fault(diagram);
  diagram.meta.output = `${name}.html`;
  write(`fixtures/diagrams/${name}.tree.json`, diagram);
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/tree/render-tree.mjs'), out(`fixtures/diagrams/${name}.tree.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
    console.warn(`tree fault ${name} unexpectedly rendered — no receipt written`);
    fs.rmSync(out(`goldens/${name}.html`), { force: true });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; write(`fixtures/diagrams/${name}.tree.expected.json`, JSON.parse(String(e.stderr))); }
}
// Tree layout reports (`--layout-json` stdout) for both upstream tree goldens and the rightward fixture.
for (const [name, input] of [['tree-payment-platform', path.join(EX, 'payment-platform.tree.json')], ['tree-archify-repository', path.join(EX, 'archify-repository.tree.json')],
  ['tree-payment-platform.right', out('fixtures/diagrams/payment-platform.right.tree.json')]]) {
  const stdout = execFileSync(process.execPath, [path.join(UP, 'renderers/tree/render-tree.mjs'), input, out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  fs.writeFileSync(out(`fixtures/diagrams/${name}.layout.json`), stdout);
}
// Timeline failure receipts: payment-incident.timeline.json with one fault each, one per code render-timeline.mjs's input
// checks (:61-110) can emit, plus the second branch of each two-branch check.
const timelineFaults = [
  ['invalid-timezone', (d) => { d.meta.timezone = 'Mars/Olympus_Mons'; }],
  ['invalid-timezone-windows', (d) => { d.meta.timezone = 'China Standard Time'; }],                   // a Windows id V8 rejects
  ['duplicate-lane-id', (d) => { d.lanes.push({ ...d.lanes[0] }); }],
  ['duplicate-id', (d) => { d.events[2].id = 'alert'; }],
  ['invalid-timestamp', (d) => { d.events[1].at = '2026-02-30T10:05:00+08:00'; }],                       // schema-valid, calendar-invalid
  ['invalid-timestamp-hour', (d) => { d.events[3].at = '2026-10-01T25:15:00+08:00'; }],                  // calendar-valid, Date.parse NaN
  ['unknown-lane', (d) => { d.events[1].lane = 'ghost'; delete d.events[4].lane; }],                     // both message variants
];
for (const [name, fault] of timelineFaults) {
  const diagram = JSON.parse(fs.readFileSync(path.join(EX, 'payment-incident.timeline.json'), 'utf8'));
  fault(diagram);
  diagram.meta.output = `${name}.html`;
  write(`fixtures/diagrams/${name}.timeline.json`, diagram);
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/timeline/render-timeline.mjs'), out(`fixtures/diagrams/${name}.timeline.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
    console.warn(`timeline fault ${name} unexpectedly rendered — no receipt written`);
    fs.rmSync(out(`goldens/${name}.html`), { force: true });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; write(`fixtures/diagrams/${name}.timeline.expected.json`, JSON.parse(String(e.stderr))); }
}
// Timeline layout reports (`--layout-json` stdout) for the timeline goldens and the tick probes.
for (const [name, input] of [['timeline-payment-incident', path.join(EX, 'payment-incident.timeline.json')],
  ['timeline-archify-dev-activity', path.join(EX, 'archify-dev-activity.timeline.json')],
  ['dst-new-york', out('fixtures/diagrams/dst-new-york.timeline.json')],
  ['payment-incident.zh-CN', out('fixtures/diagrams/payment-incident.zh-CN.timeline.json')],
  ...tickProbes.map(({ meta: { title } }) => [title, out(`fixtures/diagrams/${title}.timeline.json`)])]) {
  const stdout = execFileSync(process.execPath, [path.join(UP, 'renderers/timeline/render-timeline.mjs'), input, out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  fs.writeFileSync(out(`fixtures/diagrams/${name}.layout.json`), stdout);
}
// Each tick probe reaches its branch (a later upstream change must not silently turn one into a no-op).
{
  const zoneOffset = (tz, ms) => {   // verbatim render-timeline.mjs:121-127 over wallParts
    const w = wallParts(tz, ms); const local = new Date(0);
    local.setUTCFullYear(+w.y, +w.mo - 1, +w.d); local.setUTCHours(+w.h, +w.mi, +w.s, 0);
    return local.getTime() - Math.floor(ms / 1000) * 1000;
  };
  const probe = (name) => {
    const report = JSON.parse(fs.readFileSync(out(`fixtures/diagrams/${name}.layout.json`), 'utf8'));
    const html = fs.readFileSync(out(`goldens/${name}-rendered.html`), 'utf8');
    const tickLabels = [...html.matchAll(/<text\b[^>]*\btl-tick\b[^>]*>([^<]*)<\/text>/g)].map((m) => m[1]);
    const texts = [...html.matchAll(/>([^<]*)<\/text>/g)].map((m) => m[1]);
    const fail = (why) => { throw new Error(`${name}: ${why}`); };
    return { report, tickLabels, texts, fail, ticks: report.ticks.map(({ t }) => t), seg: report.segments[0] };
  };
  const count = (list, value) => list.filter((x) => x === value).length;
  { const { report, seg, fail } = probe('dst-roundtrip-segment'); const tz = 'America/New_York';
    if (report.segments.length !== 1) fail(`${report.segments.length} segments, expected 1`);
    if (zoneOffset(tz, seg.start) !== zoneOffset(tz, seg.end)) fail('end offsets differ');
    const offsets = new Set([zoneOffset(tz, seg.start), zoneOffset(tz, seg.end)]);
    for (let p = seg.start + 86_400_000; p < seg.end; p += 86_400_000) offsets.add(zoneOffset(tz, p));
    if (offsets.size !== 2) fail(`offsets.size ${offsets.size}, expected 2`); }
  { const { tickLabels, ticks, fail } = probe('dst-fall-back-hour');
    if (ticks[1] - ticks[0] !== 1_800_000) fail('tick interval is not 30 minutes');
    if (count(tickLabels, '01:00') !== 2 || count(tickLabels, '01:30') !== 2) fail(`labels ${tickLabels.join(' | ')}`); }
  { const { tickLabels, fail } = probe('dst-spring-forward-hour');
    if (!tickLabels.length || tickLabels.some((label) => label.startsWith('02:'))) fail(`labels ${tickLabels.join(' | ') || '(none)'}`); }
  { const { texts, fail } = probe('ms-clock-label');
    for (const end of [':05.250', ':59.750', ':00:08']) if (!texts.some((x) => x.endsWith(end))) fail(`no label ends in ${end}`); }
  { const { ticks, seg, fail } = probe('dst-lord-howe'); const tz = 'Australia/Lord_Howe'; const interval = ticks[1] - ticks[0];
    if (interval !== 3_600_000) fail('tick interval is not one hour');
    const offset = zoneOffset(tz, seg.start); const old = [];   // 3.1.0: one offset, the segment start's
    for (let t = Math.ceil((seg.start + offset) / interval) * interval - offset; t <= seg.end; t += interval) old.push(t);
    if (JSON.stringify(old) === JSON.stringify(ticks)) fail('tick set equals the 3.1.0 single-offset loop'); }
}
// Waterfall failure receipts: checkout-request.waterfall.json with one fault each, one per code render-waterfall.mjs's timing
// checks (:54-105) can emit, every per-span invalid-timing message, and the axis-scale site (:167-171).
const waterfallFaults = [
  ['duplicate-id', (d) => { d.spans[2].id = 'auth'; }],                                                  // inventory renamed onto its sibling
  ['invalid-timing', (d) => {
    d.spans[2].end = 50;                                                                                  // before start, and disagrees with duration
    d.spans[3].end = 250;                                                                                 // disagrees with start + duration
    delete d.spans[4].duration;                                                                           // neither end nor duration
    delete d.spans[5].duration; d.spans[5].status = 'cancelled';                                          // same, status in evidence
  }],
  ['invalid-timing-overflow', (d) => { d.spans[5].start = 1e308; d.spans[5].duration = 1e308; }],         // derived end is Infinity
  ['invalid-timing-scale', (d) => { d.spans = [{ id: 'request', name: 'POST /checkout', start: 0, end: 5e-324 }]; }],   // 760 / 5e-324 = Infinity
  ['missing-parent', (d) => { d.spans[1].parent = 'ghost'; }],
  ['cycle', (d) => { d.spans[0].parent = 'auth'; d.spans[5].parent = 'save'; }],                        // request <-> auth reached thrice, save -> save
];
for (const [name, fault] of waterfallFaults) {
  const diagram = JSON.parse(fs.readFileSync(path.join(EX, 'checkout-request.waterfall.json'), 'utf8'));
  fault(diagram);
  diagram.meta.output = `${name}.html`;
  write(`fixtures/diagrams/${name}.waterfall.json`, diagram);
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/waterfall/render-waterfall.mjs'), out(`fixtures/diagrams/${name}.waterfall.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
    console.warn(`waterfall fault ${name} unexpectedly rendered — no receipt written`);
    fs.rmSync(out(`goldens/${name}.html`), { force: true });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; write(`fixtures/diagrams/${name}.waterfall.expected.json`, JSON.parse(String(e.stderr))); }
}
// Waterfall layout reports (`--layout-json` stdout) for the waterfall goldens and the axis probes.
for (const [name, input] of [['waterfall-checkout-request', path.join(EX, 'checkout-request.waterfall.json')],
  ['waterfall-example-rebuild', path.join(EX, 'example-rebuild.waterfall.json')],
  ['incomplete-trace', out('fixtures/diagrams/incomplete-trace.waterfall.json')],
  ...axisProbes.map(({ meta: { title } }) => [title, out(`fixtures/diagrams/${title}.waterfall.json`)])]) {
  const stdout = execFileSync(process.execPath, [path.join(UP, 'renderers/waterfall/render-waterfall.mjs'), input, out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  fs.writeFileSync(out(`fixtures/diagrams/${name}.layout.json`), stdout);
}
// Each axis probe reaches its branch (a later upstream change must not silently turn one into a no-op).
{
  const { nodeTextFit } = await imp('renderers/shared/text-fit.mjs');
  const tickStepOf = (wall, axisWidth) => {   // verbatim render-waterfall.mjs:176-184 (minTickPx 80, NICE)
    if (!wall) return 1;
    const target = (80 / axisWidth) * wall;
    let magnitude = 10 ** Math.floor(Math.log10(target));
    for (;;) {
      for (const factor of [1, 2, 2.5, 5]) if (factor * magnitude >= target) return factor * magnitude;
      magnitude *= 10;
    }
  };
  const probe = (name) => {
    const report = JSON.parse(fs.readFileSync(out(`fixtures/diagrams/${name}.layout.json`), 'utf8'));
    const html = fs.readFileSync(out(`goldens/${name}-rendered.html`), 'utf8');
    const labels = [...html.matchAll(/<text x="([^"]+)"[^>]*class="t-muted wf-num" font-size="10\.5" text-anchor="middle">([^<]*)<\/text>/g)]
      .map((m) => ({ x: Number(m[1]), text: m[2] }));
    const durations = report.rows.map((row) => row.end - row.start);   // no open span in a probe, so row.end is the recorded end
    const resolution = Math.min(tickStepOf(report.wall, report.axis.x1 - report.axis.x0), ...durations.filter((d) => d > 0));   // :197
    const ordinaryFits = (v, res) => Math.abs(v) < 1e21 && Math.abs(Number(wfNumber(v).replaceAll(',', '')) - v) <= res / 100;
    const times = [...report.rows.flatMap((row) => [row.start, row.end]), ...report.ticks.map(({ t }) => t)];
    const fail = (why) => { throw new Error(`${name}: ${why}`); };
    return { report, html, labels, durations, resolution, ordinaryFits, times, fail, ticks: report.ticks };
  };
  { const { html, durations, resolution, ordinaryFits, times, fail } = probe('epoch-offsets');
    const precise = times.filter((v) => !ordinaryFits(v, resolution));
    if (!precise.length) fail('no recorded time takes the precision branch');
    for (const v of precise) {
      const text = wfTimeNumber(v, resolution);
      if (text === wfNumber(v) || !html.includes(text)) fail(`${v}: "${text}" missing or equal to 3.1.0's number()`);
    }
    if (!durations.includes(0)) fail('no zero-length span');
    if (!times.some((v) => ordinaryFits(v, resolution) && !ordinaryFits(v, 0))) fail('counting the zero-length span would change no label'); }
  { const { labels, ticks, resolution, fail } = probe('crowded-axis');
    const text = (t) => `${wfTimeNumber(t, resolution)} ms`;
    if (ticks.length < 3) fail(`${ticks.length} ticks, expected an intermediate one`);
    if (labels[0]?.text !== text(ticks[0].t) || labels.at(-1)?.text !== text(ticks.at(-1).t)) fail('an endpoint label is missing');
    if (labels.length >= ticks.length) fail('no intermediate label omitted'); }
  { const { report, labels, ticks, fail } = probe('endpoint-shift');
    if (ticks.length !== 2 || labels.length !== 2) fail(`${ticks.length} ticks, ${labels.length} labels, expected 2 and 2`);
    const half = (textUnits(labels[0].text) * 10.5 * nodeTextFit.widthFactor + 4) / 2;
    const clamped = Math.max(28 + half, Math.min(ticks[0].x, report.viewBox[0] - 28 - half));   // :244, margin 28
    if (!(labels[0].x < clamped)) fail(`first label at ${labels[0].x}, unshifted ${clamped}`); }
  { const { html, durations, resolution, report, fail } = probe('zero-duration');
    if (!durations.length || durations.some((d) => d > 0)) fail(durations.length ? 'a positive duration' : 'no spans');
    const t0 = report.origin;
    if (resolution !== 1 || wfTimeNumber(t0, resolution) !== wfNumber(t0) || wfTimeNumber(t0, 0) === wfNumber(t0) || !html.includes(`${wfNumber(t0)} → `)) {
      fail('the header range does not tell timeResolution = tickStep from 0');
    } }
}
// --- phase 3 manifest: one row per workflow/dataflow input, with owning task and (workflow) the compiler branches it reaches ---
// Coverage probe: workflow-compiler.mjs with a tag call inserted after each anchor, imported from a data: URL (relative imports made absolute).
const wfCompilerPath = path.join(UP, 'renderers/workflow/workflow-compiler.mjs');
const wfFileUrl = pathToFileURL(wfCompilerPath).href;
let wfProbeSrc = fs.readFileSync(wfCompilerPath, 'utf8').replace(/\r\n/g, '\n')
  .replace(/from '(\.[^']+)'/g, (_, rel) => `from '${new URL(rel, wfFileUrl).href}'`);
for (const [tag, anchor] of [
  ['controlled', 'function readableControlledRoute(edge, from, to) {\n'],
  ['preset', 'function readablePresetVia(edge, from, to, start, end, fromSide, toSide) {\n'],
  ['sizing', 'widthOverrides.set(node.id, width);\n'],
  ['endpoint-repairs', 'function automaticEndpointRepairs(edge, selected, from, to, naturalFromSide, naturalToSide, narrowSlots = false) {\n'],
  ['internal-detour', 'function readableInternalDetourPlans(edge, selected, from, to, naturalFromSide, naturalToSide) {\n'],
  ['free-space', 'function readableFreeSpacePlans(edge, selected, from, to, naturalFromSide, naturalToSide) {\n'],
]) {
  const count = wfProbeSrc.split(anchor).length - 1;
  if (count !== 1) throw new Error(`coverage probe anchor ${tag}: ${count} occurrences, expected 1`);
  wfProbeSrc = wfProbeSrc.replace(anchor, () => `${anchor}globalThis.__wfcov?.add('${tag}');\n`);
}
const wfProbe = await import(`data:text/javascript;base64,${Buffer.from(wfProbeSrc).toString('base64')}`);
const coverageOf = (workflow, qualityProfile) => {
  globalThis.__wfcov = new Set();
  wfProbe.compileWorkflow({ workflow: JSON.parse(JSON.stringify(workflow)), qualityProfile, sourceEvidence: null });
  const tags = [...globalThis.__wfcov].sort(); delete globalThis.__wfcov; return tags;
};
const repoRel = (p) => (p.startsWith(EX) ? `upstream/examples/${path.basename(p)}` : path.relative(ROOT, p).replaceAll('\\', '/'));
const PIN_KEYS = ['via', 'labelAt', 'channelX', 'channelY', 'route', 'fromSide', 'toSide', 'labelDx', 'labelDy', 'labelSegment'];
const goldenName = (g) => g.replace('-rendered.html', '');
const optionQuality = new Map(optionGoldens.map(([, , g, quality]) => [goldenName(g), quality]));
const p3Rows = [   // [name, type, input, golden (repo-relative)]
  ['agent-tool-call', 'workflow', path.join(EX, 'agent-tool-call.workflow.json'), 'upstream/examples/workflow-agent-tool-call-rendered.html'],
  ['product-analytics', 'dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'upstream/examples/dataflow-product-analytics.html'],
  ...[...p3Targets, ...optionGoldens].map(([type, input, golden]) => [goldenName(golden), type, input, `tests/goldens/${golden}`]),
].map(([name, type, input, golden]) => {
  const d = JSON.parse(fs.readFileSync(input, 'utf8'));
  const qualityProfile = optionQuality.get(name) ?? d.meta?.quality_profile ?? null;
  const pins = (d.edges || d.flows || []).some((r) => PIN_KEYS.some((k) => r[k] !== undefined));
  const coverage = type === 'workflow' ? coverageOf(d, qualityProfile ?? undefined) : [];
  const viewBox = Array.isArray(d.meta?.viewBox);
  const task = type === 'dataflow' ? (viewBox ? 4 : 5)
    : d.schema_version === 1 ? 7
      : ['agent-tool-call.option-standard', 'holiday-planning.brand'].includes(name) ? 11
        : pins || coverage.includes('controlled') || coverage.includes('preset') ? 10 : 8;
  return { name, type, input: repoRel(input), golden, schemaVersion: d.schema_version, viewBox, qualityProfile, locale: d.meta?.locale ?? null, pins, coverage, task };
});
write('fixtures/diagrams/phase3-manifest.json', {
  coverageNote: 'coverage = workflow-compiler branches reached by an instrumented in-process compile (controlled, preset, sizing decide ownership; endpoint-repairs, internal-detour, free-space are information for reviewers). Fix-discovery recompiles also reach them, so a tag is a superset of the final layout\'s branches. Never an expected value.',
  rows: p3Rows,
});
// Workflow compiler receipts: the CLI's own `--layout-json` stdout (render-workflow.mjs:30-33 prints JSON.stringify(compiled.receipt, null, 2)).
// A child process because diagnostics.mjs (already loaded through geometry.mjs above) fixes ARCHIFY_DIAGNOSTIC_FORMAT at import time.
fs.mkdirSync(out('fixtures/workflow'), { recursive: true });
for (const { name, type, input } of p3Rows.filter((r) => r.type === 'workflow')) {
  const option = optionQuality.has(name) ? { ARCHIFY_QUALITY_PROFILE: optionQuality.get(name) } : {};
  const stdout = execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), path.join(ROOT, input), out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json', ...option }, stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 64 * 1024 * 1024 });
  fs.writeFileSync(out(`fixtures/workflow/${name}.receipt.json`), stdout);
}
// T/workflow-migration-semantics.test.mjs (v3.2.0) document(): a v1 workflow whose labelAt-pinned bottom-channel pin-edge fails the
// intrinsic candidate, so column capacity falls back to planningWorkflow, which drops that edge (#497).
const migrationSemanticsDocument = (semanticChecks) => ({
  schema_version: 1, diagram_type: 'workflow',
  meta: { title: 'Fallback semantic contract', output: 'fallback.html', viewBox: [720, 520], legend: { mode: 'hidden' } },
  lanes: [{ id: 'pin', label: 'Pinned label' }, { id: 'straight', label: 'Straight constraint' }, { id: 'blocker', label: 'Blocker' }],
  nodes: [
    { id: 'a', lane: 'pin', col: 0, type: 'backend', label: 'A' }, { id: 'b', lane: 'pin', col: 1, type: 'backend', label: 'B' },
    { id: 'c', lane: 'straight', col: 0, type: 'backend', label: 'C' }, { id: 'd', lane: 'straight', col: 1, type: 'backend', label: 'D' },
    { id: 'obstacle', lane: 'blocker', col: 4, type: 'database', label: 'Obstacle' },
  ],
  mainPath: ['a', 'b'], semanticChecks: structuredClone(semanticChecks),
  edges: [
    { id: 'pin-edge', from: 'a', to: 'b', label: 'p', labelAt: [562, 367], route: 'bottom-channel', fromSide: 'bottom', toSide: 'bottom' },
    { id: 'straight-edge', from: 'c', to: 'd', label: 'x', route: 'straight' },
  ],
});
// Phase-3 failure receipts: one fault per diagnostic code; upstream must FAIL with the target code in its receipt or the script throws.
const p3Faults = [   // [type, source input (null: the edit builds the document), name, target code, edit]
  // workflow v2 without pins (holiday-planning, zh-CN). lanes calendar makeup; nodes today@calendar:0 mid_autumn@calendar:1 national_day@calendar:3
  // year_end@calendar:5 makeup_work@makeup:3; edges 0 today>mid_autumn 1 mid_autumn>national_day 2 national_day>year_end 3 national_day>makeup_work
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-duplicate-node-id', 'workflow/duplicate-node-id', (d) => { d.nodes[2].id = d.nodes[1].id; }],
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-duplicate-lane-id', 'workflow/duplicate-lane-id', (d) => { d.lanes[1].id = d.lanes[0].id; }],
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-unknown-edge-endpoint', 'workflow/unknown-edge-endpoint', (d) => { d.edges[2].to = 'year_ned'; }],
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-unknown-node-lane', 'workflow/unknown-node-lane', (d) => { d.nodes[4].lane = 'makeups'; }],
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-node-overlap', 'workflow/node-overlap', (d) => { d.nodes[4].lane = 'calendar'; }],
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-viewbox-capacity', 'workflow/viewbox-capacity', (d) => { d.meta.viewBox = [700, 360]; }],   // schema minimum width is 700
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-sublabel-readability', 'workflow/sublabel-readability',
    (d) => { for (const n of d.nodes) n.width = 280; d.nodes[1].width = 92; d.nodes[1].sublabel = '1234567890123456789012'; }],
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-semantic-reference', 'workflow/semantic-node-reference', (d) => { d.semanticChecks = { allowedRoots: ['nobody'] }; }],
  ['workflow', path.join(EX, 'holiday-planning.workflow.json'), 'wf-required-edge', 'workflow/required-edge', (d) => { d.semanticChecks = { requiredEdges: [{ from: 'today', to: 'year_end' }] }; }],
  // workflow v2 with pins/presets (agent-tool-call)
  ['workflow', path.join(EX, 'agent-tool-call.workflow.json'), 'wf-preset-conflict', 'workflow/route-preset-conflict', (d) => { d.edges[0].route = 'drop'; }],
  ['workflow', path.join(EX, 'agent-tool-call.workflow.json'), 'wf-single-pin-conflict', 'workflow/explicit-pin-conflict', (d) => { d.edges[1].via = [[300, 60]]; }],
  // workflow v1
  ['workflow', path.join(EX, 'incident-response.workflow.json'), 'wf-unknown-main-path', 'workflow/unknown-endpoint', (d) => { d.mainPath[3] = 'ghost'; }],
  ['workflow', path.join(TF, 'v1-workflow-explicit-coordinates.workflow.json'), 'wf-v1-column-capacity', 'workflow/column-capacity', (d) => { d.nodes[2].col = 1; d.nodes[3].col = 2; }],
  ['workflow', path.join(TF, 'workflow-first-draft/release-luna.workflow.json'), 'wf-release-luna', 'workflow/node-overlap', () => {}],   // upstream's own negative fixture, unedited
  // migrationSemanticsDocument with a/b widened to 130px (2px of the 8px clearance): only the planning fallback can offer the
  // migration, and its required a→b edge is the one planning drops. No source input: the edit builds the whole document.
  // Verified once out of tree (PORT-007 Task 5 scratchpad probe), not by this script: 3.1.0's receipt for this input has supportedFixes [], 3.2.0's offers the migration.
  ['workflow', null, 'wf-v1-semantic-fallback', 'workflow/column-capacity', (d) => {
    Object.assign(d, migrationSemanticsDocument({ requiredEdges: [{ from: 'a', to: 'b' }] }));
    d.nodes[0].width = 130; d.nodes[1].width = 130;
  }],
  // dataflow (product-analytics, viewBox 1080x520)
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-unknown-endpoint', 'dataflow/unknown-endpoint', (d) => { d.flows[2].to = 'consnt'; }],
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-node-above-area', 'dataflow/node-above-area', (d) => { d.nodes[3].yOffset = -400; }],
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-node-below-area', 'dataflow/node-below-area', (d) => { d.nodes[9].row = 4; d.nodes[9].yOffset = 300; }],
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-node-below-area-raised', 'dataflow/node-below-area', (d) => { d.nodes[9].row = 2; d.nodes[9].yOffset = 160; }],
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-duplicate-node', 'layout/constraint', (d) => { d.nodes[4].id = d.nodes[3].id; }],
  ['dataflow', path.join(EX, 'product-analytics.dataflow.json'), 'df-pinned-crossing', 'composition/proper-crossing',
    (d) => { delete d.flows[4].route; d.flows[4].via = [[610, 157], [610, 320], [660, 320], [660, 157]]; }],
];
const p3FaultReceipts = new Map();
for (const [type, source, name, code, edit] of p3Faults) {
  const diagram = source ? JSON.parse(fs.readFileSync(source, 'utf8')) : {};
  edit(diagram);
  write(`fixtures/diagrams/${name}.${type}.json`, diagram);
  let receipt;
  try {
    execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), out(`fixtures/diagrams/${name}.${type}.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; receipt = JSON.parse(String(e.stderr)); }
  if (!receipt) { fs.rmSync(out(`goldens/${name}.html`), { force: true }); throw new Error(`fault ${name}: rendered, expected ${code}`); }
  if (!receipt.diagnostics?.some((x) => x.code === code)) throw new Error(`fault ${name}: expected ${code}`);
  write(`fixtures/diagrams/${name}.${type}.expected.json`, receipt);
  p3FaultReceipts.set(name, receipt);
}
// The two node-below-area rows differ in whether the raised canvas fits (dataflow-compiler.mjs raisedCanvasFits).
const belowFits = (name) => p3FaultReceipts.get(name).diagnostics.find((x) => x.code === 'dataflow/node-below-area').evidence?.requiredViewBoxFitsViewport;
if (belowFits('df-node-below-area') !== false || belowFits('df-node-below-area-raised') !== true) {
  throw new Error(`node-below-area: requiredViewBoxFitsViewport ${belowFits('df-node-below-area')}/${belowFits('df-node-below-area-raised')}, expected false/true`);
}
// #497: the migration offer for wf-v1-semantic-fallback comes from the planning projection, which must drop semanticChecks.
{
  const capacity = p3FaultReceipts.get('wf-v1-semantic-fallback').diagnostics.find((x) => x.code === 'workflow/column-capacity');
  if (!capacity.supportedFixes.includes('migrate this workflow to schema_version 2')) throw new Error('wf-v1-semantic-fallback: no migration offer');
  const { compileWorkflow } = await imp('renderers/workflow/workflow-compiler.mjs');
  const fallbackMg = await imp('renderers/workflow/workflow-migration-geometry.mjs');
  const fallback = JSON.parse(fs.readFileSync(out('fixtures/diagrams/wf-v1-semantic-fallback.workflow.json'), 'utf8'));
  const planned = fallbackMg.planningWorkflow(fallback);
  if (compileWorkflow({ workflow: fallbackMg.intrinsicWorkflow(fallback) }).ok || planned.edges.length === fallback.edges.length
    || 'semanticChecks' in planned || !compileWorkflow({ workflow: planned }).ok) {
    throw new Error('wf-v1-semantic-fallback: the offer no longer comes from a semantic-free planning projection');
  }
}
// TEST-002 failure receipts (survey-test2 "Path table"): the committed t2-*.workflow.json inputs, never rewritten here. Upstream must FAIL
// with the code and the field that names the branch (evidence.invariant, bracketed name in the survey; conflictType; verifiedRepair).
const PIN = 'workflow/explicit-pin-conflict', PRESET = 'workflow/route-preset-conflict', CROSS = 'composition/proper-crossing';
const inv = (code, invariant, extra = {}) => ({ code, test: (e) => e?.invariant === invariant && Object.entries(extra).every(([k, v]) => e[k] === v) });
const t2Faults = [   // [name, target]
  ['t2-lg6', inv(PIN, 'readable route feasibility with authored endpoint sides')],
  ['t2-fb-exhaust', { code: 'workflow/solver-budget-exhausted' }],
  ['t2-labelroute2077', inv(PIN, 'explicit label-route clearance')],
  ['t2-labellabel2036', inv(PIN, 'explicit label-label clearance')],
  ['t2-labellabel2084', inv(PIN, 'explicit label-label clearance')],
  ['t2-route-crossing', inv(PIN, 'explicit route-route crossing')],
  ['t2-route-corridor', inv(PIN, 'explicit route-route corridor clearance')],
  ['t2-labelat-origin', inv(PIN, 'viewBox-origin containment')],
  ['t2-labelat-node', inv(PIN, 'edge-label node clearance')],
  ['t2-labelat-legend', inv(PIN, 'edge-label legend clearance')],
  ['t2-labelat-scene', inv(PIN, 'edge-label lane/phase/group clearance')],
  ['t2-neg-via', inv(PIN, 'viewBox-origin containment')],
  ['t2-neg-channel', inv(PIN, 'viewBox-origin containment')],
  ['t2-fin-wide-1e308', inv(PIN, 'finite route coordinates')],
  ['t2-nonzero', inv(PIN, 'non-zero route segments')],
  ['t2-stub', inv(PIN, '8px endpoint stub clearance')],
  ['t2-channel', inv(PIN, 'channel pin preservation')],
  ['t2-preset2562', inv(PIN, 'route preset compatibility')],
  ['t2-preset2218', inv(PIN, 'route preset compatibility')],
  ['t2-perpendicular', inv(PIN, 'perpendicular endpoint-side direction')],
  ['t2-pin-node', inv(PIN, 'node clearance')],
  ['t2-pin-legend', inv(PIN, 'legend clearance')],
  ['t2-pin-scene-label', inv(PIN, 'lane/phase/group label clearance')],
  ['t2-pin-frame-border', inv(PIN, 'structural-frame border clearance')],
  ['t2-placed-label-label', inv(PRESET, 'placed edge-label clearance', { conflictType: 'label-label' })],
  ['t2-placed-route-label', inv(PRESET, 'placed edge-label clearance', { conflictType: 'route-label' })],
  ['t2-placed-label-route', inv(PRESET, 'placed edge-label clearance', { conflictType: 'label-route' })],
  ['t2-preset-rhythm', inv(PRESET, 'readable segment rhythm')],
  ['t2-xrep', { code: CROSS, test: (e) => e?.verifiedRepair !== undefined }],
  ['t2-prep', { code: CROSS, test: (e) => e?.verifiedRepair?.kind === 'optional-node-placement' }],
];
for (const [name, target] of t2Faults) {
  let receipt;
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/workflow/render-workflow.mjs'), out(`fixtures/diagrams/${name}.workflow.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; receipt = JSON.parse(String(e.stderr)); }
  if (!receipt) { fs.rmSync(out(`goldens/${name}.html`), { force: true }); throw new Error(`t2 fault ${name}: rendered, expected ${target.code}`); }
  if (!receipt.diagnostics?.some((x) => x.code === target.code && (target.test === undefined || target.test(x.evidence)))) {
    throw new Error(`t2 fault ${name}: expected ${target.code} with the named branch; got ${JSON.stringify(receipt.diagnostics?.map((x) => [x.code, x.evidence?.invariant, x.evidence?.conflictType]))}`);
  }
  write(`fixtures/diagrams/${name}.workflow.expected.json`, receipt);
}
// TEST-004 failure receipts (survey-t004): committed t4-*.<type>.json inputs, never rewritten here. The test sees the whole diagnostic
// (arrays compared by JSON.stringify), and the dataflow rows also pin the diagnostic count (49 with the > 80 guard, 40 at the boundary).
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const t4Faults = [   // [type, name, code, test(diagnostic), receipt.diagnostics.length]
  ['workflow', 't4-retry-feedback', 'composition/ambiguous-corridor', (x) => same(x.evidence?.from, [481, 278]) && same(x.evidence?.to, [526, 278])],
  ['workflow', 't4-interior-turn', PIN, (x) => x.evidence?.invariant === '16px interior turn clearance'],
  ['workflow', 't4-viewbox-origin', PIN, (x) => x.subject?.path === '/meta/viewBox' && same(x.evidence?.contentBounds, [40, -21, 752, 218])],
  ['dataflow', 't4-label-guard', 'layout/constraint', (x) => x.message.includes('Label "N1 文文文" overlaps node "n03"'), 49],
  ['dataflow', 't4-label-guard-80', 'layout/constraint', () => true, 40],
];
for (const [type, name, code, test, count] of t4Faults) {
  let receipt;
  try {
    execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), out(`fixtures/diagrams/${name}.${type}.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 64 * 1024 * 1024 });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; receipt = JSON.parse(String(e.stderr)); }
  if (!receipt) { fs.rmSync(out(`goldens/${name}.html`), { force: true }); throw new Error(`t4 fault ${name}: rendered, expected ${code}`); }
  if (!receipt.diagnostics?.some((x) => x.code === code && test(x))) throw new Error(`t4 fault ${name}: expected ${code} with the named branch`);
  if (count !== undefined && receipt.diagnostics.length !== count) throw new Error(`t4 fault ${name}: ${receipt.diagnostics.length} diagnostics, expected ${count}`);
  write(`fixtures/diagrams/${name}.${type}.expected.json`, receipt);
}
// Phase-4 erd failure receipts: orders.erd.json with one edit each (agy's Node probe, plan review 2026-10-15). Upstream must FAIL with
// every target [code, evidence.reason?] in its receipt or the script throws. entities: 0 customer, 5 payment (tag checkout).
const erdFaults = [   // [name, targets, edit, env]
  ['erd-domain-scattered', [['erd/domain-not-drawn', 'scattered']], (d) => { d.entities[5].row = 1; d.entities[5].col = 3; }],
  ['erd-domain-pos', [['erd/domain-not-drawn', 'no-grid']], (d) => { delete d.entities[5].row; delete d.entities[5].col; d.entities[5].pos = [760, 44]; }],
  ['erd-domain-off-canvas', [['erd/domain-not-drawn', 'off-canvas']], (d) => { d.layout.origin = [5, 44]; }],
  ['erd-domain-band-covers', [['erd/domain-not-drawn', 'band-covers-entity']], (d) => { d.layout.gapY = 16; }],
  ['erd-header-capacity', [['erd/header-text-capacity']], (d) => { d.entities[0].label = 'CUSTOMER_ACCOUNT_DIRECTORY_RECORD_LONG'; }],
  ['erd-attribute-capacity', [['erd/attribute-text-capacity']], (d) => { d.entities[0].attributes[1].name = 'primary_verified_contact_email_address'; }],
  ['erd-marker-capacity', [['layout/marker-capacity']], (d) => {
    d.entities[0].attributes = [{ name: 'id', type: 'bigint', key: 'pk' }];
    for (let i = 0; i < 4; i++) d.relationships.push({ id: 'extra_' + i, from: 'order', to: 'customer', fromCardinality: 'many', toCardinality: 'one' });
  }],
  ['erd-duplicate-entity', [['layout/constraint']], (d) => { d.entities.push({ ...structuredClone(d.entities[0]), row: 2 }); }],
  ['erd-self-loop', [['layout/constraint']], (d) => { d.relationships.push({ from: 'customer', to: 'customer', fromCardinality: 'one', toCardinality: 'many' }); }],
  ['erd-legend-overflow', [['legend/vertical-overflow']], (d) => { d.meta.viewBox = [980, 480]; }],
  // customer is an endpoint of the one labelled relationship (`placed by`): NaN geometry reaches labelPoint -> JS TypeError (P2).
  ['erd-unplaced-labelled', [['internal/unclassified']], (d) => { delete d.entities[0].row; delete d.entities[0].col; }],
  // Same without labels: no crash, NaN comparisons give the grid problem plus no-grid / off-canvas domains (pinned in full).
  ['erd-unplaced-unlabelled', [['layout/constraint'], ['erd/domain-not-drawn', 'no-grid'], ['erd/domain-not-drawn', 'off-canvas']],
    (d) => { delete d.entities[0].row; delete d.entities[0].col; for (const r of d.relationships) delete r.label; }],
  // orders.crossing-standard's edit under the showcase option (upstream reads ARCHIFY_QUALITY_PROFILE in the gate path).
  ['erd-crossing-showcase', [['composition/proper-crossing']], erdCrossing, { ARCHIFY_QUALITY_PROFILE: 'showcase' }],
  // TEST-003 (survey-test3 §2): validateEr branches. Each starts from cloneWithoutViews, as upstream's tests do.
  ['erd-duplicate-attribute', [['layout/constraint']], (d) => { delete d.meta.views; d.entities[0].attributes.push({ name: 'id', type: 'bigint' }); }],
  ['erd-unknown-source', [['layout/constraint']], (d) => { delete d.meta.views; d.relationships[0].from = 'ghost'; d.relationships[0].id = 'ghost_link'; }],
  ['erd-unknown-target', [['layout/constraint']], (d) => { delete d.meta.views; d.relationships[0].to = 'ghost'; d.relationships[0].id = 'ghost_link'; }],
  ['erd-ref-unknown-entity', [['layout/constraint']], (d) => { delete d.meta.views; d.entities[1].attributes[1].references = 'nobody.id'; }],
  ['erd-ref-unknown-attribute', [['layout/constraint']], (d) => { delete d.meta.views; d.entities[1].attributes[1].references = 'customer.nope'; }],
  ['erd-too-short', [['layout/constraint']], (d) => { delete d.meta.views; d.layout.gapX = 10; }],
  ['erd-entity-overlap', [['layout/constraint'], ['erd/domain-not-drawn']], (d) => {
    delete d.meta.views; const e = d.entities[1]; delete e.row; delete e.col; delete e.tag; e.pos = [60, 100];
  }],
  // The renderer's non-finite-width check is dead upstream (survey-test3 §3); pin the schema gate: JSON.parse gives Infinity for 1e400.
  ['erd-width-infinite', [['schema/type']], (d) => {
    d.entities[0].width = 987654321;
    const text = JSON.stringify(d, null, 2) + '\n', anchor = '"width": 987654321';
    if (text.split(anchor).length !== 2) throw new Error('erd-width-infinite: anchor not unique');
    return text.replace(anchor, '"width": 1e400');
  }],
  // upstream T/erd-rendering.test.mjs:683-697, replacing the whole document (no `cards`, no `views` may survive).
  ['erd-label-over-entity', [['layout/constraint']], (d) => {
    for (const k of Object.keys(d)) delete d[k];
    Object.assign(d, { schema_version: 1, diagram_type: 'erd', meta: { title: 'Label corridor', locale: 'en' }, layout: { mode: 'grid', gapX: 60, gapY: 44 },
      entities: [{ id: 'left', label: 'left', pos: [32, 40], attributes: tpk },
        { id: 'right', label: 'right', pos: [332, 40], attributes: [...tpk, { name: 'left_id', type: 'bigint', key: 'fk', references: 'left.id' }] }],
      relationships: [trel('right_left', 'right', 'left', { label: 'settles by', labelDx: 0 })] });
  }],
];
for (const [name, want, edit, env] of erdFaults) {
  const diagram = orders();
  const raw = edit(diagram);   // an edit may return raw JSON text for inputs JSON.stringify cannot produce
  if (typeof raw === 'string') fs.writeFileSync(out(`fixtures/diagrams/${name}.erd.json`), raw);
  else write(`fixtures/diagrams/${name}.erd.json`, diagram);
  if (name === 'erd-label-over-entity') {   // the written file, not the in-memory doc
    const written = JSON.parse(fs.readFileSync(out(`fixtures/diagrams/${name}.erd.json`), 'utf8'));
    if ('cards' in written || 'views' in written.meta) throw new Error('erd-label-over-entity: cards/views survived');
  }
  let receipt;
  try {
    execFileSync(process.execPath, [path.join(UP, 'renderers/erd/render-erd.mjs'), out(`fixtures/diagrams/${name}.erd.json`), out(`goldens/${name}.html`)],
      { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json', ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (e) { if (e.status !== 1 || !e.stderr) throw e; receipt = JSON.parse(String(e.stderr)); }
  if (!receipt) { fs.rmSync(out(`goldens/${name}.html`), { force: true }); throw new Error(`fault ${name}: rendered, expected ${want}`); }
  for (const [code, reason] of want) {
    if (!receipt.diagnostics?.some((x) => x.code === code && (reason === undefined || x.evidence?.reason === reason))) throw new Error(`fault ${name}: expected ${code} ${reason ?? ''}`);
  }
  // internal/unclassified names the absolute input path; make it repo-relative so the fixture is machine-independent.
  for (const x of receipt.diagnostics) if (x.subject?.input === out(`fixtures/diagrams/${name}.erd.json`)) x.subject.input = `tests/fixtures/diagrams/${name}.erd.json`;
  write(`fixtures/diagrams/${name}.erd.expected.json`, receipt);
}
// Erd layout reports (`--layout-json` stdout) for both upstream erd examples and every clean phase-4 input.
fs.mkdirSync(out('fixtures/erd'), { recursive: true });
for (const [name, input] of [['orders', path.join(EX, 'orders.erd.json')], ['subscription-billing', path.join(EX, 'subscription-billing.erd.json')],
  ...erdNames.map((n) => [n, out(`fixtures/diagrams/${n}.erd.json`)])]) {
  const stdout = execFileSync(process.execPath, [path.join(UP, 'renderers/erd/render-erd.mjs'), input, out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  fs.writeFileSync(out(`fixtures/erd/${name}.layout.json`), stdout);
}
// --- phase 4: repository-location.mjs (pure, imported directly) and the JS URI built-ins it relies on (survey-evidence §1c, §7). ---
// js-uri.json: encodeURIComponent / decodeURIComponent / toLowerCase on one battery; a throw is recorded as null. Lone surrogates
// cannot travel through JSON to System.Text.Json, so those rows carry UTF-16 `units` instead of `s`.
const uriBattery = [
  '', 'plain', ' !"#$%&\'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~', '\u0000\u001f\u007f\u0080',
  "src/(x)!'*~.js", '中文 # router.js', '😀👍🏽', 'é', '\u00e9\u0301', '\u0130', 'İstanbul', 'ß', 'ẞ', '\u01c5', '\u01c4\u01c6', 'ﬀ', 'Ⅷ', 'ＡＢＣ', '𐐀𐐨',
  'ΟΔΟΣ', 'ΟΔΟΣ ΟΔΟΣ', 'Σ', 'AΣ', 'AΣB', '1Σ', 'AΣ.', "AΣ'B", 'AΣ\u0301', 'A\u0301Σ', 'Σ\u0301A', 'ΑΣ1', 'ᾼ', 'Ꭰ', 'ꭰ', '\u212a', 'Å',
  '%41', '%e4%b8%ad', '%E4%B8%AD', '%zz', '%', '%4', '%2F', '%25', '%2e%2e', '%C0%AF', '%E0%80%AF', '%F0%80%80%AF', '%ED%A0%80', '%ED%BF%BF',
  '%F4%90%80%80', '%F4%8F%BF%BF', '%F0%9F%98%80', '%80', '%BF', '%C3', '%C3%28', '%C3%', '%C3%A', '%FF', '%FE', '%F8%88%80%80%80', '%C2%A0',
  'a%20b', '%u0041', '%%41', '%4G', '%EF%BB%BF', '100%', 'x%E4%B8', 'repo%41',
];
const uriSafe = (f, s) => { try { return f(s); } catch { return null; } };
const uriRows = uriBattery.map((s) => ({ s, encode: uriSafe(encodeURIComponent, s), decode: uriSafe(decodeURIComponent, s), lower: s.toLowerCase() }));
if (!uriRows.some((r) => r.decode === null)) throw new Error('js-uri: no throwing decode row');
write('fixtures/js/js-uri.json', {
  rows: uriRows,
  encodeUnits: [[0xd800], [0x61, 0xdc00], [0xdbff, 0x62], [0xd83d, 0xde00, 0xd83d]].map((units) =>
    ({ units, encode: uriSafe(encodeURIComponent, String.fromCharCode(...units)) })),
});

// repository-location.json: parseRepositoryRemote under authored true/false and every provider hint; `pathKind` is identity[2]
// (the C# record carries it). URLs: every spelling in T/repository-evidence.test.mjs:147-482, then the survey §7 WHATWG probes.
const RL = await imp('renderers/shared/repository-location.mjs');
const rlValues = [
  // T/repository-evidence.test.mjs
  'https://github.com/example/evidence-repo', 'not a url at all', 'C:\\Users\\x\\evidence-repo', '/tmp/evidence-repo', './evidence-repo',
  'https://gitee.com/example/evidence-repo', 'https://gitee.com/example/evidence-repo.git/', 'git@gitee.com:example/evidence-repo.git',
  'ssh://git@gitee.com/example/evidence-repo.git', 'https://gitlab.com/example/platform/evidence-repo',
  'https://gitlab.com/example/platform/evidence-repo.git/', 'https://oauth2:not-a-real-token@gitlab.com/example/platform/evidence-repo.git',
  'git@gitlab.com:example/platform/evidence-repo.git', 'ssh://git@gitlab.com/example/platform/evidence-repo', 'git@gitlab.com:Example/Platform/evidence-repo.git',
  'https://git.example.internal/Platform/Services/evidence-repo', 'git@git.example.internal:Platform/Services/evidence-repo.git',
  'https://constructor/Platform/evidence-repo', 'https://__proto__/Platform/evidence-repo', 'http://git.example.internal/Platform/evidence-repo',
  'ssh://git@git.example.internal:2222/Platform/Services/evidence-repo.git', 'git@ssh.example.internal:Platform/Services/evidence-repo.git',
  'http://git.example.internal:3000/Platform/Services/evidence-repo',
  'https://git.internal/Platform/Services/repo.git', 'https://git.internal:443/Platform/Services/repo.git', 'ssh://git@git.internal/Platform/Services/repo',
  'ssh://git@git.internal:22/Platform/Services/repo', 'ssh://git@git.internal:2222/Platform/repo.git', 'git@git.internal:Platform/repo',
  'git@git.internal:/Platform/repo', 'ssh://git@git.internal/Platform/repo', 'http://git.internal:3000/Platform/repo.git',
  'http://user:SYNTHETIC_TOKEN@git.internal:3000/Platform/repo.git', 'https://git.internal/Platform/repo.git',
  'https://user:SYNTHETIC_TOKEN@git.internal/Platform/repo.git', 'git@git.internal:Platform/repo%41', 'ssh://git@git.internal/Platform/repo%41',
  'ssh://git@git.internal/Platform/repoA', 'ssh://git@git.internal/Team/repo', 'git@git.internal:Team/repo', 'git@git.internal:Team/repoA',
  'git@git.internal:Team/repo%41', 'https://git.internal/Team/repo', 'https://other.internal/Team/repo', 'https://git.internal/Team/other',
  'https://git.internal/team/repo', 'http://git.internal/Team/repo', 'https://git.internal:8443/Team/repo', 'ssh://git@git.internal:2222/Team/repo',
  'ssh://git@git.internal:2223/Team/repo', 'https://git.internal:2222/Team/repo', 'git@ssh-alias:Team/repo', 'https://git.internal/scm/Team/repo',
  'https://git.internal/Team/ignored/../repo', 'git@git.internal:Team/repo.git', 'http://git.internal/Team/repo.git', 'http://git.internal/team/repo',
  'https://gitee.com/team/repo', 'https://user:SYNTHETIC_TOKEN@gitee.com/team/repo', 'https://gitee.com/team/repo?token=SYNTHETIC_TOKEN',
  'https://gitee.com/team/repo#SYNTHETIC_TOKEN', 'https://gitee.com/team/%2e%2e/repo', 'https://gitee.com/team%2Frepo', 'file:///tmp/repo',
  'javascript:alert(1)', 'http://user:SYNTHETIC_TOKEN@git.internal/Team/other', 'http://git.internal/Team/repo?token=SYNTHETIC_TOKEN',
  'https://user:SYNTHETIC_TOKEN@git.internal/Team/repo', 'https://github.com/example/evidence-repo.git/', 'git@github.com:example/evidence-repo.git',
  'https://gitee.com/example/evidence-repo.git/',
  // §7 probes: scheme and host case, IDNA, IPv4 shorthand, default and invalid ports, the ssh opaque host, IPv6, trailing dot
  'HTTPS://GitHub.COM/Example/Repo.GIT', 'https://gitee.com/example/Repo.GIT', 'https://gitlab.com/example/Repo.GIT', 'https://bücher.example/team/repo',
  'https://BÜCHER.example/team/repo', 'https://xn--bcher-kva.example/team/repo', 'https://ｇｉｔｈｕｂ.com/example/repo', 'https://github。com/example/repo',
  'https://github%2Ecom/example/repo', 'https://git%20hub.com/x/y', 'https://git%zzhub/x/y', 'https://git%25hub/x/y', 'https://-bücher.example/x/y',
  'https://bücher-.example/x/y', 'https://ab--ü.example/x/y', `https://ü${'a'.repeat(70)}.example/x/y`, 'https://-bucher.example/x/y', 'https://bü-cher.example/x/y', 'https://xn--zz.example/x/y', 'https://a\u200db.example/x/y', 'https://a\u00adb.example/x/y', 'https://%C2%AD/x/y',
  'https://bücher..example/o/r', 'https://xn--bcher-kva..example/o/r',
  'https://127.1/team/repo', 'https://0x7f.1/x/y', 'https://0177.0.0.1/x/y', 'https://1.2.3.4.5/x/y', 'https://256.1.1.1/x/y', 'https://1.2.3.256/x/y',
  'https://4294967295/x/y', 'https://4294967296/x/y', 'https://github.com.1/x/y', 'https://1.2.3.4./x/y', 'https://0x/x/y', 'https://１２７.１/x/y',
  'https://github.com:443/example/repo', 'http://github.com:80/example/repo', 'https://github.com:80/example/repo', 'http://github.com/example/repo',
  'https://github.com:0443/example/repo', 'https://github.com:/example/repo', 'https://github.com:abc/example/repo', 'https://github.com:65535/x/y',
  'https://github.com:65536/x/y', 'https://github.com:99999999999999999999/x/y', 'https://github.com:-1/x/y',
  'ssh://git@github.com:22/example/repo', 'ssh://git@github.com:022/example/repo', 'ssh://git@github.com:/example/repo', 'ssh://git@GitHub.com/example/repo',
  'ssh://git@GitHub.com:22/Example/Repo.git', 'ssh://git@bücher.example/x/y', 'git@bücher.example:x/y', 'git@GitHub.com:Example/Repo.GIT',
  'ssh://git@git%41.internal/x/y', 'ssh://git@a<b/x/y', 'ssh://git@a^b/x/y', 'ssh://git@a|b/x/y', 'ssh://git@[::1]/x/y', 'ssh://git@[::1]:2222/x/y',
  'https://[::1]/team/repo', 'https://[::1]:443/x/y', 'https://[::1]:8443/x/y', 'https://[::FFFF:1.2.3.4]/x/y', 'https://[2001:DB8:0:0:1:0:0:1]/x/y',
  'https://[2001:db8::1:0:0:1]/x/y', 'https://[1:0:0:2:0:0:0:3]/x/y', 'https://[::1%25eth0]/x/y', 'https://[::1/x/y', 'https://[::1]x/x/y', 'https://[:::1]/x/y', 'https://[[::1]]/x/y',
  'https://github.com./example/repo', 'https://gitlab.com./example/repo', 'https://hasOwnProperty/x/y', 'https://toString/x/y',
  // credentials and userinfo shapes
  'https://user:pw@github.com/x/y', 'https://user@github.com/x/y', 'https://:pw@github.com/x/y', 'https://@github.com/x/y', 'https://:@github.com/x/y',
  'https://a@b@github.com/x/y', 'ssh://git:pw@github.com/x/y', 'ssh://git:@github.com/x/y', 'ssh://user@github.com/x/y', 'ssh://github.com/x/y',
  'ssh://GIT@github.com/x/y', 'ssh://%67it@github.com/x/y', 'ssh://a@git@github.com/x/y', 'ssh://git@/x/y', 'https://user@/x/y', 'git@host@x:y/z',
  'git@github.com:/x/y', 'git@github.com:x', 'git@:x/y', 'https://git@github.com/x/y',
  // trimming, path segments and decoding
  '  https://github.com/x/y  ', '\ufeffhttps://github.com/x/y', 'https://github.com/x/y\n', '\u00a0https://github.com/x/y', 'https://github.com/x/y\u3000',
  'https://github.com/', 'https://github.com', 'https://github.com/x/y/', 'https://github.com/x/y//', 'https://github.com/x//y', 'https://github.com/x/./y',
  'https://github.com/x/.git', 'https://github.com/x/..git', 'https://github.com/x/y.git.git', 'https://github.com/x/%2Egit', 'https://gitee.com/x/.git',
  'https://git.internal/x/.git', 'https://git.internal/team/%zz', 'https://git.internal/team%2Frepo', 'https://git.internal/team%5Crepo',
  'https://git.internal/a/b%20c', 'https://git.internal/a/%E2%80%A8', 'https://git.internal/a/b%3Fc', 'https://git.internal/a/b%23c', 'https://git.internal/a/%00',
  'https://git.internal/a/%C0%AF', 'https://git.internal/a/%ED%A0%80', 'https://git.internal/a/%F0%9F%98%80', "https://git.internal/a/b'c(d)!*~",
  'https://git.internal/中文/仓库', 'https://git.internal/%E4%B8%AD%e6%96%87/repo', 'https://git.internal/a/%2e', 'https://git.internal/a/%2E%2E',
  'git@git.internal:a/%2e%2e', 'git@git.internal:a/b%20c', 'git@git.internal:中文/仓库', 'https://github.com/İstanbul/Repo',
  'https://gitlab.com/ΟΔΟΣ/Repo', 'https://github.com/A%CE%A3/x', 'https://git.internal/ΟΔΟΣ/Repo', 'ftp://github.com/x/y', 'HTTP://git.internal/x/y',
  'Ssh://git@git.internal/x/y', 'git@git.internal:x/y.GIT', 'GIT@github.com:x/y',
];
const rlHints = [undefined, 'github', 'gitee', 'gitlab'];
const rlParse = (value, authored, provider) => {
  const r = RL.parseRepositoryRemote(value, { authored, provider });
  return r && { ...r, pathKind: JSON.parse(r.identity)[2] };
};
// Rows the port cannot match (stated deviation, README): kept, flagged, and asserted to differ by the C# test. WHATWG runs UTS #46
// with CheckHyphens and VerifyDnsLength off; IdnMapping always applies both, so a non-ASCII label with a leading or trailing hyphen,
// or longer than 63 octets, parses in Node and is refused (null) by the port.
// IdnMapping.GetAscii also throws on an empty label, which WHATWG accepts: two more deviation rows.
const rlDeviations = new Set(['https://-bücher.example/x/y', 'https://bücher-.example/x/y', `https://ü${'a'.repeat(70)}.example/x/y`,
  'https://bücher..example/o/r', 'https://xn--bcher-kva..example/o/r']);
for (const v of rlDeviations) if (!rlValues.includes(v) || !RL.parseRepositoryRemote(v)) throw new Error(`repository-location: deviation row ${v} is not a parsing row`);
write('fixtures/js/repository-location.json', {
  parse: rlValues.map((value) => ({
    value, ...(rlDeviations.has(value) ? { expectDeviation: true } : {}),
    results: [false, true].flatMap((authored) => rlHints.map((provider) => ({ authored, provider: provider ?? null, result: rlParse(value, authored, provider) }))),
  })),
  redact: [null, '', ...rlValues, 'https://u:p@h/x@y/z', 'HTTPS://u@h/x', 'https://h/x?a#b', 'https://h/x#a\nb', 'https://h/x?\n', 'git@h:x?y',
    'ssh://@h/x', 'ftp://u@h/x', 'https://u@h'].map((value) => ({ value, redacted: RL.redactRepositoryRemote(value) })),
  sourceHref: [
    ['github', 'https://github.com/example/evidence-repo', { path: 'src/router.js', line: 1, endLine: 3 }],
    ['github', 'https://github.com/example/evidence-repo', { path: 'src/store.js', line: 1 }],
    ['github', 'https://github.com/example/evidence-repo', { path: 'src/store.js' }],
    ['github', 'https://github.com/example/evidence-repo', { path: 'src/store.js', line: 2, endLine: 2 }],
    ['github', 'https://github.com/example/evidence-repo', { path: 'src/中文 # router.js', line: 1, endLine: 2 }],
    ['gitee', 'https://gitee.com/example/evidence-repo', { path: 'src/中文 # router.js', line: 1, endLine: 2 }],
    ['gitee', 'https://gitee.com/example/evidence-repo', { path: 'src/router.js', line: 1, endLine: 3 }],
    ['gitee', 'https://gitee.com/example/evidence-repo', { path: "src/(x)!'*~.js" }],
    ['gitlab', 'https://gitlab.com/example/platform/evidence-repo', { path: 'src/router.js', line: 1, endLine: 3 }],
    ['gitlab', 'https://gitlab.com/example/platform/evidence-repo', { path: 'docs/guide.md', line: 1, endLine: 3 }],
    ['gitlab', 'https://gitlab.com/example/platform/evidence-repo', { path: 'docs/guide.md' }],
    ['gitlab', 'https://gitlab.com/example/platform/evidence-repo', { path: 'docs/GUIDE.Markdown', line: 4 }],
    ['gitlab', 'https://gitlab.com/example/platform/evidence-repo', { path: 'docs/guide.md', line: 5, endLine: 5 }],
    ['gitlab', 'https://gitlab.com/example/platform/evidence-repo', { path: 'docs/guide.mdx', line: 1 }],
    ['gitlab', 'https://git.example.internal/Platform/Services/evidence-repo', { path: 'src/a b%.js', line: 1, endLine: 3 }],
  ].map(([provider, url, source]) => ({ provider, url, revision: 'f'.repeat(40), source, href: RL.repositorySourceHref(provider, url, 'f'.repeat(40), source) })),
  treeHref: [['github', 'https://github.com/example/evidence-repo'], ['gitee', 'https://gitee.com/example/evidence-repo'],
    ['gitlab', 'https://gitlab.com/example/platform/evidence-repo'], [null, 'git@git.internal:Team/repo']]
    .map(([provider, url]) => ({ provider, url, revision: 'a'.repeat(40), href: RL.repositoryTreeHref(provider, url, 'a'.repeat(40)) })),
});

// --- phase 4: repository evidence (survey-evidence §1b, §5, §6). The repo is built from the checked-in fast-import stream the same way
// the C# EvidenceRepo helper builds it (GIT_* scrubbed, no system/global config). Verification runs in a child node with
// ARCHIFY_DIAGNOSTIC_FORMAT=json: diagnostics.mjs reads it once at import, and this process imported it long ago. Absolute temp roots
// are replaced by <ROOT> in every parsed string value, in both the OS-separator and forward-slash (git) spellings; the C# test does
// the same. scenarios.json carries each input; the outcome is `<name>.payload.json` or `<name>.expected.json` ({ diagnostics }).
const EVIDENCE_HEAD = '77eb757055fa2c17900278114600cfe23c3957d1';   // EvidenceRepo.PinnedHead
const gitEnv = Object.fromEntries(Object.entries(process.env).filter(([k]) => !k.toUpperCase().startsWith('GIT_')));
Object.assign(gitEnv, { GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null' });
const evidenceTemps = [];
const evidenceTemp = () => { const d = fs.mkdtempSync(path.join(os.tmpdir(), 'archify-evidence-')); evidenceTemps.push(d); return d; };
function evidenceRepo(origin) {
  const root = evidenceTemp();
  const git = (args, input) => execFileSync('git', ['-C', root, ...args], { env: gitEnv, input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] }).trim();
  git(['init', '--quiet', '--template=', '--initial-branch=main']);
  git(['fast-import', '--quiet'], fs.readFileSync(out('fixtures/evidence/evidence-repo.fi')));
  if (origin) git(['remote', 'add', 'origin', origin]);
  const head = git(['rev-parse', 'HEAD']);
  if (head !== EVIDENCE_HEAD) throw new Error(`evidence-repo.fi builds ${head}, expected ${EVIDENCE_HEAD}`);
  return root;
}
const GH = { url: 'https://github.com/example/evidence-repo', revision: EVIDENCE_HEAD };
const GH_ORIGIN = 'git@github.com:example/evidence-repo.git';
const GITLAB = { url: 'https://gitlab.com/example/platform/evidence-repo', revision: EVIDENCE_HEAD };
const GITLAB_ORIGIN = 'git@gitlab.com:example/platform/evidence-repo.git';
const ROUTER_STORE = [{ path: 'src/router.js', line: 1, end_line: 3, label: 'Request router' }, { path: 'src/store.js', line: 1 }];   // T/helpers fixture
const webAppIds = ex('web-app.architecture.json').components.slice(0, 2).map(({ id }) => id);
// An architecture doc reduced to what the verifier reads: meta.repository and the first two components' ids (plus sources).
const evArch = (repository, first, second) => ({ meta: repository ? { repository } : {},
  components: [first, second].map((sources, i) => ({ id: webAppIds[i], ...(sources ? { sources } : {}) })) });
const evOne = (collection, id, repository, sources) => ({ meta: { repository }, [collection]: [{ id, sources }] });
// [name, type, origin, root, diagram, expected]: root 'repo' | 'src' | 'empty' | 'missing' | ''; expected 'payload' or the code list.
const evidenceScenarios = [
  ['github-web', 'architecture', GH_ORIGIN, 'repo', evArch(GH, ROUTER_STORE), 'payload'],
  // T/repository-evidence-types.test.mjs:261-302 provider rotation (gitee, gitlab, local-only), on three node collections
  ['gitee-web', 'architecture', 'git@gitee.com:example/evidence-repo.git', 'repo',
    evArch({ url: 'https://gitee.com/example/evidence-repo', provider: 'gitee', revision: EVIDENCE_HEAD }, ROUTER_STORE), 'payload'],
  ['gitlab-nested-web', 'workflow', GITLAB_ORIGIN, 'repo', evOne('nodes', 'user', GITLAB, ROUTER_STORE), 'payload'],
  ['local-only', 'sequence', 'http://git.internal:3000/Platform/evidence-repo', 'repo', evOne('participants', 'user',
    { url: 'http://git.internal:3000/Platform/evidence-repo', link_mode: 'local-only', revision: EVIDENCE_HEAD }, ROUTER_STORE), 'payload'],
  ['cjk-path', 'architecture', GH_ORIGIN, 'repo', evArch(GH, [{ path: 'src/中文 # router.js', line: 2 }]), 'payload'],   // T/…:465-482
  ['md-gitlab-plain', 'erd', GITLAB_ORIGIN, 'repo', evOne('entities', 'customer', GITLAB, [{ path: 'docs/readme.md', line: 2 }]), 'payload'],
  ['duplicate-citation', 'architecture', GH_ORIGIN, 'repo', evArch(GH, [{ path: 'src/store.js' }, { path: 'src/store.js', line: 1 }]), 'payload'],
  ['repository-required', 'architecture', GH_ORIGIN, 'repo', evArch(null, ROUTER_STORE), ['repository-evidence/repository-required']],
  ['root-required', 'architecture', GH_ORIGIN, '', evArch(GH, ROUTER_STORE), ['repository-evidence/root-required']],
  ['url-invalid-local-path', 'architecture', GH_ORIGIN, 'repo', evArch({ url: 'C:\\Users\\x\\evidence-repo', revision: EVIDENCE_HEAD }, ROUTER_STORE),
    ['repository-evidence/url-invalid']],
  ['provider-invalid', 'architecture', GH_ORIGIN, 'repo', evArch({ ...GH, provider: 'gitlab' }, ROUTER_STORE), ['repository-evidence/provider-invalid']],
  ['links-unsupported', 'architecture', GH_ORIGIN, 'repo', evArch({ url: 'http://git.internal:3000/Platform/evidence-repo', revision: EVIDENCE_HEAD }, ROUTER_STORE),
    ['repository-evidence/links-unsupported']],
  ['root-missing', 'architecture', GH_ORIGIN, 'missing', evArch(GH, ROUTER_STORE), ['repository-evidence/root-unreadable']],
  ['not-a-repo', 'architecture', GH_ORIGIN, 'empty', evArch(GH, ROUTER_STORE), ['repository-evidence/git-command']],
  ['subdirectory-root', 'architecture', GH_ORIGIN, 'src', evArch(GH, ROUTER_STORE), ['repository-evidence/root-not-top-level']],
  ['no-origin', 'architecture', null, 'repo', evArch(GH, ROUTER_STORE), ['repository-evidence/git-command']],
  ['origin-mismatch', 'architecture', 'git@github.com:example/other.git', 'repo', evArch(GH, ROUTER_STORE), ['repository-evidence/origin-mismatch']],
  ['revision-unavailable', 'architecture', GH_ORIGIN, 'repo', evArch({ ...GH, revision: '0'.repeat(40) }, ROUTER_STORE), ['repository-evidence/revision-unavailable']],
  ['missing-then-escape', 'architecture', GH_ORIGIN, 'repo', evArch(GH, [{ path: 'src/nope.js' }, { path: '../x' }]),   // T/…:37-49
    ['repository-evidence/file-missing', 'repository-evidence/path-escape']],
  ['two-out-of-range', 'architecture', GH_ORIGIN, 'repo',   // T/…:51-78
    evArch(GH, [{ path: 'src/router.js', line: 1, end_line: 99 }], [{ path: 'src/store.js', line: 1, end_line: 9 }]),
    ['repository-evidence/line-out-of-range', 'repository-evidence/line-out-of-range']],
  ['path-invalid', 'architecture', GH_ORIGIN, 'repo', evArch(GH, [{ path: 'src\\router.js' }]), ['repository-evidence/path-invalid']],
  ['directory-path', 'architecture', GH_ORIGIN, 'repo', evArch(GH, [{ path: 'src', line: 1 }]), ['repository-evidence/file-missing']],
  ['line-required', 'architecture', GH_ORIGIN, 'repo', evArch(GH, [{ path: 'src/router.js', end_line: 2 }]), ['repository-evidence/line-required']],
  ['line-range-invalid', 'architecture', GH_ORIGIN, 'repo', evArch(GH, [{ path: 'src/router.js', line: 3, end_line: 1 }]),
    ['repository-evidence/line-range-invalid']],
  ['source-required', 'architecture', GH_ORIGIN, 'repo', evArch(GH), ['repository-evidence/source-required']],
  // TEST-003 (survey-test3 §4): the per-file fallbacks and object-unavailable, through damaged or partial repos.
  ['object-unavailable-partial', 'architecture', GH_ORIGIN, 'partial', evArch(GH, ROUTER_STORE),
    ['repository-evidence/object-unavailable', 'repository-evidence/object-unavailable']],
  ['object-unavailable-path-only', 'architecture', GH_ORIGIN, 'partial', evArch(GH, [{ path: 'src/router.js' }]), ['repository-evidence/object-unavailable']],
  ['batch-check-fallback', 'architecture', GH_ORIGIN, 'bad-type', evArch(GH, [{ path: 'src/router.js' }, { path: 'src/store.js', line: 9 }]),
    ['repository-evidence/object-unavailable', 'repository-evidence/line-out-of-range']],
  ['content-batch-fallback', 'architecture', GH_ORIGIN, 'truncated', evArch(GH, ROUTER_STORE), ['repository-evidence/file-unreadable']],
  ['content-batch-path-only', 'architecture', GH_ORIGIN, 'truncated', evArch(GH, [{ path: 'src/router.js' }]), 'payload'],
];
// Per-file fallback roots (TEST-003, survey-test3 §4). fast-import wrote loose objects, so one object file can be damaged in place.
const ROUTER_BLOB = '.git/objects/9e/396e4ede13a17cccc016bf524c97bd8e2efee4';
function damageRouterBlob(repo, damage) {
  const file = path.join(repo, ROUTER_BLOB);
  const inflated = zlib.inflateSync(fs.readFileSync(file));   // "blob 54\0" + 54 content bytes
  fs.chmodSync(file, 0o666);   // git writes loose objects read-only
  fs.writeFileSync(file, damage(inflated));
}
// Header type changed to an unknown one: cat-file --batch-check dies with "invalid object type".
const badTypeRoot = (repo) => damageRouterBlob(repo, (b) => zlib.deflateSync(Buffer.concat([Buffer.from('bogus'), b.subarray(4)])));
// A hand-built zlib stored stream (78 01, final stored block 01 LEN ~LEN) cut after 10 content bytes: the size is readable, the body is not.
const truncatedRoot = (repo) => damageRouterBlob(repo, (b) => Buffer.concat([Buffer.from([0x78, 0x01, 0x01, b.length & 255, b.length >> 8, ~b.length & 255, (~b.length >> 8) & 255]), b]).subarray(0, 2 + 5 + 9 + 10));
// Blobs filtered out of a clone of the repo: every cited object is unavailable (uploadpack.allowFilter must be set on the source).
function partialRoot(repo, origin) {
  const git = (cwd, args) => execFileSync('git', ['-C', cwd, ...args], { env: gitEnv, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] });
  git(repo, ['config', 'uploadpack.allowFilter', 'true']);
  const clone = evidenceTemp();
  git(clone, ['clone', '--quiet', '--filter=blob:none', '--no-checkout', pathToFileURL(repo).href, clone]);
  git(clone, ['remote', 'set-url', 'origin', origin]);
  return clone;
}
const evidenceJobs = evidenceScenarios.map(([, type, origin, rootKind, diagram]) => {
  const repo = evidenceRepo(origin);
  const empty = rootKind === 'empty' ? evidenceTemp() : null;
  const partial = rootKind === 'partial' ? partialRoot(repo, origin) : null;
  if (rootKind === 'src') fs.mkdirSync(path.join(repo, 'src'));
  if (rootKind === 'bad-type') badTypeRoot(repo);
  if (rootKind === 'truncated') truncatedRoot(repo);
  const root = { repo, src: path.join(repo, 'src'), empty, partial, missing: `${repo}-missing`, '': '' }[rootKind] ?? repo;
  return { type, diagram, root, base: partial ?? empty ?? repo };
});
const evidenceChild = `
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';
const up = process.argv[1];
const { verifyRepositoryEvidence } = await import(pathToFileURL(up + '/renderers/shared/repository-evidence.mjs').href);
const { withIsolatedDiagnosticRecording } = await import(pathToFileURL(up + '/renderers/shared/diagnostics.mjs').href);
const results = JSON.parse(fs.readFileSync(0, 'utf8')).map(({ type, diagram, root }) => {
  try { return { payload: withIsolatedDiagnosticRecording(() => verifyRepositoryEvidence(type, diagram, root)) }; }
  catch (e) { if (!e.archifyDiagnostics) throw e; return { diagnostics: e.archifyDiagnostics }; }
});
process.stdout.write(JSON.stringify(results));`;
const evidenceResults = JSON.parse(execFileSync(process.execPath, ['--input-type=module', '-e', evidenceChild, UP],
  { input: JSON.stringify(evidenceJobs), encoding: 'utf8', env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' }, maxBuffer: 64 * 1024 * 1024 }));
const tokenise = (v, base) => typeof v === 'string' ? v.replaceAll(base, '<ROOT>').replaceAll(base.replaceAll('\\', '/'), '<ROOT>')
  : Array.isArray(v) ? v.map((x) => tokenise(x, base))
  : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, tokenise(x, base)])) : v;
const allStrings = (v) => typeof v === 'string' ? [v] : v && typeof v === 'object' ? Object.values(v).flatMap(allStrings) : [];
const tmpSpellings = [os.tmpdir(), os.tmpdir().replaceAll('\\', '/')].map((s) => s.toLowerCase());
evidenceScenarios.forEach(([name, , , , , expected], i) => {
  const result = tokenise(evidenceResults[i], evidenceJobs[i].base);
  const got = result.payload ? 'payload' : result.diagnostics.map(({ code }) => code);
  if (JSON.stringify(got) !== JSON.stringify(expected)) throw new Error(`evidence ${name}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(got)}`);
  if (allStrings(result).some((s) => tmpSpellings.some((t) => s.toLowerCase().includes(t)))) throw new Error(`evidence ${name}: an absolute temp path survived tokenisation`);
  if (result.payload) write(`fixtures/evidence/${name}.payload.json`, result.payload);
  else write(`fixtures/evidence/${name}.expected.json`, { diagnostics: result.diagnostics });
});
write('fixtures/evidence/scenarios.json', evidenceScenarios.map(([name, type, origin, root, diagram]) => ({ name, type, origin, root, diagram })));
// git-absent: github-web again, its child's PATH reduced to node's own directory (C#: a GitRunner pointed at a missing executable).
const noGitEnv = Object.fromEntries(Object.entries(cleanEnv).filter(([k]) => k.toUpperCase() !== 'PATH'));
noGitEnv.PATH = path.dirname(process.execPath);
const [absent] = JSON.parse(execFileSync(process.execPath, ['--input-type=module', '-e', evidenceChild, UP],
  { input: JSON.stringify([evidenceJobs[0]]), encoding: 'utf8', env: { ...noGitEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json' } }));
if (absent.diagnostics?.[0]?.evidence?.reason !== 'spawnSync git ENOENT') throw new Error(`evidence git-absent: ${JSON.stringify(absent)}`);
write('fixtures/evidence/git-absent.expected.json', { diagnostics: tokenise(absent.diagnostics, evidenceJobs[0].base) });
// Task 6: per-type evidence goldens. Each type's example from T/repository-evidence-types.test.mjs:19-30 gets meta.repository (github,
// fixture HEAD) and the T/helpers sources on its first node (:83-91), and renders through cli.mjs:85 with ARCHIFY_REPO_ROOT, which is
// how `archify render --repo-root` reaches the renderer (bin/archify.mjs:2394). Inputs `<name>.<type>.json`, goldens `<name>-rendered.html`.
const EVIDENCE_TYPES = [   // [type, collection, example, first], T/repository-evidence-types.test.mjs:19-30
  ['architecture', 'components', 'web-app.architecture.json', 'users'], ['workflow', 'nodes', 'agent-tool-call.workflow.json', 'user'],
  ['sequence', 'participants', 'cache-miss-request.sequence.json', 'user'], ['dataflow', 'nodes', 'product-analytics.dataflow.json', 'web'],
  ['lifecycle', 'states', 'agent-run.lifecycle.json', 'queued'], ['erd', 'entities', 'orders.erd.json', 'customer'],
  ['tree', 'nodes', 'payment-platform.tree.json', 'platform'], ['class', 'types', 'payments.class.json', 'order_service'],
  ['timeline', 'events', 'payment-incident.timeline.json', 'deploy'], ['waterfall', 'spans', 'checkout-request.waterfall.json', 'request'],
];
const withSources = (d, collection, first, sources = ROUTER_STORE) => {
  d.meta.repository = { ...GH };
  const node = d[collection].find((candidate) => candidate.id === first);
  if (!node) throw new Error(`evidence golden: ${collection} has no ${first}`);
  node.sources = structuredClone(sources);
  return d;
};
const evidenceGoldens = EVIDENCE_TYPES.map(([type, collection, example, first]) => [type, `${type}.evidence`, withSources(ex(example), collection, first)]);
// The compact-leading fallback (text-fit.mjs:80-96): a 27-unit label on the first state. At 12px it needs 194.4px, so the state hits
// the 220px cap and keeps 142.4px between the step (35.6) and the source badge (178): the label moves below the decoration rail. Three
// rows then end at 68.5 > height - 2 = 62, and compact leading ends at 60.7 <= 63.5. The probe below proves the branch is taken.
const narrow = withSources(ex('agent-run.lifecycle.json'), 'states', 'queued');
narrow.states[0].label = 'Queued awaiting worker slot';
evidenceGoldens.push(['lifecycle', 'lifecycle.evidence-narrow', narrow]);
// The per-type workflow and lifecycle goldens differ from their plain goldens only in the payload: their first labels fit beside the
// badge. Two more pin the badge's geometry. preferredWidth (render-lifecycle.mjs:80): 'Queued job' needs 72 + 35.6 + 4 + 38 + 4,
// so the state is 154px wide, 140 without the badge. The workflow rail (workflow-compiler.mjs:5413): holiday-planning's 92px `today`
// already puts its label below the rail; with the badge its rows reach compact leading too (probed below).
const widthDoc = withSources(ex('agent-run.lifecycle.json'), 'states', 'queued');
widthDoc.states[0].label = 'Queued job';
evidenceGoldens.push(['lifecycle', 'lifecycle.evidence-width', widthDoc], ['workflow', 'workflow.evidence-shift', withSources(ex('holiday-planning.workflow.json'), 'nodes', 'today')]);
const evidenceRoot = evidenceRepo(GH_ORIGIN);
for (const [type, name, diagram] of evidenceGoldens) {
  write(`fixtures/diagrams/${name}.${type}.json`, diagram);
  const golden = out(`goldens/${name}-rendered.html`);
  renderEnv(type, out(`fixtures/diagrams/${name}.${type}.json`), golden, { ARCHIFY_REPO_ROOT: evidenceRoot });
  const html = fs.readFileSync(golden, 'utf8');
  if (!html.includes('<script id="archify-source-evidence-data" type="application/json">{')) throw new Error(`evidence golden ${name}: no payload`);
  if (tmpSpellings.some((t) => html.toLowerCase().includes(t))) throw new Error(`evidence golden ${name}: the temp root leaked into the HTML`);
  writeCheckerReport(golden);
}
// Coverage probe: text-fit.mjs with a tag call at the compact block and at its return, served by a resolve hook from a data: URL
// (relative imports made absolute) to the real lifecycle renderer in a child node. Upstream is never modified.
const tfPath = path.join(UP, 'renderers/shared/text-fit.mjs');
const tfUrl = pathToFileURL(tfPath).href;
let tfProbeSrc = fs.readFileSync(tfPath, 'utf8').replace(/\r\n/g, '\n').replace(/from '(\.[^']+)'/g, (_, rel) => `from '${new URL(rel, tfUrl).href}'`);
for (const [tag, anchor, before] of [['compact-block', '  if (source) {\n', false], ['compact-return', '      result.ys = compactYs;\n', true]]) {
  const count = tfProbeSrc.split(anchor).length - 1;
  if (count !== 1) throw new Error(`text-fit probe anchor ${tag}: ${count} occurrences, expected 1`);
  const tagCall = `globalThis.__tfcov['${tag}'] = (globalThis.__tfcov['${tag}'] ?? 0) + 1;\n`;
  tfProbeSrc = tfProbeSrc.replace(anchor, () => (before ? tagCall + anchor : anchor + tagCall));
}
const tfProbeChild = String.raw`
import { registerHooks } from 'node:module';
const [tfUrl, dataUrl, renderer, input, output] = process.argv.slice(1);
registerHooks({ resolve(specifier, context, next) { const r = next(specifier, context); return r.url === tfUrl ? { url: dataUrl, shortCircuit: true } : r; } });
globalThis.__tfcov = {};
process.argv = [process.argv[0], renderer, input, output];
await import(renderer);
process.stdout.write('\nTFCOV ' + JSON.stringify(globalThis.__tfcov) + '\n');`;
const tfProbeOut = path.join(evidenceTemp(), 'probe.html');
for (const [type, name] of [['lifecycle', 'lifecycle.evidence-narrow'], ['workflow', 'workflow.evidence-shift']]) {
  const stdout = execFileSync(process.execPath, ['--input-type=module', '-e', tfProbeChild,
    tfUrl, `data:text/javascript;base64,${Buffer.from(tfProbeSrc).toString('base64')}`, pathToFileURL(path.join(UP, `renderers/${type}/render-${type}.mjs`)).href,
    out(`fixtures/diagrams/${name}.${type}.json`), tfProbeOut],
  { encoding: 'utf8', env: { ...cleanEnv, ARCHIFY_REPO_ROOT: evidenceRoot }, stdio: ['ignore', 'pipe', 'inherit'] });
  const tfCoverage = JSON.parse(/TFCOV (.*)/.exec(stdout)[1]);
  if (!(tfCoverage['compact-return'] > 0)) throw new Error(`${name} misses the compact-leading return: ${JSON.stringify(tfCoverage)}`);
  if (fs.readFileSync(tfProbeOut, 'utf8') !== fs.readFileSync(out(`goldens/${name}-rendered.html`), 'utf8')) throw new Error(`text-fit probe render of ${name} differs from the golden`);
  console.log(`text-fit probe (${name}): ${JSON.stringify(tfCoverage)}`);
}
// A workflow fault whose node-overlap fixes are verified by recompiles (acceptsFix) while evidence is present: wf-node-overlap plus
// meta.repository and a source on nodes[0]. Without both and a root, evidence fails first (repository-required / root-required).
const overlapEvidence = withSources(JSON.parse(fs.readFileSync(out('fixtures/diagrams/wf-node-overlap.workflow.json'), 'utf8')), 'nodes', 'today',
  [{ path: 'src/router.js', line: 1 }]);
if (overlapEvidence.nodes[0].id !== 'today') throw new Error('wf-node-overlap: nodes[0] is no longer today');
write('fixtures/diagrams/wf-node-overlap.evidence.workflow.json', overlapEvidence);
let overlapReceipt;
try {
  execFileSync(process.execPath, [path.join(UP, 'renderers/workflow/render-workflow.mjs'), out('fixtures/diagrams/wf-node-overlap.evidence.workflow.json'), tfProbeOut],
    { env: { ...cleanEnv, ARCHIFY_DIAGNOSTIC_FORMAT: 'json', ARCHIFY_REPO_ROOT: evidenceRoot }, stdio: ['ignore', 'pipe', 'pipe'] });
} catch (e) { if (e.status !== 1 || !e.stderr) throw e; overlapReceipt = tokenise(JSON.parse(String(e.stderr)), evidenceRoot); }
if (!overlapReceipt?.diagnostics?.some((x) => x.code === 'workflow/node-overlap' && x.supportedFixes.some((f) => f.includes('verified free column')))) {
  throw new Error(`wf-node-overlap.evidence: expected workflow/node-overlap with a recompile-verified fix, got ${JSON.stringify(overlapReceipt)}`);
}
if (allStrings(overlapReceipt).some((s) => tmpSpellings.some((t) => s.toLowerCase().includes(t)))) throw new Error('wf-node-overlap.evidence: temp path in receipt');
write('fixtures/diagrams/wf-node-overlap.evidence.workflow.expected.json', overlapReceipt);
for (const d of evidenceTemps) fs.rmSync(d, { recursive: true, force: true });
// --- Intl.Segmenter word granularity (label-capacity.mjs:32-33) on every string a phase-3 label can carry + a battery. `dictionary`
// rows (Han/Kana/Thai/Lao/Khmer/Myanmar) are parity-exempt: C# asserts concatenation and per-grapheme splitting of those runs only. ---
const wordSeg3 = new Intl.Segmenter(undefined, { granularity: 'word' });
const DICT = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Thai}\p{Script=Lao}\p{Script=Khmer}\p{Script=Myanmar}ーｰ]/u;
const battery = [
  'OrderPlaced', 'identity + consent', 'metrics SQL', 'argv / JSON-RPC', 'value · key · TTL', 'response payload v1',
  'api.example.com', 'GET /v1/users?id=42', 's3://bucket/key.parquet', 'user_id', 'snake_case_name', 'x86_64', 'don\'t', 'it\'s',
  'O\'Reilly', '1,000 rows', '3.5 MB', '1.2.3', '10:30', '2026-10-14', '50%', '$100', 'p95 latency', '2xx/5xx', 'C++ code', '#tag',
  '@user', 'e-mail', 'kebab-case', 'wait—what', 'foo...bar', 'a.b.c', 'U.S.A.', 'a:b', 'result:error', '“quoted”', '"dq"',
  'tab\u0009here', 'two  spaces', ' lead', 'trail ', 'naïve café', 'café au lait', 'Ünïcödé', 'ＡＢＣ ｄｅｆ', '한국어 텍스트', '한국어텍스트처리',
  'ZWJ 👨\u200D👩\u200D👧 family', 'emoji 🇩🇪🇫🇷 flags', '❤\uFE0F love', '😀😀', 'a😀b', '👩\u200D💻 / é result',
  'save / copy result', 'v2 API', 'k8s pod', 'A/B test', 'in→out', 'x×y', 'a&b', 'a|b', '(paren) [br]', 'él.está', 'Δx.Δy',
  'שלום עולם', 'מ"מ', 'צה\'\'ל', 'привет мир', 'a\u00ADb', 'a\u200Bb', '1,000,000', '.5', '5.', 'a1.b2', 'ab12_cd', 'x‿y',
  'ＡＢＣ１２３', '١٢٣ عربي', '#\uFE0F\u20E3 key', 'a\u202Fb', 'a\u00A0b', 'a\u000D\u000Ab', 'a\u000Ab', 'a  \u0009 b',
  'v1.2.3-beta+build', 'user@example.com', 'https://example.com/a_b?c=d#e', 'C#', 'F# and .NET', 'Node.js', '3rd-party', '10x',
  'x10', '1st', 'O’Brien', 'rock ’n’ roll', 'i18n', 'e.g.', 'i.e., this', '50/50', '±5', '~/home', 'a—b', 'a–b', 'αβγ δε',
  'Ελληνικά.κείμενο', 'İstanbul', 'straße', 'ǅemal', 'ﬁle', 'a\u0301\u0302b', '😀\u200D😀', '👍🏽 ok', 'a\u200Db', '·', 'a·b·c',
  'x\'', '\'x', 'x.', 'x..y', '1..2', '1.a', 'a.1', '1\'2', 'a\'1', '1_a', '_x_', '__init__',
  // WB4 and Hangul edges where V8 differs from the measured prototype: Extend/Format clusters after a control or a space ride on it,
  // a space carrying one no longer joins the next space (WB3d is pre-WB4), jamo are ALetter and only syllables form ICU's Hangul run.
  'a\u0009\u0301b', 'a\u0001\u0301b', 'a\u00AD\u0301b', 'x \u0301y', 'x \u00ADy', ' \u0301 ', ' \u00AD ', '\u0301ab', 'x\u0009ﾞz',
  '\u0009\u{1F3FD}a', 'ﾞa', 'ㄱㄱ', 'ㄱa', 'ㄱ가', '가ㄱ', '가\u0301가', '가\u2060가',
];
const wordTexts = new Set(battery);
const strWalk = (v) => { if (typeof v === 'string') wordTexts.add(v); else if (v && typeof v === 'object') for (const x of Object.values(v)) strWalk(x); };
for (const f of fs.readdirSync(EX).filter((f) => /\.(workflow|dataflow)\.json$/.test(f))) strWalk(JSON.parse(fs.readFileSync(path.join(EX, f), 'utf8')));
for (const { input } of p3Rows) strWalk(JSON.parse(fs.readFileSync(path.join(ROOT, input), 'utf8')));
write('fixtures/js/word-breaks.json', [...wordTexts].filter((text) => text.length <= 200).sort()
  .map((text) => ({ text, dictionary: DICT.test(text), words: [...wordSeg3.segment(text)].map((s) => s.segment) })));
// --- dataflow label-capacity.mjs: flowLabelSize on a battery, wrapBlockedFlowLabels on the scenes of T/dataflow-label-capacity.test.mjs.
// `dictionary` marks scenes whose label holds a dictionary script (R1): there the port's per-grapheme breaks may pick another split. ---
const lc = await imp('renderers/dataflow/label-capacity.mjs');
const sizeFlows = [
  [{ label: 'x' }], [{ label: 'save / copy result' }], [{ label: '保存 / 复制结果' }], [{ label: '👩‍💻 / é result' }],
  [{ label: 'clickstream events with a rather long description of the payload' }], [{ label: '' }],
  [{ label: 'save', classification: 'private' }], [{ label: 'x', classification: 'a much longer classification text' }],
  [{ label: '复制结果', classification: '机密' }], [{ label: 'save', classification: '' }],
  [{ label: 'save / copy result' }, ['save / ', 'copy result']], [{ label: '保存 / 复制结果', classification: 'pii' }, ['保存 / ', '复制结果']],
  [{ label: 'a b c' }, ['a', 'b', 'c']], [{ label: 'x', classification: 'restricted' }, ['x']],
];
// fixture() verbatim from T/dataflow-label-capacity.test.mjs:6-20; the scene edits verbatim from :22, :43-48, :52-57.
function lcFixture(text = '保存 / 复制结果') {
  const relation = { id: 'output', label: text };
  const size = lc.flowLabelSize(relation);
  return {
    labels: [{ relation, relationIndex: 0, label: text, ...size, lx: 251, ly: 203,
      x: 251 - size.width / 2, y: 192 }],
    routes: [
      { relationIndex: 0, points: [[201, 213], [301, 213]] },
      { relationIndex: 1, points: [[280, 257], [280, 50]] },
      { relationIndex: 2, points: [[42, 161], [279.5, 161]] },
    ],
    components: [{ x: 89, y: 184, width: 112, height: 58 }, { x: 301, y: 184, width: 118, height: 58 }],
    titles: [], viewBox: [468, 355], placementBottom: 281,
  };
}
const lcScenes = [];
for (const text of ['保存 / 复制结果', 'save / copy result', '👩‍💻 / é result']) lcScenes.push([`blocked: ${text}`, lcFixture(text)]);
lcScenes.push(['valid: save', lcFixture('save')]);
for (const key of ['labelAt', 'labelDx', 'labelDy', 'labelSegment']) {
  const scene = lcFixture(); scene.labels[0].relation[key] = key === 'labelAt' ? [251, 203] : 0;
  lcScenes.push([`pinned: ${key}`, scene]);
}
const classified = lcFixture(); classified.labels[0].relation.classification = 'private';
Object.assign(classified.labels[0], lc.flowLabelSize(classified.labels[0].relation));
lcScenes.push(['classified', classified]);
const exhausted = lcFixture(); exhausted.components.push({ x: 205, y: 0, width: 92, height: 300 });
lcScenes.push(['exhausted', exhausted]);
const LABEL_KEYS = new Set(['relation', 'relationIndex', 'label', 'x', 'y', 'width', 'height', 'lx', 'ly', 'lines']);
const labelJson = (rect) => {
  const extra = Object.keys(rect).filter((k) => !LABEL_KEYS.has(k));
  if (extra.length) throw new Error(`label rect carries unported keys: ${extra}`);
  const { relation, ...rest } = rect;
  return { ...rest, relation };
};
write('fixtures/js/label-capacity.json', {
  flowLabelHeight: lc.FLOW_LABEL_HEIGHT,
  sizes: sizeFlows.map(([flow, lines]) => ({ flow, lines: lines ?? null, size: lines ? lc.flowLabelSize(flow, lines) : lc.flowLabelSize(flow) })),
  scenes: lcScenes.map(([name, scene]) => {
    const input = structuredClone(scene);
    const result = lc.wrapBlockedFlowLabels(scene);
    return { name, dictionary: DICT.test(scene.labels[0].label), input: { ...input, labels: input.labels.map(labelJson) },
      output: result.map((rect, i) => ({ same: rect === scene.labels[i], ...labelJson(rect) })) };
  }),
});
// --- dataflow joint-label-routing.mjs: repairLabelRoutes input and output, recorded inside the real compile. An in-thread load hook
// rewrites dataflow-compiler.mjs in memory (a `?joint-capture` instance, so an earlier import cannot serve the unhooked module) to route
// the call through globalThis.__jlr. Relations serialise as {id, from, to, pins}; sideFor becomes a relationIndex -> sides table.
// `same` flags record identity (R9): the compiler adopts a proposal only when `labels !== resolvedLabelRects`. ---
const { registerHooks } = await import('node:module');
const JLR_ANCHOR = 'const proposal = repairLabelRoutes({';
const jlrHook = registerHooks({
  load(url, context, nextLoad) {
    const result = nextLoad(url, context);
    if (!url.endsWith('/renderers/dataflow/dataflow-compiler.mjs?joint-capture')) return result;
    const src = String(result.source);
    if (src.split(JLR_ANCHOR).length !== 2) throw new Error(`anchor not unique: ${JLR_ANCHOR}`);
    return { ...result, source: src.replace(JLR_ANCHOR, () => 'const proposal = globalThis.__jlr(repairLabelRoutes, {'), shortCircuit: true };
  },
});
const { compileDataflow: compileDataflowJoint } = await import(pathToFileURL(path.join(UP, 'renderers/dataflow/dataflow-compiler.mjs')).href + '?joint-capture');
jlrHook.deregister();
// PIN_KEYS (manifest block above) is joint-label-routing.mjs:16-18's `pinned` key set plus `via` and `route`.
const relationJson = (flow) => ({ id: flow.id, from: flow.from, to: flow.to,
  pins: Object.fromEntries(PIN_KEYS.filter((k) => flow[k] !== undefined).map((k) => [k, flow[k]])) });
const jointLabel = (rect) => ({ ...labelJson(rect), relation: relationJson(rect.relation) });
const jointRows = [];
for (const f of fs.readdirSync(out('fixtures/diagrams')).filter((f) => /\.dataflow\.json$/.test(f) && !f.includes('.evidence') && !f.startsWith('t4-')).sort()) {   // evidence inputs: Task 6 goldens, not repair scenes; t4-*: TEST-004 end-to-end receipts (80+ labels would add 6k lines of scene)
  const d = JSON.parse(fs.readFileSync(out(`fixtures/diagrams/${f}`), 'utf8'));
  const calls = [];
  globalThis.__jlr = (fn, args) => {
    const sides = Object.fromEntries(args.routes.map((r) => [r.relationIndex, args.sideFor(r.relation)]));
    const input = JSON.parse(JSON.stringify({
      labels: args.labels.map(jointLabel),
      routes: args.routes.map((r) => ({ relationIndex: r.relationIndex, relation: relationJson(r.relation), points: r.points })),
      nodes: args.nodes.map(({ id, x, y, width, height }) => ({ id, x, y, width, height })),
      titles: args.titles.map(({ x, y, width, height }) => ({ x, y, width, height })),
      frames: args.frames.map(({ id, label, kind, x, y, width, height, radius }) => ({ id, label, kind, x, y, width, height, radius })),
      viewBox: args.viewBox, placementBottom: args.placementBottom, sides,
    }));
    if (args.maximumSearches !== undefined || args.metrics !== undefined) throw new Error('compiler now passes maximumSearches/metrics');
    const metrics = {};
    const result = fn({ ...args, metrics });
    calls.push({ input, output: {
      labelsSame: result.labels === args.labels, routesSame: result.routes === args.routes, metrics,
      labels: result.labels.map((rect, i) => ({ same: rect === args.labels[i], ...jointLabel(rect) })),
      routes: result.routes.map((r, i) => ({ same: r === args.routes[i], relationIndex: r.relationIndex, points: r.points })),
    } });
    return result;
  };
  try { compileDataflowJoint({ dataflow: d, sourceEvidence: null, qualityProfile: d.meta?.quality_profile }); } catch { /* failure fixtures */ }
  for (const [i, call] of calls.entries()) jointRows.push({ name: `${f.replace(/\.dataflow\.json$/, '')}#${i}`, ...call });
}
delete globalThis.__jlr;
for (const name of ['first-draft.joint-label-routing#0', 'first-draft.rejected-grid-recovery#0']) {
  if (jointRows.find((r) => r.name === name)?.output.labelsSame !== false) throw new Error(`repairLabelRoutes: ${name} no longer changes a label`);
}
write('fixtures/js/joint-label-routing.json', jointRows);
// --- workflow model (Task 6): workflow-compiler.mjs's module-level clusters A-D through a shimmed copy (relative imports made absolute,
// so the shim shares every shared/MG module instance with imp(); the private functions exported) written to os.tmpdir(); MG and PR
// imported directly. canonicalReadableWorkflow runs before createReadableLayout on every row: it fills the automaticNodeWidths WeakMap.
// Rows: every manifest workflow input (deduplicated by file), three fault inputs (unknown lane → NaN geometry, semantic codes) and
// crafted edits of holiday-planning / incident-response, embedded as `document` (no new input files). ---
const WM_EXPORTS = ['createReadableLayout', 'canonicalReadableWorkflow', 'semanticContractDiagnostics', 'automaticNodeWidth', 'authoredNodeWidth',
  'stableValueKey', 'compareWorkflowEdges', 'resolveWorkflowLegendFootprint', 'createWorkflowLaneGeometry', 'measureWorkflowNodes',
  'workflowNodeTextProblems', 'createWorkflowStepIndexes', 'createLegacyLayout', 'hasAbsoluteWorkflowPins', 'hasVerticalStack', 'usesIndependentLaneMeasurement'];
const wmShimPath = path.join(os.tmpdir(), 'archify-net-workflow-model-shim.mjs');
fs.writeFileSync(wmShimPath, fs.readFileSync(wfCompilerPath, 'utf8').replace(/\r\n/g, '\n')
  .replace(/from '(\.\.\/shared\/[^']+|\.\/workflow-[^']+)'/g, (_, rel) => `from '${new URL(rel, wfFileUrl).href}'`)
  + `\nexport { ${WM_EXPORTS.join(', ')} };\n`);
const wm = await import(pathToFileURL(wmShimPath).href);
const mg = await imp('renderers/workflow/workflow-migration-geometry.mjs');
const { proposeWorkflowPlacement } = await imp('renderers/workflow/workflow-placement-repair.mjs');
const { registerLocale } = await imp('renderers/shared/i18n.mjs');
const holiday = () => ex('holiday-planning.workflow.json');
const wmCrafted = [
  // compareWorkflowEdges ties on id/from/to/label/route: three id-less today→mid_autumn edges (one with id '') ordered by stableValueKey.
  ['crafted.edge-tiebreak', (() => { const d = holiday(); d.edges.push({ from: 'today', to: 'mid_autumn', variant: 'dashed', width: 2 },
    { width: 1.5, from: 'today', to: 'mid_autumn', variant: 'dashed' }, { id: '', from: 'today', to: 'mid_autumn' }); return d; })()],
  // yOffset stack in makeup:3 (independent lane measurement), a single-column group and phase, a phase range.
  ['crafted.stack-groups', (() => { const d = holiday(); d.nodes[4].yOffset = -30;
    d.nodes.push({ id: 'makeup_extra', lane: 'makeup', col: 3, type: 'security', label: '补班二', tag: 'x', yOffset: 40 });
    d.groups = [{ id: 'g1', label: 'A very long group label that spans', lane: 'makeup', fromCol: 3, toCol: 3 }];
    d.phases = [{ id: 'p1', label: 'Single column phase with a long label', fromCol: 2, toCol: 2 }, { id: 'p0', label: 'Range', fromCol: 0, toCol: 1 }];
    return d; })()],
  // fromSide top on a col-0 node (lane-header shift), a same-lane label over the channel budget, a labeled direct edge under it.
  ['crafted.top-side-labels', (() => { const d = holiday(); d.edges[0].fromSide = 'top';
    d.edges[1].label = 'a long direct label that exceeds the automatic channel detour budget by far';
    d.edges[2].label = 'medium length label'; return d; })()],
  // Two-decimal authored widths (the sizing loop's verifiedReducedWidths output shape): measured cx = colXs[col] differs from
  // x + width / 2 for at least one node (checked below), which pins the stored Box.Cx (R2).
  ['crafted.fractional-widths', (() => { const d = holiday();
    for (const [i, width] of [156.48, 124.32, 150.57, 198.61, 164.6].entries()) d.nodes[i].width = width; return d; })()],
  ['crafted.semantic', (() => { const d = holiday(); d.semanticChecks = { allowedRoots: [], allowedTerminals: ['year_end'],
    requiredEdges: [{ from: 'today', to: 'year_end' }], requiredPaths: [{ from: 'today', to: 'makeup_work' }, { from: 'makeup_work', to: 'today' }] };
    return d; })()],
  // proposeWorkflowPlacement: a return cycle (makeup_work→mid_autumn), a same-lane cyclic branch (retry) and three unconnected
  // mixed-case ids sharing makeup:1, ordered by localeCompare.
  ['crafted.placement', (() => { const d = holiday();
    d.nodes.push({ id: 'retry', lane: 'calendar', col: 2, type: 'backend', label: 'Retry' },
      ...['beta', 'alpha', 'Alpha'].map((id) => ({ id, lane: 'makeup', col: 0, type: 'external', label: id })));
    d.edges.push({ id: 'makeup_return', from: 'makeup_work', to: 'mid_autumn' }, { id: 'to_retry', from: 'national_day', to: 'retry' },
      { id: 'retry_back', from: 'retry', to: 'mid_autumn' }); return d; })()],
  // v1 migration: a straight labeled edge, a kept edge whose labelAt is mapped and whose label planning drops. Planning drops
  // the routed edges, so its projection loses the semanticChecks naming the kept alert→triage edge (#497).
  ['crafted.migration', (() => { const d = ex('incident-response.workflow.json');
    Object.assign(d.edges[6], { route: 'straight', label: 'declare it', width: 2, role: 'branch' });
    d.edges.push({ from: 'alert', to: 'triage', label: 'skip', labelAt: [150.5, 60], width: 1.6 });
    d.semanticChecks = { requiredEdges: [{ from: 'alert', to: 'triage' }] }; return d; })()],
  // T/workflow-migration-semantics.test.mjs "planning keeps semantic contracts when it retains every authored edge".
  ['crafted.migration-kept', (() => { const d = migrationSemanticsDocument({ requiredEdges: [{ from: 'a', to: 'b' }],
    requiredPaths: [{ from: 'a', to: 'b' }], allowedRoots: ['a', 'c', 'obstacle'], allowedTerminals: ['b', 'd', 'obstacle'] });
    d.edges[0] = { id: 'pin-edge', from: 'a', to: 'b', label: 'p', route: 'auto' }; return d; })()],
];
const wmRows = [];
const seenInputs = new Set();
for (const { name, input } of p3Rows.filter((r) => r.type === 'workflow')) {
  if (seenInputs.has(input)) continue;
  seenInputs.add(input);
  wmRows.push({ name, input });
}
for (const name of ['wf-unknown-node-lane', 'wf-semantic-reference', 'wf-required-edge', 'wf-v1-semantic-fallback']) wmRows.push({ name, input: `tests/fixtures/diagrams/${name}.workflow.json` });
for (const [name, document] of wmCrafted) wmRows.push({ name, document: JSON.parse(JSON.stringify(document)) });
const wmDoc = (row) => (row.document ? structuredClone(row.document) : JSON.parse(fs.readFileSync(path.join(ROOT, row.input), 'utf8')));
const WM_FEEDBACK = { rankGapMinimums: { '1:3': 260.5, '0:1': 150.25, '2:3': 133 }, rankGapContributors: { '1:3': ['rank 1→3 route clearance', 'edge x route'] },
  laneGapMin: 31.2, laneGapContributors: ['edge y lane-gap route clearance'] };
const layoutJson = (layout) => ({ ...layout, ...(layout.channelLabelEdgeKeys ? { channelLabelEdgeKeys: [...layout.channelLabelEdgeKeys] } : {}) });
function measuredJson(doc) {
  registerLocale(doc.meta?.locale, doc.meta?.translations);   // as the loader does before any renderer runs
  const layout = doc.schema_version === 2 ? wm.createReadableLayout(doc, {}) : wm.createLegacyLayout();
  const legend = wm.resolveWorkflowLegendFootprint(doc, layout);
  const lanes = wm.createWorkflowLaneGeometry(doc, layout, legend.legendExtraHeight, legend.minimumCanvasWidth);
  const { nodes, nodeTextFit } = wm.measureWorkflowNodes(doc, layout, lanes);
  return {
    legend: { entries: legend.workflowLegendEntries.map((e) => [e.kind, e.label]), present: [...legend.presentLegendKinds],
      packingWidth: legend.legendPackingWidth, rows: legend.packedLegendFootprint.rowCount, extraHeight: legend.legendExtraHeight,
      minimumCanvasWidth: legend.minimumCanvasWidth },
    autoHeight: lanes.autoHeight, initialViewBox: lanes.initialViewBox, laneTops: doc.lanes.map((l) => lanes.laneTop(l.id)),
    laneHeights: doc.lanes.map((_, i) => lanes.laneHeight(i)), lastLaneBottom: lanes.lastLaneBottom(), legendY: lanes.legendY(),
    nodes: [...nodes.values()].map((n) => ({ id: n.id, x: n.x, y: n.y, width: n.width, height: n.height, cx: n.cx, cy: n.cy,
      context: lanes.nodeContext(n), problems: wm.workflowNodeTextProblems(n, nodeTextFit) })),
  };
}
const wmModel = wmRows.map((row) => {
  const doc = wmDoc(row);
  const c = wm.canonicalReadableWorkflow(doc);
  const steps = wm.createWorkflowStepIndexes(c);
  return { ...row,
    canonical: { lanes: c.lanes.map((l) => l.id), nodes: c.nodes.map((n) => n.id), edges: c.edges.map((e) => wm.stableValueKey(e)),
      phases: c.phases?.map((p) => p.id) ?? null, groups: c.groups?.map((g) => g.id) ?? null,
      widths: c.nodes.map((n) => wm.authoredNodeWidth(n)), automatic: c.nodes.map((n) => wm.automaticNodeWidth(n)),
      absolutePins: wm.hasAbsoluteWorkflowPins(c), verticalStack: wm.hasVerticalStack(c), independent: wm.usesIndependentLaneMeasurement(c),
      preserves: mg.preservesWorkflowAbsoluteGeometry(c) },
    semantic: wm.semanticContractDiagnostics(c).map((d) => [d.code, d.subject.path]),
    layout: { none: layoutJson(wm.createReadableLayout(c)), feedback: layoutJson(wm.createReadableLayout(c, WM_FEEDBACK)) },
    measured: measuredJson(c),
    steps: { edges: c.edges.map((e) => steps.edgeSteps.get(e)), nodes: c.nodes.map((n) => steps.nodeStep(n)) },
  };
});
if (!wmModel.find((r) => r.name === 'crafted.fractional-widths').measured.nodes.some((n) => n.x + n.width / 2 !== n.cx)) {
  throw new Error('crafted.fractional-widths: every x + width / 2 equals cx; the stored-cx row pins nothing');
}
// widthOverrides (sizing loop input, :712): the larger of the automatic width and the override wins; 0 deletes the entry.
const overridden = wm.canonicalReadableWorkflow(holiday(), new Map([['today', 150], ['mid_autumn', 40], ['year_end', 0]]));
const WM_LEGACY = [88, 220, 300, 430, 500, 625];
const mapperColumns = [[94, 214, 334, 454, 574, 694], [94.1, 247.2, 367.2, 487.25, 607.3333, 727.0000005]];
const mapperXs = [-40, 0, 87.9999999, 88, 100, 154.3, 219.9999995, 220, 260, 299.5, 300, 365.123456789, 430, 465.4444444, 500, 562.5, 625,
  625.0000001, 700, 1234.5678915, 0.0000004, 333.3333335];
const mapperRow = (oldColumns, newColumns, xs) => {
  try {
    const map = mg.createHorizontalRankMapper(oldColumns, newColumns);
    return { oldColumns, newColumns, xs, outputs: xs.map((x) => { try { return map(x); } catch (e) { return { error: e.message }; } }) };
  } catch (e) { return { oldColumns, newColumns, error: e.message }; }
};
const mgDocs = wmRows.filter((row) => wmDoc(row).schema_version === 1).map((row) => {
  const doc = wmDoc(row);
  const intrinsic = mg.intrinsicWorkflow(doc);
  const planning = mg.planningWorkflow(doc);
  const columns = wm.createReadableLayout({ ...doc, schema_version: 2 }).colXs;
  const mapped = mg.createMappedWorkflowCandidate(doc, WM_LEGACY, columns);
  return { name: row.name, preserves: mg.preservesWorkflowAbsoluteGeometry(doc),
    intrinsic, intrinsicPolicy: mg.preservesWorkflowAbsoluteGeometry(intrinsic), planning, planningPolicy: mg.preservesWorkflowAbsoluteGeometry(planning),
    columns, changedCoordinates: mapped.changedCoordinates, mapped: mapped.document };
});
const prRows = wmRows.map((row) => ({ name: row.name, result: proposeWorkflowPlacement(wmDoc(row)) }));
if (!prRows.some((r) => r.name === 'crafted.placement' && r.result.eligible && r.result.changes.length)) throw new Error('crafted.placement: not an eligible proposal');
if (!prRows.some((r) => !r.result.eligible)) throw new Error('proposeWorkflowPlacement: no ineligible row');
if (!mgDocs.some((r) => r.changedCoordinates.length)) throw new Error('mapExplicitCoordinates: no row changes a coordinate');
// #497: planning deletes semanticChecks exactly when it drops an edge; each side needs a row that carries contracts.
for (const [name, kept] of [['crafted.migration', false], ['wf-v1-semantic-fallback', false], ['crafted.migration-kept', true]]) {
  const row = mgDocs.find((r) => r.name === name);
  if (!row?.intrinsic.semanticChecks || (row.planning.edges.length === row.intrinsic.edges.length) !== kept || ('semanticChecks' in row.planning) !== kept) {
    throw new Error(`${name}: planning ${kept ? 'must keep' : 'must drop'} its edges and semanticChecks together`);
  }
}
write('fixtures/js/workflow-model.json', {
  feedback: WM_FEEDBACK,
  rows: wmModel,
  overrides: { overrides: { today: 150, mid_autumn: 40, year_end: 0 }, widths: overridden.nodes.map((n) => [n.id, wm.authoredNodeWidth(n)]) },
  legacyLayout: wm.createLegacyLayout(),
  mapper: [...mapperColumns.map((cols) => mapperRow(WM_LEGACY, cols, mapperXs)),
    mapperRow([88, 220], [1, 2, 3], []), mapperRow([88], [1], []), mapperRow([88, 88], [1, 2], []), mapperRow([88, 220], [5, 5], []),
    mapperRow([88, 220], [1, 2], [null])],
  migration: mgDocs,
  placement: prRows,
});
// Hand-written checker probes: the goldens are all clean, so these reach the issue, diagnostic, route-review and
// sequence-note paths. A failing artifact exits 1 with its report on stdout; any other failure is real.
const checkerDir = out('fixtures/checker');
for (const f of fs.readdirSync(checkerDir).filter((f) => f.endsWith('.html')).sort()) {
  let stdout;
  try {
    stdout = execFileSync(process.execPath, [path.join(UP, 'scripts/check-render-output.mjs'), path.join(checkerDir, f)],
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: cleanEnv, maxBuffer: 64 * 1024 * 1024 });
  } catch (e) {
    if (e.status !== 1 || !e.stdout) throw e;
    stdout = e.stdout;
  }
  const res = JSON.parse(stdout);
  delete res.file;
  write(`fixtures/checker/${path.basename(f, '.html')}.expected.json`, res);
}

// --- PORT-008 charts: tools/chart-seeds/<group>.json -> tests/goldens/charts/<group>/<name>.json (layout: tools/chart-seeds/README.md) ---
const { compileExplainerChart, chartFromData } = await import('file:///C:/projects/archify/archify/explainer/charts.mjs');
const { PeleError } = await import('file:///C:/projects/archify/archify/explainer/vendor/pele/core.js');   // the instance charts.mjs uses
const CHART_SEEDS = path.join(ROOT, 'tools', 'chart-seeds');
// charts.mjs:25 runs detectType outside its try, so a front-matter PeleError leaves compileExplainerChart: a thrown row (review B1).
const chartCompile = (text) => {
  try { return compileExplainerChart(text); } catch (e) {
    if (!(e instanceof PeleError)) throw e;
    return { thrown: { name: e.name, message: e.message, code: e.code, line: e.line } };
  }
};
// Only TextDecoder (fatal) throws inside chartFromData; sources.mjs:311 gives that its own message (PORT-009), so record the fact.
const chartData = (s) => {
  try { return chartFromData(Buffer.from(s.bytes, 'base64'), s.file, s.spec); } catch (e) {
    if (e.code !== 'ERR_ENCODING_INVALID_ENCODED_DATA') throw e;
    return { notUtf8: true };
  }
};
// Pele 1.3.0 tests/support/corpus.ts:34-57 `random` and `mutator`, verbatim (types dropped).
function peleRandom(seed) {
  let state = seed >>> 0;
  return () => {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    return state / 4294967296;
  };
}
function peleMutator(corpus, fragments, rnd) {
  const pick = (list) => list[Math.floor(rnd() * list.length)];
  return () => {
    let s = pick(corpus);
    const edits = 1 + Math.floor(rnd() * 3);
    for (let k = 0; k < edits; k++) {
      const pos = Math.floor(rnd() * (s.length + 1));
      const r = rnd();
      if (r < 0.35) {
        s = s.slice(0, pos) + pick(fragments) + s.slice(pos);
      } else if (r < 0.6) {
        s = s.slice(0, pos) + s.slice(pos + 1 + Math.floor(rnd() * 3));
      } else if (r < 0.75) {
        const other = pick(corpus);
        const from = Math.floor(rnd() * other.length);
        s = s.slice(0, pos) + other.slice(from, from + Math.floor(rnd() * 20)) + s.slice(pos);
      } else if (r < 0.9) {
        s = s.slice(0, pos) + s.slice(pos, pos + Math.floor(rnd() * 6)) + s.slice(pos);
      } else {
        s = s.slice(0, pos) + pick(fragments) + s.slice(pos + 1);
      }
    }
    return s;
  };
}
fs.rmSync(out('goldens/charts'), { recursive: true, force: true });   // a renamed or removed seed leaves no stale golden
const chartGroups = new Map();
for (const f of fs.readdirSync(CHART_SEEDS).filter((f) => f.endsWith('.json') && !f.endsWith('.fuzz.json')).sort()) {
  const group = f.slice(0, -'.json'.length);
  const seeds = JSON.parse(fs.readFileSync(path.join(CHART_SEEDS, f), 'utf8'));
  const names = new Set();
  for (const s of seeds) {
    // Golden file names: one per seed, unique ignoring case (Windows), and `fuzz` is the fuzz array's name.
    if (!/^[\w.-]+$/.test(s.name) || s.name === 'fuzz' || names.has(s.name.toLowerCase())) throw new Error(`${f}: bad or duplicate seed name ${JSON.stringify(s.name)}`);
    names.add(s.name.toLowerCase());
    const isText = typeof s.text === 'string', isData = typeof s.bytes === 'string' && typeof s.file === 'string' && Array.isArray(s.spec?.y);
    if (isText === isData) throw new Error(`${f}/${s.name}: a seed is {name, text} or {name, file, bytes, spec}`);
    const result = isText ? chartCompile(s.text) : chartData(s);
    if (group === 'wrapper' && /^native-(?!treeview$)/.test(s.name) && !result.error?.startsWith(`Mermaid ${s.name.slice('native-'.length).split('.')[0]} `)) {
      throw new Error(`wrapper/${s.name}: not a NATIVE redirect: ${JSON.stringify(result)}`);
    }
    write(`goldens/charts/${group}/${s.name}.json`, result);
  }
  chartGroups.set(group, seeds);
}
// Fuzz: {seed, count, fragments} per type; the corpus is the group's own text seeds; all results go to one array (review M7).
for (const f of fs.readdirSync(CHART_SEEDS).filter((f) => f.endsWith('.fuzz.json')).sort()) {
  const type = f.slice(0, -'.fuzz.json'.length);
  const { seed, count, fragments } = JSON.parse(fs.readFileSync(path.join(CHART_SEEDS, f), 'utf8'));
  const corpus = (chartGroups.get(type) ?? []).filter((s) => typeof s.text === 'string').map((s) => s.text);
  if (!corpus.length || !Number.isInteger(seed) || !Number.isInteger(count) || !fragments?.length) throw new Error(`${f}: needs ${type}.json text seeds, an integer seed and count, and fragments`);
  const next = peleMutator(corpus, fragments, peleRandom(seed));
  write(`goldens/charts/${type}/fuzz.json`, Array.from({ length: count }, () => { const input = next(); return { input, result: chartCompile(input) }; }));
}

// --- PORT-008 Task 3: Pele core unit tables -> tests/fixtures/js/pele-*.json ---
// Pele's internals are not exported by the vendored core.js, so they come from tools/pele-internals/pele-internals.mjs, a
// bundle of the 1.3.0 tag source (build command in tools/pele-internals/entry.mjs; committed so no esbuild install is needed).
const PELE_INTERNALS = path.join(ROOT, 'tools', 'pele-internals', 'pele-internals.mjs');
if (!fs.existsSync(PELE_INTERNALS)) {
  throw new Error(`${PELE_INTERNALS} is missing. Rebuild it from the repo root with esbuild 0.28.2: node <esbuild>/bin/esbuild ` +
    'tools/pele-internals/entry.mjs --bundle --format=esm --target=es2022 --charset=ascii --legal-comments=none --outfile=tools/pele-internals/pele-internals.mjs');
}
const P = await import(pathToFileURL(PELE_INTERNALS).href);
const { detectType: vendoredDetectType } = await import('file:///C:/projects/archify/archify/explainer/vendor/pele/core.js');
const peleNumIn = (v) => (Object.is(v, -0) ? '-0' : String(v));   // Js.ToNumber reads it back bit for bit
const peleTry = (run) => { try { return run(); } catch (e) { return { throws: e.name, message: e.message, ...(e instanceof P.PeleError ? { code: e.code, line: e.line } : {}) }; } };
// Seeded LCG (Pele's tests/support/corpus.ts random), never Math.random.
const peleRnd = peleRandom(0x5EED0300);

// num(): .xx5 boundaries, >= 1e11 hundredths (the String() fallback), -0, NaN, infinities, plus 150 seeded values.
const peleNums = [0, -0, NaN, Infinity, -Infinity, 1, -1, 0.5, -0.5, 0.005, -0.005, 0.004999, 0.0049999999999999, 0.015, 0.025,
  0.045, 0.125, -0.125, 0.135, 0.995, -0.995, 1.005, -1.005, 1.115, -1.115, 2.675, -2.675, 9.995, -9.995, 12.345, 99.995,
  123456.785, 123456789.125, 0.1 + 0.2, 1 / 3, -1 / 3, 2 / 3, 1e-7, -1e-7, 5e-324, -5e-324, 1e9, -1e9, 999999999.99,
  999999999.994, 999999999.995, -999999999.995, 1e9 - 0.005, 1e9 + 0.005, 1e10, 1e10 + 0.005, 1e11, -1e11, 1e21, -1e21,
  123456789012.345, 1.7976931348623157e308, -1.7976931348623157e308, 0.01, 0.1, 0.07, 0.29, 0.57, 1.45, 4.35, 8.2];
for (let i = 0; i < 150; i++) {
  const base = Math.floor(peleRnd() * 2000000 - 1000000) / 100;
  const scale = 10 ** (Math.floor(peleRnd() * 16) - 4);
  peleNums.push(i % 3 === 0 ? base + (i % 2 ? 0.005 : -0.005) : base * scale);
}
write('fixtures/js/pele-num.json', peleNums.map((v) => [peleNumIn(v), P.num(v)]));

// esc() / escText(): every control character, the four escaped ones, entity codes and placeholders.
const peleEscIn = ['', 'plain', 'a&b', '<tag>', '"q"', "'s'", 'a\x01\x02b', '\t\n\r', '\x7f', '&amp;', '&#35;', '&#x41;',
  '&nbsp;x', '&unknown;', '&Alpha;&alpha;', 'ﬂ°quot¶ß', 'ﬂ°°35¶ß', 'ﬂ°°1.5¶ß', '\u2028\u2029', '\uFEFF', '\ud800x', '😀',
  '&#0;', '&#55296;', '&#1114111;', '&#1114112;', '&#x110000;', '&lt;b&gt;', 'x\x00\x0b\x0c\x0e\x1fy'];
for (let c = 0; c < 32; c++) peleEscIn.push('a' + String.fromCharCode(c) + 'b');
write('fixtures/js/pele-esc.json', peleEscIn.map((s) => ({ s, esc: P.esc(s), escText: peleTry(() => P.escText(s)) })));

// encodeEntities / decodeEntities.
const peleEncodeIn = ['no hash', '#', '#35;', '#quot;', 'a #9829; b', '#+12;', '##;', '#_;', '#é;', '#0;', '#007;',
  'style a fill:#f00;', 'classDef x fill:#f9f;stroke:#333;', 'style a fill:#f00\nstyle b color:#0f0;', 'classDef a fill:#abc ;',
  'style x : #fff;', 'stylefoo:#a;', 'style a:#1;b;c', 'style a:#1;\u2028x;', 'style a:#1\u2029;', 'style a:#1;\r#2;',
  'style a fill:#f00;#35;', 'x style a:b #c;', 'classDef a:#b;\nclassDef c:#d;e;', 'style a :#f;', 'style\n:#a;', 'style:#a;'];
const peleDecodeIn = ['plain', 'ﬂ°quot¶ß', 'ﬂ°°35¶ß', 'ﬂ°°0x41¶ß', 'ﬂ°° 65¶ß', 'ﬂ°°¶ß', 'ﬂ°°1e1¶ß', 'ﬂ°°1.5¶ß', 'ﬂ°°-5¶ß',
  'ﬂ°°55296¶ß', 'ﬂ°°1114112¶ß', 'ﬂ°nope¶ß', 'ﬂ°abc', 'ﬂ°x¶', 'ﬂ°x¶ßy¶ß', 'aﬂ°amp¶ßbﬂ°lt¶ßc', '¶ß alone', 'ﬂ°ﬂ°amp¶ß',
  'ﬂ°__proto__¶ß', '&#9;', '&#x1F600;', '&#xD800;', '&lt;&gt;', '&Alpha;', '&alpha', '&amp;amp;', '&#99999999999999999999;',
  '&#x;', '&#;', '&__proto__;', '&constructor;', '&quot;ﬂ°°65¶ß&#66;', '&x1;', '&#X41;', '&#00065;'];
write('fixtures/js/pele-entities.json', {
  encode: peleEncodeIn.map((s) => [s, P.encodeEntities(s)]),
  decode: peleDecodeIn.map((s) => [s, peleTry(() => P.decodeEntities(s))]),
});

// The Arial metrics measurer: ASCII, Han, emoji (surrogates), combining marks, zero-width and wide ranges, every style bit.
const peleAscii = Array.from({ length: 95 }, (_, i) => String.fromCharCode(32 + i)).join('');
const peleMeasureIn = [peleAscii, 'Hello World', '中文字', '😀👍', 'e\u0301', '\uFEFF', '\u200B\u200F', '\t\n\x1f', '\u00A0x',
  '한국어', 'ＡＢ｡', '\uFE0F\uFE00', '\uFE30\uFE4F', '\u1100\u115F', '\u2E80\ua4cf', '\uFFE0\uFFE6', '\uF900', '\u0300\u036f',
  '\u2028', 'Mixed 中 😀 é', '', '\x7f\x80'];
const peleMeasure = [];
for (const text of peleMeasureIn) for (const size of [13, 16, 11.5]) for (let style = 0; style < 8; style++) {
  peleMeasure.push([text, size, style, P.metricsMeasurer.width(text, size, style)]);
}
write('fixtures/js/pele-measure.json', peleMeasure);

// detectType over every detector, in order, plus near misses. Each row is cross-checked against the vendored core.js.
const peleDetectIn = ['flowchart-elk', 'mindmap', 'architecture-beta', 'agentflow-beta', 'agentflow-beta x', 'agentflow-betax',
  'C4Context', 'xC4Container', 'notes C4Deployment', 'C4Component', 'C4Dynamic', 'kanban', 'classDiagram', 'erDiagram', 'gantt',
  'info', 'pie', '  pie', 'pies', '\uFEFF pie', '\u00A0pie', '\u0085pie', '\u2028pie', 'requirement', 'requirementDiagram',
  'sequenceDiagram', 'swimlane-beta', 'swimlane-beta2', 'swimlane-beta-x', 'graph TD', 'flowchart LR', 'Flowchart', 'timeline',
  'gitGraph', 'stateDiagram-v2', 'journey', 'quadrantChart', 'sankey', 'sankey-beta', 'packet', 'packet-beta', 'xychart',
  'xychart-beta', 'block', 'block-beta', 'eventmodeling', 'treeView-beta', 'treeview-beta', 'radar-beta', 'radar', 'ishikawa',
  'ISHIKAWA', 'ishikawa-beta', 'ishikawa-betaX', 'ishikawax', 'ishi\u212Awa', 'treemap', 'railroad-beta', 'Railroad-EBNF-beta',
  'railroad-abnf-beta', 'railroad-peg-beta', 'railroad-xyz-beta', 'venn-beta', 'venn', 'wardley-beta', 'WARDLEY-BETA',
  'cynefin-beta', 'cynefin-beta:', 'cynefin-beta x', 'cynefin-betax', 'cynefin-beta\n', 'cynefin-beta\u00A0', 'usecase-beta',
  'usecase-beta\t', 'usecase-beta:', 'usecase-beta\u00A0', 'usecase-beta\u0085', '', '   ', '%% comment\npie',
  '%%{init: {"a":1}}%%\nxychart-beta', 'unknown', 'Pie', '\n\n  gantt', 'x\npie', '%%\npie'];
write('fixtures/js/pele-detect.json', peleDetectIn.map((t) => {
  const type = P.detect(P.preprocess(t).text);
  if (type !== vendoredDetectType(t)) throw new Error(`pele-internals drifted from the vendored core.js on ${JSON.stringify(t)}`);
  return [t, type];
}));

// preprocess(): {text, title, config, withComments, lineOffset} as JSON.stringify writes it (front matter rows: Task 4).
const peleNest = (n) => '{"a":'.repeat(n) + '1' + '}'.repeat(n);
const pelePreIn = [
  ['comment', 'pie\n%% comment\n"a": 1'], ['comments-top', '%% top\n\n%% another\npie'], ['comment-indented', '\n\n  %% c\npie'],
  ['comment-inline', 'pie %% not a line comment'], ['comment-empty', '%%\npie'], ['comment-percent3', '%%%\npie'],
  ['comment-last', 'pie\n%% last'], ['comment-blank-run', 'pie\n\n\n%% c\n\n"a":1'], ['comment-u2028', 'pie\u2028%% c\n"a":1'],
  ['comment-u2029-end', 'pie\u2029  %% c'], ['comment-cr-only', 'pie\r%% c\r"a" : 1'], ['crlf', 'pie\r\n"a" : 1\r\n'],
  ['cr-crlf', 'pie\r\r\n"a"\r'], ['comment-tab', 'pie\n\t%% tabbed\n"a":1'], ['comment-nbsp', 'pie\n\u00A0%% nbsp\nx'],
  ['comment-feff', '\uFEFF%% bom\npie'], ['comment-directive-like', '%%{ not closed\npie'], ['percent-in-string', 'pie "a%%b" : 1'],
  ['trim-start', '  \n\t pie'], ['trim-feff', '\uFEFFpie'],
  ['init', '%%{init: {"theme":"dark"}}%%\npie'], ['init-single-quotes', "%%{init: {'a':1}}%%\npie"],
  ['init-apostrophe-value', "%%{init: {\"a\":\"it's\"}}%%\npie"], ['init-dup-keys', '%%{init: {"a":1,"b":2,"a":3}}%%\npie'],
  ['init-dup-object', '%%{init: {"a":{"x":1},"a":{"y":2}}}%%\npie'], ['init-invalid', '%%{init: invalid}%%\npie'],
  ['init-empty', '%%{init: }%%\npie'], ['init-array', '%%{init: [1,2]}%%\npie'], ['init-null', '%%{init: null}%%\npie'],
  ['init-merge', '%%{initialize: {"x": {"y": 1}, "k": [1]}}%%\n%%{init: {"x": {"z": 2}, "k": {"q": 1}}}%%\npie'],
  ['init-merge-replace', '%%{init: {"x": {"y": 1}}}%%\n%%{init: {"x": 5}}%%\n%%{init: {"x": {"w": 1}}}%%\npie'],
  ['init-unsafe', '%%{init: {"__proto__": {"a":1}, "constructor": 1, "prototype": 2, "ok": {"__proto__": 3, "b": 4}}}%%\npie'],
  ['init-arrays', '%%{init: {"a": [1, {"b": 2}], "c": "s"}}%%\npie'], ['init-numbers', '%%{init: {"n": 1e400, "m": -0, "f": 1.50}}%%\npie'],
  ['init-int-keys', '%%{init: {"b": 1, "2": 2, "a": 3, "1": 4}}%%\npie'], ['init-lone-surrogate', '%%{init: {"s": "\\ud800"}}%%\npie'],
  ['init-nest-100', `%%{init: ${peleNest(100)}}%%\npie`], ['wrap', '%%{wrap}%%\npie'], ['wrap-colon', '%%{wrap: true}%%\npie'],
  ['init-closer-in-string', '%%{init: {"a":"x}%%y"}}%%\npie'], ['init-multiline', '%%{init: {\n"a": 1\n}}%%\npie'],
  ['init-crlf', '%%{init: {\r\n"a": 1\r\n}}%%\r\npie'], ['init-u2028', '%%{init: {"a":\u20281}}%%\npie'], ['init-upper', '%%{INIT: {"a":1}}%%\npie'],
  ['init-spaces', '%%{ init : {"a" : 1} }%%\npie'], ['init-later', 'pie\n%%{init: {"a":1}}%%'], ['init-unclosed', '%%{init: {"a":1}\npie'],
  ['init-word', '%%{init: theme}%%\npie'], ['init-twice-line', '%%{init: {"a":1}}%%%%{init: {"b":2}}%%\npie'],
  ['directive-other', '%%{config: {"a":1}}%%\npie'], ['directive-trim', '  %%{init: {"a":1}}%%  \npie'],
  ['html-attrs', 'flowchart\nA["<a href="x" title="y">t</a>"]'], ['html-attrs-b', 'x <b class="c">y</b>'], ['html-br', 'a<br/>b'],
  ['html-first-gt', '>pie <x a="1"'], ['html-digit-tag', '<1 a="b">'], ['html-unicode-tag', '<é a="b">'], ['html-newline-attrs', '<x\na="1">'],
  ['html-after-last', '<x a="1"> <y b="2"'], ['html-empty-attr', '<x a="">'], ['html-crlf', '<x\r\na="1">\r\n'],
  ['dashes-not-front-matter', 'pie\n---\n"a":1'], ['dashes-glued', '---pie'], ['dashes-unclosed', '---\npie'], ['empty', ''],
];
// Front matter (Task 4): title truthiness then String() (preprocess.ts:156), config merge, displayMode, the line shift, and
// thrown YAML errors wrapped as "Front matter is not valid YAML. ..." (a peleTry row).
const fm = (yaml, rest = 'pie') => `---\n${yaml}\n---\n${rest}`;
pelePreIn.push(
  ['fm-title', fm('title: Hello  world')], ['fm-title-array', fm('title: [1, 2]')], ['fm-title-object', fm('title: {}')],
  ['fm-title-zero', fm('title: 0')], ['fm-title-empty', fm('title: ""')], ['fm-title-false', fm('title: false')],
  ['fm-title-null', fm('title: null')], ['fm-title-nan', fm('title: .nan')], ['fm-title-neg-zero', fm('title: -0')],
  ['fm-title-inf', fm('title: .inf')], ['fm-title-float', fm('title: 1.50')], ['fm-title-true', fm('title: true')],
  ['fm-title-nested-array', fm('title: [null, [1, [2]], ~, "x"]')], ['fm-title-block', fm('title: |\n  two\n  lines')],
  ['fm-title-dup', fm('title: a\ntitle: b')],
  ['fm-config', fm('config:\n  pie:\n    textPosition: 0.5\n  theme: dark')], ['fm-config-scalar', fm('config: 5')],
  ['fm-config-array', fm('config: [1]')], ['fm-config-unsafe', fm('config:\n  __proto__:\n    a: 1\n  constructor: 1\n  ok:\n    prototype: 2\n    b: 3')],
  ['fm-config-numbers', fm('config:\n  a: .inf\n  b: -.inf\n  c: .nan\n  d: -0\n  e: 0x10')],
  ['fm-config-then-directive', fm('config:\n  a:\n    x: 1\n  k: [1]', '%%{init: {"a": {"y": 2}, "k": {"q": 1}}}%%\npie')],
  ['fm-config-xychart', fm('config:\n  xyChart:\n    useMaxWidth: false', 'xychart-beta\n  bar [1]')],
  ['fm-display-mode', fm('displayMode: compact')], ['fm-display-mode-merge', fm('displayMode: compact\nconfig:\n  gantt:\n    x: 1')],
  ['fm-display-mode-replace', fm('config:\n  gantt: 5\ndisplayMode: [a, b]')], ['fm-display-mode-falsy', fm('displayMode: 0')],
  ['fm-top-sequence', fm('- a\n- b')], ['fm-top-scalar', fm('plain')], ['fm-empty', '---\n\n---\npie'], ['fm-comment-only', fm('# c')],
  ['fm-indented', '  ---\n  title: T\n  config:\n    a: 1\n  ---\npie'], ['fm-crlf', '---\r\ntitle: T\r\n---\r\npie'],
  ['fm-line-shift', fm('title: T\nconfig:\n  a: 1', 'pie\n  "A" : abc')], ['fm-then-comments', fm('title: T', '%% c\npie')],
  ['fm-error-flow', fm('config: [', 'pie\n"a": 1')], ['fm-error-tab', fm('\ta: 1')], ['fm-error-code-point-nan', fm('title: "\\uZZZZ"')],
  ['fm-error-code-point-high', fm('title: "\\U00110000"')], ['fm-error-escape', fm('title: "\\q"')], ['fm-error-end', fm('a: 1\n-b')],
);
write('fixtures/js/pele-preprocess.json', pelePreIn.map(([name, input]) => ({ name, input, out: JSON.stringify(peleTry(() => {
  const { text, title, config, withComments, lineOffset } = P.preprocess(input);
  return { text, title, config, withComments, lineOffset };
})) })));

// util/yaml.ts parseYaml (Task 4). Numbers are written as {num} so that Infinity, NaN and -0 survive JSON; an error is
// {error: message} (preprocess.ts wraps any error's message, so the class is not recorded).
const yamlEnc = (v) => JSON.stringify(v, (_k, x) => (typeof x === 'number' ? { num: peleNumIn(x) } : x));
const yamlNest = (n, open, close, leaf = '1') => open.repeat(n) + leaf + close.repeat(n);
const yamlKeys = (n) => Array.from({ length: n }, (_, i) => ' '.repeat(i) + 'k:').join('\n') + ' 1';
const yamlSeqs = (n) => Array.from({ length: n }, (_, i) => ' '.repeat(i) + '-').join('\n') + ' 1';
const peleYamlIn = [
  // scalars
  'a: 1', 'a: -0', 'a: +1', 'a: 01', 'a: 1.', 'a: .5', 'a: +.5', 'a: -.5e1', 'a: 1e3', 'a: 1E400', 'a: -1e400', 'a: 1.5e-3', 'a: 5e-324',
  'a: 123456789012345678901234567890', 'a: 0x1F', 'a: -0x1F', 'a: +0x1f', 'a: 0x0', 'a: -0x0', 'a: 0x1FFFFFFFFFFFFF', 'a: 0o17',
  'a: -0o17', 'a: 0o8', 'a: 0b101', 'a: -0b0', 'a: 0X1F', 'a: 0B1',
  // past 2^53 Number() rounds once, ties to even (Js.ToNumber used to round at every digit)
  'a: 0xa3f71d68be3994e', 'a: 0x3ec1d1793b7c69d9f', 'a: -0x2484978515ef1d42', 'a: 0x20000000000001', 'a: 0x20000000000003',
  'a: 0x2000000000000100000000001', 'a: 0o7777777777777777777777777777', 'a: 0b' + '10'.repeat(40), 'a: 0x' + 'f'.repeat(256),
  'a: 0x' + 'f'.repeat(255), 'a: [0x' + 'f'.repeat(13) + '8' + '0'.repeat(242) + ', 0x' + 'f'.repeat(13) + '7' + 'f'.repeat(242) + ', 0x' + 'f'.repeat(13) + 'c' + '0'.repeat(242) + ', 0x' + 'f'.repeat(13) + 'b' + 'f'.repeat(242) + ']', 'a: .inf', 'a: -.Inf', 'a: +.INF', 'a: .iNf', 'a: .nan', 'a: .NaN',
  'a: .NAN', 'a: -.nan', 'a: ~', 'a: null', 'a: Null', 'a: NULL', 'a: nUll', 'a: true', 'a: True', 'a: TRUE', 'a: tRUE', 'a: false',
  'a: False', 'a: FALSE', 'a: yes', 'a: 1_000', 'a: 12:30', 'a: 1,2', 'a:', 'a: # c', 'a: x # c', 'a: x#c', 'a: "x # c"', 'a:  spaced  out  ',
  'a: 1 2', 'a: -', 'a: - x', 'a: \u00A0x\u00A0', 'a: \uFEFF1', 'a: x\u2028y',
  // documents
  '', '   ', '\n\n', '# only a comment', '---', '...', 'a: 1\n...', 'a: 1\n...\nb: 2', 'a: 1\n---\nb: 2', 'plain', 'plain # c', '"quoted"',
  "'single'", '42', '- a\n- b', '[1, 2]', '{a: 1}', '{a: 1} x', '{a: 1}\nb: 2', '[1] # c', '[1]\n# c', '  {a: 1}  ', '[1,\n2]', '{a: [1, 2]',
  '[1, 2', 'a\nb', '  a: 1', '  a: 1\nb: 2', 'a: 1\r\nb: 2\r', 'a: 1\rb: 2',
  // block mappings
  'a: 1\nb: 2', 'a:\n  b: 1\n  c: 2', 'a:\n  b:\n    c: 1', 'a:\n- 1\n- 2', 'a:\n  - 1\n  - 2', 'a: 1\n  b: 2', 'a:\n  b: 1\n c: 2',
  'a: 1\nb', 'a: 1\na: 2', 'a:\n  x: 1\na: 2', 'a: 1\nb: 2\na: 3', '"a b": 1', "'it''s': 1", '"a\\tb": 1', '"a": 1', 'a : 1', 'a:1',
  'a:b: c', 'x: a:1', 'a#b: 1', 'a #b: 1', '__proto__: 1', '__proto__:\n  a: 1', '1: a\n2: b\nz: c\n0: d', 'null: 1', '~: 1', 'true: 1',
  '1.0: x', '.inf: x', '0x10: x', '[a]: 1', '{a: 1}: 2', '"a": 1\n"b"', '"a" : 1', '"a":1', '"a\\": 1', "'a'b': 1", '-x: 1', '- : 1',
  'a: 1 # c\nb: 2', 'a:\n\n\n  b: 1', 'a:\n# c\n  b: 1', 'a:\nb:', 'a:\n  # only comment\nb: 1', 'a:\n---\n  b: 1', 'k: v\n  # x\n  w',
  'a\u00A0: 1', 'a\t: 1', 'a:\tb', 'a: \tb',
  // tabs
  'a:\n\tb: 1', '\ta: 1', 'a:\n  b: 1\n\tc: 2', '- \t- a', 'a:\n  \tb: 1',
  // sequences
  '- a\n-b', '- a\n  - b', '- a: 1\n  b: 2', '- - a\n  - b', '-\n  a: 1', '- \n- x', '-', '- # c\n  a: 1', '- a\n\n- b', '- a\n  b',
  '-  a: 1\n   b: 2', '- [1, 2]\n- {a: 1}', '- "x"\n- \'y\'', '- |\n  text\n- b', 'a:\n- 1\nb: 2', 'a:\n  - 1\n - 2', '- a: 1\n- b: 2',
  '- a:\n    b: 1', '-\ta', '- a # c\n- b',
  // block scalars
  'a: |\n  line1\n  line2\n', 'a: |-\n  x\n\n', 'a: |+\n  x\n\n\nb: 1', 'a: |\n  x\n\n\n', 'a: >\n  folded\n  text\n\n  para\n    indented\n  back',
  'a: |2\n    two extra', 'a: |1\n  x', 'a: | # comment-x\n  y', 'a: | # 3\n     y', 'a: >-\n', 'a: |\nb: 1', 'a: |+\n\n\n', 'a: |x', 'a: |\n  x\n   y\n z',
  'a: >\n  a\n\n\n  b', 'a: >\n\n  lead', 'a: >+\n  x\n\n', 'a: |-\n', 'a: >\n  a\n  \tb\n  c', '- >\n  x\n  y', 'a: |\n\tx', 'a: |\n  \u2028x',
  'a: >\n  x  \n  y', 'a: | \u2028', 'a: |-+\n  x\n',
  // multi-line plain
  'a: one\n  two\n\n  three\nb: 1', 'a: one\n  # not comment\n  two', 'a: x\n\n\nb: 1', 'a: one\n  two # c\n  three', 'a: x\n\n  \n  y',
  'a: one\n\n', '- one\n  two', 'a: x\n  - y',
  // flow collections
  'a: [1, 2, 3]', 'a: [1,2,]', 'a: [,1]', 'a: [1,,2]', 'a: [1 2]', 'a: [1 "b"]', 'a: {x: 1, y: [2, {z: 3}]}', 'a: {x}', 'a: {x: 1, x: 2}',
  'a: {"x": 1}', 'a: {x:1}', 'a: {x: a:b}', 'a: [a:b, c: d]', 'a: [1, 2] # c', 'a: [1, 2] x', 'a: [1,\n  2,\n  3]', 'a: [1,\n2]\nb: 3',
  'a: [1, 2', 'a: {x: 1', 'a: [\n  1, # c\n  2\n]', 'a: [a#b]', 'a: [a #b]', 'a: [{}, []]', 'a: []', 'a: {}', "a: ['x', 'it''s']", 'a: [1]]',
  'a: {a: {b: {c: 1}}}', 'a: {,}', 'a: {x: 1,, y: 2}', 'a: {x: 1 y: 2}', 'a: {x: 1 "y": 2}', 'a: {: 1}', 'a: {]}', 'a: [}]', 'a: [:x]',
  'a: {"a" "b"}', 'a: [\n1,\n2,\n3,\n4,\n5,\n6]\nb: 1', 'a: [\n1,\n2] x\nb: 1', 'a: [1\n,2]', 'a: {x: [1, 2]: 3}', 'a: [ ~, null, true ]',
  'a: [.inf, -.nan, 0x1F, 1e400]', 'a: {1: a, 0x2: b, .inf: c, ~: d, [1, 2]: e, {}: f}', 'a: [1, 2]\n  b', 'a: {x: "y\n  z"}',
  'a: [1, [2, [3]]]', 'a: { x : 1 , y : 2 }', 'a: {x:\n  1}', 'a: [a,b]#c', 'a: [a,b] #c', 'a: [\u00A01\u00A0]', 'a: [x\u2028y]',
  'a: {x\u3000: 1}', 'a: [1]\n- 2', '[1]\n[2]', '{a: 1}\n{b: 2}', 'a: [#x\n1]', 'a: [1, #x\n2]',
  // depth (MAX_DEPTH = 64)
  yamlNest(64, '[', ']'), yamlNest(65, '[', ']'), yamlNest(66, '[', ']'), 'a: ' + yamlNest(64, '[', ']'), 'a: ' + yamlNest(65, '[', ']'),
  yamlNest(64, '{a: ', '}'), yamlNest(65, '{a: ', '}'), yamlNest(66, '{a: ', '}'), 'a: ' + yamlNest(65, '{a: ', '}'),
  yamlKeys(64), yamlKeys(65), yamlKeys(66), yamlKeys(67), yamlSeqs(64), yamlSeqs(65), yamlSeqs(66), yamlSeqs(67),
  yamlKeys(66).replace(/ 1$/, ' ' + yamlNest(64, '[', ']')),
  // quoted scalars and escapes (review M3)
  'a: "\\0\\a\\b\\t\\n\\v\\f\\r\\e\\ \\"\\/\\\\\\N\\_\\L\\P"', 'a: "\\q"', 'a: "\\x41"', 'a: "\\x4"', 'a: "\\u41"', 'a: "\\u0041"',
  'a: "\\U00000041"', 'a: "\\U0001F600"', 'a: "\\U0010FFFF"', 'a: "\\uD800"', 'a: "\\uDC00\\uD800"', 'a: "\\uD83D\\uDE00"', 'a: "\\uZZZZ"',
  'a: "\\U00110000"', 'a: "\\x"', 'a: "\\u"', 'a: "\\U"', 'a: "\\u-041"', 'a: "\\U-0000001"', 'a: "\\u0x41"', 'a: "\\u 41 "',
  'a: "\\U0000004G"', 'a: "\\x4G"', 'a: "\\xG4"', 'a: "\\U+0000041"', 'a: "\\u00e9x"', 'a: "\\xffz"', 'a: "\\U7FFFFFFF"', 'a: "\\uFFFF"',
  'a: "\\x-0"', 'a: "\\t"x', 'a: "one \\\n   two"', 'a: "x\n  y"', 'a: "x\n\n  y"', 'a: "a\\\\\n b"', 'a: "unterminated', 'a: "x" y',
  'a: "x" # c', 'a: \'single\'', "a: 'x\n  y'", "a: 'unterminated", "a: 'a''b'", "a: 'a\\b'", "a: ''", 'a: ""', 'a: "\\\\"', 'a: "\\"',
  'a: "x\\\n\n  y"', 'a: "  lead\n   trail  "', 'a: "x\t\n\ty"', "a: 'x\n\n\n  y'", 'a: "\\', '"\\uZZZZ"', '["\\U00110000"]',
  'a: "\\u{41}"', 'a: "\u2028"', '"x"\n"y"', 'a: "x\n"\nb: 1', 'a: "x\ny"\nb: 1',
];
write('fixtures/js/pele-yaml.json', peleYamlIn.map((src) => {
  try { return [src, yamlEnc(P.parseYaml(src))]; } catch (e) { return [src, JSON.stringify({ error: e.message })]; }
}));
// Nesting past the port's JSON depth cap (a stated deviation): Node merges it; the port ignores the directive.
const peleDeep = P.preprocess(`%%{init: ${peleNest(300)}}%%\npie`);
write('fixtures/js/pele-preprocess-deep.json', { input: `%%{init: ${peleNest(300)}}%%\npie`, text: peleDeep.text, merged: JSON.stringify(peleDeep.config).length });

// layoutLabel(raw, markdown, metricsMeasurer, size, maxWidth, base): wrapping, <br/>, inline tags, markdown, icons.
const peleLabelIn = [
  ['', false, 13, 400, 0], ['Hello', false, 13, 400, 0], ['  padded  ', false, 13, 400, 0], ['Hello', false, 16, 400, 1],
  ['a long label that should wrap nicely', false, 13, 50, 0], ['nospaceslongword', false, 13, 10, 0], ['two  spaces  here', false, 13, 30, 0],
  ['line1<br/>line2', false, 13, 400, 0], ['a<br>b<BR />c<br \t/>d', false, 13, 400, 0], ['a\\nb', false, 13, 400, 0], ['a\nb', false, 13, 400, 0],
  ['<br/>x', false, 13, 400, 0], ['<b>bold</b> text', false, 13, 400, 0], ['<i>it</i>', false, 13, 400, 0], ['<strong>x</strong><em>y</em>', false, 13, 400, 0],
  ['<span class="x">s</span>', false, 13, 400, 0], ['<b>unclosed', false, 13, 400, 0], ['<b>x</b>', false, 13, 400, 1], ['a < b', false, 13, 400, 0],
  ['<B>up</B>', false, 13, 400, 0], ['<b-x>t</b-x>', false, 13, 400, 0], ['<b\u00A0x>t', false, 13, 400, 0],
  ['fa:fa-car Cars', false, 13, 400, 0], ['Cars fa:fa-car', false, 13, 400, 0], ['Cars fa:fa-car more', false, 13, 400, 0],
  ['fab:fa-github x', false, 13, 400, 0], ['fa:fa-car', false, 13, 400, 0], ['fa:fa-car,x', false, 13, 400, 0], ['a fa:fa-x  b', false, 13, 400, 0],
  ['faz:fa-car x', false, 13, 400, 0], ['<b>fa:fa-car</b> x', false, 13, 400, 0], ['x fa:fa-car<br/>y', false, 13, 400, 0],
  ['ﬂ°quot¶ß quoted', false, 13, 400, 0], ['&amp; x', false, 13, 400, 0], ['ﬂ°°65¶ß', false, 13, 400, 0],
  ['**bold** md', true, 13, 400, 0], ['_it_ md', true, 13, 400, 0], ['snake_case_word', true, 13, 400, 0], ['* not', true, 13, 400, 0],
  ['__b__ and *i*', true, 13, 400, 0], ['**unclosed', true, 13, 400, 0], ['a *b c* d', true, 13, 400, 0], ['**bold** md', false, 13, 400, 0],
  ['**a long bold** text that wraps', true, 13, 60, 0], ['  a\n    b\r\n  c', true, 13, 400, 0], ['x**y**', true, 13, 400, 0],
  ['中文标签很长很长', false, 13, 30, 0], ['中文 标签 很长 很长', false, 13, 30, 0], ['emoji 😀 here and more', false, 13, 40, 0],
  ['<b>a b c d e f</b> g h', false, 13, 30, 0], ['a<b>b c</b>d e', false, 13, 20, 0], ['wrap\u00A0nbsp words here', false, 13, 30, 0],
  ['trailing   <br/>x', false, 13, 400, 0], ['  <b> spaced </b>  ', false, 13, 400, 0], ['a b', false, 13, 0, 0], ['x', false, 0, 400, 0],
];
write('fixtures/js/pele-label.json', peleLabelIn.map(([raw, markdown, size, maxWidth, base]) =>
  [raw, markdown, size, maxWidth, base, JSON.stringify(P.layoutLabel(raw, markdown, P.metricsMeasurer, size, maxWidth, base))]));

// The SVG writer: svgDocument (rx rewrite, weights, accessible names, useMaxWidth), labelSvg, resolveStyle, classNames,
// seriesColor, fontStyle. Theme constants are written out so the port's copies are checked too.
const RX = ' rx="var(--pele-radius,4px)"';
const peleDocs = [
  ['pie', 100, 50, 13, true, '', '', ''], ['xychart', 480.5, 260, 13, false, 'T & t', 'D <d>', '<g/>'],
  ['pie', 10, 10, 16, true, 'ﬂ°amp¶ß', '', `<rect x="0"${RX} y="1"/>`], ['venn', 1, 2, 11.5, true, '', 'only desc', `<rect${RX} style="fill:red"/>`],
  ['sankey', 3, 4, 13, true, '', '', `<rect${RX}>`], ['radar', 5, 6, 13, true, '', '', `<rectx${RX}/><rect a="1"/>`],
  ['quadrantChart', 7, 8, 13, true, '', '', '<text font-weight="var(--_w)">a</text><g fill="var(--_tw)"/><x y="var(--_hw)"/>'],
  ['pie', 9, 9, 13, true, '', '', `<rect${RX}${RX}/>`], ['pie', 9, 9, 13, true, '', '', 'var(--_r) only'],
];
const peleLabelSvgIn = [
  ['Hello', false, 13, 400, 0, 50, 20, ''], ['<b>Bold</b>', false, 13, 400, 0, 50, 20, ''], ['<b>Bold</b>', false, 13, 400, 0, 50, 20, ' font-weight="600"'],
  ['a<br/>b', false, 13, 400, 0, 0, 0, ' class="t"'], ['<br/>x', false, 13, 400, 0, 1.005, 2.675, ''], ['**bold** and _it_', true, 13, 400, 0, 10, 10, ''],
  ['x', false, 13, 400, 1, 3, 4, ''], ['<i>a</i><b>b</b>', false, 13, 400, 0, 3, 4, ''], ['<i><b>a</b></i> <i>b</i>', false, 13, 400, 0, 3, 4, ' font-style="normal"'],
  ['a & b', false, 13, 400, 0, -5, -5, ''], ['', false, 13, 400, 0, 0, 0, ''], ['long words that wrap around', false, 16, 60, 0, 100, 100, ''],
  ['fa:fa-car Cars', false, 13, 400, 0, 0, 0, ''],
];
const peleStyles = [[], ['fill:#f00'], ['fill:#f00', 'stroke:blue', 'stroke-width:2px'], ['color:red', 'font-weight:bold'], ['font-weight:700'],
  ['font-weight:500'], ['font-weight: 600'], ['font-weight:bolder'], ['font-weight:abc'], ['font-size:14px'], ['font-size:14'], ['font-size:300px'],
  ['font-size:0'], ['font-size:1.5.2px'], ['FONT-SIZE:12PX'], ['font-size:.5'], ['FILL : #abc !IMPORTANT'], ['fill:url(x)'], ['fill:url\t(x)'],
  ['fill:url\u00A0(x)'], ['fill: '], [':x'], ['nocolon'], ['opacity:0.5'], ['fill:a', 'stroke:c', 'fill:b'], ['font-style:italic'], ['fill:"q"'],
  ['fill:a&b'], ['stroke:red;'], ['fill:expression (x)'], ['fill:JavaScript:x'], ['rx:5', 'ry:6'], ['font-family:Arial'], ['fill:/*x*/'],
  ['fill:@IMPORT'], ['fill:image-set(x)'], ['fill-opacity:0.2', 'stroke-dasharray:3 3'], ['text-decoration:underline', 'letter-spacing:1px'],
  ['fill:a!important!important'], ['fill:#f00 !important'], ['unknown:1']];
write('fixtures/js/pele-svg.json', {
  constants: { ROOT_STYLE: P.ROOT_STYLE, RADIUS: P.RADIUS, CRITICAL: P.CRITICAL },
  documents: peleDocs.map(([type, w, h, size, responsive, accTitle, accDescr, content]) =>
    [type, w, h, size, responsive, accTitle, accDescr, content, P.svgDocument(type, w, h, size, responsive ? {} : { responsive: false }, { accTitle, accDescr }, content)]),
  labels: peleLabelSvgIn.map(([raw, md, size, max, base, cx, cy, attrs]) =>
    [raw, md, size, max, base, cx, cy, attrs, P.labelSvg(P.layoutLabel(raw, md, P.metricsMeasurer, size, max, base), cx, cy, attrs)]),
  styles: peleStyles.map((decls) => [decls, JSON.stringify(P.resolveStyle(decls))]),
  classNames: ['a b', 'a.b#c', '  x  ', '', '---', 'é', 'a_b-c', '<x>', 'a\u00A0b'].map((s) => [s, P.classNames(s)]),
  seriesColor: Array.from({ length: 27 }, (_, i) => [i - 9, P.seriesColor(i - 9)]),
  fontStyle: [13, 11.5, 16].map((size) => [size, P.fontStyle(size, undefined, undefined)]),
});

// The Langium-style tokenizer and Reader over a test table built from the common tokens. The C# test builds the same table
// (PeleCoreTests.TestTokens); it uses a literal pattern with `longer`, a `match` scanner, a `scanner` factory and every shared token.
const T = P.tokens;
const tokId = { name: 'ID', pattern: /[a-z]+/y, first: 'abcdefghijklmnopqrstuvwxyz' };
const tokDigits = (src, at) => { let i = at; while (src.charCodeAt(i) >= 48 && src.charCodeAt(i) <= 57) i++; return i > at ? i : -1; };
const TEST_TOKENS = [{ name: 'pie', pattern: 'pie', longer: [tokId] }, T.keyword('showData'), { name: ':', pattern: ':' },
  { name: 'NUM', pattern: /[0-9]+/y, match: tokDigits, first: '0123456789' }, T.ACC_DESCR, T.ACC_TITLE, T.TITLE, T.STRING, T.NEWLINE,
  T.WHITESPACE, T.YAML, T.DIRECTIVE, T.SINGLE_LINE_COMMENT, tokId,
  { name: 'HASH', pattern: /#[0-9]+/y, scanner: () => (src, at) => (src.charCodeAt(at) === 35 && tokDigits(src, at + 1) !== -1 ? tokDigits(src, at + 1) : -1), first: '#' }];
const peleTokenIn = ['pie', 'pies', 'pie showData', 'pie showDatax', 'pie title X\n"a" : 1', 'pie\n%% c\n"a":1', '  accTitle: t\naccDescr {\nx\n}',
  'accDescr: d %% c', 'accTitle: a %% b', 'title T %% c', '%%{init: {}}%%\npie', '%%{init: {}}%%x', '%%{ unclosed\npie', '---\nx\n---\npie',
  '---\n---', '---\nunclosed', '--- \r\n a \r\n---', 'pie ?', '"unterminated', "'s' \"d\"", '"a\\"b"', 'pie%%c', 'é', 'pie é', 'title', 'titlex',
  'showData%%x', '12:34', 'a\r\nb', 'a\rb', '\t \t', '', 'accDescr\n{ multi\n line }', 'accDescr {unclosed', 'pie\u2028x', '"a\u2028b"', '#12 pie', '#', 'pie#3#4'];
write('fixtures/js/pele-tokens.json', {
  tokenize: peleTokenIn.map((src) => [src, peleTry(() => { const t = T.tokenize(src, TEST_TOKENS, 'test'); return { kinds: t.kinds, starts: t.starts, ends: t.ends }; })]),
  // Reader: expect pie, accept showData, expect ':' then NUM; the Chevrotain-style failure message otherwise.
  reader: ['pie:1', 'pie showData:2', 'pie', 'pie:x', 'pie::', 'x:1', 'pie showData', '\npie:1'].map((src) => [src, peleTry(() => {
    const r = new T.Reader(T.tokenize(src, TEST_TOKENS, 'test'), TEST_TOKENS, 'test');
    const out = [r.expect(0)];
    out.push(String(r.accept(1)));
    out.push(r.expect(2), r.expect(3));
    return out;
  })]),
  titleValue: ['title  My   Title', 'title', 'title\tx  y ', 'xtitle a', 'title\u00A0a  b'].map((s) => [s, T.titleValue(s)]),
  accTitleValue: ['accTitle: x  y', 'accTitle:', 'accTitle :\t a\t\tb'].map((s) => [s, T.accTitleValue(s)]),
  accDescrValue: ['accDescr: one  two', 'accDescr {\n  line one  \n\n\n  line two\n}', 'accDescr{a\u2028  b}', 'accDescr   {  x\r\n\r\n y  }',
    'accDescr {}', 'accDescr{\u00A0a\u00A0\n\u2029b}', 'accDescr {a\t\t b\r\r\nc}', 'accDescr:x', 'accDescr {  \n  }'].map((s) => [s, T.accDescrValue(s)]),
  stringValue: ['"abc"', '"a\\nb"', '"a\\"b"', "'x\\ty'", '"\\q"', '"\\0"', '"\\v\\b\\f\\r"', '""', '"\\\\"'].map((s) => [s, T.stringValue(s)]),
  locate: [['abc', 0], ['abc', 2], ['abc', 3], ['a\nbc\nd', 3], ['a\nbc\nd', 2], ['a\nbc\nd', 5], ['\n\n', 1], ['', 0], ['x\r\ny', 2]]
    .map(([src, at]) => [src, at, P.locate(src, at)]),
  syntaxError: [['pie', 'pie\nx', 4, 'Expecting a but found b.', false], ['xy', 'a\nb\nc', 2, '', true], ['t', 'abc', 1, 'detail', false]]
    .map(([type, src, at, detail, lexical]) => { const e = P.syntaxError(type, src, at, detail, lexical); return [type, src, at, detail, lexical, { message: e.message, code: e.code, line: e.line }]; }),
  wordAt: [['XYChart', 0, 'xychart'], ['xyCHART', 0, 'xychart'], ['xy', 0, 'xychart'], ['éa', 0, 'éa'], ['ÉA', 0, 'éa'], ['K', 0, 'k'],
    ['\u212A', 0, 'k'], ['x[', 0, 'x'], ['ab', 1, 'b'], ['a1', 0, 'a1'], ['A_', 0, 'a_'], ['', 0, '']].map(([s, at, w]) => [s, at, w, P.wordAt(s, at, w)]),
  fit: { titleRoom: P.titleRoom({ padding: 8 }), capWidth: P.capWidth({}, 480, 200, 30), fitWidth: P.fitWidth({}, 480, 200, (plot, narrow) => ({ svg: `${plot}|${narrow}`, width: plot, height: 1, links: [] })).svg },
});
console.log('fixtures captured');
