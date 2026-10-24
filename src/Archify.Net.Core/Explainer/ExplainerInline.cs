using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// parseInline's options (inline.mjs:33-41): the source location its diagnostics report.
internal sealed record InlineOptions(double? Line = null, string? Component = null, string? PanelId = null, string? Input = null);

// renderInline's context, what render.mjs:49 passes. Footnotes and Citations keep first-use numbering across calls; JS creates
// `context.citations` on first use, so a fresh context starts with an empty map.
internal sealed class InlineContext
{
    public JsonObject? Document { get; init; }
    public JsonObject? Sources { get; init; }
    public JsonObject? Diagrams { get; init; }
    public string? PanelId { get; init; }
    public string? BlockId { get; init; }
    public JsonArray? Footnotes { get; init; }
    public JsMap<string, int> Citations { get; init; } = new();
}

// parseInline's result: tokens, the references they hold (each link token's `reference` is a copy of its entry), diagnostics.
internal sealed record InlineParse(JsonArray Tokens, JsonArray References, List<Diagnostic> Diagnostics)
{
    public JsonObject ToJson() => new()
    {
        ["tokens"] = Tokens.DeepClone(), ["references"] = References.DeepClone(),
        ["diagnostics"] = new JsonArray([.. Diagnostics.Select(d => (JsonNode?)d.ToJson())]),
    };
}

// archify 3.2.0 explainer/inline.mjs: the explainer's deliberately small inline language; never an HTML escape hatch.
internal static class ExplainerInline
{
    public static string EscapeHtml(string? value) => Js.EscapeHtml(value);

