using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Delivery;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 8: `deliver` against the Node-minted fixtures (stdout, stderr, exit, the workdir's entries and every delivery
// sidecar), plus the lock, journal and commit behaviour no fixture can pin (own-pid owner, a forced provenance write failure).
public partial class DeliverCommandTests
{
    const string Upstream = "upstream/examples/";
    const string Inputs = "tests/fixtures/cli/inputs/";
    // V8's JSON.parse text (Q8): compared with the reason masked.
    static readonly string[] V8Text = ["deliver/json-parse-json", "deliver/json-parse-human", "deliver/json-parse-existing-artifact-json"];

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidV4();

    static string Uuids(string text) => UuidV4().Replace(text, "<uuid>");

    // The fixture plus its `entries` and `sidecars` (tools/capture-cli-fixtures.mjs, deliver block).
    sealed record DeliverFixture(CliFixture Cli, List<string> Entries, Dictionary<string, string> Sidecars);

    static DeliverFixture Load(string name)
    {
        var json = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name.Replace('/', '\\') + ".json")))!;
        return new(CliTestHost.Fixture(name), [.. json["entries"]!.AsArray().Select(e => e!.GetValue<string>())],
            json["sidecars"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()));
    }

    // One in-process deliver in a fresh workdir; returns tokenised output, the entries and the sidecars, like the fixtures.
    static (int Exit, string Stdout, string Stderr, List<string> Entries, Dictionary<string, string> Sidecars) Deliver(string[] args,
        IReadOnlyDictionary<string, string> seed, DeliveryStore? store = null, Action<string>? inspect = null, CliServices? services = null)
    {
        var work = Directory.CreateTempSubdirectory("archify-deliver-").FullName;
        try
        {
            foreach (var (name, source) in seed)
            {
                var target = Path.Combine(work, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(CliTestHost.RepoRoot, source.Replace('/', '\\')), target);
            }
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var io = new CliIo(stdout, stderr, work, services ?? CliServices.Default);
            var exit = store is null ? Cli.Run(args, io) : Commands.DeliverCommand.Run(args[1..], io, store);
            inspect?.Invoke(work);
            var entries = Entries(work);
            var sidecars = entries.Where(e => IsSidecar(Path.GetFileName(e)))
                .ToDictionary(e => e, e => Uuids(CliTestHost.Tokenise(CliTestHost.ReadLf(Path.Join(work, e)), work)));
            return (exit, Uuids(CliTestHost.Tokenise(stdout.ToString(), work)), Uuids(CliTestHost.Tokenise(stderr.ToString(), work)), entries, sidecars);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    static bool IsSidecar(string name) =>
        name.EndsWith(".delivery.json", StringComparison.Ordinal) || name.EndsWith(".delivery-pending.json", StringComparison.Ordinal)
        || name == ".archify-delivery-lock.json";

    static List<string> Entries(string work) =>
        [.. Directory.GetFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Fixtures() => [.. CliTestHost.FixtureNames("deliver").Where(n => !V8Text.Contains(n))];

    [Theory, MemberData(nameof(Fixtures))]
    public void Deliver_matches_node(string name)
    {
        var fixture = Load(name);
        var (exit, stdout, stderr, entries, sidecars) = Deliver(fixture.Cli.Args, fixture.Cli.Files, inspect: work =>
        {
            // A lock owner is never taken over: every seeded file stays byte-identical (Review Focus 3).
            if (name.StartsWith("deliver/lock-", StringComparison.Ordinal))
                foreach (var (dest, source) in fixture.Cli.Files) AssertSameBytes(source, Path.Join(work, dest));
        });
        Assert.Equal(fixture.Cli.Stdout, stdout);
        Assert.Equal(fixture.Cli.Stderr, stderr);
        Assert.Equal(fixture.Cli.Exit, exit);
        Assert.Equal(fixture.Entries, entries);
        Assert.Equal(fixture.Sidecars, sidecars);
    }

    // input/json-parse: V8's reason upstream, .NET's here; every occurrence of it (receipt, journal-free marker, stderr) is masked.
    [Theory]
    [InlineData("deliver/json-parse-json")]
    [InlineData("deliver/json-parse-human")]
    [InlineData("deliver/json-parse-existing-artifact-json")]
    public void Parse_failures_match_node_but_for_the_v8_text(string name)
    {
        var fixture = Load(name);
        var (exit, stdout, stderr, entries, sidecars) = Deliver(fixture.Cli.Args, fixture.Cli.Files);
        var (expectedStdout, expectedStderr, expectedSidecars) = MaskReason(fixture.Cli.Stdout, fixture.Cli.Stderr, fixture.Sidecars);
        var (actualStdout, actualStderr, actualSidecars) = MaskReason(stdout, stderr, sidecars);
        Assert.Equal(expectedStdout, actualStdout);
        Assert.Equal(expectedStderr, actualStderr);
        Assert.Equal(fixture.Cli.Exit, exit);
        Assert.Equal(fixture.Entries, entries);
        Assert.Equal(expectedSidecars, actualSidecars);
    }

    static (string, string, Dictionary<string, string>) MaskReason(string stdout, string stderr, Dictionary<string, string> sidecars)
    {
        var reason = stdout.Length > 0
            ? JsonNode.Parse(stdout)!["diagnostics"]![0]!["evidence"]!["reason"]!.GetValue<string>()
            : Regex.Match(stderr, @"\[input/json-parse\] Input JSON could not be parsed: (?<r>.+) Fix: ").Groups["r"].Value;
        Assert.NotEmpty(reason);
        var escaped = Js.Stringify(JsonValue.Create(reason))[1..^1];
        string Mask(string text) => text.Replace(escaped, "<REASON>").Replace(reason, "<REASON>");
        return (Mask(stdout), Mask(stderr), sidecars.ToDictionary(kv => kv.Key, kv => Mask(kv.Value)));
    }

    public static TheoryData<string> ArgumentFixtures() => [.. CliTestHost.FixtureNames("arguments", "deliver-")];

    [Theory, MemberData(nameof(ArgumentFixtures))]
    public void Argument_errors_match_node_and_create_no_files(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        CliTestHost.AssertFixture(fixture, work => Assert.Equal([.. fixture.Files.Keys.Order(StringComparer.Ordinal)], Entries(work)));
    }

    // Review Focus 2: a failed attempt leaves its journal (check fails closed on it); only the next successful deliver removes it.
    [Fact]
    public void A_failed_attempt_keeps_its_journal_until_a_delivery_succeeds()
    {
        var work = Directory.CreateTempSubdirectory("archify-deliver-").FullName;
        try
        {
            var spec = Path.Join(work, "spec.json");
            File.Copy(Path.Join(CliTestHost.RepoRoot, Inputs, "broken.workflow.json"), spec);
            var io = new CliIo(new StringWriter(), new StringWriter(), work, CliServices.Default);
            Assert.Equal(1, Cli.Run(["deliver", "architecture", "spec.json", "out.html", "--json"], io));
            Assert.Equal(["out.delivery-pending.json", "spec.json"], Entries(work));
            var journal = JsonNode.Parse(File.ReadAllText(Path.Join(work, "out.delivery-pending.json")))!;
            Assert.Equal("pending", journal["status"]!.GetValue<string>());

            File.Copy(Path.Join(CliTestHost.RepoRoot, Upstream, "web-app.architecture.json"), spec, overwrite: true);
            Assert.Equal(0, Cli.Run(["deliver", "architecture", "spec.json", "out.html", "--json"], io));
            Assert.Equal(["out.delivery.json", "out.html", "spec.json"], Entries(work));
            Assert.Equal("current", JsonNode.Parse(File.ReadAllText(Path.Join(work, "out.delivery.json")))!["status"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // A live owner (this test process) is a concurrent attempt: no journal, no marker, every file untouched.
    [Fact]
    public void A_lock_owned_by_a_live_process_is_a_concurrent_attempt()
    {
        var lockSource = Path.Join(Path.GetTempPath(), $"archify-live-lock-{Guid.NewGuid():N}.json");
        File.WriteAllText(lockSource, $"{{\"schemaVersion\":1,\"receiptId\":\"11111111-2222-4333-8444-555555555555\",\"pid\":{Environment.ProcessId}}}\n");
        try
        {
            var seed = new Dictionary<string, string>
            {
                ["web-app.architecture.json"] = Upstream + "web-app.architecture.json",
                ["out.html"] = Upstream + "sequence-cache-miss-request.html",
                [".archify-delivery-lock.json"] = Path.GetRelativePath(CliTestHost.RepoRoot, lockSource),
            };
            var (exit, stdout, stderr, entries, _) = Deliver(["deliver", "architecture", "web-app.architecture.json", "out.html", "--json"], seed,
                inspect: work => { foreach (var (dest, source) in seed) AssertSameBytes(source, Path.Join(work, dest)); });
            Assert.Equal((1, ""), (exit, stderr));
            Assert.Equal([".archify-delivery-lock.json", "out.html", "web-app.architecture.json"], entries);
            var receipt = JsonNode.Parse(stdout)!;
            Assert.Equal(["schemaVersion", "ok", "command", "receiptId", "stage", "type", "input", "output", "error", "diagnostics"], receipt.AsObject().Select(p => p.Key));
            const string reason = "Another delivery attempt owns \"<WORK>\\out.html\" through lock \"<WORK>\\.archify-delivery-lock.json\".";
            Assert.Equal($"Could not start delivery for \"<WORK>\\out.html\": {reason}", receipt["error"]!.GetValue<string>());
            var diagnostic = Assert.Single(receipt["diagnostics"]!.AsArray())!;
            Assert.Equal("delivery/concurrent-attempt", diagnostic["code"]!.GetValue<string>());
            Assert.Equal(Js.Stringify(new JsonObject { ["reason"] = reason }), Js.Stringify(diagnostic["evidence"]));
            Assert.Equal("""["wait for the active delivery to finish, then retry"]""", Js.Stringify(diagnostic["supportedFixes"]));
        }
        finally
        {
            File.Delete(lockSource);
        }
    }

    // Q5 commit: a provenance write that fails after the HTML was swapped in restores the previous HTML, records the failure beside
    // it and keeps the journal; no temp or backup file survives.
    [Fact]
    public void A_failed_provenance_write_rolls_back_to_the_previous_html()
    {
        var failures = 0;
        var store = new DeliveryStore
        {
            Publish = (target, bytes, backup) =>
            {
                if (target.EndsWith("out.delivery.json", StringComparison.Ordinal) && failures++ == 0) throw new IOException("forced provenance failure");
                DeliveryStore.PublishFile(target, bytes, backup);
            },
        };
        var seed = new Dictionary<string, string>
        {
            ["web-app.architecture.json"] = Upstream + "web-app.architecture.json",
            ["out.html"] = Upstream + "sequence-cache-miss-request.html",
        };
        var (exit, stdout, stderr, entries, sidecars) = Deliver(["deliver", "architecture", "web-app.architecture.json", "out.html", "--json"], seed, store,
            inspect: work => AssertSameBytes(Upstream + "sequence-cache-miss-request.html", Path.Join(work, "out.html")));
        Assert.Equal((1, ""), (exit, stderr));
        Assert.Equal(["out.delivery-pending.json", "out.delivery.json", "out.html", "web-app.architecture.json"], entries);
        var receipt = JsonNode.Parse(stdout)!;
        Assert.Equal(("commit", "failed"), (receipt["stage"]!.GetValue<string>(), receipt["provenance"]!.GetValue<string>()));
        const string message = "Could not commit verified delivery \"<WORK>\\out.html\": Delivery pair commit failed; the previous files were restored.";
        Assert.Equal(message, receipt["error"]!.GetValue<string>());
        var diagnostic = Assert.Single(receipt["diagnostics"]!.AsArray())!;
        Assert.Equal(Js.Stringify(new JsonObject
        {
            ["code"] = "delivery/commit", ["severity"] = "error", ["message"] = message,
            ["subject"] = new JsonObject { ["output"] = "<WORK>\\out.html" },
            ["evidence"] = new JsonObject { ["reason"] = "forced provenance failure" },
            ["supportedFixes"] = new JsonArray("choose replaceable regular-file artifact and provenance targets on the same writable filesystem"),
        }), Js.Stringify(diagnostic));
        var marker = JsonNode.Parse(sidecars["out.delivery.json"])!;
        Assert.Equal(("failed", "commit", message), (marker["status"]!.GetValue<string>(), marker["stage"]!.GetValue<string>(), marker["error"]!.GetValue<string>()));
        var previous = File.ReadAllBytes(Path.Join(CliTestHost.RepoRoot, Upstream, "sequence-cache-miss-request.html"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(previous)), marker["artifact"]!["sha256"]!.GetValue<string>());
    }

    // Without a previous artifact the rollback deletes the new one, so the receipt says provenance 'absent' and no marker is written.
    [Fact]
    public void A_failed_provenance_write_without_a_previous_artifact_leaves_none()
    {
        var store = new DeliveryStore
        {
            Publish = (target, bytes, backup) =>
            {
                if (target.EndsWith("out.delivery.json", StringComparison.Ordinal)) throw new IOException("forced provenance failure");
                DeliveryStore.PublishFile(target, bytes, backup);
            },
        };
        var (exit, stdout, _, entries, _) = Deliver(["deliver", "architecture", "web-app.architecture.json", "out.html", "--json"],
            new Dictionary<string, string> { ["web-app.architecture.json"] = Upstream + "web-app.architecture.json" }, store);
        Assert.Equal(1, exit);
        Assert.Equal(["out.delivery-pending.json", "web-app.architecture.json"], entries);
        Assert.Equal("absent", JsonNode.Parse(stdout)!["provenance"]!.GetValue<string>());
    }

    // Q5 commit: a reader holding the previous artifact without sharing (a scanner, a viewer) makes the swap fail with a sharing
    // violation; the commit retries until it is released. The hold starts when the swap does and ends 200 ms later.
    [Fact]
    public void A_held_target_is_retried_until_released()
    {
        FileStream? holder = null;
        Task? release = null;
        var waited = TimeSpan.Zero;
        var store = new DeliveryStore
        {
            Publish = (target, bytes, backup) =>
            {
                if (target.EndsWith("out.html", StringComparison.Ordinal))
                {
                    holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
                    release = Task.Delay(200).ContinueWith(_ => holder.Dispose(), TaskScheduler.Default);
                }
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                DeliveryStore.PublishFile(target, bytes, backup);
                if (target.EndsWith("out.html", StringComparison.Ordinal)) waited = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            },
        };
        var seed = new Dictionary<string, string>
        {
            ["web-app.architecture.json"] = Upstream + "web-app.architecture.json",
            ["out.html"] = Upstream + "sequence-cache-miss-request.html",
        };
        try
        {
            var (exit, stdout, stderr, entries, _) = Deliver(["deliver", "architecture", "web-app.architecture.json", "out.html", "--json"], seed, store,
                inspect: work => AssertSameBytes(Upstream + "web-app-rendered.html", Path.Join(work, "out.html")));
            Assert.Equal((0, ""), (exit, stderr));
            Assert.True(JsonNode.Parse(stdout)!["ok"]!.GetValue<bool>());
            Assert.Equal(["out.delivery.json", "out.html", "web-app.architecture.json"], entries);
            Assert.True(waited >= TimeSpan.FromMilliseconds(150), $"the swap returned after {waited.TotalMilliseconds} ms, before the hold was released");
        }
        finally
        {
            release?.Wait();
            holder?.Dispose();
        }
    }

    // A:4847-4884, the check stage end to end: the final artifact check fails on the bytes about to be committed. Nothing is
    // committed, the previous artifact stays, the journal stays and a failed marker names stage check, so check fails closed. No real
    // input renders and then fails the checker, so the check seam returns the checker's report on a second <svg> block.
    [Fact]
    public void A_failed_final_check_keeps_the_previous_artifact_and_records_the_attempt()
    {
        var failing = new DeliveryStore { Check = (bytes, file) => Cli.CheckArtifact([.. bytes, .. Encoding.UTF8.GetBytes("<svg></svg>")], file) };
        var seed = new Dictionary<string, string>
        {
            ["web-app.architecture.json"] = Upstream + "web-app.architecture.json",
            ["out.html"] = Upstream + "sequence-cache-miss-request.html",
        };
        var checkExit = -1;
        var checkStdout = "";
        var (exit, stdout, stderr, entries, sidecars) = Deliver(["deliver", "architecture", "web-app.architecture.json", "out.html", "--json"], seed, failing,
            inspect: work =>
            {
                AssertSameBytes(Upstream + "sequence-cache-miss-request.html", Path.Join(work, "out.html"));
                var output = new StringWriter();
                checkExit = Cli.Run(["check", "out.html", "--json"], new CliIo(output, new StringWriter(), work, CliServices.Default));
                checkStdout = output.ToString();
            });
        Assert.Equal((1, ""), (exit, stderr));
        Assert.Equal(["out.delivery-pending.json", "out.delivery.json", "out.html", "web-app.architecture.json"], entries);
        var receipt = JsonNode.Parse(stdout)!;
        Assert.Equal(("check", "failed", "Final artifact check failed; the previous artifact was preserved."),
            ((string?)receipt["stage"], (string?)receipt["provenance"], (string?)receipt["error"]));
        var checker = receipt["checker"]!.AsObject();
        Assert.Equal(["ok", "file"], checker.Select(p => p.Key).Take(2));
        Assert.Equal(("<WORK>\\out.html", false), ((string?)checker["file"], checker["ok"]!.GetValue<bool>()));
        Assert.Contains(checker["checks"]!.AsArray(), c => (string?)c!["name"] == "single_svg" && !c["ok"]!.GetValue<bool>());
        Assert.NotEmpty(receipt["diagnostics"]!.AsArray());
        var marker = JsonNode.Parse(sidecars["out.delivery.json"])!;
        Assert.Equal(("failed", "check"), ((string?)marker["status"], (string?)marker["stage"]));
        var previous = File.ReadAllBytes(Path.Join(CliTestHost.RepoRoot, Upstream, "sequence-cache-miss-request.html"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(previous)), (string?)marker["artifact"]!["sha256"]);
        Assert.Equal("pending", (string?)JsonNode.Parse(sidecars["out.delivery-pending.json"])!["status"]);
        Assert.Equal((1, "failed"), (checkExit, (string?)JsonNode.Parse(checkStdout)!["provenance"]));
    }

    // Q5/D9: --open never launches anything; the receipt carries upstream's fallback and stderr its notice (A:5173-5188).
    [Fact]
    public void Open_reports_the_unsupported_fallback()
    {
        var (exit, stdout, stderr, _, _) = Deliver(["deliver", "architecture", "web-app.architecture.json", "out.html", "--json", "--open"],
            new Dictionary<string, string> { ["web-app.architecture.json"] = Upstream + "web-app.architecture.json" });
        Assert.Equal(0, exit);
        var receipt = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("open", receipt.Last().Key);
        Assert.Equal("""{"requested":true,"status":"unsupported","target":"<WORK>\\out.html","method":null}""", Js.Stringify(receipt["open"]));
        Assert.Equal("Could not open the verified artifact (unsupported). Open it manually. Target: <WORK>\\out.html\n", stderr);
    }

    // processIsRunning (A:218-226): a missing pid is ESRCH (dead); any pid OpenProcess rejects as invalid reads as ESRCH in libuv.
    [Fact]
    public void Process_liveness_follows_upstream()
    {
        Assert.True(DeliveryStore.ProcessIsRunning(Environment.ProcessId));
        Assert.False(DeliveryStore.ProcessIsRunning(999999));
        Assert.False(DeliveryStore.ProcessIsRunning(long.MaxValue / 2));
    }

    static void AssertSameBytes(string repoRelative, string written) =>
        Assert.True(File.ReadAllBytes(Path.Join(CliTestHost.RepoRoot, repoRelative)).AsSpan().SequenceEqual(File.ReadAllBytes(written)),
            $"{written} differs from {repoRelative}");
}
