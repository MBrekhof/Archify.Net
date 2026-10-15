using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Paths;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 4: T/path-semantics.test.mjs (each test named after its upstream case; the probe-cleanup quarantine races :627-780
// are cut with the hardening they test), sidecarNamespaceComponentKey against values minted from Node (UP @ c21c31ff) on this NTFS
// volume, and the Windows path-identity facts: mklink /J junctions, 8.3 aliases, CreateHardLinkW hard links.
public partial class PathSemanticsTests
{
    static readonly PathSemantics Native = PathSemantics.Native;

    static void Expect(PathRelation value, string status, string? code = null)
    {
        Assert.Equal(status, value.Status);
        Assert.False(string.IsNullOrEmpty(value.Reason["code"]?.GetValue<string>()), value.Reason.ToJsonString());
        if (code is not null) Assert.Equal(code, value.Reason["code"]!.GetValue<string>());
    }

    // ---- T/path-semantics.test.mjs, mocked Windows filesystem (mockWindowsFilesystem :27-120) ----

    [Fact]
    public void Windows_drive_and_share_roots_reach_native_resolution_as_complete_filesystem_roots()
    {
        var fs = new MockWindowsFs();
        var semantics = new PathSemantics(fs);
        Expect(semantics.SameLocation(@"\\server\share\reports\diagram.html", @"\\?\UNC\server\share\reports\diagram.html"), "match");
        Expect(semantics.SameLocation(@"\\server\CON\reports", @"\\?\UNC\server\CON\reports"), "match");
        Expect(semantics.SameLocation(@"\\CON\share\reports", @"\\?\UNC\CON\share\reports"), "match");
        Expect(semantics.SameLocation(@"\\server\share", @"\\?\UNC\server\share"), "match");
        Expect(semantics.SameLocation(@"C:\reports\diagram.html", @"\\?\C:\reports\diagram.html"), "match");
        Expect(semantics.SameLocation(@"C:\", @"\\?\C:\"), "match");
        Assert.Contains(@"\\server\share\", fs.RealpathCalls);
        Assert.Contains(@"\\?\UNC\server\share\", fs.RealpathCalls);
        Assert.Contains(@"C:\", fs.RealpathCalls);
        Assert.Contains(@"\\?\C:\", fs.RealpathCalls);
    }

    [Fact]
    public void Windows_extended_drive_roots_retain_stable_identity_when_native_realpath_reports_EISDIR()
    {
        var fs = new MockWindowsFs { ExtendedRootRealpathIsDirectory = true, ExtendedRootStatIsDirectory = true };
        Expect(new PathSemantics(fs).SameLocation(@"\\?\C:\reports\diagram.html", @"C:\reports\diagram.html"), "match");
        Assert.Contains(@"C:\", fs.StatCalls);
    }

    [Fact]
    public void Windows_extended_drive_roots_retain_traversal_spelling_when_root_stat_reports_EISDIR()
    {
        var fs = new MockWindowsFs { PreserveExtendedRealpaths = true, ExtendedRootStatIsDirectory = true };
        Expect(new PathSemantics(fs).SameLocation(@"\\?\C:\reports\diagram.html", @"C:\reports\diagram.html"), "match");
        Assert.Contains(@"C:\", fs.StatCalls);
    }

    [Fact]
    public void Windows_extended_UNC_roots_use_ordinary_share_identity_when_root_APIs_report_EISDIR()
    {
        var fs = new MockWindowsFs { ExtendedRootRealpathIsDirectory = true, ExtendedRootStatIsDirectory = true };
        Expect(new PathSemantics(fs).SameLocation(@"\\?\UNC\server\share\reports\diagram.html", @"\\server\share\reports\diagram.html"), "match");
        Assert.Contains(@"\\server\share\", fs.StatCalls);
    }

    [Fact]
    public void Windows_device_and_malformed_extended_namespaces_fail_closed_before_filesystem_access()
    {
        var fs = new MockWindowsFs();
        var semantics = new PathSemantics(fs);
        foreach (var (targetPath, reason) in new[]
        {
            (@"\\.\NUL", "windows-namespace-unsupported"),
            ("//./NUL", "windows-namespace-unsupported"),
            (@"\\?\GLOBALROOT\Device\HarddiskVolume1\diagram.html", "windows-namespace-unsupported"),
            ("//?/GLOBALROOT/Device/HarddiskVolume1/diagram.html", "windows-namespace-unsupported"),
            (@"\\?\Volume{01234567-89AB-CDEF-0123-456789ABCDEF}\diagram.html", "windows-namespace-unsupported"),
            (@"\\server\IPC$\diagram.html", "windows-namespace-unsupported"),
            (@"\\server\pipe\diagram.html", "windows-namespace-unsupported"),
            (@"\\server\mailslot\diagram.html", "windows-namespace-unsupported"),
            (@"\\?\UNC\server\IPC$\diagram.html", "windows-namespace-unsupported"),
            (@"\\?\UNC\server\pipe\diagram.html", "windows-namespace-unsupported"),
            (@"\\?\UNC\server\mailslot\diagram.html", "windows-namespace-unsupported"),
            (@"\\server\bad+share\diagram.html", "windows-root-invalid"),
            (@"\\?\UNC\server\trailing.\diagram.html", "windows-root-invalid"),
            (@"\\?\UNC\server", "windows-root-invalid"),
            (@"\\?\other\diagram.html", "windows-root-invalid"),
            (@"\\server", "windows-root-invalid"),
            (@"\reports\diagram.html", "windows-root-invalid"),
            (@"C:reports\diagram.html", "drive-relative-path"),
        })
            Expect(semantics.SameLocation(targetPath, targetPath), "unknown", reason);
        Assert.Empty(fs.RealpathCalls);
        Assert.Empty(fs.LstatCalls);
    }

    [Fact]
    public void Windows_extended_dot_segments_fail_closed_before_filesystem_access()
    {
        var fs = new MockWindowsFs();
        foreach (var targetPath in new[]
        {
            @"\\?\C:\reports\.\diagram.html", @"\\?\C:\reports\..\diagram.html",
            @"\\?\UNC\server\share\reports\.\diagram.html", @"\\?\UNC\server\share\reports\..\diagram.html",
        })
            Expect(new PathSemantics(fs).SameLocation(targetPath, targetPath), "unknown", "windows-root-invalid");
        Assert.Empty(fs.RealpathCalls);
    }

    [Fact]
    public void Windows_extended_repeated_separators_fail_closed_before_filesystem_access()
    {
        var fs = new MockWindowsFs();
        foreach (var targetPath in new[] { @"\\?\C:\reports\\diagram.html", @"\\?\UNC\server\share\reports\\diagram.html" })
            Expect(new PathSemantics(fs).SameLocation(targetPath, targetPath), "unknown", "windows-root-invalid");
        Assert.Empty(fs.RealpathCalls);
    }

    [Fact]
    public void Relative_symbolic_link_navigation_remains_physical_through_an_extended_path()
    {
        const string link = @"\\?\C:\container\link.html";
        var fs = new MockWindowsFs { PreserveExtendedRealpaths = true, SymbolicLinks = { [link] = @"..\\target.html" } };
        Expect(new PathSemantics(fs).SameEntry(link, @"C:\target.html"), "match");
    }

    [Fact]
    public void Future_Windows_device_names_fail_closed_across_the_portable_reserved_set()
    {
        var semantics = new PathSemantics(new MockWindowsFs { EntriesExist = false });
        foreach (var reserved in new[] { "CONIN$.txt", "CONOUT$.json", "COM¹.html", "COM²", "LPT³.log" })
            Expect(semantics.SameLocation($@"C:\{reserved}", @"C:\ordinary-output.html"), "unknown", "future-device-name-indeterminate");
    }

    // ---- T/path-semantics.test.mjs, real filesystem ----

    [Fact]
    public void SameEntry_uses_physical_identity_for_files_symbolic_links_and_hard_links()
    {
        using var scratch = new Scratch();
        var original = scratch.File("original.txt", "original");
        var other = scratch.File("other.txt", "other");
        var hard = scratch.Path("hard.txt");
        HardLink(hard, original);
        var realDirectory = scratch.Directory("real-directory");
        var linkedDirectory = scratch.Path("linked-directory");
        Junction(linkedDirectory, realDirectory);

        Expect(Native.SameEntry(realDirectory, linkedDirectory), "match");
        Expect(Native.SameEntry(original, hard), "match");
        Expect(Native.SameEntry(original, other), "different");
        Expect(Native.SameEntry(original, scratch.Path("missing.txt")), "unknown");
    }

    [Fact]
    public void SameLocation_follows_directory_links_for_future_targets()
    {
        using var scratch = new Scratch();
        var realDirectory = scratch.Directory("real");
        var linkedDirectory = scratch.Path("linked");
        Junction(linkedDirectory, realDirectory);
        Expect(Native.SameLocation(Path.Join(realDirectory, "future", "diagram.html"), Path.Join(linkedDirectory, "future", "diagram.html")), "match");
    }

    [Fact]
    public void Physical_traversal_resolves_dot_dot_after_following_a_directory_alias()
    {
        using var scratch = new Scratch();
        var container = scratch.Directory("container");
        var nested = scratch.Directory(@"physical\nested");
        var escape = scratch.File(@"physical\escape.html", "physical target");
        Junction(Path.Join(container, "jump"), nested);

        var authored = $@"{container}\jump\..\escape.html";
        Expect(Native.SameEntry(authored, escape), "match");
        Expect(Native.ContainedBy(container, authored), "different");
        var resolved = Native.ResolvePhysicalLocation(authored);
        Assert.Equal("resolved", resolved.Status);
        Assert.Equal(("existing", NativeFs.RealPath(escape)), (resolved.Location!.Kind, resolved.Location.Path));
    }

    [Fact]
    public void A_missing_segment_before_dot_dot_is_not_lexically_collapsed_into_an_existing_entry()
    {
        using var scratch = new Scratch();
        var target = scratch.File("target.html", "target");
        Expect(Native.SameEntry($@"{scratch.Root}\missing\..\target.html", target), "unknown", "future-parent-traversal-indeterminate");
    }

    [Fact]
    public void Physical_resolution_preserves_dot_dot_inside_a_symbolic_link_target()
    {
        using var scratch = new Scratch();
        var container = scratch.Directory("container");
        var authoredParent = scratch.Directory("authored");
        var nested = scratch.Directory(@"physical\nested");
        var target = scratch.File(@"physical\target.html", "physical target");
        Junction(Path.Join(authoredParent, "jump"), nested);
        var link = Path.Join(container, "link.html");
        FileSymlink(link, @"..\authored\jump\..\target.html");

        var resolved = Native.ResolvePhysicalLocation(link);
        Assert.Equal("resolved", resolved.Status);
        Assert.Equal("existing", resolved.Location!.Kind);
        Assert.Equal(NativeFs.RealPath(target), resolved.Location.Path);
        Expect(Native.SameEntry(link, target), "match");
    }

    [Fact]
    public void Symbolic_link_targets_cannot_erase_a_missing_segment_or_a_cycle_with_dot_dot()
    {
        using var scratch = new Scratch();
        var target = scratch.File("target.html", "target");
        var missingLink = scratch.Path("missing-link.html");
        var loop = scratch.Path("loop");
        FileSymlink(missingLink, @"missing\..\target.html");
        FileSymlink(loop, @"loop\..");

        Expect(Native.SameEntry(missingLink, target), "unknown", "future-parent-traversal-indeterminate");
        Expect(Native.SameEntry(loop, scratch.Root), "unknown", "symlink-cycle");
    }

    [Fact]
    public void Physical_resolution_returns_a_real_ancestor_and_unresolved_future_segments()
    {
        using var scratch = new Scratch();
        var physical = scratch.Directory("physical");
        var nested = scratch.Directory(@"physical\nested");
        var alias = scratch.Path("alias");
        Junction(alias, nested);

        var resolved = Native.ResolvePhysicalLocation($@"{alias}\..\future\diagram.html");
        Assert.Equal("resolved", resolved.Status);
        Assert.Equal("future", resolved.Location!.Kind);
        Assert.Equal(NativeFs.RealPath(physical), resolved.Location.AncestorPath);
        Assert.Equal(["future", "diagram.html"], resolved.Location.Unresolved);
        Assert.Equal("""{"code":"future-physical-ancestor","method":"native-realpath"}""", resolved.Reason.ToJsonString());
    }

    [Fact]
    public void SameLocation_asks_the_containing_filesystem_about_case_only_future_names()
    {
        using var scratch = new Scratch();
        var comparison = Native.SameLocation(scratch.Path("Future.HTML"), scratch.Path("future.html"));
        Expect(comparison, AliasesCase(scratch.Root) ? "match" : "different");
        // Node on this volume: {"code":"future-location-alias","operation":"same-location","method":"filesystem-probe","probedSegments":1}
        if (comparison.Status == "match")
            Assert.Equal("""{"code":"future-location-alias","operation":"same-location","method":"filesystem-probe","probedSegments":1}""",
                comparison.Reason.ToJsonString());
    }

    [Fact]
    public void SameLocation_does_not_project_ancestor_semantics_through_a_future_directory()
    {
        using var scratch = new Scratch();
        var fs = new RecordingFs();
        var semantics = new PathSemantics(fs);
        var caseOverlap = semantics.SameLocation(scratch.Path(@"future-parent\Diagram.HTML"), scratch.Path(@"future-parent\diagram.html"));
        var normalizationOverlap = semantics.SameLocation(scratch.Path("future-parent\\Caf\u00e9.html"), scratch.Path("future-parent\\Cafe\u0301.html"));
        var distinct = semantics.SameLocation(scratch.Path(@"future-parent\diagram.html"), scratch.Path(@"future-parent\receipt.json"));

        foreach (var comparison in new[] { caseOverlap, normalizationOverlap })
        {
            Expect(comparison, "unknown", "future-descendant-semantics-indeterminate");
            Assert.Equal(1, comparison.Reason["segmentIndex"]!.GetValue<int>());
        }
        // Node: {"code":"future-descendant-semantics-indeterminate","method":"unmaterialized-directory","operation":"same-location","segmentIndex":1}
        Assert.Equal("""{"code":"future-descendant-semantics-indeterminate","method":"unmaterialized-directory","operation":"same-location","segmentIndex":1}""",
            caseOverlap.Reason.ToJsonString());
        Expect(distinct, "different", "future-name-distinct");
        Assert.Empty(fs.Created);
    }

    [Fact]
    public void SameLocation_probes_an_existing_intermediate_directory_using_its_own_semantics()
    {
        using var scratch = new Scratch();
        var intermediate = scratch.Directory("existing-parent");
        Expect(Native.SameLocation(Path.Join(intermediate, "Diagram.HTML"), Path.Join(intermediate, "diagram.html")),
            AliasesCase(intermediate) ? "match" : "different");
    }

    [Fact]
    public void SameLocation_distinguishes_different_existing_entries_and_future_depths()
    {
        using var scratch = new Scratch();
        var left = scratch.File("left.txt", "left");
        var right = scratch.File("right.txt", "right");
        Expect(Native.SameLocation(left, right), "different");
        var depths = Native.SameLocation(scratch.Path("future.txt"), scratch.Path(@"nested\future.txt"));
        Expect(depths, "different");
        Assert.Equal("""{"code":"future-depth-different","operation":"same-location","leftDepth":1,"rightDepth":2}""", depths.Reason.ToJsonString());
        Expect(Native.SameLocation(Path.Join(left, "future.txt"), Path.Join(left, "future.txt")), "unknown", "ancestor-not-directory");
    }

    // Upstream fails the quarantine unlink; with the quarantine cut (:260-324) the probe's own delete fails.
    [Fact]
    public void SameLocation_reports_probe_cleanup_failures_as_unknown()
    {
        using var scratch = new Scratch();
        var fs = new RecordingFs { OnDelete = path => { File.Delete(path); throw new NativeFsException("EIO", "simulated probe cleanup failure"); } };
        var comparison = new PathSemantics(fs).SameLocation(scratch.Path("Future.HTML"), scratch.Path("future.html"));
        Expect(comparison, "unknown", "future-name-probe-cleanup-failed");
        Assert.Equal("EIO", comparison.Reason["systemCode"]!.GetValue<string>());
        Assert.IsType<string>(comparison.Reason["comparison"]?["code"]?.GetValue<string>());
    }

    [Fact]
    public void SameLocation_does_not_recursively_probe_its_own_watcher_events()
    {
        using var scratch = new Scratch();
        var fs = new RecordingFs();
        new PathSemantics(fs).SameLocation(scratch.Path("Future.HTML"), scratch.Path("future.html"));
        var probeName = Path.GetFileName(Assert.Single(fs.Created));

        Expect(Native.SameLocation(scratch.Path(probeName), scratch.Path("future.html")), "different", "internal-probe-name");
    }

    [Fact]
    public void SameLocation_proves_distinct_safe_ASCII_names_without_writing_to_the_directory()
    {
        using var scratch = new Scratch();
        var fs = new RecordingFs { OnCreate = _ => throw new NativeFsException("EACCES", "the directory is read-only") };
        var comparison = new PathSemantics(fs).SameLocation(scratch.Path("diagram.workflow.json"), scratch.Path("diagram.html"));
        Expect(comparison, "different", "future-name-distinct");
        Assert.Equal("semantic-envelope", comparison.Reason["method"]!.GetValue<string>());
    }

    [Fact]
    public void SameLocation_proves_a_long_Unicode_artifact_distinct_from_a_bounded_hash_sidecar()
    {
        using var scratch = new Scratch();
        var longNfd = string.Concat(Enumerable.Repeat("e\u0301", 76)) + "-architecture.html";
        var fs = new RecordingFs { OnCreate = _ => throw new InvalidOperationException("a distinct-name proof must not write a probe") };
        var comparison = new PathSemantics(fs).SameLocation(scratch.Path(longNfd), scratch.Path(".archify-delivery-a3f5712c9e19.json"));
        Expect(comparison, "different", "future-name-distinct");
        Assert.Equal("semantic-envelope", comparison.Reason["method"]!.GetValue<string>());
    }

    [Fact]
    public void SameLocation_never_unlinks_a_probe_path_it_did_not_create()
    {
        using var scratch = new Scratch();
        var fs = new RecordingFs { OnCreate = _ => throw new NativeFsException("EACCES", "probe creation denied") };
        var comparison = new PathSemantics(fs).SameLocation(scratch.Path("Future.HTML"), scratch.Path("future.html"));
        Expect(comparison, "unknown", "future-name-probe-failed");
        Assert.Equal("EACCES", comparison.Reason["systemCode"]!.GetValue<string>());
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void SameLocation_fails_closed_when_a_direct_probe_would_exceed_the_component_limit()
    {
        using var scratch = new Scratch();
        var upper = new string('a', 220) + "A.html";
        var comparison = Native.SameLocation(scratch.Path(upper), scratch.Path(upper.ToLowerInvariant()));
        if (AliasesCase(scratch.Root)) Expect(comparison, "unknown", "future-name-probe-unrepresentable");
        else Assert.Contains(comparison.Status, new[] { "different", "unknown" });
    }

    [Fact]
    public void Successful_probe_cleanup_leaves_no_probe_entry()
    {
        using var scratch = new Scratch();
        Native.SameLocation(scratch.Path("Future.HTML"), scratch.Path("future.html"));
        Assert.Empty(Directory.GetFileSystemEntries(scratch.Root, ".archify-path-semantics-*"));
    }

    [Fact]
    public void Future_name_probes_run_in_the_directory_whose_semantics_are_being_queried()
    {
        using var scratch = new Scratch();
        var fs = new RecordingFs();
        new PathSemantics(fs).SameLocation(scratch.Path("Future.HTML"), scratch.Path("future.html"));
        Assert.Equal(NativeFs.RealPath(scratch.Root), Path.GetDirectoryName(Assert.Single(fs.Created)));
    }

    [Fact]
    public void Future_Windows_short_name_shapes_fail_closed_before_creation()
    {
        using var scratch = new Scratch();
        Expect(Native.SameLocation(scratch.Path(@"PROGRA~1\diagram.html"), scratch.Path(@"Program Files\diagram.html")),
            "unknown", "future-short-name-indeterminate");
    }

    [Fact]
    public void SameParent_compares_the_parent_location_through_directory_aliases()
    {
        using var scratch = new Scratch();
        var realDirectory = scratch.Directory("real");
        var otherDirectory = scratch.Directory("other");
        var linkedDirectory = scratch.Path("linked");
        Junction(linkedDirectory, realDirectory);
        Expect(Native.SameParent(Path.Join(realDirectory, "left.html"), Path.Join(linkedDirectory, "right.json")), "match");
        Expect(Native.SameParent(Path.Join(realDirectory, "left.html"), Path.Join(otherDirectory, "right.json")), "different");
    }

    [Fact]
    public void ContainedBy_handles_existing_and_future_descendants_and_rejects_link_escapes()
    {
        using var scratch = new Scratch();
        var container = scratch.Directory("container");
        var nested = scratch.Directory(@"container\nested");
        var outside = scratch.Directory("outside");
        var file = scratch.File(@"container\nested\diagram.html", "diagram");
        var escape = Path.Join(container, "escape");
        Junction(escape, outside);

        Expect(Native.ContainedBy(container, container), "match");
        Expect(Native.ContainedBy(container, file), "match");
        Expect(Native.ContainedBy(container, Path.Join(nested, "future.html")), "match");
        Expect(Native.ContainedBy(container, Path.Join(escape, "future.html")), "different");
        Expect(Native.ContainedBy(container, Path.Join(outside, "future.html")), "different");
        var missing = Native.ContainedBy(scratch.Path("missing"), file);
        Expect(missing, "unknown");
        Assert.Equal("""{"code":"container-missing","operation":"contained-by"}""", missing.Reason.ToJsonString());
    }

    [Fact]
    public void ContainedBy_does_not_fold_case_distinct_sibling_directories()
    {
        using var scratch = new Scratch();
        var upper = scratch.Directory("CaseRoot");
        var sibling = Directory.Exists(scratch.Path("caseroot")) ? scratch.Directory("different-root") : scratch.Directory("caseroot");
        Assert.NotEqual(NativeFs.Identity(upper), NativeFs.Identity(sibling));
        Expect(Native.ContainedBy(upper, Path.Join(sibling, "future.html")), "different");
    }

    [Fact]
    public void ContainedBy_resolves_physical_aliases_for_a_future_container_prefix()
    {
        using var scratch = new Scratch();
        var physical = scratch.Directory("physical");
        var alias = scratch.Path("alias");
        Junction(alias, physical);
        Expect(Native.ContainedBy(Path.Join(alias, "future-stage"), Path.Join(physical, "future-stage", "modes.json")), "match",
            "future-container-prefix-match");
        Expect(Native.ContainedBy(Path.Join(alias, "other-stage"), Path.Join(physical, "future-stage", "modes.json")), "different");
    }

    [Fact]
    public void ContainedBy_does_not_project_ancestor_semantics_through_a_future_container()
    {
        using var scratch = new Scratch();
        var comparison = Native.ContainedBy(scratch.Path(@"future-parent\Diagram"), scratch.Path(@"future-parent\diagram\artifact.html"));
        Expect(comparison, "unknown", "future-descendant-semantics-indeterminate");
        Assert.Equal(1, comparison.Reason["segmentIndex"]!.GetValue<int>());
    }

    // ---- sidecarNamespaceComponentKey (:737-790); keys minted from Node in a temp directory on this NTFS volume ----

    [Theory]
    [InlineData("WebApp.HTML", "webapp.html")]
    [InlineData("\u00DCn\u00EFcode Stra\u00DFe.html", "\u00FCn\u00EFcode stra\u00DFe.html")]
    [InlineData("Cafe\u0301.HTML", "cafe\u0301.html")]
    [InlineData("Caf\u00E9.HTML", "caf\u00E9.html")]
    [InlineData("\u03A3\u03AF\u03C3\u03C5\u03C6\u03BF\u03C2.html", "\u03C3\u03AF\u03C3\u03C5\u03C6\u03BF\u03C2.html")]
    [InlineData("\u01C5emal.html", "\u01C5emal.html")]
    [InlineData("\u0130stanbul.html", "\u0130stanbul.html")]
    [InlineData("\uFB00.html", "\uFB00.html")]
    [InlineData("PROGRA~1.html", "progra~1.html")]
    [InlineData("a.html", "a.html")]
    public void Sidecar_namespace_keys_match_node_on_ntfs(string component, string expected)
    {
        using var scratch = new Scratch();
        var key = Native.SidecarNamespaceComponentKey(scratch.Root, component);
        Assert.Equal(("resolved", expected, NativeFs.RealPath(scratch.Root)), (key.Status, key.ComponentKey, key.DirectoryPath));
        Assert.Equal("""{"code":"sidecar-namespace-key","method":"directory-filesystem-probe"}""", key.Reason.ToJsonString());
        Assert.Empty(Directory.GetFileSystemEntries(scratch.Root));
    }

    [Fact]
    public void Sidecar_namespace_key_keeps_a_lone_surrogate()
    {
        using var scratch = new Scratch();
        Assert.Equal("x\uD800y.html", Native.SidecarNamespaceComponentKey(scratch.Root, "x\uD800y.html").ComponentKey);
    }

    [Fact]
    public void Sidecar_namespace_key_failures_match_node()
    {
        using var scratch = new Scratch();
        var file = scratch.File("file.txt", "x");
        foreach (var (directory, component, reason) in new[]
        {
            (scratch.Path("missing"), "a.html", """{"code":"sidecar-directory-missing","operation":"sidecar-namespace-key"}"""),
            (file, "a.html", """{"code":"sidecar-parent-not-directory","operation":"sidecar-namespace-key"}"""),
            (scratch.Root, "a/b.html", """{"code":"invalid-sidecar-component","operation":"sidecar-namespace-key"}"""),
            (scratch.Root, "..", """{"code":"invalid-sidecar-component","operation":"sidecar-namespace-key"}"""),
        })
        {
            var key = Native.SidecarNamespaceComponentKey(directory, component);
            Assert.Equal(("unknown", reason), (key.Status, key.Reason.ToJsonString()));
        }
    }

    // A probe that cannot run leaves the key indeterminate; the probe's reason is kept as the cause.
    [Fact]
    public void Sidecar_namespace_key_is_indeterminate_when_the_probe_fails()
    {
        using var scratch = new Scratch();
        var fs = new RecordingFs { OnCreate = _ => throw new NativeFsException("EACCES", "denied") };
        var key = new PathSemantics(fs).SidecarNamespaceComponentKey(scratch.Root, "A.html");
        Assert.Equal("unknown", key.Status);
        Assert.Equal("""{"code":"sidecar-case-semantics-indeterminate","operation":"sidecar-namespace-key","cause":{"code":"future-name-probe-failed","method":"filesystem-probe","systemCode":"EACCES"}}""",
            key.Reason.ToJsonString());
    }

    // ---- Windows path identity facts (NativeFs) ----

    [Fact]
    public void RealPath_strips_the_extended_prefix_and_restores_canonical_case()
    {
        using var scratch = new Scratch();
        var file = scratch.File("MixedCase.html", "x");
        var real = NativeFs.RealPath(file);
        Assert.Equal(real, NativeFs.RealPath(@"\\?\" + file));
        Assert.Equal(real, NativeFs.RealPath(file.ToUpperInvariant()));
        Assert.EndsWith(@"\MixedCase.html", real);
        Assert.DoesNotContain(@"\\?\", real);
        var missing = Assert.ThrowsAny<IOException>(() => NativeFs.RealPath(scratch.Path("missing.html")));
        Assert.Equal("ENOENT", NativeFs.ErrorCode(missing));

        var root = Path.GetPathRoot(scratch.Root)!;   // C:\ — the real counterpart of the mocked extended-root cases
        Assert.Equal(root, NativeFs.RealPath(@"\\?\" + root));
        Assert.True(NativeFs.Stat(@"\\?\" + root).IsDirectory);
        Expect(Native.SameLocation(root, @"\\?\" + root), "match", "file-identity-match");
        Expect(Native.SameEntry(@"\\?\" + file, file.ToLowerInvariant()), "match", "file-identity-match");
    }

    [Fact]
    public void A_mklink_junction_is_a_link_that_resolves_to_its_target()
    {
        using var scratch = new Scratch();
        var target = scratch.Directory("target");
        var junction = scratch.Path("junction");
        Junction(junction, target);
        var stat = NativeFs.LStat(junction);
        Assert.True(stat.IsSymbolicLink);
        Assert.False(stat.IsDirectory);
        Assert.True(NativeFs.Stat(junction).IsDirectory);
        Assert.Equal(target, NativeFs.ReadLink(junction));
        Assert.Equal(NativeFs.RealPath(target), NativeFs.RealPath(junction));
        Assert.Equal(NativeFs.Identity(target), NativeFs.Identity(junction));
    }

    [Fact]
    public void An_8dot3_alias_resolves_to_the_long_name()
    {
        using var scratch = new Scratch();
        var longDirectory = scratch.Directory("Long Directory Name");
        var file = scratch.File(@"Long Directory Name\Diagram Output.html", "x");
        var shortDirectory = Cmd($"/c for %I in (\"{longDirectory}\") do @echo %~sI").Trim();
        Assert.True(!string.Equals(Path.GetFileName(shortDirectory), "Long Directory Name", StringComparison.OrdinalIgnoreCase),
            $"8.3 short-name generation is disabled on the temp volume ({longDirectory} has no short alias; check `fsutil 8dot3name query`), " +
            "so this fact cannot be observed here.");
        Assert.Matches("~[1-9]", shortDirectory);

        Assert.Equal(NativeFs.RealPath(longDirectory), NativeFs.RealPath(shortDirectory));
        Expect(Native.SameEntry(Path.Join(shortDirectory, "Diagram Output.html"), file), "match");
        Assert.Equal(NativeFs.RealPath(longDirectory), Native.SidecarNamespaceComponentKey(shortDirectory, "Diagram Output.html").DirectoryPath);
    }

    [Fact]
    public void A_CreateHardLinkW_hard_link_shares_identity_and_counts_its_links()
    {
        using var scratch = new Scratch();
        var original = scratch.File("original.html", "x");
        var hard = scratch.Path("hard.html");
        HardLink(hard, original);
        var identity = NativeFs.Identity(original)!.Value;
        Assert.Equal(identity, NativeFs.Identity(hard));
        Assert.Equal(2u, identity.LinkCount);
        Assert.NotEqual(NativeFs.RealPath(original), NativeFs.RealPath(hard));
        Expect(Native.SameLocation(original, hard), "match", "file-identity-match");
    }

    // ---- helpers ----

    sealed class Scratch : IDisposable
    {
        public string Root { get; } = System.IO.Directory.CreateTempSubdirectory("archify-path-semantics-").FullName;
        public string Path(string relative) => System.IO.Path.Join(Root, relative);
        public string Directory(string relative) => System.IO.Directory.CreateDirectory(Path(relative)).FullName;
        public string File(string relative, string content) { var path = Path(relative); System.IO.File.WriteAllText(path, content); return path; }
        // Links first: a recursive delete calls DeleteVolumeMountPoint on a junction, which is access-denied without elevation.
        public void Dispose()
        {
            var links = System.IO.Directory.EnumerateFileSystemEntries(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .Where(entry => System.IO.File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)).ToList();
            foreach (var link in links)
                if (System.IO.File.GetAttributes(link).HasFlag(FileAttributes.Directory)) System.IO.Directory.Delete(link);
                else System.IO.File.Delete(link);
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }

    // The test's own ArchifyCaseProbe check (:409-414): does this directory fold case?
    static bool AliasesCase(string directory)
    {
        var authored = Path.Join(directory, "ArchifyCaseProbe");
        File.WriteAllText(authored, "probe");
        try { return NativeFs.Identity(authored) == (File.Exists(Path.Join(directory, "archifycaseprobe")) ? NativeFs.Identity(Path.Join(directory, "archifycaseprobe")) : null); }
        finally { File.Delete(authored); }
    }

    static string Cmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"cmd {arguments} exited {process.ExitCode}: {stderr}");
        return stdout;
    }

    // mklink /J needs no elevation.
    static void Junction(string link, string target) => Cmd($"/c mklink /J \"{link}\" \"{target}\"");

    // Upstream skips when file symbolic links are unavailable; here that fails loudly with the fix (Q10).
    static void FileSymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Fail($"file symbolic links are unavailable ({error.Message}); enable Windows Developer Mode or run elevated.");
        }
    }

    static void HardLink(string link, string existing) =>
        Assert.True(CreateHardLinkW(link, existing, 0), $"CreateHardLinkW failed: Win32 error {Marshal.GetLastPInvokeError()}");

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);

    // NativeFs with recorded probe creations and deletes and optional failures (upstream's t.mock.method on openSync/unlinkSync).
    sealed class RecordingFs : IPathFs
    {
        public List<string> Created { get; } = [];
        public List<string> Deleted { get; } = [];
        public Func<string, IDisposable>? OnCreate { get; init; }
        public Action<string>? OnDelete { get; init; }
        public string RealPath(string path) => NativeFs.Ops.RealPath(path);
        public FsStat Stat(string path) => NativeFs.Ops.Stat(path);
        public FsStat LStat(string path) => NativeFs.Ops.LStat(path);
        public string ReadLink(string path) => NativeFs.Ops.ReadLink(path);
        public IDisposable CreateExclusive(string path)
        {
            var handle = OnCreate is null ? NativeFs.Ops.CreateExclusive(path) : OnCreate(path);
            Created.Add(path);
            return handle;
        }
        public void Delete(string path)
        {
            Deleted.Add(path);
            if (OnDelete is null) NativeFs.Ops.Delete(path); else OnDelete(path);
        }
    }

    // mockWindowsFilesystem (:27-120): a virtual, case-insensitive Windows namespace where every entry exists (unless EntriesExist
    // is false) and is a directory, identities are handed out per canonical lower-cased path, and listed paths are symbolic links.
    sealed partial class MockWindowsFs : IPathFs
    {
        public bool EntriesExist { get; init; } = true;
        public bool PreserveExtendedRealpaths { get; init; }
        public bool ExtendedRootRealpathIsDirectory { get; init; }
        public bool ExtendedRootStatIsDirectory { get; init; }
        public Dictionary<string, string> SymbolicLinks { get; } = [];
        public List<string> RealpathCalls { get; } = [];
        public List<string> LstatCalls { get; } = [];
        public List<string> StatCalls { get; } = [];
        readonly Dictionary<string, ulong> identities = [];

        [GeneratedRegex(@"^\\\\\?\\UNC\\[^\\]+\\[^\\]+(?:\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ExtendedUnc();
        [GeneratedRegex(@"^\\\\\?\\[A-Za-z]:\\")]
        private static partial Regex ExtendedDrive();
        [GeneratedRegex(@"^\\\\[^\\]+\\[^\\]+(?:\\|$)")]
        private static partial Regex Unc();
        [GeneratedRegex(@"^[A-Za-z]:\\")]
        private static partial Regex Drive();
        [GeneratedRegex(@"^\\\\\?\\(?:[A-Za-z]:\\|UNC\\[^\\]+\\[^\\]+\\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ExtendedRoot();
        [GeneratedRegex(@"^(?:\\\\\?\\UNC\\[^\\]+\\[^\\]+\\?|\\\\\?\\[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+\\?|[A-Za-z]:\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Root();

        // path.win32.normalize for the rooted paths these tests use: `.` dropped, `..` popped, separators collapsed.
        static string Normalize(string path)
        {
            var root = Root().Match(path).Value;
            var segments = new List<string>();
            foreach (var segment in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
                else if (segment != ".") segments.Add(segment);
            return (root.EndsWith('\\') ? root : root + "\\") + string.Join('\\', segments);
        }

        static string Canonical(string path)
        {
            string canonical;
            if (ExtendedUnc().IsMatch(path)) canonical = @"\\" + path[8..];
            else if (ExtendedDrive().IsMatch(path)) canonical = path[4..];
            else if (Unc().IsMatch(path) || Drive().IsMatch(path)) canonical = path;
            else throw new NativeFsException("EINVAL", $"virtual Windows path is not rooted at a drive or share: {path}");
            return Normalize(canonical);
        }

        static string Key(string path) => Canonical(path).ToLowerInvariant();

        public string RealPath(string path)
        {
            RealpathCalls.Add(path);
            if (ExtendedRootRealpathIsDirectory && ExtendedRoot().IsMatch(path))
                throw new NativeFsException("EISDIR", $"virtual Windows extended root is a directory: {path}");
            return PreserveExtendedRealpaths ? Normalize(path) : Canonical(path);
        }

        public FsStat Stat(string path)
        {
            StatCalls.Add(path);
            if (ExtendedRootStatIsDirectory && ExtendedRoot().IsMatch(path))
                throw new NativeFsException("EISDIR", $"virtual Windows extended root is a directory: {path}");
            var key = Key(path);
            if (!identities.TryGetValue(key, out var index)) identities[key] = index = (ulong)identities.Count + 1;
            return new FsStat(true, false, new FileIdentity(1, index, 1));
        }

        public FsStat LStat(string path)
        {
            LstatCalls.Add(path);
            if (!EntriesExist) throw new NativeFsException("ENOENT", $"virtual Windows entry does not exist: {path}");
            var stat = Stat(path);
            return SymbolicLinks.Keys.Any(link => Key(link) == Key(path)) ? stat with { IsDirectory = false, IsSymbolicLink = true } : stat;
        }

        public string ReadLink(string path) =>
            SymbolicLinks.FirstOrDefault(link => Key(link.Key) == Key(path)).Value
            ?? throw new NativeFsException("EINVAL", $"virtual Windows entry is not a symbolic link: {path}");

        public IDisposable CreateExclusive(string path) => throw new InvalidOperationException($"the virtual filesystem takes no probes: {path}");
        public void Delete(string path) => throw new InvalidOperationException($"the virtual filesystem takes no probes: {path}");
    }
}
