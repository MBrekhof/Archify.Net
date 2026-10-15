using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class BrandMarkTests
{
    [Fact] public void Catalog_has_107_marks_and_aliases_resolve()
    {
        Assert.Equal(107, BrandMarks.ListBrandMarks().Count);
        Assert.Equal("alibaba-cloud", BrandMarks.FindBrandMark("aliyun")!.Id);
        Assert.Null(BrandMarks.FindBrandMark("no-such-brand"));
    }
    [Fact] public void Remote_brand_is_reported_not_fetched()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/remote-brand.architecture.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/remote-brand.expected.json")))!;
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        var upstreamCodes = expected["diagnostics"]!.AsArray().Select(x => x!["code"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("brand/capture-unavailable", upstreamCodes);
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics));
        Assert.Equal(upstreamCodes, ex.Diagnostics.Select(x => x.Code).ToHashSet());
    }
    [Fact] public void Unknown_and_unpinned_brands_report_upstream_codes()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/brand-aware-delivery.architecture.json")));
        d["components"]![0]!["brand"] = "not-a-real-brand"; d["components"]![1]!["brand"] = "https://example.com/logo.svg";
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics));
        Assert.Equal(new HashSet<string> { "brand/unknown", "brand/unpinned-url" }, ex.Diagnostics.Select(x => x.Code).ToHashSet());
    }

    // Probes below: expected values captured from upstream brand-marks.mjs under node (no network: string brands only).
    [Theory]
    [InlineData("Alibaba Cloud", "alibaba-cloud")] [InlineData("ALIBABA_CLOUD", "alibaba-cloud")] [InlineData("alibaba.cloud", "alibaba-cloud")]
    [InlineData(".net", "dotnet")] [InlineData("fly.io", "fly-io")] [InlineData("flyio", "fly-io")] [InlineData("Fly IO", "fly-io")]
    [InlineData("  Claude  ", "claude")] [InlineData("GOOGLE CLOUD", "google-cloud")]
    [InlineData("https://www.aliyun.com/x", "alibaba-cloud")] [InlineData("https://docs.github.com/en", "github")]
    [InlineData("HTTPS://GITHUB.COM.", "github")] [InlineData("https://example.com", null)] [InlineData("ftp://github.com", null)]
    public void Lookup_forms_and_domain_suffix_match_upstream(string value, string? id) => Assert.Equal(id, BrandMarks.FindBrandMark(value)?.Id);

    // GLOB-001: toLocaleLowerCase('en-US') (brand-marks.mjs:28) maps İ to "i̇", so "GİTHUB" is not github. Node: findBrandMark → null,
    // and suggestions (:575) fall back to localeCompare order.
    [Fact] public void Lookup_forms_use_full_lowercasing()
    {
        Assert.Null(BrandMarks.FindBrandMark("GİTHUB"));
        Assert.Equal(["airtable", "alibaba-cloud", "angular", "ansible", "anthropic"], BrandMarks.Suggestions("GİTHUB"));
    }

    // WHATWG URL edge cases, outcome per upstream prepareDiagramBrandMarks on components[1]: a mark id, or code + message.
    [Theory]
    [InlineData("https:github.com", "github", null, null)]
    [InlineData("https:/github.com", "github", null, null)]
    [InlineData("https://github.com\\foo", "github", null, null)]
    [InlineData("HTTPS:\\\\github.com", "github", null, null)]
    [InlineData(" \thttps://git\thub.com/a\\b?q\\r ", "github", null, null)]
    [InlineData("https://exa\nmple.com/", null, "brand/unpinned-url", "/components/1/brand \"https://exa\\nmple.com/\" is an unpinned URL; capture it first with `archify brands capture https://example.com/ --json`")]
    [InlineData("https:///example.com", null, "brand/unpinned-url", "/components/1/brand \"https:///example.com\" is an unpinned URL; capture it first with `archify brands capture https://example.com/ --json`")]
    // Upstream's message here ends `capture https://example.com/a/b?q\r#f\g --json`; System.Uri's href escapes the query and
    // fragment backslash as %5C, so only the code is pinned (known href ceiling, see AsUrl).
    [InlineData("https://example.com/a\\b?q\\r#f\\g", null, "brand/unpinned-url", null)]
    [InlineData("https://github.com ", null, "brand/unknown", "/components/1/brand \"https://github.com \" is not a built-in brand; closest IDs: github, airtable, alibaba-cloud, angular, ansible")]
    public void Url_brands_follow_whatwg_parsing(string brand, string? markId, string? code, string? message)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/brand-aware-delivery.architecture.json")));
        d["components"]![1]!["brand"] = brand;
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        if (markId is not null)
        {
            ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics);
            Assert.Equal(markId, ctx.Brands.BrandMarkFor(d["components"]![1]!.AsObject())!.Mark.Id);
            return;
        }
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics));
        var actual = Assert.Single(ex.Diagnostics);
        Assert.Equal(code, actual.Code);
        if (message is not null) Assert.Equal(message, actual.Message);
    }

    [Fact] public void Problem_messages_and_shapes_match_upstream()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/brand-aware-delivery.architecture.json")));
        d["components"]![0]!["brand"] = "not-a-real-brand"; d["components"]![1]!["brand"] = "https://example.com/logo.svg"; d["components"]![2]!["brand"] = "git";
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics));
        Assert.Equal("Brand mark validation failed:\n- /components/0/brand \"not-a-real-brand\" is not a built-in brand; closest IDs: airtable, alibaba-cloud, angular, ansible, anthropic\n- /components/1/brand \"https://example.com/logo.svg\" is an unpinned URL; capture it first with `archify brands capture https://example.com/logo.svg --json`\n- /components/2/brand \"git\" is not a built-in brand; closest IDs: digitalocean, github, github-actions, gitlab, airtable", ex.Message);
        Assert.Equal(["brand/unknown", "brand/unpinned-url", "brand/unknown"], ex.Diagnostics.Select(x => x.Code));
        Assert.All(ex.Diagnostics, x => Assert.Equal("{\"diagramType\":\"architecture\",\"collection\":\"components\"}", Js.Stringify(x.Subject)));
        Assert.All(ex.Diagnostics, x => Assert.Empty(x.Evidence));
        Assert.Equal(["run `archify brands capture <url> --json` and author the returned digest-pinned brand object"], ex.Diagnostics[1].SupportedFixes);
        Assert.Equal(["choose an ID from `archify brands`", "run `archify brands capture <url> --json` for an unknown official site"], ex.Diagnostics[0].SupportedFixes);
        Assert.Equal(ex.Diagnostics.Select(x => x.Message), ctx.Diagnostics.Recorded.Select(x => x.Message));
        Assert.Equal("docker", ctx.Brands.BrandMarkFor(d["components"]![3]!.AsObject())!.Mark.Id);   // presets still resolve beside failures
    }

    [Fact] public void Remote_brand_diagnostic_matches_upstream_shape()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/remote-brand.architecture.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/remote-brand.expected.json")))!["diagnostics"]![0]!;
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics));
        var actual = Assert.Single(ex.Diagnostics);
        Assert.Equal("/components/0/brand could not reproduce the pinned capture: remote capture is not available in Archify.Net.Core", actual.Message);
        Assert.Equal(Js.Stringify(expected["subject"]), Js.Stringify(actual.Subject));
        Assert.Equal(Js.Stringify(expected["evidence"]), Js.Stringify(actual.Evidence));
        Assert.Equal(expected["supportedFixes"]!.AsArray().Select(x => x!.GetValue<string>()), actual.SupportedFixes);
    }

    [Fact] public void Resolution_is_keyed_by_the_authored_node()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/brand-aware-delivery.architecture.json")));
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Architecture, d, ctx.Diagnostics);
        var claude = d["components"]![1]!.AsObject();
        Assert.Equal("claude", ctx.Brands.BrandMarkFor(claude)!.Mark.Id);
        Assert.Equal("https://claude.ai", ctx.Brands.BrandMarkFor(claude)!.SourceUrl);
        Assert.Null(ctx.Brands.BrandMarkFor(claude.DeepClone().AsObject()));
        Assert.Null(ctx.Brands.BrandMarkFor(d["components"]![0]!.AsObject()));   // no brand authored
    }

    [Fact] public void Svg_metadata_fit_and_rail_match_upstream()
    {
        var mark = new ResolvedBrand(BrandMarks.FindBrandMark("claude")!, "https://claude.ai");
        static string Svg(string translate, string size, string scale, string path) =>
            $"<g aria-hidden=\"true\" data-brand-mark=\"claude\" data-brand-title=\"Claude\" data-brand-status=\"preset\" data-brand-source=\"https://claude.ai\" class=\"brand-mark\" transform=\"translate({translate})\">\n            <rect width=\"{size}\" height=\"{size}\" rx=\"4\" class=\"brand-mark-badge\"/>\n            <path d=\"{path}\" transform=\"translate(3 3) scale({scale})\" fill=\"#D97757\"/>\n            <rect width=\"{size}\" height=\"{size}\" rx=\"4\" class=\"brand-mark-frame\"/>\n          </g>";
        Assert.Equal(Svg("10.5 6", "16", "0.4166666666666667", Utils.Esc(mark.Mark.Path)), BrandMarks.RenderBrandMark(mark, 10.5, 6));
        Assert.Equal(Svg("0 0", "20", "0.5833333333333334", Utils.Esc(mark.Mark.Path)), BrandMarks.RenderBrandMark(mark, 0, 0, 20));
        Assert.Equal("{\"brand\":\"Claude\",\"brandId\":\"claude\",\"brandStatus\":\"preset\",\"brandSource\":\"https://claude.ai\"}", Js.Stringify(BrandMarks.BrandMetadataFor(mark)));
        Assert.Null(BrandMarks.BrandMetadataFor(null));
        Assert.Equal([72d, 1d, 30d], [BrandMarks.BrandLabelFitWidth(mark, 120), BrandMarks.BrandLabelFitWidth(mark, 30), BrandMarks.BrandLabelFitWidth(null, 30)]);
        Assert.Equal("Component \"claude\" brand top rail leaves 90px for its label, but \"A very long component label here\" needs ~154px at the 8px legible minimum — widen the node or shorten the label.",
            BrandMarks.BrandTopRailProblem(mark, "claude", "A very long component label here", 138, 8, "Component"));
        Assert.Null(BrandMarks.BrandTopRailProblem(mark, "claude", "Claude", 400, 8));
        Assert.Null(BrandMarks.BrandTopRailProblem(null, "claude", "A very long component label here", 138, 8));
    }
}
