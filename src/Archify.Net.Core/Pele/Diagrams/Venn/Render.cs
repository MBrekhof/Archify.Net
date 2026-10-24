using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/venn/render.ts
    internal static partial class Venn
    {
        const double RadiusUnit = 100;   // RADIUS
        const double MaxGrowth = 1.6;
        static readonly ResolvedStyle NoStyle = Theme.ResolveStyle([]);

        // What is written in a region: its label first, then its texts. The id is that of a text.
        sealed class Line(string? id, string raw, double size, int @base, Label label)
        {
            public string? Id = id;
            public string Raw = raw;
            public double Size = size;
            public int Base = @base;
            public Label Label = label;
        }

        sealed record Region(List<string> Sets, List<int> Inside, Spot Spot, List<Line> Lines, ResolvedStyle Style);

        // One key for a list of set ids. Ids may hold any character, so they are not simply joined.
        static string KeyOf(List<string> ids) => ids.Count == 1 ? ids[0] : JsonKey(ids);

        public static PeleRenderResult Render(VennDb model, JsonObject config, PeleRenderContext context)
        {
            _ = config;   // render.ts reads no config
            var size = context.Options.FontSize;
            var small = Js.Round(size * 0.875);
            var tiny = Js.Round(size * 0.8125);
            var pad = context.Options.Padding;

            var circles = LayoutCircles(model.Subsets);
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < circles.Count; i++) index[circles[i].Id] = i;

            // Styles for the same targets add up, later declarations winning.
            var styles = new JsMap<string, JsMap<string, string>>();
            foreach (var (targets, declared) in model.Styles)
            {
                var key = KeyOf(targets);
                var merged = styles.Get(key);
                if (merged is null) styles.Set(key, merged = new());
                foreach (var (property, value) in declared.Entries) merged.Set(property, value);
            }
            ResolvedStyle StyleOf(string key)
            {
                var declared = styles.Get(key);
                if (declared is null) return NoStyle;
                return Theme.ResolveStyle([.. declared.Entries.Select(e => $"{e.Key}:{e.Value}")]);
            }

            // Circles that touch each circle; only those can cut into a region of it.
            var neighbors = circles.Select(_ => new List<int>()).ToList();
            for (var a = 0; a < circles.Count; a++)
            {
                for (var b = a + 1; b < circles.Count; b++)
                {
                    if (Js.Hypot(circles[a].X - circles[b].X, circles[a].Y - circles[b].Y) < circles[a].R + circles[b].R)
                    {
                        neighbors[a].Add(b);
                        neighbors[b].Add(a);
                    }
                }
            }

            // One region for each set and each stated overlap; a later statement about the same sets replaces an earlier one.
            var stated = new JsMap<string, VennSubset>();
            foreach (var subset in model.Subsets) stated.Set(KeyOf(subset.Sets), subset);
            var regions = new JsMap<string, Region>();
            foreach (var (key, subset) in stated.Entries)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var sets = subset.Sets.Where(seen.Add).ToList();   // [...new Set(subset.sets)]
                var inside = sets.Select(id => index[id]).ToList();
                var within = new HashSet<int>(inside);
                var smallest = inside[0];
                foreach (var i in inside) if (circles[i].R < circles[smallest].R) smallest = i;
                var style = StyleOf(key);
                var text = subset.Label ?? (subset.Sets.Count == 1 ? subset.Sets[0] : null);
                var @base = (style.Bold ? (int)Pele.Style.Bold : 0) | (style.Italic ? (int)Pele.Style.Italic : 0);
                var spot = FindSpot(circles, inside, neighbors[smallest].Where(i => !within.Contains(i)).ToList());
                // A region that other circles cover, or all but cover, is written across them instead.
                if (spot.Margin < circles[smallest].R * 0.2) spot = FindSpot(circles, inside, []);
                var region = new Region(sets, inside, spot, [], style);
                if (!string.IsNullOrEmpty(text))
                {
                    var fontSize = sets.Count == 1 ? size : small;
                    region.Lines.Add(new(null, text, fontSize, @base, Labels.LayoutLabel(text, false, fontSize, 4000, @base)));
                }
                regions.Set(key, region);
            }
            foreach (var node in model.TextNodes)
            {
                var raw = node.Label ?? node.Id;
                regions.Get(KeyOf(node.Sets))?.Lines.Add(new(node.Id, raw, tiny, 0, Labels.LayoutLabel(raw, false, tiny, 4000)));
            }

            // Lengths so far are in units of the layout. The scale gives the largest circle a set radius, and grows, within
            // limits, until every region holds what is written in it.
            double largest = 0;
            foreach (var c in circles) if (c.R > largest) largest = c.R;
            var scale = largest > 0 ? RadiusUnit / largest : 1;
            double growth = 1;
            foreach (var region in regions.Values)
            {
                if (region.Spot.Margin <= 0) continue;
                double width = 0, height = 0;
                foreach (var line in region.Lines)
                {
                    width = Math.Max(width, line.Label.Width);
                    height += line.Label.Height;
                }
                var need = Js.Hypot(width, height) / 2 + 4;
                growth = Math.Max(growth, Math.Min(need / (region.Spot.Margin * scale), MaxGrowth));
            }
            scale *= growth;

            // What is still too wide for its region is wrapped to it.
            foreach (var region in regions.Values)
            {
                var room = Math.Max(region.Spot.Margin * scale * 1.8, 72);
                foreach (var line in region.Lines)
                    if (line.Label.Width > room) line.Label = Labels.LayoutLabel(line.Raw, false, line.Size, room, line.Base);
            }

            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var c in circles)
            {
                minX = Math.Min(minX, (c.X - c.R) * scale);
                minY = Math.Min(minY, (c.Y - c.R) * scale);
                maxX = Math.Max(maxX, (c.X + c.R) * scale);
                maxY = Math.Max(maxY, (c.Y + c.R) * scale);
            }
            foreach (var region in regions.Values)
            {
                double wide = 0, tall = 0;
                foreach (var line in region.Lines)
                {
                    wide = Math.Max(wide, line.Label.Width);
                    tall += line.Label.Height;
                }
                minX = Math.Min(minX, region.Spot.X * scale - wide / 2);
                maxX = Math.Max(maxX, region.Spot.X * scale + wide / 2);
                minY = Math.Min(minY, region.Spot.Y * scale - tall / 2);
                maxY = Math.Max(maxY, region.Spot.Y * scale + tall / 2);
            }
            if (circles.Count == 0) minX = minY = maxX = maxY = 0;

            var title = Labels.LayoutLabel(string.IsNullOrEmpty(model.Title) ? model.Title : NormalizeText(model.Title), false, size, 4000, (int)Pele.Style.Bold);
            var titleHeight = title.Height > 0 ? title.Height + 12 : 0;
            var inner = Math.Max(maxX - minX, title.Width);
            var ox = pad + (inner - (maxX - minX)) / 2 - minX;
            var oy = pad + titleHeight - minY;
            var totalWidth = Math.Ceiling(inner + 2 * pad);
            var totalHeight = Math.Ceiling(maxY - minY + titleHeight + 2 * pad);
            var placed = circles.Select(c => new Circle(c.Id, c.X * scale + ox, c.Y * scale + oy, c.R * scale)).ToList();

            var setsOut = new StringBuilder();
            for (var i = 0; i < placed.Count; i++)
            {
                var c = placed[i];
                var color = Theme.SeriesColor(i);
                var style = regions.Get(c.Id)?.Style ?? NoStyle;
                setsOut.Append($"<circle class=\"pele-venn-set\" data-id=\"{Builder.EscText(c.Id)}\" cx=\"{Builder.Num(c.X)}\" cy=\"{Builder.Num(c.Y)}\" r=\"{Builder.Num(c.R)}\" fill=\"{color}\" fill-opacity=\"0.2\" stroke=\"{color}\"{style.Shape}/>");
            }

            var overlaps = new StringBuilder();
            var labels = new StringBuilder();
            foreach (var region in regions.Values)
            {
                var id = Builder.EscText(string.Join("|", region.Sets));
                // An overlap is only drawn when a style gives it a look of its own; otherwise the circles show it.
                if (region.Sets.Count > 1 && region.Style.Shape != "")
                {
                    var d = RegionPath([.. region.Inside.Select(i => placed[i])]);
                    if (d != "") overlaps.Append($"<path class=\"pele-venn-overlap\" data-id=\"{id}\" d=\"{d}\" fill=\"none\"{region.Style.Shape}/>");
                }
                double total = 0;
                foreach (var line in region.Lines) total += line.Label.Height;
                if (total == 0) continue;
                var x = region.Spot.X * scale + ox;
                var y = region.Spot.Y * scale + oy - total / 2;
                var content = new StringBuilder();
                foreach (var line in region.Lines)
                {
                    var attrs = line.Id is null
                        ? $" class=\"pele-label\"{(line.Size == size ? "" : $" font-size=\"{Js.Num(line.Size)}\"")}{region.Style.Text}"
                        : $" class=\"pele-venn-text\" data-id=\"{Builder.EscText(line.Id)}\" font-size=\"{Js.Num(tiny)}\" fill=\"var(--_m)\"{StyleOf(line.Id).Text}";
                    content.Append(Builder.LabelSvg(line.Label, x, y + line.Label.Height / 2, attrs));
                    y += line.Label.Height;
                }
                labels.Append($"<g class=\"pele-venn-region\" data-id=\"{id}\">{content}</g>");
            }

            var svg = Root.SvgDocument("venn", totalWidth, totalHeight, size, context, null, null,
                Builder.LabelSvg(title, totalWidth / 2, pad + title.Height / 2, " class=\"pele-title\" font-weight=\"var(--_tw)\"")
                + $"<g class=\"pele-venn-sets\">{setsOut}</g>"
                + (overlaps.Length > 0 ? $"<g class=\"pele-venn-overlaps\">{overlaps}</g>" : "")
                + (labels.Length > 0 ? $"<g class=\"pele-venn-labels\">{labels}</g>" : ""));
            return new(svg, totalWidth, totalHeight, "venn");
        }
    }
}
