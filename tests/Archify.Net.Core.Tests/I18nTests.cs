using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class I18nTests
{
    [Fact] public void Supported_locales_match_manifest() => Assert.Equal(new[] { "en", "zh-CN", "zh-TW", "es", "ko", "ar" }, LocaleCatalog.SupportedLocales);
    [Fact] public void Unknown_locale_falls_back_to_en_and_reports()
    {
        var c = new LocaleCatalog();
        var r = c.Register("xx-YY", null);
        Assert.True(r.Fallback); Assert.Equal("en", c.ResolveLocale("xx-YY"));
    }
    [Fact] public void Case_insensitive_bundled_tag() { var c = new LocaleCatalog(); c.Register("zh-cn", null); Assert.Equal("zh-CN", c.ResolveLocale("zh-cn")); }
    [Fact] public void Translations_override_then_bundled_then_en()
    {
        var c = new LocaleCatalog();
        var es = JsonNode.Parse(Resources.Text("locales/es.json"))!.AsObject();
        c.Register("es", new JsonObject { ["page.title"] = "Mapa {title}" });
        Assert.Equal("Mapa X", c.Translate("es", "page.title", new Dictionary<string, string> { ["title"] = "X" }));   // override wins
        Assert.Equal(es["legend.architecture.database"]!.GetValue<string>(), c.Translate("es", "legend.architecture.database")); // bundled ("Base de datos")
        Assert.NotEqual(c.Translate("en", "legend.architecture.database"), c.Translate("es", "legend.architecture.database"));  // es "Frontend" equals en, database does not
        var r = c.Register("es", new JsonObject { ["page.title"] = "no placeholder" });                               // {title} missing
        var mismatch = Assert.Single(r.Override.PlaceholderMismatches);
        Assert.Equal("page.title", mismatch.Key);
        Assert.Equal(["title"], mismatch.Expected);
    }
    [Fact] public void Locale_diagnostics_use_i18n_codes()
    {
        var c = new LocaleCatalog();
        var meta = new JsonObject { ["locale"] = "xx-YY" };
        c.Register("xx-YY", null);
        Assert.Contains(c.LocaleDiagnostics(DiagramType.Architecture, meta), d => d.Code == "i18n/locale-fallback");
    }
    [Fact] public void Missing_key_throws_like_upstream() { var c = new LocaleCatalog(); var ex = Assert.Throws<InvalidOperationException>(() => c.Translate("en", "no.such.key")); Assert.StartsWith("Missing Archify i18n message", ex.Message); }
    [Fact] public void Sigils_and_definitions_match_fixture()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/sigils.json")))!;
        Assert.Equal(fx["defs"]!.GetValue<string>(), Utils.RenderDefinitions());
        Assert.Equal(fx["defsExtra"]!.GetValue<string>(), Utils.RenderDefinitions("<x/>"));
        foreach (var s in fx["sigils"]!.AsArray())
        {
            var k = s!["kind"]!.GetValue<string>();
            Assert.Equal(s["svg"]!.GetValue<string>(), Utils.RenderSemanticSigil(k, 10.5, 3));
            Assert.Equal(s["iconClock"]!.GetValue<string>(), Utils.RenderSemanticSigil(k, 0, 0, icon: "clock"));
            Assert.Equal("", Utils.RenderSemanticSigil(k, 0, 0, icon: "none"));
        }
    }
    [Fact] public void TextUnits_matches_fixture()
    {
        foreach (var e in JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/text-units.json")))!.AsArray())
            Assert.Equal(e!["units"]!.GetValue<int>(), Utils.TextUnits(e["text"]!.GetValue<string>()));
    }
}
