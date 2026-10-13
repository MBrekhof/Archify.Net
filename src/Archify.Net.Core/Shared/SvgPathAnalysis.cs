using System.Text.RegularExpressions;

namespace Archify.Net;

// svg-path-analysis.mjs. Analyzes only the path subset the static checker needs; not a general SVG renderer.
public sealed class SvgSubpath
{
    public List<Pt> Points { get; } = [];
    public List<Segment> Segments { get; } = [];
    public List<Segment> StraightSegments { get; } = [];
    public bool Closed { get; set; }
}

// `error` is `{ code, tokenOffset, reason, command? }` upstream; the checker spreads everything but `code` into evidence.
public sealed record SvgPathError(string Code, int TokenOffset, string Reason, string? Command);

public sealed record SvgPathAnalysisResult(bool Ok, List<SvgSubpath> Subpaths, SvgPathError? Error);

public static class SvgPathAnalysis
{
    // NUMBER is sticky (/y) upstream: \G anchors the match at the scan offset.
    static readonly Regex Number = new(@"\G[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    const string Commands = "MLHVQZACSTmlhvqzacst";

    static int? Arity(char upper) => upper switch { 'M' => 2, 'L' => 2, 'H' => 1, 'V' => 1, 'Q' => 4, _ => null };

    static SvgPathAnalysisResult Failure(int tokenOffset, string reason, string? command = null, bool unsupported = false) =>
        new(false, [], new(unsupported ? "artifact/svg-path-unsupported" : "artifact/svg-path-malformed", tokenOffset, reason, command));

    readonly record struct Token(bool IsCommand, char Command, double Value, int Offset);

    static (List<Token>? Tokens, SvgPathAnalysisResult? Failure) Tokenize(string source)
    {
        var tokens = new List<Token>();
        var offset = 0;
        int? comma = null;
        while (offset < source.Length)
        {
            var character = source[offset];
            if (character is ' ' or '\t' or '\r' or '\n') { offset += 1; continue; }
            if (character == ',')
            {
                if (comma is not null || tokens.Count == 0 || tokens[^1].IsCommand) return (null, Failure(offset, "unexpected comma"));
                comma = offset++;
                continue;
            }
            if (Commands.Contains(character))
            {
                if (comma is { } at) return (null, Failure(at, "a comma must separate numbers"));
                tokens.Add(new(true, character, 0, offset++));
                continue;
            }
            var number = Number.Match(source, offset);
            if (!number.Success) return (null, Failure(offset, "expected a number or supported path command"));
            var value = Js.ToNumber(number.Value);
            if (!double.IsFinite(value)) return (null, Failure(offset, "coordinate is not finite"));
            tokens.Add(new(false, '\0', value, offset));
            offset = number.Index + number.Length;
            comma = null;
        }
        return comma is { } trailing ? (null, Failure(trailing, "trailing comma")) : (tokens, null);
    }

    /// <summary>Each step consumes one command or one complete parameter group; a failure returns no partial geometry.
    /// Offsets are into the passed d string.</summary>
    public static SvgPathAnalysisResult AnalyzeSvgPath(string source)
    {
        var (tokens, failure) = Tokenize(source);
        if (failure is not null) return failure;
        var subpaths = new List<SvgSubpath>();
        var index = 0;
        char? command = null;
        var commandOffset = 0;
        var current = new Pt(0, 0);
        SvgSubpath? subpath = null;

        void Move(Pt point)
        {
            current = point;
            subpath = new SvgSubpath();
            subpath.Points.Add(point);
            subpaths.Add(subpath);
        }
        void Line(Pt point)
        {
            var segment = new Segment(current, point);
            subpath!.Segments.Add(segment);
            subpath.StraightSegments.Add(segment);
            subpath.Points.Add(point);
            current = point;
        }

        while (index < tokens!.Count)
        {
            if (tokens[index].IsCommand)
            {
                command = tokens[index].Command;
                commandOffset = tokens[index++].Offset;
            }
            else if (command is null)
            {
                return Failure(tokens[index].Offset, "a command is required after closepath");
            }
            var cmd = command.Value;
            var upper = char.ToUpperInvariant(cmd);
            var cmdText = new string(cmd, 1);
            if (subpath is null && upper != 'M') return Failure(commandOffset, "the first command must be moveto", cmdText);
            if (Arity(upper) is null && upper != 'Z')
                return Failure(commandOffset, "path command is not supported by artifact analysis", cmdText, true);
            if (upper == 'Z')
            {
                Line(subpath!.Points[0]);
                subpath.Closed = true;
                command = null;
                continue;
            }
            var arity = Arity(upper)!.Value;
            var parameters = new List<double>();
            var parameterOffset = index < tokens.Count ? tokens[index].Offset : source.Length;
            for (var count = 0; count < arity; count += 1)
            {
                if (index >= tokens.Count || tokens[index].IsCommand)
                    return Failure(index < tokens.Count ? tokens[index].Offset : source.Length, $"command {cmdText} requires {Js.Num(arity)} parameters", cmdText);
                parameters.Add(tokens[index].Value);
                index += 1;
            }
            var relative = cmd != upper;
            Pt Point(double x, double y) => relative ? new(current.X + x, current.Y + y) : new(x, y);
            Pt end;
            Pt? control = null;
            if (upper == 'H') end = new(relative ? current.X + parameters[0] : parameters[0], current.Y);
            else if (upper == 'V') end = new(current.X, relative ? current.Y + parameters[0] : parameters[0]);
            else if (upper == 'Q')
            {
                control = Point(parameters[0], parameters[1]);
                end = Point(parameters[2], parameters[3]);
            }
            else end = Point(parameters[0], parameters[1]);
            if (!(double.IsFinite(end.X) && double.IsFinite(end.Y) && (control is not { } c || (double.IsFinite(c.X) && double.IsFinite(c.Y)))))
                return Failure(parameterOffset, "computed coordinate is not finite", cmdText);
            if (upper == 'M')
            {
                Move(end);
                // Extra parameter groups after a moveto are linetos, never extra subpaths.
                command = relative ? 'l' : 'L';
                continue;
            }
            if (subpath!.Closed) Move(current);
            if (upper != 'Q') { Line(end); continue; }

            var start = current;
            var ctl = control!.Value;
            var cross = (ctl.X - start.X) * (end.Y - start.Y) - (ctl.Y - start.Y) * (end.X - start.X);
            if (Math.Abs(cross) <= 1e-9) subpath!.StraightSegments.Add(new(start, end));
            // Keep the existing eight-step Q sampling; a non-collinear curve never poses as a frame's straight border.
            for (var step = 1; step <= 8; step += 1)
            {
                var amount = step / 8.0;
                var remaining = 1 - amount;
                var next = new Pt(
                    remaining * remaining * start.X + 2 * remaining * amount * ctl.X + amount * amount * end.X,
                    remaining * remaining * start.Y + 2 * remaining * amount * ctl.Y + amount * amount * end.Y);
                if (!(double.IsFinite(next.X) && double.IsFinite(next.Y))) return Failure(parameterOffset, "sampled coordinate is not finite", cmdText);
                subpath!.Segments.Add(new(current, next));
                subpath.Points.Add(next);
                current = next;
            }
            current = end;
        }
        return new(true, subpaths, null);
    }
}
