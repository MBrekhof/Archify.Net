using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net.Cli.Commands;

// commandGuide (A:6054-6105) and the CLI half of recipes/scenarios.mjs (:331-446); the recipe data is the embedded
// SCENARIO_RECIPES (Q9). startPromptsFor and publicGuideData serve upstream's site build, not the CLI, so they are not ported.
static class GuideCommand
{
    static readonly JsonArray Recipes = JsonNode.Parse(Resources.Text("recipes/scenarios.json"))!.AsArray();

    public static int Run(string[] args, CliIo io)
    {
        string? lang = null;
        var json = false;
        var queryParts = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == "--json") json = true;
            else if (arg == "--lang") { lang = LangValue(args.ElementAtOrDefault(index + 1)); index++; }
            else if (arg.StartsWith("--lang=", StringComparison.Ordinal)) lang = LangValue(arg["--lang=".Length..]);
            else if (arg.StartsWith("--", StringComparison.Ordinal)) throw new CliFailException($"Unknown guide option \"{arg}\".");
            else queryParts.Add(arg);
        }

        var query = Js.Trim(string.Join(" ", queryParts));
        if (query.Length == 0)
        {
            var selectedLang = lang ?? "en";
            io.Out(json
                ? Js.Stringify(new JsonObject { ["ok"] = true, ["mode"] = "list", ["lang"] = selectedLang, ["recipes"] = List(selectedLang) }, 2)
                : FormatList(selectedLang));
            return 0;
        }
        var result = Recommend(query, lang);
        io.Out(json ? Js.Stringify(result, 2) : FormatRecommendation(result));
        return 0;
    }

    static string LangValue(string? value) =>
        value is "en" or "zh" ? value : throw new CliFailException("--lang must be \"en\" or \"zh\".");

    // detectGuideLanguage (:331-333)
    internal static string DetectLanguage(string value) => value.Any(c => c is >= '㐀' and <= '鿿') ? "zh" : "en";

    // normalized (:350-352): NFKC, toLowerCase, /[\s_]+/g -> ' ', trim.
    static string Normalized(string value)
    {
        var sb = new StringBuilder();
        var inRun = false;
        foreach (var c in Js.ToLowerCase(value.Normalize(NormalizationForm.FormKC)))
        {
            var separator = c == '_' || Js.IsSpace(c);
            if (!separator) sb.Append(c);
            else if (!inRun) sb.Append(' ');
            inRun = separator;
        }
        return Js.Trim(sb.ToString());
    }

    // localized (:354-364): the language block spreads after presentation, include copied in place.
    static JsonObject Localized(JsonNode recipe, string lang)
    {
        var result = new JsonObject
        {
            ["id"] = recipe["id"]!.DeepClone(),
            ["type"] = recipe["type"]!.DeepClone(),
            ["proof"] = recipe["proof"]!.DeepClone(),
            ["presentation"] = recipe["presentation"]!.DeepClone(),
        };
        foreach (var (key, value) in recipe[lang == "zh" ? "zh" : "en"]!.AsObject()) result[key] = value?.DeepClone();
        return result;
    }

    static JsonArray List(string lang) => [.. Recipes.Select(r => (JsonNode)Localized(r!, lang))];

    // scoreRecipe (:370-385)
    static (JsonNode Recipe, int Score, List<string> Matched) Score(JsonNode recipe, string query)
    {
        var text = Normalized(query);
        var id = (string)recipe["id"]!;
        if (text.Length == 0) return (recipe, 0, []);
        if (text == id || text == id.Replace('-', ' ')) return (recipe, 100, [id]);
        var score = 0;
        var matched = new List<string>();
        foreach (var signal in recipe["signals"]!.AsArray())
        {
            var phrase = (string)signal![0]!;
            if (!text.Contains(Normalized(phrase), StringComparison.Ordinal)) continue;
            score += (int)signal[1]!;
            matched.Add(phrase);
        }
        return (recipe, score, matched);
    }

    // recommendScenario (:387-405); `lang` is --lang, else detected from the query.
    internal static JsonObject Recommend(string query, string? lang)
    {
        lang ??= DetectLanguage(query);
        // Score descending, then recipe order: a total order, so the stable sort is the JS sort.
        var ranked = Recipes.Select(r => Score(r!, query)).OrderByDescending(e => e.Score).ToList();
        var winner = ranked[0].Score > 0 ? ranked[0] : (Recipe: Recipes[0]!, Score: 0, Matched: new List<string>());
        return new JsonObject
        {
            ["ok"] = true,
            ["mode"] = "recommendation",
            ["lang"] = lang,
            ["query"] = query,
            ["confidence"] = winner.Score >= 14 ? "high" : winner.Score >= 7 ? "medium" : "low",
            ["matchedSignals"] = new JsonArray([.. winner.Matched.Select(m => (JsonNode)m)]),
            ["recommendation"] = Localized(winner.Recipe, lang),
            ["alternatives"] = new JsonArray([.. ranked.Where(e => e.Recipe != winner.Recipe && e.Score > 0).Take(2).Select(e =>
            {
                var alternative = Localized(e.Recipe, lang);
                alternative["score"] = e.Score;
                return (JsonNode)alternative;
            })]),
        };
    }

    // formatScenarioList (:407-419)
    static string FormatList(string lang)
    {
        var isZh = lang == "zh";
        string[] lines =
        [
            isZh ? $"Archify 场景配方（{Recipes.Count}）" : $"Archify scenario recipes ({Recipes.Count})",
            "",
            isZh ? "先选择你要回答的问题，再选择图表类型。可运行：archify guide \"你的场景\"" : "Choose the question before the diagram type. Run: archify guide \"your scenario\"",
            "",
            .. List(lang).SelectMany(r => new[] { $"{r!["id"]}  [{r["type"]}]  {r["title"]}", $"  {r["question"]}" }),
        ];
        return string.Join("\n", lines);
    }

    sealed record Labels(string Heading, string Question, string Use, string Avoid, string Include, string Presentation, string Prompt,
        string Alternatives, string Confidence);

    static readonly Labels ZhLabels = new("推荐", "要回答的问题", "适合", "不要这样用", "必须包含", "表现建议", "可直接复制的提示词", "其他可能", "置信度");
    static readonly Labels EnLabels = new("Recommendation", "Question answered", "Use when", "Avoid when", "Must include", "Presentation",
        "Copy-ready prompt", "Other possible fits", "Confidence");

    // formatScenarioRecommendation (:421-446)
    static string FormatRecommendation(JsonObject result)
    {
        var isZh = (string?)result["lang"] == "zh";
        var recipe = result["recommendation"]!;
        var labels = isZh ? ZhLabels : EnLabels;
        var lines = new List<string>
        {
            $"{labels.Heading}: {recipe["title"]}  [{recipe["type"]}]",
            $"{labels.Confidence}: {result["confidence"]}",
            $"{labels.Question}: {recipe["question"]}",
            "",
            $"{labels.Use}: {recipe["useWhen"]}",
            $"{labels.Avoid}: {recipe["avoidWhen"]}",
            $"{labels.Include}: {string.Join(isZh ? "、" : "; ", recipe["include"]!.AsArray().Select(i => (string?)i))}",
            $"{labels.Presentation}: {recipe["presentation"]!["preset"]} · {recipe["presentation"]!["motion"]}",
            "",
            $"{labels.Prompt}:",
            (string)recipe["prompt"]!,
        };
        var alternatives = result["alternatives"]!.AsArray();
        if (alternatives.Count > 0)
            lines.AddRange(["", $"{labels.Alternatives}: {string.Join(" · ", alternatives.Select(a => $"{a!["title"]} [{a["type"]}]"))}"]);
        return string.Join("\n", lines);
    }
}
