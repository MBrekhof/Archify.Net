using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// utils.mjs:130-200 applyTemplate.
public static class TemplateWriter
{
    const RegexOptions Ecma = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    static readonly Regex SvgSlotRe = new("      <!-- ARCHIFY:SVG_SLOT_START -->[\\s\\S]*?      <!-- ARCHIFY:SVG_SLOT_END -->", Ecma);
    static readonly Regex CardsSlotRe = new("    <!-- ARCHIFY:CARDS_SLOT_START -->[\\s\\S]*?    <!-- ARCHIFY:CARDS_SLOT_END -->", Ecma);
    // The `m` flag: ^ anchors at any line start. ponytail: .NET's Multiline ^ follows \n only, JS's also \r, U+2028 and
    // U+2029; the bundled template is LF-only, so only a custom template with bare-CR line ends could tell them apart.
    static readonly Regex SubtitleSlotRe = new("^([ \\t]*)<p class=\"subtitle\">\\[Subtitle description\\]</p>[ \\t]*(\\r?\\n)?", Ecma | RegexOptions.Multiline);
    const string SourceEvidencePlaceholder = "    <!-- ARCHIFY:SOURCE_EVIDENCE_DATA -->";
    const string I18nPlaceholder = "    <!-- ARCHIFY:I18N_DATA -->";

    static string SerializeScriptJson(JsonNode? value) =>
        Js.Stringify(value).Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026");

    static readonly string[] TemplatePlaceholders =
    [
        "<html lang=\"en\" data-theme=\"dark\" data-preset=\"[VISUAL PRESET]\">",
        "<title>[PROJECT NAME] Architecture Diagram</title>",
        "<h1>[PROJECT NAME] Architecture</h1>",
        I18nPlaceholder,
    ];

    public static string Apply(string template, string title, string? subtitle, string svg, string cards, string? locale,
        string visualPreset, JsonNode? sourceEvidence, LocaleCatalog catalog)
    {
        if (!SvgSlotRe.IsMatch(template)) throw new InvalidOperationException("applyTemplate: template missing ARCHIFY:SVG_SLOT sentinel");
        if (!CardsSlotRe.IsMatch(template)) throw new InvalidOperationException("applyTemplate: template missing ARCHIFY:CARDS_SLOT sentinel");
        if (!SubtitleSlotRe.IsMatch(template)) throw new InvalidOperationException("applyTemplate: template missing subtitle placeholder");
        foreach (var ph in TemplatePlaceholders)
            if (!template.Contains(ph, StringComparison.Ordinal)) throw new InvalidOperationException($"applyTemplate: template missing placeholder {Js.Quote(ph)}");
        // Keep existing custom templates compatible when evidence is not requested.
        // Silently dropping verified evidence would be misleading, so the new slot
        // becomes mandatory only for the opt-in evidence path.
        if (sourceEvidence is not null && !template.Contains(SourceEvidencePlaceholder, StringComparison.Ordinal))
            throw new InvalidOperationException($"applyTemplate: repository evidence requires placeholder {Js.Quote(SourceEvidencePlaceholder)}");
        // Function replacers: a literal `$&`, `$'`, `$\`` or `$$` in titles, labels,
        // or rendered SVG must not be interpreted as a replacement pattern.
        var sourceEvidenceJson = SerializeScriptJson(sourceEvidence);
        var resolvedLocale = catalog.ResolveLocale(locale);
        var i18nJson = SerializeScriptJson(new JsonObject { ["locale"] = resolvedLocale, ["messages"] = catalog.ViewerCatalog(resolvedLocale) });
        var renderedSubtitle = subtitle is not null && Js.Trim(subtitle).Length > 0 ? $"<p class=\"subtitle\">{Js.EscapeHtml(subtitle)}</p>" : "";
        var i18nData = $"    <script id=\"archify-i18n-data\" type=\"application/json\">{i18nJson}</script>";
        var html = catalog.LocalizeTemplate(template, resolvedLocale);
        html = Js.ReplaceFirst(html, I18nPlaceholder, _ => i18nData);
        html = Js.ReplaceFirst(html, TemplatePlaceholders[0], _ => $"<html lang=\"{Js.EscapeHtml(resolvedLocale)}\" data-theme=\"dark\" data-preset=\"{Js.EscapeHtml(visualPreset)}\">");
        html = Js.ReplaceFirst(html, TemplatePlaceholders[1], _ => $"<title>{Js.EscapeHtml(catalog.Translate(resolvedLocale, "page.title", new Dictionary<string, string> { ["title"] = title }))}</title>");
        html = Js.ReplaceFirst(html, TemplatePlaceholders[2], _ => $"<h1>{Js.EscapeHtml(title)}</h1>");
        html = SubtitleSlotRe.Replace(html, m => renderedSubtitle.Length > 0 ? $"{m.Groups[1].Value}{renderedSubtitle}{m.Groups[2].Value}" : "", 1);
        html = SvgSlotRe.Replace(html, _ => svg, 1);
        html = CardsSlotRe.Replace(html, _ => cards, 1);
        return Js.ReplaceFirst(html, SourceEvidencePlaceholder, _ => sourceEvidence is not null
            ? $"    <script id=\"archify-source-evidence-data\" type=\"application/json\">{sourceEvidenceJson}</script>"
            : "");
    }
}
