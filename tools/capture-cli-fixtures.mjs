#!/usr/bin/env node
// Dev-time only. Mints the expected outputs of upstream's `archify` CLI (bin/archify.mjs) for the C# CLI suite.
// Usage: node tools/capture-cli-fixtures.mjs   (from repo root; needs C:\projects\archify at c21c31ff)
//
// Each case runs `node <SKILL>/bin/archify.mjs …args` in a fresh temp workdir seeded with `files` and writes
// tests/fixtures/cli/<group>/<name>.json = {args, files, exit, stdout, stderr}. `files` maps a workdir-relative name to the
// repo-relative file it was copied from; a C# test rebuilds the same workdir, runs the same args and tokenises its output
// the same way: the workdir becomes <WORK> and the skill root <SKILL>, both raw and JSON-escaped (`\` doubled).
// Also mints upstream/recipes/scenarios.json from recipes/scenarios.mjs SCENARIO_RECIPES.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

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

const escaped = (p) => p.replaceAll('\\', '\\\\');
function tokenise(text, work) {
  let t = text;
  for (const [p, token] of [[work, '<WORK>'], [SKILL, '<SKILL>']]) t = t.replaceAll(escaped(p), token).replaceAll(p, token);
  // Determinism guard: any temp path left over (an owned temp dir, a pid-named layout file) would make the fixture vary.
  if (t.includes(tmpRoot) || t.includes(escaped(tmpRoot)) || t.includes(path.basename(tmpRoot))) {
    throw new Error(`untokenised temp path in output:\n${t}`);
  }
  return t;
}

fs.rmSync(repo(CLI_FIX), { recursive: true, force: true });
const minted = new Set();
// `inspect(work, fixture)` runs before the workdir is removed (output-byte assertions).
function mint(group, name, args, files = {}, inspect = () => {}) {
  const rel = `${CLI_FIX}/${group}/${name}.json`;
  if (minted.has(rel)) throw new Error(`duplicate fixture ${rel}`);
  const work = fs.mkdtempSync(path.join(tmpRoot, 'w-'));
  try {
    for (const [dest, src] of Object.entries(files)) {
      fs.mkdirSync(path.dirname(path.join(work, dest)), { recursive: true });
      fs.copyFileSync(repo(src), path.join(work, dest));
    }
    const r = spawnSync(process.execPath, [cli, ...args], { cwd: work, env, encoding: 'utf8' });
    if (r.error) throw r.error;
    const fixture = { args, files, exit: r.status, stdout: tokenise(r.stdout, work), stderr: tokenise(r.stderr, work) };
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
// A UTF-8 BOM: Node's JSON.parse does not strip it (survey-cli §2.2 step 3).
craft('bom.architecture.json', '\ufeff' + fs.readFileSync(repo(`${EX}/web-app.architecture.json`), 'utf8'));

// --- help ---
const IN_COMMANDS = ['render', 'deliver', 'finalize', 'validate', 'inspect', 'check', 'browser-check', 'visual-check', 'guide',
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
const EXAMPLES = fs.readdirSync(repo(EX)).filter((f) => f.endsWith('.json')).sort();
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

console.log(`minted ${minted.size} CLI fixtures into ${CLI_FIX}, three path tables, and upstream/recipes/scenarios.json`);
