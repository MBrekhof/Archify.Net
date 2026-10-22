using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

/// <summary>
/// Port of renderers/shared/repository-evidence.mjs: verifies <c>meta.repository</c> and node <c>sources</c> against a local clone
/// and returns the <c>sourceEvidence</c> payload the template embeds.
/// </summary>
/// <remarks>
/// Root identity (path-semantics.mjs <c>sameEntry</c>) is asked of git: after <c>rev-parse --show-toplevel</c>, an empty
/// <c>rev-parse --show-prefix</c> means the root is the top level. Stated deviation: <c>repository-evidence/root-identity-indeterminate</c>
/// is unreachable here; upstream reaches it only through a mocked <c>fs.statSync</c>.
/// </remarks>
internal static class RepositoryEvidence
{
    const RegexOptions Es = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    const int MaxSourceBytes = 16 * 1024 * 1024;
    const int MaxBatchBytes = 64 * 1024 * 1024;
    static readonly Regex FullSha = new(@"^[a-fA-F0-9]{40}\z", Es);
    static readonly Regex FilesystemPath = new(@"^(?:[\\/]|~|\.{1,2}(?:[\\/]|\z)|[A-Za-z]:[\\/])", Es);
    static readonly Regex GitlabWebPath = new(@"^[A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)+\z", Es);
    static readonly Regex WebPath = new(@"^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\z", Es);
    static readonly Regex TreeBlob = new(@"^\d+ blob [a-f0-9]+\z", Es);
    static readonly Regex LabelPrefix = new(@"^(?:https?:\/\/|ssh:\/\/git@|git@)", Es);

    sealed record Blob(bool Missing, string? Type, double Size, string? Content);

    static Exception EvidenceFailure(DiagnosticSink sink, string code, string message, JsonObject? subject = null, JsonObject? evidence = null,
        IReadOnlyList<string>? supportedFixes = null)
    {
        var full = new JsonObject { ["surface"] = "repository-evidence" };
        foreach (var (key, value) in subject ?? []) full[key] = value?.DeepClone();
        return sink.ThrowDiagnosticError(message, [new(code, Severity.Error, message, full, evidence ?? new JsonObject(), supportedFixes ?? [], null)]);
    }

    static GitRunner.Result RunGit(GitRunner git, DiagnosticSink sink, string root, params string[] args)
    {
        var result = git.Run(root, args, MaxSourceBytes);
        if (result.Error is { } error)
            throw EvidenceFailure(sink, "repository-evidence/git-unavailable", $"Could not run Git: {error}",
                evidence: new JsonObject { ["reason"] = error }, supportedFixes: ["install Git and ensure it is available on PATH"]);
        return result;
    }

    // Check types in one session, then read only blobs whose cited lines need
    // verification. Path-only references never require loading the file contents.
    static Dictionary<string, Blob>? PrefetchBlobs(GitRunner git, string root, JsMap<string, bool> objectNeedsContent)
    {
        var blobs = ReadBatchObjects(git, root, [.. objectNeedsContent.Keys], false);
        if (blobs is null) return null;
        var readable = objectNeedsContent.Entries
            .Where(e => e.Value && blobs.TryGetValue(e.Key, out var blob) && blob.Type == "blob" && blob.Size <= MaxSourceBytes)
            .Select(e => e.Key).ToList();
        var contents = ReadBatchObjects(git, root, readable, true);
        if (contents is not null) foreach (var (key, blob) in contents) blobs[key] = blob;
        // Failed or oversized reads fall back in source order to the original
        // per-file path, preserving its size limit and diagnostic behavior.
        return blobs;
    }

