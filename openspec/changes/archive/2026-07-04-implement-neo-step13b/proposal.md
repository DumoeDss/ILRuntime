## Why

Neo Step 13 left two CLR-call-ABI concerns stubbed, and they are now the single
biggest gap in CLR interop: passing a **CLR value type by value** as a method
parameter or return, and the `CLRMethod.Invoke(byte*)` reflection fallback
that reads params. Today the call-lowering has a temporary **caller-temp-slot
fallback** (`Optimizer.Neo.cs:1172-1181`) that copies a CLR struct param using
the *caller's* slot shape instead of a real callee layout, and
`CLRMethod.Invoke(byte*)` throws `NotImplementedException` for any CLR struct
parameter (`CLRMethod.cs:364`). The result is the pre-existing **K2/K2-FAM**
bugs: a CLR struct by-value param reads a primitive field VALUE as an mStack
index (`ArgumentOutOfRangeException`), blocking CLR-enum/struct interop
end-to-end. This is the highest-value follow-up in the roadmap and unblocks
async state machines (`TaskAwaiter.GetResult()`, Step 20).

This step is also flagged the **highest-ABI-regression-risk step in the
roadmap**: it touches the param layout read by EVERY CLR method call in Neo.
Scope discipline (an explicit In/Deferred list below) keeps the diff reviewable.

## What Changes

- **IN -- Core (Area 5): unified CLRMethod param layout + remove the
  caller-temp-slot fallback.**
  - Route CLR-struct-by-value parameters (and the value-type instance `this`)
    through the existing `AllocateNeoCallParamSlot` callee layout
    (`Optimizer.Neo.cs:1319`), which already sizes a CLR struct via
    `GetPrimitiveSize` (`IsValueType` branch, line 1357-1361). REMOVE the
    `srcInfo`-shape fallback at `Optimizer.Neo.cs:1172-1181`.
  - `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:332`) reads CLR struct params via
    `ReadNeo*`-by-slot-width (replace the `Step 13` NIE at line 364) and writes
    a CLR struct return value into the dest frame slot (replace the NIE at
    line 193 of `ILIntepreter.Neo.cs`).
  - The autogen binding reader (`BindingGeneratorExtensions.AppendArgumentCodeNeo`,
    line 107) reads a CLR struct param by its flat-bytes width via a new
    `ReadNeoValueType<T>` helper (and `GetReturnValueCodeNeo` line 460 writes
    a CLR struct return). The no-`ValueTypeBinder` path supports pure-primitive
    CLR structs; structs with ref fields without a binder throw a clear
    Step-13b-tagged NIE.
  - Closes K2 (Step 8 VT-by-value param copy) and K2-FAM (Move-path
    scalar->boxed-ref CLR-VT-local) as natural consequences.

- **DEFERRED -- Area 4 (binding-codegen `Unsafe.Unbox<T>` direct-call +
  eliminate `WriteBackInstance`).** The Neo wrapper already does NOT emit
  `WriteBackInstance` (it reads `this` via `ReadNeoReference` for reference
  types; the value-type `this` is a `// TODO` at `MethodBindingGenerator.cs:261`).
  The remaining Area 4 work (a value-type `this` direct-call lowering that
  boxes/unboxes in place via `Unsafe.Unbox<T>`, plus the return-side boxed-ref
  writeback) is a distinct rewrite of the generated instance-method prologue
  that does NOT fall out of the param-layout work and would, if bundled,
  make the diff unreviewable. The value-type `this` TODO stays. DEFERRED to a
  dedicated follow-up.

