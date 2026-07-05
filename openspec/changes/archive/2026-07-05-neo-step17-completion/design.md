## Context

Step 17 shipped the Neo byref model (8-byte Ref Slot `(objectIndex, offset)`,
the `ldloca`/`ldflda`/`ldarga`/`ldelema` producers, the `stind_*`/`ldind_*`/
`stobj`/`ldobj` consumers, the `ref`/`out` IL call ABI, and the IL value-type
array `ldelema` path) but deferred `constrained.`-on-value-type dispatch and a
handful of sub-cases. Three prerequisites now unblock the highest-value piece:

1. **`ldarga`/`ldarga.s`** arms landed in Step 17 (`ILIntepreter.Neo.cs:709-733`,
   produce `(-1, paramFrameOffset)`).
2. **VT-THIS-ADDR** shipped the in-frame VT address model + the Newobj-dest
   typing + the addrAlias VT-`this` root (`neo-vt-this-addr`).
3. **`neo-step13-area4`** shipped the byref-`this` direct-call on a CLR struct
   (`CopyNeoCallArguments` derefs a byref source via the new
   `NeoCallParamMap.PrimitiveByRefSrc` flag; the callee `this` slot holds flat
   bytes; `CopyNeoCallThisBack` propagates mutations).

The current state of the four deferred pieces (code-grounded at HEAD):

- **(a) `constrained.`-on-VT** (`ILIntepreter.Neo.cs:~3126-3141`): the
  `Constrained` arm is a Step-17-tagged `NotImplementedException`. The JIT
  detects a preceding `Constrained` and RE-APPENDS it AFTER the callvirt
  (`JITCompiler.cs:~1954-1964`): it removes the `Constrained` opcode from before
  the callvirt, stamps `op.Operand4 |= 0x1` on the callvirt (and on
  `Call_Redirect`), copies `op.Operand2` (the dispatch slot) onto the
  `Constrained.Operand2`, and `lst.Add(old)` re-appends it. The constrained type
  token is in `Constrained.Operand`. So at runtime the callvirt executes FIRST
  (with `Operand4 & 0x1` set) and the `Constrained` arm runs AFTER. The C#
  compiler lowers `v.ToString()` on a struct `this` to `ldloca v; constrained T;
  callvirt Object.ToString` -- the callvirt's `this` is the 8-byte Ref Slot from
  `ldloca`, but the callvirt resolver (`ResolveNeoCallvirtILTarget` /
  `ResolveNeoCallvirtCLRTarget` -> `ReadNeoCallThis`) reads `this` as an mStack
  index, which is garbage for a byref `this`. THIS IS THE ARCHITECTURAL CRUX
  (Decision D1).

- **(b) `Stobj`/`Ldobj`** (`ILIntepreter.Neo.cs:2987-3026`): copies `primSize`
  bytes only (no ref-slot loop). A VT WITH reference fields copied through
  stobj/ldobj loses its ref slots. Narrow extension; isolated.

- **(c) generic-byref / `fixed` / interface-on-VT-constrained**: still tagged
  NIEs; not unblocked by (a).

- **(d) CLR primitive-array `ldelema`** (`ILIntepreter.Neo.cs:~3053-3057`): the
  `else` branch (a non-IL-VT array) throws a Step-17 NIE. The IL-VT-array path
  (`ilArr` branch) is green. A CLR primitive array (`int[]`, `float[]`, etc.)
  address-of-element is the missing piece.

The Legacy reference (`ILIntepreter.Register.cs:3898`, the `Constrained` arm)
carries the SEMANTICS: it resolves the constrained type, walks the params, and
calls `GetObjectAndResolveReference(objRef)` to get the receiver; for a value-
type constrained type it box-loads the struct into an `ILTypeInstance` / boxed
CLR object so the dispatched method sees a boxed `this`. Neo reshapes that into
the Ref-Slot + flat-bytes model.

## Goals / Non-Goals

**Goals:**

