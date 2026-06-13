using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the AP 3.3 / S3 validation-gate + import/export/reset/save + invalid-on-open
/// contract (ADR-011 §2/§4, the spec's "Final editor design" → File tab + validation
/// panel + invalid-user-file-on-open). The mirror is edited, then the SINGLE
/// save/import/apply validation gate — <see cref="RulesetLoader.Load"/> over
/// <c>BuildRuleset()</c> → <c>RulesetSerializer.Serialize</c>, never a parallel
/// hand-rolled validator — decides whether the bytes ever reach disk.
///
/// <para>The pins (each an ADR-011 open-risk mitigation):</para>
/// <list type="bullet">
/// <item><b>Duplicate naming id ⇒ Save refused.</b> A mirror whose two naming rows
/// share an id (case-insensitively) fails the gate: <c>Save()</c> returns false,
/// <c>ValidationErrors</c> carries the loader's error at path <c>$.naming[1].id</c>,
/// and the user file is NEVER written (no torn/partial save of an invalid ruleset).</item>
/// <item><b>Invalid user file on open ⇒ run on default, file byte-unchanged
/// (open-risk #2).</b> Seeding the editor from an <see cref="EffectiveRuleset"/>
/// whose <c>Errors</c> is non-empty (<c>FromUserFile==false</c>) seeds the mirror
/// from <see cref="RulesetLoader.LoadDefault"/> (what the app runs on), surfaces
/// those exact errors, sets <c>RunningOnDefaultBecauseInvalid=true</c>, and — the
/// strong guarantee — leaves the broken on-disk file BYTE-IDENTICAL: never
/// auto-rewritten, never auto-replaced, recoverable by re-Import.</item>
/// <item><b>Valid edit ⇒ atomic Save ⇒ reloads clean.</b> A valid mirror Saves
/// atomically to <c>_locator.UserRulesetPath</c>; re-running
/// <c>LoadEffective()</c> yields <c>FromUserFile==true</c> with no errors and the
/// edit present (the saved file passed <c>Load</c>, so it is provably reloadable).</item>
/// <item><b>Import bad ⇒ mirror untouched + errors; Import good ⇒ whole mirror
/// replaced (no merge, ADR-008).</b></item>
/// <item><b>ResetToDefault ⇒ mirror rebuilt from <see cref="RulesetLoader.LoadDefault"/></b>
/// (in-memory only until an explicit Save).</item>
/// </list>
///
/// <para>RED until S3 extends <see cref="SettingsViewModel"/> with the
/// <c>Open(EffectiveRuleset, RulesetLocator)</c> seam factory, <c>ValidationErrors</c>,
/// <c>RunningOnDefaultBecauseInvalid</c>, <c>Validate()</c>, <c>Save()</c>,
/// <c>ImportFrom(string)</c>, <c>ResetToDefault()</c>, the <c>RulesetApplied</c>
/// event, and an <see cref="IRulesetFileDialogs"/> abstraction (faked here). Pure
/// VM logic against a <c>RulesetLocator(baseDir)</c> temp-dir seam — no headless UI,
/// no dispatcher.</para>
/// </summary>
public sealed class SettingsValidationTests
{
    // === duplicate-naming-id mirror ⇒ Save refused, error at $.naming[n].id, NOT written ===

    /// <summary>
    /// Two naming rows with the same id (the loader's case-insensitive uniqueness
    /// rule) make <c>BuildRuleset()</c> produce a ruleset the gate rejects: the
    /// second row's id is the duplicate, so the loader's error path is
    /// <c>$.naming[1].id</c>. <c>Save()</c> must return false and surface exactly
    /// that error — the single validation source, never a parallel validator.
    /// </summary>
    [Fact]
    public void Save_DuplicateNamingId_IsRefused_ErrorAtNamingIdPath()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        DuplicateFirstNamingId(vm);

        bool saved = vm.Save();

