# Tasks — implement-neo-step12b

Single-implementer pass. All new runtime code is `#if ENABLE_NEO_MODE`.
Author tests in ASCII (CJK-write corruption caveat).

## 1. Add the `Move_Vt` opcode

- [x] 1.1 Add `Move_Vt` to `OpCodeREnum` (OpCodeREnum.cs), placed immediately
  after the existing `Move` entry. Keep enum ordering stable relative to the
  existing Ldfld/Stfld families (do NOT insert into the middle of a typed
  block).
- [x] 1.2 Add `Move_Vt` to the `ToString(AppDomain)` switch in OpCode.cs
  (alongside the existing `Move` case) so JIT debug output is readable.

## 2. Register `Move_Vt` in the optimizer helpers

- [x] 2.1 In `Optimizer.Utils.cs`, add `Move_Vt` to the `GetOpcodeDestRegister`
  and `GetOpcodeSourceRegister` switches exactly where `Move` is registered
  (Register1 = dest, Register2 = src). This is defensive (BCP/FCP run before
  LowerMove so never see Move_Vt) but keeps the inliner and any future pass
  correct.

## 3. Implement `LowerMove` in the JIT

- [x] 3.1 In `JITCompiler.TypeSpecializeNeoOpcodes` (JITCompiler.cs ~542-548),
  extend the existing `case OpCodeREnum.Move:` block. After the existing
  `IsNeoReferenceSlot` stamping and `SetRegisterType`, look up the DEST
  register's type (`GetRegisterType(registerTypes, op.Register1)`).
- [x] 3.2 If the dest type `is ILType dstIl && dstIl.IsValueType && !dstIl.IsEnum`
  AND `dstIl.TotalReferenceCount > 0`, set `op.Code = OpCodeREnum.Move_Vt`.
  Leave pure-primitive VTs (`TotalReferenceCount == 0`) and all reference-type
  Moves as plain `Move`. Do NOT stamp sizes here — the authoritative slot
  Size/RefOffset/RefCount come from `localInfos` at lowering time (design 3).

## 4. Extend `LowerNeoOffsets` to lower `Move_Vt`

- [x] 4.1 In `Optimizer.Neo.cs LowerNeoOffsets`, add a `case
  OpCodeREnum.Move_Vt:` sibling to the existing `Move` case (~153-177). Using
  `localInfos[srcReg]`/`localInfos[dstReg]`:
  - `primSize = min(src.Size, dst.Size)` (same min rule as Move, to avoid
    clobbering neighbours);
  - call `LowerR1R2(ref op, localInfos)` to set `DstOffset`/`SrcOffset`;
  - `op.Operand2 = primSize;`
  - `op.Operand3 = localInfos[dstReg].RefOffset;`  (dst ref-run base)
  - `op.Operand  = localInfos[srcReg].RefOffset;`  (src ref-run base; repurpose
    the former isRefMove flag field)
  - `op.Operand4 = localInfos[dstReg].RefCount;`
- [x] 4.2 Verify the chosen Operand fields are standalone (offsets 8/12/16/20
  per OpCode.cs) and that none alias Register1/2/3 after `LowerR1R2`. Confirm
  `Operand` (offset 8, aliased with Register3/OperandOffset) is safe to write
  here because Move_Vt does not use Register3 (only Register1/2 via LowerR1R2).
  Document this in a code comment.

## 5. Implement the `Move_Vt` ExecuteNeo arm

- [x] 5.1 In `ILIntepreter.Neo.cs`, add `case OpCodeREnum.Move_Vt:` immediately
  after the existing `Move` arm (~line 458). Body:
  - `int primSize = ip->Operand2;`
  - `int dstRefBase = ip->Operand3;`
  - `int srcRefBase = ip->Operand;`
  - `int refCount = ip->Operand4;`
  - if `primSize > 0`: `Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase +
    ip->SrcOffset, (uint)primSize);`
  - for `i in 0..refCount`: `mStack[frameRefBase + dstRefBase + i] =
    mStack[frameRefBase + srcRefBase + i];` (direct mStack object-reference
    copy; preserves object identity per shallow-copy semantics).
