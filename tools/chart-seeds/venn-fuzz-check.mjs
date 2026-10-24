// Checks the venn deviations (PORT-008 Task 10) against upstream archify 3.2.0 charts.mjs, for
// ChartParityTests.Fuzz_deviations_are_the_stated_quirks. The oracle is upstream itself with each fix written into a copy of
// its vendored Pele venn chunk (one replacement each, asserted to occur once), so it is code apart from the port:
//   Q1  the second comment rule `[^}]%%` takes only whitespace before `%%` with it;
//   VN1 a keyword followed by `-` and more of a name (not a number) gives way to the name;
//   VN2 the space after `title` is not a line break;
//   VN3 the "unknown set identifier" message names the ids decoded (Pele's own decodeEntities), line breaks written back
//       as `#10;` and `#13;`;
//   L1  (the line remap, UPSTREAM-007) an error's line is counted in the input as written: the input is prepared as Pele's
//       core does, keeping where each character came from, Node's snippet must be that line of the preparation, and the
//       error goes to the input's line; an error past the end is on the last content line.
// Each fuzz case pinned in tests/fixtures/charts/venn.fuzz-deviations.json (the port's output): the fixes together, then the
// line remap, must give that output, and the fixes that change it one at a time, plus L1, must be among the quirks
// ChartParityTests lists for it. Every other case: the fixes together must change nothing (the port equals Node there), so
// the fixes are no wider than the port's. It also lists the venn seeds that the fixes change, which must be the deviation
// rows (VennQuirkRows and VennSwapRows).
// Run from the repo root: node tools/chart-seeds/venn-fuzz-check.mjs. It prints each pinned case and exits 1 if one fails.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const UP = 'C:/projects/archify/archify/explainer';
const CHUNK = 'vendor/pele/chunks/EEIX5ZYH.js';   // the venn diagram chunk of archify 3.2.0's vendored Pele 1.3.0
const PATCHES = {
  Q1: [
    'n!==125&&e.charCodeAt(t+1)===37&&e.charCodeAt(t+2)===37',
    'F(n)&&e.charCodeAt(t+1)===37&&e.charCodeAt(t+2)===37',   // F is chars.ts isSpace in this chunk
  ],
  VN1: [
    'for(let[s,r]of xt)if(this.keyword(t,s))return',
    'for(let[s,r]of xt)if(this.keyword(t,s)&&!(e.charCodeAt(t+s.length)===45&&!(/[0-9]/.test(e[t+s.length+1]??"")||e[t+s.length+1]==="."&&/[0-9]/.test(e[t+s.length+2]??""))))return',
  ],
  VN2: ['this.word(t,"title")&&F(e.charCodeAt(t+5))', 'this.word(t,"title")&&F(e.charCodeAt(t+5))&&!/[\\n\\r]/.test(e[t+5])'],
  VN3: ['unknown set identifier: ${a.join(", ")}', 'unknown set identifier: ${a.map(x=>VN3decode(x).replace(/\\n/g,"#10;").replace(/\\r/g,"#13;")).join(", ")}'],
};
// VN3 needs Pele's decodeEntities, which the chunk does not import yet.
const IMPORT = ['import{a as V,c as H,g as ft}from"./XTDUIGUI.js"', 'import{a as V,c as H,g as ft,d as VN3decode}from"./XTDUIGUI.js"'];

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'venn-oracle-'));
async function variant(names) {
  const dir = path.join(root, names.join('-') || 'none');
  fs.cpSync(path.join(UP, 'vendor'), path.join(dir, 'vendor'), { recursive: true });
  fs.copyFileSync(path.join(UP, 'charts.mjs'), path.join(dir, 'charts.mjs'));
  let src = fs.readFileSync(path.join(dir, CHUNK), 'utf8');
  for (const [from, to] of [...names.map((n) => PATCHES[n]), ...(names.includes('VN3') ? [IMPORT] : [])]) {
    if (src.split(from).length !== 2) throw new Error(`patch anchor not found once: ${from}`);
    src = src.replace(from, () => to);
  }
  fs.writeFileSync(path.join(dir, CHUNK), src);
  const charts = await import(pathToFileURL(path.join(dir, 'charts.mjs')).href);
  const core = await import(pathToFileURL(path.join(dir, 'vendor/pele/core.js')).href);
  const run = (t) => { try { return charts.compileExplainerChart(t); } catch (e) { return { thrown: { name: 'PeleError', message: e.message, code: e.code, line: e.line } }; } };
  return { run, render: core.render };
}
const NAMES = Object.keys(PATCHES);
const upstream = await variant([]);
const single = Object.fromEntries(await Promise.all(NAMES.map(async (n) => [n, await variant([n])])));
const fixed = await variant(NAMES);

