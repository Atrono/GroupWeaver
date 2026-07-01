export const meta = {
  name: 'fit-audit',
  description: 'Multi-agent UX/design fit-audit: one critic per app surface, two diverse-lens jurors triage each surface, a completeness critic hunts gaps, a synthesizer ranks levers. Read-only and advisory - returns structured findings; the caller persists the doc and files issues.',
  whenToUse: 'A whole-app or targeted UX/config fit-audit - the 20+ agent topology sessions 21/29/37/39 rebuilt by hand each time. Returns Verdict + Top-N ranked levers + A/B/C triage. Pass args.focus to scope it, args.surfaces to override the surface list, args.artifactsDir to ground critics in pre-rendered screenshots.',
  phases: [
    { title: 'Critique', detail: 'one critic per surface vs docs/ui-checklist.md + docs/adr/' },
    { title: 'Jury', detail: 'consumer-lens + correctness-lens jurors triage each surface (keep/demote/drop)' },
    { title: 'Completeness', detail: 'one critic names missed surfaces/criteria/cross-cutting concerns' },
    { title: 'Synthesize', detail: 'dedup + rank Top-N levers, bucket A/B/C, honour settled ADRs' },
  ],
}

// -----------------------------------------------------------------------------
// GroupWeaver "fit-audit" workflow.
//
// Encodes the design-review fan-out that was previously re-derived by hand every
// audit. It is deliberately READ-ONLY: an audit must not mutate the product, so
// this workflow NEVER writes files or files issues. It returns a structured
// result; the orchestrator stamps it into docs/ux-fit-audit-<date>.md, files the
// lever issues, and drafts Proposed ADRs (via [[adr-author]]).
//
// Invoke:  Workflow({ name: 'fit-audit' })
//          Workflow({ name: 'fit-audit', args: { focus: 'light theme WCAG only' } })
//          Workflow({ name: 'fit-audit', args: { artifactsDir: 'artifacts/ui' } })
// -----------------------------------------------------------------------------

// --- Inputs (all optional; sensible defaults below) --------------------------
const DEFAULT_SURFACES = [
  { key: 'connect',    name: 'Connect / mode selection',                 files: 'src/App/Views/ConnectionView.axaml',                                             section: 'Native chrome > Connect step' },
  { key: 'rootpicker', name: 'Root Picker (scope selection)',            files: 'src/App/Views/RootPickerView.axaml',                                             section: 'Native chrome > RootPicker' },
  { key: 'workspace',  name: 'Workspace (graph + sidebar + detail)',     files: 'src/App/Views/WorkspaceView.axaml, DetailPanelView.axaml, ViolationsSidebarView.axaml', section: 'Native chrome > Workspace / Detail panel / Violations sidebar' },
  { key: 'graph',      name: 'Graph layer (Cytoscape web bundle)',       files: 'src/App/web/',                                                                   section: 'Graph Layer (browser bundle)' },
  { key: 'audit',      name: 'Audit dashboard',                          files: 'src/App/Views/AuditView.axaml',                                                  section: 'Native chrome > Audit screen' },
  { key: 'plan',       name: 'Plan editor',                              files: 'src/App/Views/PlanView.axaml',                                                   section: 'Native chrome > Plan mode' },
  { key: 'gap',        name: 'Gap (Plan vs Ist diff)',                   files: 'src/App/Views/GapView.axaml',                                                    section: 'Native chrome > Gap mode' },
  { key: 'settings',   name: 'Settings modal',                           files: 'src/App/Views/SettingsWindow.axaml',                                             section: 'Native chrome > Settings modal' },
  { key: 'crosscut',   name: 'Cross-cutting (a11y, persistence, onboarding, keyboard)', files: 'src/App/Views/KeyboardHelpWindow.axaml, MainWindow.axaml',           section: 'Global + WCAG 2.2 AA' },
]

// args can arrive as a parsed object OR as a JSON string (harness-dependent) - normalise both,
// so the surfaces/topLevers/focus knobs never silently no-op into the full-audit default.
const argv = (typeof args === 'string')
  ? (() => { try { return JSON.parse(args) } catch { return {} } })()
  : (args ?? {})

