#!/usr/bin/env node
// Dev-time only. The PORT-009 explainer seed runner (layout: README.md beside this file). tools/capture-fixtures.mjs runs it
// twice: `--oracle node` is upstream archify 3.2.0 as is, `--oracle fixed` is a copy of it with Decisions 2 and 3 written in.
// Usage: node tools/explainer-seeds/run.mjs --oracle node|fixed --out <dir>
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';

const UP = 'C:/projects/archify/archify';
const ROOT = path.resolve(fileURLToPath(import.meta.url), '..', '..', '..');
const SEEDS = path.join(ROOT, 'tools', 'explainer-seeds');
const flag = (name) => { const i = process.argv.indexOf(name); return i > 1 ? process.argv[i + 1] : undefined; };
const ORACLE = flag('--oracle'), OUT = flag('--out');
if (!['node', 'fixed'].includes(ORACLE) || !OUT) throw new Error('usage: run.mjs --oracle node|fixed --out <dir>');

const sha256 = (data) => createHash('sha256').update(data).digest('hex');
const temps = [];
process.on('exit', () => { process.chdir(ROOT); for (const d of temps) fs.rmSync(d, { recursive: true, force: true }); });
const temp = (prefix) => { const d = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), prefix))); temps.push(d); return d; };
// `from` must occur exactly once; `to` is inserted literally (no `$` patterns).
const replaceOnce = (text, from, to, what) => {
  if (text.split(from).length !== 2) throw new Error(`${what}: ${JSON.stringify(from)} does not occur exactly once`);
  return text.replace(from, () => to);
};

// --- the oracle: upstream's skill root, or a copy with the two decided fixes (plan Global Constraints, Fixed goldens) ---
const FIXES = {
  // Decision 3: a page without @meta locale is English.
  'explainer/render.mjs': [["const locale = document.meta.locale || 'zh-CN';", "const locale = document.meta.locale || 'en';"]],
  // Decision 2: a shorthand workflow's branch lanes are "Branch N" unless the page's locale starts with zh.
  'explainer/diagrams.mjs': [
    ["export function compileExplainerDiagram(diagramType, rows, { title, id, line = 1, quality = 'showcase' } = {}) {",
      "export function compileExplainerDiagram(diagramType, rows, { title, id, line = 1, quality = 'showcase', locale } = {}) {"],
    ['row ? `分支 ${row}` : spec.meta.title', "row ? (String(locale || 'en').startsWith('zh') ? `分支 ${row}` : `Branch ${row}`) : spec.meta.title"],
  ],
  'explainer/parse.mjs': [[',line:at+1,quality:document.meta.quality_profile});', ',line:at+1,quality:document.meta.quality_profile,locale:document.meta.locale});']],
};
let SKILL = UP;
if (ORACLE === 'fixed') {
  SKILL = path.join(temp('axs-fixed-'), 'archify');
  fs.cpSync(UP, SKILL, { recursive: true, filter: (src) => !path.relative(UP, src).split(path.sep).includes('node_modules') });
  for (const [file, edits] of Object.entries(FIXES)) {
    let text = fs.readFileSync(path.join(SKILL, file), 'utf8');
    for (const [from, to] of edits) text = replaceOnce(text, from, to, `fixed oracle ${file}`);
    fs.writeFileSync(path.join(SKILL, file), text);
  }
}
const MODULES = {
  parseExplainer: 'parse', parseInline: 'inline', renderInline: 'inline', parseExplainerDiff: 'diff', lintExplainerWriting: 'writing',
  validateExplainer: 'validate', compileExplainerDiagram: 'diagrams', prepareExplainerDiagrams: 'diagrams', validateExplainerDiagramTarget: 'diagrams',
  resolveExplainerSources: 'sources',
  prepareExplainer: 'prepare', readPreparedBundle: 'prepare', layoutExplainerPanels: 'layout', renderExplainer: 'render',
  checkExplainerHtml: 'check', preparePanelPatch: 'patch',
};
const fn = async (call) => {
  if (!MODULES[call]) throw new Error(`unknown call ${JSON.stringify(call)}`);
  return (await import(pathToFileURL(path.join(SKILL, 'explainer', `${MODULES[call]}.mjs`)).href))[call];
};

