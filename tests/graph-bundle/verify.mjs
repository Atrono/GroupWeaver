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
//   <screenshot-dir>   receives graph-overview.png / graph-focus.png / graph-cycle.png

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
    const chunks = [];
    let nodeIndex = 0;
    let edgeIndex = 0;
    while (nodeIndex < fixture.nodes.length || edgeIndex < fixture.edges.length) {
      const nodes = fixture.nodes.slice(nodeIndex, nodeIndex + MAX_NODES_PER_CHUNK);
      const edges = fixture.edges.slice(edgeIndex, edgeIndex + MAX_EDGES_PER_CHUNK);
      chunks.push({ type: 'graphChunk', nodes, edges });
      nodeIndex += nodes.length;
      edgeIndex += edges.length;
    }
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

    // --- final audit: ZERO jsError across the whole run (ADR-004 D6) ---------
    const jsErrors = allMessages.filter((m) => m.type === 'jsError');
    assert(jsErrors.length === 0,
      `jsError messages were reported during the run: ${JSON.stringify(jsErrors, null, 2)}`);
    phase('audit (zero jsError)');

    console.log(
      `PASS graph-bundle: ${loaded.nodeCount} nodes, ${loaded.edgeCount} edges, `
      + `${chunks.length} chunks, ${kindsPresent.size}/7 kinds, `
      + `minDist ${minDistance.toFixed(1)}, ${assertCount} asserts, `
      + `3 screenshots -> ${screenshotDir}`);
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
