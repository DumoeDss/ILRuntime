# Neo Deferred Items — Resolution Map

> Companion to `neo-implementation-steps.md` (the 26-step roadmap).
> Purpose: track every Neo item that was DEFERRED during Steps 11-16, and map
> each to the step / follow-up where it will be resolved. The roadmap says WHAT
> to build per step; this doc says WHERE the deferred bits land.
>
> Maintenance: when a future step CLOSES a deferred item, move that item to the
> "Resolved" section (or delete it) and commit. Do not let this file silently
> drift from reality.
>
> Source of truth for each item's detail: the `ship-log.md` +
> `planning-context.md` in `openspec/changes/archive/2026-07-04-implement-neo-step*/`.

Convention: "Pre-existing" = the bug existed before the step that surfaced it
(verified by git-stash baseline); it is NOT a regression. "Roadmap step" =
lands inside an existing step of `neo-implementation-steps.md`. "Derived step" =
a follow-up step NOT in the 26-step roadmap (e.g. Step 13b, optimizer-hardening).

---

## 1. Recommended sequencing (where the new slots go)

Insert these into the roadmap ordering:

1. **[OPT-HARDEN]** Neo optimizer-correctness micro-step — slot it **before or
   alongside Step 17**. **DONE (2026-07-04):** K1 FIXED via the FCP
   `ldloca-kill` (Neo-only, Legacy-neutral). Q-STRUCT + Q-LONG were NOT
   reproducible on current HEAD (probes pass) — left deferred with suspect
   locations pinned (see §3).
2. **Step 17 (Ref/out + ldloca/ldflda + stind/ldind)** — fold IN: `ldelema`
   (Step 16) and `constrained.`-on-VT (Step 13 area 3). The Step 17 Ref-Slot /
   byref / VT-address model is the prerequisite for both.
3. **Step 13b (CLR binding codegen + unified CLRMethod param layout)** — slot
   it **immediately AFTER Step 17**. **DONE (2026-07-04):** Area 5 core landed
   (unified CLRMethod param layout — removed caller-temp-slot fallback;
   ReadNeoValueType/WriteNeoValueType byte-consistent; filled CLRMethod.Invoke
   + return + autogen NIEs). **Closes K2.** K2-FAM partially (flat-bytes path);
   the boxed-ref-bridge half DEFERRED. Area 4 (value-type-`this` direct-call),
   CLR-method ref/out, CLR-object stind/ldind field-hash DEFERRED to a future
   step (13b was Neo-only-codegen; Legacy untouched). Surfaced **F-MAJ-1**
   (pre-existing AllocateLocalStackSpaces slot-reuse with 2+ CLR struct locals)
   -> next optimizer-hardening.
4. **Step 18 (value-type newobj + CLR newobj)** — fold IN: quirk-newobj-alias.
   (Step 18 already owns IL value-type newobj.)
