#!/usr/bin/env bash
# Copies the Windows build of the `fsm` code generator, with its templates,
# into src/FsmEditor.Vsix/cli so the VSIX bundles it.
#
# fsm is the program of fsm-cli (github.com/vincedupuis/fsm-cli). It is taken from:
#   - a folder holding fsm.exe and templates/, such as an unpacked
#     fsm-<version>-windows-x64.zip of an fsm-cli release;
#   - or a clone of fsm-cli (by default the sibling ../fsm-cli): its
#     target/release/fsm.exe (under $CARGO_TARGET_DIR when set) and templates/.
#
# Usage: scripts/fetch-cli.sh [path to a clone of fsm-cli, or to a folder holding fsm.exe]
#        scripts/fetch-cli.sh --build   also runs `cargo build --release` in the clone first (on Windows)
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
build=false
src="$here/../fsm-cli"
for arg in "$@"; do
  case "$arg" in
    --build) build=true ;;
    *) src="$arg" ;;
  esac
done

if [ -f "$src/fsm.exe" ]; then
  exe="$src/fsm.exe"
else
  if $build; then
    (cd "$src" && cargo build --release --locked)
  fi
  exe="${CARGO_TARGET_DIR:-$src/target}/release/fsm.exe"
fi
if [ ! -f "$exe" ] || [ ! -d "$src/templates" ]; then
  echo "No fsm.exe with templates/ found in $src. Build fsm-cli there on Windows (cargo build --release, or run this script with --build), or pass the folder of an unpacked fsm-<version>-windows-x64.zip from https://github.com/vincedupuis/fsm-cli/releases." >&2
  exit 1
fi

out="$here/src/FsmEditor.Vsix/cli"
rm -rf "$out"
mkdir -p "$out"
cp "$exe" "$out/"
cp -R "$src/templates" "$out/templates"
echo "Copied $exe and its templates to src/FsmEditor.Vsix/cli"
