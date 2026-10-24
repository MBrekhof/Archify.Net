using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// One prose entry for lintExplainerWriting (writing.mjs:61). Null members are JS undefined.
internal sealed record WritingEntry(string? Text, double? Line, string? Component, string? PanelId = null, string? Pointer = null, string? Raw = null);

// archify 3.2.0 explainer/writing.mjs: independently authored prose hints; no external dictionary or automatic rewriting.
internal static class ExplainerWriting
{
    // `/giu`: Unicode simple case folding is IgnoreCase | CultureInvariant (U+017F and U+212A pinned by the writing seeds).
    // Under `iu`, \b's word characters are [A-Za-z0-9_] plus U+017F and U+212A (they fold into it); a Han character is not one.
    const string Word = @"[A-Za-z0-9_\u017F\u212A]";
    const string B = $"(?-i:(?:(?<={Word})(?!{Word})|(?<!{Word})(?={Word})))";
    const string Ws = Js.WsClass;
    static readonly string Han = Atom(Js.IsHan);
    const RegexOptions Iu = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // \p{Script=Han} as a .NET pattern: a BMP class plus one surrogate pair alternative per high surrogate.
    static string Atom(Func<int, bool> test)
    {
        var ranges = new List<(int First, int Last)>();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (!test(cp)) continue;
            if (ranges.Count > 0 && ranges[^1].Last == cp - 1) ranges[^1] = (ranges[^1].First, cp); else ranges.Add((cp, cp));
        }
        static string U(int c) => $"\\u{c:X4}";
        var bmp = new StringBuilder();
        var pairs = new List<string>();
        foreach (var (first, last) in ranges)
        {
            if (last <= 0xFFFF) { bmp.Append(first == last ? U(first) : $"{U(first)}-{U(last)}"); continue; }
            for (var hi = 0xD800 + ((first - 0x10000) >> 10); hi <= 0xD800 + ((last - 0x10000) >> 10); hi++)
            {
                var lo1 = Math.Max(first, 0x10000 + ((hi - 0xD800) << 10));
                var lo2 = Math.Min(last, 0x10000 + ((hi - 0xD800) << 10) + 0x3FF);
                pairs.Add($"{U(hi)}[{U(0xDC00 + ((lo1 - 0x10000) & 0x3FF))}-{U(0xDC00 + ((lo2 - 0x10000) & 0x3FF))}]");
            }
        }
        return $"(?:[{bmp}]|{string.Join("|", pairs)})";
    }

    sealed record Rule(string Id, Regex Pattern, string Reason, string Example, string Fix);

    // writing.mjs:5-22. The pattern sources are translated once; a new Match loop runs per entry, as upstream's per-entry RegExp.
    static readonly Rule[] Rules =
    [
        new("qualification", new($@"示例|本例|不代表|未实测|仅供参考|{B}illustrative{B}|{B}not measured{B}", Iu),
            "把限定条件写进 scope 或来源", "@answer scope=\"适用于当前请求路径\"", "把限定条件写进 scope 或来源，正文直接解释判断和原因。"),
        new("light-verb", new($@"进行(?![中时]){Han}{{2}}|(?:加以|予以|做出|作出){Han}{{2}}|{B}(?:perform an analysis of|carry out an investigation of|make a decision|give consideration to|conduct an? (?:review|investigation|analysis) of){B}", Iu),
            "A light-verb construction can obscure the action.", "进行检查 → 检查；做出决定 → 决定; make a decision → decide", "Use the direct action verb where it preserves the meaning; name who performs the action."),
        new("cliche", new($@"赋能|抓手|全方位|多维度|深度融合|至关重要|不可或缺|一站式|底层逻辑|{B}(?:at the end of the day|in today[’']s fast-paced world|unlock the full potential|game[- ]changer|cutting[- ]edge|seamless(?:ly)?|delve into){B}", Iu),
            "This stock phrase gives little information about the concrete outcome.", "赋能业务 → 让客服查到订单状态; seamless → name what no longer needs a manual step", "Replace the stock phrase with the specific action or outcome the reader needs."),
        new("filler", new($@"下面我(?:来|将)|接下来我(?:来|将)|让我(?:们)?来|总的来说|综上所述|总而言之|值得注意的是|众所周知|毋庸置疑|希望(?:这)?(?:对你|对您)有(?:所)?帮助|如有疑问|{B}(?:here is an overview|let[’']s (?:dive|take a look)|in conclusion|in summary|it is worth noting that|as we all know|i hope this helps){B}", Iu),
            "Opening, closing or signposting filler delays the facts.", "综上所述，选 A → 选 A; In summary, use A → Use A", "Delete the phrase; open with the claim and stop after the last supported point."),
        new("vague-quantity", new($@"大量(?:请求|用户|数据)|很多(?:请求|用户|数据)|若干|尽快|{B}(?:many users|a large number of requests|significantly faster|as soon as possible){B}", Iu),
            "The quantity or comparison lacks a stated bound or measurement.", "大量请求 → 写明已知请求量或说明未测量；尽快 → 写明期限; significantly faster → give the measured comparison or say it is unmeasured", "Add a supported quantity, range, deadline or comparison basis; when unknown, state that it is unmeasured. Do not invent a number."),
        new("number-boundary", new($@"(?<=[0-9]{Ws}*(?:个|次|秒|天|分钟|小时|倍|人|条|项|%|ms|MB|GB|KB|QPS)?{Ws}*)(?:以上|以下|以内)"),
            "“以上/以下/以内” after a number leaves the endpoint unclear.", "3 次以上 → 超过 3 次 / 不少于 3 次；1 秒以内 → 不超过 1 秒", "Say whether the endpoint is included: 超过/不少于, 低于/不超过."),
        new("plain-word", new($@"{B}(?:utili[sz]e[sd]?|utili[sz]ation|prior to|in order to|commence[sd]?|facilitate[sd]?|subsequently|in the event that|due to the fact that|at this point in time|endeavou?r){B}", Iu),
            "A longer word replaces a common one with the same meaning.", "utilize → use; prior to → before; in order to → to; due to the fact that → because", "Use the shorter common word."),
        new("redundancy", new($@"提前预先|重复重复|基本基础|{B}(?:each and every|future plans|completely eliminate){B}", Iu),
            "Adjacent wording may repeat the same meaning.", "提前预先 → 提前; each and every → each", "Keep one expression of the meaning unless both terms carry a necessary distinction."),
    ];

    static readonly Regex Url = new($"https?://[^{Ws[1..^1]}<>。！？，；、：）]+", Iu);
    static readonly Regex Bold = new(@"\*\*([^\n\r\u2028\u2029]+?)\*\*");
    static readonly Regex DeExcluded = new("目的|的确|的话");

    // Mask URL bodies without changing offsets or swallowing trailing sentence punctuation (writing.mjs:25-42).
    static string ProseMask(string text) => Url.Replace(text, match =>
    {
        var value = match.Value;
        // A Markdown link's closing parenthesis is outside the URL. Preserve that
        // delimiter and any following prose, while retaining balanced URL paths.
        int depth = 0, end = value.Length;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '(') depth++;
            else if (value[i] == ')')
            {
                if (depth == 0) { end = i; break; }
                depth--;
            }
        }
        var url = value[..end];
        var masked = url.Length;   // url.match(/[.!?,;]+$/u): the trailing run of those characters stays visible
        while (masked > 0 && url[masked - 1] is '.' or '!' or '?' or ',' or ';') masked--;
        return new string(' ', masked) + value[masked..];
    });

    static bool IsDigit(char c) => c is >= '0' and <= '9';
    static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '。' or '！' or '？';

    static List<(string Text, int Offset)> Sentences(string text)
    {
        var result = new List<(string, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var dec = c == '.' && i > 0 && IsDigit(text[i - 1]) && i + 1 < text.Length && IsDigit(text[i + 1]);
            var paragraph = false;
            if (c == '\n')   // /^\s*\n/u on the rest: a second \n inside the whitespace run that follows
                for (var j = i + 1; j < text.Length && Js.IsSpace(text[j]); j++) if (text[j] == '\n') { paragraph = true; break; }
            if ((IsSentenceEnd(c) && !dec) || paragraph) { result.Add((text[start..(i + 1)], start)); start = i + 1; }
        }
        if (start < text.Length) result.Add((text[start..], start));
        return result;
    }

    static int CodePointAt(string s, int i, out int width)
    {
        width = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        return width == 2 ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];
    }
    static int HanCount(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length;)
        {
            if (Js.IsHan(CodePointAt(s, i, out var w))) n++;
            i += w;
        }
        return n;
    }

    // /[\p{Script=Latin}\d]+(?:[’'_-][\p{Script=Latin}\d]+)*(?:\.\d+)?/gu, counted. Each part is maximal and the tail parts are
    // optional, so the first match at a position is the greedy scan.
    static int WordCount(string s)
    {
        bool L(int i, out int w) { w = 1; if (i >= s.Length) return false; var cp = CodePointAt(s, i, out w); return Js.IsLatin(cp) || cp is >= '0' and <= '9'; }
        int Run(int i) { while (L(i, out var w)) i += w; return i; }
        var n = 0;
        for (var i = 0; i < s.Length;)
        {
            if (!L(i, out var width)) { i += width; continue; }
            i = Run(i);
            while (i < s.Length && s[i] is '’' or '\'' or '_' or '-' && L(i + 1, out _)) i = Run(i + 1);
            if (i + 1 < s.Length && s[i] == '.' && IsDigit(s[i + 1])) { i++; while (i < s.Length && IsDigit(s[i])) i++; }
            n++;
        }
        return n;
    }
    static int Units(string value) => HanCount(value) + WordCount(value);
    static int Search(string s) { for (var i = 0; i < s.Length; i++) if (!Js.IsSpace(s[i])) return i; return -1; }

    sealed record Found(int Offset, string Rule, string Reason, string Example, string Fix, string? Matched = null, int Units = 0, double Limit = 0);

    static bool SafePositive(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.D() is var d
        && d == Math.Floor(d) && Math.Abs(d) <= 9007199254740991 && d >= 1;

    /// <summary>lintExplainerWriting (writing.mjs:67-139). Upstream's TypeErrors are ArgumentExceptions with the same message.</summary>
    public static List<Diagnostic> Lint(IReadOnlyList<WritingEntry> entries, string mode = "warn", string locale = "auto", JsonObject? sentenceLimits = null)
    {
        if (mode is not ("warn" or "strict" or "off")) throw new ArgumentException("Writing mode must be warn, strict or off");
        if (mode == "off") return [];
        var limits = new JsonObject { ["zh"] = 50, ["en"] = 28 };
        foreach (var (key, value) in sentenceLimits ?? []) limits[key] = value?.DeepClone();
        if (!limits.All(entry => SafePositive(entry.Value))) throw new ArgumentException("Sentence limits must be positive integers");
        var diagnostics = new List<Diagnostic>();
        foreach (var entry in entries)
        {
            if (entry.Text is null) throw new ArgumentException("Writing entries require text strings");
            if (entry.Component == "source") continue;
            var original = entry.Text;
            var text = ProseMask(original);
            var found = new List<Found>();
            if (entry.Component == "scope")
            {
                if (Units(text) > 50) found.Add(new(0, "scope-length", "The scope reads as a disclaimer rather than a short label.",
                    "8 人团队 · 上线半年 · 峰值约 300 QPS / Eight people · six months live · ~300 QPS peak",
                    "Keep the conditions a reader must check, as short labels separated by “·”. Drop process notes about how the page was made.", Units: Units(text), Limit: 50));
            }
            else
                foreach (var rule in Rules)
                    foreach (Match match in rule.Pattern.Matches(text))
                        found.Add(new(match.Index, rule.Id, rule.Reason, rule.Example, rule.Fix, match.Value));
            if (entry.Pointer == "/answer/summary" && entry.Raw is { } raw)
            {
                var bold = Bold.Matches(raw).Sum(match => Units(match.Groups[1].Value));
                if (Units(text) >= 12 && (double)bold / Units(text) > .6) found.Add(new(0, "verdict-emphasis", "Most of the conclusion is bold, so nothing stands out.",
                    "先做**模块化单体**，等边界稳定后再拆。 / Keep a **modular monolith** until boundaries settle.", "Bold only the few words that carry the decision."));
            }
            var sentenceCount = Sentences(text).Count(sentence => Js.Trim(sentence.Text).Length > 0);
            if (entry.Component == "prose" && (Units(text) > 120 || sentenceCount > 4))
                found.Add(new(0, "paragraph-length", $"This paragraph has {sentenceCount} sentences and {Units(text)} reading units; long paragraphs are what the page should replace.",
                    "把并列要点写成列表，把先后动作写成 @steps，把方案差异写成对比表。 / Turn parallel points into a list, ordered actions into @steps and option differences into a comparison.",
                    "Split the paragraph into a list, steps or a comparison; keep at most two or three short sentences of prose.", Units: Units(text), Limit: 120));
            if (entry.Component != "scope")
                foreach (var (sentence, offset) in Sentences(text))
                {
                    var han = HanCount(sentence);
                    var words = WordCount(sentence);
                    var units = han + words;
                    var language = han > 0 ? "zh" : words > 0 ? "en" : locale.StartsWith("zh", StringComparison.Ordinal) ? "zh" : "en";
                    var limit = limits[language]!.D();
                    var particles = DeExcluded.Replace(sentence, "").Count(c => c == '的');
                    if (particles >= 3) found.Add(new(offset + Math.Max(0, Search(sentence)), "de-chain", $"This sentence chains {particles} “的” modifiers, so the head noun arrives late.",
                        "负责处理用户提交的订单的服务的日志 → 订单服务的日志。它记录用户提交的订单。", "Split the sentence or move a modifier into its own clause.", Js.Trim(sentence)));
                    if (units > limit)
                        found.Add(new(offset + Math.Max(0, Search(sentence)), "sentence-length", $"Sentence has {units} reading units, above the configured {language} limit of {Js.Num(limit)}.",
                            "先查缓存。未命中时再查数据库。 / Check the cache first. Query the database on a miss.",
                            "Split at a logical action or condition boundary while preserving qualifications and causal links.", Units: units, Limit: limit));
                }
            Js.Sort(found, (a, b) => a.Offset != b.Offset ? a.Offset - b.Offset : Js.LocaleCompare(a.Rule, b.Rule));
            foreach (var item in found)
            {
                var line = entry.Line is { } l && l == Math.Floor(l) && double.IsFinite(l) && l > 0 ? l : 1;
                var subject = new JsonObject { ["line"] = line + original[..item.Offset].Count(c => c == '\n') };
                if (entry.Component is not null) subject["component"] = entry.Component;
                if (entry.PanelId is not null) subject["panelId"] = entry.PanelId;
                var evidence = new JsonObject { ["rule"] = item.Rule, ["reason"] = item.Reason, ["example"] = item.Example };
                if (!string.IsNullOrEmpty(item.Matched)) evidence["matched"] = item.Matched;
                if (item.Units != 0) { evidence["units"] = item.Units; evidence["limit"] = item.Limit; }
                diagnostics.Add(new($"explainer/writing-{item.Rule}", mode == "strict" ? Severity.Error : Severity.Warning, item.Reason, subject, evidence, [item.Fix], null));
            }
        }
        return diagnostics;
    }
}
