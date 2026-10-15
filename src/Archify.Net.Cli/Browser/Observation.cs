using System.Text.Json.Nodes;

namespace Archify.Net.Cli.Browser;

// What the viewer-gate metrics script returns (upstream/viewer-gate/metrics.js, VC:1635-1799), as typed fields. A missing number
// reads as null. pageComposition, workflowLanes and the text owner pass through to the receipt untouched, so they stay JSON.
public sealed record ViewportMetrics
{
    public JsonObject? PageComposition { get; init; }
    public double? InnerWidth { get; init; }
    public double? InnerHeight { get; init; }
    public double? ScrollWidth { get; init; }
    public double? ScrollHeight { get; init; }
    public string? ResolvedTheme { get; init; }
    public string? ReaderLayout { get; init; }
    public string? ReaderOverflow { get; init; }
    public string? ReaderFit { get; init; }
    public string? DiagramType { get; init; }
    public bool? DocumentScrollUnclipped { get; init; }
    public double? ReaderWidth { get; init; }
    public double? DiagramWidth { get; init; }
    public double? ViewBoxWidth { get; init; }
    public JsonArray? WorkflowLanes { get; init; }
    public double? MinimumProjectedNodeTextPx { get; init; }
    public double? MinimumProjectedNonEdgeTextPx { get; init; }
    public double? MinimumProjectedEdgeTextPx { get; init; }
    public string? MinimumProjectedNodeText { get; init; }
    public string? MinimumProjectedNodeTextDetail { get; init; }
    public JsonObject? MinimumProjectedNodeTextOwner { get; init; }
    public bool HasLegend { get; init; }
    public bool HasNavigationDock { get; init; }
    public double? LegendDockIntersectionArea { get; init; }
    public double? DockStageIntersectionArea { get; init; }
    public double? DockStageGap { get; init; }
    public double? ViewerChromeRequiredGap { get; init; }
    public double? ViewerChromeReserve { get; init; }
    public bool ViewerChromeActive { get; init; }
}

// One viewport's verdict: observation() (VC:1867-1957), a pure function of the viewport and its metrics.
internal sealed record Observation
{
    public int Width { get; init; }
    public int Height { get; init; }
    public required string Theme { get; init; }
    public double InnerWidth { get; init; }
    public double InnerHeight { get; init; }
    public double ScrollWidth { get; init; }
    public double ScrollHeight { get; init; }
    public bool OverflowX { get; init; }
    public bool OverflowY { get; init; }
    public bool VerticalScrollAccepted { get; init; }
    public required string OverflowDisposition { get; init; }
    public string? ReaderLayout { get; init; }
    public string? ReaderOverflow { get; init; }
    public string? ReaderFit { get; init; }
    // diagramType and documentScrollUnclipped are keys only when readerFit is 'authored-height'
    public string? DiagramType { get; init; }
    public bool DocumentScrollUnclipped { get; init; }
    public bool Ok { get; init; }
    public double? ReaderWidth { get; init; }
    public double? DiagramWidth { get; init; }
    public JsonObject? PageComposition { get; init; }
    public double? ViewBoxWidth { get; init; }
    public JsonArray? WorkflowLanes { get; init; }
    public double? MinimumProjectedNodeTextPx { get; init; }
    public double? MinimumProjectedNonEdgeTextPx { get; init; }
    public double? MinimumProjectedEdgeTextPx { get; init; }
    public string? MinimumProjectedNodeText { get; init; }
    public string? MinimumProjectedNodeTextDetail { get; init; }
    public JsonObject? MinimumProjectedNodeTextOwner { get; init; }
    public bool ReadabilityOk { get; init; }
    public bool HasLegend { get; init; }
    public bool HasNavigationDock { get; init; }
    public double LegendDockIntersectionArea { get; init; }
    public double DockStageIntersectionArea { get; init; }
    public double? DockStageGap { get; init; }
    public double RequiredDockStageGap { get; init; }
    public bool ViewerChromeStageOk { get; init; }
    public double ViewerChromeReserve { get; init; }
    public bool ViewerChromeActive { get; init; }
    public bool ViewerChromeOk { get; init; }
    public required string ResolvedTheme { get; init; }

    bool AuthoredHeight => ReaderFit == "authored-height";
    public bool AuthoredClipped => AuthoredHeight && DiagramType == "architecture" && !DocumentScrollUnclipped;

