using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Delivery;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 tasks 6 and 9: `check` and its delivery provenance gate against the Node-minted fixtures, writing nothing.
public partial class CheckCommandTests
{
    public static TheoryData<string> Fixtures() => [.. CliTestHost.FixtureNames("check"), .. CliTestHost.FixtureNames("arguments", "check-")];

    [Theory, MemberData(nameof(Fixtures))]
    public void Check_matches_node(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        CliTestHost.AssertFixture(fixture, work =>
            Assert.Equal([.. fixture.Files.Keys.Order(StringComparer.Ordinal)], Directory.GetFileSystemEntries(work).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidV4();

    public static TheoryData<string> ProvenanceFixtures() => [.. CliTestHost.FixtureNames("provenance")];

    // Every provenance state, each made by a real deliver (C# here, Node in the fixture) plus the fixture's documented edits. The
    // deliveryReceiptId is <uuid>; the one V8 JSON.parse text is masked (Q8).
    [Theory, MemberData(nameof(ProvenanceFixtures))]
    public void Provenance_states_match_node(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Args, fixture.Files, setup: fixture.Setup);
        var expected = fixture.Stdout;
        stdout = UuidV4().Replace(stdout, "<uuid>");
        if (name == "provenance/invalid-json") (expected, stdout) = (MaskReason(expected), MaskReason(stdout));
        Assert.Equal(expected, stdout);
        Assert.Equal(fixture.Stderr, stderr);
        Assert.Equal(fixture.Exit, exit);
    }

    static string MaskReason(string receipt)
    {
        var reason = JsonNode.Parse(receipt)!["diagnostics"]![0]!["evidence"]!["reason"]!.GetValue<string>();
        Assert.NotEmpty(reason);
        return receipt.Replace(Js.Stringify(JsonValue.Create(reason))[1..^1], "<REASON>");
    }

    // verifyDeliveryUnchanged (A:5426-5448): after the checker, the artifact and its sidecar must still be the bytes first inspected,
    // and the checked bytes those of the provenance.
    [Fact]
    public void A_change_during_the_check_fails_the_provenance_gate()
    {
        var work = Directory.CreateTempSubdirectory("archify-check-").FullName;
        try
        {
            File.Copy(Path.Join(CliTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json"), Path.Join(work, "spec.json"));
            Assert.Equal(0, Cli.Run(["deliver", "architecture", "spec.json", "out.html", "--json"], new CliIo(new StringWriter(), new StringWriter(), work, CliServices.Default)));
            var artifact = Path.Join(work, "out.html");
            var sidecar = Path.Join(work, "out.delivery.json");
            var inspected = DeliveryStore.InspectProvenance(artifact, requireProvenance: true);
            Assert.Equal((true, "current"), (inspected.Ok, inspected.Status));
            var bytes = File.ReadAllBytes(artifact);
            var identity = new JsonObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length };

            var unchanged = DeliveryStore.VerifyUnchanged(artifact, inspected, identity);
            Assert.Equal((true, "current", inspected.ReceiptId), (unchanged.Ok, unchanged.Status, unchanged.ReceiptId));

            // The checker saw other bytes than the provenance names.
            var otherChecked = new JsonObject { ["sha256"] = new string('0', 64), ["bytes"] = bytes.Length };
            var mismatch = DeliveryStore.VerifyUnchanged(artifact, inspected, otherChecked);
            Assert.Equal((false, "mismatch"), (mismatch.Ok, mismatch.Status));
            Assert.Equal(Js.Stringify(new JsonObject
            {
                ["code"] = "delivery/provenance-mismatch", ["severity"] = "error", ["message"] = "The artifact or delivery record changed during inspection.",
                ["subject"] = new JsonObject { ["artifact"] = artifact },
                ["evidence"] = new JsonObject { ["expected"] = identity.DeepClone(), ["checked"] = otherChecked.DeepClone(), ["current"] = identity.DeepClone() },
                ["supportedFixes"] = new JsonArray("finish delivery and other writes before rerunning the checker"),
            }), Js.Stringify(Assert.Single(mismatch.Diagnostics).ToJson()));

            // The sidecar was rewritten (same meaning, other bytes): its binding no longer holds.
            File.AppendAllText(sidecar, " ");
            var sidecarChanged = DeliveryStore.VerifyUnchanged(artifact, inspected, identity);
            Assert.Equal((false, "invalid", "delivery/provenance-invalid"), (sidecarChanged.Ok, sidecarChanged.Status, sidecarChanged.Diagnostics[0].Code));
            Assert.Equal("Could not read delivery provenance: Delivery provenance is not a stable regular file (delivery-provenance-content-changed).",
                sidecarChanged.Diagnostics[0].Message);

            // The artifact was replaced: captureRegularFileBinding's content check reads as an unreadable artifact.
            File.AppendAllText(artifact, "\n");
            var artifactChanged = DeliveryStore.VerifyUnchanged(artifact, inspected, identity);
            Assert.Equal((false, "unknown", "input/artifact-unreadable"), (artifactChanged.Ok, artifactChanged.Status, artifactChanged.Diagnostics[0].Code));
            Assert.Equal("Could not read the HTML artifact: The artifact is not a stable regular file (delivery-artifact-content-changed).",
                artifactChanged.Diagnostics[0].Message);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
