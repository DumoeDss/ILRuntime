# neo-dispatch

Neo mode method dispatch over the class VTable (`neoVTable`): class virtual
dispatch (Step 10) and interface dispatch (Step 11), both resolved against the
same class VTable with one shared slot-key scheme.

## Requirements

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
  dispatch -- there SHALL be no separate interface method array

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

### Requirement: Neo ldftn instruction

The Neo interpreter SHALL implement the `ldftn` opcode by resolving the method
token (`Operand2`) to an `IMethod` via `AppDomain.GetMethod`, storing the
`IMethod` into a managed ref slot (mStack), and writing that ref slot's index
into the destination's frame byte offset. An `IMethod` is a managed object and
SHALL be represented as a Neo ref slot, NOT as a raw pointer or in-frame
non-managed value. The JIT lowering of `ldftn` (which already resolves the
method token via `InitializeFunctionParam` and stamps the destination register)
SHALL be unchanged.

#### Scenario: ldftn over a static method

- **WHEN** Neo mode executes `Action a = StaticFoo;` where `StaticFoo` is a
  static IL method (the C# compiler emits `ldftn StaticFoo`)
- **THEN** the `ldftn` arm SHALL resolve `StaticFoo` via
  `AppDomain.GetMethod(Operand2)`, store the resolved `ILMethod` into a managed
  ref slot, and write the ref-slot index into the destination frame byte offset
  so the subsequent delegate `.ctor` reads the `ILMethod` back as a managed
  object

#### Scenario: ldftn over a non-virtual instance method

- **WHEN** Neo mode executes `Func<int,int> f = this.NonVirtualM;` where
  `NonVirtualM` is a non-virtual instance method (the compiler emits `ldftn`)
- **THEN** the `ldftn` arm SHALL resolve the method token the same way as for a
  static method, and the bound `this` SHALL be supplied separately by the
  preceding `ldarg`/`ldloc` (the `ldftn` arm itself SHALL NOT resolve a virtual
  override)

### Requirement: Neo ldvirtftn instruction

The Neo interpreter SHALL implement the `ldvirtftn` opcode by reading the
`this` object from the source operand's ref slot, resolving the virtual-method
override via the runtime type's VTable (`Type.GetVirtualMethod`, the Step 10
path), storing the resolved `IMethod` into a managed ref slot, and writing that
ref slot's index into the destination frame byte offset. For a null `this`, the
runtime SHALL throw a clear exception (for example `NullReferenceException`)
and SHALL NOT dereference null or fall back to the Legacy stack model. The JIT
lowering of `ldvirtftn` (which stamps `Register1` = destination and `Register2`
= `this` source) SHALL be unchanged.

#### Scenario: ldvirtftn dispatches the override

- **WHEN** a delegate is constructed over a virtual method on a derived
  instance (`Derived d; Action a = d.VirtualM;` where `Derived` overrides
  `Base.VirtualM`, compiled to `ldvirtftn`)
- **THEN** the `ldvirtftn` arm SHALL read `this` (the `Derived` instance),
  resolve `Derived.VirtualM` via `Type.GetVirtualMethod`, and store the
  resolved override (NOT the base declaration) into the destination ref slot,
  so a later delegate invocation dispatches to the override

#### Scenario: ldvirtftn on a null receiver

- **WHEN** `ldvirtftn` executes with a null `this`
- **THEN** the runtime SHALL throw a clear null-receiver exception and SHALL
  NOT overrun the instruction pointer, dereference null, or silently produce a
  delegate

### Requirement: Neo delegate newobj

The Neo interpreter SHALL implement the `Newobj` arm for an IL delegate type
(`DeclearingType.IsDelegate`) by reading the bound `this` and the `IMethod`
from the operand registers, building a `DelegateAdapter` via
`DelegateManager.FindDelegateAdapter` (caching on the `ILTypeInstance` for an
instance method or on the `ILMethod` for a static method, exactly as the Legacy
arm does), and storing the resulting adapter into the destination ref slot
with its index written to the destination byte offset. The Step-19
`NotImplementedException` placeholder SHALL be replaced. The change SHALL NOT
alter the IL value-type, IL reference-type, or CLR-type `Newobj` paths shipped
in Steps 8b/18.

#### Scenario: Static method delegate construction

- **WHEN** Neo mode executes `Action a = StaticFoo;` (ldftn; newobj Delegate::.ctor)
- **THEN** the `Newobj` delegate arm SHALL build a `DelegateAdapter` bound to
  `(null, StaticFoo)` via `DelegateManager.FindDelegateAdapter(null, ilMethod,
  invokeMethod)`, cache it on `ilMethod.DelegateAdapter`, store the adapter
  into the destination ref slot, and write the index to the destination byte
  offset, so invoking the delegate calls `StaticFoo`

