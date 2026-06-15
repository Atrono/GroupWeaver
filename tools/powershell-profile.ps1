# GroupWeaver pwsh profile - source of truth lives HERE in the repo; tools/
# bootstrap.ps1 installs a copy to the user's CurrentUserAllHosts profile
# (<Documents>\PowerShell\profile.ps1). Edit here, then re-run bootstrap.ps1 to
# re-sync. This is dev-box convenience only (a Windows-first lab box that
# defaults to OEM codepage 850) - it ships nothing into the product.
#
# CONTRACT: stay SILENT (never write to stdout). Scripts like tools/build.ps1
# run pwsh WITHOUT -NoProfile and rely on clean output; hooks and the statusline
# use -NoProfile and skip this file entirely. Any banner here would corrupt
# captured output.

# --- UTF-8 console -----------------------------------------------------------
# Force UTF-8 so Unicode renders cleanly (Claude Code's TUI block-art/glyphs,
# umlauts, box-drawing) instead of OEM-850 mojibake. Launching `claude` from a
# pwsh that ran this profile leaves the console codepage at UTF-8 for the whole
# session, including the TUI child process. Guarded: the [Console] encoding
# setters throw when stdout/stdin is redirected (e.g. pwsh output captured by a
# parent) - harmless to skip, a redirected stream is not a live console.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::InputEncoding  = [System.Text.Encoding]::UTF8
}
catch { }
# Pipeline encoding to native commands - UTF-8 without a BOM.
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

# --- Interactive niceties (PSReadLine) ---------------------------------------
# Only at a live interactive console prompt: gate on a non-redirected console
# host so this never loads PSReadLine into captured/script pwsh (build.ps1,
# Claude Code command shells) - keeps those fast and side-effect-free.
if ($Host.Name -eq 'ConsoleHost' -and -not [Console]::IsOutputRedirected `
        -and (Get-Module -ListAvailable -Name PSReadLine)) {
    Import-Module PSReadLine -ErrorAction SilentlyContinue
    Set-PSReadLineOption -EditMode Windows -BellStyle None -ErrorAction SilentlyContinue
    Set-PSReadLineOption -PredictionSource History -ErrorAction SilentlyContinue
    # ListView prediction needs PSReadLine 2.2+; ignore on older builds.
    try { Set-PSReadLineOption -PredictionViewStyle ListView -ErrorAction Stop } catch { }
    Set-PSReadLineOption -HistorySearchCursorMovesToEnd -ErrorAction SilentlyContinue
    Set-PSReadLineKeyHandler -Key UpArrow   -Function HistorySearchBackward -ErrorAction SilentlyContinue
    Set-PSReadLineKeyHandler -Key DownArrow -Function HistorySearchForward  -ErrorAction SilentlyContinue
}
