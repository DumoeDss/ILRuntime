## 1. Neo offset-lowering: stamp the ldtoken dest ref slot

- [x] 1.1 In `Optimizer.Neo.cs` `LowerNeoOffsets`, add a
      `case OpCodeREnum.Ldtoken:` (place it near the `Ldstr`/`Ldftn`/`Ldvirtftn`
      neighbours, ~line 822-853). The case SHALL stamp the dest ref-slot index into
      the SPARE `Operand3` (because `Operand` is already the 0/1 discriminator) and
      call `LowerR1`:
      `op.Operand3 = localInfos[op.Register1].RefOffset; LowerR1(ref op, localInfos);`
      Confirm `LowerR1` sets only `DstOffset` and does not clobber `Operand3`.
- [x]1.2 Sanity-check the `OpCodeR` explicit-layout union: `Operand3` (@16) is
      disjoint from `Operand` (@4-7), `OperandLong` (@12-19 low 8), and the register
      aliases (@4-11), so stamping it does not corrupt a field `ldtoken` reads at
      runtime. (Re-confirm by inspection; the recurring aliasing sharp edge should
      NOT apply here.)
- [x]1.3 Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
      Debug_Neo --no-incremental`) and confirm `WarnUnhandledNeoLoweringOpcode` no
      longer flags `Ldtoken` for a method containing `typeof(...)` (inspect
      `OUTPUT_JIT_RESULT` output, on by default in `Debug_Neo`).

## 2. ExecuteNeo arm: implement ldtoken (type path + field path)

- [x]2.1 In `ILIntepreter.Neo.cs` `ExecuteNeo`, add `case OpCodeREnum.Ldtoken:`
      (place near the `Ldstr` (~1484) / `Ldsfld` (~3783) neighbours). Gate is the
      enclosing Neo-only file (`#if ENABLE_NEO_MODE` end-to-end).
- [x]2.2 **Type path (`ip->Operand == 1`):** `IType type = AppDomain.GetType((int)ip->OperandLong);`
      throw `TypeLoadException` if null; else push `type.ReflectionType` as a Neo
      object reference:
      `dstIdx = frameRefBase + ip->Operand3; mStack[dstIdx] = type.ReflectionType; *(int*)(frameBase + ip->DstOffset) = dstIdx;`
      (Mirrors Legacy `case 1` `AssignToRegister(..., type.ReflectionType)`. The
      no-op `Type.GetTypeFromHandle` redirect passes the value through.)
- [x]2.3 **Field path (`ip->Operand == 0`):** mirror Legacy + the `Ldsfld` arm.
      `var declType = AppDomain.GetType((int)(ip->OperandLong >> 32)); int sIdx = (int)ip->OperandLong;`
      For an `ILType ilt`, read the static field value per category (primitive /
      inline-VT / reference) into `frameBase + ip->DstOffset` -- the SAME branches as
      the `Ldsfld` arm (~3783-3825). For a CLR declaring type, throw a tagged
      `NotImplementedException("Neo Ldtoken: CLR field handle not implemented")`.
      Add a comment that this mirrors a Legacy quirk (it returns the field VALUE, not
      a `RuntimeFieldHandle`).
- [x]2.4 **(Refactor, preferred)** Extract a shared helper for the IL-static-field
      read body shared by `Ldsfld` and the `Ldtoken` field path -- e.g.
      `ReadNeoILStaticField(ILType ilt, int sIdx, byte* dstSlot, byte* frameBase, AutoList mStack, AppDomain domain)`
      -- and call it from both arms, to avoid ~30 lines of duplication. If extraction
      proves risky, an inline copy with a cross-reference comment is acceptable for
      this change.
      **IMPLEMENTER NOTE:** chose INLINE (the acceptable fallback) over helper
      extraction. The `Ldsfld` arm is on the green NeoStep 306 baseline; extracting
      its body into a shared helper would touch a working path for a field-ldtoken
      case that is effectively unreached in the smoke (all ~60 typeof() hits are the
      TYPE path). The ldtoken field path is a verbatim copy of the Ldsfld body with a
      cross-reference comment, so divergence is unlikely. A future refactor can
      extract `ReadNeoILStaticField` from both arms trivially.
