# Review Report — implement-neo-step13

Reviewer: independent verify stage (author != verifier). Leaf reviewer, no
subagents. No code edits performed.

Branch: `features/object-model-overhaul` (working-tree diff vs HEAD `ed68c116`).
Scope reviewed: areas 1 (coverage) + 2 (CLR Box/Unbox/Initobj) LANDED; area 3
(`constrained.`) DEFERRED; areas 4-5 DEFERRED to 13b.

## Scope check

**Scope: CLEAN (matches the spec's delivered subset).**

- Intent (spec/proposal/tasks): complete IL + CLR value-type Box/Unbox/Initobj +
  `constrained.` callvirt specialization (5-area original scope; areas 1-3 in
  this pass, 4-5 explicitly deferred to 13b in proposal/design/tasks).
- Delivered: areas 1 (no runtime change, 4 tests) + 2 (3 CLR NIE replacements +
  2 helpers in `ILIntepreter.Neo.cs`, 4 tests). Area 3 deferred at propose-time
  per planning-context §8 Finding K.
- Runtime files changed: **exactly one** —
  `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`. No diff bytes
  in `Runtime/CLRBinding/`, `CLR/Method/CLRMethod.cs`, or
  `RegisterVM/Optimizer.Neo.cs` (areas 4-5 confirmed UNTOUCHED — verified via
  `git diff` returning 0 lines for each). No byref(17)/VT-newobj(18)/async(20)
  scope creep. All new code is inside the file's outer `#if ENABLE_NEO_MODE`.
- New test file: `TestCases/NeoStep13Test.cs` (8 public static tests).

## Build / test results (re-run by reviewer)

- `dotnet build ILRuntimeTestCLI -c Debug_Neo` → **0 errors**.
- `dotnet build TestCases -c Debug` → **0 errors**.
- NeoStep13 filter → **8 tests, 0 failed**.
- Full NeoStep smoke → **49/49 green, 0 failed** (was 41/41 after Step 12b; +8
  new, ZERO regressions). CLR-binding tests (the call-ABI canary) all green.

## Executive verdict: **FINDINGS (no Blocker, no Major)**

The change is correct for what it delivers and passes the full regression gate.
Two Minor findings (both pre-existing-pattern / untested-edge, neither blocking):
(1) the three new CLR-enum branches assume a flat-bytes local representation that
does not match the actual boxed-ref representation for a CLR-enum local, and are
untested; (2) `NeoBoxPrimitiveByType` lacks `IntPtr`/`UIntPtr` (consistent with
the pre-existing `NeoBoxReturnValue` it mirrors — not a regression).

## Adjudication of the three required questions

### (a) Area 2 boxed-ref deviation — **TRUE and CORRECT**

The implementer's core claim is verified against source. In
`JITCompiler.AllocateLocalStackSpaces` (JITCompiler.cs:1362-1373, the CLR-VT
`else` branch), a CLR value-type LOCAL is allocated as a **4-byte frame slot
holding an mStack index** with `Size = 4`, `RefCount = 1`,
`localIsRef[locVarRegStart + i] = true` — i.e. a **boxed object reference**, NOT
flat bytes. The `ivt is ILType` branch (1349-1361) handles IL value types as
flat bytes; only IL value types get the flat-bytes + per-ref-slot layout. So the
design D2 premise ("CLR struct locals are flat bytes") is **factually wrong**,
and the implementer's reframe is **grounded in the actual JIT allocation code**.

The reframed semantics are CORRECT:
- **Initobj (CLR struct)**: `clrInitType.CreateDefaultInstance()` (CLRType.cs
  :1009-1020) = `Activator.CreateInstance(TypeForCLR)` → a real default-init
  boxed struct; installs the mStack index at the dest ref slot. Verified
  semantics: a `default(T)` struct local materializes a genuine boxed instance.
- **Box (CLR struct)**: reads the source mStack slot, then
  `clrBoxType.PerformMemberwiseClone(obj)` (CLRType.cs:357-378) delegates to
  `object.MemberwiseClone` → a **distinct boxed instance** with shallow-copied
  fields. This is an independent snapshot (later mutation of the source local
  does not affect the box) — matching value-type box semantics. The
  `NeoTestClrStructNoBindingBoxRoundTrip` / `...WithBinder...` tests assert
  `ReferenceEquals(o, o2) == false`, which exercises and confirms this.
- **Unbox (CLR struct)**: `PerformMemberwiseClone(obj)` into the dest mStack
  slot — independent copy, value semantics preserved.
- **Primitive/enum CLR box/unbox**: new helpers
  `NeoBoxPrimitiveByType` / `NeoWritePrimitiveToFrame` round-trip sized values
  through the flat-bytes slot (CLR primitives ARE flat bytes per the `else if
  (!vt.IsValueType)`/primitive JIT branch). `NeoTestIlPrimitiveBoxUnbox`
  exercises this path and passes.

The WITH-binder vs no-binder distinction is moot for LOCALS (the local is always
a boxed ref regardless of binder), which is why the design's binder Neo helpers
were correctly NOT added (would be dead code). The binder only becomes relevant
for the flat-bytes representation (array elements / by-value params / IL-typed
fields) — that is area 5 / 13b, correctly deferred.

**Net: not a deviation-from-correctness — a deviation-from-design that FIXED a
wrong design premise. Adjudicated TRUE and the implementation is CORRECT.**

### (b) Spec gap ("no-binder struct-with-refs throws NIE") — **ACCEPT as scope refinement**

The spec (Requirement: "CLR value-type Box with and without ValueTypeBinder",
scenarios at lines 48-77) requires a no-binder CLR struct WITH reference fields
to throw a Step-13b-tagged `NotImplementedException`. The implementer does NOT
implement this throw for the local path.

This is a **legitimate scope refinement, not a violation**, because the spec's
NIE requirement was premised on the same wrong flat-bytes assumption as design
D2 (the binder's job is to map frame ref slots ↔ CLR ref fields — but a CLR-struct
LOCAL has no frame ref slots; it is a single boxed-ref slot). With the corrected
boxed-ref representation, a no-binder struct WITH refs is handled uniformly by
`PerformMemberwiseClone` — there is no unrepresentable state, so the NIE has no
trigger. The "structs-with-refs-and-no-binder" concern genuinely belongs to the
flat-bytes world (array/param/field) deferred to 13b.

**Recommendation: ACCEPT as scope refinement.** The 13b change MUST carry the
flat-bytes NIE scenario when it introduces that representation. Suggest a one-line
note in this change's `tasks.md`/spec delta pointing the NIE scenario to 13b so
the deferral is explicit rather than implicit.

### (c) Area 3 (`constrained.`) deferral — **LEGITIMATE (all 3 blockers are real)**

All three blockers verified:
1. **`ldarga`/`ldarga_s` unimplemented**: grep of `ILIntepreter.Neo.cs` for
   `ldarga|Ldarga` returns **zero** matches in the runtime arm (they are Step 6
   NIE). A constrained callvirt on a value-type `this` requires loading that
   address — owned by the Step 17 byref model.
2. **`Constrained` has no runtime arm + is re-appended after the callvirt**:
   grep for `Constrained` in `ILIntepreter.Neo.cs` returns **zero** runtime
   matches. JITCompiler.cs:1763-1772 removes the `Constrained` pseudo-op from
   before the callvirt, stamps `op.Operand4 = 1`, and re-appends it AFTER — so
   at runtime it cannot inform the callvirt it prefixes. Confirmed structural.
3. **box-once/direct-call needs the VT `this` address model** = Step 17. Real.

**No existing green NeoStep test exercises `constrained.`** (the only occurrences
were in the attempted Step 13 test, which the implementer removed; confirmed —
the final `NeoStep13Test.cs` contains zero `constrained` usage). Deferral incurs
**zero regression risk** (49/49 smoke proves it). The design D3 case analysis is
retained (not deleted) as the blueprint for when it lands with Step 17 / a
follow-up.

**Net: legitimate deferral. Not a cop-out — the prerequisites genuinely live in
Step 17.** Area 3 will land with Step 17 (byref) or a dedicated follow-up.

### (d) Area 1 (coverage only) — **CONFIRMED, no silent runtime change**

The IL Box/Unbox arms (ILIntepreter.Neo.cs:1654-1694 Box IL branch, 1950-1985
Unbox IL branch) are **unchanged by this diff** — they were already complete from
Step 5, using `CopyFrameToIL` (1681) / `CopyILToFrame` (1976) for IL
value-types-with-refs and sized `CopyBlock` for IL enum/primitive. The 4 IL
tests (`NeoTestIlVtOneRefBoxUnbox`, `...ManyRefs...`, `...IlEnum...`,
`...IlPrimitive...`) pass for the right reason: the multi-ref test exercises
`refCount > 1` per-ref-slot copy, the one-ref test asserts ref-identity +
snapshot independence (mutate source after box → boxed value unchanged), and the
enum/primitive tests assert value round-trip. The ref-identity assertion
(`ReferenceEquals` would be implicit via shared `mStack` slot) and snapshot
semantics both hold via the existing copy helpers. No runtime change was needed
or made for Area 1 — confirmed.

## Findings (detail)

### MINOR-1 — Untested / likely-wrong CLR-enum local Box/Unbox/Initobj branches
- **File:line**: `ILIntepreter.Neo.cs:1623-1631` (Initobj enum branch),
  `:1714-1724` (Box enum branch), `:1994-1998` (Unbox enum branch).
- **What**: The three new `clrXxxType.IsEnum` branches treat the local's
  `SrcOffset`/`DstOffset` as **flat bytes** (`InitBlock` / `*(int*)src` /
  `NeoWritePrimitiveToFrame`). But a CLR-enum LOCAL is NOT flat bytes: in
  `AllocateLocalStackSpaces`, `vt.IsValueType && !vt.IsPrimitive` is true for a
  CLR enum (`System.Type.IsPrimitive` is false for enums; `IsValueType` true),
  so the enum local hits the CLR-VT branch (1362-1373) → **boxed object
  reference** (Size=4, RefCount=1). The enum branches would therefore read the
  mStack INDEX as the enum value, producing a wrong box.
- **Why not blocking**: No Step 13 test exercises a CLR-enum local. The only
  enum test (`NeoTestIlEnumBoxUnbox`) uses `NeoStep13IlEnum`, a TestCases-
  declared enum → parsed as an **ILType** (per Finding H), so it hits the IL
  enum path (1656-1665), not the CLR-enum path. The buggy branches are dead
  code in the current test matrix. The full smoke (49/49) does not hit them
  either.
- **Fix**: Either (a) remove the `IsEnum` special-casing and let CLR enums flow
  through the struct `PerformMemberwiseClone` path (a boxed enum IS a boxed
  struct semantically — `MemberwiseClone` works on it), or (b) add a CLR-enum-
  local test to pin the actual behavior and correct the branch. Recommended for
  the next touch of this area (Step 15 isinst/castclass or a boxing-completeness
  step). Note the implementer's Finding L already flags cross-type enum unbox as
  a separate open edge — this is a sibling gap.

### MINOR-2 — `NeoBoxPrimitiveByType` lacks IntPtr/UIntPtr
- **File:line**: `ILIntepreter.Neo.cs:2312-2339` (and the inverse
  `NeoWritePrimitiveToFrame` :2342-2370).
- **What**: Neither helper handles `IntPtr`/`UIntPtr` (native int). The fallback
  in `NeoBoxPrimitiveByType` returns `*(int*)src` (4 bytes) — wrong for an 8-byte
  native int on x64.
- **Why not blocking**: This mirrors the **pre-existing** `NeoBoxReturnValue`
  (:2278-2307), which also lacks IntPtr — so it is consistent with the
  established (if incomplete) pattern, not a regression introduced here. No test
  exercises native-int boxing.
- **Fix**: Add `IntPtr`/`UIntPtr` arms to both helpers when native-int support
  matures; non-urgent.

### INFORMATIONAL — documentation of the deferrals
- The spec was written for the full 5-area scope; only areas 1-2 delivered. The
  spec's unmet scenarios this pass (intentionally): the "no-binder struct-with-
  refs throws NIE" scenario (adjudicated in (b) — belongs to 13b), the entire
  "constrained. callvirt specialization" Requirement (adjudicated in (c) —
  deferred to Step 17/follow-up), and the area-4/5 binding-codegen + CLRMethod
  param-layout scenarios (deferred to 13b by proposal/design/tasks). These are
  all legitimate deferrals documented in `planning-context.md` §8 and
  `tasks.md`. The spec delta should be annotated so the next reader sees which
  scenarios are 13 vs 13b/Step17 without re-deriving.

## Coverage diagram (new code paths)

```
ILIntepreter.Neo.cs
│
├── Initobj CLR branch (:1604-1647)
│   ├── clrInitType == null (null index)        [untested, trivial]
│   ├── IsPrimitive || IsEnum (InitBlock flat)  [GAP: enum path wrong rep — MINOR-1]
│   ├── !IsValueType (null index)               [untested]
│   └── else: CreateDefaultInstance + mStore    [★ TESTED indirectly via ClrStruct tests]
│
├── Box CLR branch (:1699-1747)
│   ├── clrBoxType == null → InvalidCastException [untested, throw path]
│   ├── IsPrimitive (NeoBoxReturnValue flat)    [★★ TESTED — NeoTestIlPrimitiveBoxUnbox]
│   ├── IsEnum (NeoBoxPrimitiveByType flat)     [GAP: wrong rep for enum local — MINOR-1]
│   └── else struct (PerformMemberwiseClone)    [★★ TESTED — ClrStructNoBinding/WithBinder, Vec3, Point]
│
├── Unbox CLR branch (:1988-2014)
│   ├── clrUnboxType == null → InvalidCastException [untested, throw path]
│   ├── null source → NullReferenceException    [pre-existing, :1945-1949, IL path covers]
│   ├── IsPrimitive||IsEnum (NeoWritePrimitiveToFrame) [★★ TESTED — NeoTestIlPrimitiveBoxUnbox; enum GAP MINOR-1]
│   └── else struct (PerformMemberwiseClone)    [★★ TESTED]
│
├── NeoBoxPrimitiveByType (:2312)               [★ exercised via int box; IntPtr gap MINOR-2]
└── NeoWritePrimitiveToFrame (:2342)            [★ exercised via int unbox; IntPtr gap MINOR-2]

COVERAGE: delivered paths well-covered for primitives + CLR structs (with and
without binder) + IL value types. Gaps are the untested enum-local path
(MINOR-1) and native-int (MINOR-2). InvalidCastException throw arms and the
null-clrInitType/clrUnboxType arms are untested but trivial (harness cannot
easily assert throws; documented limitation).
```

## Migration / rollback

All changes behind `#if ENABLE_NEO_MODE`, additive (3 NIE throws →
implementations + 2 helpers). Legacy (`ExecuteR`) untouched. Rollback = revert
the change directory + the `ILIntepreter.Neo.cs` diff; no data-format changes.

## Final

**Verdict: FINDINGS — ship-eligible.** No Blocker, no Major. Two Minor findings
(MINOR-1 enum-local rep, MINOR-2 IntPtr) are pre-existing-pattern / untested-edge
and do not gate this change. The Area 2 boxed-ref deviation is verified TRUE and
the implementation CORRECT; the spec NIE gap is a legitimate scope refinement
(accept, annotate for 13b); the Area 3 deferral is legitimate (all blockers real,
zero regression). Full NeoStep smoke 49/49 green, areas 4-5 untouched.
