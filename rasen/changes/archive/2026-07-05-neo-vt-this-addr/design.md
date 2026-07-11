## Context

Neo Step 18 shipped CLR-type `newobj` and **deferred IL value-type `newobj`**
(Q-VT-NEWOBJ / [VT-THIS-ADDR]). The deferral was forced by a value-type field-
access lowering inconsistency that the Step 18 apply phase pinned empirically
(design.md "Apply-phase findings", `openspec/changes/archive/2026-07-04-
implement-neo-step18/`):

- Inside a VT instance method, the `this` parameter register (slot 0) is typed
  as the declaring IL value type (`BuildInitialRegisterTypes` @
  `JITCompiler.cs:888-905` seeds `registerTypes[0] = declaringType`), and
  `AllocateLocalStackSpaces` (`JITCompiler.cs:1332-1344`) sizes the ctor's
  `ParamInfos[0]` as the **in-frame value** (`Size = TotalPrimitiveSize`,
  `RefCount = TotalReferenceCount`). So `TryRewriteFieldAccessForInline`
  (`JITCompiler.cs:780-830`) recognizes `this` as an in-frame VT and rewrites
  `this.field =` to `Stfld_*_Inline` -- which resolves against the **callee
  frame bytes** (`Optimizer.Neo.cs:892-934`).
- The caller, however, has **no** `SetRegisterType` entry for the dest of a
  `Newobj`. So after `newobj VT(...)`, the dest register is untyped, and a
  subsequent `ldfld` on it is NOT recognized as in-frame -- it stays as the heap
  `Ldfld_*` arm, which calls `GetNeoILInstance` treating the dest as an mStack
  index. The two ends disagree on the representation.
- The `addrAlias` folding (`Optimizer.Neo.cs:25-81`) only tracks addresses
  produced by `ldloca`/`ldloca_S`/`ldflda`. A VT `this` param and a VT newobj
  dest are NOT in that producer set, so even an `ldflda` chain rooted at `this`
  cannot fold to a compile-time offset.

