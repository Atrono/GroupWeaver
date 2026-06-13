# ADR-012: Release packaging and build provenance

## Status
Accepted (AP 3.5, milestone M3).

## Context
M3 ships v0.1 as a publicly verifiable portable download. There is no
code-signing certificate (PLANNING E8). The app is an Avalonia WinExe
(`net8.0-windows`) hosting WebView2 + a vendored Cytoscape bundle served from
loose `web/` files via `file://` (ADR-004 D6). Trimming is unsafe here:
Avalonia + System.Text.Json reflection + CommunityToolkit.Mvvm source-gen +
WebView2 interop + the embedded `DefaultRuleset.jsonc`/`demo-directory.json`
resources are reflection-reached.

## Decision
- **Self-contained, single-file, `win-x64`, NOT trimmed.** Bundle the .NET 8
  runtime so users need no SDK/runtime (O4). `-p:PublishSingleFile=true`
  `--self-contained`; no `PublishTrimmed`, no `PublishReadyToRun`, no
  `EnableCompressionInSingleFile`.
- **`IncludeNativeLibrariesForSelfExtract=true`** folds WebView2Loader/Skia/
  HarfBuzz/apphost into the exe; gated behind a mandatory post-publish launch
  smoke (`--check` + `--demo --dump-graph`). Fallback if it ever misbehaves:
  drop the flag (the CI-proven loose-DLL layout).
- **The `web/` bundle and `examples/rulesets/` ship LOOSE** beside the exe;
  `web/` via `ExcludeFromSingleFile`, `examples/` copied by the pack script.
- **WebView2 Evergreen Runtime is a documented prerequisite, NOT bundled** — it
  is a shared, auto-updating system component; the app probes for it and shows a
  download banner when absent.
- **Integrity = SHA256 hash + `actions/attest-build-provenance`** (E8), no
  signing cert. Publish a `.zip`, a `.sha256` sidecar, and a provenance
  attestation; users verify with `Get-FileHash` + `gh attestation verify`.
- **A tag-triggered `release.yml`** (`on: push: tags: ['v*']`, `windows-2022`)
  builds, attests, and uploads; all real work lives in `tools/pack-release.ps1`
  so the only workflow-only surface is the trigger/perms/attest/upload.

## Consequences
- Larger exe (~80-150 MB, bundled runtime + natives) — acceptable for a portable
  desktop tool; compression is a one-line future lever.
- A first-run SmartScreen warning persists (no signing cert) and is documented
  honestly in the README with the verify-then-run-anyway path.
- The release is reproducible on GitHub runners; provenance is verifiable
  without a certificate.
- `release.yml` is not exercised until the real `v0.1` tag; risk is contained by
  delegating to the locally/CI-exercised pack script.

## Rejected alternatives
- **Trimmed publish** — breaks reflection / source-gen / interop reached types.
- **Framework-dependent deploy** — forces a .NET install, fails O4.
- **Bundling the WebView2 runtime** — it is a shared evergreen component;
  redistribution is the standalone installer's job.
- **ReadyToRun** — size + cross-gen reproducibility cost for negligible
  single-launch desktop gain.
- **Code-signing** — no certificate (E8).
- **`EnableCompressionInSingleFile`** — first-launch decompression cost, not
  worth it for v0.1.
