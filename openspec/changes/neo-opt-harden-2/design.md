# Design - neo-opt-harden-2

Bug-fix step grounded in a JIT-dump-proven root cause. The prior review's
hypothesis was investigated against current code at propose time and is
DISPROVEN as worded; two stronger candidates remain, ranked. The fix is
PROVISIONAL and LOCKED at apply from the dump. Author does NOT verify; the
implementer confirms the dump, ships the fix, or honestly defers.

## Scope decision (from propose)

| Candidate root cause | Status at propose | Action this change |
|---|---|---|
| Prior review hypothesis: `AllocateLocalStackSpaces` slot-reuse / liveness | DISPROVEN (no reuse logic in the method; cursors only advance) | NOT the fix; recorded so it is not mis-attributed later |
| (1) D6 return-write vs local-slot representation mismatch (struct > 4-byte ref slot overflow) | LEADING; fits the full symptom signature (struct-specific, int-passes, each-value-individually-wrong, neighbour-corruption) | CONFIRM by JIT dump; FIX if confirmed |
| (2) `CleanupRegister` compaction renumbering a live struct local | POSSIBLE; runs before AllocateLocalStackSpaces | CONFIRM/REFUTE by dump; FIX only if (1) refuted and (2) confirmed |
| Not reproducible on HEAD (like Q-STRUCT / Q-LONG / Q-NEWOBJ) | UNLIKELY (13b reviewer reproduced deterministically) | DEFER with dump + reproducer pinned; ship NO guessed fix |

The honest call: the prior review's worded root cause does not match the code.
The discipline established by OPT-HARDEN K1 (probe the dump, STOP if the
designed fix is wrong) and the Q-* closures (do NOT guess at the shared pass)
is binding. The fix lands ONLY with a dump-proven defect.

---

## F-MAJ-1 -- 2+ simultaneous CLR struct locals (PROVISIONAL fix; dump-gated)

### Reproduction (confirmed by the 13b reviewer; re-confirm on current HEAD)

Probe (extend `NeoOptHardeningTest.cs` as `NeoOptHardTest_Fmaj1_TwoClrStructLocals`):

```csharp
TestVector3NoBinding v = TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f);
TestVector3NoBinding w = TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
int r1 = TestCLRBinding.SumTestVector3NoBindingFields(v);   // expect 600
int r2 = TestCLRBinding.SumTestVector3NoBindingFields(w);   // expect 3
if (r1 != 600 || r2 != 3) { int _ = 1 / 0; }   // FAILS on HEAD (DivideByZero)
// Isolation controls (each MUST also be promoted to its own test):
//   if (r1 != 600) { 1/0 }   -- FAILS alone
//   if (r2 != 3)   { 1/0 }   -- FAILS alone   (NOT an r1<->r2 clobber)
```

HEAD result (per the 13b review): the combined check raises
DivideByZero; each sub-check fails individually; a control with two CLR **int**
returns + combined check PASSES. After the fix: all PASS.

### Why the prior hypothesis is DISPROVEN (code-grounded)

`AllocateLocalStackSpaces` (`JITCompiler.cs:1394-1587`) was read at propose
time. The locals loop (`:1455-1514`) and the temp-register loop (`:1536-1548`)
allocate each slot by ADVANCING monotonic cursors:

- A CLR value-type LOCAL goes to the `else` branch (`:1475-1486`): `Size=4,
  RefCount=1`, `offset += 4`, `refOffset++` -- a boxed-object-reference slot.
- A reference-type local (`:1488-1498`): same `Size=4, RefCount=1`.
- A primitive local (`:1499-1512`): `Size = primSize`, `offset += size`.
- A temp register (`:1536-1548`): `Size = maxSize`, `RefCount = maxRefCount`,
  `offset += maxSize`, `refOffset += maxRefCount`.

