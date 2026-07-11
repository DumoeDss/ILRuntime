## Context

`GetNeoILInstance(AutoList mStack, int objIndex)` (`ILIntepreter.Neo.cs:6070`)
is the `[AggressiveInlining]` guard that every typed Neo field/address arm calls
to resolve the owner `ILTypeInstance`:

- typed load arms `Ldfld_I1..Ldfld_R8` + `Ldfld_Ref` (read `ins.Primitives` /
  `ins.ManagedObjects`);
- typed store arms `Stfld_I1..Stfld_R8` + `Stfld_Ref`;
- whole-IL-VT field arms `Stfld_Value` / `Ldfld_Value`;
- the `Stobj` / `Ldobj` IL-instance fallback (the `else` after the `NeoIsClrObject`
  guard).

The pre-existing body was: `objIndex < 0` -> NRE; else `mStack[objIndex] as
ILTypeInstance`; if null -> `throw NotImplementedException("Step 17/13b ...")`.
So **any** non-`ILTypeInstance` owner (including null) hit a single "deferred"
NIE. The typed arms are JIT-emitted ONLY for an IL-declaring field
(`JITCompiler.cs:2789/2860` `if (type is ILType)`), so a CLR-declaring field
never reaches here (it takes the raw `Ldfld`/`Stfld` opcode handled by the
CLR field-hash path, child 4). The byref consumers (`Stind_*`/`Ldind_*`/
`Stobj`/`Ldobj`) already guard with `NeoIsClrObject` BEFORE the fallback, so a
genuine raw CLR object does not reach `GetNeoILInstance` either.

**Full-smoke diagnosis (5 hits, instrumented the guard to dump the owner):**

| test | arm (line) | owner shape |
|------|------------|-------------|
| `DelegateExtObjMethod.IntTest` | Ldfld_I4 (3799) | delegate `this` (Step 19) |
| `JsonTest2` | Ldfld_Ref (3823) | `TestClass3Adaptor+Adaptor` (CrossBindingAdaptorType) |
| `RegisterVMTest04` | Stfld (3980) | null |
| `TestStaticFieldInstance` | Ldfld_Ref (3823) | null |
| `StructTest14` | Stfld_Value (4028) | null |

So the owners are **null** (3x) or a **CrossBindingAdaptor wrapper** (1x) or a
delegate artifact (1x) -- never a raw CLR object. The task's "route a CLR object
to the Area-4d field-hash accessor" hypothesis is disproved for all 5.

