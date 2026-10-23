using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
namespace Archify.Net.Tests;

// PORT-008: ExplainerCharts against goldens Node mints through upstream charts.mjs (tools/capture-fixtures.mjs, charts
// section). Seeds and layout: tools/chart-seeds/README.md.
public class ChartParityTests
{
    // A group is compared once it is listed; each type task adds its own.
    static readonly string[] Groups = ["data", "wrapper", "pie", "xychart", "quadrantChart"];
    // Rows whose golden needs a later type task, keyed by that type, filtered out of the theory data (xunit 2.9.3 has no
    // dynamic skip). The type's task removes its entry.
    static readonly Dictionary<string, HashSet<(string Group, string Name)>> PendingUntil = [];
    // Rows that differ from Node by a stated deviation (PORT-008 Task 11, docs/deviations.md), each pinned by its own fact below.
    static readonly HashSet<(string Group, string Name)> KnownDeviations =
    [
        ("data", "json-deep-unread-column"), ("wrapper", "native-treeview"),
        ("xychart", "pow-clamped-equal"), ("xychart", "pow-clamped-equal-negative"),
        ("quadrantChart", "phantom-empty-quadrant-label"), ("quadrantChart", "phantom-empty-point-label"), ("quadrantChart", "stress-points"),
        .. QuadrantQuirkRows.Select(row => ("quadrantChart", row.Name)),
    ];
    // Fuzz cases that differ from Node by a stated deviation: index -> the quirk that explains it, checked by
    // Fuzz_deviations_are_the_stated_quirks.
    static readonly Dictionary<string, Dictionary<int, string>> FuzzDeviations = new()
    {
        ["quadrantChart"] = new() {
            [64] = "Q3", [78] = "Q3", [106] = "Q2", [131] = "Q2", [134] = "Q2", [177] = "Q3", [245] = "Q2", [277] = "Q2",
            [279] = "Q3", [309] = "Q2", [317] = "Q3", [330] = "Q3", [332] = "Q3", [371] = "Q1", [393] = "Q3", [401] = "Q2",
            [456] = "Q1Q3", [489] = "Q2", [607] = "Q3", [656] = "Q2", [667] = "Q3", [730] = "Q2Q3", [739] = "Q3", [740] = "Q3",
            [748] = "Q3", [796] = "Q3", [797] = "Q3", [827] = "Q3", [855] = "Q3", [871] = "Q1", [882] = "Q3", [894] = "Q3",
            [900] = "Q2", [927] = "Q3", [928] = "Q1Q3", [971] = "Q3", [984] = "Q2Q3", [995] = "Q3",
        },
    };

    public static TheoryData<string, string> Targets()
    {
        var data = new TheoryData<string, string>();
        foreach (var group in Groups)
        {
            foreach (var seed in SeedsOf(group))
            {
                var name = seed!["name"]!.GetValue<string>();
                if (!PendingUntil.Values.Any(rows => rows.Contains((group, name))) && !KnownDeviations.Contains((group, name))) data.Add(group, name);
            }
            if (File.Exists(SeedFile($"{group}.fuzz.json"))) data.Add(group, "fuzz");   // the capture refuses a seed named "fuzz"
        }
        return data;
    }

    [Theory, MemberData(nameof(Targets))]
    public void Matches_node(string group, string name)
    {
        var golden = Fixtures.Lf(Golden(group, name));
        if (name == "fuzz")
        {
            if (FuzzFailure(golden, RunText, FuzzDeviations.GetValueOrDefault(group)) is { } failure) Assert.Fail(failure);
            return;
        }
        Assert.Equal(golden, Run(Seed(group, name)) + "\n");
    }

    // ExplainerCharts.JsonDepth: V8 parses the 300-level column nobody reads and draws the chart; the port stops at 256 levels.
    [Fact] public void Deep_data_json_is_the_stated_deviation()
    {
        Assert.Contains("\"text\": \"xychart-beta", Fixtures.Lf(Golden("data", "json-deep-unread-column")));
        Assert.Equal("{\n  \"error\": \"The data file is not valid JSON.\"\n}", Run(Seed("data", "json-deep-unread-column")));
    }

