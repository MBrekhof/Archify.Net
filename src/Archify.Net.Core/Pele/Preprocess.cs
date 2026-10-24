using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// preprocess.ts Preprocessed. `Config` is the shallow `{ [key]: YamlValue }` record; JS object key order is not kept
// (config is only ever read by key on the chart path; sankey's nodeColors goes into a lookup Map).
// `Origin[i]` is the offset in the source of `Text[i]` (see SourceMap).
internal sealed record Preprocessed(string Text, string? Title, JsonObject Config, string WithComments, int LineOffset, int[] Origin);

// Deviation (PORT-008 Task 11, UPSTREAM-007: mermaid#8537, pele#61; Martin 2026-10-23): upstream counts an error's line in
// the text the diagram parsed, so every line that preparation removes before the error (front matter, `%%` comment lines,
// `%%{...}%%` directives, leading blank lines, sankey's blank lines, and archify's pie legend before Pele) moves the number
// up. The port reports the line as written in the fence. `origin[i]` is the source offset of the prepared text's
// character i: a kept character's own, a replaced run's first (no replacement holds a line break but CRLF's, which takes
// the CR's). An error past the last character (EOF, and the "\n" Pele.Render appends) is on the last content line: the
// line of the last kept source character that is not blank (controller, fix round 1), never a blank or comment line after
// it or a line past the fence's end. Lines are counted as Preprocess.Run normalises them: `\r\n`, `\n` and a lone `\r`
// each end one, and line 1 is the fence's first.
internal sealed class SourceMap(string source, int[] origin)
{
    public int Offset(int at)
    {
        if (at < origin.Length) return origin[at];
        for (var k = origin.Length - 1; k >= 0; k--) if (!Js.IsSpace(source[origin[k]])) return origin[k];
        return 0;
    }

    // A text prepared from this one, where inner[i] is the offset here of its character i (sankey's own preparation).
    public SourceMap Then(int[] inner) => new(source, Array.ConvertAll(inner, Offset));

    // 1 + the line breaks that start before the offset (a CRLF maps to its CR, so it counts once).
    public int Line(int at)
    {
        var end = Math.Min(Offset(at), source.Length);
        var line = 1;
        for (var i = 0; i < end; i++)
        {
            if (source[i] == '\r' && i + 1 < source.Length && source[i + 1] == '\n') i++;
            if (source[i] is '\n' or '\r') line++;
        }
        return line;
    }
}

internal static partial class Pele
{
    internal static class Preprocess
    {
        // ponytail: JSON.parse nests without limit and V8 merged 20,000 levels; the C# parse recurses (DiagramLoader.ParseNode),
        // so a directive nested deeper than this is ignored as invalid JSON (deviation, PORT-008 Task 11; the same cap as
        // ExplainerCharts' data files). merge() recurses no deeper than the JSON it is given.
        internal const int JsonDepth = 256;

        const string S = Chars.Ws;
        const string W = Chars.Word;
        // preprocess.ts:16 /^\s*%%(?!{)[^\n]+\n?/gm: `^` with `m` is also after \r, U+2028 and U+2029 (review M6).
        static readonly Regex ReComment = new($@"(?<=\A|[\n\r\u2028\u2029]){S}*%%(?!\{{)[^\n]+\n?");
        // :17 /%{2}{\s*(?:(\w+)\s*:|(\w+))\s*(?:(\w+)|((?:(?!}%{2}).|\r?\n)*))?\s*(?:}%{2})?/gi. The `i` flag changes nothing:
        // the only letters are in \w, which is closed under case.
        static readonly Regex ReDirective = new($@"%{{2}}\{{{S}*(?:({W}+){S}*:|({W}+)){S}*(?:({W}+)|((?:(?!\}}%{{2}}){Chars.Dot}|\r?\n)*))?{S}*(?:\}}%{{2}})?");
        static readonly Regex ReHtmlTag = new($@"<({W}+)([^>]*)>");
        static readonly Regex ReAttr = new("=\"([^\"]*)\"");
        static readonly HashSet<string> UnsafeKeys = new(StringComparer.Ordinal) { "__proto__", "constructor", "prototype" };

        // CleanupComments (preprocess.ts:22-24), with the origins of what is left.
        static (string, int[]) CleanupComments(string text, int[] origin)
        {
            if (text.Contains("%%", StringComparison.Ordinal)) (text, origin) = Replace(text, origin, Removed(ReComment.Matches(text)));
            var trimmed = Js.TrimStart(text);
            return (trimmed, origin[(text.Length - trimmed.Length)..]);
        }

        static IEnumerable<(int At, int Length, string With)> Removed(MatchCollection matches) => matches.Select(m => (m.Index, m.Length, ""));

