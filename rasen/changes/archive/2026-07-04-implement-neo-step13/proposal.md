## Why

Step 5 shipped the *basics* of IL value-type Box/Unbox for the Neo VM, but
three of the five Box/Unbox completion areas (Step 13) are still open and throw
`NotImplementedException` (Step-13-tagged):

- CLR value-type Box (`ILIntepreter.Neo.cs:1661`), Unbox/Unbox_Any
  (`ILIntepreter.Neo.cs:1904`), and CLR value-type `Initobj`
  (`ILIntepreter.Neo.cs:1604`).
- `constrained.` callvirt specializing on a value-type `this` is not wired.

These are reached by everyday IL (boxing a `Vector3`-like CLR struct, `foreach`
over `List<int>`, `T.ToString()` where `T : struct`), so the Neo VM cannot yet
run that code. This change closes the box/unbox mechanics and the
`constrained.` specialization, while *explicitly deferring* the binding-codegen
+ CLR-call-ABI overhaul (areas 4-5) to a Step 13b follow-up because that
overhaul touches every CLR method call and is the single highest-regression
risk in the roadmap.

## What Changes

- **CLR value-type Box** (`Box` ExecuteNeo arm, the `else` branch at line 1659):
  allocate a boxed CLR object from the frame's flat bytes. WITH a registered
  `ValueTypeBinder` -> memcpy the flat bytes into a real CLR struct instance via
  the binder. WITHOUT a binder -> keep the value in `mStack` as a boxed CLR
  struct (`Activator`/`FormatterServices`-style raw box) so method calls on the
  boxed value work in-place. Replace the `throw` at line 1662.
- **CLR value-type Unbox / Unbox_Any** (the `else` branch at line 1902): copy a
  boxed CLR struct back into the frame's flat bytes (WITH binder -> binder
  assign-from-object; WITHOUT binder -> raw memcpy of the unboxed struct back
  into the frame byte region). Replace the `throw` at line 1905.
- **CLR value-type Initobj** (line 1604): zero-init the frame byte region for a
  CLR value-type local. WITH binder -> binder-zero; WITHOUT binder -> `InitBlock`
  of `GetPrimitiveSize`. Replace the `throw`.
- **`constrained.` callvirt on a value-type `this`**: complete the compile-time
  specialization that Step 10/11 left for the value-type case. When the
  `constrained` token resolves to a value type and the target method is a
  non-boxing override (e.g. an inherited `object.ToString` / `GetHashCode`), box
  the `this` value once into a temp and dispatch; when the target is a
  value-type-declared method, call directly on the in-frame `this` address.
- **Edge-case hardening** of the IL value-type Box/Unbox paths Step 5 already
  implemented (areas verified present: IL enum, IL primitive, IL VT with refs
  via `CopyFrameToIL`/`CopyILToFrame`, reference-type-as-box no-op). No
  behavioral change expected; covered by new tests.
- New `TestCases/NeoStep13Test.cs` covering: IL VT box/unbox round-trip, CLR
  VT box/unbox (binder + no-binder), `constrained.` `T.ToString()` on a struct,
  `foreach`-style no-Binder path.

### Explicit In-this-pass vs Deferred list

**IN this pass (Step 13):**
1. Area 1 -- IL value-type Box/Unbox: verify + edge-case-hardening only (Step 5
   did the mechanics; `CopyFrameToIL`/`CopyILToFrame` already exist).
2. Area 2 -- CLR value-type Box/Unbox/Initobj WITH and WITHOUT `ValueTypeBinder`.
3. Area 3 -- `constrained.` callvirt compile-time specialization on a
   value-type `this`.

**DEFERRED to Step 13b (separate change, with rationale):**
4. Area 4 -- Binding codegen overhaul (`Unsafe.Unbox<T>` + direct-call mode,
   eliminating the Legacy `WriteBackInstance` / `StackObject*` writeback).
5. Area 5 -- CLRMethod unified Neo param layout (reuse
   `AllocateNeoCallParamSlot` for CLR structs, read via non-generic `ReadNeo*`
   helpers by slot width, REMOVE the caller-temp-slot fallback at
   `Optimizer.Neo.cs:646-655`).

**Rationale for the split.** Areas 4-5 modify the CLR binding generator
(`Runtime/CLRBinding/`) and the Neo call ABI shared by *every* CLR method
invocation -- a single mistake breaks all CLR bindings in the smoke, not just
value types. The Neo path today has no `ValueTypeBinder` consumption at all
(`ILIntepreter.Neo.cs` has zero `ValueTypeBinder` references), the generated
Neo redirect code path (`RedirectionNeo` / `*_Neo`) is distinct from the Legacy
`WriteBackInstance` path, and `CLRMethod.Invoke(byte*)` already throws NIE for
CLR struct params (`CLRMethod.cs:364`). Landing areas 4-5 is a self-contained
ABI change whose review must stand alone. Bundling it with the box/unbox
mechanics (which are frame-local and testable in isolation) would make the diff
unreviewable and risk a red smoke that is hard to bisect. The deferral is
accepted-known: areas 1-3 are independently shippable and useful, and the K2
bug (Step 12b VT-by-value param) stays covered by its existing fallback until
13b.

## Capabilities

### New Capabilities
- `neo-boxing`: Box/Unbox of value types (IL and CLR, with and without
  `ValueTypeBinder`) and CLR value-type `Initobj` in the Neo register VM, plus
  `constrained.` callvirt specialization on a value-type `this`.

### Modified Capabilities
- (none -- `neo-value-types` is NOT modified; boxing is a distinct capability.
  The CLR-call-ABI changes that would touch `neo-value-types` are deferred to
  Step 13b and will modify that capability then.)

## Impact

- **Code**: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Box
  arm ~1608-1664, Unbox arm ~1856-1907, Initobj ~1590-1607); JIT `constrained.`
  specialization in `JITCompiler.cs` (~1709-1773 hasConstrained handling) and
  the `Constrained` opcode translation (~2168). Possibly a small
  `Optimizer.Neo.cs` touch for `constrained.`-box-temp slotting. All new code
  behind `#if ENABLE_NEO_MODE`. `ValueTypeBinder` may gain Neo-mode helpers
  (frame-byte <-> struct memcpy) in `Runtime/Enviorment/ValueTypeBinder.cs`.
- **Regression surface**: areas 1-3 are frame-local and dispatch-local; the
  full NeoStep smoke (was 41/41 after Step 12b) is the gate. CLR binding tests
  are the canary but are NOT touched by areas 1-3 (areas 4-5 are deferred).
- **Tests**: new `TestCases/NeoStep13Test.cs`, ASCII, value-round-trip /
  DivideByZero-assertion patterns (no throw-asserting tests -- harness cannot
  construct `new Exception(...)` yet, CLR newobj is Step 9).
- **Deferred work**: Step 13b will cover areas 4-5 and resolve the Step 12b K2
  bug (VT-by-value CLR param). K2 relationship documented in design.md.
- **Non-goals**: `byref`/`ref`/`out` = Step 17; IL value-type `newobj` = Step 18;
  async state machines (`TaskAwaiter` by-value) = Step 20 (relies on area 5).
