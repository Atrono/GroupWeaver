# ADR-010: Severity on the graph — wire fields, overlay rendering, roll-up

**Status:** Accepted · **Date:** 2026-06-13
**Refines:** ADR-004 (wire contract, renderer seam), ADR-005 (replace-in-place)
**Consumes:** ADR-009 (RuleReport) · **Decides:** PLANNING.md AP 3.4, issue #40 · **Phase:** 3

## Context

AP 3.4 paints the ADR-009 report onto the graph: a per-node max-severity
indicator, a "n below" roll-up on loaded groups, a violations sidebar with
jump-to-node, and the "unexpanded areas are unchecked" hint. A node already
encodes its KIND via fill colour + shape (AdObjectKindConverters), and root +
External own the border (ADR-004). Severity needs a third, non-colliding visual
channel. The report is Dn.Comparer-keyed and the node id is the DN verbatim, so
the join is mechanical. GraphBuilder must stay a pure topology projector
(ADR-009 D6): severity is merged in the App wire mapper, never in Core.

## Decision

1. **Visual-channel law.** Kind owns `background-color` + `shape`; root/External
   own `border-*`; **severity owns the cytoscape `overlay-*` channel** (unused
   before this AP). The halo paints behind the node, touching neither fill,
   shape, nor border. Palette (disjoint from every kind fill, monotonic padding
   as a colourblind-redundant channel, pinned by a verify.mjs tripwire analogous
   to the kind PALETTE): Error `#D13438` / pad 7, Warning `#F7A30B` / pad 6, Info
   `#4FA3E3` / pad 5; opacity 0.45/0.45/0.40 on the `#1b1f27` page. No finding ⇒
   no `sev` field ⇒ overlay-opacity 0 ⇒ byte-identical to a pre-AP node.

2. **Wire contract delta — three optional `NodeDto` fields**, `WhenWritingNull`-
   ignored (unflagged nodes stay byte-identical): `sev` ("error"|"warning"|
   "info"), `below` (int, distinct findings among LOADED transitive descendants,
   emitted only when >0), `belowSev` (max severity among them). Joined in the ONE
   shared `GraphJson.MapNodes` path (flat dump + GraphChunker can't drift) from
   `RuleReport.MaxSeverityByDn` and a VM-computed below-map. `graph.js`'
   accumulator copies the fields into `data`; `node[sev=…]`/`node[below]` style
   rules sit AFTER the kind+root rules and win only on the overlay channel.

3. **Where Evaluate runs.** `RuleEngine.Evaluate` (pure/sync, ADR-009) runs in
   the VM at the two graph-build sites — LoadAsync and ExpandAsync's fetch branch
   — BEFORE the renderer call, inside the existing IsLoading window (no new gate).
   The cache-hit/focus-only branch does NOT re-evaluate (topology and ruleset
   unchanged). Full re-run per expand (ADR-009); severity rides ADR-005's
   remove-all/re-add for free because it is a re-sent wire field, never preserved
   cytoscape state. The ruleset is located once in the composition root
   (RulesetLocator → %APPDATA%\GroupWeaver\ruleset.jsonc) and threaded through the
   VM chain as a defaulted ctor param (null ⇒ embedded default); EffectiveRuleset.
   Errors is carried, surfaced by AP 3.3.

   **Refines ADR-005 D3 cache predicate.** The expand cache-hit guard narrows
   from "not `IsLoaded`" to "no cached members" (`!forceFetch && cachedMembers is
   { Count: > 0 }`): a loaded-and-EMPTY fetchable group now re-fetches on a normal
   expand instead of a focus-only camera move. An empty group has nothing to focus
   and may have gained members; re-fetching then re-Evaluating lets it shed its
   empty-group finding. This is a real (non-null) `GetMembersAsync` + `SetMembers`
   (replace semantics, ADR-005 D3 ordering preserved) — never a fabricated load,
   so the null-vs-empty contract holds (`null` still = never loaded = focus-only
   is unreachable for an unloaded node, which takes the fetch branch anyway).

4. **Roll-up = loaded-only Walk, never fetch, VM-computed.** `below` =
   `report.ViolationsAmong(MembershipTraversal.Walk(snapshot, dn).Visited.Skip(1))`
   over every loaded fetchable-kind node — Walk reads GetMembers per node
   (null = unexpanded = excluded, data-model contract), so the count is
   loaded-only by construction. Computed in the VM (the only sanctioned Walk
   consumer); JS never walks. On canvas the roll-up is a wider/fainter
   max-severity ring (no on-canvas number — canvas-only cytoscape has no
   pseudo-elements); the exact count is authoritative in the sidebar.

5. **Sidebar** binds `RuleReport.Violations` in canonical report order
   (unshuffled — ADR-009 pins this), each row a severity glyph (colour + redundant
   letter) + wrapping message + dimmed subject name, the whole row a jump command
   (FocusAsync + SelectedDn). Placed in a vertical split of the existing right
   column, above the AP 2.5 detail stack — beside GraphHost, never over it
   (ADR-001 airspace). The "unexpanded areas are unchecked" hint binds to
   `UncheckedDns.Count > 0` (never ignore-filtered); all-clear shows "No rule
   violations found." with the hint still firing if areas remain unchecked.

## Consequences

- One pure wire-field extension; the demo dump (`--demo --dump-graph`) runs
  Evaluate(default ruleset) so the Playwright fixture carries severity. The
  renderer seam (ShowGraphAsync/UpdateGraphAsync) grows a report parameter; the
  single-flight guard and chunking are untouched.
- The detail panel stays whitelist-only (severity is not an attribute); GraphBuilder
  stays report-blind; ruleset edits never force a graph rebuild (ADR-009).
- Per-expand below-map runs one Walk per loaded fetchable node; trivial at demo
  scale, O(loaded·(V+E)) worst case — fold into the engine sweep only if a
  profiled forest bites (ADR-006-reviewed Walk change, never a second walk).

## Rejected alternatives

Severity via border-colour (collides with root/External borders); severity via
fill tint (collides with the kind palette, breaks the parity tripwire); a numeric
on-canvas badge (needs an HTML overlay — airspace-forbidden over GraphHost — or a
compound child node — voids the no-overlap proof); overriding the node `label` to
carry the count (clobbers the kind name across zoom); JS-side Walk for the roll-up
(violates the single-Walk data-model mandate); merging severity in GraphBuilder
(couples ruleset edits to rebuilds — ADR-009); a JS-side severity cache surviving
graphUpdate (re-send is simpler and can't desync); severity-sorted sidebar
(unstable under AP 3.3 live preview — ADR-009); a third sidebar grid column
(steals graph width the no-overlap proof depends on).
