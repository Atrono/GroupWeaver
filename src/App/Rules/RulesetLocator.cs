using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Rules;

/// <summary>
/// Locates the user ruleset and applies ADR-008's WHOLE-FILE precedence — no
/// merging, ever: a valid user file wins outright; an invalid (or unreadable)
/// one falls back to the embedded default WITH the loader's path-addressed
/// errors; an absent one is the embedded default with no errors. The locator
/// never throws on bad user input and never materializes the user file — the
/// default is written only by AP 3.3's first save, never auto-copied. Error
/// surfacing (log/status bar) is AP 3.3's settings UI; until then callers see
/// the errors on <see cref="EffectiveRuleset.Errors"/>. Pinned by
/// <c>tests/GroupWeaver.App.Tests/Rules/RulesetLocatorTests.cs</c>.
/// </summary>
public sealed class RulesetLocator
{
    /// <summary>Production locator: the repo-wide user-persistence convention
    /// is <c>%APPDATA%\GroupWeaver\</c> (ADR-008).</summary>
    public RulesetLocator()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    /// <summary>Test seam: the same layout under an injected base directory.</summary>
    public RulesetLocator(string baseDirectory)
    {
        UserRulesetPath = Path.Combine(baseDirectory, "GroupWeaver", "ruleset.jsonc");
    }

    /// <summary>Full path of the user ruleset file (which may not exist).</summary>
    public string UserRulesetPath { get; }

    /// <summary>Resolves the ruleset the app runs on right now — never throws
    /// on a missing, malformed, or unreadable user file.</summary>
    public EffectiveRuleset LoadEffective()
    {
        string jsonText;
        try
        {
            jsonText = File.ReadAllText(UserRulesetPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Present but unreadable (locked, ACL'd, ...): same degradation as an
            // invalid file — run on the default and say why, in the loader's shape.
            return new EffectiveRuleset(
                RulesetLoader.LoadDefault(),
                FromUserFile: false,
                [new RulesetValidationError("$", $"the user ruleset '{UserRulesetPath}' could not be read: {ex.Message}")]);
        }

        var result = RulesetLoader.Load(jsonText);
        return result.Success
            ? new EffectiveRuleset(result.Ruleset, FromUserFile: true, [])
            : new EffectiveRuleset(RulesetLoader.LoadDefault(), FromUserFile: false, result.Errors);
    }
}

/// <summary>The outcome of <see cref="RulesetLocator.LoadEffective"/>:
/// <see cref="Errors"/> non-empty means a user file exists but was rejected —
/// <see cref="Ruleset"/> is then the embedded default the app runs on instead
/// (and <see cref="FromUserFile"/> is false).</summary>
public sealed record EffectiveRuleset(
    Ruleset Ruleset, bool FromUserFile, IReadOnlyList<RulesetValidationError> Errors);
