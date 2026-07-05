## Why

Neo Step 18 shipped CLR-type `newobj` but **deferred IL value-type `newobj`**
(recorded as Q-VT-NEWOBJ / [VT-THIS-ADDR] -- the portfolio's highest-value
correctness fix, and a prerequisite for neo-step13-area4, neo-step17-completion,
and neo-k2fam-bridge). The blocker is a value-type field-access **lowering
consistency** gap: inside a VT instance method the `this` parameter register is
typed as the in-frame value type, so `this.field =` rewrites to `_Inline` and
writes the **callee frame**; but the caller passes `this` either as an mStack
index (ref-type call ABI) or as a frame-native Ref Slot, and the caller's
subsequent field reads on a newobj dest are typed as a heap object
(non-inline). The two ends cannot agree on a representation, so a value type
constructed via `newobj` (and the common local form `VT x = new VT(args)`,
which the C# compiler lowers to `ldloca; call ctor`) is broken end-to-end. The
Step 18 Newobj arm surfaces a loud Step-18-tagged NIE for the VT case rather
than shipping a silently-wrong construction.

## What Changes

This is the **D2 JIT change**: track a value-type `this` (parameter slot 0 of a
VT instance method) AND a value-type `newobj` dest as an **in-frame address**
for ALL field access (ctor `stfld` + caller `ldfld`), reusing and extending the
Step 17 byref / `addrAlias` / `liveAliasMap` machinery.

- **Caller side (newobj dest as an in-frame VT).** After the type-specialization
  pass, the dest register of a `Newobj` of an IL value type SHALL carry the VT
  type (today it is untyped), so the caller's subsequent `ldfld`/`stfld` on the
  result are recognized as in-frame-VT accesses and rewritten to `_Inline`
  (resolving against the dest register's frame byte/ref region). This is the
  minimal additive change that makes the caller read the construction site
  in-frame, matching the ctor's in-frame writes.
- **Callee side (VT `this` consistency).** The VT ctor's `this` (param slot 0)
  is already laid out and typed as the in-frame value (`ParamInfos[0].Size =
  TotalPrimitiveSize`, `RegisterTypes[0] = declaringType`), so `this.field =`
  already lowers to `_Inline`. The ctor receives the caller's frame-native Ref
  Slot `(-1, destFrameByteOff)` at its `this` param slot (Step 18 D1 already
  specifies the runtime write); this change ensures the lowering does not
  produce a contradictory mix of inline + heap arms for the SAME `this`-relative
  access by making the type-specialization discrimination self-consistent
  end-to-end.
- **Runtime (Newobj arm, IL VT branch).** Implement the deferred branch: zero-
  init the dest region, write the frame-native Ref Slot `(-1, destByteOff)` into
  the ctor's `this` param slot, copy the remaining ctor args, and invoke the
  ctor with `isNewobj=true` (no heap `ILTypeInstance`, no post-ctor copy-back).
- **addrAlias extension.** Extend the addrAlias producer set so a VT `this`
  param and a VT `newobj` dest are tracked as in-frame address roots for the
  folding/`liveAliasMap` machinery, exactly as `ldloca`/`ldflda` are today --
  so a `this.field` (or `newobjResult.field`) chain folds to a compile-time
  offset instead of being mis-resolved.
- **Adversarial test coverage.** Add `TestCases/NeoStep19Test.cs` (re-using the
  `NeoStep` smoke filter) with adversarial probes: VT ctor mutating multiple
  fields; newobj result read AFTER intervening heap writes / register reuse;
  nested VT; VT with reference-type fields; VT returned from a method then
  field-read; and the `VT x = new VT(args)` local form.

**Explicit non-goals (stated, NOT silently dropped):**
- Delegate `newobj` (Step 19 roadmap, separate child `neo-step19-delegate`).
- CLR value-type `newobj` with reference fields and no ValueTypeBinder (Step 13b
  NIE guard stays).
- Generic-parameter VT `newobj` spanning IL/CLR (with the generic-byref
  follow-up).
- A heap-alloc + copy-back fallback is **rejected** (it would mix object models
  -- the CLAUDE.md anti-pattern -- and does not even work without this
  consistency fix, since inline stflds would write the callee frame while heap
  stflds write the `ILTypeInstance`).

## Capabilities

### New Capabilities
<!-- None. This change extends an existing capability; it does not introduce one. -->

### Modified Capabilities
- `neo-value-types`: The "IL value-type construction via newobj" requirement
  (currently DEFERRED) is **delivered**: the dest register of a VT `newobj`
  carries the VT type so the caller's field reads are in-frame; the VT ctor's
  `this` is consistently an in-frame address end-to-end; the `Newobj` arm's IL
  VT branch constructs in-frame with no heap `ILTypeInstance`. Also extends the
  addrAlias requirement to track VT `this`/newobj-dest roots.
- `neo-byref`: The value-type `this` is a new, named caller of the frame-native
  Ref-Slot call-ABI (`(-1, frameByteOff)` passed as the ctor's first param),
  alongside `ldloca`-produced byrefs. The byref call-ABI itself is unchanged.
- `neo-newobj`: The IL value-type `newobj` requirement (DEFERRED in Step 18)
  is delivered: frame zero-init + Ref-Slot `this` + ctor writeback via stind/
  stfld, with the newobj-dest-as-in-frame-VT contract.

## Impact

- **Code (Neo-only; Legacy `ExecuteR` is the REFERENCE and is NOT modified):**
  - `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- type-
    specialization pass: seed the `Newobj` dest register's type when the
    constructed type is an IL value type (so downstream `TryRewriteFieldAccessForInline`
    recognizes the dest as an in-frame VT). This is the single load-bearing JIT
    decision point. `AllocateLocalStackSpaces` (`JITCompiler.cs:1332-1344`)
    already sizes a VT ctor's `ParamInfos[0]` as the in-frame value -- unchanged.
  - `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- extend the
    `addrAlias` producer set / `liveAliasMap` maintenance so a VT `this` param
    and a VT `newobj` dest are tracked as in-frame address roots (mirroring the
    `ldloca`/`ldflda` rule); ensure the Newobj-lowering dest stamping
    (`Optimizer.Neo.cs:1234-1241`) and the Call/Newobj Push-scanning remain
    sound under the new typing. File-gated Neo-only.
  - `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- `Newobj`
    arm, IL VT branch (`ILIntepreter.Neo.cs:1654-1701`): replace the Step-18
    NIE with the construction (zero-init dest region + write frame-native Ref
    Slot `(-1, destByteOff)` into the ctor `this` param slot + copy remaining
    args + `InvokeNeoCallTarget(ctor, isNewobj=true, ...)`).
  - If a shared optimizer pass (FCP/BCP/copy-prop/RegisterCleanup) needs a guard
    so the new VT-newobj-dest typing does not perturb copy-propagation, the
    change is gated `#if ENABLE_NEO_MODE` and confirmed Legacy-neutral.
- **Regression risk:** MEDIUM-HIGH. The change touches the field-access
  discriminator (`TryRewriteFieldAccessForInline`) and the addrAlias machinery,
  which are shared by EVERY VT instance method and every VT local access. The
  full `NeoStep` smoke (91/91 baseline) is the gate; Legacy (plain `Debug` +
  `useRegister=true`) must stay at its 518/519 baseline for any shared-pass
  gate. Adversarial probes are MANDATORY (a green smoke does NOT prove an
  optimizer/lowering gate correct -- Step 17 B1 / OPT-HARDEN K1 lessons).
- **Tests:** new `TestCases/NeoStep19Test.cs` (ASCII; `public static void`
  parameterless), filtered by `NeoStep`. The `NeoStep` count rises from 91.
- **Deferred-items doc:** Q-VT-NEWOBJ / [VT-THIS-ADDR] moves to Resolved in
  `.trae/documents/neo-deferred-items.md` (and the `neo-value-types` spec's
  DEFERRED marker is cleared).
