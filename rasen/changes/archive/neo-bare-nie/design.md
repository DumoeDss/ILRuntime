## Context

The Neo smoke (unfiltered) prints the bare default-message NIE ~38 times
pre-crash (the full run segfaults mid-stream, an unrelated pre-existing crash;
counts are pre-crash). The default message `"The method or operation is not
implemented."` is produced ONLY by `throw new NotImplementedException()` with no
string arg. There are ~208 such bare throws across `ILRuntime/Runtime/`, but
most are Legacy-only. `ILIntepreter.Neo.cs` itself has 28 NIE throws and ZERO
bare ones (all carry a `"Step N"` tag) — so the bare hits come from helpers the
Neo JIT/interpreter invoke.

Method used to find the Neo-reachable subset:
1. The test harness stores `ex.ToString()` (full throw-site stack) as the test
   message (`HotfixTestUnit.Run`), so the smoke output already contains the NIE
   stack traces. Ran the full smoke (no filter) to a file, grepped for the
   bare-NIE message, and extracted the distinct first stack frame.
2. Instrumented `GetPrimitiveSize` to log the offending `IType` (FullName /
   IsEnum / IsPrimitive / TypeForCLR) in its throw message, re-ran, to confirm
   the exact type category. (Instrumentation removed; NeoStep 316/0 restored.)
3. Cross-checked the bare-NIE sites in the Neo-reachable shared files
   (`AppDomain`, `ILTypeInstance`, `ValueTypeBinder`, `CLRRedirections`,
   `JITCompiler`) against their `#if ENABLE_NEO_MODE` gating to rule out
   Legacy-only sites.

## Deduplicated Neo-reachable bare-NIE site list

| # | Site (file:line) | Gates | What it guards | Hits | Verdict |
|---|------------------|-------|----------------|------|---------|
| 1 | `AppDomain.GetPrimitiveSize` `AppDomain.cs:2305` | Neo | residual: IType not a primitive singleton | ~16 | **IMPLEMENT** |
| 2 | `JITCompiler.GetLdfldCodeForType` `JITCompiler.cs:3013` | Neo | IL field type with no typed `Ldfld_*` opcode | 0 | **GUARD** |
| 3 | `JITCompiler.GetStfldCodeForType` `JITCompiler.cs:3098` | Neo | IL field type with no typed `Stfld_*` opcode | 0 | **GUARD** |
| 4 | `AppDomain` type-resolution `AppDomain.cs:1717` | shared | unhandled token shape in `GetType(token,...)` | 0 | **GUARD** |
| 5 | `AppDomain` method-ref `AppDomain.cs:2162` | shared | unhandled method-reference param-list shape | 0 | **GUARD** |
| 6 | `JITCompiler` token-resolution `JITCompiler.cs:2915` | shared | unhandled token shape | 0 | **GUARD** |
| 7 | `System_Enum_Binding.GetValues_0_Neo` `System_Enum_Binding.cs:77` | Neo binding | framework `Type.GetEnumValues()` NIE for IL-enum-as-Type | ~3 | **DEFER** |

The `GetPrimitiveSize` instrumentation confirmed three distinct ITypes reach it:
`System.Reflection.BindingFlags` (enum, 13), `TestVector3` (CLR struct, 2),
`TestCLREnum` (enum, 1) — i.e. enums (via `AllocateNeoCallParamSlot`'s `IsEnum`
branch at `Optimizer.Neo.cs:1567`) and CLR structs (via the `Stobj`/`Ldobj` arms
at `ILIntepreter.Neo.cs:5155`/`5258`, `ilType == null`).

## Goals / Non-Goals

**Goals:**
- Implement `GetPrimitiveSize` sizing for enums + CLR value types (unblocks
  ~16 method compilations/opcode runs). Tag the residual throw.
- Tag the 5 Neo-reachable resolution/splitter bare-NIE `else` branches (sites
  2-6) so none can ever emit the anonymous default message.
