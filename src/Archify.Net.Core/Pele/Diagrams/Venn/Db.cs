namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/venn/db.ts
    internal static partial class Venn
    {
        // Mermaid takes one pair of quotes off an id, label or style value.
        public static string NormalizeText(string text)
        {
            var trimmed = Js.Trim(text);
            return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
        }

        static List<string> NormalizeIds(List<string> ids)
        {
            var list = ids.Select(NormalizeText).ToList();
            Js.SortDefault(list);
            return list;
        }

        internal sealed class VennDb
        {
            public string? Title { get; set; }
            public List<VennSubset> Subsets { get; } = [];
            public List<VennText> TextNodes { get; } = [];
            public List<VennStyle> Styles { get; } = [];
            readonly HashSet<string> known = new(StringComparer.Ordinal);
            List<string>? current;
            // Whether an indented `text` line belongs to the set or union before it. The lexer reads this.
            bool indent;

            public void SetDiagramTitle(string title) => Title = title;

            // A set without a size gets 10, and an overlap of n sets 10 / n².
            public void AddSubsetData(List<string> ids, string? label, double? size)
            {
                var sets = NormalizeIds(ids);
                current = sets;
                if (sets.Count == 1) known.Add(sets[0]);
                Subsets.Add(new(sets, size ?? 10.0 / (ids.Count * ids.Count), string.IsNullOrEmpty(label) ? null : NormalizeText(label)));
            }

            public void AddTextData(List<string> ids, string id, string? label) =>
                TextNodes.Add(new(NormalizeIds(ids), NormalizeText(id), string.IsNullOrEmpty(label) ? null : NormalizeText(label)));

            public void AddStyleData(List<string> ids, List<(string Key, string Value)> data)
            {
                var styles = new JsMap<string, string>();
                foreach (var (key, value) in data) styles.Set(key, value != "" ? NormalizeText(value) : value);
                Styles.Add(new(NormalizeIds(ids), styles));
            }

            public void ValidateUnionIdentifiers(List<string> ids)
            {
                var unknown = ids.Select(NormalizeText).Where(id => !known.Contains(id)).ToList();
                // Deviation VN3 (PORT-008 Task 11): upstream writes the ids as parsed, so a Mermaid code shows as its internal
                // placeholder (`ﬂ°°35¶ß`) and an HTML entity as typed. The port names them as the chart draws an id, but with
                // a line break written back as its code (`#10;`, `#13;`): the message is shown up to its first line break.
                if (unknown.Count > 0)
                {
                    var named = unknown.Select(id => Entities.DecodeEntities(id).Replace("\n", "#10;", StringComparison.Ordinal).Replace("\r", "#13;", StringComparison.Ordinal));
                    throw new PeleException($"unknown set identifier: {string.Join(", ", named)}", "semantic");
                }
            }

            public List<string>? GetCurrentSets() => current;
            public bool GetIndentMode() => indent;
            public void SetIndentMode(bool enabled) => indent = enabled;
        }
    }
}
