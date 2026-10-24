using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
namespace Archify.Net.Tests;

// PORT-008: ExplainerCharts against goldens Node mints through upstream charts.mjs (tools/capture-fixtures.mjs, charts
// section). Seeds and layout: tools/chart-seeds/README.md.
public class ChartParityTests
{
    // A group is compared once it is listed; each type task adds its own.
    static readonly string[] Groups = ["data", "wrapper", "pie", "xychart", "quadrantChart", "sankey", "radar", "lines"];
    // Rows whose golden needs a later type task, keyed by that type, filtered out of the theory data (xunit 2.9.3 has no
    // dynamic skip). The type's task removes its entry.
    static readonly Dictionary<string, HashSet<(string Group, string Name)>> PendingUntil = [];
    // Rows that differ from Node by a stated deviation (PORT-008 Task 11, docs/deviations.md), each pinned by its own fact below.
    static readonly HashSet<(string Group, string Name)> KnownDeviations =
    [
        ("data", "json-deep-unread-column"), ("wrapper", "native-treeview"),
        ("xychart", "pow-clamped-equal"), ("xychart", "pow-clamped-equal-negative"),
        ("quadrantChart", "phantom-empty-quadrant-label"), ("quadrantChart", "phantom-empty-point-label"), ("quadrantChart", "stress-points"),
        .. QuirkRows.SelectMany(group => group.Value.Select(row => (group.Key, row.Name))),
        .. SankeyUnicodeRows.Select(row => ("sankey", row.Name)), .. SankeyValueRows.Select(row => ("sankey", row.Name)),
        .. RadarBoundaryRows.Select(row => ("radar", row.Name)),
        .. LineRemapRows.Select(row => (row.Group, row.Name)),
    ];
    // Fuzz cases that differ from Node by a stated deviation: index -> the quirk that explains it, checked by
    // Fuzz_deviations_are_the_stated_quirks.
    static readonly Dictionary<string, Dictionary<int, string>> FuzzDeviations = new()
    {
        ["pie"] = new() {
            [7] = "L1", [10] = "L1", [12] = "L1", [14] = "L1", [22] = "L1", [32] = "L1", [36] = "L1", [38] = "L1", [43] = "L1",
            [53] = "L1", [62] = "L1", [73] = "L1", [79] = "L1", [116] = "L1", [125] = "L1", [129] = "L1", [149] = "L1",
            [169] = "L1", [177] = "L1", [178] = "L1", [182] = "L1", [189] = "L1", [195] = "L1", [232] = "L1", [233] = "L1",
            [265] = "L1", [279] = "L1", [303] = "L1", [315] = "L1", [321] = "L1", [327] = "L1", [329] = "L1", [338] = "L1",
            [355] = "L1", [367] = "L1", [413] = "L1", [417] = "L1", [450] = "L1", [457] = "L1", [492] = "L1", [504] = "L1",
            [509] = "L1", [525] = "L1", [538] = "L1", [544] = "L1", [552] = "L1", [556] = "L1", [563] = "L1", [567] = "L1",
            [584] = "L1", [602] = "L1", [608] = "L1", [621] = "L1", [639] = "L1", [687] = "L1", [697] = "L1", [705] = "L1",
            [712] = "L1", [713] = "L1", [720] = "L1", [739] = "L1", [742] = "L1", [744] = "L1", [770] = "L1", [777] = "L1",
            [818] = "L1", [827] = "L1", [841] = "L1", [853] = "L1", [856] = "L1", [880] = "L1", [883] = "L1", [908] = "L1",
            [914] = "L1", [916] = "L1", [943] = "L1", [962] = "L1", [989] = "L1",
        },
        ["quadrantChart"] = new() {
            [12] = "Q3L1", [14] = "Q1", [19] = "L1", [25] = "L1", [52] = "L1", [78] = "Q3", [81] = "Q3L1", [120] = "Q3L1",
            [124] = "Q3L1", [125] = "L1", [134] = "Q1", [139] = "Q3L1", [169] = "L1", [191] = "Q3L1", [193] = "Q2",
            [196] = "L1", [221] = "Q3", [233] = "L1", [234] = "L1", [263] = "Q3L1", [264] = "L1", [279] = "Q3", [305] = "Q3",
            [317] = "Q3", [320] = "Q3", [332] = "Q3", [338] = "Q2", [410] = "L1", [429] = "Q3", [431] = "Q2", [487] = "Q3",
            [491] = "Q1", [492] = "Q3L1", [499] = "Q2", [513] = "Q3L1", [522] = "L1", [527] = "Q1", [557] = "Q3",
            [565] = "Q3L1", [619] = "Q3L1", [620] = "Q2", [623] = "Q3", [634] = "Q1L1", [647] = "R1", [665] = "L1",
            [686] = "Q3", [688] = "Q3L1", [689] = "Q3L1", [718] = "L1", [741] = "L1", [748] = "Q3", [749] = "L1",
            [769] = "Q3L1", [776] = "Q3L1", [783] = "Q1", [795] = "L1", [810] = "Q3", [815] = "Q3", [839] = "Q3", [849] = "L1",
            [877] = "Q3", [898] = "Q3L1", [901] = "Q3", [904] = "L1", [906] = "Q3", [926] = "Q3L1", [928] = "Q3", [948] = "L1",
            [995] = "Q3",
        },
        ["xychart"] = new() {
            [2] = "L1", [14] = "X4L1", [15] = "Q1", [16] = "X2X4", [19] = "L1", [26] = "L1", [35] = "L1", [55] = "L1",
            [95] = "L1", [98] = "X2L1", [106] = "L1", [145] = "X4L1", [149] = "L1", [164] = "L1", [176] = "L1", [180] = "L1",
            [184] = "L1", [185] = "X2X4", [186] = "L1", [191] = "X2X4L1", [193] = "L1", [200] = "Q1", [208] = "X4",
            [223] = "Q1L1", [238] = "Q1", [256] = "X3", [284] = "L1", [285] = "L1", [309] = "L1", [327] = "L1", [338] = "X4",
            [355] = "L1", [367] = "X3", [370] = "L1", [376] = "X2", [383] = "L1", [389] = "L1", [394] = "L1", [401] = "X2X4",
            [439] = "L1", [477] = "X3", [489] = "L1", [516] = "Q1", [522] = "L1", [524] = "X4", [557] = "X4", [576] = "L1",
            [583] = "L1", [586] = "X3X4", [592] = "X2X4", [610] = "L1", [612] = "X4L1", [634] = "Q1", [668] = "X2L1",
            [675] = "L1", [687] = "L1", [692] = "L1", [699] = "L1", [720] = "L1", [743] = "L1", [752] = "L1", [765] = "Q1",
            [776] = "L1", [780] = "L1", [782] = "Q1", [796] = "L1", [820] = "L1", [848] = "X2", [868] = "L1", [869] = "L1",
            [871] = "L1", [902] = "L1", [910] = "X4L1", [911] = "L1", [949] = "X4L1", [956] = "X4", [970] = "L1", [973] = "L1",
            [986] = "X4", [998] = "L1",
        },
        ["sankey"] = new() {
            [2] = "S1S4", [3] = "L1", [26] = "S4L1", [28] = "S4L1", [36] = "L1", [42] = "S1S4L1", [47] = "S4L1", [60] = "L1",
            [62] = "L1", [63] = "L1", [73] = "S3S4", [77] = "S2", [82] = "S4", [83] = "S3", [87] = "S3S4", [88] = "S4L1",
            [96] = "S3", [97] = "S3S4", [114] = "S4", [115] = "L1", [126] = "S2", [132] = "S4", [142] = "L1", [154] = "S4",
            [162] = "S1S3S4", [182] = "L1", [191] = "S4", [192] = "S1", [195] = "L1", [211] = "S3", [216] = "S4L1",
            [221] = "L1", [226] = "S4", [229] = "S2", [234] = "S1S4", [240] = "S4", [253] = "S4L1", [255] = "S2", [267] = "S1",
            [281] = "S4L1", [288] = "S4", [303] = "S1L1", [307] = "S4L1", [312] = "S4", [319] = "L1", [321] = "L1",
            [327] = "L1", [355] = "L1", [363] = "S4", [364] = "S4L1", [370] = "L1", [387] = "S3", [389] = "S2", [397] = "S4L1",
            [399] = "L1", [408] = "S1", [409] = "S4", [429] = "S4L1", [431] = "L1", [432] = "S4", [439] = "S4", [445] = "S3S4",
            [451] = "L1", [455] = "L1", [461] = "S2S3S4", [471] = "L1", [476] = "S4", [487] = "S3", [498] = "S3", [501] = "S2",
            [503] = "L1", [506] = "L1", [509] = "S4", [511] = "S4", [518] = "S4", [527] = "S3L1", [531] = "S1L1", [532] = "L1",
            [538] = "S2S4L1", [541] = "S4L1", [543] = "L1", [544] = "S3S4", [549] = "S1S4", [552] = "L1", [554] = "L1",
            [568] = "S4", [569] = "L1", [570] = "S4", [575] = "S4L1", [592] = "S4L1", [598] = "S4L1", [601] = "S3",
            [607] = "S4", [617] = "S4", [621] = "S1", [627] = "L1", [630] = "L1", [636] = "S4L1", [644] = "L1", [654] = "L1",
            [655] = "S1", [658] = "S4", [663] = "S3", [678] = "S4", [685] = "S4", [687] = "S4", [692] = "S4L1", [695] = "L1",
            [696] = "S4", [702] = "S4L1", [703] = "L1", [706] = "L1", [708] = "L1", [709] = "S2S3", [713] = "L1", [725] = "S1",
            [730] = "L1", [735] = "L1", [745] = "S1", [750] = "S2S4L1", [752] = "L1", [776] = "L1", [779] = "S3", [788] = "S3",
            [793] = "L1", [805] = "S4", [807] = "L1", [809] = "L1", [813] = "S2", [822] = "S4L1", [829] = "L1", [830] = "L1",
            [838] = "S3S4", [844] = "S4L1", [852] = "S3", [868] = "S2L1", [871] = "S2L1", [873] = "S4L1", [883] = "S4L1",
            [884] = "L1", [894] = "S3", [907] = "S4", [925] = "L1", [928] = "S4", [933] = "S4L1", [942] = "S4", [947] = "L1",
            [950] = "L1", [958] = "S4L1", [967] = "S3", [968] = "S4", [970] = "L1", [980] = "S4", [990] = "L1", [995] = "S4L1",
        },
        ["radar"] = new() {
            [12] = "L1", [26] = "L1", [38] = "L1", [52] = "L1", [88] = "L1", [164] = "L1", [188] = "L1", [198] = "L1",
            [217] = "L1", [219] = "L1", [224] = "R2", [229] = "L1", [232] = "L1", [243] = "L1", [254] = "R2L1",
            [257] = "L1", [274] = "L1", [284] = "L1", [292] = "L1", [295] = "L1", [301] = "L1", [319] = "L1", [343] = "L1",
            [345] = "L1", [358] = "L1", [383] = "L1", [391] = "L1", [405] = "L1", [435] = "R1", [453] = "L1", [475] = "L1",
            [483] = "L1", [500] = "L1", [517] = "R1", [529] = "L1", [566] = "L1", [570] = "R1", [610] = "L1", [611] = "R1",
            [619] = "R1", [643] = "L1", [665] = "L1", [667] = "L1", [686] = "L1", [692] = "L1", [717] = "L1", [737] = "L1",
            [759] = "L1", [767] = "L1", [806] = "L1", [838] = "L1", [847] = "L1", [851] = "L1", [852] = "L1", [871] = "L1",
            [872] = "L1", [882] = "R1", [884] = "L1", [900] = "L1", [903] = "L1", [915] = "L1", [934] = "L1", [970] = "L1",
            [971] = "L1",
        },
    };

