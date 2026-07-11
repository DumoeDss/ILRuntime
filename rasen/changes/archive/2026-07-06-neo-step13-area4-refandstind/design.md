## Context

The Step 13 Area 4 cohort (`neo-step13-area4`, 2026-07-05) shipped the value-
type-`this` direct-call (4b) and the `Unsafe.Unbox<T>` boxed direct-call (4a)
and deliberately scoped OUT two independent plumbing pieces — the explicit 13b
"don't bundle, the diff becomes unreviewable" lesson. This change closes the
remainder of D-13B Area 4: **4c** (a CLR method with `ref`/`out` params — the
typed-ref bridge) and **4d** (`stind_*`/`ldind_*`/`stobj`/`ldobj` on a byref to
a CLR OBJECT field — the field-hash path). Both are pre-existing gaps verified
on HEAD (`2ca6614f`).

**Current state (code-grounded).**

- **4c CLR-method ref/out.** A CLR method call from IL flows through TWO
  readers that must agree on the callee param region: (1) the reflection
  fallback `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:333-522`) when no
  `RedirectionNeo` autogen delegate is registered, and (2) the autogen
  `*_Neo` redirect delegate whose body is emitted by
  `GenerateMethodWraperCode_Neo` (`MethodBindingGenerator.cs:249`) via
  `AppendArgumentCodeNeo` (`BindingGeneratorExtensions.cs:135`). Both walk a
  contiguous callee param region whose byref-typed slots are sized 8 bytes
  (the Ref Slot `(objectIndex, offset)`) by `AllocateNeoCallParamSlot` -> the
  `IsByRef` branch of `AllocateSlotForType` (`JITCompiler.cs:1659`). The
  caller copy (`CopyNeoCallArguments`, `ILIntepreter.Neo.cs:314-356`) copies
  the 8 Ref-Slot bytes verbatim (no deref for a byref PARAM — only the VT
  `this` slot carries the `PrimitiveByRefSrc` flag).
  - **Reflection fallback current failure:** `CLRMethod.Invoke`'s param loop
    (`:407-474`) has NO `IsByRef` check. For a `ref int` param,
    `t = pt.TypeForCLR` strips the byref modifier -> `int` -> reads 4 bytes via
    `ReadNeoInt32` = the Ref Slot's `objectIndex` half = silent-wrong; no
    write-back (CLR `MethodInfo.Invoke` mutates the boxed copy and returns).
  - **Autogen current failure:** `AppendArgumentCodeNeo` (`:173-177`) emits
    `{realClsName} {varName} = default({realClsName});` + a `// TODO: ByRef
    parameters in Neo (CLR-method ref/out: Step 13b DEFERRED ...)` comment for
    `pt.IsByRef` = silent-wrong; no write-back.

- **4d CLR-object stind/ldind.** A `ref clrObj.field` (a CLR object field
  address) is produced by the `Ldflda` arm's `else` branch
  (`ILIntepreter.Neo.cs:877-883`) as `(objIdx, fieldPrimOff)` where
  `fieldPrimOff = ip->Operand2 = field.PrimitiveOffset`. But a CLR object has
  no `Primitives[]` byte array, so `field.PrimitiveOffset` is meaningless for
  a CLR field. The `stind_*`/`ldind_*`/`stobj`/`ldobj` consumer arms'
  `else` branch (the non-frame-native, non-Array case) calls
  `GetNeoILInstance(mStack, objIdx)` (`:3890-3903`) which throws a clean
  `NotImplementedException` ("Step 17/13b: field/element access on a CLR object
  via the IL-instance path is deferred (CLR field-hash plumbing lands in Step
  13b)"). So 4d is a **clean NIE today**, not an `InvalidCastException`.

**Prerequisite machinery (all shipped, REUSE — do not rebuild):**
- Step 17's 8-byte Ref Slot `(objectIndex, offset)` model (`AllocateSlotForType`
  `IsByRef` branch).
- `CopyNeoCallArguments`'s `PrimitiveByRefSrc` deref-at-copy-site pattern
  (4b) — the template for a byref-aware reader.
