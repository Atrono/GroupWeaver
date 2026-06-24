using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the WP6a raw-JSONC ("Advanced" tab) editor contract on
/// <see cref="SettingsViewModel"/> (<c>RawEditorText</c> / <c>RawEditorErrors</c> /
/// <c>RawEditorValid</c> / <c>SeedRawEditor</c> / <c>ApplyRaw</c>) plus the
/// <see cref="LineNumberGutterConverter"/> gutter mapping. The raw text is UNTRUSTED
/// and only ever flows through the SINGLE gate (<see cref="RulesetLoader.Load"/>,
/// JSONC + <c>UnmappedMemberHandling.Disallow</c>); see
/// <c>.claude/rules/rule-model.md</c> (Load never-throws, JSON-pathed errors,
/// whole-file precedence, the byte fixed point).
///
/// <para>The pins:</para>
/// <list type="bullet">
/// <item><b>Seed on open / re-seed:</b> a freshly-opened VM's <c>RawEditorText</c> is
/// the serialized current ruleset; <c>SeedRawEditor()</c> re-canonicalizes after a
/// raw edit.</item>
/// <item><b>Valid Apply:</b> a valid edited JSONC → <c>ApplyRaw()</c> true, the
/// structured mirror is re-seeded from the re-parsed ruleset, <c>RulesetApplied</c>
/// fires exactly once with the re-parsed ruleset.</item>
/// <item><b>Invalid Apply:</b> bad/unknown-property/semantic JSONC → live
/// <c>RawEditorValid==false</c> + path-addressed <c>RawEditorErrors</c>;
/// <c>ApplyRaw()</c> false; nothing applied (no event, mirror unchanged, no disk
/// write).</item>
/// <item><b>Whole-file replace (no merge):</b> fewer naming rules than current ⇒ the
/// extras are dropped, not merged.</item>
/// <item><b>Re-canonicalization on apply:</b> JSONC comments / odd whitespace
/// normalize to serializer output — the byte fixed point.</item>
/// <item><b>Gutter:</b> <c>ToLineNumbers</c> = newline-count + 1.</item>
/// <item><b>Single write path:</b> the applied ruleset is the loader fixed point of
/// the edit, not the raw bytes.</item>
/// </list>
///
/// <para>Pure VM logic against a <c>RulesetLocator(baseDir)</c> temp-dir seam (#124) —
/// no headless UI, no dispatcher. Projections (sorted (id, …) tuples) are compared,
/// never record identity.</para>
/// </summary>
public sealed class SettingsRawEditorTests
{
    // === 1. seed on open / re-seed re-canonicalizes ====================================

