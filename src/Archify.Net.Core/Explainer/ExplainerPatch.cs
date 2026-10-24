using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

/// <summary>preparePanelPatch's options. ExpectedArtifact is the validated original's {sha256, bytes}.</summary>
public sealed record PatchRequest(string ArtifactPath, string PanelId, string ReplacementPath, string? RepoRoot = null, string? Quality = null,
    string? WritingMode = null, JsonObject? ExpectedArtifact = null);

/// <summary>A prepared panel patch: the new bundle, its draft, and the original's identity.</summary>
public sealed record PatchResult(JsonObject Bundle, string SourceText, string SourcePath, string PreviousRevision, string PreviousSourceSha256, string PanelId)
{
    public JsonObject ToJson() => new()
    {
        ["bundle"] = Bundle.DeepClone(), ["sourceText"] = SourceText, ["sourcePath"] = SourcePath, ["previousRevision"] = PreviousRevision,
        ["previousSourceSha256"] = PreviousSourceSha256, ["panelId"] = PanelId,
    };
}

// archify 3.2.0 explainer/patch.mjs: one authored stable panel replaced, every immutable resource outside it retained.
internal static class ExplainerPatch
{
    const string Word = "[A-Za-z0-9_]";
    static string Ci(string literal) => ExplainerSources.Ci(literal);
    static readonly Regex Prepared = new($"<{Ci("script")}(?!{Word})[^>]*(?<!{Word}){Ci("id")}=[\"']{Ci("archify-explainer-prepared")}[\"'][^>]*>([\\s\\S]*?)</{Ci("script")}>");

    // fail (:11-14); an undefined panelId leaves the key out
    static Exception Fail(string reason, string message, string input, string? panelId, string example)
    {
        var subject = new JsonObject { ["input"] = input };
        if (panelId is not null) subject["panelId"] = panelId;
        subject["line"] = 1;
        subject["component"] = "panel";
        return new ArchifyDiagnosticException(message, [Diagnostic.Normalize(new($"explainer/patch-{reason}", Severity.Error, message, subject,
            new JsonObject { ["reason"] = reason, ["example"] = example }, [example], null))]);
    }

    // embedded (:15-19)
    static JsonNode? Embedded(string html, string input)
    {
        var matches = Prepared.Matches(html);
        if (matches.Count != 1) throw Fail("payload", "The artifact must contain one original draft and prepared resource payload.", input, null, "Use an artifact produced by the formal explainer CLI.");
        var parsed = ExplainerJson.Parse(matches[0].Groups[1].Value, out string? error);
        if (error is not null) throw Fail("payload", "Embedded draft payload is invalid.", input, null, "Rebuild the artifact from its original draft.");
        return parsed;
    }

