#!/usr/bin/env bash
# Copies fsm-editor-common's sidecar into src/FsmEditor.Vsix/cli so the VSIX bundles it, with fsm-cli's templates
# (cli/templates), the files' JSON Schemas (cli/schemas, bundled too) and the interface's cases (cli/api-cases, which
# the Core tests replay; not bundled).
#
# It is taken from:
#   - a folder of an fsm-editor-common release's archives, unpacked: fsm-editor-common.exe (the windows-x64 zip's, in
#     its folder fsm-editor-common-<version>-windows-x64/ or beside the others), templates/, schemas/ and api-cases/;
#   - or clones of fsm-editor-common and fsm-cli side by side (by default the siblings ../fsm-editor-common and
#     ../fsm-cli): the sidecar from target/release (fsm-editor-common.exe on Windows, fsm-editor-common elsewhere,
#     which the Core tests run), fsm-cli's templates/ and schemas/, fsm-editor-common's schemas/ and api-cases/.
#
# Usage: scripts/fetch-common.sh [folder of the release's archives, or clone of fsm-editor-common]
#        scripts/fetch-common.sh --build   runs `cargo build --release` in the clone first
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
build=false
src="$here/../fsm-editor-common"
for arg in "$@"; do
  case "$arg" in
    --build) build=true ;;
    *) src="$(cd "$arg" && pwd)" ;;
  esac
done
out="$here/src/FsmEditor.Vsix/cli"
rm -rf "$out"
mkdir -p "$out/schemas"

if [ -f "$src/Cargo.toml" ]; then
  cli="$src/../fsm-cli"
  if $build; then
    cargo="cargo"
    command -v cargo > /dev/null || cargo="$HOME/.cargo/bin/cargo"
    (cd "$src" && "$cargo" build --release --locked)
  fi
  release="${CARGO_TARGET_DIR:-$src/target}/release"
  for exe in fsm-editor-common.exe fsm-editor-common; do
    [ -f "$release/$exe" ] && cp "$release/$exe" "$out/"
  done
  cp -R "$cli/templates" "$out/templates"
  cp "$cli/schemas/"*.json "$src/schemas/"*.json "$out/schemas/"
  cp -R "$src/api-cases" "$out/api-cases"
else
  for exe in "$src/fsm-editor-common.exe" "$src"/fsm-editor-common-*-windows-x64/fsm-editor-common.exe; do
    [ -f "$exe" ] && cp "$exe" "$out/" && break
  done
  cp -R "$src/templates" "$out/templates"
  cp "$src/schemas/"*.json "$out/schemas/"
  cp -R "$src/api-cases" "$out/api-cases"
fi
if ! ls "$out"/fsm-editor-common* > /dev/null 2>&1; then
  echo "No fsm-editor-common sidecar found in $src: build it (cargo build --release, or run this script with --build), or pass a folder of an unpacked release (https://github.com/vincedupuis/fsm-editor-common/releases). Copied the templates, the schemas and the cases only." >&2
fi
echo "Copied to src/FsmEditor.Vsix/cli: $(cd "$out" && ls | tr '\n' ' ')"
