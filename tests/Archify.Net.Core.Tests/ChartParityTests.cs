using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
namespace Archify.Net.Tests;

// PORT-008: ExplainerCharts against goldens Node mints through upstream charts.mjs (tools/capture-fixtures.mjs, charts
// section). Seeds and layout: tools/chart-seeds/README.md.
public class ChartParityTests
{
    // A group is compared once it is listed; each type task adds its own.
    static readonly string[] Groups = ["data", "wrapper", "pie", "xychart", "quadrantChart", "sankey"];
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
    ];
    // Fuzz cases that differ from Node by a stated deviation: index -> the quirk that explains it, checked by
    // Fuzz_deviations_are_the_stated_quirks.
    static readonly Dictionary<string, Dictionary<int, string>> FuzzDeviations = new()
    {
        ["quadrantChart"] = new() {
            [14] = "Q1", [78] = "Q3", [134] = "Q1", [193] = "Q2", [221] = "Q3", [279] = "Q3", [305] = "Q3", [317] = "Q3",
            [320] = "Q3", [332] = "Q3", [338] = "Q2", [429] = "Q3", [431] = "Q2", [487] = "Q3", [491] = "Q1", [499] = "Q2",
            [527] = "Q1", [557] = "Q3", [620] = "Q2", [623] = "Q3", [647] = "R1", [686] = "Q3", [748] = "Q3", [783] = "Q1",
            [810] = "Q3", [815] = "Q3", [839] = "Q3", [877] = "Q3", [901] = "Q3", [906] = "Q3", [928] = "Q3", [995] = "Q3",
        },
        ["xychart"] = new() {
            [15] = "Q1", [16] = "X2X4", [185] = "X2X4", [200] = "Q1", [208] = "X4", [238] = "Q1", [256] = "X3", [338] = "X4",
            [367] = "X3", [376] = "X2", [401] = "X2X4", [477] = "X3", [516] = "Q1", [524] = "X4", [557] = "X4", [586] = "X3X4",
            [592] = "X2X4", [634] = "Q1", [765] = "Q1", [782] = "Q1", [848] = "X2", [956] = "X4", [986] = "X4",
        },
        ["sankey"] = new() {
            [23] = "S2", [77] = "S1", [81] = "S1", [96] = "S1", [178] = "S1", [196] = "S1", [256] = "S2", [311] = "S1", [341] = "S1",
            [353] = "S1", [355] = "S1", [452] = "S1", [461] = "S1", [487] = "S1", [529] = "S1", [553] = "S2", [593] = "S1", [719] = "S2",
            [811] = "S1", [822] = "S1", [835] = "S1", [838] = "S1", [854] = "S1", [868] = "S1", [915] = "S1", [933] = "S1", [986] = "S1",
            [992] = "S2",
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
    //      decodes it; the port decodes them in the label too.
    // A reference row writes the same chart without those blanks (S1) or with the decoded character (S2).
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
    ];

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
        "sankey/rows-tab-mid-row", "sankey/rows-tab-at-text-end", "sankey/rows-comment-indented", "sankey/rows-blank-row-then-error",
        "sankey/rows-crlf-spaces-unquoted", "sankey/stress-rows-blanks-50k", "sankey/stress-rows-quoted-50k", "sankey/render-quoted-names",
        "sankey/oracle-1", "sankey/spec-allows-proto-as-id-with-sankey-syntax", "sankey/directive-config", "sankey/corpus-docs-0", "sankey/crlf",
        "sankey/crlf-quoted", "sankey/error-line-after-blank-lines", "sankey/entity-unknown-name", "sankey/entity-no-semicolon",
        "sankey/render-hostile-t10-p17", "sankey/names-html", "sankey/rows-blank-last-field", "sankey/rows-blank-first-field",
        "sankey/rows-blank-fields-tabs",
    ];

    // The grammar-slip rows of each group, each pinned by Grammar_slips_are_the_stated_deviations.
    static Dictionary<string, (string Name, string Quirk, string Reference)[]> QuirkRows => new()
    {
        ["quadrantChart"] = QuadrantQuirkRows,
        ["xychart"] = XyChartQuirkRows,
        ["sankey"] = SankeyQuirkRows,
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
        "sankey" => string.Concat(SankeyRowBlanks(text) ? "S1" : "", ReHtmlEntity.IsMatch(text) ? "S2" : ""),
        _ => "",
    };

    // sankey. S1: outside front matter, comment and directive lines, with quoted fields (read as the lexer reads them) held
    // as `"Q"`, blanks (JS \s but a line break) that Node does not trim away and the port strips: a row of blanks only
    // between the first and the last row that hold text; or at the start of a row (not the first) or the end of a row (not
    // the last), a run not next to a comma that holds a blank other than a space (outside the CSV alphabet), is next to a
    // quote, or ends the header row. Spaces next to an unquoted field's text change nothing, as the parser trims fields.
    // S2: an HTML entity
    // (in a name only a named one reaches the label, since Mermaid's `#…;` encoding hides a numeric one in a placeholder that
    // is never CSV text; a prefix or suffix from the configuration is not encoded).
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
                // R1: quadrantChart round 1's empty-label deviation, which moves only point labels.
                var why = QuirkIn(group, input)
                    + (group == "quadrantChart" && node.Contains("\"svg\"") && OutsidePointLabels(node) == OutsidePointLabels(port) ? "R1" : "");
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
