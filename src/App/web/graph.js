// graph.js - production cytoscape setup + command handlers (AP 2.2 S3, ADR-004).
// Dataset arrives from .NET as chunked bridge.dispatch({type:'graphChunk',...})
// calls (WebResourceRequested in Avalonia.Controls.WebView 11.4.0 cannot serve
// responses, so fetch()-based transfer is not possible - GraphSpike evidence).
// Wire contract (ADR-004 D4): node {id, label, kind, x, y, root?:true},
// edge {id, s, t, rel: 'member'|'contains'}; positions are .NET-precomputed
// (preset layout), kind strings are AdObjectKind enum names verbatim.
(function () {
  'use strict';

  var cy = null;
  var pendingNodes = [];
  var pendingEdges = [];

  function initGraph() {
    if (cy !== null) { cy.destroy(); cy = null; }
    var elements = [];
    var i, n, e, data;
    for (i = 0; i < pendingNodes.length; i++) {
      n = pendingNodes[i];
      data = { id: n.id, label: n.label, kind: n.kind };
      if (n.root) { data.root = true; }
      elements.push({ group: 'nodes', data: data, position: { x: n.x, y: n.y } });
    }
    for (i = 0; i < pendingEdges.length; i++) {
      e = pendingEdges[i];
      elements.push({ group: 'edges', data: { id: e.id, source: e.s, target: e.t, rel: e.rel } });
    }

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
          selector: "edge[rel='contains']",
          style: {
            'curve-style': 'bezier',
            width: 1,
            'line-style': 'dashed',
            'line-color': '#4A5568',
            opacity: 0.2
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
    cy.one('render', function () {
      window.bridge.send({
        type: 'loaded',
        nodeCount: cy.nodes().length,
        edgeCount: cy.edges().length
      });
    });
  }

  function focusOn(ids) {
    // ADR-004 D5: cy.getElementById ONLY - selector concatenation silently
    // matches nothing for every comma-containing DN.
    var col = cy.collection();
    for (var i = 0; i < ids.length; i++) {
      col = col.union(cy.getElementById(ids[i]));
    }
    cy.fit(col, 80);
    cy.one('render', function () { window.bridge.send({ type: 'focused' }); });
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
        case 'clickTest':
          // Synthetic tap; same handler path as a human click on the node.
          cy.getElementById(cmd.id).emit('tap');
          break;
        case 'focus':
          focusOn(cmd.ids);
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
