# Parity fixtures

Reference results produced by FSM Editor for VS Code (TypeScript), used by
`ParityTests.cs` to check that this C# implementation reads, writes and
validates state machines exactly the same way:

- `expected.json`: validation issues of `examples/*.fsm` and of the cases below,
  and the results of every grammar check for a list of texts;
- `<case>.fsm`: edge-case models (invalid text, broken structure, protocol
  machine with stereotypes, context and documentation, cycles of pseudostates)
  written by `toXmi`, except `colors.fsm`, whose colors were then edited by hand
  (uppercase and invalid values, unknown items, a missing element, a foreign
  extension) to check that reading them is lenient, and `loops.fsm`, given extra
  waypoints on self-transitions (as older versions wrote them) to check that
  reading resets a state's loop unless it has one or three, and keeps a
  pseudostate's bend points, and `regions.fsm`, composite states whose regions
  have their own sizes (stacked, side by side, a single region, and sizes that
  don't fit their state), and `groups.fsm`, whose groups were edited by hand
  (members out of file order, a member on a state's border, members in another
  region, a vertex in two groups, a missing member, a group without an id and
  groups of one) to check which groups reading keeps, and `edges.fsm`
  (completion transitions with and without guards, a comment text with spaces
  and line breaks around it, a time trigger and an event whose ids would collide,
  and the model hash line that files saved by earlier versions end with, which
  reading ignores and writing leaves out), and `regionids.fsm` (region ids used
  again by another region, a vertex and a transition, and several top-level
  regions, one without an initial pseudostate), and `settle.fsm`, written by an
  earlier version and then edited by hand (a composite state without its region
  shapes, a transition in an outer region, a coordinate off the 0.1 px grid, an
  infinite width, and a label that the earlier writer moved by 0.1 px on each
  save), whose round trip must save to itself, as every case's must;
- `<case>.roundtrip.xmi`: `toXmi(fromXmi(<case>.fsm))`, which saves to itself:
  `toXmi(fromXmi(<case>.roundtrip.xmi))` gives it back unchanged.

Regenerate them when the reference implementation changes its rules or format,
from the umbrella repo: `node parity/fixtures.mjs` (needs `npm install` done in
fsm-editor-vscode; `--vscode`, `--vs`, `--jb` and `--cli` point it at other
checkouts). It bundles `src/xmi.ts`, `src/validation.ts` and
`media/expressions.js` of the VS Code extension, rewrites `expected.json` and the
`.roundtrip.xmi` files in the Visual Studio and JetBrains fixtures and in
fsm-cli's (`tests/fixtures`, which also gets the VS Code examples), and copies
the `<case>.fsm` files from the Visual Studio fixtures to the other two. The grammar texts and the
case list are in the script; a new case is a `<case>.fsm` added to the Visual
Studio fixtures and to the script's list, then to `ParityTests.cs` and
`ParityTest.kt`.
