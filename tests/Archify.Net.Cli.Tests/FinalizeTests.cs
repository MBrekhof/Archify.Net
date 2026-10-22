using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Archify.Net.Cli.Browser;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Finalize;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 12: runFinalize (finalize.mjs:706-1009) and compactFinalizeReceipt through the IStageRunner seam. Ports
// T/finalize.test.mjs (T:), T/finalize-browser-lifecycle.test.mjs (L:) and the first two cases of T/finalize-paths.test.mjs (P:).
// A fake runner stands in for upstream's injected runCommand: it answers each stage with a child's {status, stdout, stderr}.
public sealed class FinalizeTests
{
    static readonly UTF8Encoding Utf8 = new(false);
    const string ReceiptId = "11111111-1111-4111-8111-111111111111";

    sealed class Workspace : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("archify-finalize-").FullName;
        public string At(string name) => Path.Join(Root, name);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    // result(receipt, status, stderr) (T:26-28)
    static StageResult Result(JsonNode? receipt, int status = 0, string stderr = "") => new(status, Js.Stringify(receipt) + "\n", stderr);

    static JsonObject Identity(string contents) => Identity(Utf8.GetBytes(contents));
    static JsonObject Identity(byte[] bytes) => new() { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length };

    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    // writeCurrentDelivery (T:38-50)
    static void WriteCurrentDelivery(string output, JsonObject receipt) =>
        File.WriteAllText(Path.ChangeExtension(output, ".delivery.json"), Js.Stringify(new JsonObject
        {
            ["schemaVersion"] = 1, ["receiptId"] = receipt["receiptId"]?.DeepClone(), ["status"] = "current", ["command"] = "deliver",
            ["type"] = receipt["type"]?.DeepClone(), ["input"] = receipt["input"]?.DeepClone(), ["output"] = receipt["output"]?.DeepClone(),
            ["specification"] = receipt["specification"]?.DeepClone(), ["artifact"] = receipt["artifact"]?.DeepClone(),
        }) + "\n", Utf8);

    // passingDelivery (T:52-75)
    static JsonObject PassingDelivery(string input, string output, string source, string artifact = "<!doctype html><title>verified</title>",
        string receiptId = ReceiptId, string quality = "showcase", string type = "architecture")
    {
        File.WriteAllText(output, artifact, Utf8);
        var receipt = new JsonObject
        {
            ["schemaVersion"] = 1, ["receiptId"] = receiptId, ["ok"] = true, ["command"] = "deliver", ["type"] = type, ["input"] = input, ["output"] = output,
            ["specification"] = Identity(source), ["artifact"] = Identity(artifact),
            ["validation"] = new JsonObject
            {
                ["checksPassed"] = 9, ["checkCount"] = 9, ["compositionProfile"] = quality, ["compositionStatus"] = "pass", ["errors"] = 0, ["warnings"] = 0,
            },
        };
        WriteCurrentDelivery(output, receipt);
        return receipt;
    }

    // passingCheck (T:77-93)
    static JsonObject PassingCheck(string output, JsonNode? artifact, JsonNode? deliveryReceiptId, string quality = "showcase", int checkCount = 9) => new()
    {
        ["schemaVersion"] = 1, ["ok"] = true, ["file"] = output, ["artifact"] = artifact?.DeepClone(),
        ["checks"] = new JsonArray([.. Enumerable.Range(0, checkCount).Select(i => (JsonNode)new JsonObject { ["name"] = $"artifact-{i}", ["ok"] = true })]),
        ["provenance"] = "current", ["deliveryReceiptId"] = deliveryReceiptId?.DeepClone(),
        ["composition"] = new JsonObject
        {
            ["schemaVersion"] = 1, ["profile"] = quality, ["status"] = "pass", ["summary"] = new JsonObject { ["errors"] = 0, ["warnings"] = 0 },
        },
    };

    static JsonArray Viewports(IEnumerable<(int Width, int Height)> viewports, Func<int, int, JsonObject> entry) =>
        new([.. viewports.Select(v => (JsonNode)entry(v.Width, v.Height))]);

