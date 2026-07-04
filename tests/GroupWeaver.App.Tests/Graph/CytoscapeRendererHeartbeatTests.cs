using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Diagnostics;

using Microsoft.Extensions.Logging;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the ADR-037 D8 liveness-heartbeat STATE MACHINE — the actual WebView2-crash-detector
/// deliverable of WP2 (#241) — via the test seam the implementer added on top of the
/// test-engineer's first-pass report (<c>TestScriptInvoker</c>/<c>ProbeHeartbeatOnceAsync</c>/
/// <c>HeartbeatMisses</c>/<c>ResetHeartbeatForTest</c>): each test constructs a REAL
/// <see cref="CytoscapeGraphRenderer"/>, stubs the probe's script-invocation point, and awaits
/// <c>ProbeHeartbeatOnceAsync()</c> — one full probe cycle, synchronously driven, NEVER the real
/// 10 s <see cref="Avalonia.Threading.DispatcherTimer"/> and never a <c>Task.Delay</c>-based wait.
/// <see cref="AvaloniaFactAttribute"/> is used throughout (not a plain <c>[Fact]</c>) because a
/// tripped <c>HeartbeatLost</c> raises <see cref="IGraphRenderer.RendererError"/> through
/// <c>RaiseError</c>, which marshals via <c>Avalonia.Threading.Dispatcher.UIThread</c> — that needs
/// a real headless dispatcher context, unlike the message-handling paths in
/// <see cref="CytoscapeRendererEventLogTests"/> (which raise <c>RendererError</c> directly).
/// </summary>
public sealed class CytoscapeRendererHeartbeatTests
{
    private static Task<string?> Success(string script) => Task.FromResult<string?>("1");

    private static Task<string?> Miss(string script) => Task.FromResult<string?>("0");

    // === 1. Success keeps/resets misses at 0; HeartbeatMissed logs Warn per individual miss ======

