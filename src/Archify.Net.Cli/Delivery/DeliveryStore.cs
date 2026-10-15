using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Delivery;

// A coded lock failure (deliveryLockCode / deliveryOwnershipCode, A:158-226, :574-581): the lock diagnostic's code and path, the
// stale owner {pid, receiptId}, and the OS code of a failed release.
internal sealed class DeliveryLockException(string code, string lockPath, string message, JsonObject? owner = null, string? systemCode = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public string LockPath { get; } = lockPath;
    public JsonObject? Owner { get; } = owner;
    public string? SystemCode { get; } = systemCode;
}

// commitDeliveryPair's error (A:3560-3576): Details is its deliveryCommitDetails ({reason: cause, rollbackErrors?, backup?}).
internal sealed class DeliveryCommitException(string message, JsonObject details) : Exception(message)
{
    public JsonObject Details { get; } = details;
}

// An owned directory lock and the attempt's sidecar paths (deliveryOwnershipStates, A:584-595). Output is the stable output
// (its realpath when it exists).
internal sealed class DeliveryAttempt(string output, string lockPath, string pendingPath, string provenancePath, string receiptId)
{
    public string Output { get; } = output;
    public string LockPath { get; } = lockPath;
    public string PendingPath { get; } = pendingPath;
    public string ProvenancePath { get; } = provenancePath;
    public string ReceiptId { get; } = receiptId;
    public bool JournalWritten { get; set; }
}

// recordDeliveryFailure's result (A:1607-1872): status failed | absent | unrecorded | not-applicable, an extra diagnostic, and a
// lock error that kept it from recording or releasing.
internal sealed record FailureRecord(string Status, Diagnostic? Diagnostic = null, Exception? LockError = null);

// inspectArtifactDeliveryProvenance's result: the gate's status (current | unknown | failed | locked | mismatch | invalid), the
// delivery receiptId, its one failure diagnostic, and the bytes it read (the expected bindings verifyDeliveryUnchanged re-checks).
internal sealed record Provenance(bool Ok, string Status, string? ReceiptId = null, Diagnostic? Failure = null)
{
    public IReadOnlyList<Diagnostic> Diagnostics => Failure is null ? [] : [Failure];
    public byte[] Artifact { get; init; } = [];
    public byte[]? Sidecar { get; init; }
}

// The delivery sidecars (Q5): the directory lock, the pending journal, success and failure provenance, and the pair commit.
// Cut (survey-delivery §3, D1-D10): atomic-output bindings, lock/journal identity re-checks, quarantine and recovery directories,
// creating the legacy per-artifact lock.
internal sealed partial class DeliveryStore
{
    public const string DirectoryLockName = ".archify-delivery-lock.json";
    public static DeliveryStore Native { get; } = new();

    // The write seam: every sidecar and the artifact go through it as (target, bytes, backup path or null).
    public Action<string, byte[], string?> Publish { get; init; } = PublishFile;

    static OutputPaths Paths => OutputPaths.Native;

    public static string ProvenancePath(string artifact) => SidecarPath.DeliverySidecarPath(artifact, ".delivery.json");
    public static string PendingPath(string artifact) => SidecarPath.DeliverySidecarPath(artifact, ".delivery-pending.json");

    // canonicalExistingDeliveryOutput (A:108-116)
    public static string CanonicalExistingOutput(string output)
    {
        var resolved = NodePath.Resolve(output);
        try { return NativeFs.RealPath(resolved); }
        catch (Exception error) when (NativeFs.ErrorCode(error) is "ENOENT" or "ENOTDIR") { return resolved; }
    }

    // deliveryLockPath (A:136-156): one lock per physical output directory.
    public static string LockPath(string artifact)
    {
        var resolved = NodePath.Resolve(artifact);
        string directory;
        try { directory = Path.GetDirectoryName(NativeFs.RealPath(resolved))!; }
        catch (Exception error) when (NativeFs.ErrorCode(error) is "ENOENT" or "ENOTDIR")
        {
            directory = Path.GetDirectoryName(resolved)!;
            try { directory = NativeFs.RealPath(directory); }
            catch (Exception directoryError) when (NativeFs.ErrorCode(directoryError) is "ENOENT" or "ENOTDIR") { }
        }
        return Path.Join(directory, DirectoryLockName);
    }

