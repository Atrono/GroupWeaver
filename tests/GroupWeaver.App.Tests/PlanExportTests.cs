using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using GroupWeaver.App.Export;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Region (A) of AP 4.2.4 (ADR-014, closes #59): pins the Plan Mode PowerShell-export VM
/// command (<c>ExportPlanScriptCommand</c> on <see cref="PlanViewModel"/>) — the App-side
/// seam between the authored <see cref="PlanModel"/> and the FROZEN pure-Core
/// <see cref="PlanScriptExporter"/>. The exporter itself is pinned byte-for-byte by
/// <c>tests/GroupWeaver.Tests/Core/Plan/PlanScriptExporterTests.cs</c> (incl. injection
/// safety); these tests pin ONLY the VM wiring around it, mirroring the AP 4.1
/// <c>WorkspaceExportTests</c> CSV/HTML shape.
///
/// <para><b>The deterministic-export contract (the whole reason this VM takes injected
/// inputs).</b> The plan ctor gains an injectable <c>clock</c> (<see cref="Func{T}"/> of
/// <see cref="DateTimeOffset"/>) and a fixed <c>toolVersion</c> string so the produced
/// <c>.ps1</c> is reproducible — never the wall clock, never the live assembly version. A
/// test therefore knows the EXACT expected bytes: with a FIXED clock value and a FIXED tool
/// version, the command must write <c>PlanScriptExporter.ToPowerShell(plan, new
/// PlanScriptHeader(BaseOuDn, toolVersion, fixedClockValue))</c> through the BOM-less
/// <c>UTF8Encoding(false)</c> writer. RE-PINNED for #330: the exporter STRING now leads with
/// one in-string U+FEFF (its own tests pin that), so the BOM-less writer produces a file
/// whose first bytes ARE the UTF-8 BOM EF BB BF — that preamble is what makes Windows
/// PowerShell 5.1 decode non-ASCII names correctly (same in-string idiom as #329's CSV).</para>
///
/// <para>Binding contract (AP 4.2.4 spec §A; mirrors the workspace export discipline):</para>
/// <list type="bullet">
///   <item><b>Executing the command writes the exporter output to the picked path</b> via the
///   BOM-less writer — asserted byte-for-byte AND with an explicit "the first bytes ARE the
///   in-string UTF-8 BOM (EF BB BF), exactly once" check (#330 re-pin; old pin: NO BOM).</item>
///   <item><b>PS 5.1 parse-clean proof (#330):</b> a plan with a non-ASCII name exported
///   through the real command produces a file Windows PowerShell 5.1's own parser reads
///   with ZERO errors (spawned <c>powershell.exe</c>, <c>Parser.ParseFile</c> — which honors
///   the BOM exactly like <c>-File</c> does).</item>
///   <item><b>The kind requested is <see cref="ExportKind.Ps1"/></b> — the fake records it.</item>
///   <item><b>A cancelled pick (path == null) is a no-op</b> — nothing is written anywhere.</item>
///   <item><b><c>CanExportPlanScript</c></b>: false with no dialogs installed, false on an
///   empty plan (zero nodes), true once ≥1 node AND dialogs installed; re-gates after an
///   add/remove driven through the public AP 4.2.3 commands + <c>UseExportFileDialogs</c>.</item>
/// </list>
///
/// <para>The plan is seeded through the PUBLIC AP 4.2.3 command surface (set
/// <c>NewObjectKind</c>/<c>NewObjectName</c>, then <c>AddObjectCommand</c>); the export is
/// never executed (the <c>.ps1</c> is inert text). The reused fake is
/// <see cref="FakeExportDialogs"/> (the AP 4.1 export-seam fake — it records the requested
/// <see cref="ExportKind"/> and returns a per-kind path / null for a cancelled pick).</para>
///
/// <para><b>RED until AP 4.2.4</b> adds <see cref="ExportKind.Ps1"/>, the
/// <see cref="PlanViewModel"/> ctor params (<c>exportDialogs</c>/<c>clock</c>/<c>toolVersion</c>),
/// <c>UseExportFileDialogs</c>, and the <c>ExportPlanScriptCommand</c>/<c>CanExportPlanScript</c>
/// members. The App.Tests assembly will not compile until those exist — the intended TDD
/// state for this slice.</para>
/// </summary>
public sealed class PlanExportTests
{
    private const string PlanBaseOuDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary>The fixed tool version injected into every plan VM here — proves the header
    /// carries the INJECTED version, never the live assembly's informational version.</summary>
    private const string FixedToolVersion = "9.9.9-test";

    /// <summary>The fixed generation timestamp the injected clock returns — proves the header
    /// carries the INJECTED instant, never <see cref="DateTimeOffset.Now"/>; the exporter is
    /// then byte-deterministic (its own tests pin that determinism).</summary>
    private static readonly DateTimeOffset FixedClock =
        new(2026, 6, 13, 14, 2, 11, TimeSpan.Zero);

    // === byte-exact export to the picked path, UTF-8 NO BOM ==============================

    /// <summary>
    /// The keystone byte-exactness pin (spec §A): a non-empty plan + a fake returning a temp
    /// path → <c>ExportPlanScriptCommand</c> writes a <c>.ps1</c> whose bytes EQUAL
    /// <c>PlanScriptExporter.ToPowerShell(plan, new PlanScriptHeader(BaseOuDn, toolVersion,
    /// fixedClockValue))</c> encoded through the BOM-less <c>UTF8Encoding(false)</c> writer.
    /// The expected bytes are recomputed here from the SAME frozen exporter the VM calls and
    /// the SAME injected header inputs, so this pins that the VM (a) builds the header from
    /// <c>BaseOuDn</c> + the injected tool version + the injected clock value, (b) routes
    /// through the frozen exporter unchanged, and (c) keeps the WRITER BOM-less — the BOM the
    /// file carries is the exporter's own leading in-string U+FEFF, nothing the writer adds.
    /// RE-PINNED for #330 (old pin: the file must NOT start with EF BB BF): the file now MUST
    /// start with EF BB BF, exactly once — that preamble is what makes PS 5.1 decode
    /// non-ASCII names correctly. The explicit first-bytes assertion is load-bearing: a
    /// writer that ALSO added its own encoder BOM would double the preamble yet still pass a
    /// naive string round-trip, so the exactly-once check guards both directions.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ExportPlanScript_WritesExporterBytes_WithTheInStringBomLeading_ToThePickedPath()
    {
        using var temp = new TempFile("ps1");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Ps1, temp.Path);
        var plan = HeadlessPlanWithExport(dialogs);
        await SeedGroupAsync(plan, "GG_Sales_Team");
        await SeedGroupAsync(plan, "DL_FileShare_RW");

        await plan.ExportPlanScriptCommand.ExecuteAsync(null);

        // The frozen exporter is the byte-authority (pinned by PlanScriptExporterTests); the VM
        // must write EXACTLY ToPowerShell(plan, header) for the injected header inputs through
        // the BOM-less writer. Recompute the expected text + bytes from the same exporter +
        // the same inputs.
        var expectedHeader = new PlanScriptHeader(plan.BaseOuDn, FixedToolVersion, FixedClock);
        var expectedText = PlanScriptExporter.ToPowerShell(plan.Plan, expectedHeader);
        var expectedBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(expectedText);

        Assert.True(File.Exists(temp.Path), "the export command must write the picked .ps1 file");
        var actualBytes = File.ReadAllBytes(temp.Path);
        Assert.Equal(expectedBytes, actualBytes);

        // Explicit BOM pin (#330 re-pin; old assertion was the exact inverse): the file MUST
        // begin with the UTF-8 BOM EF BB BF — the exporter's in-string U+FEFF surviving the
        // BOM-less writer — and must carry it exactly ONCE (no writer-added second preamble).
        Assert.True(
            actualBytes.Length >= 3 && actualBytes[0] == 0xEF && actualBytes[1] == 0xBB && actualBytes[2] == 0xBF,
            "the .ps1 must start with the in-string UTF-8 BOM EF BB BF (PS 5.1 decodes non-ASCII via it)");
        Assert.False(
            actualBytes.Length >= 6 && actualBytes[3] == 0xEF && actualBytes[4] == 0xBB && actualBytes[5] == 0xBF,
            "the BOM must appear exactly once - a doubled preamble means the writer added its own");

        plan.Dispose();
    }

    /// <summary>
    /// The honest PS 5.1 parse-clean proof (#330): a plan carrying a non-ASCII name, exported
    /// through the REAL command + the REAL BOM-less writer, must parse with ZERO errors under
    /// a spawned Windows PowerShell 5.1 (<c>Parser.ParseFile</c> honors the BOM exactly like
    /// <c>-File</c>; an in-process .NET re-read would silently use UTF-8 and prove nothing).
    /// The name's trailing Cyrillic <c>ђ</c> (UTF-8 <c>D1 92</c>) is the parse-breaking
    /// sentinel: BOM-less, ANSI mis-decodes 0x92 into a curly-quote string DELIMITER, so the
    /// old encoding did not merely mojibake — it failed to parse. Skips LOUDLY via
    /// <see cref="WindowsPowerShell51FactAttribute"/> when powershell.exe is absent (never on
    /// this box or windows-2022 CI); the spawn is bounded like every other test here.
    /// </summary>
    [WindowsPowerShell51Fact(Timeout = 30_000)]
    public async Task ExportPlanScript_NonAsciiName_ParsesCleanUnderWindowsPowerShell51()
    {
        using var temp = new TempFile("ps1");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Ps1, temp.Path);
        var plan = HeadlessPlanWithExport(dialogs);
        await SeedGroupAsync(plan, "GG_Vertrieb_Käuferђ");

        await plan.ExportPlanScriptCommand.ExecuteAsync(null);
        Assert.True(File.Exists(temp.Path), "the export command must write the picked .ps1 file");

        // Spawn Windows PowerShell 5.1 and let ITS parser read the file the way -File would.
        var psi = new ProcessStartInfo
        {
            FileName = WindowsPowerShell51FactAttribute.Ps51Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "$errs = $null; "
            + "[void][System.Management.Automation.Language.Parser]::ParseFile('"
            + temp.Path.Replace("'", "''", StringComparison.Ordinal)
            + "', [ref]$null, [ref]$errs); "
            + "$errs.Count");
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        Assert.True(proc.ExitCode == 0, $"powershell.exe failed (exit {proc.ExitCode}): {stderr}");
        Assert.True(
            stdout == "0",
            $"PS 5.1 reported {stdout} parse error(s) on the exported .ps1; stderr: {stderr}");

        // File-level first-bytes pin inside the same journey (#330): the parse-clean result
        // above exists BECAUSE the file starts with the in-string BOM's EF BB BF preamble.
        var bytes = File.ReadAllBytes(temp.Path);
        Assert.True(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the exported .ps1 must start with EF BB BF - without it PS 5.1 reads ANSI");

        plan.Dispose();
    }

    /// <summary>
    /// The picker is consulted for the <see cref="ExportKind.Ps1"/> kind (spec §A): the fake
    /// records every requested kind, and a plan export must request exactly <c>Ps1</c> — the
    /// new enum member this slice adds. Pins that the VM passes the right kind to the seam (the
    /// production adapter maps <c>Ps1</c> → the <c>.ps1</c> picker options; that [I] mapping is
    /// driven manually / by the wiring test, not here).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ExportPlanScript_RequestsThePs1ExportKind()
    {
        using var temp = new TempFile("ps1");
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Ps1, temp.Path);
        var plan = HeadlessPlanWithExport(dialogs);
        await SeedGroupAsync(plan, "GG_Sales_Team");

        await plan.ExportPlanScriptCommand.ExecuteAsync(null);

        Assert.Contains(ExportKind.Ps1, dialogs.RequestedKinds);

        plan.Dispose();
    }

    /// <summary>
    /// A cancelled pick (the fake returns null for <see cref="ExportKind.Ps1"/>) is a no-op
    /// (spec §A): the picker WAS consulted (the gate let the command run — there is ≥1 node and
    /// dialogs are installed) but nothing is written anywhere. Mirrors the workspace's
    /// cancelled-pick discipline.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ExportPlanScript_CancelledPick_WritesNothing()
    {
        using var temp = new TempFile("ps1");
        // A cancelled save dialog returns null for the Ps1 kind.
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Ps1, null);
        var plan = HeadlessPlanWithExport(dialogs);
        await SeedGroupAsync(plan, "GG_Sales_Team");

        await plan.ExportPlanScriptCommand.ExecuteAsync(null);

        // The picker WAS consulted (the gate let the command run)…
        Assert.Contains(ExportKind.Ps1, dialogs.RequestedKinds);
        // …but a null pick writes nothing anywhere under the isolation directory.
        Assert.False(File.Exists(temp.Path));
        Assert.Empty(temp.WrittenFiles());

        plan.Dispose();
    }

    // === CanExportPlanScript gate: dialogs installed AND ≥1 node, re-gates on add/remove ==

    /// <summary>
    /// With NO dialogs installed the command is disarmed even with nodes present (spec §A): the
    /// gate is <c>_exportDialogs is not null</c> AND <c>Plan.Nodes.Count &gt; 0</c>. A headless
    /// plan constructed WITHOUT the export seam (the AP 4.2.2 ctor shape) must stay disarmed; a
    /// stale-armed Execute is a silent no-op (no write).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CanExportPlanScript_NoDialogs_IsDisarmed_EvenWithNodes()
    {
        // The AP 4.2.2 ctor (no export seam): a node is added, but the dialogs half of the gate
        // is unmet, so the command stays disarmed.
        var plan = new PlanViewModel(PlanBaseOuDn, DefaultEffectiveRuleset());
        await SeedGroupAsync(plan, "GG_Sales_Team");

        Assert.False(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "plan export is disarmed with no dialogs installed (even with a node present)");

        // Stale-armed Execute (RelayCommand ignores CanExecute) is a no-op — no picker, no write.
        await plan.ExportPlanScriptCommand.ExecuteAsync(null);

        plan.Dispose();
    }

    /// <summary>
    /// With dialogs installed but an EMPTY plan (zero nodes) the command is disarmed (spec §A):
    /// the <c>Plan.Nodes.Count &gt; 0</c> half of the gate is unmet. A stale-armed Execute is a
    /// silent no-op — it never consults the picker.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CanExportPlanScript_EmptyPlan_IsDisarmed_EvenWithDialogs()
    {
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Ps1, "unused.ps1");
        var plan = HeadlessPlanWithExport(dialogs);

        Assert.Empty(plan.Plan.Nodes);
        Assert.False(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "plan export is disarmed on an empty plan (zero nodes)");

        // Stale-armed Execute is a no-op: the picker is never consulted for an empty plan.
        await plan.ExportPlanScriptCommand.ExecuteAsync(null);
        Assert.DoesNotContain(ExportKind.Ps1, dialogs.RequestedKinds);

        plan.Dispose();
    }

    /// <summary>
    /// The command ARMS once both gate halves are met — dialogs installed AND ≥1 node — and
    /// RE-GATES as the plan crosses the zero-node boundary in both directions, driven entirely
    /// through the public AP 4.2.3 commands (<c>AddObjectCommand</c>/<c>RemoveSelectedCommand</c>)
    /// and <c>UseExportFileDialogs</c> (spec §A): adding the first node arms it, removing the
    /// last node disarms it again. This pins that <c>CanExportPlanScript</c>'s
    /// <c>NotifyCanExecuteChanged</c> fires from <c>RefreshAuthoredCollections</c> after every
    /// mutation.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CanExportPlanScript_ReGates_AcrossTheFirstAndLastNode_ViaPublicCommands()
    {
        var dialogs = new FakeExportDialogs().SavePathFor(ExportKind.Ps1, "unused.ps1");
        // Install the seam AFTER construction through the public wiring point (mirrors the
        // workspace + the #63 production path); start from an empty plan.
        var plan = new PlanViewModel(PlanBaseOuDn, DefaultEffectiveRuleset());
        plan.UseExportFileDialogs(dialogs);

        // Empty + dialogs installed: disarmed (no node yet).
        Assert.False(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "armed only once ≥1 node exists AND dialogs are installed");

        // Add the first node via the public command → arms.
        await SeedGroupAsync(plan, "GG_Sales_Team");
        Assert.True(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "adding the first node must re-gate plan export to armed");

        // Remove the last node via the public command (select the row, then RemoveSelected) →
        // back to an empty plan → disarms.
        plan.SelectedNodeRow = plan.Nodes.Single();
        await plan.RemoveSelectedCommand.ExecuteAsync(null);
        Assert.Empty(plan.Plan.Nodes);
        Assert.False(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "removing the last node must re-gate plan export back to disarmed");

        plan.Dispose();
    }

    /// <summary>
    /// <c>UseExportFileDialogs</c> ARMS the command when a node already exists (spec §A; mirrors
    /// the workspace seam): a non-empty plan built WITHOUT the export seam is disarmed, and
    /// installing the seam after the fact must re-arm it (the seam calls
    /// <c>ExportPlanScriptCommand.NotifyCanExecuteChanged()</c>).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task UseExportFileDialogs_OnANonEmptyPlan_ArmsTheCommand()
    {
        var plan = new PlanViewModel(PlanBaseOuDn, DefaultEffectiveRuleset());
        await SeedGroupAsync(plan, "GG_Sales_Team");
        Assert.False(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "disarmed before the seam is installed (dialogs half unmet)");

        plan.UseExportFileDialogs(new FakeExportDialogs().SavePathFor(ExportKind.Ps1, "unused.ps1"));

        Assert.True(
            plan.ExportPlanScriptCommand.CanExecute(null),
            "installing the export seam on a non-empty plan must arm the command");

        plan.Dispose();
    }

    // === helpers ========================================================================

    private static EffectiveRuleset DefaultEffectiveRuleset() =>
        new(RulesetLoader.LoadDefault(), FromUserFile: false, []);

    /// <summary>A headless plan VM (no renderer factory) WITH the export seam, the FIXED clock,
    /// and the FIXED tool version injected — the deterministic-export shape this slice adds to
    /// the ctor. Rooted at the lab base OU, default ruleset.</summary>
    private static PlanViewModel HeadlessPlanWithExport(IExportFileDialogs dialogs) =>
        new(
            PlanBaseOuDn,
            DefaultEffectiveRuleset(),
            exportDialogs: dialogs,
            clock: () => FixedClock,
            toolVersion: FixedToolVersion);

    /// <summary>Seeds a GROUP through the PUBLIC AP 4.2.3 command surface (set the form's kind +
    /// name, then run <c>AddObjectCommand</c>) — never by reaching into the model directly, so
    /// the re-gate path (RefreshAuthoredCollections → NotifyCanExecuteChanged) is exercised.</summary>
    private static async Task SeedGroupAsync(PlanViewModel plan, string name)
    {
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = name;
        plan.NewObjectSam = "";
        await plan.AddObjectCommand.ExecuteAsync(null);
        Assert.True(
            plan.Plan.TryGetNode(plan.Plan.FormDn(name), out _),
            $"the public AddObject command must have authored '{name}'");
    }

    /// <summary>A temp file under its OWN per-instance isolation directory so a no-op / cancelled
    /// export can be pinned by scanning that directory for stray writes (<see cref="WrittenFiles"/>)
    /// without cross-test interference. The PATH is computed but the file is NOT created — a
    /// no-op export must leave it absent, and the directory then scans empty. Mirrors the
    /// <c>WorkspaceExportTests.TempFile</c> shape.</summary>
    private sealed class TempFile : IDisposable
    {
        private readonly string _dir;

        public TempFile(string extension)
        {
            _dir = Directory.CreateTempSubdirectory("groupweaver-plan-export-tests-").FullName;
            Path = System.IO.Path.Combine(_dir, $"plan.{extension}");
        }

        public string Path { get; }

        /// <summary>Every file that currently exists under THIS file's isolation directory —
        /// used to assert a cancelled/no-op export wrote nothing.</summary>
        public string[] WrittenFiles() =>
            Directory.Exists(_dir)
                ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
                : [];

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// <see cref="FactAttribute"/> for tests that spawn Windows PowerShell 5.1
/// (<c>powershell.exe</c>): sets <see cref="FactAttribute.Skip"/> with a LOUD reason when the
/// binary is absent (mirrors the <c>AdFact</c> loud-skip idiom). It is never absent on this
/// lab box or the windows-2022 CI image — the guard exists so an exotic environment degrades
/// to a visible skip instead of a spawn failure.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsPowerShell51FactAttribute : FactAttribute
{
    public WindowsPowerShell51FactAttribute()
    {
        if (!File.Exists(Ps51Path))
        {
            Skip = "WARNING: Windows PowerShell 5.1 (powershell.exe) not found at "
                + Ps51Path
                + " - the PS 5.1 parse-clean pin SKIPPED; it is mandatory on the lab box and CI.";
        }
    }

    /// <summary>The canonical 5.1 binary path — also the spawn target for the test body.</summary>
    internal static string Ps51Path { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32",
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
}
