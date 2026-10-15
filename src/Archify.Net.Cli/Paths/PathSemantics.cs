using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net.Cli.Paths;

// {status, reason}: status is match | different | unknown; reason is upstream's object, key order kept, ready for evidence.relation.
internal sealed record PathRelation(string Status, JsonObject Reason);

// resolvePhysicalLocation's location: kind existing (Path) or future (AncestorPath + Unresolved).
internal sealed record PhysicalLocation(string Kind, string? Path, string? AncestorPath, IReadOnlyList<string> Unresolved);

// status resolved with a Location, or upstream's unknown failure.
internal sealed record PhysicalResolution(string Status, JsonObject Reason, PhysicalLocation? Location = null);

// status resolved with DirectoryPath + ComponentKey, or upstream's unknown failure.
internal sealed record SidecarNamespaceKey(string Status, JsonObject Reason, string? DirectoryPath = null, string? ComponentKey = null);

// UP/renderers/shared/path-semantics.mjs, whole, except the probe-cleanup quarantine (:260-324): a probe is deleted by name while
// its handle is still open. Relative inputs resolve against the process cwd as upstream's do; the CLI passes absolute paths.
internal sealed partial class PathSemantics(IPathFs fs)
{
    public static PathSemantics Native { get; } = new(NativeFs.Ops);

    // realpathSync.native on this instance's filesystem, for callers that resolve before asking the semantics.
    public string RealPath(string path) => fs.RealPath(path);

    const int MaxSymlinkDepth = 64;
    const int RecentProbeNameLimit = 128;
    static readonly bool Windows = OperatingSystem.IsWindows();
    static readonly char Sep = Windows ? '\\' : '/';
    // Module state upstream: one set per process, oldest name evicted first.
    static readonly HashSet<string> RecentProbeNames = [];
    static readonly Queue<string> RecentProbeOrder = new();

