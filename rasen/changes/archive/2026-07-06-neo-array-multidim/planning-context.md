# Planning Context — neo-array-multidim (LEAD seed)

> SEED for the planner. Read THIS FIRST, then the handoff docs, then research
> only what is missing. APPEND durable findings after propose.

## User intent

Continue the Neo completion portfolio. This child = multi-dimensional arrays
(rank-2+): `new T[n,m]` allocation + `a[i,j]` element get/set. Capability
`neo-arrays` (multi-dim is currently an explicit NON-GOAL there; this child
lifts it). Full autonomy: drive propose->apply->verify->review-loop->ship->
archive, LEAD commits+pushes after.

## What the LEAD already confirmed (orientation, 2026-07-06) — do NOT re-derive

### The autogen codegen for multi-dim ALREADY EXISTS for Neo
- **Array ctor (rank-2+ allocation):** `ConstructorBindingGenerator.cs:55-107`
  `GenerateConstructorWraperCode_Neo`, the `isMultiArr` branch (lines 77-89),
  emits `elemType result = new elemType[a1, a2, ...];` then
  `type.GetReturnValueCodeNeo(sb)`. `isMultiArr = type.IsArray &&
  type.GetArrayRank() > 1` (line 408).
- **Element Get/Set/Address:** `MethodBindingGenerator.cs:249-443`
  `GenerateMethodWraperCode_Neo`, the `isMultiArr` branch (lines 313-355):
  `Get` -> `instance[a1,a2,...]`, `Set` -> `instance[a1,...,aN-1] = aN`,
  `Address` -> `instance.Address(...)`. The byref write-back epilogue is
  SKIPPED for multi-arr (line 439 `if (!isMultiArr)`).
- So the AUTOGEN (registered-binder) path is COMPLETE for multi-dim on Neo.

### The likely gap = the reflection-fallback path (NO registered binder)
- The Neo `Newobj` arm (`ILIntepreter.Neo.cs:2168`) routes a CLR-type newobj to
  `InvokeNeoClrMethod(clrCtor, true, ...)` (`:2242`). For an array type
  (`int[0...,0...]`) `ilNewobjType` is null (arrays are CLR types) -> CLR branch
  -> `InvokeNeoClrMethod` -> `clrMethod.Invoke(targetBase, mStack, isNewobj:true)`
  (`CLRMethod.cs:333`).
- In `CLRMethod.Invoke` the `isNewObj` ctor path does `res = cDef.Invoke(param)`
  (`CLRMethod.cs:557`). For an array ConstructorInfo, `ConstructorInfo.Invoke`
  does NOT allocate a multi-dim array -- it likely throws (NotSupportedException
  / ArgumentException). The element Get/Set fallback `def.Invoke(instance, param)`
  (`:569`) on an Array's Get/Set MethodInfo MIGHT work via reflection, OR might
  need a special case. **DUMP-GATE THIS** -- do not assume.
- Whether the test harness registers a binder for `int[,]` determines which path
  a probe hits. CHECK the Legacy reference: does Legacy multi-dim work via autogen
  (binder registered) or via a reflection-fallback special case? Find the Legacy
  special-case (grep `ILIntepreter.Register.cs` + `CLRMethod.Invoke(esp,...)`
  overload at `CLRMethod.cs:653` for `IsArray`/`ArrayRank`/`CreateInstance`).
  NOTE: my grep for `IsArray|ArrayRank|CreateInstance` in
  `ILIntepreter.Register.cs` found ONLY the rank-1 `Newarr` arm (`:4894`
  `Array.CreateInstance(type.TypeForCLR, reg2->Value)`) -- NO multi-dim
  special case in the Legacy interpreter loop. So Legacy multi-dim likely works
  entirely through the CLR-method call machinery (the array ctor + Get/Set are
  CLR methods dispatched via the general CLR-call/newobj path, possibly with a
  reflection-fallback array special case in `CLRMethod.cs`). CONFIRM.