    // /^[\p{L}\p{N}_-]+$/u and /^[\p{L}\p{N}_.:-]+$/u, per code point (a lone surrogate matches neither).
    static bool Identifier(string value, bool target = false)
    {
        if (value.Length == 0) return false;
        for (var i = 0; i < value.Length; i++)
        {
            int cp = value[i];
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) cp = char.ConvertToUtf32(value[i], value[++i]);
            if (!(Js.IsLetter(cp) || Js.IsNumber(cp) || cp is '_' or '-' || (target && cp is '.' or ':'))) return false;
        }
        return true;
    }

    // `new URL(value)` succeeds, the protocol is http: or https:, there is no username or password, and the value holds no
    // C0 control, space or DEL (inline.mjs:11, validate.mjs:111). A special URL's path, query and fragment never fail to
    // parse, so only the authority is parsed (RepositoryLocation.Authority, WHATWG); slashes and backslashes after the
    // scheme are skipped as the special-authority-ignore-slashes state does.
    internal static bool CredentialFreeHttpUrl(string value)
    {
        if (value.Any(c => c <= ' ' || c == '\u007f')) return false;
        var colon = value.IndexOf(':');
        if (colon < 0 || !(value[..colon].Equals("http", StringComparison.OrdinalIgnoreCase) || value[..colon].Equals("https", StringComparison.OrdinalIgnoreCase))) return false;
        var rest = value[(colon + 1)..].TrimStart('/', '\\');
        var end = rest.IndexOfAny(['/', '\\', '?', '#']);
        return RepositoryLocation.Authority(value[..colon], end < 0 ? rest : rest[..end]) is { } url && url.Username.Length == 0 && url.Password.Length == 0;
    }

    static readonly Regex Http = new("^[Hh][Tt][Tt][Pp][Ss]?://");

    // referenceFor (inline.mjs:7-30). The key order of each object is upstream's.
    static JsonObject? ReferenceFor(string href)
    {
        if (Http.IsMatch(href)) return CredentialFreeHttpUrl(href) ? new JsonObject { ["kind"] = "web", ["url"] = href } : null;
        if (href.StartsWith("source:", StringComparison.Ordinal) && Identifier(href[7..])) return new JsonObject { ["kind"] = "source", ["id"] = href[7..] };
        if (href.StartsWith("block:", StringComparison.Ordinal) && Identifier(href[6..])) return new JsonObject { ["kind"] = "block", ["id"] = href[6..] };
        if (href.StartsWith('#') && Identifier(href[1..])) return new JsonObject { ["kind"] = "block", ["id"] = href[1..] };
        if (href.StartsWith("typical:", StringComparison.Ordinal) && Js.Trim(href[8..]).Length > 0) return new JsonObject { ["kind"] = "typical", ["text"] = Js.Trim(href[8..]) };
        if (href.StartsWith("node:", StringComparison.Ordinal) || href.StartsWith("route:", StringComparison.Ordinal))
        {
            var kind = href.StartsWith("node:", StringComparison.Ordinal) ? "node" : "route";
            var value = href[(kind.Length + 1)..];
            var split = value.IndexOf('.');
            if (split < 1 || !Identifier(value[..split])) return null;
            var (diagram, target) = (value[..split], value[(split + 1)..]);
            if (kind == "node" && Identifier(target, true)) return new JsonObject { ["kind"] = kind, ["diagram"] = diagram, ["node"] = target };
            var route = target.Split('~');
            if (kind == "route" && route.Length == 2 && route.All(id => Identifier(id, true)))
                return new JsonObject { ["kind"] = kind, ["diagram"] = diagram, ["from"] = route[0], ["to"] = route[1] };
        }
        return null;
    }

    const string Escapable = "\\`*_[]()!<>~|";
    static readonly Regex HtmlStart = new("^(?:</?[A-Za-z][^>]*>|<!--)");
    static readonly Regex Unescape = new(@"\\([()\\])");

    /// <summary>parseInline (inline.mjs:33-115).</summary>
    public static InlineParse Parse(string text, InlineOptions o)
    {
        var references = new JsonArray();
        var diagnostics = new List<Diagnostic>();
        void Problem(string code, int index, string reason, string example)
        {
            var before = text[..index];
            var lastBreak = before.LastIndexOf('\n');
            var line = o.Line is { } l && double.IsFinite(l) && l == Math.Floor(l) ? l : 1;
            var subject = new JsonObject
            {
                ["line"] = line + before.Count(c => c == '\n'), ["column"] = index - lastBreak,
                ["component"] = string.IsNullOrEmpty(o.Component) ? "inline" : o.Component,
            };
            if (!string.IsNullOrEmpty(o.PanelId)) subject["panelId"] = o.PanelId;
            if (!string.IsNullOrEmpty(o.Input)) subject["input"] = o.Input;
            diagnostics.Add(new($"explainer/inline-{code}", Severity.Error, reason, subject,
                new JsonObject { ["reason"] = reason, ["example"] = example }, [$"Use the supported form: {example}"], null));
        }
        JsonArray Segment(int start, int end, int depth = 0)
        {
            var tokens = new JsonArray();
            void Literal(string value)
            {
                if (value.Length == 0) return;
                if (tokens.Count > 0 && tokens[^1] is JsonObject previous && previous.Str("type") == "text") previous["value"] = previous.Str("value") + value;
                else tokens.Add(new JsonObject { ["type"] = "text", ["value"] = value });
            }
            for (var i = start; i < end;)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < end && Escapable.Contains(text[i + 1])) { Literal(text[i + 1].ToString()); i += 2; continue; }
                if (c == '`')
                {
                    var close = text.IndexOf('`', i + 1);
                    if (close == -1 || close >= end || text[(i + 1)..close].Contains('\n'))
                    {
                        Problem("code", i, "Inline code must have one closing backtick on the same line.", "`cache.get(key)`"); Literal("`"); i++; continue;
                    }
                    tokens.Add(new JsonObject { ["type"] = "code", ["value"] = text[(i + 1)..close] }); i = close + 1; continue;
                }
                if (c == '<' && HtmlStart.IsMatch(text[i..end]))
                {
                    Problem("html", i, "Raw HTML is not part of the explainer inline language.", "**Important** or `[literal <tag>]`");
                    Literal("<"); i++; continue;
                }
                if (string.CompareOrdinal(text, i, "![", 0, 2) == 0)
                {
                    Problem("image", i, "Inline Markdown images are unsupported. Use a contained image block.", "@image path=diagram.png alt=\"Request path\"");
                    Literal("!"); i++; continue;
                }
                if (string.CompareOrdinal(text, i, "~~", 0, 2) == 0)
                {
                    Problem("decoration", i, "Strikethrough is unsupported; use a diff or an explicit change block.", "Changed: **new behavior**"); Literal("~~"); i += 2; continue;
                }
                if (c == '*')
                {
                    var marker = string.CompareOrdinal(text, i, "**", 0, 2) == 0 ? "**" : "*";
                    var close = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
                    bool Escaped(int index)
                    {
                        var slashes = 0;
                        while (index > start && text[--index] == '\\') slashes++;
                        return slashes % 2 == 1;
                    }
                    while (close > -1 && Escaped(close)) close = Js.IndexOf(text, marker, close + marker.Length);
                    // ponytail: depth >= 6 is unreachable (an em holds no unescaped `*`, so a strong holds at most an em), kept as upstream.
                    if (close < 0 || close >= end || close == i + marker.Length || depth >= 6)
                    {
                        Problem("emphasis", i, "Emphasis needs matching nonempty delimiters. Escape a literal asterisk.", "**important** or \\*"); Literal(marker); i += marker.Length; continue;
                    }
                    tokens.Add(new JsonObject { ["type"] = marker.Length == 2 ? "strong" : "em", ["children"] = Segment(i + marker.Length, close, depth + 1) });
                    i = close + marker.Length; continue;
                }
                if (c == '[')
                {
                    var labelEnd = text.IndexOf(']', i + 1);
                    if (labelEnd > -1 && labelEnd < end && labelEnd + 1 < text.Length && text[labelEnd + 1] == '(')
                    {
                        int close = labelEnd + 2, nesting = 1;
                        for (; close < end; close++)
                        {
                            if (text[close] == '\\') { close++; continue; }
                            if (text[close] == '(') nesting++;
                            if (text[close] == ')' && --nesting == 0) break;
                        }
                        if (close >= end || nesting != 0)
                        {
                            Problem("link", i, "The link destination is not closed.", "[Evidence](source:cache-read)"); Literal("["); i++; continue;
                        }
                        var label = text[(i + 1)..labelEnd];
                        var href = Unescape.Replace(text[(labelEnd + 2)..close], "$1");
                        var reference = ReferenceFor(href);
                        if ((Js.Trim(label).Length == 0 && reference?.Str("kind") != "source") || reference is null)
                        {
                            Problem("target", i, "Use a nonempty label and an HTTP(S), source, block, node, route or typical destination.", "[Redis](node:read-path.redis)");
                            Literal(text[i..(close + 1)]);
                        }
                        else
                        {
                            reference["offset"] = i;
                            reference["end"] = close + 1;
                            references.Add(reference);
                            tokens.Add(new JsonObject { ["type"] = "link", ["label"] = label, ["href"] = href, ["reference"] = reference.DeepClone() });
                        }
                        i = close + 1; continue;
                    }
                    if (i + 1 < text.Length && text[i + 1] == '^')
                        Problem("footnote", i, "Arbitrary Markdown footnotes are unsupported. Use a source or typical-value reference.", "[Claim](source:trace)");
                }
                Literal(c.ToString()); i++;
            }
            return tokens;
        }
        return new(Segment(0, text.Length), references, diagnostics);
    }

    /// <summary>renderInline (inline.mjs:125-155): the same parsed language; any diagnostic throws (inlineError, :117-122).</summary>
    public static string Render(string text, InlineContext ctx)
    {
        var parsed = Parse(text, new InlineOptions(PanelId: ctx.PanelId));
        if (parsed.Diagnostics.Count > 0) throw new ArchifyDiagnosticException(parsed.Diagnostics[0].Message, parsed.Diagnostics);
        string Html(JsonArray tokens)
        {
            var sb = new StringBuilder();
            foreach (var token in tokens.Cast<JsonObject>())
            {
                var type = token.Str("type");
                if (type == "text") { sb.Append(EscapeHtml(token.Str("value"))); continue; }
                if (type == "code") { sb.Append($"<code>{EscapeHtml(token.Str("value"))}</code>"); continue; }
                if (type is "strong" or "em") { sb.Append($"<{type}>{Html(token["children"]!.AsArray())}</{type}>"); continue; }
                var label = EscapeHtml(token.Str("label"));
                var reference = token["reference"]!.AsObject();
                switch (reference.Str("kind"))
                {
                    case "web":
                        sb.Append($"<a href=\"{EscapeHtml(reference.Str("url"))}\" target=\"_blank\" rel=\"noopener noreferrer\">{label}</a>"); break;
                    case "source":
                        var id = reference.Str("id")!;
                        if (!ctx.Citations.Has(id)) ctx.Citations.Set(id, ctx.Citations.Count + 1);
                        sb.Append($"{label}<sup class=\"cite\"><a href=\"#source-{EscapeHtml(id)}\">{ctx.Citations.Get(id)}</a></sup>"); break;
                    case "block":
                        sb.Append($"<a href=\"#{EscapeHtml(reference.Str("id"))}\" data-block-ref=\"{EscapeHtml(reference.Str("id"))}\">{label}</a>"); break;
                    case "node" or "route":
                        var hash = reference.Str("kind") == "node" ? $"focus={Js.EncodeUriComponent(reference.Str("node")!)}"
                            : $"route={Js.EncodeUriComponent(reference.Str("from")!)}~{Js.EncodeUriComponent(reference.Str("to")!)}";
                        var diagram = EscapeHtml(reference.Str("diagram"));
                        sb.Append($"<a class=\"diagram-link\" href=\"#{diagram}\" data-diagram-link=\"{diagram}\" data-diagram-hash=\"{EscapeHtml(hash)}\">{label}</a>"); break;
                    case "typical":
                        var note = reference.Str("text");
                        if (ctx.Footnotes is { } footnotes)
                        {
                            var index = footnotes.Count + 1;
                            var footnote = $"explainer-typical-{index}";
                            footnotes.Add(new JsonObject { ["id"] = footnote, ["index"] = index, ["text"] = note, ["label"] = token.Str("label") });
                            sb.Append($"<span class=\"typical\" title=\"{EscapeHtml(note)}\">{label}<sup><a href=\"#{footnote}\" role=\"doc-noteref\" class=\"typical-ref\">[{index}]</a></sup></span>");
                        }
                        else sb.Append($"<span class=\"typical\">{label}<small class=\"typical-note\"> ({EscapeHtml(note)})</small></span>");
                        break;
                    default: sb.Append(label); break;
                }
            }
            return sb.ToString();
        }
        return Html(parsed.Tokens);
    }
}
