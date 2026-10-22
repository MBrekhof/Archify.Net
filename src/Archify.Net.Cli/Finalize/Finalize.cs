using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Browser;
using Archify.Net.Cli.Commands;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Paths;
using static Archify.Net.Cli.Receipts;

namespace Archify.Net.Cli.Finalize;

// finalize's stage seam (Q2): upstream's runCommand and runBrowserCheck (finalize.mjs:486-497, :820-856). One stage, with the argv
// stageArguments built; the answer is what a child process gives back.
public interface IStageRunner
{
    Task<StageResult> RunAsync(string stage, IReadOnlyList<string> args, StageContext context);
}

// The run a stage belongs to: the cwd and seams of the finalize command, the resolved artifact and --out-dir.
public sealed record StageContext(string Cwd, CliServices Services, string Output, string? OutDir);

// A child's {status, stdout, stderr}. Error is execFile's error.message, set for a non-zero exit; InProcess marks the browser gate
// run in this process (upstream's `execution: 'in-process'`).
public sealed record StageResult(int Status, string Stdout, string Stderr = "")
{
    public string? Error { get; init; }
    public bool InProcess { get; init; }
}

// The default runner: deliver and check through Cli.Run in this process (D8), browser-check through Task 11's browser gate with the
// browser resolved only now, after deliver and check passed (finalize.mjs:794-856).
public sealed class StageRunner : IStageRunner
{
    public async Task<StageResult> RunAsync(string stage, IReadOnlyList<string> args, StageContext context)
    {
        if (stage == "browser-check") return await BrowserCheck(context);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        int status;
        try { status = Cli.Run([.. args], new CliIo(stdout, stderr, context.Cwd, context.Services)); }
        catch (Exception error)
        {
            // a child that crashes: Program.Main prints the stack and exits 1
            stderr.Write(error.ToString().ReplaceLineEndings("\n") + "\n");
            status = 1;
        }
        var err = stderr.ToString();
        return new StageResult(status, stdout.ToString(), err)
        {
            // execFile's `Command failed: <file> <args>\n<stderr>`, the command being ["archify", …args] (D8)
            Error = status == 0 ? null : $"Command failed: {string.Join(" ", ["archify", .. args])}\n{err}",
        };
    }

    static async Task<StageResult> BrowserCheck(StageContext context)
    {
        var browsers = context.Services.BrowserInspectors;
        string? executable;
        try { executable = browsers.ResolveExecutable(); }
        catch (Exception error)
        {
            // A discovery error has no executable to launch: a failed gate, not a missing browser (finalize.mjs:834-848).
            var failure = new JsonObject
            {
                ["schemaVersion"] = 1, ["ok"] = false, ["command"] = "browser-check", ["status"] = "fail", ["evidenceKind"] = "automated-browser",
                ["visualReview"] = "not-requested", ["error"] = error.Message,
                ["diagnostics"] = new JsonArray(BrowserReceipt.Failure("viewer/browser-check-runtime", "browser-check could not discover Chrome.",
                    new JsonObject { ["artifact"] = context.Output }, new JsonObject { ["reason"] = error.Message },
                    ["resolve the reported Chrome discovery error, then rerun browser-check"]).ToJson()),
            };
            var persisted = BrowserEvidence.PersistFailure(context.Output, failure, context.OutDir, "browser-check", capture: false);
            return new StageResult(1, Js.Stringify(persisted.Receipt)) { InProcess = true };
        }
        var (result, _) = await BrowserEvidenceCommand.ExecuteAsync(context.Output, context.OutDir, requireProvenance: true, "browser-check",
            capture: false, new Resolved(browsers, executable));
        return new StageResult(result.ExitCode, Js.Stringify(result.Receipt)) { InProcess = true };
    }

    // `resolveChrome: () => chromePath`: the browser found once, launched by the gate.
    sealed class Resolved(IBrowserInspectorFactory inner, string? executable) : IBrowserInspectorFactory
    {
        public string? ResolveExecutable() => executable;
        public Task<IBrowserInspector> LaunchAsync(string path) => inner.LaunchAsync(path);
    }
}

// The four paths of a delivery that finalize's receipts must not replace (finalize.mjs:530-544).
internal sealed record DeliveryPaths(string Provenance, string Pending, string Lock, string DirectoryLock)
{
    public IEnumerable<string> All => [Provenance, Pending, Lock, DirectoryLock];
}

// runFinalize's options. The defaults are runFinalize's own; the finalize command passes the CLI's delivery paths and provenance
// inspection (A:5814-5828). ReadFile is the read behind every identity and the final provenance read (T:794 patches fs.readFileSync).
internal sealed record FinalizeRequest(string Type, string Input, string Output, string Cwd)
{
    public string Quality { get; init; } = "showcase";
    public string? RepoRoot { get; init; }
    public string? CandidateSha256 { get; init; }
    public string? OutDir { get; init; }
    public string? ReceiptPath { get; init; }
    public Func<string, DeliveryPaths> DeliveryPaths { get; init; } = FinalizeGate.DefaultDeliveryPaths;
    public Func<string, Provenance>? InspectDelivery { get; init; }
    public Func<string, byte[]> ReadFile { get; init; } = File.ReadAllBytes;
}

internal sealed record FinalizeResult(int ExitCode, JsonObject Receipt, JsonObject Summary);

// An error carrying upstream's finalizeCode and finalizeEvidence (finalize.mjs:733-742).
internal sealed class FinalizeException(string message, string code, JsonObject evidence) : Exception(message)
{
    public string Code { get; } = code;
    public JsonObject Evidence { get; } = evidence;
}

// UP/bin/finalize.mjs: runFinalize, compactFinalizeReceipt and the receipt paths. Stage receipts are parsed JSON read with JS
// semantics: a missing key is undefined, `===` compares a type and a value, `x || y` and `x ? :` follow JS truthiness. After the cut
// (Q5 D1): receipts are written through DeliveryStore.PublishFile without atomic-output's capture and hard-link checks, and the
// `update` block is gone (D7).
internal static partial class FinalizeGate
{
    public static readonly IReadOnlyList<string> Stages = ["validate", "deliver", "check", "browser-check"];
    static readonly UTF8Encoding Utf8 = new(false);
    static OutputPaths Paths => OutputPaths.Native;

    [GeneratedRegex(@"^\[[^\]]+\]\s*")]
    private static partial Regex DetailCode();

    // Route failures call for a connected-scene repair; size and text failures do not (finalize.mjs:569).
    [GeneratedRegex(@"^(clean-flow/|composition/(proper-crossing|ambiguous-corridor|border-run|container-border-run|excessive-route-detour|route-rhythm|arrowhead-collision))")]
    private static partial Regex RouteDiagnostic();

    // --- JS reads ---