// --- the phase-4 evidence repository (`repo: true`), built as tools/capture-fixtures.mjs builds it, plus the explainer's
// commit on top (tests/fixtures/evidence/explainer-repo.fi: a secret, CRLF, Latin-1 and NUL file) and an origin ---
const EVIDENCE_HEAD = '77eb757055fa2c17900278114600cfe23c3957d1';
const EXPLAINER_HEAD = 'f691b4b9368318ae74b3c16e3bd96e9d33788e34';
const EXPLAINER_ORIGIN = 'https://github.com/example/evidence';
// `repo: true` has that origin; `repo: {origin}` names another (null: no origin). One repository per origin, built once.
let REPO;
const REPOS = new Map();
function evidenceRepo(origin) {
  if (REPOS.has(origin)) return REPO = REPOS.get(origin);
  const root = temp('axs-repo-');
  const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !k.toUpperCase().startsWith('GIT_')));
  Object.assign(env, { GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null' });
  const git = (args, input) => execFileSync('git', ['-C', root, ...args], { env, input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] }).trim();
  git(['init', '--quiet', '--template=', '--initial-branch=main']);
  git(['fast-import', '--quiet'], fs.readFileSync(path.join(ROOT, 'tests', 'fixtures', 'evidence', 'evidence-repo.fi')));
  const head = git(['rev-parse', 'HEAD']);
  if (head !== EVIDENCE_HEAD) throw new Error(`evidence-repo.fi builds ${head}, expected ${EVIDENCE_HEAD}`);
  git(['fast-import', '--quiet'], fs.readFileSync(path.join(ROOT, 'tests', 'fixtures', 'evidence', 'explainer-repo.fi')));
  if (origin !== null) git(['remote', 'add', 'origin', origin]);
  const explainer = git(['rev-parse', 'HEAD']);
  if (explainer !== EXPLAINER_HEAD) throw new Error(`explainer-repo.fi builds ${explainer}, expected ${EXPLAINER_HEAD}`);
  REPOS.set(origin, root);
  return REPO = root;
}
const repoOrigin = (repo) => {
  if (repo === true) return EXPLAINER_ORIGIN;
  if (repo && typeof repo === 'object' && (repo.origin === null || typeof repo.origin === 'string')) return repo.origin;
  throw new Error(`repo is true or {origin: string | null}, not ${JSON.stringify(repo)}`);
};

// --- seed inputs ---
// `files`: {path: {text} | {base64, zeros?} | {zeros: n} | null}, written into (null: removed from) the seed's directory.
// `zeros` after `base64` appends that many zero bytes (an image header padded to a size limit).
function writeFiles(dir, files) {
  for (const [rel, entry] of Object.entries(files ?? {})) {
    const file = path.join(dir, rel);
    if (entry === null) { fs.rmSync(file, { force: true }); continue; }
    const bytes = typeof entry.text === 'string' ? Buffer.from(entry.text)
      : typeof entry.base64 === 'string' ? Buffer.concat([Buffer.from(entry.base64, 'base64'), Buffer.alloc(Number.isInteger(entry.zeros) ? entry.zeros : 0)])
      : Number.isInteger(entry.zeros) ? Buffer.alloc(entry.zeros) : null;
    if (!bytes) throw new Error(`file ${rel}: needs {text}, {base64} or {zeros}`);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, bytes);
  }
}
// `<DIR>` and `<REPO>` in any string of an argument.
const substitute = (value, dir) => typeof value === 'string' ? value.replaceAll('<DIR>', dir).replaceAll('<REPO>', REPO ?? '<REPO>')
  : Array.isArray(value) ? value.map((v) => substitute(v, dir))
  : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([k, v]) => [k, substitute(v, dir)])) : value;
