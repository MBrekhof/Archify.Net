using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// A JS `new Error(message)` the explainer modules throw without diagnostics; the facade reports it as internal/unclassified.
internal sealed class JsError(string message) : Exception(message);

// renderExplainer's options (render.mjs:37). Null is JS undefined: SourceText defaults to JSON.stringify(document), the
// maps and the diagnostics to empty.
internal sealed record RenderExplainerOptions(JsonObject? PreparedBundle = null, string? SourceText = null, JsonObject? SourceMap = null,
    string? SourcePath = null, JsonObject? PreparedSources = null, JsonObject? PreparedDiagrams = null, JsonArray? Diagnostics = null,
    string? PageDirectory = null)
{
    // render-explainer.mjs:21: {...bundle, preparedBundle: bundle, pageDirectory}
    public static RenderExplainerOptions FromBundle(JsonObject bundle, string? pageDirectory) => new(bundle, bundle.Str("sourceText"),
        bundle.Obj("sourceMap"), bundle.Str("sourcePath"), bundle.Obj("preparedSources"), bundle.Obj("preparedDiagrams"), bundle.Arr("diagnostics"), pageDirectory);
}

// archify 3.2.0 explainer/layout.mjs and explainer/render.mjs: the one-page reader for a validated canonical explainer and its
// separately prepared, immutable resources.
internal static partial class ExplainerRender
{
    // --- layout.mjs ---
    internal sealed class Item
    {
        public JsonNode? Id;
        public double Span, Min, Weight;
        public bool Explicit, Answer, Spacer;
        public JsonObject? Panel;
        public int PanelIndex;

        public JsonObject ToJson()
        {
            if (Spacer) return new() { ["spacer"] = true, ["span"] = Span };
            if (Answer) return new() { ["id"] = "answer", ["span"] = Span, ["min"] = Min, ["weight"] = Weight, ["answer"] = true };
            var json = new JsonObject();
            if (Panel!.TryGetPropertyValue("id", out var id)) json["id"] = id?.DeepClone();
            json["span"] = Span;
            json["min"] = Min;
            json["explicit"] = Explicit;
            json["weight"] = Weight;
            json["panel"] = Panel.DeepClone();
            return json;
        }
    }

    static readonly HashSet<string> Unsized = new(StringComparer.Ordinal) { "id", "type", "span", "target", "source", "diagramType", "path", "url", "revision" };
    [GeneratedRegex("""viewBox=["']([^"']+)["']""")] private static partial Regex ViewBox();
    [GeneratedRegex("[ ,]+")] private static partial Regex ViewBoxSeparators();

