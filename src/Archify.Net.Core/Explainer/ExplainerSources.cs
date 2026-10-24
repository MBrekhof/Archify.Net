using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

// archify 3.2.0 explainer/sources.mjs. For now only containsExplainerSecret (:19-34), which the diff parser needs.
internal static class ExplainerSources
{
    // The patterns' `/i` has no `u`, so it folds ASCII letters only: each letter is spelled as a class, because .NET's
    // IgnoreCase also folds U+017F (long s) to s and U+212A (Kelvin sign) to k. `\b` and `\w` are ASCII.
    const string Word = "[A-Za-z0-9_]";
    const string B = $"(?:(?<={Word})(?!{Word})|(?<!{Word})(?={Word}))";
    const string Ws = Js.WsClass;

    static string Ci(string literal)
    {
        var sb = new StringBuilder();
        foreach (var c in literal) sb.Append(char.IsAsciiLetter(c) ? $"[{char.ToUpperInvariant(c)}{char.ToLowerInvariant(c)}]" : Regex.Escape(c.ToString()));
        return sb.ToString();
    }

    static readonly Regex SecretContent = new(
        $"{Ci("-----BEGIN ")}(?:[A-Za-z ]*{Ci("PRIVATE KEY")}|{Ci("PGP PRIVATE KEY BLOCK")})-----"
        + $"|{B}(?:{Ci("AKIA")}|{Ci("ASIA")})[A-Za-z0-9]{{16}}{B}"
        + $"|{B}(?:{Ci("gh")}[PpOoUuSsRr]_[A-Za-z0-9]{{20,}}|{Ci("github_pat_")}[A-Za-z0-9_]{{20,}}|{Ci("sk-")}(?:{Ci("proj-")})?[A-Za-z0-9_-]{{20,}}){B}");
    static readonly Regex CredentialAssignment = new(
        $"{B}(?:{Ci("password")}|{Ci("passwd")}|{Ci("secret")}|{Ci("api")}[_-]?{Ci("key")}|{Ci("access")}[_-]?{Ci("token")}|{Ci("auth")}[_-]?{Ci("token")}|{Ci("client")}[_-]?{Ci("secret")}){B}[\"']?[ \\t]*[:=][ \\t]*([^\\r\\n]*)");
    static readonly Regex EnvironmentReference = new(
        $@"\A(?:\$(?:[A-Za-z_]{Word}*|\{{[A-Za-z_]{Word}*\}})|(?:process\.env|os\.environ|env)(?:\.[A-Za-z_]{Word}*|\[['""][A-Za-z_]{Word}*['""]\])|(?:os\.getenv|getenv|env)\({Ws}*['""][A-Za-z_]{Word}*['""]{Ws}*\))\z");
    static readonly Regex Configuration = new($@"\.(?:[Yy][Aa]?[Mm][Ll]|{Ci("toml")}|{Ci("ini")}|{Ci("conf")}|{Ci("config")}|{Ci("properties")}|{Ci("cfg")}|{Ci("sh")}|{Ci("bash")})\z");
    static readonly Regex Comment = new($@"{Ws}+(?:#|//)[^\n\r\u2028\u2029]*\z");
    static readonly Regex TrailingSeparator = new($@"[;,]{Ws}*\z");
    static readonly Regex Empty = new($@"\A(?:{Ci("null")}|{Ci("nil")}|{Ci("none")}|{Ci("undefined")}|~)\z");
    static readonly Regex Quoted = new(@"\A(['""`])([\s\S]*?)\1");

    /// <summary>containsExplainerSecret (sources.mjs:19-34).</summary>
    public static bool ContainsSecret(string text, string? sourcePath = "")
    {
        if (SecretContent.IsMatch(text)) return true;
        var configuration = Configuration.IsMatch(sourcePath ?? "");
        foreach (Match match in CredentialAssignment.Matches(text))
        {
            var value = Js.Trim(TrailingSeparator.Replace(Comment.Replace(Js.Trim(match.Groups[1].Value), "", 1), "", 1));
            if (value.Length == 0 || Empty.IsMatch(value) || EnvironmentReference.IsMatch(value)) continue;
            var quoted = Quoted.Match(value);
            if (quoted.Success)
            {
                if (quoted.Groups[2].Length >= 4 && !EnvironmentReference.IsMatch(quoted.Groups[2].Value)) return true;
            }
            // Configuration scalars are values, whereas an unquoted JS/Python identifier can be a code reference.
            else if (configuration) return true;
        }
        return false;
    }
}
