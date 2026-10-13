namespace Archify.Net;

// desktop-readability.mjs.
public sealed record Viewport(double Width, double Height);
public sealed record VerticalChrome(double Body, double Header, double Diagram);

public sealed record DeclaredWideBudget(double RequestedTargetPx, double RequestedScale, double DesiredReaderWidth, double ViewportCap,
    double MaximumReaderWidth, double ActualReaderWidth, double GuaranteedSvgWidth, double ProjectedMinimumTextPx, double HardFloorPx,
    bool HardFloorMet, bool RequestedTargetMet, string Limit);

// Carries the viewBox it was computed from: describeFixedWidthOverflow reads `issue.viewBoxWidth/Height`, which the
// check script spreads onto the issue upstream.
public sealed record FixedWidthOverflow(double ViewBoxWidth, double ViewBoxHeight, double ViewportWidth, double ViewportHeight, double Ratio,
    double WideRatio, double SvgWidthPx, double SvgHeightPx, double FixedChromePx, double PageHeightPx, double OverflowPx);

public static class DesktopReadability
{
    public static readonly Viewport DESKTOP_READABILITY_VIEWPORT = new(1440, 900);
    public const double DESKTOP_READER_MIN_WIDTH = 960;
    public const double DESKTOP_READER_HORIZONTAL_CHROME = 30;
    public const double DESKTOP_READER_DIAGRAM_WIDTH = DESKTOP_READER_MIN_WIDTH - DESKTOP_READER_HORIZONTAL_CHROME;
    public const double MIN_PROJECTED_NODE_TEXT_PX = 6;
    public const string DECLARED_WIDE_READER_CONTRACT = "declared-wide-v1";
    public const double DECLARED_WIDE_READER_RATIO = 1.55;
    public const double DECLARED_WIDE_READER_MAX_WIDTH = 1920;
    public const double DECLARED_WIDE_REFERENCE_BODY_HORIZONTAL_PX = 64;
    public const double DECLARED_WIDE_REFERENCE_DIAGRAM_HORIZONTAL_PX = 30;

    public static double ProjectedNodeTextPx(double sourceFontPx, double viewBoxWidth, double diagramWidth = DESKTOP_READER_DIAGRAM_WIDTH)
    {
        if (!(double.IsFinite(sourceFontPx) && double.IsFinite(viewBoxWidth) && double.IsFinite(diagramWidth)) || viewBoxWidth <= 0 || diagramWidth <= 0)
            return double.NaN;
        return sourceFontPx * Math.Min(1, diagramWidth / viewBoxWidth);
    }

    public static double MinimumReadableSourceTextPx(double viewBoxWidth, double diagramWidth = DESKTOP_READER_DIAGRAM_WIDTH,
        double minimumProjectedPx = MIN_PROJECTED_NODE_TEXT_PX)
    {
        if (!(double.IsFinite(viewBoxWidth) && double.IsFinite(diagramWidth) && double.IsFinite(minimumProjectedPx))
            || viewBoxWidth <= 0
            || diagramWidth <= 0
            || minimumProjectedPx <= 0)
            return double.NaN;
        return minimumProjectedPx / Math.Min(1, diagramWidth / viewBoxWidth);
    }

    // This is deliberately separate from the legacy 930px projection. Architecture
    // boundary convergence depends on that legacy default, while only a recognized
    // v2 wide Reader may use this declared-width proof.
    public static DeclaredWideBudget? DeclaredWideReadabilityBudget(double viewBoxWidth, double viewBoxHeight, double minimumSourceTextPx,
        double requestedMinimumTextPx, double? viewportWidth = null, double bodyHorizontalPx = DECLARED_WIDE_REFERENCE_BODY_HORIZONTAL_PX,
        double diagramHorizontalPx = DECLARED_WIDE_REFERENCE_DIAGRAM_HORIZONTAL_PX, double minimumReaderWidth = DESKTOP_READER_MIN_WIDTH,
        double maximumReaderWidth = DECLARED_WIDE_READER_MAX_WIDTH)
    {
        var viewport = viewportWidth ?? DESKTOP_READABILITY_VIEWPORT.Width;
        double[] values =
        [
            viewBoxWidth, viewBoxHeight, minimumSourceTextPx, requestedMinimumTextPx,
            viewport, bodyHorizontalPx, diagramHorizontalPx, minimumReaderWidth, maximumReaderWidth,
        ];
        if (!values.All(double.IsFinite) || viewBoxWidth <= 0 || viewBoxHeight <= 0
            || minimumSourceTextPx <= 0 || requestedMinimumTextPx <= 0 || viewport <= 0
            || bodyHorizontalPx < 0 || diagramHorizontalPx < 0 || minimumReaderWidth <= 0
            || maximumReaderWidth < minimumReaderWidth || viewBoxWidth / viewBoxHeight < DECLARED_WIDE_READER_RATIO)
            return null;
        var requestedTargetPx = Math.Max(MIN_PROJECTED_NODE_TEXT_PX, requestedMinimumTextPx);
        var requestedScale = Math.Min(1, requestedTargetPx / minimumSourceTextPx);
        var desiredReaderWidth = Math.Max(minimumReaderWidth, viewBoxWidth * requestedScale + diagramHorizontalPx);
        var viewportCap = Math.Max(0, viewport - bodyHorizontalPx);
        var cap = Math.Min(maximumReaderWidth, viewportCap);
        var actualReaderWidth = Math.Min(desiredReaderWidth, cap);
        var guaranteedSvgWidth = Math.Max(0, actualReaderWidth - diagramHorizontalPx);
        var projectedMinimumTextPx = ProjectedNodeTextPx(minimumSourceTextPx, viewBoxWidth, guaranteedSvgWidth);
        var limit = actualReaderWidth < desiredReaderWidth
            ? (viewportCap <= maximumReaderWidth ? "viewport-cap" : "reader-cap")
            : "source-size";
        return new(requestedTargetPx, requestedScale, desiredReaderWidth, viewportCap, maximumReaderWidth, actualReaderWidth, guaranteedSvgWidth,
            projectedMinimumTextPx, MIN_PROJECTED_NODE_TEXT_PX,
            projectedMinimumTextPx >= MIN_PROJECTED_NODE_TEXT_PX,
            projectedMinimumTextPx >= requestedMinimumTextPx,
            limit);
    }

