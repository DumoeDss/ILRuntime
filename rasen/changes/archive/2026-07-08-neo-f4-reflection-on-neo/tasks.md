# Tasks - neo-f4-reflection-on-neo

> Implementation tasks. DUMP-GATE OUTCOMES (re-derived on HEAD during apply, with
> the probe confounds removed -- see design.md section 7 for the correction):
> - Path #1: NO-OP (already works; doc STALE).
> - Path #2: NO-OP (already works; doc STALE). The design's `-96` was NOT GetType
>   -- it was `Type.op_Equality` (`t != null`), a SEPARATE pre-existing
>   `ReadNeoReference` null-operand gap. With the confound removed, `e.GetType()`
>   returns a valid non-null Type on HEAD via the reflection fallback.
> - Path #3: SEQUENCED (parametrized-Run ABI extension; follow-on child).
> - Path #4: SHIP (the Neo indexer `get`/`set` arms). The ONLY real fix landed.
>
> Neo-only edits; Legacy byte-identical. Full `NeoStep` smoke (221/0/0) is the
> regression gate; the adversarial probes are the correctness gate.

## Task 1: Path #2 -- Neo `Object.GetType` redirect (REVERSED to NO-OP)

- [x] DUMP on HEAD with the confound removed (probe uses `ReferenceEquals(t, null)`
      instead of `t == null`/`t != null`, which lower to `Type.op_Equality` -- a
      separate `ReadNeoReference` null-operand gap, NOT a GetType gap): `e.GetType()`
      returns a valid non-null Type (the caught Adapter's CLR type) via the
      `InvokeNeoClrMethod` reflection fallback (`clrMethod.Invoke`, `CLRMethod.cs:334`).
      The `reference this` read at `:413` (`thisIdx < 0 ? null : mStack[thisIdx]`)
      is correct; the result store at `ILIntepreter.Neo.cs:727-753` is correct.
- [x] VERDICT: NO-OP. The redirect is NOT load-bearing (the fallback already works)
      AND would not fire even if added: the Neo redirect map is keyed by
      `typeof(object).GetMethod("GetType")` (DeclaringType=Object), but the JIT
      resolves `Object.GetType` on an IL exception (CLR base `System.Exception`)
      to the `System.Exception`-declared MethodInfo (a DISTINCT object + distinct
      MethodHandle per .NET reflection -- `typeof(Exception).GetMethod("GetType")
      != typeof(object).GetMethod("GetType")`), so `TryGetRedirection` misses.
- [x] NO engine edit shipped for #2 (the dead redirect that was prototyped was
      REMOVED to avoid implying a non-existent fix). The probe
      `NeoStep14_ILEx_GetType` is KEPT as a regression guard (asserts GetType
      returns non-null via `ReferenceEquals`, sidestepping the op_Equality gap).

## Task 2: Path #4 -- `ILTypeInstance` Neo field indexer (SHIP -- the real fix)

