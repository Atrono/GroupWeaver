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

// THE WCAG 1.4.11 node-lift tripwire (#90 / ADR-021). The three kind FILLS whose
// graphical-object contrast vs the #1b1f27 page bg falls below the 3:1 floor
// (DL 2.55:1 / UG 2.66:1 / Computer 2.59:1) are LIFTED by a 2px #8A93A3 ring in
// graph.js - the fills themselves stay UNCHANGED (PALETTE above is untouched).
// Hand-copied from BrandTokens.NodeLiftRingHex (#8A93A3, the C# source of truth in
// src/App/Views/BrandTokens.cs); if graph.js or that token drifts, this fails. Only
// these three kinds carry the ring - every other kind has border-width 0 (asserted
// against a control below) EXCEPT External, which owns its OWN distinct dashed
// #B0B6BF border (NOT the lift - so it is never used as the no-border control).
const KIND_BORDER_COLOR = '#8A93A3';
const KIND_BORDER_WIDTH = 2;
const KIND_BORDER = {
  DomainLocalGroup: KIND_BORDER_COLOR,
  UniversalGroup: KIND_BORDER_COLOR,
  Computer: KIND_BORDER_COLOR,
};

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
// node[below] rules sit AFTER node[sev=...] but are gated [!sev] (issue #268 /
// graph-1 fix) so a node's OWN severity always wins the overlay channel over the
// fainter roll-up ring - the roll-up cue paints ONLY on a node with no finding of
// its own - padding 10 (> every per-sev padding), opacity 0.30 (< every per-sev
// opacity), color = SEVERITY[belowSev].
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
// line-style (added keeps solid, removed dashed, unchecked dotted). The `opacity`
// is the DARK diffXxxLineOpacity (graph.js THEME.dark) - pinned so the ADR-029
// fade-OFF-on-diff check can prove a diff edge keeps its OWN diff opacity (never
// the 0.15 overview-fade wash) on the graph-diff frame.
const DIFF_LINE = {
  added: { color: '#2FAE4E', style: 'solid', opacity: 0.95 },
  removed: { color: '#E0503A', style: 'dashed', opacity: 0.85 },
  unchecked: { color: '#8A8F98', style: 'dotted', opacity: 0.5 },
};
// BASE (non-diff) edge styling, F6 edge-legibility change (graph.js
// edge[rel='member'] / edge[rel='contains']). Membership is the PRIMARY directed
// signal: lighter (#8E9BB4 ~5.8:1 on #1b1f27), heavier (1.6), solid, triangle
// arrow. Containment is subordinate scaffolding: darker (#6B788F ~3.65:1),
// thinner (1), dashed, no arrow. The colorblind-redundant (hue-free) channel is
// line-style: member=solid vs contains=dashed - asserted here so a regression to
// a single monochrome edge style fails even for a red-green-blind reader. These
// pin the NON-diff rel rules; an edge carrying a `diff` field is overridden by
// the edge[diff=...] rules pinned in DIFF_LINE above, so the edges read here are
// chosen from the demo fixture precisely because they have NO `diff` field.
const BASE_EDGE = {
  member: { color: '#8E9BB4', width: 1.6, style: 'solid', arrowColor: '#8E9BB4' },
  contains: { color: '#6B788F', width: 1, style: 'dashed' },
};

// THE overview edge-fade tripwire (WP-A #176 / ADR-029). Parity constants
// hand-mirrored from graph.js: at/near the fit zoom EVERY plain (non-diff) edge
// on an EXPLORE graph carries the `gw-edge-faded` class and renders opacity 0.15
// (the `edge.gw-edge-faded { opacity: 0.15 }` rule); zoomed in past
// fitZoom * EDGE_FADE_FACTOR the class is removed and the edge returns to the
// base rel opacity (1). The fade is OFF wholesale on a gap/diff graph
// (isDiffGraph) - the diff frame asserts no edge ever carries the class and the
// diff edges keep their pinned diff opacities. These are behaviour constants
// (theme-invariant, ADR-029 D4), NOT colours - so they are NOT in the
// palette-parity mirror set; the value 1 is graph.js' base edge[rel=...] opacity.
const EDGE_FADE_OPACITY = 0.15;   // graph.js edge.gw-edge-faded opacity
const EDGE_FADE_FACTOR = 1.6;     // graph.js EDGE_FADE_FACTOR (fade band ceiling = fitZoom * this)
const EDGE_FULL_OPACITY = 1;      // graph.js base edge[rel='member'|'contains'] opacity (zoomed-in)

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

// THE C#/JS LIGHT-theme parity tripwire (ADR-026 D5 / WP1b). The light-canvas
// analogue of every block above: hand-copied from graph.js' THEME.light /
// CHROME.light (the JS owner) and BrandTokens.Graph*LightHex (the documented C#
// source). The wire carries ONLY {type:'theme', variant:'dark'|'light'} - no
// token values cross the bridge - so a drift between BrandTokens, graph.js'
// THEME.light, and these constants fails HERE (the lightThemeProbe below pulls
// the COMPUTED cytoscape styles after a live {variant:'light'} restyle and
// compares them to these, then proves {variant:'dark'} restores the byte-
// identical DARK computed styles - the round-trip pin). Every WCAG ratio is in
// ADR-026 D5; the light halos blend < 3:1 by design (read at/above their dark
// counterpart, a known WCAG point under separate review - NOT this harness' call).
const LIGHT_CANVAS = '#F5F6F8';        // canvas / body bg + node label outline
const LIGHT_LABEL_INK = '#1C2127';     // node label color (14.98:1 on the light canvas)
const LIGHT_LABEL_OUTLINE = '#F5F6F8'; // node label text-outline-color (= canvas)
const LIGHT_EDGE = { member: '#5A6473', contains: '#3A424E' }; // F6 lightness channel held
const LIGHT_NODE_LIFT_WIDTH = 0;       // DL/UG/Computer ring DROPPED on light (fills clear 3:1)
const LIGHT_ROOT_BORDER = '#1C2127';
const LIGHT_EXTERNAL_BORDER = '#6B7480';
const LIGHT_SELECTION_BORDER = '#1C2127'; // dark ink; white would vanish on the light canvas
// Severity halos (soft transparent overlay): deepened hues + raised opacities,
// monotonic padding 7/6/5 (the colorblind-redundant geometry channel, UNCHANGED
// across themes). Roll-up ring keyed off belowSev at the fainter 0.50 opacity.
const LIGHT_SEVERITY_OVERLAY = {
  error: { color: '#D63A4A', opacity: 0.70, padding: 7 },
  warning: { color: '#BD7C00', opacity: 0.75, padding: 6 },
  info: { color: '#2F6FE0', opacity: 0.70, padding: 5 },
};
const LIGHT_ROLLUP_OVERLAY = { opacity: 0.50, padding: 10 };
const LIGHT_BUSY = { color: '#2F6FE0', opacity: 0.55, padding: 8 };
// Diff: node underlay (soft, raised opacity + the theme-INVARIANT removed bg-fade
// 0.45) and the near-opaque directed EDGE line (clears ~3:1) with the colorblind-
// redundant solid/dashed/dotted line-style channel (UNCHANGED across themes).
const LIGHT_DIFF_UNDERLAY = {
  added: { color: '#1F9D57', opacity: 0.70, padding: 8, bgOpacity: null },
  removed: { color: '#D63A4A', opacity: 0.70, padding: 8, bgOpacity: 0.45 },
  unchecked: { color: '#5A6473', opacity: 0.50, padding: 6, bgOpacity: null },
};
const LIGHT_DIFF_LINE = {
  added: { color: '#1F9D57', style: 'solid', opacity: 0.95 },
  removed: { color: '#D63A4A', style: 'dashed', opacity: 0.85 },
  unchecked: { color: '#5A6473', style: 'dotted', opacity: 0.60 },
};
// index.html :root chrome custom properties that FLIP to light on a {variant:'light'}
// command (graph.js applyChromeVariant writes CHROME.light onto documentElement).
// Pinned as the light counterpart of the dark :root defaults so a CHROME.light drift
// (or a theme handler that forgets to set a var) fails here. Only the load-bearing,
// hue-carrying vars are pinned (the canvas/chrome surfaces + the severity/diff swatch
// fills that must mirror the themed canvas, ADR-026 D5).
const LIGHT_CHROME_VARS = {
  '--gw-canvas-bg': LIGHT_CANVAS,  // the light canvas bg (#F5F6F8) reaches the body via this var
  '--gw-canvas-grid': 'rgba(0, 0, 0, 0.045)',  // #168 decorative dot-grid tint flips to the light value
  '--gw-chrome-bg': 'rgba(255, 255, 255, 0.92)',
  '--gw-sev-error': '#D63A4A',
  '--gw-sev-warning': '#BD7C00',
  '--gw-sev-info': '#2F6FE0',
  '--gw-diff-added': '#1F9D57',
  '--gw-diff-removed': '#D63A4A',
  '--gw-diff-unchecked': '#5A6473',
  // ADR-027 D4 (WP3): the selection accent ring hue flips to the light brand purple
  // (= THEME.light.accent / BrandTokens.GraphAccentLightHex) — the #gw-accent-ring reads it.
  '--gw-accent': '#6A5CFF',
};

// ADR-027 D4: the DARK selection accent ring hue (= THEME.dark.accent / BrandTokens.GraphAccentHex),
// the :root index.html default + CHROME.dark value. Pinned in the dark round-trip chrome-var block.
const DARK_ACCENT = '#8B7BFF';

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

// ADR-027 D3 (WP3): one round-trip pulling the selection accent-ring DOM state.
// The ring is a SINGLE absolutely-positioned #gw-accent-ring element OUTSIDE
// cytoscape (the canvas renderer has no per-node DOM), shown by applySelection /
// hidden by clearSelection, tracked at the selected node's renderedPosition. Reads
// the `hidden` attribute, the pulse class (gw-accent-pulse — added only when motion
// is allowed), and the rendered left/top/width/height so the caller can prove the
// ring sits OVER the selected node (its center near the node's renderedPosition).
// Pure DOM reads — never a cytoscape mutation.
async function accentRingStateOf(page) {
  return page.evaluate(() => {
    const el = document.getElementById('gw-accent-ring');
    if (el === null) { return { exists: false }; }
    return {
      exists: true,
      hidden: el.hidden,
      hasPulse: el.classList.contains('gw-accent-pulse'),
      left: parseFloat(el.style.left),
      top: parseFloat(el.style.top),
      width: parseFloat(el.style.width),
      height: parseFloat(el.style.height),
    };
  });
}

// ADR-027 D3: the live renderedPosition of a node (the coordinate the accent ring
// must center on). getElementById keeps comma DNs byte-identical (ADR-004 D5).
async function renderedPositionOf(page, id) {
  return page.evaluate((nid) => {
    const p = window.__cy.getElementById(nid).renderedPosition();
    return { x: p.x, y: p.y };
  }, id);
}

// ADR-027 D3: the accent ring is shown AND its center sits on the selected node's
// renderedPosition (within a small tolerance for the half-diameter math). The ring
// is left/top positioned at (pos - diameter/2), so center = left + width/2.
function assertAccentRingOver(ring, pos, label) {
  assert(ring.exists, `${label}: #gw-accent-ring element must exist in index.html`);
  assert(ring.hidden === false,
    `${label}: #gw-accent-ring must be SHOWN (not hidden) over the selected node (ADR-027 D3 applySelection)`);
  assert(Number.isFinite(ring.width) && ring.width > 0 && Number.isFinite(ring.height) && ring.height > 0,
    `${label}: #gw-accent-ring must be sized to the node + glow margin (width ${ring.width}, height ${ring.height})`);
  const cx = ring.left + ring.width / 2;
  const cy = ring.top + ring.height / 2;
  assert(Math.abs(cx - pos.x) < 1.0 && Math.abs(cy - pos.y) < 1.0,
    `${label}: #gw-accent-ring center (${cx}, ${cy}) must track the selected node's renderedPosition (${pos.x}, ${pos.y})`);
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

  // --- WP-A (#176, ADR-029): the overview edge-fade is OFF on a diff graph ----
  // This is a GAP graph (>= 1 diff-tagged edge => isDiffGraph true), so
  // updateEdgeFade is a wholesale NO-OP: the Added/Removed/Unchecked edges ARE the
  // signal, never noise, and must NEVER be faded - even though this dataset renders
  // at its own fit/overview zoom, exactly the zoom that fades an EXPLORE graph. Two
  // pins: (1) NO edge anywhere carries gw-edge-faded (the isDiffGraph guard short-
  // circuited the class toggle), and (2) every diff edge keeps its OWN pinned diff
  // line opacity (DIFF_LINE[*].opacity, the dark diffXxxLineOpacity) - i.e. NOT the
  // 0.15 wash. The COMMON no-diff edge is the keystone: on an explore graph it WOULD
  // fade at this overview zoom, but here isDiffGraph keeps it at full opacity (1).
  const fadedEdgeCount = await page.evaluate(() => window.__cy.edges('.gw-edge-faded').length);
  assert(fadedEdgeCount === 0,
    `ADR-029 D3 fade-off-on-diff: a gap/diff graph (isDiffGraph) must NEVER fade an edge - ${fadedEdgeCount} edge(s) carry gw-edge-faded (updateEdgeFade must be a no-op when cy.edges('[?diff]').nonempty())`);
  for (const [status, eid] of Object.entries(EDGE_PINS)) {
    const got = await lineOf(eid);
    assert(!(await page.evaluate((id) => window.__cy.getElementById(id).hasClass('gw-edge-faded'), eid)),
      `ADR-029 D3: diff '${status}' edge '${eid}' must not carry gw-edge-faded on a diff graph`);
    assert(Math.abs(toNumber(got.opacity) - DIFF_LINE[status].opacity) < 1e-6,
      `ADR-029 D3: diff '${status}' edge '${eid}' must keep its pinned diff line opacity ${DIFF_LINE[status].opacity} (its OWN signal, NOT the 0.15 overview-fade wash), got ${got.opacity}`);
  }
  const commonLine = await lineOf(COMMON_EDGE);
  assert(commonLine.found, `ADR-029 D3: Common no-diff edge '${COMMON_EDGE}' not found`);
  assert(Math.abs(toNumber(commonLine.opacity) - EDGE_FULL_OPACITY) < 1e-6,
    `ADR-029 D3: the Common NO-diff edge '${COMMON_EDGE}' on a diff graph must render full opacity ${EDGE_FULL_OPACITY} (isDiffGraph turns fade OFF wholesale - an explore graph would fade it to ${EDGE_FADE_OPACITY} at this overview zoom), got ${commonLine.opacity}`);
  phase(`ADR-029 D3: overview-fade OFF on diff graph (0 faded edges; diff edges keep own opacity; Common edge full ${EDGE_FULL_OPACITY})`);

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

  // --- ADR-027 D3 (WP3): the accent ring is STATIC under reduced motion ------
  // showAccentRing adds the gw-accent-pulse class ONLY when motion is allowed
  // (reduceMotion===false); under prefers-reduced-motion:reduce the ring is shown
  // but carries NO pulse class (ADR-017 no-animation contract). The CSS pulse is
  // not cy.animate, so __gwAnimateCalls must stay 0 across the select. Drive the
  // select via the {type:'select'} command (instant, addClass/removeClass only).
  const accentSelectNode = fixture.nodes.find((x) =>
    x.id.includes(',') && !x.root && !x.sev && !x.below);
  assert(accentSelectNode !== undefined,
    'reduced-motion: fixture must contain a comma-DN, non-root, unflagged node for the accent-ring static check');
  await page.evaluate(() => { window.__gwAnimateCalls = 0; });
  await page.evaluate((id) => window.bridge.dispatch({ type: 'select', id }), accentSelectNode.id);
  const reducedRing = await page.evaluate(() => {
    const el = document.getElementById('gw-accent-ring');
    return el === null
      ? { exists: false }
      : { exists: true, hidden: el.hidden, hasPulse: el.classList.contains('gw-accent-pulse'), animateCalls: window.__gwAnimateCalls };
  });
  assert(reducedRing.exists && reducedRing.hidden === false,
    `reduced-motion: the accent ring must still be SHOWN on select (only the pulse is suppressed, ADR-027 D3): hidden=${reducedRing.hidden}`);
  assert(reducedRing.hasPulse === false,
    `reduced-motion: the accent ring must be STATIC (NO gw-accent-pulse class) under prefers-reduced-motion:reduce (ADR-017): hasPulse=${reducedRing.hasPulse}`);
  assert(reducedRing.animateCalls === 0,
    `reduced-motion: showing the accent ring must NOT call cy.animate (the CSS pulse is not cytoscape motion): __gwAnimateCalls ${reducedRing.animateCalls} != 0`);
  // Clear the selection so the focus phase below starts from a clean state.
  await page.evaluate(() => window.bridge.dispatch({ type: 'select', id: '' }));
  phase('reduced-motion: accent ring shown but static (no pulse class, no cy.animate)');

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

// ---------------------------------------------------------------------------
// Fresh-page ACCENT-RING DROP probe (ADR-027 D3 / WP3): the selection accent ring
// must HIDE when the tracked node VANISHES on a graphUpdate (lazy-expand replaces
// the element set). graph.js' updateAccentRing reads the live element by id on the
// render handler and hides the ring if it is gone (node.empty()), so a stale ring
// never floats over empty canvas. Its OWN page/context/channel (modeled on
// reducedMotionProbe / diffRenderTripwire) so its accounting is independent of the
// main run's zero-jsError audit (and it must itself be jsError-free). The main
// selection block already pins the show/hide-on-clear cases; this isolates the
// drop-on-graphUpdate case (a graphUpdate that omits a node cannot run mid-selection-
// block without disturbing the downstream phases). Harness morals hold: every wait
// is a bridge-message promise (MESSAGE_TIMEOUT_MS), only primitives leave
// page.evaluate, the bare protocol awaits fall under the global watchdog, no sleeps.
// ---------------------------------------------------------------------------
async function accentRingDropProbe(browser, indexHtml) {
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
          `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (accent-ring-drop: ${context}); probe messages seen so far: ${seen}`));
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
      `FAILED page-crash: accent-ring-drop probe renderer crashed; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  page.on('pageerror', (err) => onProbeMessage(JSON.stringify(
    { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));
  await page.exposeFunction('__bridgeSendShim', onProbeMessage);
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
  await awaitProbeMessage('ready', 'accent-ring-drop bundle startup after goto');

  // Tiny two-node dataset (comma-DNs - getElementById only, ADR-004 D5): the node
  // that will be SELECTED (and tracked by the ring) and a survivor that stays.
  const TRACKED = 'CN=Tracked,OU=AccentDrop,DC=groupweaver,DC=invalid';
  const SURVIVOR = 'CN=Survivor,OU=AccentDrop,DC=groupweaver,DC=invalid';
  const nodes = [
    { id: TRACKED, label: 'Tracked', kind: 'GlobalGroup', x: 0, y: 0 },
    { id: SURVIVOR, label: 'Survivor', kind: 'DomainLocalGroup', x: 160, y: 0 },
  ];
  const edges = [
    { id: 'edge:accent-drop', s: SURVIVOR, t: TRACKED, rel: 'member' },
  ];
  for (const chunk of toChunks(nodes, edges)) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphCommit' }));
  await awaitProbeMessage('loaded', 'accent-ring-drop graphCommit -> first render');

  // Select the tracked node -> the ring is shown over it.
  await page.evaluate((id) => window.bridge.dispatch({ type: 'select', id }), TRACKED);
  const ringShown = await accentRingStateOf(page);
  assert(ringShown.exists && ringShown.hidden === false,
    `accent-ring-drop: ring must be SHOWN over the tracked node before the update, hidden=${ringShown.hidden}`);

  // graphUpdate that DROPS the tracked node (re-feed only the survivor). The render
  // handler's updateAccentRing reads the now-empty tracked node and hides the ring.
  for (const chunk of toChunks([nodes[1]], [])) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
  await awaitProbeMessage('loaded', 'accent-ring-drop graphUpdate (tracked node dropped)');

  const ringAfterDrop = await accentRingStateOf(page);
  assert(ringAfterDrop.exists && ringAfterDrop.hidden === true,
    `accent-ring-drop: the ring must HIDE when the tracked node vanishes on graphUpdate (ADR-027 D3 - no stale ring over empty canvas): hidden=${ringAfterDrop.hidden}`);
  phase('accent-ring-drop: ring hides when the tracked node vanishes on graphUpdate');

  // This phase is itself jsError-free on its own channel.
  const probeJsErrors = probeMessages.filter((m) => m.type === 'jsError');
  assert(probeJsErrors.length === 0,
    `accent-ring-drop probe must be jsError-free on its own channel: ${JSON.stringify(probeJsErrors, null, 2)}`);

  await page.close();
}

// ---------------------------------------------------------------------------
// Fresh-page ISSUES-ONLY ALL-CLEAR probe (WP3b / #142): the "Issues only" toggle
// is INERT when no node carries a finding. graph.js' controlToggleIssues guards on
// anyIssues() (sev||below over cy.nodes()): with zero flagged nodes a click must
// NOT flip issuesOnly (turning ON would hide the whole graph -> blank canvas), and
// syncIssuesButton surfaces the inert "No issues" label with aria-pressed=false.
// The main demo run always has 19 findings (flagged nodes present), so this
// zero-flagged case needs a HAND-BUILT fixture - its OWN page/context/channel
// (modeled on accentRingDropProbe / diffRenderTripwire) so its accounting is
// independent of the main run's zero-jsError audit (and it must itself be
// jsError-free). Harness morals hold: every wait is a bridge-message promise
// (MESSAGE_TIMEOUT_MS), only primitives leave page.evaluate, the bare protocol
// awaits fall under the global watchdog, no sleeps.
// ---------------------------------------------------------------------------
async function issuesAllClearProbe(browser, indexHtml) {
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
          `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (issues-all-clear: ${context}); probe messages seen so far: ${seen}`));
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
      `FAILED page-crash: issues-all-clear probe renderer crashed; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  page.on('pageerror', (err) => onProbeMessage(JSON.stringify(
    { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));
  await page.exposeFunction('__bridgeSendShim', onProbeMessage);
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
  await awaitProbeMessage('ready', 'issues-all-clear bundle startup after goto');

  // Hand-built ZERO-FLAGGED dataset (comma-DNs - getElementById only, ADR-004 D5):
  // not one node carries `sev` or `below`, so nodeHasIssue is false everywhere and
  // anyIssues() returns false -> the toggle is inert. Tiny on purpose (the demo
  // floors are the main phase's).
  const A = 'CN=Clean A,OU=AllClear,DC=groupweaver,DC=invalid';
  const B = 'CN=Clean B,OU=AllClear,DC=groupweaver,DC=invalid';
  const nodes = [
    { id: A, label: 'Clean A', kind: 'GlobalGroup', x: 0, y: 0 },
    { id: B, label: 'Clean B', kind: 'DomainLocalGroup', x: 160, y: 0 },
  ];
  const edges = [
    { id: 'edge:all-clear', s: A, t: B, rel: 'member' },
  ];
  for (const chunk of toChunks(nodes, edges)) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphCommit' }));
  await awaitProbeMessage('loaded', 'issues-all-clear graphCommit -> first render');

  // After the commit, sendLoaded -> syncIssuesButton sees anyIssues()===false and
  // surfaces the inert "No issues" label (aria-pressed false), with every node
  // visible (the filter never engaged).
  const beforeClick = await page.evaluate(() => {
    const btn = document.getElementById('issues-btn');
    const nodes = window.__cy.nodes();
    let allVisible = true;
    nodes.forEach((n) => { if (!n.visible()) { allVisible = false; } });
    const statusEl = document.getElementById('gw-status');
    return {
      present: !!btn,
      ariaPressed: btn ? btn.getAttribute('aria-pressed') : null,
      text: btn ? btn.textContent.trim() : null,
      allVisible,
      nodeCount: nodes.length,
      // ADR-035 D3 (#223): the all-clear branch of syncIssuesButton also announces
      // "No issues" on the aria-live channel, mirroring the visible button label.
      statusPresent: !!statusEl,
      statusRole: statusEl ? statusEl.getAttribute('role') : null,
      statusAriaLive: statusEl ? statusEl.getAttribute('aria-live') : null,
      statusText: statusEl ? statusEl.textContent : null,
    };
  });
  assert(beforeClick.present,
    'WP3b all-clear: #issues-btn must exist in the shipped bundle');
  assert(beforeClick.ariaPressed === 'false' && beforeClick.text === 'No issues',
    `WP3b all-clear: with zero flagged nodes #issues-btn must read "No issues" / aria-pressed=false after load (syncIssuesButton all-clear branch): ${JSON.stringify(beforeClick)}`);
  assert(beforeClick.statusPresent
    && beforeClick.statusRole === 'status' && beforeClick.statusAriaLive === 'polite',
    `ADR-035 D3 all-clear: #gw-status must exist with role=status + aria-live=polite: ${JSON.stringify(beforeClick)}`);
  assert(beforeClick.statusText === 'No issues',
    `ADR-035 D3 all-clear: the all-clear path must announce "No issues" on the #gw-status live region (syncIssuesButton announce), got '${beforeClick.statusText}'`);
  assert(beforeClick.allVisible && beforeClick.nodeCount === nodes.length,
    `WP3b all-clear: every node must be visible before the click (filter never engaged): ${JSON.stringify(beforeClick)}`);

  // Clicking the toggle on a zero-flagged graph is a NO-OP: issuesOnly never flips
  // (the anyIssues() guard short-circuits), every node stays visible, and the label
  // stays "No issues" / aria-pressed=false (never "Issues: on", never a blank canvas).
  await page.click('#issues-btn', { timeout: MESSAGE_TIMEOUT_MS });
  const afterClick = await page.evaluate(() => {
    const btn = document.getElementById('issues-btn');
    const nodes = window.__cy.nodes();
    let allVisible = true;
    let anyHidden = 0;
    nodes.forEach((n) => { if (!n.visible()) { allVisible = false; anyHidden += 1; } });
    return {
      ariaPressed: btn.getAttribute('aria-pressed'),
      text: btn.textContent.trim(),
      allVisible,
      anyHidden,
    };
  });
  assert(afterClick.ariaPressed === 'false' && afterClick.text === 'No issues',
    `WP3b all-clear: clicking #issues-btn with zero findings must STAY inert ("No issues" / aria-pressed=false - the anyIssues() guard blocks the flip, never "Issues: on"): ${JSON.stringify(afterClick)}`);
  assert(afterClick.allVisible && afterClick.anyHidden === 0,
    `WP3b all-clear: a no-op toggle must leave EVERY node visible (never a blank canvas): ${afterClick.anyHidden} hidden`);
  phase('WP3b: issues-only toggle is inert on a zero-flagged fixture ("No issues", all visible)');

  // This phase is itself jsError-free on its own channel.
  const probeJsErrors = probeMessages.filter((m) => m.type === 'jsError');
  assert(probeJsErrors.length === 0,
    `issues-all-clear probe must be jsError-free on its own channel: ${JSON.stringify(probeJsErrors, null, 2)}`);

  await page.close();
}

// ---------------------------------------------------------------------------
// Fresh-page MINIMAP DEGENERATE probe (WP3d / #146): the minimap must STAY hidden
// (no broken/empty thumbnail) on an EMPTY graph (zero nodes -> cy.nodes().empty()
// guard in refreshMinimap -> hideMinimap), and a SMALL (single-node) graph must
// still render a clean thumbnail with no broken img / decode error. The degenerate
// `!(bb.w>0)||!(bb.h>0)` guard exists for a zero-extent bbox, but a live rendered
// node always has a non-zero bbox (its D=44 diameter), so the single-node case
// exercises the small-graph SHOWN path, and the empty case the HIDDEN path. The main
// run always loads the ~200-node demo (minimap shown), so these cases need a hand-
// built fixture -
// its OWN page/context/channel (modeled on issuesAllClearProbe), so its accounting is
// independent of the main run's zero-jsError audit (and it must itself be jsError-
// free). hideMinimap sets #minimap [hidden] AND background-image:none, so a hidden
// minimap never shows a stale/broken img. Harness morals: every wait is a bridge-
// message promise (MESSAGE_TIMEOUT_MS), only primitives leave page.evaluate, the bare
// protocol awaits fall under the global watchdog, no sleeps.
// ---------------------------------------------------------------------------
async function minimapDegenerateProbe(browser, indexHtml) {
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
          `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (minimap-degenerate: ${context}); probe messages seen so far: ${seen}`));
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
      `FAILED page-crash: minimap-degenerate probe renderer crashed; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  page.on('pageerror', (err) => onProbeMessage(JSON.stringify(
    { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));
  await page.exposeFunction('__bridgeSendShim', onProbeMessage);
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
  await awaitProbeMessage('ready', 'minimap-degenerate bundle startup after goto');

  // Helper: read the minimap hidden-state + background-image as primitives.
  const minimapState = () => page.evaluate(() => {
    const mm = document.getElementById('minimap');
    return {
      present: !!mm,
      hidden: mm ? mm.hasAttribute('hidden') : null,
      bgImage: mm ? getComputedStyle(mm).backgroundImage : null,
    };
  });

  // Before any graph: #minimap ships with the [hidden] attribute, so it must be
  // hidden from the very first paint (no thumbnail yet, never a broken img).
  const atStart = await minimapState();
  assert(atStart.present,
    'WP3d minimap-degenerate: #minimap must exist in the shipped bundle');
  assert(atStart.hidden === true,
    `WP3d minimap-degenerate: #minimap must be [hidden] before any graph render (index.html ships it hidden), got hidden=${atStart.hidden}`);

  // (a) SINGLE-NODE graph: a rendered node has a non-zero boundingBox (its diameter
  // D=44), so it is NOT degenerate - refreshMinimap shows the minimap with a valid
  // thumbnail (no broken img, jsError-free). This pins the small-graph render path: a
  // one-node graph still produces a clean data:image/png thumbnail, never a broken img
  // or a decode error. (The TRULY-hidden case - zero rendered extent - is the empty
  // graph in (b); cy.nodes().boundingBox() never collapses to 0 for a live node.)
  const SOLO = 'CN=Solo,OU=Degenerate,DC=groupweaver,DC=invalid';
  for (const chunk of toChunks([{ id: SOLO, label: 'Solo', kind: 'GlobalGroup', x: 0, y: 0 }], [])) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphCommit' }));
  await awaitProbeMessage('loaded', 'minimap-degenerate single-node graphCommit -> first render');
  const solo = await minimapState();
  assert(solo.hidden === false,
    `WP3d minimap-degenerate: a single-node graph has a non-zero bbox (node diameter), so #minimap must be SHOWN with a valid thumbnail (NOT degenerate), got hidden=${solo.hidden}`);
  assert(solo.bgImage && solo.bgImage !== 'none' && /data:image\/png/i.test(solo.bgImage),
    `WP3d minimap-degenerate: a single-node graph must produce a valid data:image/png thumbnail (no broken img, no decode error), got '${(solo.bgImage || '').slice(0, 48)}'`);
  phase('WP3d minimap-degenerate: single-node graph shows a valid thumbnail (small-graph path is clean)');

  // (b) EMPTY graph: graphUpdate to zero nodes -> cy.nodes().empty() -> hideMinimap
  // ([hidden] + background-image:none, no broken/stale img). This is the canonical
  // "minimap stays hidden / no broken img" case.
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
  await awaitProbeMessage('loaded', 'minimap-degenerate empty graphUpdate -> re-render');
  const empty = await minimapState();
  assert(empty.hidden === true,
    `WP3d minimap-degenerate: an EMPTY graph must keep #minimap [hidden] (refreshMinimap's cy.nodes().empty() guard), got hidden=${empty.hidden}`);
  assert(empty.bgImage === 'none',
    `WP3d minimap-degenerate: an empty/hidden minimap must carry NO thumbnail (background-image:none), got '${(empty.bgImage || '').slice(0, 48)}'`);
  phase('WP3d minimap-degenerate: empty graph keeps #minimap hidden + no thumbnail');

  // This phase is itself jsError-free on its own channel (no broken img => no decode
  // error; the degenerate/empty guards run clean).
  const probeJsErrors = probeMessages.filter((m) => m.type === 'jsError');
  assert(probeJsErrors.length === 0,
    `minimap-degenerate probe must be jsError-free on its own channel: ${JSON.stringify(probeJsErrors, null, 2)}`);

  await page.close();
}