    static Dictionary<string, Blob>? ReadBatchObjects(GitRunner git, string root, List<string> objects, bool includeContent)
    {
        if (objects.Count == 0) return new(StringComparer.Ordinal);
        var result = git.Run(root, ["cat-file", includeContent ? "--batch" : "--batch-check"], MaxBatchBytes, string.Join('\n', objects) + "\n");
        if (result.Error is not null || result.Status != 0) return null;
        var buffer = result.Stdout;
        var utf8 = new UTF8Encoding(false);
        var blobs = new Dictionary<string, Blob>(StringComparer.Ordinal);   // lookup only, never enumerated
        var cursor = 0;
        foreach (var obj in objects)
        {
            var newline = Array.IndexOf(buffer, (byte)0x0a, cursor);
            if (newline < 0) return null;
            var header = utf8.GetString(buffer, cursor, newline - cursor);
            cursor = newline + 1;
            if (header.EndsWith(" missing", StringComparison.Ordinal)) { blobs[obj] = new(true, null, 0, null); continue; }
            var parts = header.Split(' ');
            var size = parts.Length > 2 ? Js.ToNumber(parts[2]) : double.NaN;
            if (parts.Length != 3 || !IsSafeInteger(size) || size < 0) return null;
            string? content = null;
            if (includeContent)
            {
                if (cursor + size >= buffer.Length || buffer[cursor + (int)size] != 0x0a) return null;
                content = utf8.GetString(buffer, cursor, (int)size);
                cursor += (int)size + 1;
            }
            blobs[obj] = new(false, parts[1], size, content);
        }
        return blobs;
    }

