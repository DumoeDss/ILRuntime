## Why

Neo Step 15 (isinst/castclass) landed; Step 16 is **array element access**. Today
`ExecuteNeo` has NO `Newarr` / `Ldelem_*` / `Stelem_*` / `Ldelema` arms at all — every
array opcode falls through to the generic `NotImplementedException("...not yet implemented
(Step 6)")` at `ILIntepreter.Neo.cs:2183`. Any IL method that touches an array therefore
fails in Neo mode, and because arrays are pervasive this blocks a large slice of real
test coverage. This change makes the testable array kinds work end-to-end on the Neo
`byte*` frame.

## What Changes

**IN (this pass):**

- `Newarr` — allocate a new array. CLR primitive arrays (CLR allocation via
  `Array.CreateInstance` / `CLRType.CreateArrayInstance`) AND IL arrays
  (`new ILTypeInstance[n]`; for a value-type element array, pre-instantiate each
  element via `ILType.Instantiate` so `stelem`/`ldelem` always target a heap
  `ILTypeInstance`). The new array reference is stored on the mStack with its index
  written into the dest ref slot.
- `Ldelem_*` element loads for the three array kinds:
  - CLR primitive arrays (`int[]`, `float[]`, …) — typed CLR indexer read into the dest
    frame byte slot.
  - IL reference-type arrays (`MyClass[]` / `object[]`, stored as a CLR `Array` whose
    element is an mStack-resident object, or as `ILTypeInstance[]`) — load the element
    object into the dest ref slot.
  - IL value-type arrays (`MyStruct[]` = `ILTypeInstance[]`) — `CopyBlock` the element's
    `Primitives` into the dest frame byte region + copy its ref slots (reuse the
    Step 12/12b primitive+ref copy pattern via `CopyFrameToIL`/`CopyILToFrame`).
- `Stelem_*` element stores — the symmetric write per array kind (primitive typed
  indexer write; ref-type store into the array's element slot; VT `CopyBlock` frame →
  element `Primitives`).
- `Ldlen` — array length into the dest frame slot (cheap, shares the array-ref read).
- Bounds check: rely on the underlying CLR array's own `IndexOutOfRangeException`
  (matches Legacy `ExecuteR`, which has no explicit check). The throw contract is
  enforced in code; a throw-asserting test is NOT green-expressible (harness
  limitation) so bounds are exercised only via the success path.

**DEFERRED (explicitly out of scope, tracked for later):**

- `Ldelema` (address-of-element). Its result is a Ref Slot — but the **full** Ref Slot /
  byref model (`stind`/`ldind`, `ldloca`/`ldflda` unified 8-byte `(objIdx, offset)`,
  `ref`/`out` parameters) is **Step 17**. `ldelema` has no testable consumer without
  Step 17 (its only consumers are `stind`/`ldind`, `fixed`, and `ref` params — all Step
  17). Implementing the `ldelema` plumbing now would ship dead code that cannot be
  exercised. It is therefore deferred to Step 17 where it lands together with its
  consumers.
- Generic-with-token `Code.Ldelem` / `Code.Stelem` and native-int `Code.Ldelem_I` /
  `Code.Ldelem_U8`. The JIT's `Translate` default arm throws `NotImplementedException`
  for CIL codes it does not enumerate (`JITCompiler.cs:2266`); these generic/native
  variants are NOT in the ldelem/stelem case groups and are rare in C# output. They are
  left as existing NIEs and addressed when a real test needs them.
- Multi-dimensional arrays (`int[,]`). Step 16 is single-dim (`*` rank); mdim uses
  `Address`/element access helpers with a different ABI and is not in scope.

## Capabilities

### New Capabilities
- `neo-arrays`: Neo interpreter support for single-dimension array creation (`Newarr`)
  and element access (`Ldelem_*` / `Stelem_*` / `Ldlen`) across the three array
  representations (CLR primitive arrays, IL reference-type arrays, IL value-type
  arrays). Bounds enforcement via the CLR array's own
  `IndexOutOfRangeException`. `Ldelema`, multi-dim, and generic-token variants are
  explicitly out of scope (Step 17 / later).

### Modified Capabilities
<!-- None. This is fresh Neo interpreter behavior; no existing spec requirement changes. -->

## Impact

- **Code:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (new
  `Newarr` / `Ldelem_*` / `Stelem_*` / `Ldlen` case arms behind `ENABLE_NEO_MODE`).
  `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` `LowerNeoOffsets` (lower
  Register1/2/3 of the array opcodes to byte offsets + carry the dest ref offsets,
  mirroring how `Box`/`Isinst` carry `Operand3`/`Operand4` ref offsets).
  Possibly `JITCompiler.cs` to ensure `Newarr`'s dest ref slot is allocated and the
  element-type token is carried (already carried via `GetTypeTokenHashCode`).
- **Reference (do NOT modify):** `ILIntepreter.Register.cs` `ExecuteR` array arms
  (Newarr @4879, Stelem_* @4916–5107, Ldelem_* @5133–5304, Ldelema @5118) — these are
  the spec.
- **Tests:** new `TestCases/NeoStep16Test.cs` (ASCII): CLR primitive array store/read;
  IL reference-type array store/access; IL value-type array store/access via the
  primitive+ref copy; `Newarr`; `Ldlen`. No out-of-bounds throw-asserting test.
- **Regression:** arrays are pervasive — full `NeoStep` smoke (was 65/65) is the gate;
  no existing case may regress.
