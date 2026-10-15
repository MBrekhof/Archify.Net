using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 6: `check` without provenance (Task 9) against the Node-minted fixtures, writing nothing.
public class CheckCommandTests
{
    public static TheoryData<string> Fixtures() => [.. CliTestHost.FixtureNames("check"), .. CliTestHost.FixtureNames("arguments", "check-")];

    [Theory, MemberData(nameof(Fixtures))]
    public void Check_matches_node(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        CliTestHost.AssertFixture(fixture, work =>
            Assert.Equal([.. fixture.Files.Keys.Order(StringComparer.Ordinal)], Directory.GetFileSystemEntries(work).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
    }
}
