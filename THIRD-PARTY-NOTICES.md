# Third-party notices

GroupWeaver ships third-party components under their own licenses, listed
below. NuGet dependencies (Avalonia, etc.) will be enumerated here as they are
added to the product.

## NuGet packages

Shipped (non-vendored) runtime dependencies; their license texts travel inside
the packages themselves. Versions are pinned exactly (ADR-001/ADR-003).

- **Avalonia 11.3.17** (+ Avalonia.Desktop, Avalonia.Themes.Fluent) — MIT —
  https://avaloniaui.net/
- **Avalonia.Controls.WebView 11.4.0** — MIT — https://avaloniaui.net/
  (the official AvaloniaUI WebView, FOSS since March 2026; WebView2 backend on
  Windows, see [ADR-001](docs/adr/001-graph-library.md))
- **CommunityToolkit.Mvvm 8.4.2** — MIT —
  https://github.com/CommunityToolkit/dotnet

## Cytoscape.js 3.34.0

- License: MIT
- Source: https://js.cytoscape.org/
- A pinned, vendored copy of this exact version ships inside the application
  (see [ADR-001](docs/adr/001-graph-library.md)).
- **Upstream provenance:** `src/App/web/vendor/cytoscape.min.js` is byte-identical
  to the official npm distribution `cytoscape@3.34.0/dist/cytoscape.min.js`
  (e.g. https://unpkg.com/cytoscape@3.34.0/dist/cytoscape.min.js).
  Verified SHA256 (raw, LF):
  `9c2a3bf2592e0b14a1f7bec07c03a54f16dedf32af9cd0af155c716aa6c87bc3`
  The file is marked `-text` in `.gitattributes` so it is stored and checked out
  byte-for-byte; `WebBundleTests` asserts the vendored copy still matches this
  hash, so the supply chain is independently re-verifiable, not self-referential.

```
Copyright (c) 2016-2026, The Cytoscape Consortium.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
