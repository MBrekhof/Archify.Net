#!/usr/bin/env node
// Dev-time only. Mints the expected outputs of upstream's `archify` CLI (bin/archify.mjs) for the C# CLI suite.
// Usage: node tools/capture-cli-fixtures.mjs   (from repo root; needs C:\projects\archify at tag v3.2.0, commit 208acba8)
// `--only explainer` is a development shortcut the capture never uses: it re-mints the explainer group alone and reads every
// earlier fixture back from disk.
//
// Each case runs `node <SKILL>/bin/archify.mjs …args` in a fresh temp workdir seeded with `files` and writes
// tests/fixtures/cli/<group>/<name>.json = {args, files, exit, stdout, stderr}. `files` maps a workdir-relative name to the
// repo-relative file it was copied from; a C# test rebuilds the same workdir, runs the same args and tokenises its output
// the same way: the workdir becomes <WORK> and the skill root <SKILL>, both raw and JSON-escaped (`\` doubled).
// Also mints upstream/recipes/scenarios.json from recipes/scenarios.mjs SCENARIO_RECIPES.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';

const UP = 'C:/projects/archify/archify';
const ROOT = path.resolve(fileURLToPath(import.meta.url), '..', '..');
const CLI_FIX = 'tests/fixtures/cli';
const EX = 'upstream/examples';
const repo = (rel) => path.join(ROOT, rel);
const writeJson = (rel, value) => {
  fs.mkdirSync(path.dirname(repo(rel)), { recursive: true });
  fs.writeFileSync(repo(rel), JSON.stringify(value, null, 2) + '\n');
};

// --- scenarios.json: the recipe data the C# guide embeds (Q9) ---
const { SCENARIO_RECIPES } = await import(pathToFileURL(path.join(UP, 'recipes/scenarios.mjs')).href);
writeJson('upstream/recipes/scenarios.json', SCENARIO_RECIPES);

// --- a skill copy without node_modules, as an installed skill runs (survey-cli §5.2) ---
const tmpRoot = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'archify-cli-fixtures-')));
process.on('exit', () => fs.rmSync(tmpRoot, { recursive: true, force: true }));   // also when an expectation throws
const SKILL = path.join(tmpRoot, 'skill');
fs.cpSync(UP, SKILL, { recursive: true, filter: (src) => !path.relative(UP, src).split(path.sep).includes('node_modules') });
const cli = path.join(SKILL, 'bin', 'archify.mjs');

// The C# CLI reads no ARCHIFY_* variable (Q3); a stray one in the developer's shell must not reach the child either.
const env = { ...process.env, ARCHIFY_UPDATE_CHECK_DISABLED: '1' };
for (const k of Object.keys(env)) if (k.startsWith('ARCHIFY_') && k !== 'ARCHIFY_UPDATE_CHECK_DISABLED') delete env[k];

// Q7/Q8: browser-gate fixtures drive upstream with ARCHIFY_CHROME (child env only) at the headless shell `archify install-browser`
// installs, the binary the C# inspector launches; its path is <CHROME> in every fixture.
const CHROME = path.join(process.env.LOCALAPPDATA ?? '', 'ms-playwright', 'chromium_headless_shell-1243', 'chrome-headless-shell-win64', 'chrome-headless-shell.exe');
const chromeEnv = { ...env, ARCHIFY_CHROME: CHROME };

const escaped = (p) => p.replaceAll('\\', '\\\\');
function tokenise(text, work) {
  let t = text;
  for (const [p, token] of [[work, '<WORK>'], [SKILL, '<SKILL>'], [CHROME, '<CHROME>']]) t = t.replaceAll(escaped(p), token).replaceAll(p, token);
  // A raw Node stack (render explainer, Review Focus 1) names modules by file URL.
  t = t.replaceAll(pathToFileURL(SKILL).href, '<SKILL>');
  // Determinism guard: any temp path left over (an owned temp dir, a pid-named layout file) would make the fixture vary.
  if (t.includes(tmpRoot) || t.includes(escaped(tmpRoot)) || t.includes(path.basename(tmpRoot))) {
    throw new Error(`untokenised temp path in output:\n${t}`);
  }
  return t;
}

const ONLY = process.argv.includes('--only') ? process.argv[process.argv.indexOf('--only') + 1] : undefined;
if (ONLY !== undefined && ONLY !== 'explainer') throw new Error('--only takes explainer');
fs.rmSync(repo(ONLY ? `${CLI_FIX}/explainer` : CLI_FIX), { recursive: true, force: true });
const minted = new Set();
// `setup` steps run in the workdir after seeding and before the recorded command; a C# test replays the same list. Each step is
// one of {run: args, exit} (a CLI run that must exit so), {copy: from, to}, {delete: p}, {append: p, text}, {write: p, text},
// {set: p, key, value} (a JSON file rewritten as indent 2 + "\n"; "<WORK>" in a string value is the workdir), {mkdir: p},
// {symlink: p, target} (a file symbolic link to a workdir-relative target), {replace: p, from, to} (the one occurrence of `from` in a
// text file), {tamper: p, from, to} (the first occurrence of `from`) or {denyCreate: p} (a directory where Everyone is denied creating files and subdirectories: icacls (WD,AD)). Paths are
// workdir-relative.
function applySetup(work, steps, cliPath = cli) {
  const at = (p) => path.join(work, p);
  for (const step of steps) {
    if (step.run) {
      const r = spawnSync(process.execPath, [cliPath, ...step.run], { cwd: work, env, encoding: 'utf8' });
      if (r.error) throw r.error;
      expect(r.status === step.exit, `setup ${step.run.join(' ')} exits ${step.exit}, got ${r.status}: ${r.stdout}${r.stderr}`);
    } else if (step.copy) fs.copyFileSync(at(step.copy), at(step.to));
    else if (step.delete) fs.rmSync(at(step.delete));
    else if (step.append) fs.appendFileSync(at(step.append), step.text);
    else if (step.write) fs.writeFileSync(at(step.write), step.text);
    else if (step.set) {
      const value = typeof step.value === 'string' ? step.value.replaceAll('<WORK>', work) : step.value;
      fs.writeFileSync(at(step.set), `${JSON.stringify({ ...JSON.parse(fs.readFileSync(at(step.set), 'utf8')), [step.key]: value }, null, 2)}\n`);
    } else if (step.mkdir) fs.mkdirSync(at(step.mkdir));
    else if (step.symlink) fs.symlinkSync(step.target, at(step.symlink), 'file');
    else if (step.replace) {
      const text = fs.readFileSync(at(step.replace), 'utf8');
      expect(text.split(step.from).length === 2, `setup replace: one ${JSON.stringify(step.from)} in ${step.replace}`);
      fs.writeFileSync(at(step.replace), text.replace(step.from, step.to));
    } else if (step.tamper) {
      // the first occurrence of `from`, as String.prototype.replace (T/explainer-browser.test.mjs:49)
      const text = fs.readFileSync(at(step.tamper), 'utf8');
      expect(text.includes(step.from), `setup tamper: ${JSON.stringify(step.from)} in ${step.tamper}`);
      fs.writeFileSync(at(step.tamper), text.replace(step.from, () => step.to));
    } else if (step.denyCreate) {
      const r = spawnSync('icacls', [at(step.denyCreate), '/deny', '*S-1-1-0:(WD,AD)'], { encoding: 'utf8' });
      expect(r.status === 0, `setup denyCreate ${step.denyCreate}: ${r.stdout}${r.stderr}`);
    } else throw new Error(`unknown setup step ${JSON.stringify(step)}`);
  }
}
// `inspect(work, fixture)` runs before the workdir is removed (output-byte assertions).
function mint(group, name, args, files = {}, inspect = () => {}, setup = undefined, childEnv = env) {
  const rel = `${CLI_FIX}/${group}/${name}.json`;
  if (minted.has(rel)) throw new Error(`duplicate fixture ${rel}`);
  if (ONLY && group !== ONLY && fs.existsSync(repo(rel))) {
    minted.add(rel);
    return { fixture: JSON.parse(fs.readFileSync(repo(rel), 'utf8')) };
  }
  const work = fs.mkdtempSync(path.join(tmpRoot, 'w-'));
  try {
    for (const [dest, src] of Object.entries(files)) {
      fs.mkdirSync(path.dirname(path.join(work, dest)), { recursive: true });
      fs.copyFileSync(repo(src), path.join(work, dest));
    }
    if (setup) applySetup(work, setup);
    const r = spawnSync(process.execPath, [cli, ...args], { cwd: work, env: childEnv, encoding: 'utf8' });
    if (r.error) throw r.error;
    const fixture = { args, files, ...(setup ? { setup } : {}), exit: r.status, stdout: tokenise(r.stdout, work), stderr: tokenise(r.stderr, work) };
    inspect(work, fixture);
    writeJson(rel, fixture);
    minted.add(rel);
    return { fixture };
  } finally {
    fs.rmSync(work, { recursive: true, force: true });
  }
}
const expect = (cond, what) => { if (!cond) throw new Error(`fixture expectation failed: ${what}`); };
const sameBytes = (a, b) => Buffer.compare(fs.readFileSync(a), fs.readFileSync(b)) === 0;

// --- crafted inputs (committed beside the fixtures so a C# test can seed the same workdir) ---
const INPUTS = `${CLI_FIX}/inputs`;
const example = (f) => JSON.parse(fs.readFileSync(repo(`${EX}/${f}`), 'utf8'));
const inputs = {};
function craft(name, content) {
  const rel = `${INPUTS}/${name}`;
  fs.mkdirSync(path.dirname(repo(rel)), { recursive: true });
  fs.writeFileSync(repo(rel), content);
  inputs[name] = rel;
}
// T/render-failure-diagnostics.test.mjs:99-112: rejected inside validateArchitecture (component-label-overflow).
const overflow = {
  schema_version: 1,
  diagram_type: 'architecture',
  meta: { title: 'Wide label', output: 'wide-label.html', quality_profile: 'standard', viewBox: [975, 395] },
  components: [{ id: 'node', type: 'security', label: '字'.repeat(40), pos: [40, 40], size: [88, 71] }],
};
craft('overflow-label.architecture.json', JSON.stringify(overflow));
// Same, plus a locale warning that must not displace the layout error (T:render-failure-diagnostics.test.mjs:126-140).
craft('overflow-label-locale.architecture.json', JSON.stringify({ ...overflow, meta: { ...overflow.meta, locale: 'en-US' } }));
// A success that carries a locale warning (stderr `archify: …`, `diagnostics` in the receipt).
const localeWarn = example('web-app.architecture.json'); localeWarn.meta.locale = 'en-US';
craft('locale-warning.architecture.json', JSON.stringify(localeWarn, null, 2));
// T/cli.test.mjs:4747-4752: an unknown edge endpoint, rejected before any measurement.
const ghost = example('agent-tool-call.workflow.json'); ghost.edges[0].to = 'ghost';
craft('ghost.workflow.json', JSON.stringify(ghost));
// Schema-valid but too small for the measured plan: the layout receipt carries viewBox/requiredViewBox and ok:false.
const small = example('agent-tool-call.workflow.json'); small.meta.viewBox = [700, 300];
craft('small-viewbox.workflow.json', JSON.stringify(small, null, 2));
// T/cli.test.mjs:4419-4467: one primary column-capacity diagnostic.
craft('column-capacity.workflow.json', `${JSON.stringify({
  schema_version: 1,
  diagram_type: 'workflow',
  meta: { title: 'Pinned issue 126 diagnostic boundary', output: 'pinned-column-capacity.html', viewBox: [720, 400], legend: { mode: 'hidden' } },
  lanes: [{ id: 'main', label: 'Main' }],
  nodes: [
    { id: 'a', lane: 'main', col: 1, type: 'backend', label: 'A' },
    { id: 'b', lane: 'main', col: 2, type: 'backend', label: 'B' },
  ],
  edges: [{ id: 'ab', from: 'a', to: 'b', fromSide: 'top', toSide: 'top', via: [[220, 60], [300, 60]] }],
}, null, 2)}\n`);
// Schema failure: without meta.output the CLI pre-gate runs validateSchema; with it, only the output path is checked there
// and the renderer's own load gate rejects the schema (A:6866-6867).
const schemaNoOutput = example('web-app.architecture.json'); delete schemaNoOutput.meta.output; schemaNoOutput.components[0].colour = 'red';
craft('schema-failure.architecture.json', JSON.stringify(schemaNoOutput, null, 2));
const schemaOutput = example('web-app.architecture.json'); schemaOutput.components[0].colour = 'red';
craft('schema-failure-output.architecture.json', JSON.stringify(schemaOutput, null, 2));
craft('broken.workflow.json', '{"broken":');
// validate's pre-gate reports an OutputPathError's diagnostics as spread (output-path.mjs:359-370; A:6866-6878).
const badMetaOutput = example('web-app.architecture.json'); badMetaOutput.meta.output = 'out.txt';
craft('bad-meta-output.architecture.json', JSON.stringify(badMetaOutput, null, 2));
// A UTF-8 BOM: Node's JSON.parse does not strip it (survey-cli §2.2 step 3).
craft('bom.architecture.json', '\ufeff' + fs.readFileSync(repo(`${EX}/web-app.architecture.json`), 'utf8'));

// --- help ---
const IN_COMMANDS = ['render', 'deliver', 'patch', 'finalize', 'validate', 'inspect', 'check', 'browser-check', 'visual-check', 'guide',
  'examples', 'doctor', 'demo'];
mint('help', 'no-args', []);
mint('help', 'double-dash-help', ['--help']);
mint('help', 'help', ['help']);
mint('help', 'dash-h', ['-h']);
for (const c of IN_COMMANDS) mint('help', `${c}-help`, [c, '--help']);
mint('help', 'unknown-command', ['bogus']);
mint('help', 'not-a-command-help', ['not-a-command', '--help']);

// --- arguments: T/cli.test.mjs:4535-4644 receipt cases, their human counterparts, per-command option errors ---
const wf = 'agent-tool-call.workflow.json';
const seq = 'cache-miss-request.sequence.json';
const arch = 'web-app.architecture.json';
const exFiles = (...names) => Object.fromEntries(names.map((n) => [n, `${EX}/${n}`]));
const receiptCases = [
  ['validate-usage-no-args', ['validate', '--json']],
  ['validate-quality-invalid', ['validate', '--json', 'workflow', wf, '--quality', 'hero']],
  ['validate-quality-missing', ['validate', 'workflow', wf, '--quality', '--json']],
  ['validate-quality-empty', ['validate', 'workflow', wf, '--quality=', '--json']],
  ['validate-layout-json-assignment', ['validate', 'workflow', wf, '--layout-json=true', '--json']],
  ['validate-unknown-option-first', ['validate', '--bogus', 'payload', 'workflow', wf, '--json']],
  ['validate-extra-positional', ['validate', 'workflow', wf, 'unexpected-output.html', '--json']],
  ['validate-unknown-type', ['validate', 'unknown', wf, '--json']],
  ['validate-repo-root-empty', ['validate', 'architecture', wf, '--repo-root=', '--json']],
  ['validate-layout-json-unsupported', ['validate', 'sequence', seq, '--layout-json', '--json']],
  ['deliver-unknown-option', ['deliver', '--json', 'workflow', wf, '--bogus']],
  ['deliver-usage-no-args', ['deliver', '--json']],
  ['deliver-repo-root-missing', ['deliver', 'workflow', wf, '--repo-root', '--json']],
  ['deliver-unknown-type', ['deliver', 'unknown', wf, '--json']],
  ['deliver-extra-positional', ['deliver', 'workflow', wf, 'diagram.html', 'extra.html', '--json']],
  ['inspect-unknown-option', ['inspect', 'architecture', arch, '--json', '--bogus']],
  ['inspect-usage-missing-input', ['inspect', 'architecture', '--json']],
  ['inspect-extra-positional', ['inspect', 'architecture', arch, 'extra.json', '--json']],
  ['inspect-unsupported-type', ['inspect', 'workflow', wf, '--json']],
];
expect(receiptCases.length === 19, '19 receipt cases');
const filesFor = (args) => exFiles(...[wf, seq, arch].filter((n) => args.includes(n)));
for (const [name, args] of receiptCases) {
  const { fixture } = mint('arguments', `${name}-json`, args, filesFor(args));
  expect(fixture.exit === 2 && fixture.stderr === '' && JSON.parse(fixture.stdout).stage === 'arguments', `${name}-json receipt`);
  // Human counterpart: the same argv without --json reports on stderr (A:7131-7138).
  const human = args.filter((a) => a !== '--json');
  const h = mint('arguments', name, human, filesFor(human)).fixture;
  expect(h.exit === 2 && h.stdout === '', `${name} human`);
}
// T/cli.test.mjs:211-232: `--help` mixed into validate args is an unknown option.
mint('arguments', 'validate-help-json', ['validate', '--help', '--json']);
// T/cli.test.mjs:4489-4532 forms the receipt battery does not already cover.
mint('arguments', 'validate-quality-invalid-trailing', ['validate', 'workflow', wf, '--quality', 'hero'], exFiles(wf));
mint('arguments', 'deliver-quality-empty', ['deliver', 'workflow', wf, '--quality='], exFiles(wf));
mint('arguments', 'validate-layout-json-unknown-option', ['validate', 'workflow', wf, '--layout-json', '--bogus'], exFiles(wf));
mint('arguments', 'validate-layout-json-extra-positional', ['validate', 'workflow', wf, 'unexpected-output.html', '--layout-json'], exFiles(wf));
// T/cli.test.mjs:4754-4766: the type is rejected before the (missing) input is read.
mint('arguments', 'validate-unknown-type-missing-input', ['validate', 'unknown', 'ignored.json']);
// render (A:4233-4249; T:4768-4791)
mint('arguments', 'render-no-args', ['render']);
mint('arguments', 'render-missing-input', ['render', 'architecture']);
mint('arguments', 'render-unknown-option', ['render', 'architecture', arch, '--json', 'out.html'], exFiles(arch));
mint('arguments', 'render-extra-positional', ['render', 'architecture', arch, 'out.html', 'extra.html'], exFiles(arch));
mint('arguments', 'render-unknown-type', ['render', 'bogus', arch], exFiles(arch));
mint('arguments', 'render-quality-invalid', ['render', 'architecture', arch, '--quality', 'hero'], exFiles(arch));
mint('arguments', 'render-quality-missing', ['render', 'architecture', arch, '--quality'], exFiles(arch));
mint('arguments', 'render-repo-root-missing', ['render', 'architecture', arch, '--repo-root'], exFiles(arch));
// check (A:5470-5545; T:4375-4391)
mint('arguments', 'check-no-args', ['check']);
mint('arguments', 'check-unknown-option', ['check', '--strict-json', 'web-app-rendered.html'], exFiles('web-app-rendered.html'));
mint('arguments', 'check-extra-positional', ['check', 'web-app-rendered.html', 'ignored-extra'], exFiles('web-app-rendered.html'));
// examples, doctor (T:307-317)
for (const c of ['examples', 'doctor']) {
  mint('arguments', `${c}-extra-positional`, [c, 'ignored-extra']);
  mint('arguments', `${c}-unknown-option`, [c, '--bogus']);
}
// demo (T:419-428)
mint('arguments', 'demo-unknown-option', ['demo', '--typo']);
mint('arguments', 'demo-extra-positional', ['demo', 'one', 'two']);
// guide (A:6054-6080)
mint('arguments', 'guide-lang-invalid', ['guide', '--lang', 'fr']);
mint('arguments', 'guide-lang-missing', ['guide', '--lang']);
mint('arguments', 'guide-lang-assignment-invalid', ['guide', '--lang=fr']);
mint('arguments', 'guide-unknown-option', ['guide', '--bogus']);

