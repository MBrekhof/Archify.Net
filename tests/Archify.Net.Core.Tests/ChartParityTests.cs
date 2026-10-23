using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

// PORT-008: ExplainerCharts against goldens Node mints through upstream charts.mjs (tools/capture-fixtures.mjs, charts
// section). Seeds and layout: tools/chart-seeds/README.md.
public class ChartParityTests
{
    // A group is compared once it is listed; each type task adds its own. "wrapper" needs pie and xychart (Task 5).
    static readonly string[] Groups = ["data"];
    // Rows whose golden needs a later task, filtered out of the theory data (xunit 2.9.3 has no dynamic skip).
    static readonly HashSet<(string Group, string Name)> PendingUntil = [];
    // Rows that differ from Node by a stated deviation (PORT-008 Task 11, docs/deviations.md), each pinned by its own fact below.
    static readonly HashSet<(string Group, string Name)> KnownDeviations = [("data", "json-deep-unread-column")];

    public static TheoryData<string, string> Targets()
    {
        var data = new TheoryData<string, string>();
        foreach (var group in Groups)
        {
            foreach (var seed in SeedsOf(group))
            {
                var name = seed!["name"]!.GetValue<string>();
                if (!PendingUntil.Contains((group, name)) && !KnownDeviations.Contains((group, name))) data.Add(group, name);
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
