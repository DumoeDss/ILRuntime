# Design -- neo-nre-cluster-subclusters (Wave-2 C16)

## Re-audit: the NRE bucket at 95 (the worklist)
Full Neo smoke baseline (this child, HEAD fc2baa26 + prior wave-2 children):
`Ran 922 tests, 95 failed`. Extracted the 44 NRE failures and sub-clustered by the
top Neo.cs stack frame:

| Sub-cluster (Neo.cs frame) | Size | Notes |
|---|---|---|
| `InvokeNeoClrMethod:1252` -> `*_Enumerator_Binding.MoveNext_1_Neo` | **10** | LARGEST -- fixed (6 flip green; 4 progress to secondary roots) |
| `ExecuteNeo:5007` | 4 | ExpTest_10.UnitTest_10023, StaticTest05, UnitTest_10025, Vector3.get_One |
| `ResolveNeoCallvirtILTarget:1366` | 3 | DelegateTest16/17, SimpleTest.EqualsTest |
| `ExecuteNeo:5417/5419` | 2 | ArrayBindTest, ReflectionTest14 |
| `NeoMarshalByrefFieldToSlot:519` | 2 | InheritanceTest21/22 |
| `InvokeNeoClrMethod:1252` (non-enumerator) | 2 | ReflectionTest06 (FieldInfo.SetValue), NullableTest |
| `ExecuteNeo` singleton lines (4648/4672/4720/4852/5182/5332/5534/5860/6142/6157/6332/4379) | ~14 | mostly VT/struct/refout edges |
| `ResolveNeoCallvirtCLRTarget:1432` ("callvirt this is null") | 4 | C4 residual (different shape) |

(The 4 "Neo callvirt this is null" tests are the C4 residual, a different sub-cluster.)

## Largest sub-cluster: the enumerator stale-stub round-trip (10 tests) -- FIXED
### Root cause (JIT-dump + stash-toggle confirmed)
The autogen Neo stubs for `Dictionary<,>.Enumerator` / `List<>.Enumerator` /
`KeyValuePair<,>` are STALE `default(...)` TODOs that predate the Neo value-type
support. The post-Step-13b generator never regenerated them (regen is GUI-bound per
child-28). Three stubs per container were each broken:

1. `GetEnumerator_*_Neo` (container): calls `instance.GetEnumerator()` then DISCARDS the
   returned value-type enumerator (`// TODO: CLR value type return in reflection fallback:
   Step 13`) -> the caller's enumerator local is never written.
2. `MoveNext_1_Neo`: `instance_of_this_method = default(Enumerator)` (`// TODO: ValueType
   instance in Neo`) -> `.MoveNext()` runs on a zeroed enumerator whose `_dictionary` field
   is null -> **NRE inside `System.Collections.Generic.Dictionary<,>.Enumerator.MoveNext()`**
   (the top frame is the BCL MoveNext, called from our stub at line 135).
3. `get_Current_0_Neo`: same `default(...)` this-read + the VT return discarded.

Stack evidence (GCTest.TestDicEnumerator): `Dictionary<,>.Enumerator.MoveNext() ->
..._Enumerator_Binding.MoveNext_1_Neo:135 -> InvokeNeoClrMethod:1252 -> ExecuteNeo:3386`.

### The fix (hand-port to the post-Step-13b template, mirrors child-28)
For each stale stub (all under `#if ENABLE_NEO_MODE`; Legacy `#else` byte-identical):
- **Enumerator `MoveNext_1_Neo` / `get_Current_0_Neo`**: read the VT `this` at frame offset 0
  (`ReadNeoValueType(typeof(Enum), __frameBase, ref __curPrim, __sz)` with `__off_0` captured
  before), call the method, `WriteNeoValueType(instance, __frameBase + __off_0, __sz)`
  write-back (MoveNext mutates `index`; Current is read-only but write-back is idempotent),
  and for get_Current write the VT return (`WriteNeoValueType(result, __retDst, retSz)`).
- **Container `GetEnumerator_*_Neo`**: the ref `this` is already read via `ReadNeoReference`;
  add the VT return write (`WriteNeoValueType(result, __retDst, retSz)`).
- **`KeyValuePair<,>` `get_Key_*_Neo` / `get_Value_*_Neo`**: read the VT `this` + write-back.
  Their type-specific return writes (ref -> mStack[retRefBase]; primitive -> `*(int*)`;
  VT -> WriteNeoValueType) were ALREADY correctly emitted by the generator -- only the `this`
  read was missing.

The uniform return-write for the VT-return stubs uses `result.GetType()` for the size
(`ILIntepreter.GetNeoValueTypeManagedSize`), avoiding hardcoding the KeyValuePair type.

### Why this is sound for the enumerator (a ref-field value type)
The enumerator is a value-type struct WITH reference fields (the `dictionary`
back-pointer). The Neo flat-byte VT path is sound here because the round-trip is entirely
CLR-native:
- `GetEnumerator`'s return path (`InvokeNeoClrMethod` VT-return, `ILIntepreter.Neo.cs:1297+`)
  writes the boxed struct via `WriteNeoValueType` (`Unsafe.WriteUnaligned`), embedding REAL
  GC pointers in the flat bytes.
- `ReadNeoValueType` (`Unsafe.ReadUnaligned`) reconstitutes the struct with those real
  pointers, so `MoveNext` sees a valid `_dictionary`.
- The write-back preserves the mutated `index` + the real `_dictionary` pointer.

The reflection-fallback's `NeoClrStructHasReferenceField` NIE guard (`CLRMethod.cs:391`) is
NOT in play -- these are registered Neo redirects, so they take the stub path
(`InvokeNeoClrMethod:1252`), never the reflection fallback (`:1256`). The guard remains
correct for genuinely IL-produced ref-field structs (whose flat bytes would carry mStack
indices, not GC pointers).

