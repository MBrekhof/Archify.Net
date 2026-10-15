using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Attrs = System.Collections.Generic.Dictionary<string, string>;

namespace Archify.Net;

// scripts/check-render-output.mjs. The argv/fs head becomes the `html` parameter; the final console.log becomes the
// returned report (without `file`). Scanning stays regex-based, as upstream: report parity depends on the same matching.
// Every JS regex goes through Rx(): ECMAScript mode (ASCII \w \d \b), JS `\s` spelled out, JS `$` (no m flag) as \z.
public sealed class ArtifactChecker
{
    // check-render-output.mjs:29-33: hash the file bytes, scan bytes.toString('utf8'). Encoding.UTF8.GetString keeps a BOM
    // and replaces each maximal invalid subpart with U+FFFD, as Node does.
    public static JsonObject Check(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new ArtifactChecker().Run(bytes, Encoding.UTF8.GetString(bytes));
    }

    public static JsonObject Check(string html) => Check(new UTF8Encoding(false).GetBytes(html));

    // ---- regex plumbing -----------------------------------------------------------------------------------------------
    // JS \s: WhiteSpace + LineTerminator (TAB VT FF SP NBSP ZWNBSP, Zs, LF CR LS PS). .NET ECMAScript \s is ASCII only.
    // Built from code points so the source stays ASCII; declared before every Rx() field (static initializers run in order).
    static readonly string JsSpace = @"\t\n\v\f\r " + new string([(char)0xA0, (char)0x1680, (char)0x2000, '-', (char)0x200A,
        (char)0x2028, (char)0x2029, (char)0x202F, (char)0x205F, (char)0x3000, (char)0xFEFF]);

    // `ignoreCase` is JS /i without the u flag: it folds by toUpperCase and never maps a non-ASCII character onto ASCII.
    // .NET IgnoreCase folds U+212A KELVIN SIGN onto k (and U+0130/U+0131 onto i), so it is never used: every ASCII letter
    // is spelled in both cases instead, [kK] outside a class and each letter/range's other case appended inside one.
    static Regex Rx(string js, bool ignoreCase = false)
    {
        var sb = new StringBuilder(js.Length * 2);
        for (var i = 0; i < js.Length; i++)
        {
            var c = js[i];
            if (c == '\\')
            {
                var n = js[i + 1];
                sb.Append(n switch { 's' => "[" + JsSpace + "]", 'S' => "[^" + JsSpace + "]", _ => new string([c, n]) });
                i++;
                continue;
            }
            if (c == '[')
            {
                var end = i + 1;
                while (js[end] != ']') end += js[end] == '\\' ? 2 : 1;
                sb.Append(TranslateClass(js[(i + 1)..end], ignoreCase));
                i = end;
                continue;
            }
            if (c == '$') sb.Append(@"\z");
            else if (ignoreCase && char.IsAsciiLetter(c)) sb.Append('[').Append(char.ToUpperInvariant(c)).Append(char.ToLowerInvariant(c)).Append(']');
            else sb.Append(c);
        }
        return new Regex(sb.ToString(), RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    }

    // One JS character class body (between the brackets). A lone '-' is escaped so the folded ranges appended at the end
    // can never join a preceding literal into a range.
    static string TranslateClass(string body, bool ignoreCase)
    {
        var sb = new StringBuilder("[");
        var folded = new StringBuilder();
        var i = 0;
        if (body.StartsWith('^')) { sb.Append('^'); i = 1; }
        while (i < body.Length)
        {
            if (body[i] == '\\')
            {
                sb.Append(body[i + 1] == 's' ? JsSpace : body.Substring(i, 2));
                i += 2;
                continue;
            }
            var lo = body[i];
            var hi = lo;
            if (i + 2 < body.Length && body[i + 1] == '-' && body[i + 2] != '\\') { hi = body[i + 2]; sb.Append(lo).Append('-').Append(hi); i += 3; }
            else { sb.Append(lo == '-' ? @"\-" : new string(lo, 1)); i++; }
            if (!ignoreCase) continue;
            Fold('a', 'z', 'A' - 'a');
            Fold('A', 'Z', 'a' - 'A');

            void Fold(char from, char to, int shift)
            {
                var a = Math.Max(lo, from);
                var b = Math.Min(hi, to);
                if (a <= b) folded.Append((char)(a + shift)).Append('-').Append((char)(b + shift));
            }
        }
        return sb.Append(folded).Append(']').ToString();
    }

    static readonly Regex NonFiniteToken = Rx(@"\b(?:NaN|undefined|Infinity)\b");
    // Consume comments/CDATA as whole tokens, including any tag-like prose.
    static readonly Regex SvgTagToken = Rx(@"<!--[\s\S]*?(?:-->|$)|<!\[CDATA\[[\s\S]*?(?:\]\]>|$)|<(/?)([A-Za-z][\w:-]*)(?=[\s/>])(?:[^>""']|""[^""]*""|'[^']*')*>");
    static readonly Regex AutomaticCrossoverUnderlayTag = Rx(@"<path\b[^>]*\bdata-graph-role=""automatic-crossover-underlay""[^>]*/>", true);
    static readonly Regex HtmlAttribute = Rx(@"([A-Za-z_:][\w:.-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+))");
    static readonly Regex SvgBlock = Rx(@"<svg\b[\s\S]*?</svg>", true);
    static readonly Regex SvgRootTag = Rx(@"<svg\b[^>]*>", true);
    static readonly Regex SelfClosing = Rx(@"/\s*>$");
    static readonly Regex Blank = Rx(@"^\s*$");
    static readonly Regex ArrowClass = Rx(@"\bclass=""[^""]*\ba-(?:default|emphasis|security|dashed)\b");
    static readonly Regex MarkerEnd = Rx(@"\bmarker-end=");
    static readonly Regex StraightD = Rx(@"^\s*M\s+[-+\d.eE]+\s+[-+\d.eE]+\s+L\s+[-+\d.eE]+\s+[-+\d.eE]+\s*$");
    static readonly Regex VariantClass = Rx(@"\ba-(default|emphasis|security|dashed)\b");
    static readonly Regex EdgeLabelGroup = Rx(@"<g\b[^>]*\bdata-edge-(?:key|id|from)=""[^""]*""[^>]*>[\s\S]*?</g>", true);
    static readonly Regex GOpenTag = Rx(@"<g\b[^>]*>", true);
    static readonly Regex RectTag = Rx(@"<rect\b[^>]*>", true);
    static readonly Regex MaskClass = Rx(@"\bclass=""[^""]*\bc-mask\b");
    static readonly Regex Whitespace = Rx(@"\s+");
    static readonly Regex ViewBoxSeparator = Rx(@"[\s,]+");
    static readonly Regex GTransform = Rx(@"<g\b[^>]*\btransform\s*=[^>]*>", true);
    static readonly Regex SemanticSigilAttr = Rx(@"\bdata-semantic-sigil=");
    static readonly Regex ShapeTransform = Rx(@"<(?:path|line|rect|text)\b[^>]*\btransform\s*=", true);
    static readonly Regex NodeIdGroup = Rx(@"<g\b[^>]*\bdata-node-id=", true);
    static readonly Regex FrameLabelMask = Rx(@"<g\b[^>]*\bdata-graph-role=""structural-frame-label""[^>]*>[\s\S]*?<rect\b[^>]*\bdata-graph-role=""structural-frame-label-mask""[^>]*>", true);
    static readonly Regex TextElement = Rx(@"<text\b([^>]*)>([\s\S]*?)</text>", true);
    static readonly Regex TspanOpen = Rx(@"<tspan\b", true);
    static readonly Regex TspanElement = Rx(@"<tspan\b([^>]*)>([\s\S]*?)</tspan>", true);
    static readonly Regex Digits = Rx(@"^\d+$");
    static readonly Regex MetaTag = Rx(@"<meta\b[^>]*>", true);
    static readonly Regex ReadabilityToken = Rx(@"<!--[\s\S]*?(?:-->|$)|<!\[CDATA\[[\s\S]*?(?:\]\]>|$)|<text\b([^>]*)>([\s\S]*?)</text>|<g\b[^>]*>|</g\s*>", true);
    static readonly Regex GClose = Rx(@"^</g", true);
    static readonly Regex NumericReference = Rx(@"&#(?:x([0-9a-f]+)|([0-9]+));?", true);
    static readonly Regex AnyTag = Rx(@"<[^>]*>");
    static readonly Regex DataNodeLabel = Rx(@"(?:^|\s)data-node-label(?:\s*=|\s|$)", true);
    static readonly Regex DataBoundaryLabel = Rx(@"(?:^|\s)data-boundary-label(?:\s*=|\s|$)", true);

    static readonly HashSet<string> HtmlVoidElements = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"];
    static readonly HashSet<string> SvgHtmlIntegrationPoints = ["foreignobject", "desc", "title"];
    static readonly HashSet<string> NumericAttrs =
    [
        "x", "y", "x1", "y1", "x2", "y2", "dx", "dy", "cx", "cy", "r", "rx", "ry", "fx", "fy",
        "fr", "width", "height", "d", "points", "pathlength", "transform", "viewbox", "offset",
        "opacity", "fill-opacity", "flood-opacity", "stop-opacity", "stroke-opacity", "font-size",
        "font-size-adjust", "font-weight", "letter-spacing", "word-spacing", "kerning", "stroke-width",
        "stroke-dasharray", "stroke-dashoffset", "stroke-miterlimit", "textlength", "startoffset",
        "rotate", "markerwidth", "markerheight", "refx", "refy", "orient", "patterntransform",
        "gradienttransform", "filterres", "stddeviation", "basefrequency", "numoctaves", "seed",
        "surfacescale", "diffuseconstant", "specularconstant", "specularexponent",
        "limitingconeangle", "azimuth", "elevation", "pointsatx", "pointsaty", "pointsatz",
        "kernelmatrix", "order", "divisor", "bias", "targetx", "targety", "kernelunitlength",
        "scale", "radius", "k1", "k2", "k3", "k4", "tablevalues", "slope", "intercept",
        "amplitude", "exponent",
    ];
    static readonly Dictionary<string, HashSet<string>> ElementNumericAttrs = new()
    {
        ["fecolormatrix"] = ["values"],
        ["fepointlight"] = ["z"],
        ["fespotlight"] = ["z"],
    };

    // ---- records --------------------------------------------------------------------------------------------------------
    // One `{ kind, index, raw, … }` arrow record. Identity fields live in `Rel`, the object geometry.mjs reads as
    // `relation` (absent key = `undefined`), so the label pass's `owner.key = …` mutation is seen by every later reader.
    sealed class Arrow
    {
        public required string Kind { get; init; }
        public int Index { get; init; }
        public required string Raw { get; init; }
        public bool AuthoredStraight { get; init; }
        public bool CrossoverHalo { get; init; }
        public bool IndependentPorts { get; init; }
        public bool AutomaticWorkflowRoute { get; init; }
        public required List<Segment> Segments { get; init; }
        public required List<Segment> BorderSegments { get; init; }
        public required List<Pt> RoutePoints { get; init; }
        public required List<List<Pt>> RouteSubpaths { get; init; }
        public int Offset { get; init; }
        public JsonObject Rel { get; private set; } = new();
        public string? From { get => Rel.Str("from"); set => Put("from", value); }
        public string? To { get => Rel.Str("to"); set => Put("to", value); }
        public string? Id { get => Rel.Str("id"); set => Put("id", value); }
        public string? Key { get => Rel.Str("key"); set => Put("key", value); }
        public string? Label { get => Rel.Str("label"); set => Put("label", value); }
        public void Put(string key, string? value) { if (value is null) Rel.Remove(key); else Rel[key] = value; }
        // `{ ...arrow }`: later mutations of the original do not reach the copy.
        public Arrow Snapshot() { var copy = (Arrow)MemberwiseClone(); copy.Rel = Rel.DeepClone().AsObject(); return copy; }
    }

    sealed record Entry(Arrow Relation, int RelationIndex, List<Pt> Points);
    sealed record Part(Arrow Relation, int RelationIndex, List<Pt> Points, int SegmentOffset, bool SourceEndpoint, bool TargetEndpoint);
    sealed record Crossing(Arrow Left, Arrow Right, Pt Point);
    sealed class NodeRect(string id, string label, double[] box)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public double[] Box { get; } = box;
        public JsonObject ToJson() => new() { ["id"] = Id, ["label"] = Label, ["box"] = new JsonArray([.. Box.Select(v => (JsonNode?)v)]) };
    }
    sealed record TextBoxRect(double X1, double Y1, double X2, double Y2, string Label);
    sealed record Owner(string Kind, string? Id, string? From = null, string? To = null)
    {
        public JsonObject ToJson()
        {
            var o = new JsonObject { ["kind"] = Kind, ["id"] = Id };
            if (Kind == "edge") { o["from"] = From; o["to"] = To; }
            return o;
        }
    }
    sealed class ReadabilityEntry
    {
        public string? NodeId { get; init; }
        public required Owner Owner { get; init; }
        public required string Text { get; init; }
        public required string Detail { get; init; }
        public double SourceFontPx { get; init; }
        public double ViewBoxWidth { get; set; }
        public double Scale { get; set; }
        public double ProjectedFontPx { get; set; }
    }

