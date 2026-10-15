using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// utils.mjs:1-149 and 201-260. Template literals are spelled with \n escapes, never multi-line literals:
// .gitattributes checks *.cs out as CRLF, which would put \r into the output.
public static class Utils
{
    public static string Esc(string? value) => Js.EscapeHtml(value);

    // `.replace(/[ \t]+$/gm, '')` (render-tree.mjs, render-erd.mjs:1385): JS multiline `$` also stops before CR, U+2028 and
    // U+2029 (built from char codes so no tool decodes the escapes into literal line separators).
    public static readonly Regex TrailingBlanks = new("[ \t]+(?=[\n\r" + (char)0x2028 + (char)0x2029 + "]|\\z)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

    // `extra` appends type-owned definitions (the entity renderer's cardinality
    // markers) inside the same <defs>. Callers that pass nothing keep the exact
    // bytes the five longer-standing renderers already emit.
    public static string RenderDefinitions(string extra = "") =>
        "        <!-- Definitions -->\n" +
        "        <defs>\n" +
        "          <marker id=\"arrowhead\" markerWidth=\"10\" markerHeight=\"7\" refX=\"9\" refY=\"3.5\" orient=\"auto\">\n" +
        "            <polygon points=\"0 0, 10 3.5, 0 7\" class=\"m-default\" />\n" +
        "          </marker>\n" +
        "          <marker id=\"arrowhead-emphasis\" markerWidth=\"10\" markerHeight=\"7\" refX=\"9\" refY=\"3.5\" orient=\"auto\">\n" +
        "            <polygon points=\"0 0, 10 3.5, 0 7\" class=\"m-emphasis\" />\n" +
        "          </marker>\n" +
        "          <marker id=\"arrowhead-security\" markerWidth=\"10\" markerHeight=\"7\" refX=\"9\" refY=\"3.5\" orient=\"auto\">\n" +
        "            <polygon points=\"0 0, 10 3.5, 0 7\" class=\"m-security\" />\n" +
        "          </marker>\n" +
        "          <marker id=\"arrowhead-dashed\" markerWidth=\"10\" markerHeight=\"7\" refX=\"9\" refY=\"3.5\" orient=\"auto\">\n" +
        "            <polygon points=\"0 0, 10 3.5, 0 7\" class=\"m-dashed\" />\n" +
        "          </marker>\n" +
        "          <pattern id=\"grid\" width=\"40\" height=\"40\" patternUnits=\"userSpaceOnUse\">\n" +
        "            <path d=\"M 40 0 L 0 0 0 40\" class=\"c-grid\" stroke-width=\"0.5\"/>\n" +
        $"          </pattern>{extra}\n" +
        "        </defs>";

    static readonly Dictionary<string, string> SigilTone = new(StringComparer.Ordinal)   // lookup only
    {
        ["frontend"] = "frontend",
        ["start"] = "frontend",
        ["backend"] = "backend",
        ["active"] = "frontend",
        ["database"] = "database",
        ["success"] = "backend",
        ["cloud"] = "cloud",
        ["waiting"] = "cloud",
        ["security"] = "security",
        ["decision"] = "database",
        ["failure"] = "security",
        ["messagebus"] = "messagebus",
        ["external"] = "external",
        ["neutral"] = "external",
    };

    const string I12 = "\n            ";   // the 12-space continuation indent inside SIGIL_SHAPE's template literals

    static readonly Dictionary<string, string> SigilShape = new(StringComparer.Ordinal)  // lookup only
    {
        ["calendar"] = "<rect x=\"2\" y=\"3.5\" width=\"12\" height=\"10.5\" rx=\"2\"/><path d=\"M5 2v3M11 2v3M2 7h12M5 10h2M9 10h2\"/>",
        ["clock"] = "<circle cx=\"8\" cy=\"8\" r=\"6\"/><path d=\"M8 4v4l3 2\"/>",
        ["person"] = "<circle cx=\"8\" cy=\"4.5\" r=\"2.5\"/><path d=\"M3 14v-2a5 5 0 0 1 10 0v2\"/>",
        ["briefcase"] = "<rect x=\"2\" y=\"5\" width=\"12\" height=\"9\" rx=\"2\"/><path d=\"M5 5V2h6v3M2 9h12M7 9v2h2V9\"/>",
        ["flag"] = "<path d=\"M3 14V2h10l-2 3 2 3H3\"/>",
        ["moon"] = "<path d=\"M13.5 10A6 6 0 0 1 6 2.5 6 6 0 1 0 13.5 10Z\"/>",
        ["frontend"] = "<rect x=\"2\" y=\"3\" width=\"12\" height=\"10\" rx=\"2\"/>" +
            I12 + "<path d=\"M2 6.5h12\"/>" +
            I12 + "<circle cx=\"4.1\" cy=\"4.8\" r=\".7\" class=\"sigil-fill\"/>" +
            I12 + "<circle cx=\"6.3\" cy=\"4.8\" r=\".7\" class=\"sigil-fill\"/>",
        ["backend"] = "<path d=\"M6 3 3 8l3 5M10 3l3 5-3 5\"/>",
        ["database"] = "<ellipse cx=\"8\" cy=\"4\" rx=\"5\" ry=\"2\"/>" +
            I12 + "<path d=\"M3 4v8c0 1.1 2.2 2 5 2s5-.9 5-2V4M3 8c0 1.1 2.2 2 5 2s5-.9 5-2\"/>",
        ["cloud"] = "<path d=\"M4.3 12.5h7.3a2.4 2.4 0 0 0 .2-4.8 4 4 0 0 0-7.5-1.3A3.1 3.1 0 0 0 4.3 12.5Z\"/>",
        ["security"] = "<path d=\"M8 2.2 13 4v3.5c0 3.1-1.8 5.4-5 6.5-3.2-1.1-5-3.4-5-6.5V4Z\"/>" +
            I12 + "<path d=\"m5.8 8 1.5 1.5 3-3\"/>",
        ["messagebus"] = "<path d=\"M2.5 4.5h11M2.5 8h11M2.5 11.5h11\"/>" +
            I12 + "<circle cx=\"5\" cy=\"4.5\" r=\"1\" class=\"sigil-fill\"/>" +
            I12 + "<circle cx=\"10.5\" cy=\"8\" r=\"1\" class=\"sigil-fill\"/>" +
            I12 + "<circle cx=\"7\" cy=\"11.5\" r=\"1\" class=\"sigil-fill\"/>",
        ["external"] = "<rect x=\"2.5\" y=\"5\" width=\"8.5\" height=\"8\" rx=\"1.5\"/>" +
            I12 + "<path d=\"M8 2.5h5.5V8M13.5 2.5 7.5 8.5\"/>",
        ["start"] = "<circle cx=\"8\" cy=\"8\" r=\"5\"/>" +
            I12 + "<path d=\"m7 5.4 3.6 2.6L7 10.6Z\" class=\"sigil-fill\"/>",
        ["active"] = "<path d=\"M2 8h3l1.5-3.5L9 12l1.6-4H14\"/>",
        ["waiting"] = "<path d=\"M4 2.5h8M4 13.5h8M5 3c0 2.8 2 3.2 3 5-1 1.8-3 2.2-3 5M11 3c0 2.8-2 3.2-3 5 1 1.8 3 2.2 3 5\"/>",
        ["decision"] = "<path d=\"M8 2.2 13.8 8 8 13.8 2.2 8Z\"/>",
        ["success"] = "<circle cx=\"8\" cy=\"8\" r=\"5.3\"/>" +
            I12 + "<path d=\"m5.2 8 1.8 1.8 3.8-4\"/>",
        ["failure"] = "<circle cx=\"8\" cy=\"8\" r=\"5.3\"/>" +
            I12 + "<path d=\"m5.7 5.7 4.6 4.6m0-4.6-4.6 4.6\"/>",
        ["stop"] = "<circle cx=\"8\" cy=\"8\" r=\"5.3\"/>" +
            I12 + "<rect x=\"6.1\" y=\"6.1\" width=\"3.8\" height=\"3.8\" rx=\".5\" class=\"sigil-fill\"/>",
        ["neutral"] = "<rect x=\"3\" y=\"3\" width=\"10\" height=\"10\" rx=\"2\"/>" +
            I12 + "<circle cx=\"8\" cy=\"8\" r=\"1.2\" class=\"sigil-fill\"/>",
    };

    // A quiet, renderer-owned corner symbol (type default or authored icon). It is
    // SVG content rather than a
    // viewer overlay, so it survives canonical export while adding no focus target,
    // accessible name, layout box, or interaction state of its own.
    // Shared with label clearance so the reserved rail matches the actual icon.
    public const int SemanticSigilInset = 6;
    public const int SemanticSigilSize = 11;
    public const int SemanticSigilFootprint = SemanticSigilInset + SemanticSigilSize;
    // The viewer installs a runtime "sources" beacon on the node's top-right
    // rail, just left of the brand mark. Layout must reserve the same footprint
    // so labels never sit under the badge.
    public const int SourceBadgeFootprint = 38;

    public static string RenderSemanticSigil(string kind, double x, double y, double size = SemanticSigilSize, string? icon = null)
    {
        if (icon == "none") return "";
        var selected = icon ?? kind;
        var normalized = SigilShape.ContainsKey(selected) ? selected : "neutral";
        var tone = SigilTone.GetValueOrDefault(kind) ?? "external";
        var scale = size / 16;
        return $"<g aria-hidden=\"true\" data-semantic-sigil=\"{Esc(normalized)}\" class=\"semantic-sigil s-{tone}\" transform=\"translate({Js.Num(x)} {Js.Num(y)}) scale({Js.Num(scale)})\">\n" +
            $"            {SigilShape[normalized]}\n" +
            "          </g>";
    }

    // Cards are schema-validated (common.schema.json: dot, title and string items are required).
    public static string RenderCards(JsonArray? cards)
    {
        var list = cards ?? [];
        var rendered = list.Select(card =>
            "      <div class=\"card\">\n" +
            "        <div class=\"card-header\">\n" +
            $"          <div class=\"card-dot {Esc(card.Str("dot"))}\"></div>\n" +
            $"          <h3>{Esc(card.Str("title"))}</h3>\n" +
            "        </div>\n" +
            "        <ul>\n" +
            string.Join('\n', card.Arr("items")!.Select(item => $"          <li>{Esc(item?.GetValue<string>())}</li>")) + "\n" +
            "        </ul>\n" +
            "      </div>");
        return "    <!-- Info Cards -->\n" +
            "    <div class=\"cards\">\n" +
            string.Join("\n\n", rendered) + "\n" +
            "    </div>";
    }

    // CJK and other wide/fullwidth glyphs render at roughly twice the advance
    // width of ASCII in the monospace stacks the template uses. Keep halfwidth
    // forms (notably U+FF61–U+FF9F Katakana) out of this set. The explicit ranges
    // also cover vertical punctuation and supplementary East Asian scripts that
    // literal glyph ranges made difficult to audit.
    // Code points that take two columns of advance width: East Asian Wide and
    // Fullwidth per UAX #11, tracking Unicode 17.0. That takes in the BMP symbols
    // carrying emoji presentation (U+2705, U+2B50, U+26A1, U+231B, ...), which
    // render at the same square advance as the supplementary-plane emoji already
    // listed here, and Hangul Jamo Extended-A. Two boundary calls worth naming:
    // Unicode 16.0 reclassified the trigrams (U+2630-U+2637) and the monogram /
    // digram symbols (U+268A-U+268F) from Neutral to Wide, so both are in; and
    // Hangul Jamo Extended-A stops at U+A97C, its last assigned jamo, because
    // U+A97D-U+A97F are unassigned, and unassigned code points outside the CJK
    // ranges UAX #11 names default to Neutral rather than Wide.
    // FULLWIDTH_RE (utils.mjs:219) as a sorted range table, copied range for range.
    static readonly (int Lo, int Hi)[] Fullwidth =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2630, 0x2637), (0x2648, 0x2653), (0x267F, 0x267F), (0x268A, 0x268F),
        (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE),
        (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD),
        (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755),
        (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50),
        (0x2B55, 0x2B55), (0x2E80, 0xA4CF), (0xA960, 0xA97C), (0xAC00, 0xD7A3), (0xF900, 0xFAFF), (0xFE10, 0xFE19),
        (0xFE30, 0xFE6F), (0xFF01, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x18DFF), (0x1AFF0, 0x1AFFF), (0x1B000, 0x1B2FF),
        (0x1F000, 0x1FAFF), (0x20000, 0x3FFFD),
    ];

    static bool IsFullwidth(int cp)
    {
        foreach (var (lo, hi) in Fullwidth)
        {
            if (cp < lo) return false;
            if (cp <= hi) return true;
        }
        return false;
    }

    // Variation selectors contribute no separate unit. This is a conservative
    // width estimate, not a measurement of the selected glyph: its actual advance
    // depends on the font and presentation (Unicode UAX #11).
    // VS16 requests emoji presentation, so reserve two units for the sequence.
    // VS15 retains the base's width estimate; forcing every text-presentation
    // sequence to one unit undercounts wide bases, including CJK characters whose
    // font ignores that selector. Neutral bases remain one unit. Some selected
    // text glyphs can be narrower than this estimate; prefer extra space to overflow.
    const int VariationSelectorFirst = 0xfe00;
    const int VariationSelectorLast = 0xfe0f;
    const int VariationSelectorEmoji = 0xfe0f;

    // ponytail: upstream memoizes by input string; the count is pure and cheap, so no cache. `ctx` is where a
    // per-render cache would live if profiling ever asks for one.
    public static int TextUnits(string? text, RenderContext? ctx = null)
    {
        var s = text ?? "";
        // Array.from(string): code points, with a lone surrogate kept as its own element.
        var chars = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { chars.Add(char.ConvertToUtf32(s[i], s[i + 1])); i++; }
            else chars.Add(s[i]);
        }
        var units = 0;
        for (var i = 0; i < chars.Count; i++)
        {
            var codePoint = chars[i];
            if (codePoint is >= VariationSelectorFirst and <= VariationSelectorLast) continue;
            var next = i + 1 < chars.Count ? chars[i + 1] : -1;
            if (next == VariationSelectorEmoji) units += 2;
            else units += IsFullwidth(codePoint) ? 2 : 1;
        }
        return units;
    }
}
