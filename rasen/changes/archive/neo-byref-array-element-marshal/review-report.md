# Review: neo-byref-array-element-marshal (child 25)

Reviewer: independent (not the implementer). Branch `features/object-model-overhaul`.
Scope: the `target is Array arr` branch in `NeoMarshalByrefFieldToSlot`
(`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:508-594`), the 4 host
helpers in `TestClass3.cs::TestCLRBinding` (~:226-258), and the 3 TCs in
`TestCases/NeoStepByrefArrayElementTest.cs`.

## Verdict: APPROVE

Correct, minimal, Neo-gated, Legacy-neutral, well-probed, and empirically verified
end-to-end (forward materialize + reflection mutate + write-back persist). Only
Minor/Trivial doc+comment nits remain -- none functional.

## Empirical evidence (re-run by reviewer)

| check | result |
|---|---|
| `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental -p:UseSharedCompilation=false` | **0 errors** |
| `dotnet build TestCases -c Debug -p:UseSharedCompilation=false` | **0 errors** |
| NeoStep smoke (`true NeoStep`) | **371/0** (matches implementer claim) |
| 3 new probes (`true ByrefArrayElement`) | **3/0 PASS** |
| canary `CLRBindingTest01` (the triage trigger) | **1/0 PASS** (was the R2 NIE on HEAD) |
| stash-toggle: `git stash push -- ILIntepreter.Neo.cs`, rebuild, run probes | **3/3 FAULT** with exact NIE `Step 13 Area 4c: a CLR-array-element byref param is not handled` (TC1 fails at the forward-deref call line `NeoStepByrefArrayElementTest.cs:43`) |
| stash pop, rebuild, re-run probes | **3/0 PASS** |

The stash-toggle fault is thrown from the interpreter's byref marshal (the call
REACHED `NeoMarshalByrefFieldToSlot` and the `Array` branch threw) -- i.e. the
host helpers were resolved correctly throughout; the failure is the Area-4c NIE,
not a helper-resolution / MissingMethodException failure.

## Build-server-cache sanity (finding #2 in the task): CONFIRMED CLEAN

The implementer hit a stale Roslyn VBCSCompiler cache for `TestClass3.cs`. I
confirmed the COMMITTED helpers are genuinely in the BUILT DLL (not a stale-cache
false-positive), three independent ways:

1. **Compile-time metadata resolution.** `TestCases` (netstandard2.1) compiled
   clean while explicitly referencing `TestCLRBinding.NeoByrefArrElemIncrementByte`
   / `...IncrementInt` / `...MutateVectorAndReturnIncomingSum` /
   `BuildNeoByrefVectorArray`. The C# compiler resolves method references against
   the referenced assembly's **MethodDef** table (NOT the PDB), so a stale DLL
   whose MethodDef table lacked these would have CS0117'd. It did not. (This is
   exactly the failure mode the implementer's stale-cache run hit; my
   `-p:UseSharedCompilation=false` rebuild cleared it.)
2. **Fresh artifact.** The CLI's net8.0 `ILRuntimeTestBase.dll` was rebuilt by my
   `--no-incremental -p:UseSharedCompilation=false` build (mtime 05:32:09, this
   session).
3. **Runtime resolution.** The 3 probes PASS at runtime -- a missing helper would
   throw `MissingMethodException`/`TypeLoadException`, not pass. The stash-toggle
   reinforces this: reverting only `ILIntepreter.Neo.cs` made the probes fail with
   the interpreter's Area-4c NIE (the call dispatched into the byref marshal),
   proving the host helpers were resolved from live metadata throughout.

Conclusion: the cache bug was real but is NOT present in the current tree; a clean
`-p:UseSharedCompilation=false` build produces a DLL whose MethodDef table contains
all 4 helpers.

## Dimension-by-dimension

### 1. Both-directions correctness -- PASS
- Forward deref (`isWrite == false`, `CopyNeoCallArguments:424`): `arr.GetValue(off)`
  + `WriteNeoValueType(elemVal, slot, sz)` by category. Verified the call site at
  `:411-424` reads `(objIdx, offset)` from the byref source slot and passes
  `isWrite: false`.
