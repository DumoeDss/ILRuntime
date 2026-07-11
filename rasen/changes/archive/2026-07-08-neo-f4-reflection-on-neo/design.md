# Design - neo-f4-reflection-on-neo

> Scope-AWARE dump-gate for F-4 / NEO-IL-EX-FIELDACCESS. Each of the 4 broken
> reflection-read paths was probed on HEAD `b0041e74` (probe-then-revert; the
> probes were removed before any artifact shipped). The dump decides SHIP vs
> SEQUENCE per path. Legacy is the REFERENCE; every shipped edit is Neo-only.

## 0. Dump-gate verdict matrix

| # | Path | HEAD behavior (probe) | Verdict | Fix site |
|---|------|-----------------------|---------|----------|
| 1 | `((CrossBindingAdaptorType)e).ILInstance` callvirt-on-CLR-interface | WORKS (returned 9; non-null ILTypeInstance, no exception) | NO-OP (already fixed; doc STALE) | none |
| 2 | `e.GetType()` callvirt.clr `Object.GetType` | `ArgumentOutOfRangeException` (probe -96) | SHIP | `CLRRedirections.cs` + `AppDomain.cs:222` |
| 3 | `appdomain.Invoke(instanceMethod, e)` instance re-entry | NRE (Run ignores `instance`+`p`) | SEQUENCE (parametrized-Run; LARGE) | `ILIntepreter.cs:104-137` (follow-on child) |
| 4 | `ILTypeInstance.this[index]` indexer | `null` (`return null` at `:398-400`) | SHIP | `ILTypeInstance.cs:375-448` |

## 1. Path #1 -- ALREADY WORKS on HEAD (no-op; doc correction)

**Probe (`F4_P1_ILInstanceBridge`, probe-then-revert):**
```
catch (MyEx e) { var ili = ((CrossBindingAdaptorType)e).ILInstance; return ili != null ? 9 : -10; }
```
Result on HEAD `b0041e74`: **Return:9** -- the callvirt on the CLR interface
`CrossBindingAdaptorType::get_ILInstance` against the caught `Adapter` receiver
returns a non-null `ILTypeInstance` WITHOUT throwing.

**Dispatch trace (why it works):** the callvirt resolves through
`ResolveNeoGenericCallvirtTarget` (`ILIntepreter.Neo.cs:809`): the receiver is the
`Adapter` (a CLR object, NOT an `ILTypeInstance`), and `get_ILInstance` resolves
to a `CLRMethod`, so the `declaredMethod is CLRMethod` branch returns it;
`InvokeNeoCallTarget` -> `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:634`) finds no
`RedirectionNeo` and calls `clrMethod.Invoke(targetBase, mStack, false)`, whose
autogen wrapper reads the reference `this` (the `Adapter`) and calls the real
`Adapter.ILInstance` getter, returning the wrapped `ILTypeInstance`.

