# Harness contracts (the gates themselves; #301/#311, 2026-07-10 audit)

Rules about the build/CI/dependency harness - the machinery that verifies the
product, as opposed to the product contracts in the sibling rule files.

- **Gate-surface diffs carry a justification (audit item 14).** Any diff
  touching `tools/build.ps1`, `tools/bootstrap.ps1`, `tools/pack-release.ps1`,
  `.claude/hooks/*.ps1`, or `.github/workflows/*.yml` must say WHY in the
  PR/commit body (one line suffices). These files are the harness's own
  enforcement surface; the `reviewer` agent rejects silent gate weakening
  (dropped steps, loosened filters, lowered floors). This deliberately does
  NOT freeze `tools/` - CLAUDE.md mandates keeping `bootstrap.ps1` current
  with every installed dependency, and `build.ps1` is the designated extension
  point for new quality gates (CI calls it verbatim; never reimplement a gate
  in workflow YAML).
- **The ~52 `[AdFact]`/`Category=RequiresAd` tests never run in CI - accepted
  risk, decided 2026-07-10 (audit item 13).** GitHub-hosted runners have no
  lab DC; a self-hosted domain-joined runner was considered and REJECTED (an
  internet-reachable Actions fleet with credentials into a DC is more security
  surface than the coverage is worth). Compensating controls: the doubly
  redundant local gate (`AdFact` reachability probe + trait filter), CLAUDE.md
  DoD step 3 (provider/graph changes run the live-AD suite locally), and the
  loud skip message when the OU is unreachable. Revisiting this means its own
  ADR, not a CI tweak.
- **NuGet lock-file discipline (audit items 1/7).** The committed
  `packages.lock.json` shape is what a plain `dotnet restore` produces; the
  gate restores `--locked-mode`, so after ANY package edit regenerate with a
  plain restore or the gate fails NU1004 (that failure is the feature). Two
  known rewriters of that shape: configuration-conditional PackageReferences
  (solved once via the Avalonia.Diagnostics assets-excluded twin in
  `GroupWeaver.App.csproj` - copy that pattern, don't invent a new one) and
  self-contained publishes (implicit ILLink.Tasks; `pack-release.ps1`
  re-normalizes itself - one-off publishes need a plain restore afterwards).
- **Coverage floor: raise deliberately, never lower (audit items 6/12).** CI
  enforces `coverage-report.ps1 -MinLine 83` over the three product assemblies
  (CI-equivalent baseline 85.1% without AD tests; full local suite 90.2%).
  Lowering the floor to make a build green is the same offense as weakening a
  test. When coverage rises durably, ratchet the floor up in ci.yml (with the
  baseline note there), keeping ~2 points of headroom.
- **The web-bundle lint gate is check-only BY CONTRACT (audit item 8).**
  `tools/lint-web.ps1` / the repo-root `eslint.config.js` must never gain a
  `--fix`/write path: CI hash-verifies the published bundle byte-identical to
  `src/App/web`, so any tool that rewrites the bundle breaks the release
  integrity chain (ADR-004/ADR-012). Formatting stays hand-curated (ADR-001);
  Prettier was evaluated and rejected for exactly this reason.