    // UPSTREAM-003: charts.mjs:11 keys NATIVE by `treeview`, but Pele detects `treeView`, so upstream never redirects a
    // treeView-beta fence. The port keys it `treeView` (Martin, 2026-10-23) and redirects it like every other native type.
    [Fact] public void TreeView_redirect_is_the_stated_deviation()
    {
        Assert.Contains("A chart fence supports", Fixtures.Lf(Golden("wrapper", "native-treeview")));
        const string port = "{\n  \"error\": \"Mermaid treeView is drawn with the native tree shorthand, which Archify validates and lets readers explore.\",\n  \"example\": \"@tree id=...\"\n}";
        Assert.Equal(port, Run(Seed("wrapper", "native-treeview")));
        Assert.Equal(port, RunText("treeView-beta\n  a\n    b"));
    }

    // xychart render.ts: upstream tests an exact y range for `lo !== hi` before clamping it to ±1e300, so `1e301 --> 1e302`
    // collapses to one value and divides by zero: 100 ticks at NaN. The port tests the clamped ends and falls back to the
    // data, as a collapsed range does, so both rows draw what Node draws for `y-axis 5 --> 5`.
    [Fact] public void Clamped_equal_y_range_is_the_stated_deviation()
    {
        var reference = Fixtures.Lf(Golden("xychart", "pow-clamped-equal-reference"));
        Assert.DoesNotContain("NaN", reference);
        foreach (var name in new[] { "pow-clamped-equal", "pow-clamped-equal-negative" })
        {
            Assert.Contains("M40,NaNH472", Fixtures.Lf(Golden("xychart", name)));
            Assert.Equal(reference, Run(Seed("xychart", name)) + "\n");
        }
    }

    // quadrant render.ts:96,179: upstream reserves a zero-size "taken" box for a label with no text (an empty quadrant, a
    // point label that trims to nothing), so a point label whose box holds that invisible spot is pushed away from empty
    // space. The port reserves boxes only for drawn labels.
    [Fact] public void Empty_label_reserves_no_space_is_the_stated_deviation()
    {
        // Empty quadrant-1: P goes right in Node. The reference moves only the invisible anchor (quadrantTextTopPadding: 100,
        // same 400x400 size), and Node then draws P above; the port draws that reference byte for byte.
        var quadrant = Fixtures.Lf(Golden("quadrantChart", "phantom-empty-quadrant-label"));
        Assert.Contains("x=\\\"309.33\\\" y=\\\"36.46\\\" text-anchor=\\\"middle\\\">P<", quadrant);
        var reference = Fixtures.Lf(Golden("quadrantChart", "phantom-empty-quadrant-label-reference"));
        Assert.Contains("x=\\\"296\\\" y=\\\"20.46\\\" text-anchor=\\\"middle\\\">P<", reference);
        Assert.Equal(reference, Run(Seed("quadrantChart", "phantom-empty-quadrant-label")) + "\n");

        // A point labelled " " above P: Node sends P right; the port draws P where Node draws it without that point (the
        // control), and everything else as Node does.
        const string right = "<text class=\\\"pele-label\\\" x=\\\"117.34\\\" y=\\\"203.5\\\" text-anchor=\\\"middle\\\">P</text>";
        const string above = "<text class=\\\"pele-label\\\" x=\\\"104\\\" y=\\\"187.5\\\" text-anchor=\\\"middle\\\">P</text>";
        var point = Fixtures.Lf(Golden("quadrantChart", "phantom-empty-point-label"));
        Assert.Contains(right, point);
        Assert.Contains(above, Fixtures.Lf(Golden("quadrantChart", "phantom-empty-point-label-control")));
        Assert.Equal(point.Replace(right, above), Run(Seed("quadrantChart", "phantom-empty-point-label")) + "\n");

        // stress-points (250 points, no quadrant text) moves some labels off the empty quadrants' anchors; only the label group
        // differs from Node.
        var stress = Fixtures.Lf(Golden("quadrantChart", "stress-points"));
        var port = Run(Seed("quadrantChart", "stress-points")) + "\n";
        Assert.NotEqual(stress, port);
        Assert.Equal(OutsidePointLabels(stress), OutsidePointLabels(port));
    }

