using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

// archify 3.2.0 explainer/charts.mjs: compileExplainerChart (Compile, PORT-008 Task 5) and chartFromData (FromData).
internal static class ExplainerCharts
{
    public static ChartResult Compile(string text) => throw new NotImplementedException("compileExplainerChart lands in PORT-008 Task 5.");

    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // ponytail: JSON.parse nests without limit; the C# parse and Js.String recurse at about 1 KB of stack per level, so a
    // data file nested deeper than this reports "not valid JSON" (deviation, PORT-008 Task 11). 256 levels stay under a
    // quarter of a 1 MB stack. An iterative parser lifts it, if a real data file ever nests that deep.
    const int JsonDepth = 256;

    // `row[column]` is a plain property get: a key no row owns reads through Object.prototype. String() of each (Node 24).
    static readonly Dictionary<string, string> Inherited = new(StringComparer.Ordinal)
    {
        ["constructor"] = "function Object() { [native code] }", ["__proto__"] = "[object Object]",
        ["__defineGetter__"] = "function __defineGetter__() { [native code] }", ["__defineSetter__"] = "function __defineSetter__() { [native code] }",
        ["__lookupGetter__"] = "function __lookupGetter__() { [native code] }", ["__lookupSetter__"] = "function __lookupSetter__() { [native code] }",
        ["hasOwnProperty"] = "function hasOwnProperty() { [native code] }", ["isPrototypeOf"] = "function isPrototypeOf() { [native code] }",
        ["propertyIsEnumerable"] = "function propertyIsEnumerable() { [native code] }", ["toString"] = "function toString() { [native code] }",
        ["valueOf"] = "function valueOf() { [native code] }", ["toLocaleString"] = "function toLocaleString() { [native code] }",
    };

