using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Rules;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Root of the AP 3.3 editable mirror tree (ADR-011 §2): the immutable
/// <see cref="Ruleset"/> records (<c>required</c>/<c>init</c>, un-bindable) are
/// mirrored here into bindable <see cref="ObservableObject"/> editors via
/// <see cref="LoadFrom"/>, edited, then projected back to an immutable
/// <see cref="Ruleset"/> via <see cref="BuildRuleset"/>.
///
/// <para><b>The single save/import/apply validation gate is
/// <see cref="RulesetLoader.Load"/></b> (ADR-011 §2) — never a parallel
/// hand-rolled validator. Every gated action runs
/// <c>BuildRuleset()</c> → <see cref="RulesetSerializer.Serialize"/> →
/// <see cref="RulesetLoader.Load"/>; on <see cref="RulesetLoadResult.Success"/> it
/// persists/applies the loader-RE-PARSED <see cref="RulesetLoadResult.Ruleset"/>
/// (a known-good fixed point), otherwise it surfaces
/// <see cref="RulesetLoadResult.Errors"/> in <see cref="ValidationErrors"/> and
/// writes nothing.</para>
///
/// <para><b>The byte fixed point</b> (the safety contract, pinned in S2):
/// <c>Serialize(BuildRuleset(LoadFrom(LoadDefault())))</c> is byte-equal to
/// <c>Serialize(LoadDefault())</c>. The matrix mirror preserves source-cell
/// PRESENCE and canonical key order; the <c>deny</c>(false,null) vs
/// <c>error</c>(false,Error) token distinction survives; dn/name XOR survives;
/// endpoints survive only on nesting exceptions; circular/empty <c>RuleId</c>s are
/// reconstructed from <see cref="RuleIds"/>; <see cref="Ruleset.SchemaVersion"/>
/// stays 1.</para>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly RulesetLocator _locator;
    private IRulesetFileDialogs _dialogs;

    private SettingsViewModel(RulesetLocator locator, IRulesetFileDialogs dialogs)
    {
        _locator = locator;
        _dialogs = dialogs;

        // The section editors are non-null by construction; every entry point
        // (Open/LoadFrom/Import/Reset) replaces them via Seed before use.
        var seed = RulesetLoader.LoadDefault();
        Metadata = new MetadataEditor();
        Nesting = NestingEditor.LoadFrom(seed.Nesting);
        Circular = SimpleRuleEditor.LoadFrom(seed.Circular);
        EmptyGroup = SimpleRuleEditor.LoadFrom(seed.EmptyGroup);
    }

    /// <summary>Name/Description/Author.</summary>
    public MetadataEditor Metadata { get; private set; }

    /// <summary>The nesting matrix editor (rule-level flags + 3×6 cell grid).</summary>
    public NestingEditor Nesting { get; private set; }

    /// <summary>Naming rules in file order (a flat list keyed on id).</summary>
    public ObservableCollection<NamingRuleEditor> Naming { get; } = [];

    /// <summary>The circular-membership rule.</summary>
    public SimpleRuleEditor Circular { get; private set; }

    /// <summary>The empty-group rule.</summary>
    public SimpleRuleEditor EmptyGroup { get; private set; }

    /// <summary>The global ignore list (endpoint hidden).</summary>
    public ObservableCollection<MatchEntryEditor> Ignore { get; } = [];

    /// <summary>The Rules-tab master grid: nesting, each naming rule (file order),
    /// circular, empty-group — each a 2-way handle into the section editors.</summary>
    public ObservableCollection<RuleRowEditor> Rules { get; } = [];

    /// <summary>The validation-panel errors: the loader's path-addressed findings
    /// from the most recent gate (failed Save/Export/Import) or, on open, the
    /// rejected-user-file errors the locator carried (AP 3.4 → finally surfaced).</summary>
    public ObservableCollection<RulesetValidationError> ValidationErrors { get; } = [];

    /// <summary>True when the editor opened on a rejected user file: the app is
    /// running on the embedded default, the mirror is seeded from it, and the
    /// broken on-disk file was left byte-unchanged (ADR-011 open-risk #2).</summary>
    [ObservableProperty]
    private bool _runningOnDefaultBecauseInvalid;

    /// <summary>Raised once on a successful Save/Apply with the gate-re-parsed
    /// ruleset — the shell re-threads it into the live workspace (Apply = live;
    /// Save = live + persist). A refused gate never raises it.</summary>
    public event Action<Ruleset>? RulesetApplied;

    /// <summary>Opens the editor on the ruleset the app is currently running on
    /// (<paramref name="effective"/>). On a CLEAN effective the mirror is seeded
    /// from it. On a REJECTED user file (<see cref="EffectiveRuleset.Errors"/>
    /// non-empty, <see cref="EffectiveRuleset.FromUserFile"/> false) the mirror is
    /// seeded from <see cref="RulesetLoader.LoadDefault"/> (what the app runs on),
    /// <see cref="RunningOnDefaultBecauseInvalid"/> is set, the errors are surfaced,
    /// and the broken file is NEVER auto-rewritten.</summary>
    public static SettingsViewModel Open(EffectiveRuleset effective, RulesetLocator locator) =>
        Open(effective, locator, new NullRulesetFileDialogs());

    /// <inheritdoc cref="Open(EffectiveRuleset, RulesetLocator)"/>
    public static SettingsViewModel Open(
        EffectiveRuleset effective, RulesetLocator locator, IRulesetFileDialogs dialogs)
    {
        var vm = new SettingsViewModel(locator, dialogs);
        if (effective.Errors.Count == 0)
        {
            vm.Seed(effective.Ruleset);
        }
        else
        {
            vm.Seed(RulesetLoader.LoadDefault());
            vm.RunningOnDefaultBecauseInvalid = true;
            foreach (var error in effective.Errors)
            {
                vm.ValidationErrors.Add(error);
            }
        }

        return vm;
    }

    /// <summary>Installs the real file-picker seam (AP 3.3 / S7): the production
    /// <see cref="SettingsWindow"/> calls this from its own <c>TopLevel</c>
    /// (<c>StorageProviderRulesetFileDialogs</c>) once it is attached, so the
    /// Import/Export commands reach the OS picker. Headless tests leave the
    /// null/fake seam in place. Idempotent — the last writer wins.</summary>
    public void UseFileDialogs(IRulesetFileDialogs dialogs) => _dialogs = dialogs;

    /// <summary>Mirrors <paramref name="ruleset"/> into a fresh editable tree.</summary>
    public static SettingsViewModel LoadFrom(Ruleset ruleset)
    {
        var vm = new SettingsViewModel(new RulesetLocator(), new NullRulesetFileDialogs());
        vm.Seed(ruleset);
        return vm;
    }

    /// <summary>Projects the mirror tree back to an immutable <see cref="Ruleset"/>.
    /// <see cref="Ruleset.SchemaVersion"/> is pinned to 1; the matrix is emitted
    /// sparse (present cells only); circular/empty <c>RuleId</c>s come from
    /// <see cref="RuleIds"/>. The result is what the save/import/apply gate re-parses.</summary>
    public Ruleset BuildRuleset() => new()
    {
        SchemaVersion = 1,
        Name = Metadata.Name,
        Description = Metadata.Description,
        Author = Metadata.Author,
        Nesting = Nesting.Build(),
        Naming = Naming.Select(r => r.Build()).ToList(),
        Circular = Circular.Build(),
        EmptyGroup = EmptyGroup.Build(),
        Ignore = Ignore.Select(e => e.Build()).ToList(),
    };

    /// <summary>Runs the single validation gate over the current mirror without any
    /// side effect: <c>BuildRuleset()</c> → <see cref="RulesetSerializer.Serialize"/>
    /// → <see cref="RulesetLoader.Load"/>. On success <see cref="ValidationErrors"/>
    /// is cleared; on failure it carries the loader's verbatim errors. Returns true
    /// when the mirror is gate-clean.</summary>
    public bool Validate() => RunGate(out _);

    /// <summary>Atomically persists the mirror IF it passes the gate. On success the
    /// gate-re-parsed ruleset is written to <see cref="RulesetLocator.UserRulesetPath"/>
    /// (atomic temp+move via <see cref="RulesetSerializer.Save"/>), errors and the
    /// invalid-file banner clear, and <see cref="RulesetApplied"/> fires once. On
    /// failure nothing is written and the errors surface. Returns true on success.</summary>
    public bool Save()
    {
        if (!RunGate(out var reparsed))
        {
            return false;
        }

        // First Save materializes the user ruleset; the %APPDATA%\GroupWeaver
        // directory may not exist yet (the locator never creates it on read).
        Directory.CreateDirectory(Path.GetDirectoryName(_locator.UserRulesetPath)!);
        RulesetSerializer.Save(reparsed, _locator.UserRulesetPath);
        RunningOnDefaultBecauseInvalid = false;
        RulesetApplied?.Invoke(reparsed);
        return true;
    }

    /// <summary>Re-threads the live workspace WITHOUT persisting (AP 3.3 / ADR-011 §3):
    /// runs the single gate and, on success, fires <see cref="RulesetApplied"/> with the
    /// gate-re-parsed ruleset — Apply differs from <see cref="Save"/> only in that it never
    /// writes the user file. On a refused gate nothing fires and the errors surface.
    /// Returns true on success.</summary>
    public bool Apply()
    {
        if (!RunGate(out var reparsed))
        {
            return false;
        }

        RulesetApplied?.Invoke(reparsed);
        return true;
    }

    /// <summary>The footer Apply button (AP 3.3 / S8): live re-thread, no disk write.</summary>
    [RelayCommand]
    private void ApplyAction() => Apply();

    /// <summary>The footer Save button (AP 3.3 / S8): live re-thread + atomic persist.</summary>
    [RelayCommand]
    private void SaveAction() => Save();

    /// <summary>Replaces the WHOLE mirror tree from <paramref name="text"/> (ADR-008
    /// whole-file precedence — no merge) IF it loads cleanly: on success the mirror is
    /// re-seeded from the loader's ruleset, errors and the banner clear, and true is
    /// returned. On failure the current mirror is left UNTOUCHED and the loader's
    /// errors surface.</summary>
    public bool ImportFrom(string text)
    {
        var result = RulesetLoader.Load(text);
        if (!result.Success)
        {
            SurfaceErrors(result.Errors);
            return false;
        }

        Seed(result.Ruleset);
        RunningOnDefaultBecauseInvalid = false;
        return true;
    }

    /// <summary>Rebuilds the mirror from <see cref="RulesetLoader.LoadDefault"/>,
    /// discarding all edits. In-memory only — never writes the user file (ADR-008:
    /// the default is materialized only by an explicit Save).</summary>
    public void ResetToDefault()
    {
        Seed(RulesetLoader.LoadDefault());
        RunningOnDefaultBecauseInvalid = false;
    }

    /// <summary>The File-tab Reset button (AP 3.3 / S7): rebuilds the mirror from the
    /// embedded default via <see cref="ResetToDefault"/> — in-memory only, no disk write.</summary>
    [RelayCommand]
    private void Reset() => ResetToDefault();

    /// <summary>The Ignore-tab Add button (AP 3.3 / S7): appends a fresh, empty dn-mode
    /// global ignore entry (endpoint hidden) for the user to fill in. The gate
    /// re-checks dn/name validity on the next Save/Export.</summary>
    [RelayCommand]
    private void AddIgnore() =>
        Ignore.Add(new MatchEntryEditor { Mode = EntryMode.Dn });

    /// <summary>The Ignore-tab per-row Remove button (AP 3.3 / S7): drops
    /// <paramref name="entry"/> from the global ignore list.</summary>
    [RelayCommand]
    private void RemoveIgnore(MatchEntryEditor entry) => Ignore.Remove(entry);

    /// <summary>Import via the file-dialog seam: a picked file's text is fed to
    /// <see cref="ImportFrom"/> (whole-file replace + gate); a cancelled pick
    /// (null) is a no-op.</summary>
    [RelayCommand]
    private async Task ImportAsync(CancellationToken ct)
    {
        string? text = await _dialogs.PickOpenTextAsync(ct).ConfigureAwait(true);
        if (text is not null)
        {
            ImportFrom(text);
        }
    }

    /// <summary>Export via the file-dialog seam: runs the save gate; on success the
    /// gate-re-parsed ruleset is written to the picked path (NOT the user ruleset
    /// path — Export is not a Save). An invalid mirror is blocked and nothing is
    /// written; a cancelled pick (null) is a no-op.</summary>
    [RelayCommand]
    private async Task ExportAsync(CancellationToken ct)
    {
        if (!RunGate(out var reparsed))
        {
            return;
        }

        string? path = await _dialogs.PickSavePathAsync(ct).ConfigureAwait(true);
        if (path is not null)
        {
            RulesetSerializer.Save(reparsed, path);
        }
    }

    /// <summary>Runs the single validation gate. On success returns the loader's
    /// RE-PARSED ruleset (a known-good fixed point, never the un-validated mirror)
    /// and clears <see cref="ValidationErrors"/>; on failure surfaces the loader's
    /// errors and returns false with <paramref name="reparsed"/> null.</summary>
    private bool RunGate(out Ruleset reparsed)
    {
        var result = RulesetLoader.Load(RulesetSerializer.Serialize(BuildRuleset()));
        if (!result.Success)
        {
            SurfaceErrors(result.Errors);
            reparsed = null!;
            return false;
        }

        ValidationErrors.Clear();
        reparsed = result.Ruleset;
        return true;
    }

    private void SurfaceErrors(IReadOnlyList<RulesetValidationError> errors)
    {
        ValidationErrors.Clear();
        foreach (var error in errors)
        {
            ValidationErrors.Add(error);
        }
    }

    /// <summary>Re-seeds the whole mirror tree from <paramref name="ruleset"/>
    /// (whole-file replace — the Open/Import/Reset substrate) and clears errors.</summary>
    private void Seed(Ruleset ruleset)
    {
        Metadata = MetadataEditor.LoadFrom(ruleset);
        Nesting = NestingEditor.LoadFrom(ruleset.Nesting);
        Circular = SimpleRuleEditor.LoadFrom(ruleset.Circular);
        EmptyGroup = SimpleRuleEditor.LoadFrom(ruleset.EmptyGroup);
        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(Nesting));
        OnPropertyChanged(nameof(Circular));
        OnPropertyChanged(nameof(EmptyGroup));

        Naming.Clear();
        foreach (var rule in ruleset.Naming)
        {
            Naming.Add(NamingRuleEditor.LoadFrom(rule));
        }

        Ignore.Clear();
        foreach (var entry in ruleset.Ignore)
        {
            Ignore.Add(MatchEntryEditor.LoadFrom(entry, endpointEditable: false));
        }

        Rules.Clear();
        Rules.Add(RuleRowEditor.ForNesting(Nesting));
        foreach (var naming in Naming)
        {
            Rules.Add(RuleRowEditor.ForNaming(naming));
        }

        Rules.Add(RuleRowEditor.ForSimple(RuleIds.Circular, "Circular nesting", Circular));
        Rules.Add(RuleRowEditor.ForSimple(RuleIds.EmptyGroup, "Empty groups", EmptyGroup));

        ValidationErrors.Clear();
    }

    /// <summary>The headless default for the file-dialog seam when no real picker is
    /// wired (e.g. the <see cref="LoadFrom"/> / two-arg <see cref="Open"/> paths): a
    /// cancelled picker, so the Import/Export commands are inert no-ops.</summary>
    private sealed class NullRulesetFileDialogs : IRulesetFileDialogs
    {
        public Task<string?> PickOpenTextAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSavePathAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }
}
