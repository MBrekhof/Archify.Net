using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ParityTests
{
    // Oracle list = UP/scripts/render-examples.mjs TARGETS (upstream goldens live in upstream/examples)
    // + goldens we minted in tests/goldens. Phase 1 covers architecture; later phases add rows.
    public static IEnumerable<object[]> Targets() =>
    [
        ["architecture", Fixtures.Upstream("examples/web-app.architecture.json"), Fixtures.Upstream("examples/web-app-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/production-deployment.architecture.json"), Fixtures.Golden("production-deployment-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/brand-aware-delivery.architecture.json"), Fixtures.Golden("brand-aware-delivery-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/checkout-platform.base.architecture.json"), Fixtures.Golden("checkout-platform.base-rendered.html")],
        ["architecture", Fixtures.Upstream("examples/checkout-platform.head.architecture.json"), Fixtures.Golden("checkout-platform.head-rendered.html")],
        ["architecture", Fixtures.Fixture("diagrams/web-app.no-profile.architecture.json"), Fixtures.Golden("web-app.no-profile-rendered.html")],
    ];

    [Theory, MemberData(nameof(Targets))]
    public void Render_is_byte_identical_to_upstream(string type, string input, string golden)
    {
        var diagram = DiagramLoader.Parse(Fixtures.Lf(input));
        var result = Archify.Render(DiagramTypes.Parse(type), diagram);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Severity.Error);
        var expected = Fixtures.Lf(golden);
        if (expected != result.Html) Assert.Fail(FirstDifference(expected, result.Html));
        Assert.Equal(Fixtures.Bytes(golden), new System.Text.UTF8Encoding(false).GetBytes(result.Html));   // BOM/encoding guard
    }

    [Fact] public void No_profile_fixture_is_advisory()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.no-profile.architecture.json")));
        var r = Archify.Render(DiagramType.Architecture, d);
        Assert.Contains("data-quality-profile=\"standard\" data-quality-gates=\"advisory\"", r.Svg);
        var showcase = Archify.Render(DiagramType.Architecture, d, new RenderOptions(Quality: QualityProfile.Showcase));
        Assert.Contains("data-quality-profile=\"showcase\"", showcase.Svg);
        Assert.DoesNotContain("advisory", showcase.Svg);
        // Option-level showcase must NOT turn on the meta-only layout gates: the no-profile diagram still renders. Verified
        // 2026-10-14 against node: `ARCHIFY_QUALITY_PROFILE=showcase node renderers/architecture/render-architecture.mjs
        // tests/fixtures/diagrams/web-app.no-profile.architecture.json out.html` exits 0 (task-9-report.md); that output is
        // the fixture below, so the option reproduces the env byte for byte.
        Assert.DoesNotContain(showcase.Diagnostics, x => x.Severity == Severity.Error);
        var expected = Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.no-profile.showcase-rendered.html"));
        if (expected != showcase.Html) Assert.Fail(FirstDifference(expected, showcase.Html));
    }

    // No upstream example uses layout.mode "grid" for architecture; this fixture (rendered by node 2026-10-14, see
    // task-9-report.md) covers Grid.ResolveComponentPos, the grid validation branch and the grid layout report.
    [Fact] public void Grid_layout_is_byte_identical_to_upstream()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/grid-layout.architecture.json")));
        var r = Archify.Render(DiagramType.Architecture, d);
        Assert.DoesNotContain(r.Diagnostics, x => x.Severity == Severity.Error);
        var expected = Fixtures.Lf(Fixtures.Fixture("diagrams/grid-layout-rendered.html"));
        if (expected != r.Html) Assert.Fail(FirstDifference(expected, r.Html));
        Assert.Equal("grid", r.Layout!.Report["layout"]!["mode"]!.GetValue<string>());
        Assert.Equal(3d, r.Layout.Report["layout"]!["cols"]!.D());
    }

    [Fact] public void Bad_input_returns_diagnostics_not_exceptions()
    {
        var d = DiagramLoader.Parse("{\"schema_version\":1,\"diagram_type\":\"architecture\",\"meta\":{\"title\":\"x\",\"output\":\"x.html\"},\"components\":[]}");
        var r = Archify.Render(DiagramType.Architecture, d);
        Assert.Equal("", r.Html);
        Assert.NotEmpty(r.Diagnostics);
    }

    // The goldens only exercise clean renders; this receipt (ARCHIFY_DIAGNOSTIC_FORMAT=json stderr of upstream on a diagram
    // with 16 injected layout faults) pins validateArchitecture's failure path: codes, messages, subjects, evidence, fixes, order.
    [Fact] public void Invalid_diagram_diagnostics_match_upstream_receipt()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.invalid.architecture.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.invalid.expected.json")))!;
        var r = Archify.Render(DiagramType.Architecture, d);
        Assert.Equal("", r.Html);
        var upstream = expected["diagnostics"]!.AsArray();
        Assert.Equal(upstream.Select(x => x!["code"]!.GetValue<string>()), r.Diagnostics.Select(x => x.Code));
        for (var i = 0; i < upstream.Count; i++)
        {
            var (e, a) = (upstream[i]!, r.Diagnostics[i]);
            Assert.Equal(e["message"]!.GetValue<string>(), a.Message);
            Assert.Equal(e["severity"]!.GetValue<string>(), a.Severity == Severity.Error ? "error" : "warning");
            Assert.Equal(Js.Stringify(e["subject"]), Js.Stringify(a.Subject));
            Assert.Equal(Js.Stringify(e["evidence"]), Js.Stringify(a.Evidence));
            Assert.Equal(e["supportedFixes"]!.AsArray().Select(x => x!.GetValue<string>()), a.SupportedFixes);
        }
    }

    [Fact] public void Non_object_root_returns_json_parse_diagnostic()
    {
        var r = Archify.Render(DiagramType.Architecture, new JsonArray());
        Assert.Equal("", r.Html);
        Assert.Equal(["input/json-parse"], r.Diagnostics.Select(x => x.Code));
    }

    internal static string FirstDifference(string expected, string actual)
    {
        var el = expected.Split('\n'); var al = actual.Split('\n');
        for (var i = 0; i < Math.Max(el.Length, al.Length); i++)
        {
            var e = i < el.Length ? el[i] : "<EOF>"; var a = i < al.Length ? al[i] : "<EOF>";
            if (e == a) continue;
            var ctx = string.Join('\n', Enumerable.Range(Math.Max(0, i - 3), Math.Min(7, el.Length - Math.Max(0, i - 3))).Select(j => $"{j + 1,6}: {el[j]}"));
            return $"First difference at line {i + 1}\nexpected: {e}\nactual:   {a}\n--- expected context ---\n{ctx}";
        }
        return "lengths differ only in trailing content";
    }
}
