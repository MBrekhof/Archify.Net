using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

public sealed record PlaceholderMismatch(string Key, IReadOnlyList<string> Expected, IReadOnlyList<string>? Actual);
public sealed record OverrideReport(int SuppliedKeys, int AppliedKeys, IReadOnlyList<string> UnknownKeys, IReadOnlyList<PlaceholderMismatch> PlaceholderMismatches);
public sealed record LocaleReport(string? Locale, string ResolvedLocale, string? BundledLocale, bool Fallback, int TotalKeys, int TranslatedKeys,
    double Coverage, IReadOnlyList<string> FallbackKeys, OverrideReport Override);

// i18n.mjs. Bundled catalogs are immutable package data and stay static; DOCUMENT_CATALOGS (registerLocale's state)
// becomes one instance per render (RenderContext.Locale).
// ponytail: validateTranslations and catalogKeys are not ported: no renderer or CLI path calls them.
public sealed class LocaleCatalog
{
    public const string DefaultLocale = "en";

    // Bundled catalogs are package data, not renderer code. locales/manifest.json
    // enrolls the catalogs meta.locale selects automatically; only listed files
    // are read, from the embedded package resources. Adding a language changes data
    // only. en.json defines the canonical keys and the final fallback.
    // Mirrors common.schema.json#/$defs/locale.
    static readonly Regex LocaleTag = new("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    static readonly Regex CatalogFile = new("^[A-Za-z0-9][A-Za-z0-9._-]*\\.json$", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    static readonly Regex PlaceholderPattern = new("\\{([a-zA-Z0-9_]+)\\}", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    static readonly Regex TemplateKeyPattern = new("\\{\\{i18n:([a-zA-Z0-9_.-]+)\\}\\}", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

    sealed record ManifestEntry(string Locale, string File);
    sealed record Bundled(IReadOnlyDictionary<string, string> Usable, IReadOnlyDictionary<string, string> Messages);
    sealed record DocumentCatalog(string Locale, IReadOnlyDictionary<string, string> Messages);

    static readonly Dictionary<string, ManifestEntry> Manifest;           // lowercase tag → entry; lookup only
    public static IReadOnlyList<string> SupportedLocales { get; }         // manifest order
    static readonly string[] CanonicalKeys;                               // en.json file order
    static readonly Dictionary<string, string> En;
    static readonly Dictionary<string, HashSet<string>> CanonicalPlaceholders;
    static readonly Dictionary<string, Bundled> BundledCatalogs;          // keyed by bundled tag (manifest case)

    static LocaleCatalog()
    {
        (Manifest, SupportedLocales) = LoadManifest();
        var en = ReadLocaleData(Manifest[DefaultLocale].File);
        CanonicalKeys = [.. en.Select(p => p.Key)];
        En = new(StringComparer.Ordinal);
        foreach (var (key, value) in en)
        {
            var message = value.Str0();
            if (string.IsNullOrEmpty(message)) throw new InvalidOperationException($"Archify source message {Js.Quote(key)} must be a non-empty string");
            En[key] = message;
        }
        CanonicalPlaceholders = new(StringComparer.Ordinal);
        foreach (var key in CanonicalKeys) CanonicalPlaceholders[key] = ExtractPlaceholders(En[key]);
        // ponytail: upstream loads each bundled catalog on first use; all five load here so the static state is
        // immutable and thread-safe. Same messages, only an earlier failure point for a broken catalog file.
        BundledCatalogs = new(StringComparer.Ordinal) { [DefaultLocale] = new(En, En) };
        foreach (var locale in SupportedLocales)
        {
            if (locale == DefaultLocale) continue;
            var usable = Classify(ReadLocaleData(Manifest[locale.ToLowerInvariant()].File)).Usable;
            var messages = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in CanonicalKeys) messages[key] = usable.GetValueOrDefault(key) ?? En[key];
            BundledCatalogs[locale] = new(usable, messages);
        }
    }

    static JsonObject ReadLocaleData(string file)
    {
        try { return DiagramLoader.Parse(Resources.Text($"locales/{file}")); }
        catch (Exception error) { throw new InvalidOperationException($"Archify locale data {Js.Quote(file)} could not be loaded: {error.Message}", error); }
    }