- Write-back (`isWrite == true`, `CopyNeoCallThisBack:730`): `ReadNeoValueType(et,
  slot, ...)` + `arr.SetValue(value, off)`. Verified the call site at `:689-730`
  passes `isWrite: true`, gated by `map.PrimitiveByRefWriteBack[i]` (`:704`) -- a
  `ref`/`out` param IS flagged, so the write-back fires.
- The `isWrite` flag selects read vs write correctly; ONE branch covers both
  directions (the two call sites are structurally identical loops over the same
  `NeoCallParamMap`, differing only in `isWrite`).
- VT element box/mutate/unbox: `Array.GetValue` boxes the VT element; the
  reflection callee mutates the box in place; `CLRMethod.Invoke`'s epilogue
  re-flattens into the callee slot; the write-back re-boxes via
  `ReadNeoValueType` + `Array.SetValue`. **TC3 (ret2 = 6777) proves the struct
  round-trip persists.**

### 2. Byref decode correctness -- PASS
- `off` IS the element index. The `ldelema` producer at `:5840-5878` stamps
  `*(int*)(DstOffset+0) = arrIdx; *(int*)(DstOffset+4) = elementIdx;` where
  `elementIdx = *(int*)(frameBase + ip->Operand4)` (the IL array index). The
  forward call site reads `objIdx = arrIdx`, `offset = elementIdx`, and passes
  `offset` as the helper's `off`. `Array.GetValue(off)` / `SetValue(value, off)`
  are rank-1 element accessors taking an element index -- correct for a
  multi-byte struct (e.g. `TestVector3`, 12 bytes), which TC3 exercises.
- **TC2 (arr[0]=101, arr[3]=104, both correct) proves the element-index decode
  addresses the right element on both forward and write-back passes.**

### 3. elemType recovery -- PASS (reliable)
- For any `Array` instance, `GetType().GetElementType()` returns the runtime
  element type (never null for a real array; correct for covariant /
  `Array.CreateInstance` arrays -- it reflects the array's actual runtime type).
  The byref-param path always propagates `elemType` via the call map
  (`PrimitiveByRefElemType`), so the recovery is defensive-only. Write-back
  marshalling by category (primitive/enum/struct -> `ReadNeoValueType`; reference
  -> mStack index) is the symmetric inverse of the forward read. Correct.

### 4. No-overfire / branch exclusivity -- PASS
- Branch order is `ILTypeInstance` -> `Array` -> CLR-object-field fall-through,
  and is exclusive: an IL-instance referent hits the first branch; a CLR-object
  referent is not an `Array` so skips to the fall-through. The `Array` branch
  fires only when `mStack[objIdx]` is genuinely a `System.Array`, which (per the
  `ldelema` producer) happens only for the CLR-array `(arrIdx, elementIdx)`
  encoding. An `ILTypeInstance[]` element byref is produced by `ldelema` as
  `(elemMStackIdx, 0)` (`:5846-5854`) so `target` is the element `ILTypeInstance`
  and is caught by the first branch -- the `Array` branch never sees it. No
  overfire. The `ILTypeInstance` branch (`:456-507`, incl. the F-10 boxed-CLR-
  struct-field sub-case) and the CLR-object-field fall-through (`:595-651`) are
  byte-identical to HEAD -- unchanged.

### 5. Write-back persistence -- PASS (empirically proven)
- **TC1**: byte 20 -> 25, read back via `ldelem.u1` (Step 16 green). The
  `1 / (arr[1] - 25)` DivideByZero guard fires on mismatch; PASS means 25.
- **TC2**: int sum 205 (arr[0]=101, arr[3]=104). The `if (s != 205) { 1/0 }` guard
  fires on mismatch; PASS means both elements mutated at the correct indices.
- **TC3**: VT twice-call ret2 = 6777. The second call's incoming sum (1007+2070+
  3700) observes the first call's write-back. If write-back did not persist, ret2
  would be 777 (re-reading the original 7+70+700). **The twice-call pattern is a
  genuine isolation from the still-broken R1-Shape-B `ldobj`-array-element read**
  (`ILIntepreter.Neo.cs:5704`, triage-batch-2) -- TC3 NEVER reads the struct
  IL-side; it uses the host helper's return value (incoming sum) to observe
  persistence. Clean isolation.