    [AvaloniaFact]
    public async Task SuccessfulProbe_KeepsHeartbeatMissesAtZero_NoHeartbeatLogsEmitted()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture) { TestScriptInvoker = Success };

        await renderer.ProbeHeartbeatOnceAsync();

        Assert.Equal(0, renderer.HeartbeatMisses);
        Assert.Empty(capture.EntriesNamed("HeartbeatMissed"));
        Assert.Empty(capture.EntriesNamed("HeartbeatLost"));
    }

    [AvaloniaFact]
    public async Task EachMiss_LogsHeartbeatMissed_Warning_Graph_Renderer_WithIncrementingMissesField()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture) { TestScriptInvoker = Miss };

        await renderer.ProbeHeartbeatOnceAsync(); // miss 1
        await renderer.ProbeHeartbeatOnceAsync(); // miss 2

        var entries = capture.EntriesNamed("HeartbeatMissed");
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal("Graph.Renderer", e.Category);
            Assert.Equal(LogLevel.Warning, e.Level);
        });
        // FIELD NAME + VALUE pin: "misses", incrementing with each individual miss.
        Assert.Equal(1, entries[0].Fields["misses"]);
        Assert.Equal(2, entries[1].Fields["misses"]);
        Assert.Equal(2, renderer.HeartbeatMisses);
    }

    // === 2. Exactly 3 consecutive misses -> exactly one HeartbeatLost + one RendererError ========
    // A 4th consecutive miss must NOT raise a second RendererError ("==3, not >=3").

    [AvaloniaFact]
    public async Task ThreeConsecutiveMisses_LogHeartbeatLostOnce_AndRaiseRendererErrorOnce()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture) { TestScriptInvoker = Miss };
        var rendererErrors = new List<GraphErrorEventArgs>();
        renderer.RendererError += (_, e) => rendererErrors.Add(e);

        await renderer.ProbeHeartbeatOnceAsync(); // miss 1
        await renderer.ProbeHeartbeatOnceAsync(); // miss 2
        Assert.Empty(capture.EntriesNamed("HeartbeatLost"));
        Assert.Empty(rendererErrors);

        await renderer.ProbeHeartbeatOnceAsync(); // miss 3 -> trips

        Assert.Equal(3, renderer.HeartbeatMisses);
        var lost = Assert.Single(capture.EntriesNamed("HeartbeatLost"));
        Assert.Equal("Graph.Renderer", lost.Category);
        Assert.Equal(LogLevel.Error, lost.Level);
        // FIELD NAME + VALUE pin: HeartbeatLost{misses=3}.
        Assert.Equal(3, lost.Fields["misses"]);
        var error = Assert.Single(rendererErrors);
        Assert.Equal("renderer", error.Source);
        Assert.Contains("stopped responding", error.Message);
    }

    [AvaloniaFact]
    public async Task AFourthConsecutiveMiss_DoesNotRaiseASecondRendererError_ButStillLogsHeartbeatMissed()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture) { TestScriptInvoker = Miss };
        var rendererErrors = new List<GraphErrorEventArgs>();
        renderer.RendererError += (_, e) => rendererErrors.Add(e);

        await renderer.ProbeHeartbeatOnceAsync(); // miss 1
        await renderer.ProbeHeartbeatOnceAsync(); // miss 2
        await renderer.ProbeHeartbeatOnceAsync(); // miss 3 -> HeartbeatLost + RendererError (once)
        Assert.Single(rendererErrors);
        Assert.Single(capture.EntriesNamed("HeartbeatLost"));

        await renderer.ProbeHeartbeatOnceAsync(); // miss 4 -> the "==3 not >=3" contract

        Assert.Equal(4, renderer.HeartbeatMisses);
        // The Warn trail keeps going per miss...
        Assert.Equal(4, capture.EntriesNamed("HeartbeatMissed").Count);
        // ...but the Error banner and the RendererError raise do NOT repeat.
        Assert.Single(capture.EntriesNamed("HeartbeatLost"));
        Assert.Single(rendererErrors);
    }

    // === 3. A throwing TestScriptInvoker counts as a miss (the existing catch-fallback path) =====

    [AvaloniaFact]
    public async Task ThrowingScriptInvoker_SynchronousThrow_CountsAsAMiss()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture)
        {
            TestScriptInvoker = _ => throw new InvalidOperationException("wedged InvokeScript"),
        };

        await renderer.ProbeHeartbeatOnceAsync();

        Assert.Equal(1, renderer.HeartbeatMisses);
        var entry = Assert.Single(capture.EntriesNamed("HeartbeatMissed"));
        Assert.Equal(1, entry.Fields["misses"]);
    }

    [AvaloniaFact]
    public async Task ThrowingScriptInvoker_FaultedTask_CountsAsAMiss()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture)
        {
            TestScriptInvoker = _ => Task.FromException<string?>(new InvalidOperationException("dead child process")),
        };

        await renderer.ProbeHeartbeatOnceAsync();

        Assert.Equal(1, renderer.HeartbeatMisses);
        Assert.Single(capture.EntriesNamed("HeartbeatMissed"));
    }

    [AvaloniaFact]
    public async Task ThreeConsecutiveThrowingProbes_StillTripHeartbeatLost()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture)
        {
            TestScriptInvoker = _ => throw new InvalidOperationException("wedged"),
        };

        await renderer.ProbeHeartbeatOnceAsync();
        await renderer.ProbeHeartbeatOnceAsync();
        await renderer.ProbeHeartbeatOnceAsync();

        Assert.Equal(3, renderer.HeartbeatMisses);
        Assert.Single(capture.EntriesNamed("HeartbeatLost"));
    }

    // === 4. Reset-on-success: the streak does NOT carry across a success ==========================

    [AvaloniaFact]
    public async Task SuccessResetsTheStreak_SoItTakesThreeFreshConsecutiveMissesToTripHeartbeatLostAgain()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture) { TestScriptInvoker = Miss };

        await renderer.ProbeHeartbeatOnceAsync(); // miss 1
        await renderer.ProbeHeartbeatOnceAsync(); // miss 2
        Assert.Equal(2, renderer.HeartbeatMisses);

        renderer.TestScriptInvoker = Success;
        await renderer.ProbeHeartbeatOnceAsync(); // success -> resets to 0
        Assert.Equal(0, renderer.HeartbeatMisses);

        renderer.TestScriptInvoker = Miss;
        await renderer.ProbeHeartbeatOnceAsync(); // fresh miss 1
        await renderer.ProbeHeartbeatOnceAsync(); // fresh miss 2

        // NOT 4 -- the pre-success streak must not carry across the success.
        Assert.Equal(2, renderer.HeartbeatMisses);
        Assert.Empty(capture.EntriesNamed("HeartbeatLost")); // only 2 FRESH misses so far -- not tripped

        await renderer.ProbeHeartbeatOnceAsync(); // fresh miss 3 -> NOW it trips

        Assert.Equal(3, renderer.HeartbeatMisses);
        Assert.Single(capture.EntriesNamed("HeartbeatLost"));
    }

    /// <summary><see cref="CytoscapeGraphRenderer.ResetHeartbeatForTest"/> itself (distinct from
    /// a production success probe) also clears the streak — the test-arrangement primitive the
    /// other tests could use instead of a real success probe.</summary>
    [AvaloniaFact]
    public async Task ResetHeartbeatForTest_ZeroesAnInProgressStreak()
    {
        var capture = new CapturingLoggerFactory();
        var renderer = new CytoscapeGraphRenderer(capture) { TestScriptInvoker = Miss };

        await renderer.ProbeHeartbeatOnceAsync();
        await renderer.ProbeHeartbeatOnceAsync();
        Assert.Equal(2, renderer.HeartbeatMisses);

        renderer.ResetHeartbeatForTest();

        Assert.Equal(0, renderer.HeartbeatMisses);
    }
}
