using System;
using System.Linq;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins ADR-033 — the app-wide KEYBOARD-focus-visible ring (App.axaml) — at the rendered visual-tree
/// level. The mechanism shipped is a custom accent <see cref="Control.FocusAdorner"/> that REPLACES the Fluent
/// default focus visual: a <see cref="Border"/> stroked <c>{DynamicResource AccentTextBrush}</c>
/// (dark <c>#A99BFF</c> / light <c>#4A3CC8</c>), <c>BorderThickness=2</c>, the control's
/// <c>CornerRadius</c>, drawn edge-hugging (<c>Margin=0</c>). The original <c>:focus-visible</c> inset
/// <see cref="BoxShadow"/> approach was abandoned because Fluent's default focus visual composited ON
/// TOP and MASKED it (white/black/blue dominated; on ComboBox the accent was fully covered) — replacing
/// the whole <see cref="Control.FocusAdorner"/> is what makes the accent the dominant indicator. These tests are
/// re-pinned to the new mechanism (the superseded BoxShadow assertions are gone), and a render-pixel
/// guard is added below — the gap the BoxShadow tests could not catch (the ui-verifier had to find the
/// masking by eye).
///
/// <para><b>How <c>:focus-visible</c> is read (the mechanism the task pins):</b> <c>:focus-visible</c> is
/// TRUE on <c>control.Focus(NavigationMethod.Tab)</c> (keyboard / programmatic) and FALSE on
/// <c>control.Focus(NavigationMethod.Pointer)</c> (a pointer press). We never read the pseudo-class flag
/// directly — we assert its OBSERVABLE EFFECT through TWO load-bearing seams (both empirically verified):
/// <list type="number">
/// <item>the control's <see cref="Control.FocusAdorner"/> is the custom <c>FocusAdornerTemplate</c>
///   (non-null) under keyboard focus, and <c>null</c> under pointer focus / when focus is elsewhere; and</item>
/// <item>the REALIZED adorner — a <see cref="Border"/> child of the window's
///   <see cref="AdornerLayer"/> — is present under keyboard focus with its <see cref="Border.BorderBrush"/>
///   resolving to the accent ink (dark <c>#A99BFF</c>, the app's default Dark variant), and the
///   <see cref="AdornerLayer"/> has NO border children under pointer focus / focus-elsewhere.</item>
/// </list>
/// Both arms together prove the ring is keyboard-only by construction.</para>
///
/// <para>Re-focusing the already-focused element is a no-op (the <see cref="NavigationMethod"/>, hence
/// <c>:focus-visible</c>, would not change), so each probe parks focus on a real sibling first and then
/// focuses the control FROM the sibling — the same technique the original file used. Coverage spans the
/// covered control set (<see cref="Button"/> incl. ghost/accent/chip/segment variants,
/// <see cref="ToggleButton"/>, <see cref="CheckBox"/>, <see cref="ComboBox"/>, <see cref="ListBoxItem"/>),
/// each built bare in a sized, shown headless window so the real <see cref="App"/> styles + FluentTheme
/// apply and the <c>:focus-visible</c> setters resolve.</para>
/// </summary>
public sealed class FocusRingViewTests
{
    /// <summary>The pinned accent ink (Dark variant, the app default) the realized FocusAdorner strokes.</summary>
    private static readonly Color DarkAccentInk = Color.Parse("#A99BFF");

    // ---- the covered control TYPES (task item 1), one per :focus-visible rule -----------------

    [AvaloniaFact]
    public void Button_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(new Button { Content = "Action" });

    [AvaloniaFact]
    public void GhostButton_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(WithClasses(new Button { Content = "Ghost" }, "ghost"));

    [AvaloniaFact]
    public void AccentButton_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(WithClasses(new Button { Content = "Connect" }, "accent"));

    /// <summary>The filter CHIP is a <see cref="Button"/> with a bound <c>chip</c> class — it inherits
    /// the bare <c>Button</c> rule (and gets a radius-12 ring override in AuditView.axaml, so the chip
    /// ring ROUNDS — the old square-corner limitation is resolved).</summary>
    [AvaloniaFact]
    public void ChipButton_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(WithClasses(new Button { Content = "Errors" }, "chip"));

    /// <summary>The SEGMENT control is a <see cref="Button"/> with a bound <c>segment</c> class — it
    /// inherits the bare <c>Button</c> rule (the radius-0 segment gets the square-corner ring).</summary>
    [AvaloniaFact]
    public void SegmentButton_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(WithClasses(new Button { Content = "Graph" }, "segment"));

    [AvaloniaFact]
    public void ToggleButton_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(new ToggleButton { Content = "Pin" });

