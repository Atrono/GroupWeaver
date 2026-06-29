# GroupWeaver UX Fit-Audit — June 2026

**Question asked:** *now that the tool's purpose is understood, does the UI/UX fit it?*
**Answer:** Yes — well — with three structural seams worth addressing. This document is the evidence.

**Method:** an adversarial fleet (7 per-surface critics → 14 persona-jurors → 1 synthesizer) attacked each
surface against the target persona, every finding cross-examined for *real impediment vs. nitpick* and
checked against the ADRs so settled calls are not re-litigated. Read-only analysis; demo-mode renders only.
No source was changed, no issue/ADR was opened, no AD was touched. Acting on any finding below is a separate,
explicit decision.

**Scale:** 39 findings raised · 5 dropped as fit-confirmed non-findings · **34 surviving** → **15 bucket-A**
(genuine misfit worth changing) · **5 bucket-B** (accept-with-rationale / settled) · **14 bucket-C**
(backlog / minor).

---

## 1. Verdict

GroupWeaver fits its purpose well as a **point-in-time AGDLP/naming auditor.** The spine the persona needs —
*launch → pick scope → explore → spot color-coded violations → drill → export* — is intact and coherent. The
tri-state honesty about unchecked areas is genuinely load-bearing and respected end-to-end; the read-only
contract is never threatened; the per-finding `WhyItMatters` / `HowToFix` content is a real strength; and the
WCAG-AA redundant-channel accessibility (shape + letter + pattern, not hue alone) is settled and strong. The
June redesign closed the gaps that would have made it merely "correct but unprincipled."

The seams are not in the snapshot audit. They are where the tool meets the persona's **actual** job rather
than an idealized one:

1. **Reach.** The tool can only audit the single domain the box is joined to (current-user-against-detected-
   domain only), and the whole-domain root is not even a pickable scope — stranding the consultant / MSP /
   multi-domain auditor, *even though `LdapProvider` already carries the `server`/`baseDn` parameters to lift
   the ceiling.*
2. **The recurring cadence that defines this persona is unserved.** No baseline, no history, no answer to
   "what changed since last quarter," no scope memory. The tool gives an excellent photo but no film.
3. **The headline can lie by omission.** A green "Excellent" health band can sit above a live structural
   Error, and triage can lift the score without the summary disclosing it — an honesty failure in precisely
   the artifact that gets screenshotted into an attestation.

None of the high-value fixes require relaxing scope or writing to AD; the biggest ones are **wiring and
surfacing of capabilities the Core already has.**

### Two hypotheses the audit killed (the pass has teeth)

The adversarial jury **dropped or demoted two of the opening critiques**, which is the audit working —
plausible-but-wrong findings did not survive:

- *"Graph-first is the wrong front door for a findings-primary persona"* → **demoted.** The enumerable,
  exportable **findings rail sits right next to the graph on the workspace step** — it is not "one screen
  deeper." Only the full triage *table* is one click away. Graph-first *explore → spot → drill* is the
  settled, sensible product flow (both jurors corrected the premise). Residual grain: a returning power-user
  might want to land straight on the table.
- *"Plan + Gap (2 of 6 steps) dilute the audit core"* → **dropped.** Plan is opt-in from the explore step,
  never on the default audit path, and the redesign's dedicated **Audit screen** is now the home of the audit
  core, so the two no longer compete for surface. This is the worry's own rebuttal.

---

## 2. Top levers (ranked)

The five highest-value changes. Each absorbs several individual findings (noted), and each respects read-only
+ the permanently-out-of-scope "P".

