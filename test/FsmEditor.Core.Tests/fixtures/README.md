# Parity fixtures

Reference results produced by FSM Editor for VS Code (TypeScript), used by
`ParityTests.cs` to check that this C# implementation reads, writes and
validates state machines exactly the same way:

- `expected.json`: validation issues of `examples/*.fsm` and of the cases below,
  and the results of every grammar check for a list of texts;
- `<case>.fsm`: edge-case models (invalid text, broken structure, protocol
  machine with stereotypes, context and documentation) written by `toXmi`;
- `<case>.roundtrip.xmi`: `toXmi(fromXmi(<case>.fsm))`.

They were generated with a small script bundling `src/xmi.ts`,
`src/validation.ts` and `media/expressions.js` of the VS Code extension.
Regenerate them when the reference implementation changes its rules or format.
