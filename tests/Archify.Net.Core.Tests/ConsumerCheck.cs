using System.Text.Json.Nodes;
using Archify.Net;
using Xunit;

// Outside Archify.Net on purpose: compiles only if a consumer can name the facade through `using Archify.Net;`.
// ParityTests.cs has a file-scoped namespace, which forbids a second namespace in that file.
namespace ConsumerCheck;

public class ConsumerShapedTests
{
    [Fact] public void Facade_resolves_through_using_directive()
    {
        var r = ArchifyNet.Render(DiagramType.Architecture, new JsonArray());
        Assert.Equal("input/json-parse", Assert.Single(r.Diagnostics).Code);
    }
}
