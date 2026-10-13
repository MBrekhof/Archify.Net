using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class JsTests
{
    static JsonArray Load(string f) => (JsonArray)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture($"js/{f}")))!;
    static double Val(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s == "-0" ? -0.0 : n.GetValue<double>();

    [Fact] public void Num_matches_V8() { foreach (var e in Load("numbers.json")) Assert.Equal(e!["str"]!.GetValue<string>(), Js.Num(Val(e["value"]!))); }
    [Fact] public void Round_matches_V8() { foreach (var e in Load("numbers.json")) Assert.Equal(e!["round"]!.GetValue<double>(), Js.Round(Val(e["value"]!))); }
    [Fact] public void ToFixed_matches_V8() { foreach (var e in Load("numbers.json")) { var v = Val(e!["value"]!); Assert.Equal(e["fixed2"]!.GetValue<string>(), Js.ToFixed(v, 2)); Assert.Equal(e["fixed6"]!.GetValue<string>(), Js.ToFixed(v, 6)); } }
    [Fact] public void ToPrecision_matches_V8() { foreach (var e in Load("numbers.json")) Assert.Equal(e!["prec3"]!.GetValue<string>(), Js.ToPrecision(Val(e["value"]!), 3)); }
    [Fact] public void ToLocaleString_enUS_matches_V8() { foreach (var e in Load("numbers.json")) { var v = Val(e!["value"]!);
        Assert.Equal(e["loc3sig"]!.GetValue<string>(), Js.ToLocaleStringEnUs(v, maximumSignificantDigits: 3));
        Assert.Equal(e["loc2frac"]!.GetValue<string>(), Js.ToLocaleStringEnUs(v, maximumFractionDigits: 2)); } }

    [Fact] public void Hypot_matches_V8()
    {
        foreach (var e in Load("hypot.json")) { var h = Js.Hypot(e!["a"]!.GetValue<double>(), e["b"]!.GetValue<double>()); Assert.Equal(e["hypot"]!.GetValue<double>(), h); Assert.Equal(e["str"]!.GetValue<string>(), Js.Num(h)); }
    }
    [Fact] public void ToNumber_and_ParseFloat_match_V8()
    {
        foreach (var e in Load("to-number.json")) { var s = e!["s"]!.GetValue<string>(); Assert.Equal(e["number"]!.GetValue<string>(), Js.Num(Js.ToNumber(s))); Assert.Equal(e["parseFloat"]!.GetValue<string>(), Js.Num(Js.ParseFloat(s))); }
    }
    [Fact] public void SortDefault_is_utf16_ordinal()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/sort.json")))!;
        var words = fx["words"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Js.SortDefault(words);
        Assert.Equal(fx["defaultSorted"]!.AsArray().Select(x => x!.GetValue<string>()), words);
    }
    [Fact] public void Sort_is_stable_and_honours_fractional_comparators()
    {
        var items = new List<(double k, string v)> { (1, "a"), (0, "b"), (1, "c"), (0, "d"), (1, "e") };
        Js.Sort(items, (x, y) => x.k - y.k);
        Assert.Equal(new[] { "b", "d", "a", "c", "e" }, items.Select(i => i.v));
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/sort.json")))!;
        var frac = new List<(double k, string v)> { (1.25, "a"), (0.75, "b"), (1.0, "c"), (0.5, "d"), (double.NaN, "e") };
        Js.Sort(frac, (x, y) => x.k - y.k);
        Assert.Equal(fx["fractional"]!.AsArray().Select(x => x!.GetValue<string>()), frac.Select(i => i.v));
    }
    [Fact] public void LocaleCompare_sign_matches_V8()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/sort.json")))!;
        var words = fx["words"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        var m = fx["localeCompare"]!.AsArray();
        for (var i = 0; i < words.Length; i++) for (var j = 0; j < words.Length; j++)
            Assert.Equal(Math.Sign(m[i]![j]!.GetValue<int>()), Math.Sign(Js.LocaleCompare(words[i], words[j])));
    }
    [Fact] public void Stringify_matches_V8()
    {
        foreach (var e in Load("stringify.json"))
        {
            var input = e!["input"]?.DeepClone();
            Assert.Equal(e["compact"]!.GetValue<string>(), Js.Stringify(input));
            Assert.Equal(e["indented"]!.GetValue<string>(), Js.Stringify(input, 2));
        }
    }
    [Fact] public void Quote_escapes_lone_surrogates_lowercase_and_keeps_pairs()
    {   // JSON files cannot carry a lone surrogate, so this one is a C# literal (verified against V8: JSON.stringify("\ud800 x") === '"\\ud800 x"')
        Assert.Equal("\"\\ud800 x\"", Js.Quote("\ud800 x"));
        Assert.Equal("\"\\udc00\"", Js.Quote("\udc00"));
        Assert.Equal("\"😀\"", Js.Quote("😀"));
        Assert.Equal("\"\u2028\u2029\u007f\"", Js.Quote("\u2028\u2029\u007f"));   // literal, not escaped
    }
    [Fact] public void Trim_and_IsSpace_match_V8()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/strings.json")))!;
        foreach (var p in fx["trim"]!.AsArray()) Assert.Equal(p![1]!.GetValue<string>(), Js.Trim(p[0]!.GetValue<string>()));
        foreach (var p in fx["isSpaceRegex"]!.AsArray()) Assert.Equal(p![1]!.GetValue<bool>(), Js.IsSpace(char.ConvertToUtf32(p[0]!.GetValue<string>(), 0)));
        Assert.Equal(fx["replaceFirst"]![0]!.GetValue<string>(), Js.ReplaceFirst("a-b-c", "-", _ => "$&"));
    }
    [Fact] public void EscapeHtml_only_five_chars() => Assert.Equal("&amp;&lt;&gt;&quot;&#39;+/é", Js.EscapeHtml("&<>\"'+/é"));
    [Fact] public void JsMap_keeps_insertion_order_across_delete_and_readd()
    {
        var m = new JsMap<string, int>(); m.Set("a", 1); m.Set("b", 2); m.Set("c", 3); m.Delete("a"); m.Set("a", 4);
        Assert.Equal(new[] { "b", "c", "a" }, m.Keys);
        var s = new JsSet<int>(); s.Add(3); s.Add(1); s.Add(2); s.Delete(3); s.Add(3);
        Assert.Equal(new[] { 1, 2, 3 }, s);
    }
    [Fact] public void Num_handles_raw_json_text_variants()
    {
        Assert.Equal("1", Js.Num(JsonNode.Parse("1.0")!.GetValue<double>()));
        Assert.Equal("100", Js.Num(JsonNode.Parse("1e2")!.GetValue<double>()));
    }
}
