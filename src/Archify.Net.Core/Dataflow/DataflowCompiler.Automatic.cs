using System.Text.Json.Nodes;

namespace Archify.Net;

// dataflow-compiler.mjs automatic-showcase clusters: B port rebalance, C auto node height, H route repair, I port reorder,
// M label/corridor co-repair, N wrap, O joint repair and the `automaticShowcase` branches of F/J/K. Every caller in
// DataflowCompiler.cs sits behind `automaticShowcase`, which no authored-viewBox input reaches. Task 5 fills these in.
internal sealed partial class DataflowCompiler
{
    sealed partial class Build
    {
        static NotImplementedException Task5() => new("Task 5");

        // :157-199 automaticSourceSides (alternate top/bottom exits); runs on the retry only.
        void RebalancePorts() => throw Task5();
        // :206-240 grows crowded nodes (autoHeightInset).
        void GrowCrowdedNodes() => throw Task5();
        // :593-603 automaticPortRhythmBridge inside routeVia; null falls through to the preset corridors.
        List<Pt>? AutomaticPortBridgeVia(Box from, Box to, Pt start, Pt end, string fromSide, string toSide) => throw Task5();
        // :624-628 vertical/vertical automatic route.
        List<Pt> AutomaticVerticalVia(Pt start, Pt end) => throw Task5();
        // :673-698 alignAutomaticFacingPorts inside pathFor.
        (Pt Start, Pt End) AlignAutomaticPorts(JsonObject flow, Box from, Box to, Pt start, Pt end, string fromSide, string toSide, SpreadPorts? ports) => throw Task5();
        // :765-926 compactCorridors / gridDetour / detourFor, the obstruction pass and the crossing pass.
        void RepairAutomaticRoutes() => throw Task5();
        // :941-1006 slot reorder by the first turn of the settled route.
        void ReorderAutomaticPorts() => throw Task5();
        // :1012-1022 the corridor anchor for an unclassified vertical two-point flow; null falls through to legacyLabelPoint.
        Pt? AutomaticLabelAnchor(JsonObject flow, List<Pt> points) => throw Task5();
        // :1074-1085 placeAutomaticLabels over the initial rects.
        List<LabelRect> PlaceInitialLabels(List<LabelRect> labels) => throw Task5();
        // :1159-1330 blockedLabelSlots, corridor shift, port-bridge alignment, crossover set.
        void RepairLabelsAndCorridors() => throw Task5();
        // :1335-1345 wrapBlockedFlowLabels.
        void WrapBlockedLabels() => throw Task5();
        // :1359-1380 repairLabelRoutes, height refit, crossover recompute.
        void RepairJointLabelRoutes() => throw Task5();
    }
}
