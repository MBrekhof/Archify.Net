using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// render-timeline.mjs. The module-level flow (line 28 to the final writeDiagram call) becomes one Scene built per render;
// `--layout-json` becomes the LayoutReport on the result and writeDiagram's HTML is returned by the facade.
public sealed class TimelineRenderer : IDiagramRenderer
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
        public const double AxisH = 60;
        public const double EdgePad = 72;
        public const double BreakW = 60;
        public const double LaneGap = 10;
        public const double LanePad = 14;
        public const double StemGap = 16;
        public const double RowGap = 8;
        public const double CardMaxW = 200;
        public const double CardPadX = 10;
        public const double CardAccent = 12;
        public const double TitleFont = 12.5;
        public const double TitleLine = 15;
        public const double TimeFont = 10.5;
        public const double MinTickPx = 84;
    }

    const double ADVANCE = NodeTextFit.WidthFactor;
    const double MINUTE = 60_000;
    const double HOUR = 60 * MINUTE;
    const double DAY = 24 * HOUR;
    const double SEGMENT_FLOOR = 28;
    static readonly double[] TICKS = [1000, 2000, 5000, 10_000, 15_000, 30_000, MINUTE, 2 * MINUTE, 5 * MINUTE, 10 * MINUTE, 15 * MINUTE, 30 * MINUTE,
        HOUR, 2 * HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, DAY, 2 * DAY, 7 * DAY];
    static readonly (string Unit, double Size)[] UNITS = [("d", DAY), ("h", HOUR), ("min", MINUTE), ("s", 1000)];
    // KIND_TONE (:363), iterated by Object.keys (:366) and Object.values (:499): source order, never a Dictionary.
    static readonly (string Kind, string Tone)[] KIND_TONE =
        [("default", "external"), ("change", "frontend"), ("alert", "security"), ("action", "messagebus"), ("recovery", "backend")];

    static string ToneOf(string kind)
    {
        foreach (var (k, tone) in KIND_TONE) if (k == kind) return tone;
        return "undefined";   // `${KIND_TONE[kind]}` of a kind the schema enum excludes
    }

    // `{ ...event, index, t }` (:137) plus the card fields of :301-312 and the lane-packing fields of :333-353.
    sealed class Card
    {
        public required JsonObject Source;
        public required string Id, Title, At;
        public string? Lane;
        public int Index, Row;
        public double T, Width, Height, X, Left, PadLeft, LineY, Top;
        public string Time = "", Side = "";
        public List<string> Lines = [];
    }

    sealed class Gap { public double From, To, Length, Width, X0, X1; public string Label = ""; }
    sealed class Segment { public double Start, End, X0, X1; }
    sealed record Tick(double T, double X, double? Interval);
    sealed record LaneBox(string Id, string Label, double Y, double Height, double LineY);

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject tl;
        readonly JsonObject meta;
        readonly string? locale;
        readonly double laneLabelW, axisWidth;
        readonly string timezone;
        readonly List<Card> events;
        readonly List<double> times;
        readonly double first, last;
        readonly bool showSeconds, multiDay;
        readonly List<Gap> breaks = [];
        readonly List<Segment> segments = [];
        readonly double scale, axisX0, axisX1;
        readonly List<Tick> ticks = [];
        readonly bool tickSeconds;
        readonly bool hasLanes;
        readonly List<LaneBox> laneLayout = [];
        readonly List<LegendEntry> legendEntries;
        readonly double contentBottom, width, height;

        static string N(double v) => Js.Num(v);

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            tl = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            var lanesSource = Geometry.AsArray(tl["lanes"]).Select(lane => (JsonObject)lane!).ToList();
            hasLanes = lanesSource.Count > 0;
            // The lane column fits its longest label (13px bold), so a lane name never runs into the axis.
            laneLabelW = hasLanes
                ? Math.Ceiling(Js.MaxOf(lanesSource.Select(lane => Utils.TextUnits(lane.Str("label")) * 13 * NodeTextFit.WidthFactor * 1.06 + 24).Prepend(88)))
                : 0;
            axisWidth = tl.Obj("layout").Num("width") ?? 960;

            // `tl.meta.timezone || 'UTC'` (:73).
            timezone = meta.Str("timezone") is { Length: > 0 } zone ? zone : "UTC";
            ValidateInput(lanesSource);

            // ---- Events in time order (:136-153) ----
            events = Geometry.AsArray(tl["events"]).Select((node, index) =>
            {
                var source = (JsonObject)node!;
                return new Card
                {
                    Source = source, Id = source.Str("id")!, Title = source.Str("title")!, At = source.Str("at")!, Lane = source.Str("lane"),
                    Index = index, T = Js.DateParse(source.Str("at")!),
                };
            }).ToList();
            Js.Sort(events, (a, b) => a.T - b.T is var d && d != 0 && !double.IsNaN(d) ? d : a.Index - b.Index);
            var timeSet = new JsSet<double>();
            foreach (var card in events) timeSet.Add(card.T);
            times = [.. timeSet];
            first = times[0];
            last = times[^1];
            var span = last - first;

            showSeconds = events.Any(card => FloorMod(card.T, MINUTE) != 0);
            {
                var a = Wall(first);
                var b = Wall(last);
                multiDay = a.Y != b.Y || a.Mo != b.Mo || a.D != b.D;
            }

            // ---- Axis segments and disclosed breaks (:191-247) ----
            var gaps = times.Skip(1).Select((time, index) => new Gap { From = times[index], To = time, Length = time - times[index] }).ToList();
            var sortedGaps = gaps.Select(gap => gap.Length).ToList();
            Js.Sort(sortedGaps, (a, b) => a - b);
            var medianGap = sortedGaps.Count > 0 ? sortedGaps[(sortedGaps.Count - 1) / 2] : 0;
            var candidates = new List<Gap>();
            if (tl.Obj("layout").Str("breaks") != "none")
            {
                candidates = gaps.Where(gap => gap.Length > 8 * medianGap && gap.Length > 0.1 * span).ToList();
                Js.Sort(candidates, (a, b) => b.Length - a.Length);
                candidates = candidates.Take(8).ToList();
            }

            // Reserve room for proportional time before accepting a break.
            var fixedWidth = Layout.EdgePad * 2 + SEGMENT_FLOOR;
            foreach (var gap in candidates)
            {
                gap.Label = $"≈ {ApproximateDuration(gap.Length)}";
                gap.Width = Math.Max(Layout.BreakW, Math.Ceiling(Utils.TextUnits(gap.Label) * 10.5 * ADVANCE + 18));
                var addedWidth = gap.Width + SEGMENT_FLOOR;
                if (fixedWidth + addedWidth + Layout.MinTickPx > axisWidth) continue;
                breaks.Add(gap);
                fixedWidth += addedWidth;
            }
            Js.Sort(breaks, (a, b) => a.From - b.From);

            {
                var start = first;
                foreach (var gap in breaks)
                {
                    segments.Add(new Segment { Start = start, End = gap.From });
                    start = gap.To;
                }
                segments.Add(new Segment { Start = start, End = last });
            }
            var linearTime = segments.Aggregate(0d, (sum, segment) => sum + (segment.End - segment.Start));
            axisX0 = Layout.Margin + laneLabelW;
            var drawable = axisWidth - Layout.EdgePad * 2
                - breaks.Aggregate(0d, (sum, gap) => sum + gap.Width) - SEGMENT_FLOOR * segments.Count;
            scale = linearTime > 0 ? Math.Max(0, drawable) / linearTime : 0;
            {
                var x = axisX0 + Layout.EdgePad;
                var share = linearTime > 0 ? 0 : Math.Max(0, drawable) / segments.Count;
                for (var index = 0; index < segments.Count; index++)
                {
                    var segment = segments[index];
                    var inner = (segment.End - segment.Start) * scale + share;
                    segment.X0 = x + SEGMENT_FLOOR / 2 + (segment.End == segment.Start ? inner / 2 : 0);
                    segment.X1 = segment.End == segment.Start ? segment.X0 : segment.X0 + inner;
                    x += SEGMENT_FLOOR + inner;
                    if (index < breaks.Count)
                    {
                        breaks[index].X0 = x;
                        breaks[index].X1 = x + breaks[index].Width;
                        x += breaks[index].Width;
                    }
                }
            }
            axisX1 = axisX0 + axisWidth;

            // Ticks fall on the display zone's wall clock (:255-274).
            foreach (var segment in segments)
            {
                var length = segment.End - segment.Start;
                if (length <= 0 || scale <= 0)
                {
                    ticks.Add(new Tick(segment.Start, segment.X0, null));
                    continue;
                }
                var interval = TICKS.FirstOrDefault(candidate => candidate * scale >= Layout.MinTickPx, TICKS[^1]);
                var before = ticks.Count;
                foreach (var t in WallClockTicks(segment.Start, segment.End, interval)) ticks.Add(new Tick(t, XOf(t), interval));
                // A segment too short for a regular tick still states where it starts.
                if (ticks.Count == before) ticks.Add(new Tick(segment.Start, segment.X0, interval));
            }
            tickSeconds = ticks.Any(tick => tick.Interval < MINUTE);

            // ---- Lanes and event cards (:277-360) ----
            var lanes = hasLanes
                ? lanesSource.Select(lane => (Id: lane.Str("id")!, Label: lane.Str("label") ?? "")).ToList()
                : [("__all", "")];
            foreach (var card in events)
            {
                card.Time = ClockLabel(card.T, showSeconds, multiDay);
                card.PadLeft = Layout.CardAccent + 2;
                var natural = Math.Max(Utils.TextUnits(card.Title) * Layout.TitleFont * ADVANCE * 1.05, Utils.TextUnits(card.Time) * Layout.TimeFont * ADVANCE);
                card.Width = Math.Ceiling(Math.Min(Layout.CardMaxW, natural + card.PadLeft + Layout.CardPadX));
                card.Lines = WrapTitle(card.Title, card.Width - card.PadLeft - Layout.CardPadX);
                card.Height = 9 + 12 + card.Lines.Count * Layout.TitleLine + 7;
                card.X = XOf(card.T);
                // The card is centred on its instant but never leaves the axis area.
                card.Left = Math.Min(Math.Max(card.X - card.Width / 2, axisX0 + 4), axisX1 - card.Width - 4);
            }

            {
                var y = Layout.Top + Layout.AxisH;
                foreach (var (laneId, laneLabel) in lanes)
                {
                    var members = events.Where(card => LaneOf(card) == laneId).ToList();
                    var above = (Ends: new List<double>(), Heights: new List<double>());
                    var below = (Ends: new List<double>(), Heights: new List<double>());
                    static int Fit((List<double> Ends, List<double> Heights) side, Card card)
                    {
                        var index = side.Ends.FindIndex(end => card.Left >= end + 10);
                        return index == -1 ? side.Ends.Count : index;
                    }
                    static void SetAt(List<double> list, int index, double value)
                    {
                        if (index == list.Count) list.Add(value);
                        else list[index] = value;
                    }
                    foreach (var card in members)
                    {
                        var aboveRow = Fit(above, card);
                        var belowRow = Fit(below, card);
                        card.Side = aboveRow < belowRow ? "above" : "below";
                        card.Row = card.Side == "above" ? aboveRow : belowRow;
                        var side = card.Side == "above" ? above : below;
                        SetAt(side.Ends, card.Row, card.Left + card.Width);
                        SetAt(side.Heights, card.Row, Math.Max(card.Row < side.Heights.Count ? side.Heights[card.Row] : 0, card.Height));
                    }
                    static double Extent(List<double> heights) =>
                        heights.Aggregate(0d, (sum, height) => sum + height + Layout.RowGap) - (heights.Count > 0 ? Layout.RowGap : 0);
                    var aboveExtent = Extent(above.Heights);
                    var lineY = y + Layout.LanePad + (aboveExtent != 0 && !double.IsNaN(aboveExtent) ? aboveExtent + Layout.StemGap : 10);
                    static List<double> Offsets(List<double> heights)
                    {
                        var output = new List<double>();
                        var offset = Layout.StemGap;
                        foreach (var height in heights)
                        {
                            output.Add(offset);
                            offset = offset + height + Layout.RowGap;
                        }
                        return output;
                    }
                    var aboveOffset = Offsets(above.Heights);
                    var belowOffset = Offsets(below.Heights);
                    foreach (var card in members)
                    {
                        card.LineY = lineY;
                        // Above the line a row is bottom-aligned to its edge nearest the line, below it top-aligned.
                        card.Top = card.Side == "above" ? lineY - aboveOffset[card.Row] - card.Height : lineY + belowOffset[card.Row];
                    }
                    var bottom = below.Heights.Count > 0 ? lineY + belowOffset[^1] + below.Heights[^1] : lineY + 10;
                    var laneHeight = Math.Max(56, bottom - y + Layout.LanePad);
                    laneLayout.Add(new LaneBox(laneId, laneLabel, y, laneHeight, lineY));
                    y += laneHeight + Layout.LaneGap;
                }
            }

            // ---- Legend (:363-368) ----
            var present = new JsSet<string>();
            foreach (var card in events) present.Add(KindOf(card));
            var resolved = Legend.ResolveLegend(null,
                KIND_TONE.Select(entry => new LegendEntry(entry.Kind, ctx.Locale.Translate(locale, $"legend.timeline.{entry.Kind}"), Interactive: false)).ToList(),
                present);
            legendEntries = resolved.Count > 1 ? resolved : [];

            contentBottom = laneLayout[^1].Y + laneLayout[^1].Height;
            width = Math.Ceiling(axisX1 + Layout.Margin);
            var footprint = Legend.LegendFootprint(legendEntries, width - Layout.Margin * 2, 11);
            height = Math.Ceiling(contentBottom + Layout.Margin + (legendEntries.Count > 0 ? 34 + footprint.ExtraHeight : 0));
        }


        static double FloorMod(double a, double b) => a - Math.Floor(a / b) * b;

        string LaneOf(Card card) => hasLanes ? card.Lane! : "__all";
        static string KindOf(Card card) => card.Source.Str("kind") is { Length: > 0 } kind ? kind : "default";

        // ---- Input checks (:61-110) ---------------------------------------------------------
        // Date.parse normalizes some impossible dates (February 30 becomes March 2), so the authored calendar date is
        // checked independently of its UTC offset first.
        static bool ValidCalendarDate(string timestamp)
        {
            var parts = Js.SplitOnChar(timestamp.Length > 10 ? timestamp[..10] : timestamp, '-').Select(Js.ToNumber).ToList();
            double At(int i) => i < parts.Count ? parts[i] : double.NaN;   // a missing element is undefined: every comparison fails
            var (year, month, day) = (At(0), At(1), At(2));
            var leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
            double[] days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
            // days[month - 1] on a non-integer index is undefined, and `day <= undefined` is false.
            return month >= 1 && month <= 12 && day >= 1 && month == Math.Floor(month) && day <= days[(int)month - 1];
        }

        void ValidateInput(List<JsonObject> lanesSource)
        {
            var problems = new List<string>();
            var details = new List<Diagnostic>();
            void Fail(string code, string message, JsonObject subject, JsonObject evidence, string[] supportedFixes)
            {
                problems.Add(message);
                var full = new JsonObject { ["diagramType"] = "timeline" };
                foreach (var (key, value) in subject) full[key] = value?.DeepClone();
                details.Add(new Diagnostic(code, Severity.Error, message, full, evidence, supportedFixes, null));
            }
            if (!Js.IsIanaTimeZone(timezone))
            {
                Fail("timeline/invalid-timezone", $"meta.timezone \"{timezone}\" is not a recognised IANA time zone.",
                    new JsonObject { ["path"] = "/meta/timezone" }, new JsonObject { ["timezone"] = timezone },
                    ["Use an IANA zone such as \"UTC\", \"Europe/Berlin\", or \"Asia/Shanghai\"."]);
            }
            var laneIds = new JsSet<string>();
            for (var index = 0; index < lanesSource.Count; index++)
            {
                var id = lanesSource[index].Str("id")!;
                if (laneIds.Has(id))
                {
                    Fail("timeline/duplicate-lane-id", $"Lane id \"{id}\" is declared twice.",
                        new JsonObject { ["path"] = $"/lanes/{Js.Num(index)}/id" }, new JsonObject { ["id"] = id },
                        ["Give every lane a unique id and update its event references."]);
                }
                laneIds.Add(id);
            }
            var seen = new JsSet<string>();
            var eventList = Geometry.AsArray(tl["events"]).Select(node => (JsonObject)node!).ToList();
            for (var index = 0; index < eventList.Count; index++)
            {
                var ev = eventList[index];
                var id = ev.Str("id")!;
                if (seen.Has(id))
                {
                    Fail("timeline/duplicate-id", $"Event id \"{id}\" is declared twice.", new JsonObject { ["path"] = $"/events/{Js.Num(index)}/id" },
                        new JsonObject { ["id"] = id }, ["Give every event a unique id."]);
                }
                seen.Add(id);
                var at = ev.Str("at")!;
                if (!ValidCalendarDate(at) || !double.IsFinite(Js.DateParse(at)))
                {
                    Fail("timeline/invalid-timestamp", $"Event \"{id}\" timestamp \"{at}\" is not a valid date and time.",
                        new JsonObject { ["path"] = $"/events/{Js.Num(index)}/at", ["nodeId"] = id }, new JsonObject { ["at"] = at },
                        ["Use ISO 8601 with an explicit offset, e.g. \"2026-10-01T10:05:00+08:00\"."]);
                }
                var lane = ev.Str("lane");
                if (laneIds.Count > 0 && (lane is null || !laneIds.Has(lane)))
                {
                    Fail("timeline/unknown-lane", lane is null
                            ? $"Event \"{id}\" has no lane, but the timeline declares lanes."
                            : $"Event \"{id}\" names lane \"{lane}\", which is not declared.",
                        new JsonObject { ["path"] = $"/events/{Js.Num(index)}/lane", ["nodeId"] = id },
                        new JsonObject { ["lane"] = lane, ["lanes"] = new JsonArray([.. laneIds.Select(laneId => (JsonNode?)laneId)]) },
                        ["Set lane to one of the declared lane ids.", "Declare the missing lane."]);
                }
            }
            if (problems.Count > 0)
            {
                throw ctx.Diagnostics.ThrowDiagnosticProblems("Timeline validation failed", problems, "timeline/input",
                    new JsonObject { ["diagramType"] = "timeline" }, details);
            }
        }

        // ---- Time zone formatting (:113-133) ----------------------------------------------------
        Js.WallClock Wall(double ms) => Js.ZoneWall(timezone, ms);

        // Offset of the display zone at an instant, in ms (local wall clock - UTC): setUTCFullYear/setUTCHours on a zeroed Date,
        // so a year "0" or "99" is the literal year.
        double ZoneOffset(double ms)
        {
            var w = Wall(ms);
            var local = Js.DaysFromCivil((long)Js.ToNumber(w.Y), (int)Js.ToNumber(w.Mo), (int)Js.ToNumber(w.D)) * DAY
                + Js.ToNumber(w.H) * HOUR + Js.ToNumber(w.Mi) * MINUTE + Js.ToNumber(w.S) * 1000;
            return local - Math.Floor(ms / 1000) * 1000;
        }

        string OffsetLabel(double ms)
        {
            var minutes = Js.Round(ZoneOffset(ms) / MINUTE);
            var sign = minutes < 0 ? "-" : "+";
            var abs = Math.Abs(minutes);
            return $"UTC{sign}{Js.Num(Math.Floor(abs / 60)).PadLeft(2, '0')}:{Js.Num(abs % 60).PadLeft(2, '0')}";
        }

        // `new Date(ms).getUTCMilliseconds()`: the non-negative remainder, so a pre-1970 -250 is .750 (:150-154).
        string ClockLabel(double ms, bool seconds, bool date)
        {
            var w = Wall(ms);
            var fraction = FloorMod(Math.Truncate(ms), 1000);
            return $"{(date ? $"{w.Mo}-{w.D} " : "")}{w.H}:{w.Mi}{(seconds ? $":{w.S}{(fraction != 0 ? $".{Js.Num(fraction).PadLeft(3, '0')}" : "")}" : "")}";
        }

        // Wall-clock ticks across every offset the segment sees (:261-276). Day probes only collect offsets; a candidate from one
        // offset's series is kept when the zone really has that offset there, so a repeated hour keeps both ticks and a skipped one none.
        List<double> WallClockTicks(double start, double end, double interval)
        {
            var offsets = new JsSet<double>();
            offsets.Add(ZoneOffset(start));
            offsets.Add(ZoneOffset(end));
            for (var probe = start + DAY; probe < end; probe += DAY) offsets.Add(ZoneOffset(probe));
            var candidates = new JsSet<double>();
            foreach (var offset in offsets)
            {
                var firstTick = Math.Ceiling((start + offset) / interval) * interval - offset;
                for (var t = firstTick; t <= end; t += interval)
                    if (offsets.Count == 1 || ZoneOffset(t) == offset) candidates.Add(t);
            }
            List<double> ticks = [.. candidates];
            Js.Sort(ticks, (left, right) => left - right);
            return ticks;
        }

        static string DurationLabel(double ms)
        {
            var parts = new List<string>();
            var rest = ms;
            foreach (var (unit, size) in UNITS)
            {
                var value = Math.Floor(rest / size);
                if (value != 0) parts.Add($"{Js.Num(value)} {unit}");
                rest -= value * size;
                if (parts.Count == 2) break;
            }
            return parts.Count > 0 ? string.Join(" ", parts) : "0 s";
        }

        // The visible break label is approximate (it carries ≈), so it rounds to the two most significant adjacent units.
        static string ApproximateDuration(double ms)
        {
            var index = Array.FindIndex(UNITS, unit => ms >= unit.Size);
            if (index == -1) return "0 s";
            var (major, majorSize) = UNITS[index];
            if (index == UNITS.Length - 1) return $"{Js.Num(Js.Round(ms / majorSize))} {major}";
            var (minor, minorSize) = UNITS[index + 1];
            var whole = Math.Floor(ms / majorSize);
            var rest = Js.Round((ms - whole * majorSize) / minorSize);
            if (rest * minorSize >= majorSize) { whole += 1; rest = 0; }
            return rest != 0 ? $"{Js.Num(whole)} {major} {Js.Num(rest)} {minor}" : $"{Js.Num(whole)} {major}";
        }

        double XOf(double time)
        {
            var segment = segments.FirstOrDefault(candidate => time >= candidate.Start && time <= candidate.End) ?? segments[0];
            return Js.Round((segment.X0 + (time - segment.Start) * scale) * 100) / 100;
        }

        // ---- wrapTitle (:280-299) -----------------------------------------------------------------
        static List<string> WrapTitle(string text, double maxWidth)
        {
            var limit = Math.Max(6, Math.Floor(maxWidth / (Layout.TitleFont * ADVANCE * 1.05)));
            var words = Js.SplitKeepingSpaces(text);
            var lines = new List<string>();
            var current = "";
            foreach (var word in words)
            {
                if (Utils.TextUnits(Js.Trim(current + word)) <= limit) { current += word; continue; }
                if (Js.Trim(current).Length > 0) lines.Add(Js.Trim(current));
                current = Js.TrimStart(word);
                while (Utils.TextUnits(current) > limit)
                {
                    var chars = Js.CodePoints(current);
                    var cut = 0;
                    for (var units = 0; cut < chars.Count && units + Utils.TextUnits(chars[cut]) <= limit; cut += 1) units += Utils.TextUnits(chars[cut]);
                    var at = Math.Max(1, cut);
                    lines.Add(string.Concat(chars.Take(at)));
                    current = string.Concat(chars.Skip(at));
                }
            }
            if (Js.Trim(current).Length > 0) lines.Add(Js.Trim(current));
            return lines;
        }

        public JsonObject BuildLayoutReport() => new()
        {
            ["diagram_type"] = "timeline",
            ["viewBox"] = new JsonArray(width, height),
            ["timezone"] = timezone,
            ["scalePxPerMinute"] = Js.Round(scale * MINUTE * 1000) / 1000,
            ["segments"] = new JsonArray([.. segments.Select(segment => (JsonNode?)new JsonObject
            {
                ["start"] = segment.Start, ["end"] = segment.End, ["x0"] = segment.X0, ["x1"] = segment.X1,
            })]),
            ["breaks"] = new JsonArray([.. breaks.Select(gap => (JsonNode?)new JsonObject
            {
                ["from"] = gap.From, ["to"] = gap.To, ["length"] = gap.Length, ["x0"] = gap.X0, ["x1"] = gap.X1,
            })]),
            ["ticks"] = new JsonArray([.. ticks.Select(tick => (JsonNode?)new JsonObject { ["t"] = tick.T, ["x"] = tick.X })]),
            ["lanes"] = new JsonArray([.. laneLayout.Select(lane => (JsonNode?)new JsonObject
            {
                ["id"] = lane.Id, ["y"] = lane.Y, ["height"] = lane.Height, ["lineY"] = lane.LineY,
            })]),
            ["events"] = new JsonArray([.. events.Select(card => (JsonNode?)new JsonObject
            {
                ["id"] = card.Id, ["t"] = card.T, ["lane"] = LaneOf(card), ["x"] = card.X, ["left"] = card.Left, ["top"] = card.Top,
                ["width"] = card.Width, ["height"] = card.Height, ["row"] = card.Row,
            })]),
        };

        // ---- Rendering (:381-531) ------------------------------------------------------------------
        string RenderAxis()
        {
            const double top = Layout.Top;
            var bottom = contentBottom;
            const double lineY = top + Layout.AxisH - 10;
            // Tick labels never overprint each other or a break's duration label. On a multi-day axis a tick names its date
            // only when the date changes from the previous label.
            var taken = new List<(double A, double B)>();
            string? lastDate = null;
            var shown = new List<(Tick Tick, string Label)>();
            foreach (var tick in ticks)
            {
                var w = Wall(tick.T);
                var date = $"{w.Mo}-{w.D}";
                var label = ClockLabel(tick.T, tickSeconds, multiDay && date != lastDate);
                var half = Utils.TextUnits(label) * 10.5 * ADVANCE / 2 + 5;
                var box = (A: tick.X - half, B: tick.X + half);
                if (taken.Any(t => box.A < t.B && t.A < box.B)) continue;
                taken.Add(box);
                lastDate = date;
                shown.Add((tick, label));
            }
            var grid = string.Join("\n", shown.Select(s =>
                $"          <line x1=\"{N(s.Tick.X)}\" y1=\"{N(lineY)}\" x2=\"{N(s.Tick.X)}\" y2=\"{N(bottom)}\" class=\"tl-grid\" stroke-width=\"1\"/>\n"
                + $"          <line x1=\"{N(s.Tick.X)}\" y1=\"{N(lineY - 4)}\" x2=\"{N(s.Tick.X)}\" y2=\"{N(lineY)}\" class=\"tl-axis\" stroke-width=\"1\"/>\n"
                + $"          <text x=\"{N(s.Tick.X)}\" y=\"{N(lineY - 10)}\" class=\"t-muted tl-tick\" font-size=\"10.5\" text-anchor=\"middle\">{Utils.Esc(s.Label)}</text>"));
            var segmentLines = string.Join("\n", segments.Select((segment, index) =>
            {
                var from = index != 0 ? breaks[index - 1].X1 : axisX0 + 8;
                var to = index < breaks.Count ? breaks[index].X0 : axisX1 - 8;
                return $"          <line x1=\"{N(from)}\" y1=\"{N(lineY)}\" x2=\"{N(to)}\" y2=\"{N(lineY)}\" class=\"tl-axis\" stroke-width=\"1\"/>";
            }));
            var breakMarks = string.Join("\n", breaks.Select(gap =>
            {
                var mid = (gap.X0 + gap.X1) / 2;
                static string Zig(double x) => $"M {N(x - 2.5)} {N(lineY - 6)} L {N(x + 2.5)} {N(lineY + 6)}";
                var omitted = ctx.Locale.Translate(locale, "timeline.break", new Dictionary<string, string> { ["duration"] = DurationLabel(gap.Length) });   // lookup only
                return $"          <g data-timeline-break=\"\" data-break-ms=\"{N(gap.Length)}\" aria-label=\"{Utils.Esc(omitted)}\" role=\"img\">\n"
                    + $"            <rect x=\"{N(gap.X0 + 6)}\" y=\"{N(lineY)}\" width=\"{N(gap.Width - 12)}\" height=\"{N(bottom - lineY)}\" fill=\"url(#tl-break-hatch)\" class=\"tl-break-band\"/>\n"
                    + $"            <path d=\"{Zig(gap.X0 + 6)} {Zig(gap.X0 + 10)} {Zig(gap.X1 - 10)} {Zig(gap.X1 - 6)}\" class=\"tl-break-zig\" stroke-width=\"1.3\" stroke-linecap=\"round\" fill=\"none\"/>\n"
                    + $"            <rect x=\"{N(mid - gap.Width / 2 + 4)}\" y=\"{N(lineY - 41)}\" width=\"{N(gap.Width - 8)}\" height=\"17\" rx=\"8.5\" class=\"tl-break-chip\"/>\n"
                    + $"            <text x=\"{N(mid)}\" y=\"{N(lineY - 29)}\" class=\"t-muted\" font-size=\"10\" font-weight=\"700\" text-anchor=\"middle\">{Utils.Esc(gap.Label)}</text>\n"
                    + "          </g>";
            }));
            return $"{grid}\n{segmentLines}\n{breakMarks}";
        }

        string RenderHeader()
        {
            var evidenceKey = meta.Str("evidence");
            var evidence = ctx.Locale.Translate(locale, $"timeline.evidence.{evidenceKey}");
            var evidenceW = Math.Ceiling(Utils.TextUnits(evidence) * 10.5 * ADVANCE + 24);
            var firstWall = Wall(first);
            var date = multiDay ? "" : $" · {firstWall.Y}-{firstWall.Mo}-{firstWall.D}";
            var zone = $"{(timezone == "UTC" ? "UTC" : $"{timezone} ({OffsetLabel(first)})")}{date}";
            var disclosure = breaks.Count > 0
                ? $" · {ctx.Locale.Translate(locale, "timeline.compressed", new Dictionary<string, string> { ["count"] = Js.Num(breaks.Count) })}"   // lookup only
                : "";
            var caption = $"{ctx.Locale.Translate(locale, "timeline.axis")}: {zone}{disclosure}";
            var tone = evidenceKey == "observed" ? "backend" : "messagebus";
            return $"        <g data-timeline-evidence=\"{Utils.Esc(evidenceKey)}\">\n"
                + $"          <rect x=\"{N(Layout.Margin)}\" y=\"14\" width=\"{N(evidenceW)}\" height=\"22\" rx=\"11\" class=\"c-{tone}\" stroke-width=\"1\"/>\n"
                + $"          <circle cx=\"{N(Layout.Margin + 11)}\" cy=\"25\" r=\"3\" class=\"t-{tone}\"/>\n"
                + $"          <text x=\"{N(Layout.Margin + evidenceW / 2 + 5)}\" y=\"29\" class=\"t-{tone}\" font-size=\"10.5\" font-weight=\"700\" text-anchor=\"middle\">{Utils.Esc(evidence)}</text>\n"
                + "        </g>\n"
                + $"        <text data-timeline-axis-caption=\"\" x=\"{N(Layout.Margin + evidenceW + 14)}\" y=\"29.5\" class=\"t-muted\" font-size=\"11.5\">{Utils.Esc(caption)}</text>";
        }

        string RenderLanes() => string.Join("\n", laneLayout.Select((lane, index) =>
            $"        <g data-timeline-lane=\"{Utils.Esc(lane.Id)}\">\n"
            + $"          <rect x=\"{N(Layout.Margin - 8)}\" y=\"{N(lane.Y)}\" width=\"{N(axisX1 - Layout.Margin + 8)}\" height=\"{N(lane.Height)}\" rx=\"10\" class=\"tl-lane{(index % 2 != 0 ? " tl-lane-alt" : "")}\"/>\n"
            + $"          <line x1=\"{N(axisX0 + 8)}\" y1=\"{N(lane.LineY)}\" x2=\"{N(axisX1 - 8)}\" y2=\"{N(lane.LineY)}\" class=\"tl-lane-line\" stroke-width=\"1.5\" stroke-linecap=\"round\"/>\n"
            + $"          {(lane.Label.Length > 0 ? $"<text x=\"{N(Layout.Margin)}\" y=\"{N(lane.LineY + 4.5)}\" class=\"t-primary\" font-size=\"13\" font-weight=\"700\">{Utils.Esc(lane.Label)}</text>" : "")}\n"
            + "        </g>"));

        string RenderEvent(Card card, int order)
        {
            var tone = ToneOf(KindOf(card));
            var lane = laneLayout.FirstOrDefault(entry => entry.Id == LaneOf(card));
            // The Node index groups by the context before " › ", so the lane leads.
            var context = string.Join(" › ", new[] { lane?.Label, $"{ClockLabel(card.T, true, true)} {OffsetLabel(card.T)}" }.Where(part => !string.IsNullOrEmpty(part)));
            var passport = new JsonObject { ["kind"] = tone };
            if (card.Source["detail"] is { } detail) passport["sublabel"] = detail.DeepClone();
            passport["context"] = context;
            var textX = card.Left + card.PadLeft;
            var y = card.Top + 9 + 10;
            var time = $"<text x=\"{N(textX)}\" y=\"{N(y)}\" class=\"t-{tone} tl-time\" font-size=\"{N(Layout.TimeFont)}\" font-weight=\"600\">{Utils.Esc(card.Time)}</text>";
            y += 2;
            var title = new StringBuilder();
            foreach (var line in card.Lines)
            {
                y += Layout.TitleLine;
                title.Append($"<tspan x=\"{N(textX)}\" y=\"{N(y)}\">{Utils.Esc(line)}</tspan>");
            }
            // A tone accent down the card's leading edge carries the event kind.
            var accent = $"<line x1=\"{N(card.Left + 6)}\" y1=\"{N(card.Top + 7)}\" x2=\"{N(card.Left + 6)}\" y2=\"{N(card.Top + card.Height - 7)}\" class=\"tl-accent tl-accent-{tone}\" stroke-width=\"3\" stroke-linecap=\"round\"/>";
            return $"        <g {SvgAttrs.FocusNodeAttrs(card.Id, card.Title, passport, locale, ctx)} data-timeline-at=\"{Utils.Esc(card.At)}\" data-timeline-ms=\"{N(card.T)}\" data-timeline-x=\"{N(card.X)}\">\n"
                + $"          {SvgAttrs.FocusNodeTitle(card.Title, passport)}\n"
                + $"          <rect x=\"{N(card.Left)}\" y=\"{N(card.Top)}\" width=\"{N(card.Width)}\" height=\"{N(card.Height)}\" rx=\"7\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(card.Left)}\" y=\"{N(card.Top)}\" width=\"{N(card.Width)}\" height=\"{N(card.Height)}\" rx=\"7\" class=\"c-{tone} tl-card\"{SvgAttrs.AnimateAttr(meta, "node", order)} stroke-width=\"1\"/>\n"
                + $"          {accent}\n"
                + $"          {time}\n"
                + $"          <text data-node-label=\"\" class=\"t-primary\" font-size=\"{N(Layout.TitleFont)}\" font-weight=\"650\">{title}</text>\n"
                + $"          <circle cx=\"{N(card.X)}\" cy=\"{N(card.LineY)}\" r=\"4.75\" class=\"t-{tone}\"/>\n"
                + "        </g>";
        }

        static string LegendSwatch(LegendEntry entry) =>
            $"<circle cx=\"{N(entry.X + 6)}\" cy=\"{N(entry.Baseline - 4)}\" r=\"4.5\" class=\"t-{ToneOf(entry.Kind)} tl-dot\" stroke-width=\"2\"/>";

        public string RenderSvg()
        {
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(width)} {N(height)}\" {SvgAttrs.SvgRootAttrs(ctx)} data-motion-choreography=\"sweep\" data-timeline-ui=\"\" data-reader-fit=\"intrinsic-height\" data-reader-min-text=\"7.5\">\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "timeline")).Append('\n');
            sb.Append(Utils.RenderDefinitions(breaks.Count > 0
                ? "\n          <pattern id=\"tl-break-hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" class=\"tl-hatch\" stroke-width=\"1\"/></pattern>"
                : "")).Append('\n');
            sb.Append("        <style>\n");
            sb.Append("          svg[data-timeline-ui] .tl-grid { stroke: var(--lane-stroke); stroke-dasharray: 2 4; opacity: .7; }\n");
            sb.Append("          svg[data-timeline-ui] .tl-axis { stroke: var(--text-dim); }\n");
            sb.Append("          svg[data-timeline-ui] .tl-lane { fill: var(--lane-fill); stroke: none; opacity: .75; }\n");
            sb.Append("          svg[data-timeline-ui] .tl-lane-alt { opacity: 0; }\n");
            sb.Append("          svg[data-timeline-ui] .tl-lane-line { stroke: var(--lane-stroke); }\n");
            sb.Append("          svg[data-timeline-ui] .tl-stem { stroke: var(--text-dim); }\n");
            sb.Append("          svg[data-timeline-ui] .tl-card { fill-opacity: .55; stroke-opacity: .5; }\n");
            sb.Append("          svg[data-timeline-ui] .tl-card.c-external { fill: var(--mask); stroke: var(--lane-stroke); stroke-opacity: 1; }\n");
            sb.Append("          svg[data-timeline-ui] .tl-dot { stroke: var(--mask); }\n");
            sb.Append("          svg[data-timeline-ui] .tl-halo { fill: var(--mask); }\n");
            sb.Append("          svg[data-timeline-ui] .tl-break-band { opacity: .55; }\n");
            sb.Append("          svg[data-timeline-ui] .tl-break-chip { fill: var(--mask); stroke: var(--lane-stroke); stroke-width: 1; }\n");
            sb.Append("          svg[data-timeline-ui] .tl-hatch { stroke: var(--lane-stroke); }\n");
            sb.Append("          svg[data-timeline-ui] .tl-break-zig { stroke: var(--text-dim); }\n");
            sb.Append(string.Join("\n", KIND_TONE.Select(entry =>
                $"          svg[data-timeline-ui] .tl-accent-{entry.Tone} {{ stroke: var(--{entry.Tone}-stroke); }}\n"
                + $"          svg[data-timeline-ui] .tl-stem-{entry.Tone} {{ stroke: color-mix(in srgb, var(--{entry.Tone}-stroke) 45%, var(--lane-stroke)); }}\n"
                + $"          svg[data-timeline-ui] .tl-pin-{entry.Tone} {{ fill: var(--{entry.Tone}-fill); stroke: color-mix(in srgb, var(--{entry.Tone}-stroke) 35%, transparent); stroke-width: 1; }}"))).Append('\n');
            sb.Append("          svg[data-timeline-ui] .tl-tick, svg[data-timeline-ui] .tl-time { font-variant-numeric: tabular-nums; }\n");
            sb.Append("        </style>\n");
            sb.Append('\n');
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append('\n');
            sb.Append(RenderHeader()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Lanes -->\n");
            sb.Append(RenderLanes()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Time axis (proportional within each segment; breaks are disclosed) -->\n");
            sb.Append(RenderAxis()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Stems sit behind every card, so a stacked card is never crossed by\n");
            sb.Append("             another event's connector -->\n");
            sb.Append(string.Join("\n", events.Select(card =>
                $"        <line data-detail=\"context\" data-timeline-stem=\"{Utils.Esc(card.Id)}\" x1=\"{N(card.X)}\" y1=\"{N(card.LineY)}\" x2=\"{N(card.X)}\" y2=\"{N(card.Side == "above" ? card.Top + card.Height : card.Top)}\" class=\"tl-stem tl-stem-{ToneOf(KindOf(card))}\" stroke-width=\"1\"/>"))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- One halo layer under every dot, so events seconds apart merge into\n");
            sb.Append("             one outlined capsule instead of cutting crescents into each other -->\n");
            sb.Append(string.Join("\n", events.Select(card =>
                $"        <circle data-detail=\"context\" cx=\"{N(card.X)}\" cy=\"{N(card.LineY)}\" r=\"7.5\" class=\"tl-halo\"/>"))).Append('\n');
            sb.Append(string.Join("\n", events.Select(card =>
                $"        <circle data-detail=\"context\" cx=\"{N(card.X)}\" cy=\"{N(card.LineY)}\" r=\"6.5\" class=\"tl-pin tl-pin-{ToneOf(KindOf(card))}\"/>"))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Events in time order -->\n");
            sb.Append(string.Join("\n", events.Select(RenderEvent))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Legend -->\n");
            sb.Append(legendEntries.Count > 0
                ? Legend.RenderLegend(ctx, legendEntries, new LegendLayout(Layout.Margin, height - 16, width - Layout.Margin * 2, FontSize: 11,
                    MinTitleY: contentBottom + 8, DiagramType: "timeline"), LegendSwatch, locale)
                : "").Append('\n');
            sb.Append("      </svg>");
            return sb.ToString();
        }
    }
}
