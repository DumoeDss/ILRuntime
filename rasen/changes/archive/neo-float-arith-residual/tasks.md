# Tasks -- neo-float-arith-residual

- [x] T1 Phase-1 re-audit at baseline 65: build CLI (Debug_Neo --no-incremental,
      UseSharedCompilation=false) + TestCases (Debug). Confirm
      InheritanceTest07/16 fail with `3E-45` garbage on Neo (name filter).
      DONE: `val2 = 3E-45`, `val = 11`, exit-127 known pre-existing crash.
- [x] T2 Dump the JIT for `TestCls5.AbMethod2`: confirm opcodes
      `conv.r4 r3,r1; addi.r4 r2,r3,1.2; br; ret r2` (specialization FIRES) and
      that `OperandFloat=1.2f` (0x3F99999A=1067030938) survives the ELDC fold.
      DONE: JIT dump at `.tmp-fa-i07.log:7046-7073` proves `addi.r4` is emitted;
      child-16/21 seeding holds; `LowerR1R2` (Optimizer.Neo.cs:1798) touches only
      offsets 4/6, so the immediate at offset 8 (`OperandFloat`, aliased with
      `Operand`/`Register3`/`OperandOffset` per OpCode.cs:58-61) is preserved.
- [x] T3 Instrument `conv.r4` / `Addi_R4` runtime arms + the `Ret` arm +
      `Run`'s `NeoBoxReturnValue` read-back to locate the corruption. DONE:
      diagnostics proved body=12.2f, retDst=12.2f, Run result=12.2f (boxed
      Single). Corruption is AFTER `Run`, in `InvokeNeo.PushObject(isBox:true)`.
- [x] T4 Pin root cause: `ReadFloat` is `*(float*)&esp->Value` (INLINE), but
      `PushObject(isBox:true)` stores `ObjectType=Object` + `Value=mStack index`
      for ALL values incl primitives -> `ReadFloat` reinterprets the mStack index
      (3) as float -> `3E-45`. The cross-binding reader is
      `CrossBindingFunctionInfo<int,float>.Invoke -> ReadResult<float> ->
      ReadFloat`. Legacy `ExecuteR` leaves primitives inline; `InvokeNeo`
      violated that contract. DONE.
- [x] T5 Implement fix: `InvocationContext.InvokeNeo` `PushObject(ebp, mStack,
      result, true)` -> `false` (+ explanatory comment). `isBox:false` routes
      primitives through `UnboxObject` (inline, matches Legacy); refs/VTs
      identical. DONE: `InvocationContext.cs` (+15/-1, all `#if ENABLE_NEO_MODE`).
- [x] T6 Remove all temporary diagnostics (4 sites: Addi_R4, Conv_R4, Ret,
      Run-read). DONE: `grep FADBG` = 0 matches; `git diff --stat -- ILRuntime/`
      shows ONLY `InvocationContext.cs`.
- [x] T7 Name-filter verify: InheritanceTest07 PASS, InheritanceTest16 PASS
      after fix. DONE: both `Ran 1 tests, 0 failded`.
- [x] T8 Stash-toggle causality: stash `InvocationContext.cs` ONLY -> rebuild ->
      InheritanceTest07 FAILS (`3E-45 != 12.1f`) -> pop -> PASS. DONE: airtight.
- [x] T9 NeoStep smoke: **394 ran, 0 failed** (no regression).
- [x] T10 FULL SMOKE (truth): **65 -> 63** (928 ran, 63 failed). Delta -2 =
      InheritanceTest07 + InheritanceTest16 flipped to PASS (neither appears in
      the failure list). `.tmp-floatarith-postfix.log`.
- [x] T11 Legacy-neutral: structural -- the `isBox:false` change is at
      `InvocationContext.cs:561`, fully inside `#if ENABLE_NEO_MODE`
      (lines 478-567; `InvokeNeo` exists only under the gate). Legacy compiles
      none of it -> byte-identical. Legacy NeoStep smoke (plain `Debug` +
      `useRegister=true`) = **394 ran / 18 failed** == the documented
      pre-existing Legacy baseline (no regression; structurally impossible to
      regress since the gate excludes Legacy).