#### Scenario: Instance method delegate construction

- **WHEN** Neo mode executes `Func<int,int> f = instance.M;` (ldftn; newobj)
- **THEN** the `Newobj` delegate arm SHALL build a `DelegateAdapter` bound to
  `(instance, M)`, caching it on the instance
  (`GetDelegateAdapter`/`SetDelegateAdapter`), so invoking the delegate calls
  `instance.M` and the adapter's `InvokeILMethod` pushes the bound instance as
  `this`

#### Scenario: Virtual method delegate construction via ldvirtftn

- **WHEN** Neo mode executes `Action a = derived.VirtualM;` (ldvirtftn; newobj)
- **THEN** the `Newobj` delegate arm SHALL read the already-resolved override
  `IMethod` (resolved by `ldvirtftn`) and bind it to the `derived` instance,
  so invoking the delegate dispatches to `Derived.VirtualM`

#### Scenario: Existing non-delegate newobj paths unchanged

- **WHEN** Neo mode executes `new ILRefType(...)`, `new CLRType(...)`, or an
  IL value-type newobj
- **THEN** the `Newobj` arm SHALL proceed exactly as in Steps 8b/18
  (reference-type instantiation, `InvokeNeoClrMethod(isNewobj:true)`, or the
  IL-VT construction branch respectively), and the introduction of the delegate
  branch SHALL NOT change those paths

### Requirement: Neo delegate invocation calling convention (CLR to IL callback)

The `DelegateAdapter.InvokeILMethod` path SHALL, under Neo mode
(`ENABLE_NEO_MODE`), invoke the bound IL method through `ExecuteNeo` using the
Neo calling convention. The adapter SHALL build a Neo frame (a `byte*`
frameBase sized to the method's `CompiledFrame.TotalStructSize` plus a frame
ref region and a return slot), write the bound `this` (for an instance method)
and each CLR argument into the callee param region using the same per-param
layout the optimizer produces for IL to IL Call (primitives by direct write,
reference args by mStack index, and CLR value-type args via the Step 13b/area4
`WriteNeoValueType` helper), call `ExecuteNeo`, and read the return value back
into a CLR object (primitives by direct read, reference returns by mStack
index, value-type returns via `ReadNeoValueType`). A fresh interpreter SHALL be
obtained from the pool per invocation (`RequestILIntepreter`) and SHALL be
returned to the pool (`FreeILIntepreter`) on every exit path (happy path,
`next`-chain recursion, and exception escape), mirroring the Legacy
`using (BeginInvoke())` -> `Dispose` lifecycle. The Legacy
`StackObject`-based `ILInvoke`/`ILInvokeSub`/`ClearStack` path SHALL remain
byte-identical under `!ENABLE_NEO_MODE`.

#### Scenario: CLR callback into an IL action (List.ForEach)

- **WHEN** an IL `Action<T>` delegate is passed to a CLR method that invokes
  it (for example `List<T>.ForEach(ilAction)`, which calls the delegate from
  CLR code)
- **THEN** the Neo `InvokeILMethod` SHALL build the Neo frame, write the `T`
  argument into the IL action's param region, call `ExecuteNeo`, and return
  cleanly, so the IL action body executes for each list element

#### Scenario: Instance method delegate invocation pushes this

- **WHEN** a delegate bound to an IL instance method is invoked
- **THEN** the Neo `InvokeILMethod` SHALL write the bound instance into the
  callee's slot 0 (the `this` ref slot) so the method body sees its `this`,
  matching the Legacy `ILInvokeSub` instance push

#### Scenario: Value-type parameter and return round-trip

- **WHEN** a delegate has a CLR value-type parameter or a value-type return
  (for example `Func<Vector3,int>` or `Func<int,Vector3>`)
- **THEN** the Neo `InvokeILMethod` SHALL write/read the value type via
  `WriteNeoValueType`/`ReadNeoValueType` (the area4 helpers), producing a
  correct round-trip with no silent byte corruption

#### Scenario: Nested delegate invocation (delegate invoked from inside ExecuteNeo)

- **WHEN** a delegate is invoked from a CLR redirect delegate body that itself
  runs inside `ExecuteNeo` (a CLR -> IL callback re-entering the interpreter)