        // The text with each edit made (in order, not overlapping), and the source offset of each character left (SourceMap): a
        // replacement as long as its match keeps its characters' origins, any other takes its match's first. Regex.Replace
        // over the same matches gives the same text.
        internal static (string Text, int[] Origin) Replace(string text, int[] origin, IEnumerable<(int At, int Length, string With)> edits)
        {
            var output = new StringBuilder(text.Length);
            var origins = new List<int>(text.Length);
            var last = 0;
            var edited = false;
            foreach (var (at, length, with) in edits)
            {
                edited = true;
                output.Append(text, last, at - last).Append(with);
                origins.AddRange(origin.AsSpan(last, at - last));
                if (with.Length == length) origins.AddRange(origin.AsSpan(at, length));
                else for (var k = 0; k < with.Length; k++) origins.Add(origin[at]);
                last = at + length;
            }
            if (!edited) return (text, origin);
            output.Append(text, last, text.Length - last);
            origins.AddRange(origin.AsSpan(last));
            return (output.ToString(), [.. origins]);
        }

        static bool IsLineBreak(int ch) => ch is '\n' or '\r';

        sealed record FrontMatter(string Indent, string Body, int Length);

        // preprocess.ts:41-84: a `---` fenced YAML block at the start of the text, the way Mermaid's front matter regex finds it.
        static FrontMatter? MatchFrontMatter(string text)
        {
            int At(int i) => Chars.CharAt(text, i);
            var cursor = 0;
            while (cursor < text.Length && Chars.IsSpace(At(cursor)) && !IsLineBreak(At(cursor))) cursor++;
            var indent = text[..cursor];
            if (string.CompareOrdinal(text, cursor, "---", 0, 3) != 0) return null;

            var openingEnds = new List<int>();
            for (var i = cursor + 3; i < text.Length && Chars.IsSpace(At(i)); i++)
                if (IsLineBreak(At(i))) openingEnds.Add(i);
            if (openingEnds.Count == 0) return null;

            var fence = indent + "---";
            int ClosingEnd(int i)
            {
                if (!IsLineBreak(At(i)) || string.CompareOrdinal(text, i + 1, fence, 0, fence.Length) != 0) return -1;
                var last = -1;
                for (var j = i + 1 + fence.Length; j < text.Length && Chars.IsSpace(At(j)); j++)
                    if (IsLineBreak(At(j))) last = j;
                return last;
            }

            var needle = "\n" + fence;
            var lastFence = -1;
            for (var i = Js.LastIndexOf(text, needle, text.Length); i > openingEnds[0]; i = Js.LastIndexOf(text, needle, i - 1))
            {
                if (ClosingEnd(i) != -1) { lastFence = i; break; }
            }
            if (lastFence == -1) return null;

            var openingEnd = openingEnds[0];
            foreach (var candidate in openingEnds) if (candidate < lastFence) openingEnd = candidate;

            var bodyStart = openingEnd + 1;
            var firstAt = bodyStart > 0 ? bodyStart - 1 : 0;
            for (var i = Js.IndexOf(text, needle, firstAt); i != -1 && i <= lastFence; i = Js.IndexOf(text, needle, i + 1))
            {
                if (i < bodyStart) continue;
                var end = ClosingEnd(i);
                if (end != -1) return new(indent, text[bodyStart..i], end + 1);
            }
            return null;
        }

        static bool IsRecord(JsonNode? value) => value is JsonObject;

        // preprocess.ts:90-100. A value is moved into `target` as a copy (a JsonNode has one parent).
        internal static JsonObject Merge(JsonObject target, JsonObject source)
        {
            foreach (var (key, value) in source)
            {
                if (UnsafeKeys.Contains(key)) continue;
                var current = target[key];
                if (value is JsonObject v && current is JsonObject c) Merge(c, v);
                else if (value is JsonObject r) target[key] = Merge(new JsonObject(), r);
                else target[key] = value?.DeepClone();
            }
            return target;
        }

        // preprocess.ts:102-123: matches are read from the trimmed, quote-normalised text, but removed from the original.
        static (string, int[]) Directives(string text, int[] origin, JsonObject config)
        {
            if (!text.Contains("%%{", StringComparison.Ordinal)) return (text, origin);
            var normalized = Js.Trim(text).Replace('\'', '"');
            for (var match = ReDirective.Match(normalized); match.Success; match = match.NextMatch())
            {
                var g = match.Groups;
                var type = g[1].Success ? g[1].Value : g[2].Success ? g[2].Value : null;
                if (type == "wrap")
                {
                    config["wrap"] = true;
                }
                else if (type is "init" or "initialize" && g[4].Success && g[4].Value.Length > 0)
                {
                    JsonNode? args;
                    try { args = DiagramLoader.ParseNode(Js.Trim(g[4].Value), JsonDepth); }
                    catch (JsonException) { continue; }   // Mermaid ignores an init directive whose body is not valid JSON.
                    if (args is JsonObject record) Merge(config, record);
                }
            }
            return Replace(text, origin, Removed(ReDirective.Matches(text)));
        }

        // `\r\n` and a lone `\r` become `\n`; the `\n` of a CRLF takes the CR's origin.
        static IEnumerable<(int At, int Length, string With)> NormalizedBreaks(string text)
        {
            for (var i = text.IndexOf('\r'); i != -1; i = text.IndexOf('\r', i + 1))
            {
                var crlf = i + 1 < text.Length && text[i + 1] == '\n';
                yield return (i, crlf ? 2 : 1, "\n");
                if (crlf) i++;
            }
        }

