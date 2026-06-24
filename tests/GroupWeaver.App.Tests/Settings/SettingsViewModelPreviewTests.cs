using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using GroupWeaver.App.Settings;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the <see cref="SettingsViewModel"/> live-preview wiring (WP6b / #164): the
/// <c>Preview</c> / <c>PreviewComputing</c> / <c>PreviewUnavailable</c> / <c>HasPreview</c>
/// state machine driven by the raw-editor text, through the internal test seams
/// (<see cref="SettingsViewModel.LoadFrom(Ruleset, DemoPreviewSource)"/> +
/// the <see cref="DemoPreviewSource(IDirectoryProvider)"/> ctor). The contract:
/// <list type="bullet">
/// <item><b>Valid text -&gt; preview</b> — setting <c>RawEditorText</c> to valid JSONC
/// computes a preview whose counts equal the AuditSummary baseline (default = 19/4/3/12).</item>
/// <item><b>Invalid text -&gt; no preview</b> — setting <c>RawEditorText</c> to invalid
/// JSONC clears the preview (<c>HasPreview</c> false / <c>Preview</c> null); the validation
/// band carries the errors separately.</item>
/// <item><b>Degrade gracefully</b> — a <see cref="DemoPreviewSource"/> over a provider that
/// THROWS sets <c>PreviewUnavailable</c> true, <c>Preview</c> null, and never throws.</item>
/// </list>
///
/// <para>The preview eval is async (off-thread <see cref="Task.Run"/> behind a monotonic
/// generation token, ConfigureAwait(true) onto whatever context — none, in these plain
/// VM tests, so continuations land on the thread pool). Each test awaits the settled
/// state deterministically via <see cref="WaitForPreviewSettledAsync"/> (polls the
/// observable flags, never a fixed sleep), then asserts. The injected demo provider is a
/// real <see cref="DemoProvider"/> (the 19-finding baseline authority) carrying the
/// GG_Circle cycle — every Evaluate must terminate, hence the per-test Timeout. Counts are
/// compared as projections, never <see cref="RulesetPreview"/> record identity.</para>
/// </summary>
public sealed class SettingsViewModelPreviewTests
{
    // === valid text -> preview with the baseline counts =================================

    /// <summary>
    /// A VM seeded from the default ruleset over a working demo source computes a preview
    /// whose counts are the AP 3.2 baseline (Total 19 / 4 / 3 / 12) and whose per-severity
    /// deltas are all zero (default vs default baseline).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task LoadFromDefault_OverWorkingDemoSource_ComputesBaselinePreview()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault(), WorkingDemoSource());

        await WaitForPreviewSettledAsync(vm);

        Assert.True(vm.HasPreview);
        Assert.False(vm.PreviewUnavailable);
        Assert.False(vm.PreviewComputing);

        var preview = Assert.IsType<RulesetPreview>(vm.Preview);
        Assert.Equal(19, preview.Total);
        Assert.Equal(4, preview.Critical);
        Assert.Equal(3, preview.Warning);
        Assert.Equal(12, preview.Info);

        // Default vs default baseline => all-zero deltas + no class deltas.
        Assert.Equal(0, preview.TotalDelta.Value);
        Assert.Empty(preview.RuleClassDeltas);
    }

    /// <summary>
    /// Editing the raw text to a VALID ruleset that ADDS findings (ignore list cleared,
    /// AP 3.2 both-ways) recomputes the preview from the re-parsed ruleset: the counts and
    /// the diff-from-default reflect the edit (Info 12 -&gt; 14, Total +2 caution).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RawEditorText_ValidEditAddingFindings_RecomputesPreviewFromReParsedRuleset()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault(), WorkingDemoSource());
        await WaitForPreviewSettledAsync(vm);

        var edited = RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };
        vm.RawEditorText = RulesetSerializer.Serialize(edited);

        await WaitForPreviewSettledAsync(vm);

        Assert.True(vm.RawEditorValid);
        Assert.True(vm.HasPreview);
        var preview = Assert.IsType<RulesetPreview>(vm.Preview);
        Assert.Equal(21, preview.Total);
        Assert.Equal(14, preview.Info);
        Assert.Equal(2, preview.TotalDelta.Value);
        Assert.True(preview.TotalDelta.IsCaution);
        Assert.Equal("+2", preview.TotalDelta.DisplayValue);
    }

    // === invalid text -> no preview =====================================================

    /// <summary>
    /// Setting <c>RawEditorText</c> to MALFORMED JSONC clears the preview at once:
    /// <c>HasPreview</c> false, <c>Preview</c> null, <c>PreviewComputing</c> false (no
    /// async eval is even scheduled for invalid text). The validation band's
    /// <c>RawEditorErrors</c> carries the failure separately.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RawEditorText_InvalidJsonc_ClearsPreview_NoComputeScheduled()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault(), WorkingDemoSource());
        await WaitForPreviewSettledAsync(vm);
        Assert.True(vm.HasPreview); // a valid preview is present first

        vm.RawEditorText = "{ \"schemaVersion\": 1, \"name\": \"x\", ";

        // Invalid text clears the preview SYNCHRONOUSLY (no async race to wait on).
        Assert.False(vm.RawEditorValid);
        Assert.False(vm.HasPreview);
        Assert.Null(vm.Preview);
        Assert.False(vm.PreviewComputing);
        Assert.False(vm.PreviewUnavailable);
        Assert.NotEmpty(vm.RawEditorErrors); // the band explains why, independently
    }

    /// <summary>
    /// Recovering from invalid -&gt; valid text re-computes the preview: after a malformed
    /// edit clears it, a valid edit brings <c>HasPreview</c> back true with correct counts.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RawEditorText_InvalidThenValid_PreviewClearsThenReturns()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault(), WorkingDemoSource());
        await WaitForPreviewSettledAsync(vm);

        vm.RawEditorText = "not json at all";
        Assert.False(vm.HasPreview);

        vm.RawEditorText = RulesetSerializer.Serialize(RulesetLoader.LoadDefault());
        await WaitForPreviewSettledAsync(vm);

        Assert.True(vm.RawEditorValid);
        Assert.True(vm.HasPreview);
        var preview = Assert.IsType<RulesetPreview>(vm.Preview);
        Assert.Equal(19, preview.Total);
    }

    // === degrade gracefully: demo source fails ==========================================

    /// <summary>
    /// A <see cref="DemoPreviewSource"/> over a provider whose <c>ConnectAsync</c> THROWS
    /// must degrade silently: the VM sets <c>PreviewUnavailable</c> true, leaves
    /// <c>Preview</c> null / <c>HasPreview</c> false, and never lets the exception escape.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RawEditorText_ValidEdit_DemoSourceThrows_SetsUnavailable_NeverThrows()
    {
        var failing = new DemoPreviewSource(new ThrowingDirectoryProvider());

        var ex = await Record.ExceptionAsync(async () =>
        {
            var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault(), failing);
            await WaitForPreviewSettledAsync(vm);

            Assert.True(vm.PreviewUnavailable);
            Assert.False(vm.HasPreview);
            Assert.Null(vm.Preview);
            Assert.False(vm.PreviewComputing);
        });

        Assert.Null(ex);
    }

    // === helpers ========================================================================

    /// <summary>A demo preview source over a fresh real <see cref="DemoProvider"/> — the
    /// embedded 19-finding dataset, no AD, no network.</summary>
    private static DemoPreviewSource WorkingDemoSource() => new(new DemoProvider());

    /// <summary>Awaits the preview state machine settling: <c>PreviewComputing</c> false AND
    /// either a preview is present OR it is unavailable. Polls the observable flags (the eval
    /// completes on a thread-pool continuation in these dispatcher-less tests) — never a
    /// fixed sleep. The enclosing Fact Timeout is the hard ceiling; this spins shorter so a
    /// genuine hang still fails as a timeout, not a false pass.</summary>
    private static async Task WaitForPreviewSettledAsync(SettingsViewModel vm)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (!vm.PreviewComputing && (vm.HasPreview || vm.PreviewUnavailable))
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail(
            $"preview did not settle within 30s (computing={vm.PreviewComputing}, " +
            $"hasPreview={vm.HasPreview}, unavailable={vm.PreviewUnavailable})");
    }

    /// <summary>A provider whose every call throws — the degrade-gracefully fixture for
    /// <see cref="DemoPreviewSource"/> (a corrupt/missing embedded dataset analogue). The
    /// source's <c>LoadAsync</c> catch turns this into a null baseline, never a crash.</summary>
    private sealed class ThrowingDirectoryProvider : IDirectoryProvider
    {
        public Task<DirectoryConnection> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("demo provider deliberately unavailable");

        public Task<IReadOnlyList<AdObject>> GetRootCandidatesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("demo provider deliberately unavailable");

        public Task<DirectorySnapshot> LoadScopeAsync(string baseDn, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("demo provider deliberately unavailable");

        public Task<AdObject?> GetObjectAsync(string dn, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("demo provider deliberately unavailable");

        public Task<IReadOnlyList<AdObject>> GetMembersAsync(string groupDn, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("demo provider deliberately unavailable");
    }
}
