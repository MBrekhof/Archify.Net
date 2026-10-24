using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// The resource options prepare.mjs passes to resolveExplainerSources and prepareExplainerDiagrams (`common`).
internal sealed record ResourceOptions(string? RepoRoot, string? BaseDir, JsonObject? SourceMap, string? SourcePath, string Quality = "showcase");

// archify 3.2.0 explainer/sources.mjs: containsExplainerSecret, and resolveExplainerSources, which binds pinned Git lines and
// freezes the bytes of code excerpts, local images and chart data files.
internal static class ExplainerSources
{
    const int MaxCodeBytes = 16 * 1024 * 1024, MaxImageBytes = 8 * 1024 * 1024, MaxDataBytes = 2 * 1024 * 1024, MaxAssetBytes = 32 * 1024 * 1024;
    const string Any = "[^\\n\\r\\u2028\\u2029]";   // JS `.`
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // SECRET_NAME (:14), `/i` without `u`: ASCII folding only.
    static readonly Regex SecretName = new(
        $@"\A(?:{Ci(".env")}(?:\.{Any}*)?|{Ci(".npmrc")}|{Ci(".netrc")}|{Ci(".pypirc")}|\.(?:{Ci("ssh")}|{Ci("aws")}|{Ci("gnupg")}|{Ci("kube")})"
        + $@"|(?:{Ci("id_")}(?:{Ci("rsa")}|{Ci("dsa")}|{Ci("ecdsa")}|{Ci("ed25519")}))(?:\.{Any}*)?|(?:{Ci("credential")}[Ss]?|{Ci("secret")}[Ss]?)(?:[._-]{Any}*)?"
        + $@"|{Any}*\.(?:{Ci("pem")}|{Ci("key")}|{Ci("p12")}|{Ci("pfx")}|{Ci("keystore")}))\z");
    // /\.(?:csv|json)$/iu: under `iu` U+017F LONG S folds to s.
    static readonly Regex DataPath = new(@"\.(?:[Cc][Ssſ][Vv]|[Jj][Ssſ][Oo][Nn])\z");
    static readonly Regex LineBreak = new("\r\n|\n|\r");

    static readonly Dictionary<string, string> Examples = new(StringComparer.Ordinal)
    {
        ["source"] = "{\"id\":\"router\",\"title\":\"Router\",\"path\":\"src/router.js\",\"lines\":[1,3]}",
        ["code"] = "{\"id\":\"code\",\"type\":\"code\",\"source\":\"router\"}",
        ["image"] = "{\"id\":\"image\",\"type\":\"image\",\"path\":\"images/figure.png\",\"alt\":\"Figure\"}",
    };

    static readonly Dictionary<string, string> RepositoryExamples = new(StringComparer.Ordinal)
    {
        ["repository-evidence/root-required"] = "--repo-root /absolute/path/to/repository",
        ["repository-evidence/root-not-top-level"] = "--repo-root /absolute/path/to/repository",
        ["repository-evidence/root-unreadable"] = "--repo-root /absolute/path/to/repository",
        ["repository-evidence/revision-invalid"] = "git rev-parse HEAD",
        ["repository-evidence/revision-unavailable"] = "git rev-parse HEAD",
        ["repository-evidence/url-invalid"] = "{\"url\":\"https://github.com/owner/repository\"}",
        ["repository-evidence/origin-mismatch"] = "git remote get-url origin",
        ["repository-evidence/line-out-of-range"] = "{\"path\":\"src/router.js\",\"lines\":[1,3]}",
        ["repository-evidence/file-missing"] = "{\"path\":\"src/router.js\",\"lines\":[1,3]}",
    };

    // location (:36-41): {path, component, input?, line, column?}.
    static JsonObject Location(ResourceOptions o, string pointer, string component)
    {
        var entry = o.SourceMap?[pointer] as JsonObject;
        var subject = new JsonObject { ["path"] = pointer, ["component"] = component };
        if (!string.IsNullOrEmpty(o.SourcePath)) subject["input"] = o.SourcePath;
        subject["line"] = IsInteger(entry?["line"]) && entry!["line"]!.D() > 0 ? entry["line"]!.DeepClone() : 1;
        if (IsInteger(entry?["column"])) subject["column"] = entry!["column"]!.DeepClone();
        return subject;
    }
    static bool IsInteger(JsonNode? value) => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && double.IsFinite(v.D()) && v.D() == Math.Floor(v.D());