    static (Dictionary<string, ManifestEntry>, IReadOnlyList<string>) LoadManifest()
    {
        if (ReadLocaleData("manifest.json")["catalogs"] is not JsonArray catalogs) throw new InvalidOperationException("Archify locale manifest must list catalogs");
        var byKey = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in catalogs)
        {
            var locale = entry.Str("locale"); var file = entry.Str("file");
            if (locale is null || locale.Length > 35 || !LocaleTag.IsMatch(locale))
                throw new InvalidOperationException($"Archify locale manifest has an invalid locale tag {(locale is null ? "undefined" : Js.Quote(locale))}");
            if (file is null || !CatalogFile.IsMatch(file))
                throw new InvalidOperationException($"Archify locale manifest has an invalid catalog file for {locale}");
            // Tags match case-insensitively, so two entries differing only in case
            // would make selection ambiguous.
            var key = locale.ToLowerInvariant();
            if (!byKey.TryAdd(key, new(locale, file))) throw new InvalidOperationException($"Archify locale manifest lists {locale} more than once");
            order.Add(locale);
        }
        if (byKey.GetValueOrDefault(DefaultLocale)?.Locale != DefaultLocale)
            throw new InvalidOperationException($"Archify locale manifest must enroll the {DefaultLocale} source catalog");
        return (byKey, order);
    }

    static HashSet<string> ExtractPlaceholders(string message) =>
        [.. PlaceholderPattern.Matches(message).Select(m => m.Groups[1].Value)];

    static List<string> Sorted(IEnumerable<string> values) { var list = values.ToList(); Js.SortDefault(list); return list; }

    sealed record Classified(List<string> Supplied, Dictionary<string, string> Usable, List<string> UnknownKeys, List<PlaceholderMismatch> PlaceholderMismatches);

    // Splits translation data into the entries that can replace a canonical
    // message and the ones that cannot. Rejected entries never replace a valid
    // lower-priority message.
    // Object.keys order equals JsonObject order: the schema's key pattern rules out integer-like keys.
    static Classified Classify(JsonObject? translations)
    {
        var supplied = translations?.Select(p => p.Key).ToList() ?? [];
        var usable = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknownKeys = new List<string>();
        var placeholderMismatches = new List<PlaceholderMismatch>();
        foreach (var key in supplied)
        {
            if (!CanonicalPlaceholders.TryGetValue(key, out var canonical)) { unknownKeys.Add(key); continue; }
            var value = translations![key].Str0();
            var expected = Sorted(canonical);
            if (string.IsNullOrEmpty(value)) { placeholderMismatches.Add(new(key, expected, null)); continue; }
            var actual = ExtractPlaceholders(value);
            if (canonical.SetEquals(actual)) usable[key] = value;
            else placeholderMismatches.Add(new(key, expected, Sorted(actual)));
        }
        return new(supplied, usable, unknownKeys, placeholderMismatches);
    }

    public static string? BundledLocaleFor(string? locale) => locale is null ? null : Manifest.GetValueOrDefault(locale.ToLowerInvariant())?.Locale;

    static bool IsEnglishTag(string locale)
    {
        var key = locale.ToLowerInvariant();
        return key == DefaultLocale || key.StartsWith($"{DefaultLocale}-", StringComparison.Ordinal);
    }

    // Resolves each canonical message as: valid meta.translations value →
    // selected bundled-catalog value → English source. Pure; returns the resolved
    // catalog with a report that separates rejected overrides from real English
    // fallback gaps. Omitted or empty translations mean no override. A tag with
    // neither a bundled catalog nor a usable override falls back to English UI
    // and language metadata.
    public static (IReadOnlyDictionary<string, string> Messages, LocaleReport Report) ResolveCatalog(string? locale, JsonObject? translations = null)
    {
        var @override = Classify(translations);
        var bundledLocale = BundledLocaleFor(locale);
        var fallback = !string.IsNullOrEmpty(locale) && bundledLocale is null && @override.Usable.Count == 0;
        var resolvedLocale = string.IsNullOrEmpty(locale) || fallback ? DefaultLocale : bundledLocale ?? locale;
        var bundled = bundledLocale is null ? null : BundledCatalogs[bundledLocale];
        var english = IsEnglishTag(resolvedLocale);
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        var fallbackKeys = new List<string>();
        foreach (var key in CanonicalKeys)
        {
            if (@override.Usable.TryGetValue(key, out var supplied)) messages[key] = supplied;
            else if (bundled is not null && bundled.Usable.TryGetValue(key, out var shipped)) messages[key] = shipped;
            else
            {
                messages[key] = En[key];
                if (!english) fallbackKeys.Add(key);
            }
        }
        var translatedKeys = english ? CanonicalKeys.Length : CanonicalKeys.Length - fallbackKeys.Count;
        return (messages, new LocaleReport(
            string.IsNullOrEmpty(locale) ? null : locale,
            resolvedLocale,
            bundledLocale,
            fallback,
            CanonicalKeys.Length,
            translatedKeys,
            CanonicalKeys.Length > 0 ? (double)translatedKeys / CanonicalKeys.Length : 1,
            fallbackKeys,
            new OverrideReport(@override.Supplied.Count, @override.Usable.Count, @override.UnknownKeys, @override.PlaceholderMismatches)));
    }

    // Per-document catalogs installed by Register(). Tags are
    // case-insensitive, so the key is lowercased: renderers may pass the authored
    // meta.locale (zh-cn) or its resolved tag (zh-CN) and reach the same document
    // catalog. They never mutate a bundled catalog.
    readonly Dictionary<string, DocumentCatalog> documentCatalogs = new(StringComparer.Ordinal);   // lookup only

    DocumentCatalog? CatalogFor(string? locale)
    {
        if (locale is null) return null;
        if (documentCatalogs.TryGetValue(locale.ToLowerInvariant(), out var document)) return document;
        var bundledLocale = BundledLocaleFor(locale);
        return bundledLocale is null ? null : new(bundledLocale, BundledCatalogs[bundledLocale].Messages);
    }

    // Installs the resolved catalog for one authored locale and returns its
    // report. Re-registering the same tag replaces the previous document's
    // overrides; a fallback clears them.
    public LocaleReport Register(string? locale, JsonObject? translations)
    {
        var (messages, report) = ResolveCatalog(locale, translations);
        if (locale is null) return report;
        if (report.Fallback || locale.Length == 0) documentCatalogs.Remove(locale.ToLowerInvariant());
        else documentCatalogs[locale.ToLowerInvariant()] = new(report.ResolvedLocale, messages);
        return report;
    }

    static JsonArray Strings(IEnumerable<string> values) => [.. values.Select(v => (JsonNode?)JsonValue.Create(v))];

    static JsonObject MismatchJson(PlaceholderMismatch m) => new()
    {
        ["key"] = m.Key,
        ["expected"] = Strings(m.Expected),
        ["actual"] = m.Actual is null ? null : Strings(m.Actual),
    };

    // Agent-facing warnings for one document's meta.locale/meta.translations.
    // Pure, so the renderer and the CLI receipts (validate/deliver/finalize)
    // report the same diagnostics from the same input.
    public IReadOnlyList<Diagnostic> LocaleDiagnostics(DiagramType type, JsonObject meta)
    {
        var locale = meta.Str("locale");
        if (string.IsNullOrEmpty(locale)) return [];
        var report = ResolveCatalog(locale, meta.Obj("translations")).Report;
        var diagnostics = new List<Diagnostic>();
        var (unknownKeys, placeholderMismatches, appliedKeys) = (report.Override.UnknownKeys, report.Override.PlaceholderMismatches, report.Override.AppliedKeys);
        var rejected = unknownKeys.Count + placeholderMismatches.Count;
        if (rejected > 0)
        {
            var kept = report.BundledLocale is not null ? $"bundled {report.BundledLocale} message" : "English message";
            diagnostics.Add(new(
                "i18n/invalid-translation",
                Severity.Warning,
                $"meta.translations for locale {Js.Quote(locale)} has {Js.Num(rejected)} unusable {(rejected == 1 ? "entry" : "entries")} ({Js.Num(unknownKeys.Count)} unknown, {Js.Num(placeholderMismatches.Count)} placeholder mismatch); each keeps its {kept}.",
                new JsonObject { ["diagramType"] = type.Name(), ["path"] = "/meta/translations" },
                new JsonObject
                {
                    ["locale"] = locale,
                    ["unknownKeys"] = Strings(unknownKeys.Take(10)),
                    ["unknownKeysTotal"] = unknownKeys.Count,
                    ["placeholderMismatches"] = new JsonArray([.. placeholderMismatches.Take(10).Select(m => (JsonNode?)MismatchJson(m))]),
                    ["placeholderMismatchesTotal"] = placeholderMismatches.Count,
                },
                ["Remove each unknown key or rename it to a canonical message key.", "Use exactly the {placeholders} of the English source string for each mismatched key."],
                null));
        }
        if (report.Fallback)
        {
            diagnostics.Add(new(
                "i18n/locale-fallback",
                Severity.Warning,
                $"meta.locale {Js.Quote(locale)} has no bundled catalog and no usable meta.translations; the Viewer chrome and <html lang> fall back to English.",
                new JsonObject { ["diagramType"] = type.Name(), ["path"] = "/meta/locale" },
                new JsonObject { ["locale"] = locale, ["bundledLocales"] = Strings(SupportedLocales) },
                ["Supply meta.translations for this locale.", $"Use a bundled locale: {string.Join(", ", SupportedLocales)}."],
                null));
        }
        else if (report.FallbackKeys.Count > 0)
        {
            var source = string.Join(" and ", new[]
            {
                report.BundledLocale is not null ? $"the bundled {report.BundledLocale} catalog" : null,
                appliedKeys > 0 ? $"{Js.Num(appliedKeys)} meta.translations {(appliedKeys == 1 ? "entry" : "entries")}" : null,
            }.Where(s => !string.IsNullOrEmpty(s)));
            var missingKeys = report.FallbackKeys.Take(10).ToList();
            var englishSource = new JsonObject();
            foreach (var key in missingKeys) englishSource[key] = En[key];
            diagnostics.Add(new(
                "i18n/translation-coverage",
                Severity.Warning,
                $"meta.locale {Js.Quote(locale)} resolves {Js.Num(report.TranslatedKeys)}/{Js.Num(report.TotalKeys)} renderer-owned messages ({Js.Num(Math.Floor(report.Coverage * 100))}%) from {source}; {Js.Num(report.FallbackKeys.Count)} fall back to English.",
                new JsonObject { ["diagramType"] = type.Name(), ["path"] = "/meta/translations" },
                new JsonObject
                {
                    ["locale"] = locale,
                    ["resolvedLocale"] = report.ResolvedLocale,
                    ["bundledLocale"] = report.BundledLocale,
                    ["translatedKeys"] = report.TranslatedKeys,
                    ["totalKeys"] = report.TotalKeys,
                    ["appliedOverrideKeys"] = appliedKeys,
                    ["missingKeys"] = Strings(missingKeys),
                    ["missingKeysTotal"] = report.FallbackKeys.Count,
                    // English source for the listed gaps, so a caller can translate
                    // exactly those keys without re-reading the whole catalog.
                    ["englishSource"] = englishSource,
                },
                ["Add each missing key to meta.translations, translating its English source string and keeping its {placeholders}."],
                null));
        }
        return diagnostics;
    }

    public string ResolveLocale(string? locale) => CatalogFor(locale)?.Locale ?? DefaultLocale;

    public static string FormatMessage(string template, IReadOnlyDictionary<string, string>? values) =>
        PlaceholderPattern.Replace(template, m => values is not null && values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public string Translate(string? locale, string key, IReadOnlyDictionary<string, string>? values = null)
    {
        var catalog = CatalogFor(locale)?.Messages ?? En;
        if (!catalog.TryGetValue(key, out var message))
            throw new InvalidOperationException($"Missing Archify i18n message {Js.Quote(key)} for {ResolveLocale(locale)}");
        return FormatMessage(message, values);
    }

    public string TranslateCount(string? locale, string key, int count, IReadOnlyDictionary<string, string>? values = null)
    {
        var suffix = count == 1 ? "one" : "other";
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in values ?? new Dictionary<string, string>()) merged[k] = v;
        merged["count"] = Js.Num(count);
        return Translate(locale, $"{key}.{suffix}", merged);
    }

    // Object.entries(messages) order is canonical (en.json) order for every catalog.
    public JsonObject ViewerCatalog(string? locale)
    {
        var messages = CatalogFor(locale)?.Messages ?? En;
        var result = new JsonObject();
        foreach (var key in CanonicalKeys) if (key.StartsWith("viewer.", StringComparison.Ordinal)) result[key] = messages[key];
        return result;
    }

    public string LocalizeTemplate(string template, string? locale) =>
        TemplateKeyPattern.Replace(template, m => Js.EscapeHtml(Translate(locale, m.Groups[1].Value)));
}

file static class JsonStr
{
    // typeof value === 'string' ? value : null
    public static string? Str0(this JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