**Conclusion:** the F-4 section 3 claim that this path throws
`InvalidCastException` is STALE. An intervening change (the Step 19 delegate /
Step 20 cross-binding + the callvirt-CLR dispatch maturation) closed it. **No
source fix.** The deferred-items doc SHALL be corrected so a future worker does
not re-investigate. (The probe also confirmed a direct `(MyEx)ili cast is
rejected by the C# compiler -- the two are unrelated CLR types -- which is WHY
field-read off a recovered ILTypeInstance goes through the indexer, path #4, not
a cast+ldfld.)

## 2. Path #2 -- SHIP: Neo `Object.GetType` redirect

**Probe (`F4_P2_GetType`):**
```
catch (MyEx e) { try { var t = e.GetType(); return t != null ? 9 : -10; } catch (Exception ex) { return ExCode(ex); } }
```
Result on HEAD: **Return:-96 = `ArgumentOutOfRangeException`** (NOT a
`NotImplementedException` as the deferred-items doc guessed; the doc's
characterisation of the symptom was also imprecise).

**Root cause:** `Object.GetType` has a Legacy redirect registered in the AppDomain
ctor (`AppDomain.cs:222`, `RegisterCLRMethodRedirection(mi,
CLRRedirections.ObjectGetType)`; the Legacy delegate is `CLRRedirections.cs:1109`),
but NO Neo `RedirectionNeo`. Neo redirects live in a SEPARATE map
(`RedirectMapNeo` via `RegisterCLRMethodRedirectionNeo`; `CLRMethod.cs:145-155`),
so a Legacy redirect does NOT auto-populate the Neo path. With `RedirectionNeo ==
null`, `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:636-645`) falls through to
`clrMethod.Invoke(targetBase, mStack, false)`, whose autogen reader mis-reads the
reference `this` (the classic Neo "primitive value read as an mStack index"
symptom -- the same family as K2 / F-3), yielding the `ArgumentOutOfRangeException`.

**Fix design (Neo-only, focused):** add a `CLRRedirectionDelegateNeo
ObjectGetType_Neo` alongside the Legacy `ObjectGetType` in `CLRRedirections.cs`,
mirroring the Legacy semantics (`CLRRedirections.cs:1109-1124`):
- Read the instance `this` (a reference; slot 0 of `targetBase`): `int thisIdx =
  *(int*)frameBase; object inst = (thisIdx >= 0 && thisIdx < mStack.Count) ?
  mStack[thisIdx] : null;` (the reference-`this` read pattern used by
  `Task_T_GetAwaiter_Neo`, `CLRRedirections.AsyncNeo.cs:721-723`).
- Compute the `Type`: if `inst is ILTypeInstance ili` -> `ili.Type.ReflectionType`
  (the Legacy arm at `:1118-1120`); else `inst.GetType()` (null `inst` -> throw
  NRE, matching Legacy's implicit behavior).
- Write the reference return via the shared `WriteReturnByType` helper (or the
  direct reference-store pattern at `ILIntepreter.Neo.cs:720-727`): `mStack[retRefBase]
  = typeResult; *(int*)retDst = retRefBase;` (null -> `*(int*)retDst = -1`, the
  neo-array-multidim Gap 1 null convention at `:716-718`).
- Register in the AppDomain ctor next to `:222`: `RegisterCLRMethodRedirectionNeo(mi,
  CLRRedirections.ObjectGetType_Neo)` (same `mi` already resolved for the Legacy
  registration).

**Adversarial probe (the gate; a green smoke does NOT prove it):**
`NeoStep14_ILEx_GetType` -- throw `new MyEx(...)`, catch, call `e.GetType()`, assert
the returned `Type` is non-null AND `typeof(MyEx).IsAssignableFrom(t)` (the IL type
identity is recoverable through the bridge). On HEAD this throws
`ArgumentOutOfRangeException`; after the fix it returns the IL type's CLR
projection. Stash-toggle: with the redirect unregistered, the probe FAILs (the
exception); with it registered, PASS.

## 3. Path #3 -- SEQUENCE: parametrized `Run` entry (LARGE; follow-on child)

**Code evidence (conclusive; not IL-probable without a bound `AppDomain.Invoke`):**
the public re-entry path is `AppDomain.Invoke(m, instance, p)` (`AppDomain.cs:1666`)
-> `inteptreter.Run((ILMethod)m, instance, p)` (`AppDomain.cs:1674`). Under
`ENABLE_NEO_MODE` the `Run` shim (`ILIntepreter.cs:104-137`) sets up the Neo frame
and calls `ExecuteNeo(method, neoFrame, retDst, retRefBase, ...)` but NEVER marshals
`instance` (no slot-0 `this` push into `mStack`) and NEVER marshals `p` (no
param-region population). The comment at `:112-113` states it explicitly: "Step 6
entry shim: only no-arg static methods are expected here." So an IL instance method
invoked through `AppDomain.Invoke` runs with no `this` -> NRE on the first field
access.

**Why SEQUENCE (not SHIP):** a correct fix is the parametrized-Run machinery --
allocate the callee param region, `WriteNeoCallSlot` each `p` element by
parameter type, push `instance` as the slot-0 `this` reference, allocate the full
frame ref region, then `ExecuteNeo`. That is a non-trivial ABI extension (it must
agree with `CopyNeoCallArguments` / `NeoCallParamMap` and the return-side
`NeoBoxReturnValue` -- which itself only handles primitive returns per F-12 / the
STEP-25-PARTIAL note). This is exactly the "fuller Run entry" prerequisite already
recorded on STEP-25-PARTIAL and F-12 (`neo-deferred-items.md` rows STEP-25-PARTIAL
+ F-12 / NEO-RUN-REF-RETURN).

**`neo-async-movenext-fix` did NOT unblock it:** that change routes truly-async
resumption through `DriveMoveNextCore` + a FRESH pooled interpreter calling
`ExecuteNeo` directly with the state machine seeded as slot-0
(`ILAsyncContext<T>.MoveNext`), NOT through the public `Run(method, instance, p)`.
So `Run` is still parameterless-only on HEAD.

**Follow-on child (TRUE-COMPLETION -- MUST be driven next, not parked):**
`neo-f4-parametrized-run-entry` (or fold into the STEP-25-PARTIAL S3
parametrized-Run prerequisite). Scope: marshal `instance` + `p` into the Neo frame
in `ILIntepreter.Run` under `ENABLE_NEO_MODE`; extend `NeoBoxReturnValue` to the
reference-return shape (F-12). Gate: an IL instance-method override (e.g.
`MyEx.Message`) invoked via `AppDomain.Invoke(get_Message, e)` returns the IL
override's value, not NRE.

## 4. Path #4 -- SHIP: `ILTypeInstance` Neo field indexer

**Code evidence (conclusive):** the indexer `this[int index].get` under
`ENABLE_NEO_MODE` is `return null;` (`ILTypeInstance.cs:398-400`). The Legacy arm
(`:380-397`) reads `StackObject[] fields` (sized to `type.TotalFieldCount` at
construction, `:342`) via `StackObject.ToObject`, with a `FirstCLRBaseType`
CLR-inherited fallback (`:390-394`). The Neo instance stores fields as
`byte[] Primitives` (`fields`, `:334`, sized to `type.TotalPrimitiveSize`) +
`AutoList ManagedObjects` (`managedObjs`, `:337`, sized to
`type.TotalReferenceCount`). The indexer is exercised by generated
cross-binding-adaptor property forwarders and by CLR-side reflection (a direct
`(MyEx)ili` cast is impossible -- unrelated CLR types -- so field read-off a
recovered ILTypeInstance IS the indexer).

**Fix design (Neo-only, focused; get arm is load-bearing, set arm mirrored):**
Replace `return null;` with a Neo `get` arm that:
1. Gates IL-field vs CLR-inherited by `index >= 0 && index < type.TotalFieldCount`
   (the exact analogue of the Legacy `index < fields.Length` gate; under Neo
   `fields.Length` is the primitive BYTE count, NOT the field count -- the current
   `#else return null` exists precisely because the byte-length gate would be
   wrong). Out-of-range -> the existing `FirstCLRBaseType` CLR-inherited branch
   (`clrType.GetFieldValue(index, clrInstance)`), byte-identical to Legacy.
