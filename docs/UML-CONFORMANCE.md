# UML Conformance

FSM Editor and the state machines of UML 2.5.1 (OMG formal/17-12-05, chapter 14 "StateMachines").

- [Supported](#supported)
- [Different](#different)
- [Not supported](#not-supported)

## Supported

- Simple, composite and orthogonal states
- Submachine states and connection point references
- Entry and exit points, of states and of the state machine
- Final states
- Initial pseudostates
- Shallow history pseudostates
- Deep history pseudostates
- Choice pseudostates
- Junction pseudostates
- Fork pseudostates
- Join pseudostates
- Terminate pseudostates
- Entry, exit and do behaviors
- Deferrable events
- State invariants
- Stereotypes on states
- External, local and internal transitions
- Transitions with several triggers, a guard (conditions combined with `!`, `&&`, `||`) and an effect (action)
- `else` on the branches of a choice or junction
- Completion transitions
- Relative time events
- Any-receive events (`all`)
- Protocol state machines, with preconditions and postconditions
- Comments attached to any element
- The context of a state machine, by its name

## Different

### Behaviors

UML allows any `Behavior`, in any language. Here a behavior is argument-less calls separated by `;`, without variables or expressions.

### Guards and constraints

UML allows any `ValueSpecification`. Here they are argument-less calls combined with `!`, `&&`, `||` and parentheses.

### Signal and call events

UML distinguishes them. Here both are event names.

### Time events

UML allows any time expression. Here a relative time event is `after(<number><ms|s|m|h>)`, only on transitions leaving a state.

### Deferrable events

UML allows any event to be deferred. Here only named events can be, not time events.

### Stereotypes

UML defines stereotypes in profiles, for any element, with tagged values. Here a state can have one stereotype, which is only a name.

### Names

UML's names are optional, and unique among the members of a namespace. Here every element and point has a name, and so does every region of a state with several (a single region may have one). Names are unique in the whole machine (a point's, on its owner), can't contain spaces or dots, and contain `*` only as their last character; a point can't be named `initial`, `history` or `deepHistory`. An element left unnamed gets a generated name, ending with `*`, that the diagram doesn't show.

### Top-level regions

UML allows a state machine to have several. Here the file can hold several, but the editor places new elements in the first one and can't add another.

### Initial and history pseudostates

UML allows them to be named, and the top-level initial transition to have a «create» trigger. Here they have no name, the initial transition has no trigger, a history's default transition has only a target and an action, and a transition can't target an initial pseudostate. UML's constraint allows an initial pseudostate without a transition; here it needs exactly one.

### Transitions leaving an entry point

UML lets them have guards. Here they are all taken, as a fork's are: they have no guard (no precondition, in a protocol state machine).

### Context

UML's context is a classifier of the model. Here it is only the classifier's name: the machine stands alone, without classes or operations.

## Not supported

- State machine extension: inheritance and redefinition (`{extended}`, `{final}`), by design
- Transition-oriented notation: signal and action symbols along a transition, by design
- Send signal actions, by design
- Change events (`when`)
- Absolute time events (`at`)
- Event parameters
- Operations referred by protocol transitions: triggers are event names
- Protocol conformance (`ProtocolConformance`)
- Diagram frame with the state machine's name
- Hidden decomposition icon of collapsed composite states