- [x] In `ILRuntime/Runtime/Intepreter/ILTypeInstance.cs`, replaced the
      `#else return null;` in the `this[int index].get` arm (`:398-400`) with a
      Neo `get` body:
  - [x] Gate: `if (index >= 0 && index < type.TotalFieldCount)` -> IL-field path;
        else -> the existing `FirstCLRBaseType` CLR-inherited branch
        (`clrType.GetFieldValue(index, clrInstance)`), byte-identical to Legacy.
  - [x] IL-field path: `var off = type.GetFieldOffset(index);` and
        `IType ft = type.GetField(index, out ILRuntime.Mono.Cecil.FieldReference fr);`.
  - [x] Dispatch on `ft` (the SAME source-of-truth the storage allocator at
        `ILType.cs:2300-2361` uses):
    - [x] Primitive (`ft.IsPrimitive`) -> `ReadNeoPrimitive(fields, off.PrimitiveOffset, ft, appdomain)`
          (new helper, switches on the AppDomain primitive singletons, mirrors
          the `Ldfld_*` arms at `ILIntepreter.Neo.cs:2753-2792`).
    - [x] IL value-type field (`ft.IsValueType && ft is ILType`) -> TAGGED NIE
          ("Neo ILTypeInstance indexer: IL-value-type field reconstruction not
          supported").
    - [x] Reference / enum (boxed) / CLR-struct (F-10 boxed) -> `managedObjs[off.ReferenceOffset]`.
          (Enum fields: `ILType.IsPrimitive` is always false and an enum IType is
          not an ILType, so enums fall here as boxed references -- matches storage.)
- [x] Mirrored the `set` arm: added a Neo `set` body (was empty under Neo) with
      the symmetric write (primitive via `WriteNeoPrimitive` / null via
      `WriteNeoPrimitiveDefault`; reference/enum/CLR-struct -> `managedObjs[off.ReferenceOffset]`;
      IL-value-type -> tagged NIE). RETAINED the `CheckAndCloneValueType` prelude
      and the `FirstCLRBaseType` `set` fallback.
- [x] Added helpers `ReadNeoPrimitive` / `WriteNeoPrimitive` / `WriteNeoPrimitiveDefault`
      under `#if ENABLE_NEO_MODE` (keyed on the AppDomain primitive singletons +
      `GetPrimitiveSize`, the SAME identity the allocator uses).
- [x] CONFIRMED the Legacy `get`/`set` arms (the `StackObject[] fields` path) are
      NOT modified (Legacy-neutral; the Neo body is under `#if ENABLE_NEO_MODE`).

## Task 3: Adversarial probes (correctness gate -- FAIL-on-HEAD -> PASS-after)

- [x] `NeoStep14_ILEx_GetType`: throw `new MyEx(...)`, catch, `e.GetType()`, assert
      non-null via `ReferenceEquals` (NOT `t == null`, which lowers to
      `Type.op_Equality`). FAIL-on-HEAD with the OLD probe form (`t != null` ->
      op_Equality AoRE, `-96`); with the confound removed it PASSES on HEAD too
      ( GetType already works) -- KEPT as a regression guard.
- [x] `NeoStep14_ILEx_IndexerFieldRead`: construct `MyEx` via the default ctor,
      assign `Msg = "idx-msg"` via a direct field write (NOT the string-param
      ctor -- see design.md section 8 for the newobj-arg caveat), throw+catch,
      recover `ili` via the bridge, read `Msg` through the indexer via the host
      helper `NeoF4ReflectionProbe.ReadFieldStringMatch` (pure-CLR comparison to
      avoid the IL `string == string` op_Equality gap). Assert value == "idx-msg".
      FAIL-on-HEAD (`-9`, indexer returns null) -> PASS-after task 2 (`9`).
- [x] Stash-toggle (task 2 only): engine edit reverted -> probe returns `-9`
      (null); restored -> returns `9`. Binding evidence the fix is load-bearing.
- [x] Added host helper `ILRuntimeTestBase/TestFramework/NeoF4ReflectionProbe.cs`
      (`ReadFieldStringMatch`) -- the pure-CLR adaptor-forward shape that exercises
      exactly the indexer.

## Task 4: Regression gate

- [x] Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors); `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] NeoStep14: 19/0/0 (17 existing + 2 new probes PASS).
- [x] Full `NeoStep` smoke: 221/0/0 (was 219; +2 new probes; no regression).
- [x] Legacy-neutral: plain `Debug` build of `ILRuntime` = 0 errors.

## Task 5: Doc correction (paths #1/#2 already-works + path #3 sequenced)

- [ ] In `.trae/documents/neo-deferred-items.md`, F-4 / NEO-IL-EX-FIELDACCESS:
  - [ ] Mark path #1 STALE: the bridge WORKS on HEAD (no fix).
  - [ ] Mark path #2 STALE/NO-OP: GetType WORKS on HEAD (the `-96` was
        `Type.op_Equality`, a separate `ReadNeoReference` null-operand gap). No fix.
        Record the new finding: the Neo redirect map key-mismatch
        (Object-declared vs Exception-declared MethodInfo) blocks any future
        GetType redirect from firing -- relevant if GetType semantics ever need
        the IL projection instead of the raw Adapter type.
  - [ ] Mark path #4 RESOLVED (the Neo indexer `get`/`set` arms in
        `ILTypeInstance.cs`).
  - [ ] Mark path #3 SEQUENCED: follow-on child `neo-f4-parametrized-run-entry`
        (or STEP-25-PARTIAL S3), driven NEXT. `neo-async-movenext-fix` did NOT
        unblock it.
  - [ ] RECORD the new pre-existing gap discovered: (a) `Type.op_Equality` /
        `String.op_Equality` Neo bindings throw AoRE on a NULL operand
        (`ReadNeoReference` indexes mStack with the null sentinel) -- a general
        Neo gap affecting any `t == null` / `s == null` on reference types with a
        custom `==` overload; (b) `new MyEx(string)` ctor stores the ILTypeInstance
        (this) into the `Msg` field instead of the string param (a newobj-arg
        passing bug for IL-exception string-param ctors) -- see design.md section 8.

## Out of scope (sequenced -- follow-on children, design only here)

- **Path #3 parametrized `Run`** (`neo-f4-parametrized-run-entry`): marshal
  `instance` + `p` into the Neo frame in `ILIntepreter.Run` under
  `ENABLE_NEO_MODE`; extend `NeoBoxReturnValue` to the reference-return shape
  (F-12 / NEO-RUN-REF-RETURN). Gate: an IL instance-method override (e.g.
  `MyEx.Message`) invoked via `AppDomain.Invoke(get_Message, e)` returns the IL
  override's value, not NRE. The LEAD SHALL drive this child next (TRUE-COMPLETION:
  not parked).
- **op_Equality null-operand gap**: the autogen `Type.op_Equality` /
  `String.op_Equality` Neo bindings (`ReadNeoReference`) AoRE on a null operand.
  Separate change; surfaces in any `== null` on these types under Neo.
- **newobj string-arg to IL-exception ctor**: `new MyEx("...")` stores `this` into
  the string field instead of the arg. Separate change; see design.md section 8.