### Lever 1 — Honest top-line: never read green over a live Error, and disclose triage *(Audit · needs ADR)*
The band is a pure threshold switch on the scalar score with **no severity floor**: `BandFor` returns
"Excellent" for any score ≥ 90 (`AuditSummary.cs:170`), and the score is a per-subject penalty *average*
(`AuditSummary.cs:152-154`), so the same finding dilutes as the directory grows — **1 unresolved circular-
nesting Error among ~2000 checked subjects ≈ 99.85 → 100 → "Excellent."** Separately, Acknowledge/Suppress
drop findings from the live report that drives the score (`AuditViewModel.cs:995-1000`), so the ring can go
green after triage with **no "(N suppressed)" mark** on the summary that gets screenshotted.
**Fix:** cap the headline band by worst *live* severity, independent of the scalar (any live Error caps at
Poor/"Action required"; any live Warning caps below Excellent); keep the number only as a secondary trend
metric. Render a triage caveat on the band when Acknowledged/Suppressed > 0 (reusing the already-computed
would-be report), and stamp the suppression count + ruleset name into the CSV/HTML export. *Subsumes the two
bucket-A Audit findings.*

### Lever 2 — Lift the single-domain reach ceiling *(Connection + RootPicker · needs ADR)*
The live path always builds `new LdapProvider()` with no arguments (`App.axaml.cs:46`, `Program.cs:84`) —
serverless bind to the joined domain — yet the provider **already exposes `server`/`baseDn` ctor parameters**
(`LdapProvider.cs:44-51`) reachable from no UI. The whole-domain root (`DC=…`) is also never a pickable
candidate (`LdapProvider.cs:97,104`); only OUs and groups are.
**Fix:** an optional *"Advanced / target a specific domain or DC"* disclosure on the Connect card that surfaces
the existing `server`/`baseDn` parameters (still integrated-auth, still no stored credentials); add the
domain root as a first-class scope candidate; and show the detected target FQDN in the connect helper text so
the auditor confirms *which* domain before a large pull. *Subsumes Connection "multi-domain" + "names user not
domain" and RootPicker "whole-domain not pickable."*