        Assert.False(saved);
        Assert.Contains(vm.ValidationErrors, e => e.Path == "$.naming[1].id");
    }

    /// <summary>
    /// The refused save must leave NO file on disk — an invalid ruleset never
    /// produces even a torn/partial write at <c>UserRulesetPath</c> (and the first
    /// save is the only thing that may ever materialize the default, so nothing is
    /// written at all here).
    /// </summary>
    [Fact]
    public void Save_DuplicateNamingId_FileIsNeverWritten()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        DuplicateFirstNamingId(vm);

        _ = vm.Save();

        Assert.False(File.Exists(locator.UserRulesetPath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The error surfaced for the rejected save is the loader's verbatim error for
    /// the SAME bytes — the gate is <see cref="RulesetLoader.Load"/> over the mirror's
    /// serialized form, not a re-implementation that might drift in path or message.
    /// </summary>
    [Fact]
    public void Save_DuplicateNamingId_SurfacesTheLoadersOwnError_VerbatimForTheSameBytes()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        DuplicateFirstNamingId(vm);

        _ = vm.Save();

        var expected = RulesetLoader.Load(RulesetSerializer.Serialize(vm.BuildRuleset())).Errors;
        Assert.Equal(expected, vm.ValidationErrors.ToList());
    }

    // === invalid user file on open ⇒ default mirror, errors surfaced, file byte-unchanged ===

    /// <summary>
    /// Opening settings on a rejected user file (the <see cref="EffectiveRuleset"/>
    /// the app is already running on: <c>Errors</c> non-empty,
    /// <c>FromUserFile==false</c>, <c>Ruleset</c> = the embedded default) seeds the
    /// mirror from <see cref="RulesetLoader.LoadDefault"/> — what the app runs on —
    /// and flags <c>RunningOnDefaultBecauseInvalid</c>.
    /// </summary>
    [Fact]
    public void Open_OnInvalidEffective_SeedsMirrorFromDefault_AndFlagsRunningOnDefault()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        WriteUserFile(locator, BrokenJsonc);

        var effective = locator.LoadEffective();
        Assert.False(effective.FromUserFile);
        Assert.NotEmpty(effective.Errors);

        var vm = SettingsViewModel.Open(effective, locator);

        Assert.True(vm.RunningOnDefaultBecauseInvalid);
        // The mirror IS the default (what the app runs on) — byte-equal on rebuild.
        Assert.Equal(
            RulesetSerializer.Serialize(RulesetLoader.LoadDefault()),
            RulesetSerializer.Serialize(vm.BuildRuleset()));
    }

    /// <summary>
    /// The rejected file's errors are surfaced into the validation panel exactly as
    /// the locator carried them (the AP 3.4 errors that were threaded but unsurfaced
    /// finally appear) — same list, same paths, same messages.
    /// </summary>
    [Fact]
    public void Open_OnInvalidEffective_SurfacesTheEffectiveErrors()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        WriteUserFile(locator, BrokenJsonc);
        var effective = locator.LoadEffective();

        var vm = SettingsViewModel.Open(effective, locator);

        Assert.Equal(effective.Errors, vm.ValidationErrors.ToList());
        Assert.Contains(vm.ValidationErrors, e => e.Path == "$.schemaVersion");
        Assert.Contains(vm.ValidationErrors, e => e.Path == "$.circular.severity");
    }

    /// <summary>
    /// THE strong guarantee (ADR-011 open-risk #2): opening settings on an invalid
    /// user file must NOT touch that file. The broken-but-recoverable bytes on disk
    /// are byte-identical after open — never auto-rewritten, never auto-replaced
    /// with the default. The user re-Imports the broken file to recover its content;
    /// only an explicit Save ever overwrites it.
    /// </summary>
    [Fact]
    public void Open_OnInvalidEffective_LeavesTheOnDiskFileByteUnchanged()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        WriteUserFile(locator, BrokenJsonc);
        var before = File.ReadAllBytes(locator.UserRulesetPath);

        _ = SettingsViewModel.Open(locator.LoadEffective(), locator);

        Assert.Equal(before, File.ReadAllBytes(locator.UserRulesetPath));
    }

    /// <summary>
    /// A clean open (valid user file, or no file at all → the embedded default with
    /// no errors) does NOT flag the invalid-file banner and surfaces no errors.
    /// </summary>
    [Fact]
    public void Open_OnCleanEffective_NoBanner_NoErrors()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);

        var vm = SettingsViewModel.Open(locator.LoadEffective(), locator);

        Assert.False(vm.RunningOnDefaultBecauseInvalid);
        Assert.Empty(vm.ValidationErrors);
    }

    // === valid edit ⇒ atomic Save ⇒ LoadEffective().FromUserFile==true, reloads clean ===

    /// <summary>
    /// A valid edit Saves successfully: <c>Save()</c> returns true and clears any
    /// prior errors.
    /// </summary>
    [Fact]
    public void Save_ValidEdit_Succeeds_AndClearsErrors()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Author = "SettingsValidationTests";

        bool saved = vm.Save();

        Assert.True(saved);
        Assert.Empty(vm.ValidationErrors);
    }

    /// <summary>
    /// After a valid Save the file exists at <c>UserRulesetPath</c> and reloading it
    /// via <see cref="RulesetLocator.LoadEffective"/> yields <c>FromUserFile==true</c>
    /// with no errors and the edit present — the saved file passed <c>Load</c>, so it
    /// is provably reloadable (the first Save also materializes the default into the
    /// base dir).
    /// </summary>
    [Fact]
    public void Save_ValidEdit_FileReloadsClean_FromUserFileTrue_WithTheEdit()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Author = "SettingsValidationTests";

        Assert.True(vm.Save());
        Assert.True(File.Exists(locator.UserRulesetPath));

        var reloaded = locator.LoadEffective();
        Assert.True(reloaded.FromUserFile);
        Assert.Empty(reloaded.Errors);
        Assert.Equal("SettingsValidationTests", reloaded.Ruleset.Author);
    }

    /// <summary>
    /// The saved bytes are exactly what the gate's re-parsed ruleset serializes to
    /// (header aside) — Save persists the loader-re-parsed <c>result.Ruleset</c>, not
    /// the un-validated mirror, so what lands on disk is a known-good fixed point.
    /// </summary>
    [Fact]
    public void Save_ValidEdit_PersistsTheGateReparsedRuleset()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Author = "SettingsValidationTests";

        Assert.True(vm.Save());

        var reloaded = locator.LoadEffective();
        Assert.Equal(
            RulesetSerializer.Serialize(RulesetLoader.Load(RulesetSerializer.Serialize(vm.BuildRuleset())).Ruleset!),
            RulesetSerializer.Serialize(reloaded.Ruleset));
    }

    /// <summary>
    /// A successful Save re-threads the live workspace: the <c>RulesetApplied</c>
    /// event fires once with the gate-re-parsed ruleset (Apply = live; Save = live +
    /// persist). A refused save never raises it.
    /// </summary>
    [Fact]
    public void Save_ValidEdit_RaisesRulesetApplied_Once()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Author = "SettingsValidationTests";

        var applied = new List<Ruleset>();
        vm.RulesetApplied += applied.Add;

        Assert.True(vm.Save());

        var ruleset = Assert.Single(applied);
        Assert.Equal("SettingsValidationTests", ruleset.Author);
    }

    [Fact]
    public void Save_DuplicateNamingId_DoesNotRaiseRulesetApplied()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        DuplicateFirstNamingId(vm);

        var applied = new List<Ruleset>();
        vm.RulesetApplied += applied.Add;

        _ = vm.Save();

        Assert.Empty(applied);
    }

    // === Import bad ⇒ mirror untouched + errors ; Import good ⇒ whole mirror replaced ====

    /// <summary>
    /// Importing semantically-invalid text leaves the current mirror UNTOUCHED and
    /// populates <c>ValidationErrors</c> with the loader's errors (no merge, no
    /// partial apply). The mirror's pre-import serialized form is unchanged.
    /// </summary>
    [Fact]
    public void ImportFrom_BadText_LeavesMirrorUntouched_SurfacesErrors()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Author = "before-import";
        var before = RulesetSerializer.Serialize(vm.BuildRuleset());

        bool imported = vm.ImportFrom(BrokenJsonc);

        Assert.False(imported);
        Assert.NotEmpty(vm.ValidationErrors);
        Assert.Equal(before, RulesetSerializer.Serialize(vm.BuildRuleset()));
        Assert.Equal("before-import", vm.Metadata.Author);
    }

    [Fact]
    public void ImportFrom_MalformedJson_LeavesMirrorUntouched_SurfacesOneRootError()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        var before = RulesetSerializer.Serialize(vm.BuildRuleset());

        bool imported = vm.ImportFrom("{ \"schemaVersion\": 1, \"name\": \"x\", ");

        Assert.False(imported);
        var error = Assert.Single(vm.ValidationErrors);
        Assert.Equal("$", error.Path);
        Assert.Equal(before, RulesetSerializer.Serialize(vm.BuildRuleset()));
    }

    /// <summary>
    /// Importing a VALID file replaces the WHOLE mirror tree from the imported
    /// ruleset (ADR-008 whole-file precedence — no merge with the prior mirror): the
    /// imported name/author win outright and any prior edit is gone. Errors clear.
    /// </summary>
    [Fact]
    public void ImportFrom_GoodText_ReplacesWholeMirror_NoMerge()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Author = "prior-edit-should-be-gone";

        var incoming = RulesetLoader.LoadDefault() with
        {
            Name = "Imported ruleset",
            Author = "the-importer",
            Naming = Array.Empty<NamingRule>(),
        };
        string incomingText = RulesetSerializer.Serialize(incoming);

        bool imported = vm.ImportFrom(incomingText);

        Assert.True(imported);
        Assert.Empty(vm.ValidationErrors);
        Assert.Equal("Imported ruleset", vm.Metadata.Name);
        Assert.Equal("the-importer", vm.Metadata.Author);
        // Whole-file replace: the imported file's empty naming list wins — the
        // default's naming rows are gone, not merged in.
        Assert.Empty(vm.Naming);
        Assert.Equal(
            RulesetSerializer.Serialize(incoming),
            RulesetSerializer.Serialize(vm.BuildRuleset()));
    }

    /// <summary>
    /// A good Import over an invalid-file-on-open state clears the
    /// <c>RunningOnDefaultBecauseInvalid</c> banner (the user recovered by importing
    /// a valid ruleset).
    /// </summary>
    [Fact]
    public void ImportFrom_GoodText_ClearsRunningOnDefaultBanner()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        WriteUserFile(locator, BrokenJsonc);
        var vm = SettingsViewModel.Open(locator.LoadEffective(), locator);
        Assert.True(vm.RunningOnDefaultBecauseInvalid);

        bool imported = vm.ImportFrom(RulesetSerializer.Serialize(RulesetLoader.LoadDefault()));

        Assert.True(imported);
        Assert.False(vm.RunningOnDefaultBecauseInvalid);
    }

    // === ResetToDefault ⇒ mirror rebuilt from LoadDefault ================================

    /// <summary>
    /// <c>ResetToDefault()</c> rebuilds the mirror from
    /// <see cref="RulesetLoader.LoadDefault"/> (in-memory only — no disk write): any
    /// edit is discarded, the rebuilt mirror is byte-equal to the default on
    /// serialize, errors clear, and the banner clears.
    /// </summary>
    [Fact]
    public void ResetToDefault_RebuildsMirrorFromDefault_DiscardsEdits()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);
        vm.Metadata.Author = "edit-to-discard";
        vm.Metadata.Name = "edit-to-discard";

        vm.ResetToDefault();

        Assert.Equal(
            RulesetSerializer.Serialize(RulesetLoader.LoadDefault()),
            RulesetSerializer.Serialize(vm.BuildRuleset()));
        Assert.Empty(vm.ValidationErrors);
        Assert.False(vm.RunningOnDefaultBecauseInvalid);
    }

    /// <summary>
    /// Reset is IN-MEMORY ONLY — it must not write the user file (ADR-008: the
    /// default is materialized only by an explicit Save, never by Reset).
    /// </summary>
    [Fact]
    public void ResetToDefault_DoesNotWriteTheUserFile()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator);

        vm.ResetToDefault();

        Assert.False(File.Exists(locator.UserRulesetPath));
    }

    // === Import / Export through the IRulesetFileDialogs seam ============================

    /// <summary>
    /// The Import COMMAND routes file selection through the
    /// <see cref="IRulesetFileDialogs"/> seam (the real picker is the headless-untestable
    /// <c>[I]</c> layer); a fake returns the chosen file's text and the VM applies the
    /// same whole-file-replace semantics as <c>ImportFrom</c>.
    /// </summary>
    [Fact]
    public async Task ImportCommand_GoodFile_ReplacesWholeMirror_ViaDialogSeam()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var incoming = RulesetLoader.LoadDefault() with { Name = "Picked import" };
        var dialogs = new FakeDialogs { OpenText = RulesetSerializer.Serialize(incoming) };
        var vm = OpenClean(locator, dialogs);

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal("Picked import", vm.Metadata.Name);
        Assert.Empty(vm.ValidationErrors);
    }

    /// <summary>
    /// A cancelled Import picker (the seam returns null) is a no-op: the mirror is
    /// untouched and no errors appear.
    /// </summary>
    [Fact]
    public async Task ImportCommand_Cancelled_IsNoOp()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        var vm = OpenClean(locator, new FakeDialogs { OpenText = null });
        vm.Metadata.Author = "kept";
        var before = RulesetSerializer.Serialize(vm.BuildRuleset());

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(before, RulesetSerializer.Serialize(vm.BuildRuleset()));
        Assert.Empty(vm.ValidationErrors);
    }

    /// <summary>
    /// The Export COMMAND runs the save gate, then writes the gate-re-parsed ruleset
    /// to the path the <see cref="IRulesetFileDialogs"/> seam returns — the exported
    /// file is itself reloadable (it passed <c>Load</c>). A valid mirror exports a
    /// file that round-trips clean.
    /// </summary>
    [Fact]
    public async Task ExportCommand_ValidMirror_WritesAReloadableFile_AtThePickedPath()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        string exportPath = Path.Combine(dir.Path, "exported.jsonc");
        var vm = OpenClean(locator, new FakeDialogs { SavePath = exportPath });
        vm.Metadata.Author = "exporter";

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(exportPath));
        var reloaded = RulesetLoader.Load(File.ReadAllText(exportPath));
        Assert.True(reloaded.Success);
        Assert.Equal("exporter", reloaded.Ruleset.Author);
        // Export NEVER touches the user ruleset path (it is not a Save).
        Assert.False(File.Exists(locator.UserRulesetPath));
    }

    /// <summary>
    /// Export of an INVALID mirror is blocked: the gate fails, errors surface, and
    /// nothing is written to the picked path (no invalid file ever leaves the editor).
    /// </summary>
    [Fact]
    public async Task ExportCommand_InvalidMirror_IsBlocked_NothingWritten()
    {
        using var dir = new TempDir();
        var locator = new RulesetLocator(dir.Path);
        string exportPath = Path.Combine(dir.Path, "exported.jsonc");
        var vm = OpenClean(locator, new FakeDialogs { SavePath = exportPath });
        DuplicateFirstNamingId(vm);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.ValidationErrors);
        Assert.False(File.Exists(exportPath));
    }

    // === helpers ========================================================================

    /// <summary>A parseable user file with THREE independent semantic defects (a
    /// future schema version, a missing name, an unknown severity token) — the same
    /// shape <see cref="RulesetLocatorTests"/> uses for the invalid-file arm.</summary>
    private const string BrokenJsonc =
        """
        {
          // deliberately broken user file
          "schemaVersion": 99,
          "nesting": {
            "enabled": true,
            "severity": "error",
            "unlisted": "deny",
            "matrix": { "GlobalGroup": { "User": "allow" } },
            "exceptions": []
          },
          "naming": [],
          "circular": { "enabled": true, "severity": "nuclear", "exceptions": [] },
          "emptyGroup": { "enabled": true, "severity": "info", "exceptions": [] },
          "ignore": []
        }
        """;

    /// <summary>Opens the editor on a CLEAN effective (no user file → embedded
    /// default, no errors) against the temp-dir locator seam.</summary>
    private static SettingsViewModel OpenClean(RulesetLocator locator) =>
        SettingsViewModel.Open(locator.LoadEffective(), locator);

    private static SettingsViewModel OpenClean(RulesetLocator locator, IRulesetFileDialogs dialogs) =>
        SettingsViewModel.Open(locator.LoadEffective(), locator, dialogs);

    /// <summary>Forces a duplicate naming id: copies the first row's id onto the
    /// second so the gate rejects with a <c>$.naming[1].id</c> error. The default
    /// ruleset ships ≥2 naming rules.</summary>
    private static void DuplicateFirstNamingId(SettingsViewModel vm)
    {
        Assert.True(vm.Naming.Count >= 2, "the default ruleset must ship at least two naming rules");
        vm.Naming[1].Id = vm.Naming[0].Id;
    }

    private static void WriteUserFile(RulesetLocator locator, string jsonc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(locator.UserRulesetPath)!);
        File.WriteAllText(locator.UserRulesetPath, jsonc);
    }

    /// <summary>Fake of the S3 <see cref="IRulesetFileDialogs"/> seam: the real
    /// picker (Avalonia <c>StorageProvider</c> via <c>TopLevel</c>) is the
    /// headless-untestable <c>[I]</c> layer; the VM's import/export LOGIC is driven
    /// here. <c>PickOpenTextAsync</c> returns the selected file's text (or null when
    /// cancelled); <c>PickSavePathAsync</c> returns the chosen path (or null).</summary>
    private sealed class FakeDialogs : IRulesetFileDialogs
    {
        public string? OpenText { get; init; }

        public string? SavePath { get; init; }

        public Task<string?> PickOpenTextAsync(CancellationToken ct = default) =>
            Task.FromResult(OpenText);

        public Task<string?> PickSavePathAsync(CancellationToken ct = default) =>
            Task.FromResult(SavePath);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-settings-validation-tests-").FullName;

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
