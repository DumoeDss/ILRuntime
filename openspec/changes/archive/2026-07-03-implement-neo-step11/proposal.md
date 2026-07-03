## Why

Neo Step 10 delivered compile-time class VTable construction and O(1) `Callvirt_IL`
/ `Callvirt_CLR` dispatch, but a `callvirt` whose declared method lives on an
**interface** type cannot use a raw interface slot as a class VTable slot — a single
class may implement several interfaces, and each interface owns an independent
0-based method namespace. Today the Neo JIT falls back to the generic `Callvirt`
arm (or leaves such calls un-resolved) whenever `declaringILType.IsInterface` is
true (see `JITCompiler.InitializeCallvirtDispatch`), so interface calls either
miss the fast path or are not yet exercised under Neo. Step 11 adds an interface
**offset map** so that `classVTable[interfaceOffset + interfaceMethodSlot]` lands
on the implementing method, giving interface calls the same O(1) dispatch as class
virtuals and unblocking every downstream Neo step that relies on interface
invocation (delegates via `ldvirtftn`, `isinst`/`castclass`, async builders, etc.).

## What Changes

- Add an interface dispatch map to `ILType`, computed lazily alongside the existing
  `EnsureNeoVTable()` lazy-build pattern: each implemented interface gets a
  **starting slot offset** into the EXISTING `neoVTable` (`IMethod[]`), and each
  interface method gets an independent 0-based method slot resolved via Step 10's
  `SignatureString` slot-key scheme (no new matcher).
- Add a new Neo opcode **`Callvirt_Interface`** to `OpCodeREnum` that encodes the
  interface type identity and the interface method slot, plus an `ExecuteNeo`
  handler that resolves `instance.Type.GetInterfaceVTableOffset(iface) + slot`
  into a `neoVTable` entry and dispatches exactly like `Callvirt_IL`.
- Lower interface `callvirt` in `JITCompiler.InitializeCallvirtDispatch`: when the
  target method's declaring type `IsInterface`, emit `Callvirt_Interface`; non-
  interface virtuals keep the Step 10 `Callvirt_IL` / `Callvirt_CLR` / generic
  `Callvirt` path. Teach the Neo optimizer's call/argument pass so the new opcode
  participates in the same call ABI as the other `Callvirt_*` variants.
- Add `TestCases/NeoStep11Test.cs` covering: IL class implements IL interface,
  multiple interfaces on one class, interface inheritance chain, IL class
  implements a CLR interface (e.g. `IDisposable`) on the IL side, and the explicit
  interface-dispatch-failure path.
- Error semantics: object not implementing the interface, or a missing interface
  slot, throws a clear `MissingMethodException` — no ip overrun, no null-deref,
  no silent Legacy fallback.

## Capabilities

### New Capabilities

- `neo-dispatch`: Neo-mode method dispatch (class VTable + interface offset map).
  This is the home capability for both the Step 10 class-virtual dispatch
  requirements (carried in here so the Step 11 MODIFIED requirement has a place to
  live) and the new Step 11 interface-dispatch requirements.

### Modified Capabilities

- `neo-dispatch`: the "Neo class virtual dispatch" requirement (Step 10) is
  extended so the dispatch table also serves interface calls via an offset map —
  interface dispatch MUST resolve the implementing type's interface offset before
  indexing the class VTable, and MUST NOT reuse a raw interface slot as a class
  VTable slot.

## Impact

- **`ILRuntime/CLR/TypeSystem/ILType.cs`** — new interface map data structure,
  lazy builder, and offset/slot query API (alongside `EnsureNeoVTable` /
  `TryGetNeoVTableSlot`). Reuses `SignatureString` slot-key scheme from Step 10.
- **`ILRuntime/CLR/TypeSystem/IType.cs`** — no public API change required; offset
  query stays on `ILType` (only IL types carry a Neo VTable). Kept out of scope.
- **`ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs`** — add
  `Callvirt_Interface` next to `Callvirt_IL` / `Callvirt_CLR`.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`** —
  `InitializeCallvirtDispatch` gains the interface branch; new
  `EncodeCallvirtInterface` operand packer mirroring `EncodeCallvirtDispatch`.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`** — the call-ABI
  argument pass (`Call` / `Callvirt*` / `Newobj` arm) adds
  `OpCodeREnum.Callvirt_Interface` so push/reg bookkeeping treats it like the
  other callvirt variants.
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** — new
  `case OpCodeREnum.Callvirt_Interface` arm and a `ResolveNeoCallvirtInterfaceTarget`
  helper next to `ResolveNeoCallvirtILTarget` / `ResolveNeoCallvirtCLRTarget`.
- **`TestCases/NeoStep11Test.cs`** — new test fixture following the
  `NeoStep10Test.cs` convention (`public static` parameterless methods, optional
  `[ILRuntimeTest]`, `AssertEqual` helper).
- **Out of scope**: the CLR-exposed `CrossBindingAdapter` path (CLR code calling
  into an IL object through a CLR interface) and generic-interface / explicit
  interface implementation beyond what Step 10's `SignatureString` matcher already
  resolves. These are noted as Non-Goals in `design.md`.
- **No breaking changes** to Legacy mode — all new code is behind
  `#if ENABLE_NEO_MODE`.
