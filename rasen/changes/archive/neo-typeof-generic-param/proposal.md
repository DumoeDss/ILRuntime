# neo-typeof-generic-param

## Why
`TestGenericMethod2` failed under Neo: `typeof(<genericparam>)` mis-resolved.
`GetRows<int,int>` then `GetRows<int,double>` (same generic method, second
instantiation) resolved `typeof(B)` (B=double) to int. Root: the Step-22
generic-method template captured the body from the first instantiation
(<int,int>) and `ExtractPatches` had NO `Ldtoken` case, so the cloned template
kept the capture-T (int) type-token hash for `typeof(T)`. Legacy resolves
correctly (it has no template mechanism -- each instantiation JITs fresh).

## What (3 complementary Neo-gated fixes)
1. **Template ldtoken patch (THE typeof fix).** Add `case OpCodeREnum.Ldtoken`
   to `ExtractPatches` (`GenericMethodTemplate.cs`), field `Operand2` (the
   type-token lives in `OperandLong`'s low dword @12), Kind `TypeToken`.
   `DoCloneAndPatch` then re-resolves `typeof(T)` for the concrete T.
2. **Typed-opcode category fall-back.** The Neo JIT bakes TYPED arms
   (`Stfld_I4` vs `Stfld_R8`) into the template at Translate time, keyed on the
   capture T. CloneAndPatch patches tokens but NOT the typed opcode CODE, so a
   concrete T whose typed-field-opcode category differs from the capture T
   (e.g. int->double for the `NRow<V>` backing field) runs a wrong-typed arm
   (4-byte write of an 8-byte double -> corrupt value). Record the capture-T
   args on the template; in `TryInstantiate`, compare each concrete arg's
   `Stfld` opcode category (authoritative `JITCompiler.GetNeoStfldCodeForType`);
   a mismatch returns false -> per-occurrence JIT (the correct reference body).
3. **ConvertChangeType Neo redirect.** `typeof(CLRType)` pushes
   `CLRType.ReflectionType` = `ILRuntimeWrapperType`; the autogen
   `ChangeType_1_Neo` stub casts it raw to `System.Type` and hands it to the
   framework `Convert.ChangeType` -> "Invalid cast String->Int32". Legacy's
   autogen binding unwraps via `CheckCLRTypes`; the new `ChangeTypeNeo`
   redirect (registered on `RedirectMapNeo`) is the Neo twin -- unwraps
   `ILRuntimeWrapperType`/`ILRuntimeType` to the real `System.Type`.

Fixes 1 + 3 alone made TestGenericMethod2 stop throwing but V printed as a
garbage denormalized double (1.8E-315) -- fix 2 is what makes the values
CORRECT (K=789, V=345.678).

## Scope
Neo-only (`#if ENABLE_NEO_MODE` / Neo-gated files). Legacy-neutral by
construction. No JIT emission change (the category check uses the EXISTING
`GetStfldCodeForType` logic, refactored to a static form).

## Verify (truth = full-smoke number)
- Name-filter: `TestGenericMethod2` PASS after fix (K=123 V=345; K=789 V=345.678).
- Stash-toggle: HEAD engine -> TC1 DivByZero, TC2 Invalid-cast, TestGenericMethod2
  fail; popped -> all PASS.
- FULL SMOKE: **20 -> 19** (`Ran 935, 19 failed`; the 19 are a strict subset,
  TestGenericMethod2 removed; exit 127 = known graceful Dict-NRE crash).
- NeoStep **403/0** (401 baseline + 2 new probes; no regression).
- Legacy (plain Debug) build clean; TestGenericMethod2 + both probes PASS.
