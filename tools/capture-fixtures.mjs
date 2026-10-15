#!/usr/bin/env node
// Dev-time only. Captures V8 behaviour and upstream renderer output as fixtures for the C# parity suite.
// Usage: node tools/capture-fixtures.mjs   (from repo root; needs C:\projects\archify at c21c31ff)
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
  ['timeline', out('fixtures/diagrams/payment-incident.zh-CN.timeline.json'), 'payment-incident.zh-CN-rendered.html'],
  ['waterfall', out('fixtures/diagrams/incomplete-trace.waterfall.json'), 'incomplete-trace-rendered.html'],
  ['waterfall', out('fixtures/diagrams/incomplete-trace.no-unit.waterfall.json'), 'incomplete-trace.no-unit-rendered.html'],
  ['waterfall', out('fixtures/diagrams/checkout-request.width.waterfall.json'), 'checkout-request.width-rendered.html'],
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
for (const locale of ['en', 'zh-CN', 'zh-TW', 'es', 'ko']) erdWrite(`i18n.${locale}`, i18nFixture(undefined, locale));
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
// Timeline layout reports (`--layout-json` stdout) for the four timeline goldens.
for (const [name, input] of [['timeline-payment-incident', path.join(EX, 'payment-incident.timeline.json')],
  ['timeline-archify-dev-activity', path.join(EX, 'archify-dev-activity.timeline.json')],
  ['dst-new-york', out('fixtures/diagrams/dst-new-york.timeline.json')],
  ['payment-incident.zh-CN', out('fixtures/diagrams/payment-incident.zh-CN.timeline.json')]]) {
  const stdout = execFileSync(process.execPath, [path.join(UP, 'renderers/timeline/render-timeline.mjs'), input, out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  fs.writeFileSync(out(`fixtures/diagrams/${name}.layout.json`), stdout);
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
// Waterfall layout reports (`--layout-json` stdout) for the three waterfall goldens.
for (const [name, input] of [['waterfall-checkout-request', path.join(EX, 'checkout-request.waterfall.json')],
  ['waterfall-example-rebuild', path.join(EX, 'example-rebuild.waterfall.json')],
  ['incomplete-trace', out('fixtures/diagrams/incomplete-trace.waterfall.json')]]) {
  const stdout = execFileSync(process.execPath, [path.join(UP, 'renderers/waterfall/render-waterfall.mjs'), input, out(`goldens/${name}.layout.html`), '--layout-json'],
    { encoding: 'utf8', env: cleanEnv, stdio: ['ignore', 'pipe', 'pipe'] });
  fs.writeFileSync(out(`fixtures/diagrams/${name}.layout.json`), stdout);
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
// Phase-3 failure receipts: one fault per diagnostic code; upstream must FAIL with the target code in its receipt or the script throws.
const p3Faults = [   // [type, source input, name, target code, edit]
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
  const diagram = JSON.parse(fs.readFileSync(source, 'utf8'));
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
  if (name === 'erd-label-over-entity' && ('cards' in diagram || 'views' in diagram.meta)) throw new Error('erd-label-over-entity: cards/views survived');
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
for (const f of fs.readdirSync(out('fixtures/diagrams')).filter((f) => /\.dataflow\.json$/.test(f) && !f.includes('.evidence')).sort()) {   // evidence inputs: Task 6 goldens, not repair scenes
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
  // v1 migration: a straight labeled edge, a kept edge whose labelAt is mapped and whose label planning drops.
  ['crafted.migration', (() => { const d = ex('incident-response.workflow.json');
    Object.assign(d.edges[6], { route: 'straight', label: 'declare it', width: 2, role: 'branch' });
    d.edges.push({ from: 'alert', to: 'triage', label: 'skip', labelAt: [150.5, 60], width: 1.6 }); return d; })()],
];
const wmRows = [];
const seenInputs = new Set();
for (const { name, input } of p3Rows.filter((r) => r.type === 'workflow')) {
  if (seenInputs.has(input)) continue;
  seenInputs.add(input);
  wmRows.push({ name, input });
}
for (const name of ['wf-unknown-node-lane', 'wf-semantic-reference', 'wf-required-edge']) wmRows.push({ name, input: `tests/fixtures/diagrams/${name}.workflow.json` });
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
console.log('fixtures captured');