    static bool IsSafeInteger(double d) => double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) <= 9007199254740991;

    static string GitValue(GitRunner git, DiagnosticSink sink, string root, string[] args, string failure)
    {
        var result = RunGit(git, sink, root, args);
        if (result.Status != 0)
            throw EvidenceFailure(sink, "repository-evidence/git-command", failure,
                evidence: new JsonObject { ["gitArgs"] = new JsonArray([.. args.Select(a => (JsonNode)a)]), ["exitCode"] = result.Status },
                supportedFixes: ["use the intended local Git repository and verify its origin and revision"]);
        // Removes Git's record terminator without changing whitespace in a path (`replace(/\n$/, '')`: one \n, \r kept).
        var text = result.Text;
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    static string VerifiedSourcePath(DiagnosticSink sink, JsonNode? value, string where)
    {
        var sourcePath = value.Truthy() ? Js.PrimitiveString(value) : "";   // String(value || '')
        // path-contract-allow: git-path -- Git tree entries use repository-relative POSIX syntax.
        if (sourcePath.Length == 0 || sourcePath.StartsWith('/') || sourcePath.Contains('\\') || sourcePath.Any(c => c is < ' ' or '\u007f'))
            throw EvidenceFailure(sink, "repository-evidence/path-invalid", $"{where} must be a repo-relative POSIX path.",
                new JsonObject { ["path"] = where }, new JsonObject { ["authoredPath"] = sourcePath },
                ["use a repository-relative path with forward slashes"]);
        var segments = sourcePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or "..") || segments[0] == ".git")
            throw EvidenceFailure(sink, "repository-evidence/path-escape", $"{where} must stay inside the repository and may not address .git.",
                new JsonObject { ["path"] = where }, new JsonObject { ["authoredPath"] = sourcePath },
                ["remove empty, dot, parent, or .git path segments"]);
        return string.Join('/', segments);
    }

    // split(/\r\n|\n|\r/).length, minus one when the content ends with a break.
    static int SourceLineCount(string content)
    {
        if (content.Length == 0) return 0;
        var lines = 1;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
            if (content[i] is '\r' or '\n') lines++;
        }
        return lines - (content[^1] is '\r' or '\n' ? 1 : 0);
    }

    // EVIDENCE_NODE_COLLECTIONS (:129-140) is cli.mjs's semantic collection table for all ten types.
    static bool HasRepositoryEvidence(DiagramType type, JsonObject diagram) =>
        diagram.Obj("meta")?["repository"].Truthy() == true
        || diagram[type.SemanticCollection()].AsArrayOrEmpty().Any(node => node.Arr("sources") is { Count: > 0 });

    public static JsonObject? Verify(DiagramType type, JsonObject diagram, string? repoRoot, DiagnosticSink sink, GitRunner? git = null)
    {
        if (!HasRepositoryEvidence(type, diagram)) return null;
        git ??= new GitRunner();
        var collection = type.SemanticCollection();
        var authoredNodes = diagram[collection].AsArrayOrEmpty().ToList();

        var repository = diagram.Obj("meta")?.Obj("repository");
        if (repository is null)
            throw EvidenceFailure(sink, "repository-evidence/repository-required", "Repository evidence requires /meta/repository.",
                new JsonObject { ["path"] = "/meta/repository", ["diagramType"] = type.Name(), ["collection"] = collection },
                supportedFixes: [$"add the pinned repository metadata or remove /{collection} sources"]);
        if (!FullSha.IsMatch(repository["revision"].Truthy() ? Js.PrimitiveString(repository["revision"]) : ""))
            throw EvidenceFailure(sink, "repository-evidence/revision-invalid", "/meta/repository/revision must be a full 40-character commit SHA.",
                new JsonObject { ["path"] = "/meta/repository/revision" }, new JsonObject { ["revision"] = repository["revision"]?.DeepClone() },
                ["pin one full 40-character commit SHA"]);
        var url = repository.Str("url");
        var provider = repository.Str("provider");
        if (RepositoryLocation.Parse(url, authored: true, provider) is not { } location)
        {
            // A filesystem path is the common authoring mistake: the field carries the
            // remote origin identity, which `git remote get-url origin` reports.
            var filesystemPath = FilesystemPath.IsMatch(repository["url"] is null ? "" : Js.PrimitiveString(repository["url"]));
            throw EvidenceFailure(sink, "repository-evidence/url-invalid",
                "/meta/repository/url must be a credential-free HTTP(S) or Git SSH repository address without query, fragment, or dot segments.",
                new JsonObject { ["path"] = "/meta/repository/url" },
                filesystemPath ? new JsonObject { ["authoredValueLooksLike"] = "local filesystem path; the expected value is the remote origin address" } : new JsonObject(),
                ["run `git remote get-url origin` inside --repo-root and declare that credential-free address", "use link_mode: local-only for internal repositories"]);
        }
        var linkMode = repository["link_mode"] is null ? "web" : repository.Str("link_mode");   // ?? 'web'
        if (linkMode is not ("web" or "local-only"))
            throw EvidenceFailure(sink, "repository-evidence/link-mode-invalid", "Repository link_mode must be web or local-only.");
        if (repository.ContainsKey("provider") && (provider is not ("github" or "gitee" or "gitlab") || provider != location.Provider))
            throw EvidenceFailure(sink, "repository-evidence/provider-invalid",
                "Repository provider must match its supported public host (github.com, gitee.com or gitlab.com); gitlab also names a self-managed GitLab host.",
                new JsonObject { ["path"] = "/meta/repository/provider" },
                supportedFixes: ["use the matching provider or omit provider and select link_mode: local-only"]);
        // GitLab projects may sit in nested groups; GitHub and Gitee are owner/repository.
        var webPath = location.Provider == "gitlab" ? GitlabWebPath : WebPath;
        if (linkMode == "web" && (location.Provider is null || location.Protocol != "https:" || location.Endpoint != "standard" || !webPath.IsMatch(location.Path)))
            throw EvidenceFailure(sink, "repository-evidence/links-unsupported", "Web source links require a canonical GitHub, Gitee, or GitLab HTTPS repository URL.",
                new JsonObject { ["path"] = "/meta/repository/url" },
                supportedFixes: ["use a canonical GitHub, Gitee, or GitLab HTTPS URL; declare provider: gitlab for a self-managed GitLab host",
                    "select link_mode: local-only to retain local verification without web links"]);
        if (string.IsNullOrEmpty(repoRoot))
            throw EvidenceFailure(sink, "repository-evidence/root-required",
                "This diagram declares source evidence. Pass --repo-root <repository> so Archify can verify it before rendering.",
                new JsonObject { ["path"] = "/meta/repository" }, supportedFixes: ["pass --repo-root with the matching local Git checkout"]);

        var requestedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));   // path.resolve
        // fs.realpathSync for diagnostic text: git resolves the physical root itself. ponytail: only a link on the final directory is
        // resolved; resolve every component if a diagnostic ever has to name a root reached through an intermediate link.
        string realRoot;
        if (Directory.Exists(requestedRoot) || File.Exists(requestedRoot))
            realRoot = new DirectoryInfo(requestedRoot).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? requestedRoot;
        else
        {
            // Node's message names the lstat that failed; exact when the missing component is the last one.
            var reason = $"ENOENT: no such file or directory, lstat '{requestedRoot}'";
            throw EvidenceFailure(sink, "repository-evidence/root-unreadable", $"Could not resolve evidence repository root \"{requestedRoot}\": {reason}",
                new JsonObject { ["repoRoot"] = requestedRoot }, new JsonObject { ["reason"] = reason }, ["pass one readable local repository directory"]);
        }
        var notRepository = $"Evidence root \"{realRoot}\" is not a Git repository.";
        var gitRoot = GitValue(git, sink, realRoot, ["rev-parse", "--show-toplevel"], notRepository);
        // Port-only call (sameEntry's stand-in): git prints "\n" at the top level, empty once the terminator is gone.
        if (GitValue(git, sink, realRoot, ["rev-parse", "--show-prefix"], notRepository).Length > 0)
            throw EvidenceFailure(sink, "repository-evidence/root-not-top-level", $"Evidence root must be the Git top-level directory: {gitRoot}",
                new JsonObject { ["repoRoot"] = realRoot }, new JsonObject { ["gitTopLevel"] = gitRoot }, [$"pass --repo-root {gitRoot}"]);
        var origin = Js.Trim(GitValue(git, sink, realRoot, ["remote", "get-url", "origin"], "Evidence repository must have an origin remote."));
        if (RepositoryLocation.Parse(origin, authored: false, provider)?.Identity != location.Identity)
        {
            var safeOrigin = RepositoryLocation.Redact(origin);
            throw EvidenceFailure(sink, "repository-evidence/origin-mismatch",
                $"Evidence repository origin {Js.Quote(safeOrigin)} does not match {Js.Quote(url!)}.",
                new JsonObject { ["repoRoot"] = realRoot }, new JsonObject { ["localOrigin"] = safeOrigin, ["authoredRepository"] = url },
                ["use the matching local checkout or correct the authored repository URL"]);
        }

        var revision = repository.Str("revision")!.ToLowerInvariant();   // FULL_SHA_RE passed: ASCII hex
        if (RunGit(git, sink, realRoot, "cat-file", "-e", $"{revision}^{{commit}}").Status != 0)
            throw EvidenceFailure(sink, "repository-evidence/revision-unavailable", $"Evidence revision {revision} is not available in the local repository.",
                new JsonObject { ["repoRoot"] = realRoot }, new JsonObject { ["revision"] = revision },
                ["fetch the pinned commit or pin an available full commit SHA"]);

        // The batch is an optimization only: every path, line-range, file and line
        // check still runs in source order in the verification loop below, so a
        // citation the batch cannot answer for never reorders the diagnostics.
        var citedObjects = new JsMap<string, bool>();
        for (var nodeIndex = 0; nodeIndex < authoredNodes.Count; nodeIndex++)
        {
            if (authoredNodes[nodeIndex].Arr("sources") is not { Count: > 0 } sources) continue;
            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                var at = $"/{collection}/{Js.Num(nodeIndex)}/sources/{Js.Num(sourceIndex)}";
                string sourcePath;
                try { sourcePath = sink.WithRecordingSuppressed(() => VerifiedSourcePath(sink, sources[sourceIndex]?["path"], $"{at}/path")); }
                catch (ArchifyDiagnosticException) { continue; }
                var obj = $"{revision}:{sourcePath}";
                citedObjects.Set(obj, citedObjects.Get(obj) || sources[sourceIndex]?["line"].Truthy() == true);
            }
        }
        var prefetchedBlobs = PrefetchBlobs(git, realRoot, citedObjects);

        var nodes = new JsonObject();
        var sourceDiagnostics = new List<Diagnostic>();
        var referenceCount = 0;
        for (var nodeIndex = 0; nodeIndex < authoredNodes.Count; nodeIndex++)
        {
            var node = authoredNodes[nodeIndex];
            if (node.Arr("sources") is not { Count: > 0 } sources) continue;
            // `componentId` shipped with the architecture-only path; keep it beside the
            // type-neutral `nodeId` so existing agent handling stays valid.
            var nodeSubject = new JsonObject { ["diagramType"] = type.Name(), ["collection"] = collection, ["nodeId"] = node!["id"]?.DeepClone() };
            if (collection == "components") nodeSubject["componentId"] = node["id"]?.DeepClone();
            JsonObject Subject(string path)
            {
                var subject = new JsonObject { ["path"] = path };
                foreach (var (key, value) in nodeSubject) subject[key] = value?.DeepClone();
                return subject;
            }
            var verified = new JsonArray();
            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                var authored = sources[sourceIndex];
                var at = $"/{collection}/{Js.Num(nodeIndex)}/sources/{Js.Num(sourceIndex)}";
                var where = $"{at}/path";
                try
                {
                    var source = new JsonObject { ["path"] = VerifiedSourcePath(sink, authored?["path"], where) };
                    if (authored?["line"].Truthy() == true) source["line"] = authored["line"]!.DeepClone();
                    if (authored?["end_line"].Truthy() == true) source["endLine"] = authored["end_line"]!.DeepClone();
                    if (authored?["label"].Truthy() == true) source["label"] = authored["label"]!.DeepClone();
                    var sourcePath = source.Str("path")!;
                    var line = source["line"] is { } l ? l.D() : (double?)null;
                    var endLine = source["endLine"] is { } e ? e.D() : (double?)null;
                    if (endLine is not null && line is null)
                        throw EvidenceFailure(sink, "repository-evidence/line-required", $"{at}/end_line requires line.", Subject($"{at}/end_line"),
                            supportedFixes: ["add line or remove end_line"]);
                    if (endLine < line)
                        throw EvidenceFailure(sink, "repository-evidence/line-range-invalid", $"{at}/end_line must be greater than or equal to line.", Subject(at),
                            new JsonObject { ["line"] = source["line"]!.DeepClone(), ["endLine"] = source["endLine"]!.DeepClone() },
                            ["use an end_line greater than or equal to line"]);
                    var obj = $"{revision}:{sourcePath}";
                    var prefetched = prefetchedBlobs?.GetValueOrDefault(obj);
                    var objectIsBlob = prefetched is not null
                        ? !prefetched.Missing && prefetched.Type == "blob"
                        : RunGit(git, sink, realRoot, "cat-file", "-t", obj) is var objectType && objectType.Status == 0 && Js.Trim(objectType.Text) == "blob";
                    if (!objectIsBlob)
                    {
                        // A promisor blob can exist in the pinned tree without being stored
                        // locally. Do not tell the caller to change a correct source path.
                        var tree = RunGit(git, sink, realRoot, "ls-tree", "-z", revision, "--", sourcePath);
                        var knownBlob = tree.Status == 0 && tree.Text.Split('\0').Any(entry =>
                        {
                            var separator = entry.IndexOf('\t');
                            return entry[(separator + 1)..] == sourcePath && TreeBlob.IsMatch(separator < 0 ? entry[..^1] : entry[..separator]);
                        });
                        if (knownBlob)
                            throw EvidenceFailure(sink, "repository-evidence/object-unavailable",
                                $"{where} exists at revision {revision}, but its content is unavailable in the local repository.", Subject(where),
                                new JsonObject { ["sourcePath"] = sourcePath, ["revision"] = revision },
                                ["explicitly fetch the pinned source objects into the local checkout, then retry verification"]);
                        throw EvidenceFailure(sink, "repository-evidence/file-missing", $"{where} does not identify a file at revision {revision}.", Subject(where),
                            new JsonObject { ["sourcePath"] = sourcePath, ["revision"] = revision }, ["use a file path that exists at the pinned revision"]);
                    }
                    if (line is not null)
                    {
                        var content = prefetched?.Content;
                        if (content is null)
                        {
                            var shown = RunGit(git, sink, realRoot, "show", obj);
                            if (shown.Status != 0)
                                throw EvidenceFailure(sink, "repository-evidence/file-unreadable", $"{where} could not be read at revision {revision}.", Subject(where),
                                    new JsonObject { ["sourcePath"] = sourcePath, ["revision"] = revision }, ["verify the pinned blob is readable in the local checkout"]);
                            content = shown.Text;
                        }
                        var lineCount = SourceLineCount(content);
                        var requestedLine = endLine ?? line.Value;   // source.endLine || source.line
                        if (requestedLine > lineCount)
                            throw EvidenceFailure(sink, "repository-evidence/line-out-of-range",
                                $"{at} requests line {Js.Num(requestedLine)}, but {sourcePath} has {Js.Num(lineCount)} lines at revision {revision}.", Subject(at),
                                new JsonObject { ["sourcePath"] = sourcePath, ["requestedLine"] = (source["endLine"] ?? source["line"])!.DeepClone(),
                                    ["lineCount"] = lineCount, ["revision"] = revision },
                                ["use a line range that exists at the pinned revision"]);
                    }
                    if (linkMode == "web")
                        source["href"] = RepositoryLocation.SourceHref(location.Provider, location.Url, revision, sourcePath, (int?)line, (int?)endLine);
                    verified.Add(source);
                    referenceCount += 1;
                }
                catch (ArchifyDiagnosticException error) when (error.Diagnostics.Count > 0)
                {
                    // Independent citations can be repaired together. Keep the first
                    // diagnostic and source order; an invalid reference never verifies.
                    sourceDiagnostics.AddRange(error.Diagnostics);
                    // An unavailable Git process or an oversized read is not a bad
                    // citation. Stop rather than repeat that failure for every source.
                    if (error.Diagnostics.Any(d => d.Code == "repository-evidence/git-unavailable"))
                        throw sink.ThrowDiagnosticError(sourceDiagnostics[0].Message, sourceDiagnostics);
                }
            }
            nodes[node.Str("id") ?? "undefined"] = verified;
        }
        if (sourceDiagnostics.Count > 0) throw sink.ThrowDiagnosticError(sourceDiagnostics[0].Message, sourceDiagnostics);
        if (referenceCount == 0)
            throw EvidenceFailure(sink, "repository-evidence/source-required", $"/meta/repository requires at least one /{collection} source reference.",
                new JsonObject { ["path"] = "/meta/repository", ["diagramType"] = type.Name(), ["collection"] = collection },
                supportedFixes: [$"add at least one verified /{collection} source or remove repository metadata"]);

        var repositoryPayload = new JsonObject
        {
            ["url"] = location.Url, ["revision"] = revision, ["shortRevision"] = revision[..7],
            ["label"] = location.Provider == "github" ? location.Path : LabelPrefix.Replace(location.Url, "", 1),
        };
        if (linkMode == "web") repositoryPayload["href"] = RepositoryLocation.TreeHref(location.Provider, location.Url, revision);
        else repositoryPayload["linkMode"] = linkMode;
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["verified"] = true, ["repository"] = repositoryPayload, ["referenceCount"] = referenceCount, ["nodes"] = nodes,
        };
    }
}
