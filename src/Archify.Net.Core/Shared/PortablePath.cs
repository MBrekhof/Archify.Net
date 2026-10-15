using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

// portable-path.mjs PortablePathError. `Code` is portable-path/<reason>; optional details stay null when upstream leaves them undefined.
public sealed class PortablePathException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
    public string Code => $"portable-path/{Reason}";
    public string? Value { get; init; }
    public string? Profile { get; init; }
    public string? Segment { get; init; }
    public int? SegmentIndex { get; init; }
    public string? Character { get; init; }
    public int? Index { get; set; }
    public int? ConflictIndex { get; init; }
    public string? ConflictValue { get; init; }
    public string? CollisionKind { get; init; }
    public string? Semantics { get; init; }
    public string? PathPart { get; init; }
    public string? ConflictPathPart { get; init; }
    public int? Utf8Bytes { get; init; }
    public int? Utf16CodeUnits { get; init; }
    public int? Limit { get; init; }
}

// portable-path.mjs, line for line. Upstream's /u regexes are ASCII- or code-unit-checks here: none of them needs a
// regex engine except the 8.3 short-name shape.
public static class PortablePath
{
    static readonly string[] Profiles = ["output", "repo", "archive"];
    static readonly string[] WindowsSafeProfiles = ["output", "archive"];
    static readonly string[] ReservedStems = ["con", "prn", "aux", "nul", "conin$", "conout$"];
    static readonly Regex WindowsShortName = new(@"~[1-9][0-9]*(?:\.|\z)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    static readonly Regex UriScheme = new(@"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    const int MaxSemanticVariants = 32;

    static PortablePathException PathError(string? value, string? profile, string reason, string message) =>
        new(reason, message) { Value = value, Profile = profile };

    static void AssertProfile(string profile)
    {
        if (Profiles.Contains(profile)) return;
        throw new PortablePathException("profile", $"Portable path profile must be one of: {string.Join(", ", Profiles)}.") { Profile = profile };
    }

    // validatePortablePath (:72). Returns nothing; upstream's return value is the input.
    public static void Validate(string value, string profile = "output")
    {
        AssertProfile(profile);
        if (value is null) throw PathError(value, profile, "type", "Portable path must be a string.");
        if (value.Length == 0) throw PathError(value, profile, "empty", "Portable path must not be empty.");
        if ((value.Length >= 3 && IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/') || value.StartsWith('/') || value.StartsWith('\\'))
            throw PathError(value, profile, "absolute", "Portable path must be relative.");
        if (value.Length >= 2 && IsAsciiLetter(value[0]) && value[1] == ':')
            throw PathError(value, profile, "drive-relative", "Portable path must not use a drive-relative Windows path.");
        if (UriScheme.IsMatch(value)) throw PathError(value, profile, "uri", "Portable path must not be a URI.");
        if (value.Contains('\\')) throw PathError(value, profile, "backslash", "Portable path must use forward slashes as separators.");

        var segments = value.Split('/');
        for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            PortablePathException Fail(string reason, string message, string? character = null) =>
                new(reason, message) { Value = value, Profile = profile, Segment = segment, SegmentIndex = segmentIndex, Character = character };

            if (segment.Length == 0) throw Fail("empty-segment", "Portable path must not contain empty segments.");
            if (segment is "." or "..") throw Fail("dot-segment", "Portable path must not contain dot segments.");
            foreach (var ch in segment)   // \p{Cc} = U+0000-001F, U+007F-009F: all BMP, so char.IsControl per code unit
                if (char.IsControl(ch)) throw Fail("control", "Portable path must not contain control characters.", ch.ToString());
            if (LoneSurrogate(segment) is char surrogate)
                throw Fail("unpaired-surrogate", "Portable path must not contain unpaired UTF-16 surrogates.", surrogate.ToString());

            if (!WindowsSafeProfiles.Contains(profile)) continue;
            if (segment.Contains(':')) throw Fail("windows-ads", "Portable path must not select a Windows alternate data stream.", ":");
            var invalid = segment.IndexOfAny(['<', '>', '"', '|', '?', '*']);
            if (invalid >= 0) throw Fail("windows-invalid-character", "Portable path contains a character that is invalid in Windows file names.", segment[invalid].ToString());
            if (segment[^1] is '.' or ' ') throw Fail("windows-trailing-dot-space", "Portable path segments must not end with a dot or space.");
            if (IsReservedName(segment)) throw Fail("windows-reserved-name", "Portable path must not use a reserved Windows device name.");
            if (WindowsShortName.IsMatch(segment)) throw Fail("windows-short-name", "Portable path must not use a Windows 8.3 short-name shape.");
            var utf8Bytes = Encoding.UTF8.GetByteCount(segment);   // no lone surrogates left, so this equals Buffer.byteLength
            var utf16CodeUnits = segment.Length;
            if (utf8Bytes > 255 || utf16CodeUnits > 255)
                throw new PortablePathException("component-too-long", "Portable path segments must fit both UTF-8 and UTF-16 filesystem component limits.")
                { Value = value, Profile = profile, Segment = segment, SegmentIndex = segmentIndex, Utf8Bytes = utf8Bytes, Utf16CodeUnits = utf16CodeUnits, Limit = 255 };
        }
    }

    static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    // /[\uD800-\uDFFF]/u: matches a surrogate only when it is not half of a pair.
    internal static char? LoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(s[i])) return s[i];
        }
        return null;
    }

    // /^(?:con|prn|aux|nul|conin\$|conout\$|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)/iu — no non-ASCII character simple-case-folds
    // onto these letters, so the /i is an ASCII lowering.
    static bool IsReservedName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = AsciiLower(dot < 0 ? segment : segment[..dot]);
        return ReservedStems.Contains(stem) || (stem.Length == 4 && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
    }

    static string AsciiLower(string s) => string.Create(s.Length, s, (span, src) => { for (var i = 0; i < src.Length; i++) span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] | 0x20) : src[i]; });

    sealed record Entry(int Index, string Value);

    static PortablePathException CollisionError(IReadOnlyList<string> values, string profile, int index, int conflictIndex, string reason,
        string collisionKind, string? semantics, string pathPart, string conflictPathPart) =>
        new(reason, $"Portable paths at indexes {Js.Num(conflictIndex)} and {Js.Num(index)} collide under portable filesystem semantics.")
        {
            Value = values[index], Profile = profile, Index = index, ConflictIndex = conflictIndex, ConflictValue = values[conflictIndex],
            CollisionKind = collisionKind, Semantics = semantics, PathPart = pathPart, ConflictPathPart = conflictPathPart,
        };

    // toLocale{Lower,Upper}Case('en-US') equal toLowerCase/toUpperCase for every code point (Node v24): Js's full case mapping.
    static List<string> SemanticEnvelope(string value, string profile, int index)
    {
        var variants = new List<string> { value };
        var seen = new HashSet<string>(StringComparer.Ordinal) { value };
        Func<string, string>[] transforms =
        [
            c => c.Normalize(NormalizationForm.FormC),
            c => c.Normalize(NormalizationForm.FormD),
            Js.ToLowerCase,
            Js.ToUpperCase,
        ];
        for (var cursor = 0; cursor < variants.Count; cursor++)
            foreach (var transform in transforms)
            {
                var transformed = transform(variants[cursor]);
                if (seen.Contains(transformed)) continue;
                if (variants.Count >= MaxSemanticVariants)
                    throw new PortablePathException("semantic-expansion", "Portable path semantic comparison exceeded its bounded Unicode expansion.")
                    { Value = value, Profile = profile, Index = index, Limit = MaxSemanticVariants };
                seen.Add(transformed);
                variants.Add(transformed);
            }
        return variants;
    }

    static string ClassifySemanticCollision(string value, string conflictValue)
    {
        if (value == conflictValue) return "exact";
        if (value.Normalize(NormalizationForm.FormC) == conflictValue.Normalize(NormalizationForm.FormC)) return "normalization";
        var directLowerMatch = Js.ToLowerCase(value) == Js.ToLowerCase(conflictValue);
        var directUpperMatch = Js.ToUpperCase(value) == Js.ToUpperCase(conflictValue);
        return directLowerMatch || directUpperMatch ? "case" : "case-and-normalization";
    }

    static (Entry? entry, List<string> variants, string? semantics) FindSemanticCollision(Dictionary<string, Entry> index, string value, string profile, int sourceIndex)
    {
        var variants = SemanticEnvelope(value, profile, sourceIndex);
        foreach (var variant in variants)
            if (index.TryGetValue(variant, out var entry)) return (entry, variants, ClassifySemanticCollision(value, entry.Value));
        return (null, variants, null);
    }

    static void RememberSemanticEntry(Dictionary<string, Entry> index, Entry entry, List<string> variants)
    {
        foreach (var variant in variants) index.TryAdd(variant, entry);
    }

    // validatePortablePathSet (:300). The 'set-type' reason (non-array input) cannot arise from a typed list.
    public static void ValidateSet(IReadOnlyList<string> values, string profile)
    {
        AssertProfile(profile);
        var leafEntries = new Dictionary<string, Entry>(StringComparer.Ordinal);        // lookups only, never enumerated
        var directoryEntries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            try { Validate(value, profile); }
            catch (PortablePathException error) { error.Index ??= index; throw; }

            var leaf = FindSemanticCollision(leafEntries, value, profile, index);
            if (leaf.entry is not null)
                throw CollisionError(values, profile, index, leaf.entry.Index, leaf.semantics == "exact" ? "duplicate" : leaf.semantics!,
                    "entry", leaf.semantics, value, leaf.entry.Value);

            var segments = value.Split('/');
            var prefixes = Enumerable.Range(0, segments.Length - 1).Select(p => string.Join('/', segments[..(p + 1)])).ToList();
            foreach (var prefix in prefixes)
            {
                var directory = FindSemanticCollision(directoryEntries, prefix, profile, index);
                if (directory.entry is not null && directory.semantics != "exact")
                    throw CollisionError(values, profile, index, directory.entry.Index, $"directory-{directory.semantics}",
                        "directory-spelling", directory.semantics, prefix, directory.entry.Value);

                var file = FindSemanticCollision(leafEntries, prefix, profile, index);
                if (file.entry is not null)
                    throw CollisionError(values, profile, index, file.entry.Index, file.semantics == "exact" ? "tree-file" : $"tree-file-{file.semantics}",
                        "tree-file", file.semantics, prefix, file.entry.Value);
            }

            var asDirectory = FindSemanticCollision(directoryEntries, value, profile, index);
            if (asDirectory.entry is not null)
                throw CollisionError(values, profile, index, asDirectory.entry.Index, asDirectory.semantics == "exact" ? "tree-file" : $"tree-file-{asDirectory.semantics}",
                    "tree-file", asDirectory.semantics, value, asDirectory.entry.Value);

            RememberSemanticEntry(leafEntries, new(index, value), leaf.variants);
            foreach (var prefix in prefixes)
                RememberSemanticEntry(directoryEntries, new(index, prefix), SemanticEnvelope(prefix, profile, index));
        }
    }
}
