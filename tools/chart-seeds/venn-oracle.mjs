// The venn oracle of PORT-008 Task 10: upstream archify 3.2.0 charts.mjs with any of the port's venn fixes written into a
// copy of its vendored Pele venn chunk (one replacement each, asserted to occur once). Used by venn-fuzz-check.mjs and
// venn-q1-grid.mjs.
//   Q1  the second comment rule `[^}]%%` takes only whitespace before `%%` with it;
//   VN1 a keyword followed by `-` and more of a name (not a number) gives way to the name;
//   VN2 the space after `title` is not a line break;
//   VN3 the "unknown set identifier" message names the ids decoded (Pele's own decodeEntities), line breaks written back
//       as `#10;` and `#13;`.
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

export const FIXES = Object.keys(PATCHES);
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'venn-oracle-'));

// { run(text) -> compileExplainerChart result (a front-matter PeleError as {thrown}), render } for upstream with `names`.
export async function variant(names) {
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

export function cleanup() {
  fs.rmSync(root, { recursive: true, force: true });
}
