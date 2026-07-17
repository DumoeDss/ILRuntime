# Review Report — `neo-clr-object-field-il-path`

**Reviewer:** author != verifier gate (dispatched leaf reviewer, rasen:review)
**Branch:** `features/object-model-overhaul`
**Change:** child 10 of `neo-overhaul`. Reworks `GetNeoILInstance`
(`ILIntepreter.Neo.cs:6071`) + adds 2 probes (`TestCases/NeoStepClrObjIlPathTest.cs`).
**Mode:** dispatched (report-only) — no edits, no commits, no subagents.

---

## TL;DR

**VERDICT: APPROVE.** The rework is sound, CLR-faithful, minimal (~25 lines, no
JIT/optimizer/object-model change), and every claim survives independent
re-verification. The scope correction is accurate and honestly documented: the
guard is fixed and the misleading "deferred" NIE is eliminated for the null +
adaptor shapes, but most of the 5 full-smoke tests do NOT go green from this
alone (deeper `brtrue`-on-reference / `[VT-THIS-ADDR]` / Step-19 causes) — which
is the intended, documented scope. No Blocker, no Major findings.

---

## Re-verification results

### 1. `GetNeoILInstance`-rework soundness — **SOUND**

Diff read (`ILIntepreter.Neo.cs:6070-6105`). The new body discriminates:

1. `objIndex < 0` -> `NullReferenceException` (unchanged).
2. `o == null` -> `NullReferenceException`. **Correct.** `ldfld`/`stfld`/`ldobj`/
   `stobj` on a null instance is an NRE in the CLR; the previous "Step 17/13b
   deferred" NIE masked an upstream materialization gap as a missing feature.
3. `o is ILTypeInstance ins` -> `return ins` (unchanged fast path).
4. `o is CrossBindingAdaptorType cba` -> null `ILInstance` throws NRE, else
   `return cba.ILInstance`. **Correct.** The typed arm was JIT-emitted ONLY for
   an IL-declaring field (verified: `JITCompiler.cs:2789` `if (type is ILType)`),
   so the field lives on `ILTypeInstance.Primitives`/`ManagedObjects`; the
   adaptor wrapper holds exactly that instance. This is distinct from child-9's
   **raw** handler (`:3758`), which reads a CLR-base field via `il.CLRInstance`
   — different field location, correctly different access. Parity is intent-level
   (both unwrap), not byte-level, and that is right.
5. else -> defensive Step-tagged NIE (retained; now also appends
   `o.GetType().FullName` for diagnostics — minor improvement).

**No caller is broken by null now throwing NRE instead of NIE.** All call sites
are instance field/ref dereferences (typed `Ldfld_*`/`Stfld_*`/`Stfld_Value`/
`Ldfld_Value` heap arms + `Stobj`/`Ldobj`/`Stind_*`/`Ldind_*` byref arms). A null
receiver at every one of these is an NRE in the CLR. No site treats a null owner
as a valid "no-owner" case (that pattern is `objIndex < 0`, handled separately and
unchanged). The previous NIE was never "handled" — it always crashed — so changing
NIE->NRE cannot regress any caller; it only makes the exception type correct.

### 2. Byref-consumers-already-route claim — **CONFIRMED**

Spot-checked every `GetNeoILInstance` byref caller. Each guards CLR objects out
BEFORE the fallback:

- `Stind_I1..Stind_R8` (`:4994-4997` etc.): chain is `objIdx==-1` (frame-native)
  -> `mStack[objIdx] is Array` (array.SetValue) -> **`NeoIsClrObject` ->
  `NeoWriteClrObjectField`** -> `else { GetNeoILInstance(...) }`.
- `Stobj` (`:5364`): `else if (NeoIsClrObject(mStack, objIdx)) ->
  NeoWriteClrObjectField;` BEFORE the `else` that calls `GetNeoILInstance` (`:5375`).
- `Ldobj` (`:5463`): `else if (NeoIsClrObject(...)) -> NeoReadClrObjectField;`
  BEFORE its `else`.