const surfaces = Array.isArray(argv?.surfaces) && argv.surfaces.length ? argv.surfaces : DEFAULT_SURFACES
const TOP = (Number.isInteger(argv?.topLevers) && argv.topLevers > 0) ? argv.topLevers : 5
const focus = argv?.focus ? `\nAUDIT FOCUS (scope every judgement to this): ${argv.focus}\n` : ''
const shots = argv?.artifactsDir
  ? `\nPre-rendered screenshots may exist under ${argv.artifactsDir}; Read any PNG whose name matches this surface and judge the pixels, not only the code.\n`
  : ''

// Two diverse verification lenses (perspective-diverse verify, not N identical refuters).
const LENSES = [
  {
    key: 'consumer',
    brief: 'the point-in-time AGDLP/naming auditor persona: an MSP / IT consultant auditing ONE domain at a snapshot who needs to spot violations, drill in, export, and track trends. Judge whether each finding actually impedes that job. DROP nitpicks; DEMOTE nice-to-haves.',
  },
  {
    key: 'correctness',
    brief: 'a WCAG-2.2-AA + BrandTokens + ADR correctness reviewer. Read docs/adr/ where relevant. Judge whether each finding is technically accurate AND not already settled by an Accepted ADR. If it contradicts a settled ADR decision, DROP it and name the ADR id; if overstated, DEMOTE.',
  },
]

// --- Schemas (validated at the tool layer; agents retry on mismatch) ---------
const FINDINGS_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['surface', 'findings'],
  properties: {
    surface: { type: 'string' },
    findings: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['id', 'title', 'severity', 'criterion', 'evidence', 'rationale'],
        properties: {
          id: { type: 'string', description: 'stable within this surface, e.g. connect-1' },
          title: { type: 'string' },
          severity: { enum: ['High', 'Medium', 'Low'] },
          criterion: { type: 'string', description: 'the ui-checklist clause / WCAG SC / brand token it fails' },
          evidence: { type: 'string', description: 'file:line, screenshot name, or checklist evidence tag' },
          rationale: { type: 'string' },
        },
      },
    },
  },
}

const JURY_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['verdicts'],
  properties: {
    verdicts: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['id', 'verdict', 'rationale'],
        properties: {
          id: { type: 'string' },
          verdict: { enum: ['keep', 'demote', 'drop'] },
          rationale: { type: 'string' },
        },
      },
    },
  },
}

const SYNTH_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['verdict', 'levers', 'triage'],
  properties: {
    verdict: { type: 'string' },
    levers: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        required: ['rank', 'title', 'problem', 'codeLocation', 'impact', 'fix', 'subsumes'],
        properties: {
          rank: { type: 'integer' },
          title: { type: 'string' },
          problem: { type: 'string' },
          codeLocation: { type: 'string' },
          impact: { type: 'string' },
          fix: { type: 'string', description: 'a READ-ONLY, in-scope fix (never a write/mutation feature)' },
          subsumes: { type: 'array', items: { type: 'string' }, description: 'finding ids this lever absorbs' },
        },
      },
    },
    triage: {
      type: 'object', additionalProperties: false,
      required: ['bucketA', 'bucketB', 'bucketC'],
      properties: {
        bucketA: { type: 'array', items: { type: 'string' }, description: 'ship now' },
        bucketB: { type: 'array', items: { type: 'string' }, description: 'next' },
        bucketC: { type: 'array', items: { type: 'string' }, description: 'backlog / defer (incl. ADR-settled)' },
      },
    },
  },
}

const demote = (s) => (s === 'High' ? 'Medium' : 'Low')

const critPrompt = (s) => `You are auditing GroupWeaver's "${s.name}" surface for UX / visual-design fit.
${focus}${shots}
The bar is defined by docs/ui-checklist.md (section: "${s.section}") plus docs/adr/ (Accepted decisions are SETTLED - do not re-litigate them). Owning file(s): ${s.files}.
Read those files and the checklist section, then list concrete fit findings. GroupWeaver is a READ-ONLY product: NEVER propose a write/mutation feature. Every finding MUST cite the criterion it fails (a checklist clause, WCAG success criterion, or brand token) and concrete evidence (file:line or screenshot name). No generic "improve UX" - be specific and falsifiable.`