There is NO liveness analysis, NO reuse of a freed slot, NO "reusable only
after the LAST use of its current occupant" logic. The cursors (`offset`,
`refOffset`) only advance; every surviving register index gets a DISTINCT,
non-overlapping `[Offset, Offset+Size)` byte region and a DISTINCT
`[RefOffset, RefOffset+RefCount)` ref region. Therefore the proposed
"liveness-aware slot allocator" fix describes a mechanism that DOES NOT EXIST
in this code; shipping it would be a no-op at best and an invented allocator at
worst. (Q-NEWOBJ reached the same outcome: a planner hypothesis of slot
collision was disproven by a dump showing distinct regions per register.)

### Candidate (1) LEADING -- D6 return-write / local-slot representation mismatch

`InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:263-331`), the CLR-struct-return
branch (`:315-330`):

```csharp
else if (retType.IsValueType)
{
    int retSz = Optimizer.GetNeoValueTypeManagedSize(retType.TypeForCLR); // e.g. 12 for Vector3
    WriteNeoValueType(res, retDstPtr, retSz);   // writes retSz FLAT bytes at retDstPtr
}
```

`retDstPtr` resolves to the dest register's frame byte `Offset`. For a CLR
value-type LOCAL dest, `AllocateLocalStackSpaces` declared that slot as
`Size=4` (boxed-ref). A 12-byte flat write into a 4-byte slot overflows by 8
bytes into the next local's `[Offset+4, Offset+12)` region. Two struct locals
=> the second `Make(...)` return corrupts the first; both `Sum` reads then
resolve corrupted mStack indices => both individually wrong.

Why this fits the FULL symptom signature (better than the liveness hypothesis):

- **Struct-specific:** only structs with `GetNeoValueTypeManagedSize > 4`
  overflow. The two-int-return control writes 4 bytes into 4-byte slots -> no
  overflow -> passes. (A `Vector3` is 12 bytes -> overflows by 8.)
- **Each value individually wrong when both live:** the overflow from the
  second struct's dest corrupts the FIRST struct's slot (the neighbour). The
  first struct's `Sum` then reads a corrupted mStack index => wrong. The second
  struct's own dest is written correctly by its return but its neighbour (the
  first) is not. Reordering changes which neighbour is hit but both sub-checks
  observe corruption because the `Sum` reads happen AFTER both writes.
- **Not an r1<->r2 clobber:** the corrupted bytes are the neighbour's mStack
  INDEX bytes, not the r1/r2 int values (those live in their own 4-byte slots).
- **Pre-existing:** the D6 path was added in Step 13b but the
  boxed-ref-vs-flat-bytes disagreement for a CLR-VT LOCAL dest is as old as the
  `Size=4, RefCount=1` declaration (Step 12 frame model). Pre-13b the pattern
  NIE'd before reaching this write, so it was unreachable, not silently wrong.

### Candidate (1) FIX -- provisional (LOCK at apply from the dump)

Two representation-consistent options; pick the one the dump + downstream paths
confirm:

- **Option A (write-side fix, Neo-only, PREFERRED if downstream agrees):**
  the D6 return-write for a CLR struct dest stores a BOXED REFERENCE (mStack
  index) into the 4-byte ref slot, mirroring how a CLR-VT local is DECLARED
  (`Size=4, RefCount=1`, `localIsRef=true`). I.e. `mStack[destRefBase] = res;
  *(int*)retDstPtr = destRefBase;` -- the same shape as the reference-type
  return store (`:332+`) and the newobj arm (`:283-293`). This is the minimal
  change that makes the write match the declaration. Risk: a SUBSEQUENT by-
  value PARAM read (D2) of that local must then read a boxed ref, not flat
  bytes -- but D2 already handles the boxed-ref CLR-VT-local shape for the
  Box/Initobj-sourced case (K2-FAM partial). VERIFY D2 consistency at apply.
- **Option B (declare-side fix, SHARED engine -- `JITCompiler.cs`):** declare a
  CLR value-type LOCAL dest as FLAT BYTES (`Size = GetNeoValueTypeManagedSize`,
  `RefCount = 0`, `localIsRef=false`) so the existing D6 flat-bytes write fits.
  This changes `AllocateLocalStackSpaces` (SHARED -- Legacy uses it too), so it
  MUST be Legacy-neutral: either gate `#if ENABLE_NEO_MODE` (and confirm Legacy
  keeps its `Size=4, RefCount=1` path) OR confirm a plain-`Debug` smoke is
  byte-identical. Risk: a CLR-VT local as flat bytes must be COPIED as flat
  bytes on `Move_Vt` / by-value-param copy; the IL-VT-local path already does
  this -- confirm the CLR-VT path does not assume boxed-ref here.