`NeoIsClrObject` (`:6146-6154`) returns **false for null** (`if (o == null)
return false;`) and true only for non-ILTypeInstance, non-Array objects. So:
- a genuine raw CLR object is routed to the field-hash accessor and NEVER reaches
  the guard's `else` NIE; the retained NIE is genuinely only reachable by
  null / adaptor / genuinely-unexpected shapes.
- a null owner falls through every guard and reaches `GetNeoILInstance` (now NRE).
- a `CrossBindingAdaptorType` owner at a byref consumer returns true from
  `NeoIsClrObject` (not ILTypeInstance, not Array) -> routed to the field-hash
  path, so the adaptor-unwrap arm in the guard is reachable ONLY via the typed
  `Ldfld_*`/`Stfld_*` arms (which have no `NeoIsClrObject` guard, by JIT design).
  Consistent.

This validates the fix is **sufficient** — the defensive NIE is not masking a
real CLR-object path that still needs plumbing.

### 3. Probe validity — **VALID / non-spurious**

Both probes (`NeoStepClrObjIl_TC1_NullOwnerLdfldNre`, `_TC2_NullOwnerLdfldRefNre`)
wrap a typed `ldfld` on the uninitialized `NeoStepClrObjIlHolder.Lazy` (null IL
static ref field) in `try { ldfld; int _ = 1/0; } catch (NullReferenceException) {}`.

The probe can pass IFF exactly NRE is thrown:
- HEAD: `ldfld` throws the "Step 17/13b" NIE -> `catch(NRE)` misses -> propagates
  -> FAIL (verified, see §4).
- Fix: `ldfld` throws NRE -> caught -> returns normally -> PASS.
- Any OTHER exception or no-exception: not caught (or the deliberate `1/0`
  divide-by-zero fault trigger fires, also uncaught by `catch(NRE)`) -> FAIL.

The `1/0` fault trigger is a sound defensive measure against a silent "no throw"
regression. The probe is discriminating and cannot pass spuriously. Empirically
confirmed by the stash-toggle (§4).

### 4. Stash-toggle (FAULT-on-HEAD / PASS-after) — **CONFIRMED**

- `git stash push -- ILIntepreter.Neo.cs` -> HEAD (NIE) guard restored (verified
  `"A CLR object reached an IL-instance field/address path"` comment back at
  `:6077`); rebuilt `Debug_Neo --no-incremental` (0 errors).
- HEAD guard run of `NeoStepClrObjIl`: **`Ran 2 tests, 2 failed`**. Failure cause
  is exactly the tagged NIE propagating (8 occurrences of the "field/element
  access on a CLR object via the IL-instance path is deferred" string in the log;
  2 `NotImplementedException: Step 17/13b ...` stack traces) — the `catch(NRE)`
  filter misses it, so it propagates uncaught. This is the FAULT.
- `git stash pop` -> fix restored byte-exact (verified `"A null heap owner at a
  typed IL-instance field arm"` back at `:6077`; `Neo.cs` back to `M`).
- Fix run of `NeoStepClrObjIl`: **`Ran 2 tests, 0 failed`** (PASS).
- Working tree left exactly as found (`Neo.cs` M, test file untracked).

### 5. NeoStep smoke — **330 / 0**

`Debug_Neo` CLI + `true NeoStep`: **`Ran 330 tests, 0 failed`** (328 baseline +
2 new probes). No regression.

### 6. Full-smoke tagged-NIE count — **0** (was ~5)

Ran each of the 5 diagnosed tests under `Debug_Neo` and grepped combined output
for `"field/element access on a CLR object via the IL-instance path is deferred"`:

| test | NIE-string hits | result |
|------|----------------:|--------|
| `JsonTest2` | 0 | Ran 1, 1 failed (now `NullReferenceException`) |
| `RegisterVMTest04` | 0 | Ran 1, 1 failed (now `NullReferenceException`) |
| `TestStaticFieldInstance` | 0 | Ran 1, 1 failed (now `NullReferenceException`) |
| `StructTest14` | 0 | Ran 1, 1 failed (now `NullReferenceException`) |
| `DelegateExtObjMethod` | 0 | filter matched 0 tests (class vs method name) |

