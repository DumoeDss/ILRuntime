# Ship Log -- implement-neo-step16

**Change:** `implement-neo-step16` -- Neo Step 16: single-dimension array creation
(`Newarr`) and element access (`Ldelem_*` / `Stelem_*` / `Ldlen`) across the three
Neo array representations (CLR primitive arrays, IL reference-type arrays, IL
value-type arrays).
**Verdict:** **CLEAN** (0 Blocker / 0 Major / 0 Minor / 3 Informational).
**Date:** 2026-07-04. **Branch:** `features/object-model-overhaul`.
**Status:** Uncommitted (working tree). LEAD commits + pushes; SHIPPER did not
touch source.

## Change (one-line)

Implement `Newarr`, `Ldelem_*`, `Stelem_*`, and `Ldlen` in `ExecuteNeo`, with the
Neo offset-lowering pass lowering the array opcodes' three registers to byte
offsets and carrying the dest/src ref offsets (Operand4 = 3rd register byte
offset, Operand3 = ref offset), so rank-1 array element access works on Neo's
`byte*` frame.

## Verification evidence

- **CLI `Debug_Neo`:** 0 errors.
- **TestCases `Debug`:** 0 errors (`TestCases/bin/Debug/netstandard2.1/TestCases.dll`).
- **FULL NeoStep smoke:** **72 ran / 0 failed** (was 65 + 7 new NeoStep16 cases,
  0 regression). Filter `NeoStep` over the shared TestCases.dll + pre-generated
  HotfixAOT.patch, `useRegister=true`, `-f net8.0 --no-build`. New cases:
  TC1 int[]/TC2 long[]/TC3 float+double[]/TC4 IL ref-type[]/TC5 IL value-type[]/
  TC6 Ldlen/TC7 newobj-arg control.
- **Legacy untouched:** `git diff HEAD --stat` on
  `ILIntepreter.Register.cs` = 0 lines. All new runtime code behind file-level
  `#if ENABLE_NEO_MODE` (both runtime files wrapped from line 1).

## Review summary

REVIEWER (adversarial, author != verifier): **CLEAN**. 0 Blocker / 0 Major / 0
Minor / 3 Informational. Priority scrutiny confirmed: (a) Operand non-clobbering
is safe -- Ldelem/Stelem do not carry a token (Operand not stamped by JIT), and
Newarr's lowering never writes Operand; the Ldelem/Stelem arms touch only
DstOffset/SrcOffset/Operand3/Operand4, none of which alias Operand(@8). (b)
Newarr per-kind correctness verified, incl. full-loop VT pre-instantiation and
safe in-place read-before-write (JIT sets R1==R2). (c) Ldelem/Stelem per-kind
cross-checked against Legacy (primitive I1/U1/I2/U2 bool/sbyte/char
disambiguation order; IL ref -> mStack object; IL VT -> CopyILToFrame/
CopyFrameToIL; CLR object[] -> GetValue/SetValue). All 3 pre-existing quirks
verified genuinely pre-existing via TC controls and 0-diff-line grep.
`openspec-gstack-review` not invoked (Rails/JS-centric, does not apply); no PR
by design.

## Delivered scope

- `Newarr` `ExecuteNeo` arm: read count from `frameBase + SrcOffset`; resolve
  element type via `AppDomain.GetType(ip->Operand)`; allocate per kind -- CLR
  primitive/ref via `CLRType.CreateArrayInstance` / `Array.CreateInstance` and
  `AppDomain.GetType` registration; IL VT via `new ILTypeInstance[count]` +
  pre-instantiate EVERY slot via `((ILType)et).Instantiate(true)`; IL ref via
  `new ILTypeInstance[count]` (null slots); store array on mStack at
  `frameRefBase + Operand3`, write index to `frameBase + DstOffset`.
- `Ldelem_*` `ExecuteNeo` arms: I1/U1/I2/U2/U4/I4/I8/R4/R8 + Ref/Any. CLR
  primitive -> typed CLR indexer read into dest primitive slot (matching
  Legacy bool/sbyte/char disambiguation); IL ref -> mStack-resident object
  element, write index to dest slot; IL VT -> CopyILToFrame (primitive bytes +
  ref slots); CLR object[] -> Array.GetValue. Bounds via CLR typed indexer
  IndexOutOfRangeException.
