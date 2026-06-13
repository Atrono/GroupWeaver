using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins #45 / ADR-011 §4 — the load-bearing plain-text contract for a
/// <see cref="MatchEntry.Note"/> in the AP 3.3 settings editor: an untrusted ruleset
/// file may carry control chars in a note (a BEL, an ANSI escape, ...), so every note
/// DISPLAY surface in the editor must render the string as INERT, VERBATIM plain text —
/// a <c>TextBlock</c> / <c>SelectableTextBlock</c> (the <c>DetailPanelView</c> precedent)
/// or a <c>TextBox</c> that is EDITING the note — never a format template, a markup
/// surface, a tooltip-on-raw-value, or anything that interprets the bytes.
///
/// <para>The subject is the exact spec token <c>"[31m"</c>: a BEL
/// (<c>U+0007</c>) followed by an ANSI SGR red-foreground escape (<c>ESC [31m</c>). The
/// test seeds a single ignore entry whose <see cref="MatchEntryEditor.Note"/> is that
/// string, renders the REAL <see cref="SettingsWindow"/> Ignore &amp; Exceptions tab
/// headlessly (Avalonia.Headless — the same seam <c>ShellScreenshotTests</c> uses; the
/// note binding is what is under test, not a window-show), and asserts that some realized,
/// text-bearing control in the visual tree carries that note's <c>Text</c> ORDINALLY
/// byte-for-byte — neither stripped, escaped, re-encoded, nor interpreted. A control that
/// merely CONTAINS the note as a substring of a larger interpolated string (e.g. a
/// "{path} — {note}" template) does NOT satisfy the pin: the contract is a standalone,
/// verbatim Text target.</para>
///
/// <para><b>RED</b> until the AP 3.3 / S7 Ignore &amp; Exceptions tab binds
/// <see cref="SettingsViewModel.Ignore"/> rows and renders each <c>Note</c> through a
/// <c>TextBlock</c>/<c>SelectableTextBlock</c>/<c>TextBox</c> <c>Text</c> target — today
/// the tab is a placeholder, so no control carries the note at all.</para>
/// </summary>
public sealed class MatchEntryNotePlainTextTests
{
    /// <summary>The exact #45 token: a BEL then an ANSI SGR red escape — inert bytes the
    /// editor must render verbatim, never interpret.</summary>
    private const string ControlCharNote = "[31m";

    /// <summary>
    /// The seeded ignore-entry note (<c>"[31m"</c>) renders into at least one
    /// realized control whose <c>Text</c> is ORDINALLY byte-for-byte equal to the note — the
    /// control chars are never interpreted (no BEL rung, no ANSI escape honored), never
    /// stripped, never re-encoded, and never a markup/format surface (#45 / ADR-011 §4).
    /// </summary>
    [AvaloniaFact]
    public void IgnoreNote_WithControlChars_RendersVerbatimPlainText()
    {
        // A minimal default mirror with one ignore entry whose note is the control-char token.
        // (LoadFrom seeds the full default; we add the subject row so the assertion has a known target.)
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        var entry = MatchEntryEditor.LoadFrom(
            new MatchEntry { Dn = "CN=Subject,OU=Pin,*", Note = ControlCharNote },
            endpointEditable: false);
        vm.Ignore.Add(entry);

        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        SelectIgnoreTab(window);
        Dispatcher.UIThread.RunJobs();

        // The note must reach a standalone, verbatim Text target — a TextBlock/SelectableTextBlock
        // (SelectableTextBlock derives from TextBlock) OR a TextBox editing the note. ORDINAL
        // equality pins it byte-for-byte: the BEL and the ANSI escape survive, uninterpreted.
        var verbatimInTextBlock = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(t => string.Equals(t.Text, ControlCharNote, StringComparison.Ordinal));
        var verbatimInTextBox = window.GetVisualDescendants()
            .OfType<TextBox>()
            .Any(t => string.Equals(t.Text, ControlCharNote, StringComparison.Ordinal));

        Assert.True(
            verbatimInTextBlock || verbatimInTextBox,
            "the ignore note must render VERBATIM into a TextBlock/SelectableTextBlock/TextBox "
            + "Text target — control chars never interpreted, never a format/markup surface (#45)");

        window.Close();
    }

    /// <summary>Brings the "Ignore &amp; Exceptions" tab to the front so its rows realize.
    /// Substring + ordinal-ignore-case so it still resolves if the implementer relabels it.</summary>
    private static void SelectIgnoreTab(SettingsWindow window)
    {
        var tabs = Assert.Single(window.GetVisualDescendants().OfType<TabControl>());
        var item = Assert.Single(
            tabs.GetVisualDescendants().OfType<TabItem>(),
            t => (t.Header?.ToString() ?? string.Empty)
                .Contains("Ignore", StringComparison.OrdinalIgnoreCase));
        tabs.SelectedItem = item;
    }
}
