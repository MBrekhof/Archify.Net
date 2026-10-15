using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class ValidationTests
{
    public static IEnumerable<object[]> AllExamples() =>
        Directory.GetFiles(Fixtures.Upstream("examples"), "*.json").Where(f => !f.Contains("locales")).Select(f => new object[] { Path.GetFileName(f) });

    [Theory, MemberData(nameof(AllExamples))]
    public void Every_upstream_example_validates(string file)
    {
        var json = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream($"examples/{file}")));
        var type = DiagramTypes.Parse(json["diagram_type"]!.GetValue<string>());
        var ctx = new RenderContext(type, json, new RenderOptions());
        DiagramLoader.Load(type, json, ctx);                        // must not throw
        Assert.DoesNotContain(ctx.Diagnostics.Recorded, d => d.Severity == Severity.Error);
    }

    static JsonObject Example(string f) => DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream($"examples/{f}")));
    static ArchifyDiagnosticException Fails(DiagramType t, JsonObject d) =>
        Assert.Throws<ArchifyDiagnosticException>(() => DiagramLoader.Load(t, d, new RenderContext(t, d, new RenderOptions())));

    // Mirrors UP/test/golden.mjs expectFailure cases (lines 129-200) — assert on code only.
    [Fact] public void Extra_property_rejected() { var d = Example("agent-tool-call.workflow.json"); d["bogus"] = 1; Assert.Contains("schema/additionalProperties", Fails(DiagramType.Workflow, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Missing_schema_version() { var d = Example("cache-miss-request.sequence.json"); d.Remove("schema_version"); Assert.Contains("schema/required", Fails(DiagramType.Sequence, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Node_id_starting_with_digit() { var d = Example("agent-tool-call.workflow.json"); d["nodes"]![0]!["id"] = "1bad"; var codes = Fails(DiagramType.Workflow, d).Diagnostics.Select(x => x.Code).ToList(); Assert.Contains("schema/pattern", codes); }
    [Fact] public void Zero_component_width() { var d = Example("web-app.architecture.json"); d["components"]![0]!["size"] = new JsonArray(0, 60); Assert.Contains("schema/exclusiveMinimum", Fails(DiagramType.Architecture, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Card_dot_outside_enum() { var d = Example("agent-tool-call.workflow.json"); d["cards"]![0]!["dot"] = "purple"; Assert.Contains("schema/enum", Fails(DiagramType.Workflow, d).Diagnostics.Select(x => x.Code)); }
    [Fact] public void Legacy_meta_views_accepted_and_ignored()
    {   // golden.mjs:135-146: retired guided views still render, without guided-view UI — byte for byte the example's golden
        var d = Example("agent-tool-call.workflow.json");
        d["meta"]!["views"] = new JsonArray(new JsonObject { ["id"] = "v", ["label"] = "Legacy view", ["focus"] = new JsonArray(d["nodes"]![0]!["id"]!.DeepClone()) });
        Assert.Equal(Fixtures.Lf(Fixtures.Upstream("examples/workflow-agent-tool-call-rendered.html")), ArchifyNet.Render(DiagramType.Workflow, d).Html);
    }
    [Fact] public void Workflow_column_beyond_layout_maximum()
    {   // golden.mjs:147
        var d = Example("agent-tool-call.workflow.json"); d["nodes"]![0]!["col"] = 7;
        Assert.Contains(Fails(DiagramType.Workflow, d).Diagnostics, x => x.Code == "schema/maximum" && x.Path == "/nodes/0/col" && x.Message.Contains("<= 5"));
    }
    [Fact] public void Duplicate_relationship_id_rejected()
    {
        var d = Example("web-app.architecture.json"); var conns = d["connections"]!.AsArray();
        conns[0]!["id"] = "dup"; conns[1]!["id"] = "dup";
        Assert.Contains(Fails(DiagramType.Architecture, d).Diagnostics, x => x.Code == "relationship/duplicate-id");
    }
    [Fact] public void Length_keywords_count_code_points_not_graphemes()
    {   // 17 × family emoji = 85 code points / 17 graphemes; brand maxLength is 80 → upstream rejects (ajv ucs2length), a grapheme counter would accept
        var d = Example("web-app.architecture.json"); d["components"]![0]!["brand"] = string.Concat(Enumerable.Repeat("👨‍👩‍👧", 17));
        var codes = Fails(DiagramType.Architecture, d).Diagnostics.Where(x => x.Path == "/components/0/brand").Select(x => x.Code).ToHashSet();
        Assert.Contains("schema/maxLength", codes);
    }
    [Fact] public void Long_output_segment_is_rejected_by_portable_path_before_schema()
    {   // cli.mjs:79-81 runs validateAuthoredOutputPath first; portable-path.mjs:196 rejects segments > 255 UTF-16 units
        var d = Example("web-app.architecture.json"); d["meta"]!["output"] = "x/" + new string('a', 256) + ".html";
        Assert.Contains("output/meta-path-syntax", Fails(DiagramType.Architecture, d).Diagnostics.Select(x => x.Code));
        d["meta"]!["output"] = "out/\ud800.html";                                   // lone surrogate: same gate, same code
        Assert.Contains("output/meta-path-syntax", Fails(DiagramType.Architecture, d).Diagnostics.Select(x => x.Code));
    }
    [Fact] public void Emoji_in_output_path_is_accepted()
    {   // common.schema.json:38 rejects LONE surrogates; a valid pair must pass. .NET's [\uD800-\uDFFF] would match each half — the evaluator must check code points.
        var d = Example("web-app.architecture.json"); d["meta"]!["output"] = "out/😀.html";
        var ctx = new RenderContext(DiagramType.Architecture, d, new RenderOptions());
        DiagramLoader.Load(DiagramType.Architecture, d, ctx);
        Assert.DoesNotContain(ctx.Diagnostics.Recorded, x => x.Severity == Severity.Error);
    }
    [Fact] public void Id_with_trailing_newline_fails_pattern()
    {   // JS `$` without the m flag matches only at end of input; .NET `$` also matches before a trailing "\n"
        var d = Example("web-app.architecture.json"); d["components"]![0]!["id"] = "abc\n";
        Assert.Contains(Fails(DiagramType.Architecture, d).Diagnostics, x => x.Code == "schema/pattern" && x.Path == "/components/0/id");
    }

    // The ajv oracle: for every mutation captured in Task 2, the (keyword, instancePath) SET must match exactly.
    public static IEnumerable<object[]> SchemaErrorCases() =>
        JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/schema-errors.json")))!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory, MemberData(nameof(SchemaErrorCases))]
    public void Schema_error_set_equals_ajv(string name)
    {
        var c = JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/schema-errors.json")))!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var type = DiagramTypes.Parse(c["type"]!.GetValue<string>());
        var input = DiagramLoader.Normalize(c["input"]!.DeepClone().AsObject());
        var expected = c["errors"]!.AsArray().Select(e => (e![0]!.GetValue<string>(), e[1]!.GetValue<string>())).ToHashSet();
        var sink = new DiagnosticSink();
        var actual = new HashSet<(string, string)>();
        try { SchemaValidator.Validate(type, input, sink); }
        // subject.path is validator.mjs:7 annotatedPath: "/" for a root error, where the fixture records ajv's raw instancePath "".
        catch (ArchifyDiagnosticException ex) { foreach (var d in ex.Diagnostics) actual.Add((d.Code["schema/".Length..], d.Path is "/" ? "" : d.Path ?? "")); }
        Assert.Equal(c["valid"]!.GetValue<bool>(), actual.Count == 0);
        Assert.Equal(expected, actual);
    }

    // ---- Beyond the brief. Every expectation below is upstream's own output, captured in node from C:\projects\archify
    // (generated-validators.mjs, validator.mjs, output-path.mjs, portable-path.mjs, engineering-profiles.mjs, cli.mjs). ----

    static string Json(Diagnostic d) => Js.Stringify(new JsonObject
    {
        ["code"] = d.Code, ["severity"] = d.Severity == Severity.Error ? "error" : "warning", ["message"] = d.Message,
        ["subject"] = d.Subject.DeepClone(), ["evidence"] = d.Evidence.DeepClone(), ["supportedFixes"] = new JsonArray([.. d.SupportedFixes.Select(f => (JsonNode)f)]),
    });

    static ArchifyDiagnosticException SchemaFails(DiagramType t, JsonObject d) =>
        Assert.Throws<ArchifyDiagnosticException>(() => SchemaValidator.Validate(t, DiagramLoader.Normalize(d), new DiagnosticSink()));

    static List<(string, string)> Pairs(ArchifyDiagnosticException ex) =>
        [.. ex.Diagnostics.Select(d => (d.Code["schema/".Length..], d.Path is "/" ? "" : d.Path ?? ""))];

    [Fact]
    public void Schema_errors_come_in_ajv_order()
    {   // ajv rule groups: up-front type, untyped ($ref const enum not anyOf oneOf allOf if), number, string, array, object.
        var d = Example("web-app.architecture.json");
        var c0 = d["components"]![0]!; c0["id"] = 5; c0["size"] = "x"; c0["brand"] = 5;
        var c1 = d["components"]![1]!.AsObject(); c1.Remove("label"); c1["zzz"] = 1;
        d["meta"]!["translations"] = new JsonObject { ["Bad.key"] = "", ["Also-bad"] = "y" };
        Assert.Equal([
            ("pattern", "/meta/translations"), ("propertyNames", "/meta/translations"), ("pattern", "/meta/translations"), ("propertyNames", "/meta/translations"),
            ("minLength", "/meta/translations/Bad.key"), ("type", "/components/0/id"), ("type", "/components/0/brand"), ("type", "/components/0/brand"),
            ("oneOf", "/components/0/brand"), ("type", "/components/0/size"), ("required", "/components/1"), ("additionalProperties", "/components/1"),
        ], Pairs(SchemaFails(DiagramType.Architecture, d)));

        var o = Example("web-app.architecture.json"); o["meta"]!["output"] = 5;   // `not { pattern }` passes a non-string, so every `not` fails
        Assert.Equal([.. Enumerable.Repeat(("not", "/meta/output"), 7), ("type", "/meta/output")], Pairs(SchemaFails(DiagramType.Architecture, o)));
    }

    [Fact]
    public void Schema_diagnostics_match_upstream_shape()
    {
        var d = Example("web-app.architecture.json");
        var c0 = d["components"]![0]!; c0["id"] = 5; c0["brand"] = 5;
        var brand = SchemaFails(DiagramType.Architecture, d).Diagnostics.Where(x => x.Path is "/components/0/id" or "/components/0/brand").Select(Json).ToList();
        Assert.Equal([
            """{"code":"schema/type","severity":"error","message":"/components/0/id (id/label: \"5\") must be string","subject":{"diagramType":"architecture","path":"/components/0/id","identity":"5"},"evidence":{"keyword":"type","type":"string"},"supportedFixes":["use \"string\" at /components/0/id"]}""",
            """{"code":"schema/type","severity":"error","message":"/components/0/brand (id/label: \"5\") must be string","subject":{"diagramType":"architecture","path":"/components/0/brand","identity":"5"},"evidence":{"keyword":"type","type":"string"},"supportedFixes":["use \"string\" at /components/0/brand"]}""",
            """{"code":"schema/type","severity":"error","message":"/components/0/brand (id/label: \"5\") must be object","subject":{"diagramType":"architecture","path":"/components/0/brand","identity":"5"},"evidence":{"keyword":"type","type":"object"},"supportedFixes":["use \"object\" at /components/0/brand"]}""",
            """{"code":"schema/oneOf","severity":"error","message":"/components/0/brand (id/label: \"5\") must match exactly one schema in oneOf","subject":{"diagramType":"architecture","path":"/components/0/brand","identity":"5"},"evidence":{"keyword":"oneOf","passingSchemas":null},"supportedFixes":[]}""",
        ], brand);

        var erd = Example("orders.erd.json"); erd["relationships"]![0]!["identifying"] = false; erd["relationships"]![0]!["variant"] = "emphasis";
        var ex = SchemaFails(DiagramType.Erd, erd);
        Assert.Equal("erd schema validation failed:\n  /relationships/0/variant (id/label: \"order_customer\") must be equal to one of the allowed values {\"allowedValues\":[\"dashed\"]}\n  /relationships/0 (id/label: \"order_customer\") must match \"then\" schema {\"failingKeyword\":\"then\"}", ex.Message);
        Assert.Equal([
            """{"code":"schema/enum","severity":"error","message":"/relationships/0/variant (id/label: \"order_customer\") must be equal to one of the allowed values","subject":{"diagramType":"erd","path":"/relationships/0/variant","identity":"order_customer"},"evidence":{"keyword":"enum","allowedValues":["dashed"]},"supportedFixes":["choose one of the allowed values"]}""",
            """{"code":"schema/if","severity":"error","message":"/relationships/0 (id/label: \"order_customer\") must match \"then\" schema","subject":{"diagramType":"erd","path":"/relationships/0","identity":"order_customer"},"evidence":{"keyword":"if","failingKeyword":"then"},"supportedFixes":[]}""",
        ], ex.Diagnostics.Select(Json));

        var cls = Example("payments.class.json"); cls["types"]![0]!["col"] = 0.25;
        ex = SchemaFails(DiagramType.Class, cls);
        Assert.Equal("class schema validation failed:\n  /types/0/col (id/label: \"order_service\") must be multiple of 0.5 {\"multipleOf\":0.5}", ex.Message);
        Assert.Equal("""{"code":"schema/multipleOf","severity":"error","message":"/types/0/col (id/label: \"order_service\") must be multiple of 0.5","subject":{"diagramType":"class","path":"/types/0/col","identity":"order_service"},"evidence":{"keyword":"multipleOf","multipleOf":0.5},"supportedFixes":[]}""", Json(Assert.Single(ex.Diagnostics)));
    }

    [Fact]
    public void Remaining_keywords_match_ajv()
    {
        var wf = Example("agent-tool-call.workflow.json"); wf["semanticChecks"] = new JsonObject();
        Assert.Equal([("minProperties", "/semanticChecks")], Pairs(SchemaFails(DiagramType.Workflow, wf)));

        var tr = Example("web-app.architecture.json"); tr["meta"]!["locale"] = "es";
        tr["meta"]!["translations"] = new JsonObject(Enumerable.Range(0, 601).Select(i => KeyValuePair.Create($"k{Js.Num(i)}", (JsonNode?)"x")));
        Assert.Equal([("maxProperties", "/meta/translations")], Pairs(SchemaFails(DiagramType.Architecture, tr)));

        var inf = Example("web-app.architecture.json"); inf["components"]![0]!["size"] = new JsonArray(double.PositiveInfinity, 60);   // strictNumbers: not a number
        Assert.Equal([("type", "/components/0/size/0")], Pairs(SchemaFails(DiagramType.Architecture, inf)));

        var lone = Example("web-app.architecture.json"); lone["meta"]!["output"] = "out/\ud800.html";
        Assert.Equal([("not", "/meta/output")], Pairs(SchemaFails(DiagramType.Architecture, lone)));
    }

    [Fact]
    public void Portable_path_keyword_runs_after_a_passing_schema()
    {   // Under the /u flag [^/]{256} counts code points: 200 emoji (400 UTF-16 units) pass the schema and fail portable-path.mjs:196.
        var emoji = Example("web-app.architecture.json"); emoji["meta"]!["output"] = "out/" + string.Concat(Enumerable.Repeat("😀", 200)) + ".html";
        var ex = SchemaFails(DiagramType.Architecture, emoji);
        Assert.Equal("architecture schema validation failed:\n  /meta/output must satisfy the portable output path contract {\"reason\":\"component-too-long\"}", ex.Message);
        Assert.Equal("""{"code":"schema/portablePath","severity":"error","message":"/meta/output must satisfy the portable output path contract","subject":{"diagramType":"architecture","path":"/meta/output"},"evidence":{"keyword":"portablePath","reason":"component-too-long"},"supportedFixes":[]}""", Json(Assert.Single(ex.Diagnostics)));

        var bytes = Example("web-app.architecture.json"); bytes["meta"]!["output"] = "out/" + new string('€', 86) + ".html";   // 263 UTF-8 bytes
        Assert.Equal([("portablePath", "/meta/output")], Pairs(SchemaFails(DiagramType.Architecture, bytes)));
    }

    [Fact]
    public void Js_regex_keeps_js_semantics()
    {
        Assert.DoesNotMatch(JsRegex.Translate("^a$"), "a\n");
        Assert.Matches(JsRegex.Translate("^[^/]{3}$"), "😀😀😀");    // three code points
        Assert.DoesNotMatch(JsRegex.Translate("^[^/]{3}$"), "😀a");     // two code points, three code units
        Assert.Matches(JsRegex.Translate(@"[\uD800-\uDFFF]"), "a\udc00");
        Assert.DoesNotMatch(JsRegex.Translate(@"[\uD800-\uDFFF]"), "😀");
        Assert.DoesNotMatch(JsRegex.Translate(@"^\d$"), "\u0663");       // Arabic-Indic three: \d is ASCII
        Assert.Matches(JsRegex.Translate("[$]"), "a$b");              // `$` inside a class stays literal
    }

    [Fact]
    public void Authored_output_path_diagnostics_match_upstream()
    {
        string Fail(string raw) => Json(Assert.Single(Assert.Throws<ArchifyDiagnosticException>(() => OutputPath.ValidateAuthoredOutputPath(raw, new DiagnosticSink())).Diagnostics));
        Assert.Equal("""{"code":"output/meta-absolute","severity":"error","message":"meta.output must be a relative path resolved from the current working directory.","subject":{"output":"/abs.html","path":"/meta/output"},"evidence":{"reason":"absolute"},"supportedFixes":["set meta.output to a portable relative .html path such as reports/diagram.html"]}""", Fail("/abs.html"));
        Assert.Equal("""{"code":"output/meta-extension","severity":"error","message":"meta.output must target an .html file.","subject":{"output":"a/b.htm","path":"/meta/output"},"evidence":{},"supportedFixes":["change meta.output to a portable path ending in .html"]}""", Fail("a/b.htm"));
        Assert.Contains("\"code\":\"output/meta-extension\"", Fail(".html"));   // posix extname of a dotfile is ""
        OutputPath.ValidateAuthoredOutputPath("..html", new DiagnosticSink());   // extname "..html" is ".html"

        var d = Example("web-app.architecture.json"); d["meta"]!["output"] = 5;   // cli.mjs:79 validates any present value, so a number fails first
        Assert.Equal("""{"code":"output/meta-path-syntax","severity":"error","message":"meta.output must be a portable POSIX-relative path.","subject":{"output":5,"path":"/meta/output"},"evidence":{"reason":"type"},"supportedFixes":["set meta.output to a portable relative .html path such as reports/diagram.html"]}""", Json(Assert.Single(Fails(DiagramType.Architecture, d).Diagnostics)));
    }

    [Theory]
    [InlineData("C:/x", "absolute")] [InlineData("C:x", "drive-relative")] [InlineData("a:b", "drive-relative")] [InlineData("http:x", "uri")]
    [InlineData("a\\b", "backslash")] [InlineData("a//b", "empty-segment")] [InlineData("a/./b", "dot-segment")] [InlineData("a\u0007", "control")]
    [InlineData("a?b", "windows-invalid-character")] [InlineData("a.", "windows-trailing-dot-space")] [InlineData("CON.txt", "windows-reserved-name")]
    [InlineData("com¹", "windows-reserved-name")] [InlineData("conin$", "windows-reserved-name")] [InlineData("PROGRA~1", "windows-short-name")]
    [InlineData("a~1.txt", "windows-short-name")] [InlineData("ok/fine.html", null)]
    public void Portable_path_reasons_match_upstream(string value, string? reason)
    {
        var error = Record.Exception(() => PortablePath.Validate(value));
        Assert.Equal(reason, (error as PortablePathException)?.Reason);
        if (reason is not null) Assert.IsType<PortablePathException>(error);
    }

    [Fact] public void Portable_path_limits_utf8_bytes_too() =>   // 128 code units, 256 UTF-8 bytes
        Assert.Equal("component-too-long", Assert.Throws<PortablePathException>(() => PortablePath.Validate(new string('é', 128))).Reason);

    [Fact]
    public void Portable_path_set_collisions_match_upstream()
    {
        (string, int?, int?, string?)? Collide(params string[] values)
        {
            var error = Record.Exception(() => PortablePath.ValidateSet(values, "archive")) as PortablePathException;
            return error is null ? null : (error.Reason, error.Index, error.ConflictIndex, error.CollisionKind);
        }
        Assert.Equal(("duplicate", 1, 0, "entry"), Collide("a/b", "a/b"));
        Assert.Equal(("case", 1, 0, "entry"), Collide("a/B", "a/b"));
        Assert.Equal(("normalization", 1, 0, "entry"), Collide("caf\u00e9", "cafe\u0301"));
        Assert.Equal(("tree-file", 1, 0, "tree-file"), Collide("a", "a/b"));
        Assert.Equal(("tree-file", 1, 0, "tree-file"), Collide("a/b", "a"));
        Assert.Equal(("directory-case", 1, 0, "directory-spelling"), Collide("A/x", "a/y"));
        Assert.Null(Collide("a/x", "a/y", "b"));
    }

    [Fact]
    public void Relationship_and_engineering_diagnostics_match_upstream()
    {
        var wa = Example("web-app.architecture.json");
        foreach (var i in new[] { 0, 1, 2 }) wa["connections"]![i]!["id"] = "dup";
        var dup = Assert.Throws<ArchifyDiagnosticException>(() => DiagramLoader.ValidateCrossCollectionContracts(DiagramType.Architecture, wa, new DiagnosticSink()));
        Assert.Equal("Relationship identity validation failed:\n- /connections/1/id duplicates relationship id \"dup\"\n- /connections/2/id duplicates relationship id \"dup\"", dup.Message);
        Assert.Equal([
            """{"code":"relationship/duplicate-id","severity":"error","message":"/connections/1/id duplicates relationship id \"dup\"","subject":{"diagramType":"architecture","collection":"connections"},"evidence":{},"supportedFixes":[]}""",
            """{"code":"relationship/duplicate-id","severity":"error","message":"/connections/2/id duplicates relationship id \"dup\"","subject":{"diagramType":"architecture","collection":"connections"},"evidence":{},"supportedFixes":[]}""",
        ], dup.Diagnostics.Select(Json));

        var pd = Example("production-deployment.architecture.json");
        pd["components"]![0]!.AsObject().Remove("tag"); pd["connections"]![0]!["label"] = " ";   // component 0 is external, so only the crossing fails
        var eng = Fails(DiagramType.Architecture, pd);
        Assert.Equal("Engineering profile \"deployment-ownership\" failed:\n- Cross-boundary connection \"clients->edge\" does not name its mechanism.", eng.Message);
        Assert.Equal("""{"code":"engineering/deployment-crossing-mechanism","severity":"error","message":"Cross-boundary connection \"clients->edge\" does not name its mechanism.","subject":{"diagramType":"architecture","profile":"deployment-ownership","collection":"connections","index":0},"evidence":{"from":"clients","to":"edge","crossedBoundaries":[{"boundaryIndex":0,"kind":"region","label":"AWS us-east-1 / production"}]},"supportedFixes":["set /connections/0/label to the real cross-boundary mechanism"]}""", Json(Assert.Single(eng.Diagnostics)));

        var noRegion = Example("production-deployment.architecture.json");
        noRegion["boundaries"] = new JsonArray([.. noRegion["boundaries"]!.AsArray().Where(b => b!["kind"]!.GetValue<string>() != "region").Select(b => b!.DeepClone())]);
        var all = Assert.Throws<ArchifyDiagnosticException>(() => EngineeringProfiles.Validate(DiagramType.Architecture, noRegion, new DiagnosticSink())).Diagnostics;
        Assert.Equal(13, all.Count);
        Assert.Equal([
            """{"code":"engineering/deployment-boundary-kind","severity":"error","message":"Deployment ownership requires at least one region boundary.","subject":{"diagramType":"architecture","profile":"deployment-ownership","collection":"boundaries","index":-1},"evidence":{"requiredKind":"region","found":0},"supportedFixes":["add one region boundary with an explicit wraps list"]}""",
            """{"code":"engineering/deployment-region-scope","severity":"error","message":"Deployment component \"edge\" is not assigned to a region boundary.","subject":{"diagramType":"architecture","profile":"deployment-ownership","collection":"components","index":1,"id":"edge"},"evidence":{"componentType":"cloud","regionMemberships":0},"supportedFixes":["add the component id to the real region boundary wraps list"]}""",
            """{"code":"engineering/deployment-private-region-consistency","severity":"error","message":"Private boundary \"private application network\" must contain components from exactly one shared region.","subject":{"diagramType":"architecture","profile":"deployment-ownership","collection":"boundaries","index":0},"evidence":{"boundaryKind":"security-group","members":[{"id":"api_a","regions":[]},{"id":"api_b","regions":[]},{"id":"redis","regions":[]},{"id":"postgres","regions":[]},{"id":"events","regions":[]},{"id":"worker","regions":[]}]},"supportedFixes":["assign every private-boundary component to exactly one shared region boundary"]}""",
        ], all.Where((x, i) => i < 2 || x.Code.EndsWith("consistency")).Take(3).Select(Json));
    }

    public static IEnumerable<object[]> GoldenSchemaCases() =>
    [   // golden.mjs line, type, example, mutation name, expected code, expected instance path
        ["182 tree node without a label", "tree", "payment-platform.tree.json", "schema/required", "/nodes/1"],
        ["184 unknown tree direction", "tree", "payment-platform.tree.json", "schema/enum", "/layout/direction"],
        ["186 type without a kind", "class", "payments.class.json", "schema/required", "/types/0"],
        ["188 unknown relationship kind", "class", "payments.class.json", "schema/enum", "/relationships/0/kind"],
        ["190 unknown member visibility", "class", "payments.class.json", "schema/enum", "/types/0/methods/0/visibility"],
        ["192 timeline timestamp without an offset", "timeline", "payment-incident.timeline.json", "schema/pattern", "/events/0/at"],
        ["194 timeline without evidence status", "timeline", "payment-incident.timeline.json", "schema/required", "/meta"],
        ["196 negative span start", "waterfall", "checkout-request.waterfall.json", "schema/minimum", "/spans/0/start"],
        ["198 waterfall without evidence status", "waterfall", "checkout-request.waterfall.json", "schema/required", "/meta"],
    ];

    [Theory, MemberData(nameof(GoldenSchemaCases))]
    public void Golden_schema_failure_cases(string name, string type, string file, string code, string path)
    {
        var d = Example(file);
        switch (name[..3])
        {
            case "182": d["nodes"]![1]!.AsObject().Remove("label"); break;
            case "184": d["layout"] = new JsonObject { ["direction"] = "up" }; break;
            case "186": d["types"]![0]!.AsObject().Remove("kind"); break;
            case "188": d["relationships"]![0]!["kind"] = "uses"; break;
            case "190": d["types"]![0]!["methods"]![0]!["visibility"] = "internal"; break;
            case "192": d["events"]![0]!["at"] = "2026-10-01T10:00:00"; break;
            case "194": case "198": d["meta"]!.AsObject().Remove("evidence"); break;
            case "196": d["spans"]![0]!["start"] = -1; break;
        }
        var ex = Fails(DiagramTypes.Parse(type), DiagramLoader.Normalize(d));
        Assert.Contains(ex.Diagnostics, x => x.Code == code && x.Path == path);
    }
}
