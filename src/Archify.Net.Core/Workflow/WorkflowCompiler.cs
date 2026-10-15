using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// compileWorkflow's result (workflow-compiler.mjs:663-669, :5618). `Receipt` carries every receipt key but `diagnostics`
// (a C# Diagnostic has no JSON form); `Diagnostics` is that key.
internal sealed record WorkflowCompileResult(bool Ok, string? Svg, JsonObject Receipt, string? Error, IReadOnlyList<Diagnostic> Diagnostics);

// compileWorkflowInternal's options bag (:1362-1369) plus the per-top-level-compile width table (R8).
internal sealed record WorkflowCompileOptions(WorkflowDoc Workflow, string? QualityProfile, JsonObject? SourceEvidence, WorkflowWidths Widths,
    bool DiscoverFixes = true, JsonObject? LayoutFeedback = null, JsMap<string, double>? WidthOverrides = null);

// workflow-compiler.mjs cluster O: the public entry (:5917-5920) and the feedback wrappers around compileWorkflowInternal (:5646-5848).
internal static class WorkflowCompiler
{
    // compileWorkflow (:5917-5920). The crossing/placement repair wrappers only act on route-order failures; every successful
    // compile and every other failure passes through them unchanged.
    public static WorkflowCompileResult Compile(RenderContext ctx, WorkflowDoc workflow, string? qualityProfile, JsonObject? sourceEvidence)
    {
        var options = new WorkflowCompileOptions(workflow, qualityProfile, sourceEvidence, new WorkflowWidths());
        return AddVerifiedPlacementRepair(ctx, AddVerifiedCrossingRepair(ctx, CompileWorkflowWithFeedback(ctx, options), options), options);
    }

    // Upstream mutates `diagnostic.evidence`/`.supportedFixes` in place; compilerFailure's receipt shares the array (:663-669) and
    // the port derives the receipt's diagnostics from `Diagnostics`, so replacing the one element there updates both.
    static WorkflowCompileResult WithRepair(WorkflowCompileResult result, Diagnostic diagnostic, JsonObject verifiedRepair, string fix)
    {
        var repaired = diagnostic with
        {
            Evidence = Spread(diagnostic.Evidence, ("verifiedRepair", verifiedRepair)),
            SupportedFixes = [fix, .. diagnostic.SupportedFixes],
        };
        List<Diagnostic> diagnostics = [.. result.Diagnostics];
        diagnostics[diagnostics.FindIndex(candidate => ReferenceEquals(candidate, diagnostic))] = repaired;
        return result with { Diagnostics = diagnostics };
    }

