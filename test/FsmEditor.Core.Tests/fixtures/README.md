# Test fixtures

- `grammar.json`: the results of every grammar check of the VS Code edition (`media/expressions.js`, the reference) for a list of texts, which `ParityTests.cs` compares with this edition's. Made by the umbrella repo's `node parity/fixtures.mjs`, which also copies the next two.
- `layout/valid`, `layout/invalid`: layout files (`.fsm.layout`) that the layout schema accepts and refuses: the VS Code edition's `test/unit/fixtures/layout`. The layout file's format belongs to the editions and is defined by the VS Code edition (its `docs/UML-CONFORMANCE.md`, "The layout file", and `schemas/fsm-layout.schema.json`, copied to `src/FsmEditor.Vsix/Schemas/fsm-layout.schema.json`). `CasesTests.cs` checks that the layout reader takes and refuses the same files.
- `old/`: the examples in the old format (XMI, FSM Editor 0.6 and earlier), the VS Code edition's `test/unit/fixtures/old`: converting them must give `examples/`, byte for byte.

The model file's format is fsm-cli's: `CasesTests.cs` checks the reader and the validator against fsm-cli's test cases, which `scripts/fetch-cli.sh` (into `src/FsmEditor.Vsix/cli/cases`) copies. That the three editions write the same files for the same machines (examples, fsm-cli's cases, old files converted) is checked from the umbrella repo with `node parity/check.mjs`.
