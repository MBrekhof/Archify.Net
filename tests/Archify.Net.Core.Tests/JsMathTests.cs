using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
namespace Archify.Net.Tests;

// JsMath must equal V8 bit for bit: cases are minted by tools/v8-math/gen.mjs (see tools/v8-math/README.md).
public class JsMathTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static double D(string hex) => BitConverter.UInt64BitsToDouble(ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    static double Call(string fn, double a, double b) => fn switch
    {
        "sin" => JsMath.Sin(a), "cos" => JsMath.Cos(a), "acos" => JsMath.Acos(a), "atan2" => JsMath.Atan2(a, b),
        "log10" => JsMath.Log10(a), "pow" or "pow-sample" => JsMath.Pow(a, b), _ => throw new ArgumentException(fn),
    };

    // Returns (cases of fn, differences as "fn(in) v8=… got=…"). NaN payloads are unobservable in JS, so NaN matches NaN.
    static (int total, List<string> diffs) Compare(string file, string fn)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        int total = 0; var diffs = new List<string>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("fn").GetString() != fn) continue;
            total++;
            var ins = e.GetProperty("in").EnumerateArray().Select(i => i.GetString()!).ToArray();
            var want = D(e.GetProperty("out").GetString()!);
            var got = Call(fn, D(ins[0]), ins.Length > 1 ? D(ins[1]) : 0);
            if (double.IsNaN(want) ? !double.IsNaN(got) : BitConverter.DoubleToInt64Bits(want) != BitConverter.DoubleToInt64Bits(got))
                diffs.Add($"{fn}({string.Join(", ", ins.Select(i => D(i).ToString("R", CultureInfo.InvariantCulture)))}) v8={want:R} got={got:R}");
        }
        return (total, diffs);
    }

    public static TheoryData<string> Fns => new() { "sin", "cos", "acos", "atan2", "log10", "pow" };

    [Theory, MemberData(nameof(Fns))]
    public void Matches_V8_bit_for_bit(string fn)
    {
        var (total, diffs) = Compare(Fixtures.Fixture("js/v8-math.json"), fn);
        Assert.True(total > 2000, $"{fn}: {total} cases");
        Assert.True(diffs.Count == 0, $"{diffs.Count}/{total} differ:\n{string.Join("\n", diffs.Take(20))}");
    }

    // Dev-time 1,000,000-case run: `node tools/v8-math/gen.mjs --full 1000000`, then `dotnet test --filter JsMathFull`.
    // xunit 2 has no dynamic skip: without the git-ignored file this returns early (the plan's one allowed skip).
    // "pow-sample" (random non-integer exponents, outside what Pele reaches) is informational: its count is printed only.
    public static TheoryData<string> FullFns => new() { "sin", "cos", "acos", "atan2", "log10", "pow", "pow-sample" };
    [Theory, MemberData(nameof(FullFns)), Trait("Category", "Dev")]
    public void JsMathFull_matches_V8_bit_for_bit(string fn)
    {
        var file = Path.Combine(Fixtures.RepoRoot, "TestResults", "v8-math-full.json");
        if (!File.Exists(file)) return;
        var (total, diffs) = Compare(file, fn);
        output.WriteLine($"{fn}: {diffs.Count}/{total} differ");
        if (fn == "pow-sample") return;
        Assert.True(diffs.Count == 0, $"{diffs.Count}/{total} differ:\n{string.Join("\n", diffs.Take(20))}");
    }

    // Under Pele/ and Explainer/ every transcendental goes through JsMath (and Math.round through Js.Round).
    [Fact]
    public void Pele_sources_use_JsMath_only()
    {
        var core = Path.Combine(Fixtures.RepoRoot, "src", "Archify.Net.Core");
        var files = new List<string>();
        if (Directory.Exists(Path.Combine(core, "Pele"))) files.AddRange(Directory.GetFiles(Path.Combine(core, "Pele"), "*.cs", SearchOption.AllDirectories));
        if (Directory.Exists(Path.Combine(core, "Explainer"))) files.AddRange(Directory.GetFiles(Path.Combine(core, "Explainer"), "*.cs"));
        // Math, MathF and the double/Double generic-math statics, plus `using static System.Math` (bare Sin(...) calls).
        var banned = new Regex(@"(?<!\w)(Math|MathF|double|Double)\.(Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Exp|Log|Log10|Pow|Round|Truncate)\(|using\s+static\s+System\.MathF?\s*;");
        var hits = files.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(h => banned.IsMatch(h.line)).Select(h => $"{Path.GetRelativePath(core, h.f)}:{h.i + 1}: {h.line.Trim()}").ToList();
        Assert.Empty(hits);
    }

    // Expected values from Node 24: parseInt(s, radix) — "-0" is -0, NaN when no digits.
    [Theory]
    [InlineData("08", 10, "8")]
    [InlineData(" 42px", 10, "42")]
    [InlineData("-0", 10, "-0")]
    [InlineData("0x1f", 16, "31")]
    [InlineData("0x1f", 0, "31")]
    [InlineData("0x1f", 10, "0")]
    [InlineData("", 10, "NaN")]
    [InlineData("abc", 10, "NaN")]
    [InlineData("1e3", 10, "1")]
    [InlineData("  +7.9", 10, "7")]
    [InlineData(" \n-12abc", 10, "-12")]
    [InlineData("ff", 16, "255")]
    [InlineData("12", 1, "NaN")]
    [InlineData("12", 37, "NaN")]
    [InlineData("-", 10, "NaN")]
    [InlineData("0x", 16, "NaN")]
    public void ParseInt_matches_V8(string s, int radix, string want)
    {
        var got = Js.ParseInt(s, radix);
        Assert.Equal(want, got == 0 && double.IsNegative(got) ? "-0" : Js.Num(got));
    }
}
