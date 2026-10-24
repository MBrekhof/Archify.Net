using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// V8's JSON.parse (json-parser.cc, Node 24): the document in V8 property order (array-index keys ascending, then the rest in
// insertion order; a duplicate key takes the last value at its first position) and V8's error message, whose position
// parse.mjs:78-80 turns into a line. The parse is written from V8's own scanner, so the message kinds and positions match
// (pinned by ExplainerJsonTests against Node).
internal static partial class ExplainerJson
{
    // ponytail: recursive, where V8 is iterative. Deeper nesting reports a message without a position (line 1), which is also
    // what Node reports once parse.mjs's own recursive scan overflows its stack (somewhere past a few thousand levels). Between
    // the two limits Node parses and the port does not; an iterative parse lifts it if a real draft ever nests that deep.
    const int MaxDepth = 512;

    [GeneratedRegex("position ([0-9]+)")] private static partial Regex Position();

    // The document, or null with the position V8's message names (null where it names none).
    internal static JsonNode? Parse(string text, out int? errorPosition)
    {
        var node = Parse(text, out string? message);
        errorPosition = message is not null && Position().Match(message) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
        return node;
    }

    // The document, or null with V8's error message.
    internal static JsonNode? Parse(string text, out string? message)
    {
        try
        {
            var parser = new Parser(text);
            var value = parser.Document();
            message = null;
            return value;
        }
        catch (JsonFailure failure)
        {
            message = failure.Message;
            return null;
        }
    }

    sealed class JsonFailure(string message) : Exception(message);

    sealed class Parser(string s)
    {
        int pos;
        const int End = -1;

        int Cur => pos < s.Length ? s[pos] : End;
        static bool IsWs(int c) => c is ' ' or '\t' or '\n' or '\r';
        void SkipWs() { while (pos < s.Length && IsWs(s[pos])) pos++; }

        public JsonNode? Document()
        {
            var value = Value(0);
            SkipWs();
            if (pos < s.Length) throw Report(Kind(Cur), "Unexpected non-whitespace character after JSON");
            return value;
        }

        JsonNode? Value(int depth)
        {
            SkipWs();
            if (depth > MaxDepth) throw new JsonFailure("Maximum call stack size exceeded");
            switch (Cur)
            {
                case '"': pos++; return JsonValue.Create(String());
                case '-' or (>= '0' and <= '9'): return JsonValue.Create(Number());
                case '{': return Object(depth);
                case '[': return Array(depth);
                case 't': Literal("true"); return JsonValue.Create(true);
                case 'f': Literal("false"); return JsonValue.Create(false);
                case 'n': Literal("null"); return null;
                default: throw Unexpected(Cur);
            }
        }

        JsonObject Object(int depth)
        {
            pos++;
            SkipWs();
            var keys = new List<string>();
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (Cur == '}') { pos++; return new JsonObject(); }
            var expected = "Expected property name or '}' in JSON";
            while (true)
            {
                SkipWs();
                if (Cur != '"') throw Report(Kind(Cur), expected);
                pos++;
                var key = String();
                SkipWs();
                if (Cur != ':') throw Report(Kind(Cur), "Expected ':' after property name in JSON");
                pos++;
                var value = Value(depth + 1);
                if (!values.ContainsKey(key)) keys.Add(key);
                values[key] = value;
                SkipWs();
                if (Cur == ',') { pos++; expected = "Expected double-quoted property name in JSON"; continue; }
                if (Cur != '}') throw Report(Kind(Cur), "Expected ',' or '}' after property value in JSON");
                pos++;
                break;
            }
            var result = new JsonObject();
            foreach (var key in keys.Where(ExplainerCharts.IsArrayIndex).OrderBy(ulong.Parse).Concat(keys.Where(k => !ExplainerCharts.IsArrayIndex(k))))
                result[key] = values[key];
            return result;
        }

        JsonArray Array(int depth)
        {
            pos++;
            SkipWs();
            var result = new JsonArray();
            if (Cur == ']') { pos++; return result; }
            while (true)
            {
                result.Add(Value(depth + 1));
                SkipWs();
                if (Cur == ',') { pos++; continue; }
                if (Cur != ']') throw Report(Kind(Cur), "Expected ',' or ']' after array element in JSON");
                pos++;
                return result;
            }
        }

