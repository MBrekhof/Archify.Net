namespace Archify.Net.Cli.Commands;

static class ExamplesCommand
{
    // render-examples.mjs TARGETS (:12-28): type, embedded example, output name.
    static readonly (DiagramType Type, string Input, string Output)[] Targets =
    [
        (DiagramType.Workflow, "agent-tool-call.workflow.json", "workflow-agent-tool-call-rendered.html"),
        (DiagramType.Sequence, "cache-miss-request.sequence.json", "sequence-cache-miss-request.html"),
        (DiagramType.Dataflow, "product-analytics.dataflow.json", "dataflow-product-analytics.html"),
        (DiagramType.Lifecycle, "agent-run.lifecycle.json", "lifecycle-agent-run.html"),
        (DiagramType.Architecture, "web-app.architecture.json", "web-app-rendered.html"),
        (DiagramType.Erd, "orders.erd.json", "erd-orders-rendered.html"),
        (DiagramType.Erd, "subscription-billing.erd.json", "subscription-billing-rendered.html"),
        (DiagramType.Tree, "payment-platform.tree.json", "tree-payment-platform-rendered.html"),
        (DiagramType.Tree, "archify-repository.tree.json", "tree-archify-repository-rendered.html"),
        (DiagramType.Class, "payments.class.json", "class-payments-rendered.html"),
        (DiagramType.Class, "payment-processors.class.json", "class-payment-processors-rendered.html"),
        (DiagramType.Timeline, "payment-incident.timeline.json", "timeline-payment-incident-rendered.html"),
        (DiagramType.Timeline, "archify-dev-activity.timeline.json", "timeline-archify-dev-activity-rendered.html"),
        (DiagramType.Waterfall, "checkout-request.waterfall.json", "waterfall-checkout-request-rendered.html"),
        (DiagramType.Waterfall, "example-rebuild.waterfall.json", "waterfall-example-rebuild-rendered.html"),
    ];

    // commandExamples (A:5865-5871). Q3: the outputs go to <cwd>/examples/, not into the installed tool. Upstream's script stops
    // at the first failing renderer (execFileSync throws), so this does too.
    public static int Run(string[] args, CliIo io)
    {
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal)) is { } unknown)
            throw new CliFailException($"Unknown examples option \"{unknown}\".");
        if (args.Length > 0) throw new CliFailException(Cli.Usage());

        foreach (var (type, input, output) in Targets)
        {
            var status = RenderCommand.RenderToFile(io, type, () => DiagramLoader.Parse(Resources.Example(input)), null,
                Path.Join(io.Cwd, "examples", output), new RenderOptions());
            if (status != 0) return status;
        }
        return 0;
    }
}
