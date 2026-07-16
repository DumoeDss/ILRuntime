# Full Neo Smoke Grounding -- 2026-07-16 FRESH re-cluster (5 -> 4 -> 3; StructTest6 FIXED)

## STATUS: FRESH grounding (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch).
Full smoke is **3 failed** after this child (was 4 on entry; was 5 at ground-05).
`Ran 948 tests, 3 failded, 20 ignored, 7 todos` (exit 127 = known graceful
Dict-NRE crash; summary emitted). This child FRESH-ran the smoke, re-clustered
the CURRENT 4, deep-diagnosed the most tractable singleton (StructTest6),
PINNED the root cause (byref out-STRUCT ref-region write-back for a 0-prim+N-ref
IL struct local passed as a BYREF reference-typed CLR param), shipped a verified-
correct fix, and confirmed the drop by re-running the full smoke. NeoStep
**414/0** (no regression). Legacy-neutral (plain Debug build = 0 errors; all
changes are `#if ENABLE_NEO_MODE`-gated / file-gated).

Build this child: CLI `Debug_Neo --no-incremental -p:UseSharedCompilation=false`
(0 errors), TestCases `Debug -p:UseSharedCompilation=false` (0 errors).

## Fresh ground (no filter, Debug_Neo, same TestCases.dll + HotfixAOT.patch)
- PRE-this-child (ground-05 baseline):  `Ran 948 tests, 4 failded, 20 ignored, 7 todos`.
- POST-fix (this child):                  `Ran 948 tests, 3 failded, 20 ignored, 7 todos`.
- Delta: **-1 (StructTest6 flipped green)**. The 3 survivors are a STRICT
  SUBSET of the entry 4 (no new failures, no regressions).
- NeoStep: 414/0 (no regression throughout).

## The CURRENT 4 (entry) -> pinned root + verdict (1 FIXED this child, 3 remain)

### StructTest6 -- FIXED this child (byref out-STRUCT ref-region write-back)
- **Symptom:** Structs.cs:262. `Dictionary<string,StructTest>.TryGetValue(strId,
  out cube)` where cube = StructTest {object objAsset; string type} = 0 prim +
  2 ref (a PURE-REFERENCE struct). The out param was NOT written back -- cube
  kept its pre-call value (cube.type stayed "123", expected "111").
