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
            receiptId = lockJson?["receiptId"] is JsonValue id && id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : "";
            if (schemaVersion != 1 || pidValue is not double p || p != Math.Floor(p) || p <= 0 || p > 9007199254740991 || !UuidV4().IsMatch(receiptId))
                throw new FormatException();
            pid = (long)p;
        }
        catch { return LockInvalid(lockPath); }
        if (ProcessIsRunning(pid))
            return new("delivery/concurrent-attempt", lockPath, $"Another delivery attempt owns \"{output}\" through lock \"{lockPath}\".");
        return new("delivery/lock-stale", lockPath, $"A stale delivery lock remains at \"{lockPath}\"; recover it serially before retrying.",
            new JsonObject { ["pid"] = pid, ["receiptId"] = receiptId });

        static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;
    }

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
