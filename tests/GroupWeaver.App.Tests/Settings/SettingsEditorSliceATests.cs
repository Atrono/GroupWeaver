using System;
using System.IO;
using System.Linq;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the "Settings editor completeness — Slice A" surface of
/// <see cref="SettingsViewModel"/>: the <c>AddNaming</c>/<c>RemoveNaming</c> relay
/// commands (mirrors of the existing <c>AddIgnore</c>/<c>RemoveIgnore</c>), the
/// naming-rule round-trip through <see cref="SettingsViewModel.BuildRuleset"/>, the
/// single save/import/apply validation gate (<see cref="RulesetLoader.Load"/> over
/// <c>BuildRuleset()</c> → <see cref="RulesetSerializer.Serialize"/>) still guarding
/// an added rule, and the editable metadata header (Name/Description/Author bound to
/// <c>Metadata</c>). Same temp-dir <see cref="RulesetLocator"/> seam +
/// <c>OpenClean</c> + gate-fail idiom as <see cref="SettingsValidationTests"/>; pure
/// VM logic, no headless UI, no dispatcher.
///
/// <para><b>Save-gate truth (verified against the live loader, documented here so
/// future edits do not re-assert a false premise).</b> The gate is
/// <see cref="RulesetLoader.Load"/> over <c>Serialize(BuildRuleset())</c>, and the
/// serializer omits ONLY nulls (<c>WhenWritingNull</c>) while the loader's field
/// checks are <c>is null</c>, never empty-string. A defaulted added rule therefore
/// serializes its id/pattern as <c>""</c> (non-null) — the loader's Missing checks do
/// not fire, and an EMPTY regex compiles — so an added-but-untouched rule does NOT by
/// itself fail the gate. What DOES fail the gate for an added rule is genuinely
/// invalid content the loader rejects: an uncompilable regex pattern
/// (<c>$.naming[n].pattern</c>) or a duplicate id (<c>$.naming[n].id</c>). These
/// tests pin that real behavior — the gate blocks a bad added rule and lets a valid
/// one through — rather than the (untrue) "empty id/pattern is refused" premise.
/// Likewise an empty <c>Metadata.Name</c> serializes as <c>"name": ""</c> (non-null),
/// which the loader accepts — so the metadata arm pins the true accept/reject shape:
/// a NULL name (serialized to absent, the loader's actual reject path
/// <c>$.name</c>) fails the gate, and a non-empty name round-trips clean.</para>
/// </summary>
public sealed class SettingsEditorSliceATests
{
    // === AddNaming / RemoveNaming relay commands ========================================

    /// <summary>
    /// <c>AddNamingCommand.Execute(null)</c> appends exactly one
    /// <see cref="NamingRuleEditor"/> carrying the documented Slice-A defaults —
    /// Kind=User, Severity=Error, Enabled=true, empty Id + empty Pattern — to the
    /// end of <see cref="SettingsViewModel.Naming"/> (mirrors <c>AddIgnore</c>).
    /// </summary>
    [Fact]
    public void AddNamingCommand_AppendsOneRule_WithDocumentedDefaults()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        int before = vm.Naming.Count;

        vm.AddNamingCommand.Execute(null);