5. **[CATCH-COMPLETE]** small follow-up (anytime after Step 15) — closes
   `CheckExceptionType`-NIE-for-non-CLRType. **DONE (2026-07-04):** the
   `CheckExceptionType` IL branch landed (CanAssignTo reuse; shared-engine;
   Legacy-neutral). D-CHECKEX PARTIAL — the CheckExceptionType NIE is gone, but
   end-to-end IL-exception catch still needs an Exception CrossBindingAdaptor +
   Throw handling for IL instances (new follow-up D-IL-EXCEPTION-THROW). No
   positive test yet (harness can't author an ILType catch clause).
6. **Opportunistic** (no fixed slot; handle when triggered):
   - peephole + PatchKind.IsinstResult (Step 15) — when a patch-infra step lands.
   - Stelem_I / generic-token Ldelem·Stelem / native Ldelem_I·U8 / multi-dim
     arrays (Step 16) — when a test or feature needs them.
   - cgt-un comment-nit (Step 15) — trivial.
   - catch-wrapper (Step 14) — not a bug (matches Legacy); revisit only if a
     real symptom appears.

---

## 2. Master table

| ID | Item | Surfaced by | Target | Unblocked by | Severity |
|----|------|-------------|--------|--------------|----------|
| D-LDELEMA | `ldelema` opcode | Step 16 | **RESOLVED (Step 17 + neo-step17-completion)** | Step 17 Ref-Slot/stind/ldind | fully resolved (IL VT array path Step 17; CLR primitive-array ldelema remainder in neo-step17-completion 2026-07-05) |
| D-CONSTRAINED | `constrained.`-on-VT specialization (Step 13 area 3) | Step 13 | **FULLY RESOLVED for (a)/(b)/(d)/(M2); (c) edges remain -> `neo-step17-generic-byref-etc`** | Step 17 byref/VT-this-address | {a,d,M2} RESOLVED 2026-07-05 (neo-step17-completion): full constrained.-on-VT dispatch + CLR primitive-array ldelema + F-5 boxed-source NIE-guard. **(b) Stobj/Ldobj ref-region copy + IL-VT-with-ref-fields constrained RESOLVED 2026-07-06 (neo-step17-stobj-refloop):** ref-region copy gated on `TotalReferenceCount > 0` (primitive-only VTs byte-identical); byref source ref-base recovered via runtime `localInfos` scan (R2, no JIT change); Constrained IL-VT-direct-call + inherited-CLRMethod box paths seed the callee slot-0 ref region via the VT-THIS-ADDR copy-back mechanism (new `ExecuteNeo` hook, seeded post-mStack-reservation). (c) generic-byref/fixed/interface-on-VT-constrained STILL DEFERRED -> `neo-step17-generic-byref-etc` (task #22). area4 M2 obligation CLOSED. |
| D-13B | Step 13 areas 4-5 (binding codegen + CLRMethod param layout) | Step 13 | **FULLY RESOLVED 2026-07-06 (neo-step13-area4-refandstind, 4c+4d)** | Area 5 core done; Area 4b/4a done in neo-step13-area4; Area 4c (CLR ref/out typed-ref bridge) + 4d (CLR stind/ldind/stobj/ldobj via field identity) done in neo-step13-area4-refandstind — all of Area 4 done | roadmap gap |
| K1 | FCP mis-propagates value-type Moves (copy-then-mutate silent) | Step 12b | **RESOLVED (OPT-HARDEN)** | — | fixed (ldloca-kill) |
| K2 | Step 8 VT-by-value param copy reads primitive value as mStack index | Step 12b | **RESOLVED (Step 13b)** | unified param layout | fixed |
| K2-FAM | Move-path scalar->boxed-ref CLR-VT-local (reads int as mStack idx) | Step 13 | **RESOLVED 2026-07-06 (neo-k2fam-bridge, TEST-ONLY — subsumed by opt-harden-2 + review-fix + step13b; 6 regression guards)** | flat-bytes path resolved; boxed-ref bridge subsumed (declare-side flat-bytes + Initobj/Box/Unbox_Any flat-bytes arms + by-value-param flat-bytes read) | pre-existing (closed by recent work; no new engine fix) |
| F-MAJ-1 | 2+ simultaneous CLR struct locals -> silent wrong result (representation mismatch, NOT slot-reuse) | Step 13b | **RESOLVED ([OPT-HARDEN-2])** | dump-confirmed: D6 return-write flat-bytes into a 4-byte boxed-ref local slot overflowed 8 bytes into the neighbour; fixed by declaring a CLR-VT local as flat-bytes (Option B, gated `#if ENABLE_NEO_MODE`) | pre-existing (13b made reachable); fixed 2026-07-05 |
| Q-NEWOBJ | Newobj dest/arg aliasing after a `newarr` | Step 16 | **RESOLVED (Step 18, non-reproducible)** | — | not reproducible on HEAD (JIT dump: distinct frame regions + ref slots per register); same outcome as Q-STRUCT/Q-LONG |
| Q-VT-NEWOBJ | IL value-type `newobj` (real, non-inlined) + `call VT ctor` via ldloca | Step 18 | **RESOLVED in [VT-THIS-ADDR]** | RESOLVED 2026-07-05: inline-stfld owner-type clobber fix + D1 Newobj-dest typing + Newobj temp sizing + copy-back runtime branch | fixed; full NeoStep smoke 99/99 |
| F-2 / INLINER-REFONLY-VT | ref-only VT (prim-size 0) local `new S(refArgs)` mis-compiles: inlined `stfld.ref.inline` writes don't survive to the following in-frame `ldfld.ref` read | neo-vt-this-addr re-review (F-1 probe) | **future** (fold into K2-FAM bridge or [OPT-HARDEN-3]) | JITCompiler inliner ref-fold over a 0-prim-size VT local | pre-existing (latent) |
| F-3 / NEO-BYREF-THIS | `new ClrStruct(args)` + `local.VTMethod()` (CLR/IL struct instance method via byref-`this`) hit a pre-existing reflection gap (`CLRMethod.Invoke` read the byref `this` as a 4-byte mStack index; the byref `this` is an 8-byte Ref Slot from `ldloca`) | neo-opt-harden-2 re-review | **RESOLVED for direct `call` (neo-step13-area4); `callvirt`/`constrained.callvirt` still Step 17 D-CONSTRAINED** | `CopyNeoCallArguments` derefs byref sources at the copy site; `this` slot holds flat bytes; `CopyNeoCallThisBack` propagates mutations | pre-existing; direct-`call` shape closed 2026-07-05 (6/9 probes FAIL-on-HEAD) |
| F-4 / NEO-IL-EX-FIELDACCESS | Reading IL-declared fields/methods off a CAUGHT IL exception via the adaptor bridge is broken on Neo (4 broken read paths: `((CrossBindingAdaptorType)e).ILInstance` callvirt-on-CLR-interface -> InvalidCastException; `e.GetType()` callvirt.clr -> NIE; `appdomain.Invoke` instance-method -> NRE under ENABLE_NEO_MODE; `ILTypeInstance.this[index]` indexer -> null under ENABLE_NEO_MODE) | neo-il-exception-throw apply (OQ1/OQ2) | **future** (Step 13 Area 4 / cross-binding-adaptor follow-up) | Neo callvirt-on-CLR-interface + `appdomain.Invoke` instance-method re-entry + `ILTypeInstance` Neo indexer | pre-existing (NOT introduced; surfaces only because IL exceptions can now be thrown + caught); workaround `e is MyEx` (isinst) |
| Q-STRUCT | struct-local + field-mutation + element-read temp-renumber | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect `Optimizer.BCP.cs:97-141` | pre-existing (unconfirmed) |
| F-5 / NEO-CALLARG-BOXED-SRC | boxed-source branch of `CopyNeoCallArguments` (`ILIntepreter.Neo.cs:295-300`) mis-copies a boxed `this` (would read an mStack field offset as a struct address); UNREACHABLE today (boxed `this` only via `constrained.callvirt` = Step 17 NIE); also `CopyNeoCallThisBack` comment claims ctor coverage but the newobj path does not invoke it | neo-step13-area4 review (Finding M2) | **RESOLVED (neo-step17-completion)** | the box-once BYPASSES CopyNeoCallArguments; the wrong defensive CopyBlock replaced with a tagged NIE-guard + CopyNeoCallThisBack comment tightened | latent dead-branch (closed 2026-07-05) |
| F-6 / NEO-VT-FLDADDR | `ldflda`-on-in-frame-VT mis-reads: the Ldflda arm reads the operand slot as an mStack objIdx; an in-frame VT slot holds flat bytes -> garbage. There is NO `Ldflda_Inline`. Any IL-struct method taking a field address (`field.ToString()`, `ref field`, `fixed`) is broken on Neo REGARDLESS of constrained | neo-step17-completion apply (the IL-struct ToString probe) | **RESOLVED 2026-07-06 (neo-vt-ldflda-inline)** | marker stamp (`Operand4` bit 0x1 in `TypeSpecializeNeoOpcodes case Ldflda:`) + 3-way runtime dispatch (marker + leading-int: `-1` -> frame-native; else marker -> flat-bytes shape 3; else -> heap/CLR). Neo-only, Legacy-neutral. NeoStep 154/154. CLR-object-field `ldflda` coverage gap deferred to `neo-step17-stobj-refloop` (F-R2) | pre-existing (NOT introduced; surfaced when the IL-struct ToString probe hit it) |
| Q-LONG | long default-zero compare (conv.i8) quirk | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect conv.i8 / branch type-spec | pre-existing (unconfirmed) |
| D-CHECKEX | `CheckExceptionType` NIE for non-CLRType catch types | Step 14 | **RESOLVED ([CATCH-COMPLETE] + neo-il-exception-throw)** | CheckExceptionType IL branch + Exception-adaptor + Throw-for-IL all landed | shared-engine gap (fully closed; IL branch now reachable end-to-end) |
| D-IL-EXCEPTION-THROW | End-to-end IL-exception catch (Exception-adaptor + Throw-for-IL) | Step 18/CATCH-COMPLETE | **RESOLVED (neo-il-exception-throw)** | System.Exception CrossBindingAdaptor (built-in) + Throw `as Exception` IL-instance unwrap on BOTH engines | shared-engine gap (closed 2026-07-05; F-4 / NEO-IL-EX-FIELDACCESS follow-up surfaced) |
| D-PEEP | `box T; isinst U` peephole + `PatchKind.IsinstResult` | Step 15 | **opportunistic** | patch-infra step | optimization (non-functional) |
| D-ARR | Stelem_I / generic-token Ldelem·Stelem / native Ldelem_I·U8 / multi-dim | Step 16 | **PARTIAL RESOLVED 2026-07-06 (neo-array-completion, rank-1); multi-dim -> `neo-array-multidim`** | rank-1 closed; multi-dim deferred | rank-1 closed; multi-dim deferred (rare) |
| N-CGTUN | Cgt_Un divergence comment (src=sentinel case) | Step 15 | **RESOLVED 2026-07-06 (neo-opportunistic-cleanup)** | — | cosmetic nit (comment-only; runtime expression byte-identical) |
| N-CATCHWRAP | catch slot stores ILRuntimeException wrapper | Step 14 | **accept** (matches Legacy) | — | not-a-bug |
| N-TC2 | Step 14 TC2 asserts `e != null` | Step 14 | **RESOLVED 2026-07-06 (neo-opportunistic-cleanup)** (was resolved by Step 15; the test-tighten follow-up is now done) | — | cleanup |
| F-7 / NEO-DELEGATE-REFOUT | byref-aware arg marshaling in `DelegateAdapter.NeoInvokeSub` (delegate ref/out params) | Step 19 | **future** (route to a byref follow-up child — same family as D-13B area 4c / neo-step17-stobj-refloop) | byref-typed Ref Slot in the delegate Invoke param region | pre-existing (latent; the only reachable shape today is plain primitives via `WriteNeoCallSlot`) |
| F-8 / NEO-DOUBLE-COMBINE | 2+ `double` locals combined in one boolean expression silently misfire (F-MAJ-1 class; `double`-specific, not all 8-byte primitives) | neo-array-completion review (F-1) | **RESOLVED 2026-07-06 (neo-double-combine-quirk, D4)** | dead `Operand3 = RefOffset` write in `Optimizer.Neo.cs LowerNeoOffsets` immediate-branch case clobbered the high 4 bytes of `OperandDouble`/`OperandLong` (@12-19) via the `[StructLayout(Explicit)]` union; copy-prop folds `Ldc_R8` into `Bnei_Un_R8` (reachable) but keeps `Ldc_I8` register-register (unreachable) -> double-fails/long-works | pre-existing (NOT introduced by D-ARR; upstream of the array work; surfaced when the array probes needed combined `double` assertions) |
| F-9 / NEO-INLINED-RETURN-MOVE | an int returned from an inlined IL method moved as a reference -> `mStack[intValue]` OOB (return-value classification edge in the trivial inliner) | neo-step13-area4-refandstind review (4d.2 probe-avoidance) | **future** (route to an inliner/optimizer follow-up) | the trivial-inliner mis-classifies an inlined IL-method return value (moves an int as a reference) | pre-existing (latent; surfaced when the 4d.2 `LdindClrIntFieldPeek` probe needed to defeat it via `int v = slot; return v + 0;`) |

---

## 3. Per-item detail

### D-LDELEMA — `ldelema` opcode (Step 16 -> Step 17 -> FULLY RESOLVED)
**RESOLVED 2026-07-05 (neo-step17-completion).** The CLR primitive-array
remainder (the `Ldelema` arm's `else` branch -- a non-IL-VT array -- which
still threw a Step-17 NIE after the Step-17 IL-VT-array path shipped) is now
done. The `else` branch encodes `(arrIdx, elementIdx)` for a CLR value-type-
element array (the `off` half IS the element index); NIEs a reference-type-
element array (validates `elemClrType.IsValueType`). The consumer side gained a
`mStack[objIdx] is Array` branch in `Stind_I4` (`cArr.SetValue(v, off)`) and
`Ldind_I4` (`(int)cArr.GetValue(off)`) -- the existing `objectIndex >= 0` arm
calls `GetNeoILInstance`, which a CLR `Array` is not. Scoped to the green
target (Stind_I4 / Ldind_I4 on a CLR primitive array); the OTHER stind/ldind
widths (the F-4 accepted-known from step17-completion) were RESOLVED
2026-07-06 by `neo-array-completion` — the `is Array` branch is now extended
to `Stind_I1/I2/I8/R4/R8` + `Ldind_I1/U1/I2/U2/U4/I8/R4/R8` +
`Stind_Ref`/`Ldind_Ref` (the width matrix is complete except the UIntPtr-
primitive and ref-array upstream gaps, which remain unreachable). D-LDELEMA is
now FULLY resolved (both the Step-17 IL-VT-array path and this CLR-primitive-
array remainder). See
`openspec/changes/archive/2026-07-05-neo-step17-completion/ship-log.md` +
`openspec/changes/archive/2026-07-06-neo-array-completion/ship-log.md` (F-4).

Step 16 implemented Newarr/Ldelem/Stelem/Ldlen but deferred `ldelema`. Its only
consumers are `stind_*`/`ldind_*`, `fixed`, and `ref`/`out` params — all Step 17
(the unified 8-byte Ref Slot `(objectIndex, offset)`). `ldelema` is a no-op to
ship without those consumers (it would produce a ref nothing reads). Currently a
Step-tagged NIE. **Resolution (Step 17):** `ldelema` is implemented. For an IL value-type array
(ILTypeInstance[] with pre-instantiated elements, the Step 16 representation)
the arm resolves the element ILTypeInstance and parks it on mStack, encoding
`(elementMStackIdx, 0)` so stind/ldind (and the heap stfld/ldfld that the C#
compiler emits for `arr[i].field = v`) hit the standard IL-instance Primitives
path on that element instance. A CLR primitive-array `ldelema` still throws a
Step-17 NIE (deferred -- direct indexing covers that path). The addrAlias
coexistence decision: the Step 12 folding stays the fast path; Step 17 adds a
consumer-scan gate that evicts an alias dest (making its producer real) ONLY
when the dest's address escapes the folding window AND the dest register is not
reused for any surviving foldable (`_Inline`/`Initobj`) consumer -- purely
additive, so Steps 12-16 are untouched (72/0 preserved).

### D-CONSTRAINED — `constrained.`-on-value-type (Step 13 area 3 -> Step 17 -> {a,b,d,M2} RESOLVED; (c) edges remain)
**RESOLVED 2026-07-06 (neo-step17-stobj-refloop) for the (b) + IL-VT-with-ref-fields
constrained scope.** The Stobj/Ldobj ref-region copy and the IL-VT-with-ref-fields
constrained sub-case are now delivered (closes the remaining (b) + IL-VT-with-refs
deferral). The arms gate the ref-region copy on `ilType.TotalReferenceCount > 0`
(primitive-only VTs are byte-identical). The byref source/dest ref-region mStack
base is recovered via **R2 (runtime `localInfos` scan)** — dump-confirmed, NO JIT
change (the scan locates the local whose frame byte `Offset == thisByteOff`; the
byref carries the primitive byte offset but NOT the ref base). Stobj:
`DstOffset`=byref address, `SrcOffset`=value local; Ldobj is the mirror.
Frame-native-direct-local = a byte `CopyBlock` of `primSize` PLUS an mStack-to-
mStack copy of `TotalReferenceCount` ref slots (mirrors `Move_Vt`); IL-instance
(`objectIndex >= 0`) routes the ref half through `ManagedObjects` via
`CopyFrameToIL`/`CopyILToFrame`; a nested-field-byref (scan miss) throws a
Step-17-tagged NIE. The Constrained IL-VT-direct-call path + the inherited-
CLRMethod box path removed their `TotalReferenceCount > 0` NIEs and now seed the
callee slot-0 ref region via the **VT-THIS-ADDR copy-back mechanism** — a new
`ExecuteNeo` hook (`constrainedSlot0SeedRefOffset/SrcRefBase/RefCount`), seeded
INSIDE `ExecuteNeo` right after the mStack reservation (the
mStack-reservation-clobber gotcha — a pre-call mStack write would be zeroed by the
reservation's `Add(null)`). The M1 round-1 fix made the IL-instance branches throw
a tagged NIE on a scan-miss (loud, not silent corruption); the M2 fix dropped the
dead `constrainedSlot0SeedRefBase` param (3 hooks suffice — the seed uses the
callee's own `frameRefBase` captured inside `ExecuteNeo`). **Earned constraint:**
R2 resolves the byref to a direct local ONLY in the same frame — a byref PARAMETER
(cross-frame) cannot be recovered -> deferred to a follow-up (fails clean, the
`dstRefBase < 0` / `srcRefBase < 0` NIE). Verification: NeoStep 181/181
(175 + 6 probes), NeoStep17 Legacy-neutral 41/41; all probes FAIL-on-HEAD -> PASS.
Review round 0 APPROVED; round 1 fixed M1 + M2; LEAD non-author diff-read
confirmed. See
`openspec/changes/archive/2026-07-06-neo-step17-stobj-refloop/ship-log.md`.

**STILL DEFERRED -> `neo-step17-generic-byref-etc` (task #22):** (c) generic-byref
(`ref T`/`out T` with `T` generic), `fixed` unmanaged-pinning, interface-on-VT-
constrained beyond the common shape -- remain Step-17-tagged NIEs (or accept-known
for `fixed` if a probe shows the address works without GC pinning). They are
independent plumbing (a generic-param type-token discriminator; a pinned-local
flag; an interface-dispatch branch) that does NOT fall out of (b) and is not
exercised by the smoke.

---

**RESOLVED 2026-07-05 (neo-step17-completion) for the {a,d,M2} scope.** Full
`constrained.callvirt T.M` dispatch on a value-type `T` is delivered. The
apply-phase JIT dump DISPROVED the design's Option F fusion premise: the JIT
order is `[Push..., Constrained T, Callvirt M]` (Constrained runs BEFORE
callvirt), so the runtime `Constrained` arm OWNS the dispatch -- it carries the
type token, reads the trailing callvirt at `ip+1` for method/map/ret info, and
skips the callvirt (`ip += 2`). NO JIT change; non-constrained callvirts byte-
identical. Discriminator = IL-vs-CLR (IL-VT + ILMethod override -> direct-call;
IL-VT + inherited CLRMethod -> box into ILTypeInstance; CLR-VT -> box-once via
ReadNeoValueType; already-boxed -> no-op). The round-1 F1 review-fix added the
IL-VT-inherited-CLRMethod box sub-branch (`Instantiate(false)` + `CopyFrameToIL`
+ `Boxed=true`) so `anyIlStruct.ToString()`/`GetHashCode()`/`Equals()`/`$"{x}"`
actually work (was a Blocker segfault regression in round-0), plus a
`!(constrainedType is ILType)` guard on the generic CLR-VT branch. ALSO closes
the area4 M2 obligation: the box-once BYPASSES `CopyNeoCallArguments`, so its
boxed-source branch is genuinely unreachable -- the wrong defensive CopyBlock
replaced with a tagged NIE-guard, and the `CopyNeoCallThisBack` comment
tightened (mutating INSTANCE METHODS, not ctors). NeoStep 130/130, NeoOptHard
16/16, Legacy-neutral. **(d) CLR primitive-array ldelema also shipped in this
cohort** (see D-LDELEMA -- now fully resolved).

**STILL DEFERRED -> `neo-step17-generic-byref-etc` (task #22):** (c) generic-byref
(`ref T`/`out T` with `T` generic), `fixed` unmanaged-pinning, interface-on-VT-
constrained beyond the common shape -- remain Step-17-tagged NIEs. [(b) Stobj/
Ldobj ref-region copy + IL-VT-with-ref-fields constrained were RESOLVED
2026-07-06 by `neo-step17-stobj-refloop` — see the RESOLVED-(b) prepend at the
top of this §3 entry.] See
`openspec/changes/archive/2026-07-05-neo-step17-completion/ship-log.md` for the
{a,d,M2} cohort and
`openspec/changes/archive/2026-07-06-neo-step17-stobj-refloop/ship-log.md` for
the (b) closure.

Step 13 deferred `constrained.` callvirt specialization on a value-type `this`
(`T.ToString()` where T:struct). Three blockers, all Step 17 territory: (1)
`ldarga`/`ldarga.s` unimplemented (Step 6 NIE -> Step 17 byref); (2) the
`Constrained` opcode has no runtime arm and is re-appended AFTER the callvirt
(`JITCompiler.cs:1763-1772`) so it cannot inform it; (3) the box-once/direct-call
lowering needs the value-type `this` address model (= Step 17). No green NeoStep
test exercises `constrained.` today (zero regression). **Resolution:** fold into
Step 17 (or a tiny "Step 13 area 3 completion" immediately after Step 17).

**Folded-in obligation from neo-step13-area4 (F-5 / NEO-CALLARG-BOXED-SRC, M2):**
when the Step 17 Constrained arm is completed (so a boxed `this` becomes
reachable via `constrained.callvirt`), the boxed-source branch of
`CopyNeoCallArguments` (`ILIntepreter.Neo.cs:~295-300`) MUST add a NIE guard (or
a correct copy) — as written it would mis-copy a boxed `this` (it reads an mStack
field offset as a struct address). Also tighten the `CopyNeoCallThisBack` comment
to say it covers mutating INSTANCE METHODS (not ctors) — the newobj path does
NOT invoke it. See the F-5 / NEO-CALLARG-BOXED-SRC §3 entry for full detail.

### D-13B — Step 13 areas 4-5 (-> new Step 13b)
Step 13 deliberately scoped down to areas 1-3 (then area 3 also deferred — see
D-CONSTRAINED). Areas 4-5 were deferred because they touch the CLR binding
generator and the Neo call ABI shared by EVERY CLR method call — the single
highest-regression risk in the roadmap. Bundling them with the frame-local
box/unbox mechanics would have made the diff unreviewable.
- **Area 4:** binding-codegen overhaul — `Unsafe.Unbox<T>` + direct-call mode,
  eliminate `WriteBackInstance`.
- **Area 5:** CLRMethod param region unified Neo slot layout — reuse
  `AllocateSlotForType`/`StackSlotInfo`; read params via non-generic
  `ReadNeo*` helpers by actual slot width; REMOVE the caller-temp-slot fallback
  in `Optimizer.Neo.cs` (~646-655); cover CLR struct by-value params, return
  values, instance-method `this`, generic CLR struct params.
**Resolution:** a dedicated `implement-neo-step13b` change. Recommended AFTER
Step 17 so the byref/param model informs the layout. This is the highest-value
follow-up — it also closes K2 and K2-FAM.
- **Area 5 core: DONE (implement-neo-step13b, 2026-07-04)** — unified
  CLRMethod param layout (removed caller-temp-slot fallback;
  `ReadNeoValueType`/`WriteNeoValueType` byte-consistent; filled
  `CLRMethod.Invoke` + return + autogen NIEs). Closes **K2**. K2-FAM PARTIAL
  (flat-bytes path resolved; boxed-ref-source half still deferred — see K2-FAM
  below; it depends on 4c + the F-2 inliner).
- **Area 4b (value-type-`this` direct-call) + 4a (`Unsafe.Unbox<T>` boxed
  direct-call + write-back): DONE (neo-step13-area4, 2026-07-05)** — closes
  **F-3 / NEO-BYREF-THIS** for the direct-`call` shape; the boxed re-box is
  NOT emitted in the autogen wrapper (boxed-`this` only reachable via
  `constrained.callvirt` = Step 17 NIE today). Neo 117/117, NeoOptHard 16/16,
  Legacy `NeoStep13_` 9/9. Surfaced **F-5 / NEO-CALLARG-BOXED-SRC** (latent
  dead-branch mis-copy in `CopyNeoCallArguments`; routed to Step 17). See
  `openspec/changes/archive/2026-07-05-neo-step13-area4/`.
- **Area 4c (CLR-method `ref`/`out` typed-ref bridge) + Area 4d (CLR-object
  `stind`/`ldind`/`stobj`/`ldobj` via field hash): DONE
  (neo-step13-area4-refandstind, 2026-07-06)** — RESOLVED 2026-07-06
  (neo-step13-area4-refandstind, 4c+4d). 4c: a CLR method with a `ref`/`out`
  param was SILENT-WRONG on HEAD (read the Ref Slot's `objectIndex` half as the
  int value, no write-back); the reflection `CLRMethod.Invoke(byte*)` + the
  autogen `AppendArgumentCodeNeo` now marshal byref params via the area4b
  deref-at-copy-site mechanism (`CopyNeoCallArguments` derefs byref params;
  `CopyNeoCallThisBack` propagates) + a write-back gated `!IsIn || IsOut`. The
  D2 dead-discriminator fix keys on `p.IsByRef`/`ptRaw.IsByRef` (the LIVE one),
  NOT the dead `pt.IsByRef`. `NeoCallParamMap` gains
  `PrimitiveByRefWriteBack` + `PrimitiveByRefElemType`. 4d: every width arm +
  `Stobj`/`Ldobj` gained a `NeoIsClrObject` branch routing to
  `NeoReadClrObjectField`/`NeoWriteClrObjectField` (CLRType.GetFieldValue/
  SetFieldValue by the JIT-stamped FieldInfo hash); a shared
  `NeoMarshalByrefFieldToSlot` covers ILTypeInstance + CLR-object + Array.
  Verification: NeoStep 175/175 (161 + 14 probes), NeoOptHard 24/24,
  Legacy-neutral; all 13 functional probes FAIL-on-HEAD -> PASS. Review
  APPROVED (0 Blocker/Major; M-1 `ref arr[i]` to CLR method NIEs — documented
  limitation; M-2 ref/out write-back mStack growth — not correctness; T-1/T-2
  cosmetic). **NEW follow-up F-9 / NEO-INLINED-RETURN-MOVE** (inlined-IL-
  method-return-move misclassification; pre-existing; surfaced by 4d.2). **F-7
  (delegate ref/out) still OPEN** (4c is IL->CLR direction; F-7's
  `DelegateAdapter.NeoInvokeSub` is CLR->IL — different site/direction). All
  of Area 4 is now done. See
  `openspec/changes/archive/2026-07-06-neo-step13-area4-refandstind/ship-log.md`.

### K1 — FCP mis-propagates value-type Moves (Step 12b -> RESOLVED in OPT-HARDEN)
After `b = a`, FCP rewrote later `b.field` reads to `a.field` even AFTER
`a.field` was mutated, because FCP's kill condition (`Optimizer.FCP.cs`) only
checked a whole-register write, not a field write via `ldloca; stfld`. Real
`b = a; mutate(a); read(b.field)` was silently wrong.

**RESOLVED (2026-07-04, OPT-HARDEN):** the corrected root cause is that the
`Stfld_*_Inline` reaches the field indirectly through a `ldloca.s` address
handle, so its `Register1` is the address temp, not the source local (the
original "kill on stfld Register1" design was a no-op). The fix kills the
propagation on the `Ldloca`/`Ldloca_S` itself: when the addressed local
(`op.Register2`) equals the propagation's `xSrc` or `xDst`, kill it (taking an
address = potential mutation through it). Gated `#if ENABLE_NEO_MODE`
(Legacy-neutral, stash-verified). Regression test `NeoOptHardTest_K1_*`
(FAIL-on-HEAD -> PASS-after). See
`openspec/changes/archive/2026-07-04-implement-neo-opt-hardening/`.

### K2 / K2-FAM — Move-path boxed-ref CLR-VT-local (Step 12b / Step 13 -> Step 13b)
**RESOLVED 2026-07-06 (neo-k2fam-bridge, TEST-ONLY).** The Box/Initobj/Unbox-
source half that was DEFERRED here is now closed — NOT by a new engine fix, but
as a side effect of three changes whose combined effect was re-assessed against
K2-FAM by `neo-k2fam-bridge` and confirmed via adversarial reproducer probes on
HEAD (all 6 PASS on HEAD): `neo-opt-harden-2` (F-MAJ-1) declared a CLR-VT LOCAL
as flat bytes (`Size = GetNeoValueTypeManagedSize, RefCount = 0, localIsRef =
false` under `#if ENABLE_NEO_MODE`) — the OLD boxed-ref representation
(`Size=4, RefCount=1`) that produced the "int-as-mStack-index" corruption NO
LONGER EXISTS for a CLR-VT local; the `neo-opt-harden-2` review-fix (round 1)
rewrote the `Initobj` (M1) / `Box` (M2) / `Unbox_Any`-dest arms to read/write
flat bytes; and `implement-neo-step13b` unified the by-value-param read in
`CopyNeoCallArguments` to byte-copy N flat bytes. The closure holds for ALL
source shapes of a CLR-VT local (return / Box / Initobj / Unbox). The
`neo-k2fam-bridge` change shipped 6 adversarial regression guards in
`TestCases/NeoStep13bTest.cs` (`NeoStep13_K2Fam_*`) to lock the closure in
(plus the spec delta + this doc update). The F-2 / INLINER-REFONLY-VT cross-
reference is left intact (F-2 is a distinct inliner ref-fold defect class —
still deferred). See
`openspec/changes/archive/2026-07-06-neo-k2fam-bridge/ship-log.md`.

- **K2:** the Step 8 VT-by-value param copy reads a primitive-field VALUE as an
  mStack index -> `ArgumentOutOfRangeException`. The call param-setup path uses
  `NeoCallParamMap`/`CopyNeoCallArguments`, not `Move_Vt`. **RESOLVED (Step 13b):**
  the unified CLRMethod param layout (removed caller-temp-slot fallback;
  ReadNeoValueType by width) reads CLR struct params correctly. K2 closed.
- **K2-FAM:** the Move path mis-handles scalar/constant -> boxed-ref CLR-VT-local
  assignment (reads an int as an mStack index). **PARTIAL (Step 13b):** the
  flat-bytes path (a CLR struct obtained from a method RETURN, stored as flat
  bytes) now passes by value correctly without a bridge. The boxed-ref-source
  shape (a CLR struct local sourced from Box/Initobj) still needs a bridge to
  pass by value — DEFERRED (Phase 3 safety valve); it needs IL-side
  ldfld/stfld on CLR struct fields for a clean reproducer. Pre-existing; not a
  13b regression.

### F-MAJ-1 — CLR struct local representation mismatch (Step 13b -> RESOLVED in [OPT-HARDEN-2])
A method holding 2+ simultaneous CLR struct locals computed a silently wrong
result (a combined `r1!=600 || r2!=3` check failed though each sub-check failed
in isolation too; not an r1<->r2 clobber). The prior 13b-review hypothesis
("`AllocateLocalStackSpaces` slot-reuse/liveness") was DISPROVEN at propose:
that method allocates strictly monotonic non-overlapping regions (no reuse
logic). **Resolution ([OPT-HARDEN-2], 2026-07-05, dump-confirmed):** the true
root cause is a representation mismatch. The Step-13b D6 CLR-struct return-
write (`ILIntepreter.Neo.cs` InvokeNeoClrMethod) writes the struct's FLAT
managed bytes (`retSz = GetNeoValueTypeManagedSize`, e.g. 12 for Vector3) via
`WriteNeoValueType` into the caller's dest local slot, but
`AllocateLocalStackSpaces` DECLARED a CLR value-type local as a 4-byte boxed-
ref (`Size=4, RefCount=1, isRef=true`). A 12-byte flat write into a 4-byte slot
overflowed 8 bytes into the neighbouring local -> both `Sum()` reads resolved
corrupted mStack indices -> both individually wrong. The D2 by-value-param read
already byte-copies flat bytes from the local's Offset (so the runtime
representation was ALWAYS flat bytes; only the declaration lied -- the 13b
single-local tests passed despite the under-sizing because the overflow hit an
empty neighbour). Candidate (2) (`CleanupRegister` compaction) was REFUTED by
the dump (distinct regions). **Fix:** Option B -- declare a CLR value-type
LOCAL as flat bytes (`Size = GetNeoValueTypeManagedSize`, `RefCount=0`,
`localIsRef=false`), mirroring the callee param layout
(`AllocateNeoCallParamSlot`), gated `#if ENABLE_NEO_MODE` so Legacy keeps the
boxed-ref path. Option A (boxed-ref write) was REJECTED: it would break the D2
caller-local -> callee-param byte-copy (would copy the 4-byte mStack index +
garbage). Legacy-neutral (stash-toggle: same 7 pre-existing Legacy NeoStep
failures with and without the fix). Full NeoStep smoke 100/100 (99 baseline +
promoted `NeoStep13bTwoClrStructLocalsRegression`); 9 `NeoOptHardTest_Fmaj1_*`
probes all green. **Note for future changes:** `AllocateLocalStackSpaces` has
NO slot-reuse/liveness logic -- do not mis-attribute this fix to a liveness
allocator (it is a representation-sizing fix, period).

### Q-NEWOBJ — Newobj dest/arg aliasing after `newarr` (Step 16 -> Step 18 -> RESOLVED)
`new T(intArg)` immediately FOLLOWS a `newarr` collides in the Call/Newobj
Push-scanning lowering. `new T(intArg)` alone (no array) works (TC7 green); the
collision is in the call/newobj lowering (Step 10/11 territory), 0 diff lines
added there by Step 16. Worked around in Step 16 TC4 via default-ctor + field-set.
**Resolution (Step 18 apply, 2026-07-04):** NOT REPRODUCIBLE on current HEAD.
The Q-NEWOBJ reproducer (`newarr; new T(intArg); assert`) PASSES. A JIT dump of
`localInfos` shows the newobj dest, the newarr array temp, and the int arg each
get a DISTINCT frame byte region (`Offset`) AND a DISTINCT mStack ref slot
(`RefOffset`); the planner's hypothesis (newarr doesn't decrement baseRegIdx ->
array temp collides with newobj dest/arg in the mStack ref region) is disproven
-- `AllocateLocalStackSpaces` already allocates distinct regions/ref-slots per
register. Same outcome as Q-STRUCT / Q-LONG (OPT-HARDEN: suspected quirk already
gone; Steps OPT-HARDEN/13b/17 likely resolved it). No fix shipped (a fix to the
shared call/newobj lowering without a reproducing case would be worse than none).
Step 16 TC4 restored to the real ctor-with-arg form (`new NeoStep16Item(5)`) and
passes.

### Q-VT-NEWOBJ — IL value-type `newobj` + `call VT ctor` via ldloca (Step 18 -> RESOLVED in [VT-THIS-ADDR])
**RESOLVED 2026-07-05.** Full NeoStep smoke 99/99 green (91 baseline + 8 new
TC8-TC15); Legacy-neutral. The apply-phase JIT-dump probes found the
propose-time root-cause hypothesis was PARTLY right but missed the true
load-bearing bug. Four fixes (all Neo-only):
1. **(LOAD-BEARING, pre-existing) inline-stfld owner-type clobber.**
   `TypeSpecializeNeoOpcodes` seeded the dest-temp type after EVERY inline
   rewrite -- including `Stfld_*_Inline`, where `Register1` is the OWNING VT,
   not a destination. This clobbered the owner's VT type, so the 2nd+
   `this.field=` on the same owner fell back to the heap arm (NRE). Fix: seed
   dest type ONLY for inline Ldfld (`IsInlineLdfldDestSeedable` helper). This
   single fix turned the common inlined `new VT(args)` local form green.
2. **D1 Newobj-dest typing** (as proposed): `case Newobj:` in the type-spec
   pass seeds `registerTypes[op.Register1] = ilVtType`. Needed for the
   non-inlined path (factory `S Make() { return new S(args); }`).
3. **Newobj dest temp sizing**: `GatherValueTypes` did NOT include Newobj, so
   a VT > 8 bytes got an undersized dest temp and the subsequent Move
   truncated to 8 bytes (silent field corruption). Added `case Newobj:`.
4. **D2 runtime copy-back** (NOT frame-native Ref Slot -- the `_Inline` arm
   writes through the owning slot's frame bytes directly, and caller dest is
   a separate frame buffer). Zero-init caller dest; copy dest prim INTO
   callee slot-0 (pre-call); copy ctor args; invoke; copy callee slot-0 BACK
   to caller dest. Ref-half runs in the Ret arm BEFORE the mStack pop
   (ExecuteNeo's `RemoveRange` destroys the slot-0 ref entries; added 4
   optional `vtNewobjCallerDst*` params to `ExecuteNeo`).
D3 (addrAlias VT-`this` root) was NOT needed -- the `ResolveLiveAlias`
fallback already resolves param slot 0 correctly. `Stfld_Value` (whole-VT-
into-VT-field store) is a SEPARATE Step 12b deferred item (TC10 adapted to
avoid it). See `openspec/changes/neo-vt-this-addr/design.md` "Apply-phase
findings" for full detail.

### F-2 / INLINER-REFONLY-VT — ref-only VT local newobj inliner mis-compile (-> future)
Surfaced by the neo-vt-this-addr re-review F-1 probe (`NeoStep18_TC16`:
`struct S { string a; string b; }`, TotalPrimitiveSize == 0). A ref-only VT
local constructed via `new S(refArgs)` (which the C# compiler + Neo inliner
lower to `initobj` + `stfld.ref.inline` -- NO `newobj`, so the F-1 Ret-arm
copy-back path is never entered) mis-compiles: the inlined `stfld.ref.inline`
writes do NOT survive to the following in-frame `ldfld.ref` read. Pre-existing
(latent -- the factory-return path that would force a real `newobj` is blocked
by the separate return-NIE at `ILIntepreter.Neo.cs:1861-1862` for a 0-prim/
multi-ref return). Confirmed NOT caused by neo-vt-this-addr: reverting the F-1
gate fix reproduces the identical failure, and TC11 (prim-size 4, reachable
Ret-arm ref-copy shape) passes. The reviewer did NOT ship a failing test
(would regress the smoke for an out-of-scope bug). **Resolution:** future --
fold into the K2-FAM bridge child (ref-field handling on VTs) or a small
[OPT-HARDEN-3] inliner-hardening. Suspect: the JIT inliner's ref-fold over a
0-prim-size VT local in `JITCompiler.cs`.

### F-3 / NEO-BYREF-THIS — `new ClrStruct(args)` + struct-instance-method byref-`this` (RESOLVED for direct `call`; callvirt still Step 17)

**RESOLVED 2026-07-05 (neo-step13-area4, direct-`call` shape).** A DIRECT `call`
on a CLR struct `this` now works: `new ClrStruct(args)` (lowers to
`initobj; ldloca; call .ctor`), a read-only `local.VTMethod()` on an in-frame
local, and a mutating instance method (mutation propagates back to the local).
The fix deviated from the design's literal reader-side deref (the readers lack
the real caller `frameBase`): instead, `CopyNeoCallArguments` derefs byref
sources at the copy site (new `NeoCallParamMap.PrimitiveByRefSrc` flag set in
`JITCompiler.cs` + the call-lowering in `Optimizer.Neo.cs`), so the callee param
region's `this` slot holds flat bytes; both readers (`CLRMethod.Invoke` HasThis +
the autogen `GenerateMethodWraperCode_Neo`) read it via `ReadNeoValueType`. NEW
`CopyNeoCallThisBack` (post-call reverse copy in the bare `Call` arm) propagates
ctor/mutating-method mutations to the caller's local (CLR `ref this` semantics).
Neo 117/117, NeoOptHard 16/16, Legacy `NeoStep13_` 9/9. Stash-toggle: 6/9 probes
FAIL-on-HEAD. The boxed `this` re-box (autogen D4) is NOT emitted (a boxed `this`
is only reachable via `constrained.callvirt`, a Step 17 NIE today). See
`openspec/changes/archive/2026-07-05-neo-step13-area4/`.

**NOT closed by this change (NOT a regression):** the `callvirt` /
`constrained.callvirt` shape on a CLR struct override compiles to a Step 17
`Constrained` NIE (`ILIntepreter.Neo.cs:~3140`) — that is the **Step 17
D-CONSTRAINED** follow-up (portfolio task #5 `neo-step17-completion`). Only the
direct-`call` path was in 4b's scope. **CAVEAT CLOSED 2026-07-05
(neo-step17-completion):** `constrained.callvirt` on a CLR struct now dispatches
(see D-CONSTRAINED §3 RESOLVED prepend) -- the F-3 callvirt caveat is fully
resolved.

---

Surfaced by the neo-opt-harden-2 round-1 re-review (the "is there a 4th broken
arm?" completeness sweep). A DIRECT `new ClrStruct(args)` in interpreted IL
(e.g. `new TestVector3NoBinding(100f,200f,300f)`) fails with
`ArgumentOutOfRangeException` at `CLRMethod.Invoke:353`. INVESTIGATION SHOWS THIS
IS A PRE-EXISTING GAP, NOT a regression from F-MAJ-1 or the round-1 consumer-arm
fix:

- The C# compiler does NOT emit `newobj` for `new ClrStruct(...)` assigned to a
  local. It lowers to `initobj r1; ldloca.s r8, r1; push r8; call
  ClrStruct::.ctor(...)` -- the struct is constructed IN-PLACE via a byref
  `this`, NOT via `InvokeNeoClrMethod(isNewobj:true)`. So the
  `InvokeNeoClrMethod(isNewobj:true)` boxed-ref-write path (which IS
  representation-inconsistent post-F-MAJ-1) is GENUINELY UNREACHABLE for a C#
  `new ClrStruct(...)` local init.
- The actual failure is at `CLRMethod.Invoke:353` reading the ctor `this`
  argument as a 4-byte mStack index. The `this` is a byref (8-byte Ref Slot from
  `ldloca`). This is a byref-`this`-to-CLR-struct-ctor reflection gap that has
  NEVER worked in Neo mode -- confirmed it fails IDENTICALLY on `f673b9c9`
  (pre-F-MAJ-1, pre-round-1).
- Same defect class as the byref-`this`-via-callvirt-on-a-CLR-struct gap (row 19
  of the round-1 sweep) and the broader "CLRMethod.Invoke reflection fallback
  only handles a 4-byte mStack-index `this`, not a frame-native byref"
  pre-existing limitation.
- It is NOT the F-MAJ-1 boxed-ref-vs-flat-bytes defect class. The reviewer did
  NOT ship a failing test (would regress the smoke for an out-of-scope bug);
  the probe was reverted.

**Resolution:** the direct-`call` shape RESOLVED in neo-step13-area4
(2026-07-05) — see the RESOLVED prepend at the top of this entry. The originally
hypothesized fix site (`CLRMethod.Invoke` reader-side deref) was superceded by
deref-at-the-copy-site (see ship-log §2); the readers now read flat bytes via
`ReadNeoValueType`. The `callvirt`/`constrained.callvirt` shape REMAINS future
work under Step 17 D-CONSTRAINED (`neo-step17-completion`).

### F-4 / NEO-IL-EX-FIELDACCESS — reading IL fields/methods off a caught IL exception is broken on Neo (-> future)
Surfaced by the neo-il-exception-throw apply (OQ1/OQ2). Once an IL exception
can be THROWN + CAUGHT end-to-end (this change), reading IL-declared fields or
methods off the CAUGHT exception object via the standard cross-binding-adaptor
bridge turns out to be broken on Neo. **PRE-EXISTING gap, NOT introduced by
neo-il-exception-throw** -- the change only (1) registers an adaptor and (2)
adds an unwrap fallback; it does NOT touch callvirt-on-CLR-interface /
`appdomain.Invoke` instance-method / `ILTypeInstance` indexer machinery. The
gap was simply unreachable before (no IL exception could be caught), so it was
invisible.

Four distinct broken read paths (each blocked by a separate pre-existing Neo
mechanism; the exception-throw change exposed all of them at once):
1. **`((CrossBindingAdaptorType)e).ILInstance` bridge** -- the standard
   cross-domain pattern requires `callvirt` on a CLR interface
   (`CrossBindingAdaptorType::get_ILInstance`) against the `Adapter` receiver;
   ExecuteNeo throws `InvalidCastException` ("Object does not match target
   type") for that callvirt-on-CLR-interface-where-receiver-is-the-Adapter
   shape.
2. **`e.GetType()`** -- NIE (`callvirt.clr` on `Object.GetType`).
3. **`appdomain.Invoke(instanceMethod, e)`** -- NRE: the public `Run`/`Invoke`
   re-entry path (`ILIntepreter.cs:87-120`) ignores the `instance` argument
   under `ENABLE_NEO_MODE` (the Step-6 entry shim handles only no-arg static
   methods), so an IL `get_Message` override has no `this`.
4. **`ILTypeInstance.this[index]` indexer** -- returns `null` under
   `ENABLE_NEO_MODE` (Legacy-only `StackObject[] fields` path; Neo uses
   `byte[] Primitives + AutoList ManagedObjects`).

The probe tests (`NeoStep14_ILEx_MessageField` et al.) use the `e is MyEx`
(isinst) workaround -- isinst is the same opcode the catch matcher uses and is
known-good on the `Adapter`. The `MyEx` class retains its `Msg` field +
`Message` override on the throw side; forwarding will activate once the
callvirt/indexer gaps close.

The reviewer did NOT ship a failing test for the broken read paths (would
regress the smoke for an out-of-scope bug). **Resolution:** future -- route to
Step 13 Area 4 (CLR binding codegen overhaul + cross-binding-adaptor
completion) or a dedicated cross-binding-adaptor follow-up. The fix touches
Neo callvirt-on-CLR-interface + `appdomain.Invoke` instance-method re-entry +
`ILTypeInstance` Neo indexer (all independent mechanisms; a single follow-up
likely closes all four for the caught-exception shape).

### F-5 / NEO-CALLARG-BOXED-SRC — boxed-source branch of CopyNeoCallArguments (latent dead-branch; -> RESOLVED neo-step17-completion)
**RESOLVED 2026-07-05 (neo-step17-completion).** When the Constrained arm landed,
it turned out the box-once BYPASSES `CopyNeoCallArguments` entirely -- the
Constrained arm writes the boxed mStack index directly into the callee slot
(manually skipping slot 0 in the map copy). So the boxed-source branch of
`CopyNeoCallArguments` (`PrimitiveByRefSrc` with `objIdx >= 0`) is genuinely
UNREACHABLE (`PrimitiveByRefSrc` is set ONLY for a VT instance `this` direct
`call`, where the source is a frame-native byref `objIdx == -1`, NOT `>= 0`).
The wrong defensive `CopyBlock` (which would treat an mStack field offset as a
struct address) was replaced with a tagged NIE-guard (no silent mis-copy). The
`CopyNeoCallThisBack` comment was tightened to "mutating INSTANCE METHODS, not
ctors" (the newobj VT-THIS-ADDR path performs its own slot-0 -> caller-dest
copy-back in `ExecuteNeo`'s Ret arm and does NOT invoke this). The stale
`Constrained` NIE text (T1) was also replaced. See
`openspec/changes/archive/2026-07-05-neo-step17-completion/ship-log.md`.

Surfaced by the neo-step13-area4 review (Finding M2). The byref-deref
discriminator added in `CopyNeoCallArguments` (`ILIntepreter.Neo.cs:~295-300`)
has an `else` branch for a boxed-struct `this` source (`objectIndex >= 0`) that
performs `CopyBlock(targetBase + Dst[i], frameBase + offset, Size[i])` — but for
a boxed source, `offset` is an mStack field offset, so `frameBase + offset` is
NOT a meaningful struct address. The branch would mis-copy IF reached.

**Reachability:** UNREACHABLE today. A boxed `this` is ONLY produced via
`constrained.callvirt`, which hits the Step 17 `Constrained` NIE
(`ILIntepreter.Neo.cs:~3140`). So a direct `call` always uses `ldloca`
(frame-native byref, `objectIndex == -1`). The branch is dead-on-arrival until
the Step 17 Constrained arm is completed.

**Related comment-accuracy nit:** the new `CopyNeoCallThisBack` comment claims it
propagates ctor mutations, but the newobj path does NOT invoke it (the ctor's
result flows via the method RETURN through `InvokeNeoClrMethod`'s newobj branch,
not via the reverse copy). Behavior is correct; the comment is misleading.

**Resolution:** future -- the Step 17 D-CONSTRAINED completion child
(`neo-step17-completion`, portfolio task #5) MUST add a NIE guard (or a correct
copy) in the boxed-source branch of `CopyNeoCallArguments` when it lands the
Constrained path, AND tighten the `CopyNeoCallThisBack` comment to cover mutating
INSTANCE METHODS (not ctors). Not a regression (latent dead code); recorded so
the Step 17 planner finds it.

### F-6 / NEO-VT-FLDADDR — `ldflda`-on-in-frame-VT (-> RESOLVED `neo-vt-ldflda-inline`)
**RESOLVED 2026-07-06 (neo-vt-ldflda-inline).** Fixed via a marker-stamp + a
3-way runtime dispatch (all Neo-only; Legacy untouched). The JIT
`TypeSpecializeNeoOpcodes` `case OpCodeREnum.Ldflda:` stamps
`op.Operand4 |= NeoLdfldaInlineMarker (0x1)` when the source `Register2` is an
in-frame IL value type (the SAME condition that seeds the dest type; pre-
lowering so `Register2` is still a register index). `Operand4` (standalone
field, offset 20) was confirmed UNUSED for `Ldflda` at HEAD (no collision).
The runtime `case OpCodeREnum.Ldflda:` arm now does a 3-way dispatch keyed on
the marker + the operand slot's leading int: marker + leading-int `== -1` ->
shape 1/2 frame-native Ref Slot (byte-identical to the existing frame-native
branch); marker + leading-int `!= -1` -> **shape 3 flat-bytes (the F-6 fix)**
— the operand slot holds the struct's flat primitive bytes, so the slot's own
frame byte offset IS the struct base; produce `(-1, operandSlotOff +
fieldPrimOff)`; marker absent -> the existing heap-IL / CLR-object dispatch
byte-identical. The `addrAlias` folding is unchanged (the marker is on
`Operand4`, which the COEXIST gate never reads for `Ldflda`). Load-bearing
stash-toggle: probe `NeoStep17_LdfldaInline_StructMethodFlatBytes` FAILS on HEAD
with `Index was out of range` (ldflda reads id=42 as an mStack index ->
`mStack[42]` OOB); PASSES with the fix (marker branch -> `(-1, 0)` -> reads 42).
NeoStep smoke 154/154 (146 baseline + 8 new probes); Legacy-neutral. Blast-
radius sweep confirmed all 4 Ldflda operand kinds safe. Review APPROVED (0
Blocker/Major; 2 Minor/Trivial findings accepted-known):

- **F-R1 (Minor, accepted-known):** the implementer's "key deviation" rationale
  (the literal `id.ToString()` body was claimed blocked by a separate F-3 gap)
  DOES NOT REPRODUCE in independent reconstruction — the literal body PASSES in
  BOTH configurations (with the fix AND with it stashed); for an `int` field the
  C# compiler emits a by-value `ldfld` + a value-`this` `call`, never a byref
  to a CLR method. F-6 correctness is unaffected; the deviation note is
  softened to "the literal body was avoided out of caution / probe-isolation
  preference; the F-3 interaction is not reproducible."
- **F-R2 (Trivial, accepted-known):** probe 4.8 is a byref-of-primitive local,
  not a CLR-object-field `ldflda`. The genuine CLR-object-field `ldflda`
  operand kind remains UNCOVERED (a real CLR-object `ldflda` carries no marker
  and hits the existing `else` branch, so F-6 correctness is unaffected, but
  the probe set does not independently cover it). Deferred to the Step-17
  stind/ldind follow-up `neo-step17-stobj-refloop` (task #18) — same family as
  the D-CONSTRAINED stobj-refloop / CLR-object-field-hash deferral.

See `openspec/changes/archive/2026-07-06-neo-vt-ldflda-inline/ship-log.md`.

Surfaced by the neo-step17-completion apply phase (the IL-struct `ToString`
override probe). The `Ldflda` arm reads `*(frameBase + operandSlotOff)` as an
mStack objIdx; an in-frame VT operand slot holds flat bytes -> the first
field's value, read as an index (garbage). There is NO `Ldflda_Inline` (only
`Ldfld_*_Inline` / `Stfld_*_Inline`). So `ldflda this.field` on an in-frame VT
is broken -- any IL-struct method that takes a field address
(`field.ToString()`, `ref field`, `fixed`) fails on Neo REGARDLESS of
constrained. **PRE-EXISTING gap, NOT introduced by neo-step17-completion** --
the Ldflda opcode handler is untouched by that change and would reproduce on
HEAD for any IL-struct method taking a field address. The constrained DISPATCH
itself is independently validated (the IL-VT interface direct-call probe uses
`Ldfld_I4_Inline`, not `ldflda`).

**Resolution:** future -- new follow-up child `neo-vt-ldflda-inline` (portfolio
task #19): add a `Ldflda_Inline` / extend the Ldflda arm to recognise an
in-frame-VT operand via the type-spec seed (mirror the `Ldloca` / `Ldflda`
dest-typing rules from `neo-vt-this-addr`). Recorded so the Step 17 follow-up
planner finds it.

### F-7 / NEO-DELEGATE-REFOUT — delegate ref/out param marshaling in NeoInvoke (-> future byref follow-up)
Surfaced by Step 19 (neo-step19-delegate). `DelegateAdapter.NeoInvokeSub`
(the CLR -> IL callback path, e.g. `List.ForEach(ilAction)`) writes the CLR
args into the callee param region via `WriteNeoCallSlot`, which handles
primitives / reference args / CLR value types but NOT a **byref-typed delegate
param** (a `ref T` / `out T` parameter on an `Action<>`/`Func<>` Invoke).
A byref arg is an 8-byte Ref Slot `(objectIndex, offset)`; `WriteNeoCallSlot`
has no byref-aware arm for the delegate-callback direction (the byref Ref Slot
model lives in Step 17's `ldelema`/`stind`/`ldind` consumers and the
`CopyNeoCallArguments` byref-deref flag from neo-step13-area4, neither of
which `NeoInvokeSub` consults). The return-side `WriteNeoDelegateInvokeReturn`
has the symmetric gap for a `ref`/`out` return.

**Pre-existing / latent, NOT a Step 19 regression.** The byref-on-delegate
shape has never worked on Neo (delegates did not exist on Neo before Step 19).
Step 19 probe 8 (`NeoStep19_*`) deliberately uses a **plain `int`** param to
exercise the IL-delegate construct + Invoke routing — the load-bearing
assertion for the `NeoInvokeSub` path — and is green on both engines. The
byref variant was scoped OUT and recorded, not silently dropped
(`design.md` + `tasks.md`).

**Resolution:** future -- route to a byref follow-up child (same family as
D-13B area 4c CLR-method `ref`/`out` typed-ref bridge and the
`neo-step17-stobj-refloop` byref work). The fix makes `NeoInvokeSub`'s
arg-write / return-read byref-aware (read the `(objectIndex, offset)` Ref Slot,
deref to the underlying frame/mStack slot, write the address into the callee
param region — mirroring how `CopyNeoCallArguments`'s `PrimitiveByRefSrc` flag
handles a byref `this` for a direct `call`). Recorded so the byref-follow-up
planner finds it. See
`openspec/changes/archive/2026-07-06-neo-step19-delegate/ship-log.md`.

**NOTE (2026-07-06, neo-step13-area4-refandstind):** the 4c typed-ref bridge
that shipped in `neo-step13-area4-refandstind` is the **IL->CLR** direction
(an IL method passes a byref to a CLR method). F-7
(`DelegateAdapter.NeoInvokeSub`) is the **CLR->IL** callback direction (a
delegate Invoke calls back into an IL method that takes a byref param). They
are DIFFERENT sites with OPPOSITE directions — the 4c helper does NOT apply to
F-7. F-7 remains OPEN; it is not closed by 4c.

### F-8 / NEO-DOUBLE-COMBINE — 2+ double locals combined in one boolean expression (RESOLVED 2026-07-06, neo-double-combine-quirk / [OPT-HARDEN-3], D4)
Surfaced by the neo-array-completion review (Finding F-1, Probe #4). When a
method reads 2+ `double` values into separate locals and combines them in a
single boolean expression (e.g.
`if (a0 != expected0 || a1 != expected1)`), the comparison SILENTLY
MISFIRES (DivideByZero on the assertion trip — i.e. the `||` evaluates the
wrong branch / a `double` reads as 0). **Refined characterization:** the
quirk is NOT "3+ locals" and NOT all 8-byte primitives — it is **2+ `double`
locals combined in one boolean expression**. A single `double` read is
correct; combine two in one `if` and it misfires. 3 `long` locals combined
work fine (the quirk is `double`-specific). This is the **F-MAJ-1 class**
(silent wrong result on 8-byte primitives).

**Pre-existing / latent, NOT introduced by neo-array-completion.** The
failing comparison uses plain `Ldelem_R8` reads (the pre-existing fast typed-
indexer path, NOT the new `is Array` branches) and a pure `double`-local
combine. The corruption is in how the optimizer/runtime handles 2+
simultaneous `double` locals in a combined expression — upstream of, and
independent from, the array work. The implementer correctly worked around it
(incremental `bad`-fold pattern: read one element, fold into a running
`bool bad`, never combine two `double` reads in one expression) in TC11/TC12/
TC14 and flagged it.

**Suspect:** `AllocateLocalStackSpaces` 8-byte-primitive slot handling (same
family as F-MAJ-1 / OPT-HARDEN-2), or the `double`-local combine in copy-
prop. Exact locus NOT pinned in the review (no dump probe of the failing
frame layout). **Severity: Major (silent wrong result), pre-existing.**

**RESOLVED 2026-07-06 (neo-double-combine-quirk, D4).** The propose-time
leading candidate D2 (R8 type-spec mis-types the combine -- `Ldelem_R8` dest
lacks a registerType seed) was REFUTED by the dump (the dest registers ARE
correctly seeded `System.Double`; the emitted opcodes ARE `*_R8`). The actual
defect is a D4 shape the propose had dismissed as a "long shot, refuted in
principle": in `Optimizer.Neo.cs LowerNeoOffsets`, the immediate-branch case
stamped `op.Operand3 = localInfos[r1].RefOffset` for EVERY immediate branch
(I4/I8/R4/R8). `OpCodeR` is `[StructLayout(LayoutKind.Explicit)]` --
`Operand3` (@16) overlaps the HIGH 4 bytes of `OperandLong`/`OperandDouble`
(@12-19); `Operand3` is NEVER READ by any immediate-branch runtime arm, so the
write is dead -- but destructive for I8/R8 (clobbers the 8-byte immediate's
high 4 bytes). The `long`-works / `double`-fails split: copy-prop folds a
`double` `Ldc_R8` INTO the immediate form (`Bnei_Un_R8`, corruption reachable)
but keeps `Ldc_I8` in a register (`Bne_Un_I8`, I8 immediate form never
produced -> corruption unreachable). **Fix:** gate the dead `Operand3` write
OFF for the I8/R4/R8 immediate-branch forms (single `immLarge` boolean); I4
byte-identical (its immediate `Operand` @8 is disjoint from @16). Neo-only
(`#if ENABLE_NEO_MODE`); NO `AllocateLocalStackSpaces` change (the 8-byte-slot
hypothesis was propose-refuted -- double + long get byte-identical 8-byte
slots). **Accepted-known:** F2 -- `immLarge` includes R4 unnecessarily (R4's
`OperandFloat` @8-11 is disjoint from `Operand3` @16, so the dead write was
harmless for R4; the broader set is safe, just not minimal). **Verification:**
NeoStep 161/161, NeoOptHard 24/24 (16 K1/F-MAJ-1 + 8 new `NeoOptHardTest_Dbl_*`),
Legacy-neutral; Block-0 dump-gate + stash-proven pre-existing; 5 probes
FAIL-on-HEAD -> PASS; review APPROVED. **The OpCodeR union gotcha** -- a
recurring class (handoff §4 warns; this is the 3rd instance after Step 12 +
OPT-HARDEN). See
`openspec/changes/archive/2026-07-06-neo-double-combine-quirk/ship-log.md`.
(The previous "future" resolution is preserved below for the history trail.)
See `openspec/changes/archive/2026-07-06-neo-array-completion/ship-log.md` (F-1).

**Resolution (prior, pre-fix):** future -- new follow-up child
`neo-double-combine-quirk` / [OPT-HARDEN-3]. The fix should dump-gate the
failing frame layout (the F-MAJ-1 discipline: probe BEFORE designing the fix;
STOP if the designed fix is wrong). Recorded so the optimizer-hardening
planner finds it. See
`openspec/changes/archive/2026-07-06-neo-array-completion/ship-log.md` (F-1).

### F-9 / NEO-INLINED-RETURN-MOVE — inlined IL-method return-value misclassification (-> future inliner/optimizer follow-up)
Surfaced by the neo-step13-area4-refandstind review (the 4d.2 probe
`LdindClrIntFieldPeek` had to be deliberately structured to defeat it). When an
IL method's return value (e.g. an `int`) flows from an INLINED IL method call
into its caller, the trivial-inliner mis-classifies the return value: an `int`
returned from an inlined IL method is MOVED AS A REFERENCE, so a subsequent
read interprets the int value as an mStack index -> `mStack[intValue]` OOB.

**Pre-existing / latent, NOT introduced by neo-step13-area4-refandstind.** The
4d work only needs to drive the `ldflda clrObj.field; ldind_i4` path; the
return-move edge is in the inliner's return-value classification, upstream of
4d. The 4d.2 probe avoids it by structuring the body as
`int v = slot; return v + 0;` (the `+ 0` defeats the trivial-inliner's
return-move fold). The reviewer correctly flagged it rather than silently
dropping it.

**Resolution:** future -- route to an inliner/optimizer follow-up (the
trivial-inliner's return-value classification in `JITCompiler.cs`). Suspect:
the inliner's return-move fold does not consult the inlined method's declared
return type when the call site folds the return into the caller's move. The
reviewer did NOT ship a failing probe (would regress the smoke for an
out-of-scope bug); the probe-avoidance in 4d.2 is the load-bearing evidence
the edge is real. Recorded so a future inliner-hardening change finds it. See
`openspec/changes/archive/2026-07-06-neo-step13-area4-refandstind/ship-log.md`.

### Q-STRUCT — struct-local + field-mutation + element-read temp-renumber (Step 16 -> deferred)
A struct local, followed by a field mutation, followed by an element read, was
suspected to hit an optimizer temp-renumber quirk (BCP/copy-prop). **OPT-HARDEN
probe result: NOT reproducible on current HEAD** — 6 probes pass (incl. the exact
Step 16 TC5 array/mutation/read pattern + high-register-pressure variants); the
JIT shows the ldelem dest is correctly distinct from the source local. Suspect
location pinned (`Optimizer.BCP.cs:97-141` renumber) for recovery IF a
reproducing case surfaces. No fix shipped (a guessed fix to the shared pass would
be worse than none).

### Q-LONG — long default-zero compare (conv.i8) quirk (Step 16 -> deferred)
A long default-zero compare (involving `conv.i8` + compare) was suspected to
mis-evaluate. **OPT-HARDEN probe result: NOT reproducible on current HEAD** — 3
probes pass (array, scalar-local w/ register pressure, default-field); the conv
readers and I8 compare/branch arms read `*(long*)` correctly; `InferPrimTag`→
`_I8` widening is correct. Suspect location pinned (JIT branch
type-specialization / `AllocateLocalStackSpaces` 4-vs-8-byte overlap) for
recovery IF a reproducing case surfaces. No fix shipped.

### D-CHECKEX — `CheckExceptionType` NIE for non-CLRType catch types (Step 14 -> RESOLVED [CATCH-COMPLETE] + neo-il-exception-throw)
RESOLVED 2026-07-05. The shared engine's `CheckExceptionType`
(`ILIntepreter.cs:~5823`) threw NIE for catch types that are not CLRType (an
`ILType` catch clause). Neo catch matching uses this shared path. **RESOLUTION
in two stages:**
- **(CATCH-COMPLETE, 2026-07-04):** the NIE is replaced with an IL branch —
  `exception as ILTypeInstance` → exact or `CanAssignTo(catchType)` (reuses
  Step 15's CanAssignTo) → else CLR fallback. Shared-engine (NOT Neo-gated);
  Legacy-neutral (the new branch is unreachable for every existing CLRType
  catch). D-CHECKEX's CheckExceptionType piece was CLOSED at this stage, but
  the branch was DEAD CODE (no IL exception could be thrown yet).
- **(neo-il-exception-throw, 2026-07-05):** the IL branch is now REACHABLE
  end-to-end for the first time -- an IL exception class can finally be loaded
  (Exception-adaptor) and thrown (Throw-unwrap), so the IL-catch matcher
  branch actually fires for a thrown IL exception. See D-IL-EXCEPTION-THROW.

D-CHECKEX is now FULLY RESOLVED (both the matcher piece and the end-to-end
reachability). See `openspec/changes/archive/2026-07-05-neo-il-exception-throw/`.

### D-IL-EXCEPTION-THROW — end-to-end IL-exception catch (Exception-adaptor + Throw-for-IL) (-> RESOLVED)
RESOLVED 2026-07-05. Even with D-CHECKEX's CheckExceptionType branch, throwing
an IL-typed exception and catching it end-to-end was blocked on TWO more pieces:
(a) a registered `System.Exception` `CrossBindingAdaptor` (an IL
`class X : System.Exception` throws TypeLoadException at `ILType.cs:1418`
without it); (b) the `Throw` opcode does `mStack[idx] as Exception` on BOTH
engines (`ILIntepreter.Neo.cs:~3204`, `ILIntepreter.Register.cs:~5307`) → a
plain IL class (an `ILTypeInstance`, not an Exception) NREs.

**Resolution (neo-il-exception-throw, shared-engine, NOT Neo-gated):**
1. NEW `ExceptionAdaptor` (`ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs`):
   nested `Adapter : System.Exception, CrossBindingAdaptorType` mirroring
   `AttributeAdapter` exactly; forwards `ToString()` only (Message forwarding
   blocked by separate pre-existing Neo gaps -- see F-4). Registered as a
   built-in in the `AppDomain` ctor (~line 231, next to `AttributeAdapter`).
2. Throw-unwrap in BOTH arms: after `o as Exception`, fall back to
   `((ILTypeInstance)o).CLRInstance as Exception` (the adaptor's `Adapter`,
   which IS-A CLR `Exception`); else the existing NullReferenceException guard.
   Sites: Neo `GetNeoException` (`ILIntepreter.Neo.cs:~3204`) + Legacy `Throw`
   (`ILIntepreter.Register.cs:~5307`). Byte-identical for existing CLR-Exception
   operands (the first `as` succeeds; IL fallback unreachable).

**Verification:** Neo 108/108 (100 baseline + 8 `NeoStep14_ILEx_*`); Legacy
617 tests / 9 failed (ALL pre-existing; the 3 NeoStep14 TC failures are
`ArgumentOutOfRangeException` in `List.set_Item` for CLR-exception tests, where
the IL fallback is unreachable -> Throw-unwrap exonerated). Stash-toggle: with
the fix stashed, the whole session crashes at `TestSession.LoadTest()` with
`TypeLoadException: Cannot find Adaptor for:System.Exception` (gap a load-
bearing). Review APPROVED (0 Blocker/Major; doc-only M1 methodology + M2
breaking-change callout).

**Side-benefit:** CATCH-COMPLETE's `CheckExceptionType` IL branch is now
exercised end-to-end for the first time.

**Surfaced F-4 / NEO-IL-EX-FIELDACCESS** (pre-existing Neo gap; reading
IL-declared fields/methods off a caught IL exception via the adaptor bridge is
broken -- see F-4 detail). Workaround: `e is MyEx` (isinst). Route to Step 13
Area 4 / cross-binding-adaptor follow-up.

See `openspec/changes/archive/2026-07-05-neo-il-exception-throw/ship-log.md`.

### D-PEEP — `box T; isinst U` peephole + `PatchKind.IsinstResult` (Step 15 -> opportunistic)
The compile-time peephole (detect `box T; isinst U`, statically resolve) and the
generic-parameter `PatchKind.IsinstResult` patch-table entry were deferred because
**neither the fusion pass nor `PatchKind` exists in this codebase**. `box->isinst`
executes correctly via the two runtime arms (Step 15) without fusion, so this is
pure optimization (non-functional). **Resolution:** opportunistically, when a
patch-infra step lands (possibly related to HybridPatch, or a dedicated
optimizer-features step). Low priority.

### D-ARR — array-completion gaps (Step 16 -> PARTIAL RESOLVED 2026-07-06, rank-1)
**PARTIAL RESOLVED 2026-07-06 (neo-array-completion, rank-1).** The proposal's
"5 gaps" collapsed to **2 real gaps** in this Mono.Cecil fork: `Code.Ldelem`
(generic) IS `Code.Ldelem_Any`, `Code.Stelem` (generic) IS `Code.Stelem_Any`
(both already handled), and `Code.Ldelem_U8` does NOT EXIST (not a real ECMA
opcode — an 8-byte unsigned load is just `Ldelem_I8`). The 2 real gaps:

- **`Stelem_I` + `Ldelem_I` runtime arms** (`ILIntepreter.Neo.cs`): Option A
  (`Stelem_I` goto `Stelem_I4`) REJECTED (Stelem_I4's typed casts only handle
  `int[]`/`uint[]` -> `IntPtr[]` `InvalidCastException`); Option B = dedicated
  arms dispatching on `int[]`/`uint[]`/`IntPtr[]`/`UIntPtr[]` (native-int is
  I4-width on this VM; dump-confirmed). `Ldelem_I` also added to the JIT
  `Translate` switch + a 5-site optimizer cascade (4 `Optimizer.Utils.cs` +
  `LowerNeoOffsets`); reviewer confirmed NO MISS (FCP/BCP/RegisterCleanup do not
  enumerate the `Ldelem_*` family).
- **F-4 other-width CLR-array Stind/Ldind branches:** the step17-completion
  I4-only `mStack[objIdx] is Array` branch extended to `Stind_I1/I2/I8/R4/R8` +
  `Ldind_I1/U1/I2/U2/U4/I8/R4/R8` + `Stind_Ref`/`Ldind_Ref`.

**Verification:** NeoStep 161/161 (154 + 7 probes). Stash-toggle TC8/TC12/TC13/
TC14 FAIL-on-HEAD -> PASS. Legacy-neutral. Review APPROVED (0 Blocker/Major
introduced; F-1 double-combine quirk pre-existing follow-up; F-2/F-3
Minor/Trivial). Accepted-known upstream gaps: TC9 (UIntPtr[] — unsupported
primitive), TC15 (ref-array — Neo `ldelema` NIEs on CLR ref-type arrays).
See `openspec/changes/archive/2026-07-06-neo-array-completion/ship-log.md`.

**STILL DEFERRED -> `neo-array-multidim` (separate child):** multi-dimensional
arrays (rank-2+). The rank-aware `Address`/`Get`/`Set` `callvirt`, the rank-
aware frame model, and `new T[n,m]` construction do NOT fall out of the rank-1
work. Stays an untagged JIT `NotImplementedException` today.

---

- `Stelem_I` is lowered (correct 3-register encoding) but has NO interpreter arm
  (Step-tagged NIE) — rare `IntPtr[]`/`UIntPtr[]` native-int store.
- generic-token `Code.Ldelem`/`Code.Stelem` and native `Code.Ldelem_I`/`Ldelem_U8`
  are not enumerated by JIT `Translate` -> JIT-time NIE; rare in C# output.
- multi-dimensional arrays: rank-1 only.
**Resolution:** rank-1 RESOLVED 2026-07-06 (neo-array-completion); multi-dim
deferred to `neo-array-multidim`.

### N-CGTUN — Cgt_Un divergence comment (Step 15 -> opportunistic)
**RESOLVED 2026-07-06 (neo-opportunistic-cleanup).** The `Cgt_Un` arm's
divergence comment in `ILIntepreter.Neo.cs` now names BOTH symmetric sentinel
collisions: (a) the operand case `cgt.un x, (uint)0xFFFFFFFF` (`cguB == -1` →
true) AND (b) the source case `cgt.un (uint)0xFFFFFFFF, x` (`cguA != -1`
clause → false). Comment-only — the runtime comparison expression
(`cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1)`) is byte-identical.
See `openspec/changes/archive/2026-07-06-neo-opportunistic-cleanup/ship-log.md`.

The `Cgt_Un` arm's divergence comment names only the operand=sentinel case; the
symmetric source=sentinel case also diverges (same sentinel-collision class,
unexercised by the whole TestCases suite). Cosmetic. **Resolution:** one-line
comment tighten, anytime.

### N-CATCHWRAP — catch slot stores ILRuntimeException wrapper (Step 14 -> accept)
The Neo catch slot stores the `ILRuntimeException` wrapper (not the unwrapped
inner), which MATCHES Legacy exactly (`ILIntepreter.Register.cs:5327`). The
spec's "unwrapped" wording is loose. Not a bug. **Resolution:** accept; revisit
only if a real symptom appears (e.g. when isinst-on-caught-exception is exercised).

### N-TC2 — Step 14 TC2 asserts `e != null` (Step 14 -> resolved by Step 15 -> test-tighten done)
**RESOLVED 2026-07-06 (neo-opportunistic-cleanup).** The follow-up test-tighten
is now done: `NeoStep14_TC2_CatchObjectAccess` asserts
`e is DivideByZeroException && e.Message != null` (was `e != null`). The `is`
lowers to `isinst`, exercising the type-check-in-catch shape now that Step 15
`isinst` has landed. TC2 asserts strictly more; NeoStep 154/154. See
`openspec/changes/archive/2026-07-06-neo-opportunistic-cleanup/ship-log.md`.

Step 14 TC2 asserted `e != null` because the type-check-in-catch (`isinst`) was
Step 15. Step 15 has now landed isinst. **Resolution:** TC2 can be tightened to
assert the exception type/identity; opportunistic cleanup.

---

## 4. Resolved
- **F-8 / NEO-DOUBLE-COMBINE** — 2+ `double` locals combined in one boolean
  expression silently misfire. RESOLVED 2026-07-06 (neo-double-combine-quirk,
  [OPT-HARDEN-3], D4): dump-confirmed the propose-time D2 type-spec candidate
  REFUTED (R8 dests ARE seeded; opcodes ARE `*_R8`) and the D4 lowering
  candidate CONFIRMED. The dead `op.Operand3 = localInfos[r1].RefOffset` write
  in `Optimizer.Neo.cs LowerNeoOffsets`'s immediate-branch case clobbers the
  high 4 bytes of `OperandDouble`/`OperandLong` (@12-19) via the
  `[StructLayout(Explicit)]` union (`Operand3` @16 is never read by any
  immediate-branch arm -- dead but destructive for I8/R8). Copy-prop folds
  `Ldc_R8` into `Bnei_Un_R8` (reachable) but keeps `Ldc_I8` register-register
  (`Bne_Un_I8`, I8 immediate form never produced -> unreachable) -- that is
  the long-works/double-fails discriminator, NOT 8-byte-slot sizing (double +
  long get byte-identical 8-byte slots). Fix: gate the dead `Operand3` write
  OFF for I8/R4/R8 immediate-branch forms (single `immLarge` boolean); I4
  byte-identical. Neo-only (`#if ENABLE_NEO_MODE`); NO
  `AllocateLocalStackSpaces` change. Accepted-known: F2 -- `immLarge` includes
  R4 unnecessarily (harmless; R4's `OperandFloat` @8-11 is disjoint from
  @16). Verification: NeoStep 161/161, NeoOptHard 24/24 (16 K1/F-MAJ-1 + 8
  new `NeoOptHardTest_Dbl_*`), Legacy-neutral; stash-proven pre-existing; 5
  probes FAIL-on-HEAD -> PASS; review APPROVED. **The OpCodeR union gotcha**
  -- 3rd concrete instance (after Step 12 + OPT-HARDEN); handoff §4 warns.
  See §3 F-8 / NEO-DOUBLE-COMBINE.
- **N-CGTUN** — `Cgt_Un` divergence comment. RESOLVED 2026-07-06
  (neo-opportunistic-cleanup, comment-only): the `ILIntepreter.Neo.cs` comment
  now names BOTH symmetric sentinel collisions — (a) operand
  `cgt.un x, (uint)0xFFFFFFFF` (`cguB == -1` → true) AND (b) source
  `cgt.un (uint)0xFFFFFFFF, x` (`cguA != -1` clause → false). The runtime
  expression is byte-identical (comment-only edit). NeoStep 154/154. See
  §3 N-CGTUN.
- **N-TC2** — Step 14 TC2 `e != null` assertion. RESOLVED 2026-07-06
  (neo-opportunistic-cleanup, test tighten): TC2 now asserts
  `e is DivideByZeroException && e.Message != null`, exercising `isinst` on a
  caught exception (the type-check-in-catch shape). Asserts strictly more.
  NeoStep 154/154. See §3 N-TC2.
- **F-6 / NEO-VT-FLDADDR** — `ldflda`-on-in-frame-VT (the Ldflda arm read the
  operand slot as an mStack objIdx; an in-frame VT slot holds flat bytes ->
  garbage). RESOLVED 2026-07-06 (neo-vt-ldflda-inline, Neo-only): marker stamp
  (`NeoLdfldaInlineMarker = 0x1` in standalone `Operand4`, stamped in
  `TypeSpecializeNeoOpcodes case Ldflda:` when the source is an in-frame IL VT
  — the dest-type-seed condition, pre-lowering) + a 3-way runtime dispatch
  (marker + leading-int: `-1` -> frame-native; else marker -> flat-bytes shape
  3; else -> heap/CLR). addrAlias folding unchanged (the marker is invisible to
  the COEXIST gate). Load-bearing stash-toggle (shape-3 probe: FAIL-on-HEAD ->
  PASS-after-fix). NeoStep 154/154; Legacy-neutral. Review APPROVED (0
  Blocker/Major; F-R1 + F-R2 accepted-known). F-R2 follow-up: the CLR-object-
  field `ldflda` operand kind remains uncovered -> `neo-step17-stobj-refloop`.
  See §3 F-6 / NEO-VT-FLDADDR.
- **K2-FAM** — Move-path scalar->boxed-ref CLR-VT-local (reads int as mStack
  index). RESOLVED 2026-07-06 (neo-k2fam-bridge, TEST-ONLY — subsumed): the
  closure was a side effect of `neo-opt-harden-2` (F-MAJ-1: declared a CLR-VT
  local as flat bytes — the OLD boxed-ref representation that produced the
  corruption NO LONGER EXISTS) + its review-fix (rewrote the Initobj/Box/
  Unbox_Any arms to read/write flat bytes) + `implement-neo-step13b` (unified
  by-value-param flat-bytes read). No new engine fix shipped; instead 6
  adversarial regression guards added to `TestCases/NeoStep13bTest.cs`
  (`NeoStep13_K2Fam_*`) lock the closure in. NeoStep 146/146, K2Fam 7/7,
  Legacy-neutral. See §3 K2/K2-FAM.
- **D-CONSTRAINED ({a,d,M2} scope)** — `constrained.callvirt T.M` on a value
  type `T`. Fixed in neo-step17-completion (2026-07-05, Neo-only): the runtime
  `Constrained` arm OWNS the dispatch (JIT order is `[Push..., Constrained T,
  Callvirt M]` -- Constrained runs FIRST, disproving the design's Option F
  fusion premise); IL-vs-CLR discriminator (IL-VT + ILMethod override ->
  direct-call; IL-VT + inherited CLRMethod -> box into ILTypeInstance via
  CopyFrameToIL; CLR-VT -> box-once via ReadNeoValueType; already-boxed ->
  no-op); `ip += 2` skips the trailing callvirt. The round-1 F1 review-fix added
  the IL-VT-inherited-CLRMethod box sub-branch so `anyIlStruct.ToString()`
  works (was a Blocker segfault regression). NeoStep 130/130, NeoOptHard 16/16,
  Legacy-neutral. STILL OPEN: (b) Stobj/Ldobj ref-loop + (c) generic-byref /
  `fixed` / interface-on-VT-constrained + IL-VT-with-ref-fields constrained ->
  follow-up `neo-step17-stobj-refloop`. See §3 D-CONSTRAINED.
- **D-LDELEMA (fully)** — `ldelema` opcode. Fixed in Step 17 (2026-07-04, IL VT
  array path) + neo-step17-completion (2026-07-05, the CLR primitive-array
  remainder: `Ldelema` `else` branch encodes `(arrIdx, elementIdx)`; Stind_I4 /
  Ldind_I4 gained a `mStack[objIdx] is Array` branch). See §3 D-LDELEMA.
- **F-5 / NEO-CALLARG-BOXED-SRC** — boxed-source branch of
  `CopyNeoCallArguments`. Closed in neo-step17-completion (2026-07-05): the
  box-once BYPASSES `CopyNeoCallArguments` (genuinely unreachable); the wrong
  defensive `CopyBlock` replaced with a tagged NIE-guard + the
  `CopyNeoCallThisBack` comment tightened. Closes the area4 M2 obligation. See
  §3 F-5.
- **F-3 / NEO-BYREF-THIS (direct-`call` shape)** — CLR/IL struct instance
  method via byref `this` (`new ClrStruct(args)`, `local.VTMethod()`). Fixed in
  neo-step13-area4 (2026-07-05, Neo-only): `CopyNeoCallArguments` derefs byref
  sources at the copy site (new `PrimitiveByRefSrc` flag); the callee param
  region's `this` slot holds flat bytes; `CopyNeoCallThisBack` propagates
  mutations. Neo 117/117, NeoOptHard 16/16, Legacy `NeoStep13_` 9/9. Stash-toggle
  6/9 FAIL-on-HEAD. CAVEAT: the `callvirt`/`constrained.callvirt` shape REMAINS
  a Step 17 D-CONSTRAINED follow-up (NOT closed by this change). Surfaced F-5 /
  NEO-CALLARG-BOXED-SRC (latent dead-branch). See §3 F-3 / NEO-BYREF-THIS.
- **D-IL-EXCEPTION-THROW** — End-to-end IL-exception catch. Fixed in
  neo-il-exception-throw (2026-07-05, shared-engine): built-in
  `ExceptionAdaptor` + Throw `as Exception` IL-instance unwrap in BOTH Neo
  `GetNeoException` and Legacy `Throw`. NeoStep smoke 108/108; Legacy-neutral
  (9 pre-existing failures, Throw-unwrap exonerated). CATCH-COMPLETE's
  `CheckExceptionType` IL branch now reachable end-to-end. Surfaced F-4 /
  NEO-IL-EX-FIELDACCESS (pre-existing). See §3 D-IL-EXCEPTION-THROW.
- **D-CHECKEX** — `CheckExceptionType` NIE for non-CLRType catch types. Now
  FULLY RESOLVED: CATCH-COMPLETE (2026-07-04) closed the matcher piece;
  neo-il-exception-throw (2026-07-05) made the IL branch reachable end-to-end
  (was dead code before). See §3 D-CHECKEX.
- **K1** — FCP value-type-move mis-propagation. Fixed in OPT-HARDEN (2026-07-04)
  via the `ldloca-kill` (Neo-only, Legacy-neutral). See §3 K1.
- **K2** — Step 8 VT-by-value param copy (reads primitive as mStack index).
  Fixed in Step 13b (2026-07-04) via the unified CLRMethod param layout +
  ReadNeoValueType. See §3 K2/K2-FAM.
- **D-LDELEMA** — `ldelema` opcode. Fixed in Step 17 (2026-07-04, IL VT array
  path; CLR primitive-array ldelema still NIE). See §3 D-LDELEMA.
- **Q-VT-NEWOBJ / [VT-THIS-ADDR]** — IL value-type `newobj` + `call VT ctor`
  via ldloca. Fixed in [VT-THIS-ADDR] (2026-07-05): inline-stfld owner-type
  clobber fix + D1 Newobj-dest typing + Newobj temp sizing + copy-back
  runtime branch. Full NeoStep smoke 99/99. See §3 Q-VT-NEWOBJ.
- **F-MAJ-1** — 2+ simultaneous CLR struct locals silent-wrong-result. Fixed
  in [OPT-HARDEN-2] (2026-07-05, dump-confirmed): the CLR-VT local declaration
  in `AllocateLocalStackSpaces` under-sized a flat-bytes local as a 4-byte
  boxed-ref; the D6 return-write's 12-byte flat write overflowed 8 bytes into
  the neighbour. Option B (declare flat-bytes), gated `#if ENABLE_NEO_MODE`,
  Legacy-neutral (stash-toggle). Full NeoStep smoke 100/100. See §3 F-MAJ-1.