    static JsonNode? P(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var value) ? value : null;
    static bool Has(JsonNode? node, string key) => node is JsonObject o && o.ContainsKey(key);
    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;
    static bool Is(JsonNode? node, string text) => Text(node) == text;
    static bool Is(JsonNode? node, double number) => Number(node) == number;
    static bool IsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;
    static bool IsSafeInteger(double value) => IsInteger(value) && Math.Abs(value) <= 9007199254740991;

    // `${value}` of a present value
    static string Template(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "[object Object]",
        JsonArray array => string.Join(",", array.Select(item => item is null ? "" : Template(item))),
        _ when Text(value) is { } text => text,
        _ when Number(value) is { } number => Js.Num(number),
        _ => IsTrue(value) ? "true" : "false",
    };

    // `${node.key}`: undefined when the key is missing
    static string T(JsonNode? node, string key) => Has(node, key) ? Template(P(node, key)) : "undefined";

    // `{ key: source.key }`: the key only when the source has it (JSON.stringify drops undefined)
    static void CopyIfPresent(JsonObject target, string key, JsonNode? source, string? sourceKey = null)
    {
        if (source is JsonObject o && o.TryGetPropertyValue(sourceKey ?? key, out var value)) target[key] = value?.DeepClone();
    }

    // `{ ...target, ...source }`: a key the target has keeps its place
    static JsonObject Spread(JsonObject target, JsonNode? source)
    {
        if (source is JsonObject o) foreach (var (key, value) in o) target[key] = value?.DeepClone();
        return target;
    }

    static JsonArray Items(IEnumerable<JsonNode?> items) => new([.. items.Select(item => item?.DeepClone())]);

    static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // --- receipt validation (finalize.mjs:89-209) ---

    static JsonNode? ParsedReceipt(string stdout)
    {
        var source = Js.Trim(stdout);
        if (source.Length == 0) return null;
        try { return JsonNode.Parse(source); }
        catch (JsonException) { return null; }
    }

    static bool ValidIdentity(JsonNode? value) =>
        value is JsonObject && Text(P(value, "sha256")) is { } sha && Sha256Hex().IsMatch(sha) && Number(P(value, "bytes")) is { } bytes && IsSafeInteger(bytes) && bytes >= 0;

    static bool IdentitiesMatch(JsonNode? left, JsonNode? right) =>
        ValidIdentity(left) && ValidIdentity(right) && Text(P(left, "sha256")) == Text(P(right, "sha256")) && Number(P(left, "bytes")) == Number(P(right, "bytes"));

    static bool ValidReceiptId(JsonNode? value) => Text(value) is { } id && UuidV4().IsMatch(id);

    static bool AllChecksPassed(JsonNode? checks) => checks is JsonArray array && array.Count > 0 && array.All(check => check is JsonObject && IsTrue(P(check, "ok")));

    static bool ValidCheckComposition(JsonNode? receipt, string quality)
    {
        var composition = P(receipt, "composition");
        var summary = P(composition, "summary");
        return composition is JsonObject && Is(P(composition, "schemaVersion"), 1) && Is(P(composition, "profile"), quality) && Is(P(composition, "status"), "pass")
            && Is(P(summary, "errors"), 0) && (quality != "showcase" || Is(P(summary, "warnings"), 0));
    }

    static bool ExactViewportCoverage(JsonNode? entries, IReadOnlyList<(int Width, int Height, string? Theme)> viewports, Func<JsonNode, string?, bool> predicate) =>
        entries is JsonArray array && array.Count == viewports.Count && viewports.All(expected =>
        {
            var matching = array.Where(entry => entry is JsonObject && Is(P(entry, "width"), expected.Width) && Is(P(entry, "height"), expected.Height)
                && (expected.Theme is null || Is(P(entry, "requestedTheme"), expected.Theme))).ToList();
            return matching.Count == 1 && predicate(matching[0]!, expected.Theme);
        });

    static bool ValidBrowserEvidence(JsonNode receipt)
    {
        var light = BrowserEvidence.VisualCheckViewports.Select(v => (v.Width, v.Height, (string?)null)).ToList();
        var themes = BrowserEvidence.VisualCheckViewports.Select(v => (v.Width, v.Height, (string?)"light"))
            .Concat(BrowserEvidence.CaptureViewports.Select(v => (v.Width, v.Height, (string?)"dark"))).ToList();
        return ExactViewportCoverage(P(P(receipt, "containment"), "viewports"), light, (entry, _) => Is(P(entry, "theme"), "light") && IsTrue(P(entry, "ok")))
            && ExactViewportCoverage(P(P(receipt, "readability"), "viewports"), light,
                (entry, _) => Is(P(entry, "theme"), "light") && IsTrue(P(entry, "ok")) && IsTrue(P(entry, "readabilityOk")))
            && ExactViewportCoverage(P(P(receipt, "viewerChrome"), "viewports"), light,
                (entry, _) => Is(P(entry, "theme"), "light") && IsTrue(P(entry, "ok")) && IsTrue(P(entry, "viewerChromeOk")))
            && ExactViewportCoverage(P(P(receipt, "themeStates"), "viewports"), themes,
                (entry, theme) => IsTrue(P(entry, "ok")) && Is(P(entry, "requestedTheme"), theme!) && Is(P(entry, "resolvedTheme"), theme!));
    }

    static bool ValidDeliveryValidation(JsonNode? receipt, string quality, bool allowShowcaseWarnings)
    {
        var validation = P(receipt, "validation");
        return validation is JsonObject && Number(P(validation, "checkCount")) is { } count && IsInteger(count) && count > 0
            && Number(P(validation, "checksPassed")) == count && Is(P(validation, "compositionStatus"), "pass") && Is(P(validation, "errors"), 0)
            && Is(P(validation, "compositionProfile"), quality) && (quality != "showcase" || allowShowcaseWarnings || Is(P(validation, "warnings"), 0));
    }

    static bool ValidStageReceipt(string stage, JsonNode? receipt, string quality, bool allowShowcaseWarnings = false)
    {
        if (receipt is not JsonObject || !IsTrue(P(receipt, "ok"))) return false;
        if (P(receipt, "status").Truthy() && !Is(P(receipt, "status"), "pass")) return false;
        if (stage == "deliver")
            return Is(P(receipt, "command"), "deliver") && Is(P(receipt, "schemaVersion"), 1) && ValidReceiptId(P(receipt, "receiptId"))
                && P(receipt, "specification") is JsonObject && ValidIdentity(P(receipt, "specification")) && ValidIdentity(P(receipt, "artifact"))
                && ValidDeliveryValidation(receipt, quality, allowShowcaseWarnings);
        if (stage == "check")
            return ValidIdentity(P(receipt, "artifact")) && AllChecksPassed(P(receipt, "checks")) && Is(P(receipt, "provenance"), "current")
                && ValidReceiptId(P(receipt, "deliveryReceiptId")) && ValidCheckComposition(receipt, quality);
        return Is(P(receipt, "command"), "browser-check") && Is(P(receipt, "schemaVersion"), 1) && Is(P(receipt, "status"), "pass")
            && Is(P(receipt, "evidenceKind"), "automated-browser") && ValidIdentity(P(receipt, "artifact")) && Is(P(receipt, "provenance"), "current")
            && ValidReceiptId(P(receipt, "deliveryReceiptId")) && Is(P(P(receipt, "containment"), "status"), "pass")
            && Is(P(P(receipt, "themeStates"), "status"), "pass") && Is(P(P(receipt, "readability"), "status"), "pass")
            && Is(P(P(receipt, "viewerChrome"), "status"), "pass") && ValidBrowserEvidence(receipt);
    }