// ---------------------------------------------------------------------------
// Fresh-page LIGHT-THEME probe (ADR-026 D5 / WP1b): the graph canvas follows the
// app theme over a {type:'theme', variant:'dark'|'light'} wire command (the wire
// carries ONLY the variant string - no token values cross the bridge; graph.js
// owns the dark+light THEME / CHROME token tables). Proves the LIVE restyle:
//   1. load a hand-built dataset exercising EVERY themeable channel (kind fills,
//      member+contains edges, root, External, the three severities + a roll-up +
//      a busy node, the three diff statuses on nodes AND edges), graphCommit;
//   2. dispatch {variant:'light'}, await the restyle, assert the COMPUTED
//      cytoscape styles equal the LIGHT_* constants AND the :root chrome vars
//      flipped to light;
//   3. dispatch {variant:'dark'} and assert the computed styles + chrome vars are
//      restored BYTE-IDENTICAL to dark (the round-trip pin - a live restyle that
//      forgot a property would leave a light value stranded here).
// Its OWN page/context/channel (modeled on diffRenderTripwire / reducedMotionProbe)
// so its accounting is independent of the main run's zero-jsError audit (and it
// must itself be jsError-free). Harness morals hold: every wait is a bridge-
// message promise (MESSAGE_TIMEOUT_MS), only primitives leave page.evaluate
// (never a cytoscape collection), the bare protocol awaits fall under the global
// watchdog (see the boundedness inventory above), no sleeps. The 'theme' command
// is fire-and-forget (no bridge reply), so a {type:'ping'} is dispatched right
// after it and its 'pong' awaited: bridge.onCommand processes commands
// synchronously IN ORDER, so cy.style(...) + applyChromeVariant(...) have already
// run by the time the ping handler replies - a sleep-free restyle barrier.
// ---------------------------------------------------------------------------
async function lightThemeProbe(browser, indexHtml, screenshotDir) {
  const themeMessages = [];
  const themePending = new Map();
  const themeWaiters = new Map();

  function onThemeMessage(text) {
    const msg = JSON.parse(text);
    themeMessages.push(msg);
    const waiter = themeWaiters.get(msg.type)?.shift();
    if (waiter) {
      clearTimeout(waiter.timer);
      waiter.resolve(msg);
      return;
    }
    if (!themePending.has(msg.type)) {
      themePending.set(msg.type, []);
    }
    themePending.get(msg.type).push(msg);
  }

  function awaitThemeMessage(type, context) {
    const queued = themePending.get(type)?.shift();
    if (queued) {
      return Promise.resolve(queued);
    }
    return new Promise((resolvePromise, rejectPromise) => {
      const timer = setTimeout(() => {
        const list = themeWaiters.get(type) ?? [];
        const index = list.findIndex((w) => w.resolve === resolvePromise);
        if (index >= 0) {
          list.splice(index, 1);
        }
        const seen = themeMessages.map((m) => m.type).join(', ') || '(none)';
        rejectPromise(new Error(
          `timed out after ${MESSAGE_TIMEOUT_MS / 1000}s waiting for '${type}' (light-theme: ${context}); theme messages seen so far: ${seen}`));
      }, MESSAGE_TIMEOUT_MS);
      if (!themeWaiters.has(type)) {
        themeWaiters.set(type, []);
      }
      themeWaiters.get(type).push({ resolve: resolvePromise, timer });
    });
  }

  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('crash', () => {
    console.error(
      `FAILED page-crash: light-theme-probe renderer crashed; last completed phase: ${lastPhase}`);
    process.exit(1);
  });
  page.on('pageerror', (err) => onThemeMessage(JSON.stringify(
    { type: 'jsError', source: 'playwright:pageerror', message: String(err) })));
  await page.exposeFunction('__bridgeSendShim', onThemeMessage);

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
  await awaitThemeMessage('ready', 'light-theme bundle startup after goto');

  // HAND-BUILT dataset exercising every themeable channel (comma-DNs - getElementById
  // only, ADR-004 D5). Roots/edges/External/severity/rollup/busy/diff all present so a
  // single restyle is checked across the whole style array. Tiny on purpose (the demo-
  // fixture floors are the main run's, not this synthetic theme probe's).
  const ROOT_NODE = 'CN=Root,OU=Theme,DC=groupweaver,DC=invalid';
  const USER_NODE = 'CN=User Plain,OU=Theme,DC=groupweaver,DC=invalid';
  const GG_NODE = 'CN=GG Plain,OU=Theme,DC=groupweaver,DC=invalid';
  const DL_NODE = 'CN=DL Lifted,OU=Theme,DC=groupweaver,DC=invalid';
  const EXT_NODE = 'CN=External One,OU=Theme,DC=groupweaver,DC=invalid';
  const ERR_NODE = 'CN=Err Node,OU=Theme,DC=groupweaver,DC=invalid';
  const WARN_NODE = 'CN=Warn Node,OU=Theme,DC=groupweaver,DC=invalid';
  const INFO_NODE = 'CN=Info Node,OU=Theme,DC=groupweaver,DC=invalid';
  const ROLLUP_NODE = 'CN=Rollup Node,OU=Theme,DC=groupweaver,DC=invalid';
  const DIFF_ADD_NODE = 'CN=Diff Added,OU=Theme,DC=groupweaver,DC=invalid';
  const DIFF_REM_NODE = 'CN=Diff Removed,OU=Theme,DC=groupweaver,DC=invalid';
  const DIFF_UNC_NODE = 'CN=Diff Unchecked,OU=Theme,DC=groupweaver,DC=invalid';
  const nodes = [
    { id: ROOT_NODE, label: 'Root', kind: 'OrganizationalUnit', x: 0, y: 0, root: true },
    { id: USER_NODE, label: 'User Plain', kind: 'User', x: 120, y: -120 },
    { id: GG_NODE, label: 'GG Plain', kind: 'GlobalGroup', x: 120, y: 0 },
    { id: DL_NODE, label: 'DL Lifted', kind: 'DomainLocalGroup', x: 120, y: 120 },
    { id: EXT_NODE, label: 'External One', kind: 'External', x: 240, y: -120 },
    { id: ERR_NODE, label: 'Err Node', kind: 'User', x: 240, y: 0, sev: 'error' },
    { id: WARN_NODE, label: 'Warn Node', kind: 'User', x: 240, y: 120, sev: 'warning' },
    { id: INFO_NODE, label: 'Info Node', kind: 'User', x: 360, y: -120, sev: 'info' },
    { id: ROLLUP_NODE, label: 'Rollup Node', kind: 'GlobalGroup', x: 360, y: 0, below: 3, belowSev: 'error' },
    { id: DIFF_ADD_NODE, label: 'Diff Added', kind: 'GlobalGroup', x: 360, y: 120, diff: 'added' },
    { id: DIFF_REM_NODE, label: 'Diff Removed', kind: 'User', x: 480, y: -120, diff: 'removed' },
    { id: DIFF_UNC_NODE, label: 'Diff Unchecked', kind: 'GlobalGroup', x: 480, y: 120, diff: 'unchecked' },
  ];
  const MEMBER_EDGE = 'edge:member';
  const CONTAINS_EDGE = 'edge:contains';
  const DIFF_ADD_EDGE = 'edge:diff-added';
  const DIFF_REM_EDGE = 'edge:diff-removed';
  const DIFF_UNC_EDGE = 'edge:diff-unchecked';
  const edges = [
    { id: MEMBER_EDGE, s: GG_NODE, t: USER_NODE, rel: 'member' },
    { id: CONTAINS_EDGE, s: ROOT_NODE, t: GG_NODE, rel: 'contains' },
    { id: DIFF_ADD_EDGE, s: GG_NODE, t: DIFF_ADD_NODE, rel: 'member', diff: 'added' },
    { id: DIFF_REM_EDGE, s: GG_NODE, t: DIFF_REM_NODE, rel: 'member', diff: 'removed' },
    { id: DIFF_UNC_EDGE, s: GG_NODE, t: DIFF_UNC_NODE, rel: 'member', diff: 'unchecked' },
  ];

  // Busy is a TRANSIENT data flag (set by the {type:'busy'} command, [!sev] gated).
  // Set it on the plain User node AFTER commit so node[busy][!sev] paints its ring.
  for (const chunk of toChunks(nodes, edges)) {
    await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
  }
  await page.evaluate(() => window.bridge.dispatch({ type: 'graphCommit' }));
  await awaitThemeMessage('loaded', 'light-theme dataset graphCommit -> first render');
  await page.evaluate((id) => window.bridge.dispatch({ type: 'busy', id, on: true }), USER_NODE);

  // Restyle barrier: dispatch the theme command then a ping; await the pong. Commands
  // are handled synchronously in order, so the restyle is complete when pong arrives.
  let pingSeq = 0;
  const setVariant = async (variant) => {
    await page.evaluate((v) => window.bridge.dispatch({ type: 'theme', variant: v }), variant);
    pingSeq += 1;
    await page.evaluate((seq) => window.bridge.dispatch({ type: 'ping', seq }), pingSeq);
    const pong = await awaitThemeMessage('pong', `restyle barrier after {variant:'${variant}'}`);
    assert(pong.seq === pingSeq,
      `light-theme restyle barrier: pong.seq ${pong.seq} != ${pingSeq} (out-of-order command handling?)`);
  };

  // One round-trip per element pulling the full themeable computed-style bundle.
  // Primitives only out of evaluate (CI moral); getElementById keeps comma DNs
  // byte-identical (ADR-004 D5).
  const nodeStyleOf = (id) => page.evaluate((nid) => {
    const el = window.__cy.getElementById(nid);
    return {
      found: el.length === 1,
      labelColor: el.style('color'),
      labelOutline: el.style('text-outline-color'),
      borderColor: el.style('border-color'),
      borderWidth: el.style('border-width'),
      overlayColor: el.style('overlay-color'),
      overlayOpacity: el.style('overlay-opacity'),
      overlayPadding: el.style('overlay-padding'),
      underlayColor: el.style('underlay-color'),
      underlayOpacity: el.style('underlay-opacity'),
      bgColor: el.style('background-color'),
    };
  }, id);
  const edgeStyleOf = (id) => page.evaluate((eid) => {
    const el = window.__cy.getElementById(eid);
    return {
      found: el.length === 1,
      color: el.style('line-color'),
      style: el.style('line-style'),
      opacity: el.style('opacity'),
      arrowColor: el.style('target-arrow-color'),
    };
  }, id);
  const selectedBorderOf = (id) => page.evaluate((nid) => {
    const cy = window.__cy;
    const el = cy.getElementById(nid);
    el.select();
    const out = { color: el.style('border-color'), width: el.style('border-width') };
    el.unselect();
    return out;
  }, id);
  const chromeVarsNow = (names) => page.evaluate((vars) => {
    const root = document.documentElement;
    const cs = getComputedStyle(root);
    const out = {};
    for (const name of vars) {
      out[name] = cs.getPropertyValue(name).trim();
    }
    return out;
  }, names);
  // Normalize a CSS color OR an rgba()/hex chrome-var value for comparison: hex/rgb
  // collapse to #RRGGBB via toHex; an rgba(...) with alpha is compared structurally
  // (channels) so '#F5F6F8' and the alpha-bearing chrome surfaces both work.
  const sameColor = (got, want) => {
    if (/^rgba?\([^)]*\)$/i.test(want) && /a/i.test(want)) {
      // alpha-bearing want (e.g. rgba(255,255,255,0.92)): compare channel-wise.
      const norm = (s) => s.replace(/\s+/g, '').toLowerCase();
      return norm(got) === norm(want);
    }
    return toHex(got) === toHex(want);
  };

  // ===== LIGHT: dispatch + restyle barrier ==================================
  await setVariant('light');
  phase('light-theme: {variant:\'light\'} dispatched + restyle barrier (pong)');

  // -- label ink + outline (read on the plain User node) --
  const userLight = await nodeStyleOf(USER_NODE);
  assert(userLight.found, `light-theme: User node '${USER_NODE}' not found`);
  assert(toHex(userLight.labelColor) === LIGHT_LABEL_INK.toUpperCase(),
    `light label ink: rendered '${userLight.labelColor}' (${toHex(userLight.labelColor)}) != ${LIGHT_LABEL_INK} (graph.js THEME.light.labelInk drift?)`);
  assert(toHex(userLight.labelOutline) === LIGHT_LABEL_OUTLINE.toUpperCase(),
    `light label outline: rendered '${userLight.labelOutline}' (${toHex(userLight.labelOutline)}) != ${LIGHT_LABEL_OUTLINE}`);
  // Kind fill is theme-INVARIANT: the plain User fill stays #038387 on light too.
  assert(toHex(userLight.bgColor) === PALETTE.User.toUpperCase(),
    `light kind fill MUST be theme-invariant: User '${userLight.bgColor}' (${toHex(userLight.bgColor)}) != ${PALETTE.User} (ADR-026 D5: fills not re-toned)`);

  // -- node-lift DROPPED on light: DL ring width 0 (fills clear 3:1 on light) --
  const dlLight = await nodeStyleOf(DL_NODE);
  assert(dlLight.found, `light-theme: DL node '${DL_NODE}' not found`);
  assert(Math.abs(toNumber(dlLight.borderWidth) - LIGHT_NODE_LIFT_WIDTH) < 1e-6,
    `light node-lift: DL border-width rendered ${dlLight.borderWidth} != ${LIGHT_NODE_LIFT_WIDTH} (the 1.4.11 ring is DROPPED on light, ADR-026 D5 - DL fill clears 3:1)`);

  // -- root border + External dashed border re-tone --
  const rootLight = await nodeStyleOf(ROOT_NODE);
  assert(toHex(rootLight.borderColor) === LIGHT_ROOT_BORDER.toUpperCase(),
    `light root border: rendered '${rootLight.borderColor}' (${toHex(rootLight.borderColor)}) != ${LIGHT_ROOT_BORDER}`);
  const extLight = await nodeStyleOf(EXT_NODE);
  assert(toHex(extLight.borderColor) === LIGHT_EXTERNAL_BORDER.toUpperCase(),
    `light External border: rendered '${extLight.borderColor}' (${toHex(extLight.borderColor)}) != ${LIGHT_EXTERNAL_BORDER}`);

  // -- edges (member + contains): light hues, F6 lightness channel held --
  const memberLight = await edgeStyleOf(MEMBER_EDGE);
  assert(toHex(memberLight.color) === LIGHT_EDGE.member.toUpperCase()
    && toHex(memberLight.arrowColor) === LIGHT_EDGE.member.toUpperCase(),
    `light member edge: line/arrow rendered '${memberLight.color}'/'${memberLight.arrowColor}' (${toHex(memberLight.color)}/${toHex(memberLight.arrowColor)}) != ${LIGHT_EDGE.member}`);
  const containsLight = await edgeStyleOf(CONTAINS_EDGE);
  assert(toHex(containsLight.color) === LIGHT_EDGE.contains.toUpperCase(),
    `light contains edge: line rendered '${containsLight.color}' (${toHex(containsLight.color)}) != ${LIGHT_EDGE.contains}`);

  // -- severity halos (deepened hue + raised opacity; padding unchanged) --
  const SEV_NODES = { error: ERR_NODE, warning: WARN_NODE, info: INFO_NODE };
  for (const [sev, dn] of Object.entries(SEV_NODES)) {
    const want = LIGHT_SEVERITY_OVERLAY[sev];
    const got = await nodeStyleOf(dn);
    assert(got.found, `light-theme: ${sev} node '${dn}' not found`);
    assert(toHex(got.overlayColor) === want.color.toUpperCase(),
      `light severity ${sev} overlay-color: rendered '${got.overlayColor}' (${toHex(got.overlayColor)}) != ${want.color}`);
    assert(Math.abs(toNumber(got.overlayOpacity) - want.opacity) < 1e-6,
      `light severity ${sev} overlay-opacity: rendered ${got.overlayOpacity} != ${want.opacity}`);
    assert(Math.abs(toNumber(got.overlayPadding) - want.padding) < 1e-6,
      `light severity ${sev} overlay-padding (theme-invariant geometry): rendered ${got.overlayPadding} != ${want.padding}`);
  }

  // -- roll-up ring (wider/fainter, light error hue at 0.50) --
  const rollupLight = await nodeStyleOf(ROLLUP_NODE);
  assert(toHex(rollupLight.overlayColor) === LIGHT_SEVERITY_OVERLAY.error.color.toUpperCase(),
    `light roll-up ring color (belowSev=error): rendered '${rollupLight.overlayColor}' (${toHex(rollupLight.overlayColor)}) != ${LIGHT_SEVERITY_OVERLAY.error.color}`);
  assert(Math.abs(toNumber(rollupLight.overlayOpacity) - LIGHT_ROLLUP_OVERLAY.opacity) < 1e-6,
    `light roll-up ring opacity: rendered ${rollupLight.overlayOpacity} != ${LIGHT_ROLLUP_OVERLAY.opacity}`);
  assert(Math.abs(toNumber(rollupLight.overlayPadding) - LIGHT_ROLLUP_OVERLAY.padding) < 1e-6,
    `light roll-up ring padding: rendered ${rollupLight.overlayPadding} != ${LIGHT_ROLLUP_OVERLAY.padding}`);

  // -- busy ring (light blue at 0.55, on the plain User node, [!sev]) --
  const busyLight = await nodeStyleOf(USER_NODE);
  assert(toHex(busyLight.overlayColor) === LIGHT_BUSY.color.toUpperCase(),
    `light busy ring color: rendered '${busyLight.overlayColor}' (${toHex(busyLight.overlayColor)}) != ${LIGHT_BUSY.color}`);
  assert(Math.abs(toNumber(busyLight.overlayOpacity) - LIGHT_BUSY.opacity) < 1e-6,
    `light busy ring opacity: rendered ${busyLight.overlayOpacity} != ${LIGHT_BUSY.opacity}`);
  assert(Math.abs(toNumber(busyLight.overlayPadding) - LIGHT_BUSY.padding) < 1e-6,
    `light busy ring padding: rendered ${busyLight.overlayPadding} != ${LIGHT_BUSY.padding}`);

  // -- diff node underlays (raised opacity; removed keeps the invariant 0.45 bg-fade) --
  const DIFF_NODES = { added: DIFF_ADD_NODE, removed: DIFF_REM_NODE, unchecked: DIFF_UNC_NODE };
  for (const [status, dn] of Object.entries(DIFF_NODES)) {
    const want = LIGHT_DIFF_UNDERLAY[status];
    const got = await nodeStyleOf(dn);
    assert(got.found, `light-theme: diff ${status} node '${dn}' not found`);
    assert(toHex(got.underlayColor) === want.color.toUpperCase(),
      `light diff ${status} underlay-color: rendered '${got.underlayColor}' (${toHex(got.underlayColor)}) != ${want.color}`);
    assert(Math.abs(toNumber(got.underlayOpacity) - want.opacity) < 1e-6,
      `light diff ${status} underlay-opacity: rendered ${got.underlayOpacity} != ${want.opacity}`);
  }

  // -- diff edge lines (light hues; line-style channel theme-invariant) --
  const DIFF_EDGES = { added: DIFF_ADD_EDGE, removed: DIFF_REM_EDGE, unchecked: DIFF_UNC_EDGE };
  for (const [status, eid] of Object.entries(DIFF_EDGES)) {
    const want = LIGHT_DIFF_LINE[status];
    const got = await edgeStyleOf(eid);
    assert(got.found, `light-theme: diff ${status} edge '${eid}' not found`);
    assert(toHex(got.color) === want.color.toUpperCase(),
      `light diff ${status} edge line-color: rendered '${got.color}' (${toHex(got.color)}) != ${want.color}`);
    assert(got.style === want.style,
      `light diff ${status} edge line-style (theme-invariant colorblind channel): rendered '${got.style}' != '${want.style}'`);
    assert(Math.abs(toNumber(got.opacity) - want.opacity) < 1e-6,
      `light diff ${status} edge opacity: rendered ${got.opacity} != ${want.opacity}`);
  }

  // -- node:selected border re-tone (dark ink on light, NOT white) --
  const selLight = await selectedBorderOf(GG_NODE);
  assert(toHex(selLight.color) === LIGHT_SELECTION_BORDER.toUpperCase(),
    `light node:selected border-color: rendered '${selLight.color}' (${toHex(selLight.color)}) != ${LIGHT_SELECTION_BORDER} (white would vanish on the light canvas)`);
  assert(Math.abs(toNumber(selLight.width) - SELECTION.selBorderWidth) < 1e-6,
    `light node:selected border-width: rendered ${selLight.width} != ${SELECTION.selBorderWidth} (width is theme-invariant)`);

  // -- :root chrome custom properties flipped to light --
  const lightVars = await chromeVarsNow(Object.keys(LIGHT_CHROME_VARS));
  for (const [name, want] of Object.entries(LIGHT_CHROME_VARS)) {
    assert(sameColor(lightVars[name], want),
      `light chrome var ${name}: rendered '${lightVars[name]}' != ${want} (applyChromeVariant(CHROME.light) drift / theme handler missed it?)`);
  }
  // -- #168: the decorative dot-grid is actually WIRED into the #cy stage (not just a declared
  //    :root var). The themed dot COLOUR is pinned by the --gw-canvas-grid chrome var above; here
  //    we pin that #cy consumes it as a 24px radial-gradient texture so removing the stage
  //    background-image (orphaning the var) fails the bundle. Structure is theme-invariant. --
  const cyStage = await page.evaluate(() => {
    const cs = getComputedStyle(document.getElementById('cy'));
    return { image: cs.backgroundImage, size: cs.backgroundSize };
  });
  assert(/radial-gradient/i.test(cyStage.image),
    `#168 dot-grid: #cy background-image '${cyStage.image}' is not a radial-gradient (stage texture dropped / var orphaned?)`);
  assert(/^24px\s+24px$/.test(cyStage.size.trim()),
    `#168 dot-grid: #cy background-size '${cyStage.size}' != '24px 24px' (grid pitch drift?)`);
  phase('light-theme: computed canvas styles + :root chrome vars + #cy dot-grid verified vs LIGHT_* (ADR-026 D5, #168)');

  // -- screenshot: the light frame the ui-verifier judges --
  await page.screenshot({ path: join(screenshotDir, 'graph-light.png') });
  phase('light-theme screenshot');

  // ===== ROUND-TRIP: back to DARK, computed styles byte-identical ===========
  // Capture the full dark computed-style bundle for one representative element per
  // channel, then compare against the same DARK constants the rest of the harness
  // pins (PALETTE/SEVERITY_OVERLAY/DIFF_*/BASE_EDGE/etc) - proving the live restyle
  // ROUND-TRIPS (a property the light pass set that dark forgot to reset strands here).
  await setVariant('dark');
  phase('light-theme: {variant:\'dark\'} dispatched + restyle barrier (round-trip)');

  const userDark = await nodeStyleOf(USER_NODE);
  assert(toHex(userDark.labelColor) === '#E8ECF2'
    && toHex(userDark.labelOutline) === '#1B1F27',
    `dark round-trip label ink/outline: rendered '${userDark.labelColor}'/'${userDark.labelOutline}' (${toHex(userDark.labelColor)}/${toHex(userDark.labelOutline)}) != #E8ECF2/#1B1F27 (restyle did not restore dark)`);
  const dlDark = await nodeStyleOf(DL_NODE);
  assert(toHex(dlDark.borderColor) === KIND_BORDER_COLOR.toUpperCase()
    && Math.abs(toNumber(dlDark.borderWidth) - KIND_BORDER_WIDTH) < 1e-6,
    `dark round-trip node-lift: DL ring rendered '${dlDark.borderColor}' (${toHex(dlDark.borderColor)}) / width ${dlDark.borderWidth} != ${KIND_BORDER_COLOR} / ${KIND_BORDER_WIDTH} (the 2px ring must come BACK on dark)`);
  const memberDark = await edgeStyleOf(MEMBER_EDGE);
  assert(toHex(memberDark.color) === BASE_EDGE.member.color.toUpperCase(),
    `dark round-trip member edge: rendered '${memberDark.color}' (${toHex(memberDark.color)}) != ${BASE_EDGE.member.color}`);
  const containsDark = await edgeStyleOf(CONTAINS_EDGE);
  assert(toHex(containsDark.color) === BASE_EDGE.contains.color.toUpperCase(),
    `dark round-trip contains edge: rendered '${containsDark.color}' (${toHex(containsDark.color)}) != ${BASE_EDGE.contains.color}`);
  for (const [sev, dn] of Object.entries(SEV_NODES)) {
    const want = SEVERITY_OVERLAY[sev];
    const got = await nodeStyleOf(dn);
    assert(toHex(got.overlayColor) === want.color.toUpperCase()
      && Math.abs(toNumber(got.overlayOpacity) - want.opacity) < 1e-6
      && Math.abs(toNumber(got.overlayPadding) - want.padding) < 1e-6,
      `dark round-trip severity ${sev} ('${dn}'): overlay '${got.overlayColor}' (${toHex(got.overlayColor)}) / op ${got.overlayOpacity} / pad ${got.overlayPadding} != ${want.color}/${want.opacity}/${want.padding}`);
  }
  const busyDark = await nodeStyleOf(USER_NODE);
  assert(toHex(busyDark.overlayColor) === BUSY.color.toUpperCase()
    && Math.abs(toNumber(busyDark.overlayOpacity) - BUSY.opacity) < 1e-6,
    `dark round-trip busy ring: rendered '${busyDark.overlayColor}' (${toHex(busyDark.overlayColor)}) / op ${busyDark.overlayOpacity} != ${BUSY.color}/${BUSY.opacity}`);
  for (const [status, dn] of Object.entries(DIFF_NODES)) {
    const want = DIFF_UNDERLAY[status];
    const got = await nodeStyleOf(dn);
    assert(toHex(got.underlayColor) === want.color.toUpperCase()
      && Math.abs(toNumber(got.underlayOpacity) - want.opacity) < 1e-6,
      `dark round-trip diff ${status} underlay ('${dn}'): rendered '${got.underlayColor}' (${toHex(got.underlayColor)}) / op ${got.underlayOpacity} != ${want.color}/${want.opacity}`);
  }
  for (const [status, eid] of Object.entries(DIFF_EDGES)) {
    const want = DIFF_LINE[status];
    const got = await edgeStyleOf(eid);
    assert(toHex(got.color) === want.color.toUpperCase() && got.style === want.style,
      `dark round-trip diff ${status} edge ('${eid}'): rendered '${got.color}' (${toHex(got.color)}) / '${got.style}' != ${want.color}/'${want.style}'`);
  }
  const selDark = await selectedBorderOf(GG_NODE);
  assert(toHex(selDark.color) === SELECTION.selBorderColor.toUpperCase()
    && Math.abs(toNumber(selDark.width) - SELECTION.selBorderWidth) < 1e-6,
    `dark round-trip node:selected border: rendered '${selDark.color}' (${toHex(selDark.color)}) / width ${selDark.width} != ${SELECTION.selBorderColor} / ${SELECTION.selBorderWidth} (white border must return on dark)`);

  // :root chrome vars restored to the DARK defaults (index.html :root literals).
  const DARK_CHROME_VARS = {
    '--gw-canvas-bg': '#1b1f27',
    '--gw-canvas-grid': 'rgba(255, 255, 255, 0.04)',  // #168 dot-grid tint restores to the dark value
    '--gw-chrome-bg': 'rgba(22, 26, 33, 0.92)',
    '--gw-sev-error': SEVERITY.error,
    '--gw-sev-warning': SEVERITY.warning,
    '--gw-sev-info': SEVERITY.info,
    '--gw-diff-added': DIFF.added,
    '--gw-diff-removed': DIFF.removed,
    '--gw-diff-unchecked': DIFF.unchecked,
    // ADR-027 D4: the accent var must restore to the DARK brand purple on the dark round-trip.
    '--gw-accent': DARK_ACCENT,
  };
  const darkVars = await chromeVarsNow(Object.keys(DARK_CHROME_VARS));
  for (const [name, want] of Object.entries(DARK_CHROME_VARS)) {
    assert(sameColor(darkVars[name], want),
      `dark round-trip chrome var ${name}: rendered '${darkVars[name]}' != ${want} (applyChromeVariant(CHROME.dark) did not restore the dark default)`);
  }
  phase('light-theme: DARK round-trip computed styles + :root chrome vars restored byte-identical');

  // This phase is itself jsError-free on its own channel.
  const themeJsErrors = themeMessages.filter((m) => m.type === 'jsError');
  assert(themeJsErrors.length === 0,
    `light-theme probe must be jsError-free on its own channel: ${JSON.stringify(themeJsErrors, null, 2)}`);

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

    // --- BASE edge styling parity C# <-> JS (F6/F7 edge-legibility change) -----
    // Pin the NON-diff edge[rel='member'] / edge[rel='contains'] computed styles
    // so the F6 legibility change can't silently regress (verify.mjs previously
    // pinned ONLY the diff-edge overrides). Pick a representative member edge and
    // a contains edge from the demo fixture that carry NO `diff` field, so the
    // base rel rule is exactly what cytoscape resolves (a diffed edge would be
    // overridden by edge[diff=...], DIFF_LINE above). Read line-color/width/
    // line-style (+ member's target-arrow-color) via getElementById on the edge's
    // own id (ADR-004 D5 byte-identical lookup), the same idiom as `lineOf` in the
    // diff tripwire. The colorblind-redundant (hue-free) channel is line-style:
    // member=solid vs contains=dashed - assert both so the two layers stay
    // distinguishable without relying on the lightness/hue difference.
    const baseMemberEdge = fixture.edges.find((e) => e.rel === 'member' && !e.diff);
    const baseContainsEdge = fixture.edges.find((e) => e.rel === 'contains' && !e.diff);
    assert(baseMemberEdge !== undefined,
      'F6 base-edge: demo fixture must contain a non-diff member edge to pin edge[rel=\'member\'] styling');
    assert(baseContainsEdge !== undefined,
      'F6 base-edge: demo fixture must contain a non-diff contains edge to pin edge[rel=\'contains\'] styling');
    const edgeStyleOf = (eid) => page.evaluate((id) => {
      const el = window.__cy.getElementById(id);
      return {
        found: el.length === 1,
        color: el.style('line-color'),
        width: el.style('width'),
        style: el.style('line-style'),
        arrowColor: el.style('target-arrow-color'),
      };
    }, eid);

    const gotMember = await edgeStyleOf(baseMemberEdge.id);
    assert(gotMember.found, `F6 base-edge: member edge '${baseMemberEdge.id}' not found on the rendered graph`);
    assert(toHex(gotMember.color) === BASE_EDGE.member.color.toUpperCase(),
      `F6 base-edge line-color for member ('${baseMemberEdge.id}'): rendered '${gotMember.color}' (${toHex(gotMember.color)}) != pinned ${BASE_EDGE.member.color} (graph.js edge[rel='member'] regressed?)`);
    assert(Math.abs(toNumber(gotMember.width) - BASE_EDGE.member.width) < 1e-6,
      `F6 base-edge width for member ('${baseMemberEdge.id}'): rendered ${gotMember.width} != pinned ${BASE_EDGE.member.width}`);
    assert(gotMember.style === BASE_EDGE.member.style,
      `F6 base-edge line-style (colorblind-redundant channel) for member ('${baseMemberEdge.id}'): rendered '${gotMember.style}' != pinned '${BASE_EDGE.member.style}' (member must stay SOLID, the non-color channel vs dashed contains)`);
    assert(toHex(gotMember.arrowColor) === BASE_EDGE.member.arrowColor.toUpperCase(),
      `F6 base-edge target-arrow-color for member ('${baseMemberEdge.id}'): rendered '${gotMember.arrowColor}' (${toHex(gotMember.arrowColor)}) != pinned ${BASE_EDGE.member.arrowColor} (arrow must match the directed-membership line color)`);

    const gotContains = await edgeStyleOf(baseContainsEdge.id);
    assert(gotContains.found, `F6 base-edge: contains edge '${baseContainsEdge.id}' not found on the rendered graph`);
    assert(toHex(gotContains.color) === BASE_EDGE.contains.color.toUpperCase(),
      `F6 base-edge line-color for contains ('${baseContainsEdge.id}'): rendered '${gotContains.color}' (${toHex(gotContains.color)}) != pinned ${BASE_EDGE.contains.color} (graph.js edge[rel='contains'] regressed?)`);
    assert(Math.abs(toNumber(gotContains.width) - BASE_EDGE.contains.width) < 1e-6,
      `F6 base-edge width for contains ('${baseContainsEdge.id}'): rendered ${gotContains.width} != pinned ${BASE_EDGE.contains.width}`);
    assert(gotContains.style === BASE_EDGE.contains.style,
      `F6 base-edge line-style (colorblind-redundant channel) for contains ('${baseContainsEdge.id}'): rendered '${gotContains.style}' != pinned '${BASE_EDGE.contains.style}' (containment must stay DASHED, the non-color channel vs solid member)`);
    phase('F6 base edge styling (member solid #8E9BB4/1.6 + arrow, contains dashed #6B788F/1)');

    // --- F7: hideEdgesOnViewport gated on edge count --------------------------
    // graph.js sets hideEdgesOnViewport: edgeCount > EDGE_HIDE_THRESHOLD (1500) at
    // cy init (was hardcoded true). The demo fixture is ~334 edges, well below the
    // threshold, so edges must stay VISIBLE during pan/zoom => the resolved option
    // is false. cytoscape stores the resolved value on its canvas renderer
    // (cy.renderer().hideEdgesOnViewport - copied from the init options); read it
    // there. This is the resolved render-time value, not a re-read of our own input.
    const hideEdges = await page.evaluate(() => {
      const r = window.__cy.renderer();
      return (r && typeof r.hideEdgesOnViewport === 'boolean') ? r.hideEdgesOnViewport : null;
    });
    assert(hideEdges === false,
      `F7: demo graph (${loaded.edgeCount} edges, < ${1500} threshold) must resolve hideEdgesOnViewport=false (edges stay visible during gestures); cy.renderer().hideEdgesOnViewport = ${JSON.stringify(hideEdges)} (null => renderer didn't expose it - if it ever stops exposing this, downgrade to a review-enforced comment rather than reading internals)`);
    phase(`F7 hideEdgesOnViewport=false for ${loaded.edgeCount}-edge demo (< 1500 threshold)`);

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

    // --- WP-A (#176, ADR-029): overview edge-fade ----------------------------
    // The demo is an EXPLORE graph (no diff edge => isDiffGraph false), so at the
    // post-cy.fit() overview zoom updateEdgeFade(true) (run in initGraph after
    // fit) has flagged EVERY edge with `gw-edge-faded` and the
    // `edge.gw-edge-faded { opacity: 0.15 }` rule resolves to EDGE_FADE_OPACITY.
    // This is the frame graph-overview.png (below) captures - the README hero is
    // copied from it, so the faded constellation is the pinned first impression.
    // Reuse the SAME representative non-diff member + contains edges the F6 BASE
    // block pinned (baseMemberEdge/baseContainsEdge), reading opacity + the class
    // via getElementById (comma-DN safe, ADR-004 D5). The base buildStyle
    // edge[rel=...] opacity literal is UNCHANGED (still 1, pinned implicitly by
    // the zoomed-in read below); only the RENDERED overview state changed via the
    // toggled class. NOTE: pre-ADR-029 this overview frame rendered these edges at
    // opacity 1 (the base rel rule); the F6 BASE block never pinned edge OPACITY
    // (only color/width/line-style/arrow), so no prior assertion is invalidated -
    // this block ADDS the overview-state pin ADR-029 D5 / Consequences calls for.
    const fadeStyleOf = (eid) => page.evaluate((id) => {
      const el = window.__cy.getElementById(id);
      return { found: el.length === 1, opacity: el.style('opacity'), faded: el.hasClass('gw-edge-faded') };
    }, eid);
    const fitZoomDemo = await page.evaluate(() => window.__cy.zoom());
    const ovMemberFade = await fadeStyleOf(baseMemberEdge.id);
    const ovContainsFade = await fadeStyleOf(baseContainsEdge.id);
    assert(ovMemberFade.found && ovContainsFade.found,
      `ADR-029 overview-fade: sample edges must be present (member '${baseMemberEdge.id}', contains '${baseContainsEdge.id}')`);
    assert(ovMemberFade.faded,
      `ADR-029 overview-fade: member edge '${baseMemberEdge.id}' must carry the gw-edge-faded class at the fit/overview zoom (${fitZoomDemo}) on an explore graph`);
    assert(Math.abs(toNumber(ovMemberFade.opacity) - EDGE_FADE_OPACITY) < 1e-6,
      `ADR-029 overview-fade: member edge '${baseMemberEdge.id}' must render opacity ${EDGE_FADE_OPACITY} at overview, got ${ovMemberFade.opacity} (graph.js edge.gw-edge-faded rule regressed or updateEdgeFade not applied after fit?)`);
    assert(ovContainsFade.faded,
      `ADR-029 overview-fade: contains edge '${baseContainsEdge.id}' must carry the gw-edge-faded class at the fit/overview zoom on an explore graph`);
    assert(Math.abs(toNumber(ovContainsFade.opacity) - EDGE_FADE_OPACITY) < 1e-6,
      `ADR-029 overview-fade: contains edge '${baseContainsEdge.id}' must render opacity ${EDGE_FADE_OPACITY} at overview, got ${ovContainsFade.opacity}`);
    phase(`ADR-029 overview-fade: member + contains edges faded (opacity ${EDGE_FADE_OPACITY}, gw-edge-faded) at fit zoom ${fitZoomDemo.toFixed(4)}`);

    // --- screenshot 1: overview (initGraph already ran cy.fit()) -------------
    await page.screenshot({ path: join(screenshotDir, 'graph-overview.png') });
    phase('overview screenshot');

    // --- WP-A (#176, ADR-029): zoom IN past fitZoom*EDGE_FADE_FACTOR restores --
    // The same two edges must return to full opacity (class removed) once the user
    // zooms in to inspect - the fade is a zoom-driven OVERVIEW treatment, not a
    // permanent dim. Snapshot the eased/overview viewport, zoom to fitZoom*2 (well
    // past the fitZoom*EDGE_FADE_FACTOR=1.6 band ceiling), let the 'zoom' event +
    // hysteresis settle, read, then RESTORE the snapshot (the
    // snapshot-and-restore idiom from assertEasedFocus) so every later frame the
    // page reuses (selection/controls/etc.) starts from the unchanged overview
    // camera. EDGE_FULL_OPACITY (1) pins the base rel opacity literal too - so the
    // overview 0.15 above is unambiguously the class toggle, not a buildStyle edit.
    const fadeSnap = await page.evaluate(() => {
      const cy = window.__cy; const p = cy.pan();
      return { zoom: cy.zoom(), panX: p.x, panY: p.y };
    });
    await page.evaluate((f) => { window.__cy.zoom(window.__cy.zoom() * (f + 0.4)); }, EDGE_FADE_FACTOR);
    const zoomedInZoom = await page.evaluate(() => window.__cy.zoom());
    assert(zoomedInZoom > fitZoomDemo * EDGE_FADE_FACTOR,
      `ADR-029 zoom-in: probe zoom ${zoomedInZoom} must exceed the fade ceiling fitZoom*${EDGE_FADE_FACTOR} (${(fitZoomDemo * EDGE_FADE_FACTOR).toFixed(4)}) so the fade is expected OFF`);
    const ziMemberFade = await fadeStyleOf(baseMemberEdge.id);
    const ziContainsFade = await fadeStyleOf(baseContainsEdge.id);
    assert(!ziMemberFade.faded,
      `ADR-029 zoom-in: member edge '${baseMemberEdge.id}' must DROP the gw-edge-faded class once zoomed past fitZoom*${EDGE_FADE_FACTOR}`);
    assert(Math.abs(toNumber(ziMemberFade.opacity) - EDGE_FULL_OPACITY) < 1e-6,
      `ADR-029 zoom-in: member edge '${baseMemberEdge.id}' must render full opacity ${EDGE_FULL_OPACITY} when zoomed in, got ${ziMemberFade.opacity} (base edge[rel='member'] opacity regressed?)`);
    assert(!ziContainsFade.faded,
      `ADR-029 zoom-in: contains edge '${baseContainsEdge.id}' must DROP the gw-edge-faded class once zoomed past fitZoom*${EDGE_FADE_FACTOR}`);
    assert(Math.abs(toNumber(ziContainsFade.opacity) - EDGE_FULL_OPACITY) < 1e-6,
      `ADR-029 zoom-in: contains edge '${baseContainsEdge.id}' must render full opacity ${EDGE_FULL_OPACITY} when zoomed in, got ${ziContainsFade.opacity}`);
    // Restore the overview camera (assertEasedFocus snapshot-restore idiom) so the
    // shared page resumes at the unchanged fit zoom for every later frame.
    await page.evaluate((s) => { window.__cy.zoom(s.zoom); window.__cy.pan({ x: s.panX, y: s.panY }); }, fadeSnap);
    const restoredMemberFade = await fadeStyleOf(baseMemberEdge.id);
    assert(restoredMemberFade.faded
      && Math.abs(toNumber(restoredMemberFade.opacity) - EDGE_FADE_OPACITY) < 1e-6,
      `ADR-029 zoom-in: restoring the overview viewport must re-fade member edge '${baseMemberEdge.id}' (opacity ${EDGE_FADE_OPACITY} + gw-edge-faded) so later frames start from the unchanged overview camera, got opacity ${restoredMemberFade.opacity} faded=${restoredMemberFade.faded}`);
    phase(`ADR-029 zoom-in: edges restore to opacity ${EDGE_FULL_OPACITY} past fitZoom*${EDGE_FADE_FACTOR} (zoom ${zoomedInZoom.toFixed(4)}), overview camera restored`);
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

    // --- WCAG 1.4.11 node-lift parity C# <-> JS (#90, ADR-021) ---------------
    // The three kind FILLS below the 3:1 graphical-object-contrast floor (DL/UG/
    // Computer) gain a 2px #8A93A3 ring in graph.js; the fills stay UNCHANGED (the
    // palette-parity block above already pinned the fills). Pinned per rendered node
    // via getElementById (comma-DN safe, ADR-004 D5) + the same toHex/style read the
    // fill asserts use. KIND_BORDER mirrors BrandTokens.NodeLiftRingHex. Each lifted
    // kind MUST be present in the demo fixture (DL/UG/Computer all ship in the demo
    // dataset); a missing one is a fixture regression and fails here, not a skip.
    const borderOf = (id) => page.evaluate((nid) => {
      const el = window.__cy.getElementById(nid);
      return {
        found: el.length === 1,
        color: el.style('border-color'),
        width: el.style('border-width'),
      };
    }, id);
    for (const [kind, expectedBorderHex] of Object.entries(KIND_BORDER)) {
      const sample = fixture.nodes.find((x) => x.kind === kind);
      assert(sample !== undefined,
        `#90 node-lift: demo fixture must contain a '${kind}' node to verify its WCAG 1.4.11 ring (fixture/demo regression)`);
      const got = await borderOf(sample.id);
      assert(got.found, `#90 node-lift: '${kind}' node '${sample.id}' not found on the rendered graph`);
      assert(toHex(got.color) === expectedBorderHex.toUpperCase(),
        `#90 node-lift border-color for ${kind} ('${sample.id}'): rendered '${got.color}' (${toHex(got.color)}) != BrandTokens.NodeLiftRing ${expectedBorderHex} (graph.js missing the 2px ring on node[kind='${kind}']?)`);
      assert(Math.abs(toNumber(got.width) - KIND_BORDER_WIDTH) < 1e-6,
        `#90 node-lift border-width for ${kind} ('${sample.id}'): rendered ${got.width} != pinned ${KIND_BORDER_WIDTH}px`);
    }
    // Tripwire: a kind WITHOUT the lift carries NO border (width ~0) - so the ring is
    // EXACTLY the three low-contrast kinds, never a blanket "border every node". TWO
    // exclusions on the control node: (1) NOT External (it owns its own distinct dashed
    // #B0B6BF border), and (2) NOT root (node[?root] paints a 3px white #E8ECF2 border -
    // an unrelated lift that would false-fail this assert). So pick a NON-ROOT node of a
    // present kind that is neither lifted nor External (User/GG/OU, each fill >= 3:1, all
    // border-width 0 in graph.js).
    const noLiftSample = fixture.nodes.find(
      (x) => !(x.kind in KIND_BORDER) && x.kind !== 'External' && !x.root);
    assert(noLiftSample !== undefined,
      '#90 node-lift: fixture must contain a non-lifted, non-External, non-root node (border-width 0 control)');
    const noLiftBorder = await borderOf(noLiftSample.id);
    assert(noLiftBorder.found, `#90 node-lift: control node '${noLiftSample.id}' not found`);
    assert(Math.abs(toNumber(noLiftBorder.width)) < 1e-6,
      `#90 node-lift: non-lifted kind '${noLiftSample.kind}' ('${noLiftSample.id}') must carry NO border (width 0) - the ring must be EXACTLY DL/UG/Computer, not every node; rendered border-width ${noLiftBorder.width}`);
    phase(`#90 node-lift ring (DL/UG/Computer +2px #8A93A3, '${noLiftSample.kind}' control border 0)`);

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

    // Own-severity vs roll-up-ring collision (issue #268 / finding graph-1
    // regression pin). A node can carry BOTH its own `sev` (a live finding) AND
    // `below`/`belowSev` (a hidden flagged descendant under it, e.g. a collapsed
    // subtree) - before #268 the node[below] rules had no `[!sev]` guard, so they
    // cascaded OVER node[sev=...] and the fainter/wider roll-up ring silently
    // replaced the node's OWN severity halo on the overlay-* channel whenever both
    // were present. The fix added `[!sev]` to all three node[below] selectors
    // (mirroring the existing node[busy][!sev] precedent just below them) so a
    // node's own severity now always wins. Mutate BELOW_PIN in place - it is
    // already proven above to be below>0/belowSev='${belowFx.belowSev}' with no
    // own sev - by adding sev='error' via the SAME live `data()` field graph.js
    // reads, WITHOUT touching belowSev (so the roll-up values are still present
    // and would win here if the guard regressed). Restored immediately after the
    // assert so the live-cy state does not leak into any downstream block (the
    // WP3b issues-only-filter's `!!sev || !!below` predicate already treats this
    // node as flagged via `below` alone, so this is belt-and-suspenders, not load-
    // bearing for that block - but keeping this block side-effect-free is cheap).
    const collisionBefore = await overlayOf(page, BELOW_PIN);
    assert(toHex(collisionBefore.color) === SEVERITY[belowFx.belowSev].toUpperCase(),
      `precondition: '${BELOW_PIN}' must still show the plain roll-up ring before the collision mutation`);
    await page.evaluate(
      (id) => window.__cy.getElementById(id).data('sev', 'error'), BELOW_PIN);
    const collision = await overlayOf(page, BELOW_PIN);
    assert(collision.found, `collision node '${BELOW_PIN}' not found after adding sev='error'`);
    assert(toHex(collision.color) === SEVERITY_OVERLAY.error.color.toUpperCase(),
      `own-sev-vs-roll-up collision (#268): '${BELOW_PIN}' with sev='error' AND below/belowSev=`
      + `'${belowFx.belowSev}' must render its OWN severity overlay-color, rendered '${collision.color}' `
      + `(${toHex(collision.color)}) != error halo ${SEVERITY_OVERLAY.error.color} `
      + `(node[below][!sev] guard regressed - the roll-up ring overwrote severity?)`);
    assert(Math.abs(toNumber(collision.opacity) - SEVERITY_OVERLAY.error.opacity) < 1e-6,
      `own-sev-vs-roll-up collision (#268): '${BELOW_PIN}' overlay-opacity rendered ${collision.opacity} `
      + `!= own-severity ${SEVERITY_OVERLAY.error.opacity} (must not fall back to the fainter roll-up `
      + `${ROLLUP_OVERLAY.opacity})`);
    assert(Math.abs(toNumber(collision.padding) - SEVERITY_OVERLAY.error.padding) < 1e-6,
      `own-sev-vs-roll-up collision (#268): '${BELOW_PIN}' overlay-padding rendered ${collision.padding} `
      + `!= own-severity ${SEVERITY_OVERLAY.error.padding} (must not fall back to the wider roll-up `
      + `${ROLLUP_OVERLAY.padding})`);
    // Restore: drop the injected 'sev' field so live-cy state matches the
    // unmutated fixture again (BELOW_PIN reverts to below-only, no state leak).
    // updateStyle() is REQUIRED here: cytoscape leaves overlay-* frozen at its
    // last computed value when an element merely stops matching a selector (the
    // same gotcha the 'busy'-off command works around in graph.js) - without it
    // this node would stay stuck showing the error halo despite no longer
    // matching node[sev='error'].
    await page.evaluate((id) => {
      const el = window.__cy.getElementById(id);
      el.removeData('sev');
      el.updateStyle();
    }, BELOW_PIN);
    const restored = await overlayOf(page, BELOW_PIN);
    assert(toHex(restored.color) === SEVERITY[belowFx.belowSev].toUpperCase()
      && Math.abs(toNumber(restored.opacity) - ROLLUP_OVERLAY.opacity) < 1e-6
      && Math.abs(toNumber(restored.padding) - ROLLUP_OVERLAY.padding) < 1e-6,
      `restore check: '${BELOW_PIN}' must return to the plain roll-up ring after removeData('sev'): ${JSON.stringify(restored)}`);
    phase(`severity vs roll-up collision (#268): own sev='error' wins over belowSev='${belowFx.belowSev}' ('${BELOW_PIN}')`);

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

    // --- ADR-027 D3 (WP3): the accent ring is SHOWN over the tapped node ---------
    // applySelection (the tap path) shows #gw-accent-ring at the selected node's
    // renderedPosition, additively over the white node:selected border. Prove it is
    // not hidden AND centers on the selected node. The CSS pulse does NOT call
    // cy.animate, so __gwAnimateCalls stays 0 (asserted at the end of this block).
    const ringAfterTap = await accentRingStateOf(page);
    const tapPos = await renderedPositionOf(page, selectDn);
    assertAccentRingOver(ringAfterTap, tapPos, 'accent ring after tap-select');
    phase(`accent ring shown over the tap-selected node ('${selectDn}')`);

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
    // Braces: do NOT implicitly return cy.emit()'s cyclic collection (CDP
    // serialization wedge - see the find-clear note in the ADR-023 block).
    await page.evaluate(() => { window.__cy.emit('tap', { target: window.__cy }); });
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

    // --- ADR-027 D3: the accent ring HIDES on a background-tap clear ------------
    // clearSelection hides #gw-accent-ring (no stale ring floats over empty canvas).
    const ringAfterClear = await accentRingStateOf(page);
    assert(ringAfterClear.exists && ringAfterClear.hidden === true,
      `accent ring after background-tap clear: #gw-accent-ring must be HIDDEN (clearSelection, ADR-027 D3), got hidden=${ringAfterClear.hidden}`);
    phase('accent ring hidden after background-tap clear');

    // --- ADR-020 (#96): {type:'select'} command drives selection from OUTSIDE a tap
    // The reverse sidebar->graph sync: a select command must reuse applySelection /
    // clearSelection, so a COMMAND-driven select is byte-identical to the tap-driven
    // one asserted above (same selected node, same closedNeighborhood un-dim, same
    // +0.6 dim on non-neighbors). Runs from the clean post-background-tap state.
    // Reset the #88 motion recorders: the command path is INSTANT (addClass /
    // removeClass only, never cy.animate), so neither counter may move across this block.
    await page.evaluate(() => {
      window.__gwAnimateCalls = 0;
      window.__gwAnimateLastDuration = null;
      window.__gwEnterAnims = [];
    });

    // (1) select a real comma-DN node => IDENTICAL to a tap-driven applySelection.
    await page.evaluate((id) => window.bridge.dispatch({ type: 'select', id }), selectDn);
    const cmdSelState = await page.evaluate((a) => {
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
        nbrHasDim: nbr.hasClass('gw-dim'),
        nbrBlacken: nbr.style('background-blacken'),
        nonHasDim: non.hasClass('gw-dim'),
        nonBlacken: non.style('background-blacken'),
      };
    }, { selectDn, neighborDn, nonNeighborDn });
    assert(cmdSelState.selSelected === true && cmdSelState.selectedCount === 1,
      `{type:'select', id:'${selectDn}'} must select EXACTLY that node via applySelection (reverse sync, ADR-020): selected()=${cmdSelState.selSelected}, count=${cmdSelState.selectedCount}`);
    assert(!cmdSelState.selHasDim && Math.abs(toNumber(cmdSelState.selBlacken)) < 1e-6,
      `command-selected node '${selectDn}' must be UN-dimmed (byte-identical to the tap path): background-blacken ${cmdSelState.selBlacken}, gw-dim=${cmdSelState.selHasDim}`);
    assert(toHex(cmdSelState.selBorderColor) === SELECTION.selBorderColor.toUpperCase()
      && Math.abs(toNumber(cmdSelState.selBorderWidth) - SELECTION.selBorderWidth) < 1e-6,
      `command-selected node '${selectDn}' must carry the node:selected border (${SELECTION.selBorderColor} / width ${SELECTION.selBorderWidth}): rendered '${cmdSelState.selBorderColor}' (${toHex(cmdSelState.selBorderColor)}) / ${cmdSelState.selBorderWidth}`);
    assert(!cmdSelState.nbrHasDim && Math.abs(toNumber(cmdSelState.nbrBlacken)) < 1e-6,
      `1-hop neighbor '${neighborDn}' must be UN-dimmed under a command select (closedNeighborhood): background-blacken ${cmdSelState.nbrBlacken}, gw-dim=${cmdSelState.nbrHasDim}`);
    assert(cmdSelState.nonHasDim && Math.abs(toNumber(cmdSelState.nonBlacken) - SELECTION.dimBlacken) < 1e-6,
      `non-neighbor '${nonNeighborDn}' must be dimmed +${SELECTION.dimBlacken} under a command select (background-blacken): rendered ${cmdSelState.nonBlacken}, gw-dim=${cmdSelState.nonHasDim}`);
    phase(`select command selects + dims identical to a tap ('${selectDn}')`);

    // --- ADR-027 D3: the {type:'select'} reverse-sync ALSO shows the accent ring -
    // The command path reuses applySelection, so the ring is byte-identical to the
    // tap path: shown + centered on the selected node's renderedPosition.
    const ringAfterCmdSelect = await accentRingStateOf(page);
    const cmdSelectPos = await renderedPositionOf(page, selectDn);
    assertAccentRingOver(ringAfterCmdSelect, cmdSelectPos, 'accent ring after {type:select} reverse-sync');
    phase(`accent ring shown over the command-selected node ('${selectDn}')`);

    // (2) select with the EMPTY id => clearSelection (a null sidebar selection clears).
    await page.evaluate(() => window.bridge.dispatch({ type: 'select', id: '' }));
    const cmdCleared = await page.evaluate(() => {
      const cy = window.__cy;
      return { selectedCount: cy.nodes(':selected').length, anyDim: cy.nodes('.gw-dim').length };
    });
    assert(cmdCleared.selectedCount === 0 && cmdCleared.anyDim === 0,
      `{type:'select', id:''} must clearSelection (null sidebar selection visibly clears the canvas): selected=${cmdCleared.selectedCount}, dim=${cmdCleared.anyDim}`);
    phase("select command with empty id clears the selection");

    // --- ADR-027 D3: the accent ring HIDES on an empty {type:'select'} ----------
    const ringAfterEmptySelect = await accentRingStateOf(page);
    assert(ringAfterEmptySelect.exists && ringAfterEmptySelect.hidden === true,
      `accent ring after {type:'select', id:''}: #gw-accent-ring must be HIDDEN (clearSelection), got hidden=${ringAfterEmptySelect.hidden}`);
    phase('accent ring hidden after empty select command');

    // (3) re-select, then select an UNKNOWN comma DN => clearSelection (a stale/unknown
    // DN clears rather than leaving a frozen highlight) AND, crucially, hits the
    // `case 'select'` branch — never `default` (a default would emit a jsError, tripping
    // the zero-jsError audit). The unknown DN is comma-containing so getElementById is
    // exercised on the byte-identical lookup path (ADR-004 D5), returning empty.
    await page.evaluate((id) => window.bridge.dispatch({ type: 'select', id }), selectDn);
    const UNKNOWN_SELECT_DN = 'CN=NoSuch,OU=Phantom,DC=groupweaver,DC=invalid';
    await page.evaluate(
      (id) => window.bridge.dispatch({ type: 'select', id }), UNKNOWN_SELECT_DN);
    const cmdUnknown = await page.evaluate(() => {
      const cy = window.__cy;
      return { selectedCount: cy.nodes(':selected').length, anyDim: cy.nodes('.gw-dim').length };
    });
    assert(cmdUnknown.selectedCount === 0 && cmdUnknown.anyDim === 0,
      `{type:'select'} of an UNKNOWN DN '${UNKNOWN_SELECT_DN}' must clearSelection (getElementById empty -> clear, never a stale highlight): selected=${cmdUnknown.selectedCount}, dim=${cmdUnknown.anyDim}`);
    phase("select command with an unknown DN clears (hits 'select' case, not default)");

    // --- ADR-027 D3: the accent ring HIDES on an unknown {type:'select'} --------
    // An unknown DN -> getElementById empty -> clearSelection -> ring hidden (never a
    // stale ring frozen over the previous selection).
    const ringAfterUnknownSelect = await accentRingStateOf(page);
    assert(ringAfterUnknownSelect.exists && ringAfterUnknownSelect.hidden === true,
      `accent ring after {type:'select'} of an unknown DN: #gw-accent-ring must be HIDDEN (clearSelection), got hidden=${ringAfterUnknownSelect.hidden}`);
    phase('accent ring hidden after unknown-DN select command');

    // (4) the WHOLE select block is INSTANT — no camera animate, no enter tween fired.
    const cmdSelectMotion = await page.evaluate(() => ({
      animateCalls: window.__gwAnimateCalls,
      enterCount: (window.__gwEnterAnims || []).length,
    }));
    assert(cmdSelectMotion.animateCalls === 0 && cmdSelectMotion.enterCount === 0,
      `the select command must be INSTANT (ADR-020: addClass/removeClass only, never cy.animate): __gwAnimateCalls ${cmdSelectMotion.animateCalls}, __gwEnterAnims ${cmdSelectMotion.enterCount} (both must be 0)`);
    phase('select command block is instant (#88 motion counters untouched)');

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
    // Braces: do NOT implicitly return cy.emit()'s cyclic collection (CDP
    // serialization wedge - see the find-clear note in the ADR-023 block).
    await page.evaluate(() => { window.__cy.emit('tap', { target: window.__cy }); });
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
    // Braces: do NOT implicitly return cy.emit()'s cyclic collection (CDP
    // serialization wedge - see the find-clear note in the ADR-023 block).
    await page.evaluate(() => { window.__cy.emit('tap', { target: window.__cy }); });
    const finalClear = await page.evaluate(() => ({
      selectedCount: window.__cy.nodes(':selected').length,
      anyDim: window.__cy.nodes('.gw-dim').length,
      anyHover: window.__cy.nodes('.gw-hover').length,
    }));
    assert(finalClear.selectedCount === 0 && finalClear.anyDim === 0 && finalClear.anyHover === 0,
      `interaction block must leave a CLEAN state for the downstream dbltap/focus phases: selected=${finalClear.selectedCount}, dim=${finalClear.anyDim}, hover=${finalClear.anyHover}`);
    phase('graph-selection screenshot (select + dim + selective labels, then cleared)');

    // =========================================================================
    // ADR-023 (#WP-B): in-graph control cluster + find-a-node (web-layer).
    // Placed here on purpose: the interaction block above left a CLEAN state
    // (no selection/dim/hover, asserted) and the camera at the post-graphCommit
    // fit, and the downstream focus phases reset __gwAnimateCalls before they
    // use it - so this block may freely move the camera and select a node. The
    // run has emitted ZERO 'focused' messages so far (every focus phase is
    // BELOW), which makes the "Fit/Zoom/Find are 'focused'-silent" asserts a
    // clean before/after delta over allMessages. ids/classes mirror the shipped
    // src/App/web/index.html + graph.js verbatim (read, not guessed):
    //   #controls (pointer-events:auto), #find-input, #find-no-match (.no-match,
    //   toggled via the `hidden` ATTRIBUTE - graph.js sets noMatchEl.hidden, and
    //   the CSS is `.no-match[hidden]{display:none}`; there is NO `.hidden`
    //   class), #fit-btn, #zoom-in-btn/#zoom-out-btn (.zoom-btn), #labels-btn
    //   (aria-pressed + "Labels: auto"<->"Labels: all"). Find: ONE nodeClick +
    //   applySelection + a LOCAL cy.animate fit, NEVER focusOn/'focused'. Note
    //   that Find's frame IS a real core cy.animate, so it DOES bump
    //   __gwAnimateCalls (asserted only for Fit/Zoom that it does NOT) - the
    //   load-bearing Find invariant is zero 'focused', not the animate counter.

    // (1) Cluster renders: #controls + each control exists, is visible, and the
    // interactive overlay is pointer-events:auto while #legend stays :none. A
    // control is "visible" = a non-zero client rect AND display !== 'none'
    // (getBoundingClientRect is viewport-relative; position:fixed bottom-right).
    const controlsDom = await page.evaluate(() => {
      const ids = ['controls', 'find-input', 'fit-btn', 'zoom-in-btn', 'zoom-out-btn', 'labels-btn', 'find-no-match'];
      const out = {};
      for (const id of ids) {
        const el = document.getElementById(id);
        if (!el) { out[id] = { present: false }; continue; }
        const box = el.getBoundingClientRect();
        const cs = getComputedStyle(el);
        out[id] = {
          present: true,
          width: box.width, height: box.height,
          left: box.left, top: box.top, right: box.right, bottom: box.bottom,
          display: cs.display, visibility: cs.visibility,
          pointerEvents: cs.pointerEvents,
        };
      }
      out.__viewport = { innerWidth: window.innerWidth, innerHeight: window.innerHeight };
      out.__legendPE = getComputedStyle(document.getElementById('legend')).pointerEvents;
      return out;
    });
    // Every control PRESENT.
    for (const id of ['controls', 'find-input', 'fit-btn', 'zoom-in-btn', 'zoom-out-btn', 'labels-btn', 'find-no-match']) {
      assert(controlsDom[id].present,
        `ADR-023 (1): #${id} must exist in the shipped bundle (control cluster missing)`);
    }
    // Every control EXCEPT the no-match affordance is visible right now (the
    // no-match starts hidden via the `hidden` attribute - asserted separately).
    for (const id of ['controls', 'find-input', 'fit-btn', 'zoom-in-btn', 'zoom-out-btn', 'labels-btn']) {
      const c = controlsDom[id];
      assert(c.width > 0 && c.height > 0 && c.display !== 'none' && c.visibility !== 'hidden',
        `ADR-023 (1): #${id} must be visible (non-zero box, display!='none', visibility!='hidden'): ${JSON.stringify(c)}`);
    }
    // #find-no-match is PRESENT but starts HIDDEN (display:none via [hidden]);
    // it becomes visible only on a no-match query (asserted in (4)).
    assert(controlsDom['find-no-match'].display === 'none',
      `ADR-023 (1): #find-no-match must start HIDDEN (display:none via the [hidden] attribute), got display '${controlsDom['find-no-match'].display}'`);
    // ADR-035 D3 (#223): the parallel AT channel - a visually-hidden aria-live status
    // region. It must EXIST with role=status (implicit polite) AND an explicit
    // aria-live=polite. Structural presence pinned here; the runtime text-write
    // behavior is proven in (4) (failed Find => "No match", resolve => cleared) and the
    // issuesAllClearProbe ("No issues"). NOTE: on the flagged demo fixture the region is
    // NOT empty here - syncIssuesButton mirrors the visible button label ("Issues only")
    // into the region on load (announce is called in BOTH branches, graph.js), so this
    // check pins the element/role/aria-live only, not the transient text.
    const statusDom = await page.evaluate(() => {
      const el = document.getElementById('gw-status');
      return el ? {
        present: true,
        role: el.getAttribute('role'),
        ariaLive: el.getAttribute('aria-live'),
        textIsString: typeof el.textContent === 'string',
      } : { present: false };
    });
    assert(statusDom.present,
      'ADR-035 D3 (1): #gw-status (the aria-live status region) must exist in the shipped bundle');
    assert(statusDom.role === 'status' && statusDom.ariaLive === 'polite',
      `ADR-035 D3 (1): #gw-status must be role=status + aria-live=polite: ${JSON.stringify(statusDom)}`);
    assert(statusDom.textIsString,
      `ADR-035 D3 (1): #gw-status must be a text-only region (a writable textContent): ${JSON.stringify(statusDom)}`);
    // The cluster sits bottom-right, fully within the viewport (mirror of the
    // legend airspace assert): box.right <= innerWidth, box.bottom <= innerHeight,
    // and RIGHT of viewport center (the legend owns the top-left, controls the
    // bottom-right - they must not fight for the same corner).
    const cbox = controlsDom['controls'];
    assert(cbox.right <= controlsDom.__viewport.innerWidth + 0.5 && cbox.bottom <= controlsDom.__viewport.innerHeight + 0.5,
      `ADR-023 (1): #controls must stay fully within the viewport (right ${cbox.right} <= ${controlsDom.__viewport.innerWidth}, bottom ${cbox.bottom} <= ${controlsDom.__viewport.innerHeight})`);
    assert(cbox.left > controlsDom.__viewport.innerWidth / 2,
      `ADR-023 (1): #controls must sit RIGHT of viewport center (bottom-right cluster, never over the legend's top-left): box.left ${cbox.left} <= innerWidth/2 ${controlsDom.__viewport.innerWidth / 2}`);
    // The interactive-overlay invariant: #controls is pointer-events:auto (the
    // FIRST interactive bundle overlay) while #legend stays pointer-events:none
    // (taps fall through to the canvas). This is THE ADR-023 D1 keystone.
    assert(cbox.pointerEvents === 'auto',
      `ADR-023 (1): #controls must be pointer-events:auto (the first interactive bundle overlay - clicks land on the controls, not the canvas), got '${cbox.pointerEvents}'`);
    assert(controlsDom.__legendPE === 'none',
      `ADR-023 (1): #legend must STAY pointer-events:none alongside the new interactive #controls, got '${controlsDom.__legendPE}'`);
    phase('ADR-023 (1) control cluster renders (visible, bottom-right, pointer-events:auto; legend stays none)');

    // Helper: live 'focused' tally over allMessages (Fit/Zoom/Find must NOT move
    // it - that confirmation is the .NET focus protocol's alone, ADR-023 D3).
    const focusedCount = () => allMessages.filter((m) => m.type === 'focused').length;

    // (2) Fit: pre-zoom so Fit visibly changes the viewport, then click #fit-btn.
    // The camera must change (zoom differs from the zoomed-in state) AND neither
    // __gwAnimateCalls (Fit is a synchronous cy.fit, not cy.animate) nor the
    // 'focused' tally may move (Fit is local + bridge-silent, ADR-023 D2).
    await page.evaluate(() => {
      // Zoom in hard from the current fit so Fit has somewhere to return FROM.
      window.__cy.zoom(window.__cy.zoom() * 3);
      window.__gwAnimateCalls = 0;
      window.__gwAnimateLastDuration = null;
    });
    const preFit = await page.evaluate(() => ({ zoom: window.__cy.zoom() }));
    const focusedBeforeFit = focusedCount();
    // Bound every #controls click with MESSAGE_TIMEOUT_MS (defensive, CI run
    // 27977999334): a future reflow stall fails fast with a clear message
    // instead of silently eating the 300s watchdog. The standalone Playwright
    // lib's default action timeout is 0 (unbounded), so this is the only bound.
    await page.click('#fit-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const postFit = await page.evaluate(() => ({
      zoom: window.__cy.zoom(),
      animateCalls: window.__gwAnimateCalls,
    }));
    assert(Math.abs(postFit.zoom - preFit.zoom) > 1e-6,
      `ADR-023 (2): clicking #fit-btn must CHANGE the camera (cy.zoom ${preFit.zoom} -> ${postFit.zoom} unchanged - Fit did nothing?)`);
    assert(postFit.animateCalls === 0,
      `ADR-023 (2): Fit must be a LOCAL synchronous cy.fit, NOT an eased cy.animate (ADR-017 motion counter must stay 0): __gwAnimateCalls ${postFit.animateCalls} != 0`);
    assert(focusedCount() === focusedBeforeFit,
      `ADR-023 (2): Fit must be bridge-SILENT - no 'focused' message may be emitted (that channel is the .NET focus protocol's): 'focused' tally ${focusedBeforeFit} -> ${focusedCount()}`);
    phase(`ADR-023 (2) Fit changes the camera, motion counter + 'focused' untouched`);

    // (3) Zoom +/-: #zoom-in-btn RAISES cy.zoom(), #zoom-out-btn LOWERS it; both
    // clamp within [minZoom, maxZoom]; no bridge traffic. controlZoom is a
    // synchronous cy.zoom (not cy.animate), so __gwAnimateCalls stays 0 too.
    await page.evaluate(() => { window.__gwAnimateCalls = 0; });
    const zoomBounds = await page.evaluate(() => ({
      min: window.__cy.minZoom(), max: window.__cy.maxZoom(), z0: window.__cy.zoom(),
    }));
    const focusedBeforeZoom = focusedCount();
    await page.click('#zoom-in-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const afterIn = await page.evaluate(() => ({ z: window.__cy.zoom() }));
    assert(afterIn.z > zoomBounds.z0,
      `ADR-023 (3): #zoom-in-btn must RAISE cy.zoom (${zoomBounds.z0} -> ${afterIn.z})`);
    await page.click('#zoom-out-btn', { timeout: MESSAGE_TIMEOUT_MS });
    await page.click('#zoom-out-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const afterOut = await page.evaluate(() => ({ z: window.__cy.zoom() }));
    assert(afterOut.z < afterIn.z,
      `ADR-023 (3): #zoom-out-btn must LOWER cy.zoom (${afterIn.z} -> ${afterOut.z})`);
    // Clamp tripwire that ACTUALLY bites: the demo cy uses cytoscape's default
    // (effectively unbounded) min/max zoom (~1e-50 / 1e50), so spamming buttons
    // would never reach the clamp. Install TIGHT bounds around the current zoom,
    // spam each direction past them, and assert controlZoom's Math.max/min held
    // the result inside [min, max]. Bounds are restored afterward so the rest of
    // the run sees the production config. (cy.maxZoom(n)/cy.minZoom(n) setters.)
    const clamp = await page.evaluate(() => {
      const cy = window.__cy;
      const savedMin = cy.minZoom();
      const savedMax = cy.maxZoom();
      const z = cy.zoom();
      const tightMax = z * 1.5;
      const tightMin = z / 1.5;
      cy.maxZoom(tightMax);
      cy.minZoom(tightMin);
      // Spam in past tightMax (1.2^10 ~ 6.2x >> 1.5x), then out past tightMin.
      for (let i = 0; i < 10; i++) { document.getElementById('zoom-in-btn').click(); }
      const hi = cy.zoom();
      for (let i = 0; i < 20; i++) { document.getElementById('zoom-out-btn').click(); }
      const lo = cy.zoom();
      cy.minZoom(savedMin);
      cy.maxZoom(savedMax);
      return { tightMax, tightMin, hi, lo };
    });
    assert(clamp.hi <= clamp.tightMax + 1e-6,
      `ADR-023 (3): zoom-in must CLAMP at cy.maxZoom (${clamp.tightMax}): after 10 in-clicks cy.zoom ${clamp.hi} exceeded it (Math.min clamp missing?)`);
    assert(clamp.lo >= clamp.tightMin - 1e-6,
      `ADR-023 (3): zoom-out must CLAMP at cy.minZoom (${clamp.tightMin}): after 20 out-clicks cy.zoom ${clamp.lo} fell below it (Math.max clamp missing?)`);
    const zoomMotion = await page.evaluate(() => window.__gwAnimateCalls);
    assert(zoomMotion === 0,
      `ADR-023 (3): Zoom buttons must use a synchronous cy.zoom, never cy.animate: __gwAnimateCalls ${zoomMotion} != 0`);
    assert(focusedCount() === focusedBeforeZoom,
      `ADR-023 (3): Zoom must be bridge-SILENT - no 'focused' may be emitted: 'focused' tally ${focusedBeforeZoom} -> ${focusedCount()}`);
    // Restore the fit so the camera state is clean for the find frame below.
    await page.evaluate(() => { window.__cy.fit(window.__cy.elements(), 80); });
    phase(`ADR-023 (3) Zoom +/- raises/lowers + clamps within [${zoomBounds.min}, ${zoomBounds.max}], bridge-silent`);

    // (4) Find. Subjects derived from the rendered fixture (never hard-coded), so
    // they survive demo-baseline drift: a node whose exact Name (data('label'))
    // lookup is unambiguous, and a full comma-containing DN. The bundle's
    // findNode prefers an exact label/id match (all 196 demo labels are unique),
    // so each subject resolves to ITSELF. Find sends exactly ONE nodeClick with
    // that node's id, selects it (applySelection - same neighborhood dim as a
    // tap), and emits ZERO 'focused'. We drive Enter on #find-input (the bundle's
    // submit gesture) and consume the single nodeClick off the FIFO each time.
    const findByNameNode = fixture.nodes.find((x) => !x.root && x.label && x.label.length >= 3);
    assert(findByNameNode !== undefined,
      'ADR-023 (4): fixture must contain a non-root node with a usable Name for find-by-name');
    // A DIFFERENT node for the DN test (distinct from the name subject) so the
    // comma-DN value lookup is genuinely independent of the name path - and a
    // comma-containing DN so it proves the comma-safe value compare (ADR-004 D5).
    const findByDnNode = fixture.nodes.find((x) =>
      x.id.includes(',') && !x.root && x.id !== findByNameNode.id);
    assert(findByDnNode !== undefined,
      'ADR-023 (4): fixture must contain a comma-containing DN (distinct from the name subject) for find-by-DN');

    // Drive Find via the #find-input value + a real Enter keydown (the shipped
    // submit gesture). We focus the input and drive it with DOCUMENT-level
    // page.keyboard.* (the same hang-free mechanism the ADR-023 (6) keyboard
    // block uses at ~lines 2120/2131/2151/2162), NOT selector-targeted
    // page.fill/page.press. Reason (CI run 27977999334): page.fill/page.press
    // re-run actionability auto-wait (which is UNBOUNDED in the standalone
    // Playwright lib) against #find-input every call; once a prior no-match
    // un-hides #find-no-match the #controls flex-column reflows and the
    // cytoscape canvas keeps repainting, so on the slow 2-core CI runner
    // Playwright never observes a "stable" frame and the wait blocks until the
    // watchdog fires. keyboard.type dispatches keys without selector
    // actionability. We set focus once, clear any prior value, then type the
    // query and press Enter. The adjacency map (built for the selection phase
    // above) gives the expected neighborhood dim.
    async function driveFind(query, label) {
      const focusedBefore = focusedCount();
      await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
      // Clear any residual value deterministically (no selector re-actionability).
      await page.evaluate(() => {
        const i = document.getElementById('find-input');
        i.value = '';
        i.dispatchEvent(new Event('input', { bubbles: true }));
      });
      await page.keyboard.type(query);
      await page.keyboard.press('Enter');
      return { focusedBefore, label };
    }

    // --- Find by NAME --------------------------------------------------------
    const fbn = await driveFind(findByNameNode.label, 'name');
    const fbnClick = await awaitMessage('nodeClick', `find-by-name Enter on '${findByNameNode.label}'`);
    assert(fbnClick.id === findByNameNode.id,
      `ADR-023 (4) find-by-name: ONE nodeClick must carry the matched node's id '${findByNameNode.id}', got '${fbnClick.id}'`);
    const fbnState = await page.evaluate((a) => {
      const cy = window.__cy;
      const n = cy.getElementById(a.id);
      return {
        found: n.length === 1,
        selected: n.selected(),
        selectedCount: cy.nodes(':selected').length,
        selfDim: n.hasClass('gw-dim'),
        noMatchHidden: document.getElementById('find-no-match').hidden,
        // ADR-035 D3 (#223): a successful find calls announce('') to RESOLVE any prior
        // "No match", so the live region must be empty (cleared) on a hit.
        statusText: document.getElementById('gw-status').textContent,
      };
    }, { id: findByNameNode.id });
    assert(fbnState.found && fbnState.selected && fbnState.selectedCount === 1 && !fbnState.selfDim,
      `ADR-023 (4) find-by-name: matched node '${findByNameNode.id}' must become :selected (exactly one selected, self un-dimmed via applySelection): ${JSON.stringify(fbnState)}`);
    assert(fbnState.noMatchHidden === true,
      `ADR-023 (4) find-by-name: a successful match must keep #find-no-match HIDDEN (hidden attribute true), got hidden=${fbnState.noMatchHidden}`);
    assert(fbnState.statusText === '',
      `ADR-035 D3 (4) find-by-name: a successful find must CLEAR the #gw-status live region (announce('')), got '${fbnState.statusText}'`);
    assert(focusedCount() === fbn.focusedBefore,
      `ADR-023 (4) find-by-name: Find must NEVER emit 'focused' (it frames LOCALLY, never focusOn): 'focused' tally ${fbn.focusedBefore} -> ${focusedCount()}`);
    // Exactly ONE nodeClick was produced (the FIFO is now empty for that type).
    const fbnExtraClicks = (pendingByType.get('nodeClick') || []).length;
    assert(fbnExtraClicks === 0,
      `ADR-023 (4) find-by-name: Find must send EXACTLY ONE nodeClick, found ${fbnExtraClicks} extra queued`);
    phase(`ADR-023 (4) find by Name selects '${findByNameNode.id}' (one nodeClick, zero focused)`);

    // --- Find by DN (full comma-containing DN proves the comma-safe lookup) ---
    const fbd = await driveFind(findByDnNode.id, 'dn');
    const fbdClick = await awaitMessage('nodeClick', `find-by-DN Enter on '${findByDnNode.id}'`);
    assert(fbdClick.id === findByDnNode.id,
      `ADR-023 (4) find-by-DN: ONE nodeClick must carry the matched DN '${findByDnNode.id}' byte-identically (value compare, not selector concatenation - ADR-004 D5), got '${fbdClick.id}'`);
    const fbdState = await page.evaluate((a) => {
      const cy = window.__cy;
      const n = cy.getElementById(a.id);
      return { selected: n.selected(), selectedCount: cy.nodes(':selected').length };
    }, { id: findByDnNode.id });
    assert(fbdState.selected && fbdState.selectedCount === 1,
      `ADR-023 (4) find-by-DN: matched DN '${findByDnNode.id}' must become the sole :selected node: ${JSON.stringify(fbdState)}`);
    assert(focusedCount() === fbd.focusedBefore,
      `ADR-023 (4) find-by-DN: Find by DN must also emit ZERO 'focused': tally ${fbd.focusedBefore} -> ${focusedCount()}`);
    const fbdExtraClicks = (pendingByType.get('nodeClick') || []).length;
    assert(fbdExtraClicks === 0,
      `ADR-023 (4) find-by-DN: Find must send EXACTLY ONE nodeClick, found ${fbdExtraClicks} extra queued`);
    phase(`ADR-023 (4) find by full comma-DN selects '${findByDnNode.id}' (comma-safe, one nodeClick, zero focused)`);

    // --- No-match: a junk query shows #find-no-match (NOT hidden) and produces
    // ZERO new bridge traffic (no nodeClick, no focused, nothing). Snapshot the
    // TOTAL message count before/after to prove total bridge silence.
    const JUNK_QUERY = 'zzz__no_such_node__qqq__adr023';
    assert(!fixture.nodes.some((x) =>
      (x.label || '').toLowerCase().includes(JUNK_QUERY.toLowerCase()) || x.id.toLowerCase().includes(JUNK_QUERY.toLowerCase())),
      `ADR-023 (4) no-match: the junk query '${JUNK_QUERY}' must not be a substring of any fixture Name/DN (else it would match)`);
    const msgCountBeforeJunk = allMessages.length;
    // Same hang-free document-keyboard driving as driveFind (no selector
    // actionability against the reflowing #controls box - CI run 27977999334).
    await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
    await page.evaluate(() => {
      const i = document.getElementById('find-input');
      i.value = '';
      i.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await page.keyboard.type(JUNK_QUERY);
    await page.keyboard.press('Enter');
    const junkState = await page.evaluate(() => {
      const el = document.getElementById('find-no-match');
      return {
        hidden: el.hidden,
        display: getComputedStyle(el).display,
        selectedCount: window.__cy.nodes(':selected').length,
        // ADR-035 D3 (#223): a failed Find calls announce('No match') to write the
        // parallel AT channel. The preceding successful find-by-name/DN left it ''
        // (asserted above), so this "No match" is an observable clear -> set flip.
        statusText: document.getElementById('gw-status').textContent,
      };
    });
    assert(junkState.hidden === false && junkState.display !== 'none',
      `ADR-023 (4) no-match: a junk query must SHOW #find-no-match (hidden attribute false, display!='none'): ${JSON.stringify(junkState)}`);
    assert(junkState.statusText === 'No match',
      `ADR-035 D3 (4) no-match: a failed Find must write "No match" to the #gw-status live region (announce('No match')), got '${junkState.statusText}'`);
    assert(allMessages.length === msgCountBeforeJunk,
      `ADR-023 (4) no-match: a no-match must produce ZERO bridge traffic (no nodeClick/focused/anything): message count ${msgCountBeforeJunk} -> ${allMessages.length}`);
    phase('ADR-023 (4) no-match shows #find-no-match + announces "No match" (zero bridge traffic)');

    // Clear the find input + selection so the labels phase + downstream
    // dbltap/focus phases start clean. Esc clears+blurs the input (and re-hides
    // the no-match affordance); a background tap clears the selection/dim.
    //
    // HARDENING (CI run 27977999334): the preceding no-match un-hid
    // #find-no-match, growing the #controls flex column. Driving the input via
    // selector-targeted page.press('#find-input', 'Escape') re-runs Playwright's
    // actionability auto-wait against the just-moved input - unbounded in the
    // standalone Playwright lib. We instead focus + document-level keyboard.press
    // (the same mechanism the ADR-023 (6) block uses below), which dispatches the
    // Escape keydown the bundle listens for WITHOUT any selector-actionability
    // wait. NOTE: this is the secondary hardening - the watchdog-eating hang was
    // actually the NEXT statement (see the load-bearing brace below).
    await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
    await page.keyboard.press('Escape');
    // Braces are LOAD-BEARING (the actual #117/run-27977999334 hang, found by
    // bisecting DIAG phase logs): `() => cy.emit('tap', ...)` IMPLICITLY returns
    // the value of emit() - the cytoscape CORE collection, a huge cyclic object.
    // Playwright's page.evaluate then tries to returnByValue-serialize that graph
    // over CDP. Right here that runs while the just-shown #find-no-match has
    // grown the #controls flex column and the canvas is mid-repaint; on a
    // contended 2-core runner the serialization wedges the renderer thread and
    // the CDP round-trip never returns - the 300s watchdog fires (last completed
    // phase: "no-match shows #find-no-match"). The brace makes the arrow return
    // undefined, so nothing is serialized. This is the IDENTICAL trap already
    // documented + fixed for the dbltap emit below (~line 2280; runs
    // 27409858814 / 27419366522). The page.focus + keyboard.press above are also
    // hardened (no selector actionability against the reflowing box), but the
    // serialization wedge was the silent hang.
    await page.evaluate(() => { window.__cy.emit('tap', { target: window.__cy }); });
    const findCleared = await page.evaluate(() => ({
      inputValue: document.getElementById('find-input').value,
      noMatchHidden: document.getElementById('find-no-match').hidden,
      selectedCount: window.__cy.nodes(':selected').length,
      anyDim: window.__cy.nodes('.gw-dim').length,
    }));
    assert(findCleared.inputValue === '' && findCleared.noMatchHidden === true,
      `ADR-023 (4): Esc in #find-input must CLEAR the value and re-hide #find-no-match: value '${findCleared.inputValue}', noMatchHidden ${findCleared.noMatchHidden}`);
    assert(findCleared.selectedCount === 0 && findCleared.anyDim === 0,
      `ADR-023 (4): post-find state must be clean (no selection/dim) for downstream phases: selected ${findCleared.selectedCount}, dim ${findCleared.anyDim}`);
    phase('ADR-023 (4) find input + selection cleared (clean state)');

    // (5) Labels toggle: click #labels-btn -> aria-pressed="true", label
    // "Labels: all", every cy.node carries gw-labels-all, and its effective
    // min-zoomed-font-size is 0 (the rule drops the ADR-018 fit-zoom gate). Then
    // exercise persistence across a graphUpdate: re-feed the SAME fixture (all
    // ids survive => no fade, no camera move) and assert the nodes STILL carry
    // gw-labels-all (re-applied via applyLabelMode() inside sendLoaded). Toggle
    // back -> class removed, aria-pressed="false", label "Labels: auto".
    const beforeToggle = await page.evaluate(() => {
      const btn = document.getElementById('labels-btn');
      return { ariaPressed: btn.getAttribute('aria-pressed'), text: btn.textContent.trim() };
    });
    assert(beforeToggle.ariaPressed === 'false' && /labels:\s*auto/i.test(beforeToggle.text),
      `ADR-023 (5): #labels-btn must start in the 'auto' state (aria-pressed=false, "Labels: auto"): ${JSON.stringify(beforeToggle)}`);
    await page.click('#labels-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const labelsOn = await page.evaluate(() => {
      const btn = document.getElementById('labels-btn');
      const nodes = window.__cy.nodes();
      let allHaveClass = true;
      let mzfsMax = 0;
      nodes.forEach((n) => {
        if (!n.hasClass('gw-labels-all')) { allHaveClass = false; }
        const m = Number(n.style('min-zoomed-font-size'));
        if (m > mzfsMax) { mzfsMax = m; }
      });
      return {
        ariaPressed: btn.getAttribute('aria-pressed'),
        text: btn.textContent.trim(),
        allHaveClass,
        nodeCount: nodes.length,
        mzfsMax,
      };
    });
    assert(labelsOn.ariaPressed === 'true' && /labels:\s*all/i.test(labelsOn.text),
      `ADR-023 (5): after one click #labels-btn must be the 'all' state (aria-pressed=true, "Labels: all"): ${JSON.stringify(labelsOn)}`);
    assert(labelsOn.allHaveClass && labelsOn.nodeCount === fixture.nodes.length,
      `ADR-023 (5): Labels:all must add gw-labels-all to EVERY cy node (${labelsOn.nodeCount} nodes, allHaveClass=${labelsOn.allHaveClass})`);
    assert(Math.abs(labelsOn.mzfsMax) < 1e-6,
      `ADR-023 (5): with gw-labels-all every node's effective min-zoomed-font-size must be 0 (label gate dropped), got max ${labelsOn.mzfsMax}`);
    phase('ADR-023 (5) Labels:all toggles class + drops min-zoomed-font-size to 0');

    // Persistence across graphUpdate: re-feed the identical fixture set + commit
    // with graphUpdate. All ids survive (no new nodes => no F1 enter fade), the
    // viewport is untouched (ADR-005 D1), and applyLabelMode() re-asserts the
    // 'all' mode inside sendLoaded so the re-added nodes STILL carry the class.
    await page.evaluate(() => { window.__gwAnimateCalls = 0; window.__gwEnterAnims = []; });
    for (const chunk of toChunks(fixture.nodes, fixture.edges)) {
      await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
    }
    await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
    await awaitMessage('loaded', 'ADR-023 (5): graphUpdate -> labelMode persistence check');
    const labelsAfterUpdate = await page.evaluate(() => {
      const btn = document.getElementById('labels-btn');
      const nodes = window.__cy.nodes();
      let allHaveClass = true;
      nodes.forEach((n) => { if (!n.hasClass('gw-labels-all')) { allHaveClass = false; } });
      return {
        ariaPressed: btn.getAttribute('aria-pressed'),
        allHaveClass,
        nodeCount: nodes.length,
        animateCalls: window.__gwAnimateCalls,
        enterCount: (window.__gwEnterAnims || []).length,
      };
    });
    assert(labelsAfterUpdate.allHaveClass && labelsAfterUpdate.nodeCount === fixture.nodes.length,
      `ADR-023 (5): Labels:all must SURVIVE a graphUpdate - the re-added nodes must STILL carry gw-labels-all (re-applied via applyLabelMode in sendLoaded): allHaveClass=${labelsAfterUpdate.allHaveClass}, nodes=${labelsAfterUpdate.nodeCount}`);
    assert(labelsAfterUpdate.ariaPressed === 'true',
      `ADR-023 (5): #labels-btn aria-pressed must remain 'true' across the graphUpdate (labelMode is module-level state), got '${labelsAfterUpdate.ariaPressed}'`);
    assert(labelsAfterUpdate.animateCalls === 0 && labelsAfterUpdate.enterCount === 0,
      `ADR-023 (5): the identical-set graphUpdate must add NO new nodes (all survivors) - no camera move, no enter fade: __gwAnimateCalls ${labelsAfterUpdate.animateCalls}, __gwEnterAnims ${labelsAfterUpdate.enterCount}`);
    phase('ADR-023 (5) Labels:all survives graphUpdate (re-applied in sendLoaded)');

    // Toggle back -> 'auto': class removed from every node, aria-pressed=false,
    // label "Labels: auto". Restores the default label gate for downstream phases.
    await page.click('#labels-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const labelsOff = await page.evaluate(() => {
      const btn = document.getElementById('labels-btn');
      const anyHaveClass = window.__cy.nodes('.gw-labels-all').length;
      return {
        ariaPressed: btn.getAttribute('aria-pressed'),
        text: btn.textContent.trim(),
        anyHaveClass,
      };
    });
    assert(labelsOff.ariaPressed === 'false' && /labels:\s*auto/i.test(labelsOff.text),
      `ADR-023 (5): a second click must return #labels-btn to 'auto' (aria-pressed=false, "Labels: auto"): ${JSON.stringify(labelsOff)}`);
    assert(labelsOff.anyHaveClass === 0,
      `ADR-023 (5): toggling back to 'auto' must REMOVE gw-labels-all from every node, ${labelsOff.anyHaveClass} still carry it`);
    phase('ADR-023 (5) Labels toggle back to auto removes the class');

    // =========================================================================
    // WP3b (#142): the "Issues only" graph filter (#issues-btn). Placed right
    // after the Labels toggle - the same control-cluster phase, with the camera
    // at the canonical fit and a CLEAN selection (the labels block restored it,
    // asserted). Subjects are derived from the rendered fixture (the demo
    // baseline has 19 findings -> flagged nodes present): a flagged sev-only DN,
    // a roll-up (below) DN, and a CLEAN node (neither sev nor below). ids/labels/
    // predicate read VERBATIM from the shipped src/App/web/index.html + graph.js:
    //   #issues-btn (after #labels-btn in #controls), aria-pressed, label
    //   "Issues only" (off) / "Issues: on" (on); nodeHasIssue(n) = sev||below;
    //   applyIssuesFilter hides clean nodes (.hide() => display:none), keeps
    //   sev||below; controlToggleIssues guards on anyIssues(); revealIfHiddenByFilter
    //   clears the filter when Find/select targets a hidden node. The all-clear
    //   guard (zero-flagged) is the SEPARATE issuesAllClearProbe (the demo run can
    //   never reach zero findings).

    // (1) #issues-btn renders: present, visible, INSIDE #controls (after #labels-btn),
    // starts the 'off' state (aria-pressed=false, label "Issues only").
    const issuesDom = await page.evaluate(() => {
      const btn = document.getElementById('issues-btn');
      if (!btn) { return { present: false }; }
      const box = btn.getBoundingClientRect();
      const cs = getComputedStyle(btn);
      const inControls = !!btn.closest('#controls');
      // The shipped order is #labels-btn then #issues-btn in the same controls-row.
      const labelsBtn = document.getElementById('labels-btn');
      const afterLabels = !!(labelsBtn
        && (labelsBtn.compareDocumentPosition(btn) & Node.DOCUMENT_POSITION_FOLLOWING));
      return {
        present: true,
        width: box.width, height: box.height,
        display: cs.display, visibility: cs.visibility,
        inControls, afterLabels,
        ariaPressed: btn.getAttribute('aria-pressed'),
        text: btn.textContent.trim(),
      };
    });
    assert(issuesDom.present,
      'WP3b (1): #issues-btn must exist in the shipped bundle (issues-only toggle missing)');
    assert(issuesDom.width > 0 && issuesDom.height > 0 && issuesDom.display !== 'none' && issuesDom.visibility !== 'hidden',
      `WP3b (1): #issues-btn must be visible (non-zero box, display!='none', visibility!='hidden'): ${JSON.stringify(issuesDom)}`);
    assert(issuesDom.inControls && issuesDom.afterLabels,
      `WP3b (1): #issues-btn must sit INSIDE #controls, AFTER #labels-btn (shipped order): inControls=${issuesDom.inControls}, afterLabels=${issuesDom.afterLabels}`);
    assert(issuesDom.ariaPressed === 'false' && issuesDom.text === 'Issues only',
      `WP3b (1): #issues-btn must start OFF (aria-pressed=false, label "Issues only"): ${JSON.stringify(issuesDom)}`);
    phase('WP3b (1) #issues-btn renders inside #controls after #labels-btn, starts "Issues only" / off');

    // Subjects from the rendered fixture (never hard-coded - survive baseline drift).
    // A flagged sev-only node, a roll-up (below) node, and a CLEAN node (the
    // applyIssuesFilter predicate's two keep-cases + the hide-case).
    const flaggedSevNode = fixture.nodes.find((x) => x.sev && !x.below);
    const rollupNode = fixture.nodes.find((x) => x.below && !x.sev);
    const cleanNode = fixture.nodes.find((x) => !x.sev && !x.below && !x.root && x.id.includes(','));
    assert(flaggedSevNode !== undefined,
      'WP3b: demo fixture must contain a sev-only flagged node (the 19-finding baseline)');
    assert(rollupNode !== undefined,
      'WP3b: demo fixture must contain a roll-up (below) node for the keep-on-rollup case');
    assert(cleanNode !== undefined,
      'WP3b: demo fixture must contain a clean comma-DN node (neither sev nor below) for the hide case');

    // Helper: per-node visibility + the button state in one round-trip. Counts the
    // visible/hidden split over the flagged-vs-clean partition so the keep/hide
    // assertions are exact, not sampled. Pure cy reads (.visible()) + DOM reads.
    const issuesSnapshot = () => page.evaluate((subjects) => {
      const cy = window.__cy;
      const btn = document.getElementById('issues-btn');
      const vis = (id) => cy.getElementById(id).visible();
      let cleanVisible = 0;
      let cleanHidden = 0;
      let flaggedVisible = 0;
      let flaggedHidden = 0;
      cy.nodes().forEach((n) => {
        const flagged = !!n.data('sev') || !!n.data('below');
        if (flagged) {
          if (n.visible()) { flaggedVisible += 1; } else { flaggedHidden += 1; }
        } else if (n.visible()) { cleanVisible += 1; } else { cleanHidden += 1; }
      });
      return {
        ariaPressed: btn.getAttribute('aria-pressed'),
        text: btn.textContent.trim(),
        flaggedSevVisible: vis(subjects.flaggedSev),
        rollupVisible: vis(subjects.rollup),
        cleanVisible: vis(subjects.clean),
        cleanVisibleCount: cleanVisible,
        cleanHiddenCount: cleanHidden,
        flaggedVisibleCount: flaggedVisible,
        flaggedHiddenCount: flaggedHidden,
      };
    }, { flaggedSev: flaggedSevNode.id, rollup: rollupNode.id, clean: cleanNode.id });

    // (2) Toggle ON: clean nodes hide (.visible()===false), flagged (sev) and
    // roll-up (below) nodes stay visible; button -> aria-pressed=true / "Issues: on".
    const beforeIssuesOn = await issuesSnapshot();
    assert(beforeIssuesOn.cleanVisible && beforeIssuesOn.flaggedSevVisible && beforeIssuesOn.rollupVisible,
      `WP3b (2): before the toggle every node (clean + flagged + roll-up) must be visible: ${JSON.stringify(beforeIssuesOn)}`);
    await page.click('#issues-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const issuesOn = await issuesSnapshot();
    assert(issuesOn.ariaPressed === 'true' && issuesOn.text === 'Issues: on',
      `WP3b (2): after one click #issues-btn must be ON (aria-pressed=true, label "Issues: on"): ${JSON.stringify(issuesOn)}`);
    assert(issuesOn.flaggedSevVisible === true,
      `WP3b (2): a flagged (sev) node '${flaggedSevNode.id}' must STAY visible under issues-only: ${JSON.stringify(issuesOn)}`);
    assert(issuesOn.rollupVisible === true,
      `WP3b (2): a roll-up (below) node '${rollupNode.id}' must STAY visible under issues-only (the path to a finding survives): ${JSON.stringify(issuesOn)}`);
    assert(issuesOn.cleanVisible === false,
      `WP3b (2): a clean node '${cleanNode.id}' (neither sev nor below) must be HIDDEN (.visible()===false) under issues-only: ${JSON.stringify(issuesOn)}`);
    // Whole-partition exactness: NO flagged/roll-up node is hidden, and >= 1 clean
    // node was hidden (the filter actually engaged, not a no-op).
    assert(issuesOn.flaggedHiddenCount === 0,
      `WP3b (2): NO flagged-or-roll-up node may be hidden under issues-only, ${issuesOn.flaggedHiddenCount} were: ${JSON.stringify(issuesOn)}`);
    assert(issuesOn.cleanHiddenCount >= 1 && issuesOn.cleanVisibleCount === 0,
      `WP3b (2): EVERY clean node must hide under issues-only (hidden ${issuesOn.cleanHiddenCount} >= 1, still-visible ${issuesOn.cleanVisibleCount} must be 0): ${JSON.stringify(issuesOn)}`);
    phase(`WP3b (2) Issues:on hides ${issuesOn.cleanHiddenCount} clean nodes, keeps ${issuesOn.flaggedVisibleCount} flagged/roll-up`);

    // (3) Toggle OFF: every node visible again; aria-pressed=false / "Issues only".
    await page.click('#issues-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const issuesOff = await issuesSnapshot();
    assert(issuesOff.ariaPressed === 'false' && issuesOff.text === 'Issues only',
      `WP3b (3): a second click must return #issues-btn to OFF (aria-pressed=false, "Issues only"): ${JSON.stringify(issuesOff)}`);
    assert(issuesOff.cleanHiddenCount === 0 && issuesOff.flaggedHiddenCount === 0
      && issuesOff.cleanVisible && issuesOff.flaggedSevVisible && issuesOff.rollupVisible,
      `WP3b (3): toggling OFF must make EVERY node visible again (cy.nodes().show()): ${JSON.stringify(issuesOff)}`);
    phase('WP3b (3) Issues:off restores every node to visible');

    // (4) With the filter ON, a graphUpdate that ADDS a CLEAN node -> that node is
    // hidden (re-applied via applyIssuesFilter inside sendLoaded), flagged stay
    // visible. Mirror of the labels-all-survives-graphUpdate assert: re-feed the
    // fixture + a brand-new clean node, commit with graphUpdate. Turn the filter
    // back ON first (the run left it OFF after (3)).
    await page.click('#issues-btn', { timeout: MESSAGE_TIMEOUT_MS });
    const issuesReOn = await issuesSnapshot();
    assert(issuesReOn.ariaPressed === 'true' && issuesReOn.text === 'Issues: on',
      `WP3b (4): re-arming the filter must turn #issues-btn ON before the graphUpdate persistence check: ${JSON.stringify(issuesReOn)}`);
    // A genuinely-new CLEAN node placed clear of every fixture node (no overlap),
    // distinct comma-DN. Reuses the maxAbs spacing idiom from the reduced-motion probe.
    const issuesMaxAbs = fixture.nodes.reduce(
      (acc, x) => Math.max(acc, Math.abs(x.x), Math.abs(x.y)), 0);
    const ISSUES_NEW_CLEAN = 'CN=Issues New Clean,OU=IssuesFilter,DC=groupweaver,DC=invalid';
    const issuesNewNode = {
      id: ISSUES_NEW_CLEAN, label: 'Issues New Clean', kind: 'User',
      x: issuesMaxAbs + 211.5, y: -(issuesMaxAbs + 173.25),
    };
    const issuesUpdatedNodes = [...fixture.nodes, issuesNewNode];
    for (const chunk of toChunks(issuesUpdatedNodes, fixture.edges)) {
      await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
    }
    await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
    await awaitMessage('loaded', 'WP3b (4): graphUpdate adds a clean node under issues-only');
    const afterUpdate = await page.evaluate((a) => {
      const cy = window.__cy;
      const btn = document.getElementById('issues-btn');
      return {
        ariaPressed: btn.getAttribute('aria-pressed'),
        text: btn.textContent.trim(),
        newNodeFound: cy.getElementById(a.newId).length === 1,
        newNodeVisible: cy.getElementById(a.newId).visible(),
        flaggedSevVisible: cy.getElementById(a.flaggedSev).visible(),
        rollupVisible: cy.getElementById(a.rollup).visible(),
      };
    }, { newId: ISSUES_NEW_CLEAN, flaggedSev: flaggedSevNode.id, rollup: rollupNode.id });
    assert(afterUpdate.newNodeFound,
      `WP3b (4): the freshly added node '${ISSUES_NEW_CLEAN}' must be present after the graphUpdate`);
    assert(afterUpdate.newNodeVisible === false,
      `WP3b (4): a CLEAN node added via graphUpdate while issues-only is ON must be HIDDEN (applyIssuesFilter re-applied in sendLoaded): visible=${afterUpdate.newNodeVisible}`);
    assert(afterUpdate.flaggedSevVisible && afterUpdate.rollupVisible,
      `WP3b (4): flagged + roll-up nodes must STAY visible across the graphUpdate: ${JSON.stringify(afterUpdate)}`);
    assert(afterUpdate.ariaPressed === 'true' && afterUpdate.text === 'Issues: on',
      `WP3b (4): #issues-btn must remain ON across the graphUpdate (issuesOnly is module-level state): ${JSON.stringify(afterUpdate)}`);
    phase('WP3b (4) Issues:on survives graphUpdate - the new clean node hides, flagged stay');

    // (5) Find/select a node hidden by the filter -> revealIfHiddenByFilter clears
    // the filter (aria-pressed=false), the target becomes visible + selected, and
    // the existing no-extra-bridge-traffic invariants hold (the {type:'select'}
    // reverse-sync is fire-and-forget: zero new messages). The filter is still ON
    // from (4); the clean node added in (4) is the perfect hidden target.
    const hiddenTarget = ISSUES_NEW_CLEAN;
    const hiddenBefore = await page.evaluate((id) => ({
      visible: window.__cy.getElementById(id).visible(),
      ariaPressed: document.getElementById('issues-btn').getAttribute('aria-pressed'),
    }), hiddenTarget);
    assert(hiddenBefore.visible === false && hiddenBefore.ariaPressed === 'true',
      `WP3b (5): the select target must start HIDDEN with the filter ON: ${JSON.stringify(hiddenBefore)}`);
    const msgCountBeforeReveal = allMessages.length;
    await page.evaluate((id) => window.bridge.dispatch({ type: 'select', id }), hiddenTarget);
    const afterReveal = await page.evaluate((id) => {
      const cy = window.__cy;
      const n = cy.getElementById(id);
      return {
        ariaPressed: document.getElementById('issues-btn').getAttribute('aria-pressed'),
        text: document.getElementById('issues-btn').textContent.trim(),
        targetVisible: n.visible(),
        targetSelected: n.selected(),
        selectedCount: cy.nodes(':selected').length,
        anyHidden: cy.nodes().filter((x) => !x.visible()).length,
      };
    }, hiddenTarget);
    assert(afterReveal.ariaPressed === 'false' && afterReveal.text === 'Issues only',
      `WP3b (5): selecting a filter-hidden node must CLEAR the filter (revealIfHiddenByFilter -> aria-pressed=false, "Issues only"): ${JSON.stringify(afterReveal)}`);
    assert(afterReveal.targetVisible === true,
      `WP3b (5): the reverse-selected hidden node '${hiddenTarget}' must become visible (filter cleared so the jump lands on a visible node): ${JSON.stringify(afterReveal)}`);
    assert(afterReveal.targetSelected === true && afterReveal.selectedCount === 1,
      `WP3b (5): the reverse-selected node must be the sole :selected node (applySelection still applies): ${JSON.stringify(afterReveal)}`);
    assert(afterReveal.anyHidden === 0,
      `WP3b (5): clearing the filter must make EVERY node visible again, ${afterReveal.anyHidden} still hidden`);
    // The {type:'select'} reverse-sync is fire-and-forget: it must emit ZERO new
    // bridge messages (the existing no-extra-traffic invariant for the select command).
    assert(allMessages.length === msgCountBeforeReveal,
      `WP3b (5): a reverse {type:'select'} (even one that clears the filter) must produce ZERO new bridge traffic: message count ${msgCountBeforeReveal} -> ${allMessages.length}`);
    phase('WP3b (5) selecting a filter-hidden node clears the filter, target visible + selected, no extra bridge traffic');

    // Restore the graph to the canonical fixture set + a fully clean state for the
    // downstream keyboard/dbltap/focus phases: clear the selection, re-feed the
    // exact fixture (drops the synthetic clean node) with graphUpdate, and refit.
    // The filter is already OFF (cleared in (5)); the button reads "Issues only".
    await page.evaluate(() => window.bridge.dispatch({ type: 'select', id: '' }));
    for (const chunk of toChunks(fixture.nodes, fixture.edges)) {
      await page.evaluate((cmd) => window.bridge.dispatch(cmd), chunk);
    }
    await page.evaluate(() => window.bridge.dispatch({ type: 'graphUpdate' }));
    await awaitMessage('loaded', 'WP3b: restore canonical fixture after the issues-filter block');
    await page.evaluate(() => { window.__cy.fit(window.__cy.elements(), 80); });
    const issuesClean = await page.evaluate(() => ({
      selectedCount: window.__cy.nodes(':selected').length,
      anyDim: window.__cy.nodes('.gw-dim').length,
      anyHidden: window.__cy.nodes().filter((x) => !x.visible()).length,
      issuesAria: document.getElementById('issues-btn').getAttribute('aria-pressed'),
      nodeCount: window.__cy.nodes().length,
    }));
    assert(issuesClean.selectedCount === 0 && issuesClean.anyDim === 0
      && issuesClean.anyHidden === 0 && issuesClean.issuesAria === 'false'
      && issuesClean.nodeCount === fixture.nodes.length,
      `WP3b: the issues-filter block must leave a CLEAN state (no selection/dim/hidden, filter off, canonical node count) for downstream phases: ${JSON.stringify(issuesClean)}`);
    phase('WP3b issues-filter block verified + clean canonical state restored');
    // =========================================================================

    // (6) Keyboard (web-layer, document keydown - ADR-023 D5). Playwright injects
    // real key events at the browser level, so the document listener fires.
    //   - Ctrl+F focuses #find-input (and preventDefault stops the browser find).
    //   - Ctrl+0 fits (camera changes, no 'focused').
    //   - a plain '-' while NOT focused in find zooms out; a '-' TYPED while
    //     #find-input is focused must NOT change zoom (the suppression branch).
    // Ctrl+F: focus the find input.
    await page.evaluate(() => { document.getElementById('find-input').blur(); });
    await page.keyboard.press('Control+f');
    const ctrlF = await page.evaluate(() => ({
      active: document.activeElement && document.activeElement.id,
    }));
    assert(ctrlF.active === 'find-input',
      `ADR-023 (6): Ctrl+F must make #find-input the document.activeElement, got '${ctrlF.active}'`);
    phase('ADR-023 (6) Ctrl+F focuses the find input');

    // '-' WHILE find is focused must be suppressed (it is normal typing): zoom
    // unchanged. (Ctrl+F left the input focused.)
    //
    // SETTLE BARRIER (found by capturing cy.animated() at the sample point): an
    // earlier eased viewport tween - the find-by-name/DN controlFind fit
    // (cy.animate({fit}, 280ms), ADR-023 D3) - can still be DRAINING here. With
    // the old page.fill/page.press find-driving its actionability *stability*
    // wait happened to absorb that tween; the hang-free keyboard driving above
    // does not, so on a slow runner the run reaches this assert with
    // cy.animated()===true and cy.zoom() drifting frame-to-frame. The '-' is
    // correctly SUPPRESSED (the active-element assert below proves focus never
    // left #find-input, so the bundle's typing-guard short-circuits controlZoom),
    // but the drifting tween moves zoom between the before/after reads and trips
    // the 1e-9 tolerance. Draining the camera to rest first removes the timing
    // confound WITHOUT touching what is asserted: on a settled camera, the only
    // thing that can move zoom is the keypress - exactly the suppression under
    // test. Bounded so a stuck tween fails fast, never the 300s watchdog.
    await page.waitForFunction(() => !window.__cy.animated(), null, { timeout: MESSAGE_TIMEOUT_MS });
    const zoomBeforeTypedMinus = await page.evaluate(() => window.__cy.zoom());
    await page.keyboard.press('-');
    const zoomAfterTypedMinus = await page.evaluate(() => ({
      zoom: window.__cy.zoom(),
      active: document.activeElement && document.activeElement.id,
    }));
    assert(zoomAfterTypedMinus.active === 'find-input',
      `ADR-023 (6): typing '-' in #find-input must keep it focused (typing, not a zoom gesture), active '${zoomAfterTypedMinus.active}'`);
    assert(Math.abs(zoomAfterTypedMinus.zoom - zoomBeforeTypedMinus) < 1e-9,
      `ADR-023 (6): a '-' typed WHILE #find-input is focused must NOT zoom (suppressed while typing): cy.zoom ${zoomBeforeTypedMinus} -> ${zoomAfterTypedMinus.zoom}`);
    // Clear the stray '-' the input now holds and blur so the next plain-key
    // assert runs with focus OFF the find box.
    await page.evaluate(() => {
      const i = document.getElementById('find-input');
      i.value = '';
      i.blur();
    });
    phase("ADR-023 (6) '-' typed in find is suppressed (no zoom)");

    // Plain '-' while NOT focused in find: zooms out (the document keydown acts).
    const zoomBeforePlainMinus = await page.evaluate(() => window.__cy.zoom());
    await page.keyboard.press('-');
    const zoomAfterPlainMinus = await page.evaluate(() => window.__cy.zoom());
    assert(zoomAfterPlainMinus < zoomBeforePlainMinus - 1e-9,
      `ADR-023 (6): a plain '-' while NOT in the find box must zoom OUT: cy.zoom ${zoomBeforePlainMinus} -> ${zoomAfterPlainMinus}`);
    phase("ADR-023 (6) plain '-' (find unfocused) zooms out");

    // Ctrl+0 fits: pre-zoom, then Ctrl+0 must return to the fit (camera changes)
    // with no 'focused' emitted (Ctrl+0 -> controlFit, local + bridge-silent).
    await page.evaluate(() => { window.__cy.zoom(window.__cy.zoom() * 2.5); });
    const preCtrl0 = await page.evaluate(() => window.__cy.zoom());
    const focusedBeforeCtrl0 = focusedCount();
    await page.keyboard.press('Control+0');
    const postCtrl0 = await page.evaluate(() => window.__cy.zoom());
    assert(Math.abs(postCtrl0 - preCtrl0) > 1e-6,
      `ADR-023 (6): Ctrl+0 must FIT (change the camera): cy.zoom ${preCtrl0} -> ${postCtrl0} unchanged`);
    assert(focusedCount() === focusedBeforeCtrl0,
      `ADR-023 (6): Ctrl+0 Fit must be bridge-silent (no 'focused'): tally ${focusedBeforeCtrl0} -> ${focusedCount()}`);
    phase('ADR-023 (6) Ctrl+0 fits (camera changes, no focused)');

    // Restore the canonical fit + a fully clean state for the downstream
    // dbltap/focus phases (selection cleared, camera at fit, no labels-all).
    await page.evaluate(() => {
      window.__cy.emit('tap', { target: window.__cy });
      window.__cy.fit(window.__cy.elements(), 80);
    });
    const adr023Clean = await page.evaluate(() => ({
      selectedCount: window.__cy.nodes(':selected').length,
      anyDim: window.__cy.nodes('.gw-dim').length,
      anyLabelsAll: window.__cy.nodes('.gw-labels-all').length,
    }));
    assert(adr023Clean.selectedCount === 0 && adr023Clean.anyDim === 0 && adr023Clean.anyLabelsAll === 0,
      `ADR-023: the control block must leave a CLEAN state for downstream phases: selected ${adr023Clean.selectedCount}, dim ${adr023Clean.anyDim}, labels-all ${adr023Clean.anyLabelsAll}`);
    // Screenshot the control cluster frame for the ui-verifier (docs/ui-checklist §A).
    await page.screenshot({ path: join(screenshotDir, 'graph-controls.png') });
    phase('ADR-023 control cluster verified + clean state restored (graph-controls.png)');
    // =========================================================================

    // =========================================================================
    // WP3c (#144): the Ctrl+K command palette. Placed right after the ADR-023
    // control block (clean state restored above) and BEFORE the expand/focus
    // phases below, mirroring the ADR-023 (4)/(6) find+keyboard idioms it builds
    // on (the palette reuses #find-input + selectAndFrame/controlFind). ids and
    // classes are read VERBATIM from the shipped src/App/web (index.html +
    // graph.js), not guessed:
    //   #find-input (role=combobox, aria-expanded/aria-controls), #palette-results
    //   (role=listbox, toggled via the `hidden` ATTRIBUTE - CSS
    //   `#palette-results[hidden]{display:none}`), rows <li class="palette-item">
    //   with .palette-label + .palette-hint, highlighted row class .gw-active.
    //   Quick-action names: "Fit to view" / "Toggle labels" / "Issues only" /
    //   "Expand selected node" (PALETTE_ACTIONS in graph.js ->
    //   controlFit/controlToggleLabels/controlToggleIssues/controlExpandSelected;
    //   the 4th action is the discoverability slice's keyboard-reachable twin of the
    //   dbltap gesture — it sends the EXISTING {type:'nodeExpand'} when an expandable
    //   External node is accent-selected, else it is a bridge-silent no-op).
    //   Node Enter => selectAndFrame (the SAME path as Find:
    //   exactly one nodeClick + applySelection + a LOCAL frame, ZERO 'focused');
    //   action Enter => its handler, ZERO bridge traffic. The harness drives the
    //   palette the hang-free way the ADR-023 (4) block established: focus
    //   #find-input once, set value + dispatch an 'input' event (the bundle opens
    //   the palette lazily on first input), then DOCUMENT-level page.keyboard.*
    //   for Arrow/Enter/Esc (never selector-targeted page.fill/press against the
    //   reflowing #controls box - CI run 27977999334).
    //
    // The four pinned action names (must match graph.js PALETTE_ACTIONS verbatim).
    // "Expand selected node" is the discoverability slice's 4th action (added to the
    // pin deliberately, not a weakening — the empty-query palette now lists FOUR
    // actions and the ACTIONS-ONLY every(...) assert below must accept it).
    const PALETTE_FIT = 'Fit to view';
    const PALETTE_TOGGLE_LABELS = 'Toggle labels';
    const PALETTE_ISSUES = 'Issues only';
    const PALETTE_EXPAND = 'Expand selected node';
    const PALETTE_ACTION_NAMES = [PALETTE_FIT, PALETTE_TOGGLE_LABELS, PALETTE_ISSUES, PALETTE_EXPAND];

    // Read the live palette row model: each row's kind (node|action via the
    // .palette-hint text), label + hint text, gw-active highlight, aria-selected.
    // Pure DOM reads off #palette-results <li.palette-item> rows.
    const readPalette = () => page.evaluate(() => {
      const input = document.getElementById('find-input');
      const ul = document.getElementById('palette-results');
      const rows = Array.prototype.map.call(
        ul ? ul.querySelectorAll('li.palette-item') : [],
        (li) => ({
          label: (li.querySelector('.palette-label') || {}).textContent || '',
          hint: (li.querySelector('.palette-hint') || {}).textContent || '',
          active: li.classList.contains('gw-active'),
          ariaSelected: li.getAttribute('aria-selected'),
        }));
      return {
        active: document.activeElement && document.activeElement.id,
        ulPresent: ul !== null,
        ulHidden: ul ? ul.hidden : null,
        ariaExpanded: input ? input.getAttribute('aria-expanded') : null,
        // ADR-035 D2 (#223): the combobox owns aria-activedescendant, pointing at the
        // highlighted option's id (palette-opt-<i>). getAttribute (not the property)
        // so a MISSING attribute reads null - the closed / no-match contract.
        ariaActiveDescendant: input ? input.getAttribute('aria-activedescendant') : null,
        // Each row's id (renderPalette sets `palette-opt-<i>` per index) so the
        // active-descendant pin can be checked against the actual DOM ids.
        rowIds: Array.prototype.map.call(
          ul ? ul.querySelectorAll('li.palette-item') : [], (li) => li.id),
        inputValue: input ? input.value : null,
        rows,
      };
    });

    // Drive a query into the palette the hang-free way (focus once, set value +
    // dispatch 'input' so the bundle's input listener opens+rebuilds the palette).
    async function typePalette(query) {
      await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
      await page.evaluate((q) => {
        const i = document.getElementById('find-input');
        i.value = q;
        i.dispatchEvent(new Event('input', { bubbles: true }));
      }, query);
    }

    // The palette markup must exist in the shipped bundle (role=listbox, combobox
    // wiring on #find-input) and its CSS `[hidden]` rule must collapse it to
    // display:none. The preceding ADR-023 (6) block ends with a Ctrl+F that — now
    // that Ctrl+F is a palette OPEN alias (WP3c) — leaves the palette OPEN, so we
    // first close it deterministically (focus + Esc) to assert the CLOSED contract
    // from a known baseline (this also re-proves Esc closes ahead of the dedicated
    // (5) Esc pin). Mirrors the ADR-023 (1) render pin.
    await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
    await page.keyboard.press('Escape');
    await page.evaluate(() => { document.getElementById('find-input').blur(); });
    const paletteDom = await page.evaluate(() => {
      const ul = document.getElementById('palette-results');
      const input = document.getElementById('find-input');
      return {
        ulPresent: ul !== null,
        ulRole: ul ? ul.getAttribute('role') : null,
        ulHidden: ul ? ul.hidden : null,
        ulDisplay: ul ? getComputedStyle(ul).display : null,
        inputRole: input ? input.getAttribute('role') : null,
        ariaControls: input ? input.getAttribute('aria-controls') : null,
        ariaExpanded: input ? input.getAttribute('aria-expanded') : null,
      };
    });
    assert(paletteDom.ulPresent && paletteDom.ulRole === 'listbox',
      `WP3c (0): #palette-results must exist with role=listbox in the shipped bundle: ${JSON.stringify(paletteDom)}`);
    assert(paletteDom.ulHidden === true && paletteDom.ulDisplay === 'none',
      `WP3c (0): a CLOSED #palette-results must be hidden (hidden attribute + display:none via the [hidden] CSS rule): ${JSON.stringify(paletteDom)}`);
    assert(paletteDom.inputRole === 'combobox' && paletteDom.ariaControls === 'palette-results' && paletteDom.ariaExpanded === 'false',
      `WP3c (0): #find-input must be a combobox wired to #palette-results, aria-expanded=false when closed: ${JSON.stringify(paletteDom)}`);
    phase('WP3c (0) palette markup present (role=listbox, [hidden] collapses, combobox wiring)');

    // (1) Ctrl+K OPENS: #find-input gains focus, #palette-results un-hides, and
    // an EMPTY query shows the action rows (no nodes). Blur first so the focus
    // change is observable. Ctrl+K is the primary opener (Cmd+K alias on mac).
    await page.evaluate(() => { document.getElementById('find-input').blur(); });
    await page.keyboard.press('Control+k');
    const ctrlK = await readPalette();
    assert(ctrlK.active === 'find-input',
      `WP3c (1): Ctrl+K must focus #find-input, got '${ctrlK.active}'`);
    assert(ctrlK.ulHidden === false && ctrlK.ariaExpanded === 'true',
      `WP3c (1): Ctrl+K must OPEN #palette-results (un-hidden) and set aria-expanded=true: ${JSON.stringify({ ulHidden: ctrlK.ulHidden, ariaExpanded: ctrlK.ariaExpanded })}`);
    // Empty query => action rows ONLY (every action present, no node rows). A node
    // row's hint is `Kind · DN`; an action row's hint is the action's secondary
    // text, never containing the ` · ` node separator - so action rows have NO
    // ` · ` in their hint and their label equals an action name.
    const emptyLabels = ctrlK.rows.map((r) => r.label);
    for (const name of PALETTE_ACTION_NAMES) {
      assert(emptyLabels.includes(name),
        `WP3c (1): an empty-query palette must list the action '${name}', got rows ${JSON.stringify(emptyLabels)}`);
    }
    assert(ctrlK.rows.every((r) => PALETTE_ACTION_NAMES.includes(r.label)),
      `WP3c (1): an empty-query palette must show ACTIONS ONLY (no node rows), got ${JSON.stringify(emptyLabels)}`);
    phase('WP3c (1) Ctrl+K opens + focuses #find-input, empty query shows action rows only');

    // Close it again (Esc) so (2) drives a fresh open via typing.
    await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
    await page.keyboard.press('Escape');

    // (2) Typing lists NODE + ACTION matches. The query "lab" is a substring of the
    // action name "Toggle labels" AND of the `LAB-` computer node labels in the demo
    // fixture, so one query yields both row classes. Derived from the fixture (NOT
    // hard-coded) so it survives label drift: pick a query that is a substring of
    // some action name AND matches >= 1 fixture node. Among the action names' word
    // fragments, "lab" (from "labels") is the demo's shared token; assert generically
    // that the chosen query produces >= 1 node row (with a `Kind · DN` hint) AND the
    // matching action row.
    const SHARED_QUERY = 'lab';
    const sharedActionName = PALETTE_ACTION_NAMES.find((n) => n.toLowerCase().includes(SHARED_QUERY));
    assert(sharedActionName === PALETTE_TOGGLE_LABELS,
      `WP3c (2): the shared query '${SHARED_QUERY}' must be a substring of exactly the "${PALETTE_TOGGLE_LABELS}" action name (PALETTE_ACTIONS drift?), resolved '${sharedActionName}'`);
    const sharedNodeMatches = fixture.nodes.filter((x) =>
      (x.label || '').toLowerCase().includes(SHARED_QUERY) || x.id.toLowerCase().includes(SHARED_QUERY));
    assert(sharedNodeMatches.length >= 1,
      `WP3c (2): the shared query '${SHARED_QUERY}' must match >= 1 fixture node (fixture drift?), matched ${sharedNodeMatches.length}`);
    await typePalette(SHARED_QUERY);
    const shared = await readPalette();
    assert(shared.ulHidden === false && shared.ariaExpanded === 'true',
      `WP3c (2): typing must keep the palette OPEN: ${JSON.stringify({ ulHidden: shared.ulHidden, ariaExpanded: shared.ariaExpanded })}`);
    // The action row for "Toggle labels" must be present (label == the action name,
    // hint has NO ` · ` node separator).
    const sharedActionRow = shared.rows.find((r) => r.label === PALETTE_TOGGLE_LABELS);
    assert(sharedActionRow !== undefined && !sharedActionRow.hint.includes(' · '),
      `WP3c (2): typing '${SHARED_QUERY}' must list the "${PALETTE_TOGGLE_LABELS}" ACTION row (action hint, no node ' · ' separator): rows ${JSON.stringify(shared.rows.map((r) => r.label))}`);
    // At least one NODE row: label is a fixture node Name whose label/id contains the
    // query, and the hint is `Kind · DN` (the node-row format from renderPalette).
    const sharedNodeRow = shared.rows.find((r) =>
      r.label !== PALETTE_TOGGLE_LABELS
      && r.hint.includes(' · ')
      && sharedNodeMatches.some((m) => (m.label || m.id) === r.label));
    assert(sharedNodeRow !== undefined,
      `WP3c (2): typing '${SHARED_QUERY}' must list >= 1 NODE row (a matching fixture Name with a 'Kind · DN' hint): rows ${JSON.stringify(shared.rows)}`);
    // The node-row hint must be exactly `Kind · DN` for the matched fixture node.
    const sharedNodeFixture = fixture.nodes.find((x) => (x.label || x.id) === sharedNodeRow.label);
    assert(sharedNodeFixture !== undefined
      && sharedNodeRow.hint === `${sharedNodeFixture.kind} · ${sharedNodeFixture.id}`,
      `WP3c (2): the node row's hint must be 'Kind · DN' ('${sharedNodeFixture ? sharedNodeFixture.kind + ' · ' + sharedNodeFixture.id : '??'}'), got '${sharedNodeRow.hint}'`);
    phase('WP3c (2) typing lists both a NODE row (Kind · DN hint) and the matching ACTION row');

    // (2a) ADR-035 D2 (#223): the ARIA combobox owns aria-activedescendant, tracking
    // the highlighted option (palette-opt-<i>). Four arms, all read from the LIVE DOM
    // (the implementer sets it centrally in renderPalette + clears it in closePalette):
    //   OPEN  => equals the highlighted option's id (renderPalette auto-highlights
    //            index 0 on a non-empty result set => palette-opt-0);
    //   NAV   => ArrowDown/ArrowUp move the highlight and the attribute TRACKS it
    //            (byte-identical to the gw-active row's id);
    //   CLOSED=> the attribute is ABSENT (closePalette removes it);
    //   NO-MATCH => a query with zero rows highlights nothing (paletteIndex -1) so the
    //            attribute is ABSENT (the central paletteIndex<0 clear).
    // The SHARED_QUERY palette from (2) is still OPEN with >= 2 rows, auto-highlighted
    // at index 0 - the OPEN baseline. Each rendered <li> must carry id palette-opt-<i>.
    const adOpen = await readPalette();
    assert(adOpen.rows.length >= 2,
      `WP3c (2a): the aria-activedescendant nav check needs >= 2 open palette rows (SHARED_QUERY '${SHARED_QUERY}'), got ${adOpen.rows.length}`);
    adOpen.rowIds.forEach((id, i) => assert(id === `palette-opt-${i}`,
      `WP3c (2a): each rendered option <li> must have id 'palette-opt-${i}' (renderPalette), got '${id}'`));
    // On open, index 0 is auto-highlighted => aria-activedescendant === palette-opt-0.
    assert(adOpen.rows[0].active === true,
      `WP3c (2a): an open palette must auto-highlight row 0 (gw-active): ${JSON.stringify(adOpen.rows.map((r) => r.active))}`);
    assert(adOpen.ariaActiveDescendant === 'palette-opt-0',
      `WP3c (2a) OPEN: #find-input aria-activedescendant must equal the highlighted option's id 'palette-opt-0', got '${adOpen.ariaActiveDescendant}'`);
    // ArrowDown moves the highlight to row 1; the attribute must TRACK it. (Reads the
    // gw-active row's own id from rowIds so the pin is against the actual DOM, not a
    // fixed index.)
    await page.keyboard.press('ArrowDown');
    const adDown = await readPalette();
    const adDownActive = adDown.rows.findIndex((r) => r.active === true);
    assert(adDownActive === 1,
      `WP3c (2a): ArrowDown must move the highlight from row 0 to row 1, active row is ${adDownActive}`);
    assert(adDown.ariaActiveDescendant === adDown.rowIds[adDownActive]
      && adDown.ariaActiveDescendant === 'palette-opt-1',
      `WP3c (2a) NAV down: aria-activedescendant must track the new highlight ('palette-opt-1' = the gw-active row id), got '${adDown.ariaActiveDescendant}'`);
    // ArrowUp moves it back to row 0; the attribute tracks back.
    await page.keyboard.press('ArrowUp');
    const adUp = await readPalette();
    const adUpActive = adUp.rows.findIndex((r) => r.active === true);
    assert(adUpActive === 0,
      `WP3c (2a): ArrowUp must move the highlight back to row 0, active row is ${adUpActive}`);
    assert(adUp.ariaActiveDescendant === adUp.rowIds[adUpActive]
      && adUp.ariaActiveDescendant === 'palette-opt-0',
      `WP3c (2a) NAV up: aria-activedescendant must track back to 'palette-opt-0' (the gw-active row id), got '${adUp.ariaActiveDescendant}'`);
    // CLOSED: Esc tears the list down (closePalette) => the attribute is ABSENT (null).
    await page.keyboard.press('Escape');
    const adClosed = await readPalette();
    assert(adClosed.ulHidden === true && adClosed.ariaExpanded === 'false',
      `WP3c (2a): precondition - Esc must close the palette before the closed-descendant check: ${JSON.stringify({ ulHidden: adClosed.ulHidden, ariaExpanded: adClosed.ariaExpanded })}`);
    assert(adClosed.ariaActiveDescendant === null,
      `WP3c (2a) CLOSED: a closed palette must REMOVE aria-activedescendant (closePalette), got '${adClosed.ariaActiveDescendant}'`);
    // NO-MATCH: a junk query yields zero rows => nothing highlighted => absent. Reuse
    // the same junk sentinel the ADR-023 (4) no-match block proves matches no fixture
    // node; the palette-node matcher (findNodes) also finds nothing, and no action
    // name contains it, so buildPaletteItems returns [] => paletteIndex -1.
    const AD_JUNK_QUERY = 'zzz__no_such_node__qqq__adr035';
    assert(!fixture.nodes.some((x) =>
      (x.label || '').toLowerCase().includes(AD_JUNK_QUERY.toLowerCase())
      || x.id.toLowerCase().includes(AD_JUNK_QUERY.toLowerCase()))
      && !PALETTE_ACTION_NAMES.some((n) => n.toLowerCase().includes(AD_JUNK_QUERY.toLowerCase())),
      `WP3c (2a): the junk query '${AD_JUNK_QUERY}' must match NO fixture node and NO action name (empty palette => nothing highlighted)`);
    await typePalette(AD_JUNK_QUERY);
    const adNoMatch = await readPalette();
    assert(adNoMatch.rows.length === 0,
      `WP3c (2a): the junk query '${AD_JUNK_QUERY}' must yield ZERO palette rows, got ${adNoMatch.rows.length}`);
    assert(adNoMatch.ariaActiveDescendant === null,
      `WP3c (2a) NO-MATCH: a zero-row palette must have NO aria-activedescendant (paletteIndex -1 => central clear), got '${adNoMatch.ariaActiveDescendant}'`);
    // Close the palette so (3) drives its own fresh open from a known baseline.
    await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
    await page.keyboard.press('Escape');
    phase('WP3c (2a) aria-activedescendant tracks the highlight (open/nav/closed/no-match)');

    // (3) Arrow + Enter invokes a NODE. Use a query that matches EXACTLY ONE node
    // and NO action (so the single result row is that node at index 0), then
    // ArrowDown (wraps to the sole row) + Enter must run the SAME path as Find:
    // exactly ONE nodeClick with that DN, the node :selected, zero 'focused'
    // (mirrors the ADR-023 (4) find-by-name asserts). Subject derived from the
    // fixture: a non-root node with a unique exact label that contains none of the
    // action-name substrings (so no action row joins the result set).
    const actionLower = PALETTE_ACTION_NAMES.map((n) => n.toLowerCase());
    const labelMatchesAnAction = (s) => actionLower.some((a) => {
      // share a >=3-char token? cheap: does the label contain any 3-gram of an action name?
      const t = s.toLowerCase();
      for (let i = 0; i + 3 <= a.length; i++) {
        const g = a.slice(i, i + 3).trim();
        if (g.length === 3 && t.includes(g)) { return true; }
      }
      return false;
    });
    // The query (the subject's full label) must resolve to EXACTLY ONE row: it must
    // be an exact label/id of the subject and NOT a substring of ANY OTHER node's
    // label or id (else those join as substring matches — e.g. the "Computers" OU
    // label is a substring of every "OU=Computers,..." child DN). Counts the live
    // matches the bundle's findNodes would see (exact-or-substring over label+id).
    const matchCountFor = (q) => {
      const ql = q.toLowerCase();
      return fixture.nodes.filter((x) =>
        (x.label || '').toLowerCase().includes(ql) || x.id.toLowerCase().includes(ql)).length;
    };
    const paletteNodeSubject = fixture.nodes.find((x) =>
      !x.root && x.label && x.label.length >= 3
      && !labelMatchesAnAction(x.label)
      && matchCountFor(x.label) === 1);
    assert(paletteNodeSubject !== undefined,
      'WP3c (3): fixture must contain a non-root node whose full label matches EXACTLY ONE node (not a substring of any other label/DN) and shares no 3-gram with any action name (single-row node palette query)');
    // Confirm the query yields no action row (so the single row is the node).
    const subjQ = paletteNodeSubject.label;
    await typePalette(subjQ);
    const beforeArrow = await readPalette();
    assert(beforeArrow.rows.length === 1 && beforeArrow.rows[0].label === paletteNodeSubject.label
      && beforeArrow.rows[0].hint.includes(' · '),
      `WP3c (3): an exact unique node query '${subjQ}' must yield EXACTLY ONE node row (no action rows): ${JSON.stringify(beforeArrow.rows)}`);
    const palFocusedBefore = focusedCount();
    await page.keyboard.press('ArrowDown');
    const afterArrow = await readPalette();
    assert(afterArrow.rows.length === 1 && afterArrow.rows[0].active === true && afterArrow.rows[0].ariaSelected === 'true',
      `WP3c (3): ArrowDown must highlight the sole node row (gw-active + aria-selected=true): ${JSON.stringify(afterArrow.rows)}`);
    await page.keyboard.press('Enter');
    const palNodeClick = await awaitMessage('nodeClick', `WP3c palette Enter on node '${paletteNodeSubject.label}'`);
    assert(palNodeClick.id === paletteNodeSubject.id,
      `WP3c (3): Enter on the node row must send ONE nodeClick with that node's id '${paletteNodeSubject.id}', got '${palNodeClick.id}'`);
    const palNodeState = await page.evaluate((id) => {
      const cy = window.__cy;
      const n = cy.getElementById(id);
      return {
        selected: n.selected(),
        selectedCount: cy.nodes(':selected').length,
        selfDim: n.hasClass('gw-dim'),
        paletteHidden: document.getElementById('palette-results').hidden,
      };
    }, paletteNodeSubject.id);
    assert(palNodeState.selected && palNodeState.selectedCount === 1 && !palNodeState.selfDim,
      `WP3c (3): the palette-selected node '${paletteNodeSubject.id}' must be the SOLE :selected node, self un-dimmed (selectAndFrame -> applySelection): ${JSON.stringify(palNodeState)}`);
    assert(focusedCount() === palFocusedBefore,
      `WP3c (3): a palette NODE invoke must emit ZERO 'focused' (selectAndFrame frames LOCALLY, never focusOn): tally ${palFocusedBefore} -> ${focusedCount()}`);
    const palExtraClicks = (pendingByType.get('nodeClick') || []).length;
    assert(palExtraClicks === 0,
      `WP3c (3): a palette NODE invoke must send EXACTLY ONE nodeClick, found ${palExtraClicks} extra queued`);
    assert(palNodeState.paletteHidden === true,
      `WP3c (3): invoking a row must CLOSE the palette (#palette-results hidden), got hidden=${palNodeState.paletteHidden}`);
    phase(`WP3c (3) Arrow+Enter invokes the NODE row '${paletteNodeSubject.id}' (one nodeClick, selected, zero focused)`);

    // Clear the selection the node invoke left, so the action invoke below starts
    // from a clean canvas (a background tap clears selection/dim; the palette is
    // already closed).
    await page.evaluate(() => { window.__cy.emit('tap', { target: window.__cy }); });

    // (4) Arrow + Enter invokes an ACTION (bridge-silent). Type "labels" so the
    // sole-or-first matching row is the "Toggle labels" action; navigate to it and
    // Enter. The gw-labels-all class must TOGGLE on (every node) AND zero new bridge
    // messages may be produced (the action path is bridge-silent). Then "issues" =>
    // #issues-btn aria-pressed flips, and "fit" => the camera fits (controlFit).
    // "labels" matches ONLY the Toggle-labels action among the three names AND no
    // demo node label (verified: no fixture label contains "labels"), so it is the
    // sole row and is auto-highlighted at index 0 - ArrowDown wraps back to it.
    const ACTION_QUERY_LABELS = 'labels';
    assert(PALETTE_ACTION_NAMES.filter((n) => n.toLowerCase().includes(ACTION_QUERY_LABELS)).length === 1,
      `WP3c (4): the query '${ACTION_QUERY_LABELS}' must match exactly ONE action name, matched ${PALETTE_ACTION_NAMES.filter((n) => n.toLowerCase().includes(ACTION_QUERY_LABELS)).length}`);
    assert(!fixture.nodes.some((x) =>
      (x.label || '').toLowerCase().includes(ACTION_QUERY_LABELS) || x.id.toLowerCase().includes(ACTION_QUERY_LABELS)),
      `WP3c (4): the query '${ACTION_QUERY_LABELS}' must match NO fixture node (so the sole row is the action)`);
    const labelsAllBefore = await page.evaluate(() => window.__cy.nodes('.gw-labels-all').length);
    assert(labelsAllBefore === 0,
      `WP3c (4): precondition - no node may carry gw-labels-all before the Toggle-labels action, got ${labelsAllBefore}`);
    const msgCountBeforeLabelsAction = allMessages.length;
    await typePalette(ACTION_QUERY_LABELS);
    const labelsRows = await readPalette();
    assert(labelsRows.rows.length === 1 && labelsRows.rows[0].label === PALETTE_TOGGLE_LABELS,
      `WP3c (4): the query '${ACTION_QUERY_LABELS}' must yield exactly the "${PALETTE_TOGGLE_LABELS}" action row: ${JSON.stringify(labelsRows.rows)}`);
    await page.keyboard.press('ArrowDown');
    const labelsHi = await readPalette();
    assert(labelsHi.rows[0] && labelsHi.rows[0].active === true,
      `WP3c (4): ArrowDown must highlight the "${PALETTE_TOGGLE_LABELS}" row: ${JSON.stringify(labelsHi.rows)}`);
    await page.keyboard.press('Enter');
    const labelsActionState = await page.evaluate(() => {
      const cy = window.__cy;
      const nodes = cy.nodes();
      let all = true;
      nodes.forEach((n) => { if (!n.hasClass('gw-labels-all')) { all = false; } });
      return {
        nodeCount: nodes.length,
        allHaveClass: all,
        anyHaveClass: cy.nodes('.gw-labels-all').length,
        btnAria: document.getElementById('labels-btn').getAttribute('aria-pressed'),
        paletteHidden: document.getElementById('palette-results').hidden,
      };
    });
    assert(labelsActionState.allHaveClass && labelsActionState.anyHaveClass === labelsActionState.nodeCount,
      `WP3c (4): the Toggle-labels ACTION must add gw-labels-all to EVERY node (controlToggleLabels): ${JSON.stringify(labelsActionState)}`);
    assert(labelsActionState.btnAria === 'true',
      `WP3c (4): the Toggle-labels action must flip #labels-btn aria-pressed=true (same handler as the button): got '${labelsActionState.btnAria}'`);
    assert(labelsActionState.paletteHidden === true,
      `WP3c (4): invoking the action must CLOSE the palette, got hidden=${labelsActionState.paletteHidden}`);
    assert(allMessages.length === msgCountBeforeLabelsAction,
      `WP3c (4): a palette ACTION invoke must be BRIDGE-SILENT (zero new messages): count ${msgCountBeforeLabelsAction} -> ${allMessages.length}`);
    phase('WP3c (4) Arrow+Enter on Toggle-labels toggles gw-labels-all, bridge-silent');

    // (4b) Optional extra: "issues" => the Issues-only action flips #issues-btn
    // aria-pressed (the demo fixture has flagged nodes so the all-clear guard does
    // NOT block the toggle), bridge-silent. "issues" matches only that action name
    // and no fixture node.
    const ACTION_QUERY_ISSUES = 'issues';
    assert(PALETTE_ACTION_NAMES.filter((n) => n.toLowerCase().includes(ACTION_QUERY_ISSUES)).length === 1
      && !fixture.nodes.some((x) =>
        (x.label || '').toLowerCase().includes(ACTION_QUERY_ISSUES) || x.id.toLowerCase().includes(ACTION_QUERY_ISSUES)),
      `WP3c (4b): the query '${ACTION_QUERY_ISSUES}' must match exactly the Issues-only action and no fixture node`);
    const issuesAriaBefore = await page.evaluate(() => document.getElementById('issues-btn').getAttribute('aria-pressed'));
    const msgCountBeforeIssuesAction = allMessages.length;
    await typePalette(ACTION_QUERY_ISSUES);
    const issuesRows = await readPalette();
    assert(issuesRows.rows.length === 1 && issuesRows.rows[0].label === PALETTE_ISSUES,
      `WP3c (4b): the query '${ACTION_QUERY_ISSUES}' must yield exactly the "${PALETTE_ISSUES}" action row: ${JSON.stringify(issuesRows.rows)}`);
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    const issuesActionState = await page.evaluate(() => ({
      btnAria: document.getElementById('issues-btn').getAttribute('aria-pressed'),
      paletteHidden: document.getElementById('palette-results').hidden,
    }));
    assert(issuesActionState.btnAria !== issuesAriaBefore,
      `WP3c (4b): the Issues-only action must FLIP #issues-btn aria-pressed (was '${issuesAriaBefore}', now '${issuesActionState.btnAria}')`);
    assert(allMessages.length === msgCountBeforeIssuesAction,
      `WP3c (4b): the Issues-only action invoke must be BRIDGE-SILENT: count ${msgCountBeforeIssuesAction} -> ${allMessages.length}`);
    phase('WP3c (4b) Issues-only action flips #issues-btn aria-pressed, bridge-silent');

    // (4c) Optional extra: "fit" => the Fit-to-view action fits the camera
    // (controlFit), bridge-silent and zero 'focused'. Pre-zoom so Fit visibly moves
    // the camera. "fit" matches only the Fit-to-view action and no fixture node.
    const ACTION_QUERY_FIT = 'fit';
    assert(PALETTE_ACTION_NAMES.filter((n) => n.toLowerCase().includes(ACTION_QUERY_FIT)).length === 1
      && !fixture.nodes.some((x) =>
        (x.label || '').toLowerCase().includes(ACTION_QUERY_FIT) || x.id.toLowerCase().includes(ACTION_QUERY_FIT)),
      `WP3c (4c): the query '${ACTION_QUERY_FIT}' must match exactly the Fit-to-view action and no fixture node`);
    await page.evaluate(() => { window.__cy.zoom(window.__cy.zoom() * 3); });
    const preFitAction = await page.evaluate(() => window.__cy.zoom());
    const focusedBeforeFitAction = focusedCount();
    const msgCountBeforeFitAction = allMessages.length;
    await typePalette(ACTION_QUERY_FIT);
    const fitRows = await readPalette();
    assert(fitRows.rows.length === 1 && fitRows.rows[0].label === PALETTE_FIT,
      `WP3c (4c): the query '${ACTION_QUERY_FIT}' must yield exactly the "${PALETTE_FIT}" action row: ${JSON.stringify(fitRows.rows)}`);
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    const postFitAction = await page.evaluate(() => ({
      zoom: window.__cy.zoom(),
      paletteHidden: document.getElementById('palette-results').hidden,
    }));
    assert(Math.abs(postFitAction.zoom - preFitAction) > 1e-6,
      `WP3c (4c): the Fit-to-view action must CHANGE the camera (controlFit): cy.zoom ${preFitAction} -> ${postFitAction.zoom} unchanged`);
    assert(focusedCount() === focusedBeforeFitAction,
      `WP3c (4c): the Fit-to-view action must be bridge-silent (no 'focused'): tally ${focusedBeforeFitAction} -> ${focusedCount()}`);
    assert(allMessages.length === msgCountBeforeFitAction,
      `WP3c (4c): the Fit-to-view action must produce ZERO bridge traffic: count ${msgCountBeforeFitAction} -> ${allMessages.length}`);
    phase('WP3c (4c) Fit-to-view action fits the camera, bridge-silent + zero focused');

    // (4d) discoverability slice: the "Expand selected node" ACTION is the keyboard-
    // reachable twin of the dbltap gesture. controlExpandSelected reads the accent-
    // selected node (accentSelectedId, set by applySelection on tap/select) and, when
    // it is an EXPANDABLE (kind==='External') node, sends the EXISTING
    // {type:'nodeExpand', id} — the SAME wire message the dbltap handler sends (NO new
    // message type). With nothing selected OR a non-expandable node selected it is a
    // BRIDGE-SILENT no-op. Three arms: (a) External selected => exactly ONE nodeExpand
    // for that id, (b) nothing selected => zero traffic, (c) non-External selected =>
    // zero traffic. Driven through the palette (query "expand"), mirroring (4)/(4b)/(4c).
    const ACTION_QUERY_EXPAND = 'expand';
    // Invariant guards (mirror the (4)/(4b)/(4c) pattern): "expand" must match exactly
    // the "Expand selected node" action name and NO fixture node, so the sole palette
    // row is that action (auto-highlighted at index 0; ArrowDown wraps back to it).
    assert(PALETTE_ACTION_NAMES.filter((n) => n.toLowerCase().includes(ACTION_QUERY_EXPAND)).length === 1,
      `WP3c (4d): the query '${ACTION_QUERY_EXPAND}' must match exactly ONE action name, matched ${PALETTE_ACTION_NAMES.filter((n) => n.toLowerCase().includes(ACTION_QUERY_EXPAND)).length}`);
    assert(!fixture.nodes.some((x) =>
      (x.label || '').toLowerCase().includes(ACTION_QUERY_EXPAND) || x.id.toLowerCase().includes(ACTION_QUERY_EXPAND)),
      `WP3c (4d): the query '${ACTION_QUERY_EXPAND}' must match NO fixture node (so the sole row is the action)`);
    // Subjects from the fixture (never hard-coded): an EXPANDABLE (External) node and a
    // NON-expandable (non-External) node — both must exist to exercise all three arms.
    const expandExtNode = fixture.nodes.find((x) => x.kind === 'External');
    assert(expandExtNode !== undefined,
      'WP3c (4d): the demo fixture must contain >= 1 External (expandable/frontier) node for the Expand-selected action (the ignored builtin member DNs)');
    const expandNonExtNode = fixture.nodes.find((x) => x.kind !== 'External');
    assert(expandNonExtNode !== undefined,
      'WP3c (4d): the demo fixture must contain a non-External node for the non-expandable no-op arm');

    // A tiny driver: type "expand", confirm the sole row IS the action, ArrowDown to
    // highlight it, Enter to invoke. Returns after the palette closes.
    async function invokeExpandAction() {
      await typePalette(ACTION_QUERY_EXPAND);
      const rows = await readPalette();
      assert(rows.rows.length === 1 && rows.rows[0].label === PALETTE_EXPAND,
        `WP3c (4d): the query '${ACTION_QUERY_EXPAND}' must yield exactly the "${PALETTE_EXPAND}" action row: ${JSON.stringify(rows.rows)}`);
      await page.keyboard.press('ArrowDown');
      const hi = await readPalette();
      assert(hi.rows[0] && hi.rows[0].active === true,
        `WP3c (4d): ArrowDown must highlight the "${PALETTE_EXPAND}" row: ${JSON.stringify(hi.rows)}`);
      await page.keyboard.press('Enter');
    }

    // -- (4d-a) POSITIVE: an External node is accent-selected => ONE nodeExpand -----
    // Select the External node via the SAME clickTest tap path selection uses (it runs
    // applySelection => sets accentSelectedId). Drain the resulting nodeClick so the
    // downstream negative-arm counts start clean.
    await page.evaluate((id) => window.bridge.dispatch({ type: 'clickTest', id }), expandExtNode.id);
    await awaitMessage('nodeClick', `clickTest (select External) on '${expandExtNode.id}'`);
    const accentAfterExtSelect = await page.evaluate(() => document.getElementById('gw-accent-ring').hidden);
    assert(accentAfterExtSelect === false,
      `WP3c (4d-a): selecting the External node must show the accent ring (applySelection set accentSelectedId), got hidden=${accentAfterExtSelect}`);
    await invokeExpandAction();
    // The action must emit EXACTLY the existing {type:'nodeExpand', id} for that id —
    // NOT a new message type (wire protocol unchanged). awaitMessage('nodeExpand')
    // consumes it FIFO; the byte-identical id roundtrip proves the reused wire.
    const expandFromAction = await awaitMessage('nodeExpand',
      `Expand-selected action on External '${expandExtNode.id}'`);
    assert(expandFromAction.id === expandExtNode.id,
      `WP3c (4d-a): the Expand-selected action must send the EXISTING {type:'nodeExpand'} with the byte-identical id: got '${expandFromAction.id}', selected '${expandExtNode.id}'`);
    const afterExpandActionState = await page.evaluate(() => ({
      paletteHidden: document.getElementById('palette-results').hidden,
    }));
    assert(afterExpandActionState.paletteHidden === true,
      `WP3c (4d-a): invoking the Expand-selected action must CLOSE the palette, got hidden=${afterExpandActionState.paletteHidden}`);
    phase(`WP3c (4d-a) Expand-selected on an External node emits one nodeExpand ('${expandExtNode.id}')`);

    // -- (4d-b) NO-OP: nothing selected => zero bridge traffic ----------------------
    // Background-tap clears the selection (accentSelectedId -> null). Invoking the
    // action now must be a pure no-op: NO nodeExpand, NO other message.
    await page.evaluate(() => { window.__cy.emit('tap', { target: window.__cy }); });
    const accentAfterClear = await page.evaluate(() => document.getElementById('gw-accent-ring').hidden);
    assert(accentAfterClear === true,
      `WP3c (4d-b): a background tap must hide the accent ring (clearSelection => accentSelectedId=null), got hidden=${accentAfterClear}`);
    const msgCountBeforeNoSelExpand = allMessages.length;
    await invokeExpandAction();
    assert(allMessages.length === msgCountBeforeNoSelExpand,
      `WP3c (4d-b): the Expand-selected action with NOTHING selected must be BRIDGE-SILENT (zero new messages, no nodeExpand): count ${msgCountBeforeNoSelExpand} -> ${allMessages.length}`);
    phase('WP3c (4d-b) Expand-selected with nothing selected is a bridge-silent no-op');

    // -- (4d-c) NO-OP: a NON-expandable (non-External) node selected => zero traffic -
    // Select a non-External node (accentSelectedId set, but isExpandable is false).
    // Drain its nodeClick, then invoke: the action must NOT send a nodeExpand.
    await page.evaluate((id) => window.bridge.dispatch({ type: 'clickTest', id }), expandNonExtNode.id);
    await awaitMessage('nodeClick', `clickTest (select non-External) on '${expandNonExtNode.id}'`);
    const msgCountBeforeNonExtExpand = allMessages.length;
    await invokeExpandAction();
    assert(allMessages.length === msgCountBeforeNonExtExpand,
      `WP3c (4d-c): the Expand-selected action with a NON-External node selected must be BRIDGE-SILENT (isExpandable false => no nodeExpand): count ${msgCountBeforeNonExtExpand} -> ${allMessages.length}`);
    phase(`WP3c (4d-c) Expand-selected on a non-External node is a bridge-silent no-op ('${expandNonExtNode.id}')`);

    // (5) Esc CLOSES the palette AND clears the input value (the existing find-Esc
    // behavior, extended to hide the dropdown). Open with a query first, then Esc.
    await typePalette(SHARED_QUERY);
    const beforeEsc = await readPalette();
    assert(beforeEsc.ulHidden === false && beforeEsc.inputValue === SHARED_QUERY,
      `WP3c (5): precondition - the palette must be OPEN with the query before Esc: ${JSON.stringify({ ulHidden: beforeEsc.ulHidden, inputValue: beforeEsc.inputValue })}`);
    await page.keyboard.press('Escape');
    const afterEsc = await readPalette();
    assert(afterEsc.ulHidden === true && afterEsc.ariaExpanded === 'false',
      `WP3c (5): Esc must HIDE #palette-results (and aria-expanded=false): ${JSON.stringify({ ulHidden: afterEsc.ulHidden, ariaExpanded: afterEsc.ariaExpanded })}`);
    assert(afterEsc.inputValue === '',
      `WP3c (5): Esc must CLEAR the #find-input value (existing find-Esc behavior), got '${afterEsc.inputValue}'`);
    phase('WP3c (5) Esc closes the palette + clears the input value');

    // (6) Ctrl+F ALIAS still opens + focuses the palette (extends the ADR-023 (6)
    // Ctrl+F focus assert: it must ALSO un-hide #palette-results now). Blur first.
    await page.evaluate(() => { document.getElementById('find-input').blur(); });
    await page.keyboard.press('Control+f');
    const ctrlFAlias = await readPalette();
    assert(ctrlFAlias.active === 'find-input',
      `WP3c (6): Ctrl+F (alias) must keep focusing #find-input, got '${ctrlFAlias.active}'`);
    assert(ctrlFAlias.ulHidden === false && ctrlFAlias.ariaExpanded === 'true',
      `WP3c (6): Ctrl+F (alias) must ALSO OPEN #palette-results (un-hidden, aria-expanded=true), not just focus: ${JSON.stringify({ ulHidden: ctrlFAlias.ulHidden, ariaExpanded: ctrlFAlias.ariaExpanded })}`);
    phase('WP3c (6) Ctrl+F alias opens + focuses the palette');

    // Restore a clean state for the downstream dbltap/focus phases: Esc to clear+
    // close the palette, toggle labels + issues back OFF (the (4)/(4b) actions left
    // them ON), background-tap to clear selection, and refit.
    await page.focus('#find-input', { timeout: MESSAGE_TIMEOUT_MS });
    await page.keyboard.press('Escape');
    await page.evaluate(() => { document.getElementById('find-input').blur(); });
    const palCleanup = await page.evaluate(() => {
      // Toggle labels-all OFF if on (controlToggleLabels via the button click).
      if (window.__cy.nodes('.gw-labels-all').length > 0) { document.getElementById('labels-btn').click(); }
      // Toggle issues-only OFF if on.
      if (document.getElementById('issues-btn').getAttribute('aria-pressed') === 'true') {
        document.getElementById('issues-btn').click();
      }
      window.__cy.emit('tap', { target: window.__cy });
      window.__cy.fit(window.__cy.elements(), 80);
      return {
        selectedCount: window.__cy.nodes(':selected').length,
        anyDim: window.__cy.nodes('.gw-dim').length,
        anyLabelsAll: window.__cy.nodes('.gw-labels-all').length,
        issuesAria: document.getElementById('issues-btn').getAttribute('aria-pressed'),
        anyHidden: window.__cy.nodes().filter((x) => !x.visible()).length,
        paletteHidden: document.getElementById('palette-results').hidden,
        inputValue: document.getElementById('find-input').value,
      };
    });
    assert(palCleanup.selectedCount === 0 && palCleanup.anyDim === 0 && palCleanup.anyLabelsAll === 0
      && palCleanup.issuesAria === 'false' && palCleanup.anyHidden === 0
      && palCleanup.paletteHidden === true && palCleanup.inputValue === '',
      `WP3c: the palette block must leave a CLEAN state for downstream phases (no selection/dim/labels-all, issues off, palette closed, input empty): ${JSON.stringify(palCleanup)}`);
    phase('WP3c command palette verified + clean state restored');
    // =========================================================================

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

    // --- WP3d (#146): graph minimap ------------------------------------------
    // The bottom-LEFT minimap overlay: a downscaled cy.png thumbnail of the WHOLE
    // graph (background-image, re-png'd ONLY on graph/theme change) + a live
    // #minimap-viewport rect (the single per-frame DOM write) + click/drag-to-pan.
    // graph.js owns the module (MINIMAP_W=200/MINIMAP_H=140/MINIMAP_COARSE_THRESHOLD
    // =1500/SCALE 0.5|0.15); index.html owns the #minimap (pointer-events:auto,
    // hidden by default) + #minimap-viewport markup. This block runs LAST on the
    // main page, AFTER every dark screenshot (graph-overview/selection/controls/
    // focus/cycle/expanded), so pin (5)'s {theme:'light'} re-png never touches the
    // byte-identical dark baseline; it restores dark before the run continues. The
    // minimap thumbnail is NOT part of any pinned screenshot baseline (it post-dates
    // the captures). MINIMAP_W/H mirror the index.html literals - pinned here too so
    // an index.html/graph.js size drift fails. Harness morals hold: primitives only
    // out of evaluate, no sleeps, watchdog-bounded.
    const MINIMAP_W = 200;   // mirror of #minimap width (index.html) / graph.js MINIMAP_W
    const MINIMAP_H = 140;   // mirror of #minimap height (index.html) / graph.js MINIMAP_H

    // (1) #minimap exists, is SHOWN (not [hidden]) after the non-empty render, sits
    // bottom-LEFT (its left < viewport center-x AND its top > center-y),
    // pointer-events:auto (it is interactive, unlike the pointer-events:none legend),
    // mirrors the pinned 200x140 size, and does NOT overlap #legend (top-left) or
    // #controls (bottom-right) by bounding rect. One round-trip pulls all three boxes.
    const overlayBoxes = await page.evaluate(() => {
      const rectOf = (id) => {
        const el = document.getElementById(id);
        if (!el) { return null; }
        const b = el.getBoundingClientRect();
        return { left: b.left, top: b.top, right: b.right, bottom: b.bottom, width: b.width, height: b.height };
      };
      const mm = document.getElementById('minimap');
      return {
        present: !!mm,
        hidden: mm ? mm.hasAttribute('hidden') : null,
        pointerEvents: mm ? getComputedStyle(mm).pointerEvents : null,
        minimap: rectOf('minimap'),
        legend: rectOf('legend'),
        controls: rectOf('controls'),
        innerWidth: window.innerWidth,
        innerHeight: window.innerHeight,
      };
    });
    assert(overlayBoxes.present,
      'WP3d minimap: #minimap must exist in the shipped bundle');
    assert(overlayBoxes.hidden === false,
      `WP3d minimap: #minimap must NOT be [hidden] after a non-empty graph render (refreshMinimap shows it), got hidden=${overlayBoxes.hidden}`);
    assert(overlayBoxes.pointerEvents === 'auto',
      `WP3d minimap: #minimap must be pointer-events:auto (it is click/drag interactive), got '${overlayBoxes.pointerEvents}'`);
    const mmBox = overlayBoxes.minimap;
    // #minimap is content-box (no box-sizing override) with a 1px border, so its
    // border-box getBoundingClientRect is the 200x140 CONTENT plus 2px (1px each
    // side). MINIMAP_W/H pin the CONTENT size that graph.js maps the thumbnail into;
    // the rendered border-box is MINIMAP_W+2 x MINIMAP_H+2.
    const MINIMAP_BORDER = 1;
    const mmBorderBoxW = MINIMAP_W + 2 * MINIMAP_BORDER;
    const mmBorderBoxH = MINIMAP_H + 2 * MINIMAP_BORDER;
    assert(Math.abs(mmBox.width - mmBorderBoxW) < 1 && Math.abs(mmBox.height - mmBorderBoxH) < 1,
      `WP3d minimap: #minimap border-box must be ${mmBorderBoxW}x${mmBorderBoxH} (${MINIMAP_W}x${MINIMAP_H} content + ${MINIMAP_BORDER}px border each side; index.html/graph.js MINIMAP_W/H), got ${mmBox.width}x${mmBox.height}`);
    assert(mmBox.left < overlayBoxes.innerWidth / 2,
      `WP3d minimap: #minimap must sit on the LEFT half (bottom-left corner): box.left ${mmBox.left} >= innerWidth/2 ${overlayBoxes.innerWidth / 2}`);
    assert(mmBox.top > overlayBoxes.innerHeight / 2,
      `WP3d minimap: #minimap must sit on the BOTTOM half (bottom-left corner): box.top ${mmBox.top} <= innerHeight/2 ${overlayBoxes.innerHeight / 2}`);
    // No-overlap by AABB: two rects are disjoint iff one is fully left/right/above/
    // below the other. #legend (top-left) and #controls (bottom-right) must each be
    // disjoint from #minimap (bottom-left) so the three overlays never collide.
    const disjoint = (a, b) =>
      a.right <= b.left || b.right <= a.left || a.bottom <= b.top || b.bottom <= a.top;
    assert(disjoint(mmBox, overlayBoxes.legend),
      `WP3d minimap: #minimap (bottom-left) must NOT overlap #legend (top-left): minimap ${JSON.stringify(mmBox)} vs legend ${JSON.stringify(overlayBoxes.legend)}`);
    assert(disjoint(mmBox, overlayBoxes.controls),
      `WP3d minimap: #minimap (bottom-left) must NOT overlap #controls (bottom-right): minimap ${JSON.stringify(mmBox)} vs controls ${JSON.stringify(overlayBoxes.controls)}`);
    phase('WP3d minimap: bottom-left, 200x140, pointer-events:auto, disjoint from legend + controls');

    // (2) thumbnail present: #minimap computed background-image is a non-'none'
    // data:image/png URI once the graph renders (refreshMinimap set cy.png base64uri).
    const bgImageDark = await page.evaluate(
      () => getComputedStyle(document.getElementById('minimap')).backgroundImage);
    assert(bgImageDark && bgImageDark !== 'none' && /data:image\/png/i.test(bgImageDark),
      `WP3d minimap: #minimap background-image must be a non-'none' data:image/png URI thumbnail after render, got '${(bgImageDark || '').slice(0, 64)}...'`);
    phase('WP3d minimap: thumbnail background-image is a data:image/png URI');

    // (3) #minimap-viewport tracks the camera: capture the rect's left/top/width/
    // height, then zoom + pan to a DIFFERENT camera (driving cy directly, then firing
    // the 'render' event the rect listens on), and assert at least one of the four
    // changed. The rect is the only per-frame DOM write (cy.on('render pan zoom')).
    const vpBefore = await page.evaluate(() => {
      const vp = document.getElementById('minimap-viewport');
      if (!vp) { return null; }
      const s = vp.style;
      return { present: true, left: s.left, top: s.top, width: s.width, height: s.height };
    });
    assert(vpBefore && vpBefore.present,
      'WP3d minimap: #minimap-viewport must exist inside #minimap');
    await page.evaluate(() => {
      const cy = window.__cy;
      // Move to a clearly different camera (zoom in, pan), then emit 'render' so the
      // bundle's cy.on('render pan zoom') handler repositions the viewport rect.
      cy.zoom(cy.zoom() * 2 + 0.5);
      cy.pan({ x: cy.pan().x + 137, y: cy.pan().y - 91 });
      cy.emit('render');
    });
    const vpAfter = await page.evaluate(() => {
      const s = document.getElementById('minimap-viewport').style;
      return { left: s.left, top: s.top, width: s.width, height: s.height };
    });
    const vpChanged = vpAfter.left !== vpBefore.left || vpAfter.top !== vpBefore.top
      || vpAfter.width !== vpBefore.width || vpAfter.height !== vpBefore.height;
    assert(vpChanged,
      `WP3d minimap: #minimap-viewport rect must track the camera (left/top/width/height) on render/pan/zoom - none changed after a zoom+pan. before ${JSON.stringify(vpBefore)} after ${JSON.stringify(vpAfter)}`);
    phase('WP3d minimap: #minimap-viewport rect tracks the live camera on pan/zoom');

    // (4) click-to-pan: record cy.pan(), dispatch a real DOM mousedown at an
    // off-center #minimap pixel (the element's own client coords + an offset), and
    // assert (a) cy.pan() changed (minimapPanTo -> cy.center) AND (b) zero NEW bridge
    // messages of type nodeClick/focused/select (a minimap pan is coarse navigation,
    // NOT a node tap or a focus frame - it must stay bridge-silent). Snapshot the
    // main-channel message list length first so only messages provoked by THIS click
    // are inspected.
    const beforeMsgCount = allMessages.length;
    const panBefore = await page.evaluate(() => ({ x: window.__cy.pan().x, y: window.__cy.pan().y }));
    await page.evaluate(() => {
      const el = document.getElementById('minimap');
      const r = el.getBoundingClientRect();
      // An off-center point well inside the box (not the exact centre, so cy.center
      // actually moves the camera): ~30% in from the top-left of the minimap.
      const cx = r.left + r.width * 0.3;
      const cy = r.top + r.height * 0.3;
      el.dispatchEvent(new MouseEvent('mousedown', {
        bubbles: true, cancelable: true, view: window,
        clientX: cx, clientY: cy, button: 0,
      }));
    });
    const panAfter = await page.evaluate(() => ({ x: window.__cy.pan().x, y: window.__cy.pan().y }));
    const panMoved = Math.abs(panAfter.x - panBefore.x) > 1e-6 || Math.abs(panAfter.y - panBefore.y) > 1e-6;
    assert(panMoved,
      `WP3d minimap: a mousedown at an off-center #minimap pixel must pan the main view (minimapPanTo -> cy.center). pan before ${JSON.stringify(panBefore)} after ${JSON.stringify(panAfter)}`);
    const newMsgs = allMessages.slice(beforeMsgCount);
    const chattyMsgs = newMsgs.filter((m) => m.type === 'nodeClick' || m.type === 'focused' || m.type === 'select');
    assert(chattyMsgs.length === 0,
      `WP3d minimap: click-to-pan must be bridge-silent (coarse navigation, not a tap/focus) - zero nodeClick/focused/select. got ${JSON.stringify(chattyMsgs)}`);
    phase('WP3d minimap: click-to-pan moves the camera AND emits no nodeClick/focused/select');

    // (5) theme re-png: the thumbnail is re-rasterized to the recolored canvas on a
    // {theme} command (the handler calls refreshMinimap after cy.style). Capture the
    // current DARK thumbnail data-URI, flip to light (with the ping/pong restyle
    // barrier - theme is fire-and-forget), assert the new data-URI DIFFERS (light
    // canvas bg + hues), then flip back to dark and assert the restored thumbnail
    // differs from the light one (the re-png round-trips). The MAIN dark graph
    // screenshots are already captured above, so this never disturbs the byte-
    // identical dark baseline; the minimap thumbnail is not part of it.
    let mmPingSeq = 0;
    const setMinimapVariant = async (variant) => {
      await page.evaluate((v) => window.bridge.dispatch({ type: 'theme', variant: v }), variant);
      mmPingSeq += 1;
      await page.evaluate((seq) => window.bridge.dispatch({ type: 'ping', seq }), mmPingSeq);
      const pong = await awaitMessage('pong', `WP3d minimap restyle barrier after {variant:'${variant}'}`);
      assert(pong.seq === mmPingSeq,
        `WP3d minimap restyle barrier: pong.seq ${pong.seq} != ${mmPingSeq} (out-of-order command handling?)`);
    };
    const mmBgNow = () => page.evaluate(
      () => getComputedStyle(document.getElementById('minimap')).backgroundImage);
    const darkThumb = await mmBgNow();
    assert(darkThumb && /data:image\/png/i.test(darkThumb),
      `WP3d minimap: dark thumbnail must be a data:image/png URI before the theme flip, got '${(darkThumb || '').slice(0, 48)}...'`);
    await setMinimapVariant('light');
    const lightThumb = await mmBgNow();
    assert(lightThumb && /data:image\/png/i.test(lightThumb),
      `WP3d minimap: light thumbnail must still be a data:image/png URI after {theme:'light'}, got '${(lightThumb || '').slice(0, 48)}...'`);
    assert(lightThumb !== darkThumb,
      'WP3d minimap: the {theme:\'light\'} re-png must produce a DIFFERENT thumbnail data-URI than dark (refreshMinimap re-rasterizes to the light canvas) - they are byte-identical, the theme handler did not re-png the minimap');
    // Restore dark and prove the re-png round-trips (the restored thumbnail differs
    // from the light one). This also leaves the main page back on the byte-identical
    // dark canvas for the PNG-export round-trip / final audit / downstream probes.
    await setMinimapVariant('dark');
    const darkThumb2 = await mmBgNow();
    assert(darkThumb2 && /data:image\/png/i.test(darkThumb2) && darkThumb2 !== lightThumb,
      `WP3d minimap: restoring {theme:'dark'} must re-png back to a dark thumbnail DIFFERENT from the light one (round-trip) - got ${darkThumb2 === lightThumb ? 'byte-identical to light' : 'a non-PNG URI'}`);
    phase('WP3d minimap: theme flip re-pngs the thumbnail (dark -> light differs, dark restored)');

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

    // --- fresh-page ACCENT-RING DROP probe (ADR-027 D3 / WP3) ----------------
    // After the audit and the other probes, on its OWN page/context/channel. Pins
    // that the selection accent ring HIDES when its tracked node vanishes on a
    // graphUpdate (lazy expand) - the one accent-ring case the main selection block
    // cannot drive without disturbing the downstream phases. Independent of the main
    // run's zero-jsError audit (and itself jsError-free).
    await accentRingDropProbe(browser, indexHtml);
    phase('accent-ring-drop probe (fresh page: ring hides when tracked node vanishes on graphUpdate)');

    // --- fresh-page ISSUES-ONLY ALL-CLEAR probe (WP3b / #142) ----------------
    // After the audit and the other probes, on its OWN page/context/channel. The
    // demo run always has 19 findings (flagged nodes present), so the all-clear
    // guard - clicking #issues-btn with ZERO flagged nodes must be inert ("No
    // issues" / aria-pressed=false, every node visible, never a blank canvas) -
    // needs a hand-built zero-flagged fixture. Independent of the main run's
    // zero-jsError audit (and itself jsError-free).
    await issuesAllClearProbe(browser, indexHtml);
    phase('issues-all-clear probe (fresh page: zero-flagged toggle is inert, "No issues")');

    // --- fresh-page MINIMAP DEGENERATE probe (WP3d / #146) -------------------
    // After the audit and the other probes, on its OWN page/context/channel. The demo
    // run always loads the ~200-node graph (minimap shown), so the HIDDEN case (an
    // EMPTY graph must keep #minimap [hidden] with NO thumbnail) and the small-graph
    // SHOWN case (a single-node graph still renders a clean thumbnail, no broken img)
    // need a hand-built fixture. Independent of the main run's zero-jsError audit (and
    // itself jsError-free).
    await minimapDegenerateProbe(browser, indexHtml);
    phase('minimap-degenerate probe (fresh page: empty keeps #minimap hidden; single-node shows a clean thumbnail)');

    // --- fresh-page LIGHT-THEME probe (ADR-026 D5 / WP1b) -------------------
    // After the audit and the other probes, on its OWN page/context/channel. Loads a
    // hand-built dataset, dispatches {type:'theme',variant:'light'}, awaits the live
    // restyle (ping/pong barrier - the theme command is fire-and-forget), asserts the
    // COMPUTED cytoscape styles + :root chrome vars equal the LIGHT_* constants (the C#
    // BrandTokens.Graph*LightHex mirror), then dispatches {variant:'dark'} and asserts
    // the computed styles + chrome vars are restored byte-identical to dark (the live
    // restyle round-trips). Independent of the main run's zero-jsError audit.
    await lightThemeProbe(browser, indexHtml, screenshotDir);
    phase('light-theme probe (fresh page: live light restyle vs LIGHT_* + dark round-trip)');

    console.log(
      `PASS graph-bundle: ${loaded.nodeCount} nodes, ${loaded.edgeCount} edges `
      + `(post-update ${updated.nodeCount}/${updated.edgeCount}), `
      + `${chunks.length} chunks, ${kindsPresent.size}/7 kinds, `
      + `WCAG node-lift ring (DL/UG/Computer) verified (#90), `
      + `${flaggedBySev.error.length}/${flaggedBySev.warning.length}/${flaggedBySev.info.length} err/warn/info halos, `
      + `${belowNodes.length} roll-up rings (own-sev-wins-over-below collision verified, #268), `
      + `diff underlay/line + COEXIST verified, `
      + `F2 eased focus + F1 enter fade + reduced-motion verified, `
      + `light-theme live restyle + dark round-trip verified (ADR-026 D5), `
      + `selection + neighborhood dim + hover + selective labels verified (#89), `
      + `selection accent ring (show/clear/empty/unknown/drop + static-under-reduce) verified (ADR-027), `
      + `reverse select command (tap-identical/empty-clear/unknown-clear/instant) verified (#96), `
      + `busy ring (paint/severity-wins/clear/transient) verified (#94), `
      + `control cluster + find (name/DN/no-match) + zoom/fit + labels toggle + keyboard verified (ADR-023), `
      + `issues-only filter (toggle on/off, keep flagged+roll-up, hide clean, survive graphUpdate, reveal-on-select, all-clear inert) verified (WP3b #142), `
      + `minimap (bottom-left/disjoint, thumbnail, viewport-tracks-camera, click-to-pan silent, theme re-png, empty+degenerate hidden) verified (WP3d #146), `
      + `minDist ${minDistance.toFixed(1)}, ${assertCount} asserts, `
      + `8 screenshots -> ${screenshotDir}`);
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
