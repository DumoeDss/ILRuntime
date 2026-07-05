# Proposal - neo-opt-harden-2

## Why

Step 13b surfaced **F-MAJ-1**: a method holding **2+ simultaneous CLR struct
locals** (+ int locals) computes a **silently wrong result**. The signature
(deterministically reproduced by the 13b reviewer): two CLR struct locals `v`,
`w`, each from a CLR method return (`Make(...)`), with `r1 = Sum(v); r2 =
Sum(w)`; a combined `r1 != 600 || r2 != 3` check FAILS though each sub-check
`r1 != 600` and `r2 != 3` PASSES in isolation. It is **not** an r1<->r2 cross-
clobber -- each value is individually wrong when both struct locals are live.
Pre-existing (stash-proven: pre-Step-13b the pattern was an unsupported NIE,
not a silently-wrong result). Step 13b made it **reachable** (the CLR-struct-
by-value feature now exists); the 13b tests work around it by holding a single
CLR struct local at a time.

This is the **`[OPT-HARDEN-2]`** follow-up: deliver a fix whose root cause is
PROVEN by a JIT dump on current HEAD (not assumed), or honestly report the bug
is not reproducible / not where the prior review assumed -- mirroring the
discipline established by OPT-HARDEN (K1) and the Q-STRUCT / Q-LONG / Q-NEWOBJ
closures. **A fix is shipped ONLY if a reproducing case on current HEAD plus a
JIT dump pinpoints the defect; a guessed fix to the shared frame-allocation /
return-write path is NOT shipped.**

## What Changes

- **F-MAJ-1 (FIXED -- root cause to be PROVEN by JIT dump at apply): two
  simultaneously-live CLR struct locals no longer silently corrupt each other.**
  The prior 13b review's hypothesis ("`AllocateLocalStackSpaces` slot-reuse /
  liveness") was INVESTIGATED AT PROPOSE TIME against current code and is
  **DISPROVEN as worded**: `AllocateLocalStackSpaces`
  (`JITCompiler.cs:1394-1587`) allocates a **strictly monotonic, non-overlapping
  `Offset` / `RefOffset` per surviving local/temp register** (cursors only
  advance; there is no slot-reuse logic in this method). So there is no
  liveness-aware-reuse allocator to "fix." Two STRONGER candidate root causes
  remain, ranked by code-grounded likelihood, each to be confirmed or refuted by
  a JIT dump at apply:
  1. **(LEADING) Return-write / local-slot representation mismatch (D6 path).**
     The Step-13b D6 return-write (`ILIntepreter.Neo.cs:315-330`) writes a CLR
     struct return's FLAT managed bytes (`retSz` bytes via `WriteNeoValueType`,
     e.g. 12 for a `Vector3`) into the caller's dest frame slot. But
     `AllocateLocalStackSpaces` sizes a CLR value-type LOCAL as `Size=4,
     RefCount=1` -- a **boxed object reference / mStack index** -- NOT flat
     bytes. A 12-byte flat write into a 4-byte ref slot OVERFLOWS by 8 bytes,
     clobbering the adjacent local's slot. The "two CLR **int** returns PASS"
     control fits: `int` writes 4 bytes into a 4-byte slot, no overflow. The
     "struct-specific" signature fits: only structs (size > 4) overflow. The
     "each value individually wrong when both live" fits: the overflow corrupts
     the neighbour, and the corrupted slot's mStack index resolves to a garbage
     object.
  2. **(FALLBACK) `CleanupRegister` compaction** renumbers a still-live struct
     local's register index onto a slot a different value still occupies -- to
     be confirmed/refuted by the dump (the compaction pass runs BEFORE
     `AllocateLocalStackSpaces` at `JITCompiler.cs:491`).
  The fix is PROVISIONAL pending the dump: for candidate (1) the fix is to make
  the D6 return-write and the local-slot declaration AGREE on the representation
  for a CLR struct dest -- either write a boxed ref (not flat bytes) into the
  4-byte ref slot, OR declare a CLR-VT-return dest as a flat-bytes region
  (sized `GetNeoValueTypeManagedSize`, `RefCount=0`). The representation choice
  has downstream consequences (the param-read D2 path, the `Move_Vt` /
  by-value-param copy) and is LOCKED at apply from the dump, not here.
- **If the dump REFUTES both candidates** (e.g. the offsets are provably
  distinct and the write sizes fit, yet the symptom persists), the change falls
  back to tracking F-MAJ-1 as a DEFERRED requirement (mirroring Q-STRUCT /
  Q-LONG) with the dump artifacts and the reproducer pinned. **No guessed fix
  ships.**

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: ADDS the F-MAJ-1 invariant -- the D6 CLR-struct return-write
  path and the local-slot declaration MUST agree on the representation of a CLR
  value-type local (boxed-ref vs flat-bytes), so a struct return does not
  overflow its dest slot and corrupt a neighbour. Records the
  `AllocateLocalStackSpaces` monotonic-allocation finding (no reuse logic
  exists) so a future liveness-allocator is NOT mis-attributed as the fix. The
  requirement is ACTIVE once a reproducing case + dump land a fix; DEFERRED
  (tracked, no fix) if the dump refutes both candidates.

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the D6 CLR
  struct return-write path (`~315-330`): the likely fix site (make the write
  agree with the local-slot representation). Gated `#if ENABLE_NEO_MODE` if the
  fix touches a path shared with `ExecuteR`; the return-write arm itself is
  Neo-only, but confirm.
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `AllocateLocalStackSpaces` (`~1475-1486`, the CLR-VT-local branch) is the
  ALTERNATIVE fix site IF the dump shows the representation should be flat-bytes
  (declare `Size = GetNeoValueTypeManagedSize`, `RefCount = 0`). SHARED ENGINE:
  a change here affects Legacy too, so it MUST be Legacy-neutral (gate
  `#if ENABLE_NEO_MODE` OR confirm plain-`Debug` smoke is unchanged). NOTE: the
  liveness-aware-reuse fix from the prior review is NOT the fix -- confirmed at
  propose that the method has no reuse logic.
- `TestCases/NeoOptHardeningTest.cs` (extend) -- the F-MAJ-1 regression tests
  (the exact reproducer + adversarial probes) under the `NeoOptHardTest_`
  prefix, mirroring the K1 convention (separate filter; FAIL-on-HEAD ->
  PASS-after for the leading candidate).
- No change to Legacy `ExecuteR`, FCP/BCP/copy-prop, or `addrAlias` (those are
  not the fix site).
- Regression gate: full `NeoStep` smoke (99/99 baseline at HEAD after
  neo-vt-this-addr) stays all-green; Legacy `Debug` smoke unchanged for any
  shared-engine edit.