    static string StageStatus(string stage, int exitCode, JsonNode? receipt, string quality) =>
        exitCode == 2 || Is(P(receipt, "status"), "skipped") ? "skipped" : exitCode == 0 && ValidStageReceipt(stage, receipt, quality) ? "pass" : "fail";

    // --- binding diagnostics (finalize.mjs:211-367) ---

    static JsonObject Diagnostic(string code, string message, JsonObject subject, JsonObject evidence, string fix) => new()
    {
        ["code"] = code, ["severity"] = "error", ["message"] = message, ["subject"] = subject, ["evidence"] = evidence, ["supportedFixes"] = new JsonArray(fix),
    };

    // identityMismatchDiagnostic; a null artifact is undefined (dropped)
    static JsonObject IdentityMismatch(string stage, string output, JsonNode? expected, JsonNode? actual, JsonNode? expectedReceiptId, JsonNode? actualReceiptId)
    {
        var evidence = new JsonObject();
        if (expected is not null) evidence["expectedArtifact"] = expected.DeepClone();
        if (actual is not null) evidence["actualArtifact"] = actual.DeepClone();
        if (expectedReceiptId.Truthy()) evidence["expectedDeliveryReceiptId"] = expectedReceiptId!.DeepClone();
        if (actualReceiptId.Truthy()) evidence["actualDeliveryReceiptId"] = actualReceiptId!.DeepClone();
        return Diagnostic("finalize/artifact-binding-mismatch", $"The {stage} receipt does not prove the artifact delivered by this finalize run.",
            new JsonObject { ["stage"] = stage, ["artifact"] = output }, evidence,
            "finish other delivery attempts for this output, then rerun finalize from the frozen candidate");
    }

    static List<JsonNode?> ShowcaseWarningDiagnostics(JsonNode receipt)
    {
        var issues = P(P(receipt, "validation"), "compositionIssues");
        var warnings = Number(P(P(receipt, "validation"), "warnings"))!.Value;
        if (issues is not JsonArray list || list.Count != warnings || !list.All(issue => issue is JsonObject && Is(P(issue, "severity"), "warning")
                && Text(P(issue, "code")) is { } code && code.StartsWith("composition/", StringComparison.Ordinal)
                && Text(P(issue, "detail")) is { } detail && Js.Trim(detail).Length > 0))
        {
            return [Diagnostic("finalize/showcase-warnings",
                $"Showcase delivery reported {Js.Num(warnings)} composition warning(s), but did not provide matching issue details.",
                new JsonObject { ["stage"] = "deliver", ["check"] = "composition" },
                new JsonObject { ["warnings"] = warnings, ["reportedIssues"] = issues is JsonArray array ? array.Count : null },
                "run validate on the frozen candidate to inspect the composition warnings, repair them, then rerun finalize")];
        }
        return [.. list.Select(issue =>
        {
            var code = Text(P(issue, "code"))!;
            var evidence = new JsonObject { ["reportedSeverity"] = P(issue, "severity")!.DeepClone() };
            foreach (var (key, value) in issue!.AsObject())
                if (key is not ("code" or "severity" or "detail")) evidence[key] = value?.DeepClone();
            var subject = new JsonObject { ["stage"] = "deliver", ["check"] = "composition" };
            if (P(issue, "nodeId").Truthy()) subject["nodeId"] = P(issue, "nodeId")!.DeepClone();
            return (JsonNode?)Diagnostic(code, $"Showcase delivery reported {code}; zero warnings are required.", subject, evidence,
                DetailCode().Replace(Text(P(issue, "detail"))!, "", 1));
        })];
    }

    static JsonObject? StageBindingDiagnostic(string stage, JsonNode? receipt, JsonNode? expectedArtifact, JsonNode? expectedReceiptId, string output,
        JsonObject specification, string type, JsonNode? deliveryValidation, Func<string, JsonObject> identity)
    {
        if (stage == "deliver")
        {
            if (!Is(P(receipt, "type"), type))
            {
                var evidence = new JsonObject { ["expectedType"] = type };
                CopyIfPresent(evidence, "actualType", receipt, "type");
                return Diagnostic("finalize/delivery-type-mismatch", "The delivery command reported a different diagram type than finalize requested.",
                    new JsonObject { ["stage"] = "deliver", ["artifact"] = output }, evidence,
                    "rerun finalize with a delivery command for the requested diagram type");
            }
            if (!IdentitiesMatch(P(receipt, "specification"), specification))
            {
                var evidence = new JsonObject();
                CopyIfPresent(evidence, "expectedSha256", specification, "sha256");
                CopyIfPresent(evidence, "actualSha256", P(receipt, "specification"), "sha256");
                CopyIfPresent(evidence, "expectedBytes", specification, "bytes");
                CopyIfPresent(evidence, "actualBytes", P(receipt, "specification"), "bytes");
                return Diagnostic("finalize/candidate-changed-during-delivery", "The delivery command froze a different candidate than finalize started with.",
                    new JsonObject { ["stage"] = "validate", ["candidate"] = specification["path"]!.DeepClone() }, evidence,
                    "restore the frozen candidate and rerun finalize");
            }
            if (Text(P(receipt, "output")) is not { Length: > 0 } recorded || !Paths.PathsAlias(recorded, output) || !IdentitiesMatch(P(receipt, "artifact"), identity(output)))
                return IdentityMismatch(stage, output, P(receipt, "artifact"), identity(output), P(receipt, "receiptId"), null);
            return null;
        }
        var checks = P(receipt, "checks") as JsonArray;
        if (stage == "check" && Number(P(deliveryValidation, "checkCount")) != checks!.Count)
        {
            var evidence = new JsonObject();
            CopyIfPresent(evidence, "deliveryCheckCount", deliveryValidation, "checkCount");
            evidence["checkerCheckCount"] = checks.Count;
            return Diagnostic("finalize/delivery-validation-mismatch",
                "The delivery validation count does not match the complete checker receipt for the delivered artifact.",
                new JsonObject { ["stage"] = "check", ["artifact"] = output }, evidence,
                "restore the complete delivery validation receipt and rerun finalize from the frozen candidate");
        }
        var path = stage == "check" ? Text(P(receipt, "file")) : Text(P(P(receipt, "artifact"), "path"));
        if (path is null || !Paths.PathsAlias(path, output) || !IdentitiesMatch(P(receipt, "artifact"), expectedArtifact)
            || Text(P(receipt, "deliveryReceiptId")) != Text(expectedReceiptId))
            return IdentityMismatch(stage, output, expectedArtifact, P(receipt, "artifact"), expectedReceiptId, P(receipt, "deliveryReceiptId"));
        return null;
    }

