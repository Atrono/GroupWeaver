# ADR-014: Plan Mode — author a proposed AGUDLP structure, live-validate, export an inert script

## Context

v0.2's headline feature (issue #59, PLANNING.md E7) lets a user design a *proposed*
group structure, see it live-validated by the existing rule engine, and export it as a
PowerShell script they run themselves. GroupWeaver is and stays read-only toward AD
(CLAUDE.md non-negotiable) — the whole feature must produce something that *will* change
AD without the app ever writing to it.

The read model is wrong for editing: `DirectorySnapshot` is append-only (`AddObject`
upserts, `SetMembers` replaces a whole member list, there is no `RemoveObject` and no
single-edge op), and its `null`-vs-`[]` tri-state is a *load-state* concept meaningless
in a fully-authored plan. The graph WebView is strictly read-only (no edit gesture, no
inbound edit message, positions are .NET-precomputed, no live layout engine), and this
build environment cannot inject canvas mouse gestures for headless verification
(.claude/rules/lab-environment.md).

## Decision

1. **A separate mutable `PlanModel` (Core/Plan), not an editable snapshot.** It owns
   add/remove of nodes and single edges, removal cascade, and rename (replace-by-DN with
   endpoint rewrite, since the DN is identity). Edges reuse `MembershipEdge`
   (Dn.Comparer, direction-sensitive). Cycles and self-membership are *authorable* — they
   are findings the engine reports, not structural errors.

2. **Reuse the engine and builder UNCHANGED via a pure projection.**
   `PlanProjection.ToSnapshot(plan)` builds a fresh `DirectorySnapshot`; `SetMembers` is
   called for every group even when empty (loaded-empty `[]`), so the empty-group rule
   fires and `RuleReport.UncheckedDns` is empty by construction. No new `Evaluate`
   overload, no `GraphBuilder` edit. New object DNs are `CN=<RFC-4514-escaped name>,
   <BaseOuDn>`, stored as-formed.

3. **Plan Mode is a sibling shell step (`PlanViewModel`)**, reachable from the workspace
   header, returning to the *same* (never-disposed) `WorkspaceViewModel` so the Ist load
   and viewport survive. Not a Workspace toggle, not a second window.

4. **A panel-based editor; the read-only graph is the live preview.** Forms mutate
   `PlanModel`, then re-project → `GraphBuilder.Build` → `RuleEngine.Evaluate` →
   `UpdateGraphAsync` (the `ApplyRulesetAsync` loop, triggered by edits).

5. **Export is a pure-Core inert-string generator** (`PlanScriptExporter.ToPowerShell`)
   written to a user-picked file via the existing `IExportFileDialogs` seam
   (`ExportKind.Ps1`). The app never executes it. Injection safety: every untrusted token
   is emitted only inside a PowerShell single-quoted literal with `'`→`''` doubling;
   control characters are rejected at author time and at emission.

## Consequences

- `RuleEngine`, `GraphBuilder`, `DirectorySnapshot`, `MembershipEdge` are untouched; the
  pinned `WorkspaceViewModel` test surface is untouched.
- The base OU renders as a raw-DN External root node (a backdrop); authoring it as a real
  OU node is a future additive, not a contract change.
- `ComputeBelow` is copied into `PlanViewModel` (it is `private static` in
  `WorkspaceViewModel`); the shell now holds and disposes both the workspace and plan
  steps, and `OnRulesetApplied` re-threads a live plan step too.
- The read-only invariant holds by construction: the plan never leaves memory except as
  text the user themselves chooses to run.

## Rejected alternatives

- **Make `DirectorySnapshot` editable (add `RemoveObject`):** breaks the append-only
  data-model contract that cycle detection and refresh semantics depend on.
- **A second `Evaluate(PlanModel, …)` overload:** duplicates engine logic; the ~15-line
  projection reuses the exact engine.
- **In-WebView drag-to-connect editing:** needs new wire messages, node placement, a live
  layout engine, and Playwright audits, and is un-injectable in this headless context —
  deferred (possibly never).
- **Second top-level window:** doubles the headless-hostile modal `[I]` path.
- **Seed plan from Ist as the v0.2 default:** External/unloaded/out-of-base-OU edge cases
  — deferred; empty-start ships first.
- **Reject self-membership (A→A) in the model:** would make the plan unable to reproduce a
  finding the live tool reports; the UI disables the combo instead.
