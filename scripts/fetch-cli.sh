#!/usr/bin/env bash
# Copies the Windows build of the `fsm` code generator, with its templates,
# into src/FsmEditor.Vsix/cli so the VSIX bundles it, with the JSON Schemas of
# the file format (cli/schemas, bundled too) and its test cases (cli/cases,
# which the Core tests check the reader and validator against; not bundled).
#
# fsm is the program of fsm-cli (github.com/vincedupuis/fsm-cli). It is taken from:
#   - a folder holding fsm.exe and templates/, such as an unpacked
#     fsm-<version>-windows-x64.zip of an fsm-cli release, with schemas/ and
#     cases/ unpacked from its fsm-schemas-<version>.tar.gz and
#     fsm-cases-<version>.tar.gz;
#   - or a clone of fsm-cli (by default the sibling ../fsm-cli): its
#     target/release/fsm.exe (under $CARGO_TARGET_DIR when set), templates/,
#     schemas/ and tests/cases/.
# Without fsm.exe (on macOS or Linux, where it isn't built), the schemas, the
# cases and the templates are copied anyway, for the tests.
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
cases="$src/cases"
[ -d "$cases" ] || cases="$src/tests/cases"
if [ ! -d "$src/templates" ] || [ ! -d "$src/schemas" ] || [ ! -d "$cases" ]; then
  echo "No templates/, schemas/ and cases (cases/ or tests/cases/) found in $src. Pass a clone of fsm-cli, or the folder of an unpacked fsm-<version>-windows-x64.zip with the fsm-schemas and fsm-cases archives of the same release unpacked in it (https://github.com/vincedupuis/fsm-cli/releases)." >&2
  exit 1
fi

out="$here/src/FsmEditor.Vsix/cli"
rm -rf "$out"
mkdir -p "$out"
cp -R "$src/templates" "$out/templates"
cp -R "$src/schemas" "$out/schemas"
cp -R "$cases" "$out/cases"
if [ -f "$exe" ]; then
  cp "$exe" "$out/"
  echo "Copied $exe, its templates, the schemas and the test cases to src/FsmEditor.Vsix/cli"
else
  echo "No fsm.exe found ($exe): build fsm-cli there on Windows (cargo build --release, or run this script with --build), or pass the folder of an unpacked fsm-<version>-windows-x64.zip from https://github.com/vincedupuis/fsm-cli/releases. Copied the templates, the schemas and the test cases only." >&2
fi