    // JS `.` excludes every line terminator and `$` (no m flag) is end of input: hence [^\n\r\u2028\u2029] and \z below.
    const string Dot = @"[^\n\r\u2028\u2029]";
    const RegexOptions Iu = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    [GeneratedRegex(@"^[\\/]{2}\.[\\/]")] private static partial Regex DeviceNamespace();
    [GeneratedRegex(@"^[\\/]{2}\?[\\/]")] private static partial Regex QuestionNamespace();
    [GeneratedRegex(@"^\\\\\?\\(?:GLOBALROOT|Device)(?:\\|\z)", Iu)] private static partial Regex GlobalRoot();
    [GeneratedRegex(@"^\\\\\?\\Volume\{", Iu)] private static partial Regex VolumeGuid();
    [GeneratedRegex(@"^(\\\\\?\\UNC\\([^\\]+)\\([^\\]+))(?:\\(" + Dot + @"*))?\z", Iu)] private static partial Regex ExtendedUnc();
    [GeneratedRegex(@"^(\\\\\?\\[A-Za-z]:\\)(" + Dot + @"*)\z")] private static partial Regex ExtendedDrive();
    [GeneratedRegex(@"^[\\/]{2}([^\\/]+)[\\/]([^\\/]+)(?:[\\/](" + Dot + @"*))?\z")] private static partial Regex Unc();
    [GeneratedRegex(@"^([A-Za-z]:)[\\/](" + Dot + @"*)\z")] private static partial Regex Drive();
    [GeneratedRegex(@"^\\\\\?\\([A-Za-z]:\\)\z")] private static partial Regex OrdinaryExtendedDrive();
    [GeneratedRegex(@"^\\\\\?\\UNC\\([^\\]+)\\([^\\]+)\\\z", Iu)] private static partial Regex OrdinaryExtendedUnc();
    [GeneratedRegex(@"[\u0000-\u001F""/\\[\]:|<>+=;,?*]")] private static partial Regex SmbShareForbidden();
    [GeneratedRegex(@"~[1-9][0-9]*(?:\.|\z)")] private static partial Regex WindowsShortName();
    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])\z")] private static partial Regex WindowsDevice();

    sealed record Entry(string RealPath, FsStat Stat);

    // resolvePlan's {ok, existing, entry | ancestor + unresolved} or {ok:false, failure}.
    sealed record Resolution(PathRelation? Failure, Entry? Entry = null, Entry? Ancestor = null, IReadOnlyList<string>? Unresolved = null)
    {
        public bool Existing => Entry is not null;
    }

    sealed record Plan(string Root, List<string> Segments);

    // /^(?:IPC\$|pipe|mailslot)$/iu — ASCII letters fold, and under /iu ſ (U+017F) folds to s; nothing else reaches these letters.
    public static bool IsWindowsIpcShare(string share) =>
        string.Concat(share.Select(c => c == 'ſ' ? 's' : c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c)) is "ipc$" or "pipe" or "mailslot";

    public static bool IsValidWindowsSmbShareName(string share) =>
        share is { Length: > 0 and <= 80 } && !SmbShareForbidden().IsMatch(share) && share[^1] is not (' ' or '.');

    static PathRelation Result(string status, string code, JsonObject? details = null)
    {
        var reason = new JsonObject { ["code"] = code };
        if (details is not null) Spread(reason, details);
        return new PathRelation(status, reason);
    }

    static PathRelation Unknown(string code, JsonObject? details = null) => Result("unknown", code, details);

    // `{...source}`: a key already present keeps its position and takes the new value, as in JS.
    static JsonObject Spread(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source) target[key] = value?.DeepClone();
        return target;
    }

    static PathRelation SystemFailure(string code, Exception error, JsonObject? details = null)
    {
        var merged = details is null ? [] : Spread([], details);
        if (NativeFs.ErrorCode(error) is { } systemCode) merged["systemCode"] = systemCode;
        return Unknown(code, merged);
    }

    // The exceptions a filesystem call raises (upstream catches whatever fs throws).
    static bool IsFsError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    static JsonObject Side(string side, int? segmentIndex = null)
    {
        var details = new JsonObject { ["side"] = side };
        if (segmentIndex is { } index) details["segmentIndex"] = index;
        return details;
    }

    static JsonObject Method(string method) => new() { ["method"] = method };

    static bool IsSep(char c) => c == '/' || (Windows && c == '\\');
    static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    static bool IsAbsolute(string path) =>
        path.Length > 0 && (IsSep(path[0]) || (Windows && path.Length > 2 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSep(path[2])));
    static bool HasDriveRoot(string path) => Windows && path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':';   // path.parse root of a non-absolute path

    // path.join(directory, name) for a resolved directory and one component.
    static string Join(string directory, string name) => directory.Length > 0 && IsSep(directory[^1]) ? directory + name : directory + Sep + name;

    // path.dirname: the root (with its separator) is its own parent.
    internal static string DirName(string path)
    {
        var rootLength = (Path.GetPathRoot(path) ?? "").Length;
        // A UNC root (`\\server\share`) comes back without its separator; a drive root (`C:\`) already ends in one.
        if (rootLength > 0 && !IsSep(path[rootLength - 1]) && rootLength < path.Length && IsSep(path[rootLength])) rootLength++;
        var end = path.Length;
        while (end > rootLength && IsSep(path[end - 1])) end--;
        while (end > rootLength && !IsSep(path[end - 1])) end--;
        while (end > rootLength && IsSep(path[end - 1])) end--;
        return end <= rootLength ? path[..rootLength] : path[..end];
    }

    static (string? Path, PathRelation? Failure) AbsolutePath(string? input, string side, string? basePath = null)
    {
        if (string.IsNullOrEmpty(input)) return (null, Unknown("invalid-path", Side(side)));
        if (IsAbsolute(input)) return (input, null);
        if (HasDriveRoot(input)) return (null, Unknown("drive-relative-path", Side(side)));
        basePath ??= Directory.GetCurrentDirectory();
        return (basePath.EndsWith(Sep) ? basePath + input : basePath + Sep + input, null);
    }

    static (Plan? Plan, PathRelation? Failure) RootFailure(string code, string side) => (null, Unknown(code, Side(side)));

    static (List<string>? Segments, PathRelation? Failure) SplitWindowsExtendedTail(string tail, string side, bool internalRelativeLinkTarget)
    {
        var authored = tail.Split('\\');
        // A single final separator is harmless, while an internal empty segment would require normalizing a raw extended path
        // before it reaches Windows.
        if (!internalRelativeLinkTarget && authored[..^1].Any(component => component.Length == 0))
            return (null, Unknown("windows-root-invalid", Side(side)));
        var segments = authored.Where(component => component.Length > 0).ToList();
        if (!internalRelativeLinkTarget && segments.Any(component => component is "." or ".."))
            return (null, Unknown("windows-root-invalid", Side(side)));
        return (segments, null);
    }

    // splitWindowsAbsolute (:101-186)
    static (Plan? Plan, PathRelation? Failure) SplitWindowsAbsolute(string absolute, string side, bool internalRelativeLinkTarget)
    {
        if (DeviceNamespace().IsMatch(absolute)) return RootFailure("windows-namespace-unsupported", side);
        if (QuestionNamespace().IsMatch(absolute) && !absolute.StartsWith(@"\\?\", StringComparison.Ordinal))
            return RootFailure("windows-namespace-unsupported", side);

        if (absolute.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (absolute.Contains('/')) return RootFailure("windows-root-invalid", side);
            if (GlobalRoot().IsMatch(absolute) || VolumeGuid().IsMatch(absolute)) return RootFailure("windows-namespace-unsupported", side);

            var extendedUnc = ExtendedUnc().Match(absolute);
            if (extendedUnc.Success)
            {
                var (server, share) = (extendedUnc.Groups[2].Value, extendedUnc.Groups[3].Value);
                if (IsWindowsIpcShare(share)) return RootFailure("windows-namespace-unsupported", side);
                if (!IsValidWindowsSmbShareName(share)) return RootFailure("windows-root-invalid", side);
                if (server is "." or ".." || share is "." or "..") return RootFailure("windows-root-invalid", side);
                var tail = SplitWindowsExtendedTail(extendedUnc.Groups[4].Value, side, internalRelativeLinkTarget);
                if (tail.Failure is not null) return (null, tail.Failure);
                return (new Plan(extendedUnc.Groups[1].Value + "\\", tail.Segments!), null);
            }

            var extendedDrive = ExtendedDrive().Match(absolute);
            if (extendedDrive.Success)
            {
                var tail = SplitWindowsExtendedTail(extendedDrive.Groups[2].Value, side, internalRelativeLinkTarget);
                if (tail.Failure is not null) return (null, tail.Failure);
                return (new Plan(extendedDrive.Groups[1].Value, tail.Segments!), null);
            }
            return RootFailure("windows-root-invalid", side);
        }

        var unc = Unc().Match(absolute);
        if (unc.Success)
        {
            var (server, share) = (unc.Groups[1].Value, unc.Groups[2].Value);
            if (IsWindowsIpcShare(share)) return RootFailure("windows-namespace-unsupported", side);
            if (!IsValidWindowsSmbShareName(share)) return RootFailure("windows-root-invalid", side);
            if (server is "." or ".." || share is "." or "..") return RootFailure("windows-root-invalid", side);
            return (new Plan($@"\\{server}\{share}\", [.. unc.Groups[3].Value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)]), null);
        }

        var drive = Drive().Match(absolute);
        if (drive.Success)
            return (new Plan(drive.Groups[1].Value + "\\", [.. drive.Groups[2].Value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)]), null);
        return RootFailure("windows-root-invalid", side);
    }

    static (Plan? Plan, PathRelation? Failure) SplitAbsolute(string absolute, string side, bool internalRelativeLinkTarget = false)
    {
        if (Windows) return SplitWindowsAbsolute(absolute, side, internalRelativeLinkTarget);
        var root = Path.GetPathRoot(absolute) ?? "";
        return (new Plan(root, [.. absolute[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)]), null);
    }

    static string? OrdinaryWindowsRoot(string extendedRoot)
    {
        var drive = OrdinaryExtendedDrive().Match(extendedRoot);
        if (drive.Success) return drive.Groups[1].Value;
        var unc = OrdinaryExtendedUnc().Match(extendedRoot);
        return unc.Success ? $@"\\{unc.Groups[1].Value}\{unc.Groups[2].Value}\" : null;
    }

    static bool HasStableIdentity(FsStat stat) => stat.Identity is { FileIndex: not 0 };

    static PathRelation CompareKnownEntries(Entry left, Entry right, string operation)
    {
        if (HasStableIdentity(left.Stat) && HasStableIdentity(right.Stat))
        {
            var (l, r) = (left.Stat.Identity!.Value, right.Stat.Identity!.Value);
            var matches = l.VolumeSerial == r.VolumeSerial && l.FileIndex == r.FileIndex;
            return Result(matches ? "match" : "different", matches ? "file-identity-match" : "file-identity-different",
                new JsonObject { ["operation"] = operation, ["method"] = "bigint-stat" });
        }
        if (left.RealPath == right.RealPath)
            return Result("match", "native-realpath-match", new JsonObject { ["operation"] = operation, ["method"] = "native-realpath" });
        return Unknown("file-identity-unavailable", new JsonObject { ["operation"] = operation, ["method"] = "bigint-stat-and-native-realpath" });
    }

    static void RememberProbeName(string name)
    {
        lock (RecentProbeNames)
        {
            if (RecentProbeNames.Add(name)) RecentProbeOrder.Enqueue(name);
            if (RecentProbeNames.Count > RecentProbeNameLimit) RecentProbeNames.Remove(RecentProbeOrder.Dequeue());
        }
    }

    static bool IsRecentProbeName(string name)
    {
        lock (RecentProbeNames) return RecentProbeNames.Contains(name);
    }

    // resolvePlan (:326-499)
    Resolution ResolvePlan(Plan plan, string side, int depth)
    {
        if (depth > MaxSymlinkDepth) return new(Unknown("symlink-cycle", Side(side)));

        string current;
        FsStat currentStat;
        try { current = fs.RealPath(plan.Root); }
        catch (Exception error) when (IsFsError(error))
        {
            // Node on Windows can report EISDIR while resolving a valid extended filesystem root (for example \\?\C:\). The
            // namespace was already validated by SplitWindowsAbsolute, so retain its exact spelling for traversal.
            if (Windows && NativeFs.ErrorCode(error) == "EISDIR" && OrdinaryWindowsRoot(plan.Root) is not null) current = plan.Root;
            else return new(SystemFailure("root-resolution-failed", error, Side(side)));
        }

        try { currentStat = fs.Stat(current); }
        catch (Exception error) when (IsFsError(error))
        {
            // The same edge can occur during stat after realpath succeeds. A drive or share root is short enough for its ordinary
            // spelling, while `current` remains namespaced so descendants keep long-path semantics.
            var ordinaryRoot = Windows && NativeFs.ErrorCode(error) == "EISDIR" ? OrdinaryWindowsRoot(plan.Root) : null;
            if (ordinaryRoot is null || ordinaryRoot == current) return new(SystemFailure("root-resolution-failed", error, Side(side)));
            try { currentStat = fs.Stat(ordinaryRoot); }
            catch (Exception statError) when (IsFsError(statError)) { return new(SystemFailure("root-resolution-failed", statError, Side(side))); }
        }

        var segments = plan.Segments;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (!currentStat.IsDirectory) return new(Unknown("ancestor-not-directory", Side(side, index)));
                try
                {
                    current = fs.RealPath(DirName(current));
                    currentStat = fs.Stat(current);
                }
                catch (Exception error) when (IsFsError(error)) { return new(SystemFailure("path-resolution-failed", error, Side(side, index))); }
                continue;
            }

            var candidate = Join(current, segment);
            FsStat candidateStat;
            try { candidateStat = fs.LStat(candidate); }
            catch (Exception error) when (IsFsError(error))
            {
                var code = NativeFs.ErrorCode(error);
                if (code == "ENOENT")
                {
                    if (!currentStat.IsDirectory) return new(Unknown("ancestor-not-directory", Side(side, index)));
                    var unresolved = segments.Skip(index).Where(item => item != ".").ToList();
                    if (unresolved.Contains(".."))
                        return new(Unknown("future-parent-traversal-indeterminate", Side(side, index)));
                    return new(null, Ancestor: new Entry(current, currentStat), Unresolved: unresolved);
                }
                if (code == "ENOTDIR") return new(SystemFailure("ancestor-not-directory", error, Side(side, index)));
                return new(SystemFailure("path-resolution-failed", error, Side(side, index)));
            }

            if (candidateStat.IsSymbolicLink)
            {
                Plan linkPlan;
                try
                {
                    var authoredTarget = fs.ReadLink(candidate);
                    var internalRelativeLinkTarget = !IsAbsolute(authoredTarget) && !HasDriveRoot(authoredTarget);
                    var parsedTarget = AbsolutePath(authoredTarget, side, DirName(candidate));
                    if (parsedTarget.Failure is not null) return new(parsedTarget.Failure);
                    var target = SplitAbsolute(parsedTarget.Path!, side, internalRelativeLinkTarget);
                    if (target.Failure is not null) return new(target.Failure);
                    linkPlan = new Plan(target.Plan!.Root, [.. target.Plan.Segments, .. segments.Skip(index + 1)]);
                }
                catch (Exception error) when (IsFsError(error)) { return new(SystemFailure("symlink-resolution-failed", error, Side(side, index))); }
                return ResolvePlan(linkPlan, side, depth + 1);
            }

            if (index < segments.Count - 1 && !candidateStat.IsDirectory) return new(Unknown("ancestor-not-directory", Side(side, index)));

            try
            {
                current = fs.RealPath(candidate);
                currentStat = fs.Stat(current);
            }
            catch (Exception error) when (IsFsError(error)) { return new(SystemFailure("path-resolution-failed", error, Side(side, index))); }
        }

        return new(null, Entry: new Entry(current, currentStat), Unresolved: []);
    }

    Resolution ResolveLocation(string? input, string side)
    {
        var parsed = AbsolutePath(input, side);
        if (parsed.Failure is not null) return new(parsed.Failure);
        var plan = SplitAbsolute(parsed.Path!, side);
        if (plan.Failure is not null) return new(plan.Failure);
        return ResolvePlan(plan.Plan!, side, 0);
    }

    // JS normalize leaves lone surrogates alone; .NET throws on them. ponytail: such a string stays unnormalized as a whole.
    static string Normalize(string value, NormalizationForm form)
    {
        try { return value.Normalize(form); }
        catch (ArgumentException) { return value; }
    }

    // toLocale{Lower,Upper}Case('en-US') below are Js.ToLowerCase/ToUpperCase: equal for every code point (Node v24).
    static HashSet<string>? SemanticNameEnvelope(string name)
    {
        var names = new HashSet<string> { name };
        var pending = new Queue<string>([name]);
        while (pending.Count > 0 && names.Count <= 64)
        {
            var current = pending.Dequeue();
            foreach (var candidate in new[]
            {
                Normalize(current, NormalizationForm.FormC), Normalize(current, NormalizationForm.FormD), Js.ToLowerCase(current), Js.ToUpperCase(current),
            })
                if (names.Add(candidate)) pending.Enqueue(candidate);
        }
        return names.Count <= 64 ? names : null;
    }

    static bool SemanticEnvelopesOverlap(string leftName, string rightName)
    {
        var left = SemanticNameEnvelope(leftName);
        var right = SemanticNameEnvelope(rightName);
        return left is null || right is null || left.Overlaps(right);
    }

    // name.split('.')[0].replace(/[ .]+$/u, '').toUpperCase()
    static bool WindowsDeviceName(string name) => WindowsDevice().IsMatch(name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant());

    static PathRelation? FutureWindowsNameRisk(string leftName, string rightName)
    {
        if (!Windows) return null;
        if (WindowsShortName().IsMatch(leftName) || WindowsShortName().IsMatch(rightName))
            return Unknown("future-short-name-indeterminate", Method("windows-8dot3"));
        if (WindowsDeviceName(leftName) || WindowsDeviceName(rightName))
            return Unknown("future-device-name-indeterminate", Method("windows-device-name"));
        if (leftName[^1] is ' ' or '.' || rightName[^1] is ' ' or '.')
            return Unknown("future-trim-name-indeterminate", Method("windows-trim-alias"));
        return null;
    }

    // resolvePhysicalLocation (:563)
    public PhysicalResolution ResolvePhysicalLocation(string targetPath)
    {
        var resolution = ResolveLocation(targetPath, "target");
        if (resolution.Failure is { } failure) return new(failure.Status, failure.Reason);
        if (resolution.Existing)
            return new("resolved", new JsonObject { ["code"] = "existing-native-realpath", ["method"] = "native-realpath" },
                new PhysicalLocation("existing", resolution.Entry!.RealPath, null, []));
        return new("resolved", new JsonObject { ["code"] = "future-physical-ancestor", ["method"] = "native-realpath" },
            new PhysicalLocation("future", null, resolution.Ancestor!.RealPath, [.. resolution.Unresolved!]));
    }

    // probeNameAlias (:593-683): create `<prefix><left>` in the directory itself and stat `<prefix><right>`.
    PathRelation ProbeNameAlias(string directoryPath, string leftName, string rightName)
    {
        if (leftName == rightName) return Result("match", "future-name-exact", Method("lexical-exact"));
        if (IsRecentProbeName(leftName) || IsRecentProbeName(rightName)) return Result("different", "internal-probe-name", Method("probe-suppression"));
        if (FutureWindowsNameRisk(leftName, rightName) is { } windowsRisk) return windowsRisk;
        if (!SemanticEnvelopesOverlap(leftName, rightName)) return Result("different", "future-name-distinct", Method("semantic-envelope"));

        var prefix = $".archify-path-semantics-{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10))}-";
        var probeName = prefix + leftName;
        var lookupName = prefix + rightName;
        int ComponentLength(string name) => Windows ? name.Length : Encoding.UTF8.GetByteCount(name);
        if (ComponentLength(probeName) > 255 || ComponentLength(lookupName) > 255)
            return Unknown("future-name-probe-unrepresentable", new JsonObject { ["method"] = "direct-filesystem-probe", ["systemCode"] = "ENAMETOOLONG" });

        string? probePath = null;
        IDisposable? probe = null;
        PathRelation? comparison = null;
        Exception? cleanupError = null;
        try
        {
            // Probe entries must live directly in the directory being queried. On Windows case sensitivity is per-directory and a
            // child may not inherit it.
            RememberProbeName(probeName);
            RememberProbeName(lookupName);
            probePath = Join(directoryPath, probeName);
            var lookupPath = Join(directoryPath, lookupName);
            probe = fs.CreateExclusive(probePath);

            FsStat? authoredStat = null, lookupStat = null;
            try
            {
                authoredStat = fs.Stat(probePath);
                lookupStat = fs.Stat(lookupPath);
            }
            catch (Exception error) when (IsFsError(error))
            {
                comparison = NativeFs.ErrorCode(error) == "ENOENT"
                    ? Result("different", "future-name-different", Method("filesystem-probe"))
                    : SystemFailure("future-name-probe-failed", error, Method("filesystem-probe"));
            }

            if (comparison is null)
            {
                var identity = CompareKnownEntries(new Entry(fs.RealPath(probePath), authoredStat!), new Entry(fs.RealPath(lookupPath), lookupStat!), "future-name");
                comparison = identity.Status switch
                {
                    "match" => Result("match", "future-name-alias", Method("filesystem-probe")),
                    "different" => Result("different", "future-name-different", Method("filesystem-probe")),
                    _ => identity,
                };
            }
        }
        catch (Exception error) when (IsFsError(error))
        {
            comparison = SystemFailure("future-name-probe-failed", error, Method("filesystem-probe"));
        }
        finally
        {
            // Delete before closing, as upstream unlinks before closeSync; the open handle (shared for delete) pins the entry.
            if (probe is not null)
            {
                try { fs.Delete(probePath!); }
                catch (Exception error) when (IsFsError(error)) { cleanupError = error; }
                try { probe.Dispose(); }
                catch (Exception error) when (IsFsError(error)) { cleanupError ??= error; }
            }
        }

        if (cleanupError is not null)
        {
            var details = Method("filesystem-probe");
            if (comparison is not null) details["comparison"] = comparison.Reason.DeepClone();
            return SystemFailure("future-name-probe-cleanup-failed", cleanupError, details);
        }
        return comparison!;
    }

    // The candidate is accepted only after the containing filesystem confirms that the original grapheme and this spelling name
    // the same entry.
    static string SidecarCaseKey(string component) => Js.ToLowerCase(Js.ToUpperCase(component));

    // portableSidecarComponentKey (visual-check.mjs:164-170): the collision envelope of a target directory that cannot be probed yet,
    // with V8's full uppercasing (ß → SS), since the key decides a sidecar name rather than a probe.
    internal static string PortableSidecarComponentKey(string component) =>
        Normalize(Js.ToLowerCase(Js.ToUpperCase(Normalize(component, NormalizationForm.FormC))), NormalizationForm.FormC);

    (string? Component, PathRelation? Failure) TransformSidecarComponent(string directoryPath, string component, Func<string, string> transform, string phase)
    {
        var comparisons = new Dictionary<(string, string), PathRelation>();
        var output = new StringBuilder();
        foreach (var segment in Js.Graphemes(component))
        {
            var candidate = transform(segment);
            if (candidate == segment)
            {
                output.Append(segment);
                continue;
            }
            if (!comparisons.TryGetValue((segment, candidate), out var comparison))
                comparisons[(segment, candidate)] = comparison = ProbeNameAlias(directoryPath, segment, candidate);
            if (comparison.Status == "unknown")
                return (null, Unknown($"sidecar-{phase}-semantics-indeterminate",
                    new JsonObject { ["operation"] = "sidecar-namespace-key", ["cause"] = comparison.Reason.DeepClone() }));
            output.Append(comparison.Status == "match" ? candidate : segment);
        }
        return (output.ToString(), null);
    }

    // basename(component) !== component
    static bool IsBareComponent(string component) => Windows
        ? component.IndexOfAny(['\\', '/']) < 0 && !HasDriveRoot(component)
        : !component.Contains('/');

    // sidecarNamespaceComponentKey (:737-790). The returned string is a collision-domain key, not a canonical path, and must never
    // be used to decide whether two filesystem entries match.
    public SidecarNamespaceKey SidecarNamespaceComponentKey(string directoryPath, string component)
    {
        static SidecarNamespaceKey Failed(PathRelation failure) => new(failure.Status, failure.Reason);
        static SidecarNamespaceKey Fail(string code) => Failed(Unknown(code, new JsonObject { ["operation"] = "sidecar-namespace-key" }));

        if (string.IsNullOrEmpty(component) || component is "." or ".." || !IsBareComponent(component)) return Fail("invalid-sidecar-component");

        var directory = ResolveLocation(directoryPath, "sidecar-directory");
        if (directory.Failure is not null) return Failed(directory.Failure);
        if (!directory.Existing) return Fail("sidecar-directory-missing");
        if (!directory.Entry!.Stat.IsDirectory) return Fail("sidecar-parent-not-directory");

        var realPath = directory.Entry.RealPath;
        var normalized = TransformSidecarComponent(realPath, component, segment => Normalize(segment, NormalizationForm.FormC), "normalization");
        if (normalized.Failure is not null) return Failed(normalized.Failure);
        var cased = TransformSidecarComponent(realPath, normalized.Component!, SidecarCaseKey, "case");
        if (cased.Failure is not null) return Failed(cased.Failure);
        var renormalized = TransformSidecarComponent(realPath, cased.Component!, segment => Normalize(segment, NormalizationForm.FormC), "normalization");
        if (renormalized.Failure is not null) return Failed(renormalized.Failure);

        return new("resolved", new JsonObject { ["code"] = "sidecar-namespace-key", ["method"] = "directory-filesystem-probe" }, realPath, renormalized.Component);
    }

    static PathRelation CompareUnmaterializedNames(string leftName, string rightName)
    {
        if (leftName == rightName) return Result("match", "future-name-exact", Method("lexical-exact"));
        if (FutureWindowsNameRisk(leftName, rightName) is { } windowsRisk) return windowsRisk;
        if (!SemanticEnvelopesOverlap(leftName, rightName)) return Result("different", "future-name-distinct", Method("semantic-envelope"));
        return Unknown("future-descendant-semantics-indeterminate", Method("unmaterialized-directory"));
    }

    // `result(names.status, code, {...reasonDetails, operation, segmentIndex})`
    static PathRelation AtSegment(PathRelation names, string operation, int index)
    {
        var details = Spread([], names.Reason);
        details.Remove("code");
        details["operation"] = operation;
        details["segmentIndex"] = index;
        return Result(names.Status, names.Reason["code"]!.GetValue<string>(), details);
    }

    PathRelation CompareFutureLocations(Resolution left, Resolution right)
    {
        var ancestor = CompareKnownEntries(left.Ancestor!, right.Ancestor!, "future-ancestor");
        if (ancestor.Status != "match") return ancestor;

        if (left.Unresolved!.Count != right.Unresolved!.Count)
            return Result("different", "future-depth-different", new JsonObject
            {
                ["operation"] = "same-location", ["leftDepth"] = left.Unresolved.Count, ["rightDepth"] = right.Unresolved.Count,
            });

        var probedSegments = 0;
        for (var index = 0; index < left.Unresolved.Count; index++)
        {
            var names = index == 0
                ? ProbeNameAlias(left.Ancestor!.RealPath, left.Unresolved[index], right.Unresolved[index])
                : CompareUnmaterializedNames(left.Unresolved[index], right.Unresolved[index]);
            if (names.Reason["method"]?.GetValue<string>() == "filesystem-probe") probedSegments++;
            if (names.Status != "match") return AtSegment(names, "same-location", index);
        }

        return Result("match", probedSegments > 0 ? "future-location-alias" : "future-location-exact", new JsonObject
        {
            ["operation"] = "same-location", ["method"] = probedSegments > 0 ? "filesystem-probe" : "lexical-exact", ["probedSegments"] = probedSegments,
        });
    }

    // sameEntry (:849): two existing entries by physical identity; missing or unidentifiable entries are unknown, never different.
    public PathRelation SameEntry(string leftPath, string rightPath)
    {
        var left = ResolveLocation(leftPath, "left");
        if (left.Failure is not null) return left.Failure;
        var right = ResolveLocation(rightPath, "right");
        if (right.Failure is not null) return right.Failure;

        if (!left.Existing || !right.Existing)
        {
            var missing = new JsonArray();
            if (!left.Existing) missing.Add("left");
            if (!right.Existing) missing.Add("right");
            return Unknown("entry-missing", new JsonObject { ["operation"] = "same-entry", ["missing"] = missing });
        }
        return CompareKnownEntries(left.Entry!, right.Entry!, "same-entry");
    }

    // sameLocation (:872): existing or future write targets under the containing filesystem's own name semantics.
    public PathRelation SameLocation(string leftPath, string rightPath)
    {
        var left = ResolveLocation(leftPath, "left");
        if (left.Failure is not null) return left.Failure;
        var right = ResolveLocation(rightPath, "right");
        if (right.Failure is not null) return right.Failure;

        if (left.Existing && right.Existing) return CompareKnownEntries(left.Entry!, right.Entry!, "same-location");
        if (left.Existing != right.Existing)
            return Result("different", "location-existence-different",
                new JsonObject { ["operation"] = "same-location", ["existing"] = left.Existing ? "left" : "right" });
        return CompareFutureLocations(left, right);
    }

    // sameParent (:891)
    public PathRelation SameParent(string leftPath, string rightPath)
    {
        var left = AbsolutePath(leftPath, "left");
        if (left.Failure is not null) return left.Failure;
        var right = AbsolutePath(rightPath, "right");
        if (right.Failure is not null) return right.Failure;
        return SameLocation(DirName(left.Path!), DirName(right.Path!));
    }

    // parentEntry (:900): null at a root.
    (Entry? Entry, PathRelation? Failure)? ParentEntry(Entry entry)
    {
        var parentPath = DirName(entry.RealPath);
        if (parentPath == entry.RealPath) return null;
        try
        {
            var realPath = fs.RealPath(parentPath);
            return (new Entry(realPath, fs.Stat(realPath)), null);
        }
        catch (Exception error) when (IsFsError(error))
        {
            return (null, SystemFailure("ancestor-resolution-failed", error, new JsonObject { ["operation"] = "contained-by" }));
        }
    }

    // containedBy (:927): inclusive physical containment in an existing directory, following links; the target may be future.
    public PathRelation ContainedBy(string directoryPath, string targetPath)
    {
        var directory = ResolveLocation(directoryPath, "directory");
        if (directory.Failure is not null) return directory.Failure;
        if (directory.Existing && !directory.Entry!.Stat.IsDirectory) return Unknown("container-not-directory", new JsonObject { ["operation"] = "contained-by" });

        var target = ResolveLocation(targetPath, "target");
        if (target.Failure is not null) return target.Failure;

        if (!directory.Existing)
        {
            if (target.Existing) return Unknown("container-missing", new JsonObject { ["operation"] = "contained-by" });
            var ancestor = CompareKnownEntries(directory.Ancestor!, target.Ancestor!, "contained-by");
            if (ancestor.Status != "match") return ancestor;
            if (target.Unresolved!.Count < directory.Unresolved!.Count)
                return Result("different", "future-container-prefix-too-deep", new JsonObject { ["operation"] = "contained-by" });
            for (var index = 0; index < directory.Unresolved.Count; index++)
            {
                var segment = index == 0
                    ? ProbeNameAlias(directory.Ancestor!.RealPath, directory.Unresolved[index], target.Unresolved[index])
                    : CompareUnmaterializedNames(directory.Unresolved[index], target.Unresolved[index]);
                if (segment.Status == "match") continue;
                return AtSegment(segment, "contained-by", index);
            }
            return Result("match", "future-container-prefix-match", new JsonObject
            {
                ["operation"] = "contained-by", ["target"] = "future", ["depth"] = target.Unresolved.Count - directory.Unresolved.Count,
            });
        }

        var targetKind = target.Existing ? "existing" : "future";
        Entry? current;
        if (!target.Existing) current = target.Ancestor;
        else if (target.Entry!.Stat.IsDirectory) current = target.Entry;
        else
        {
            var parent = ParentEntry(target.Entry);
            if (parent?.Entry is null)
                return parent?.Failure ?? Unknown("ancestor-resolution-failed", new JsonObject { ["operation"] = "contained-by" });
            current = parent.Value.Entry;
        }

        var depth = 0;
        var identityWasUnknown = false;
        while (current is not null)
        {
            var comparison = CompareKnownEntries(directory.Entry!, current, "contained-by");
            if (comparison.Status == "match")
                return Result("match", "physical-ancestor-match", new JsonObject { ["operation"] = "contained-by", ["depth"] = depth, ["target"] = targetKind });
            if (comparison.Status == "unknown") identityWasUnknown = true;

            var parent = ParentEntry(current);
            if (parent is null) break;
            if (parent.Value.Failure is not null) return parent.Value.Failure;
            current = parent.Value.Entry;
            depth++;
        }

        if (identityWasUnknown)
            return Unknown("containment-identity-unavailable", new JsonObject { ["operation"] = "contained-by", ["target"] = targetKind });
        return Result("different", "physical-ancestor-not-found", new JsonObject { ["operation"] = "contained-by", ["target"] = targetKind });
    }
}
