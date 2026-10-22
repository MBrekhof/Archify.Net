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
    // GLOB-001: LocaleCompare uses the invariant CompareInfo, which under ICU is CLDR root, as `en` is. Pinned over the strings that
    // reach it: architecture component ids (ArchitectureRenderer), workflow node ids (WorkflowPlacementRepair), brand ids (Suggestions).
    [Fact] public void LocaleCompare_agrees_in_sign_with_en_over_the_ids_it_sees()
    {
        var en = System.Globalization.CultureInfo.GetCultureInfo("en").CompareInfo;
        var words = new List<string> { "a", "A", "b", "B", "z", "Z", "aa", "aB", "Ab", "0", "1", "9", "10", "a1", "a10", "a2", "-", "_", ".",
            "a-b", "a_b", "a.b", "ab", "a-", "_a", ".a", "é", "É", "e", "E", "f", "ø", "Ø", "o", "p", "café", "cafe", "cafë", "中", "文", "日本",
            "中文", "한국", "ア", "a中" };
        static void Ids(JsonNode? n, List<string> into)
        {
            if (n is JsonObject o) foreach (var (k, v) in o) { if (k == "id" && v is JsonValue s && s.TryGetValue<string>(out var id)) into.Add(id); else Ids(v, into); }
            else if (n is JsonArray a) foreach (var v in a) Ids(v, into);
        }
        foreach (var file in Directory.GetFiles(Fixtures.Upstream("examples"), "*.json").Where(f => f.EndsWith(".architecture.json") || f.EndsWith(".workflow.json")))
            Ids(JsonNode.Parse(File.ReadAllText(file)), words);
        words.AddRange(BrandMarks.ListBrandMarks().Select(m => m.Id));
        words = [.. words.Distinct()];
        Assert.True(words.Count > 250, $"{words.Count} strings");   // above 250: the examples and the catalog were found
        var mismatches = new List<string>();
        foreach (var a in words) foreach (var b in words)
            if (Math.Sign(en.Compare(a, b, System.Globalization.CompareOptions.None)) != Math.Sign(Js.LocaleCompare(a, b))) mismatches.Add($"{a} vs {b}");
        Assert.Empty(mismatches);
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
    [Fact] public void Stringify_accepts_clr_backed_numbers()
    {   // node: JSON.stringify({x:5,y:[1,2.5],z:9007199254740993}) === '{"x":5,"y":[1,2.5],"z":9007199254740992}'
        Assert.Equal("{\"x\":5,\"y\":[1,2.5],\"z\":9007199254740992}", Js.Stringify(new JsonObject { ["x"] = 5, ["y"] = new JsonArray(1, 2.5), ["z"] = 9007199254740993L }));
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
    [Fact] public void Waterfall_number_matches_V8()
    {
        foreach (var e in Load("waterfall-number.json")) Assert.Equal(e!["text"]!.GetValue<string>(), WaterfallRenderer.Number(e["value"]!.GetValue<double>()));
    }
    [Fact] public void Waterfall_time_number_matches_V8()
    {
        var rows = Load("waterfall-time-number.json");
        Assert.NotEmpty(rows);
        foreach (var e in rows)
        {
            Assert.Equal(e!["text"]!.GetValue<string>(), WaterfallRenderer.TimeNumber(e["value"]!.GetValue<double>(), e["resolution"]!.GetValue<double>()));
        }
    }
    [Fact] public void Num_handles_raw_json_text_variants()
    {
        Assert.Equal("1", Js.Num(JsonNode.Parse("1.0")!.GetValue<double>()));
        Assert.Equal("100", Js.Num(JsonNode.Parse("1e2")!.GetValue<double>()));
    }

    // Expected values from node: `x || 5`, Math.max/min, String(x).
    [Fact] public void Or_is_js_or_on_numbers()
    {
        Assert.Equal(5, Js.Or(null, 5));
        Assert.Equal(5, Js.Or(0, 5));
        Assert.Equal(5, Js.Or(-0.0, 5));
        Assert.Equal(5, Js.Or(double.NaN, 5));
        Assert.Equal(-3, Js.Or(-3, 5));
        Assert.Equal(2, Js.Or(2, 5));
    }
    [Fact] public void MaxOf_and_MinOf_match_Math_max_and_min()
    {
        Assert.Equal(double.NegativeInfinity, Js.MaxOf([]));
        Assert.Equal(double.PositiveInfinity, Js.MinOf([]));
        Assert.Equal(7, Js.MaxOf([], 7));
        Assert.Equal(7, Js.MinOf([], 7));
        Assert.Equal(9, Js.MaxOf([1, 9, 3], 7));
        Assert.Equal(1, Js.MinOf([1, 9, 3], 7));
        Assert.True(double.IsNaN(Js.MaxOf([1, double.NaN, 3])));
        Assert.True(double.IsNaN(Js.MinOf([1, double.NaN, 3])));
        // Math.max(-0, 0) and Math.max(0, -0) are 0; Math.min of the same pairs is -0, in either order.
        Assert.False(double.IsNegative(Js.MaxOf([-0.0, 0.0])));
        Assert.False(double.IsNegative(Js.MaxOf([0.0, -0.0])));
        Assert.True(double.IsNegative(Js.MinOf([0.0, -0.0])));
        Assert.True(double.IsNegative(Js.MinOf([-0.0, 0.0])));
    }
    [Fact] public void String_is_js_String_and_PrimitiveString_differs_only_on_arrays_and_objects()
    {
        foreach (var (json, expected) in new[] { ("true", "true"), ("false", "false"), ("1.5", "1.5"), ("1e21", "1e+21"), ("-0", "0"), ("\"a\"", "a") })
        {
            Assert.Equal(expected, Js.String(JsonNode.Parse(json)));
            Assert.Equal(expected, Js.PrimitiveString(JsonNode.Parse(json)));
        }
        Assert.Equal("null", Js.String(null));
        Assert.Equal("null", Js.PrimitiveString(null));
        Assert.Equal("1,,b,2,3", Js.String(JsonNode.Parse("[1,null,\"b\",[2,3]]")));
        Assert.Equal("", Js.String(JsonNode.Parse("[]")));
        Assert.Equal("[object Object]", Js.String(JsonNode.Parse("{\"a\":1}")));
        // PrimitiveString JSON-serialises non-primitives (BrandMarks' former JsString); callers rely on it.
        Assert.Equal("[1,null,\"b\",[2,3]]", Js.PrimitiveString(JsonNode.Parse("[1,null,\"b\",[2,3]]")));
        Assert.Equal("{\"a\":1}", Js.PrimitiveString(JsonNode.Parse("{\"a\":1}")));
    }
    [Fact] public void PtOf_reads_a_json_number_pair()
    {
        Assert.Equal(new Pt(1, 2.5), J.PtOf(JsonNode.Parse("[1,2.5,9]")));
        var partial = J.PtOf(JsonNode.Parse("[\"1\",2]"));
        Assert.True(double.IsNaN(partial.X));
        Assert.Equal(2, partial.Y);
        foreach (var json in new[] { "[1]", "null", "{\"x\":1}" })
        {
            var p = J.PtOf(JsonNode.Parse(json));
            Assert.True(double.IsNaN(p.X) && double.IsNaN(p.Y));
        }
        Assert.Equal(3, J.Coord(JsonNode.Parse("3")));
        Assert.True(double.IsNaN(J.Coord(JsonNode.Parse("\"3\""))));
        Assert.True(double.IsNaN(J.Coord(null)));
    }
}
