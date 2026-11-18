# UML Conformance

FSM Editor implements the state machine part of **UML 2.5.1** (OMG formal/17-12-05, chapter 14 "StateMachines"). This document lists what is supported, where the tool deliberately differs from the specification, which rules the validator checks, how files are stored, what the SVG and PNG exports contain, and how generated code executes. The file format, rules and messages are the same as in FSM Editor for VS Code: both editors open and save the same files.

- [Supported features](#supported-features)
- [Text syntax](#text-syntax)
- [Submachines and connection point references](#submachines-and-connection-point-references)
- [File format](#file-format)
- [Deviations from UML 2.5.1](#deviations-from-uml-251)
- [Validation rules](#validation-rules)
- [SVG and PNG export](#svg-and-png-export)
- [Code generation](#code-generation)

## Supported features

| Element | Support |
| --- | --- |
| Simple, composite and **orthogonal** states (any number of regions, stacked or side by side) | ✔ |
| **Submachine** states (reference another `.fsm` file; Alt+double-click or the properties button opens it) | ✔ |
| **Connection point references** on submachine states, bound to the entry/exit points of the referenced machine | ✔ |
| Entry/exit points of composite states and of the state machine itself | ✔ |
| Final state | ✔ |
| Pseudostates: initial, shallow history, deep history, choice, junction, fork, join, entry point, exit point, terminate | ✔ |
| State behaviors: `entry /`, `exit /`, `do /`, deferrable events, state invariant, stereotype | ✔ |
| Transitions: external, local and internal kinds; multiple triggers, guard, action (UML's effect) | ✔ |
| Completion transitions (no trigger) and time triggers (`after`) | ✔ |
| Protocol state machines: `[precondition] event / [postcondition]` | ✔ |
| Comments attached to any element | ✔ |
| State machine context and documentation | ✔ |

Problems found by the [validator](#validation-rules) show up in the editor status bar, on the offending elements, and in Visual Studio's Error List.

## Text syntax

A state machine holds no variables. Behaviors and conditions are calls to functions **without arguments**; the parentheses only mark the call.

| Where | Syntax | Example |
| --- | --- | --- |
| `entry`, `exit`, `do`, transition action | calls separated by `;` | `rewind(); showTime()` |
| Guard, state invariant, pre/postcondition | calls combined with `!`, `&&`, `\|\|` and parentheses | `hasDisc() && !(isJammed() \|\| isOpen())` |
| Guard on a choice/junction branch | as above, or `else` | `else` |
| Trigger | event name, or `after(<number><unit>)` with unit `ms`, `s`, `m`, `h` | `play`, `after(500ms)`, `after(2s)` |
| Deferrable event | event name | `play` |

A transition label combines them as `event, after(2s) [isReady() && !isBusy()] / doIt(); log()`. For protocol state machines it is `[isOpen()] event / [isClosed()]`.

Names are letters, digits and `_` and must not start with a digit. Decimal times such as `after(1.5s)` are accepted, but the time must be greater than zero.

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

- The model file holds the machine and nothing about the drawing, without ids: elements are identified by their names, unique in the machine. A transition is written inside its source, with its `type`, `trigger`, `guard`, `action` (UML's effect), `precondition`, `postcondition` and `target`, a reference: an element's name, `<owner>.<point>`, `<owner>.history` or `<owner>.deepHistory`. A region's initial pseudostate is `"initial": { "target": … }`, its history pseudostates `"history"` and `"deepHistory"`. A state with several regions has `"regions": [{ "name": …, … }]`; a state with one region holds that region's fields itself, and the region has no name. A submachine state has `"submachine": "Payment.fsm"` and its points are the submachine's, by name.
- The layout file mirrors the model file (the same nesting and names) and holds positions, sizes, region sizes and layout, bend points, label positions, colors and groups. Positions are relative to the owning state, bend points to the innermost state containing both ends of their transition, numbers rounded to 0.1: moving a state changes its own position only. A transition's entry is found under its source by its trigger and target (the events it names, however the trigger is spaced), then by order among identical ones: a transition with nothing to store gets an entry anyway when a later one with the same trigger and target has one. An initial or history pseudostate's color is its transition's. Renaming a model file in Solution Explorer renames its layout file with it.
- Both files are written deterministically: reading a file the editor wrote and writing it again gives the same bytes.

The editor never drops or guesses anything in the model. A file that isn't valid JSON, or has the wrong shape (a missing or unknown field, a value of the wrong kind, a newer format), isn't drawn: its problems are listed with their lines, with *Open as Text*. A file of the right shape with a wrong meaning (a reference that names nothing, a name used twice) is drawn, its problems are validation errors, and it is saved back as it was; a transition whose target doesn't exist ends in a marker, and can be retargeted or deleted. An unreadable layout file gives automatic places and a warning, and is replaced only if you confirm when saving. When the two files disagree, an element without a layout entry is placed automatically beside the others of its region (the same way on every open; a message says how many), and a layout entry that matches nothing is dropped at the next save. A reference that names nothing (a target, a comment's anchor) is kept, and names its element again once one with that name exists. Opening a file never changes it, and doesn't mark it as changed (an old file, or one with merge conflicts, included).

**Model hash.** The code generator computes a model hash from the model file, and the bundled templates write it in the header of every generated file of a machine, which is how `fsm write --check` finds generated files that are out of date. It is computed from the model's content, the JSON value without `$schema` (the first 16 hex digits of the SHA-256 of its canonical form: compact, fields sorted by name), so reformatting, reordering fields or changing escapes doesn't change it, and the layout file has no part in it.

**Files of earlier versions.** Until 0.6, a `.fsm` file was XMI 2.5.1 with the UML model and a UML DI diagram in one file. The editor still reads them, and converts one when it is saved: the model file becomes JSON and the layout file is created. The files linked to it by submachine states (the machines it uses and those using it, transitively) are converted at the same time, as their point references go from ids to names. Unnamed elements get generated names, a name used twice gets a number (`Idle2`), spaces, `*` and `.` in names become `_`, the machine's context is dropped, and what the format can't hold (names of initial and history pseudostates and of a state's only region, the size of a pseudostate, a second initial pseudostate in a region, a transition to an initial pseudostate, …) is left out: a message lists every file converted, every rename and every loss. A file nobody saves after a change stays in the old format. The `fsm` command refuses old files.

### The layout file

`<Name>.fsm.layout` holds the drawing: positions and sizes, the arrangement and sizes of regions, bend points and label positions of transitions, colors, groups. It mirrors the model file, with the same nesting, names and order:

| Object | Fields |
| --- | --- |
| top level | `format` (`1`, required, first), `points`, then the region fields or `regions` |
| region fields | `initial`, `history`, `deepHistory` (each `x`, `y`, `group`, and for its arrow `bends`, `label`, `color`, `itemColors`), `elements` |
| item of `regions` | `name`, `size` (its extent along its state's region layout), then the region fields |
| element | `name`, `x`, `y`, `width`, `height` (states, comments, forks and joins: the others have a fixed size), `color`, `itemColors`, `group`, `regionLayout` (`"horizontal"` or `"vertical"`), `transitions`, `points`, then the region fields or `regions` |
| point | `name`, `x`, `y`, `color`, `itemColors`, `group` (the machine's points), `transitions` |
| transition, under its source | `trigger` and `target` (as in the model file: they identify it), `bends` (list of `{ "x", "y" }`), `label` (`{ "x", "y" }`, from the middle of the line), `color`, `itemColors`. A transition has an entry only when it has something to store. |

`itemColors` gives items their own colors (`#rrggbb`), keyed by the model file's field names (see [fsm-cli's README](https://github.com/vincedupuis/fsm-cli#the-fsm-file-format)): `background` (a composite state's regions), `entry`, `exit`, `do`, `defer`, `invariant`, `trigger`, `guard`, `action`, `precondition`, `postcondition`.

What keeps diffs small:

- Every object is written one field per line, except an object holding only `x` and `y` (a bend point, a `label`, a bare position), which is one line: two people changing different elements don't touch adjacent lines.
- `x` and `y` are relative to the top-left corner of the owning state (the diagram's origin at the top level); a point's position is relative to its owner. Bend points are relative to the top-left corner of the innermost state containing both ends of the transition (the state itself for a transition between a state and what it contains, or a self-transition; the origin at the top level). Moving a state changes its own `x` and `y` only; moving one end of a transition changes nothing in the transition's entry.
- Numbers are rounded to 0.1. Saving rewrites nothing that didn't change: reading the two files and writing them again gives the same bytes.

## Deviations from UML 2.5.1

### Deliberately excluded

| UML feature | Status | Reason |
| --- | --- | --- |
| State machine extension (inheritance): `{extended}`, `{final}`, redefinition of states, regions and transitions (§14.3.3) | Not supported. An `extends` property in a file is dropped when the file is loaded. | Out of scope by design. Reuse goes through submachine states. |
| Transition-oriented notation: receive-signal, send-signal and action symbols drawn along a transition (§14.2.4.9) | Not supported | It only duplicates the text label, and sending signals has no place in the call-only syntax. |
| Send signal actions | Not supported | Behaviors are argument-less function calls only. |

### Restricted

| UML | FSM Editor |
| --- | --- |
| Behaviors are any `Behavior`, often `OpaqueBehavior` in any language | Only argument-less calls separated by `;` ([Text syntax](#text-syntax)). There are no variables or expressions. |
| Guards and constraints are any `ValueSpecification` | Only calls combined with `!`, `&&`, `\|\|` and parentheses. |
| Triggers can be signal, call, change, time or any-receive events | Event names (signal and call events are not distinguished), plus relative time events `after(...)`. Change events (`when`), absolute time events (`at`) and `all` are not supported. |
| Time events use any time expression | Only `after(<number><ms\|s\|m\|h>)`, and only on transitions leaving a state. |
| Any event can be deferred | Only named events. Time events cannot be deferred. |
| `else` is a guard value usable on any transition | Only on transitions leaving a choice or junction. |
| The transition from the top-level initial pseudostate may have a trigger stereotyped «create» | Transitions leaving any pseudostate, including initial, cannot have triggers. |
| Stereotypes are defined in profiles and can have tagged values | Each file embeds a generated profile with one stereotype per name used, applicable to states only. Stereotype names are letters, digits and `_`. There are no tagged values. |
| A state machine can have several top-level regions | Allowed in the file format, but the editor always places new elements in the first top-level region and has no command to add another. |
| Only composite states may own entry/exit points (a constraint) | Reported as a warning rather than an error. |
| Names are optional, and unique only among the members of a namespace (a region's subvertices, for instance) | Every element, region of a state with several and point has a name, unique in the whole machine (points: on their state); elements without a name of their own get a generated one ending with `*`, not shown on the diagram. Names are the elements' identity in the file. A name has no spaces and no `.`, and `*` only at its end. *(tool)* |
| A region of a state with one region can be named | It has no name: the state holds its contents. |
| Initial and history pseudostates can be named, and can have any outgoing transitions the constraints allow | They have no name, and a region has at most one of each; an initial pseudostate's transition is its `target` and `action`, a history's default transition likewise. A transition can't target an initial pseudostate. |
| A state machine can have a context classifier | Not supported: the machine stands alone. |

### Notation and tooling

- The diagram frame with the machine's name in its header isn't drawn. The name, with `{protocol}` for protocol state machines, appears in the editor toolbar and as the SVG export's title.
- Colors are a notation option: an element's color draws its outline, filled marks and text and tints its fill (for a composite state, only the header above its regions: its background color tints the regions), and an item's color (an internal activity, the invariant, or a line of a transition label) overrides it for that text. An internal transition in its state's compartment takes a single color. Selection and problem highlights replace the outline color while they're shown.
- Composite states always show their contents. The "hidden decomposition" icon for collapsed composite states isn't supported. Submachine states show the submachine icon.
- The editor doesn't execute or simulate machines, and the validator flags only simple conflicts (several unguarded completion transitions). Run-to-completion semantics, transition priority and conflict resolution are implemented by the [generated code](#code-generation).

## Validation rules

Beyond the [text syntax](#text-syntax), the validator checks the following. ✕ is an error, ⚠ a warning, ℹ a hint. Rules marked *(tool)* aren't UML constraints: they come from the tool's own design or are quality checks.

**Structure**
- ✕ Every element, region (of a state with several) and point has a name, without spaces or `.` and with `*` only at its end; names are unique in the machine, and point names on their state, which they can't name `history` or `deepHistory`. *(tool)* Every vertex is in a known region, or on the border of a state when it's a connection point.
- ✕ A transition's target exists: a reference that names nothing is an error (the diagram draws the transition ending in a marker). A comment anchor that matches nothing is not a problem: its dashed line isn't drawn.
- ✕ A region contains at most one initial pseudostate, one shallow history and one deep history.
- ⚠ History pseudostates belong inside a composite state.
- ⚠ A composite state entered by default has an initial pseudostate in each region that contains states.
- ✕ A submachine state cannot also own regions.

**Pseudostates and final states**
- ✕ Initial: a target (an `initial` without one, as a just-dropped initial pseudostate is saved, is an error), exactly one outgoing transition, no guard on it, and no incoming transitions. ✕ A history pseudostate's default transition with an action has a target.
- ✕ Final state: no outgoing transitions, regions or entry/exit/do behaviors.
- ✕ Terminate: no outgoing transitions.
- ✕ History: at most one outgoing (default) transition.
- ✕ Fork: exactly one incoming and at least two outgoing transitions, with no guards or triggers on the outgoing ones. ⚠ The targets should be in different orthogonal regions.
- ✕ Join: at least two incoming and exactly one outgoing transition, with no guards or triggers on the incoming ones. ⚠ The sources should be in different orthogonal regions.
- ✕ Choice and junction: at least one incoming and one outgoing transition. ⚠ With several branches, each needs a guard. ℹ An `[else]` branch is suggested.
- ✕ Transitions leaving a pseudostate have no triggers.
- ✕ Junctions, choices, forks, history pseudostates and the entry and exit points of states don't form a cycle of transitions without a state: a compound transition going around it would never end *(tool)*.

**Entry points, exit points and connection point references**
- ✕ Entry and exit points are on the border of a state or at the top level of the machine, never on a submachine state. ⚠ They should only be on composite states.
- ✕ Transitions leaving a state's entry point stay inside the state. Transitions leaving a state's exit point leave the state.
- ⚠ The machine's own entry points have an outgoing transition. ✕ Its exit points have no outgoing transitions.
- ✕ A connection point reference is on the border of a submachine state and names an entry or exit point of the referenced machine (by its name), with the matching direction. ⚠ Each point is referenced at most once per state.
- ✕ Entry references only receive transitions, and those come from outside the submachine state. Exit references only have outgoing transitions. ⚠ Warns about references without transitions.
- ✕ The referenced state machine file exists and contains the referenced machine.

**Transitions**
- ✕ Source and target exist. Comments are not connected by transitions.
- ✕ An internal transition starts and ends on the same state. A local transition goes from a composite state (or entry point) to a vertex it contains.
- ✕ Time triggers are only on transitions leaving a state *(tool)*.
- ⚠ A simple state has several completion transitions without triggers or guards *(tool)*.

**Protocol state machines**
- ✕ States have no entry/exit/do behaviors, and transitions have no actions.
- ⚠ Guards should be preconditions. Pre/postconditions on a behavioral machine are ignored.

**Reachability** *(tool)*
- ⚠ Each top-level region that contains states has an initial pseudostate (unless the machine is entered through its own entry points).
- ⚠ Every state can be entered, starting from the initial pseudostates of the top-level regions and the machine's own entry points.

**Layout** *(tool, diagram editor only)*
- ⚠ Elements don't overlap, except a state and what it contains. Comments, transitions and labels don't count, nor a point on its own state's border. The diagram editor shows this warning; the Error List and code generation don't.

## SVG and PNG export

*Export as SVG* writes a standalone drawing of the diagram exactly as shown, cropped to its content, in a light theme whatever the editor theme. *Export as PNG* writes the same drawing as an image at twice its size, or smaller when a side would exceed 16384 pixels.

## Code generation

*Generate Code…* and the `fsm write` command generate source code from Jinja templates (see [CODEGEN.md](CODEGEN.md)), and FSM Editor bundles a TypeScript template and a C++ template for Boost.SML. Generation stops when the validator reports an error. The generated code follows UML's run-to-completion semantics, with these choices where UML leaves room or the tool simplifies:

| UML | Generated code |
| --- | --- |
| Conflicting transitions in orthogonal regions: the choice among them isn't specified | The first transition selected fires; a transition whose source has been left meanwhile is skipped. |
| Order of the actions of orthogonal regions isn't specified | Regions are exited and entered in document order. |
| Junction: static conditional branch | Expanded into one transition per path with the guards combined, so a transition only fires when a complete path is enabled. |
| Choice with no enabled branch: ill-formed | The generated code throws an error. |
| Guards on the transitions leaving an entry point | Ignored: all of them are taken, like a fork. |
| A region last exited through its final state, re-entered through history | Default entry, as if there were no history. |
| Protocol violations are left to the implementation | `onConstraintViolation('precondition', …)` when an event finds no transition because a precondition is false. Postconditions are checked after the transition, invariants after every step. |
| Do activities run concurrently with the state | `start…()`/`stop…()` calls. Their end is reported with `activityDone()`, which completes the state. |

The Boost.SML template keeps these semantics, with one exception: Boost.SML fires the transitions of orthogonal regions region after region, so when several regions react to the same event, a later region's guards are evaluated after the earlier regions' transitions fired. The template warns where this can happen.

See [CODEGEN.md](CODEGEN.md) for the templates, the command line and the code model.
