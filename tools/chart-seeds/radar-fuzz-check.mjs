// Checks the attribution of the radar fuzz deviations (PORT-008 Task 9) against upstream archify 3.2.0 charts.mjs, for
// ChartParityTests.Fuzz_deviations_are_the_stated_quirks: each case listed in tests/fixtures/charts/radar.fuzz-deviations.json
// (the port's output) must be what Node draws once the input is corrected by hand for the stated quirks, using code
// written apart from the port:
//   R1 a name that starts with circle, polygon, true, false or title: its first letter becomes one of the same advance
//      (c/k, p/b, t/f, f/t), which upstream reads as a name, and Node's output gets the word back;
//   R2 an entity code in a label: the label written as the chart draws it (Mermaid's `#…;` codes as their characters, then
//      HTML entities), so a "Missing entry" message names it decoded;
//   L1 (the line remap, UPSTREAM-007): an error's line is counted in the input as written. The checker prepares the text
//      as Pele's core does (line breaks, front matter, directives, comment lines, leading blanks, entity placeholders),
//      keeping where each character came from, checks that Node's snippet is that line of its preparation, and puts Node's
//      line and column on the input's line; an error past the end is on the last content line.
// Run from the repo root: node tools/chart-seeds/radar-fuzz-check.mjs. It prints each case and exits 1 if one fails.
import fs from 'node:fs';

const { compileExplainerChart } = await import('file:///C:/projects/archify/archify/explainer/charts.mjs');
const { render } = await import('file:///C:/projects/archify/archify/explainer/vendor/pele/core.js');
const fuzz = JSON.parse(fs.readFileSync('tests/goldens/charts/radar/fuzz.json', 'utf8'));
const pinned = JSON.parse(fs.readFileSync('tests/fixtures/charts/radar.fuzz-deviations.json', 'utf8'));
const run = (t) => { try { return compileExplainerChart(t); } catch (e) { return { thrown: { name: 'PeleError', message: e.message, code: e.code, line: e.line } }; } };

// R1. The stand-in is the word with its first letter swapped; `words` records each one to swap back in Node's output.
const STAND = { c: 'k', p: 'b', t: 'f', f: 't' };
const R1 = /(?<![A-Za-z0-9_-])(?:circle|polygon|true|false|title)(?=[-A-Za-z0-9_]*[A-Za-z0-9_])[-A-Za-z0-9_]*/g;
function standIns(text, words) {
  return text.replace(R1, (word) => {
    const standIn = STAND[word[0]] + word.slice(1);
    words.set(standIn, word);
    return standIn;
  });
}

// R2. A label is `[` then a quoted string; its entity codes are written as their characters. A character that would read
// as something else in a label (a quote, a backslash, markup, another entity) leaves the case unchecked.
const NAMED = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: '\u00a0', hearts: '\u2665', euro: '\u20ac' };
const code = (n) => (/^\+?\d+$/.test(n) ? String.fromCodePoint(+n) : NAMED[n] ?? `&${n};`);
const html = (s) => s.replace(/&(?:#(\d+)|#[xX]([0-9a-fA-F]+)|(\w+));/g, (m, d, h, n) =>
  d ? String.fromCodePoint(+d) : h ? String.fromCodePoint(parseInt(h, 16)) : NAMED[n] ?? m);
let unchecked = '';
function decodeLabels(text) {
  return text.replace(/\[([\t ]*)("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')/g, (m, blank, quoted) => {
    const body = quoted.slice(1, -1);
    const drawn = html(body.replace(/#(\w+);/g, (_, n) => code(n)));
    if (drawn === body) return m;
    if (/["'\\<>]|&\w+;|#\w+;/.test(drawn)) unchecked = `label ${JSON.stringify(body)} decodes to markup or a quote`;
    return `[${blank}${quoted[0]}${drawn}${quoted[0]}`;
  });
}

// A text with, for each character, its offset in the input: `edit` makes edits [{at, len, with}] (in order); a replacement
// as long as what it replaces keeps the origins, any other takes the first one's.
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

// What the radar parser reads: line breaks as \n, a front matter block at the start, `%%{…}%%` directives, comment lines
// and leading blanks out, `#…;` codes as Mermaid's placeholders, and Pele's appended "\n".
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
// Node's error on the input's line, or null for an error without one; 'mismatch' when the checker's preparation does not
// hold Pele's snippet on Pele's line.
function nodeLine(text) {
  try { render(text); return null; } catch (e) {
    if (!(e.line > 0)) return null;
    const p = prepare(text);
    const lines = p.s.split('\n');
    if ((lines[e.line - 1] ?? '') !== e.snippet.split('\n')[0]) return 'mismatch';
    let at = e.column - 1;
    for (let k = 0; k < e.line - 1; k++) at += lines[k].length + 1;
    return lineOf(text, p, at);
  }
}

let bad = 0;
for (const { index, output } of pinned) {
  const input = fuzz[index].input;
  const port = JSON.parse(output);
  const words = new Map();
  unchecked = '';
  const corrected = decodeLabels(standIns(input, words));
  let node = run(corrected);
  const line = node.error !== undefined && node.line > 0 ? nodeLine(corrected) : null;
  if (typeof line === 'number') node = { ...node, error: node.error.replace(new RegExp(`line ${node.line}(?![0-9])`), `line ${line}`), line };
  let shown = JSON.stringify(node);
  for (const [standIn, word] of words) shown = shown.replaceAll(standIn, word);
  const ok = !unchecked && line !== 'mismatch' && shown === JSON.stringify(port);
  if (!ok) bad++;
  const what = unchecked || (line === 'mismatch' ? 'checker preparation differs from Pele\'s' : port.svg !== undefined ? 'svg' : 'error ' + JSON.stringify(port.error));
  console.log(String(index).padStart(4), ok ? 'ok  ' : 'FAIL', what.slice(0, 60).padEnd(60), JSON.stringify(input).slice(0, 80));
}
console.log(`${pinned.length - bad} of ${pinned.length} attributed`);
process.exit(bad ? 1 : 0);