- **(a) Deliver `constrained.callvirt T.M` on a value-type `T`** for the two
  dispatch shapes:
  - **Box-once** (`M` requires boxing, e.g. `T.ToString()` overriding
    `Object.ToString`, or any interface method on `T`): box the struct ONCE
    using the constrained type token and the byref `this` address, then dispatch
    the method on the boxed `this` so the override sees a boxed receiver.
  - **Direct-call** (`M` does NOT require boxing -- a struct method that is not
    an override/interface): reuse area4's byref-`this` direct-call path (the
    `PrimitiveByRefSrc` deref-at-copy-site + `ReadNeoValueType` reader).
  Cover BOTH an IL value type and a CLR value type as the constrained `T`.
- **(d) Deliver CLR primitive-array `ldelema`** (the `int[]`/`float[]`/etc.
  address-of-element Ref Slot, consumable by `stind_*`/`ldind_*`).
- **(M2 obligation) Close `F-5 / NEO-CALLARG-BOXED-SRC`**: when the box-once
  path produces a boxed `this` that flows through `CopyNeoCallArguments`, the
  boxed-source branch (`~258-265`) MUST be correct (or NIE-guarded). Tighten the
  `CopyNeoCallThisBack` comment (instance methods, NOT ctors). Fix the stale
  `Constrained` NIE text (`T1`).
- **Reuse, do not reinvent**: VT-THIS-ADDR's in-frame VT address + area4's
  byref-`this` machinery. The constrained path is a NEW CALLER of those, not a
  parallel implementation.
- **Legacy-neutral**: all runtime changes Neo-only / in `#if ENABLE_NEO_MODE`-
  gated files. Legacy `Constrained` arm is the REFERENCE, not modified.

**Non-Goals (deferred to `neo-step17-stobj-refloop` / `neo-step17-misc`):**

- **(b) `Stobj`/`Ldobj` ref-slot loop** (VT-with-ref-fields copy through
  stobj/ldobj). Isolated; does not fall out of (a).
- **(c) generic-byref** (`ref T`/`out T`, `T` generic parameter), **`fixed`**
  unmanaged-pinning blocks, **interface-on-VT-constrained** rare sub-cases. None
  unblocked by (a); none exercised by the smoke. Remain Step-17-tagged NIEs.
