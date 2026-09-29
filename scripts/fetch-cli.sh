#!/usr/bin/env bash
# Copies the Windows build of the `fsm` code generation CLI, with its templates,
# into src/FsmEditor.Vsix/cli so the VSIX bundles it.
#
# The CLI is the one of FSM Editor for VS Code (github.com/vincedupuis/fsm-editor-vscode),
# built there with `npm run build:bin`. By default it is taken from a sibling clone:
#   ../fsm-editor-vscode/dist/fsm-<version>-windows-x64/
#
# Usage: scripts/fetch-cli.sh [path to fsm-editor-vscode, or to a dist/fsm-*-windows-x64 folder]
#        scripts/fetch-cli.sh --build   also runs `npm run build:bin -- --target bun-windows-x64` there first
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
build=false
src="$here/../fsm-editor-vscode"
for arg in "$@"; do
  case "$arg" in
    --build) build=true ;;
    *) src="$arg" ;;
  esac
done

if [ -f "$src/fsm.exe" ]; then
  dist="$src"
else
  if $build; then
    (cd "$src" && npm run build:bin -- --target bun-windows-x64)
  fi
  dist="$(ls -d "$src"/dist/fsm-*-windows-x64 2>/dev/null | sort -V | tail -1 || true)"
fi
if [ -z "${dist:-}" ] || [ ! -f "$dist/fsm.exe" ]; then
  echo "No fsm.exe found under $src (dist/fsm-*-windows-x64). Build it there with: npm run build:bin -- --target bun-windows-x64, or run this script with --build." >&2
  exit 1
fi

out="$here/src/FsmEditor.Vsix/cli"
rm -rf "$out"
mkdir -p "$out"
cp "$dist/fsm.exe" "$out/"
cp -R "$dist/templates" "$out/templates"
echo "Copied $(basename "$dist") to src/FsmEditor.Vsix/cli"
