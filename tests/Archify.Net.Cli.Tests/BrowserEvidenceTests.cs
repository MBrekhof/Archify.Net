using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Browser;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Paths;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 10: the browser evidence engine (VC:1867-2685) through the IBrowserInspector seam, against receipts that upstream's
// own engine produced with T/visual-check.test.mjs's fakeBrowser (tools/capture-cli-fixtures.mjs, tests/fixtures/cli/browser-engine).
public sealed partial class BrowserEvidenceTests
{
    [GeneratedRegex(@"\.~archify-[0-9a-f]{64}\.")]
    private static partial Regex HashMarker();

    const string ArtifactText = "<!doctype html><html><body>checked artifact</body></html>";
    static readonly UTF8Encoding Utf8 = new(false);

    public static TheoryData<string> EngineFixtures() => [.. CliTestHost.FixtureNames("browser-engine")];

    // Every case: the returned receipt, the receipt file, the contact sheet, the one-line summary, the workdir afterwards, whether the
    // browser launched and the inspections in order (T:323 viewport order, T:3112 six inspections and no screenshots).
    [Theory, MemberData(nameof(EngineFixtures))]
    public async Task Engine_matches_node(string name)
    {
        var fixture = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name + ".json")))!.AsObject();
        var scenario = fixture["scenario"]!.AsObject();
        var command = scenario["command"]!.GetValue<string>();
        var capture = command == "visual-check";
        var work = Directory.CreateTempSubdirectory("archify-browser-").FullName;
        try
        {
            var artifact = Path.Join(work, "diagram.html");
            File.WriteAllText(artifact, ArtifactText, Utf8);
            var outDir = scenario["outDir"] is { } dir ? Path.Join(work, dir.GetValue<string>()) : null;
            var outputs = BrowserEvidence.SidecarPaths(artifact, outDir, command, capture);
            foreach (var step in (scenario["before"]?.AsArray() ?? []).Select(s => s!.AsObject()))
            {
                if (step.ContainsKey("pass"))
                    Assert.Equal(0, (await BrowserEvidence.RunAsync(new BrowserEvidenceRequest(artifact, command, capture) { OutDir = outDir }, new FakeBrowsers(null))).ExitCode);
                else if (step["write"] is { } write) File.WriteAllText(Path.Join(work, write.GetValue<string>()), step["text"]!.GetValue<string>(), Utf8);
                else if (step["mkdir"] is { } mkdir) Directory.CreateDirectory(Path.Join(work, mkdir.GetValue<string>()));
                else if (step["delete"] is { } delete) File.Delete(Path.Join(work, delete.GetValue<string>()));
                else if (step["edit"] is { } edit)
                {
                    var file = Path.Join(work, edit.GetValue<string>());
                    var value = JsonNode.Parse(File.ReadAllText(file, Utf8))!;
                    var keys = step["key"]!.AsArray().Select(k => k!.GetValue<string>()).ToList();
                    var parent = keys[..^1].Aggregate(value, (node, key) => node[key]!).AsObject();
                    if (step.ContainsKey("value")) parent[keys[^1]] = step["value"]!.DeepClone();
                    else parent.Remove(keys[^1]);
                    File.WriteAllText(file, Js.Stringify(value, 2) + "\n", Utf8);
                }
                else throw new InvalidOperationException($"unknown before step {Js.Stringify(step)}");
            }

            if (fixture["throws"] is JsonObject throws)
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => BrowserEvidence.RunAsync(new BrowserEvidenceRequest(artifact, command, capture) { OutDir = outDir },
                    new FakeBrowsers(null)));
                Assert.Equal(throws["message"]!.GetValue<string>(), CliTestHost.Tokenise(error.Message, work));
                Assert.Equal(throws["code"]?.GetValue<string>(), DeliveryStore.SystemCode(error));
                Assert.Equal(Js.Stringify(fixture["entries"]), Js.Stringify(new JsonArray([.. Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories)
                    .Select(entry => Path.GetRelativePath(work, entry)).Order(StringComparer.Ordinal).Select(entry => (JsonNode?)entry)])));
                return;
            }

            var browsers = new FakeBrowsers(scenario["fake"]?.AsObject(), scenario["browser"]?.GetValue<string>());
            BrowserEvidenceResult result;
            if (scenario["persist"] is JsonObject persist)
            {
                var failure = JsonNode.Parse(Js.Stringify(persist).Replace("<WORK>", work.Replace("\\", "\\\\")))!.AsObject();
                result = BrowserEvidence.PersistFailure(artifact, failure, outDir, command, capture);
            }
            else
            {
                var request = new BrowserEvidenceRequest(artifact, command, capture) { OutDir = outDir };
                if (scenario["provenance"] is JsonObject provenance)
                    request = request with { DeliveryProvenance = new Provenance(true, provenance["status"]!.GetValue<string>(), provenance["receiptId"]?.GetValue<string>()) };
                if (scenario["verify"] is JsonObject verify) request = request with { VerifyArtifact = Verifier(verify, outputs) };
                result = await BrowserEvidence.RunAsync(request, browsers);
            }

            string Ours(JsonNode? text) => text is null ? "<none>" : ChromeUnavailableRuling(text.GetValue<string>(), command);
            string Tokenised(string? text) => text is null ? "<none>" : CliTestHost.Tokenise(text, work);
            Assert.Equal(Ours(fixture["receipt"]), Tokenised(Js.Stringify(result.Receipt, 2) + "\n"));
            Assert.Equal(fixture["exit"]!.GetValue<int>(), result.ExitCode);
            Assert.Equal(fixture["launched"]!.GetValue<bool>(), browsers.Launched);
            Assert.Equal(Js.Stringify(fixture["calls"]), Js.Stringify(browsers.Calls));
            Assert.Equal(Ours(fixture["sidecar"]), Tokenised(File.Exists(outputs.Receipt) ? File.ReadAllText(outputs.Receipt, Utf8) : null));
            Assert.Equal(Ours(fixture["contactSheet"]), Tokenised(capture && File.Exists(outputs.ContactSheet) ? File.ReadAllText(outputs.ContactSheet, Utf8) : null));
            Assert.Equal(Ours(fixture["summary"]), Tokenised(Js.Stringify(BrowserReceipt.Summarize(result.Receipt, result.Published))));
            Assert.Equal(Js.Stringify(fixture["entries"]), Js.Stringify(new JsonArray([.. Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(work, entry)).Order(StringComparer.Ordinal).Select(entry => (JsonNode?)entry)])));
            if (result.Published && result.Receipt["captures"]!["contactSheet"] is not null)
                foreach (var screenshot in outputs.Screenshots) Assert.Equal("89504E470D0A1A0A", Convert.ToHexString(File.ReadAllBytes(screenshot.Path)));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Q4: the browser is Playwright's pinned Chromium, so the unavailable path names `archify install-browser`, never upstream's
    // ARCHIFY_CHROME variable (stated deviation; everything else in those receipts is upstream's).
    internal static string ChromeUnavailableRuling(string text, string command) => text
        .Replace("Chrome or Chromium is unavailable. Set ARCHIFY_CHROME to its executable path.", BrowserEvidence.ChromeUnavailableError)
        .Replace($"set ARCHIFY_CHROME to a Chrome or Chromium executable and rerun {command}", BrowserEvidence.ChromeUnavailableFix(command));

    static Action<byte[]> Verifier(JsonObject verify, EvidenceOutputs outputs)
    {
        var calls = 0;
        return bytes =>
        {
            Assert.Equal(ArtifactText, Utf8.GetString(bytes));
            if (++calls != verify["failOnCall"]!.GetValue<int>()) return;
            if (verify["claimScreenshot"] is not null) Directory.CreateDirectory(outputs.Screenshots[0].Path);
            var code = verify["code"]!.GetValue<string>();
            throw new BrowserEvidenceException($"synthetic {verify["status"]} delivery")
            {
                ProvenanceStatus = verify["status"]!.GetValue<string>(),
                Diagnostics = [new Diagnostic(code, Severity.Error, $"synthetic {code}", new JsonObject { ["artifact"] = "synthetic" },
                    new JsonObject { ["synthetic"] = true }, ["synthetic fix"], null)],
            };
        };
    }

    // The capture script's engineBrowser: T/visual-check.test.mjs:72-145 fakeBrowser with declarative `WxH:theme` lists.
    internal sealed class FakeBrowsers(JsonObject? config, string? mode = null) : IBrowserInspectorFactory
    {
        public JsonArray Calls { get; } = [];
        public bool Launched { get; private set; }

        public string? ResolveExecutable() => mode == "unavailable" ? null : "/fake/chrome";

        public Task<IBrowserInspector> LaunchAsync(string executable)
        {
            Launched = true;
            if (mode == "launch-error") throw new InvalidOperationException("synthetic launch failure");
            if (mode == "startup-timeout") throw new BrowserEvidenceException("synthetic startup timeout") { Code = BrowserEvidenceException.StartupTimeout };
            return Task.FromResult<IBrowserInspector>(new FakeInspector(config ?? [], Calls));
        }
    }

    sealed class FakeInspector(JsonObject config, JsonArray calls) : IBrowserInspector
    {
        bool At(string list, string key) => config[list] is JsonArray keys && keys.Any(k => k!.GetValue<string>() == key);

        public Task<ViewportMetrics> InspectAsync(string snapshotPath, int width, int height, string theme, string? screenshotPath)
        {
            var key = $"{width}x{height}:{theme}";
            calls.Add(new JsonObject { ["width"] = width, ["height"] = height, ["theme"] = theme, ["screenshot"] = screenshotPath is not null });
            if (screenshotPath is not null && At("screenshotFailure", key)) throw new InvalidOperationException("synthetic screenshot failure");
            if (screenshotPath is not null)
            {
                using var file = new FileStream(screenshotPath, FileMode.CreateNew);
                file.Write(Convert.FromHexString("89504e470d0a1a0a"));
            }
            bool overflow = At("overflow", key), tall = At("tall", key), readableScroll = At("readableScroll", key), authoredScroll = At("authoredScroll", key),
                unreadable = At("unreadable", key), chromeCollision = At("chromeCollision", key), stageCollision = At("stageCollision", key);
            var dockStageGap = config["stageGap"]?[key]?.GetValue<double>() ?? (stageCollision ? -12 : 10);
            var stageClearanceFailure = stageCollision || dockStageGap < 10;
            var automaticReaderFit = config.ContainsKey("automaticReaderFit") ? config["automaticReaderFit"]?.GetValue<string>() : "intrinsic-height";
            var authoredDiagramType = config.ContainsKey("authoredDiagramType") ? config["authoredDiagramType"]?.GetValue<string>() : "architecture";
            var authoredUnclipped = config["authoredUnclipped"]?.GetValue<bool>() ?? true;
            var metrics = new ViewportMetrics
            {
                InnerWidth = width,
                InnerHeight = height,
                ScrollWidth = width + (overflow ? 1 : 0),
                ScrollHeight = height + (readableScroll || authoredScroll ? 240 : 0) + (tall ? 300 : 0),
                ResolvedTheme = config["resolvedTheme"]?[key]?.GetValue<string>() ?? theme,
                PageComposition = tall ? new JsonObject
                {
                    ["bodyPaddingPx"] = 12, ["headerPx"] = 100, ["diagramChromePx"] = 76, ["svgPx"] = 800, ["cardsPx"] = 212, ["viewBoxHeight"] = 1000,
                } : null,
                ReaderLayout = readableScroll ? "adaptive" : null,
                ReaderOverflow = readableScroll ? "authored" : null,
                ReaderFit = readableScroll ? automaticReaderFit : authoredScroll ? "authored-height" : null,
                DiagramType = authoredScroll ? authoredDiagramType : null,
                DocumentScrollUnclipped = authoredScroll && authoredUnclipped,
                ReaderWidth = 960,
                DiagramWidth = 930,
                ViewBoxWidth = 1300,
                MinimumProjectedNodeTextPx = unreadable ? 5.72 : 6.44,
                MinimumProjectedNodeText = unreadable ? "Compact node" : "Readable node",
                MinimumProjectedNodeTextDetail = unreadable ? "primary" : "context",
                HasLegend = true,
                HasNavigationDock = true,
                LegendDockIntersectionArea = chromeCollision ? 42 : 0,
                DockStageIntersectionArea = stageCollision ? 84 : 0,
                DockStageGap = dockStageGap,
                ViewerChromeRequiredGap = 10,
                ViewerChromeReserve = chromeCollision || stageClearanceFailure ? 0 : 44,
                ViewerChromeActive = !chromeCollision && !stageClearanceFailure,
            };
            if (config["scrollHeightOver"] is { } over) metrics = metrics with { ScrollHeight = height + over.GetValue<double>() };
            if (config["scrollWidthOver"] is { } wider) metrics = metrics with { ScrollWidth = width + wider.GetValue<double>() };
            if (config["lanes"] is JsonArray lanes) metrics = metrics with { WorkflowLanes = lanes.DeepClone().AsArray() };
            return Task.FromResult(metrics);
        }

        public ValueTask DisposeAsync() => config["closeFails"] is not null
            ? throw new BrowserEvidenceException("Temporary directory cleanup failed: locked") { Code = BrowserEvidenceException.TempCleanup, Directory = "/retained/profile" }
            : ValueTask.CompletedTask;
    }

    // evidenceSidecarPaths (VC:190-241) against Node on real directories: beside the artifact, an existing --out-dir and a missing one
    // that path calculation must not create (T:652-797). A name whose --out-dir alias guard hashed in the artifact's absolute path is
    // compared with its hash masked, then must be bounded, distinct from the name beside the artifact, and deterministic.
    [Fact]
    public void Evidence_sidecar_names_match_node()
    {
        var rows = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, "paths", "evidence-sidecar-names.json")))!.AsArray().Select(r => r!.AsObject()).ToList();
        var work = Directory.CreateTempSubdirectory("archify-sidecars-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Join(work, "source"));
            Directory.CreateDirectory(Path.Join(work, "evidence"));
            foreach (var group in rows.GroupBy(r => r["name"]!.GetValue<string>()))
            {
                var file = Path.Join(work, "source", group.Key);
                using (new FileStream(file, FileMode.CreateNew)) { }
                try
                {
                    foreach (var row in group)
                    {
                        var command = row["command"]!.GetValue<string>();
                        var outDir = row["outDir"]?.GetValue<string>() is { } relative ? Path.Join(work, relative) : null;
                        var outputs = BrowserEvidence.SidecarPaths(file, outDir, command, command == "visual-check");
                        Assert.False(Directory.Exists(Path.Join(work, "missing")), "path calculation must not create --out-dir");
                        Assert.Equal(row["directory"]!.GetValue<string>(), CliTestHost.Tokenise(Path.GetDirectoryName(outputs.Receipt)!, work).Replace("<WORK>", "<DIR>"));
                        var pathHashed = row["pathHashed"]!.GetValue<bool>();
                        string Name(string path) => pathHashed ? HashMarker().Replace(Path.GetFileName(path), ".~archify-<hash>.", 1) : Path.GetFileName(path);
                        var names = new JsonObject
                        {
                            ["receipt"] = Name(outputs.Receipt), ["contactSheet"] = Name(outputs.ContactSheet),
                            ["screenshots"] = new JsonArray([.. outputs.Screenshots.Select(s => (JsonNode?)Name(s.Path))]),
                        };
                        Assert.Equal(Js.Stringify(new JsonObject { ["receipt"] = row["receipt"]!.DeepClone(), ["contactSheet"] = row["contactSheet"]!.DeepClone(), ["screenshots"] = row["screenshots"]!.DeepClone() }),
                            Js.Stringify(names));
                        if (!pathHashed) continue;
                        var receipt = Path.GetFileName(outputs.Receipt);
                        Assert.NotEqual(Path.GetFileName(BrowserEvidence.SidecarPaths(file, null, command, command == "visual-check").Receipt), receipt);
                        Assert.True(Encoding.UTF8.GetByteCount(receipt) <= 255 && receipt.Length <= 255);
                        Assert.Equal(outputs.Receipt, BrowserEvidence.SidecarPaths(file, outDir, command, command == "visual-check").Receipt);
                    }
                }
                finally
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // T/visual-check.test.mjs:670-769: under a target directory that folds case and normalisation while the source directory does
    // not, source-distinct artifacts keep distinct evidence names, deterministically, and creating --out-dir renames nothing.
    [Fact]
    public void Sidecar_names_stay_distinct_under_a_folding_target_directory()
    {
        var work = Directory.CreateTempSubdirectory("archify-semantics-").FullName;
        try
        {
            var sourceDir = Directory.CreateDirectory(Path.Join(work, "semantic-source-directory")).FullName;
            var outDir = Directory.CreateDirectory(Path.Join(work, "semantic-target-directory")).FullName;
            var semantics = new PathSemantics(new FoldingTargetFs(NativeFs.RealPath(sourceDir), NativeFs.RealPath(outDir)));
            string[] artifacts = [.. new[] { "Report.html", "report.html", "Café.html", "Café.html" }.Select(name => Path.Join(sourceDir, name))];
            EvidenceOutputs Calculate(string file, string target)
            {
                using (new FileStream(file, FileMode.CreateNew)) { }
                try { return BrowserEvidence.SidecarPaths(file, target, "visual-check", true, semantics); }
                finally { File.Delete(file); }
            }

            var receipts = artifacts.Select(file => Calculate(file, outDir).Receipt).ToList();
            var names = receipts.Select(Path.GetFileName).ToList();
            Assert.NotEqual(FoldingTargetFs.Key(names[0]!), FoldingTargetFs.Key(names[1]!));
            Assert.NotEqual(FoldingTargetFs.Key(names[2]!), FoldingTargetFs.Key(names[3]!));
            Assert.Equal(receipts, artifacts.Select(file => Calculate(file, outDir).Receipt));

            var futureOutDir = Path.Join(work, "future-semantic-target-directory");
            var beforeCreation = Calculate(artifacts[0], futureOutDir);
            Directory.CreateDirectory(futureOutDir);
            var afterCreation = Calculate(artifacts[0], futureOutDir);
            Assert.Equal(Js.Stringify(Describe(beforeCreation)), Js.Stringify(Describe(afterCreation)));

            foreach (var (receipt, file) in receipts.Zip(artifacts))
            {
                Assert.StartsWith(Path.GetFileNameWithoutExtension(file), Path.GetFileName(receipt), StringComparison.Ordinal);
                Assert.EndsWith(".visual-check.json", receipt, StringComparison.Ordinal);
                Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(receipt)) <= 255);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    static JsonObject Describe(EvidenceOutputs outputs) => new()
    {
        ["receipt"] = outputs.Receipt, ["contactSheet"] = outputs.ContactSheet,
        ["screenshots"] = new JsonArray([.. outputs.Screenshots.Select(s => (JsonNode?)s.Path)]),
    };

    // The upstream test's fs mocks: probes created in the target directory are found again under its semantic key (case and
    // normalisation folded, as targetSemanticKey does), and a probe name in the source directory that was not created is ENOENT.
    sealed class FoldingTargetFs(string sourcePhysical, string outPhysical) : IPathFs
    {
        readonly Dictionary<string, string> targetEntries = [];
        readonly HashSet<string> sourceProbes = [];

        public static string Key(string name) =>
            Js.ToLowerCase(Js.ToUpperCase(name.Normalize(NormalizationForm.FormC))).Normalize(NormalizationForm.FormC);

        static bool IsProbe(string path) => Path.GetFileName(path).StartsWith(".archify-path-semantics-", StringComparison.Ordinal);

        string? Alias(string path) => Path.GetDirectoryName(path) == outPhysical ? targetEntries.GetValueOrDefault(Key(Path.GetFileName(path))) : null;

        public IDisposable CreateExclusive(string path)
        {
            var handle = NativeFs.Ops.CreateExclusive(path);
            if (IsProbe(path) && Path.GetDirectoryName(path) == outPhysical) targetEntries[Key(Path.GetFileName(path))] = path;
            else if (IsProbe(path) && Path.GetDirectoryName(path) == sourcePhysical) sourceProbes.Add(path);
            return handle;
        }

        public FsStat Stat(string path)
        {
            if (Path.GetDirectoryName(path) == sourcePhysical && IsProbe(path) && !sourceProbes.Contains(path))
                throw new NativeFsException("ENOENT", "simulated case- and normalization-sensitive lookup");
            return NativeFs.Ops.Stat(Alias(path) ?? path);
        }

        public string RealPath(string path) => NativeFs.Ops.RealPath(Alias(path) ?? path);
        public FsStat LStat(string path) => NativeFs.Ops.LStat(path);
        public string ReadLink(string path) => NativeFs.Ops.ReadLink(path);
        public void Delete(string path) => NativeFs.Ops.Delete(path);
    }

    // T/visual-check.test.mjs:2643-2663, assertion for assertion.
    [Fact]
    public void Vertical_overflow_fixes_state_the_stacked_page_budget_and_the_actionable_target()
    {
        static Observation Entry(double scrollHeight, string? readerLayout, bool composition = true) => Observation.From(1440, 900, "light", new ViewportMetrics
        {
            InnerWidth = 1440, InnerHeight = 900, ScrollWidth = 1440, ScrollHeight = scrollHeight, DiagramWidth = 930, ReaderLayout = readerLayout,
            ReaderOverflow = readerLayout is null ? null : "authored",
            PageComposition = composition ? new JsonObject { ["bodyPaddingPx"] = 12, ["headerPx"] = 100, ["diagramChromePx"] = 76, ["svgPx"] = 800, ["cardsPx"] = 212, ["viewBoxHeight"] = 1000 } : null,
        });

        var atMinimum = Observation.VerticalBudgetFixes(Entry(1200, "adaptive"));
        Assert.Contains("300px too tall", atMinimum[0]);
        Assert.Contains("12px body padding + 100px header + 76px diagram chrome + 800px SVG + 212px cards = 1200px against 900px", atMinimum[0]);
        Assert.Contains("reduce the viewBox height to at most 625 (from 1000)", atMinimum[0]);
        Assert.Single(atMinimum);

        var fullWidth = Observation.VerticalBudgetFixes(Entry(1200, null));
        Assert.Contains("viewBox ratio is below 1.55", fullWidth[0]);
        Assert.Contains("1.55x wider than tall", fullWidth[0]);

        Assert.Contains("cards take at most 112px", Observation.VerticalBudgetFixes(Entry(1000, "adaptive"))[1]);
        Assert.Empty(Observation.VerticalBudgetFixes(Entry(1200, "adaptive", composition: false)));
        Assert.Empty(Observation.VerticalBudgetFixes(Entry(900, "adaptive")));
    }

    // T/visual-check.test.mjs:33-62: the summary indexes every captured viewport of a published receipt and only that.
    [Fact]
    public async Task Summary_indexes_published_captures_and_preserves_failed_diagnostics()
    {
        var work = Directory.CreateTempSubdirectory("archify-browser-").FullName;
        try
        {
            var artifact = Path.Join(work, "summary-captures.html");
            File.WriteAllText(artifact, ArtifactText, Utf8);
            var result = await BrowserEvidence.RunAsync(new BrowserEvidenceRequest(artifact, "visual-check", true), new FakeBrowsers(null));
            var summary = BrowserReceipt.Summarize(result.Receipt, result.Published);
            Assert.Equal("pending", summary["visualReview"]!.GetValue<string>());
            var screenshots = summary["evidence"]!["screenshots"]!.AsArray();
            Assert.Equal(4, screenshots.Count);
            Assert.All(screenshots, s => Assert.True(Path.IsPathFullyQualified(s!["path"]!.GetValue<string>()) && File.Exists(s["path"]!.GetValue<string>())));
            Assert.True(File.Exists(summary["evidence"]!["receipt"]!.GetValue<string>()));
            Assert.True(File.Exists(summary["evidence"]!["contactSheet"]!.GetValue<string>()));
            Assert.True(Js.Stringify(summary).Length < Js.Stringify(result.Receipt).Length / 2);

            var diagnostic = new JsonObject { ["code"] = "viewer/example", ["severity"] = "error", ["evidence"] = new JsonObject { ["gap"] = 3 }, ["supportedFixes"] = new JsonArray("reposition") };
            var failedReceipt = result.Receipt.DeepClone().AsObject();
            failedReceipt["ok"] = false;
            failedReceipt["status"] = "fail";
            failedReceipt["diagnostics"] = new JsonArray(diagnostic.DeepClone());
            var failed = BrowserReceipt.Summarize(failedReceipt, published: false);
            Assert.False(failed["ok"]!.GetValue<bool>());
            Assert.Equal("fail", failed["status"]!.GetValue<string>());
            Assert.Equal(Js.Stringify(new JsonArray(diagnostic)), Js.Stringify(failed["diagnostics"]));
            Assert.False(failed["evidence"]!.AsObject().ContainsKey("receipt"));
            Assert.False(failed["evidence"]!.AsObject().ContainsKey("contactSheet"));
            Assert.Empty(failed["evidence"]!["screenshots"]!.AsArray());
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // runBrowserEvidence's input refusals (VC:2394-2408), message and error.code printed by Node v24 against upstream: they escape the
    // engine for the CLI to report as viewer/<command>-input.
    [Fact]
    public async Task Input_failures_escape_with_upstreams_message_and_code()
    {
        var work = Directory.CreateTempSubdirectory("archify-browser-").FullName;
        try
        {
            var browsers = new FakeBrowsers(null);
            var text = await Assert.ThrowsAsync<BrowserEvidenceException>(() => BrowserEvidence.RunAsync(new BrowserEvidenceRequest(Path.Join(work, "x.txt"), "browser-check", false), browsers));
            Assert.Equal(("browser-check requires an .html artifact.", (string?)null), (text.Message, text.Code));
            var missing = await Assert.ThrowsAsync<BrowserEvidenceException>(() => BrowserEvidence.RunAsync(new BrowserEvidenceRequest(Path.Join(work, "missing.html"), "browser-check", false), browsers));
            Assert.Equal(("The visual-check artifact could not be read safely (visual-check-artifact-inspection-failed).", "ENOENT"), (missing.Message, missing.Code));
            Directory.CreateDirectory(Path.Join(work, "dir.html"));
            var directory = await Assert.ThrowsAsync<BrowserEvidenceException>(() => BrowserEvidence.RunAsync(new BrowserEvidenceRequest(Path.Join(work, "dir.html"), "visual-check", true), browsers));
            Assert.Equal(("The visual-check artifact could not be read safely (visual-check-artifact-not-regular-file).", (string?)null), (directory.Message, directory.Code));
            Assert.False(browsers.Launched);
            Assert.Equal(["dir.html"], Directory.GetFileSystemEntries(work).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // executeBrowserEvidence's verifyArtifact (A:5571-5585) reuses check's verifyDeliveryUnchanged: the bytes the engine inspected must
    // still be the delivered artifact.
    [Fact]
    public void The_delivery_verifier_is_the_provenance_gate()
    {
        var work = Directory.CreateTempSubdirectory("archify-browser-").FullName;
        try
        {
            File.Copy(Path.Join(CliTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json"), Path.Join(work, "spec.json"));
            Assert.Equal(0, Cli.Run(["deliver", "architecture", "spec.json", "out.html", "--json"], new CliIo(new StringWriter(), new StringWriter(), work, CliServices.Default)));
            var artifact = Path.Join(work, "out.html");
            var provenance = DeliveryStore.InspectProvenance(artifact, requireProvenance: true);
            Assert.Equal((true, "current"), (provenance.Ok, provenance.Status));
            var verify = BrowserEvidence.DeliveryVerifier(artifact, provenance);
            var bytes = File.ReadAllBytes(artifact);
            verify(bytes);

            var other = bytes.Append((byte)'\n').ToArray();
            var mismatch = Assert.Throws<BrowserEvidenceException>(() => verify(other));
            Assert.Equal("mismatch", mismatch.ProvenanceStatus);
            var diagnostic = Assert.Single(mismatch.Diagnostics!);
            Assert.Equal(("delivery/provenance-mismatch", "The artifact or delivery record changed during inspection."), (diagnostic.Code, diagnostic.Message));
            Assert.Equal(diagnostic.Message, mismatch.Message);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(other)), diagnostic.Evidence["checked"]!["sha256"]!.GetValue<string>());

            File.AppendAllText(artifact, "\n");
            var changed = Assert.Throws<BrowserEvidenceException>(() => verify(bytes));
            Assert.Equal("input/artifact-unreadable", Assert.Single(changed.Diagnostics!).Code);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Q4: a missing browser is a skipped, warning-only receipt whose one fix is the install command, ready to paste.
    [Fact]
    public void Chrome_unavailable_names_the_install_command()
    {
        Assert.Contains("archify install-browser", BrowserEvidence.ChromeUnavailableError);
        Assert.Contains("archify install-browser", BrowserEvidence.ChromeUnavailableFix("browser-check"));
        Assert.EndsWith("rerun browser-check", BrowserEvidence.ChromeUnavailableFix("browser-check"));
    }
}
