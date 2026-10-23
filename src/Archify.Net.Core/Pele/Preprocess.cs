using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// preprocess.ts Preprocessed. `Config` is the shallow `{ [key]: YamlValue }` record; JS object key order is not kept
// (config is only ever read by key on the chart path; sankey's nodeColors goes into a lookup Map).
internal sealed record Preprocessed(string Text, string? Title, JsonObject Config, string WithComments, int LineOffset);

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

        public static string CleanupComments(string text) =>
            Js.TrimStart(text.Contains("%%", StringComparison.Ordinal) ? ReComment.Replace(text, "") : text);

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
        static string Directives(string text, JsonObject config)
        {
            if (!text.Contains("%%{", StringComparison.Ordinal)) return text;
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
            return ReDirective.Replace(text, "");
        }

        public static Preprocessed Run(string source)
        {
            var text = source.Contains('\r') ? source.Replace("\r\n", "\n").Replace('\r', '\n') : source;
            // A tag needs a closing bracket, so nothing after the last one can match.
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
            }

            text = Directives(text, config);
            return new(CleanupComments(text), title, config, text, lineOffset);
        }

        // preprocess.ts:149-162: parseYaml(body), its errors wrapped as "Front matter is not valid YAML. …", then `title`,
        // `config` and `displayMode`. util/yaml.ts is ported in PORT-008 Task 4.
        static string? FrontMatterYaml(string body, JsonObject config) =>
            throw new NotImplementedException("Front-matter YAML lands in PORT-008 Task 4.");

        // preprocess.ts:171-219.
        static readonly Regex ReEntity = new($"#{W}+;");
        static readonly Regex ReInt = new(@"\A\+?[0-9]+\z");

        // Does what Mermaid's `text.replace(/keyword.*:\S*#.*;/g, drop the last character)` does (preprocess.ts:175-211).
        static string DropColorSemicolons(string text, string keyword)
        {
            var from = text.IndexOf(keyword, StringComparison.Ordinal);
            if (from == -1) return text;
            var output = new StringBuilder();
            var last = 0;
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
                    if (semi > hash)
                    {
                        output.Append(text, last, semi - last);
                        last = semi + 1;
                    }
                }
                from = lineEnd >= text.Length ? -1 : Js.IndexOf(text, keyword, lineEnd + 1);
            }
            return last == 0 ? text : output.Append(text, last, text.Length - last).ToString();
        }

        // Mermaid hides `#name;` entity codes behind placeholder characters so `#` and `;` survive parsing.
        public static string EncodeEntities(string text)
        {
            if (!text.Contains('#')) return text;
            return ReEntity.Replace(DropColorSemicolons(DropColorSemicolons(text, "style"), "classDef"), m =>
            {
                var inner = m.Value[1..^1];
                return (ReInt.IsMatch(inner) ? "ﬂ°°" : "ﬂ°") + inner + "¶ß";
            });
        }
    }
}
