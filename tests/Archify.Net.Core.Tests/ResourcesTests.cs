using Xunit;
namespace Archify.Net.Tests;

public class ResourcesTests
{
    [Fact]
    public void Template_resource_equals_upstream_file_bytes()
    {
        var embedded = Resources.Text("assets/template.html");
        var onDisk = Fixtures.Lf(Fixtures.Upstream("assets/template.html"));
        Assert.Equal(onDisk, embedded);
        Assert.Contains("<!-- ARCHIFY:SVG_SLOT_START -->", embedded);
    }

    [Theory]
    [InlineData("schemas/common.schema.json")] [InlineData("schemas/architecture.schema.json")]
    [InlineData("locales/manifest.json")] [InlineData("locales/en.json")] [InlineData("generated-brand-marks.json")]
    public void Named_resources_exist(string path) => Assert.True(Resources.Exists(path));

    [Fact]
    public void DiagramType_names_round_trip()
    {
        foreach (var t in Enum.GetValues<DiagramType>()) Assert.Equal(t, DiagramTypes.Parse(t.Name()));
        Assert.Equal("components", DiagramType.Architecture.SemanticCollection());
        Assert.Null(DiagramType.Tree.RelationshipCollection());
    }
}