    /// <summary>
    /// A freshly-opened editor starts with <c>RawEditorText</c> equal to the serialized
    /// current ruleset (<c>Serialize(BuildRuleset())</c>) — the raw view is in
    /// lock-step with the structured tabs from the first frame.
    /// </summary>
    [Fact]
    public void Open_SeedsRawEditorText_ToSerializedCurrentRuleset()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        Assert.Equal(RulesetSerializer.Serialize(vm.BuildRuleset()), vm.RawEditorText);
        Assert.True(vm.RawEditorValid);
        Assert.Empty(vm.RawEditorErrors);
    }

    /// <summary>
    /// After the raw text is hand-edited away from canonical, <c>SeedRawEditor()</c>
    /// re-canonicalizes it back to <c>Serialize(BuildRuleset())</c> (the "Load current"
    /// action) — the structured mirror is the source of truth, untouched by the edit.
    /// </summary>
    [Fact]
    public void SeedRawEditor_AfterRawEdit_ReCanonicalizesFromStructuredMirror()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        vm.RawEditorText = "// scratch edits the user is abandoning\n{ }";

        vm.SeedRawEditor();

        Assert.Equal(RulesetSerializer.Serialize(vm.BuildRuleset()), vm.RawEditorText);
    }

    // === 2. valid Apply ================================================================

    /// <summary>
    /// Setting <c>RawEditorText</c> to a VALID edited JSONC and calling
    /// <c>ApplyRaw()</c> returns true; the effective ruleset reflects the edit (the
    /// metadata name), the STRUCTURED mirror is re-seeded (the changed field shows in
    /// <c>Metadata</c>), and <c>RulesetApplied</c> fires EXACTLY once with the
    /// re-parsed ruleset.
    /// </summary>
    [Fact]
    public void ApplyRaw_ValidEdit_ReturnsTrue_ReseedsMirror_FiresAppliedOnce()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        var edited = RulesetLoader.LoadDefault() with { Name = "Raw-edited name" };
        vm.RawEditorText = RulesetSerializer.Serialize(edited);

        var applied = new List<Ruleset>();
        vm.RulesetApplied += applied.Add;

        bool ok = vm.ApplyRaw();

        Assert.True(ok);
        Assert.True(vm.RawEditorValid);
        Assert.Empty(vm.RawEditorErrors);
        // Structured mirror re-seeded from the re-parsed ruleset.
        Assert.Equal("Raw-edited name", vm.Metadata.Name);
        // RulesetApplied fired exactly once, with the edit.
        var fired = Assert.Single(applied);
        Assert.Equal("Raw-edited name", fired.Name);
    }

    /// <summary>
    /// A valid Apply that edits a NAMING rule re-seeds the structured naming
    /// collection: the changed field (a renamed rule id) shows in <c>vm.Naming</c>.
    /// Projection compared (the id sequence), never record identity.
    /// </summary>
    [Fact]
    public void ApplyRaw_ValidNamingEdit_ReseedsStructuredNamingCollection()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        var defaults = RulesetLoader.LoadDefault();
        Assert.True(defaults.Naming.Count >= 1);
        var renamed = defaults.Naming
            .Select((r, i) => i == 0 ? r with { Id = "naming-renamed" } : r)
            .ToList();
        var edited = defaults with { Naming = renamed };
        vm.RawEditorText = RulesetSerializer.Serialize(edited);

        Assert.True(vm.ApplyRaw());

        Assert.Equal(
            renamed.Select(r => r.Id).ToList(),
            vm.Naming.Select(r => r.Id).ToList());
        Assert.Contains("naming-renamed", vm.Naming.Select(r => r.Id));
    }

    // === 3. invalid Apply ==============================================================

    /// <summary>
    /// Setting <c>RawEditorText</c> to MALFORMED JSON makes the live validation flip
    /// <c>RawEditorValid</c> false and populate <c>RawEditorErrors</c> (rooted at
    /// <c>$</c>); <c>ApplyRaw()</c> returns false and applies NOTHING (no event, mirror
    /// unchanged, no disk write).
    /// </summary>
    [Fact]
    public void ApplyRaw_MalformedJson_LiveInvalid_ReturnsFalse_AppliesNothing()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        string before = RulesetSerializer.Serialize(vm.BuildRuleset());

        var applied = new List<Ruleset>();
        vm.RulesetApplied += applied.Add;

        vm.RawEditorText = "{ \"schemaVersion\": 1, \"name\": \"x\", ";

        // Live (on-change) validation already reflects the failure.
        Assert.False(vm.RawEditorValid);
        var liveError = Assert.Single(vm.RawEditorErrors);
        Assert.Equal("$", liveError.Path);

        bool ok = vm.ApplyRaw();

        Assert.False(ok);
        Assert.Empty(applied);
        Assert.Equal(before, RulesetSerializer.Serialize(vm.BuildRuleset()));
        Assert.False(File.Exists(locator.UserRulesetPath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// An UNKNOWN top-level property (the loader's <c>UnmappedMemberHandling.Disallow</c>)
    /// is rejected: live invalid, path-addressed error, <c>ApplyRaw()</c> false,
    /// nothing applied.
    /// </summary>
    [Fact]
    public void ApplyRaw_UnknownProperty_LiveInvalid_ReturnsFalse_AppliesNothing()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        string before = RulesetSerializer.Serialize(vm.BuildRuleset());

        var applied = new List<Ruleset>();
        vm.RulesetApplied += applied.Add;

        // A valid default with one extra, unmapped property injected.
        string text = RulesetSerializer.Serialize(RulesetLoader.LoadDefault())
            .TrimEnd().TrimEnd('}')
            + ",\n  \"bogusUnknownProperty\": 42\n}";
        vm.RawEditorText = text;

        Assert.False(vm.RawEditorValid);
        Assert.NotEmpty(vm.RawEditorErrors);

        bool ok = vm.ApplyRaw();

        Assert.False(ok);
        Assert.Empty(applied);
        Assert.Equal(before, RulesetSerializer.Serialize(vm.BuildRuleset()));
        Assert.False(File.Exists(locator.UserRulesetPath));
    }

    /// <summary>
    /// A SEMANTICALLY-invalid ruleset (a duplicate naming id — parses fine, fails the
    /// loader's case-insensitive uniqueness rule, path <c>$.naming[1].id</c>) is
    /// rejected the same way: live invalid, <c>ApplyRaw()</c> false, nothing applied.
    /// </summary>
    [Fact]
    public void ApplyRaw_SemanticError_DuplicateNamingId_ReturnsFalse_AppliesNothing()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        string before = RulesetSerializer.Serialize(vm.BuildRuleset());

        var applied = new List<Ruleset>();
        vm.RulesetApplied += applied.Add;

        var defaults = RulesetLoader.LoadDefault();
        Assert.True(defaults.Naming.Count >= 2, "the default ships >= 2 naming rules");
        var dup = defaults.Naming.ToList();
        dup[1] = dup[1] with { Id = dup[0].Id };
        vm.RawEditorText = RulesetSerializer.Serialize(defaults with { Naming = dup });

        Assert.False(vm.RawEditorValid);
        Assert.Contains(vm.RawEditorErrors, e => e.Path == "$.naming[1].id");

        bool ok = vm.ApplyRaw();

        Assert.False(ok);
        Assert.Empty(applied);
        Assert.Equal(before, RulesetSerializer.Serialize(vm.BuildRuleset()));
        Assert.False(File.Exists(locator.UserRulesetPath));
    }

    // === 4. whole-file replace (no merge) ==============================================

    /// <summary>
    /// Applying raw JSONC with FEWER naming rules than the current mirror DROPS the
    /// extras (whole-file replace, ADR-008) — the structured naming collection shrinks
    /// to exactly the applied set, never merging the prior rules back in.
    /// </summary>
    [Fact]
    public void ApplyRaw_FewerNamingRules_DropsExtras_NoMerge()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        int originalCount = vm.Naming.Count;
        Assert.True(originalCount >= 2, "the default ships >= 2 naming rules");

        // Keep only the first naming rule.
        var defaults = RulesetLoader.LoadDefault();
        var trimmed = defaults with { Naming = new[] { defaults.Naming[0] } };
        vm.RawEditorText = RulesetSerializer.Serialize(trimmed);

        Assert.True(vm.ApplyRaw());

        Assert.True(vm.Naming.Count < originalCount);
        Assert.Equal(
            new[] { defaults.Naming[0].Id },
            vm.Naming.Select(r => r.Id).ToArray());
    }

    // === 5. re-canonicalization on apply (the byte fixed point) ========================

    /// <summary>
    /// After a successful <c>ApplyRaw</c> of text carrying JSONC comments and odd
    /// whitespace, <c>RawEditorText</c> normalizes to the serializer's output (comments
    /// stripped, canonical whitespace) — the byte fixed point: the raw view re-seeds
    /// from the structured mirror, never echoing the user's loose bytes.
    /// </summary>
    [Fact]
    public void ApplyRaw_TextWithCommentsAndOddWhitespace_NormalizesRawTextToSerializerOutput()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        var edited = RulesetLoader.LoadDefault() with { Name = "Canonicalize me" };
        string canonical = RulesetSerializer.Serialize(edited);

        // Decorate the canonical text with a leading comment + trailing whitespace +
        // blank lines: still valid JSONC, but NOT byte-equal to the serializer output.
        string loose = "// a comment the serializer will strip\n\n   "
            + canonical + "\n\n   \n";
        Assert.NotEqual(canonical, loose);

        vm.RawEditorText = loose;
        Assert.True(vm.ApplyRaw());

        // Re-canonicalized: comments gone, whitespace normalized, byte-equal to the
        // serializer's output for the (re-parsed) mirror.
        Assert.Equal(RulesetSerializer.Serialize(vm.BuildRuleset()), vm.RawEditorText);
        Assert.DoesNotContain("// a comment", vm.RawEditorText);
    }

    // === 6. gutter =====================================================================

    /// <summary>
    /// <see cref="LineNumberGutterConverter.ToLineNumbers"/> maps text to a
    /// newline-joined <c>"1\n2\n…\nN"</c> where N = newline-count + 1: "" → "1",
    /// "a" → "1", "a\nb" → "1\n2", "a\nb\n" → "1\n2\n3" (a trailing newline opens a
    /// final blank line).
    /// </summary>
    [Theory]
    [InlineData("", "1")]
    [InlineData("a", "1")]
    [InlineData("a\nb", "1\n2")]
    [InlineData("a\nb\n", "1\n2\n3")]
    public void ToLineNumbers_MapsNewlineCountPlusOne(string input, string expected)
    {
        object? result = LineNumberGutterConverter.ToLineNumbers.Convert(
            input, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    // === 7. single write path (loader fixed point, not raw bytes) ======================

    /// <summary>
    /// A successful <c>ApplyRaw</c> routes through the SAME loader gate as every other
    /// apply: the applied ruleset is the loader's fixed point of the edit
    /// (<c>Load(Serialize(edit)).Ruleset</c>), NOT the un-validated raw bytes. Compared
    /// by serialized projection (the re-parse is idempotent under serialize), never
    /// record identity.
    /// </summary>
    [Fact]
    public void ApplyRaw_AppliedRulesetIsTheLoaderFixedPointOfTheEdit_NotTheRawBytes()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        var edited = RulesetLoader.LoadDefault() with
        {
            Name = "Loader fixed point",
            Author = "raw-editor",
        };
        string editText = RulesetSerializer.Serialize(edited);
        vm.RawEditorText = editText;

        var applied = new List<Ruleset>();
        vm.RulesetApplied += applied.Add;

        Assert.True(vm.ApplyRaw());

        var fired = Assert.Single(applied);
        // The loader fixed point of the SAME bytes.
        var loaderResult = RulesetLoader.Load(editText);
        Assert.True(loaderResult.Success);
        Assert.Equal(
            RulesetSerializer.Serialize(loaderResult.Ruleset),
            RulesetSerializer.Serialize(fired));
        // ... and the structured mirror equals that fixed point too.
        Assert.Equal(
            RulesetSerializer.Serialize(loaderResult.Ruleset),
            RulesetSerializer.Serialize(vm.BuildRuleset()));
    }

    // === helpers =======================================================================

    private static SettingsViewModel OpenClean(RulesetLocator locator) =>
        SettingsViewModel.Open(locator.LoadEffective(), locator);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-settings-raweditor-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a test over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
