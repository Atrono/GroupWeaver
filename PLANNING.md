# GroupWeaver – Projektplanung

**Projektname:** GroupWeaver (GitHub-Fallback: `groupweaver-app`)
**Stand:** Juni 2026 · **Status:** lebender Plan — **v0.2.0 öffentlich released (2026-06-15)**; Phase 4 läuft
**Projekttyp:** Open-Source-Nebenprojekt, Solo-Start, Read-only-Tool

---

## 1. Projektziel

Eine Windows-Desktop-App, die bestehende Active-Directory-Strukturen als interaktiven Graphen visualisiert (AD als Zentrum, Objekte und Verschachtelungen drumherum) und sie gegen das AGDLP-Prinzip sowie konfigurierbare Namenskonventionen prüft. Die App liest ausschließlich – sie verändert nichts im AD – und nutzt die Berechtigungen des angemeldeten Windows-Benutzers (Integrated Auth, kein Credential-Handling).

**Präzisierung des Claims:** GroupWeaver prüft die **A-G-DL-Struktur und Namenskonventionen**, nicht die Berechtigungsvergabe selbst. Das „P" in AGDLP – die tatsächliche Rechtevergabe über Ressourcen-ACLs (Fileserver, Shares etc.) – liegt außerhalb des AD-Objektmodells und ist für das Tool unsichtbar. Das README erhält dazu einen eigenen Abschnitt **„Was GroupWeaver nicht sieht"**. ACL-/Fileserver-Scanning ist **dauerhaft** außerhalb des Scopes und auch kein Backlog-Item (das wäre ein eigenes Produkt).

Abgrenzung zu bestehenden Tools: BloodHound/Adalanche zeigen Angriffspfade (Security-Perspektive); GroupWeaver zeigt strukturelle Sauberkeit und Konventionstreue (Governance-Perspektive). Diese Nische ist nach aktueller Recherche unbesetzt.

---

## 2. Getroffene Grundsatzentscheidungen

| # | Entscheidung | Festlegung | Begründung |
|---|---|---|---|
| E1 | Plattform | Windows-only (v1) | Zielgruppe AD-Admins arbeitet auf Windows; Integrated Auth gratis |
| E2 | Zugriffsmodell | Read-only, Benutzerkontext | Niedrige Einstiegshürde, keine Credentials im Code, Security-Story im README |
| E3 | Architektur | Dezentral (Client-Tool, keine Server-Komponente) | Keine Installation/DB/Hosting; portable .zip entpacken genügt |
| E4 | Lizenzmodell | Open Source, **MIT** (O2 ✅) | Community-Aufbau, geteilte Regelwerke als Wachstumstreiber |
| E5 | Tech-Stack | **.NET 8 + Avalonia (fix)**; offen ist nur noch die Graph-Library (Cytoscape.js im WebView favorisiert) – Entscheidung per ADR-001 nach Spike (Phase 0) | Windows-first, Tür für Cross-Platform bleibt offen |
| E6 | Regelwerk | Konfigurierbar, JSON/YAML im Profilordner, editierbar unter /settings | Jede Firma hat eigene Konventionen; Export/Import für Community |
| E7 | Priorisierung | 1. Ist-Modus + Graph · 2. Plan-Modus · 3. Gap-Analyse | Ist-Modus allein ist releasewürdig (v0.1) |
| E8 | Release-Integrität | Keine Code-Signierung per Zertifikat; stattdessen SHA256-Hashes + GitHub Build Provenance/Attestations | Kostenlos, simpel, funktionell; Verifikationsweg im README |

---

## 3. Scope