### Type-system support exists
- `IType.ArrayRank`, `IType.IsArray`, `IType.MakeArrayType(int rank)`
  (`IType.cs:30,32,92`). `ILType` (`ILType.cs:1101-1125, 2375-2386`) and
  `CLRType` (`CLRType.cs:237-242, 1045-1058`) both implement them.
  `ILType.arrayCLRType = rank>1 ? TypeForCLR.MakeArrayType(rank) :
  TypeForCLR.MakeArrayType()` (`ILType.cs:2386`). So a rank-2+ IL array type
  resolves its CLR form correctly.

## Probe BEFORE designing (binding lessons: OPT-HARDEN K1 / Q-NEWOBJ / F-10)

1. WRITE a minimal `NeoStep16_MultiDim` probe (or extend `NeoStep16Test.cs`):
   `int[,] a = new int[2,3]; a[1,2] = 42; return a[1,2];` + a 3-dim variant +
   an IL-element-type variant (`MyClass[,]`) + a `Get`-then-arithmetic probe.
2. RUN it on HEAD (Debug_Neo). Capture the EXACT failure (NIE text /
   exception type / stack). This is the arbiter.
3. ONLY THEN design the fix. If the failure is the reflection-fallback array
   ctor, the fix is a special case in `CLRMethod.Invoke` isNewObj (detect
   `DeclearingType.IsArray` -> `Array.CreateInstance(elemType, param dims)`)
   -- mirrors Legacy if Legacy has it; if not, mirror how Legacy's
   `CLRMethod.Invoke(esp,...)` newobj handles an array type. If the failure is
   elsewhere (JIT, autogen registration), follow the dump.
4. Adversarial probes MANDATORY (Step 17 B1 lesson: green smoke != correct):
   rank-3, IL-ref-element `[,]`, IL-VT-element `[,]` (element copy semantics),
   non-zero-based lower bounds if the C# compiler can emit them (it usually
   can't for `new T[,,]`; `(T[,])Array.CreateInstance(...)` can -- probably out
   of scope), out-of-range -> IndexOutOfRangeException caught, null array ->
   NullReferenceException, `Ldlen`-equivalent (`a.Length` / `a.GetLength(0)`).

## Capability spec delta (propose these)

- `neo-arrays`: LIFT the "Multi-dimensional arrays (`int[,]`)" NON-GOAL. Add a
  new Requirement: multi-dim allocation (`newobj` on the array ctor ->
  `Array.CreateInstance(elemType, dims)`) + element Get/Set (call to the array
  type's `Get`/`Set` instance methods -> `Array.GetValue`/`SetValue` or typed
  indexer). Scenarios: rank-2 int get/set round-trip; rank-3; IL-element-type
  `[,]`; out-of-range caught; null-array NRE.

## Build + test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # CLI, 0 errors
dotnet build TestCases/TestCases.csproj -c Debug                      # TestCases.dll
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # ALWAYS -f net8.0; baseline 190/190
```
- Build CLI with `Debug_Neo`; NEVER TestCases with `Debug_Neo`.
- `Debug_Neo` prints huge JIT output -- filter for the summary line
  ("Ran N tests, X failded"). A test >10s = interpreter infinite loop -> kill.
- For any SHARED-engine edit, confirm Legacy-neutral (plain `Debug` +
  `useRegister=true`, relevant filter).
- Build-cache gotcha: confirm the DLL actually rebuilt (mtime / grep a new
  string literal in UTF-16 via `strings -e l`). A stale DLL silently runs old
  code.

## Codebase gotchas (full detail in handoff section 4)

- `OpCodeR` is `[StructLayout(Explicit)]`; `LowerNeoOffsets` overwrites register
  indices with byte offsets. Snapshot `preOp = op` before a lowering case mutates.
- Shared vs Neo-only passes: FCP/BCP/copy-prop/RegisterCleanup are SHARED (gate
  `#if ENABLE_NEO_MODE` or confirm Legacy-neutral). The CLR binding generator
  emits separate `*Neo` variants -- only touch those.
