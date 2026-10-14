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
        ["sequence", Fixtures.Upstream("examples/cache-miss-request.sequence.json"), Fixtures.Upstream("examples/sequence-cache-miss-request.html")],
        ["sequence", Fixtures.Upstream("examples/async-job-roundtrip.sequence.json"), Fixtures.Golden("async-job-roundtrip-rendered.html")],
        ["sequence", Fixtures.Fixture("diagrams/note-wrap.sequence.json"), Fixtures.Golden("note-wrap-rendered.html")],
        ["sequence", Fixtures.Fixture("diagrams/brand-participants.sequence.json"), Fixtures.Golden("brand-participants-rendered.html")],
        ["lifecycle", Fixtures.Upstream("examples/agent-run.lifecycle.json"), Fixtures.Upstream("examples/lifecycle-agent-run.html")],
        ["lifecycle", Fixtures.Upstream("examples/deployment-release.lifecycle.json"), Fixtures.Golden("deployment-release-rendered.html")],
        ["lifecycle", Fixtures.Fixture("diagrams/deployment-release.notes.lifecycle.json"), Fixtures.Golden("deployment-release.notes-rendered.html")],
        ["class", Fixtures.Upstream("examples/payments.class.json"), Fixtures.Upstream("examples/class-payments-rendered.html")],
        ["class", Fixtures.Upstream("examples/payment-processors.class.json"), Fixtures.Upstream("examples/class-payment-processors-rendered.html")],
        ["tree", Fixtures.Upstream("examples/payment-platform.tree.json"), Fixtures.Upstream("examples/tree-payment-platform-rendered.html")],
        ["tree", Fixtures.Upstream("examples/archify-repository.tree.json"), Fixtures.Upstream("examples/tree-archify-repository-rendered.html")],
        ["tree", Fixtures.Fixture("diagrams/payment-platform.right.tree.json"), Fixtures.Golden("payment-platform.right-rendered.html")],
        ["tree", Fixtures.Fixture("diagrams/payment-platform.wrap.tree.json"), Fixtures.Golden("payment-platform.wrap-rendered.html")],   // CJK/Hangul, spaces, in-word cut
    ];

    [Fact] public void Tree_missing_parent_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-platform.tree.json")));
        d["nodes"]![1]!["parent"] = "ghost";
        var r = ArchifyNet.Render(DiagramType.Tree, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "tree/missing-parent" && x.Path == "/nodes/1/parent");
    }

    // render-tree.mjs:29 `tree.layout?.direction === 'right'` reaches the layout report (:395) unchanged.
    [Fact] public void Tree_right_direction_reaches_layout_report()
    {
        var r = ArchifyNet.Render(DiagramType.Tree, DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/payment-platform.right.tree.json"))));
        Assert.DoesNotContain(r.Diagnostics, x => x.Severity == Severity.Error);
        Assert.Equal("right", r.Layout!.Report["direction"]!.GetValue<string>());
    }

    // render-class.mjs:623-650 generalizationCycle walks `inheritance` edges only, after the per-relationship kind checks;
    // a reversed class-to-class inheritance stays on one side of the interface boundary, so the cycle is the code reported.
    [Fact] public void Class_inheritance_cycle_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-processors.class.json")));
        var rel = d["relationships"]!.AsArray().First(x => x!.Str("kind") == "inheritance")!.AsObject();
        d["relationships"]!.AsArray().Add(new JsonObject { ["from"] = rel["to"]!.DeepClone(), ["to"] = rel["from"]!.DeepClone(), ["kind"] = "inheritance" });
        var r = ArchifyNet.Render(DiagramType.Class, DiagramLoader.Normalize(d));
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "class/inheritance-cycle");
    }

    [Fact] public void Lifecycle_main_path_gap_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/agent-run.lifecycle.json")));
        var t = d["transitions"]!.AsArray(); var gap = t.First(x => x!.Str("from") == "queued" && x.Str("to") == "planning"); t.Remove(gap);
        var r = ArchifyNet.Render(DiagramType.Lifecycle, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "lifecycle/main-path-gap" && x.Path == "/mainPath/0");
    }

    [Fact] public void Sequence_unknown_endpoint_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/cache-miss-request.sequence.json")));
        d["messages"]![0]!["to"] = "nobody";
        var r = ArchifyNet.Render(DiagramType.Sequence, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "sequence/unknown-endpoint" && x.Path == "/messages/0/to");
    }

    [Theory, MemberData(nameof(Targets))]
    public void Render_is_byte_identical_to_upstream(string type, string input, string golden)
    {
        var diagram = DiagramLoader.Parse(Fixtures.Lf(input));
        var result = ArchifyNet.Render(DiagramTypes.Parse(type), diagram);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Severity.Error);
        var expected = Fixtures.Lf(golden);
        if (expected != result.Html) Assert.Fail(FirstDifference(expected, result.Html));
        Assert.Equal(Fixtures.Bytes(golden), new System.Text.UTF8Encoding(false).GetBytes(result.Html));   // BOM/encoding guard
    }

    [Fact] public void No_profile_fixture_is_advisory()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.no-profile.architecture.json")));
        var r = ArchifyNet.Render(DiagramType.Architecture, d);
        Assert.Contains("data-quality-profile=\"standard\" data-quality-gates=\"advisory\"", r.Svg);
        var showcase = ArchifyNet.Render(DiagramType.Architecture, d, new RenderOptions(Quality: QualityProfile.Showcase));
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
        var r = ArchifyNet.Render(DiagramType.Architecture, d);
        Assert.DoesNotContain(r.Diagnostics, x => x.Severity == Severity.Error);
        var expected = Fixtures.Lf(Fixtures.Fixture("diagrams/grid-layout-rendered.html"));
        if (expected != r.Html) Assert.Fail(FirstDifference(expected, r.Html));
        Assert.Equal("grid", r.Layout!.Report["layout"]!["mode"]!.GetValue<string>());
        Assert.Equal(3d, r.Layout.Report["layout"]!["cols"]!.D());
    }

    [Fact] public void Bad_input_returns_diagnostics_not_exceptions()
    {
        var d = DiagramLoader.Parse("{\"schema_version\":1,\"diagram_type\":\"architecture\",\"meta\":{\"title\":\"x\",\"output\":\"x.html\"},\"components\":[]}");
        var r = ArchifyNet.Render(DiagramType.Architecture, d);
        Assert.Equal("", r.Html);
        Assert.NotEmpty(r.Diagnostics);
    }

    // The goldens only exercise clean renders; this receipt (ARCHIFY_DIAGNOSTIC_FORMAT=json stderr of upstream on a diagram
    // with 16 injected layout faults) pins validateArchitecture's failure path: codes, messages, subjects, evidence, fixes, order.
    [Fact] public void Invalid_diagram_diagnostics_match_upstream_receipt()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.invalid.architecture.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/web-app.invalid.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Architecture, d);
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

    // Sequence failure receipts (ARCHIFY_DIAGNOSTIC_FORMAT=json stderr of upstream, captured by tools/capture-fixtures.mjs
    // from the cache-miss example with one fault each): validateSequence's codes, messages, subjects, evidence, fixes, order.
    [Theory]
    [InlineData("label-overlap")]
    [InlineData("self-message")]
    [InlineData("note-overlap")]
    [InlineData("note-canvas-limit")]
    [InlineData("segment-border-run")]
    [InlineData("segment-label-wide")]
    [InlineData("legend-room")]
    [InlineData("participant-overflow")]
    public void Sequence_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.sequence.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.sequence.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Sequence, d);
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

    // Lifecycle failure receipts (same capture, from the agent-run example with one fault each): validateStructure's and
    // validateLayout's codes, messages, subjects, evidence, fixes, order — every lifecycle/* code the renderer can emit.
    [Theory]
    [InlineData("unknown-main-path-state")]
    [InlineData("unknown-endpoint")]
    [InlineData("duplicate-state")]
    [InlineData("repeated-main-path-state")]
    [InlineData("main-path-gap")]
    [InlineData("state-text-too-long")]
    [InlineData("crowded-side-transitions")]
    [InlineData("too-wide")]
    [InlineData("label-unplaced")]
    public void Lifecycle_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.lifecycle.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.lifecycle.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Lifecycle, d);
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

    // Class failure receipts (same capture, from the payments example with one fault each): validateClassDiagram's typed
    // codes, the placement gate's layout/constraint, and the untyped validation problems — messages, subjects, evidence,
    // fixes, order.
    [Theory]
    [InlineData("realization-target")]
    [InlineData("inheritance-kind")]
    [InlineData("inheritance-cycle")]
    [InlineData("title-text-capacity")]
    [InlineData("marker-capacity")]
    [InlineData("unknown-type")]
    [InlineData("unplaced-type")]
    public void Class_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.class.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.class.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Class, d);
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

    // `node render-class.mjs <in> <out> --layout-json` stdout for both class goldens (captured by tools/capture-fixtures.mjs):
    // the LayoutReport stringifies to the same bytes — same keys in the same order, same numbers.
    [Theory]
    [InlineData("payments.class.json", "class-payments")]
    [InlineData("payment-processors.class.json", "class-payment-processors")]
    public void Class_layout_report_equals_upstream_layout_json(string input, string name)
    {
        var r = ArchifyNet.Render(DiagramType.Class, DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream($"examples/{input}"))));
        Assert.Equal(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.layout.json")), Js.Stringify(r.Layout!.Report, 2) + "\n");
    }

    // Tree failure receipts (same capture, from the payment-platform example with one fault each): validateStructure's four
    // tree/* codes and the canvas check's untyped layout/constraint problems — messages, subjects, evidence, fixes, order.
    [Theory]
    [InlineData("root-count")]
    [InlineData("missing-parent")]
    [InlineData("duplicate-id")]
    [InlineData("cycle")]
    [InlineData("canvas-constraint")]
    public void Tree_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.tree.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.tree.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Tree, d);
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

    // `node render-tree.mjs <in> <out> --layout-json` stdout for both tree goldens and the rightward fixture.
    [Theory]
    [InlineData("upstream:examples/payment-platform.tree.json", "tree-payment-platform")]
    [InlineData("upstream:examples/archify-repository.tree.json", "tree-archify-repository")]
    [InlineData("fixture:diagrams/payment-platform.right.tree.json", "tree-payment-platform.right")]
    public void Tree_layout_report_equals_upstream_layout_json(string input, string name)
    {
        var path = input.StartsWith("upstream:") ? Fixtures.Upstream(input["upstream:".Length..]) : Fixtures.Fixture(input["fixture:".Length..]);
        var r = ArchifyNet.Render(DiagramType.Tree, DiagramLoader.Parse(Fixtures.Lf(path)));
        Assert.Equal(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.layout.json")), Js.Stringify(r.Layout!.Report, 2) + "\n");
    }

    [Fact] public void Non_object_root_returns_json_parse_diagnostic()
    {
        var r = ArchifyNet.Render(DiagramType.Architecture, new JsonArray());
        Assert.Equal("", r.Html);
        Assert.Equal(["input/json-parse"], r.Diagnostics.Select(x => x.Code));
    }

    // installRendererDiagnosticBoundary: an error without a diagnostic comes back as internal/unclassified, never thrown.
    [Fact] public void Non_diagnostic_renderer_error_returns_internal_unclassified()
    {
        var d = (JsonObject)JsonNode.Parse(Fixtures.Lf(Fixtures.Upstream("examples/web-app.architecture.json")))!;
        d["meta"]!["title"] = JsonNode.Parse("\"\\ud800\"");   // lone-surrogate escape: reading it as a string throws InvalidOperationException
        var r = ArchifyNet.Render(DiagramType.Architecture, d);
        Assert.Equal("", r.Html);
        var diagnostic = Assert.Single(r.Diagnostics, x => x.Code == "internal/unclassified");
        Assert.Equal("InvalidOperationException", diagnostic.Evidence["errorName"]!.GetValue<string>());
    }

    [Fact] public void Validate_passes_every_architecture_target_and_fails_bad_input()
    {
        foreach (var t in Targets()) Assert.DoesNotContain(ArchifyNet.Validate(DiagramTypes.Parse((string)t[0]), DiagramLoader.Parse(Fixtures.Lf((string)t[1]))), d => d.Severity == Severity.Error);
        var bad = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/web-app.architecture.json"))); bad["components"]![0]!["id"] = "1bad";
        Assert.Contains(ArchifyNet.Validate(DiagramType.Architecture, bad), d => d.Code == "schema/pattern");
    }

    // Upstream bin/archify.mjs checkerDiagnostics, sliced out verbatim and run by node 2026-10-14 over each failing checker
    // probe's expected report: the hash is SHA-256 of JSON.stringify(checkerDiagnostics(report)). Together the probes reach
    // composition issues with relationship and nodeId subjects, CHECK_FIXES, the svg_path_data forward of the checker's own
    // path diagnostics and compositionFixes' below-hard-floor desktop-readability branch with and without the reader cap.
    [Theory]
    [InlineData("corridor-single-path", "58bfe390472a8c059f85fbf52f69c184400e339b88f7c9dab873eccc2a07e42c")]
    [InlineData("corridor-subpaths", "58bfe390472a8c059f85fbf52f69c184400e339b88f7c9dab873eccc2a07e42c")]
    [InlineData("declared-wide-origin", "023f39d0c6044d9a2fe5625f448c543cf1b77a4ab726a32aa8e0527d571b5c32")]
    [InlineData("empty", "f4d909a8d6d8272f1586efa381642c0e6357c8f73c8850c1c17034861aa24899")]
    [InlineData("intrinsic-height-fit", "4a61cac47b3a39858b787caee5090dd3bc214e3743de456de7cddcb1e0d074d0")]
    [InlineData("readable-v2-halos", "3481d20d21b23e5bb6c83d0e82acd8bc62ac3023b1b898b7bd67e4c2b9545350")]
    [InlineData("showcase-advisory-gates", "5f78f5d4c559bce1861a68972862ce50ed112f7b5722745513a08f35db3278cb")]
    [InlineData("standard-profile", "4a61cac47b3a39858b787caee5090dd3bc214e3743de456de7cddcb1e0d074d0")]
    [InlineData("two-svg-blocks", "3d13b38566c8e2ec4f85e5981e0cc3f3558293c13f060777901dc1cd1eff4d26")]
    public void Checker_diagnostics_equal_upstream_on_failing_probe(string probe, string sha256)
    {
        var check = ArchifyNet.Check(Fixtures.Lf(Fixtures.Fixture($"checker/{probe}.html")));
        Assert.False(check.Ok);
        var json = DiagnosticsJson(CheckerDiagnostics.From(check.Report));
        Assert.Equal(sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))));
    }

    // The branches no probe reaches, with upstream's output for this exact report (same node run): viewport-height detail
    // stripping (and its unfiltered "" fix when detail is missing), desktop-readability's non-finite and reflow branches with
    // Number() coercion of "12" and null, a warning issue skipped, falsy relationship/nodeId, COMPOSITION_CHECKS skipped,
    // svg_path_data without a forwardable error diagnostic, all-falsy details and a check without details.
    [Fact] public void Checker_diagnostics_cover_every_composition_fix_branch()
    {
        var report = JsonNode.Parse("""{"ok":false,"checks":[{"name":"svg_path_data","ok":false,"details":["x"]},{"name":"some_check","ok":false,"details":["",null,0]},{"name":"route_rhythm","ok":false,"details":["skip"]},{"name":"fine","ok":true},{"name":"bare_check","ok":false}],"composition":{"issues":[{"severity":"warning","code":"composition/proper-crossing"},{"severity":"error","code":"composition/viewport-height","detail":"[composition/viewport-height] page is 12.5px too tall","viewBoxWidth":400},{"code":"composition/viewport-height","severity":"error"},{"severity":"error","code":"composition/desktop-readability","nodeId":"n1","sourceFontPx":12,"availableDiagramWidth":930,"minimumProjectedFontPx":6,"viewBoxWidth":2400.5,"budgetBasis":"legacy-930","budgetLimit":"legacy"},{"severity":"error","code":"composition/desktop-readability","sourceFontPx":"12","availableDiagramWidth":null,"minimumProjectedFontPx":6,"viewBoxWidth":400},{"severity":"error","code":"composition/desktop-readability","sourceFontPx":7,"availableDiagramWidth":1346,"minimumProjectedFontPx":6,"viewBoxWidth":3000,"budgetBasis":"recognized-declared-wide","budgetLimit":1346.25},{"severity":"error","code":"composition/unknown-thing","relationship":"","nodeId":0,"extra":1.5}]},"diagnostics":[{"code":"artifact/svg-path-malformed","severity":"warning","message":"ignored"}]}""")!.AsObject();
        const string expected = """[{"code":"composition/viewport-height","severity":"error","message":"Final artifact failed composition/viewport-height.","subject":{"check":"composition"},"evidence":{"detail":"[composition/viewport-height] page is 12.5px too tall","viewBoxWidth":400},"supportedFixes":["page is 12.5px too tall"]},{"code":"composition/viewport-height","severity":"error","message":"Final artifact failed composition/viewport-height.","subject":{"check":"composition"},"evidence":{},"supportedFixes":[""]},{"code":"composition/desktop-readability","severity":"error","message":"Final artifact failed composition/desktop-readability.","subject":{"check":"composition","nodeId":"n1"},"evidence":{"sourceFontPx":12,"availableDiagramWidth":930,"minimumProjectedFontPx":6,"viewBoxWidth":2400.5,"budgetBasis":"legacy-930","budgetLimit":"legacy"},"supportedFixes":["Preserve the semantic text and any supplied coordinates, routes, sides, channels, and labels. Compactly reflow automatic spacing and empty corridors so the complete viewBox width is at most 1860px (current 2400.5px; 12px source text at 930px desktop budget). If supplied geometry fixes that width, use a renderer-supported semantic text-size setting or renderer-level fix instead. Position-only label controls cannot repair its projection."]},{"code":"composition/desktop-readability","severity":"error","message":"Final artifact failed composition/desktop-readability.","subject":{"check":"composition"},"evidence":{"sourceFontPx":"12","availableDiagramWidth":null,"minimumProjectedFontPx":6,"viewBoxWidth":400},"supportedFixes":["Preserve the semantic text and any supplied coordinates, routes, sides, channels, and labels. Repair the measured source font, desktop budget, or complete viewBox width; position-only label controls do not change projected text size."]},{"code":"composition/desktop-readability","severity":"error","message":"Final artifact failed composition/desktop-readability.","subject":{"check":"composition"},"evidence":{"sourceFontPx":7,"availableDiagramWidth":1346,"minimumProjectedFontPx":6,"viewBoxWidth":3000,"budgetBasis":"recognized-declared-wide","budgetLimit":1346.25},"supportedFixes":["Preserve the semantic text and any supplied coordinates, routes, sides, channels, and labels. Compactly reflow automatic spacing and empty corridors so the complete viewBox width is at most 1570px (current 3000px; 7px source text at 1346px desktop budget). If supplied geometry fixes that width, use a renderer-supported semantic text-size setting or renderer-level fix instead. Position-only label controls cannot repair its projection. The declared Reader reports a 1346.25 limit and 1346px actual diagram budget; do not assume an uncapped viewport."]},{"code":"composition/unknown-thing","severity":"error","message":"Final artifact failed composition/unknown-thing.","subject":{"check":"composition"},"evidence":{"extra":1.5},"supportedFixes":[]},{"code":"artifact/svg-path-data","severity":"error","message":"x","subject":{"check":"svg_path_data"},"evidence":{"details":["x"]},"supportedFixes":[]},{"code":"artifact/some-check","severity":"error","message":"Final artifact failed some_check.","subject":{"check":"some_check"},"evidence":{"details":["",null,0]},"supportedFixes":[]},{"code":"artifact/bare-check","severity":"error","message":"Final artifact failed bare_check.","subject":{"check":"bare_check"},"evidence":{"details":[]},"supportedFixes":[]}]""";
        Assert.Equal(expected, DiagnosticsJson(CheckerDiagnostics.From(report)));
        // Nothing classified (a composition check and a warning only), and an unparseable checker (`checker = null`).
        const string fallback = """[{"code":"artifact/check-failed","severity":"error","message":"Final artifact check failed without a classified diagnostic.","subject":{"check":"unknown"},"evidence":{},"supportedFixes":[]}]""";
        Assert.Equal(fallback, DiagnosticsJson(CheckerDiagnostics.From(JsonNode.Parse("""{"ok":false,"checks":[{"name":"label_route_clearance","ok":false}],"composition":{"issues":[{"severity":"warning","code":"x"}]}}""")!.AsObject())));
        Assert.Equal(fallback, DiagnosticsJson(CheckerDiagnostics.From(null)));
    }

    // diagnostic({ code, severity, message, subject, evidence, supportedFixes }) as JSON.stringify writes it.
    static string DiagnosticsJson(IEnumerable<Diagnostic> diagnostics) => Js.Stringify(new JsonArray([.. diagnostics.Select(d => (JsonNode)new JsonObject
    {
        ["code"] = d.Code, ["severity"] = d.Severity == Severity.Error ? "error" : "warning", ["message"] = d.Message,
        ["subject"] = d.Subject.DeepClone(), ["evidence"] = d.Evidence.DeepClone(), ["supportedFixes"] = new JsonArray([.. d.SupportedFixes.Select(f => (JsonNode)f)]),
    })]));

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
