namespace GroupWeaver.Tests;

/// <summary>
/// xUnit trait conventions for GroupWeaver tests.
/// </summary>
/// <remarks>
/// AD-dependent integration tests must carry
/// <c>[Trait(TestCategories.Category, TestCategories.RequiresAd)]</c>. They run locally
/// against the live <c>OU=AGDLP-Lab,DC=agdlp,DC=lab</c> fixtures and are excluded in CI
/// by <c>tools/build.ps1 -SkipAdTests</c> (test filter <c>Category!=RequiresAd</c>).
/// No AD-touching tests exist yet; the first ones land with the providers (AP 1.4+).
/// </remarks>
public static class TestCategories
{
    /// <summary>The trait name under which test categories are recorded.</summary>
    public const string Category = "Category";

    /// <summary>Tests that need the live AGDLP-Lab AD fixtures; excluded in CI.</summary>
    public const string RequiresAd = "RequiresAd";
}