- **THEN** the Neo `InvokeILMethod` SHALL obtain a fresh pooled interpreter
  for the nested invocation (NOT push onto the in-flight engine stack), and
  SHALL free that interpreter back to the pool on return, so nested delegate
  invocation does not clobber the in-flight frame or leak the engine stack /
  interpreter pool

### Requirement: Neo multicast delegate (next-chain)

Multicast delegates (`+=` / `-=` and the `next`-chain) SHALL work under Neo
mode. The `next` field, `Combine`, and `Remove` are engine-agnostic and SHALL
be reused unchanged. Invoking a multicast delegate SHALL walk the `next`-chain
via the Neo `InvokeILMethod` path, invoking each adapter in order, discarding
intermediate return values, and returning the last delegate's result, matching
Legacy `ILInvokeSub` multicast semantics.

#### Scenario: Multicast combine invokes all

- **WHEN** Neo mode executes `a += Baz; a();` after `a = Foo;`
- **THEN** invoking `a` SHALL call `Foo` then `Baz` (in combine order), and
  for a non-void delegate only the LAST return value SHALL be observable

#### Scenario: Multicast remove

- **WHEN** Neo mode executes `a -= Foo;` after `a = Foo; a += Baz;`
- **THEN** the `Foo` adapter SHALL be removed from the `next`-chain and a
  subsequent invocation SHALL call only `Baz`, matching Legacy `Remove`
  semantics (identity by `(instance, method)`)

### Requirement: Neo async builder Start drives MoveNext via the Neo call convention (not the CLR interface)

The Neo async `Start<TSM>(ref sm)` builder redirection (Step 20) SHALL invoke
the compiler-generated state machine's `MoveNext` IL method through the Neo
call convention, reusing the Step 19 `DelegateAdapter.InvokeILMethod` /
`NeoInvokeSub` fresh-pooled-interpreter frame-build pattern (build at
`StackBase`, write `this` + params, `ExecuteNeo`, read return,
`FreeILIntepreter` in `finally`). The state machine is a HEAP `ILTypeInstance`
(loaded as a reference type despite the C# `struct`). The redirect SHALL NOT
dispatch `MoveNext` through the CLR `IAsyncStateMachine` interface, SHALL NOT
create or consult a `CrossBindingAdaptor`, and SHALL NOT recurse `ExecuteNeo`
in-place on the caller's in-flight frame (the in-place premise corrupted the
caller's frame between `Start` and `get_Task`; a fresh pooled interpreter
isolates the SM). The custom builder redirects SHALL override (FIRST-registered-
wins) the autogen non-functional `*Neo` builder stubs by registering in the
`AppDomain` ctor (which runs before the test-harness binding initializer). The
async resumption path (deferred to `neo-step20-async-suspend`) reuses the same
fresh-pooled-interpreter pattern when a hoisted state machine is resumed on a
CLR continuation thread that has no in-flight Neo frame.

#### Scenario: Start invokes MoveNext via a fresh pooled interpreter

- **WHEN** the Neo `Start<TSM>(ref sm)` builder redirect executes for a
  sync-completing async method
- **THEN** `MoveNext` SHALL be invoked through a fresh pooled interpreter
  (`RequestILIntepreter` / `FreeILIntepreter` in `finally`), with the state-
  machine heap instance written into the fresh frame's slot-0 as `this`, and
  the call SHALL NOT route through any CLR interface, adaptor, or in-place
  recursive `ExecuteNeo` on the caller's frame

#### Scenario: Async resumption reuses the Step 19 fresh-interpreter pattern

- **WHEN** a hoisted state machine is resumed on a CLR continuation callback
  (the deferred suspend path), where the callback thread has no in-flight Neo
  frame
- **THEN** the resumption entry SHALL build a Neo frame at a fresh pooled
  interpreter's `StackBase` (the `NeoInvokeSub` shape from Step 19), write the
  hoisted state machine as `this`, run `ExecuteNeo` on the `MoveNext` IL
  method, read the result, and return the interpreter to the pool on every
  exit path (`FreeILIntepreter` in `finally`), mirroring the Step 19
  `DelegateAdapter.InvokeILMethod` Neo lifecycle

#### Scenario: Builder redirect overrides the autogen stub

- **WHEN** the Neo mode builder redirections are registered for
  `AsyncTaskMethodBuilder<T>` (and the other builder types)
- **THEN** the custom Neo redirects SHALL override (FIRST-registered-wins) the
  autogen `*Neo` builder stubs for the same `MethodBase`s by registering in the
  `AppDomain` ctor (which runs before the test-harness binding initializer), so
  the `Start`→`MoveNext` path is the active implementation and the CLR-
  interface-dispatching autogen stubs are NOT invoked