    // fail (:43-52)
    internal static Exception Fail(ResourceOptions o, string pointer, string component, string reason, string message, string fix, string? example = null) =>
        new ArchifyDiagnosticException(message, [Diagnostic.Normalize(new($"explainer/{reason}", Severity.Error, message, Location(o, pointer, component),
            new JsonObject { ["reason"] = reason, ["example"] = !string.IsNullOrEmpty(example) ? example : Examples.GetValueOrDefault(component) ?? Examples["source"] },
            [fix], null))]);

    // safePath (:54-66)
    static string SafePath(ResourceOptions o, JsonNode? value, string pointer, string component)
    {
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrEmpty(text) || text[0] is '/' or '\\' || text.Any(c => c is '\\' or ':' or '?' or '#' or '\u007f' || c <= '\u001f')
            || text.Split('/').Any(part => part is "" or "." or ".." || Js.ToLowerCase(part) == ".git"))
            throw Fail(o, pointer, component, "source-path", "Resource paths must stay inside their root directory.",
                "Use a relative path with forward slashes and no dot segments.", "src/router.js");
        if (text.Split('/').Any(SecretName.IsMatch))
            throw Fail(o, pointer, component, "secret-path", "This resource belongs to a sensitive file class.", "Reference a public source file or remove this resource.");
        return text;
    }

    // gitBytes (:68-75): GitRunner is spawnSync('git', ['--no-replace-objects', '-C', repoRoot, ...args], {maxBuffer}).
    static byte[] GitBytes(ResourceOptions o, string repoRoot, string[] args, string pointer)
    {
        var result = new GitRunner().Run(repoRoot, args, MaxCodeBytes);
        if (result.Error is not null || result.Status != 0 || result.Stdout.Length > MaxCodeBytes)
            throw Fail(o, pointer, "source", "source-read", "The pinned repository resource could not be read within the size limit.",
                "Use an available, readable source file smaller than 16 MiB.");
        return result.Stdout;
    }
    static string Utf8(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);   // toString('utf8')

    // deriveRepository (:77-87)
    static JsonObject DeriveRepository(ResourceOptions o)
    {
        if (string.IsNullOrEmpty(o.RepoRoot))
            throw Fail(o, "/meta/repository", "source", "repository-root", "Repository sources require a local repository.",
                "Pass --repo-root with the matching local Git checkout.", "--repo-root /absolute/path/to/repository");
        var raw = Js.Trim(Utf8(GitBytes(o, o.RepoRoot, ["remote", "get-url", "origin"], "/meta/repository")));
        var remote = RepositoryLocation.Parse(raw, authored: false, provider: null)
            ?? throw Fail(o, "/meta/repository", "source", "repository-origin", "The repository origin cannot be represented safely.",
                "Configure a credential-free repository origin without query or fragment.");
        var revision = Js.Trim(Utf8(GitBytes(o, o.RepoRoot, ["rev-parse", "--verify", "HEAD^{commit}"], "/meta/repository")));
        var result = new JsonObject { ["url"] = remote.Url, ["revision"] = revision };
        if (!(remote.Provider is not null && remote.Protocol == "https:" && remote.Endpoint == "standard")) result["link_mode"] = "local-only";
        return result;
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    // digest (:97-100): {sha256, bytes, contentBase64}
    internal static JsonObject Digest(byte[] bytes) => new()
    {
        ["sha256"] = Sha256(bytes), ["bytes"] = bytes.Length, ["contentBase64"] = Convert.ToBase64String(bytes),
    };

    // imageMime (:102-109)
    static string? ImageMime(byte[] b)
    {
        static string Ascii(byte[] b, int from, int to) => Encoding.Latin1.GetString(b, from, to - from);
        if (b.Length >= 24 && b.AsSpan(0, 8).SequenceEqual((byte[])[137, 80, 78, 71, 13, 10, 26, 10]) && Ascii(b, 12, 16) == "IHDR") return "image/png";
        if (b.Length >= 4 && b[0] == 255 && b[1] == 216 && b[2] == 255) return "image/jpeg";
        if (b.Length >= 13 && Ascii(b, 0, 6) is "GIF87a" or "GIF89a") return "image/gif";
        if (b.Length >= 16 && Ascii(b, 0, 4) == "RIFF" && Ascii(b, 8, 12) == "WEBP") return "image/webp";
        return null;
    }

    // path.extname: the last dot of the base name, unless the name starts with it.
    static string ExtName(string relative)
    {
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..];
    }

    // readImage (:115-127)
    static JsonObject ReadImage(ResourceOptions o, JsonNode? authoredPath, string pointer)
    {
        if (string.IsNullOrEmpty(o.BaseDir))
            throw Fail(o, pointer, "image", "asset-root", "Local images require an input directory.", "Provide the original input directory as baseDir.");
        var relative = SafePath(o, authoredPath, $"{pointer}/path", "image");
        var expected = Js.ToLowerCase(ExtName(relative)) switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => null,
        } ?? throw Fail(o, $"{pointer}/path", "image", "asset-format", "Only local PNG, JPEG, WebP and GIF images are supported.", "Use a PNG, JPEG, WebP or GIF image.");
        var bytes = ReadContained(o, o.BaseDir, relative, pointer, "image", MaxImageBytes, "8 MiB");
        var mime = ImageMime(bytes);
        if (mime != expected)
            throw Fail(o, pointer, "image", "asset-format", "The image bytes do not match a supported image format.", "Use a correctly encoded image whose extension matches its bytes.");
        var asset = new JsonObject { ["path"] = relative, ["mime"] = mime };
        foreach (var (key, value) in Digest(bytes)) asset[key] = value!.DeepClone();
        asset["dataUrl"] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        return asset;
    }

    // A path component and what it was when first seen: size and last-write time (sameFile, :111-113).
    // ponytail: Node also compares dev+ino, so a same-size, same-time swap of the file during the read is caught there and not
    // here; asset-changed and diagram-source-changed are race-only codes. A GetFileInformationByHandle file id is the upgrade.
    internal readonly record struct Stamp(long Size, DateTime Written);
    internal static Stamp StampOf(FileSystemInfo info) => new(info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc);

    // What Node's lstat reports as a link: libuv reads a symbolic link or a junction (mount point) as one, and every other
    // reparse point (a cloud-file placeholder, dedup, WCI) as the regular file or directory it stands for. LinkTarget is non-null
    // for exactly those two tags. ponytail: libuv also counts an AppExecLink (the WindowsApps aliases), which LinkTarget does not
    // read; no draft resource lives there.
    internal static bool IsLink(FileAttributes attributes, Func<string?> linkTarget) => attributes.HasFlag(FileAttributes.ReparsePoint) && linkTarget() is not null;
    static bool IsLink(FileSystemInfo info) => IsLink(info.Attributes, () => info.LinkTarget);

    // The shared walk of readContained (:130-168) and diagrams.mjs readSpec: the real root, then each component, refusing a
    // symbolic link or junction (IsLink). Returns the components and their stamps; `link` builds the refusal.
    internal static List<(FileSystemInfo Info, Stamp Stamp)> Walk(string baseDir, string relative, Func<Exception> link)
    {
        var full = Path.GetFullPath(baseDir);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);   // fs.realpathSync throws
        var root = new DirectoryInfo(full);
        root = root.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? root;
        var ancestors = new List<(FileSystemInfo, Stamp)> { (root, StampOf(root)) };
        var candidate = root.FullName;
        foreach (var part in relative.Split('/'))
        {
            candidate = Path.Join(candidate, part);
            var attributes = File.GetAttributes(candidate);   // throws when the component is missing
            FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (IsLink(attributes, () => info.LinkTarget)) throw link();
            ancestors.Add((info, StampOf(info)));
        }
        return ancestors;
    }

    // Reads the walked file. Opened: the handle's stamp differs from the walk's, or exceeds `limit` (Bytes null). Read: the file
    // or a component changed while it was read (Bytes null).
    internal static (bool Opened, byte[]? Bytes) ReadUnchanged(List<(FileSystemInfo Info, Stamp Stamp)> ancestors, long limit)
    {
        var (file, stamp) = ancestors[^1];
        using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var before = new Stamp(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle));
        if (before != stamp || before.Size > limit) return (false, null);
        var bytes = new byte[before.Size];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) break;
            offset += count;
        }
        var unchanged = offset == bytes.Length && new Stamp(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle)) == before
            && ancestors.All(entry =>
            {
                entry.Info.Refresh();
                return !IsLink(entry.Info) && StampOf(entry.Info) == entry.Stamp;
            });
        return (true, unchanged ? bytes : null);
    }

    // readContained (:130-168): a regular file inside baseDir, not through links, unchanged while it was read.
    static byte[] ReadContained(ResourceOptions o, string baseDir, string relative, string pointer, string component, int limit, string limitText)
    {
        var noun = component == "image" ? "image" : "data file";
        var Noun = char.ToUpperInvariant(noun[0]) + noun[1..];
        try
        {
            var ancestors = Walk(baseDir, relative, () => Fail(o, $"{pointer}/path", component, "asset-symlink", $"{Noun} paths may not traverse symbolic links.",
                $"Copy the {noun} into the input directory as a regular file."));
            if (ancestors[^1].Info is not FileInfo) throw Fail(o, pointer, component, "asset-file", $"The {noun} must be a regular file.", $"Use a regular {noun}.");
            var (opened, bytes) = ReadUnchanged(ancestors, limit);
            if (!opened) throw Fail(o, pointer, component, "asset-size", $"The {noun} changed or exceeds the {limitText} size limit.", $"Use a stable {noun} smaller than {limitText}.");
            return bytes ?? throw Fail(o, pointer, component, "asset-changed", $"The {noun} path or bytes changed while being frozen.", $"Finish {noun} updates before rendering again.");
        }
        catch (Exception error) when (error is not ArchifyDiagnosticException)
        {
            throw Fail(o, pointer, component, "asset-read", $"The {noun} cannot be read safely inside the input directory.", $"Use an accessible regular {noun} inside the input directory.");
        }
    }

    static IEnumerable<(JsonObject Block, string Pointer)> Blocks(JsonObject document) =>
        document["panels"].AsArrayOrEmpty().SelectMany((panel, panelIndex) =>
            panel.Arr("blocks").AsArrayOrEmpty().Select((block, index) => (block as JsonObject ?? [], $"/panels/{panelIndex}/blocks/{index}")));

    static JsonObject Verify(JsonObject metadata, IEnumerable<(JsonObject Entry, int Index)> entries, string? repoRoot) =>
        RepositoryEvidence.Verify(DiagramType.Architecture, new JsonObject
        {
            ["meta"] = new JsonObject { ["repository"] = metadata.DeepClone() },
            ["components"] = new JsonArray([.. entries.Select(e => (JsonNode?)new JsonObject
            {
                ["id"] = e.Entry["id"]?.DeepClone(),
                ["sources"] = new JsonArray(new JsonObject { ["path"] = e.Entry["path"]?.DeepClone(), ["line"] = e.Entry["lines"]?[0]?.DeepClone(), ["end_line"] = e.Entry["lines"]?[1]?.DeepClone() }),
            })]),
        }, repoRoot, new DiagnosticSink())!;

    // The excerpt (:256): lines[0]..lines[1] of the file, split on any line break and joined with \n.
    static string Excerpt(string text, JsonObject entry)
    {
        var lines = LineBreak.Split(text);
        var (start, end) = ((int)entry["lines"]![0]!.D() - 1, (int)entry["lines"]![1]!.D());
        return string.Join("\n", lines[Math.Min(start, lines.Length)..Math.Min(end, lines.Length)]);
    }
    // TextDecoder('utf-8', {fatal: true}): throws on invalid bytes and drops one leading BOM.
    static string DecodeStrict(byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        return text.StartsWith('﻿') ? text[1..] : text;
    }

    // The source record of a verified repository entry (:245-247, :278-279), without its excerpt.
    static JsonObject RepositorySource(JsonObject entry, string revision, JsonObject reference)
    {
        var result = new JsonObject { ["id"] = entry["id"]?.DeepClone() };
        if (entry.ContainsKey("title")) result["title"] = entry["title"]?.DeepClone();
        result["revision"] = revision;
        result["path"] = reference["path"]?.DeepClone();
        result["lines"] = entry["lines"]!.DeepClone();
        if (reference["href"].Truthy()) result["url"] = reference["href"]!.DeepClone();
        return result;
    }
    static JsonObject RepositoryDependency(JsonObject entry, string revision, byte[] bytes)
    {
        var dependency = new JsonObject { ["kind"] = "repository", ["id"] = entry["id"]?.DeepClone(), ["path"] = entry["path"]?.DeepClone(), ["revision"] = revision };
        foreach (var (key, value) in Digest(bytes)) dependency[key] = value!.DeepClone();
        return dependency;
    }

    /// <summary>resolveExplainerSources (sources.mjs:171-318): source locations prepared, code, image and data bytes frozen;
    /// author input is never changed. Returns {sources, assets, charts, dependencies, repository}.</summary>
    public static JsonObject Resolve(JsonObject document, ResourceOptions o)
    {
        var sources = new JsonObject();
        var assets = new JsonObject();
        var dependencies = new JsonArray();
        // ponytail: upstream reads `document.sources.map` at :266 and throws a TypeError when the key is absent; the schema
        // requires it, so a draft never gets here without it. Here a missing list is empty.
        var entries = document["sources"].AsArrayOrEmpty().Select(entry => entry as JsonObject ?? []).ToList();
        var blocks = Blocks(document).ToList();
        var codeIds = new HashSet<string>(StringComparer.Ordinal);
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (block, pointer) in blocks)
        {
            var type = block.Str("type");
            if (type is not ("code" or "image")) continue;
            var id = block.Str("id");
            if (string.IsNullOrEmpty(id) || !blockIds.Add(id))
                throw Fail(o, pointer, type, "resource-id", "Resource blocks require a unique stable id.", "Give each code or image block its own id.");
            if (type == "code") codeIds.Add(Key(block["source"]));
        }
        var repositorySources = new List<(JsonObject Entry, int Index)>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var pointer = $"/sources/{index}";
            var id = entry.Str("id");
            if (string.IsNullOrEmpty(id) || sources.ContainsKey(id))
                throw Fail(o, pointer, "source", "source-id", "Sources require unique ids.", "Give each source a unique id.");
            sources[id] = null;
            if (entry["path"].Truthy())
            {
                SafePath(o, entry["path"], $"{pointer}/path", "source");
                if (entry["lines"].Truthy() && !(entry["lines"] is JsonArray { Count: 2 } lines && lines.All(IsSafeInteger)
                        && lines[0]!.D() >= 1 && lines[1]!.D() >= lines[0]!.D()))
                    throw Fail(o, $"{pointer}/lines", "source", "source-lines", "Repository line ranges must be ordered and positive.", "Use lines: [start, end].", "[1, 3]");
            }
            if (entry["url"].Truthy() && !(entry["url"] is JsonValue url && url.TryGetValue<string>(out var href) && ExplainerInline.CredentialFreeHttpUrl(href)))
                throw Fail(o, $"{pointer}/url", "source", "source-url", "Source links require credential-free HTTP(S) URLs.", "Use https://example.com/article.");
            var record = new JsonObject { ["id"] = id };
            if (entry.ContainsKey("title")) record["title"] = entry["title"]?.DeepClone();
            foreach (var key in (string[])["url", "path", "lines", "revision"])
                if (entry[key].Truthy()) record[key] = entry[key]!.DeepClone();
            sources[id] = record;
            if (codeIds.Contains(id))
            {
                if (!entry["path"].Truthy() || !entry["lines"].Truthy())
                    throw Fail(o, pointer, "code", "code-source", "Code requires a Git file and line range.", "Add path and lines to the source.");
                repositorySources.Add((entry, index));
            }
        }

        JsonObject? repository = null;
        if (repositorySources.Count > 0)
        {
            var metadata = document["meta"]?["repository"].Truthy() == true ? document["meta"]!["repository"]!.DeepClone().AsObject() : DeriveRepository(o);
            foreach (var (entry, index) in repositorySources)
                if (entry.ContainsKey("revision") && Js.ToLowerCase(Js.String(entry["revision"])) != Js.ToLowerCase(Js.String(metadata["revision"] ?? "undefined")))
                    throw Fail(o, $"/sources/{index}/revision", "source", "source-revision", "All repository sources must use the document’s pinned revision.",
                        "Use the document repository revision or omit the source revision.");
            JsonObject verified;
            try { verified = Verify(metadata, repositorySources, o.RepoRoot); }
            catch (ArchifyDiagnosticException error)
            {
                var original = error.Diagnostics.Count > 0 ? error.Diagnostics[0] : null;
                var match = original?.Subject.Str("path") is { } path ? Regex.Match(path, @"\A/components/([0-9]+)") : Match.Empty;
                int? index = match.Success && int.TryParse(match.Groups[1].Value, out var at) && at < repositorySources.Count ? repositorySources[at].Index : null;
                throw Fail(o, index is null ? "/meta/repository" : $"/sources/{index}", "source", "repository-location", "The declared repository location could not be verified.",
                    "Correct the repository origin, pinned commit, file path and line range.", original is null ? null : RepositoryExamples.GetValueOrDefault(original.Code));
            }
            repository = verified["repository"]!.DeepClone().AsObject();
            var revision = repository.Str("revision")!;
            foreach (var (entry, index) in repositorySources)
            {
                var result = RepositorySource(entry, revision, verified["nodes"]![entry.Str("id")!]![0]!.AsObject());
                var bytes = GitBytes(o, o.RepoRoot!, ["show", $"{revision}:{entry.Str("path")}"], $"/sources/{index}");
                string text;
                try { text = DecodeStrict(bytes); }
                catch (DecoderFallbackException)
                {
                    throw Fail(o, $"/sources/{index}", "code", "source-encoding", "Code sources must contain UTF-8 text.", "Use a UTF-8 text source file.");
                }
                if (text.Contains('\0') || ContainsSecret(text, entry.Str("path")))
                    throw Fail(o, $"/sources/{index}", "code", "secret-content", "This source cannot be displayed because it contains sensitive or binary content.",
                        "Use a public source file without secrets.");
                result["text"] = Excerpt(text, entry);
                result["sha256"] = Sha256(bytes);
                result["bytes"] = bytes.Length;
                dependencies.Add(RepositoryDependency(entry, revision, bytes));
                sources[entry.Str("id")!] = result;
            }
        }
        // A cited repository file also carries its excerpt when the repository is at hand, so a reader can check the claim in
        // place. A citation that cannot be verified or read stays a plain reference (:264-284).
        var cited = entries.Select((entry, index) => (entry, index))
            .Where(e => e.entry["path"].Truthy() && e.entry["lines"].Truthy() && !codeIds.Contains(Key(e.entry["id"]))).ToList();
        if (cited.Count > 0 && !string.IsNullOrEmpty(o.RepoRoot))
        {
            JsonObject? metadata = null;
            try { metadata = document["meta"]?["repository"].Truthy() == true ? document["meta"]!["repository"]!.DeepClone().AsObject() : DeriveRepository(o); }
            catch (Exception) { }   // `catch {}`
            foreach (var (entry, index) in metadata is null ? [] : cited)
            {
                try
                {
                    var verified = Verify(metadata!, [(entry, index)], o.RepoRoot);
                    var revision = verified["repository"].Str("revision")!;
                    var bytes = GitBytes(o, o.RepoRoot, ["show", $"{revision}:{entry.Str("path")}"], $"/sources/{index}");
                    var text = DecodeStrict(bytes);
                    if (text.Contains('\0') || ContainsSecret(text, entry.Str("path"))) continue;
                    var result = RepositorySource(entry, revision, verified["nodes"]![entry.Str("id")!]![0]!.AsObject());
                    result["text"] = Excerpt(text, entry);
                    result["sha256"] = Sha256(bytes);
                    result["bytes"] = bytes.Length;
                    sources[entry.Str("id")!] = result;
                    dependencies.Add(RepositoryDependency(entry, revision, bytes));
                    repository ??= verified["repository"]!.DeepClone().AsObject();
                }
                catch (Exception) { }   // `catch {}`: the citation stays a plain reference
            }
        }
        foreach (var (block, pointer) in blocks)
        {
            if (block.Str("type") != "code") continue;
            var source = sources[Key(block["source"])] as JsonObject;
            if (source is null || !source["path"].Truthy() || source["text"] is not JsonValue text || !text.TryGetValue<string>(out _))
                throw Fail(o, $"{pointer}/source", "code", "code-source", "Code blocks require a Git code excerpt.", "Reference a repository source id with a valid line range.");
        }
        long assetBytes = 0;
        foreach (var (block, pointer) in blocks)
        {
            if (block.Str("type") != "image") continue;
            var asset = ReadImage(o, block["path"], pointer);
            assetBytes += (long)asset["bytes"]!.D();
            if (assetBytes > MaxAssetBytes)
                throw Fail(o, pointer, "image", "asset-budget", "Embedded images exceed the 32 MiB document limit.", "Reduce the total embedded image bytes below 32 MiB.");
            var frozen = new JsonObject { ["id"] = block["id"]?.DeepClone() };
            if (block.ContainsKey("alt")) frozen["alt"] = block["alt"]?.DeepClone();
            foreach (var (key, value) in asset) frozen[key] = value!.DeepClone();
            assets[block.Str("id")!] = frozen;
            dependencies.Add(new JsonObject
            {
                ["kind"] = "image", ["id"] = block["id"]?.DeepClone(), ["path"] = asset["path"]!.DeepClone(), ["sha256"] = asset["sha256"]!.DeepClone(),
                ["bytes"] = asset["bytes"]!.DeepClone(), ["contentBase64"] = asset["contentBase64"]!.DeepClone(),
            });
        }
        // A data chart reads its CSV or JSON once, freezes the bytes and keeps the chart text built from them (:302-316).
        var charts = new JsonObject();
        foreach (var (block, pointer) in blocks)
        {
            if (block.Str("type") != "chart" || !block["data"].Truthy()) continue;
            if (string.IsNullOrEmpty(o.BaseDir))
                throw Fail(o, pointer, "chart", "asset-root", "A data chart requires an input directory.", "Run finalize on the draft file next to its data.");
            var data = block["data"]!;
            var relative = SafePath(o, data["path"], $"{pointer}/data/path", "chart");
            if (!DataPath.IsMatch(relative)) throw Fail(o, $"{pointer}/data/path", "chart", "chart-data-format", "Chart data is a .csv or .json file.", "data=latency.csv");
            var bytes = ReadContained(o, o.BaseDir, relative, pointer, "chart", MaxDataBytes, "2 MiB");
            // ponytail: upstream's catch also turns a TypeError from a malformed `data` (y not a list) into the not-UTF-8 message;
            // the schema rules that shape out, so only the decoder failure maps here.
            var built = ExplainerCharts.FromData(bytes, relative, new(Key(data["kind"]), Key(data["x"]),
                [.. data["y"].AsArrayOrEmpty().Select(Key)], data["unit"].Truthy() ? Js.String(data["unit"]) : null));
            var error = built.NotUtf8 ? "The data file is not UTF-8 text." : built.Error ?? ExplainerCharts.Compile(built.Text!).Error;
            if (error is not null) throw Fail(o, pointer, "chart", "chart-data", error, "@chart type=bar data=latency.csv x=day y=p50,p99");
            charts[Key(block["id"])] = built.Text;
            var dependency = new JsonObject { ["kind"] = "data", ["id"] = block["id"]?.DeepClone(), ["path"] = relative };
            foreach (var (key, value) in Digest(bytes)) dependency[key] = value!.DeepClone();
            dependencies.Add(dependency);
        }
        return new JsonObject { ["sources"] = sources, ["assets"] = assets, ["charts"] = charts, ["dependencies"] = dependencies, ["repository"] = repository };
    }

    // A JS property key from a value: String(value), undefined for a missing one.
    static string Key(JsonNode? value) => value is null ? "undefined" : Js.String(value);
    static bool IsSafeInteger(JsonNode? value) => IsInteger(value) && Math.Abs(value!.D()) <= 9007199254740991;

    // The patterns' `/i` has no `u`, so it folds ASCII letters only: each letter is spelled as a class, because .NET's
    // IgnoreCase also folds U+017F (long s) to s and U+212A (Kelvin sign) to k. `\b` and `\w` are ASCII.
    const string Word = "[A-Za-z0-9_]";
    const string B = $"(?:(?<={Word})(?!{Word})|(?<!{Word})(?={Word}))";
    const string Ws = Js.WsClass;

    internal static string Ci(string literal)
    {
        var sb = new StringBuilder();
        foreach (var c in literal) sb.Append(char.IsAsciiLetter(c) ? $"[{char.ToUpperInvariant(c)}{char.ToLowerInvariant(c)}]" : Regex.Escape(c.ToString()));
        return sb.ToString();
    }

    static readonly Regex SecretContent = new(
        $"{Ci("-----BEGIN ")}(?:[A-Za-z ]*{Ci("PRIVATE KEY")}|{Ci("PGP PRIVATE KEY BLOCK")})-----"
        + $"|{B}(?:{Ci("AKIA")}|{Ci("ASIA")})[A-Za-z0-9]{{16}}{B}"
        + $"|{B}(?:{Ci("gh")}[PpOoUuSsRr]_[A-Za-z0-9]{{20,}}|{Ci("github_pat_")}[A-Za-z0-9_]{{20,}}|{Ci("sk-")}(?:{Ci("proj-")})?[A-Za-z0-9_-]{{20,}}){B}");
    static readonly Regex CredentialAssignment = new(
        $"{B}(?:{Ci("password")}|{Ci("passwd")}|{Ci("secret")}|{Ci("api")}[_-]?{Ci("key")}|{Ci("access")}[_-]?{Ci("token")}|{Ci("auth")}[_-]?{Ci("token")}|{Ci("client")}[_-]?{Ci("secret")}){B}[\"']?[ \\t]*[:=][ \\t]*([^\\r\\n]*)");
    static readonly Regex EnvironmentReference = new(
        $@"\A(?:\$(?:[A-Za-z_]{Word}*|\{{[A-Za-z_]{Word}*\}})|(?:process\.env|os\.environ|env)(?:\.[A-Za-z_]{Word}*|\[['""][A-Za-z_]{Word}*['""]\])|(?:os\.getenv|getenv|env)\({Ws}*['""][A-Za-z_]{Word}*['""]{Ws}*\))\z");
    static readonly Regex Configuration = new($@"\.(?:[Yy][Aa]?[Mm][Ll]|{Ci("toml")}|{Ci("ini")}|{Ci("conf")}|{Ci("config")}|{Ci("properties")}|{Ci("cfg")}|{Ci("sh")}|{Ci("bash")})\z");
    static readonly Regex Comment = new($@"{Ws}+(?:#|//)[^\n\r\u2028\u2029]*\z");
    static readonly Regex TrailingSeparator = new($@"[;,]{Ws}*\z");
    static readonly Regex Empty = new($@"\A(?:{Ci("null")}|{Ci("nil")}|{Ci("none")}|{Ci("undefined")}|~)\z");
    static readonly Regex Quoted = new(@"\A(['""`])([\s\S]*?)\1");

    /// <summary>containsExplainerSecret (sources.mjs:19-34).</summary>
    public static bool ContainsSecret(string text, string? sourcePath = "")
    {
        if (SecretContent.IsMatch(text)) return true;
        var configuration = Configuration.IsMatch(sourcePath ?? "");
        foreach (Match match in CredentialAssignment.Matches(text))
        {
            var value = Js.Trim(TrailingSeparator.Replace(Comment.Replace(Js.Trim(match.Groups[1].Value), "", 1), "", 1));
            if (value.Length == 0 || Empty.IsMatch(value) || EnvironmentReference.IsMatch(value)) continue;
            var quoted = Quoted.Match(value);
            if (quoted.Success)
            {
                if (quoted.Groups[2].Length >= 4 && !EnvironmentReference.IsMatch(quoted.Groups[2].Value)) return true;
            }
            // Configuration scalars are values, whereas an unquoted JS/Python identifier can be a code reference.
            else if (configuration) return true;
        }
        return false;
    }
}
