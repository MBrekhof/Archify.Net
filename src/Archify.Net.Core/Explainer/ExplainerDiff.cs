using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// parseExplainerDiff's options (diff.mjs:27). A null Component is the default 'diff'; empty Input, PanelId and Path drop out.
internal sealed record DiffOptions(double Line = 1, string? Component = null, string? Input = null, string? PanelId = null, string? Path = null);

internal sealed record DiffParse(JsonArray Rows, JsonObject Counts, List<Diagnostic> Diagnostics, string Format)
{
    public JsonObject ToJson() => new()
    {
        ["rows"] = Rows.DeepClone(), ["counts"] = Counts.DeepClone(),
        ["diagnostics"] = new JsonArray([.. Diagnostics.Select(d => (JsonNode?)d.ToJson())]), ["format"] = Format,
    };
}

// archify 3.2.0 explainer/diff.mjs: unified diff parsed without rewriting its literal bytes. Fragment line numbers stay null.
internal static class ExplainerDiff
{
    const string Example = "@@ -1,2 +1,2 @@\n unchanged\n-old value\n+new value";
    // `.` stops at a line terminator and `$` (no m flag) is the end of input; \b and \d are ASCII.
    const string Nl = "[^\n\r\u2028\u2029]";
    static readonly Regex ReCombined = new(@"\A(?:diff --(?:cc|combined)(?![A-Za-z0-9_])|@@@)");
    static readonly Regex ReBinary = new($@"\A(?:GIT binary patch|Binary files {Nl}+ differ|literal [0-9]+\z|delta [0-9]+\z)");
    static readonly Regex ReGitHeader = new($@"\Adiff --git {Nl}+ {Nl}+\z");
    static readonly Regex ReHunk = new($@"\A@@ -([0-9]+)(?:,([0-9]+))? \+([0-9]+)(?:,([0-9]+))? @@(?:[ \t]{Nl}*)?\z");
    static readonly Regex ReMetadata = new(@"\A(?:index |(?:new file|deleted file|old|new) mode |(?:similarity|dissimilarity) index |(?:rename|copy) (?:from|to) )");
    static readonly Regex ReIndex = new(@"\Aindex [a-fA-F0-9]{1,64}\.\.[a-fA-F0-9]{1,64}(?: [0-7]{6})?\z");
    static readonly Regex ReMode = new(@"\A(?:new file|deleted file|old|new) mode [0-7]{6}\z");
    static readonly Regex ReRename = new($@"\A(?:rename|copy) (?:from|to) {Nl}+\z");
    static readonly Regex ReSimilarity = new(@"\A(?:similarity|dissimilarity) index ([0-9]+)%\z");
    const double MaxSafe = 9007199254740991;
    static bool Safe(double v) => double.IsFinite(v) && v == Math.Floor(v) && Math.Abs(v) <= MaxSafe;