### Lever 3 — Serve the recurring cadence: audit baseline / history + drift comparison *(CrossCutting + Gap + Audit · needs ADR)*
Persistence today is only `ui-state.json` + `ruleset.jsonc`; **no code path saves a snapshot, report, or
finding set** for later comparison (`UiStateStore.cs`). The persona's defining question — *"what changed since
last quarter?"* — is unanswerable; Gap diffs Plan-vs-Ist, never Ist-vs-prior-Ist.
**Fix:** persist an opt-in deterministic *"audit run"* artifact (RuleId + PrimaryDn + Severity + canonical
Dns + root DN + ruleset name/hash + timestamp + UncheckedDns) to `%APPDATA%\GroupWeaver\runs\`, then a
*"Compare to previous run"* view (Fixed / New / Still-open / Now-unchecked) reusing the `SnapshotDiff` pattern.
Read-only, no AD writes. *Subsumes Gap "no drift axis," CrossCutting "no baseline/history," and the history
half of "scope memory."*

### Lever 4 — Make Plan/Gap usable on a real directory: seed plan from Ist *(Plan + Gap · already AP 4.2.3)*
`OnDesignPlan` seeds the plan **empty** at the root (`ShellViewModel.cs:462-465`); there is no seed-from-Ist
path. To express even a one-edge remediation ("nest this DL under that GG") the auditor must hand-retype every
object the fix touches by exact name — and a single typo forms a different DN, so Gap shows it as Added/Removed
instead of a match. This makes Plan/Gap usable only for toy what-ifs, the inverse of "see current state fast."
**Fix:** ship the already-named **AP 4.2.3 seed-from-Ist** — prime a plan from the loaded Ist scope (loaded
in-base-OU nodes + edges; External/unloaded excluded with an honest banner mirroring Gap's Unchecked banner),
editable to the target. Pure `PlanModel` construction over the borrowed snapshot, no AD write. *Subsumes Plan
"empty-start" + Gap "hand-author from empty," and unblocks the searchable-combos finding.*

### Lever 5 — One management-ready, self-describing consolidated report *(CrossCutting + Workspace + Audit)*
The exporter emits only the `RuleReport` table (`ViolationReportExporter.cs`); there is **no document bundling
the health verdict, the count tiles, the rule-class roll-up, and the per-finding rationale.** Worse, two
export buttons (rail + Audit) can yield different artifacts after triage — *"which is the audit of record?"*
**Fix:** a consolidated *"Audit report"* HTML export from the Audit screen composing `AuditSummary` band +
count tiles + `ByRuleClass` categories + findings table + a per-rule-class `WhyItMatters`/`HowToFix` appendix,
self-stamped (ruleset name, triage-applied flag, unchecked count, timestamp). Make the Audit screen the
canonical export surface and demote/reframe the rail export so there is one audit of record. *Subsumes
Workspace "two finding lists / authority ambiguity."*

**Cheap wins outside the top 5 (high value / low cost):** add a Gap **Export CSV/HTML** (the one sibling
surface with no export); trim the RootPicker DN **from the left** (or add a hover tooltip) so same-named OUs
are distinguishable; surface `WhyItMatters` on the **explore→drill** path (the copy already exists); wire the
rail **severity chips** to actually filter (they read as clickable but do nothing).

---

## 3. Per-surface findings

Severity/status shown are the **jury-revised** values. *Status* = New / Settled / Backlog. *Jury* = consensus
of two persona-jurors (keep / demote / drop).

### Connection (Step 1)
Clean, well-signposted first-run (accent Connect / ghost Demo, honest demo badge downstream — recorded as
**fit-confirmed, dropped**). The real gaps are about *reach* and *evidence discipline*, not the surface's look.

- **[A · High · New · keep] Cannot target any domain except the one you are logged into.** `new LdapProvider()`
  with no args (`App.axaml.cs:46`); the `server`/`baseDn` capability exists but is exposed nowhere
  (`LdapProvider.cs:44-51`). *Impact:* the plural-directory auditor (customer/child/forest-root domain over a
  trust, hardened audit workstation) is hard-blocked. → **Lever 2.**
- **[A · Med · New · keep] Connect-failure dead-ends with a "try Demo mode" nudge, not triage.**
  (`ConnectionViewModel.cs:71-73`) *Impact:* steering a mid-audit auditor to synthetic data is the least useful
  next step and risks demo findings being mistaken for evidence. *Fix:* lead with triage (domain-join / network
  / VPN / read-rights / retry); demote the Demo hint.
- **[B · Low · Settled · demote] No dedicated read-only audit-account path.** By E2 ("keine Credentials im
  Code") + ADR-003 D7. *Residual:* discoverability — one line noting *"launch via Windows Run-as / `runas
  /netonly` to audit as the audit account."* No code/contract change.
- **[C · Low · New · demote] Names the user but not the target domain before connecting.**
  (`ConnectionView.axaml:51-53`) Near-zero risk today (one possible target); earns its keep once Lever 2 lands.

### RootPicker (Step 2)
The flat virtualized list (tree deliberately rejected for scale) is sound; the gaps are *coverage* and
*disambiguation*.

- **[A · Med · New · demote] Whole-domain audit is impossible — the domain root is never pickable.**
  (`LdapProvider.cs:97,104`; header literally "Choose a root — OU or group") *Correction from jury:* every
  group *is* individually pickable and a picked root loads its whole subtree, so this is a one-pass-coverage
  gap, not "unauditable." → **Lever 2.**
- **[A · Med · New · keep] Flat list + right-trimmed DN can't disambiguate same-named OUs.**
  (`RootPickerView.axaml:40-41`) *Impact:* five "Groups" OUs look identical; picking the wrong scope silently
  audits the wrong thing. *Fix:* trim the DN from the left / show parent path / hover tooltip. → **cheap win.**
- **[C · Med · New · mixed] One mandatory root per session, no cross-scope aggregate.**
  (`RootPickerViewModel.cs:53,114`) Forces N passes + hand-stitched reports for a directory-wide verdict.
  Largely collapses into Levers 2 + 3.
- **[C · Low · New · demote] No result count / distinct empty-state** — filter-matched-nothing vs genuinely-empty
  both render blank (`RootPickerViewModel.cs:101`). The AuditView two-empty-state idiom is reusable.
- **[C · Low · New · keep] No scope-size hint before committing to a load** (`RootPickerViewModel.cs:114`).
- **[B · Low · New · keep] No remembered / recently-used scope** for the recurring auditor — merged with
  CrossCutting "scope memory," built alongside Lever 3 (both need a recorded root DN).

### Workspace (Step 3)
The graph + co-present findings rail + detail panel is the right shape (the "front door" critique was demoted,
above). Two genuine seams remain — both about *trustworthy, complete evidence.*

- **[A · Med · New · keep] Two finding lists (rail vs Audit) create authority ambiguity.** The rail binds the
  live post-suppression report; the Audit table shows the would-be (pre-triage) set (`ADR-028`), so on-screen
  counts diverge and there are two export buttons. *Impact:* "which export is the audit of record?" is a real
  governance-trail hazard. → **Lever 5** (one canonical, self-stamped report).
- **[A · Med · New · keep] "Unexpanded areas are unchecked" is a binary flag — no count, no list, no path to
  completeness.** (`WorkspaceViewModel.cs:295`, `ViolationsSidebarView.axaml:200`) *Impact:* the auditor needs a
  *complete* set; "some areas unchecked" with no number (is it 1 group or 500?) gives no coverage gauge and no
  efficient way to drive to done. *Fix (lazy-expand stays, ADR-005):* show the unchecked **count**, make
  `UncheckedDns` **navigable** (jump-to-next-unchecked), and offer a user-driven **bulk-expand of the current
  frontier** reusing the existing per-node fetch (needs an ADR for bulk semantics/cancellation).
- **[C · Low · New · demote] Rail list has no filter/sort/search; severity chips look clickable but do nothing.**
  (`ViolationsSidebarView.axaml:52,139`) Filtering is the Audit table's job (acceptable split), but the dead
  chips read as broken. *Fix:* wire the chips as a local severity filter. → **cheap win.**
- **[C · Low · New · demote] Graph-first front door** — *the demoted opening hypothesis* (§1). Findings rail is
  co-present; only the table is one click. Settled flow.
- **[B · Low · Settled · demote] Focus mode collapses the rail (and the unchecked caveat) to width 0** — ADR-022
  (opt-in, one-click-reversible via the seam chevron / Ctrl+B / Esc). *Residual:* a rail persisted-collapsed from
  a prior session hides the honesty caveat before the user touches anything — consider a tiny in-canvas
  unchecked indicator so the caveat survives focus mode.

### Plan (Step 4)
Forms-based editing (not drag-on-graph) is **fit-confirmed, dropped** — for this persona a form that names the
AGUDLP kinds and refuses an illegal edge is *better* than a Visio canvas, and ADR-014 settled it. The dilution
worry is also **dropped** (§1). The real problem is the empty start.

- **[A · High · Backlog · keep] Empty-start forces hand-retyping existing AD to model any realistic
  remediation.** (`ShellViewModel.cs:462-465`; `PlanModel.cs:61` de-dups only within the plan) → **Lever 4.**
- **[A · Med · New · mixed] Membership form uses flat, unsearchable ComboBoxes** (`PlanView.axaml:98-135`) —
  latent today (combos hold only what you typed), but **mandatory** the moment seed-from-Ist lands. *Fix:*
  type-to-filter / AutoCompleteBox reusing the in-graph Find pattern; show DN when names collide.
- **[C · Low · New · keep] No warning when authoring an object that already exists in the loaded Ist**
  (`ShellViewModel.cs:483` passes RootDn, not the snapshot). Costs a Gap round-trip, not a wrong conclusion.
- **[C · Low · New · mixed] Single edit-error line shares the scrolling column and can scroll out of view**
  (`PlanView.axaml:142-145`) — a rejected edit can read as a silent no-op. *Fix:* pin the error to a non-scrolling
  band or a per-card slot.

### Gap (Step 5)
The unchecked-areas honesty banner (separating "unchecked" from "removed") is genuinely good and credited. The
"objects/memberships" vs "groups/members" wording is **fit-confirmed, dropped** ("objects" is the precise term;
users/computers are nodes too).

- **[A · Low→(High via Lever 3) · New · demote] No drift axis — Gap can't answer "what changed since last
  audit?"** (`GapViewModel.cs:156-162`) Demoted *as a Gap-surface defect* (it's a new capability, not a bug in
  this screen) but the underlying need is the §2 Lever-3 history feature. → **Lever 3.**
- **[A · Med · Backlog · keep] Diffing requires hand-authoring the whole target from an empty plan.** Same root
  cause as Plan empty-start. → **Lever 4.**
- **[A · Med · New · keep] No export of the changes list — Gap produces no audit-trail artifact.**
  (`GapView.axaml`; every sibling surface exports) *Impact:* the change set — the highest-value change-review
  evidence — is screen-only. *Fix:* add Export CSV/HTML reusing the AuditView seam. → **cheap win.**
- **[C · Low · New · mixed] Honesty banner names "reload or expand" but offers neither in-place**
  (`GapView.axaml:64-67`). ADR-015 D5 *intends* the reload to be offered; the implementation only names it.
  *Fix:* a "Reload full scope & re-diff" button, or stop implying an unoffered action.
- **[C · Low · New · keep] Summary "N unchecked" (edge count) and the banner trigger (parent count) are two
  unlabeled numbers** (`GapKindConverters.cs:95-104`, `GapSummary.cs`). *Fix:* label the quantities; surface
  `UncheckedParents`.

### Audit (Step 6)
The strongest surface — health band, count tiles, categories, severity/status/rule-class filter chips, a
sortable triage table (acknowledge/suppress/reopen), two-pane detail with `HowToFix`, CSV/HTML export. The
seams are about the *headline's honesty*, not the workbench.

- **[A · High · New · keep] A single live Error can headline as "Excellent" on a large directory.** → **Lever 1.**
- **[A · Med · New · keep] Triage lifts the score to green with no "triaged" disclosure on the headline.** →
  **Lever 1.**
- **[C · Low · New · demote] Score weights (3/1/0.25) + band cutoffs are undocumented magic numbers**
  (`AuditSummary.cs:55-61,170`). Defensible evidence is the table/export, not the scalar; fix is to document the
  formula (tooltip + export header) or demote the score to advisory/trend (Lever 1 already does the latter).
- **[C · Low · New · demote] Unchecked caveat is honest but buried far from the score** (`AuditView.axaml:276`
  vs the ring at `:93`) — a tight crop of the ring can omit it. *Fix:* an inline "checked N of M" subline so the
  qualifier travels with the headline.
- **[C · Low · New · demote] No blast-radius cue for multi-DN findings** ("n below" roll-up; only cycles are
  genuinely multi-DN). Data (`Violation.Dns.Count`) already carried.

### Cross-cutting (IA · onboarding · scale · persistence · accessibility)
Accessibility/identity (per-kind shapes over circle+ring, E/W/i letters, declared tokens, WCAG-AA) is
**fit-confirmed, dropped** — checked and genuinely strong; must be inherited by any new surface.

- **[A · High · New · keep] No audit baseline/history — recurring-audit drift is invisible.** → **Lever 3.**
- **[A · Med · New · keep] Export is per-surface — no single consolidated report for management.** → **Lever 5.**
- **[B · Med→Low · New · mixed] `WhyItMatters`/`HowToFix` teaching is stranded on the Audit screen, absent from
  the explore→drill path** (`AuditFindingDetail.cs:118-166` consumed only by AuditView). *Impact:* a
  non-specialist drilling a flagged node reads "GG nested directly in a user" with no *why*. *Fix (cheap):* link
  the sidebar selection to the Audit detail, or an expandable "Why?" on the row — the copy already exists. →
  **cheap win.**
- **[B · Low · New · keep] No scope memory — every session re-picks the root** (`UiStateStore.cs`). Merged with
  RootPicker; co-built with Lever 3.
- **[C · Low · New · demote] First run assumes AGDLP fluency — no orientation primer.** Overlaps the stranded-
  teaching finding; README covers the model. A dismissible primer reusing existing copy is additive polish.

---

## 4. Triage roll-up

**Bucket A — genuine misfit worth changing (15)**

| Surface | Finding | Sev | Lever |
|---|---|---|---|
| Audit | "Excellent" can headline over a live Error | High | 1 |
| Audit | Triage lifts score with no headline disclosure | Med | 1 |
| Connection | Cannot target any domain but the joined one | High | 2 |
| Connection | Connect-failure nudges to Demo, not triage | Med | — |
| RootPicker | Whole-domain root not pickable | Med | 2 |
| RootPicker | Same-named OUs indistinguishable (right-trimmed DN) | Med | cheap |
| CrossCutting | No audit baseline/history (drift) | High | 3 |
| CrossCutting | No consolidated management report | Med | 5 |
| Gap | No drift axis (what changed since last audit) | High* | 3 |
| Gap | Hand-author target from empty plan | Med | 4 |
| Gap | No export of the changes list | Med | cheap |
| Plan | Empty-start forces hand-retyping AD | High | 4 |
| Plan | Unsearchable membership combos (latent) | Med | 4 |
| Workspace | Two finding lists → authority ambiguity | Med | 5 |
| Workspace | Unchecked = binary flag, no path to completeness | Med | — |

**Bucket B — accept-with-rationale / settled (5)**

| Surface | Finding | Settled by | Residual |
|---|---|---|---|
| Connection | No dedicated audit-account path | E2 / ADR-003 D7 | discoverability note (runas) |
| Workspace | Focus mode collapses the rail | ADR-022 | in-canvas unchecked echo |
| CrossCutting | Teaching stranded on Audit screen | — (cheap wiring) | link drill→detail |
| CrossCutting | No scope memory | ui-state idiom | co-build with Lever 3 |
| RootPicker | No remembered scope | ui-state idiom | co-build with Lever 3 |

**Bucket C — backlog / minor (14):** Audit score-weight documentation · Audit unchecked-caveat proximity ·
Audit multi-DN blast-radius cue · Connection target-domain-before-connect · RootPicker cross-scope aggregate ·
RootPicker result-count/empty-state · RootPicker scope-size hint · Workspace rail filter / dead chips ·
Workspace graph-first front door *(demoted hypothesis)* · Plan existing-Ist warning · Plan edit-error anchoring ·
Gap banner reload-offer · Gap unchecked-count labeling · CrossCutting first-run primer.

**Dropped — fit-confirmed non-findings (5):** Connect/Demo first-run clarity · Plan forms-vs-drag editing
(ADR-014) · Plan+Gap surface dilution · Gap "objects/memberships" wording · Accessibility/redundant-channel
identity (ADR-027/021). *Recorded so they are not "fixed" into regressions.*

---

## 5. What this audit is NOT

- **Read-only.** No source was changed, no AD was read, demo-mode renders only.
- **No work was created.** No GitHub issue, no ADR, no commit beyond this document. Turning any bucket-A
  finding into work is a separate decision; the levers that "need ADR" are flagged for that step.
- **No re-litigation.** Findings that contradicted a documented decision were tagged Settled with the ADR
  cited and placed in bucket B, not argued.
- **"P" is respected.** Not one recommendation proposes an AD write or an ACL/permission/fileserver audit —
  the permanently-out-of-scope boundary held across all 39 findings.

---

## 6. Evidence index

**Method artifacts:** workflow `wf_c4fb50a3-4da` (22 agents: 7 critics + 14 persona-jurors + 1 synthesizer).
**Headless renders (demo mode), `artifacts/ui/`:** Connection `connection-idle-1280x720.png`,
`connection-error-1280x720.png` · Workspace `workspace-demo-1280x720.png`, `workspace-violations-1280x720.png`,
`workspace-detail-1280x720.png`, `workspace-scope-summary-1280x720.png`, `workspace-rail-collapsed-1280x720.png`,
`workspace-focus-1280x720.png` · graph layer `_review_crops/overview-center-fresh.png` · Audit
`wp5b-audit-dark-1280x720.png`, `wp5d-audit-dark.png`, `wp5e-audit-triage-dark.png`, `audit-actions-dark.png` ·
Plan `wp6a-editor-dark.png` · Gap `_gap-header-1920.png`, `_gap-sidebar-zoom.png`, `wp1b-dark-diff.png`.
Per-finding `file:line` anchors are inline in §3.

*\*Gap "no drift axis" is Low as a Gap-surface defect but High as the persona need it points at (Lever 3).*