    // `Number(x)`: a missing value is NaN. `Number(x) || fallback`: 0 and NaN fall back too.
    static double Number(double? value) => value ?? double.NaN;
    static double? Truthy(double? value) => value is { } v && v != 0 && !double.IsNaN(v) ? v : null;
    static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static Observation From(int width, int height, string theme, ViewportMetrics metrics)
    {
        var innerWidth = Number(metrics.InnerWidth);
        var innerHeight = Number(metrics.InnerHeight);
        var scrollWidth = Number(metrics.ScrollWidth);
        var scrollHeight = Number(metrics.ScrollHeight);
        var overflowX = scrollWidth > innerWidth;
        var overflowY = scrollHeight > innerHeight;
        var minimum = metrics.MinimumProjectedNodeTextPx;
        var readabilityOk = minimum is null || minimum >= DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX;
        var readerLayout = Truthy(metrics.ReaderLayout);
        var readerOverflow = Truthy(metrics.ReaderOverflow);
        var readerFit = Truthy(metrics.ReaderFit);
        var unclipped = metrics.DocumentScrollUnclipped == true;
        var verticalScrollAccepted = overflowY && !overflowX && readabilityOk && minimum is { } px && double.IsFinite(px)
            && ((readerLayout == "adaptive" && readerOverflow == "authored" && readerFit is "intrinsic-height" or "width-first")
                || (readerFit == "authored-height" && metrics.DiagramType == "architecture" && unclipped));
        var authoredClipped = readerFit == "authored-height" && metrics.DiagramType == "architecture" && !unclipped;
        var containmentOk = !authoredClipped && !overflowX && (!overflowY || verticalScrollAccepted);
        var legendDockIntersectionArea = Truthy(metrics.LegendDockIntersectionArea) ?? 0;
        var dockStageIntersectionArea = Truthy(metrics.DockStageIntersectionArea) ?? 0;
        var dockStageGap = metrics.DockStageGap;
        var requiredDockStageGap = metrics.ViewerChromeRequiredGap is { } gap && double.IsFinite(gap) ? gap : 0;
        var viewerChromeStageOk = !metrics.HasNavigationDock
            || (dockStageGap is { } stageGap && double.IsFinite(stageGap) && dockStageIntersectionArea <= 0.5 && stageGap >= requiredDockStageGap - 1);
        return new Observation
        {
            Width = width,
            Height = height,
            Theme = theme,
            InnerWidth = innerWidth,
            InnerHeight = innerHeight,
            ScrollWidth = scrollWidth,
            ScrollHeight = scrollHeight,
            OverflowX = overflowX,
            OverflowY = overflowY,
            VerticalScrollAccepted = verticalScrollAccepted,
            OverflowDisposition = authoredClipped || overflowX || (overflowY && !verticalScrollAccepted) ? "unexpected-overflow"
                : verticalScrollAccepted ? "readable-vertical-scroll" : "contained",
            ReaderLayout = readerLayout,
            ReaderOverflow = readerOverflow,
            ReaderFit = readerFit,
            DiagramType = metrics.DiagramType,
            DocumentScrollUnclipped = unclipped,
            Ok = containmentOk,
            ReaderWidth = Truthy(metrics.ReaderWidth),
            DiagramWidth = Truthy(metrics.DiagramWidth),
            PageComposition = metrics.PageComposition,
            ViewBoxWidth = Truthy(metrics.ViewBoxWidth),
            WorkflowLanes = metrics.WorkflowLanes is { Count: > 0 } lanes ? lanes : null,
            MinimumProjectedNodeTextPx = minimum,
            MinimumProjectedNonEdgeTextPx = metrics.MinimumProjectedNonEdgeTextPx,
            MinimumProjectedEdgeTextPx = metrics.MinimumProjectedEdgeTextPx,
            MinimumProjectedNodeText = Truthy(metrics.MinimumProjectedNodeText),
            MinimumProjectedNodeTextDetail = Truthy(metrics.MinimumProjectedNodeTextDetail),
            MinimumProjectedNodeTextOwner = metrics.MinimumProjectedNodeTextOwner,
            ReadabilityOk = readabilityOk,
            HasLegend = metrics.HasLegend,
            HasNavigationDock = metrics.HasNavigationDock,
            LegendDockIntersectionArea = legendDockIntersectionArea,
            DockStageIntersectionArea = dockStageIntersectionArea,
            DockStageGap = dockStageGap,
            RequiredDockStageGap = requiredDockStageGap,
            ViewerChromeStageOk = viewerChromeStageOk,
            ViewerChromeReserve = Truthy(metrics.ViewerChromeReserve) ?? 0,
            ViewerChromeActive = metrics.ViewerChromeActive,
            ViewerChromeOk = legendDockIntersectionArea <= 0.5 && viewerChromeStageOk,
            ResolvedTheme = Truthy(metrics.ResolvedTheme) ?? theme,
        };
    }

