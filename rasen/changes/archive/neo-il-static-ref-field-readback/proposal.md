# Proposal — neo-il-static-ref-field-readback

> Child 13 of the `neo-overhaul` portfolio. Neo-gated (`#if ENABLE_NEO_MODE`),
> therefore Legacy-neutral by construction. An active gap surfaced post-child-12
> (`neo-ceq-null-sentinel`): `SimpleTest.TestStaticFieldInstance` progressed past
> the ceq null-sentinel NRE and now throws
> `InvalidCastException: … ILTypeInstance → System.String`.

## The defect (as surfaced)

Reading a freshly-written IL-static **reference** field's value and using it
faults. The canonical reproducer is the C# lazy-init idiom
`if (_field == null) _field = new T(refArg); return _field;` (an IL-static
reference field initialized through a getter), then reading a field of the
stored instance whose value came from the ctor's reference argument.
`TestStaticFieldInstance` (`TestA.Instance.TestCall(...)`) throws
`InvalidCastException` casting an `ILTypeInstance` to `String` at
`String.Concat`, and child-12's TC1 redesign observed the static field's value
read back wrong.

## Root cause (investigated, NOT the framed hypothesis)

**The IL-static reference-field read-back itself is CORRECT.** Instrumented
diagnostics confirm `Stsfld` writes the object to
`sinst.ManagedObjects[off.ReferenceOffset]` and `Ldsfld` reads it back and
pushes the stored object (the `TestA` instance round-trips faithfully). The
static-path arms (`ILIntepreter.Neo.cs` Stsfld ~4260 / Ldsfld ~4405) are
symmetric and correct; child-3 + child-11's F3 localInfos resolution hold.

**The real defect is in the IL reference-type `newobj` arm** (`ILIntepreter.Neo.cs`,
"Step 8b" block, ~3309). When the newobj **dest register aliases a reference
argument register** — the canonical register-VM lowering of
`ldstr/ldloc refArg; newobj(refArg)` reuses the arg's register as the dest —
the new instance's mStack ref slot (`newobjDstIdx = frameRefBase + dstRefOffset`)
coincides with that arg's own ref slot (same register ⇒ same `RefOffset`), so
**the arg's mStack index EQUALS `newobjDstIdx`**. The newobj arm stores the
instance (`mStack[newobjDstIdx] = ins`) BEFORE `CopyNeoCallArguments` copies the
arg into the ctor frame, clobbering the arg object. The ctor then receives the
new instance (`this`) as the aliased reference argument.

For `TestStaticFieldInstance`: `_instance = new TestA("testerror")` → the
`TestA..ctor(string name)` ctor sees `name = this` (the new TestA), stores
`this` into its `name` instance field; the later `this.name` read yields the
`ILTypeInstance`, which `String.Concat` cannot cast to `String`.

Empirical proof (diagnostics on HEAD):
- Getter (`Instance`): `mStack[newobjDstIdx=6] BEFORE overwrite = String=testerror`;
  the arg sits at mStack[6] and `newobjDstIdx=6` (= `frameRefBase(4) + dstRefOff(2)`).
  `prim[0] srcOff=16 == retDstOff=16` ⇒ dest register IS the arg register.
- Probe (no static field, works): `mStack[newobjDstIdx=6] BEFORE overwrite = null`;
  arg at mStack[8], `srcOff=12 != retDstOff`. No alias ⇒ no clobber.

This is the **reference-arg** case the existing `neo-newobj` "Q-NEWOBJ dest/arg
aliasing contract" explicitly claimed needed no fix — that claim was verified
only for a primitive `intArg` (whose slot value is not an mStack index, so it
cannot be clobbered). A reference arg's slot value IS an mStack index, so the
contract did not extend to it.

## What changes

1. **`ILIntepreter.Neo.cs`** — in the IL reference-type `newobj` arm, BEFORE
   `mStack[newobjDstIdx] = ins`, detect whether the dest register aliases a
   reference argument (the dest's `dstRefOffset` appears in the call's ref-source
   map) AND the aliased arg's index equals `newobjDstIdx`. If so, re-base the
   arg object to a fresh mStack slot (`mStack.Add(mStack[aIdx])`) and rewrite the
   source register so `CopyNeoCallArguments` hands the ctor the arg object, not
   the instance. `~15` lines, fully `#if ENABLE_NEO_MODE`-gated. The discriminator
   (ref-map membership) excludes primitive `int` args whose value coincidentally
   equals `newobjDstIdx` — those are left untouched.
2. **`TestCases/NeoStepNewobjArgAliasTest.cs`** (new) — two NeoStep probes
   (`NeoStepNewobjArgAlias_TC1_LazyInitRefArg`, `_TC2_SecondLazyInitRefArg`)
   mirroring the `TestA.Instance` lazy-init shape. Each FAULTs on HEAD
   (`DivideByZeroException` from a deliberate 1/0 when the ctor's reference arg
   round-trips wrong) and PASSES with the fix. Stash-toggle confirmed.
3. **Spec** — `neo-newobj` Q-NEWOBJ aliasing contract MODIFIED to cover the
   reference-arg case and mandate the re-base.

## Why this is the right scope (not "fix the static read-back")

The framed name (`neo-il-static-ref-field-readback`) describes the *symptom
surface* (the failing test reads an IL-static ref field). The *root cause* is the
newobj arg-clobber; the static read-back is innocent. Fixing the newobj arm
makes `TestStaticFieldInstance` pass and closes the reference-arg hole in the
existing aliasing contract. Fixing the (correct) static arms would change
nothing.

## Verify

- NeoStep smoke (`Debug_Neo`, `true NeoStep`): **339/0** (335 baseline + 4 probe
  runs; the filter matches both probe methods + their getters). No regression.
- `TestStaticFieldInstance`: PASSES (was `InvalidCastException` on HEAD).
- Stash-toggle: disabling the re-base → both probe TCs FAULT
  (`DivideByZeroException`); restoring → 339/0.
- Legacy-neutral (plain `Debug`, `true NeoStepNewobjArgAlias`): 4/0 (the new
  probes pass under `ExecuteR`; the edit is Neo-gated).

## Out of scope

- The IL-static Stsfld/Ldsfeld arms (correct; untouched).
- Byref / value-type ctor args (byref sources are excluded from the re-base;
  their 8-byte Ref Slot is handled by `SnapshotNeoCallByRefSources`).
- A general newobj-lowering rewrite (the targeted runtime re-base is the minimal
  sound fix; the JIT dest/arg allocation is unchanged).