// A text with, for each character, its offset in the input (as radar-fuzz-check.mjs).
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
// What the venn parser reads: line breaks as \n, a front matter block at the start, `%%{…}%%` directives, comment lines and
// leading blanks out, `#…;` codes as Mermaid's placeholders, and Pele's appended "\n".
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
  return { s: m.s + '\n', o: [...m.o] };
}
// Node's error (with the fixes) on the input's line, or null for an error without one; 'mismatch' when the preparation
// does not hold Pele's snippet on Pele's line.
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

// The labels ChartParityTests gives the venn fuzz cases.
const cs = fs.readFileSync('tests/Archify.Net.Core.Tests/ChartParityTests.cs', 'utf8');
const block = /\["venn"\] = new\(\) \{([^}]*)\}/.exec(cs)?.[1] ?? '';
const labels = new Map([...block.matchAll(/\[(\d+)\] = "([A-Z0-9]*)"/g)].map((m) => [+m[1], m[2]]));

const fuzz = JSON.parse(fs.readFileSync('tests/goldens/charts/venn/fuzz.json', 'utf8'));
const PINNED = 'tests/fixtures/charts/venn.fuzz-deviations.json';
const pinned = new Map(fs.existsSync(PINNED) ? JSON.parse(fs.readFileSync(PINNED, 'utf8')).map((p) => [p.index, p.output]) : []);
let bad = 0;
for (let index = 0; index < fuzz.length; index++) {
  const { input } = fuzz[index];
  if (!pinned.has(index)) {
    // The port equals Node here (Matches_node), so the fixes must change nothing.
    if (JSON.stringify(fixed.run(input)) === JSON.stringify(upstream.run(input)) && !labels.has(index)) continue;
    bad++;
    console.log(String(index).padStart(4), 'FAIL', `the fixes change an unlisted case: ${fixesThatChange(input).join('+')}`.padEnd(65), JSON.stringify(input).slice(0, 70));
    continue;
  }
  const { node, remapped } = expected(input);
  const port = pinned.has(index) ? JSON.parse(pinned.get(index)) : upstream.run(input);
  const why = [...fixesThatChange(input), ...(remapped ? ['L1'] : [])];
  const label = labels.get(index) ?? '';
  // As in ChartParityTests, a case with a quirk can be moved by the remap too and carry no L1.
  const fixes = why.filter((q) => q !== 'L1');
  const named = (fixes.length > 0 ? fixes.every((q) => label.includes(q)) : label.includes('L1')) && pinned.has(index) === labels.has(index);
  const ok = node !== null && JSON.stringify(node) === JSON.stringify(port) && named;
  if (!ok) bad++;
  if (!pinned.has(index) && ok) continue;
  const what = node === null ? 'checker preparation differs from Pele\'s' : !named ? `fixes ${why.join('+') || 'none'} vs label ${label || 'none'}` : port.svg !== undefined ? 'svg' : 'error ' + JSON.stringify(port.error ?? port.thrown?.message);
  console.log(String(index).padStart(4), ok ? 'ok  ' : 'FAIL', (label || '-').padEnd(8), what.slice(0, 56).padEnd(56), JSON.stringify(input).slice(0, 70));
}
console.log(`${pinned.size} pinned, ${bad} failing of ${fuzz.length} fuzz cases`);

// The seeds the fixes change, with the fixes that change each.
const seeds = JSON.parse(fs.readFileSync('tools/chart-seeds/venn.json', 'utf8'));
for (const { name, text } of seeds) {
  if (JSON.stringify(fixed.run(text)) !== JSON.stringify(upstream.run(text))) console.log('seed', name.padEnd(48), fixesThatChange(text).join('+'));
}
fs.rmSync(root, { recursive: true, force: true });
process.exit(bad ? 1 : 0);
