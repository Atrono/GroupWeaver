using System.Collections.Generic;

using GroupWeaver.App.Settings;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the "Settings editor completeness — Slice B" live glob-match preview surface of
/// <see cref="MatchEntryEditor"/>: the UI-only <see cref="MatchEntryEditor.PreviewCandidate"/>
/// scratch field, its computed <see cref="MatchEntryEditor.PreviewMatch"/>, the
/// <c>PropertyChanged</c> notifications that keep the "matches?" chip live, and the load-bearing
/// guarantee that the preview scratch NEVER leaks into <see cref="MatchEntryEditor.Build"/> /
/// the serialized ruleset.
///
/// <para><b>Match truth.</b> <see cref="MatchEntryEditor.PreviewMatch"/> delegates to
/// <see cref="GlobPreview.IsMatch"/> → <see cref="GlobMatcher.IsMatch(string,string)"/> — the
/// engine's own compiled/memoized matcher (full-string anchored, case-insensitive; the same
/// semantics <c>MatchEntryTests</c> pins). The glob here is <c>*,CN=Builtin,*</c> (a real ignore
/// glob from <c>MatchEntryTests.Matches_DnEntry_GlobsAgainstObjectDn</c>): it matches a
/// Builtin-container DN and rejects a Groups-OU DN. An EMPTY candidate is always no-match (the
/// chip is hidden) regardless of <see cref="MatchEntryEditor.Value"/>.</para>
///
/// <para><b>No headless UI, no dispatcher</b> — <see cref="MatchEntryEditor"/> is a plain
/// <c>ObservableObject</c> (CommunityToolkit.Mvvm), so its <c>PropertyChanged</c> and its
/// <c>Build()</c> projection are exercised directly, the same way <c>DetailPanelCopyDnTests</c>
/// asserts a computed-property notification.</para>
/// </summary>
public sealed class MatchEntryEditorPreviewTests
{
    /// <summary>A Builtin-container DN the <c>*,CN=Builtin,*</c> glob matches.</summary>
    private const string MatchingDn = "CN=Administrators,CN=Builtin,DC=weavedemo,DC=example";

    /// <summary>A Groups-OU DN the <c>*,CN=Builtin,*</c> glob does NOT match.</summary>
    private const string NonMatchingDn = "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>The real ignore glob under test.</summary>
    private const string BuiltinGlob = "*,CN=Builtin,*";

    // === PreviewMatch truth ============================================================

    /// <summary>
    /// An EMPTY <see cref="MatchEntryEditor.PreviewCandidate"/> is always no-match — even when
    /// <see cref="MatchEntryEditor.Value"/> is a match-everything glob — so the preview chip stays
    /// hidden until the user types a candidate. (The default candidate is <c>""</c>.)
    /// </summary>
    [Fact]
    public void PreviewMatch_EmptyCandidate_IsFalse_EvenWithMatchAllGlob()
    {
        var editor = new MatchEntryEditor { Mode = EntryMode.Dn, Value = "*" };

        Assert.Equal(string.Empty, editor.PreviewCandidate);
        Assert.False(editor.PreviewMatch);
    }

    /// <summary>
    /// A non-empty candidate that the <see cref="MatchEntryEditor.Value"/> glob matches makes
    /// <see cref="MatchEntryEditor.PreviewMatch"/> true — via <see cref="GlobPreview.IsMatch"/>,
    /// the engine's own matcher, never a parallel implementation.
    /// </summary>
    [Fact]
    public void PreviewMatch_MatchingCandidate_IsTrue()
    {
        var editor = new MatchEntryEditor
        {
            Mode = EntryMode.Dn,
            Value = BuiltinGlob,
            PreviewCandidate = MatchingDn,
        };

        Assert.True(editor.PreviewMatch);
    }

    /// <summary>
    /// A non-empty candidate the glob does NOT match makes <see cref="MatchEntryEditor.PreviewMatch"/>
    /// false — the anchored full-string glob rejects a DN that is not under <c>CN=Builtin</c>.
    /// </summary>
    [Fact]
    public void PreviewMatch_NonMatchingCandidate_IsFalse()
    {
        var editor = new MatchEntryEditor
        {
            Mode = EntryMode.Dn,
            Value = BuiltinGlob,
            PreviewCandidate = NonMatchingDn,
        };

        Assert.False(editor.PreviewMatch);
    }

    // === PropertyChanged for PreviewMatch ==============================================

