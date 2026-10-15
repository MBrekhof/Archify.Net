using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Browser;

// runBrowserEvidence's options (VC:2380-2390): the artifact, the command (browser-check: no captures; visual-check: captures),
// --out-dir, the delivery provenance the CLI inspected first, and its verifyArtifact hook.
internal sealed record BrowserEvidenceRequest(string ArtifactPath, string Command, bool Capture)
{
    public string? OutDir { get; init; }
    public Provenance? DeliveryProvenance { get; init; }
    public Action<byte[]>? VerifyArtifact { get; init; }
    public PathSemantics Semantics { get; init; } = PathSemantics.Native;
}

// {exitCode, receipt}; Published = the receipt file was committed (upstream's publishedBrowserEvidenceReceipts WeakSet).
internal sealed record BrowserEvidenceResult(int ExitCode, JsonObject Receipt, bool Published);

internal sealed record EvidenceCapture(int Width, int Height, string Theme, string Path);

// evidenceSidecarPaths' result (VC:229-232). A browser-check names a contact sheet it never writes, as upstream does.
internal sealed record EvidenceOutputs(string Command, bool Capture, string Receipt, string ContactSheet, IReadOnlyList<EvidenceCapture> Screenshots)
{
    // visualEvidenceTargets (VC:267-277): the receipt, then the contact sheet and the screenshots of a visual-check.
    public IReadOnlyList<string> Targets => [Receipt, .. VisualEvidence];
    // visualEvidencePaths (VC:76-78)
    public IReadOnlyList<string> VisualEvidence => Capture ? [ContactSheet, .. Screenshots.Select(s => s.Path)] : [];
}

// The browser-check / visual-check engine (VC:2380-2685) behind the IBrowserInspector seam: a private snapshot of the artifact,
// 4 light then 2 dark inspections, the status roll-up, the skipped path, runtime-failure receipts and the artifact re-verification.
// Cut with atomic-output (Q5, D1): staging-directory hard links, dev/ino bindings, backups, quarantine and recovery directories and the
// committed-with-warning cleanup report. What stays is upstream's ownership contract: evidence is written only over evidence an
// earlier receipt of this command proves it wrote for these bytes, a target that changed kind during the run is a conflict, and a
// publication replaces the whole owned set (stale captures are removed).
internal static partial class BrowserEvidence
{
    // VISUAL_CHECK_VIEWPORTS, CAPTURE_VIEWPORTS and THEMES (VC:34-45)
    public static readonly IReadOnlyList<(int Width, int Height)> VisualCheckViewports = [(1440, 900), (1600, 1000), (1920, 1080), (2048, 1320)];
    public static readonly IReadOnlyList<(int Width, int Height)> CaptureViewports = [VisualCheckViewports[0], VisualCheckViewports[^1]];
    static readonly string[] Themes = ["light", "dark"];
    static readonly IReadOnlyList<string> VisualSidecarSuffixes =
        [".visual-check.json", ".visual-check.html", .. CaptureViewports.SelectMany(v => Themes.Select(theme => $".visual-check.{v.Width}x{v.Height}.{theme}.png"))];

    // Q4: the browser is Playwright's pinned Chromium, so the one fix is the install command (upstream: set ARCHIFY_CHROME).
    public const string ChromeUnavailableError = "Chromium is unavailable. Run archify install-browser to install it.";
    public static string ChromeUnavailableFix(string command) => $"run archify install-browser, then rerun {command}";

