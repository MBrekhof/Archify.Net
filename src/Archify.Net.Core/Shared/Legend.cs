using System.Text.Json.Nodes;

namespace Archify.Net;

// legend.mjs. One record stands in for the catalog entry, its resolved copy (`present`/`interactive`), the measured copy
// (`width`) and the positioned copy (`x`/`baseline`/`row`); `Interactive` is null on a catalog entry that does not opt out.
public sealed record LegendEntry(string Kind, string Label, bool Present = false, bool? Interactive = null, double? SwatchWidth = null,
    double? SwatchGap = null, double? TrailingWidth = null, double Width = 0, double X = 0, double Baseline = 0, int Row = 0);

// `{ kind: 'relationship-segment', start, end }` or `{ kind: 'relationship-label', x, y, width, height }`.
public sealed record LegendObstacle(string Kind, Segment? Segment, Rect? Rect);

public sealed record LegendFootprintResult(List<LegendEntry> Measured, List<List<LegendEntry>> Rows, int RowCount, double MinWidth, double ExtraHeight);
public sealed record MeasuredLegend(List<LegendEntry> Entries, int RowCount, double? TitleY, double FontSize);

// measureLegend's destructured layout (legend.mjs:111-123) with its defaults.
public sealed record LegendLayout(double X, double BaselineY, double Width, double FontSize = Legend.DefaultFontSize, double ItemGap = Legend.DefaultItemGap,
    double LineGap = Legend.DefaultLineGap, double SwatchGap = Legend.DefaultSwatchGap, double MinTitleY = 0, IReadOnlyList<LegendObstacle>? Obstacles = null,
    string Unfit = "error", string DiagramType = "diagram");

public static class Legend
{
    public const double DefaultFontSize = 8;
    public const double DefaultItemGap = 22;
    public const double DefaultLineGap = 22;
    public const double DefaultSwatchGap = 8;
    const double TextAdvanceEm = 0.62;
    const double InteractiveBadgeAllowance = 21;

