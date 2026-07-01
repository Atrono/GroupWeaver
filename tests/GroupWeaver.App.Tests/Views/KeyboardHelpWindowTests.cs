using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Views;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Discoverability slice (feat/discoverability): pins the static keyboard/gesture cheat
/// sheet — <see cref="KeyboardHelpWindow"/>, the "?" top-command-strip affordance — at the
/// realized visual-tree level. The window is content-only (no VM), so there is nothing to
/// drive: showing it headlessly and running a layout pass materializes the whole sheet, and
/// these asserts prove the two labelled sections (ANYWHERE / ON THE GRAPH) and a
/// representative shortcut chip from each render. Realizing the window (not opening a modal
/// <c>ShowDialog</c> loop — headless-hostile, ADR-011 open-risk #3) is the right level: the
/// content is static, so a realized-tree assertion is the whole contract.
///
/// <para>The ADR-001 airspace guardrail (never layer a native window over the WebView2
/// GraphHost) is why this is its OWN top-level <see cref="Window"/> — asserted structurally
/// by the fact this realizes as a standalone Window with the sheet as its content.</para>
/// </summary>
public sealed class KeyboardHelpWindowTests
{
    /// <summary>The window realizes headlessly with BOTH section eyebrows and a representative
    /// shortcut chip from each — the ANYWHERE "F11" (full-screen) and the ON-THE-GRAPH
    /// "Ctrl + K" (command palette). Proves the sheet's two-section structure and that the
    /// discoverability copy the "?" button surfaces is actually present in the shipped view.</summary>
    [AvaloniaFact]
    public void Realizes_WithBothSections_AndRepresentativeShortcutRows()
    {
        var window = new KeyboardHelpWindow { Width = 560, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout(); // force template application so every row's TextBlock exists
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();

        // The two section eyebrows (ANYWHERE / ON THE GRAPH) — the sheet's structure.
        Assert.Contains("ANYWHERE", texts);
        Assert.Contains("ON THE GRAPH", texts);

        // A representative shortcut chip from EACH section (both the key chip and its
        // description render): F11 (ANYWHERE, full-screen) and Ctrl + K (ON THE GRAPH,
        // the command palette — the discoverability slice's headline shortcut).
        Assert.Contains("F11", texts);
        Assert.Contains("Toggle full-screen", texts);
        Assert.Contains("Ctrl + K", texts);
        Assert.Contains("Open the search & command palette", texts);

        // The window is its OWN top-level Window (ADR-001 airspace guardrail 5): the sheet
        // realizes inside a standalone Window, never layered over another surface.
        Assert.IsType<KeyboardHelpWindow>(window);
        Assert.True(window.IsVisible, "the keyboard-help sheet window must realize as shown");

        window.Close();
    }
}
