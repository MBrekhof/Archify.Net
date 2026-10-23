// Checks the attribution of the sankey fuzz deviations (PORT-008 Task 8) against upstream archify 3.2.0 charts.mjs, for
// ChartParityTests.Fuzz_deviations_are_the_stated_quirks: each case listed in tests/fixtures/charts/sankey.fuzz-deviations.json
// (the port's output) must be what Node draws once the input is corrected by hand for the stated quirks, using code
// written apart from the port:
//   S1 blanks at a row's edges (outside quotes, not a whole field next to a comma) removed;
//   S2 HTML entities in the text written as their characters;
//   S3 non-ASCII text replaced by ASCII stand-ins (one `u` per UTF-16 unit, in the input and in the port's SVG alike);
//   S4 a value that is not a whole decimal number: the port's error must name the first such value, and Node must report
//      nothing earlier.
// Run from the repo root: node tools/chart-seeds/sankey-fuzz-check.mjs. It prints each case and exits 1 if one fails.
import fs from 'node:fs';

const { compileExplainerChart } = await import('file:///C:/projects/archify/archify/explainer/charts.mjs');
const fuzz = JSON.parse(fs.readFileSync('tests/goldens/charts/sankey/fuzz.json', 'utf8'));
const pinned = JSON.parse(fs.readFileSync('tests/fixtures/charts/sankey.fuzz-deviations.json', 'utf8'));
const run = (t) => { try { return compileExplainerChart(t); } catch (e) { return { thrown: { name: 'PeleError', message: e.message, code: e.code, line: e.line } }; } };
const DECIMAL = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/;
const S4 = /^Sankey diagrams need a number for each link's value, and ("(?:[^"\\]|\\.)*") on line (\d+) is not one\.$/;

