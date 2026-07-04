## Context

Neo Step 8b implemented IL **reference-type** `newobj`: the `ExecuteNeo` `Newobj`
arm allocates `new ILTypeInstance(type)`, stores it in the dest mStack ref slot,
writes the mStack index into the callee param region's `this` slot
(`*(int*)targetBase = newobjDstIdx`), copies the remaining args via
`CopyNeoCallArguments`, pushes a fresh mStack slot for the ctor's own `this`, and
calls the IL ctor with `isNewobj=true`. The dest register holds the new object.

Two `newobj` paths are still open, plus one pre-existing lowering quirk:

1. **CLR type newobj** -- the arm unconditionally does
   `var newobjType = targetMethod.DeclearingType as ILType; if (newobjType == null)
   throw NIE("Step 9")` (`ILIntepreter.Neo.cs:1598-1600`). Every CLR type
   instantiation from IL throws.

2. **IL value-type newobj** -- there is no `IsValueType` branch; the arm always
   heap-allocates an `ILTypeInstance` and passes its mStack index as `this`.
   For a value type the result must live in the **caller's frame** (the dest
   register's byte region), and the ctor's `this` must be an address into that
   region so `this.field =` writes back to the frame.

3. **Q-NEWOBJ** -- `new T(intArg)` works in isolation (Step 16 TC7 green) but is
   silently wrong when it **immediately follows a `newarr`** (Step 16 TC4 worked
   around it with a default ctor + field set).

The Step 17 byref/Ref-Slot model is the prerequisite for (2): a frame-native
Ref Slot `(-1, frameByteOffset)` is exactly the address a value-type ctor `this`
needs. The Step 13b unified CLRMethod param layout is the prerequisite for (1):
`CLRMethod.Invoke(byte*, mStack, isNewObj=true)` already does
`cDef.Invoke(param)` and reads params via `ReadNeo*`; only the `ExecuteNeo`
routing is missing.

Legacy (`ExecuteR` `Newobj`, `ILIntepreter.Register.cs:3359-3607`) is the
semantics reference: IL-VT uses `AllocValueType` + a `StackObjectReference`
`this` that the ctor writes through; CLR uses `cm.Redirection`(isNewObj) or
`cm.Invoke(..., true)`. Neo uses frame + Ref Slot, not `StackObject`; Legacy is
NOT modified.

## Goals / Non-Goals

**Goals:**
- IL value-type `newobj`: `new MyILStruct(args)` produces a correctly-initialized
  value in the caller's frame slot, including a ctor that sets
  `this.field = value` via the Ref-Slot writeback.
- CLR-type `newobj`: `new SomeClrType(args)` allocates and constructs a CLR
  object (Neo Redirection when present; reflection otherwise) and the dest
  register holds the new object.
- Q-NEWOBJ: root-cause and minimally fix the `new T(intArg)`-after-`newarr`
  regression; restore the TC4 form to a real ctor-with-arg.
- Full NeoStep smoke stays green (84/84 baseline); NeoStep18 cases added.

**Non-Goals:**
- Delegate `newobj` (`new Action(foo)`) -- Step 19 (needs `ldftn`/DelegateAdapter).
- CLR value-type `newobj` with reference fields and no ValueTypeBinder via the
  reflection path (Step 13b NIE guard).
- Generic-parameter VT newobj.
- Whole-value-type newobj of a CLR value type stored flat in the frame (a CLR
  VT `newobj` result is a boxed object on mStack, matching Legacy reflection
  semantics; a binder-backed flat CLR VT is a separate concern).

## Decisions

### D1. IL value-type newobj: frame slot as the construction site

The dest register of a value-type `newobj` is a value-type temp allocated by
`AllocateLocalStackSpaces` with `Size = ilType.TotalPrimitiveSize` and
`RefCount = ilType.TotalReferenceCount` (the existing temp-slot sizing already
accommodates the max VT, so the dest region is already correctly sized/aligned
for the new value type). The construction steps:

1. **Zero-init the dest region** at newobj time: `InitBlock` the
   `TotalPrimitiveSize` bytes at `frameBase + destOffset`, and null every one of
   the dest's `TotalReferenceCount` mStack ref slots (`frameRefBase + destRefOffset
   + i`). This matches the `Initobj` semantics (`neo-value-types` "Initobj memset")
   and the CLR value-type default state. Rationale: a value type with no
   parameterless init still needs its untouched fields zeroed; the ctor may only
   set some fields.

2. **Pass `this` as a frame-native Ref Slot.** The ctor is an IL method whose
   `this` (param slot 0) must be a managed pointer to the dest region. We write
   an 8-byte Ref Slot `(-1, destFrameByteOffset)` into the callee param region
   at the `this` slot:
   ```
   *(int*)(targetBase + thisPrimOff + 0) = -1;                    // frame-native
   *(int*)(targetBase + thisPrimOff + 4) = destFrameByteOffset;   // absolute offset
   ```
   where `thisPrimOff`/`thisRefOff` come from the ctor's `CompiledFrame.ParamInfos[0]`
   (already laid out for an IL method). The ctor is then invoked via
   `InvokeNeoCallTarget(ctorMethod, isNewobj=true, targetBase, ...)` exactly like
   the IL ref-type path -- only the `this` payload differs (a Ref Slot vs an mStack
   index).

3. **No mStack push of a `this` object.** The IL ref-type path does
   `mStack.Add(mStack[newobjDstIdx])` to give the callee a fresh `this` ref slot.
   For the value-type path the `this` is an 8-byte frame-native address (no
   object), so the callee's `this` ref slot (if `ParamInfos[0].RefCount > 0`) is
   left at the frame's natural state -- see "ctor this ref slot" below.

4. **Ctor writeback is automatic.** Because `this` is a frame-native Ref Slot,
   the ctor's `stfld`/`stind` on `this.field` resolves through the existing
   Step 17 stind/ldind + Step 12 _Inline machinery to writes at
   `frameBase + destFrameByteOffset + fieldOffset` -- i.e. directly into the
   caller's frame slot. No post-ctor copy-back is needed (contrast Legacy, which
   copies `*reg1 = *ins` after the ctor; Neo's address model makes the copy
   implicit).

**ctor this ref slot.** An IL value type with reference fields has
`TotalReferenceCount > 0`; its ctor's `this` param slot therefore has
`RefCount > 0` in `ParamInfos[0]`. The ctor reads/writes those ref fields via
the Ref Slot's `(objectIndex, offset)` -> since `objectIndex == -1`, the stind/ldind
frame-native path applies and the ref fields live in the **caller's** dest ref
slots. The Ref Slot's `offset` half is the primitive base; the ref fields are at
`destRefOffset + fieldRefOffset` in the caller's frame. This is consistent with
the Step 17 in-frame-VT ref-field model. (If the ctor's `this` Ref Slot needs to
also encode the ref base, the design uses the frame-native convention where the
ref slots are addressed by the owning slot's `RefOffset` -- the stind/ldind arm
already resolves ref fields of a frame-native address through the dest register's
ref region. Confirm during apply that the ctor's JIT lowers `this.field`-ref
through the in-frame path; if not, the minimal extra step is to make the newobj
value-type `this` a Ref Slot whose offset resolves ref fields correctly.)

**Why not allocate a heap ILTypeInstance for the VT and copy back?** That would
introduce a format-conversion hop (the explicit anti-pattern called out in
CLAUDE.md: "two object models must not be mixed -- value-type field access would
degrade via format conversion"). The frame-native Ref Slot keeps the value in
the frame end-to-end.

### D2. IL value-type newobj: JIT `this`-as-byref param layout

The ctor's callee param region is built by the `Optimizer.Neo.cs` Call/Newobj
lowering from `ILMethod.CompiledFrame.ParamInfos`. For a value-type ctor,
`ParamInfos[0]` (the `this`) must be an **8-byte byref slot** (a Ref Slot),
not a 4-byte object index. The IL ctor's own `InitializeMethods`/frame layout
already sizes a value-type `this` parameter -- verify during apply that
`ParamInfos[0]` for a value-type ctor is 8 bytes with the byref shape
(`Size == 8`, treating the 8 bytes as the address carrier). If the existing IL
ctor frame layout sizes the value-type `this` as the value (in-frame copy) rather
than a byref, the design chooses the **byref** form (matching the Step 17 byref
call-ABI) because the ctor must write back to the caller's slot, not a callee
copy. This is the single JIT-side decision point for D1; it reuses the Step 17
byref-param machinery, not a new ABI.

### D3. CLR type newobj: route to InvokeNeoClrMethod

Remove the blanket CLR NIE. When `targetMethod.DeclearingType` is a `CLRType`:

1. Resolve `CLRMethod clrCtor = targetMethod as CLRMethod`.
2. Set up `targetBase` param region exactly as the lowering already does for a
   CLR Newobj (the `Optimizer.Neo.cs` CLR branch builds `paramInfos` with a
   `this` slot at [0] and the ctor args after). The newobj does NOT pre-fill the
   `this` slot -- for CLR newobj the object does not exist yet.
3. Call `InvokeNeoClrMethod(clrCtor, isNewobj: true, targetBase, mStack,
   retDstPtr: null, targetRetRefBase: destRefOffset)`.
   - **With Neo Redirection:** the redirect delegate runs with `isNewobj=true`
     and is responsible for allocating the object. Per the Step 9 contract the
     redirect writes the new object into the dest ref slot
     (`mStack[targetRetRefBase] = newObj`; the dest byte offset holds the index).
     (The autogen CLR binding codegen already emits a newobj-capable redirect
     body for bound types; confirm during apply.)
   - **Without Neo Redirection:** `clrCtor.Invoke(targetBase, mStack, true)`
     returns the new object (`cDef.Invoke(param)`, Step 13b). `InvokeNeoClrMethod`
     then stores the result: it is a reference type, so the existing
     `InvokeNeoClrMethod` return-store logic does `mStack[targetRetRefBase] = res`
     and `*(int*)retDstPtr = targetRetRefBase` -- BUT for newobj,
     `InvokeNeoClrMethod` currently early-returns on `isNewobj`
     (`if (isNewobj || retDstPtr == null) return;` at line 274). The design
     changes this: for the **reflection** newobj path, the returned object MUST be
     stored into the dest ref slot. The cleanest split:
       - `InvokeNeoClrMethod` keeps the early-return ONLY for the redirect path
         (the redirect owns the dest write).
       - For the reflection newobj path, store `res` into the dest mStack ref
         slot and write the index to the dest byte offset, mirroring the
         reference-type return store. Pass `retDstPtr = frameBase + destByteOff`
         and `targetRetRefBase = destRefOff` so the existing store logic handles
         it; remove the `isNewobj` early-return for the reflection branch.

**dest layout.** A CLR newobj dest is a reference temp (4-byte mStack index +
1 ref slot), identical to the IL ref-type newobj dest. No new dest shape.

### D4. Q-NEWOBJ root cause

> **APPLY-PHASE UPDATE (2026-07-04):** The Q-NEWOBJ reproducer (`newarr; new
> T(intArg); assert`) **PASSES on current HEAD** -- the quirk is NOT
> reproducible. A JIT dump of `localInfos` for the newobj shows the dest
> register, the newarr array temp, and the int arg each get a DISTINCT frame
> byte region (`Offset`) and a DISTINCT mStack ref slot (`RefOffset`): e.g. for
> `new NeoStep18Item(7)` after `new NeoStep18Item[3]`, dest=r1(off=4,ref=1),
> array=r0(off=0,ref=0), arg=r11(off=44,ref=3). The planner's hypothesis
> (newarr doesn't decrement baseRegIdx -> the array temp collides with the
> newobj dest/arg in the mStack ref region) is **disproven**: the JIT/Optimizer
> `AllocateLocalStackSpaces` already allocates a distinct region + ref slot for
> every register, so no aliasing occurs. This matches the OPT-HARDEN outcome
> for Q-STRUCT / Q-LONG (suspected quirk already gone on HEAD; Steps
> OPT-HARDEN/13b/17 likely resolved it). **No fix was applied** (the instruction
> was explicit: do not force a guessed fix to the shared call/newobj lowering
> without a reproducing case). Step 16 TC4 was restored to the real
> ctor-with-arg form and passes. Marked RESOLVED (non-reproducible) in
> `neo-deferred-items.md`. The original (pre-apply) hypothesis is preserved
> below for the record.

**Symptom:** `new T(intArg)` alone works (Step 16 TC7). `newarr X; new T(intArg)`
immediately after is silently wrong (Step 16 TC4). 0 diff lines in the
Call/Newobj lowering were added by Step 16, so the bug is pre-existing
(Step 10/11 territory) and only exposed by the newarr+newobj sequence.

**Suspected site (to confirm with a JIT-dump reproducer during apply):** the JIT
`Newobj` lowering's dest/arg register convention
(`JITCompiler.cs:1759-1760`):
```
baseRegIdx -= (short)pCnt;       // pop the args
op.Register1 = baseRegIdx++;     // dest REUSES the just-popped arg's slot
```
For `new T(intArg)` (pCnt=1), the dest register is the **same register** that
held `intArg`. This is normally fine (consume-args-produce-result-on-top). The
collision with a preceding `newarr`: `newarr` does NOT decrement `baseRegIdx`
(consumes count, produces array, net 0), so the array temp sits at the register
the subsequent `newobj` arg lands in / adjacent to. When the `Optimizer.Neo.cs`
Call/Newobj lowering scans back for Push instructions and resolves `srcRegs` +
the dest `Register1`, the dest-register-reuse interacts with the array temp's
still-live frame metadata: specifically the dest's `DstOffset`/`Operand3` (ref
offset) is stamped from `localInfos[op.Register1]`, and if that register's slot
still carries the `newarr` dest's array ref in the mStack ref region, the newobj
dest write clobbers the array reference (or vice-versa) before/around the ctor
call.

**Confirmation step (apply phase, before editing):** add a minimal reproducer to
NeoStep18 (the TC4 form: `newarr; new T(intArg); assert(T.field == intArg)`),
dump the JIT body + `localInfos` for the method on current HEAD, and confirm
which slot aliasing (frame byte overlap, ref-slot overlap, or the dest being
read as an arg) produces the wrong value. The deferred-items note already
records the symptom is in "the call/newobj Push-scanning lowering" and that
`new T(intArg)` alone is green -- pinning the exact alias needs the dump.

**Fix shape (minimal, localized):** once the alias is confirmed, the fix is in
one of:
- (a) `Optimizer.Neo.cs` Newobj dest handling: ensure the dest's ref slot is
  written with the NEW object's index AFTER the ctor call (not before), so a
  stale array ref in the same register's ref slot cannot be observed -- OR
  ensure the dest register's ref slot is reserved distinct from the array temp's.
- (b) `JITCompiler.cs` Newobj dest: do not reuse the popped-arg register as the
  dest when a preceding same-frame temp is still live (allocate a fresh dest
  register). This is the higher-risk option (touches the shared register
  convention) and is the fallback only if (a) is infeasible.

The fix MUST be validated by: TC4-restored form green; TC7 (newobj-arg in
isolation) still green; full NeoStep smoke green; no Call/Callvirt regression.

**Why fold into Step 18:** Step 18 owns newobj completion; Q-NEWOBJ is a newobj
quirk; fixing it here avoids a separate step and lets TC4 use a real
ctor-with-arg (the cleaner test).

### D5. Edge cases

- **Default ctor (parameterless) IL VT:** pCnt=0, no args; dest zero-init; `this`
  Ref Slot written; ctor runs (may be a no-op or set defaults). Covered.
- **IL VT ctor with args:** pCnt>0; args copied via `CopyNeoCallArguments` into
  param slots [1..]; `this` Ref Slot at slot [0]. Covered.
- **Base ctor chain:** a VT ctor `: base(...)` calls its base IL ctor with
  `this` propagated (the same frame Ref Slot). The base ctor's `Call` passes
  `this` as a normal (already-byref) first param -- no newobj re-allocation.
  Verify during apply that the chained base-ctor `Call` forwards the byref `this`
  unchanged (it should, since the ctor's `this` local is already a Ref Slot).
- **CLR generic type newobj** (`new List<int>()`): the CLR ctor's generic
  instantiation is resolved by `AppDomain.GetMethod`; the reflection
  `cDef.Invoke` handles the closed generic ctor. Covered (no special-casing).
- **CLR type with Neo Redirection** (`new` a bound CLR type, e.g. a bound
  `List<T>` or a redirected ctor): the redirect runs with `isNewobj=true`.
  Covered; confirm the autogen redirect writes the dest.
- **`throw new SomeClrException()`**: this is `newobj` of a CLR exception type
  followed by `throw`. CLR newobj (D3) allocates the exception object into the
  dest mStack slot; `throw` then surfaces it. This MAY turn previously-failing
  exception tests green (side-benefit, not a hard requirement; note any such
  test in the ship log).

## Risks / Trade-offs

- **[Q-NEWOBJ fix touches the shared Call/Newobj lowering]** -> Mitigation: full
  NeoStep smoke (84/84) is the gate; the fix is validated in isolation (TC4/TC7)
  before the smoke run; the higher-risk JIT-dest-register change (D4 option b)
  is the fallback only if the localized optimizer fix (option a) is infeasible.
- **[IL-VT ctor `this` ref-field resolution]** -> Mitigation: D1 step 4 + the
  "ctor this ref slot" note call out that the ctor's JIT must lower
  `this.field`-ref through the in-frame path. If a reproducer shows the ref
  fields are not written back, the minimal extra step is encoding the dest ref
  base into the Ref Slot. The design does not preclude this; it is the fallback.
- **[CLR newobj redirect dest-write contract]** -> Mitigation: D3 splits the
  early-return so the reflection path stores the result and the redirect path
  owns its dest write; confirm with a redirected-ctor test (e.g. a bound
  `List<T>()` if available, else a hand-redirected ctor).
- **[value-type newobj of a type with no parameterless init]** -> Mitigation:
  D1 step 1 zero-inits unconditionally, so even a ctor that sets only some
  fields leaves the rest at the CLR default. Matches Legacy `AllocValueType`
  semantics.
- **[Q-NEWOBJ root cause unconfirmed at propose time]** -> Mitigation: the
  apply phase opens with a JIT-dump reproducer to pin the exact aliasing before
  any lowering edit; the deferred-items note already localizes the symptom. If
  the dump shows a different root cause than the hypothesis, the fix shape is
  adjusted to the confirmed cause (the proposal/design name the suspected site,
  not a fixed patch).

## Open Questions

- Does the IL value-type ctor's `CompiledFrame.ParamInfos[0]` already size `this`
  as an 8-byte byref, or as the in-frame value? (Resolved during apply via a
  frame-layout dump; D2 chooses byref either way.)
  **RESOLVED (apply):** it is sized as the **in-frame value** (`Size =
  TotalPrimitiveSize`, `RefCount = TotalReferenceCount`), NOT an 8-byte byref
  (`JITCompiler.cs:1332-1344`, keyed on `declaringType.IsValueType`). This is
  the Step 12 layout. The D2 byref change is therefore required for D1 to work
  -- see the apply findings below.
- Does any NeoStep smoke case (or the broader suite) currently exercise
  `throw new SomeClrException()` that would turn green with D3? (Noted as a
  side-benefit; checked at verify time.)
  **RESOLVED (apply):** YES. `throw new InvalidOperationException(...)` +
  try/catch is now GREEN-testable (NeoStep18 TC7). CLR newobj (D3) allocates the
  exception object; `throw` surfaces it; the catch runs.

## Apply-phase findings (2026-07-04)

**Shipped.**
- **CLR-type newobj (D3):** DONE. The blanket CLR NIE is removed; the Newobj arm
  routes `CLRType`-declared ctors to `InvokeNeoClrMethod(isNewobj:true, ...)` with
  `retDstPtr = frameBase + destByteOff` and `targetRetRefBase = newobjDstIdx`.
  The `InvokeNeoClrMethod` early-return was split: the **reflection** path now
  stores the returned object into the dest mStack ref slot + writes the index to
  the dest byte offset (mirroring the reference-type return store); the
  **redirect** path keeps its early-return (the redirect owns the dest write).
  `new List<int>()` and `new Dictionary<int,string>()` construct and are usable
  (TC5, TC6 green). Side-benefit: `throw new ClrException` + try/catch works (TC7
  green). NOTE: a CLR ctor WITH a registered Neo Redirection is JIT-lowered to
  `Call_Redirect` (which has no `ExecuteNeo` arm) -- that path is unchanged by
  this step (the Newobj arm only sees non-redirected CLR newobj, which stays as
  `Newobj`).
- **Q-NEWOBJ (D4):** RESOLVED as non-reproducible -- see the D4 update above. No
  code change. Step 16 TC4 restored to the real ctor-with-arg form.

**Deferred (with a clear Step-18-tagged NIE in the Newobj arm, not a silent
wrong result).**
- **IL value-type newobj (D1/D2):** BLOCKED on a pre-existing VT field-access
  lowering inconsistency. A value-type ctor's `this`-relative `stfld` lowers to
  a MIX of in-frame `_Inline` (writes the callee frame bytes at
  `frameBase + thisOffset + fieldOff`) and heap `Stfld_*`/`GetNeoILInstance`
  (treats `this` as an mStack index -> ILTypeInstance). The caller's subsequent
  field reads on the newobj result are likewise non-inline (expect an mStack
  object index), not in-frame reads of the dest bytes. The `addrAlias` folding
  only tracks `ldloca`-produced addresses, not a `this` param or a newobj dest,
  so the VT representation is inconsistent end-to-end.
  - Confirmed empirically: `new NeoStep18Big(seed)` (5-field ctor) lowers its
    first stfld to `_Inline` and the rest to heap `Stfld_I4`; the caller's
    `ldfld` on the result is non-inline. A single-field ctor is all-inline but
    the caller's read is STILL non-inline.
  - A heap-alloc + copy-back fallback (Legacy-style) is ALSO infeasible without
    first fixing the consistency: the ctor's inline stflds write the callee
    frame while its heap stflds write the ILTypeInstance, so the two diverge.
  - Making this work is the **D2 JIT change**: a value-type `this` (and a VT
    newobj dest) must be tracked as an in-frame address for ALL field access
    (ctor writes + caller reads). This touches the Step 12 VT frame layout /
    the field-access lowering shared by EVERY VT instance method -- too broad
    and risky for this step (the 84/84 -> 91/91 smoke is the gate).
  - Note: the C# compiler lowers `VT x = new VT(args)` on a local to
    `ldloca + call ctor`, which hits the SAME VT-`this` field-access issue
    (TC3b); that path is likewise deferred. Recorded as **Q-VT-NEWOBJ** in
    `.trae/documents/neo-deferred-items.md` for a dedicated follow-up (the D2
    VT-`this`-as-in-frame-address change).

**Smoke.** FULL NeoStep smoke: **91/91 green, 0 failed** (baseline 84/84; the +7
= the 7 NeoStep18 cases). No regression. No previously-failing NeoStep case
turned green as a side-benefit beyond the new TC7 (which is new, not
pre-existing). Delegate newobj, generic-param VT newobj, and no-binder
CLR-VT-with-refs newobj remain NIE (unchanged). Legacy (`ExecuteR`) untouched;
all Neo code behind `#if ENABLE_NEO_MODE`.
