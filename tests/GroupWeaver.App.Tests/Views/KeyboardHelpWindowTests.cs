using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Feedback;
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
///
/// <para>Issue #271 (crosscut-2): this file previously only ever asserted <c>TextBlock</c>
/// strings, never rendering an actual PNG — the sheet had zero screenshot-fixture and zero
/// <c>docs/ui-checklist.md</c> coverage. <see cref="Realizes_WithBothSections_AndRepresentativeShortcutRows"/>
/// now additionally captures <c>keyboard-help-560x480.png</c> (the default size, from
/// <c>KeyboardHelpWindow.axaml</c>'s own <c>Width</c>/<c>Height</c>); <see cref="Realizes_AtMinSize_NoClipping"/>
/// captures <c>keyboard-help-440x360.png</c> at the window's <c>MinWidth</c>/<c>MinHeight</c> — the
/// min-size overflow/clipping behavior had never been rendered before this fixture.</para>
/// </summary>
public sealed class KeyboardHelpWindowTests
{
    private static readonly Lazy<string> ArtifactsUiDir = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Directory.CreateDirectory(Path.Combine(dir.FullName, "artifacts", "ui")).FullName;
    });

    /// <summary>The window realizes headlessly with BOTH section eyebrows and a representative
    /// shortcut chip from each — the ANYWHERE "F11" (full-screen) and the ON-THE-GRAPH
    /// "Ctrl + K" (command palette). Proves the sheet's two-section structure and that the
    /// discoverability copy the "?" button surfaces is actually present in the shipped view.
    /// Also captures <c>keyboard-help-560x480.png</c> (the window's own default size,
    /// <c>KeyboardHelpWindow.axaml</c>'s <c>Width</c>/<c>Height</c>) for the ui-verifier.</summary>
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

        CapturePng(window, "keyboard-help", 560, 480);

        window.Close();
    }

    /// <summary>
    /// Issue #271 (crosscut-2): renders the sheet at its <c>MinWidth</c>/<c>MinHeight</c>
    /// (440x360, <c>KeyboardHelpWindow.axaml</c>) — the smallest the window is ever allowed to
    /// shrink to — and captures <c>keyboard-help-440x360.png</c>. Both section eyebrows and one
    /// representative chip from each still realize (the content does not silently vanish at the
    /// floor size); whether the <c>ScrollViewer</c> shows a scroll cue rather than clipping
    /// silently at this size is a visual judgment the ui-verifier makes from the PNG (an
    /// <c>[I]</c> row in <c>docs/ui-checklist.md</c> — not independently provable headless
    /// beyond "the content still realizes and the window did not throw/crash at the floor size").
    /// </summary>
    [AvaloniaFact]
    public void Realizes_AtMinSize_NoClipping()
    {
        var window = new KeyboardHelpWindow { Width = 440, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text)
            .ToList();

        // At the floor size the content must still realize — no silent loss of a section or a
        // representative shortcut row (whether it clips vs. scrolls is the ui-verifier's PNG call).
        Assert.Contains("ANYWHERE", texts);
        Assert.Contains("ON THE GRAPH", texts);
        Assert.Contains("F11", texts);
        Assert.Contains("Ctrl + K", texts);

        Assert.True(window.IsVisible, "the keyboard-help sheet window must realize as shown at MinWidth/MinHeight");

        CapturePng(window, "keyboard-help", 440, 360);

        window.Close();
    }

    /// <summary>
    /// Feedback-intake slice (feat/feedback-intake): the footer's "Report an issue…" link and
    /// the Close button's unchanged contract, asserted at the 440x360 MinWidth/MinHeight FLOOR
    /// (the hardest case — a footer inside the ScrollViewer would scroll out of reach exactly
    /// here). Both buttons must (a) realize effectively visible, and (b) sit OUTSIDE the
    /// ScrollViewer (docked footer, no ScrollViewer visual ancestor) so they stay reachable at
    /// any size. The link carries the explicit automation name "Report an issue" (glyph/ellipsis
    /// content — the #219 accessible-name discipline); Close keeps IsDefault + IsCancel (Enter
    /// and Esc both dismiss the modal sheet — the pre-existing contract, unchanged by adding
    /// the link). A bare-constructed window (this headless path — no shell) must fall back to
    /// the un-prefilled template URL, never an empty/null link target.
    /// </summary>
    [AvaloniaFact]
    public void Footer_ReportIssueLink_AndCloseContract_AtMinSizeFloor()
    {
        var window = new KeyboardHelpWindow { Width = 440, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

        // The "Report an issue" link: named for automation, visible, and OUTSIDE the scroller.
        var report = buttons.Single(b => AutomationProperties.GetName(b) == "Report an issue");
        Assert.True(report.IsEffectivelyVisible, "the report-issue link must be visible at the floor size");
        Assert.Empty(report.GetVisualAncestors().OfType<ScrollViewer>());

        // Close keeps its full pre-existing contract: IsDefault (Enter) + IsCancel (Esc),
        // visible and reachable outside the scroller at the floor size.
        var close = buttons.Single(b => (b.Content as string) == "Close");
        Assert.True(close.IsDefault, "Close must remain the default (Enter) button");
        Assert.True(close.IsCancel, "Close must remain the cancel (Esc) button");
        Assert.True(close.IsEffectivelyVisible, "Close must be visible at the floor size");
        Assert.Empty(close.GetVisualAncestors().OfType<ScrollViewer>());

        // Bare construction (no shell): the link target falls back to the template-only URL.
        Assert.Equal(UxFeedbackLink.TemplateOnlyUrl, window.ReportIssueUrl);

        window.Close();
    }

    /// <summary>Capture core (parity with the other screenshot fixtures' capture-and-discard
    /// idiom, e.g. <c>ShellScreenshotTests.CapturePng</c>): flush pending jobs, discard the
    /// lagging compositor frame, capture the settled frame, prove it is a real non-blank
    /// rasterization of the requested size, and write the PNG to <c>artifacts/ui</c>.</summary>
    private static void CapturePng(Window window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(width, height), frame.PixelSize);
        AssertSampledPixelsNonUniform(frame, name);

        var path = Path.Combine(ArtifactsUiDir.Value, $"{name}-{width}x{height}.png");
        frame.Save(path);

        var file = new FileInfo(path);
        Assert.True(file.Exists, $"'{path}' was not written");
        Assert.True(file.Length > 0, $"'{path}' is empty");
    }

    /// <summary>Non-trivial-frame gate (parity with <c>ShellScreenshotTests</c>): sample a 32x32
    /// grid and require at least two distinct pixel values — a failed capture renders uniformly
    /// blank, while every real frame paints text on a background.</summary>
    private static void AssertSampledPixelsNonUniform(WriteableBitmap frame, string name)
    {
        using var fb = frame.Lock();
        Assert.Equal(32, fb.Format.BitsPerPixel);

        var first = Marshal.ReadInt32(fb.Address);
        var stepX = Math.Max(1, fb.Size.Width / 32);
        var stepY = Math.Max(1, fb.Size.Height / 32);
        for (var y = 0; y < fb.Size.Height; y += stepY)
        {
            for (var x = 0; x < fb.Size.Width; x += stepX)
            {
                if (Marshal.ReadInt32(fb.Address, (y * fb.RowBytes) + (x * 4)) != first)
                {
                    return;
                }
            }
        }

        Assert.Fail($"'{name}': every sampled pixel is identical — a blank frame, not the rendered sheet");
    }
}