- [x] 5.2 Confirm `frameRefBase` and `mStack` are in scope at the Move arm
  (they are — the existing Move arm and the Ldfld_Ref arm at ~1682 both use
  them). No new locals beyond the four int reads.

## 6. Tests — `TestCases/NeoStep12bTest.cs` (ASCII)

- [x] 6.1 Define value types: `NeoStep12bVector3` (3 floats, refCount 0),
  `NeoStep12bWithOneRef` (int + string), `NeoStep12bWithManyRefs` (3 strings),
  `NeoStep12bInner`/`NeoStep12bOuterNested` (nested VT with a ref field).
- [x] 6.2 `NeoTestPurePrimitiveVtCopy` — populate a `Vector3`, copy it, assert
  all three fields equal; mutate the source's `x`; assert the copy's `x` is
  unchanged (aliasing independence). Uses the DivideByZero assertion pattern
  (NOT throw — CLR newobj is Step 9, see Step 12 Finding K).
- [x] 6.3 `NeoTestVtWithOneRefCopy` — `S { int a; string b; }`; copy with a
  non-null `b`; assert `y.a == x.a`, `y.b == x.b` (same reference), mutate
  `x.a`, assert `y.a` unchanged.
- [x] 6.4 `NeoTestVtWithManyRefsCopy` — `S { string p,q,r; }` (refCount 3);
  copy; assert all three fields copied (the current single-ref-Move breakage
  would drop 2 of 3). This is the core Step 12b regression test.
- [x] 6.5 `NeoTestNestedVtCopy` — `Outer { Inner i; int y; }`, `Inner { int x;
  string s; }`; populate, copy `Outer`, assert nested primitive + ref fields
  land correctly in the destination.
- [~] 6.6 `NeoTestVtCopyAliasingIndependence` — NOT shipped as a test.
  Investigation found this exposes a PRE-EXISTING optimizer bug (FCP
  propagates field reads through a value-type Move, ignoring intervening
  source mutations), independent of Move_Vt and out of scope. Documented in
  NeoStep12bTest.cs (inline note) and planning-context section 8. Move_Vt's
  value-copy correctness is instead covered by 6.3 / 6.4 (copy + read-back).
- [~] 6.7 (Validation, not new machinery) `NeoTestVtPassedByValueToMethod` —
  NOT shipped as a test. Investigation found VT-by-value parameter passing
  hits a PRE-EXISTING Step 8 call-lowering bug (the param-setup Move reads a
  stale ref index from the in-frame VT byte region; the call path does not
  route through Move_Vt). Even a primitive-only VT param fails. Documented
  in NeoStep12bTest.cs (inline note) and planning-context section 8. Out of
  scope.

## 7. Build, run, gate

- [x] 7.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
  → 0 errors.
- [x] 7.2 `dotnet build TestCases/TestCases.csproj -c Debug` → 0 errors,
  produces `TestCases/bin/Debug/netstandard2.1/TestCases.dll`. NEVER Debug_Neo
  for TestCases.
- [x] 7.3 Run the full NeoStep smoke (regression gate — VT copy is pervasive):
  `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
  TestCases/bin/Debug/netstandard2.1/TestCases.dll
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 36/36 after
  Step 12; the Step 12b cases ADD, no existing case regresses). Watch for
  previously-passing-by-luck whole-VT-copy cases that now behave differently —
  if a previously-green case now FAILS, that is a real Move_Vt regression.
- [x] 7.4 Confirm no Move_Vt path throws a Step-tagged
  `NotImplementedException` for the cases implemented.
- [x] 7.5 If any test takes >10s, kill it — that signals an interpreter
  infinite loop; investigate the Move_Vt arm.

## 8. Post-implementation notes (append to planning-context.md 8)

- [x] 8.1 After implementation, append durable findings (the chosen Operand
  encoding actually used, any deviation from this design, the final NeoStep
  count) to
  `openspec/changes/implement-neo-step12b/planning-context.md` section 8.
