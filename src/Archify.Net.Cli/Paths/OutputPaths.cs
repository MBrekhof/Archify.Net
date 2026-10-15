using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net.Cli.Paths;

// resolveOutputPath's arguments (output-path.mjs:373-383). AuthoredOutput is meta.output when HasAuthoredOutput (`!== undefined`:
// a JSON null still counts). Cwd is the command's working directory, never the process's.
internal sealed record OutputRequest(string Cwd, string? RequestedOutput, string? DefaultOutput)
{
    public JsonNode? AuthoredOutput { get; init; }
    public bool HasAuthoredOutput { get; init; }
    public IReadOnlyList<string> InputPaths { get; init; } = [];
    public string InputDescription { get; init; } = "an input";
    public IReadOnlyList<string> OtherOutputPaths { get; init; } = [];
    public string RequiredExtension { get; init; } = ".html";
    public string Platform { get; init; } = OutputPaths.HostPlatform;

    // meta.output of a parsed diagram, as `diagram?.meta?.output` reads it.
    public OutputRequest WithAuthored(JsonObject diagram) => diagram["meta"] is JsonObject meta && meta.TryGetPropertyValue("output", out var output)
        ? this with { AuthoredOutput = output, HasAuthoredOutput = true }
        : this;
}

// UP/renderers/shared/output-path.mjs, the native half: the lexical validateAuthoredOutputPath is Core's OutputPath. Failures
// throw ArchifyDiagnosticException (upstream's OutputPathError: message + one archifyDiagnostics entry). Paths handed to
// path-semantics are absolute (resolved against the request's cwd), since it would otherwise use the process cwd.
internal sealed partial class OutputPaths(PathSemantics semantics)
{
    public static OutputPaths Native { get; } = new(PathSemantics.Native);
    public static string HostPlatform { get; } = OperatingSystem.IsWindows() ? "win32" : "linux";

    static ArchifyDiagnosticException Failure(string message, string code, string diagnosticMessage, JsonObject subject, JsonObject? evidence,
        params string[] supportedFixes) =>
        new(message, [new Diagnostic(code, Severity.Error, diagnosticMessage, subject, evidence ?? new JsonObject(), supportedFixes, null)]);

    static JsonObject Relation(JsonObject reason) => new() { ["relation"] = reason.DeepClone() };

    public string CanonicalFuturePath(string targetPath)
    {
        var resolution = semantics.ResolvePhysicalLocation(targetPath);
        if (resolution.Status == "resolved")
        {
            var location = resolution.Location!;
            return location.Kind == "existing" ? location.Path! : NodePath.Join(location.AncestorPath!, location.Unresolved);
        }
        var output = NodePath.Resolve(targetPath);
        if (resolution.Reason["code"]?.GetValue<string>() == "symlink-cycle") throw SymlinkCycle(output, resolution.Reason);
        throw Failure("Output path could not be resolved safely.", "output/path-resolution-indeterminate",
            "Output path could not be resolved safely for the requested filesystem location.",
            new JsonObject { ["output"] = output }, Relation(resolution.Reason),
            "use an ordinary local filesystem path that can be resolved safely, then retry");
    }

    static ArchifyDiagnosticException SymlinkCycle(string output, JsonObject reason) =>
        Failure($"Output path contains a symbolic-link cycle: \"{output}\".", "output/symlink-cycle",
            "Output path could not be resolved because it contains a symbolic-link cycle.",
            new JsonObject { ["output"] = output }, Relation(reason), "remove the symbolic-link cycle or choose an output path outside it");