2. For an IL field: `ILTypeFieldOffset off = type.GetFieldOffset(index);`
   (`ILType.cs:2102`, recurses through the IL base-type chain) and
   `IType ft = type.GetField(index, out FieldReference _);` (`ILType.cs:2138`,
   handles inheritance). Then dispatch on `ft.TypeForCLR`:
   - **Primitive:** read `fields[off.PrimitiveOffset]` by the primitive width
     (mirror the `Ldfld_*` arms at `ILIntepreter.Neo.cs:2753-2792`) and box.
   - **Enum:** read the underlying-primitive width and box as the enum.
   - **Reference (ILType / CLR ref / string):** return
     `managedObjs != null ? managedObjs[off.ReferenceOffset] : null`.
   - **CLR-struct field of an IL instance (F-10 shape):** the struct is stored
     boxed at `managedObjs[off.ReferenceOffset]`; return it (one line -- the F-10
     layout, `ILType.cs:2129-2157` + the `neo-clrstruct-field-of-il` resolution).
   - **IL value-type field:** accepted-known edge -- throw a TAGGED
     `NotImplementedException` ("Neo ILTypeInstance indexer: IL-value-type field
     reconstruction not supported") rather than return wrong data. (Rare for
     exception types; the caught-exception TRUE-COMPLETION shape is
     primitive/string fields.)

