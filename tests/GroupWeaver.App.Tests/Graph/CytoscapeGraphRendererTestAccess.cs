using System.Reflection;

using GroupWeaver.App.Graph;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Reflection-only access to the private, WebView-INDEPENDENT slice of
/// <see cref="CytoscapeGraphRenderer"/>'s ADR-037 WP2 (#241) event surface.
///
/// <para><b>Why reflection, not a fake:</b> <c>CytoscapeGraphRenderer</c> is WebView-bound with
/// no unit-testable surface (see <c>GraphRendererSeamTests</c>/<c>CytoscapePngDecodeTests</c>) —
/// every render/dispatch/heartbeat-probe path requires a live <c>NativeWebView</c> (a sealed-in-
/// practice, third-party Avalonia control this test project cannot substitute) or a REAL 10 s/60 s
/// wall-clock wait (no injectable clock/interval exists). <see cref="HandleMessage"/> and
/// <see cref="EnterSingleFlight"/> are the ONLY two ADR-037 hot log sites that touch neither: both
/// are synchronous, private, and operate purely on parsed JSON / primitive state — so invoking them
/// directly is a deterministic, non-flaky exercise of REAL production code, not a fake standing in
/// for it. Nothing here fabricates behavior; it only reaches methods the production ctor/ready-gate
/// would otherwise require a live page to drive.</para>
///
/// <para><b>Product finding (reported, not fixed here — tests/ ownership only):</b> the remaining
/// WP2 surface — <c>RenderDispatchStarted/Completed</c>, <c>RenderTimeout{phase}</c>,
/// <c>AdapterCreated/Destroyed</c>, <c>FireAndForgetFailed</c>, <c>ExportPngFailed/Completed</c>,
/// <c>BridgeChunkSent</c>, and the WHOLE heartbeat tick/miss/lost state machine — has NO
/// deterministic seam at all (real WebView required, or a real-time <c>DispatcherTimer</c> with
/// fixed private intervals). A small internal seam would unlock it: e.g. bump
/// <c>HandleMessage</c>/<c>EnterSingleFlight</c> to <c>internal</c> (retiring this reflection
/// helper), an <c>internal Task ProbeHeartbeatAsync()</c> + <c>internal void SetHeartbeatMisses(int)</c>
/// pair (or an injectable <see cref="TimeSpan"/> heartbeat interval/probe timeout), and an
/// injectable script-invoker seam (e.g. <c>Func&lt;string, Task&lt;string?&gt;&gt;</c>) so
/// dispatch/probe failures can be simulated without a native child. See the WP2 test report for
/// the full list.</para>
/// </summary>
internal static class CytoscapeGraphRendererTestAccess
{
    /// <summary>Invokes the private <c>HandleMessage(string)</c> — the bridge inbound-message
    /// router (ADR-037 D5/D8: BridgeMessageReceived/BridgeReady/JsErrorReported/
    /// BridgeUnknownMessage + the heartbeat miss-reset). Unwraps
    /// <see cref="TargetInvocationException"/> so callers see the SAME exception the real
    /// dispatcher-posted call site would observe.</summary>
    public static void InvokeHandleMessage(CytoscapeGraphRenderer renderer, string body)
    {
        var method = typeof(CytoscapeGraphRenderer).GetMethod(
            "HandleMessage", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(CytoscapeGraphRenderer), "HandleMessage");
        try
        {
            method.Invoke(renderer, [body]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>Invokes the private <c>EnterSingleFlight(string)</c> guard (ADR-037 D5:
    /// <c>SingleFlightViolation</c> Error, log-then-throw). Unwraps
    /// <see cref="TargetInvocationException"/> so a re-entrant call's
    /// <see cref="InvalidOperationException"/> surfaces exactly as production callers see it.</summary>
    public static void InvokeEnterSingleFlight(CytoscapeGraphRenderer renderer, string operation)
    {
        var method = typeof(CytoscapeGraphRenderer).GetMethod(
            "EnterSingleFlight", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(CytoscapeGraphRenderer), "EnterSingleFlight");
        try
        {
            method.Invoke(renderer, [operation]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>Reads the private <c>_heartbeatMisses</c> counter (ADR-037 D8) — lets a test prove
    /// <see cref="InvokeHandleMessage"/>'s miss-reset-on-parseable-message rule without ever
    /// starting the real timer.</summary>
    public static int GetHeartbeatMisses(CytoscapeGraphRenderer renderer) =>
        (int)(HeartbeatMissesField.GetValue(renderer) ?? 0);

    /// <summary>Seeds the private <c>_heartbeatMisses</c> counter so a test can arrange a
    /// pre-existing miss streak before driving a message through
    /// <see cref="InvokeHandleMessage"/>.</summary>
    public static void SetHeartbeatMisses(CytoscapeGraphRenderer renderer, int value) =>
        HeartbeatMissesField.SetValue(renderer, value);

    private static readonly FieldInfo HeartbeatMissesField =
        typeof(CytoscapeGraphRenderer).GetField("_heartbeatMisses", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(CytoscapeGraphRenderer), "_heartbeatMisses");
}
