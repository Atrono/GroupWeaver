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

  function sendLoaded() {
    window.bridge.send({
      type: 'loaded',
      nodeCount: cy.nodes().length,
      edgeCount: cy.edges().length
    });
  }

  function initGraph() {
    if (cy !== null) { cy.destroy(); cy = null; }
    var elements = takePendingElements();

    cy = cytoscape({
      container: document.getElementById('cy'),
      elements: elements,
      layout: { name: 'preset' },        // positions precomputed in .NET (ADR-004 D1/D3)
      pixelRatio: 1,
      hideEdgesOnViewport: true,
      textureOnViewport: true,
      motionBlur: false,
      wheelSensitivity: 0.2,
      style: [
        {
          selector: 'node',
          style: {
            label: 'data(label)',
            'text-valign': 'bottom',
            'text-margin-y': 4,
            'font-size': 10,
            'min-zoomed-font-size': 10,  // labels only appear once zoomed in (ADR-004)
            color: '#E8ECF2',
            'text-outline-width': 2,
            'text-outline-color': '#1b1f27'
          }
        },
        // Palette MUST stay in lockstep with src/App/Views/AdObjectKindConverters.cs
        // (pinned by WebBundleTests.Graph_PaletteMatchesAdObjectKindConverters).
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
          style: { shape: 'diamond', width: 22, height: 22, 'background-color': '#A14000' }
        },
        {
          selector: "node[kind='UniversalGroup']",
          style: { shape: 'pentagon', width: 22, height: 22, 'background-color': '#744DA9' }
        },
        {
          selector: "node[kind='OrganizationalUnit']",
          style: { shape: 'round-rectangle', width: 22, height: 22, 'background-color': '#0F6CBD' }
        },
        {
          selector: "node[kind='Computer']",
          style: { shape: 'rectangle', width: 14, height: 14, 'background-color': '#556070' }
        },
        {
          selector: "node[kind='External']",
          style: {
            shape: 'ellipse', width: 14, height: 14, 'background-color': '#757575',
            'border-width': 2, 'border-style': 'dashed', 'border-color': '#B0B6BF'
          }
        },
        {
          selector: 'node[?root]',
          style: { width: 30, height: 30, 'border-width': 3, 'border-color': '#E8ECF2' }
        },
        // Severity (AP 3.4, ADR-010): owns the overlay-* channel ONLY - the halo
        // paints behind the node, touching neither the kind fill/shape nor the
        // root/External border. Appended AFTER node[?root] so these rules win only
        // on overlay-*. Palette PINNED + parity-tripwired in verify.mjs (SEVERITY).
        // Monotonic padding (7/6/5) is a colorblind-redundant channel. No `sev`
        // field => no rule matches => overlay-opacity default 0 => byte-identical.
        // NO label override anywhere: the kind name stays the only label.
        {
          selector: "node[sev='error']",
          style: { 'overlay-color': '#D13438', 'overlay-opacity': 0.45, 'overlay-padding': 7 }
        },
        {
          selector: "node[sev='warning']",
          style: { 'overlay-color': '#F7A30B', 'overlay-opacity': 0.45, 'overlay-padding': 6 }
        },
        {
          selector: "node[sev='info']",
          style: { 'overlay-color': '#4FA3E3', 'overlay-opacity': 0.40, 'overlay-padding': 5 }
        },
        // Roll-up ring cue: a loaded group hiding flagged descendants gets a wider,
        // fainter max-severity glow keyed to belowSev. NOT a number on canvas
        // (canvas-only cytoscape has no pseudo-elements) - the count is
        // authoritative in the sidebar (AP 3.4 S4/S5).
        {
          selector: 'node[below]',
          style: { 'overlay-padding': 10, 'overlay-opacity': 0.30, 'overlay-color': '#D13438' }
        },
        {
          selector: "node[below][belowSev='warning']",
          style: { 'overlay-color': '#F7A30B' }
        },
        {
          selector: "node[below][belowSev='info']",
          style: { 'overlay-color': '#4FA3E3' }
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
          style: { 'underlay-color': '#2FAE4E', 'underlay-opacity': 0.5, 'underlay-padding': 8 }
        },
        {
          selector: "node[diff='removed']",
          style: {
            'underlay-color': '#E0503A', 'underlay-opacity': 0.5, 'underlay-padding': 8,
            'background-opacity': 0.45
          }
        },
        {
          selector: "node[diff='unchecked']",
          style: { 'underlay-color': '#8A8F98', 'underlay-opacity': 0.35, 'underlay-padding': 6 }
        },
        {
          selector: "edge[rel='member']",
          style: {
            'curve-style': 'bezier',     // bezier keeps the seeded A<->B cycle legible (ADR-004 D2)
            width: 1.5,
            'line-color': '#7A8699',
            opacity: 0.85,
            'target-arrow-shape': 'triangle',
            'target-arrow-color': '#7A8699'
          }
        },
        {
          // Dashed = containment; the dash pattern (not color) separates it from
          // member edges. Opacity tuned for ~2.45:1 blended contrast on #1b1f27
          // (ui-checklist A2: legible, verifier-justified vs the 3:1 floor by the
          // crisp dash pattern; bump to ~0.72 if a hard 3:1 is ever required)
          // while staying visually subordinate to member edges.
          selector: "edge[rel='contains']",
          style: {
            'curve-style': 'bezier',
            width: 1,
            'line-style': 'dashed',
            'line-color': '#7A8699',
            opacity: 0.6
          }
        },
        // Gap diff EDGE override (AP 66, ADR-015 Slice 5): placed AFTER the
        // edge[rel=...] rules so it WINS line-color / line-style on a diffed edge.
        // line-style (solid/dashed/dotted) is the colorblind-redundant channel for
        // edges; an edge with no `diff` field keeps its rel styling unchanged.
        // Palette PINNED + parity-tripwired in verify.mjs (DIFF / DIFF_LINE).
        {
          selector: "edge[diff='added']",
          style: { 'line-color': '#2FAE4E', 'target-arrow-color': '#2FAE4E', opacity: 0.95 }
        },
        {
          selector: "edge[diff='removed']",
          style: {
            'line-color': '#E0503A', 'target-arrow-color': '#E0503A',
            'line-style': 'dashed', opacity: 0.85
          }
        },
        {
          selector: "edge[diff='unchecked']",
          style: {
            'line-color': '#8A8F98', 'target-arrow-color': '#8A8F98',
            'line-style': 'dotted', opacity: 0.5
          }
        }
      ]
    });

    // Same code path a human click takes: cy tap handler -> bridge -> .NET.
    cy.on('tap', 'node', function (evt) {
      window.bridge.send({
        type: 'nodeClick',
        id: evt.target.id(),
        label: evt.target.data('label'),
        kind: evt.target.data('kind')
      });
    });
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
    cy.one('render', sendLoaded);
    cy.batch(function () {
      cy.elements().remove();
      cy.add(elements);
    });
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
    // Register BEFORE mutating, same rationale as updateGraph: cy.fit schedules
    // the redraw, and a listener attached after a render already fired would
    // leave 'focused' unsent (FocusAsync would hit its bounded wait). Same
    // synchronous turn, so no stale pre-fit render can slip in between.
    cy.one('render', function () { window.bridge.send({ type: 'focused' }); });
    cy.fit(col, 80);
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

  window.bridge.send({ type: 'ready', userAgent: navigator.userAgent });
})();
