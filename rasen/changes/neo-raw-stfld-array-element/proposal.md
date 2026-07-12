## Why

Raw `Stfld` whose owner is a **CLR-struct array element** (`clrStructArray[i].field = x`) throws a
tagged `NotImplementedException` ("Neo raw Stfld: array-element field write is deferred (stfld on a
CLR array element; follow-up)") at `ILIntepreter.Neo.cs:4077` (CLR value-type declaring branch — the
reachable ~4 full-smoke hits) and `:4103` (CLR ref-type declaring branch — symmetric, unreachable via
`ldelema` but fail-loud). This is a child-4 (`neo-raw-stfld-ldfld`) deferred shape: child-4 handled
the three CLR owner cases for raw Stfld/Ldfld (CLR ref-type Area 4d, CLR VT Ldfld inline-bytes, CLR
VT Stfld frame-byref) and explicitly deferred the array-element + IL-instance-CLR-base + unrecognized
shapes as tagged NIEs. child-9 (`neo-il-instance-clr-base-field`) closed the IL-instance-CLR-base
shape; this change closes the remaining array-element shape.

## What Changes

- **Execute raw `Stfld` when the owner is a CLR-struct array element** (replace the two tagged NIEs
  at `ILIntepreter.Neo.cs:4077` and `:4103`). Decode the owner byref as `(arrIdx, elementIdx)` — the
  encoding `ldelema` on a CLR-struct array already produces (`ILIntepreter.Neo.cs:5729-5749`) and the
  `stind_*`/`ldind_*` consumers already consume (`cArr.SetValue(v, off)` / `cArr.GetValue(off)`,
  `:5176`/`:5244`). Mirror Legacy's `ObjectTypes.ArrayReference` writeback (`ILIntepreter.Register.cs:
  3154-3158`): box the element via `Array.GetValue(elementIdx)`, reflection-write the field
  (`FieldInfo.SetValue`), write the mutated struct back via `Array.SetValue(boxedElem, elementIdx)`.
  This is the same box/mutate/unbox pattern as child-4's CLR-VT-OWNER Stfld case, with
  `Array.GetValue`/`SetValue` in place of `ReadNeoValueType`/`WriteNeoValueType`.
- **No JIT / optimizer / object-model change.** No new opcode, no new helper (inline). The owner
  byref shape, the `value` marshalling (already boxed by field category earlier in the handler,
  `:4044-4057`), and the resolved `FieldInfo f` are all already in hand.
- **Ldfld array-element is DEFERRED** (not symmetric — see design D2 / planner-findings): a raw
  `Ldfld` value-type owner can be EITHER flat managed bytes (`ldloc` by-value of a local) OR a byref
  (`ldelema`), and the Neo frame is untyped (no per-slot tag), so a naive `mStack[objIdx] is Array`
  discriminator risks false-positives. A robust Ldfld fix needs a JIT marker (child-15
  `ldflda`-marker style) and is deeper; out of scope here. The Stfld owner is unambiguously a byref
  (you cannot write a field without the address), so Stfld has no such ambiguity.
- **Neo-gated** (`#if ENABLE_NEO_MODE`); Legacy-neutral by construction. Add a `NeoStep` probe
  (`clrStructArray[i].intField = x` + host-side read-back) that FAULTs on HEAD (the tagged NIE).

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-value-types`: ADDED a requirement pinning how raw `Stfld` executes when the owner is a
  CLR-struct array element (the `(arrIdx, elementIdx)` byref + `Array.GetValue`/`SetValue` box/
  mutate/unbox). Sibling of child-4 (`neo-raw-stfld-ldfld`) and child-9
  (`neo-il-instance-clr-base-field`), which share this spec.

## Impact

- **Code:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — the raw `Stfld` handler's
  two array-element branches (`:4076-4077` value-type declaring, `:4102-4103` ref-type declaring):
  replace each tagged NIE with the `Array.GetValue` / `f.SetValue` / `Array.SetValue` box/mutate/
  unbox. ~10-15 lines total, inline.
- **Test infra (host):** add a small host CLR struct `NeoArrElemIntProbe { public int A; public int B; }`
  (`ILRuntimeTestBase/TestFramework/TestVector3.cs`) + one host helper
  `TestCLRBinding.NeoArrElemFieldSum(NeoArrElemIntProbe[] arr, int i)` (`TestClass3.cs`). Int fields
  deliberately, to avoid the pre-existing unrelated `addi`-on-float / `conv.i4`-float-bit-reinterpret
  Neo bugs (child-16 candidate).
- **Probe:** new `TestCases/NeoStepRawStfldArrElemTest.cs` (TC1 single-element round-trip, TC2
  multi-index element-index-decode proof).
- **No JIT / optimizer / object-model / CLR-binding change.** No new opcode.
- **Risk:** low — the pattern is byte-identical to Legacy's array-element writeback and reuses
  child-4's established box/mutate/unbox; the owner byref shape is already produced and consumed
  elsewhere in the Neo interpreter.
