# Design -- neo-nre-residual-sites (Wave-2, after C16 brought smoke to 89)

## Phase 1 -- RE-AUDIT: NRE sub-clusters at the 89-baseline (re-confirmed)

Full Neo smoke baseline (this child, HEAD fc2baa26 + prior wave-2 children, fresh
Debug_Neo CLI + Debug TestCases builds):
`Ran 922 tests, 89 failed`. Extracted the 35 distinct NRE tests and sub-clustered
by the top ILIntepreter frame (deduped by test; rethrows excluded):

| Sub-cluster (Frame:Line)                                  | Size | Notes |
|---|---|---|
| `ResolveNeoCallvirtCLRTarget:1432` ("Neo callvirt this is null") | **4** | C4 residual (callvirt this==null). DelegateTest43, ReflectionTest04/19, StaticTest.UnitTest_StaticTest03 |
| `ExecuteNeo:5007` (Ldsfeld IL-static VT arm)              | **4** | get_One, UnitTest_10025, UnitTest_StaticTest05, UnitTest_10023 |
| `ResolveNeoCallvirtILTarget:1366`                         | 3 | DelegateTest16/17, SimpleTest.EqualsTest |
| `ExecuteNeo:4852` (Stsfeld IL-static VT arm)              | 2 | ExpTest_10.UnitTest_10022, StructTests.StructTest4 |
| `ExecuteNeo:4672`                                         | 2 | Test01.UnitTest_Generics, UnitTest_Generics2 |
| `ExecuteNeo:4720`                                         | 2 | StructTests.StructTest3, StructTest14 |
| `NeoMarshalByrefFieldToSlot:519`                          | 2 | InheritanceTest21/22 |
| `ExecuteNeo:5182`                                         | 2 | GenericStaticMethodTest19, RefOutTest.UnitTest_RefOutNull2 |
| singletons (4379/4430/4648/5332/5417/5419/5534/5860/6142/6157/6332, InvokeNeoClrMethod:1252/1256, Execute:2260) | ~14 | mostly VT/struct/refout edges |

Line numbers in C16's design MATCH exactly (HEAD unchanged since C16).

## Largest sub-cluster: tied at 4. PICKED ExecuteNeo:5007/4852 (same root)

Two sub-clusters tied at size 4: the C4 callvirt-this-null residual (1432) and the
Ldsfeld IL-static-VT arm (5007). The task hint flagged the C4 one as "C4-adjacent,
extending C4's fix may be the route". I audited BOTH and picked the **5007/4852**
cluster because:

- The 5007 (Ldsfeld) and 4852 (Stsfeld) sub-clusters are the **same root cause**
  (proven below): both read `ILTypeStaticInstance.Primitives`, which is null. Fixing
  the root spans 2 sub-clusters (6 tests) at once -- higher coverage than the C4
  residual's 4.