- `Stelem_*` `ExecuteNeo` arms: I1/I2/I4/I8/R4/R8 + Ref/Any. Symmetric writes
  per kind: typed indexer write; mStack object store; CopyFrameToIL for VT;
  Array.SetValue for CLR object[]. VT-vs-ref dispatch via runtime
  `elemIns.Type.IsValueType && !elemIns.Boxed` (no token dependency).
- `Ldlen` `ExecuteNeo` arm: read array ref, write `((Array)obj).Length` to dest
  primitive slot (native int). Null array -> NullReferenceException via
  srcIdx<0 / null guards.
- Neo offset-lowering (`Optimizer.Neo.cs` `LowerNeoOffsets`): Newarr / Ldlen /
  all Ldelem_* / all Stelem_* added to the switch; Register1/2/3 lowered to
  byte offsets (DstOffset/SrcOffset) with the third register's byte offset in
  Operand4 and dest/src ref offsets in Operand3. Operand (Newarr's type token)
  never clobbered.
- New test: `TestCases/NeoStep16Test.cs` (7 cases, all green).

## Non-goals (deferred)

- **`Ldelema`** -- deferred to Step 17 (its only consumers are `stind`/`ldind`/
  `fixed`/`ref`-`out`, all Step 17). Remains a Step-tagged
  `NotImplementedException`; shipping the plumbing now would be dead code.
- **Generic-with-token `Code.Ldelem` / `Code.Stelem` and native-int
  `Code.Ldelem_I` / `Code.Ldelem_U8`** -- not enumerated by JIT `Translate`;
  remain JIT-time NIEs (rare in C#). Address when a real test needs them.
- **Multi-dimensional arrays (`int[,]`)** -- out of scope (rank-1 `*` only this
  step); different ABI.

## Accepted-known / pre-existing quirks

- **stelem-i-nie (Info):** `Stelem_I` (native-int store) is added to the
  LowerNeoOffsets switch (correct 3-register encoding for safety) but has NO
  interpreter arm -- falls through to the Step-tagged NIE. Loud, scoped out
  (rare `IntPtr[]`/`UIntPtr[]` native-int store). Flag for a future step if
  such tests are added.
- **quirk-newobj-alias (pre-existing, Step 10/11):** Newobj dest/arg register
  aliasing when `new T(intArg)` immediately FOLLOWS a `newarr`. TC7 (newobj
  with arg, no array) is GREEN, proving the newobj path works in isolation;
  the collision is in Call/Newobj Push-scanning lowering (0 diff lines added
  there). Worked around in TC4 via default-ctor + field-set. Flag for Step
  10/11 newobj/call hardening.
- **quirk-struct-renumber (pre-existing, BCP/copy-prop):** struct-local +
  field-mutation + element-read optimizer temp-renumber quirk. Basic struct
  store/load round-trips correctly (TC5 GREEN); the quirk is in optimizer temp
  renumbering, not the array path (0 diff lines to BCP/copy-prop). Worked
  around in TC5.
- **quirk-long-compare (pre-existing, conv/compare):** long default-zero
  compare (conv.i8) quirk. long store/load itself round-trips correctly (TC2
  GREEN); the quirk is in conv.i8 + long-compare evaluation (0 diff lines to
  conv/compare). Worked around in TC2.
- **Pre-existing carryovers from earlier steps (not introduced here):** K1
  (FCP VT propagation); K2-family (Move-path boxed-ref VT local); Step 13 area
  3 (constrained. -> Step 17) + areas 4-5 (Step 13b); Step 14 catch-wrapper +
  CheckExceptionType-NIE-for-non-CLRType; Step 15 cgt-un divergence
  comment-nit + peephole/PatchKind deferred.

## Files changed (working tree, uncommitted)

- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (+85) --
  LowerNeoOffsets array arms (Newarr/Ldlen/Ldelem_*/Stelem_*): lower
  Register1/2/3 to byte offsets, carry dest/src ref offsets in Operand3/
  Operand4, leave Operand (Newarr token) untouched.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+299) --
  Newarr / Ldelem_* / Stelem_* / Ldlen `ExecuteNeo` arms.
- `TestCases/NeoStep16Test.cs` (new, 5305 bytes, 7 cases).

## Git note

All of the above are **uncommitted working-tree changes**. Per workflow, the
SHIPPER did not edit source and did not commit; LEAD commits and pushes.
