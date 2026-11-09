using Xunit;

namespace Archify.Net.Tests;

// PERF-003 (archify#1072): automaticPortCandidates' 12px probe walk. The port skips the steps whose rounded probe points equal the
// previous step's (WorkflowCompilation.NextProbeOffset); these tests hold it to the result of upstream's one-by-one walk.
public class WorkflowProbeTests
{
    // The probe loop of workflow-compiler.mjs:4492 on one axis, with a pluggable step: `offset += 12` (upstream) or the port's skip.
    static List<double> Probe(double center, int direction, double extent, (double Coordinate, double Clearance)[] occupied,
        Func<double, double, double> next)
    {
        bool Clear(double p) => occupied.All(e => Math.Abs(e.Coordinate - p) >= e.Clearance - 0.0001);
        var candidates = new List<double>();
        var limit = extent / 2 - 16;
        for (double offset = 0; offset <= limit; offset = next(offset, limit))
        {
            foreach (var sign in (int[])[direction, -direction])
            {
                var point = center + sign * offset;
                if (Clear(point) && !candidates.Contains(point)) candidates.Add(point);
            }
            if (candidates.Count >= 2) return candidates[..2];
        }
        return candidates;
    }

    static List<double> Upstream(double center, int direction, double extent, params (double, double)[] occupied)
        => Probe(center, direction, extent, occupied, (offset, _) => offset + 12);

    static List<double> Port(double center, int direction, double extent, params (double, double)[] occupied)
        => Probe(center, direction, extent, occupied, (offset, limit) => WorkflowCompilation.NextProbeOffset(center, offset, limit));

    [Theory]
    // ordinary sizes: no step is ever skipped
    [InlineData(400, 1, 200, 400, 16)]
    [InlineData(400, -1, 200, 400, 16)]
    [InlineData(-250.5, 1, 96, -250.5, 16)]
    // 5e17 (double spacing 64): a few steps per move are skipped
    [InlineData(5e17, 1, 1e18, 5e17, 16)]
    [InlineData(5e17, -1, 1e18, 5e17, 16)]
    [InlineData(-5e17, 1, 1e18, -5e17, 16)]
    // 5e21 (spacing 2^20): about 87k no-op steps before the first move, as the perf3-wide-1e22 fixture
    [InlineData(5e21, 1, 1e22, 5e21, 16)]
    [InlineData(5e21, -1, 1e22, 5e21, 16)]
    // a wide clearance: the first moved points are still blocked, so the walk continues past them
    [InlineData(5e17, 1, 1e18, 5e17, 500)]
    // the bound is reached before any probe moves: candidates stay empty
    [InlineData(5e21, 1, 2e5, 5e21, 16)]
    // a power of two: the spacing below the centre is half the spacing above, so `center - offset` moves first (offset 24, not 36)
    [InlineData(288230376151711744, 1, 1e18, 288230376151711744, 16)]
    [InlineData(288230376151711744, -1, 1e18, 288230376151711744, 16)]
    [InlineData(-288230376151711744, 1, 1e18, -288230376151711744, 16)]
    public void Skipping_gives_the_one_by_one_result(double center, int direction, double extent, double occupiedAt, double clearance)
    {
        var expected = Upstream(center, direction, extent, (occupiedAt, clearance));
        Assert.Equal(expected, Port(center, direction, extent, (occupiedAt, clearance)));
    }

    [Fact]
    public void Ordinary_steps_are_twelve_pixels()
    {
        for (double offset = 0; offset < 1200; offset += 12) Assert.Equal(offset + 12, WorkflowCompilation.NextProbeOffset(512.25, offset, 5000));
        Assert.Equal(5016, WorkflowCompilation.NextProbeOffset(512.25, 5004, 5000));   // past the bound: the walk ends
    }

    // Gemini plan review, 2026-11-09: the walk must end when no step up to a bound of 2^56 and above moves (`limit + 12 == limit`),
    // and must advance from an offset where `offset + 12 == offset`.
    [Theory]
    [InlineData(5e307, 0, 5e29)]     // a 1e30-wide node in a column right of a 1e308-wide one: no step up to the bound moves
    [InlineData(5e29, 5e29, 5e29)]   // already at the bound
    [InlineData(5e307, 4.99e291, 5e307)]
    public void Huge_offsets_advance(double center, double offset, double limit)
    {
        var next = WorkflowCompilation.NextProbeOffset(center, offset, limit);
        Assert.True(next > offset, $"{next} after {offset}");
        if (next <= limit) Assert.True(center + next != center + offset || center - next != center - offset, "a skipped-to step must move");
    }