    // Mermaid grammar slips Pele mirrors (quadrant.jison), fixed in the port:
    //   Q1 `[^\}]\%\%[^\n]*` takes the character before a comment with it;
    //   Q2 the coordinate `0(.\d+)?` has an unescaped dot, so `0x1` is 1 and `0,5` drops the point;
    //   Q3 keywords end without a word boundary and fire anywhere in a line; the port takes a keyword only as a whole word
    //      leading a statement, and a statement in point form (`Title: [0.5, 0.5]`) is a point.
    // Each row's port output equals the Node golden of its reference row: the same input written so that upstream already
    // reads it the corrected way, or (Q2) a coordinate upstream already rejects, at the same line.
    static (string Name, string Quirk, string Reference)[] QuadrantQuirkRows =>
    [
        ("quirk-comment-eats-char", "Q1", "quirk-comment-eats-char-reference"),
        ("quirk-comment-eats-style-digit", "Q1", "quirk-comment-eats-style-digit-reference"),
        ("comment-glued", "Q1", "comment-glued-reference"),
        ("stress-comment-markers", "Q1", "stress-comment-markers-reference"),
        ("quirk-coordinate-hex", "Q2", "quirk-coordinate-reference"),
        ("quirk-coordinate-exponent", "Q2", "quirk-coordinate-reference"),
        ("quirk-coordinate-comma", "Q2", "quirk-coordinate-reference"),
        ("quirk-coordinate-semicolon", "Q2", "quirk-coordinate-reference"),
        ("coord-binary", "Q2", "quirk-coordinate-reference"),
        ("coord-comma-dot", "Q2", "quirk-coordinate-reference"),
        ("coord-exponent", "Q2", "quirk-coordinate-reference"),
        ("coord-hex-one", "Q2", "quirk-coordinate-reference"),
        ("coord-zero-then-space", "Q2", "quirk-coordinate-reference"),
        ("render-extreme-coordinates", "Q2", "quirk-coordinate-reference"),
        ("render-not-a-number", "Q2", "quirk-coordinate-reference"),
        ("place-out-of-range-not-counted", "Q2", "place-out-of-range-reference"),
        ("quirk-keyword-x-axis-name", "Q3", "quirk-keyword-x-axis-name-reference"),
        ("quirk-keyword-y-axis-name", "Q3", "quirk-keyword-y-axis-name-reference"),
        ("quirk-keyword-quadrant-name", "Q3", "quirk-keyword-quadrant-name-reference"),
        ("quirk-keyword-chart-name", "Q3", "quirk-keyword-chart-name-reference"),
        ("quirk-keyword-in-text", "Q3", "quirk-keyword-in-text-reference"),
        ("keyword-glued", "Q3", "keyword-glued-reference"),
        ("keyword-x-axis-glued", "Q3", "keyword-x-axis-glued-reference"),
        ("quirk-comment-eats-after-dot", "Q1", "quirk-comment-eats-after-dot-reference"),
        ("quirk-comment-eats-digit", "Q1", "quirk-comment-eats-digit-reference"),
        ("quirk-coordinate-y-hex", "Q2", "quirk-coordinate-reference"),
        ("keyword-whole-word-in-text", "Q3", "keyword-whole-word-in-text-reference"),
        ("quirk-keyword-quadrant-1x", "Q3", "quirk-keyword-quadrant-1x-reference"),
        ("quirk-keyword-quadrant-12", "Q3", "quirk-keyword-quadrant-12-reference"),
        ("quirk-keyword-mid-quadrant", "Q3", "quirk-keyword-mid-quadrant-reference"),
        ("quirk-keyword-mid-x-axis", "Q3", "quirk-keyword-mid-x-axis-reference"),
        ("quirk-keyword-mid-classdef", "Q3", "quirk-keyword-mid-classdef-reference"),
        ("quirk-keyword-title-point", "Q3", "quirk-keyword-title-point-reference"),
        ("quirk-keyword-acc-title-point", "Q3", "quirk-keyword-acc-title-point-reference"),
        ("quirk-keyword-x-axis-point", "Q3", "quirk-keyword-x-axis-point-reference"),
        ("quirk-keyword-quadrant-point", "Q3", "quirk-keyword-quadrant-point-reference"),
        ("quirk-keyword-mid-quadrant-text", "Q3", "quirk-keyword-mid-quadrant-text-reference"),
        ("quirk-keyword-mid-axis-text", "Q3", "quirk-keyword-mid-axis-text-reference"),
        ("quirk-keyword-quadrant-1st-text", "Q3", "quirk-keyword-quadrant-1st-text-reference"),
    ];