    // ---- state ----------------------------------------------------------------------------------------------------------
    readonly JsonArray checks = [];
    readonly List<JsonObject> pathDiagnostics = [];
    readonly Dictionary<int, SvgPathAnalysisResult> pathAnalyses = [];   // lookup only
    readonly Dictionary<JsonObject, Arrow> arrowOf = new(ReferenceEqualityComparer.Instance);   // lookup only

    Arrow Of(JsonObject? relation) => arrowOf[relation!];

    void AddCheck(string name, bool ok, IEnumerable<string> details) =>
        checks.Add(new JsonObject { ["name"] = name, ["ok"] = ok, ["details"] = new JsonArray([.. details.Select(d => (JsonNode?)d)]) });

    static JsonObject SuggestedLimits() => new() { ["bendsPerRelationship"] = 2, ["stretch"] = 1.35, ["segmentPx"] = 16, ["microSegmentPx"] = 8 };

    static JsonObject InitialComposition() => new()
    {
        ["schemaVersion"] = 1,
        ["profile"] = "standard",
        ["status"] = "pass",
        ["summary"] = new JsonObject { ["errors"] = 0, ["warnings"] = 0 },
        ["metrics"] = new JsonObject
        {
            ["properCrossings"] = 0,
            ["resolvedCrossovers"] = 0,
            ["ambiguousCorridors"] = 0,
            ["containerBorderRuns"] = 0,
            ["labelRouteClearanceIssues"] = 0,
            ["minLabelRouteClearance"] = null,
            ["labelCanvasOverflowIssues"] = 0,
            ["maxBends"] = 0,
            ["routesOverSuggestedBends"] = 0,
            ["maxStretch"] = null,
            ["routesOverSuggestedStretch"] = 0,
            ["minSegmentPx"] = null,
            ["minInteriorSegmentPx"] = null,
            ["shortSegmentCount"] = 0,
            ["shortEndpointSegmentCount"] = 0,
            ["shortInteriorSegmentCount"] = 0,
            ["microSegmentCount"] = 0,
            ["desktopReadabilityIssues"] = 0,
            ["minProjectedNodeTextPx"] = null,
        },
        ["suggestedLimits"] = SuggestedLimits(),
        ["issues"] = new JsonArray(),
    };

    JsonObject Run(byte[] bytes, string html)
    {
        var artifact = new JsonObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length };
        var readerContract = ReaderContractFromHtml(html);
        var composition = InitialComposition();

        var svgMatches = SvgBlock.Matches(html);
        AddCheck("single_svg", svgMatches.Count == 1, [$"found {Js.Num(svgMatches.Count)} <svg> block(s)"]);
        if (svgMatches.Count == 1) composition = CheckSvg(svgMatches[0].Value, readerContract);

        if (pathDiagnostics.Count > 0) AddCheck("svg_path_data", false, pathDiagnostics.Select(item => item.Str("message")!));