### 6. Regression surface -- PASS
- NeoStep smoke **371/0** (baseline 368 + 3 new probes). `CLRBindingTest01`
  (the triage trigger) now PASSES. The change only ADDS behaviour for the
  previously-NIE `Array` case; the `ILTypeInstance` / CLR-object-field /
  frame-native byref paths are unchanged, so any `ref`/`out` test that passed
  before hit one of those unchanged branches and is unaffected by construction.
- Legacy-neutral: the whole file is `#if ENABLE_NEO_MODE`; Legacy marshals byref
  array params via `ObjectTypes.ArrayReference` in the autogen wrapper epilogue,
  untouched.

### 7. Build-server-cache gotcha -- PASS (see dedicated section above)

## Findings by severity

### Blocker
(none)

### Major
(none)

### Minor
- **[Minor / this-PR / doc-only] tasks.md TC3 description is stale.**
  `tasks.md` section 4 (lines ~38-40) describes TC3 as "ret1 = 0 (incoming sum of
  a zero-init element), ret2 = 6000", but the IMPLEMENTED TC3 (matching
  `design.md` sections 8-9 and the probe file) uses a HOST-built `(7, 70, 700)`
  array with `ret1 = 777`, `ret2 = 6777`. The implemented version is strictly
  better (host-built avoids a dependency on Neo `newarr` zero-init semantics for a
  struct array; non-zero init gives a stronger persistence signal). Code, spec,
  and design agree on 777/6777 -- only `tasks.md` section 4 lags. No functional
  impact; route to implementer to sync `tasks.md`.

### Trivial
- **[Trivial / this-PR / comment-only] Stale line citation `:5797-5798`.** The
  in-code comment at `ILIntepreter.Neo.cs:514-515` (and `proposal.md`, `design.md`
  section 2, and `triage-batch-2.md` R2) cite `:5797-5798` for the `ldelema`
  `(arrIdx, elementIdx)` convention. The ACTUAL stamping is at `:5874-5875`;
  lines `5797-5798` are inside the `ldobj` IL-instance arm
  (`ref byte srcP = ref ins.Primitives[off]; Unsafe.CopyBlock(...)`), not
  `ldelema`. The CODE is correct -- it uses `off` as the element index, matching
  the real producer at `:5874-5875`. Comment/doc drift only.
- **[Trivial / pre-existing pattern, mirrored] Redundant element-category arms.**
  The primitive/enum arm and the struct arm in both the new Array branch and the
  existing CLR-object-field branch (`:600-634`) have byte-identical bodies. Since
  `IsPrimitive` and `IsEnum` are subsets of `IsValueType`, the two arms are
  logically redundant (a single `et.IsValueType` arm would suffice). The
  implementer correctly mirrored the pre-existing convention; no functional
  impact. Not worth changing (consistency with the sibling branch is more
  valuable than the micro-dedup).

### Pre-existing (NOT this PR)
- **R1-Shape-B `ldobj` of a CLR-struct array element** (`ILIntepreter.Neo.cs:5704`)
  -- the READ counterpart of child-19/24 for `arr[i]` IL-side via
  `ldelema; ldobj`. Still throws a tagged NIE. TC3's twice-call pattern correctly
  ISOLATES the byref marshal from this separate gap. Out of scope for this change
  and correctly documented in the proposal (Out of scope) and the probe comment.

## Notes for the implementer (non-blocking)
1. Sync `tasks.md` section 4 TC3 to the implemented 777/6777 host-built version.
2. Optionally refresh the `:5797-5798` citation to `:5874-5875` in the in-code
   comment (and the proposal/design if those docs are still live).
Neither blocks ship.
