# PostToolUse (Edit|Write): format the edited C# file so the local gate's
# `dotnet format --verify-no-changes` never fails on style drift.
# `dotnet format` takes a solution/project, not a bare file -> --include.
$ErrorActionPreference = 'SilentlyContinue'

$payload = [Console]::In.ReadToEnd()
try { $json = $payload | ConvertFrom-Json } catch { exit 0 }
$file = [string]$json.tool_input.file_path
if (-not $file -or $file -notmatch '\.cs$' -or -not (Test-Path $file)) { exit 0 }

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
$sln = Get-ChildItem -Path $root -Filter *.sln -File -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $sln) { exit 0 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { exit 0 }

$rel = [System.IO.Path]::GetRelativePath($root, $file)
dotnet format $sln.FullName --include $rel --no-restore *> $null
exit 0
