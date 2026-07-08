# Tasks - neo-debugger-neo-frame

> Implementation tasks. DUMP-GATE VERDICT (HEAD `50d0e7e2`, by-reasoning -- the
> guards return "not supported yet", no HEAD behavior to revert; see design.md):
> - `GetLocalVariableInfo`: SHIP (primitive + reference + CLR-struct; IL-VT
>   placeholder).
> - `GetThisInfo`: SHIP (slot-0 `this` recovery + F-4 indexer reuse for fields).
> - IL-value-type LOCAL reconstruction: SEQUENCE (follow-on).
>
> Neo-only edits; Legacy byte-identical. Full `NeoStep` smoke (226/0/0) is the
> regression gate; the host-side self-check (correct value + reflected mutation)
> is the correctness gate.
>
> **STATUS: DONE (apply).** All 6 tasks shipped. Capstone 4/4 PASS; NeoStep
> 226/0/0/0 (no regression); Legacy-neutral (plain `Debug` = 0 errors). See
> the per-task `[DONE]` markers + the durable-finding note on the LocalInfos
> this-slot indexing (Task 2).

## Task 1: `ReadNeoLocalValue` helper + `ReadNeoFramePrimitive` (Neo-only)  [DONE]

- [x] In `ILRuntime/Runtime/Debugger/DebugService.cs`, added (under
      `#if ENABLE_NEO_MODE`) `private unsafe object ReadNeoLocalValue(byte*
      frameBase, AutoList mStack, StackSlotInfo slot, IType localType,
      AppDomain domain)` mirroring the F-4 indexer branches
      (`ILTypeInstance.cs:421-444`):
  - [x] `localType.IsPrimitive` -> `ReadNeoFramePrimitive(frameBase +
        slot.Offset, localType, domain)`.
  - [x] `!localType.IsValueType` -> `int idx = *(int*)(frameBase + slot.Offset);
        return (idx >= 0) ? mStack[idx] : null;` (ABSOLUTE mStack index;
        design.md 3.5; confirmed by the ref-init sentinel + executor writes).
  - [x] `localType is ILType` -> placeholder string
        `"<IL value-type local: reconstruction deferred>"` (SEQUENCE).
  - [x] else (CLR value type, F-MAJ-1 flat bytes) ->
        `ILIntepreter.ReadNeoValueType(localType.TypeForCLR, frameBase +
        slot.Offset, ref cursor, slot.Size)` (boxed struct).
- [x] Added `ReadNeoFramePrimitive(byte* p, IType fieldType, AppDomain domain)`
      -- switch on the AppDomain primitive singletons, read the width via
      `Unsafe.ReadUnaligned<T>`, mirrors F-4's `ReadNeoPrimitive`
      (`ILTypeInstance.cs:547-579`) line-for-line (the only diff is `byte*` vs
      `byte[]`); unknown primitive -> tagged NIE (mirrors `:578`).
- [x] Added `ResolveLocalType(ILMethod m, VariableDefinition vd, AppDomain
      domain)`: `vd.VariableType.IsGenericParameter ?
      m.FindGenericArgument(vt.Name) : domain.GetType(vd.VariableType,
      m.DeclearingType, m)` (mirrors `ILMethod.cs:780-786`; the generic-param
      branch is also handled inside `AppDomain.GetType` -- the explicit branch
      matches the JIT/Prewarm single-source-of-truth resolution exactly).

## Task 2: `GetLocalVariableInfo` Neo arm (SHIP)  [DONE]

