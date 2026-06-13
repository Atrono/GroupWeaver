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

    // --- screenshot 1: overview (initGraph already ran cy.fit()) -------------
    await page.screenshot({ path: join(screenshotDir, 'graph-overview.png') });
    phase('overview screenshot');

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
    await page.evaluate((ids) => window.bridge.dispatch({ type: 'focus', ids }), [rootNode.id, ...ring1]);
    await awaitMessage('focused', `focus on root + ${ring1.length} ring-1 children`);
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
    await page.evaluate((ids) => window.bridge.dispatch({ type: 'focus', ids }), cycleIds);
    await awaitMessage('focused', `focus on cycle pair ${cycleIds.join(' <-> ')}`);
    await page.screenshot({ path: join(screenshotDir, 'graph-cycle.png') });
    phase('cycle screenshot');

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
    await page.evaluate((ids) => window.bridge.dispatch({ type: 'focus', ids }), [newNode.id]);
    await awaitMessage('focused', `focus on post-update node '${newNode.id}'`);
    await page.screenshot({ path: join(screenshotDir, 'graph-expanded.png') });
    phase('graphUpdate replace-in-place (instance, viewport, handlers survive)');

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

    console.log(
      `PASS graph-bundle: ${loaded.nodeCount} nodes, ${loaded.edgeCount} edges `
      + `(post-update ${updated.nodeCount}/${updated.edgeCount}), `
      + `${chunks.length} chunks, ${kindsPresent.size}/7 kinds, `
      + `${flaggedBySev.error.length}/${flaggedBySev.warning.length}/${flaggedBySev.info.length} err/warn/info halos, `
      + `${belowNodes.length} roll-up rings, `
      + `minDist ${minDistance.toFixed(1)}, ${assertCount} asserts, `
      + `4 screenshots -> ${screenshotDir}`);
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