- **DEFERRED -- CLR-method `ref`/`out` params (closes K2-FAM's byref crossing).**
  A byref param is now an 8-byte Ref Slot (Step 17). Materializing it into a
  CLR `ref T` argument on the callee side requires a typed-reference bridge
  back into the Neo frame (or a copy-in / copy-out protocol) that is separate
  infrastructure from the by-value param layout. Land a value-type by-value
  param this pass; DEFER the byref CLR crossing (the IL-method byref path from
  Step 17 stays the green target). Accepted-known.

- **DEFERRED -- CLR-object `stind`/`ldind` via field hash.** The Step 17
  stind/ldind/stobj/ldobj arms dispatch only on frame-native (`objectIndex ==
  -1`) and heap-IL (`ILTypeInstance`) targets; a CLR-object target throws a
  Step-17-tagged NIE. Implementing it needs `Ldflda`/`Ldflda`-of-CLR-field to
  stamp a field hash into the Ref Slot's offset half and the consumer arms to
  dispatch to `CLRType.GetFieldValue(hash, target)` / `SetFieldValue(...)`.
  This is independent plumbing (the field hash is unrelated to the param
  layout) and does not fall out of the Area 5 work. DEFERRED. Accepted-known.

## Capabilities

### New Capabilities

(none -- this change extends an existing capability)

### Modified Capabilities

- `neo-boxing`: Step 13 left the CLR-call-ABI aspects explicitly deferred to
  13b ("this capability does not change the call ABI"). 13b lands the
  unified CLR param layout + CLR-struct by-value param/return read, so the
  box/unbox capability now reaches the call boundary. ADDS requirements for
  CLR value-type by-value parameter reading and return-value writing, and a
  MODIFIED note that the call-ABI deferral is now partially closed (by-value
  in; byref / CLR-object stind-ldind still deferred).

## Impact

- **Code (runtime, all `#if ENABLE_NEO_MODE`, additive/replacing NIE throws):**
  - `Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- remove the caller-temp-
    slot fallback (`:1172-1181`); CLR struct params now use
    `AllocateNeoCallParamSlot` like every other param type.
  - `CLR/Method/CLRMethod.cs` -- `Invoke(byte*)` (`:332`/`:364`) reads CLR
    struct params by width; the constructor reflection fallback path is
    covered.
  - `Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the CLR-method
    return path (`:191-194`) writes a CLR struct return into the dest slot;
    add `ReadNeoValueType<T>` (and the matching return-write helper) near the
    existing `ReadNeo*` family (`:26-127`).
  - `Runtime/CLRBinding/BindingGeneratorExtensions.cs` --
    `AppendArgumentCodeNeo` (`:107`) and `GetReturnValueCodeNeo` (`:440`) emit
    the new read/write for CLR struct params/return.
- **Tests:** `TestCases/NeoStep13bTest.cs` (NEW, ASCII). Canary: the FULL
  NeoStep smoke (was 81/81) -- every CLR call uses this ABI, so the existing
  CLR-binding tests are the regression gate; new cases add CLR-struct
  by-value param + return round-trips on host-assembly structs (Finding H:
  TestCases-declared structs are ILTypes, so use
  `TestFramework.TestVector3NoBinding` / `TestVector3`).
- **Non-impact (Legacy neutrality):** Legacy CLR binding (`ExecuteR`, the
  `StackObject*` paths, the Legacy `AppendArgumentCode`/`GetRetrieveValueCode`/
  `GetReturnValueCode`, `WriteBackInstance`, the Legacy `CLRMethod.Invoke` with
  `StackObject*`) is the REFERENCE and is NOT modified. The Neo codegen path
  (`*Neo` methods, `AppendArgumentCodeNeo`, `GetReturnValueCodeNeo`,
  `RegisterCLRMethodRedirectionNeo`) is a SEPARATE set of generators invoked
  only under `#if ENABLE_NEO_MODE`; the shared generator dispatches to the Neo
  variant only when building the Neo path. A bug in 13b can therefore only
  affect Neo CLR calls, never Legacy.

## Explicit In / Deferred list

| Item | Status | Rationale |
|------|--------|-----------|
| Area 5 -- unified CLRMethod param layout (reuse `AllocateNeoCallParamSlot`); read CLR struct params/return via `ReadNeo*`-by-width | **IN** | Core ABI fix; closes K2/K2-FAM; `AllocateNeoCallParamSlot` already handles CLR structs -- the change is removing the fallback + filling NIEs. Highest value. |
| Remove caller-temp-slot fallback (`Optimizer.Neo.cs:1172-1181`) | **IN** | Required for Area 5; the layout helper already produces the correct callee slot. |
| `CLRMethod.Invoke(byte*)` CLR-struct param read + struct return write | **IN** | Fills the `Step 13` NIE; reuses the same `ReadNeo*`-by-width model. |
| Autogen `AppendArgumentCodeNeo` / `GetReturnValueCodeNeo` for CLR struct | **IN** | Same read/write model as the reflection fallback; keeps generated + reflection paths consistent. |
| Area 4 -- `Unsafe.Unbox<T>` direct-call + eliminate `WriteBackInstance` | **DEFERRED** | Distinct rewrite of the instance-method prologue; the Neo wrapper does not emit `WriteBackInstance` today (no-op). Bundling risks the ABI. Value-type `this` TODO stays. |
| CLR-method `ref`/`out` (byref Ref Slot -> CLR `ref T`) | **DEFERRED** | Separate typed-reference bridge into the frame; IL-method byref (Step 17) stays green. |
| CLR-object `stind`/`ldind` via field hash | **DEFERRED** | Needs `Ldflda` field-hash stamping + consumer dispatch to `CLRType.Get/SetFieldValue`; independent plumbing. |