        internal static int[] Identity(int length)
        {
            var origin = new int[length];
            for (var i = 0; i < length; i++) origin[i] = i;
            return origin;
        }

        public static Preprocessed Run(string source)
        {
            var origin = Identity(source.Length);
            var text = source;
            if (source.Contains('\r')) (text, origin) = Replace(source, origin, NormalizedBreaks(source));
            // A tag needs a closing bracket, so nothing after the last one can match. The attribute quoting is as long as
            // what it replaces (`="x"` -> `='x'`), so every character keeps its origin.
            var lastClose = text.Contains('<') ? text.LastIndexOf('>') : -1;
            if (lastClose > 0)
            {
                text = ReHtmlTag.Replace(text[..(lastClose + 1)], m => "<" + m.Groups[1].Value + ReAttr.Replace(m.Groups[2].Value, a => "='" + a.Groups[1].Value + "'") + ">")
                    + text[(lastClose + 1)..];
            }

            var config = new JsonObject();
            string? title = null;
            var lineOffset = 0;

            var front = text.Contains("---", StringComparison.Ordinal) ? MatchFrontMatter(text) : null;
            if (front is not null)
            {
                var body = front.Indent.Length > 0
                    ? string.Join("\n", front.Body.Split('\n').Select(line => line.StartsWith(front.Indent, StringComparison.Ordinal) ? line[front.Indent.Length..] : line))
                    : front.Body;
                title = FrontMatterYaml(body, config);
                for (var i = text.IndexOf('\n'); i != -1 && i < front.Length; i = Js.IndexOf(text, "\n", i + 1)) lineOffset++;
                text = text[front.Length..];
                origin = origin[front.Length..];
            }

            (text, origin) = Directives(text, origin, config);
            var (clean, cleanOrigin) = CleanupComments(text, origin);
            return new(clean, title, config, text, lineOffset, cleanOrigin);
        }

        // preprocess.ts:149-162: parseYaml(body), its errors wrapped as "Front matter is not valid YAML. …", then `title`
        // (JS truthiness, then String()), `config` and `displayMode`.
        static string? FrontMatterYaml(string body, JsonObject config)
        {
            JsonNode? parsed;
            try { parsed = Yaml.Parse(body); }
            catch (Yaml.YamlError error) { throw new PeleException($"Front matter is not valid YAML. {error.Message}", "syntax"); }
            if (parsed is not JsonObject front) return null;
            var title = front["title"].Truthy() ? Js.String(front["title"]) : null;
            if (front["config"] is JsonObject frontConfig) Merge(config, frontConfig);
            if (front["displayMode"].Truthy())
            {
                if (config["gantt"] is not JsonObject gantt) config["gantt"] = gantt = new JsonObject();
                gantt["displayMode"] = Js.String(front["displayMode"]);
            }
            return title;
        }

        // preprocess.ts:171-219.
        static readonly Regex ReEntity = new($"#{W}+;");
        static readonly Regex ReInt = new(@"\A\+?[0-9]+\z");

        // Does what Mermaid's `text.replace(/keyword.*:\S*#.*;/g, drop the last character)` does (preprocess.ts:175-211).
        // The semicolons it drops, as edits (at most one per line, in order).
        static IEnumerable<(int At, int Length, string With)> ColorSemicolons(string text, string keyword)
        {
            var from = text.IndexOf(keyword, StringComparison.Ordinal);
            while (from != -1)
            {
                var lineEnd = text.IndexOfAny(['\n', '\r', '\u2028', '\u2029'], from);
                if (lineEnd == -1) lineEnd = text.Length;
                var afterColon = false;
                var hash = -1;
                for (var j = from + keyword.Length; j < lineEnd; j++)
                {
                    var c = text[j];
                    if (c == ':') afterColon = true;
                    else if (c == '#') { if (afterColon) { hash = j; break; } }
                    else if (Chars.IsSpace(c)) afterColon = false;
                }
                if (hash != -1)
                {
                    var semi = Js.LastIndexOf(text, ";", lineEnd - 1);
                    if (semi > hash) yield return (semi, 1, "");
                }
                from = lineEnd >= text.Length ? -1 : Js.IndexOf(text, keyword, lineEnd + 1);
            }
        }

        // Mermaid hides `#name;` entity codes behind placeholder characters so `#` and `;` survive parsing.
        public static string EncodeEntities(string text) => EncodeEntities(text, new int[text.Length]).Text;

        public static (string Text, int[] Origin) EncodeEntities(string text, int[] origin)
        {
            if (!text.Contains('#')) return (text, origin);
            (text, origin) = Replace(text, origin, ColorSemicolons(text, "style"));
            (text, origin) = Replace(text, origin, ColorSemicolons(text, "classDef"));
            return Replace(text, origin, ReEntity.Matches(text).Select(m =>
            {
                var inner = m.Value[1..^1];
                return (m.Index, m.Length, (ReInt.IsMatch(inner) ? "ﬂ°°" : "ﬂ°") + inner + "¶ß");
            }));
        }
    }
}
