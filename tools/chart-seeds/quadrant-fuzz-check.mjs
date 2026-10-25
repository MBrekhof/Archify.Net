// Checks the quadrantChart deviations (PORT-008 Task 7) against upstream archify 3.2.0 charts.mjs, for
// ChartParityTests.Fuzz_deviations_are_the_stated_quirks. The oracle is upstream itself with each fix written into a copy
// of its vendored Pele quadrant chunk (42UTT654.js, one replacement each, asserted to occur once), so it is code apart from
// the port:
//   Q1  the second comment rule `[^}]%%` takes only whitespace before `%%` with it;
//   Q2  a coordinate `0(.\d+)?` requires a literal `.` after `0`;
//   Q3  keywords end at a word boundary and lead a statement that is not in point form;
//   R1  empty quadrant and point labels reserve no box in the collision table;
//   L1  (the line remap, UPSTREAM-007) an error's line is counted in the input as written.
// Run from the repo root: node tools/chart-seeds/quadrant-fuzz-check.mjs. It prints each pinned case and exits 1 if one fails.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const UP = 'C:/projects/archify/archify/explainer';
const CHUNK = 'vendor/pele/chunks/42UTT654.js';
const Q3_HELPERS = [
  'let start=!0,pfAt=-1,pf=!1,',
  'pair=r=>{let E=q(r);if(E===-1||t.charCodeAt(T(E))!==44)return!1;E=q(T(T(E)+1));return E!==-1&&t.charCodeAt(T(E))===93},',
  'pairAfter=r=>t.charCodeAt(T(r+1))===91&&pair(T(T(r+1)+1)),',
  'onLine=(r,E)=>{for(;r<E&&!Ut(t.charCodeAt(r));)r++;return r===E},',
  'titlePt=r=>{for(;xt(t.charCodeAt(r))&&!Ut(t.charCodeAt(r));)r++;return t.charCodeAt(r)===58&&pairAfter(r)},',
  'pointForm=r=>{for(let k=r;k<n;k++){let c=t.charCodeAt(k);if(c===10||c===13||c===59||c===37&&t.charCodeAt(k+1)===37)return!1;if(c===34){if((k=t.indexOf(\'"\',k+1))===-1)return!1}else if(c===58&&pairAfter(k))return!0}return!1},',
  'lead=r=>(start&&(r!==pfAt&&(pfAt=r,pf=pointForm(r)),!pf));',
].join('');
const PATCHES = {
  Q1: [
    'if(r!==125&&E===37&&t.charCodeAt(e+2)===37)',
    'if(xt(r)&&E===37&&t.charCodeAt(e+2)===37)',
  ],
  Q2: [
    'if(C<n&&!Ut(t.charCodeAt(C))&&nt(t.charCodeAt(C+1)))',
    'if(t.charCodeAt(C)===46&&nt(t.charCodeAt(C+1)))',
  ],
  Q3: [
    'A=(r,E,C)=>{s.push(r),o.push(t.slice(E,C)),d.push(E),e=C}',
    'A=(r,E,C)=>{s.push(r),o.push(t.slice(E,C)),d.push(E),e=C,start=r===2||r===39||r===24||r===38&&start}',
  ],
  R1: [
    'F.push(g-_.width/2,h-_.height/2,g+_.width/2,h+_.height/2)',
    '_.height>0&&F.push(g-_.width/2,h-_.height/2,g+_.width/2,h+_.height/2)',
  ],
};
const Q3_EXTRA = [
  ['t:for(;e<n;){', `${Q3_HELPERS}t:for(;e<n;){`],
  ['if(C===116&&v(t,e,"title")&&!it(t.charCodeAt(e+5))){', 'if(C===116&&v(t,e,"title")&&!it(t.charCodeAt(e+5))&&start&&!titlePt(e+5)){'],
  ['if(y===58){A(i?7:5,e,T(l+1)),b(i?3:2);continue}if(y===123&&i){e=T(l+1),b(4);continue}', 'if(y===58&&start&&!(onLine(e+8,l)&&pairAfter(l))){A(i?7:5,e,T(l+1)),b(i?3:2);continue}if(y===123&&i&&start){e=T(l+1),b(4);continue}'],
  ['if(H===120&&v(t,p,"x-axis"))L=10,O=p+6;else if(H===121&&v(t,p,"y-axis"))L=11,O=p+6;', 'if(H===120&&v(t,p,"x-axis")&&!it(t.charCodeAt(p+6))&&lead(p))L=10,O=p+6;else if(H===121&&v(t,p,"y-axis")&&!it(t.charCodeAt(p+6))&&lead(p))L=11,O=p+6;'],
  ['t.charCodeAt(p+8)===45&&i>=49&&i<=52?(L=13+i-49,O=p+10):v(t,p+8,"chart")&&(L=24,O=p+13)', 't.charCodeAt(p+8)===45&&i>=49&&i<=52?!it(t.charCodeAt(p+10))&&lead(p)&&(L=13+i-49,O=p+10):v(t,p+8,"chart")&&!it(t.charCodeAt(p+13))&&start&&(L=24,O=p+13)'],
  ['if(C===99&&v(t,e,"classdef")&&!it(t.charCodeAt(e+8))){', 'if(C===99&&v(t,e,"classdef")&&!it(t.charCodeAt(e+8))&&lead(e)){'],
];
const R1_EXTRA = [
  ['for(let{cx:c,cy:x,reach:m,text:_}of Z){let g=_.width/2', 'for(let{cx:c,cy:x,reach:m,text:_}of Z){if(_.height===0)continue;let g=_.width/2'],
];
const NAMES = Object.keys(PATCHES);
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'quadrant-oracle-'));

