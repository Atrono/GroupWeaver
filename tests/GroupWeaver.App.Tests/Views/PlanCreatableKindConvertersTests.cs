using System.Globalization;

using GroupWeaver.App.Views;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the AP 4.2.3 kind-selector legibility fix: the add-object kind ComboBox renders each
/// <see cref="PlanCreatableKind"/> through <see cref="PlanCreatableKindConverters"/> so it speaks
/// the product's vocabulary ("Domain-local group") instead of leaking the raw camel-case enum
/// name ("DomainLocalGroup") the ui-verifier FAILed. These spaced strings are LOAD-BEARING — they
/// are the exact text the kind combo and the Objects-row tooltip (<c>PlanNodeRowModel.KindLabel</c>,
/// which resolves through the same <see cref="PlanCreatableKindConverters.FriendlyLabel"/> mapping)
/// must show; this test fails the instant either rendering seam diverges from the friendly labels.
///
/// Style mirrors the sibling <see cref="SeverityConvertersTests"/> parity oracle: assert the static
/// mapping AND invoke the <see cref="System.IValueConverter"/> through its <c>Convert</c> binding
/// seam (exactly as the XAML <c>ItemTemplate</c> does), never a private helper, so the four labels
/// are pinned both at their source and at the seam the combo actually binds.
/// </summary>
public sealed class PlanCreatableKindConvertersTests
{
    // The four friendly, spaced labels the kind selector must render (AP 4.2.3).
    [Theory]
    [InlineData(PlanCreatableKind.User, "User")]
    [InlineData(PlanCreatableKind.GlobalGroup, "Global group")]
    [InlineData(PlanCreatableKind.DomainLocalGroup, "Domain-local group")]
    [InlineData(PlanCreatableKind.UniversalGroup, "Universal group")]
    public void FriendlyLabel_MapsEachKind_ToItsSpacedLabel(PlanCreatableKind kind, string label)
    {
        Assert.Equal(label, PlanCreatableKindConverters.FriendlyLabel(kind));
    }

    // The IValueConverter binding seam (the combo's ItemTemplate path) yields the same labels.
    [Theory]
    [InlineData(PlanCreatableKind.User, "User")]
    [InlineData(PlanCreatableKind.GlobalGroup, "Global group")]
    [InlineData(PlanCreatableKind.DomainLocalGroup, "Domain-local group")]
    [InlineData(PlanCreatableKind.UniversalGroup, "Universal group")]
    public void ToFriendlyLabel_ConvertsEachKind_ToItsSpacedLabel(PlanCreatableKind kind, string label)
    {
        var rendered = PlanCreatableKindConverters.ToFriendlyLabel.Convert(
            kind, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(label, Assert.IsType<string>(rendered));
    }

    /// <summary>The four labels are distinct — a collision would make the kind selector
    /// ambiguous (the whole point of the friendly mapping is an unambiguous, spaced label).</summary>
    [Fact]
    public void FriendlyLabel_IsInjective_AcrossTheFourKinds()
    {
        var labels = Enum.GetValues<PlanCreatableKind>()
            .Select(PlanCreatableKindConverters.FriendlyLabel)
            .ToArray();

        Assert.Equal(4, labels.Length);
        Assert.Equal(4, labels.Distinct().Count());

        // No leaked raw camel-case group enum name — every GROUP label is spaced (the regression
        // the ui-verifier FAILed was "DomainLocalGroup"/"GlobalGroup"/"UniversalGroup"). "User" is
        // a single word by design, so the spacing guarantee applies to the three group kinds.
        var groupLabels = new[]
        {
            PlanCreatableKindConverters.FriendlyLabel(PlanCreatableKind.GlobalGroup),
            PlanCreatableKindConverters.FriendlyLabel(PlanCreatableKind.DomainLocalGroup),
            PlanCreatableKindConverters.FriendlyLabel(PlanCreatableKind.UniversalGroup),
        };
        Assert.All(groupLabels, l => Assert.Contains(' ', l));
        Assert.All(
            groupLabels, l => Assert.DoesNotContain(l, Enum.GetNames<PlanCreatableKind>()));
    }
}
