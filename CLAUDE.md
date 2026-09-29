# FSM Editor for Visual Studio

Visual Studio 2022+ extension (VSIX): a visual editor for UML 2.5.1 state machines. `*.fsm` files are XMI 2.5.1 documents holding the UML model plus a UML DI diagram with the layout. It is the Visual Studio edition of FSM Editor for VS Code (`../fsm-editor-vscode`): same concepts, file format, rules and messages, reimplemented in C# (no JavaScript is reused). Code generation runs that project's `fsm` CLI.

## Commands

```sh
dotnet build                 # all projects; VSIX packaging (VSSDK, vsct) only on Windows
dotnet test                  # test/FsmEditor.Core.Tests
scripts/fetch-cli.sh         # or scripts\fetch-cli.ps1: copy fsm.exe + templates into src/FsmEditor.Vsix/cli (gitignored)
```

F5 in Visual Studio (startup project FsmEditor.Vsix) starts the experimental instance; try `examples/MediaPlayer.fsm` and `examples/Order.fsm` (uses `Payment.fsm` as a submachine).

## Architecture

- `src/FsmEditor.Core` (netstandard2.0, no VS references, testable anywhere) holds all the logic:
  - `Model.cs`: `FsmModel`, a flat vertex list where `Parent` is a region id, or a state id for border vertices (entry/exit points of a state, connection point references). Entry/exit points whose parent is a top-level region are the machine's own connection points. `ModelIndex` gives tree navigation.
  - `Xmi.cs` + `XmlTree.cs`: the file format with a lenient XML reader and a writer. `ToXmi(FromXmi(x))` must round-trip, and the output must stay byte-identical to the VS Code extension's.
  - `Validation.cs`, `Expressions.cs` (text grammar), `Labels.cs` (transition label syntax), `Geometry.cs` (compartments, regions, routes, labels; also used for the UML DI region shapes), `DiagramSession.cs` (every editing operation, raising `Committed`), `Tools.cs`, `SvgExport.cs`, `ClipboardJson.cs` (same clipboard JSON as VS Code), `Workspace.cs` (hrefs, machine summaries, submachine resolution).
- `src/FsmEditor.Vsix` (net48, Community.VisualStudio.Toolkit):
  - `FsmEditorFactory` creates `FsmEditorPane` over VS's own text buffer (`IVsTextLines`) as document data, so save, dirty state, undo/redo and a side-by-side XMI text view all share one document. Diagram edits are written by `FsmEditorPane.Write` as one minimal text edit in an undo transaction; buffer changes are debounced, parsed and handed to `EditorControl.Update` (`null` model when the text is the editor's own echo). Validation runs off the UI thread against the machines found by `FsmWorkspace` (solution folder, open buffers preferred), then goes to the diagram and the Error List (`Diagnostics`).
  - `Editor/`: WPF built in code (no XAML). `DiagramCanvas` renders in `OnRender` and hit-tests geometrically; `PropertiesPanel` rebuilds itself per selection; `Theme` maps VS theme colors.
  - Edit commands (Undo/Redo/Cut/Copy/Paste/Delete/Select All) arrive as VS commands in `FsmEditorPane`'s `IOleCommandTarget`; a focused text box gets them instead of the diagram.
  - `CodeGen/FsmCli.cs` runs `fsm <file> --template <hbs> --out <dir>`, parses `wrote …` lines and `file:line: severity: message` problems. `FsmEditorPackage.GenerateCodeAsync` is the whole flow.

## Project rules

- Keep parity with the VS Code extension: text grammar, validation rules and messages, XMI output, clipboard JSON. `test/FsmEditor.Core.Tests/fixtures` holds reference results from it; regenerate them (see fixtures/README.md) when the reference changes, and add a parity case for any format or rule change.
- **Text grammar:** behaviors are argument-less calls separated by `;`; conditions combine calls with `!`, `&&`, `||` and parentheses; `else` only on choice/junction branches; triggers are event names or `after(<number><ms|s|m|h>)`. The editor refuses invalid text, and the validator reports it.
- **Out of scope:** state machine extension/inheritance and the transition-oriented notation.
- Don't reimplement code generation: templates and the code model belong to the `fsm` CLI.
- Core must not reference Visual Studio assemblies. VS code must respect the threading analyzers (the build has no warnings).
- User-facing docs: `README.md`, `docs/UML-CONFORMANCE.md` (update when behavior or rules change), `docs/CODEGEN.md`.
