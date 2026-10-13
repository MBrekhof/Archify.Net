namespace Archify.Net;

// labels.mjs: compatibility re-exports of the shared automatic label placer for the architecture renderer.
public static class Labels
{
    public static List<LabelRect> PlaceAutomaticLabels(IReadOnlyList<LabelRect> labels, IReadOnlyList<LabelRoute> routes, IReadOnlyList<Box> components,
        IReadOnlyList<Rect> titles, IReadOnlyList<double> viewBox, double? placementBottom = null, bool fallbackRing = true,
        bool keepFallbackNearRoute = false, bool gridSweep = false, double maxOwnRouteDistance = double.PositiveInfinity) =>
        AutomaticLabels.PlaceAutomaticLabels(labels, routes, components, titles, viewBox, placementBottom, fallbackRing, keepFallbackNearRoute, gridSweep, maxOwnRouteDistance);

    public static LabelRect? ReservedLabelRect(LabelRect label, IReadOnlyList<Pt> points, IReadOnlyList<Box> components, IReadOnlyList<LabelRoute>? routes = null,
        IReadOnlyList<LabelRect>? labels = null, IReadOnlyList<double>? viewBox = null, double placementBottom = double.PositiveInfinity) =>
        AutomaticLabels.ReservedLabelRect(label, points, components, routes, labels, viewBox, placementBottom);
}
