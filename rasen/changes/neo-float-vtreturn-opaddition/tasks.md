# Tasks: neo-float-vtreturn-opaddition

## 1. Diagnose (Phase 1) -- DONE
- [x] Wrote 3 minimal NeoStep probes (ctor alone / op_Addition return alone / array +=).
- [x] Built + ran under Neo; dumped JIT. TC1 actual=3 (v=(0,0,0)); TC2 actual=3 (c=(0,0,0));
      TC3 JIT-crash (separate). Pinned root cause = stale autogen `*_Neo` stubs (both route to
      call.redirect; reflection fallback works; generator correct but never regenerated).
- [x] Eliminated hypotheses (a)/(b)/(c)/(d) -- see design.md.

## 2. Implement (Phase 2) -- DONE
- [x] Part A: public facade `ILIntepreter.GetNeoValueTypeManagedSize` (ILIntepreter.Neo.cs,
      file-gated) delegating to internal `Optimizer` (the generator emitted an uncompileable
      internal-Optimizer reference -- latent generator bug).
- [x] Part A: repoint 5 generator emission sites to the facade (BindingGeneratorExtensions.cs
      x4, MethodBindingGenerator.cs x1) so future regen compiles.
- [x] Part B: hand-port `get_One2_0_Neo` (+ VT return write).
- [x] Part B: hand-port `op_Multiply_1_Neo` (VT param read + cursor realign + VT return write).
- [x] Part B: hand-port `op_Addition_2_Neo` (2 VT params read + VT return write).
- [x] Part B: hand-port `Ctor_0_Neo` (+ VT return write; see ctor follow-up note).
- [x] (Out of scope) `Normalize_4_Neo`/`Test_3_Neo` left (Area-4a VT-`this`).

## 3. Probes + host helpers -- DONE
- [x] Permanent probe TestCases/NeoStepFloatVtReturnTest.cs: TC1 op_Addition (9), TC2
      op_Multiply (18), TC3 child-26 shape arr[0]+=One (6, host-built array). Constants
      hand-checked vs inputs.
- [x] Host helpers (TestClass3.cs / TestCLRBinding): SumTestVector3ArrElem,
      BuildTestVector3OneArray, NeoAssertEq (diag, retained).

## 4. Verify -- DONE
- [x] NeoStep smoke 378/0 (375 baseline + 3), no regressions.
- [x] Stash-toggle (TestVector3_Binding.cs only) -> 3/3 FAIL (DivideByZero) -> restore -> 3/3 PASS.
- [x] Legacy-neutral: plain Debug+useRegister=true+NeoStep = 378/18 (pre-existing set; 3 probes
      PASS under Legacy). All changes `#if ENABLE_NEO_MODE` / file-gated / generator-only.
- [x] build-server-cache gotcha honored (kill dotnet build-server + UseSharedCompilation=false).

## 5. Ship
- [ ] Do NOT commit (LEAD commits after review-clean).

## Follow-ups (out of scope, documented)
- SYMPTOM 1: `new TestVector3(float,float,float)` still yields zero. The optimizer rewrites a
  CLR struct newobj to `initobj; ldloca; push(this byref); call.redirect .ctor` with dest=`-`
  -> runtime passes retDst=null -> the Ctor_0_Neo write-to-retDst no-ops. Needs either an
  optimizer change (preserve a dest register for struct newobj) or a redirect write-through-
  byref. Pin: ILIntepreter.Neo.cs:3079 (crRetDstPtr = Register1>=0 ? ... : null). Diagnosis +
  JIT dump in design.md.
- TC3(diag) JIT Push-lowering crash for the `arr[0] = new VT(...); arr[0] += new VT(...)`
  compound (LowerNeoOffsets "could not find expected Push").
- Instance-method stubs (Normalize/Test) Area-4a VT-`this` write-back.
- Full regen of all stale AutoGenerate files (TestStructB/Fixed64/JInt/...).
