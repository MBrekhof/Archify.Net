#!/usr/bin/env node
// Dev-time only. Captures V8 behaviour and upstream renderer output as fixtures for the C# parity suite.
// Usage: node tools/capture-fixtures.mjs   (from repo root; needs C:\projects\archify at c21c31ff)
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

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
// Quality-option goldens: minted with the env var upstream reads; C# renders the same input with RenderOptions.
const renderEnv = (type, input, output, env) => execFileSync(process.execPath, [path.join(UP, `renderers/${type}/render-${type}.mjs`), input, output], { stdio: 'inherit', env: { ...cleanEnv, ...env } });
const optionGoldens = [   // [type, input, golden, quality]
  ['dataflow', out('fixtures/diagrams/five-stage.dataflow.json'), 'five-stage.option-showcase-rendered.html', 'showcase'],
  ['workflow', path.join(EX, 'agent-tool-call.workflow.json'), 'agent-tool-call.option-standard-rendered.html', 'standard'],
];
for (const [type, input, golden, quality] of optionGoldens) renderEnv(type, input, out(`goldens/${golden}`), { ARCHIFY_QUALITY_PROFILE: quality });
for (const [type, input, golden] of targets) render(type, input, out(`goldens/${golden}`));
// Node checker output for every golden (upstream 15 + ours); strip the absolute `file` so it is path-independent.
const upstreamGoldens = fs.readdirSync(EX).filter((f) => f.endsWith('.html')).map((f) => path.join(EX, f));
for (const html of [...upstreamGoldens, ...[...targets, ...optionGoldens].map(([, , g]) => out(`goldens/${g}`))]) {
  const res = JSON.parse(execFileSync(process.execPath, [path.join(UP, 'scripts/check-render-output.mjs'), html],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: cleanEnv, maxBuffer: 64 * 1024 * 1024 }));   // upstream's checker buffer
  delete res.file;
  write(`goldens/checker/${path.basename(html, '.html')}.json`, res);
}
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