        var ok = checks.All(check => check!["ok"]!.GetValue<bool>()) && composition.Str("status") != "fail";
        var report = new JsonObject { ["ok"] = ok, ["artifact"] = artifact, ["checks"] = checks, ["composition"] = composition };
        if (pathDiagnostics.Count > 0) report["diagnostics"] = new JsonArray([.. pathDiagnostics]);
        return report;
    }

    JsonObject CheckSvg(string svg, string? readerContract)
    {
        var svgRootMatch = SvgRootTag.Match(svg);
        var svgAttrs = ParseAttrs(svgRootMatch.Success ? svgRootMatch.Value : "");
        var qualityProfile = Or(A(svgAttrs, "data-quality-profile"), "standard")!;
        var workflowV2 = A(svgAttrs, "data-layout-contract") == "readable-v2";
        var qualityGatesEnforced = A(svgAttrs, "data-quality-gates") != "advisory";
        var nonFiniteAttrs = CollectNonFiniteAttrs(svg);
        AddCheck("finite_svg", nonFiniteAttrs.Count == 0, nonFiniteAttrs);
        var legendStart = svg.IndexOf("<!-- Legend -->", StringComparison.Ordinal);
        var beforeLegend = legendStart >= 0 ? svg[..legendStart] : svg;
        var (readabilityEvidence, desktopReadabilityIssue) = CollectDesktopReadability(svgAttrs, beforeLegend, readerContract);
        // The browser gate measures this later; the geometry is already certain here.
        var viewBoxSize = ViewBoxSize(svgAttrs);
        var viewportHeightIssue = DesktopReadability.PredictedFixedWidthOverflow(viewBoxSize[0], viewBoxSize[1],
            Or(A(svgAttrs, "data-reader-fit"), null), Or(A(svgAttrs, "data-diagram-type"), null));
        var arrows = CollectArrows(beforeLegend, workflowV2);
        var diagonal = arrows.SelectMany(arrow => DiagonalStraightSegments(arrow).Select(segmentIndex => (Arrow: arrow, SegmentIndex: segmentIndex))).ToList();
        AddCheck("orthogonal_arrows", diagonal.Count == 0, diagonal.Select(d =>
            $"{d.Arrow.Kind} {Js.Num(d.Arrow.Index)} segment {Js.Num(d.SegmentIndex + 1)}: expected an orthogonal segment or an explicitly authored direct straight route; {d.Arrow.Raw}"));
        var measuredRelationshipCrossings = CollectRelationshipCrossings(arrows, workflowV2);
        var resolvedCrossovers = measuredRelationshipCrossings.Where(hit => hit.Left.CrossoverHalo && hit.Right.CrossoverHalo).ToList();
        var relationshipCrossings = measuredRelationshipCrossings.Where(hit => !hit.Left.CrossoverHalo || !hit.Right.CrossoverHalo).ToList();
        var compositionFrames = CollectCompositionFrames(beforeLegend);
        var containerBorderRuns = Geometry.CollectBorderRuns(
            arrows.Where(arrow => T(arrow.From) && T(arrow.To) && arrow.BorderSegments.Count > 0)
                .Select(arrow => new RoutedRelation(null, arrow.Rel, arrow.Index, Segments: arrow.BorderSegments)).ToList(),
            compositionFrames);
        var routedRelationships = arrows
            .Where(arrow => T(arrow.From) && T(arrow.To) && arrow.RouteSubpaths.Any(points => points.Count > 1))
            .Select(arrow => new Entry(arrow, arrow.Index, arrow.RoutePoints)).ToList();
        var nodeRects = T(A(svgAttrs, "transform")) ? [] : CollectUntransformedNodeRects(beforeLegend);
        var routeMetrics = SubpathBudgetMetrics(routedRelationships);
        // Architecture marks only one of its two Reader fits with its type.
        var crowdedSides = A(svgAttrs, "data-diagram-type") == "architecture" || A(svgAttrs, "data-reader-primary-text") == "14"
            ? CrowdedNodeSides(arrows, nodeRects) : [];
        var routeRhythmIssues = SubpathRhythmIssues(routedRelationships);
        var ambiguousCorridors = SubpathCorridors(routedRelationships,
            (left, right) => workflowV2 || (left.IndependentPorts && right.IndependentPorts),
            workflowV2,
            (left, right) => left.AutomaticWorkflowRoute && right.AutomaticWorkflowRoute);
        var arrowheadCollisions = Geometry.CollectArrowheadCollisions(
            routedRelationships.Where(entry => workflowV2 || entry.Relation.IndependentPorts)
                .SelectMany(entry => RouteParts(entry).Where(part => part.TargetEndpoint)).Select(ToRouted).ToList(),
            workflowV2);
        var relationshipLabels = CollectRelationshipLabelMasks(beforeLegend, arrows);
        var leadingSpace = CollectArchitectureLeadingSpace(svgAttrs, beforeLegend, nodeRects, compositionFrames, arrows, relationshipLabels);
        var sequenceColumnSpace = CollectSequenceColumnSpace(svgAttrs, beforeLegend, nodeRects, arrows);
        var sequenceNoteBorders = CollectSequenceNoteBorders(svgAttrs, beforeLegend, compositionFrames, arrows);
        double labelClearanceThreshold = qualityProfile == "showcase" ? 4 : 2;
        var allRoutes = arrows.Select(arrow => new Entry(arrow, arrow.Index, arrow.RoutePoints)).ToList();
        var labelRouteMeasurements = SubpathLabelClearance(allRoutes, relationshipLabels, double.MaxValue);
        var labelRouteClearance = SubpathLabelClearance(allRoutes, relationshipLabels, labelClearanceThreshold);
        // The renderers bound their own label rects, but `check` also re-measures an artifact it did not produce; see
        // collectLabelCanvasOverflow in shared/geometry.mjs.
        var labelCanvasOverflow = Geometry.CollectLabelCanvasOverflow(relationshipLabels, ViewBoxRect(svgAttrs));
        var showcase = qualityProfile == "showcase";
        var crossingIsError = showcase;
        var corridorIsError = showcase;
        var rhythmIsError = showcase;
        var labelClearanceIsError = showcase;
        var labelContainmentIsError = showcase;
        var desktopReadabilityIsError = showcase;
        // Certain geometry, but new: surface it as evidence first (CONTRIBUTING.md#product-and-compatibility-contracts).
        var viewportHeightIsError = false;
        var compositionErrors = (qualityGatesEnforced ? containerBorderRuns.Count : 0)
            + (crossingIsError ? relationshipCrossings.Count : 0)
            + (corridorIsError ? ambiguousCorridors.Count + arrowheadCollisions.Count : 0)
            + (labelClearanceIsError ? labelRouteClearance.Count : 0)
            + (labelContainmentIsError ? labelCanvasOverflow.Count : 0)
            + (rhythmIsError ? routeRhythmIssues.Count : 0)
            + (desktopReadabilityIsError && desktopReadabilityIssue is not null ? 1 : 0)
            + (viewportHeightIsError && viewportHeightIssue is not null ? 1 : 0);
        var compositionWarnings = (qualityGatesEnforced ? 0 : containerBorderRuns.Count)
            + (crossingIsError ? 0 : relationshipCrossings.Count)
            + (corridorIsError ? 0 : ambiguousCorridors.Count + arrowheadCollisions.Count)
            + (labelClearanceIsError ? 0 : labelRouteClearance.Count)
            + (labelContainmentIsError ? 0 : labelCanvasOverflow.Count)
            + (rhythmIsError ? 0 : routeRhythmIssues.Count)
            + (desktopReadabilityIsError || desktopReadabilityIssue is null ? 0 : 1)
            + (viewportHeightIsError || viewportHeightIssue is null ? 0 : 1);

        var metrics = new JsonObject
        {
            ["properCrossings"] = relationshipCrossings.Count,
            ["resolvedCrossovers"] = resolvedCrossovers.Count,
            ["ambiguousCorridors"] = ambiguousCorridors.Count,
            ["arrowheadCollisions"] = arrowheadCollisions.Count,
            ["containerBorderRuns"] = containerBorderRuns.Count,
            ["labelRouteClearanceIssues"] = labelRouteClearance.Count,
            ["labelCanvasOverflowIssues"] = labelCanvasOverflow.Count,
            ["minLabelRouteClearance"] = Geometry.MinimumLabelRouteClearance(labelRouteMeasurements),
            ["desktopReadabilityIssues"] = desktopReadabilityIssue is not null ? 1 : 0,
            ["viewportHeightIssues"] = viewportHeightIssue is not null ? 1 : 0,
            ["minProjectedNodeTextPx"] = readabilityEvidence["minimumProjectedTextPx"]?.DeepClone(),
        };
        AddRoundedRouteMetrics(metrics, routeMetrics);

        // Perceptual review needs the affected relationships, not just a total. These are review evidence, not new
        // pass/fail thresholds: a short, clear crossover can be preferable to a long crossing-free detour.
        var routeReview = new JsonObject
        {
            ["crossings"] = new JsonArray([.. resolvedCrossovers.Select(hit =>
            {
                var shared = new[] { hit.Left.From, hit.Left.To }.FirstOrDefault(id => T(id) && (id == hit.Right.From || id == hit.Right.To));
                var record = new JsonObject
                {
                    ["left"] = RelationshipRecord(hit.Left),
                    ["right"] = RelationshipRecord(hit.Right),
                    ["point"] = PtJson(hit.Point),
                };
                if (T(shared)) record["sharedNode"] = shared;
                return (JsonNode?)record;
            })]),
        };
        if (crowdedSides.Count > 0) routeReview["crowdedSides"] = new JsonArray([.. crowdedSides]);
        var detours = new JsonArray();
        foreach (var entry in routedRelationships)
        {
            var detourMetrics = SubpathBudgetMetrics([entry]);
            var blockers = DirectCorridorBlockers(entry.Relation, nodeRects);
            if (detourMetrics.RoutesOverSuggestedBends == 0 && detourMetrics.RoutesOverSuggestedStretch == 0) continue;
            var detour = new JsonObject
            {
                ["relationship"] = RelationshipRecord(entry.Relation),
                ["bends"] = detourMetrics.MaxBends,
                ["stretch"] = detourMetrics.MaxStretch is { } stretch ? Js.Round(stretch * 1000) / 1000 : null,
            };
            if (blockers.Count > 0) detour["directCorridorBlockers"] = new JsonArray([.. blockers.Select(node => (JsonNode?)node.ToJson())]);
            detours.Add(detour);
        }
        routeReview["detours"] = detours;

        var composition = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["profile"] = qualityProfile,
            ["status"] = compositionErrors != 0 ? "fail" : "pass",
            ["summary"] = new JsonObject { ["errors"] = compositionErrors, ["warnings"] = compositionWarnings },
            ["metrics"] = metrics,
            ["suggestedLimits"] = SuggestedLimits(),
            ["routeReview"] = routeReview,
            ["leadingSpace"] = leadingSpace,
        };
        if (sequenceColumnSpace is not null) composition["sequenceColumnSpace"] = sequenceColumnSpace;
        if (sequenceNoteBorders.Count > 0) composition["sequenceNoteBorders"] = new JsonArray([.. sequenceNoteBorders]);
        composition["desktopReadability"] = readabilityEvidence;

        var issues = new JsonArray();
        foreach (var hit in containerBorderRuns)
        {
            issues.Add(new JsonObject
            {
                ["severity"] = qualityGatesEnforced ? "error" : "warning",
                ["code"] = "composition/container-border-run",
                ["relationship"] = RelationshipRecord(Of(hit.Routed.Relation)),
                ["frame"] = new JsonObject { ["kind"] = hit.Frame.Kind, ["id"] = hit.Frame.Id?.DeepClone() },
                ["side"] = hit.Side,
                ["segmentIndex"] = hit.SegmentIndex,
                ["overlapLength"] = Round1(hit.OverlapLength),
                ["from"] = PtR1Json(hit.OverlapStart),
                ["to"] = PtR1Json(hit.OverlapEnd),
            });
        }
        foreach (var hit in labelRouteClearance)
        {
            issues.Add(new JsonObject
            {
                ["severity"] = labelClearanceIsError ? "error" : "warning",
                ["code"] = "composition/label-route-clearance",
                ["label"] = ClearanceLabelText(hit),
                ["labelRelationship"] = RelationshipRecord(Of(hit.LabelRelation)),
                ["otherRelationship"] = RelationshipRecord(Of(hit.OtherRelation)),
                ["segmentIndex"] = hit.SegmentIndex,
                ["labelRect"] = RoundedRect(hit.Rect),
                ["clearance"] = Round1(hit.Clearance),
                ["intersectionLength"] = Round1(Js.Or(hit.IntersectionLength, 0)),
                ["threshold"] = hit.Threshold,
                ["from"] = PtR1Json(hit.Start),
                ["to"] = PtR1Json(hit.End),
            });
        }
        foreach (var hit in labelCanvasOverflow)
        {
            var relation = Of(hit.Relation);
            var label = T(hit.Label.Label) ? hit.Label.Label! : T(relation.Label) ? relation.Label! : "";
            var overflowPx = new JsonObject();
            foreach (var (side, value) in hit.OverflowPx.Entries) overflowPx[side] = value;
            var origin = hit.ViewBoxOrigin.Any(v => v != 0 && !double.IsNaN(v)) ? $" at {Js.Num(hit.ViewBoxOrigin[0])},{Js.Num(hit.ViewBoxOrigin[1])}" : "";
            issues.Add(new JsonObject
            {
                ["severity"] = labelContainmentIsError ? "error" : "warning",
                ["code"] = "composition/label-canvas-containment",
                ["label"] = label,
                ["relationship"] = RelationshipRecord(relation),
                ["labelRect"] = RoundedRect(hit.Rect),
                ["viewBox"] = new JsonArray(hit.ViewBox[0], hit.ViewBox[1]),
                ["viewBoxOrigin"] = new JsonArray(hit.ViewBoxOrigin[0], hit.ViewBoxOrigin[1]),
                ["overflowPx"] = overflowPx,
                ["detail"] = $"[composition/label-canvas-containment] {qualityProfile} label \"{label}\" on {RelationshipName(relation)} extends past the {Geometry.DescribeLabelCanvasOverflow(hit)} (label rect {Geometry.FormatRect(hit.Rect)}; viewBox {Js.Num(hit.ViewBox[0])}x{Js.Num(hit.ViewBox[1])}{origin}) — use renderer-supported label controls (shorten the label or reorder participants for sequence; otherwise labelAt, labelDx, labelDy, or labelSegment), or enlarge meta.viewBox.",
            });
        }
        foreach (var hit in relationshipCrossings)
        {
            issues.Add(new JsonObject
            {
                ["severity"] = crossingIsError ? "error" : "warning",
                ["code"] = "composition/proper-crossing",
                ["relationship"] = RelationshipRecord(hit.Left),
                ["otherRelationship"] = RelationshipRecord(hit.Right),
                ["point"] = PtR1Json(hit.Point),
            });
        }
        foreach (var hit in ambiguousCorridors)
        {
            issues.Add(new JsonObject
            {
                ["severity"] = corridorIsError ? "error" : "warning",
                ["code"] = "composition/ambiguous-corridor",
                ["relationship"] = RelationshipRecord(Of(hit.Left.Relation)),
                ["otherRelationship"] = RelationshipRecord(Of(hit.Right.Relation)),
                ["segmentIndex"] = hit.LeftSegment,
                ["otherSegmentIndex"] = hit.RightSegment,
                ["overlapLength"] = Round1(hit.OverlapLength),
                ["from"] = PtR1Json(hit.OverlapStart),
                ["to"] = PtR1Json(hit.OverlapEnd),
            });
        }
        foreach (var hit in arrowheadCollisions)
        {
            issues.Add(new JsonObject
            {
                ["severity"] = corridorIsError ? "error" : "warning",
                ["code"] = "composition/arrowhead-collision",
                ["relationship"] = RelationshipRecord(Of(hit.Left.Relation)),
                ["otherRelationship"] = RelationshipRecord(Of(hit.Right.Relation)),
                ["distancePx"] = hit.Distance,
                ["minimumPx"] = hit.Minimum,
                ["endpoints"] = new JsonArray(PtJson(hit.Left.Tip), PtJson(hit.Right.Tip)),
            });
        }
        foreach (var hit in routeRhythmIssues)
        {
            issues.Add(new JsonObject
            {
                ["severity"] = rhythmIsError ? "error" : "warning",
                ["code"] = hit.Code,
                ["relationship"] = RelationshipRecord(Of(hit.Relation)),
                ["segmentIndex"] = hit.SegmentIndex,
                ["position"] = hit.Position,
                ["length"] = Round1(hit.Length),
                ["from"] = PtR1Json(hit.Start),
                ["to"] = PtR1Json(hit.End),
            });
        }
        if (desktopReadabilityIssue is { } issue)
        {
            var record = new JsonObject
            {
                ["severity"] = desktopReadabilityIsError ? "error" : "warning",
                ["code"] = "composition/desktop-readability",
            };
            if (T(issue.NodeId)) record["nodeId"] = issue.NodeId;
            record["owner"] = issue.Owner.ToJson();
            record["viewportWidth"] = DesktopReadability.DESKTOP_READABILITY_VIEWPORT.Width;
            record["viewportHeight"] = DesktopReadability.DESKTOP_READABILITY_VIEWPORT.Height;
            record["availableDiagramWidth"] = readabilityEvidence["availableDiagramWidth"]?.DeepClone();
            record["budgetBasis"] = readabilityEvidence["budgetBasis"]?.DeepClone();
            record["readerContract"] = readabilityEvidence["readerContract"]?.DeepClone();
            record["requestedTargetPx"] = readabilityEvidence["requestedTargetPx"]?.DeepClone();
            record["requestedTargetMet"] = readabilityEvidence["requestedTargetMet"]?.DeepClone();
            record["budgetLimit"] = readabilityEvidence["limit"]?.DeepClone();
            record["viewBoxWidth"] = issue.ViewBoxWidth;
            record["scale"] = issue.Scale;
            record["text"] = issue.Text;
            record["detail"] = issue.Detail;
            record["sourceFontPx"] = issue.SourceFontPx;
            record["projectedFontPx"] = issue.ProjectedFontPx;
            record["minimumProjectedFontPx"] = DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX;
            issues.Add(record);
        }
        if (viewportHeightIssue is { } overflow)
        {
            issues.Add(new JsonObject
            {
                ["severity"] = viewportHeightIsError ? "error" : "warning",
                ["code"] = "composition/viewport-height",
                ["readerFit"] = Or(A(svgAttrs, "data-reader-fit"), null),
                ["viewBoxWidth"] = viewBoxSize[0],
                ["viewBoxHeight"] = viewBoxSize[1],
                ["viewportWidth"] = overflow.ViewportWidth,
                ["viewportHeight"] = overflow.ViewportHeight,
                ["ratio"] = overflow.Ratio,
                ["wideRatio"] = overflow.WideRatio,
                ["svgWidthPx"] = overflow.SvgWidthPx,
                ["svgHeightPx"] = overflow.SvgHeightPx,
                ["fixedChromePx"] = overflow.FixedChromePx,
                ["pageHeightPx"] = overflow.PageHeightPx,
                ["overflowPx"] = overflow.OverflowPx,
                ["detail"] = $"[composition/viewport-height] {DesktopReadability.DescribeFixedWidthOverflow(overflow)}",
            });
        }
        composition["issues"] = issues;

        AddCheck("label_route_clearance", !labelClearanceIsError || labelRouteClearance.Count == 0, labelRouteClearance.Select(hit =>
            $"[composition/label-route-clearance] {qualityProfile} label \"{ClearanceLabelText(hit)}\" on {RelationshipName(Of(hit.LabelRelation))} is {Js.Num(Round1(hit.Clearance))}px from {RelationshipName(Of(hit.OtherRelation))} segment {Js.Num(hit.SegmentIndex)} [{FormatPoint(hit.Start)}] -> [{FormatPoint(hit.End)}]{(hit.IntersectionLength > 0 ? $" with {Js.Num(Round1(hit.IntersectionLength.Value))}px hidden by the mask" : "")} (minimum {Js.Num(hit.Threshold)}px) — use renderer-supported label controls (message y for sequence; otherwise labelAt, labelDx, labelDy, or labelSegment), or adjust the other relationship route/via/channel."));
        AddCheck("relationship_crossings", !crossingIsError || relationshipCrossings.Count == 0, relationshipCrossings.Select(hit =>
            $"[composition/proper-crossing] {qualityProfile} {RelationshipName(hit.Left)} crosses {RelationshipName(hit.Right)} at [{FormatPoint(hit.Point)}]"));
        AddCheck("relationship_corridors", !corridorIsError || ambiguousCorridors.Count + arrowheadCollisions.Count == 0,
        [
            .. ambiguousCorridors.Select(hit =>
                $"[composition/ambiguous-corridor] {qualityProfile} {RelationshipName(Of(hit.Left.Relation))} shares a {Js.Num(Round1(hit.OverlapLength))}px corridor with {RelationshipName(Of(hit.Right.Relation))} at [{FormatPoint(hit.OverlapStart)}] -> [{FormatPoint(hit.OverlapEnd)}]"),
            .. arrowheadCollisions.Select(hit =>
                $"[composition/arrowhead-collision] {qualityProfile} {RelationshipName(Of(hit.Left.Relation))} and {RelationshipName(Of(hit.Right.Relation))} have incoming arrowheads {Js.Num(hit.Distance)}px apart (minimum {Js.Num(hit.Minimum)}px) — enlarge or reposition the destination, or choose separate toSide ports."),
        ]);
        AddCheck("container_border_runs", !qualityGatesEnforced || containerBorderRuns.Count == 0, containerBorderRuns.Select(hit =>
            $"[composition/container-border-run] {RelationshipName(Of(hit.Routed.Relation))} follows {FrameName(hit.Frame)} {hit.Side} border for {Js.Num(Round1(hit.OverlapLength))}px on segment {Js.Num(hit.SegmentIndex)} [{FormatPoint(hit.OverlapStart)}] -> [{FormatPoint(hit.OverlapEnd)}]"));
        AddCheck("route_rhythm", !rhythmIsError || routeRhythmIssues.Count == 0, routeRhythmIssues.Select(hit =>
            $"[{hit.Code}] {qualityProfile} {RelationshipName(Of(hit.Relation))} has a {Js.Num(Round1(hit.Length))}px {hit.Position} segment {Js.Num(hit.SegmentIndex)} [{FormatPoint(hit.Start)}] -> [{FormatPoint(hit.End)}]"));

        if (legendStart >= 0)
        {
            var legendFragment = svg[legendStart..];
            var legendBoxes = CollectLegendBoxes(legendFragment);
            var collisions = CollectLegendCollisions(arrows, legendBoxes);
            AddCheck("legend_clearance", collisions.Count == 0, collisions.Select(hit => $"{hit.Arrow.Kind} {Js.Num(hit.Arrow.Index)} crosses legend {hit.Box.Label}"));
        }
        else
        {
            AddCheck("legend_clearance", true, ["no legend marker found"]);
        }
        return composition;
    }

    // ---- arrows -----------------------------------------------------------------------------------------------------
    List<Arrow> CollectArrows(string fragment, bool useActualPoints)
    {
        var arrows = new List<Arrow>();
        var index = 0;
        var pathIndex = 0;
        (string Name, Attrs? UnderlayAttrs)? previousTag = null;
        var previousTagEnd = 0;

        foreach (Match tag in SvgTagToken.Matches(fragment))
        {
            var name = TagName(tag);
            if (Closing(tag) || name is not ("path" or "line")) continue;
            if (name == "path") pathIndex += 1;
            var kind = name!;
            var tagStart = tag.Index;
            var raw = tag.Value;
            var gap = previousTag is not null ? fragment[previousTagEnd..tagStart] : "";
            var precedingUnderlay = previousTag is { } prev && Blank.IsMatch(gap) && prev.Name == "path" ? prev.UnderlayAttrs : null;
            previousTag = (kind, kind == "path" && AutomaticCrossoverUnderlayTag.IsMatch(raw) ? ParseAttrs(raw) : null);
            previousTagEnd = tagStart + raw.Length;
            if (!ArrowClass.IsMatch(raw)) continue;
            if (!MarkerEnd.IsMatch(raw)) continue;
            var attrs = ParseAttrs(raw);
            var routeStrokeWidth = NumberAttr(attrs, "stroke-width");
            var underlayStrokeWidth = precedingUnderlay is not null ? NumberAttr(precedingUnderlay, "stroke-width") : double.NaN;
            var verifiedCrossoverHalo = A(attrs, "data-composition-crossover") == "halo"
                && precedingUnderlay is not null
                && A(precedingUnderlay, "d") == A(attrs, "d")
                && A(precedingUnderlay, "fill") == "none"
                && A(precedingUnderlay, "stroke") == "var(--mask)"
                && A(precedingUnderlay, "pointer-events") == "none"
                && double.IsFinite(routeStrokeWidth)
                && double.IsFinite(underlayStrokeWidth)
                && underlayStrokeWidth >= routeStrokeWidth + 3;
            var line = name == "line";
            var parsed = line ? null : InspectedPath(attrs, tag.Index, pathIndex, "relationship");
            var segments = line ? LineSegments(attrs) : [.. parsed!.Subpaths.SelectMany(part => part.Segments)];
            var borderSegments = line ? segments : [.. parsed!.Subpaths.SelectMany(part => part.StraightSegments)];
            var routeSubpaths = line ? ContinuousRoutePoints(segments)
                : [.. parsed!.Subpaths.SelectMany(part => ContinuousRoutePoints(part.StraightSegments))];
            // Legacy single-path metadata remains supported. It cannot bridge actual moveto boundaries.
            if (!useActualPoints && (line || parsed!.Subpaths.Count == 1))
            {
                var authoredPoints = ParseRoutePoints(A(attrs, "data-composition-points"));
                if (authoredPoints is not null) routeSubpaths = [authoredPoints];
            }
            var arrow = new Arrow
            {
                Kind = kind,
                Index = index += 1,
                Raw = raw,
                // Trust route intent only for a semantic edge with one visible direct segment. A stale marker on
                // bent/curved geometry cannot waive the gate.
                AuthoredStraight = A(attrs, "data-composition-route") == "straight"
                    && T(A(attrs, "data-edge-from")) && T(A(attrs, "data-edge-to"))
                    && segments.Count == 1 && borderSegments.Count == 1
                    && (name == "line" || StraightD.IsMatch(A(attrs, "d") ?? "")),
                CrossoverHalo = verifiedCrossoverHalo,
                IndependentPorts = verifiedCrossoverHalo && A(attrs, "data-composition-independent") == "true",
                // Compatibility with first-round exports lacking a root layout contract. Readable-v2's root contract
                // supersedes this narrower automatic-pair rule. This marker never certifies a crossover halo or waives a
                // quality rule.
                AutomaticWorkflowRoute = A(attrs, "data-composition-routing") == "workflow-v2-auto",
                Segments = segments,
                BorderSegments = borderSegments,
                RoutePoints = routeSubpaths.Count == 1 ? routeSubpaths[0] : [],
                RouteSubpaths = routeSubpaths,
                Offset = tag.Index,
            };
            var variant = VariantClass.Match(raw);
            arrow.Put("junction", Or(A(attrs, "data-composition-junction"), null));
            if (double.IsFinite(routeStrokeWidth)) arrow.Rel["width"] = routeStrokeWidth;   // NaN width reads as falsy upstream
            arrow.Put("variant", variant.Success ? variant.Groups[1].Value : "default");
            arrow.Put("role", A(attrs, "data-edge-role"));
            arrow.From = Or(A(attrs, "data-edge-from"), A(attrs, "data-composition-edge-from"));
            arrow.To = Or(A(attrs, "data-edge-to"), A(attrs, "data-composition-edge-to"));
            arrow.Id = Or(A(attrs, "data-edge-id"), A(attrs, "data-composition-edge-id"));
            arrow.Key = A(attrs, "data-edge-key");
            arrow.Label = A(attrs, "data-edge-label");
            arrowOf[arrow.Rel] = arrow;
            arrows.Add(arrow);
        }
        return arrows;
    }

    static List<List<Pt>> ContinuousRoutePoints(List<Segment> segments)
    {
        var parts = new List<List<Pt>>();
        foreach (var (start, end) in segments)
        {
            var previous = parts.Count > 0 ? parts[^1] : null;
            // A non-collinear Q takes no part in the straight budget; its two ends must not be joined into a fictitious line.
            if (previous is not null && previous[^1].X == start.X && previous[^1].Y == start.Y) previous.Add(end);
            else parts.Add([start, end]);
        }
        // Keep the place of a subpath without straight lines, so an earlier piece is never taken for the semantic end.
        return parts.Count > 0 ? parts : [[]];
    }

    SvgPathAnalysisResult InspectedPath(Attrs attrs, int offset, int pathIndex, string role)
    {
        if (pathAnalyses.TryGetValue(offset, out var cached)) return cached;
        var result = SvgPathAnalysis.AnalyzeSvgPath(DecodeNumericReferences(Or(A(attrs, "d"), "")!));
        if (result.Ok)
        {
            pathAnalyses[offset] = result;
            return result;
        }
        var error = result.Error!;
        var evidence = new JsonObject { ["tokenOffset"] = error.TokenOffset, ["reason"] = error.Reason };
        if (error.Command is not null) evidence["command"] = error.Command;
        var id = Or(Or(A(attrs, "id"), A(attrs, "data-edge-id")), A(attrs, "data-composition-frame-id"));
        var subject = new JsonObject { ["role"] = role, ["pathIndex"] = pathIndex };
        if (T(id)) subject["id"] = id;
        pathDiagnostics.Add(new JsonObject
        {
            ["code"] = error.Code,
            ["severity"] = "error",
            ["message"] = $"{role} path {Js.Num(pathIndex)}{(T(id) ? $" ({id})" : "")}: {error.Reason} at d offset {Js.Num(error.TokenOffset)}.",
            ["subject"] = subject,
            ["evidence"] = evidence,
            ["supportedFixes"] = new JsonArray(error.Code == "artifact/svg-path-unsupported"
                ? "regenerate the inspected relationship or frame using M/L/H/V/Q/Z commands"
                : "correct the path data at the reported offset or regenerate it from the original diagram"),
        });
        var invalid = new SvgPathAnalysisResult(false, [], null);
        pathAnalyses[offset] = invalid;
        return invalid;
    }

    // ---- per-subpath wrappers over geometry.mjs ---------------------------------------------------------------------
    static RoutedRelation ToRouted(Entry entry) => new(entry.Points, entry.Relation.Rel, entry.RelationIndex);
    static RoutedRelation ToRouted(Part part) => new(part.Points, part.Relation.Rel, part.RelationIndex, part.SourceEndpoint, part.TargetEndpoint);

    // Keep relationship identity separate from its disconnected drawing pieces. Normalizing each piece also makes
    // evidence segment indices stable across consumers.
    static List<Part> RouteParts(Entry entry)
    {
        var subpaths = entry.Relation.RouteSubpaths;
        var segmentOffset = 0;
        var parts = new List<Part>();
        for (var subpathIndex = 0; subpathIndex < subpaths.Count; subpathIndex++)
        {
            var normalized = Geometry.NormalizeRoutePoints(subpaths[subpathIndex]);
            var part = new Part(entry.Relation, entry.RelationIndex, normalized, segmentOffset, subpathIndex == 0, subpathIndex == subpaths.Count - 1);
            segmentOffset += Math.Max(0, normalized.Count - 1);
            if (normalized.Count > 1) parts.Add(part);
        }
        return parts;
    }

    static RouteBudget SubpathBudgetMetrics(List<Entry> entries)
    {
        if (entries.All(entry => entry.Relation.RouteSubpaths.Count <= 1))
            return Geometry.RouteBudgetMetrics(entries.Select(ToRouted).ToList());
        var total = Geometry.RouteBudgetMetrics([]);
        foreach (var entry in entries)
        {
            var parts = RouteParts(entry);
            var metrics = Geometry.RouteBudgetMetrics(parts.Select(ToRouted).ToList());
            var maxBends = 0;
            foreach (var part in parts) maxBends += Math.Max(0, part.Points.Count - 2);
            // A relationship may exceed a budget in several pieces; count its identity once.
            total = total with
            {
                MaxBends = Math.Max(total.MaxBends, maxBends),
                RoutesOverSuggestedBends = total.RoutesOverSuggestedBends + (maxBends > 2 ? 1 : 0),
                MaxStretch = metrics.MaxStretch is { } stretch ? Math.Max(total.MaxStretch ?? stretch, stretch) : total.MaxStretch,
                RoutesOverSuggestedStretch = total.RoutesOverSuggestedStretch + (metrics.RoutesOverSuggestedStretch > 0 ? 1 : 0),
                MinSegmentPx = metrics.MinSegmentPx is { } min ? Math.Min(total.MinSegmentPx ?? min, min) : total.MinSegmentPx,
                MinInteriorSegmentPx = metrics.MinInteriorSegmentPx is { } interior ? Math.Min(total.MinInteriorSegmentPx ?? interior, interior) : total.MinInteriorSegmentPx,
                ShortSegmentCount = total.ShortSegmentCount + metrics.ShortSegmentCount,
                ShortEndpointSegmentCount = total.ShortEndpointSegmentCount + metrics.ShortEndpointSegmentCount,
                ShortInteriorSegmentCount = total.ShortInteriorSegmentCount + metrics.ShortInteriorSegmentCount,
                MicroSegmentCount = total.MicroSegmentCount + metrics.MicroSegmentCount,
            };
        }
        return total;
    }

    // `...roundedRouteMetrics(routeMetrics)`: the budget's own key order, three of them rounded.
    static void AddRoundedRouteMetrics(JsonObject target, RouteBudget metrics)
    {
        target["maxBends"] = metrics.MaxBends;
        target["routesOverSuggestedBends"] = metrics.RoutesOverSuggestedBends;
        target["maxStretch"] = metrics.MaxStretch is { } stretch ? Js.Round(stretch * 1000) / 1000 : null;
        target["routesOverSuggestedStretch"] = metrics.RoutesOverSuggestedStretch;
        target["minSegmentPx"] = metrics.MinSegmentPx is { } min ? Round1(min) : null;
        target["minInteriorSegmentPx"] = metrics.MinInteriorSegmentPx is { } interior ? Round1(interior) : null;
        target["shortSegmentCount"] = metrics.ShortSegmentCount;
        target["shortEndpointSegmentCount"] = metrics.ShortEndpointSegmentCount;
        target["shortInteriorSegmentCount"] = metrics.ShortInteriorSegmentCount;
        target["microSegmentCount"] = metrics.MicroSegmentCount;
    }

    static List<RhythmIssue> SubpathRhythmIssues(List<Entry> entries) =>
        [.. entries.SelectMany(entry => RouteParts(entry).SelectMany(part =>
            Geometry.CollectRouteRhythmIssues([ToRouted(part)]).Select(issue => issue with { SegmentIndex = issue.SegmentIndex + part.SegmentOffset })))];

    List<CorridorHit> SubpathCorridors(List<Entry> routedRelations, Func<Arrow, Arrow, bool> includeSharedEndpoints,
        bool allowShortWorkflowTrunks, Func<Arrow, Arrow, bool> includeSharedEndpointCounterflow)
    {
        bool Shared(JsonObject left, JsonObject right) => includeSharedEndpoints(Of(left), Of(right));
        bool Counterflow(JsonObject left, JsonObject right) => includeSharedEndpointCounterflow(Of(left), Of(right));
        if (routedRelations.All(entry => entry.Relation.RouteSubpaths.Count <= 1))
            return Geometry.CollectAmbiguousCorridors(routedRelations.Select(ToRouted).ToList(), 8, Shared, Counterflow, allowShortWorkflowTrunks);
        var hits = new List<CorridorHit>();
        var parts = routedRelations.Select(RouteParts).ToList();
        for (var left = 0; left < parts.Count; left += 1)
        {
            for (var right = left + 1; right < parts.Count; right += 1)
            {
                CorridorHit? longest = null;
                foreach (var a in parts[left])
                {
                    foreach (var b in parts[right])
                    {
                        var found = Geometry.CollectAmbiguousCorridors([ToRouted(a), ToRouted(b)], 8, Shared, Counterflow, allowShortWorkflowTrunks);
                        var hit = found.Count > 0 ? found[0] : null;
                        if (hit is not null && (longest is null || hit.OverlapLength > longest.OverlapLength + 0.0001))
                            longest = hit with { LeftSegment = hit.LeftSegment + a.SegmentOffset, RightSegment = hit.RightSegment + b.SegmentOffset };
                    }
                }
                if (longest is not null) hits.Add(longest);
            }
        }
        return hits;
    }

    static List<LabelClearanceHit> SubpathLabelClearance(List<Entry> routedRelations, List<LabelRect> labels, double threshold)
    {
        if (routedRelations.All(entry => entry.Relation.RouteSubpaths.Count <= 1))
            return Geometry.CollectLabelRouteClearance(labels, routedRelations.Select(ToRouted).ToList(), threshold);
        // The shared helper deduplicates by relationship identity. Measure each piece independently, then preserve one
        // nearest hit per label/relationship pair.
        var hits = new List<LabelClearanceHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // lookup only
        foreach (var entry in routedRelations)
        {
            var relation = entry.Relation;
            var identity = relation.Key is not null ? $"key:{relation.Key}"
                : T(relation.Id) ? $"id:{relation.From ?? ""}\0{relation.To ?? ""}\0{relation.Id}" : $"index:{Js.Num(entry.RelationIndex)}";
            if (seen.Contains(identity) || RouteParts(entry).Count == 0) continue;
            seen.Add(identity);
            var nearest = new JsMap<int, LabelClearanceHit>();
            foreach (var part in RouteParts(entry))
            {
                foreach (var hit in Geometry.CollectLabelRouteClearance(labels, [ToRouted(part)], threshold))
                {
                    var current = nearest.Get(hit.LabelRelationIndex);
                    if (current is null || hit.Clearance < current.Clearance)
                        nearest.Set(hit.LabelRelationIndex, hit with { SegmentIndex = hit.SegmentIndex + part.SegmentOffset });
                }
            }
            hits.AddRange(nearest.Values);
        }
        return hits;
    }

    List<LabelRect> CollectRelationshipLabelMasks(string fragment, List<Arrow> arrows)
    {
        var labels = new List<LabelRect>();
        foreach (Match match in EdgeLabelGroup.Matches(fragment))
        {
            var group = match.Value;
            var groupOpen = GOpenTag.Match(group);
            var groupAttrs = ParseAttrs(groupOpen.Success ? groupOpen.Value : "");
            var rectTag = RectTag.Matches(group).Select(item => item.Value).FirstOrDefault(tag => MaskClass.IsMatch(tag));
            if (rectTag is null) continue;
            var attrs = ParseAttrs(rectTag);
            var (x, y, width, height) = (NumberAttr(attrs, "x"), NumberAttr(attrs, "y"), NumberAttr(attrs, "width"), NumberAttr(attrs, "height"));
            if (!(double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height))) continue;
            var groupStart = match.Index;
            var groupEnd = groupStart + group.Length;
            var groupKey = A(groupAttrs, "data-edge-key");
            var groupId = A(groupAttrs, "data-edge-id");
            var groupFrom = A(groupAttrs, "data-edge-from");
            var groupTo = A(groupAttrs, "data-edge-to");
            var containedOwner = arrows.FirstOrDefault(arrow =>
                arrow.Offset > groupStart
                && arrow.Offset < groupEnd
                && arrow.From == groupFrom
                && arrow.To == groupTo
                && (!T(groupId) || !T(arrow.Id) || arrow.Id == groupId));
            var owner = arrows.FirstOrDefault(arrow => groupKey is not null && arrow.Key == groupKey)
                ?? containedOwner
                ?? arrows.FirstOrDefault(arrow => arrow.Id == groupId && arrow.From == groupFrom && arrow.To == groupTo);
            if (owner is null) continue;
            if (owner.Key is null && groupKey is not null) owner.Key = groupKey;
            if (!T(owner.Id) && T(groupId)) owner.Id = groupId;
            if (!T(owner.Label) && T(A(groupAttrs, "data-edge-label"))) owner.Label = A(groupAttrs, "data-edge-label");
            labels.Add(new LabelRect
            {
                Relation = owner.Rel,
                RelationIndex = owner.Index,
                Label = Or(A(groupAttrs, "data-edge-label"), ""),
                X = x, Y = y, Width = width, Height = height,
            });
        }
        return labels;
    }

    static JsonObject RoundedRect(Rect rect) => new()
    {
        ["x"] = Round1(rect.X), ["y"] = Round1(rect.Y), ["width"] = Round1(rect.Width), ["height"] = Round1(rect.Height),
    };

    static List<Pt>? ParseRoutePoints(string? value)
    {
        if (!T(value)) return null;
        var points = value!.Split(';').Select(pair => pair.Split(',').Select(Js.ToNumber).ToList()).ToList();
        return points.Count >= 2 && points.All(IsPoint) ? [.. points.Select(p => new Pt(p[0], p[1]))] : null;
    }

    static List<Crossing> CollectRelationshipCrossings(List<Arrow> arrows, bool includeSharedEndpoints)
    {
        // `{ ...arrow, segments }`: a copy taken now, before the label pass fills in key/id/label on the originals.
        var relationships = arrows.Where(arrow => T(arrow.From) && T(arrow.To) && arrow.Segments.Count > 0).Select(arrow => (
            Arrow: arrow.Snapshot(),
            // Readable-v2 routePoints come from the visible path, never its metadata. A straight-through via is not a
            // visual endpoint; preserve real bends.
            Segments: includeSharedEndpoints
                ? [.. arrow.RouteSubpaths.SelectMany(points => Geometry.ForwardCollinearAnalysisSegments(points)).Select(s => new Segment(s.Start, s.End))]
                : arrow.Segments)).ToList();
        var crossings = new List<Crossing>();
        for (var leftIndex = 0; leftIndex < relationships.Count; leftIndex += 1)
        {
            var left = relationships[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < relationships.Count; rightIndex += 1)
            {
                var right = relationships[rightIndex];
                // A shared semantic endpoint does not make an interior X a junction when both paths opt into automatic
                // workflow or independent-port checks.
                var shares = new[] { left.Arrow.From, left.Arrow.To }.Any(id => id == right.Arrow.From || id == right.Arrow.To);
                if (shares
                    && !includeSharedEndpoints
                    && !(left.Arrow.IndependentPorts && right.Arrow.IndependentPorts)
                    && !(left.Arrow.AutomaticWorkflowRoute && right.Arrow.AutomaticWorkflowRoute)) continue;
                Pt? point = null;
                foreach (var leftSegment in left.Segments)
                {
                    foreach (var rightSegment in right.Segments)
                    {
                        point = ProperSegmentIntersection(leftSegment.Start, leftSegment.End, rightSegment.Start, rightSegment.End);
                        if (point is not null) break;
                    }
                    if (point is not null) break;
                }
                if (point is { } p) crossings.Add(new(left.Arrow, right.Arrow, p));
            }
        }
        return crossings;
    }

    static string RelationshipName(Arrow arrow) => T(arrow.Id)
        ? $"relationship id \"{arrow.Id}\" (\"{S(arrow.From)}\" -> \"{S(arrow.To)}\")"
        : $"relationship \"{S(arrow.From)}\" -> \"{S(arrow.To)}\"";

    // Review evidence only. Mask rectangles are the visible node bounds emitted by our renderers. Skip transformed
    // ancestry rather than mixing coordinate spaces.
    static List<NodeRect> CollectUntransformedNodeRects(string fragment)
    {
        var groups = new List<Attrs>();
        var nodes = new JsMap<string, NodeRect?>();
        foreach (Match token in SvgTagToken.Matches(fragment))
        {
            if (!token.Groups[2].Success) continue;
            var name = TagName(token);
            if (name == "g")
            {
                if (Closing(token)) { if (groups.Count > 0) groups.RemoveAt(groups.Count - 1); }
                else if (!SelfClosing.IsMatch(token.Value)) groups.Add(ParseAttrs(token.Value));
                continue;
            }
            if (name != "rect" || Closing(token) || groups.Any(group => T(A(group, "transform")))) continue;
            var attrs = ParseAttrs(token.Value);
            var owner = Enumerable.Reverse(groups).FirstOrDefault(group => T(A(group, "data-node-id")));
            if (owner is null || T(A(attrs, "transform")) || !ClassList(attrs).Contains("c-mask")) continue;
            double[] box = [NumberAttr(attrs, "x"), NumberAttr(attrs, "y"), NumberAttr(attrs, "width"), NumberAttr(attrs, "height")];
            if (!box.All(double.IsFinite) || box[2] <= 0 || box[3] <= 0) continue;
            var id = A(owner, "data-node-id")!;
            // Ambiguous ownership is not reliable evidence for moving a node.
            nodes.Set(id, nodes.Has(id) ? null : new NodeRect(id, Or(A(owner, "data-node-label"), id)!, box));
        }
        return [.. nodes.Values.OfType<NodeRect>()];
    }

    static List<NodeRect> DirectCorridorBlockers(Arrow relation, List<NodeRect> nodes)
    {
        var from = nodes.FirstOrDefault(node => node.Id == relation.From);
        var to = nodes.FirstOrDefault(node => node.Id == relation.To);
        if (from is null || to is null || ReferenceEquals(from, to)) return [];
        static Pt Center(NodeRect node) => new(node.Box[0] + node.Box[2] / 2, node.Box[1] + node.Box[3] / 2);
        var a = Center(from);
        var b = Center(to);
        var horizontal = Math.Abs(a.Y - b.Y) < 0.01;
        var vertical = Math.Abs(a.X - b.X) < 0.01;
        if (horizontal == vertical) return [];
        var axis = horizontal ? 0 : 1;
        var cross = 1 - axis;
        double Coord(Pt p, int i) => i == 0 ? p.X : p.Y;
        var (first, last) = Coord(a, axis) < Coord(b, axis) ? (from, to) : (to, from);
        var low = first.Box[axis] + first.Box[axis + 2];
        var high = last.Box[axis];
        if (high <= low) return [];
        return [.. nodes.Where(node => !ReferenceEquals(node, from) && !ReferenceEquals(node, to)
            && node.Box[axis] < high && node.Box[axis] + node.Box[axis + 2] > low
            && node.Box[cross] < Coord(a, cross) && node.Box[cross] + node.Box[cross + 2] > Coord(a, cross))];
    }

    // Automatic ports need a 16px corner gutter and 14px between neighbours, so a side facing more counterparts than
    // that fits pushes routes onto other sides.
    static List<JsonNode?> CrowdedNodeSides(List<Arrow> arrows, List<NodeRect> nodes)
    {
        var byId = new Dictionary<string, NodeRect>(StringComparer.Ordinal);   // lookup only
        foreach (var node in nodes) byId[node.Id] = node;
        var demand = new JsMap<string, (NodeRect Node, string Side, int Relationships)>();
        foreach (var arrow in arrows)
        {
            if (arrow.From is null || arrow.To is null || !byId.TryGetValue(arrow.From, out var from) || !byId.TryGetValue(arrow.To, out var to)
                || ReferenceEquals(from, to)) continue;
            foreach (var (node, other) in new[] { (from, to), (to, from) })
            {
                var (x, y, w, h) = (node.Box[0], node.Box[1], node.Box[2], node.Box[3]);
                var dx = other.Box[0] + other.Box[2] / 2 - (x + w / 2);
                var dy = other.Box[1] + other.Box[3] / 2 - (y + h / 2);
                var side = dx != 0 && Math.Abs(dx) >= Math.Abs(dy) ? (dx < 0 ? "left" : "right") : (dy > 0 ? "bottom" : "top");
                var key = $"{node.Id}\u0000{side}";
                (NodeRect Node, string Side, int Relationships) entry = demand.Has(key) ? demand.Get(key) : (node, side, 0);
                demand.Set(key, (entry.Node, entry.Side, entry.Relationships + 1));
            }
        }
        var sides = new List<JsonNode?>();
        foreach (var (node, side, relationships) in demand.Values)
        {
            var sidePx = side is "left" or "right" ? node.Box[3] : node.Box[2];
            var neededPx = 32 + 14 * (relationships - 1);
            if (relationships > 1 && sidePx < neededPx)
                sides.Add(new JsonObject
                {
                    ["node"] = node.Id, ["label"] = node.Label, ["side"] = side, ["relationships"] = relationships,
                    ["sidePx"] = sidePx, ["neededPx"] = neededPx,
                });
        }
        return sides;
    }

    static JsonObject RelationshipRecord(Arrow arrow)
    {
        var stableIndex = arrow.Key is null ? double.NaN : Js.ToNumber(arrow.Key);
        var record = new JsonObject();
        if (arrow.Id is not null) record["id"] = arrow.Id;
        if (arrow.From is not null) record["from"] = arrow.From;
        if (arrow.To is not null) record["to"] = arrow.To;
        record["label"] = Or(arrow.Label, "");
        record["collectionIndex"] = IsInteger(stableIndex) && stableIndex >= 0 ? stableIndex : arrow.Index - 1;
        record["artifactIndex"] = arrow.Index;
        return record;
    }

    List<Frame> CollectCompositionFrames(string fragment)
    {
        var frames = new List<Frame>();
        var pathIndex = 0;
        foreach (Match match in SvgTagToken.Matches(fragment))
        {
            var name = TagName(match);
            if (Closing(match) || name is not ("rect" or "path" or "line")) continue;
            if (name == "path") pathIndex += 1;
            var attrs = ParseAttrs(match.Value);
            var kind = A(attrs, "data-composition-frame-kind");
            if (!T(kind)) continue;
            JsonNode identity = T(A(attrs, "data-composition-frame-id")) ? A(attrs, "data-composition-frame-id")! : frames.Count;
            if (name == "rect")
            {
                var rx = NumberAttr(attrs, "rx");
                var frame = new Frame(NumberAttr(attrs, "x"), NumberAttr(attrs, "y"), NumberAttr(attrs, "width"), NumberAttr(attrs, "height"),
                    Radius: rx != 0 && !double.IsNaN(rx) ? rx : 0, Kind: kind, Id: identity);
                if (double.IsFinite(frame.X) && double.IsFinite(frame.Y) && double.IsFinite(frame.Width) && double.IsFinite(frame.Height)) frames.Add(frame);
                continue;
            }
            var segments = name == "line"
                ? LineSegments(attrs)
                : [.. InspectedPath(attrs, match.Index, pathIndex, "structural-frame").Subpaths.SelectMany(part => part.Segments)];
            for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                frames.Add(new Frame(double.NaN, double.NaN, double.NaN, double.NaN, Kind: kind,
                    Id: segments.Count > 1 ? $"{IdText(identity)}:{Js.Num(segmentIndex)}" : identity.DeepClone(),
                    Shape: "line", Start: segment.Start, End: segment.End));
            }
        }
        return frames;
    }

    // Evidence for author review only. Automatic Architecture is identifiable by its intrinsic Reader contract; authored
    // canvases must retain their geometry.
    static JsonObject CollectArchitectureLeadingSpace(Attrs svgAttrs, string fragment, List<NodeRect> nodeRects, List<Frame> frames,
        List<Arrow> arrows, List<LabelRect> labels)
    {
        var evidence = new JsonObject { ["measured"] = false, ["reviewSuggested"] = false };
        if (A(svgAttrs, "data-reader-fit") != "intrinsic-height"
            || A(svgAttrs, "data-reader-primary-text") != "14"
            || T(A(svgAttrs, "transform"))
            || GTransform.Matches(fragment).Any(match => !SemanticSigilAttr.IsMatch(match.Value))
            || ShapeTransform.IsMatch(fragment)) return evidence;
        var viewBox = ViewBoxRect(svgAttrs);
        var (originY, height) = (viewBox[1], viewBox[3]);
        if (!viewBox.All(double.IsFinite) || viewBox[2] <= 0 || height <= 0) return evidence;
        var nodeCount = NodeIdGroup.Matches(fragment).Count;
        if (nodeRects.Count == 0 || nodeRects.Count != nodeCount) return evidence;
        var semanticArrows = arrows.Where(arrow => T(arrow.From) && T(arrow.To)).ToList();
        if (semanticArrows.Any(arrow => !arrow.RouteSubpaths.Any(points => points.Count > 0))) return evidence;

        var occupied = nodeRects.Select(node => node.Box[1]).ToList();
        foreach (var frame in frames)
        {
            var top = frame.Shape == "line" ? Math.Min(frame.Start!.Value.Y, frame.End!.Value.Y) : frame.Y;
            if (!double.IsFinite(top)) return evidence;
            occupied.Add(top);
        }
        // Boundary titles can protrude above their structural frame.
        foreach (Match match in FrameLabelMask.Matches(fragment))
        {
            var rectMatch = RectTag.Match(match.Value);
            var rect = ParseAttrs(rectMatch.Success ? rectMatch.Value : "");
            var y = NumberAttr(rect, "y");
            if (!double.IsFinite(y)) return evidence;
            occupied.Add(y);
        }
        foreach (var arrow in semanticArrows)
            foreach (var points in arrow.RouteSubpaths) foreach (var point in points) occupied.Add(point.Y);
        foreach (var label in labels) occupied.Add(label.Y);
        if (!occupied.All(double.IsFinite)) return evidence;
        var occupiedTop = Js.MinOf(occupied);
        var gap = Math.Max(0, occupiedTop - originY);
        var heights = nodeRects.Select(node => node.Box[3]).ToList();
        Js.Sort(heights, (a, b) => a - b);
        var typicalNodeHeight = heights[heights.Count / 2];
        var ratio = gap / height;
        return new JsonObject
        {
            ["measured"] = true,
            ["emptyTopPx"] = Round1(gap),
            ["emptyTopRatio"] = Js.Round(ratio * 1000) / 1000,
            ["occupiedTop"] = Round1(occupiedTop),
            ["viewBoxTop"] = originY,
            ["canvasHeight"] = height,
            ["typicalNodeHeight"] = typicalNodeHeight,
            ["reviewSuggested"] = gap > 2 * typicalNodeHeight && ratio > 0.2,
        };
    }

    // Advisory only: fixed columns are a compatibility contract. Measure semantic content, including long labels/notes,
    // rather than treating every wide canvas as wasted space. Auto-sized segment frames do not add a participant column.
    static JsonObject? CollectSequenceColumnSpace(Attrs svgAttrs, string fragment, List<NodeRect> nodeRects, List<Arrow> arrows)
    {
        var columnFit = A(svgAttrs, "data-sequence-column-fit");
        if (columnFit is not ("fixed" or "spread")) return null;
        var evidence = new JsonObject { ["measured"] = false, ["reviewSuggested"] = false, ["columnFit"] = columnFit };
        if (T(A(svgAttrs, "transform"))) return evidence;
        // Brand badges stay inside their participant box. Ignore only that subtree's transforms, including the preset
        // path or nested fallback icon's scale.
        var brandGroups = new List<bool>();
        foreach (Match token in SvgTagToken.Matches(fragment))
        {
            if (!token.Groups[2].Success) continue;
            var name = TagName(token);
            if (Closing(token))
            {
                if (name == "g" && brandGroups.Count > 0) brandGroups.RemoveAt(brandGroups.Count - 1);
                continue;
            }
            var attrs = ParseAttrs(token.Value);
            var inBrand = (brandGroups.Count > 0 && brandGroups[^1]) || (name == "g"
                && T(A(attrs, "data-brand-mark"))
                && ClassList(attrs).Contains("brand-mark"));
            if (!inBrand && T(A(attrs, "transform")) && (name is "g" or "path" or "line" or "rect" or "text")
                && !(name == "g" && T(A(attrs, "data-semantic-sigil")))) return evidence;
            if (name == "g" && !SelfClosing.IsMatch(token.Value)) brandGroups.Add(inBrand);
        }
        var viewBox = ViewBoxRect(svgAttrs);
        var (originX, width, height) = (viewBox[0], viewBox[2], viewBox[3]);
        if (!(double.IsFinite(originX) && double.IsFinite(width) && double.IsFinite(height)) || width <= 0 || height <= 0) return evidence;
        var nodeCount = NodeIdGroup.Matches(fragment).Count;
        if (nodeRects.Count == 0 || nodeCount != nodeRects.Count) return evidence;
        var semanticArrows = arrows.Where(arrow => T(arrow.From) && T(arrow.To)).ToList();
        if (semanticArrows.Any(arrow => !arrow.RouteSubpaths.Any(points => points.Count > 0))) return evidence;
        var rightEdges = nodeRects.Select(node => node.Box[0] + node.Box[2]).ToList();
        foreach (var arrow in semanticArrows)
            foreach (var points in arrow.RouteSubpaths) foreach (var point in points) rightEdges.Add(point.X);
        // Label plates, activations and segment titles also reserve horizontal room.
        foreach (Match match in RectTag.Matches(fragment))
        {
            var attrs = ParseAttrs(match.Value);
            if (!ClassList(attrs).Contains("c-mask")) continue;
            rightEdges.Add(NumberAttr(attrs, "x") + NumberAttr(attrs, "width"));
        }
        foreach (Match match in TextElement.Matches(fragment))
        {
            var attrs = ParseAttrs(match.Groups[1].Value);
            // A wrapped note places each line in a <tspan> with its own x. Any other line offset is not measured here.
            var lines = TspanOpen.IsMatch(match.Groups[2].Value)
                ? TspanElement.Matches(match.Groups[2].Value)
                    .Select(line => (Attrs: Merge(attrs, ParseAttrs(line.Groups[1].Value)), Text: Js.Trim(StripTags(line.Groups[2].Value)))).ToList()
                : [(Attrs: attrs, Text: Js.Trim(StripTags(match.Groups[2].Value)))];
            if (lines.Count == 0 || lines.Any(line => line.Attrs.ContainsKey("dx"))) return evidence;
            foreach (var line in lines)
            {
                var box = TextBox(line.Attrs, line.Text);
                if (box is null) return evidence;
                rightEdges.Add(box.X2);
            }
        }
        if (!rightEdges.All(double.IsFinite)) return evidence;
        var occupiedRight = Js.MaxOf(rightEdges);
        var gap = Math.Max(0, originX + width - occupiedRight);
        var ratio = gap / width;
        var widths = nodeRects.Select(node => node.Box[2]).ToList();
        Js.Sort(widths, (a, b) => a - b);
        var typicalParticipantWidth = widths[widths.Count / 2];
        return new JsonObject
        {
            ["measured"] = true,
            ["columnFit"] = columnFit,
            ["participantCount"] = nodeCount,
            ["occupiedRight"] = Round1(occupiedRight),
            ["viewBoxLeft"] = originX,
            ["canvasWidth"] = width,
            ["emptyRightPx"] = Round1(gap),
            ["emptyRightRatio"] = Js.Round(ratio * 1000) / 1000,
            ["typicalParticipantWidth"] = typicalParticipantWidth,
            // Avoid stretching a small conversation merely to fill its canvas. These conservative review thresholds
            // never contribute errors or warnings.
            ["reviewSuggested"] = columnFit == "fixed" && nodeCount >= 4 && gap > 2 * typicalParticipantWidth && ratio > 0.25,
        };
    }

    // Sequence notes are direct text children of a semantic message group; message labels use a nested group. Inspect
    // the emitted lines without changing layout. This is advisory: phase ownership still belongs to the author.
    static List<JsonNode?> CollectSequenceNoteBorders(Attrs svgAttrs, string fragment, List<Frame> frames, List<Arrow> arrows)
    {
        if (!T(A(svgAttrs, "data-sequence-column-fit")) || T(A(svgAttrs, "transform"))) return [];
        var segments = frames.Where(frame => frame.Kind == "segment" && !T(frame.Shape)).ToList();
        if (segments.Count == 0) return [];
        var issues = new List<JsonNode?>();
        var stack = new List<Attrs>();
        foreach (Match tag in SvgTagToken.Matches(fragment))
        {
            var name = TagName(tag);
            if (name == "g")
            {
                if (Closing(tag)) { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
                else if (!tag.Value.EndsWith("/>", StringComparison.Ordinal)) stack.Add(ParseAttrs(tag.Value));
                continue;
            }
            if (name == "rect" && !Closing(tag))
            {
                var rectAttrs = ParseAttrs(tag.Value);
                if (A(rectAttrs, "data-composition-frame-kind") == "segment"
                    && (T(A(rectAttrs, "transform")) || stack.Any(group => T(A(group, "transform"))))) return [];
            }
            if (name != "text" || Closing(tag) || stack.Any(group => T(A(group, "transform")))) continue;
            var owner = stack.Count > 0 ? stack[^1] : null;
            var attrs = ParseAttrs(tag.Value);
            if (owner is null || !T(A(owner, "data-edge-key")) || A(attrs, "data-detail") is not ("context" or "fine")
                || T(A(attrs, "transform")) || T(A(attrs, "rotate")) || T(A(attrs, "dx")) || T(A(attrs, "dy"))) continue;
            var index = Js.ToNumber(A(owner, "data-edge-key")!);
            if (!IsSafeInteger(index) || index < 0) continue;
            var arrow = index < arrows.Count ? arrows[(int)index] : null;
            var arrowY = arrow is not null && arrow.RoutePoints.Count > 0 ? arrow.RoutePoints[0].Y : double.NaN;
            if (!double.IsFinite(arrowY)) continue;
            var end = fragment.IndexOf("</text>", tag.Index + tag.Length, StringComparison.Ordinal);
            if (end < 0) continue;
            var content = fragment[(tag.Index + tag.Length)..end];
            var baseline = NumberAttr(attrs, "y");
            var boxes = new List<TextBoxRect>();
            var spans = TspanElement.Matches(content).Select(span => (Attrs: span.Groups[1].Value, Text: span.Groups[2].Value)).ToList();
            var supported = true;
            foreach (var span in spans.Count > 0 ? spans : [(Attrs: "", Text: content)])
            {
                var line = ParseAttrs(span.Attrs);
                if (line.ContainsKey("y") || line.ContainsKey("dx") || line.ContainsKey("rotate") || line.ContainsKey("transform")) { supported = false; break; }
                baseline += Js.ToNumber(Or(A(line, "dy"), "0")!);
                var merged = Merge(Merge(attrs, line), new Attrs { ["y"] = Js.Num(baseline) });
                var box = TextBox(merged, StripTags(span.Text));
                if (box is null || !(double.IsFinite(box.X1) && double.IsFinite(box.X2) && double.IsFinite(box.Y1) && double.IsFinite(box.Y2))) { supported = false; break; }
                boxes.Add(box);
            }
            if (!supported || boxes.Count == 0) continue;
            var noteX = Js.MinOf(boxes.Select(b => b.X1));
            var noteY = Js.MinOf(boxes.Select(b => b.Y1));
            var noteWidth = Js.MaxOf(boxes.Select(b => b.X2)) - noteX;
            var noteHeight = Js.MaxOf(boxes.Select(b => b.Y2)) - noteY;
            var bottom = noteY + noteHeight;
            foreach (var frame in segments)
            {
                var border = frame.Y + frame.Height;
                if (!(arrowY >= frame.Y && arrowY < border && noteY < border && bottom > border + 0.5)) continue;
                var requiredTo = Math.Ceiling(bottom + 4);
                // A single-field fix may not swallow another message or stage, or push the frame beyond an authored
                // canvas. Otherwise ask for a joint review.
                var viewBox = ViewBoxRect(svgAttrs);
                var safe = Digits.IsMatch(IdText(frame.Id)) && requiredTo <= viewBox[1] + viewBox[3] - 45
                    && !arrows.Any(other => other.RoutePoints.Any(point => point.Y > border - 4 && point.Y < requiredTo + 4))
                    && !segments.Any(other => !ReferenceEquals(other, frame) && other.Y < requiredTo && other.Y + other.Height > border);
                var segmentPath = $"/segments/{IdText(frame.Id)}/to";
                var messageName = Or(Or(A(owner, "data-edge-label"), A(owner, "data-edge-id")), Js.Num(index));
                issues.Add(new JsonObject
                {
                    ["code"] = "sequence/note-segment-border",
                    ["reviewSuggested"] = true,
                    ["message"] = $"Note on message \"{messageName}\" ends at y={Js.Num(Math.Ceiling(bottom))}, crossing its segment bottom y={Js.Num(border)}. {(safe ? $"Set {segmentPath} to {Js.Num(requiredTo)} to keep the full note inside." : "Review the note and neighbouring phase boundaries together; keep the full note and message order.")}",
                    ["subject"] = new JsonObject { ["diagramType"] = "sequence", ["path"] = $"/messages/{Js.Num(index)}/note" },
                    ["evidence"] = new JsonObject
                    {
                        ["note"] = new JsonObject { ["x"] = noteX, ["y"] = noteY, ["width"] = noteWidth, ["height"] = noteHeight },
                        ["lines"] = boxes.Count,
                        ["messageY"] = arrowY,
                        ["segment"] = new JsonObject { ["path"] = segmentPath, ["from"] = frame.Y, ["to"] = border },
                        ["requiredTo"] = requiredTo,
                    },
                    ["supportedFixes"] = safe ? new JsonArray($"set {segmentPath} to {Js.Num(requiredTo)}") : new JsonArray(),
                });
            }
        }
        return issues;
    }

    static string FrameName(Frame frame) => $"{(T(frame.Kind) ? frame.Kind : "frame")} \"{IdText(frame.Id)}\"";

    static string FormatPoint(Pt point) => $"{Js.Num(Round1(point.X))}, {Js.Num(Round1(point.Y))}";

    static List<Segment> LineSegments(Attrs attrs)
    {
        var start = new Pt(NumberAttr(attrs, "x1"), NumberAttr(attrs, "y1"));
        var end = new Pt(NumberAttr(attrs, "x2"), NumberAttr(attrs, "y2"));
        if (!IsPoint(start) || !IsPoint(end)) return [];
        return [new(start, end)];
    }

    static List<int> DiagonalStraightSegments(Arrow arrow)
    {
        if (arrow.AuthoredStraight) return [];
        var indexes = new List<int>();
        for (var segmentIndex = 0; segmentIndex < arrow.BorderSegments.Count; segmentIndex++)
        {
            var (start, end) = arrow.BorderSegments[segmentIndex];
            if (Math.Abs(start.X - end.X) > 0.01 && Math.Abs(start.Y - end.Y) > 0.01) indexes.Add(segmentIndex);
        }
        return indexes;
    }

    // ---- legend -----------------------------------------------------------------------------------------------------
    static List<TextBoxRect> CollectLegendBoxes(string fragment)
    {
        var boxes = new List<TextBoxRect>();
        foreach (Match match in RectTag.Matches(fragment))
        {
            var attrs = ParseAttrs(match.Value);
            var (x, y, width, height) = (NumberAttr(attrs, "x"), NumberAttr(attrs, "y"), NumberAttr(attrs, "width"), NumberAttr(attrs, "height"));
            if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height))
                boxes.Add(new(x, y, x + width, y + height, $"rect@{Js.Num(x)},{Js.Num(y)}"));
        }
        foreach (Match match in TextElement.Matches(fragment))
        {
            var attrs = ParseAttrs(match.Groups[1].Value);
            var box = TextBox(attrs, Js.Trim(StripTags(match.Groups[2].Value)), LegendTextWidth);
            if (box is not null) boxes.Add(box);
        }
        return boxes;
    }

    static List<(Arrow Arrow, TextBoxRect Box)> CollectLegendCollisions(List<Arrow> arrows, List<TextBoxRect> boxes)
    {
        var collisions = new List<(Arrow, TextBoxRect)>();
        foreach (var arrow in arrows)
            foreach (var segment in arrow.Segments)
                foreach (var box in boxes)
                    if (SegmentIntersectsBox(segment, PadBox(box, 2))) collisions.Add((arrow, box));
        return collisions;
    }

    // The footprint a legend entry reserves is the renderer's own: textUnits() at TEXT_ADVANCE_EM
    // (renderers/shared/legend.mjs). The default estimate below stays as it is for the readability projection, which is
    // deliberately conservative.
    static double LegendTextWidth(string text, double fontSize)
    {
        const double TextAdvanceEm = 0.62;
        return Math.Max(fontSize, Utils.TextUnits(text) * fontSize * TextAdvanceEm);
    }

    static TextBoxRect? TextBox(Attrs attrs, string text, Func<string, double, double>? measure = null)
    {
        measure ??= EstimatedTextWidth;
        var x = NumberAttr(attrs, "x");
        var y = NumberAttr(attrs, "y");
        var fontSize = Js.ParseFloat(Or(A(attrs, "font-size"), "10")!);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(fontSize)) return null;
        var width = measure(text, fontSize);
        var anchor = Or(A(attrs, "text-anchor"), "start");
        var x1 = x;
        if (anchor == "middle") x1 = x - width / 2;
        if (anchor == "end") x1 = x - width;
        return new(x1, y - fontSize, x1 + width, y + fontSize * 0.25, T(text) ? text : $"text@{Js.Num(x)},{Js.Num(y)}");
    }

    // A foreign artifact may author a legal non-zero viewBox origin, so containment needs all four numbers; sizing checks
    // read the trailing pair.
    static double[] ViewBoxRect(Attrs svgAttrs)
    {
        var viewBox = ViewBoxSeparator.Split(Js.Trim(Or(A(svgAttrs, "viewBox"), "")!)).Select(Js.ToNumber).ToArray();
        return viewBox.Length == 4 ? viewBox : [double.NaN, double.NaN, double.NaN, double.NaN];
    }

    static double[] ViewBoxSize(Attrs svgAttrs) => ViewBoxRect(svgAttrs)[2..];

    static string? ReaderContractFromHtml(string source)
    {
        var markers = MetaTag.Matches(source).Select(match => ParseAttrs(match.Value))
            .Where(attrs => A(attrs, "name") == "archify-reader-contract").ToList();
        return markers.Count == 1 && A(markers[0], "content") == DesktopReadability.DECLARED_WIDE_READER_CONTRACT
            ? DesktopReadability.DECLARED_WIDE_READER_CONTRACT
            : null;
    }

    static (JsonObject Evidence, ReadabilityEntry? Issue) CollectDesktopReadability(Attrs svgAttrs, string fragment, string? contract)
    {
        var viewBoxSize = ViewBoxSize(svgAttrs);
        var (viewBoxWidth, viewBoxHeight) = (viewBoxSize[0], viewBoxSize[1]);
        var requestedMinimumTextPx = Js.ParseFloat(Or(A(svgAttrs, "data-reader-min-text"), "")!);
        var entries = new List<ReadabilityEntry>();
        var invalidSemanticText = false;
        var groups = new List<Attrs>();
        // Keep the complete ancestry, rather than a node-only stack: an edge can share its context group with its label
        // or nest that group (Sequence).
        foreach (Match match in ReadabilityToken.Matches(fragment))
        {
            if (match.Value.StartsWith("<!", StringComparison.Ordinal)) continue;
            if (!match.Groups[1].Success)
            {
                if (GClose.IsMatch(match.Value)) { if (groups.Count > 0) groups.RemoveAt(groups.Count - 1); }
                else if (!SelfClosing.IsMatch(match.Value)) groups.Add(ParseAttrs(match.Value));
                continue;
            }
            var rawAttrs = match.Groups[1].Value;
            var attrs = ParseAttrs(rawAttrs);
            if (A(attrs, "data-detail") == "fine" || groups.Any(group => A(group, "data-detail") == "fine")) continue;
            var primary = DataNodeLabel.IsMatch(rawAttrs);
            var boundary = DataBoundaryLabel.IsMatch(rawAttrs);
            var context = A(attrs, "data-detail") == "context" || groups.Any(group => A(group, "data-detail") == "context");
            var nodeOwner = Enumerable.Reverse(groups).FirstOrDefault(group => T(A(group, "data-node-id")));
            var edgeOwner = Enumerable.Reverse(groups).FirstOrDefault(group => T(A(group, "data-edge-from")) && T(A(group, "data-edge-to")));
            var owner = primary
                ? new Owner("node", nodeOwner is not null ? Or(A(nodeOwner, "data-node-id"), null) : null)
                : boundary ? new Owner("boundary", null)
                    : context && edgeOwner is not null ? new Owner("edge", Or(A(edgeOwner, "data-edge-id"), null), A(edgeOwner, "data-edge-from"), A(edgeOwner, "data-edge-to"))
                        : context && nodeOwner is not null ? new Owner("node", A(nodeOwner, "data-node-id"))
                            : null;
            // A context text with neither semantic owner is legend/fine/loose copy.
            if (owner is null) continue;
            var fontSize = Js.ParseFloat(Or(A(attrs, "font-size"), "")!);
            if (!double.IsFinite(fontSize))
            {
                invalidSemanticText = true;
                continue;
            }
            if (fontSize <= 0) invalidSemanticText = true;
            entries.Add(new ReadabilityEntry
            {
                NodeId = owner.Kind == "node" && T(owner.Id) ? owner.Id : null,
                Owner = owner,
                Text = Js.Trim(StripTags(match.Groups[2].Value)),
                Detail = primary ? "primary" : boundary ? "boundary" : owner.Kind == "edge" ? "edge" : "context",
                SourceFontPx = fontSize,
            });
        }
        var minimumSourceTextPx = entries.Count > 0 ? Js.MinOf(entries.Select(entry => entry.SourceFontPx)) : double.NaN;
        var eligible = contract == DesktopReadability.DECLARED_WIDE_READER_CONTRACT
            && A(svgAttrs, "data-reader-fit") is "intrinsic-height" or "width-first"
            && double.IsFinite(requestedMinimumTextPx) && requestedMinimumTextPx > 0
            && !invalidSemanticText && entries.Count > 0;
        var declared = eligible ? DesktopReadability.DeclaredWideReadabilityBudget(viewBoxWidth, viewBoxHeight, minimumSourceTextPx, requestedMinimumTextPx) : null;
        var availableDiagramWidth = declared?.GuaranteedSvgWidth ?? DesktopReadability.DESKTOP_READER_DIAGRAM_WIDTH;
        var budgetBasis = declared is not null ? "recognized-declared-wide" : "legacy-930";
        var scale = double.IsFinite(viewBoxWidth) && viewBoxWidth > 0 ? Math.Min(1, availableDiagramWidth / viewBoxWidth) : double.NaN;
        ReadabilityEntry? worst = null;
        foreach (var entry in entries)
        {
            entry.ViewBoxWidth = viewBoxWidth;
            entry.Scale = scale;
            entry.ProjectedFontPx = DesktopReadability.ProjectedNodeTextPx(entry.SourceFontPx, viewBoxWidth, availableDiagramWidth);
            if (worst is null || entry.ProjectedFontPx < worst.ProjectedFontPx) worst = entry;
        }
        double? projectedMinimumTextPx = worst is not null && double.IsFinite(worst.ProjectedFontPx) ? worst.ProjectedFontPx : null;
        bool? hardFloorMet = worst is not null && double.IsFinite(worst.ProjectedFontPx)
            ? worst.ProjectedFontPx >= DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX : null;
        bool? requestedTargetMet = worst is not null && double.IsFinite(requestedMinimumTextPx)
            ? worst.ProjectedFontPx >= requestedMinimumTextPx : null;
        var evidence = new JsonObject
        {
            ["budgetBasis"] = budgetBasis,
            ["readerContract"] = contract,
            ["availableDiagramWidth"] = availableDiagramWidth,
            ["actualBudgetPx"] = availableDiagramWidth,
        };
        if (declared is not null)
        {
            evidence["actualReaderWidth"] = declared.ActualReaderWidth;
            evidence["desiredReaderWidth"] = declared.DesiredReaderWidth;
            evidence["viewportCap"] = declared.ViewportCap;
            evidence["limit"] = declared.Limit;
        }
        else
        {
            evidence["limit"] = "legacy";
        }
        evidence["requestedTargetPx"] = double.IsFinite(requestedMinimumTextPx) ? requestedMinimumTextPx : null;
        evidence["requestedTargetMet"] = requestedTargetMet;
        evidence["hardFloorPx"] = DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX;
        evidence["hardFloorMet"] = hardFloorMet;
        evidence["minimumOwner"] = worst?.Owner.ToJson();
        evidence["minimumSourceTextPx"] = worst?.SourceFontPx;
        evidence["minimumProjectedTextPx"] = projectedMinimumTextPx;
        evidence["semanticTextCount"] = entries.Count;
        return (evidence, worst is not null && hardFloorMet == false ? worst : null);
    }

    static double EstimatedTextWidth(string text, double fontSize)
    {
        double units = 0;
        // `for (const char of text)`: code points; charCodeAt(0) of a pair is its high surrogate (> 255).
        for (var i = 0; i < text.Length; i++)
        {
            units += text[i] > 255 ? 1.8 : 0.62;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        }
        return Math.Max(fontSize, units * fontSize);
    }

    static Pt? ProperSegmentIntersection(Pt a, Pt b, Pt c, Pt d)
    {
        var abC = CrossProduct(a, b, c);
        var abD = CrossProduct(a, b, d);
        var cdA = CrossProduct(c, d, a);
        var cdB = CrossProduct(c, d, b);
        const double epsilon = 1e-9;
        static bool Opposite(double left, double right) => (left > epsilon && right < -epsilon) || (left < -epsilon && right > epsilon);
        if (!Opposite(abC, abD) || !Opposite(cdA, cdB)) return null;
        var denominator = (a.X - b.X) * (c.Y - d.Y) - (a.Y - b.Y) * (c.X - d.X);
        if (Math.Abs(denominator) < epsilon) return null;
        var ab = a.X * b.Y - a.Y * b.X;
        var cd = c.X * d.Y - c.Y * d.X;
        return new Pt(
            (ab * (c.X - d.X) - (a.X - b.X) * cd) / denominator,
            (ab * (c.Y - d.Y) - (a.Y - b.Y) * cd) / denominator);
    }

    static double CrossProduct(Pt a, Pt b, Pt c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static bool SegmentIntersectsBox(Segment segment, TextBoxRect box)
    {
        var (start, end) = segment;
        if (PointInsideBox(start, box) || PointInsideBox(end, box)) return true;
        (Pt, Pt)[] edges =
        [
            (new(box.X1, box.Y1), new(box.X2, box.Y1)),
            (new(box.X2, box.Y1), new(box.X2, box.Y2)),
            (new(box.X2, box.Y2), new(box.X1, box.Y2)),
            (new(box.X1, box.Y2), new(box.X1, box.Y1)),
        ];
        return edges.Any(edge => SegmentsIntersect(start, end, edge.Item1, edge.Item2));
    }

    static bool SegmentsIntersect(Pt a, Pt b, Pt c, Pt d)
    {
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);

        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment(a, c, b)) return true;
        if (o2 == 0 && OnSegment(a, d, b)) return true;
        if (o3 == 0 && OnSegment(c, a, d)) return true;
        if (o4 == 0 && OnSegment(c, b, d)) return true;
        return false;
    }

    static int Orientation(Pt a, Pt b, Pt c)
    {
        var value = (b.Y - a.Y) * (c.X - b.X) - (b.X - a.X) * (c.Y - b.Y);
        if (Math.Abs(value) < 1e-9) return 0;
        return value > 0 ? 1 : 2;
    }

    static bool OnSegment(Pt a, Pt b, Pt c) =>
        b.X <= Math.Max(a.X, c.X) + 1e-9
        && b.X + 1e-9 >= Math.Min(a.X, c.X)
        && b.Y <= Math.Max(a.Y, c.Y) + 1e-9
        && b.Y + 1e-9 >= Math.Min(a.Y, c.Y);

    static bool PointInsideBox(Pt point, TextBoxRect box) => point.X >= box.X1 && point.X <= box.X2 && point.Y >= box.Y1 && point.Y <= box.Y2;

    static TextBoxRect PadBox(TextBoxRect box, double padding) =>
        box with { X1 = box.X1 - padding, Y1 = box.Y1 - padding, X2 = box.X2 + padding, Y2 = box.Y2 + padding };

    // Only geometry/numeric attributes are scanned: authored prose such as node tags, <title> text, aria-labels, or
    // data-* attributes may legitimately mention "NaN" or "Infinity" without any coordinate being non-finite.
    static List<string> CollectNonFiniteAttrs(string svg)
    {
        var details = new List<string>();
        var stack = new List<(string Element, bool InSvg)>();
        foreach (Match match in SvgTagToken.Matches(svg))
        {
            if (!match.Groups[2].Success) continue;
            var element = TagName(match)!;
            if (Closing(match))
            {
                var index = stack.FindLastIndex(entry => entry.Element == element);
                if (index >= 0) stack.RemoveRange(index, stack.Count - index);
                continue;
            }
            (string Element, bool InSvg)? parent = stack.Count > 0 ? stack[^1] : null;
            var inSvg = element == "svg" || (parent is { } p && p.InSvg && !SvgHtmlIntegrationPoints.Contains(p.Element));
            if (inSvg)
            {
                foreach (var (name, value) in AttrEntries(match.Value))
                {
                    if (!IsNumericAttr(element, name) || !NonFiniteToken.IsMatch(DecodeNumericReferences(value))) continue;
                    details.Add($"{match.Groups[2].Value} {name}=\"{value}\"");
                }
            }
            // HTML void elements do not open a context; SVG self-closing tags do not either. A nested <svg> inside
            // foreignObject restores SVG checking.
            if (!(inSvg ? SelfClosing.IsMatch(match.Value) : HtmlVoidElements.Contains(element))) stack.Add((element, inSvg));
        }
        return details;
    }

    static readonly string Replacement = new((char)0xFFFD, 1);

    static string DecodeNumericReferences(string value) =>
        // Numeric references can encode every letter of NaN/Infinity/undefined. Decode once, locally: other checks and
        // diagnostic evidence keep raw values.
        NumericReference.Replace(value, match =>
        {
            var hex = match.Groups[1];
            var digits = hex.Success ? hex.Value : match.Groups[2].Value;
            var radix = hex.Success ? 16 : 10;
            double point = 0;   // Number.parseInt: comparisons below only need the magnitude
            foreach (var c in digits) point = point * radix + (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
            return point > 0 && point <= 0x10ffff && !(point >= 0xd800 && point <= 0xdfff) ? char.ConvertFromUtf32((int)point) : Replacement;
        });

    static bool IsNumericAttr(string elementName, string attrName)
    {
        var normalizedAttr = attrName.ToLowerInvariant();
        return NumericAttrs.Contains(normalizedAttr)
            || (ElementNumericAttrs.TryGetValue(elementName.ToLowerInvariant(), out var extra) && extra.Contains(normalizedAttr));
    }

    static List<(string Name, string Value)> AttrEntries(string tag) =>
        [.. HtmlAttribute.Matches(tag).Select(match => (match.Groups[1].Value,
            match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value))];

    // Object.fromEntries: the last duplicate wins. Lookup only, never enumerated.
    static Attrs ParseAttrs(string tag)
    {
        var attrs = new Attrs(StringComparer.Ordinal);
        foreach (var (name, value) in AttrEntries(tag)) attrs[name] = value;
        return attrs;
    }

    // `{ ...left, ...right }`
    static Attrs Merge(Attrs left, Attrs right)
    {
        var merged = new Attrs(left, StringComparer.Ordinal);
        foreach (var (name, value) in right) merged[name] = value;
        return merged;
    }

    static double NumberAttr(Attrs attrs, string name) => A(attrs, name) is { } value ? Js.ParseFloat(value) : double.NaN;

    static bool IsPoint(Pt point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    static bool IsPoint(List<double> point) => point.Count == 2 && point.All(double.IsFinite);

    static string StripTags(string value) => AnyTag.Replace(value, "");

    // ---- JS value helpers ---------------------------------------------------------------------------------------------
    static string? A(Attrs attrs, string name) => attrs.TryGetValue(name, out var value) ? value : null;
    static bool T(string? s) => !string.IsNullOrEmpty(s);
    static string? Or(string? left, string? right) => T(left) ? left : right;   // JS `left || right` on strings
    static string S(string? s) => s ?? "undefined";
    static double Round1(double v) => Js.Round(v * 10) / 10;
    static JsonArray PtJson(Pt p) => new(p.X, p.Y);
    static JsonArray PtR1Json(Pt p) => new(Round1(p.X), Round1(p.Y));
    static List<string> ClassList(Attrs attrs) => [.. Whitespace.Split(Or(A(attrs, "class"), "")!)];
    static bool IsInteger(double v) => double.IsFinite(v) && Math.Floor(v) == v;
    static bool IsSafeInteger(double v) => IsInteger(v) && Math.Abs(v) <= 9007199254740991;
    // String(frame.id): the authored string, or the frame index as a number.
    static string IdText(JsonNode? id) => id is JsonValue v && v.TryGetValue<string>(out var s) ? s : id is null ? "undefined" : Js.Num(Js.ToDouble((JsonValue)id));
    // hit.label?.label || hit.labelRelation?.label || ''
    string ClearanceLabelText(LabelClearanceHit hit)
    {
        var relation = Of(hit.LabelRelation);
        return T(hit.Label.Label) ? hit.Label.Label! : T(relation.Label) ? relation.Label! : "";
    }
    static string? TagName(Match tag) => tag.Groups[2].Success ? tag.Groups[2].Value.ToLowerInvariant() : null;
    static bool Closing(Match tag) => tag.Groups[1].Success && tag.Groups[1].Length > 0;   // `tag[1]` is "/" or ""
}