    // --- failure diagnostics (finalize.mjs:374-451) ---

    // slimEvidence; returns false for undefined (an absent key)
    static bool Slim(JsonNode? entry, out JsonNode? slim)
    {
        slim = null;
        if (!Has(entry, "evidence")) return false;
        var evidence = P(entry, "evidence");
        if (evidence is not JsonObject o)
        {
            slim = evidence?.DeepClone();
            return true;
        }
        var rest = new JsonObject();
        foreach (var (key, value) in o)
            if (key is not ("innerWidth" or "innerHeight" or "readerLayout" or "readerOverflow" or "readerFit")) rest[key] = value?.DeepClone();
        slim = rest;
        return true;
    }

    // `const { artifact, ...subject } = entry.subject || {}`
    static JsonObject SubjectWithoutArtifact(JsonNode? entry)
    {
        var subject = new JsonObject();
        if (P(entry, "subject") is JsonObject o)
            foreach (var (key, value) in o)
                if (key != "artifact") subject[key] = value?.DeepClone();
        return subject;
    }

    static JsonArray Consolidate(IEnumerable<JsonNode?> diagnostics)
    {
        var result = new List<JsonNode?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var overflow = new List<JsonNode?>();
        var overflowIndex = -1;
        foreach (var diagnostic in diagnostics)
        {
            if (Is(P(diagnostic, "code"), "viewer/viewport-overflow"))
            {
                if (overflowIndex < 0) overflowIndex = result.Count;
                overflow.Add(diagnostic);
                continue;
            }
            if (!seen.Add(T(diagnostic, "code") + T(diagnostic, "message") + Js.Stringify(P(diagnostic, "subject")))) continue;
            result.Add(diagnostic);
        }
        if (overflow.Count > 1)
        {
            var first = overflow[0];
            var viewports = overflow.Select(entry => P(P(entry, "subject"), "viewport")).Where(viewport => viewport.Truthy()).ToList();
            static double Area(JsonNode? viewport) => (Number(P(viewport, "width")) ?? double.NaN) * (Number(P(viewport, "height")) ?? double.NaN);
            Js.Sort(viewports, (a, b) => Area(a) - Area(b));
            var tightest = viewports.FirstOrDefault();
            var fixes = tightest is not null
                ? new JsonArray($"contain the rendered layout within {T(tightest, "width")}x{T(tightest, "height")} (smallest reported viewport), then rerun browser-check")
                : Items(overflow.SelectMany(entry => P(entry, "supportedFixes") switch
                {
                    JsonArray list => list.AsEnumerable(),
                    { } fix when fix.Truthy() => [fix],
                    _ => [],
                }).DistinctBy(fix => Js.Stringify(fix)));
            var subject = new JsonObject();
            CopyIfPresent(subject, "diagramType", P(first, "subject"));
            var merged = Spread(new JsonObject(), first);
            merged["message"] = $"The rendered artifact overflows {overflow.Count} viewport/theme combinations.";
            merged["subject"] = subject;
            merged["supportedFixes"] = fixes;
            merged["evidence"] = new JsonObject
            {
                ["overflows"] = new JsonArray([.. overflow.Select(entry =>
                {
                    var item = new JsonObject
                    {
                        // validate's prediction carries estimatedTextWidthPx; the browser measurement carries scroll geometry
                        ["detection"] = P(P(entry, "evidence"), "estimatedTextWidthPx") is not null ? "validate-prediction" : "browser-measurement",
                        ["subject"] = SubjectWithoutArtifact(entry),
                    };
                    if (Slim(entry, out var slim)) item["evidence"] = slim;
                    return (JsonNode)item;
                })]),
            };
            result.Insert(overflowIndex, merged);
        }
        else if (overflow.Count == 1)
        {
            var entry = Spread(new JsonObject(), overflow[0]);
            entry["subject"] = SubjectWithoutArtifact(overflow[0]);
            if (Slim(overflow[0], out var slim)) entry["evidence"] = slim;
            result.Insert(overflowIndex, entry);
        }
        return Items(result);
    }

    static IEnumerable<JsonNode?> FailureDiagnostics(string stage, StageResult result, JsonNode? receipt, string quality)
    {
        if (P(receipt, "diagnostics") is JsonArray { Count: > 0 } diagnostics) return diagnostics;
        var invalidReceipt = result.Status == 0 && !ValidStageReceipt(stage, receipt, quality);
        var evidence = new JsonObject { ["exitCode"] = result.Status };
        if (!string.IsNullOrEmpty(result.Error)) evidence["reason"] = result.Error;
        foreach (var (key, text) in new[] { ("stdout", result.Stdout), ("stderr", result.Stderr) })
            if (Js.Trim(text) is { Length: > 0 } trimmed) evidence[key] = trimmed[..Math.Min(trimmed.Length, 2000)];
        return [Diagnostic(invalidReceipt ? "finalize/invalid-stage-receipt" : "finalize/stage-failure",
            invalidReceipt ? $"The {stage} stage exited successfully without a valid passing receipt." : $"The {stage} stage did not complete successfully.",
            new JsonObject { ["stage"] = stage }, evidence,
            invalidReceipt ? $"restore the {stage} JSON receipt contract before retrying finalize"
                : "use the compact finalize summary to repair the named subject in place, then rerun finalize once")];
    }

    // --- paths (finalize.mjs:499-544) ---

    static List<string> StageArguments(string stage, string type, string input, string output, string quality, string? repoRoot, string? outDir) => stage switch
    {
        "deliver" => ["deliver", type, input, output, "--quality", quality, .. repoRoot is null ? Array.Empty<string>() : ["--repo-root", repoRoot], "--json"],
        "check" => ["check", output, "--require-provenance"],
        _ => ["browser-check", output, "--json", "--require-provenance", .. outDir is null ? Array.Empty<string>() : ["--out-dir", outDir]],
    };

    // defaultFinalizeReceiptPath: browser evidence's physical artifact namespace, then a stem bounded for both finalize suffixes.
    public static string DefaultReceiptPath(string output, string? outDir)
    {
        var browserReceipt = BrowserEvidence.SidecarPaths(output, outDir, "browser-check", false).Receipt;
        var stem = Path.GetFileName(browserReceipt)[..^".browser-check.json".Length];
        return Path.Join(Path.GetDirectoryName(browserReceipt), SidecarPath.BoundedSidecarStem(stem, [".finalize.json", ".finalize-summary.json"]) + ".finalize.json");
    }