**LOCK the option at apply from the dump + a D2/Move_Vt consistency probe.**
Design does NOT force either; both are representation-consistent.

### Candidate (2) FALLBACK -- `CleanupRegister` compaction

`Optimizer.RegisterCleanup.cs:CleanupRegister` runs at `JITCompiler.cs:491`
BEFORE `AllocateLocalStackSpaces` at `:514`. It compacts dead registers and
renumbers survivors. IF the dump shows two live struct-local registers being
renumbered onto the same post-compaction index (so `AllocateLocalStackSpaces`
allocates ONE slot for two logical locals), candidate (2) is the defect and the
fix is in `CleanupRegister`. LIKELY REFUTED (the locals loop indexes by
`locVarRegStart + i` for original `body.Variables[i]`, and `locVarRegStart =
paramCnt` is protected from compaction), but confirm from the dump. If refuted,
do NOT touch `CleanupRegister`.

### Edge cases / adversarial probes (MANDATORY -- Step 17 B1 / OPT-HARDEN K1 lessons)

A green smoke does NOT prove a representation-consistency fix correct. Required
probes (extend `NeoOptHardeningTest.cs` under `NeoOptHardTest_Fmaj1_*`):

1. **Exact F-MAJ-1 reproducer** -- two CLR struct locals, combined check (FAILS
   on HEAD).
2. **Isolation controls** -- `r1 != 600` alone, `r2 != 3` alone (each FAILS on
   HEAD; proves not-a-cross-clobber).
3. **3+ simultaneous CLR struct locals** -- `v, w, x` from three `Make(...)`
   calls; combined `Sum` check. Stresses the overflow direction (does the 3rd
   corrupt the 1st, or the 2nd?).
4. **Live-range OVERLAP across a method call** -- construct `v`, call a method
   (not `Sum`) that touches the frame, construct `w`, then `Sum(v)`. Tests that
   the fix is not order-dependent and survives an intervening call.
5. **Slot-reuse / reclaim probe** -- a CLR struct local reused after its last
   use (scoped block `{ S v = Make(); r1 = Sum(v); }` then `{ S w = Make(); r2
   = Sum(w); }`). VERIFIES the fix does NOT over-conservatively balloon the
   frame (a real liveness allocator would reclaim `v`'s slot for `w`; the
   monotonic allocator never did, and this change must not regress frame size).
6. **Struct exactly 4 bytes / 8 bytes** -- boundary sizes (overflow only starts
   past 4). A 4-byte struct MUST pass on HEAD (no overflow); an 8-byte struct
   overflows by 4.
7. **Single CLR struct local regression** -- the 13b tests
   (`NeoStep13bClrStructByValueParamNoBinding` etc.) still PASS (they hold one
   struct local; the fix must not break the working single-local path).
8. **Two CLR int returns** -- the documented control; MUST pass on HEAD and
   after (the fix must not regress the primitive-return path).

Probes (1)(2) FAIL on HEAD and PASS after; (3)(4)(6) FAIL on HEAD where the
overflow is real; (5)(7)(8) PASS throughout (regression guards).

### Regression test that pins it

`TestCases/NeoOptHardeningTest.cs` -- `NeoOptHardTest_Fmaj1_*` (the reproducer
+ the 8 probes above). Filter: run under `NeoOptHardTest_` (mirrors K1; the
`NeoStep` filter does NOT catch them) AND add the exact reproducer under the
`NeoStep` filter (e.g. promote to `NeoStep13bTest` as
`NeoStep13bTwoClrStructLocalsRegression`) so the smoke catches a future
regression. Specify BOTH in tasks.

---

## Fix-point scope: shared engine -- Legacy-neutrality gate

