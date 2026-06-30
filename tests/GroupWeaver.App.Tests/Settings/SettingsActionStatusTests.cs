using System;
using System.IO;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the Slice B (UX polish) Apply/Save success-feedback contract on
/// <see cref="SettingsViewModel.LastActionStatus"/> — the success twin of the
/// <see cref="SettingsViewModel.ValidationErrors"/> band. Mirrors the
/// <see cref="SettingsValidationTests"/>/<see cref="SettingsRawEditorTests"/> harness: pure
/// VM logic against a <c>RulesetLocator(baseDir)</c> temp-dir seam (#124) — no headless UI,
/// no dispatcher, no write outside the temp dir.
///
/// <para>The pins (each an <c>[ObservableProperty]</c> on the gate-clean path):</para>
/// <list type="bullet">
/// <item><b>Apply, valid ⇒ "Applied to the open workspace."</b> — the live re-thread succeeded.</item>
/// <item><b>Save, valid ⇒ "Saved to ruleset.jsonc."</b> — the atomic persist succeeded.</item>
/// <item><b>Refused gate ⇒ empty.</b> An INVALID mirror (a duplicate naming id) makes Apply/Save
/// return false and leave <c>LastActionStatus</c> EMPTY — never a false "Applied"/"Saved" beside
/// the validation errors. Both clear it at the top before the gate, so a refusal cannot leave a
/// stale prior success line either.</item>
/// <item><b>A raw-editor keystroke clears a prior success</b> (<c>OnRawEditorTextChanged</c>) —
/// the one structured-VM edit seam; a new edit must invalidate a stale "Applied"/"Saved".</item>
/// </list>
/// </summary>
public sealed class SettingsActionStatusTests
{
    private const string AppliedStatus = "Applied to the open workspace.";
    private const string SavedStatus = "Saved to ruleset.jsonc.";

    // === default: empty before any action ==============================================

    /// <summary>A freshly-opened editor carries NO success line — empty by default until
    /// a gate-clean Apply/Save sets it.</summary>
    [Fact]
    public void Open_LastActionStatus_IsEmptyByDefault()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);

        Assert.Equal(string.Empty, vm.LastActionStatus);
    }

    // === Apply (valid) ⇒ the Applied line ==============================================

    /// <summary>
    /// <see cref="SettingsViewModel.Apply"/> on a valid mirror succeeds and sets the
    /// "Applied to the open workspace." line (live re-thread, no disk write).
    /// </summary>
    [Fact]
    public void Apply_ValidMirror_SetsTheAppliedStatus()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);
        vm.Metadata.Author = "SettingsActionStatusTests";

        Assert.True(vm.Apply());

        Assert.Equal(AppliedStatus, vm.LastActionStatus);
    }

    // === Save (valid) ⇒ the Saved line =================================================

    /// <summary>
    /// <see cref="SettingsViewModel.Save"/> on a valid mirror succeeds and sets the
    /// "Saved to ruleset.jsonc." line (atomic persist).
    /// </summary>
    [Fact]
    public void Save_ValidMirror_SetsTheSavedStatus()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);
        vm.Metadata.Author = "SettingsActionStatusTests";

        Assert.True(vm.Save());

        Assert.Equal(SavedStatus, vm.LastActionStatus);
    }

    // === refused gate ⇒ empty (never a false "Applied"/"Saved") ========================

    /// <summary>
    /// An INVALID mirror (duplicate naming id) makes <see cref="SettingsViewModel.Apply"/>
    /// return false and leave <c>LastActionStatus</c> EMPTY — the refused gate must never
    /// claim a phantom "Applied" beside the surfaced validation errors.
    /// </summary>
    [Fact]
    public void Apply_InvalidMirror_LeavesLastActionStatusEmpty()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);
        DuplicateFirstNamingId(vm);

        Assert.False(vm.Apply());

        Assert.Equal(string.Empty, vm.LastActionStatus);
        Assert.NotEmpty(vm.ValidationErrors);
    }

    /// <summary>
    /// As above for <see cref="SettingsViewModel.Save"/>: an invalid mirror is refused and
    /// <c>LastActionStatus</c> stays empty (no false "Saved").
    /// </summary>
    [Fact]
    public void Save_InvalidMirror_LeavesLastActionStatusEmpty()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);
        DuplicateFirstNamingId(vm);

        Assert.False(vm.Save());

        Assert.Equal(string.Empty, vm.LastActionStatus);
        Assert.NotEmpty(vm.ValidationErrors);
    }

    /// <summary>
    /// Both actions clear the line at the TOP before the gate: a prior success followed by a
    /// REFUSED action must not leave the stale "Applied"/"Saved" lingering — it is wiped even
    /// though the second action wrote nothing new.
    /// </summary>
    [Fact]
    public void RefusedActionAfterASuccess_ClearsTheStaleSuccessLine()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);
        vm.Metadata.Author = "SettingsActionStatusTests";
        Assert.True(vm.Apply());
        Assert.Equal(AppliedStatus, vm.LastActionStatus);

        // Now break the mirror and Apply again: refused ⇒ the stale success is cleared.
        DuplicateFirstNamingId(vm);
        Assert.False(vm.Apply());

        Assert.Equal(string.Empty, vm.LastActionStatus);
    }

    // === a raw-editor keystroke clears a prior success =================================

    /// <summary>
    /// After a gate-clean Apply, a change to <see cref="SettingsViewModel.RawEditorText"/>
    /// (a user keystroke on the Advanced tab) clears <c>LastActionStatus</c> via the
    /// <c>OnRawEditorTextChanged</c> seam — a new edit invalidates the stale success line so
    /// it never lingers over now-superseded structured/raw state.
    /// </summary>
    [Fact]
    public void RawEditorTextChange_AfterASuccess_ClearsTheStatus()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);
        vm.Metadata.Author = "SettingsActionStatusTests";
        Assert.True(vm.Apply());
        Assert.Equal(AppliedStatus, vm.LastActionStatus);

        // A keystroke on the raw editor (any new value) clears the success line.
        vm.RawEditorText = vm.RawEditorText + "\n// a stray comment the user typed";

        Assert.Equal(string.Empty, vm.LastActionStatus);
    }

    /// <summary>Same seam after a Save: the Saved line clears on the next raw-editor edit.</summary>
    [Fact]
    public void RawEditorTextChange_AfterASave_ClearsTheStatus()
    {
        using var dir = new TempDir();
        var vm = OpenClean(dir);
        vm.Metadata.Author = "SettingsActionStatusTests";
        Assert.True(vm.Save());
        Assert.Equal(SavedStatus, vm.LastActionStatus);

        vm.RawEditorText = vm.RawEditorText + "\n// another stray comment";

        Assert.Equal(string.Empty, vm.LastActionStatus);
    }

    // === helpers =======================================================================

    /// <summary>Opens the editor on a CLEAN effective (no user file → embedded default, no
    /// errors) against the temp-dir locator seam.</summary>
    private static SettingsViewModel OpenClean(TempDir dir)
    {
        var locator = new RulesetLocator(dir.Path);
        return SettingsViewModel.Open(locator.LoadEffective(), locator);
    }

    /// <summary>Forces a duplicate naming id so the gate rejects (the default ruleset ships
    /// >= 2 naming rules) — the same lever <see cref="SettingsValidationTests"/> uses.</summary>
    private static void DuplicateFirstNamingId(SettingsViewModel vm)
    {
        Assert.True(vm.Naming.Count >= 2, "the default ruleset must ship at least two naming rules");
        vm.Naming[1].Id = vm.Naming[0].Id;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("groupweaver-settings-actionstatus-tests-").FullName;

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
