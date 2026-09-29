# UML Conformance

FSM Editor implements the state machine part of **UML 2.5.1** (OMG formal/17-12-05, chapter 14 "StateMachines"). This document lists what is supported, where the tool deliberately differs from the specification, which rules the validator checks, how files are stored, what the SVG export contains, and how generated code executes. The file format, rules and messages are the same as in FSM Editor for VS Code: both editors open and save the same files.

- [Supported features](#supported-features)
- [Text syntax](#text-syntax)
- [Submachines and connection point references](#submachines-and-connection-point-references)
- [File format](#file-format)
- [Deviations from UML 2.5.1](#deviations-from-uml-251)
- [Validation rules](#validation-rules)
- [SVG export](#svg-export)
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
| Transitions: external, local and internal kinds; multiple triggers, guard, effect | ✔ |
| Completion transitions (no trigger) and time triggers (`after`) | ✔ |
| Protocol state machines: `[precondition] event / [postcondition]` | ✔ |
| Comments attached to any element | ✔ |
| State machine context and documentation | ✔ |

Problems found by the [validator](#validation-rules) show up in the editor status bar, on the offending elements, and in Visual Studio's Error List.

## Text syntax

A state machine holds no variables. Behaviors and conditions are calls to functions **without arguments**; the parentheses only mark the call.

| Where | Syntax | Example |
| --- | --- | --- |
| `entry`, `exit`, `do`, transition effect | calls separated by `;` | `rewind(); showTime()` |
| Guard, state invariant, pre/postcondition | calls combined with `!`, `&&`, `\|\|` and parentheses | `hasDisc() && !(isJammed() \|\| isOpen())` |
| Guard on a choice/junction branch | as above, or `else` | `else` |
| Trigger | event name, or `after(<number><unit>)` with unit `ms`, `s`, `m`, `h` | `play`, `after(500ms)`, `after(2s)` |
| Deferrable event | event name | `play` |

A transition label combines them as `event, after(2s) [isReady() && !isBusy()] / doIt(); log()`. For protocol state machines it is `[isOpen()] event / [isClosed()]`.

Names are letters, digits and `_` and must not start with a digit. Decimal times such as `after(1.5s)` are accepted, but the time must be greater than zero.

Variables, arguments, comparisons, dotted names and change events (`when(...)`) are not accepted. The editor refuses them as you type: the field turns red and nothing is saved. Files edited as XMI text are checked by the validator. The grammar is defined in [`src/FsmEditor.Core/Expressions.cs`](../src/FsmEditor.Core/Expressions.cs).

## Submachines and connection point references

1. In the machine you will reuse (for example `Payment.fsm`), place **entry and exit points on empty canvas**. These are the state machine's own connection points; name them (`retry`, `failed`, …) so they are easy to recognise.
2. In the machine that uses it (for example `Order.fsm`), add a **Submachine State** and pick `Payment` in *Referenced state machine*. The list shows every state machine in the solution folder (or in the folder of the file when it is outside the solution).
3. Select the submachine state. The properties panel lists the entry and exit points found in `Payment.fsm`: click **Add** or **Add all references**. You can also drop an Entry Point, Exit Point or Connection Point Ref tool on the state's border.
4. Draw transitions **into** entry references and **out of** exit references.

The submachine and its connection points are referenced the XMI way, with `href="Payment.fsm#id"` pointing into the other file. Renaming the other machine or its points keeps the references working; moving or renaming the file breaks them, and the validator reports it. The references stay in sync with that file, including unsaved edits to it. See [`examples/Order.fsm`](../examples/Order.fsm) and [`examples/Payment.fsm`](../examples/Payment.fsm).

## File format

A `.fsm` file is an XMI 2.5.1 document with the UML 2.5.1 model and a UML DI diagram. The model part uses standard UML metaclasses only:

| Model | XMI |
| --- | --- |
| State machine / protocol state machine | `uml:StateMachine` / `uml:ProtocolStateMachine`, owned by a `uml:Class` when a context is set |
| Regions, states, final states, pseudostates | `uml:Region`, `uml:State`, `uml:FinalState`, `uml:Pseudostate` with its `kind` |
| Entry/exit points | `connectionPoint` of the state machine or of a state |
| Submachine state, connection point references | `submachine href="Other.fsm#id"`, `uml:ConnectionPointReference` with `entry`/`exit` hrefs |
| `entry`, `exit`, `do`, effects | `uml:OpaqueBehavior` with language `FSM` |
| Guards, invariants, pre/postconditions | `uml:Constraint` holding a `uml:OpaqueExpression` with language `FSM` |
| Triggers and deferrable events | `uml:Trigger` referencing a `uml:SignalEvent` (with its `uml:Signal`) or a relative `uml:TimeEvent` |
| Transitions | `uml:Transition` / `uml:ProtocolTransition` with `kind`, owned by the innermost region containing both ends |
| Comments and documentation | `uml:Comment` with `annotatedElement` |
| Stereotypes | an embedded `uml:Profile` named `FsmStereotypes`, applied to the model |

The layout is a `umldi:UMLStateMachineDiagram`:

- every vertex, region and comment has a `umldi:UMLShape` with `dc:Bounds`;
- every drawn transition has a `umldi:UMLEdge` whose waypoints run from the center of its source, through its bend points, to the center of its target;
- a moved transition label is a `umldi:UMLLabel` on its edge;
- comment links are `umldi:UMLEdge`s from the comment to the annotated element.

Coordinates are rounded to 0.1 px when saved.

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

### Notation and tooling

- The diagram frame with the machine's name in its header isn't drawn. The name, with `{protocol}` for protocol state machines, appears in the editor toolbar and as the SVG export's title.
- Composite states always show their contents. The "hidden decomposition" icon for collapsed composite states isn't supported. Submachine states show the submachine icon.
- The editor doesn't execute or simulate machines, and the validator flags only simple conflicts (several unguarded completion transitions). Run-to-completion semantics, transition priority and conflict resolution are implemented by the [generated code](#code-generation).

## Validation rules

Beyond the [text syntax](#text-syntax), the validator checks the following. ✕ is an error, ⚠ a warning, ℹ a hint. Rules marked *(tool)* aren't UML constraints: they come from the tool's own design or are quality checks.

**Structure**
- ✕ Ids are unique. Every vertex is in a known region, or on the border of a state when it's a connection point.
- ✕ A region contains at most one initial pseudostate, one shallow history and one deep history.
- ⚠ History pseudostates belong inside a composite state.
- ⚠ A composite state entered by default has an initial pseudostate in each region that contains states.
- ⚠ State names are unique within a region.
- ✕ A submachine state cannot also own regions.

**Pseudostates and final states**
- ✕ Initial: exactly one outgoing transition, no guard on it, and no incoming transitions.
- ✕ Final state: no outgoing transitions, regions or entry/exit/do behaviors.
- ✕ Terminate: no outgoing transitions.
- ✕ History: at most one outgoing (default) transition.
- ✕ Fork: exactly one incoming and at least two outgoing transitions, with no guards or triggers on the outgoing ones. ⚠ The targets should be in different orthogonal regions.
- ✕ Join: at least two incoming and exactly one outgoing transition, with no guards or triggers on the incoming ones. ⚠ The sources should be in different orthogonal regions.
- ✕ Choice and junction: at least one incoming and one outgoing transition. ⚠ With several branches, each needs a guard. ℹ An `[else]` branch is suggested.
- ✕ Transitions leaving a pseudostate have no triggers.

**Entry points, exit points and connection point references**
- ✕ Entry and exit points are on the border of a state or at the top level of the machine, never on a submachine state. ⚠ They should only be on composite states.
- ✕ Transitions leaving a state's entry point stay inside the state. Transitions leaving a state's exit point leave the state.
- ⚠ The machine's own entry and exit points are named *(tool)*, and its entry points have an outgoing transition. ✕ Its exit points have no outgoing transitions.
- ✕ A connection point reference is on the border of a submachine state and names an entry or exit point of the referenced machine, with the matching direction. ⚠ Each point is referenced at most once per state.
- ✕ Entry references only receive transitions, and those come from outside the submachine state. Exit references only have outgoing transitions. ⚠ Warns about references without transitions.
- ✕ The referenced state machine file exists and contains the referenced machine.

**Transitions**
- ✕ Source and target exist. Comments are not connected by transitions.
- ✕ An internal transition starts and ends on the same state. A local transition goes from a composite state (or entry point) to a vertex it contains.
- ✕ Time triggers are only on transitions leaving a state *(tool)*.
- ⚠ A simple state has several completion transitions without triggers or guards *(tool)*.

**Protocol state machines**
- ✕ States have no entry/exit/do behaviors, and transitions have no effects.
- ⚠ Guards should be preconditions. Pre/postconditions on a behavioral machine are ignored.

**Reachability** *(tool)*
- ⚠ The top-level region has an initial pseudostate (unless the machine is entered through its own entry points).
- ⚠ Every state can be entered, starting from the initial pseudostate and the machine's own entry points.

## SVG export

*Export as SVG* writes a standalone drawing of the diagram exactly as shown, cropped to its content, in a light theme whatever the editor theme.

## Code generation

*Generate Code…* and the `fsm` command-line tool generate source code from Handlebars templates, and FSM Editor bundles a TypeScript template. Generation stops when the validator reports an error. The generated code follows UML's run-to-completion semantics, with these choices where UML leaves room or the tool simplifies:

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

See [CODEGEN.md](CODEGEN.md) for the templates, the command line and the code model.
