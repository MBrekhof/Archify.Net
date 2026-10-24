using System.Text.Json.Nodes;

namespace Archify.Net.Cli;

// rejectCliArgument (A:2226-2235): unwinds to Cli.Run, which prints a receipt or the message (A:7131-7138).
sealed class CliArgumentException(string message, string? code = null, JsonObject? subject = null, JsonObject? evidence = null,
    IReadOnlyList<string>? supportedFixes = null) : Exception(message)
{
    public string Code { get; } = code ?? "cli/invalid-arguments";
    public JsonObject Subject { get; } = subject ?? new JsonObject();
    public JsonObject Evidence { get; } = evidence ?? new JsonObject();
    public IReadOnlyList<string> SupportedFixes { get; } = supportedFixes ?? ["correct the command arguments and retry"];
}

// fail(message, code = 2) (A:2221-2224) is process.exit: it unwinds to Cli.Run, which writes the message to stderr. A command
// must not swallow it with a broad catch.
sealed class CliFailException(string message, int exitCode = 2) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

// The option extractors. Each strips its tokens and returns the rest; `--opt v` and `--opt=v`, the last occurrence wins.
// A missing value is `--opt` followed by nothing, by an empty token or by a token starting `--`, or an empty `--opt=`.
static class Args
{
    static (string[] Remaining, string? Value) Extract(string[] args, string option, Func<Exception> missing)
    {
        var rest = new List<string>();
        string? value = null;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == option)
            {
                value = index + 1 < args.Length ? args[index + 1] : null;
                if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal)) throw missing();
                index++;
                continue;
            }
            if (arg.StartsWith(option + "=", StringComparison.Ordinal))
            {
                value = arg[(option.Length + 1)..];
                if (value.Length == 0) throw missing();
                continue;
            }
            rest.Add(arg);
        }
        return ([.. rest], value);
    }

    static CliArgumentException MissingValue(string option, string message, string fix) =>
        new(message, "cli/missing-option-value", new JsonObject { ["option"] = option }, supportedFixes: [fix]);

    // extractQualityArgs (A:2304-2339); the value is checked after the loop.
    public static (string[] Remaining, string? Quality) Quality(string[] args)
    {
        var (rest, quality) = Extract(args, "--quality", () => MissingValue("--quality", "--quality requires standard or showcase.",
            "provide --quality standard or --quality showcase"));
        if (quality is not null and not ("standard" or "showcase"))
            throw new CliArgumentException($"Unknown quality profile \"{quality}\". Expected standard or showcase.", "cli/invalid-option-value",
                new JsonObject { ["option"] = "--quality" },
                new JsonObject { ["value"] = quality, ["supportedValues"] = new JsonArray("standard", "showcase") },
                ["use --quality standard or --quality showcase"]);
        return (rest, quality);
    }

    // extractRepoRootArgs (A:2341-2368): path.resolve(repoRoot) against the command's cwd.
    public static (string[] Remaining, string? RepoRoot) RepoRoot(string[] args, string cwd)
    {
        var (rest, repoRoot) = Extract(args, "--repo-root", () => MissingValue("--repo-root", "--repo-root requires a repository path.",
            "provide one repository path after --repo-root"));
        return (rest, repoRoot is null ? null : Paths.NodePath.Resolve(cwd, repoRoot));
    }

    // extractOutDirArgs (A:2370-2389): a missing value is fail(), plain stderr and exit 2, never a receipt.
    public static (string[] Remaining, string? OutDir) OutDir(string[] args) =>
        Extract(args, "--out-dir", () => new CliFailException("--out-dir requires a directory path."));

    // extractFinalizeReceiptArgs (A:5706-5733)
    public static (string[] Remaining, string? ReceiptPath) Receipt(string[] args) =>
        Extract(args, "--receipt", () => MissingValue("--receipt", "--receipt requires a JSON output path.", "provide one .json path after --receipt"));

    // extractCandidateSha256Args (A:5735-5770); the value is checked after the loop.
    public static (string[] Remaining, string? CandidateSha256) CandidateSha256(string[] args)
    {
        var (rest, sha) = Extract(args, "--candidate-sha256", () => MissingValue("--candidate-sha256", "--candidate-sha256 requires a SHA-256 digest.",
            "provide the candidate sha256 from the passing validate receipt"));
        if (sha is not null && !Receipts.Sha256Hex().IsMatch(sha))
            throw new CliArgumentException("--candidate-sha256 must be 64 lowercase hexadecimal characters.", "cli/invalid-option-value",
                new JsonObject { ["option"] = "--candidate-sha256" }, new JsonObject { ["value"] = sha },
                ["copy candidate.sha256 from the passing validate receipt without modification"]);
        return (rest, sha);
    }

    // extractExplainerWritingArgs (A:2397-2412 at v3.2.0), not the Extract above: `--writing` takes the next token whatever it is, a
    // second occurrence is an error, and a valid value needs the first positional to be `explainer`.
    public static (string[] Remaining, string? WritingMode) ExtractExplainerWritingArgs(string[] args)
    {
        var rest = new List<string>();
        string? writingMode = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--writing" || argument.StartsWith("--writing=", StringComparison.Ordinal))
            {
                var value = argument == "--writing" ? (++index < args.Length ? args[index] : null) : argument["--writing=".Length..];
                if (writingMode is not null || value is not ("warn" or "strict" or "off"))
                    throw new CliArgumentException("--writing requires one of warn, strict or off.", "cli/invalid-arguments",
                        new JsonObject { ["option"] = "--writing" }, supportedFixes: ["Use --writing warn, --writing strict or --writing off once."]);
                writingMode = value;
            }
            else rest.Add(argument);
        }
        if (writingMode is not null && rest.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) != "explainer")
            throw new CliArgumentException("--writing is supported for explainer documents only.", "cli/unsupported-option",
                new JsonObject { ["option"] = "--writing" }, supportedFixes: ["Remove --writing or use an explainer document."]);
        return ([.. rest], writingMode);
    }

    // rendererPath (A:2237-2247): an unknown type is an argument error.
    public static DiagramType DiagramType(string type)
    {
        const string types = "architecture, workflow, sequence, dataflow, lifecycle, erd, tree, class, timeline, waterfall, explainer";
        try { return DiagramTypes.Parse(type); }
        catch (ArgumentException) { }
        throw new CliArgumentException($"Unknown diagram type \"{type}\". Expected one of: {types}", "cli/unknown-diagram-type",
            new JsonObject { ["type"] = type }, new JsonObject { ["supportedTypes"] = new JsonArray([.. types.Split(", ").Select(t => (JsonNode?)t)]) },
            [$"use one of: {types}"]);
    }

    // rendererEnv (A:2391-2397) as options: --quality and --repo-root (Q3).
    public static RenderOptions RenderOptions(string? quality, string? repoRoot) =>
        new(quality is null ? null : quality == "showcase" ? QualityProfile.Showcase : QualityProfile.Standard, repoRoot);
}