// --- validate: every example, json and human; standard-quality variants; failure receipts ---
// The ten diagram types; the explainer example (a document, not a diagram) has its own group below.
const EXAMPLES = fs.readdirSync(repo(EX)).filter((f) => f.endsWith('.json') && !f.endsWith('.explainer.json')).sort();
expect(EXAMPLES.length === 25, `25 examples, found ${EXAMPLES.length}`);
const typeOf = (f) => f.split('.').at(-2);
const stem = (f) => f.split('.').slice(0, -1).join('.');
for (const f of EXAMPLES) {
  const files = exFiles(f);
  mint('validate', `${stem(f)}-json`, ['validate', typeOf(f), f, '--json'], files);
  mint('validate', `${stem(f)}-human`, ['validate', typeOf(f), f], files);
  mint('validate', `${stem(f)}-standard-json`, ['validate', typeOf(f), f, '--json', '--quality', 'standard'], files);
}
mint('validate', 'agent-tool-call.workflow-standard-assignment-human', ['validate', 'workflow', wf, '--quality=standard'], exFiles(wf));
const failureInputs = [
  ['overflow-label', 'architecture', 'overflow-label.architecture.json'],
  ['overflow-label-locale', 'architecture', 'overflow-label-locale.architecture.json'],
  ['locale-warning', 'architecture', 'locale-warning.architecture.json'],
  ['ghost-endpoint', 'workflow', 'ghost.workflow.json'],
  ['column-capacity', 'workflow', 'column-capacity.workflow.json'],
  ['broken-json', 'workflow', 'broken.workflow.json'],
  ['bom', 'architecture', 'bom.architecture.json'],
  ['schema-failure', 'architecture', 'schema-failure.architecture.json'],
  ['schema-failure-output', 'architecture', 'schema-failure-output.architecture.json'],
  ['bad-meta-output', 'architecture', 'bad-meta-output.architecture.json'],
];
for (const [name, type, file] of failureInputs) {
  mint('validate', `${name}-json`, ['validate', type, file, '--json'], { [file]: inputs[file] });
  mint('validate', `${name}-human`, ['validate', type, file], { [file]: inputs[file] });
}
mint('validate', 'missing-file-json', ['validate', 'workflow', 'missing.json', '--json']);
mint('validate', 'missing-file-human', ['validate', 'workflow', 'missing.json']);

// --- layout: validate --layout-json and inspect architecture ---
for (const f of EXAMPLES.filter((x) => ['architecture', 'workflow'].includes(typeOf(x)))) {
  mint('layout', stem(f), ['validate', typeOf(f), f, '--layout-json'], exFiles(f));
  if (typeOf(f) === 'architecture') mint('layout', `inspect-${stem(f)}`, ['inspect', 'architecture', f], exFiles(f));
}
// One failure of each kind (plan Task 2), in human and --json form.
const layoutFailures = [
  ['architecture-validate-failure', 'architecture', 'overflow-label.architecture.json', (o) => JSON.parse(o.stdout).contract === 'archify-architecture-layout-v1'],
  ['architecture-pre-layout-failure', 'architecture', 'schema-failure-output.architecture.json', (o) => !o.stdout.includes('"contract"')],
  ['architecture-pregate-failure', 'architecture', 'schema-failure.architecture.json', (o) => !o.stdout.includes('"contract"')],
  ['workflow-unmeasured-failure', 'workflow', 'ghost.workflow.json', (o) => !('requiredViewBox' in JSON.parse(o.stdout))],
  ['workflow-measured-failure', 'workflow', 'small-viewbox.workflow.json', (o) => JSON.parse(o.stdout).ok === false && 'requiredViewBox' in JSON.parse(o.stdout)],
];
for (const [name, type, file, shape] of layoutFailures) {
  const json = mint('layout', `${name}-json`, ['validate', type, file, '--layout-json', '--json'], { [file]: inputs[file] }).fixture;
  expect(json.exit === 1 && shape(json), `${name} shape`);
  mint('layout', name, ['validate', type, file, '--layout-json'], { [file]: inputs[file] });
}
mint('layout', 'inspect-overflow-label', ['inspect', 'architecture', 'overflow-label.architecture.json'], { 'overflow-label.architecture.json': inputs['overflow-label.architecture.json'] });

// --- check: the 15 checked-in goldens, plus a missing artifact ---
const GOLDENS = fs.readdirSync(repo(EX)).filter((f) => f.endsWith('.html')).sort();
expect(GOLDENS.length === 15, `15 goldens, found ${GOLDENS.length}`);
for (const g of GOLDENS) {
  const { fixture } = mint('check', g.replace(/\.html$/, ''), ['check', g], exFiles(g));
  expect(fixture.exit === 0, `check ${g} passes`);
}
mint('check', 'web-app-rendered-json', ['check', 'web-app-rendered.html', '--json'], exFiles('web-app-rendered.html'));
mint('check', 'missing-artifact', ['check', 'missing.html']);
mint('check', 'directory-artifact', ['check', '.']);   // the workdir itself: delivery-artifact-not-regular-file
// check-render-output.mjs:31-33 hashes the bytes and scans bytes.toString('utf8'): a BOM, and invalid UTF-8 inside a scanned SVG
// attribute, whose U+FFFD replacements then appear in the finite_svg detail.
{
  const golden = fs.readFileSync(repo(`${EX}/web-app-rendered.html`));
  const svgOpen = golden.indexOf('>', golden.indexOf('<svg')) + 1;
  craft('decode-invalid-utf8.html', Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), golden.subarray(0, svgOpen),
    Buffer.from('<rect x="'), Buffer.from([0xc3, 0x28, 0xe2, 0x82]), Buffer.from('NaN" y="0" width="1" height="1"/>'), golden.subarray(svgOpen),
    Buffer.from([0xc3, 0x28, 0xe2, 0x82])]));
  const { fixture } = mint('check', 'decode-invalid-utf8-json', ['check', 'decode-invalid-utf8.html', '--json'],
    { 'decode-invalid-utf8.html': inputs['decode-invalid-utf8.html'] });
  expect(fixture.exit === 1 && fixture.stdout.includes('rect x=\\"\ufffd(\ufffdNaN\\"'), 'check decodes invalid UTF-8 inside the SVG to U+FFFD');
}

// --- guide: lists, T/guide.test.mjs batteries, language overrides ---
mint('guide', 'list-en', ['guide']);
mint('guide', 'list-zh', ['guide', '--lang', 'zh']);
mint('guide', 'list-en-json', ['guide', '--json']);
mint('guide', 'list-zh-json', ['guide', '--json', '--lang=zh']);
const representative = ['Show an API request with Redis cache miss', 'Show CI/CD build deploy rollback', '展示 Kafka topic 消费者组和死信队列',
  '梳理 ETL 数仓 PII 数据血缘', 'deployment lifecycle approval rollback state', 'agent tool call approval gate MCP',
  'Show a system overview via an architecture diagram', 'Draw deployment topology with named boundary crossings',
  'Explain an API request via a webhook callback'];
const repair = ['viewport overflow', 'why does it still overflow', 'scrollHeight', 'scrollWidth',
  'overlap', 'label overlap', 'edge through node', 'crossing', 'via',
  'how do via waypoints work', 'layout repair', 'which fix order', 'repair order',
  'architecture label overlap', 'workflow layout repair', 'sequence viewport overflow',
  'dataflow edge through node', 'lifecycle layout repair',
  '视口溢出', '为什么还是溢出', '滚动高度', '滚动宽度', '节点重叠', '标签重叠',
  '连线穿过节点', '连线交叉', '途经点', '布局修复', '修复顺序', '架构图标签重叠'];
expect(representative.length === 9 && repair.length === 30, 'guide battery sizes');
const pad = (i) => String(i + 1).padStart(2, '0');
const guideQuery = (name, queryArgs) => {
  mint('guide', name, ['guide', ...queryArgs]);
  mint('guide', `${name}-json`, ['guide', ...queryArgs, '--json']);
};
representative.forEach((q, i) => guideQuery(`representative-${pad(i)}`, [q]));
repair.forEach((q, i) => guideQuery(`repair-${pad(i)}`, [q]));
guideQuery('exact-incident-runbook', ['incident-runbook']);
guideQuery('unknown-query', ['make it delightful']);
for (const lang of ['en', 'zh']) {
  guideQuery(`exact-layout-repair-${lang}`, ['layout-repair', '--lang', lang]);
  guideQuery(`override-viewport-overflow-${lang}`, ['viewport overflow', '--lang', lang]);
  guideQuery(`override-zh-viewport-overflow-${lang}`, ['视口溢出', '--lang', lang]);
}
guideQuery('override-via-waypoints-zh-assignment', ['how', 'do', 'via', 'waypoints', 'work', '--lang=zh']);
guideQuery('repair-combined-zh', ['视口溢出，标签重叠，修复顺序']);
// A lone surrogate in argv: the child's UTF-16 command line reaches process.argv through UTF-8, so the query holds U+FFFD there.
guideQuery('lone-surrogate', ['viewport \ud800 overflow']);

// --- render / demo / examples: stdout lines; their HTML must equal the checked-in goldens ---
const golden = (g) => repo(`${EX}/${g}`);
mint('render', 'render-output-arg', ['render', 'architecture', arch, 'out.html'], exFiles(arch),
  (work) => expect(sameBytes(path.join(work, 'out.html'), golden('web-app-rendered.html')), 'render web-app equals golden'));
mint('render', 'render-meta-output', ['render', 'architecture', arch], exFiles(arch),
  (work) => expect(sameBytes(path.join(work, 'web-app-rendered.html'), golden('web-app-rendered.html')), 'render meta.output equals golden'));
mint('render', 'render-meta-output-subdir', ['render', 'workflow', wf], exFiles(wf),
  (work) => expect(sameBytes(path.join(work, 'examples', 'workflow-agent-tool-call-rendered.html'), golden('workflow-agent-tool-call-rendered.html')), 'render workflow equals golden'));
mint('render', 'render-quality-standard', ['render', 'workflow', wf, 'standard.html', '--quality', 'standard'], exFiles(wf));
mint('render', 'render-locale-warning', ['render', 'architecture', 'locale-warning.architecture.json', 'out.html'],
  { 'locale-warning.architecture.json': inputs['locale-warning.architecture.json'] });
mint('render', 'render-overflow-label', ['render', 'architecture', 'overflow-label.architecture.json', 'out.html'],
  { 'overflow-label.architecture.json': inputs['overflow-label.architecture.json'] });
mint('render', 'render-overflow-label-locale', ['render', 'architecture', 'overflow-label-locale.architecture.json', 'out.html'],
  { 'overflow-label-locale.architecture.json': inputs['overflow-label-locale.architecture.json'] });
mint('render', 'render-missing-input', ['render', 'architecture', 'missing.json', 'out.html']);
mint('render', 'demo-default', ['demo'], {},
  (work) => expect(sameBytes(path.join(work, 'archify-demo.html'), golden('web-app-rendered.html')), 'demo equals golden'));
mint('render', 'demo-directory', ['demo', 'my-demo'], {},
  (work) => expect(sameBytes(path.join(work, 'my-demo', 'archify-demo.html'), golden('web-app-rendered.html')), 'demo dir equals golden'));
mint('render', 'examples', ['examples'], {}, (_, fixture) => {
  const lines = fixture.stdout.trimEnd().split('\n');
  expect(fixture.exit === 0 && lines.length === 15, 'examples prints 15 paths');
  for (const line of lines) {
    const name = path.basename(line);
    expect(sameBytes(path.join(SKILL, 'examples', name), golden(name)), `examples ${name} equals golden`);
  }
});

// --- render: output-path errors and their order against the load gates (cli.mjs:79-99) ---
// The authored meta.output check (with cwd) runs before the schema; resolveOutputPath runs after the load gates and before
// the layout, so a CLI output error loses to the schema and wins over a layout failure, after the locale warning.
const underFile = example('web-app.architecture.json'); underFile.meta.output = 'blocker/diagram.html'; underFile.components[0].colour = 'red';
craft('meta-output-under-file.architecture.json', JSON.stringify(underFile, null, 2));
const renderErrors = [
  ['render-input-alias', [arch, arch], exFiles(arch), 'output/input-alias'],
  ['render-cli-extension', [arch, 'out.htm'], exFiles(arch), 'output/cli-extension'],
  ['render-native-path-syntax', [arch, 'out.html:stream'], exFiles(arch), 'output/native-path-syntax'],
  ['render-schema-before-cli-output', ['schema-failure.architecture.json', 'out.txt'],
    { 'schema-failure.architecture.json': inputs['schema-failure.architecture.json'] }, 'schema/required'],
  ['render-cli-output-before-layout', ['overflow-label-locale.architecture.json', 'out.txt'],
    { 'overflow-label-locale.architecture.json': inputs['overflow-label-locale.architecture.json'] }, 'output/cli-extension'],
  ['render-meta-output-before-schema', ['meta-output-under-file.architecture.json'],
    { 'meta-output-under-file.architecture.json': inputs['meta-output-under-file.architecture.json'], blocker: `${EX}/${arch}` },
    'output/path-resolution-indeterminate'],
];
for (const [name, rest, files, code] of renderErrors) {
  const { fixture } = mint('render', name, ['render', 'architecture', ...rest], files, (work) =>
    expect(fs.readdirSync(work).length === Object.keys(files).length, `${name} writes nothing`));
  expect(fixture.exit === 1 && fixture.stderr.includes(`[${code}]`), `${name} reports ${code}`);
}
// An empty output positional is no output (`...(output ? [output] : [])`), an empty demo directory is the cwd, and a
// directory as input reads as EISDIR.
mint('render', 'render-empty-output', ['render', 'architecture', arch, ''], exFiles(arch),
  (work) => expect(sameBytes(path.join(work, 'web-app-rendered.html'), golden('web-app-rendered.html')), 'empty output uses meta.output'));
mint('render', 'demo-empty-directory', ['demo', ''], {},
  (work) => expect(sameBytes(path.join(work, 'archify-demo.html'), golden('web-app-rendered.html')), 'empty demo dir is cwd'));