    public static TheoryData<string, string> Targets()
    {
        var data = new TheoryData<string, string>();
        foreach (var group in Groups)
        {
            foreach (var seed in SeedsOf(group))
            {
                var name = seed!["name"]!.GetValue<string>();
                if (!PendingUntil.Values.Any(rows => rows.Contains((group, name))) && !KnownDeviations.Contains((group, name))) data.Add(group, name);
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
            if (FuzzFailure(golden, RunText, FuzzDeviations.GetValueOrDefault(group)) is { } failure) Assert.Fail(failure);
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

    // UPSTREAM-003: charts.mjs:11 keys NATIVE by `treeview`, but Pele detects `treeView`, so upstream never redirects a
    // treeView-beta fence. The port keys it `treeView` (Martin, 2026-10-23) and redirects it like every other native type.
    [Fact] public void TreeView_redirect_is_the_stated_deviation()
    {
        Assert.Contains("A chart fence supports", Fixtures.Lf(Golden("wrapper", "native-treeview")));
        const string port = "{\n  \"error\": \"Mermaid treeView is drawn with the native tree shorthand, which Archify validates and lets readers explore.\",\n  \"example\": \"@tree id=...\"\n}";
        Assert.Equal(port, Run(Seed("wrapper", "native-treeview")));
        Assert.Equal(port, RunText("treeView-beta\n  a\n    b"));
    }

    // xychart render.ts: upstream tests an exact y range for `lo !== hi` before clamping it to ±1e300, so `1e301 --> 1e302`
    // collapses to one value and divides by zero: 100 ticks at NaN. The port tests the clamped ends and falls back to the
    // data, as a collapsed range does, so both rows draw what Node draws for `y-axis 5 --> 5`.
    [Fact] public void Clamped_equal_y_range_is_the_stated_deviation()
    {
        var reference = Fixtures.Lf(Golden("xychart", "pow-clamped-equal-reference"));
        Assert.DoesNotContain("NaN", reference);
        foreach (var name in new[] { "pow-clamped-equal", "pow-clamped-equal-negative" })
        {
            Assert.Contains("M40,NaNH472", Fixtures.Lf(Golden("xychart", name)));
            Assert.Equal(reference, Run(Seed("xychart", name)) + "\n");
        }
    }

    // quadrant render.ts:96,179: upstream reserves a zero-size "taken" box for a label with no text (an empty quadrant, a
    // point label that trims to nothing), so a point label whose box holds that invisible spot is pushed away from empty
    // space. The port reserves boxes only for drawn labels.
    [Fact] public void Empty_label_reserves_no_space_is_the_stated_deviation()
    {
        // Empty quadrant-1: P goes right in Node. The reference moves only the invisible anchor (quadrantTextTopPadding: 100,
        // same 400x400 size), and Node then draws P above; the port draws that reference byte for byte.
        var quadrant = Fixtures.Lf(Golden("quadrantChart", "phantom-empty-quadrant-label"));
        Assert.Contains("x=\\\"309.33\\\" y=\\\"36.46\\\" text-anchor=\\\"middle\\\">P<", quadrant);
        var reference = Fixtures.Lf(Golden("quadrantChart", "phantom-empty-quadrant-label-reference"));
        Assert.Contains("x=\\\"296\\\" y=\\\"20.46\\\" text-anchor=\\\"middle\\\">P<", reference);
        Assert.Equal(reference, Run(Seed("quadrantChart", "phantom-empty-quadrant-label")) + "\n");

        // A point labelled " " above P: Node sends P right; the port draws P where Node draws it without that point (the
        // control), and everything else as Node does.
        const string right = "<text class=\\\"pele-label\\\" x=\\\"117.34\\\" y=\\\"203.5\\\" text-anchor=\\\"middle\\\">P</text>";
        const string above = "<text class=\\\"pele-label\\\" x=\\\"104\\\" y=\\\"187.5\\\" text-anchor=\\\"middle\\\">P</text>";
        var point = Fixtures.Lf(Golden("quadrantChart", "phantom-empty-point-label"));
        Assert.Contains(right, point);
        Assert.Contains(above, Fixtures.Lf(Golden("quadrantChart", "phantom-empty-point-label-control")));
        Assert.Equal(point.Replace(right, above), Run(Seed("quadrantChart", "phantom-empty-point-label")) + "\n");

        // stress-points (250 points, no quadrant text) moves some labels off the empty quadrants' anchors; only the label group
        // differs from Node.
        var stress = Fixtures.Lf(Golden("quadrantChart", "stress-points"));
        var port = Run(Seed("quadrantChart", "stress-points")) + "\n";
        Assert.NotEqual(stress, port);
        Assert.Equal(OutsidePointLabels(stress), OutsidePointLabels(port));
    }

    // Mermaid grammar slips Pele mirrors (quadrant.jison), fixed in the port:
    //   Q1 `[^\}]\%\%[^\n]*` takes the character before a comment with it;
    //   Q2 the coordinate `0(.\d+)?` has an unescaped dot, so `0x1` is 1 and `0,5` drops the point;
    //   Q3 keywords end without a word boundary and fire anywhere in a line; the port takes a keyword only as a whole word
    //      leading a statement, and a statement in point form (`Title: [0.5, 0.5]`) is a point.
    // Each row's port output equals the Node golden of its reference row: the same input written so that upstream already
    // reads it the corrected way, or (Q2) a coordinate upstream already rejects, at the same line.
    static (string Name, string Quirk, string Reference)[] QuadrantQuirkRows =>
    [
        ("quirk-comment-eats-char", "Q1", "quirk-comment-eats-char-reference"),
        ("quirk-comment-eats-style-digit", "Q1", "quirk-comment-eats-style-digit-reference"),
        ("comment-glued", "Q1", "comment-glued-reference"),
        ("stress-comment-markers", "Q1", "stress-comment-markers-reference"),
        ("quirk-coordinate-hex", "Q2", "quirk-coordinate-reference"),
        ("quirk-coordinate-exponent", "Q2", "quirk-coordinate-reference"),
        ("quirk-coordinate-comma", "Q2", "quirk-coordinate-reference"),
        ("quirk-coordinate-semicolon", "Q2", "quirk-coordinate-reference"),
        ("coord-binary", "Q2", "quirk-coordinate-reference"),
        ("coord-comma-dot", "Q2", "quirk-coordinate-reference"),
        ("coord-exponent", "Q2", "quirk-coordinate-reference"),
        ("coord-hex-one", "Q2", "quirk-coordinate-reference"),
        ("coord-zero-then-space", "Q2", "quirk-coordinate-reference"),
        ("render-extreme-coordinates", "Q2", "quirk-coordinate-reference"),
        ("render-not-a-number", "Q2", "quirk-coordinate-reference"),
        ("place-out-of-range-not-counted", "Q2", "place-out-of-range-reference"),
        ("quirk-keyword-x-axis-name", "Q3", "quirk-keyword-x-axis-name-reference"),
        ("quirk-keyword-y-axis-name", "Q3", "quirk-keyword-y-axis-name-reference"),
        ("quirk-keyword-quadrant-name", "Q3", "quirk-keyword-quadrant-name-reference"),
        ("quirk-keyword-chart-name", "Q3", "quirk-keyword-chart-name-reference"),
        ("quirk-keyword-in-text", "Q3", "quirk-keyword-in-text-reference"),
        ("keyword-glued", "Q3", "keyword-glued-reference"),
        ("keyword-x-axis-glued", "Q3", "keyword-x-axis-glued-reference"),
        ("quirk-comment-eats-after-dot", "Q1", "quirk-comment-eats-after-dot-reference"),
        ("quirk-comment-eats-digit", "Q1", "quirk-comment-eats-digit-reference"),
        ("quirk-coordinate-y-hex", "Q2", "quirk-coordinate-reference"),
        ("keyword-whole-word-in-text", "Q3", "keyword-whole-word-in-text-reference"),
        ("quirk-keyword-quadrant-1x", "Q3", "quirk-keyword-quadrant-1x-reference"),
        ("quirk-keyword-quadrant-12", "Q3", "quirk-keyword-quadrant-12-reference"),
        ("quirk-keyword-mid-quadrant", "Q3", "quirk-keyword-mid-quadrant-reference"),
        ("quirk-keyword-mid-x-axis", "Q3", "quirk-keyword-mid-x-axis-reference"),
        ("quirk-keyword-mid-classdef", "Q3", "quirk-keyword-mid-classdef-reference"),
        ("quirk-keyword-title-point", "Q3", "quirk-keyword-title-point-reference"),
        ("quirk-keyword-acc-title-point", "Q3", "quirk-keyword-acc-title-point-reference"),
        ("quirk-keyword-x-axis-point", "Q3", "quirk-keyword-x-axis-point-reference"),
        ("quirk-keyword-quadrant-point", "Q3", "quirk-keyword-quadrant-point-reference"),
        ("quirk-keyword-mid-quadrant-text", "Q3", "quirk-keyword-mid-quadrant-text-reference"),
        ("quirk-keyword-mid-axis-text", "Q3", "quirk-keyword-mid-axis-text-reference"),
        ("quirk-keyword-quadrant-1st-text", "Q3", "quirk-keyword-quadrant-1st-text-reference"),
        ("stress-point-form-rescan", "Q3", "stress-point-form-rescan-reference"),
    ];

    // Mermaid grammar slips Pele mirrors (xychart.jison), fixed in the port (Task 6 follow-up, PORT-010):
    //   Q1 `[^\}]\%\%[^\n]*` takes the character before a comment with it; only whitespace goes with the comment now;
    //   X1 `setOrientation` compares case-sensitively, so `HORIZONTAL` draws vertical;
    //   X2 `(?:"vertical"|"horizontal")` fires anywhere and without a word boundary; the port takes the orientation only as a
    //      whole word straight after the chart keyword;
    //   X3 the md_string rules are broken by a quoting slip (Pele's BROKEN_RULE); the port reads "`...`" as the quadrant
    //      grammar does;
    //   X4 unquoted text joins its tokens with '' (`title Sales Report` is `SalesReport`); the port joins them with the
    //      whitespace between them: same-line whitespace as written, a gap holding a line break as one space (Martin), and
    //      text in a gap that gave no token (`""`, `accDescr{}`) dropped.
    // A reference row writes the same chart so that upstream reads it the corrected way: quoted (X2, X4), without backticks
    // (X3), in lower case (X1), with a space before `%%` (Q1), or (X3 lexical errors) a backtick upstream already rejects.
    static (string Name, string Quirk, string Reference)[] XyChartQuirkRows =>
    [
        .. new[] { "quirk-comment-eats-number", "quirk-comment-eats-char", "quirk-comment-eats-bracket", "comment-glued", "stress-comment-markers" }
            .Select(name => (name, "Q1", $"{name}-reference")),
        .. new[] { "horizontal-uppercase", "quirk-orientation-capitalised" }.Select(name => (name, "X1", $"{name}-reference")),
        .. new[]
        {
            "title-starts-with-vertical", "series-title-starts-with-horizontal", "quirk-orientation-title", "quirk-orientation-series",
            "quirk-orientation-axis-title", "quirk-orientation-category", "quirk-orientation-title-words", "quirk-orientation-whole-word",
        }.Select(name => (name, "X2", $"{name}-reference")),
        .. new[]
        {
            "broken-rule-in-title", "oracle-5", "oracle-6", "quirk-md-title", "quirk-md-axis-title", "quirk-md-category", "quirk-md-series",
            "quirk-md-point-label", "quirk-md-emphasis", "quirk-md-unclosed-at-end", "quirk-md-opening-only", "stress-md-string-50k",
            // Malformed markdown strings are lexical errors, as in quadrant (Martin, fix round 1 M2).
            "quirk-md-inner-backtick", "quirk-md-inner-backtick-series", "quirk-md-label-unclosed",
        }.Select(name => (name, "X3", $"{name}-reference")),
        .. new[]
        {
            "stress-title-words", "quirk-unquoted-title", "quirk-unquoted-axis-title", "quirk-unquoted-category", "quirk-unquoted-series",
            "quirk-unquoted-y-title", "quirk-unquoted-tabs", "quirk-unquoted-mixed", "quirk-unquoted-across-lines", "stress-unquoted-words-50k",
            // A gap holding a line break is one space (Martin, fix round 1 I1).
            "quirk-unquoted-break-title", "quirk-unquoted-break-twice", "quirk-unquoted-break-crlf", "quirk-unquoted-break-category",
            "quirk-unquoted-break-y-title", "quirk-unquoted-break-series",
            // Text in a gap that gave no token is dropped; the gap keeps its whitespace (fix round 1 C1).
            "quirk-unquoted-empty-string", "quirk-unquoted-empty-md", "quirk-unquoted-empty-accdescr", "quirk-unquoted-empty-string-axis",
            "quirk-unquoted-empty-string-category",
        }.Select(name => (name, "X4", $"{name}-reference")),
    ];

    // Upstream slips in sankey, fixed in the port (Task 8):
    //   S1 Mermaid's prepareTextForParsing, commented "remove all trailing spaces for each row", has no `m` flag, so a blank
    //      row, `sankey ` with a trailing blank, a tab-indented row or a quoted field with blanks at its row's edge is a parse
    //      error; the port strips blanks at each row's edges outside quoted fields;
    //   S2 render.ts writes a label's name, prefix and suffix with esc(), so `&amp;` shows as typed while the band's <title>
    //      decodes it; the port decodes them in the label too;
    //   S3 (Martin, fix round 1) the CSV alphabet is ASCII, so `Café` is an error; the port takes Pele 1.9.0's alphabet
    //      (U+00A0 up, no surrogate halves, U+FFFE/U+FFFF or controls). Mermaid's `#…;` placeholders are non-ASCII, so an
    //      entity code in a name now lexes and decodes;
    //   S4 (Martin, fix round 1, with the controller's ruling) parseFloat reads `abc` or a blank as no flow and `1x` as 1;
    //      the port takes only a whole decimal number and reports anything else (SankeyValueRows).
    // A reference row writes the same chart without those blanks (S1), with the decoded character (S2) or the decoded text
    // (S3). S3 rows with non-ASCII names are checked against ASCII stand-ins instead (SankeyUnicodeRows).
    static (string Name, string Quirk, string Reference)[] SankeyQuirkRows =>
    [
        .. new[]
        {
            "quirk-rows-header-space", "quirk-rows-header-tab", "quirk-rows-tab-indent", "quirk-rows-blank-row", "quirk-rows-blank-rows-indented",
            "quirk-rows-quoted-indent", "quirk-rows-quoted-trailing", "quirk-rows-quoted-trailing-value", "quirk-rows-nbsp-row",
            "quirk-rows-crlf-blank-row", "quirk-rows-front-matter-header-space", "quirk-rows-init-header-space", "quirk-rows-stress-50k",
            "render-rejects-5", "unicode-nbsp-row",
        }.Select(name => (name, "S1", $"{name}-reference")),
        .. new[]
        {
            "quirk-entity-name-amp", "quirk-entity-name-quot", "quirk-entity-name-lt", "quirk-entity-prefix", "quirk-entity-suffix",
            "quirk-entity-outlined", "render-hostile-t10-p10",
        }.Select(name => (name, "S2", $"{name}-reference")),
        .. new[] { "names-entity-hash", "render-hostile-t3-p10" }.Select(name => (name, "S3", $"{name}-reference")),
    ];

    // S3 rows with non-ASCII names: the port's output is Node's output for the reference (the same chart with ASCII stand-ins)
    // with each stand-in swapped back in the SVG. Every name stays inside the chart, so its measured width moves nothing.
    static (string Name, string Reference, (string StandIn, string Name)[] Swaps)[] SankeyUnicodeRows =>
    [
        ("quirk-unicode-cafe", "quirk-unicode-cafe-reference", [("Cafe", "Café")]),
        ("quirk-unicode-cjk", "quirk-unicode-cjk-reference", [("Grid", "电网"), ("Indy", "工业"), ("Loss", "损耗")]),
        ("quirk-unicode-emoji", "quirk-unicode-emoji-reference", [("Sun", "🌞"), ("Home", "🏠")]),
        ("quirk-unicode-quoted", "quirk-unicode-quoted-reference", [("Electricite, reseau", "Électricité, réseau")]),
        ("quirk-unicode-nbsp-inside", "quirk-unicode-nbsp-inside-reference", [("AxB", "A\u00a0B")]),
        ("quirk-unicode-quoted-emoji", "quirk-unicode-quoted-emoji-reference", [("X sun", "\U0001F31E sun")]),
        ("names-non-ascii", "names-non-ascii-reference", [("Cafe", "Café")]),
        ("render-rejects-6", "names-non-ascii-reference", [("Cafe", "Café")]),   // Pele's own "rejects Café" case
    ];

    // S4 rows: the port's error, as text, with the value as written (trimmed) and its line in the fence (LineRemapRows).
    static (string Name, string Value, int Line)[] SankeyValueRows =>
    [
        ("quirk-value-abc", "abc", 3), ("quirk-value-quoted-blank", "", 2), ("quirk-value-nan", "NaN", 2),
        ("quirk-value-after-blank-lines", "x", 5), ("quirk-value-after-front-matter", "x", 5), ("quirk-value-quoted-multiline-name", "oops", 3),
        ("quirk-value-first-of-two", "x", 2), ("quirk-value-1x", "1x", 3), ("quirk-value-thousands", "1,000", 3),
        ("quirk-value-12abc", "12abc", 3), ("quirk-value-1e", "1e", 3), ("quirk-value-percent", "10%", 3),
        ("quirk-value-infinity", "Infinity", 3), ("quirk-value-hex", "0x10", 3), ("quirk-value-multiline", "1\n2", 2),
        // Existing rows: parseFloat's prefix reads (`1e`, `1 %% note`, `1` + 4,000 spaces + `x`) and blanks.
        ("values-numbers", "1e", 4), ("comment-inline", "1 %% note", 2), ("stress-one-blank-run", "1" + new string(' ', 4000) + "x", 2),
        ("stress-value-digits-50k", new string('1', 49970) + "x", 2), ("rows-blank-last-field", "", 2),
        ("layout-not-positive", "NaN", 2), ("stress-not-numbers", "x", 2),
        ("render-not-numbers", "abc", 2), ("render-hostile-t2-p2", "<img src=x onerror=alert(1)>", 2),
        ("render-hostile-t2-p9", "javascript:alert(1)", 2),
        ("render-hostile-t5-p5", "<foreignObject><iframe src=javascript:alert(1)></iframe></foreignObject>", 2),
        ("render-hostile-t5-p12", "vbscript:msgbox(1)", 2),
    ];

    static string ValueError(string value, int line) => ExplainerCharts.Json(new JsonObject
    {
        ["error"] = $"Sankey diagrams need a number for each link's value, and {Js.Quote(value)} on line {line} is not one.",
        ["line"] = line,
        ["example"] = "pie title Share\n  \"A\" : 60\n  \"B\" : 40",
    });

    // Pele 1.9.0's name check (fix round 2): the parse error's detail, which charts.mjs cuts to its first line, is 1.9.0's
    // "Invalid XML character in node name", at the name's line. Through charts.mjs each row equals Node (Controls).
    [Theory]
    [InlineData("sankey\nA#65534;B,b,1", 2)]
    [InlineData("sankey\nA&#65535;B,b,1", 2)]
    [InlineData("sankey\nA#1;B,c,1\nAB,d,2", 2)]
    [InlineData("sankey\na,b,1\nb,C#65535;,2", 3)]
    public void Sankey_name_check_is_pele_1_9s(string text, int line)
    {
        var e = Assert.Throws<PeleException>(() =>
            Pele.Sankey.Parse(Pele.Sankey.PrepareTextForParsing(Pele.Preprocess.EncodeEntities(text) + "\n"), new Pele.Sankey.SankeyDb()));
        Assert.StartsWith($"Parse error on line {line}:\n", e.Message);
        Assert.EndsWith("\nInvalid XML character in node name", e.Message);
        Assert.Equal(line, e.Line);
    }

    [Fact] public void Sankey_unicode_names_and_unreadable_values_are_the_stated_deviations()
    {
        foreach (var (name, reference, swaps) in SankeyUnicodeRows)
        {
            var node = Fixtures.Lf(Golden("sankey", name));
            Assert.True(node.Contains("\"error\""), $"{name}: Node draws it");
            Assert.True(QuirkIn("sankey", Seed("sankey", name)["text"]!.GetValue<string>()).Contains("S3"), $"{name}: input shows no S3");
            var expected = (JsonObject)DiagramLoader.ParseNode(Fixtures.Lf(Golden("sankey", reference)))!;
            var svg = expected["svg"]!.GetValue<string>();
            foreach (var (standIn, swapped) in swaps)
            {
                Assert.Contains(standIn, svg);
                svg = svg.Replace(standIn, swapped, StringComparison.Ordinal);
            }
            expected["svg"] = svg;
            Assert.Equal(ExplainerCharts.Json(expected), Run(Seed("sankey", name)));
        }
        foreach (var (name, value, line) in SankeyValueRows)
        {
            Assert.True(QuirkIn("sankey", Seed("sankey", name)["text"]!.GetValue<string>()).Contains("S4"), $"{name}: input shows no S4");
            Assert.NotEqual(Fixtures.Lf(Golden("sankey", name)), ValueError(value, line) + "\n");
            Assert.Equal(ValueError(value, line), Run(Seed("sankey", name)));
        }
    }

    // Upstream slips in radar, fixed in the port (Task 9):
    //   R1 GRATICULE (`circle|polygon`), BOOLEAN (`true|false`) and TITLE (`title`, which may stand alone) end without a word
    //      boundary, so a name that starts with one of those words (`circles`, `trueNorth`, `titles`) lexes as that token and
    //      a name, and the chart fails to parse. The port reads such a name as a name: GRATICULE and BOOLEAN give way to a
    //      longer ID, as the keywords do, and a bare `title` only where ID would read no more than `title`;
    //   R2 the "Missing entry for axis" message writes the label as parsed, so a Mermaid code shows as its placeholder
    //      (`ﬂ°°35¶ßx`) and an HTML entity as typed; the port decodes it, as the chart draws the label.
    // R1 rows: the port's output is Node's for the reference (the same chart with a stand-in first letter on each such name,
    // which upstream reads as a name) with the stand-ins swapped back. A stand-in letter has the advance of the letter it
    // stands for (c/k, p/b, t/f), so a name drawn as its own label measures the same. R2 rows are in QuirkRows: the
    // reference writes the label as the chart draws it.
    static (string Name, string Reference, (string StandIn, string Word)[] Swaps)[] RadarBoundaryRows =>
    [
        .. new (string, (string, string)[])[]
        {
            ("quirk-boundary-graticule-axis", [("kircles", "circles")]), ("quirk-boundary-polygon-axis", [("bolygonal", "polygonal")]),
            ("quirk-boundary-true-axis", [("frueNorth", "trueNorth")]), ("quirk-boundary-false-curve", [("talsePositives", "falsePositives")]),
            ("quirk-boundary-circle-curve-second", [("kircleback", "circleback")]), ("quirk-boundary-title-axis", [("fitles", "titles")]),
            ("quirk-boundary-title-after-comma", [("fitled", "titled")]), ("quirk-boundary-title-dash", [("fitle-x", "title-x")]),
            ("quirk-boundary-title-curve", [("fitleCurve", "titleCurve")]),
            ("quirk-boundary-named-entries", [("frueA", "trueA"), ("talseB", "falseB"), ("fitleC", "titleC")]),
            ("quirk-boundary-crlf", [("bolygons", "polygons")]), ("quirk-boundary-stress-50k", [("kircles", "circles")]),
        }.Select(row => (row.Item1, $"{row.Item1}-reference", row.Item2)),
        ("oracle-23", "oracle-23-reference", [("kircles", "circles")]),   // `axis circles`, its own label
    ];

    static (string Name, string Quirk, string Reference)[] RadarQuirkRows =>
        [.. new[] { "entity-code", "html-entity", "named-code", "numeric-html", "unknown-code" }.Select(name => ($"quirk-message-{name}", "R2", $"quirk-message-{name}-reference"))];

    [Fact] public void Radar_name_boundaries_are_the_stated_deviation()
    {
        foreach (var (name, reference, swaps) in RadarBoundaryRows)
        {
            Assert.True(Fixtures.Lf(Golden("radar", name)).Contains("\"error\": \""), $"{name}: Node draws it");
            Assert.True(QuirkIn("radar", Seed("radar", name)["text"]!.GetValue<string>()).Contains("R1"), $"{name}: input shows no R1");
            var expected = (JsonObject)DiagramLoader.ParseNode(Fixtures.Lf(Golden("radar", reference)))!;
            var svg = expected["svg"]!.GetValue<string>();
            foreach (var (standIn, word) in swaps)
            {
                Assert.Equal(Pele.Measurer.Width(word, 1000, Pele.Style.None), Pele.Measurer.Width(standIn, 1000, Pele.Style.None));
                Assert.Contains($"data-id=\"{standIn}\"", svg);
                svg = svg.Replace(standIn, word, StringComparison.Ordinal);
            }
            expected["svg"] = svg;
            Assert.Equal(ExplainerCharts.Json(expected), Run(Seed("radar", name)));
        }
    }

    // UPSTREAM-007 (mermaid#8537, pele#61; Martin 2026-10-23): upstream counts an error's line in the text the diagram parsed,
    // after front matter, `%%` comment lines, directives and leading blank lines (and in sankey blank lines and, with S1,
    // rows of blanks) are gone, and archify's pie legend drops a `showData` written on a later line than `pie` with the line
    // breaks before it. The port counts lines as written in the fence: `\r\n`, `\n` and a lone `\r` each end one, and an
    // error at the end of the text is on the last content line (the line of the last kept character that is not blank), not
    // on a blank or comment line after it, nor on the line after the "\n" Pele appends.
    // Each row's port output is Node's (for the row, or for its reference) with the line, in `line` and in the message, set
    // to the fence line written here by hand; FenceOracle checks that line holds the error's text too.
    static (string Group, string Name, int Line, string? Reference)[] LineRemapRows =>
    [
        ("lines", "pie-front-matter", 5, null), ("lines", "pie-front-matter-indented", 5, null), ("lines", "pie-comment", 3, null),
        ("lines", "pie-two-comments", 4, null), ("lines", "pie-directive", 3, null), ("lines", "pie-directive-multiline", 5, null),
        ("lines", "pie-leading-blank-lines", 4, null), ("lines", "pie-crlf", 3, null), ("lines", "pie-lone-cr", 3, null),
        ("lines", "pie-crlf-front-matter", 7, null), ("lines", "pie-parse-error", 3, null), ("lines", "pie-eof", 3, null),
        ("lines", "pie-unclosed-string", 4, null), ("lines", "pie-color-semicolon", 3, null), ("lines", "pie-entity-before-error", 3, null),
        ("lines", "pie-html-attribute-before-error", 4, null), ("lines", "pie-show-data-next-line", 4, null),
        ("lines", "pie-show-data-blank-lines", 6, null), ("lines", "pie-show-data-same-line", 4, null),
        ("lines", "pie-legend-blank-after-slice", 4, null), ("lines", "pie-crlf-parse-error", 3, null), ("lines", "pie-crlf-eof", 3, null),
        ("lines", "xychart-comment", 4, null), ("lines", "xychart-front-matter", 5, null), ("lines", "xychart-directive-mid-line", 3, null),
        ("lines", "xychart-directive-before-error", 3, null), ("lines", "xychart-lexical", 3, null), ("lines", "xychart-eof", 4, null),
        ("lines", "quadrant-comment", 4, null), ("lines", "quadrant-front-matter", 5, null), ("lines", "quadrant-crlf", 3, null),
        ("lines", "quadrant-parse-error", 3, null), ("lines", "quadrant-eof", 4, null),
        // An error at the end is on the last content line, not on a comment, break or blank lines after it.
        ("lines", "quadrant-eof-then-comment", 2, null), ("lines", "quadrant-eof-then-break", 2, null),
        ("lines", "quadrant-eof-then-blank-lines", 2, null), ("lines", "xychart-eof-then-comment", 2, null),
        ("lines", "xychart-eof-then-break", 2, null), ("lines", "xychart-eof-then-blank-lines", 2, null),
        ("lines", "sankey-eof-then-comment", 3, null),
        ("lines", "sankey-blank-lines", 6, null), ("lines", "sankey-leading-blank-line", 4, null), ("lines", "sankey-comment", 4, null),
        ("lines", "sankey-front-matter", 6, null), ("lines", "sankey-blank-row-reference", 4, null), ("lines", "sankey-crlf", 4, null),
        ("lines", "sankey-lone-cr", 4, null), ("lines", "sankey-eof", 3, null), ("lines", "sankey-name-check", 3, null),
        // S1 drops the row of blanks that Node stops at; the port then stops at `c d`, as Node does without that row.
        ("lines", "sankey-blank-row", 4, "sankey-blank-row-reference"),
        ("lines", "radar-front-matter", 6, null), ("lines", "radar-comment", 4, null), ("lines", "radar-directive", 3, null),
        ("lines", "radar-crlf-comment", 4, null), ("lines", "radar-eof-then-comment", 3, null), ("lines", "radar-eof-then-blank-lines", 3, null),
        ("lines", "radar-lexical", 4, null),
        // Existing rows.
        ("wrapper", "front-matter-title-line-shift", 8, null),
        ("pie", "comment-above-syntax-error", 3, null), ("pie", "comment-first-line-error", 4, null), ("pie", "front-matter-syntax-error-line", 6, null),
        ("xychart", "fm-syntax-error-line", 6, null), ("quadrantChart", "fm-syntax-error-line", 6, null), ("quadrantChart", "comment-above-error", 4, null),
        ("sankey", "error-line-after-blank-lines", 6, null), ("sankey", "error-line-after-comment", 5, null),
        ("sankey", "error-line-after-front-matter", 6, null), ("sankey", "rows-blank-row-then-error", 4, null),
        ("radar", "error-after-comment", 4, null), ("radar", "error-after-directive", 4, null), ("radar", "error-after-front-matter", 6, null),
        ("radar", "error-eof-after-comment", 3, null),
        // An error at the end of the text, with nothing removed: Node counts the "\n" Pele appends as one more line.
        ("xychart", "spec-parse-line-data-throws-error-if-data-is-not-provided", 4, null),
        ("xychart", "spec-parse-bar-should-throw-error-if-data-is-not-provided", 4, null),
        ("xychart", "render-hostile-t1-p1", 3, null), ("xychart", "stress-unclosed-string", 2, null),
        ("quadrantChart", "stress-unclosed-acc-descr", 1334, null), ("quadrantChart", "stress-unclosed-string", 2, null),
        ("quadrantChart", "stress-unclosed-md-string", 2, null), ("quadrantChart", "acc-descr-unclosed", 2, null),
        ("radar", "error-eof", 3, null), ("radar", "error-curve-unclosed", 3, null),
    ];

    [Fact] public void Error_lines_count_the_fence_as_written()
    {
        foreach (var (group, name, line, reference) in LineRemapRows)
        {
            var node = (JsonObject)DiagramLoader.ParseNode(Fixtures.Lf(Golden(group, reference ?? name)))!;
            Assert.True(node["line"] is JsonValue nodeLine && nodeLine.GetValue<double>() != line, $"{group}/{name}: Node already reports line {line}");
            Assert.Equal(WithLine(node, line), Run(Seed(group, name)));
            Assert.Null(FenceOracle(Seed(group, name)["text"]!.GetValue<string>(), line));
        }
        // Every `lines` seed is a row here or a control.
        foreach (var seed in SeedsOf("lines").Cast<JsonObject>().Select(seed => seed["name"]!.GetValue<string>()))
            Assert.True(seed.StartsWith("control-", StringComparison.Ordinal) || LineRemapRows.Any(row => row.Group == "lines" && row.Name == seed), seed);
    }

    // Node's error result with its line set to `line`, in `line` and in the message's "line N"; null when the message does
    // not name its line once.
    static string? WithLine(JsonObject node, int line)
    {
        var copy = (JsonObject)node.DeepClone();
        var number = new Regex($"line {(int)copy["line"]!.GetValue<double>()}(?![0-9])");
        var error = copy["error"]!.GetValue<string>();
        if (number.Matches(error).Count != 1) return null;
        copy["error"] = number.Replace(error, $"line {line}");
        copy["line"] = line;
        return ExplainerCharts.Json(copy);
    }

    // An oracle apart from the port's map: the fence's line `line` holds the error's text. That is the text at the snippet's
    // caret up to a blank (or, at a blank or the line's end, the last text before it) with Mermaid's entity placeholders
    // written back as `#…;`; for an error past the snippet's last character, the line of the last character preprocessing
    // keeps (KeptLine); for S4, the first line of the value it names. Returns why it does not hold, or null. It is a
    // containment check: for a short token (`x`, `1`, `"`) a neighbouring line often holds it too, so it cannot tell
    // neighbouring lines apart there; the hand-written LineRemapRows lines and the sankey checker cover that.
    static readonly Regex ReBreak = new(@"\r\n|\n|\r");
    static readonly Regex RePlaceholder = new("ﬂ°°?([A-Za-z0-9_]+)¶ß");
    static readonly Regex ReS4 = new(@"\ASankey diagrams need a number for each link's value, and (""(?:[^""\\]|\\.)*"") on line [0-9]+ is not one\.\z");
    static string? FenceOracle(string fence, int line)
    {
        PeleException error;
        try { ExplainerCharts.Render(fence); return "no error"; }
        catch (PeleException e) { error = e; }
        if (error.Line != line) return $"the error is on line {error.Line}";
        var lines = ReBreak.Split(fence);
        if (line < 1 || line > lines.Length) return $"line {line} of {lines.Length}";
        string token;
        if (ReS4.Match(error.Message) is { Success: true } s4)
        {
            token = Js.Trim(ReBreak.Split(DiagramLoader.ParseNode(s4.Groups[1].Value)!.GetValue<string>())[0]);
        }
        else
        {
            var parts = error.Message.Split('\n');
            token = Token(parts[1], parts[2].Length - 1);
            if (token == "") return KeptLine(fence) is var kept && kept == line ? null : $"the text ends on line {kept}";
        }
        // The token as written may hold a placeholder the fence spells `#…;`, or a directive the fence has inside it, or run on
        // past the end of the line when a directive took the line break with it (its trailing \s*).
        var written = lines[line - 1];
        var bare = ReDropped.Replace(written, "");
        var tail = bare.TrimEnd().Split(' ', '\t')[^1];
        bool Holds(string t) => written.Contains(t, StringComparison.Ordinal) || bare.Contains(t, StringComparison.Ordinal)
            || bare.Length < written.Length && tail != "" && t.StartsWith(tail, StringComparison.Ordinal);
        return Holds(token) || Holds(RePlaceholder.Replace(token, "#$1;")) ? null : $"line {line} {Js.Quote(written)} has no {Js.Quote(token)}";
    }

    static string Token(string snippet, int caret)
    {
        bool Blank(int k) => Js.IsSpace(snippet[k]);
        var start = Math.Min(caret, snippet.Length);
        while (start < snippet.Length && Blank(start)) start++;
        var end = start;
        if (start < snippet.Length)
        {
            while (end < snippet.Length && !Blank(end)) end++;
            return snippet[start..end];
        }
        end = Math.Min(caret, snippet.Length);
        while (end > 0 && Blank(end - 1)) end--;
        start = end;
        while (start > 0 && !Blank(start - 1)) start--;
        return snippet[start..end];
    }

    // preprocess.ts:16-17 written again over the fence: comment lines (from a line start, with the blanks before them) and
    // `%%{…}%%` directives go. An error at the end is on the last content line: the line of the last character left that is
    // not blank, counting the line breaks before it.
    static readonly Regex ReDropped = new($@"%%\{{[{WsBody}]*[A-Za-z0-9_](?:(?!\}}%%)[\s\S])*(?:\}}%%)?|(?<=\A|[\n\r  ])[{WsBody}]*%%(?!\{{)[^\n\r]+(?:\r\n|\n|\r)?");
    static int KeptLine(string fence)
    {
        var dropped = new bool[fence.Length];
        foreach (Match m in ReDropped.Matches(fence)) Array.Fill(dropped, true, m.Index, m.Length);
        var last = fence.Length - 1;
        while (last >= 0 && (dropped[last] || Js.IsSpace(fence[last]))) last--;
        return 1 + ReBreak.Matches(fence[..Math.Max(last, 0)]).Count;
    }

    // Only the remap moves the error: Node's output and the port's differ only in the error's line, the port's parser stopped
    // on Node's line of the parsed text, and the port's line passes FenceOracle. (A grammar fix can move an error to another
    // line of the parsed text; that is the fix's difference, not the remap's.)
    static bool LineOnly(string node, string port, string input)
    {
        if (DiagramLoader.ParseNode(node) is not JsonObject n || n["error"] is null || n["line"] is not JsonValue nodeLine
            || DiagramLoader.ParseNode(port) is not JsonObject p || p["line"] is not JsonValue portLine) return false;
        var line = (int)portLine.GetValue<double>();
        int? parsed = null;
        try { ExplainerCharts.Render(input); } catch (PeleException e) { parsed = e.ParsedLine; }
        return WithLine(n, line) == port && parsed == (int)nodeLine.GetValue<double>() && FenceOracle(input, line) is null;
    }

    // Every located error the port reports, for every seed and fuzz input, names a fence line that holds its text.
    [Fact] public void Every_error_line_holds_the_error()
    {
        var failures = new List<string>();
        var located = 0;
        foreach (var group in Groups.Where(group => group != "data"))
        {
            var inputs = SeedsOf(group).Cast<JsonObject>().Where(seed => seed["text"] is not null)
                .Select(seed => (seed["name"]!.GetValue<string>(), seed["text"]!.GetValue<string>())).ToList();
            if (File.Exists(Golden(group, "fuzz")))
            {
                var rows = (JsonArray)DiagramLoader.ParseNode(Fixtures.Lf(Golden(group, "fuzz")))!;
                inputs.AddRange(rows.Select((row, i) => ($"fuzz[{i}]", row!["input"]!.GetValue<string>())));
            }
            foreach (var (name, text) in inputs)
            {
                PeleException? error = null;
                try { ExplainerCharts.Render(text); } catch (PeleException e) { error = e; }
                if (error is not { Line: > 0 }) continue;   // errors without a line carry 0, as in Pele
                located++;
                if (FenceOracle(text, error.Line!.Value) is { } why) failures.Add($"{group}/{name}: {why}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(located > 500, $"{located} located errors");
    }

    // The map is built in one pass per step and read once per error: 9,000 comment lines before a pie error, and 20,000 CRLF
    // blank lines before a sankey one, stay well inside the time a chart takes.
    [Fact] public void Line_remap_is_linear()
    {
        var pie = "pie\n" + string.Concat(Enumerable.Repeat("%% c\n", 9000)) + "  \"a\": x";
        var sankey = "sankey-beta\r\n" + string.Concat(Enumerable.Repeat("\r\n", 20000)) + "a,b,1\r\nc d";
        Assert.True(pie.Length <= 50000 && sankey.Length <= 50000);
        ExplainerCharts.Compile("pie\n%% c\n  \"a\": x");   // warm up
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (pieResult, sankeyResult) = (ExplainerCharts.Compile(pie), ExplainerCharts.Compile(sankey));
        clock.Stop();
        Assert.Equal<(int?, int?)>((9002, 20003), (pieResult.Line, sankeyResult.Line));
        Assert.True(clock.ElapsedMilliseconds < 500, $"{clock.ElapsedMilliseconds} ms");
    }

    // Inputs the fixes must leave as Node draws them: each is compared with Node by Matches_node.
    static readonly string[] Controls =
    [
        "quadrantChart/quirk-comment-brace-exempt",   // `}%%` keeps its exemption (a lexical error at `}`)
        "xychart/comment-tab-before", "xychart/comment-nbsp-before", "xychart/comment-brace-before", "xychart/comment-percent-run",
        "xychart/render-horizontal", "xychart/orientation-vertical-upper", "xychart/orientation-quoted-title", "xychart/orientation-next-line",
        "xychart/orientation-crlf", "xychart/orientation-front-matter", "xychart/orientation-init", "xychart/orientation-spaces-comment",
        "xychart/orientation-glued", "xychart/orientation-after-semicolon", "xychart/md-backtick-inside", "xychart/md-backtick-unquoted",
        "xychart/orientation-next-line-after-space",   // `xychart-beta <space>\n  horizontal` stays horizontal, as in Node (M1)
        // X4 changes no statement boundary.
        "xychart/unquoted-break-ends-title", "xychart/unquoted-break-before-data", "xychart/unquoted-break-before-range",
        "xychart/unquoted-break-in-category",
        // S1 strips only blanks at a row's edges outside quotes; S2 only decodes entities that decode.
        "sankey/rows-spaces-unquoted", "sankey/rows-quoted-inner-blanks", "sankey/rows-quoted-blank-line-inside",
        "sankey/rows-mid-row-blanks-before-quote", "sankey/rows-mid-row-blanks-after-quote", "sankey/rows-unclosed-quote-trailing",
        // (`rows-blank-row-then-error` and `error-line-after-blank-lines` moved to LineRemapRows: their errors follow removed lines.)
        "sankey/rows-tab-mid-row", "sankey/rows-tab-at-text-end", "sankey/rows-comment-indented",
        "sankey/rows-crlf-spaces-unquoted", "sankey/stress-rows-blanks-50k", "sankey/stress-rows-quoted-50k", "sankey/render-quoted-names",
        "sankey/oracle-1", "sankey/spec-allows-proto-as-id-with-sankey-syntax", "sankey/directive-config", "sankey/corpus-docs-0", "sankey/crlf",
        "sankey/crlf-quoted", "sankey/entity-unknown-name", "sankey/entity-no-semicolon",
        "sankey/render-hostile-t10-p17", "sankey/names-html", "sankey/rows-blank-first-field",
        // S3 keeps controls, surrogate halves and U+FFFE out; S4 reads whole decimals (`1e999`, `-0`, `.5`, `" 8 "`) as
        // upstream does, and an empty value stays Mermaid's parse error.
        "sankey/unicode-c1-control", "sankey/unicode-c1-quoted", "sankey/unicode-del", "sankey/unicode-lone-surrogate", "sankey/unicode-fffe",
        "sankey/values-readable", "sankey/values-blank-at-end", "sankey/values-comma-at-end", "sankey/values-empty",
        // Fix round 2: the value check waits for a well-ended row; the 1.9.0 name check rejects what Node rejects.
        "sankey/values-runs-on-control", "sankey/values-runs-on-quote", "sankey/values-runs-on-space", "sankey/rows-blank-fields-tabs",
        "sankey/unicode-line-separator", "sankey/names-invalid-xml-fffe", "sankey/names-invalid-xml-ffff-html", "sankey/names-invalid-xml-c0",
        "sankey/names-invalid-xml-target",
        // The line remap moves only errors after removed lines: nothing removed, a comment after the error, a directive on
        // the error's line, trailing blank lines sankey trims, and a removed comment that an EOF past Pele's "\n" offsets.
        "lines/control-pie-nothing-removed", "lines/control-pie-comment-after-error", "lines/control-pie-directive-same-line",
        "lines/control-xychart-eof-one-comment", "lines/control-sankey-eof-trailing-blank-lines", "lines/control-sankey-nothing-removed",
        "lines/control-radar-nothing-removed",
        // R1 leaves whole words as upstream reads them (a name `circle`, `true` or `title` still fails), a bare `title` a title,
        // and a glued option value an error on its line; other names that hold the words already read as names.
        "radar/boundary-whole-circle", "radar/boundary-whole-true", "radar/boundary-whole-title", "radar/boundary-title-empty",
        "radar/boundary-title-comment", "radar/boundary-title-dash-end", "radar/boundary-titles-statement", "radar/boundary-graticule-value-glued",
        "radar/boundary-boolean-value-glued", "radar/boundary-title-tab", "radar/names-subtitle", "radar/names-keywords", "radar/names-acc-title",
        // R2 changes only a message naming a label that decodes.
        "radar/message-unknown-html-entity", "radar/message-no-entity", "radar/model-missing-entry-label",
    ];

    // The grammar-slip rows of each group, each pinned by Grammar_slips_are_the_stated_deviations.
    static Dictionary<string, (string Name, string Quirk, string Reference)[]> QuirkRows => new()
    {
        ["quadrantChart"] = QuadrantQuirkRows,
        ["xychart"] = XyChartQuirkRows,
        ["sankey"] = SankeyQuirkRows,
        ["radar"] = RadarQuirkRows,
    };

    [Fact] public void Grammar_slips_are_the_stated_deviations()
    {
        foreach (var (group, rows) in QuirkRows)
        {
            foreach (var (name, quirk, reference) in rows)
            {
                var node = Fixtures.Lf(Golden(group, name));
                var expected = Fixtures.Lf(Golden(group, reference));
                Assert.True(node != expected, $"{group}/{name}: Node already draws the corrected output");
                Assert.True(QuirkIn(group, Seed(group, name)["text"]!.GetValue<string>()).Contains(quirk), $"{group}/{name}: input shows no {quirk}");
                Assert.Equal(expected, Run(Seed(group, name)) + "\n");
            }
        }
        var compared = Targets().Select(row => $"{row[0]}/{row[1]}").ToHashSet();
        foreach (var control in Controls) Assert.True(compared.Contains(control), $"{control} is not compared with Node");
    }

    // Which quirks an input shows: Q1 a character other than `}`, `%` or whitespace directly before `%%`; Q2 a coordinate `0` followed by a character
    // other than a dot and then a digit; Q3 a keyword followed by a word character, after other text in its statement, or
    // leading a statement in point form.
    // JS `\s` as a class body.
    const string WsBody = @"\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";
    static readonly Regex ReQ1 = new($"[^}}%{WsBody}]%%");
    static readonly Regex ReQ2 = new(@"(?<=[\[,\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff])0[^.\n\r\u2028\u2029][0-9]");
    static readonly Regex ReKeyword = new("[Xx]-[Aa][Xx][Ii][Ss]|[Yy]-[Aa][Xx][Ii][Ss]|[Qq][Uu][Aa][Dd][Rr][Aa][Nn][Tt]-[1-4]|[Qq][Uu][Aa][Dd][Rr][Aa][Nn][Tt][Cc][Hh][Aa][Rr][Tt]|[Cc][Ll][Aa][Ss][Ss][Dd][Ee][Ff]|[Tt][Ii][Tt][Ll][Ee]|[Aa][Cc][Cc](?:[Tt][Ii][Tt][Ll][Ee]|[Dd][Ee][Ss][Cc][Rr])");
    const string Sp = $"[{WsBody}]*";
    const string Coord = @"(?:1|0(?:\.[0-9]+)?)";
    static readonly Regex RePointForm = new($@"\A[^\n\r;]*?:{Sp}\[{Sp}{Coord}{Sp},{Sp}{Coord}{Sp}\]");
    static bool KeywordQuirk(string text)
    {
        foreach (Match m in ReKeyword.Matches(text))
        {
            var after = m.Index + m.Length < text.Length ? text[m.Index + m.Length] : '\0';
            if (char.IsAsciiLetterOrDigit(after) || after == '_') return true;
            var from = Math.Max(text.LastIndexOfAny(['\n', '\r', ';'], Math.Max(m.Index - 1, 0)) + 1, 0);
            if (m.Index > 0 && text[from..m.Index].Trim(' ', '\t').Length > 0) return true;
            if (!m.Value.StartsWith("quadrantchart", StringComparison.OrdinalIgnoreCase) && RePointForm.IsMatch(text[m.Index..])) return true;
        }
        return false;
    }
    static string QuirkIn(string group, string text) => group switch
    {
        "quadrantChart" => string.Concat(ReQ1.IsMatch(text) ? "Q1" : "", ReQ2.IsMatch(text) ? "Q2" : "", KeywordQuirk(text) ? "Q3" : ""),
        "xychart" => XyChartQuirkIn(text),
        "sankey" => string.Concat(SankeyRowBlanks(text) ? "S1" : "",
            ReHtmlEntity.Matches(text).Any(m => Pele.Entities.DecodeEntities(m.Value) != m.Value) ? "S2" : "",
            SankeyUnicode(text) ? "S3" : "", SankeyUnreadableValue(text) ? "S4" : ""),
        "radar" => string.Concat(ReR1.IsMatch(text) ? "R1" : "", RadarLabelEntity(text) ? "R2" : ""),
        _ => "",
    };

    // radar. R1: one of the five words at the start of a word, followed by more of a name (ID reads `[-\w]*\w` on). R2: a
    // label (`[` and a quoted string) holding a Mermaid code or an HTML entity that decodes, which a "Missing entry" message
    // may name.
    static readonly Regex ReR1 = new("(?<![A-Za-z0-9_-])(?:circle|polygon|true|false|title)(?=[-A-Za-z0-9_]*[A-Za-z0-9_])");
    static readonly Regex ReRadarLabel = new(@"\[[\t ]*(?:""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)')");
    static bool RadarLabelEntity(string text) => ReRadarLabel.Matches(text).Any(m => m.Value[1..] is var label
        && (ReMermaidCode.IsMatch(label) || ReHtmlEntity.Matches(label).Any(e => Pele.Entities.DecodeEntities(e.Value) != e.Value)));

    // sankey. S1: outside front matter, comment and directive lines, with quoted fields (read as the lexer reads them) held
    // as `"Q"`, blanks (JS \s but a line break) that Node does not trim away and the port strips: a row of blanks only
    // between the first and the last row that hold text; or at the start of a row (not the first) or the end of a row (not
    // the last), a run not next to a comma that holds a blank other than a space (outside the CSV alphabet), is next to a
    // quote, or ends the header row. Spaces next to an unquoted field's text change nothing, as the parser trims fields.
    // S2: an HTML entity that DecodeEntities changes (a prefix or suffix from the configuration is not Mermaid-encoded).
    // S3: in the chart text, a character past the C1 controls, or a Mermaid `#…;` code (its placeholder is non-ASCII).
    // S4: a row whose third field, trimmed and unquoted, is not a whole decimal number.
    // Attribution of every listed fuzz case is also checked against Node by tools/chart-seeds/sankey-fuzz-check.mjs.
    static readonly Regex ReFrontMatter = new(@"\A[\t\n\v\f\r  ﻿]*---[\s\S]*?\n---[^\n]*(?:\n|\z)");
    // A `%%{…}%%` directive (it may span lines) and a comment line, which preprocess takes out too.
    static readonly Regex ReCommentLine = new(@"%%\{[\s\S]*?(?:\}%%|\z)|(?<=\A|\n)[\t\n\v\f\r    -     　﻿]*%%[^\n]*\n?");
    static readonly Regex ReCsvQuoted = new("\"(?:[^\"]|\"\")*(?:\"|\\z)");
    static readonly Regex ReHtmlEntity = new("&(?:#[0-9]+|#[xX][0-9a-fA-F]+|[A-Za-z0-9_]+);");
    static bool SankeyRowBlanks(string text)
    {
        var rows = ReCsvQuoted.Replace(ReCommentLine.Replace(ReFrontMatter.Replace(text, ""), ""), "\"Q\"").Split('\n', '\r');
        var first = Array.FindIndex(rows, row => Js.Trim(row) != "");
        var last = Array.FindLastIndex(rows, row => Js.Trim(row) != "");
        // A run next to a comma is a whole field, which the port keeps.
        bool Matters(string run, char inner, bool header) => run != "" && inner != ',' && (header || inner == '"' || run.Trim(' ') != "");
        for (var k = first + 1; k < last; k++) if (rows[k] != "" && Js.Trim(rows[k]) == "") return true;
        for (var k = Math.Max(first, 0); k <= last; k++)
        {
            var body = Js.Trim(rows[k]);
            if (body == "") continue;
            var lead = rows[k][..(rows[k].Length - Js.TrimStart(rows[k]).Length)];
            var trail = rows[k][Js.TrimEnd(rows[k]).Length..];
            if (k > first && Matters(lead, body[0], header: false)) return true;
            if (k < last && Matters(trail, body[^1], header: k == first)) return true;
        }
        return false;
    }
    static string SankeyBody(string text) => ReCommentLine.Replace(ReFrontMatter.Replace(text, ""), "");
    static readonly Regex ReMermaidCode = new("#[A-Za-z0-9_]+;");
    static readonly Regex ReWholeDecimal = new(@"\A[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.NonBacktracking);
    static bool SankeyUnicode(string text) => SankeyBody(text).Any(c => c > 0x9f) || ReMermaidCode.IsMatch(SankeyBody(text));
    static bool SankeyUnreadableValue(string text)
    {
        // Quoted fields hold `;` for their commas and line breaks, so a row splits into its own fields.
        var rows = ReCsvQuoted.Replace(SankeyBody(text), m => m.Value.Replace(",", ";").Replace("\n", ";").Replace("\r", ";")).Split('\n', '\r');
        foreach (var row in rows.Skip(1))
        {
            var fields = row.Split(',');
            if (fields.Length < 3) continue;
            var value = Js.Trim(fields[2]);
            // A quoted value counts only when its quote closes at the field's end; `"2"%%` is a parse error first.
            if (value.StartsWith('"'))
            {
                if (value.Length < 2 || value[^1] != '"' || value[1..^1].Replace("\"\"", "").Contains('"')) continue;
                value = value[1..^1];
            }
            if (!ReWholeDecimal.IsMatch(Js.Trim(value))) return true;
        }
        return false;
    }

    // xychart. Quoted text is a string token whatever it holds, and a comment is no token, so Q1 looks outside quotes and the
    // X1, X2, X4 and C1 tests outside quotes and comments too. An empty string `""` or an empty markdown string gives no token
    // at all and stays `""`; other quoted text becomes `"\u0001"`, one token. C1 is Task 6's first deviation (an exact y range
    // whose ends clamp to the same +-1e300 falls back to the data, Clamped_equal_y_range_is_the_stated_deviation).
    static readonly Regex ReQuoted = new("\"([^\"]*)(?:\"|\\z)");
    static readonly Regex ReComment = new("%%[^\n]*");
    static string XyChartQuirkIn(string text)
    {
        var unquoted = ReQuoted.Replace(text, m => m.Groups[1].Value is "" or "``" && m.Length == m.Groups[1].Length + 2 ? "\"\"" : "\"\u0001\"");
        var bare = ReComment.Replace(unquoted, "");
        return string.Concat(XyQ1(unquoted) ? "Q1" : "", OrientationQuirk(bare, upperCase: true) ? "X1" : "",
            OrientationQuirk(bare, upperCase: false) ? "X2" : "", MarkdownQuirk(text) ? "X3" : "", ReX4.IsMatch(bare) ? "X4" : "",
            ClampedEqualRange(bare) ? "C1" : "");
    }
    const string XyNumber = @"[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)";
    static readonly Regex ReYRange = new($@"[Yy]-[Aa][Xx][Ii][Ss][^\n]*?({XyNumber})[{WsBody}]*-->[{WsBody}]*({XyNumber})");
    static bool ClampedEqualRange(string text) => ReYRange.Matches(text).Any(m =>
        Js.ToNumber(m.Groups[1].Value) is var lo && Js.ToNumber(m.Groups[2].Value) is var hi && double.IsFinite(lo) && double.IsFinite(hi)
        && lo != hi && Math.Clamp(lo, -1e300, 1e300) == Math.Clamp(hi, -1e300, 1e300));

    // Q1: a character that starts a token directly before `%%`: a letter not after a letter, a digit not after a digit, or any
    // other character but `}`, `%` and whitespace. Not a character inside a number token or the `>` of `-->`, which axis_data
    // and data_inner read whole: in an x-axis or y-axis statement outside its `[...]`, or inside a series' `[...]`.
    static readonly Regex ReXyQ1 = new($"(?:(?<![A-Za-z])[A-Za-z]|(?<![0-9])[0-9]|[^A-Za-z0-9}}%{WsBody}])%%");
    static readonly Regex ReAxisStatement = new($@"\A[{WsBody}]*[XxYy]-[Aa][Xx][Ii][Ss](?![A-Za-z0-9_])");
    static readonly Regex ReSeriesStatement = new($@"\A[{WsBody}]*(?:[Ll][Ii][Nn][Ee]|[Bb][Aa][Rr])(?![A-Za-z0-9_])");
    static readonly Regex ReNumberEnd = new($@"{XyNumber}\z");
    static bool XyQ1(string text)
    {
        foreach (Match m in ReXyQ1.Matches(text))
        {
            var line = text[(m.Index == 0 ? 0 : text.LastIndexOfAny(['\n', ';'], m.Index - 1) + 1)..(m.Index + 1)];
            var open = line.LastIndexOf('[') > line.LastIndexOf(']');
            var whole = ReAxisStatement.IsMatch(line) ? !open : ReSeriesStatement.IsMatch(line) && open;
            if (whole && (ReNumberEnd.Match(line) is { Success: true, Length: > 1 } || line.EndsWith("-->", StringComparison.Ordinal))) continue;
            return true;
        }
        return false;
    }
    // X1 and X2: an orientation word where a token starts. Upstream lexes it as the orientation; X1 is a header orientation
    // `horizontal` not written in lower case, X2 one that is not a whole word after the chart keyword and its whitespace (a
    // line break after a space is part of that whitespace: the lexer skips it with the space, M1).
    static readonly Regex ReOrientation = new("(?<![A-Za-z])(?:[Vv][Ee][Rr][Tt][Ii][Cc][Aa][Ll]|[Hh][Oo][Rr][Ii][Zz][Oo][Nn][Tt][Aa][Ll])");
    // JS `\s` without the line breaks the lexer turns into NEWLINE (\n, \r); U+2028 and U+2029 are skipped as plain space.
    const string SpaceNotNewline = @"[\t\v\f \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]";
    // Whitespace the lexer skips in one run: it starts with a non-newline space and may take line breaks with it.
    const string XySkip = $@"{SpaceNotNewline}+(?:[\n\r][{WsBody}]*)?";
    static readonly Regex ReAfterHeader = new($@"[Xx][Yy][Cc][Hh][Aa][Rr][Tt](?:-[Bb][Ee][Tt][Aa])?{XySkip}\z");
    static bool OrientationQuirk(string text, bool upperCase)
    {
        foreach (Match m in ReOrientation.Matches(text))
        {
            var whole = !(m.Index + m.Length < text.Length && (char.IsAsciiLetterOrDigit(text[m.Index + m.Length]) || text[m.Index + m.Length] == '_'));
            var header = whole && ReAfterHeader.IsMatch(text[..m.Index]);
            if (upperCase ? header && m.Value.Length == 10 && m.Value != "horizontal" : !header) return true;
        }
        return false;
    }
    // X3: `"` then a backtick (a markdown string), or the jison source text Pele's BROKEN_RULE skipped.
    const string BrokenRule = "`)                                    { this.pushstate(md_string); }\n<md_string>(?:(?!`\").)+                  { return md_str; }\n<md_string>(?:`";
    static bool MarkdownQuirk(string text) => text.Contains("\"`", StringComparison.Ordinal) || text.Contains(BrokenRule, StringComparison.OrdinalIgnoreCase);
    // X4: unquoted text of two tokens or more with whitespace between them, which Node joins with ''.
    //   After `title`, `line` or `bar` and at least one space, any text token counts (digits are NUM there).
    //   After `x-axis`/`y-axis` and at least one space, the first token holds a letter and the second starts with one: axis_data
    //   reads digits and `-->` as NUMBER and ARROW, not text.
    //   After a category's `[` or `,`, the first token holds a letter.
    // Between the tokens: a whitespace run (XySkip), plus any `""` (an empty string or markdown string) or empty `accDescr{}`,
    // which give no token. A following keyword ends the text instead.
    const string XyWord = "[A-Za-z0-9&+=*.#_-]";
    const string XyLetter = "[A-Za-z&+=*#_]";
    const string XyNoToken = $@"(?:""""|[Aa][Cc][Cc][Dd][Ee][Ss][Cc][Rr]{SpaceNotNewline}*\{{{SpaceNotNewline}*\}})";
    const string XyGap = $"{XyNoToken}*{XySkip}(?:{XyNoToken}|{XySkip})*";
    const string XyNext = $"(?!{XyKeyword}(?![A-Za-z0-9_]))";
    static readonly Regex ReX4 = new(
        $"(?<![A-Za-z])(?:[Tt][Ii][Tt][Ll][Ee]|[Ll][Ii][Nn][Ee]|[Bb][Aa][Rr]){SpaceNotNewline}+{XyWord}+{XyGap}{XyNext}{XyWord}"
        + $"|(?<![A-Za-z])[XxYy]-[Aa][Xx][Ii][Ss]{SpaceNotNewline}+{XyWord}*{XyLetter}{XyWord}*{XyGap}{XyNext}[A-Za-z&*=#_]"
        + $"|[\\[,]{SpaceNotNewline}*{XyWord}*{XyLetter}{XyWord}*{XyGap}{XyNext}{XyWord}");
    // A keyword the lexer takes anywhere, which ends the text before it.
    const string XyKeyword = "(?:[Ll][Ii][Nn][Ee]|[Bb][Aa][Rr]|[Tt][Ii][Tt][Ll][Ee]|[XxYy]-[Aa][Xx][Ii][Ss]|[Xx][Yy][Cc][Hh][Aa][Rr][Tt])";

    // The SVG with its point-label group left out: round 1's empty-label fix changes only that group.
    static string OutsidePointLabels(string json)
    {
        var at = json.IndexOf("<g class=\\\"pele-point-labels\\\"", StringComparison.Ordinal);
        return at < 0 ? json : json[..at] + json[json.IndexOf("</g>", at, StringComparison.Ordinal)..];
    }

    // Every listed fuzz case differs from Node and shows its quirk (QuirkIn per group; for quadrantChart also R1, the round-1
    // empty-label fix); every other
    // case equals Node (Matches_node). What the port gives for each listed case is pinned in
    // tests/fixtures/charts/<group>.fuzz-deviations.json ([{index, output}]): port output, not a Node golden. A missing file
    // is written from the port and the fact fails once, so a re-mint is an explicit step (delete the file, run twice).
    [Fact] public void Fuzz_deviations_are_the_stated_quirks()
    {
        foreach (var (group, cases) in FuzzDeviations)
        {
            var rows = (JsonArray)DiagramLoader.ParseNode(Fixtures.Lf(Golden(group, "fuzz")))!;
            var differing = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                var input = rows[i]!["input"]!.GetValue<string>();
                var node = Js.Stringify(rows[i]!["result"], 2);
                var port = RunText(input);
                if (node == port) continue;
                // R1: quadrantChart round 1's empty-label deviation, which moves only point labels. L1: the line remap
                // (LineRemapRows), when the outputs differ only by it (LineOnly); the quirks are those the text shows. A case
                // with another quirk can be moved by the remap too and carry no L1 (the parser stops on another parsed line,
                // or Node has no error): quadrantChart 338, 429; sankey 83, 288, 432, 476, 518, 544, 607, 621, 968.
                var why = QuirkIn(group, input)
                    + (group == "quadrantChart" && node.Contains("\"svg\"") && OutsidePointLabels(node) == OutsidePointLabels(port) ? "R1" : "")
                    + (LineOnly(node, port, input) ? "L1" : "");
                differing.Add($"[{i}] = \"{why}\"");
            }
            var actual = string.Join(", ", differing);
            Assert.True(actual == string.Join(", ", cases.Select(c => $"[{c.Key}] = \"{c.Value}\"")), $"{group} fuzz cases that differ: {actual}");

            var pinned = new JsonArray([.. cases.Keys.Select(i => (JsonNode)new JsonObject { ["index"] = i, ["output"] = RunText(rows[i]!["input"]!.GetValue<string>()) })]);
            var path = Fixtures.Fixture($"charts/{group}.fuzz-deviations.json");
            var written = Js.Stringify(pinned, 2) + "\n";
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, written);
                Assert.Fail($"{path} written from the port; check it and run again");
            }
            Assert.Equal(Fixtures.Lf(path), written);
        }
    }

    // A grammar fix that reads ahead stays linear on a source at the 50,000-character limit:
    //   quadrantChart's point-form test runs once per statement, not once per leading space (review N2: 24,000 spaces before
    //   `x-axis` and a 25,000-character point name took 1.7 s when it rescanned from every space);
    //   xychart's markdown string is read in one pass (X3), and unquoted text is one slice of the source, not a concatenation
    //   per token (X4).
    [Theory]
    [InlineData("quadrantChart", "stress-point-form-rescan")]
    [InlineData("xychart", "stress-md-string-50k")]
    [InlineData("xychart", "stress-unquoted-words-50k")]
    // sankey's row scan (S1) holds each run of blanks once: blank rows, long runs dropped and kept, and quoted text.
    [InlineData("sankey", "quirk-rows-stress-50k")]
    [InlineData("sankey", "stress-rows-blanks-50k")]
    [InlineData("sankey", "stress-rows-quoted-50k")]
    [InlineData("sankey", "stress-value-digits-50k")]   // S4's decimal test: 49,970 digits then `x`
    // radar's R1: TITLE's lookahead over a 49,970-dash run, and 6,125 names that start with `circle`.
    [InlineData("radar", "stress-title-dashes-50k")]
    [InlineData("radar", "quirk-boundary-stress-50k")]
    // Not a fix: upstream's own guard (parser.ts:73-86), which fails an `accDescr {` with no `}` after it at once.
    [InlineData("radar", "stress-acc-descr-opened-50k")]
    public void Grammar_fix_is_linear(string group, string name)
    {
        var text = Seed(group, name)["text"]!.GetValue<string>();
        Assert.True(text.Length is > 49000 and <= 50000);
        ExplainerCharts.Compile("quadrantChart\n  A: [0.5, 0.5]");   // warm up
        ExplainerCharts.Compile("xychart\n  title a b\n  line [1, 2]");
        ExplainerCharts.Compile("sankey\n  a,b,1\n  \n  b,c,2");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ExplainerCharts.Compile(text);
        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 500, $"{clock.ElapsedMilliseconds} ms");
    }

    // charts.mjs:30: a rotated axis title with a Han character is set upright; others are left alone. Node pins it through
    // wrapper/archify-xychart-cjk-axis once xychart lands (Task 6).
    [Fact] public void Rotated_han_title_is_written_vertically()
    {
        const string han = "<g transform=\"translate(12.5,-3) rotate(-90)\"><text x=\"0\" y=\"4.55\" text-anchor=\"middle\">延迟（毫秒）</text></g>";
        const string latin = "<g transform=\"translate(12.5,-3) rotate(-90)\"><text x=\"0\" y=\"4.55\" text-anchor=\"middle\">Latency</text></g>";
        Assert.Equal("<text x=\"12.5\" y=\"-3\" text-anchor=\"middle\" writing-mode=\"vertical-rl\">延迟（毫秒）</text>" + latin,
            ExplainerCharts.UprightHanTitles(han + latin));
        Assert.Equal("<text x=\"1\" y=\"2\" text-anchor=\"middle\" writing-mode=\"vertical-rl\">a\U00020000</text>",
            ExplainerCharts.UprightHanTitles("<g transform=\"translate(1,2) rotate(-90)\"><text x=\"0\" y=\"0\" text-anchor=\"middle\">a\U00020000</text></g>"));
        const string arabicDigits = "<g transform=\"translate(١,2) rotate(-90)\"><text x=\"0\" y=\"0\" text-anchor=\"middle\">汉</text></g>";
        Assert.Equal(arabicDigits, ExplainerCharts.UprightHanTitles(arabicDigits));   // JS \d is ASCII
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
    static string? FuzzFailure(string golden, Func<string, string> runText, IReadOnlyDictionary<int, string>? skip = null)
    {
        var rows = (JsonArray)DiagramLoader.ParseNode(golden)!;
        for (var i = 0; i < rows.Count; i++)
        {
            if (skip?.ContainsKey(i) == true) continue;
            var input = rows[i]!["input"]!.GetValue<string>();
            var expected = Js.Stringify(rows[i]!["result"], 2);
            var actual = runText(input);
            if (expected != actual) return $"fuzz[{i}] input {Js.Quote(input)}\nexpected: {expected}\nactual:   {actual}";
        }
        return null;
    }
}