- A NeoStep probe per real implementation that FAULTS without the fix.

**Non-Goals:**
- Fix the autogen `Enum.GetValues` framework NIE (site 7) — deferred follow-up.
- Touch any Legacy-only bare-NIE site (ruled out below).
- Change JIT emission, the object model, or any opcode's happy-path behavior.

## Decisions

### D1: Broaden `GetPrimitiveSize` rather than fix each caller (IMPLEMENT, site 1)

`GetPrimitiveSize` has 24 callers across the Neo runtime/JIT. The root cause is
the helper is primitive-only while callers (legitimately) route enums and CLR
value types into it. Fixing each caller would be invasive and miss future
callers. Instead, add two branches before the throw:

```csharp
else if (fieldType.IsValueType && fieldType.TypeForCLR != null
         && !(fieldType is CLR.TypeSystem.ILType))
{
    // enum OR CLR struct: flat managed size (enum -> underlying primitive).
    return Optimizer.GetNeoValueTypeManagedSize(fieldType.TypeForCLR);
}
else
    throw new NotImplementedException(
        $"Neo GetPrimitiveSize: unsupported IType '{fieldType?.FullName}' "
        + "(not a primitive/enum/CLR-value-type) [neo-bare-nie]");
```

`Optimizer.GetNeoValueTypeManagedSize(Type)` (`Optimizer.Neo.cs:1625`) is the
canonical Neo struct sizer: it maps an enum to `Enum.GetUnderlyingType` then
returns `Unsafe.SizeOf`, handles `null` -> 0, and never throws. Reusing it keeps
the callee-layout byte-consistent with `AllocateNeoCallParamSlot`'s own
`IsValueType` branch (`Optimizer.Neo.cs:1591`) and the `Stobj`/`Ldobj` arms.

- **Why exclude `ILType`?** Callers handle IL value types BEFORE calling this
  helper (`ilType != null ? ilType.TotalPrimitiveSize : GetPrimitiveSize(t)`).
  An ILType reaching the residual throw is a caller bug; sizing an IL wrapper
  Type via `Unsafe.SizeOf` would be wrong, so we let it fall to the tagged throw
  rather than silently mis-size. (IL enums are sized by their callers via
  `TotalPrimitiveSize`, so excluding ILType does not regress IL enums.)
- **Why safe / additive?** The helper currently throws for ALL non-primitives,
  so every caller that passes a non-primitive is ALREADY broken. Working callers
  only pass primitive singletons (handled byte-identically). Broadening can only
  fix broken paths, never break a working one.
- **Alternative considered:** add the enum branch only (not structs). Rejected —
  the `Stobj`/`Ldobj` CLR-struct path (TestVector3) is the same root cause and
  `GetNeoValueTypeManagedSize` handles both uniformly.

### D2: Tag the 5 resolution/splitter residuals (GUARD, sites 2-6)

Each bare `throw new NotImplementedException();` becomes
`throw new NotImplementedException("<site-specific tagged message>");` naming the
site + the unhandled shape. No behavior change (the branches are `else` guards
for cases that do not occur in the current suite). Examples:
- Site 2: `"Neo GetLdfldCodeForType: IL field '{fieldType?.FullName}' has no typed Ldfld opcode [neo-bare-nie]"`.
- Site 4: `"Neo GetType(token): unhandled token shape {token?.GetType().Name} [neo-bare-nie]"`.

### D3: DEFER site 7 (framework NIE via autogen binding)

`GetValues_0_Neo` (`System_Enum_Binding.cs:77`) calls `System.Enum.GetValues(
@enumType)`; for an IL-defined enum surfaced as `System.Type`, the framework's
`Type.GetEnumValues()` throws the NIE (first frame `System.Type.GetEnumValues()`,
no file:line). This is NOT an ILRuntime throw, and the Legacy redirect
(`GetValues_0`) makes the same call. Fixing it requires an IL-enum-
`System.Type`-representation investigation (a redirect or a Type-wrapper fix) —
sibling to the autogen-binding follow-ups (e.g. the
`RuntimeHelpers.InitializeArray` Neo-redirect follow-up from child 2). Recorded
as a surfaced follow-up; out of scope.