    [Theory]
    [InlineData(double.NaN, 0, 1e6)]
    [InlineData(double.PositiveInfinity, 0, 1e6)]
    [InlineData(double.NegativeInfinity, 12, 1e30)]
    [InlineData(400, double.PositiveInfinity, double.PositiveInfinity)]
    public void Non_finite_walks_end(double center, double offset, double limit)
        => Assert.False(WorkflowCompilation.NextProbeOffset(center, offset, limit) <= limit);

    // Upstream's walk does not finish here; the port's takes the first probes that round to a different double on each side.
    [Theory]
    [InlineData(5e29, 1, 1e30)]     // the spike's 1e30-wide node
    [InlineData(5e29, -1, 1e30)]
    [InlineData(5e307, -1, 1e308)]
    public void Huge_probes_take_the_neighbouring_doubles(double center, int direction, double extent)
    {
        var found = Port(center, direction, extent, (center, 16));
        Assert.Equal([Math.BitDecrement(center), Math.BitIncrement(center)], found.Order());
    }

    // Upstream never returns on these inputs (two automatic edges, a 1e30-wide node; a 1e308-high node reached through its left
    // side, fuzzer seed 8244). The port must answer within seconds; there is no Node golden, so the receipt is the port's own.
    [Theory]
    [InlineData("perf3-wide-1e30", "workflow/solver-budget-exhausted")]
    [InlineData("perf3-tall-1e308", "workflow/explicit-pin-conflict")]
    public async Task Huge_nodes_return(string name, string code)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.workflow.json")));
        var result = await Task.Run(() => ArchifyNet.Render(DiagramType.Workflow, d)).WaitAsync(TimeSpan.FromSeconds(30));   // a hang fails, not blocks
        Assert.Equal("", result.Html);
        Assert.Equal([code], result.Diagnostics.Select(x => x.Code));
    }

    // Non-finite geometry never reaches the probe walk: `width: 1e309` (Infinity) fails the schema, and two 1.5e308-wide nodes side
    // by side (second centre at Infinity) fail the pre-routing gate, both as upstream does within milliseconds. NextProbeOffset's
    // non-finite guard is defensive only (Gemini diff review, 2026-11-09).
    [Theory]
    [InlineData("1e309", null, "schema/type")]
    [InlineData("1.5e308", "1.5e308", "workflow/non-finite-node-geometry")]
    public void Non_finite_nodes_stop_before_routing(string width, string? firstWidth, string code)
    {
        var text = Fixtures.Lf(Fixtures.Fixture("diagrams/perf3-wide-1e30.workflow.json")).Replace("1e+30", width);
        var d = DiagramLoader.Parse(text);
        if (firstWidth is not null) d["nodes"]![0]!["width"] = double.Parse(firstWidth, System.Globalization.CultureInfo.InvariantCulture);
        var result = ArchifyNet.Render(DiagramType.Workflow, d);
        Assert.Equal("", result.Html);
        Assert.Equal([code], result.Diagnostics.Select(x => x.Code));
    }

    // The explainer renders native diagrams in process with no timeout (upstream: a child process, 120 s), so the 1e30 workflow
    // referenced from a draft used to hang `deliver explainer` as well.
    [Fact]
    public async Task Explainer_with_a_huge_workflow_returns()
    {
        var dir = Directory.CreateTempSubdirectory("archify-perf3-").FullName;
        try
        {
            File.Copy(Fixtures.Fixture("diagrams/perf3-wide-1e30.workflow.json"), Path.Join(dir, "wide.workflow.json"));
            const string draft = "# A very wide node {#wide}\n\nThe diagram below has one very wide node.\n\n- It has two automatic edges.\n\n"
                + "@answer scope=\"PERF-003 test\"\n@meta locale=en\n\n## The diagram {#diagram}\n\n@diagram workflow id=wide-diagram source=wide.workflow.json title=\"Wide\"\n";
            var input = Path.Join(dir, "draft.archify.md");
            File.WriteAllText(input, draft);
            var refused = await Task.Run(() => Assert.Throws<ArchifyDiagnosticException>(() => ExplainerPipeline.Prepare(draft, new(input, Quality: "standard"))))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("explainer/diagram-render", refused.Diagnostics[0].Code);
        }
        finally { Directory.Delete(dir, true); }
    }
}