    public static string DefaultSummaryPath(string receiptPath)
    {
        var receipt = NodePath.Resolve(receiptPath);
        var component = Path.GetFileName(receipt);
        var isDefault = component.EndsWith(".finalize.json", StringComparison.OrdinalIgnoreCase);
        var removed = isDefault ? ".finalize.json".Length : component.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ".json".Length : 0;
        var stem = component[..^removed];
        var suffix = isDefault ? ".finalize-summary.json" : "-summary.json";
        // already bounded default stems share the receipt's namespace: no second hash
        var summaryStem = Utf8.GetByteCount(stem + suffix) <= 255 && (stem + suffix).Length <= 255 ? stem : SidecarPath.BoundedSidecarStem(stem, [suffix]);
        return Path.Join(Path.GetDirectoryName(receipt), summaryStem + suffix);
    }

    // runFinalize's own default (finalize.mjs:530-539)
    public static DeliveryPaths DefaultDeliveryPaths(string output)
    {
        var artifact = Paths.CanonicalFuturePath(output);
        var delivery = HtmlExtension().Replace(artifact, ".delivery.json");
        var stem = delivery.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? delivery[..^".json".Length] : null;
        return new(delivery, stem is null ? delivery : stem + "-pending.json", stem is null ? delivery : stem + "-lock.json",
            Path.Join(Path.GetDirectoryName(artifact), DeliveryStore.DirectoryLockName));
    }

    // the CLI's (A:5816-5821): the bounded delivery sidecars, and the directory lock beside the provenance
    public static DeliveryPaths CliDeliveryPaths(string artifact) => new(DeliveryStore.ProvenancePath(artifact), DeliveryStore.PendingPath(artifact),
        SidecarPath.DeliverySidecarPath(artifact, ".delivery-lock.json"), Path.Join(Path.GetDirectoryName(DeliveryStore.ProvenancePath(artifact)), DeliveryStore.DirectoryLockName));

    // --- runFinalize (finalize.mjs:706-1009) ---

