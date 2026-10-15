using Archify.Net.Cli.Commands;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 7: `guide` (A:6054-6105, recipes/scenarios.mjs:331-446) against the Node-minted fixture battery, JSON and human,
// en and zh, byte-equal.
public class GuideCommandTests
{
    public static TheoryData<string> GuideFixtures() => [.. CliTestHost.FixtureNames("guide"), .. CliTestHost.FixtureNames("arguments", "guide-")];

    [Theory, MemberData(nameof(GuideFixtures))]
    public void Guide_matches_node(string name) => CliTestHost.AssertFixture(CliTestHost.Fixture(name));

    // normalized() is NFKC -> toLowerCase -> /[\s_]+/g -> ' ' -> trim. Expected values captured from Node
    // (recommendScenario(query), UP @ c21c31ff) in this session.
    [Theory]
    [InlineData("INCIDENT_RUNBOOK", "high", "incident-runbook", "incident-runbook")]
    [InlineData("ｖｉａ", "medium", "layout-repair", "via")]
    [InlineData("\tLayout　__Repair ", "high", "layout-repair", "layout-repair")]
    [InlineData("SCROLL HEIGHT", "low", "system-overview", null)]
    [InlineData("İ", "low", "system-overview", null)]
    public void Normalisation_matches_node(string query, string confidence, string id, string? matched)
    {
        var result = GuideCommand.Recommend(query, null);
        Assert.Equal("en", (string?)result["lang"]);
        Assert.Equal(query, (string?)result["query"]);
        Assert.Equal(confidence, (string?)result["confidence"]);
        Assert.Equal(id, (string?)result["recommendation"]!["id"]);
        string?[] signals = matched is null ? [] : [matched];
        Assert.Equal(signals, result["matchedSignals"]!.AsArray().Select(s => (string?)s));
        Assert.Empty(result["alternatives"]!.AsArray());
    }

    // A Windows command line can carry a lone surrogate; Node's process.argv receives it as U+FFFD (it passes through UTF-8), so
    // upstream's guide answers for "viewport � overflow". Guide_matches_node runs these fixtures with the lone surrogate itself.
    [Fact]
    public void A_lone_surrogate_in_argv_reads_as_node_reads_it()
    {
        var fixture = CliTestHost.Fixture("guide/lone-surrogate-json");
        Assert.Contains("viewport \uD800 overflow", fixture.Args);
        Assert.Contains("viewport � overflow", fixture.Stdout);
    }

    [Fact]
    public void Language_detection_is_the_cjk_range()
    {
        Assert.Equal("zh", GuideCommand.DetectLanguage("abc 㐀"));
        Assert.Equal("zh", GuideCommand.DetectLanguage("鿿"));
        Assert.Equal("en", GuideCommand.DetectLanguage("㏿ꀀ"));
    }
}