- **ROOT PINNED (JIT-dump + code-dive confirmed):** `Dictionary<string,
  StructTest>` is resolved to `Dictionary<string, ILTypeInstance>` (an IL struct
  used as a CLR generic arg is boxed to ILTypeInstance -- BY DESIGN, Legacy
  too). So `TryGetValue(string, out ILTypeInstance)` is a BYREF REFERENCE-typed
  param whose CALLER-SIDE source is the StructTest LOCAL (a struct frame with 0
  prim + 2 ref slots). The Area-4c frame-native CopyBlock forward/write-back
  only touches PrimitiveSize[i] = 4 bytes (the single ref-slot mStack index):
  - Forward CopyNeoCallArguments (objIdx==-1): CopyBlock'd 4 garbage bytes
    (read-beyond the struct's EMPTY prim region) into the callee 4-byte ref slot.
  - CLRMethod.Invoke reflection write-back: wrote the result ILTypeInstance's
    mStack index (4 bytes) to the callee slot.
  - CopyNeoCallThisBack (objIdx==-1): CopyBlock'd those 4 bytes to `frameBase +
    offset` = the struct's prim BASE -- but the struct has 0 prim, so the ref
    REGION (objAsset/type at frameRefBase + cubeRefOff) was NEVER touched.
  This is the BYREF sibling of the FIXED neo-il-struct-box-call-boundary child
  (which handled the BY-VALUE struct->ref param case; the byref `out`/`ref` case
  was its deferred sibling).
- **THE FIX (the byref box/unbox mechanism, Neo-gated -> Legacy-neutral):**
  Extends the existing PrimitiveBoxIlType box/unbox pattern to BYREF struct-to-
  ref params. 3 engine sites:
  (1) JITCompiler.cs NeoCallParamMap: +`PrimitiveByRefBoxIlType` (ILType[])/
      `PrimitiveByRefBoxSrcRefOff` (ushort[]) -- the byref sibling of
      PrimitiveBoxIlType/PrimitiveBoxSrcRefOff.
  (2) Optimizer.Neo.cs param-map build: detect the case -- a byref param
      (dstByRef, !dstIsVtThisSlot) whose element type is a REFERENCE, whose
      source register (srcRegs[p]) liveAliasMap-traces back to an IL value-type
      struct LOCAL (curVtTypes[structReg] is ILType IL-VT). Record the struct's
      ILType + the struct local's caller-frame ref offset (localInfos[structReg]
      .RefOffset). ALSO seed the curVtTypes tracker for Initobj (it was an IL-VT
      PRODUCER that TypeSpecializeNeoOpcodes already seeds at :1090 but the
      tracker's generic dest-clear was wiping -- a struct local zero-init'd via
      `initobj rLocal, StructType` now keeps its type so the detection fires).
  (3) ILIntepreter.Neo.cs: CopyNeoCallArguments FORWARD -- for the byref box
      case, BOX the struct (Instantiate(false) + CopyFrameToIL) into a fresh
      mStack slot and write its index (mirrors the by-value box arm; correct
      input for `ref`, harmless for `out`). CopyNeoCallThisBack WRITE-BACK --
      gained a `frameRefBase` param (5 call sites updated); for the byref box
      case, UNBOX the result ILTypeInstance back to the caller's struct frame
      (prim + ref) via CopyILToFrame instead of the 4-byte CopyBlock. A null
      result leaves the caller's struct as-is.
- **Soundness:** the byref box case is DISJOINT from the by-value box case
  (dstByRef vs !dstByRef) and from dstIsVtThisSlot (4b, already flat-bytes-
  correct). The Initobj tracker-seed mirrors Newobj/Unbox seeding (and the
  TypeSpecialize seed at :1090); it can only make a previously-MISSED struct-
  to-ref box fire (correct behavior), never a false positive on a non-struct.
  CopyILToFrame/CopyFrameToIL are the existing Unbox/Box helpers (used by the
  in-frame-VT + by-value-box paths) -- no new marshalling code.
- **Verify:** StructTest6 PASS (targeted 1/0). Full smoke 4 -> 3. NeoStep
  414/0 (no regression). Legacy-neutral (plain Debug 0 errors). Before/after
  established by the FRESH grounding run (StructTest6 failing) vs the post-fix
  full smoke (StructTest6 gone).

### StructTest12 -- REMAINS (Activator.CreateInstance<T> generic-param mis-resolution)
- **Structs.cs:396 (via StructTest12Sub).** `T ins = new T() { i = 10 }` where
  T : struct, ITestStruct (T = MyStruct2). Dump: `ins.i = 1` (expected 10).
- **ROOT PINNED via JIT dump (confirmed still present):** Roslyn lowers `new T()`
  (generic struct T) to `call Activator.CreateInstance<T>()`, and the JIT
  resolves the generic param T to **ILTypeInstance** for the Activator call
  (JIT dump: `call.redirect r1, Activator::ILTypeInstance CreateInstance[
  ILTypeInstance]()`) while `initobj r1, MyStruct2` in the SAME method correctly
  resolves T to MyStruct2 -- inconsistent generic-param resolution. The
  Activator result (a heap ILTypeInstance whose mStack index = 1) overwrites the
  `ins` local; `ins.i` reads ins's first 4 bytes = the mStack index -> "1". The
  subsequent `constrained MyStruct2; callvirt set_i(10)` then boxes the
  ILTypeInstance-index bytes as MyStruct2 and mutates the BOX (discarded), so
  the write does not land either.
- **DEEP (JIT generic-param resolution under a generic-method context):**
  AppDomain.GetMethod marks the Activator call's T ContainsGenericParameter ->
  the fallback resolves T to ILTypeInstance. child-22's CreateInstanceNeo redirect
  does NOT fix this (the generic arg is mis-resolved to ILTypeInstance BEFORE the
  redirect body runs; CreateInstanceNeo reads method.GenericArguments[0] =
  ILTypeInstance). Even with correct resolution, the redirect returns a heap
  ILTypeInstance, not a struct-by-value -- two coupled bugs. Candidate future
  child = JIT generic-param propagation into Activator.CreateInstance<T> inside a
  generic method + a struct-returning CreateInstance path.

### UnitTest_10051 -- REMAINS (constrained-callvirt property read on a nested struct field)
- **TestValueTypeBinding.cs:619 (via TestRunTimeStack.Run).** `list[0].V2.x.
  RawValue != 999`. V2 is a property get returning Fixed64Vector2 (CLR struct)
  by value; `.x` reads a Fixed64 (CLR struct) field; `.RawValue` is a property
  read on Fixed64. A constrained-callvirt property read on a nested struct field
  (the `.x.RawValue` chain), over an INLINED struct-returning property getter.
- **DEEP:** the JIT inlines V2's getter as `ldfld.ref` (reads the CLR-struct
  backing field's boxed mStack index -- the F-10 path) then a raw `ldfld` of
  `.x` on that boxed struct. The raw Ldfld on a boxed CLR struct reached via an
  inlined struct-property-getter (predecessor = ldfld.ref, NOT ldflda/ldelema)
  does not carry child-24/29's byref markers -> falls to the flat-bytes path
  (wrong for a boxed-struct mStack index). Plus the sort lambda's op_LessThan/
  op_GreaterThan on Fixed64 and the final RawValue constrained-callvirt.
  Candidate future child = `neo-struct-field-property-read` (the inlined-
  struct-property + nested-field + constrained-callvirt-property chain).

### MyTest.Test -- REMAINS (boxed-CLR-struct enumerator interface dispatch, Step-19)
- **Test01.cs:618.** `InvalidCastException: String -> IEnumerator<KVP<int,int>>`
  at autogen `get_Current_0_Neo`. A Dictionary enumerator `this` is mis-
  marshalled (a String ends up in the `this` slot on a later loop iteration).
  The enumerator CLR struct is boxed once before the loop; MoveNext + get_Current
  work for 2 iterations (print "1 1", "0 0"), then the 3rd get_Current's `this`
  (r1 = the `e` local) reads as a String.
- **DEEP (Step-19 delegate/enumerator family):** boxed-CLR-struct enumerator
  interface dispatch with this-register aliasing/clobbering across loop
  iterations. Candidate future child = Step-19 boxed-enumerator this-marshalling.

## The fix this child (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` (+NeoCallParamMap
  fields PrimitiveByRefBoxIlType/PrimitiveByRefBoxSrcRefOff).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (Initobj tracker
  seed + byref-box detection in the param-map build + map-field assignment).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (CopyNeoCall
  Arguments forward box + CopyNeoCallThisBack frameRefBase param + write-back
  unbox + 5 call-site updates).
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-04.md` (THIS file).

## Remaining 3 (reported honestly -- each a distinct deep singleton, re-confirmed)
- **StructTest12** -- JIT generic-param resolution for `Activator.CreateInstance<T>`
  in a generic method (T resolves to ILTypeInstance instead of the enclosing T)
  + the redirect returns a heap ILTypeInstance not a struct (two coupled bugs).
- **UnitTest_10051** -- constrained-callvirt property read on a nested struct
  field (`.x.RawValue` chain over an inlined struct-returning property getter).
- **MyTest.Test** -- boxed-CLR-struct enumerator interface dispatch (Step-19,
  this-register aliasing/clobbering across loop iterations).

## Lesson
The 12-for-12 "re-audit a DEEP verdict" discipline held AGAIN and yielded a fix:
StructTest6 was filed DEEP in ground-05 ("the byref encoding must carry the
struct's ref base ... the write-back target shape is fundamentally wrong" --
ARCHITECTURAL). But the verdict conflated TWO cases: the byref encoding itself
is FINE for primitive/CLR-struct/ref params (the Area-4c frame-native CopyBlock
works); the gap was NARROWER -- it fires ONLY when a BYREF reference-typed CLR
param's caller-side source is an IL value-type struct LOCAL (0 prim + N ref),
i.e. the byref sibling of the already-FIXED by-value box-call-boundary child.
Reusing that child's BOX/UNBOX pattern (Instantiate + CopyFrameToIL forward /
CopyILToFrame write-back) + the liveAliasMap trace (byref temp -> struct local)
made it a focused, low-risk fix instead of an architectural byref-encoding
rework. The decisive signal: the param type is `out ILTypeInstance` (reference)
but the caller's local is a STRUCT -- a type-shape mismatch at the call boundary
that the existing by-value box machinery already solved for the non-byref form.