    // Array.from(value).length: code points, a lone surrogate counting as one.
    static int CodePoints(string s)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i++, count++) if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        return count;
    }
    static double ContentSize(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => CodePoints(s),
        JsonArray a => Sum(a.Select(ContentSize)),
        JsonObject o => Sum(o.Where(kv => !Unsized.Contains(kv.Key)).Select(kv => ContentSize(kv.Value))),
        _ => 0,
    };
    // reduce((n, v) => n + v, 0), left to right (Enumerable.Sum may vectorise).
    static double Sum(IEnumerable<double> values) { var n = 0.0; foreach (var v in values) n += v; return n; }

    static (double Span, double Min) Widths(JsonNode? block, JsonObject diagrams)
    {
        switch (block.Str("type"))
        {
            case "comparison": return block.Arr("columns")!.Count + 1 > 3 ? (8, 7) : (7, 6);
            case "code" or "diff": return (8, 7);
            case "chart": return (6, 5);
            case "snippet":
                var longest = Js.MaxOf(block.Str("text")!.Split('\n').Select(line => (double)line.Length));
                return longest > 72 ? (8, 7) : longest > 48 ? (6, 6) : (5, 4);
            case "diagram":
                var diagram = diagrams[block?["id"] is { } id ? Js.String(id) : "undefined"] as JsonObject;
                var match = diagram.Str("svg") is { } svg ? ViewBox().Match(svg) : null;
                var width = match is { Success: true } && ViewBoxSeparators().Split(match.Groups[1].Value) is { Length: > 2 } parts ? Js.ToNumber(parts[2]) : double.NaN;
                if (!(width != 0 && !double.IsNaN(width)))   // viewBox?.[2] || diagram?.spec?.meta?.canvas?.width || 0
                {
                    var canvas = diagram?["spec"]?["meta"]?["canvas"]?["width"];
                    width = canvas.Truthy() ? ToNumber(canvas) : 0;
                }
                return width > 1000 ? (12, 12) : (8, 8);
            default: return (4, 4);
        }
    }

    // layoutExplainerPanels (layout.mjs:5-34): order-preserving twelve-column packing.
    internal static List<List<Item>> Rows(JsonObject document, JsonObject diagrams)
    {
        var items = new List<Item> { new() { Span = 5, Min = 4, Weight = ContentSize(document["answer"]), Answer = true } };
        foreach (var (node, index) in document["panels"].AsArrayOrEmpty().Select((p, i) => (p, i)))
        {
            var panel = node!.AsObject();
            var (span, min) = panel["blocks"].AsArrayOrEmpty().Select(block => Widths(block, diagrams)).Aggregate((Span: 4.0, Min: 4.0), (best, next) => next.Span > best.Span ? next : best);
            var authored = panel.Num("span");   // panel.span ?? span
            items.Add(new() { Id = panel["id"], Span = authored ?? span, Min = authored ?? min, Explicit = panel.ContainsKey("span"), Weight = ContentSize(panel), Panel = panel, PanelIndex = index });
        }
        var rows = new List<List<Item>>();
        var row = new List<Item>();
        double used = 0;
        void Finish()
        {
            if (row.Count == 0) return;
            var remaining = 12 - used;
            var flexible = row.Where(item => !item.Explicit).ToList();
            Js.Sort(flexible, (a, b) => b.Weight - a.Weight);
            if (remaining != 0 && flexible.Count > 0) flexible[0].Span += remaining;
            else if (remaining != 0) row.Add(new() { Spacer = true, Span = remaining });
            rows.Add(row);
            row = [];
            used = 0;
        }
        foreach (var item in items)
        {
            if (used + item.Span > 12)
            {
                if (row.Count > 0 && Sum(row.Select(x => x.Min)) + item.Min <= 12)
                {
                    var deficit = used + item.Span - 12;
                    void Cut(Item x) { var d = Math.Min(deficit, x.Span - x.Min); x.Span -= d; deficit -= d; }
                    Cut(item);
                    var slack = row.ToList();
                    Js.Sort(slack, (a, b) => (b.Span - b.Min) - (a.Span - a.Min));
                    foreach (var x in slack) Cut(x);
                    used = Sum(row.Select(x => x.Span));
                }
                else Finish();
            }
            row.Add(item);
            used += item.Span;
            if (used == 12) Finish();
        }
        Finish();
        return rows;
    }

    /// <summary>layoutExplainerPanels' rows, as JSON.</summary>
    public static JsonArray Layout(JsonObject document, JsonObject? diagrams = null) =>
        new([.. Rows(document, diagrams ?? []).Select(row => (JsonNode?)new JsonArray([.. row.Select(item => (JsonNode?)item.ToJson())]))]);

    // --- render.mjs ---
    static readonly Lazy<string> Css = new(() => Resources.Text("explainer/reader.css"));
    static readonly Lazy<string> Script = new(() => Resources.Text("explainer/reader.js"));

    // escape (:13): String(value ?? '') with the five HTML characters escaped.
    static string E(JsonNode? value) => value is null ? "" : Js.EscapeHtml(Js.String(value));
    static string E(string? value) => Js.EscapeHtml(value);
    // json (:14): JSON.stringify with < > & U+2028 U+2029 as \u escapes, safe inside <script>.
    static string Json(JsonNode? value)
    {
        var text = Js.Stringify(value);
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(c is '<' or '>' or '&' or '\u2028' or '\u2029' ? $"\\u{(int)c:x4}" : c);
        return sb.ToString();
    }
    // `${value}`: a missing key is undefined.
    static string T(JsonNode? parent, string key) => parent switch
    {
        JsonObject o when o.TryGetPropertyValue(key, out var value) => Js.String(value),
        JsonArray a when int.TryParse(key, out var index) && index >= 0 && index < a.Count => Js.String(a[index]),
        _ => "undefined",
    };
    // Number(value) for a JSON value.
    static double ToNumber(JsonNode? value) => value switch
    {
        null => 0,
        JsonValue v when v.TryGetValue<string>(out var s) => Js.ToNumber(s),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue v => v.D(),
        _ => double.NaN,
    };
    // String(text ?? '') for renderInline.
    static string Text(JsonNode? value) => value is null ? "" : Js.String(value);

    // JS `===` between two members; an absent member is undefined.
    static bool StrictEquals(JsonNode? a, bool hasA, JsonNode? b, bool hasB)
    {
        if (!hasA || !hasB) return !hasA && !hasB;
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue x && b is JsonValue y)
        {
            var (kx, ky) = (x.GetValueKind(), y.GetValueKind());
            if (kx is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) return kx == ky;
            if (kx != ky) return false;
            return kx == System.Text.Json.JsonValueKind.Number ? x.D() == y.D() : x.GetValue<string>() == y.GetValue<string>();
        }
        return ReferenceEquals(a, b);
    }
    static bool Has(JsonNode? node, string key, out JsonNode? value)
    {
        value = null;
        return node is JsonObject o && o.TryGetPropertyValue(key, out value);
    }

    // Toolbar glyphs, drawn with currentColor so they follow the theme (:17-26).
    static string Icon(string path) => $"<svg class=\"tool-icon\" viewBox=\"0 0 16 16\" aria-hidden=\"true\" focusable=\"false\"><path d=\"{path}\"/></svg>";
    static readonly string Chevron = Icon("M4.5 6.5 8 10l3.5-3.5"), Check = Icon("M3.5 8.5 6.5 11.5 12.5 4.5"),
        ImageIcon = Icon("M2.5 3.5h11v9h-11zM2.5 10.5l3-3 3 3 2-2 3 3M10.5 6.25a.75.75 0 1 0 0 .01"), Print = Icon("M4.5 6V2.5h7V6M4.5 11.5h-2v-5h11v5h-2M4.5 9.5h7v4h-7z"),
        Expand = Icon("M9.5 2.5h4v4M13.5 2.5 9.5 6.5M6.5 13.5h-4v-4M2.5 13.5l4-4"), Close = Icon("M4 4l8 8M12 4l-8 8"), Export = Icon("M8 2.5v7M5 6.5l3 3 3-3M3 11v2.5h10V11");
    // The Archify symbol (docs/assets/archify-mark.svg), for the favicon and the colophon (:27-29).
    const string Mark = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 360\" fill=\"none\"><path d=\"M151 94 81 164C67 178 60 191 60 212V248H97V212C97 204 100 199 106 193L179 120Z\" fill=\"#0856F5\"/><path d=\"M238 135C252 132 261 126 270 116L321 167C334 180 339 194 339 213V248H303V213C303 204 300 199 294 193Z\" fill=\"#1DBBB7\"/><rect x=\"146\" y=\"16\" width=\"110\" height=\"104\" rx=\"30\" fill=\"#0856F5\"/><rect x=\"18\" y=\"238\" width=\"110\" height=\"104\" rx=\"28\" fill=\"#0856F5\"/><rect x=\"274\" y=\"238\" width=\"108\" height=\"104\" rx=\"28\" fill=\"#1DBBB7\"/></svg>";
    static readonly string Favicon = "data:image/svg+xml," + Js.EncodeUriComponent(Mark);

    // The reader dictionaries (:30-33) as JSON.stringify prints them: the `facts` functions drop out, leaving `facts: {}`.
    static readonly string En = """{"answer":"Answer","answerMark":"A","scope":"Applies to","sources":"Sources","print":"Print / PDF","image":"Long image","imageHints":["Sharpest","Smallest","Opens anywhere"],"export":"Export","style":"Style","styles":{"clean":"Clean","editorial":"Editorial","product":"Product","swiss":"Swiss"},"auto":"System","light":"Light","dark":"Dark","typical":"Typical value","explore":"Open interactive view","exploreTitle":"Zoom, find nodes, trace paths, upstream and downstream, export images or motion","close":"Close","loading":"Opening diagram…","graph":"Diagram structure","changes":"Structure changes","added":"Added","removed":"Removed","modified":"Modified","copy":"Copy","copied":"Copied","walk":"Step through","walkMessages":"Play messages","walkFlow":"Walk the flow","prev":"Back","next":"Next","end":"Done","ask":"Ask","askQuote":"Ask about this","askPlaceholder":"What is unclear, or what should this part cover?","askCopy":"Copy question","askCopied":"Copied. Paste it to your agent.","askCancel":"Cancel","askChips":["I do not follow this part","Go deeper","Give an example","What is this based on?"],"askPage":"Page","askFile":"File","askWhere":"Where","askQuoteLabel":"Quote","askQuestion":"Question","askNote":"Copies the page, the panel and its ID, so your agent can revise just this part.","askPatch":"Revise only this panel with Archify panel replacement (patch explainer), keeping its ID.","tagline":"Turn anything you want to understand, plan, or share into an interactive visual.","facts":{}}""";
    static readonly string Zh = """{"answer":"结论","answerMark":"答","scope":"适用于","sources":"来源","print":"打印 / PDF","image":"长图","imageHints":["最清晰","体积最小","兼容最好"],"export":"导出","style":"样式","styles":{"clean":"简洁","editorial":"杂志","product":"产品","swiss":"网格"},"auto":"系统","light":"浅色","dark":"深色","typical":"典型值","explore":"打开交互图","exploreTitle":"缩放、搜索节点、追踪路径和上下游，导出图片或动画","close":"关闭","loading":"正在打开交互图…","graph":"图的结构","changes":"结构改动","added":"新增","removed":"删除","modified":"修改","copy":"复制","copied":"已复制","walk":"逐步看","walkMessages":"逐条看消息","walkFlow":"沿流程看","prev":"上一步","next":"下一步","end":"结束","ask":"追问","askQuote":"追问这段","askPlaceholder":"哪里没看懂，或者希望这块补充什么？","askCopy":"复制追问","askCopied":"已复制，粘贴给 agent 即可","askCancel":"取消","askChips":["没看懂这块","展开讲讲","举个例子","依据是什么？"],"askPage":"页面","askFile":"文件","askWhere":"位置","askQuoteLabel":"引用","askQuestion":"问题","askNote":"会一并复制页面、面板位置和 ID，agent 可以只改这一块。","askPatch":"请用 Archify 的面板替换（patch explainer）只更新这个面板，保留它的 ID。","tagline":"把你想理解、规划或分享的事，变成可以互动的可视化作品。","facts":{}}""";
    // words.facts (:31-32)
    static string[] Facts(bool english, int panels, int diagrams, int sources, string? pinned)
    {
        var facts = new List<string>
        {
            english ? $"{panels} {(panels == 1 ? "panel" : "panels")}" : $"{panels} 个面板",
        };
        if (diagrams != 0) facts.Add(english ? $"{diagrams} {(diagrams == 1 ? "diagram" : "diagrams")} validated" : $"{diagrams} 张图已通过结构校验");
        if (sources != 0) facts.Add(english ? $"{sources} {(sources == 1 ? "source" : "sources")}" : $"{sources} 个来源");
        if (!string.IsNullOrEmpty(pinned)) facts.Add(english ? $"code pinned to {pinned}" : $"代码固定在提交 {pinned}");
        return [.. facts];
    }

    const string Ws = Js.WsClass;
    [GeneratedRegex($@"\n{Ws}*\n")] private static partial Regex Paragraphs();
    [GeneratedRegex($@"{Ws}+·{Ws}+")] private static partial Regex ScopeSeparator();
    [GeneratedRegex("""viewBox=["'][^"']*?[0-9.-]+[ ,]+[0-9.-]+[ ,]+([0-9.]+)""")] private static partial Regex NativeWidth();
    [GeneratedRegex(@"^lane-[0-9]+\z")] private static partial Regex BareLane();
    [GeneratedRegex(@"^<svg(?![A-Za-z0-9_])[^>]*>")] private static partial Regex ChartTag();
    [GeneratedRegex($@"{Ws}(?:width|height)=""[^""]*""")] private static partial Regex ChartSize();
    [GeneratedRegex(@"^data:image/(png|jpeg|webp|gif);base64,")] private static partial Regex ImageDataUrl();

    /// <summary>renderExplainer (render.mjs:36-128): the page. Throws ArchifyDiagnosticException for a missing prepared resource or
    /// a malformed diff, JsError for an inconsistent native child or bundle.</summary>
    public static string Render(JsonObject document, RenderExplainerOptions o)
    {
        var sourceText = o.SourceText ?? Js.Stringify(document);
        var sourceMap = o.SourceMap ?? [];
        var preparedSources = o.PreparedSources ?? [];
        var preparedDiagrams = o.PreparedDiagrams ?? [];
        var diagnostics = o.Diagnostics ?? [];
        var meta = document["meta"];
        // UPSTREAM-DEVIATION default-locale (Decision 3): upstream defaults a page without @meta locale to zh-CN (render.mjs:38).
        var locale = meta?["locale"].Truthy() == true ? Js.String(meta["locale"]) : "en";
        // Chinese pages get Chinese controls; every other language gets the English ones.
        var english = !locale.StartsWith("zh", StringComparison.Ordinal);
        var words = JsonNode.Parse(english ? En : Zh)!.AsObject();
        string W(string key) => T(words, key);
        var sources = (preparedSources["sources"].Truthy() ? preparedSources["sources"] : preparedSources) as JsonObject ?? [];
        var assets = (preparedSources["assets"].Truthy() ? preparedSources["assets"] : null) as JsonObject ?? [];
        var diagrams = (preparedDiagrams["diagrams"].Truthy() ? preparedDiagrams["diagrams"] : preparedDiagrams) as JsonObject ?? [];
        var sourceSha256 = ExplainerBundle.Hash(sourceText);
        var revision = ExplainerBundle.Revision(document, sourceText, preparedSources, preparedDiagrams, o.PreparedBundle?["dependencies"]);
        var footnotes = new JsonArray();
        var citations = new JsMap<string, int>();
        string Inline(JsonNode? text, string? panelId = null, string? blockId = null) => ExplainerInline.Render(Text(text),
            new InlineContext { Document = document, Sources = sources, Diagrams = diagrams, PanelId = panelId, BlockId = blockId, Footnotes = footnotes, Citations = citations });
        var panels = document["panels"].AsArrayOrEmpty().ToList();

        Exception Unavailable(JsonObject block, int panelIndex, int blockIndex, string message)
        {
            var pointer = $"/panels/{panelIndex}/blocks/{blockIndex}";
            var subject = new JsonObject();
            if (block.TryGetPropertyValue("type", out var type)) subject["component"] = type?.DeepClone();
            subject["path"] = pointer;
            if (o.SourcePath is not null) subject["input"] = o.SourcePath;
            foreach (var (key, value) in sourceMap[pointer] as JsonObject ?? []) subject[key] = value?.DeepClone();
            return new ArchifyDiagnosticException(message, [new("explainer/render-resource", Severity.Error, message, subject,
                new JsonObject { ["reason"] = message, ["example"] = "Prepare Git excerpts and native diagrams before rendering." }, [], null)]);
        }

        string NativeLink(JsonNode target, string text)
        {
            var nativeHash = target["node"].Truthy() ? $"focus={Js.EncodeUriComponent(Js.String(target["node"]))}"
                : target["route"].Truthy() ? $"route={Js.EncodeUriComponent(T(target["route"], "0"))}~{Js.EncodeUriComponent(T(target["route"], "1"))}" : "";
            var edges = target["edges"].Truthy() ? $" data-diagram-edges=\"{E(Js.Stringify(target["edges"]))}\"" : "";
            return $"<a class=\"diagram-link\" href=\"#{E(target["diagram"])}\" data-diagram-link=\"{E(target["diagram"])}\" data-diagram-hash=\"{E(nativeHash)}\"{edges}>{text}<span aria-hidden=\"true\"> ↗</span></a>";
        }

        string BlockHtml(JsonObject block, string? panelId, int panelIndex, int blockIndex)
        {
            string I(JsonNode? text) => Inline(text, panelId, block.Str("id"));
            string Each(JsonNode? list, Func<JsonNode?, string> html) => string.Concat(list.AsArrayOrEmpty().Select(html));
            var body = new StringBuilder();
            switch (block.Str("type"))
            {
                case "prose":
                    foreach (var text in Paragraphs().Split(block.Str("text")!)) body.Append($"<p>{I(text)}</p>");
                    break;
                case "list":
                    if (block["checklist"].Truthy()) { body.Append($"<ul class=\"checklist\">{Each(block["items"], text => $"<li><label><input type=\"checkbox\"><span>{I(text)}</span></label></li>")}</ul>"); break; }
                    var tag = block["ordered"].Truthy() ? "ol" : "ul";
                    body.Append($"<{tag} class=\"text-list\">{Each(block["items"], text => $"<li>{I(text)}</li>")}</{tag}>");
                    break;
                case "callout":
                    body.Append($"<aside class=\"callout {E(block["level"])}\">{(block["title"].Truthy() ? $"<h3>{I(block["title"])}</h3>" : "")}<p>{I(block["text"])}</p></aside>");
                    break;
                case "comparison":
                    string Cell(JsonNode? cell)
                    {
                        var assessed = cell?["assessment"].Truthy() == true;
                        var html = "";
                        if (assessed)
                        {
                            var fallback = Js.String(cell!["assessment"]) switch
                            {
                                "good" => english ? "Favorable" : "有利", "caution" => english ? "Tradeoff" : "有代价", "bad" => english ? "Unfavorable" : "不利", _ => null,
                            };
                            var verdict = cell["verdict"].Truthy() ? cell["verdict"] : fallback;
                            html = $"<span class=\"assessment {E(cell["assessment"])}\"><span class=\"assessment-meter\" aria-hidden=\"true\"><i></i><i></i><i></i></span><b>{I(verdict)}</b></span>";
                        }
                        var same = StrictEquals(cell?["text"], Has(cell, "text", out _), cell?["verdict"], Has(cell, "verdict", out _));
                        return $"<td>{html}{(assessed && same ? "" : $"<span class=\"cell-text\">{I(cell?["text"])}</span>")}</td>";
                    }
                    body.Append($"<div class=\"table-scroll\"><table><thead><tr><th scope=\"col\">{(block["labelColumn"].Truthy() ? I(block["labelColumn"]) : "")}</th>");
                    body.Append(Each(block["columns"], text => $"<th scope=\"col\">{I(text)}</th>"));
                    body.Append($"</tr></thead><tbody>{Each(block["rows"], row => $"<tr id=\"{E(row?["id"])}\"><th scope=\"row\">{I(row?["label"])}</th>{Each(row?["cells"], Cell)}</tr>")}</tbody></table></div>");
                    break;
                case "facts":
                    body.Append($"<dl class=\"facts\">{Each(block["entries"], entry => $"<div><dt>{I(entry?["key"])}</dt><dd>{I(entry?["value"])}</dd></div>")}</dl>");
                    break;
                case "metric":
                {
                    var value = block.Num("value");
                    var limit = block.Num("limit");
                    var over = value is not null && limit is not null && value > limit;
                    var unit = E(block["unit"].Truthy() ? block["unit"] : null);
                    var hasUnit = block["unit"].Truthy();
                    body.Append($"<div class=\"metric{(over ? " metric-over-limit" : "")}\" data-metric-status=\"{(over ? "over-limit" : value is null ? "limit-only" : "within-limit")}\"><span class=\"metric-label\">{I(block["label"])}</span><p class=\"metric-value\">");
                    body.Append(value is null ? $"{(english ? "Limit" : "上限")} {E(block["limit"])}<span>{unit}</span>"
                        : $"{E(block["value"])}{(hasUnit ? $"<span>{unit}</span>" : "")}{(limit is null ? "" : $"<small> / {E(block["limit"])}{unit}</small>")}");
                    body.Append("</p>");
                    if (value is { } v && limit is > 0) body.Append($"<meter min=\"0\" max=\"{E(block["limit"])}\" value=\"{E(Js.Num(Math.Max(0, Math.Min(v, limit.Value))))}\" aria-label=\"{E(block["label"])}\">{E(block["value"])} / {E(block["limit"])}</meter>");
                    if (over) body.Append($"<p class=\"metric-overflow\">{(english ? "Over limit by" : "超限")} {E(Js.Num(value!.Value - limit!.Value))}{(hasUnit ? $" {unit}" : "")}</p>");
                    if (block["typical"].Truthy()) body.Append($"<span class=\"typical\">{W("typical")}</span>");
                    if (block["note"].Truthy()) body.Append($"<p class=\"fine\">{I(block["note"])}</p>");
                    body.Append("</div>");
                    break;
                }
                case "steps":
                    body.Append($"<ol class=\"steps\">{Each(block["items"], item =>
                    {
                        var text = I(item?["text"]);
                        var lead = item?["target"].Truthy() == true ? NativeLink(item["target"]!, text) : text;
                        return $"<li id=\"{E(item?["id"])}\"><b>{lead}</b>{(item?["detail"].Truthy() == true ? $"<p>{I(item["detail"])}</p>" : "")}</li>";
                    })}</ol>");
                    break;
                case "diagram":
                {
                    var id = block.Str("id")!;
                    var diagram = diagrams[id] as JsonObject;
                    if (diagram is null || !diagram["svg"].Truthy() || !diagram["html"].Truthy()) throw Unavailable(block, panelIndex, blockIndex, $"Missing prepared native diagram: {id}");
                    var svgText = Js.String(diagram["svg"]);
                    var titleNode = diagram["spec"]?["meta"]?["title"];
                    var title = titleNode.Truthy() ? titleNode : block["id"];
                    var svg = StaticSvg(svgText, id);
                    var previewClass = PreviewPrefix(id);
                    var nativeStyle = ScopeNativeCss(diagram["styles"] is { } styles ? Js.String(styles) : NativeStyles(Js.String(diagram["html"])), "." + previewClass, previewClass + "-", svgText);
                    var widthMatch = NativeWidth().Match(svgText);
                    var nativeWidth = widthMatch.Success ? Js.ToNumber(widthMatch.Groups[1].Value) : double.NaN;
                    var lanes = diagram["spec"]?["lanes"].Truthy() == true ? diagram["spec"]!["lanes"].AsArrayOrEmpty().ToList() : [];
                    var singleLane = lanes.Count == 1 ? " single-lane bare-lanes"
                        : lanes.Count > 0 && lanes.All(lane => BareLane().IsMatch(T(lane, "id"))) ? " bare-lanes" : "";
                    var nodeSources = block["nodeSources"].Truthy() ? $" data-node-sources=\"{E(Js.Stringify(block["nodeSources"]))}\"" : "";
                    var width = nativeWidth > 0 ? $" style=\"--native-width:{Js.Num(Js.Round(nativeWidth))}px\"" : "";
                    body.Append($"<figure class=\"diagram\"><figcaption><span class=\"diagram-title\">{E(title)}</span><span class=\"diagram-actions interactive\"><button class=\"tool-button explore-button\" type=\"button\" data-open-diagram=\"{E(id)}\" title=\"{W("exploreTitle")}\">{Expand}<span>{W("explore")}</span></button></span></figcaption>");
                    body.Append($"<div class=\"diagram-preview {previewClass}{singleLane}\" role=\"img\" aria-label=\"{E(title)}\"{nodeSources}{width}><style>{nativeStyle}</style>{svg}</div></figure>");
                    body.Append($"<p class=\"diagram-fit-status fine\" role=\"status\" hidden></p><details class=\"diagram-semantics\" open><summary>{W("graph")}</summary><ul>");
                    body.Append(Each(diagram["semantic"]?["nodes"], node => $"<li>{E(node?["label"].Truthy() == true ? node["label"] : node?["id"])} <code>{E(node?["id"])}</code></li>"));
                    body.Append(Each(diagram["semantic"]?["edges"], edge => $"<li><code>{E(edge?["from"])}</code> → <code>{E(edge?["to"])}</code>{(edge?["label"].Truthy() == true ? $" · {E(edge["label"])}" : "")}</li>"));
                    body.Append("</ul></details>");
                    if (block["changes"] is JsonArray { Count: > 0 } changes)
                        body.Append($"<dl class=\"changes\"><dt>{W("changes")}</dt>{Each(changes, change => $"<dd><span class=\"chip {E(change?["change"])}\">{W(Js.String(change?["change"]))}</span> <code>{E(change?["target"])}</code></dd>")}</dl>");
                    break;
                }
                case "chart":
                {
                    var data = block["text"] is { } own ? own : preparedSources["charts"]?[Js.String(block["id"])];   // block.text ?? charts[id]
                    if (data is not JsonValue dv || !dv.TryGetValue<string>(out var text)) throw Unavailable(block, panelIndex, blockIndex, $"Missing prepared chart data: {T(block, "id")}");
                    var chart = ExplainerCharts.Compile(text);
                    if (!string.IsNullOrEmpty(chart.Error)) throw Unavailable(block, panelIndex, blockIndex, chart.Error);
                    var svg = ChartTag().Replace(chart.Svg!, tag => ChartSize().Replace(tag.Value, ""), 1);
                    body.Append($"<figure class=\"chart\" style=\"--native-width:{Js.Num(Js.Round(chart.Width ?? double.NaN))}px\">{svg}{(block["title"].Truthy() ? $"<figcaption>{I(block["title"])}</figcaption>" : "")}</figure>");
                    break;
                }
                case "snippet":
                {
                    var language = block["language"].Truthy();
                    body.Append($"<figure class=\"snippet\"><figcaption><span class=\"snippet-label\">{(block["title"].Truthy() ? I(block["title"]) : "")}{(language ? $"<code class=\"snippet-language\">{E(block["language"])}</code>" : "")}</span><button class=\"text-button interactive\" type=\"button\" data-copy>{W("copy")}</button></figcaption>");
                    body.Append($"<pre><code{(language ? $" class=\"language-{E(block["language"])}\"" : "")}>{E(block["text"])}</code></pre></figure>");
                    break;
                }
                case "code":
                {
                    var name = T(block, "source");
                    var source = sources[name] as JsonObject;
                    if (source is null || !source["path"].Truthy() || source["text"] is not JsonValue tv || !tv.TryGetValue<string>(out var text))
                        throw Unavailable(block, panelIndex, blockIndex, $"Code requires a Git excerpt: {name}");
                    var lines = source["lines"] is JsonArray l ? string.Join("–", l.Select(x => x is null ? "" : Js.String(x))) : "";
                    var language = block["language"].Truthy() ? $" class=\"language-{E(block["language"])}\"" : "";
                    body.Append($"<figure class=\"code\"><figcaption>{I($"[](source:{name})")}<span>{E(source["path"])}:{E(lines)}</span></figcaption><pre><code{language}>{E(text)}</code></pre></figure>");
                    break;
                }
                case "diff":
                {
                    var parsed = ExplainerDiff.Parse(block.Str("text")!, new DiffOptions(Component: "diff", Input: o.SourcePath, PanelId: panelId));
                    if (parsed.Diagnostics.Any(d => d.Severity == Severity.Error)) throw new ArchifyDiagnosticException($"Malformed diff: {T(block, "id")}", parsed.Diagnostics);
                    var caption = block["caption"].Truthy();
                    body.Append($"<figure class=\"diff\" data-diff-format=\"{parsed.Format}\"><figcaption>{(caption ? $"<span>{I(block["caption"])}</span>" : "")}<span class=\"diff-summary\"><span class=\"added\">+{T(parsed.Counts, "added")} {W("added")}</span> · <span class=\"removed\">−{T(parsed.Counts, "removed")} {W("removed")}</span></span></figcaption>");
                    body.Append($"<div class=\"diff-scroll\"><table class=\"diff-code\" aria-label=\"{(caption ? E(block["caption"]) : E(english ? "Code changes" : "代码改动"))}\"><thead><tr><th scope=\"col\">{(english ? "Old" : "原行")}</th><th scope=\"col\">{(english ? "New" : "新行")}</th><th scope=\"col\">{(english ? "Change" : "改动")}</th></tr></thead><tbody>");
                    foreach (var row in parsed.Rows)
                    {
                        var (oldLine, newLine) = (row!["oldLine"], row["newLine"]);
                        body.Append($"<tr class=\"diff-line {E(row["kind"])}\" data-diff-kind=\"{E(row["kind"])}\"{(oldLine is null ? "" : $" data-old-line=\"{Js.String(oldLine)}\"")}{(newLine is null ? "" : $" data-new-line=\"{Js.String(newLine)}\"")}><td class=\"diff-number\">{(oldLine is null ? "" : Js.String(oldLine))}</td><td class=\"diff-number\">{(newLine is null ? "" : Js.String(newLine))}</td><td><code>{E(row["raw"])}</code></td></tr>");
                    }
                    body.Append("</tbody></table></div></figure>");
                    break;
                }
                case "image":
                {
                    var asset = assets[T(block, "id")] as JsonObject;
                    if (asset?["dataUrl"].Truthy() != true || !ImageDataUrl().IsMatch(Js.String(asset["dataUrl"])))
                        throw Unavailable(block, panelIndex, blockIndex, $"Missing contained image: {T(block, "id")}");
                    body.Append($"<figure class=\"image\"><img src=\"{E(asset["dataUrl"])}\" alt=\"{E(block["alt"])}\">{(block["caption"].Truthy() ? $"<figcaption>{I(block["caption"])}</figcaption>" : "")}</figure>");
                    break;
                }
                default: throw new JsError($"Unsupported explainer block: {T(block, "type")}");
            }
            return $"<div class=\"block block-{E(block["type"])}\" id=\"{E(block["id"])}\" data-block-type=\"{E(block["type"])}\">{body}</div>";
        }

        string ScopeHtml(string scope)
        {
            var parts = ScopeSeparator().Split(scope).Select(Js.Trim).Where(part => part.Length > 0).ToList();
            return parts.Count > 1 ? string.Concat(parts.Select(part => $"<span class=\"scope-chip\">{Inline(part)}</span>")) : $"<span>{Inline(scope)}</span>";
        }
        // Render in reading order so the first citation receives number one.
        var answer = document["answer"];
        var answerHtml = new StringBuilder($"<header class=\"panel-head\"><span class=\"panel-id answer-id\" aria-hidden=\"true\">{W("answerMark")}</span><h2>{W("answer")}</h2></header><div class=\"panel-body\"><p class=\"verdict\">{Inline(answer?["summary"])}</p>");
        if (answer?["reasons"] is JsonArray { Count: > 0 } reasons) answerHtml.Append($"<ol class=\"reasons\">{string.Concat(reasons.Select(reason => $"<li>{Inline(reason)}</li>"))}</ol>");
        answerHtml.Append("</div>");
        if (answer?["scope"].Truthy() == true) answerHtml.Append($"<p class=\"answer-scope\"><span class=\"scope-label\">{W("scope")}</span>{ScopeHtml(Js.String(answer["scope"]))}</p>");

        var board = new StringBuilder();
        foreach (var (row, rowIndex) in Rows(document, diagrams).Select((r, i) => (r, i)))
            foreach (var item in row)
            {
                if (item.Spacer) { board.Append($"<div class=\"board-spacer span{Js.Num(item.Span)}\" data-row=\"{rowIndex}\" aria-hidden=\"true\"></div>"); continue; }
                if (item.Answer) { board.Append($"<section id=\"answer\" class=\"panel answer-panel span{Js.Num(item.Span)}\" data-row=\"{rowIndex}\" data-span-min=\"{Js.Num(item.Min)}\">{answerHtml}</section>"); continue; }
                var panel = item.Panel!;
                var panelId = panel.Str("id");
                var blocks = panel["blocks"].AsArrayOrEmpty().Select(b => b!.AsObject()).ToList();
                var measure = blocks.All(block => block.Str("type") is "prose" or "list" or "steps" or "callout" or "facts" or "metric") ? " data-measure=\"text\"" : "";
                var html = blocks.Select((block, n) => BlockHtml(block, panelId, item.PanelIndex, n)).ToList();
                var figures = blocks.Where(block => block.Str("type") == "diagram").ToList();
                // Steps that point into the panel's one diagram are read beside it: text left, diagram right.
                var guided = figures.Count == 1 && blocks.Any(block => block.Str("type") == "steps" && block["items"].AsArrayOrEmpty().Any(step =>
                    StrictEquals(step?["target"]?["diagram"], Has(step?["target"], "diagram", out _), figures[0]["id"], figures[0].ContainsKey("id"))));
                var body = guided
                    ? $"<div class=\"panel-body guided\"><div class=\"guide-figure\">{html[blocks.IndexOf(figures[0])]}</div><div class=\"guide-text\">{string.Concat(html.Where((_, n) => blocks[n] != figures[0]))}</div></div>"
                    : $"<div class=\"panel-body\">{string.Concat(html)}</div>";
                var title = Inline(panel["title"], panelId);
                board.Append($"<section class=\"panel span{Js.Num(item.Span)}{(figures.Count > 0 ? " diagram-panel" : "")}\" id=\"{E(panel["id"])}\" data-row=\"{rowIndex}\" data-span-min=\"{Js.Num(item.Min)}\"{(item.Explicit ? $" data-span-fixed=\"{Js.Num(item.Span)}\"" : "")}{measure} data-title=\"{E(panel["title"])}\"><header class=\"panel-head\"><a class=\"panel-id\" href=\"#{E(panel["id"])}\" aria-label=\"{E(panel["title"])}\">{item.PanelIndex + 1}</a><h2>{title}</h2></header>{body}</section>");
            }

        var documentSources = document["sources"].AsArrayOrEmpty().ToList();
        foreach (var source in documentSources) { var id = Js.String(source?["id"]); if (!citations.Has(id)) citations.Set(id, citations.Count + 1); }
        var sourceList = new StringBuilder();
        foreach (var (id, number) in citations.Entries)
        {
            var source = sources[id].Truthy() ? sources[id] : documentSources.FirstOrDefault(entry => entry?["id"] is JsonValue v && v.TryGetValue<string>(out var s) && s == id);
            var location = source?["path"].Truthy() == true
                ? $"<code>{E(source["path"])}{(source["lines"].Truthy() ? ":" + E(string.Join("–", source["lines"].AsArrayOrEmpty().Select(x => x is null ? "" : Js.String(x)))) : "")}{(source["revision"].Truthy() ? " @ " + E(source["revision"]) : "")}</code>"
                : "";
            var title = source?["url"].Truthy() == true ? $"<a href=\"{E(source["url"])}\" target=\"_blank\" rel=\"noopener noreferrer\">{E(source["title"])}</a>"
                : source?["title"].Truthy() == true ? E(source["title"]) : E(id);
            sourceList.Append($"<li id=\"source-{E(id)}\" value=\"{number}\">{title}{location}</li>");
        }

        var children = new JsonObject();
        foreach (var (id, node) in diagrams)
        {
            var diagram = node as JsonObject;
            var html = diagram?["html"] is JsonValue h && h.TryGetValue<string>(out var s) ? s : throw new ArgumentException("The \"data\" argument must be of type string or an instance of Buffer, TypedArray, or DataView. Received undefined");
            var bytes = Encoding.UTF8.GetByteCount(html);
            if (diagram["sha256"].Truthy() && !(diagram["sha256"] is JsonValue sha && sha.TryGetValue<string>(out var hex) && hex == ExplainerBundle.Hash(html))) throw new JsError($"Native child hash mismatch: {id}");
            if (diagram.ContainsKey("bytes") && !(diagram["bytes"] is JsonValue b && b.GetValueKind() == System.Text.Json.JsonValueKind.Number && b.D() == bytes)) throw new JsError($"Native child byte count mismatch: {id}");
            var child = new JsonObject
            {
                ["sha256"] = diagram["sha256"].Truthy() ? diagram["sha256"]!.DeepClone() : ExplainerBundle.Hash(html),
                ["bytes"] = diagram["bytes"] is { } counted ? counted.DeepClone() : bytes,
            };
            foreach (var key in new[] { "diagramType", "checks", "composition", "specSha256" })
                if (diagram.TryGetPropertyValue(key, out var value)) child[key] = key == "checks" ? PortableChecks(value) : value?.DeepClone();
            children[id] = child;
        }

        // What a reader can check, stated next to the maker's mark.
        var pinned = sources.Select(kv => kv.Value).FirstOrDefault(source => source?["path"].Truthy() == true && source["revision"].Truthy())?["revision"];
        var pinnedText = pinned is null ? null : Js.String(pinned) is var p && p.Length > 7 ? p[..7] : Js.String(pinned);
        var facts = Facts(english, panels.Count, diagrams.Count, citations.Count, pinnedText);
        var colophon = $"<div class=\"colophon\"><a class=\"colophon-brand\" href=\"https://github.com/tt-a1i/archify\" target=\"_blank\" rel=\"noopener noreferrer\"><span class=\"colophon-mark\" aria-hidden=\"true\">{Mark}</span><span class=\"colophon-words\"><span class=\"colophon-name\">Archify</span><span class=\"colophon-tagline\">{W("tagline")}</span></span></a><p class=\"colophon-facts\">{string.Concat(facts.Select(fact => $"<span>{E(fact)}</span>"))}</p></div>";
        var footnotesHtml = footnotes.Count > 0
            ? $"<section class=\"typical-footnotes\"><h2>{(english ? "Typical value notes" : "典型值说明")}</h2><ol>{string.Concat(footnotes.Select(note => $"<li id=\"{E(note?["id"])}\">{E(note?["label"])}: {E(note?["text"])}</li>"))}</ol></section>"
            : "";

        var dependencies = new JsonArray([.. preparedSources["dependencies"].AsArrayOrEmpty().Concat(preparedDiagrams["dependencies"].AsArrayOrEmpty()).Select(entry => entry?.DeepClone())]);
        var manifestSources = new JsonObject();
        foreach (var (id, source) in sources)
            manifestSources[id] = new JsonObject((source as JsonObject ?? []).Where(kv => kv.Key != "text").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        var manifestAssets = new JsonObject();
        foreach (var (id, asset) in assets)
        {
            var identity = new JsonObject();
            foreach (var key in new[] { "sha256", "bytes", "mime" }) if (Has(asset, key, out var value)) identity[key] = value?.DeepClone();
            manifestAssets[id] = identity;
        }
        var manifest = new JsonObject { ["version"] = 1 };
        if (Has(meta, "id", out var artifactId)) manifest["artifactId"] = artifactId?.DeepClone();
        manifest["revision"] = revision;
        manifest["documentSha256"] = ExplainerBundle.Hash(Js.Stringify(document));
        manifest["sourceSha256"] = sourceSha256;
        manifest["dependencies"] = null;   // positions only: both are replaced from the page below
        manifest["children"] = children;
        manifest["sources"] = manifestSources;
        manifest["assets"] = manifestAssets;
        manifest["diagnostics"] = null;

        var bundle = o.PreparedBundle;
        if (bundle is null)
        {
            bundle = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "archify-explainer-prepared", ["sourceText"] = sourceText, ["sourceMap"] = sourceMap.DeepClone() };
            if (o.SourcePath is not null) bundle["sourcePath"] = o.SourcePath;
            bundle["document"] = document.DeepClone();
            bundle["format"] = "json";
            bundle["diagnostics"] = diagnostics.DeepClone();
            bundle["preparedSources"] = preparedSources.DeepClone();
            bundle["preparedDiagrams"] = preparedDiagrams.DeepClone();
            bundle["dependencies"] = dependencies.DeepClone();
            bundle["source"] = new JsonObject { ["sha256"] = sourceSha256, ["bytes"] = Encoding.UTF8.GetByteCount(sourceText) };
            bundle["digest"] = ExplainerBundle.Hash(Js.Stringify(bundle));
        }
        if (bundle.Str("sourceText") != sourceText || Js.Stringify(bundle["document"]) != Js.Stringify(document))
            throw new JsError("Prepared bundle differs from rendered source or canonical document.");
        var directory = !string.IsNullOrEmpty(o.PageDirectory) ? o.PageDirectory
            : bundle.Str("baseDir") is { Length: > 0 } baseDir ? baseDir
            : bundle.Str("sourcePath") is { Length: > 0 } sourcePath ? Dirname(sourcePath) : null;
        var page = PortableBundle(bundle, directory);
        if (page.TryGetPropertyValue("diagnostics", out var pageDiagnostics)) manifest["diagnostics"] = pageDiagnostics?.DeepClone();
        else manifest.Remove("diagnostics");   // undefined
        manifest["dependencies"] = new JsonArray([.. page["dependencies"].AsArrayOrEmpty().Select(entry => (JsonNode?)Without(entry, "contentBase64"))]);

        var style = meta?["style"].Truthy() == true ? Js.String(meta["style"]) : "clean";
        var styles = words["styles"];
        var output = new StringBuilder();
        output.Append($"<!doctype html>\n<html lang=\"{E(locale)}\" data-style=\"{E(style)}\" data-archify-artifact=\"explainer\" data-artifact=\"{E(meta?["id"])}\" data-revision=\"{revision}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"color-scheme\" content=\"light dark\"><meta name=\"description\" content=\"{E(answer?["summary"])}\"><title>{E(meta?["title"])} · Archify</title><link rel=\"icon\" type=\"image/svg+xml\" href=\"{Favicon}\"><style>{Css.Value}</style></head>");
        output.Append($"<body><a class=\"skip-link\" href=\"#board\">{(english ? "Skip to content" : "跳至正文")}</a><div class=\"shell\"><header class=\"masthead\"><h1>{E(meta?["title"])}</h1><div class=\"tools interactive\"><div class=\"theme-controls\" role=\"group\" aria-label=\"{(english ? "Theme" : "主题")}\">");
        foreach (var theme in new[] { "auto", "light", "dark" }) output.Append($"<button type=\"button\" data-theme-choice=\"{theme}\" aria-pressed=\"{(theme == "auto" ? "true" : "false")}\">{W(theme)}</button>");
        output.Append($"</div><div class=\"tool-menu style-menu\"><button type=\"button\" class=\"tool-button\" data-style-toggle aria-haspopup=\"menu\" aria-expanded=\"false\" aria-label=\"{W("style")}\"><span class=\"style-swatch\" data-swatch=\"{E(style)}\"></span><span data-style-label>{T(styles, style)}</span>{Chevron}</button><div class=\"menu-options style-options\" role=\"menu\" aria-label=\"{W("style")}\" hidden>");
        foreach (var choice in new[] { "clean", "editorial", "product", "swiss" })
            output.Append($"<button type=\"button\" role=\"menuitemradio\" aria-checked=\"{(choice == style ? "true" : "false")}\" data-style-choice=\"{choice}\" tabindex=\"-1\"><span class=\"style-swatch\" data-swatch=\"{choice}\"></span><span>{T(styles, choice)}</span>{Check}</button>");
        output.Append($"</div></div><div class=\"tool-menu export-menu\"><button type=\"button\" class=\"tool-button\" data-export-toggle aria-haspopup=\"menu\" aria-expanded=\"false\">{Export}<span>{W("export")}</span>{Chevron}</button><div class=\"menu-options\" role=\"menu\" aria-label=\"{W("export")}\" hidden><p class=\"menu-label\" role=\"presentation\">{W("image")}</p>");
        foreach (var (format, label, index) in new[] { ("png", "PNG", 0), ("webp", "WebP", 1), ("jpeg", "JPG", 2) })
            output.Append($"<button type=\"button\" role=\"menuitem\" tabindex=\"-1\" data-export-image=\"{format}\" aria-label=\"{W("image")} {label}\">{ImageIcon}<span>{label}</span><small>{T(words["imageHints"], index.ToString())}</small></button>");
        output.Append($"<hr role=\"separator\"><button type=\"button\" role=\"menuitem\" tabindex=\"-1\" data-print>{Print}<span>{W("print")}</span></button></div></div></div></header><main id=\"board\"><div class=\"board\">{board}</div></main>");
        output.Append($"<footer>{footnotesHtml}<section class=\"sources\"><h2>{W("sources")}</h2><ol>{sourceList}</ol></section>{colophon}</footer></div>");
        output.Append($"<dialog class=\"viewer-dialog\" id=\"diagram-dialog\" aria-labelledby=\"diagram-dialog-title\"><header><h2 id=\"diagram-dialog-title\"></h2><button type=\"button\" class=\"viewer-close\" data-close-diagram>{Close}<span>{W("close")}</span><kbd>Esc</kbd></button></header><p class=\"dialog-loading\" role=\"status\">{W("loading")}</p></dialog><div id=\"toast\" role=\"status\" aria-live=\"polite\"></div>");
        output.Append($"<script type=\"application/json\" id=\"archify-explainer-prepared\">{Json(ExplainerBundle.Pack(page))}</script><script type=\"application/json\" id=\"explainer-manifest\">{Json(manifest)}</script><script type=\"application/json\" id=\"explainer-reader-words\">{Json(words)}</script><script>{Script.Value}</script></body></html>");
        return output.ToString();
    }

    static JsonObject? Without(JsonNode? entry, string key) =>
        entry is JsonObject o ? new JsonObject(o.Where(kv => kv.Key != key).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))) : null;

    // Native check filenames refer to disposable build files, not reader resources (portableChecks, :131-135).
    static JsonNode? PortableChecks(JsonNode? checks)
    {
        if (checks?["check"].Truthy() != true || checks is not JsonObject o) return checks?.DeepClone();
        var copy = o.DeepClone().AsObject();
        copy["check"] = o["check"] is JsonObject check ? Without(check, "file") : new JsonObject();   // {file, ...check} = a truthy primitive: {}
        return copy;
    }

    // path.dirname: '.' for a bare name.
    static string Dirname(string path) => Path.GetDirectoryName(path) is { Length: > 0 } directory ? directory : path.Length > 0 && path[0] is '/' or '\\' ? path[..1] : ".";
    // path.win32.isAbsolute: a leading separator, or a drive letter, a colon and a separator.
    static bool IsAbsolute(string path) => path.Length > 0 && (path[0] is '/' or '\\' || (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\'));

    /// <summary>portableBundle (:139-156): a shared page names local files relative to the page, never by absolute path, and
    /// is signed again. ponytail: Windows path semantics (path.win32), the platform the port and its goldens run on.</summary>
    internal static JsonObject PortableBundle(JsonObject bundle, string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return bundle;
        JsonNode? Local(JsonNode? value)
        {
            if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) return value?.DeepClone();
            if (!IsAbsolute(s)) return s;
            var relative = Path.GetRelativePath(directory, s);
            // Different Windows volumes have no relative path. Frozen content stays usable; a missing origin must not be mistaken
            // for the output directory during patching.
            return IsAbsolute(relative) ? null : relative.Length == 0 ? "." : relative;
        }
        JsonNode? Subject(JsonNode? entry)
        {
            if (entry?["subject"].Truthy() != true || entry is not JsonObject o) return entry?.DeepClone();
            var copy = o.DeepClone().AsObject();
            var subject = o["subject"] as JsonObject ?? [];
            var local = subject.DeepClone().AsObject();
            if (subject.TryGetPropertyValue("input", out var input)) local["input"] = Local(input);
            if (subject.TryGetPropertyValue("output", out var output)) local["output"] = Local(output);
            copy["subject"] = local;
            return copy;
        }
        var page = bundle.DeepClone().AsObject();
        foreach (var key in new[] { "sourcePath", "inputPath", "baseDir" })
            if (bundle.TryGetPropertyValue(key, out var value)) page[key] = Local(value);
        var preparedDiagrams = (bundle["preparedDiagrams"] as JsonObject)?.DeepClone().AsObject() ?? [];
        var diagrams = new JsonObject();
        foreach (var (id, node) in bundle["preparedDiagrams"]?["diagrams"] as JsonObject ?? [])
        {
            var diagram = node!.DeepClone().AsObject();
            if (diagram.ContainsKey("checks")) diagram["checks"] = PortableChecks(node["checks"]);
            diagrams[id] = diagram;
        }
        preparedDiagrams["diagrams"] = diagrams;
        page["preparedDiagrams"] = preparedDiagrams;
        page["dependencies"] = new JsonArray([.. bundle["dependencies"].AsArrayOrEmpty().Select(entry =>
        {
            if (entry?["path"].Truthy() != true) return entry?.DeepClone();
            var copy = entry.DeepClone().AsObject();
            copy["path"] = Local(entry["path"]);
            return copy;
        })]);
        page["diagnostics"] = new JsonArray([.. bundle["diagnostics"].AsArrayOrEmpty().Select(Subject)]);
        page["digest"] = ExplainerBundle.Digest(page);
        return page;
    }

    // --- native previews (:158-223) ---
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])id=""([^""]+)""")] private static partial Regex ElementId();
    [GeneratedRegex(@"url\(#([^\)]+)\)")] private static partial Regex UrlRef();
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(href|xlink:href)=""#([^""]+)""")] private static partial Regex HrefRef();
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(aria-labelledby|aria-describedby)=""([^""]+)""")] private static partial Regex AriaRef();
    [GeneratedRegex($@"{Ws}(?:tabindex=""[^""]*""|role=""button""|aria-pressed=""[^""]*"")")] private static partial Regex Interactive();
    [GeneratedRegex("[^A-Za-z0-9_-]")] private static partial Regex PrefixUnsafe();

    // previewPrefix (:187)
    static string PreviewPrefix(string id) => "preview-" + PrefixUnsafe().Replace(id, "-");

    // staticSvg (:159-168): native SVG identifiers prefixed in the preview only; data-node-id and data-edge-id stay semantic.
    static string StaticSvg(string svg, string id)
    {
        var prefix = PreviewPrefix(id) + "-";
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);   // lookup only
        foreach (Match match in ElementId().Matches(svg)) ids[match.Groups[1].Value] = prefix + match.Groups[1].Value;
        string Mapped(string value) => ids.TryGetValue(value, out var mapped) ? mapped : value;
        var result = ElementId().Replace(svg, m => $"id=\"{E(ids[m.Groups[1].Value])}\"");
        result = UrlRef().Replace(result, m => $"url(#{Mapped(m.Groups[1].Value)})");
        result = HrefRef().Replace(result, m => $"{m.Groups[1].Value}=\"#{E(Mapped(m.Groups[2].Value))}\"");
        result = AriaRef().Replace(result, m => $"{m.Groups[1].Value}=\"{E(string.Join(' ', Js.SplitOnSpaces(m.Groups[2].Value).Select(Mapped)))}\"");
        return Interactive().Replace(result, "");
    }

    // selectorList (:174-185): a selector list split on its top-level commas.
    static List<string> SelectorList(string selector)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        var quote = '\0';
        for (var i = 0; i < selector.Length; i++)
        {
            var c = selector[i];
            if (quote != '\0') { if (c == quote && (i == 0 || selector[i - 1] != '\\')) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(selector[start..i]); start = i + 1; }
        }
        parts.Add(selector[start..]);
        return parts;
    }

    [GeneratedRegex(@"<style(?![A-Za-z0-9_])[^>]*>[\s\S]*?</style>")] private static partial Regex StyleElement();
    // nativeStyles (:186)
    static string NativeStyles(string html) => string.Join("\n", StyleElement().Matches(html).Select(match => match.Value));

    [GeneratedRegex("<style[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex StyleOpen();
    [GeneratedRegex("</style>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex StyleClose();
    [GeneratedRegex(@"/\*[\s\S]*?\*/")] private static partial Regex CssComment();
    [GeneratedRegex(@"(?<![A-Za-z0-9_])class=""([^""]*)""")] private static partial Regex ClassAttribute();
    [GeneratedRegex(@":not\([^)]*\)")] private static partial Regex NotPseudo();
    [GeneratedRegex(@"\.([A-Za-z_][A-Za-z0-9_-]*)")] private static partial Regex ClassSelector();
    [GeneratedRegex($@"(?:^|[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF>+~])body(?![A-Za-z0-9_])")] private static partial Regex BodySelector();
    [GeneratedRegex(@"#([A-Za-z][A-Za-z0-9_-]*)")] private static partial Regex IdSelector();
    [GeneratedRegex(@"^:root|^html(?![A-Za-z0-9_])")] private static partial Regex RootSelector();
    [GeneratedRegex(@"^(?::root)?\[data-theme=[""']?dark[""']?\]\z")] private static partial Regex DarkTheme();
    [GeneratedRegex(@"^(?::root)?\[data-theme=[""']?light[""']?\]\z")] private static partial Regex LightTheme();

    // scopeNativeCss (:192-223): native CSS scoped to its static preview. Font faces, body rules and rules that name a class
    // absent from the SVG are dropped; the native theme variables become system-theme and print aliases.
    static string ScopeNativeCss(string styles, string scope, string prefix, string svg = "")
    {
        var css = CssComment().Replace(StyleClose().Replace(StyleOpen().Replace(styles, ""), ""), "");
        var present = svg.Length > 0 ? ClassAttribute().Matches(svg).SelectMany(match => Js.SplitOnSpaces(match.Groups[1].Value)).ToHashSet(StringComparer.Ordinal) : [];
        bool Applies(string value) => svg.Length == 0 || ClassSelector().Matches(NotPseudo().Replace(value, "")).All(match => present.Contains(match.Groups[1].Value));
        var defaults = new List<string>();
        var systemDark = new List<string>();
        var systemLight = new List<string>();
        string Rules(string input, bool collectThemes)
        {
            var result = new StringBuilder();
            var cursor = 0;
            while (cursor < input.Length)
            {
                var opening = input.IndexOf('{', cursor);
                if (opening < 0) break;
                var selector = Js.Trim(input[cursor..opening]);
                int depth = 1, end = opening + 1;
                var quote = '\0';
                for (; end < input.Length && depth > 0; end++)
                {
                    var c = input[end];
                    if (quote != '\0') { if (c == quote && input[end - 1] != '\\') quote = '\0'; }
                    else if (c is '"' or '\'') quote = c;
                    else if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                var body = UrlRef().Replace(end - 1 > opening ? input[(opening + 1)..(end - 1)] : "", m => $"url(#{prefix}{m.Groups[1].Value})");
                cursor = end;
                if (selector.StartsWith("@media", StringComparison.Ordinal) || selector.StartsWith("@supports", StringComparison.Ordinal) || selector.StartsWith("@layer", StringComparison.Ordinal))
                {
                    result.Append($"{selector}{{{Rules(body, false)}}}");
                    continue;
                }
                if (selector.StartsWith('@')) continue;
                var selectors = SelectorList(selector).Where(value => !BodySelector().IsMatch(value) && Applies(value)).Select(value =>
                {
                    var next = IdSelector().Replace(Js.Trim(value), m => "#" + prefix + m.Groups[1].Value);
                    next = RootSelector().Replace(next, scope, 1);
                    if (next.StartsWith("[data-theme=", StringComparison.Ordinal) || next.StartsWith("[data-preset=", StringComparison.Ordinal)) next = scope + next;
                    return next.StartsWith(scope, StringComparison.Ordinal) ? next : $"{scope} {next}";
                }).ToList();
                var rootSelectors = SelectorList(selector).Select(Js.Trim).ToList();
                if (collectThemes && rootSelectors.Contains(":root")) defaults.Add(body);
                if (collectThemes && rootSelectors.Any(DarkTheme().IsMatch)) systemDark.Add(body);
                if (collectThemes && rootSelectors.Any(LightTheme().IsMatch)) systemLight.Add(body);
                if (selectors.Count > 0) result.Append($"{string.Join(",", selectors)}{{{body}}}");
            }
            return result.ToString();
        }
        var scoped = Rules(css, true);
        var light = systemLight.Count > 0 ? systemLight : defaults;
        return scoped + (systemLight.Count > 0 ? $"{scope}:not([data-theme]){{{string.Join(";", systemLight)}}}" : "")
            + (systemDark.Count > 0 ? $"@media(prefers-color-scheme:dark){{{scope}:not([data-theme]){{{string.Join(";", systemDark)}}}}}" : "")
            + (light.Count > 0 ? $"@media print{{{scope},{scope}[data-theme],{scope}:not([data-theme]){{{string.Join(";", light)}}}}}" : "");
    }
}
