<#
.SYNOPSIS
  Copies the Windows build of the `fsm` code generator, with its templates,
  into src\FsmEditor.Vsix\cli so the VSIX bundles it.

.DESCRIPTION
  fsm is the program of fsm-cli (github.com/vincedupuis/fsm-cli). It is taken from:
  - a folder holding fsm.exe and templates\, such as an unpacked
    fsm-<version>-windows-x64.zip of an fsm-cli release;
  - or a clone of fsm-cli (by default the sibling ..\fsm-cli): its
    target\release\fsm.exe (under $env:CARGO_TARGET_DIR when set) and templates\.

.PARAMETER Source
  Path of the fsm-cli clone, or of a folder holding fsm.exe.

.PARAMETER Build
  Runs `cargo build --release` in the clone first.
#>
param(
  [string]$Source = (Join-Path $PSScriptRoot '..\..\fsm-cli'),
  [switch]$Build
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (Test-Path (Join-Path $Source 'fsm.exe')) {
  $exe = Join-Path $Source 'fsm.exe'
} else {
  if ($Build) {
    Push-Location $Source
    try {
      cargo build --release --locked
      if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
    } finally { Pop-Location }
  }
  $target = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $Source 'target' }
  $exe = Join-Path $target 'release\fsm.exe'
}
$templates = Join-Path $Source 'templates'
if (-not (Test-Path $exe) -or -not (Test-Path $templates)) {
  throw "No fsm.exe with templates\ found in $Source. Build fsm-cli there (cargo build --release, or run this script with -Build), or pass the folder of an unpacked fsm-<version>-windows-x64.zip from https://github.com/vincedupuis/fsm-cli/releases."
}

$out = Join-Path $root 'src\FsmEditor.Vsix\cli'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item $out -ItemType Directory | Out-Null
Copy-Item $exe $out
Copy-Item $templates (Join-Path $out 'templates') -Recurse
Write-Host "Copied $exe and its templates to src\FsmEditor.Vsix\cli"
