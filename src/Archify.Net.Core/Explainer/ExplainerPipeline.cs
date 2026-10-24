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

    /// <summary>computeExplainerRevision(bundle): the authored-content identity a page carries as data-revision.</summary>
    public static string Revision(JsonObject bundle) => ExplainerBundle.Revision(bundle);
}