### D4: Probe design (FAULT, per child-1/child-2 discipline)

- **Enum-param probe:** a `NeoStep` method that calls a CLR method taking an
  enum argument (e.g. a host helper taking `BindingFlags`, mirroring the
  DelegateTest reproducer). Without the fix the method FAILS TO JIT (the bare
  NIE is thrown during `LowerNeoOffsets`), so the probe FAULTS; after the fix it
  compiles and the enum value round-trips. (Pass criterion = ran without
  throwing + correct value.)
- **CLR-struct stobj/ldobj probe:** a `NeoStep` method that does a value-type
  copy through a byref for a CLR struct (`TestVector3`) — the `Stobj`/`Ldobj`
  path. Without the fix it throws the bare NIE at runtime; after, the struct
  copies correctly.

Both probes must FAULT on HEAD (stash-toggle: the fix is a 2-branch addition to
GetPrimitiveSize; toggle it off -> probe throws; on -> probe passes), matching
the child-1/child-2 stash-toggle verification.

## Risks / Trade-offs

- **[GetPrimitiveSize now sizes structs it used to throw on] -> caller-shape
  assumption.** A caller might have RELIED on the throw as a "this should never
  be a struct" guard. Mitigation: the throw was bare (no caller could
  meaningfully catch it as a control-flow signal), and the 24 callers are sizing
  helpers that WANT a size. Verify with the full NeoStep suite (must stay 316/0)
  + a Legacy-neutral check (`plain Debug + useRegister=true + NeoStep` baseline).
- **[Probe must FAULT] -> a wrong-value probe won't fail.** Mitigation: the
  pre-fix failure is a THROW (JIT NIE / runtime NIE), not a wrong value, so the
  probes fault loudly on HEAD by construction.
- **[Full smoke crashes pre-crash] -> the site list is pre-crash-exhaustive.**
  Mitigation: after implementing, the apply worker re-runs the full smoke and
  greps for the bare default message again to catch any additional site
  previously masked (same iterative discipline as children 1-4). Sites 2-6 are
  statically confirmed Neo-reachable (gating verified) regardless of whether
  they fire pre-crash.

## Ruled out: Legacy-only / non-Neo-reachable bare-NIE concentrations

For the apply worker / reviewer — these were investigated and ruled OUT (not
Neo-reachable, do NOT touch):
- `ILTypeInstance.cs:178, 214, 267, 950` — all inside `#if !ENABLE_NEO_MODE`
  (Legacy-only enum/value read paths).
- `ValueTypeBinder.cs:68, 95` — operate on the Legacy `StackObject*`/`esp->Value`
  stack model (`ObjectTypes` switch). Neo uses `ReadNeoValueType`/
  `WriteNeoValueType`, not these.
- `CLRRedirections.cs:1322, 1675` — Legacy `RedirectMap` redirects; Neo uses the
  separate `RedirectMapNeo` (child-2 durable finding).
- `ILIntepreter.Register.cs` (~97 bare) — the Legacy `ExecuteR` switch; not
  compiled into the Neo path.
- `ILIntepreter.cs` (~79 bare) — Legacy/shared helper paths; the Neo interpreter
  is `ILIntepreter.Neo.cs` (0 bare). (Individual Neo-reachable shared helpers in
  `ILIntepreter.cs` were not hit in the smoke and are not in the typed
  ExecuteNeo call chain for the failing tests.)
- Code generators (`BindingGeneratorExtensions`, `MethodBindingGenerator`,
  `CrossBindingCodeGenerator`), debugger (`DebugService`, `VariableInfo`),
  `RuntimeStack`, `StackObject`, `OpCode` — code-gen-time or Legacy/debugger
  stack-model code, not runtime Neo execution.
