using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    // util/yaml.ts: a small YAML reader covering what Mermaid feeds to js-yaml with the JSON schema: block and flow
    // collections, quoted and plain scalars, and block scalars. A value is a JsonNode: null, bool, a double (Infinity, NaN
    // and -0 kept), string, JsonArray or JsonObject.
    internal static class Yaml
    {
        // util/yaml.ts `new Error(...)`, and V8's RangeError from String.fromCodePoint: preprocess.ts:152-153 reads only the
        // message of whatever parseYaml throws.
        internal class YamlError(string message) : Exception(message);

        // Thrown when a quoted scalar or flow collection runs past the text read so far. Upstream it is not an Error; out of
        // parseYaml it is turned into one with the same message, which is all preprocess reads.
        sealed class Incomplete(string message) : YamlError(message);

        const int MaxDepth = 64;

        static int At(string s, int i) => Chars.CharAt(s, i);

        // JS `slice(from, to)` for 0 <= from <= to: clamped to the string.
        static string Slice(string s, int from, int to) => from >= s.Length ? "" : s[from..Math.Min(to, s.Length)];

        // assign(): `__proto__` becomes an own key; a JsonObject has no prototype to dodge.
        static void Assign(JsonObject target, string key, JsonNode? value) => target[key] = value;

        sealed class Reader(string src)
        {
            public readonly string[] Lines = src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            public int I;

            public void SkipBlank()
            {
                while (I < Lines.Length)
                {
                    var t = Js.Trim(Lines[I]);
                    if (t != "" && t[0] != '#' && t != "---") break;
                    I++;
                }
            }

            public int Indent()
            {
                var line = Lines[I];
                var k = 0;
                while (k < line.Length && line[k] == ' ') k++;
                if (At(line, k) == '\t') throw new YamlError("YAML: tabs are not allowed for indentation");
                return k;
            }
        }

        // yaml.ts:47-52. No `m` flag, so `$` is the end of the input: \z.
        static readonly Regex ReInt = new(@"\A[-+]?(?:0|[1-9][0-9]*)\z");
        static readonly Regex ReHex = new(@"\A[-+]?0x[0-9a-fA-F]+\z");
        static readonly Regex ReOct = new(@"\A[-+]?0o[0-7]+\z");
        static readonly Regex ReBin = new(@"\A[-+]?0b[01]+\z");
        static readonly Regex ReFloat = new(@"\A[-+]?(?:[0-9][0-9]*(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?\z");
        static readonly Regex ReInf = new(@"\A[-+]?\.(?:inf|Inf|INF)\z");
        static readonly Regex ReNan = new(@"\A\.(?:nan|NaN|NAN)\z");
        static readonly Regex ReBreaks = new("\n+");
        static readonly Regex ReEscapedBreak = new("\\\\\n[ \t]*");
        // yaml.ts:332 /^[|>][-+0-9]*\s*(?:#.*)?$/ with JS \s and `.`.
        static readonly Regex ReBlockHeader = new($@"\A[|>][-+0-9]*{Chars.Ws}*(?:#{Chars.Dot}*)?\z");

        // RE_KEY_START /[\s#:,[\]{}&*!|>'"%@`]/
        static bool IsKeyStart(int c) => Js.IsSpace(c) || c is '#' or ':' or ',' or '[' or ']' or '{' or '}' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`';

        static bool IsBlank(int ch) => ch is ' ' or '\t' or '\n' or '\r';

        // /[\s,[\]{}]/ (yaml.ts:257)
        static bool IsFlowBreak(int c) => Js.IsSpace(c) || c is ',' or '[' or ']' or '{' or '}';

        // Splits `key: rest` at the start of a line. Returns the raw key and the offset where the value starts.
        static (string Key, int Length)? SplitKey(string line)
        {
            var first = At(line, 0);
            int end, colon;
            if (first is '"' or '\'')
            {
                var i = 1;
                while (true)
                {
                    if (i >= line.Length) return null;
                    var c = line[i];
                    if (first == '"' && c == '\\')
                    {
                        i += 2;
                    }
                    else if (c == first)
                    {
                        if (first == '"' || At(line, i + 1) != '\'') break;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                end = i + 1;
                colon = end;
                while (IsBlank(At(line, colon))) colon++;
                if (At(line, colon) != ':') return null;
            }
            else
            {
                if (first == -1 || IsKeyStart(first)) return null;
                if (first == '-' && (line.Length < 2 || IsBlank(line[1]) || line[1] == ':' || line[1] == '#')) return null;
                colon = 1;
                while (colon < line.Length && line[colon] != ':' && line[colon] != '#') colon++;
                if (At(line, colon) != ':') return null;
                end = colon;
                while (IsBlank(At(line, end - 1))) end--;
            }
            var length = colon + 1;
            if (length < line.Length && !IsBlank(line[length])) return null;
            while (IsBlank(At(line, length))) length++;
            return (line[..end], length);
        }

        static JsonNode? Scalar(string raw)
        {
            var s = Js.Trim(raw);
            if (s is "" or "~" or "null" or "Null" or "NULL") return null;
            if (s is "true" or "True" or "TRUE") return true;
            if (s is "false" or "False" or "FALSE") return false;
            if (ReInt.IsMatch(s) || ReFloat.IsMatch(s)) return JsonValue.Create(Js.ToNumber(s));
            if (ReHex.IsMatch(s) || ReOct.IsMatch(s) || ReBin.IsMatch(s))
            {
                var neg = s[0] == '-';
                var v = Js.ToNumber(neg || s[0] == '+' ? s[1..] : s);
                return JsonValue.Create(neg ? -v : v);
            }
            if (ReInf.IsMatch(s)) return JsonValue.Create(s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity);
            if (ReNan.IsMatch(s)) return JsonValue.Create(double.NaN);
            return s;
        }

        static readonly Dictionary<char, string> Escapes = new()
        {
            ['0'] = "\0", ['a'] = "\u0007", ['b'] = "\b", ['t'] = "\t", ['n'] = "\n", ['v'] = "\v", ['f'] = "\f", ['r'] = "\r",
            ['e'] = "\u001b", [' '] = " ", ['"'] = "\"", ['/'] = "/", ['\\'] = "\\", ['N'] = "\u0085", ['_'] = "\u00a0",
            ['L'] = "\u2028", ['P'] = "\u2029",
        };

        // Folds the line breaks of a scalar: blanks around a break go, one break becomes a space, and each further break stays.
        static string Fold(string text)
        {
            if (!text.Contains('\n')) return text;
            var lines = text.Split('\n');
            var last = lines.Length - 1;
            for (var i = 0; i <= last; i++)
            {
                var line = lines[i];
                var from = 0;
                var to = line.Length;
                if (i > 0) while (from < to && line[from] is ' ' or '\t') from++;
                if (i < last) while (to > from && line[to - 1] is ' ' or '\t') to--;
                lines[i] = line[from..to];
            }
            return ReBreaks.Replace(string.Join("\n", lines), m => m.Length == 1 ? " " : new string('\n', m.Length - 1));
        }

        // String.fromCodePoint(n): V8's RangeError for NaN, a negative, a non-integer or n > 0x10FFFF (review M3). A lone
        // surrogate is accepted.
        static string FromCodePoint(double n)
        {
            if (double.IsNaN(n) || n < 0 || n > 0x10FFFF || n != Math.Floor(n)) throw new YamlError("Invalid code point " + Js.Num(n));
            var cp = (int)n;
            return cp <= 0xFFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);
        }

        static string UnquoteDouble(string body)
        {
            var output = new StringBuilder();
            var s = Fold(ReEscapedBreak.Replace(body, ""));
            for (var i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch != '\\')
                {
                    output.Append(ch);
                    continue;
                }
                var e = At(s, ++i);
                if (e is 'x' or 'u' or 'U')
                {
                    var len = e == 'x' ? 2 : e == 'u' ? 4 : 8;
                    output.Append(FromCodePoint(Js.ParseInt(Slice(s, i + 1, i + 1 + len), 16)));
                    i += len;
                }
                else if (e != -1 && Escapes.TryGetValue((char)e, out var r))
                {
                    output.Append(r);
                }
                else
                {
                    throw new YamlError("YAML: unknown escape sequence");
                }
            }
            return output.ToString();
        }

        static int Skip(string s, int pos, bool commas)
        {
            while (pos < s.Length)
            {
                var c = s[pos];
                if (c is ' ' or '\n' or '\t' or '\r' || (commas && c == ','))
                {
                    pos++;
                }
                else if (c == '#' && (pos == 0 || Js.IsSpace(s[pos - 1])))
                {
                    var nl = s.IndexOf('\n', pos);
                    pos = nl == -1 ? s.Length : nl;
                }
                else
                {
                    break;
                }
            }
            return pos;
        }

        // Entries of a flow collection are separated by exactly one comma, as js-yaml requires.
        static void EntryStart(char ch, bool separated)
        {
            if (!separated) throw new YamlError("YAML: missed comma between flow collection entries");
            if (ch == ',') throw new YamlError("YAML: expected the node content, but found ','");
        }

        // Parses a flow value starting at s[pos]; returns the value and the index after it.
        static (JsonNode? Value, int End) Flow(string s, int pos, string stops, int depth = 0)
        {
            if (depth > MaxDepth) throw new YamlError("YAML: nesting is too deep");
            pos = Skip(s, pos, false);
            var ch = At(s, pos);
            if (ch == '{')
            {
                var output = new JsonObject();
                pos++;
                var separated = true;
                while (true)
                {
                    pos = Skip(s, pos, false);
                    if (pos >= s.Length) throw new Incomplete("YAML: unexpected end of the stream within a flow collection");
                    if (s[pos] == '}') return (output, pos + 1);
                    EntryStart(s[pos], separated);
                    JsonNode? key;
                    (key, pos) = Flow(s, pos, ":,}", depth + 1);
                    pos = Skip(s, pos, false);
                    JsonNode? value = null;
                    if (At(s, pos) == ':') (value, pos) = Flow(s, pos + 1, ",}", depth + 1);
                    Assign(output, Js.String(key), value);
                    pos = Skip(s, pos, false);
                    separated = At(s, pos) == ',';
                    if (separated) pos++;
                }
            }
            if (ch == '[')
            {
                var output = new JsonArray();
                pos++;
                var separated = true;
                while (true)
                {
                    pos = Skip(s, pos, false);
                    if (pos >= s.Length) throw new Incomplete("YAML: unexpected end of the stream within a flow collection");
                    if (s[pos] == ']') return (output, pos + 1);
                    EntryStart(s[pos], separated);
                    JsonNode? value;
                    (value, pos) = Flow(s, pos, ",]", depth + 1);
                    output.Add(value);
                    pos = Skip(s, pos, false);
                    separated = At(s, pos) == ',';
                    if (separated) pos++;
                }
            }
            if (ch == '"')
            {
                var end = pos + 1;
                while (end < s.Length && s[end] != '"') end += s[end] == '\\' ? 2 : 1;
                if (end >= s.Length) throw new Incomplete("YAML: unexpected end of the stream within a double quoted scalar");
                return (UnquoteDouble(s[(pos + 1)..end]), end + 1);
            }
            if (ch == '\'')
            {
                var end = pos + 1;
                while (end < s.Length)
                {
                    if (s[end] == '\'')
                    {
                        if (At(s, end + 1) != '\'') break;
                        end++;
                    }
                    end++;
                }
                if (end >= s.Length) throw new Incomplete("YAML: unexpected end of the stream within a single quoted scalar");
                return (Fold(s[(pos + 1)..end]).Replace("''", "'"), end + 1);
            }
            var stop = pos;
            while (stop < s.Length)
            {
                var c = s[stop];
                if (stops.Contains(c) && (c != ':' || stop + 1 >= s.Length || IsFlowBreak(s[stop + 1]))) break;
                if (c == '#' && stop > pos && Js.IsSpace(s[stop - 1]))
                {
                    var nl = s.IndexOf('\n', stop);
                    return (Scalar(Fold(Js.Trim(s[pos..stop]))), nl == -1 ? s.Length : nl);
                }
                stop++;
            }
            return (Scalar(Fold(Js.Trim(Slice(s, pos, stop)))), stop);
        }

        static string StripComment(string s)
        {
            var quote = '\0';   // '' upstream: no quote open
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"') i++;
                    else if (c == quote) quote = '\0';
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '#' && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t'))
                {
                    return s[..i];
                }
            }
            return s;
        }

        static string BlockScalar(Reader r, string header, int parentIndent)
        {
            var folded = header[0] == '>';
            // header.includes('-') / /[1-9]/.exec(header): over the whole header, its comment included, as upstream.
            var chomp = header.Contains('-') ? '-' : header.Contains('+') ? '+' : '\0';
            var digit = header.IndexOfAny(['1', '2', '3', '4', '5', '6', '7', '8', '9']);
            var indent = digit != -1 ? parentIndent + (header[digit] - '0') : -1;
            var output = new List<string>();
            while (r.I < r.Lines.Length)
            {
                var line = r.Lines[r.I];
                if (Js.Trim(line) == "")
                {
                    output.Add("");
                    r.I++;
                    continue;
                }
                var k = line.Length - Js.TrimStart(line).Length;
                if (indent == -1)
                {
                    if (k <= parentIndent) break;
                    indent = k;
                }
                if (k < indent) break;
                output.Add(line[indent..]);
                r.I++;
            }
            var trailing = 0;
            while (output.Count > 0 && output[^1] == "")
            {
                output.RemoveAt(output.Count - 1);
                trailing++;
            }
            string text;
            if (folded)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < output.Count; i++)
                {
                    var cur = output[i];
                    if (i > 0)
                    {
                        var prev = output[i - 1];
                        if (cur == "") sb.Append('\n');
                        else if (prev != "") sb.Append(StartsBlank(prev) || StartsBlank(cur) ? '\n' : ' ');
                    }
                    sb.Append(cur);
                }
                text = sb.ToString();
            }
            else
            {
                text = string.Join("\n", output);
            }
            if (output.Count == 0) return chomp == '+' ? new string('\n', trailing) : "";
            if (chomp == '-') return text;
            if (chomp == '+') return text + new string('\n', trailing + 1);
            return text + "\n";
        }

        static bool StartsBlank(string s) => s.Length > 0 && s[0] is ' ' or '\t';

        static int LeadingSpace(string line) => line.Length - Js.TrimStart(line).Length;

        static JsonNode? InlineValue(Reader r, string rest, int indent)
        {
            var first = At(rest, 0);
            if (first is '|' or '>')
            {
                if (ReBlockHeader.IsMatch(rest)) return BlockScalar(r, rest, indent);
            }
            if (first is '{' or '[' or '"' or '\'')
            {
                // The value may continue on later lines. Read them in growing batches until it closes.
                var start = r.I;
                var text = rest;
                var taken = 0;
                var batch = 1;
                while (true)
                {
                    try
                    {
                        var (value, end) = Flow(text, 0, "");
                        var used = 0;
                        for (var k = text.IndexOf('\n'); k != -1 && k < end; k = Js.IndexOf(text, "\n", k + 1)) used++;
                        var lineEnd = Js.IndexOf(text, "\n", end);
                        if (Js.Trim(StripComment(Slice(text, end, lineEnd == -1 ? text.Length : lineEnd))) != "")
                            throw new YamlError("YAML: unexpected content after a value");
                        r.I = start + used;
                        return value;
                    }
                    catch (Incomplete err)
                    {
                        if (start + taken >= r.Lines.Length) throw new YamlError(err.Message);
                        var more = r.Lines[(start + taken)..Math.Min(start + taken + batch, r.Lines.Length)];
                        text += "\n" + string.Join("\n", more);
                        taken += more.Length;
                        batch *= 2;
                    }
                }
            }
            var plain = Js.Trim(StripComment(rest));
            while (r.I < r.Lines.Length)
            {
                var line = r.Lines[r.I];
                if (Js.Trim(line) == "")
                {
                    var j = r.I;
                    while (j < r.Lines.Length && Js.Trim(r.Lines[j]) == "") j++;
                    if (j >= r.Lines.Length || LeadingSpace(r.Lines[j]) <= indent) break;
                    plain += new string('\n', j - r.I);
                    r.I = j;
                    continue;
                }
                if (LeadingSpace(line) <= indent) break;
                var more = Js.Trim(StripComment(line));
                plain += (plain.EndsWith('\n') ? "" : " ") + more;
                r.I++;
            }
            return Scalar(plain);
        }

        static bool IsEntry(string line) => line == "-" || line.StartsWith("- ", StringComparison.Ordinal);

        static JsonNode? Block(Reader r, int minIndent, int depth = 0)
        {
            if (depth > MaxDepth) throw new YamlError("YAML: nesting is too deep");
            r.SkipBlank();
            if (r.I >= r.Lines.Length) return null;
            var indent = r.Indent();
            if (indent < minIndent) return null;
            var line = r.Lines[r.I][indent..];

            if (IsEntry(line))
            {
                var output = new JsonArray();
                while (r.I < r.Lines.Length)
                {
                    r.SkipBlank();
                    if (r.I >= r.Lines.Length || r.Indent() != indent) break;
                    var cur = r.Lines[r.I][indent..];
                    if (!IsEntry(cur)) break;
                    var rest = cur[1..];
                    var pad = LeadingSpace(rest) + 1;
                    var body = Js.Trim(StripComment(rest));
                    if (body == "")
                    {
                        r.I++;
                        output.Add(Block(r, indent + 1, depth + 1));
                    }
                    else if (SplitKey(body) is not null)
                    {
                        r.Lines[r.I] = new string(' ', indent + pad) + Js.TrimStart(rest);
                        output.Add(Block(r, indent + pad, depth + 1));
                    }
                    else
                    {
                        r.I++;
                        output.Add(InlineValue(r, Js.Trim(rest), indent));
                    }
                }
                return output;
            }

            if (SplitKey(line) is null)
            {
                r.I++;
                return InlineValue(r, Js.Trim(line), indent - 1);
            }

            var map = new JsonObject();
            while (r.I < r.Lines.Length)
            {
                r.SkipBlank();
                if (r.I >= r.Lines.Length) break;
                var k = r.Indent();
                if (k < indent) break;
                if (k > indent) throw new YamlError("YAML: bad indentation of a mapping entry");
                var cur = r.Lines[r.I][indent..];
                var km = SplitKey(cur) ?? throw new YamlError("YAML: can not read a block mapping entry");
                var key = Js.String(Flow(km.Key, 0, "").Value);
                var rest = cur[km.Length..];
                r.I++;
                if (Js.Trim(StripComment(rest)) == "")
                {
                    r.SkipBlank();
                    if (r.I < r.Lines.Length)
                    {
                        var next = r.Indent();
                        var seq = IsEntry(r.Lines[r.I][next..]);
                        Assign(map, key, next > indent || (seq && next == indent) ? Block(r, seq ? indent : indent + 1, depth + 1) : null);
                    }
                    else
                    {
                        Assign(map, key, null);
                    }
                }
                else
                {
                    Assign(map, key, InlineValue(r, Js.Trim(rest), indent));
                }
            }
            return map;
        }

        public static JsonNode? Parse(string src)
        {
            var trimmed = Js.Trim(src);
            if (At(trimmed, 0) is '{' or '[')
            {
                // Upstream rethrows an Incomplete as an Error with its message; an Incomplete already is a YamlError.
                var (value, end) = Flow(trimmed, 0, "");
                if (Skip(trimmed, end, false) == trimmed.Length) return value;
            }
            var r = new Reader(src);
            var result = Block(r, 0);
            r.SkipBlank();
            if (r.I < r.Lines.Length && Js.Trim(r.Lines[r.I]) != "...")
                throw new YamlError("YAML: end of the stream or a document separator is expected");
            return result;
        }
    }
}
