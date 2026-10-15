using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net.Cli;

// The receipt reads and patterns deliver, check, the browser gates and finalize share. JS `$` does not match before a trailing
// "\n", hence \z in every anchored pattern.
internal static partial class Receipts
{
    // /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i (A:197, A:2052, finalize.mjs:116)
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex UuidV4();

    // /^[0-9a-f]{64}$/ (A:2047, A:5761, finalize.mjs:102)
    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    public static partial Regex Sha256Hex();

    // /\.html?$/i
    [GeneratedRegex(@"\.html?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex HtmlExtension();

    // {sha256, bytes} of a file's bytes
    public static JsonObject Identity(byte[] bytes) => new() { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length };

    // `typeof x === 'string' ? x : …` and `typeof x === 'number' ? x : …` on a parsed receipt
    public static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    public static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? node.D() : null;
}