// --- Phase 1+2: critic per surface, then jurors, pipelined (no barrier) -------
phase('Critique')
const audited = await pipeline(
  surfaces,
  (s) => agent(critPrompt(s), { label: `critic:${s.key}`, phase: 'Critique', schema: FINDINGS_SCHEMA })
    .then((r) => ({ surface: s, findings: r?.findings ?? [] })),
  async (crit, s) => {
    const findings = crit?.findings ?? []
    if (!findings.length) return { surface: s, findings: [] }
    const list = findings.map((f) => `- [${f.id}] (${f.severity}) ${f.title} -- fails: ${f.criterion}; ${f.rationale}`).join('\n')
    const juries = await parallel(LENSES.map((lens) => () =>
      agent(`You are ${lens.brief}\n\nSurface: "${s.name}". Triage EACH finding below as keep / demote / drop with a one-line rationale.${focus}\n\nFindings:\n${list}`,
        { label: `jury:${s.key}:${lens.key}`, phase: 'Jury', schema: JURY_SCHEMA })))
    const votes = juries.filter(Boolean).flatMap((j) => j.verdicts)
    const survivors = findings.map((f) => {
      const vs = votes.filter((v) => v.id === f.id)
      const drops = vs.filter((v) => v.verdict === 'drop').length
      const demotes = vs.filter((v) => v.verdict === 'demote').length
      if (vs.length && drops * 2 > vs.length) return null            // majority drop -> gone
      const sev = (vs.length && demotes * 2 > vs.length) ? demote(f.severity) : f.severity
      return { ...f, surface: s.key, severity: sev, contested: drops > 0, juryRationales: vs.map((v) => `${v.verdict}: ${v.rationale}`) }
    }).filter(Boolean)
    return { surface: s, findings: survivors }
  },
)

const surviving = audited.filter(Boolean).flatMap((a) => a?.findings ?? [])

// --- Phase 3: completeness critic (what did every critic miss?) ---------------
phase('Completeness')
let gaps = []
try {
  const gapsRes = await agent(
  `You are a completeness critic for a GroupWeaver UX fit-audit. Surfaces audited: ${surfaces.map((s) => s.name).join('; ')}.${focus}
Here is every surviving finding:
${surviving.map((f) => `- [${f.surface}/${f.id}] (${f.severity}) ${f.title}`).join('\n') || '(none)'}
Name anything MISSED: a user-facing surface or flow not in the list, a docs/ui-checklist.md criterion or WCAG success criterion not exercised, or a cross-cutting concern (accessibility, persistence, onboarding, error states, empty states, keyboard-only). Return each as a finding with a cited criterion.`,
    { label: 'critic:completeness', phase: 'Completeness', schema: FINDINGS_SCHEMA },
  )
  gaps = (gapsRes?.findings ?? []).map((f) => ({ ...f, surface: 'completeness', contested: false, juryRationales: [] }))
} catch (e) {
  log(`completeness critic failed (${e?.message ?? e}); continuing without gap findings`)
}
const merged = [...surviving, ...gaps]

// --- Phase 4: synthesize into ranked levers + A/B/C triage --------------------
phase('Synthesize')
let synth = null
try {
  synth = await agent(
  `You are the synthesizer for a GroupWeaver UX fit-audit. Below are ${merged.length} triaged findings across all surfaces.${focus}
Produce: (1) a one-paragraph VERDICT on overall fit; (2) the TOP ${TOP} ranked "levers" - each names the problem, code location, user impact, a READ-ONLY in-scope fix, and the ids of the findings it subsumes; (3) an A/B/C triage that places EVERY finding id in exactly one bucket (A = ship now, B = next, C = backlog/defer). Do NOT re-litigate anything settled by an Accepted ADR in docs/adr/ - if a finding contradicts one, put it in bucket C and name the ADR. GroupWeaver is READ-ONLY: no fix may introduce a write/mutation.

Findings:
${merged.map((f) => `- [${f.surface}/${f.id}] (${f.severity}${f.contested ? ', contested' : ''}) ${f.title} -- ${f.criterion}`).join('\n') || '(none)'}`,
    { label: 'synthesizer', phase: 'Synthesize', schema: SYNTH_SCHEMA },
  )
} catch (e) {
  log(`synthesizer failed (${e?.message ?? e}); returning findings without ranked levers`)
}

return {
  surfacesAudited: surfaces.map((s) => s.key),
  findingCount: merged.length,
  verdict: synth?.verdict ?? '',
  levers: synth?.levers ?? [],
  triage: synth?.triage ?? { bucketA: [], bucketB: [], bucketC: [] },
  findings: merged,
}