    static JsonObject Merge(JsonNode? a, JsonNode? b)   // {...a, ...b}
    {
        var merged = new JsonObject();
        foreach (var source in new[] { a, b })
            foreach (var (key, value) in source as JsonObject ?? []) merged[key] = value?.DeepClone();
        return merged;
    }
    static JsonObject Keep(JsonObject map, HashSet<string> ids) => new(map.Where(kv => ids.Contains(kv.Key)).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
    static string Key(JsonNode? entry) => $"{Js.String(entry?["kind"] ?? "undefined")}:{Js.String(entry?["id"] ?? "undefined")}";   // `${kind}:${id}`

    /// <summary>preparePanelPatch (:21-117).</summary>
    public static PatchResult Prepare(PatchRequest r)
    {
        var (artifactPath, panelId) = (r.ArtifactPath, r.PanelId);
        var artifactBytes = File.ReadAllBytes(artifactPath);
        if (r.ExpectedArtifact is { } expected && (!(expected["bytes"] is JsonValue eb && eb.GetValueKind() == System.Text.Json.JsonValueKind.Number && eb.D() == artifactBytes.Length)
            || expected.Str("sha256") != ExplainerSources.Sha256(artifactBytes)))
            throw Fail("artifact-changed", "The original artifact changed after validation.", artifactPath, panelId, "Finish other writes to the artifact and retry the panel patch.");
        var html = Encoding.UTF8.GetString(artifactBytes);
        // UPSTREAM-DEVIATION patch-payload-message: patch.mjs:26 rethrows only an error with `diagnostics`, but throwDiagnosticError
        // attaches `archifyDiagnostics`, so upstream reports a missing or unparsable payload as "cannot be inflated". The port
        // lets embedded()'s own diagnostic through.
        var embedded = Embedded(html, artifactPath);
        JsonNode? previousNode;
        try { previousNode = embedded is JsonObject packed ? ExplainerBundle.Unpack(packed) : embedded; }
        catch (Exception) { throw Fail("payload", "Embedded native diagram payload cannot be inflated.", artifactPath, panelId, "Rebuild the original artifact before patching."); }

        // The page names local files relative to itself; resolve them before reading or re-preparing.
        var pageDirectory = Path.GetDirectoryName(artifactPath) ?? "";
        JsonNode? Near(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? Path.GetFullPath(Path.Combine(pageDirectory, s)) : value?.DeepClone();
        var previous = previousNode as JsonObject;
        var sourceText = previous.Str("sourceText");
        if (previous is null || sourceText is null || ExplainerBundle.Hash(sourceText) != previous["source"].Str("sha256") || ExplainerPrepare.Digest(previous) != previous.Str("digest"))
            throw Fail("payload", "The embedded original draft is not bound to the prepared resources.", artifactPath, panelId, "Rebuild the original artifact before patching.");
        var draftSourcePath = Near(previous["sourcePath"]);
        var sourcePath = draftSourcePath is JsonValue dv && dv.TryGetValue<string>(out var ds) && ds.Length > 0 ? ds : artifactPath;   // draft.sourcePath || artifactPath

        var oldParsed = ExplainerPrepare.ParseValidated(sourceText, sourcePath, null, "off");
        if (Js.Stringify(oldParsed.Document) != Js.Stringify(previous["document"]))
            throw Fail("payload", "The original draft and normalized document disagree.", artifactPath, panelId, "Rebuild the original artifact before patching.");
        var oldPanels = oldParsed.Document!["panels"]!.AsArray();
        var index = oldPanels.Select((panel, i) => (panel, i)).FirstOrDefault(p => p.panel.Str("id") == panelId, (null, -1)).Item2;
        if (index < 0) throw Fail("target", "The requested panel does not exist.", artifactPath, panelId, "Choose a stable panel id from the original artifact.");
        var range = oldParsed.SourceMap[$"/panels/{index}"];
        static bool Integer(JsonNode? n) => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.IsInteger(v.D());   // Number.isInteger
        if (!Integer(range?["start"]) || !Integer(range?["end"]))
            throw Fail("range", "The original draft has no replaceable panel range.", artifactPath, panelId, "Rebuild the artifact using the current parser.");
        var replacementBytes = File.ReadAllBytes(r.ReplacementPath);
        var replacement = Encoding.UTF8.GetString(replacementBytes);
        if (!Encoding.UTF8.GetBytes(replacement).AsSpan().SequenceEqual(replacementBytes))
            throw Fail("source-encoding", "Panel replacement must contain valid UTF-8 bytes.", r.ReplacementPath, panelId, "Save the replacement panel as UTF-8 text.");
        var (start, end) = ((int)range!["start"]!.D(), (int)range["end"]!.D());
        var patchedText = sourceText[..start] + replacement + sourceText[end..];
        var writingMode = !string.IsNullOrEmpty(r.WritingMode) ? r.WritingMode : previous["writingMode"].Truthy() ? Js.String(previous["writingMode"]) : "warn";
        var parsed = ExplainerPrepare.ParseValidated(patchedText, sourcePath, null, writingMode);
        var document = parsed.Document!.AsObject();
        var panels = document["panels"]!.AsArray();
        if (panels.Count != oldPanels.Count || panels[index].Str("id") != panelId)
            throw Fail("identity", "A panel replacement must retain the target stable id and panel count.", r.ReplacementPath, panelId,
                oldParsed.Format == "json" ? "{\"id\":\"" + panelId + "\",\"title\":\"Question?\",\"blocks\":[...]}" : "## Question? {#" + panelId + "}\n\nReplacement content.");
        var scope = oldParsed.Document.DeepClone();
        scope["panels"]![index] = panels[index]!.DeepClone();
        if (Js.Stringify(scope) != Js.Stringify(document))
            throw Fail("scope", "The replacement changes content outside the selected panel.", r.ReplacementPath, panelId, "Replace only the target panel; preserve document metadata, sources and other panels.");

        var panel = panels[index]!.AsObject();
        var blocks = panel["blocks"].AsArrayOrEmpty().OfType<JsonObject>().ToList();
        var oldBlocks = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var block in oldPanels[index]!["blocks"].AsArrayOrEmpty().OfType<JsonObject>()) oldBlocks[block.Str("id") ?? ""] = block;
        JsonObject? OldBlock(JsonObject block) => oldBlocks.GetValueOrDefault(block.Str("id") ?? "");
        var changed = blocks.Where(block => OldBlock(block) is not { } old || Js.Stringify(old) != Js.Stringify(block)).ToList();
        var previousSources = previous["preparedSources"] as JsonObject;
        var previousDiagrams = previous.Obj("preparedDiagrams").Obj("diagrams");
        // Already verified immutable code excerpts remain usable after the original checkout disappears.
        var changedCodeIds = changed.Where(block => block.Str("type") == "code"
                && previousSources.Obj("sources").Obj(Js.String(block["source"] ?? "undefined")).Str("text") is null)
            .Select(block => block.Str("source") ?? "").ToHashSet(StringComparer.Ordinal);
        var selectedQuality = !string.IsNullOrEmpty(r.Quality) ? r.Quality : previous["quality"].Truthy() ? Js.String(previous["quality"]) : "standard";
        var qualityChanged = previous.Str("quality") != selectedQuality;
        var oldDiagramBlocks = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var block in oldPanels.SelectMany(p => p!["blocks"].AsArrayOrEmpty()).OfType<JsonObject>().Where(b => b.Str("type") == "diagram"))
            oldDiagramBlocks[block.Str("id") ?? ""] = block;
        var reusedDiagramSources = new HashSet<string>(StringComparer.Ordinal);
        JsonObject FrozenDiagramInput(JsonObject block)
        {
            var id = block.Str("id") ?? "";
            if (block.Str("type") == "diagram" && block["source"].Truthy() && oldDiagramBlocks.GetValueOrDefault(id) is { } old
                && JsonNode.DeepEquals(old["source"], block["source"]) && previousDiagrams?[id]?["spec"].Truthy() == true)
            {
                reusedDiagramSources.Add(id);
                var frozen = block.DeepClone().AsObject();
                frozen.Remove("source");   // {...block, source: undefined, spec}
                frozen["spec"] = previousDiagrams[id]!["spec"]!.DeepClone();
                return frozen;
            }
            return block.DeepClone().AsObject();
        }
        var partialBlocks = changed.Where(block =>
            (block.Str("type") != "code" || changedCodeIds.Contains(block.Str("source") ?? ""))
            && !(block.Str("type") == "image" && OldBlock(block) is { } oldImage && JsonNode.DeepEquals(oldImage["path"], block["path"]) && previousSources?["assets"]?[block.Str("id") ?? ""].Truthy() == true)
            && !(block.Str("type") == "chart" && block["data"].Truthy() && Js.Stringify(OldBlock(block)?["data"]) == Js.Stringify(block["data"]) && previousSources?["charts"]?[block.Str("id") ?? ""].Truthy() == true))
            .Select(FrozenDiagramInput).ToList();
        var partial = document.DeepClone().AsObject();
        var partialPanel = panel.DeepClone().AsObject();
        partialPanel["blocks"] = new JsonArray([.. partialBlocks]);
        partial["panels"] = new JsonArray(partialPanel);
        var partialSources = document["sources"].AsArrayOrEmpty().Where(source => changedCodeIds.Contains(source.Str("id") ?? "\0")).ToList();
        partial["sources"] = new JsonArray([.. partialSources.Select(s => s!.DeepClone())]);

        string? baseDir = previous.TryGetPropertyValue("baseDir", out var previousBase) && previousBase is null ? null
            : Near(previousBase) is JsonValue bv && bv.TryGetValue<string>(out var b) && b.Length > 0 ? b : Path.GetDirectoryName(sourcePath);
        var common = new ResourceOptions(r.RepoRoot, baseDir, parsed.SourceMap, sourcePath, selectedQuality);
        var partialMap = parsed.SourceMap.DeepClone().AsObject();
        void Remap(string originalPrefix, string partialPrefix)
        {
            foreach (var (pointer, location) in parsed.SourceMap)
                if (pointer == originalPrefix || pointer.StartsWith(originalPrefix + "/", StringComparison.Ordinal))
                    partialMap[partialPrefix + pointer[originalPrefix.Length..]] = location?.DeepClone();
        }
        for (var i = 0; i < partialBlocks.Count; i++)
            Remap($"/panels/{index}/blocks/{blocks.FindIndex(entry => entry.Str("id") == partialBlocks[i].Str("id"))}", $"/panels/0/blocks/{i}");
        for (var i = 0; i < partialSources.Count; i++)
            Remap($"/sources/{document["sources"]!.AsArray().Select(s => s.Str("id")).ToList().IndexOf(partialSources[i].Str("id"))}", $"/sources/{i}");
        var newSources = ExplainerSources.Resolve(partial, common with { SourceMap = partialMap });
        // A profile change needs fresh native validation for every child, including untouched panels. Frozen original specs keep
        // this independent of old files.
        JsonObject diagramDocument;
        if (qualityChanged)
        {
            diagramDocument = document.DeepClone().AsObject();
            foreach (var p in diagramDocument["panels"]!.AsArray().OfType<JsonObject>())
                p["blocks"] = new JsonArray([.. p["blocks"].AsArrayOrEmpty().OfType<JsonObject>().Select(FrozenDiagramInput)]);
        }
        else diagramDocument = partial;
        var newDiagrams = ExplainerDiagrams.Prepare(diagramDocument, common with { SourceMap = qualityChanged ? parsed.SourceMap : partialMap });
        var previousDependencies = previous["dependencies"].AsArrayOrEmpty().ToList();
        var diagramDependencies = new JsonArray();
        foreach (var entry in newDiagrams["dependencies"]!.AsArray().OfType<JsonObject>())
        {
            var id = entry.Str("id") ?? "";
            if (!reusedDiagramSources.Contains(id)) { diagramDependencies.Add(entry.DeepClone()); continue; }
            var original = previousDependencies.FirstOrDefault(dep => dep.Str("kind") == "diagram-spec" && dep.Str("id") == id) as JsonObject
                ?? throw Fail("payload", "The original diagram has no frozen specification evidence.", artifactPath, panelId, "Rebuild the original artifact before patching.");
            var child = newDiagrams["diagrams"]![id]!.AsObject();
            if (previousDiagrams![id]!.AsObject().TryGetPropertyValue("specification", out var specification)) child["specification"] = specification?.DeepClone();
            else child.Remove("specification");   // = undefined
            var retained = original.DeepClone().AsObject();
            if (retained.ContainsKey("path")) retained["path"] = Near(original["path"]);
            retained["retained"] = true;
            diagramDependencies.Add(retained);
        }

        var preparedSources = previousSources?.DeepClone().AsObject() ?? [];
        preparedSources["sources"] = Merge(previousSources?["sources"], newSources["sources"]);
        preparedSources["assets"] = Merge(previousSources?["assets"], newSources["assets"]);
        preparedSources["charts"] = Merge(previousSources?["charts"], newSources["charts"]);
        var preparedDiagrams = previous["preparedDiagrams"]?.DeepClone() as JsonObject ?? [];
        preparedDiagrams["diagrams"] = Merge(previousDiagrams, newDiagrams["diagrams"]);
        var activeBlocks = panels.SelectMany(p => p!["blocks"].AsArrayOrEmpty()).OfType<JsonObject>().ToList();
        HashSet<string> Ids(Func<JsonObject, bool> keep) => activeBlocks.Where(keep).Select(block => block.Str("id") ?? "").ToHashSet(StringComparer.Ordinal);
        var imageIds = Ids(block => block.Str("type") == "image");
        var diagramIds = Ids(block => block.Str("type") == "diagram");
        var dataChartIds = Ids(block => block.Str("type") == "chart" && block["data"].Truthy());
        preparedSources["assets"] = Keep(preparedSources["assets"]!.AsObject(), imageIds);
        preparedSources["charts"] = Keep(preparedSources["charts"]!.AsObject(), dataChartIds);
        if (preparedSources["charts"]!.AsObject().Count == 0) preparedSources.Remove("charts");
        preparedDiagrams["diagrams"] = Keep(preparedDiagrams["diagrams"]!.AsObject(), diagramIds);
        ExplainerPrepare.ValidatePreparedTargets(document, preparedDiagrams["diagrams"]!.AsObject(), common);

        var newDependencies = newSources["dependencies"].AsArrayOrEmpty().Concat(diagramDependencies).OfType<JsonObject>().ToList();
        var preparedIds = newDependencies.Select(Key).ToHashSet(StringComparer.Ordinal);
        static JsonObject Local(JsonObject entry, string? directory)
        {
            var local = entry.DeepClone().AsObject();
            if (!(entry.Str("kind") == "repository" || entry["inline"].Truthy() || !entry["path"].Truthy()))
                local["path"] = Path.GetFullPath(Path.Combine(directory!, Js.String(entry["path"])));
            return local;
        }
        var dependencies = previousDependencies.OfType<JsonObject>().Where(entry => !preparedIds.Contains(Key(entry))
                && (entry.Str("kind") != "image" || imageIds.Contains(entry.Str("id") ?? "\0"))
                && (entry.Str("kind") != "data" || dataChartIds.Contains(entry.Str("id") ?? "\0"))
                && (entry.Str("kind") != "diagram-spec" || diagramIds.Contains(entry.Str("id") ?? "\0")))
            .Select(entry => Local(entry, pageDirectory))
            .Concat(newDependencies.Select(entry => Local(entry, baseDir))).ToList();
        // Old dependencies are embedded immutable bytes. Only newly prepared resources are reread/verified.
        var unbound = previous.DeepClone().AsObject();
        unbound["sourcePath"] = sourcePath;
        unbound["baseDir"] = baseDir;
        unbound["preparedSources"] = preparedSources;
        unbound["preparedDiagrams"] = preparedDiagrams;
        unbound["dependencies"] = new JsonArray([.. dependencies.Select(d => (JsonNode?)d.DeepClone())]);
        unbound["quality"] = selectedQuality;
        unbound["writingMode"] = writingMode;
        var bundle = ExplainerPrepare.Rebind(unbound, parsed);
        bundle["dependencies"] = new JsonArray([.. dependencies.Select(entry =>
        {
            var marked = entry.DeepClone().AsObject();
            marked["retained"] = Js.Stringify(entry["retained"]) == "true" || !preparedIds.Contains(Key(entry));
            return (JsonNode?)marked;
        })]);
        return new PatchResult(ExplainerPrepare.Rebind(bundle, parsed), patchedText, sourcePath, ExplainerBundle.Revision(previous), previous["source"].Str("sha256")!, panelId);
    }
}
