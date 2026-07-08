## Why

Once an IL-defined exception can be THROWN and CAUGHT end-to-end on Neo
(`neo-il-exception-throw` shipped that — a built-in `System.Exception`
`CrossBindingAdaptor` plus the IL-instance unwrap on `Throw`), reading
IL-declared fields/methods off the CAUGHT object via the standard reflection /
cross-binding bridges turned out to be broken. F-4 / NEO-IL-EX-FIELDACCESS is
the TRUE-COMPLETION follow-up: four distinct reflection-read paths were
characterised in `.trae/documents/neo-deferred-items.md` F-4 section 3. This
change dump-gates each path on HEAD `b0041e74`, SHIPs the tractable ones, and
SEQUENCEs the one that is a large machinery change into a follow-on child that
MUST be driven next (not parked).

## What Changes

The dump-gate (probe-then-revert on HEAD `b0041e74`, see `design.md`) found that
only TWO of the four paths are actually broken in a tractable way; one is already
fixed and one is a large prerequisite. Concretely:

- **PATH #1 already works on HEAD (no-op, document the correction).** The
  `((CrossBindingAdaptorType)e).ILInstance` callvirt-on-CLR-interface bridge
  (the standard way to recover the IL view of the caught `Adapter`) RETURNS A
  NON-NULL `ILTypeInstance` without throwing on HEAD. The F-4 section 3 entry
  that claims it throws `InvalidCastException` is STALE (an intervening change
  -- the Step 19/20 cross-binding + callvirt-CLR dispatch work -- closed it). No
  source fix; the deferred-items doc is corrected.
- **PATH #2 SHIP: `e.GetType()` -- add the Neo `Object.GetType` redirect.** On
  HEAD a `callvirt.clr` on `Object.GetType` against the caught object throws
  `ArgumentOutOfRangeException` (probe-confirmed). Root cause: `Object.GetType`
  has a Legacy redirect (`CLRRedirections.ObjectGetType`) registered in the
  `AppDomain` ctor, but NO Neo `RedirectionNeo`, so `InvokeNeoClrMethod` falls
  through to `clrMethod.Invoke` which mis-reads the reference `this`. A Neo
  redirect (`ObjectGetType_Neo`, mirroring the Legacy one -- ILTypeInstance ->
  `Type.ReflectionType`, else CLR `GetType`) SHALL be added and registered.
- **PATH #3 SEQUENCE: `appdomain.Invoke(instanceMethod, e)` -- the parametrized
  `Run` entry prerequisite.** On HEAD the public `ILIntepreter.Run(method,
  instance, p)` shim IGNORES BOTH `instance` AND `p` under `ENABLE_NEO_MODE`
  (`ILIntepreter.cs:104-137`, the Step-6 parameterless-only entry), so an IL
  instance-method override invoked via `AppDomain.Invoke` has no `this` -> NRE.
  This is the parametrized-Run machinery already recorded on STEP-25-PARTIAL /
  F-12; `neo-async-movenext-fix` did NOT unblock it (it routes through
  `DriveMoveNextCore` + fresh interpreters calling `ExecuteNeo` directly, NOT
  the public `Run`). This is a LARGE change and is sequenced into a follow-on
  child that the LEAD MUST drive next.
- **PATH #4 SHIP: `ILTypeInstance.this[index]` indexer -- add the Neo accessor.**
  On HEAD the indexer `get` arm returns `null` unconditionally under
  `ENABLE_NEO_MODE` (`ILTypeInstance.cs:398-400`); the Legacy arm reads the
  `StackObject[] fields` array. A Neo `get` arm SHALL read the field from
  `byte[] Primitives` + `AutoList ManagedObjects` via `type.GetFieldOffset(index)`
  + `type.GetField(index, ...)` (primitive / enum / reference / CLR-struct-field
  shapes; IL-value-type-field reconstruction is an accepted-known edge). The
  `set` arm is mirrored for symmetry.

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `neo-exceptions`: Add requirements that reading the type of, and IL-declared
  fields off, a CAUGHT IL exception via the standard bridges (CLR `Object.GetType`
  redirect; `ILTypeInstance` field indexer) works under `ENABLE_NEO_MODE`. Also
  records the path #1 already-works correction and the path #3 parametrized-Run
  deferral (a follow-on child).

## Impact

- **Engine (Neo-only, `#if ENABLE_NEO_MODE` or Neo-only files):**
  `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` (path #2 -- the
  `ObjectGetType_Neo` redirect delegate, Neo-gated, alongside the Legacy
  `ObjectGetType`); `ILRuntime/Runtime/Enviorment/AppDomain.cs` (path #2 -- the
  `RegisterCLRMethodRedirectionNeo` call next to the existing Legacy
  `ObjectGetType` registration at ~line 222);
  `ILRuntime/Runtime/Intepreter/ILTypeInstance.cs` (path #4 -- the Neo `get`/`set`
  arms of the `this[int index]` indexer at ~line 375-448, replacing `return null`).
- **Tests:** `TestCases/NeoStep14Test.cs` -- adversarial probes per shipped path
  (a caught IL exception whose type / field is read via the bridge -> the correct
  value, not null / exception). Path #4's indexer is a CLR-side accessor exercised
  by a bound helper (the standard generated-adaptor forward path), since the
  indexer is not directly reachable from interpreted IL.
- **Docs:** `.trae/documents/neo-deferred-items.md` -- F-4 section 3: mark path #1
  STALE (already fixed), path #3 SEQUENCED to the parametrized-Run follow-on.
- **Regression risk:** LOW. Both shipped fixes are Neo-only (Legacy byte-identical;
  the Legacy `ObjectGetType` redirect and the Legacy indexer arm are untouched).
  Path #2 risk = the redirect mis-reading a non-Exception receiver -- mitigated by
  mirroring the Legacy redirect (ILTypeInstance vs CLR discrimination). Path #4
  risk = field-type discrimination -- mitigated by gating on
  `type.TotalFieldCount` (the exact analogue of the Legacy `fields.Length` gate)
  and keeping the existing `FirstCLRBaseType` CLR-inherited fallback. The full
  `NeoStep` smoke (219/0/0) is the regression gate; the adversarial probes are the
  correctness gate (a green smoke does NOT prove the fix).