- **Constrained on a NULL or reference-type `this`** (`constrained.callvirt`
  where the runtime `this` is already a boxed object): the box-once is a no-op
  (the Legacy arm's `else` branch reads `insIdx = objRef->Value` directly). This
  case is reachable only via an interface-typed local holding a boxed struct;
  defer IF the smoke does not exercise it, otherwise it falls out of the box-once
  arm for free (the "already boxed" fast path).

## Decisions

### D1 -- THE ARCHITECTURAL CRUX: how the callvirt sees the constrained type

The JIT re-appends `Constrained` AFTER the callvirt, so a naive runtime order
executes the callvirt (mis-reading the byref `this` as an mStack index) BEFORE
the `Constrained` arm can inform it. Two viable resolutions (PICK AT APPLY via a
JIT dump of `v.ToString()` on a struct):

- **Option F (Fusion, PREFERRED):** a JIT pass that detects the
  `Constrained; Callvirt` pair and FUSES them into a specialized callvirt
  variant (e.g. reuses `Callvirt_IL`/`Callvirt_CLR`/`Callvirt_Interface` with
  the constrained type token stamped in a spare operand, OR introduces a new
  `Callvirt_Constrained` opcode). The fused opcode carries the constrained type
  token, so the callvirt resolver knows it has a byref VT `this` and dispatches
  via the box-once / direct-call path. The trailing `Constrained` arm becomes a
  runtime no-op (the dispatch already happened in the fused callvirt). This is
  cleanest: the dispatch is atomic, no two-phase runtime dance, and the existing
  `Operand4 & 0x1` flag is already the JIT's signal that a constrained is
  attached.
- **Option R (Runtime two-phase):** keep the callvirt + trailing Constrained as
  two opcodes; the callvirt detects `Operand4 & 0x1` and, instead of dispatching
  immediately, DEFERS (records the call site + byref `this` address) and the
  trailing `Constrained` arm performs the box-once + dispatch. This is more
  complex (the callvirt must stash state for the Constrained arm to consume) and
  risks the callvirt's `retDstPtr`/`targetRetRefBase` plumbing being done twice
  or not at all.

**Lean: Option F.** It matches the existing JIT infrastructure (the JIT already
stamps `Operand4 |= 0x1` on the callvirt for a constrained; fusing just means
also carrying the constrained type token onto the callvirt and making the
trailing `Constrained` a no-op). The Legacy two-phase shape exists only because
Legacy's `Constrained` arm post-processes the call's STACK effects; Neo's
register frame makes fusion natural. **VERIFY at apply**: dump the JIT IR for
`struct v; v.ToString()` and confirm the callvirt's `Operand4 & 0x1` is set and
the constrained type token is recoverable; then implement the fusion (or, if the
token is NOT recoverable on the callvirt, fall back to Option R).

### D2 -- Box-once semantics (the box-required case)

For `constrained.callvirt T.M` where `T` is a value type and `M` requires
boxing (an override of `Object.ToString`/`GetHashCode`/`Equals`, or any
interface method):

1. The byref `this` (8-byte Ref Slot from `ldloca`/`ldarga`) addresses the struct
   in the caller's frame.
2. The dispatch box-loads the struct ONCE: read the flat bytes from
   `frameBase + offset` (the byref's offset half, exactly the deref area4's
   `CopyNeoCallArguments` already does), construct a boxed representation (an
   `ILTypeInstance` for an IL VT; a CLR boxed struct for a CLR VT via
   `Activator`/the existing Box arm machinery from Step 13).
3. Dispatch `M` on the boxed receiver. The dispatched override sees a boxed
   `this` (correct `Object.ToString` semantics). The boxed object is the result
   of a SINGLE box (matching Legacy's `GetObjectAndResolveReference` box-once);
   it is NOT re-boxed per virtual dispatch.

Reuse Step 13's Box arm (`Box` opcode) for the box-load step where possible
(the Box arm already reads flat bytes via `ReadNeoValueType` and produces a
boxed object). The constrained dispatch is then a Callvirt on the boxed object.

### D3 -- Direct-call semantics (the no-box case)

For `constrained.callvirt T.M` where `T` is a value type and `M` does NOT
require boxing (a struct method that is neither an override nor an interface
impl, e.g. a method declared directly on the struct): reuse area4's byref-`this`
direct-call path verbatim -- the `PrimitiveByRefSrc` flag derefs the byref at
the copy site, the callee `this` slot holds flat bytes, `ReadNeoValueType`
reads it. No box. This is the same path `local.VTMethod()` takes.

The discriminator (box vs no-box) keys on the constrained type token's method
slot: if `M` is declared on `Object` / an interface / a System ValueType base
and the struct overrides it -> box; if `M` is declared on the struct itself ->
direct. Mirror Legacy's discriminator
(`GetObjectAndResolveReference` + the `type is ILType` / `IsEnum` branches).

### D4 -- CLR primitive-array `ldelema` (D-LDELEMA remainder)

The `Ldelema` arm's `else` branch (`~3053-3057`) handles a non-IL-VT array.
Replace the NIE with: resolve the array's element size from its CLR type
(`Array la`; `la.GetType().GetElementType()` -> `AppDomain.GetPrimitiveSize` or
the managed size for a CLR struct element), compute
`elementByteOffset = elementIdx * elementSize`, and emit
`(arrIdx, elementByteOffset)`. The consumer (`stind_*`/`ldind_*`) then reads/
writes the CLR array's element. **Subtlety (resolve at apply):** a CLR primitive
array's elements live in the CLR array's backing storage, NOT in an
`ILTypeInstance.Primitives`. So the `stind`/`ldind` frame-native-vs-mStack
dispatch does not directly apply -- the `objectIndex >= 0` arm of `stind`/`ldind`
currently assumes an `ILTypeInstance`. The likely shape: a CLR-array Ref Slot
needs the arm to index into the CLR `Array` directly (`la.GetValue(elemIdx)` /
`la.SetValue(...)`), mirroring how Step 16's `Ldelem`/`Stelem` resolve CLR
arrays. **DUMP-GATE**: confirm the consumer-arm shape for a CLR-array Ref Slot
at apply; if the existing `stind`/`ldind` `objectIndex >= 0` arm cannot address
a CLR array element without an ILTypeInstance, the `Ldelema` CLR branch must
park a wrapper or the consumer must branch on `mStack[objIdx] is Array`.
Minimal-surface preference: if the consumer plumbing is larger than the
`Ldelema` arm itself, scope (d) down to the green target that the smoke demands
(e.g. `int[]` ldelema -> stind/ldind of int) and NIE the rest.

### D5 -- M2 obligation closure (F-5 / NEO-CALLARG-BOXED-SRC)

The box-once path (D2) produces a boxed `this`. IF that boxed `this` flows
through `CopyNeoCallArguments` (the call-lowering's param-copy), the boxed-source
branch (`~258-265`) MUST be correct. Two sub-cases:

- **Box-once takes the autogen-redirect path** (the dispatched method is a CLR
  method with a `RedirectionNeo`): the boxed `this` is the `instance` argument
  to the redirect delegate. The boxed-source branch's current defensive
  `CopyBlock(targetBase + Dst, frameBase + offset, Size)` is WRONG for a boxed
  source (`offset` is an mStack field offset, not a struct address). Fix: either
  read the boxed struct's flat bytes via the Box-arm's inverse (Unbox-style read
  into the dest slot), OR -- cleaner -- have the box-once path store the boxed
  object's mStack index into the dest slot directly (the dest slot for an
  `object`-typed `this` is a 4-byte ref slot, not flat bytes), matching how a
  reference-type `this` is passed.
- **Box-once takes the IL-method path** (the dispatched method is an IL override
  on the boxed struct): the boxed `this` is an `ILTypeInstance`; the call's `this`
  slot is a 4-byte mStack index. Same shape as any IL-method call on a reference
  receiver -- no special boxed-source handling needed.

**Decide at apply** based on which path the box-once (D2) actually feeds. In
either case, the defensive `CopyBlock` in the boxed-source branch is replaced
with the correct shape OR guarded with a NIE that names the unhandled sub-case
(no silent mis-copy). Tighten the `CopyNeoCallThisBack` comment to "mutating
INSTANCE METHODS (not ctors -- the newobj path does not invoke it)".

### D6 -- Reuse map (do not reinvent)

- **VT in-frame address**: VT-THIS-ADDR's in-frame VT byte/ref region. The
  constrained byref `this` resolves to this region; the box-once reads flat
  bytes from it (same as area4's `CopyNeoCallArguments` deref).
- **Byref-`this` direct-call (no-box)**: area4's `PrimitiveByRefSrc` +
  `ReadNeoValueType` + `CopyNeoCallThisBack`. Unchanged for the no-box path.
- **Box-load**: Step 13's `Box` arm (`ReadNeoValueType` flat bytes -> boxed
  object). Reuse for the box-once.
- **Callvirt dispatch**: the existing `Callvirt_IL`/`Callvirt_CLR`/
  `Callvirt_Interface` resolvers, invoked on the boxed receiver (box case) or
  the byref-`this` (no-box case via area4's path).

## Risks / Trade-offs

- **[The Constrained re-append ordering] -> D1 Option F (fusion) is the lean,
  but Option R (runtime two-phase) is the fallback.** VERIFY via JIT dump at
  apply. If neither composes cleanly, STOP and pin the dump (the OPT-HARDEN K1 /
  F-MAJ-1 lesson: probe before fixing; STOP if the designed fix is wrong). Do
  NOT ship a guessed fusion.
- **[Box-once double-boxing / missing override dispatch]** -> the box MUST
  happen exactly once and the override MUST be the constrained type's override
  (not the static call-site type's). Mirror Legacy's `GetObjectAndResolveReference`
  discriminator. Adversarial probe: a struct that overrides `ToString` AND a
  base struct that does not, dispatched via a generic `<T> string S<T>(T v) where
  T:struct` -> assert the override fires.
- **[CLR-array ldelema consumer plumbing larger than the arm]** -> D4 dump-gate;
  scope (d) to the smoke's green target if the consumer-side change is large.
- **[Regression of the addrAlias / call-lowering gate]** -> constrained is
  ALREADY an escape consumer per Step 17's COEXIST gate (`liveAliasMap`); the
  fusion (D1 Option F) must not weaken the gate. Full `NeoStep` smoke (117/117
  baseline) + the Step-17-B1 adversarial register-reuse probe MUST stay green.
  Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1 / area4
  dump-gate lessons binding).
- **[M2 boxed-source mis-copy slipping through]** -> the box-once path is the
  ONLY producer of a boxed `this` through `CopyNeoCallArguments`. Probe it
  directly (`v.ToString()` on a struct that goes through a CLR redirect, e.g. an
  override resolved by an autogen binding). If unreachable, NIE-guard the branch
  explicitly (no silent mis-copy).
- **[CLI host-DLL stale-copy after adding a host struct]** -> rebuild CLI with
  `--no-incremental` after adding any new host CLR struct (area4 earned
  gotcha); verify `grep -c NewType ILRuntimeTestCLI/bin/Debug_Neo/net8.0/ILRuntimeTestBase.dll >= 1`.

## Migration Plan

None (additive Neo-only runtime + JIT changes; Legacy untouched; no public API
change; no on-disk format change). Rollback = revert the change's source files.

## Open Questions

- **OQ1 (D1):** does the JIT's existing `Operand4 |= 0x1` stamping on the
  callvirt carry enough for fusion, or must the constrained type token also be
  stamped onto the callvirt (spare operand)? Resolve via JIT dump at apply.
- **OQ2 (D2):** for a CLR VT constrained call whose override is resolved by an
  autogen redirect, does the boxed `this` flow through
  `CopyNeoCallArguments` (D5 box-once-takes-redirect sub-case) or does the
  box-once bypass the param-copy (store the mStack index directly)? Resolve via
  dump at apply.
- **OQ3 (D4):** can the existing `stind`/`ldind` `objectIndex >= 0` arm address
  a CLR array element, or does the arm need a `mStack[objIdx] is Array` branch?
  Resolve via dump at apply (scopes (d) if the consumer change is large).
- **OQ4:** is the constrained-on-a-reference-type-`this` case (the box-once
  no-op) reachable by any smoke case? If yes it must be green; if no, defer with
  a tagged NIE.

## Resolution (apply phase, 2026-07-05)

### D1 = Option F' (NOT F, NOT R). The "JIT moves Constrained AFTER the callvirt" premise was WRONG.

A JIT body dump of `struct v; v.ToString()` showed the final order is
`[Push..., Constrained T, Callvirt M]` -- Constrained runs BEFORE the callvirt.
(The `lst.Add(old)` for Constrained happens during the Callvirt case at
JITCompiler.cs:1963, but the callvirt `op` is only `lst.Add`-ed at the end of
Translate at :2412, so Constrained ends up before it.)

So the runtime Constrained arm runs FIRST. It carries the type token
(`ip->Operand`) and reads the trailing callvirt at `ip+1` for the method token
(`cv->Operand2`), the param map (`cv->Operand`), and the return-slot info
(`cv->Register1` / `cv->DstOffset` / `cv->Operand3`). The arm OWNS the dispatch
and skips the trailing callvirt (`ip += 2`).

- **NO JIT change, NO operand stamping** -> the existing `Operand4` flag semantics
  are byte-identical for non-constrained callvirts (Risk 1 dissolved).
- The dump-confirmed callvirt shape for the constrained probe: `op.Code=Callvirt`,
  `Operand4=0x1` (just the constrained flag; `thisArgOffset = 0`, slot = 1 = bogus
  -- `InitializeCallvirtDispatch` was skipped at JITCompiler.cs:1922). Map slot 0:
  `primSrc=<byref temp offset>`, `size=4` (Object ref dest),
  `byref=(-1, structLocalOffset)`.

### D1 dispatch-shape discriminator (IL-vs-CLR, NOT box-vs-method)

- **IL value type + ILMethod override -> direct-call (no box).** Deref the byref,
  copy flat primitive bytes into `targetBase + ParamInfos[0].Offset`, ExecuteNeo.
  The override reads `this.field` via in-frame Ldfld_Inline. A Neo IL-struct
  method body CANNOT consume a boxed ILTypeInstance `this` (its JIT uses in-frame
  layout).
- **CLR value type -> box-once.** `NeoBoxReturnValue` (primitive) /
  `ReadNeoValueType` (struct); park on mStack; write index to callee slot-0;
  dispatch via `GetVirtualMethod`-resolved CLR override.
- **Already-boxed / ref-type `this` (objIdx>=0) -> no-op box.**
- IL-VT-WITH-ref-fields -> NIE (deferred to `neo-step17-stobj-refloop`; the byref
  does not carry the source struct's ref-region mStack base).

The override resolves via `constrainedType.GetVirtualMethod(targetMethod)` --
the constrained TYPE's concrete override.

### D4 (OQ3) resolution: `stind`/`ldind` `objectIndex >= 0` arm CANNOT address a CLR array element.

The arm calls `GetNeoILInstance(mStack, objIdx)`; a CLR `Array` is NOT an
ILTypeInstance. Resolution: Ldelema encodes `(arrIdx, elementIdx)` where `off` IS
the element index (NOT a byte offset); Stind_I4 / Ldind_I4 gained a
`mStack[objIdx] is Array` branch (`cArr.SetValue(v, off)` /
`(int)cArr.GetValue(off)`). Scoped to the green target (Stind_I4 / Ldind_I4 on a
CLR primitive array); other stind/ldind variants + ref-element arrays remain
NIE-tagged in their existing arms.

### D5 (Risk 3 / OQ2) resolution: NIE-guard. Box-once BYPASSES `CopyNeoCallArguments`.

The Constrained arm writes the boxed mStack index directly into the callee slot
(skipping slot 0 in the map copy). So the boxed-source branch of
`CopyNeoCallArguments` (`PrimitiveByRefSrc` with `objIdx >= 0`) is NOT reached by
the constrained path. The defensive `CopyBlock` was wrong (a boxed source's
`offset` is an mStack FIELD offset, not a struct address) -> replaced with a
tagged NIE-guard (no silent mis-copy). The map's slot-0 ref entry is meaningless
for the constrained byref source (reads a garbage mStack index from the byref
bytes) -- the Constrained arm's ref-copy loop SKIPS the leading `slot0RefCount`
ref entries (Object `this` -> RefCount=1).

### Actual edit sites (all Neo-only)

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`:
  - `case OpCodeREnum.Constrained:` -- replaced the NIE with the dispatch (reads
    `ip->Operand` + `cv = ip+1`; IL-VT direct-call / CLR box-once / already-boxed
    shapes; `ip += 2`).
  - `case OpCodeREnum.Ldelema:` `else` branch -- encode `(arrIdx, elementIdx)`
    for a CLR value-type-element array; NIE a ref-element array.
  - `case OpCodeREnum.Stind_I4:` / `case OpCodeREnum.Ldind_I4:` -- added a
    `mStack[objIdx] is Array` branch.
  - `CopyNeoCallArguments` boxed-source branch -- NIE-guard (was defensive
    `CopyBlock`).
  - `CopyNeoCallThisBack` comment -- tightened (mutating INSTANCE METHODS, not
    ctors).
  - Stale `Constrained` NIE text (T1) -- replaced with accurate description.
- `TestCases/NeoStep17Test.cs` -- 6 `NeoStep17_*` adversarial probes + an
  IL-VT-interface struct + generic constrained callers.

### Deviation from design

The design's D2/D3 (Box arm reuse + byref-through reader deref) was PARTLY
wrong: for an IL value type the box-once into an ILTypeInstance does NOT work
(the override's JIT body uses in-frame Ldfld_Inline and cannot consume a boxed
`this`). The dump revealed the discriminator is IL-vs-CLR: IL-VT uses direct-call
(flat bytes into slot 0), CLR-VT uses box-once. The IL-VT-WITH-ref-fields sub-case
NIE-defers (the byref does not carry the source ref-region base).

### Out-of-scope gap discovered

`ldflda`-on-in-frame-VT is a PRE-EXISTING gap (the Ldflda arm reads the operand
slot as an mStack objIdx; an in-frame VT slot holds flat bytes -> garbage). Any
IL-struct method taking a field address (`field.ToString()`, `ref field`,
`fixed`) is broken on Neo REGARDLESS of constrained. Route: new follow-up child
`neo-vt-ldflda-inline`. The constrained DISPATCH is validated by the interface
direct-call probe (which uses `Ldfld_I4_Inline`, not `ldflda`).

## Review-loop round 1 (F1 fix)

The non-author reviewer's adversarial probing (MANDATORY PROBE 1, finding F1/F2)
uncovered a **Blocker REGRESSION**: `anyIlStruct.ToString()` / `.GetHashCode()` /
`.Equals()` (and the `$"{ilStruct}"` interpolation -- the COMMON C# default
`object.ToString` for any IL struct with NO override) reached the box-once
branch with `actualMethod` = the inherited CLRMethod (`Object.ToString` /
`ValueType.GetHashCode`), and the generic `constrainedType.IsValueType && clrT
!= null` sub-branch called `ReadNeoValueType(clrT = ILTypeInstance, ...)`,
reading the struct's FLAT BYTES as an `ILTypeInstance` (a CLASS) -> corrupt
boxed receiver -> native segfault (exit 139, ToString) / NRE (GetHashCode).
Stash-toggle proved it a REGRESSION over HEAD's clean Step-17 tagged NIE. The
green apply-phase smoke (123/123) MISSED it because no probe exercised an
IL-struct + inherited-Object-method call (the implementer's IL-struct ToString
probe was replaced by the interface direct-call probe due to the unrelated
`ldflda` gap).

### Dump-gate (per OPT-HARDEN K1 discipline -- do NOT guess)

Constructed `struct IlStruct { int x; }` (no ToString override) + `ilStruct.ToString()`.
The JIT dump + runtime discriminator trace confirmed:
- `constrainedType` = the ILType for `IlStruct` (an IL value type).
- `constrainedType.GetVirtualMethod(Object.ToString)` returns the inherited
  **CLRMethod** (`Object.ToString`), NOT an ILMethod -- so the discriminator's
  `actualMethod is ILMethod` gate (the safe direct-call path) does NOT fire.
- The IL VT representation is flat primitive bytes in the caller's frame
  (`frameBase + thisByteOff`), `TotalPrimitiveSize` bytes, `TotalReferenceCount
  == 0` for the plain struct.
- The struct's `TypeForCLR` is `ILTypeInstance` (a reference type), so the
  generic `ReadNeoValueType(typeof(ILTypeInstance), ...)` reader is unsound for
  an IL VT (it interprets the struct's flat bytes as an `ILTypeInstance` shape).

### Fix chosen: CORRECT-FIX (not tagged-NIE)

The dump showed the correct fix is clean + low-risk, so the PREFERRED path was
taken: handle the IL-VT + inherited-CLRMethod box-once sub-case so
`anyIlStruct.ToString()` actually WORKS (high-value for debugging/logging),
rather than tagged-NIE'ing it back to HEAD's graceful degradation.

**Discriminator edit site** (`ILIntepreter.Neo.cs`, the Constrained arm's
box-once `else` branch):
1. A NEW sub-branch was inserted BEFORE the generic CLR-VT branch:
   `else if (constrainedType is ILType ilBoxType && ilBoxType.IsValueType)`.
2. **Box mechanism** (reuses Step 13's Box-arm machinery, NOT `ReadNeoValueType`):
   - If the IL VT has reference fields, NIE (the byref source does not carry the
     struct's ref-region mStack base -- mirrors the direct-call path's deferral
     to `neo-step17-stobj-refloop`).
   - Otherwise `ilBoxType.Instantiate(false)` -> a fresh `ILTypeInstance`, then
     `CopyFrameToIL(frameBase, thisByteOff, ..., ilBoxType.TotalPrimitiveSize,
     0 /*refCount*/, ..., ilBox)` copies the struct's flat primitive bytes into
     the box's `Primitives` array. `ilBox.Boxed = true`. This is the IL box
     representation (the same shape Step 13's `Box` opcode produces).
3. The resulting `boxedReceiver` (the `ILTypeInstance`) is parked on `mStack`
   and its index written into the callee slot-0 ref slot, exactly like the
   CLR-VT box-once path. The inherited CLRMethod (`Object.ToString` etc.) is
   then dispatched via `InvokeNeoClrMethod` on the boxed ILTypeInstance. For
   `Object.ToString`, the host `ILTypeInstance.ToString` override returns the
   type's full name (when no IL override exists) -- a useful non-null string.
4. **Blast-radius tightening**: the generic CLR-VT branch (`constrainedType
   .IsValueType && clrT != null`) gained a `&& !(constrainedType is ILType)`
   guard. Without it, an IL value type would still fall through and recur the
   F1 segfault. The guard makes the new IL-VT branch the SOLE handler for an
   IL value type in the box-once arm.

The (a) IL-VT-direct-call (override), (b) CLR-VT box-once, (c) already-boxed
paths are byte-identical (only a new branch was inserted + one guard added);
none of their inputs changed.

### Keeper probes added (TestCases/NeoStep17Test.cs)

Seven `NeoStep17_*` adversarial probes (NIE cases caught internally via
try/catch flag where applicable; here all are clean-return contracts):
- K1 `NeoStep17_ConstrainedIlVtInheritedToString` -- `ilStruct.ToString()` (F1
  segfault case, no override) -> non-null string, no crash.
- K2 `NeoStep17_ConstrainedIlVtInheritedGetHashCode` -- `.GetHashCode()` (F1
  NRE case) -> int, no crash.
- K3 `NeoStep17_ConstrainedIlVtInheritedEquals` -- `.Equals(other)` -> bool,
  no crash.
- K4 `NeoStep17_ConstrainedIlVtInheritedInterpolate` -- `$"{ilStruct}"` (the
  common interpolation pattern) -> non-null string, no crash.
- K5 `NeoStep17_ConstrainedIlVtOverrideStillWorks` -- an IL struct WITH an IL
  ToString override dispatched via a generic constrained caller -> verifies the
  IL-VT-direct-call (override) path is NOT perturbed (`"Named:42"`).
- K6 `NeoStep17_ConstrainedClrVtBoxOnceStillWorks` -- a CLR struct `.ToString()`
  via a generic constrained caller -> verifies the CLR-VT box-once path is NOT
  perturbed (`"(9,10,11)"`).
- K7 `NeoStep17_AddrAliasTwoConstrainedReuse` (F5 reconstruction) -- two
  simultaneous constrained inherited-method calls whose boxed temps reuse the
  register region, an intervening byref dest-reuse window, then re-read the
  folded fields -> verifies the Constrained dispatch does NOT perturb the
  addrAlias COEXIST gate (the Step-17-B1 silent-corruption class). F5 was
  previously blocked by F1; with F1 fixed it now runs cleanly.

### F5 (addrAlias COEXIST gate) reconstruction

With F1 fixed, the two-simultaneous-constrained-callvirts reuse probe (K7) runs
cleanly: folded field values (`p.a == 100`, `p.b == 200`), the byref dest (`x
== 50`), and both boxed-receiver strings survive. The Constrained arm touches
NO optimizer/lowering code (D1 = F' = no JIT change), so the addrAlias /
liveAliasMap machinery is byte-identical; the gate is NOT perturbed by the F1
fix. Independent confirmation achieved (previously blocked by F1's crash).

### Smoke (re-verified)

- NeoStep: **130/130 green** (123 baseline + 7 new keepers), exit 0.
- NeoOptHardening: **16/16 green**, exit 0.
- Plain `Debug` CLI builds clean (0 errors) -- Legacy-neutral confirmed.

### F3 / F4 disposition (Minor)

- **F3** (slot0RefCount source): for the green target the call-site declaring
  type (`Object`, RefCount=1) is the correct source -- the box-once writes
  exactly one boxed receiver ref into slot 0, and the skip of the leading
  `slot0RefCount` ref entries correctly drops the (meaningless) source byref ref
  bytes. The override-resolves-constrained-type case is empirically correct
  (K6 + the implementer's override-resolves probe). No fix; the F1 dump
  confirmed the slot-0 ref-skip is sound for the box-once shape. Left as a
  comment-level note.
- **F4** (Stind/Ldind CLR-array: only I4 handled): LEFT AS-IS. Matches the
  design's explicit scoping ("green target = Stind_I4/Ldind_I4 on a CLR
  primitive array; other variants remain NIE-tagged in their existing arms").
  Recorded as an accepted-known boundary, not fixed (the Ldelema arm's
  `IsValueType` guard prevents a ref-element array from reaching the consumer;
  the untagged cast failure for an unhandled width is the documented edge, not
  silent corruption). Route any future widening to `neo-step17-misc` /
  `array-completion`.
