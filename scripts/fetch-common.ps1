<#
.SYNOPSIS
  Copies fsm-editor-common's sidecar into src\FsmEditor.Vsix\cli so the VSIX bundles it, with fsm-cli's templates
  (cli\templates), the files' JSON Schemas (cli\schemas, bundled too) and the interface's cases (cli\api-cases, which
  the Core tests replay; not bundled).

.DESCRIPTION
  It is taken from:
  - a folder of an fsm-editor-common release's archives, unpacked: fsm-editor-common.exe (the windows-x64 zip's, in
    its folder fsm-editor-common-<version>-windows-x64\ or beside the others), templates\, schemas\ and api-cases\;
  - or clones of fsm-editor-common and fsm-cli side by side (by default the siblings ..\fsm-editor-common and
    ..\fsm-cli): target\release\fsm-editor-common.exe, fsm-cli's templates\ and schemas\, fsm-editor-common's
    schemas\ and api-cases\.

.PARAMETER Source
  The folder of the release's archives, or the clone of fsm-editor-common.

.PARAMETER Build
  Runs `cargo build --release` in the clone first.
#>
param(
  [string]$Source = (Join-Path $PSScriptRoot '..\..\fsm-editor-common'),
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'src\FsmEditor.Vsix\cli'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force (Join-Path $out 'schemas') | Out-Null

if (Test-Path (Join-Path $Source 'Cargo.toml')) {
  $cli = Join-Path $Source '..\fsm-cli'
  if ($Build) { Push-Location $Source; cargo build --release --locked; Pop-Location }
  $target = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $Source 'target' }
  $exe = Join-Path $target 'release\fsm-editor-common.exe'
  if (Test-Path $exe) { Copy-Item $exe $out }
  Copy-Item -Recurse (Join-Path $cli 'templates') (Join-Path $out 'templates')
  Copy-Item (Join-Path $cli 'schemas\*.json') (Join-Path $out 'schemas')
  Copy-Item (Join-Path $Source 'schemas\*.json') (Join-Path $out 'schemas')
  Copy-Item -Recurse (Join-Path $Source 'api-cases') (Join-Path $out 'api-cases')
} else {
  $exe = @(Join-Path $Source 'fsm-editor-common.exe') +
    @(Get-ChildItem $Source -Directory -Filter 'fsm-editor-common-*-windows-x64' | ForEach-Object { Join-Path $_.FullName 'fsm-editor-common.exe' }) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($exe) { Copy-Item $exe $out }
  Copy-Item -Recurse (Join-Path $Source 'templates') (Join-Path $out 'templates')
  Copy-Item (Join-Path $Source 'schemas\*.json') (Join-Path $out 'schemas')
  Copy-Item -Recurse (Join-Path $Source 'api-cases') (Join-Path $out 'api-cases')
}
if (-not (Test-Path (Join-Path $out 'fsm-editor-common.exe'))) {
  Write-Warning "No fsm-editor-common.exe found in ${Source}: build it (cargo build --release, or -Build), or pass a folder of an unpacked release (https://github.com/vincedupuis/fsm-editor-common/releases)."
}
Write-Output "Copied to src\FsmEditor.Vsix\cli: $((Get-ChildItem $out).Name -join ' ')"
