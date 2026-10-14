using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// render-waterfall.mjs. The module-level flow (line 29 to the final writeDiagram call) becomes one Scene built per render;
// `--layout-json` becomes the LayoutReport on the result and writeDiagram's HTML is returned by the facade.
public sealed class WaterfallRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        var svg = scene.RenderSvg();
        return (svg, ctx.Diagram.Arr("cards"), new LayoutReport(scene.BuildLayoutReport()));
    }

    static class Layout
    {
        public const double Margin = 28;
        public const double Top = 52;
        public const double AxisH = 36;
        public const double RowH = 25;
        public const double BarH = 14;
        public const double Indent = 18;
        public const double NameFont = 12.5;
        public const double DurationFont = 11;
        public const double ColumnGap = 28;
        public const double MinTickPx = 80;
    }

    const double ADVANCE = NodeTextFit.WidthFactor;
    const double NAME_DOT = 14;
    const double LABEL_GAP = 7;
    static readonly double[] NICE = [1, 2, 2.5, 5];
    static readonly string[] SERVICE_TONES = ["frontend", "backend", "database", "cloud", "messagebus"];
    static readonly string[] SOLID_TONES = ["frontend", "backend", "database", "cloud", "messagebus", "security", "external"];
    static readonly string[] LEGEND_STATUSES = ["error", "cancelled", "incomplete"];

    // STATUS_TONE[status] (:156): undefined for ok/incomplete.
    static string? StatusTone(string status) => status switch { "error" => "security", "cancelled" => "external", _ => null };

    // number() (:131-138). Shared with the fixture test; the two toLocaleString shapes are Js.ToLocaleStringEnUs's.
    internal static string Number(double value)
    {
        if (value != 0 && Math.Abs(value) < 0.01)
        {
            return Math.Abs(value) < 1e-6 ? Js.Num(Js.ToNumber(Js.ToPrecision(value, 3)))
                : Js.ToLocaleStringEnUs(value, maximumSignificantDigits: 3);
        }
        var rounded = Math.Abs(value) > double.MaxValue / 100 ? value : Js.Round(value * 100) / 100;
        return Js.ToLocaleStringEnUs(rounded, maximumFractionDigits: 2);
    }

    // `{ ...span, index, status, open, end, duration }` (:118) plus depth (:126) and the placed fields of :198-207.
    sealed class Row
    {
        public required string Id, Name, Status;
        public string? Parent, Service, Detail;
        public int Index, Depth;
        public bool Open, ParentBar;
        public double Start, End, Duration, Y, X0, X1, Width, LabelWidth;
        public string Label = "", Placement = "", Tone = "";
    }

    sealed record Tick(double T, double X);

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject wf;
        readonly JsonObject meta;
        readonly string? locale;
        readonly string unit, unitLabel;
        readonly double axisWidth;
        readonly List<string> problems = [];
        readonly List<Diagnostic> details = [];
        readonly double t0, t1, wall;
        readonly List<Row> rows = [];
        readonly List<string> services;
        readonly JsMap<string, string?> serviceOf = new();
        readonly double labelW, axisX0, axisX1, scale, rowsTop;
        readonly List<Tick> ticks = [];
        readonly List<LegendEntry> legendEntries;
        readonly double contentBottom, width, height;

        static string N(double v) => Js.Num(v);

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            wf = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            unit = meta.Str("unit") is { Length: > 0 } u ? u : "ms";
            // The schema spells microseconds `us` for ASCII input; the canvas uses µs.
            unitLabel = unit == "us" ? "µs" : unit;
            axisWidth = wf.Obj("layout").Num("width") ?? 760;

            var raw = Geometry.AsArray(wf["spans"]).Select(node => (JsonObject)node!).ToList();
            ValidateTiming(raw);

            // ---- Timing model (:107-129) ----
            // ponytail: Math.min/max seeds; the schema's minItems makes the empty-list ±Infinity case unreachable.
            t0 = double.PositiveInfinity;
            foreach (var span in raw) t0 = Math.Min(t0, span.Num("start")!.Value);
            t1 = double.NegativeInfinity;
            foreach (var span in raw) t1 = Math.Max(t1, RecordedEnd(span) ?? span.Num("start")!.Value);
            wall = t1 - t0;

            var spans = raw.Select((span, index) =>
            {
                var start = span.Num("start")!.Value;
                var end = RecordedEnd(span);
                var open = end is null;
                // An incomplete span with no end is drawn to the last recorded instant of the trace and labelled as a lower
                // bound, never as a measured duration.
                return new Row
                {
                    Id = span.Str("id")!, Name = span.Str("name")!, Parent = span.Str("parent"), Service = span.Str("service"), Detail = span.Str("detail"),
                    Status = span.Str("status") is { Length: > 0 } status ? status : "ok", Index = index, Open = open, Start = start,
                    End = open ? t1 : end!.Value, Duration = (open ? t1 : end!.Value) - start,
                };
            }).ToList();
            var children = new JsMap<string, List<Row>>();
            foreach (var span in spans) children.Set(span.Id, []);
            foreach (var span in spans) if (span.Parent is not null) children.Get(span.Parent)!.Add(span);
            void Visit(List<Row> list, int depth)
            {
                var sorted = list.ToList();
                Js.Sort(sorted, ByStart);
                foreach (var span in sorted)
                {
                    span.Depth = depth;   // `{ ...span, depth }`: each span is visited once, so the copy is the span itself
                    rows.Add(span);
                    Visit(children.Get(span.Id)!, depth + 1);
                }
            }
            Visit(spans.Where(span => span.Parent is null).ToList(), 0);

            // ---- Tones (:146-157) ----
            var serviceSet = new JsSet<string>();
            foreach (var row in rows) if (row.Service is { Length: > 0 } service) serviceSet.Add(service);
            services = [.. serviceSet];
            foreach (var row in rows) serviceOf.Set(row.Id, row.Service ?? (row.Parent is not null ? serviceOf.Get(row.Parent) : null));

            // ---- Geometry (:159-208) ----
            labelW = Math.Ceiling(MaxOf(rows.Select(NameWidth).Prepend(140)));
            axisX0 = Layout.Margin + labelW + Layout.ColumnGap;
            axisX1 = axisX0 + axisWidth;
            scale = wall > 0 ? axisWidth / wall : 0;
            if (!double.IsFinite(scale))
            {
                Fail("waterfall/invalid-timing", "The recorded range cannot form a finite axis scale.",
                    new JsonObject { ["path"] = "/spans" }, new JsonObject { ["start"] = t0, ["end"] = t1, ["wall"] = wall },
                    ["Rescale the recorded timing and unit to a representable range."]);
                throw ctx.Diagnostics.ThrowDiagnosticProblems("Waterfall validation failed", problems, details: details);
            }
            rowsTop = Layout.Top + Layout.AxisH;

            var tickStep = TickStep();
            var firstTick = Math.Ceiling(t0 / tickStep) * tickStep;
            var tickCount = Math.Max(0, Math.Floor((t1 - firstTick) / tickStep + 1e-9) + 1);
            for (double index = 0; index < tickCount; index += 1)
            {
                var t = firstTick + index * tickStep;
                // Large timestamps can round adjacent ticks to the same double. Index-based iteration remains bounded even
                // when adding tickStep cannot advance t.
                if (ticks.Count == 0 || ticks[^1].T != t) ticks.Add(new Tick(t, XOf(t)));
            }

            var parentIds = new JsSet<string>();
            foreach (var row in rows) if (row.Parent is not null) parentIds.Add(row.Parent);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                row.Y = rowsTop + index * Layout.RowH;
                row.X0 = XOf(row.Start);
                row.X1 = XOf(row.End);
                row.Width = Math.Max(2, row.X1 - row.X0);
                row.Label = row.Open
                    ? ctx.Locale.Translate(locale, "waterfall.incomplete", new Dictionary<string, string> { ["duration"] = FormatDuration(row.Duration) })   // lookup only
                    : FormatDuration(row.Duration);
                row.LabelWidth = Utils.TextUnits(row.Label) * Layout.DurationFont * ADVANCE + 4;
                // Every duration reads at the same place, just after its bar.
                row.Placement = "after";
                row.Tone = ToneOf(row);
                row.ParentBar = parentIds.Has(row.Id);
            }

            // ---- Legend & canvas (:210-231) ----
            var presentStatuses = new JsSet<string>();
            foreach (var row in rows) presentStatuses.Add(row.Status);
            // Past five services the palette repeats. Services that share a colour share one entry.
            var byTone = new JsMap<string, List<string>>();
            foreach (var service in services)
            {
                var tone = ServiceTone(service);
                byTone.Set(tone, [.. byTone.Get(tone) ?? [], service]);
            }
            var serviceLegendEntries = byTone.Values
                .Select(owners => new LegendEntry($"service:{owners[0]}", string.Join(" · ", owners), Interactive: false)).ToList();
            legendEntries =
            [
                .. services.Count > 1 ? serviceLegendEntries : [],
                .. Legend.ResolveLegend(null,
                    LEGEND_STATUSES.Select(kind => new LegendEntry(kind, ctx.Locale.Translate(locale, $"legend.waterfall.{kind}"), Interactive: false)).ToList(),
                    presentStatuses),
            ];
            contentBottom = rowsTop + rows.Count * Layout.RowH + 8;
            var labelRoom = Math.Ceiling(MaxOf(rows.Select(row => row.X1 + LABEL_GAP + row.LabelWidth)) - axisX1);
            width = Math.Ceiling(axisX1 + Math.Max(24, labelRoom) + Layout.Margin);
            var footprint = Legend.LegendFootprint(legendEntries, width - Layout.Margin * 2, 11);
            height = Math.Ceiling(contentBottom + Layout.Margin + (legendEntries.Count > 0 ? 34 + footprint.ExtraHeight : 0));
        }

        // Math.max(...xs): -Infinity for none, NaN if any is NaN.
        static double MaxOf(IEnumerable<double> values)
        {
            var max = double.NegativeInfinity;
            foreach (var value in values)
            {
                if (double.IsNaN(value)) return double.NaN;
                max = Math.Max(max, value);
            }
            return max;
        }

        static double ByStart(Row a, Row b) => a.Start - b.Start is var d && d != 0 && !double.IsNaN(d) ? d : a.Index - b.Index;

        // recordedEnd (:108)
        static double? RecordedEnd(JsonObject span) =>
            span.Num("end") is { } end && double.IsFinite(end) ? end
            : span.Num("duration") is { } duration && double.IsFinite(duration) ? span.Num("start")!.Value + duration
            : null;

        // ---- Timing checks (:49-105) ----------------------------------------------------------
        // Bars are drawn from recorded timing only. Parents must exist and must not form a cycle.
        void Fail(string code, string message, JsonObject subject, JsonObject evidence, string[] supportedFixes)
        {
            problems.Add(message);
            var full = new JsonObject { ["diagramType"] = "waterfall" };
            foreach (var (key, value) in subject) full[key] = value?.DeepClone();
            details.Add(new Diagnostic(code, Severity.Error, message, full, evidence, supportedFixes, null));
        }

        void ValidateTiming(List<JsonObject> raw)
        {
            var byId = new JsMap<string, JsonObject>();
            for (var index = 0; index < raw.Count; index++)
            {
                var span = raw[index];
                var id = span.Str("id")!;
                if (byId.Has(id))
                {
                    Fail("waterfall/duplicate-id", $"Span id \"{id}\" is declared twice.", new JsonObject { ["path"] = $"/spans/{Js.Num(index)}/id" },
                        new JsonObject { ["id"] = id }, ["Give every span a unique id."]);
                }
                byId.Set(id, span);
                var start = span.Num("start")!.Value;
                var end = span.Num("end");
                var duration = span.Num("duration");
                var hasEnd = end is { } e && double.IsFinite(e);
                var hasDuration = duration is { } d && double.IsFinite(d);
                void Timing(string message, string[] fixes) => Fail("waterfall/invalid-timing", $"Span \"{id}\" {message}",
                    new JsonObject { ["path"] = $"/spans/{Js.Num(index)}", ["nodeId"] = id },
                    new JsonObject { ["start"] = start, ["end"] = end, ["duration"] = duration, ["status"] = span.Str("status") ?? "ok" }, fixes);
                if (hasDuration && !double.IsFinite(start + duration!.Value))
                {
                    Timing("has a derived end outside the finite numeric range.", ["Rescale the recorded timing and unit to a representable range."]);
                }
                if (hasEnd && end < start) Timing($"ends ({Js.Num(end!.Value)}) before it starts ({Js.Num(start)}).", ["Correct start or end from the recorded timing."]);
                if (hasEnd && hasDuration && Math.Abs(end!.Value - start - duration!.Value) > 1e-9)
                {
                    Timing($"states end {Js.Num(end.Value)} and duration {Js.Num(duration.Value)}, which disagree with start {Js.Num(start)}.", ["Keep only one of end or duration."]);
                }
                if (!hasEnd && !hasDuration && span.Str("status") != "incomplete")
                {
                    Timing("has neither end nor duration. A span without a recorded end must be marked status \"incomplete\".",
                        ["Add the recorded end or duration.", "Set status to \"incomplete\" if the trace has no end for it."]);
                }
            }
            for (var index = 0; index < raw.Count; index++)
            {
                var span = raw[index];
                if (span.Str("parent") is { } parent && !byId.Has(parent))
                {
                    Fail("waterfall/missing-parent", $"Span \"{span.Str("id")}\" names parent \"{parent}\", which is not declared.",
                        new JsonObject { ["path"] = $"/spans/{Js.Num(index)}/parent", ["nodeId"] = span.Str("id") }, new JsonObject { ["parent"] = parent },
                        ["Declare the parent span.", "Remove parent to make this a root span."]);
                }
            }
            var reported = new JsSet<string>();
            for (var index = 0; index < raw.Count; index++)
            {
                var span = raw[index];
                var chain = new List<string>();
                JsonObject? current = span;
                while (current is not null && current.Str("parent") is { } parentId && !chain.Contains(current.Str("id")!))
                {
                    chain.Add(current.Str("id")!);
                    current = byId.Get(parentId);
                }
                if (current is not null && chain.Contains(current.Str("id")!))
                {
                    var cycle = chain.Skip(chain.IndexOf(current.Str("id")!)).ToList();
                    var sorted = cycle.ToList();
                    Js.SortDefault(sorted);
                    var key = string.Join("|", sorted);
                    if (reported.Has(key)) continue;
                    reported.Add(key);
                    Fail("waterfall/cycle", $"Parent links form a cycle: {string.Join(" -> ", cycle.Append(cycle[0]).Select(id => $"\"{id}\""))}.",
                        new JsonObject { ["path"] = $"/spans/{Js.Num(index)}/parent", ["nodeId"] = span.Str("id") },
                        new JsonObject { ["cycle"] = new JsonArray([.. cycle.Select(id => (JsonNode?)id)]) },
                        ["Point one span in the cycle at a parent outside it."]);
                }
            }
            if (problems.Count > 0)
            {
                throw ctx.Diagnostics.ThrowDiagnosticProblems("Waterfall validation failed", problems, "waterfall/input",
                    new JsonObject { ["diagramType"] = "waterfall" }, details);
            }
        }

        string FormatDuration(double value) => $"{Number(value)} {unitLabel}";

        string Percent(double value)
        {
            if (wall == 0 || double.IsNaN(wall)) return "100%";   // `!wall`
            var share = value / wall * 100;
            return $"{Js.Num(share >= 10 ? Js.Round(share) : Js.Round(share * 10) / 10)}%";
        }

        // ---- Tones (:146-157) -------------------------------------------------------------------
        // Colour states who did the work; red is reserved for status "error", never a service colour.
        string ServiceTone(string? service) => service is null ? "external" : SERVICE_TONES[services.IndexOf(service) % SERVICE_TONES.Length];
        string ToneOf(Row row) => StatusTone(row.Status) ?? ServiceTone(serviceOf.Get(row.Id));
        // The swatch's `entry.tone` (:221, :224): a service entry is keyed by its first owner, a status entry by its status.
        string EntryTone(string kind) => kind.StartsWith("service:", StringComparison.Ordinal) ? ServiceTone(kind["service:".Length..]) : StatusTone(kind) ?? "external";

        // ---- Geometry (:159-193) ----------------------------------------------------------------
        // Name column: tree indent, then a service dot, then the name.
        static double NameWidth(Row row) => row.Depth * Layout.Indent + NAME_DOT + Utils.TextUnits(row.Name) * Layout.NameFont * ADVANCE * 1.04;

        double XOf(double t) => Js.Round((axisX0 + (t - t0) * scale) * 100) / 100;

        double TickStep()
        {
            if (wall == 0 || double.IsNaN(wall)) return 1;   // `!wall`
            var target = Layout.MinTickPx / axisWidth * wall;
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
            for (; ; )
            {
                foreach (var factor in NICE) if (factor * magnitude >= target) return factor * magnitude;
                magnitude *= 10;
            }
        }

        // Tree guides in the name column state the parent/child structure; the bars themselves only ever state time.
        static double DotX(Row row) => Layout.Margin + row.Depth * Layout.Indent + 4;

        public JsonObject BuildLayoutReport() => new()
        {
            ["diagram_type"] = "waterfall",
            ["unit"] = unit,
            ["viewBox"] = new JsonArray(width, height),
            ["origin"] = t0,
            ["wall"] = wall,
            ["pxPerUnit"] = scale,
            ["axis"] = new JsonObject { ["x0"] = axisX0, ["x1"] = axisX1 },
            ["ticks"] = new JsonArray([.. ticks.Select(tick => (JsonNode?)new JsonObject { ["t"] = tick.T, ["x"] = tick.X })]),
            ["rows"] = new JsonArray([.. rows.Select(row => (JsonNode?)new JsonObject
            {
                ["id"] = row.Id, ["parent"] = row.Parent, ["depth"] = row.Depth, ["start"] = row.Start, ["end"] = row.End, ["open"] = row.Open,
                ["x0"] = row.X0, ["x1"] = row.X1, ["y"] = row.Y, ["label"] = row.Label, ["placement"] = row.Placement,
            })]),
        };

        // ---- Rendering (:233-346) ------------------------------------------------------------------
        string RenderHeader()
        {
            var evidenceKey = meta.Str("evidence");
            var evidence = ctx.Locale.Translate(locale, $"waterfall.evidence.{evidenceKey}");
            var evidenceW = Math.Ceiling(Utils.TextUnits(evidence) * 10.5 * ADVANCE + 24);
            var tone = evidenceKey == "measured" ? "backend" : "messagebus";
            var wallText = ctx.Locale.Translate(locale, "waterfall.wall", new Dictionary<string, string> { ["duration"] = FormatDuration(wall) });   // lookup only
            var range = $"{Number(t0)} → {Number(t1)} {unitLabel}";
            var wallX = Layout.Margin + evidenceW + 14;
            return $"        <g data-waterfall-evidence=\"{Utils.Esc(evidenceKey)}\">\n"
                + $"          <rect x=\"{N(Layout.Margin)}\" y=\"14\" width=\"{N(evidenceW)}\" height=\"22\" rx=\"11\" class=\"c-{tone}\" stroke-width=\"1\"/>\n"
                + $"          <circle cx=\"{N(Layout.Margin + 11)}\" cy=\"25\" r=\"3\" class=\"t-{tone}\"/>\n"
                + $"          <text x=\"{N(Layout.Margin + evidenceW / 2 + 5)}\" y=\"29\" class=\"t-{tone}\" font-size=\"10.5\" font-weight=\"700\" text-anchor=\"middle\">{Utils.Esc(evidence)}</text>\n"
                + "        </g>\n"
                + $"        <text data-waterfall-total=\"{N(wall)}\" x=\"{N(wallX)}\" y=\"29.5\" class=\"t-primary wf-num\" font-size=\"13\" font-weight=\"700\">{Utils.Esc(wallText)}<tspan class=\"t-muted\" font-weight=\"500\" dx=\"10\">{Utils.Esc(range)}</tspan></text>";
        }

        string RenderAxis()
        {
            var lineY = rowsTop - 6;
            var lines = new List<string>
            {
                $"        <text x=\"{N(Layout.Margin)}\" y=\"{N(lineY - 10)}\" class=\"t-muted wf-caps\" font-size=\"10\" font-weight=\"700\">{Utils.Esc(ctx.Locale.Translate(locale, "waterfall.operation"))}</text>",
                $"        <line x1=\"{N(axisX0)}\" y1=\"{N(lineY)}\" x2=\"{N(axisX1)}\" y2=\"{N(lineY)}\" class=\"wf-axis\" stroke-width=\"1\"/>",
            };
            lines.AddRange(ticks.Select((tick, index) =>
                $"        <line x1=\"{N(tick.X)}\" y1=\"{N(lineY)}\" x2=\"{N(tick.X)}\" y2=\"{N(contentBottom)}\" class=\"wf-grid{(index != 0 ? "" : " wf-grid-origin")}\" stroke-width=\"1\"/>\n"
                + $"        <line x1=\"{N(tick.X)}\" y1=\"{N(lineY - 4)}\" x2=\"{N(tick.X)}\" y2=\"{N(lineY)}\" class=\"wf-axis\" stroke-width=\"1\"/>\n"
                + $"        <text x=\"{N(tick.X)}\" y=\"{N(lineY - 10)}\" class=\"t-muted wf-num\" font-size=\"10.5\" text-anchor=\"middle\">{Utils.Esc(Number(tick.T))} {Utils.Esc(unitLabel)}</text>"));
            return string.Join("\n", lines);
        }

        string RenderGuides()
        {
            var rowIndex = new JsMap<string, int>();
            for (var index = 0; index < rows.Count; index++) rowIndex.Set(rows[index].Id, index);
            return string.Join("\n", rows.Where(row => row.Parent is not null).Select(row =>
            {
                var parent = rows[rowIndex.Get(row.Parent!)];
                var parentY = parent.Y + Layout.RowH / 2 + 6;
                var y = row.Y + Layout.RowH / 2;
                var x = DotX(parent);
                return $"        <path {SvgAttrs.FocusEdgeAttrs(row.Parent!, row.Id, null, Js.Num(row.Index), null)} d=\"M {N(x)} {N(parentY)} V {N(y - 4)} Q {N(x)} {N(y)} {N(x + 4)} {N(y)} H {N(DotX(row) - 7)}\" class=\"wf-guide\" stroke-width=\"1\" fill=\"none\"/>";
            }));
        }

        // A span with children is drawn as a tinted, outlined bracket over its time; a span that does the work itself is a
        // solid bar. Parent and child are never added together.
        string RenderRow(Row row, int order)
        {
            var parent = row.Parent is not null ? rows.FirstOrDefault(candidate => candidate.Id == row.Parent) : null;
            var share = ctx.Locale.Translate(locale, "waterfall.share",
                new Dictionary<string, string> { ["percent"] = Percent(row.Duration), ["total"] = FormatDuration(wall) });   // lookup only
            var timing = $"{Number(row.Start)}–{(row.Open ? "…" : Number(row.End))} {unitLabel} · {row.Label} · {share}";
            var service = serviceOf.Get(row.Id);
            var passport = new JsonObject
            {
                ["kind"] = row.Tone,
                ["sublabel"] = string.Join(" — ", new[] { timing, row.Detail }.Where(part => !string.IsNullOrEmpty(part))),
            };
            // `[service, parent?.name].filter(Boolean).join(' › ') || undefined`
            var context = string.Join(" › ", new[] { service, parent?.Name }.Where(part => !string.IsNullOrEmpty(part)));
            if (context.Length > 0) passport["context"] = context;
            var midY = row.Y + Layout.RowH / 2;
            var barY = midY - Layout.BarH / 2;
            var nameX = DotX(row) + 10;
            var paint = row.Open ? "wf-open" : row.ParentBar ? "wf-span" : "wf-solid";
            var openTail = row.Open
                ? $"<path d=\"M {N(row.X1 - 8)} {N(barY + 2)} L {N(row.X1 - 2)} {N(midY)} L {N(row.X1 - 8)} {N(barY + Layout.BarH - 2)}\" class=\"wf-open-edge t-{row.Tone}\" stroke-width=\"1.6\" fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>"
                : "";
            var serviceAttr = !string.IsNullOrEmpty(service) ? $" data-waterfall-service=\"{Utils.Esc(service)}\"" : "";
            return $"        <g {SvgAttrs.FocusNodeAttrs(row.Id, row.Name, passport, locale, ctx)} data-waterfall-start=\"{N(row.Start)}\" data-waterfall-end=\"{(row.Open ? "" : N(row.End))}\" data-waterfall-duration=\"{N(row.Duration)}\" data-waterfall-depth=\"{N(row.Depth)}\" data-waterfall-status=\"{row.Status}\"{serviceAttr}>\n"
                + $"          {SvgAttrs.FocusNodeTitle(row.Name, passport)}\n"
                + $"          <rect x=\"{N(Layout.Margin - 8)}\" y=\"{N(row.Y + 1)}\" width=\"{N(width - Layout.Margin * 2 + 16)}\" height=\"{N(Layout.RowH - 2)}\" rx=\"6\" class=\"c-mask wf-row{(order % 2 != 0 ? " wf-row-alt" : "")}\"/>\n"
                + $"          <rect x=\"{N(row.X0)}\" y=\"{N(barY)}\" width=\"{N(row.Width)}\" height=\"{N(Layout.BarH)}\" rx=\"{N(Math.Min(4, row.Width / 2))}\" class=\"c-{row.Tone} {paint}\"{SvgAttrs.AnimateAttr(meta, "node", order)} stroke-width=\"1.2\"/>\n"
                + $"          {openTail}\n"
                + $"          <circle cx=\"{N(DotX(row))}\" cy=\"{N(midY)}\" r=\"{(row.Depth != 0 ? "3.5" : "4.5")}\" class=\"t-{row.Tone} wf-dot\"/>\n"
                + $"          <text data-node-label=\"\" x=\"{N(nameX)}\" y=\"{N(midY + 4.3)}\" class=\"t-primary\" font-size=\"{N(Layout.NameFont)}\" font-weight=\"{(row.Depth != 0 ? (row.ParentBar ? "650" : "500") : "750")}\">{Utils.Esc(row.Name)}</text>\n"
                + $"          <text x=\"{N(row.X1 + LABEL_GAP)}\" y=\"{N(midY + 3.8)}\" class=\"{(row.ParentBar ? "t-primary" : "t-muted")} wf-num\" font-size=\"{N(Layout.DurationFont)}\" font-weight=\"{(row.ParentBar ? "700" : "600")}\">{Utils.Esc(row.Label)}</text>\n"
                + "        </g>";
        }

        string LegendSwatch(LegendEntry entry)
        {
            var paint = entry.Kind == "incomplete" ? "wf-open" : "wf-solid";
            return $"<rect x=\"{N(entry.X)}\" y=\"{N(entry.Baseline - 10)}\" width=\"14\" height=\"10\" rx=\"2.5\" class=\"c-{EntryTone(entry.Kind)} {paint}\" stroke-width=\"1.2\"/>";
        }

        public string RenderSvg()
        {
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(width)} {N(height)}\" {SvgAttrs.SvgRootAttrs(ctx)} data-motion-choreography=\"replay\" data-waterfall-ui=\"\" data-waterfall-unit=\"{Utils.Esc(unit)}\" data-reader-fit=\"width-first\" data-reader-min-text=\"7.5\">\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "waterfall")).Append('\n');
            sb.Append(Utils.RenderDefinitions()).Append('\n');
            sb.Append("        <style>\n");
            sb.Append("          svg[data-waterfall-ui] .wf-axis { stroke: var(--text-dim); }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-grid { stroke: var(--lane-stroke); stroke-dasharray: 2 4; opacity: .7; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-grid-origin { stroke-dasharray: none; opacity: .9; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-row { fill: transparent; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-row-alt { fill: var(--lane-fill); fill-opacity: 1; opacity: .7; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-guide { stroke: var(--lane-stroke); }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-span { fill-opacity: .9; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-open { stroke-dasharray: 3 2.5; fill-opacity: .6; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-open-edge { fill: none !important; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-num { font-variant-numeric: tabular-nums; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-caps { letter-spacing: .06em; }\n");
            sb.Append("          svg[data-waterfall-ui] .wf-solid { stroke: none; }\n");
            sb.Append("          /* Thin bars stay crisp: the classic preset's card shadow blurs their edges. */\n");
            sb.Append("          svg[data-waterfall-ui] [data-node-id] > rect { filter: none !important; }\n");
            sb.Append(string.Join("\n", SOLID_TONES.Select(tone =>
                $"          svg[data-waterfall-ui] .wf-solid.c-{tone} {{ fill: var(--{tone}-stroke); }}\n"
                + $"          svg[data-waterfall-ui] .wf-open-edge.t-{tone} {{ stroke: var(--{tone}-stroke); }}"))).Append('\n');
            sb.Append("        </style>\n");
            sb.Append('\n');
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append('\n');
            sb.Append(RenderHeader()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Time axis (bar position and length come only from recorded timing) -->\n");
            sb.Append(RenderAxis()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Parent / child guides -->\n");
            sb.Append(RenderGuides()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Spans, depth-first by start time -->\n");
            sb.Append(string.Join("\n", rows.Select(RenderRow))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Legend -->\n");
            sb.Append(legendEntries.Count > 0
                ? Legend.RenderLegend(ctx, legendEntries, new LegendLayout(Layout.Margin, height - 16, width - Layout.Margin * 2, FontSize: 11,
                    MinTitleY: contentBottom + 8, DiagramType: "waterfall"), LegendSwatch, locale)
                : "").Append('\n');
            sb.Append("      </svg>");
            return sb.ToString();
        }
    }
}
