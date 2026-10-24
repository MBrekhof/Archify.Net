using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

// Pele 1.3.0 core (PORT-008 Task 3) against the unit tables tools/capture-fixtures.mjs mints from the tag source
// (tests/fixtures/js/pele-*.json). Each fact compares every row and lists the first rows that differ.
public class PeleCoreTests
{
    static JsonNode Load(string name) => DiagramLoader.ParseNode(File.ReadAllText(Fixtures.Fixture("js/" + name)))!;
    static string S(JsonNode? n) => n!.GetValue<string>();
    static double D(JsonNode? n) => n!.D();
    static JsonArray A(JsonNode? n) => n!.AsArray();

    static void AllMatch(IEnumerable<(string Row, string Want, string Got)> rows, int least)
    {
        var all = rows.ToList();
        Assert.True(all.Count >= least, $"{all.Count} rows");
        var bad = all.Where(r => r.Want != r.Got).Select(r => $"{r.Row}\n  want {r.Want}\n  got  {r.Got}").ToList();
        Assert.True(bad.Count == 0, $"{bad.Count}/{all.Count} differ:\n{string.Join("\n", bad.Take(20))}");
    }

    // peleTry() in the capture: a thrown PeleError or RangeError is recorded instead of a value.
    static JsonNode? Try(Func<JsonNode?> run)
    {
        try { return run(); }
        catch (PeleException e) { return new JsonObject { ["throws"] = "PeleError", ["message"] = e.Message, ["code"] = e.Code, ["line"] = e.Line }; }
        catch (JsRangeError e) { return new JsonObject { ["throws"] = "RangeError", ["message"] = e.Message }; }
    }

    static string Q(string s) => Js.Quote(s);

    [Fact]
    public void Num_matches_V8()
    {
        AllMatch(A(Load("pele-num.json")).Select(r => (S(r![0]), S(r[1]), Pele.Builder.Num(Js.ToNumber(S(r[0]))))), 200);
    }

    [Fact]
    public void Esc_and_escText_match_V8()
    {
        // UPSTREAM-008: V8's output with the characters XML forbids past the controls (U+FFFE, U+FFFF, lone surrogate halves)
        // taken out, which the port drops too (ChartParityTests.Xml_invalid_characters_are_dropped).
        static string Xml(string s) => ChartParityTests.ReXmlInvalid.Replace(s, "");
        var rows = A(Load("pele-esc.json"));
        Assert.True(rows.Count(r => Xml(S(r!["esc"])) != S(r["esc"])) >= 5);
        AllMatch(rows.SelectMany(r => new[]
        {
            ("esc " + Q(S(r!["s"])), Q(Xml(S(r["esc"]))), Q(Pele.Builder.Esc(S(r["s"])))),
            ("escText " + Q(S(r["s"])), Js.Stringify(r["escText"] is JsonValue v && v.TryGetValue<string>(out var t) ? JsonValue.Create(Xml(t)) : r["escText"]), Js.Stringify(Try(() => Pele.Builder.EscText(S(r["s"]))))),
        }), 120);
    }

    [Fact]
    public void Entities_match_V8()
    {
        var t = Load("pele-entities.json");
        AllMatch(A(t["encode"]).Select(r => ("encode " + Q(S(r![0])), Q(S(r[1])), Q(Pele.Preprocess.EncodeEntities(S(r[0]))))), 25);
        AllMatch(A(t["decode"]).Select(r => ("decode " + Q(S(r![0])), Js.Stringify(r[1]), Js.Stringify(Try(() => Pele.Entities.DecodeEntities(S(r[0])))))), 30);
    }

    [Fact]
    public void Measurer_matches_the_metrics_table()
    {
        AllMatch(A(Load("pele-measure.json")).Select(r =>
            ($"{Q(S(r![0]))} {D(r[1])} {D(r[2])}", Js.Num(D(r[3])), Js.Num(Pele.Measurer.Width(S(r[0]), D(r[1]), (Pele.Style)(int)D(r[2]))))), 500);
    }

    [Fact]
    public void DetectType_matches_every_detector()
    {
        AllMatch(A(Load("pele-detect.json")).Select(r => (Q(S(r![0])), r[1]?.GetValue<string>() ?? "null", Pele.DetectType(S(r[0])) ?? "null")), 80);
    }