    // charts.mjs:41-70. Rows are JsonObjects: insertion order, `row[key] = v` keeps a duplicate key at its first position
    // (Object.fromEntries and JSON.parse alike), and an absent key is JS undefined.
    public static ChartData FromData(byte[] bytes, string filePath, ChartDataSpec spec)
    {
        var (kind, x, y, unit) = spec;
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return new(null, null, true); }   // TextDecoder fatal: the caller's message (PORT-009)
        for (var i = 0; i < 2 && text.StartsWith('﻿'); i++) text = text[1..];   // TextDecoder drops one BOM, the regex a second
        List<JsonObject> rows;
        if (IsJsonPath(filePath))
        {
            JsonNode? parsed;
            try { parsed = DiagramLoader.ParseNode(text, JsonDepth); }
            catch (JsonException) { return Fail("The data file is not valid JSON."); }
            if (parsed is not JsonArray array || !array.All(row => row is JsonObject)) return Fail("JSON chart data must be an array of objects.");
            rows = [.. array.Cast<JsonObject>()];
        }
        else
        {   // text.split(/\r?\n/u).filter(line => line.trim())
            var lines = text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).Where(line => Js.Trim(line) != "").ToList();
            if (lines.Count < 2) return Fail("CSV chart data needs a header row and at least one data row.");
            var header = Cells(lines[0]);
            rows = [.. lines.Skip(1).Select(line =>
            {
                var row = new JsonObject();
                var cells = Cells(line);
                for (var i = 0; i < cells.Count; i++) row[i < header.Count ? header[i] : "undefined"] = cells[i];   // header[i] undefined → key "undefined"
                return row;
            })];
        }
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows) foreach (var key in Keys(row)) if (seen.Add(key)) columns.Add(key);
        var missing = y.Prepend(x).FirstOrDefault(column => !seen.Contains(column));
        if (!string.IsNullOrEmpty(missing)) return Fail($"The data file has no column \"{missing}\". Columns: {string.Join(", ", columns)}.");   // `if (missing)`: "" is falsy
        var cap = kind == "pie" ? 12 : 60;
        if (rows.Count > cap) return Fail($"A {kind} chart takes at most {cap} rows; this file has {rows.Count}.");
        foreach (var column in y)
        {
            var bad = Array.FindIndex(Numbers(rows, column), value => !double.IsFinite(value));
            if (bad < 0) continue;
            var (blank, value) = Cell(rows[bad], column);
            if (blank) return Fail($"Column \"{column}\" is empty in data row {bad + 1}. A blank is missing data, not zero: fill in the value, or leave the row out and say so in the caption.");
            return Fail($"Column \"{column}\" has a non-numeric value in data row {bad + 1}: {value}.");
        }
        if (kind == "pie")
        {
            var values = Numbers(rows, y.Count > 0 ? y[0] : "undefined");   // y[0] of an empty list is undefined
            return new("pie\n" + string.Join("\n", rows.Select((row, i) => $"  {Quote(Cell(row, x).Str)} : {Js.Num(values[i])}")), null, false);
        }
        var axis = string.IsNullOrEmpty(unit) ? "" : $"  y-axis {Quote(unit)}\n";   // `${unit ? … : ''}`: "" is absent (review M8c)
        var series = string.Join("\n", y.Select(column => $"  {kind} {Quote(column)} [{string.Join(", ", Numbers(rows, column).Select(Js.Num))}]"));
        return new($"xychart-beta\n  x-axis [{string.Join(", ", rows.Select(row => Quote(Cell(row, x).Str)))}]\n{axis}{series}", null, false);
    }

    static ChartData Fail(string error) => new(null, error, false);
    // JSON.stringify(result, null, 2); a null member stands for undefined, which JSON.stringify omits.
    internal static string Json(JsonObject result)
    {
        foreach (var key in result.Where(property => property.Value is null).Select(property => property.Key).ToList()) result.Remove(key);
        return Js.Stringify(result, 2);
    }
    static string Quote(string value) => $"\"{value.Replace("\"", "").Replace("\\", "")}\"";

    // /\.json$/iu: under `iu` the simple case folding also maps U+017F LATIN SMALL LETTER LONG S to "s".
    static bool IsJsonPath(string path) => path.Length >= 5 && path[^5] == '.' && (path[^4] | 0x20) == 'j'
        && (path[^3] is 's' or 'S' or 'ſ') && (path[^2] | 0x20) == 'o' && (path[^1] | 0x20) == 'n';

    // The quoted-cell splitter (charts.mjs:52), per UTF-16 code unit; each cell is JS-trimmed.
    static List<string> Cells(string line)
    {
        var output = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { output.Add(Js.Trim(cell.ToString())); cell.Clear(); }
            else cell.Append(c);
        }
        output.Add(Js.Trim(cell.ToString()));
        return output;
    }

    // Object.keys: array-index keys ("0".."4294967294", canonical) ascending first, then the rest in insertion order.
    static IEnumerable<string> Keys(JsonObject row)
    {
        var keys = row.Select(property => property.Key).ToList();
        return keys.Where(IsArrayIndex).OrderBy(key => ulong.Parse(key)).Concat(keys.Where(key => !IsArrayIndex(key)));
    }
    static bool IsArrayIndex(string key) => key.Length is > 0 and <= 10 && key.All(char.IsAsciiDigit) && (key == "0" || key[0] != '0')
        && ulong.Parse(key) < uint.MaxValue;

    // blank(row[column]) and String(row[column]): undefined and null are blank, otherwise String(v).trim() === ''.
    static (bool Blank, string Str) Cell(JsonObject row, string key)
    {
        if (!row.TryGetPropertyValue(key, out var value)) return Inherited.TryGetValue(key, out var inherited) ? (false, inherited) : (true, "undefined");
        if (value is null) return (true, "null");
        var str = Js.String(value);
        return (Js.Trim(str) == "", str);
    }
    // blank(v) ? NaN : Number(String(v).replace(/,/gu, ''))
    static double[] Numbers(List<JsonObject> rows, string column) =>
        [.. rows.Select(row => Cell(row, column) is (false, var str) ? Js.ToNumber(str.Replace(",", "")) : double.NaN)];
}

// compileExplainerChart's result: { svg, width, height, type } or { error, line?, example }.
internal sealed record ChartResult(string? Svg, double? Width, double? Height, string? Type, string? Error, int? Line, string? Example)
{
    public string ToJson() => ExplainerCharts.Json(new JsonObject
    {
        ["svg"] = Svg, ["width"] = Width, ["height"] = Height, ["type"] = Type, ["error"] = Error, ["line"] = Line, ["example"] = Example,
    });
}

// charts.mjs:69 `${unit ? … : ''}`: Unit is truthiness-tested, so "" behaves as absent (review M8c).
internal sealed record ChartDataSpec(string Kind, string X, IReadOnlyList<string> Y, string? Unit);

// chartFromData's result: { text } or { error }. NotUtf8 marks where TextDecoder `fatal: true` throws; the capture records
// that as { notUtf8: true }, and the caller's message for it is PORT-009's.
internal sealed record ChartData(string? Text, string? Error, bool NotUtf8)
{
    public string ToJson() => ExplainerCharts.Json(new JsonObject { ["text"] = Text, ["error"] = Error, ["notUtf8"] = NotUtf8 ? true : null });
}

