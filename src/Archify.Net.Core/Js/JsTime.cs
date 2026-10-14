using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Archify.Net;

// ponytail: V8 parity — Date.parse on the timeline schema's timestamp shape, Intl.DateTimeFormat time-zone acceptance and the
// formatToParts wall clock (render-timeline.mjs:61-133). Fixtures: tests/fixtures/js/dates.json, time-zones.json.
public static partial class Js
{
    const long DayMs = 86_400_000;

    // ponytail: V8 parity — timeline.schema.json:128 with capture groups (JS `$` → `\z`).
    static readonly Regex IsoInstant = new(@"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,3}))?)?(?:Z|([+-])(\d{2}):(\d{2}))\z", Es);
    // ponytail: V8 parity — the three fixed-offset zone spellings Intl accepts: +08:00, +0800, +08.
    static readonly Regex FixedOffset = new(@"^([+-])(\d{2})(?::?(\d{2}))?\z", Es);
    // ponytail: V8 parity — off-Windows fallback guard for TryFindSystemTimeZoneById, which also accepts Windows display ids
    // ("China Standard Time") that V8 rejects. On Windows the ICU list below excludes them by itself.
    static readonly Regex IanaShaped = new(@"/|^[A-Z]{3,4}\z", Es);

    // ponytail: V8 parity — Date.parse for the schema shape only; anything else is NaN (documented simplification: V8's legacy
    // parser accepts more, but the schema rejects those strings before any renderer runs, golden.mjs:192).
    public static double DateParse(string s)
    {
        var m = IsoInstant.Match(s);
        if (!m.Success) return double.NaN;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].ValueSpan) : 0;
        int mo = G(2), d = G(3), h = G(4), mi = G(5), sec = G(6);
        int frac = m.Groups[7].Success ? int.Parse(m.Groups[7].Value.PadRight(3, '0')) : 0;   // .5 = 500 ms, .12 = 120 ms
        int oh = G(9), om = G(10);
        if (mo is < 1 or > 12 || d is < 1 or > 31 || mi > 59 || sec > 59 || oh > 23 || om > 59) return double.NaN;
        if (h > 24 || (h == 24 && (mi | sec | frac) != 0)) return double.NaN;   // 24 only as 24:00[:00[.000]]
        long offsetMs = (oh * 60L + om) * 60_000 * (m.Groups[8].Value == "-" ? -1 : 1);
        return DaysFromCivil(G(1), mo, d) * DayMs + h * 3_600_000L + mi * 60_000L + sec * 1000L + frac - offsetMs;   // day 30 of Feb overflows into March, like MakeDay
    }

    /// <summary>
    /// True iff <c>new Intl.DateTimeFormat('en-US', { timeZone: id })</c> does not throw (time-zones.json "valid"). IANA ids match
    /// case-insensitively against ICU's own list, as V8 does.
    /// Deviation, by host: a Windows host with icu.dll gets the full ICU list. On non-Windows hosts the list is not read, and only
    /// exact-case IANA ids are accepted, through TimeZoneInfo. On Windows without ICU (builds before 10 1903, Server 2019, or
    /// System.Globalization.UseNls=true) TimeZoneInfo cannot resolve IANA ids, so only UTC and fixed-offset zones are accepted.
    /// Deviation: ids present in V8's ICU but absent from, or unresolvable by, the Windows ICU/TimeZoneInfo build are rejected; the
    /// set varies with the OS build. On this machine (ICU 72.1, 2026-10-14) examples are CET, EET, MET, WET, America/Ciudad_Juarez,
    /// Antarctica/Troll, Asia/Urumqi, Asia/Kashgar, Antarctica/Vostok, America/Coyhaique and the SystemV/* legacy ids.
    /// </summary>
    // ponytail: V8 parity — rule order: UTC, fixed offset, exact IANA→Windows mapping, then the ICU case-insensitive list.
    public static bool IsIanaTimeZone(string id) =>
        id == "UTC" || FixedOffsetMs(id) is not null || TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)
        || (IcuZones.Value is null
            ? IanaShaped.IsMatch(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)
            : CanonicalZoneId(id) is { } c && TimeZoneInfo.TryFindSystemTimeZoneById(c, out _));

    // ponytail: V8 parity — ICU's spelling of a zone id, matched case-insensitively ("asia/shanghai" → "Asia/Shanghai"); null when
    // ICU does not list it or the ICU list is unavailable (non-Windows, or Windows without icu.dll).
    internal static string? CanonicalZoneId(string id) => IcuZones.Value?.GetValueOrDefault(id);

    // ponytail: V8 parity — ICU's zone ids (aliases included, no two differ only by case), read once; null when unavailable.
    static readonly Lazy<Dictionary<string, string>?> IcuZones = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            int status = 0;
            var e = Icu.ucal_openTimeZones(ref status);
            if (status > 0 || e == IntPtr.Zero) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (IntPtr p; (p = Icu.uenum_unext(e, out int len, ref status)) != IntPtr.Zero;)
                    if (Marshal.PtrToStringUni(p, len) is { } zone) map[zone] = zone;
            }
            finally { Icu.uenum_close(e); }
            return status > 0 ? null : map;
        }
        catch { return null; }   // ponytail: any failure (no icu.dll before Windows 10 1903, missing export, …) degrades to the exact-case path
    });

    // ponytail: ucal_openTimeZones / uenum_unext / uenum_close from the OS icu.dll (Windows 10 1903+), Windows-only. Linux ICU exports
    // version-suffixed names (ucal_openTimeZones_74); add a suffix probe if the off-Windows deviation matters.
    static class Icu
    {
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr ucal_openTimeZones(ref int status);
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr uenum_unext(IntPtr e, out int len, ref int status);
        [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void uenum_close(IntPtr e);
    }

    // ponytail: V8 parity — ±HH[:MM] / ±HHMM with hour ≤ 23 and minute ≤ 59; `+08` means +08:00. Null when not a fixed offset.
    static long? FixedOffsetMs(string id)
    {
        var m = FixedOffset.Match(id);
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[2].ValueSpan), mi = m.Groups[3].Success ? int.Parse(m.Groups[3].ValueSpan) : 0;
        if (h > 23 || mi > 59) return null;
        return (h * 60L + mi) * 60_000 * (m.Groups[1].Value == "-" ? -1 : 1);
    }

    // ponytail: V8 parity — one formatToParts row: two-digit fields, BC years as 1 - year (so year 0 → "0").
    public readonly record struct WallClock(string Y, string Mo, string D, string H, string Mi, string S);

    // ponytail: V8 parity — wall(ms), render-timeline.mjs:116-119. Callers gate the zone with IsIanaTimeZone (TimeZoneInfo throws otherwise).
    // Deviation (stated): non-fixed zones use the OS zone table, so LMT years (< ~1900) and pre-rule years differ from ICU.
    public static WallClock ZoneWall(string timeZone, double ms)
    {
        long floorMs = (long)Math.Floor(ms / 1000) * 1000;
        long offsetMs = FixedOffsetMs(timeZone) ?? ZoneOffsetMs(TimeZoneInfo.FindSystemTimeZoneById(CanonicalZoneId(timeZone) ?? timeZone), floorMs);
        long local = floorMs + offsetMs, days = FloorDiv(local, DayMs), t = local - days * DayMs;
        var (y, mo, d) = CivilFromDays(days);
        static string Two(long v) => Num(v).PadLeft(2, '0');
        return new(Num(y), Two(mo), Two(d), Two(t / 3_600_000), Two(t / 60_000 % 60), Two(t / 1000 % 60));
    }

    // Integer ticks: TimeSpan.TotalMilliseconds is a double and rounds MaxMs up past DateTime.MaxValue (9999-12-31T24:00Z threw).
    static readonly long MinMs = (DateTime.MinValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond,
        MaxMs = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
    static long ZoneOffsetMs(TimeZoneInfo z, long ms) =>
        (long)(ms >= MinMs && ms <= MaxMs ? z.GetUtcOffset(DateTime.UnixEpoch.AddMilliseconds(ms)) : z.BaseUtcOffset).TotalMilliseconds;

    static long FloorDiv(long a, long b) => a / b - (a % b < 0 ? 1 : 0);

    // ponytail: V8 parity — Howard Hinnant's days_from_civil (proleptic Gregorian, day 0 = 1970-01-01), the arithmetic of V8's MakeDay.
    internal static long DaysFromCivil(long y, int m, int d)
    {
        if (m <= 2) y--;
        long era = (y >= 0 ? y : y - 399) / 400, yoe = y - era * 400;
        long doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1, doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    // ponytail: V8 parity — Hinnant's civil_from_days, inverse of DaysFromCivil.
    internal static (long y, int m, int d) CivilFromDays(long z)
    {
        z += 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097, doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365, doy = doe - (365 * yoe + yoe / 4 - yoe / 100), mp = (5 * doy + 2) / 153;
        int d = (int)(doy - (153 * mp + 2) / 5 + 1), m = (int)(mp < 10 ? mp + 3 : mp - 9);
        return (yoe + era * 400 + (m <= 2 ? 1 : 0), m, d);
    }
}
