using System;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// An exception with an INJECTED stack trace, for the D9 redaction pins: a merely-constructed
/// exception has a null <see cref="Exception.StackTrace"/>, and a genuinely thrown one carries
/// the real test-host frames (with real user-profile paths) instead of the hostile DN the
/// tests must prove gets scrubbed. Overriding the property is the only deterministic way to
/// put a DN into the stack lane.
/// </summary>
internal sealed class StackedException(string message, string stack) : Exception(message)
{
    public override string StackTrace => stack;
}