    // sourceLines (:5-13): one row per `[^\n]*(\n|$)` match, the empty match at the end skipped; CRLF is split off.
    static List<(string Raw, string Ending)> SourceLines(string text)
    {
        var result = new List<(string, string)>();
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (last && parts[i].Length == 0) break;
            var crlf = !last && parts[i].EndsWith('\r');
            result.Add((crlf ? parts[i][..^1] : parts[i], crlf ? "\r\n" : last ? "" : "\n"));
        }
        return result;
    }

    static string? FileName(string value)
    {
        var name = value.Split('\t')[0];
        if (name.StartsWith('"') && name.EndsWith('"'))
        {
            var parsed = ExplainerJson.Parse(name, out string? error);
            name = error is null ? parsed!.GetValue<string>() : name.Length >= 2 ? name[1..^1] : "";
        }
        if (name == "/dev/null") return null;
        return name.Length >= 2 && name[0] is 'a' or 'b' && name[1] == '/' ? name[2..] : name;
    }

    sealed class Hunk { public double OldStart, OldCount, NewStart, NewCount, UsedOld, UsedNew, SourceLine; }

    /// <summary>parseExplainerDiff (diff.mjs:27-124). File paths identify credential syntax only; no filesystem reads.</summary>
    public static DiffParse Parse(string text, DiffOptions o)
    {
        var line = o.Line;
        var component = o.Component ?? "diff";
        var rows = new JsonArray();
        var diagnostics = new List<Diagnostic>();
        var counts = new JsonObject { ["added"] = 0, ["removed"] = 0, ["context"] = 0, ["hunks"] = 0, ["files"] = 0 };
        void Count(string key) => counts[key] = counts[key]!.GetValue<int>() + 1;
        Hunk? hunk = null;
        string? oldPath = null, newPath = null;
        int fileIndex = -1;
        bool oldHeader = false, newHeader = false;
        var oldHeaderLine = line;
        JsonObject? lastBody = null, markerFor = null;
        var format = "fragment";
        var groups = new JsMap<int, List<JsonObject>>();
        void Fail(string code, double sourceLine, string reason, string example = Example)
        {
            var subject = new JsonObject { ["line"] = sourceLine, ["column"] = 1, ["component"] = component };
            if (!string.IsNullOrEmpty(o.Input)) subject["input"] = o.Input;
            if (!string.IsNullOrEmpty(o.PanelId)) subject["panelId"] = o.PanelId;
            if (!string.IsNullOrEmpty(o.Path)) subject["path"] = o.Path;
            diagnostics.Add(new($"explainer/diff-{code}", Severity.Error, reason, subject, new JsonObject { ["reason"] = reason, ["example"] = example },
                [$"Use a valid unified diff, for example:\n{example}"], null));
        }
        void FinishHunk()
        {
            if (hunk is null) return;
            if (hunk.UsedOld != hunk.OldCount || hunk.UsedNew != hunk.NewCount)
                Fail("hunk-count", hunk.SourceLine, $"Hunk declares {Js.Num(hunk.OldCount)} old and {Js.Num(hunk.NewCount)} new lines, but contains {Js.Num(hunk.UsedOld)} old and {Js.Num(hunk.UsedNew)} new lines.");
            hunk = null;
        }
        void StartFile()
        {
            if (oldHeader && !newHeader) Fail("header-order", oldHeaderLine, "An old-file header needs its matching new-file header.", "--- a/file.txt\n+++ b/file.txt\n" + Example);
            FinishHunk(); fileIndex++; Count("files"); oldPath = null; newPath = null; oldHeader = false; newHeader = false; lastBody = null; markerFor = null; format = "unified";
        }
        JsonObject Row(string kind, (string Raw, string Ending) value, int index, JsonObject? extra = null)
        {
            var entry = new JsonObject { ["kind"] = kind, ["raw"] = value.Raw, ["text"] = value.Raw, ["ending"] = value.Ending, ["sourceLine"] = line + index, ["oldLine"] = null, ["newLine"] = null };
            foreach (var (key, v) in extra ?? []) entry[key] = v?.DeepClone();
            var knownPath = kind == "removed" ? (!string.IsNullOrEmpty(oldPath) ? oldPath : newPath) : (!string.IsNullOrEmpty(newPath) ? newPath : oldPath);
            if (!string.IsNullOrEmpty(knownPath)) entry["filePath"] = knownPath;
            if (kind is "header" or "hunk") { lastBody = null; markerFor = null; }
            rows.Add(entry);
            return entry;
        }
        var values = SourceLines(text);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var raw = value.Raw;
            var sourceLine = line + index;
            if (ReCombined.IsMatch(raw)) { FinishHunk(); Fail("combined", sourceLine, "Combined merge diffs are unsupported. Provide a two-way unified diff."); Row("header", value, index); continue; }
            if (ReBinary.IsMatch(raw)) { FinishHunk(); Fail("binary", sourceLine, "Binary patches are unsupported. Describe the binary change separately."); Row("header", value, index); continue; }
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (!ReGitHeader.IsMatch(raw)) Fail("header", sourceLine, "A git diff header must identify old and new paths.", "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n" + Example);
                StartFile(); Row("header", value, index); continue;
            }
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                FinishHunk(); format = "unified";
                var match = ReHunk.Match(raw);
                if (!match.Success) { Fail("hunk-header", sourceLine, "A hunk header requires old and new start/count ranges."); Row("hunk", value, index); continue; }
                double Group(int g) => Js.ToNumber(match.Groups[g].Success ? match.Groups[g].Value : "1");
                var (oldStart, oldCount, newStart, newCount) = (Group(1), Group(2), Group(3), Group(4));
                var valid = new[] { oldStart, oldCount, newStart, newCount }.All(Safe) && Safe(oldStart + oldCount) && Safe(newStart + newCount)
                    && (oldCount == 0 || oldStart >= 1) && (newCount == 0 || newStart >= 1);
                if (!valid) { Fail("hunk-range", sourceLine, "Hunk ranges must use safe nonnegative integers; a nonempty range starts at line 1 or later."); Row("hunk", value, index); continue; }
                Count("hunks");
                hunk = new Hunk { OldStart = oldStart, OldCount = oldCount, NewStart = newStart, NewCount = newCount, SourceLine = sourceLine };
                lastBody = null; markerFor = null;
                Row("hunk", value, index, new JsonObject { ["oldStart"] = oldStart, ["oldCount"] = oldCount, ["newStart"] = newStart, ["newCount"] = newCount });
                continue;
            }
            // Once the declared hunk is complete, a file header belongs to the next file.
            // Within an unfinished hunk, +++/--- remain ordinary additions/removals.
            var within = hunk is not null && (hunk.UsedOld < hunk.OldCount || hunk.UsedNew < hunk.NewCount);
            if (!within && raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                FinishHunk();
                if (oldHeader && newHeader) StartFile(); else if (fileIndex < 0) StartFile();
                oldPath = FileName(raw[4..]); oldHeader = true; oldHeaderLine = sourceLine; newHeader = false; Row("header", value, index); continue;
            }
            if (!within && raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                FinishHunk(); format = "unified";
                if (!oldHeader) Fail("header-order", sourceLine, "A new-file header needs its preceding old-file header.", "--- a/file.txt\n+++ b/file.txt\n" + Example);
                newPath = FileName(raw[4..]); newHeader = true; Row("header", value, index); continue;
            }
            if (ReMetadata.IsMatch(raw))
            {
                FinishHunk(); format = "unified";
                var similarity = ReSimilarity.Match(raw);
                var valid = ReIndex.IsMatch(raw) || ReMode.IsMatch(raw) || ReRename.IsMatch(raw) || (similarity.Success && Js.ToNumber(similarity.Groups[1].Value) <= 100);
                if (!valid) Fail("header", sourceLine, "This git metadata header is malformed.", "diff --git a/file.txt b/file.txt\nindex 1234567..abcdef0 100644\n--- a/file.txt\n+++ b/file.txt\n" + Example);
                Row("header", value, index); lastBody = null; markerFor = null; continue;
            }
            if (raw == "\\ No newline at end of file")
            {
                if (lastBody is null || markerFor == lastBody)
                    Fail("newline-marker", sourceLine, "A no-newline marker must immediately follow one content row and cannot repeat for that row.", "@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file");
                var body = lastBody;
                Row("no-newline", value, index, body is null ? null : new JsonObject { ["forLine"] = body["sourceLine"]!.DeepClone() });
                markerFor = body; continue;
            }
            var kind = raw.StartsWith('+') ? "added" : raw.StartsWith('-') ? "removed" : raw.StartsWith(' ') ? "context" : null;
            if (kind is null) { Fail("row", sourceLine, "Diff content rows must start with a space, + or -."); Row("header", value, index); lastBody = null; markerFor = null; continue; }
            var entry = Row(kind, value, index, new JsonObject { ["text"] = raw[1..] });
            Count(kind);
            if (hunk is not null)
            {
                if (kind != "added") entry["oldLine"] = hunk.OldStart + hunk.UsedOld++;
                if (kind != "removed") entry["newLine"] = hunk.NewStart + hunk.UsedNew++;
            }
            lastBody = entry; markerFor = null;
            if (!groups.Has(fileIndex)) groups.Set(fileIndex, []);
            groups.Get(fileIndex)!.Add(entry);
        }
        FinishHunk();
        if (oldHeader && !newHeader)
            Fail("header-order", rows.Count > 0 && rows[^1]!["sourceLine"]!.D() is var last && last != 0 ? last : line, "An old-file header needs its matching new-file header.", "--- a/file.txt\n+++ b/file.txt\n" + Example);
        if (rows.Count == 0) Fail("empty", line, "A diff must contain content or a standard git metadata change.");
        // Scan the whole authored content and pinpoint a matching row when possible.
        // The shared scanner owns credential rules; diagnostics never quote rejected bytes.
        foreach (var group in groups.Values)
        {
            var knownPaths = group.Select(entry => entry.Str("filePath")).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            var combined = string.Join("\n", group.Select(entry => entry.Str("text")));
            if ((knownPaths.Count > 0 ? knownPaths : [null]).Any(path => ExplainerSources.ContainsSecret(combined, path ?? "")))
            {
                var matched = group.FirstOrDefault(entry => ExplainerSources.ContainsSecret(entry.Str("text")!, entry.Str("filePath") ?? ""));
                var at = matched?["sourceLine"]!.D() is { } m && m != 0 ? m : group[0]["sourceLine"]!.D();
                Fail("secret", at, "The diff contains a credential or sensitive secret value. Remove or redact it before rendering.", "-const timeout = 100;\n+const timeout = 200;");
            }
        }
        return new(rows, counts, diagnostics, format);
    }
}
