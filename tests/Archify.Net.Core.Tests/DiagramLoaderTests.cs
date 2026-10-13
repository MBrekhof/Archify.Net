using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class DiagramLoaderTests
{
    [Fact] public void Duplicate_keys_last_wins()
    {
        var o = DiagramLoader.Parse("{\"meta\":{\"title\":\"a\",\"title\":\"b\"}}");
        Assert.Equal("b", o["meta"]!["title"]!.GetValue<string>());
    }
    [Fact] public void Numbers_normalised_to_double_text()
    {
        var o = DiagramLoader.Parse("{\"size\":[1.0,1e2],\"n\":-0,\"big\":1e21}");
        Assert.Equal("[1,100]", Js.Stringify(o["size"]));
        Assert.Equal("1e+21", Js.Stringify(o["big"]));
        Assert.Equal("0", Js.Stringify(o["n"]));   // JSON.stringify(-0) === "0"
    }
    [Fact] public void Key_order_is_insertion_order()
    {
        var o = DiagramLoader.Parse("{\"b\":1,\"a\":2,\"c\":{\"z\":1,\"y\":2}}");
        Assert.Equal("{\"b\":1,\"a\":2,\"c\":{\"z\":1,\"y\":2}}", Js.Stringify(o));
    }
    [Fact] public void Lone_surrogate_escapes_survive_parsing()
    {   // JSON.parse keeps \ud800 as the raw code unit; System.Text.Json replaces it with U+FFFD. Upstream rejects it in meta.output (Task 6).
        var o = DiagramLoader.Parse("{\"s\":\"\\ud800x\",\"t\":\"\\udc00\",\"u\":\"\\ud83d\\ude00\"}");
        Assert.Equal('\ud800', o["s"]!.GetValue<string>()[0]);
        Assert.Equal("\udc00", o["t"]!.GetValue<string>());
        Assert.Equal("😀", o["u"]!.GetValue<string>());
        Assert.Equal("{\"s\":\"\\ud800x\",\"t\":\"\\udc00\",\"u\":\"😀\"}", Js.Stringify(o));
    }
    [Fact] public void D_reads_every_numeric_backing_type()
    {
        Assert.Equal(3.0, JsonValue.Create(3).D());
        Assert.Equal(3.0, JsonValue.Create(3L).D());
        Assert.Equal(0.5, JsonValue.Create(0.5m).D());
        Assert.Equal(60.0, new JsonArray(0, 60)[1]!.D());
        Assert.Equal(100.0, DiagramLoader.Parse("{\"a\":[1e2]}")["a"]![0]!.D());   // Parse returns the diagram object, so the array sits under a key
    }
    [Fact] public void Sink_dedupes_exact_repeats_and_keeps_first_by_message()
    {
        var sink = new DiagnosticSink();
        var d = new Diagnostic("layout/x", Severity.Error, "m", new JsonObject { ["id"] = "a" }, new JsonObject(), [], null);
        sink.Record(d); sink.Record(d); sink.Record(d with { Subject = new JsonObject { ["id"] = "b" } });
        Assert.Equal(2, sink.Recorded.Count);
        var ex = Assert.IsType<ArchifyDiagnosticException>(sink.ThrowDiagnosticProblems("Layout failed", ["m", "other"]));
        Assert.Equal("Layout failed:\n- m\n- other", ex.Message);
        Assert.Equal(["layout/x", "layout/constraint"], ex.Diagnostics.Select(x => x.Code));   // "m" reuses the recorded structured entry
    }
    [Fact] public void Isolated_recording_does_not_leak()
    {
        var sink = new DiagnosticSink();
        sink.WithIsolatedRecording(() => { sink.Record(new Diagnostic("x/y", Severity.Error, "inner", new(), new(), [], null)); return 0; });
        Assert.Empty(sink.Recorded);
    }
    [Fact] public void Syntax_errors_carry_input_json_parse()
    {   // cli.mjs:67-78; the reason is .NET's JsonException text, not V8's SyntaxError text
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => DiagramLoader.Parse("{\"a\":1,}"));
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal("input/json-parse", d.Code);
        Assert.StartsWith("Input JSON could not be parsed: ", ex.Message);
        Assert.Equal(ex.Message, d.Message);
        Assert.False(string.IsNullOrEmpty(d.Evidence["reason"]!.GetValue<string>()));
        Assert.Equal(["repair the JSON syntax and run validation again"], d.SupportedFixes);
    }
    [Fact] public void Non_object_root_is_input_json_parse()
    {   // upstream parses it and fails schema/type at "/"; Parse returns the diagram object, so it rejects here
        var ex = Assert.Throws<ArchifyDiagnosticException>(() => DiagramLoader.Parse("[1e2]"));
        Assert.Equal("input/json-parse", Assert.Single(ex.Diagnostics).Code);
    }
    [Fact] public void Out_of_range_number_is_infinity()
    {   // JSON.parse("1e400") === Infinity; JSON.stringify(Infinity) === "null"
        var o = DiagramLoader.Parse("{\"inf\":1e400}");
        Assert.True(double.IsPositiveInfinity(o["inf"]!.D()));
        Assert.Equal("{\"inf\":null}", Js.Stringify(o));
    }
    [Fact] public void Short_escapes_decode()
    {
        var o = DiagramLoader.Parse("{\"e\":\"a\\\"b\\\\c\\/d\\b\\f\\n\\r\\t\\u0041\"}");
        Assert.Equal("a\"b\\c/d\b\f\n\r\tA", o["e"]!.GetValue<string>());
    }
}