const tamper = (text, edits, what) => (edits ?? []).reduce((t, { from, to }) => replaceOnce(t, from, to, what), text);
// prepareExplainer upstream's way (render-explainer.mjs:12-14): {text, file = 'draft.archify.md', options}.
async function prepareDraft(seed, draft, dir) {
  const inputPath = path.join(dir, draft.file ?? 'draft.archify.md');
  fs.mkdirSync(path.dirname(inputPath), { recursive: true });
  fs.writeFileSync(inputPath, draft.text);
  const options = { ...substitute(draft.options ?? {}, dir), ...(seed.repo ? { repoRoot: REPO } : {}), inputPath };
  return { inputPath, bundle: await (await fn('prepareExplainer'))(draft.text, options) };
}
// The page render-explainer.mjs:23 writes for a draft, next to it.
async function renderDraft(seed, page, dir) {
  const { bundle } = await prepareDraft(seed, page.draft, dir);
  const html = tamper((await fn('renderExplainer'))(bundle.document, { ...bundle, preparedBundle: bundle, pageDirectory: dir }), page.tamper, `${seed.name} tamper`);
  fs.writeFileSync(path.join(dir, page.file ?? 'page.html'), html);
  return html;
}
const digest = (bundle) => sha256(JSON.stringify({ ...bundle, digest: undefined }));   // prepare.mjs:14
// {draft, mutate, read}: prepare draft A, write its bundle, apply one mutation, read it back against draft B.
async function readPrepared(seed, dir) {
  const { inputPath, bundle: prepared } = await prepareDraft(seed, seed.draft, dir);
  const bundle = JSON.parse(JSON.stringify(prepared));
  const mutate = seed.mutate ?? {};
  const kinds = Object.keys(mutate);
  if (kinds.length > 1 || kinds.some((k) => !['path', 'bytes', 'quality', 'writingMode', 'document', 'signed', 'dependency'].includes(k))) {
    throw new Error(`${seed.name}: mutate is one of path, bytes, quality, writingMode, document, signed, dependency`);
  }
  if ('path' in mutate) bundle.sourcePath = path.join(dir, mutate.path);
  if ('quality' in mutate) bundle.quality = mutate.quality;
  if ('writingMode' in mutate) { if (mutate.writingMode === null) delete bundle.writingMode; else bundle.writingMode = mutate.writingMode; }
  if ('document' in mutate) bundle.document = JSON.parse(tamper(JSON.stringify(bundle.document), mutate.document, `${seed.name} document`));
  if ('signed' in mutate) Object.assign(bundle, JSON.parse(tamper(JSON.stringify(bundle), mutate.signed, `${seed.name} signed`)));
  if (['path', 'quality', 'writingMode', 'document', 'signed'].some((k) => k in mutate)) bundle.digest = digest(bundle);
  const file = (await import(pathToFileURL(path.join(SKILL, 'explainer', 'prepare.mjs')).href)).writePreparedBundle(dir, bundle);
  if ('bytes' in mutate) fs.writeFileSync(file, tamper(fs.readFileSync(file, 'utf8'), mutate.bytes, `${seed.name} bytes`));
  if ('dependency' in mutate) writeFiles(dir, mutate.dependency);
  const read = seed.read ?? {};
  const inputB = read.file ? path.join(dir, read.file) : inputPath;
  if (read.file && typeof read.text === 'string') fs.writeFileSync(inputB, read.text);
  return (await fn('readPreparedBundle'))(file, read.text ?? seed.draft.text, inputB, substitute(read.options ?? {}, dir));
}

// --- tokenising (the C# side applies the same rules: ExplainerParityTests) ---
const htmlGzip = (s) => s.replace(/"htmlGzip":"([A-Za-z0-9+/=]*)"/g, (_, b64) => `"htmlGzip":"gz-sha256:${sha256(gunzipSync(Buffer.from(b64, 'base64')))}"`);
function tokeniseValue(value, key) {
  if (typeof value === 'string') {
    if (key === 'htmlGzip' && /^[A-Za-z0-9+/=]*$/.test(value)) return `gz-sha256:${sha256(gunzipSync(Buffer.from(value, 'base64')))}`;
    return htmlGzip(value);
  }
  if (Array.isArray(value)) return value.map((v) => tokeniseValue(v));
  if (!value || typeof value !== 'object') return value;
  if ((key === 'artifact' || key === 'expectedArtifact') && typeof value.sha256 === 'string' && typeof value.bytes === 'number') return '<artifact>';
  const result = Object.fromEntries(Object.entries(value).map(([k, v]) => [k, tokeniseValue(v, k)]));   // own `__proto__` keys stay keys
  if (result.kind === 'archify-explainer-prepared' && typeof result.digest === 'string') result.digest = '<digest>';
  if (key === 'diagrams') {
    for (const child of Object.values(result)) if (child && typeof child === 'object' && typeof child.html === 'string') child.html = `sha256:${sha256(child.html)}`;
  }
  return result;
}
const escaped = (p) => p.replaceAll('\\', '\\\\');
const spellings = (p) => [...new Set([escaped(p), p, p.replaceAll('\\', '/')])];
const TMP_ROOTS = [...new Set([os.tmpdir(), fs.realpathSync.native(os.tmpdir())])];
const reEscape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
function tokeniseText(text, dir) {
  let t = text;
  for (const [p, token] of [[dir, '<DIR>'], [REPO, '<REPO>']]) if (p) for (const s of spellings(p)) t = t.replaceAll(s, token);
  for (const root of TMP_ROOTS) for (const s of spellings(root)) t = t.replace(new RegExp(reEscape(s) + String.raw`(?:\\\\|\\|/)archify-explainer-[\w-]+`, 'g'), '<TMP>');
  // Determinism guard: any other temp path (the fixed oracle's copy, an unlisted temp file) would make the golden vary.
  for (const root of TMP_ROOTS) for (const s of spellings(root)) if (t.includes(s)) throw new Error(`untokenised temp path in output:\n${t.slice(0, 2000)}`);
  return t;
}