        // ScanJsonString, after the opening quote.
        string String()
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw Report(Token.Illegal, "Unterminated string in JSON");
                var c = s[pos];
                if (c == '"') { pos++; return sb.ToString(); }
                if (c == '\\')
                {
                    pos++;
                    var e = Cur;
                    if (e == End || e > 0xFF) throw Unexpected(e);
                    switch (e)
                    {
                        case '"' or '\\' or '/': sb.Append((char)e); break;
                        case 'b': sb.Append('\b'); break;
                        case 't': sb.Append('\t'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'u':
                            var value = 0;
                            for (var i = 0; i < 4; i++)
                            {
                                pos++;
                                var digit = Hex(Cur);
                                if (digit < 0) throw Report(Token.Illegal, "Bad Unicode escape in JSON");
                                value = value * 16 + digit;
                            }
                            sb.Append((char)value);
                            break;
                        default: throw Report(Token.Illegal, "Bad escaped character in JSON");
                    }
                    pos++;
                    continue;
                }
                if (c < 0x20) throw Report(Token.Illegal, "Bad control character in string literal in JSON");
                sb.Append(c);
                pos++;
            }
        }

        static int Hex(int c) => c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' && c != End ? (c | 0x20) - 'a' + 10 : -1;
        static bool IsNumberPart(int c) => c is (>= '0' and <= '9') or '-' or '+' or '.' or 'e' or 'E';
        void Digits() { while (Cur is >= '0' and <= '9') pos++; }

        // ParseJsonNumber.
        double Number()
        {
            var start = pos;
            var negative = Cur == '-';
            if (negative) pos++;
            if (Cur == '0')
            {
                pos++;
                if (IsNumberPart(Cur)) { if (Cur is >= '0' and <= '9') throw Report(Token.Number, null); }
                else if (!negative) return 0;
            }
            else
            {
                var digits = pos;
                Digits();
                if (pos == digits) throw Report(Token.Illegal, "No number after minus sign in JSON");
            }
            if (Cur == '.')
            {
                pos++;
                if (Cur is not (>= '0' and <= '9')) throw Report(Token.Illegal, "Unterminated fractional number in JSON");
                Digits();
            }
            if (Cur is 'e' or 'E')
            {
                pos++;
                if (Cur is '-' or '+') pos++;
                if (Cur is not (>= '0' and <= '9')) throw Report(Token.Illegal, "Exponent part is missing a number in JSON");
                Digits();
            }
            return Js.ToNumber(s[start..pos]);
        }

        // ScanLiteral: the first character was matched by the caller.
        void Literal(string word)
        {
            var remaining = s.Length - pos;
            if (remaining >= word.Length && string.CompareOrdinal(s, pos + 1, word, 1, word.Length - 1) == 0) { pos += word.Length; return; }
            pos++;
            for (var i = 0; i < Math.Min(word.Length - 1, remaining - 1); i++)
            {
                if (word[1 + i] != s[pos]) throw Unexpected(s[pos]);
                pos++;
            }
            throw Report(Token.End, null);
        }

        enum Token { End, Number, String, Illegal }
        static Token Kind(int c) => c switch { End => Token.End, '"' => Token.String, '-' or (>= '0' and <= '9') => Token.Number, _ => Token.Illegal };

        // ReportUnexpectedCharacter.
        JsonFailure Unexpected(int c) => Report(Kind(c), null);

        // ReportUnexpectedToken: a given message wins; otherwise the token picks one (LookUpErrorMessageForJsonToken).
        JsonFailure Report(Token token, string? message)
        {
            if (message is not null) return new($"{message}{At()}");
            switch (token)
            {
                case Token.End: return new("Unexpected end of JSON input");
                case Token.Number: return new($"Unexpected number in JSON{At()}");
                case Token.String: return new($"Unexpected string in JSON{At()}");
            }
            if (s is "[object Object]" or "undefined" or "Infinity" or "NaN") return new($"\"{s}\" is not valid JSON");
            var ch = s[pos].ToString();
            const int context = 10;
            if (s.Length < 2 * context + 1) return new($"Unexpected token '{ch}', \"{s}\" is not valid JSON");
            if (pos < context) return new($"Unexpected token '{ch}', \"{s[..(pos + context)]}\"... is not valid JSON");
            if (pos < s.Length - context) return new($"Unexpected token '{ch}', ...\"{s[(pos - context)..(pos + context)]}\"... is not valid JSON");
            return new($"Unexpected token '{ch}', ...\"{s[(pos - context)..]}\" is not valid JSON");
        }

        // " at position N (line L column C)": a CRLF counts as one line break, a lone CR or LF as one each.
        string At()
        {
            int line = 1, lineStart = 0, i = 0;
            for (; i < pos; i++)
            {
                if (s[i] == '\r' && i < pos - 1 && s[i + 1] == '\n') i++;
                if (s[i] is '\r' or '\n') { line++; lineStart = i + 1; }
            }
            return $" at position {pos} (line {line} column {1 + i - lineStart})";
        }
    }
}