async function variant(names) {
  const dir = path.join(root, names.join('-') || 'none');
  fs.cpSync(path.join(UP, 'vendor'), path.join(dir, 'vendor'), { recursive: true });
  fs.copyFileSync(path.join(UP, 'charts.mjs'), path.join(dir, 'charts.mjs'));
  let src = fs.readFileSync(path.join(dir, CHUNK), 'utf8');
  const pairs = [
    ...names.map((n) => PATCHES[n]),
    ...(names.includes('Q3') ? Q3_EXTRA : []),
    ...(names.includes('R1') ? R1_EXTRA : []),
  ];
  for (const [from, to] of pairs) {
    if (src.split(from).length !== 2) throw new Error(`patch anchor not found once: ${from}`);
    src = src.replace(from, () => to);
  }
  fs.writeFileSync(path.join(dir, CHUNK), src);
  const charts = await import(pathToFileURL(path.join(dir, 'charts.mjs')).href);
  const core = await import(pathToFileURL(path.join(dir, 'vendor/pele/core.js')).href);
  const run = (t) => { try { return charts.compileExplainerChart(t); } catch (e) { return { thrown: { name: 'PeleError', message: e.message, code: e.code, line: e.line } }; } };
  return { run, render: core.render };
}

const upstream = await variant([]);
const single = Object.fromEntries(await Promise.all(NAMES.map(async (n) => [n, await variant([n])])));
const fixed = await variant(NAMES);