    [GeneratedRegex(@"\.html?\z", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlArtifact();

    // --- sidecar paths ---

    // evidenceSidecarPaths (VC:190-233): the physical artifact's namespace stem, bounded for every suffix of the command; with
    // --out-dir, a target directory whose name semantics differ from the source's gets a hashed stem so distinct artifacts cannot share
    // a name. Pure: a missing --out-dir is never created.
    public static EvidenceOutputs SidecarPaths(string artifactPath, string? outDir, string command, bool capture, PathSemantics? semantics = null)
    {
        semantics ??= PathSemantics.Native;
        var artifact = Path.GetFullPath(artifactPath);
        try { artifact = semantics.RealPath(artifact); }
        catch (Exception error) when (NativeFs.ErrorCode(error) is "ENOENT" or "ENOTDIR" or "ENAMETOOLONG") { }
        var source = semantics.SidecarNamespaceComponentKey(Path.GetDirectoryName(artifact)!, Path.GetFileName(artifact));
        if (source.Status != "resolved") throw new SidecarNamespaceException(source.Reason);
        var componentKey = source.ComponentKey!;
        artifact = Path.Join(source.DirectoryPath, Path.GetFileName(artifact));
        var (stem, force, hashDomain) = SidecarPath.StemFromComponent(componentKey);
        var directory = outDir is null ? Path.GetDirectoryName(artifact)! : Path.GetFullPath(outDir);
        if (outDir is not null)
        {
            var portableKey = PathSemantics.PortableSidecarComponentKey(componentKey);
            if (TargetSidecarNamespace(semantics, directory, componentKey) != componentKey || portableKey != componentKey)
                (force, hashDomain) = (true, $"{command}-artifact\0{artifact}\0target-component\0{portableKey}");
        }
        var bounded = SidecarPath.BoundedSidecarStem(stem, capture ? VisualSidecarSuffixes : [".browser-check.json"], force, hashDomain);
        var stemPath = Path.Join(directory, $"{bounded}.{command}");
        return new(command, capture, $"{stemPath}.json", $"{stemPath}.html", capture
            ? [.. CaptureViewports.SelectMany(v => Themes.Select(theme => new EvidenceCapture(v.Width, v.Height, theme, $"{stemPath}.{v.Width}x{v.Height}.{theme}.png")))]
            : []);
    }

    // targetSidecarNamespace (VC:172-188): a directory that does not exist yet cannot be probed, so it gets the portable envelope.
    static string TargetSidecarNamespace(PathSemantics semantics, string directory, string component)
    {
        var target = semantics.SidecarNamespaceComponentKey(directory, component);
        if (target.Status == "resolved") return target.ComponentKey!;
        if (target.Reason["code"]?.GetValue<string>() == "sidecar-directory-missing") return PathSemantics.PortableSidecarComponentKey(component);
        throw new SidecarNamespaceException(target.Reason, "target sidecar semantics");
    }

    sealed record SidecarDirectory(JsonObject Sidecars, EvidenceFailure? Failure = null);

    // resolveSidecarDirectory (VC:2037-2066): `sidecars.directory` only when the evidence lives elsewhere than the artifact.
    static SidecarDirectory ResolveSidecarDirectory(string artifactPath, EvidenceOutputs outputs, PathSemantics semantics)
    {
        var artifact = Path.GetFullPath(artifactPath);
        var sidecarDirectory = Path.GetDirectoryName(outputs.Receipt)!;
        var comparison = semantics.SameParent(outputs.Receipt, artifact);
        var sidecars = new JsonObject();
        if (comparison.Status == "different") sidecars["directory"] = sidecarDirectory;
        sidecars["receipt"] = Path.GetFileName(outputs.Receipt);
        if (outputs.Capture) sidecars["contactSheet"] = Path.GetFileName(outputs.ContactSheet);
        if (comparison.Status is "match" or "different") return new(sidecars);
        var error = $"visual-check could not determine the physical identity of its evidence directory \"{sidecarDirectory}\" relative to the artifact directory \"{Path.GetDirectoryName(artifact)}\".";
        return new(sidecars, new(error, BrowserReceipt.Failure("viewer/sidecar-directory-identity", error,
            new JsonObject { ["artifact"] = artifact, ["sidecarDirectory"] = sidecarDirectory }, new JsonObject { ["comparison"] = comparison.Reason.DeepClone() },
            ["use existing, accessible artifact and evidence directories, then rerun visual-check"])));
    }

    // --- evidence ownership and publication ---

    sealed record EvidenceFailure(string Error, Diagnostic Diagnostic);

    // What each target was when the run began: "absent" or "file".
    sealed record Ownership(IReadOnlyDictionary<string, string> Kinds);

    // evidencePathConflict (VC:243-258)
    static EvidenceFailure Conflict(string artifactPath, EvidenceOutputs outputs, string? file, JsonObject reason)
    {
        var target = file ?? outputs.Receipt;
        var error = $"{outputs.Command} will not replace the existing unowned evidence path \"{target}\".";
        return new(error, BrowserReceipt.Failure("viewer/evidence-path-conflict", error,
            new JsonObject { ["artifact"] = Path.GetFullPath(artifactPath), ["evidencePath"] = target }, new JsonObject { ["reason"] = reason },
            [$"move the conflicting file aside, or choose a separate --out-dir, then rerun {outputs.Command}"]));
    }

    // evidenceWriteFailure (VC:279-292)
    static EvidenceFailure WriteFailure(string artifactPath, string message, JsonObject error) =>
        new(message, BrowserReceipt.Failure("viewer/evidence-write", message, new JsonObject { ["artifact"] = Path.GetFullPath(artifactPath) },
            new JsonObject { ["errors"] = new JsonArray(error) }, ["restore write access to the evidence directory and rerun visual-check"]));

    // captureRequestedEntry with the regular-or-absent policy (atomic-output.mjs:1458-1476): the kind, or the refusal reason.
    static (string? Kind, JsonObject? Refusal) RequestedEntry(string path)
    {
        FsStat stat;
        try { stat = NativeFs.LStat(path); }
        catch (Exception error) when (NativeFs.ErrorCode(error) == "ENOENT") { return ("absent", null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var reason = new JsonObject { ["code"] = "requested-entry-inspection-failed", ["requestedPath"] = path };
            if (NativeFs.ErrorCode(error) is { } systemCode) reason["systemCode"] = systemCode;
            reason["message"] = error.Message;
            return (null, reason);
        }
        if (!stat.IsDirectory && !stat.IsSymbolicLink) return ("file", null);
        return (null, new JsonObject
        {
            ["code"] = stat.IsSymbolicLink ? "requested-entry-symbolic-link" : "requested-entry-not-regular-file", ["requestedPath"] = path,
            ["entryType"] = stat.IsSymbolicLink ? "symbolic-link" : "directory",
        });
    }

    // beginVisualEvidenceWrite (VC:604-783) without the staging directory: create the evidence directory, then accept the targets only
    // if each is absent or a regular file and every existing one is evidence the existing receipt proves this command wrote for
    // exactly these artifact bytes.
    static (Ownership? Ownership, EvidenceFailure? Failure) BeginWrite(string artifactPath, byte[] artifactBytes, EvidenceOutputs outputs, PathSemantics semantics)
    {
        var directory = Path.GetDirectoryName(outputs.Receipt)!;
        try { Directory.CreateDirectory(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, WriteFailure(artifactPath, "visual-check could not create its evidence directory.", new JsonObject { ["file"] = directory, ["reason"] = error.Message }));
        }
        var kinds = new Dictionary<string, string>();
        foreach (var target in outputs.Targets)
        {
            var (kind, refusal) = RequestedEntry(target);
            if (refusal is not null) return (null, Conflict(artifactPath, outputs, target, refusal));
            kinds[target] = kind!;
        }
        var existing = outputs.Targets.Where(target => kinds[target] == "file").ToList();
        if (existing.Count > 0 && Unowned(artifactPath, artifactBytes, outputs, kinds, existing, semantics) is { } refused) return (null, refused);
        return (new Ownership(kinds), null);
    }

    static EvidenceFailure? Unowned(string artifactPath, byte[] artifactBytes, EvidenceOutputs outputs, Dictionary<string, string> kinds, List<string> existing,
        PathSemantics semantics)
    {
        EvidenceFailure Refuse(string file, string code) => Conflict(artifactPath, outputs, file, new JsonObject { ["code"] = code });
        if (kinds[outputs.Receipt] != "file") return Refuse(existing[0], "ownership-receipt-missing");
        if (DeliveryStore.RegularFile(outputs.Receipt, "ownership-receipt", null, out var receiptBytes, out var receiptSystemCode) is { } unreadable)
        {
            var reason = new JsonObject { ["code"] = "ownership-receipt-invalid", ["bindingCode"] = unreadable };
            if (receiptSystemCode is not null) reason["systemCode"] = receiptSystemCode;
            return Conflict(artifactPath, outputs, outputs.Receipt, reason);
        }
        JsonNode? parsed;
        // ponytail: JsonNode.Parse, not JSON.parse: a receipt with duplicate keys counts as invalid here (upstream keeps the last value).
        try { parsed = JsonNode.Parse(Encoding.UTF8.GetString(receiptBytes)); }
        catch (Exception error) when (error is JsonException or ArgumentException) { return Refuse(outputs.Receipt, "ownership-receipt-invalid"); }
        var previous = parsed as JsonObject;
        if (!IsNumber(previous?["schemaVersion"], 1) || Text(previous!["command"]) != outputs.Command) return Refuse(outputs.Receipt, "ownership-receipt-schema-mismatch");

        var sidecars = previous["sidecars"] as JsonObject;
        var receiptName = Text(sidecars?["receipt"]);
        var contactName = Text(sidecars?["contactSheet"]);
        var previousArtifact = previous["artifact"] as JsonObject;
        var previousPath = Text(previousArtifact?["path"]);
        var recordedDirectory = Text(sidecars?["directory"]) is { Length: > 0 } recorded ? recorded
            : previousPath is { Length: > 0 } ? PathSemantics.DirName(previousPath) : ".";
        // path.basename(name) === name
        static bool Bare(string? name) => name is not null && Path.GetFileName(name) == name;
        if (!IsAbsolute(recordedDirectory) || !Bare(receiptName) || (outputs.Capture && !Bare(contactName))
            || semantics.SameLocation(Path.Join(recordedDirectory, receiptName), outputs.Receipt).Status != "match"
            || (outputs.Capture && semantics.SameLocation(Path.Join(recordedDirectory, contactName), outputs.ContactSheet).Status != "match"))
            return Refuse(outputs.Receipt, "ownership-sidecar-path-mismatch");
        var identity = DeliveryStore.Identity(artifactBytes);
        if (previousPath is null || !IsAbsolute(previousPath) || semantics.SameEntry(previousPath, artifactPath).Status != "match"
            || Text(previousArtifact!["sha256"]) != identity["sha256"]!.GetValue<string>() || !IsNumber(previousArtifact["bytes"], artifactBytes.Length))
            return Refuse(outputs.Receipt, "ownership-artifact-mismatch");

        var existingEvidence = outputs.VisualEvidence.Where(file => kinds[file] == "file").ToList();
        var recordedFiles = sidecars?["files"];
        if (existingEvidence.Count > 0 && recordedFiles is not JsonArray) return Refuse(existingEvidence[0], "ownership-evidence-digests-missing");
        if (recordedFiles is not JsonArray files) return null;
        var allowed = outputs.VisualEvidence.Select(Path.GetFileName).ToHashSet();
        var byName = new Dictionary<string, JsonObject>();
        foreach (var item in files)
        {
            if (item is not JsonObject entry || Text(entry["file"]) is not { } name || byName.ContainsKey(name) || !allowed.Contains(name))
                return Refuse(outputs.Receipt, "ownership-evidence-list-invalid");
            byName[name] = entry;
        }
        if (byName.Count != existingEvidence.Count || existingEvidence.Any(file => !byName.ContainsKey(Path.GetFileName(file))))
            return Refuse(outputs.Receipt, "ownership-evidence-list-mismatch");
        foreach (var file in existingEvidence)
        {
            var expected = byName[Path.GetFileName(file)];
            if (DeliveryStore.RegularFile(file, "evidence-file", null, out var content, out var systemCode) is { } code)
            {
                // regularFileEvidence's reasons (VC:80-115)
                var reason = new JsonObject
                {
                    ["code"] = systemCode == "ENOENT" ? "missing-evidence-file" : code.EndsWith("-not-regular-file", StringComparison.Ordinal) ? "evidence-path-not-regular-file" : "evidence-file-unreadable",
                };
                if (systemCode is not null) reason["systemCode"] = systemCode;
                return Conflict(artifactPath, outputs, file, reason);
            }
            var actual = DeliveryStore.Identity(content);
            if (Text(expected["sha256"]) != actual["sha256"]!.GetValue<string>() || !IsNumber(expected["bytes"], content.Length))
                return Refuse(file, "ownership-evidence-digest-mismatch");
        }
        return null;
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static bool IsNumber(JsonNode? node, double expected) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.D() == expected;

    // path.isAbsolute: on Windows a rooted path (`\x`, `C:\x`, UNC) but not a drive-relative one (`C:x`).
    static bool IsAbsolute(string path) => OperatingSystem.IsWindows()
        ? path.Length > 0 && (path[0] is '\\' or '/' || (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'))
        : path.StartsWith('/');

    // commitVisualEvidence (VC:965-1220), cut to its contract: every target still has the kind it had at the start (a claimant that
    // appeared or changed kind is a conflict), owned targets outside the new set are removed, then the set is written with the receipt
    // last. ponytail: no rollback of a half-written set (atomic-output cut, D1); the next run's ownership check sees the old receipt.
    static EvidenceFailure? Commit(string artifactPath, EvidenceOutputs outputs, Ownership ownership, IReadOnlyList<(string Path, byte[] Bytes)> desired)
    {
        foreach (var target in outputs.Targets)
        {
            var (kind, refusal) = RequestedEntry(target);
            if (refusal is not null) return Conflict(artifactPath, outputs, target, refusal);
            if (kind != ownership.Kinds[target])
                return Conflict(artifactPath, outputs, target, new JsonObject
                {
                    ["code"] = "requested-entry-changed", ["requestedPath"] = target, ["previousKind"] = ownership.Kinds[target], ["currentKind"] = kind,
                });
        }
        try
        {
            foreach (var target in outputs.Targets.Where(target => ownership.Kinds[target] == "file" && desired.All(d => d.Path != target))) File.Delete(target);
            foreach (var (path, bytes) in desired.OrderBy(d => d.Path == outputs.Receipt)) DeliveryStore.PublishFile(path, bytes, null);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return WriteFailure(artifactPath, "visual-check could not atomically publish its evidence set.", new JsonObject { ["reason"] = error.Message });
        }
    }

    static byte[] ReceiptBytes(JsonObject receipt) => new UTF8Encoding(false).GetBytes(Js.Stringify(receipt, 2) + "\n");

    // appendEvidencePublicationFailure (VC:2237-2242)
    static void AppendPublicationFailure(JsonObject receipt, EvidenceFailure failure)
    {
        if (receipt["error"] is not JsonValue error || error.GetValueKind() != JsonValueKind.String || error.GetValue<string>().Length == 0) receipt["error"] = failure.Error;
        var diagnostics = receipt["diagnostics"]?.DeepClone() as JsonArray ?? [];
        diagnostics.Add(failure.Diagnostic.ToJson());
        receipt["diagnostics"] = diagnostics;
    }

    // --- failure persistence ---

    // persistBrowserCheckFailure / persistVisualCheckFailure (VC:2310-2378): the failure receipt with the artifact's identity (when it
    // reads), failed containment and captures, written alone over this command's owned evidence. Exit 1.
    public static BrowserEvidenceResult PersistFailure(string artifactPath, JsonObject failure, string? outDir, string command, bool capture,
        PathSemantics? semantics = null) => Persist(artifactPath, failure, outDir, command, capture, semantics ?? PathSemantics.Native, null);

    static BrowserEvidenceResult Persist(string artifactPath, JsonObject failure, string? outDir, string command, bool capture, PathSemantics semantics,
        Ownership? ownership)
    {
        var outputs = SidecarPaths(artifactPath, outDir, command, capture, semantics);
        var directory = ResolveSidecarDirectory(artifactPath, outputs, semantics);
        var artifact = Path.GetFullPath(artifactPath);
        var readable = DeliveryStore.RegularFile(artifact, "failure-artifact", null, out var artifactBytes, out _) is null;
        var receipt = failure.DeepClone().AsObject();
        receipt["ok"] = false;
        receipt["status"] = "fail";
        if (readable) receipt["artifact"] = BrowserReceipt.ArtifactIdentity(artifact, artifactBytes);
        receipt["containment"] = BrowserReceipt.Containment();
        receipt["captures"] = BrowserReceipt.Captures(capture);
        receipt["sidecars"] = BrowserReceipt.WithFiles(directory.Sidecars);

        BrowserEvidenceResult Refused(EvidenceFailure refusal)
        {
            receipt["error"] = refusal.Error;
            var diagnostics = failure["diagnostics"]?.DeepClone() as JsonArray ?? [];
            diagnostics.Add(refusal.Diagnostic.ToJson());
            receipt["diagnostics"] = diagnostics;
            return new(1, receipt, false);
        }
        if (directory.Failure is { } identity) return Refused(identity);
        if (ownership is null)
        {
            if (!readable) return Refused(Conflict(artifactPath, outputs, outputs.Receipt, new JsonObject { ["code"] = "artifact-unreadable" }));
            var (began, preflight) = BeginWrite(artifactPath, artifactBytes, outputs, semantics);
            if (preflight is not null) return Refused(preflight);
            ownership = began!;
        }
        var publication = Commit(artifactPath, outputs, ownership, [(outputs.Receipt, ReceiptBytes(receipt))]);
        if (publication is not null) AppendPublicationFailure(receipt, publication);
        return new(1, receipt, publication is null);
    }

    // --- the run ---

    static JsonObject Chrome(string status, string? executable) => new() { ["status"] = status, ["executable"] = executable };

    static BrowserEvidenceResult Refused(JsonObject receipt, EvidenceFailure refusal)
    {
        receipt["error"] = refusal.Error;
        receipt["diagnostics"] = BrowserReceipt.Json([refusal.Diagnostic]);
        return new(1, receipt, false);
    }

    // runBrowserEvidence (VC:2380-2675). Input failures (a non-.html name, an unreadable artifact, an indeterminate sidecar namespace)
    // escape as exceptions for the CLI's viewer/<command>-input receipt (A:5586-5606); every later failure is a receipt.
    public static async Task<BrowserEvidenceResult> RunAsync(BrowserEvidenceRequest request, IBrowserInspectorFactory browsers)
    {
        var (command, capture, semantics) = (request.Command, request.Capture, request.Semantics);
        if (string.IsNullOrEmpty(request.ArtifactPath)) throw new BrowserEvidenceException($"{command} requires one delivered HTML artifact.");
        var artifact = Path.GetFullPath(request.ArtifactPath);
        if (!HtmlArtifact().IsMatch(artifact)) throw new BrowserEvidenceException($"{command} requires an .html artifact.");
        if (DeliveryStore.RegularFile(artifact, "visual-check-artifact", null, out var artifactBytes, out var systemCode) is { } unreadable)
            throw new BrowserEvidenceException($"The visual-check artifact could not be read safely ({unreadable}).") { Code = systemCode };
        var outputs = SidecarPaths(artifact, request.OutDir, command, capture, semantics);
        var directory = ResolveSidecarDirectory(artifact, outputs, semantics);
        var receipt = BrowserReceipt.Base(artifact, artifactBytes, directory.Sidecars, Chrome("not-checked", null), request.DeliveryProvenance, command, capture);
        if (directory.Failure is { } identity) return Refused(receipt, identity);
        var (ownership, preflight) = BeginWrite(artifact, artifactBytes, outputs, semantics);
        if (preflight is not null) return Refused(receipt, preflight);

        string? inspection = null;
        try
        {
            // Chrome's file loader cannot reliably open long Windows or UNC paths (VC:2413-2414): one private local snapshot.
            string snapshot;
            try
            {
                inspection = Directory.CreateTempSubdirectory("archify-inspection-").FullName;
                snapshot = Path.Join(inspection, "artifact-snapshot.html");
                using (var stream = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write)) stream.Write(artifactBytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var failure = receipt.DeepClone().AsObject();
                failure["status"] = "fail";
                failure["ok"] = false;
                failure["error"] = $"visual-check could not stage its private artifact snapshot: {error.Message}";
                failure["diagnostics"] = BrowserReceipt.Json([BrowserReceipt.Failure("viewer/artifact-snapshot", "visual-check could not bind a private artifact snapshot.",
                    new JsonObject { ["artifact"] = artifact }, new JsonObject { ["reason"] = error.Message }, ["retry after resolving the reported staging filesystem error"])]);
                return Persist(artifact, failure, request.OutDir, command, capture, semantics, ownership);
            }

            try { request.VerifyArtifact?.Invoke(artifactBytes); }
            catch (Exception error)
            {
                var failure = new JsonObject
                {
                    ["schemaVersion"] = 1, ["command"] = command, ["evidenceKind"] = "automated-browser", ["visualReview"] = capture ? "pending" : "not-requested",
                    ["artifact"] = BrowserReceipt.ArtifactIdentity(artifact, artifactBytes),
                };
                if (error is BrowserEvidenceException { ProvenanceStatus: { } status }) failure["provenance"] = status;
                failure["error"] = error.Message;
                failure["diagnostics"] = BrowserReceipt.Json((error as BrowserEvidenceException)?.Diagnostics ?? []);
                return Persist(artifact, failure, request.OutDir, command, capture, semantics, ownership);
            }

            var executable = browsers.ResolveExecutable();
            receipt["chrome"] = executable is null ? Chrome("unavailable", null) : Chrome("available", executable);
            if (executable is null) return Skipped(artifact, receipt, outputs, ownership!, command, capture);
            return await Inspect(request, browsers, executable, artifact, artifactBytes, snapshot, inspection, receipt, outputs, ownership!);
        }
        finally
        {
            // ponytail: best effort; upstream reports a retained inspection directory as a cleanup warning (cut with atomic-output).
            if (inspection is not null)
                try { Directory.Delete(inspection, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // The unavailable-browser path (VC:2470-2492): a truthful skipped receipt, exit 2.
    static BrowserEvidenceResult Skipped(string artifact, JsonObject receipt, EvidenceOutputs outputs, Ownership ownership, string command, bool capture)
    {
        receipt["status"] = "skipped";
        foreach (var check in new[] { "containment", "readability", "viewerChrome", "themeStates" }) receipt[check]!["status"] = "skipped";
        if (capture) receipt["captures"]!["status"] = "skipped";
        receipt["error"] = ChromeUnavailableError;
        receipt["diagnostics"] = BrowserReceipt.Json([BrowserReceipt.Failure("viewer/chrome-unavailable", ChromeUnavailableError, new JsonObject { ["artifact"] = artifact },
            new JsonObject { ["executable"] = null }, [ChromeUnavailableFix(command)], Severity.Warning)]);
        var publication = Commit(artifact, outputs, ownership, [(outputs.Receipt, ReceiptBytes(receipt))]);
        if (publication is null) return new(2, receipt, true);
        AppendPublicationFailure(receipt, publication);
        return new(1, receipt, false);
    }

    static async Task<BrowserEvidenceResult> Inspect(BrowserEvidenceRequest request, IBrowserInspectorFactory browsers, string executable, string artifact,
        byte[] artifactBytes, string snapshot, string inspection, JsonObject receipt, EvidenceOutputs outputs, Ownership ownership)
    {
        var (command, capture) = (request.Command, request.Capture);
        try
        {
            var browser = await browsers.LaunchAsync(executable);
            var observations = new List<Observation>();
            var captured = new Dictionary<string, byte[]>();
            try
            {
                foreach (var (theme, viewports) in new[] { ("light", VisualCheckViewports), ("dark", CaptureViewports) })
                    foreach (var (width, height) in viewports)
                    {
                        var index = outputs.Screenshots.ToList().FindIndex(s => (s.Width, s.Height, s.Theme) == (width, height, theme));
                        var staged = index < 0 ? null : Path.Join(inspection, $"capture-{index}.png");
                        var metrics = await browser.InspectAsync(snapshot, width, height, theme, staged);
                        if (!File.ReadAllBytes(snapshot).AsSpan().SequenceEqual(artifactBytes))
                            throw new BrowserEvidenceException("The private visual-check artifact snapshot changed during inspection.");
                        if (staged is not null) captured[outputs.Screenshots[index].Path] = ReadStaged(staged);
                        observations.Add(Observation.From(width, height, theme, metrics));
                    }
            }
            finally
            {
                await browser.DisposeAsync();
            }

            VerifyDelivered(artifact, artifactBytes, command);
            request.VerifyArtifact?.Invoke(artifactBytes);
            VerifyDelivered(artifact, artifactBytes, command);

            var light = observations.Take(VisualCheckViewports.Count).ToList();
            receipt["containment"]!["viewports"] = new JsonArray([.. light.Select(entry => (JsonNode)entry.ToJson())]);
            receipt["readability"]!["viewports"] = new JsonArray([.. light.Select(entry => (JsonNode)entry.ToJson())]);
            receipt["viewerChrome"]!["viewports"] = new JsonArray([.. light.Select(entry => (JsonNode)entry.ToJson())]);
            var screenshots = outputs.Screenshots
                .Select(s => (Entry: observations.First(o => (o.Width, o.Height, o.Theme) == (s.Width, s.Height, s.Theme)), File: Path.GetFileName(s.Path))).ToList();
            receipt["captures"]!["screenshots"] = new JsonArray([.. screenshots.Select(s => (JsonNode)s.Entry.ToJson(s.File))]);
            receipt["themeStates"]!["viewports"] = new JsonArray([.. observations.Select(entry => (JsonNode)new JsonObject
            {
                ["width"] = entry.Width, ["height"] = entry.Height, ["requestedTheme"] = entry.Theme, ["resolvedTheme"] = entry.ResolvedTheme,
                ["ok"] = entry.ResolvedTheme == entry.Theme,
            })]);
            var themeStatePass = observations.All(entry => entry.ResolvedTheme == entry.Theme);
            var containmentPass = observations.All(entry => entry.Ok);
            var readabilityPass = light.All(entry => entry.ReadabilityOk);
            var viewerChromePass = observations.All(entry => entry.ViewerChromeOk);
            receipt["diagnostics"] = BrowserReceipt.Json(BrowserReceipt.ObservationDiagnostics(artifact, observations, light, command));
            receipt["themeStates"]!["status"] = themeStatePass ? "pass" : "fail";
            receipt["containment"]!["status"] = containmentPass ? "pass" : "fail";
            receipt["readability"]!["status"] = readabilityPass ? "pass" : "fail";
            receipt["viewerChrome"]!["status"] = viewerChromePass ? "pass" : "fail";
            if (capture)
            {
                receipt["captures"]!["status"] = "pass";
                receipt["captures"]!["contactSheet"] = Path.GetFileName(outputs.ContactSheet);
            }
            var pass = containmentPass && themeStatePass && readabilityPass && viewerChromePass;
            receipt["status"] = pass ? "pass" : "fail";
            receipt["ok"] = pass;

            var desired = new List<(string Path, byte[] Bytes)>();
            if (capture)
            {
                desired.Add((outputs.ContactSheet, new UTF8Encoding(false).GetBytes(BrowserReceipt.ContactSheetHtml(artifact, receipt, screenshots))));
                desired.AddRange(outputs.Screenshots.Select(s => (s.Path, captured[s.Path])));
                // recordVisualEvidenceFiles (VC:785-797): {sha256, bytes, file} of the sheet and each capture
                receipt["sidecars"]!["files"] = new JsonArray([.. desired.Select(d =>
                {
                    var file = DeliveryStore.Identity(d.Bytes);
                    file["file"] = Path.GetFileName(d.Path);
                    return (JsonNode)file;
                })]);
            }
            desired.Add((outputs.Receipt, ReceiptBytes(receipt)));
            var publication = Commit(artifact, outputs, ownership, desired);
            if (publication is not null)
            {
                AppendPublicationFailure(receipt, publication);
                receipt["ok"] = false;
                receipt["status"] = "fail";
                return new(1, receipt, false);
            }
            return new(pass ? 0 : 1, receipt, true);
        }
        catch (Exception error)
        {
            receipt["status"] = "fail";
            receipt["ok"] = false;
            receipt["error"] = error.Message;
            foreach (var check in new[] { "containment", "readability", "viewerChrome", "themeStates" }) receipt[check]!["status"] = "fail";
            receipt["captures"]!["status"] = capture ? "fail" : "not-requested";
            receipt["captures"]!["screenshots"] = new JsonArray();
            receipt["captures"]!["contactSheet"] = null;
            var carried = error as BrowserEvidenceException;
            if (carried?.ProvenanceStatus is { } status) receipt["provenance"] = status;
            receipt["diagnostics"] = BrowserReceipt.Json(carried?.Diagnostics ?? [RuntimeDiagnostic(artifact, error, carried, command)]);
            return Persist(artifact, receipt, request.OutDir, command, capture, request.Semantics, ownership);
        }
    }

    static Diagnostic RuntimeDiagnostic(string artifact, Exception error, BrowserEvidenceException? carried, string command)
    {
        var evidence = new JsonObject { ["reason"] = error.Message };
        if (!string.IsNullOrEmpty(carried?.Directory)) evidence["retainedDirectory"] = carried.Directory;
        var subject = new JsonObject { ["artifact"] = artifact };
        return carried?.Code switch
        {
            BrowserEvidenceException.TempCleanup => BrowserReceipt.Failure("viewer/temp-cleanup-incomplete",
                "Browser cleanup could not safely finish; inspect the reported retained path.", subject, evidence,
                ["resolve the reported process or filesystem failure; verify directory ownership before manual cleanup"]),
            BrowserEvidenceException.StartupTimeout => BrowserReceipt.Failure("viewer/chrome-startup-timeout",
                "Chrome did not finish its initial DevTools handshake within the startup window.", subject, evidence,
                ["do not edit or simplify the artifact because this is a browser-startup failure",
                    $"retry {command} once after host load subsides; if it repeats, stop and report the environment failure"]),
            _ => BrowserReceipt.Failure($"viewer/{command}-runtime", $"{command} could not complete its Chrome inspection.", subject, evidence,
                [$"resolve the reported Chrome inspection error, then rerun {command}"]),
        };
    }

    // registerStagedEvidence (VC:325-371) reduced to reading what the inspector wrote.
    static byte[] ReadStaged(string staged)
    {
        try { return File.ReadAllBytes(staged); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new BrowserEvidenceException($"visual-check could not inspect staged evidence \"{staged}\": {error.Message}");
        }
    }

    // verifyRegularFileBinding on the delivered artifact (VC:2556-2563): the bytes on disk are still the bytes inspected.
    static void VerifyDelivered(string artifact, byte[] artifactBytes, string command)
    {
        if (DeliveryStore.RegularFile(artifact, "visual-check-artifact", artifactBytes, out _, out var systemCode) is { } changed)
            throw new BrowserEvidenceException($"The delivered artifact changed while {command} was running could not be read safely ({changed}).") { Code = systemCode };
    }

    // executeBrowserEvidence's verifyArtifact (A:5571-5585): check's verifyDeliveryUnchanged against the inspected bytes.
    public static Action<byte[]> DeliveryVerifier(string artifactPath, Provenance provenance) => bytes =>
    {
        var verified = DeliveryStore.VerifyUnchanged(artifactPath, provenance, DeliveryStore.Identity(bytes));
        if (!verified.Ok)
            throw new BrowserEvidenceException(verified.Diagnostics[0].Message) { ProvenanceStatus = verified.Status, Diagnostics = verified.Diagnostics };
    };
}