- Legacy `ExecuteR` is the REFERENCE (read for semantics); never modify Legacy
  to make Neo work.
- Test harness is NOT xUnit: a test = `public static` parameterless method.
  Throw-asserting is hard; use DivideByZero pattern or try/catch flag.
- Write tool corrupts ~0.5% of CJK on large payloads; author ASCII-primary.

## Files the implementer will likely touch (dump-locked)

- `ILRuntime/CLR/Method/CLRMethod.cs:333-619` -- `Invoke(targetBase,...)` the
  `isNewObj` ctor path (`:557`) + the Get/Set call path (`:569`); the array
  special case (IF the dump confirms the reflection fallback is the gap). This
  is the SHARED reflection path -- a Neo-specific array branch must be gated or
  confirmed Legacy-neutral (Legacy's array handling may already cover it).
- Possibly `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:2168`
  (Newobj arm) if the array-ctor needs a Neo-side special case before reaching
  `InvokeNeoClrMethod`.
- Possibly `CLR/Method/CLRMethod.cs:653` `Invoke(esp,...)` (Legacy overload) --
  ONLY to understand the reference; do NOT modify Legacy unless the fix is
  genuinely shared-engine (mirror CATCH-COMPLETE / D-IL-EXCEPTION-THROW
  precedent).
- `TestCases/NeoStep16Test.cs` (extend) -- `NeoStep16_MultiDim*` probes.
- Verify whether `CLRRedirection` or an autogen binder registration in the test
  harness covers `int[,]`; if the autogen path is the intended route and the
  gap is "no binder registered", the fix may be a redirect, not engine code.

## Regression risk: MEDIUM.

The fix site is likely the CLR-method reflection-fallback array branch
(isolated, the same class as the rank-1 helpers) OR a Neo Newobj special case.
Gate: full `NeoStep` smoke (190/190 baseline) + Legacy-neutral stash-toggle for
any shared-engine edit. Adversarial probes MANDATORY.

## Maintain this file

APPEND durable findings after propose (decisions, dump-confirmed root cause,
the exact fix site). Do NOT append chatter.

## Findings -- neo-array-multidim (propose, 2026-07-06)

### The LEAD's hypothesis is DISPROVEN by the HEAD dump

The orientation guessed the gap was (a) a missing autogen binder for `int[,]`,
or (b) the reflection-fallback array-ctor (`cDef.Invoke(param)` failing for an
array ConstructorInfo). **Both are wrong.** A minimal probe
`int[,] a = new int[2,3]; a[1,2]=42; return a[1,2];` PASSES on HEAD `0aafdb34`.

What actually works on HEAD (all dump-confirmed green):
- **Autogen binder IS registered**: `System_Int32_Array2_Binding.Register`
  (`CLRBindings.cs:56`) for `int[,]` + `System_Int32_Array3_Binding.Register`
  (`:55`) for `int[,,]`. Neo redirects for the array ctor (`Ctor_0_Neo`) +
  `Set` (`Set_0_Neo`) are present (`System_Int32_Array2_Binding.cs:51-112`).
  The autogen codegen for multi-dim already exists for Neo
  (`ConstructorBindingGenerator.cs:77-89`; `MethodBindingGenerator.cs:313-355`).
- **Reflection-fallback ctor + Set + Get work for primitives**: `long[,]`
  (no binder) full-reflection round-trip PASSES.
- **Metadata works**: `Rank` / `Length` / `GetLength(i)` all correct.
- Rank-3 `int[,,]` works.

NOTE: there is NO `Get` redirect in the pre-generated binder (it registers
only Set + ctor). So EVERY multi-dim `Get` (read) falls to reflection
`def.Invoke(instance, param)` -- and that works for primitives.

### The REAL gaps (3, all in the reflection fallback, dump-confirmed)

- **Gap 1 (Neo-specific, value):** `string[,]` Get/Set round-trips WRONG on
  Neo (assertion fires); Legacy PASSES. Site: Neo `CLRMethod.Invoke(byte*,
  AutoList, bool)` overload (`CLRMethod.cs:333`) -- reference-param read
  (`:497-515`) and/or reference-return write (`InvokeNeoClrMethod`
  `ILIntepreter.Neo.cs:703-711`). DUMP-PIN Set vs Get at apply (OQ1). NOT
  multi-dim-specific -- it is the general Neo reflection-fallback reference-
  value path (rank-1 uses Ldelem_Ref/Stelem_Ref so it never surfaced before).
- **Gap 2 (Neo-specific, exception):** null array `this` -> Neo does NOT
  surface NRE (throws `ArgumentOutOfRangeException` from unguarded
  `mStack[-1]`); Legacy PASSES (explicit `if (instance == null) throw new
  NullReferenceException()` at `:712-713`). Fix: mirror Legacy's null-`this`
  guard in the Neo `HasThis` branch (`CLRMethod.cs:365-405`); handle the Neo
  null-ref sentinel `thisIdx < 0`.
- **Gap 3 (shared-engine, exception):** `MethodInfo.Invoke`/`ConstructorInfo.
  Invoke` WRAP the underlying exception in `TargetInvocationException` on BOTH
  engines -> user `catch(IndexOutOfRangeException)` fails. Probe
  `NeoStep16_MultiDimRank2OutOfRange` FAILS identically on Neo AND Legacy.
  Fix: `try/catch(TargetInvocationException)` around `def.Invoke`/`cDef.Invoke`
  in BOTH overloads (Neo `:557`/`:569`, Legacy `:694`/`:720`) -> rethrow
  `InnerException` via `ExceptionDispatchInfo`. Shared; Legacy-neutral gate
  MANDATORY (mirror CATCH-COMPLETE / D-IL-EXCEPTION-THROW precedent).

### Locked fix sites

- `ILRuntime/CLR/Method/CLRMethod.cs` -- Neo overload `Invoke(byte*, AutoList,
  bool)` (`:333`): Gap 1 (ref marshaling, dump-pinned) + Gap 2 (null-`this`
  guard). Both Neo-only.
- `ILRuntime/CLR/Method/CLRMethod.cs` -- BOTH overloads: Gap 3
  (TargetInvocation unwrap around `:557`/`:569` Neo + `:694`/`:720` Legacy).
  Shared; the ONLY shared-engine edit.
- Possibly `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (`InvokeNeoClrMethod :634-712`) IF Gap 1 pins to the return path.
- NO change to the autogen binder, the Newobj arm, or the JIT. NO multi-dim-
  specific engine code -- the gaps are reflection-fallback issues.

### New constraints / lessons

- **The autogen-vs-reflection decision is per-element-type AND per-method.**
  For `int[,]`: ctor+Set autogen, Get reflection. For `long[,]`/`string[,]`:
  all reflection. The harness binder set is FIXED (pre-generated, checked in
  `ILRuntimeTestBase/AutoGenerate/`); it is NOT regenerated at runtime.
- **`ConstructorInfo.Invoke` on an array type DOES allocate** the multi-dim
  array (reflection-correct) -- the LEAD's "likely throws NotSupportedException"
  guess was wrong. Confirmed by the passing `long[,]` ctor probe.
- **This is a verify-and-complete child, NOT a build child.** The multi-dim
  mechanism is ~90% delivered; the child closes the reflection-fallback
  exception/reference gaps + lifts the NON-GOAL + adds regression guards.
- Probes are scaffolded in `TestCases/NeoStep16Test.cs` (`NeoStep16_MultiDim*`,
  8 tests). HEAD status: Probe/MultiCell/Rank3/Metadata/Long PASS; String/
  Null/OutOfRange FAIL (the 3 load-bearing FAIL-on-HEAD probes). NOTE: the 3
  failing probes make the Neo smoke go 190+5 pass / 3 fail until the fix lands
  -- apply fix + probes together.

### Regression risk: MEDIUM

Gap 1/2 Neo-only (isolated). Gap 3 shared (Legacy behavioral change -- surfaces
inner exception instead of TargetInvocationException; strictly more correct;
Legacy-neutral stash-toggle required). Adversarial probes MANDATORY (string/
null/outofrange each FAIL-on-HEAD -> PASS-after).

## Findings -- neo-array-multidim (apply, 2026-07-06)

### OQ1 resolution -- Gap 1 is the GET-side return write-back (NULL encoding)

The planner's Set-vs-Get open question is resolved by a 3-stage temp-diagnostic
dump (`Console.WriteLine` inside `CLRMethod.Invoke` Neo overload PRE/POST
`def.Invoke`, inside `InvokeNeoClrMethod` reference-return branch, and inside
the `op_Inequality_3_Neo` autogen redirect), run ONLY on the
`NeoStep16_MultiDimRank2String` probe:

- **Set side is CORRECT.** `Set inst=String[,] params=[0,0,alpha]` / `[1,1,omega]`
  -- right array instance, right indices, right value. Reflection
  `def.Invoke(instance, param)` stores correctly (Legacy passes).
- **Get side `def.Invoke` returns CORRECT values.** alpha / omega / null.
- **The bug is the reference-return WRITE-BACK** in `InvokeNeoClrMethod`
  (`ILIntepreter.Neo.cs:703-712`). All 3 Gets shared `targetRetRefBase=1` and the
  SAME `retDstPtr` (the JIT merges the 3 short-circuited comparison operands into
  one dest temp `r5`), which by itself is fine (each Get is consumed inline by
  its `op_Inequality` / `cgt.un` BEFORE the next Get runs -- confirmed by the
  `[GAP1-OPINEQ] a=alpha b=alpha` / `a=omega b=omega` logs BOTH returning equal).
  The ACTUAL divergence: the `a[0,1] != null` cell. `Get` correctly returned
  `null`, but the write-back did `mStack[targetRetRefBase] = null;
  *(int*)retDstPtr = targetRetRefBase` -- i.e. it wrote the VALID INDEX
  `targetRetRefBase` (1) into the dest, NOT the null sentinel (-1). The Neo
  `cgt.un` null-test (`ILIntepreter.Neo.cs:1305-1308`,
  `cguRes = cguA != -1 && (...)`) inspects ONLY the dest's 4-byte index; a valid
  index (1) reads as "not null" -> `a[0,1] != null` returns TRUE -> the assertion
  fires (false positive).

  **Root cause:** the reflection-fallback reference-return write-back encoded a
  null result as a valid mStack index, violating the Neo null-sentinel
  convention (`Ldnull` writes `-1` at `ILIntepreter.Neo.cs:971`; the param read
  at `CLRMethod.cs:504` treats `idx < 0` as null). Rank-1 reference arrays never
  surfaced this because they use the dedicated `Ldelem_Ref` opcode (which
  encodes null as -1), not the reflection-fallback return path.

  **Fix (Neo-only, `ILIntepreter.Neo.cs` reference-return branch):** if
  `res == null`, write `-1` to retDstPtr (the null sentinel); else unchanged
  (keep the `mStack[targetRetRefBase] = res` + index write). The non-null path is
  byte-identical to before, so the primitive + metadata + non-null reference
  returns are unaffected (the 5 regression-guard probes + the 190-test Neo
  baseline stay green).

  **NOT the slot-reuse issue the design speculated about.** The
  `mStack[targetRetRefBase]` reuse across multiple sequential Gets is safe
  BECAUSE the JIT schedules each call's dest consumer (the next comparison
  instruction) before the next call clobbers the slot -- the `[GAP1-OPINEQ]`
  logs prove both `op_Inequality` calls read the correct (alpha/omega) value.
  Only the null-encoding was broken. (No fresh-slot / mStack.Add change needed.)

### OQ2 resolution -- no Legacy test catches TargetInvocationException

`grep -r TargetInvocationException TestCases/` = **0 matches**. No Legacy test
depends on catching `TargetInvocationException` directly, so unwrapping it to
the inner exception cannot regress any catch contract. Confirmed empirically by
the full-baseline stash-toggle (below).

### Exact lines changed

- `ILRuntime/CLR/Method/CLRMethod.cs`
  - Line 10: `using System.Runtime.ExceptionServices;` (added).
  - Gap 2 (Neo `HasThis` reference-`this` sub-branch, ~:401-413): changed
    `instance = mStack[thisIdx]` to `instance = thisIdx < 0 ? null : mStack[thisIdx]`
    (mirrors the param-read pattern at `:504`); the existing ctor/method
    null-instance guards then throw NRE.
  - Gap 3 (6 reflection Invoke sites, all in `try/catch (TargetInvocationException
    tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }`):
    Neo overload `:555` (non-newobj ctor), `:570` (newobj ctor), `:583` (method);
    Legacy overload `:698` (non-newobj ctor), `:711` (newobj ctor), `:738`
    (method). The non-newobj ctor sites were wrapped too (not only the 4 named
    in the design) for consistency -- any reflection Invoke can throw TIE.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  - Gap 1 (`InvokeNeoClrMethod` reference-return branch, ~:703-720): added the
    `if (res == null) *(int*)retDstPtr = -1; else { ...unchanged... }` split.

### New edge discovered (deferred -- Non-Goal)

- **IL value-type-element multi-dim arrays (`MyStruct[,]`).** The reflection
  `Get` returns a BOXED struct. The existing `retType.IsValueType` return branch
  (`ILIntepreter.Neo.cs:686-702`) writes a boxed CLR struct's flat bytes via
  `WriteNeoValueType`, so a PURE-PRIMITIVE struct element likely round-trips.
  BUT a VT with reference fields (or an IL-defined element type) needs
  element-copy semantics the reflection fallback does not own (mirrors the
  rank-1 VT-element + the Step 17 ldelema-on-ref-array deferred edges). Documented
  as a Non-Goal; NOT exercised by the smoke (the added probe set is
  primitive-element + `string[,]` only).
- **`op_Inequality_3_Neo` (autogen redirect) reads a null arg via
  `ReadNeoReference`, which does `mStack[idx]` with NO null guard** -- a null
  string arg (idx=-1) to `op_Inequality` would throw `ArgumentOutOfRangeException`
  (mStack[-1]). NOT exercised by the keeper probes (the `!= null` cell uses
  `cgt.un`, not `op_Inequality`), but it is a latent gap in the autogen reader
  for any `someString != nullArg` shape where the arg is a null reflection
  return. Out of scope for this child (autogen tooling); flagged for the
  neo-byref / autogen-hardening follow-up.

### Smoke evidence (final)

- **Neo full smoke** (`Debug_Neo`, `-f net8.0`, filter `NeoStep`): **198/198
  pass, 0 failed** (190 baseline + 8 multi-dim probes; the 3 formerly-failing
  String/Null/OutOfRange now pass).
- **Legacy multi-dim** (plain `Debug`, `useRegister=true`, filter
  `NeoStep16_MultiDim`): **8/8 pass** (incl OutOfRange -- Gap 3 is
  Legacy-neutral and now Legacy-correct).
- **Legacy full baseline stash-toggle**: HEAD (source stashed) = 723 ran / **11
  fail** (incl `MultiDimRank2OutOfRange` = TargetInvocationException); with fix
  = 723 ran / **10 fail** (OutOfRange now passes). The 10 remaining failures are
  IDENTICAL pre-existing Neo-under-Legacy failures (NeoOptHardening K1 x2,
  NeoStep13 ClrStructBox x2, NeoStep14 TC1/TC5/TC8, NeoStep15 TC6, NeoStep16
  TC8 StelemI, NeoNaNR8) -- none mention TargetInvocationException, none
  changed. **Strictly Legacy-neutral (11 -> 10, an improvement).**