- `ReadNeoValueType`/`WriteNeoValueType` + `GetNeoValueTypeManagedSize`
  (13b) — the typed read/write of flat-bytes CLR structs (for `ref struct`).
- The `mStack[objIdx] is Array` consumer branch (step17-completion +
  array-completion) — the template for a new `else` discriminator branch.
- `ILIntepreter.ReadNeoInt32`/`WriteNeoValueType` etc. — the cursor primitives.

**Legacy is the REFERENCE.** Legacy `AppendArgumentCode`
(`BindingGeneratorExtensions.cs:239`) handles byref via the
`hasByRef`/`shouldFreeParam=false` + `WriteBackInstance` mechanism; Legacy
`CLRMethod.Invoke(intepreter, esp, mStack)` (the StackObject path) handles
byref via `Reference*` pushees. Neither is modified; only the `*Neo` variants
and the Neo runtime are touched.

## Goals / Non-Goals

**Goals:**
- **4c:** A CLR method with `ref`/`out` params (primitive, reference-type, and
  pure-primitive-binder-struct element types) called from IL works end-to-end:
  the deref-read passes the referent's value to the CLR method, and the
  write-back propagates the mutation / `out` assignment to the caller's local
  or heap object. Both readers (reflection fallback + autogen) fixed.
- **4d:** `ref clrObj.field` via `ldflda` -> `stind_*`/`ldind_*`/`stobj`/
  `ldobj` reads/writes the CLR field via its reflection accessor, for a CLR
  object with primitive and reference-type fields. No NIE.
- Both close the D-13B Area 4 deferral and the 4d piece of the F-7 obligation
  (the byref-marshaling primitives are factored so F-7's delegate `Invoke`
  byref param can reuse them — F-7 wiring itself is a Non-Goal unless the diff
  is small at apply).
- Neo-only; Legacy byte-identical.

**Non-Goals:**
- A CLR value type WITH reference fields and no `ValueTypeBinder` passed by
  `ref`/`out` (GC refs unmappable without a binder) — stays a tagged NIE
  (same constraint as the by-value CLR-struct-param path).
- Generic-byref (`ref T`/`out T` with `T` a generic parameter) — Step 17
  deferral, untouched.
- `stobj`/`ldobj` ref-slot loop for a VT WITH reference fields (Step 17
  stobj-refloop follow-up, task #18).
- The `callvirt`/`constrained.callvirt` shape on a CLR struct (closed by
  neo-step17-completion; not a 4c/4d concern).
- F-7 delegate `ref`/`out` `Invoke` wiring (`DelegateAdapter.NeoInvokeSub` /
  `WriteNeoCallSlot`) — IN scope ONLY IF the byref-marshaling primitives factor
  cleanly into a shared helper AND the delegate wiring is a small additive
  call; otherwise it splits to a dedicated follow-up (see Scoping below).

## Decisions

**D1 — Scoping: SHIP 4c + 4d together; F-7 deferred unless it falls out.**
4c and 4d are independent plumbing (a typed-ref bridge; a field-identity
scheme) that do NOT share fix sites, but they are each SMALL (4c = a byref arm
in each of 2 readers + a write-back epilogue; 4d = a new `else` branch in the
stind/ldind/stobj/ldobj arms + a CLR-field-identity stamp in `Ldflda`). The
two together stay reviewable. F-7 (delegate `ref`/`out`) depends on the SAME
byref-marshaling primitives as 4c but lives in a third site
(`DelegateAdapter.NeoInvokeSub`); it is in scope as a stretch ONLY if 4c's
helper is generic enough to call directly from `WriteNeoCallSlot`. Default
lean: ship 4c + 4d, defer F-7 to its own follow-up child (recorded so the
planner finds it). This mirrors the portfolio's "宁可串行也不能乱并行" + the 13b
"don't bundle" lesson — 4c+4d is one cohesive CLR-binding/byref completion;
F-7 is a delegate-callback-path concern.

**D2 — 4c reader discriminator: `pt.IsByRef` on the `IType` (reflection) and
`ParameterType.IsByRef` on the CLR `ParameterInfo` (autogen).** The byref-ness
is determined unconditionally by the parameter's type token (the per-arm
type-token insight from 13b / opt-harden-2: no per-slot runtime flag is
stamped at lowering). For the reflection fallback, the param loop branches on
`pt.IsByRef` BEFORE the existing primitive/struct/ref dispatch. For the
autogen, `AppendArgumentCodeNeo` already computes `pt = p.IsByRef ?
p.GetElementType() : p` (`:140`); the new arm fires when `p.IsByRef` (note:
the existing `pt.IsByRef` check at `:173` is dead because `pt` is already
de-byref'd — the discriminator must be on `p.IsByRef`, not `pt.IsByRef`; this
is a load-bearing correctness detail for the codegen).

