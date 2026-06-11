// graph.js - cytoscape setup + spike command handlers.
// Dataset arrives from .NET as chunked bridge.dispatch({type:'graphChunk',...})
// calls (WebResourceRequested in Avalonia.Controls.WebView 11.4.0 cannot serve
// responses, so fetch()-based transfer is not possible - see RESULTS.md).
(function () {
  'use strict';

  var cy = null;
  var pendingNodes = [];
  var pendingEdges = [];
  var firstChunkAt = 0;
  var lastChunkAt = 0;

  function initGraph(textureOnViewport) {
    if (cy !== null) { cy.destroy(); cy = null; }
    var tBuild0 = performance.now();
    var elements = [];
    var i, n, e;
    for (i = 0; i < pendingNodes.length; i++) {
      n = pendingNodes[i];
      elements.push({
        group: 'nodes',
        data: { id: n.id, label: n.label, kind: n.kind },
        position: { x: n.x, y: n.y }
      });
    }
    for (i = 0; i < pendingEdges.length; i++) {
      e = pendingEdges[i];
      elements.push({ group: 'edges', data: { id: e.id, source: e.s, target: e.t } });
    }

    var tInit0 = performance.now();
    cy = cytoscape({
      container: document.getElementById('cy'),
      elements: elements,
      layout: { name: 'preset' },          // positions precomputed in .NET (BFS depth = ring)
      pixelRatio: 1,
      hideEdgesOnViewport: true,
      textureOnViewport: !!textureOnViewport, // measured both ways: false = honest full redraw, true = production knob
      motionBlur: false,
      wheelSensitivity: 0.2,
      style: [
        {
          selector: 'node',
          style: {
            width: 8, height: 8,
            'background-color': '#8899aa',
            label: 'data(label)',
            color: '#cfd8e3',
            'font-size': 9,
            'min-zoomed-font-size': 12,    // labels only appear once zoomed in
            'text-opacity': 0.85
          }
        },
        { selector: 'node[kind = "root"]', style: { 'background-color': '#ff5555', width: 22, height: 22 } },
        { selector: 'node[kind = "ou"]', style: { 'background-color': '#e0a430', width: 14, height: 14 } },
        { selector: 'node[kind = "group"]', style: { 'background-color': '#4f9cf0' } },
        { selector: 'node[kind = "user"]', style: { 'background-color': '#5fbf77' } },
        { selector: 'node[kind = "computer"]', style: { 'background-color': '#b07fd9' } },
        {
          selector: 'edge',
          style: {
            'curve-style': 'haystack',     // cheapest edge rendering, no arrows
            'haystack-radius': 0,
            width: 1,
            'line-color': '#56627a',
            opacity: 0.55
          }
        }
      ]
    });
    var cyInitMs = performance.now() - tInit0;

    // Same code path a human click takes: cy tap handler -> bridge -> .NET.
    cy.on('tap', 'node', function (evt) {
      window.bridge.send({
        type: 'nodeClick',
        id: evt.target.id(),
        label: evt.target.data('label'),
        kind: evt.target.data('kind')
      });
    });

    cy.fit(undefined, 30);
    cy.one('render', function () {
      var webgl2;
      try {
        webgl2 = !!document.createElement('canvas').getContext('webgl2');
      } catch (err) { webgl2 = false; }
      window.bridge.send({
        type: 'loaded',
        webgl2: webgl2,
        textureOnViewport: !!textureOnViewport,
        nodeCount: cy.nodes().length,
        edgeCount: cy.edges().length,
        chunkSpanMs: lastChunkAt - firstChunkAt,
        buildMs: tInit0 - tBuild0,
        cyInitMs: cyInitMs,
        firstRenderMs: performance.now() - tInit0
      });
    });
  }

  // Synthetic pan/zoom animation driven by requestAnimationFrame for ~durationMs;
  // FPS derived from rAF deltas (avg = frames/duration, min = worst single delta).
  function measureFps(durationMs) {
    var deltas = [];
    var baseZoom = cy.zoom();
    var basePan = { x: cy.pan().x, y: cy.pan().y };
    var rect = document.getElementById('cy').getBoundingClientRect();
    var center = { x: rect.width / 2, y: rect.height / 2 };
    var t0 = null;
    var last = null;

    function frame(now) {
      if (t0 === null) { t0 = now; last = now; requestAnimationFrame(frame); return; }
      deltas.push(now - last);
      last = now;
      var t = (now - t0) / 1000;
      cy.viewport({
        zoom: baseZoom * (1 + 0.5 * Math.sin(t * 2.0)),
        pan: {
          x: basePan.x + Math.sin(t * 1.7) * 120,
          y: basePan.y + Math.cos(t * 1.3) * 120
        }
      });
      if (now - t0 < durationMs) {
        requestAnimationFrame(frame);
      } else {
        cy.viewport({ zoom: baseZoom, pan: basePan });
        var sum = 0, maxDelta = 0, i;
        for (i = 0; i < deltas.length; i++) {
          sum += deltas[i];
          if (deltas[i] > maxDelta) { maxDelta = deltas[i]; }
        }
        window.bridge.send({
          type: 'fps',
          frames: deltas.length,
          durationMs: sum,
          avgFps: deltas.length > 0 ? 1000 / (sum / deltas.length) : 0,
          minFps: maxDelta > 0 ? 1000 / maxDelta : 0,
          maxFrameMs: maxDelta
        });
      }
    }
    requestAnimationFrame(frame);
  }

  // Human-gesture path: cytoscape only engages hideEdgesOnViewport /
  // textureOnViewport while (pinching || hoverData.dragging || swipePanning ||
  // wheelZooming) - flags set by its DOM input handlers, never by cy.viewport().
  // So we additionally measure with synthetic DOM mouse-drag + wheel events on
  // the container: the exact code path of a human pan/zoom.
  function measureGestureFps(durationMs) {
    var container = document.getElementById('cy');
    var rect = container.getBoundingClientRect();
    var cx = rect.left + rect.width / 2;
    var cyMid = rect.top + rect.height / 2;
    var deltas = [];
    var t0 = null;
    var last = null;
    var dragging = false;
    var baseZoom = cy.zoom();
    var basePan = { x: cy.pan().x, y: cy.pan().y };

    function mouse(type, x, y) {
      container.dispatchEvent(new MouseEvent(type, {
        bubbles: true, cancelable: true, view: window,
        clientX: x, clientY: y, button: 0, buttons: type === 'mouseup' ? 0 : 1
      }));
    }
    function wheel(x, y, dy) {
      container.dispatchEvent(new WheelEvent('wheel', {
        bubbles: true, cancelable: true, view: window,
        clientX: x, clientY: y, deltaY: dy
      }));
    }

    function frame(now) {
      if (t0 === null) {
        t0 = now;
        last = now;
        mouse('mousedown', cx + 37, cyMid + 23); // off-center: background, not a node
        dragging = true;
        requestAnimationFrame(frame);
        return;
      }
      deltas.push(now - last);
      last = now;
      var t = now - t0;
      if (t < durationMs / 2) {
        var ts = t / 1000; // drag-pan along a lissajous
        mouse('mousemove', cx + 37 + Math.sin(ts * 2.2) * 140, cyMid + 23 + Math.cos(ts * 1.7) * 100);
      } else {
        if (dragging) { mouse('mouseup', cx + 37, cyMid + 23); dragging = false; }
        wheel(cx, cyMid, Math.floor(t / 400) % 2 === 0 ? -120 : 120); // wheel-zoom in/out
      }
      if (t < durationMs) {
        requestAnimationFrame(frame);
      } else {
        if (dragging) { mouse('mouseup', cx + 37, cyMid + 23); }
        cy.viewport({ zoom: baseZoom, pan: basePan });
        var sum = 0, maxDelta = 0, i;
        for (i = 0; i < deltas.length; i++) {
          sum += deltas[i];
          if (deltas[i] > maxDelta) { maxDelta = deltas[i]; }
        }
        window.bridge.send({
          type: 'gestureFps',
          frames: deltas.length,
          durationMs: sum,
          avgFps: deltas.length > 0 ? 1000 / (sum / deltas.length) : 0,
          minFps: maxDelta > 0 ? 1000 / maxDelta : 0,
          maxFrameMs: maxDelta
        });
      }
    }
    requestAnimationFrame(frame);
  }

  function expandNode(cmd) {
    var t0 = performance.now();
    cy.batch(function () {
      var i;
      for (i = 0; i < cmd.children.length; i++) {
        var n = cmd.children[i];
        cy.add({
          group: 'nodes',
          data: { id: n.id, label: n.label, kind: n.kind },
          position: { x: n.x, y: n.y }
        });
      }
      for (i = 0; i < cmd.edges.length; i++) {
        var e = cmd.edges[i];
        cy.add({ group: 'edges', data: { id: e.id, source: e.s, target: e.t } });
      }
    });
    window.bridge.send({
      type: 'expanded',
      nodeCount: cy.nodes().length,
      edgeCount: cy.edges().length,
      addMs: performance.now() - t0
    });
  }

  window.bridge.onCommand(function (cmd) {
    switch (cmd.type) {
      case 'graphChunk':
        if (firstChunkAt === 0) { firstChunkAt = performance.now(); }
        if (cmd.nodes) { Array.prototype.push.apply(pendingNodes, cmd.nodes); }
        if (cmd.edges) { Array.prototype.push.apply(pendingEdges, cmd.edges); }
        lastChunkAt = performance.now();
        break;
      case 'graphCommit':
        initGraph(false);
        break;
      case 'reinit':
        // Rebuild cy from the buffered dataset with a different renderer config
        // (used to measure FPS with textureOnViewport on vs. off).
        initGraph(!!cmd.textureOnViewport);
        break;
      case 'measureFps':
        measureFps(cmd.durationMs || 3000);
        break;
      case 'measureGestureFps':
        measureGestureFps(cmd.durationMs || 3000);
        break;
      case 'clickTest':
        // Synthetic tap; same handler path as a human click on the node.
        cy.$('#' + cmd.id).emit('tap');
        break;
      case 'expand':
        expandNode(cmd);
        break;
      case 'focus':
        // Zoom onto the given nodes so the screenshot shows real nodes/labels.
        cy.fit(cy.$(cmd.ids.map(function (id) { return '#' + id; }).join(', ')), 80);
        cy.one('render', function () { window.bridge.send({ type: 'focused' }); });
        break;
      case 'ping':
        window.bridge.send({ type: 'pong', seq: cmd.seq });
        break;
      case 'triggerError':
        // Throw outside the dispatch call stack so window.onerror handles it.
        setTimeout(function () { throw new Error('GraphSpike deliberate test error'); }, 0);
        break;
      default:
        window.bridge.send({ type: 'jsError', source: 'bridge', message: 'unknown command: ' + cmd.type });
    }
  });

  window.bridge.send({ type: 'ready', userAgent: navigator.userAgent });
})();
