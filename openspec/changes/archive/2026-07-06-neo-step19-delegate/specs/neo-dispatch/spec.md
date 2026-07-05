## ADDED Requirements

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
index, value-type returns via `ReadNeoValueType`). The Legacy
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
- **THEN** the Neo `InvokeILMethod` SHALL push its frame onto the engine stack
  past the in-flight IL frame (advancing `esp`), and SHALL restore `esp` and
  `mStack.Count` on return, so nested delegate invocation does not clobber the
  in-flight frame or leak the engine stack

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