**D3 — 4c deref + write-back mechanism (frame-native vs mStack-object).** The
byref param's Ref Slot is `(objectIndex, offset)`:
- `objectIndex == -1` (frame-native): the referent is `*(T*)(frameBase +
  offset)`. Deref-read via the existing `ReadNeoInt32`/`ReadNeoValueType`/
  `ReadNeoReference` cursor (positioned at `offset`). Write-back via the
  corresponding `WriteNeo*` at `offset`. This covers `ref localInt` and
  `ref localStruct`.
- `objectIndex >= 0` (mStack-object): the referent is a field of the mStack
  object. Deref-read/write via the SAME field-identity mechanism as 4d (D4) —
  so 4c and 4d share the field-accessor primitive. This covers
  `ref heapIlObj.field` and `ref clrObj.field`.

This unification is the load-bearing design insight: **4c's mStack-object
byref and 4d's `ldflda`-produced CLR-object Ref Slot are the SAME operation**
(read/write a CLR or IL field via a field identity resolved from the Ref
Slot). Factoring it into a shared `ReadNeoFieldRef`/`WriteNeoFieldRef` helper
keeps both arms small and consistent.

**D4 — 4d CLR-field-identity stamp in `Ldflda`.** The `Ldflda` arm's `else`
branch (heap-IL / CLR-object) currently stamps `(objIdx, field.PrimitiveOffset)`.
For a CLR operand, stamp a CLR-field identity instead. Two options:
- **Option A (PREFERRED): resolve the CLR `FieldInfo` at JIT time** (the field
  is known statically — `Ldflda` carries the field token in `Operand`). Stamp
  the `FieldInfo`'s `MetadataToken` (a stable int) into the offset half, and
  keep a domain-level `Dictionary<int, FieldInfo>` cache. The consumer resolves
  the token -> `FieldInfo` -> `fieldInfo.GetValue(obj)`/`SetValue(obj, v)`.
  Pros: stable, reflection-friendly, no GC handle. Cons: a `MetadataToken` is
  only unique within its module — must key the cache on
  `(module, token)` or store a `FieldInfo` directly via a domain cache index.
- **Option B: stamp a domain-cached field-handle index.** The JIT registers
  the `FieldInfo` in a domain-level list at compile time and stamps the list
  index into the offset half. The consumer indexes the list. Pros: simplest
  runtime lookup (an array index). Cons: a new cache + a new JIT-time
  registration step; more surface than A.

**Lean A; the `FieldInfo` is already resolved by the JIT's field lookup (the
`Ldflda` `Operand` decodes to an `IField` whose `FieldInfo` is available via
the CLR field type).** Pin at apply via a JIT-dump probe of the `Ldflda`
operand for a CLR field — confirm the `FieldInfo` is resolvable at JIT time
and the offset half is free (not used by the consumer for a CLR operand). The
existing `field.PrimitiveOffset` stamp for an IL heap field stays — only the
CLR-field sub-case changes.

