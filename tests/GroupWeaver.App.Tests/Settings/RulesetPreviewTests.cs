using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using GroupWeaver.App.Settings;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins <see cref="RulesetPreview.Compute"/> (WP6b / #164 — the Settings editor's live
/// finding-count + diff-from-default projection) against the SAME embedded demo snapshot
/// the AP 3.2 baseline pins. The contract (.claude/rules/rule-engine.md + rule-model.md):
/// <list type="bullet">
/// <item><b>Counts == AuditSummary</b> — <see cref="RulesetPreview.Total"/>/Critical/
/// Warning/Info equal <see cref="AuditSummary.Compute"/> over the same snapshot + ruleset,
/// for BOTH the default ruleset (Total 19 / 4 / 3 / 12) and an edited one that adds
/// findings.</item>
/// <item><b>Diff-from-default</b> — every per-severity delta is <c>current - baseline</c>,
/// with the signed <see cref="PreviewDelta.DisplayValue"/> formatting (<c>+3</c> / <c>-1</c>
/// / <c>0</c>) and the <see cref="PreviewDelta.IsCaution"/> rule (MORE than default is the
/// only cautionary direction).</item>
/// <item><b>Per-rule-class deltas</b> — the UNION of both summaries' <c>ByRuleClass</c>
/// keys, canonical <see cref="Ruleset.EnumerateRules"/> order, zero-delta classes omitted,
/// a default-only (removed) class appended.</item>
/// <item><b>Default vs default</b> — all-zero deltas + an EMPTY
/// <see cref="RulesetPreview.RuleClassDeltas"/>.</item>
/// </list>
///
/// <para>The demo snapshot carries the GG_Circle_A &lt;-&gt; GG_Circle_B cycle, so every
/// Evaluate over it runs off-thread under a Timeout — termination proven, never trusted.
/// The demo snapshot is loaded ONCE per test class via the real <see cref="DemoProvider"/>
/// (App.Tests does not reference the Core test project's <c>DemoProviderFixture</c>).
/// Deltas are compared as (label, value) PROJECTIONS, never <see cref="PreviewDelta"/>
/// record identity. If the 19/4/3/12 baseline drifts, suspect the dataset or the engine
/// first, never this table.</para>
/// </summary>
public sealed class RulesetPreviewTests : IClassFixture<RulesetPreviewTests.DemoSnapshotFixture>
{
    private readonly DemoSnapshotFixture _fixture;

    public RulesetPreviewTests(DemoSnapshotFixture fixture) => _fixture = fixture;

    /// <summary>The bindable label the class-delta list carries for each rule — the rule's
    /// <see cref="RuleSummary.DisplayName"/> (surfaced through <see cref="Ruleset.EnumerateRules"/>),
    /// NOT the raw rule id. Derived from the default ruleset so a DisplayName change fails
    /// HERE deliberately, never as a silent drift.</summary>
    private static string DisplayNameOf(string ruleId) =>
        RulesetLoader.LoadDefault().EnumerateRules().Single(r => r.Id == ruleId).DisplayName;

    private static readonly string NestingDisplayName = DisplayNameOf(RuleIds.Nesting);
    private static readonly string EmptyGroupDisplayName = DisplayNameOf(RuleIds.EmptyGroup);

    // === 1. preview counts == AuditSummary over the demo snapshot =======================

    /// <summary>
    /// For the DEFAULT ruleset the preview's count tiles are exactly the AP 3.2 baseline:
    /// Total 19 / Critical 4 / Warning 3 / Info 12 — and they equal a fresh
    /// <see cref="AuditSummary.Compute"/> over the same snapshot + ruleset.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_DefaultRuleset_CountsAreTheNineteenBaseline_AndEqualAuditSummary()
    {
        var @default = RulesetLoader.LoadDefault();
        var summary = await SummaryAsync(@default);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, @default);

        // The authoritative AP 3.2 baseline (rule-engine.md / AuditSummaryTests).
        Assert.Equal(19, preview.Total);
        Assert.Equal(4, preview.Critical);
        Assert.Equal(3, preview.Warning);
        Assert.Equal(12, preview.Info);

        // ...and the tiles equal the AuditSummary over the SAME inputs.
        Assert.Equal(summary.Critical, preview.Critical);
        Assert.Equal(summary.Warnings, preview.Warning);
        Assert.Equal(summary.Info, preview.Info);
        Assert.Equal(summary.Critical + summary.Warnings + summary.Info, preview.Total);
    }

    /// <summary>
    /// For an EDITED ruleset that ADDS findings (the default ignore list cleared, which
    /// un-suppresses the two builtin DL&lt;-External info edges, AP 3.2 both-ways pin),
    /// the preview tiles STILL equal a fresh <see cref="AuditSummary.Compute"/> over the
    /// same inputs — the projection never re-derives counts, it joins two summaries. The
    /// edit lifts Info 12 -&gt; 14 and Total 19 -&gt; 21 (Critical/Warning unchanged).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_EditedRulesetThatAddsFindings_CountsEqualAuditSummary()
    {
        var edited = RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };
        var summary = await SummaryAsync(edited);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, edited);

        // Tiles mirror the AuditSummary over the edited ruleset exactly.
        Assert.Equal(summary.Critical, preview.Critical);
        Assert.Equal(summary.Warnings, preview.Warning);
        Assert.Equal(summary.Info, preview.Info);
        Assert.Equal(summary.Critical + summary.Warnings + summary.Info, preview.Total);

        // And the concrete shape: the two un-suppressed builtin External infos surface.
        Assert.Equal(4, preview.Critical);
        Assert.Equal(3, preview.Warning);
        Assert.Equal(14, preview.Info);
        Assert.Equal(21, preview.Total);
    }

    // === 2. diff-from-default per-severity ==============================================

    /// <summary>
    /// An edit producing MORE findings than the default yields POSITIVE per-severity
    /// deltas (<c>current - baseline</c>) that read as caution, with the signed
    /// <see cref="PreviewDelta.DisplayValue"/> formatting. Clearing the ignore list adds
    /// exactly 2 Info (Total +2), no Critical/Warning change.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_EditAddsFindings_PerSeverityDeltasArePositiveAndCaution()
    {
        var edited = RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };
        var summary = await SummaryAsync(edited);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, edited);

        // current - baseline (Info 14-12=+2, Total 21-19=+2; severities unchanged).
        Assert.Equal(0, preview.CriticalDelta.Value);
        Assert.Equal(0, preview.WarningDelta.Value);
        Assert.Equal(2, preview.InfoDelta.Value);
        Assert.Equal(2, preview.TotalDelta.Value);

        // The rise reads as caution; the signed text always carries the meaning (WCAG 1.4.1).
        Assert.True(preview.InfoDelta.IsCaution);
        Assert.True(preview.TotalDelta.IsCaution);
        Assert.Equal(PreviewDeltaTone.Caution, preview.InfoDelta.Tone);
        Assert.Equal("+2", preview.InfoDelta.DisplayValue);
        Assert.Equal("+2", preview.TotalDelta.DisplayValue);

        // A zero delta is neutral, displayed as "0".
        Assert.False(preview.CriticalDelta.IsCaution);
        Assert.Equal(PreviewDeltaTone.Neutral, preview.CriticalDelta.Tone);
        Assert.Equal("0", preview.CriticalDelta.DisplayValue);
    }

    /// <summary>
    /// An edit producing FEWER findings (the empty-group rule disabled, dropping all 12
    /// empty-group infos) yields NEGATIVE deltas that are NOT caution, with the signed
    /// <c>-N</c> formatting.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_EditRemovesFindings_PerSeverityDeltasAreNegativeAndNotCaution()
    {
        var d = RulesetLoader.LoadDefault();
        var edited = d with { EmptyGroup = d.EmptyGroup with { Enabled = false } };
        var summary = await SummaryAsync(edited);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, edited);

        // Info 0-12=-12, Total 7-19=-12; Critical/Warning unchanged.
        Assert.Equal(-12, preview.InfoDelta.Value);
        Assert.Equal(-12, preview.TotalDelta.Value);
        Assert.Equal(0, preview.CriticalDelta.Value);
        Assert.Equal(0, preview.WarningDelta.Value);

        // A drop is never cautionary; the signed "-12" carries the meaning.
        Assert.False(preview.InfoDelta.IsCaution);
        Assert.False(preview.TotalDelta.IsCaution);
        Assert.Equal(PreviewDeltaTone.Neutral, preview.InfoDelta.Tone);
        Assert.Equal("-12", preview.InfoDelta.DisplayValue);
        Assert.Equal("-12", preview.TotalDelta.DisplayValue);
    }

    // === 3. per-rule-class deltas =======================================================

    /// <summary>
    /// Disabling the empty-group rule drops the whole <c>empty-group</c> class (12 -&gt; 0).
    /// The class delta list shows exactly that one removed class as <c>-12</c>; every
    /// other class is a zero delta and is OMITTED. The class is appended (the disabled
    /// rule still enumerates, so it surfaces in canonical order regardless).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_DisabledEmptyGroupRule_ShowsNegativeClassDelta_OthersOmitted()
    {
        var d = RulesetLoader.LoadDefault();
        var edited = d with { EmptyGroup = d.EmptyGroup with { Enabled = false } };
        var summary = await SummaryAsync(edited);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, edited);

        // Only the changed class is listed (zero-deltas omitted), as a negative delta,
        // labelled by the rule's DisplayName.
        var delta = Assert.Single(preview.RuleClassDeltas);
        Assert.Equal(EmptyGroupDisplayName, delta.Label);
        Assert.Equal(-12, delta.Value);
        Assert.False(delta.IsCaution);
        Assert.Equal("-12", delta.DisplayValue);
    }

    /// <summary>
    /// Clearing the ignore list un-suppresses the two builtin DL&lt;-External edges, which
    /// land in the <c>nesting</c> class (an Info cell, AP 3.2) — so the <c>nesting</c> class
    /// rises 3 -&gt; 5. The class delta list shows exactly that one class as <c>+2</c>,
    /// caution, with every zero-delta class omitted.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_ClearedIgnoreList_ShowsPositiveNestingClassDelta_OthersOmitted()
    {
        var edited = RulesetLoader.LoadDefault() with { Ignore = Array.Empty<MatchEntry>() };
        var summary = await SummaryAsync(edited);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, edited);

        var delta = Assert.Single(preview.RuleClassDeltas);
        // The class delta surfaces through EnumerateRules(), so its Label is the rule's
        // DisplayName (the bindable text), not the raw rule id.
        Assert.Equal(NestingDisplayName, delta.Label);
        Assert.Equal(2, delta.Value);
        Assert.True(delta.IsCaution);
        Assert.Equal("+2", delta.DisplayValue);
    }

    /// <summary>
    /// The class deltas are listed in canonical <see cref="Ruleset.EnumerateRules"/> order
    /// (nesting -&gt; naming -&gt; circular -&gt; empty-group). An edit that changes two
    /// classes at once (clear ignore = nesting +2; disable empty-group = empty-group -12)
    /// lists nesting BEFORE empty-group, with the labels carrying the rule ids.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_TwoChangedClasses_AreListedInCanonicalRuleOrder()
    {
        var d = RulesetLoader.LoadDefault();
        var edited = d with
        {
            Ignore = Array.Empty<MatchEntry>(),
            EmptyGroup = d.EmptyGroup with { Enabled = false },
        };
        var summary = await SummaryAsync(edited);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, edited);

        // Exactly two changed classes, nesting first (canonical order), empty-group last.
        // Labels are the rules' DisplayNames (surfaced through EnumerateRules()).
        Assert.Equal(2, preview.RuleClassDeltas.Count);
        Assert.Equal(NestingDisplayName, preview.RuleClassDeltas[0].Label);
        Assert.Equal(2, preview.RuleClassDeltas[0].Value);
        Assert.Equal(EmptyGroupDisplayName, preview.RuleClassDeltas[1].Label);
        Assert.Equal(-12, preview.RuleClassDeltas[1].Value);
    }

    // === 4. default ruleset -> zero delta + empty RuleClassDeltas ========================

    /// <summary>
    /// The DEFAULT ruleset against the default baseline is the no-change identity: every
    /// per-severity delta is 0 (displayed "0", neutral) and
    /// <see cref="RulesetPreview.RuleClassDeltas"/> is EMPTY (no class changed).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Compute_DefaultAgainstDefaultBaseline_AllZeroDeltas_EmptyClassDeltas()
    {
        var @default = RulesetLoader.LoadDefault();
        var summary = await SummaryAsync(@default);

        var preview = RulesetPreview.Compute(summary, _fixture.DefaultSummary, @default);

        Assert.Equal(0, preview.TotalDelta.Value);
        Assert.Equal(0, preview.CriticalDelta.Value);
        Assert.Equal(0, preview.WarningDelta.Value);
        Assert.Equal(0, preview.InfoDelta.Value);
        Assert.All(
            new[] { preview.TotalDelta, preview.CriticalDelta, preview.WarningDelta, preview.InfoDelta },
            delta =>
            {
                Assert.False(delta.IsCaution);
                Assert.Equal("0", delta.DisplayValue);
            });

        Assert.Empty(preview.RuleClassDeltas);
    }

    // === helpers ========================================================================

    /// <summary>Evaluate + roll up the demo snapshot under <paramref name="ruleset"/>
    /// OFF-thread (the demo dataset carries the GG_Circle cycle — termination is proven,
    /// never trusted, ADR-006 D4).</summary>
    private Task<AuditSummary> SummaryAsync(Ruleset ruleset) => Task.Run(() =>
    {
        var report = RuleEngine.Evaluate(_fixture.Snapshot, ruleset);
        return AuditSummary.Compute(report, _fixture.Snapshot, ruleset);
    });

    /// <summary>Loads the embedded demo snapshot once + the default-ruleset baseline summary
    /// over it (the diff-from-default reference) — the App-side analogue of the Core test
    /// project's <c>DemoProviderFixture</c> (not referenced from App.Tests).</summary>
    public sealed class DemoSnapshotFixture : IAsyncLifetime
    {
        private const string RootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

        public DirectorySnapshot Snapshot { get; private set; } = null!;

        public AuditSummary DefaultSummary { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var provider = new DemoProvider();
            await provider.ConnectAsync();
            Snapshot = await provider.LoadScopeAsync(RootDn);

            var @default = RulesetLoader.LoadDefault();
            var report = await Task.Run(() => RuleEngine.Evaluate(Snapshot, @default));
            DefaultSummary = AuditSummary.Compute(report, Snapshot, @default);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