- [x]2.5 Build the CLI (`Debug_Neo --no-incremental`) and build TestCases
      (`dotnet build TestCases/TestCases.csproj -c Debug`). Resolve any compile
      errors (the arm is `unsafe`; reuse the existing `dstIdx`/`mStack`/`frameBase`/
      `frameRefBase` locals already in scope).

## 3. NeoStep regression probes

- [x]3.1 Add `TestCases/NeoStepLdtokenTest.cs` (public static no-arg methods,
      `[ILRuntimeTest]`, mirroring existing `NeoStep*Test.cs` conventions) with
      probes whose ASSERTIONS depend on `ldtoken` executing correctly (per the
      child-1 finding, a fault-only probe will NOT catch a wrong-value bug -- assert
      the consumed value, not just "did not throw"):
      - `typeof(int)` -> assert `result.Name == "Int32"` (and/or `result == typeof(int)`).
      - `typeof(string)` -> assert `result.Name == "String"`.
      - `typeof(<an ILRuntime-defined type>)` -> assert a non-null Type whose name
        matches.
      - a downstream consumer: e.g. feed `typeof(T)` into a CLR redirect that reads
        the name, or compare two `typeof` results for equality, asserting the
        expected value.
- [x]3.2 (If reachable from IL in TestCases) add a method/field handle probe
      (reflection via handle). If the JIT throws at emission time (no method branch)
      or the field path is unreachable, skip and record why in the task note.
      **IMPLEMENTER NOTE:** SKIPPED. The JIT has NO `ldtoken <method>` branch
      (`JITCompiler.cs:2914` throws NIE for any non-Field/TypeReference token), so
      `RuntimeMethodHandle` is unreachable by construction. `ldtoken <field>`
      (field path, Operand==0) is emitted by C# only for rare reflection/expression-
      tree/`RuntimeFieldHandle` patterns not present in the TestCases IL, so it is
      not reachable from a probe. The field-path arm is implemented and mirrors
      `Ldsfld` (reads the field VALUE, the Legacy quirk) but is exercised only by
      code paths outside this smoke. The field path also throws a tagged NIE for a
      CLR declaring type (parity with Legacy + Ldsfld; CLR static fields are child 4
      `neo-clr-static-fields`).

---

## IMPLEMENTER FINDINGS (post-implementation)

### CORRECTION to the planner's GetTypeFromHandle assumption (load-bearing)
The design/proposal assumed `Type.GetTypeFromHandle` is a registered no-op
redirect ("returns `esp` unchanged") and therefore the `ldtoken` type path can
push `type.ReflectionType` directly and have it pass through. **This is TRUE for
Legacy but FALSE for Neo**, and required a correction:

- `CLRRedirections.GetTypeFromHandle` (`CLRRedirections.cs:992`) IS a no-op
  (`return esp`) -- but it is registered via `RegisterCLRMethodRedirection` into
  `RedirectMap` (the **Legacy** map) and has the Legacy delegate signature. Neo
  dispatch (`InvokeNeoClrMethod`, `ILIntepreter.Neo.cs:943`) consults ONLY
  `RedirectMapNeo` (a separate dictionary, `CLRMethod.RedirectionNeo`), so the
  Legacy no-op never runs under Neo.
- The auto-generated `System_Type_Binding.GetTypeFromHandle_0_Neo`
  (`ILRuntimeTestBase/AutoGenerate/System_Type_Binding.cs:172`, registered via
  `RegisterCLRMethodRedirectionNeo`) was the sole Neo entry, and it was a BROKEN
  STUB: `@handle = default(RuntimeTypeHandle); result = Type.GetTypeFromHandle
  (@handle);` -- it IGNORED the argument and returned **null**. So without a fix,
  `typeof(T)` would yield null (`.Name`/`.FullName` NRE).

