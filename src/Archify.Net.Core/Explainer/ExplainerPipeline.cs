using System.Text.Json.Nodes;

namespace Archify.Net;

/// <summary>The explainer pipeline for the CLI: a draft prepared into a bundle, the bundle checked, and the page rendered from it.
/// Bundles are JsonObjects in upstream's key order. Problems throw <see cref="ArchifyDiagnosticException"/>; a chart fence whose
/// front matter is not valid YAML throws <see cref="PeleException"/>.</summary>
public static class ExplainerPipeline
{
    /// <summary>prepareExplainer: the draft parsed, validated, its sources resolved and native diagrams rendered, signed.</summary>
    public static JsonObject Prepare(string sourceText, PrepareOptions options) => ExplainerPrepare.Prepare(sourceText, options);

    /// <summary>readPreparedBundle on an in-memory bundle: a copy, checked against the draft it must belong to.</summary>
    public static JsonObject ReadPrepared(JsonObject bundle, string sourceText, string inputPath, ReadPreparedOptions options) =>
        ExplainerPrepare.ReadPrepared(bundle, sourceText, inputPath, options);

    /// <summary>assertPreparedDependencies: frozen bytes match their identity and the files they came from are unchanged.</summary>
    public static void AssertDependencies(JsonObject bundle) => ExplainerPrepare.AssertDependencies(bundle);

    /// <summary>writePreparedBundle's limit: the serialised bundle stays within 64 MiB.</summary>
    public static void CheckBundleSize(JsonObject bundle) => ExplainerPrepare.CheckBundleSize(bundle);

    /// <summary>render-explainer.mjs:21: <c>renderExplainer(bundle.document, {...bundle, preparedBundle: bundle, pageDirectory})</c>.
    /// A null pageDirectory falls back to the bundle's baseDir, then its draft's directory.</summary>
    public static string Render(JsonObject bundle, string? pageDirectory) =>
        ExplainerRender.Render(bundle["document"] as JsonObject ?? throw new ArgumentException("The bundle holds no document.", nameof(bundle)),
            RenderExplainerOptions.FromBundle(bundle, pageDirectory));

    /// <summary>preparePanelPatch: one stable panel of an explainer page replaced from a file, every frozen resource outside it
    /// retained. Returns the re-signed bundle; the caller renders and checks it.</summary>
    public static PatchResult Patch(PatchRequest request) => ExplainerPatch.Prepare(request);

    /// <summary>isExplainerHtml: the explainer family marker, or the prepared bundle's payload script.</summary>
    public static bool IsExplainerHtml(string html) => ExplainerCheck.IsExplainerHtml(html);

    /// <summary>checkExplainerHtml(html, {file}): the report with <c>file</c> at index 1, as check-render-output.mjs prints it for
    /// the artifact at <paramref name="file"/>; its diagnostics name that file.</summary>
    public static JsonObject Check(byte[] html, string file) => ExplainerCheck.Check(html, file);

    /// <summary>scanHtml's ids and payload (browser-check.mjs:196-198): each element id's line (a repeated id takes its last line), and
    /// the text of the first <c>archify-explainer-prepared</c> script, or null.</summary>
    public static (Dictionary<string, int> Lines, string? Prepared) ScanIds(string html)
    {
        var (elements, scripts) = ExplainerCheck.Scan(html);
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var element in elements)
            if (element.Attrs.GetValueOrDefault("id") is { Length: > 0 } id) lines[id] = element.Line;
        return (lines, scripts.FirstOrDefault(s => s.Attrs.GetValueOrDefault("id") == "archify-explainer-prepared")?.Text);
    }

    /// <summary>rebindPreparedBundle({...bundle, inputPath}, bundle's own parse): the bundle bound to the draft copy at
    /// <paramref name="inputPath"/> and signed again (archify.mjs:5333-5335, patch).</summary>
    public static JsonObject Rebind(JsonObject bundle, string inputPath)
    {
        var next = bundle.DeepClone().AsObject();
        next["inputPath"] = inputPath;
        next["source"] = ExplainerPrepare.Identity(ExplainerPrepare.Utf8(next.Str("sourceText") ?? ""));
        next["digest"] = ExplainerPrepare.Digest(next);
        return next;
    }

    /// <summary>The bundle without the draft-copy binding <see cref="Rebind"/> added, signed again: bound to its original draft through
    /// <c>sourcePath</c> only, as a bundle prepared from a draft is. A bundle without <c>inputPath</c> comes back unchanged.</summary>
    /// <remarks>UPSTREAM-DEVIATION patch-binding (UPSTREAM-016, tt-a1i/archify#1085): upstream's patched page embeds the temporary
    /// patched-draft.json as <c>inputPath</c>, and its digest covers it, so the page names a deleted directory and two equal patches
    /// differ. The port drops the binding once deliver has read the bundle against that draft.</remarks>
    public static JsonObject Unbind(JsonObject bundle)
    {
        if (!bundle.ContainsKey("inputPath")) return bundle;
        var next = bundle.DeepClone().AsObject();
        next.Remove("inputPath");
        next["digest"] = ExplainerPrepare.Digest(next);
        return next;
    }

    /// <summary>computeExplainerRevision(bundle): the authored-content identity a page carries as data-revision.</summary>
    public static string Revision(JsonObject bundle) => ExplainerBundle.Revision(bundle);
}