`TestClass3Adaptor+Adaptor` (`ILRuntimeTestBase/Adapters/TestClass3Adaptor.cs`)
is `internal class Adaptor : TestClass3, CrossBindingAdaptorType` with a real
`ILTypeInstance ILInstance` property -- so the adaptor unwrap is well-defined
and mirrors child 9's raw-handler pattern (`target as ILTypeInstance ??
((CrossBindingAdaptorType)target).ILInstance`).

## Goals / Non-Goals

**Goals:**
- Make `GetNeoILInstance` CLR-faithful: null owner -> `NullReferenceException`;
  `CrossBindingAdaptorType` owner -> `.ILInstance`.
- Eliminate the misleading `"Step 17/13b deferred"` NIE for the null + adaptor
  shapes (the null case is a plain NRE; the adaptor case is real field access
  on the unwrapped instance).
- A NeoStep probe that FAULTs on HEAD and PASSES after the fix.

**Non-Goals (out of scope -- separate follow-ups):**
- The lazy-init `if (x == null)` null-comparison gap (see Risks): the `ceq` of
  an ldsfld-loaded null ref against `ldnull` misfires, so `brfalse` jumps the
  wrong way and the `newobj; stsfld` block is skipped. Root cause of the null
  owner in `TestStaticFieldInstance` / `RegisterVMTest04`. This is the
  "raw-brtrue-on-reference gap in the delegate-cache pattern" child 3
  explicitly deferred. NOT chased here.
- IL value-type `newobj` `this` ([VT-THIS-ADDR]) -- root cause of the null owner
  in `StructTest14` (`new NestedStruct(...)` -> ctor `this` is null).
- The `DelegateExt` hit (Step 19 delegates).
- Changing the byref consumers or the raw `Ldfld`/`Stfld` handlers (they already
  guard/route correctly).
- Lowering `Stsfld`/`Ldsfld` via `LowerNeoOffsets` (the IL-static offset
  resolution gap -- see Risks -- is real but unverifiable here because the
  brtrue-on-reference gap blocks the round-trip first; left untouched).

## Decisions

1. **Fix the guard, not the callers.** All three owner shapes funnel through
   `GetNeoILInstance`, so discriminating there is the minimal blast-radius fix.
   No JIT / optimizer / object-model change; no new helper. The defensive NIE is
   retained for genuinely-unexpected CLR shapes (fail-loud).

2. **Null -> NRE, not NIE.** `ldfld`/`stfld`/`ldobj`/`stobj` on a null instance
   is a `NullReferenceException` in the CLR. The previous NIE masked an upstream
   materialization gap as a "deferred feature". NRE is both CLR-correct and
   points future debugging at the real upstream cause.

3. **Adaptor -> `.ILInstance`.** The typed arm was emitted because the field's
   declaring type is an ILType, so the field lives on the `ILTypeInstance`. An
   adaptor wrapper (IL type that inherits a CLR base, round-tripped through CLR
   code / reflection / a generic collection) holds exactly that instance via
   `CrossBindingAdaptorType.ILInstance`. Unwrap and proceed -- byte-identical in
   intent to child 9's raw-handler unwrap. No writeback concern (the field is on
   the IL instance, not the CLR base).

4. **Probe = null-owner `ldfld` wrapped in `catch(NullReferenceException)`.**
   The constructible, deterministic trigger is an uninitialized IL static
   reference field: the Neo `ldsfld` IL-static-ref arm materializes the stored
   null as a **valid mStack index pointing at a null entry** (it does
   `mStack.Add(null)` and writes the index), so the owner register reaches the
   guard with `objIndex >= 0` and `mStack[objIndex] == null`. On HEAD the typed
   `ldfld` throws the `"Step 17/13b"` NIE, which is NOT an NRE, so the catch
   filter misses and the NIE propagates (FAULT). After the fix it throws NRE,
   the catch matches, and the probe returns (PASS). Verified empirically:
   WITH-fix probe exit 0; HEAD-behavior probe exit 127 (NIE propagates). The
   adaptor case is validated by code-inspection parity with child 9 + the
   `JsonTest2` progression (the adaptor unwrap lets it advance past the former
   NIE site).

## Risks / Trade-offs

- **Most of the 5 full-smoke tests do NOT go green from this change alone.**
  3 are blocked upstream (brtrue-on-reference lazy-init gap; [VT-THIS-ADDR]
  struct-newobj `this`) and 1 is Step 19 (delegates). Only the adaptor shape
  (`JsonTest2`) is directly unblocked at THIS site (it still has further
  downstream NIEs from the broader Neo reflection surface). The honest win is:
  the guard is CLR-faithful, the misleading "deferred" NIE is gone for null +
  adaptor, and the tagged-NIE occurrence count drops. The LEAD should treat the
  null/struct/delegate follow-ups as separate children.
- **`catch(NullReferenceException)` relies on Neo EH type-filtering** (Step 14
  + [CATCH-COMPLETE]). Verified working for both NRE (caught) and NIE
  (not-matched -> propagates) in the probe run. If a future EH regression
  weakens reference-type catch filters, the probe's FAULT signal degrades -- but
  that would itself be a visible NeoStep regression.
- **Adaptor unwrap assumes `ILInstance` is non-null for a real field access.**
  If a malformed adaptor with a null `ILInstance` reaches a typed field arm, the
  guard throws NRE (correct -- a null underlying instance is a null deref).
- **The IL-static `Stsfld`/`Ldsfld` offset gap is real but left unfixed.** The
  arms use the raw `ip->DstOffset` (register index) as a frame byte offset
  (the code comment at `ILIntepreter.Neo.cs:4180-4194` documents this; the
  CLR-static arms + `Ldsflda` were fixed via `localInfos`, the IL-static arms
  were not). A runtime `localInfos` resolution (mirroring the CLR-static arms)
  is the likely fix, but it is NOT verifiable while the brtrue-on-reference gap
  blocks the round-trip, and lowering it globally risks the delegate-cache gap
  child 3 warned about. Documented; not touched.
