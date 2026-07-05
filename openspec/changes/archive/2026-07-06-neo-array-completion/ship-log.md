# Ship Log — neo-array-completion (D-ARR rank-1 array gaps)

**Date:** 2026-07-06. **Branch:** `features/object-model-overhaul`.
**Review:** APPROVED (0 Blocker / 0 Major introduced by this change;
1 Major PRE-EXISTING follow-up surfaced — F-1 double-local-combine quirk,
upstream of D-ARR; F-2 Minor, F-3 Trivial).
**Working tree:** UNCOMMITTED (LEAD commits after ship).

---

## What shipped — D-ARR rank-1 array completion

The Neo rank-1 array story (Step 16 + step17-completion) left small "rare in
C# output" gaps that surfaced as a Step-16/17-tagged `NotImplementedException`
or an untagged `InvalidCastException` the first time a real test hit them. This
change closes the rank-1 gaps. **The proposal's "5 gaps" collapsed to 2 real
gaps** in this Mono.Cecil fork:

- `Code.Ldelem` (generic) does NOT EXIST — opcode 0xa3 IS `Code.Ldelem_Any`
  (already enumerated + handled).
- `Code.Stelem` (generic) does NOT EXIST — opcode 0xa4 IS `Code.Stelem_Any`.
- `Code.Ldelem_U8` does NOT EXIST — not a real ECMA opcode (an 8-byte unsigned
  load is just `Ldelem_I8`).

OQ1 (Ldelem_U8 routing) is MOOT — no such opcode. Future proposals should
verify CIL-code existence in THIS fork's `Code` enum before designing JIT
cases.

### Delivered machinery (Neo-only; Legacy `ExecuteR` is the REFERENCE, untouched)

- **`Stelem_I` + `Ldelem_I` runtime arms** (`ILIntepreter.Neo.cs`).
  - **Option A (`Stelem_I` goto `Stelem_I4`) REJECTED.** The `Stelem_I4` arm's
    typed-indexer casts only handle `int[]`/`uint[]`; an `IntPtr[]` would hit
    the `((uint[])sa)` fallback -> `InvalidCastException`. The design's "CLR
    indexer boxes a 4-byte IntPtr correctly" assumption was WRONG for this
    runtime (it uses direct casts, not `Array.SetValue`).
  - **Option B (dedicated arms).** The dedicated `Stelem_I` arm reads
    `val4 = *(int*)(frameBase + ip->Operand4)` and dispatches
    `int[] sia[si]=val4` / `uint[] sua[si]=(uint)val4` /
    `IntPtr[] ipa[si]=(IntPtr)val4` / `((UIntPtr[])sa)[si]=(UIntPtr)val4`.
    The symmetric `Ldelem_I` arm reads `v` (int) and writes
    `*(int*)(frameBase + ip->DstOffset) = v`. Native-int is I4-width on this
    VM (4-byte frame slot; dump-confirmed `100`/`-7`/`0x1234` round-trip
    including negative-value sign-extension).
