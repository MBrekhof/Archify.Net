using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class RepositoryLocationTests
{
    static readonly JsonObject Fx = (JsonObject)JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("js/repository-location.json")))!;
    static string? Str(JsonNode? n) => n?.GetValue<string>();
    static int? Int(JsonNode? n) => n?.GetValue<int>();

    static string Show(ParsedRemote? r) => r is null ? "null"
        : Js.Stringify(new JsonObject { ["identity"] = r.Identity, ["url"] = r.Url, ["provider"] = r.Provider, ["protocol"] = r.Protocol,
            ["path"] = r.Path, ["endpoint"] = r.Endpoint, ["pathKind"] = r.PathKind });

    public static IEnumerable<object[]> ParseRows() => Fx["parse"]!.AsArray().Select((r, i) => new object[] { i, r!["value"]!.GetValue<string>() });

    // Each row: one value under authored false/true and every provider hint. `expectDeviation` rows are the stated deviation
    // (README): the port must still differ from Node on them, so a fix shows up as a failing test that asks for the flag's removal.
    [Theory, MemberData(nameof(ParseRows))]
    public void Parse_matches_parseRepositoryRemote(int i, string value)
    {
        var row = Fx["parse"]![i]!;
        var diffs = new List<string>();
        foreach (var c in row["results"]!.AsArray())
        {
            var want = c!["result"] is JsonObject o ? Js.Stringify(o) : "null";
            var got = Show(RepositoryLocation.Parse(value, c["authored"]!.GetValue<bool>(), Str(c["provider"])));
            if (want != got) diffs.Add($"authored={c["authored"]} provider={c["provider"]}: want {want} got {got}");
        }
        if (row["expectDeviation"]?.GetValue<bool>() == true) Assert.NotEmpty(diffs);
        else Assert.Empty(diffs);
    }

    [Fact] public void Redact_matches_redactRepositoryRemote()
    {
        foreach (var r in Fx["redact"]!.AsArray()) Assert.Equal(Str(r!["redacted"]), RepositoryLocation.Redact(Str(r["value"])));
    }

    [Fact] public void SourceHref_and_TreeHref_match_upstream()
    {
        foreach (var r in Fx["sourceHref"]!.AsArray())
        {
            var s = r!["source"]!;
            Assert.Equal(Str(r["href"]), RepositoryLocation.SourceHref(Str(r["provider"]), Str(r["url"])!, Str(r["revision"])!,
                Str(s["path"])!, Int(s["line"]), Int(s["endLine"])));
        }
        foreach (var r in Fx["treeHref"]!.AsArray())
            Assert.Equal(Str(r!["href"]), RepositoryLocation.TreeHref(Str(r["provider"]), Str(r["url"])!, Str(r["revision"])!));
    }
}
