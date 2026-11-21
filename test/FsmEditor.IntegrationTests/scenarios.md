# IDE scenarios

What every edition's tests check inside its IDE (here: `test/web/index.ts`, on the web and on desktop; Visual Studio:
`test/FsmEditor.IntegrationTests`; JetBrains: `plugin/src/test`). The editing itself is checked by the shared fixtures
(`test/unit/fixtures`); these scenarios check the IDE around it: its documents, commands, problems list, text editor,
files and tool windows.

Each `###` heading is a scenario's id. Every edition's IDE tests name the ids they implement, and their suite fails
when an id of this file is neither implemented nor listed under "Not applicable" with its reason. The other editions
keep a copy of this file (as of the fixtures): after a change, copy it to
`../fsm-editor-vs/test/FsmEditor.IntegrationTests/scenarios.md` and `../fsm-editor-jb/plugin/src/test/scenarios.md`.

The files are copies of `examples/` (`MediaPlayer`, `Order` using `Payment` as a submachine, `demo.log`) in a fresh
folder, plus the files a scenario writes.

## Documents

### open-diagram
Open `MediaPlayer.fsm` as the IDE does on a double-click. It opens in the diagram (not a text editor), the diagram is
drawn, and the document is not marked changed.

### open-as-text-and-back
On `Payment.fsm` in the diagram, run Open as Text: the file is in a text editor. Run Open in Diagram: the diagram is
back, on the same file.

### text-editor-json
`.fsm` and `.fsm.layout` files open as JSON in the IDE's text editor, each with its schema (fsm-cli's for the model,
fsm-editor-common's for the layout).

### text-edit-reaches-diagram
`Broken.fsm` (a copy of `MediaPlayer.fsm` whose `Stopped -play-> Playing` has the guard `count > 0`) opened in the
diagram, then as text (where the IDE can't show both at once, the text replaces the diagram). Changing the guard to
`isReady()` in the text editor clears the guard's problem, and the saved file holds `isReady()`.

### text-closed-unsaved
After an edit of the text that isn't saved, closing its text editor without saving: the diagram (reopened, where the
text replaced it) shows the file, not the edit, and the file on disk is unchanged.

### old-file-opens-unchanged
`Old.fsm`, a machine in the old format (XMI: an initial pseudostate, a state `Idle`, an unnamed state), opens in the
diagram, not marked changed; the file is unchanged on disk and no layout file is written.

### old-file-saved-converts
Saving `Old.fsm` (after a change, where the IDE saves only changed documents) writes the model file in the current
format, with the generated name of the unnamed state
(`state1*`), and its layout file.

### layout-follows-rename
Renaming or moving `Rename.fsm` through the IDE renames or moves `Rename.fsm.layout` with it.

### new-state-machine
The IDE's New File (or New Item) › State Machine creates a machine from the template and opens it in the diagram.

## Problems

### validation-at-line
`Broken.fsm` (above), opened: the IDE's problems list shows the guard's problem (`Guard: …`, explaining there are no
variables) on `Broken.fsm`, at the line of that transition.

### valid-machines-no-problems
The examples, opened, report no problems.

### submachine-references-resolve
`Order.fsm`, opened: its references to `Payment.fsm` and its connection points resolve (no problem about them).

### follow-reference
Following the submachine state `Payment` in `Order`'s diagram (Alt+double-click, the gesture of every edition) opens
`Payment.fsm` in the diagram.

### missing-submachine
Following a submachine state whose file doesn't exist opens nothing and says the file doesn't exist.

## Editing through the IDE

### ide-edit-commands
Through the IDE's own commands (what its keys and Edit menu run; Select All through the diagram's own key where the
IDE's command doesn't reach it): Select All and Copy in `Order`, then Paste in `Empty.fsm`
(`{ "version": 1, "name": "Empty", "type": "behavioral" }`, no elements) adds `Order`'s elements to it, `Cart` among
them; `Order.fsm` is unchanged. (Not into `Order` itself: its initial pseudostate would be a second one in the region,
which the file can't hold, and the paste is refused.) Then, in `Order` (put back afterwards), Cut removes a selected
state, and Delete (where the IDE has a Delete command for the diagram) removes another. In VS Code, Copy, Cut and
Paste go through the diagram's context menu: the IDE runs its own in a webview as the browser's `execCommand`, which
Chromium ignores without a real key press or click. In VS Code for the Web: not run.

### undo-redo-save-both-files
Right after that paste into `Empty`, the IDE's Undo brings the document back to the files' text (and, where the IDE
does so, it is no longer marked changed), Redo brings the paste back, and saving writes both files: the model file
holds `Cart`, the layout file changed. In VS Code for the Web: not run, as above.

### move-changes-layout-only
Moving a state and saving changes `MediaPlayer.fsm.layout` only; `MediaPlayer.fsm` is unchanged.

### hot-exit
An unsaved change survives the IDE's backup and restore (VS Code: hot exit; Visual Studio: AutoRecover after a
crash): the change comes back (the document reopens with it, or the recovery writes it to the file), and until then
the file on disk is unchanged.

## Commands and tool windows

### commands-registered
Every command of the extension is registered and in its menus, named as the edition names them: New State Machine,
Generate Code, Export as SVG, Export as PNG, Open as Text, Open in Diagram (VS Code: Open in FSM Editor), Open Trace,
Show Trace Player (where the Trace Player is a tool window or a view: that window, registered), Compare as Diagrams.

### export-svg-png
Export as SVG writes an SVG of the diagram (`Stopped` in it); Export as PNG writes a PNG twice the SVG's size.

### codegen-bundled
Generate Code on `Order.fsm` with the bundled `ts` template writes `Order.ts` (with `export class Order`), and not
`Payment.ts`: the submachine is read, not generated.

### codegen-workspace-template
A template of the workspace (`{{ machine.name }}: {{ submachine names }}`) writes `Order.txt` holding `Order: Payment`.

### codegen-refused-template
A Handlebars template (`.hbs`) is refused with fsm-cli's message, shown as it is.

### compare-as-diagrams
Compare as Diagrams on `MediaPlayer.fsm` and another version of it (a changed copy, or its last commit where the
edition takes the other version from Git) opens the two as a diagram diff, linked, with the change marked.

### merge-conflicts
`Payment.fsm` with merge conflicts (one conflicting change) opens as the diagram merge, with one conflict, not marked
changed.

### trace-player
Show Trace Player opens its window. Opening `demo.log` shows its records on `MediaPlayer`'s diagram; where logs can be
followed (desktop), records appended to it are shown; a log in a configured format is read with that format.

## Not applicable

A scenario an edition's IDE has no equivalent for, with the reason. Every other scenario is implemented by every
edition.

| Scenario | Edition | Why |
| --- | --- | --- |
| text-closed-unsaved | JetBrains | The Text and Diagram tabs edit one document, and closing an editor never discards its unsaved changes: there is no "close without saving". |
| hot-exit | JetBrains | The IDE has no backup and restore of unsaved documents: it saves changed documents itself (on losing focus, before running, at exit). |
