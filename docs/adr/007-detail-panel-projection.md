# ADR-007: Detail panel — snapshot-only reads, immutable whitelist projection

**Status:** Accepted · **Date:** 2026-06-12
**Decides:** PLANNING.md AP 2.5 (detail panel data source and binding shape) · **Phase:** 2

## Context

A node click sets WorkspaceViewModel.SelectedDn (the AP 2.2/2.3 seam). The panel
must show ONLY the provider-filtered AdObject.Attributes plus the typed members
Dn/Kind/Name/SamAccountName (data-model rule; privacy baseline of AP 1.5). The
snapshot is not thread-safe, the one global busy gate drops overlapping work,
and expand/refresh upsert objects via AddObject — a selected object can be
replaced under an unchanged SelectedDn.

## Decision

1. **Selection reads only the snapshot.** Clicks never call the provider;
   freshness is exclusively the expand/Refresh pipeline's job. The projection
   is recomputed at three points: SelectedDn change, and the completion
   (finally) of the load and expand pipelines — every pipeline run re-projects.
2. **The view binds an immutable projection, never the domain object.**
   DetailPanelModel.Build(snapshot, dn) is the single choke point producing
   header (the four typed members) + rows (the Attributes dictionary verbatim —
   the UI never re-filters; a provider whitelist bug must become visible, not
   masked). DetailPanelView's x:DataType is the projection: binding anything
   else is structurally impossible without editing Build. Pinned by a
   rows-mirror-Attributes-exactly test and a live row-labels ⊆ display-set test.
3. **Load-state honesty from snapshot state alone:** DN absent or
   External ∧ ¬IsLoaded → "not loaded, expand/Refresh to resolve";
   External ∧ IsLoaded → unresolvable (FSP) — attributes genuinely unavailable.
4. Row order: AttributeWhitelist.FetchProperties order, unknown keys appended
   alphabetically. Values wrap and are selectable; DN shown verbatim (never
   canonicalized). Panel lives in the existing DetailPanelRegion beside the
   graph (ADR-001 guardrail 5) under the ADR-005 Refresh header.

## Consequences

- Zero provider traffic and zero busy-gate interaction on click; selection
  stays responsive during any in-flight pipeline.
- A refreshed/expanded object updates the open panel automatically; a stale
  panel is impossible by construction.
- AP 3.4's sidebar inherits the pattern: read the snapshot, project immutably,
  never bind domain objects into XAML.

## Rejected alternatives

GetObjectAsync on click (new async/error/busy surface; races the non-thread-safe
snapshot or queues behind the gate; duplicates Refresh); child observable
DetailPanelViewModel (no commands/state of its own — wiring weight under the
manual composition root); UI-side attribute re-filtering (masks provider bugs;
enforcement is the provider's contract); truncating long DNs (the panel IS the
full-value surface; values wrap and are selectable instead).
