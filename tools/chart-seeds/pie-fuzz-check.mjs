// Checks the pie deviations (PORT-008 Task 5, PORT-011) against upstream archify 3.2.0 charts.mjs, for
// ChartParityTests.Fuzz_deviations_are_the_stated_quirks. The oracle is upstream itself with its fix written into a copy of
// its vendored Pele pie chunk (MSKIJHB2.js, asserted to occur once), so it is code apart from the port:
//   PL  the "has invalid value" message names the slice decoded (Pele's decodeEntities, XML-invalid chars dropped, line
//       breaks written back as `#10;` and `#13;`);
//   L1  (the line remap, UPSTREAM-007) an error's line is counted in the input as written: the input is prepared as Pele's
//       core does, keeping where each character came from, Node's snippet must be that line of the preparation, and the
//       error goes to the input's line; an error past the end is on the last content line.
// Run from the repo root: node tools/chart-seeds/pie-fuzz-check.mjs. It prints each pinned case and exits 1 if one fails.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const UP = 'C:/projects/archify/archify/explainer';
const CHUNK = 'vendor/pele/chunks/MSKIJHB2.js';
const PATCHES = {
  PL: [
    '`"${n}" has invalid value:',
    '`"${PLdecode(n).replace(/[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f\\ufffe\\uffff]|[\\ud800-\\udbff](?![\\udc00-\\udfff])|(?<![\\ud800-\\udbff])[\\udc00-\\udfff]/g,"").replace(/\\n/g,"#10;").replace(/\\r/g,"#13;")}" has invalid value:',
  ],
};
const IMPORT = ['import{a as oe,g as re}from"./XTDUIGUI.js"', 'import{a as oe,g as re,d as PLdecode}from"./XTDUIGUI.js"'];
const NAMES = Object.keys(PATCHES);
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pie-oracle-'));

async function variant(names) {
  const dir = path.join(root, names.join('-') || 'none');
  fs.cpSync(path.join(UP, 'vendor'), path.join(dir, 'vendor'), { recursive: true });
  fs.copyFileSync(path.join(UP, 'charts.mjs'), path.join(dir, 'charts.mjs'));
  let src = fs.readFileSync(path.join(dir, CHUNK), 'utf8');
  for (const [from, to] of [...names.map((n) => PATCHES[n]), ...(names.includes('PL') ? [IMPORT] : [])]) {
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
  m = replaceAll(m, /#\w+;/g, (s) => (/^#\+?\d+;$/.test(s) ? 'ﬂ°°' : 'ﬂ°') + s.slice(1, -1) + '¶ß');
  return { s: m.s, o: [...m.o] };
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
const block = /\["pie"\] = new\(\) \{([^}]*)\}/.exec(cs)?.[1] ?? '';
const labels = new Map([...block.matchAll(/\[(\d+)\] = "([A-Z0-9]*)"/g)].map((m) => [+m[1], m[2]]));

const fuzz = JSON.parse(fs.readFileSync('tests/goldens/charts/pie/fuzz.json', 'utf8'));
const PINNED = 'tests/fixtures/charts/pie.fuzz-deviations.json';
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

const seeds = JSON.parse(fs.readFileSync('tools/chart-seeds/pie.json', 'utf8'));
for (const { name, text } of seeds) {
  if (JSON.stringify(fixed.run(text)) !== JSON.stringify(upstream.run(text))) console.log('seed', name.padEnd(48), fixesThatChange(text).join('+'));
}
fs.rmSync(root, { recursive: true, force: true });
process.exit(bad ? 1 : 0);