### In Scope v0.1 (erstes öffentliches Release)
- Verbindung zur lokalen AD-Domäne im Benutzerkontext (LDAP, read-only, Paging)
- Demo-Modus mit Fake-AD (JSON) – testbar ohne produktives AD
- Einstiegsfilter: Auswahl von Basis-OU oder Gruppe als Wurzel
- Interaktiver Graph: Knotentypen unterscheidbar (User, GG, DL, UG, OU), Verschachtelungslinien, Lazy-Expand, Drag/Zoom
- Detail-Panel mit Objektattributen („zweiter Blick") – zeigt ausschließlich Attribute der Whitelist
- Regel-Engine: Namenskonventionen (Pattern pro Gruppentyp/Scope) **und** strukturelle Checks als konfigurierbare Verschachtelungsmatrix (plus Zirkularität, leere Gruppen)
- Konventions-Ampel an jedem Knoten inkl. Roll-up-Aggregation + Verstoß-Liste
- Settings-Seite mit Regel-Editor (Live-Vorschau, Import/Export)

### Out of Scope v0.1 (Backlog, siehe Abschnitt 7)
- Plan-Modus, Gap-Analyse
- Entra ID / Microsoft 365 (dynamische Gruppen, Verteiler)
- Export (PNG/SVG, CSV/HTML-Report), PowerShell-Skript-Export
- Schreibende Operationen jeglicher Art
- Mobile, Linux/macOS-Builds
- winget-Einreichung (→ O5, nach v0.1 mit Win-11-Testumgebung)

### Dauerhaft out of scope (kein Backlog)
- ACL-/Berechtigungs-Scanning („P" sichtbar machen) – eigenes Produkt; stattdessen ehrlicher Claim + README-Abschnitt „Was GroupWeaver nicht sieht"

---

## 4. Architektur im Überblick

```
┌─────────────────────────────────────────────┐
│  UI (Avalonia)                              │
│  ├─ Verbindungsdialog / OU-Picker           │
│  ├─ Graph-View (WebView2 + Cytoscape.js,    │
│  │   vendored, gepinnte Version)            │
│  ├─ Detail-Panel (Whitelist-Attribute)      │
│  └─ Settings (Regel-Editor)                 │
├─────────────────────────────────────────────┤
│  Kernlogik (UI-unabhängig, voll testbar)    │
│  ├─ Datenmodell: AdObject, Membership       │
│  ├─ RuleEngine (Matrix-, Namens-, Spezial-  │
│  │   checks, Severities, Ignore-Listen)     │
│  └─ GraphBuilder (inkl. Zirkularitätsschutz)│
├─────────────────────────────────────────────┤
│  IDirectoryProvider                         │
│  ├─ LdapProvider (Integrated Auth, Paging,  │
│  │   Ranged Retrieval, FSP-Handling)        │
│  └─ DemoProvider (JSON-Fake-AD)             │
└─────────────────────────────────────────────┘
```

Leitprinzip: Kernlogik und Provider sind UI-frei → Unit-Tests ohne AD und ohne Fenster möglich; DemoProvider dient zugleich als Entwicklungs- und Testumgebung.

---

## 5. Phasenplan

> Aufwände werden als **Scope-Größen** angegeben: **S** (klein, eine Session), **M** (mittel, 1–2 Sessions), **L** (groß, mehrere Sessions). Keine Kalenderprognose – Fortschritt wird über Meilensteine und das Session-Journal gemessen.

### Phase 0 – Spike (Risiken klären)

**Ziel:** Das verbliebene technische Risiko entscheiden. Wegwerf-Code ausdrücklich erlaubt.

| AP | Arbeitspaket | Scope | Hängt ab von |
|---|---|---|---|
| 0.1 | Graph-Test: 5.000 Knoten **im Avalonia-gehosteten WebView** rendern (Drag, Zoom, Lazy-Expand), inkl. **bidirektionalem Event-Roundtrip**: Knoten-Klick → .NET → Detail-Panel sowie .NET → JS → Expand. Ein Standalone-Browser-Test genügt ausdrücklich nicht. | L | – |
| 0.2 | LDAP-Test: DirectorySearcher mit Integrated Auth, Paging, rekursive Mitgliederauflösung gegen Test-AD | S | – |
| 0.3 | **ADR-001: Graph-Library** dokumentieren (Cytoscape.js-in-WebView vs. native Alternative wie MSAGL) | S | 0.1, 0.2 |

**DoD:** Beide Prototypen laufen; ADR-001 (Graph-Library) liegt im Repo. → **Meilenstein M0**

---

### Phase 1 – Fundament

**Ziel:** Projektgerüst steht, Daten fließen (noch ohne Grafik).

| AP | Arbeitspaket | Scope | Hängt ab von |
|---|---|---|---|
| 1.1 | Repo-Setup: MIT-Lizenz, README-Skelett, .gitignore, THIRD-PARTY-NOTICES (Cytoscape vendored), Issue-Vorlagen, GitHub Project | S | M0 |
| 1.2 | CI-Pipeline: Build + Format + Unit-/DemoProvider-Tests + Release-Artefakt (GitHub Actions); Tests mit Trait `Category=RequiresAd` sind in CI ausgeschlossen | S | 1.1 |
| 1.3 | Datenmodell: AdObject (User/GG/DL/UG/OU/Computer + „extern/unauflösbar"), Membership-Kante, IDirectoryProvider | M | 1.1 |
| 1.4 | DemoProvider: JSON-Fake-AD, ~200 Objekte mit **realistischen, aber erkennbar fiktiven Namen**, inkl. absichtlicher AGDLP-Verstöße, Namensverstöße, einer Zirkularität, leerer Gruppen sowie **Built-in-Imitaten** für den Ignore-Listen-Test | M | 1.3 |
| 1.5 | LdapProvider: read-only, Benutzerkontext, Paging (500–1000), **Ranged Retrieval** für `member` (>1500 Einträge, `member;range=…`), dokumentierte Entscheidung zu `primaryGroupID` (primäre Mitgliedschaft als Kante ja/nein), **Foreign Security Principals → Knotentyp „extern/unauflösbar"** statt Exception, explizite **Attribut-Whitelist** (zugleich Privacy-Grundlage für das Detail-Panel) | L | 1.3 |

**DoD:** Konsolen-/Log-Ausgabe „verbunden, X Gruppen geladen" für beide Provider; CI grün. → **Meilenstein M1**

---

### Phase 2 – Graph-View

**Ziel:** Demo- und echtes AD vollständig explorierbar.

| AP | Arbeitspaket | Scope | Hängt ab von |
|---|---|---|---|
| 2.1 | App-Shell: Hauptfenster, Verbindungsdialog (Domäne erkannt / Demo-Modus), OU-/Gruppen-Picker als Pflichtfilter; Startup-Check auf WebView2-Runtime mit verständlicher Fehlermeldung + Download-Link | M | M1 |
| 2.2 | Graph-Rendering: Knotentypen (Farbe/Form), Verschachtelungslinien, concentric Layout (AD im Zentrum) | L | 2.1 |
| 2.3 | Lazy-Expand per Doppelklick + lokaler Cache + „Aktualisieren"-Button | M | 2.2 |
| 2.4 | Zirkularitätsschutz im Traversal (besuchte DNs tracken) – mit Unit-Test gegen DemoProvider | S | 2.3 |
| 2.5 | Detail-Panel: Klick auf Knoten → Attribute, **ausschließlich aus der Attribut-Whitelist** (AP 1.5) | M | 2.2 |

**DoD:** Demo-AD ohne Absturz/Einfrieren explorierbar inkl. Zirkel; echtes AD im Benutzerkontext getestet. → **Meilenstein M2**

---

### Phase 3 – Regel-Engine & Release v0.1

**Ziel:** Aus dem Viewer wird ein Governance-Tool; erstes öffentliches Release.

| AP | Arbeitspaket | Scope | Hängt ab von |
|---|---|---|---|
| 3.1 | Regelmodell (YAML/JSON) definieren: Strukturchecks als **konfigurierbare Verschachtelungsmatrix** (erlaubte Kanten zwischen User/GG/UG/DL; Default-Matrix AGUDLP-konform), Namens-Pattern pro Gruppentyp/Scope, jede Regel mit `enabled` + `severity` (error/warning/info → rot/gelb/info), Default-Regelwerk strikt AGDLP; Zirkularität und leere Gruppen als separate Checks; **Default-Ignore-Liste** für Well-known-SIDs/Builtin-Container (sichtbar/editierbar) + generischer Ausnahmemechanismus pro Regel (Pattern oder DN-Liste); Beispiel-Regelwerk beilegen | M | M1 |
| 3.2 | RuleEngine per TDD: Matrix-Prüfung, Namensprüfung, Zirkularität, leere Gruppen, Severities, Ignore-/Ausnahmemechanismus | L | 3.1 |
| 3.3 | Settings-Seite: Regel-Editor mit Live-Vorschau („GG_Vertrieb_Lesen ✓"), Matrix-Editor, Ignore-Listen, Import/Export | L | 3.2 |
| 3.4 | Ampel im Graph: Max-Severity am Knoten (rot/gelb/info); **Roll-up-Badge** („⚠ n darunter") für bereits geladene, kollabierte Kinder; Verstoß-Liste als Seitenleiste mit Sprung-zum-Knoten und Hinweis **„Nicht expandierte Bereiche sind ungeprüft."** | M | 3.2, M2 |
| 3.5 | Release v0.1: Primärkanal **GitHub Release mit portablem .zip** (self-contained single-file); **SHA256-Hashes veröffentlichen + GitHub Build Provenance/Attestations aktivieren**; README mit animiertem GIF (nur Demo-Modus!), Systemvoraussetzungen inkl. WebView2-Runtime, Abschnitte „Download verifizieren" und „Was GroupWeaver nicht sieht"; Changelog | S | 3.3, 3.4 |

**DoD:** Alle Regeltests grün; Demo-AD zeigt erwartete Verstöße korrekt an; v0.1 als verifizierbares .zip öffentlich verfügbar. → **Meilenstein M3 = Release v0.1**

---

### Phase 4 – Ausbau (nach v0.1, feedbackgetrieben)

Keine Scope-Schätzung – Priorisierung erfolgt über GitHub-Issues echter Nutzer.

> **Stand 2026-06-15:** Die v0.2- und v0.3-Features (Export, Plan-Modus,
> Gap-Analyse) wurden gebündelt als **0.2.0** veröffentlicht (Tag `v0.2`,
> [Release](https://github.com/Atrono/GroupWeaver/releases/tag/v0.2)) — hash- und
> attestierungs-verifiziert. Vorausgegangen: Pre-Release-Security-Audit (1 HIGH
> gefixt), Cross-Mode-UX-Review (Dark-Theme) und Doku-Abgleich.

| Release | Inhalt | Status |
|---|---|---|
| v0.2 | Export (Verstoß-Report CSV/HTML + Graph als **PNG**; SVG zurückgestellt, ADR-013) · Plan-Modus (Gruppen/User/Mitgliedschaften entwerfen — **panel-basierter** Editor statt Canvas-Drag&Drop, ADR-014; Live-Validierung; inertes PowerShell-Skript, bleibt read-only) | **erledigt** (#56, #59); in **0.2.0** released (2026-06-15) |
| v0.2 | winget-Einreichung (O5) | **zurückgestellt** — braucht eine Win-11-Testumgebung (auf dieser Box nicht verfügbar) |
| v0.3 | Gap-Analyse: Diff Ist-Struktur vs. Plan (ADR-015, #66; SnapshotDiff/GapReport/GapSummary, Graph-Diff-Cues, GapViewModel + GapView) | **erledigt**; in **0.2.0** released (2026-06-15) |
| v0.4 | Entra ID / M365 via Graph API (dynamische Gruppen, Verteiler – z. B. GG „Firma" ↔ Verteiler firma@…) | geplant; offene Follow-ups: ADR-015-Erweiterungen (DiffStatus.Modified, Cross-Scope-Rebase), #54 Graph-Layer-Pruning |

---

## 6. Aufwandsübersicht

| Phase | Scope-Profil |
|---|---|
| 0 – Spike | 1×L, 2×S |
| 1 – Fundament | 1×L, 2×M, 2×S |
| 2 – Graph-View | 1×L, 3×M, 1×S |
| 3 – Regel-Engine & Release | 2×L, 2×M, 1×S |
| **Summe bis v0.1** | **5×L, 7×M, 6×S** |

Fortschrittsmessung erfolgt nicht über Kalenderwochen, sondern über abgeschlossene Arbeitspakete und Session-Journal-Einträge (`docs/journal/`). Grobe Orientierung: S ≈ 1 autonome Session, M ≈ 1–2, L ≈ mehrere.

---

## 7. Meilensteine

| MS | Ergebnis | Sichtbar als |
|---|---|---|
| M0 | Graph-Library entschieden, Risiken geklärt | ADR-001 im Repo |
| M1 | Daten fließen aus Demo + echtem AD | CI grün, Log-Ausgabe |
| M2 | Graph vollständig explorierbar | Screencast/GIF (Demo-Modus) |
| M3 | **Release v0.1** (Ist-Modus + Konventions-Ampel) | GitHub-Release (.zip + Hashes + Attestations) |

---

## 8. Risiken & Gegenmaßnahmen

| Risiko | Auswirkung | Gegenmaßnahme |
|---|---|---|
| Graph-Performance bei großen ADs | Tool wirkt wie Spielzeug | Spike in Phase 0; Pflichtfilter; Lazy-Loading; Paging; Ranged Retrieval |
| Zirkuläre Verschachtelungen | Endlosschleife/Absturz | AP 2.4 mit dediziertem Unit-Test; Zirkel im Demo-AD und im Test-AD enthalten |
| Kein Test-AD verfügbar beim Entwickeln | Entwicklung blockiert | DemoProvider ist Erstklass-Feature (AP 1.4), kein Nachgedanke |
| Drift/Qualitätserosion bei autonomen Sessions | Schleichender Qualitätsverlust, inkonsistente Entscheidungen | Journal-Disziplin (Eintrag je Session, committed + gepusht), reviewer-Gate vor jedem Merge, Stuck-Rule (3 Fehlversuche → BLOCKED-Doku, Themenwechsel) |
| WebView/Avalonia-Brücke hakelig | Mehraufwand UI | AP 0.1 (neu): Spike explizit im Avalonia-gehosteten WebView inkl. Event-Roundtrip; Fallback-Entscheidung in ADR-001 |
| Unsignierte .exe → SmartScreen/AV-Warnungen bei der Zielgruppe | Abschreckung beim ersten Start, Support-Aufwand | Signier-Strategie entschieden (E8): SHA256-Hashes + GitHub Build Provenance/Attestations; README-Abschnitt „Download verifizieren". Restrisiko: SmartScreen-Warnung beim ersten Start bleibt bestehen und wird dort ehrlich dokumentiert („Weitere Informationen → Trotzdem ausführen" + Verifikationsweg) |
| Scope Creep (M365, Plan-Modus zu früh) | v0.1 verzögert sich | Harte Out-of-Scope-Liste (Abschnitt 3); Backlog statt Einbau |

---

## 9. Querschnittsthemen (gelten in allen Phasen)

- **Vorgehen:** leichtgewichtig iterativ, vertikale Schnitte; Trunk-based mit kurzen Feature-Branches und PRs (auch solo)
- **Tests:** RuleEngine und GraphBuilder per TDD/Unit-Tests; AD-abhängige Integrationstests mit xUnit-Trait `Category=RequiresAd` (lokal verpflichtend, in CI ausgeschlossen)
- **UI-Verifikation (zweigeteilt, headless, gemäß CLAUDE.md):** (a) Graph-Schicht als eigenständiges Browser-Bundle via Playwright/headless Chromium – derselbe Code, der im WebView läuft; (b) natives Chrome (Panels, Settings, Dialoge) via Avalonia.Headless; Bewertung gegen `docs/ui-checklist.md` (zwei Abschnitte)
- **Releases:** früh und oft, SemVer (0.x), Conventional Commits → automatischer Changelog; portable .zip + Hashes + Attestations
- **Öffentliche Medien:** README-GIFs, Screencasts, Screenshots in Issues/Releases entstehen ausschließlich im Demo-Modus – nie gegen ein echtes oder Lab-AD
- **Doku:** Architektur-Entscheidungen als kurze ADRs (Markdown im Repo); README mit GIF ab v0.1
- **Backlog:** GitHub Issues + Projects, öffentlich ab Tag 1

---

## 10. Definition of Done (global, je Arbeitspaket)

Code gemergt und CI grün · für Kernlogik: Unit-Tests vorhanden · keine Schreiboperation Richtung AD im Codepfad · bei Architekturentscheidung: ADR ergänzt · Issue geschlossen mit kurzem Ergebnisvermerk.

---

## 11. Offene Punkte

| # | Punkt | Status / Zu klären bis |
|---|---|---|
| O1 | Projektname | ✅ **GroupWeaver** (Fallback `groupweaver-app`) |
| O2 | Lizenz | ✅ **MIT** |
| O3 | Graph-Library (Cytoscape vs. MSAGL vs. Sigma) | offen, reduziert auf Graph-Lib → M0 (ADR-001) |
| O4 | Mindest-OS / Runtime-Strategie | ✅ self-contained single-file .zip; WebView2-Runtime als dokumentierte Voraussetzung |
| O5 | winget-Einreichung nach v0.1 (inkl. Testumgebung Win-11-VM) | Backlog, Ziel v0.2 |