**`set` arm (mirror):** write primitive bytes / enum / reference / CLR-struct box
to the same offsets; IL-value-type field -> tagged NIE. The Legacy `set` arm's
`CheckAndCloneValueType` prelude (`:405`) is retained (engine-agnostic). The
existing `FirstCLRBaseType` CLR-inherited `set` fallback (`:438-441`) is reused.

**Adversarial probe (the gate):** `NeoStep14_ILEx_IndexerFieldRead` -- throw
`new MyEx("idx-msg")`, catch, recover `ili = ((CrossBindingAdaptorType)e).ILInstance`
(path #1, already works), then read the IL-declared `Msg` field through the indexer
via a bound CLR helper (`ili[fieldIndex]`, where `fieldIndex` comes from
`ili.Type.GetField("Msg", out _)`). Assert the value equals `"idx-msg"`, not null.
On HEAD the indexer returns null -> the assertion FAILs; after the fix it PASSes.
Stash-toggle: `#else return null;` restored -> FAIL; Neo arm -> PASS. (The helper
lives in `ILRuntimeTestBase` and is bound so the interpreter can call it; this is
the standard generated-adaptor forward path.)

## 5. Why SHIP #2+#4 and SEQUENCE #3 (scope-aware)

- The two SHIP fixes are SELF-CONTAINED, Neo-only, and each closes one fully
  reachable reflection-read path for the caught-exception shape with a focused
  edit (one redirect + one accessor). Their combined regression surface is small
  (Legacy byte-identical; the full `NeoStep` smoke is the gate).
- Path #3 is a genuine ABI extension (parametrized `Run` + reference return) that
  is its own substantial change and is already a named prerequisite
  (STEP-25-PARTIAL / F-12). Bundling it would make the diff unreviewable and
  conflate two risk profiles. TRUE-COMPLETION is honoured by sequencing it into a
  follow-on child the LEAD drives NEXT (not parked).
- Path #1 needs no work; documenting the staleness is itself a durable correction
  (a future worker would otherwise re-investigate a non-bug).

## 6. Verification plan

- Path #2 + #4: adversarial probes FAIL-on-HEAD -> PASS-after (stash-toggle
  confirmed per path). Full `NeoStep` smoke stays 219/0/0 (no regression).
  Legacy plain-`Debug` build = 0 errors (Legacy-neutral).
- Path #1: the correction is doc-only; no probe ships (the existing
  `NeoStep14_ILEx_MessageField` already covers the `e is MyEx` isinst shape).
- Path #3: recorded as the follow-on child; out of this change's implementation
  scope (design only).

## 7. APPLY-TIME CORRECTION (the dump-gate re-derived on HEAD with confounds removed)

The original dump (section 0) reported path #2 as SHIP with HEAD behavior
`-96 = ArgumentOutOfRangeException`, attributing it to `Object.GetType`. That
characterisation was **WRONG** -- a probe-confound, re-derived during apply:

- The original `F4_P2_GetType` probe body was
  `var t = e.GetType(); return t != null ? 9 : -10;`. The `t != null` check on a
  `System.Type` local lowers to `Type.op_Equality(t, null)` (System.Type
  OVERLOADS `==`). The autogen `System_Type_Binding.op_Equality_1_Neo`
  (`ILRuntimeTestBase/AutoGenerate/System_Type_Binding.cs:207-215`) reads BOTH
  operands via `ILIntepreter.ReadNeoReference`; a NULL operand is encoded as the
  Neo null sentinel (-1), and `ReadNeoReference` indexes `mStack[-1]` ->
  `ArgumentOutOfRangeException`. The `-96` was the **op_Equality null-operand
  gap**, NOT GetType.

