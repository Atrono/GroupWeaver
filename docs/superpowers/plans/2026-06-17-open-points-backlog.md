# Open-Points Backlog (Phase 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear the four open GitHub issues that constitute GroupWeaver's current "open points" after the v0.2.0 release — a release-blocking CI fix (#79), a security-hardening parity fix (#77), a stale-media refresh (#78), and a design-gated architecture follow-up (#54).

**Architecture:** Four *independent* task groups, each its own short-lived branch → PR → `reviewer` approval → squash merge (trunk-based, per CLAUDE.md). They share no state and may be executed in any order, but the recommended order is by urgency: **A (#79) → B (#77) → C (#78) → D (#54)**. #79 is time-critical: GitHub's Node 20 runtime removal flipped 2026-06-16, so the *next* tagged release would fail attestation until A lands. D is deferred — it must not be implemented without an accepted ADR.

**Tech Stack:** .NET 8 (C# 12), Avalonia, xUnit (`Assert.*`, never FluentAssertions), GitHub Actions (SHA-pinned), PowerShell 7, ffmpeg. Full local gate: `pwsh tools/build.ps1`.

---

## Background facts (verified 2026-06-17, do not re-derive)

These were established by direct inspection of the repo and the GitHub Actions registry. Trust them; they are the basis for the code below.

- **#79 root cause:** `release.yml:44` pins `actions/attest-build-provenance@v2.4.0` (`e8998f94…`). That action is a `composite` that internally calls `actions/attest@ce27ba3b… `, whose `runs.using` is **`node20`** — the runtime GitHub removed on 2026-06-16. Its replacement `actions/attest-build-provenance@v3.0.0` (commit `977bb373ede98d70efdf65b84cb5f73e068dcc2a`) wraps `actions/attest@daf44fb9… ` which is **`node24`**, while keeping the exact same input/output surface (`subject-path`, `bundle-path`/`attestation-id`/`attestation-url`). The workflow uses only `subject-path` and consumes no outputs ⇒ v3.0.0 is a drop-in SHA swap.
- **Every other action is already `node24`:** `checkout@v6.0.3`, `setup-dotnet@v5.3.0`, `cache@v5.0.5`, `upload-artifact@v7.0.1` all report `using: node24` at their pinned SHAs. **They are NOT in scope.** `ci.yml` does not use `attest-build-provenance`, so #79 touches `release.yml` only.
- **Why v3.0.0 (not v4.1.0):** v3.0.0 is the minimal hop off Node 20; it preserves v2's composite structure (separate `predicate` sub-step) and adds no new behavior. v4.x additionally defaults `create-storage-record: true` (a newer GitHub feature / new permission surface) — out of scope for "get off Node 20". v4.1.0 (`a2bbfa25375fe432b6a289bc6b6cd05ecd0c4c32`) is the documented fallback if the project later wants the latest line.
- **#77 parity gap (two holes):** `PlanScriptExporter.Guard` (export-time) rejects `char.IsControl(c) || c==' ' || c==' ' || (c>='‘' && c<='‟')`. But author-time validation is weaker and inconsistent: `PlanModel.AddNode` calls `HasControlChars` which only rejects `c < ' '` (ASCII control), **missing** U+0085 NEL, U+2028/U+2029, and the curly-quote block U+2018–U+201F; and `PlanModel.RenameNode` does **no** char validation at all. The fix is to give both author-time and export-time **one shared predicate** so they can never drift again (drift is exactly why #77 exists). The ASCII apostrophe U+0027 must stay **allowed** (the exporter doubles it).
- **#77 no message is pinned:** no test asserts the string `"Names must not contain control characters."`, so broadening it is safe. The App-level ViewModel test `AddObject_ControlCharName_SetsEditError_NoNodeAdded` uses a BEL (U+0007) which the widened predicate still catches ⇒ it stays green.
- **#78 state:** `docs/media/m2-explore.gif` (light-themed, 2026-06-12) is still git-tracked but **no longer referenced** by README (the hero was swapped to two dark stills, `graph-explore.png` + `gap-analysis.png`, on 2026-06-15). The Avalonia shell is now hard-Dark (`App.axaml:4` `RequestedThemeVariant="Dark"`); the graph WebView was always dark (#1b1f27). The recorder `tools/record-demo-gif.ps1` drives chrome via UIA and graph nodes via posted `WM_*`, pixel-hunting graph node *fill* colors (unchanged by the shell flip) ⇒ re-running produces a dark GIF with no code change. Recording is demo-mode-only by construction (it UIA-clicks "Demo mode" off-camera, never "Connect to domain").

---

## File Structure

| Group | Files created / modified | Responsibility |
|---|---|---|
| A (#79) | Modify `.github/workflows/release.yml:44` | Move attestation off the removed Node 20 runtime |
| B (#77) | Create `src/Core/Plan/PlanText.cs`; Modify `src/Core/Plan/PlanModel.cs`, `src/Core/Plan/PlanScriptExporter.cs`; Test `tests/GroupWeaver.Tests/Core/Plan/PlanModelTests.cs`, `tests/GroupWeaver.App.Tests/PlanModeEditorTests.cs` | One shared unsafe-char predicate; author-time parity for `AddNode` + `RenameNode`; refactor `Guard` to the shared predicate |
| C (#78) | Regenerate `docs/media/m2-explore.gif` (binary); Modify `README.md` | Dark-theme demo GIF + re-embed so the artifact is used, not dead weight |
| D (#54) | Create `docs/adr/016-graph-reachability-pruning.md` (DRAFT only) | **Design-gated.** ADR draft; NO implementation without acceptance + user-feedback justification |

`PlanText` is a new `internal static` helper in the `GroupWeaver.Core.Plan` namespace (same assembly as `PlanModel`/`PlanScriptExporter`), so no `InternalsVisibleTo` is needed — tests exercise it through the public `AddNode`/`RenameNode`/`ToPowerShell` API.

---

## Task Group A — #79: attestation off Node 20 (release.yml)

**Priority: do this first.** Release-blocking; a one-line SHA swap plus verification.

**Files:**
- Modify: `.github/workflows/release.yml:44`

- [ ] **Step A1: Create the branch**

```bash
git switch -c fix/79-attest-node24
```

- [ ] **Step A2: Verify the target SHA and runtime (the facts the swap relies on)**

Run:
```bash
gh api repos/actions/attest-build-provenance/git/ref/tags/v3.0.0 --jq '.object.sha'
gh api "repos/actions/attest-build-provenance/contents/action.yml?ref=977bb373ede98d70efdf65b84cb5f73e068dcc2a" --jq '.content' | base64 --decode | grep -E 'actions/attest@'
gh api "repos/actions/attest/contents/action.yml?ref=daf44fb950173508f38bd2406030372c1d1162b1" --jq '.content' | base64 --decode | grep -iE '^\s*using:'
```
Expected:
- First line prints `977bb373ede98d70efdf65b84cb5f73e068dcc2a`.
- Second line shows `actions/attest@daf44fb950173508f38bd2406030372c1d1162b1 # v3.0.0`.
- Third line shows `using: node24`.

If any differ, STOP — the registry moved and the pin below must be re-derived.

- [ ] **Step A3: Swap the pin (single edit)**

In `.github/workflows/release.yml`, replace the `Attest build provenance` step's `uses:` line.

Old (exact, including the em-dash):
```yaml
        uses: actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be   # v2.4.0; v4 is current but is a thin actions/attest wrapper — v2 stays fully supported
```
New:
```yaml
        uses: actions/attest-build-provenance@977bb373ede98d70efdf65b84cb5f73e068dcc2a   # v3.0.0 (#79: v2.x wrapped actions/attest on the removed Node 20 runtime; v3 moved it to Node 24, same subject-path I/O)
```

- [ ] **Step A4: Confirm NO workflow action still targets Node 20**

Run:
```bash
for repo_sha in \
  "actions/checkout|df4cb1c069e1874edd31b4311f1884172cec0e10" \
  "actions/setup-dotnet|9a946fdbd5fb07b82b2f5a4466058b876ab72bb2" \
  "actions/cache|27d5ce7f107fe9357f9df03efb73ab90386fccae" \
  "actions/upload-artifact|043fb46d1a93c77aae656e7c1c64a875d1fc6a0a"; do
  repo="${repo_sha%%|*}"; sha="${repo_sha##*|}"
  printf "%-28s %s\n" "$repo" "$(gh api "repos/$repo/contents/action.yml?ref=$sha" --jq '.content' | base64 --decode | grep -iE '^\s*using:' | head -1)"
done
```
Expected: every line reports `node24` (these are the already-fine actions; confirms the attestation action was the sole Node 20 dependency).

- [ ] **Step A5: Sanity-parse the workflow file**

Run:
```bash
gh workflow view Release --yaml > /dev/null && echo "release.yml parses" || echo "PARSE ERROR — fix YAML"
```
Expected: `release.yml parses`. (If `gh workflow view` is not authenticated for this, fall back to confirming the diff is exactly the one-line `uses:` swap with `git diff`.)

- [ ] **Step A6: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci(release): bump attest-build-provenance v2.4.0 -> v3.0.0 (#79)

v2.x wrapped actions/attest on the Node 20 runtime GitHub removed 2026-06-16;
v3.0.0 moves it to Node 24 with an unchanged subject-path I/O surface. All
other workflow actions were already on Node 24."
```

- [ ] **Step A7: Push and open the PR**

```bash
git push -u origin fix/79-attest-node24
gh pr create --fill --base main
```
PR body must flag for the `reviewer`: *"#79; one-line SHA swap of the attestation action off the removed Node 20 runtime onto v3.0.0 (Node 24), input/output surface unchanged."*

- [ ] **Step A8: Definition of Done for Group A**
  - `reviewer` subagent approves the diff.
  - Push → `ci-sentinel` watches Actions until **CI green** (CI does not run `release.yml`, but must stay green).
  - **Acceptance note (record in the issue close):** `release.yml` only runs on a `v*` tag push, so the *true* end-to-end proof is deferred to the next real release — do **not** cut a throwaway public release to test it. The next time the user tags a release, attestation must succeed and `gh attestation verify <zip>` must pass on the published artifact (the existing release DoD already covers this). The SHA/runtime checks in A2/A4 are the pre-merge proof.
  - Close #79 with a one-line result note: *"Bumped attest-build-provenance to v3.0.0 (Node 24); other actions already Node 24."*

---

## Task Group B — #77: author-time char parity (Plan editor)

**Files:**
- Create: `src/Core/Plan/PlanText.cs`
- Modify: `src/Core/Plan/PlanModel.cs:51-68` (AddNode), `:92-123` (RenameNode), `:169-180` (remove `HasControlChars`)
- Modify: `src/Core/Plan/PlanScriptExporter.cs:193-222` (Guard + its doc comment)
- Test: `tests/GroupWeaver.Tests/Core/Plan/PlanModelTests.cs`
- Test: `tests/GroupWeaver.App.Tests/PlanModeEditorTests.cs`

- [ ] **Step B1: Create the branch**

```bash
git switch -c fix/77-plan-char-parity
```

- [ ] **Step B2: Write the failing author-time tests (Core)**

Add these to `tests/GroupWeaver.Tests/Core/Plan/PlanModelTests.cs` (namespace `GroupWeaver.Tests.Core.Plan`; `BaseOu` and the `xUnit` style already exist in that file):

```csharp
    // --- #77: author-time unsafe-char parity with PlanScriptExporter.Guard ----------------

    [Theory]
    [InlineData('‘')] // LEFT SINGLE QUOTATION MARK — start of the curly-quote block
    [InlineData('’')] // RIGHT SINGLE QUOTATION MARK — the 0.2 audit's reproduced breakout char
    [InlineData('“')] // LEFT DOUBLE QUOTATION MARK
    [InlineData('‟')] // DOUBLE HIGH-REVERSED-9 — end of the curly-quote block
    [InlineData(' ')] // LINE SEPARATOR — not char.IsControl
    [InlineData(' ')] // PARAGRAPH SEPARATOR — not char.IsControl
    [InlineData('')] // NEXT LINE (NEL) — a C1 control the old "c < ' '" gate missed
    public void AddNode_NameWithUnsafeChar_ThrowsPlanConflict_AndAddsNothing(char unsafeChar)
    {
        var plan = new PlanModel(BaseOu);

        Assert.Throws<PlanConflictException>(
            () => plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales" + unsafeChar));

        Assert.Empty(plan.Nodes);
    }

    [Fact]
    public void AddNode_SamWithUnsafeChar_ThrowsPlanConflict_AndAddsNothing()
    {
        var plan = new PlanModel(BaseOu);

        // The smart quote rides the SAM channel; the name is clean.
        Assert.Throws<PlanConflictException>(
            () => plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales", sam: "GG_Sales’"));

        Assert.Empty(plan.Nodes);
    }

    [Theory]
    [InlineData('’')] // RIGHT SINGLE QUOTATION MARK
    [InlineData(' ')] // LINE SEPARATOR
    [InlineData('')] // NEL
    public void RenameNode_NewNameWithUnsafeChar_ThrowsPlanConflict_AndLeavesNodeUnchanged(char unsafeChar)
    {
        var plan = new PlanModel(BaseOu);
        var original = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales");

        Assert.Throws<PlanConflictException>(() => plan.RenameNode(original.Dn, "GG_Renamed" + unsafeChar));

        // The rejected rename leaves the original node intact under its original DN.
        Assert.True(plan.TryGetNode(original.Dn, out _));
        Assert.Single(plan.Nodes);
    }

    [Fact]
    public void AddNode_NameWithStraightApostrophe_IsAccepted()
    {
        // U+0027 is the normal apostrophe — SAFE (the exporter doubles it in the
        // single-quoted literal). Only the curly-quote block U+2018–U+201F is unsafe.
        // Guards against over-rejection.
        var plan = new PlanModel(BaseOu);

        var node = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_O'Brien");

        Assert.Equal("GG_O'Brien", node.Name);
        Assert.Single(plan.Nodes);
    }
```

- [ ] **Step B3: Run the new tests to verify they fail**

Run:
```bash
dotnet test tests/GroupWeaver.Tests/GroupWeaver.Tests.csproj --filter "FullyQualifiedName~PlanModelTests" -v minimal
```
Expected: FAIL. `AddNode_NameWithUnsafeChar…` fails for the non-`c<' '` chars (U+2018–U+201F, U+2028, U+2029, U+0085) because `HasControlChars` lets them through; all three `RenameNode_NewNameWithUnsafeChar…` cases fail because `RenameNode` has no char validation. (`AddNode_NameWithStraightApostrophe_IsAccepted` should already PASS.)

- [ ] **Step B4: Create the shared predicate**

Create `src/Core/Plan/PlanText.cs`:

```csharp
namespace GroupWeaver.Core.Plan;

/// <summary>
/// THE single definition of which characters are unsafe in a plan token (an object
/// Name or SamAccountName). Author-time validation (<see cref="PlanModel"/>) and
/// export-time validation (<see cref="PlanScriptExporter"/>) both route through this
/// predicate so they can never drift — issue #77 existed precisely because they had.
///
/// Unsafe = any <see cref="char.IsControl(char)"/> (supersedes the old "c &lt; ' '"
/// test; also catches U+0085 NEL and the C1 range), the line/paragraph separators
/// U+2028/U+2029 (neither is IsControl), and the curly-quote block U+2018..U+201F,
/// which PowerShell's tokenizer honours as string delimiters (the 0.2 audit's
/// single-quote breakout). The ASCII apostrophe U+0027 is NOT unsafe — it is the safe
/// doubled case in <see cref="PlanScriptExporter"/>'s single-quoted literal.
/// </summary>
internal static class PlanText
{
    /// <summary>True if <paramref name="c"/> is unsafe in a plan token.</summary>
    public static bool IsUnsafe(char c) =>
        char.IsControl(c)
        || c == ' '
        || c == ' '
        || (c >= '‘' && c <= '‟');

    /// <summary>True if <paramref name="value"/> contains any unsafe character.</summary>
    public static bool ContainsUnsafe(string value)
    {
        foreach (var c in value)
        {
            if (IsUnsafe(c))
            {
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step B5: Route `AddNode` through the shared predicate and broaden its message**

In `src/Core/Plan/PlanModel.cs`, replace the head of `AddNode` (lines 53–56):

Old:
```csharp
        if (HasControlChars(name) || (sam is not null && HasControlChars(sam)))
        {
            throw new PlanConflictException("Names must not contain control characters.");
        }
```
New:
```csharp
        if (PlanText.ContainsUnsafe(name) || (sam is not null && PlanText.ContainsUnsafe(sam)))
        {
            throw new PlanConflictException(
                "A name carries a character (a control character, line separator, or curly quote) "
                + "that is unsafe to embed in the exported script and must not be used.");
        }
```

- [ ] **Step B6: Add the same guard to `RenameNode`**

In `src/Core/Plan/PlanModel.cs`, `RenameNode` currently validates only the DN existence and duplication. Add a char guard as the first check inside the method (immediately after the opening brace, before the `if (!_nodes.TryGetValue(...))`):

```csharp
        if (PlanText.ContainsUnsafe(newName))
        {
            throw new PlanConflictException(
                "A name carries a character (a control character, line separator, or curly quote) "
                + "that is unsafe to embed in the exported script and must not be used.");
        }
```

- [ ] **Step B7: Delete the now-orphaned `HasControlChars`**

In `src/Core/Plan/PlanModel.cs`, delete the entire private helper (lines 169–180):

```csharp
    private static bool HasControlChars(string value)
    {
        foreach (var c in value)
        {
            if (c < ' ')
            {
                return true;
            }
        }

        return false;
    }
```
Removing it is the point of #77: leaving a second, weaker definition lying around re-opens the drift.

- [ ] **Step B8: Refactor `Guard` onto the shared predicate (export side)**

In `src/Core/Plan/PlanScriptExporter.cs`, replace the `Guard` body (lines 206–222) so it delegates to `PlanText`, preserving its exception type and message:

Old:
```csharp
    private static string Guard(string raw)
    {
        foreach (var c in raw)
        {
            if (char.IsControl(c)
                || c == ' '
                || c == ' '
                || (c >= '‘' && c <= '‟'))
            {
                throw new PlanScriptException(
                    "A plan token carries a character that is unsafe to embed in the exported script "
                    + "and cannot be exported.");
            }
        }

        return raw;
    }
```
New:
```csharp
    private static string Guard(string raw)
    {
        if (PlanText.ContainsUnsafe(raw))
        {
            throw new PlanScriptException(
                "A plan token carries a character that is unsafe to embed in the exported script "
                + "and cannot be exported.");
        }

        return raw;
    }
```

- [ ] **Step B9: Make the Guard doc comment honest (parity is no longer a follow-up)**

In `src/Core/Plan/PlanScriptExporter.cs`, the `Guard` doc comment (lines 193–205) still says author-time is control-chars-only and parity is "a tracked follow-up". Replace that summary so it reflects the shared predicate. Replace lines 193–205 with:

```csharp
    /// <summary>Rejects a token carrying a character that is unsafe to embed in the
    /// exported script — the last gate every emitted token passes through. The unsafe
    /// set lives in <see cref="PlanText.IsUnsafe(char)"/>, shared verbatim with the
    /// author-time guards in <see cref="PlanModel.AddNode"/>/<see cref="PlanModel.RenameNode"/>
    /// (#77: they no longer drift). It closes the single-quote BREAKOUT the 0.2 audit
    /// reproduced — PowerShell's tokenizer treats U+2018..U+201F as string delimiters, so
    /// a near-invisible smart quote (e.g. U+2019) would terminate the single-quoted literal
    /// early and inject code — plus all control chars (incl. U+0085 NEL / the C1 range) and
    /// U+2028/U+2029. The ASCII apostrophe U+0027 is NOT rejected; it is the safe doubled
    /// case in <see cref="Ps1"/>.</summary>
```

Also fix the stale clause in the `Ps1` doc comment (lines 185–188), which says author-time "rejects control characters" — replace `<see cref="PlanModel.AddNode"/> rejects control characters at author time as a first line` with `<see cref="PlanModel.AddNode"/> rejects the same unsafe set at author time as a first line`.

- [ ] **Step B10: Run the Core tests — new pass, exporter regression intact**

Run:
```bash
dotnet test tests/GroupWeaver.Tests/GroupWeaver.Tests.csproj --filter "FullyQualifiedName~Core.Plan" -v minimal
```
Expected: the new `PlanModelTests` cases pass. **CORRECTION (found during execution 2026-06-17):** two existing `PlanScriptExporterTests` theories — `ToPowerShell_NameWithUnicodeQuoteDelimiter_ThrowsPlanScriptException` and `ToPowerShell_NameWithNonAsciiLineBreak_ThrowsPlanScriptException` — delivered their unsafe char through the **Name** channel via `AddNode("GG_Sales" + char)`. Now that `AddNode` rejects those chars (the point of #77), it throws `PlanConflictException` *before* the exporter runs, so those 11 cases fail. This is NOT a src bug (do not weaken `AddNode`). Fix is test-side and coverage-preserving: add a `ForceName(plan, dn, newName)` helper (mirroring the file's existing `ForceSam`) that injects the unsafe char directly into `PlanObject.Name`, bypassing author-time validation, and rewrite the two theories to `AddNode("GG_Clean")` then `ForceName(node.Dn, "GG_Sales" + char)` — exactly the defense-in-depth pattern the control-char and SAM theories already use. This keeps the exporter-Guard coverage on the name-token emission path. Owned by `test-engineer` (a deliberate, reviewed test update). After it: full `Core.Plan` suite green.

- [ ] **Step B11: Add the ViewModel parity test (App), then run it**

The author-time fix flows to the UI for free: `AddNode`/`RenameNode` already throw `PlanConflictException`, and `PlanViewModel` already catches it into `EditError`. Prove the *new* chars surface through that path by mirroring the existing BEL test. Add to `tests/GroupWeaver.App.Tests/PlanModeEditorTests.cs`:

```csharp
    /// <summary>
    /// #77 author-time parity through the ViewModel: a curly quote (U+2019) — which the
    /// exporter's Guard rejects — must now also be rejected at add time, surfacing as
    /// <see cref="PlanViewModel.EditError"/> with NO node added (early feedback, not an
    /// export-time surprise).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AddObject_CurlyQuoteName_SetsEditError_NoNodeAdded()
    {
        var plan = HeadlessPlan();
        plan.NewObjectKind = PlanCreatableKind.GlobalGroup;
        plan.NewObjectName = "GG_Sales’"; // RIGHT SINGLE QUOTATION MARK — a PS string delimiter

        await plan.AddObjectCommand.ExecuteAsync(null);

        Assert.NotNull(plan.EditError);
        Assert.Empty(plan.Nodes); // nothing was authored

        plan.Dispose();
    }
```

Run:
```bash
dotnet test tests/GroupWeaver.App.Tests/GroupWeaver.App.Tests.csproj --filter "FullyQualifiedName~AddObject_" -v minimal
```
Expected: PASS, including the pre-existing `AddObject_ControlCharName_SetsEditError_NoNodeAdded` (BEL is still caught) and the new curly-quote test.

- [ ] **Step B12: Full local gate**

Run:
```bash
pwsh tools/build.ps1
```
Expected: restore + build succeed; `dotnet format --verify-no-changes` reports no changes (the PostToolUse hook already formatted edited `.cs` files); **all tests pass, 0 failed** (count is the prior total + 5 new Core theory/fact cases + 1 App test).

- [ ] **Step B13: Commit**

```bash
git add src/Core/Plan/PlanText.cs src/Core/Plan/PlanModel.cs src/Core/Plan/PlanScriptExporter.cs tests/GroupWeaver.Tests/Core/Plan/PlanModelTests.cs tests/GroupWeaver.App.Tests/PlanModeEditorTests.cs
git commit -m "fix(plan): author-time parity with the export-time unsafe-char guard (#77)

PlanModel.AddNode rejected only ASCII control chars and RenameNode rejected none,
while PlanScriptExporter.Guard rejected the wider set (control + U+2028/U+2029 +
the U+2018..U+201F curly-quote block). Extract the predicate to a shared
PlanText.IsUnsafe and route AddNode, RenameNode, and Guard through it so author-
time and export-time can no longer drift. Smart-quote names now fail fast in the
editor (EditError) instead of only at export."
```

- [ ] **Step B14: Push, PR, Definition of Done for Group B**

```bash
git push -u origin fix/77-plan-char-parity
gh pr create --fill --base main
```
PR body for `reviewer`: *"#77; extracts one shared unsafe-char predicate (PlanText), gives AddNode/RenameNode author-time parity with the export Guard, refactors Guard onto it. ASCII apostrophe stays allowed. No AD-write path; Plan stays read-only/inert."*
  - `reviewer` approves; full gate green (B12); push → `ci-sentinel` until CI green.
  - Close #77: *"AddNode/RenameNode now share PlanText.IsUnsafe with the exporter Guard; smart-quote/separator/NEL names rejected at author time."*

---

## Task Group C — #78: dark-theme demo GIF

This group is media/tooling, not TDD. Verification is visual: read captured frames, judge dark theme + no identity leak + dimensions.

**Files:**
- Regenerate (binary): `docs/media/m2-explore.gif`
- Modify: `README.md`

- [ ] **Step C1: Create the branch**

```bash
git switch -c chore/78-dark-demo-gif
```

- [ ] **Step C2: Record the GIF in demo mode (dark theme is automatic)**

Run:
```bash
pwsh tools/record-demo-gif.ps1
```
Expected: the script builds the app if needed, launches it, UIA-clicks "Demo mode" **off camera**, drives the root picker → root click → lazy-expand → wheel-zoom beats, captures frames to `artifacts/ui/gif-frames/` (gitignored), and assembles `docs/media/m2-explore.gif` via ffmpeg. It must **never** invoke "Connect to domain". If the script exits non-zero, treat it as a blocker and triage (do not hand-edit frames).

- [ ] **Step C3: Verify the captured frames are dark and leak-free**

The first captured frame is the root picker (chrome), now dark; later frames are the graph. Read two frames and judge:

Read: `artifacts/ui/gif-frames/frame_001.png` and a late frame (e.g. `artifacts/ui/gif-frames/frame_015.png` — pick any mid/late index that exists).

Judge against `docs/ui-checklist.md` and these hard checks:
- **Dark chrome:** background is the dark shell, not light. (The shell flipped to Dark on 2026-06-15; a light frame means a stale build — rebuild and re-record.)
- **No identity leak:** no frame shows a real/lab identity (e.g. `AGDLP\Administrator`) — the connect card must not appear; frame 1 must already be at the root picker or later (the 2026-06-12 reviewer rejection was exactly an identity-leaking connect-card frame).
- **Legibility:** node colors per type distinguishable, no overlap, edges legible at the demo node count.

- [ ] **Step C4: Verify the GIF dimensions**

Run:
```bash
pwsh -NoProfile -Command "Add-Type -AssemblyName System.Drawing; \$i=[System.Drawing.Image]::FromFile((Resolve-Path 'docs/media/m2-explore.gif')); ('{0}x{1}, {2} frames' -f \$i.Width,\$i.Height,\$i.GetFrameCount([System.Drawing.Imaging.FrameDimension]::Time)); \$i.Dispose()"
```
Expected: width `960`, a sensible height (~600–630), and a frame count > 1 (the prior was 960×627, 21 frames). If it is 1 frame or not 960 wide, the assembly failed — re-run C2.

- [ ] **Step C5: Re-embed the refreshed GIF in README**

The GIF is currently tracked but unreferenced — refreshing it without using it leaves dead weight. Add it where motion adds value: the demo-mode section. In `README.md`, the demo-mode paragraph ends with the verbatim line:

```
All screenshots and GIFs published for this project are produced in demo mode
only — never against a real directory.
```

Immediately **after** that line, insert a blank line and:

```markdown
![GroupWeaver in demo mode — selecting a root, lazy-expanding a group, and zooming the AD-centered graph, all under the dark theme](docs/media/m2-explore.gif)
```

(If the executor/reviewer prefers the animated GIF as the top hero instead, that is an acceptable alternative: swap `README.md:12`'s `graph-explore.png` embed for the GIF and drop this insert. Pick one; do not leave the GIF unreferenced.)

- [ ] **Step C6: Confirm README references resolve**

Run:
```bash
grep -nE '!\[.*\]\((docs/media/[^)]+)\)' README.md
ls docs/media/
```
Expected: every referenced path (`graph-explore.png`, `gap-analysis.png`, `m2-explore.gif`) exists in `docs/media/`.

- [ ] **Step C7: Commit**

```bash
git add docs/media/m2-explore.gif README.md
git commit -m "docs(media): re-record the demo GIF under the dark theme and re-embed it (#78)

The shell flipped to the dark theme on 2026-06-15, staling the light-mode GIF
(which had also fallen out of the README hero). Re-recorded in demo mode (no
real/lab AD), dark, and embedded in the demo-mode section so the artifact is
used rather than orphaned."
```

- [ ] **Step C8: Push, PR, Definition of Done for Group C**

```bash
git push -u origin chore/78-dark-demo-gif
gh pr create --fill --base main
```
PR body for `reviewer`: *"#78; re-recorded the demo GIF dark (demo-mode only — UIA-clicks 'Demo mode' off-camera, never 'Connect to domain'), 960px, re-embedded in the demo-mode section. Public-media rule honoured."*
  - `ui-verifier` (or the C3 manual judgement) confirms the frames; `reviewer` approves (special attention: public-media rule — no real/lab identity in any frame); push → `ci-sentinel` until CI green.
  - Close #78: *"Demo GIF re-recorded under the dark theme and re-embedded; no identity leak."*

---

## Task Group D — #54: graph-layer reachability pruning  ⚠️ DEFERRED / DESIGN-GATED

**Do NOT implement code in this group.** Per the data-model rules and issue #54, graph-layer reachability pruning is "the only sanctioned future alternative and needs its own ADR" because it (a) breaks `GraphBuilder` totality (ADR-004 D1 / ADR-009 D6 pure-projector), (b) creates graph-vs-report node-set divergence (a pruned node could still carry a finding the AP 3.4 sidebar lists), and (c) re-opens the no-overlap geometry proof (`GraphBuilderGeometryTests` + the Playwright render-space assert). It is explicitly **low priority** — "pick up only if user feedback shows the node-Refresh residual matters." Reload-scope is already the one-click ergonomic cure (#30/ADR-005 addendum).

Writing bite-sized TDD steps here would be fabricating an undesigned feature. The only honest deliverable now is the design.

**Files:**
- Create: `docs/adr/016-graph-reachability-pruning.md` (DRAFT, status `Proposed`)

- [ ] **Step D1 (only proceed if user feedback justifies it): Brainstorm, then draft ADR-016**

Use `superpowers:brainstorming` first, then draft `docs/adr/016-graph-reachability-pruning.md`. The ADR must resolve, at minimum:
  - **Where pruning lives:** in `GraphBuilder` with `DirectorySnapshot` untouched (the snapshot stays append-only — non-negotiable per the data-model rules). Confirm no snapshot `RemoveObject` is introduced.
  - **Reachability definition:** prune nodes not reachable from the scope root / with no surviving edge — and define precisely what "root" and "surviving edge" mean against the null-vs-empty tri-state, so an *unexpanded* (null-members) parent is never pruned (that would re-tell the "unexpanded = unchecked" lie).
  - **Totality vs. pruning:** how to reconcile with ADR-004 D1 ("edges never dropped") / ADR-009 D6 — likely a *new* documented exception, gated behind an explicit option so the default stays the pure projector.
  - **Graph-vs-report divergence:** the resolution for a pruned node that still carries a `RuleReport` finding (the AP 3.4 sidebar's "jump to node" must not dangle). Either exclude pruning from finding-bearing nodes, or define the sidebar's behavior for pruned nodes.
  - **Geometry proof:** how `GraphBuilderGeometryTests` and the Playwright render-space no-overlap assert are updated/extended for the reduced node set.

- [ ] **Step D2: STOP. Get the ADR reviewed and accepted before any implementation.**

Do not write production code for #54 in this plan. Once ADR-016 is accepted (status `Accepted`) and user feedback has confirmed the node-Refresh residual matters, write a *separate* implementation plan against the accepted ADR. Leave #54 open with a comment linking the ADR draft.

---

## Out of this plan's scope (recorded for completeness)

These appear in `PLANNING.md` but are deliberately **not** task groups here:
- **O5 — winget submission:** blocked on a Windows 11 test environment, which this disposable Server 2022 lab box does not provide. Backlog; revisit when such an environment exists.
- **v0.4 — Entra ID / M365 (Graph API):** a large new feature (dynamic groups, distribution lists). Needs its own `superpowers:brainstorming` + spec + ADR cycle before a plan can be written — out of scope for an "open points" cleanup.
- **ADR-015 extensions — `DiffStatus.Modified`, Cross-Scope-Rebase:** explicitly tracked as v0.4 gap-analysis follow-ups; design-gated like #54, not bite-sized today.

---

## Self-Review (performed against the four issues + PLANNING.md §11)

1. **Coverage:** #79 → Group A; #77 → Group B; #78 → Group C; #54 → Group D (design-gated, honestly not implemented). PLANNING.md §11 open points O1–O4 are resolved; O5 + v0.4 + ADR-015 extensions are listed under "Out of scope" with reasons. No open item is silently dropped.
2. **Placeholder scan:** every code step shows real, verbatim code or an exact command with expected output. Group D contains *no* fabricated implementation steps — that is intentional and correct (the feature is undesigned; inventing TDD steps would be a placeholder). The README insert is anchored on a verbatim existing line.
3. **Type/identifier consistency:** `PlanText.IsUnsafe` / `PlanText.ContainsUnsafe` are defined once (B4) and referenced consistently in B5/B6/B8. The broadened `PlanConflictException` message is byte-identical in B5 and B6. `Guard` keeps its own `PlanScriptException` + message (B8). The new tests use identifiers proven to exist in the target files (`BaseOu`, `TryGetNode`, `plan.Nodes`, `HeadlessPlan()`, `NewObjectKind`/`NewObjectName`/`AddObjectCommand`/`EditError`, `PlanCreatableKind.GlobalGroup`). Action SHAs in A are the verified resolved commit hashes.
4. **Rules check:** no AD-write path is introduced anywhere; Plan stays read-only/inert; commit/PR prose avoids naming `*-AD*` cmdlets (guard-hook safe); commits use `git commit -m` separate from `git push` (no `-F`+push-in-one-Bash false positive); branches are short-lived → PR → reviewer → squash (trunk-based).

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-17-open-points-backlog.md`. Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task group (or per task), review between tasks, fast iteration. Maps cleanly onto this repo's subagents: `implementer` for B's code, `test-engineer` for B's tests, `ui-verifier` for C's frame judgement, `ci-sentinel` after each push, `reviewer` as the merge gate.
2. **Inline Execution** — execute the task groups in this session with checkpoints for review.

Recommended order regardless of mode: **A (#79, release-blocking) → B (#77) → C (#78) → D (#54, only if feedback justifies, ADR-first).**
