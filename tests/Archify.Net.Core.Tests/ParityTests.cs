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
        ["timeline", Fixtures.Upstream("examples/payment-incident.timeline.json"), Fixtures.Upstream("examples/timeline-payment-incident-rendered.html")],
        ["timeline", Fixtures.Upstream("examples/archify-dev-activity.timeline.json"), Fixtures.Upstream("examples/timeline-archify-dev-activity-rendered.html")],
        ["timeline", Fixtures.Fixture("diagrams/dst-new-york.timeline.json"), Fixtures.Golden("dst-new-york-rendered.html")],
        ["timeline", Fixtures.Fixture("diagrams/payment-incident.zh-CN.timeline.json"), Fixtures.Golden("payment-incident.zh-CN-rendered.html")],
        ["waterfall", Fixtures.Upstream("examples/checkout-request.waterfall.json"), Fixtures.Upstream("examples/waterfall-checkout-request-rendered.html")],
        ["waterfall", Fixtures.Upstream("examples/example-rebuild.waterfall.json"), Fixtures.Upstream("examples/waterfall-example-rebuild-rendered.html")],
        ["waterfall", Fixtures.Fixture("diagrams/incomplete-trace.waterfall.json"), Fixtures.Golden("incomplete-trace-rendered.html")],
        // TEST-001: schema-valid fallback sites no other golden reaches, one edit each on a committed input.
        ["waterfall", Fixtures.Fixture("diagrams/incomplete-trace.no-unit.waterfall.json"), Fixtures.Golden("incomplete-trace.no-unit-rendered.html")],   // `meta.unit || 'ms'`
        ["waterfall", Fixtures.Fixture("diagrams/checkout-request.width.waterfall.json"), Fixtures.Golden("checkout-request.width-rendered.html")],     // `layout?.width ?? 760`
        ["timeline", Fixtures.Fixture("diagrams/payment-incident.no-timezone.timeline.json"), Fixtures.Golden("payment-incident.no-timezone-rendered.html")],   // `meta.timezone || 'UTC'`
        ["timeline", Fixtures.Fixture("diagrams/payment-incident.minus-half.timeline.json"), Fixtures.Golden("payment-incident.minus-half-rendered.html")],     // fixed offset -05:30
        ["lifecycle", Fixtures.Fixture("diagrams/agent-run.no-sublabel-tag.lifecycle.json"), Fixtures.Golden("agent-run.no-sublabel-tag-rendered.html")],
        ["class", Fixtures.Fixture("diagrams/payments.no-sublabel.class.json"), Fixtures.Golden("payments.no-sublabel-rendered.html")],
        // Phase 3, task 4: dataflow with authored viewBox (automaticShowcase false). Rows from tests/fixtures/diagrams/phase3-manifest.json.
        ["dataflow", Fixtures.Upstream("examples/product-analytics.dataflow.json"), Fixtures.Upstream("examples/dataflow-product-analytics.html")],
        ["dataflow", Fixtures.Upstream("examples/event-stream.dataflow.json"), Fixtures.Golden("event-stream-rendered.html")],   // reaches the two-line repair
        ["dataflow", Fixtures.Fixture("diagrams/label-visibility.dataflow.json"), Fixtures.Golden("label-visibility-rendered.html")],
        ["dataflow", Fixtures.Fixture("diagrams/v1-baseline.product-analytics.dataflow.json"), Fixtures.Golden("v1-baseline.product-analytics-rendered.html")],   // standard, subtitle
        ["dataflow", Fixtures.Fixture("diagrams/legacy-narrow.dataflow.json"), Fixtures.Golden("legacy-narrow-rendered.html")],   // no quality profile, 423px canvas
        ["dataflow", Fixtures.Fixture("diagrams/product-analytics.brand.dataflow.json"), Fixtures.Golden("product-analytics.brand-rendered.html")],   // brand marks
        // Phase 3, task 5: automatic showcase (no viewBox, showcase profile) — clusters B/C/H/I/M/N/O. five-stage is standard (automatic
        // width only); first-draft.mixed-port-recovery is the R1 deviation fact below.
        ["dataflow", Fixtures.Fixture("diagrams/bidirectional-labels.dataflow.json"), Fixtures.Golden("bidirectional-labels-rendered.html")],
        ["dataflow", Fixtures.Fixture("diagrams/first-draft.joint-label-routing.dataflow.json"), Fixtures.Golden("first-draft.joint-label-routing-rendered.html")],   // zh-CN, repairLabelRoutes
        ["dataflow", Fixtures.Fixture("diagrams/first-draft.ml-features.dataflow.json"), Fixtures.Golden("first-draft.ml-features-rendered.html")],   // crossover halos
        ["dataflow", Fixtures.Fixture("diagrams/first-draft.rejected-grid-recovery.dataflow.json"), Fixtures.Golden("first-draft.rejected-grid-recovery-rendered.html")],
        ["dataflow", Fixtures.Fixture("diagrams/first-draft.vertical-label-corridor.dataflow.json"), Fixtures.Golden("first-draft.vertical-label-corridor-rendered.html")],
        ["dataflow", Fixtures.Fixture("diagrams/first-draft.vertical-label-neighbour.dataflow.json"), Fixtures.Golden("first-draft.vertical-label-neighbour-rendered.html")],
        ["dataflow", Fixtures.Fixture("diagrams/five-stage.dataflow.json"), Fixtures.Golden("five-stage-rendered.html")],   // standard, automatic width
        ["dataflow", Fixtures.Fixture("diagrams/pipeline.dataflow.json"), Fixtures.Golden("pipeline-rendered.html")],
        // Phase 3, task 7: workflow schema v1 (fixed-v1 contract) — clusters G, I1, J, K3/K4 v1, K8, L, M, N and the legacy capacity repair.
        ["workflow", Fixtures.Upstream("examples/incident-response.workflow.json"), Fixtures.Golden("incident-response-rendered.html")],   // one via
        ["workflow", Fixtures.Upstream("examples/release-delivery.workflow.json"), Fixtures.Golden("release-delivery-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/border-clearance.workflow.json"), Fixtures.Golden("border-clearance-rendered.html")],   // authored viewBox
        ["workflow", Fixtures.Fixture("diagrams/v1-700x400.workflow.json"), Fixtures.Golden("v1-700x400-rendered.html")],   // no quality profile
        ["workflow", Fixtures.Fixture("diagrams/v1-explicit-coordinates.workflow.json"), Fixtures.Golden("v1-explicit-coordinates-rendered.html")],   // via/labelAt/channel pins
        ["workflow", Fixtures.Fixture("diagrams/v1-baseline.agent-tool-call.workflow.json"), Fixtures.Golden("v1-baseline.agent-tool-call-rendered.html")],   // presets, sides, label offsets
        // Phase 3, task 8: workflow readable-v2 automatic routing — F readable, H2/H4/H5 skeleton, J3, K1/K2/K5/K6, L, M, O feedback.
        ["workflow", Fixtures.Upstream("examples/holiday-planning.workflow.json"), Fixtures.Golden("holiday-planning-rendered.html")],   // zh-CN, authored legend
        ["workflow", Fixtures.Fixture("diagrams/dense-automatic.workflow.json"), Fixtures.Golden("dense-automatic-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/fanin-label-recovery.workflow.json"), Fixtures.Golden("fanin-label-recovery-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/free-space.workflow.json"), Fixtures.Golden("free-space-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/return-arrival.workflow.json"), Fixtures.Golden("return-arrival-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/routing-clarity.workflow.json"), Fixtures.Golden("routing-clarity-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/first-draft.agent-tool-call.workflow.json"), Fixtures.Golden("first-draft.agent-tool-call-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/first-draft.automatic-fanout.workflow.json"), Fixtures.Golden("first-draft.automatic-fanout-rendered.html")],   // standard
        ["workflow", Fixtures.Fixture("diagrams/first-draft.automatic-port-slot.workflow.json"), Fixtures.Golden("first-draft.automatic-port-slot-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/first-draft.customer-refund.workflow.json"), Fixtures.Golden("first-draft.customer-refund-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/first-draft.incident-response.workflow.json"), Fixtures.Golden("first-draft.incident-response-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/viewport.order-overflow.workflow.json"), Fixtures.Golden("viewport.order-overflow-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/viewport.order-reflow.workflow.json"), Fixtures.Golden("viewport.order-reflow-rendered.html")],
        ["workflow", Fixtures.Fixture("diagrams/viewport.three-lane-remainder.workflow.json"), Fixtures.Golden("viewport.three-lane-remainder-rendered.html")],
    ];

    [Fact] public void Dataflow_quality_option_switches_the_automatic_layout_like_the_env_var()   // Review Focus 2
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/five-stage.dataflow.json")));
        var r = ArchifyNet.Render(DiagramType.Dataflow, d, new RenderOptions(Quality: QualityProfile.Showcase));
        Assert.Equal(Fixtures.Lf(Fixtures.Golden("five-stage.option-showcase-rendered.html")), r.Html);
    }

    [Fact] public void Mixed_port_recovery_differs_from_upstream_only_in_the_dictionary_wrapped_label()   // R1 deviation
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/first-draft.mixed-port-recovery.dataflow.json")));
        var port = ArchifyNet.Render(DiagramType.Dataflow, d).Html.Split('\n');
        var node = Fixtures.Lf(Fixtures.Golden("first-draft.mixed-port-recovery-rendered.html")).Split('\n');
        Assert.Equal(node.Length, port.Length);
        var diff = Enumerable.Range(0, node.Length).Where(i => node[i] != port[i]).ToList();
        Assert.Single(diff);
        Assert.Contains("已持有", node[diff[0]]);
        // The only licensed difference is where the word segmenter split the label: V8 (ICU dictionary) `已持有：处理|完成或失败`,
        // the port (per grapheme) `已持有：处|理完成或失败`. Everything else on the line — the <tspan> attributes included — must match.
        var split = new System.Text.RegularExpressions.Regex(">已持有：处理</tspan>(<tspan [^>]*>)完成或失败<");
        Assert.Single(split.Matches(node[diff[0]]));
        var expectedPortLine = split.Replace(node[diff[0]], ">已持有：处</tspan>$1理完成或失败<");
        Assert.NotEqual(node[diff[0]], expectedPortLine);
        Assert.Equal(expectedPortLine, port[diff[0]]);
    }

    // Dataflow failure receipts (same capture, from the product-analytics example with one fault each): validateDataflow's three
    // dataflow/* codes (both raisedCanvasFits branches of node-below-area, sorted candidates for unknown-endpoint), the untyped
    // layout/constraint problems and a pinned crossing's composition gates — messages, subjects, evidence, fixes, order.
    [Theory]
    [InlineData("df-unknown-endpoint")]
    [InlineData("df-node-above-area")]
    [InlineData("df-node-below-area")]
    [InlineData("df-node-below-area-raised")]
    [InlineData("df-duplicate-node")]
    [InlineData("df-pinned-crossing")]
    public void Dataflow_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.dataflow.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.dataflow.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Dataflow, d);
        AssertReceipt(expected, r);
    }

    // Workflow failure receipts (same capture, from the incident-response example with one fault each): validateWorkflow's
    // mainPath check with its verified repoint fixes (acceptsFix recompiles every candidate). wf-v1-column-capacity needs the
    // migration recompile (Task 10). Task 8: the readable-v2 canvas (finalizeReadableViewBox, both verified fixes recompile
    // v2) and node-text (validateReadableNodeText after the sizing loop) failures.
    [Theory]
    [InlineData("wf-unknown-main-path")]
    [InlineData("wf-viewbox-capacity")]
    [InlineData("wf-sublabel-readability")]
    public void Workflow_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.workflow.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.workflow.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Workflow, d);
        AssertReceipt(expected, r);
    }

    [Fact] public void Waterfall_cycle_reports_upstream_code()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/checkout-request.waterfall.json")));
        var spans = d["spans"]!.AsArray(); spans[0]!["parent"] = spans[1]!.Str("id");   // root now points at its own child
        var r = ArchifyNet.Render(DiagramType.Waterfall, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "waterfall/cycle");
    }

    // render-timeline.mjs:81-86: `new Intl.DateTimeFormat('en-US', { timeZone })` throws on a Windows zone id, so it is rejected.
    [Fact] public void Timeline_windows_zone_id_is_rejected_like_V8()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-incident.timeline.json")));
        d["meta"]!["timezone"] = "China Standard Time";
        var r = ArchifyNet.Render(DiagramType.Timeline, d);
        Assert.Equal("", r.Html);
        Assert.Contains(r.Diagnostics, x => x.Code == "timeline/invalid-timezone" && x.Path == "/meta/timezone");
    }

    [Theory] [InlineData("0000")] [InlineData("0001")] [InlineData("0099")] [InlineData("0100")] [InlineData("2026")]
    public void Timeline_historical_years_keep_utc_offset(string year)   // UP/test/timeline-rendering.test.mjs "historical years"
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/payment-incident.timeline.json")));
        d["meta"]!["timezone"] = "UTC";
        d["events"] = new JsonArray(new JsonObject { ["id"] = "event", ["at"] = $"{year}-01-01T00:00:00Z", ["title"] = "Historical event", ["lane"] = "release" });
        var r = ArchifyNet.Render(DiagramType.Timeline, d);
        Assert.DoesNotContain(r.Diagnostics, x => x.Severity == Severity.Error);
        Assert.Contains("UTC+00:00", r.Svg);
        Assert.DoesNotMatch(@"UTC\+\d{3,}:", r.Svg);
    }

    [Fact] public void Timeline_dst_fixture_crosses_both_transitions()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/dst-new-york.timeline.json")));
        var r = ArchifyNet.Render(DiagramType.Timeline, d);
        Assert.Contains("America/New_York (UTC-05:00)", r.Svg);                      // header uses the first event's offset (EST)
        Assert.Contains("01:30:00 UTC-04:00", r.Svg); Assert.Contains("01:30:00 UTC-05:00", r.Svg);   // the repeated hour, both offsets
        Assert.True(r.Layout!.Report["breaks"]!.AsArray().Count >= 1);
    }

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
        AssertReceipt(expected, r);
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
    [InlineData("duplicate-participant")]
    public void Sequence_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.sequence.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.sequence.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Sequence, d);
        AssertReceipt(expected, r);
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
        AssertReceipt(expected, r);
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
        AssertReceipt(expected, r);
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
        AssertReceipt(expected, r);
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

    // Timeline failure receipts (same capture, from the payment-incident example with one fault each): the input checks'
    // five timeline/* codes (render-timeline.mjs:61-110), incl. a Windows zone id, a calendar-invalid and an hour-invalid
    // timestamp, and both unknown-lane messages — messages, subjects, evidence, fixes, order.
    [Theory]
    [InlineData("invalid-timezone")]
    [InlineData("invalid-timezone-windows")]
    [InlineData("duplicate-lane-id")]
    [InlineData("duplicate-id")]
    [InlineData("invalid-timestamp")]
    [InlineData("invalid-timestamp-hour")]
    [InlineData("unknown-lane")]
    public void Timeline_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.timeline.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.timeline.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Timeline, d);
        AssertReceipt(expected, r);
    }

    // `node render-timeline.mjs <in> <out> --layout-json` stdout for the four timeline goldens.
    [Theory]
    [InlineData("upstream:examples/payment-incident.timeline.json", "timeline-payment-incident")]
    [InlineData("upstream:examples/archify-dev-activity.timeline.json", "timeline-archify-dev-activity")]
    [InlineData("fixture:diagrams/dst-new-york.timeline.json", "dst-new-york")]
    [InlineData("fixture:diagrams/payment-incident.zh-CN.timeline.json", "payment-incident.zh-CN")]
    public void Timeline_layout_report_equals_upstream_layout_json(string input, string name)
    {
        var path = input.StartsWith("upstream:") ? Fixtures.Upstream(input["upstream:".Length..]) : Fixtures.Fixture(input["fixture:".Length..]);
        var r = ArchifyNet.Render(DiagramType.Timeline, DiagramLoader.Parse(Fixtures.Lf(path)));
        Assert.Equal(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.layout.json")), Js.Stringify(r.Layout!.Report, 2) + "\n");
    }

    // Waterfall failure receipts (same capture, from the checkout-request example with one fault each): the timing checks'
    // five codes (render-waterfall.mjs:54-105), every per-span invalid-timing message, and the axis-scale site (:167-171)
    // that throws without a code — messages, subjects, evidence, fixes, order.
    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("invalid-timing")]
    [InlineData("invalid-timing-overflow")]
    [InlineData("invalid-timing-scale")]
    [InlineData("missing-parent")]
    [InlineData("cycle")]
    public void Waterfall_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.waterfall.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.waterfall.expected.json")))!;
        var r = ArchifyNet.Render(DiagramType.Waterfall, d);
        AssertReceipt(expected, r);
    }

    // `node render-waterfall.mjs <in> <out> --layout-json` stdout for the three waterfall goldens.
    [Theory]
    [InlineData("upstream:examples/checkout-request.waterfall.json", "waterfall-checkout-request")]
    [InlineData("upstream:examples/example-rebuild.waterfall.json", "waterfall-example-rebuild")]
    [InlineData("fixture:diagrams/incomplete-trace.waterfall.json", "incomplete-trace")]
    public void Waterfall_layout_report_equals_upstream_layout_json(string input, string name)
    {
        var path = input.StartsWith("upstream:") ? Fixtures.Upstream(input["upstream:".Length..]) : Fixtures.Fixture(input["fixture:".Length..]);
        var r = ArchifyNet.Render(DiagramType.Waterfall, DiagramLoader.Parse(Fixtures.Lf(path)));
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

    // A failure receipt (upstream's ARCHIFY_DIAGNOSTIC_FORMAT=json stderr): no HTML, the same codes in order, then message,
    // severity, subject, evidence and fixes per index.
    static void AssertReceipt(JsonNode expected, RenderResult r)
    {
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