- [x] Replaced the `:268-271` "not supported yet" guard with a Neo arm (under
      `#if ENABLE_NEO_MODE`) that:
  - [x] Casts `byte* frameBase = (byte*)topFrame.LocalVarPointer;`.
  - [x] `var nf = m.CompiledFrame;`.
  - [x] **DURABLE FINDING (load-bearing):** `int paramCnt = m.ParameterCount +
        (m.HasThis ? 1 : 0);`. `m.ParameterCount` is the Cecil param count
        (EXCLUDES `this`), but `AllocateLocalStackSpaces`
        (JITCompiler.cs:1654) sizes `LocalInfos` with `paramCnt + HasThis?1:0`
        so `[0]` is the `this` slot. Using the bare `m.ParameterCount` indexes
        the WRONG slot (reads the `this`/neighbouring slot -> always 0/null).
        First-cut used the bare count -> all locals read as 0/null -> fixed.
  - [x] Loops `i in [0, m.LocalVariableCount)`: `vd = Variables[i]`,
        `lt = ResolveLocalType(...)`, `slot = nf.LocalInfos[paramCnt + i]`,
        `v = ReadNeoLocalValue(...)`, null->"null", formats as the Legacy arm
        (name via `m.Definition.DebugInformation.TryGetName` + the line-break
        logic; type name from the resolved `lt.Name` with a Cecil fallback).
  - [x] Wraps each iteration in `try/catch` (Legacy's resilience).
- [x] CONFIRMED the Legacy `StackObject*` arm (`#else`/unchanged) is NOT
      modified.

## Task 3: `GetThisInfo` Neo arm (SHIP)  [DONE]

- [x] Replaced the `:207-209` guard with a Neo arm that:
  - [x] Casts `frameBase` + reads `nf`/`mStack` as in Task 2.
  - [x] If `!m.HasThis` -> return "null".
  - [x] `var thisSlot = nf.ParamInfos[0]; int nIdx = *(int*)(frameBase +
        thisSlot.Offset); object thisObj = (nIdx >= 0) ? mStack[nIdx] : null;`
        (`ParamInfos[0]` IS the `this` slot; `ParamInfos` populated from the
        HasThis-inclusive `paramInfo` array, JITCompiler.cs:1693).
  - [x] Unwrap: `if (thisObj is ILTypeInstance) ... else if (adaptor)
        adaptor.ILInstance;` (mirrors Legacy `:222-229`).
  - [x] If `instance == null` -> return "null".
  - [x] Enumerate `instance.Type.TypeDefinition.Fields`; for each non-static
        field: `var v = instance[fieldIdx];` (the F-4 indexer, NO new field-read
        code), null->"null", formats as Legacy, increments `fieldIdx`.
  - [x] **DURABLE FINDING:** under Neo `instance.Fields` is `null` (byte[]
        Primitives + AutoList ManagedObjects instead), so the line-break logic
        keys off a COUNTED `totalInstanceFields` (non-static field count), NOT
        `instance.Fields.Length` (which would NRE). Renamed Neo-block locals
        with an `n` prefix to avoid CS0136 vs the Legacy arm's `idx`/`instance`/
        `fields`/`sb` in the same method scope.
  - [x] Wraps each field in `try/catch` (Legacy `:255-258`) so the F-4 NIE on an
        IL-VT field is swallowed.
- [x] CONFIRMED the Legacy `StackObject*` arm is NOT modified.

## Task 4: Capstone -- host-side self-check (the correctness gate)  [DONE]

- [x] Added `TestCases/NeoDebuggerFrameProbe.cs` -- an INSTANCE IL type
      (`HasThis`, so `GetThisInfo` runs) with non-static fields + 3 probe
      methods (primitive + reference locals; an adversarial mutate-before-throw;
      an 8-byte-long + reference mix), each assigning known values then throwing
      UNHANDLED (so `ExecuteNeo`'s unwind constructs the `ILRuntimeException` at
      `ILIntepreter.Neo.cs:4543`, populating `.ThisInfo`/`.LocalInfo`).
- [x] Added `ILRuntime/Runtime/Debugger/NeoDebuggerFrameCheck.cs` (under
      `#if ENABLE_NEO_MODE && DEBUG`) mirroring `NeoStep25LoadExecCheck`:
      a `static Result Run(AppDomain)` that invokes each probe via
      `appdomain.Invoke` (routes through the Neo parametrized-`Run` shim),
      catches the `ILRuntimeException`, asserts `.LocalInfo` CONTAINS the
      correct primitive AND reference values + `.ThisInfo` carries the field
      value; ADVERSARIAL cell asserts the MUTATED local value is reflected.
- [x] Added a 4th load-bearing cell: asserts `.LocalInfo` is NOT the HEAD
      refusal string ("not supported") -- binds the guard-vs-arm distinction
      explicitly (a future regression that re-introduces the guard trips here).
- [x] Wired the check into CLI special mode `NeoDebuggerFrame`
      (`ILRuntimeTestCLI/Program.cs`, inside the Neo `#if` block).
- [x] Binding evidence (the LEAD's gate): on HEAD the guards returned "not
      supported yet" -> the value assertions MISS + the refusal-string cell
      FAILs; with the Neo arms -> 4/4 PASS (the refusal-string cell also PASSes
      because LocalInfo now carries real values). The fix is load-bearing.

## Task 5: Regression gate  [DONE]

- [x] Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors); `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] Full `NeoStep` smoke: **226 ran, 0 failed, 0 ignored, 0 todos** (no
      regression). `NeoDebuggerFrame` capstone: **4/4 cells PASS**.
- [x] Legacy-neutral: plain `Debug` build of `ILRuntimeTestCLI` (+ the
      transitive `ILRuntime`) = 0 errors (all Neo arms under
      `#if ENABLE_NEO_MODE`).

## Task 6: Doc update  [DONE]

- [x] In `.trae/documents/neo-deferred-items.md`, the "DebugService reads the
      Neo frame" row: marked RESOLVED (primitive + reference + CLR-struct local
      shapes; `this`/field read via the F-4 indexer reuse); SEQUENCED:
      IL-value-type-LOCAL reconstruction + AOT-body variable inspection.

## Out of scope (sequenced -- follow-on children, design only here)

- **IL-value-type LOCAL reconstruction**: reconstruct the boxed IL-VT local
  instance from its split primitive + reference sub-regions (the frame-local
  analogue of F-4's IL-VT-FIELD reconstruction). Replace the placeholder string
  with the value. Gate: a probe with an IL `struct` local throws unhandled ->
  `.LocalInfo` shows the struct's fields.
- **AOT-body variable inspection**: under Neo AOT, `registerSymbols` is null
  (`ILMethod.cs:997-998`); serialize the variable metadata into the `.neo` so
  `GetLocalVariableInfo` works on a deserialized body.
- **CLI debugger-protocol capstone (optional)**: drive the VSCode DAP
  (`Debugging/VSCode/`) against a Neo frame and inspect locals through the
  frontend. The host-side self-check (Task 4) is the binding gate; this is
  frontend integration only.
