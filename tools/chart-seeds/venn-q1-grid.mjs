// The Q1 probe grid (PORT-008 Task 10, fix round 2): every printable ASCII character and a few others placed directly before
// `%%` in each lexer position venn has, run through upstream archify 3.2.0 as is (`node`), with Q1 alone (`q1`), and with
// all four venn fixes (`fixed`, venn-oracle.mjs). ChartParityTests.Venn_q1_grid_matches_the_patched_upstream checks that the
// port gives `fixed` for every probe (its line counted in the fence, UPSTREAM-007), and that every probe the fixes change
// is changed by Q1 alone, so the grid needs no hand-written row.
// Run from the repo root: node tools/chart-seeds/venn-q1-grid.mjs. It writes tests/fixtures/charts/venn-q1-grid.json.
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { FIXES, cleanup, variant } from './venn-oracle.mjs';

const CHARS = [...Array.from({ length: 95 }, (_, i) => String.fromCharCode(32 + i)), '\t', ' ', '　', 'é'];
// Each position, with `X` where the character goes.
const CONTEXTS = {
  'header': 'venn-beta{X}%% note\n  set A',
  'header-spaced': 'venn-beta {X}%% note\n  set A',
  'statement-start': 'venn-beta\n  set A\n  {X}%% note\n  set B',
  'after-id': 'venn-beta\n  set A{X}%% note\n  set B',
  'after-statement': 'venn-beta\n  set A {X}%% note\n  set B',
  'in-bracket-label': 'venn-beta\n  set A[{X}%% x]\n  set B',
  'after-bracket-label': 'venn-beta\n  set A["L"]{X}%% note\n  set B',
  'after-string': 'venn-beta\n  set "Q"{X}%% note\n  set B',
  'after-number': 'venn-beta\n  set A:5{X}%% note\n  set B',
  'after-union-list': 'venn-beta\n  set A\n  set B\n  union A,B{X}%% note',
  'after-text': 'venn-beta\n  set A\n    text t{X}%% note',
  'after-hex': 'venn-beta\n  set A\n  style A fill:#f00{X}%% note',
  'after-style-colon': 'venn-beta\n  set A\n  style A fill:{X}%% note',
  'after-title': 'venn-beta\n  title T{X}%% note\n  set A',
};
// A drawn result keeps its SVG as a SHA-256 (UTF-8, lower-case hex), so the fixture stays small; errors are kept whole.
const compact = (r) => (typeof r.svg === 'string' ? { ...r, svg: createHash('sha256').update(r.svg, 'utf8').digest('hex') } : r);
const node = await variant([]);
const q1 = await variant(['Q1']);
const fixed = await variant(FIXES);
const probes = [];
for (const [context, template] of Object.entries(CONTEXTS)) {
  for (const ch of CHARS) {
    const input = template.replace('{X}', ch);
    probes.push({ context, input, node: compact(node.run(input)), q1: compact(q1.run(input)), fixed: compact(fixed.run(input)) });
  }
}
cleanup();
fs.writeFileSync('tests/fixtures/charts/venn-q1-grid.json', '[\n' + probes.map((p) => JSON.stringify(p)).join(',\n') + '\n]\n');
const changed = probes.filter((p) => JSON.stringify(p.fixed) !== JSON.stringify(p.node)).length;
console.log(`${probes.length} probes, ${changed} changed by the fixes`);
