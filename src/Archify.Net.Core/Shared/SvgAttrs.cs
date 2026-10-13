using System.Text.Json.Nodes;

namespace Archify.Net;

// cli.mjs:360-429.
public static class SvgAttrs
{
    // Accessible name for the generated diagram SVG.
    public static string SvgRootAttrs(RenderContext ctx)
    {
        var meta = ctx.Meta;
        // Motion is a Viewer capability for every diagram; Live/Still controls playback.
        const string animation = " data-animation=\"trace\"";
        var preset = $" data-preset=\"{Js.EscapeHtml(Truthy(meta.Str("visual_preset")) ?? "classic")}\"";
        var engineeringProfile = Truthy(meta.Str("engineering_profile")) is { } profile ? $" data-engineering-profile=\"{Js.EscapeHtml(profile)}\"" : "";
        // `explicitQualityProfile || process.env.ARCHIFY_QUALITY_PROFILE || meta.quality_profile`: RenderOptions.Quality
        // replaces both the explicit argument and the environment variable.
        var requestedProfile = ctx.Options.Quality?.Name() ?? Truthy(meta.Str("quality_profile"));
        var qualityProfile = requestedProfile == "showcase" ? "showcase" : "standard";
        var advisory = requestedProfile is not null ? "" : " data-quality-gates=\"advisory\"";
        return $"role=\"img\" lang=\"{Js.EscapeHtml(ctx.Locale.ResolveLocale(meta.Str("locale")))}\" aria-labelledby=\"archify-diagram-title archify-diagram-description\"{animation}{preset}{engineeringProfile} data-quality-profile=\"{Js.EscapeHtml(qualityProfile)}\"{advisory}";
    }

    // Keep the accessible name inside the SVG so it survives standalone SVG
    // export and embedding. The fixed IDs are deterministic because an Archify
    // artifact intentionally contains one primary diagram SVG.
    public static string SvgAccessibleText(RenderContext ctx, string kind)
    {
        var meta = ctx.Meta;
        var description = Truthy(meta.Str("subtitle")) ?? ctx.Locale.Translate(meta.Str("locale"), $"diagram.description.{kind}");
        return $"        <title id=\"archify-diagram-title\">{Js.EscapeHtml(meta.Str("title"))}</title>\n        <desc id=\"archify-diagram-description\">{Js.EscapeHtml(description)}</desc>";
    }

    public static string AnimateAttr(JsonObject meta, string kind, double? step)
    {
        // Ambient trace must finish inside the fixed six-second WebM capture. The
        // cap affects visual delay only; authored order and semantic identity stay
        // untouched in the JSON, DOM, and relationship contracts.
        var safeStep = step is { } s && double.IsFinite(s) && s >= 0 ? Math.Min(12, Math.Floor(s)) : 0;
        return $" data-animate=\"{kind}\" style=\"--step:{Js.Num(safeStep)}\"";
    }

    static readonly (string Attr, string Key)[] OptionalNodeAttrs =
    [
        ("data-node-kind", "kind"),
        ("data-node-sublabel", "sublabel"),
        ("data-node-tag", "tag"),
        ("data-node-context", "context"),
        ("data-node-brand", "brand"),
        ("data-node-brand-id", "brandId"),
        ("data-node-brand-status", "brandStatus"),
        ("data-node-brand-source", "brandSource"),
    ];

    // Stable semantic hooks for the standalone HTML explorer. IDs already pass
    // the schema's conservative identifier pattern; escape again at the markup
    // boundary so these helpers remain safe if that contract expands later.
    // ponytail: metadata values are read as strings: every renderer's passport holds strings or omits the key.
    public static string FocusNodeAttrs(string id, string label, JsonObject? metadata, string? locale, RenderContext ctx)
    {
        var optional = string.Concat(OptionalNodeAttrs
            .Select(a => (a.Attr, Value: Present(metadata.Str(a.Key))))
            .Where(p => p.Value is not null)
            .Select(p => $" {p.Attr}=\"{Js.EscapeHtml(p.Value)}\""));
        var detail = string.Join(", ", new[] { metadata.Str("sublabel"), metadata.Str("context"), metadata.Str("brand") }.Where(v => Present(v) is not null));
        var aria = detail.Length > 0
            ? ctx.Locale.Translate(locale, "node.focus.detail", new Dictionary<string, string> { ["label"] = label, ["detail"] = detail })
            : ctx.Locale.Translate(locale, "node.focus", new Dictionary<string, string> { ["label"] = label });
        return $"id=\"node-{Js.EscapeHtml(id)}\" data-node-id=\"{Js.EscapeHtml(id)}\" data-node-label=\"{Js.EscapeHtml(label)}\" tabindex=\"0\" role=\"button\" aria-label=\"{Js.EscapeHtml(aria)}\" aria-pressed=\"false\"{optional}";
    }

    // Native SVG titles preserve a compact details-on-demand fallback when the
    // canonical SVG is embedded inline outside the full Archify viewer.
    public static string FocusNodeTitle(string label, JsonObject? metadata)
    {
        var parts = new[] { label, metadata.Str("sublabel"), metadata.Str("context"), metadata.Str("tag"), metadata.Str("brand") }
            .Where(v => Present(v) is not null);
        return $"<title>{Js.EscapeHtml(string.Join(" · ", parts))}</title>";
    }

    public static string FocusEdgeAttrs(string from, string to, string? label, string key, string? id)
    {
        var named = !string.IsNullOrEmpty(label) ? $" data-edge-label=\"{Js.EscapeHtml(label)}\"" : "";
        var keyed = $" data-edge-key=\"{Js.EscapeHtml(key)}\"";
        var identified = Present(id) is not null ? $" data-edge-id=\"{Js.EscapeHtml(id)}\"" : "";
        return $"data-edge-from=\"{Js.EscapeHtml(from)}\" data-edge-to=\"{Js.EscapeHtml(to)}\"{named}{keyed}{identified}";
    }

    // JS `value || fallback` on a string: the empty string falls through.
    static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;
    // value !== undefined && value !== null && String(value).trim() !== ''
    static string? Present(string? value) => value is not null && Js.Trim(value).Length > 0 ? value : null;
}
