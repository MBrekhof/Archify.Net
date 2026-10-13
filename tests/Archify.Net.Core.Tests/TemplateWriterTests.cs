using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class TemplateWriterTests
{
    const string SvgSentinel = "\u0001SVG\u0001", CardsSentinel = "\u0001CARDS\u0001";

    [Theory]
    [InlineData("web-app.architecture.json", "web-app-rendered.html")]
    [InlineData("cache-miss-request.sequence.json", "sequence-cache-miss-request.html")]
    [InlineData("orders.erd.json", "erd-orders-rendered.html")]
    public void Reassembling_golden_reproduces_golden(string input, string golden)
    {
        var html = Fixtures.Lf(Fixtures.Upstream($"examples/{golden}"));
        var diagram = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream($"examples/{input}")));
        var meta = diagram["meta"]!.AsObject();
        var catalog = new LocaleCatalog(); if (meta.Str("locale") is not null) catalog.Register(meta.Str("locale"), meta.Obj("translations"));
        var template = Resources.Text("assets/template.html");
        var cards = Utils.RenderCards(diagram.Arr("cards"));
        string Apply(string svg, string cardsHtml) => TemplateWriter.Apply(template, meta.Str("title")!, meta.Str("subtitle"),
            svg, cardsHtml, meta.Str("locale"), meta.Str("visual_preset") ?? "classic", null, catalog);

        var skeleton = Apply(SvgSentinel, CardsSentinel);
        var i = skeleton.IndexOf(SvgSentinel, StringComparison.Ordinal); var j = skeleton.IndexOf(CardsSentinel, StringComparison.Ordinal);
        var prefix = skeleton[..i]; var mid = skeleton[(i + SvgSentinel.Length)..j]; var suffix = skeleton[(j + CardsSentinel.Length)..];
        Assert.StartsWith(prefix, html); Assert.EndsWith(suffix, html);
        var svgEnd = html.IndexOf(mid + cards + suffix, prefix.Length, StringComparison.Ordinal);
        Assert.True(svgEnd > prefix.Length, "golden does not contain mid + cards + suffix after the prefix");
        var svg = html[prefix.Length..svgEnd];
        Assert.StartsWith("      <svg viewBox=", svg);

        var result = Apply(svg, cards);
        if (result != html) Assert.Fail(ParityTests.FirstDifference(html, result));
    }

    [Fact] public void Dollar_patterns_in_title_are_literal()
    {
        var catalog = new LocaleCatalog();
        var r = TemplateWriter.Apply(Resources.Text("assets/template.html"), "Cost $& $' $$", null, "      <!-- ARCHIFY:SVG_SLOT_START -->\n      <svg/>\n      <!-- ARCHIFY:SVG_SLOT_END -->", "    <!-- ARCHIFY:CARDS_SLOT_START -->\n    <!-- ARCHIFY:CARDS_SLOT_END -->", null, "classic", null, catalog);
        Assert.Contains("<h1>Cost $&amp; $&#39; $$</h1>", r);
    }

    [Fact] public void RenderCards_matches_upstream_layout()
    {
        var cards = (JsonArray)JsonNode.Parse("[{\"title\":\"T\",\"dot\":\"frontend\",\"items\":[\"a<b\",\"c\"]}]")!;
        Assert.Equal("    <!-- Info Cards -->\n    <div class=\"cards\">\n      <div class=\"card\">\n        <div class=\"card-header\">\n          <div class=\"card-dot frontend\"></div>\n          <h3>T</h3>\n        </div>\n        <ul>\n          <li>a&lt;b</li>\n          <li>c</li>\n        </ul>\n      </div>\n    </div>", Utils.RenderCards(cards));
    }
}