    // error.code as the lock diagnostic reports it.
    public static string? SystemCode(Exception error) => error switch
    {
        DeliveryLockException locked => locked.SystemCode,
        SidecarNamespaceException => "ARCHIFY_SIDECAR_NAMESPACE_INDETERMINATE",
        _ => NativeFs.ErrorCode(error),
    };

    // --- the lock ---

    // acquireDeliveryLock (A:520-757) without the legacy fence and the ownership states: exclusive create of the directory lock,
    // an existing one classified and never touched.
    public DeliveryAttempt Acquire(string output, string receiptId, string inputPath)
    {
        var stable = CanonicalExistingOutput(output);
        var lockPath = LockPath(stable);
        var attempt = new DeliveryAttempt(stable, lockPath, PendingPath(stable), ProvenancePath(stable), receiptId);
        RegularLockExists(lockPath);
        if (LockPathsAlias(lockPath, inputPath))
            throw new DeliveryLockException("delivery/lock-path-conflict", lockPath, "Delivery lock path aliases the input specification; choose another output path.");
        FileStream stream;
        try { stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete); }
        catch (IOException error) when (NativeFs.ErrorCode(error) == "EEXIST") { throw RejectExistingLock(lockPath, output); }
        try
        {
            using (stream) stream.Write(Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"receiptId\":\"{receiptId}\",\"pid\":{Environment.ProcessId}}}\n"));
        }
        catch
        {
            File.Delete(lockPath);
            throw;
        }
        return attempt;
    }

    public static void Release(DeliveryAttempt attempt)
    {
        try { File.Delete(attempt.LockPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DeliveryLockException("delivery/lock-release", attempt.LockPath, error.Message, systemCode: NativeFs.ErrorCode(error));
        }
    }

    static DeliveryLockException LockInvalid(string lockPath) =>
        new("delivery/lock-invalid", lockPath, $"Unrecognized delivery lock at \"{lockPath}\"; inspect the existing entry before retrying.");

    // regularDeliveryLockExists (A:167-176)
    static void RegularLockExists(string lockPath)
    {
        FsStat stat;
        try { stat = NativeFs.LStat(lockPath); }
        catch (Exception error) when (NativeFs.ErrorCode(error) is "ENOENT" or "ENOTDIR") { return; }
        catch { throw LockInvalid(lockPath); }
        if (stat.IsDirectory || stat.IsSymbolicLink) throw LockInvalid(lockPath);
    }

    // lockPathsAlias (A:538-551): the lock beside its realpath'd directory against the input.
    static bool LockPathsAlias(string left, string right)
    {
        static string Comparable(string candidate)
        {
            var absolute = NodePath.Resolve(candidate);
            try { return Path.Join(NativeFs.RealPath(Path.GetDirectoryName(absolute)!), Path.GetFileName(absolute)); }
            catch (Exception error) when (NativeFs.ErrorCode(error) is "ENOENT" or "ENOTDIR") { return absolute; }
        }
        var comparableLeft = Comparable(left);
        var comparableRight = Comparable(right);
        return Paths.PathsAlias(Path.GetDirectoryName(comparableLeft)!, Path.GetDirectoryName(comparableRight)!) && Paths.PathsAlias(comparableLeft, comparableRight);
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex UuidV4();

    // rejectExistingDeliveryLock (A:176-216): a readable {schemaVersion:1, receiptId:<v4>, pid:<safe integer>} names its owner.
    static DeliveryLockException RejectExistingLock(string lockPath, string output)
    {
        long pid;
        string receiptId;
        try
        {
            using var reader = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            reader.CopyTo(buffer);
            var lockJson = JsonNode.Parse(new UTF8Encoding(false).GetString(buffer.ToArray())) as JsonObject;
            var schemaVersion = Number(lockJson?["schemaVersion"]);
            var pidValue = Number(lockJson?["pid"]);
            receiptId = Text(lockJson?["receiptId"]) ?? "";
            if (schemaVersion != 1 || pidValue is not double p || p != Math.Floor(p) || p <= 0 || p > 9007199254740991 || !UuidV4().IsMatch(receiptId))
                throw new FormatException();
            pid = (long)p;
        }
        catch { return LockInvalid(lockPath); }
        if (ProcessIsRunning(pid))
            return new("delivery/concurrent-attempt", lockPath, $"Another delivery attempt owns \"{output}\" through lock \"{lockPath}\".");
        return new("delivery/lock-stale", lockPath, $"A stale delivery lock remains at \"{lockPath}\"; recover it serially before retrying.",
            new JsonObject { ["pid"] = pid, ["receiptId"] = receiptId });
    }

    static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? node.D() : null;
    static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    // processIsRunning (A:218-226): alive unless the pid is gone (ESRCH); a process we may not open still counts as alive, as EPERM
    // does upstream. libuv maps OpenProcess's invalid-parameter answer for an impossible pid to ESRCH as well. ponytail: Windows
    // recycles pids, so a stale lock can read as a concurrent attempt (upstream has the same flaw); compare start times if it bites.
    public static bool ProcessIsRunning(long pid)
    {
        if (pid <= 0 || pid > int.MaxValue) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    // --- the journal and provenance ---

    static byte[] Sidecar(JsonObject value) => new UTF8Encoding(false).GetBytes(Js.Stringify(value, 2) + "\n");

    static JsonObject Identity(byte[] bytes) => new() { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length };

    // beginDeliveryAttempt (A:985-1007): written before render; only a successful commit removes it.
    public void BeginJournal(DeliveryAttempt attempt, string input)
    {
        foreach (var protectedPath in new[] { input, attempt.Output, attempt.ProvenancePath })
            if (Paths.PathsAlias(attempt.PendingPath, protectedPath)) throw new IOException("Delivery journal aliases an input or output.");
        Publish(attempt.PendingPath, Sidecar(new JsonObject
        {
            ["schemaVersion"] = 1, ["command"] = "deliver", ["status"] = "pending", ["receiptId"] = attempt.ReceiptId, ["input"] = input,
            ["output"] = NodePath.Resolve(attempt.Output),
        }), null);
        attempt.JournalWritten = true;
    }

    // deliverySuccessProvenance (A:1889-1901), serialised.
    public static byte[] SuccessProvenance(JsonObject receipt) => Sidecar(new JsonObject
    {
        ["schemaVersion"] = 1, ["receiptId"] = receipt["receiptId"]!.DeepClone(), ["status"] = "current", ["command"] = "deliver",
        ["type"] = receipt["type"]!.DeepClone(), ["input"] = receipt["input"]!.DeepClone(), ["output"] = receipt["output"]!.DeepClone(),
        ["specification"] = receipt["specification"]!.DeepClone(), ["artifact"] = receipt["artifact"]!.DeepClone(),
    });

    [GeneratedRegex(@"\.html?\z", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlExtension();

    // recordDeliveryFailure (A:1607-1872) minus the target snapshot: the lock (acquired here when the attempt holds none), the
    // journal (created if missing), then the failed marker when an artifact exists ('absent' otherwise), then the lock released.
    public FailureRecord RecordFailure(string? output, string stage, string input, string error, string receiptId, DeliveryAttempt? attempt)
    {
        if (output is null || !HtmlExtension().IsMatch(output)) return new("not-applicable");
        if (attempt is null)
        {
            try { attempt = Acquire(output, receiptId, input); }
            catch (Exception lockError)
            {
                var reported = lockError is SidecarNamespaceException { Reason: var reason } && reason["code"]?.GetValue<string>() == "sidecar-directory-missing"
                    ? new NativeFsException("ENOENT", $"Delivery lock parent directory does not exist for \"{output}\".")
                    : lockError;
                return new("unrecorded", LockError: reported);
            }
        }
        Exception? journalError = null;
        if (!attempt.JournalWritten)
        {
            try { BeginJournal(attempt, input); }
            catch (Exception cause) { journalError = cause; }
        }
        var recorded = RecordFailedMarker(attempt, stage, input, error, journalError);
        try { Release(attempt); }
        catch (DeliveryLockException lockError) { return recorded with { LockError = lockError }; }
        return recorded;
    }

    FailureRecord RecordFailedMarker(DeliveryAttempt attempt, string stage, string input, string error, Exception? journalError)
    {
        var output = attempt.Output;
        byte[]? artifact = null;
        try
        {
            // A failed marker needs no hash to invalidate old evidence; a non-regular entry is never read.
            if (!Directory.Exists(output)) artifact = File.ReadAllBytes(output);
        }
        catch (Exception cause) when (NativeFs.ErrorCode(cause) is "ENOENT" or "ENOTDIR") { return new("absent"); }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException) { }
        var provenancePath = attempt.ProvenancePath;
        var subject = new JsonObject { ["output"] = output, ["provenance"] = provenancePath };
        bool aliasesProtectedPath;
        try { aliasesProtectedPath = Paths.PathsAlias(provenancePath, input) || Paths.PathsAlias(provenancePath, output); }
        catch (Exception aliasError)
        {
            var evidence = new JsonObject { ["reason"] = aliasError.Message };
            if (SystemCode(aliasError) is { } code) evidence["systemCode"] = code;
            return new("unrecorded", new Diagnostic("delivery/provenance-path-resolution", Severity.Error,
                "Delivery failure provenance could not resolve its target safely.", subject, evidence,
                ["remove the sidecar path conflict or symbolic-link cycle, then rerun deliver"], null));
        }
        if (aliasesProtectedPath)
            return new("unrecorded", new Diagnostic("delivery/provenance-target-alias", Severity.Error,
                "Delivery failure provenance could not be recorded without replacing an input or the artifact.", subject,
                new JsonObject { ["input"] = NodePath.Resolve(input) },
                ["choose an output whose .delivery.json sidecar is distinct from every input and the HTML artifact"], null));
        var marker = new JsonObject
        {
            ["schemaVersion"] = 1, ["receiptId"] = attempt.ReceiptId, ["status"] = "failed", ["command"] = "deliver", ["stage"] = stage,
            ["input"] = input, ["output"] = output,
        };
        if (artifact is not null) marker["artifact"] = Identity(artifact);
        marker["error"] = error;
        try
        {
            Publish(provenancePath, Sidecar(marker), null);
            return new("failed");
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            var evidence = new JsonObject { ["reason"] = writeError.Message };
            if (SystemCode(writeError) is { } code) evidence["systemCode"] = code;
            evidence["failureJournalRecorded"] = journalError is null;
            if (journalError is not null) evidence["journalError"] = journalError.Message;
            return new("unrecorded", new Diagnostic("delivery/provenance-write", Severity.Error,
                "The failed delivery could not persist its stale-artifact marker.", subject, evidence,
                ["restore write access to the output directory, then rerun deliver before trusting the artifact"], null));
        }
    }

    // --- the commit (Q5 step 8-9) ---

    // The artifact then its provenance, each swapped in beside its target; a provenance failure puts the previous HTML back (or
    // removes the new one). The journal goes only after both landed, then the backup.
    public void Commit(DeliveryAttempt attempt, byte[] html, byte[] provenance)
    {
        var output = attempt.Output;
        var backup = File.Exists(output) ? Path.Join(Path.GetDirectoryName(output), $".archify-delivery-{Guid.NewGuid():N}.bak") : null;
        var htmlPublished = false;
        try
        {
            Publish(output, html, backup);
            htmlPublished = true;
            Publish(attempt.ProvenancePath, provenance, null);
        }
        catch (Exception cause)
        {
            var details = new JsonObject { ["reason"] = cause.Message };
            try
            {
                if (backup is not null && File.Exists(backup)) File.Move(backup, output, overwrite: true);
                else if (htmlPublished) File.Delete(output);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                // D4: no recovery directory; the journal stays, so check fails closed, and the backup is named here.
                details["rollbackErrors"] = new JsonArray(rollback.Message);
                if (backup is not null) details["backup"] = backup;
                throw new DeliveryCommitException("Delivery pair commit failed and its previous files could not be fully restored.", details);
            }
            throw new DeliveryCommitException("Delivery pair commit failed; the previous files were restored.", details);
        }
        File.Delete(attempt.PendingPath);
        // ponytail: a backup that cannot be removed is left beside the target; it is not evidence and never read again.
        try { if (backup is not null) File.Delete(backup); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // A temp file beside the target, then File.Replace (which keeps the target's ACL and can leave a backup) or File.Move. The temp
    // name is not derived from the target's, so a 255-unit name still gets one.
    public static void PublishFile(string target, byte[] bytes, string? backup)
    {
        var temp = Path.Join(Path.GetDirectoryName(target), $".archify-delivery-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
            RetrySharing(() =>
            {
                if (File.Exists(target)) File.Replace(temp, target, backup, ignoreMetadataErrors: true);
                else File.Move(temp, target);
            });
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ponytail: a scanner or viewer holding the target without FILE_SHARE_DELETE makes the swap fail with a sharing violation (32)
    // or access denied (5); it is retried for about a second, then the commit fails.
    static void RetrySharing(Action swap)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                swap();
                return;
            }
            catch (Exception error) when (attempt < 10 && error is IOException or UnauthorizedAccessException && (error.HResult & 0xFFFF) is 32 or 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    // --- the provenance gate (check --require-provenance, survey-delivery §2.4) ---

    static readonly HashSet<string> Types = [.. Enum.GetValues<DiagramType>().Select(type => type.Name())];

    // inspectArtifactDeliveryProvenance (A:5309-5424): (a) the artifact's realpath read as a regular file, then gates (b)-(i). With
    // `expected` (verifyDeliveryUnchanged) the artifact and the sidecar must still hold the bytes that inspection read.
    // Cut with atomic-output (D1, D2): identity and link-count bindings (a hard link or a same-bytes replacement passes) and the
    // re-verification of the artifact binding right after the gate.
    public static Provenance InspectProvenance(string artifactPath, bool requireProvenance, Provenance? expected = null)
    {
        var stable = CanonicalExistingOutput(artifactPath);
        if (RegularFile(stable, "delivery-artifact", expected?.Artifact, out var artifact) is { } code)
        {
            var reason = $"The artifact is not a stable regular file ({code}).";
            var message = $"Could not read the HTML artifact: {reason}";
            return new(false, "unknown", Failure: new Diagnostic("input/artifact-unreadable", Severity.Error, message, new JsonObject { ["artifact"] = artifactPath },
                new JsonObject { ["reason"] = reason }, ["provide an existing readable .html artifact, then rerun the checker"], null));
        }
        return Gate(artifactPath, stable, artifact, requireProvenance, expected?.Sidecar) with { Artifact = artifact };
    }

    // verifyDeliveryUnchanged (A:5426-5448): after the checker, a second inspection bound to the first one's bytes, which must agree
    // with it and with what the checker read.
    public static Provenance VerifyUnchanged(string artifactPath, Provenance expected, JsonNode? checkedArtifact)
    {
        var current = InspectProvenance(artifactPath, requireProvenance: false, expected);
        if (!current.Ok) return current;
        var expectedIdentity = Identity(expected.Artifact);
        if (current.Status == expected.Status && current.ReceiptId == expected.ReceiptId && current.Artifact.AsSpan().SequenceEqual(expected.Artifact)
            && Text(checkedArtifact?["sha256"]) == expectedIdentity["sha256"]!.GetValue<string>() && Number(checkedArtifact?["bytes"]) == expected.Artifact.Length)
            return current;
        return new(false, "mismatch", Failure: new Diagnostic("delivery/provenance-mismatch", Severity.Error, "The artifact or delivery record changed during inspection.",
            new JsonObject { ["artifact"] = artifactPath },
            new JsonObject { ["expected"] = expectedIdentity, ["checked"] = checkedArtifact?.DeepClone(), ["current"] = Identity(current.Artifact) },
            ["finish delivery and other writes before rerunning the checker"], null));
    }

    // inspectDeliveryProvenance (A:1903-2178), gates (b)-(i) over the artifact bytes gate (a) read.
    // Cut with the legacy migration (survey-delivery §3): raw-head and legacy-extension journals and sidecars are not looked for.
    static Provenance Gate(string artifactPath, string stable, byte[] artifact, bool requireProvenance, byte[]? expectedSidecar)
    {
        var sidecar = ProvenancePath(artifactPath);
        var pending = PendingPath(artifactPath);
        string[] locks = [LockPath(stable), .. LegacyLockPaths(artifactPath, stable)];
        JsonObject Subject(string key, string value) => new() { ["artifact"] = artifactPath, [key] = value };
        Provenance Failed(string status, string code, string message, JsonObject subject, JsonObject evidence, string fix, string? receiptId = null) =>
            new(false, status, receiptId, new Diagnostic(code, Severity.Error, message, subject, evidence, [fix], null));

        // (b) an unfinished or failed attempt's journal
        try
        {
            if (EntryExists(pending))
                return Failed("failed", "delivery/provenance-failed",
                    "A delivery attempt is unfinished or failed; complete a successful deliver before trusting this artifact.", Subject("provenance", sidecar),
                    new JsonObject { ["pendingJournal"] = pending, ["found"] = true },
                    "finish any active delivery, then rerun deliver successfully; retain the pending journal until recovery succeeds");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Unverified(artifactPath, pending, new JsonObject { ["reason"] = error.Message });
        }
        // (c) any lock, the directory's or a legacy per-artifact one
        foreach (var lockPath in locks)
        {
            try
            {
                if (EntryExists(lockPath))
                    return Failed("locked", "delivery/provenance-locked",
                        "Delivery ownership is unresolved while the output lock remains; complete or recover delivery before trusting this artifact.",
                        Subject("lock", lockPath), new JsonObject { ["deliveryLock"] = lockPath, ["found"] = true },
                        "finish the active delivery or inspect and recover the preserved lock, then rerun deliver successfully");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Unverified(artifactPath, lockPath, new JsonObject { ["reason"] = error.Message });
            }
        }
        // (d) no sidecar: unknown, or a failure when provenance is required
        bool found;
        try { found = EntryExists(sidecar); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Unverified(artifactPath, sidecar, new JsonObject { ["reason"] = error.Message });
        }
        if (!found)
            return requireProvenance
                ? Failed("unknown", "delivery/provenance-required", "Delivery provenance is required, but the artifact has no .delivery.json sidecar.",
                    Subject("provenance", sidecar), new JsonObject { ["required"] = true, ["found"] = false },
                    "rerun deliver for the current specification, then retry with --require-provenance")
                : new(true, "unknown");
        // (e) a regular file holding a supported receipt
        JsonObject receipt;
        byte[] sidecarBytes;
        try
        {
            if (RegularFile(sidecar, "delivery-provenance", expectedSidecar, out sidecarBytes) is { } code)
                throw new InvalidDataException($"Delivery provenance is not a stable regular file ({code}).");
            receipt = SupportedReceipt(sidecarBytes);
        }
        catch (Exception error)
        {
            return Failed("invalid", "delivery/provenance-invalid", $"Could not read delivery provenance: {error.Message}", Subject("provenance", sidecar),
                new JsonObject { ["reason"] = error.Message }, "rerun deliver to replace the invalid provenance sidecar");
        }
        return Verdict() with { Sidecar = sidecarBytes };

        Provenance Verdict()
        {
            // (f) the recorded output is this artifact
            var recordedOutput = Text(receipt["output"])!;
            PathRelation identity;
            try { identity = PathSemantics.Native.SameEntry(recordedOutput, artifactPath); }
            catch (Exception error)
            {
                var reason = new JsonObject { ["code"] = "path-identity-check-failed" };
                if (NativeFs.ErrorCode(error) is { } systemCode) reason["systemCode"] = systemCode;
                identity = new("unknown", reason);
            }
            if (identity.Status == "unknown")
                return Failed("invalid", "delivery/provenance-output-indeterminate",
                    "Delivery provenance could not determine whether its recorded output identifies the inspected artifact.",
                    new JsonObject { ["artifact"] = artifactPath, ["provenance"] = sidecar, ["recordedOutput"] = recordedOutput },
                    new JsonObject { ["pathIdentity"] = identity.Reason.DeepClone() },
                    "restore access to both the artifact and its recorded output path, then rerun deliver successfully");
            if (identity.Status != "match")
                return Unverified(artifactPath, sidecar, new JsonObject
                {
                    ["reason"] = "The recorded delivery output does not identify this artifact.", ["recordedOutput"] = recordedOutput,
                    ["pathIdentity"] = identity.Reason.DeepClone(),
                });
            var receiptId = Text(receipt["receiptId"])!;
            // (g) the latest attempt failed
            if (Text(receipt["status"]) == "failed")
                return Failed("failed", "delivery/provenance-failed", "The artifact is stale because the latest delivery attempt failed.", Subject("provenance", sidecar),
                    new JsonObject { ["stage"] = receipt["stage"]!.DeepClone(), ["deliveryReceiptId"] = receiptId },
                    "repair the specification and complete a successful deliver before checking this artifact", receiptId);
            // (h) other bytes than delivered
            var recorded = receipt["artifact"]!;
            var actualSha256 = Identity(artifact)["sha256"]!.GetValue<string>();
            if (Text(recorded["sha256"]) != actualSha256 || Number(recorded["bytes"]) != artifact.Length)
                return Failed("mismatch", "delivery/provenance-mismatch", "The artifact bytes do not match their delivery provenance.", Subject("provenance", sidecar),
                    new JsonObject
                    {
                        ["expectedSha256"] = recorded["sha256"]!.DeepClone(), ["actualSha256"] = actualSha256, ["expectedBytes"] = recorded["bytes"]!.DeepClone(),
                        ["actualBytes"] = artifact.Length,
                    },
                    "restore the delivered artifact or rerun deliver to create a matching artifact and provenance pair", receiptId);
            // (i)
            return new(true, "current", receiptId);
        }
    }

    // invalidProvenance (A:2180-2189): evidence is {reason, ...extra}; the subject's artifact is the path as given.
    static Provenance Unverified(string artifactPath, string sidecar, JsonObject evidence) =>
        new(false, "invalid", Failure: new Diagnostic("delivery/provenance-invalid", Severity.Error, "Delivery provenance could not be verified.",
            new JsonObject { ["artifact"] = artifactPath, ["provenance"] = sidecar }, evidence,
            ["restore access to the delivery records, then rerun deliver successfully"], null));

    // legacyDeliveryLockPaths (A:118-134): `<artifact minus .htm(l)>.delivery-lock.json`, read only (D5: deliver never creates one).
    static IEnumerable<string> LegacyLockPaths(params string[] artifacts) =>
        artifacts.Select(artifact => HtmlExtension().Replace(NodePath.Resolve(artifact), "") + ".delivery-lock.json")
            .Where(lockPath => OperatingSystem.IsWindows() ? Path.GetFileName(lockPath).Length <= 255 : Encoding.UTF8.GetByteCount(Path.GetFileName(lockPath)) <= 255)
            .Distinct(StringComparer.Ordinal);

    // pathEntryExists (A:759-767)
    static bool EntryExists(string path)
    {
        try
        {
            NativeFs.LStat(path);
            return true;
        }
        catch (Exception error) when (NativeFs.ErrorCode(error) is "ENOENT" or "ENOTDIR") { return false; }
    }

    // captureRegularFileBinding (atomic-output.mjs:302-421) reduced to its verdicts on Windows: lstat, a regular file, its bytes, and,
    // against an earlier inspection, the same bytes. The reason code, or null with the content.
    static string? RegularFile(string path, string subject, byte[]? expected, out byte[] content)
    {
        content = [];
        FsStat stat;
        try { stat = NativeFs.LStat(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return $"{subject}-inspection-failed"; }
        if (stat.IsDirectory || stat.IsSymbolicLink) return $"{subject}-not-regular-file";
        try { content = File.ReadAllBytes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return $"{subject}-handle-inspection-failed"; }
        return expected is null || content.AsSpan().SequenceEqual(expected) ? null : $"{subject}-content-changed";
    }

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    private static partial Regex Sha256Hex();

    // A:2046-2060: the sidecar's UTF-8 text through JSON.parse, then the schema of a current or failed receipt.
    // ponytail: a sidecar with duplicate keys fails here with .NET's message where JSON.parse keeps the last; deliver never writes one.
    static JsonObject SupportedReceipt(byte[] bytes)
    {
        static bool Absolute(string? path) => path is { Length: > 0 } && (OperatingSystem.IsWindows()
            ? path[0] is '/' or '\\' || path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\'
            : path[0] == '/');
        static bool IdentityValid(JsonNode? value) => value is JsonObject identity && Text(identity["sha256"]) is { } sha256 && Sha256Hex().IsMatch(sha256)
            && Number(identity["bytes"]) is double bytes && bytes == Math.Floor(bytes) && bytes >= 0 && bytes <= 9007199254740991;

        var receipt = JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
        var status = Text(receipt?["status"]);
        if (receipt is null || Number(receipt["schemaVersion"]) != 1 || Text(receipt["command"]) != "deliver"
            || Text(receipt["receiptId"]) is not { } receiptId || !UuidV4().IsMatch(receiptId)
            || !Absolute(Text(receipt["output"])) || !Absolute(Text(receipt["input"]))
            || status is not ("current" or "failed")
            || status == "current" && (Text(receipt["type"]) is not { } type || !Types.Contains(type) || !IdentityValid(receipt["specification"]) || !IdentityValid(receipt["artifact"]))
            || status == "failed" && Text(receipt["stage"]) is null)
            throw new InvalidDataException("Delivery provenance does not conform to the supported receipt schema.");
        return receipt;
    }

    // --- diagnostics ---

    // deliveryLockFailureDiagnostic (A:2410-2459)
    public static Diagnostic LockDiagnostic(string output, Exception error)
    {
        var locked = error as DeliveryLockException;
        var code = locked?.Code ?? "delivery/lock-acquire";
        var lockPath = locked?.LockPath ?? LockPath(output);
        var evidence = new JsonObject { ["reason"] = error.Message };
        if (SystemCode(error) is { } systemCode) evidence["systemCode"] = systemCode;
        foreach (var (key, value) in locked?.Owner ?? []) evidence[key] = value?.DeepClone();
        string[] fixes = code switch
        {
            "delivery/concurrent-attempt" => ["wait for the active delivery to finish, then retry"],
            "delivery/lock-stale" => ["confirm no delivery attempt is active for this output", $"remove only the preserved lock \"{lockPath}\", then rerun deliver successfully"],
            "delivery/lock-path-conflict" => ["choose an output whose lock path is distinct from the input specification"],
            "delivery/lock-invalid" => ["inspect and preserve the unrecognized lock entry, or choose another output path"],
            "delivery/lock-release" => ["preserve the reported lock, restore permission to remove that exact entry, then recover serially before retrying"],
            _ => ["resolve the reported filesystem error and any lock cleanup error, then retry"],
        };
        var message = code == "delivery/lock-release"
            ? $"Delivery lock could not be released for \"{output}\": {error.Message}"
            : $"Could not start delivery for \"{output}\": {error.Message}";
        return new Diagnostic(code, Severity.Error, message, new JsonObject { ["output"] = output, ["lock"] = lockPath }, evidence, fixes, null);
    }
}