    // The observation's key order; a capture appends its `file`.
    public JsonObject ToJson(string? file = null)
    {
        var json = new JsonObject
        {
            ["width"] = Width, ["height"] = Height, ["theme"] = Theme, ["innerWidth"] = InnerWidth, ["innerHeight"] = InnerHeight,
            ["scrollWidth"] = ScrollWidth, ["scrollHeight"] = ScrollHeight, ["overflowX"] = OverflowX, ["overflowY"] = OverflowY,
            ["verticalScrollAccepted"] = VerticalScrollAccepted, ["overflowDisposition"] = OverflowDisposition, ["readerLayout"] = ReaderLayout,
            ["readerOverflow"] = ReaderOverflow, ["readerFit"] = ReaderFit,
        };
        if (AuthoredHeight)
        {
            json["diagramType"] = DiagramType;
            json["documentScrollUnclipped"] = DocumentScrollUnclipped;
        }
        json["ok"] = Ok;
        json["readerWidth"] = ReaderWidth;
        json["diagramWidth"] = DiagramWidth;
        if (PageComposition is not null) json["pageComposition"] = PageComposition.DeepClone();
        json["viewBoxWidth"] = ViewBoxWidth;
        if (WorkflowLanes is not null) json["workflowLanes"] = WorkflowLanes.DeepClone();
        json["minimumProjectedNodeTextPx"] = MinimumProjectedNodeTextPx;
        json["minimumProjectedNonEdgeTextPx"] = MinimumProjectedNonEdgeTextPx;
        json["minimumProjectedEdgeTextPx"] = MinimumProjectedEdgeTextPx;
        json["minimumProjectedNodeText"] = MinimumProjectedNodeText;
        json["minimumProjectedNodeTextDetail"] = MinimumProjectedNodeTextDetail;
        json["minimumProjectedNodeTextOwner"] = MinimumProjectedNodeTextOwner?.DeepClone();
        json["minimumRequiredNodeTextPx"] = DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX;
        json["readabilityOk"] = ReadabilityOk;
        json["hasLegend"] = HasLegend;
        json["hasNavigationDock"] = HasNavigationDock;
        json["legendDockIntersectionArea"] = LegendDockIntersectionArea;
        json["dockStageIntersectionArea"] = DockStageIntersectionArea;
        json["dockStageGap"] = DockStageGap;
        json["requiredDockStageGap"] = RequiredDockStageGap;
        json["viewerChromeStageOk"] = ViewerChromeStageOk;
        json["viewerChromeReserve"] = ViewerChromeReserve;
        json["viewerChromeActive"] = ViewerChromeActive;
        json["viewerChromeOk"] = ViewerChromeOk;
        json["resolvedTheme"] = ResolvedTheme;
        if (file is not null) json["file"] = file;
        return json;
    }

    // verticalBudgetFixes (VC:2059-2083): which stacked block holds the height, and what the SVG or cards must shrink to.
    public static IReadOnlyList<string> VerticalBudgetFixes(Observation entry)
    {
        var page = entry.PageComposition;
        if (!entry.OverflowY || page is null) return [];
        double Px(string key) => page[key] is { } value ? value.D() : double.NaN;
        var excess = entry.ScrollHeight - entry.InnerHeight;
        var fixes = new List<string>();
        var stacked = $"{Js.Num(Px("bodyPaddingPx"))}px body padding + {Js.Num(Px("headerPx"))}px header + {Js.Num(Px("diagramChromePx"))}px diagram chrome + "
            + $"{Js.Num(Px("svgPx"))}px SVG + {Js.Num(Px("cardsPx"))}px cards = {Js.Num(entry.ScrollHeight)}px against {Js.Num(entry.InnerHeight)}px";
        var svgPx = Px("svgPx");
        var viewBoxHeight = Px("viewBoxHeight");
        var diagramWidth = entry.DiagramWidth is { } width ? Js.Num(width) : "null";
        if (svgPx > 0 && viewBoxHeight > 0)
        {
            var targetSvg = svgPx - excess;
            var targetViewBoxHeight = Math.Floor(viewBoxHeight * targetSvg / svgPx);
            if (entry.ReaderLayout == "adaptive")
                fixes.Add(targetSvg > 0
                    ? $"the page is {Js.Num(excess)}px too tall ({stacked}); the Reader already narrowed the stage to its {diagramWidth}px minimum, so the SVG height only follows meta.viewBox: keep every node and relationship and reduce the viewBox height to at most {Js.Num(targetViewBoxHeight)} (from {Js.Num(viewBoxHeight)}) by tightening vertical gaps and empty rows"
                    : $"the page is {Js.Num(excess)}px too tall ({stacked}) and the SVG alone exceeds the viewport at the minimum reader width; split the diagram into two");
            else
                fixes.Add($"the page is {Js.Num(excess)}px too tall ({stacked}); the SVG spans the full {diagramWidth}px reader width because its viewBox ratio is below 1.55 and it declares no automatic Reader fit, so the Reader can neither narrow it nor accept readable vertical scroll: either remove meta.viewBox so the renderer sizes the canvas and declares the fit, or make the viewBox at least 1.55x wider than tall");
        }
        var cardsPx = Px("cardsPx");
        if (cardsPx >= excess)
            fixes.Add($"alternatively, conclusion cards occupy {Js.Num(cardsPx)}px below the diagram; shortening card copy or dropping a card row so cards take at most {Js.Num(cardsPx - excess)}px also fits");
        return fixes;
    }
}
