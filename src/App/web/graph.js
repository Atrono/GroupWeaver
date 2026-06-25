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
      diffAddedLineOpacity: 0.95, diffRemovedLineOpacity: 0.85, diffUncheckedLineOpacity: 0.5,
      // ADR-027 D4 (WP3): the selection accent halo/pulse ring hue — brand purple, mirrored from
      // BrandTokens.GraphAccentHex (= the chrome decorative AccentHex). NOT a cytoscape style
      // value (all per-node channels are taken); it reaches the #gw-accent-ring DOM element via
      // the --gw-accent CSS var (CHROME below). Decorative non-text glow, reads on the dark canvas.
      accent: '#8B7BFF'
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
      diffAddedLineOpacity: 0.95, diffRemovedLineOpacity: 0.85, diffUncheckedLineOpacity: 0.60,
      // ADR-027 D4: light-canvas selection accent — brand purple #6A5CFF (mirror of
      // BrandTokens.GraphAccentLightHex / chrome AccentLightHex); opacity (index.html) chosen to
      // read on the light canvas #F5F6F8.
      accent: '#6A5CFF'
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
      '--gw-canvas-grid': 'rgba(255, 255, 255, 0.04)',  // #168 decorative dot-grid tint (index.html #cy mirror)
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
      '--gw-diff-added': '#2FAE4E', '--gw-diff-removed': '#E0503A', '--gw-diff-unchecked': '#8A8F98',
      // ADR-027 D4: selection accent ring hue (= THEME.dark.accent, BrandTokens.GraphAccentHex).
      '--gw-accent': '#8B7BFF'
    },
    light: {
      '--gw-canvas-bg': '#F5F6F8',
      '--gw-canvas-grid': 'rgba(0, 0, 0, 0.045)',  // #168 decorative dot-grid tint (light: faint dark dot, mock frame 4)
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
      '--gw-diff-added': '#1F9D57', '--gw-diff-removed': '#D63A4A', '--gw-diff-unchecked': '#5A6473',
      // ADR-027 D4: selection accent ring hue (= THEME.light.accent, BrandTokens.GraphAccentLightHex).
      '--gw-accent': '#6A5CFF'
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

  // WP-A (#176, ADR-029): zoom-driven overview edge-fade. At/near the fit zoom an
  // individual edge is untraceable, so the full-opacity mesh is pure visual noise (the
  // "hairball"); at overview zoom every plain (non-diff) edge fades to a faint wash so
  // the shaped-node constellation reads, then returns to full as the user zooms in to
  // inspect. fitZoom is the size-independent overview baseline captured once per full
  // build (cy.fit()); a binary class toggle with hysteresis keeps the software-
  // rendering floor safe — the cy.batch toggle runs ONLY on a threshold crossing,
  // never per frame. The gap/diff view is EXCLUDED wholesale (isDiffGraph): its
  // Added/Removed/Unchecked edges are the signal, never noise. Instant (the
  // gw-edge-faded rule carries no transition) so the #88 motion counters stay 0.
  var EDGE_FADE_FACTOR = 1.6;   // fade while cy.zoom() <= fitZoom * this (tunable)
  var fitZoom = null;           // zoom right after cy.fit() (overview baseline), per build
  var edgesFaded = false;       // current applied fade state (the hysteresis guard)
  var isDiffGraph = false;      // a gap/diff build (>=1 diff-tagged edge) => never fade

  // ADR-023 D4: Labels toggle state ('auto' = ADR-018 gate, 'all' = every label
  // at fit zoom). Module-level so applyLabelMode() can re-assert it in sendLoaded
  // after a graphUpdate adds new nodes. Default 'auto'.
  var labelMode = 'auto';

  // WP3b (#142): "Issues only" filter state. When ON, every node WITHOUT a finding
  // (data('sev')) AND without flagged loaded descendants (data('below')) is hidden
  // (cytoscape .hide() => display:none); the rest stay shown. A flagged node's loaded
  // ancestor groups carry `below` (ADR-010 D4 roll-up), so the path to a finding stays
  // visible and the directory collapses to a navigable issue tree. Module-level (mirror
  // of labelMode) so applyIssuesFilter() can re-assert it in sendLoaded after a
  // graphUpdate adds new nodes. Default OFF (false). Canvas-local (ADR-023): the
  // sev/below fields already cross the wire — no bridge command, no C# change.
  var issuesOnly = false;

  // ADR-017 D5: read ONCE at IIFE init. prefers-reduced-motion:reduce degrades
  // BOTH the F2 eased focus-fit and the F1 enter fade to the instant pre-slice
  // paths (synchronous cy.fit + cy.one('render') for focus; full-opacity add for
  // update) - no cy.animate, no opacity tween.
  var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ADR-027 D3 (WP3): the selection accent halo/pulse. A SINGLE DOM-overlay ring
  // element (#gw-accent-ring in index.html — the same overlay family as #legend/
  // #controls, pointer-events:none), positioned at the selected node's
  // renderedPosition and sized to its rendered diameter + a glow margin. Every per-
  // node cytoscape channel is taken (kind/severity/diff/border/blacken), so the
  // accent glow lives OUTSIDE cytoscape's style system — additive over the white
  // node:selected border, which is kept. EXACTLY ONE element (software-rendering-floor
  // safe). The pulse is a CSS @keyframes class (gw-accent-pulse), gated on
  // reduceMotion (ADR-017): reduced-motion => static ring, no animation class.
  // Tracked across cy render/pan/zoom/position so it follows the node during
  // gestures and graphUpdate re-layouts; the tracked id is cleared when the node
  // vanishes (lazy expand) so a stale ring never floats over empty canvas.
  var accentRingEl = null;          // cached #gw-accent-ring (lazily, once)
  var accentSelectedId = null;      // DN of the node the ring currently tracks, or null
  // Extra px the glow extends beyond the node's rendered radius (margin for the
  // soft outer glow). The ring's own box-shadow/border render the visible glow.
  var ACCENT_RING_MARGIN = 10;

  function getAccentRingEl() {
    if (accentRingEl === null) {
      accentRingEl = document.getElementById('gw-accent-ring');
    }
    return accentRingEl;
  }

  // Reposition the ring over the tracked node. Hides it if cy is gone, nothing is
  // tracked, or the tracked node has vanished (graphUpdate dropped it). Pure DOM
  // writes + cy reads — never a cytoscape mutation, never cy.animate.
  function updateAccentRing() {
    var el = getAccentRingEl();
    if (el === null) { return; }
    if (cy === null || accentSelectedId === null) { hideAccentRing(); return; }
    var node = cy.getElementById(accentSelectedId);
    if (node.empty()) { hideAccentRing(); return; }  // node vanished on lazy expand.
    var pos = node.renderedPosition();
    // rendered* account for the live zoom; use the larger dimension so the ring
    // encloses non-square shapes (triangle/diamond/pentagon).
    var diameter = Math.max(node.renderedWidth(), node.renderedHeight()) + ACCENT_RING_MARGIN * 2;
    el.style.width = diameter + 'px';
    el.style.height = diameter + 'px';
    el.style.left = (pos.x - diameter / 2) + 'px';
    el.style.top = (pos.y - diameter / 2) + 'px';
  }

  // Show the accent ring tracking `node`. The pulse class is added only when motion
  // is allowed (ADR-017 D5); reduced motion => a static ring (visible, no animation).
  function showAccentRing(node) {
    var el = getAccentRingEl();
    if (el === null) { return; }
    accentSelectedId = node.id();
    el.classList.toggle('gw-accent-pulse', !reduceMotion);
    el.hidden = false;
    updateAccentRing();
  }

  function hideAccentRing() {
    var el = getAccentRingEl();
    if (el === null) { return; }
    accentSelectedId = null;
    el.hidden = true;
    el.classList.remove('gw-accent-pulse');
  }

  // WP3d (#146): the graph minimap. A downscaled thumbnail of the WHOLE graph (a single
  // cy.png snapshot set as the #minimap background-image, refreshed ONLY on graph/theme
  // change — NEVER per frame) plus a live viewport rectangle (#minimap-viewport) and
  // click/drag-to-pan. The viewport rect is the ONLY per-frame work and it is a single
  // DOM write (like the accent ring), so the software-rendering floor is safe.
  //
  // The thumbnail is a PNG, not a second live cytoscape instance: cy.png({full:true})
  // rasterizes the whole graph once. To stay smooth on the 5000-node/6499-edge
  // software-rendering spike (RESULTS-software-rendering.md), the snapshot SCALE is gated
  // on node count like the F7 EDGE_HIDE_THRESHOLD: above MINIMAP_COARSE_THRESHOLD nodes
  // a coarser scale keeps the one-off rasterize cheap (a thumbnail never needs node
  // detail). The cy.png "full" extent maps 1:1 to cy.elements().boundingBox(), so the
  // SAME bbox maps the live cy.extent() viewport into thumbnail pixels for the rect, and
  // a minimap click maps back to a model point for cy.pan()/center().
  var MINIMAP_W = 200;              // must match #minimap width in index.html
  var MINIMAP_H = 140;              // must match #minimap height in index.html
  // Snapshot scale: small graphs get a crisp thumbnail; large graphs a coarse one so the
  // one-off cy.png stays cheap under software rendering (the thumbnail is downscaled to
  // ~200x140 anyway, so node-level crispness past this size buys nothing).
  var MINIMAP_COARSE_THRESHOLD = 1500;   // node count; aligned with the F7 edge gate magnitude
  var MINIMAP_SCALE_FINE = 0.5;
  var MINIMAP_SCALE_COARSE = 0.15;
  var minimapEl = null;            // cached #minimap (lazily, once)
  var minimapViewportEl = null;    // cached #minimap-viewport (lazily, once)
  // The full-graph model bounding box captured at the LAST thumbnail render, and the
  // thumbnail's pixel size — both fixed until the next re-png. The viewport rect + the
  // click-to-pan inverse map are computed against these (NOT a live boundingBox() per
  // frame), so a pan/zoom never recomputes the O(V) bbox.
  var minimapBbox = null;          // { x1, y1, w, h } in model coords, or null when hidden
  var minimapImgW = 0;             // rendered thumbnail width inside #minimap (px)
  var minimapImgH = 0;             // rendered thumbnail height inside #minimap (px)
  var minimapOffX = 0;             // left offset of the centered thumbnail inside #minimap (px)
  var minimapOffY = 0;             // top offset of the centered thumbnail inside #minimap (px)

  function getMinimapEl() {
    if (minimapEl === null) { minimapEl = document.getElementById('minimap'); }
    return minimapEl;
  }
  function getMinimapViewportEl() {
    if (minimapViewportEl === null) { minimapViewportEl = document.getElementById('minimap-viewport'); }
    return minimapViewportEl;
  }

  function hideMinimap() {
    var el = getMinimapEl();
    if (el === null) { return; }
    el.hidden = true;
    el.style.backgroundImage = 'none';
    minimapBbox = null;
  }

  // (Re)rasterize the whole graph into the minimap background. Called ONLY on a graph
  // change (sendLoaded) and on a theme change (so the thumbnail matches the recolored
  // canvas) — never per frame. Empty graph => hide (never a broken/empty img). Computes
  // the centered "contain" placement of the thumbnail inside #minimap and caches the
  // full-graph model bbox so the per-frame viewport rect + click-to-pan can map without
  // recomputing boundingBox().
  function refreshMinimap() {
    var el = getMinimapEl();
    if (el === null) { return; }
    if (cy === null || cy.nodes().empty()) { hideMinimap(); return; }
    var bb = cy.elements().boundingBox();
    if (!(bb.w > 0) || !(bb.h > 0)) { hideMinimap(); return; }   // degenerate (single point)
    var scale = cy.nodes().length > MINIMAP_COARSE_THRESHOLD ? MINIMAP_SCALE_COARSE : MINIMAP_SCALE_FINE;
    var bg = (CHROME[currentVariant] || CHROME.dark)['--gw-canvas-bg'];
    var png = cy.png({ output: 'base64uri', full: true, scale: scale, bg: bg });
    el.style.backgroundImage = 'url("' + png + '")';
    minimapBbox = { x1: bb.x1, y1: bb.y1, w: bb.w, h: bb.h };
    // "contain" placement: the thumbnail fills #minimap preserving the graph aspect ratio.
    var fit = Math.min(MINIMAP_W / bb.w, MINIMAP_H / bb.h);
    minimapImgW = bb.w * fit;
    minimapImgH = bb.h * fit;
    minimapOffX = (MINIMAP_W - minimapImgW) / 2;
    minimapOffY = (MINIMAP_H - minimapImgH) / 2;
    el.hidden = false;
    updateMinimapViewport();
  }

  // Reposition the viewport rectangle from the live cy.extent() (the viewport's model
  // rect) into thumbnail pixels, clamped to the thumbnail box. The ONLY per-frame work:
  // pure cy.extent() read + a few style writes on ONE div (no boundingBox, no png).
  function updateMinimapViewport() {
    var rect = getMinimapViewportEl();
    if (rect === null || minimapBbox === null || cy === null) { return; }
    var ext = cy.extent();   // { x1, y1, x2, y2, w, h } in model coords
    var bb = minimapBbox;
    var sx = minimapImgW / bb.w;
    var sy = minimapImgH / bb.h;
    var left = minimapOffX + (ext.x1 - bb.x1) * sx;
    var top = minimapOffY + (ext.y1 - bb.y1) * sy;
    var w = (ext.x2 - ext.x1) * sx;
    var h = (ext.y2 - ext.y1) * sy;
    // Clamp to the thumbnail so a zoomed-out viewport (larger than the graph) does not
    // overflow the minimap box.
    var x0 = Math.max(minimapOffX, left);
    var y0 = Math.max(minimapOffY, top);
    var x1 = Math.min(minimapOffX + minimapImgW, left + w);
    var y1 = Math.min(minimapOffY + minimapImgH, top + h);
    rect.style.left = x0 + 'px';
    rect.style.top = y0 + 'px';
    rect.style.width = Math.max(0, x1 - x0) + 'px';
    rect.style.height = Math.max(0, y1 - y0) + 'px';
  }

  // Map a minimap-local pixel point (relative to #minimap's top-left) to a model
  // coordinate and CENTER the main view there. Inverse of the viewport-rect mapping.
  // Clicks outside the thumbnail area (the centered letterbox margins) are ignored.
  // Instant (no cy.animate): a click/drag needs to track the pointer every move, so an
  // eased camera would fight the drag — a minimap pan is coarse navigation, not a focus
  // frame (the ADR-017 easing belongs to Find/focus, not here). zoom is left untouched.
  function minimapPanTo(px, py) {
    if (cy === null || minimapBbox === null) { return; }
    var bb = minimapBbox;
    var ix = px - minimapOffX;
    var iy = py - minimapOffY;
    if (ix < 0 || iy < 0 || ix > minimapImgW || iy > minimapImgH) { return; }
    var mx = bb.x1 + (ix / minimapImgW) * bb.w;
    var my = bb.y1 + (iy / minimapImgH) * bb.h;
    cy.center({ position: { x: mx, y: my } });
  }

  // Bind the minimap pointer interactions ONCE (the element is static; cy is what gets
  // recreated). Click + drag pan; pointer capture keeps the drag alive past the box edge.
  function wireMinimap() {
    var el = getMinimapEl();
    if (el === null) { return; }
    var dragging = false;
    function localPoint(e) {
      var r = el.getBoundingClientRect();
      return { x: e.clientX - r.left, y: e.clientY - r.top };
    }
    function pan(e) {
      var p = localPoint(e);
      minimapPanTo(p.x, p.y);
    }
    el.addEventListener('mousedown', function (e) {
      if (cy === null || minimapBbox === null) { return; }
      e.preventDefault();
      dragging = true;
      pan(e);
    });
    document.addEventListener('mousemove', function (e) {
      if (!dragging) { return; }
      pan(e);
    });
    document.addEventListener('mouseup', function () { dragging = false; });
  }

  // ADR-018 (#89): selection + neighborhood dim. INSTANT class toggles only -
  // never cy.animate / collection.animate (the #88 isolated motion counters must
  // stay 0). applySelection enforces exactly-one-selected: drop any prior
  // selection/dim/hover, select the tapped node EXPLICITLY (synthetic emit('tap')
  // does not run native select), dim everything, then un-dim the node + its 1-hop
  // closed neighborhood (node + neighbors + connecting edges stay bright). ADR-027
  // D3: ALSO shows the accent ring over the selected node (additive over the
  // node:selected white border; the DOM ring's CSS pulse never touches cy.animate).
  function applySelection(node) {
    cy.$(':selected').unselect();
    cy.elements().removeClass('gw-dim gw-hover');
    node.select();
    cy.elements().addClass('gw-dim');
    node.closedNeighborhood().removeClass('gw-dim');
    showAccentRing(node);
  }

  function clearSelection() {
    cy.elements().removeClass('gw-dim gw-hover');
    cy.$(':selected').unselect();
    hideAccentRing();  // ADR-027 D3: drop the accent ring on any clear.
  }

  // ADR-023 D4: apply the current labelMode to every live node. Called from the
  // Labels-toggle handler AND from sendLoaded() so the chosen mode survives a
  // graphUpdate (lazy expand) — new nodes pick up gw-labels-all when mode='all'.
  function applyLabelMode() {
    cy.nodes().toggleClass('gw-labels-all', labelMode === 'all');
  }

  // WP-A (#176, ADR-029): apply the overview edge-fade for the CURRENT zoom. Driven by
  // the 'zoom' cy event (via onZoomFade, force=false) and re-asserted from initGraph /
  // sendLoaded (force=true) so a freshly built graph and any graphUpdate-added edges
  // pick up the right state. Hysteresis: force=false short-circuits unless the fade
  // threshold was just crossed, so the per-frame 'zoom' stream costs one read + one
  // compare; the cy.batch class toggle runs ONLY on a crossing (software-floor safe).
  // No-op until fitZoom is captured, and OFF entirely on a diff graph (isDiffGraph) so
  // the gap view's coloured edges always stay full-opacity. Explore graphs carry no
  // diff edge, so cy.edges() == the [!diff] set.
  function updateEdgeFade(force) {
    if (cy === null || fitZoom === null || isDiffGraph) { return; }
    var shouldFade = cy.zoom() <= fitZoom * EDGE_FADE_FACTOR;
    if (!force && shouldFade === edgesFaded) { return; }
    edgesFaded = shouldFade;
    cy.batch(function () {
      if (shouldFade) { cy.edges().addClass('gw-edge-faded'); }
      else { cy.edges().removeClass('gw-edge-faded'); }
    });
  }

  // The 'zoom' cy event passes an event object as the first arg; wrap so it never
  // reaches updateEdgeFade's `force` param (a truthy event would defeat the hysteresis).
  function onZoomFade() { updateEdgeFade(false); }

  // WP3b (#142): does a node carry a finding or a flagged-descendant roll-up?
  // The predicate for "keep visible under issues-only". data('below') is emitted
  // only when > 0 (ADR-010 D2), so its mere presence means flagged descendants.
  function nodeHasIssue(node) {
    return !!node.data('sev') || !!node.data('below');
  }

  // WP3b (#142): are there ANY flagged nodes to filter to? Guards the all-clear
  // case — toggling issues-only with zero findings would hide the whole graph
  // (blank canvas), so the toggle is inert when nothing is flagged.
  function anyIssues() {
    if (cy === null) { return false; }
    return cy.nodes().filter(function (n) { return nodeHasIssue(n); }).nonempty();
  }

  // WP3b (#142): re-assert the current issues-only filter across every live node.
  // Called from the toggle handler AND sendLoaded() so the mode survives a
  // graphUpdate (lazy expand) — newly added clean nodes get hidden when the filter
  // is ON, exactly as labels-all re-applies. OFF => show all. ON => hide every node
  // WITHOUT sev||below (display:none); connected edges follow cytoscape's
  // hidden-endpoint behavior, so no dangling edge visual remains. The all-clear
  // guard lives in the toggle handler (the flag never turns ON with zero issues),
  // so here ON always implies >=1 flagged node — never a blank canvas.
  function applyIssuesFilter() {
    if (!issuesOnly) {
      cy.nodes().show();
      return;
    }
    cy.nodes().forEach(function (n) {
      if (nodeHasIssue(n)) { n.show(); } else { n.hide(); }
    });
  }

  // WP3b (#142): if the issues-only filter is ON and `node` is currently hidden by
  // it (a clean node reached via Find or a reverse `select`), clear the filter so
  // the target becomes visible — the least-surprising behavior (documented in the
  // button title): a jump always lands on a visible node. Returns nothing; callers
  // run this BEFORE applySelection/the camera frame. Cheap: cytoscape .visible() is
  // O(1) per element. No-op when the filter is off or the node is already visible.
  function revealIfHiddenByFilter(node) {
    if (issuesOnly && node.nonempty() && !node.visible()) {
      issuesOnly = false;
      applyIssuesFilter();
      syncIssuesButton();
    }
  }

  // WP3b (#142): reflect issuesOnly + the all-clear state on the toggle button.
  // Mirrors the labels-btn pattern (textContent + aria-pressed). When no node is
  // flagged the button reads "No issues" and stays aria-pressed=false (inert).
  function syncIssuesButton() {
    var btn = document.getElementById('issues-btn');
    if (!btn) { return; }
    if (cy !== null && !anyIssues()) {
      btn.textContent = 'No issues';
      btn.setAttribute('aria-pressed', 'false');
      return;
    }
    btn.textContent = issuesOnly ? 'Issues: on' : 'Issues only';
    btn.setAttribute('aria-pressed', issuesOnly ? 'true' : 'false');
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

  // WP3c (#144): the command-palette node matcher — REUSES findNode's exact match
  // rule (label OR id, case-insensitive, comma-DN-safe value compare, ADR-004 D5)
  // but returns up to `limit` matches (exact hits first, then substring hits in
  // cytoscape z-order). Empty/blank query => [] (the palette then shows actions
  // only). Pure cy read; never a mutation.
  function findNodes(query, limit) {
    if (cy === null) { return []; }
    var q = query.trim().toLowerCase();
    if (q === '') { return []; }
    var nodes = cy.nodes();
    var exact = [];
    var substr = [];
    // The `limit * 4` cap is a PERF bound (10K-node target), not a correctness one:
    // element 0 of the result — the auto-highlighted item, identical to findNode's
    // single result — is always reached (the first exact, else the first substring,
    // is collected long before the cap). Only the tail ORDER of the top-N could vary.
    for (var i = 0; i < nodes.length && (exact.length + substr.length) < limit * 4; i++) {
      var node = nodes[i];
      var label = String(node.data('label') || '').toLowerCase();
      var id = String(node.id() || '').toLowerCase();
      if (label === q || id === q) { exact.push(node); }
      else if (label.indexOf(q) !== -1 || id.indexOf(q) !== -1) { substr.push(node); }
    }
    return exact.concat(substr).slice(0, limit);
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
    applyIssuesFilter();  // WP3b (#142): re-assert issues-only across a graphUpdate.
    updateEdgeFade(true); // WP-A (#176): re-assert overview fade onto graphUpdate edges.
    syncIssuesButton();   // WP3b: refresh the toggle (incl. all-clear) per build.
    refreshMinimap();     // WP3d (#146): (re)rasterize the thumbnail on any graph change.
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
        // WP-A (#176, ADR-029): overview edge-fade. Toggled on every edge by
        // updateEdgeFade() when an explore graph is zoomed out near its fit baseline.
        // Placed BEFORE the edge[diff=...] rules and the edge.gw-dim rule so BOTH still
        // win the opacity channel below it: a diff-tagged edge keeps its diff opacity
        // (and isDiffGraph turns fade off wholesale anyway), and a selection-dimmed edge
        // keeps 0.12. The fade therefore governs only an undiffed, undimmed edge at
        // overview zoom. No transition => instant => the #88 motion counters stay 0.
        {
          selector: 'edge.gw-edge-faded',
          style: { opacity: 0.15 }
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
    hideAccentRing();  // ADR-027 D3: a full re-init starts with no selection ring.
    hideMinimap();     // WP3d (#146): clear the old thumbnail before the new graph renders.
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
    // ADR-027 D3: keep the accent ring glued to the tracked node during gestures /
    // re-layouts. render covers pan/zoom and the graphUpdate redraw (if the tracked
    // node vanished, updateAccentRing hides the ring). position covers a node move.
    // These attach ONCE per cy instance (initGraph destroys + recreates cy).
    cy.on('render pan zoom', updateAccentRing);
    cy.on('position', 'node', updateAccentRing);
    // WP3d (#146): keep the minimap viewport rect synced to the live camera. The ONLY
    // per-frame minimap work — a single div repositioned from cy.extent() (no png, no
    // boundingBox). Attaches ONCE per cy instance (initGraph recreates cy).
    cy.on('render pan zoom', updateMinimapViewport);
    // WP-A (#176, ADR-029): track the overview edge-fade as the camera zooms.
    cy.on('zoom', onZoomFade);

    cy.fit();
    // ADR-029: capture the overview baseline, decide if this is a diff graph (once per
    // build), and apply the fade BEFORE the first paint so the explore hero never
    // flashes the full-opacity hairball. sendLoaded re-asserts it after a graphUpdate.
    fitZoom = cy.zoom();
    isDiffGraph = cy.edges('[?diff]').nonempty();
    updateEdgeFade(true);
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
          if (selNode.nonempty()) {
            // WP3b (#142): a sidebar/jump reverse-select may target a clean node
            // hidden by issues-only — clear the filter first so the selection ring
            // lands on a visible node (same least-surprising rule as Find).
            revealIfHiddenByFilter(selNode);
            applySelection(selNode);
          } else { clearSelection(); }
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
          if (cy !== null) {
            cy.style(buildStyle(THEME[currentVariant]));
            // WP3d (#146): re-png the minimap thumbnail so it matches the recolored
            // canvas (new node hues + the variant's --gw-canvas-bg). Re-png ONLY here
            // and on graph change — not per frame. No-op (hide) on an empty graph.
            refreshMinimap();
          }
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

  // ADR-023 D3: select + frame a resolved node LOCALLY (the shared node-jump path,
  // reused by controlFind AND the WP3c command palette). Mirrors a tap (nodeClick +
  // applySelection) then frames the node — it deliberately does NOT reuse focusOn()
  // and NEVER emits a `focused` message (that confirmation is the .NET focus
  // protocol's; an unsolicited one would perturb the JumpAsync/FocusCalls pin,
  // ADR-017/020). Caller guarantees node !== null and cy !== null.
  function selectAndFrame(node) {
    // WP3b (#142): a jump can resolve to a clean node hidden by issues-only —
    // clear the filter so the target is visible BEFORE selecting/framing it
    // (least-surprising: a find always lands on a visible node).
    revealIfHiddenByFilter(node);
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

  // ADR-023 D3: find-on-submit fallback (still the Enter behavior when the palette
  // has nothing highlighted, e.g. a no-match). Resolves the best match via findNode
  // and either jumps to it or surfaces the no-match affordance (no bridge traffic on
  // a no-match — ADR-023 D3).
  function controlFind(noMatchEl) {
    if (cy === null) { return; }
    var input = document.getElementById('find-input');
    var node = findNode(input ? input.value : '');
    if (node === null) {
      if (noMatchEl) { noMatchEl.hidden = false; }
      return;  // no bridge traffic on a no-match.
    }
    if (noMatchEl) { noMatchEl.hidden = true; }
    selectAndFrame(node);
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

  // WP3b (#142): toggle the issues-only filter. All-clear guard: if no node is
  // flagged, turning ON would hide everything, so the toggle is a no-op and the
  // button reflects "No issues" (inert). Otherwise flip issuesOnly, re-filter, and
  // sync the button (mirror of controlToggleLabels).
  function controlToggleIssues() {
    if (cy === null) { return; }
    if (!issuesOnly && !anyIssues()) {
      syncIssuesButton();  // surface "No issues"; stay OFF (no blank canvas).
      return;
    }
    issuesOnly = !issuesOnly;
    applyIssuesFilter();
    syncIssuesButton();
  }

  // WP3c (#144): the Ctrl+K command palette. A small module over the existing
  // #find-input + a new #palette-results dropdown. As the user types, the dropdown
  // lists node matches (via findNodes — the EXISTING matcher) then quick actions
  // whose names match the query; ↑/↓ move a highlighted index, Enter invokes it
  // (node => selectAndFrame, the existing find/select path; action => its handler),
  // Esc closes+clears, click invokes, click-outside closes. Canvas-local (ADR-023):
  // no bridge command, no C# change — node selection still fires exactly one
  // nodeClick (via selectAndFrame); actions fire none.
  var PALETTE_NODE_LIMIT = 8;
  // The quick actions, filtered by query against `name`. Handlers are the EXISTING
  // control functions (no new behavior). `name` is what the query matches and what
  // the row shows; `hint` is the dim secondary line.
  var PALETTE_ACTIONS = [
    { name: 'Fit to view', hint: 'Reset the camera', run: controlFit },
    { name: 'Toggle labels', hint: 'Show all labels at fit zoom', run: controlToggleLabels },
    { name: 'Issues only', hint: 'Filter to flagged nodes', run: controlToggleIssues }
  ];

  var paletteItems = [];   // current result rows: {kind:'node'|'action', node?, action?}
  var paletteIndex = -1;   // highlighted row index, or -1 (nothing highlighted)
  var paletteOpen = false;

  function getPaletteEl() { return document.getElementById('palette-results'); }

  // Build the result row model for `query`: node matches first (top N), then the
  // actions whose name matches. Empty query => all actions (a hint, not all nodes —
  // WP3c). Pure data; rendering is renderPalette.
  function buildPaletteItems(query) {
    var items = [];
    var q = query.trim().toLowerCase();
    var nodes = findNodes(query, PALETTE_NODE_LIMIT);
    for (var i = 0; i < nodes.length; i++) {
      items.push({ kind: 'node', node: nodes[i] });
    }
    for (var a = 0; a < PALETTE_ACTIONS.length; a++) {
      if (q === '' || PALETTE_ACTIONS[a].name.toLowerCase().indexOf(q) !== -1) {
        items.push({ kind: 'action', action: PALETTE_ACTIONS[a] });
      }
    }
    return items;
  }

  // Render paletteItems into #palette-results, marking paletteIndex active. Rebuilt
  // wholesale each input — small lists (<= 8 nodes + 3 actions). Click handlers bind
  // per row (mousedown so the input keeps focus; preventDefault stops blur).
  function renderPalette() {
    var el = getPaletteEl();
    if (el === null) { return; }
    while (el.firstChild) { el.removeChild(el.firstChild); }
    for (var i = 0; i < paletteItems.length; i++) {
      var it = paletteItems[i];
      var li = document.createElement('li');
      li.className = 'palette-item' + (i === paletteIndex ? ' gw-active' : '');
      li.setAttribute('role', 'option');
      li.setAttribute('aria-selected', i === paletteIndex ? 'true' : 'false');
      var labelEl = document.createElement('div');
      labelEl.className = 'palette-label';
      var hintEl = document.createElement('div');
      hintEl.className = 'palette-hint';
      if (it.kind === 'node') {
        labelEl.textContent = String(it.node.data('label') || it.node.id());
        hintEl.textContent = String(it.node.data('kind') || '') + ' · ' + it.node.id();
      } else {
        labelEl.textContent = it.action.name;
        hintEl.textContent = it.action.hint;
      }
      li.appendChild(labelEl);
      li.appendChild(hintEl);
      (function (idx) {
        // mousedown (not click) + preventDefault: invoke without blurring the input
        // first, so a node jump still runs with the palette state intact.
        li.addEventListener('mousedown', function (e) { e.preventDefault(); invokePaletteItem(idx); });
      })(i);
      el.appendChild(li);
    }
    el.hidden = paletteItems.length === 0;
  }

  // Open the palette: focus the input and render the current results (empty query =>
  // the action list). Idempotent.
  function openPalette() {
    var input = document.getElementById('find-input');
    if (input === null) { return; }
    paletteOpen = true;
    input.setAttribute('aria-expanded', 'true');
    input.focus();
    refreshPalette();
  }

  // Close the palette: hide + clear the dropdown and forget the highlight. Does NOT
  // clear the input value (Esc does that separately, matching the prior find-Esc).
  function closePalette() {
    var input = document.getElementById('find-input');
    var el = getPaletteEl();
    paletteOpen = false;
    paletteIndex = -1;
    paletteItems = [];
    if (el !== null) {
      while (el.firstChild) { el.removeChild(el.firstChild); }
      el.hidden = true;
    }
    if (input !== null) { input.setAttribute('aria-expanded', 'false'); }
  }

  // Rebuild + render from the current input value. Auto-highlights the first row
  // (index 0) when there are results so Enter has a sensible default; -1 when empty.
  function refreshPalette() {
    var input = document.getElementById('find-input');
    var noMatchEl = document.getElementById('find-no-match');
    paletteItems = buildPaletteItems(input ? input.value : '');
    paletteIndex = paletteItems.length > 0 ? 0 : -1;
    // Typing dismisses a stale no-match affordance from a prior failed submit.
    if (noMatchEl) { noMatchEl.hidden = true; }
    renderPalette();
  }

  function movePaletteHighlight(delta) {
    if (paletteItems.length === 0) { return; }
    if (paletteIndex < 0) { paletteIndex = delta > 0 ? 0 : paletteItems.length - 1; }
    else { paletteIndex = (paletteIndex + delta + paletteItems.length) % paletteItems.length; }
    renderPalette();
  }

  // Invoke a result row: a node => selectAndFrame (existing find/select path: one
  // nodeClick + applySelection + camera frame + WP3b reveal-if-hidden); an action =>
  // its handler (zero bridge traffic). Closes the palette after.
  function invokePaletteItem(idx) {
    var it = paletteItems[idx];
    if (!it) { return; }
    if (it.kind === 'node') {
      if (cy !== null) { selectAndFrame(it.node); }
    } else {
      it.action.run();
    }
    closePalette();
  }

  function wireControls() {
    var findInput = document.getElementById('find-input');
    var noMatchEl = document.getElementById('find-no-match');
    var fitBtn = document.getElementById('fit-btn');
    var zoomInBtn = document.getElementById('zoom-in-btn');
    var zoomOutBtn = document.getElementById('zoom-out-btn');
    var labelsBtn = document.getElementById('labels-btn');
    var issuesBtn = document.getElementById('issues-btn');

    if (fitBtn) { fitBtn.addEventListener('click', controlFit); }
    if (zoomInBtn) { zoomInBtn.addEventListener('click', function () { controlZoom(1.2); }); }
    if (zoomOutBtn) { zoomOutBtn.addEventListener('click', function () { controlZoom(1 / 1.2); }); }
    if (labelsBtn) { labelsBtn.addEventListener('click', controlToggleLabels); }
    if (issuesBtn) { issuesBtn.addEventListener('click', controlToggleIssues); }
    if (findInput) {
      // WP3c (#144): typing rebuilds the palette dropdown (open it lazily on first
      // input so a programmatic value-set + 'input' event — as the verify harness
      // drives Find — also populates results).
      findInput.addEventListener('input', function () {
        if (!paletteOpen) { paletteOpen = true; findInput.setAttribute('aria-expanded', 'true'); }
        refreshPalette();
      });
      findInput.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowDown') {
          e.preventDefault();
          if (!paletteOpen) { openPalette(); } else { movePaletteHighlight(1); }
        } else if (e.key === 'ArrowUp') {
          e.preventDefault();
          if (!paletteOpen) { openPalette(); } else { movePaletteHighlight(-1); }
        } else if (e.key === 'Enter') {
          e.preventDefault();
          // Enter invokes the highlighted item; with nothing highlighted (empty
          // results / no-match) fall back to the ADR-023 D3 best-match find — which
          // surfaces #find-no-match on a true miss (and stays bridge-silent then).
          if (paletteIndex >= 0 && paletteIndex < paletteItems.length) {
            invokePaletteItem(paletteIndex);
          } else {
            controlFind(noMatchEl);
          }
        } else if (e.key === 'Escape') {
          findInput.value = '';
          if (noMatchEl) { noMatchEl.hidden = true; }
          closePalette();
          findInput.blur();
        }
      });
      // Click outside the #controls cluster closes the palette (the input keeps its
      // value; clicking a row is handled by the per-row mousedown above).
      document.addEventListener('mousedown', function (e) {
        if (!paletteOpen) { return; }
        var controls = document.getElementById('controls');
        if (controls && !controls.contains(e.target)) { closePalette(); }
      });
    }

    // ADR-023 D5: web-layer keyboard, scoped to the bundle (the native ADR-022
    // shortcuts fire when Avalonia chrome has focus). Only act when cy exists;
    // never swallow keys we do not handle.
    document.addEventListener('keydown', function (e) {
      var key = e.key;
      // WP3c (#144): Ctrl+K / Cmd+K opens the command palette and focuses the input.
      if ((e.ctrlKey || e.metaKey) && (key === 'k' || key === 'K')) {
        if (findInput) { e.preventDefault(); openPalette(); }
        return;
      }
      // Ctrl+F stays an alias for opening the palette (ADR-023 D5 — keep working).
      if ((e.ctrlKey || e.metaKey) && (key === 'f' || key === 'F')) {
        if (findInput) { e.preventDefault(); openPalette(); }
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
  wireMinimap();  // WP3d (#146): bind the minimap click/drag-to-pan once (cy-independent).

  // ADR-026 WP1b: index.html ships DARK chrome var defaults on :root (so the bundle is
  // byte-identical pre-theme-command), but assert them from the JS table too so the
  // single source of truth is CHROME[currentVariant]. Default currentVariant = 'dark'.
  applyChromeVariant(currentVariant);

  window.bridge.send({ type: 'ready', userAgent: navigator.userAgent });
})();
