// graph.js - production cytoscape setup + command handlers (AP 2.2 S3, ADR-004).
// Dataset arrives from .NET as chunked bridge.dispatch({type:'graphChunk',...})
// calls (WebResourceRequested in Avalonia.Controls.WebView 11.4.0 cannot serve
// responses, so fetch()-based transfer is not possible - GraphSpike evidence).
// Wire contract (ADR-004 D4): node {id, label, kind, x, y, root?:true},
// edge {id, s, t, rel: 'member'|'contains'}; positions are .NET-precomputed
// (preset layout), kind strings are AdObjectKind enum names verbatim.
// Commit verbs: graphCommit = full init (destroy + fit), graphUpdate =
// replace-in-place on the live instance (ADR-005 D1, lazy expand AP 2.3).
(function () {
  'use strict';

  var cy = null;
  var pendingNodes = [];
  var pendingEdges = [];

  // ADR-026 WP1b: the graph canvas follows the app theme. The wire carries ONLY the
  // variant string ({type:'theme', variant:'dark'|'light'}); graph.js owns the dark+
  // light token TABLES itself (a mirror of BrandTokens.cs, per the ADR-021 hand-mirror
  // parity invariant — NO token values cross the wire). currentVariant defaults to 'dark'
  // so the FIRST render (before any theme command) is byte-identical to the shipped graph
  // (verify.mjs's computed-style asserts depend on this). THEME holds every themeable
  // value; buildStyle(THEME[v]) constructs the cytoscape style array from it. The kind
  // FILLS are theme-INVARIANT (all read >= 4.2:1 on the light canvas too, ADR-026 D5) and
  // stay hardcoded in the per-kind rules, identical in both themes — they are NOT in THEME.
  //
  // Parity (ADR-021/ADR-026): the DARK values below MUST equal the literals the bundle
  // shipped with; the LIGHT values mirror BrandTokens' *LightHex graph tokens and are
  // pinned by tests/graph-bundle/verify.mjs (the test-engineer adds a LIGHT block) and
  // WebBundleTests. Every WCAG ratio for the light values is documented in ADR-026 D5.
  var currentVariant = 'dark';
  var THEME = {
    dark: {
      canvasBg: '#1b1f27',          // body / #cy background (index.html mirror)
      labelInk: '#E8ECF2',          // node label color
      labelOutline: '#1b1f27',      // node label text-outline-color
      nodeLiftRing: '#8A93A3',      // 1.4.11 border-lift on DL/UG/Computer (2px)
      nodeLiftWidth: 2,
      rootBorder: '#E8ECF2',        // node[?root] white-ish border
      externalBorder: '#B0B6BF',    // External dashed border
      selectionBorder: '#FFFFFF',   // node:selected border (white both themes)
      edgeMember: '#8E9BB4',        // membership: primary directed signal (~5.8:1)
      edgeContains: '#6B788F',      // containment: subordinate scaffolding (~3.65:1)
      sevError: '#D13438', sevWarning: '#F7A30B', sevInfo: '#4FA3E3',
      sevErrorOpacity: 0.45, sevWarningOpacity: 0.45, sevInfoOpacity: 0.40,
      rollupOpacity: 0.30,
      busy: '#4FA3E3', busyOpacity: 0.35,
      diffAdded: '#2FAE4E', diffRemoved: '#E0503A', diffUnchecked: '#8A8F98',
      diffAddedUnderlayOpacity: 0.5, diffRemovedUnderlayOpacity: 0.5, diffUncheckedUnderlayOpacity: 0.35,
      diffAddedLineOpacity: 0.95, diffRemovedLineOpacity: 0.85, diffUncheckedLineOpacity: 0.5
    },
    // ADR-026 D5 light-canvas hues (all ratios computed vs the light canvas #F5F6F8):
    //   structural (>= 3:1): edge member #5A6473 5.54:1, contains #3A424E 9.39:1, root
    //   border #1C2127 14.98:1, External dashed #6B7480 4.38:1, label ink #1C2127 14.98:1
    //   (>= 4.5:1 text). Kind fills theme-INVARIANT (all >= 4.2:1 on light) so the dark
    //   1.4.11 border-lift is NOT needed on light → nodeLiftWidth 0. Severity HALOS and diff
    //   UNDERLAYS are soft transparent emphasis cues (redundant with the sidebar E/W/i letter
    //   + node shape, like the dark halos which themselves blend < 3:1): deepened hues + raised
    //   opacities so each reads at or above its DARK counterpart's blended ratio. Diff EDGE
    //   lines (near-opaque) clear ~3:1 as structural signals.
    light: {
      canvasBg: '#F5F6F8',
      labelInk: '#1C2127',
      labelOutline: '#F5F6F8',
      nodeLiftRing: '#5A6473',      // unused on light (width 0) but kept defined for parity
      nodeLiftWidth: 0,
      rootBorder: '#1C2127',
      externalBorder: '#6B7480',
      selectionBorder: '#1C2127',   // dark ink reads on the light canvas (white would vanish)
      edgeMember: '#5A6473',
      edgeContains: '#3A424E',
      sevError: '#D63A4A', sevWarning: '#BD7C00', sevInfo: '#2F6FE0',
      sevErrorOpacity: 0.70, sevWarningOpacity: 0.75, sevInfoOpacity: 0.70,
      rollupOpacity: 0.50,
      busy: '#2F6FE0', busyOpacity: 0.55,
      diffAdded: '#1F9D57', diffRemoved: '#D63A4A', diffUnchecked: '#5A6473',
      diffAddedUnderlayOpacity: 0.70, diffRemovedUnderlayOpacity: 0.70, diffUncheckedUnderlayOpacity: 0.50,
      diffAddedLineOpacity: 0.95, diffRemovedLineOpacity: 0.85, diffUncheckedLineOpacity: 0.60
    }
  };

  // index.html chrome custom properties driven from THEME on a theme command (the legend +
  // controls + their severity/diff swatch fills mirror the canvas, ADR-026 D5). Kind swatch
  // fills are theme-invariant and stay hardcoded in the SVG markup. The light values here are
  // hand-derived from Frame 4 (redesign-2026-06) for the chrome surfaces; the severity/diff
  // swatch fills reuse the canvas hues above so the legend stays truthful in light mode.
  var CHROME = {
    dark: {
      '--gw-canvas-bg': '#1b1f27',
      '--gw-chrome-bg': 'rgba(22, 26, 33, 0.92)',
      '--gw-chrome-border': 'rgba(255, 255, 255, 0.06)',
      '--gw-chrome-shadow': '0 4px 16px rgba(0, 0, 0, 0.35)',
      '--gw-chrome-title': '#8A93A3',
      '--gw-chrome-label': '#E8ECF2',
      '--gw-chrome-count': '#C8CFDB',
      '--gw-chrome-count-zero': '#5A6171',
      '--gw-chrome-edge-text': '#A6AEBC',
      '--gw-input-bg': 'rgba(0, 0, 0, 0.25)',
      '--gw-input-border': 'rgba(255, 255, 255, 0.12)',
      '--gw-btn-bg': 'rgba(255, 255, 255, 0.06)',
      '--gw-btn-border': 'rgba(255, 255, 255, 0.12)',
      '--gw-btn-hover-bg': 'rgba(255, 255, 255, 0.12)',
      '--gw-focus-ring': '#4FA3E3',
      '--gw-no-match': '#F7A30B',
      '--gw-edge-member': '#8E9BB4',
      '--gw-edge-contains': '#6B788F',
      '--gw-sev-error': '#D13438', '--gw-sev-warning': '#F7A30B', '--gw-sev-info': '#4FA3E3',
      '--gw-sev-info-ink': '#1b1f27',
      '--gw-diff-added': '#2FAE4E', '--gw-diff-removed': '#E0503A', '--gw-diff-unchecked': '#8A8F98'
    },
    light: {
      '--gw-canvas-bg': '#F5F6F8',
      '--gw-chrome-bg': 'rgba(255, 255, 255, 0.92)',
      '--gw-chrome-border': 'rgba(0, 0, 0, 0.10)',
      '--gw-chrome-shadow': '0 4px 16px rgba(0, 0, 0, 0.14)',
      '--gw-chrome-title': '#5A636E',
      '--gw-chrome-label': '#1C2127',
      '--gw-chrome-count': '#3A424E',
      '--gw-chrome-count-zero': '#A0A6AF',
      '--gw-chrome-edge-text': '#5A636E',
      '--gw-input-bg': 'rgba(0, 0, 0, 0.04)',
      '--gw-input-border': 'rgba(0, 0, 0, 0.18)',
      '--gw-btn-bg': 'rgba(0, 0, 0, 0.05)',
      '--gw-btn-border': 'rgba(0, 0, 0, 0.18)',
      '--gw-btn-hover-bg': 'rgba(0, 0, 0, 0.10)',
      '--gw-focus-ring': '#2F6FE0',
      '--gw-no-match': '#BD7C00',
      '--gw-edge-member': '#5A6473',
      '--gw-edge-contains': '#3A424E',
      '--gw-sev-error': '#D63A4A', '--gw-sev-warning': '#BD7C00', '--gw-sev-info': '#2F6FE0',
      '--gw-sev-info-ink': '#F5F6F8',
      '--gw-diff-added': '#1F9D57', '--gw-diff-removed': '#D63A4A', '--gw-diff-unchecked': '#5A6473'
    }
  };

  // Apply the chrome custom properties for a variant to :root (index.html reads them via
  // var(--gw-*)). Pure DOM write — no cytoscape touch. The legend swatch fills bound to
  // these vars (severity/diff) re-tone with the canvas; kind swatches stay invariant.
  function applyChromeVariant(v) {
    var vars = CHROME[v] || CHROME.dark;
    var root = document.documentElement;
    for (var name in vars) {
      if (Object.prototype.hasOwnProperty.call(vars, name)) {
        root.style.setProperty(name, vars[name]);
      }
    }
  }

  // F7: gate hideEdgesOnViewport on edge count. Below this, a full-redraw pan/zoom
  // stays smooth even under software rendering, so edges stay VISIBLE during
  // gestures (kills the mid-zoom vanish on the ~334-edge demo and typical scopes);
  // above it, keep cytoscape's hide-on-viewport optimization that the
  // 5000-node/6499-edge software-rendering spike relies on
  // (spikes/GraphSpike/RESULTS-software-rendering.md). Re-evaluated per full graph
  // build (ShowGraph/ReloadScope rebuilds cy); a lazy-expand that crosses the
  // threshold keeps the init value until the next full build (acceptable for v0.1).
  var EDGE_HIDE_THRESHOLD = 1500;

  // ADR-023 D4: Labels toggle state ('auto' = ADR-018 gate, 'all' = every label
  // at fit zoom). Module-level so applyLabelMode() can re-assert it in sendLoaded
  // after a graphUpdate adds new nodes. Default 'auto'.
  var labelMode = 'auto';

  // ADR-017 D5: read ONCE at IIFE init. prefers-reduced-motion:reduce degrades
  // BOTH the F2 eased focus-fit and the F1 enter fade to the instant pre-slice
  // paths (synchronous cy.fit + cy.one('render') for focus; full-opacity add for
  // update) - no cy.animate, no opacity tween.
  var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ADR-018 (#89): selection + neighborhood dim. INSTANT class toggles only -
  // never cy.animate / collection.animate (the #88 isolated motion counters must
  // stay 0). applySelection enforces exactly-one-selected: drop any prior
  // selection/dim/hover, select the tapped node EXPLICITLY (synthetic emit('tap')
  // does not run native select), dim everything, then un-dim the node + its 1-hop
  // closed neighborhood (node + neighbors + connecting edges stay bright).
  function applySelection(node) {
    cy.$(':selected').unselect();
    cy.elements().removeClass('gw-dim gw-hover');
    node.select();
    cy.elements().addClass('gw-dim');
    node.closedNeighborhood().removeClass('gw-dim');
  }

  function clearSelection() {
    cy.elements().removeClass('gw-dim gw-hover');
    cy.$(':selected').unselect();
  }

  // ADR-023 D4: apply the current labelMode to every live node. Called from the
  // Labels-toggle handler AND from sendLoaded() so the chosen mode survives a
  // graphUpdate (lazy expand) — new nodes pick up gw-labels-all when mode='all'.
  function applyLabelMode() {
    cy.nodes().toggleClass('gw-labels-all', labelMode === 'all');
  }

  // ADR-023 D3: find a node by Name (data('label')) OR DN (id()), case-insensitive,
  // preferring an exact match, else the FIRST substring match (deterministic by
  // cytoscape z-order). Iterates cy.nodes() comparing values (not selector
  // concatenation), so comma-DNs are safe (ADR-004 D5). Returns a node or null.
  function findNode(query) {
    var q = query.trim().toLowerCase();
    if (q === '') { return null; }
    var nodes = cy.nodes();
    var substr = null;
    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];
      var label = String(node.data('label') || '').toLowerCase();
      var id = String(node.id() || '').toLowerCase();
      if (label === q || id === q) { return node; }
      if (substr === null && (label.indexOf(q) !== -1 || id.indexOf(q) !== -1)) {
        substr = node;
      }
    }
    return substr;
  }

  // Builds cytoscape element descriptors from the accumulated chunks and CLEARS
  // the accumulator: each chunk+commit cycle starts from scratch, never re-feeds
  // the accumulated union (duplicate-id errors). Shared by both commit verbs
  // (graphCommit = full init, graphUpdate = replace-in-place, ADR-005 D1).
  function takePendingElements() {
    var elements = [];
    var i, n, e, data;
    for (i = 0; i < pendingNodes.length; i++) {
      n = pendingNodes[i];
      data = { id: n.id, label: n.label, kind: n.kind };
      if (n.root) { data.root = true; }
      if (n.sev) { data.sev = n.sev; }
      if (n.below) { data.below = n.below; data.belowSev = n.belowSev; }
      if (n.diff) { data.diff = n.diff; }
      elements.push({ group: 'nodes', data: data, position: { x: n.x, y: n.y } });
    }
    for (i = 0; i < pendingEdges.length; i++) {
      e = pendingEdges[i];
      var ed = { id: e.id, source: e.s, target: e.t, rel: e.rel };
      if (e.diff) { ed.diff = e.diff; }
      elements.push({ group: 'edges', data: ed });
    }
    pendingNodes = [];
    pendingEdges = [];
    return elements;
  }

  // #87 encoding-key legend: live per-kind counts. The 7 `.count` element refs are
  // cached on first call (one querySelectorAll keyed by data-kind), then each call
  // does ONE O(V) pass over cy.nodes() grouping by data('kind') and writes the tally
  // into each row (or "0"), toggling the `.zero` class. Pure cy read + textContent
  // write - no cytoscape mutation, no bridge traffic, no severity counts. Called from
  // sendLoaded() so it refreshes on BOTH graphCommit (init) and graphUpdate (lazy
  // expand), and the External bucket self-corrects when a frontier node resolves.
  var legendCountEls = null;
  function updateLegendCounts() {
    if (legendCountEls === null) {
      legendCountEls = {};
      var rows = document.querySelectorAll('#legend [data-kind]');
      for (var r = 0; r < rows.length; r++) {
        var el = rows[r].querySelector('.count');
        if (el) { legendCountEls[rows[r].getAttribute('data-kind')] = el; }
      }
    }
    var tally = {};
    cy.nodes().forEach(function (node) {
      var k = node.data('kind');
      tally[k] = (tally[k] || 0) + 1;
    });
    for (var kind in legendCountEls) {
      var count = tally[kind] || 0;
      var countEl = legendCountEls[kind];
      countEl.textContent = String(count);
      if (count === 0) { countEl.classList.add('zero'); }
      else { countEl.classList.remove('zero'); }
    }
  }

  function sendLoaded() {
    window.bridge.send({
      type: 'loaded',
      nodeCount: cy.nodes().length,
      edgeCount: cy.edges().length
    });
    updateLegendCounts();
    applyLabelMode();  // ADR-023 D4: re-assert label mode across a graphUpdate.
  }

  // ADR-026 WP1b: build the full cytoscape style array from a theme token table `t`
  // (THEME[currentVariant]). The kind FILLS are theme-invariant (hardcoded per-kind
  // rules); everything page-relative (canvas/label/edge/severity/diff/border) reads
  // from `t`. The DARK table reproduces the shipped literals EXACTLY, so the first
  // render (currentVariant='dark') is byte-identical to the pre-WP1b bundle.
  function buildStyle(t) {
    return [
        {
          selector: 'node',
          style: {
            label: 'data(label)',
            'text-valign': 'bottom',
            'text-margin-y': 4,
            'font-size': 10,
            'min-zoomed-font-size': 10,  // labels only appear once zoomed in (ADR-004)
            color: t.labelInk,
            'text-outline-width': 2,
            'text-outline-color': t.labelOutline
          }
        },
        // Kind FILLS are theme-INVARIANT (ADR-026 D5: all >= 4.2:1 on the light canvas too) and
        // stay HARDCODED here, identical in both themes, in lockstep with src/App/Views/BrandTokens.cs
        // (THE source of truth, ADR-021) / AdObjectKindConverters.cs (pinned by
        // WebBundleTests.Graph_PaletteMatchesAdObjectKindConverters). The DL/UG/Computer border-lift
        // (t.nodeLiftRing / t.nodeLiftWidth) is the WCAG 1.4.11 graphical-object-contrast LIFT
        // (#90/ADR-021): those three FILLS measure 2.55/2.66/2.59 vs the DARK #1b1f27 page bg
        // (< the 3:1 floor); the dark ring #8A93A3 (5.33:1) lifts them while the fill HEX stays
        // unchanged. On the LIGHT canvas they already clear 3:1 (5.98/5.75/5.90) so nodeLiftWidth=0
        // (no ring needed). The node[?root] border (t.rootBorder, w3, appended later) still wins on
        // root; the External dashed border (t.externalBorder) is distinct.
        {
          selector: "node[kind='User']",
          style: { shape: 'ellipse', width: 14, height: 14, 'background-color': '#038387' }
        },
        {
          selector: "node[kind='GlobalGroup']",
          style: { shape: 'triangle', width: 22, height: 22, 'background-color': '#107C10' }
        },
        {
          selector: "node[kind='DomainLocalGroup']",
          style: {
            shape: 'diamond', width: 22, height: 22, 'background-color': '#A14000',
            'border-width': t.nodeLiftWidth, 'border-color': t.nodeLiftRing
          }
        },
        {
          selector: "node[kind='UniversalGroup']",
          style: {
            shape: 'pentagon', width: 22, height: 22, 'background-color': '#744DA9',
            'border-width': t.nodeLiftWidth, 'border-color': t.nodeLiftRing
          }
        },
        {
          selector: "node[kind='OrganizationalUnit']",
          style: { shape: 'round-rectangle', width: 22, height: 22, 'background-color': '#0F6CBD' }
        },
        {
          selector: "node[kind='Computer']",
          style: {
            shape: 'rectangle', width: 14, height: 14, 'background-color': '#556070',
            'border-width': t.nodeLiftWidth, 'border-color': t.nodeLiftRing
          }
        },
        {
          selector: "node[kind='External']",
          style: {
            shape: 'ellipse', width: 14, height: 14, 'background-color': '#757575',
            'border-width': 2, 'border-style': 'dashed', 'border-color': t.externalBorder
          }
        },
        {
          // ADR-018 D4 (F9): force the root label on at fit zoom (mzfs 0) so the
          // overview stays orientable; the base node floor stays 10.
          selector: 'node[?root]',
          style: { width: 30, height: 30, 'border-width': 3, 'border-color': t.rootBorder, 'min-zoomed-font-size': 0 }
        },
        // Severity (AP 3.4, ADR-010): owns the overlay-* channel ONLY - the halo
        // paints behind the node, touching neither the kind fill/shape nor the
        // root/External border. Appended AFTER node[?root] so these rules win only
        // on overlay-*. Palette PINNED + parity-tripwired in verify.mjs (SEVERITY).
        // Monotonic padding (7/6/5) is a colorblind-redundant channel. No `sev`
        // field => no rule matches => overlay-opacity default 0 => byte-identical.
        // NO label override anywhere: the kind name stays the only label.
        {
          // ADR-018 D4 (F9): Error nodes stay labeled at fit zoom (mzfs 0) - the
          // AP 3.4 max-severity-always-on mandate; warning/info keep the base floor.
          selector: "node[sev='error']",
          style: { 'overlay-color': t.sevError, 'overlay-opacity': t.sevErrorOpacity, 'overlay-padding': 7, 'min-zoomed-font-size': 0 }
        },
        {
          selector: "node[sev='warning']",
          style: { 'overlay-color': t.sevWarning, 'overlay-opacity': t.sevWarningOpacity, 'overlay-padding': 6 }
        },
        {
          selector: "node[sev='info']",
          style: { 'overlay-color': t.sevInfo, 'overlay-opacity': t.sevInfoOpacity, 'overlay-padding': 5 }
        },
        // Roll-up ring cue: a loaded group hiding flagged descendants gets a wider,
        // fainter max-severity glow keyed to belowSev. NOT a number on canvas
        // (canvas-only cytoscape has no pseudo-elements) - the count is
        // authoritative in the sidebar (AP 3.4 S4/S5).
        {
          selector: 'node[below]',
          style: { 'overlay-padding': 10, 'overlay-opacity': t.rollupOpacity, 'overlay-color': t.sevError }
        },
        {
          selector: "node[below][belowSev='warning']",
          style: { 'overlay-color': t.sevWarning }
        },
        {
          selector: "node[below][belowSev='info']",
          style: { 'overlay-color': t.sevInfo }
        },
        // ADR-019 (#94, F12 split from ADR-017): the in-canvas BUSY ring — a static
        // overlay halo marking a directory round-trip on the node being lazy-expanded.
        // Appended AFTER the severity + roll-up rules and gated [!sev] so a flagged
        // node's own halo always wins the overlay channel (busy never paints over a
        // finding). Set transiently by the 'busy' command; CLEARED automatically by the
        // next graphUpdate (remove-all/add-all drops the data flag). STATIC — no
        // per-frame tween (software-rendering-floor safe; pulsing deferred).
        {
          selector: 'node[busy][!sev]',
          style: { 'overlay-color': t.busy, 'overlay-opacity': t.busyOpacity, 'overlay-padding': 8 }
        },
        // Gap diff (AP 66, ADR-015 Slice 5): owns the cytoscape underlay-* channel
        // on NODES (a layer BENEATH the node, disjoint from kind background-color/
        // shape, root/External border-*, and severity overlay-*) plus a line-*
        // override on EDGES (below). Diff (underlay) and severity (overlay) COEXIST
        // on the same node by construction. Palette PINNED + parity-tripwired in
        // verify.mjs (DIFF). No `diff` field => no rule => underlay-opacity default
        // 0 => byte-identical. Removed nodes ALSO fade their kind fill via
        // background-opacity (the colorblind-redundant BRIGHTNESS channel: added
        // stays full-opacity, removed dims to 0.45 so added != removed without
        // relying on green-vs-red hue); background-opacity fades the fill but leaves
        // background-color untouched, so kind identity survives.
        {
          selector: "node[diff='added']",
          style: { 'underlay-color': t.diffAdded, 'underlay-opacity': t.diffAddedUnderlayOpacity, 'underlay-padding': 8 }
        },
        {
          selector: "node[diff='removed']",
          style: {
            'underlay-color': t.diffRemoved, 'underlay-opacity': t.diffRemovedUnderlayOpacity, 'underlay-padding': 8,
            'background-opacity': 0.45
          }
        },
        {
          selector: "node[diff='unchecked']",
          style: { 'underlay-color': t.diffUnchecked, 'underlay-opacity': t.diffUncheckedUnderlayOpacity, 'underlay-padding': 6 }
        },
        {
          selector: "edge[rel='member']",
          style: {
            'curve-style': 'bezier',     // bezier keeps the seeded A<->B cycle legible (ADR-004 D2)
            width: 1.6,
            'line-color': t.edgeMember,  // the primary directed signal (~5.8:1 dark / ~5.5:1 light)
            opacity: 1,
            'target-arrow-shape': 'triangle',
            'target-arrow-color': t.edgeMember
          }
        },
        {
          // F6: membership vs containment are now separated on FOUR redundant,
          // hue-free channels - lightness (#8E9BB4 vs #6B788F), weight (1.6 vs 1),
          // solid vs dashed, and arrow vs none - so the graph reads as two layers
          // (primary directed membership + subordinate containment scaffolding),
          // not one monochrome cobweb. Both clear the 3:1 non-text-contrast floor
          // on #1b1f27 (membership ~5.8:1, containment ~3.65:1), so the old
          // opacity-based sub-3:1 compromise is gone. Dashed stays the
          // colorblind-redundant channel.
          selector: "edge[rel='contains']",
          style: {
            'curve-style': 'bezier',
            width: 1,
            'line-style': 'dashed',
            'line-color': t.edgeContains,
            opacity: 1
          }
        },
        // Gap diff EDGE override (AP 66, ADR-015 Slice 5): placed AFTER the
        // edge[rel=...] rules so it WINS line-color / line-style on a diffed edge.
        // line-style (solid/dashed/dotted) is the colorblind-redundant channel for
        // edges; an edge with no `diff` field keeps its rel styling unchanged.
        // Palette PINNED + parity-tripwired in verify.mjs (DIFF / DIFF_LINE).
        {
          selector: "edge[diff='added']",
          style: { 'line-color': t.diffAdded, 'target-arrow-color': t.diffAdded, opacity: t.diffAddedLineOpacity }
        },
        {
          selector: "edge[diff='removed']",
          style: {
            'line-color': t.diffRemoved, 'target-arrow-color': t.diffRemoved,
            'line-style': 'dashed', opacity: t.diffRemovedLineOpacity
          }
        },
        {
          selector: "edge[diff='unchecked']",
          style: {
            'line-color': t.diffUnchecked, 'target-arrow-color': t.diffUnchecked,
            'line-style': 'dotted', opacity: t.diffUncheckedLineOpacity
          }
        },
        // Interaction feedback (ADR-018 / #89): APPENDED LAST, in this source order
        // (last wins on a shared channel). All cues ride background-blacken / border
        // / z-index / text-opacity ONLY - NEVER element opacity (the #88 enter-fade,
        // ADR-017), so a dimmed node's severity overlay-* halo and diff underlay-*
        // stay full-strength (background-blacken darkens only the kind fill).
        // - gw-dim darkens the fill (+0.6 = darker) and fades the label.
        // - gw-hover AFTER gw-dim so hovering a dimmed node BRIGHTENS it (negative
        //   blacken = brighter; there is NO background-brighten property).
        // - node:selected LAST so its white border always wins; mzfs 0 forces the
        //   selected node's label on regardless of zoom.
        {
          selector: 'node.gw-dim',
          style: { 'background-blacken': 0.6, 'text-opacity': 0.15 }
        },
        {
          selector: 'node.gw-hover',
          style: { 'background-blacken': -0.15, 'border-opacity': 1 }
        },
        {
          selector: 'node:selected',
          style: { 'border-color': t.selectionBorder, 'border-width': 3, 'border-opacity': 1, 'z-index': 10, 'min-zoomed-font-size': 0 }
        },
        {
          selector: 'edge.gw-dim',
          style: { opacity: 0.12 }
        },
        // ADR-023 D4: Labels "all" mode override of the ADR-018 fit-zoom gate.
        // Toggled on cy.nodes() by applyLabelMode(); drops min-zoomed-font-size to
        // 0 so every label shows at fit zoom. Order-independent for mzfs.
        {
          selector: 'node.gw-labels-all',
          style: { 'min-zoomed-font-size': 0 }
        }
    ];
  }

  function initGraph() {
    if (cy !== null) { cy.destroy(); cy = null; }
    var elements = takePendingElements();

    var edgeCount = 0;
    for (var ei = 0; ei < elements.length; ei++) {
      if (elements[ei].group === 'edges') { edgeCount++; }
    }

    cy = cytoscape({
      container: document.getElementById('cy'),
      elements: elements,
      layout: { name: 'preset' },        // positions precomputed in .NET (ADR-004 D1/D3)
      pixelRatio: 1,
      hideEdgesOnViewport: edgeCount > EDGE_HIDE_THRESHOLD,  // F7
      textureOnViewport: true,
      motionBlur: false,
      style: buildStyle(THEME[currentVariant])
    });

    // Same code path a human click takes: cy tap handler -> bridge -> .NET. The
    // nodeClick send stays the UNCONDITIONAL FIRST statement (the graph->VM
    // SelectedDn contract, keyed off tap); applySelection then paints the
    // selection + neighborhood dim (ADR-018 D3).
    cy.on('tap', 'node', function (evt) {
      window.bridge.send({
        type: 'nodeClick',
        id: evt.target.id(),
        label: evt.target.data('label'),
        kind: evt.target.data('kind')
      });
      applySelection(evt.target);
    });
    // Core-level background tap (evt.target === cy): clear selection + dim/hover.
    cy.on('tap', function (evt) {
      if (evt.target === cy) { clearSelection(); }
    });
    // Hover feedback (ADR-018 D1): instant gw-hover toggle, no transition.
    cy.on('mouseover', 'node', function (evt) { evt.target.addClass('gw-hover'); });
    cy.on('mouseout', 'node', function (evt) { evt.target.removeClass('gw-hover'); });
    cy.on('dbltap', 'node', function (evt) {
      window.bridge.send({ type: 'nodeExpand', id: evt.target.id() });
    });

    cy.fit();
    cy.one('render', sendLoaded);
  }

  // Replace-in-place on the LIVE instance (ADR-005 D1): no destroy, no fit -
  // viewport and the core-bound tap/dbltap handlers survive, so they cover the
  // newly added elements via delegation. One batch removes everything and adds
  // the accumulated set at its preset positions.
  function updateGraph() {
    var elements = takePendingElements();
    // Register BEFORE mutating: the batched mutation schedules the redraw (no
    // fit() forces one on this path), and a listener attached after a render
    // already fired would leave 'loaded' unsent. Same synchronous turn, so no
    // stale pre-update render can slip in between.
    // Capture the pre-removal live node-id set BEFORE the batch so genuinely-new
    // nodes (incoming id not seen here) can be distinguished from survivors.
    var existing = {};
    cy.nodes().forEach(function (n) { existing[n.id()] = true; });
    cy.one('render', sendLoaded);
    cy.batch(function () {
      cy.elements().remove();
      cy.add(elements);
    });
    // ADR-017 F1: fade ONLY genuinely-new nodes in via the element opacity
    // channel (0->1, 240 ms, ease-out-cubic); survivors are replaced instantly
    // (no tween), exactly as before. The collection-prototype animate is on
    // purpose (the F2 camera fit is the core cy.animate - a separate counter);
    // opacity composites the whole node, never touching position/fit.
    if (!reduceMotion) {
      var newNodes = cy.nodes().filter(function (n) { return !existing[n.id()]; });
      if (newNodes.nonempty()) {
        newNodes.style('opacity', 0);
        newNodes.animate({ style: { opacity: 1 } }, { duration: 240, easing: 'ease-out-cubic' });
      }
    }
  }

  // AP 4.1 (ADR-013): rasterize the LIVE instance via cytoscape's built-in
  // cy.png. output:'base64' returns a BARE base64 string (no data: prefix) -
  // the .NET side Convert.FromBase64String's it and writes the bytes to the
  // user-picked .png path. full:false (viewport - "what you see is what you
  // export"), scale:2, bg:'#1b1f27' are the ADR-013 defaults; the outbound
  // command carries no untrusted tokens. cy===null is guarded by the caller.
  function exportPng(cmd) {
    var data = cy.png({
      output: 'base64',
      full: !!cmd.full,
      scale: cmd.scale || 2,
      bg: cmd.bg || '#1b1f27'
    });
    window.bridge.send({ type: 'pngExported', data: data, width: cy.width(), height: cy.height() });
  }

  function focusOn(ids) {
    // ADR-004 D5: cy.getElementById ONLY - selector concatenation silently
    // matches nothing for every comma-containing DN.
    var col = cy.collection();
    for (var i = 0; i < ids.length; i++) {
      col = col.union(cy.getElementById(ids[i]));
    }
    function confirmFocus() { window.bridge.send({ type: 'focused' }); }
    // Empty/un-pannable target (unknown or raw-External single DN): instant fit
    // so 'focused' still posts exactly once. Guarded BEFORE the reduce check
    // (ADR-017 D5). cy.fit schedules the redraw; the listener registered first,
    // same synchronous turn, so no stale pre-fit render can slip in between.
    if (col.empty()) { cy.one('render', confirmFocus); cy.fit(col, 80); return; }
    // ADR-017 D5: reduced motion degrades to the instant pre-slice fit path.
    if (reduceMotion) { cy.one('render', confirmFocus); cy.fit(col, 80); return; }
    // ADR-017 F2: ease the camera to the fit target. cy.stop() coalesces a focus
    // arriving mid-ease so a superseded animation never strands its complete. The
    // 'focused' confirmation comes SOLELY from `complete` (fires even for a
    // zero-distance fit), not cy.one('render') - the render frame precedes settle.
    cy.stop();
    cy.animate(
      { fit: { eles: col, padding: 80 } },
      { duration: 280, easing: 'ease-out-cubic', complete: confirmFocus });
  }

  window.bridge.onCommand(function (cmd) {
    try {
      switch (cmd.type) {
        case 'graphChunk':
          if (cmd.nodes) { Array.prototype.push.apply(pendingNodes, cmd.nodes); }
          if (cmd.edges) { Array.prototype.push.apply(pendingEdges, cmd.edges); }
          break;
        case 'graphCommit':
          initGraph();
          break;
        case 'graphUpdate':
          // ADR-005 D1: graphUpdate before any graphCommit has no live
          // instance to update - report instead of crashing.
          if (cy === null) {
            window.bridge.send({
              type: 'jsError',
              source: 'handler:graphUpdate',
              message: 'graphUpdate before graphCommit: no live cytoscape instance'
            });
            break;
          }
          updateGraph();
          break;
        case 'clickTest':
          // Synthetic tap; same handler path as a human click on the node.
          cy.getElementById(cmd.id).emit('tap');
          break;
        case 'focus':
          focusOn(cmd.ids);
          break;
        case 'busy':
          // ADR-019: toggle the transient `busy` data flag on a live node. cy===null ->
          // silent break (a busy racing ahead of graphCommit is benign and must keep the
          // verify.mjs zero-jsError audit green — unlike graphUpdate/exportPng which DO
          // jsError pre-commit). getElementById ONLY (comma-DN safe, ADR-004 D5). No
          // bridge reply (fire-and-forget); cleared by the next graphUpdate.
          if (cy === null) { break; }
          var busyNode = cy.getElementById(cmd.id);
          if (busyNode.nonempty()) {
            if (cmd.on) {
              busyNode.data('busy', true);
            } else {
              // removeData stops the element matching node[busy][!sev], but cytoscape
              // leaves overlay-opacity frozen at its last computed 0.35 (no other rule
              // re-sets it to its default 0). updateStyle() forces a single-element
              // recompute so the ring reverts to 0 NOW, even on the expand
              // failure/cancel path where no graphUpdate follows the busy-off. Stays a
              // SELECTOR-driven flag (no inline style pin) so a later busy-on repaints
              // via node[busy][!sev], and the flag still self-clears on graphUpdate.
              busyNode.removeData('busy');
              busyNode.updateStyle();
            }
          }
          break;
        case 'select':
          // ADR-020 (#96): drive node:selected + neighborhood dim from OUTSIDE a tap
          // (the sidebar/jump reverse sync). Reuses applySelection/clearSelection so the
          // command-driven select is byte-identical to a tap-driven one. INSTANT
          // (addClass/removeClass only — never cy.animate; the #88 motion counters stay
          // 0). cy===null -> silent break (keeps the zero-jsError audit green). Empty or
          // unknown id -> clearSelection (a null sidebar selection visibly clears the
          // canvas; an unknown DN is surfaced as clear, never a stale highlight). A
          // redundant select right after a tap is harmless (idempotent). getElementById
          // ONLY (comma-DN safe). No bridge reply (fire-and-forget).
          if (cy === null) { break; }
          var selNode = cmd.id ? cy.getElementById(cmd.id) : cy.collection();
          if (selNode.nonempty()) { applySelection(selNode); } else { clearSelection(); }
          break;
        case 'exportPng':
          // ADR-013: cy.png on the live instance. Guard like graphUpdate -
          // an exportPng before any graphCommit has no instance to rasterize.
          if (cy === null) {
            window.bridge.send({
              type: 'jsError',
              source: 'handler:exportPng',
              message: 'exportPng before graphCommit: no live cytoscape instance'
            });
            break;
          }
          exportPng(cmd);
          break;
        case 'theme':
          // ADR-026 WP1b: switch the canvas/chrome theme. Wire carries ONLY the variant
          // string. Unknown/missing variant => dark (the safe default, byte-identical to
          // pre-WP1b). Re-style the LIVE cytoscape instance in place (no destroy, no fit,
          // viewport preserved) if one exists, and set the index.html chrome CSS vars.
          // No bridge reply (fire-and-forget) — the C# side does not await a confirmation.
          currentVariant = (cmd.variant === 'light') ? 'light' : 'dark';
          applyChromeVariant(currentVariant);
          if (cy !== null) { cy.style(buildStyle(THEME[currentVariant])); }
          break;
        case 'ping':
          window.bridge.send({ type: 'pong', seq: cmd.seq });
          break;
        default:
          window.bridge.send({ type: 'jsError', source: 'bridge', message: 'unknown command: ' + cmd.type });
      }
    } catch (err) {
      // file:// = opaque origin mutes window.onerror details (ADR-004 D6) -
      // every handler failure is reported through the bridge instead.
      window.bridge.send({
        type: 'jsError',
        source: 'handler:' + cmd.type,
        message: String(err && err.stack || err)
      });
    }
  });

  // ADR-023: in-graph control cluster wiring. Attached ONCE at IIFE init (DOM is
  // ready — this script runs at end of body). Every handler guards cy === null so
  // a control press before graphCommit is a silent no-op (keeps the zero-jsError
  // audit green). None of these touch the .NET `focused` confirmation channel.
  function controlFit() {
    if (cy === null) { return; }
    cy.fit(cy.elements(), 80);  // same 80px padding as focusOn.
  }

  // Anchored at viewport center, clamped to cytoscape's configured min/max zoom.
  function controlZoom(factor) {
    if (cy === null) { return; }
    var next = cy.zoom() * factor;
    next = Math.max(cy.minZoom(), Math.min(cy.maxZoom(), next));
    cy.zoom({ level: next, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
  }

  // ADR-023 D3: find mirrors a tap (nodeClick + applySelection) then frames the
  // node LOCALLY — it deliberately does NOT reuse focusOn() and NEVER emits a
  // `focused` message (that confirmation is the .NET focus protocol's; an
  // unsolicited one would perturb the JumpAsync/FocusCalls pin, ADR-017/020).
  function controlFind(noMatchEl) {
    if (cy === null) { return; }
    var input = document.getElementById('find-input');
    var node = findNode(input ? input.value : '');
    if (node === null) {
      if (noMatchEl) { noMatchEl.hidden = false; }
      return;  // no bridge traffic on a no-match.
    }
    if (noMatchEl) { noMatchEl.hidden = true; }
    // (a) the SAME message a tap sends — updates .NET SelectedDn + detail panel.
    window.bridge.send({
      type: 'nodeClick',
      id: node.id(),
      label: node.data('label'),
      kind: node.data('kind')
    });
    // (b) instant selection + neighborhood dim (ADR-018).
    applySelection(node);
    // (c) a LOCAL eased frame; reduced motion degrades to an instant fit.
    if (reduceMotion) {
      cy.fit(node, 80);
    } else {
      cy.animate({ fit: { eles: node, padding: 80 } }, { duration: 280, easing: 'ease-out-cubic' });
    }
  }

  function controlToggleLabels() {
    if (cy === null) { return; }
    labelMode = (labelMode === 'all') ? 'auto' : 'all';
    applyLabelMode();
    var btn = document.getElementById('labels-btn');
    if (btn) {
      btn.textContent = 'Labels: ' + labelMode;
      btn.setAttribute('aria-pressed', labelMode === 'all' ? 'true' : 'false');
    }
  }

  function wireControls() {
    var findInput = document.getElementById('find-input');
    var noMatchEl = document.getElementById('find-no-match');
    var fitBtn = document.getElementById('fit-btn');
    var zoomInBtn = document.getElementById('zoom-in-btn');
    var zoomOutBtn = document.getElementById('zoom-out-btn');
    var labelsBtn = document.getElementById('labels-btn');

    if (fitBtn) { fitBtn.addEventListener('click', controlFit); }
    if (zoomInBtn) { zoomInBtn.addEventListener('click', function () { controlZoom(1.2); }); }
    if (zoomOutBtn) { zoomOutBtn.addEventListener('click', function () { controlZoom(1 / 1.2); }); }
    if (labelsBtn) { labelsBtn.addEventListener('click', controlToggleLabels); }
    if (findInput) {
      findInput.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') {
          e.preventDefault();
          controlFind(noMatchEl);
        } else if (e.key === 'Escape') {
          findInput.value = '';
          if (noMatchEl) { noMatchEl.hidden = true; }
          findInput.blur();
        }
      });
    }

    // ADR-023 D5: web-layer keyboard, scoped to the bundle (the native ADR-022
    // shortcuts fire when Avalonia chrome has focus). Only act when cy exists;
    // never swallow keys we do not handle.
    document.addEventListener('keydown', function (e) {
      var key = e.key;
      if ((e.ctrlKey || e.metaKey) && (key === 'f' || key === 'F')) {
        if (findInput) { e.preventDefault(); findInput.focus(); }
        return;
      }
      // Esc handling for the find input lives on the input listener above.
      if (e.ctrlKey || e.metaKey || e.altKey) {
        if ((e.ctrlKey || e.metaKey) && key === '0') {
          if (cy !== null) { e.preventDefault(); controlFit(); }
        }
        return;
      }
      // Plain keys: don't fight typing in the find box (or any text field).
      if (document.activeElement === findInput) { return; }
      if (key === '+' || key === '=') {
        if (cy !== null) { e.preventDefault(); controlZoom(1.2); }
      } else if (key === '-') {
        if (cy !== null) { e.preventDefault(); controlZoom(1 / 1.2); }
      }
    });
  }

  wireControls();

  // ADR-026 WP1b: index.html ships DARK chrome var defaults on :root (so the bundle is
  // byte-identical pre-theme-command), but assert them from the JS table too so the
  // single source of truth is CHROME[currentVariant]. Default currentVariant = 'dark'.
  applyChromeVariant(currentVariant);

  window.bridge.send({ type: 'ready', userAgent: navigator.userAgent });
})();
