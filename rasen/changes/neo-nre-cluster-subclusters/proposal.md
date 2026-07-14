# neo-nre-cluster-subclusters (Wave-2 C16)

## Why
The full Neo smoke stood at **95 failed** (after C1/C4/C2/C12/C10/C13/C8/C5/C14/C7/
struct-newobj/struct-arg/C3). Re-auditing the residual `NullReferenceException` bucket
(44 NRE tests at 95: 40 generic "Object reference not set" + 4 "Neo callvirt this is null")
sub-clustered by Neo.cs stack frame revealed that the **single largest sub-cluster (10
tests)** all NRE inside the SAME autogen binding stub family: the `*_Enumerator_Binding.
MoveNext_1_Neo` stale stubs.

## Root cause (pinned, stash-toggle-confirmed on 1 canary, full-smoke-confirmed on the set)
The autogen Neo stubs for CLR value-type enumerator round-trips are STALE `default(...)`
TODOs that predate the Neo value-type support (the post-Step-13b generator never regenerated
them; regen is GUI-bound, same defect class as child-28 `neo-float-vtreturn-opaddition`).
Three stubs per container were each broken:

- `GetEnumerator_*_Neo` -- calls `instance.GetEnumerator()` then DISCARDS the returned
  value-type enumerator (`// TODO: CLR value type return in reflection fallback: Step 13`),
  so the caller's enumerator local is never written.
- `MoveNext_1_Neo` -- `instance_of_this_method = default(Enumerator)` (`// TODO: ValueType
  instance in Neo`); never reads the `this`, so `.MoveNext()` runs on a zeroed enumerator
  whose `_dictionary` field is null -> NRE inside `Dictionary<,>.Enumerator.MoveNext()`.
- `get_Current_0_Neo` -- same `default(...)` this-read; `.Current` returns garbage / the VT
  return is also discarded.

The cleanest fix mirrors child-28: hand-port each stale stub to the post-Step-13b template
(`ReadNeoValueType` the VT `this` at frame offset 0 / write the VT return via
`WriteNeoValueType`), exactly what the reflection-fallback (`CLRMethod.Invoke`) already does
for a VT `this` (`CLRMethod.cs:398` read / `:588` write-back). The enumerator is a value-type
struct WITH reference fields (the `dictionary` back-pointer), but the round-trip is sound
because `GetEnumerator`'s return path writes the boxed struct via `WriteNeoValueType`
carrying REAL GC pointers, which `ReadNeoValueType` (`Unsafe.ReadUnaligned`) reconstitutes
faithfully. The reflection-fallback's `NeoClrStructHasReferenceField` NIE guard
(`CLRMethod.cs:391`) is NOT in play here -- these methods are registered Neo redirects so
they take the stub path (`ILIntepreter.Neo.cs:1252`), never the reflection fallback.

## The 10-test sub-cluster (largest in the NRE bucket)
GCTest.TestDicEnumerator, JsonTest.JsonTest9, GenericMethodTest.GenericTest, MyTest.Test,
InheritanceTest.InheritanceTest24, Test05.TestForEach, Test05.TestForEachTry,
Test05.TestReturn, Test05.TestStructDictionary, TestValueTypeBinding.UnitTest_10034.

6 of these flip green from the enumerator-stub port alone. The other 4 PROGRESS past the
enumerator NRE to a SECONDARY root (different sub-cluster, reported not fixed):
- TestStructDictionary -> NRE directly in ExecuteNeo (IL-struct marshalling into a CLR
  Dictionary<int,ILStruct>; the enumerator part now works).
- JsonTest9 -> the test's own `throw new Exception()` (item.Value.GetType().Name returns
  "ILTypeInstance" not "B" -- an IL-type-reflection root, sibling of child-18).
- MyTest.Test -> InvalidCastException on `IEnumerator<KeyValuePair<,>>` (a non-generic /
  interface GetEnumerator cast path -- different root).
- TestForEach -> now reaches its DESIGNED `throw new NotSupportedException("error")`
  (ParseOne); the test carries `[ExpectException=NotSupportedException]`, so whether it
  counts as pass depends on the harness honoring that attribute (a separate harness question,
  not an enumerator bug).

## What changed
Hand-ported 32 stale autogen Neo stubs across 17 binding files (all under
`#if ENABLE_NEO_MODE` -- Legacy `#else` stubs byte-identical => Legacy-neutral by
construction): the enumerator `MoveNext_1_Neo`/`get_Current_0_Neo` (read VT this at offset 0
+ write-back + VT return), the container `GetEnumerator_*_Neo` (write the VT return), and the
`KeyValuePair<,>` `get_Key_*_Neo`/`get_Value_*_Neo` (read VT this; their type-specific return
writes were already emitted by the generator). Files in
`ILRuntimeTestBase/AutoGenerate/System_Collections_Generic_Dictionary_2_*`,
`..._List_1_*`, `..._KeyValuePair_2_*`.

## Out of scope (reported, not fixed)
The other NRE sub-clusters at 95 (see design.md): ExecuteNeo:5007 (4), ExecuteNeo:5417/5419
(2), ResolveNeoCallvirtILTarget:1366 (3), NeoMarshalByrefFieldToSlot:519 (2), ~14 singleton
ExecuteNeo lines, and the 4-test "callvirt this is null" residual. Plus the 4 enumerator
tests' secondary roots above.