`AllocateLocalStackSpaces` and `Optimizer.RegisterCleanup.cs` are SHARED
(Legacy `ExecuteR` uses them). The D6 return-write arm is Neo-only (inside
`ILIntepreter.Neo.cs`). Therefore:

- **Option A (write-side):** Neo-only; Legacy `ExecuteR` compiles it out. Gate
  is automatic (the file is Neo-only). LOWEST Legacy risk -- PREFERRED unless
  the dump forces Option B.
- **Option B (declare-side):** SHARED. MUST be gated `#if ENABLE_NEO_MODE`
  (Legacy keeps `Size=4, RefCount=1`) OR confirmed Legacy-neutral with a
  plain-`Debug` NeoStep-filter run (the K1 / neo-vt-this-addr pattern).

The 7 pre-existing Legacy `NeoStep`-filter failures (NeoStep15_TC6, NeoNaNR8,
...) are unrelated Neo-feature tests; the new F-MAJ-1 probes must show the SAME
Legacy failures with and without the fix (stash-toggle proof), as K1 did.

---

## Verification plan (implementer)

1. Build CLI `Debug_Neo` (0 errors) and TestCases `Debug` (0 errors).
2. STASH-PROVEN reproducer: write the F-MAJ-1 probe; confirm it FAILS on HEAD
   (DivideByZero on the combined check). Then stash ANY runtime change and
   rebuild -- confirm pre-fix behaviour (FAIL). Restore.
3. JIT DUMP gate (BEFORE the fix): dump `frame.LocalInfos` for the probe method.
   Confirm candidate (1) -- the two CLR struct locals' dest slots are
   `Size=4, RefCount=1` (boxed-ref), while the D6 write is `retSz` flat bytes
   (`retSz > 4` for Vector3) -- OR refute it (the slots are flat-bytes-sized
   and the write fits). LOCK Option A / B / fallback from this dump.
4. Apply the dump-locked fix. Re-run the F-MAJ-1 probes -- all PASS.
5. Full `NeoStep` smoke (99/99 baseline): 0 regressions; the promoted
   reproducer is green.
6. Legacy-neutrality: plain-`Debug` CLI build + NeoStep-filter smoke -- SAME
   pre-existing failures with and without the fix (stash-toggle), OR the fix is
   fully Neo-only (Option A) and a code-read argument suffices.
7. IF the dump refutes BOTH candidates (offsets distinct AND write fits yet
   symptom persists): capture the dump + reproducer, mark F-MAJ-1 DEFERRED in
   the spec, ship NO guessed fix (the Q-STRUCT / Q-LONG / Q-NEWOBJ outcome).

## Non-goals

- No fix to `AllocateLocalStackSpaces` liveness/reuse (NO such logic exists;
  the prior worded hypothesis is disproven -- do not invent an allocator).
- No change to FCP / BCP / copy-prop / `addrAlias` (not the fix site).
- No change to Legacy `ExecuteR` (it is the REFERENCE; Option B is gated if
  taken).
- K2-FAM boxed-ref bridge, Step 13 Area 4, Step 17 completions, delegates,
  async, AOT -- all out of scope.
- If the dump shows the bug is something else entirely (a third candidate), the
  implementer captures it and ships the minimal representation-consistency fix
  for THAT defect; the design does not force candidates (1) or (2).

---

## Apply outcome (2026-07-05) -- F-MAJ-1 FIXED via Option B

### Dump-confirmed root cause: candidate (1) CONFIRMED; candidate (2) REFUTED

A focused `frame.LocalInfos` dump for `NeoOptHardTest_Fmaj1_TwoClrStructLocals`
on HEAD (before the fix) plus a D6 return-write probe:

```
=== F-MAJ-1 DUMP (HEAD, pre-fix) ===
  stackReg=3 TotalStructSize=56 TotalRefSize=5 locVarRegStart=0
  slot[0] Offset=0  Size=4 RefOffset=0 RefCount=1 isRef=True   ; local v (boxed-ref)
  slot[1] Offset=4  Size=4 RefOffset=1 RefCount=1 isRef=True   ; local w (boxed-ref)
  slot[2] Offset=8  Size=4 ...                                  ; r1 (int)
=== D6 writes ===
  MakeTestVector3NoBinding retSz=12 retDstByteOff=...slot[0]   ; 12-byte flat write into 4-byte slot[0]
  MakeTestVector3NoBinding retSz=12 retDstByteOff=...slot[1]   ; 12-byte flat write into 4-byte slot[1]
```

