using System;
using System.Reflection;

using GroupWeaver.App.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Reflection access to the ADR-037 D9 instance redaction core the WP10 RED phase pins
/// BEFORE it exists: <c>GroupWeaver.App.Diagnostics.Redaction</c> — the constructible twin
/// behind the static <see cref="Redactor"/> facade (whose call sites stay static and
/// unchanged). Reflection keeps the whole suite compiling against the WP1 identity stub;
/// every accessor fails with the pinned shape in its message, so the missing member IS the
/// red assertion. Once the implementer lands the type these helpers bind to it directly.
///
/// <para><b>The pinned shape</b> (the implementer builds to THIS): static
/// <c>CreateSalted()</c> (fresh random salt — a new "session") and static <c>Identity</c>
/// (the <c>--log-plain</c> pass-through singleton); instance <c>Mode</c> (<c>"redacted"</c> /
/// <c>"identity"</c>), <c>Dn/Host/Path/RunFile/Scrub(string?)</c> and <c>Learn(string?)</c>.</para>
/// </summary>
internal static class RedactionSurface
{
    internal const string TypeName = "GroupWeaver.App.Diagnostics.Redaction";

    /// <summary>The instance core type — RED with the pinned shape while it is missing.</summary>
    internal static Type Require()
    {
        var type = typeof(Redactor).Assembly.GetType(TypeName);
        Assert.True(
            type is not null,
            $"ADR-037 D9 (WP10 #249) pins the instance redaction core '{TypeName}': "
            + "static CreateSalted()/Identity, instance Mode/Dn/Host/Path/RunFile/Scrub/Learn "
            + "behind the unchanged static Redactor facade.");
        return type!;
    }

    /// <summary>A fresh salted instance — a new "session" (unlinkable tokens).</summary>
    internal static object CreateSalted(Type type)
    {
        var factory = type.GetMethod(
            "CreateSalted", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        Assert.True(
            factory is not null,
            "Redaction.CreateSalted() — a parameterless static factory minting a fresh random session salt.");
        return factory!.Invoke(null, null)!;
    }

    /// <summary>The pass-through singleton the <c>--log-plain</c> path installs.</summary>
    internal static object Identity(Type type)
    {
        var identity = type.GetProperty("Identity", BindingFlags.Public | BindingFlags.Static);
        Assert.True(
            identity is not null,
            "Redaction.Identity — the static pass-through instance --log-plain swaps in.");
        return identity!.GetValue(null)!;
    }

    /// <summary>The instance's mode string: <c>"redacted"</c> or <c>"identity"</c>.</summary>
    internal static string Mode(object redaction)
    {
        var mode = redaction.GetType().GetProperty("Mode", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(mode is not null, "Redaction.Mode — \"redacted\" (salted) / \"identity\" (plain).");
        return (string)mode!.GetValue(redaction)!;
    }

    /// <summary>Invokes an instance helper (<c>Dn</c>/<c>Host</c>/<c>Path</c>/<c>RunFile</c>/<c>Scrub</c>).</summary>
    internal static string? Invoke(object redaction, string method, string? value)
    {
        var helper = redaction.GetType().GetMethod(method, new[] { typeof(string) });
        Assert.True(helper is not null, $"Redaction.{method}(string?) — the typed instance helper.");
        return (string?)helper!.Invoke(redaction, new object?[] { value });
    }

    /// <summary>Registers a connect-time server/baseDn string for <c>Scrub</c>.</summary>
    internal static void Learn(object redaction, string? value)
    {
        var learn = redaction.GetType().GetMethod("Learn", new[] { typeof(string) });
        Assert.True(learn is not null, "Redaction.Learn(string?) — connect-time server/baseDn registration.");
        learn!.Invoke(redaction, new object?[] { value });
    }
}