- **`Ldelem_I` JIT case + optimizer cascade.** `case Code.Ldelem_I:` added to
  the JIT `Translate` switch (`JITCompiler.cs`, 3-register shape, no
  `op.Code` rewrite — direct cast to `OpCodeREnum.Ldelem_I`). Adding it
  exposed FIVE further enumerations of the `Ldelem` family that lacked
  `Ldelem_I` (each threw a generic NIE on first contact):
  `Optimizer.Utils.cs` `GetOpcodeSourceRegister` / `GetOpcodeDestRegister` /
  `ReplaceOpcodeSource` / `ReplaceOpcodeDest` + `Optimizer.Neo.cs`
  `LowerNeoOffsets` (Ldelem block). All SHARED (Legacy-neutral: they only add
  handling for a previously-NIE'd opcode). `Stelem_I` was ALREADY complete in
  all 5 optimizer enumerations pre-change — only its runtime arm was missing.
  Reviewer confirmed **NO MISS**: `FCP.cs` / `BCP.cs` / `RegisterCleanup.cs`
  do NOT enumerate the `Ldelem_*` family at all.
- **F-4 other-width CLR-array Stind/Ldind branches.** The step17-completion
  I4-only `mStack[objIdx] is Array cArr` branch is extended to the remaining
  widths a `ldelema`-produced CLR-array address can flow into:
  `Stind_I1/I2/I8/R4/R8` + `Ldind_I1/U1/I2/U2/U4/I8/R4/R8` +
  `Stind_Ref`/`Ldind_Ref`. Each becomes `cArr.SetValue(boxed(v), off)` /
  typed-cast `cArr.GetValue(off)` for the `Array` branch (mirroring the I4
  precedent), falling through to the existing IL-instance branch otherwise.
  Additive: the frame-native and IL-instance paths are byte-for-byte
  unchanged. (`Stind_I`/`Ldind_I` already `goto Stind_I4`/`Ldind_I4`, so they
  inherit the array branch and need no change.)

### Verification

- **NeoStep smoke:** 161/161 green (154 baseline + 7 keeper probes
  TC8/TC10/TC11/TC12/TC13/TC14/TC16), reproduced independently twice.
- **Load-bearing stash-toggle (engine files stashed):** TC8 (IntPtr[]),
  TC12 (long[]), TC13 (float[]), TC14 (double[]) FAIL-on-HEAD
  (`Stelem_I` NIE / `Stind_I8`·`R4`·`R8` array-branch absent ->
  `InvalidCastException` or the `GetNeoILInstance` fallback NIE) -> PASS-after-
  fix. TC10/TC11/TC16 PASS throughout (regression guards for already-working
  paths, as designed).
- **Optimizer cascade completeness (Probe #1, HIGHEST PRIORITY):** `Ldelem_I`
  present in ALL 6 sibling enumerations (4 Utils + 1 Neo + 1 JIT); no MISS.
  Empirically confirmed (TC8 + a high-register-pressure stress probe both
  clean). `Stelem_I` was already complete pre-change.
- **Dispatch correctness (Probe #2):** dedicated int[]/uint[]/IntPtr[]/
  UIntPtr[] dispatch is correct; Option A correctly rejected; wrong-type ->
  clean `InvalidCastException`; OOB -> clean; negative-value sign-extension
  round-trips (dump-confirmed).
- **F-4 width matrix (Probe #3):** I8/R4/R8 round-trip via `ref`+`ldelema` ->
  `stind`/`ldind` proven (TC12/13/14); I4 precedent byte-identical-additive;
  FAIL-on-HEAD confirmed.
- **Legacy-neutral (Probe #5):** byte-identical. NeoStep16 sub-filter:
  1 failure = TC8 (`Ldelem_I` NIE — Legacy runtime lacks the arm, a pre-
  existing Legacy gap), identical with/without the change. The shared JIT/
  optimizer changes only add handling for a previously-NIE'd opcode.

### DEFERRED — multi-dimensional arrays (rank-2+)

Multi-dimensional arrays (`int[,]`, rank-2+) remain OUT of scope. The rank-
aware `Address`/`Get`/`Set` `callvirt`, the rank-aware frame model, and
`new T[n,m]` construction are a substantially larger change that does NOT fall
out of the rank-1 work. **Deferred to a separate child `neo-array-multidim`**
(tracked in `.trae/documents/neo-deferred-items.md` + the portfolio). Multi-
dim stays an untagged JIT `NotImplementedException` today.

### Files touched (working tree UNCOMMITTED)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — `Stelem_I` +
  `Ldelem_I` runtime arms (Option B dispatch); `is Array` branch added to
  `Stind_I1/I2/I8/R4/R8` + `Ldind_I1/U1/I2/U2/U4/I8/R4/R8` +
  `Stind_Ref`/`Ldind_Ref`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` —
  `case Code.Ldelem_I:` (3-register shape).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Utils.cs` — `Ldelem_I`
  added to 4 enumerations.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — `Ldelem_I`
  added to `LowerNeoOffsets` Ldelem block.
- `TestCases/NeoStep16Test.cs` — 7 keeper probes + 2 documented omissions
  (TC9 UIntPtr[], TC15 ref-array).

---

## Accept review findings

### F-1 (Major, PRE-EXISTING) — double-local-combine optimizer quirk

**Not introduced by D-ARR; upstream of, and independent from, the array work.**
Refined beyond the implementer's note: the quirk is NOT "3+ locals" and NOT
"long" — it is **2+ `double` locals combined in one boolean expression** that
yields a silent wrong result. A single `double` read is correct; combine two
in one `if` and the comparison misfires. 3 `long` locals combined work fine.
This is the F-MAJ-1 class (silent wrong result on 8-byte primitives).

The failing comparison uses plain `Ldelem_R8` reads (the pre-existing fast
typed-indexer path, NOT the new `is Array` branches) and a pure double-local
combine. The implementer correctly worked around it (incremental `bad`-fold
pattern in TC11/TC12/TC14) and flagged it.

**Action (recorded):** new follow-up child `neo-double-combine-quirk` /
[OPT-HARDEN-3]. Tracked in `.trae/documents/neo-deferred-items.md` (F-8 /
NEO-DOUBLE-COMBINE) + the portfolio. Do NOT block this change on it.

### F-2 (Minor) — TC8 provenance / naming honesty

TC8 is titled `StelemI` and its comment claims it exercises the `Stelem_I`
runtime arm. But on HEAD its failure is `NotImplementedException: Unknown
Opcode:Ldelem_I` — the JIT NIE on `Code.Ldelem_I` fires at method-JIT time
(before any statement executes), so TC8 actually proves the **`Ldelem_I` JIT
case + optimizer cascade** is load-bearing, NOT the `Stelem_I` runtime arm.
The `Stelem_I` runtime arm's correctness is proven only by the apply-time
runtime debug output (`design.md`). The probe IS load-bearing for the change
overall, just not for the specific arm its name implies. Accepted-known;
naming nit, no correctness impact.

### F-3 (Trivial, positive) — `is Array` branches byte-identical-additive

The new `is Array` branches are mechanical one-line `else if` inserts between
existing branches; the frame-native and IL-instance paths are byte-for-byte
unchanged. TC16 guards the I4 path. No regression surface introduced.

### Accepted-known pre-existing upstream gaps (NOT regressions)

- **TC9 (UIntPtr[]) — unsupported primitive.** `AppDomain.GetPrimitiveSize`
  (`AppDomain.cs:1947`) recognizes `IntPtr` (returns 8) but NOT `UIntPtr`. Any
  `UIntPtr`-typed local/temp throws at `AllocateLocalStackSpaces`. The
  `Stelem_I`/`Ldelem_I` `UIntPtr[]` arms are correct but unreachable.
- **TC15 (ref-array) — Neo `ldelema` NIEs on CLR ref-type arrays.** Sits
  upstream of the new `Stind_Ref`/`Ldind_Ref` `is Array` branch (a Step 17
  follow-up, NOT D-ARR). The Ref branches are correct-by-construction (mirror
  the I4 precedent) but unreachable until the ldelema ref-type gap closes.

---

## Status

**DONE.** `ship-log.md` written. D-ARR rank-1 -> RESOLVED (multi-dim ->
`neo-array-multidim`); F-4 -> RESOLVED (width matrix complete except
UIntPtr/ref-array upstream gaps); F-1 (Major, pre-existing) recorded as
follow-up `neo-double-combine-quirk` / [OPT-HARDEN-3]; F-2/F-3 accepted-
known. Working tree UNCOMMITTED (LEAD commits after archive).