### GC-soundness caveat (latent, out of scope, documented)
The Neo frame is `byte*`; a GC pointer embedded in flat bytes is NOT a tracked GC root.
If a compacting GC fires mid-foreach and RELOCATES the Dictionary, the enumerator's
`_dictionary` pointer (in flat bytes) becomes stale -> corruption/NRE. For these tests the
Dictionary stays alive via the caller's local (an mStack ref, GC-tracked) and no GC is
triggered mid-foreach, so the tests pass. This is the SAME known Neo-model limitation as
the Step-13b ref-field-struct stance (child-8/27/29). A principled fix (store ref-field VTs
as boxed objects in mStack, like Legacy) is an object-model change, out of scope.

## Scope of code change (17 binding files, ~32 stubs; all Neo-gated)
Enumerator bindings (MoveNext + get_Current): Dictionary_2_{String_Int32, String_ILTypeInst,
Int32_Int32, Int32_String_Arra, UInt32_ILTypeInst, Int32_ILTypeInsta_t2(ValueCollection)}_*
+ List_1_{String, ILTypeInstance}_*. Container GetEnumerator: the matching Dictionary_2_* /
List_1_* main bindings. KeyValuePair_2_* get_Key/get_Value (7 files). No engine / JIT /
optimizer / object-model change.

## 4 enumerator tests that PROGRESS but do not flip (secondary roots, reported)
- **TestStructDictionary**: now NRE directly in `ExecuteNeo` (IL-struct `TestStruct`
  marshalling into a CLR `Dictionary<int,TestStruct>`; the enumerator/ValueCollection part
  works). Different root (IL-struct-in-CLR-collection).
- **JsonTest9**: now reaches its own `throw new Exception()` -- `item.Value.GetType().Name`
  returns "ILTypeInstance" not "B" (IL-type-reflection GetType; sibling of child-18
  ILRuntimeType). Different root.
- **MyTest.Test**: `InvalidCastException: String -> IEnumerator<KeyValuePair<Int32,Int32>>`
  (a non-generic / interface GetEnumerator cast path). Different root.
- **TestForEach**: now reaches its DESIGNED `throw new NotSupportedException("error")`
  (ParseOne). The test carries `[ILRuntimeTest(ExpectException=NotSupportedException)]`;
  the smoke counts it failed, so the harness appears not to honor ExpectException here
  (separate harness question, not an enumerator bug). TestForEachTry (same loop, wrapped in
  try/catch) PASSES.

## Verify (truth = full-smoke number)
- **Full smoke: 95 -> 89 (delta -6, 0 regressions).** The 6 flipped: GCTest.TestDicEnumerator,
  GenericMethodTest.GenericTest, InheritanceTest.InheritanceTest24, Test05.TestForEachTry,
  Test05.TestReturn, TestValueTypeBinding.UnitTest_10034.
- **Stash-toggle (airtight):** revert the 2 Dictionary_2_String_Int32 canary files to HEAD ->
  rebuild -> TestDicEnumerator FAILS (NRE via the default-stub MoveNext) -> restore -> PASS (1/0).
- **NeoStep: 388/0** (no regression).
- **Legacy-neutral:** plain Debug + useRegister=true + NeoStep = 388 ran / 18 failed == the
  documented pre-existing Legacy NeoStep set (Neo-specific probes that fail under ExecuteR);
  all edits are `#if ENABLE_NEO_MODE` -> Legacy compiles none of them.

## Durable findings (for future children)
- **The stale-autogen-stub defect class (child-28 lineage) is the gift that keeps giving.**
  The committed Neo stubs predate the Step-13b generator; ANY CLR value-type method whose
  Neo stub is `default(...) + // TODO: ValueType instance in Neo` or `// TODO: CLR value type
  return` is a stale stub that silently returns default/discards. The fix is always the same:
  hand-port to the post-Step-13b template (`ReadNeoValueType`/`WriteNeoValueType`). 33 files /
  66 `// TODO: ValueType instance in Neo` occurrences remain (most are async-builder /
  Nullable / primitive-box stubs, NOT yet reached by the smoke). A future child could sweep
  them, but only the enumerator/KeyValuePair subset is currently load-bearing for the smoke.
- **The VT-instance-method `this` is laid out as FLAT BYTES at callee offset 0** (the
  call-lowering + CopyNeoCallArguments deref the ldloca byref into the `this` slot, sized by
  AllocateNeoCallParam's IsValueType branch -- `CLRMethod.cs:368-398`). So a stub reads it
  with `ReadNeoValueType(typeof(T), frameBase, ref curPrim=0, sz)` and writes the (possibly
  mutated) struct back to `frameBase + 0`. This is the autogen equivalent of the reflection-
  fallback's `vtThisSlotOff`/`vtThisSz` machinery (`CLRMethod.cs:396-398/588`).
- **A ref-field value-type round-trips correctly through flat bytes WHENEVER the producer is
  CLR-native** (a CLR method return written via `WriteNeoValueType` embeds real GC pointers).
  The `NeoClrStructHasReferenceField` guard is only correct for IL-produced structs (mStack
  indices in ref slots). Do NOT relax that guard globally; hand-port the specific CLR-native
  stub instead.
- **Build-server cache (child-25/29 gotcha, reaffirmed):** touching `ILRuntimeTestBase`
  (where the bindings live) requires `dotnet build-server shutdown` +
  `-p:UseSharedCompilation=false` on BOTH the CLI and TestCases builds, else a stale DLL
  masks the change.