**D5 — 4c write-back gating (`ref`/`out` vs `in`).** The reflection fallback
must NOT write back a `readonly`/`in` param (CLR contract forbids mutation).
Gate the write-back on `!ParameterInfo.IsIn || ParameterInfo.IsOut` (a `ref`
param has neither flag; an `out` has `IsOut`; an `in` has `IsIn` only). The
autogen emits the write-back unconditionally for `ref`/`out` (it knows the
param's `IsByRef` + `IsOut`/`IsIn` from `ParameterInfo`; an `in`-only byref
is rare in CLR APIs and the autogen can emit a no-op write-back for it without
harm — a `readonly` violation surfaces in the CLR method, not the marshal).
This mirrors Legacy's `shouldFreeParam = hasByRef ? "false" : "true"`.

**D6 — The reflection fallback `ref struct` path uses `MethodInfo.Invoke`'s
boxed-copy semantics.** CLR `MethodInfo.Invoke` boxes a struct arg, mutates the
boxed copy in place, and the caller must re-unbox to observe the mutation
(same as the 4b value-type-`this` direct-call). The deref-read boxes the
struct off the frame; the write-back re-flattens the (mutated) boxed struct
into the frame via `WriteNeoValueType` (the 4b pattern, `CLRMethod.cs:493,517`).
A `ref int` does NOT box (it's a primitive) — the deref-read passes the int
value by-ref to `Invoke` via a `object[]` box that the CLR runtime handles;
the write-back stores the post-call int. (CLR `MethodInfo.Invoke` actually
passes ALL params as `object`, so `ref int` is passed as a boxed int and the
mutation of the box is what the write-back reads — this is the Legacy
semantics; the write-back is mandatory.)

**D7 — Discriminator at the stind/ldind consumer (4d).** The new
CLR-object-field branch fires when `mStack[objIdx]` is neither an
`ILTypeInstance` nor an `Array`. The runtime type check is
`mStack[objIdx].GetType().IsCLR-...` — but cheaper: the existing `else` branch
already proves "not ILTypeInstance, not Array"; the FIRST CLR-object probe is
`mStack[objIdx] is ILTypeInstance` (already false in `else`) so the new branch
is simply the body of the current `else` (replace the `GetNeoILInstance` call
with: if the offset encodes a CLR field identity, resolve it; else throw the
existing NIE — for a genuine IL heap field that somehow reached here, which is
unreachable today). The discriminator is additive — frame-native and Array and
ILTypeInstance paths byte-identical.

## Risks / Trade-offs

- **[4c touches shared CLR-binding codegen]** → the `*Neo` variants are on
  EVERY Neo CLR method call's hot path. Mitigation: the `pt.IsByRef`
  discriminator fires ONLY for a byref-typed param; every by-value path is
  byte-identical. Adversarial probes MANDATORY: a non-byref CLR method
  (the 13b/4b regression suite) + a byref-every-param method. Stash-toggle
  FAIL-on-HEAD -> PASS-after.
- **[4c D2 dead-discriminator codegen bug]** the existing `AppendArgumentCodeNeo`
  checks `pt.IsByRef` (`:173`) but `pt` is already de-byref'd at `:140`, so
  that branch is DEAD today — the fix MUST key on `p.IsByRef`. Mitigation:
  apply-phase probe confirms the byref codegen fires for a `ref int` param
  (a temp `Console.WriteLine` in the emitted wrapper, the dump-noise gotcha).
- **[4d D4 FieldInfo stamp collision with IL heap-field offset]** the offset
  half is shared between IL heap-field (`field.PrimitiveOffset`, a small int)
  and the new CLR-field identity. Mitigation: the consumer's discriminator
  (`mStack[objIdx] is ILTypeInstance` vs CLR object) routes BEFORE reading the
  offset, so an IL heap field never reaches the CLR-field-identity resolver
  and vice versa. Probe both paths.
- **[4d reflection-accessor perf]** `FieldInfo.GetValue`/`SetValue` is slower
  than a raw byte write. Mitigation: acceptable — a `ref clrObj.field` is a
  rare shape (C# usually lowers `clrObj.field = v` to a direct `stfld`, not
  `ldflda; stind`); the `ldflda; stind` shape only arises from `ref` passing
  or `fixed`. A perf pass can cache a typed delegate later; out of scope here.
- **[F-7 deferred — partial F-7 closure]** → the byref-on-delegate shape
  remains broken after this change (4c closes the CLR-METHOD byref path, not
  the delegate-callback byref path). Mitigation: record F-7 as STILL OPEN in
  the deferred-items doc + planning-context, pointing at the new shared helper
  as the ready-made primitive for the F-7 follow-up.
- **[Apply-phase dump gate for 4d D4]** the `FieldInfo`-resolvable-at-JIT-time
  assumption is unverified at propose. Mitigation: if the JIT dump shows the
  `Ldflda` operand does NOT carry a resolvable CLR `FieldInfo` for a CLR field
  (e.g. it carries an `IField` that is the IL-side wrapper), fall back to
  Option B (domain-cached index stamped at a new JIT field-lookup site) or
  STOP and pin the dump (the OPT-HARDEN K1 / Q-NEWOBJ discipline — do NOT ship
  a guessed fix).

## Open Questions

- **OQ1 (4d D4):** is the CLR `FieldInfo` resolvable at JIT time from the
  `Ldflda` operand for a CLR field, OR does the JIT only carry an IL-side
  `IField` wrapper? Resolve at apply via a JIT dump of `ldflda clrObj.field`.
  (If wrapper-only, fall back to Option B / a domain cache populated from the
  field's `TypeForCLR` at JIT time.)
- **OQ2 (F-7 scope):** does 4c's byref-marshaling helper
  (`ReadNeoFieldRef`/`WriteNeoFieldRef`, or a `MarshalNeoByrefArg` helper)
  factor cleanly enough that `DelegateAdapter.NeoInvokeSub` /
  `WriteNeoCallSlot` can call it directly? If YES and the diff is < ~50 lines,
  include F-7 in this change; if NO or larger, defer F-7 to its own follow-up
  child. Decide at apply after 4c's helper shape is concrete.
- **OQ3 (4c reflection `ref int` boxing):** does CLR `MethodInfo.Invoke`
  observe a `ref int` mutation through the `object[]` box (so the write-back
  reads the post-call boxed value), OR does it pass the int by-value (so the
  write-back must read from a separate byref channel)? The Legacy path uses
  `Reference*` pushees (a different mechanism), so Legacy is NOT a direct
  oracle here. Resolve at apply via a `ref int` mutation probe (the F-7-style
  probe) — if the box-mutation is NOT observed, the reflection fallback may
  need to pass the byref via a `TypedReference`/`__makeref` or throw a tagged
  NIE for `ref primitive` on the reflection path (the autogen path, which
  emits a direct `ref` call site, handles it natively).

## Apply resolution (2026-07-06, dump-confirmed)

**DUMP-GATE D4 (FieldInfo at JIT) + D6 (reflection ref int boxing): RESOLVED.**

- **D4 CONFIRMED via JIT dump.** The `Ldflda` arm's `fieldPrimOff = ip->Operand2`
  already holds `type.GetFieldIndex(token)` = the CLR FieldInfo hash for a CLR
  declaring type (set by `AppDomain.GetFieldOffset` for a non-IL type). At
  runtime, `((CLRType)appdomain.GetType(obj.GetType())).GetFieldValue(hash, obj)`
  resolves it. **NO JIT change required** for 4d (Option A's "resolve FieldInfo
  at JIT + stamp" is already the existing behavior; the offset half is the
  hash). The stind/ldind/stobj/ldobj consumer arms add a `NeoIsClrObject`
  discriminator branch (before the ILTypeInstance fallback) that routes to
  `NeoReadClrObjectField`/`NeoWriteClrObjectField` (which call
  `CLRType.GetFieldValue`/`SetFieldValue` by hash). IL heap-field + Array +
  frame-native paths byte-identical (additive discriminator).

- **D6 NOT APPLICABLE in the chosen mechanism.** The dump confirmed the area4b
  blocker applies identically to 4c: the readers (`CLRMethod.Invoke(byte*)` +
  the autogen redirect) only receive `targetBase` (the callee param region),
  NOT the caller `frameBase` (the redirect delegate signature is checked-in;
  threading the caller frameBase would break static bindings). So 4c uses the
  SAME deref-at-copy-site mechanism as area4b (NOT the design's literal "reader
  derefs the byref"): the optimizer call-lowering sizes a byref param's dest
  slot by the ELEMENT type + flags it; `CopyNeoCallArguments` derefs the byref
  (frame-native OR mStack-object) into the dest flat bytes; the reader then
  reads flat bytes exactly like a by-value param of the element type; a post-
  call reverse copy (`CopyNeoCallThisBack`, gated on the per-slot ref/out flag)
  writes the mutated dest back through the byref. The reflection `ref int`
  boxing channel is a non-issue: the reader reads flat bytes, calls
  `def.Invoke` (CLR mutates the box), the reflection write-back re-flattens
  `param[i]` into the dest slot, the reverse copy propagates it.

**The D2 dead-discriminator (HIGHEST 4c risk): FIXED, keyed on `p.IsByRef`.**
`AppendArgumentCodeNeo` de-byrefs `pt` at the top, so the old `pt.IsByRef` arm
(around line 173) was DEAD. The fix keys the WRITE-BACK epilogue
(`AppendNeoWriteBackCode`) on `p.IsByRef` (the raw `ParameterType`). The READ
below dispatches on the element type (`pt`) exactly like a by-value param (the
dest is sized by element type + deref'd by CopyNeoCallArguments). A
`TypedReference` param keeps a default+TODO (genuinely unsupported, not a byref
marshal). The by-value path is byte-identical (the discriminator fires only for
byref).

**Key implementation deviation from the design's literal D2/D3 (reader-deref):
4c uses DEREF-AT-COPY-SITE (the area4b pattern), NOT reader-deref.** The design
assumed the readers dereference the byref; both readers lack the caller
`frameBase` (the area4b blocker). The implemented mechanism:
1. Optimizer call-lowering (`Optimizer.Neo.cs`): for a CLR-callee byref param,
   size the dest by element type (`AllocateNeoCallParamSlot(elemType)`) + flag
   the slot (`PrimitiveByRefSrc`) + capture the element CLR Type
   (`PrimitiveByRefElemType`) + the ref/out write-back gate
   (`PrimitiveByRefWriteBack`, `!IsIn || IsOut`).
2. `CopyNeoCallArguments`: for a flagged slot, deref the byref Ref Slot
   `(objIdx, off)` -- frame-native (`-1`): CopyBlock from `frameBase + off`;
   mStack-object (`>=0`): route through `NeoMarshalByrefFieldToSlot` (the D3
   shared field accessor: ILTypeInstance -> Primitives[off]; CLR object ->
   GetFieldValue/SetFieldValue by hash).
3. Reader (`CLRMethod.Invoke` / autogen wrapper): reads flat bytes of the
   element type (the de-byref'd `pt`). The reflection reader de-byrefs `pt` to
   its element type for the dispatch arms. A `ref`/non-`out` byref param's
   element value is read normally; an `out`-only reference-type param skips the
   read (the dest slot is uninitialized -- reading an mStack index from it is
   garbage/OOB; `param[i]` starts null and the method overwrites it).
4. Reflection write-back (`CLRMethod.Invoke`): after `def.Invoke`, for each
   ref/out param, re-flatten `param[i]` (the mutated box) into the dest slot
   (primitive/struct via WriteNeoValueType; reference via the mStack index).
5. `CopyNeoCallThisBack`: for each flagged + write-back slot, reverse the deref
   (frame-native: CopyBlock to `frameBase + off`; mStack-object: write through
   `NeoMarshalByrefFieldToSlot` isWrite).

**D3 unified field accessor: `NeoMarshalByrefFieldToSlot` (read+write).** Used
by BOTH 4c's mStack-object byref deref AND the stind/ldind consumer's CLR-
object field path (via `NeoReadClrObjectField`/`NeoWriteClrObjectField`). The
ILTypeInstance sub-case is byte-Primitives; the CLR-object sub-case is
GetFieldValue/SetFieldValue by hash; the Array sub-case is NIE-tagged (owned by
the stind/ldind array arm, not the field accessor).

**F-7 (delegate ref/out): DEFERRED (OQ2).** The byref-marshaling helper
(`NeoMarshalByrefFieldToSlot`) is IL->CLR direction (deref at the IL call
site); F-7 lives in `DelegateAdapter.NeoInvokeSub` / `WriteNeoCallSlot` (CLR->
IL callback direction, a different site + direction). The helper does NOT
trivially route through `WriteNeoCallSlot`. F-7 stays OPEN -- recorded in
planning-context + neo-deferred-items (the shipper updates the latter at
archive).

**Pre-existing bug fixed in passing (the null-ref-param reflection read):**
`CLRMethod.Invoke`'s reference-param read did `mStack[idx]` with no null check;
a null reference param (encoded as mStack index -1) threw OOB. Fixed to
materialize null for idx<0. Surfaced by the 4d probes that pass a null string
to `MakeArea4dHolder`.

**Pre-existing inliner edge AVOIDED in the 4d.2 probe (not fixed):** an
inlined IL method that returns an `ldind.i4` result triggers a return-Move
misclassification (the int is moved as a reference -> mStack[intValue] OOB).
This is unrelated to 4d (it's the inliner's return-value classification). The
4d.2 probe is structured to drive the ldflda;ldind path WITHOUT an inlined
method-return-move (the helper body is `int v = slot; return v + 0;`, which
defeats the trivial-inliner's return-move misclassification). Flagged as a
separate follow-up.

**Actual edit sites:**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- `NeoCallParamMap`
  gains `PrimitiveByRefWriteBack` (bool[]) + `PrimitiveByRefElemType` (Type[]).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- CLR-callee
  call-lowering: size byref-param dest by element type; flag the slot; capture
  element type + write-back gate.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` --
  `CopyNeoCallArguments`/`CopyNeoCallThisBack` signatures gain `(mStack,
  appdomain)` + the byref-param deref/write-back; new
  `NeoMarshalByrefFieldToSlot`/`NeoReadClrObjectField`/`NeoWriteClrObjectField`
  /`NeoIsClrObject` helpers; stind/ldind/stobj/ldobj arms gain the
  CLR-object-field branch.
- `ILRuntime/CLR/Method/CLRMethod.cs` -- `Invoke(byte*)`: byref-param
  detection (de-byref `pt` for dispatch) + element-type/offset/write-back
  capture + reflection write-back after `def.Invoke`; null-ref-param read fix.
- `ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs` -- the dead
  `pt.IsByRef` arm replaced (only TypedReference keeps default+TODO); new
  `AppendNeoWriteBackCode` helper; per-param `__off_<idx>` capture.
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` --
  `GenerateMethodWraperCode_Neo` calls `AppendNeoWriteBackCode` after the call.
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` -- 4c/4d host helpers
  (`BumpRefInt`, `ProduceOutInt`, `BumpRefStruct`, `ProduceOutString`,
  `BumpRefAndProduceOut`, `ObserveInOnly`, `PlainByValue`,
  `BumpRefClrStructWithRef`, `Area4dHolder`, `MakeArea4dHolder`,
  `ReadArea4dIntField`, `ReadArea4dRefField`).
- `TestCases/NeoStep13bTest.cs` -- 9 `NeoStep13_*` probes (8 reflection 4c +
  1 NIE).
- `TestCases/NeoStep17Test.cs` -- 5 `NeoStep17_*` probes (4d stind/ldind/stobj
  + 4c+4d interaction).

**Verify: Neo smoke 175/175 (161 baseline + 14 new probes); each new probe
FAIL-on-HEAD -> PASS-after; Legacy-neutral (plain Debug builds clean; the new
probes pass on Legacy too; the 8 Legacy failures are all pre-existing --
NeoStep13 ClrStruct box, NeoStep14 TC1/TC5/TC8, NeoStep15 TC6, NeoStep16 TC8,
NeoStep6 NeoNaNR8).**

**Did NOT git commit/push** (per process discipline; the LEAD commits after
review). Did NOT update neo-deferred-items.md (the shipper does at archive).