// --- the run: every tools/explainer-seeds/<group>.json, in name order ---
const seedRoot = temp('axs-seeds-');
for (const f of fs.readdirSync(SEEDS).filter((f) => f.endsWith('.json')).sort()) {
  const group = f.slice(0, -'.json'.length);
  const seeds = JSON.parse(fs.readFileSync(path.join(SEEDS, f), 'utf8'));
  const names = new Set();
  for (const seed of seeds) {
    // Golden file names: one per seed, unique ignoring case (the goldens live on Windows), never a `.fixed` golden's name.
    if (typeof seed.name !== 'string' || !/^[\w.-]+$/.test(seed.name) || seed.name.endsWith('.fixed') || names.has(seed.name.toLowerCase())) {
      throw new Error(`${f}: bad or duplicate seed name ${JSON.stringify(seed.name)}`);
    }
    names.add(seed.name.toLowerCase());
    const dir = path.join(seedRoot, group, seed.name);
    fs.mkdirSync(dir, { recursive: true });
    writeFiles(dir, seed.files);
    REPO = seed.repo ? evidenceRepo(repoOrigin(seed.repo)) : undefined;
    process.chdir(dir);   // relative paths resolve inside the seed's directory
    let result;
    try {
      if (seed.call === 'readPreparedBundle') result = await readPrepared(seed, dir);
      else if (seed.call === 'renderExplainer' && seed.draft) result = await renderDraft(seed, { draft: seed.draft }, dir);
      else {
        const args = substitute(seed.args ?? [], dir);
        if (seed.repo) {
          if (!args.length || !args.at(-1) || typeof args.at(-1) !== 'object' || Array.isArray(args.at(-1))) throw new Error(`${f}/${seed.name}: repo needs an options object as the last argument`);
          args.at(-1).repoRoot = REPO;
        }
        if (seed.page) {
          const html = await renderDraft(seed, seed.page, dir);
          if (seed.call === 'checkExplainerHtml') args.unshift(html);
        }
        // `parsed`: a draft text parsed first; its document and source map lead the arguments (args[0] adds options).
        if (typeof seed.parsed === 'string') {
          const draft = (await fn('parseExplainer'))(seed.parsed, { inputPath: args[0]?.inputPath });
          if (!draft.document) throw new Error(`${f}/${seed.name}: parsed draft does not parse: ${JSON.stringify(draft.diagnostics)}`);
          args.splice(0, 1, draft.document, { sourceMap: draft.sourceMap, ...(args[0] ?? {}) });
        }
        // `texts`: one call per text with the same context object (args[0]), as render.mjs shares footnotes and citations.
        if (Array.isArray(seed.texts)) {
          const context = args[0] ?? {};
          const html = [];
          for (const text of seed.texts) html.push(await (await fn(seed.call))(text, context));
          result = { html, footnotes: context.footnotes, citations: [...(context.citations ?? new Map())] };
        } else result = await (await fn(seed.call))(...args);
      }
    } catch (error) {
      if (!(error instanceof Error)) throw error;
      result = { thrown: { name: error.name, message: error.message, code: error.archifyDiagnostics ?? error.diagnostics ?? error.code } };
    }
    process.chdir(ROOT);
    if (result === undefined) throw new Error(`${f}/${seed.name}: ${seed.call} returned undefined`);
    const page = seed.call === 'renderExplainer' && typeof result === 'string';
    // Tokenised over the JSON view (JSON.parse(JSON.stringify)), so the golden is what JSON.stringify(result, null, 2) prints.
    const text = tokeniseText(page ? htmlGzip(result) : JSON.stringify(tokeniseValue(JSON.parse(JSON.stringify(result))), null, 2) + '\n', dir);
    const file = path.join(OUT, group, `${seed.name}.${page ? 'html' : 'json'}`);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
  }
}