    [Fact] public void Quadrant_grammar_slips_are_the_stated_deviations()
    {
        foreach (var (name, quirk, reference) in QuadrantQuirkRows)
        {
            var node = Fixtures.Lf(Golden("quadrantChart", name));
            var expected = Fixtures.Lf(Golden("quadrantChart", reference));
            Assert.True(node != expected, $"{name}: Node already draws the corrected output");
            Assert.True(QuirkIn(Seed("quadrantChart", name)["text"]!.GetValue<string>()).Contains(quirk), $"{name}: input shows no {quirk}");
            Assert.Equal(expected, Run(Seed("quadrantChart", name)) + "\n");
        }
        // `}%%` keeps its exemption: Node and the port agree (a lexical error at `}`).
        Assert.Contains("quadrantChart/quirk-comment-brace-exempt", Targets().Select(row => $"{row[0]}/{row[1]}"));
    }

    // Which quirks an input shows: Q1 a character other than `}` before `%%`; Q2 a coordinate `0` followed by a character
    // other than a dot and then a digit; Q3 a keyword followed by a word character, after other text in its statement, or
    // leading a statement in point form.
    static readonly Regex ReQ1 = new("[^}]%%");
    static readonly Regex ReQ2 = new(@"(?<=[\[,\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff])0[^.\n\r\u2028\u2029][0-9]");
    static readonly Regex ReKeyword = new("[Xx]-[Aa][Xx][Ii][Ss]|[Yy]-[Aa][Xx][Ii][Ss]|[Qq][Uu][Aa][Dd][Rr][Aa][Nn][Tt]-[1-4]|[Qq][Uu][Aa][Dd][Rr][Aa][Nn][Tt][Cc][Hh][Aa][Rr][Tt]|[Cc][Ll][Aa][Ss][Ss][Dd][Ee][Ff]|[Tt][Ii][Tt][Ll][Ee]|[Aa][Cc][Cc](?:[Tt][Ii][Tt][Ll][Ee]|[Dd][Ee][Ss][Cc][Rr])");
    const string Sp = @"[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*";
    const string Coord = @"(?:1|0(?:\.[0-9]+)?)";
    static readonly Regex RePointForm = new($@"\A[^\n\r;]*?:{Sp}\[{Sp}{Coord}{Sp},{Sp}{Coord}{Sp}\]");
    static bool KeywordQuirk(string text)
    {
        foreach (Match m in ReKeyword.Matches(text))
        {
            var after = m.Index + m.Length < text.Length ? text[m.Index + m.Length] : '\0';
            if (char.IsAsciiLetterOrDigit(after) || after == '_') return true;
            var from = Math.Max(text.LastIndexOfAny(['\n', '\r', ';'], Math.Max(m.Index - 1, 0)) + 1, 0);
            if (m.Index > 0 && text[from..m.Index].Trim(' ', '\t').Length > 0) return true;
            if (!m.Value.StartsWith("quadrantchart", StringComparison.OrdinalIgnoreCase) && RePointForm.IsMatch(text[m.Index..])) return true;
        }
        return false;
    }
    static string QuirkIn(string text) =>
        string.Concat(ReQ1.IsMatch(text) ? "Q1" : "", ReQ2.IsMatch(text) ? "Q2" : "", KeywordQuirk(text) ? "Q3" : "");

