using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the fixed rule ids (ADR-008). These strings appear in user-edited
/// ruleset files, AP 3.2 findings, and the AP 3.3 settings UI — changing
/// one breaks every shared community ruleset, so they are pinned verbatim
/// (kebab-case, exactly these spellings).
/// </summary>
public class RuleIdsTests
{
    [Fact]
    public void Nesting_IsPinned()
    {
        Assert.Equal("nesting", RuleIds.Nesting);
    }

    [Fact]
    public void Circular_IsPinned()
    {
        Assert.Equal("circular", RuleIds.Circular);
    }

    [Fact]
    public void EmptyGroup_IsPinnedKebabCase()
    {
        Assert.Equal("empty-group", RuleIds.EmptyGroup);
    }
}
