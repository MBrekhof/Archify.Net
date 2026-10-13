using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

// A JS RegExp source compiled with the `u` flag (ajv's unicodeRegExp default, see generated-validators.mjs) as a .NET Regex.
// ECMAScript mode gives ASCII \d and JS escape rules; the rewrites below cover what that mode does not:
//   `$` outside a class            → \z           (JS `$` without `m` is end of input; .NET `$` also matches before a final \n)
//   [\uD800-\uDFFF]                → lone-surrogate alternation (under /u a surrogate pair is one code point, never matched)
//   [^…] (negated class)           → (?>pair|[^…]) (under /u a negated class consumes a whole pair, so [^/]{256} counts code points)
// ponytail: `.`, \D \S \W and positive classes holding astral ranges keep code-unit semantics; no schema pattern uses them.
// Rewrite them the same way if one ever does. Sources with \u{…} or \p{…} are rejected for the same reason.
public static class JsRegex
{
    const string LoneSurrogate = @"(?:[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF])";
    const string SurrogatePair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";

    public static Regex Translate(string jsSource)
    {
        if (jsSource.Contains(@"\u{", StringComparison.Ordinal) || jsSource.Contains(@"\p{", StringComparison.Ordinal) || jsSource.Contains(@"\P{", StringComparison.Ordinal))
            throw new NotSupportedException($"JsRegex: unsupported /u construct in {jsSource}");
        var sb = new StringBuilder(jsSource.Length + 16);
        for (var i = 0; i < jsSource.Length; i++)
        {
            var c = jsSource[i];
            if (c == '\\') { sb.Append(jsSource, i, 2); i++; continue; }
            if (c == '$') { sb.Append(@"\z"); continue; }
            if (c != '[') { sb.Append(c); continue; }
            var end = i + 1;
            if (end < jsSource.Length && jsSource[end] == '^') end++;
            while (jsSource[end] != ']') end += jsSource[end] == '\\' ? 2 : 1;
            var cls = jsSource[i..(end + 1)];
            sb.Append(cls.Equals(@"[\uD800-\uDFFF]", StringComparison.OrdinalIgnoreCase) ? LoneSurrogate
                : cls.StartsWith("[^", StringComparison.Ordinal) ? $"(?>{SurrogatePair}|{cls})"
                : cls);
            i = end;
        }
        return new Regex(sb.ToString(), RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    }
}
