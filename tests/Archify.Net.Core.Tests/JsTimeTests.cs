using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class JsTimeTests
{
    static JsonNode Fx(string f) => JsonNode.Parse(File.ReadAllText(Fixtures.Fixture($"js/{f}")))!;

    [Fact] public void DateParse_matches_V8_on_the_schema_shape_and_its_edges()
    {   // inSchema:false rows are V8's legacy-parser results; the schema rejects those strings before any renderer runs, so they are not asserted.
        foreach (var e in Fx("dates.json").AsArray().Where(e => e!["inSchema"]!.GetValue<bool>()))
            Assert.Equal(e!["ms"]!.GetValue<string>(), Js.Num(Js.DateParse(e["s"]!.GetValue<string>())));
    }
    [Fact] public void Out_of_schema_strings_are_NaN()   // the documented simplification, pinned so nobody "improves" it into local-time parsing
    {
        Assert.True(double.IsNaN(Js.DateParse("2026-10-01T10:00:00"))); Assert.True(double.IsNaN(Js.DateParse("2026-10-01 10:00:00Z")));
    }
    [Fact] public void IsIanaTimeZone_matches_Intl_DateTimeFormat_acceptance()
    {
        foreach (var e in Fx("time-zones.json")["valid"]!.AsArray())
            Assert.True(e!["valid"]!.GetValue<bool>() == Js.IsIanaTimeZone(e["id"]!.GetValue<string>()), $"zone id \"{e["id"]}\"");
    }
    [Fact] public void Windows_display_ids_are_rejected_like_V8()   // Review Focus 2
    {
        Assert.False(Js.IsIanaTimeZone("China Standard Time")); Assert.False(Js.IsIanaTimeZone("Eastern Standard Time"));
        Assert.True(Js.IsIanaTimeZone("Asia/Shanghai")); Assert.True(Js.IsIanaTimeZone("asia/shanghai")); Assert.True(Js.IsIanaTimeZone("America/New_York"));
        Assert.True(Js.IsIanaTimeZone("UTC")); Assert.True(Js.IsIanaTimeZone("+08:00")); Assert.True(Js.IsIanaTimeZone("-05:30"));
        Assert.True(Js.IsIanaTimeZone("+0800")); Assert.True(Js.IsIanaTimeZone("+08")); Assert.False(Js.IsIanaTimeZone("+08:00:00")); Assert.False(Js.IsIanaTimeZone("+8:00"));
    }
    [Fact] public void ZoneWall_matches_formatToParts_on_every_guaranteed_row()
    {   // guaranteed = UTC and fixed offsets for every year, every zone for 2026/9999. The other rows are the stated OS-zone-table deviation (LMT
        // before ~1900, pre-rule years for Chatham/Lord Howe) and are kept in the fixture for information only.
        var rows = Fx("time-zones.json")["wall"]!.AsArray();
        Assert.Contains(rows, r => !r!["guaranteed"]!.GetValue<bool>());   // the fixture still carries the deviation rows
        foreach (var e in rows.Where(r => r!["guaranteed"]!.GetValue<bool>()))
        {
            var p = e!["parts"]!; var w = Js.ZoneWall(e["zone"]!.GetValue<string>(), e["ms"]!.D());
            Assert.Equal((p["y"]!.GetValue<string>(), p["mo"]!.GetValue<string>(), p["d"]!.GetValue<string>(), p["h"]!.GetValue<string>(), p["mi"]!.GetValue<string>(), p["s"]!.GetValue<string>()),
                (w.Y, w.Mo, w.D, w.H, w.Mi, w.S));
        }
    }
    [Fact] public void ZoneWall_year_zero_in_utc_and_fixed_offset() { Assert.Equal("0", Js.ZoneWall("UTC", Js.DateParse("0000-01-01T00:00:00Z")).Y); Assert.Equal(("08", "00"), (Js.ZoneWall("+08:00", 0).H, Js.ZoneWall("+08:00", 0).Mi)); }
    [Fact] public void Probe_edges_checked_against_node_24_12()   // V8 results recorded by hand in the task-3 report; not in the fixtures
    {
        Assert.Equal("1790899200000", Js.Num(Js.DateParse("2026-10-01T24:00Z"))); Assert.Equal("1790899200000", Js.Num(Js.DateParse("2026-10-01T24:00:00.000Z")));
        Assert.True(double.IsNaN(Js.DateParse("2026-10-01T24:00:00.001Z")));
        foreach (var id in new[] { "-0000", "Europe/Kyiv", "GMT0" }) Assert.True(Js.IsIanaTimeZone(id), id);
        foreach (var id in new[] { "+24:00", "+08:60", "UTC-08", "W. Europe Standard Time" }) Assert.False(Js.IsIanaTimeZone(id), id);
        var w = Js.ZoneWall("-05:30", -62167219200000); Assert.Equal(("-1", "12", "31", "18", "30", "00"), (w.Y, w.Mo, w.D, w.H, w.Mi, w.S));   // era BC year 2 → 1 - 2
    }
    [Fact] public void Canonical_zone_id_comes_from_the_ICU_list_not_the_TimeZoneInfo_cache()   // TimeZoneInfo only matches case once the exact id is cached
    {
        Assert.Equal("Asia/Shanghai", Js.CanonicalZoneId("asia/shanghai")); Assert.Equal("America/New_York", Js.CanonicalZoneId("AMERICA/NEW_YORK"));
        Assert.Equal("Etc/UTC", Js.CanonicalZoneId("etc/utc")); Assert.Equal("Japan", Js.CanonicalZoneId("japan"));
        Assert.Null(Js.CanonicalZoneId("China Standard Time")); Assert.Null(Js.CanonicalZoneId("Eastern Standard Time")); Assert.Null(Js.CanonicalZoneId("UTC-08"));
        Assert.Equal(Js.ZoneWall("Asia/Shanghai", 0), Js.ZoneWall("asia/shanghai", 0));
    }
    [Fact] public void ZoneWall_past_DateTime_MaxValue_takes_the_base_offset()   // node 24.12: formatToParts → 10000-01-01 00:00:00 AD
    {
        foreach (var s in new[] { "9999-12-31T24:00Z", "9999-12-31T23:00:00-01:00" })
        {
            var w = Js.ZoneWall("UTC", Js.DateParse(s)); Assert.Equal(("10000", "01", "01", "00", "00", "00"), (w.Y, w.Mo, w.D, w.H, w.Mi, w.S));
        }
        Assert.Equal("10000", Js.ZoneWall("Asia/Shanghai", Js.DateParse("9999-12-31T24:00Z")).Y);   // named zone: no throw
    }
    [Fact] public void Civil_round_trip() { foreach (var z in new long[] { -719468, -1, 0, 1, 19999, 2932896 }) { var (y, m, d) = Js.CivilFromDays(z); Assert.Equal(z, Js.DaysFromCivil(y, m, d)); } }
}