Confirmed empirically (Step 18 apply): `new NeoStep18Big(seed)` (5-field ctor)
lowers its first stfld to `_Inline` and the rest to heap `Stfld_I4`; the
caller's `ldfld` on the result is non-inline. A single-field ctor is all-inline
but the caller's read is STILL non-inline. The C# compiler lowers `VT x = new
VT(args)` on a local to `ldloca; call ctor` -- same VT-`this` field-access
issue, so the common local form is also broken.

Legacy (`ILIntepreter.Register.cs` `ExecuteR` `Newobj` for IL VT) is the
SEMANTICS reference: it uses `AllocValueType` + a `StackObjectReference` `this`
the ctor writes through, then `*reg1 = *ins` copies the result back. Neo uses
the frame + Ref-Slot model (no `StackObject`), and the frame-native Ref Slot
makes the copy-back implicit. Legacy is NOT modified.

Pre-requisite machinery (all shipped): Step 12 in-frame VT layout + `_Inline`
field opcodes; Step 12b `Move_Vt`; Step 17 byref call-ABI + Ref Slot
`(-1, frameByteOff)` (frame-native) / `(mStackIdx, fieldOff)` (heap) +
`addrAlias` COEXIST gate (`liveAliasMap`); Step 18 `InvokeNeoCallTarget(...,
isNewobj=true, ...)` + CLR newobj routing.

## Goals / Non-Goals

**Goals:**
- IL value-type `newobj`: `new MyILStruct(args)` produces a correctly-initialized
  value in the caller's frame dest slot, including a ctor that sets
  `this.field = value` (multiple fields, primitive + reference), with NO heap
  `ILTypeInstance` and NO post-ctor copy-back.
- The common local form `VT x = new VT(args)` (C#-lowered to `ldloca; call ctor`)
  works identically -- the ctor's `this`-relative writes land in the caller's
  frame slot for `x`.
- End-to-end representation consistency: a VT `this` (param slot 0) and a VT
  newobj dest are treated as in-frame addresses for ALL field access (ctor
  writes + caller reads), so no `_Inline`/heap mix can occur for the same
  logical value.
- Full `NeoStep` smoke stays green (91/91 baseline); Legacy (plain `Debug`) stays
  at its 518/519 baseline for any shared-pass gate.

**Non-Goals:**
- Delegate `newobj` (`new Action(foo)`) -- Step 19 (`ldftn`/DelegateAdapter),
  separate child `neo-step19-delegate`.
- CLR value-type `newobj` with reference fields and no ValueTypeBinder (Step 13b
  `NeoClrStructHasReferenceField` NIE guard stays).
- Generic-parameter VT `newobj` spanning IL/CLR (generic-byref follow-up).
- A heap-alloc + copy-back fallback -- REJECTED (would mix object models; also
  infeasible without the consistency fix).

## Decisions

### D1. The load-bearing change: type the Newobj dest as an in-frame VT

The single JIT-side decision: in the type-specialization pass
(`JITCompiler.cs`, the loop that calls `TryRewriteFieldAccessForInline` @537),
add a `case OpCodeREnum.Newobj:` that, when the resolved `targetMethod.DeclearingType`
is an IL value type (not enum, not primitive), seeds
`SetRegisterType(registerTypes, op.Register1, ilVtType)`.

Rationale: `TryRewriteFieldAccessForInline` already keys purely on the operand
register's value-category (`operandType is ILType ot && ot.IsValueType && !ot.IsEnum`,
@823-824). Once the dest register carries the VT type, EVERY subsequent
`ldfld`/`stfld` on it is recognized as in-frame and rewritten to `_Inline`,
which resolves against the dest register's frame byte/ref region. This is
exactly the rule already applied for `Ldloca` (@741-747) and `Ldflda` (@761-767)
dests. The Newobj dest is the missing third case.

This decision is **additive and minimal** -- it does not change the frame
layout, the call ABI, the existing `_Inline` lowering, or the addrAlias folding
for `ldloca`/`ldflda`. It only ensures the dest register is typed so the
existing discriminator fires.

Why not change the ctor's `ParamInfos[0]` to an 8-byte byref? The Step 18
design D2 considered this and the apply phase confirmed `ParamInfos[0]` is the
in-frame value. Keeping it as the in-frame value is correct: the ctor reads/
writes `this.field` through its OWN frame bytes (the callee frame), and the
caller seeds those callee bytes with a frame-native Ref Slot pointing at the
caller's dest region. The in-frame-VT typing of `this` makes the ctor's
`_Inline` stflds write the callee frame; the runtime then need not copy back
BECAUSE the callee frame's `this` slot IS the caller's dest region -- see D2.

### D2. Runtime: the Newobj IL-VT branch (frame-native Ref Slot `this`)

Replace the Step-18 NIE (`ILIntepreter.Neo.cs:1654-1701`) with the construction.
For `newobj VT(args)` where `VT` is an IL value type:

1. **Zero-init the dest region** at `frameBase + destByteOff`:
   `Unsafe.InitBlock(..., 0, ilVtType.TotalPrimitiveSize)`, and null every one
   of the dest's `ilVtType.TotalReferenceCount` ref slots
   (`mStack[frameRefBase + destRefOff + i] = null`). Matches `Initobj` semantics
   (`neo-value-types` "Initobj memset") -- a ctor may set only some fields.
2. **Write the frame-native Ref Slot `this`** into the ctor callee param
   region's slot 0. The ctor's `ParamInfos[0]` is sized as the in-frame value
   (`Size = TotalPrimitiveSize`, `RefCount = TotalReferenceCount`); the caller
   seeds those bytes so that the ctor's `this` reads resolve to the CALLER's
   dest region:
   - For the primitive half: the ctor's `_Inline` stfld resolves `this` via the
     `addrAlias`/`liveAliasMap` root at param slot 0 (see D3). The root's base
     register is param slot 0, whose `localInfos[0].Offset` is the callee's
     `this` primitive offset. To make the ctor's `_Inline` write land in the
     **caller** frame, the runtime writes the caller's frame-native address
     into the callee's `this` slot. Concretely: the callee param region's slot-0
     bytes are seeded as a frame-native Ref Slot `(-1, destFrameByteOff)`:
     `*(int*)(targetBase + thisPrimOff + 0) = -1;
      *(int*)(targetBase + thisPrimOff + 4) = destFrameByteOff;`
   - For the reference half: the ctor's `Stfld_Ref_Inline` resolves the ref
     field at `owningSlot.RefOffset + fieldRefOffset`. The owning slot is param
     slot 0, whose callee-frame `RefOffset` is `thisRefOff`. The runtime
     additionally seeds the callee's `this` REF slots
     (`mStack[calleeFrameRefBase + thisRefOff + i]`) to point at the caller's
     dest ref slots -- encoded as heap Ref Slots
     `(-1, destRefAbsFrameIdx + i)` so the ctor's ref-field writes propagate to
     the caller's dest ref region. (This mirrors the Step 17 in-frame-VT
     ref-field model and the Step 16 `ldelema` IL-VT-array element seeding.)
3. **Copy the remaining ctor args** via `CopyNeoCallArguments(ref map, ...)`
   into param slots [1..] (the lowering already builds the `NeoCallParamMap`
   skipping slot 0 for Newobj -- `Optimizer.Neo.cs:1197-1215, 1201`).
4. **Invoke the ctor** via `InvokeNeoCallTarget(ctorMethod, isNewobj:true,
   targetBase, mStack, retDstPtr: null, targetRetRefBase: destRefOff, out _)`.
   No heap `ILTypeInstance` is allocated; no mStack `this` push (contrast the
   IL ref-type path @1712); no post-ctor copy-back (the frame-native Ref Slot
   makes the ctor's writes land directly in the caller's dest region).

**ctor-this root in addrAlias (D3) is what makes step 2 work**: the ctor's
`this.field` lowers to `_Inline` with the owning operand = param slot 0; the
`liveAliasMap` must resolve param slot 0 to the frame-native Ref Slot written in
step 2, so the `_Inline` write targets `frameBase + destFrameByteOff +
fieldOffset`. Without D3 the `_Inline` would write the callee frame's slot-0
bytes (the Ref Slot payload itself), corrupting it.

### D3. addrAlias extension: track VT `this` param and VT newobj dest as roots

Extend the `addrAlias` producer set (`Optimizer.Neo.cs:25-81`) and the
`liveAliasMap` maintenance (`Optimizer.Neo.cs:1259-1301`) so two new in-frame
address roots are recognized:

- **VT `this` param (per-method, in the ctor's own body).** When the method
  being compiled `HasThis` and `declaringType.IsValueType`, param slot 0 is a
  pre-existing in-frame address root. The ctor's `this.field` lowers to
  `_Inline` with `Register1`/`Register2` = param slot 0; the lowering's
  `ResolveLiveAlias` (`Optimizer.Neo.cs:413-427`) must resolve param slot 0 to
  itself (Reg = 0, Offset = 0). Concretely: seed `addrAlias[0] = {Reg=0,
  Offset=0}` (and `liveAliasMap[0]` likewise) when the method is a VT instance
  method. The runtime (D2 step 2) has already seeded the callee's slot-0 bytes
  with the frame-native Ref Slot; the `_Inline` lowering then resolves the
  owning offset to `localInfos[0].Offset` (the slot-0 primitive offset in the
  callee frame), and the runtime `_Inline` arm writes there -- which IS the
  Ref Slot payload, which the caller-side field read (also `_Inline`, via D1)
  reads back. (Confirm during apply whether the slot-0 bytes need to be read AS
  a Ref Slot by the `_Inline` arm or whether a direct-callee-frame write
  suffices; the minimal form is the latter, and the frame-native Ref Slot is
  the mechanism for the ref-field half -- D2 step 2.)

  **Simplification (preferred, to confirm during apply):** if the ctor's
  `_Inline` stfld resolves `this` directly to param slot 0's callee-frame bytes
  AND the runtime seeds those bytes with a COPY of the caller's dest region
  (byte-copy the dest region INTO the callee `this` slot before the call, then
  byte-copy BACK after the call), the ref-field seeding is avoided. This is the
  Legacy `*reg1 = *ins` pattern adapted to Neo. D2 step 2's frame-native Ref
  Slot is the zero-copy alternative; the apply phase picks the one that needs
  less new runtime code, with a bias to the frame-native Ref Slot (no copy =
  no aliasing window). The design does NOT preclude either.

- **VT newobj dest (per-call, in the CALLER's body).** Once D1 types the Newobj
  dest as the VT, the caller's `ldfld`/`stfld` on it are already rewritten to
  `_Inline` by the existing discriminator. The dest register's frame byte/ref
  region is the construction site. No new addrAlias entry is needed on the
  caller side PER SE -- the dest register IS the owning slot, resolved
  directly. The only caller-side care: ensure the Newobj-lowering dest stamping
  (`Optimizer.Neo.cs:1234-1241`) leaves `op.Register1` intact for the
  type-specialization pass (which runs pre-lowering) -- it does, because D1
  runs in the pre-lowering type-specialization pass and `LowerNeoOffsets`
  mutates `DstOffset`/`Operand3` afterwards.

### D4. Shared-pass guard (if needed)

If FCP/BCP/copy-prop/RegisterCleanup mis-handle the new VT-typed Newobj dest
(e.g. copy-prop through a `Move` whose source is a VT newobj dest), the guard
is gated `#if ENABLE_NEO_MODE` and confirmed Legacy-neutral (plain-`Debug`
build compiles it out; Legacy 518/519 baseline unaffected). The Step 12b
`Move_Vt` + OPT-HARDEN `ldloca-kill` already cover VT-move copy-prop soundness;
D1 does not introduce a new move path, so the expected risk is low (probe
during apply; add a guard only if a reproducer fails).