    // Vertical chrome that always stacks with the SVG at the 1440x900 desktop
    // viewport, measured from the delivered Viewer with the shortest one-line
    // header and no cards: body padding 12, header 39, diagram padding/border 75.
    // Cards are excluded so the prediction stays a lower bound.
    public static readonly VerticalChrome DESKTOP_FIXED_VERTICAL_CHROME_PX = new(12, 39, 75);

    // A canvas the Reader can neither narrow (viewBox ratio below the wide
    // threshold) nor scroll readably (no automatic Reader fit) renders at the full
    // reader width, so its page height is a function of the viewBox alone. Returns
    // null when the Reader has a way to fit the page; otherwise the certain
    // overflow at 1440x900 before any cards are counted.
    public static FixedWidthOverflow? PredictedFixedWidthOverflow(double viewBoxWidth, double viewBoxHeight, string? readerFit = null, string? diagramType = null,
        Viewport? viewport = null, double bodyHorizontalPx = DECLARED_WIDE_REFERENCE_BODY_HORIZONTAL_PX,
        double diagramHorizontalPx = DECLARED_WIDE_REFERENCE_DIAGRAM_HORIZONTAL_PX, VerticalChrome? chrome = null)
    {
        viewport ??= DESKTOP_READABILITY_VIEWPORT;
        chrome ??= DESKTOP_FIXED_VERTICAL_CHROME_PX;
        if (!(double.IsFinite(viewBoxWidth) && double.IsFinite(viewBoxHeight)) || viewBoxWidth <= 0 || viewBoxHeight <= 0) return null;
        var ratio = viewBoxWidth / viewBoxHeight;
        if (readerFit == "intrinsic-height" || readerFit == "width-first"
            || (readerFit == "authored-height" && diagramType == "architecture")
            || ratio >= DECLARED_WIDE_READER_RATIO) return null;
        var svgWidthPx = viewport.Width - bodyHorizontalPx - diagramHorizontalPx;
        var svgHeightPx = Js.Round(svgWidthPx * viewBoxHeight / viewBoxWidth);
        var fixedChromePx = chrome.Body + chrome.Header + chrome.Diagram;
        var pageHeightPx = svgHeightPx + fixedChromePx;
        if (pageHeightPx <= viewport.Height) return null;
        return new(viewBoxWidth, viewBoxHeight, viewport.Width, viewport.Height, Js.Round(ratio * 100) / 100, DECLARED_WIDE_READER_RATIO,
            svgWidthPx, svgHeightPx, fixedChromePx, pageHeightPx, pageHeightPx - viewport.Height);
    }

    public static string DescribeFixedWidthOverflow(FixedWidthOverflow issue)
    {
        var maximumViewBoxHeight = Math.Floor(issue.ViewBoxWidth / issue.WideRatio);
        var wideViewBoxWidth = Math.Ceiling(issue.ViewBoxHeight * issue.WideRatio);
        return $"Preserve every node, relationship, and label. This {Js.Num(issue.ViewBoxWidth)}x{Js.Num(issue.ViewBoxHeight)} canvas (ratio {Js.Num(issue.Ratio)}) declares no intrinsic-height fit and is below the {Js.Num(issue.WideRatio)} wide ratio, so the desktop Reader can neither narrow it nor accept vertical scroll: it renders {Js.Num(issue.SvgHeightPx)}px tall at the full {Js.Num(issue.SvgWidthPx)}px width and the page reaches {Js.Num(issue.PageHeightPx)}px before cards against {Js.Num(issue.ViewportHeight)}px, a certain visual-check failure. Either compact vertical spacing so meta.viewBox height is at most {Js.Num(maximumViewBoxHeight)} at this width, or spread content sideways so the width is at least {Js.Num(wideViewBoxWidth)} at this height; for architecture and readable-v2 workflow, omitting meta.viewBox lets the renderer size the canvas and declare the fit.";
    }
}