const mapped = (s) => ({ s, o: Array.from({ length: s.length }, (_, i) => i) });
function edit(m, edits) {
  let s = '', last = 0;
  const o = [];
  const keep = (from, to) => { for (let k = from; k < to; k++) o.push(m.o[k]); };
  for (const e of edits) {
    s += m.s.slice(last, e.at) + e.with;
    keep(last, e.at);
    for (let k = 0; k < e.with.length; k++) o.push(e.with.length === e.len ? m.o[e.at + k] : m.o[e.at]);
    last = e.at + e.len;
  }
  s += m.s.slice(last);
  keep(last, m.s.length);
  return { s, o };
}
const replaceAll = (m, re, by) => edit(m, [...m.s.matchAll(re)].map((x) => ({ at: x.index, len: x[0].length, with: by(x[0]) })));
const inputLine = (input, at) => input.slice(0, at).split(/\r\n|\n|\r/).length;
const lineOf = (input, m, at) => inputLine(input, at < m.o.length ? m.o[at] : m.o.findLast((k) => !/\s/.test(input[k])) ?? 0);
const FRONT = /^[ \t]*---[ \t]*\n[\s\S]*?\n[ \t]*---[ \t]*(?:\n|$)/;
const DIRECTIVE = /%%\{[\s\S]*?(?:\}%%|$)/g;
function prepare(input) {
  let m = replaceAll(mapped(input), /\r\n?/g, () => '\n');
  const front = FRONT.exec(m.s);
  if (front) m = edit(m, [{ at: 0, len: front[0].length, with: '' }]);
  m = replaceAll(m, DIRECTIVE, () => '');
  if (m.s.includes('%%')) m = replaceAll(m, /^\s*%%(?!\{)[^\n]+\n?/gm, () => '');
  m = edit(m, [{ at: 0, len: m.s.length - m.s.trimStart().length, with: '' }]);
  m = replaceAll(m, /\b(style|classDef)\b[^\n\r\u2028\u2029]*:[^\s]*#[^\n\r\u2028\u2029]*;/g, (s) => s.slice(0, -1));
  m = replaceAll(m, /#\w+;/g, (s) => (/^#\+?\d+;$/.test(s) ? 'ﬂ°°' : 'ﬂ°') + s.slice(1, -1) + '¶ß');
  return { s: m.s + '\n', o: [...m.o] };
}
function nodeLine(text) {
  try { fixed.render(text); return null; } catch (e) {
    if (!(e.line > 0)) return null;
    const p = prepare(text);
    const lines = p.s.split('\n');
    if ((lines[e.line - 1] ?? '') !== e.snippet.split('\n')[0]) return 'mismatch';
    let at = e.column - 1;
    for (let k = 0; k < e.line - 1; k++) at += lines[k].length + 1;
    return lineOf(text, p, at);
  }
}
function expected(input) {
  let node = fixed.run(input);
  const line = node.error !== undefined && node.line > 0 ? nodeLine(input) : null;
  if (line === 'mismatch') return { node: null, remapped: false };
  const remapped = typeof line === 'number' && line !== node.line;
  if (remapped) node = { ...node, error: node.error.replace(new RegExp(`line ${node.line}(?![0-9])`), `line ${line}`), line };
  return { node, remapped };
}
const fixesThatChange = (input) => {
  const base = JSON.stringify(upstream.run(input));
  return NAMES.filter((n) => JSON.stringify(single[n].run(input)) !== base);
};

const cs = fs.readFileSync('tests/Archify.Net.Core.Tests/ChartParityTests.cs', 'utf8');
const block = /\["quadrantChart"\] = new\(\) \{([^}]*)\}/.exec(cs)?.[1] ?? '';
const labels = new Map([...block.matchAll(/\[(\d+)\] = "([A-Z0-9]*)"/g)].map((m) => [+m[1], m[2]]));

const fuzz = JSON.parse(fs.readFileSync('tests/goldens/charts/quadrantChart/fuzz.json', 'utf8'));
const PINNED = 'tests/fixtures/charts/quadrantChart.fuzz-deviations.json';
const pinned = new Map(fs.existsSync(PINNED) ? JSON.parse(fs.readFileSync(PINNED, 'utf8')).map((p) => [p.index, p.output]) : []);
let bad = 0;
for (let index = 0; index < fuzz.length; index++) {
  const { input } = fuzz[index];
  if (!pinned.has(index)) {
    if (JSON.stringify(fixed.run(input)) === JSON.stringify(upstream.run(input)) && !labels.has(index)) continue;
    bad++;
    console.log(String(index).padStart(4), 'FAIL', `the fixes change an unlisted case: ${fixesThatChange(input).join('+')}`.padEnd(65), JSON.stringify(input).slice(0, 70));
    continue;
  }
  const { node, remapped } = expected(input);
  const port = JSON.parse(pinned.get(index));
  const why = [...fixesThatChange(input), ...(remapped ? ['L1'] : [])];
  const label = labels.get(index) ?? '';
  const fixes = why.filter((q) => q !== 'L1');
  const named = (fixes.length > 0 ? fixes.every((q) => label.includes(q)) : label.includes('L1')) && pinned.has(index) === labels.has(index);
  const ok = node !== null && JSON.stringify(node) === JSON.stringify(port) && named;
  if (!ok) bad++;
  const what = node === null ? 'checker preparation differs from Pele\'s' : !named ? `fixes ${why.join('+') || 'none'} vs label ${label || 'none'}` : port.svg !== undefined ? 'svg' : 'error ' + JSON.stringify(port.error ?? port.thrown?.message);
  console.log(String(index).padStart(4), ok ? 'ok  ' : 'FAIL', (label || '-').padEnd(8), what.slice(0, 56).padEnd(56), JSON.stringify(input).slice(0, 70));
}
console.log(`${pinned.size} pinned, ${bad} failing of ${fuzz.length} fuzz cases`);

const seeds = JSON.parse(fs.readFileSync('tools/chart-seeds/quadrantChart.json', 'utf8'));
for (const { name, text } of seeds) {
  if (JSON.stringify(fixed.run(text)) !== JSON.stringify(upstream.run(text))) console.log('seed', name.padEnd(48), fixesThatChange(text).join('+'));
}
fs.rmSync(root, { recursive: true, force: true });
process.exit(bad ? 1 : 0);