// S1, as stated: outside quotes, a run of blanks at a row's start or end goes, unless a comma next to it makes it a field.
function strip(body) {
  const rows = [];
  let row = '', quoted = false;
  for (let i = 0; i < body.length; i++) {
    const c = body[i];
    if (quoted) { row += c; if (c === '"') { if (body[i + 1] === '"') row += body[++i]; else quoted = false; } continue; }
    if (c === '\n' || c === '\r') { rows.push(row, c); row = ''; continue; }
    if (c === '"') quoted = true;
    row += c;
  }
  rows.push(row);
  let out = '';
  for (let k = 0; k < rows.length; k += 2) {
    const [, lead, mid, trail] = /^([^\S\n\r]*)([\s\S]*?)([^\S\n\r]*)$/.exec(rows[k]);
    out += (mid === '' ? '' : (mid.startsWith(',') ? lead : '') + mid + (mid.endsWith(',') ? trail : '')) + (rows[k + 1] ?? '');
  }
  return out;
}
// A decoded quote or apostrophe would change the CSV, so it becomes a typographic stand-in, which the comparison maps as it
// maps the port's escaped `&quot;` and `'`.
const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '\u201d', apos: '\u2019', pound: '\u00a3', euro: '\u20ac', times: '\u00d7', nbsp: '\u00a0' };
const point = (n) => (n === 34 ? '\u201d' : n === 39 ? '\u2019' : String.fromCodePoint(n));
const decode = (s) => s.replace(/&(?:#(\d+)|#[xX]([0-9a-fA-F]+)|(\w+));/g, (m, d, h, n) =>
  d ? point(+d) : h ? point(parseInt(h, 16)) : ENTITIES[n] ?? m);
// Mermaid's `#\u2026;` codes in the chart text (core encodeEntities, then decodeEntities): a number is its code point, a name
// its character, or `&name;` when it has none.
const mermaid = (s) => s.replace(/#(\w+);/g, (m, n) => (/^\+?\d+$/.test(n) ? point(+n) : ENTITIES[n] ?? `&${n};`));
// One stand-in per UTF-16 unit, distinct for distinct units, so names stay apart.
const ascii = (s) => s.replace(/[^\x00-\x7f]/g, (c) => {
  const n = c.charCodeAt(0);
  return 'Q' + String.fromCharCode(97 + (n % 26), 97 + (Math.floor(n / 26) % 26), 97 + (Math.floor(n / 676) % 26));
});
const normal = (result) => ascii(JSON.stringify(result).replaceAll('&quot;', '\u201d').replaceAll("'", '\u2019'));
// Front matter or a leading directive: preprocess removes both before sankey's preparation sees the text.
const HEAD = /^(\s*---[\s\S]*?\n---[^\n]*\n|%%\{[\s\S]*?\}%%[^\n]*\n)/;
const correct = (input) => {
  const head = HEAD.exec(input)?.[1] ?? '';
  // In the front matter only S2 applies (a prefix or suffix); YAML takes a tab as \t inside double quotes.
  return decode(head).replace(/\t/g, '\\t') + ascii(decode(mermaid(strip(input.slice(head.length)))));
};

// The text sankey's parser sees, with S1 applied: preprocess's comment lines out, the row edges stripped, line breaks
// collapsed, trimmed.
const prepare = (input) => {
  let body = input.slice((HEAD.exec(input)?.[1] ?? '').length);
  if (body.includes('%%')) body = body.replace(/^\s*%%(?!\{)[^\n]+\n?/gm, '');
  return strip(body.trimStart()).replace(/[\n\r]+/g, '\n').trim();
};
// Pele 1.9.0's alphabet (its `c0`) and its name check, written out again.
const textData = (c) => (c >= 32 && c <= 126 && c !== 34 && c !== 44) || (c >= 0xa0 && c <= 0xd7ff) || (c >= 0xe000 && c <= 0xfffd) || c >= 0x10000;
const INVALID_XML = /[\x00-\x08\x0b\x0c\x0e-\x1f\ud800-\udfff\ufffe\uffff]/u;
// S4's own reader: the first row that ends well and whose value is not a whole decimal, as {value, line}; null when the
// rows end (or one breaks, which is a parse error) first.
function firstBadValue(text) {
  const header = text.indexOf('\n');
  if (header < 0 || !/^sankey(-beta)?$/i.test(text.slice(0, header))) return null;
  const line = (at) => text.slice(0, at).split('\n').length;
  let p = header + 1;
  for (;;) {
    const fields = [];
    for (let f = 0; f < 3; f++) {
      const start = p;
      let value;
      if (text[p] === '"') {
        let q = p + 1;
        value = '';
        for (;;) {
          if (q >= text.length) return null;
          const c = text.codePointAt(q);
          if (c === 34) { if (text[q + 1] === '"') { value += '""'; q += 2; continue; } break; }
          if (!(textData(c) || c === 44 || c === 10 || c === 13)) return null;
          value += String.fromCodePoint(c);
          q += c > 0xffff ? 2 : 1;
        }
        if (value === '') return null;   // `""` gives no text token
        p = q + 1;
      } else {
        let q = p;
        while (q < text.length && textData(text.codePointAt(q))) q += text.codePointAt(q) > 0xffff ? 2 : 1;
        if (q === p) return null;
        value = text.slice(p, q);
        p = q;
      }
      fields.push({ value: value.trim(), start });
      if (f < 2) {
        if (text[p] !== ',') return null;
        p++;
      }
    }
    for (const name of fields.slice(0, 2)) if (INVALID_XML.test(decode(mermaid(name.value.replaceAll('""', '"'))))) return null;
    if (p < text.length && text[p] !== '\n') return null;
    if (!DECIMAL.test(fields[2].value)) return { value: fields[2].value, line: line(fields[2].start) };
    if (p >= text.length) return null;
    p++;
  }
}

let bad = 0;
for (const { index, output } of pinned) {
  const input = fuzz[index].input;
  const port = JSON.parse(output);
  const node = run(correct(input));
  let ok, why = '';
  const m = S4.exec(port.error ?? '');
  const expected = firstBadValue(prepare(input));
  if (m) {
    // The port must name exactly the value and line the reader finds, and Node nothing earlier.
    const line = +m[2];
    const value = JSON.parse(m[1]);
    const earlier = node.error !== undefined && node.line > 0 && node.line < line;
    ok = expected !== null && expected.value === value && expected.line === line && !earlier;
    why = `S4 ${m[1].slice(0, 30)} line ${line}; reader ${expected ? JSON.stringify(expected.value).slice(0, 20) + ' line ' + expected.line : 'none'}`;
  } else {
    // Both sides mapped, as the SVG has non-ASCII of its own (the `→` of a band's title); and no unreadable value to report.
    ok = normal(node) === normal(port) && expected === null;
    why = (port.svg !== undefined ? 'svg' : 'error ' + JSON.stringify(port.error)) + (expected ? ' (reader expects S4)' : '');
  }
  if (!ok) bad++;
  console.log(String(index).padStart(4), ok ? 'ok  ' : 'FAIL', why.padEnd(60), JSON.stringify(input).slice(0, 80));
}
console.log(`${pinned.length - bad} of ${pinned.length} attributed`);
process.exit(bad ? 1 : 0);