    /// <summary>
    /// Setting <see cref="MatchEntryEditor.Value"/> raises <c>PropertyChanged</c> for
    /// <see cref="MatchEntryEditor.PreviewMatch"/> (the <c>OnValueChanged</c> partial) — so a
    /// glob edit re-evaluates the live chip. The transition here flips the match from false→true.
    /// </summary>
    [Fact]
    public void SettingValue_RaisesPropertyChanged_ForPreviewMatch()
    {
        var editor = new MatchEntryEditor
        {
            Mode = EntryMode.Dn,
            Value = "does-not-match-anything-*",
            PreviewCandidate = MatchingDn,
        };
        Assert.False(editor.PreviewMatch);

        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.Value = BuiltinGlob;

        Assert.Contains(nameof(MatchEntryEditor.PreviewMatch), changed);
        Assert.True(editor.PreviewMatch);
    }

    /// <summary>
    /// Setting <see cref="MatchEntryEditor.PreviewCandidate"/> raises <c>PropertyChanged</c> for
    /// <see cref="MatchEntryEditor.PreviewMatch"/> (the <c>OnPreviewCandidateChanged</c> partial)
    /// — so typing a candidate re-evaluates the live chip. The transition flips false→true.
    /// </summary>
    [Fact]
    public void SettingPreviewCandidate_RaisesPropertyChanged_ForPreviewMatch()
    {
        var editor = new MatchEntryEditor { Mode = EntryMode.Dn, Value = BuiltinGlob };
        Assert.False(editor.PreviewMatch); // empty candidate

        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.PreviewCandidate = MatchingDn;

        Assert.Contains(nameof(MatchEntryEditor.PreviewMatch), changed);
        Assert.True(editor.PreviewMatch);
    }

    // === Build() omits the preview scratch =============================================

    /// <summary>
    /// <see cref="MatchEntryEditor.Build"/> reads only Mode/Value/Note/Endpoint — the
    /// <see cref="MatchEntryEditor.PreviewCandidate"/> scratch is NEVER serialized: the built
    /// <see cref="MatchEntry"/> carries the Dn (per Mode), Note and endpoint, with no trace of
    /// the preview candidate anywhere in its fields.
    /// </summary>
    [Fact]
    public void Build_IgnoresPreviewCandidate_CarriesOnlyModeValueNoteEndpoint()
    {
        var editor = new MatchEntryEditor
        {
            Mode = EntryMode.Dn,
            Value = BuiltinGlob,
            Note = "a note",
            PreviewCandidate = MatchingDn, // scratch the user typed to test the glob
        };

        var built = editor.Build();

        Assert.Equal(BuiltinGlob, built.Dn);
        Assert.Null(built.Name);
        Assert.Equal("a note", built.Note);
        Assert.Equal(MatchEndpoint.Any, built.Endpoint); // non-nesting list forces Any
        // The preview candidate value appears in none of the built entry's data fields.
        Assert.NotEqual(MatchingDn, built.Dn);
        Assert.NotEqual(MatchingDn, built.Name);
        Assert.NotEqual(MatchingDn, built.Note);
    }

    /// <summary>
    /// A full ruleset round-trip is byte-identical whether or not a preview candidate is set on an
    /// ignore entry: the preview scratch is pure UI state, so
    /// <c>Serialize(BuildRuleset())</c> of a mirror with a candidate set equals that of the same
    /// mirror without one. This proves the scratch leaves NO residue in the serialized bytes — the
    /// strong, whole-file version of the <c>Build()</c>-omission pin.
    /// </summary>
    [Fact]
    public void FullRulesetRoundTrip_WithPreviewCandidate_SerializesByteIdentically()
    {
        // Two independent mirrors over the same default; add one ignore entry to each.
        var withoutCandidate = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        withoutCandidate.Ignore.Add(new MatchEntryEditor
        {
            Mode = EntryMode.Dn,
            Value = BuiltinGlob,
            Note = "builtin ignore",
        });

        var withCandidate = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        withCandidate.Ignore.Add(new MatchEntryEditor
        {
            Mode = EntryMode.Dn,
            Value = BuiltinGlob,
            Note = "builtin ignore",
            PreviewCandidate = MatchingDn, // the ONLY difference — pure UI scratch
        });

        var withoutBytes = RulesetSerializer.Serialize(withoutCandidate.BuildRuleset());
        var withBytes = RulesetSerializer.Serialize(withCandidate.BuildRuleset());

        Assert.Equal(withoutBytes, withBytes);
    }
}
