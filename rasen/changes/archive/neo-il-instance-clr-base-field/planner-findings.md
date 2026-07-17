# Planner Findings — neo-il-instance-clr-base-field

## The defect (confirmed)
- Raw `Ldfld` NIE: `ILIntepreter.Neo.cs:3750` ("Neo raw Ldfld: IL-instance owner with a CLR-base
  field is deferred").
- Raw `Stfld` NIE: `ILIntepreter.Neo.cs:3913` ("Neo raw Stfld: IL-instance owner with a CLR-base
  field is deferred").
- Both sit in the CLR **reference-type owner** `else` of child-4's raw handlers. The owner slot's
  first int is the `mStack` index of the owner, which for this shape is an `ILTypeInstance` (or its
  `CrossBindingAdaptorType` wrapper).

## How an IL instance stores a CLR-base field (THE load-bearing finding)
A CLR-base field is NOT in the IL instance's `Primitives`/`ManagedObjects` IL-field layout. It lives
on `ILTypeInstance.clrInstance` (`.CLRInstance`): the wrapped CLR object created at construction
(`ILTypeInstance.cs:366-368`: when `type.FirstCLRBaseType is CrossBindingAdaptor`, `clrInstance =
adaptor.CreateCLRInstance(appdomain, this)`). For `TestCls : ClassInheritanceTest` the wrapped object
is a `ClassInheritanceTestAdaptor.Adaptor` instance, which IS-A `ClassInheritanceTest`, so the CLR
base's instance fields (`testVal`, `TestVal2`) are real CLR fields on it.

Legacy routes by INDEX gate, not type discrimination:
- `ILTypeInstance.AssignFromStack(fieldIdx)` (`:953`): `fieldIdx < fields.Length` -> IL field; else
  -> `clrType = FirstCLRBaseType.BaseCLRType; clrType.SetFieldValue(fieldIdx, ref clrInstance, value)`.
- Read-indexer (`:445-453`): out-of-range index -> `clrType.GetFieldValue(index, clrInstance)`.
- The raw encoding `(typeHash<<32)|fieldHash` is IDENTICAL in Legacy and Neo
  (`AppDomain.GetFieldOffset:2242-2258`: returns the field's declaring CLRType + `GetFieldIndex`);
  the large field hash lands in the `else` (CLR-inherited) branch deterministically.

## The Neo handler data path (the fix)
Unwrap the owner to `ILTypeInstance` (`target as ILTypeInstance ?? ((CrossBindingAdaptorType)target).ILInstance`),
then read/write the field on `il.CLRInstance` via the EXISTING helpers
`NeoReadClrObjectField(AppDomain, il.CLRInstance, fieldHash)` / `NeoWriteClrObjectField(AppDomain,
il.CLRInstance, fieldHash, value)`. These resolve `ct = appdomain.GetType(target.GetType())` and
call `ct.GetFieldValue/SetFieldValue(fieldHash, ...)` which walks the base chain, so the field
declared on the CLR base resolves from the Adaptor's CLRType. Equivalent explicit form:
`ct.GetFieldValue(fieldHash, clrTarget)` / `ct.SetFieldValue(fieldHash, ref clrTarget, value)` using
the already-decoded declaring `CLRType` (byte-identical to Legacy `:966`). No writeback needed (CLR
base is a class; `clrInstance` not replaced). No JIT/optimizer/object-model change -- child-4 already
lowered the raw opcodes and stamped `OperandLong`.

## Capability
`neo-value-types` (same as child-4 `neo-raw-stfld-ldfld`). Delta = ADDED a sibling requirement for
the IL-instance-with-CLR-base-field owner shape.

## Probe
IL type `NeoStepIlClrBaseHolder : ClassInheritanceTest` (CLR host base in ILRuntimeTestBase,
registered via `ClassInheritanceTestAdaptor`) + static `NeoStepIlClrBase_TC1_RoundTrip` (newobj,
WriteBase(4242), read back, assert == 4242 else `1/0`). Faults on the current tagged NIE; asserts
the value so a wrong-value bug also fails. Embeds "NeoStep" so the smoke filter picks it up.

## Gotchas
- The owner slot may hold the `CrossBindingAdaptorType` wrapper OR the `ILTypeInstance` directly;
  handle both (D3).
- `clrInstance == this` only when there is NO CLR-base adaptor -- impossible for a CLR-base-FIELD
  scenario (the field's declaring CLRType must be a base), so `.CLRInstance` is always the Adaptor
  here. If `NeoReadClrObjectField` ever hits the "non-CLR-resolvable target" NIE, it means
  `clrInstance` was unexpectedly the IL instance -- treat as a distinct bug.
- newobj of an IL-type-with-CLR-base runs the CLR base ctor on `clrInstance` (Step-18 surface,
  exercised by existing `InheritanceTest.TestCls`); if it throws a DIFFERENT NIE, that is a separate
  gap, not this change's defect.
- Out of scope: the `target is Array` raw Stfld/Ldfld shape (separate ~2-hit child); CLR VT with ref
  fields (child-6/child-8).

## Expected result
NeoStep 326 -> 327/328, 0 failures; full smoke loses ~5 tagged-NIE occurrences for this shape.
Legacy-neutral (all edits under `#if ENABLE_NEO_MODE`).