    // passingBrowserCheck (T:95-129)
    static JsonObject PassingBrowserCheck(string output, JsonNode? artifact, JsonNode? deliveryReceiptId, string? outDir)
    {
        var light = BrowserEvidence.VisualCheckViewports;
        var artifactEntry = new JsonObject { ["path"] = output };
        foreach (var (key, value) in artifact!.AsObject()) artifactEntry[key] = value?.DeepClone();
        var themes = Viewports(light, (w, h) => new JsonObject { ["width"] = w, ["height"] = h, ["requestedTheme"] = "light", ["resolvedTheme"] = "light", ["ok"] = true });
        foreach (var entry in Viewports(BrowserEvidence.CaptureViewports, (w, h) => new JsonObject { ["width"] = w, ["height"] = h, ["requestedTheme"] = "dark", ["resolvedTheme"] = "dark", ["ok"] = true }))
            themes.Add(entry!.DeepClone());
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["ok"] = true, ["command"] = "browser-check", ["evidenceKind"] = "automated-browser", ["status"] = "pass",
            ["visualReview"] = "not-requested", ["provenance"] = "current", ["deliveryReceiptId"] = deliveryReceiptId?.DeepClone(), ["artifact"] = artifactEntry,
            ["diagnostics"] = new JsonArray(),
            ["containment"] = new JsonObject { ["status"] = "pass", ["viewports"] = Viewports(light, (w, h) => new JsonObject { ["width"] = w, ["height"] = h, ["theme"] = "light", ["ok"] = true }) },
            ["themeStates"] = new JsonObject { ["status"] = "pass", ["viewports"] = themes },
            ["readability"] = new JsonObject { ["status"] = "pass", ["viewports"] = Viewports(light, (w, h) => new JsonObject { ["width"] = w, ["height"] = h, ["theme"] = "light", ["ok"] = true, ["readabilityOk"] = true }) },
            ["viewerChrome"] = new JsonObject { ["status"] = "pass", ["viewports"] = Viewports(light, (w, h) => new JsonObject { ["width"] = w, ["height"] = h, ["theme"] = "light", ["ok"] = true, ["viewerChromeOk"] = true }) },
            ["captures"] = new JsonObject { ["status"] = "not-requested", ["screenshots"] = new JsonArray(), ["contactSheet"] = null, ["contactSheetImage"] = null },
            ["sidecars"] = new JsonObject { ["directory"] = outDir, ["receipt"] = "diagram.browser-check.json" },
        };
    }

    // upstream's runCommand: one answer per stage, the stages asked recorded in order.
    sealed class FakeStages(Func<string, StageResult> answer) : IStageRunner
    {
        public List<string> Calls { get; } = [];

        public Task<StageResult> RunAsync(string stage, IReadOnlyList<string> args, StageContext context)
        {
            Calls.Add(stage);
            return Task.FromResult(answer(stage));
        }
    }

    static Task<FinalizeResult> Finalize(FinalizeRequest request, IStageRunner runner, IBrowserInspectorFactory? browsers = null) =>
        FinalizeGate.RunAsync(request, CliServices.Default with { StageRunner = runner, BrowserInspectors = browsers ?? new BrowserEvidenceTests.FakeBrowsers(null, "unavailable") });

    static void AssertJson(string expected, JsonNode? actual) => Assert.Equal(Js.Stringify(JsonNode.Parse(expected)), Js.Stringify(actual));

    static string Code(FinalizeResult result, int index = 0) => result.Summary["diagnostics"]![index]!["code"]!.GetValue<string>();

    // T:131
    [Fact]
    public async Task Finalize_reuses_delivery_validation_runs_one_build_and_keeps_full_stage_receipts_out_of_its_compact_summary()
    {
        using var w = new Workspace();
        var (input, output, outDir) = (w.At("diagram.json"), w.At("diagram.html"), w.At("evidence"));
        const string source = "{\"meta\":{\"quality_profile\":\"showcase\"}}";
        File.WriteAllText(input, source, Utf8);
        var runner = new FakeStages(stage =>
        {
            if (stage == "deliver") return Result(PassingDelivery(input, output, source));
            var delivery = PassingDelivery(input, output, source);
            if (stage == "check") return Result(PassingCheck(output, delivery["artifact"], delivery["receiptId"]));
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Join(outDir, "diagram.browser-check.json"), "browser evidence", Utf8);
            var browser = PassingBrowserCheck(output, delivery["artifact"], delivery["receiptId"], outDir);
            browser["containment"]!["viewports"]![0]!["large"] = "large-stage-array-is-kept-only-in-full-receipt";
            return Result(browser);
        });

        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root) { OutDir = outDir }, runner);

        Assert.Equal(0, finalized.ExitCode);
        Assert.True((bool)finalized.Receipt["ok"]!);
        Assert.Equal(["deliver", "check", "browser-check"], runner.Calls);
        AssertJson("""{"validate":"pass","deliver":"pass","check":"pass","browser-check":"pass"}""", finalized.Summary["gates"]);
        Assert.Equal(Path.Join(outDir, "diagram.browser-check.json"), (string?)finalized.Summary["evidence"]!["browserCheckReceipt"]);
        Assert.Equal("not-requested", (string?)finalized.Summary["visualReview"]);
        Assert.False(finalized.Summary.ContainsKey("stages"));
        Assert.DoesNotContain("large-stage-array", Js.Stringify(finalized.Summary));
        Assert.Equal(BrowserEvidence.VisualCheckViewports.Count, finalized.Receipt["stages"]!["browser-check"]!["receipt"]!["containment"]!["viewports"]!.AsArray().Count);

        var receiptPath = FinalizeGate.DefaultReceiptPath(output, outDir);
        Assert.Equal(receiptPath, (string?)finalized.Summary["evidence"]!["receipt"]);
        var summaryPath = FinalizeGate.DefaultSummaryPath(receiptPath);
        Assert.Equal(summaryPath, (string?)finalized.Summary["evidence"]!["summaryReceipt"]);
        var persisted = JsonNode.Parse(File.ReadAllText(receiptPath))!;
        Assert.True((bool)persisted["ok"]!);
        Assert.Equal("embedded-in-deliver", (string?)persisted["stages"]!["validate"]!["execution"]);
        Assert.Equal(9, (int)persisted["stages"]!["validate"]!["receipt"]!["validation"]!["checkCount"]!);
        var persistedSummary = JsonNode.Parse(File.ReadAllText(summaryPath))!.AsObject();
        Assert.True((bool)persistedSummary["ok"]!);
        Assert.False(persistedSummary.ContainsKey("stages"));
    }

    // T:190
    [Fact]
    public async Task Finalize_stops_at_the_failed_gate_and_persists_actionable_failure_evidence()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        File.WriteAllText(input, "{}", Utf8);
        const string diagnostic = """{"code":"composition/route-crossing","severity":"error","message":"A route crosses an unrelated node.","subject":{"edge":"a-b"},"evidence":{"intersection":[12,40]},"supportedFixes":["move the diagnosed route"]}""";
        var runner = new FakeStages(_ => Result(Parse($$"""{"ok":false,"command":"deliver","stage":"render","diagnostics":[{{diagnostic}}]}"""), 1));

        var finalized = await Finalize(new FinalizeRequest("workflow", input, output, w.Root), runner);

        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(["deliver"], runner.Calls);
        Assert.Equal("validate", (string?)finalized.Receipt["failedStage"]);
        foreach (var gate in new[] { "deliver", "check", "browser-check" }) Assert.Equal("not-run", (string?)finalized.Summary["gates"]![gate]);
        AssertJson($"[{diagnostic}]", finalized.Summary["diagnostics"]);
        AssertJson("""{"total":1,"shown":1,"truncated":false}""", finalized.Summary["diagnosticSummary"]);
        AssertJson(Js.Stringify(new JsonObject
        {
            ["action"] = "edit-in-place", ["candidate"] = input,
            ["constraint"] = "Preserve unaffected semantics and geometry; apply each diagnostic's supportedFixes, or the fix its message names, and do not replace the whole candidate.",
            ["then"] = "finalize-once",
        }), finalized.Summary["nextAction"]);
        Assert.Equal(40, (int)finalized.Receipt["diagnostics"]![0]!["evidence"]!["intersection"]![1]!);
        Assert.Equal("fail", (string?)JsonNode.Parse(File.ReadAllText((string)finalized.Summary["evidence"]!["receipt"]!))!["status"]);
        Assert.Equal(Js.Stringify(finalized.Summary), Js.Stringify(JsonNode.Parse(File.ReadAllText((string)finalized.Summary["evidence"]!["summaryReceipt"]!))));
    }

    // T:246
    [Fact]
    public void Compact_finalize_receipts_preserve_the_acceptance_boundary()
    {
        var compact = FinalizeGate.Compact(Parse("""
            {"ok":true,"status":"pass","type":"sequence","quality":"showcase","specification":{"path":"/tmp/spec.json","sha256":"spec"},
             "artifact":{"path":"/tmp/artifact.html","sha256":"artifact"},
             "stages":{"validate":{"status":"pass"},"deliver":{"status":"pass"},"check":{"status":"pass"},"browser-check":{"status":"pass"}},
             "diagnostics":[],"evidence":{"receipt":"/tmp/artifact.finalize.json","browserCheckReceipt":"/tmp/artifact.browser-check.json"},
             "visualReview":"not-requested","durationMs":4200}
            """));
        Assert.True((bool)compact["ok"]!);
        Assert.Equal("not-requested", (string?)compact["visualReview"]);
        Assert.Equal("pass", (string?)compact["gates"]!["browser-check"]);
        Assert.EndsWith(".json", (string)compact["evidence"]!["browserCheckReceipt"]!);
        AssertJson("""{"total":0,"shown":0,"truncated":false}""", compact["diagnosticSummary"]);
        Assert.False(compact.ContainsKey("nextAction"));
    }

    // T:268
    [Fact]
    public void Compact_success_retains_route_quality_review_signals_without_claiming_perceptual_approval()
    {
        var compact = FinalizeGate.Compact(Parse("""
            {"ok":true,"status":"pass","diagnostics":[],"stages":{"check":{"status":"pass","receipt":{"composition":{"metrics":
             {"resolvedCrossovers":12,"routesOverSuggestedBends":11,"routesOverSuggestedStretch":0}}}}}}
            """));
        Assert.True((bool)compact["ok"]!);
        Assert.Equal("not-requested", (string?)compact["visualReview"]);
        AssertJson("""{"resolvedCrossovers":12,"routesOverSuggestedBends":11}""", compact["visualReviewRecommendation"]!["signals"]);
        Assert.Equal("inspect-route-readability", (string?)compact["visualReviewRecommendation"]!["action"]);
        var uncomplicated = FinalizeGate.Compact(Parse("""
            {"ok":true,"stages":{"check":{"receipt":{"composition":{"metrics":{"resolvedCrossovers":0,"routesOverSuggestedBends":0,"routesOverSuggestedStretch":0}}}}}}
            """));
        Assert.False(uncomplicated.ContainsKey("visualReviewRecommendation"));
    }

    // T:285
    [Fact]
    public void Compact_leading_space_advice_preserves_successful_gates_and_optional_visual_review()
    {
        var leadingSpace = new JsonObject { ["occupiedTop"] = 181, ["emptyTopPx"] = 181, ["canvasHeight"] = 576, ["emptyTopRatio"] = 181.0 / 576, ["reviewSuggested"] = true };
        JsonObject Receipt(bool ok = true) => new()
        {
            ["ok"] = ok, ["status"] = ok ? "pass" : "fail", ["diagnostics"] = new JsonArray(),
            ["stages"] = new JsonObject { ["check"] = new JsonObject { ["status"] = "pass", ["receipt"] = new JsonObject { ["composition"] = new JsonObject { ["leadingSpace"] = leadingSpace.DeepClone() } } } },
        };
        var compact = FinalizeGate.Compact(Receipt());
        Assert.Equal("pass", (string?)compact["status"]);
        Assert.Equal("pass", (string?)compact["gates"]!["check"]);
        AssertJson("[]", compact["diagnostics"]);
        Assert.Equal("not-requested", (string?)compact["visualReview"]);
        Assert.Equal("inspect-leading-space", (string?)compact["layoutReviewRecommendation"]!["action"]);
        Assert.Equal(Js.Stringify(leadingSpace), Js.Stringify(compact["layoutReviewRecommendation"]!["evidence"]));
        Assert.Contains("user-fixed geometry", (string)compact["layoutReviewRecommendation"]!["repair"]!);
        Assert.Contains("No screenshot is required", (string)compact["layoutReviewRecommendation"]!["repair"]!);
        leadingSpace["reviewSuggested"] = false;
        Assert.False(FinalizeGate.Compact(Receipt()).ContainsKey("layoutReviewRecommendation"));
        leadingSpace["reviewSuggested"] = true;
        Assert.False(FinalizeGate.Compact(Receipt(ok: false)).ContainsKey("layoutReviewRecommendation"));
    }

    // T:306
    [Fact]
    public void Compact_success_bounds_review_context_and_preserves_relationship_identity()
    {
        var crossings = new JsonArray([.. Enumerable.Range(0, 10).Select(i => (JsonNode)Parse($$"""{"left":{"id":"edge-{{i}}"},"right":{"id":"hub"},"point":[{{i}},50]}"""))]);
        var detours = JsonNode.Parse("""[{"relationship":{"id":"return","from":"worker","to":"api"},"bends":4,"stretch":1.5,"directCorridorBlockers":[{"id":"store","label":"Shared store","box":[100,40,80,50]}]}]""")!;
        var receipt = new JsonObject
        {
            ["ok"] = true,
            ["stages"] = new JsonObject { ["check"] = new JsonObject { ["receipt"] = new JsonObject { ["composition"] = new JsonObject
            {
                ["metrics"] = new JsonObject { ["resolvedCrossovers"] = 10, ["routesOverSuggestedBends"] = 1 },
                ["routeReview"] = new JsonObject { ["crossings"] = crossings, ["detours"] = detours },
            } } } },
        };
        var compact = FinalizeGate.Compact(receipt);
        AssertJson(Js.Stringify(new JsonObject
        {
            ["crossings"] = new JsonArray([.. crossings.Take(8).Select(c => c!.DeepClone())]), ["detours"] = detours.DeepClone(), ["truncated"] = true,
        }), compact["visualReviewRecommendation"]!["affectedRoutes"]);
        Assert.Equal(10, crossings.Count);
        Assert.Equal("not-requested", (string?)compact["visualReview"]);
        Assert.Contains("architecture-layout-repair.md", (string)compact["visualReviewRecommendation"]!["repair"]!);
    }

    // T:322
    [Fact]
    public void Compact_success_turns_route_evidence_into_node_move_hints_that_keep_relationships()
    {
        var hints = FinalizeGate.Compact(Parse("""
            {"ok":true,"stages":{"check":{"receipt":{"composition":{"metrics":{"resolvedCrossovers":2,"routesOverSuggestedBends":2},"routeReview":{
             "crossings":[{"left":{"from":"desktop","to":"http"},"right":{"from":"tunnel","to":"http"},"sharedNode":"http"},
                          {"left":{"from":"teamcli","to":"http"},"right":{"from":"runtime","to":"sqlite"}}],
             "crowdedSides":[{"node":"app","side":"right","relationships":4,"sidePx":70,"neededPx":74}],
             "detours":[{"relationship":{"from":"registry","to":"shim"},"bends":3},{"relationship":{"from":"worker","to":"api"},"bends":4,"directCorridorBlockers":[{"id":"store"}]}]}}}}}}
            """))["visualReviewRecommendation"]!["hints"]!.AsArray().Select(h => h!.GetValue<string>()).ToList();
        Assert.Equal(4, hints.Count);
        Assert.Contains("app has 4 relationships facing its right side, which fits 3 ports: make that side at least 74px", hints[0]);
        Assert.Contains("move node desktop or tunnel so the two reach http from different sides", hints[1]);
        Assert.Contains("teamcli → http crosses runtime → sqlite", hints[2]);
        Assert.Contains("move node registry or shim", hints[3]);
        Assert.All(hints, hint => Assert.DoesNotContain("endpoint", hint));
        Assert.False(FinalizeGate.Compact(Parse("""
            {"ok":true,"stages":{"check":{"receipt":{"composition":{"metrics":{"resolvedCrossovers":1},"routeReview":{"crossings":[],"detours":[]}}}}}}
            """))["visualReviewRecommendation"]!.AsObject().ContainsKey("hints"));
    }

    // T:348
    [Fact]
    public void Compact_failure_receipts_retain_diverse_actionable_subjects_without_embedding_full_stage_evidence()
    {
        var diagnostics = new JsonArray([.. Enumerable.Range(0, 20).Select(i => (JsonNode)Parse($$"""
            {"code":"composition/proper-crossing","severity":"error","message":"Connection edge-{{i}} crosses another route.",
             "subject":{"id":"edge-{{i}}","collection":"connections","index":{{i}}},"evidence":{"crossing":{"x":{{i * 10}},"y":{{i * 20}} } },
             "supportedFixes":["move edge-{{i}} without changing its endpoints"]}
            """))]);
        var receipt = new JsonObject
        {
            ["ok"] = false, ["status"] = "fail", ["failedStage"] = "validate", ["type"] = "architecture", ["quality"] = "showcase",
            ["specification"] = Parse("""{"path":"/tmp/spec.json","sha256":"spec"}"""), ["artifact"] = Parse("""{"path":"/tmp/artifact.html"}"""),
            ["stages"] = new JsonObject { ["validate"] = new JsonObject { ["status"] = "fail", ["receipt"] = new JsonObject { ["diagnostics"] = diagnostics.DeepClone(), ["renderedHtml"] = new string('x', 100000) } } },
            ["diagnostics"] = diagnostics,
            ["evidence"] = Parse("""{"receipt":"/tmp/artifact.finalize.json","summaryReceipt":"/tmp/artifact.finalize-summary.json"}"""),
        };

        var compact = FinalizeGate.Compact(receipt);
        Assert.Equal(8, compact["diagnostics"]!.AsArray().Count);
        Assert.Equal(8, compact["diagnostics"]!.AsArray().Select(d => (string?)d!["subject"]!["id"]).Distinct().Count());
        AssertJson("""{"total":20,"shown":8,"truncated":true,"omittedByCode":{"composition/proper-crossing":12}}""", compact["diagnosticSummary"]);
        Assert.Equal("edit-in-place", (string?)compact["nextAction"]!["action"]);
        Assert.Contains("Preserve all semantics and user-fixed geometry", (string)compact["nextAction"]!["constraint"]!);
        Assert.Contains("local repair or connected-scene reflow", (string)compact["nextAction"]!["constraint"]!);
        Assert.False(compact.ContainsKey("stages"));
        Assert.True(Js.Stringify(compact).Length < Js.Stringify(receipt).Length / 4);
    }

    // T:384
    [Fact]
    public void Compact_failure_summaries_count_omitted_entries_by_code_with_repeated_subjects()
    {
        string[] codes = ["composition/proper-crossing", "layout/constraint", "__proto__"];
        var diagnostics = new JsonArray([.. Enumerable.Range(0, 14).Select(i => (JsonNode)new JsonObject
        {
            ["code"] = codes[i % codes.Length], ["message"] = $"Failure {i}", ["subject"] = new JsonObject { ["id"] = $"edge-{i / 3}" },
        })]);
        var receipt = new JsonObject { ["ok"] = false, ["status"] = "fail", ["diagnostics"] = diagnostics };
        var original = Js.Stringify(diagnostics);
        var compact = FinalizeGate.Compact(receipt);
        Assert.Equal(new[] { 0, 1, 2, 3, 6, 9, 12, 4 }.Select(i => $"Failure {i}"), compact["diagnostics"]!.AsArray().Select(d => (string)d!["message"]!));
        var shown = compact["diagnostics"]!.AsArray().Select(d => (string)d!["message"]!).ToHashSet();
        var omitted = diagnostics.Where(d => !shown.Contains((string)d!["message"]!)).ToList();
        Assert.Equal(omitted.Count, (int)compact["diagnosticSummary"]!["total"]! - (int)compact["diagnosticSummary"]!["shown"]!);
        AssertJson("""{"layout/constraint":3,"__proto__":3}""", compact["diagnosticSummary"]!["omittedByCode"]);
        Assert.Equal(original, Js.Stringify(diagnostics));
        foreach (var entries in new[] { new JsonArray(), new JsonArray([.. diagnostics.Take(8).Select(d => d!.DeepClone())]) })
            Assert.False(FinalizeGate.Compact(new JsonObject { ["ok"] = false, ["status"] = "fail", ["diagnostics"] = entries })["diagnosticSummary"]!.AsObject().ContainsKey("omittedByCode"));
    }

    // T:411
    [Fact]
    public async Task Merged_viewport_overflow_diagnostics_preserve_each_subject_evidence_and_detection_source()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        File.WriteAllText(input, "{}", Utf8);
        var diagnostics = JsonNode.Parse("""
            [{"code":"viewer/viewport-overflow","severity":"error","message":"meta.title overflows the 2048px viewport by 193px.",
              "subject":{"diagramType":"architecture","path":"/meta/title","viewport":"2048x1200"},
              "evidence":{"scrollWidth":2241,"scrollHeight":1200,"overflowX":true,"overflowY":false,"overflowDisposition":"clipped"},"supportedFixes":["shorten meta.title"]},
             {"code":"viewer/viewport-overflow","severity":"error","message":"meta.subtitle overflows the 2048px viewport by 91px.",
              "subject":{"diagramType":"architecture","path":"/meta/subtitle","viewport":"2048x1200"},
              "evidence":{"scrollWidth":2139,"scrollHeight":1200,"overflowX":true,"overflowY":false,"overflowDisposition":"clipped"},"supportedFixes":["shorten meta.subtitle"]},
             {"code":"viewer/viewport-overflow","severity":"error","message":"meta.title (~4090px estimated) exceeds the widest 2048px viewport — it will overflow before render.",
              "subject":{"diagramType":"architecture","path":"/meta/title"},
              "evidence":{"estimatedTextWidthPx":4090,"widestViewportPx":2048,"prediction":"text-width estimate"},"supportedFixes":["shorten meta.title"]},
             {"code":"layout/constraint","severity":"error","message":"Two distinct subjects share this identical message.",
              "subject":{"diagramType":"architecture","path":"/nodes/0/label"},"evidence":{"node":0},"supportedFixes":[]},
             {"code":"layout/constraint","severity":"error","message":"Two distinct subjects share this identical message.",
              "subject":{"diagramType":"architecture","path":"/nodes/1/label"},"evidence":{"node":1},"supportedFixes":[]}]
            """);
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root),
            new FakeStages(_ => Result(new JsonObject { ["ok"] = false, ["command"] = "deliver", ["diagnostics"] = diagnostics!.DeepClone() }, 1)));

        Assert.Equal(1, finalized.ExitCode);
        var all = finalized.Receipt["diagnostics"]!.AsArray();
        var merged = all[0]!;
        Assert.Equal("viewer/viewport-overflow", (string?)merged["code"]);
        Assert.Null(merged["subject"]!["path"]);
        var overflows = merged["evidence"]!["overflows"]!.AsArray();
        Assert.Equal(3, overflows.Count);
        Assert.Equal(new[] { ("/meta/title", "browser-measurement"), ("/meta/subtitle", "browser-measurement"), ("/meta/title", "validate-prediction") },
            overflows.Select(o => ((string)o!["subject"]!["path"]!, (string)o["detection"]!)));
        Assert.Equal(2241, (int)overflows[0]!["evidence"]!["scrollWidth"]!);
        Assert.Equal(2139, (int)overflows[1]!["evidence"]!["scrollWidth"]!);
        Assert.Equal(4090, (int)overflows[2]!["evidence"]!["estimatedTextWidthPx"]!);
        Assert.Equal(["/nodes/0/label", "/nodes/1/label"], all.Skip(1).Select(d => (string)d!["subject"]!["path"]!));
    }

    // T:487
    [Fact]
    public async Task Finalize_refuses_a_receipt_path_that_aliases_a_gate_sidecar()
    {
        using var w = new Workspace();
        var (input, output, visualReceipt) = (w.At("diagram.json"), w.At("diagram.html"), w.At("diagram.browser-check.json"));
        File.WriteAllText(input, "{}", Utf8);
        File.WriteAllText(visualReceipt, "preserve me", Utf8);
        var runner = new FakeStages(_ => Result(new JsonObject { ["ok"] = true }));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Finalize(new FinalizeRequest("architecture", input, output, w.Root) { ReceiptPath = visualReceipt }, runner));
        Assert.Contains("gate sidecars", error.Message);
        Assert.Empty(runner.Calls);
        Assert.Equal("preserve me", File.ReadAllText(visualReceipt));
    }

    // T:508
    [Fact]
    public async Task Finalize_binds_a_passing_validate_receipt_to_the_unchanged_candidate()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"quality_profile\":\"showcase\"}}";
        File.WriteAllText(input, source, Utf8);
        var candidate = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(source)));
        var runner = new FakeStages(stage =>
        {
            var delivery = PassingDelivery(input, output, source, artifact: "<!doctype html>");
            if (stage == "deliver") return Result(delivery);
            if (stage == "check") return Result(PassingCheck(output, delivery["artifact"], delivery["receiptId"]));
            return Result(PassingBrowserCheck(output, delivery["artifact"], delivery["receiptId"], w.Root));
        });
        var pass = await Finalize(new FinalizeRequest("architecture", input, output, w.Root) { CandidateSha256 = candidate }, runner);
        Assert.Equal(0, pass.ExitCode);
        Assert.Equal(["deliver", "check", "browser-check"], runner.Calls);

        File.WriteAllText(input, source + "\n", Utf8);
        var never = new FakeStages(_ => throw new InvalidOperationException("must not run"));
        var error = await Assert.ThrowsAsync<FinalizeException>(() => Finalize(new FinalizeRequest("architecture", input, w.At("changed.html"), w.Root) { CandidateSha256 = candidate }, never));
        Assert.Equal("finalize/candidate-changed", error.Code);
        Assert.Equal(candidate, (string?)error.Evidence["expectedSha256"]);
    }

    // T:552
    [Theory]
    [InlineData(""), InlineData("not json"), InlineData("{}"), InlineData("\"success\""), InlineData("""{"ok":true,"status":"fail","command":"validate","checks":[]}""")]
    public async Task Finalize_fails_closed_when_a_stage_exits_zero_without_a_valid_passing_receipt(string stdout)
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram-0.json"), w.At("diagram-0.html"));
        File.WriteAllText(input, "{}", Utf8);
        var runner = new FakeStages(_ => new StageResult(0, stdout));
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), runner);
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(["deliver"], runner.Calls);
        Assert.Equal("deliver", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/invalid-stage-receipt", Code(finalized));
    }

    // T:584
    [Fact]
    public async Task Showcase_delivery_warnings_name_the_checker_issue_without_advancing_to_later_gates()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"quality_profile\":\"showcase\"}}";
        File.WriteAllText(input, source, Utf8);
        var issue = Parse("""{"severity":"warning","code":"composition/viewport-height","viewBoxHeight":700,"overflowPx":193,"detail":"[composition/viewport-height] Preserve every node and compact vertical spacing to fit the Reader."}""");
        var runner = new FakeStages(_ =>
        {
            var delivery = PassingDelivery(input, output, source);
            delivery["validation"]!["warnings"] = 1;
            delivery["validation"]!["compositionIssues"] = new JsonArray(issue.DeepClone());
            return Result(delivery);
        });
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), runner);

        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(["deliver"], runner.Calls);
        Assert.Equal("deliver", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("fail", (string?)finalized.Summary["gates"]!["deliver"]);
        Assert.Equal("not-run", (string?)finalized.Summary["gates"]!["check"]);
        var first = finalized.Summary["diagnostics"]![0]!;
        Assert.Equal("composition/viewport-height", (string?)first["code"]);
        Assert.Equal(193, (int)first["evidence"]!["overflowPx"]!);
        Assert.Contains("compact vertical spacing", (string)first["supportedFixes"]![0]!);
        Assert.Equal("warning", (string?)first["evidence"]!["reportedSeverity"]);
        Assert.Equal("composition/viewport-height", (string?)JsonNode.Parse(File.ReadAllText((string)finalized.Summary["evidence"]!["receipt"]!))!["diagnostics"]![0]!["code"]);
    }

    // T:619
    [Fact]
    public async Task Showcase_warning_diagnostic_never_masks_a_mismatched_delivery_artifact()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        File.WriteAllText(input, "{}", Utf8);
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), new FakeStages(_ =>
        {
            var delivery = PassingDelivery(input, output, "{}");
            delivery["validation"]!["warnings"] = 1;
            delivery["validation"]!["compositionIssues"] = JsonNode.Parse("""[{"severity":"warning","code":"composition/viewport-height","detail":"Reduce height."}]""");
            delivery["artifact"] = Identity("another artifact");
            return Result(delivery);
        }));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("finalize/artifact-binding-mismatch", Code(finalized));
    }

    // T:641
    [Fact]
    public async Task Showcase_warning_count_without_matching_issue_details_still_fails_with_a_quality_diagnostic()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        File.WriteAllText(input, "{}", Utf8);
        var runner = new FakeStages(_ =>
        {
            var delivery = PassingDelivery(input, output, "{}", type: "workflow");
            delivery["validation"]!["warnings"] = 1;
            delivery["validation"]!["compositionIssues"] = JsonNode.Parse("""[{"severity":"warning","code":"composition/viewport-height"}]""");
            return Result(delivery);
        });
        var finalized = await Finalize(new FinalizeRequest("workflow", input, output, w.Root), runner);
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(["deliver"], runner.Calls);
        Assert.Equal("finalize/showcase-warnings", Code(finalized));
        Assert.Equal(1, finalized.Summary["diagnostics"]![0]!["evidence"]!["warnings"]!.GetValue<double>());
    }

    // T:664, through Cli.Run in process instead of three node children.
    [Fact]
    public void Public_deliver_and_finalize_cli_propagate_a_real_workflow_viewport_warning()
    {
        using var w = new Workspace();
        var input = w.At("warning.workflow.json");
        File.WriteAllText(input, Js.Stringify(Parse("""
            {"schema_version":1,"diagram_type":"workflow","meta":{"title":"Viewport warning fixture","output":"warning.html","quality_profile":"showcase",
             "viewBox":[1080,780],"legend":{"mode":"hidden"}},"lanes":[{"id":"work","label":"Work"}],
             "nodes":[{"id":"start","lane":"work","col":0,"type":"frontend","label":"Start"},{"id":"finish","lane":"work","col":2,"type":"backend","label":"Finish"}],
             "edges":[{"id":"flow","from":"start","to":"finish"}]}
            """), 2) + "\n", Utf8);
        (int Status, JsonNode Json) Run(string command)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            string[] args = [command, "workflow", input, .. command == "validate" ? Array.Empty<string>() : [w.At("warning.html")], "--quality", "showcase", "--json"];
            var status = Cli.Run(args, new CliIo(stdout, stderr, w.Root, CliServices.Default with { BrowserInspectors = new BrowserEvidenceTests.FakeBrowsers(null, "unavailable") }));
            return (status, JsonNode.Parse(stdout.ToString())!);
        }

        var (validated, validation) = Run("validate");
        Assert.Equal(0, validated);
        Assert.Equal(9, validation["checks"]!.AsArray().Count);
        Assert.All(validation["checks"]!.AsArray(), check => Assert.True((bool)check!["ok"]!));
        AssertJson("""{"errors":0,"warnings":1}""", validation["composition"]!["summary"]);

        var (delivered, delivery) = Run("deliver");
        Assert.Equal(0, delivered);
        Assert.Equal((9, 0, 1, 1), ((int)delivery["validation"]!["checksPassed"]!, (int)delivery["validation"]!["errors"]!, (int)delivery["validation"]!["warnings"]!,
            delivery["validation"]!["compositionIssues"]!.AsArray().Count));
        Assert.Equal("composition/viewport-height", (string?)delivery["validation"]!["compositionIssues"]![0]!["code"]);

        var (finalizedStatus, summary) = Run("finalize");
        Assert.Equal(1, finalizedStatus);
        Assert.Equal("deliver", (string?)summary["failedStage"]);
        Assert.Equal("not-run", (string?)summary["gates"]!["check"]);
        Assert.Equal("composition/viewport-height", (string?)summary["diagnostics"]![0]!["code"]);
        Assert.Equal(validation["composition"]!["issues"]![0]!["overflowPx"]!.GetValue<double>(), summary["diagnostics"]![0]!["evidence"]!["overflowPx"]!.GetValue<double>());
        Assert.Contains("meta.viewBox height", (string)summary["diagnostics"]![0]!["supportedFixes"]![0]!);
        Assert.Equal("composition/viewport-height", (string?)JsonNode.Parse(File.ReadAllText((string)summary["evidence"]!["receipt"]!))!["diagnostics"]![0]!["code"]);
    }

    // T:715
    [Fact]
    public async Task Finalize_rejects_an_interleaved_delivery_whose_check_proves_another_artifact_and_receipt()
    {
        using var w = new Workspace();
        var (input, replacement, output) = (w.At("a.json"), w.At("b.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"A\"}}", replacementSource = "{\"meta\":{\"title\":\"B\"}}";
        File.WriteAllText(input, source, Utf8);
        File.WriteAllText(replacement, replacementSource, Utf8);
        var runner = new FakeStages(stage =>
        {
            if (stage == "deliver") return Result(PassingDelivery(input, output, source, "<!doctype html><title>A</title>", ReceiptId));
            var deliveredB = PassingDelivery(replacement, output, replacementSource, "<!doctype html><title>B</title>", "22222222-2222-4222-8222-222222222222");
            return Result(PassingCheck(output, deliveredB["artifact"], deliveredB["receiptId"]));
        });
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), runner);
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(["deliver", "check"], runner.Calls);
        Assert.Equal("check", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/artifact-binding-mismatch", Code(finalized));
        Assert.Equal(ReceiptId, (string?)finalized.Summary["diagnostics"]![0]!["evidence"]!["expectedDeliveryReceiptId"]);
        Assert.Equal("22222222-2222-4222-8222-222222222222", (string?)finalized.Summary["diagnostics"]![0]!["evidence"]!["actualDeliveryReceiptId"]);
    }

    // A passing deliver -> check -> browser-check sequence; `beforeBrowser` runs as the browser stage answers.
    static FakeStages Passing(string input, string output, string source, string outDir, Action<JsonObject>? beforeBrowser = null, string artifact = "<!doctype html><title>verified</title>")
    {
        JsonObject? delivery = null;
        return new FakeStages(stage =>
        {
            if (stage == "deliver") return Result(delivery = PassingDelivery(input, output, source, artifact));
            if (stage == "check") return Result(PassingCheck(output, delivery!["artifact"], delivery["receiptId"]));
            var browser = PassingBrowserCheck(output, delivery!["artifact"], delivery["receiptId"], outDir);
            beforeBrowser?.Invoke(delivery);
            return Result(browser);
        });
    }

    // T:759
    [Fact]
    public async Task Finalize_verifies_the_artifact_and_delivery_sidecar_after_browser_evidence_completes()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        File.WriteAllText(input, "{\"meta\":{\"title\":\"stable\"}}", Utf8);
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root),
            Passing(input, output, "{\"meta\":{\"title\":\"stable\"}}", w.Root, _ => File.Delete(output)));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("finalize", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/final-artifact-mismatch", Code(finalized));
        AssertJson(Js.Stringify(new JsonObject { ["path"] = output }), finalized.Receipt["artifact"]);
    }

    // T:794: upstream patches fs.readFileSync; here the read seam changes the artifact right after the barrier's first read of it.
    [Fact]
    public async Task A_successful_finalize_receipt_keeps_the_delivery_identity_after_its_final_verification_snapshot()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"stable\"}}";
        File.WriteAllText(input, source, Utf8);
        JsonObject? delivery = null;
        var (armed, mutated) = (false, false);
        var request = new FinalizeRequest("architecture", input, output, w.Root)
        {
            ReadFile = file =>
            {
                var contents = File.ReadAllBytes(file);
                if (armed && !mutated && Path.GetFullPath(file) == output)
                {
                    mutated = true;
                    File.WriteAllText(output, "<!doctype html><title>B</title>", Utf8);
                }
                return contents;
            },
        };
        var finalized = await Finalize(request, Passing(input, output, source, w.Root, d => { delivery = d; armed = true; }, "<!doctype html><title>A</title>"));

        Assert.True(mutated);
        Assert.Equal(0, finalized.ExitCode);
        Assert.True((bool)finalized.Receipt["ok"]!);
        var expected = new JsonObject { ["path"] = output };
        foreach (var (key, value) in delivery!["artifact"]!.AsObject()) expected[key] = value?.DeepClone();
        AssertJson(Js.Stringify(expected), finalized.Receipt["artifact"]);
        Assert.NotEqual(Js.Stringify(delivery["artifact"]), Js.Stringify(Identity(File.ReadAllBytes(output))));
    }

    // T:844
    [Fact]
    public async Task Finalize_rejects_a_delivery_sidecar_replaced_after_the_browser_receipt()
    {
        using var w = new Workspace();
        var (input, replacement, output) = (w.At("diagram.json"), w.At("replacement.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"stable\"}}", replacementSource = "{\"meta\":{\"title\":\"replacement\"}}";
        File.WriteAllText(input, source, Utf8);
        File.WriteAllText(replacement, replacementSource, Utf8);
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), Passing(input, output, source, w.Root, delivery =>
        {
            var replaced = delivery.DeepClone().AsObject();
            replaced["receiptId"] = "22222222-2222-4222-8222-222222222222";
            replaced["input"] = replacement;
            replaced["specification"] = Identity(replacementSource);
            WriteCurrentDelivery(output, replaced);
        }));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("finalize", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/final-artifact-mismatch", Code(finalized));
        Assert.Equal("22222222-2222-4222-8222-222222222222", (string?)finalized.Summary["diagnostics"]![0]!["evidence"]!["currentDeliveryReceiptId"]);
    }

    // T:888
    [Fact]
    public async Task Finalize_rejects_a_delivery_type_mismatch_before_checking_a_different_diagram_contract()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"type\"}}";
        File.WriteAllText(input, source, Utf8);
        var runner = new FakeStages(_ => Result(PassingDelivery(input, output, source, type: "workflow")));
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), runner);
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(["deliver"], runner.Calls);
        Assert.Equal("deliver", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/delivery-type-mismatch", Code(finalized));
        AssertJson("""{"expectedType":"architecture","actualType":"workflow"}""", finalized.Summary["diagnostics"]![0]!["evidence"]);
    }

    // T:916
    [Fact]
    public async Task Finalize_rejects_a_final_delivery_sidecar_whose_type_no_longer_matches_the_requested_diagram()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"type-sidecar\"}}";
        File.WriteAllText(input, source, Utf8);
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), Passing(input, output, source, w.Root, delivery =>
        {
            var retyped = delivery.DeepClone().AsObject();
            retyped["type"] = "workflow";
            WriteCurrentDelivery(output, retyped);
        }));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("finalize", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/final-artifact-mismatch", Code(finalized));
        Assert.Equal("workflow", (string?)finalized.Summary["diagnostics"]![0]!["evidence"]!["currentType"]);
    }

    // T:950
    [Fact]
    public async Task Finalize_rejects_a_showcase_delivery_whose_count_does_not_match_the_complete_artifact_checker()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"incomplete\"}}";
        File.WriteAllText(input, source, Utf8);
        JsonObject? delivery = null;
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), new FakeStages(stage =>
        {
            if (stage != "deliver") return Result(PassingCheck(output, delivery!["artifact"], delivery["receiptId"], checkCount: 9));
            delivery = PassingDelivery(input, output, source);
            delivery["validation"]!["checksPassed"] = 4;
            delivery["validation"]!["checkCount"] = 4;
            return Result(delivery);
        }));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("check", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/delivery-validation-mismatch", Code(finalized));
        AssertJson("""{"deliveryCheckCount":4,"checkerCheckCount":9}""", finalized.Summary["diagnostics"]![0]!["evidence"]);
    }

    // T:984
    [Theory, InlineData("check"), InlineData("browser-check")]
    public async Task Finalize_requires_the_canonical_check_and_browser_artifact_paths_without_inventing_fallbacks(string failing)
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"path\"}}";
        File.WriteAllText(input, source, Utf8);
        JsonObject? delivery = null;
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), new FakeStages(stage =>
        {
            if (stage == "deliver") return Result(delivery = PassingDelivery(input, output, source));
            if (stage == "check")
            {
                var check = PassingCheck(output, delivery!["artifact"], delivery["receiptId"]);
                if (failing == "check") check.Remove("file");
                return Result(check);
            }
            var browser = PassingBrowserCheck(output, delivery!["artifact"], delivery["receiptId"], w.Root);
            browser["artifact"]!.AsObject().Remove("path");
            return Result(browser);
        }));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(failing, (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/artifact-binding-mismatch", Code(finalized));
    }

    // T:1038
    public static TheoryData<string> BrowserCoverageCases() =>
    [
        "missing containment viewport", "null containment viewport before a valid matching entry", "failed readability child", "non-light containment viewport",
        "missing viewer chrome viewport", "incomplete theme coverage", "missing intermediate light-theme coverage",
        "mismatched intermediate light theme despite passing parent status",
    ];

    [Theory, MemberData(nameof(BrowserCoverageCases))]
    public async Task Finalize_requires_complete_passing_browser_evidence_coverage(string name)
    {
        void Mutate(JsonObject receipt)
        {
            var containment = receipt["containment"]!["viewports"]!.AsArray();
            var themes = receipt["themeStates"]!["viewports"]!.AsArray();
            switch (name)
            {
                case "missing containment viewport": containment.RemoveAt(containment.Count - 1); break;
                case "null containment viewport before a valid matching entry":
                    containment.RemoveAt(containment.Count - 1);
                    containment.Insert(0, null);
                    break;
                case "failed readability child": receipt["readability"]!["viewports"]![0]!["readabilityOk"] = false; break;
                case "non-light containment viewport": containment[1]!["theme"] = "dark"; break;
                case "missing viewer chrome viewport": receipt["viewerChrome"]!["viewports"] = new JsonArray(); break;
                case "incomplete theme coverage": themes.RemoveAt(themes.Count - 1); break;
                case "missing intermediate light-theme coverage":
                    receipt["themeStates"]!["viewports"] = new JsonArray([.. themes.Where(t => (int)t!["width"]! != 1600).Select(t => t!.DeepClone())]);
                    break;
                default: themes.First(t => (int)t!["width"]! == 1920)!["resolvedTheme"] = "dark"; break;
            }
        }
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"browser\"}}";
        File.WriteAllText(input, source, Utf8);
        JsonObject? delivery = null;
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), new FakeStages(stage =>
        {
            if (stage == "deliver") return Result(delivery = PassingDelivery(input, output, source));
            if (stage == "check") return Result(PassingCheck(output, delivery!["artifact"], delivery["receiptId"]));
            var browser = PassingBrowserCheck(output, delivery!["artifact"], delivery["receiptId"], w.Root);
            Mutate(browser);
            return Result(browser);
        }));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("browser-check", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("finalize/invalid-stage-receipt", Code(finalized));
    }

    // T:1112
    [Theory]
    [InlineData("standard validation reported for a showcase delivery", "deliver")]
    [InlineData("empty artifact checker receipt", "check")]
    [InlineData("artifact checker child failure", "check")]
    [InlineData("minimal browser receipt", "browser-check")]
    public async Task Finalize_rejects_incomplete_or_mismatched_successful_protocol_receipts(string name, string failing)
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        const string source = "{\"meta\":{\"title\":\"protocol\"}}";
        File.WriteAllText(input, source, Utf8);
        JsonObject? delivery = null;
        JsonObject Scenario() => name switch
        {
            "standard validation reported for a showcase delivery" => Standard(),
            "empty artifact checker receipt" => Parse("""{"ok":true,"artifact":{},"checks":[],"provenance":"current"}"""),
            "artifact checker child failure" => ChildFailure(),
            _ => Parse("""{"ok":true,"command":"browser-check","status":"pass"}"""),
        };
        JsonObject Standard()
        {
            var receipt = PassingDelivery(input, output, source, quality: "standard");
            receipt["validation"]!["checksPassed"] = 4;
            receipt["validation"]!["checkCount"] = 4;
            return receipt;
        }
        JsonObject ChildFailure()
        {
            var receipt = PassingCheck(output, delivery!["artifact"], delivery["receiptId"]);
            receipt["checks"] = JsonNode.Parse("""[{"name":"artifact","ok":false}]""");
            return receipt;
        }
        var runner = new FakeStages(stage =>
        {
            if (stage == "deliver")
            {
                delivery = PassingDelivery(input, output, source);
                return Result(failing == stage ? Scenario() : delivery);
            }
            if (stage == "check") return Result(failing == stage ? Scenario() : PassingCheck(output, delivery!["artifact"], delivery["receiptId"]));
            return Result(Scenario());
        });
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), runner);
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(failing, (string?)finalized.Receipt["failedStage"]);
        Assert.Equal(FinalizeGate.Stages.Skip(1).Take(FinalizeGate.Stages.ToList().IndexOf(failing)), runner.Calls);
        Assert.Equal("finalize/invalid-stage-receipt", Code(finalized));
    }

    // T:1180
    [Theory, InlineData("pending-delivery"), InlineData("changed-candidate")]
    public async Task Finalize_rechecks_delivery_barriers_and_the_frozen_candidate_after_the_browser_gate(string scenario)
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        File.WriteAllText(input, "{}", Utf8);
        var request = new FinalizeRequest("architecture", input, output, w.Root);
        if (scenario == "pending-delivery")
            request = request with
            {
                InspectDelivery = _ => new Provenance(false, "pending", null,
                    new Diagnostic("delivery/provenance-pending", Severity.Error, "A new delivery is pending.", new JsonObject(), new JsonObject(), [], null)),
            };
        var finalized = await Finalize(request, Passing(input, output, "{}", w.Root, _ =>
        {
            if (scenario == "changed-candidate") File.WriteAllText(input, "{\"changed\":true}", Utf8);
        }));
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("finalize", (string?)finalized.Summary["failedStage"]);
        Assert.False((bool)finalized.Summary["ok"]!);
        Assert.Equal(scenario == "pending-delivery" ? "delivery/provenance-pending" : "finalize/candidate-changed", Code(finalized));
    }

    // T:1212, through Cli.Run in process.
    [Fact]
    public void Default_finalize_json_preserves_complete_verified_workflow_repairs()
    {
        using var w = new Workspace();
        var (input, output, receipt) = (w.At("shared-endpoint.json"), w.At("shared-endpoint.html"), w.At("receipt.json"));
        var source = Parse("""
            {"schema_version":2,"diagram_type":"workflow","meta":{"title":"Shared missing endpoint","output":"shared-endpoint.html","legend":{"mode":"hidden"}},
             "lanes":[{"id":"main","label":"M"}],
             "nodes":[{"id":"a","lane":"main","col":0,"type":"backend","label":"A"},{"id":"b","lane":"main","col":1,"type":"backend","label":"B"},
                      {"id":"c","lane":"main","col":2,"type":"backend","label":"C"}],
             "edges":[{"id":"ab","from":"a","to":"ghost"},{"id":"bc","from":"ghost","to":"c"}],"mainPath":["a","ghost","c"]}
            """);
        File.WriteAllText(input, Js.Stringify(source), Utf8);
        (int Status, string Stdout) Run(params string[] args)
        {
            var stdout = new StringWriter();
            var status = Cli.Run(args, new CliIo(stdout, new StringWriter(), w.Root, CliServices.Default with { BrowserInspectors = new BrowserEvidenceTests.FakeBrowsers(null, "unavailable") }));
            return (status, stdout.ToString());
        }
        var (finalizedStatus, stdout) = Run("finalize", "workflow", input, output, "--quality", "standard", "--json", "--receipt", receipt);
        Assert.Equal(1, finalizedStatus);
        var summary = JsonNode.Parse(stdout)!;
        var full = JsonNode.Parse(File.ReadAllText(receipt))!;
        var diagnostic = summary["diagnostics"]!.AsArray().First(d => (string?)d!["code"] == "workflow/unknown-edge-endpoint")!;
        Assert.Equal(Js.Stringify(full["diagnostics"]!.AsArray().First(d => (string?)d!["code"] == "workflow/unknown-edge-endpoint")!["supportedFixes"]), Js.Stringify(diagnostic["supportedFixes"]));
        var candidates = new Dictionary<string, List<string>>();
        foreach (var fix in diagnostic["supportedFixes"]!.AsArray().Select(f => f!.GetValue<string>()))
        {
            var match = System.Text.RegularExpressions.Regex.Match(fix, "^set (/\\S+) to verified node id \"([^\"]+)\"$");
            Assert.True(match.Success, $"Expected an executable verified repoint: {fix}");
            candidates.TryAdd(match.Groups[2].Value, []);
            candidates[match.Groups[2].Value].Add(match.Groups[1].Value);
        }
        Assert.NotEmpty(candidates);
        foreach (var (candidate, pointers) in candidates)
        {
            Assert.Equal(["/edges/0/to", "/edges/1/from", "/mainPath/1"], pointers.Order(StringComparer.Ordinal));
            var repaired = source.DeepClone().AsObject();
            foreach (var pointer in pointers)
            {
                var segments = pointer[1..].Split('/');
                JsonNode parent = repaired;
                foreach (var segment in segments[..^1]) parent = parent is JsonArray array ? array[int.Parse(segment)]! : parent[segment]!;
                if (parent is JsonArray list) list[int.Parse(segments[^1])] = candidate;
                else parent[segments[^1]] = candidate;
            }
            File.WriteAllText(input, Js.Stringify(repaired), Utf8);
            var (validated, validation) = Run("validate", "workflow", input, "--quality", "standard", "--json");
            Assert.Equal(0, validated);
            Assert.True((bool)JsonNode.Parse(validation)!["ok"]!);
        }
    }

    // T:1335
    [Fact]
    public void Compact_review_and_repair_guidance_follow_the_mode_and_the_failing_diagnostics()
    {
        var lifecycle = FinalizeGate.Compact(Parse("""
            {"ok":true,"type":"lifecycle","stages":{"check":{"receipt":{"composition":{"metrics":{"resolvedCrossovers":1},
             "routeReview":{"crossings":[{"left":{"from":"a","to":"b"},"right":{"from":"c","to":"d"}}],"detours":[]}}}}}}
            """))["visualReviewRecommendation"]!.AsObject();
        Assert.False(lifecycle.ContainsKey("hints"));
        Assert.DoesNotContain("architecture-layout-repair", (string)lifecycle["repair"]!);
        string Failure(string code) => (string)FinalizeGate.Compact(new JsonObject
        {
            ["ok"] = false, ["status"] = "fail", ["failedStage"] = "validate", ["type"] = "architecture",
            ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = code, ["severity"] = "error", ["message"] = code }),
        })["nextAction"]!["constraint"]!;
        Assert.DoesNotContain("architecture-layout-repair", Failure("composition/desktop-readability"));
        Assert.Contains("architecture-layout-repair", Failure("composition/proper-crossing"));
    }

    // Persistence (finalize.mjs:780-789, :961, :1003): `running` before the first stage, again after each passing stage, final at the end;
    // the summary file always the compact form of the receipt beside it.
    [Fact]
    public async Task Finalize_persists_running_then_after_each_passing_stage_then_the_final_receipt()
    {
        using var w = new Workspace();
        var (input, output) = (w.At("diagram.json"), w.At("diagram.html"));
        File.WriteAllText(input, "{}", Utf8);
        var receiptPath = w.At("diagram.finalize.json");
        var seen = new List<string>();
        JsonObject? delivery = null;
        var finalized = await Finalize(new FinalizeRequest("architecture", input, output, w.Root), new FakeStages(stage =>
        {
            var persisted = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
            seen.Add($"{stage}: {persisted["status"]} [{string.Join(",", persisted["stages"]!.AsObject().Select(s => s.Key))}]");
            Assert.Equal(Js.Stringify(FinalizeGate.Compact(persisted), 2) + "\n", File.ReadAllText(w.At("diagram.finalize-summary.json")));
            if (stage == "deliver") return Result(delivery = PassingDelivery(input, output, "{}"));
            if (stage == "check") return Result(PassingCheck(output, delivery!["artifact"], delivery["receiptId"]));
            return Result(PassingBrowserCheck(output, delivery!["artifact"], delivery["receiptId"], w.Root));
        }));
        Assert.Equal(0, finalized.ExitCode);
        Assert.Equal(["deliver: running []", "check: running [validate,deliver]", "browser-check: running [validate,deliver,check]"], seen);
        Assert.Equal(Js.Stringify(finalized.Receipt, 2) + "\n", File.ReadAllText(receiptPath));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", (string)finalized.Receipt["startedAt"]!);
        Assert.Equal(["schemaVersion", "ok", "command", "status", "type", "quality", "startedAt", "specification", "artifact", "stages", "browserCheck",
            "diagnostics", "evidence", "visualReview", "finishedAt", "durationMs"], finalized.Receipt.Select(kv => kv.Key));
        Assert.Equal(["schemaVersion", "ok", "command", "status", "type", "quality", "specification", "artifact", "gates", "diagnostics", "diagnosticSummary",
            "evidence", "visualReview", "durationMs", "browserCheck"], finalized.Summary.Select(kv => kv.Key));
        // ran: the browser-check stage receipt has containment viewports (finalize.mjs:907 at v3.2.0)
        Assert.Equal("""{"requested":true,"ran":true}""", Js.Stringify(finalized.Receipt["browserCheck"]));
    }

    // T:1265 (runFinalize reads ARCHIFY_CHECK_MAX_BUFFER from its env) is not ported: the stages run in process, with no pipe buffer
    // and no environment variable (Q3, D10).

    // --- T/finalize-browser-lifecycle.test.mjs, through the in-process runner: real deliver and check on web-app, a recording browser ---

    // Records the stages run and lets a test answer one stage itself.
    sealed class RecordingStages(List<string> events, Func<string, StageResult?>? answer = null) : IStageRunner
    {
        readonly StageRunner inner = new();

        public async Task<StageResult> RunAsync(string stage, IReadOnlyList<string> args, StageContext context)
        {
            events.Add($"run:{stage}");
            return answer?.Invoke(stage) ?? await inner.RunAsync(stage, args, context);
        }
    }

    // A browser that passes every inspection, recording discovery, launch, inspections and close.
    sealed class RecordingBrowsers(List<string> events, Func<string?>? resolve = null, Func<Task<IBrowserInspector>>? launch = null) : IBrowserInspectorFactory
    {
        readonly BrowserEvidenceTests.FakeBrowsers passing = new(null);

        public string? ResolveExecutable()
        {
            events.Add("discover");
            return resolve is null ? "/fake/chrome" : resolve();
        }

        public async Task<IBrowserInspector> LaunchAsync(string executable)
        {
            events.Add("create");
            return launch is null ? new Recorded(await passing.LaunchAsync(executable), events) : await launch();
        }

        sealed class Recorded(IBrowserInspector inner, List<string> events) : IBrowserInspector
        {
            public Task<ViewportMetrics> InspectAsync(string snapshotPath, int width, int height, string theme, string? screenshotPath)
            {
                events.Add("inspect");
                return inner.InspectAsync(snapshotPath, width, height, theme, screenshotPath);
            }

            public ValueTask DisposeAsync()
            {
                events.Add("close");
                return inner.DisposeAsync();
            }
        }
    }

    static (string Input, string Output, string OutDir) WebApp(Workspace w)
    {
        var input = w.At("web-app.architecture.json");
        File.Copy(Path.Join(CliTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json"), input);
        return (input, w.At("diagram.html"), w.At("evidence"));
    }

    static Task<FinalizeResult> FinalizeInProcess(Workspace w, IStageRunner runner, IBrowserInspectorFactory browsers)
    {
        var (input, output, outDir) = WebApp(w);
        return Finalize(new FinalizeRequest("architecture", input, output, w.Root)
        {
            OutDir = outDir, DeliveryPaths = FinalizeGate.CliDeliveryPaths, InspectDelivery = artifact => DeliveryStore.InspectProvenance(artifact, true),
        }, runner, browsers);
    }

    // A stage that crashes in process prints its stack the way Program.Main does: LF only, as every CLI line (Q2).
    [Fact]
    public async Task A_crashed_stage_leaves_no_carriage_return_in_the_receipt()
    {
        using var w = new Workspace();
        var (input, output, _) = WebApp(w);
        var crashing = CliServices.Default with
        {
            StageRunner = new StageRunner(), BrowserInspectors = new BrowserEvidenceTests.FakeBrowsers(null),
            NewGuid = () => throw new InvalidOperationException("synthetic crash"),
        };
        var finalized = await FinalizeGate.RunAsync(new FinalizeRequest("architecture", input, output, w.Root), crashing);
        Assert.Equal(1, finalized.ExitCode);
        var stderr = finalized.Receipt["stages"]!["deliver"]!["stderr"]!.GetValue<string>();
        Assert.StartsWith("System.InvalidOperationException: synthetic crash\n   at ", stderr);
        Assert.DoesNotContain('\r', stderr);
        Assert.DoesNotContain(@"\r", Js.Stringify(finalized.Receipt));
    }

    // L:152
    [Fact]
    public async Task Finalize_launches_the_browser_only_after_delivery_and_strict_artifact_checks_pass()
    {
        using var w = new Workspace();
        var events = new List<string>();
        var finalized = await FinalizeInProcess(w, new RecordingStages(events), new RecordingBrowsers(events));
        Assert.Equal(0, finalized.ExitCode);
        Assert.Equal("in-process", (string?)finalized.Receipt["stages"]!["browser-check"]!["execution"]);
        Assert.Equal(["run:deliver", "run:check", "run:browser-check", "discover", "create", .. Enumerable.Repeat("inspect", 6), "close"], events);
    }

    // L:195, L:221: a failed deliver or check never discovers, launches or closes a browser.
    [Theory, InlineData("deliver"), InlineData("check")]
    public async Task Finalize_does_not_discover_or_launch_a_browser_when_delivery_or_strict_checks_fail(string failing)
    {
        using var w = new Workspace();
        var events = new List<string>();
        var stages = new RecordingStages(events, stage => stage == failing
            ? Result(new JsonObject { ["ok"] = false, ["command"] = stage, ["diagnostics"] = new JsonArray() }, 1)
            : null);
        var browsers = new RecordingBrowsers(events, launch: () => Task.FromException<IBrowserInspector>(new InvalidOperationException("attach failed")));
        var finalized = await FinalizeInProcess(w, stages, browsers);
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal(failing, (string?)finalized.Receipt["failedStage"]);
        Assert.DoesNotContain("discover", events);
        Assert.DoesNotContain("create", events);
        Assert.DoesNotContain("inspect", events);
        Assert.DoesNotContain("close", events);
    }

    // L:248
    [Theory, InlineData("startup failure", 1, "fail"), InlineData("browser unavailable", 2, "skipped")]
    public async Task Browser_startup_and_discovery_failures_are_reported_by_the_browser_gate(string name, int exitCode, string status)
    {
        using var w = new Workspace();
        var events = new List<string>();
        var browsers = name == "startup failure"
            ? new RecordingBrowsers(events, launch: () => throw new InvalidOperationException("Chrome attach failed"))
            : new RecordingBrowsers(events, resolve: () => null);
        var finalized = await FinalizeInProcess(w, new RecordingStages(events), browsers);
        Assert.Equal(exitCode, finalized.ExitCode);
        Assert.Equal(status, (string?)finalized.Receipt["stages"]!["browser-check"]!["status"]);
        Assert.Equal("browser-check", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal(exitCode == 1, events.Contains("create"));
    }

    // L:299
    [Fact]
    public async Task Browser_discovery_exceptions_persist_terminal_browser_check_and_finalize_failures()
    {
        using var w = new Workspace();
        var events = new List<string>();
        var browsers = new RecordingBrowsers(events, resolve: () => throw new InvalidOperationException("Chrome discovery failed"),
            launch: () => throw new InvalidOperationException("discovery failure must not launch Chrome"));
        var finalized = await FinalizeInProcess(w, new RecordingStages(events), browsers);
        Assert.Equal(1, finalized.ExitCode);
        Assert.Equal("fail", (string?)finalized.Receipt["status"]);
        Assert.Equal("browser-check", (string?)finalized.Receipt["failedStage"]);
        Assert.Equal("fail", (string?)finalized.Receipt["stages"]!["browser-check"]!["status"]);
        Assert.DoesNotContain("create", events);
        var saved = JsonNode.Parse(File.ReadAllText((string)finalized.Receipt["evidence"]!["receipt"]!))!;
        Assert.Equal("fail", (string?)saved["status"]);
        Assert.Equal("browser-check", (string?)saved["failedStage"]);
        var browserFailure = JsonNode.Parse(File.ReadAllText((string)finalized.Receipt["evidence"]!["browserCheckReceipt"]!))!;
        Assert.Equal("fail", (string?)browserFailure["status"]);
        Assert.Equal("Chrome discovery failed", (string?)browserFailure["error"]);
        Assert.Equal("viewer/browser-check-runtime", (string?)browserFailure["diagnostics"]![0]!["code"]);
    }

    // L:328: upstream creates the browser before its callback and must close it when the callback throws. Here the browser is created
    // inside the gate, so a throwing gate leaves none to close: the error propagates and nothing was launched.
    [Fact]
    public async Task A_throwing_browser_gate_propagates_without_launching_a_browser()
    {
        using var w = new Workspace();
        var events = new List<string>();
        var stages = new RecordingStages(events, stage => stage == "browser-check" ? throw new InvalidOperationException("browser callback failed before consume") : null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => FinalizeInProcess(w, stages, new RecordingBrowsers(events)));
        Assert.Equal("browser callback failed before consume", error.Message);
        Assert.DoesNotContain("create", events);
    }

    // P:25, run twice in one workdir: bounded names for a 78-character CJK output, the browser gate skipped, the same receipt both times.
    [Fact]
    public void Finalize_uses_bounded_canonical_delivery_and_evidence_names_for_long_output_components()
    {
        using var w = new Workspace();
        var (input, _, _) = WebApp(w);
        var output = w.At($"{new string('审', 78)}.html");
        JsonNode Run()
        {
            var stdout = new StringWriter();
            var status = Cli.Run(["finalize", "architecture", input, output, "--quality", "showcase", "--json"],
                new CliIo(stdout, new StringWriter(), w.Root, CliServices.Default with { BrowserInspectors = new BrowserEvidenceTests.FakeBrowsers(null, "unavailable") }));
            Assert.Equal(2, status);
            return JsonNode.Parse(stdout.ToString())!;
        }
        var result = Run();
        AssertJson("""{"validate":"pass","deliver":"pass","check":"pass","browser-check":"skipped"}""", result["gates"]);
        Assert.Equal("not-requested", (string?)result["visualReview"]);
        Assert.All(Directory.EnumerateFileSystemEntries(w.Root), entry => Assert.True(Utf8.GetByteCount(Path.GetFileName(entry)) <= 255, entry));
        foreach (var key in new[] { "receipt", "summaryReceipt", "browserCheckReceipt" }) Assert.True(File.Exists((string)result["evidence"]![key]!), key);
        Assert.DoesNotContain(Directory.EnumerateFiles(w.Root), name => name.EndsWith(".png", StringComparison.Ordinal));
        Assert.Equal((string?)result["evidence"]!["receipt"], (string?)Run()["evidence"]!["receipt"]);
    }
}