    // addVerifiedCrossingRepair (:5850-5886)
    static WorkflowCompileResult AddVerifiedCrossingRepair(RenderContext ctx, WorkflowCompileResult result, WorkflowCompileOptions options)
    {
        var workflow = options.Workflow;
        if (result.Ok || workflow.SchemaVersion != 2 || WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(workflow)) return result;
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity != Severity.Warning).ToList();
        if (errors.Count == 0 || errors.Any(diagnostic => diagnostic.Code != "composition/proper-crossing")) return result;
        var diagnostic = errors[0];
        var indexes = new List<int>();
        foreach (var value in (JsonNode?[])[diagnostic.Subject["index"], diagnostic.Evidence["otherRelationship"]?["index"]])
        {
            if (IntegerAt(value) is not { } index || index < 0 || index >= workflow.Edges.Count) return result;
            indexes.Add((int)index);
        }
        var groups = indexes.Select(index => ((string[])["fromSide", "toSide", "route"])
            .Where(field => workflow.Edges[index][field].Truthy() && !StrictEquals(workflow.Edges[index][field], "auto"))
            .Select(field => (Index: index, Field: field)).ToList()).ToList();
        // Try each complete set of optional side/preset constraints, then the pair.
        // At most three full compiler probes; do not search every field combination.
        var seen = new JsSet<string>();
        foreach (var removals in (List<(int Index, string Field)>[])[.. groups, [.. groups.SelectMany(group => group)]])
        {
            var key = string.Join(",", removals.Select(removal => $"{removal.Index}/{removal.Field}"));   // JSON.stringify(removals)
            if (removals.Count == 0 || seen.Has(key)) continue;
            seen.Add(key);
            var candidate = workflow.Clone();
            candidate.AbsoluteGeometryPolicy = workflow.AbsoluteGeometryPolicy;   // the own Symbols (:5870)
            foreach (var (index, field) in removals) candidate.Edges[index].Remove(field);
            var verified = ctx.Diagnostics.WithRecordingSuppressed(() => CompileWorkflowWithFeedback(ctx, options with { Workflow = candidate, DiscoverFixes = false }));
            if (!verified.Ok) continue;
            var paths = removals.Select(removal => $"/edges/{removal.Index}/{removal.Field}").ToList();
            var changes = new JsonArray([.. paths.Select(path => (JsonNode)new JsonObject { ["op"] = "remove", ["path"] = path })]);
            var fix = $"if these endpoint sides and route presets are optional, remove {string.Join(", ", paths)} together; this complete workflow passes compilation with those edits (browser verification still required)";
            return WithRepair(result, diagnostic, new JsonObject { ["scope"] = "complete-workflow-compile", ["changes"] = changes }, fix);
        }
        return result;
    }

    // addVerifiedPlacementRepair (:5888-5915)
    static WorkflowCompileResult AddVerifiedPlacementRepair(RenderContext ctx, WorkflowCompileResult result, WorkflowCompileOptions options)
    {
        var workflow = options.Workflow;
        var profile = !string.IsNullOrEmpty(options.QualityProfile) ? options.QualityProfile : workflow.Meta.Str("quality_profile");
        if (result.Ok || workflow.SchemaVersion != 2 || profile != "showcase" || WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(workflow)) return result;
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity != Severity.Warning).ToList();
        if (errors.Count == 0 || errors.Any(diagnostic => !RouteOrderCodes.Contains(diagnostic.Code) || diagnostic.Evidence["verifiedRepair"].Truthy())) return result;
        var proposal = WorkflowPlacementRepair.ProposeWorkflowPlacement(workflow);
        if (!proposal.Eligible || proposal.Changes!.Count == 0) return result;
        var verified = ctx.Diagnostics.WithRecordingSuppressed(() => CompileWorkflowWithFeedback(ctx, options with { Workflow = proposal.Proposed!, DiscoverFixes = false }));
        if (!verified.Ok) return result;
        var changes = new JsonArray([.. proposal.Changes.Select(change => (JsonNode)new JsonObject { ["op"] = change.Op, ["path"] = change.Path, ["value"] = change.Value })]);
        return WithRepair(result, errors[0], new JsonObject { ["scope"] = "complete-workflow-compile", ["kind"] = "optional-node-placement", ["changes"] = changes },
            "if node columns and row offsets are flexible, apply the complete optional-node-placement changes in evidence.verifiedRepair together; this workflow then passes compilation with every node, relationship, lane, condition and mainPath preserved (browser verification and visual review still required)");
    }

    // compilerFailure (:663-671)
    public static WorkflowCompileResult CompilerFailure(string contract, IReadOnlyList<Diagnostic> diagnostics, string? error = null) =>
        new(false, null, new JsonObject { ["contract"] = contract }, error ?? string.Join("\n", diagnostics.Select(d => d.Message)), diagnostics);

    // feedbackFailure (:5646-5665)
    static WorkflowCompileResult FeedbackFailure(JsonObject request)
    {
        var name = request["edge"].Truthy() ? Js.PrimitiveString(request["edge"]) : $"{Tpl(request, "from")}->{Tpl(request, "to")}";
        var message = $"Workflow edge \"{name}\" exhausted bounded readable-v2 layout feedback without a feasible automatic route.";
        Diagnostic[] diagnostics = [new("workflow/solver-budget-exhausted", Severity.Error, message,
            new JsonObject
            {
                ["diagramType"] = "workflow", ["edge"] = request["edge"]?.DeepClone(), ["from"] = request["from"]?.DeepClone(), ["to"] = request["to"]?.DeepClone(),
            },
            new JsonObject
            {
                ["attemptedCandidateFamilies"] = request["attemptedCandidateFamilies"]?.DeepClone(), ["candidateCount"] = request["candidateCount"]?.DeepClone(),
            },
            [], null)];
        return CompilerFailure("readable-v2", diagnostics, message);
    }

    static readonly string[] RouteOrderCodes = ["composition/ambiguous-corridor", "composition/proper-crossing"];
    const int MaxRouteOrderRetries = 3;

    // Automatic routes are planned in canonical order, each against those already
    // placed, so an earlier edge can take the only clear corridor of a later one
    // (a long cross-lane branch dropping between two same-lane neighbours). When
    // the readable result fails only because two automatic edges share a corridor
    // or cross, plan the later edge first and keep the retry only if it removes
    // errors; the document itself is never changed.
    static int ErrorCount(WorkflowCompileResult result) => result.Ok ? 0 : result.Diagnostics.Count(diagnostic => diagnostic.Severity != Severity.Warning);

    static double? IntegerAt(JsonNode? value) => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && IsInteger(v.D()) ? v.D() : null;

    static double? RouteOrderRetryIndex(WorkflowCompileResult result, JsonObject layoutFeedback, WorkflowDoc workflow)
    {
        if (result.Ok) return null;
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity != Severity.Warning).ToList();
        if (errors.Count == 0 || !errors.All(diagnostic => RouteOrderCodes.Contains(diagnostic.Code))) return null;
        var tried = new JsSet<double>();
        foreach (var index in layoutFeedback["routeFirst"].AsArrayOrEmpty()) if (index is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number) tried.Add(v.D());
        foreach (var diagnostic in errors)
        {
            var indexes = new List<double>();
            foreach (var value in (JsonNode?[])[diagnostic.Subject["index"], diagnostic.Evidence["otherRelationship"]?["index"]])
                if (IntegerAt(value) is { } index) indexes.Add(index);
            if (indexes.Count != 2) continue;
            // Diagnostic indexes identify the authored array, not routing priority.
            Js.Sort(indexes, (a, b) => CompareWorkflowEdges(workflow.Edges[(int)a], workflow.Edges[(int)b]));
            var later = indexes[^1];
            if (!tried.Has(later)) return later;
        }
        return null;
    }

    static bool CanRepairAutomaticSublabels(WorkflowCompileResult result, WorkflowCompileOptions options)
    {
        var workflow = options.Workflow;
        var profile = !string.IsNullOrEmpty(options.QualityProfile) ? options.QualityProfile : workflow.Meta.Str("quality_profile");
        if (workflow.SchemaVersion != 2 || profile != "showcase" || WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(workflow)
            || result.Receipt.Str("contract") != "readable-v2" || result.Receipt["nodes"] is not JsonArray receiptNodes) return false;
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity != Severity.Warning).ToList();
        return errors.Count > 0 && errors.All(diagnostic =>
        {
            if (diagnostic.Code != "workflow/sublabel-readability") return false;
            var subjectNode = diagnostic.Subject["node"];
            var node = workflow.Nodes.FirstOrDefault(candidate => StrictEquals(candidate["id"], subjectNode));
            var measured = receiptNodes.OfType<JsonObject>().FirstOrDefault(candidate => StrictEquals(candidate["id"], subjectNode));
            if (node is null || !node["sublabel"].Truthy() || node.Num("width") is { } authored && double.IsFinite(authored) || measured is null) return false;
            var minimum = diagnostic.Evidence.Num("requiredFontPx") ?? double.NaN;
            if (!double.IsFinite(minimum) || minimum > Math.Min(8, diagnostic.Evidence.Num("maximumSlotFontPx") ?? double.NaN)) return false;
            var measuredWidth = measured.Num("width") ?? double.NaN;
            var padding = measuredWidth - TextFit.AvailableNodeTextWidth(measuredWidth);
            var width = Math.Ceiling((TextFit.MinimumNodeTextWidth(node.Str("sublabel"), minimum) + padding) / 4) * 4;
            return width > measuredWidth && width <= 200;
        });
    }

    // `{ ...layoutFeedback, ...extra }`: a fresh object (a JsonNode has one parent), later keys win in place.
    static JsonObject Spread(JsonObject source, params (string Key, JsonNode? Value)[] extra)
    {
        var copy = (JsonObject)source.DeepClone();
        foreach (var (key, value) in extra) copy[key] = value;
        return copy;
    }

    // compileWithRouteOrderFeedback (:5715-5742)
    static WorkflowCompileResult CompileWithRouteOrderFeedback(RenderContext ctx, WorkflowCompileOptions options)
    {
        var first = WorkflowCompilation.CompileWorkflowInternal(ctx, options);
        if (options.Workflow.SchemaVersion != 2) return first;
        var best = first;
        var current = first;
        var layoutFeedback = options.LayoutFeedback ?? [];
        for (var retry = 0; retry < MaxRouteOrderRetries && ErrorCount(best) > 0; retry++)
        {
            if (RouteOrderRetryIndex(current, layoutFeedback, options.Workflow) is not { } index) break;
            var routeFirst = new JsonArray([.. layoutFeedback["routeFirst"].AsArrayOrEmpty().Select(value => value?.DeepClone()), index]);
            layoutFeedback = Spread(layoutFeedback, ("fanInLabelClearance", true), ("routeFirst", routeFirst));
            try
            {
                current = WorkflowCompilation.CompileWorkflowInternal(ctx, options with { LayoutFeedback = layoutFeedback });
            }
            catch (WorkflowLayoutFeedback)
            {
                break;
            }
            // Removing a crossing may expose several text-width errors on a wider
            // canvas. Let the existing bounded sizing loop finish that automatic plan;
            // counting errors alone would discard it before sizing could run.
            if (ErrorCount(current) < ErrorCount(best) || CanRepairAutomaticSublabels(current, options)) best = current;
        }
        return best;
    }

    // compileWithLayoutFeedback (:5744-5800)
    static WorkflowCompileResult CompileWithLayoutFeedback(RenderContext ctx, WorkflowCompileOptions options)
    {
        var layoutFeedback = new JsonObject();
        for (var attempt = 0; attempt <= MaxReadableLayoutFeedbackRounds; attempt++)
        {
            try
            {
                return CompileWithRouteOrderFeedback(ctx, options with { LayoutFeedback = layoutFeedback });
            }
            catch (WorkflowLayoutFeedback error)
            {
                var request = error.Request;
                JsonObject? nextFeedback = null;
                var name = request["edge"].Truthy() ? Js.PrimitiveString(request["edge"]) : $"{Tpl(request, "from")}->{Tpl(request, "to")}";
                var minimum = request.Num("minimum");
                if (error.Kind == "rank-gap-minimum" && IntegerAt(request["fromCol"]) is { } fromCol && IntegerAt(request["toCol"]) is { } toCol
                    && minimum is { } rankMinimum && double.IsFinite(rankMinimum))
                {
                    var key = $"{Js.Num(fromCol)}:{Js.Num(toCol)}";
                    var current = layoutFeedback.Obj("rankGapMinimums")?.Num(key) ?? double.NegativeInfinity;
                    if (rankMinimum > current + 0.0001)
                    {
                        var minimums = (JsonObject?)layoutFeedback.Obj("rankGapMinimums")?.DeepClone() ?? [];
                        minimums[key] = rankMinimum;
                        var contributors = (JsonObject?)layoutFeedback.Obj("rankGapContributors")?.DeepClone() ?? [];
                        contributors[key] = new JsonArray($"rank {Js.Num(fromCol)}→{Js.Num(toCol)} route clearance", $"edge {name} route");
                        nextFeedback = Spread(layoutFeedback, ("rankGapMinimums", minimums), ("rankGapContributors", contributors));
                    }
                }
                else if (error.Kind == "lane-gap-minimum" && minimum is { } laneMinimum && double.IsFinite(laneMinimum)
                    && laneMinimum > (layoutFeedback.Num("laneGapMin") ?? double.NegativeInfinity) + 0.0001)
                {
                    nextFeedback = Spread(layoutFeedback, ("laneGapMin", laneMinimum),
                        ("laneGapContributors", new JsonArray($"edge {name} lane-gap route clearance")));
                }
                if (nextFeedback is null || attempt == MaxReadableLayoutFeedbackRounds) return FeedbackFailure(request);
                layoutFeedback = nextFeedback;
            }
        }
        throw new InvalidOperationException("unreachable readable-v2 layout feedback state");
    }

    // compileWorkflowWithFeedback (:5802-5848). Its options bag drops layoutFeedback and widthOverrides: every call starts fresh.
    public static WorkflowCompileResult CompileWorkflowWithFeedback(RenderContext ctx, WorkflowCompileOptions options)
    {
        options = options with { LayoutFeedback = null, WidthOverrides = null };
        var workflow = options.Workflow;
        var result = CompileWithLayoutFeedback(ctx, options);
        // Keep successful output and authored geometry exactly as before. Check the
        // original migration policy before cloning, since its Symbol is not JSON.
        var profile = !string.IsNullOrEmpty(options.QualityProfile) ? options.QualityProfile : workflow.Meta.Str("quality_profile");
        if (result.Ok || workflow.SchemaVersion != 2 || profile != "showcase" || WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(workflow)) return result;

        WorkflowDoc? repairWorkflow = null;
        var widthOverrides = new JsMap<string, double>();
        double minimumTick = 60;
        // Each of the 6.1..8.0 minimum-font ticks can trigger at most one batch.
        // Widths are fixed throughout a route/rank solver epoch; every new epoch
        // starts with empty geometry feedback and measures its own final canvas.
        // Only a complete measured receipt carries nodes; early diagnostic-only
        // failures must never start sizing feedback.
        while (!result.Ok && result.Receipt.Str("contract") == "readable-v2" && result.Receipt["nodes"] is JsonArray receiptNodes)
        {
            var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity != Severity.Warning).ToList();
            if (errors.Count == 0 || errors.Any(diagnostic => diagnostic.Code != "workflow/sublabel-readability")) break;
            var minimum = Js.MaxOf(errors.Select(diagnostic => diagnostic.Evidence.Num("requiredFontPx") ?? double.NaN), double.NegativeInfinity);
            var maximum = errors.Select(diagnostic => diagnostic.Evidence.Num("maximumSlotFontPx") ?? double.NaN).Aggregate(double.PositiveInfinity, Math.Min);
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum) break;
            var tick = Js.Round(minimum * 10);
            if (tick <= minimumTick || tick > 80) break;
            minimumTick = tick;

            var measuredWidths = new JsMap<string, double>();
            foreach (var measured in receiptNodes.OfType<JsonObject>()) measuredWidths.Set(IdKey(measured["id"]), measured.Num("width") ?? double.NaN);
            var changed = false;
            foreach (var node in workflow.Nodes)
            {
                if (node.Num("width") is { } authored && double.IsFinite(authored) || !node["sublabel"].Truthy()) continue;
                var currentWidth = measuredWidths.Has(IdKey(node["id"])) ? measuredWidths.Get(IdKey(node["id"])) : double.NaN;
                var needed = TextFit.MinimumNodeTextWidth(node.Str("sublabel"), minimum);
                if (needed <= TextFit.AvailableNodeTextWidth(currentWidth)) continue;
                var padding = currentWidth - TextFit.AvailableNodeTextWidth(currentWidth);
                var width = Math.Min(200, Math.Ceiling((needed + padding) / 4) * 4);
                if (width <= currentWidth) continue;
                widthOverrides.Set(IdKey(node["id"]), width);
                changed = true;
            }
            if (!changed) break;
            repairWorkflow ??= workflow.Clone();
            result = CompileWithLayoutFeedback(ctx, options with { Workflow = repairWorkflow, WidthOverrides = widthOverrides });
        }
        return result;
    }
}
