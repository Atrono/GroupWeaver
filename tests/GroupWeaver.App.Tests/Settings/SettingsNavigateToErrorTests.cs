using System;
using System.IO;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the "Settings editor completeness — Slice B" <see cref="SettingsViewModel.SelectedTabIndex"/>
/// + <c>NavigateToError</c> surface: a clicked validation-band error jumps the editor to the tab
/// that OWNS the offending JSON-pointer path. The mapping is over the fixed tab layout
/// Rules(0) / Naming(1) / Matrix(2) / Ignore &amp; Exceptions(3) / File(4) / Advanced(5):
/// <list type="bullet">
/// <item><c>$.naming*</c> → 1 (Naming)</item>
/// <item><c>$.nesting*</c> → 2 (Matrix)</item>
/// <item><c>$.ignore*</c> / <c>$.circular*</c> / <c>$.emptyGroup*</c> → 3 (Ignore &amp; Exceptions —
///   the exception lists live there)</item>
/// <item><c>$.name</c> / <c>$.description</c> / <c>$.author</c> and any UNRECOGNIZED path → no-op
///   (the always-visible metadata header needs no tab change)</item>
/// <item>a null error → no-op</item>
/// </list>
///
/// <para>The matching goes through <c>StartsWithSection</c>, which is bounded by end-of-string or a
/// <c>.</c>/<c>[</c> follow-char — so <c>$.name</c> maps to no-op (metadata), NOT to Naming(1). That
/// over-match boundary is the real risk (<c>name</c> is a prefix of <c>naming</c>), so it is pinned
/// directly. Same temp-dir <see cref="RulesetLocator"/> seam + <c>OpenClean</c> idiom as
/// <see cref="SettingsValidationTests"/> / <see cref="SettingsEditorSliceATests"/>; pure VM logic,
/// no headless UI, no dispatcher. The command never reads
/// <see cref="RulesetValidationError.Message"/> (#45 plain-text discipline) — only the path.</para>
/// </summary>
public sealed class SettingsNavigateToErrorTests
{
    // === path prefix ⇒ owning tab index =================================================

    /// <summary>A <c>$.naming[n].*</c> path jumps to the Naming tab (1).</summary>
    [Fact]
    public void NavigateToError_NamingPath_SelectsNamingTab()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.naming[1].pattern", "msg"));

        Assert.Equal(1, vm.SelectedTabIndex);
    }

    /// <summary>A <c>$.nesting.*</c> path jumps to the Matrix tab (2).</summary>
    [Fact]
    public void NavigateToError_NestingPath_SelectsMatrixTab()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(
            new RulesetValidationError("$.nesting.matrix.GlobalGroup.User", "msg"));

        Assert.Equal(2, vm.SelectedTabIndex);
    }

    /// <summary>A <c>$.ignore[n].*</c> path jumps to the Ignore &amp; Exceptions tab (3).</summary>
    [Fact]
    public void NavigateToError_IgnorePath_SelectsIgnoreAndExceptionsTab()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.ignore[0].dn", "msg"));

        Assert.Equal(3, vm.SelectedTabIndex);
    }

    /// <summary>A <c>$.circular.*</c> path jumps to the Ignore &amp; Exceptions tab (3) —
    /// the circular rule's exception list lives there.</summary>
    [Fact]
    public void NavigateToError_CircularPath_SelectsIgnoreAndExceptionsTab()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(
            new RulesetValidationError("$.circular.exceptions[0].dn", "msg"));

        Assert.Equal(3, vm.SelectedTabIndex);
    }

    /// <summary>A <c>$.emptyGroup.*</c> path jumps to the Ignore &amp; Exceptions tab (3).</summary>
    [Fact]
    public void NavigateToError_EmptyGroupPath_SelectsIgnoreAndExceptionsTab()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.emptyGroup.enabled", "msg"));

        Assert.Equal(3, vm.SelectedTabIndex);
    }

    // === metadata / unrecognized / null ⇒ no-op ========================================

    /// <summary>
    /// A <c>$.name</c> path is a metadata-header field — NOT a section tab — so it is a no-op:
    /// the index is left at whatever it was. THE boundary case: <c>name</c> is a prefix of
    /// <c>naming</c>, so this also proves <c>StartsWithSection</c> does not over-match
    /// <c>$.name</c> onto the Naming(1) tab (the follow-char bound at end-of-string holds).
    /// </summary>
    [Fact]
    public void NavigateToError_NamePath_IsNoOp_AndDoesNotOverMatchNamingTab()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.name", "msg"));

        Assert.Equal(StartIndex, vm.SelectedTabIndex);
        Assert.NotEqual(1, vm.SelectedTabIndex); // never spuriously the Naming tab
    }

    /// <summary>A <c>$.description</c> path is a metadata-header field — a no-op.</summary>
    [Fact]
    public void NavigateToError_DescriptionPath_IsNoOp()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.description", "msg"));

        Assert.Equal(StartIndex, vm.SelectedTabIndex);
    }

    /// <summary>A <c>$.author</c> path is a metadata-header field — a no-op.</summary>
    [Fact]
    public void NavigateToError_AuthorPath_IsNoOp()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.author", "msg"));

        Assert.Equal(StartIndex, vm.SelectedTabIndex);
    }

    /// <summary>An UNRECOGNIZED path (e.g. <c>$.bogus</c>) is a no-op — the mapping fails
    /// closed to "no tab change" rather than jumping somewhere arbitrary.</summary>
    [Fact]
    public void NavigateToError_UnrecognizedPath_IsNoOp()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.bogus", "msg"));

        Assert.Equal(StartIndex, vm.SelectedTabIndex);
    }

    /// <summary>
    /// A hypothetical longer sibling token that merely STARTS with a section word (e.g.
    /// <c>$.namingfoo</c>, if such a path could ever occur) must NOT be treated as the naming
    /// section: <c>StartsWithSection</c> requires the section token to end at a <c>.</c>/<c>[</c>
    /// or end-of-string, so a bare-word continuation (<c>foo</c>) is an unrecognized path and a
    /// no-op — never the Naming(1) tab. This is the direct anti-over-match pin.
    /// </summary>
    [Fact]
    public void NavigateToError_LongerSiblingToken_DoesNotOverMatchSection_IsNoOp()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(new RulesetValidationError("$.namingfoo", "msg"));

        Assert.Equal(StartIndex, vm.SelectedTabIndex);
        Assert.NotEqual(1, vm.SelectedTabIndex);
    }

    /// <summary>A null error is a no-op — the command guards against it and leaves the index.</summary>
    [Fact]
    public void NavigateToError_NullError_IsNoOp()
    {
        var vm = OpenClean();
        vm.SelectedTabIndex = StartIndex;

        vm.NavigateToErrorCommand.Execute(null);

        Assert.Equal(StartIndex, vm.SelectedTabIndex);
    }

    // === helpers ========================================================================

    /// <summary>A non-zero, non-target start so a "no-op" is provably distinct from "reset to 0"
    /// and from any of the mapped targets (1/2/3) — the File tab (4).</summary>
    private const int StartIndex = 4;

    /// <summary>Opens the editor on a CLEAN effective (no user file → embedded default, no
    /// errors) against a temp-dir locator seam — the <see cref="SettingsValidationTests"/> idiom
    /// (the temp dir is disposed at process exit; these VM-only tests never write it).</summary>
    private static SettingsViewModel OpenClean()
    {
        var locator = new RulesetLocator(
            Directory.CreateTempSubdirectory("groupweaver-settings-navigate-tests-").FullName);
        return SettingsViewModel.Open(locator.LoadEffective(), locator);
    }
}