    // Object key order in config is not load-bearing (read by key only), but JSON.stringify writes V8's order: integer-like
    // keys first, ascending. The port keeps insertion order, so the comparison rewrites it the V8 way.
    static JsonNode? V8Order(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => kv.Key)
            .OrderBy(k => uint.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i != uint.MaxValue && i.ToString(CultureInfo.InvariantCulture) == k ? i : uint.MaxValue)
            .Select(k => KeyValuePair.Create(k, V8Order(o[k])))),
        JsonArray a => new JsonArray([.. a.Select(V8Order)]),
        null => null,
        _ => node.DeepClone(),
    };

    static string PreprocessJson(string input)
    {
        Preprocessed p;
        try { p = Pele.Preprocess.Run(input); }
        catch (PeleException e) { return Js.Stringify(Try(() => throw e)); }
        var o = new JsonObject { ["text"] = p.Text };
        if (p.Title is not null) o["title"] = p.Title;
        o["config"] = V8Order(p.Config);
        o["withComments"] = p.WithComments;
        o["lineOffset"] = p.LineOffset;
        return Js.Stringify(o);
    }

    [Fact]
    public void Preprocess_matches_V8()
    {
        AllMatch(A(Load("pele-preprocess.json")).Select(r => (S(r!["name"]), S(r["out"]), PreprocessJson(S(r["input"])))), 60);
    }

    // The capture's yamlEnc: every number as {num} (Infinity, NaN and -0 survive), keys in V8 order.
    static JsonNode? YamlEnc(JsonNode? node) => V8Order(node) switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, YamlEnc(kv.Value?.DeepClone())))),
        JsonArray a => new JsonArray([.. a.Select(x => YamlEnc(x?.DeepClone()))]),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => new JsonObject { ["num"] = NumIn(v.D()) },
        var other => other,
    };
    static string NumIn(double d) => d == 0 && double.IsNegative(d) ? "-0" : Js.Num(d);

    [Fact]
    public void Yaml_matches_V8()
    {
        AllMatch(A(Load("pele-yaml.json")).Select(r =>
        {
            var src = S(r![0]);
            string got;
            try { got = Js.Stringify(YamlEnc(Pele.Yaml.Parse(src))); }
            catch (Pele.Yaml.YamlError e) { got = Js.Stringify(new JsonObject { ["error"] = e.Message }); }
            return (Q(src), S(r[1]), got);
        }), 330);
    }

    // Front-matter config keeps .inf, -.inf, .nan and -0 as doubles (later chart code tests `typeof === 'number'`); the
    // JSON.stringify golden shows them only as null and 0.
    [Fact]
    public void Front_matter_config_keeps_non_finite_numbers_and_negative_zero()
    {
        var c = Pele.Preprocess.Run("---\nconfig:\n  a: .inf\n  b: -.inf\n  c: .nan\n  d: -0\n  n:\n    e: [.NaN]\n---\npie").Config;
        static double Num(JsonNode? n) { Assert.Equal(System.Text.Json.JsonValueKind.Number, n!.GetValueKind()); return n.D(); }
        Assert.Equal(double.PositiveInfinity, Num(c["a"]));
        Assert.Equal(double.NegativeInfinity, Num(c["b"]));
        Assert.True(double.IsNaN(Num(c["c"])));
        Assert.True(Num(c["d"]) == 0 && double.IsNegative(Num(c["d"])));
        Assert.True(double.IsNaN(Num(c["n"]!["e"]![0])));
    }

    // Deviation (PORT-008 Task 11): V8 merges a directive nested 300 levels deep; the port's JSON depth cap (256) ignores it.
    [Fact]
    public void Deep_directive_is_the_stated_deviation()
    {
        var t = Load("pele-preprocess-deep.json");
        Assert.True(D(t["merged"]) > 1000);   // Node merged the whole nest
        var p = Pele.Preprocess.Run(S(t["input"]));
        Assert.Equal(S(t["text"]), p.Text);
        Assert.Empty(p.Config);
        Assert.True(Pele.Preprocess.JsonDepth < 300);
    }

    static string LabelJson(Pele.Label label) => Js.Stringify(new JsonObject
    {
        ["lines"] = new JsonArray([.. label.Lines.Select(line => (JsonNode)new JsonArray([.. line.Select(span =>
            (JsonNode)new JsonObject { ["text"] = span.Text, ["style"] = span.Style, ["width"] = span.Width })]))]),
        ["widths"] = new JsonArray([.. label.Widths.Select(w => (JsonNode)w)]),
        ["width"] = label.Width,
        ["height"] = label.Height,
        ["size"] = label.Size,
        ["lineHeight"] = label.LineHeight,
    });

    [Fact]
    public void LayoutLabel_matches_V8()
    {
        AllMatch(A(Load("pele-label.json")).Select(r =>
            ($"{Q(S(r![0]))} md={r[1]} size={D(r[2])} max={D(r[3])} base={D(r[4])}", S(r[5]),
             LabelJson(Pele.Labels.LayoutLabel(S(r[0]), r[1]!.GetValue<bool>(), D(r[2]), D(r[3]), (int)D(r[4]))))), 50);
    }

    static readonly PeleOptions Options = new(8, 13, false, false, []);

    [Fact]
    public void Svg_writer_matches_V8()
    {
        var t = Load("pele-svg.json");
        var c = t["constants"]!;
        Assert.Equal(Pele.Theme.RootStyle, S(c["ROOT_STYLE"]));
        Assert.Equal(Pele.Theme.Radius, S(c["RADIUS"]));
        Assert.Equal(Pele.Theme.Critical, S(c["CRITICAL"]));
        AllMatch(A(t["documents"]).Select(r => (Q(S(r![7])), S(r[8]), Pele.Root.SvgDocument(S(r[0]), D(r[1]), D(r[2]), D(r[3]),
            new PeleRenderContext(Options, r[4]!.GetValue<bool>()), S(r[5]), S(r[6]), S(r[7])))), 9);
        AllMatch(A(t["labels"]).Select(r => (Q(S(r![0])), S(r[8]), Pele.Builder.LabelSvg(
            Pele.Labels.LayoutLabel(S(r[0]), r[1]!.GetValue<bool>(), D(r[2]), D(r[3]), (int)D(r[4])), D(r[5]), D(r[6]), S(r[7])))), 13);
        AllMatch(A(t["styles"]).Select(r =>
        {
            var decls = A(r![0]).Select(S).ToList();
            var s = Pele.Theme.ResolveStyle(decls);
            var o = new JsonObject { ["shape"] = s.Shape, ["line"] = s.Line, ["text"] = s.Text };
            if (s.Stroke is not null) o["stroke"] = s.Stroke;
            o["bold"] = s.Bold;
            o["italic"] = s.Italic;
            if (s.FontSize is { } px) o["fontSize"] = px;
            return (Js.Stringify(r[0]), S(r[1]), Js.Stringify(o));
        }), 40);
        AllMatch(A(t["classNames"]).Select(r => (Q(S(r![0])), S(r[1]), Pele.Theme.ClassNames(S(r[0])))), 9);
        AllMatch(A(t["seriesColor"]).Select(r => (Js.Num(D(r![0])), S(r[1]), Pele.Theme.SeriesColor((int)D(r[0])))), 27);
        AllMatch(A(t["fontStyle"]).Select(r => (Js.Num(D(r![0])), S(r[1]), Pele.Theme.FontStyle(D(r[0])))), 3);
    }

    // The capture's TEST_TOKENS: a literal with `longer`, a keyword, a `match` scanner, a `scanner` factory and every shared token.
    static readonly TokenType Id = new() { Name = "ID", Pattern = Pele.Tokens.Sticky("[a-z]+"), First = "abcdefghijklmnopqrstuvwxyz" };
    static int Digits(string src, int at) { var i = at; while (i < src.Length && src[i] is >= '0' and <= '9') i++; return i > at ? i : -1; }
    static readonly TokenType[] TestTokens =
    [
        new() { Name = "pie", Literal = "pie", Longer = [Id] }, Pele.Tokens.Keyword("showData"), new() { Name = ":", Literal = ":" },
        new() { Name = "NUM", Pattern = Pele.Tokens.Sticky("[0-9]+"), Match = Digits, First = "0123456789" },
        Pele.Tokens.AccDescr, Pele.Tokens.AccTitle, Pele.Tokens.Title, Pele.Tokens.String, Pele.Tokens.Newline,
        Pele.Tokens.Whitespace, Pele.Tokens.Yaml, Pele.Tokens.Directive, Pele.Tokens.SingleLineComment, Id,
        new() { Name = "HASH", Pattern = Pele.Tokens.Sticky("#[0-9]+"), Scanner = () => (src, at) => at < src.Length && src[at] == '#' && Digits(src, at + 1) != -1 ? Digits(src, at + 1) : -1, First = "#" },
    ];

    static JsonArray Ints(List<int> list) => new([.. list.Select(i => (JsonNode)i)]);

    [Fact]
    public void Tokens_and_reader_match_V8()
    {
        var t = Load("pele-tokens.json");
        AllMatch(A(t["tokenize"]).Select(r => (Q(S(r![0])), Js.Stringify(r[1]), Js.Stringify(Try(() =>
        {
            var tokens = Pele.Tokens.Tokenize(S(r[0]), TestTokens, "test");
            return new JsonObject { ["kinds"] = Ints(tokens.Kinds), ["starts"] = Ints(tokens.Starts), ["ends"] = Ints(tokens.Ends) };
        })))), 35);
        AllMatch(A(t["reader"]).Select(r => (Q(S(r![0])), Js.Stringify(r[1]), Js.Stringify(Try(() =>
        {
            var reader = new Pele.Reader(Pele.Tokens.Tokenize(S(r[0]), TestTokens, "test"), TestTokens, "test");
            var output = new JsonArray(reader.Expect(0));
            output.Add(reader.Accept(1) ? "true" : "false");
            output.Add(reader.Expect(2));
            output.Add(reader.Expect(3));
            return output;
        })))), 8);
        AllMatch(A(t["titleValue"]).Select(r => (Q(S(r![0])), S(r[1]), Pele.Tokens.TitleValue(S(r[0])))), 5);
        AllMatch(A(t["accTitleValue"]).Select(r => (Q(S(r![0])), S(r[1]), Pele.Tokens.AccTitleValue(S(r[0])))), 3);
        AllMatch(A(t["accDescrValue"]).Select(r => (Q(S(r![0])), Q(S(r[1])), Q(Pele.Tokens.AccDescrValue(S(r[0]))))), 9);
        AllMatch(A(t["stringValue"]).Select(r => (Q(S(r![0])), Q(S(r[1])), Q(Pele.Tokens.StringValue(S(r[0]))))), 9);
        AllMatch(A(t["locate"]).Select(r =>
        {
            var (line, column, snippet) = Pele.Errors.Locate(S(r![0]), (int)D(r[1]));
            return ($"{Q(S(r[0]))} @{D(r[1])}", Js.Stringify(r[2]), Js.Stringify(new JsonObject { ["line"] = line, ["column"] = column, ["snippet"] = snippet }));
        }), 9);
        AllMatch(A(t["syntaxError"]).Select(r =>
        {
            var e = Pele.Errors.SyntaxError(S(r![0]), S(r[1]), (int)D(r[2]), S(r[3]), r[4]!.GetValue<bool>());
            return (Q(S(r[1])), Js.Stringify(r[5]), Js.Stringify(new JsonObject { ["message"] = e.Message, ["code"] = e.Code, ["line"] = e.Line }));
        }), 3);
        AllMatch(A(t["wordAt"]).Select(r => ($"{Q(S(r![0]))} {D(r[1])} {Q(S(r[2]))}", Js.Stringify(r[3]),
            Pele.Caseless.WordAt(S(r[0]), (int)D(r[1]), S(r[2])) ? "true" : "false")), 12);
        var fit = t["fit"]!;
        Assert.Equal(Pele.FitWidth.TitleRoom, D(fit["titleRoom"]));
        Assert.Equal(D(fit["capWidth"]), Pele.FitWidth.CapWidth(480));
        Assert.Equal(S(fit["fitWidth"]), Pele.FitWidth.Fit(480, (plot, narrow) => $"{Js.Num(plot)}|{(narrow ? "true" : "false")}"));
    }

    // core.ts parseSource/render: the limits and the unsupported-diagram errors, as core.ts words them. gitGraph is detected
    // but archify never registers it.
    [Theory]
    [InlineData("gitGraph\n  commit", "Diagram type \"gitGraph\" is not supported yet.", "unsupported-diagram")]
    [InlineData("hello", "No diagram type detected for the given text.", "unsupported-diagram")]
    [InlineData("ﬂ°°1.5¶ß", "No diagram type detected for the given text.", "unsupported-diagram")]
    public void Render_reports_core_errors(string text, string message, string code)
    {
        var e = Assert.Throws<PeleException>(() => Pele.Render(text, Options));
        Assert.Equal((message, code, 0), (e.Message, e.Code, e.Line));
    }

    [Fact]
    public void Render_limits_the_source_to_50000_characters()
    {
        var e = Assert.Throws<PeleException>(() => Pele.Render("gitGraph\n" + new string('x', 49_992), Options));
        Assert.Equal(("Diagram source is longer than the limit of 50000 characters.", "limit"), (e.Message, e.Code));
        var at = Assert.Throws<PeleException>(() => Pele.Render("gitGraph\n" + new string('x', 49_991), Options));
        Assert.Equal("Diagram type \"gitGraph\" is not supported yet.", at.Message);
    }
}