    public bool PathsAlias(string leftPath, string rightPath)
    {
        var relation = semantics.SameLocation(leftPath, rightPath);
        if (relation.Status == "match") return true;
        if (relation.Status == "different") return false;
        var code = relation.Reason["code"]?.GetValue<string>();
        if (code == "ancestor-not-directory") return false;
        if (code == "symlink-cycle")
            throw SymlinkCycle(NodePath.Resolve(relation.Reason["side"]?.GetValue<string>() == "right" ? rightPath : leftPath), relation.Reason);
        throw Failure("Path identity could not be determined safely.", "output/path-identity-indeterminate",
            "Path identity could not be determined safely for the requested filesystem location.",
            new JsonObject { ["left"] = NodePath.Resolve(leftPath), ["right"] = NodePath.Resolve(rightPath) }, Relation(relation.Reason),
            "use ordinary local filesystem paths whose identity can be verified, then retry");
    }

    bool PathIsInside(string directoryPath, string targetPath)
    {
        var relation = semantics.ContainedBy(directoryPath, targetPath);
        if (relation.Status == "match") return true;
        if (relation.Status == "different") return false;
        throw Failure("Output containment could not be determined safely.", "output/containment-indeterminate",
            "Output containment could not be determined safely for the requested filesystem location.",
            new JsonObject { ["output"] = NodePath.Resolve(targetPath), ["cwd"] = NodePath.Resolve(directoryPath) }, Relation(relation.Reason),
            "use an output beneath an ordinary local directory whose identity can be verified");
    }

    // validateAuthoredOutputPath (:323-357): Core's lexical checks, then the two that need the filesystem.
    public void ValidateAuthoredOutputPath(JsonNode? rawOutput, string cwd)
    {
        OutputPath.ValidateAuthoredOutputPath(rawOutput, new DiagnosticSink());
        var raw = rawOutput!.GetValue<string>();   // portable-path rejected every non-string
        var outputPath = NodePath.Resolve(cwd, raw);
        if (Js.ToLowerCase(NodePath.Extname(CanonicalFuturePath(outputPath))) != ".html")
            throw Failure("meta.output must resolve to an .html file.", "output/meta-resolved-extension",
                "meta.output must resolve to an .html file after symbolic links are followed.", new JsonObject { ["output"] = raw }, null,
                "remove the symbolic-link alias or point it to an .html target inside the current working directory");
        if (!PathIsInside(NodePath.Resolve(cwd, "."), outputPath))
            throw Failure("meta.output must stay inside the current working directory.", "output/meta-outside-cwd",
                "meta.output must stay inside the current working directory after symbolic links are resolved.",
                new JsonObject { ["output"] = raw, ["cwd"] = NodePath.Resolve(cwd, ".") },
                null, "set meta.output to a relative .html path inside the current working directory");
    }

    // resolveOutputPath (:373-440): precedence cli > meta > default; meta.output resolves against cwd, not the input's directory.
    public (string OutputPath, string Source) ResolveOutputPath(OutputRequest request)
    {
        if (request.HasAuthoredOutput) ValidateAuthoredOutputPath(request.AuthoredOutput, request.Cwd);
        var source = request.RequestedOutput is not null ? "cli" : request.HasAuthoredOutput ? "meta" : "default";
        var rawOutput = source switch
        {
            "cli" => request.RequestedOutput!,
            "meta" => request.AuthoredOutput!.GetValue<string>(),
            _ => request.DefaultOutput!,
        };
        if (source != "meta") ValidateNativeOutputPath(rawOutput, request.Platform);
        var outputPath = NodePath.Resolve(request.Cwd, rawOutput);
        foreach (var inputPath in request.InputPaths.Select(p => NodePath.Resolve(request.Cwd, p)))
        {
            if (!PathsAlias(outputPath, inputPath)) continue;
            var message = $"Output must not replace {request.InputDescription}, including through a symbolic-link or future-path alias.";
            throw Failure($"Output must not replace {request.InputDescription}.", "output/input-alias", message,
                new JsonObject { ["output"] = outputPath, ["input"] = inputPath }, null,
                "choose an output path that is distinct from every input path");
        }
        foreach (var otherOutputPath in request.OtherOutputPaths.Select(p => NodePath.Resolve(request.Cwd, p)))
        {
            if (!PathsAlias(outputPath, otherOutputPath)) continue;
            throw Failure("Output targets must use distinct paths.", "output/target-alias",
                "Output targets must use distinct paths, including symbolic-link and future-path aliases.",
                new JsonObject { ["output"] = outputPath, ["conflictingOutput"] = otherOutputPath }, null,
                "choose distinct paths for every generated output");
        }

        // Explicit CLI directories stay unrestricted, but a mistaken file type is rejected (after the alias checks).
        if (source == "cli")
        {
            var required = request.RequiredExtension;
            var resolvedOutput = CanonicalFuturePath(outputPath);
            var authoredExtension = Js.ToLowerCase(NodePath.Extname(rawOutput));
            var resolvedMatches = Js.ToLowerCase(NodePath.Extname(resolvedOutput)) == required;
            var existingWindowsHtmlAlias = request.Platform == "win32" && required == ".html" && authoredExtension == ".htm"
                && resolvedMatches && PathsAlias(outputPath, resolvedOutput);
            var authoredMatches = authoredExtension == required || existingWindowsHtmlAlias;
            if (!authoredMatches || !resolvedMatches)
            {
                var message = $"CLI output must {(authoredMatches ? "resolve to" : "target")} a {required} file.";
                throw Failure(message, authoredMatches ? "output/cli-resolved-extension" : "output/cli-extension", message,
                    new JsonObject { ["output"] = rawOutput }, new JsonObject { ["resolvedOutput"] = resolvedOutput, ["requiredExtension"] = required },
                    $"choose a path ending in {required} whose symbolic-link target also ends in {required}");
            }
        }
        return (outputPath, source);
    }