mint('render', 'render-input-directory', ['render', 'architecture', 'specs', 'out.html'], { [`specs/${arch}`]: `${EX}/${arch}` });
mint('render', 'render-nested-output',['render', 'architecture', arch, 'nested/deeper/out.html'], exFiles(arch),
  (work) => expect(sameBytes(path.join(work, 'nested', 'deeper', 'out.html'), golden('web-app-rendered.html')), 'nested render equals golden'));

// --- deliver (Q5, Q8): receipts without `update`, every v4 uuid as <uuid>; the workdir's entries and delivery sidecars ---
// A fixture gains `entries` (every workdir path, sorted, `\`-separated) and `sidecars` (workdir path -> normalised content of each
// .delivery.json, .delivery-pending.json and directory lock left behind).
const UUID_V4 = /[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}/giu;
const dropUpdate = (stdout) => {
  if (!stdout.startsWith('{')) return stdout;
  const receipt = JSON.parse(stdout);
  delete receipt.update;
  return `${JSON.stringify(receipt, null, 2)}\n`;
};
const walk = (dir, base = dir) => fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
  const full = path.join(dir, e.name);
  return [path.relative(base, full), ...(e.isDirectory() ? walk(full, base) : [])];
});
const SIDECAR = /(\.delivery\.json|\.delivery-pending\.json|^\.archify-delivery-lock\.json)$/u;
function deliverFixture(name, args, files = {}, inspect = () => {}) {
  return mint('deliver', name, args, files, (work, fixture) => {
    fixture.stdout = dropUpdate(fixture.stdout).replace(UUID_V4, '<uuid>');
    fixture.stderr = fixture.stderr.replace(UUID_V4, '<uuid>');
    fixture.entries = walk(work).sort();
    fixture.sidecars = Object.fromEntries(fixture.entries.filter((e) => SIDECAR.test(path.basename(e)))
      .map((e) => [e, tokenise(fs.readFileSync(path.join(work, e), 'utf8'), work).replace(UUID_V4, '<uuid>')]));
    inspect(work, fixture);
  }).fixture;
}
craft('stale.archify-delivery-lock.json', '{"schemaVersion":1,"receiptId":"11111111-2222-4333-8444-555555555555","pid":999999}\n');
craft('invalid.archify-delivery-lock.json', 'not a delivery lock\n');
craft('pending.delivery-pending.json', `${JSON.stringify({ schemaVersion: 1, command: 'deliver', status: 'pending', receiptId: '11111111-2222-4333-8444-555555555555', input: 'seeded', output: 'seeded' }, null, 2)}\n`);
craft('current.delivery.json', `${JSON.stringify({ schemaVersion: 1, receiptId: '11111111-2222-4333-8444-555555555555', status: 'current', command: 'deliver', type: 'sequence' }, null, 2)}\n`);
for (const f of EXAMPLES) {
  const fixture = deliverFixture(`${stem(f)}-json`, ['deliver', typeOf(f), f, '--json'], exFiles(f));
  const receipt = JSON.parse(fixture.stdout);
  expect(fixture.exit === 0 && receipt.ok && !('update' in receipt), `deliver ${f} passes`);
  expect(fixture.entries.some((e) => e.endsWith('.delivery.json')) && !fixture.entries.some((e) => /pending|lock/u.test(e)), `deliver ${f} leaves only provenance`);
}
deliverFixture('web-app-human', ['deliver', 'architecture', arch, 'out.html'], exFiles(arch));
// Locale warnings print as `archify: …` and stay in the receipt's `diagnostics`.
expect(deliverFixture('locale-warning-json', ['deliver', 'architecture', 'locale-warning.architecture.json', 'out.html', '--json'],
  { 'locale-warning.architecture.json': inputs['locale-warning.architecture.json'] }).stderr.startsWith('archify: '), 'deliver locale warning');
// A redelivery replaces an existing artifact and its stale provenance in place.
deliverFixture('replace-existing-json', ['deliver', 'architecture', arch, 'out.html', '--json'],
  { ...exFiles(arch), 'out.html': `${EX}/sequence-cache-miss-request.html`, 'out.delivery.json': inputs['current.delivery.json'] },
  (work) => expect(sameBytes(path.join(work, 'out.html'), golden('web-app-rendered.html')), 'redelivery equals golden'));
// Failures after the output is known record the attempt: the journal stays, a failed marker only beside an existing artifact.
const broken = { 'broken.workflow.json': inputs['broken.workflow.json'] };
for (const suffix of ['json', 'human']) {
  const f = deliverFixture(`json-parse-${suffix}`, ['deliver', 'workflow', 'broken.workflow.json', ...(suffix === 'json' ? ['--json'] : [])], broken);
  expect(f.exit === 1 && 'workflow.delivery-pending.json' in f.sidecars && !f.entries.includes('workflow.delivery.json'), `json-parse-${suffix} journal only`);
}
expect('workflow.delivery.json' in deliverFixture('json-parse-existing-artifact-json', ['deliver', 'workflow', 'broken.workflow.json', '--json'],
  { ...broken, 'workflow.html': `${EX}/web-app-rendered.html` }, (work) => expect(sameBytes(path.join(work, 'workflow.html'), golden('web-app-rendered.html')), 'artifact kept')).sidecars,
  'failed marker beside the kept artifact');
const schemaOut = { 'schema-failure-output.architecture.json': inputs['schema-failure-output.architecture.json'], 'out.html': `${EX}/web-app-rendered.html` };
for (const suffix of ['json', 'human']) {
  const f = deliverFixture(`schema-render-failure-${suffix}`, ['deliver', 'architecture', 'schema-failure-output.architecture.json', 'out.html',
    ...(suffix === 'json' ? ['--json'] : [])], schemaOut, (work) => expect(sameBytes(path.join(work, 'out.html'), golden('web-app-rendered.html')), 'previous artifact kept'));
  expect(f.exit === 1 && 'out.delivery.json' in f.sidecars && f.stdout + f.stderr !== '' && (f.stdout + f.stderr).includes('schema/'), `schema-render-failure-${suffix}`);
}
// input/read keeps Node's ENOENT text; with an output directory that does not exist, recording the attempt cannot take the lock
// (A:1633-1643): delivery/lock-acquire follows the input diagnostic and no provenance status is claimed.
deliverFixture('missing-input-json', ['deliver', 'workflow', 'missing.json', '--json']);
deliverFixture('missing-input-missing-directory-json', ['deliver', 'workflow', 'missing.json', 'nested/out.html', '--json']);
deliverFixture('cli-extension-json', ['deliver', 'architecture', arch, 'out.htm', '--json'], exFiles(arch));
deliverFixture('input-alias-json', ['deliver', 'architecture', arch, arch, '--json'], exFiles(arch));
// deliver validates meta.output even when it is absent (A:4461): a diagram without one fails before any output is resolved.
deliverFixture('no-meta-output-json', ['deliver', 'architecture', 'schema-failure.architecture.json', 'out.html', '--json'],
  { 'schema-failure.architecture.json': inputs['schema-failure.architecture.json'] });
// Lock owners: a dead pid is stale and is never taken over; every seeded file stays byte-identical (T/cli.test.mjs:1569-1654).
const lockedFiles = (lock) => ({ ...exFiles(arch), '.archify-delivery-lock.json': inputs[lock], 'out.html': `${EX}/sequence-cache-miss-request.html`,
  'out.delivery.json': inputs['current.delivery.json'], 'out.delivery-pending.json': inputs['pending.delivery-pending.json'] });
const untouched = (lock) => (work) => {
  for (const [dest, src] of Object.entries(lockedFiles(lock))) expect(sameBytes(path.join(work, dest), repo(src)), `${dest} untouched`);
};
for (const suffix of ['json', 'human']) {
  const f = deliverFixture(`lock-stale-${suffix}`, ['deliver', 'architecture', arch, 'out.html', ...(suffix === 'json' ? ['--json'] : [])],
    lockedFiles('stale.archify-delivery-lock.json'), untouched('stale.archify-delivery-lock.json'));
  expect(f.exit === 1 && (f.stdout + f.stderr).includes('delivery/lock-stale'), `lock-stale-${suffix}`);
}
// The input itself sits at the directory lock's path (A:574-581).
expect(deliverFixture('lock-path-conflict-json', ['deliver', 'architecture', '.archify-delivery-lock.json', 'out.html', '--json'],
  { '.archify-delivery-lock.json': `${EX}/${arch}` }, (work) => expect(sameBytes(path.join(work, '.archify-delivery-lock.json'), golden(arch)), 'input untouched'))
  .stdout.includes('delivery/lock-path-conflict'), 'lock-path-conflict');
expect(deliverFixture('lock-invalid-json',['deliver', 'architecture', arch, 'out.html', '--json'], lockedFiles('invalid.archify-delivery-lock.json'),
  untouched('invalid.archify-delivery-lock.json')).stdout.includes('delivery/lock-invalid'), 'lock-invalid');
// A receipt id with a trailing "\n": JS `$` does not match before it, so the lock is unrecognised, not stale (A:197).
craft('uuid-newline.archify-delivery-lock.json', '{"schemaVersion":1,"receiptId":"11111111-2222-4333-8444-555555555555\\n","pid":999999}\n');
expect(deliverFixture('lock-uuid-newline-json', ['deliver', 'architecture', arch, 'out.html', '--json'], lockedFiles('uuid-newline.archify-delivery-lock.json'),
  untouched('uuid-newline.archify-delivery-lock.json')).stdout.includes('delivery/lock-invalid'), 'lock-uuid-newline');
// An explicit empty output argument reaches resolveOutputPath (A:4376): output/native-path-syntax, reason empty, nothing written.
for (const suffix of ['json', 'human']) {
  const f = deliverFixture(`empty-output-${suffix}`, ['deliver', 'architecture', arch, '', ...(suffix === 'json' ? ['--json'] : [])], exFiles(arch));
  expect(f.exit === 1 && (f.stdout + f.stderr).includes('output/native-path-syntax') && f.entries.length === 1, `empty-output-${suffix}`);
}

// --- provenance: check's delivery gate (A:1903-2178, :5309-5468), every state from a real deliver plus documented edits ---
// The deliveryReceiptId is a v4 uuid -> <uuid>.
const delivered = { run: ['deliver', 'architecture', arch, 'out.html', '--json'], exit: 0 };
// A render failure over the delivered artifact keeps it, leaves the journal and writes a failed marker (stage render).
const failedRedelivery = { run: ['deliver', 'architecture', 'schema-failure-output.architecture.json', 'out.html', '--json'], exit: 1 };
const withFailure = { ...exFiles(arch), 'schema-failure-output.architecture.json': inputs['schema-failure-output.architecture.json'] };
const withLock = { ...exFiles(arch), 'lock.json': inputs['stale.archify-delivery-lock.json'] };
const renderedOnly = { 'out.html': `${EX}/web-app-rendered.html` };
const strict = ['check', 'out.html', '--require-provenance'];
const plain = ['check', 'out.html'];
const provenanceCases = [
  // [name, files, setup, args, exit, provenance status, first diagnostic code]
  ['current-required', exFiles(arch), [delivered], strict, 0, 'current'],
  ['current', exFiles(arch), [delivered], plain, 0, 'current'],
  ['unknown', renderedOnly, [], plain, 0, 'unknown'],
  ['required', renderedOnly, [], strict, 1, 'unknown', 'delivery/provenance-required'],
  ['failed-pending', withFailure, [delivered, failedRedelivery], strict, 1, 'failed', 'delivery/provenance-failed'],
  ['failed-pending-plain', withFailure, [delivered, failedRedelivery], plain, 1, 'failed', 'delivery/provenance-failed'],
  ['failed-marker', withFailure, [delivered, failedRedelivery, { delete: 'out.delivery-pending.json' }], strict, 1, 'failed', 'delivery/provenance-failed'],
  ['locked', withLock, [delivered, { copy: 'lock.json', to: '.archify-delivery-lock.json' }], strict, 1, 'locked', 'delivery/provenance-locked'],
  ['locked-legacy', withLock, [delivered, { copy: 'lock.json', to: 'out.delivery-lock.json' }], plain, 1, 'locked', 'delivery/provenance-locked'],
  ['mismatch', exFiles(arch), [delivered, { append: 'out.html', text: '\n' }], strict, 1, 'mismatch', 'delivery/provenance-mismatch'],
  ['mismatch-plain', exFiles(arch), [delivered, { append: 'out.html', text: '\n' }], plain, 1, 'mismatch', 'delivery/provenance-mismatch'],
  ['invalid-json', exFiles(arch), [delivered, { write: 'out.delivery.json', text: 'not json\n' }], strict, 1, 'invalid', 'delivery/provenance-invalid'],
  ['invalid-null', exFiles(arch), [delivered, { write: 'out.delivery.json', text: 'null\n' }], strict, 1, 'invalid', 'delivery/provenance-invalid'],
  // JSON.parse keeps the last of duplicate keys: a valid last value is current, an invalid one fails the schema.
  ['current-duplicate-key', exFiles(arch), [delivered, { replace: 'out.delivery.json', from: '"schemaVersion": 1,', to: '"schemaVersion": 999,\n  "schemaVersion": 1,' }],
    strict, 0, 'current'],
  ['invalid-duplicate-key', exFiles(arch), [delivered, { replace: 'out.delivery.json', from: '"status": "current",', to: '"status": "current",\n  "status": "stale",' }],
    strict, 1, 'invalid', 'delivery/provenance-invalid'],
  ['invalid-schema', exFiles(arch), [delivered, { set: 'out.delivery.json', key: 'schemaVersion', value: 999 }], strict, 1, 'invalid', 'delivery/provenance-invalid'],
  ['invalid-other-output', exFiles(arch), [{ run: ['deliver', 'architecture', arch, 'a.html', '--json'], exit: 0 },
    { copy: 'a.html', to: 'out.html' }, { copy: 'a.delivery.json', to: 'out.delivery.json' }], strict, 1, 'invalid', 'delivery/provenance-invalid'],
  ['output-indeterminate', exFiles(arch), [delivered, { set: 'out.delivery.json', key: 'output', value: '<WORK>\\missing.html' }], strict, 1, 'invalid',
    'delivery/provenance-output-indeterminate'],
  ['invalid-directory-sidecar', exFiles(arch), [delivered, { delete: 'out.delivery.json' }, { mkdir: 'out.delivery.json' }], strict, 1, 'invalid',
    'delivery/provenance-invalid'],
  ['invalid-dangling-sidecar', renderedOnly, [{ symlink: 'out.delivery.json', target: 'missing.json' }], plain, 1, 'invalid', 'delivery/provenance-invalid'],
  // A dangling artifact link is no regular file (lstat after the realpath fails, A:5320-5354).
  ['artifact-dangling-symlink', {}, [{ symlink: 'out.html', target: 'missing.html' }], plain, 1, 'unknown', 'input/artifact-unreadable'],
];
for (const [name, files, setup, args, exit, status, code] of provenanceCases) {
  const { fixture } = mint('provenance', name, args, files, (_, f) => { f.stdout = f.stdout.replace(UUID_V4, '<uuid>'); }, setup);
  const receipt = JSON.parse(fixture.stdout);
  expect(fixture.exit === exit && receipt.provenance === status && fixture.stderr === '', `provenance ${name}: exit ${fixture.exit}, ${receipt.provenance}`);
  expect(code ? receipt.diagnostics[0].code === code : receipt.ok === true, `provenance ${name}: ${code ?? 'ok'}`);
}

await explainerGroup();
if (ONLY) {
  console.log(`minted the explainer group (${[...minted].filter((rel) => rel.includes('/explainer/')).length} fixtures); every other group read back`);
  process.exit(0);
}

// --- paths: output-path.mjs and sidecar-path.mjs, called directly (pure, or on a real temp directory of this volume) ---
const outputPath = await import(pathToFileURL(path.join(SKILL, 'renderers/shared/output-path.mjs')).href);
const sidecarPath = await import(pathToFileURL(path.join(SKILL, 'renderers/shared/sidecar-path.mjs')).href);
const pathSemantics = await import(pathToFileURL(path.join(SKILL, 'renderers/shared/path-semantics.mjs')).href);
const { raw } = String;

// T/native-output-path.test.mjs, row by row: [fn, value, options, upstream's assertion]. `ok` = returns the value (or, for a
// directory, `expect` is the resolved path); a string = the reason it must fail with; 'fail' = output/native-path-syntax.
const W = { platform: 'win32' };
const nativeRows = [];
const rows = (fn, options, values, check) => values.forEach((v) => nativeRows.push([fn, v, options, check]));
rows('validate', W, [raw`C:\reports\diagram.html`, raw`\\server\share\reports\diagram.html`, raw`\\server\CON\reports\diagram.html`,
  '//server/share/reports/diagram.html', raw`\\?\C:\reports\diagram.html`, raw`\\?\UNC\server\share\reports\diagram.html`,
  raw`\\?\UNC\server\CON\reports\diagram.html`, raw`reports\diagram.html`, raw`..\diagram.html`, raw`.\reports\..\diagram.html`,
  raw`C:\reports\..\diagram.html`, raw`\\server\share\reports\..\diagram.html`, raw`C:\ARCHIF~1\diagram.html`,
  `C:\\reports\\${'界'.repeat(100)}.html`, `\\\\?\\C:\\reports\\${'界'.repeat(100)}.html`], 'ok');
