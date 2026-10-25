# tools/v8-math

`gen.mjs` captures V8's `Math.sin`, `Math.cos`, `Math.acos`, `Math.atan2`, `Math.log10` and `**` as IEEE-754 bit
patterns, so that `JsMathTests` can check `JsMath` (src/Archify.Net.Core/Js/JsMath.cs) bit for bit.

- `node tools/v8-math/gen.mjs --fixture` writes `tests/fixtures/js/v8-math.json`, which is committed (minted on Windows with Node 24.12.0). It holds chart
  angles, fdlibm branch edges, special values and a seeded mulberry32 sample, one `{fn, in: [hex…], out: hex}` per
  line. NaN outputs are written as `7ff8000000000000`, and the test accepts any NaN for them, since JS cannot observe
  NaN payloads.
- `node tools/v8-math/gen.mjs --full 1000000` writes `TestResults/v8-math-full.json` (git-ignored, about 70 MB) with a
  different seed. Run it with `dotnet test tests/Archify.Net.Core.Tests --filter JsMathFull`.
- `node tools/v8-math/gen.mjs --cs-table` prints the body of `JsMath.Pow10`: Windows Node's `10 ** p` for every
  integer p in [−330, 310]. To regenerate it, run this command on Windows Node 24.12.0 and paste the output over the
  array in JsMath.cs.

`JsMathFull_matches_V8_bit_for_bit` is `[Trait("Category","Dev")]`. xunit 2.9.3 has no dynamic skip, so when
`TestResults/v8-math-full.json` is absent the test returns early and reports as passed. This is the one allowed skip
in the PORT-008 plan.

## Pow

V8 13.6 sends `Math.pow` and `**` to the C library's `std::pow` (flag `--use-std-math-pow`, on by default), after a
few JS special cases. So Node's own results differ between Windows, which uses the UCRT, and Linux, which uses glibc:
for example `10 ** 210`. The port matches Windows Node, where the goldens are minted, on every power Pele reaches:

- `10 ** p` comes from the table.
- `0.99 ** k`, `0.5 ** k`, `2 ** k` and `x ** 2` (which V8 turns into `x * x`) match through V8's special cases and
  `Math.Pow`.

The exact pow rows in the fixture cover only that domain, plus the special cases: ±0, ±∞, NaN, and the grid of
negative bases with integer exponents. Random bases and exponents go into the `--full` run as `pow-sample`. Their
difference count is printed and never fails the test.

Inputs are never built with `**` or `Math.exp`, so the inputs are the same on Windows and Linux. Powers of two are
built from bits, and decimal magnitudes are parsed. Two runs write identical bytes.