### D5. Edge cases

- **Default ctor (parameterless) IL VT:** pCnt=0; zero-init dest; Ref Slot `this`
  written; ctor runs (no-op or sets defaults). Covered.
- **IL VT ctor with args:** pCnt>0; args copied to param slots [1..]; `this` at
  slot [0]. Covered.
- **IL VT ctor with reference fields:** D2 step 2 ref-slot seeding (or the
  copy-back simplification). Adversarial probe REQUIRED.
- **Base ctor chain (`VT ctor : base(...)`):** the base IL ctor `Call` forwards
  `this` (param slot 0) unchanged -- it is already an in-frame address root.
  Verify the chained `Call` does not re-allocate. Adversarial probe REQUIRED.
- **`VT x = new VT(args)` local form:** compiles to `ldloca x; call VT::ctor`;
  the `ldloca` already produces an in-frame address (existing Step 12 path),
  and the ctor receives it as `this`. The ctor's `_Inline` writes land in `x`'s
  frame slot. This works once D3 (VT `this` root) is in place -- the local
  form is the SAME path as newobj from the ctor's perspective. Adversarial
  probe REQUIRED.
- **VT returned from a method then field-read:** the return-value path uses
  `Move_Vt` (Step 12b) into the caller's dest; the dest is then field-read.
  Confirm the return dest is typed (D1 analog for `Call` dest of a VT-returning
  method -- likely already handled by `Move_Vt`'s type map; probe during apply).
- **Intervening heap writes / register reuse between newobj and the field read:**
  the dest register's live range must survive. The `liveAliasMap` per-
  instruction snapshot (Step 17 B1) already handles register reuse for
  `ldloca`; D3 extends it to the VT `this` root. Adversarial probe REQUIRED
  (the Step 17 B1 silent-corruption class).

## Risks / Trade-offs

- **[Field-access discriminator broadened to Newobj dest -- shared by every VT
  access]** → Mitigation: the change is purely additive typing (a dest that was
  untyped is now typed as the VT it constructs). The discriminator's existing
  logic is unchanged. The full `NeoStep` smoke (91/91) is the gate; adversarial
  probes (D5) are MANDATORY. Legacy is untouched (Neo-only code path).
- **[VT ctor `this` ref-field propagation]** → Mitigation: D2 step 2 ref-slot
  seeding OR the copy-back simplification (D3). The design permits both; the
  apply phase picks the minimal one. If ref fields do not propagate, fall back
  to copy-back (byte-copy dest region into callee `this` slot pre-call, back
  post-call). Adversarial probe: VT with 2+ reference fields, ctor sets them,
  caller reads them.
- **[Register reuse between newobj and field read (Step 17 B1 class)]** →
  Mitigation: the `liveAliasMap` per-instruction snapshot already kills an alias
  when its register is redefined. D3 extends the producer set, not the kill
  logic. Adversarial probe: newobj dest, intervening heap write that reuses the
  dest register, then field read -- MUST NOT observe a stale value.
- **[Base-ctor chain re-allocates `this`]** → Mitigation: the chained `Call`
  forwards param slot 0 byref (Step 17 byref call-ABI). Verify the byref
  forwarding does not allocate a fresh frame region. Adversarial probe: VT
  ctor `: base(arg)` where the base ctor sets a field.
- **[Shared-pass copy-prop through a VT newobj dest]** → Mitigation: probe FCP/
  BCP/copy-prop with a `Move` whose source is a VT newobj dest; if mis-
  propagated, gate the fix `#if ENABLE_NEO_MODE` (Legacy-neutral). Low expected
  risk (no new move path).
- **[Frame-native Ref Slot vs copy-back choice]** → Mitigation: the frame-
  native Ref Slot is zero-copy (preferred) but couples the caller and callee
  frame layouts; copy-back is simpler but opens an aliasing window. The apply
  phase implements ONE and the adversarial probes validate it; the design does
  not force a wrong choice.

## Open Questions

- Does the ctor's `_Inline` stfld on `this` resolve param slot 0 directly to
  the callee-frame bytes (direct-write), or does it need the runtime to seed
  the slot-0 bytes as a frame-native Ref Slot that the `_Inline` arm
  dereferences? (Resolved during apply via a JIT dump of the ctor body + a
  single-field reproducer; D2/D3 permit both.)
- Does any `NeoStep` smoke case (or the broader suite) currently exercise
  `VT x = new VT(args)` on a local that would turn green with D3? (Probe at
  verify; the local form is the common C# pattern, so this is the high-value
  green-up.)
- Is a shared-pass (FCP/BCP) guard needed for copy-prop through a VT newobj
  dest? (Probe during apply; add only if a reproducer fails.)

## Apply-phase findings (2026-07-05) -- CONFIRMED MECHANISM

The propose-time hypothesis (D1 Newobj-dest-typing + D3 addrAlias VT-`this`
root + D2 frame-native-Ref-Slot runtime) was PARTLY right but MISSED the true
load-bearing root cause. The JIT-dump probes (tasks 1.2 / 2.2 / 3.3) revealed
three distinct gaps, each requiring its own fix. Summary of what was actually
done (all Neo-only; Legacy `ExecuteR` untouched):

### A. The TRUE load-bearing root cause: inline-stfld owner-type clobber

`TypeSpecializeNeoOpcodes` (`JITCompiler.cs:537-547`) seeded the dest-temp
type after EVERY successful inline rewrite -- including `Stfld_*_Inline`,
where `Register1` is the OWNING in-frame VT (NOT a destination). Stamping the
field's primitive type onto the owner clobbered `registerTypes[owner]`, so
the SECOND (and every subsequent) `this.field=` / `dest.field=` on the SAME
owner register saw a non-VT type and fell back to the heap `Stfld_*` arm.
This is why a multi-field VT ctor (`NeoStep18Big`, 3 ints) lowered to a MIX
of `_Inline` (field a) + heap `Stfld_I4` (fields b, c), and the heap arm
NRE'd on the garbage mStack index read from the in-frame dest bytes.

This bug was INDEPENDENT of newobj -- it affected every multi-field VT ctor
AND every multi-field in-frame-VT store. The fix is one rule: seed the
dest-temp type ONLY for an inline `Ldfld` (where Register1 is the load
destination); for an inline `Stfld`, Register1 is the owner and MUST keep its
VT type. Added `IsInlineLdfldDestSeedable(code)` helper to gate the seeding
(`JITCompiler.cs`). This single fix turned the INLINED ctor path (the common
C# `new VT(args)` local form) green.

### B. D1 (as proposed) -- Newobj dest typing

CONFIRMED needed for the NON-inlined path (a factory method `S Make()` that
does `return new S(args)` inlines the FACTORY but emits a REAL `newobj` for
the VT ctor; the dest is then field-read in the caller). Without D1 the
caller's `ldfld` on the newobj result stays heap (NRE). Added
`case OpCodeREnum.Newobj:` to the type-spec pass that seeds
`registerTypes[op.Register1] = ilVtType` when the ctor's declaring type is
an IL value type (mirrors the `Ldloca`/`Ldflda` rules). `JITCompiler.cs`
near the `Ldflda` case.

### C. Newobj dest temp sizing -- `GatherValueTypes` gap

`AllocateLocalStackSpaces` sizes every temp register to `maxSize` gathered
from `GatherValueTypes`. `GatherValueTypes` did NOT include `Newobj`, so a VT
constructed via newobj that is LARGER than the default 8 bytes got an
undersized dest temp, and the subsequent `Move` of the dest (`op.Operand2 =
min(srcSz, dstSz)`) truncated the copy to 8 bytes (silent corruption: field c
read back as 0). Added `case OpCodeREnum.Newobj:` to `GatherValueTypes`
resolving the ctor's declaring type. `JITCompiler.cs` GatherValueTypes.

### D. D2 (runtime) -- copy-back, NOT frame-native Ref Slot

The dump resolved the D2 open question: the `_Inline` stfld arm writes
through the owning slot's frame bytes DIRECTLY (it does NOT dereference a
frame-native Ref Slot), and the caller's dest region is a SEPARATE frame
buffer from the callee's slot-0 region. So the zero-copy frame-native Ref
Slot mechanism does NOT apply. The runtime uses the **copy-back pattern**
(Legacy `*reg1 = *ins` adapted to Neo): zero-init caller dest; copy caller
dest prim bytes INTO callee slot-0 prim bytes (pre-call); copy ctor args;
invoke ctor; copy callee slot-0 BACK to caller dest (post-call).

The ref-half needs special handling: ExecuteNeo pops the callee's mStack
reservation on return (RemoveRange destroys the slot-0 ref entries). So the
slot-0 -> caller-dest ref copy-back MUST run BEFORE the pop. Implemented by
adding optional params to `ExecuteNeo` (`vtNewobjCallerDst`,
`vtNewobjCallerDstRefBase`, `vtNewobjCallerPrimSize`, `vtNewobjCallerRefCount`)
and doing the copy-back in the **Ret arm** (before its `RemoveRange`). The
Newobj branch calls `ExecuteNeo` directly (not `InvokeNeoCallTarget`) to pass
these. Defaults preserve existing callers (no behavior change for non-VT-
ctor calls). `ILIntepreter.Neo.cs` Newobj arm + Ret arm + signature.

### E. D3 (addrAlias VT-`this` root) -- NOT NEEDED

The propose-time D3 (seed `addrAlias[0]` / `liveAliasMap[0]` for a VT `this`)
was NOT needed. The `ResolveLiveAlias` fallback (`Optimizer.Neo.cs:421-428`)
already returns `{Reg=reg, Offset=0}` for an unaliased register, which for
param slot 0 resolves to `localInfos[0].Offset` = the slot-0 byte region --
exactly correct for the ctor's `this.field=` `_Inline` lowering. So no
addrAlias change was required. (D3 would only matter for `ldflda
this.field` chains in a ctor body, which the test cases do not exercise; the
direct `this.field=` path -- the common case -- works via the fallback.)

### Non-goals confirmed out-of-scope

- **`Stfld_Value` (whole-VT-into-VT-field store)** is a Step 12b deferred
  item. TC10 (nested VT) was adapted to construct the nested VT directly
  rather than store it into an Outer field, avoiding `stfld.value`.
- **Delegate / no-binder-CLR-VT-with-refs / generic-param VT newobj**: NIE
  guards unchanged.

### Shared-pass probe (task 6.1) -- NO shared-pass change

All fixes are in Neo-only code (`TypeSpecializeNeoOpcodes`,
`GatherValueTypes`, `ILIntepreter.Neo.cs` -- all `#if ENABLE_NEO_MODE` or
Neo-only files). No FCP/BCP/copy-prop/RegisterCleanup change. Legacy
(plain `Debug`) compiles all changes out; the 7 pre-existing Legacy
NeoStep-filter failures (NeoStep15_TC6, NeoNaNR8, ...) are unrelated and
present without the change.

### Result

Full NeoStep smoke: **99/99 green** (91 baseline + 8 new TC8-TC15). The 8
adversarial probes (multi-field ctor; newobj-result-read-after-intervening-
heap-writes; nested VT; VT with ref fields incl. forced-real-newobj factory;
VT returned then field-read; local form; complex ctor with helper call;
partial-init) all pass.

### Edit sites

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
  - `TypeSpecializeNeoOpcodes` inline-rewrite seeding: gate dest-type seeding
    to Ldfld only via `IsInlineLdfldDestSeedable` (fix A).
  - `TypeSpecializeNeoOpcodes`: `case Newobj:` dest-typing (D1 / fix B).
  - `GatherValueTypes`: `case Newobj:` gather constructed VT (fix C).
  - New helper `IsInlineLdfldDestSeedable`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  - `Newobj` arm IL-VT branch: copy-back construction (D2 / fix D).
  - `Ret` arm: slot-0 -> caller-dest copy-back before the mStack pop (D2).
  - `ExecuteNeo` signature: + 4 optional `vtNewobjCallerDst*` params.