rows('validate', W, [raw`C:`, raw`C:diagram.html`, raw`C:\reports\diagram.html:payload`, raw`\\.\NUL`, '//./NUL/diagram.html',
  raw`\\?\GLOBALROOT\Device\HarddiskVolume1\diagram.html`, '//?/GLOBALROOT/Device/HarddiskVolume1/diagram.html',
  raw`\\?/GLOBALROOT\Device\HarddiskVolume1\diagram.html`, raw`\\?\\C:diagram.html`, raw`\\?\foo.html`, raw`\\ser?ver\share\file.html`,
  raw`\reports\diagram.html`, raw`C:\reports\CON.html`, raw`C:\reports\diagram .html `, raw`C:\reports\bad?.html`,
  raw`\\localhost\pipe\diagram.html`, raw`\\server\mailslot\diagram.html`, raw`\\server\IPC$\diagram.html`,
  raw`\\?\UNC\localhost\pipe\diagram.html`, raw`\\?\UNC\server\MAILSLOT\diagram.html`, raw`\\?\UNC\server\ipc$\diagram.html`], 'fail');
rows('validate', W, [raw`\\server\CON\diagram.html`, raw`\\CON\share\diagram.html`, raw`\\?\UNC\server\CON\diagram.html`,
  raw`\\?\UNC\CON\share\diagram.html`], 'ok');
rows('validate', W, [raw`\\server\bad+share\diagram.html`, raw`\\server\trailing.\diagram.html`, `\\\\server\\${'x'.repeat(81)}\\diagram.html`,
  raw`\\?\UNC\server\bad,share\diagram.html`, raw`\\?\UNC\server\trailing \diagram.html`], 'windows-unc-share-name');
rows('resolve', { platform: 'win32', defaultOutput: 'diagram.html' }, [raw`\\server\IPC$\diagram.html`, raw`\\server\pipe\diagram.html`,
  raw`\\server\mailslot\diagram.html`, raw`\\?\UNC\server\IPC$\diagram.html`, raw`\\?\UNC\server\pipe\diagram.html`,
  raw`\\?\UNC\server\mailslot\diagram.html`], 'windows-ipc-namespace');
const WD = { platform: 'win32', cwd: raw`C:\workspace\project` };
for (const [v, reason] of [[raw`C:evidence`, 'drive-relative'], [raw`\evidence`, 'current-drive-rooted'], [raw`\\.\NUL`, 'windows-device-namespace'],
  [raw`C:\reports\evidence:stream`, 'windows-ads'], [raw`C:\reports\CON`, 'windows-reserved-name'],
  [raw`C:\reports\evidence.`, 'windows-trailing-dot-space'], [raw`C:\reports\evidence `, 'windows-trailing-dot-space']]) rows('directory', WD, [v], reason);
rows('directory', WD, ['evidence'], raw`C:\workspace\project\evidence`);
rows('directory', WD, [raw`D:\evidence`], raw`D:\evidence`);
rows('validate', W, ['C:\\reports\\diagram.html\\', '\\\\server\\share\\diagram.html\\', '\\\\?\\C:\\reports\\diagram.html\\',
  '\\\\?\\UNC\\server\\share\\diagram.html\\'], 'trailing-separator');
rows('validate', { platform: 'linux' }, ['/tmp/archive.zip/'], 'fail');
for (const [v, resolved] of [['C:\\reports\\', raw`C:\reports`], ['\\\\server\\share\\reports\\', raw`\\server\share\reports`],
  ['\\\\?\\C:\\reports\\', raw`\\?\C:\reports`], ['\\\\?\\UNC\\server\\share\\reports\\', raw`\\?\UNC\server\share\reports`]]) {
  rows('validate', { platform: 'win32', kind: 'directory' }, [v], 'ok');
  rows('directory', WD, [v], resolved);
}
rows('validate', { platform: 'linux', kind: 'directory' }, ['/tmp/evidence/'], 'ok');
rows('validate', W, [raw`\\server\\share\report.html`, '//server//share/report.html', raw`\\server\/share\report.html`,
  raw`\\server/share\report.html`, raw`//server\share/report.html`], 'windows-unc-root');
rows('validate', W, [raw`\\?\C:\reports\..\diagram.html`, raw`\\?\C:\reports\.\diagram.html`, raw`\\?\C:\reports\\diagram.html`,
  raw`\\?\UNC\server\share\..\other\diagram.html`, raw`\\?\UNC\server\share\.\diagram.html`, raw`\\?\UNC\server\share\reports\\diagram.html`,
  raw`\\?\C:/reports/diagram.html`, raw`\\?\UNC/server/share/diagram.html`, '//?/C:/reports/diagram.html', raw`\\?\C:diagram.html`,
  raw`\\?\UNC\server`, raw`\\?\UNC\\share\diagram.html`, raw`\\?\Device\HarddiskVolume1\diagram.html`,
  raw`\\?\Volume{01234567-89AB-CDEF-0123-456789ABCDEF}\reports\diagram.html`], 'fail');
// The unpaired-surrogate rows (\ud800, \udfff) cannot cross JSON intact; the C# test asserts them directly.
for (const platform of ['darwin', 'linux', 'win32']) rows('validate', { platform }, ['reports/\ud83d\ude00.html'], 'ok');
rows('validate', { platform: 'linux' }, [`/tmp/${'é'.repeat(128)}.html`], 'fail');
rows('validate', W, [`C:\\reports\\${'x'.repeat(256)}.html`, `C:\\reports\\${'x'.repeat(256)}~1.html`], 'fail');
rows('resolve', { platform: 'win32', defaultOutput: 'diagram.html' }, [raw`C:\reports\NUL.html`], 'windows-reserved-name');
rows('validate', { platform: 'linux' }, ['reports/CON.html', 'reports/name:stream.html', 'reports/name .html'], 'ok');
rows('validate', { platform: 'linux' }, ['reports/diagram\0.html'], 'fail');
const nativeTable = nativeRows.map(([fn, value, options, check]) => {
  let outcome;
  try {
    const result = fn === 'validate' ? outputPath.validateNativeOutputPath(value, options)
      : fn === 'directory' ? outputPath.resolveNativeOutputDirectory(value, options)
        : outputPath.resolveOutputPath({ requestedOutput: value, ...options }).outputPath;
    outcome = { ok: true, result };
  } catch (error) {
    if (!error.archifyDiagnostics) throw error;
    outcome = { ok: false, message: error.message, diagnostic: error.archifyDiagnostics[0] };
  }
  const reason = outcome.diagnostic?.evidence?.reason;
  const as = `${fn} ${JSON.stringify(value)} ${check}`;
  if (check === 'ok') expect(outcome.ok && outcome.result === value, as);
  else if (check === 'fail') expect(!outcome.ok && outcome.diagnostic.code === 'output/native-path-syntax', as);
  else if (fn === 'directory' && /[\\/]/.test(check)) expect(outcome.ok && outcome.result === check, as);
  else expect(!outcome.ok && reason === check, as);
  return { fn, value, options, ...outcome };
});
writeJson(`${CLI_FIX}/paths/native-output-path.json`, nativeTable);

// sidecar-path.mjs, all four functions. Delivery (archify.mjs:13-17) and compare (:18) suffix sets.
const DELIVERY = ['.delivery.json', '.delivery-pending.json', '.delivery-lock.json'];
const COMPARE = ['.receipt.json'];
const stemRows = [
  ['web-app', DELIVERY], ['web-app', COMPARE],
  ['x'.repeat(233), DELIVERY], ['x'.repeat(234), DELIVERY], ['L'.repeat(240), DELIVERY], ['L'.repeat(240), COMPARE],
  ['\ud83d\ude00'.repeat(120), DELIVERY], ['\ud835\udc9c'.repeat(60) + 'tail', DELIVERY],
  ['界'.repeat(77), DELIVERY], ['界'.repeat(78), DELIVERY], ['界'.repeat(100), COMPARE],
  ['a\u0301'.repeat(100), DELIVERY], ['👨‍👩‍👧'.repeat(30), DELIVERY],
  [`legacy.~archify-${'a'.repeat(64)}`, DELIVERY], [`X.~ARCHIFY-${'A'.repeat(64)}`, DELIVERY], [`x.~archify-${'a'.repeat(63)}`, DELIVERY],
  [`x.~archify-${'a'.repeat(64)}.bak`, DELIVERY],
];
const components = ['web-app.html', 'WebApp.HTML', 'report.htm', 'report.Html', 'archive.html.txt', '.html', 'x.html.html',
  `${'x'.repeat(250)}.html`, `${'界'.repeat(90)}.HTML`];
const stemTable = {
  stems: stemRows.flatMap(([stem, suffixes]) => [undefined, { force: true }, { force: true, hashDomain: 'full-component' }].map((options) => ({
    stem, suffixes, options: options ?? null,
    needsBounding: sidecarPath.sidecarStemNeedsBounding(stem, suffixes),
    isBounded: sidecarPath.isBoundedSidecarStem(stem),
    bounded: sidecarPath.boundedSidecarStem(stem, suffixes, options),
  }))),
  components: components.map((component) => {
    const { stem, options } = sidecarPath.sidecarStemFromComponent(component);
    return { component, stem, options: options ?? null, bounded: sidecarPath.boundedSidecarStem(stem, DELIVERY, options) };
  }),
};
expect(stemTable.stems.every((r) => !sidecarPath.sidecarStemNeedsBounding(r.bounded, r.suffixes)), 'bounded stems fit');
expect(stemTable.stems.some((r) => r.isBounded && r.bounded !== r.stem && !r.options), 'a crafted stem is re-hashed unforced');
writeJson(`${CLI_FIX}/paths/sidecar-stems.json`, stemTable);

// The delivery provenance name (archify.mjs:35-66) of a future artifact in a real directory on this NTFS volume: the namespace
// key folds what the directory folds, then the stem is bounded.
const ntfsDir = fs.realpathSync.native(fs.mkdtempSync(path.join(tmpRoot, 'ntfs-')));
const ntfsNames = ['WebApp.HTML', 'Ünïcode Straße.html', 'Ünïcode Straße.html'.normalize('NFD'), `${'L'.repeat(240)}.html`, 'web-app.html'];
const ntfsTable = ntfsNames.map((name) => {
  const key = pathSemantics.sidecarNamespaceComponentKey(ntfsDir, name);
  expect(key.status === 'resolved' && key.directoryPath === ntfsDir, `namespace of ${name}`);
  const { stem, options } = sidecarPath.sidecarStemFromComponent(key.componentKey);
  return { name, componentKey: key.componentKey, provenance: `${sidecarPath.boundedSidecarStem(stem, DELIVERY, options)}.delivery.json` };
});
expect(fs.readdirSync(ntfsDir).length === 0, 'namespace probes leave no residue');
writeJson(`${CLI_FIX}/paths/ntfs-sidecar-names.json`, ntfsTable);