        Assert.Equal(before + 1, vm.Naming.Count);
        var added = vm.Naming[^1];
        Assert.Equal(AdObjectKind.User, added.Kind);
        Assert.Equal(RuleSeverity.Error, added.Severity);
        Assert.True(added.Enabled);
        Assert.Equal(string.Empty, added.Id);
        Assert.Equal(string.Empty, added.Pattern);
    }

    /// <summary>
    /// <c>RemoveNamingCommand.Execute(rule)</c> removes that exact instance from
    /// <see cref="SettingsViewModel.Naming"/> — the added rule is gone and the list
    /// returns to its prior length (mirrors <c>RemoveIgnore</c>).
    /// </summary>
    [Fact]
    public void RemoveNamingCommand_RemovesThatInstance()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        int before = vm.Naming.Count;
        vm.AddNamingCommand.Execute(null);
        var added = vm.Naming[^1];

        vm.RemoveNamingCommand.Execute(added);

        Assert.Equal(before, vm.Naming.Count);
        Assert.DoesNotContain(added, vm.Naming);
    }

    /// <summary>
    /// Multiple adds accumulate distinct rule instances — each
    /// <c>AddNamingCommand.Execute(null)</c> appends a fresh editor, not a shared
    /// singleton.
    /// </summary>
    [Fact]
    public void AddNamingCommand_MultipleAdds_Accumulate_AsDistinctInstances()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        int before = vm.Naming.Count;

        vm.AddNamingCommand.Execute(null);
        vm.AddNamingCommand.Execute(null);
        vm.AddNamingCommand.Execute(null);

        Assert.Equal(before + 3, vm.Naming.Count);
        var addedThree = vm.Naming.Skip(before).ToList();
        Assert.Equal(3, addedThree.Distinct().Count());
    }

    /// <summary>
    /// Removing a rule that is not in the list is a no-op — the
    /// <see cref="System.Collections.ObjectModel.ObservableCollection{T}.Remove"/>
    /// under <c>RemoveNaming</c> simply returns false, leaving the list unchanged
    /// (mirrors <c>RemoveIgnore</c> of a non-member).
    /// </summary>
    [Fact]
    public void RemoveNamingCommand_NonMember_IsNoOp()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        int before = vm.Naming.Count;
        var stranger = new NamingRuleEditor { Id = "not-in-the-list" };

        vm.RemoveNamingCommand.Execute(stranger);

        Assert.Equal(before, vm.Naming.Count);
        Assert.DoesNotContain(stranger, vm.Naming);
    }

    // === BuildRuleset round-trip for an added rule ======================================

    /// <summary>
    /// An added-then-filled naming rule flows through <c>BuildRuleset()</c> with its
    /// exact edited values (id/pattern/kind/severity/enabled) — the projection is
    /// <c>Naming.Select(r => r.Build())</c>, so the last built rule equals what the
    /// editor holds.
    /// </summary>
    [Fact]
    public void BuildRuleset_IncludesAddedRule_WithItsEditedValues()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        int before = vm.Naming.Count;
        vm.AddNamingCommand.Execute(null);
        var added = vm.Naming[^1];
        added.Id = "svc-account-name";
        added.Pattern = "^svc-[a-z0-9]+$";
        added.Kind = AdObjectKind.Computer;
        added.Severity = RuleSeverity.Warning;

        var built = vm.BuildRuleset();

        Assert.Equal(before + 1, built.Naming.Count);
        var builtAdded = built.Naming[^1];
        Assert.Equal("svc-account-name", builtAdded.Id);
        Assert.Equal("^svc-[a-z0-9]+$", builtAdded.Pattern);
        Assert.Equal(AdObjectKind.Computer, builtAdded.Kind);
        Assert.Equal(RuleSeverity.Warning, builtAdded.Severity);
        Assert.True(builtAdded.Enabled);
    }

    /// <summary>
    /// A rule that was added then removed is absent from <c>BuildRuleset()</c> — the
    /// projection reflects the live <see cref="SettingsViewModel.Naming"/> list, so a
    /// removed rule leaves no residue in the built ruleset.
    /// </summary>
    [Fact]
    public void BuildRuleset_OmitsAddedThenRemovedRule()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.AddNamingCommand.Execute(null);
        var added = vm.Naming[^1];
        added.Id = "will-be-removed";
        added.Pattern = "^x$";

        vm.RemoveNamingCommand.Execute(added);
        var built = vm.BuildRuleset();

        Assert.DoesNotContain(built.Naming, r => r.Id == "will-be-removed");
    }

    // === Save gate still guards the added rule ==========================================

    /// <summary>
    /// An added rule with an UNCOMPILABLE regex pattern makes <c>Save()</c> fail: the
    /// gate is <see cref="RulesetLoader.Load"/>, which validate-compiles every
    /// pattern, so a bad regex surfaces the loader's own <c>$.naming[n].pattern</c>
    /// error and NOTHING is written to disk. (This is the real gate-block for an added
    /// rule — an empty pattern compiles and would NOT be refused; see the class
    /// remarks.) The added rule's index is captured from the pre-add count, so the
    /// error path is asserted against the actual position rather than a hard-coded one.
    /// </summary>
    [Fact]
    public void Save_AddedRule_InvalidPattern_IsRefused_NothingWritten()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        int addedIndex = vm.Naming.Count;
        vm.AddNamingCommand.Execute(null);
        var added = vm.Naming[^1];
        added.Id = "unique-added-rule";
        added.Pattern = "([A-Z"; // unterminated group — invalid regex

        bool saved = vm.Save();

        Assert.False(saved);
        Assert.Contains(vm.ValidationErrors, e => e.Path == $"$.naming[{addedIndex}].pattern");
        // A refused save never even materializes the default — the temp base dir is empty.
        Assert.False(File.Exists(locator.UserRulesetPath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// An added rule that DUPLICATES an existing rule's id (case-insensitively) also
    /// fails the gate — the loader's uniqueness rule reports <c>$.naming[n].id</c> and
    /// nothing is written. Pins that id validity of the ADDED rule is enforced by the
    /// single gate, not a parallel validator.
    /// </summary>
    [Fact]
    public void Save_AddedRule_DuplicateId_IsRefused_NothingWritten()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        Assert.NotEmpty(vm.Naming);
        int addedIndex = vm.Naming.Count;
        vm.AddNamingCommand.Execute(null);
        var added = vm.Naming[^1];
        added.Id = vm.Naming[0].Id; // duplicate of an existing rule's id
        added.Pattern = "^valid$";

        bool saved = vm.Save();

        Assert.False(saved);
        Assert.Contains(vm.ValidationErrors, e => e.Path == $"$.naming[{addedIndex}].id");
        Assert.False(File.Exists(locator.UserRulesetPath));
    }

    /// <summary>
    /// Filling the added rule with a unique id + a valid pattern lets <c>Save()</c>
    /// succeed: the gate passes, errors clear, the user file is written, and reloading
    /// it round-trips the added rule (the saved file is provably reloadable — it passed
    /// <c>Load</c>).
    /// </summary>
    [Fact]
    public void Save_AddedRule_ValidIdAndPattern_Succeeds_AndReloadsWithTheRule()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.AddNamingCommand.Execute(null);
        var added = vm.Naming[^1];
        added.Id = "added-slice-a-rule";
        added.Pattern = "^GG_[A-Za-z0-9]+$";

        bool saved = vm.Save();

        Assert.True(saved);
        Assert.Empty(vm.ValidationErrors);
        Assert.True(File.Exists(locator.UserRulesetPath));

        var reloaded = locator.LoadEffective();
        Assert.True(reloaded.FromUserFile);
        Assert.Empty(reloaded.Errors);
        var reloadedRule = Assert.Single(reloaded.Ruleset.Naming, r => r.Id == "added-slice-a-rule");
        Assert.Equal("^GG_[A-Za-z0-9]+$", reloadedRule.Pattern);
        Assert.Equal(AdObjectKind.User, reloadedRule.Kind);
        Assert.Equal(RuleSeverity.Error, reloadedRule.Severity);
    }

    // === Metadata round-trip + gate ====================================================

    /// <summary>
    /// <c>Metadata.Name</c>/<c>Description</c>/<c>Author</c> are carried verbatim into
    /// <c>BuildRuleset()</c> — the editable metadata header binds straight through to
    /// the built ruleset's top-level fields.
    /// </summary>
    [Fact]
    public void BuildRuleset_CarriesMetadata_NameDescriptionAuthor()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Name = "Team AGDLP conventions";
        vm.Metadata.Description = "Naming + nesting for the lab OU.";
        vm.Metadata.Author = "SettingsEditorSliceATests";

        var built = vm.BuildRuleset();

        Assert.Equal("Team AGDLP conventions", built.Name);
        Assert.Equal("Naming + nesting for the lab OU.", built.Description);
        Assert.Equal("SettingsEditorSliceATests", built.Author);
    }

    /// <summary>
    /// A NULL <c>Metadata.Name</c> fails the Save gate: the serializer omits the null
    /// (<c>WhenWritingNull</c>) so the loader sees no <c>name</c> and reports its
    /// Missing error at <c>$.name</c>, refuses the save, and writes nothing. (Note: an
    /// EMPTY <c>""</c> name serializes as a present-but-empty string, which the loader
    /// accepts — the loader's reject is <c>is null</c>, not emptiness; the metadata
    /// header's required-name guarantee is thus the null/absent path pinned here.)
    /// </summary>
    [Fact]
    public void Save_NullMetadataName_IsRefused_ErrorAtNamePath_NothingWritten()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Name = null!;

        bool saved = vm.Save();

        Assert.False(saved);
        Assert.Contains(vm.ValidationErrors, e => e.Path == "$.name");
        Assert.False(File.Exists(locator.UserRulesetPath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A non-empty name plus an otherwise-valid mirror saves and round-trips
    /// Name/Description/Author from disk — the metadata header's edits reach the user
    /// file and reload clean (<c>FromUserFile==true</c>, no errors).
    /// </summary>
    [Fact]
    public void Save_ValidMetadata_RoundTripsNameDescriptionAuthor_FromDisk()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Name = "Lab ruleset";
        vm.Metadata.Description = "Round-tripped through disk.";
        vm.Metadata.Author = "SettingsEditorSliceATests";

        Assert.True(vm.Save());

        var reloaded = locator.LoadEffective();
        Assert.True(reloaded.FromUserFile);
        Assert.Empty(reloaded.Errors);
        Assert.Equal("Lab ruleset", reloaded.Ruleset.Name);
        Assert.Equal("Round-tripped through disk.", reloaded.Ruleset.Description);
        Assert.Equal("SettingsEditorSliceATests", reloaded.Ruleset.Author);
    }

    // === helpers ========================================================================

    /// <summary>Opens the editor on a CLEAN effective (no user file → embedded
    /// default, no errors) against the temp-dir locator seam — the
    /// <see cref="SettingsValidationTests"/> idiom.</summary>
    private static SettingsViewModel OpenClean(RulesetLocator locator) =>
        SettingsViewModel.Open(locator.LoadEffective(), locator);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-settings-editor-slice-a-tests-").FullName;

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