    // resolveNativeOutputDirectory (:314-321)
    public static string ResolveNativeOutputDirectory(string rawDirectory, string cwd, string? platform = null)
    {
        ValidateNativeOutputPath(rawDirectory, platform ?? HostPlatform, "directory");
        return NodePath.Resolve(cwd, rawDirectory);
    }

    // --- validateNativeOutputPath (:98-311): pure, the platform is a parameter ---

    static ArchifyDiagnosticException NativeFailure(string rawOutput, string reason, JsonObject? details = null)
    {
        var evidence = new JsonObject { ["reason"] = reason };
        foreach (var (key, value) in details ?? []) evidence[key] = value?.DeepClone();
        const string message = "The output path is not a valid native filesystem path on this host.";
        return Failure(message, "output/native-path-syntax", message, new JsonObject { ["output"] = rawOutput }, evidence,
            "choose an ordinary filesystem path without device names, alternate data streams, trailing dots or spaces, or overlong components");
    }

    [GeneratedRegex(@"^\\\\\?\\(?:GLOBALROOT|Device)(?:\\|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtendedDevice();
    [GeneratedRegex(@"^\\\\\?\\[A-Za-z]:\\")] private static partial Regex ExtendedDrive();
    [GeneratedRegex(@"^\\\\\?\\UNC\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ExtendedUnc();
    [GeneratedRegex(@"^([\\/])\1([^\\/]+)\1([^\\/]+)(?:[\\/]|\z)")] private static partial Regex RawUnc();
    [GeneratedRegex(@"^\\\\([^\\]+)\\([^\\]+)(?:\\|\z)")] private static partial Regex NormalizedUnc();
    [GeneratedRegex(@"^[A-Za-z]:(?:\z|[^\\/])")] private static partial Regex DriveRelative();

    static bool IsSep(char c) => c is '\\' or '/';

    static List<string> ExtendedTailComponents(string rawOutput, string tail)
    {
        var authored = tail.Split('\\');
        // One trailing separator is kept for directory arguments; an empty component inside the raw namespace is never repaired.
        if (authored[..^1].Any(component => component.Length == 0)) throw NativeFailure(rawOutput, "windows-extended-root");
        return [.. authored.Where(component => component.Length > 0)];
    }

    static void ValidateWindowsNativeComponent(string rawOutput, string component, int componentIndex, bool allowReservedName = false)
    {
        try
        {
            // The prefix makes a colon read as an ADS separator before the URI detector can claim it.
            PortablePath.Validate($"native/{component}", "output");
        }
        catch (PortablePathException error)
        {
            if (allowReservedName && error.Reason == "windows-reserved-name") return;
            // A native argument may name an existing 8.3 alias; the filesystem proves it during resolution.
            if (error.Reason == "windows-short-name")
            {
                if (component.Length <= 255) return;
                throw NativeFailure(rawOutput, "component-too-long", new JsonObject
                {
                    ["component"] = component, ["componentIndex"] = componentIndex, ["utf16CodeUnits"] = component.Length, ["limit"] = 255,
                });
            }
            // Native Windows filesystems bound components in UTF-16 code units; the UTF-8 bound is portable-only.
            if (error.Reason == "component-too-long" && error.Utf16CodeUnits <= 255) return;
            var details = new JsonObject { ["component"] = component, ["componentIndex"] = componentIndex };
            if (error.Character is not null) details["character"] = error.Character;
            if (error.Utf8Bytes is int utf8Bytes) details["utf8Bytes"] = utf8Bytes;
            if (error.Utf16CodeUnits is int utf16CodeUnits) details["utf16CodeUnits"] = utf16CodeUnits;
            if (error.Limit is int limit) details["limit"] = limit;
            throw NativeFailure(rawOutput, error.Reason, details);
        }
    }

    static void ValidateWindowsUncRootComponents(string rawOutput, string server, string share)
    {
        if (PathSemantics.IsWindowsIpcShare(share)) throw NativeFailure(rawOutput, "windows-ipc-namespace", new JsonObject { ["share"] = share });
        if (!PathSemantics.IsValidWindowsSmbShareName(share))
            throw NativeFailure(rawOutput, "windows-unc-share-name", new JsonObject { ["share"] = share, ["utf16CodeUnits"] = share.Length, ["limit"] = 80 });
        // UNC servers are root components, not DOS file names: CON is allowed, ordinary syntax is not.
        ValidateWindowsNativeComponent(rawOutput, server, 0, allowReservedName: true);
    }

    static List<string> WindowsExtendedPathComponents(string rawOutput)
    {
        // The extended namespace bypasses Win32 normalization, so its raw spelling is inspected: a dot segment cannot retarget a share.
        if (rawOutput.Contains('/')) throw NativeFailure(rawOutput, "windows-extended-separator", new JsonObject { ["character"] = "/" });
        if (ExtendedDevice().IsMatch(rawOutput)) throw NativeFailure(rawOutput, "windows-device-namespace");
        if (ExtendedDrive().Match(rawOutput) is { Success: true } drive) return ExtendedTailComponents(rawOutput, rawOutput[drive.Length..]);
        if (ExtendedUnc().Match(rawOutput) is { Success: true } unc)
        {
            var components = rawOutput[unc.Length..].Split('\\');
            if (components.Length < 2 || components[0].Length == 0 || components[1].Length == 0) throw NativeFailure(rawOutput, "windows-extended-root");
            ValidateWindowsUncRootComponents(rawOutput, components[0], components[1]);
            return ExtendedTailComponents(rawOutput, string.Join('\\', components[2..]));
        }
        throw NativeFailure(rawOutput, "windows-extended-root");
    }

    static void ValidateWindowsRawUncRoot(string rawOutput)
    {
        // Only `[\\/]{2}` roots that are not `\\.\` or `\\?\` (in either separator).
        if (!(rawOutput.Length >= 2 && IsSep(rawOutput[0]) && IsSep(rawOutput[1]))
            || (rawOutput.Length >= 4 && (rawOutput[2] is '.' or '?') && IsSep(rawOutput[3]))) return;
        // The raw server/share boundary, before normalize can collapse an empty share or mix the two separators.
        var unc = RawUnc().Match(rawOutput);
        if (!unc.Success) throw NativeFailure(rawOutput, "windows-unc-root");
        ValidateWindowsUncRootComponents(rawOutput, unc.Groups[2].Value, unc.Groups[3].Value);
    }

    static List<string> WindowsPathComponents(string rawOutput, string normalized)
    {
        var authoredUncPrefix = rawOutput.Length >= 2 && IsSep(rawOutput[0]) && IsSep(rawOutput[1]);
        if (normalized.StartsWith(@"\\.\", StringComparison.Ordinal)) throw NativeFailure(rawOutput, "windows-device-namespace");
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) throw NativeFailure(rawOutput, "windows-extended-root");
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var unc = NormalizedUnc().Match(normalized);
            if (!unc.Success) throw NativeFailure(rawOutput, "windows-unc-root");
            return [.. normalized[unc.Length..].Split('\\').Where(c => c.Length > 0)];
        }
        if (authoredUncPrefix) throw NativeFailure(rawOutput, "windows-unc-root");
        if (normalized.StartsWith('\\')) throw NativeFailure(rawOutput, "current-drive-rooted");
        // path.win32.parse(normalized).root: `X:\` or nothing (a drive-relative `X:` was rejected before).
        var root = normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && IsSep(normalized[2]) ? 3 : 0;
        // normalize keeps a relative path's leading navigation; those dot segments are syntax, not file names.
        return [.. normalized[root..].Split(['\\', '/']).Where(c => c.Length > 0 && c is not ("." or ".."))];
    }

    // Command-line and default outputs in the host's native syntax. A file output rejects a trailing separator before resolution
    // could erase it; directories opt in with kind "directory". The component bound also keeps derived sidecars creatable.
    public static string ValidateNativeOutputPath(string rawOutput, string? platform = null, string kind = "file")
    {
        platform ??= HostPlatform;
        if (kind is not ("file" or "directory")) throw new ArgumentException($"Unsupported native output path kind: {Js.Stringify(JsonValue.Create(kind))}", nameof(kind));
        if (string.IsNullOrEmpty(rawOutput)) throw NativeFailure(rawOutput, "empty");
        if (rawOutput.Contains('\0')) throw NativeFailure(rawOutput, "nul-character");
        if (Js.CodePoints(rawOutput).Any(cp => cp.Length == 1 && char.IsSurrogate(cp[0]))) throw NativeFailure(rawOutput, "unpaired-surrogate");
        var hasTrailingSeparator = platform == "win32" ? IsSep(rawOutput[^1]) : rawOutput[^1] == '/';
        if (kind == "file" && hasTrailingSeparator) throw NativeFailure(rawOutput, "trailing-separator", new JsonObject { ["kind"] = kind });

        if (platform == "win32")
        {
            if (DriveRelative().IsMatch(rawOutput)) throw NativeFailure(rawOutput, "drive-relative");
            List<string> components;
            if (rawOutput.StartsWith(@"\\?\", StringComparison.Ordinal)) components = WindowsExtendedPathComponents(rawOutput);
            else
            {
                ValidateWindowsRawUncRoot(rawOutput);
                components = WindowsPathComponents(rawOutput, NodePath.Normalize(rawOutput));
            }
            for (var index = 0; index < components.Count; index++) ValidateWindowsNativeComponent(rawOutput, components[index], index);
        }
        else
        {
            // Upstream resolves with the host's path module here, whatever `platform` says.
            var normalized = Path.GetFullPath(rawOutput);
            var components = normalized[(Path.GetPathRoot(normalized) ?? "").Length..].Split(Path.DirectorySeparatorChar).Where(c => c.Length > 0).ToList();
            for (var index = 0; index < components.Count; index++)
            {
                var utf8Bytes = Encoding.UTF8.GetByteCount(components[index]);
                if (utf8Bytes > 255)
                    throw NativeFailure(rawOutput, "component-too-long", new JsonObject
                    {
                        ["component"] = components[index], ["componentIndex"] = index, ["utf8Bytes"] = utf8Bytes, ["limit"] = 255,
                    });
            }
        }
        return rawOutput;
    }
}