// --- browser engine (Task 10): runBrowserCheck / runVisualCheck / persist*Failure driven by T/visual-check.test.mjs's fakeBrowser ---
// The fake is declarative here so the C# test reads the same configuration from the fixture: viewport keys are `WxH:theme`.
// A fixture is {scenario, exit, launched, calls, receipt (the returned receipt, indent 2), sidecar (the receipt file or null),
// contactSheet (or null), summary (one line), entries (the workdir after the run)}, the workdir tokenised as <WORK>.
const visualCheck = await import(pathToFileURL(path.join(SKILL, 'bin/visual-check.mjs')).href);
const PNG = Buffer.from('89504e470d0a1a0a', 'hex');
const ARTIFACT_TEXT = '<!doctype html><html><body>checked artifact</body></html>';
function engineBrowser(config = {}, calls = []) {
  const at = (list, k) => (config[list] || []).includes(k);
  return {
    calls,
    async inspect({ width, height, theme, screenshotPath, writeScreenshot }) {
      const k = `${width}x${height}:${theme}`;
      calls.push({ width, height, theme, screenshot: Boolean(screenshotPath) });
      if (screenshotPath && at('screenshotFailure', k)) throw new Error('synthetic screenshot failure');
      if (screenshotPath) writeScreenshot(PNG);
      const overflow = at('overflow', k);
      const tall = at('tall', k);
      const readableScroll = at('readableScroll', k);
      const authoredScroll = at('authoredScroll', k);
      const unreadable = at('unreadable', k);
      const chromeCollision = at('chromeCollision', k);
      const stageCollision = at('stageCollision', k);
      const dockStageGap = config.stageGap?.[k] ?? (stageCollision ? -12 : 10);
      const stageClearanceFailure = stageCollision || dockStageGap < 10;
      const automaticReaderFit = 'automaticReaderFit' in config ? config.automaticReaderFit : 'intrinsic-height';
      const authoredDiagramType = 'authoredDiagramType' in config ? config.authoredDiagramType : 'architecture';
      const authoredUnclipped = config.authoredUnclipped ?? true;
      const metrics = {
        innerWidth: width,
        innerHeight: height,
        scrollWidth: width + (overflow ? 1 : 0),
        scrollHeight: height + (readableScroll || authoredScroll ? 240 : 0) + (tall ? 300 : 0),
        resolvedTheme: config.resolvedTheme?.[k] ?? theme,
        ...(tall ? {
          pageComposition: { bodyPaddingPx: 12, headerPx: 100, diagramChromePx: 76, svgPx: 800, cardsPx: 212, viewBoxHeight: 1000 },
        } : {}),
        readerLayout: readableScroll ? 'adaptive' : null,
        readerOverflow: readableScroll ? 'authored' : null,
        readerFit: readableScroll ? automaticReaderFit : authoredScroll ? 'authored-height' : null,
        diagramType: authoredScroll ? authoredDiagramType : null,
        documentScrollUnclipped: authoredScroll && authoredUnclipped,
        readerWidth: 960,
        diagramWidth: 930,
        viewBoxWidth: 1300,
        minimumProjectedNodeTextPx: unreadable ? 5.72 : 6.44,
        minimumProjectedNodeText: unreadable ? 'Compact node' : 'Readable node',
        minimumProjectedNodeTextDetail: unreadable ? 'primary' : 'context',
        hasLegend: true,
        hasNavigationDock: true,
        legendDockIntersectionArea: chromeCollision ? 42 : 0,
        dockStageIntersectionArea: stageCollision ? 84 : 0,
        dockStageGap,
        viewerChromeRequiredGap: 10,
        viewerChromeReserve: chromeCollision || stageClearanceFailure ? 0 : 44,
        viewerChromeActive: !chromeCollision && !stageClearanceFailure,
      };
      // T/visual-check.test.mjs:3033-3054 wraps inspect the same way.
      if (config.scrollHeightOver != null) metrics.scrollHeight = height + config.scrollHeightOver;
      if (config.scrollWidthOver != null) metrics.scrollWidth = width + config.scrollWidthOver;
      if (config.lanes) metrics.workflowLanes = config.lanes;
      return metrics;
    },
    async close() {
      if (config.closeFails) {
        throw Object.assign(new Error('Temporary directory cleanup failed: locked'), { code: 'ARCHIFY_TEMP_CLEANUP', directory: '/retained/profile' });
      }
    },
  };
}
// A full diagnostic, as verifyDeliveryUnchanged would raise one (the C# carrier needs every field).
const engineDiagnostic = (code) => ({
  code, severity: 'error', message: `synthetic ${code}`, subject: { artifact: 'synthetic' }, evidence: { synthetic: true }, supportedFixes: ['synthetic fix'],
});
const passing = { browser: 'fake' };
const at1440 = ['1440x900:light'];
const lanes = [{ frameId: 'lane-0', heightPx: 720, nodeCount: 12, nodeIds: ['wait', 'cancel'], nodeSpanPx: 480, spaceAboveNodesPx: 180, spaceBelowNodesPx: 60 }];
const persistFailure = { schemaVersion: 1, command: 'visual-check', artifact: { path: '<WORK>\\diagram.html' }, error: 'delivery failed', diagnostics: [engineDiagnostic('delivery/provenance-failed')] };
const engineScenarios = {
  // T:3112, T:3139, T:3160-3182, T:2992 (both commands)
  'browser-check-pass': { command: 'browser-check' },
  'browser-check-provenance-pass': { command: 'browser-check', provenance: { status: 'current', receiptId: 'delivery-receipt-123' } },
  'browser-check-theme-mismatch': { command: 'browser-check', fake: { resolvedTheme: { '1440x900:dark': 'light', '2048x1320:dark': 'light' } } },
  'browser-check-intermediate-theme-1600': { command: 'browser-check', fake: { resolvedTheme: { '1600x1000:light': 'dark' } } },
  'browser-check-skipped': { command: 'browser-check', browser: 'unavailable' },
  'browser-check-rerun': { command: 'browser-check', before: [{ pass: true }] },
  'browser-check-launch-error': { command: 'browser-check', browser: 'launch-error' },
  'browser-check-startup-timeout': { command: 'browser-check', browser: 'startup-timeout' },
  'browser-check-unowned-receipt': { command: 'browser-check', before: [{ write: 'diagram.browser-check.json', text: 'external receipt claimant\n' }] },
  'browser-check-persist': { command: 'browser-check', persist: { ...persistFailure, command: 'browser-check', ok: false, evidenceKind: 'automated-browser', status: 'fail', visualReview: 'not-requested', provenance: 'mismatch' } },
  // T:323, T:768 (--out-dir end to end)
  'visual-check-pass': { command: 'visual-check', provenance: { status: 'current', receiptId: 'delivery-receipt-123' } },
  'visual-check-out-dir': { command: 'visual-check', outDir: 'outdir-e2e-evidence' },
  'visual-check-intermediate-theme-1920': { command: 'visual-check', fake: { resolvedTheme: { '1920x1080:light': 'dark' } } },
  // T:2615-2962: overflow, vertical budget, Reader scroll, readability, Dock
  'visual-check-overflow': { command: 'visual-check', fake: { overflow: ['1600x1000:light'] } },
  'visual-check-tall': { command: 'visual-check', fake: { tall: ['1440x900:light', '1440x900:dark'] } },
  'visual-check-readable-intrinsic-height': { command: 'visual-check', fake: { readableScroll: at1440 } },
  'visual-check-readable-width-first': { command: 'visual-check', fake: { readableScroll: at1440, automaticReaderFit: 'width-first' } },
  'visual-check-readable-null': { command: 'visual-check', fake: { readableScroll: at1440, automaticReaderFit: null } },
  'visual-check-readable-unknown': { command: 'visual-check', fake: { readableScroll: at1440, automaticReaderFit: 'unknown' } },
  'visual-check-readable-horizontal': { command: 'visual-check', fake: { readableScroll: at1440, overflow: at1440 } },
  'visual-check-readable-unreadable': { command: 'visual-check', fake: { readableScroll: at1440, unreadable: at1440 } },
  'visual-check-unreadable': { command: 'visual-check', fake: { unreadable: at1440 } },
  'visual-check-chrome-collision': { command: 'visual-check', fake: { chromeCollision: ['1920x1080:light'] } },
  'visual-check-stage-collision': { command: 'visual-check', fake: { stageCollision: ['1920x1080:light'] } },
  'visual-check-stage-low-gap': { command: 'visual-check', fake: { stageGap: { '1920x1080:light': 5 } } },
  // T:2963 (screenshot failure discards the previous captures), T:2992, T:3010
  'visual-check-screenshot-failure': { command: 'visual-check', before: [{ pass: true }], fake: { screenshotFailure: ['1440x900:dark', '2048x1320:dark'] } },
  'visual-check-skipped': { command: 'visual-check', browser: 'unavailable' },
  'visual-check-skipped-rerun': { command: 'visual-check', before: [{ pass: true }], browser: 'unavailable' },
  // T:3033
  'visual-check-lanes-vertical': { command: 'visual-check', fake: { scrollHeightOver: 599, lanes } },
  'visual-check-lanes-horizontal': { command: 'visual-check', fake: { scrollWidthOver: 1, lanes } },
  // T:3185
  'visual-check-authored-readable': { command: 'visual-check', fake: { authoredScroll: at1440 } },
  'visual-check-authored-workflow': { command: 'visual-check', fake: { authoredScroll: at1440, authoredDiagramType: 'workflow' } },
  'visual-check-authored-missing-mode': { command: 'visual-check', fake: { authoredScroll: at1440, authoredDiagramType: null } },
  'visual-check-authored-clipped': { command: 'visual-check', fake: { authoredScroll: at1440, authoredUnclipped: false } },
  'visual-check-authored-unreadable': { command: 'visual-check', fake: { authoredScroll: at1440, unreadable: at1440 } },
  'visual-check-authored-horizontal': { command: 'visual-check', fake: { authoredScroll: at1440, overflow: at1440 } },
  // T:3209
  'visual-check-cleanup-failure': { command: 'visual-check', fake: { closeFails: true } },
  // T:2700, T:2737, T:2763 (delivery re-verification before and after the browser; a screenshot-path claimant)
  'visual-check-verify-before': { command: 'visual-check', outDir: 'changed-before-browser-evidence', before: [{ pass: true }], verify: { failOnCall: 1, status: 'mismatch', code: 'delivery/provenance-mismatch' } },
  'visual-check-verify-after': { command: 'visual-check', provenance: { status: 'current', receiptId: 'previous-receipt' }, verify: { failOnCall: 2, status: 'failed', code: 'delivery/provenance-failed' } },
  'visual-check-verify-claimant': { command: 'visual-check', provenance: { status: 'current', receiptId: 'previous-receipt' }, verify: { failOnCall: 2, status: 'failed', code: 'delivery/provenance-failed', claimScreenshot: true } },
  // T:2752 (failure persistence refuses an unowned non-file evidence path), plain persistence, ownership refusals at preflight
  'visual-check-persist': { command: 'visual-check', persist: persistFailure },
  'visual-check-persist-unowned-directory': { command: 'visual-check', before: [{ mkdir: 'diagram.visual-check.1440x900.light.png' }], persist: persistFailure },
  'visual-check-stray-capture': { command: 'visual-check', before: [{ write: 'diagram.visual-check.1440x900.light.png', text: 'stray\n' }] },
  'visual-check-artifact-changed': { command: 'visual-check', before: [{ pass: true }, { write: 'diagram.html', text: `${ARTIFACT_TEXT}\n` }] },
  'visual-check-foreign-schema': { command: 'visual-check', before: [{ write: 'diagram.visual-check.json', text: '{"schemaVersion":2,"command":"visual-check"}\n' }] },
  'visual-check-tampered-capture': { command: 'visual-check', before: [{ pass: true }, { write: 'diagram.visual-check.2048x1320.dark.png', text: 'tampered\n' }] },
  'visual-check-missing-capture': { command: 'visual-check', before: [{ pass: true }, { delete: 'diagram.visual-check.1440x900.dark.png' }] },
  // The remaining ownership refusals of beginVisualEvidenceWrite (VC:644-762): a receipt that names no absolute directory, and a
  // passing receipt edited to drop or break its evidence list. `edit` rewrites a JSON file (indent 2) with `key` set to `value`,
  // or deleted when there is no `value`.
  'visual-check-ownership-sidecar-path-mismatch': { command: 'visual-check', before: [{ write: 'diagram.visual-check.json',
    text: '{"schemaVersion":1,"command":"visual-check","sidecars":{"receipt":"diagram.visual-check.json","contactSheet":"diagram.visual-check.html"}}\n' }] },
  'visual-check-ownership-digests-missing': { command: 'visual-check', before: [{ pass: true }, { edit: 'diagram.visual-check.json', key: ['sidecars', 'files'] }] },
  'visual-check-ownership-list-invalid': { command: 'visual-check', before: [{ pass: true },
    { edit: 'diagram.visual-check.json', key: ['sidecars', 'files'], value: ['not an evidence entry'] }] },
  // An --out-dir that is a regular file is no evidence-write failure: the target sidecar namespace cannot be established, and that
  // input error escapes the engine (`throws` records its message and code).
  'browser-check-out-dir-file': { command: 'browser-check', outDir: 'evidence-file', before: [{ write: 'evidence-file', text: 'not a directory\n' }], throws: true },
};
const untoken = (value, work) => JSON.parse(JSON.stringify(value).replaceAll('<WORK>', escaped(work)));
let engineCount = 0;
for (const [name, scenario] of Object.entries(engineScenarios)) {
  const work = fs.mkdtempSync(path.join(tmpRoot, 'w-'));
  try {
    const artifact = path.join(work, 'diagram.html');
    fs.writeFileSync(artifact, ARTIFACT_TEXT);
    const outDir = scenario.outDir ? path.join(work, scenario.outDir) : undefined;
    const browserCheck = scenario.command === 'browser-check';
    const run = browserCheck ? visualCheck.runBrowserCheck : visualCheck.runVisualCheck;
    const outputs = (browserCheck ? visualCheck.browserCheckSidecarPaths : visualCheck.sidecarPaths)(artifact, { outDir });
    for (const step of scenario.before ?? []) {
      if (step.pass) {
        const previous = await run({ artifactPath: artifact, outDir, chromePath: '/fake/chrome', browserFactory: async () => engineBrowser() });
        expect(previous.exitCode === 0, `${name}: the earlier run passes`);
      } else if (step.write) fs.writeFileSync(path.join(work, step.write), step.text);
      else if (step.mkdir) fs.mkdirSync(path.join(work, step.mkdir));
      else if (step.delete) fs.rmSync(path.join(work, step.delete));
      else if (step.edit) {
        const file = path.join(work, step.edit);
        const value = JSON.parse(fs.readFileSync(file, 'utf8'));
        const parent = step.key.slice(0, -1).reduce((node, key) => node[key], value);
        if ('value' in step) parent[step.key.at(-1)] = step.value;
        else delete parent[step.key.at(-1)];
        fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`);
      } else throw new Error(`${name}: unknown before step ${JSON.stringify(step)}`);
    }
    if (scenario.throws) {
      const error = await run({ artifactPath: artifact, outDir, chromePath: '/fake/chrome', browserFactory: async () => engineBrowser() })
        .then(() => null, (thrown) => thrown);
      expect(error, `${name}: the engine throws`);
      writeJson(`${CLI_FIX}/browser-engine/${name}.json`, { scenario, throws: { message: tokenise(error.message, work), code: error.code ?? null }, entries: walk(work).sort() });
      engineCount += 1;
      continue;
    }
    const calls = [];
    let launched = false;
    let verifications = 0;
    let result;
    if (scenario.persist) {
      const persist = browserCheck ? visualCheck.persistBrowserCheckFailure : visualCheck.persistVisualCheckFailure;
      result = { exitCode: 1, receipt: persist(artifact, untoken(scenario.persist, work), { outDir }) };
    } else {
      const { verify } = scenario;
      result = await run({
        artifactPath: artifact,
        outDir,
        ...(scenario.provenance ? { deliveryProvenance: scenario.provenance } : {}),
        ...(scenario.browser === 'unavailable' ? { resolveChrome: () => null } : { chromePath: '/fake/chrome' }),
        browserFactory: async () => {
          launched = true;
          if (scenario.browser === 'launch-error') throw new Error('synthetic launch failure');
          if (scenario.browser === 'startup-timeout') {
            throw Object.assign(new Error('synthetic startup timeout'), { code: 'ERR_CHROME_CDP_TIMEOUT', method: 'Target.getTargets' });
          }
          return engineBrowser(scenario.fake, calls);
        },
        ...(verify ? {
          verifyArtifact: (bytes) => {
            expect(bytes.toString('utf8') === ARTIFACT_TEXT, `${name}: verifyArtifact gets the captured bytes`);
            verifications += 1;
            if (verifications !== verify.failOnCall) return;
            if (verify.claimScreenshot) fs.mkdirSync(outputs.screenshots[0].path);
            throw Object.assign(new Error(`synthetic ${verify.status} delivery`), {
              deliveryProvenance: { status: verify.status }, archifyDiagnostics: [engineDiagnostic(verify.code)],
            });
          },
        } : {}),
      });
    }
    const read = (file) => (file && fs.existsSync(file) && fs.statSync(file).isFile() ? tokenise(fs.readFileSync(file, 'utf8'), work) : null);
    writeJson(`${CLI_FIX}/browser-engine/${name}.json`, {
      scenario,
      exit: result.exitCode,
      launched,
      calls,
      receipt: tokenise(`${JSON.stringify(result.receipt, null, 2)}\n`, work),
      sidecar: read(outputs.receipt),
      contactSheet: browserCheck ? null : read(outputs.contactSheet),
      summary: tokenise(JSON.stringify(visualCheck.summarizeBrowserEvidence(result.receipt)), work),
      entries: walk(work).sort(),
    });
    engineCount += 1;
  } finally {
    fs.rmSync(work, { recursive: true, force: true });
  }
}

// evidenceSidecarPaths (VC:190-241) on real directories of this volume: beside the artifact, an existing --out-dir and a missing
// one (which path calculation must not create). `pathHashed` marks a name whose --out-dir alias guard hashed the artifact's
// absolute path into it, so only its shape is portable.
const sidecarWork = fs.mkdtempSync(path.join(tmpRoot, 'sidecars-'));
fs.mkdirSync(path.join(sidecarWork, 'source'));
fs.mkdirSync(path.join(sidecarWork, 'evidence'));
const evidenceNames = ['outdir-source.html', 'WebApp.HTML', 'report.htm', 'Ünïcode Straße.html', 'Ünïcode Straße.html'.normalize('NFD'),
  `${'L'.repeat(240)}.html`, `${'界'.repeat(80)}.html`];
const evidenceTable = [];
for (const name of evidenceNames) {
  const file = path.join(sidecarWork, 'source', name);
  fs.writeFileSync(file, '<!doctype html>', { flag: 'wx' });
  try {
    for (const command of ['browser-check', 'visual-check']) {
      const calculate = command === 'browser-check' ? visualCheck.browserCheckSidecarPaths : visualCheck.sidecarPaths;
      const beside = calculate(file);
      for (const outDir of [null, 'evidence', 'missing\\deeper']) {
        const outputs = outDir ? calculate(file, { outDir: path.join(sidecarWork, outDir) }) : beside;
        expect(!fs.existsSync(path.join(sidecarWork, 'missing')), 'path calculation creates no directory');
        const pathHashed = Boolean(outDir) && path.basename(outputs.receipt) !== path.basename(beside.receipt);
        // The hash of a path-hashed name covers this run's temp directory: masked, so the table is deterministic.
        const name_ = (file) => (pathHashed ? path.basename(file).replace(/\.~archify-[0-9a-f]{64}\./u, '.~archify-<hash>.') : path.basename(file));
        evidenceTable.push({
          name, command, outDir,
          directory: tokenise(path.dirname(outputs.receipt), sidecarWork).replace('<WORK>', '<DIR>'),
          receipt: name_(outputs.receipt),
          contactSheet: name_(outputs.contactSheet),
          screenshots: outputs.screenshots.map((entry) => name_(entry.path)),
          pathHashed,
        });
      }
    }
  } finally {
    fs.unlinkSync(file);
  }
}
expect(evidenceTable.some((row) => row.pathHashed) && evidenceTable.some((row) => row.outDir && !row.pathHashed), 'both kinds of --out-dir name');
expect(evidenceTable.every((row) => row.pathHashed === row.receipt.includes('<hash>')), 'exactly the path-hashed names are masked');
writeJson(`${CLI_FIX}/paths/evidence-sidecar-names.json`, evidenceTable);

// --- browser-check / visual-check commands (Task 11): T/cli.test.mjs:439-634, 4050, 4225, and receipts from real Chromium ---
// A fixture gains `entries` (the workdir afterwards, sorted) and `evidence` (workdir path -> each evidence file: its text, or
// `png <W>x<H>` from the IHDR of a real PNG). v4 ids are <uuid>. A capture's sha256 and size depend on the browser build (Q4):
// receipts carry `<sha256>` and `<bytes>` for each PNG in sidecars.files.
expect(fs.existsSync(CHROME), `the Playwright Chromium 1243 headless shell at ${CHROME}; run archify install-browser`);
craft('delivered.html', '<!doctype html><html><body>delivered</body></html>');
const EVIDENCE_NAME = /\.(?:visual|browser)-check\./u;
function maskCaptures(text) {
  if (!text.startsWith('{')) return text;
  const receipt = JSON.parse(text);
  const files = receipt.sidecars?.files ?? [];
  if (!files.some((f) => f.file.endsWith('.png'))) return text;
  for (const f of files) if (f.file.endsWith('.png')) Object.assign(f, { sha256: '<sha256>', bytes: '<bytes>' });
  return `${JSON.stringify(receipt, null, 2)}\n`;
}
const normalise = (text) => maskCaptures(text.replace(UUID_V4, '<uuid>'));
function evidenceFixture(group, name, args, files, setup, childEnv) {
  return mint(group, name, args, files, (work, fixture) => {
    fixture.stdout = normalise(fixture.stdout);
    fixture.stderr = normalise(fixture.stderr);
    fixture.entries = walk(work).sort();
    fixture.evidence = Object.fromEntries(fixture.entries.filter((e) => EVIDENCE_NAME.test(path.basename(e)) && fs.statSync(path.join(work, e)).isFile())
      .map((e) => {
        const bytes = fs.readFileSync(path.join(work, e));
        return [e, bytes.subarray(0, 8).equals(PNG) ? `png ${bytes.readUInt32BE(16)}x${bytes.readUInt32BE(20)}` : normalise(tokenise(bytes.toString('utf8'), work))];
      }));
  }, setup, childEnv).fixture;
}

// Without a browser: ARCHIFY_CHROME names a file that does not exist (upstream's "Chrome unavailable"; the C# tests use a factory
// that reports none). The stale-evidence writes are T/cli.test.mjs:4050 and :4225.
const noChrome = { ...env, ARCHIFY_CHROME: path.join(tmpRoot, 'missing-chrome', 'chrome.exe') };
const deliveredHtml = { 'out.html': inputs['delivered.html'] };
const stale = (base, screenshots) => [
  { write: `${base}.json`, text: '{"sentinel":"stale receipt"}\n' },
  { write: `${base}.html`, text: '<!doctype html><title>stale contact sheet</title>\n' },
  ...screenshots.map((s) => ({ write: `${base}.${s}.png`, text: 'stale screenshot' })),
];
const allShots = ['1440x900.light', '1440x900.dark', '2048x1320.light', '2048x1320.dark'];
const evidenceCases = [
  // [name, args, files, setup, exit]
  ['browser-check-skipped-json', ['browser-check', 'out.html', '--json'], deliveredHtml, undefined, 2],
  ['visual-check-skipped-json', ['visual-check', 'out.html', '--json'], deliveredHtml, undefined, 2],
  ['browser-check-skipped-human', ['browser-check', 'out.html'], deliveredHtml, undefined, 2],
  ['visual-check-skipped-human', ['visual-check', 'out.html'], deliveredHtml, undefined, 2],
  ['browser-check-skipped-summary', ['browser-check', 'out.html', '--summary'], deliveredHtml, undefined, 2],
  ['visual-check-summary-out-dir', ['visual-check', 'out.html', '--summary', '--out-dir', 'evidence'], deliveredHtml, undefined, 2],
  ['visual-check-out-dir-json', ['visual-check', 'out.html', '--json', '--out-dir', 'evidence'], deliveredHtml, undefined, 2],
  ['visual-check-summary-missing', ['visual-check', 'missing.html', '--summary'], {}, undefined, 1],
  ['visual-check-missing-human', ['visual-check', 'missing.html'], {}, undefined, 1],
  ['browser-check-missing-human', ['browser-check', 'missing.html'], {}, undefined, 1],
  ['visual-check-out-dir-provenance', ['visual-check', 'out.html', '--json', '--require-provenance', '--out-dir', 'evidence'], deliveredHtml, undefined, 1],
  ['browser-check-provenance-human', ['browser-check', 'out.html', '--require-provenance'], deliveredHtml, undefined, 1],
  ['visual-check-invalid-out-dir-json', ['visual-check', 'out.html', '--json', '--out-dir', 'C:evidence'], deliveredHtml, undefined, 1],
  ['visual-check-invalid-out-dir-human', ['visual-check', 'out.html', '--out-dir', 'C:evidence'], deliveredHtml, undefined, 1],
  ['browser-check-invalid-out-dir-summary', ['browser-check', 'out.html', '--summary', '--out-dir', 'C:evidence'], deliveredHtml, undefined, 1],
  ['visual-check-input-missing-stale', ['visual-check', 'missing-visual.html', '--json'], {}, stale('missing-visual.visual-check', ['1440x900.light']), 1],
  ['visual-check-input-txt-stale', ['visual-check', 'wrong-visual.txt', '--json'], {},
    [{ write: 'wrong-visual.txt', text: '<html></html>' }, ...stale('wrong-visual.txt.visual-check', ['1440x900.light'])], 1],
  ['visual-check-provenance-preflight-stale', ['visual-check', 'out.html', '--json', '--require-provenance'], withFailure,
    [delivered, failedRedelivery, ...stale('out.visual-check', allShots)], 1],
];
for (const [name, args, files, setup, exit] of evidenceCases) {
  const f = evidenceFixture('evidence', name, args, files, setup, noChrome);
  expect(f.exit === exit, `evidence ${name}: exit ${f.exit}, expected ${exit}`);
}
// An evidence directory where files cannot be created: refused before any browser, when the staging directory cannot be made
// (VC:772-788). Its reason is the OS's mkdtemp error with a random suffix, so the fixture keeps `<REASON>` (Q8: compared by code
// and subject).
{
  const f = evidenceFixture('evidence-acl', 'browser-check-unwritable-directory', ['browser-check', 'locked/plain.html', '--json'], {},
    [{ mkdir: 'locked' }, { write: 'locked/plain.html', text: '<!doctype html><html><body>plain</body></html>\n' }, { denyCreate: 'locked' }], noChrome);
  const receipt = JSON.parse(f.stdout);
  const errors = receipt.diagnostics[0].evidence.errors;
  expect(f.exit === 1 && receipt.chrome.status === 'not-checked' && receipt.diagnostics[0].code === 'viewer/evidence-write' && errors.length === 1
    && errors[0].reason.startsWith('EPERM'), 'unwritable evidence directory: refused before the browser');
  errors[0].reason = '<REASON>';
  f.stdout = `${JSON.stringify(receipt, null, 2)}\n`;
  writeJson(`${CLI_FIX}/evidence-acl/browser-check-unwritable-directory.json`, f);
}
for (const command of ['browser-check', 'visual-check']) {
  mint('arguments', `${command}-no-args`, [command]);
  mint('arguments', `${command}-extra-positional`, [command, 'a.html', 'b.html']);
  mint('arguments', `${command}-unknown-option`, [command, 'out.html', '--bogus', '--other']);
  mint('arguments', `${command}-out-dir-missing`, [command, 'out.html', '--out-dir']);
  mint('arguments', `${command}-out-dir-empty`, [command, 'out.html', '--out-dir=']);
}

// With Chromium: one golden of each diagram type through browser-check (sidecar = stdout), plus human, summary, delivered
// --require-provenance and visual-check runs. Every receipt must come from a launched browser.
const TYPE_GOLDENS = ['web-app-rendered.html', 'workflow-agent-tool-call-rendered.html', 'sequence-cache-miss-request.html',
  'dataflow-product-analytics.html', 'lifecycle-agent-run.html', 'erd-orders-rendered.html', 'tree-payment-platform-rendered.html',
  'class-payments-rendered.html', 'timeline-payment-incident-rendered.html', 'waterfall-checkout-request-rendered.html'];
const launched = (receipt) => receipt.chrome.status === 'available' && receipt.chrome.executable === '<CHROME>' && receipt.themeStates.viewports.length === 6;
for (const g of TYPE_GOLDENS) {
  const name = g.replace(/\.html$/u, '');
  const f = evidenceFixture('browser-check', name, ['browser-check', g, '--json'], exFiles(g), undefined, chromeEnv);
  expect(launched(JSON.parse(f.stdout)) && f.evidence[`${name}.browser-check.json`] === f.stdout, `browser-check ${g}: a launched run whose sidecar is stdout`);
}
evidenceFixture('browser-check', 'web-app-rendered-human', ['browser-check', 'web-app-rendered.html'], exFiles('web-app-rendered.html'), undefined, chromeEnv);
evidenceFixture('browser-check', 'web-app-rendered-summary', ['browser-check', 'web-app-rendered.html', '--summary'], exFiles('web-app-rendered.html'), undefined, chromeEnv);
const provenanceRun = evidenceFixture('browser-check', 'delivered-require-provenance', ['browser-check', 'out.html', '--json', '--require-provenance'],
  exFiles(arch), [delivered], chromeEnv);
expect(launched(JSON.parse(provenanceRun.stdout)) && JSON.parse(provenanceRun.stdout).provenance === 'current', 'browser-check on a delivered artifact: current');
const visualRun = evidenceFixture('visual-check', 'web-app-rendered', ['visual-check', 'web-app-rendered.html', '--json'], exFiles('web-app-rendered.html'), undefined, chromeEnv);
expect(launched(JSON.parse(visualRun.stdout)) && Object.values(visualRun.evidence).filter((v) => v.startsWith('png ')).length === 4, 'visual-check writes 4 PNGs');
evidenceFixture('visual-check', 'web-app-rendered-human', ['visual-check', 'web-app-rendered.html'], exFiles('web-app-rendered.html'), undefined, chromeEnv);

// --- finalize (Task 12): commandFinalize (A:5706-5863) over runFinalize (finalize.mjs:706-1009) ---
// Receipts are scrubbed of what varies between runs (Q8, the brief): startedAt, finishedAt, every durationMs, each stage's command
// (D8) and stderr, the update blocks (D7), and v4 ids -> <uuid>. A scrubbed JSON keeps its layout (one line or indent 2).
function scrubFinalize(text) {
  const t = text.replace(UUID_V4, '<uuid>');
  if (!t.startsWith('{')) return t;
  const value = JSON.parse(t);
  for (const key of ['startedAt', 'finishedAt', 'durationMs', 'update']) delete value[key];
  for (const stage of Object.values(value.stages ?? {})) {
    for (const key of ['durationMs', 'command', 'stderr']) delete stage[key];
    if (stage.receipt && typeof stage.receipt === 'object') delete stage.receipt.update;
  }
  return `${JSON.stringify(value, null, t.includes('\n  ') ? 2 : undefined)}\n`;
}
// A fixture gains `entries` (the workdir afterwards) and `sidecars` (workdir path -> scrubbed text of each finalize receipt, summary,
// delivery sidecar and browser-check receipt left behind).
const FINALIZE_SIDECAR = /(\.finalize\.json|-summary\.json|\.delivery\.json|\.delivery-pending\.json|\.browser-check\.json|^\.archify-delivery-lock\.json)$/u;
function finalizeFixture(group, name, args, files, childEnv) {
  return mint(group, name, args, files, (work, fixture) => {
    fixture.stdout = scrubFinalize(fixture.stdout);
    fixture.stderr = fixture.stderr.replace(UUID_V4, '<uuid>');
    fixture.entries = walk(work).sort();
    fixture.sidecars = Object.fromEntries(fixture.entries.filter((e) => FINALIZE_SIDECAR.test(path.basename(e)) && fs.statSync(path.join(work, e)).isFile())
      .map((e) => [e, scrubFinalize(tokenise(fs.readFileSync(path.join(work, e), 'utf8'), work))]));
  }, undefined, childEnv).fixture;
}
// Argument errors (A:5706-5800): a receipt with --json (A:7133), else stderr; a missing --out-dir value is fail() either way.
const finalizeArgs = [
  ['finalize-usage-no-args', ['finalize', '--json']],
  ['finalize-missing-output', ['finalize', 'architecture', arch, '--json']],
  ['finalize-extra-positional', ['finalize', 'architecture', arch, 'out.html', 'extra.html', '--json']],
  ['finalize-unknown-option', ['finalize', 'architecture', arch, 'out.html', '--bogus', '--json']],
  ['finalize-unknown-type', ['finalize', 'bogus', arch, 'out.html', '--json']],
  ['finalize-quality-invalid', ['finalize', 'architecture', arch, 'out.html', '--quality', 'hero', '--json']],
  ['finalize-receipt-missing', ['finalize', 'architecture', arch, 'out.html', '--json', '--receipt']],
  ['finalize-receipt-empty', ['finalize', 'architecture', arch, 'out.html', '--receipt=', '--json']],
  ['finalize-candidate-missing', ['finalize', 'architecture', arch, 'out.html', '--json', '--candidate-sha256']],
  ['finalize-candidate-invalid', ['finalize', 'architecture', arch, 'out.html', '--candidate-sha256', 'ABC', '--json']],
];
for (const [name, args] of finalizeArgs) {
  const { fixture } = mint('arguments', `${name}-json`, args, filesFor(args));
  expect(fixture.exit === 2 && fixture.stderr === '' && JSON.parse(fixture.stdout).stage === 'arguments', `${name}-json receipt`);
  const human = args.filter((a) => a !== '--json');
  expect(mint('arguments', name, human, filesFor(human)).fixture.exit === 2, `${name} human`);
}
mint('arguments', 'finalize-out-dir-missing-json', ['finalize', 'architecture', arch, 'out.html', '--json', '--out-dir'], exFiles(arch));

// v3.2.0 --writing (extractExplainerWritingArgs, A:2397-2412): extracted after --repo-root (validate, deliver; inspect through
// validate) and after --candidate-sha256 (finalize), before the unknown-option check. A valid value needs an explainer document,
// which is not ported (PORT-009), so on a diagram type it is cli/unsupported-option. [name, args, code of the receipt]
const writingCases = [
  ['validate-writing-diagram', ['validate', 'architecture', arch, '--writing', 'warn', '--json'], 'cli/unsupported-option'],
  ['deliver-writing-diagram', ['deliver', 'architecture', arch, '--writing=strict', '--json'], 'cli/unsupported-option'],
  ['finalize-writing-diagram', ['finalize', 'architecture', arch, 'out.html', '--writing', 'off', '--json'], 'cli/unsupported-option'],
  ['inspect-writing-diagram', ['inspect', 'architecture', arch, '--writing', 'warn', '--json'], 'cli/unsupported-option'],
  ['validate-writing-twice', ['validate', 'architecture', arch, '--writing', 'warn', '--writing=off', '--json'], 'cli/invalid-arguments'],
  ['finalize-writing-twice', ['finalize', 'architecture', arch, 'out.html', '--writing=warn', '--writing', 'warn', '--json'], 'cli/invalid-arguments'],
  ['validate-writing-bogus', ['validate', 'architecture', arch, '--writing', 'bogus', '--json'], 'cli/invalid-arguments'],
  ['deliver-writing-bogus', ['deliver', 'architecture', arch, '--writing=', '--json'], 'cli/invalid-arguments'],
  ['validate-writing-missing', ['validate', 'architecture', arch, '--json', '--writing'], 'cli/invalid-arguments'],
  // the order: --repo-root (validate) and --candidate-sha256 (finalize) are extracted first; the unknown-option check comes after
  ['validate-writing-after-repo-root', ['validate', 'architecture', arch, '--writing', 'bogus', '--repo-root', '--json'], 'cli/missing-option-value'],
  ['finalize-writing-after-candidate', ['finalize', 'architecture', arch, 'out.html', '--writing', 'bogus', '--candidate-sha256', 'ABC', '--json'], 'cli/invalid-option-value'],
  ['deliver-writing-before-unknown-option', ['deliver', 'architecture', arch, '--bogus', '--writing', 'warn', '--json'], 'cli/unsupported-option'],
];
for (const [name, args, code] of writingCases) {
  const { fixture } = mint('arguments', `${name}-json`, args, filesFor(args));
  const receipt = JSON.parse(fixture.stdout);
  expect(fixture.exit === 2 && fixture.stderr === '' && receipt.stage === 'arguments' && receipt.diagnostics[0].code === code, `${name}-json: ${code}`);
  const human = args.filter((a) => a !== '--json');
  const h = mint('arguments', name, human, filesFor(human)).fixture;
  expect(h.exit === 2 && h.stdout === '' && h.stderr.length > 0, `${name} human`);
}
// `--writing` takes the next token even when it is an option: `--json` becomes the (invalid) value, yet the argv still asks for a receipt.
{
  const { fixture } = mint('arguments', 'validate-writing-takes-json-json', ['validate', 'architecture', arch, '--writing', '--json'], exFiles(arch));
  expect(fixture.exit === 2 && JSON.parse(fixture.stdout).diagnostics[0].code === 'cli/invalid-arguments', 'validate-writing-takes-json-json receipt');
}

// Without a browser (ARCHIFY_CHROME at a missing file; the C# tests use a factory that reports none): runs that stop before the
// browser gate, and the skipped gate itself (exit 2).
const finalizeCases = [
  // [name, args, files, exit]
  ['skipped-json', ['finalize', 'architecture', arch, 'out.html', '--json'], exFiles(arch), 2],
  ['skipped-human', ['finalize', 'architecture', arch, 'out.html'], exFiles(arch), 2],
  ['skipped-standard-json', ['finalize', 'architecture', arch, 'out.html', '--quality', 'standard', '--json'], exFiles(arch), 2],
  ['skipped-out-dir-json', ['finalize', 'architecture', arch, 'out.html', '--out-dir', 'evidence', '--json'], exFiles(arch), 2],
  ['skipped-custom-receipt-json', ['finalize', 'architecture', arch, 'out.html', '--receipt', 'reports/final.json', '--json'], exFiles(arch), 2],
  // v3.2.0 --browser-check (A:5897): accepted, and a diagram type runs the browser gate with or without it.
  ['browser-check-skipped-json', ['finalize', 'architecture', arch, 'out.html', '--browser-check', '--json'], exFiles(arch), 2],
  // T/finalize-paths.test.mjs:25-40: a 78-character CJK name; every delivery and evidence name is bounded.
  ['long-output-json', ['finalize', 'architecture', arch, `${'审'.repeat(78)}.html`, '--quality', 'showcase', '--json'], exFiles(arch), 2],
  // A deliver failure at stage input is a failed validate gate (finalize.mjs:923-934) with nextAction.
  ['deliver-input-failure-json', ['finalize', 'workflow', 'broken.workflow.json', 'out.html', '--json'], broken, 1],
  ['deliver-input-failure-human', ['finalize', 'workflow', 'broken.workflow.json', 'out.html'], broken, 1],
  // Showcase promotes composition warnings to a failed deliver gate (finalize.mjs:880-891).
  ['showcase-warning-json', ['finalize', 'workflow', 'incident-response.workflow.json', 'out.html', '--json'], exFiles('incident-response.workflow.json'), 1],
  ['showcase-warning-human', ['finalize', 'workflow', 'incident-response.workflow.json', 'out.html'], exFiles('incident-response.workflow.json'), 1],
  // "could not start" (A:5826-5847): candidate digest, --out-dir syntax (an OutputPathError's diagnostics, spread).
  ['candidate-changed-json', ['finalize', 'architecture', arch, 'out.html', '--candidate-sha256', '0'.repeat(64), '--json'], exFiles(arch), 1],
  ['candidate-changed-human', ['finalize', 'architecture', arch, 'out.html', '--candidate-sha256', '0'.repeat(64)], exFiles(arch), 1],
  ['invalid-out-dir-json', ['finalize', 'architecture', arch, 'out.html', '--out-dir', 'C:evidence', '--json'], exFiles(arch), 1],
  ['receipt-collision-human', ['finalize', 'architecture', arch, 'diagram.html', '--receipt', 'diagram.delivery.json'], exFiles(arch), 1],
];
// T/finalize-paths.test.mjs:42-53: a receipt path that would replace a delivery sidecar or the directory lock; nothing is written.
for (const name of ['diagram.delivery.json', 'diagram.delivery-pending.json', 'diagram.delivery-lock.json', '.archify-delivery-lock.json']) {
  finalizeCases.push([`receipt-collision-${name.replace(/^\./u, '').replace(/\.json$/u, '')}-json`,
    ['finalize', 'architecture', arch, 'diagram.html', '--quality', 'showcase', '--json', '--receipt', name], exFiles(arch), 1]);
}
for (const [name, args, files, exit] of finalizeCases) {
  const f = finalizeFixture('finalize', name, args, files, noChrome);
  expect(f.exit === exit, `finalize ${name}: exit ${f.exit}, expected ${exit}`);
  if (name.startsWith('receipt-collision')) expect(f.entries.length === 1 && (f.stdout + f.stderr).includes('distinct'), `finalize ${name} writes nothing`);
  if (name === 'long-output-json') expect(f.entries.every((e) => Buffer.byteLength(e) <= 255), 'bounded names');
  if (name === 'browser-check-skipped-json') {
    const summary = JSON.parse(f.stdout);
    expect(summary.gates.check === 'pass' && summary.gates['browser-check'] === 'skipped' && summary.browserCheck?.requested === true
      && summary.browserCheck.ran === false, `finalize ${name}: the browser gate ran (skipped), browserCheck requested`);
  }
}

// With Chromium: the web-app run whose summary, full receipt and sidecars the C# run must equal after scrubbing.
for (const [name, args] of [['web-app-json', ['finalize', 'architecture', arch, 'out.html', '--json']], ['web-app-human', ['finalize', 'architecture', arch, 'out.html']]]) {
  const f = finalizeFixture('finalize-chromium', name, args, exFiles(arch), chromeEnv);
  expect(f.exit === 0 && f.sidecars['out.browser-check.json'].includes('"executable": "<CHROME>"'), `finalize ${name}: a launched, passing run`);
  expect(f.sidecars['out.finalize.json'].includes('"browserCheck": {\n    "requested": true,\n    "ran": true\n  }'), `finalize ${name}: browserCheck ran`);
}

// The phase exit (plan "Exit"): upstream finalize on every example at both qualities, as {exit, ok, failedStage, gates, codes (the full
// receipt's diagnostics), artifact identity}.
const outcomes = [];
for (const f of EXAMPLES) {
  for (const quality of ['standard', 'showcase']) {
    const work = fs.mkdtempSync(path.join(tmpRoot, 'w-'));
    try {
      fs.copyFileSync(repo(`${EX}/${f}`), path.join(work, f));
      const r = spawnSync(process.execPath, [cli, 'finalize', typeOf(f), f, 'out.html', '--quality', quality, '--json'], { cwd: work, env: chromeEnv, encoding: 'utf8' });
      if (r.error) throw r.error;
      const summary = JSON.parse(r.stdout);
      const full = JSON.parse(fs.readFileSync(summary.evidence.receipt, 'utf8'));
      expect(full.stages['browser-check']?.receipt?.chrome?.status !== 'unavailable', `finalize ${f} ${quality}: never a skipped browser`);
      outcomes.push({
        example: f, quality, exit: r.status, ok: summary.ok, failedStage: summary.failedStage ?? null, gates: summary.gates,
        codes: full.diagnostics.map((d) => d.code), artifact: { sha256: summary.artifact.sha256 ?? null, bytes: summary.artifact.bytes ?? null },
      });
    } finally {
      fs.rmSync(work, { recursive: true, force: true });
    }
  }
}
writeJson(`${CLI_FIX}/finalize-exit/outcomes.json`, outcomes);
// Upstream itself does not pass every example: four fail at standard (browser-check, viewer/viewport-overflow) and at showcase
// (deliver, composition/viewport-height). The table, not an all-pass assertion, is the reference.
for (const o of outcomes.filter((x) => !x.ok)) console.log(`upstream finalize fails: ${o.example} --quality ${o.quality} at ${o.failedStage}: ${o.codes.join(', ')}`);

console.log(`minted ${minted.size} CLI fixtures into ${CLI_FIX}, ${engineCount} browser-engine cases, four path tables, and upstream/recipes/scenarios.json`);

// --- explainer (PORT-009 Task 7): validate, deliver, render, check, patch and doctor on explainer documents ---
// A fixture gains `entries` and `sidecars` (as deliver's), and `page` when the command writes one: the HTML tokenised, each
// "htmlGzip" payload as gz-sha256:<hex of the inflated bytes> (Decision 1). The fixed oracle (Decisions 2 and 3, the edits of
// tools/explainer-seeds/run.mjs) runs every page fixture again; `pageFixed` holds its page where it differs, which must be exactly
// the drafts without a locale. Masks (Q8): v4 ids <uuid>; `archify-explainer-*` temp directories <TMP>; every `artifact` identity
// <artifact> and `explainer.preparedSha256` <sha256> (the gzip stream and the absolute paths a bundle digest covers); the human
// `sha256` prefix <artifact>; a receipt's `update` is dropped (Q5).
async function explainerGroup() {
  const X = (name) => inputs[`explainer/${name}`];
  const draft = (doc) => `${JSON.stringify(doc, null, 2)}\n`;
  // T/explainer-cli.test.mjs fixture()
  const base = () => ({ schema_version: 1, diagram_type: 'explainer', meta: { id: 'cli-document', title: 'How is a draft delivered?', locale: 'en' },
    answer: { summary: 'A checked candidate preserves the authored draft.' },
    panels: [{ id: 'delivery', title: 'What is checked?', blocks: [{ id: 'delivery-text', type: 'prose', text: 'The draft is parsed and checked before publication.' }] }],
    sources: [] });
  craft('explainer/author-draft.json', draft(base()));
  const widget = base(); widget.panels[0].blocks[0].type = 'unsupported-widget';
  craft('explainer/unsupported-widget.json', JSON.stringify(widget, null, 2));
  craft('explainer/broken.json', '{\n  broken');
  const image = base(); image.panels[0].blocks.push({ id: 'contained-image', type: 'image', path: 'asset.png', alt: 'A contained test image' });
  craft('explainer/image-draft.json', draft(image));
  craft('explainer/asset.png', Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8XcAAAAASUVORK5CYII=', 'base64'));
  const writing = base(); writing.panels[0].blocks[0].text = 'We will perform an analysis of each and every request.';
  craft('explainer/writing-draft.json', draft(writing));
  const noLocale = base(); delete noLocale.meta.locale;
  craft('explainer/no-locale.json', draft(noLocale));
  const french = base(); french.meta.locale = 'fr';   // specificationLocaleDiagnostics reads a JSON draft's meta (A:1877-1887)
  craft('explainer/unbundled-locale.json', draft(french));
  // Review Focus 1: a mermaid fence whose front matter is not valid YAML (tools/explainer-seeds/validate.json parsed-front-matter).
  craft('explainer/front-matter.archify.md', '# Charts\n\nAnswer.\n\n## Data {#data}\n\n```mermaid\n---\ntitle: [unclosed\n---\npie\n  "A" : 1\n```\n');
  craft('explainer/invalid-utf8.archify.md', Buffer.concat([Buffer.from('# Why is one byte wrong? {#bytes}\n\nThe draft holds a stray byte: '),
    Buffer.from([0xc3, 0x28]), Buffer.from('.\n\n@meta locale=en\n\n## Where is it? {#where}\n\nIn the answer.\n')]));
  // T/explainer-patch.test.mjs's JSON page (tools/explainer-seeds/patch.json json-panel) and replacements.
  const patchPage = { schema_version: 1, diagram_type: 'explainer', meta: { id: 'patch-document', title: 'What can change?', locale: 'en' },
    answer: { summary: 'One stable panel can be replaced.' },
    panels: [
      { id: 'first', title: 'What changes?', blocks: [{ id: 'first-text', type: 'prose', text: 'This text may change.' }] },
      { id: 'second', title: 'What stays?', blocks: [{ id: 'second-text', type: 'prose', text: 'This text remains exactly the same.' }] }],
    sources: [] };
  craft('explainer/patch-author.json', draft(patchPage));
  const panel = (text, extra = []) => draft({ id: 'first', title: 'What changed?', blocks: [{ id: 'first-text', type: 'prose', text }, ...extra] });
  craft('explainer/patch-replacement.json', panel('The selected panel has new content.'));
  craft('explainer/patch-writing.json', panel('We will perform an analysis of each and every request.'));
  craft('explainer/patch-front-matter.json', panel('A chart follows.', [{ id: 'ch', type: 'chart', text: '---\ntitle: [unclosed\n---\npie\n  "A" : 1' }]));
  // The four v3.2.0 cases, as the upstream checkout holds them.
  const CASES = ['first-rental', 'finalize-delivery', 'p99-tail-latency', 'tls-first-visit'];
  for (const c of CASES) craft(`explainer/${c}.archify.md`, execFileSync('git', ['-C', path.dirname(UP), 'show', `v3.2.0:docs/cases/explainer/${c}.archify.md`]));

  // The fixed oracle: a copy of the skill with the decided fixes written in.
  const SKILL_FIXED = path.join(tmpRoot, 'skill-fixed');
  fs.cpSync(SKILL, SKILL_FIXED, { recursive: true });
  const FIXES = {
    'explainer/render.mjs': [["const locale = document.meta.locale || 'zh-CN';", "const locale = document.meta.locale || 'en';"]],
    'explainer/diagrams.mjs': [
      ["export function compileExplainerDiagram(diagramType, rows, { title, id, line = 1, quality = 'showcase' } = {}) {",
        "export function compileExplainerDiagram(diagramType, rows, { title, id, line = 1, quality = 'showcase', locale } = {}) {"],
      ['row ? `分支 ${row}` : spec.meta.title', "row ? (String(locale || 'en').startsWith('zh') ? `分支 ${row}` : `Branch ${row}`) : spec.meta.title"]],
    'explainer/parse.mjs': [[',line:at+1,quality:document.meta.quality_profile});', ',line:at+1,quality:document.meta.quality_profile,locale:document.meta.locale});']],
  };
  for (const [file, edits] of Object.entries(FIXES)) {
    let text = fs.readFileSync(path.join(SKILL_FIXED, file), 'utf8');
    for (const [from, to] of edits) {
      expect(text.split(from).length === 2, `fixed oracle ${file}: one ${JSON.stringify(from)}`);
      text = text.replace(from, () => to);
    }
    fs.writeFileSync(path.join(SKILL_FIXED, file), text);
  }
  const cliFixed = path.join(SKILL_FIXED, 'bin', 'archify.mjs');

  const reEscape = (s) => s.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const TMP = [new RegExp(`${reEscape(escaped(os.tmpdir()))}\\\\\\\\archify-explainer-[a-z]+-[A-Za-z0-9._]+`, 'gu'),
    new RegExp(`${reEscape(os.tmpdir())}\\\\archify-explainer-[a-z]+-[A-Za-z0-9._]+`, 'gu')];
  const maskValue = (value) => Array.isArray(value) ? value.map(maskValue)
    : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([k, v]) => [k,
      k === 'preparedSha256' && typeof v === 'string' ? '<sha256>'
        : k === 'artifact' && v && typeof v === 'object' && typeof v.sha256 === 'string' && typeof v.bytes === 'number' ? '<artifact>'
          : maskValue(v)])) : value;
  const mask = (text) => {
    let t = text.replace(UUID_V4, '<uuid>');
    for (const pattern of TMP) t = t.replace(pattern, '<TMP>');
    t = t.replace(/; sha256 [0-9a-f]{12}$/gmu, '; sha256 <artifact>');
    if (!t.startsWith('{')) return t;
    const value = maskValue(JSON.parse(t));
    delete value.update;   // Q5: no update check
    return `${JSON.stringify(value, null, t.includes('\n  ') ? 2 : undefined)}\n`;
  };
  // A patched page's bundle is bound to the temporary draft (A:5333-5334): its `inputPath`, relative to the page, names that
  // directory, and the bundle digest after which it is appended covers it. Both are masked there, and only there.
  const portablePage = (html, work) => tokenise(html, work)
    .replace(/"htmlGzip":"([A-Za-z0-9+/=]*)"/gu,
      (_, base64) => `"htmlGzip":"gz-sha256:${createHash('sha256').update(gunzipSync(Buffer.from(base64, 'base64'))).digest('hex')}"`)
    .replace(/"digest":"[0-9a-f]{64}","inputPath":"[^"]*?archify-explainer-patch-[A-Za-z0-9._]+/gu, '"digest":"<digest>","inputPath":"<TMP>');
  const seed = (work, files) => {
    for (const [dest, src] of Object.entries(files)) {
      fs.mkdirSync(path.dirname(path.join(work, dest)), { recursive: true });
      fs.copyFileSync(repo(src), path.join(work, dest));
    }
  };
  // The same run through the fixed oracle: its exit and page.
  function fixedRun(args, files, setup, page) {
    const work = fs.mkdtempSync(path.join(tmpRoot, 'w-'));
    try {
      seed(work, files);
      if (setup) applySetup(work, setup, cliFixed);
      const r = spawnSync(process.execPath, [cliFixed, ...args], { cwd: work, env, encoding: 'utf8' });
      if (r.error) throw r.error;
      return { exit: r.status, page: portablePage(fs.readFileSync(path.join(work, page), 'utf8'), work) };
    } finally {
      fs.rmSync(work, { recursive: true, force: true });
    }
  }
  function fixture(name, args, files, { setup, page, noLocale = false } = {}) {
    return mint('explainer', name, args, files, (work, f) => {
      f.stdout = mask(f.stdout);
      f.stderr = mask(f.stderr);
      f.entries = walk(work).sort();
      f.sidecars = Object.fromEntries(f.entries.filter((e) => SIDECAR.test(path.basename(e)))
        .map((e) => [e, mask(tokenise(fs.readFileSync(path.join(work, e), 'utf8'), work))]));
      if (!page) return;
      f.page = portablePage(fs.readFileSync(path.join(work, page), 'utf8'), work);
      const fixed = fixedRun(args, files, setup, page);
      expect(fixed.exit === f.exit && (fixed.page !== f.page) === noLocale, `${name}: the fixed oracle differs exactly for a draft without a locale`);
      if (fixed.page !== f.page) f.pageFixed = fixed.page;
    }, setup).fixture;
  }
  const author = { 'author-draft.json': X('author-draft.json') };
  const deliverAuthor = { run: ['deliver', 'explainer', 'author-draft.json', 'result.html', '--json'], exit: 0 };
  const withImage = { 'author-draft.json': X('image-draft.json'), 'asset.png': X('asset.png') };
  const writingDraft = { 'author-draft.json': X('writing-draft.json') };
  const frontMatter = { 'draft.archify.md': X('front-matter.archify.md') };
  const ok = (f, exit = 0) => expect(f.exit === exit, `explainer fixture exits ${exit}, got ${f.exit}: ${f.stdout}${f.stderr}`);

  // T/explainer-cli.test.mjs:21-30, without injected variables: validate, deliver, check --require-provenance.
  ok(fixture('validate-json', ['validate', 'explainer', 'author-draft.json', '--json'], author));
  ok(fixture('validate-human', ['validate', 'explainer', 'author-draft.json'], author));
  ok(fixture('deliver-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--json'], author, { page: 'result.html' }));
  ok(fixture('deliver-human', ['deliver', 'explainer', 'author-draft.json', 'result.html'], author, { page: 'result.html' }));
  ok(fixture('check-require-provenance', ['check', 'result.html', '--json', '--require-provenance'], author, { setup: [deliverAuthor] }));
  ok(fixture('check', ['check', 'result.html'], author, { setup: [deliverAuthor] }));
  // An explainer page with no delivery, then one with an external script appended: the explainer checker names the file.
  const rendered = { run: ['render', 'explainer', 'author-draft.json', 'page.html'], exit: 0 };
  ok(fixture('check-undelivered', ['check', 'page.html'], author, { setup: [rendered] }));
  ok(fixture('check-tampered', ['check', 'page.html', '--json'], author,
    { setup: [rendered, { append: 'page.html', text: '<script src="https://example.com/x.js"></script>\n' }] }), 1);
  // A:5636: a current delivery of the other family.
  ok(fixture('check-family-native-receipt', ['check', 'result.html', '--require-provenance'], author,
    { setup: [deliverAuthor, { set: 'result.delivery.json', key: 'type', value: 'architecture' }] }), 1);
  ok(fixture('check-family-explainer-receipt', ['check', 'out.html', '--require-provenance'], exFiles(arch),
    { setup: [{ run: ['deliver', 'architecture', arch, 'out.html', '--json'], exit: 0 }, { set: 'out.delivery.json', key: 'type', value: 'explainer' }] }), 1);
  // T/explainer-cli.test.mjs:32-39: a semantic failure keeps the delivered page and records the failed attempt; a malformed draft.
  ok(fixture('deliver-unsupported-block-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--json'],
    { ...author, 'widget.json': X('unsupported-widget.json') }, { setup: [deliverAuthor, { copy: 'widget.json', to: 'author-draft.json' }], page: 'result.html' }), 1);
  ok(fixture('deliver-malformed-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--json'], { 'author-draft.json': X('broken.json') }), 1);
  ok(fixture('validate-malformed-json', ['validate', 'explainer', 'author-draft.json', '--json'], { 'author-draft.json': X('broken.json') }), 1);
  // T/explainer-cli.test.mjs:54-58: render resolves the image against the draft's directory.
  ok(fixture('render-image', ['render', 'explainer', 'author-draft.json', 'result.html'], withImage, { page: 'result.html' }));
  ok(fixture('deliver-image-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--json'], withImage, { page: 'result.html' }));
  // T/explainer-cli.test.mjs:75-84: the writing modes.
  ok(fixture('deliver-writing-warn-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--writing', 'warn', '--json'], writingDraft,
    { page: 'result.html' }));
  ok(fixture('validate-writing-strict-json', ['validate', 'explainer', 'author-draft.json', '--writing', 'strict', '--json'], writingDraft), 1);
  ok(fixture('deliver-writing-strict-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--writing', 'strict', '--json'], writingDraft), 1);
  ok(fixture('deliver-writing-off-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--writing=off', '--json'], writingDraft,
    { page: 'result.html' }));
  ok(fixture('validate-writing-workflow-json', ['validate', 'workflow', 'author-draft.json', '--writing', 'strict', '--json'], writingDraft), 2);
  ok(fixture('validate-writing-twice-json', ['validate', 'explainer', 'author-draft.json', '--writing', 'warn', '--writing', 'off', '--json'], author), 2);
  // Review Focus 1: invalid chart front matter through each command's own catch.
  ok(fixture('validate-front-matter-json', ['validate', 'explainer', 'draft.archify.md', '--json'], frontMatter), 1);
  ok(fixture('validate-front-matter-human', ['validate', 'explainer', 'draft.archify.md'], frontMatter), 1);
  ok(fixture('deliver-front-matter-json', ['deliver', 'explainer', 'draft.archify.md', 'result.html', '--json'], frontMatter), 1);
  ok(fixture('render-front-matter', ['render', 'explainer', 'draft.archify.md', 'result.html'], frontMatter), 1);
  // invalid UTF-8: deliver refuses it (explainer/source-encoding); render reads it as 'utf8' text.
  const invalidUtf8 = { 'draft.archify.md': X('invalid-utf8.archify.md') };
  ok(fixture('deliver-invalid-utf8-json', ['deliver', 'explainer', 'draft.archify.md', 'result.html', '--json'], invalidUtf8), 1);
  ok(fixture('render-invalid-utf8', ['render', 'explainer', 'draft.archify.md', 'result.html'], invalidUtf8, { page: 'result.html' }));
  // The upstream example and a draft without a locale (the fixed oracle's page).
  const example = { 'delivery.explainer.json': `${EX}/delivery.explainer.json` };
  ok(fixture('example-validate-json', ['validate', 'explainer', 'delivery.explainer.json', '--json'], example));
  ok(fixture('example-deliver-json', ['deliver', 'explainer', 'delivery.explainer.json', 'result.html', '--json'], example, { page: 'result.html' }));
  ok(fixture('no-locale-deliver-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--json'], { 'author-draft.json': X('no-locale.json') },
    { page: 'result.html', noLocale: true }));
  // A JSON draft's unbundled locale: the i18n/locale-fallback warning on stderr and in the receipt.
  const unbundled = { 'author-draft.json': X('unbundled-locale.json') };
  ok(fixture('validate-unbundled-locale-json', ['validate', 'explainer', 'author-draft.json', '--json'], unbundled));
  ok(fixture('deliver-unbundled-locale-json', ['deliver', 'explainer', 'author-draft.json', 'result.html', '--json'], unbundled, { page: 'result.html' }));
  // The four cases.
  for (const c of CASES) {
    const files = { [`${c}.archify.md`]: X(`${c}.archify.md`) };
    ok(fixture(`case-${c}-validate-json`, ['validate', 'explainer', `${c}.archify.md`, '--json'], files));
    ok(fixture(`case-${c}-deliver-json`, ['deliver', 'explainer', `${c}.archify.md`, `${c}.html`, '--json'], files, { page: `${c}.html` }));
    ok(fixture(`case-${c}-render`, ['render', 'explainer', `${c}.archify.md`, `${c}.html`], files, { page: `${c}.html` }));
  }
  // patch (A:5304-5350): a delivered page, one panel replaced.
  const patchFiles = (replacement = 'patch-replacement.json') => ({ 'author.json': X('patch-author.json'), 'replacement.json': X(replacement) });
  const deliverPage = { run: ['deliver', 'explainer', 'author.json', 'page.html', '--json'], exit: 0 };
  const patch = (...rest) => ['patch', 'explainer', 'page.html', ...rest];
  ok(fixture('patch-json', patch('first', 'replacement.json', 'out.html', '--json'), patchFiles(), { setup: [deliverPage], page: 'out.html' }));
  ok(fixture('patch-human', patch('first', 'replacement.json', 'out.html'), patchFiles(), { setup: [deliverPage], page: 'out.html' }));
  ok(fixture('patch-usage-json', patch('first', '--json'), patchFiles(), { setup: [deliverPage] }), 2);
  ok(fixture('patch-usage', patch('first'), patchFiles(), { setup: [deliverPage] }), 2);
  ok(fixture('patch-missing-panel-json', patch('missing', 'replacement.json', 'out.html', '--json'), patchFiles(), { setup: [deliverPage] }), 1);
  ok(fixture('patch-missing-artifact-json', ['patch', 'explainer', 'missing.html', 'first', 'replacement.json', 'out.html', '--json'], patchFiles()), 1);
  ok(fixture('patch-tampered-artifact-json', patch('first', 'replacement.json', 'out.html', '--json'), patchFiles(),
    { setup: [deliverPage, { append: 'page.html', text: '<script src="https://example.com/x.js"></script>\n' }] }), 1);
  ok(fixture('patch-writing-strict-json', patch('first', 'replacement.json', 'out.html', '--writing', 'strict', '--json'), patchFiles('patch-writing.json'),
    { setup: [deliverPage] }), 1);
  ok(fixture('patch-front-matter-json', patch('first', 'replacement.json', 'out.html', '--json'), patchFiles('patch-front-matter.json'),
    { setup: [deliverPage] }), 1);
  // Patching a native page (UPSTREAM-DEVIATION patch-payload-message: upstream's message is the gzip one).
  ok(fixture('patch-native-page-json', ['patch', 'explainer', 'page.html', 'first', 'replacement.json', 'out.html', '--json'],
    { ...patchFiles(), 'page.html': `${EX}/web-app-rendered.html` }), 1);
  // doctor's explainer lines (A:6151, :6168-6170), against which the port's Q3 doctor is checked.
  ok(fixture('doctor', ['doctor'], {}));

  // --- finalize explainer and the explainer browser gate (PORT-009 Task 8) ---
  // Without a browser (ARCHIFY_CHROME at a missing file): the finalize receipts, scrubbed as finalize's and masked as above.
  const FINALIZE_FILES = /(\.finalize\.json|-summary\.json|\.delivery\.json|\.delivery-pending\.json|\.browser-check\.json|^\.archify-delivery-lock\.json)$/u;
  const noBrowser = { ...env, ARCHIFY_CHROME: path.join(tmpRoot, 'missing-chrome', 'chrome.exe') };
  const finalizeExplainer = (name, args, files, exit) => {
    const { fixture: f } = mint('explainer-finalize', name, args, files, (work, x) => {
      x.stdout = scrubFinalize(mask(x.stdout));
      x.stderr = mask(x.stderr);
      x.entries = walk(work).sort();
      x.sidecars = Object.fromEntries(x.entries.filter((e) => FINALIZE_FILES.test(path.basename(e)))
        .map((e) => [e, scrubFinalize(mask(tokenise(fs.readFileSync(path.join(work, e), 'utf8'), work)))]));
    }, undefined, noBrowser);
    ok(f, exit);
    return f;
  };
  const plain = finalizeExplainer('default-json', ['finalize', 'explainer', 'author-draft.json', 'out.html', '--json'], author, 0);
  expect(JSON.parse(plain.stdout).gates['browser-check'] === 'not-run' && JSON.parse(plain.stdout).browserCheck.requested === false,
    'finalize explainer: browser-check not-run, not requested');
  finalizeExplainer('default-human', ['finalize', 'explainer', 'author-draft.json', 'out.html'], author, 0);
  const skipped = finalizeExplainer('browser-check-skipped-json', ['finalize', 'explainer', 'author-draft.json', 'out.html', '--browser-check', '--json'], author, 2);
  expect(JSON.parse(skipped.stdout).gates['browser-check'] === 'skipped', 'finalize explainer --browser-check without Chromium: skipped');
  const fm = finalizeExplainer('front-matter-json', ['finalize', 'explainer', 'draft.archify.md', 'out.html', '--json'], frontMatter, 1);
  expect(JSON.parse(fm.stdout).diagnostics[0].code === 'finalize/runtime', 'finalize explainer front matter: finalize/runtime (Review Focus 1)');
  finalizeExplainer('writing-strict-json', ['finalize', 'explainer', 'author-draft.json', 'out.html', '--writing', 'strict', '--json'], writingDraft, 1);
  finalizeExplainer('writing-warn-json', ['finalize', 'explainer', 'author-draft.json', 'out.html', '--writing', 'warn', '--json'], writingDraft, 0);

  // With Chromium (Decision 4): only verdicts. A fixture keeps, instead of its output, `verdicts` = {exit, ok, status, each group's
  // status, the ordered diagnostic codes} of the browser receipt (finalize: {exit, gates, browserCheck} and the browser-check sidecar's
  // `browserVerdicts`), and `viewerTargets`: whether upstream's inspectNativeViewer found the Viewer, per observation.
  // UPSTREAM-DEVIATION viewer-target: on this Chromium the sandboxed srcdoc Viewer is an in-process frame with no CDP target, so
  // browser-check.mjs:131-133 never finds it and every step-linked page fails explainer/browser-diagram-focus. The port reads the frame
  // among the page's frames; `verdictsFixed` is upstream's verdict without that defect: no diagram-focus code, interactions failing
  // only for another diagram-* code, ok/status/exit following.
  craft('explainer/typical-draft.json', draft({ schema_version: 1, diagram_type: 'explainer', meta: { id: 'browser-case', title: 'A test explanation', locale: 'en' },
    answer: { summary: 'Read the cache first.', reasons: ['Fewer database queries'], scope: 'Illustrative request path' },
    panels: [{ id: 'why', title: 'Why does it work?', blocks: [{ id: 'claim', type: 'prose', text: 'A cached read takes [5ms](typical:Illustrative timing only, not measured).' }] }],
    sources: [{ id: 'example', title: 'Example' }] }));   // T/explainer-browser.test.mjs:25 formalDocument()
  const GROUPS = ['containment', 'readability', 'viewerChrome', 'themeStates', 'commonQa', 'interactions', 'references', 'noJavaScript', 'print'];
  const verdictsOf = (receipt, exit) => ({ exit, ok: receipt.ok, status: receipt.status, groups: Object.fromEntries(GROUPS.map((g) => [g, receipt[g]?.status ?? null])),
    codes: (receipt.diagnostics ?? []).map((d) => d.code) });
  const targetsOf = (receipt) => (receipt.containment?.viewports ?? []).map((o) => o.nativeViewer ? o.nativeViewer.available : null);
  const fixedOf = (v) => {
    const codes = v.codes.filter((c) => c !== 'explainer/browser-diagram-focus');
    const groups = { ...v.groups, interactions: codes.some((c) => c.startsWith('explainer/browser-diagram-')) ? 'fail' : 'pass' };
    const pass = Object.values(groups).every((s) => s === 'pass') && codes.length === 0;
    return { exit: pass ? 0 : 1, ok: pass, status: pass ? 'pass' : 'fail', groups, codes };
  };
  const withFix = (x, browserVerdicts, targets) => {
    x.viewerTargets = targets;
    if (!browserVerdicts.codes.includes('explainer/browser-diagram-focus')) return;
    expect(targets.length > 0 && targets.every((t) => t === false), `${x.args.join(' ')}: diagram-focus only where upstream found no Viewer target`);
    return fixedOf(browserVerdicts);
  };
  const tls = 'tls-first-visit';
  const caseFiles = { [`${tls}.archify.md`]: X(`${tls}.archify.md`) };
  const renderCase = { run: ['render', 'explainer', `${tls}.archify.md`, 'case.html'], exit: 0 };
  const typicalFiles = { 'typical.json': X('typical-draft.json') };
  const renderTypical = { run: ['render', 'explainer', 'typical.json', 'typical.html'], exit: 0 };
  // T/explainer-browser.test.mjs:49: graph text shrunk below the reading floor
  const smallText = { tamper: 'case.html', from: '</style>', to: '\n.diagram-preview svg{width:100%!important;min-width:0!important}.diagram-preview{width:200px!important}\n</style>' };
  const gate = (name, args, files, setup) => mint('explainer-browser', name, args, files, (work, x) => {
    const receipt = JSON.parse(x.stdout);
    x.verdicts = verdictsOf(receipt, x.exit);
    const fixed = withFix(x, x.verdicts, targetsOf(receipt));
    if (fixed) x.verdictsFixed = fixed;
    expect(receipt.family === 'explainer' && receipt.chrome.executable === '<CHROME>', `${name}: a launched explainer gate`);
    for (const key of ['stdout', 'stderr']) delete x[key];
  }, setup, chromeEnv);
  gate('browser-check-case', ['browser-check', 'case.html', '--json'], caseFiles, [renderCase]);
  gate('visual-check-case', ['visual-check', 'case.html', '--json'], caseFiles, [renderCase]);
  gate('browser-check-typical', ['browser-check', 'typical.html', '--json'], typicalFiles, [renderTypical]);
  gate('browser-check-small-text', ['browser-check', 'case.html', '--json'], caseFiles, [renderCase, smallText]);
  mint('explainer-browser', 'finalize-case', ['finalize', 'explainer', `${tls}.archify.md`, 'out.html', '--browser-check', '--json'], caseFiles, (work, x) => {
    const summary = JSON.parse(x.stdout);
    x.verdicts = { exit: x.exit, gates: summary.gates, browserCheck: summary.browserCheck };
    const receipt = JSON.parse(fs.readFileSync(path.join(work, 'out.browser-check.json'), 'utf8'));
    x.browserVerdicts = verdictsOf(receipt, x.exit);
    const fixed = withFix(x, x.browserVerdicts, targetsOf(receipt));
    if (fixed) {
      x.browserVerdictsFixed = fixed;
      const gates = { ...summary.gates, 'browser-check': fixed.ok ? 'pass' : 'fail' };
      x.verdictsFixed = { exit: Object.values(gates).every((g) => g === 'pass') ? 0 : 1, gates, browserCheck: summary.browserCheck };
    }
    for (const key of ['stdout', 'stderr']) delete x[key];
  }, undefined, chromeEnv);
}
