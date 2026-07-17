# ship-log -- neo-long-literal-widened-temp (Wave-2 child of neo-overhaul)

Branch `features/object-model-overhaul`. Neo = ExecuteNeo under ENABLE_NEO_MODE.
Worker: PLANNER+IMPLEMENTER (single context). Not committed (LEAD commits).

## Result
- Full Neo smoke DELTA: **33 -> 32** (ExpTest_10.UnitTest_1020 flipped green).
- NeoStep smoke: **398/0** (no regression).
- Legacy-neutral: plain `Debug` CLI build = 0 errors (the fix is entirely in
  ILIntepreter.Neo.cs, which is file-gated under ENABLE_NEO_MODE).

## The framed mechanism was DISPROVEN (re-audit vindicated, again)
The batch child's pin ("both long args arrive as 0; the IL-to-IL call reads the
high dword / 4-byte slot overflow; call-marshalling/slot-sizing of widened long
temps") was WRONG on the mechanism. Instrumented diagnostics (CopyNeoCallArguments
+ conv.i8/conv.u8/conv.r4 dumps) proved:
- The conv.i8 dest IS seeded LongType (JITCompiler.cs:1183, GetConvResultType).
- The conv.i8/conv.u8 runtime arms write the full 8 bytes to the dest temp.
- Temp registers are uniformly sized to maxSize (>=8), so a widened long temp is
  NOT under-sized -- no 4-byte slot overflow.
- CopyNeoCallArguments copies PrimitiveSize[i] (= callee long param size = 8)
  bytes from the caller temp offset to the callee param offset. The callee
  receives the bytes VERBATIM at the correct offsets (confirmed: callee conv.r4
  read exp=20176515 and maxExp=<the conv.u8 result> correctly from its frame).
Call-marshalling of long args is CORRECT. The "0" in the test's Console.WriteLine
is a separate cosmetic ToString/ldloca artifact that does NOT affect the test
assertion (which keys on the return value `res`).

## Root cause (REAL): conv.u8 sign-extends an int32 source
`ReadConvU8` (ILIntepreter.Neo.cs) I4/default case did
`(ulong)*(int*)(frameBase + offset)`. The `*(int*)` read is sign-extended through
long on the way to ulong, so an int32 literal with the high bit set widened
WRONG. CIL `ldc.i4 0x8F0D1800; conv.u8` (Roslyn's lowering of a uint literal >
int32.MaxValue, 2400000000) produced 0xFFFFFFFF8F0D1800 instead of
0x000000008F0D1800. Legacy is correct: ILIntepreter.Register.cs:1190 does
`(uint)reg1->Value` (zero-extend). ECMA-335 III.4.6 conv.u8 on int32 zero-extends.

For UnitTest_1020 this made maxExp wrong -> percent = exp/maxExp wrong ->
res = -0.010647422 instead of 0.008406881 -> assertion throw.

## Fix 1 -- conv.u8 zero-extend (the D2 fix)
`ReadConvU8` I4/default: `(ulong)*(int*)` -> `(ulong)*(uint*)`. Read the 4 slot
bytes as uint (bit-preserving), then widen to ulong (zero-extend). Matches Legacy.
One line + comment.

## Fix 2 -- long-shift-by-immediate read the wrong field (pre-existing, EXPOSED by fix 1)
Fix 1 made the NeoStep probe `NeoStepMiscOp_ConvRUn` fault (DivideByZero). That
probe does `(ulong)u << 32`. The `Shli_I8` arm read `(int)ip->OperandLong` for
the shift count, but the ELDC fold (Optimizer.Utils.ReplaceRegisterWithConstant)
stores an Ldc_I4 shift count in `ip->Operand` (the int field @8), NOT
OperandLong (@12; disjoint). So the shift count was 0 -> every `long << constant`
silently became a no-op shift. The probe was PASSING at HEAD only by accident:
conv.u8's sign-extension made `(ulong)u` huge enough (1.84e19) to satisfy the
probe's loose `dl > 1.0e18` guard even with a 0 shift. Fix 1 (correct conv.u8)
unmasked the real shift-count value, which then failed the guard.

