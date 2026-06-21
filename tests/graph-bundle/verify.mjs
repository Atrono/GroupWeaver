// verify.mjs - Playwright verification of the literal shipped graph bundle
// (ADR-004 D3/D6/D7 + Consequences): the SAME src/App/web files the app serves,
// on the SAME file:// origin, fed the SAME chunked wire protocol, against the
// literal GraphBuilder output (--demo --dump-graph fixture).
//
// Plain Node + the playwright LIBRARY on purpose (@playwright/test rejected in
// ADR-004: extra surface for one sequential spec). Sequential, event-driven,
// zero sleeps/retries/FPS - every wait is a bridge-message promise with a
// 60 s timeout, and a global watchdog (GRAPH_BUNDLE_TIMEOUT_MS, default 5 min)
// bounds the whole run. Any failed assert => nonzero exit.
//
// Usage: node verify.mjs <demo-graph.json> <screenshot-dir>
//   <demo-graph.json>  flat {nodes,edges} dump produced by
//                      GroupWeaver.App --demo --dump-graph (tools/test-graph-bundle.ps1)
//   <screenshot-dir>   receives graph-overview.png / graph-focus.png /
//                      graph-cycle.png / graph-expanded.png

import { mkdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { chromium } from 'playwright';

// THE C#/JS palette parity tripwire (ADR-004 Consequences: "pinned solely there").
// Hand-copied from src/App/Views/AdObjectKindConverters.cs - if either that file
// or graph.js drifts, this harness fails.
const PALETTE = {
  User: '#038387',
  GlobalGroup: '#107C10',
  DomainLocalGroup: '#A14000',
  UniversalGroup: '#744DA9',
  OrganizationalUnit: '#0F6CBD',
  Computer: '#556070',
  External: '#757575',
};
// The 7 AdObjectKind names, in PALETTE order. The encoding-key legend (#87) must
// carry exactly one `#legend [data-kind="<Kind>"]` row per name, each with a live
// per-kind `.count` matching cy.nodes() grouped by data('kind'). Pinned here so a
// legend that drops/renames a kind row, or adds an 8th, fails loudly.
const KIND_NAMES = Object.keys(PALETTE);

// THE C#/JS severity parity tripwire (AP 3.4, ADR-010 D1). Hand-copied from the
// pinned palette: if ADR-010 / GraphJson.SeverityWire / graph.js' node[sev=...]
// overlay rules drift on the color, this harness fails - the exact analogue of
// PALETTE above for the kind fill. Per-severity overlay GEOMETRY (the redundant
// colorblind channel: monotonic padding 7/6/5, opacity 0.45/0.45/0.40) is pinned
// alongside the color so a same-color-wrong-thickness regression is also caught.
const SEVERITY = {
  error: '#D13438',
  warning: '#F7A30B',
  info: '#4FA3E3',
};
const SEVERITY_OVERLAY = {
  // overlay-color, overlay-opacity, overlay-padding per ADR-010 D1 table.
  error: { color: '#D13438', opacity: 0.45, padding: 7 },
  warning: { color: '#F7A30B', opacity: 0.45, padding: 6 },
  info: { color: '#4FA3E3', opacity: 0.40, padding: 5 },
};
// The roll-up "n below" ring cue (ADR-010 D4): a loaded group hiding flagged
// descendants gets a WIDER, FAINTER max-severity glow keyed off belowSev. The
// node[below] rules sit AFTER node[sev=...] so they win the overlay channel even
// on a node that is itself flagged - padding 10 (> every per-sev padding),
// opacity 0.30 (< every per-sev opacity), color = SEVERITY[belowSev].
const ROLLUP_OVERLAY = { opacity: 0.30, padding: 10 };

// THE in-canvas busy-ring tripwire (ADR-019 / #94). The graph.js node[busy][!sev]
// rule sits AFTER node[below] and paints the overlay channel ONLY on a node with NO
// own severity ([!sev]) - so a finding's halo always wins (severity > busy). The
// `busy` data flag is transient (set by the {type:'busy'} command, self-cleared by
// the next graphUpdate's remove-all/add-all). Static: no tween. Hand-copied from
// graph.js so any drift in the rule's color/opacity/padding fails HERE.
const BUSY = { color: '#4FA3E3', opacity: 0.35, padding: 8 };

// THE C#/JS diff parity tripwire (AP 66, ADR-015 Slice 5). The graph-layer
// analogue of SEVERITY: hand-copied from the diff palette so a drift between the
// wire `diff` tokens, graph.js' node[diff=...]/edge[diff=...] rules, and this
// harness fails here. Diff owns the cytoscape underlay-* channel on NODES and a
// line-* override on EDGES, DISJOINT from kind (background-color/shape),
// root/External (border-*) and severity (overlay-*) - a node can be both a diff
// status AND a finding with no channel collision (the COEXIST keystone below).
// Greens/reds chosen to read distinct from GG #107C10 and severity error #D13438.
const DIFF = {
  added: '#2FAE4E',
  removed: '#E0503A',
  unchecked: '#8A8F98',
};
// Per-status NODE underlay geometry + the removed node's faded background-opacity
// (the colorblind-redundant BRIGHTNESS channel: added stays full-opacity, removed
// fades to 0.45 so added != removed without relying on green-vs-red hue). padding
// 8/8/6, opacity 0.5/0.5/0.35 per the ADR-015 underlay table; bgOpacity is the
// kind-fill fade and is null where the kind fill stays fully opaque (default 1).
const DIFF_UNDERLAY = {
  added: { color: '#2FAE4E', opacity: 0.5, padding: 8, bgOpacity: null },
  removed: { color: '#E0503A', opacity: 0.5, padding: 8, bgOpacity: 0.45 },
  unchecked: { color: '#8A8F98', opacity: 0.35, padding: 6, bgOpacity: null },
};
// Per-status EDGE line override: diff line-color + the colorblind-redundant
// line-style (added keeps solid, removed dashed, unchecked dotted).
const DIFF_LINE = {
  added: { color: '#2FAE4E', style: 'solid' },
  removed: { color: '#E0503A', style: 'dashed' },
  unchecked: { color: '#8A8F98', style: 'dotted' },
};

// THE interaction-feedback parity tripwire (ADR-018 / #89). The graph-layer
// analogue of PALETTE/SEVERITY/DIFF: hand-copied from ADR-018 D1 so any drift
// between the ADR's channel-ownership contract and graph.js' node.gw-dim /
// node.gw-hover / node:selected rules fails HERE. The brightness channel is
// cytoscape `background-blacken` (NEGATIVE = brighter; there is NO
// `background-brighten` property - do not assert it): dim darkens the kind fill
// by +0.6 (leaving the severity overlay-* halo and diff underlay-* at full
// strength - the whole reason dim rides background-blacken, never element
// opacity), hover brightens it by -0.15. Selection rides border + z only
// (white #FFFFFF border, width 3) so it composes with kind/root/External/diff.
const SELECTION = {
  dimBlacken: 0.6,
  hoverBlacken: -0.15,
  selBorderColor: '#FFFFFF',
  selBorderWidth: 3,
};
// The base/selective label-gate floor (ADR-018 D4 / ADR-004): the base `node`
// rule keeps min-zoomed-font-size 10 (labels hidden until zoomed in), but the
// node[?root] and node[sev='error'] rules force 0 so the root and Error-severity
// nodes stay labeled at the fit-zoom overview. node:selected ALSO forces 0 (a
// transient force while selected) - so the F9 selective-label assert below pins
// root + Error against a PLAIN unflagged, UNSELECTED node, never the selection.
const LABEL_MZFS = { forced: 0, baseFloor: 10 };

const MESSAGE_TIMEOUT_MS = 60_000;
// Node diameter D=44 (ADR-004 D3): model-space floor; the xUnit geometry test
// pins the stronger ~59.7 bound, this render-side assert pins "no overlap".
const MIN_CENTER_DISTANCE = 44;
// Harness chunk caps - same SHAPE as GraphChunker (greedy fill, nodes and edges
// may share a chunk, trailing graphCommit), smaller caps so the ~200-node demo
// graph exercises the accumulator across >= 3 dispatches.
const MAX_NODES_PER_CHUNK = 64;
const MAX_EDGES_PER_CHUNK = 128;

let assertCount = 0;
function assert(condition, message) {
  if (!condition) {
    throw new Error(`ASSERT FAILED: ${message}`);
  }
  assertCount += 1;
}

// ---------------------------------------------------------------------------
// Global watchdog (CI incident 2026-06-12, run 27409858814: a renderer stall
// on the 2-core windows-latest runner turned `await page.evaluate(...)` into a
// 3 h silent hang). Await-boundedness inventory for this file: awaitMessage()
// waits are bounded (MESSAGE_TIMEOUT_MS, 60 s each); page.goto and
// page.screenshot carry Playwright's 30 s defaults; chromium.launch defaults
// to 180 s. page.evaluate has NO default timeout in Playwright, and
// newPage/exposeFunction/addInitScript/browser.close are plain protocol calls
// - those bare awaits stay acceptable ONLY because this watchdog bounds the
// whole run. A pending await does NOT block the Node event loop (protocol
// calls are async I/O), so this timer always fires; the explicit
// process.exit() on success/failure means a ref'd timer never delays a
// finished run.
// ---------------------------------------------------------------------------
const WATCHDOG_MS = Number(process.env.GRAPH_BUNDLE_TIMEOUT_MS) > 0
  ? Number(process.env.GRAPH_BUNDLE_TIMEOUT_MS)
  : 5 * 60_000;
let lastPhase = 'startup (no phase completed)';
function phase(name) {
  lastPhase = name;
  console.log(`[verify] ${name}`);
}
setTimeout(() => {
  console.error(
    `FAILED watchdog: run exceeded ${WATCHDOG_MS} ms; last completed phase: ${lastPhase}`);
  process.exit(1);
}, WATCHDOG_MS);

// ---------------------------------------------------------------------------
// Bridge message plumbing: every window.__bridgeSendShim(text) call lands in
// onBridgeMessage; awaitMessage() consumes messages by type (FIFO), resolving
// immediately if one already arrived, rejecting with context after 60 s.
// ---------------------------------------------------------------------------
const allMessages = [];
const pendingByType = new Map();
const waitersByType = new Map();

function onBridgeMessage(text) {
  const msg = JSON.parse(text);
  allMessages.push(msg);
  const waiter = waitersByType.get(msg.type)?.shift();
  if (waiter) {
    clearTimeout(waiter.timer);
    waiter.resolve(msg);
    return;
  }
  if (!pendingByType.has(msg.type)) {
    pendingByType.set(msg.type, []);
  }
  pendingByType.get(msg.type).push(msg);
}

function awaitMessage(type, context) {
  const queued = pendingByType.get(type)?.shift();
  if (queued) {
    return Promise.resolve(queued);
  }
  return new Promise((resolvePromise, rejectPromise) => {
    const timer = setTimeout(() => {
      const list = waitersByType.get(type) ?? [];
      const index = list.findIndex((w) => w.resolve === resolvePromise);
      if (index >= 0) {
        list.splice(index, 1);
      }
      const seen = allMessages.map((m) => m.type).join(', ') || '(none)';
      rejectPromise(new Error(
        `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (${context}); messages seen so far: ${seen}`));
    }, MESSAGE_TIMEOUT_MS);
    if (!waitersByType.has(type)) {
      waitersByType.set(type, []);
    }
    waitersByType.get(type).push({ resolve: resolvePromise, timer });
  });
}

// Harness-side chunker - same SHAPE as GraphChunker (greedy fill, nodes and
// edges may share a chunk, ADR-004 D4); the trailing commit verb is the
// caller's choice: graphCommit = full init, graphUpdate = replace-in-place on
// the live instance (ADR-005 D1).
function toChunks(nodes, edges) {
  const chunks = [];
  let nodeIndex = 0;
  let edgeIndex = 0;
  while (nodeIndex < nodes.length || edgeIndex < edges.length) {
    const chunkNodes = nodes.slice(nodeIndex, nodeIndex + MAX_NODES_PER_CHUNK);
    const chunkEdges = edges.slice(edgeIndex, edgeIndex + MAX_EDGES_PER_CHUNK);
    chunks.push({ type: 'graphChunk', nodes: chunkNodes, edges: chunkEdges });
    nodeIndex += chunkNodes.length;
    edgeIndex += chunkEdges.length;
  }
  return chunks;
}

// Undirected adjacency over the fixture (ADR-018 selection phase): a node's
// 1-hop neighbors = every node it shares an edge with, ignoring edge direction
// (the dim ring is closedNeighborhood(), which is direction-agnostic). Only
// edges with both endpoints present as fixture nodes count - a raw-DN frontier
// member edge has no node and is irrelevant to the dim ring. Keyed by exact DN
// string (comma-safe; getElementById is the byte-identical lookup downstream).
function buildAdjacency(nodes, edges) {
  const adj = new Map();
  for (const n of nodes) { adj.set(n.id, new Set()); }
  for (const e of edges) {
    if (adj.has(e.s) && adj.has(e.t)) {
      adj.get(e.s).add(e.t);
      adj.get(e.t).add(e.s);
    }
  }
  return adj;
}

// cytoscape's canvas renderer reports colors as rgb(...)/rgba(...); normalize to
// uppercase #RRGGBB for comparison against the C# hex literals.
function toHex(cssColor) {
  const rgb = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/.exec(cssColor);
  if (rgb) {
    return `#${rgb.slice(1, 4).map((v) => Number(v).toString(16).padStart(2, '0')).join('')}`.toUpperCase();
  }
  if (/^#[0-9a-f]{6}$/i.test(cssColor)) {
    return cssColor.toUpperCase();
  }
  if (/^#[0-9a-f]{3}$/i.test(cssColor)) {
    return `#${[...cssColor.slice(1)].map((c) => c + c).join('')}`.toUpperCase();
  }
  throw new Error(`unrecognized CSS color format: '${cssColor}'`);
}

// cytoscape returns numeric overlay-opacity / overlay-padding as strings
// (e.g. "0.45", "7px") or bare numbers depending on the property; coerce to a
// finite number so the per-severity geometry asserts compare cleanly.
function toNumber(cssValue) {
  const m = /-?\d+(?:\.\d+)?/.exec(String(cssValue));
  if (!m) {
    throw new Error(`unrecognized numeric CSS value: '${cssValue}'`);
  }
  return Number(m[0]);
}

// One round-trip pulling the full severity-overlay triple for a DN (byte-
// identical id via getElementById, ADR-004 D5 - selector strings drop comma DNs).
async function overlayOf(page, id) {
  const raw = await page.evaluate((nid) => {
    const el = window.__cy.getElementById(nid);
    return {
      found: el.length === 1,
      color: el.style('overlay-color'),
      opacity: el.style('overlay-opacity'),
      padding: el.style('overlay-padding'),
    };
  }, id);
  return raw;
}

// ---------------------------------------------------------------------------
// ADR-017 motion recorders (F1 enter fade + F2 eased focus-fit). Installed in
// the PAGE context via addInitScript on the SAME page that publishes window.__cy
// (and on the standalone reduced-motion probe page). TWO recorders, isolated on
// purpose so a node enter tween never touches the camera counter - the #1
// critique finding: if it did, assertion #4 (camera must NOT move on graphUpdate)
// false-fails. Both record at CALL time (no mid-tween timing sampling): the enter
// recorder reads `fromOpacity` = the 0 graph.js just set, deterministically.
//   - window.__gwAnimateCalls  : count of cy.animate (CORE method = camera fit, F2)
//   - window.__gwAnimateLastDuration : last camera fit's options duration
//   - window.__gwEnterAnims    : [{id, fromOpacity, duration}] for node enter tweens
//                                (COLLECTION-prototype animate calls carrying a
//                                style.opacity target, F1) - survivors never appear.
// This function is serialized and run as an addInitScript; it must be self-
// contained (no closure over Node-side variables).
function installMotionRecordersSource() {
  window.installMotionRecorders = function (instance) {
    window.__gwAnimateCalls = 0;
    window.__gwAnimateLastDuration = null;
    window.__gwEnterAnims = [];

    // Camera (F2): wrap the CORE animate method. focusOn calls
    // cy.animate({fit:{...}}, {duration, easing, complete}). This counter must
    // ONLY move for the camera fit, never the enter tween (which goes through the
    // collection-prototype animate wrapped below).
    var coreAnimate = instance.animate;
    instance.animate = function (opts, params) {
      window.__gwAnimateCalls += 1;
      window.__gwAnimateLastDuration = (params && params.duration) != null
        ? params.duration
        : (opts && opts.duration);
      return coreAnimate.apply(this, arguments);
    };

    // Enter (F1): wrap the COLLECTION prototype animate. graph.js sets
    // .style('opacity', 0) on genuinely-new nodes then calls
    // <collection>.animate({style:{opacity:1}}, {duration, easing}). Record each
    // element at call time (fromOpacity = the 0 just set) only when the target is
    // a style.opacity tween, so a non-opacity collection animate (none today) is
    // ignored and the camera fit (core method, not this prototype) never lands here.
    var proto = Object.getPrototypeOf(instance.nodes());
    var protoAnimate = proto.animate;
    proto.animate = function (opts, params) {
      if (opts && opts.style && Object.prototype.hasOwnProperty.call(opts.style, 'opacity')) {
        var dur = params && params.duration;
        this.forEach(function (el) {
          window.__gwEnterAnims.push({
            id: el.id(),
            fromOpacity: Number(el.style('opacity')),
            duration: dur,
          });
        });
      }
      return protoAnimate.apply(this, arguments);
    };
  };
}

// ADR-017 F2 barrier: after an animated 'focused' has settled, assert the camera
// EASED (the core cy.animate ran >=1 with a positive duration) AND that the eased
// end-viewport lands on the SAME target a synchronous cy.fit(col,80) would (proves
// the ease's endpoint is the right padding/easing target, not a wrong one). The
// reference fit is computed NON-DESTRUCTIVELY: snapshot the real end viewport,
// fit on a throwaway read, capture it, then restore the snapshot - the visible
// camera is left exactly where the ease left it. Counter reads cross the bridge
// as primitives (CI moral); getElementById keeps comma DNs byte-identical.
async function assertEasedFocus(page, ids, label) {
  const cam = await page.evaluate(() => ({
    calls: window.__gwAnimateCalls,
    duration: window.__gwAnimateLastDuration,
  }));
  assert(cam.calls >= 1,
    `F2 (${label}): camera must EASE via cy.animate on focus - __gwAnimateCalls ${cam.calls} < 1 (still synchronous cy.fit?)`);
  assert(typeof cam.duration === 'number' && cam.duration > 0,
    `F2 (${label}): camera fit must carry a positive animation duration, got ${JSON.stringify(cam.duration)}`);

  const cmp = await page.evaluate((nids) => {
    const cy = window.__cy;
    let col = cy.collection();
    for (let i = 0; i < nids.length; i++) {
      col = col.union(cy.getElementById(nids[i]));
    }
    // Snapshot the eased end viewport.
    const endPan = cy.pan();
    const end = { zoom: cy.zoom(), panX: endPan.x, panY: endPan.y };
    // Reference fit on a throwaway read, then restore the snapshot so the
    // visible camera is left where the ease landed.
    cy.fit(col, 80);
    const refPan = cy.pan();
    const ref = { zoom: cy.zoom(), panX: refPan.x, panY: refPan.y };
    cy.zoom(end.zoom);
    cy.pan({ x: end.panX, y: end.panY });
    return { end, ref };
  }, ids);
  // Tolerance: the ease lands on the fit target up to float/animation rounding.
  const dz = Math.abs(cmp.end.zoom - cmp.ref.zoom);
  const dpx = Math.abs(cmp.end.panX - cmp.ref.panX);
  const dpy = Math.abs(cmp.end.panY - cmp.ref.panY);
  assert(dz < 1e-3 && dpx < 0.5 && dpy < 0.5,
    `F2 (${label}): eased end-viewport must equal the reference cy.fit(col,80) target - `
    + `end (zoom ${cmp.end.zoom}, pan ${cmp.end.panX}/${cmp.end.panY}) `
    + `!= fit (zoom ${cmp.ref.zoom}, pan ${cmp.ref.panX}/${cmp.ref.panY}); `
    + `dz=${dz}, dpx=${dpx}, dpy=${dpy} (wrong padding/easing endpoint?)`);
}

// ---------------------------------------------------------------------------
// Fresh-page probe (AP 2.3 review pin): graphUpdate dispatched BEFORE any
// graphCommit has no live cytoscape instance to update - ADR-005 D1 pins
// "graphUpdate before any graphCommit -> jsError". A SEPARATE page (own
// incognito context via browser.newPage) with its OWN bridge channel on
// purpose: the main session's final audit pins ZERO jsError across its whole
// run, and this probe's deliberately provoked jsError must never reach that
// channel. Harness morals hold: primitives only out of page.evaluate, the
// message waits carry MESSAGE_TIMEOUT_MS, the bare protocol awaits fall under
// the global watchdog (see the boundedness inventory above), no sleeps.
// ---------------------------------------------------------------------------
async function probeGraphUpdateBeforeCommit(browser, indexHtml) {
  const probeMessages = [];
  const probePending = new Map();
  const probeWaiters = new Map();

  function onProbeMessage(text) {
    const msg = JSON.parse(text);
    probeMessages.push(msg);
    const waiter = probeWaiters.get(msg.type)?.shift();
    if (waiter) {
      clearTimeout(waiter.timer);
      waiter.resolve(msg);
      return;
    }
    if (!probePending.has(msg.type)) {
      probePending.set(msg.type, []);
    }
    probePending.get(msg.type).push(msg);
  }

  function awaitProbeMessage(type, context) {
    const queued = probePending.get(type)?.shift();
    if (queued) {
      return Promise.resolve(queued);
    }
    return new Promise((resolvePromise, rejectPromise) => {
      const timer = setTimeout(() => {
        const list = probeWaiters.get(type) ?? [];
        const index = list.findIndex((w) => w.resolve === resolvePromise);
        if (index >= 0) {
          list.splice(index, 1);
        }
        const seen = probeMessages.map((m) => m.type).join(', ') || '(none)';
        rejectPromise(new Error(
          `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (probe: ${context}); probe messages seen so far: ${seen}`));
      }, MESSAGE_TIMEOUT_MS);
      if (!probeWaiters.has(type)) {
        probeWaiters.set(type, []);
      }
      probeWaiters.get(type).push({ resolve: resolvePromise, timer });
    });
  }

  const page = await browser.newPage();
  page.on('crash', () => {
    console.error(
      `FAILED page-crash: probe renderer crashed; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  // Same belt-and-braces folding as the main page: an UNCAUGHT page error on
  // the premature graphUpdate would surface here as source 'playwright:pageerror'
  // and fail the source assert below - the report must come from the handler.
  page.on('pageerror', (err) => onProbeMessage(JSON.stringify(
    { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));
  await page.exposeFunction('__bridgeSendShim', onProbeMessage);

  await page.goto(pathToFileURL(indexHtml).href);
  await awaitProbeMessage('ready', 'probe bundle startup after goto');

  // One minimal chunk, then the update verb - and NO graphCommit, ever.
  await page.evaluate((cmd) => window.bridge.dispatch(cmd), {
    type: 'graphChunk',
    nodes: [{ id: 'CN=Probe,OU=NoCommit,DC=groupweaver,DC=invalid', label: 'Probe', kind: 'User', x: 0, y: 0 }],
    edges: [],
  });
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));

  const jsError = await awaitProbeMessage(
    'jsError', 'graphUpdate dispatched before any graphCommit (ADR-005 D1)');
  assert(jsError.source === 'handler:graphUpdate',
    `the premature graphUpdate must be reported by the graphUpdate handler itself: source '${jsError.source}', message '${jsError.message}'`);

  // Liveness: the page survived and the bridge still dispatches - a primitive
  // out of evaluate; this round-trip also orders AFTER any further sends the
  // handler made synchronously, so the exactly-one count below is honest.
  const bridgeAlive = await page.evaluate(() => typeof window.bridge.dispatch === 'function');
  assert(bridgeAlive === true,
    'page must survive the premature graphUpdate with a working bridge (no crash)');

  const probeJsErrors = probeMessages.filter((m) => m.type === 'jsError');
  assert(probeJsErrors.length === 1,
    `the premature graphUpdate must produce exactly ONE jsError, got ${probeJsErrors.length}: ${JSON.stringify(probeJsErrors)}`);

  await page.close();
}

// ---------------------------------------------------------------------------
// Fresh-page DIFF tripwire (AP 66, ADR-015 Slice 5): the graph-layer half of
// Gap analysis. A HAND-BUILT tiny gap dataset (no demo-fixture floors apply)
// covering every diff status proves graph.js paints the wire `diff` tokens onto
// the underlay-* (nodes) and line-* (edges) channels DISJOINT from kind /
// root/External / severity. Modeled on probeGraphUpdateBeforeCommit: its OWN
// page/context/channel so its accounting is independent of the main run's
// zero-jsError audit (and it must itself be jsError-free). Harness morals hold:
// every wait is a bridge-message promise (MESSAGE_TIMEOUT_MS), only primitives
// leave page.evaluate (never a cytoscape collection - braces matter), the bare
// protocol awaits (newPage/exposeFunction/goto/evaluate/screenshot/close) fall
// under the global watchdog (see the boundedness inventory above), no sleeps.
// ---------------------------------------------------------------------------
async function diffRenderTripwire(browser, indexHtml, screenshotDir) {
  const diffMessages = [];
  const diffPending = new Map();
  const diffWaiters = new Map();

  function onDiffMessage(text) {
    const msg = JSON.parse(text);
    diffMessages.push(msg);
    const waiter = diffWaiters.get(msg.type)?.shift();
    if (waiter) {
      clearTimeout(waiter.timer);
      waiter.resolve(msg);
      return;
    }
    if (!diffPending.has(msg.type)) {
      diffPending.set(msg.type, []);
    }
    diffPending.get(msg.type).push(msg);
  }

  function awaitDiffMessage(type, context) {
    const queued = diffPending.get(type)?.shift();
    if (queued) {
      return Promise.resolve(queued);
    }
    return new Promise((resolvePromise, rejectPromise) => {
      const timer = setTimeout(() => {
        const list = diffWaiters.get(type) ?? [];
        const index = list.findIndex((w) => w.resolve === resolvePromise);
        if (index >= 0) {
          list.splice(index, 1);
        }
        const seen = diffMessages.map((m) => m.type).join(', ') || '(none)';
        rejectPromise(new Error(
          `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (diff: ${context}); diff messages seen so far: ${seen}`));
      }, MESSAGE_TIMEOUT_MS);
      if (!diffWaiters.has(type)) {
        diffWaiters.set(type, []);
      }
      diffWaiters.get(type).push({ resolve: resolvePromise, timer });
    });
  }

  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('crash', () => {
    console.error(
      `FAILED page-crash: diff-tripwire renderer crashed; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  // Same belt-and-braces folding as the main page: an uncaught page error here
  // would surface as source 'playwright:pageerror' and trip the zero-jsError
  // assert below - keeping this phase honest about being error-free.
  page.on('pageerror', (err) => onDiffMessage(JSON.stringify(
    { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));
  await page.exposeFunction('__bridgeSendShim', onDiffMessage);

  await page.addInitScript(() => {
    let wrapped;
    Object.defineProperty(window, 'cytoscape', {
      configurable: true,
      get() { return wrapped; },
      set(real) {
        wrapped = function (...args) {
          const instance = real.apply(this, args);
          window.__cy = instance;
          return instance;
        };
        Object.assign(wrapped, real);
      },
    });
  });

  await page.goto(pathToFileURL(indexHtml).href);
  await awaitDiffMessage('ready', 'diff bundle startup after goto');

  // HAND-BUILT gap dataset (comma-containing DNs - getElementById only, never
  // selector strings, ADR-004 D5). One node per diff status + a Common DL with
  // NO diff field (the underlay-opacity-0 control), plus the COEXIST keystone
  // node carrying BOTH diff:'added' AND sev:'error'. Membership edges: one each
  // added/removed/unchecked + one Common no-diff. Tiny on purpose - the >=190
  // node / >=3 chunk floors are the demo-fixture phase's, not this synthetic one.
  const ADDED_NODE = 'CN=GG Added,OU=Diff,DC=groupweaver,DC=invalid';
  const REMOVED_NODE = 'CN=User Removed,OU=Diff,DC=groupweaver,DC=invalid';
  const COMMON_NODE = 'CN=DL Common,OU=Diff,DC=groupweaver,DC=invalid';
  const UNCHECKED_NODE = 'CN=GG Unchecked,OU=Diff,DC=groupweaver,DC=invalid';
  const COEXIST_NODE = 'CN=GG Added And Error,OU=Diff,DC=groupweaver,DC=invalid';
  // COMMON is the shared membership SOURCE; the three diff targets fan out from it
  // as separated rays so no diff edge is drawn over another (a y:0-collinear layout
  // overdrew the solid-green Added edge across the dashed Removed edge - the green
  // bled into the removed line in graph-diff.png; the 160 computed-style asserts
  // are position-agnostic and never saw it). COMMON sits at the origin with the
  // targets to its right at ADDED y:-90 / REMOVED y:0 / UNCHECKED y:+90 (distinct
  // angles), and COEXIST hangs straight below on its own vertical ray. Every pair
  // is >= 90 apart, holding the >= 44 no-overlap spirit.
  const nodes = [
    { id: ADDED_NODE, label: 'GG Added', kind: 'GlobalGroup', x: 200, y: -90, diff: 'added' },
    { id: REMOVED_NODE, label: 'User Removed', kind: 'User', x: 200, y: 0, diff: 'removed' },
    { id: COMMON_NODE, label: 'DL Common', kind: 'DomainLocalGroup', x: 0, y: 0 },
    { id: UNCHECKED_NODE, label: 'GG Unchecked', kind: 'GlobalGroup', x: 200, y: 90, diff: 'unchecked' },
    { id: COEXIST_NODE, label: 'GG Added And Error', kind: 'GlobalGroup', x: 0, y: 160, diff: 'added', sev: 'error' },
  ];
  // One membership edge per diff status + one no-diff Common edge. Member edges
  // so the rel='member' base styling is what the diff line-* rules must override.
  const ADDED_EDGE = 'edge:added';
  const REMOVED_EDGE = 'edge:removed';
  const UNCHECKED_EDGE = 'edge:unchecked';
  const COMMON_EDGE = 'edge:common';
  const edges = [
    { id: ADDED_EDGE, s: COMMON_NODE, t: ADDED_NODE, rel: 'member', diff: 'added' },
    { id: REMOVED_EDGE, s: COMMON_NODE, t: REMOVED_NODE, rel: 'member', diff: 'removed' },
    { id: UNCHECKED_EDGE, s: COMMON_NODE, t: UNCHECKED_NODE, rel: 'member', diff: 'unchecked' },
    { id: COMMON_EDGE, s: COMMON_NODE, t: COEXIST_NODE, rel: 'member' },
  ];

  for (const chunk of toChunks(nodes, edges)) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphCommit' }));
  await awaitDiffMessage('loaded', 'diff dataset graphCommit -> first render');

  // One round-trip pulls the full underlay triple + the kind-fill bg-opacity for
  // a node id; primitives only out of evaluate (CI moral). getElementById keeps
  // comma DNs byte-identical (ADR-004 D5).
  const underlayOf = (id) => page.evaluate((nid) => {
    const el = window.__cy.getElementById(nid);
    return {
      found: el.length === 1,
      color: el.style('underlay-color'),
      opacity: el.style('underlay-opacity'),
      padding: el.style('underlay-padding'),
      bgOpacity: el.style('background-opacity'),
    };
  }, id);
  const lineOf = (id) => page.evaluate((eid) => {
    const el = window.__cy.getElementById(eid);
    return {
      found: el.length === 1,
      color: el.style('line-color'),
      style: el.style('line-style'),
      opacity: el.style('opacity'),
    };
  }, id);

  // --- NODE underlay parity, one per diff status -----------------------------
  const NODE_PINS = { added: ADDED_NODE, removed: REMOVED_NODE, unchecked: UNCHECKED_NODE };
  for (const [status, dn] of Object.entries(NODE_PINS)) {
    const want = DIFF_UNDERLAY[status];
    const got = await underlayOf(dn);
    assert(got.found, `diff ${status} node '${dn}' not found on the rendered graph`);
    assert(toHex(got.color) === DIFF[status].toUpperCase(),
      `underlay-color for diff '${status}' ('${dn}'): rendered '${got.color}' (${toHex(got.color)}) != pinned ${DIFF[status]} (graph.js missing node[diff='${status}'] underlay rule?)`);
    assert(Math.abs(toNumber(got.opacity) - want.opacity) < 1e-6,
      `underlay-opacity for diff '${status}' ('${dn}'): rendered ${got.opacity} != pinned ${want.opacity} (no diff rule => default 0?)`);
    assert(Math.abs(toNumber(got.padding) - want.padding) < 1e-6,
      `underlay-padding for diff '${status}' ('${dn}'): rendered ${got.padding} != pinned ${want.padding}`);
    if (want.bgOpacity !== null) {
      assert(Math.abs(toNumber(got.bgOpacity) - want.bgOpacity) < 1e-6,
        `background-opacity for diff '${status}' ('${dn}') must FADE the kind fill (colorblind brightness channel): rendered ${got.bgOpacity} != pinned ${want.bgOpacity}`);
    }
  }
  phase('diff: node underlay parity (added/removed/unchecked per pinned DN)');

  // --- Common (no diff field) control: underlay-opacity 0 --------------------
  const commonUnderlay = await underlayOf(COMMON_NODE);
  assert(commonUnderlay.found, `diff Common control node '${COMMON_NODE}' not found`);
  assert(Math.abs(toNumber(commonUnderlay.opacity)) < 1e-6,
    `Common node '${COMMON_NODE}' carries NO diff field => no underlay rule => must render underlay-opacity 0 (byte-identical to today), got ${commonUnderlay.opacity}`);
  phase(`diff: Common control underlay-opacity 0 ('${COMMON_NODE}')`);

  // --- EDGE line parity, one per diff status ---------------------------------
  const EDGE_PINS = { added: ADDED_EDGE, removed: REMOVED_EDGE, unchecked: UNCHECKED_EDGE };
  for (const [status, eid] of Object.entries(EDGE_PINS)) {
    const want = DIFF_LINE[status];
    const got = await lineOf(eid);
    assert(got.found, `diff ${status} edge '${eid}' not found on the rendered graph`);
    assert(toHex(got.color) === DIFF[status].toUpperCase(),
      `line-color for diff '${status}' edge ('${eid}'): rendered '${got.color}' (${toHex(got.color)}) != pinned ${DIFF[status]} (graph.js missing edge[diff='${status}'] line rule?)`);
    assert(got.style === want.style,
      `line-style (colorblind-redundant channel) for diff '${status}' edge ('${eid}'): rendered '${got.style}' != pinned '${want.style}'`);
  }
  phase('diff: edge line parity (added solid / removed dashed / unchecked dotted)');

  // --- COEXIST keystone: diff underlay + severity overlay, no collision ------
  // The added+error node must render its diff underlay (#2FAE4E) AND its severity
  // error overlay (#D13438 / opacity 0.45) simultaneously - the two channels are
  // independent by construction, so a finding that is also a diff status shows
  // BOTH cues. This is the no-collision pin the whole disjoint-channel design buys.
  const coexistUnderlay = await underlayOf(COEXIST_NODE);
  assert(coexistUnderlay.found, `COEXIST node '${COEXIST_NODE}' not found on the rendered graph`);
  assert(toHex(coexistUnderlay.color) === DIFF.added.toUpperCase()
    && Math.abs(toNumber(coexistUnderlay.opacity) - DIFF_UNDERLAY.added.opacity) < 1e-6,
    `COEXIST node '${COEXIST_NODE}' must keep its diff='added' underlay (#2FAE4E / opacity ${DIFF_UNDERLAY.added.opacity}): rendered '${coexistUnderlay.color}' (${toHex(coexistUnderlay.color)}) / opacity ${coexistUnderlay.opacity}`);
  const coexistOverlay = await overlayOf(page, COEXIST_NODE);
  assert(coexistOverlay.found, `COEXIST node '${COEXIST_NODE}' not found for overlay read`);
  assert(toHex(coexistOverlay.color) === SEVERITY.error.toUpperCase()
    && Math.abs(toNumber(coexistOverlay.opacity) - SEVERITY_OVERLAY.error.opacity) < 1e-6,
    `COEXIST node '${COEXIST_NODE}' must ALSO show its sev='error' overlay (${SEVERITY.error} / opacity ${SEVERITY_OVERLAY.error.opacity}) independently of the diff underlay: rendered overlay '${coexistOverlay.color}' (${toHex(coexistOverlay.color)}) / opacity ${coexistOverlay.opacity}`);
  phase(`diff: COEXIST keystone (added underlay + error overlay, no collision) ('${COEXIST_NODE}')`);

  // --- screenshot: the frame the ui-verifier judges -------------------------
  await page.screenshot({ path: join(screenshotDir, 'graph-diff.png') });
  phase('diff screenshot');

  // --- this phase must itself be jsError-free (own channel) ------------------
  const diffJsErrors = diffMessages.filter((m) => m.type === 'jsError');
  assert(diffJsErrors.length === 0,
    `diff tripwire must be jsError-free on its own channel: ${JSON.stringify(diffJsErrors, null, 2)}`);

  await page.close();
}

// ---------------------------------------------------------------------------
// Fresh-page REDUCED-MOTION probe (ADR-017 D5): with
// prefers-reduced-motion:reduce, BOTH motions degrade to the instant pre-slice
// paths - focus is a synchronous cy.fit + cy.one('render', confirmFocus) (NO
// cy.animate), and update adds new nodes at full opacity (NO opacity tween). Its
// OWN page/context/channel, and crucially its OWN emulateMedia override set
// BEFORE goto so the reduce setting NEVER leaks into the animated main run (a
// shared page would). Modeled on probeGraphUpdateBeforeCommit / diffRenderTripwire:
// every wait is a bridge-message promise (MESSAGE_TIMEOUT_MS), primitives only
// out of evaluate, the bare protocol awaits fall under the global watchdog, no
// sleeps. Reuses the SAME motion recorders (installMotionRecordersSource) so a
// regression that animates under reduce is caught on the same counters.
async function reducedMotionProbe(browser, indexHtml, fixture) {
  const probeMessages = [];
  const probePending = new Map();
  const probeWaiters = new Map();

  function onProbeMessage(text) {
    const msg = JSON.parse(text);
    probeMessages.push(msg);
    const waiter = probeWaiters.get(msg.type)?.shift();
    if (waiter) {
      clearTimeout(waiter.timer);
      waiter.resolve(msg);
      return;
    }
    if (!probePending.has(msg.type)) {
      probePending.set(msg.type, []);
    }
    probePending.get(msg.type).push(msg);
  }

  function awaitProbeMessage(type, context) {
    const queued = probePending.get(type)?.shift();
    if (queued) {
      return Promise.resolve(queued);
    }
    return new Promise((resolvePromise, rejectPromise) => {
      const timer = setTimeout(() => {
        const list = probeWaiters.get(type) ?? [];
        const index = list.findIndex((w) => w.resolve === resolvePromise);
        if (index >= 0) {
          list.splice(index, 1);
        }
        const seen = probeMessages.map((m) => m.type).join(', ') || '(none)';
        rejectPromise(new Error(
          `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (reduced-motion: ${context}); probe messages seen so far: ${seen}`));
      }, MESSAGE_TIMEOUT_MS);
      if (!probeWaiters.has(type)) {
        probeWaiters.set(type, []);
      }
      probeWaiters.get(type).push({ resolve: resolvePromise, timer });
    });
  }

  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('crash', () => {
    console.error(
      `FAILED page-crash: reduced-motion probe renderer crashed; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  page.on('pageerror', (err) => onProbeMessage(JSON.stringify(
    { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));
  await page.exposeFunction('__bridgeSendShim', onProbeMessage);

  // emulateMedia BEFORE goto: graph.js reads matchMedia('(prefers-reduced-motion:
  // reduce)').matches ONCE at IIFE init (ADR-017 D5), so the override must be in
  // place before the bundle script runs. Own page => never leaks into the main run.
  await page.emulateMedia({ reducedMotion: 'reduce' });

  // Same recorders as the main run, on this page's own context.
  await page.addInitScript(installMotionRecordersSource);
  await page.addInitScript(() => {
    let wrapped;
    Object.defineProperty(window, 'cytoscape', {
      configurable: true,
      get() { return wrapped; },
      set(real) {
        wrapped = function (...args) {
          const instance = real.apply(this, args);
          window.__cy = instance;
          window.installMotionRecorders(instance);
          return instance;
        };
        Object.assign(wrapped, real);
      },
    });
  });

  await page.goto(pathToFileURL(indexHtml).href);
  await awaitProbeMessage('ready', 'reduced-motion bundle startup after goto');

  // Feed the real demo fixture + commit (instant init path is shared with the
  // animated run; the reduce branch only touches focus + update).
  for (const chunk of toChunks(fixture.nodes, fixture.edges)) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphCommit' }));
  await awaitProbeMessage('loaded', 'reduced-motion graphCommit -> first render');

  // --- focus under reduce: synchronous fit, NO camera animate ---------------
  const rootNode = fixture.nodes.find((n) => n.root === true);
  const ring1 = fixture.edges
    .filter((e) => e.rel === 'contains' && e.s === rootNode.id)
    .map((e) => e.t);
  const focusIds = [rootNode.id, ...ring1];
  await page.evaluate(() => { window.__gwAnimateCalls = 0; });
  await page.evaluate((ids) => window.bridge.dispatch({ type: 'focus', ids }), focusIds);
  await awaitProbeMessage('focused', 'reduced-motion focus (instant fit path)');

  // On the FIRST 'focused', the viewport is already the fit target (instant
  // path), and NO camera animate fired. Reference fit computed non-destructively.
  const focusCmp = await page.evaluate((nids) => {
    const cy = window.__cy;
    let col = cy.collection();
    for (let i = 0; i < nids.length; i++) {
      col = col.union(cy.getElementById(nids[i]));
    }
    const endPan = cy.pan();
    const end = { zoom: cy.zoom(), panX: endPan.x, panY: endPan.y };
    cy.fit(col, 80);
    const refPan = cy.pan();
    const ref = { zoom: cy.zoom(), panX: refPan.x, panY: refPan.y };
    cy.zoom(end.zoom);
    cy.pan({ x: end.panX, y: end.panY });
    return { animateCalls: window.__gwAnimateCalls, end, ref };
  }, focusIds);
  assert(focusCmp.animateCalls === 0,
    `reduced-motion: focus must take the INSTANT cy.fit path, NO cy.animate - __gwAnimateCalls ${focusCmp.animateCalls} != 0`);
  assert(Math.abs(focusCmp.end.zoom - focusCmp.ref.zoom) < 1e-3
    && Math.abs(focusCmp.end.panX - focusCmp.ref.panX) < 0.5
    && Math.abs(focusCmp.end.panY - focusCmp.ref.panY) < 0.5,
    `reduced-motion: end-viewport must already equal the fit target on first 'focused' - `
    + `end (zoom ${focusCmp.end.zoom}, pan ${focusCmp.end.panX}/${focusCmp.end.panY}) `
    + `!= fit (zoom ${focusCmp.ref.zoom}, pan ${focusCmp.ref.panX}/${focusCmp.ref.panY})`);
  phase('reduced-motion: focus is instant fit (no camera animate)');

  // --- update under reduce: full-opacity add, NO enter tween ----------------
  const maxAbs = fixture.nodes.reduce(
    (acc, x) => Math.max(acc, Math.abs(x.x), Math.abs(x.y)), 0);
  const newNode = {
    id: 'CN=Reduced Probe,OU=LazyExpand,DC=groupweaver,DC=invalid',
    label: 'Reduced Probe',
    kind: 'User',
    x: maxAbs + 191.4375,
    y: -(maxAbs + 157.8125),
  };
  const updatedNodes = [...fixture.nodes, newNode];
  await page.evaluate(() => { window.__gwEnterAnims = []; window.__gwAnimateCalls = 0; });
  for (const chunk of toChunks(updatedNodes, fixture.edges)) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
  await awaitProbeMessage('loaded', 'reduced-motion graphUpdate (instant add path)');

  const updateState = await page.evaluate((id) => ({
    enterCount: (window.__gwEnterAnims || []).length,
    animateCalls: window.__gwAnimateCalls,
    newOpacity: Number(window.__cy.getElementById(id).style('opacity')),
  }), newNode.id);
  assert(updateState.enterCount === 0,
    `reduced-motion: graphUpdate must add new nodes at full opacity, NO enter tween - __gwEnterAnims has ${updateState.enterCount} entries`);
  assert(updateState.animateCalls === 0,
    `reduced-motion: graphUpdate must not animate the camera either - __gwAnimateCalls ${updateState.animateCalls} != 0`);
  assert(Math.abs(updateState.newOpacity - 1) < 1e-6,
    `reduced-motion: new node '${newNode.id}' must be at opacity 1 on the FIRST 'loaded' (no fade), got ${updateState.newOpacity}`);
  phase('reduced-motion: update is instant full-opacity add (no enter tween)');

  // This phase is itself jsError-free on its own channel.
  const probeJsErrors = probeMessages.filter((m) => m.type === 'jsError');
  assert(probeJsErrors.length === 0,
    `reduced-motion probe must be jsError-free on its own channel: ${JSON.stringify(probeJsErrors, null, 2)}`);

  await page.close();
}

async function main() {
  const fixturePath = process.argv[2];
  const screenshotDir = process.argv[3];
  if (!fixturePath || !screenshotDir) {
    console.error('usage: node verify.mjs <demo-graph.json> <screenshot-dir>');
    process.exit(2);
  }

  const fixture = JSON.parse(readFileSync(fixturePath, 'utf8'));
  assert(Array.isArray(fixture.nodes) && Array.isArray(fixture.edges),
    `fixture ${fixturePath} must be the flat {nodes,edges} --dump-graph document`);
  mkdirSync(screenshotDir, { recursive: true });

  // Resolve the bundle from this script's own location - repo-relative, no
  // hardcoded absolute paths: tests/graph-bundle -> src/App/web/index.html.
  const here = dirname(fileURLToPath(import.meta.url));
  const indexHtml = resolve(here, '..', '..', 'src', 'App', 'web', 'index.html');

  // --disable-gpu: the product is Canvas-2D-only (ADR-001 guardrail 1) and
  // software rendering is the documented target-audience floor, so this
  // changes nothing we claim - and it removes the GPU-process crash surface on
  // software-rendering CI runners (windows-latest has no GPU).
  const browser = await chromium.launch({ args: ['--disable-gpu'] });
  // A dead browser/renderer leaves every pending protocol call (page.evaluate
  // above all) hanging forever in Playwright - exactly the 3 h CI hang. Fail
  // fast and loudly instead of waiting for the watchdog.
  let expectedShutdown = false;
  browser.on('disconnected', () => {
    if (expectedShutdown) {
      return;
    }
    console.error(
      `FAILED browser-disconnected: Chromium exited mid-run; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    page.on('crash', () => {
      console.error(
        `FAILED page-crash: renderer crashed; last completed phase: ${lastPhase}`);
      process.exit(1);
    });

    // exposeFunction installs the binding BEFORE any page script runs on goto,
    // so bridge.js's rawSend finds window.__bridgeSendShim already on its very
    // first 'ready' send (invokeCSharpAction is absent under Playwright; the
    // shim is bridge.js's documented harness seam).
    await page.exposeFunction('__bridgeSendShim', onBridgeMessage);

    // graph.js keeps its cytoscape instance IIFE-local; intercept the vendor
    // UMD's `window.cytoscape = factory` assignment so every instance the page
    // creates is published as window.__cy - position/style/emit probes work
    // without touching the shipped bundle.
    // ADR-017 motion instrumentation, installed on the SAME page as the
    // __cy publisher so the camera (F2) and enter (F1) tweens are recorded
    // deterministically at CALL time (never a flaky mid-tween read). Two
    // ISOLATED recorders - the camera counter MUST never be bumped by a node
    // enter tween, or assertion #4 (camera does not move on graphUpdate) would
    // false-fail. addInitScript blocks run in order, so this global is defined
    // before the wrapper below calls window.installMotionRecorders().
    await page.addInitScript(installMotionRecordersSource);

    await page.addInitScript(() => {
      let wrapped;
      Object.defineProperty(window, 'cytoscape', {
        configurable: true,
        get() { return wrapped; },
        set(real) {
          wrapped = function (...args) {
            const instance = real.apply(this, args);
            window.__cy = instance;
            window.installMotionRecorders(instance);
            return instance;
          };
          Object.assign(wrapped, real);
        },
      });
    });

    // Belt and braces next to the bundle's own jsError reporting (ADR-004 D6):
    // page-level errors Playwright sees are folded into the same final audit.
    page.on('pageerror', (err) => onBridgeMessage(JSON.stringify(
      { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));

    // --- load the bundle on its production origin (file://, ADR-004 D6) ------
    await page.goto(pathToFileURL(indexHtml).href);
    const ready = await awaitMessage('ready', 'bundle startup after goto - bridge/graph script init');
    assert(typeof ready.userAgent === 'string' && ready.userAgent.length > 0,
      `'ready' must carry a userAgent, got: ${JSON.stringify(ready)}`);
    phase('ready');

    // --- feed the fixture through the chunked accumulator path ---------------
    // Same chunk shape the app sends (GraphChunker greedy fill):
    // {type:'graphChunk', nodes:[...], edges:[...]} then a final graphCommit.
    const chunks = toChunks(fixture.nodes, fixture.edges);
    assert(chunks.length >= 3,
      `accumulator path must be exercised across >= 3 graphChunk dispatches, got ${chunks.length}`);
    for (const chunk of chunks) {
      await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
    }
    await page.evaluate(() => window.bridge.dispatch({ type: 'graphCommit' }));
    phase(`chunks fed (${chunks.length} graphChunk + graphCommit)`);

    // --- loaded: counts ------------------------------------------------------
    const loaded = await awaitMessage('loaded', 'graphCommit -> first cytoscape render');
    assert(loaded.nodeCount === fixture.nodes.length,
      `nodeCount: rendered ${loaded.nodeCount} != fixture ${fixture.nodes.length}`);
    assert(loaded.edgeCount === fixture.edges.length,
      `edgeCount: rendered ${loaded.edgeCount} != fixture ${fixture.edges.length}`);
    assert(loaded.nodeCount >= 190,
      `anti-vacuous floor: demo graph must have >= 190 nodes, got ${loaded.nodeCount}`);
    phase(`loaded (${loaded.nodeCount} nodes, ${loaded.edgeCount} edges)`);

    // --- encoding-key legend signature (#87, ui-checklist "Encoding-key
    // signature") -------------------------------------------------------------
    // The static top-left legend becomes a crafted KEY with four structurally-keyed
    // channels:
    //   KINDS    - 7 rows `#legend [data-kind="<AdObjectKind>"]`, each carrying an
    //              inline real-shape SVG swatch + a `<span class="count">` showing a
    //              LIVE per-kind tally (updateLegendCounts() groups cy.nodes() by
    //              data('kind'), called inside sendLoaded() so it refreshes on both
    //              graphCommit and graphUpdate).
    //   SEVERITY - 3 `#legend [data-sev="error|warning|info"]` (key glyphs, NO counts).
    //   DIFF     - 3 `#legend [data-diff="added|removed|unchecked"]` (key, no counts).
    //   edges    - >= 2 `#legend .edge-sample` (member + contains samples).
    // The whole legend stays `pointer-events:none`, left of viewport-center, fully
    // within the viewport. RED until index.html grows the data-attrs/.count spans and
    // graph.js' sendLoaded() calls updateLegendCounts(). primitives only out of
    // page.evaluate (CI moral): we read the legend `.count` text into a plain map AND
    // independently group window.__cy.nodes() by data('kind') into a tally, both as
    // primitives, and compare in Node.

    // (#1) per-kind legend counts == live cy.nodes() tally, and the 7 counts sum to
    // loaded.nodeCount. ONE round-trip returns both the legend map and the cy tally.
    const legendVsCy = await page.evaluate((kinds) => {
      const legendCount = {};
      for (const k of kinds) {
        const row = document.querySelector(`#legend [data-kind="${k}"]`);
        // null row / null .count text => leave undefined so the Node-side equality
        // (treated as 0) fails loudly against a non-zero cy tally rather than here.
        const countEl = row && row.querySelector('.count');
        legendCount[k] = countEl ? Number(countEl.textContent.trim()) : null;
      }
      const cyTally = {};
      window.__cy.nodes().forEach((node) => {
        const k = node.data('kind');
        cyTally[k] = (cyTally[k] || 0) + 1;
      });
      return { legendCount, cyTally };
    }, KIND_NAMES);
    let legendSum = 0;
    for (const kind of KIND_NAMES) {
      const legendN = legendVsCy.legendCount[kind];
      const cyN = legendVsCy.cyTally[kind] || 0;
      assert(legendN !== null,
        `#87: legend row '#legend [data-kind="${kind}"] .count' is missing (no data-kind row or no .count span) - the encoding-key legend must carry one live-count row per kind`);
      assert(legendN === cyN,
        `#87: per-kind legend count for '${kind}' (${legendN}) must equal the live cy.nodes() tally (${cyN}) - updateLegendCounts() must group cy.nodes() by data('kind')`);
      legendSum += legendN;
    }
    assert(legendSum === loaded.nodeCount,
      `#87: the 7 legend per-kind counts must sum to loaded.nodeCount (${loaded.nodeCount}), got ${legendSum} - a kind row missing or double-counted`);
    phase(`legend: per-kind counts == cy.nodes() tally (sum ${legendSum} == nodeCount ${loaded.nodeCount})`);

    // (#2) 4 channels keyed STRUCTURALLY by querySelectorAll counts. KINDS == 7,
    // edge samples >= 2, SEVERITY == 3, DIFF == 3. No assertion on sev/diff COUNTS:
    // those strips are key-only by design (a count there would contradict the
    // sidebar's authoritative finding tally - ui-checklist #87).
    const channelCounts = await page.evaluate(() => ({
      kinds: document.querySelectorAll('#legend [data-kind]').length,
      edges: document.querySelectorAll('#legend .edge-sample').length,
      sevs: document.querySelectorAll('#legend [data-sev]').length,
      diffs: document.querySelectorAll('#legend [data-diff]').length,
    }));
    assert(channelCounts.kinds === 7,
      `#87: #legend must carry exactly 7 [data-kind] rows (one per AdObjectKind), got ${channelCounts.kinds}`);
    assert(channelCounts.edges >= 2,
      `#87: #legend must carry >= 2 .edge-sample rows (member + contains samples), got ${channelCounts.edges}`);
    assert(channelCounts.sevs === 3,
      `#87: #legend must carry exactly 3 [data-sev] key glyphs (error/warning/info, NO counts), got ${channelCounts.sevs}`);
    assert(channelCounts.diffs === 3,
      `#87: #legend must carry exactly 3 [data-diff] key glyphs (added/removed/unchecked, NO counts), got ${channelCounts.diffs}`);
    phase(`legend: 4 channels keyed (kinds ${channelCounts.kinds}/edges ${channelCounts.edges}/sev ${channelCounts.sevs}/diff ${channelCounts.diffs})`);

    // (#3) airspace: the legend is position:fixed, so getBoundingClientRect is
    // viewport-relative. It must sit top-left-ish: left/top non-negative, RIGHT of
    // its own box strictly LEFT of viewport center (never covering the central
    // cluster - the true invariant), and the WHOLE box within the viewport height
    // (bottom <= innerHeight; NOT bottom < innerHeight/2, which a short window breaks).
    // pointer-events stays 'none' so clicks fall through to the canvas.
    const airspace = await page.evaluate(() => {
      const el = document.getElementById('legend');
      const box = el.getBoundingClientRect();
      return {
        left: box.left, top: box.top, right: box.right, bottom: box.bottom,
        innerWidth: window.innerWidth, innerHeight: window.innerHeight,
        pointerEvents: getComputedStyle(el).pointerEvents,
      };
    });
    assert(airspace.left >= 0 && airspace.top >= 0,
      `#87: legend box must not bleed off the top-left of the viewport (left ${airspace.left}, top ${airspace.top})`);
    assert(airspace.right < airspace.innerWidth / 2,
      `#87: legend must stay LEFT of viewport center (never cover the central cluster): box.right ${airspace.right} >= innerWidth/2 ${airspace.innerWidth / 2}`);
    assert(airspace.bottom <= airspace.innerHeight,
      `#87: legend must stay fully within the viewport height (max-height clamped): box.bottom ${airspace.bottom} > innerHeight ${airspace.innerHeight}`);
    assert(airspace.pointerEvents === 'none',
      `#87: #legend must keep pointer-events:none so taps fall through to the canvas, got '${airspace.pointerEvents}'`);
    phase(`legend: airspace (left-of-center, in-viewport, pointer-events none)`);

    // (#5, optional high-value) swatch parity tripwire: each [data-kind] row's inner
    // SVG real-shape swatch `fill` must toHex-match PALETTE[kind] - making the legend
    // a 4th palette parity tripwire alongside the canvas node fill, the C# converter,
    // and graph.js. Reads the fill of the first <svg> shape element inside each row.
    const swatchFills = await page.evaluate((kinds) => {
      const out = {};
      for (const k of kinds) {
        const row = document.querySelector(`#legend [data-kind="${k}"]`);
        const svg = row && row.querySelector('svg');
        // the painted shape: prefer an explicit shape element, else the svg itself.
        const shape = svg && (svg.querySelector('circle, ellipse, rect, polygon, path') || svg);
        out[k] = shape ? (shape.getAttribute('fill') || getComputedStyle(shape).fill) : null;
      }
      return out;
    }, KIND_NAMES);
    for (const kind of KIND_NAMES) {
      const fill = swatchFills[kind];
      assert(fill !== null && fill !== 'none' && fill !== '',
        `#87: legend [data-kind="${kind}"] must contain an inline SVG real-shape swatch with a fill (the canvas-node mirror), got ${JSON.stringify(fill)}`);
      assert(toHex(fill) === PALETTE[kind].toUpperCase(),
        `#87: legend swatch fill for '${kind}' ('${fill}' -> ${toHex(fill)}) must match PALETTE ${PALETTE[kind]} (4th palette parity tripwire)`);
    }
    phase(`legend: SVG swatch fill parity vs PALETTE (7/7 kinds)`);

    // --- screenshot 1: overview (initGraph already ran cy.fit()) -------------
    await page.screenshot({ path: join(screenshotDir, 'graph-overview.png') });
    phase('overview screenshot');
    // --- the encoding-key legend frame the ui-verifier judges (#87) ----------
    await page.screenshot({ path: join(screenshotDir, 'graph-legend-key.png') });
    phase('legend-key screenshot');

    // --- preset layout honored: 5 sampled DNs at exact fixture positions -----
    const rootNode = fixture.nodes.find((n) => n.root === true);
    assert(rootNode !== undefined, 'fixture must mark a root node (root:true)');
    const n = fixture.nodes.length;
    const candidates = [
      rootNode,
      fixture.nodes.find((x) => x !== rootNode && x.id.includes(',')),
      fixture.nodes[1],
      fixture.nodes[Math.floor(n / 2)],
      fixture.nodes[n - 1],
      fixture.nodes[Math.floor(n / 4)],
      fixture.nodes[Math.floor((3 * n) / 4)],
    ];
    const samples = [...new Map(candidates.filter(Boolean).map((x) => [x.id, x])).values()].slice(0, 5);
    assert(samples.length === 5, `need 5 distinct sample nodes, got ${samples.length}`);
    assert(samples.some((s) => s.id.includes(',')),
      'samples must include at least one comma-containing DN');

    // cy.getElementById ONLY - selector strings silently fail on comma DNs (ADR-004 D5).
    const sampledPositions = await page.evaluate(
      (ids) => ids.map((id) => {
        const p = window.__cy.getElementById(id).position();
        return { x: p.x, y: p.y };
      }),
      samples.map((s) => s.id));
    for (let i = 0; i < samples.length; i++) {
      const expected = samples[i];
      const actual = sampledPositions[i];
      // 1e-9: exact up to double round-trip; preset layout must not move nodes.
      assert(Math.abs(actual.x - expected.x) < 1e-9 && Math.abs(actual.y - expected.y) < 1e-9,
        `preset position not honored for '${expected.id}': rendered (${actual.x}, ${actual.y}) != fixture (${expected.x}, ${expected.y})`);
    }

    // --- no-overlap: O(n^2) min pairwise center distance over ALL nodes ------
    const allPositions = await page.evaluate(
      () => window.__cy.nodes().map((node) => {
        const p = node.position();
        return { id: node.id(), x: p.x, y: p.y };
      }));
    assert(allPositions.length === fixture.nodes.length,
      `position sweep saw ${allPositions.length} nodes, expected ${fixture.nodes.length}`);
    let minDistance = Infinity;
    let closestPair = ['', ''];
    for (let i = 0; i < allPositions.length; i++) {
      for (let j = i + 1; j < allPositions.length; j++) {
        const d = Math.hypot(allPositions[i].x - allPositions[j].x, allPositions[i].y - allPositions[j].y);
        if (d < minDistance) {
          minDistance = d;
          closestPair = [allPositions[i].id, allPositions[j].id];
        }
      }
    }
    assert(minDistance >= MIN_CENTER_DISTANCE,
      `min pairwise center distance ${minDistance.toFixed(2)} < ${MIN_CENTER_DISTANCE} (D=44 no-overlap floor, ADR-004 D3) between '${closestPair[0]}' and '${closestPair[1]}'`);
    phase('geometry (preset positions + no-overlap)');

    // --- palette parity C# <-> JS ---------------------------------------------
    const kindsPresent = new Set(fixture.nodes.map((x) => x.kind));
    for (const kind of kindsPresent) {
      assert(kind in PALETTE, `fixture contains unknown kind '${kind}' - not one of the 7 AdObjectKind names`);
    }
    assert(kindsPresent.size >= 6,
      `demo fixture must exercise >= 6 of the 7 kinds, found ${kindsPresent.size}: ${[...kindsPresent].join(', ')}`);
    for (const [kind, expectedHex] of Object.entries(PALETTE)) {
      const sample = fixture.nodes.find((x) => x.kind === kind);
      if (!sample) {
        continue; // at most one kind may be absent (asserted >= 6 above)
      }
      const renderedColor = await page.evaluate(
        (id) => window.__cy.getElementById(id).style('background-color'), sample.id);
      assert(toHex(renderedColor) === expectedHex.toUpperCase(),
        `palette parity for ${kind} ('${sample.id}'): rendered '${renderedColor}' (${toHex(renderedColor)}) != AdObjectKindConverters ${expectedHex}`);
    }
    phase(`palette parity (${kindsPresent.size}/7 kinds)`);

    // --- severity parity C# <-> JS (AP 3.4, ADR-010 D1/D4) --------------------
    // The S2 fixture (--demo --dump-graph after the severity wire-up) carries the
    // optional sev / below / belowSev fields. Severity owns the cytoscape
    // overlay-* channel ONLY (kind owns fill+shape, root/External own border); a
    // flagged node must show its max-severity glow, an unflagged node must show
    // overlay-opacity 0 (byte-identical to a pre-AP node), and a loaded group
    // hiding flagged descendants must show the wider/fainter roll-up ring keyed
    // off belowSev. RED until graph.js copies sev/below/belowSev into `data` and
    // gains the node[sev=...] / node[below] overlay rules.

    // Anti-vacuous fixture floor: this whole block is meaningless against a
    // pre-S2 dump. Demand the 19-finding baseline shape is actually present.
    const flaggedBySev = { error: [], warning: [], info: [] };
    for (const x of fixture.nodes) {
      if (x.sev) {
        assert(x.sev in SEVERITY,
          `fixture node '${x.id}' has unknown sev '${x.sev}' - not error|warning|info`);
        flaggedBySev[x.sev].push(x);
      }
    }
    for (const sev of Object.keys(SEVERITY)) {
      assert(flaggedBySev[sev].length >= 1,
        `severity fixture floor: --demo --dump-graph must emit >= 1 '${sev}' node (S2 severity wire-up); found 0 - is this a pre-S2 fixture?`);
    }
    const belowNodes = fixture.nodes.filter((x) => x.below);
    assert(belowNodes.length >= 1,
      'severity fixture floor: --demo --dump-graph must emit >= 1 roll-up node (below>0); found 0');
    for (const x of belowNodes) {
      assert(typeof x.below === 'number' && x.below > 0,
        `roll-up node '${x.id}' must carry a positive integer below, got ${JSON.stringify(x.below)}`);
      assert(x.belowSev in SEVERITY,
        `roll-up node '${x.id}' must carry belowSev in {error,warning,info}, got '${x.belowSev}'`);
    }

    // Three pinned per-severity DNs - real flagged demo baseline objects, each
    // sev-only (NO own below, so the node[below] rules don't override the
    // per-sev overlay geometry). Pinned by DN so the assertion names the exact
    // object; cross-checked against the fixture so a fixture drift fails loudly
    // here rather than silently re-anchoring onto a different node.
    const SEV_PINS = {
      error: 'CN=DL_Nested_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example',
      warning: 'CN=dl-finance-extra,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example',
      info: 'CN=DL_App-CRM_RO,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example',
    };
    for (const [sev, dn] of Object.entries(SEV_PINS)) {
      const fx = fixture.nodes.find((x) => x.id === dn);
      assert(fx !== undefined,
        `pinned ${sev} DN '${dn}' is missing from the fixture (demo baseline drift - re-pin against --demo --dump-graph)`);
      assert(fx.sev === sev,
        `pinned ${sev} DN '${dn}' has sev '${fx.sev}' in the fixture, expected '${sev}'`);
      assert(fx.below === undefined,
        `pinned ${sev} DN '${dn}' unexpectedly carries below=${fx.below} - the node[below] ring would override the per-sev overlay; re-pin a sev-only node`);

      const want = SEVERITY_OVERLAY[sev];
      const got = await overlayOf(page, dn);
      assert(got.found, `pinned ${sev} DN '${dn}' not found on the rendered graph`);
      assert(toHex(got.color) === want.color.toUpperCase(),
        `overlay-color for ${sev} ('${dn}'): rendered '${got.color}' (${toHex(got.color)}) != pinned ${want.color}`);
      assert(Math.abs(toNumber(got.opacity) - want.opacity) < 1e-6,
        `overlay-opacity for ${sev} ('${dn}'): rendered ${got.opacity} != pinned ${want.opacity}`);
      assert(Math.abs(toNumber(got.padding) - want.padding) < 1e-6,
        `overlay-padding (colorblind-redundant channel) for ${sev} ('${dn}'): rendered ${got.padding} != pinned ${want.padding}`);
    }
    phase('severity overlay parity (error/warning/info per pinned DN)');

    // Unflagged node: no sev, no below => no overlay rule matches => the cytoscape
    // default overlay-opacity 0 => byte-identical render to a pre-AP node.
    const unflagged = fixture.nodes.find((x) => !x.sev && !x.below && !x.root);
    assert(unflagged !== undefined,
      'fixture must contain an unflagged non-root node (overlay-opacity 0 control)');
    const unflaggedOverlay = await overlayOf(page, unflagged.id);
    assert(unflaggedOverlay.found, `unflagged control node '${unflagged.id}' not found`);
    assert(Math.abs(toNumber(unflaggedOverlay.opacity)) < 1e-6,
      `unflagged node '${unflagged.id}' must render overlay-opacity 0 (no halo), got ${unflaggedOverlay.opacity}`);
    phase(`severity: unflagged control opacity 0 ('${unflagged.id}')`);

    // Roll-up ring cue: a loaded group hiding flagged descendants shows a WIDER
    // (padding 10 > every per-sev padding) and FAINTER (opacity 0.30 < every
    // per-sev opacity) max-severity ring keyed off belowSev. Pinned to a node
    // that is itself UNFLAGGED (no own sev) so the ring color is unambiguously
    // the belowSev color, and the wider-than-per-sev geometry is unambiguous.
    const BELOW_PIN = 'CN=DL_App-CRM_RW,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example';
    const belowFx = fixture.nodes.find((x) => x.id === BELOW_PIN);
    assert(belowFx !== undefined,
      `pinned roll-up DN '${BELOW_PIN}' missing from the fixture (re-pin against --demo --dump-graph)`);
    assert(belowFx.below > 0 && belowFx.sev === undefined,
      `pinned roll-up DN '${BELOW_PIN}' must be below>0 and own-sev-free; got below=${belowFx.below}, sev=${belowFx.belowSev === undefined ? '(none)' : belowFx.sev}`);
    assert(belowFx.belowSev in SEVERITY,
      `pinned roll-up DN '${BELOW_PIN}' must carry belowSev in {error,warning,info}, got '${belowFx.belowSev}'`);
    const belowOverlay = await overlayOf(page, BELOW_PIN);
    assert(belowOverlay.found, `roll-up node '${BELOW_PIN}' not found on the rendered graph`);
    assert(toHex(belowOverlay.color) === SEVERITY[belowFx.belowSev].toUpperCase(),
      `roll-up ring color for '${BELOW_PIN}' (belowSev=${belowFx.belowSev}): rendered '${belowOverlay.color}' (${toHex(belowOverlay.color)}) != ${SEVERITY[belowFx.belowSev]}`);
    assert(Math.abs(toNumber(belowOverlay.opacity) - ROLLUP_OVERLAY.opacity) < 1e-6,
      `roll-up ring for '${BELOW_PIN}' must be FAINTER than a per-sev halo: overlay-opacity rendered ${belowOverlay.opacity} != ${ROLLUP_OVERLAY.opacity}`);
    assert(toNumber(belowOverlay.padding) === ROLLUP_OVERLAY.padding,
      `roll-up ring for '${BELOW_PIN}' must be WIDER than a per-sev halo: overlay-padding rendered ${belowOverlay.padding} != ${ROLLUP_OVERLAY.padding}`);
    assert(ROLLUP_OVERLAY.padding > Math.max(...Object.values(SEVERITY_OVERLAY).map((o) => o.padding))
      && ROLLUP_OVERLAY.opacity < Math.min(...Object.values(SEVERITY_OVERLAY).map((o) => o.opacity)),
      'roll-up geometry invariant broken: padding must exceed and opacity must undercut every per-sev value');
    phase(`severity: roll-up ring (wider/fainter belowSev cue) ('${BELOW_PIN}')`);

    // --- click roundtrip on a comma-containing DN (byte-identical id) --------
    const clickDn = samples.find((s) => s.id.includes(',')).id;
    await page.evaluate((id) => window.bridge.dispatch({ type: 'clickTest', id }), clickDn);
    const click = await awaitMessage('nodeClick', `clickTest dispatch on '${clickDn}'`);
    assert(click.id === clickDn,
      `nodeClick id roundtrip not byte-identical: got '${click.id}', sent '${clickDn}'`);
    phase('click roundtrip');

    // --- ADR-018 (#89): selection + neighborhood dim + hover + selective labels
    // Inserted AFTER the click roundtrip (so the nodeClick-first contract above is
    // already pinned) and BEFORE the dbltap/focus phases: the trailing background-
    // tap clear leaves a clean (no selection, no dim, no hover) state, and the
    // camera is still at the post-graphCommit fit (overview) zoom - the dbltap and
    // focus phases below assume both. Selection is driven via the SAME clickTest
    // path the click roundtrip used (cy.getElementById(id).emit('tap')); a
    // synthetic emit('tap') does NOT run cytoscape's native select, so the
    // implementer's tap handler must call node.select() explicitly inside
    // applySelection - the selected()===true assert is exactly that pin.

    // Build undirected adjacency once from the fixture for the dim-ring asserts.
    const adjacency = buildAdjacency(fixture.nodes, fixture.edges);

    // Clean tap subject: a comma-containing DN that is NOT root / NOT sev-flagged /
    // NOT a roll-up (below) node, with >= 1 neighbor. Non-root/non-flagged so the
    // ONLY border + background-blacken effects on it come from selection/dim (a
    // root node owns its own border; a flagged node's overlay is irrelevant but a
    // clean subject keeps the dim/border asserts unambiguous). Auto-selected from
    // the fixture (never a hard-coded DN) so it survives demo-baseline drift.
    const selectNode = fixture.nodes.find((x) =>
      x.id.includes(',') && !x.root && !x.sev && !x.below && adjacency.get(x.id).size >= 1);
    assert(selectNode !== undefined,
      'fixture must contain a comma-DN, non-root, unflagged node with >= 1 neighbor for the selection phase');
    const selectDn = selectNode.id;
    const neighborSet = adjacency.get(selectDn);
    // Anti-vacuous: the dim ring (un-dim closedNeighborhood, dim everything else)
    // is only meaningful if the subject actually HAS a neighbor and a non-neighbor.
    assert(neighborSet.size >= 1,
      `selection subject '${selectDn}' must have >= 1 neighbor (anti-vacuous dim ring)`);
    const neighborDn = [...neighborSet][0];
    const nonNeighborNode = fixture.nodes.find((x) =>
      x.id !== selectDn && !neighborSet.has(x.id));
    assert(nonNeighborNode !== undefined,
      `selection subject '${selectDn}' must have a non-neighbor node (anti-vacuous dim)`);
    const nonNeighborDn = nonNeighborNode.id;

    // The halo-survives tripwire subject: a sev='error', NON-below (pure per-sev
    // overlay #D13438 / 0.45 / padding 7) node that is ALSO a non-neighbor of the
    // selection (so it IS dimmed). SEV_PINS.error is below-free by an assert above;
    // confirm it is a genuine non-neighbor here so the tripwire is anti-vacuous.
    const errorNonNeighborDn = SEV_PINS.error;
    assert(errorNonNeighborDn !== selectDn && !neighborSet.has(errorNonNeighborDn),
      `severity-error halo-survives pin '${errorNonNeighborDn}' must be a NON-neighbor of the selection '${selectDn}' (anti-vacuous: it must actually be dimmed)`);

    // Reset BOTH #88 isolated motion recorders before the whole select/hover/clear
    // sequence: ADR-018 D2 - selection/dim/hover are INSTANT addClass/removeClass
    // toggles, never cy.animate / collection.animate, so neither counter may move
    // across this entire block. Asserted at the end (item 7).
    await page.evaluate(() => {
      window.__gwAnimateCalls = 0;
      window.__gwAnimateLastDuration = null;
      window.__gwEnterAnims = [];
    });

    // --- select + dim (drive via the clickTest tap path) ----------------------
    await page.evaluate((id) => window.bridge.dispatch({ type: 'clickTest', id }), selectDn);
    // The tap handler still sends nodeClick FIRST (ADR-018 D3, unchanged contract).
    const selClick = await awaitMessage('nodeClick', `clickTest (select) on '${selectDn}'`);
    assert(selClick.id === selectDn,
      `nodeClick must still fire FIRST on tap (ADR-018 D3): got '${selClick.id}', sent '${selectDn}'`);

    const selState = await page.evaluate((a) => {
      const cy = window.__cy;
      const sel = cy.getElementById(a.selectDn);
      const nbr = cy.getElementById(a.neighborDn);
      const non = cy.getElementById(a.nonNeighborDn);
      return {
        selectedCount: cy.nodes(':selected').length,
        selSelected: sel.selected(),
        selBlacken: sel.style('background-blacken'),
        selBorderColor: sel.style('border-color'),
        selBorderWidth: sel.style('border-width'),
        selHasDim: sel.hasClass('gw-dim'),
        nbrBlacken: nbr.style('background-blacken'),
        nbrHasDim: nbr.hasClass('gw-dim'),
        nonBlacken: non.style('background-blacken'),
        nonHasDim: non.hasClass('gw-dim'),
      };
    }, { selectDn, neighborDn, nonNeighborDn });

    // The tapped node is selected (applySelection called node.select() explicitly -
    // synthetic emit('tap') would NOT have) and un-dimmed (background-blacken ~ 0).
    assert(selState.selSelected === true,
      `selection subject '${selectDn}' must report selected()===true after the tap - applySelection must call node.select() explicitly (synthetic emit('tap') never runs native select, ADR-018 D3)`);
    assert(selState.selectedCount === 1,
      `exactly ONE node must be selected after a single tap, got ${selState.selectedCount}`);
    assert(!selState.selHasDim && Math.abs(toNumber(selState.selBlacken)) < 1e-6,
      `selected node '${selectDn}' must be UN-dimmed (closedNeighborhood un-dim): background-blacken rendered ${selState.selBlacken} != ~0, gw-dim=${selState.selHasDim}`);
    // Selection border parity (ADR-018 D1): white #FFFFFF, width 3.
    assert(toHex(selState.selBorderColor) === SELECTION.selBorderColor.toUpperCase(),
      `selected node '${selectDn}' border-color: rendered '${selState.selBorderColor}' (${toHex(selState.selBorderColor)}) != pinned ${SELECTION.selBorderColor} (node:selected rule missing?)`);
    assert(Math.abs(toNumber(selState.selBorderWidth) - SELECTION.selBorderWidth) < 1e-6,
      `selected node '${selectDn}' border-width: rendered ${selState.selBorderWidth} != pinned ${SELECTION.selBorderWidth}`);
    // A 1-hop neighbor is un-dimmed (inside closedNeighborhood).
    assert(!selState.nbrHasDim && Math.abs(toNumber(selState.nbrBlacken)) < 1e-6,
      `1-hop neighbor '${neighborDn}' must be UN-dimmed (closedNeighborhood): background-blacken ${selState.nbrBlacken}, gw-dim=${selState.nbrHasDim}`);
    // A non-neighbor carries .gw-dim with background-blacken === +0.6.
    assert(selState.nonHasDim,
      `non-neighbor '${nonNeighborDn}' must carry the .gw-dim class during a live selection (ADR-018 neighborhood dim)`);
    assert(Math.abs(toNumber(selState.nonBlacken) - SELECTION.dimBlacken) < 1e-6,
      `dimmed non-neighbor '${nonNeighborDn}' background-blacken: rendered ${selState.nonBlacken} != pinned +${SELECTION.dimBlacken} (node.gw-dim rule missing/wrong?)`);
    phase(`selection + neighborhood dim ('${selectDn}': selected + ring un-dimmed, rest +0.6)`);

    // --- HALO SURVIVES DIM (load-bearing): a dimmed sev='error' non-neighbor keeps
    // its FULL-strength severity overlay (#D13438 / 0.45 / padding 7) UNCHANGED.
    // This is the whole reason dim rides background-blacken (kind-fill only) and
    // NEVER element opacity (which per ADR-017 D3 composites the overlay/underlay
    // and would HIDE the halo). The node is dimmed (asserted) yet its overlay is
    // pristine - proving the channels are disjoint.
    const dimmedErr = await page.evaluate((id) => {
      const el = window.__cy.getElementById(id);
      return {
        hasDim: el.hasClass('gw-dim'),
        blacken: el.style('background-blacken'),
        overlayColor: el.style('overlay-color'),
        overlayOpacity: el.style('overlay-opacity'),
        overlayPadding: el.style('overlay-padding'),
      };
    }, errorNonNeighborDn);
    assert(dimmedErr.hasDim && Math.abs(toNumber(dimmedErr.blacken) - SELECTION.dimBlacken) < 1e-6,
      `halo-survives pin '${errorNonNeighborDn}' must itself be dimmed first (gw-dim=${dimmedErr.hasDim}, blacken ${dimmedErr.blacken})`);
    assert(toHex(dimmedErr.overlayColor) === SEVERITY.error.toUpperCase()
      && Math.abs(toNumber(dimmedErr.overlayOpacity) - SEVERITY_OVERLAY.error.opacity) < 1e-6
      && Math.abs(toNumber(dimmedErr.overlayPadding) - SEVERITY_OVERLAY.error.padding) < 1e-6,
      `dim must use background-blacken (kind fill ONLY), never element opacity: the dimmed sev='error' node '${errorNonNeighborDn}' must keep its FULL error halo (${SEVERITY.error} / ${SEVERITY_OVERLAY.error.opacity} / padding ${SEVERITY_OVERLAY.error.padding}) - `
      + `rendered overlay '${dimmedErr.overlayColor}' (${toHex(dimmedErr.overlayColor)}) / opacity ${dimmedErr.overlayOpacity} / padding ${dimmedErr.overlayPadding} (element-opacity dimming would have composited/faded it)`);
    phase(`halo survives dim (dimmed error node keeps full overlay) ('${errorNonNeighborDn}')`);

    // --- background-tap clears selection + dim (also cleanup for downstream) ---
    // evt.target === cy => the core-level background tap handler clears everything.
    await page.evaluate(() => window.__cy.emit('tap', { target: window.__cy }));
    const cleared = await page.evaluate((a) => {
      const cy = window.__cy;
      return {
        selectedCount: cy.nodes(':selected').length,
        anyDim: cy.nodes('.gw-dim').length,
        selBlacken: cy.getElementById(a.selectDn).style('background-blacken'),
        nonBlacken: cy.getElementById(a.nonNeighborDn).style('background-blacken'),
      };
    }, { selectDn, nonNeighborDn });
    assert(cleared.selectedCount === 0,
      `background-tap (evt.target===cy) must clear the selection: ${cleared.selectedCount} node(s) still selected`);
    assert(cleared.anyDim === 0,
      `background-tap must remove ALL .gw-dim classes: ${cleared.anyDim} node(s) still carry it`);
    assert(Math.abs(toNumber(cleared.nonBlacken)) < 1e-6,
      `previously-dimmed non-neighbor '${nonNeighborDn}' must return to background-blacken 0 after clear, got ${cleared.nonBlacken}`);
    phase('background-tap clears selection + dim (clean state restored)');

    // --- hover: mouseover adds gw-hover (brightens), mouseout restores ---------
    const hoverOn = await page.evaluate((id) => {
      const el = window.__cy.getElementById(id);
      el.emit('mouseover');
      return {
        hasHover: el.hasClass('gw-hover'),
        blacken: el.style('background-blacken'),
        borderOpacity: el.style('border-opacity'),
      };
    }, selectDn);
    assert(hoverOn.hasHover,
      `mouseover on '${selectDn}' must add the .gw-hover class (ADR-018 hover cue)`);
    assert(Math.abs(toNumber(hoverOn.blacken) - SELECTION.hoverBlacken) < 1e-6,
      `hovered node '${selectDn}' must BRIGHTEN via background-blacken ${SELECTION.hoverBlacken} (negative; there is NO background-brighten): rendered ${hoverOn.blacken}`);
    assert(Math.abs(toNumber(hoverOn.borderOpacity) - 1) < 1e-6,
      `hovered node '${selectDn}' must carry border-opacity 1 (node.gw-hover rule): rendered ${hoverOn.borderOpacity}`);

    const hoverOff = await page.evaluate((id) => {
      const el = window.__cy.getElementById(id);
      el.emit('mouseout');
      return { hasHover: el.hasClass('gw-hover'), blacken: el.style('background-blacken') };
    }, selectDn);
    assert(!hoverOff.hasHover,
      `mouseout on '${selectDn}' must remove the .gw-hover class`);
    assert(Math.abs(toNumber(hoverOff.blacken)) < 1e-6,
      `un-hovered node '${selectDn}' must restore background-blacken 0, got ${hoverOff.blacken}`);
    phase('hover (mouseover brightens via gw-hover, mouseout restores)');

    // --- hovered-AND-dimmed source order: gw-hover wins over gw-dim ------------
    // Source order is `... node.gw-dim, node.gw-hover, node:selected` (ADR-018 D1):
    // gw-hover AFTER gw-dim, so hovering a DIMMED node brightens it (the shared
    // background-blacken channel resolves last-wins to the hover value -0.15, not
    // the dim +0.6). Re-establish a selection to dim the non-neighbor, then hover it.
    await page.evaluate((id) => window.bridge.dispatch({ type: 'clickTest', id }), selectDn);
    await awaitMessage('nodeClick', `clickTest (re-select for hover-over-dim) on '${selectDn}'`);
    const hoverOverDim = await page.evaluate((id) => {
      const el = window.__cy.getElementById(id);
      const dimmedBefore = el.hasClass('gw-dim');
      el.emit('mouseover');
      return {
        dimmedBefore,
        hasDim: el.hasClass('gw-dim'),
        hasHover: el.hasClass('gw-hover'),
        blacken: el.style('background-blacken'),
      };
    }, nonNeighborDn);
    assert(hoverOverDim.dimmedBefore,
      `hover-over-dim subject '${nonNeighborDn}' must be dimmed by the re-selection before hovering (anti-vacuous)`);
    assert(hoverOverDim.hasDim && hoverOverDim.hasHover,
      `hover-over-dim subject '${nonNeighborDn}' must carry BOTH gw-dim and gw-hover (dim=${hoverOverDim.hasDim}, hover=${hoverOverDim.hasHover})`);
    assert(Math.abs(toNumber(hoverOverDim.blacken) - SELECTION.hoverBlacken) < 1e-6,
      `hover must WIN over dim by source order (gw-hover after gw-dim, ADR-018 D1): effective background-blacken on a hovered+dimmed node must be ${SELECTION.hoverBlacken} (hover), got ${hoverOverDim.blacken} (if +0.6, the gw-hover rule is BEFORE gw-dim in source order)`);
    // Drop the hover, then clear the selection so the label phase starts clean.
    await page.evaluate((id) => { window.__cy.getElementById(id).emit('mouseout'); }, nonNeighborDn);
    await page.evaluate(() => window.__cy.emit('tap', { target: window.__cy }));
    phase('hovered-AND-dimmed source order (gw-hover wins over gw-dim)');

    // --- #88 motion counters UNTOUCHED across the whole select/hover/clear block
    // ADR-018 D2: every cue is an instant class toggle - no cy.animate (camera) and
    // no collection.animate (enter), so both isolated #88 recorders must read 0.
    const motionAfterInteraction = await page.evaluate(() => ({
      animateCalls: window.__gwAnimateCalls,
      enterCount: (window.__gwEnterAnims || []).length,
    }));
    assert(motionAfterInteraction.animateCalls === 0,
      `selection/dim/hover must be INSTANT (ADR-018 D2) - no cy.animate may fire across the block: __gwAnimateCalls ${motionAfterInteraction.animateCalls} != 0 (#88 isolation broken?)`);
    assert(motionAfterInteraction.enterCount === 0,
      `selection/dim/hover must NOT trigger any enter tween (ADR-018 D2): __gwEnterAnims has ${motionAfterInteraction.enterCount} entries != 0`);
    phase('#88 motion counters untouched across the interaction block (instant toggles)');

    // --- F9 selective labels at fit zoom --------------------------------------
    // At the post-graphCommit fit (overview) zoom, ADR-018 D4 keeps the root and
    // every sev='error' node LABELED (min-zoomed-font-size forced to 0 on
    // node[?root] and node[sev='error']) while a plain unflagged node stays hidden
    // (the base node floor 10). Read with NO live selection (cleared above) so the
    // node:selected mzfs-0 force cannot contaminate the plain control. mzfs is a
    // resolved STYLE value (zoom-independent to read), so this pins the rule, not
    // the camera; the screenshot below is the visual proof at the actual fit zoom.
    const plainNode = fixture.nodes.find((x) => !x.sev && !x.below && !x.root);
    assert(plainNode !== undefined,
      'fixture must contain a plain unflagged non-root node (min-zoomed-font-size 10 control)');
    const labels = await page.evaluate((a) => {
      const cy = window.__cy;
      const mzfs = (id) => cy.getElementById(id).style('min-zoomed-font-size');
      return {
        selectedCount: cy.nodes(':selected').length,
        root: mzfs(a.rootDn),
        error: mzfs(a.errorDn),
        plain: mzfs(a.plainDn),
      };
    }, { rootDn: rootNode.id, errorDn: SEV_PINS.error, plainDn: plainNode.id });
    assert(labels.selectedCount === 0,
      `F9 label read must run with NO live selection (the :selected mzfs-0 force would contaminate the control): ${labels.selectedCount} node(s) selected`);
    const rootMzfs = toNumber(labels.root);
    const errorMzfs = toNumber(labels.error);
    const plainMzfs = toNumber(labels.plain);
    assert(Math.abs(rootMzfs - LABEL_MZFS.forced) < 1e-6,
      `F9: root node '${rootNode.id}' must be LABELED at fit (min-zoomed-font-size forced to ${LABEL_MZFS.forced} on node[?root], ADR-018 D4): rendered ${labels.root}`);
    assert(Math.abs(errorMzfs - LABEL_MZFS.forced) < 1e-6,
      `F9: sev='error' node '${SEV_PINS.error}' must be LABELED at fit (min-zoomed-font-size forced to ${LABEL_MZFS.forced} on node[sev='error'], ADR-018 D4): rendered ${labels.error}`);
    assert(Math.abs(plainMzfs - LABEL_MZFS.baseFloor) < 1e-6,
      `F9: plain unflagged node '${plainNode.id}' must stay HIDDEN at fit (base node min-zoomed-font-size floor ${LABEL_MZFS.baseFloor}): rendered ${labels.plain}`);
    // Tripwire: root + Error strictly below the plain floor (an overzealous "label
    // everything" regression - mzfs 0 on the base node rule - would make them equal).
    assert(rootMzfs < plainMzfs && errorMzfs < plainMzfs,
      `F9 selective-label invariant: root (${rootMzfs}) and Error (${errorMzfs}) min-zoomed-font-size must each be STRICTLY below the plain floor (${plainMzfs}) - else labels are not selective`);
    phase('F9 selective labels at fit (root + Error labeled, plain hidden)');

    // --- screenshot: graph-selection.png (the verified select+dim+label frame) -
    // Re-establish the selection so the artifact ui-verifier judges actually shows
    // the live selection border + neighborhood dim + selective labels at fit zoom,
    // then clear so the dbltap/focus phases below start from a clean state.
    await page.evaluate((id) => window.bridge.dispatch({ type: 'clickTest', id }), selectDn);
    await awaitMessage('nodeClick', `clickTest (re-select for screenshot) on '${selectDn}'`);
    await page.screenshot({ path: join(screenshotDir, 'graph-selection.png') });
    await page.evaluate(() => window.__cy.emit('tap', { target: window.__cy }));
    const finalClear = await page.evaluate(() => ({
      selectedCount: window.__cy.nodes(':selected').length,
      anyDim: window.__cy.nodes('.gw-dim').length,
      anyHover: window.__cy.nodes('.gw-hover').length,
    }));
    assert(finalClear.selectedCount === 0 && finalClear.anyDim === 0 && finalClear.anyHover === 0,
      `interaction block must leave a CLEAN state for the downstream dbltap/focus phases: selected=${finalClear.selectedCount}, dim=${finalClear.anyDim}, hover=${finalClear.anyHover}`);
    phase('graph-selection screenshot (select + dim + selective labels, then cleared)');

    // --- expand protocol (dbltap -> nodeExpand, AP 2.3's wire) ----------------
    // Braces matter: emit() returns the cytoscape collection - returning it makes
    // Playwright serialize a huge cyclic object graph (renderer caches included),
    // which wedged the 2-core CI runner (runs 27409858814 / 27419366522).
    await page.evaluate((id) => { window.__cy.getElementById(id).emit('dbltap'); }, clickDn);
    const expand = await awaitMessage('nodeExpand', `dbltap emit on '${clickDn}'`);
    assert(expand.id === clickDn,
      `nodeExpand id roundtrip not byte-identical: got '${expand.id}', sent '${clickDn}'`);
    phase('expand roundtrip (dbltap)');

    // --- screenshot 2: focus on root + its ring-1 containment children -------
    const ring1 = fixture.edges
      .filter((e) => e.rel === 'contains' && e.s === rootNode.id)
      .map((e) => e.t);
    assert(ring1.length > 0, 'demo fixture must have containment children of the root (ring 1)');
    await page.evaluate(() => { window.__gwAnimateCalls = 0; window.__gwAnimateLastDuration = null; });
    await page.evaluate((ids) => window.bridge.dispatch({ type: 'focus', ids }), [rootNode.id, ...ring1]);
    await awaitMessage('focused', `focus on root + ${ring1.length} ring-1 children`);
    // ADR-017 F2: the non-reduced-motion focus path eases the camera via
    // cy.animate({fit:{eles,padding:80}}, {duration:280, easing:'ease-out-cubic',
    // complete:confirmFocus}). The 'focused' bridge message fires from the
    // animation COMPLETE callback (no longer cy.one('render')), so awaiting it is
    // the settle barrier - by here the ease has landed. Assert the camera animate
    // ran (>=1) with a positive duration, and that the eased end-viewport equals
    // the reference cy.fit(col,80) target (right padding/easing endpoint, not a
    // wrong one) within tolerance.
    await assertEasedFocus(page, [rootNode.id, ...ring1], 'root + ring-1');
    await page.screenshot({ path: join(screenshotDir, 'graph-focus.png') });
    phase('focus screenshot');

    // --- screenshot 3: the seeded A<->B membership cycle ----------------------
    // Auto-detect the antiparallel member-edge pair: (s,t) and (t,s) both rel=member.
    // NUL (String.fromCharCode(0)) as the composite-key separator: it cannot
    // appear in a DN, unlike space. Built in code - a literal NUL byte in this
    // file makes it binary for grep/diff tooling.
    const SEP = String.fromCharCode(0);
    const memberKeys = new Set(fixture.edges
      .filter((e) => e.rel === 'member')
      .map((e) => `${e.s}${SEP}${e.t}`));
    const cycleEdge = fixture.edges.find((e) =>
      e.rel === 'member' && e.s !== e.t && memberKeys.has(`${e.t}${SEP}${e.s}`));
    assert(cycleEdge !== undefined,
      'demo fixture must contain an antiparallel membership pair (the seeded A<->B cycle)');
    const cycleIds = [cycleEdge.s, cycleEdge.t];
    await page.evaluate(() => { window.__gwAnimateCalls = 0; window.__gwAnimateLastDuration = null; });
    await page.evaluate((ids) => window.bridge.dispatch({ type: 'focus', ids }), cycleIds);
    await awaitMessage('focused', `focus on cycle pair ${cycleIds.join(' <-> ')}`);
    // ADR-017 F2: the cycle-pair focus eases too (and lands on the fit target).
    await assertEasedFocus(page, cycleIds, 'cycle pair');
    await page.screenshot({ path: join(screenshotDir, 'graph-cycle.png') });
    phase('cycle screenshot');

    // --- ADR-019 (#94): the in-canvas busy ring -----------------------------
    // A live cy exists (post-graphCommit). The {type:'busy'} command toggles a
    // transient `busy` data flag; the node[busy][!sev] rule paints the overlay
    // channel ONLY on a finding-free node (severity > busy). It is STATIC (no
    // tween) and self-clears on the next graphUpdate. Reuses the unflagged control
    // node (no sev/below/root => the overlay channel is free) and the error pin
    // (SEV_PINS.error => the [!sev] gate must keep severity winning). Placed BEFORE
    // the graphUpdate phase so the busy left set below is cleared by it (the
    // transient-clear proof); a {type:'busy'} of an unknown command would also trip
    // the zero-jsError audit, so these asserts double as the "case exists" tripwire.
    await page.evaluate(() => {
      window.__gwAnimateCalls = 0;
      window.__gwAnimateLastDuration = null;
      window.__gwEnterAnims = [];
    });

    // busy ON an unflagged node => the #4FA3E3/0.35/8 overlay paints.
    await page.evaluate(
      (id) => window.bridge.dispatch({ type: 'busy', id, on: true }), unflagged.id);
    const busyOn = await overlayOf(page, unflagged.id);
    assert(busyOn.found, `busy control node '${unflagged.id}' not found`);
    assert(toHex(busyOn.color) === BUSY.color.toUpperCase(),
      `busy overlay-color for '${unflagged.id}': rendered '${busyOn.color}' (${toHex(busyOn.color)}) != ${BUSY.color}`);
    assert(Math.abs(toNumber(busyOn.opacity) - BUSY.opacity) < 1e-6,
      `busy overlay-opacity for '${unflagged.id}': rendered ${busyOn.opacity} != ${BUSY.opacity}`);
    assert(Math.abs(toNumber(busyOn.padding) - BUSY.padding) < 1e-6,
      `busy overlay-padding for '${unflagged.id}': rendered ${busyOn.padding} != ${BUSY.padding}`);
    phase(`busy ring paints on unflagged node ('${unflagged.id}')`);

    // busy ON a sev=error node => the [!sev] gate keeps the ERROR halo winning
    // (severity > busy: a finding's halo must never be hidden by a busy ring).
    await page.evaluate(
      (id) => window.bridge.dispatch({ type: 'busy', id, on: true }), SEV_PINS.error);
    const busyOverError = await overlayOf(page, SEV_PINS.error);
    assert(busyOverError.found, `busy-over-error pin '${SEV_PINS.error}' not found`);
    assert(toHex(busyOverError.color) === SEVERITY_OVERLAY.error.color.toUpperCase()
      && Math.abs(toNumber(busyOverError.opacity) - SEVERITY_OVERLAY.error.opacity) < 1e-6
      && Math.abs(toNumber(busyOverError.padding) - SEVERITY_OVERLAY.error.padding) < 1e-6,
      `busy must NOT paint over a finding ([!sev]): error pin '${SEV_PINS.error}' overlay `
      + `rendered '${busyOverError.color}' (${toHex(busyOverError.color)})/${busyOverError.opacity}/${busyOverError.padding} `
      + `!= error halo ${SEVERITY_OVERLAY.error.color}/${SEVERITY_OVERLAY.error.opacity}/${SEVERITY_OVERLAY.error.padding}`);
    phase(`busy does not override severity ('${SEV_PINS.error}' keeps its error halo)`);

    // busy OFF the unflagged node => the overlay channel returns to opacity 0. This
    // pins that removeData('busy') actually triggers a style RECOMPUTE on the live
    // element: cytoscape leaves a selector-set property frozen at its last value when
    // an element merely stops matching a rule, so the off-command must force the
    // recalc (a stuck 0.35 ring on the expand failure/cancel path is the regression
    // this catches — there is no following graphUpdate on those paths to clear it).
    await page.evaluate(
      (id) => window.bridge.dispatch({ type: 'busy', id, on: false }), unflagged.id);
    const busyOff = await overlayOf(page, unflagged.id);
    assert(busyOff.found && Math.abs(toNumber(busyOff.opacity)) < 1e-6,
      `busy OFF must clear the overlay on '${unflagged.id}': overlay-opacity rendered ${busyOff.opacity} != 0`);
    phase(`busy ring clears on off-command ('${unflagged.id}')`);

    // The busy block is STATIC: no camera animate, no enter tween fired across it.
    const busyMotion = await page.evaluate(() => ({
      animateCalls: window.__gwAnimateCalls,
      enterCount: (window.__gwEnterAnims || []).length,
    }));
    assert(busyMotion.animateCalls === 0,
      `busy ring must be STATIC (ADR-019): no cy.animate may fire across the block - __gwAnimateCalls ${busyMotion.animateCalls} != 0`);
    assert(busyMotion.enterCount === 0,
      `busy ring must be STATIC (ADR-019): no enter tween may fire across the block - __gwEnterAnims has ${busyMotion.enterCount} entries != 0`);

    // Leave busy SET on the unflagged node: the graphUpdate phase below
    // (remove-all/add-all) must drop the transient flag - the existing
    // "unflagged stays clear after graphUpdate" assert at ~L1827 is the
    // transient-clear proof.
    await page.evaluate(
      (id) => window.bridge.dispatch({ type: 'busy', id, on: true }), unflagged.id);
    const busyBeforeUpdate = await overlayOf(page, unflagged.id);
    assert(Math.abs(toNumber(busyBeforeUpdate.opacity) - BUSY.opacity) < 1e-6,
      `busy must be SET on '${unflagged.id}' before the graphUpdate transient-clear check, got ${busyBeforeUpdate.opacity}`);
    phase(`busy left set for the graphUpdate transient-clear check ('${unflagged.id}')`);

    // --- graphUpdate: replace-in-place on the LIVE instance (ADR-005 D1) -----
    // AP 2.3 wire pin: a mutated dataset re-fed through the graphChunk
    // accumulator and committed with the NEW verb {type:'graphUpdate'} must be
    // applied to the EXISTING cytoscape instance - no destroy, no fit. The
    // instance identity, the viewport, and the core-bound gesture handlers all
    // survive; confirmation reuses 'loaded' with post-update totals.
    //
    // Pre-state probe: an expando on the live cy instance is the identity
    // marker - the rejected destroy+recreate path publishes a NEW instance to
    // window.__cy which cannot carry it. Recorded AFTER the cycle focus so the
    // viewport numbers are the deliberate non-fit camera state graphUpdate
    // must leave untouched. Primitives only out of evaluate (CI moral).
    const preUpdate = await page.evaluate(() => {
      window.__cy.__gwUpdateMarker = 'live-instance-ap23';
      const pan = window.__cy.pan();
      return { zoom: window.__cy.zoom(), panX: pan.x, panY: pan.y };
    });

    // Mutation = one lazy-expand step (ADR-005 D3/D4): +1 discovered node at a
    // fresh exact preset position, -1 membership edge (SetMembers REPLACE:
    // stale membership edges vanish, both endpoint NODES remain), rest kept.
    // N+1 nodes / E-1 edges also makes the post-update 'loaded' totals
    // distinguishable from a vacuous re-send of the pre-update counts.
    const maxAbs = fixture.nodes.reduce(
      (acc, x) => Math.max(acc, Math.abs(x.x), Math.abs(x.y)), 0);
    const newNode = {
      id: 'CN=Update Probe,OU=LazyExpand,DC=groupweaver,DC=invalid',
      label: 'Update Probe',
      kind: 'User',
      // Fractional, exactly-representable doubles outside every existing ring:
      // the 1e-9 position assert below only bites if the coordinates are not
      // round numbers a layout fallback could coincidentally reproduce.
      x: maxAbs + 217.3125,
      y: -(maxAbs + 133.6875),
    };
    // Drop a membership edge that is NOT part of the seeded antiparallel
    // cycle pair (memberKeys/SEP from the cycle phase above): the A<->B cycle
    // must stay intact - it is the permanent traversal guard.
    const droppedEdge = fixture.edges.find((e) =>
      e.rel === 'member' && !memberKeys.has(`${e.t}${SEP}${e.s}`));
    assert(droppedEdge !== undefined,
      'fixture must contain a non-cycle membership edge for the update phase to drop');
    const updatedNodes = [...fixture.nodes, newNode];
    const updatedEdges = fixture.edges.filter((e) => e !== droppedEdge);

    for (const chunk of toChunks(updatedNodes, updatedEdges)) {
      await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
    }
    // ADR-017 F1+#4: reset BOTH isolated recorders immediately before the
    // graphUpdate so they capture only this phase - the enter recorder must see
    // exactly the genuinely-new node's fade, and the camera counter must stay 0
    // (ADR-005 D1: graphUpdate never moves the camera). Reset together so the
    // window between reset and dispatch contains no other animate call.
    await page.evaluate(() => {
      window.__gwAnimateCalls = 0;
      window.__gwAnimateLastDuration = null;
      window.__gwEnterAnims = [];
    });
    await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
    const updated = await awaitMessage('loaded',
      'graphUpdate commit -> replace-in-place re-render (new verb, ADR-005 D1)');
    assert(updated.nodeCount === updatedNodes.length,
      `post-update nodeCount: rendered ${updated.nodeCount} != mutated fixture ${updatedNodes.length}`);
    assert(updated.edgeCount === updatedEdges.length,
      `post-update edgeCount: rendered ${updated.edgeCount} != mutated fixture ${updatedEdges.length}`);

    const postUpdate = await page.evaluate(() => {
      const pan = window.__cy.pan();
      return {
        markerSurvived: window.__cy.__gwUpdateMarker === 'live-instance-ap23',
        zoom: window.__cy.zoom(),
        panX: pan.x,
        panY: pan.y,
      };
    });
    assert(postUpdate.markerSurvived,
      'cy instance identity lost across graphUpdate - the bundle destroyed/recreated instead of replacing in place (ADR-005 D1)');
    assert(Math.abs(postUpdate.zoom - preUpdate.zoom) < 1e-9
      && Math.abs(postUpdate.panX - preUpdate.panX) < 1e-9
      && Math.abs(postUpdate.panY - preUpdate.panY) < 1e-9,
      `viewport must survive graphUpdate (no fit, ADR-005 D1): zoom/pan (${postUpdate.zoom}, ${postUpdate.panX}, ${postUpdate.panY}) != pre-update (${preUpdate.zoom}, ${preUpdate.panX}, ${preUpdate.panY})`);

    // --- ADR-017 F1: new-node enter fade + #4 camera-untouched ----------------
    // graphUpdate sets opacity 0 on GENUINELY-NEW nodes (incoming id not in the
    // pre-removal live id set) then tweens them 0->1 (240 ms, ease-out-cubic);
    // SURVIVORS get NO tween (replaced instantly, exactly as today). The enter
    // recorder captured each enter tween at call time (fromOpacity = the 0 just
    // set). #4: the camera (core cy.animate) must NOT fire across graphUpdate -
    // ADR-005 D1, the WHOLE reason the two counters are isolated.
    const enter = await page.evaluate((args) => {
      const list = window.__gwEnterAnims || [];
      const find = (id) => list.find((a) => a.id === id);
      return {
        animateCalls: window.__gwAnimateCalls,
        count: list.length,
        newAnim: find(args.newNodeId) || null,
        rootPresent: list.some((a) => a.id === args.rootId),
        errPinPresent: list.some((a) => a.id === args.errPinId),
        newOpacity: Number(window.__cy.getElementById(args.newNodeId).style('opacity')),
      };
    }, { newNodeId: newNode.id, rootId: rootNode.id, errPinId: SEV_PINS.error });

    // #4: camera must not move on graphUpdate (counter is isolated from enter
    // tweens by construction - if a node enter tween bumped this, the isolation
    // is broken and the whole F2-vs-F1 separation is unsound).
    assert(enter.animateCalls === 0,
      `F1/#4: graphUpdate must NOT move the camera (ADR-005 D1) - __gwAnimateCalls ${enter.animateCalls} != 0 `
      + `(enter tween leaking into the CAMERA counter? counters MUST stay isolated)`);

    // F1: the genuinely-new node fades in from opacity 0 with a positive duration.
    assert(enter.newAnim !== null,
      `F1: genuinely-new node '${newNode.id}' must enter via an opacity fade - absent from __gwEnterAnims (${enter.count} recorded)`);
    assert(enter.newAnim.fromOpacity === 0,
      `F1: new node '${newNode.id}' enter tween must start from opacity 0 (graph.js sets .style('opacity',0) first), got fromOpacity ${enter.newAnim.fromOpacity}`);
    assert(typeof enter.newAnim.duration === 'number' && enter.newAnim.duration > 0,
      `F1: new node '${newNode.id}' enter tween must carry a positive duration, got ${JSON.stringify(enter.newAnim.duration)}`);

    // F1: SURVIVORS get NO enter tween - sampled twice (the root node AND the
    // severity-error-pinned survivor, both re-fed in updatedNodes = [...fixture, new]).
    assert(!enter.rootPresent,
      `F1: survivor root node '${rootNode.id}' must NOT be in __gwEnterAnims - survivors replace instantly, no fade (ADR-017 D2)`);
    assert(!enter.errPinPresent,
      `F1: survivor '${SEV_PINS.error}' (severity-error pin) must NOT be in __gwEnterAnims - survivors replace instantly, no fade`);

    // F1: the fade settles to full opacity. The 'loaded' barrier (sendLoaded on
    // the FIRST post-batch render) fires when the enter tween STARTS, not when it
    // settles (ADR-017: 240 ms ease-out-cubic element-opacity 0->1) - so the
    // synchronous `enter.newOpacity` read above is a MID-TWEEN value (~0.645), not
    // the resting state. Poll deterministically (NOT a fixed sleep) until the
    // element opacity has reached its terminal frame (the ease-out-cubic only
    // hits EXACTLY 1 on the final tick - any < 1 value is still tweening), then
    // assert it is exactly 1. The tween is 240 ms; 2 s is ample headroom on the
    // slow CI runner.
    await page.waitForFunction(
      (id) => Math.abs(Number(window.__cy.getElementById(id).style('opacity')) - 1) < 1e-6,
      newNode.id, { timeout: 2000 });
    const settledOpacity = await page.evaluate(
      (id) => Number(window.__cy.getElementById(id).style('opacity')), newNode.id);
    assert(Math.abs(settledOpacity - 1) < 1e-6,
      `F1: new node '${newNode.id}' must settle to opacity 1 after the enter fade, got ${settledOpacity}`);
    phase('F1 enter fade (new node fades 0->1, survivors instant, camera untouched)');

    // cy.getElementById ONLY (ADR-004 D5); position() guarded behind the
    // found-check so a missing node fails the assert, not the evaluate.
    const elements = await page.evaluate((args) => {
      const dropped = window.__cy.getElementById(args.droppedEdgeId);
      const added = window.__cy.getElementById(args.newNodeId);
      const result = { droppedCount: dropped.length, addedCount: added.length, x: null, y: null };
      if (added.length === 1) {
        const p = added.position();
        result.x = p.x;
        result.y = p.y;
      }
      return result;
    }, { droppedEdgeId: droppedEdge.id, newNodeId: newNode.id });
    assert(elements.droppedCount === 0,
      `dropped membership edge '${droppedEdge.id}' (${droppedEdge.s} -> ${droppedEdge.t}) must vanish after graphUpdate; still rendered`);
    assert(elements.addedCount === 1,
      `new node '${newNode.id}' must be rendered after graphUpdate, found ${elements.addedCount} elements`);
    assert(Math.abs(elements.x - newNode.x) < 1e-9 && Math.abs(elements.y - newNode.y) < 1e-9,
      `preset position not honored for post-update node '${newNode.id}': rendered (${elements.x}, ${elements.y}) != mutated fixture (${newNode.x}, ${newNode.y})`);

    // --- severity survives graphUpdate (AP 3.4, ADR-010 D3) ------------------
    // The remove-all/re-add of graphUpdate (ADR-005 D1) wipes all element data;
    // severity rides for free ONLY because it is a re-sent wire field (the VM
    // re-Evaluates before every UpdateGraphAsync). The error pin is part of the
    // re-fed updatedNodes (= [...fixture.nodes, newNode]) and survives, so its
    // sev:"error" overlay must re-attach on the LIVE instance. A flagged node
    // going dark after expand is exactly the regression this pins.
    const survivorDn = SEV_PINS.error;
    const survivorOverlay = await overlayOf(page, survivorDn);
    assert(survivorOverlay.found,
      `severity-survival pin '${survivorDn}' must still be rendered after graphUpdate`);
    assert(toHex(survivorOverlay.color) === SEVERITY.error.toUpperCase()
      && Math.abs(toNumber(survivorOverlay.opacity) - SEVERITY_OVERLAY.error.opacity) < 1e-6
      && Math.abs(toNumber(survivorOverlay.padding) - SEVERITY_OVERLAY.error.padding) < 1e-6,
      `error halo must SURVIVE graphUpdate for '${survivorDn}' (re-sent wire field, ADR-010 D3): `
      + `rendered overlay color '${survivorOverlay.color}' (${toHex(survivorOverlay.color)}) `
      + `opacity ${survivorOverlay.opacity} padding ${survivorOverlay.padding} `
      + `!= error ${SEVERITY.error} / ${SEVERITY_OVERLAY.error.opacity} / ${SEVERITY_OVERLAY.error.padding}`);
    // And an unflagged survivor keeps overlay-opacity 0 across the update.
    const survivorClean = await overlayOf(page, unflagged.id);
    assert(survivorClean.found && Math.abs(toNumber(survivorClean.opacity)) < 1e-6,
      `unflagged node '${unflagged.id}' must keep overlay-opacity 0 after graphUpdate, got ${survivorClean.opacity}`);
    // ADR-019 (#94) transient-clear: the busy phase above left `busy` SET on this
    // unflagged node; the graphUpdate remove-all/add-all must have dropped the flag,
    // so the busy ring is GONE (overlay-opacity back to 0, not BUSY.opacity 0.35).
    assert(Math.abs(toNumber(survivorClean.opacity) - BUSY.opacity) > 1e-6,
      `busy ring must SELF-CLEAR on graphUpdate (#94): '${unflagged.id}' still shows the busy overlay-opacity ${BUSY.opacity} after the update`);
    // ADR-017 #4: the F1 enter fade rides the element OPACITY channel and must
    // NOT bleed into a survivor's overlay/underlay layers. The same unflagged
    // survivor carries no `diff` field => a Common/undiffed survivor whose
    // underlay-opacity must read 0 throughout (the enter tween never touched it).
    // We DELIBERATELY do not sample any new+flagged node here: its own halo
    // legitimately fades in with it (element-opacity composites overlay/underlay,
    // ADR-017 D3), so that would be a transient/flaky read.
    const survivorUnderlayOpacity = await page.evaluate(
      (id) => Number(window.__cy.getElementById(id).style('underlay-opacity')), unflagged.id);
    assert(Math.abs(survivorUnderlayOpacity) < 1e-6,
      `unflagged Common/undiffed survivor '${unflagged.id}' must keep underlay-opacity 0 after graphUpdate (enter fade must not touch survivor underlay, ADR-017 D3), got ${survivorUnderlayOpacity}`);
    phase('severity survives graphUpdate (error halo re-attaches, unflagged stays clear)');

    // Handler survival: dbltap on the NEWLY ADDED node (comma DN) must still
    // round-trip nodeExpand - the delegated handler is bound on the cy core,
    // so it covers post-update elements iff the instance truly survived.
    await page.evaluate((id) => { window.__cy.getElementById(id).emit('dbltap'); }, newNode.id);
    const postExpand = await awaitMessage('nodeExpand', `dbltap emit on post-update node '${newNode.id}'`);
    assert(postExpand.id === newNode.id,
      `post-update nodeExpand id roundtrip not byte-identical: got '${postExpand.id}', sent '${newNode.id}'`);

    // --- screenshot 4: focus on the freshly expanded node ---------------------
    // Deliberate camera move AFTER the viewport-untouched assert; also pins
    // that the focus verb still works against the post-update element set.
    await page.evaluate(() => { window.__gwAnimateCalls = 0; window.__gwAnimateLastDuration = null; });
    await page.evaluate((ids) => window.bridge.dispatch({ type: 'focus', ids }), [newNode.id]);
    await awaitMessage('focused', `focus on post-update node '${newNode.id}'`);
    // ADR-017 F2: focus on the post-update element set eases too and lands on fit.
    await assertEasedFocus(page, [newNode.id], 'post-update node');
    await page.screenshot({ path: join(screenshotDir, 'graph-expanded.png') });
    phase('graphUpdate replace-in-place (instance, viewport, handlers survive)');

    // --- (#4) legend counts REFRESH on lazy-expand (#87 self-correction) ------
    // updateLegendCounts() runs inside sendLoaded(), which fires on BOTH graphCommit
    // AND graphUpdate - so a lazy-expand that re-keys a frontier-External node to its
    // true loaded kind must make the legend's External bucket DECREMENT (the design
    // critique's self-correction path: an unexpanded External resolves and the key
    // re-tallies). We snapshot the post-update legend counts, then dispatch a fresh
    // graphUpdate that re-sends an External-kinded fixture node with a NON-External
    // kind (GlobalGroup), and assert: (a) per-kind legend == live cy tally STILL
    // holds, and (b) the External count strictly DECREASED.
    //
    // The graph at this point is `updatedNodes` (= [...fixture.nodes, newNode]) minus
    // droppedEdge. The fixture carries 2 External nodes (the two ignored builtin
    // member DNs); re-keying one to GlobalGroup is the frontier-resolves-on-expand
    // analogue. We rebuild the payload from `updatedNodes` so the External-count math
    // is exact (newNode is a User, irrelevant to the External bucket).
    const externalFixtureNode = fixture.nodes.find((x) => x.kind === 'External');
    assert(externalFixtureNode !== undefined,
      '#87 (#4): demo fixture must contain >= 1 External node to exercise the frontier-resolve legend self-correction (the two ignored builtin member DNs)');
    const REKEYED_KIND = 'GlobalGroup';

    const legendBefore = await page.evaluate((kinds) => {
      const out = {};
      for (const k of kinds) {
        const row = document.querySelector(`#legend [data-kind="${k}"]`);
        const countEl = row && row.querySelector('.count');
        out[k] = countEl ? Number(countEl.textContent.trim()) : null;
      }
      return out;
    }, KIND_NAMES);
    assert(legendBefore.External !== null && legendBefore.External >= 1,
      `#87 (#4): pre-refresh legend must show >= 1 External (the frontier bucket about to self-correct), got ${JSON.stringify(legendBefore.External)}`);

    // Re-key exactly ONE External node to GlobalGroup; keep every other node + the
    // surviving edges identical. Same node/edge COUNT as the prior graphUpdate, so
    // this phase's `loaded` totals are unchanged - only the per-kind split shifts.
    const rekeyedNodes = updatedNodes.map((x) =>
      x.id === externalFixtureNode.id ? { ...x, kind: REKEYED_KIND } : x);
    for (const chunk of toChunks(rekeyedNodes, updatedEdges)) {
      await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
    }
    await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
    await awaitMessage('loaded', '#87 (#4): re-keyed graphUpdate -> legend self-correction refresh');

    const refreshed = await page.evaluate((kinds) => {
      const legendCount = {};
      for (const k of kinds) {
        const row = document.querySelector(`#legend [data-kind="${k}"]`);
        const countEl = row && row.querySelector('.count');
        legendCount[k] = countEl ? Number(countEl.textContent.trim()) : null;
      }
      const cyTally = {};
      window.__cy.nodes().forEach((node) => {
        const k = node.data('kind');
        cyTally[k] = (cyTally[k] || 0) + 1;
      });
      return { legendCount, cyTally };
    }, KIND_NAMES);
    // (a) per-kind equality STILL holds after the refresh (the key re-tallied).
    for (const kind of KIND_NAMES) {
      const legendN = refreshed.legendCount[kind];
      const cyN = refreshed.cyTally[kind] || 0;
      assert(legendN !== null,
        `#87 (#4): legend row '#legend [data-kind="${kind}"] .count' missing after the refresh graphUpdate`);
      assert(legendN === cyN,
        `#87 (#4): per-kind legend count for '${kind}' (${legendN}) must STILL equal the live cy tally (${cyN}) after a re-keying graphUpdate - sendLoaded() must call updateLegendCounts() on graphUpdate too`);
    }
    // (b) the External bucket strictly DECREMENTED (self-correction proven): the
    // re-keyed External node now tallies under GlobalGroup. This is the design
    // critique's explicit ask - a frontier External resolving to its true kind.
    assert(refreshed.legendCount.External === legendBefore.External - 1,
      `#87 (#4): re-keying an External node to ${REKEYED_KIND} must DECREMENT the live External legend count by 1 (frontier self-correction): before ${legendBefore.External}, after ${refreshed.legendCount.External}`);
    assert(refreshed.legendCount[REKEYED_KIND] === (legendBefore[REKEYED_KIND] ?? 0) + 1,
      `#87 (#4): the re-keyed node must move INTO the ${REKEYED_KIND} bucket (before ${legendBefore[REKEYED_KIND]}, after ${refreshed.legendCount[REKEYED_KIND]})`);
    phase(`legend: counts refresh on lazy-expand (External ${legendBefore.External} -> ${refreshed.legendCount.External}, self-correction)`);

    // --- PNG export round-trip (AP 4.1, ADR-013) -----------------------------
    // Dispatch the new {type:'exportPng'} command and await the {type:'pngExported',
    // data:<base64>, width, height} reply. cy.png({output:'base64'}) returns a BARE
    // base64 string (no data: prefix); decoding it must yield a real PNG, so assert
    // the 8-byte PNG signature 89 50 4E 47 0D 0A 1A 0A. The happy path sends NO
    // jsError, so this MUST sit BEFORE the zero-jsError audit below (F1). Harness
    // morals hold: bridge-message promise (MESSAGE_TIMEOUT_MS), primitives only out
    // of evaluate, watchdog-bounded - no sleeps.
    await page.evaluate(() => window.bridge.dispatch({ type: 'exportPng' }));
    const png = await awaitMessage('pngExported', 'cy.png base64 export round-trip (ADR-013)');
    assert(typeof png.data === 'string',
      `pngExported.data must be a base64 string, got ${typeof png.data}`);
    assert(png.data.length > 100,
      `pngExported.data must be a non-trivial base64 payload, got length ${png.data.length}`);
    const pngBytes = Buffer.from(png.data, 'base64');
    const PNG_MAGIC = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    assert(pngBytes.length >= PNG_MAGIC.length
      && PNG_MAGIC.every((b, i) => pngBytes[i] === b),
      `decoded pngExported.data must start with the PNG signature 89 50 4E 47 0D 0A 1A 0A, got ${[...pngBytes.subarray(0, PNG_MAGIC.length)].map((b) => b.toString(16).padStart(2, '0')).join(' ')}`);
    phase(`png export round-trip (${pngBytes.length} bytes, PNG magic ok)`);

    // --- final audit: ZERO jsError across the whole run (ADR-004 D6) ---------
    const jsErrors = allMessages.filter((m) => m.type === 'jsError');
    assert(jsErrors.length === 0,
      `jsError messages were reported during the run: ${JSON.stringify(jsErrors, null, 2)}`);
    phase('audit (zero jsError)');

    // --- fresh-page probe: graphUpdate before any graphCommit (ADR-005 D1) ---
    // Runs AFTER the audit on a separate page/context/channel - the provoked
    // jsError stays out of the zero-jsError accounting above by construction.
    await probeGraphUpdateBeforeCommit(browser, indexHtml);
    phase('graphUpdate-before-graphCommit probe (fresh page: one handler jsError, no crash)');

    // --- fresh-page DIFF tripwire: gap diff underlay/line channels (ADR-015) -
    // After the main zero-jsError audit, like the graphUpdate probe, on its own
    // page/context/channel - this phase is itself jsError-free (it asserts so),
    // and the hand-built dataset never touches the main run's accounting.
    await diffRenderTripwire(browser, indexHtml, screenshotDir);
    phase('diff render tripwire (fresh page: underlay nodes + line edges + COEXIST keystone)');

    // --- fresh-page REDUCED-MOTION probe (ADR-017 D5) ------------------------
    // After the audit and the other probes, on its OWN page/context/channel with
    // its OWN emulateMedia({reducedMotion:'reduce'}) set before goto - the reduce
    // override must never leak into the animated main run above. Pins that BOTH
    // focus and update degrade to the instant pre-slice paths (no cy.animate, no
    // opacity tween) and reach end-state immediately.
    await reducedMotionProbe(browser, indexHtml, fixture);
    phase('reduced-motion probe (fresh page: instant focus fit + full-opacity add, no tweens)');

    console.log(
      `PASS graph-bundle: ${loaded.nodeCount} nodes, ${loaded.edgeCount} edges `
      + `(post-update ${updated.nodeCount}/${updated.edgeCount}), `
      + `${chunks.length} chunks, ${kindsPresent.size}/7 kinds, `
      + `${flaggedBySev.error.length}/${flaggedBySev.warning.length}/${flaggedBySev.info.length} err/warn/info halos, `
      + `${belowNodes.length} roll-up rings, `
      + `diff underlay/line + COEXIST verified, `
      + `F2 eased focus + F1 enter fade + reduced-motion verified, `
      + `selection + neighborhood dim + hover + selective labels verified (#89), `
      + `busy ring (paint/severity-wins/clear/transient) verified (#94), `
      + `minDist ${minDistance.toFixed(1)}, ${assertCount} asserts, `
      + `6 screenshots -> ${screenshotDir}`);
  } finally {
    expectedShutdown = true;
    await browser.close();
  }
}

try {
  await main();
  process.exit(0);
} catch (err) {
  console.error(err.stack ?? String(err));
  process.exit(1);
}
