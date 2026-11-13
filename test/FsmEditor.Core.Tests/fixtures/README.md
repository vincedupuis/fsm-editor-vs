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
  and line breaks around it, a time trigger and an event whose ids would collide),
  and `regionids.fsm`, a copy of the umbrella's
  `parity/models/handmade/RegionIds/RegionIds.fsm` (region ids used again by
  another region, a vertex and a transition, and several top-level regions, one
  without an initial pseudostate);
- `<case>.roundtrip.xmi`: `toXmi(fromXmi(<case>.fsm))`.

Regenerate them when the reference implementation changes its rules or format,
from the umbrella repo: `node parity/fixtures.mjs` (needs `npm install` done in
fsm-editor-vscode; `--vscode`, `--vs` and `--jb` point it at other checkouts).
It bundles `src/xmi.ts`, `src/validation.ts` and `media/expressions.js` of the
VS Code extension, rewrites `expected.json` and the `.roundtrip.xmi` files in
both the Visual Studio and JetBrains fixtures, and copies the `<case>.fsm` files
from the Visual Studio fixtures to the JetBrains ones. The grammar texts and the
case list are in the script; a new case is a `<case>.fsm` added to the Visual
Studio fixtures and to the script's list, then to `ParityTests.cs` and
`ParityTest.kt`.