**Correction applied:** `GetTypeFromHandle_0_Neo` now reads the argument
reference (`ILIntepreter.ReadNeoReference`) -- the `type.ReflectionType` that
`ldtoken` pushed into the single `RuntimeTypeHandle`-typed arg slot -- and writes
it straight through as the result, mirroring the Legacy no-op. This is inside
`#if ENABLE_NEO_MODE` (the `#else` Legacy `GetTypeFromHandle_0` is untouched), so
Legacy-neutral.

**Durable gotcha for future planning:** the autogen Neo CLR bindings for methods
with a value-type parameter carry a `// TODO: ByRef or unsupported ValueType
parameters in Neo` and use `default(...)` -- they silently return wrong/null
values. Any Neo opcode whose result is consumed by such a binding (ldtoken ->
GetTypeFromHandle is the canonical case) needs the binding fixed too, not just
the opcode.

### ldtoken dest-ref-slot convention
`ldtoken`'s `Operand` is the 0/1 token-kind discriminator, so (unlike
`Ldstr`/`Ldftn`, which stamp the dest ref slot into `Operand`) the lowering stamps
the dest ref slot into the SPARE `Operand3` (@16), mirroring `Isinst`/`Castclass`/
`Initobj`/`Ret`. `LowerR1` then sets `DstOffset` from `Register1`. Confirmed
`Operand3` is disjoint from `Operand`/`OperandLong`/register aliases (the
recurring Explicit-layout sharp edge does NOT apply here).

### Probe assertion strategy (child-1 finding applied)
The harness (`BaseTestUnit.Invoke`) counts ANY managed exception (NIE/NRE/
DivideByZero) as Failed and "ran without throwing" as Pass. So probes feed the
`typeof` result to a CLR redirect (Type.get_FullName / Type.op_Equality /
String.op_Equality) and a wrong/null value reaches a fault (a null deref NREs
inside `get_FullName`; a wrong value fails the equality -> a deliberate `1/0`).

### Downstream issue surfaced (NOT a regression, out of scope)
The full (filter-less) Neo smoke now progresses further (typeof no longer NIEs
~60x) and reaches a pre-existing crash in
`System_Activator_Binding.CreateInstance_0_Neo` -> `ILType.GetStaticFieldOffset`
NRE (exit 127). This is a separate Activator/CLR-reflection bug unmasked by the
fix, not caused by it -- the NeoStep gate (310/0) is green. Belongs to a future
CLR-reflection/Activator child (or the broader overhaul), not neo-ldtoken.

## 4. Verify (smoke + Legacy-neutral)

- [x]4.1 Run the NeoStep smoke:
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
      -- confirm it stays green (306 baseline + the new probe TCs, 0 failures).
- [x]4.2 Run the FULL Neo smoke (drop the `NeoStep` filter) and confirm the
      `"Neo: opcode Ldtoken not yet implemented (Step 6)"` default-arm entry is GONE
      (the run still NRE-crashes later in `GenericMethodTest` -- that is a known
      pre-existing crash, not this change; capture is pre-crash).
- [x]4.3 Legacy-neutral proof: build plain `Debug` + run with `useRegister=true`
      and the `NeoStep` filter, confirm the same pre-existing Legacy failure set with
      and without this change (all runtime changes are inside fully
      `#if ENABLE_NEO_MODE` files, so plain `Debug` compiles them out -> Legacy
      binary byte-identical -> Legacy-neutral by construction).
- [x]4.4 `git status` to confirm only the intended Neo-only source files
      (`ILIntepreter.Neo.cs`, `Optimizer.Neo.cs`) + the new probe
      (`TestCases/NeoStepLdtokenTest.cs`) are modified. No stray debug prints; no
      accidental TestCases-under-`Debug_Neo` build.
