#!/usr/bin/env node
// Dev-time only. Captures V8's Math.sin/cos/acos/atan2/log10 and `**` as IEEE-754 bit patterns for JsMathTests.
// Usage (from the repo root):
//   node tools/v8-math/gen.mjs --fixture        writes tests/fixtures/js/v8-math.json (committed)
//   node tools/v8-math/gen.mjs --full 1000000   writes TestResults/v8-math-full.json (git-ignored, dev run only)
// Deterministic: every random input comes from a seeded mulberry32; two runs write identical bytes.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(fileURLToPath(import.meta.url), '..', '..', '..');
const mulberry32 = (seed) => () => {
  seed = (seed + 0x6d2b79f5) | 0;
  let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const dv = new DataView(new ArrayBuffer(8));
const bitsOf = (x) => { dv.setFloat64(0, x); return dv.getBigUint64(0); };
const fromBits = (b) => { dv.setBigUint64(0, BigInt.asUintN(64, b)); return dv.getFloat64(0); };
const hex = (x) => (Number.isNaN(x) ? 0x7ff8000000000000n : bitsOf(x)).toString(16).padStart(16, '0'); // NaN payloads are unobservable in JS
// Neighbours by ulp steps (positive steps move away from zero in magnitude for x > 0).
const ulps = (x, k) => fromBits(bitsOf(x) + BigInt(k));
const around = (x, n = 2) => { const r = [x]; for (let k = 1; k <= n; k++) r.push(ulps(x, k), ulps(x, -k)); return r.filter((v) => Number.isFinite(v)); };
const word = (hi, lo = 0) => fromBits((BigInt(hi >>> 0) << 32n) | BigInt(lo >>> 0));
const signed = (xs) => xs.flatMap((x) => [x, -x]);
// Inputs never come from `**` or Math.exp: V8's `**` is the host C library's pow, so inputs built with it would differ
// between Windows and Linux. Powers of two are built from bits; decimal magnitudes are parsed.
const p2 = (k) => (k >= -1022 ? fromBits(BigInt(k + 1023) << 52n) : fromBits(1n << BigInt(k + 1074)));
const e10 = (p) => Number(`1e${p}`);
const SPECIAL = [0, -0, Infinity, -Infinity, NaN];

const FNS = {
  sin: (a) => Math.sin(a), cos: (a) => Math.cos(a), acos: (a) => Math.acos(a), atan2: (y, x) => Math.atan2(y, x),
  log10: (a) => Math.log10(a), pow: (x, y) => x ** y,
};
// --full only: pow over random non-integer exponents, outside what Pele reaches; JsMathFull reports it, never fails on it.
const SAMPLE = { 'pow-sample': (x, y) => x ** y };

function cases(fn, rnd, randomCount) {
  const one = [], two = [];
  const anyExp = () => (rnd() < 0.5 ? -1 : 1) * (1 + rnd()) * p2(Math.floor(rnd() * 2098 - 1074));
  switch (fn) {
    case 'sin': case 'cos': {
      for (let n = 1; n <= 64; n++) for (let i = 0; i < n; i++) one.push((2 * Math.PI * i) / n - Math.PI / 2, (i * Math.PI * 2) / n, (2 * Math.PI * i) / n);
      for (let k = -16; k <= 16; k++) one.push(...around((k * Math.PI) / 4), ...around(k * (Math.PI / 2)));
      one.push(Math.PI / 6, Math.PI / 3, Math.PI, 2 * Math.PI, ...SPECIAL, 1e5, 1e10, 1e22, 1e300, Number.MAX_VALUE, 5e-324);
      one.push(...signed([p2(-27), ulps(p2(-27), -1), ulps(p2(-27), 1), p2(-28), p2(-60), 0.3, ulps(0.3, -1), 0.78125, ulps(0.78125, 1)]));
      // __ieee754_rem_pio2 branch edges: |x| ~ pi/4, 3pi/4, the pi/2 cancellation word, 2^19*(pi/2), and k*2^20*pi/2.
      for (const hi of [0x3FE921FB, 0x3FE921FC, 0x4002D97B, 0x4002D97C, 0x3FF921FB, 0x413921FB, 0x413921FC]) one.push(...signed(around(word(hi, 0)).concat(around(word(hi, 0xFFFFFFFF)))));
      for (let k = 1; k <= 64; k++) one.push(...signed(around(k * p2(20) * (Math.PI / 2), 3)));
      for (let n = 1; n <= 40; n++) one.push(...signed(around(n * (Math.PI / 2), 3)));
      for (let i = 0; i < randomCount; i++) one.push((rnd() * 2 - 1) * 4 * Math.PI);
      for (let i = 0; i < randomCount / 4; i++) one.push(anyExp());
      for (let i = 0; i < randomCount / 8; i++) one.push((rnd() * 2 - 1) * (1 + rnd()) * e10(Math.floor(rnd() * 12)));
      break;
    }
    case 'acos': {
      one.push(...SPECIAL, 1, -1, 0.5, -0.5, ...signed(around(0.5, 3)), ...signed(around(1, 3)), 2, -2, 1e300);
      for (let k = 1; k <= 64; k++) one.push(1 - k * p2(-53), -1 + k * p2(-53));
      one.push(...signed([p2(-57), ulps(p2(-57), -1), ulps(p2(-57), 1), p2(-58), p2(-56), word(0x3C600000, 1), 5e-324, p2(-27)]));
      for (let i = 0; i < randomCount; i++) one.push(rnd() * 2 - 1);
      for (let i = 0; i < randomCount / 8; i++) one.push((rnd() < 0.5 ? -1 : 1) * (1 + rnd()) * p2(-1 - Math.floor(rnd() * 70)));
      for (let i = 0; i < randomCount / 8; i++) one.push((rnd() < 0.5 ? -1 : 1) * (1 - (1 + rnd()) * p2(-2 - Math.floor(rnd() * 52))));
      break;
    }
    case 'atan2': {
      const vals = [...SPECIAL, 1, -1, 0.5, -2, 5e-324, -5e-324, Number.MAX_VALUE, -Number.MAX_VALUE, 3, -7.25];
      for (const y of vals) for (const x of vals) two.push([y, x]);
      for (const x of [1, ulps(1, 1), ulps(1, -1)]) for (const y of [...SPECIAL, 0.3, -0.3, 1e300, 2.5]) two.push([y, x]);
      // |y/x| > 2^60 and < 2^-60, every sign; atan's own branch edges via y/x ratios.
      for (const k of [59, 60, 61, 62, 70, 1000]) for (const sy of [1, -1]) for (const sx of [1, -1]) {
        two.push([sy * p2(k) * 1.5, sx * 1.5], [sy * 1.5, sx * p2(k) * 1.5], [sy * 3, sx * p2(-k)], [sy * p2(-k), sx * 3]);
      }
      for (const r of [7 / 16, 11 / 16, 19 / 16, 39 / 16, p2(66), p2(-27), 0.4375]) for (const v of around(r, 2)) for (const sy of [1, -1]) for (const sx of [1, -1]) two.push([sy * v, sx * 1]);
      for (const r of [7 / 16, 11 / 16, 19 / 16, 39 / 16, p2(66), p2(-27)]) for (const sy of [1, -1]) for (const sx of [1, -1]) two.push([sy * r * 3, sx * 3]);
      for (let i = 0; i < randomCount; i++) two.push([(rnd() * 2 - 1) * 500, (rnd() * 2 - 1) * 500]);
      for (let i = 0; i < randomCount / 4; i++) two.push([anyExp(), anyExp()]);
      break;
    }
    case 'log10': {
      one.push(...SPECIAL, -1, -1e-300, 1, ulps(1, 1), ulps(1, -1), ulps(1, 2), ulps(1, -2), 2, 0.5, Number.MAX_VALUE);
      for (let p = -323; p <= 308; p++) one.push(...around(Number(`1e${p}`), 1));
      for (let p = -12; p <= 12; p++) for (const f of [2, 2.5, 5]) one.push(f * e10(p));
      for (let e = -1074; e <= 1023; e += 7) one.push(...around(p2(e), 1));
      for (let i = 0; i < 64; i++) one.push(fromBits(BigInt(Math.floor(rnd() * 4503599627370496))) || 5e-324); // subnormals
      for (let i = 0; i < 64; i++) one.push(1 + (rnd() * 2 - 1) * p2(-20), p2(Math.floor(rnd() * 40 - 20)) * (1 + (rnd() * 2 - 1) * p2(-21)));
      for (let i = 0; i < randomCount; i++) one.push((1 + rnd() * 9) * e10(Math.floor(rnd() * 24) - 12)); // (0, 1e12)
      for (let i = 0; i < randomCount / 4; i++) one.push(rnd() * 1e12);
      for (let i = 0; i < randomCount / 4; i++) one.push(Math.abs(anyExp()));
      break;
    }
    case 'pow': {
      // The domain Pele 1.3.0 reaches: 10 ** p (xychart/gantt ticks), 0.99 ** pass (sankey), 0.5 ** depth (cluster),
      // 2 ** 25/26 (architecture), x ** 2; plus V8's special cases, including negative bases with integer exponents.
      for (let p = -330; p <= 310; p++) two.push([10, p]);
      for (let k = 0; k <= 64; k++) two.push([0.99, k], [0.5, k], [2, k], [2, -k]);
      const xs = [...SPECIAL, 1, -1, 0.5, -0.5, 2, -2, 10, -3, 0.99, 1e300, -1e300, 5e-324, -5e-324, Number.MAX_VALUE];
      const ys = [...SPECIAL, 1, -1, 2, -2, 0.5, 3, -3, 64, 1e300, -1e300, p2(53), p2(53) + 2, 1075, -1075];
      for (const x of xs) for (const y of ys) two.push([x, y]);
      for (let i = 0; i < randomCount; i++) two.push([(rnd() * 2 - 1) * 1000, 2]);
      break;
    }
    case 'pow-sample': {
      // Random bases Pele never uses: std::pow differs between C libraries here (see README).
      for (let i = 0; i < randomCount / 4; i++) two.push([-(rnd() * 50), Math.floor(rnd() * 61) - 30]);
      for (let i = 0; i < randomCount; i++) two.push([rnd() * 100, (rnd() * 2 - 1) * 40]);
      for (let i = 0; i < randomCount / 4; i++) two.push([10, Math.floor(rnd() * 641) - 330 + rnd()]);
      for (let i = 0; i < randomCount / 4; i++) two.push([Math.abs(anyExp()), (rnd() * 2 - 1) * (1 + rnd()) * p2(Math.floor(rnd() * 12))]);
      break;
    }
  }
  return one.length ? one.map((a) => [a]) : two;
}

function assertReach(fn, list) {
  const reach = {
    sin: [(a) => Math.abs(a) > 0 && Math.abs(a) < p2(-27), (a) => Math.abs(a) >= p2(20) * (Math.PI / 2), (a) => Math.abs(a) > 0 && Math.abs(a) <= Math.PI / 4],
    cos: [(a) => Math.abs(a) > 0 && Math.abs(a) < p2(-27), (a) => Math.abs(a) >= p2(20) * (Math.PI / 2), (a) => Math.abs(a) > 0 && Math.abs(a) <= Math.PI / 4],
    acos: [(a) => Math.abs(a) > 0 && Math.abs(a) <= p2(-57), (a) => Math.abs(a) > 0.5 && Math.abs(a) < 1, (a) => Math.abs(a) > 1],
    atan2: [(y, x) => Math.abs(y / x) > p2(60) && Number.isFinite(y / x), (y, x) => Math.abs(y / x) > 0 && Math.abs(y / x) < p2(-60), (y, x) => x < 0 && y < 0],
    log10: [(a) => a > 0 && a < p2(-1022), (a) => Math.abs(a - 1) > 0 && Math.abs(a - 1) < p2(-20), (a) => a < 0],
    pow: [(x, y) => x === 10 && y >= -330 && y <= 310, (x, y) => x < 0 && Number.isInteger(y), (x, y) => Math.abs(x) === 1 && !Number.isFinite(y)],
  }[fn] ?? [];
  for (let c = 0; c < reach.length; c++) {
    if (!list.some((args) => reach[c](...args))) throw new Error(`${fn}: branch class ${c} not reached`);
  }
}

// total: when given, exactly that many cases split evenly over the functions (the --full run), shuffled before slicing.
function emit(fns, randomCount, seed, total) {
  const rows = [];
  Object.entries(fns).forEach(([fn, f], i, all) => {
    const rnd = mulberry32(seed + fn.length * 7919 + fn.charCodeAt(0));
    const seen = new Set();
    let list = cases(fn, rnd, randomCount).filter((args) => { const key = args.map(hex).join(','); return seen.has(key) ? false : (seen.add(key), true); });
    if (total !== undefined) {
      for (let k = list.length - 1; k > 0; k--) { const j = Math.floor(rnd() * (k + 1)); [list[k], list[j]] = [list[j], list[k]]; }
      list = list.slice(0, Math.floor(total / all.length) + (i < total % all.length ? 1 : 0));
    }
    assertReach(fn, list);
    for (const args of list) rows.push(JSON.stringify({ fn, in: args.map(hex), out: hex(f(...args)) }));
  });
  return { text: '[\n' + rows.join(',\n') + '\n]\n', count: rows.length };
}

const mode = process.argv[2];
if (mode === '--fixture') {
  const { text, count } = emit(FNS, 4000, 20261023);
  const file = path.join(ROOT, 'tests', 'fixtures', 'js', 'v8-math.json');
  fs.writeFileSync(file, text);
  console.log(`${count} cases -> ${file}`);
} else if (mode === '--full') {
  const n = Number(process.argv[3] ?? 1000000);
  // Exactly n cases over the seven functions (six plus pow-sample), a different seed from the fixture.
  const { text, count } = emit({ ...FNS, ...SAMPLE }, Math.ceil(n / 7), 777, n);
  const file = path.join(ROOT, 'TestResults', 'v8-math-full.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, text);
  console.log(`${count} cases -> ${file}`);
} else if (mode === '--cs-table') {
  // The C# body of JsMath.Pow10: Windows V8's 10 ** p for p = -330..310, as bit patterns.
  const lines = [];
  for (let p = -330; p <= 310; p += 6) lines.push('        ' + Array.from({ length: Math.min(6, 311 - p) }, (_, i) => `0x${hex(10 ** (p + i)).toUpperCase()}UL,`).join(' '));
  console.log(lines.join('\n'));
} else {
  console.error('usage: node tools/v8-math/gen.mjs --fixture | --full [N] | --cs-table');
  process.exit(2);
}