**Re-derived probe** (`NeoStep14_ILEx_GetType`, uses `ReferenceEquals(t, null)`
instead of `t == null`/`t != null` to sidestep the op_Equality path): on HEAD
`b0041e74`-equivalent, `e.GetType()` returns a valid non-null `Type` (the caught
`ExceptionAdaptor+Adapter` CLR type) -- the `InvokeNeoClrMethod` reflection
fallback (`clrMethod.Invoke`, `CLRMethod.cs:334`) reads the reference `this`
correctly (`:413`, `thisIdx < 0 ? null : mStack[thisIdx]`) and the reference
return store (`ILIntepreter.Neo.cs:727-753`) is correct.

**Revised verdict matrix:**

| # | Path | HEAD behavior (re-derived) | Verdict | Fix |
|---|------|----------------------------|---------|-----|
| 1 | bridge | WORKS | NO-OP (doc STALE) | none |
| 2 | `e.GetType()` | WORKS (returns Adapter CLR type) | **NO-OP (doc STALE)** -- was mis-diagnosed | none |
| 3 | parametrized Run | NRE | SEQUENCE | follow-on child |
| 4 | `ili[index]` | `null` (`return null`) | SHIP | `ILTypeInstance.cs` Neo get/set arms |

**Why a Neo `Object.GetType` redirect was prototyped then REMOVED:** a redirect
`ObjectGetType_Neo` (mirroring Legacy, using `Extensions.GetActualType` for the
IL-projection result) was added to `CLRRedirectionsAsyncNeo` + registered via
`RegisterCLRMethodRedirectionNeo` in the AppDomain ctor. It does NOT fire: the
Neo redirect map is keyed by `typeof(object).GetMethod("GetType")`
(DeclaringType=`System.Object`), but the JIT resolves `Object.GetType` on an IL
exception (CLR base `System.Exception`) to the `System.Exception`-declared
`MethodInfo`. .NET reflection returns DISTINCT objects with DISTINCT
`MethodHandle`s for these (`typeof(object).GetMethod("GetType") !=
typeof(Exception).GetMethod("GetType")` -- confirmed by dump:
`def==objGet? False def==exGet? True`), so `TryGetRedirection` (`CLRMethod.cs:111`)
misses and the fallback runs. Since the fallback ALREADY works, the redirect is
dead code; it was REMOVED to avoid implying a non-existent fix. (The MethodInfo
key-mismatch is recorded for a future worker who wants GetType to return the IL
projection: they would need a normalized map key, not a plain registration.)

## 8. New pre-existing gaps discovered during apply (recorded; SEQUENCED)

1. **op_Equality null-operand gap (general Neo):** the autogen Neo bindings for
   `Type.op_Equality` (`System_Type_Binding.op_Equality_1_Neo`) and
   `String.op_Equality` (`System_String_Binding.op_Equality_19_Neo`) call
   `ILIntepreter.ReadNeoReference` for BOTH operands; a NULL operand (Neo null
   sentinel -1) indexes `mStack[-1]` -> `ArgumentOutOfRangeException`. Affects any
   `t == null` / `s == null` / `t == other` with a null operand on these types
   under Neo. Surface in the F-4 probes (the original mis-diagnosis); the probes
   sidestep it via `ReferenceEquals` / pure-CLR comparison in the host helper.
   Separate change to fix `ReadNeoReference`'s null handling (or the bindings).

2. **newobj string-arg to IL-exception ctor:** `new MyEx("idx-msg")` stores the
   ILTypeInstance (`this`) into the `Msg` field instead of the string arg. Dump:
   the `MyEx..ctor(string)` JIT is correct (`1:stfld.ref r0(this), r1(msg),
   MyEx(0,0)`), but the runtime `Stfld_Ref` arm reads `mStack[srcIdx]` where the
   src register holds a reference to the ILTypeInstance, not the string -- i.e.
   the newobj->ctor argument passing mis-routes the string param for an IL
   exception (CLR-adaptor-base) type. The `NeoStep14_ILEx_IndexerFieldRead` probe
   works around it by constructing via the default ctor + a direct field
   assignment (`toThrow.Msg = "idx-msg"`), which uses the standard stfld.ref
   path correctly. Separate change to fix the newobj-arg passing for
   CLR-adaptor-base IL types.

