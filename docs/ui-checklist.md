# UI checklist — GroupWeaver

Judged by the `ui-verifier` agent on every UI change (CLAUDE.md DoD step 2).
Two parts: (A) graph layer via Playwright/headless Chromium, (B) native chrome
via Avalonia.Headless. Screenshots go to `artifacts/ui/` (gitignored).
Stub — extend during Phase 2 alongside the views.

## A. Graph layer (browser bundle)

- [ ] Node types visually distinct (color/shape): User, GG, DL, UG, OU, Computer, extern/unresolvable
- [ ] Nesting edges legible, direction unambiguous
- [ ] Concentric layout sane: root centered, no node overlap at 200 demo nodes
- [ ] Drag/zoom/lazy-expand respond; expanded vs. collapsed state distinguishable
- [ ] Severity colors (red/yellow/info) and roll-up badge ("⚠ n below") readable at default zoom
- [ ] Label contrast sufficient on every node color (no dark-on-dark)

## B. Native chrome (Avalonia)

- [ ] Connection dialog: detected-domain and demo-mode paths both reachable
- [ ] OU/group picker usable; mandatory root filter enforced before loading
- [ ] Detail panel: whitelist attributes only; long DNs truncated with full value available
- [ ] Settings/rule editor: live preview updates; import/export present
- [ ] Violation sidebar: list with jump-to-node; "unexpanded areas are unchecked" notice visible
- [ ] No clipped or overlapping controls at 1280×720 and 1920×1080
