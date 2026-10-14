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
for (const [type, input, golden] of targets) render(type, input, out(`goldens/${golden}`));
// Node checker output for every golden (upstream 15 + ours); strip the absolute `file` so it is path-independent.
const upstreamGoldens = fs.readdirSync(EX).filter((f) => f.endsWith('.html')).map((f) => path.join(EX, f));
for (const html of [...upstreamGoldens, ...targets.map(([, , g]) => out(`goldens/${g}`))]) {
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
