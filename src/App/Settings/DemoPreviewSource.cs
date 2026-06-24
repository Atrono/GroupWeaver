using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

namespace GroupWeaver.App.Settings;

/// <summary>
/// Loads and caches the EMBEDDED DEMO snapshot plus the DEFAULT-ruleset baseline
/// <see cref="AuditSummary"/> over it — the consistent, safe, deterministic preview
/// baseline behind the Settings editor's live finding-count + diff-from-default
/// (WP6b / #164).
///
/// <para><b>Demo-only, read-only.</b> This is the PRODUCTION analogue of the test
/// <c>DemoProviderFixture.FullSnapshot</c>: a <see cref="DemoProvider"/> (embedded
/// JSON, no AD, no network) connected and <c>LoadScopeAsync</c>'d over the demo root.
/// It NEVER touches the live directory and writes nothing — the editor preview is a
/// pure in-memory eval, independent of any live connection.</para>
///
/// <para>Both the snapshot load and the baseline summary are computed LAZILY on first
/// preview and cached for the lifetime of the editor (a single shared
/// <see cref="Task{T}"/>, so concurrent first-preview callers share one load). The
/// load is async; callers await it OFF the UI thread before evaluating, then marshal
/// the result back (see <c>SettingsViewModel</c>). A load failure degrades gracefully:
/// <see cref="EnsureLoadedAsync"/> returns <c>null</c> and the preview hides.</para>
/// </summary>
public sealed class DemoPreviewSource
{
    private readonly IDirectoryProvider _provider;
    private readonly Lazy<Task<DemoPreviewBaseline?>> _baseline;

    /// <summary>Creates a source over a fresh embedded <see cref="DemoProvider"/>.</summary>
    public DemoPreviewSource()
        : this(new DemoProvider())
    {
    }

    /// <summary>Creates a source over <paramref name="provider"/> (the demo provider seam —
    /// the production path passes a <see cref="DemoProvider"/>; tests may inject their own).</summary>
    public DemoPreviewSource(IDirectoryProvider provider)
    {
        _provider = provider;
        _baseline = new Lazy<Task<DemoPreviewBaseline?>>(LoadAsync);
    }

    /// <summary>Lazily loads (once, cached) the demo snapshot and the default-ruleset
    /// baseline summary over it. Returns <c>null</c> if the demo data cannot load for ANY
    /// reason — the preview is an affordance, not a gate, so it degrades silently.</summary>
    public Task<DemoPreviewBaseline?> EnsureLoadedAsync() => _baseline.Value;

    private async Task<DemoPreviewBaseline?> LoadAsync()
    {
        try
        {
            await _provider.ConnectAsync().ConfigureAwait(false);
            var rootDn = await ResolveRootDnAsync().ConfigureAwait(false);
            if (rootDn is null)
            {
                return null;
            }

            var snapshot = await _provider.LoadScopeAsync(rootDn).ConfigureAwait(false);

            // The default-ruleset summary over the demo snapshot is the diff baseline,
            // computed ONCE here and cached with the snapshot.
            var @default = RulesetLoader.LoadDefault();
            var report = RuleEngine.Evaluate(snapshot, @default);
            var baselineSummary = AuditSummary.Compute(report, snapshot, @default);

            return new DemoPreviewBaseline(snapshot, baselineSummary);
        }
        catch (Exception)
        {
            // A corrupt/missing embedded dataset is a build defect, but the editor must
            // never crash over a preview — hide it instead.
            return null;
        }
    }

    /// <summary>The demo root scope (the broadest candidate — the OU that is an ancestor of
    /// every other root candidate). The production analogue of the fixture's hard-coded
    /// <c>rootDn</c>, derived from the provider so no DN is duplicated app-side.</summary>
    private async Task<string?> ResolveRootDnAsync()
    {
        var candidates = await _provider.GetRootCandidatesAsync().ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        // The full-scope root is the candidate under which every other candidate sits
        // (RelativeDepth >= 0 for all). The demo dataset has exactly one such OU root.
        foreach (var candidate in candidates)
        {
            if (candidates.All(other => DnPath.RelativeDepth(other.Dn, candidate.Dn) >= 0))
            {
                return candidate.Dn;
            }
        }

        // No single ancestor (unexpected for the demo dataset): fall back to the shallowest.
        return candidates
            .OrderBy(c => c.Dn.Length)
            .First()
            .Dn;
    }
}

/// <summary>The cached demo preview baseline (WP6b / #164): the embedded demo snapshot
/// and the DEFAULT ruleset's <see cref="AuditSummary"/> over it (the diff-from-default
/// reference). Both are immutable for the editor's lifetime.</summary>
public sealed record DemoPreviewBaseline(DirectorySnapshot Snapshot, AuditSummary DefaultSummary);