    [AvaloniaFact]
    public void CheckBox_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(new CheckBox { Content = "Enabled" });

    [AvaloniaFact]
    public void ComboBox_KeyboardFocus_ShowsRing_PointerFocus_DoesNot() =>
        AssertKeyboardOnlyRing(new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 });

    /// <summary>The <see cref="ListBoxItem"/> rule: the focused item shows the accent ring on keyboard
    /// focus. The item is hosted in a real <see cref="ListBox"/> (its container) so the Fluent
    /// <see cref="ListBoxItem"/> template applies; we focus the realized container.</summary>
    [AvaloniaFact]
    public void ListBoxItem_KeyboardFocus_ShowsRing_PointerFocus_DoesNot()
    {
        var listBox = new ListBox { ItemsSource = new[] { "Alpha", "Beta" } };
        var (window, _) = Show(listBox);

        var item = Assert.Single(
            listBox.GetVisualDescendants().OfType<ListBoxItem>(),
            i => i.Content as string == "Alpha");

        AssertKeyboardOnlyRingOn(item, window);
        window.Close();
    }

    // ---- a focused control inside a REAL shipped view (task item 3) ---------------------------

    /// <summary>
    /// The ADR-033 Consequences claim — "a keyboard-focused control in a real view shows the ring" —
    /// proven on a SHIPPED view rather than a bare control: the <see cref="ConnectionView"/>'s real
    /// "Connect to domain" <c>accent</c> <see cref="Button"/>, Tab-focused, shows the custom accent
    /// <see cref="Control.FocusAdorner"/> (its realized AdornerLayer <see cref="Border"/> strokes the accent ink);
    /// a Pointer focus does not. Proves the App.axaml <c>Button:focus-visible</c> rule fires through a
    /// production view's full style/template resolution, not only on a hand-built Button.
    /// </summary>
    [AvaloniaFact]
    public void RealView_KeyboardFocusedConnectButton_ShowsRing_PointerDoesNot()
    {
        var (window, view) = ShowConnectionView();

        var connect = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            b => (b.Content as string) == "Connect to domain");

        AssertKeyboardOnlyRingOn(connect, window);
        window.Close();
    }

    // ---- the MASKING GUARD: a real-render pixel check (task item 4) ----------------------------

    /// <summary>
    /// The durable masking guard the BoxShadow tests could not provide (the ui-verifier had to catch the
    /// Fluent default masking by eye). With REAL Skia drawing (<see cref="TestAppBuilder"/>'s
    /// <c>UseHeadlessDrawing=false</c>), a <see cref="Button"/> is Tab-focused, the frame is captured, and
    /// the TOP ring band (1px into the 2px ring, across the control's width) is SAMPLED: the dominant
    /// colour must be the resolved <c>AccentTextBrush</c> (within tolerance) and there must be NO
    /// pure-white (Fluent's dark-theme default ring), NO Fluent-blue (<c>#0078D7</c>) pixels in the band.
    /// Deterministic — a defined band, generous tolerance, no sleeps (capture-and-discard then capture, so
    /// the committed batch is read, not the previous frame).
    /// </summary>
    [AvaloniaFact]
    public void RenderedRingBand_IsAccent_NotWhiteOrBlue_Button() =>
        AssertRenderedRingBandIsAccent(new Button
        {
            Content = "Action",
            Width = 160,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

    /// <summary>
    /// The same render-pixel guard on a <see cref="ComboBox"/> — the WORST masking case: Fluent's
    /// <c>Border#HighlightBackground</c> turns a solid blue (<c>#0078D7</c>) on focus that fully covered
    /// the earlier BoxShadow accent. ADR-033 suppresses it (<c>:focus-visible /template/
    /// Border#HighlightBackground { Background: Transparent }</c>) AND draws the accent FocusAdorner; this
    /// proves the captured ring band reads accent with ZERO blue — the regression that masked the accent
    /// before can never silently return. (The ComboBox is <c>ClipToBounds</c>, so this also exercises the
    /// edge-hugging <c>Margin=0</c> adorner that an outward adorner would have had clipped away.)
    /// </summary>
    [AvaloniaFact]
    public void RenderedRingBand_IsAccent_NotWhiteOrBlue_ComboBox()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "One", "Two" },
            SelectedIndex = 0,
            Width = 160,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ComboBox: park focus on a real sibling so the Tab focus is keyboard, then capture.
        var sibling = new Button { Content = "Sibling" };
        var window = new Window
        {
            Content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { combo, sibling },
            },
            Width = 240,
            Height = 200,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(sibling.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();
        Assert.True(combo.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertTopRingBandIsAccent(window, combo, "ComboBox");
        window.Close();
    }

    // ---- the shared keyboard-only assertion (FocusAdorner mechanism) --------------------------

    /// <summary>Builds <paramref name="control"/> in a shown headless window, then asserts the keyboard-
    /// only ring contract: the custom accent <see cref="Control.FocusAdorner"/> after
    /// <see cref="NavigationMethod.Tab"/>, none after <see cref="NavigationMethod.Pointer"/>.</summary>
    private static void AssertKeyboardOnlyRing(Control control)
    {
        var (window, hosted) = Show(control);
        AssertKeyboardOnlyRingOn(hosted, window);
        window.Close();
    }

    /// <summary>The core contract, given an already-realized control and the live window: focusing it by
    /// Tab (keyboard / programmatic) sets the custom accent <see cref="Control.FocusAdorner"/> and realizes an
    /// accent-stroked <see cref="Border"/> in the window's <see cref="AdornerLayer"/>; focusing it by
    /// Pointer (and, as a second proof, moving focus to a sibling) leaves it ringless.</summary>
    private static void AssertKeyboardOnlyRingOn(Control control, Window window)
    {
        // A real sibling to park focus on between probes — re-focusing the already-focused element is a
        // no-op (the NavigationMethod, hence :focus-visible, would not change), so each probe must start
        // from focus elsewhere. Parented into any Panel in the live visual tree.
        var host = window.GetVisualDescendants().OfType<Panel>().First();
        var sibling = new Button { Content = "Sibling" };
        host.Children.Add(sibling);
        Dispatcher.UIThread.RunJobs();

        // Baseline: no focus on the control ⇒ no custom adorner, no realized accent border.
        Assert.Null(control.FocusAdorner);
        AssertNoRealizedAccentBorder(window);

        // (a) Keyboard / programmatic focus (NavigationMethod.Tab) ⇒ :focus-visible TRUE ⇒ the accent ring.
        Assert.True(
            control.Focus(NavigationMethod.Tab),
            "the control must accept keyboard focus (a precondition for the focus-visible ring)");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Seam 1: the control's FocusAdorner is the custom template (replacing the Fluent default).
        Assert.NotNull(control.FocusAdorner);

        // Seam 2: the realized adorner Border is in the AdornerLayer, stroked in the accent ink.
        var ring = SingleRealizedAdornerBorder(window);
        Assert.True(
            ring.BorderBrush is ISolidColorBrush,
            "the realized FocusAdorner ring must be a solid-colour border (AccentTextBrush)");
        Assert.Equal(DarkAccentInk, ((ISolidColorBrush)ring.BorderBrush!).Color);
        Assert.Equal(new Thickness(2), ring.BorderThickness); // the 2px ADR-033 ring

        // (b) Move focus AWAY (to the sibling) ⇒ the control no longer holds focus ⇒ the ring clears.
        Assert.True(sibling.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(control.FocusAdorner);

        // (c) POINTER focus (NavigationMethod.Pointer) ⇒ :focus-visible FALSE ⇒ NO ring — the keyboard-
        //     only proof. Focus arrives from the sibling, so the GotFocus carries the Pointer method (not
        //     a no-op re-focus of an already-focused element).
        Assert.True(control.Focus(NavigationMethod.Pointer), "the control must accept pointer focus");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(control.FocusAdorner);                 // no custom adorner on a pointer press
        AssertNoRealizedAccentBorder(window);              // and nothing accent-stroked in the layer
    }

    /// <summary>The single <see cref="Border"/> realized into the window's <see cref="AdornerLayer"/> by
    /// the keyboard-focus <c>FocusAdorner</c> — the thing that actually paints the ring.</summary>
    private static Border SingleRealizedAdornerBorder(Window window) =>
        Assert.Single(AdornerBorders(window));

    /// <summary>Asserts NO realized adorner <see cref="Border"/> strokes the accent ink (the ring is
    /// absent). The AdornerLayer itself always exists; under pointer focus / focus-elsewhere it carries no
    /// accent-stroked border child.</summary>
    private static void AssertNoRealizedAccentBorder(Window window) =>
        Assert.DoesNotContain(
            AdornerBorders(window),
            b => b.BorderBrush is ISolidColorBrush scb && scb.Color == DarkAccentInk);

    private static System.Collections.Generic.IEnumerable<Border> AdornerBorders(Window window) =>
        window.GetVisualDescendants()
            .OfType<AdornerLayer>()
            .SelectMany(layer => layer.Children.OfType<Border>());

    // ---- the render-pixel masking guard helpers ----------------------------------------------

    private static void AssertRenderedRingBandIsAccent(Control control)
    {
        var window = new Window { Content = control, Width = 240, Height = 160 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(control.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertTopRingBandIsAccent(window, control, control.GetType().Name);
        window.Close();
    }

    /// <summary>
    /// Captures a real-render frame and samples the TOP ring band of <paramref name="control"/> (1px into
    /// the 2px ring, across the control's width): the accent (<c>#A99BFF</c>, the resolved
    /// <c>AccentTextBrush</c>) must DOMINATE within tolerance, and there must be NO pure-white (Fluent's
    /// dark default ring) and NO Fluent-blue (<c>#0078D7</c>) pixels. The headless frame is
    /// <c>Rgba8888</c> (byte order R,G,B,A — empirically verified), so a pixel decodes directly to RGB.
    /// </summary>
    private static void AssertTopRingBandIsAccent(Window window, Control control, string label)
    {
        // capture-and-discard then capture: the headless compositor renders one committed batch per tick,
        // so the first capture after a mutation returns the previous frame (no sleeps; deterministic).
        window.CaptureRenderedFrame()?.Dispose();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var fb = frame!.Lock();
        Assert.Equal(32, fb.Format.BitsPerPixel);

        var tl = control.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        var yTop = (int)tl.Y + 1; // 1px into the 2px top ring stroke
        var x0 = (int)tl.X;
        var x1 = (int)(tl.X + control.Bounds.Width);

        var accent = DarkAccentInk;
        var blue = Color.FromRgb(0x00, 0x78, 0xD7); // Fluent's default focus blue
        int accentCount = 0, whiteCount = 0, blueCount = 0, total = 0;

        for (var x = x0; x < x1; x++)
        {
            var off = (yTop * fb.RowBytes) + (x * 4);
            var r = Marshal.ReadByte(fb.Address, off + 0);
            var g = Marshal.ReadByte(fb.Address, off + 1);
            var b = Marshal.ReadByte(fb.Address, off + 2);
            total++;

            if (Near(r, g, b, accent, tolerance: 24))
            {
                accentCount++;
            }
            else if (r > 235 && g > 235 && b > 235)
            {
                whiteCount++; // Fluent's WHITE default ring (dark theme) — the masking failure mode
            }
            else if (Near(r, g, b, blue, tolerance: 40))
            {
                blueCount++; // Fluent's solid-blue ComboBox focus fill — the worst masking failure mode
            }
        }

        Assert.True(total > 0, $"'{label}': the sampled ring band has zero width");
        Assert.Equal(0, whiteCount); // no Fluent white default ring masking the accent
        Assert.Equal(0, blueCount);  // no Fluent blue focus fill masking the accent
        Assert.True(
            accentCount > total / 2,
            $"'{label}': the rendered ring band must be DOMINATED by the accent ink — accent={accentCount} "
            + $"of {total} (white={whiteCount} blue={blueCount}); the Fluent default must not mask it");
    }

    private static bool Near(byte r, byte g, byte b, Color c, int tolerance) =>
        Math.Abs(r - c.R) <= tolerance && Math.Abs(g - c.G) <= tolerance && Math.Abs(b - c.B) <= tolerance;

    // ---- headless hosting (the App-styled real render path) -----------------------------------

    private static Control WithClasses(Control control, params string[] classes)
    {
        foreach (var c in classes)
        {
            control.Classes.Add(c);
        }

        return control;
    }

    /// <summary>Hosts <paramref name="control"/> in a focusable panel inside a sized, shown headless
    /// window (the real <see cref="App"/> FluentTheme + Tokens + App.axaml styles apply) and runs a
    /// layout/template pass so the Fluent template parts are materialized before focus.</summary>
    private static (Window Window, Control Control) Show(Control control)
    {
        var window = new Window
        {
            Content = new StackPanel { Children = { control } },
            Width = 480,
            Height = 320,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout(); // force template application so the named parts exist
        Dispatcher.UIThread.RunJobs();
        return (window, control);
    }

    /// <summary>A real <see cref="ConnectionView"/> in a sized, shown headless window (the App styles +
    /// a layout pass apply). The VM's commands never fire here — we only Tab-focus a static button — so
    /// the provider factory is a never-called stub and <c>onConnected</c> is a no-op (the TypographyTests
    /// wordmark idiom).</summary>
    private static (Window Window, ConnectionView View) ShowConnectionView()
    {
        var vm = new ConnectionViewModel(
            _ => new StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub", 0))),
            (_, _, _) => { });

        var view = new ConnectionView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
