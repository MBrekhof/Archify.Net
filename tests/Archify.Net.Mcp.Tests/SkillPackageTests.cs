using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Archify.Net.Mcp.Tests;

public sealed partial class SkillPackageTests
{
    private static readonly string SkillRoot = Path.Combine(McpTestHost.RepoRoot, "skill", "archify");
    private static readonly string UpstreamRoot = Path.Combine(McpTestHost.RepoRoot, "upstream");

    private static readonly string[] ExpectedTools =
    [
        "archify_finalize",
        "archify_guide",
        "archify_render",
        "archify_validate",
    ];

    private static readonly string[] ForbiddenStrings =
    [
        "node bin/archify.mjs",
        "bin/archify.mjs",
        "update-awareness.md",
        "brand-marks.md",
        "archify_deliver",
        "archify_check",
        "archify_browser_check",
        "archify_visual_check",
        "archify_compare",
        "archify_migrate_workflow",
        "archify_brands",
    ];

    private static readonly string[] UnavailableCommands =
    [
        "`preview`",
        "`compare`",
        "`brands`",
        "`migrate`",
        "`bin/recover-output.mjs`",
        "`deliver --open`",
    ];

    [GeneratedRegex(@"(?:references|schemas|examples)/[A-Za-z0-9._/-]+\.(?:md|json)")]
    private static partial Regex RelativeSkillPathRegex();

    [Fact]
    public void SkillMarkdown_HasExpectedTools_NoForbiddenReferences_AndAllRelativePathsExist()
    {
        var skillMdPath = Path.Combine(SkillRoot, "SKILL.md");
        Assert.True(File.Exists(skillMdPath), $"Missing {skillMdPath}");

        var rawBytes = File.ReadAllBytes(skillMdPath);
        Assert.False(rawBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "SKILL.md must not have a UTF-8 BOM.");
        var text = Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);

        foreach (var tool in ExpectedTools)
        {
            Assert.Contains(tool, text, StringComparison.Ordinal);
        }

        foreach (var forbidden in ForbiddenStrings)
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        const string differencesHeading = "## Archify.Net differences\n";
        const string outputHeading = "\n## Output\n";
        var differencesIndex = text.IndexOf(differencesHeading, StringComparison.Ordinal);
        var outputIndex = text.IndexOf(outputHeading, StringComparison.Ordinal);
        Assert.True(differencesIndex >= 0, "SKILL.md must contain a '## Archify.Net differences' section.");
        Assert.True(outputIndex > differencesIndex, "'## Archify.Net differences' must appear before '## Output'.");

        var differencesSection = text[differencesIndex..outputIndex];
        foreach (var unavailable in UnavailableCommands)
        {
            Assert.Contains(unavailable, differencesSection, StringComparison.Ordinal);
        }

        var referencedPaths = RelativeSkillPathRegex().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(referencedPaths);
        foreach (var relPath in referencedPaths)
        {
            var fullPath = Path.Combine(SkillRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Relative path '{relPath}' in SKILL.md does not exist at {fullPath}");
        }
    }

    [Fact]
    public void SkillReferences_MatchUpstreamWithSingleMechanicalSubstitution()
    {
        var upstreamRefDir = Path.Combine(UpstreamRoot, "references");
        var skillRefDir = Path.Combine(SkillRoot, "references");
        Assert.True(Directory.Exists(skillRefDir));

        var excluded = new HashSet<string>(StringComparer.Ordinal) { "update-awareness.md", "brand-marks.md" };
        foreach (var omitted in excluded)
        {
            Assert.False(File.Exists(Path.Combine(skillRefDir, omitted)), $"{omitted} must not be copied to skill/archify/references/.");
        }

        var expectedFiles = Directory.GetFiles(upstreamRefDir, "*.md")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !excluded.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var actualFiles = Directory.GetFiles(skillRefDir, "*.md")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedFiles, actualFiles);

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var fileName in expectedFiles)
        {
            var upstreamText = File.ReadAllText(Path.Combine(upstreamRefDir, fileName!), utf8NoBom);
            var expectedBytes = utf8NoBom.GetBytes(upstreamText.Replace("node bin/archify.mjs", "archify", StringComparison.Ordinal));
            var actualBytes = File.ReadAllBytes(Path.Combine(skillRefDir, fileName!));
            Assert.Equal(expectedBytes, actualBytes);
        }
    }

    [Fact]
    public void SkillSchemas_AndExamplesJson_MatchUpstreamByteForByte()
    {
        var upstreamSchemasDir = Path.Combine(UpstreamRoot, "schemas");
        var skillSchemasDir = Path.Combine(SkillRoot, "schemas");
        Assert.True(Directory.Exists(skillSchemasDir));

        var upstreamSchemaFiles = Directory.GetFiles(upstreamSchemasDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(upstreamSchemasDir, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        var skillSchemaFiles = Directory.GetFiles(skillSchemasDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skillSchemasDir, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(upstreamSchemaFiles, skillSchemaFiles);

        foreach (var rel in upstreamSchemaFiles)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(upstreamSchemasDir, rel)),
                File.ReadAllBytes(Path.Combine(skillSchemasDir, rel)));
        }

        var upstreamExamplesDir = Path.Combine(UpstreamRoot, "examples");
        var skillExamplesDir = Path.Combine(SkillRoot, "examples");
        Assert.True(Directory.Exists(skillExamplesDir));
        Assert.Empty(Directory.GetFiles(skillExamplesDir, "*.html", SearchOption.AllDirectories));

        var upstreamExampleJsonFiles = Directory.GetFiles(upstreamExamplesDir, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(upstreamExamplesDir, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        var skillExampleFiles = Directory.GetFiles(skillExamplesDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skillExamplesDir, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(upstreamExampleJsonFiles, skillExampleFiles);

        foreach (var rel in upstreamExampleJsonFiles)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(upstreamExamplesDir, rel)),
                File.ReadAllBytes(Path.Combine(skillExamplesDir, rel)));
        }
    }
}