- The C4 residual is a genuine `this==null` at the call site (a different shape from
  C4's GetType/VTable-slot fix); it is reported, not fixed here.

## Root cause (JIT dump + instrumented diagnostic, stash-toggle confirmed)

Reproducer: `TestCases.Vector3.get_One` (does `ldsfld Vector3::one` where `one` is a
`static Vector3` field -- a VALUE TYPE that has a STATIC FIELD OF ITS OWN TYPE).

`Vector3` is an IL struct: `public struct Vector3 { float x,y,z; static Vector3 one = new Vector3(1,1,1); static string typetag; }`.

Observed failure: NRE inside `Vector3..cctor` at the `stsfld one` instruction, top
frame `ExecuteNeo:4852` (the Stsfld IL-static value-type arm):
```
Unsafe.CopyBlockUnaligned(ref sinst.Primitives[off.PrimitiveOffset], ...)
```
`sinst.Primitives` is null.

Instrumented diagnostic (temporary throw at the Stsfeld site) printed:
```
DIAG stsfld-ilvt: Primitives NULL decl=TestCases.Vector3 ft=TestCases.Vector3
  pSize=-1 mCnt=0 sftLen=2 offPrim=0 vtPrimSz=12
```

Decoding:
- `pSize=-1` == the EXACT sentinel `totalStaticPrimitiveSize = -1` (ILType.cs field
  init, line 92). So `InitializeFields` never finalized the static totals for Vector3.
- `mCnt=0` and `sftLen=2` (2 static fields: `one`, `typetag`) confirm the arithmetic
  below.

### WHY totalStaticPrimitiveSize stayed -1 (the bug)

`ILType.InitializeFields()` processes ALL fields (instance + static) in ONE loop.
For each static value-type field whose type is an ILType `sit`, the Neo arm
accumulates:
```csharp
staticPrimitiveOffset += sit.TotalPrimitiveSize;
staticReferenceOffset += sit.TotalReferenceCount;
```
For Vector3's `static Vector3 one`, `sit == this` (Vector3 references ITSELF).
`TotalPrimitiveSize`/`TotalReferenceCount` have a re-entrancy guard
(ILType.cs:434) that returns the cached field if `fieldMapping != null` -- and
fieldMapping is the FIRST thing the loop assigns, so during the loop the guard
returns the **unfinalized instance totals**, which are still the `-1` sentinels
(they are only assigned at ILType.cs:3084-3085, AFTER the loop).

So for `one`:
- `staticPrimitiveOffset += -1`  -> -1
- `staticReferenceOffset += -1`  -> -1
For `typetag` (string, reference): `staticReferenceOffset++` -> 0.
Finalize (ILType.cs:3101-3102): `totalStaticPrimitiveSize = -1`, `totalStaticReferenceCnt = 0`. MATCHES the diagnostic exactly.

Consequence: the Neo `ILTypeStaticInstance` ctor sizes the `byte[] Primitives`
(`fields`) from `type.StaticTotalPrimitiveSize`:
```csharp
int pSize = type.StaticTotalPrimitiveSize;   // -1
if (pSize > 0) fields = new byte[pSize];     // FALSE -> fields stays null
```
So `Primitives == null` -> the .cctor's Stsfld and any Ldsfeld of an IL-VT static
field NRE on `ref sinst.Primitives[idx]`.

This is **Neo-specific**: the static-VT-offset Neo arm and the `byte[] Primitives`
flat-byte storage are both Neo-only. Legacy's `ILTypeStaticInstance` uses
`StackObject[]` (not Primitives) and never hits this. (Legacy's static offsets are
similarly corrupted by the self-reference, but Legacy does not size any array from
them, so it is silently latent.)

### The fix (ILType.cs InitializeFields, Neo-gated; Legacy byte-identical)

Move the Neo static-field OFFSET computation OUT of the field loop into a POST-LOOP
pass that runs AFTER the instance totals (`totalPrimitiveSize`/`totalReferenceCnt`)
are finalized. Now when the pass reads `svt.TotalPrimitiveSize`/`TotalReferenceCount`
for the self-referential field (`svt == this`), it reads the correct finalized size
(12 / 0 for Vector3), not the -1 sentinel.

Concretely (ILType.cs, both edits `#if ENABLE_NEO_MODE`):
1. Remove the inline Neo static-offset block from the loop (keep the Legacy/shared
   staticFieldTypes/mapping/references setup; keep a comment explaining the deferral).
2. Add a post-loop Neo pass (`for s in [0, idxStatic)`) that computes
   `staticFieldOffsets[s]` and accumulates `staticPrimitiveOffset`/`staticReferenceOffset`
   (primitive / IL-value-type / reference branches), mirroring the instance-field pass.
   Includes the same null-staticFieldType `TypeLoadException` guard the inline block had.

No engine/JIT/optimizer/object-model change. Legacy compiles out both edits.

## Scope of code change
- `ILRuntime/CLR/TypeSystem/ILType.cs` `InitializeFields()`: 1 file, +45/-43 lines,
  entirely `#if ENABLE_NEO_MODE`.

## Verify (truth = full-smoke number)
- **Full smoke: 89 -> 86 (delta -3, 0 regressions).**
  Command: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
- **Flipped green (3):** `TestCases.StructTests.StructTest4`, `TestCases.TestValueTypeBinding.UnitTest_10025`,
  `TestCases.Vector3.get_One`. (StructTest4 was in the ExecuteNeo:4852 / Stsfeld
  sub-cluster -- confirms the root spans both the Ldsfeld + Stsfeld sub-clusters.)
- **Newly failing (regressions): 0.**
- **Stash-toggle (airtight):** revert ILType.cs to HEAD -> rebuild -> `Vector3.get_One`
  FAILS (1/1, NRE in .cctor Stsfeld) -> restore -> rebuild -> PASS (1/0). Proves causation.
- **NeoStep: 388/0** (no regression).
- **Legacy-neutral:** plain Debug build 0 errors (both edits compile out); Legacy
  NeoStep = 388 ran / 18 failed == the documented pre-existing Legacy NeoStep set.

## Tests that PROGRESS but do not flip (secondary roots, reported not fixed)
The fix clears the primary Primitives-null NRE for ALL IL-VT-static-field sites. Two
ExecuteNeo:5007-cluster tests progress to a SECONDARY failure (different root):
- `UnitTest_StaticTest05`: now reaches the test's OWN `throw new Exception()`
  (Throw opcode, ExecuteNeo:6372) -- an assertion in the test fires. Different root
  (a ref/`Vector3&` byref pass to `UnitTest_StaticTest05Sub` -- see JIT dump).
- `ExpTest_10.UnitTest_10023`: now NRE at `ExecuteNeo:4720` (a different VT/struct
  site in `Sub10023`). Different root (the 4720 sub-cluster).
- `ExpTest_10.UnitTest_10022` (4852 cluster): still fails (progressed to a secondary).

## Remaining NRE sub-clusters (reported, NOT fixed by this child)
- `ResolveNeoCallvirtCLRTarget:1432` x4 -- C4 residual "Neo callvirt this is null"
  (genuine this==null at the call site; different shape from C4's GetType/VTable fix).
- `ResolveNeoCallvirtILTarget:1366` x3 -- DelegateTest16/17, SimpleTest.EqualsTest.
- `NeoMarshalByrefFieldToSlot:519` x2 -- InheritanceTest21/22 (child-25 byref site).
- `ExecuteNeo:4672` x2, `4720` x2, `5182` x2, `5417/5419` x2, the ~14 singletons --
  mostly VT/struct/refout/byref edges; per-test triage needed.
