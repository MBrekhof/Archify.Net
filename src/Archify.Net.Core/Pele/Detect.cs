using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    // detect.ts: Mermaid's detectors, in the order Mermaid registers them. The first match wins.
    // JS → .NET: `^` without `m` is \A and `$` is \z; `\s` is Chars.Ws; `\b` after a word character is "no word character
    // next"; the `i` flag (no `u`) folds ASCII letters only, so it is spelled out as [aA] classes (.NET IgnoreCase would let
    // U+212A KELVIN SIGN match k). The c4 pattern's alternation is unanchored after its first branch, as written (detect.ts:42).
    internal static class Detect
    {
        const string S = Chars.Ws;
        const string NoWordNext = "(?![A-Za-z0-9_])";

        static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant);

        // An ASCII case-insensitive literal, as the `i` flag without `u` reads it.
        internal static string Ci(string word)
        {
            var sb = new StringBuilder();
            foreach (var c in word)
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') sb.Append('[').Append(char.ToLowerInvariant(c)).Append(char.ToUpperInvariant(c)).Append(']');
                else sb.Append(Regex.Escape(c.ToString()));
            return sb.ToString();
        }

        static readonly (string Type, Regex Re)[] Detectors =
        [
            ("flowchart", R($@"\A{S}*flowchart-elk")),
            ("mindmap", R($@"\A{S}*mindmap")),
            ("architecture", R($@"\A{S}*architecture")),
            ("agentflow", R($@"\A{S}*agentflow-beta{NoWordNext}")),
            ("c4", R($@"\A{S}*C4Context|C4Container|C4Component|C4Dynamic|C4Deployment")),
            ("kanban", R($@"\A{S}*kanban")),
            ("class", R($@"\A{S}*classDiagram")),
            ("er", R($@"\A{S}*erDiagram")),
            ("gantt", R($@"\A{S}*gantt")),
            ("info", R($@"\A{S}*info")),
            ("pie", R($@"\A{S}*pie")),
            ("requirement", R($@"\A{S}*requirement(Diagram)?")),
            ("sequence", R($@"\A{S}*sequenceDiagram")),
            ("swimlane", R($@"\A{S}*swimlane-beta{NoWordNext}")),
            ("flowchart", R($@"\A{S}*(graph|flowchart)")),
            ("timeline", R($@"\A{S}*timeline")),
            ("gitGraph", R($@"\A{S}*gitGraph")),
            ("state", R($@"\A{S}*stateDiagram")),
            ("journey", R($@"\A{S}*journey")),
            ("quadrantChart", R($@"\A{S}*quadrantChart")),
            ("sankey", R($@"\A{S}*sankey(-beta)?")),
            ("packet", R($@"\A{S}*packet(-beta)?")),
            ("xychart", R($@"\A{S}*xychart(-beta)?")),
            ("block", R($@"\A{S}*block(-beta)?")),
            ("eventmodeling", R($@"\A{S}*eventmodeling")),
            ("treeView", R($@"\A{S}*treeView-beta")),
            ("radar", R($@"\A{S}*radar-beta")),
            ("ishikawa", R($@"\A{S}*{Ci("ishikawa")}({Ci("-beta")})?{NoWordNext}")),
            ("treemap", R($@"\A{S}*treemap")),
            ("railroad", R($@"\A{S}*{Ci("railroad-")}(?:{Ci("ebnf-")}|{Ci("abnf-")}|{Ci("peg-")})?{Ci("beta")}")),
            ("venn", R($@"\A{S}*venn-beta")),
            ("wardley", R($@"\A{S}*{Ci("wardley-beta")}")),
            ("cynefin", R($@"\A{S}*cynefin-beta(?:[{S[1..^1]}:]|\z)")),
            ("usecase", R($@"\A{S}*usecase-beta(?:{S}|\z)")),
        ];

        public static string? Run(string cleaned)
        {
            foreach (var (type, re) in Detectors) if (re.IsMatch(cleaned)) return type;
            return null;
        }
    }
}
