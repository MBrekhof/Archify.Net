using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ArtifactCheckerTests
{
    public static IEnumerable<object[]> Goldens() =>
        Directory.GetFiles(Fixtures.Upstream("examples"), "*.html").Select(f => new object[] { f })
        .Concat(Directory.GetFiles(Path.Combine(Fixtures.RepoRoot, "tests", "goldens"), "*.html").Select(f => new object[] { f }));

    [Theory, MemberData(nameof(Goldens))]
    public void Check_report_equals_node_checker(string html)
    {
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Golden($"checker/{Path.GetFileNameWithoutExtension(html)}.json")))!;
        var result = Archify.Check(Fixtures.Lf(html));
        Assert.Equal(expected["ok"]!.GetValue<bool>(), result.Ok);
        var e = Js.Stringify(expected, 2); var a = Js.Stringify(result.Report, 2);
        if (e != a) Assert.Fail(ParityTests.FirstDifference(e, a));
    }

    // Hand-written probes (tests/fixtures/checker/*.html) with upstream's report captured by tools/capture-fixtures.mjs:
    // the goldens are all clean, so these are what reach the issue, diagnostic, route-review and sequence-note paths.
    public static IEnumerable<object[]> Probes() =>
        Directory.GetFiles(Fixtures.Fixture("checker"), "*.html").Select(f => new object[] { Path.GetFileName(f) });

    [Theory, MemberData(nameof(Probes))]
    public void Check_report_equals_node_checker_on_probe(string name)
    {
        var html = Fixtures.Fixture($"checker/{name}");
        var expected = JsonNode.Parse(Fixtures.Lf(Path.ChangeExtension(html, ".expected.json")))!;
        var result = Archify.Check(Fixtures.Lf(html));
        Assert.Equal(expected["ok"]!.GetValue<bool>(), result.Ok);
        var e = Js.Stringify(expected, 2); var a = Js.Stringify(result.Report, 2);
        if (e != a) Assert.Fail(ParityTests.FirstDifference(e, a));
    }

    [Fact]
    public void Provenance_sidecars_are_phase_5()
    {
        Assert.Throws<NotSupportedException>(() => Archify.Check("<svg></svg>", new CheckOptions(RequireProvenance: true)));
    }

    // `node scripts/check-render-output.mjs empty.html` on a zero-byte file: no <svg>, so only single_svg runs and the
    // composition keeps its initial standard-profile shape.
    [Fact]
    public void Empty_input_reports_missing_svg()
    {
        var result = Archify.Check("");
        Assert.False(result.Ok);
        var report = result.Report;
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", report["artifact"]!["sha256"]!.GetValue<string>());
        var checks = report["checks"]!.AsArray();
        Assert.Single(checks);
        Assert.Equal("""{"name":"single_svg","ok":false,"details":["found 0 <svg> block(s)"]}""", Js.Stringify(checks[0]));
        Assert.Equal(["ok", "artifact", "checks", "composition"], report.Select(p => p.Key));
        Assert.Equal(["schemaVersion", "profile", "status", "summary", "metrics", "suggestedLimits", "issues"],
            report["composition"]!.AsObject().Select(p => p.Key));
    }
}
