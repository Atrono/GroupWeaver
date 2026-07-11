// verify-export.mjs - headless render check for the exported findings HTML
// (ADR-041 D2.3): the first automated OPENING of a written export artifact.
// The HTML comes from the demo-only `--dump-export` seam (the --dump-graph
// exit-64 precedent; produced by tools/test-graph-bundle.ps1), i.e. the real
// ViolationReportExporter output over the 19-finding demo baseline - so this
// gate proves the artifact an AD admin would actually forward RENDERS: loads
// in Chromium with zero console errors/pageerrors, zero non-file fetches
// (self-contained by contract, ADR-013), real table semantics, the pinned
// severity palette in the RENDERED styles, and the ADR-030 D3 honesty header
// rows. Sibling of verify.mjs on purpose (different input artifact, own
// watchdog accounting); same harness morals - plain playwright library,
// sequential, zero sleeps, custom assert(), any failure => nonzero exit.
//
// KNOWN #329 DEBTS (assert only what is TRUE today - the #329 fix PR extends
// this file): the HTML declares NO color-scheme and pairs no background with
// its body ink (WCAG F24; forced-dark UAs may invert), hardcodes plural forms,
// and its <th> cells carry no scope attributes. Do NOT assert those until
// #329 lands; then pin them here.
//
// Usage: node verify-export.mjs <demo-report.html>

import { existsSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { chromium } from 'playwright';

// The pinned ADR-010 severity palette, hand-copied like verify.mjs' SEVERITY
// block - the exporter renders these as class-keyed row accents (tr.sev-*).
const SEVERITY = {
  error: '#D13438',
  warning: '#F7A30B',
  info: '#4FA3E3',
};

// The AP 3.2 demo baseline (rule-engine.md, executable): exactly 19 findings.
const BASELINE_FINDINGS = 19;

// The pinned findings-table header cells (AppCliTests pins the same row as a
// substring; here they are asserted from the RENDERED DOM).
const FINDINGS_HEADERS = ['Severity', 'Rule', 'Subject', 'Primary DN', 'DNs', 'Message'];

// The header meta rows: the legacy four plus the ADR-030 D3 honesty rows
// (RulesetName is always non-null on the --dump-export path).
const META_HEADERS = ['Root', 'Connection', 'Generated', 'Findings', 'Ruleset', 'Triaged', 'Unchecked'];

let assertCount = 0;
function assert(condition, message) {
  if (!condition) {
    throw new Error(`ASSERT FAILED: ${message}`);
  }
  assertCount += 1;
}

// Same watchdog rationale as verify.mjs (a stalled renderer must never hang
// CI); this run is a single static page, so the default bound is shorter.
const WATCHDOG_MS = Number(process.env.GRAPH_BUNDLE_TIMEOUT_MS) > 0
  ? Number(process.env.GRAPH_BUNDLE_TIMEOUT_MS)
  : 2 * 60_000;
let lastPhase = 'startup (no phase completed)';
function phase(name) {
  lastPhase = name;
  console.log(`[verify-export] ${name}`);
}
setTimeout(() => {
  console.error(
    `FAILED watchdog: run exceeded ${WATCHDOG_MS} ms; last completed phase: ${lastPhase}`);
  process.exit(1);
}, WATCHDOG_MS);

// rgb(a)/hex CSS color -> #RRGGBB (verify.mjs' toHex idiom, self-contained here).
function toHex(cssColor) {
  const rgb = /^rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(cssColor);
  if (rgb) {
    return '#' + [rgb[1], rgb[2], rgb[3]]
      .map((v) => Number(v).toString(16).padStart(2, '0').toUpperCase())
      .join('');
  }
  return cssColor.toUpperCase();
}

async function main() {
  const reportPath = process.argv[2];
  if (!reportPath) {
    console.error('usage: node verify-export.mjs <demo-report.html>');
    process.exit(2);
  }
  assert(existsSync(reportPath), `export artifact not found: ${reportPath}`);

  const browser = await chromium.launch({ args: ['--disable-gpu'] });
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
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    page.on('crash', () => {
      console.error(
        `FAILED page-crash: renderer crashed; last completed phase: ${lastPhase}`);
      process.exit(1);
    });

    // Every renderer complaint is collected and asserted ZERO at the end:
    // console errors, uncaught pageerrors, and any request that is not the
    // file:// document itself (self-contained = no external fetches, ADR-013).
    const consoleErrors = [];
    const pageErrors = [];
    const externalRequests = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') {
        consoleErrors.push(msg.text());
      }
    });
    page.on('pageerror', (err) => pageErrors.push(String(err)));
    page.on('request', (req) => {
      if (!req.url().startsWith('file://')) {
        externalRequests.push(req.url());
      }
    });

    await page.goto(pathToFileURL(reportPath).href);
    phase('export HTML loaded on file://');

    // --- document identity -------------------------------------------------
    const title = await page.title();
    assert(title === 'GroupWeaver violation report',
      `document title must be 'GroupWeaver violation report', got '${title}'`);
    const h1 = await page.evaluate(() => document.querySelector('h1')?.textContent ?? null);
    assert(h1 === 'GroupWeaver violation report',
      `the <h1> must render 'GroupWeaver violation report', got '${h1}'`);
    phase('document identity (title + h1)');

    // --- real table semantics: <th> cells present --------------------------
    const dom = await page.evaluate((expectedMeta) => ({
      metaThTexts: [...document.querySelectorAll('table.meta th')].map((el) => el.textContent.trim()),
      findingsThTexts: [...document.querySelectorAll('table.findings thead th')].map((el) => el.textContent.trim()),
      findingsRowCount: document.querySelectorAll('table.findings tbody tr').length,
      metaRowCount: expectedMeta.length,
    }), META_HEADERS);
    for (const header of FINDINGS_HEADERS) {
      assert(dom.findingsThTexts.includes(header),
        `findings table must carry a <th>${header}</th> header cell; got [${dom.findingsThTexts.join(', ')}]`);
    }
    assert(dom.findingsThTexts.length === FINDINGS_HEADERS.length,
      `findings table must carry exactly ${FINDINGS_HEADERS.length} <th> cells, got ${dom.findingsThTexts.length}`);
    phase('findings table header (<th> cells present and pinned)');

    // --- the demo baseline: exactly 19 finding rows ------------------------
    assert(dom.findingsRowCount === BASELINE_FINDINGS,
      `the demo baseline renders exactly ${BASELINE_FINDINGS} finding rows (rule-engine.md), got ${dom.findingsRowCount}`);
    phase(`findings rows (${dom.findingsRowCount} = the demo baseline)`);

    // --- header meta rows incl. the ADR-030 D3 honesty rows ----------------
    for (const header of META_HEADERS) {
      assert(dom.metaThTexts.includes(header),
        `the header table must render a '${header}' row (ADR-030 D3 for Ruleset/Triaged/Unchecked); got [${dom.metaThTexts.join(', ')}]`);
    }
    phase('header rows (Root/Connection/Generated/Findings + Ruleset/Triaged/Unchecked)');

    // --- the pinned severity palette in the RENDERED styles ----------------
    // Class-keyed row accents (tr.sev-*), read as COMPUTED border-left-color -
    // a stylesheet typo or a class rename renders default black, never throws.
    for (const [sev, want] of Object.entries(SEVERITY)) {
      const got = await page.evaluate((cls) => {
        const row = document.querySelector(`tr.sev-${cls}`);
        return row === null ? null : getComputedStyle(row).borderLeftColor;
      }, sev);
      assert(got !== null,
        `the demo baseline must render at least one tr.sev-${sev} row (it has all three severities)`);
      assert(toHex(got) === want,
        `tr.sev-${sev} rendered border-left-color '${got}' (${toHex(got)}) != pinned ${want} (ADR-010 palette drift?)`);
    }
    phase('severity palette rendered (sev-error/warning/info row accents at the pinned hexes)');

    // NOTE (#329, expected extension): once the export declares `color-scheme`
    // and pairs body ink with an explicit background, pin BOTH here (computed
    // style of :root color-scheme + body background-color). Not asserted today
    // because both are known-absent - this render check is the backstop the
    // #329 fix PR extends, not a pre-assertion of the fix.

    // --- final audit: zero renderer complaints -----------------------------
    assert(pageErrors.length === 0,
      `the export must render with zero pageerrors: ${JSON.stringify(pageErrors, null, 2)}`);
    assert(consoleErrors.length === 0,
      `the export must render with zero console errors: ${JSON.stringify(consoleErrors, null, 2)}`);
    assert(externalRequests.length === 0,
      `the export must be self-contained - zero non-file:// requests (ADR-013), got: ${externalRequests.join(', ')}`);
    phase('audit (zero console errors, zero pageerrors, zero external fetches)');

    console.log(
      `PASS export-render: ${dom.findingsRowCount} finding rows, `
      + `${dom.metaThTexts.length} header rows, severity palette pinned, `
      + `${assertCount} asserts`);
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