    public static List<LegendObstacle> RelationshipLegendObstacles(JsonNode? relations, Func<JsonObject, int, IReadOnlyList<Pt>?>? pointsFor = null,
        Func<JsonObject, int, Rect?>? labelRectFor = null)
    {
        var obstacles = new List<LegendObstacle>();
        var index = -1;
        foreach (var node in relations.AsArrayOrEmpty())
        {
            index++;
            if (node is not JsonObject relation) continue;
            var points = pointsFor is not null ? pointsFor(relation, index) : [];
            var finitePoints = (points ?? []).Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)).ToList();
            for (var pointIndex = 0; pointIndex < finitePoints.Count - 1; pointIndex += 1)
                obstacles.Add(new("relationship-segment", new Segment(finitePoints[pointIndex], finitePoints[pointIndex + 1]), null));
            var labelRect = labelRectFor is not null ? labelRectFor(relation, index) : null;
            if (labelRect is { } rect && Geometry.IsFinitePoint(rect.X, rect.Y, rect.Width, rect.Height))
                obstacles.Add(new("relationship-label", null, rect));
        }
        return obstacles;
    }

    public static List<LegendEntry> ResolveLegend(JsonObject? config, IReadOnlyList<LegendEntry> catalog, IEnumerable<string>? presentKinds)
    {
        var mode = config.Str("mode") is { Length: > 0 } m ? m : "auto";
        if (mode == "hidden") return [];
        var present = new HashSet<string>(presentKinds ?? [], StringComparer.Ordinal);   // lookup only
        var overrides = config.Obj("entries") ?? new JsonObject();

        var resolved = new List<LegendEntry>();
        foreach (var catalogEntry in catalog)
        {
            var @override = overrides[catalogEntry.Kind] as JsonObject;
            var selectedByMode = mode == "all" || present.Contains(catalogEntry.Kind);
            var visibleFlag = @override.Bool("visible");
            var visible = visibleFlag == true || (selectedByMode && visibleFlag != false);
            if (!visible) continue;
            resolved.Add(catalogEntry with
            {
                Label = @override.Str("label") is { Length: > 0 } label ? label : catalogEntry.Label,
                Present = present.Contains(catalogEntry.Kind),
                Interactive = catalogEntry.Interactive != false && present.Contains(catalogEntry.Kind),
            });
        }
        return resolved;
    }

    // Labels render slightly larger than the layout font size; measure the
    // rendered size so a long label cannot run into the next entry.
    static double RenderedLegendFontSize(double fontSize) => fontSize < 8 ? fontSize + 0.5 : fontSize + 2;

    static double MeasuredEntryWidth(LegendEntry entry, double fontSize, double swatchGap)
    {
        var swatchWidth = entry.SwatchWidth ?? 14;
        return Math.Ceiling(
            swatchWidth
            + swatchGap
            + Utils.TextUnits(entry.Label) * RenderedLegendFontSize(fontSize) * TextAdvanceEm
            + (entry.Interactive == true ? InteractiveBadgeAllowance : 0)
            + (entry.TrailingWidth ?? 0));
    }

    // One pure footprint calculation owns both auto-viewBox sizing and final SVG
    // placement. Callers must not maintain a second approximation of legend width
    // or row count; that would make generated geometry disagree with validation.
    public static LegendFootprintResult LegendFootprint(IReadOnlyList<LegendEntry> entries, double width, double fontSize = DefaultFontSize,
        double itemGap = DefaultItemGap, double lineGap = DefaultLineGap, double swatchGap = DefaultSwatchGap)
    {
        if (entries.Count == 0) return new([], [], 0, 0, 0);
        var measured = entries.Select(entry => entry with { Width = MeasuredEntryWidth(entry, fontSize, entry.SwatchGap ?? swatchGap) }).ToList();
        var rows = new List<List<LegendEntry>> { new() };
        double cursor = 0;
        foreach (var entry in measured)
        {
            var row = rows[^1];
            var required = (row.Count > 0 ? itemGap : 0) + entry.Width;
            if (row.Count > 0 && cursor + required > width)
            {
                rows.Add([entry]);
                cursor = entry.Width;
            }
            else
            {
                row.Add(entry);
                cursor += required;
            }
        }
        double minWidth = 0;
        foreach (var entry in measured) minWidth = Math.Max(minWidth, entry.Width);
        return new(measured, rows, rows.Count, minWidth, (rows.Count - 1) * lineGap);
    }

    public static MeasuredLegend? MeasureLegend(DiagnosticSink sink, IReadOnlyList<LegendEntry> entries, LegendLayout layout)
    {
        var (x, baselineY, width, fontSize, itemGap, lineGap, swatchGap, minTitleY, obstacles, unfit, diagramType) =
            (layout.X, layout.BaselineY, layout.Width, layout.FontSize, layout.ItemGap, layout.LineGap, layout.SwatchGap, layout.MinTitleY, layout.Obstacles ?? [], layout.Unfit, layout.DiagramType);
        if (entries.Count == 0) return new([], 0, null, fontSize);
        var footprint = LegendFootprint(entries, width, fontSize, itemGap, lineGap, swatchGap);
        var tooWide = footprint.Measured.FirstOrDefault(entry => entry.Width > width);
        if (tooWide is not null)
        {
            if (unfit == "hide") return null;
            var message = $"[legend/label-too-wide] {diagramType} legend label for \"{tooWide.Kind}\" needs {Js.Num(tooWide.Width)}px but only {Js.Num(width)}px is available.";
            throw sink.ThrowDiagnosticError(message, [new Diagnostic("legend/label-too-wide", Severity.Error, message,
                new JsonObject { ["diagramType"] = diagramType, ["path"] = $"/meta/legend/entries/{tooWide.Kind}/label" },
                new JsonObject { ["kind"] = tooWide.Kind, ["measuredWidthPx"] = tooWide.Width, ["availableWidthPx"] = width },
                ["shorten the legend label or use a wider viewBox"], null)]);
        }

        var titleY = baselineY - footprint.ExtraHeight - 20;
        var legendTopY = titleY - 10;
        if (legendTopY < minTitleY)
        {
            if (unfit == "hide") return null;
            var message = $"[legend/vertical-overflow] {diagramType} legend needs {Js.Num(footprint.RowCount)} rows, which would start at y={Js.Num(legendTopY)} above the available legend band at y={Js.Num(minTitleY)}.";
            throw sink.ThrowDiagnosticError(message, [new Diagnostic("legend/vertical-overflow", Severity.Error, message,
                new JsonObject { ["diagramType"] = diagramType, ["path"] = "/meta/legend" },
                new JsonObject { ["rowCount"] = footprint.RowCount, ["requiredTopY"] = legendTopY, ["availableTopY"] = minTitleY },
                ["shorten legend labels, hide nonessential entries, or use a wider viewBox"], null)]);
        }

        var positioned = new List<LegendEntry>();
        for (var rowIndex = 0; rowIndex < footprint.Rows.Count; rowIndex++)
        {
            var entryX = x;
            var baseline = baselineY - (footprint.RowCount - rowIndex - 1) * lineGap;
            foreach (var entry in footprint.Rows[rowIndex])
            {
                positioned.Add(entry with { X = entryX, Baseline = baseline, Row = rowIndex });
                entryX += entry.Width + itemGap;
            }
        }

        var legendRects = new List<(string Kind, Rect Rect)> { ("title", new Rect(x, legendTopY, 48, 14)) };
        legendRects.AddRange(positioned.Select(entry => (entry.Kind, new Rect(entry.X, entry.Baseline - 10, entry.Width, 14))));
        var collision = legendRects.Cast<(string Kind, Rect Rect)?>().FirstOrDefault(legendRect => obstacles.Any(obstacle =>
            obstacle.Segment is { } segment
                ? Geometry.SegmentIntersectsRect(segment, legendRect!.Value.Rect)
                : Geometry.RectsOverlap(obstacle.Rect!.Value, legendRect!.Value.Rect)));
        if (collision is { } hit)
        {
            if (unfit == "hide") return null;
            var message = $"[legend/content-overlap] {diagramType} legend entry \"{hit.Kind}\" overlaps authored relationship geometry.";
            throw sink.ThrowDiagnosticError(message, [new Diagnostic("legend/content-overlap", Severity.Error, message,
                new JsonObject { ["diagramType"] = diagramType, ["path"] = "/meta/legend" },
                new JsonObject
                {
                    ["legendKind"] = hit.Kind,
                    ["legendRect"] = new JsonObject { ["kind"] = hit.Kind, ["x"] = hit.Rect.X, ["y"] = hit.Rect.Y, ["width"] = hit.Rect.Width, ["height"] = hit.Rect.Height },
                },
                ["shorten or hide legend entries, use a wider viewBox, or move the authored relationship route/label out of the legend band"], null)]);
        }

        return new(positioned, footprint.RowCount, titleY, fontSize);
    }

    // `labelClass`/`labelWeight` let a renderer state that its legend labels are
    // the same ink and weight as the value they describe; both default to the
    // shared presentation, so callers that pass nothing keep their exact bytes.
    public static string RenderLegend(RenderContext ctx, IReadOnlyList<LegendEntry> entries, LegendLayout layout, Func<LegendEntry, string> renderSwatch,
        string? locale, string labelClass = "t-muted", double labelWeight = 500)
    {
        if (entries.Count == 0) return "";
        var measured = MeasureLegend(ctx.Diagnostics, entries, layout);
        if (measured is null) return "";
        var hasInteractiveEntries = measured.Entries.Any(entry => entry.Interactive == true);
        var renderedFontSize = RenderedLegendFontSize(measured.FontSize);
        var rootAttributes = hasInteractiveEntries ? " data-legend=\"\" data-legend-bridge=\"\"" : " data-legend=\"\"";
        var parts = new List<string>
        {
            $"        <g{rootAttributes}>",
            $"          <text x=\"{Js.Num(layout.X)}\" y=\"{Js.Num(measured.TitleY!.Value)}\" class=\"t-primary\" font-size=\"12\" font-weight=\"650\">{Utils.Esc(ctx.Locale.Translate(locale, "legend.title"))}</text>",
        };

        foreach (var entry in measured.Entries)
        {
            var interactive = entry.Interactive == true
                ? $" data-legend-kind=\"{Utils.Esc(entry.Kind)}\" data-legend-label=\"{Utils.Esc(entry.Label)}\""
                : "";
            parts.Add($"          <g data-legend-semantic-kind=\"{Utils.Esc(entry.Kind)}\"{interactive} data-legend-x=\"{Js.Num(entry.X)}\" data-legend-baseline=\"{Js.Num(entry.Baseline)}\" data-legend-width=\"{Js.Num(entry.Width)}\">");
            parts.Add($"            {renderSwatch(entry)}");
            parts.Add($"            <text x=\"{Js.Num(entry.X + (entry.SwatchWidth ?? 14) + (entry.SwatchGap ?? DefaultSwatchGap))}\" y=\"{Js.Num(entry.Baseline)}\" class=\"{labelClass}\" font-size=\"{Js.Num(renderedFontSize)}\" font-weight=\"{Js.Num(labelWeight)}\">{Utils.Esc(entry.Label)}</text>");
            parts.Add("          </g>");
        }
        parts.Add("        </g>");
        return string.Join("\n", parts);
    }
}
