using System.Text.Json.Nodes;

namespace Archify.Net;

// compileWorkflow's result (workflow-compiler.mjs:663-669, :5618). `Receipt` carries every receipt key but `diagnostics`
// (a C# Diagnostic has no JSON form); `Diagnostics` is that key.
internal sealed record WorkflowCompileResult(bool Ok, string? Svg, JsonObject Receipt, string? Error, IReadOnlyList<Diagnostic> Diagnostics);

// compileWorkflowInternal's options bag (:1362-1369) plus the per-top-level-compile width table (R8).
internal sealed record WorkflowCompileOptions(WorkflowDoc Workflow, string? QualityProfile, JsonObject? SourceEvidence, WorkflowWidths Widths,
    bool DiscoverFixes = true, JsonObject? LayoutFeedback = null, JsMap<string, double>? WidthOverrides = null);

// workflow-compiler.mjs cluster O: the public entry (:5917-5920) and the feedback wrappers around compileWorkflowInternal.
internal static class WorkflowCompiler
{
    // compileWorkflow (:5917). The crossing/placement repair wrappers (:5850-5915) are v2-only and land with Task 8.
    public static WorkflowCompileResult Compile(RenderContext ctx, WorkflowDoc workflow, string? qualityProfile, JsonObject? sourceEvidence)
    {
        var options = new WorkflowCompileOptions(workflow, qualityProfile, sourceEvidence, new WorkflowWidths());
        return CompileWorkflowWithFeedback(ctx, options);
    }

    // compileWorkflowWithFeedback (:5802-5848) → compileWithLayoutFeedback (:5744-5800) → compileWithRouteOrderFeedback (:5715-5742).
    // Every wrapper is a pass-through for schema v1 (route-order retries, WorkflowLayoutFeedback and the sublabel sizing loop
    // are readable-v2 only), so a v1 document compiles exactly once here; the v2 loops are Task 8.
    public static WorkflowCompileResult CompileWorkflowWithFeedback(RenderContext ctx, WorkflowCompileOptions options)
    {
        if (options.Workflow.SchemaVersion == 2)
            throw new NotImplementedException("Task 8: compileWithRouteOrderFeedback / compileWithLayoutFeedback / sizing loop (workflow-compiler.mjs:5715-5848)");
        return WorkflowCompilation.CompileWorkflowInternal(ctx, options);
    }

    // compilerFailure (:663-671)
    public static WorkflowCompileResult CompilerFailure(string contract, IReadOnlyList<Diagnostic> diagnostics, string? error = null) =>
        new(false, null, new JsonObject { ["contract"] = contract }, error ?? string.Join("\n", diagnostics.Select(d => d.Message)), diagnostics);
}