- The two CLR struct locals (`v`, `w`) received DISTINCT, non-overlapping
  regions (Offset 0 vs 4, distinct RefOffset 0 vs 1). **Candidate (2)
  (`CleanupRegister` compaction) is REFUTED** -- no register-index collision.
- Each dest slot is `Size=4, RefCount=1, isRef=True` (boxed-ref), while the D6
  write is `retSz=12` flat bytes. A 12-byte write into a 4-byte slot overflows
  by 8 bytes into the neighbour. **Candidate (1) is CONCLUSIVELY CONFIRMED.**

### Chosen fix: Option B (declare-side flat-bytes), gated `#if ENABLE_NEO_MODE`

The D2/Move_Vt consistency probe (Block 0.6) FORCED Option B over Option A:

- The by-value PARAM read (`CLRMethod.Invoke` D2 path) reads the callee param
  region (flat bytes sized by `GetNeoValueTypeManagedSize`), and the caller
  copies the local's value into that region via `CopyNeoCallArguments` /
  `primSize = dstInfo.Size` byte-copy from the caller local's `Offset`. So D2
  already byte-copies N flat bytes from the caller local's frame region.
- The D6 return-write already writes N flat bytes (`retSz`) into the caller
  local's frame region via `WriteNeoValueType`.
- BOTH ends are flat-bytes at runtime; only the SLOT DECLARATION lied (boxed-ref
  `Size=4, RefCount=1`). The actual runtime representation is flat bytes -- the
  13b single-local tests passed DESPITE the under-sized declaration because D6
  wrote 12 flat bytes and D2 read 12 flat bytes back from the same Offset (the
  8-byte overflow hit an empty/neighbour slot, harmless with one struct local).
- **Option A (boxed-ref write) would break D2**: the caller-local -> callee-param
  copy byte-copies N bytes from the local's Offset; if the local held a 4-byte
  mStack index instead of flat bytes, D2 would copy the index value + garbage.
  Making Option A work would require changing the optimizer's param-copy map
  generation to dereference a boxed ref -- a much larger change touching the
  shared Call lowering. Option B is the minimal representation-consistency fix.

The fix declares a CLR value-type LOCAL in `AllocateLocalStackSpaces`
(`JITCompiler.cs`) as flat bytes (`Size = GetNeoValueTypeManagedSize`,
`RefCount = 0`, `localIsRef = false`), mirroring the callee param layout
(`AllocateNeoCallParamSlot`). Gated `#if ENABLE_NEO_MODE`; Legacy (ExecuteR)
keeps the boxed-ref `Size=4, RefCount=1` path (Legacy never reaches the Neo
D6/D2 flat-bytes arms).

### Post-fix dump (representation now agrees)

```
=== F-MAJ-1 DUMP (with fix) ===
  stackReg=3 TotalStructSize=72 TotalRefSize=3 locVarRegStart=0
  slot[0] Offset=0  Size=12 RefOffset=0 RefCount=0 isRef=False  ; local v (flat bytes)
  slot[1] Offset=12 Size=12 RefOffset=0 RefCount=0 isRef=False  ; local w (flat bytes)
=== D6 writes ===
  retSz=12 into slot[0] (12-byte slot) -- fits exactly
  retSz=12 into slot[1] (12-byte slot) -- fits exactly
```

Frame grew 56 -> 72 bytes (two 4 -> 12-byte slots); ref region shrank 5 -> 3
(two `-1` ref slots shed). Both struct locals now have distinct, non-
overlapping 12-byte regions `[0,12)` and `[12,24)`.

