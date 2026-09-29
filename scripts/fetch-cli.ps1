<#
.SYNOPSIS
  Copies the Windows build of the `fsm` code generation CLI, with its templates,
  into src\FsmEditor.Vsix\cli so the VSIX bundles it.

.DESCRIPTION
  The CLI is the one of FSM Editor for VS Code (github.com/vincedupuis/fsm-editor-vscode),
  built there with `npm run build:bin`. By default it is taken from a sibling clone:
  ..\fsm-editor-vscode\dist\fsm-<version>-windows-x64\

.PARAMETER Source
  Path of the fsm-editor-vscode clone, or of a dist\fsm-*-windows-x64 folder.

.PARAMETER Build
  Runs `npm run build:bin -- --target bun-windows-x64` in the clone first.
#>
param(
  [string]$Source = (Join-Path $PSScriptRoot '..\..\fsm-editor-vscode'),
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (Test-Path (Join-Path $Source 'fsm.exe')) {
  $dist = Get-Item $Source
} else {
  if ($Build) {
    Push-Location $Source
    try { npm run build:bin -- --target bun-windows-x64 } finally { Pop-Location }
  }
  $dist = Get-ChildItem (Join-Path $Source 'dist') -Directory -Filter 'fsm-*-windows-x64' -ErrorAction SilentlyContinue |
    Sort-Object { [version](($_.Name -replace '^fsm-', '') -replace '-windows-x64$', '') } | Select-Object -Last 1
}
if (-not $dist -or -not (Test-Path (Join-Path $dist.FullName 'fsm.exe'))) {
  throw "No fsm.exe found under $Source (dist\fsm-*-windows-x64). Build it there with: npm run build:bin -- --target bun-windows-x64, or run this script with -Build."
}

$out = Join-Path $root 'src\FsmEditor.Vsix\cli'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item $out -ItemType Directory | Out-Null
Copy-Item (Join-Path $dist.FullName 'fsm.exe') $out
Copy-Item (Join-Path $dist.FullName 'templates') (Join-Path $out 'templates') -Recurse
Write-Host "Copied $($dist.Name) to src\FsmEditor.Vsix\cli"