The 5 tagged-NIE occurrences are eliminated. The 4 reachable tests now throw
**`NullReferenceException`** (CLR-faithful) — they still FAIL because the NRE is
uncaught, which is exactly the documented scope: the null owner is the symptom of
the deeper `ceq`/`brfalse`-on-reference lazy-init gap (`TestStaticFieldInstance`/
`RegisterVMTest04`) or `[VT-THIS-ADDR]` (`StructTest14`); `JsonTest2`'s adaptor
unwrapped and advanced past the former site to a downstream NRE. `DelegateExt` is
Step 19 (delegates). No regression — the exception type is strictly more correct.

### 7. Legacy-neutral — **CONFIRMED**

Plain `Debug` CLI (Neo off -> Legacy `ExecuteR`), `useRegister=true`:
- `NeoStep`: **`Ran 330 tests, 17 failed`** — exactly the documented 17-failure
  baseline.
- `NeoStepClrObjIl`: **`Ran 2 tests, 0 failed`** — both probes pass under Legacy.

Neo-gated by construction (the guard lives under `ENABLE_NEO_MODE`); Legacy
behavior unchanged.

---

## Scope-worth-shipping verdict — **YES, worth shipping**

The change does not turn most of the 5 tests green, and the author states this
plainly (proposal "Scope note"; design "Goals/Non-Goals" + "Risks"). Even so it is
worth shipping as a faithful-guard improvement because:

- **CLR correctness.** `ldfld`/`stfld`/`ldobj`/`stobj` on null MUST be NRE. The
  old NIE was a wrong exception type that mis-attributed an upstream bug to a
  "deferred feature". NRE points future debugging at the real cause.
- **It unblocks a real shape.** The `CrossBindingAdaptorType` unwrap lets the
  adaptor case proceed (JsonTest2 advances past the site), where before it always
  crashed with a misleading NIE.
- **It is a prerequisite, not a dead end.** Once the separate `brtrue`-on-
  reference gap is fixed, the null owners in `TestStaticFieldInstance`/
  `RegisterVMTest04` will no longer be null, and those tests will pass through
  this (now-correct) guard. Shipping the guard now means that follow-up lands green.
- **Minimal blast radius.** ~25 lines, one helper, no JIT/optimizer/object-model
  change, defensive NIE retained for genuinely-unexpected shapes.

This is the right shape for an incremental faithful-guard fix with honest
non-goals.

---

## Findings

- **Blocker:** none.
- **Major:** none.
- **Minor:** none.
- **Trivial (informational, no action):**
  - The `DelegateExtObjMethod` CLI name-filter matched 0 tests (the method is
    `DelegateExtObjMethod.IntTest`; a class-name substring did not match). Test-
    harness filter quirk only; that hit is Step 19 (delegates), out of scope
    regardless. Not a code issue.
  - Scope limitation (4 of 5 tests still fail, now from NRE) is documented by the
    author in proposal/design/tasks — recorded here only so the LEAD sees it
    surfaced by an independent pass. It is the intended scope, not a defect.

---

## Build/test evidence (all `-f net8.0`)

- `dotnet build ILRuntimeTestCLI -c Debug_Neo --no-incremental` -> 0 errors.
- `dotnet build TestCases -c Debug` -> 0 errors.
- `dotnet build ILRuntimeTestCLI -c Debug` (Legacy) -> 0 errors.
- Neo probe filter: 2/0 PASS (fix); 2/2 FAIL (HEAD stash, NIE propagates).
- NeoStep smoke (`Debug_Neo`): 330/0.
- Legacy NeoStep (`Debug`, useRegister=true): 330 ran / 17 failed (= baseline).
- Full-smoke tagged-NIE count across the 5 diagnosed tests: 0.

---

**VERDICT: APPROVE.**

The `GetNeoILInstance` rework is sound and CLR-faithful; the byref-consumers-
already-route claim is confirmed (so the retained defensive NIE is genuinely
unreachable by raw CLR objects); the probes are valid and discriminating (FAULT on
HEAD via NIE-propagation, PASS after via NRE-caught); NeoStep is 330/0; the
tagged-NIE count dropped from ~5 to 0; Legacy is neutral at the 330/17 baseline.
The scope is honestly limited (most of the 5 tests stay red from a deeper cause)
and that is the documented, correct scope for a faithful-guard improvement. Ship it.
