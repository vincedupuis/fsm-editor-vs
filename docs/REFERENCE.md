# Reference

How to write, reuse, check, store, color and export the state machines of FSM Editor. [UML-CONFORMANCE.md](UML-CONFORMANCE.md) lists what it supports of UML 2.5.1. The file format, rules and messages are the same as in FSM Editor for VS Code: both editors open and save the same files.

- [Text syntax](#text-syntax)
- [Submachines and connection point references](#submachines-and-connection-point-references)
- [File format](#file-format)
- [Validation rules](#validation-rules)
- [Colors](#colors)
- [SVG and PNG export](#svg-and-png-export)

## Text syntax

A state machine holds no variables. Behaviors and conditions are calls to functions **without arguments**; the parentheses only mark the call.

| Where | Syntax | Example |
| --- | --- | --- |
| `entry`, `exit`, `do`, transition action | calls separated by `;` | `rewind(); showTime()` |
| Guard, state invariant, pre/postcondition | calls combined with `!`, `&&`, `\|\|` and parentheses | `hasDisc() && !(isJammed() \|\| isOpen())` |
| Guard on a choice/junction branch (in a protocol state machine, its precondition) | as above, or `else` | `else` |
| Context of the state machine | a name | `DoorController` |
| Trigger | event name; `after(<number><unit>)` with unit `ms`, `s`, `m`, `h`; or `all`, any event the state's other transitions don't name | `play`, `after(500ms)`, `all` |
| Deferrable event | event name (not `all`) | `play` |

A transition label combines them as `event, after(2s) [isReady() && !isBusy()] / doIt(); log()`. For protocol state machines it is `[isOpen()] event / [isClosed()]`: a protocol transition has a precondition instead of a guard, and no action.

Names are letters, digits and `_` and must not start with a digit. Decimal times such as `after(1.5s)` are accepted, and so is zero, `after(0s)`, taken as soon as the state is entered.

Variables, arguments, comparisons, dotted names and change events (`when(...)`) are not accepted. The editor refuses them when you enter them: the field turns red and nothing is saved. Files edited as text are checked by the validator. The grammar is fsm-cli's ([`src/expressions.rs`](https://github.com/vincedupuis/fsm-cli/blob/main/src/expressions.rs)).

## Submachines and connection point references

1. In the machine you will reuse (for example `Payment.fsm`), place **entry and exit points on empty canvas**. These are the state machine's own connection points; name them (`retry`, `failed`, …) so they are easy to recognise.
2. In the machine that uses it (for example `Order.fsm`), add a **Submachine State** and pick `Payment` in *Referenced state machine*. The list shows every state machine in the solution folder (or in the folder of the file when it is outside the solution).
3. Select the submachine state. The properties panel lists the entry and exit points found in `Payment.fsm`: click **Add** or **Add all references**. You can also drop an Entry Point, Exit Point or Connection Point Ref tool on the state's border.
4. Draw transitions **into** entry references and **out of** exit references.

**Extract to Submachine** does this from existing elements: select elements of one region, or one composite state, right-click and choose *Extract to Submachine…*, then name the new machine. It is saved next to the current file, as its two files.

- Several elements move with their contents, the transitions between them, and the comments and groups among them. A submachine state covering their bounding box takes their place. If the region's initial pseudostate moves, the region gets a new one, to the submachine state.
- A composite state with one region selected alone: its contents move, and the state becomes the submachine state, keeping its name, behaviors, transitions and place. Its own entry and exit points become the new machine's, with references in their place. A composite state with several regions moves whole instead, like several elements, since the new machine has one region.
- A transition crossing the boundary is split at a connection point, keeping its trigger, guard and action on its source's side. An incoming one goes to an entry reference, then from the new machine's entry point to its target; an outgoing one goes to an exit point of the new machine, then from the exit reference to its target. There is one entry point per inner target and one exit point per outer target, named `to<Target>`. An incoming transition to the target of the new machine's initial transition (without guard or action) goes to the submachine state itself.
- It isn't available, and the menu item's tooltip says why, for points on a state's border selected without their state, local transitions crossing the boundary and history pseudostates at either end of a crossing transition.
- Undo restores the current machine but keeps the new files.

The submachine is referenced by the path of its file (`"submachine": "Payment.fsm"`, relative to this file), and its connection points by their names (`"points": [{ "name": "retry", "type": "entryPoint" }]` on the submachine state). Renaming the other machine keeps the reference working; renaming one of its points, or moving or renaming its file, breaks the references, and the validator reports them. When the new machine's name is a name it holds (a state extracted whole), *Extract to Submachine* proposes `<Name>Machine` instead: names are unique in a machine. The references stay in sync with that file, including unsaved edits to it. See [`examples/Order.fsm`](../examples/Order.fsm) and [`examples/Payment.fsm`](../examples/Payment.fsm).

## File format

A machine is two JSON files side by side: `Name.fsm`, the model, and `Name.fsm.layout`, its drawing. The model file's format is described in full in [fsm-cli's README](https://github.com/vincedupuis/fsm-cli#the-fsm-file-format), with its JSON Schema (`schemas/fsm.schema.json` there); the layout file's [below](#the-layout-file), with its JSON Schema, [`src/FsmEditor.Vsix/Schemas/fsm-layout.schema.json`](../src/FsmEditor.Vsix/Schemas/fsm-layout.schema.json). Visual Studio's text editor shows `.fsm` and `.fsm.layout` files as JSON; both schemas are installed with the extension (`cli\schemas` and `Schemas` in its folder) for the tools that take them. In short:

- The model file holds the machine (its `name`, `type`, `context` and `documentation`) and nothing about the drawing, without ids: elements are identified by their names, unique in the machine. A transition is written inside its source, with its `type`, `trigger`, `guard`, `action` (UML's effect), `precondition`, `postcondition` and `target`, a reference: an element's name, `<owner>.<point>`, `<owner>.history` or `<owner>.deepHistory`. A region's initial pseudostate is `"initial": { "target": … }`, its history pseudostates `"history"` and `"deepHistory"`. A state with several regions has `"regions": [{ "name": …, … }]`; a state with one unnamed region holds that region's fields itself, and a named one goes in `regions` too. A comment's anchors name the machine by its name, a region by its name, an initial pseudostate as `<owner>.initial`. A submachine state has `"submachine": "Payment.fsm"` and its points are the submachine's, by name.
- The layout file mirrors the model file (the same nesting and names) and holds positions, sizes, region sizes and layout, bend points, label positions, colors and groups. Positions are relative to the owning state, bend points to the innermost state containing both ends of their transition, numbers rounded to 0.1: moving a state changes its own position only. A transition's entry is found under its source by its trigger and target (the events it names, however the trigger is spaced), then by order among identical ones: a transition with nothing to store gets an entry anyway when a later one with the same trigger and target has one. An initial or history pseudostate's color is its transition's. Renaming a model file in Solution Explorer renames its layout file with it.
- Both files are written deterministically: reading a file the editor wrote and writing it again gives the same bytes.

The editor never drops or guesses anything in the model. A file that isn't valid JSON, or has the wrong shape (a missing or unknown field, a value of the wrong kind, a newer format), isn't drawn: its problems are listed with their lines, with *Open as Text*. A file of the right shape with a wrong meaning (a reference that names nothing, a name used twice) is drawn, its problems are validation errors, and it is saved back as it was; a transition whose target doesn't exist ends in a marker, and can be retargeted or deleted. An unreadable layout file gives automatic places and a warning, and is replaced only if you confirm when saving. When the two files disagree, an element without a layout entry is placed automatically beside the others of its region (the same way on every open; a message says how many), and a layout entry that matches nothing is dropped at the next save. A reference that names nothing (a target, a comment's anchor) is kept, and names its element again once one with that name exists. Opening a file never changes it, and doesn't mark it as changed (an old file, or one with merge conflicts, included).

**Model hash.** The code generator computes a model hash from the model file, and the bundled templates write it in the header of every generated file of a machine, which is how `fsm write --check` finds generated files that are out of date. It is computed from the model's content, the JSON value without `$schema` (the first 16 hex digits of the SHA-256 of its canonical form: compact, fields sorted by name), so reformatting, reordering fields or changing escapes doesn't change it, and the layout file has no part in it.

**Files of earlier versions.** Until 0.6, a `.fsm` file was XMI 2.5.1 with the UML model and a UML DI diagram in one file. The editor still reads them, and converts one when it is saved: the model file becomes JSON and the layout file is created. The files linked to it by submachine states (the machines it uses and those using it, transitively) are converted at the same time, as their point references go from ids to names. Unnamed elements get generated names, a name used twice gets a number (`Idle2`), spaces, `*` and `.` in names, and in the machine's context, become `_`, and what the format can't hold (names of initial and history pseudostates and of a state's only region, the size of a pseudostate, a second initial pseudostate in a region, a transition to an initial pseudostate, …) is left out: a message lists every file converted, every rename and every loss. A file nobody saves after a change stays in the old format. The `fsm` command refuses old files.

### The layout file

`<Name>.fsm.layout` holds the drawing: positions and sizes, the arrangement and sizes of regions, bend points and label positions of transitions, colors, groups. It mirrors the model file, with the same nesting, names and order:

| Object | Fields |
| --- | --- |
| top level | `format` (`1`, required, first), `points`, then the region fields or `regions` |
| region fields | `initial`, `history`, `deepHistory` (each `x`, `y`, `group`, and for its arrow `bends`, `label`, `color`, `itemColors`), `elements` |
| item of `regions` (several regions, or a named single one) | `name`, `size` (its extent along its state's region layout, with several), then the region fields |
| element | `name`, `x`, `y`, `width`, `height` (states, comments, forks and joins: the others have a fixed size), `color`, `itemColors`, `group`, `regionLayout` (`"horizontal"` or `"vertical"`), `transitions`, `points`, then the region fields or `regions` |
| point | `name`, `x`, `y`, `color`, `itemColors`, `group` (the machine's points), `transitions` |
| transition, under its source | `trigger` and `target` (as in the model file: they identify it), `bends` (list of `{ "x", "y" }`), `label` (`{ "x", "y" }`, from the middle of the line), `color`, `itemColors`. A transition has an entry only when it has something to store. |

`itemColors` gives items their own colors (`#rrggbb`), keyed by the model file's field names (see [fsm-cli's README](https://github.com/vincedupuis/fsm-cli#the-fsm-file-format)): `background` (a composite state's regions), `entry`, `exit`, `do`, `defer`, `invariant`, `trigger`, `guard`, `action`, `precondition`, `postcondition`.

What keeps diffs small:

- Every object is written one field per line, except an object holding only `x` and `y` (a bend point, a `label`, a bare position), which is one line: two people changing different elements don't touch adjacent lines.
- `x` and `y` are relative to the top-left corner of the owning state (the diagram's origin at the top level); a point's position is relative to its owner. Bend points are relative to the top-left corner of the innermost state containing both ends of the transition (the state itself for a transition between a state and what it contains, or a self-transition; the origin at the top level). Moving a state changes its own `x` and `y` only; moving one end of a transition changes nothing in the transition's entry.
- Numbers are rounded to 0.1. Saving rewrites nothing that didn't change: reading the two files and writing them again gives the same bytes.

## Validation rules

Problems found by the validator show up in the editor status bar, on the offending elements, and in Visual Studio's Error List.

Beyond the [text syntax](#text-syntax), the validator checks the following. ✕ is an error, ⚠ a warning. Rules marked *(tool)* aren't UML constraints: they come from the tool's own design or are quality checks.

**Structure**
- ✕ The machine's context, when it has one, is a name.
- ✕ Every element, region (of a state with several; a single region may have one) and point has a name, without spaces or `.` and with `*` only at its end; names are unique in the machine, and point names on their state, which they can't name `initial`, `history` or `deepHistory`. *(tool)* Every vertex is in a known region, or on the border of a state when it's a connection point.
- ✕ A transition's target exists: a reference that names nothing is an error (the diagram draws the transition ending in a marker). A comment anchor that matches nothing is not a problem: its dashed line isn't drawn. A comment can be attached to an element, a transition, a region (its line goes to the region's area) or the machine (no line).
- ✕ A region contains at most one initial pseudostate, one shallow history and one deep history.
- ⚠ History pseudostates belong inside a composite state.
- ⚠ A composite state entered by default has an initial pseudostate in each region that contains states.
- ✕ A submachine state cannot also own regions.

**Pseudostates and final states**
- ✕ Initial: a target (an `initial` without one, as a just-dropped initial pseudostate is saved, is an error), exactly one outgoing transition, no guard on it, and no incoming transitions. ✕ A history pseudostate's default transition with an action has a target.
- ✕ Final state: no outgoing transitions, regions or entry/exit/do behaviors.
- ✕ Terminate: no outgoing transitions.
- ✕ History: at most one outgoing (default) transition.
- ✕ Fork: exactly one incoming and at least two outgoing transitions, which target states and have no guards (preconditions, in a protocol state machine) or triggers. ✕ The targets are in different orthogonal regions.
- ✕ Join: at least two incoming transitions, which come from states and have no guards (preconditions, in a protocol state machine) or triggers, and exactly one outgoing transition. ✕ The sources are in different orthogonal regions.
- ✕ Choice and junction: at least one incoming and one outgoing transition, and at most one `[else]` branch. ⚠ With several branches, each needs a guard (in a protocol state machine, a precondition).
- ✕ Transitions leaving a pseudostate have no triggers.
- ✕ Junctions, choices, forks, history pseudostates and the entry and exit points of states don't form a cycle of transitions without a state: a compound transition going around it would never end, and UML's compound transitions are acyclic.

**Entry points, exit points and connection point references**
- ✕ Entry and exit points are on the border of a state or at the top level of the machine, never on a submachine state. ✕ They are only on composite states.
- ✕ Transitions leaving an entry point are local. Transitions leaving a state's entry point stay inside the state, one into each of its regions at most (of the machine's regions, for its own entry points). Transitions leaving a state's exit point leave the state.
- ✕ Transitions leaving an entry point have no guards (preconditions, in a protocol state machine): all of them are taken.
- ⚠ The machine's own entry points have an outgoing transition. ✕ Its exit points have no outgoing transitions.
- ✕ A connection point reference is on the border of a submachine state and names an entry or exit point of the referenced machine (by its name), with the matching direction. ⚠ Each point is referenced at most once per state.
- ✕ Entry references only receive transitions, and those come from outside the submachine state. Exit references only have outgoing transitions. ⚠ Warns about references without transitions.
- ✕ The referenced state machine file exists and contains the referenced machine.

**Transitions**
- ✕ Source and target exist. Comments are not connected by transitions.
- ✕ An internal transition starts and ends on the same state. A local transition goes from a composite state (or entry point) to a vertex it contains.
- ✕ Time triggers are only on transitions leaving a state *(tool)*.
- The editor offers triggers only on transitions leaving a state and not entering a join, and guards (preconditions, in a protocol state machine) only where they count: not on transitions leaving a fork or an entry point, or entering a join. It draws no transition these rules refuse.
- ⚠ A simple state has several completion transitions without triggers or guards *(tool)*.

**Protocol state machines**
- ✕ States have no entry/exit/do behaviors, and transitions have no actions.
- ✕ No shallow or deep history pseudostates.
- ✕ Transitions have no guards: a precondition takes the guard's place, and on a branch of a choice or junction it selects the branch (it can be `else` there only).
- ⚠ Pre/postconditions on a behavioral machine are ignored.
- The editor doesn't offer what a protocol state machine can't have: history pseudostates, entry/exit/do behaviors, guards and actions. A machine's kind is chosen when it is created; the `type` of its model file changes it, and then what the other kind can't have is reported.

**Reachability** *(tool)*
- ⚠ Each top-level region that contains states has an initial pseudostate (unless the machine is entered through its own entry points).
- ⚠ Every state can be entered, starting from the initial pseudostates of the top-level regions and the machine's own entry points.

**Layout** *(tool, diagram editor only)*
- ⚠ Elements don't overlap, except a state and what it contains. Comments, transitions and labels don't count, nor a point on its own state's border. The diagram editor shows this warning; the Error List and code generation don't.

## Colors

An element's color draws its outline, filled marks and text and tints its fill (for a composite state, only the header above its regions: its background color tints the regions), and an item's color (an internal activity, the invariant, or a line of a transition label) overrides it for that text. An internal transition in its state's compartment takes a single color. Selection and problem highlights replace the outline color while they're shown.

## SVG and PNG export

*Export as SVG* writes a standalone drawing of the diagram exactly as shown, cropped to its content, in a light theme whatever the editor theme. *Export as PNG* writes the same drawing as an image at twice its size, or smaller when a side would exceed 16384 pixels.

The title of the SVG is the machine's name.
