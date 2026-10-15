using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class JsUriTests
{
    static JsonObject Fx => (JsonObject)JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("js/js-uri.json")))!;

    public static IEnumerable<object[]> Rows() => Fx["rows"]!.AsArray().Select((r, i) => new object[] { i, r!["s"]!.GetValue<string>() });

    [Theory, MemberData(nameof(Rows))]
    public void Uri_builtins_and_toLowerCase_match_V8(int i, string s)
    {
        var row = Fx["rows"]![i]!;
        Assert.Equal(row["encode"]?.GetValue<string>(), Js.EncodeUriComponent(s));
        Assert.Equal(row["decode"]?.GetValue<string>(), Js.DecodeUriComponent(s));
        Assert.Equal(row["lower"]!.GetValue<string>(), Js.ToLowerCase(s));
    }

    [Fact] public void EncodeUriComponent_throws_on_lone_surrogates_like_V8()
    {
        foreach (var row in Fx["encodeUnits"]!.AsArray())
        {
            var s = new string(row!["units"]!.AsArray().Select(u => (char)u!.GetValue<int>()).ToArray());
            var want = row["encode"]?.GetValue<string>();
            if (want is null) Assert.Throws<ArgumentException>(() => Js.EncodeUriComponent(s));
            else Assert.Equal(want, Js.EncodeUriComponent(s));
        }
    }
}
