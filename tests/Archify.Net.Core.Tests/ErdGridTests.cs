using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ErdGridTests
{
    static readonly JsonObject Fx = (JsonObject)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/erd-grid.json")))!;
    static readonly ErGrid ClassGrid = new(OriginX: 32, OriginY: 40, GapX: 128, GapY: 84, EntityW: 180);
    static GridEntity Ent(JsonNode n) => GridEntity.From(n.AsObject(), n["height"]?.D() ?? 0);
    static string Map(JsMap<double, double> m) => Js.Stringify(new JsonArray(m.Entries.Select(e => (JsonNode)new JsonArray(e.Key, e.Value)).ToArray()));
    static string PtJson(Pt p) => Js.Stringify(new JsonArray(double.IsNaN(p.X) ? "NaN" : p.X, double.IsNaN(p.Y) ? "NaN" : p.Y));

    [Fact] public void Defaults_match_upstream()
    {
        var d = Fx["defaults"]!; var g = ErdGrid.DefaultErGrid;
        Assert.Equal((d["gapX"]!.D(), d["gapY"]!.D(), d["entityW"]!.D(), d["headerH"]!.D(), d["rowH"]!.D()), (g.GapX, g.GapY, g.EntityW, g.HeaderH, g.RowH));
    }
    [Fact] public void ErGridLayout_cases()
    {
        foreach (var c in Fx["erGridLayout"]!.AsArray())
        {
            var grid = ErdGrid.ErGridLayout(c!["er"]!.AsObject());
            if (c["grid"] is null) Assert.Null(grid); else { Assert.NotNull(grid); Assert.Equal(c["grid"]!["gapX"]!.D(), grid!.GapX); }
        }
    }
    [Fact] public void EntityHeight_cases() { foreach (var c in Fx["entityHeight"]!.AsArray()) Assert.Equal(c!["height"]!.D(), ErdGrid.EntityHeight(c["entity"]!.AsObject(), ErdGrid.DefaultErGrid)); }
    [Fact] public void BandedLayout_and_ResolveEntityPos_match_upstream()
    {
        foreach (var c in Fx["banded"]!.AsArray())
        {
            var entities = c!["entities"]!.AsArray().Select(e => Ent(e!)).ToList();
            JsMap<double, double>? gaps = null;
            if (c["columnGaps"] is JsonArray ga) { gaps = new(); foreach (var p in ga) gaps.Set(p![0]!.D(), p[1]!.D()); }
            var b = ErdGrid.BandedLayout(entities, ClassGrid, gaps);
            var e = c["bands"]!;
            Assert.Equal(Js.Stringify(e["columnX"]), Map(b.ColumnX)); Assert.Equal(Js.Stringify(e["rowY"]), Map(b.RowY));
            Assert.Equal(Js.Stringify(e["widths"]), Map(b.Widths)); Assert.Equal(Js.Stringify(e["heights"]), Map(b.Heights));
            Assert.Equal((e["totalWidth"]!.D(), e["totalHeight"]!.D()), (b.TotalWidth, b.TotalHeight));
            var positions = c["positions"]!.AsArray();
            for (var i = 0; i < entities.Count; i++) Assert.Equal(Js.Stringify(positions[i]), PtJson(ErdGrid.ResolveEntityPos(entities[i], ClassGrid, b)));
        }
    }
    [Fact] public void ValidateErGridPlacement_messages_match_upstream()
    {
        var p = Fx["placement"]!;
        var er = new JsonObject { ["entities"] = new JsonArray(
            new JsonObject { ["id"] = "a", ["row"] = 0, ["col"] = 0 }, new JsonObject { ["id"] = "b", ["row"] = 0, ["col"] = 0 }, new JsonObject { ["id"] = "c", ["row"] = -1, ["col"] = 0 },
            new JsonObject { ["id"] = "d" }, new JsonObject { ["id"] = "e", ["row"] = 1.5, ["col"] = 0 }, new JsonObject { ["id"] = "f", ["pos"] = new JsonArray(1, 2) }) };
        er = DiagramLoader.Normalize(er);
        var grid = ErdGrid.ErGridLayout(er)!;
        var bands = ErdGrid.BandedLayout(er["entities"]!.AsArray().Select(e => GridEntity.From(e!.AsObject(), 50)).ToList(), grid);
        var problems = new List<string>(); ErdGrid.ValidateErGridPlacement(er, grid, bands, problems);
        Assert.Equal(p["withGrid"]!.AsArray().Select(x => x!.GetValue<string>()), problems);
        var noGrid = new List<string>(); ErdGrid.ValidateErGridPlacement(new JsonObject { ["entities"] = new JsonArray(new JsonObject { ["id"] = "z" }) }, null, null, noGrid);
        Assert.Equal(p["noGrid"]!.AsArray().Select(x => x!.GetValue<string>()), noGrid);
    }
}