Fix: `Shli_I8` / `Shri_I8` / `Shri_Un_I8` read `ip->Operand` (matching the int
`Shli`/`Shri`/`Shri_Un` arms at Neo.cs:2874/2877/2880, which already read
Operand). The _I8 ARITHMETIC ops (Addi_I8/Subi_I8/Muli_I8/...) are UNAFFECTED and
left alone: their immediates come from ldc.i8 / a conv.i8-folded long constant,
which the fold stores in OperandLong -- so OperandLong is correct for them. Only
the SHIFT _I8 variants are wrong, because CIL shift counts are always int32
(never widened to long). 3 one-line fixes + comment.

## Verify (truth = full-smoke number + name-filter stash-toggle)
- Name-filter `UnitTest_1020`: HEAD (both reverted) -> res=-0.010647422, FAIL;
  after both fixes -> res=0.008406881, PASS.
- Name-filter `NeoStepMiscOp_ConvRUn`: HEAD -> PASS (by accident, huge value);
  fix-1-only -> FAIL (DivideByZero, shift count 0 exposed); both fixes -> PASS
  (correct: ul=0x8000000000000000, dl=9.2e18 > 1e18).
- FULL SMOKE: 33 -> 32 (UnitTest_1020 flipped; the 32 are a STRICT SUBSET of the
  33 -- no new failures; the shli.i8 fix broke nothing). Run:
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true`
  -> "Ran 932 tests, 32 failded, 20 ignored, 7 todos" (exit 127 = known graceful
  pre-existing Dict-NRE crash; summary emitted).
- NeoStep: **398/0**.
- Legacy-neutral: plain Debug CLI build = 0 errors.

## Files (NOT committed; LEAD commits)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (+16/-4):
  ReadConvU8 I4/default zero-extend; Shli_I8/Shri_I8/Shri_Un_I8 read Operand.

## Durable findings (for future children)
- **conv.u* on int32 ZERO-extends; conv.i* on int32 SIGN-extends.** The Neo
  ReadConv helpers must read the slot as the UNSIGNED type for the `.u` variants.
  ReadConvU8's `(ulong)*(int*)` was the bug (sign-extends through long);
  `(ulong)*(uint*)` is correct. ReadConvU4's `(uint)*(int*)` is fine (int->uint is
  bit-preserving at 32 bits). Audit ReadConvU2/ReadConvU1 if a future conv.u2/u1
  on a high-bit-set int32 misbehaves (truncation cases are bit-only, likely fine).
- **CIL shift counts are ALWAYS int32** (`ldc.i4 N; shl/shr/shr.un`), even when the
  value being shifted is long. The ELDC fold stores an Ldc_I4 immediate in
  `ip->Operand`. The Neo _I8 SHIFT arms MUST read `ip->Operand` (like the int
  arms), NOT `(int)ip->OperandLong` (disjoint field @12, read 0). The _I8
  ARITHMETIC arms are different: their long immediates come from ldc.i8 / a
  conv.i8-folded constant -> OperandLong is correct for them. Do NOT unify the
  shift and arithmetic _I8 immediate fields blindly.
- **A probe that PASSES on HEAD can hide a bug that a correctness fix unmasks.**
  ConvRUn's `dl > 1.0e18` guard was loose enough that the buggy sign-extended
  (huge) value passed it; the correct (smaller) value failed it because a SECOND
  bug (shli.i8 shift=0) was also latent. When a fix flips a previously-green
  probe, audit the probe's assertion strength AND look for a second latent bug in
  the same path before doubting the fix.
- **The "long args arrive as 0" framing was a misread of a Console.WriteLine that
  uses ldarga/ldloca + CLR ToString.** That ToString path reads longs as 0 under
  Neo (a separate cosmetic defect, NOT in scope here, does not affect return
  values or assertions). Always confirm a "value is 0" symptom against the actual
  computation/return value, not just a printed string, before attributing it to
  call-marshalling.
