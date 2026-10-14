using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class JsTextTests
{
    static JsonArray Fx => (JsonArray)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/segmenter.json")))!;
    static string[] Strs(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    [Fact] public void Graphemes_match_Intl_Segmenter_for_every_script()
    {   // Not CJK-exempt: grapheme clustering is UAX #29 in both engines; Han rows included on purpose.
        foreach (var e in Fx) Assert.Equal(Strs(e!["graphemes"]), Js.Graphemes(e["text"]!.GetValue<string>()));
    }
    [Fact] public void WordBreaks_concatenate_and_fall_back_to_graphemes_on_Han_runs()
    {   // Parity-exempt by spec: the only upstream input is a Han+Mark run; `words` (ICU's dictionary segmentation) is recorded for information.
        foreach (var e in Fx)
        {
            var text = e!["text"]!.GetValue<string>();
            Assert.Equal(text, string.Concat(Js.WordBreaks(text)));
            foreach (var token in Js.HanTokens(text).Where(Js.HasHan)) Assert.Equal(Js.Graphemes(token), Js.WordBreaks(token));
        }
    }
    [Fact] public void Tokenizers_match_the_note_wrap_regexes()
    {
        foreach (var e in Fx)
        {
            var text = e!["text"]!.GetValue<string>();
            Assert.Equal(Strs(e["tokens"]), Js.SpaceTokens(text));
            Assert.Equal(Strs(e["hanTokens"]), Js.HanTokens(text));
            Assert.Equal(e["han"]!.GetValue<bool>(), Js.HasHan(text));
        }
    }
    [Fact] public void IsHan_and_IsMark_edges()
    {
        Assert.True(Js.IsHan(0x4E00)); Assert.True(Js.IsHan(0x3007)); Assert.True(Js.IsHan(0x20000)); Assert.False(Js.IsHan(0x3042)); Assert.False(Js.IsHan('a'));
        Assert.True(Js.IsMark(0x0301)); Assert.True(Js.IsMark(0xFE0E)); Assert.False(Js.IsMark(0x200D));   // ZWJ is Cf, not a mark
    }

    static readonly JsonObject Wrap = (JsonObject)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/text-wrap.json")))!;
    [Fact] public void TrimStart_TrimEnd_match_V8()
    {
        foreach (var e in Wrap["trim"]!.AsArray()) { var s = e!["s"]!.GetValue<string>(); Assert.Equal(e["trimStart"]!.GetValue<string>(), Js.TrimStart(s)); Assert.Equal(e["trimEnd"]!.GetValue<string>(), Js.TrimEnd(s)); }
    }
    [Fact] public void Tree_and_timeline_splits_match_V8()
    {
        foreach (var e in Wrap["splitOnSpaces"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitOnSpaces(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["splitKeepingSpaces"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitKeepingSpaces(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["cjkWrapTokens"]!.AsArray()) Assert.Equal(Strs(e!["tokens"]), Js.CjkWrapTokens(e["s"]!.GetValue<string>()));
    }
    [Fact] public void Class_signature_splits_match_V8()
    {
        foreach (var e in Wrap["splitSignature"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitSignature(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["splitAfterCommaSpace"]!.AsArray()) Assert.Equal(Strs(e!["parts"]), Js.SplitAfterCommaSpace(e["s"]!.GetValue<string>()));
        foreach (var e in Wrap["splitKeepingCommas"]!.AsArray())
        {
            var parts = Js.SplitKeepingCommas(e!["s"]!.GetValue<string>());
            Assert.Equal(Strs(e["parts"]), parts);
            Assert.Equal(e["isComma"]!.AsArray().Select(x => x!.GetValue<bool>()), parts.Select(Js.IsCommaPart));
        }
    }

    // Probes beyond the fixtures. Expected values captured from Node 2026-10-14 (empty matches at the ends, U+0085/U+FEFF).
    [Fact] public void WsClass_equals_IsSpace_on_every_BMP_unit()
    {
        var re = new System.Text.RegularExpressions.Regex("^" + Js.WsClass + "$", System.Text.RegularExpressions.RegexOptions.ECMAScript);
        for (var c = 0; c <= 0xFFFF; c++) if (c is < 0xD800 or > 0xDFFF) Assert.True(Js.IsSpace(c) == re.IsMatch(((char)c).ToString()), $"U+{c:X4}");
    }
    [Fact] public void Splits_keep_JS_empty_match_edges()
    {
        Assert.Equal(["f("], Js.SplitSignature("f("));
        Assert.Equal(["(", "x"], Js.SplitSignature("(x"));
        Assert.Equal([""], Js.SplitSignature(""));
        Assert.Equal(["a, "], Js.SplitAfterCommaSpace("a, "));
        Assert.Equal([", ", "a"], Js.SplitAfterCommaSpace(", a"));
        Assert.Equal(["", ",", ""], Js.SplitKeepingCommas(","));
        Assert.Equal(["", ", ", "a", ",", ""], Js.SplitKeepingCommas(", a,"));
        Assert.Equal(["", " ", "a", " ", ""], Js.SplitKeepingSpaces(" a "));
        Assert.Equal(["\u0085x", ""], Js.SplitOnSpaces("\u0085x\uFEFF"));
        Assert.False(Js.IsCommaPart(",x\n")); Assert.True(Js.IsCommaPart(",\n"));
        // s.split('-') (render-timeline.mjs:68), Node 24.12: [["2026","02","30"],[""],["","a",""]]
        Assert.Equal(["2026", "02", "30"], Js.SplitOnChar("2026-02-30", '-'));
        Assert.Equal([""], Js.SplitOnChar("", '-'));
        Assert.Equal(["", "a", ""], Js.SplitOnChar("-a-", '-'));
    }

    public static IEnumerable<object[]> WordRows() =>
        JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("js/word-breaks.json")))!.AsArray()
            .Select(r => new object[] { r!["text"]!.GetValue<string>(), r["dictionary"]!.GetValue<bool>(), Js.Stringify(r["words"]) });

    [Theory, MemberData(nameof(WordRows))]
    public void WordBreaks_match_Intl_Segmenter_outside_dictionary_scripts(string text, bool dictionary, string v8Words)
    {
        var words = Js.WordBreaks(text);
        Assert.Equal(text, string.Concat(words));
        if (!dictionary) Assert.Equal(v8Words, Js.Stringify(new JsonArray([.. words.Select(w => (JsonNode?)JsonValue.Create(w))])));
    }

    [Fact] public void WordBreaks_split_dictionary_runs_per_grapheme()
    {
        Assert.Equal(["已", "持", "有", "：", "处", "理"], Js.WordBreaks("已持有：处理"));
        Assert.Equal(["clickstream", " ", "events"], Js.WordBreaks("clickstream events"));
        Assert.Equal(["Archify", "로"], Js.WordBreaks("Archify로"));   // Hangul never joins Latin (ICU tailoring)
    }
}
