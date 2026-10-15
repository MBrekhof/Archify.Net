using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net.Cli.Paths;

// The namespace key of a sidecar's artifact could not be established (archify.mjs:47-54, code
// ARCHIFY_SIDECAR_NAMESPACE_INDETERMINATE); Reason is the path-semantics reason.
internal sealed class SidecarNamespaceException(JsonObject reason, string subject = "a stable sidecar namespace")
    : IOException($"Could not establish {subject} ({reason["code"]?.GetValue<string>() ?? "unknown"}).")
{
    public JsonObject Reason { get; } = reason;
}

// UP/renderers/shared/sidecar-path.mjs, whole, plus the delivery sidecar names built on it (archify.mjs:13-17, :35-66).
internal static partial class SidecarPath
{
    public static readonly IReadOnlyList<string> DeliverySuffixes = [".delivery.json", ".delivery-pending.json", ".delivery-lock.json"];
    public static readonly IReadOnlyList<string> CompareSuffixes = [".receipt.json"];

    // /\.~archify-[0-9a-f]{64}$/iu: none of these letters has a non-ASCII case fold, so ASCII classes are exact; `$` is end of input.
    [GeneratedRegex(@"\.~[Aa][Rr][Cc][Hh][Ii][Ff][Yy]-[0-9a-fA-F]{64}\z")]
    private static partial Regex Namespace();

    // UTF-16 length and UTF-8 bytes both within 255 (a lone surrogate counts 3 bytes, as Buffer.byteLength does).
    static bool Fits(string value) => value.Length <= 255 && Encoding.UTF8.GetByteCount(value) <= 255;

    public static bool StemNeedsBounding(string stem, IReadOnlyList<string> suffixes) => !suffixes.All(suffix => Fits(stem + suffix));

    public static string BoundedSidecarStem(string stem, IReadOnlyList<string> suffixes, bool force = false, string? hashDomain = null)
    {
        if (!force && !StemNeedsBounding(stem, suffixes) && !Namespace().IsMatch(stem)) return stem;
        var hashInput = string.IsNullOrEmpty(hashDomain) ? stem : $"{hashDomain}\0{stem}";
        var marker = $".~archify-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))}";
        var codePoints = Js.CodePoints(stem);
        while (codePoints.Count > 0 && StemNeedsBounding(string.Concat(codePoints) + marker, suffixes)) codePoints.RemoveAt(codePoints.Count - 1);
        return string.Concat(codePoints) + marker;
    }

    // `.html` exactly gives the plain stem; any other spelling (`.HTML`, `.htm`) keeps the whole name in a forced hash namespace.
    public static (string Stem, bool Force, string? HashDomain) StemFromComponent(string component) =>
        component.EndsWith(".html", StringComparison.Ordinal)
            ? (component[..^".html".Length], false, null)
            : (component, true, "full-component");

    public static bool IsBoundedSidecarStem(string stem) => Namespace().IsMatch(stem);

    // deliverySidecarNamespace + deliverySidecarPath (archify.mjs:35-66): the artifact's realpath when it exists, its directory's
    // case and normalization semantics for the name, then a bounded stem. `artifactPath` must be absolute.
    public static string DeliverySidecarPath(string artifactPath, string suffix, PathSemantics? semantics = null)
    {
        semantics ??= PathSemantics.Native;
        var artifact = artifactPath;
        try { artifact = semantics.RealPath(artifactPath); }
        catch (Exception error) when (NativeFs.ErrorCode(error) is "ENOENT" or "ENOTDIR") { }
        var key = semantics.SidecarNamespaceComponentKey(Path.GetDirectoryName(artifact) ?? artifact, Path.GetFileName(artifact));
        if (key.Status != "resolved") throw new SidecarNamespaceException(key.Reason);
        var (stem, force, hashDomain) = StemFromComponent(key.ComponentKey!);
        return Path.Join(key.DirectoryPath, BoundedSidecarStem(stem, DeliverySuffixes, force, hashDomain) + suffix);
    }
}