    // The SVG with its point-label group left out: round 1's empty-label fix changes only that group.
    static string OutsidePointLabels(string json)
    {
        var at = json.IndexOf("<g class=\\\"pele-point-labels\\\"", StringComparison.Ordinal);
        return at < 0 ? json : json[..at] + json[json.IndexOf("</g>", at, StringComparison.Ordinal)..];
    }

    // Every listed fuzz case differs from Node and shows its quirk (Q1-Q3, or R1 for the round-1 empty-label fix); every other
    // case equals Node (Matches_node).
    [Fact] public void Fuzz_deviations_are_the_stated_quirks()
    {
        foreach (var (group, cases) in FuzzDeviations)
        {
            var rows = (JsonArray)DiagramLoader.ParseNode(Fixtures.Lf(Golden(group, "fuzz")))!;
            var differing = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                var input = rows[i]!["input"]!.GetValue<string>();
                var node = Js.Stringify(rows[i]!["result"], 2);
                var port = RunText(input);
                if (node == port) continue;
                // R1: round 1's empty-label deviation, which moves only point labels.
                var why = QuirkIn(input) + (node.Contains("\"svg\"") && OutsidePointLabels(node) == OutsidePointLabels(port) ? "R1" : "");
                differing.Add($"[{i}] = \"{why}\"");
            }
            var actual = string.Join(", ", differing);
            Assert.True(actual == string.Join(", ", cases.Select(c => $"[{c.Key}] = \"{c.Value}\"")), $"{group} fuzz cases that differ: {actual}");
        }
    }

    // charts.mjs:30: a rotated axis title with a Han character is set upright; others are left alone. Node pins it through
    // wrapper/archify-xychart-cjk-axis once xychart lands (Task 6).
    [Fact] public void Rotated_han_title_is_written_vertically()
    {
        const string han = "<g transform=\"translate(12.5,-3) rotate(-90)\"><text x=\"0\" y=\"4.55\" text-anchor=\"middle\">延迟（毫秒）</text></g>";
        const string latin = "<g transform=\"translate(12.5,-3) rotate(-90)\"><text x=\"0\" y=\"4.55\" text-anchor=\"middle\">Latency</text></g>";
        Assert.Equal("<text x=\"12.5\" y=\"-3\" text-anchor=\"middle\" writing-mode=\"vertical-rl\">延迟（毫秒）</text>" + latin,
            ExplainerCharts.UprightHanTitles(han + latin));
        Assert.Equal("<text x=\"1\" y=\"2\" text-anchor=\"middle\" writing-mode=\"vertical-rl\">a\U00020000</text>",
            ExplainerCharts.UprightHanTitles("<g transform=\"translate(1,2) rotate(-90)\"><text x=\"0\" y=\"0\" text-anchor=\"middle\">a\U00020000</text></g>"));
        const string arabicDigits = "<g transform=\"translate(١,2) rotate(-90)\"><text x=\"0\" y=\"0\" text-anchor=\"middle\">汉</text></g>";
        Assert.Equal(arabicDigits, ExplainerCharts.UprightHanTitles(arabicDigits));   // JS \d is ASCII
    }

    // The capture's thrown row for a front-matter YAML error, as Node wrote it (Task 4 seeds the real row in wrapper.json).
    [Fact] public void Thrown_row_has_the_capture_format()
    {
        const string node = "{\n  \"thrown\": {\n    \"name\": \"PeleError\",\n    \"message\": \"Front matter is not valid YAML. YAML: unexpected end of the stream within a flow collection\",\n    \"code\": \"syntax\",\n    \"line\": 0\n  }\n}";
        Assert.Equal(node, Thrown(new PeleException("Front matter is not valid YAML. YAML: unexpected end of the stream within a flow collection", "syntax", 0)));
    }

    [Fact] public void Fuzz_compare_names_the_first_differing_index()
    {
        const string golden = "[{\"input\": \"a\", \"result\": {\"error\": \"x\", \"line\": 2}}, {\"input\": \"b\", \"result\": {\"error\": \"y\"}}]";
        Assert.Null(FuzzFailure(golden, text => text == "a" ? "{\n  \"error\": \"x\",\n  \"line\": 2\n}" : "{\n  \"error\": \"y\"\n}"));
        Assert.StartsWith("fuzz[1] ", FuzzFailure(golden, _ => "{\n  \"error\": \"x\",\n  \"line\": 2\n}"));
    }

    static string SeedFile(string file) => Path.Combine(Fixtures.RepoRoot, "tools", "chart-seeds", file);
    static string Golden(string group, string name) => Fixtures.Golden($"charts/{group}/{name}.json");
    static JsonArray SeedsOf(string group) => (JsonArray)DiagramLoader.ParseNode(Fixtures.Lf(SeedFile($"{group}.json")))!;
    static JsonObject Seed(string group, string name) => SeedsOf(group).Cast<JsonObject>().Single(seed => seed["name"]!.GetValue<string>() == name);

    // {name, text} → compileExplainerChart(text); {name, file, bytes, spec} → chartFromData(bytes, file, spec).
    static string Run(JsonObject seed)
    {
        if (seed["text"] is { } text) return RunText(text.GetValue<string>());
        var spec = seed["spec"]!;
        var y = spec["y"]!.AsArray().Select(column => column!.GetValue<string>()).ToList();
        return ExplainerCharts.FromData(Convert.FromBase64String(seed["bytes"]!.GetValue<string>()), seed["file"]!.GetValue<string>(),
            new ChartDataSpec(spec["kind"]!.GetValue<string>(), spec["x"]!.GetValue<string>(), y, spec["unit"]?.GetValue<string>())).ToJson();
    }

    static string RunText(string text)
    {
        try { return ExplainerCharts.Compile(text).ToJson(); }
        catch (PeleException e) { return Thrown(e); }
    }

    // {"thrown": {name, message, code, line}}; JSON.stringify omits an undefined code or line.
    static string Thrown(PeleException e)
    {
        var thrown = new JsonObject { ["name"] = "PeleError", ["message"] = e.Message };
        if (e.Code is not null) thrown["code"] = e.Code;
        if (e.Line is not null) thrown["line"] = e.Line;
        return ExplainerCharts.Json(new JsonObject { ["thrown"] = thrown });
    }

    // tests/goldens/charts/<type>/fuzz.json is [{input, result}]: each result is re-serialised as JSON.stringify(result, null, 2)
    // and compared with the port's, element by element. Returns the first difference, or null.
    static string? FuzzFailure(string golden, Func<string, string> runText, IReadOnlyDictionary<int, string>? skip = null)
    {
        var rows = (JsonArray)DiagramLoader.ParseNode(golden)!;
        for (var i = 0; i < rows.Count; i++)
        {
            if (skip?.ContainsKey(i) == true) continue;
            var input = rows[i]!["input"]!.GetValue<string>();
            var expected = Js.Stringify(rows[i]!["result"], 2);
            var actual = runText(input);
            if (expected != actual) return $"fuzz[{i}] input {Js.Quote(input)}\nexpected: {expected}\nactual:   {actual}";
        }
        return null;
    }
}
