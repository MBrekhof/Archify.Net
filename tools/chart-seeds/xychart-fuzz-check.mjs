// Checks the xychart deviations (PORT-008 Task 6, PORT-010) against upstream archify 3.2.0 charts.mjs, for
// ChartParityTests.Fuzz_deviations_are_the_stated_quirks. The oracle is upstream itself with each fix written into a copy
// of its vendored Pele xychart chunk (L4LY553T.js, asserted to occur once), so it is code apart from the port:
//   Q1  the second comment rule `[^}]%%` takes only whitespace before `%%` with it;
//   X1  `setOrientation` compares case-insensitively (`toLowerCase() === "horizontal"`);
//   X2  `vertical` and `horizontal` lex as the chart orientation only as a whole word right after the chart keyword;
//   X3  markdown strings `"`...`"` lex as strings instead of matching the jison source text;
//   X4  unquoted text joins its tokens with the whitespace between them (a gap holding a line break is one space);
//   C1  an explicit y range whose ends clamp to the same +-1e300 falls back to the data;
//   L1  (the line remap, UPSTREAM-007) an error's line is counted in the input as written.
// Run from the repo root: node tools/chart-seeds/xychart-fuzz-check.mjs. It prints each pinned case and exits 1 if one fails.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const UP = 'C:/projects/archify/archify/explainer';
const CHUNK = 'vendor/pele/chunks/L4LY553T.js';
const PATCHES = {
  Q1: [
    'if(r!==125&&A===37&&n.charCodeAt(t+2)===37)',
    'if(Zt(r)&&A===37&&n.charCodeAt(t+2)===37)',
  ],
  X1: [
    'this.orientation=e==="horizontal"?"horizontal":"vertical"',
    'this.orientation=e.toLowerCase()==="horizontal"?"horizontal":"vertical"',
  ],
  X2: [
    'case 118:if(F(n,t,"vertical")){u(10,t,t+8);continue}break;case 104:if(F(n,t,"horizontal")){u(10,t,t+10);continue}break;',
    'case 118:if(i.at(-1)===9&&m("vertical")){u(10,t,t+8);continue}break;case 104:if(i.at(-1)===9&&m("horizontal")){u(10,t,t+10);continue}break;',
  ],
  X3: [
    'if(r===96&&F(n,t,ke)){t+=ke.length,_();continue}if(r===34){t++,y(5);continue}',
    'if(r===34){A===96?(t+=2,y(9)):(t++,y(5));continue}',
  ],
  X4: [
    'let o=c[a++];if(i[a-1]!==19)for(;Oe[i[a]]===1;)o+=c[a++];',
    'let o=c[a++];if(i[a-1]!==19){let e=h[a-1]+o.length;for(;Oe[i[a]]===1;a++){let g="",b=!1;for(let k=e;k<h[a];k++){let d=n.charCodeAt(k);d===10||d===13?b=!0:Zt(d)&&(g+=n[k])}o+=(b?" ":g)+c[a],e=h[a]+c[a].length}}',
  ],
  C1: [
    'ce=n.hasSetYAxis&&isFinite(b)&&isFinite(g)&&b!==g;',
    'ce=n.hasSetYAxis&&isFinite(b)&&isFinite(g)&&K(b)!==K(g);',
  ],
};
const X3_EXTRA = [
  [
    'if(l===5){if(r===34)t++,_();else{let x=n.indexOf(\'"\',t);u(19,t,x===-1?e:x)}continue}',
    'if(l===5){if(r===34)t++,_();else{let x=n.indexOf(\'"\',t);u(19,t,x===-1?e:x)}continue}if(l===9){if(r===96&&n.charCodeAt(t+1)===34){t+=2,_();continue}if(r!==96&&r!==34){let x=t+1;for(;x<e&&n.charCodeAt(x)!==96&&n.charCodeAt(x)!==34;)x++;u(19,t,x);continue}break}',
  ],
  [
    'i.push(De[l]),c.push(""),h.push(e),',
    '(l<De.length&&(i.push(De[l]),c.push(""),h.push(e))),',
  ],
];
const NAMES = Object.keys(PATCHES);
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'xychart-oracle-'));

async function variant(names) {
  const dir = path.join(root, names.join('-') || 'none');
  fs.cpSync(path.join(UP, 'vendor'), path.join(dir, 'vendor'), { recursive: true });
  fs.copyFileSync(path.join(UP, 'charts.mjs'), path.join(dir, 'charts.mjs'));
  let src = fs.readFileSync(path.join(dir, CHUNK), 'utf8');
  for (const [from, to] of [...names.map((n) => PATCHES[n]), ...(names.includes('X3') ? X3_EXTRA : [])]) {
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
const block = /\["xychart"\] = new\(\) \{([^}]*)\}/.exec(cs)?.[1] ?? '';
const labels = new Map([...block.matchAll(/\[(\d+)\] = "([A-Z0-9]*)"/g)].map((m) => [+m[1], m[2]]));

const fuzz = JSON.parse(fs.readFileSync('tests/goldens/charts/xychart/fuzz.json', 'utf8'));
const PINNED = 'tests/fixtures/charts/xychart.fuzz-deviations.json';
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

const seeds = JSON.parse(fs.readFileSync('tools/chart-seeds/xychart.json', 'utf8'));
for (const { name, text } of seeds) {
  if (JSON.stringify(fixed.run(text)) !== JSON.stringify(upstream.run(text))) console.log('seed', name.padEnd(48), fixesThatChange(text).join('+'));
}
fs.rmSync(root, { recursive: true, force: true });
process.exit(bad ? 1 : 0);