    public static async Task<FinalizeResult> RunAsync(FinalizeRequest request, CliServices services)
    {
        var started = Stopwatch.GetTimestamp();
        var startedAt = Iso(services.Now());
        var (type, quality, cwd) = (request.Type, request.Quality, request.Cwd);
        JsonObject Identity(string file)
        {
            var path = NodePath.Resolve(cwd, file);
            try
            {
                var bytes = request.ReadFile(path);
                return new JsonObject { ["path"] = path, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new JsonObject { ["path"] = path }; }
        }

        var resolvedInput = NodePath.Resolve(cwd, request.Input);
        var resolvedOutput = Paths.ResolveOutputPath(new OutputRequest(cwd, request.Output, null) { InputPaths = [resolvedInput] }).OutputPath;
        var resolvedOutDir = request.OutDir is null ? null : OutputPaths.ResolveNativeOutputDirectory(request.OutDir, cwd);
        var specification = Identity(resolvedInput);
        if (request.CandidateSha256 is { Length: > 0 } candidate && Text(specification["sha256"]) != candidate)
        {
            var evidence = new JsonObject { ["candidate"] = resolvedInput, ["expectedSha256"] = candidate };
            CopyIfPresent(evidence, "actualSha256", specification, "sha256");
            throw new FinalizeException(
                $"The candidate changed after validation: expected sha256 {candidate}, found {Text(specification["sha256"]) ?? "unreadable"}.", "finalize/candidate-changed", evidence);
        }
        // Raw native CLI paths are validated before any directory is created.
        if (request.ReceiptPath is not null)
            Paths.ResolveOutputPath(new OutputRequest(cwd, request.ReceiptPath, null) { InputPaths = [resolvedInput], RequiredExtension = ".json" });
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.CanonicalFuturePath(resolvedOutput))!);
        var resolvedReceipt = Paths.ResolveOutputPath(new OutputRequest(cwd, request.ReceiptPath ?? DefaultReceiptPath(resolvedOutput, resolvedOutDir), null)
        {
            InputPaths = [resolvedInput], RequiredExtension = ".json",
        }).OutputPath;
        var resolvedSummary = Paths.ResolveOutputPath(new OutputRequest(cwd, DefaultSummaryPath(resolvedReceipt), null)
        {
            InputPaths = [resolvedInput], RequiredExtension = ".json",
        }).OutputPath;
        void AssertReceiptPaths()
        {
            List<string> reserved = [resolvedInput, resolvedOutput, .. request.DeliveryPaths(resolvedOutput).All,
                BrowserEvidence.SidecarPaths(resolvedOutput, resolvedOutDir, "browser-check", false).Receipt];
            if (reserved.FirstOrDefault(file => Paths.PathsAlias(resolvedReceipt, file)) is { } receiptCollision)
                throw new InvalidOperationException($"The finalize receipt must be distinct from the specification, artifact, and gate sidecars: \"{receiptCollision}\".");
            if (reserved.Prepend(resolvedReceipt).FirstOrDefault(file => Paths.PathsAlias(resolvedSummary, file)) is { } summaryCollision)
                throw new InvalidOperationException($"The finalize summary must be distinct from the full receipt, specification, artifact, and gate sidecars: \"{summaryCollision}\".");
        }
        AssertReceiptPaths();
        // ponytail: captureReceipt is the directory only; atomic-output's regular-file capture and hard-link checks are cut (Q5 D1).
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedReceipt)!);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedSummary)!);

        var receipt = new JsonObject
        {
            ["schemaVersion"] = 1, ["ok"] = false, ["command"] = "finalize", ["status"] = "running", ["type"] = type, ["quality"] = quality,
            ["startedAt"] = startedAt, ["specification"] = specification.DeepClone(), ["artifact"] = new JsonObject { ["path"] = resolvedOutput },
            // a diagram type always needs the browser (finalize.mjs:746 at v3.2.0: needsBrowser = type !== 'explainer' || browserCheck)
            ["stages"] = new JsonObject(), ["browserCheck"] = new JsonObject { ["requested"] = true, ["ran"] = false }, ["diagnostics"] = new JsonArray(),
            ["evidence"] = new JsonObject { ["receipt"] = resolvedReceipt, ["summaryReceipt"] = resolvedSummary }, ["visualReview"] = "not-requested",
        };
        var stages = receipt["stages"]!.AsObject();
        void PersistReceipts()
        {
            AssertReceiptPaths();
            DeliveryStore.PublishFile(resolvedReceipt, Utf8.GetBytes(Js.Stringify(receipt, 2) + "\n"), null);
            DeliveryStore.PublishFile(resolvedSummary, Utf8.GetBytes(Js.Stringify(Compact(receipt), 2) + "\n"), null);
        }
        PersistReceipts();

        var exitCode = 0;
        foreach (var stage in Stages.Skip(1))
        {
            var stageStarted = Stopwatch.GetTimestamp();
            var args = StageArguments(stage, type, resolvedInput, resolvedOutput, quality, request.RepoRoot, resolvedOutDir);
            var result = await services.StageRunner.RunAsync(stage, args, new StageContext(cwd, services, resolvedOutput, resolvedOutDir));
            var stageReceipt = ParsedReceipt(result.Stdout);
            if (stage == "browser-check") receipt["browserCheck"]!["ran"] = P(P(stageReceipt, "containment"), "viewports") is JsonArray { Count: > 0 };
            var code = result.Status;
            var status = StageStatus(stage, code, stageReceipt, quality);
            List<JsonNode?>? stageDiagnostics = null;
            var command = new JsonArray(["archify", .. args.Select(arg => (JsonNode?)arg)]);   // D8
            var elapsed = Elapsed(stageStarted);
            if (status == "pass")
            {
                var deliveryReceipt = stage == "deliver" ? stageReceipt : P(stages["deliver"], "receipt");
                if (StageBindingDiagnostic(stage, stageReceipt, P(deliveryReceipt, "artifact"), P(deliveryReceipt, "receiptId"), resolvedOutput, specification, type,
                        P(deliveryReceipt, "validation"), Identity) is { } binding)
                    stageDiagnostics = [binding];
            }
            else if (stage == "deliver" && code == 0 && quality == "showcase" && Number(P(P(stageReceipt, "validation"), "warnings")) is { } warnings
                && IsSafeInteger(warnings) && warnings > 0 && ValidStageReceipt(stage, stageReceipt, quality, allowShowcaseWarnings: true))
            {
                stageDiagnostics = StageBindingDiagnostic(stage, stageReceipt, null, null, resolvedOutput, specification, type, null, Identity) is { } binding
                    ? [binding] : ShowcaseWarningDiagnostics(stageReceipt!);
            }
            if (stageDiagnostics is not null) status = "fail";
            var stageEntry = new JsonObject { ["status"] = status, ["exitCode"] = code, ["durationMs"] = elapsed, ["command"] = command };
            if (result.InProcess) stageEntry["execution"] = "in-process";
            if (stageReceipt.Truthy()) stageEntry["receipt"] = stageReceipt!.DeepClone();
            else if (Js.Trim(result.Stdout) is { Length: > 0 } stdout) stageEntry["stdout"] = stdout;
            if (Js.Trim(result.Stderr) is { Length: > 0 } stderr) stageEntry["stderr"] = stderr;

            if (stage == "deliver" && status == "pass")
            {
                // non-blocking warnings (locale fallbacks) stay visible in a passing receipt; a later failing gate replaces them
                receipt["diagnostics"] = Items((P(stageReceipt, "diagnostics") as JsonArray ?? []).Where(entry => Is(P(entry, "severity"), "warning")));
                var validate = new JsonObject { ["schemaVersion"] = 1, ["ok"] = true, ["command"] = "validate" };
                CopyIfPresent(validate, "specification", stageReceipt);
                CopyIfPresent(validate, "validation", stageReceipt);
                stages["validate"] = new JsonObject
                {
                    ["status"] = "pass", ["exitCode"] = 0, ["durationMs"] = null, ["execution"] = "embedded-in-deliver", ["command"] = command.DeepClone(), ["receipt"] = validate,
                };
                stages["deliver"] = stageEntry;
            }
            else if (stage == "deliver")
            {
                var validationFailed = stageDiagnostics?.Any(d => Is(P(d, "code"), "finalize/candidate-changed-during-delivery")) == true
                    || Text(P(stageReceipt, "stage")) is "input" or "render" or "check";
                if (validationFailed)
                {
                    var validate = Spread(new JsonObject(), stageEntry);
                    validate["status"] = "fail";
                    validate["durationMs"] = null;
                    validate["execution"] = "embedded-in-deliver";
                    stages["validate"] = validate;
                    stages["deliver"] = new JsonObject { ["status"] = "not-run", ["execution"] = "blocked-by-validate" };
                }
                else
                {
                    stages["validate"] = new JsonObject { ["status"] = "not-run", ["execution"] = "embedded-in-deliver" };
                    stages["deliver"] = stageEntry;
                }
            }
            else stages[stage] = stageEntry;

            if (stage == "deliver" && P(stageReceipt, "artifact").Truthy())
                receipt["artifact"] = Spread(new JsonObject { ["path"] = resolvedOutput }, P(stageReceipt, "artifact"));
            if (stage == "browser-check")
            {
                var evidence = new JsonObject { ["receipt"] = resolvedReceipt, ["summaryReceipt"] = resolvedSummary };
                if (stageReceipt.Truthy() && P(P(stageReceipt, "sidecars"), "receipt").Truthy())
                {
                    var directory = P(P(stageReceipt, "sidecars"), "directory").Truthy()
                        ? NodePath.Resolve(cwd, Text(P(P(stageReceipt, "sidecars"), "directory")) ?? "")
                        : Path.GetDirectoryName(resolvedOutput)!;
                    evidence["browserCheckReceipt"] = NodePath.Resolve(directory, Text(P(P(stageReceipt, "sidecars"), "receipt")) ?? "");
                }
                receipt["evidence"] = evidence;
            }

            if (status != "pass")
            {
                exitCode = status == "skipped" ? 2 : code != 0 ? code : 1;
                receipt["status"] = status;
                receipt["diagnostics"] = Consolidate(stageDiagnostics ?? FailureDiagnostics(stage, result, stageReceipt, quality));
                receipt["failedStage"] = stage == "deliver" && Is(P(stages["validate"], "status"), "fail") ? "validate" : stage;
                break;
            }
            PersistReceipts();
        }

        var ok = exitCode == 0;
        receipt["ok"] = ok;
        if (ok) receipt["status"] = "pass";
        else if (Is(receipt["status"], "running")) receipt["status"] = "fail";
        void Fail(JsonNode diagnostic)
        {
            (exitCode, ok) = (1, false);
            receipt["ok"] = false;
            receipt["status"] = "fail";
            receipt["failedStage"] = "finalize";
            receipt["diagnostics"] = new JsonArray(diagnostic);
        }
        if (ok)
        {
            var deliveryReceipt = P(stages["deliver"], "receipt");
            if (FinalArtifactDiagnostic(request, resolvedOutput, P(deliveryReceipt, "artifact"), P(deliveryReceipt, "receiptId"), Identity) is { } diagnostic)
                Fail(diagnostic);
        }
        if (ok && !IdentitiesMatch(specification, Identity(resolvedInput)))
            Fail(Diagnostic("finalize/candidate-changed", "The frozen candidate changed before the final handoff.", new JsonObject { ["candidate"] = resolvedInput },
                new JsonObject { ["expected"] = specification.DeepClone(), ["actual"] = Identity(resolvedInput) },
                "restore the frozen candidate, or finalize the edited candidate as a new attempt"));
        receipt["artifact"] = ok ? Spread(new JsonObject { ["path"] = resolvedOutput }, P(P(stages["deliver"], "receipt"), "artifact")) : Identity(resolvedOutput);
        receipt["finishedAt"] = Iso(services.Now());
        receipt["durationMs"] = Elapsed(started);
        PersistReceipts();
        return new FinalizeResult(exitCode, receipt, Compact(receipt));
    }

    // The final barrier (finalize.mjs:327-367): the delivery barriers (journal, locks, provenance) once more, then the artifact and its
    // provenance record re-read against the delivery receipt.
    static JsonObject? FinalArtifactDiagnostic(FinalizeRequest request, string output, JsonNode? expectedArtifact, JsonNode? expectedReceiptId,
        Func<string, JsonObject> identity)
    {
        if (request.InspectDelivery?.Invoke(output) is { } provenance && (!provenance.Ok || provenance.ReceiptId != Text(expectedReceiptId)))
        {
            return provenance.Diagnostics.Count > 0 ? provenance.Diagnostics[0].ToJson()
                : IdentityMismatch("finalize", output, expectedArtifact, null, expectedReceiptId, provenance.ReceiptId);
        }
        var currentArtifact = identity(output);
        JsonNode? delivery;
        try { delivery = DiagramLoader.ParseNode(Utf8.GetString(request.ReadFile(request.DeliveryPaths(output).Provenance))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { delivery = null; }
        if (IdentitiesMatch(currentArtifact, expectedArtifact) && Is(P(delivery, "schemaVersion"), 1) && Is(P(delivery, "command"), "deliver")
            && Is(P(delivery, "status"), "current") && Text(P(delivery, "receiptId")) == Text(expectedReceiptId) && Is(P(delivery, "type"), request.Type)
            && Text(P(delivery, "output")) is { } recorded && Paths.PathsAlias(recorded, output) && IdentitiesMatch(P(delivery, "artifact"), expectedArtifact))
            return null;
        var evidence = new JsonObject
        {
            ["expectedArtifact"] = expectedArtifact?.DeepClone(), ["currentArtifact"] = currentArtifact, ["expectedDeliveryReceiptId"] = expectedReceiptId?.DeepClone(),
            ["expectedType"] = request.Type,
        };
        foreach (var (key, source) in new[] { ("currentDeliveryReceiptId", "receiptId"), ("currentType", "type"), ("currentDeliveryArtifact", "artifact") })
            if (P(delivery, source).Truthy()) evidence[key] = P(delivery, source)!.DeepClone();
        return Diagnostic("finalize/final-artifact-mismatch", "The artifact or delivery receipt changed after the final gate completed.",
            new JsonObject { ["stage"] = "finalize", ["artifact"] = output }, evidence,
            "finish other delivery attempts for this output, then rerun finalize from the frozen candidate");
    }

    // --- compactFinalizeReceipt (finalize.mjs:546-704) ---

    // Node moves that remove the measured crossings and detours, most specific first.
    static List<string> PlacementHints(JsonNode routeReview)
    {
        static string Name(JsonNode? relation) => $"{T(relation, "from")} → {T(relation, "to")}";
        static string Other(JsonNode? relation, JsonNode shared) =>
            JsonNode.DeepEquals(P(relation, "from"), shared) && Has(relation, "from") ? T(relation, "to") : T(relation, "from");
        var hints = new List<string>();
        foreach (var side in P(routeReview, "crowdedSides").AsArrayOrEmpty())
        {
            var ports = Math.Max(1, Math.Floor(((Number(P(side, "sidePx")) ?? double.NaN) - 32) / 14) + 1);
            hints.Add($"{T(side, "node")} has {T(side, "relationships")} relationships facing its {T(side, "side")} side, which fits {Js.Num(ports)} ports: " +
                $"make that side at least {T(side, "neededPx")}px, or move some of those neighbours so they face another side of {T(side, "node")}.");
        }
        foreach (var crossing in P(routeReview, "crossings").AsArrayOrEmpty())
        {
            var shared = P(crossing, "sharedNode");
            var (left, right) = (P(crossing, "left"), P(crossing, "right"));
            hints.Add(shared.Truthy()
                ? $"{Name(left)} and {Name(right)} cross next to {Template(shared)}: move node {Other(left, shared!)} or {Other(right, shared!)} so the two reach {Template(shared)} from different sides (for example one level with it, one directly above or below it)."
                : $"{Name(left)} crosses {Name(right)}: move the node of whichever is a branch, return, or second entrance to the other side of the main path, so that relationship runs through an empty corridor.");
        }
        foreach (var detour in P(routeReview, "detours").AsArrayOrEmpty())
        {
            if (P(detour, "directCorridorBlockers") is JsonArray { Count: > 0 } || Text(P(detour, "directCorridorBlockers")) is { Length: > 0 }) continue;
            var relationship = P(detour, "relationship");
            hints.Add($"{Name(relationship)} needs {T(detour, "bends")} bends: move node {T(relationship, "from")} or {T(relationship, "to")} so they share a row or column with matching centers, or sit diagonally with a clear corner.");
        }
        return [.. hints.Distinct().Take(8)];
    }

    // A JS Set/Map key: SameValueZero for the strings, null and undefined that codes are.
    static string Key(JsonNode? node, string key) => !Has(node, key) ? "\0undefined" : P(node, key) is null ? "\0null" : Js.Stringify(P(node, key));

    static string? SubjectKey(JsonNode? entry)
    {
        var subject = P(entry, "subject");
        if (!subject.Truthy()) return null;
        if (Text(subject) is { } text) return text;
        if (subject is not JsonObject o) return Js.Stringify(subject);
        foreach (var key in new[] { "id", "edge", "connection", "relationship", "component", "node", "path", "stage" })
            if (o.TryGetPropertyValue(key, out var value)) return $"{key}:{Js.Stringify(value)}";
        return Js.Stringify(subject);
    }

    static bool HasKeys(JsonNode? value) => value switch
    {
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        _ => Text(value) is { Length: > 0 },
    };

    public static JsonObject Compact(JsonObject receipt)
    {
        var gates = new JsonObject();
        foreach (var stage in Stages) gates[stage] = P(P(P(receipt, "stages"), stage), "status") is { } status && status.Truthy() ? status.DeepClone() : "not-run";
        var all = P(receipt, "diagnostics") as JsonArray ?? [];
        const int limit = 8;
        var selected = new JsonArray();
        var selectedIndexes = new HashSet<int>();
        void Add(JsonNode? entry, int index)
        {
            if (selected.Count >= limit || !selectedIndexes.Add(index)) return;
            var diagnostic = new JsonObject();
            CopyIfPresent(diagnostic, "code", entry);
            diagnostic["severity"] = P(entry, "severity").Truthy() ? P(entry, "severity")!.DeepClone() : "error";
            CopyIfPresent(diagnostic, "message", entry);
            if (P(entry, "subject").Truthy()) diagnostic["subject"] = P(entry, "subject")!.DeepClone();
            if (P(entry, "evidence").Truthy() && HasKeys(P(entry, "evidence"))) diagnostic["evidence"] = P(entry, "evidence")!.DeepClone();
            // verified repairs may require every edit in the set to be applied together
            if (P(entry, "supportedFixes") is JsonArray { Count: > 0 } fixes) diagnostic["supportedFixes"] = fixes.DeepClone();
            selected.Add(diagnostic);
        }
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < all.Count; index++)
            if (seenCodes.Add(Key(all[index], "code"))) Add(all[index], index);
        var seenSubjects = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < all.Count; index++)
            if (SubjectKey(all[index]) is { } key && seenSubjects.Add(key)) Add(all[index], index);
        for (var index = 0; index < all.Count; index++) Add(all[index], index);
        var truncated = all.Count > selected.Count;

        var compact = new JsonObject { ["schemaVersion"] = 1 };
        CopyIfPresent(compact, "ok", receipt);
        compact["command"] = "finalize";
        foreach (var key in new[] { "status", "type", "quality", "specification", "artifact" }) CopyIfPresent(compact, key, receipt);
        compact["gates"] = gates;
        if (P(receipt, "failedStage").Truthy()) compact["failedStage"] = P(receipt, "failedStage")!.DeepClone();
        compact["diagnostics"] = selected;
        var summary = new JsonObject { ["total"] = all.Count, ["shown"] = selected.Count, ["truncated"] = truncated };
        if (truncated)
        {
            // a Map in insertion order, keyed by the code value; Object.fromEntries names a key by String(code)
            var omitted = new List<(string Key, string Name, int Count)>();
            for (var index = 0; index < all.Count; index++)
            {
                var key = Key(all[index], "code");
                var at = omitted.FindIndex(o => o.Key == key);
                if (at < 0) omitted.Add((key, T(all[index], "code"), 0));
                at = at < 0 ? omitted.Count - 1 : at;
                if (!selectedIndexes.Contains(index)) omitted[at] = omitted[at] with { Count = omitted[at].Count + 1 };
            }
            var byCode = new JsonObject();
            foreach (var (_, name, count) in omitted.Where(o => o.Count > 0)) byCode[name] = count;
            summary["omittedByCode"] = byCode;
        }
        compact["diagnosticSummary"] = summary;
        CopyIfPresent(compact, "evidence", receipt);
        compact["visualReview"] = P(receipt, "visualReview").Truthy() ? P(receipt, "visualReview")!.DeepClone() : "not-requested";
        CopyIfPresent(compact, "durationMs", receipt);
        if (P(receipt, "browserCheck").Truthy()) compact["browserCheck"] = P(receipt, "browserCheck")!.DeepClone();

        var ok = P(receipt, "ok").Truthy();
        var type = P(receipt, "type");
        var composition = P(P(P(P(receipt, "stages"), "check"), "receipt"), "composition");
        var signals = new JsonObject();
        foreach (var key in new[] { "resolvedCrossovers", "routesOverSuggestedBends", "routesOverSuggestedStretch" })
            if (Number(P(P(composition, "metrics"), key)) is { } value && double.IsFinite(value) && value > 0) signals[key] = value;
        if (ok && signals.Count > 0)
        {
            var routeReview = P(composition, "routeReview");
            var hints = routeReview.Truthy() ? PlacementHints(routeReview!) : [];
            // modes without free node placement cannot act on node-move hints
            var advisoryOnly = type.Truthy() && !Is(type, "architecture");
            var recommendation = new JsonObject
            {
                ["action"] = "inspect-route-readability", ["signals"] = signals,
                ["reason"] = "Automated gates passed, but crossings or detours still need perceptual review before claiming visual quality.",
            };
            if (routeReview.Truthy())
            {
                var crossings = P(routeReview, "crossings").AsArrayOrEmpty().ToList();
                var detours = P(routeReview, "detours").AsArrayOrEmpty().ToList();
                recommendation["affectedRoutes"] = new JsonObject
                {
                    ["crossings"] = Items(crossings.Take(8)), ["detours"] = Items(detours.Take(8)), ["truncated"] = crossings.Count > 8 || detours.Count > 8,
                };
                if (hints.Count > 0 && !advisoryOnly) recommendation["hints"] = new JsonArray([.. hints.Select(h => (JsonNode?)h)]);
                recommendation["repair"] = !advisoryOnly
                    ? "Trace these relationships at the desktop viewport. For Architecture, use references/architecture-layout-repair.md: reflow a blocked main path or tangled connected scene, and repair an isolated defect locally only when the surrounding composition is accepted. Preserve all semantic content and user-fixed geometry. Rerun finalize once after the edit."
                    : "Advisory only: this mode places or routes these relationships itself. Report them; do not remove relationships or meaning to reduce crossings.";
            }
            compact["visualReviewRecommendation"] = recommendation;
        }
        var leadingSpace = P(composition, "leadingSpace");
        if (ok && IsTrue(P(leadingSpace, "reviewSuggested")))
            compact["layoutReviewRecommendation"] = new JsonObject
            {
                ["action"] = "inspect-leading-space", ["evidence"] = leadingSpace!.DeepClone(),
                ["reason"] = "Measured content, including routes and labels, leaves a large empty area above the diagram. This is a composition suggestion, not a failed gate.",
                ["repair"] = "Check whether that leading space is intentional. If not, reposition the connected scene nearer the canvas origin while retaining room for its actual boundaries, labels and return routes. Preserve all meaning and user-fixed geometry, then rerun finalize. No screenshot is required.",
            };
        var sequenceColumnSpace = P(composition, "sequenceColumnSpace");
        if (ok && Is(type, "sequence") && IsTrue(P(sequenceColumnSpace, "reviewSuggested")))
            compact["layoutReviewRecommendation"] = new JsonObject
            {
                ["action"] = "inspect-sequence-width", ["evidence"] = sequenceColumnSpace!.DeepClone(),
                ["reason"] = "Fixed participant columns leave substantial unused space on the right, after accounting for message labels and notes. This is a layout suggestion, not a failed gate.",
                ["repair"] = "Check whether the explicit fixed column geometry is intentional. Only when changing that geometry is authorized, save the passing fixed candidate, set only meta.column_fit to \"spread\", and rerun the complete finalize once with --out-dir <folder>/width-review. If that attempt fails, restore the saved candidate and finalize it with --out-dir <folder>/width-restore; report the remaining suggestion instead of iterating. Preserve participant order, every message, its y position, labels, notes, sources and canvas dimensions. Retain intentional fixed layouts; report the suggestion instead of changing them automatically.",
            };
        if (!ok && Is(P(receipt, "status"), "fail") && Is(P(receipt, "failedStage"), "validate"))
        {
            var nextAction = new JsonObject { ["action"] = "edit-in-place" };
            CopyIfPresent(nextAction, "candidate", P(receipt, "specification"), "path");
            nextAction["constraint"] = Is(type, "architecture") && all.Any(entry => RouteDiagnostic().IsMatch(Text(P(entry, "code")) ?? ""))
                ? "Preserve all semantics and user-fixed geometry. Use references/architecture-layout-repair.md to choose a local repair or connected-scene reflow; edit the existing candidate."
                : "Preserve unaffected semantics and geometry; apply each diagnostic's supportedFixes, or the fix its message names, and do not replace the whole candidate.";
            nextAction["then"] = "finalize-once";
            compact["nextAction"] = nextAction;
        }
        return compact;
    }
}
