using System.Text.Json.Nodes;
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
    ];

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
            if (FuzzFailure(golden, RunText) is { } failure) Assert.Fail(failure);
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

    // quadrant render.ts:96,180: upstream reserves a zero-size "taken" box for a label with no text (an empty quadrant, a
    // point label that trims to nothing), so a point label whose box holds that invisible spot is pushed away from empty
    // space. The port reserves boxes only for drawn labels.
    [Fact] public void Empty_label_reserves_no_space_is_the_stated_deviation()
    {
        static string Points(string json) { var at = json.IndexOf("<g class=\\\"pele-point-labels\\\"", StringComparison.Ordinal); return json[..at] + json[json.IndexOf("</g>", at, StringComparison.Ordinal)..]; }

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
        Assert.Equal(Points(stress), Points(port));
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
    static string? FuzzFailure(string golden, Func<string, string> runText)
    {
        var rows = (JsonArray)DiagramLoader.ParseNode(golden)!;
        for (var i = 0; i < rows.Count; i++)
        {
            var input = rows[i]!["input"]!.GetValue<string>();
            var expected = Js.Stringify(rows[i]!["result"], 2);
            var actual = runText(input);
            if (expected != actual) return $"fuzz[{i}] input {Js.Quote(input)}\nexpected: {expected}\nactual:   {actual}";
        }
        return null;
    }
}
