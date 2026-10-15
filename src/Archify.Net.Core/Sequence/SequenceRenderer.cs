using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// render-sequence.mjs. The module body (line 13 to the final writeDiagram call) becomes one Scene built per render;
// validateSequence() runs before renderSvg() exactly as lines 1096-1102 do. No layout report: upstream has none.
public sealed class SequenceRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        scene.ValidateSequence();
        return (scene.RenderSvg(), ctx.Diagram.Arr("cards"), null);
    }

    static class ParticipantTextFit
    {
        public const double SublabelPreferred = 7;
        public const double SublabelMinimum = 6;
    }

    // The legend sits below the timeline content: the last message and its note,
    // activation bars, and segment frames. Its block starts LEGEND_CONTENT_GAP
    // below that content; from the block top to the canvas bottom a one-row legend
    // needs LEGEND_BLOCK_HEIGHT (title glyphs, row, and the 54px baseline inset).
    const double LEGEND_CONTENT_GAP = 12;
    const double LEGEND_BLOCK_HEIGHT = 86;
    // A renderer-sized canvas grows to keep the legend clear of late messages and
    // shrinks when the timeline is short. Keep a readable minimum band (lifelineTop
    // 142 + 120px timeline + 65px footer = 327); honor authored canvases below.
    const double SEQUENCE_MIN_AUTO_HEIGHT = 327;

    // A note wraps inside one gap between neighbouring lifelines, so it never
    // crosses an unrelated lifeline or activation bar (#676). Every character
    // stays visible, including in image exports: long unbroken text (a URL or
    // path) breaks after its punctuation, otherwise at any character.
    const double NOTE_FONT_SIZE = 7;
    const double NOTE_LINE_HEIGHT = NOTE_FONT_SIZE + 4;
    // The first line keeps its original position: 12px past the arrow start,
    // which sits 7px from the lifeline.
    const double NOTE_ARROW_INSET = 7;
    const double NOTE_TEXT_INSET = 12;
    // Activation bars reach 5px past a lifeline; keep 6px of air beyond them.
    const double NOTE_LIFELINE_CLEARANCE = 11;
    // Spread columns share one computed gap, but subtracting floating-point column
    // centres can differ in the last digits; such gaps still count as equal.
    const double NOTE_GAP_TOLERANCE = 0.5;
    const string NOTE_BREAK_AFTER = "/.-?&=#_";
    // /^[、。，；：！？）】》」』]+$/u
    const string NOTE_CLOSING_PUNCTUATION = "、。，；：！？）】》」』";

    // LEGEND_CATALOG (render-sequence.mjs:25-37) minus the per-render label; the swatch fields the shared LegendEntry
    // does not carry are looked up by kind in RenderLegend.
    static readonly (string Kind, string ClassName, string Marker, double? StrokeWidth, string? Dash)[] LegendCatalog =
    [
        ("emphasis", "a-emphasis", "arrowhead-emphasis", 1.8, null),
        ("return", "a-default", "arrowhead", null, "3,5"),
        ("security", "a-security", "arrowhead-security", null, null),
        ("dashed", "a-dashed", "arrowhead-dashed", null, null),
        ("default", "a-default", "arrowhead", null, null),
    ];

    // `{ ...participant, index, cx, x, y, width, height, cy }` (render-sequence.mjs:350-362).
    sealed class Participant
    {
        public required JsonObject Source { get; init; }
        public required string Id { get; init; }
        public int Index;
        public double Cx, X, Y, Width, Height, Cy;
        public ResolvedBrand? Brand;
        public string? Type => Source.Str("type");
        public string? Label => Source.Str("label");
        public string? Sublabel => Source.Str("sublabel");
        public static implicit operator Rect(Participant p) => new(p.X, p.Y, p.Width, p.Height);
    }

    sealed class NoteLayout
    {
        public double X, Baseline, Width, Top, Bottom;
        public required List<string> Lines;
    }

    sealed record Occupant(string Kind, string Path, JsonObject Message, Rect Rect);
    sealed record Blocker(JsonObject Message, int Index, string Kind, Rect Rect);

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject sequence;
        readonly JsonObject meta;
        readonly string? locale;
        readonly string? gateProfile;
        readonly List<JsonObject> participantList;
        readonly List<JsonObject> messages;
        readonly List<JsonObject> segments;
        readonly List<JsonObject> activations;
        readonly double readableCanvasWidth;
        readonly double? automaticWidth;
        readonly double canvasWidth;
        readonly string columnFit;
        readonly int participantCount;
        readonly bool readableMessages;
        readonly bool readableAutomaticSublabel;
        readonly double readableSublabelMinimum;
        readonly double readableSublabelPreferred;
        readonly double participantW;
        readonly double colGap;
        readonly double leftX;
        readonly JsMap<string, int> participantIndexById = new();
        readonly JsMap<JsonObject, NoteLayout?> noteLayouts = new();
        readonly double contentBottom;
        readonly double[] viewBox;
        readonly double messageFontSize;
        readonly double messageUnitWidth;
        readonly double lifelineBottom;
        readonly double legendY;
        readonly double labelH;
        readonly string participantBoxWidthNote;
        readonly JsMap<string, Participant> participants = new();
        readonly List<Box> participantBoxes;
        readonly List<Frame> compositionFrames;

        const double TopY = 72;
        // Keep a separate top rail for the 11px semantic sigil and 16px brand mark.
        // Literal labels retain their fitted font size and full authored wording.
        const double ParticipantH = 60;
        const double ParticipantLabelY = 36;
        const double ParticipantSublabelY = 50;
        const double LifelineTop = 142;

        static string N(double v) => Js.Num(v);
        static string S(string? s) => s ?? "undefined";
        // `${node}` of a raw authored field.
        static string T(JsonNode? n) => n is null ? "undefined" : Js.PrimitiveString(n);
        static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
        static double Y(JsonObject message) => message.Num("y") ?? double.NaN;
        static bool HasY(JsonObject message) => message.Num("y") is not null;
        static double From(JsonObject item) => item.Num("from") ?? double.NaN;
        static double To(JsonObject item) => item.Num("to") ?? double.NaN;
        static string? Note(JsonObject message) => message["note"].Truthy() ? Js.PrimitiveString(message["note"]) : null;
        bool Has(string? id) => id is not null && participants.Has(id);
        Participant? Get(string? id) => id is null ? null : participants.Get(id);
        int? IndexOf(string? id) => id is not null && participantIndexById.Has(id) ? participantIndexById.Get(id) : null;
        static double Round1(double v) => Js.Round(v * 10) / 10;
        static JsonObject Bounds(Rect rect) => new()
        {
            ["x"] = Round1(rect.X),
            ["y"] = Round1(rect.Y),
            ["width"] = Round1(rect.Width),
            ["height"] = Round1(rect.Height),
        };
        static List<JsonObject> Objects(JsonNode? array) => array.AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            sequence = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            gateProfile = ctx.Options.Quality?.Name() ?? meta.Str("quality_profile");
            participantList = Objects(sequence["participants"]);
            messages = Objects(sequence["messages"]);
            segments = Objects(sequence["segments"]);
            activations = Objects(sequence["activations"]);

            readableCanvasWidth = Math.Floor(DesktopReadability.DESKTOP_READER_DIAGRAM_WIDTH * ParticipantTextFit.SublabelPreferred / DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX);
            automaticWidth = meta["viewBox"].Truthy() ? null : AutomaticCanvasWidth();

            // Column geometry depends only on the canvas width, which is known before the
            // automatic height is: notes wrap inside lane gaps, and their wrapped height
            // feeds that automatic height.
            // `column_fit: "spread"` widens the lanes with the viewBox instead of keeping
            // the fixed 108px gap, so a wide canvas gains column distance and label room
            // rather than dead space on the right. Spread is the default on both automatic
            // and authored canvases; explicit fixed retains historical coordinates.
            canvasWidth = meta.Arr("viewBox") is { Count: > 0 } authoredBox ? authoredBox[0]!.D() : automaticWidth ?? double.NaN;
            columnFit = Truthy(meta.Str("column_fit")) ? meta.Str("column_fit")! : "spread";
            participantCount = Math.Max(1, participantList.Count);
            // Resolve the actual final-canvas text floor before choosing header geometry.
            // Notes and automatic height must use the resulting column gaps below.
            readableMessages = meta.Str("quality_profile") == "showcase";
            readableAutomaticSublabel = readableMessages && automaticWidth is not null && columnFit == "spread";
            readableSublabelMinimum = readableAutomaticSublabel
                ? Math.Max(ParticipantTextFit.SublabelMinimum, Math.Ceiling(DesktopReadability.MinimumReadableSourceTextPx(canvasWidth) * 10) / 10)
                : ParticipantTextFit.SublabelMinimum;
            readableSublabelPreferred = Math.Max(ParticipantTextFit.SublabelPreferred, readableSublabelMinimum);

            const double preferredSideMargin = 62;
            var spreadGeometry = ParticipantSpreadGeometry();
            participantW = columnFit == "spread" ? spreadGeometry.Width : 86;
            // Narrow feasible frames can reduce the left margin, while ordinary frames
            // keep 62px. Compute card width first so this does not change its sizing rule.
            var sideMargin = columnFit == "spread" ? spreadGeometry.Margin : preferredSideMargin;
            // Fit the authored width when feasible, preserving a real 16px card gutter.
            // Infeasible frames retain that minimum and fail the capacity check below.
            colGap = columnFit == "spread" && participantCount > 1 ? spreadGeometry.Gap : 108;
            leftX = columnFit == "spread" ? sideMargin + participantW / 2 : sideMargin;
            for (var index = 0; index < participantList.Count; index++) participantIndexById.Set(participantList[index].Str("id") ?? "undefined", index);

            contentBottom = 0;
            foreach (var message in messages)
                contentBottom = Math.Max(contentBottom, Note(message) is not null ? NoteLayoutFor(message)?.Bottom ?? Y(message) + 22 : Y(message) + 6);
            foreach (var activation in activations) contentBottom = Math.Max(contentBottom, To(activation));
            foreach (var segment in segments) contentBottom = Math.Max(contentBottom, To(segment));
            // Timeline clearance is independent of legend visibility. Messages need 18px
            // before the lifeline bottom; notes, activations and frames need their footer.
            var timelineRequiredHeight = Js.MaxOf(messages.Select(message => Y(message) + 18 + 65), contentBottom + 65);
            // `legendRequiredHeight(automaticWidth)` with automaticWidth null: `null - 80` is -80.
            var automaticHeight = Math.Max(Math.Max(SEQUENCE_MIN_AUTO_HEIGHT, timelineRequiredHeight), LegendRequiredHeight(automaticWidth ?? 0));
            viewBox = meta["viewBox"].Truthy()
                ? [meta["viewBox"]![0]!.D(), meta["viewBox"]![1]!.D()]
                : [automaticWidth!.Value, automaticHeight];
            // The timeline scales with viewBox height: a taller viewBox gains message room,
            // a shorter one shrinks the readable band (validated below) instead of clipping.

            // Showcase is the fast-authoring default; standard retains legacy label geometry.
            messageFontSize = readableMessages ? 11 : 9;
            messageUnitWidth = readableMessages ? 6.6 : 5.2;
            lifelineBottom = viewBox[1] - 65;
            legendY = viewBox[1] - 54;
            labelH = readableMessages ? 18 : 16;

            participantBoxWidthNote = automaticWidth is not null
                ? $"participant boxes are {N(participantW)}px: the automatic {N(viewBox[0])}px canvas cannot widen further without its 7px sublabels falling below the desktop reading minimum, so keep meta.viewBox omitted"
                : columnFit == "spread"
                    ? $"participant boxes are {N(participantW)}px for this viewBox width and {N(participantCount)} participants"
                    : $"participant boxes are a fixed {N(participantW)}px unless meta.column_fit is \"spread\"";

            for (var index = 0; index < participantList.Count; index++)
            {
                var participant = participantList[index];
                participants.Set(participant.Str("id") ?? "undefined", new Participant
                {
                    Source = participant,
                    Id = participant.Str("id") ?? "undefined",
                    Index = index,
                    Cx = ParticipantX(index),
                    X = ParticipantX(index) - participantW / 2,
                    Y = TopY,
                    Width = participantW,
                    Height = ParticipantH,
                    Cy = TopY + ParticipantH / 2,
                    Brand = ctx.Brands.BrandMarkFor(participant),
                });
            }
            participantBoxes = participants.Values.Select(p => new Box { Id = p.Id, X = p.X, Y = p.Y, Width = p.Width, Height = p.Height, Source = p.Source, Brand = p.Brand }).ToList();

            compositionFrames = segments.Select((segment, index) => new Frame(48, From(segment), viewBox[0] - 96, To(segment) - From(segment),
                Radius: 10, Kind: "segment", Label: segment.Str("label"), Id: JsonValue.Create((double)index))).ToList();
        }

        // ---- Legend ------------------------------------------------------------------
        List<LegendEntry> LegendEntries()
        {
            var catalog = LegendCatalog.Select(entry => new LegendEntry(entry.Kind, ctx.Locale.Translate(locale, $"legend.sequence.{entry.Kind}"),
                Interactive: false, SwatchWidth: 34, SwatchGap: 9)).ToList();
            var presentKinds = messages.Select(message => Truthy(message.Str("variant")) ? message.Str("variant")! : "default");
            return Legend.ResolveLegend(meta.Obj("legend"), catalog, presentKinds);
        }

        double LegendRequiredHeight(double width)
        {
            var entries = LegendEntries();
            if (entries.Count == 0) return 0;
            return Math.Ceiling(contentBottom + LEGEND_CONTENT_GAP + LEGEND_BLOCK_HEIGHT
                + Legend.LegendFootprint(entries, width - 80).ExtraHeight);
        }

        // ---- Canvas width ------------------------------------------------------------
        // A renderer-sized spread canvas widens until every participant label fits,
        // but never past the width where 7px sublabels would project below the
        // desktop reading minimum; an inherently crowded row still fails below.
        double SpreadParticipantWidth(double canvas) => Math.Max(86,
            Math.Min(190, Js.Round((canvas - 124) / Math.Max(1, participantList.Count)) - 24));

        (double Width, double Margin, double Gap) SpreadColumnGeometry(double canvas, double? widthOverride = null)
        {
            var width = widthOverride ?? SpreadParticipantWidth(canvas);
            var count = Math.Max(1, participantList.Count);
            var span = count * width + (count - 1) * 16;
            var margin = Math.Max(40, Math.Min(62, canvas - 40 - span));
            return (width, margin, count > 1 ? Math.Max(width + 16, (canvas - 40 - margin - width) / (count - 1)) : 108);
        }

        // Evaluate the same spread coordinates and full masks used by the renderer;
        // short participant names alone do not imply a compact timeline will fit.
        bool TimelineLabelsFit(double canvas)
        {
            var (width, margin, gap) = SpreadColumnGeometry(canvas);
            var centers = new JsMap<string, double>();
            for (var index = 0; index < participantList.Count; index++) centers.Set(participantList[index].Str("id") ?? "undefined", margin + width / 2 + index * gap);
            var unitWidth = meta.Str("quality_profile") == "showcase" ? 6.6 : 5.2;
            bool CenterHas(string? id) => id is not null && centers.Has(id);
            return messages.All(message =>
            {
                if (!CenterHas(message.Str("from")) || !CenterHas(message.Str("to"))) return true;
                var center = (centers.Get(message.Str("from")!) + centers.Get(message.Str("to")!)) / 2;
                var halfWidth = Math.Max(34, Utils.TextUnits(message.Str("label")) * unitWidth + 12) / 2;
                return center - halfWidth >= 0 && center + halfWidth <= canvas;
            }) && segments.All(segment => 56 + Math.Max(42, Utils.TextUnits(segment.Str("label")) * 5.2 + 14) <= canvas - 48);
        }

        double AutomaticCanvasWidth()
        {
            bool ParticipantsFit(double width) => participantList.All(participant =>
                Utils.TextUnits(participant.Str("label")) * 6.8 <= width + 6
                && (!Truthy(participant.Str("sublabel")) || TextFit.MinimumNodeTextWidth(participant.Str("sublabel"), ParticipantTextFit.SublabelMinimum) <= TextFit.AvailableNodeTextWidth(width)));
            var count = Math.Max(1, participantList.Count);
            var columnFitMode = Truthy(meta.Str("column_fit")) ? meta.Str("column_fit") : "spread";
            if (columnFitMode != "spread") return 920;
            if (ParticipantsFit(SpreadParticipantWidth(920)))
            {
                // Few-participant automatic canvases: keep readable box sizes but drop the
                // empty gutters a 920px spread leaves beside 2–3 lifelines. Authored
                // viewBoxes and the widen-for-labels path below are unchanged.
                if (count <= 3)
                {
                    foreach (var candidate in (double[])[560, 640, 720, 800, 920])
                    {
                        var box = SpreadParticipantWidth(candidate);
                        var span = count * box + (count - 1) * 16;
                        if (box >= 86 && ParticipantsFit(box) && candidate - 80 >= span && TimelineLabelsFit(candidate)) return candidate;
                    }
                }
                return 920;
            }
            var needed = Js.MaxOf(participantList.Select(participant => Math.Max(
                Utils.TextUnits(participant.Str("label")) * 6.8 - 6,
                Truthy(participant.Str("sublabel")) ? TextFit.MinimumNodeTextWidth(participant.Str("sublabel"), ParticipantTextFit.SublabelPreferred) + 86 - TextFit.AvailableNodeTextWidth(86) : 0)));
            return Math.Min(readableCanvasWidth, Math.Max(920, Math.Ceiling((Math.Min(190, needed) + 25) * count + 124)));
        }

        (double Width, double Margin, double Gap) ParticipantSpreadGeometry()
        {
            var nominal = SpreadColumnGeometry(canvasWidth);
            // Preserve every already-fitting row and all authored/fixed geometry. Only
            // a failed primary label opens this bounded use of spare horizontal room.
            if (automaticWidth is null || columnFit != "spread"
                || !participantList.Any(participant => Utils.TextUnits(participant.Str("label")) * 6.8 > nominal.Width + 6)) return nominal;
            var required = Math.Ceiling(Js.MaxOf(participantList.Select(participant => Math.Max(
                Utils.TextUnits(participant.Str("label")) * 6.8 - 6,
                Truthy(participant.Str("sublabel")) ? TextFit.MinimumNodeTextWidth(participant.Str("sublabel"), readableSublabelMinimum)
                    + nominal.Width - TextFit.AvailableNodeTextWidth(nominal.Width) : 0)), nominal.Width));
            var capacity = Math.Floor(Math.Min(190, (canvasWidth - 80 - (participantCount - 1) * 16) / participantCount));
            // Keep the existing real failures if ordinary text or the brand rail cannot
            // fit the minimum candidate. Do not clamp a demand into infeasible geometry.
            // The measured map does not exist yet: resolve the mark from the authored participant.
            if (required > capacity || participantList.Any(participant => BrandMarks.BrandTopRailProblem(ctx.Brands.BrandMarkFor(participant),
                participant.Str("id") ?? "undefined", participant.Str("label"), required, 8, "Participant") is not null)) return nominal;
            return SpreadColumnGeometry(canvasWidth, required);
        }

        double ParticipantX(int index) => leftX + index * colGap;

        // ---- Notes -------------------------------------------------------------------
        static double NoteTextWidth(string text) => TextFit.MinimumNodeTextWidth(text, NOTE_FONT_SIZE);

        // Index after the last punctuation break in `piece`, or its full length (UTF-16 units, as `piece[index - 1]` is).
        static int NoteBreakIndex(string piece)
        {
            for (var index = piece.Length - 1; index > 0; index -= 1)
                if (NOTE_BREAK_AFTER.Contains(piece[index - 1])) return index;
            return piece.Length;
        }

        static List<string> WrapNoteText(string text, double width)
        {
            var original = WrapNoteWords(Js.SpaceTokens(text), width);
            if (!Js.HasHan(text)) return original;

            // Space-only tokens can waste half a line before a Chinese phrase. Split Han
            // words, preserving other runs (identifiers, URLs, +4) and closing punctuation.
            var words = new List<string>();
            foreach (var token in Js.HanTokens(text))
            {
                if (Js.HasHan(token))
                {
                    foreach (var segment in Js.WordBreaks(token)) words.Add(segment);
                }
                else if (token.Length > 0 && token.All(NOTE_CLOSING_PUNCTUATION.Contains) && words.Count > 0)
                {
                    words[^1] += token;
                }
                else
                {
                    words.Add(token);
                }
            }
            var compact = WrapNoteWords(words, width);
            // Never grow a note or change a same-height wrap merely to rebalance lines.
            return compact.Count < original.Count ? compact : original;
        }

        static List<string> WrapNoteWords(List<string> words, double width)
        {
            var lines = new List<string>();
            var line = "";
            void Flush()
            {
                var trimmed = Js.TrimEnd(line);
                if (trimmed.Length > 0) lines.Add(trimmed);
                line = "";
            }
            foreach (var word in words)
            {
                // /^\s+$/u
                if (word.Length > 0 && word.All(c => Js.IsSpace(c)))
                {
                    if (line.Length > 0) line += " ";
                    continue;
                }
                if (NoteTextWidth(line + word) <= width)
                {
                    line += word;
                    continue;
                }
                Flush();
                var piece = "";
                foreach (var segment in Js.Graphemes(word))
                {
                    while (piece.Length > 0 && NoteTextWidth(piece + segment) > width)
                    {
                        var cut = NoteBreakIndex(piece);
                        lines.Add(piece[..cut]);
                        piece = piece[cut..];
                    }
                    piece += segment;
                }
                line = piece;
            }
            Flush();
            return lines;
        }

        // The widest gap the message spans; equal gaps resolve to the left-most one,
        // where a note has always started, so notes keep their original position.
        int NoteGapIndex(int fromIndex, int toIndex)
        {
            var low = Math.Min(fromIndex, toIndex);
            var high = Math.Max(fromIndex, toIndex);
            (int Gap, double Width)? best = null;
            for (var gap = low; gap < high; gap += 1)
            {
                var width = ParticipantX(gap + 1) - ParticipantX(gap);
                if (best is null || width > best.Value.Width + NOTE_GAP_TOLERANCE) best = (gap, width);
            }
            return best!.Value.Gap;
        }

        Rect? NoteRect(JsonObject message)
        {
            var note = NoteLayoutFor(message);
            if (note is null) return null;
            return new Rect(note.X, note.Top, note.Width, note.Bottom - note.Top);
        }

        NoteLayout? NoteLayoutFor(JsonObject message)
        {
            if (noteLayouts.Has(message)) return noteLayouts.Get(message);
            NoteLayout? result = null;
            var fromIndex = IndexOf(message.Str("from"));
            var toIndex = IndexOf(message.Str("to"));
            var text = Note(message);
            if (text is not null && HasY(message) && fromIndex is { } from && toIndex is { } to && from != to)
            {
                var gap = NoteGapIndex(from, to);
                var x = ParticipantX(gap) + NOTE_ARROW_INSET + NOTE_TEXT_INSET;
                var maxWidth = Math.Max(1, ParticipantX(gap + 1) - NOTE_LIFELINE_CLEARANCE - x);
                var lines = NoteTextWidth(text) <= maxWidth ? [text] : WrapNoteText(text, maxWidth);
                var baseline = Y(message) + 18;
                result = new NoteLayout
                {
                    X = x,
                    Baseline = baseline,
                    Lines = lines,
                    Width = Js.MaxOf(lines.Select(NoteTextWidth)),
                    // Include fallback-font ascent in the same bounds used for title clearance.
                    Top = baseline - NOTE_FONT_SIZE * 1.2,
                    Bottom = Y(message) + 22 + (lines.Count - 1) * NOTE_LINE_HEIGHT,
                };
            }
            noteLayouts.Set(message, result);
            return result;
        }

        // ---- Message geometry --------------------------------------------------------
        (double Start, double End, double Center)? MessageGeometry(JsonObject message)
        {
            var from = Get(message.Str("from"));
            var to = Get(message.Str("to"));
            if (from is null || to is null || !HasY(message)) return null;
            var direction = to.Cx > from.Cx ? 1 : -1;
            var start = from.Cx + direction * 7;
            var end = to.Cx - direction * 7;
            return (start, end, (start + end) / 2);
        }

        LabelRect? MessageLabelBox(JsonObject message, int? relationIndex = null)
        {
            if (MessageGeometry(message) is not { } geometry) return null;
            var width = Math.Max(34, Utils.TextUnits(message.Str("label")) * messageUnitWidth + 12);
            return new LabelRect
            {
                Relation = message,
                RelationIndex = relationIndex,
                Label = message.Str("label"),
                X = geometry.Center - width / 2,
                Y = Y(message) - 20,
                Width = width,
                Height = labelH,
            };
        }

        Rect? MessageRouteBox(JsonObject message)
        {
            if (MessageGeometry(message) is not { } geometry) return null;
            return new Rect(Math.Min(geometry.Start, geometry.End), Y(message) - 2, Math.Abs(geometry.End - geometry.Start), 4);
        }

        Rect? LabelRectOf(JsonObject message) => MessageLabelBox(message) is { } box ? (Rect?)(Rect)box : null;
        List<Rect> MessageRects(JsonObject message) => new Rect?[] { LabelRectOf(message), MessageRouteBox(message), NoteRect(message) }.OfType<Rect>().ToList();

        // Message labels, routes and notes a segment label must not cover, with the
        // source path of each so a failure can name what it hides.
        List<Occupant> SegmentLabelOccupants()
        {
            var occupants = new List<Occupant>();
            for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                var message = messages[messageIndex];
                if (LabelRectOf(message) is { } label) occupants.Add(new("message label", $"/messages/{N(messageIndex)}/label", message, label));
                if (MessageRouteBox(message) is { } route) occupants.Add(new("message route", $"/messages/{N(messageIndex)}/y", message, route));
                if (NoteRect(message) is { } note) occupants.Add(new("note", $"/messages/{N(messageIndex)}/note", message, note));
            }
            return occupants;
        }

        Rect LegacySegmentLabelBox(JsonObject segment)
        {
            var labelW = Math.Max(42, Utils.TextUnits(segment.Str("label")) * 5.2 + 14);
            var occupied = SegmentLabelOccupants();
            var label = new Rect(56, From(segment) - 22, labelW, 18);
            for (var attempt = 0; attempt < 4; attempt += 1)
            {
                if (!occupied.Any(occupant => Geometry.RectsOverlap(label, occupant.Rect, 2))) break;
                label = label with { Y = label.Y - 22 };
            }
            if (!SegmentLabelMisplaced(label, segment)) return label;
            // Abutting segments can leave no room on the border: the badge climbed
            // behind the participant headers or into the previous segment, where it
            // names the wrong phase. Its own frame's top-left corner, clear of messages,
            // names the right one.
            // Another segment's title normally sits on that segment's own top border,
            // so its strip is taken too.
            var otherTitles = segments.Where(other => !ReferenceEquals(other, segment))
                .Select(other => new Rect(56, From(other) - 22, Math.Max(42, Utils.TextUnits(other.Str("label")) * 5.2 + 14), 18)).ToList();
            var inside = new Rect(56, From(segment) + 4, labelW, 18);
            for (var attempt = 0; attempt < 4 && inside.Y + inside.Height <= To(segment) - 2; attempt += 1)
            {
                if (!SegmentLabelMisplaced(inside, segment)
                    && !occupied.Select(occupant => occupant.Rect).Concat(otherTitles).Concat(participants.Values.Select(p => (Rect)p))
                        .Any(rect => Geometry.RectsOverlap(inside, rect, 2))) return inside;
                inside = inside with { Y = inside.Y + 22 };
            }
            return label;
        }

        Rect SegmentLabelBox(JsonObject segment)
        {
            var label = LegacySegmentLabelBox(segment);
            var headers = participants.Values.Select(p => (Rect)p).ToList();
            var coveredHeaders = headers.Where(header => Geometry.RectsOverlap(label, header, 0)).ToList();
            if (coveredHeaders.Count == 0 || label.Y + label.Height > From(segment)) return label;

            var others = segments.Where(other => !ReferenceEquals(other, segment)).ToList();
            // Only the unique first, non-overlapping phase gets this local adjustment.
            // Nested phases keep their authored bounds and the existing title policy.
            if (others.Any(other => From(other) <= From(segment)
                || (From(other) < To(segment) && To(other) > From(segment)))) return label;

            var candidate = label with { Y = Js.MaxOf(coveredHeaders.Select(header => header.Y + header.Height)) + 2 };
            var bottom = candidate.Y + candidate.Height;
            // The badge must still touch its own top border, before the first message;
            // moving it deep into the phase would name the wrong part of the timeline.
            if (candidate.Y > From(segment) || bottom < From(segment) || bottom > To(segment) - 2) return label;

            var obstacles = new List<Rect>();
            obstacles.AddRange(SegmentLabelOccupants().Select(occupant => occupant.Rect));
            obstacles.AddRange(headers);
            // Use actual legacy placements: another title may have climbed above its
            // nominal slot. Calling the unchanged helper keeps this non-recursive.
            obstacles.AddRange(others.Select(LegacySegmentLabelBox));
            if (!obstacles.Any(rect => Geometry.RectsOverlap(candidate, rect, 2))) return candidate;

            // Only this blocked header-clear strip gains a horizontal fallback. Keep
            // successful legacy placements intact, including their activation policy.
            var fallbackObstacles = new List<Rect>(obstacles);
            foreach (var activation in activations)
            {
                if (Get(activation.Str("participant")) is not { } participant) continue;
                fallbackObstacles.Add(new Rect(participant.Cx - 5, From(activation), 10, To(activation) - From(activation)));
            }
            // Every free interval on this strip starts at the left frame inset or just
            // beyond an obstacle. Scan those finite boundaries instead of pixel steps.
            var boundarySet = new JsSet<double>();
            boundarySet.Add(candidate.X);
            foreach (var rect in fallbackObstacles)
            {
                if (candidate.Y < rect.Y + rect.Height + 2 && rect.Y < candidate.Y + candidate.Height + 2) boundarySet.Add(rect.X + rect.Width + 2);
            }
            var boundaries = boundarySet.Where(x => x >= candidate.X && x + candidate.Width <= viewBox[0] - 48).ToList();
            Js.Sort(boundaries, (a, b) => a - b);
            foreach (var x in boundaries)
            {
                var shifted = candidate with { X = x };
                if (!fallbackObstacles.Any(rect => Geometry.RectsOverlap(shifted, rect, 2))) return shifted;
            }
            return label;
        }

        // Whether moving one message to `y` keeps it readable: inside the timeline,
        // 28px from messages sharing its horizontal span, and clear of every other
        // label, route and note. Used to offer only repairs that hold.
        bool MessageFitsAt(int messageIndex, double y)
        {
            if (y < LifelineTop + 18) return false;
            // An automatic canvas recalculates its height after this edit; an authored
            // canvas must still fit the suggested position without a second repair.
            if (meta["viewBox"].Truthy() && y > lifelineBottom - 18) return false;
            (double Left, double Right) Span(JsonObject message)
            {
                var from = Get(message.Str("from"))?.Cx ?? double.NaN;
                var to = Get(message.Str("to"))?.Cx ?? double.NaN;
                return (Math.Min(from, to), Math.Max(from, to));
            }
            var moved = messages[messageIndex].DeepClone().AsObject();
            moved["y"] = y;
            var (left, right) = Span(moved);
            var movedRects = MessageRects(moved);
            for (var otherIndex = 0; otherIndex < messages.Count; otherIndex++)
            {
                var other = messages[otherIndex];
                if (otherIndex == messageIndex || !HasY(other)) continue;
                var (otherLeft, otherRight) = Span(other);
                if (Math.Abs(Y(other) - y) < 28 && left < otherRight && otherLeft < right) return false;
                var otherRects = MessageRects(other);
                if (movedRects.Any(rect => otherRects.Any(otherRect => Geometry.RectsOverlap(rect, otherRect, 2)))) return false;
            }
            return true;
        }

        // The first occupant the segment label still covers after its upward steps.
        Occupant? SegmentLabelBlocker(JsonObject segment)
        {
            var label = SegmentLabelBox(segment);
            return SegmentLabelOccupants().FirstOrDefault(occupant => Geometry.RectsOverlap(label, occupant.Rect, 2));
        }

        // A badge on its frame's top border is the intended title. It stops naming
        // that frame once it hides mostly behind the participant headers, or once
        // message labels pushed it up into another segment.
        bool SegmentLabelMisplaced(Rect label, JsonObject segment)
        {
            var half = label.Height / 2;
            if (TopY + ParticipantH - label.Y > half) return true;
            if (label.Y >= From(segment) - 22) return false;
            return segments.Any(other => !ReferenceEquals(other, segment)
                && Math.Min(label.Y + label.Height, To(other)) - Math.Max(label.Y, From(other)) > half);
        }

        List<Pt> MessagePath(JsonObject message) =>
            Has(message.Str("from")) && Has(message.Str("to"))
                ? [new Pt(Get(message.Str("from"))!.Cx, Y(message)), new Pt(Get(message.Str("to"))!.Cx, Y(message))]
                : [];

        // ---- Validation --------------------------------------------------------------
        public void ValidateSequence()
        {
            var problems = new List<string>();
            var diagnostics = new List<Diagnostic>();
            if (participants.Count != participantList.Count) problems.Add("Participant ids must be unique.");

            if (lifelineBottom - LifelineTop < 120)
                problems.Add($"viewBox height {N(viewBox[1])} leaves under 120px of timeline — set meta.viewBox[1] to at least {N(LifelineTop + 120 + 65)}.");

            foreach (var participant in participants.Values)
            {
                var participantIndex = participantList.FindIndex(entry => (entry.Str("id") ?? "undefined") == participant.Id);
                void ParticipantProblem(string field, string message, JsonObject evidence, IReadOnlyList<string> supportedFixes)
                {
                    problems.Add(message);
                    var subject = new JsonObject { ["diagramType"] = "sequence" };
                    if (participant.Source["id"] is { } nodeId) subject["nodeId"] = nodeId.DeepClone();
                    subject["path"] = $"/participants/{N(participantIndex)}/{field}";
                    var fullEvidence = new JsonObject { ["viewBoxWidth"] = viewBox[0], ["participantWidth"] = participantW };
                    foreach (var (key, value) in evidence) fullEvidence[key] = value?.DeepClone();
                    diagnostics.Add(new($"sequence/participant-{field}-overflow", Severity.Error, message, subject, fullEvidence, supportedFixes, null));
                }
                IReadOnlyList<string> Fixes(string first) => columnFit == "fixed"
                    ? [first, "set meta.column_fit to \"spread\" if fixed coordinates are not required"]
                    : [first];
                var estLabelW = Utils.TextUnits(participant.Label) * 6.8;
                if (estLabelW > participantW + 6)
                {
                    var message = automaticWidth is not null
                        ? $"Label \"{S(participant.Label)}\" (~{N(Js.Round(estLabelW))}px) is wider than component \"{participant.Id}\" — shorten it to at most {N(Math.Floor((participantW + 6) / 6.8))} text units (CJK counts 2; {participantBoxWidthNote})."
                        : $"Label \"{S(participant.Label)}\" (~{N(Js.Round(estLabelW))}px) is wider than component \"{participant.Id}\" ({N(participantW)}px) — shorten the label or widen the participant box.";
                    var evidence = new JsonObject();
                    if (participant.Source["label"] is { } text) evidence["text"] = text.DeepClone();
                    evidence["requiredWidth"] = estLabelW;
                    evidence["availableWidth"] = participantW + 6;
                    ParticipantProblem("label", message, evidence, Fixes("shorten the participant label while preserving its role"));
                }
                var brandRailProblem = BrandMarks.BrandTopRailProblem(participant.Brand, participant.Id, participant.Label, participantW, 8, "Participant");
                if (brandRailProblem is not null) problems.Add(brandRailProblem);
                // sublabel renders as a single unwrapped <text>; shrink-to-fit handles the
                // ordinary case, this rejects what it cannot rescue.
                if (Truthy(participant.Sublabel))
                {
                    var availableTextW = TextFit.AvailableNodeTextWidth(participantW);
                    var minimumW = TextFit.MinimumNodeTextWidth(participant.Sublabel, readableSublabelMinimum);
                    if (minimumW > availableTextW)
                    {
                        var minimumDescription = readableAutomaticSublabel
                            ? $"{N(readableSublabelMinimum)}px minimum that stays readable on this {N(viewBox[0])}px canvas"
                            : $"{N(readableSublabelMinimum)}px legible minimum";
                        var message = $"Sublabel \"{participant.Sublabel}\" needs ~{N(Math.Ceiling(minimumW))}px at the {minimumDescription}, but participant \"{participant.Id}\" provides {N(availableTextW)}px — shorten the sublabel ({participantBoxWidthNote}).";
                        var evidence = new JsonObject
                        {
                            ["text"] = participant.Sublabel,
                            ["requiredWidth"] = minimumW,
                            ["availableWidth"] = availableTextW,
                            ["minimumFontPx"] = readableSublabelMinimum,
                        };
                        ParticipantProblem("sublabel", message, evidence,
                            Fixes("shorten the participant sublabel while preserving its role or protocol; move supplementary detail into a card"));
                    }
                }
            }

            var participantOrder = new JsMap<string, int>();
            for (var index = 0; index < participantList.Count; index++) participantOrder.Set(participantList[index].Str("id") ?? "undefined", index);
            int OrderOf(string? id) => id is not null && participantOrder.Has(id) ? participantOrder.Get(id) : 0;
            for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                var message = messages[messageIndex];
                var label = message.Str("label");
                foreach (var (field, endpoint) in (ValueTuple<string, string>[])[("from", "source"), ("to", "target")])
                {
                    if (Has(message.Str(field))) continue;
                    var problem = $"Message \"{S(label)}\" references unknown {endpoint} \"{T(message[field])}\".";
                    var otherField = field == "from" ? "to" : "from";
                    var anchorOrder = OrderOf(message.Str(otherField));
                    var candidates = participantOrder.Keys.Where(id => id != message.Str(otherField)).ToList();
                    Js.Sort(candidates, (a, b) => Math.Abs(OrderOf(a) - anchorOrder) - Math.Abs(OrderOf(b) - anchorOrder));
                    var subject = new JsonObject { ["diagramType"] = "sequence", ["message"] = message["label"]?.DeepClone(), ["path"] = $"/messages/{N(messageIndex)}/{field}" };
                    if (message["from"] is { } from) subject["from"] = from.DeepClone();
                    if (message["to"] is { } to) subject["to"] = to.DeepClone();
                    var evidence = new JsonObject { ["endpoint"] = endpoint };
                    if (message[field] is { } unknown) evidence["unknownNodeId"] = unknown.DeepClone();
                    evidence["availableNodeIds"] = new JsonArray([.. candidates.Select(id => (JsonNode?)id)]);
                    diagnostics.Add(new("sequence/unknown-endpoint", Severity.Error, problem, subject, evidence,
                        [.. candidates.Take(3).Select(id => $"set /messages/{N(messageIndex)}/{field} to verified node id \"{id}\"")], null));
                    problems.Add(problem);
                }
                if (!HasY(message)) problems.Add($"Message \"{S(label)}\" must provide a numeric y.");
                if (Y(message) < LifelineTop + 18 || Y(message) > lifelineBottom - 18)
                    problems.Add($"Message \"{S(label)}\" sits outside the readable timeline — keep y between {N(LifelineTop + 18)} and {N(lifelineBottom - 18)}.");
                if (Has(message.Str("from")) && Has(message.Str("to")))
                {
                    if (message.Str("from") == message.Str("to"))
                    {
                        var problem = $"Message \"{S(label)}\" is a self-message on participant \"{message.Str("from")}\"; this Sequence renderer supports only messages between distinct participants. Participant spacing cannot repair it. Preserve the internal step's meaning and order in a supported representation such as a note on a real message or a card; do not invent a participant.";
                        var subject = new JsonObject { ["diagramType"] = "sequence" };
                        if (message["label"] is { } labelNode) subject["message"] = labelNode.DeepClone();
                        subject["collection"] = "messages";
                        subject["index"] = messageIndex;
                        subject["path"] = $"/messages/{N(messageIndex)}";
                        if (message["id"].Truthy()) subject["id"] = message["id"]!.DeepClone();
                        subject["from"] = message["from"]!.DeepClone();
                        subject["to"] = message["to"]!.DeepClone();
                        subject["fromPath"] = $"/messages/{N(messageIndex)}/from";
                        subject["toPath"] = $"/messages/{N(messageIndex)}/to";
                        var evidence = new JsonObject
                        {
                            ["participant"] = message["from"]!.DeepClone(),
                            ["participantPath"] = $"/participants/{N(OrderOf(message.Str("from")))}",
                        };
                        if (message["y"] is { } yNode) evidence["y"] = yNode.DeepClone();
                        evidence["supportedMessageGeometry"] = "horizontal-between-distinct-participants";
                        diagnostics.Add(new("sequence/self-message-unsupported", Severity.Error, problem, subject, evidence, [], null));
                        problems.Add(problem);
                    }
                    else
                    {
                        var distance = Math.Abs(Get(message.Str("to"))!.Cx - Get(message.Str("from"))!.Cx);
                        if (distance < 60) problems.Add($"Message \"{S(label)}\" spans {N(Js.Round(distance))}px (minimum 60px) — give its participants more column distance.");
                    }
                }
            }

            var sink = ctx.Diagnostics;
            var relations = sequence["messages"];
            var endpointIds = new JsSet<string>();
            foreach (var id in participants.Keys) endpointIds.Add(id);
            // Participant headers are opaque nodes. Lifelines, activation bars, and
            // segment bands remain intentional pass-through geometry and are excluded.
            problems.AddRange(Geometry.CleanFlowProblems(sink, relations, participantBoxes, MessagePath, "sequence", "messages", "participant header",
                clearance: 0,
                routeHint: "move the message y below the participant headers or reorder participants"));
            problems.AddRange(Geometry.CleanCrossingProblems(sink, relations, endpointIds, MessagePath, "sequence", "messages",
                profile: gateProfile,
                routeHint: "separate the message y values; lifeline crossings remain allowed"));
            problems.AddRange(Geometry.CleanAmbiguousCorridorProblems(sink, relations, endpointIds, MessagePath, "sequence", "messages",
                profile: gateProfile,
                routeHint: "separate the message y values so unrelated messages do not visually merge"));
            problems.AddRange(Geometry.CleanBorderRunProblems(sink, relations, endpointIds, compositionFrames, MessagePath, "sequence", "messages",
                gateProfile,
                routeHint: "move the message y so it crosses a segment boundary perpendicularly or stays clearly inside the segment"));
            problems.AddRange(Geometry.CleanRouteRhythmProblems(sink, relations, endpointIds, MessagePath, "sequence", "messages",
                gateProfile,
                routeHint: "increase participant spacing or simplify message routing so every turn has room to read"));

            // Vertical crowding only matters when the arrows share horizontal space;
            // disjoint arrows may legitimately run in parallel rows.
            var placed = messages
                .Where(m => Has(m.Str("from")) && Has(m.Str("to")))
                .Select(m => (Label: m.Str("label"), Y: Y(m),
                    X1: Math.Min(Get(m.Str("from"))!.Cx, Get(m.Str("to"))!.Cx),
                    X2: Math.Max(Get(m.Str("from"))!.Cx, Get(m.Str("to"))!.Cx)))
                .ToList();
            Js.Sort(placed, (a, b) => a.Y - b.Y);
            for (var i = 0; i < placed.Count; i += 1)
            {
                for (var j = i + 1; j < placed.Count && placed[j].Y - placed[i].Y < 28; j += 1)
                {
                    if (placed[i].X1 < placed[j].X2 && placed[j].X1 < placed[i].X2)
                        problems.Add($"Messages \"{S(placed[i].Label)}\" and \"{S(placed[j].Label)}\" are less than 28px apart and share horizontal space — spread their y values.");
                }
            }

            // Label masks can extend well past the arrow span, so check the actual
            // label rectangles too — tangent arrows with long labels still collide.
            var labelRects = messages.Select((m, messageIndex) => MessageLabelBox(m, messageIndex)).OfType<LabelRect>().ToList();
            for (var i = 0; i < labelRects.Count; i += 1)
            {
                for (var j = i + 1; j < labelRects.Count; j += 1)
                {
                    if (Geometry.RectsOverlap(labelRects[i], labelRects[j], -2))
                        problems.Add($"Labels \"{S(labelRects[i].Label)}\" and \"{S(labelRects[j].Label)}\" overlap — spread their message y values or shorten the labels.");
                }
            }
            problems.AddRange(Geometry.CleanLabelRouteClearanceProblems(sink, relations, labelRects, endpointIds, MessagePath, "sequence", "messages",
                gateProfile,
                routeHint: "spread the message y values, shorten the label, or reorder participants so the adjacent route stays visible"));
            problems.AddRange(Geometry.CleanLabelCanvasContainmentProblems(sink, labelRects, viewBox, "sequence", "messages",
                gateProfile,
                routeHint: "shorten the label, reorder participants, or enlarge meta.viewBox"));

            // A wrapped note keeps every character (#676), so a note that still reaches a
            // later message or the canvas bottom needs the diagram changed; authored y
            // values and viewBox stay as written. Showcase only, so standard keeps
            // accepting existing diagrams.
            void Report(string problem, string code, JsonObject subject, JsonObject evidence, IReadOnlyList<string> supportedFixes)
            {
                problems.Add(problem);
                diagnostics.Add(new(code, Severity.Error, problem, subject, evidence, supportedFixes, null));
            }
            if (readableMessages)
            {
                var noteFloor = lifelineBottom + 20;
                for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
                {
                    var message = messages[messageIndex];
                    var note = NoteLayoutFor(message);
                    if (note is null) continue;
                    var label = message.Str("label");
                    var lines = note.Lines.Count == 1 ? "1 line" : $"{N(note.Lines.Count)} lines";
                    var bottom = Math.Ceiling(note.Bottom);
                    var ownRect = NoteRect(message)!.Value;
                    var subject = new JsonObject { ["diagramType"] = "sequence", ["message"] = message["label"]?.DeepClone(), ["path"] = $"/messages/{N(messageIndex)}/note" };
                    if (message["from"] is { } from) subject["from"] = from.DeepClone();
                    if (message["to"] is { } to) subject["to"] = to.DeepClone();
                    if (note.Bottom > noteFloor)
                    {
                        // A visible legend needs its own room below the note as well.
                        var requiredHeight = Math.Max(Math.Ceiling(viewBox[1] + note.Bottom - noteFloor), LegendRequiredHeight(viewBox[0]));
                        var fix = meta["viewBox"].Truthy()
                            ? $"set /meta/viewBox/1 to {N(requiredHeight)}"
                            : $"set /meta/viewBox to [{N(viewBox[0])}, {N(requiredHeight)}]";
                        Report($"Note on message \"{S(label)}\" wraps to {lines} and ends at y={N(bottom)}, below the canvas content limit y={N(noteFloor)} — set meta.viewBox[1] to at least {N(requiredHeight)}, move the message up, or shorten the note.",
                            "sequence/note-canvas-limit", subject,
                            new JsonObject
                            {
                                ["lines"] = note.Lines.Count, ["note"] = Bounds(ownRect), ["canvasLimitY"] = noteFloor,
                                ["viewBoxHeight"] = viewBox[1], ["requiredViewBoxHeight"] = requiredHeight,
                            },
                            [fix]);
                    }
                    var blockers = new List<Blocker>();
                    for (var otherIndex = 0; otherIndex < messages.Count; otherIndex++)
                    {
                        var other = messages[otherIndex];
                        if (otherIndex == messageIndex || !HasY(other) || Y(other) <= Y(message)) continue;
                        var hit = new (string Kind, Rect? Rect)[]
                        {
                            ("message label", LabelRectOf(other)),
                            ("message route", MessageRouteBox(other)),
                            ("note", NoteRect(other)),
                        }.FirstOrDefault(entry => entry.Rect is { } rect && Geometry.RectsOverlap(ownRect, rect, 2));
                        if (hit.Rect is { } hitRect) blockers.Add(new(other, otherIndex, hit.Kind, hitRect));
                    }
                    Js.Sort(blockers, (a, b) => Y(a.Message) - Y(b.Message));
                    if (blockers.Count == 0) continue;
                    var blocker = blockers[0];
                    var requiredY = Math.Ceiling(note.Bottom + 22);
                    var blockerPath = $"/messages/{N(blocker.Index)}/y";
                    var blockerLabel = blocker.Message.Str("label");
                    var blockerEvidence = new JsonObject { ["message"] = blocker.Message["label"]?.DeepClone(), ["path"] = blockerPath };
                    if (blocker.Message["y"] is { } blockerY) blockerEvidence["y"] = blockerY.DeepClone();
                    blockerEvidence["kind"] = blocker.Kind;
                    blockerEvidence["bounds"] = Bounds(blocker.Rect);
                    Report($"Note on message \"{S(label)}\" wraps to {lines} (down to y={N(bottom)}) and reaches message \"{S(blockerLabel)}\" at y={T(blocker.Message["y"])} — move \"{S(blockerLabel)}\" and later messages down so it sits at y={N(requiredY)} or below, or shorten the note.",
                        "sequence/note-overlap", subject,
                        new JsonObject
                        {
                            ["lines"] = note.Lines.Count,
                            ["note"] = Bounds(ownRect),
                            ["blocker"] = blockerEvidence,
                            ["requiredY"] = requiredY,
                        },
                        MessageFitsAt(blocker.Index, requiredY) ? [$"set {blockerPath} to {N(requiredY)}"] : []);
                }
            }

            for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                var label = segment.Str("label");
                if (To(segment) <= From(segment))
                    problems.Add($"Segment \"{S(label)}\" has invalid y range (from {T(segment["from"])} to {T(segment["to"])}) — \"to\" must be greater than \"from\".");
                if (From(segment) < TopY || To(segment) > lifelineBottom + 20)
                    problems.Add($"Segment \"{S(label)}\" extends outside the canvas — keep its y range between {N(TopY)} and {N(lifelineBottom + 20)}.");
                // A frame edge may pass behind a masked message label, but an edge that
                // runs along the arrow itself leaves the reader unable to tell which phase
                // the message belongs to.
                if (meta.Str("quality_profile") == "showcase")
                {
                    foreach (var edge in (string[])["from", "to"])
                    {
                        var border = segment.Num(edge) ?? double.NaN;
                        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
                        {
                            var message = messages[messageIndex];
                            if (!HasY(message) || !(Math.Abs(border - Y(message)) < 4)) continue;
                            var messageY = Y(message);
                            var problem = $"Segment \"{S(label)}\" {(edge == "from" ? "top" : "bottom")} edge at y {T(segment[edge])} runs along message \"{S(message.Str("label"))}\" (arrow at y {T(message["y"])}) — move the edge to at least {N(messageY + 4)} to leave the message above it, or to at most {N(messageY - 4)} to leave it below.";
                            problems.Add(problem);
                            var evidence = new JsonObject();
                            if (segment["label"] is { } segmentLabel) evidence["segmentLabel"] = segmentLabel.DeepClone();
                            evidence["edge"] = edge;
                            evidence["borderY"] = segment[edge]!.DeepClone();
                            evidence["messageIndex"] = messageIndex;
                            if (message["label"] is { } messageLabel) evidence["messageLabel"] = messageLabel.DeepClone();
                            evidence["messageY"] = message["y"]!.DeepClone();
                            evidence["minimumClearancePx"] = 4;
                            diagnostics.Add(new("sequence/segment-message-border-run", Severity.Error, problem,
                                new JsonObject { ["diagramType"] = "sequence", ["collection"] = "segments", ["index"] = segmentIndex, ["path"] = $"/segments/{N(segmentIndex)}/{edge}" },
                                evidence,
                                [$"set /segments/{N(segmentIndex)}/{edge} to at least {N(messageY + 4)} to leave the message above it", $"set /segments/{N(segmentIndex)}/{edge} to at most {N(messageY - 4)} to leave the message below it"],
                                null));
                        }
                    }
                }
                var labelBox = SegmentLabelBox(segment);
                for (var otherIndex = 0; otherIndex < segments.Count; otherIndex++)
                {
                    var other = segments[otherIndex];
                    if (ReferenceEquals(other, segment) || otherIndex < segmentIndex) continue;
                    if (Geometry.RectsOverlap(labelBox, SegmentLabelBox(other), 0))
                        problems.Add($"Segment labels \"{S(label)}\" and \"{S(other.Str("label"))}\" overlap — leave more room between the segments' messages near their shared border, or shorten a label.");
                }
                var availableWidth = Math.Max(0, viewBox[0] - 48 - labelBox.X);
                if (labelBox.X + labelBox.Width > viewBox[0] - 48)
                {
                    var requiredWidth = Math.Ceiling(labelBox.X + labelBox.Width + 48);
                    problems.Add($"Segment \"{S(label)}\" label (~{N(Js.Round(labelBox.Width))}px) exceeds the segment frame's available width ({N(availableWidth)}px) — shorten the label or increase meta.viewBox[0] to at least {N(requiredWidth)}.");
                }
                // Segment labels are drawn above messages and notes and step up to stay
                // clear of them; in showcase, a label left covering any of them fails.
                var covered = readableMessages ? SegmentLabelBlocker(segment) : null;
                if (covered is not null)
                {
                    // Offer the nearest start whose label lands clear, inside the segment.
                    double? clearFrom = null;
                    for (var distance = 1; distance <= 300 && clearFrom is null; distance += 1)
                    {
                        foreach (var candidate in (double[])[From(segment) + distance, From(segment) - distance])
                        {
                            if (candidate < TopY || candidate >= To(segment)) continue;
                            var shifted = segment.DeepClone().AsObject();
                            shifted["from"] = candidate;
                            if (SegmentLabelBlocker(shifted) is null)
                            {
                                clearFrom = candidate;
                                break;
                            }
                        }
                    }
                    var fromPath = $"/segments/{N(segmentIndex)}/from";
                    Report($"Segment \"{S(label)}\" label would cover the {covered.Kind} of message \"{S(covered.Message.Str("label"))}\" — move the segment start (from: {T(segment["from"])}) or that message so they are further apart{(covered.Kind == "note" ? ", or shorten the note" : "")}.",
                        "sequence/segment-label-overlap",
                        new JsonObject { ["diagramType"] = "sequence", ["segment"] = segment["label"]?.DeepClone(), ["path"] = fromPath },
                        new JsonObject
                        {
                            ["label"] = Bounds(labelBox),
                            ["attempts"] = 4,
                            ["covered"] = new JsonObject
                            {
                                ["kind"] = covered.Kind, ["message"] = covered.Message["label"]?.DeepClone(), ["path"] = covered.Path, ["bounds"] = Bounds(covered.Rect),
                            },
                        },
                        clearFrom is { } clear ? [$"set {fromPath} to {N(clear)}"] : []);
                }
            }

            foreach (var activation in activations)
            {
                if (!Has(activation.Str("participant"))) problems.Add($"Activation references unknown participant \"{T(activation["participant"])}\".");
                if (To(activation) <= From(activation)) problems.Add($"Activation for \"{T(activation["participant"])}\" has invalid time range — \"to\" must be greater than \"from\".");
            }

            if (participantList.Count > 0)
            {
                var lastParticipant = Get(participantList[^1].Str("id") ?? "undefined")!;
                if (lastParticipant.Cx + participantW / 2 > viewBox[0] - 40)
                {
                    var requiredWidth = Math.Ceiling(lastParticipant.Cx + participantW / 2 + 40);
                    problems.Add($"Participants exceed viewBox width — set meta.viewBox[0] to at least {N(requiredWidth)} or remove a participant.");
                }
            }

            // Showcase must not silently drop the implicit legend because late content
            // leaves no room for it; give the exact canvas height instead.
            var legendHeight = LegendRequiredHeight(viewBox[0]);
            if (meta.Str("quality_profile") == "showcase" && !meta.ContainsKey("legend")
                && legendHeight > viewBox[1] && Legend.MeasureLegend(sink, LegendEntries(), LegendLayoutFor()) is null)
            {
                problems.Add($"Sequence content ends at y={N(contentBottom)}, leaving no room for the legend below it — set meta.viewBox[1] to at least {N(legendHeight)} or omit meta.viewBox so the canvas grows.");
            }

            if (problems.Count > 0)
            {
                throw sink.ThrowDiagnosticProblems("Sequence layout validation failed", problems,
                    subject: new JsonObject { ["diagramType"] = "sequence" },
                    details: diagnostics);
            }
        }

        // ---- Rendering ---------------------------------------------------------------
        string RenderParticipant(Participant participant)
        {
            var type = participant.Type ?? "";
            var fill = Geometry.ComponentFill.TryGetValue(type, out var f) ? f : "c-external";
            var hasSub = participant.Sublabel is not null && participant.Sublabel != "";
            var sub = hasSub
                ? $"\n          <text data-detail=\"context\" x=\"{N(participant.Cx)}\" y=\"{N(TopY + ParticipantSublabelY)}\" class=\"t-muted\" font-size=\"{N(TextFit.FittedNodeFontSize(participant.Sublabel, participantW, readableSublabelPreferred, readableSublabelMinimum))}\" text-anchor=\"middle\">{Utils.Esc(participant.Sublabel)}</text>"
                : "";
            var brand = participant.Brand is { } mark ? BrandMarks.RenderBrandMark(mark, participant.X + participantW - 22, TopY + 6) : "";
            var label = participant.Label ?? "";
            var labelFontSize = TextFit.FittedNodeFontSize(participant.Label, BrandMarks.BrandLabelFitWidth(participant.Brand, participantW), 11, 8);
            var passport = new JsonObject();
            if (participant.Source["type"] is { } kind) passport["kind"] = kind.DeepClone();
            if (participant.Source["sublabel"] is { } sublabelNode) passport["sublabel"] = sublabelNode.DeepClone();
            passport["context"] = ctx.Locale.Translate(locale, "node.context.sequence");
            if (BrandMarks.BrandMetadataFor(participant.Brand) is { } brandMetadata)
                foreach (var (key, value) in brandMetadata) passport[key] = value?.DeepClone();
            return $"        <g {SvgAttrs.FocusNodeAttrs(participant.Id, label, passport, locale, ctx)}>\n"
                + $"          {SvgAttrs.FocusNodeTitle(label, passport)}\n"
                + $"          <rect x=\"{N(participant.X)}\" y=\"{N(TopY)}\" width=\"{N(participantW)}\" height=\"{N(ParticipantH)}\" rx=\"6\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(participant.X)}\" y=\"{N(TopY)}\" width=\"{N(participantW)}\" height=\"{N(ParticipantH)}\" rx=\"6\" class=\"{fill}\"{SvgAttrs.AnimateAttr(meta, "node", participant.Index)} stroke-width=\"1.5\"/>\n"
                + $"          {Utils.RenderSemanticSigil(type, participant.X + 6, TopY + 6, icon: participant.Source.Str("icon"))}{(brand.Length > 0 ? $"\n          {brand}" : "")}\n"
                + $"          <text data-node-label=\"\"{(hasSub ? " data-detail-anchor=\"\"" : "")} x=\"{N(participant.Cx)}\" y=\"{N(TopY + ParticipantLabelY)}\" class=\"t-primary\" font-size=\"{N(labelFontSize)}\" font-weight=\"600\" text-anchor=\"middle\">{Utils.Esc(participant.Label)}</text>{sub}\n"
                + "        </g>";
        }

        // Lifelines never enter the legend band. The legend is placed below all
        // timeline content, so stopping above its title still reaches every message.
        double LifelineEnd()
        {
            var legend = Legend.MeasureLegend(ctx.Diagnostics, LegendEntries(), LegendLayoutFor());
            return legend?.TitleY is not { } titleY ? lifelineBottom : Math.Min(lifelineBottom, titleY - 22);
        }

        string RenderLifeline(Participant participant, double end) =>
            $"        <path d=\"M {N(participant.Cx)} {N(LifelineTop)} L {N(participant.Cx)} {N(end)}\" class=\"a-default\" stroke-width=\"0.8\" stroke-dasharray=\"3,7\"/>";

        string RenderSegment(JsonObject segment, int index) =>
            $"        <rect data-graph-role=\"structural-frame\" data-composition-frame-kind=\"segment\" data-composition-frame-id=\"{N(index)}\" x=\"48\" y=\"{T(segment["from"])}\" width=\"{N(viewBox[0] - 96)}\" height=\"{N(To(segment) - From(segment))}\" rx=\"10\" class=\"c-lane\" stroke-width=\"1\"/>";

        string RenderSegmentLabel(JsonObject segment, int index)
        {
            var label = SegmentLabelBox(segment);
            return $"        <g data-graph-role=\"segment-label\" data-segment-id=\"{N(index)}\">\n"
                + $"          <rect x=\"{N(label.X)}\" y=\"{N(label.Y)}\" width=\"{N(label.Width)}\" height=\"{N(label.Height)}\" rx=\"3\" class=\"c-mask\"/>\n"
                + $"          <text x=\"{N(label.X + 6)}\" y=\"{N(label.Y + 13)}\" class=\"t-muted\" font-size=\"9\" font-weight=\"600\">{Utils.Esc(segment.Str("label"))}</text>\n"
                + "        </g>";
        }

        string RenderActivation(JsonObject activation)
        {
            var participant = Get(activation.Str("participant"))!;
            var fill = Geometry.ComponentFill.TryGetValue(activation.Str("type") ?? "", out var activationFill) ? activationFill
                : Geometry.ComponentFill.TryGetValue(participant.Type ?? "", out var participantFill) ? participantFill : "c-external";
            var x = participant.Cx - 5;
            var height = To(activation) - From(activation);
            return $"        <rect x=\"{N(x)}\" y=\"{T(activation["from"])}\" width=\"10\" height=\"{N(height)}\" rx=\"3\" class=\"c-mask\"/>\n"
                + $"        <rect x=\"{N(x)}\" y=\"{T(activation["from"])}\" width=\"10\" height=\"{N(height)}\" rx=\"3\" class=\"{fill}\" stroke-width=\"1\"/>";
        }

        string MessageLabel(JsonObject message, double x1, double x2)
        {
            var box = MessageLabelBox(message);
            var center = box is not null ? box.X + box.Width / 2 : (x1 + x2) / 2;
            var y = Y(message) - 10;
            var labelW = box is not null ? box.Width : Math.Max(34, Utils.TextUnits(message.Str("label")) * messageUnitWidth + 12);
            // A colored line gets a label in the same color, as in the legend swatches.
            // Gray lines (default and return) keep the readable muted text color.
            var variant = message.Str("variant");
            var accent = variant is "emphasis" or "security" or "dashed" ? Geometry.EdgeLabelAccent(variant) : "t-muted";
            return "        <g data-detail=\"context\">\n"
                + $"          <rect x=\"{N(center - labelW / 2)}\" y=\"{N(y - 10)}\" width=\"{N(labelW)}\" height=\"{N(labelH)}\" rx=\"3\" class=\"c-mask\"/>\n"
                + $"          <text x=\"{N(center)}\" y=\"{N(y)}\" class=\"{accent}\" font-size=\"{N(messageFontSize)}\" text-anchor=\"middle\">{Utils.Esc(message.Str("label"))}</text>\n"
                + "        </g>";
        }

        string NoteBackdrop(JsonObject message)
        {
            if (NoteRect(message) is not { } note) return "";
            var box = new Rect(note.X - 2, note.Y - 2, note.Width + 4, note.Height + 4);
            var crossed = segments.Any(segment => ((double[])[From(segment), To(segment)])
                .Any(border => border >= box.Y && border <= box.Y + box.Height));
            if (!crossed) return "";
            // The backdrop clears decorative frames, never another message. Standard
            // quality can retain overlapping annotations that showcase rejects.
            var coversMessage = messages.Any(other => !ReferenceEquals(other, message)
                && MessageRects(other).Any(rect => Geometry.RectsOverlap(box, rect, 0)));
            if (coversMessage) return "";
            return $"\n        <rect data-detail=\"context\" data-graph-role=\"message-note-background\" x=\"{N(box.X)}\" y=\"{N(box.Y)}\" width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" rx=\"2\" class=\"c-mask\" pointer-events=\"none\"/>";
        }

        string RenderMessage(JsonObject message, int index)
        {
            var (start, end, _) = MessageGeometry(message)!.Value;
            var variant = Truthy(message.Str("variant")) ? message.Str("variant")! : "default";
            var (cls, marker) = variant == "return" ? ("a-default", "arrowhead")
                : Geometry.ArrowClassMap.TryGetValue(variant, out var arrow) ? arrow : Geometry.ArrowClassMap["default"];
            var strokeWidth = message.Str("variant") == "emphasis" ? 1.8 : 1.4;
            var dash = message.Str("variant") == "return" ? " stroke-dasharray=\"3,5\"" : "";
            var noteBox = NoteLayoutFor(message);
            var noteContent = noteBox is { Lines.Count: > 1 }
                ? string.Concat(noteBox.Lines.Select((line, lineIndex) => $"<tspan x=\"{N(noteBox.X)}\" dy=\"{N(lineIndex != 0 ? NOTE_LINE_HEIGHT : 0)}\">{Utils.Esc(line)}</tspan>"))
                : Utils.Esc(Note(message));
            // Notes carry message conditions and already reserve timeline space. Keep
            // them readable at the default depth; Map still hides context without reflow.
            var note = noteBox is not null
                ? $"\n        <text data-detail=\"context\" x=\"{N(noteBox.X)}\" y=\"{N(noteBox.Baseline)}\" class=\"t-muted\" font-size=\"{N(NOTE_FONT_SIZE)}\">{noteContent}</text>"
                : "";
            var y = T(message["y"]);
            return $"        <g {SvgAttrs.FocusEdgeAttrs(S(message.Str("from")), S(message.Str("to")), message.Str("label"), N(index), message.Str("id"))}>\n"
                + $"          <path data-composition-edge-from=\"{Utils.Esc(message.Str("from"))}\" data-composition-edge-to=\"{Utils.Esc(message.Str("to"))}\"{(message["id"].Truthy() ? $" data-composition-edge-id=\"{Utils.Esc(T(message["id"]))}\"" : "")} data-composition-points=\"{Geometry.RoutePointsValue([new Pt(start, Y(message)), new Pt(end, Y(message))])}\" d=\"M {N(start)} {y} L {N(end)} {y}\" class=\"{cls}\"{SvgAttrs.AnimateAttr(meta, "edge", index)} stroke-width=\"{N(strokeWidth)}\"{dash} marker-end=\"url(#{marker})\"/>\n"
                + $"{MessageLabel(message, start, end)}{NoteBackdrop(message)}{note}\n"
                + "        </g>";
        }

        LegendLayout LegendLayoutFor() => new(
            X: 40,
            BaselineY: legendY,
            Width: viewBox[0] - 80,
            // The same content-based budget as legendRequiredHeight(): a wrapped
            // legend may use any rows it needs as long as it stays below the content.
            MinTitleY: Math.Max(LifelineTop, contentBottom + LEGEND_CONTENT_GAP),
            Unfit: !meta.ContainsKey("legend") ? "hide" : "error",
            DiagramType: "sequence");

        string RenderLegend() => Legend.RenderLegend(ctx, LegendEntries(), LegendLayoutFor(),
            renderSwatch: entry =>
            {
                var catalog = LegendCatalog.First(c => c.Kind == entry.Kind);
                return $"<path d=\"M {N(entry.X)} {N(entry.Baseline - 3)} L {N(entry.X + 34)} {N(entry.Baseline - 3)}\" class=\"{catalog.ClassName}\" stroke-width=\"{N(catalog.StrokeWidth ?? 1.4)}\"{(catalog.Dash is not null ? $" stroke-dasharray=\"{catalog.Dash}\"" : "")} marker-end=\"url(#{catalog.Marker})\"/>";
            },
            locale: locale);

        public string RenderSvg()
        {
            var participantValues = participants.Values.ToList();
            // Same default-canvas contract as lifecycle: 920x760 is below the 1.55 wide
            // ratio, so without intrinsic-height the desktop Reader can neither narrow
            // nor scroll it and every default sequence fails the browser gate.
            var readerFit = meta["viewBox"].Truthy() ? "" : " data-reader-fit=\"width-first\"";
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\" data-sequence-column-fit=\"{columnFit}\"{readerFit} {SvgAttrs.SvgRootAttrs(ctx)} data-motion-choreography=\"sequence\">\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "sequence")).Append('\n');
            sb.Append(Utils.RenderDefinitions()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append('\n');
            sb.Append("        <!-- Time Segments -->\n");
            sb.Append(string.Join("\n\n", segments.Select(RenderSegment))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Lifelines -->\n");
            sb.Append(string.Join("\n", participantValues.Select(participant => RenderLifeline(participant, LifelineEnd())))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Activations -->\n");
            sb.Append(string.Join("\n", activations.Select(RenderActivation))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Messages -->\n");
            sb.Append(string.Join("\n\n", messages.Select(RenderMessage))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Segment Labels -->\n");
            sb.Append(string.Join("\n", segments.Select(RenderSegmentLabel))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Participants -->\n");
            sb.Append(string.Join("\n\n", participantValues.Select(RenderParticipant))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Legend -->\n");
            sb.Append(RenderLegend()).Append('\n');
            sb.Append("      </svg>");
            return sb.ToString();
        }
    }
}
