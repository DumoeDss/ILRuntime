## ADDED Requirements

### Requirement: Neo interface offset map

Each IL type that implements one or more interfaces SHALL maintain, in Neo mode, a
dispatch map that maps each implemented interface to the **starting slot offset**
of that interface's methods within the type's existing class VTable (`neoVTable`).
The map SHALL be built lazily, mirroring the `EnsureNeoVTable()` lazy-build pattern
used by Step 10. Each interface SHALL own an independent 0-based method slot
namespace; interface method slot identity SHALL reuse the same `SignatureString`
slot-key scheme as the Step 10 class VTable (no separate matcher). A type that
implements no interfaces SHALL produce an empty map, not an error.

#### Scenario: IL class implements a single IL interface

- **WHEN** an IL class implements one IL interface and the type has completed Neo
  load (VTable ensured)
- **THEN** the type SHALL expose a query that returns the starting slot offset of
  that interface's methods in the class VTable, and that offset SHALL be stable
  across repeated queries

#### Scenario: Multiple interfaces implemented by one class

- **WHEN** an IL class implements several interfaces
- **THEN** each interface SHALL receive its own independent offset, and the
  per-interface method slots SHALL NOT alias or pollute each other within the
  class VTable

#### Scenario: Interface inheritance chain

- **WHEN** an interface inherits from one or more parent interfaces and a class
  implements the derived interface
- **THEN** the implementing type SHALL be able to resolve offsets and method slots
  for both the derived interface's own methods and the inherited parent interface
  methods

#### Scenario: Type implements no interfaces

- **WHEN** an IL type implements no interfaces
- **THEN** building / querying the interface map SHALL succeed and return an empty
  result, and SHALL NOT throw

### Requirement: Neo interface callvirt instruction

The Neo interpreter SHALL dispatch `callvirt` to an interface method via a dedicated
`Callvirt_Interface` opcode. The opcode SHALL resolve the implementing instance's
runtime type, look up that type's interface offset for the encoded interface, add
the encoded interface method slot, and index the type's class VTable (`neoVTable`)
to obtain the actual implementing method. Dispatch SHALL then proceed identically
to `Callvirt_IL` (same Neo call ABI, same return handling). The interface offset
SHALL NOT be a raw interface slot used directly as a class VTable slot.

#### Scenario: Call through an interface variable resolves to the IL implementation

- **WHEN** Neo mode executes `IInterface x = new Impl(); x.Foo();`
- **THEN** `Callvirt_Interface` SHALL resolve `Impl.Foo` via
  `classVTable[interfaceOffset(IInterface) + slot(Foo)]` and execute it through
  `ExecuteNeo`, producing the same result as a direct call to `Impl.Foo`

#### Scenario: Override dispatched through interface variable

- **WHEN** a derived IL class overrides the interface-implementing method and the
  call is made through a base-typed interface variable holding the derived instance
- **THEN** `Callvirt_Interface` SHALL dispatch to the derived override, not the
  base implementation

#### Scenario: IL class implementing a CLR interface on the IL side

- **WHEN** an IL class implements a CLR interface (for example `IDisposable`) and a
  method on that interface is invoked through the interface variable
- **THEN** Neo interface dispatch SHALL resolve the IL implementing method and
  execute it; the CLR-exposed `CrossBindingAdapter` path is out of scope for this
  step

#### Scenario: Object does not implement the target interface

- **WHEN** the runtime type of the `this` object does not implement the interface
  encoded in the opcode
- **THEN** the runtime SHALL throw a clear exception (for example
  `MissingMethodException`) describing the missing interface, and SHALL NOT
  overrun the instruction pointer, dereference null, or fall back to the Legacy
  stack model

#### Scenario: Interface slot missing from the class VTable

- **WHEN** the runtime type implements the interface but the resolved
  `offset + slot` index is out of range or points to a null VTable entry
- **THEN** the runtime SHALL throw a clear exception describing the missing slot,
  and SHALL NOT overrun the instruction pointer or dereference null

### Requirement: Neo JIT lowering for interface callvirt

The Neo JIT SHALL lower a `callvirt` instruction to `Callvirt_Interface` when the
target method's declaring type `IsInterface` is true, encoding both the interface
type identity and the interface method slot into the opcode's operands.
Non-interface virtual calls SHALL continue to use the Step 10 lowering
(`Callvirt_IL`, `Callvirt_CLR`, or generic `Callvirt`). The new opcode SHALL
participate in the same Neo call ABI (argument push/register bookkeeping, return
slot) as the other `Callvirt_*` variants so no call-convention divergence is
introduced.

#### Scenario: Interface method declaring type triggers interface lowering

- **WHEN** a `callvirt` target method's declaring type is an interface
- **THEN** the JIT SHALL emit `Callvirt_Interface` and encode the interface type
  identity and interface method slot into the instruction operands

#### Scenario: Non-interface virtual call keeps Step 10 path

- **WHEN** a `callvirt` target method's declaring type is not an interface
- **THEN** the JIT SHALL continue to emit `Callvirt_IL`, `Callvirt_CLR`, or
  generic `Callvirt` exactly as in Step 10

## MODIFIED Requirements

### Requirement: Neo class virtual dispatch

Neo class virtual dispatch SHALL support both class virtual dispatch and interface
dispatch over the same class VTable (`neoVTable`). Interface dispatch SHALL first
resolve the implementing type's interface offset via the interface offset map and
SHALL NOT reuse a raw interface slot as a class VTable slot directly. The class
VTable slot key scheme (`SignatureString`) SHALL be shared by class-virtual and
interface-method slot resolution so that interface slots and class-virtual slots
use one consistent identity rule.

#### Scenario: Class virtual dispatch unchanged

- **WHEN** a non-interface virtual call is made on an IL instance in Neo mode
- **THEN** dispatch SHALL resolve the class VTable slot exactly as in Step 10
  (`Callvirt_IL`), and the introduction of the interface offset map SHALL NOT
  change class virtual dispatch behavior

#### Scenario: Interface dispatch resolved through the offset map

- **WHEN** an interface virtual call is made on an IL instance in Neo mode
- **THEN** dispatch SHALL resolve the implementing type's interface offset, add
  the interface method slot, and index the SAME class VTable used by class virtual
  dispatch — there SHALL be no separate interface method array
