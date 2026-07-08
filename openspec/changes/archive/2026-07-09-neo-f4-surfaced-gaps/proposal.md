# Proposal -- neo-f4-surfaced-gaps

> Two small, reproducible Neo gaps surfaced by the F-4 / read-IL-fields-off-a-
> caught-exception work. This change ships BOTH as TRUE-COMPLETION.

## Why

The F-4 follow-on (`neo-f4-reflection-on-neo`, archived 2026-07-08) sequenced
two NEW gaps it surfaced while closing the 4 IL-exception field-read paths
(its deferred-items row reads: "2 NEW gaps sequenced (op_Equality null-operand
+ newobj string-arg)"). Both are real, reproducible FAIL-on-HEAD bugs that this
change closes.

## Gap A -- op_Equality null-operand autogen-binding AoRE

`Type t = e.GetType(); if (t == null) ...` lowers to `Type.op_Equality(t, null)`.
The autogen Neo binding `System_Type_Binding.op_Equality_1_Neo` reads BOTH
operands via `ILIntepreter.ReadNeoReference`. A NULL operand is the Neo null
sentinel (mStack index `-1`); the unguarded `mStack[idx]` indexed `mStack[-1]`
and threw `ArgumentOutOfRangeException`. (Same defect class for
`System_String_Binding.op_Equality_*_Neo` and every autogen Neo binding that
reads a reference operand.)

**Fix:** apply the established Neo null-sentinel convention (`(idx >= 0) ?
mStack[idx] : null`, already used at `CLRMethod.Invoke`'s Neo arg read and
`Ldelem_Ref`'s null encoding) inside `ReadNeoReference`, so a null operand
yields `null` before indexing. One-line, Neo-only helper; Legacy-neutral.

Dump-gate verdict: CONFIRMED FAIL-on-HEAD (`Return:-96`, the probe's caught
AoRE). After fix: `Return:9`.

## Gap B -- derived IL type's flat instance misses inherited fields

The F-4 finding HYPOTHESIZED "the plain `new MyEx("msg")` ctor stores `this`
into the string field instead of the arg." That hypothesis was STALE on HEAD
`bc9f1020` -- empirically `new MyEx("ctor-msg")` sets `Msg == "ctor-msg"`
correctly (the parametrized-Run follow-on `de0ef01c` incidentally closed the
plain-ctor path). The REAL surfaced bug is one level deeper:

A Neo flat `ILTypeInstance` lays ALL fields (own + inherited-from-IL-base) into
ONE `Primitives[]` + ONE `ManagedObjects[]`. But `ILType.InitializeFields`
computed `TotalPrimitiveSize` / `TotalReferenceCount` from the type's OWN
`definition.Fields` only -- so a DERIVED IL type (e.g. `DerivedEx : MyEx`,
where `MyEx` declares the `Msg` field) got `TotalReferenceCount == 0`, and
`Instantiate` allocated an instance with a NULL `ManagedObjects` list. The
ctor's `Msg = msg` stfld then NREd on `ins.ManagedObjects[0]`. This is the
FIRST IL-type-with-IL-base case the Neo suite exercised (the Step-14 exception
probes all used `MyEx : System.Exception`, a CLR base).

Legacy is FLAT too (`StackObject[TotalFieldCount]`, and `TotalFieldCount`
accumulates the IL base at `ILType.cs:373`) -- Neo diverged. The fix mirrors
Legacy: in `InitializeFields`, prepend the IL base type's already-flat
`TotalPrimitiveSize` / `TotalReferenceCount` to the offset accumulators so the
derived type's own field region starts AFTER the inherited region. Because
`GetFieldOffset(idx)` already recurses into the base for `idx <
FieldStartIndex` (returning the base's 0-based offsets) and the JIT stamps a
field's offset via the field's DECLARING type, the inherited region occupies
`[0..baseTotal)` in both the base's local view and the derived type's flat
view -- consistent by construction. Neo-only (`#if ENABLE_NEO_MODE` arm);
Legacy byte-identical.

Dump-gate verdict: CONFIRMED FAIL-on-HEAD (`new DerivedEx("derived-msg") :
base(msg)` throws NRE at `ILIntepreter.Neo.cs` `Stfld_Ref` /
`GetNeoILInstance` -> `ins.ManagedObjects[0]` on a null list; the derived
instance's `ManagedObjects` was null because `DerivedEx.TotalReferenceCount`
was 0). After fix: `Return:9` (both the plain ctor AND the `:base(msg)` chain
set `Msg` correctly).

## What changes

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` --
  `ReadNeoReference`: null-sentinel guard (Gap A).
- `ILRuntime/CLR/TypeSystem/ILType.cs` -- `InitializeFields`: prepend IL-base
  field totals to the Neo flat-layout accumulators (Gap B).
- `TestCases/NeoStep14Test.cs` -- two adversarial dump-gate probes
  (`NeoStep14_ILEx_GapA_TypeOpEqualityNull`, `NeoStep14_ILEx_GapB_NewobjStringArg`),
  both FAIL-on-HEAD -> PASS-after.

## Out of scope

Deeper IL-base hierarchies beyond the 2-level `Derived : Base(: CLR)` shape are
covered BY CONSTRUCTION (the accumulation recurses through `TotalReferenceCount`),
but are not exercised by a dedicated probe here (the Step-14 surface only has
the 2-level shape). A multi-level IL-inheritance regression probe is left to a
later step's smoke. The `String.op_Equality` sibling binding shares the Gap A
defect class and is closed by the same `ReadNeoReference` fix (no separate
change).