### Edit site (Neo-only path, SHARED method -- Legacy-neutral)

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `AllocateLocalStackSpaces` CLR-VT-local branch (was `:1475-1486`): split into
  a Neo `#if ENABLE_NEO_MODE` branch (flat bytes) and a Legacy `#else` branch
  (boxed-ref `Size=4, RefCount=1`). ONE branch changed; the Legacy control flow
  is byte-identical (verified by stash-toggle).
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` -- added the F-MAJ-1 host
  helpers (`MakeTestStruct4`/`SumTestStruct4`, `MakeTestStruct8`/`SumTestStruct8`,
  `MakeIntA`/`MakeIntB`, `TouchFrame`) and two boundary structs
  (`TestStruct4`, `TestStruct8`).
- `TestCases/NeoOptHardeningTest.cs` -- added 9 `NeoOptHardTest_Fmaj1_*` probes
  (run under the `NeoOptHardTest_` filter, NOT `NeoStep`).
- `TestCases/NeoStep13bTest.cs` -- promoted the exact reproducer as
  `NeoStep13bTwoClrStructLocalsRegression` (smoke catches future regressions).

### Verification results

- F-MAJ-1 reproducer on HEAD (pre-fix): FAILS (DivideByZero), 7 of 9 probes
  FAIL (reproducer, IsolationR1/R2, ThreeStructLocals,
  LiveRangeOverlapAcrossCall, ScopedReuseNoFrameBloat, StructSize8); the 2
  controls (StructSize4, TwoClrIntReturns) PASS -- matches the candidate-(1)
  signature exactly (overflow only past size 4).
- After the fix: ALL 9 F-MAJ-1 probes PASS; 3 K1 probes still PASS (12/12 under
  `NeoOptHardTest_`).
- Full `NeoStep` smoke: 100/100 PASS (99 baseline + 1 promoted reproducer).
- Legacy-neutrality (stash-toggle): plain-`Debug` + `useRegister=true`
  NeoStep-filter smoke shows the SAME 7 pre-existing failures with and without
  the fix (NeoTestClrStructNoBindingBoxRoundTrip,
  NeoTestClrStructWithBinderBoxRoundTrip, NeoStep14 TC1/TC5/TC8,
  NeoStep15_TC6, NeoNaNR8). Failure set byte-identical -> the `#if` gate works.

### Notable deviation from the propose-time ranking

The propose-time ranking called Option A (write-side) PREFERRED and Option B
(declare-side, SHARED) the fallback. The D2 consistency probe INVERTED this:
Option A would break the optimizer's caller-local -> callee-param byte-copy
(which already treats the local as flat bytes). Option B is the only
representation-consistent fix, and it is Legacy-neutral via the
`#if ENABLE_NEO_MODE` gate (the SHARED-method concern is handled). This is
exactly why the design LOCKED the option at apply from the dump rather than
forcing it at propose.

### Scoped-reuse probe observation

`NeoOptHardTest_Fmaj1_ScopedReuseNoFrameBloat` was DESIGNED as a regression
guard (PASS throughout). On HEAD it FAILS, not because of slot reuse, but
because the C# compiler does NOT narrow struct-local register liveness for
`{ }` block scope -- both `v` and `w` remain simultaneously-live method
locals (distinct IL variable indices), so the candidate-(1) overflow still
applies. This is consistent with the candidate-(1) signature (it is a 2-live-
struct-locals case, not a reuse case). After the fix it PASSES. The frame size
for this method grows correctly (two 4 -> 12-byte slots); there is NO slot-
reuse regression because the monotonic allocator never reused slots (the
propose-time finding holds).

### Out of scope (noted, NOT fixed)

- `GatherValueTypes` + the temp-register sizer (`maxSize` loop) only handle
  `ILType`, not CLR structs. A temp register that must hold a CLR struct > 8
  bytes (e.g. an intermediate `Box`/`Stobj` result) would be under-sized. The
  F-MAJ-1 reproducer does NOT exercise this (the Make() return dest is the
  LOCAL, not a temp). This is a separate, pre-existing gap; not in scope for
  F-MAJ-1.
- A CLR struct local WITH a registered ValueTypeBinder (managedCount > 0) is
  declared `RefCount=0` here (the reflection-fallback D6 return path NIEs
  ref-field structs upstream). The binder path is owned by autogen redirects.

