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
   - Stelem_I / generic-token Ldelem·Stelem / native Ldelem_I·U8 (Step 16) —
     when a test or feature needs them. (multi-dim RESOLVED via
     `neo-array-multidim`, 2026-07-06; removed from this list.)
   - cgt-un comment-nit (Step 15) — trivial.
   - catch-wrapper (Step 14) — not a bug (matches Legacy); revisit only if a
     real symptom appears.

---

## 2. Master table

| ID | Item | Surfaced by | Target | Unblocked by | Severity |
|----|------|-------------|--------|--------------|----------|
| D-LDELEMA | `ldelema` opcode | Step 16 | **RESOLVED (Step 17 + neo-step17-completion)** | Step 17 Ref-Slot/stind/ldind | fully resolved (IL VT array path Step 17; CLR primitive-array ldelema remainder in neo-step17-completion 2026-07-05) |
| D-CONSTRAINED | `constrained.`-on-VT specialization (Step 13 area 3) | Step 13 | **FULLY RESOLVED for (a)/(b)/(c)/(d)/(M2); `fixed` rerouted to a Conv_U step** | Step 17 byref/VT-this-address | {a,d,M2} RESOLVED 2026-07-05 (neo-step17-completion): full constrained.-on-VT dispatch + CLR primitive-array ldelema + F-5 boxed-source NIE-guard. **(b) Stobj/Ldobj ref-region copy + IL-VT-with-ref-fields constrained RESOLVED 2026-07-06 (neo-step17-stobj-refloop):** ref-region copy gated on `TotalReferenceCount > 0` (primitive-only VTs byte-identical); byref source ref-base recovered via runtime `localInfos` scan (R2, no JIT change); Constrained IL-VT-direct-call + inherited-CLRMethod box paths seed the callee slot-0 ref region via the VT-THIS-ADDR copy-back mechanism (new `ExecuteNeo` hook, seeded post-mStack-reservation). **(c) edges CLOSED 2026-07-06 (neo-step17-generic-byref-etc):** per-sub-item dump-gate found generic-byref (`ref T`/`out T`, T generic) and interface-on-VT-constrained beyond the common shape are NO-OPS (the byref model is type-agnostic; the {a,d,M2,b} cohorts already cover the box-and-interface-dispatch path) — 3 + 2 TEST-ONLY regression guards added; `fixed` unmanaged-pinning rerouted to a future pointer/Conv_U step (blocked by the unimplemented `Conv_U`/`Conv_I` opcodes, outside `neo-byref`; the array-element address works via `ref arr[i]`/ldelema). The F-10-R1 latent defect (the constrained-VT both-stamp shape) is CLOSED by the JIT-discriminator gate (see the F-10-R1 row). area4 M2 obligation CLOSED. |
| D-13B | Step 13 areas 4-5 (binding codegen + CLRMethod param layout) | Step 13 | **FULLY RESOLVED 2026-07-06 (neo-step13-area4-refandstind, 4c+4d)** | Area 5 core done; Area 4b/4a done in neo-step13-area4; Area 4c (CLR ref/out typed-ref bridge) + 4d (CLR stind/ldind/stobj/ldobj via field identity) done in neo-step13-area4-refandstind — all of Area 4 done | roadmap gap |
| K1 | FCP mis-propagates value-type Moves (copy-then-mutate silent) | Step 12b | **RESOLVED (OPT-HARDEN)** | — | fixed (ldloca-kill) |
| K2 | Step 8 VT-by-value param copy reads primitive value as mStack index | Step 12b | **RESOLVED (Step 13b)** | unified param layout | fixed |
| K2-FAM | Move-path scalar->boxed-ref CLR-VT-local (reads int as mStack idx) | Step 13 | **RESOLVED 2026-07-06 (neo-k2fam-bridge, TEST-ONLY — subsumed by opt-harden-2 + review-fix + step13b; 6 regression guards)** | flat-bytes path resolved; boxed-ref bridge subsumed (declare-side flat-bytes + Initobj/Box/Unbox_Any flat-bytes arms + by-value-param flat-bytes read) | pre-existing (closed by recent work; no new engine fix) |
| F-MAJ-1 | 2+ simultaneous CLR struct locals -> silent wrong result (representation mismatch, NOT slot-reuse) | Step 13b | **RESOLVED ([OPT-HARDEN-2])** | dump-confirmed: D6 return-write flat-bytes into a 4-byte boxed-ref local slot overflowed 8 bytes into the neighbour; fixed by declaring a CLR-VT local as flat-bytes (Option B, gated `#if ENABLE_NEO_MODE`) | pre-existing (13b made reachable); fixed 2026-07-05 |
| Q-NEWOBJ | Newobj dest/arg aliasing after a `newarr` | Step 16 | **RESOLVED (Step 18, non-reproducible)** | — | not reproducible on HEAD (JIT dump: distinct frame regions + ref slots per register); same outcome as Q-STRUCT/Q-LONG |
| Q-VT-NEWOBJ | IL value-type `newobj` (real, non-inlined) + `call VT ctor` via ldloca | Step 18 | **RESOLVED in [VT-THIS-ADDR]** | RESOLVED 2026-07-05: inline-stfld owner-type clobber fix + D1 Newobj-dest typing + Newobj temp sizing + copy-back runtime branch | fixed; full NeoStep smoke 99/99 |
| F-2 / INLINER-REFONLY-VT | ref-only VT (prim-size 0) local `new S(refArgs)` mis-compiles: inlined `stfld.ref.inline` writes don't survive to the following in-frame `ldfld.ref` read | neo-vt-this-addr re-review (F-1 probe) | **future** (fold into K2-FAM bridge or [OPT-HARDEN-3]) | JITCompiler inliner ref-fold over a 0-prim-size VT local | pre-existing (latent) |
| F-3 / NEO-BYREF-THIS | `new ClrStruct(args)` + `local.VTMethod()` (CLR/IL struct instance method via byref-`this`) hit a pre-existing reflection gap (`CLRMethod.Invoke` read the byref `this` as a 4-byte mStack index; the byref `this` is an 8-byte Ref Slot from `ldloca`) | neo-opt-harden-2 re-review | **RESOLVED for direct `call` (neo-step13-area4); `callvirt`/`constrained.callvirt` still Step 17 D-CONSTRAINED** | `CopyNeoCallArguments` derefs byref sources at the copy site; `this` slot holds flat bytes; `CopyNeoCallThisBack` propagates mutations | pre-existing; direct-`call` shape closed 2026-07-05 (6/9 probes FAIL-on-HEAD) |
| F-4 / NEO-IL-EX-FIELDACCESS | Reading IL-declared fields/methods off a CAUGHT IL exception via the adaptor bridge is broken on Neo (4 read paths: #1 bridge, #2 `e.GetType()`, #3 `appdomain.Invoke`, #4 `ILTypeInstance.this[index]`) | neo-il-exception-throw apply (OQ1/OQ2) | **PARTIALLY RESOLVED (neo-f4-reflection-on-neo 2026-07-08)**: #1+#2 NO-OP (already worked; doc STALE), #4 RESOLVED (Neo indexer get/set arms), #3 SEQUENCED (parametrized-Run follow-on child) | `ILTypeInstance` Neo indexer (path #4 only -- the real fix); #2/#3 untouched | pre-existing; NeoStep 221/0/0; 2 NEW gaps sequenced (op_Equality null-operand + newobj string-arg) |
| Q-STRUCT | struct-local + field-mutation + element-read temp-renumber | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect `Optimizer.BCP.cs:97-141` | pre-existing (unconfirmed) |
| F-5 / NEO-CALLARG-BOXED-SRC | boxed-source branch of `CopyNeoCallArguments` (`ILIntepreter.Neo.cs:295-300`) mis-copies a boxed `this` (would read an mStack field offset as a struct address); UNREACHABLE today (boxed `this` only via `constrained.callvirt` = Step 17 NIE); also `CopyNeoCallThisBack` comment claims ctor coverage but the newobj path does not invoke it | neo-step13-area4 review (Finding M2) | **RESOLVED (neo-step17-completion)** | the box-once BYPASSES CopyNeoCallArguments; the wrong defensive CopyBlock replaced with a tagged NIE-guard + CopyNeoCallThisBack comment tightened | latent dead-branch (closed 2026-07-05) |
| F-6 / NEO-VT-FLDADDR | `ldflda`-on-in-frame-VT mis-reads: the Ldflda arm reads the operand slot as an mStack objIdx; an in-frame VT slot holds flat bytes -> garbage. There is NO `Ldflda_Inline`. Any IL-struct method taking a field address (`field.ToString()`, `ref field`, `fixed`) is broken on Neo REGARDLESS of constrained | neo-step17-completion apply (the IL-struct ToString probe) | **RESOLVED 2026-07-06 (neo-vt-ldflda-inline)** | marker stamp (`Operand4` bit 0x1 in `TypeSpecializeNeoOpcodes case Ldflda:`) + 3-way runtime dispatch (marker + leading-int: `-1` -> frame-native; else marker -> flat-bytes shape 3; else -> heap/CLR). Neo-only, Legacy-neutral. NeoStep 154/154. CLR-object-field `ldflda` coverage gap deferred to `neo-step17-stobj-refloop` (F-R2) | pre-existing (NOT introduced; surfaced when the IL-struct ToString probe hit it) |
| Q-LONG | long default-zero compare (conv.i8) quirk | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect conv.i8 / branch type-spec | pre-existing (unconfirmed) |
| D-CHECKEX | `CheckExceptionType` NIE for non-CLRType catch types | Step 14 | **RESOLVED ([CATCH-COMPLETE] + neo-il-exception-throw)** | CheckExceptionType IL branch + Exception-adaptor + Throw-for-IL all landed | shared-engine gap (fully closed; IL branch now reachable end-to-end) |
| D-IL-EXCEPTION-THROW | End-to-end IL-exception catch (Exception-adaptor + Throw-for-IL) | Step 18/CATCH-COMPLETE | **RESOLVED (neo-il-exception-throw)** | System.Exception CrossBindingAdaptor (built-in) + Throw `as Exception` IL-instance unwrap on BOTH engines | shared-engine gap (closed 2026-07-05; F-4 / NEO-IL-EX-FIELDACCESS follow-up surfaced) |
| D-PEEP | `box T; isinst U` peephole + `PatchKind.IsinstResult` | Step 15 | **DEFERRED (scoped-deferral 2026-07-08)** | a peephole-pass framework + liveness -- PatchKind EXISTS post-Step-22 but is the wrong shape (generic-template T-identity value-substitution, not an opcode-stream rewrite); no fusion pass exists | optimization (non-functional) |
| D-ARR | Stelem_I / generic-token Ldelem·Stelem / native Ldelem_I·U8 / multi-dim | Step 16 | **RESOLVED 2026-07-06 (neo-array-completion rank-1 + neo-array-multidim rank-2+)** | rank-1 closed (neo-array-completion); multi-dim closed (neo-array-multidim -- primitive + ref element; Gap 1/2/3 reflection-fallback fixes) | Stelem_I / generic-token / UIntPtr[] / ref-array ldelema remain accepted-known edges |
| N-CGTUN | Cgt_Un divergence comment (src=sentinel case) | Step 15 | **RESOLVED 2026-07-06 (neo-opportunistic-cleanup)** | — | cosmetic nit (comment-only; runtime expression byte-identical) |
| N-CATCHWRAP | catch slot stores ILRuntimeException wrapper | Step 14 | **accept** (matches Legacy) | — | not-a-bug |
| N-TC2 | Step 14 TC2 asserts `e != null` | Step 14 | **RESOLVED 2026-07-06 (neo-opportunistic-cleanup)** (was resolved by Step 15; the test-tighten follow-up is now done) | — | cleanup |
| F-7 / NEO-DELEGATE-REFOUT | byref-aware arg marshaling in `DelegateAdapter.NeoInvokeSub` (delegate ref/out params) | Step 19 | **future** (route to a byref follow-up child — same family as D-13B area 4c / neo-step17-stobj-refloop) | byref-typed Ref Slot in the delegate Invoke param region | pre-existing (latent; the only reachable shape today is plain primitives via `WriteNeoCallSlot`) |
| F-8 / NEO-DOUBLE-COMBINE | 2+ `double` locals combined in one boolean expression silently misfire (F-MAJ-1 class; `double`-specific, not all 8-byte primitives) | neo-array-completion review (F-1) | **RESOLVED 2026-07-06 (neo-double-combine-quirk, D4)** | dead `Operand3 = RefOffset` write in `Optimizer.Neo.cs LowerNeoOffsets` immediate-branch case clobbered the high 4 bytes of `OperandDouble`/`OperandLong` (@12-19) via the `[StructLayout(Explicit)]` union; copy-prop folds `Ldc_R8` into `Bnei_Un_R8` (reachable) but keeps `Ldc_I8` register-register (unreachable) -> double-fails/long-works | pre-existing (NOT introduced by D-ARR; upstream of the array work; surfaced when the array probes needed combined `double` assertions) |
| F-9 / NEO-INLINED-RETURN-MOVE | an int returned from an inlined IL method moved as a reference -> `mStack[intValue]` OOB (return-value classification edge in the trivial inliner) | neo-step13-area4-refandstind review (4d.2 probe-avoidance) | **future** (route to an inliner/optimizer follow-up) | the trivial-inliner mis-classifies an inlined IL-method return value (moves an int as a reference) | pre-existing (latent; surfaced when the 4d.2 `LdindClrIntFieldPeek` probe needed to defeat it via `int v = slot; return v + 0;`) |
| F-10 / NEO-CLRSTRUCT-FIELD-OF-IL | **RESOLVED 2026-07-06 (neo-clrstruct-field-of-il)** a CLR-struct field of an IL instance (e.g. an async SM's `<>t__builder`/`<>u__1`) is laid out as a reference slot (no `primitiveOffset` advance, ILType.cs:2129-2157) but the JIT `ldflda` addressed it as a primitive offset -> byref carried one offset, unrecoverable to the field's ManagedObjects ref slot; `ldflda &SM.<>t__builder` read Primitives OOB on the non-generic-Task SM, happened to fit on the Task<int> SM | neo-step20-async sync slice (review-loop round 1) | **RESOLVED (neo-clrstruct-field-of-il)** | encoding-only fix (Option A, NO layout change, NO ILType.cs/Optimizer.Neo.cs edit): β offset-discriminator — `NeoLdfldaClrStructFieldMarker = 0x2` (Operand4 bit 0x2; `Stfld_Ref`/`Ldfld_Ref` discriminator `Operand4 != 0`, stamps `fieldType.GetHashCode()`) + runtime flag `NeoF10ByrefOffsetFlag = 0x40000000` (bit 30 of the offset half). All THREE arms (Stfld_Ref, Ldfld_Ref, Ldflda) made consistent — box/unbox/flatten the CLR struct at `ManagedObjects[ReferenceOffset]` via `ReadNeoValueType`/`WriteNeoValueType`. Blast radius CONFIRMED SAFE (5 field shapes; existing paths byte-identical when `Operand4 == 0`). Neo-only, Legacy-neutral | **was HIGH** (pre-existing; the load-bearing primitive for the rest of Step 20 sync + the suspend slice; same family as F-2 / NEO-BYREF-THIS; TC1/TC7 passed by a layout accident) — now resolved |
| F-10-R1 / NEO-CLRSTRUCT-FIELD-OF-IL-R1 | the F-6/F-10 markers are NOT mutually-exclusive at the JIT discriminator: an IL **value type** `struct V { TestVector3NoBinding f; }` taking `ref this.f` via `ldflda` inside a VT method gets BOTH stamped (`Operand4 = 0x3`); the runtime checks F-10 (`objIdx >= 0`) before F-6, predicting a mis-dispatch that reads flat bytes as an mStack index | neo-clrstruct-field-of-il review round 1 (Major-latent) | **RESOLVED 2026-07-06 (neo-step17-generic-byref-etc)** | the **JIT-discriminator gate** shipped (type-spec-pass form): `TypeSpecializeNeoOpcodes case Ldflda:` clears F-10 when F-6 stamps (`op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;`, JITCompiler.cs:913) — keys on the OPERAND's value-category (in-frame VT), NOT `!declaringType.IsValueType` (the latter would break the boxed-IL-VT-with-CLR-struct-field case). Runtime arm UNCHANGED (the reviewer's runtime F-6-before-F-10 reorder was DISPROVEN -- broke 6 F-6-only probes 190->184). Adversarial keeper `NeoStep17_F10R1_ConstrainedVtLdfldaClrField` (constrained-VT direct-call shape, forces `objIdx >= 0`) FAIL-on-HEAD (NRE) -> PASS-after; stash-toggle confirmed. Neo-only, Legacy-neutral | **resolved** (was latent Major; gated behind constrained-VT; now constructively unreachable) |
| STEP-20-PARTIAL | Step 20 async/await — sync Task<int> green; the rest deferred | neo-step20-async (Step 20) | **PARTIAL**: sync Task<int> (TC1) + nested (TC7) SHIPPED; the rest (non-generic Task, ValueTask, multi-await, exception, async void, incomplete-await NIE) deferred to F-10 + `neo-step20-async-suspend` | F-10 (CLR-struct-field-of-IL) for the remaining sync shapes; `neo-step20-async-suspend` for the truly-async suspend/resume | infrastructure shipped (builder redirects + awaiter/Task accessor overrides + Start->MoveNext fresh-interpreter routing + SmTaskMap stash + HoistNeoILValueToHeap + ILAsyncContext skeleton) |
| STEP-25-PARTIAL | Step 25 runtime `.neo` loader — S1 (non-generic load+execute) V2-PROVEN; S2 done; S3 partial (sub-surface 1 shipped) | neo-step25-runtime-loader (Step 25) | **PARTIAL**: S1 SHIPPED + V2-PROVEN (ILMethod AOT-init dual-path `isNeoAotBody` + `InitCodeBodyFromNeo` + BodyRegister short-circuit; `NeoAssemblyLoader.Attach` same-AppDomain non-generic bind; `NeoStep25LoadExecCheck` V2 capstone 8/8 -- deserialize + ExecuteNeo == JIT for the non-generic matrix: arithmetic / try-catch EH / locals+byref-call; NeoStep 208/208 + NeoStep22 55/55 + NeoStep23 15/15 + NeoStep24 5/5; Legacy-neutral). S2 SHIPPED 2026-07-07 (neo-step25-s2-generic-at-load: the no-T-identity-token generic slice); S3 PARTIAL 2026-07-08 (neo-step25-s3-full-decoupling: sub-surface 1 -- ILType layout + Neo VTable rebuild from `NeoTypeDefRecord`, structural-equivalence + 2 mutation cells proven -- SHIPPED; sub-surfaces 2/3/4/5 deferred) | S2 DONE: `NeoAssemblyLoader.Attach` consumes `model.Templates` + `GenericMethodTemplateOps.BuildFromNeoRecord` (re-resolves `VariableTypes` from `VariableTypeRefIdxs`; rejects T-identity-token/MethodToken patches w/ non-none CecilTokenKind) + `ILMethod.InitTemplateFromNeo` (OVERWRITES the JIT-captured template); the Step-22 hook (unchanged) routes generic instances through CloneAndPatch from the AOT template. V2 capstone 21/21 (4 functional T-kinds int/long/ref/struct + 4 structural-equiv incl. string-T + template body-mutation on a fresh ref-T instance + fresh-instance). NeoStep 215/215, NeoStep22/23/24 unchanged, Legacy-neutral. DEFERRED to S3: T-identity-token re-resolution (Box/Constrained T / IComparable<T>::CompareTo), generic methods WITH try/catch (Cecil Addr), cross-AppDomain (Cecil-free), full ILType decoupling, Approach-1 token-hash re-resolution, .cctor seeding, full CLR aqname indexing; S3 PARTIAL DONE (sub-surface 1): `ILType.RebuildFromNeoRecord` (reconstructs instance layout + Neo VTable + interface map from `NeoTypeDefRecord`; `naturalAlignment` re-derived from resolved field types) + `NeoAssemblyLoader.ResolveVTableFromRecord` + `ILType.NeoVTableSlotKeysForAOT` accessor + `NeoStep25LoadExecCheck` S3 cells (layout/VTable/interface structural-equiv + field-offset + VTable-slot-swap mutation); capstone 28/28 (21 S1/S2 + 7 S3), NeoStep 218/0/1, NeoStep22 55/55 + NeoStep23 15/15 + NeoStep24 5/5 unchanged, Legacy-neutral. STILL DEFERRED: sub-surface 2 Cecil-free AppDomain load (`LoadAssembly(Stream)` needs a Cecil module + a new ILType factory + injection into mapType/mapTypeToken), sub-surface 3 cross-AppDomain APPROACH-1 token-hash re-resolution (record compile-time `GetHashCode()` per ref entry under a `.neo` Version bump; Approaches 2/3 name-based-hash / body-rewrite REJECTED), sub-surface 4 static `.cctor` seeding (`.cctor` suppressed under Neo + per-static-field offsets NOT in the record), sub-surface 5 host-CLR-assembly registration RESOLVED 2026-07-08 (neo-step25-s3-clr-registration: the standalone `ilrt_neoc` `Assembly.LoadFrom`-es every host CLR ref so `GetType(string)`'s live `System.AppDomain.CurrentDomain.GetAssemblies()` CLR fallback resolves host CLR types as CLRType, NOT an ILType shadow -- mirrors the in-process model; the old `LoadAssembly(refStream)` shadowed the CLR type; TestCLREnum compile+load+invoke round-trip V2-proven via a dedicated `NeoClrProbe` probe; standalone exit-0 .neo + 7/7 self-check); full CLR aqname indexing + (NEW) standalone-CLI cross-binding-adaptor registration (the `TestClass2` fatal, see STEP-25-CLR-ADAPTOR) still deferred; Step 26 perf | S1 infrastructure shipped (the ILMethod dual-path + the loader + the V2 self-check). KEY S2/S3 prerequisite surfaced: the Neo host entry `ILIntepreter.Run` is a Step-6 PARAMETERLESS-ONLY shim (does not marshal `object[] p` into the frame, nor allocate the full frame ref region) -- a stronger V2 (parametrized + CLR-catch-shape coverage) needs either a fuller Run entry or an internal-call-driven invocation |
| STEP-25-CLR-ADAPTOR | standalone `ilrt_neoc` registers NO CLR cross-binding adaptors: an IL type INHERITING a host CLR class (e.g. TestCases types inheriting `ILRuntimeTest.TestFramework.TestClass2`) fatals the serializer (`TypeLoadException: Cannot find Adaptor for:...TestClass2`, exit 1) once the S3-5 fix gets the compile past TestCLREnum. Surfaced 2026-07-08 by neo-step25-s3-clr-registration (which closed the TestCLREnum CLR-type-RESOLUTION gap, revealing this ADAPTOR-registration gap downstream). | neo-step25-s3-clr-registration (S3-5) apply (the full-TestCases V1-B) | **RESOLVED 2026-07-08 (neo-step25-clr-adaptor; adaptor scope)** | the in-process test harness registers adaptors via `CLRBindings.Initialize(appdomain)` + `RegisterCrossBindingAdaptor`; the standalone CLI's fresh AppDomain does none of that. The focused `NeoClrProbe` probe (CLR-enum only, no CLR-class base) sidesteps it for the S3-5 gate. Fix (DECIDED by the dump-gate, NOT the S3-5 planner's "register adaptors" guess -- registering test-harness adaptors would couple a test-harness adaptor set into a GENERIC tool): a per-TYPE PRE-FILTER at the top of `NeoCompiler.CompileCore` eagerly triggers `FirstCLRBaseType`+`FirstCLRInterface` inside `try/catch(TypeLoadException)`; a harness-adaptor type is SKIPPED at the type level (`MakeTypeSkip` "(type) \<FullName\>" marker, reusing the existing `Skipped` report + the `IsComplete`->exit-2 path; `Program.cs` byte-identical). Built-in adaptors (`ExceptionAdaptor`/`AttributeAdapter`, AppDomain ctor) keep resolving -- NOT skipped. The init is memoized, so a survivor's later `BaseType` access in `NeoAssemblyWriter.BuildTypeDef` does NOT re-throw. NEO-AOT-ADAPTOR-SKIP. | RESOLVED (adaptor scope): the `Cannot find Adaptor` serializer fatal is GONE. `NeoStep25ClrAdaptor` self-check 7/7 (ExceptionProbe built-in-adaptor type EMITTED; AdaptorProbe harness-adaptor type SKIPPED + omitted from the .neo). NeoStep 219/0/0, NeoStep25S3ClrEnum 7/7 (no regression). DISTINCT from S3-5 (an enum is a primitive -- no adaptor; S3-5 is RESOLVED). NOTE: a DEEPER, DISTINCT gap NOW blocks exit-0/2 on full TestCases -- `NEO-AOT-FIELDINIT-NRE` (anonymous/open-generic-type field-init NRE); surfacing it is the expected next-step pattern (this change closed the adaptor gap just as S3-5 closed the TestCLREnum gap). The adaptor TRUE-COMPLETION (no `Cannot find Adaptor` fatal) IS met; exit-0/2 on the FULL TestCases is NOT (blocked by the field-init follow-on) -- see `NEO-AOT-FIELDINIT-NRE` row |
| NEO-AOT-FIELDINIT-NRE | standalone `ilrt_neoc` on full `TestCases.dll`: a compiler-generated ANONYMOUS type (`<>f__AnonymousType0\`2<j,k>`, an OPEN GENERIC type definition whose fields are generic-parameter-typed) reaches `NeoAssemblyWriter.BuildTypeDef` -> `type.TotalPrimitiveSize` -> `ILType.InitializeFields` (`ILType.cs:~2292`, `fieldType.IsPrimitive`) and NREs because `appdomain.GetType(field.FieldType)` returned null (`FindGenericArgument` on the OPEN definition yields null) -> `NeoCompilerFatal("serializer failure: Object reference not set...")`, exit 1, NO `.neo`. The STEP-25-CLR-ADAPTOR pre-filter does NOT trigger field init (only `FirstCLRBaseType`+`FirstCLRInterface`), so this survivor type passes the pre-filter and NREs during `Write`. | neo-step25-clr-adaptor apply (the full-TestCases V1-B smoke) | **RESOLVED 2026-07-08 (neo-step25-clr-adaptor A2 closure + A3 null-body extension)** | RESOLVED via the clean, spec-preserving fix (the implementer's recommended Option 2, design-D1-preserving): (1) `ILType.InitializeFields` now throws `TypeLoadException("Cannot resolve field type '...' for type '...'")` (and the static-field sibling) when `fieldType`/`staticFieldType` is null -- Neo-gated (`#if ENABLE_NEO_MODE`), mirroring the adaptor-lookup throw sites at `ILType.cs:1505/1568/1593`, so Legacy is byte-identical; (2) the `NeoCompiler.CompileCore` per-type pre-filter adds `_ = type.TotalPrimitiveSize;` (triggers `InitializeFields` inside the existing `TypeLoadException` catch) so the field-init TLE fires during the pre-filter, not later at `Write` -- the type enters `result.Skipped` (exit 2). A3 EXTENSION (surfaced during the A2 verify, same binding bar): a delegate `Invoke`/`BeginInvoke`/`EndInvoke` (or any abstract/extern/PInvoke method) has `MethodDefinition.HasBody == false`; `ILMethod.InitCodeBody` guards `if (def.HasBody)` so `BodyRegister` returns null WITHOUT throwing -> the per-method loop silently admitted it to `methods[]` and `CompileFresh`'s JIT NREd on the null body (`JITCompiler.Compile` :360). Fixed by a `!ilm.Definition.HasBody` silent-skip in the CompileCore per-method loop (mirrors the `IsGenericInstance` skip -- a pre-compile filter, NOT a broadened catch; a body-bearing method whose JIT NREs still throws in the force-compile try and is recorded as a skip). RESULT: `ilrt_neoc TestCases.dll out.neo ILRuntimeTestBase.dll` -> valid `.neo` (magic `0x494C524E`, 1MB), **exit 2**, **NO fatal** (neither `Cannot find Adaptor` NOR the field-init NRE NOR the null-body NRE), skip report = 69 type-skips + 181 method-skips, "compiled 1470 methods, 96 templates, 445 types". NeoStep25ClrAdaptor 7/7, NeoStep 219/0/0, NeoStep25S3ClrEnum 7/7, Legacy-neutral. | closed (the SOLE blocker after the adaptor gap; the binding exit-0/2-on-full-TestCases bar is now MET). The narrow-TLE-catch invariant (design D1) is preserved -- a genuine non-TLE init failure (a real NRE from a logic bug) still stays a loud fatal; only LOAD failures (adaptor-absence TLE + field-type-resolution TLE) and inherently-bodyless methods are skipped |
| F-11 / NEO-AOT-GENERIC-EAGER-COMPILE | the runtime `Call` resolves a generic callee via `AppDomain.GetMethod(token)`, which returns the instance registered when the callee was FIRST touched -- and the NeoCompiler compile step (force-compile of each non-generic method) eagerly touches a generic callee's `BodyRegister` (to build the caller's NeoCallParamMap), caching it with the template bound AT COMPILE TIME. So if a generic instance is compiled BEFORE the `.neo` loader binds the AOT template, that instance's bodyRegister is STALE (JIT/unmutated) and the loader's template bind never reaches it. Non-generic S1 hides this (`InitCodeBodyFromNeo` OVERWRITES the callee's bodyRegister + isNeoAotBody); generic S2 cannot (the loader binds the def cache, NOT the instance). | neo-step25-s2-generic-at-load (capstone dump-gate) | **accepted-known (S2)** + **S3 action item** | S2 capstone workaround: the mutation cell drives a FRESH instance via `MakeGenericMethod` + direct `appdomain.Invoke` (MakeGenericMethod returns a NEW ILMethod, bodyRegister null -> InitCodeBody -> CloneAndPatch against the freshly-bound AOT template). S3 must address the production case: load order (bind templates BEFORE any generic call compiles) OR invalidate stale instance bodyRegisters on bind OR a Cecil-free per-instance refresh | NOT an S2 bug (the S2 slice proves the AOT template drives CloneAndPatch for a fresh instance); a real S3 production-AOT concern (a generic instance compiled before load keeps JIT) |
| F-12 / NEO-RUN-REF-RETURN | `ILIntepreter.Run` (the Step-6 parameterless entry shim) returns via `NeoBoxReturnValue(method.ReturnType, retDst, retSize)`, which handles PRIMITIVE returns only (int/long/etc.); a REFERENCE-type return (string/object/ILType) falls through to the raw-`*(int*)retDst` fallback (reads the low 4 bytes of the ref slot). So invoking a parameterless method that returns a reference type via `appdomain.Invoke` returns garbage (the S2 `WrapEchoRef` that returned `Echo<string>` -> "hi" got 0). | neo-step25-s2-generic-at-load (capstone dump-gate) | **accepted-known (pre-existing)** | S2 capstone workaround: ref-T functional cell returns int (`ConstGeneric<string>()` -> 1234567), not string. A real fix belongs with a fuller Run entry (read the ref-region slot at `retRefBase` for a reference return) -- folded into the S3 parametrized-Run prerequisite already recorded on STEP-25-PARTIAL | pre-existing (NOT introduced by S2; S1 only tested int returns; surfaced when the S2 ref-T functional cell used a string return) |
| neo-debugger-neo-frame | Neo debugger variable inspection under ENABLE_NEO_MODE (reading frame vars off the Neo `byte* frameBase` + `AutoList mStack` + `CompiledFrame.LocalInfos` layout, NOT the Legacy `StackObject*` + `BasePointer` frame the current variable readers use) | neo-step26-perf-validation (sub-surface D dump-gate, HEAD f3be2788) | **future** (dedicated `neo-debugger-neo-frame` follow-up; NOT Step 26 scope) | the graceful degradation ALREADY ships: GetThisInfo returns "Neo this inspection is not supported yet." (`DebugService.cs:207-209`) + GetLocalVariableInfo returns "Neo local variable inspection is not supported yet." (`DebugService.cs:268-271`); the stacktrace instruction dump IS already Neo-adapted (reads `CompiledFrame.NeoExecuteBody`, `DebugService.cs:143-148`) and is NOT deferred | LARGE (~8 methods to adapt: `DebugService.cs:203-261, 263-300, 442-731, 678-740` -- AddStackFrameInfoVariables / ResolveCurrentFrameBasePointer / DumpStack / GetValueExpandable / VisitValueTypeReference / GetStackObjectText); pre-existing (NOT introduced by Step 26 -- Step 26 is perf-validation only) |


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

**(c) edges RESOLVED 2026-07-06 (neo-step17-generic-byref-etc).** The per-sub-item
dump-gate (HEAD `0aafdb34`, NeoStep 198/198) DISPROVED the LEAD orientation
hypothesis for 3 of 4 sub-items and found exactly ONE real engine gap:
- **generic-byref (`ref T`/`out T`, T generic) -- NO-OP.** 3/3 probes PASS on
  HEAD (`Swap<int>`, `Swap<IL-ref-class>`, `Swap<IL-VT>`). The byref model is
  type-agnostic (an 8-byte Ref Slot copied regardless of element type; the
  generic-param token is resolved at the call site via JIT generic
  substitution, NOT at the byref-marshal level). -> 3 TEST-ONLY guards.
- **interface-on-VT-constrained beyond the common shape -- NO-OP.** 2/2 probes
  PASS on HEAD (IL-VT via generic constrained caller -> direct-call; CLR-VT via
  generic constrained caller -> box-once). The {a,d,M2,b} cohorts already cover
  it. -> 2 TEST-ONLY guards.
- **`fixed` unmanaged-pinning -- REROUTED.** `fixed (int* p = arr)` FAILS on
  HEAD with `Conv_U not yet implemented (Step 6)` (and `Ldtoken` if an array
  initializer is used). The array-element address works via `ref arr[i]`
  (ldelema + stind/ldind, TC14 green); the `fixed` statement needs the
  unimplemented `Conv_U`/`Conv_I` pointer-conversion opcodes. Rerouted from
  "accept-known for `fixed`" to a future pointer/`Conv_U` step (outside
  `neo-byref`).
- **F-10-R1 -- REAL GAP (the only engine change).** The latent F-6/F-10 both-
  stamp shape IS reachable via the constrained-VT direct-call. The JIT-
  discriminator gate shipped (see the F-10-R1 §3 entry below). The adversarial
  keeper `NeoStep17_F10R1_ConstrainedVtLdfldaClrField` FAIL-on-HEAD -> PASS-after.


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

**(c) edges RESOLVED 2026-07-06 (neo-step17-generic-byref-etc).** [(b) Stobj/
Ldobj ref-region copy + IL-VT-with-ref-fields constrained were RESOLVED
2026-07-06 by `neo-step17-stobj-refloop` — see the RESOLVED-(b) prepend at the
top of this §3 entry.] The (c) sub-items closed by `neo-step17-generic-byref-etc`
(dump-gate): generic-byref and interface-on-VT-constrained were NO-OPS (3 + 2
TEST-ONLY guards); `fixed` rerouted to a future pointer/`Conv_U` step (blocked
by unimplemented `Conv_U`/`Conv_I`); the F-10-R1 latent both-stamp defect was
CLOSED by the JIT-discriminator gate (see the F-10-R1 §3 entry below). See
`openspec/changes/archive/2026-07-05-neo-step17-completion/ship-log.md` for the
{a,d,M2} cohort,
`openspec/changes/archive/2026-07-06-neo-step17-stobj-refloop/ship-log.md` for
the (b) closure, and
`openspec/changes/neo-step17-generic-byref-etc/ship-log.md` for the (c) closure.

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

**RESOLVED 2026-07-08 (neo-f4-reflection-on-neo, scope-aware dump-gate).** Each
of the 4 read paths was re-probed on HEAD with the probe confounds removed; the
dump decided SHIP vs NO-OP vs SEQUENCE per path (design.md section 0/7):
1. **Bridge** -- WORKS on HEAD (no-op; the doc `InvalidCastException`
   characterisation was STALE -- an intervening callvirt-CLR-dispatch change
   closed it). No fix.
2. **`e.GetType()`** -- WORKS on HEAD (no-op; STALE). The original `-96` was NOT
   GetType -- it was `Type.op_Equality` (`t != null`) hitting a SEPARATE
   `ReadNeoReference` null-operand gap (the autogen `op_Equality_1_Neo` indexes
   `mStack[-1]` for a null operand). With the confound removed, GetType returns
   the caught Adapter's CLR type via the reflection fallback. No fix. NOTE: a
   Neo `Object.GetType` redirect was prototyped then REMOVED -- it does NOT fire
   (the redirect map is keyed by the `Object`-declared MethodInfo, but the JIT
   resolves `Object.GetType` on an IL exception to the `System.Exception`-
   declared MethodInfo, a distinct object/MethodHandle per .NET reflection, so
   `TryGetRedirection` misses). Recorded for a future worker wanting the IL
   projection from GetType (would need a normalized map key).
3. **`appdomain.Invoke(instanceMethod, e)`** -- SEQUENCED to follow-on child
   `neo-f4-parametrized-run-entry` (parametrized-Run ABI extension; the public
   `Run`/`Invoke` re-entry never marshals `instance`/`p` under
   `ENABLE_NEO_MODE`). `neo-async-movenext-fix` did NOT unblock it.
4. **`ILTypeInstance.this[index]` indexer** -- SHIP: the ONLY real fix landed.
   Replaced the Neo `get` `return null` with a Neo `get` arm gated on
   `index < type.TotalFieldCount`, dispatching on the field IType (primitive ->
   `Primitives[PrimitiveOffset]`; reference/enum/CLR-struct ->
   `ManagedObjects[ReferenceOffset]`; IL-value-type -> tagged NIE), mirrored in
   `set`. Added `ReadNeoPrimitive`/`WriteNeoPrimitive`/`WriteNeoPrimitiveDefault`
   helpers (keyed on the AppDomain primitive singletons -- the SAME identity the
   storage allocator uses). Probe `NeoStep14_ILEx_IndexerFieldRead` FAIL-on-HEAD
   (`-9`, null) -> PASS-after (`9`); stash-toggle confirmed load-bearing.

NeoStep smoke 221/0/0 (was 219; +2 probes); Legacy-neutral (plain Debug = 0
errors). Two NEW pre-existing gaps discovered during apply (design.md section 8),
both SEQUENCED: (a) `op_Equality` null-operand gap (general Neo -- affects any
`t == null` on Type/String); (b) `new MyEx(string)` ctor stores `this` into the
string field instead of the arg (newobj-arg passing for CLR-adaptor-base IL
types) -- the indexer probe works around it via a direct field assignment.


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

### F-10 / NEO-CLRSTRUCT-FIELD-OF-IL — CLR-struct field of an IL instance (ldflda offset defect; -> future `neo-clrstruct-field-of-il`)

**RESOLVED 2026-07-06 (neo-clrstruct-field-of-il).** Fixed via an encoding-only
fix (Option A; NO layout change; NO `ILType.cs`/`Optimizer.Neo.cs` edit). The
β offset-discriminator: F-10 marker `NeoLdfldaClrStructFieldMarker = 0x2`
(standalone `Operand4` bit `0x2`; OR-stamped alongside F-6's bit `0x1`) + the
runtime flag `NeoF10ByrefOffsetFlag = 0x40000000` (bit 30 of the offset half;
real `ReferenceOffset` values are tiny ref-slot indices so bit 30 is unreachable;
avoids the sign bit). The `Stfld_Ref`/`Ldfld_Ref` discriminator is
`ip->Operand4 != 0` (F-10 stamps `Operand4 = fieldType.GetHashCode()`); at HEAD
these arms never read `Operand4`, so the change is additive and byte-identical
when `Operand4 == 0`.

**The design premise was DISPROVEN by the Block-0 dump** ("only `ldflda`
broken; `Stfld_Ref`/`Ldfld_Ref` already correct"): `Stfld_Ref` of a CLR-struct
field from a flat-bytes source read the source's first 4 bytes as a ref-slot
mStack index (dump: `srcIdx=1092616192` = `10.0f` reinterpreted) ->
`mStack[garbage]` OOR; `Ldfld_Ref` wrote an mStack index into a flat-bytes dest.
**All THREE heap field-access arms were broken, not just `ldflda`.** The fix
makes all three consistent: the F-10 shape boxes/unboxes/flattens the CLR struct
at `ManagedObjects[ReferenceOffset]` via `ReadNeoValueType`/`WriteNeoValueType`
(the Step-13b/area4 machinery). Consumers shipped: `NeoMarshalByrefFieldToSlot`
(the Step-20 builder-byref hot path + an elemType-recovery fallback that FAILS
LOUD with a tagged NIE on null/uninit, never silent corruption) +
`Ldobj`/`Stobj` ILTypeInstance branches. Fixed-width `stind_*`/`ldind_*`
through an F-10 byref is unreached in smoke/probes -> accepted-known (loud OOR).

**Blast radius CONFIRMED SAFE** (review probe 1, load-bearing): 5 field shapes
swept — IL-instance ref-type, CLR-ref, CLR-object (all `Stfld_Ref`/`Ldfld_Ref`
with `Operand4 == 0` -> existing path byte-identical); IL-VT field (selects
`Stfld_Value`/`Ldfld_Value`, not Ref -> not reached); IL-primitive field
(`Stfld_I4`/etc, not Ref -> not reached). The F-10 discriminator fires ONLY for
the F-10 CLR-struct-field-of-IL-instance shape. The hash-zero edge case is
astronomically rare and falls back to the existing path — accepted-known
(design.md OQ2).

**Verification:** 7 F-10 probes FAIL-on-HEAD -> PASS (stash-toggle
`IsClrStructFieldOfIL -> false`: `Ran 7, 6 failed`). **Step 20 sync 2 -> 4
green** (TC4 `AsyncVoidSync` + TC6 `AsyncExceptionFaultsTask` newly unblocked —
both FAIL-on-HEAD with `IndexOutOfRangeException` at
`ILIntepreter.Neo.cs:2119` = the F-10 OOB; TC1/TC7 stay green as the layout-
accident guards). NeoStep 190/190, NeoStep20 9/9, ClrStructField 8/8. Legacy
`Debug` 0 errors. Neo-only, Legacy-neutral by construction.

**F-10-R1 (Major, latent) reclassified accepted-known-deferred** — see its own
§3 entry below. The reviewer's recommended runtime F-6-before-F-10 reorder was
DISPROVEN (broke 6 NeoStep17 F-6-only probes, 190→184); the correct future fix
is the JIT-discriminator gate, deferred to the constrained-VT follow-up.

**Step 20 redirect-coverage follow-ups (TC2/TC3/TC5):** these pass F-10 but hit
DISTINCT Step-20 redirect edges (non-generic Task `Start` redirect null-SM;
ValueTask builder NRE; multi-await `Task<int>.get_Result` redirect) — NOT F-10;
re-trimmed green, documented as Step-20 follow-ups in
`TestCases/NeoStep20Test.cs` and the STEP-20-PARTIAL entry. See
`openspec/changes/archive/2026-07-06-neo-clrstruct-field-of-il/ship-log.md`.

---

**HIGH severity — the load-bearing primitive for the rest of Step 20 sync + the
suspend slice.** Surfaced by neo-step20-async sync slice review-loop round 1
(the dump-gated STOP). The C# async state machine `<Method>d__N` is loaded as a
HEAP ILTypeInstance (Step 20 OQ2 confirmed; D2's in-frame-VT premise did not
apply). Its fields include IL-primitive fields (`<>1__state` int) and **CLR-
struct fields** (`<>t__builder` = `AsyncTaskMethodBuilder`, `<>u__1` =
`TaskAwaiter` — both CLR structs).

The ILType field-layout pass (`ILType.cs:2129-2157`, the `else` branch at line
2146) lays out a CLR-struct field by recording its `PrimitiveOffset` (the
running `primitiveOffset` cursor) AND `ReferenceOffset`, then does
`referenceOffset++` -- it treats the CLR struct as a REFERENCE slot and does NOT
advance `primitiveOffset` by the struct's size. So the CLR-struct field's flat
bytes do NOT live in the ILTypeInstance's `Primitives` array (only IL-primitive
fields do). But the JIT's `ldflda` of that CLR-struct field emits a byref
`(smMStackIdx, field.PrimitiveOffset)` (e.g. `(2, 4)` for the builder after the
4-byte state). At runtime, `CopyNeoCallArguments` -> `NeoMarshalByrefFieldToSlot`
sees `target is ILTypeInstance` and reads `ili.Primitives[off]` for `sz` bytes
-- but `Primitives.Length` is only the IL-primitive total.

**Dump proof of the layout accident:**
- TC1 `<NeoStep20_SyncTaskOfT>d__1`: `smPrimSize=12, smPrimLen=12` -- the
  builder-byref `(2, 4, sz=8)` reads Primitives[4..12], IN range (the 8 extra
  bytes happen to be present because the Task<int> SM has more IL-primitive
  field contribution). **PASSES by luck of layout.**
- TC2 `<NeoStep20_SyncTask>d__2`: `primLen=4` -- the builder-byref `(2, 4,
  sz=8)` reads Primitives[4..12], **OOB** -> IndexOutOfRange.

So the SAME `ldflda &SM.<>t__builder` shape OOBs on the non-generic-Task SM and
happens to fit on the Task<int> SM -- a layout accident, not a designed
contract. The byref encoding `(objIdx, PrimitiveOffset)` is unrecoverable to
the field's actual storage (the ManagedObjects ref slot at `ReferenceOffset`)
because the byref carries only ONE offset.

**Pre-existing -- NOT introduced by neo-step20-async.** Same family as F-2 /
NEO-BYREF-THIS (the CLRMethod.Invoke reflection-fallback byref-`this` shape) and
F-3 / NEO-BYREF-THIS. A narrow fix does NOT exist: the byref encoding is
ambiguous (one offset, two possible storage regions). Forcing a narrow fix
(zeroing the OOB dest) yields silent wrong results (default builder -> the
SM-keyed SmTaskMap never gets a real Task) -- the silent-corruption class the
OPT-HARDEN review-fix M1 lesson forbids. This is the stacked-pre-existing-edges
STOP case.

**Resolution:** future -- dedicated child `neo-clrstruct-field-of-il`. A real
fix is broad (touches the field-layout pass + every struct-field consumer):
- (a) a JIT change so `ldflda` of a CLR-struct-field-of-IL-instance produces a
  recoverable encoding (e.g. a sentinel objIdx + the field's ReferenceOffset,
  with a runtime branch in NeoMarshalByrefFieldToSlot that reads the boxed
  struct from ManagedObjects[ReferenceOffset]); OR
- (b) a layout change so a CLR-struct field's flat bytes ARE stored in
  Primitives (advance primitiveOffset by the struct's managed size, mirror in
  AllocateNeoCallParamSlot + every stfld/ldfld/by-value-param consumer).

The fix site: the field-layout pass (`ILType.cs:2129-2157`) + the `ldflda` JIT
lowering + the NeoMarshalByrefFieldToSlot ILTypeInstance branch + the
stfld/ldfld consumers of CLR-struct fields on IL instances. Unblocks the rest
of Step 20 sync (non-generic Task, ValueTask, multi-await, exception, async
void) AND the suspend slice (the awaiter field `<>u__1` is the same shape).
Recorded so the `neo-clrstruct-field-of-il` planner finds it. See
`openspec/changes/archive/2026-07-06-neo-step20-async/ship-log.md`.

### F-10-R1 / NEO-CLRSTRUCT-FIELD-OF-IL-R1 — F-6/F-10 marker not mutually-exclusive at the JIT discriminator (RESOLVED 2026-07-06, neo-step17-generic-byref-etc)

**RESOLVED 2026-07-06 (neo-step17-generic-byref-etc).** The JIT-discriminator
gate shipped. In `TypeSpecializeNeoOpcodes case OpCodeREnum.Ldflda:` (the F-6
stamping site, JITCompiler.cs:862-914), when F-6 stamps (`srcType is ILType &&
IsValueType && !IsEnum`), the gate CLEARS any F-10 the main-JIT body emission
set: `op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;` (JITCompiler.cs:913, the
single engine line). The type-spec pass runs AFTER body emission, so the body's
F-10 stamp is already on Operand4 at the clear. The both-stamp shape
(`Operand4 = 0x3`) is now impossible at the producer; the runtime Ldflda arm's
F-10-first check can no longer mis-fire on an in-frame-VT operand.

The gate keys on the OPERAND's value-category (in-frame VT vs heap/boxed) —
exactly the F-6 condition — so it is correct for ALL three operand shapes:
in-frame VT (F-10 cleared -> F-6 shape 1/2/3), heap IL class (F-6 not stamped ->
F-10 stays), boxed IL VT (operand is a heap mStack object -> F-6 not stamped ->
F-10 stays). A naive `!declaringType.IsValueType` gate was REJECTED at propose:
it would suppress F-10 for the boxed-IL-VT-with-CLR-struct-field case.

The runtime arm is UNCHANGED (the reviewer's recommended runtime F-6-before-F-10
reorder was DISPROVEN -- broke 6 NeoStep17 F-6-only probes, 190->184). The fix
is JIT-producer-side only. The latent defect (gated behind the constrained-VT
direct-call shape) is closed: the new adversarial keeper
`NeoStep17_F10R1_ConstrainedVtLdfldaClrField` (an IL VT `struct V { int prefix;
TestVector3NoBinding field; }` implementing an interface, invoked via a generic
constrained caller `T v where T:struct,IFace`, body does `ldflda this.field`)
FAILS on HEAD (NRE at `NeoMarshalByrefFieldToSlot`, reading `prefix` value 7 as
objectIndex) -> PASSES after the gate. Stash-toggle confirmed (gate OFF -> NRE
returns; F-6-only + heap-IL F-10 probes unaffected). Neo-only (the type-spec
pass is `#if ENABLE_NEO_MODE`); Legacy-neutral by construction (Step 17 Legacy
filter 47/47). Verification: full NeoStep smoke 204/204 (198 baseline + 6 new
keepers: 3 generic-byref + 2 interface-on-VT TEST-ONLY guards + 1 F-10-R1
FAIL->PASS); NeoClrStructField 8/8; NeoStep20 9/9; NeoOptHard 24/24. See
`openspec/changes/neo-step17-generic-byref-etc/ship-log.md`.

**Forward-looking audit note (review Minor F-1):** the boxed-IL-VT-with-CLR-
struct-field operand shape (which the gate intentionally lets KEEP F-10) could
NOT be positively verified end-to-end -- boxed-IL-VT interface-`callvirt` is a
separate pre-existing Neo gap (`MissingMethodException: Neo Callvirt_Interface`,
identical on HEAD and gate-applied, so the gate introduces NO regression). The
gate's correctness for the boxed-VT-keeps-F-10 case depends on the current fact
that boxed-IL-VT dispatch is unimplemented (`BuildInitialRegisterTypes` types
`this` as the declaringType for all VT method bodies, so a boxed-VT method body's
`ldflda this.clrField` would clear F-10 either way today). **RE-AUDIT this gate
when boxed-IL-VT interface-`callvirt` / `Callvirt_Interface` is implemented** --
at that point construct a positive boxed-VT-with-CLR-struct-field `ldflda` probe
and confirm it keeps F-10 (heap-field-offset path). See
`openspec/changes/neo-step17-generic-byref-etc/review-report.md` (finding F-1).

**Reclassified accepted-known-deferred 2026-07-06 (neo-clrstruct-field-of-il
review round 1).** The reviewer flagged that the F-6 marker (`Operand4` bit
`0x1`, stamped when the source is an in-frame IL VT) and the F-10 marker
(`Operand4` bit `0x2`, stamped when the field is a CLR-struct field of an IL
instance) are NOT mutually-exclusive at the JIT discriminator: an IL **value
type** `struct V { TestVector3NoBinding f; }` taking `ref this.f` via `ldflda`
inside a VT method gets BOTH stamped (`Operand4 = 0x3`). The runtime `Ldflda`
arm checks F-10 (`clrStructFieldMarker && objIdx >= 0`) BEFORE F-6
(`inlineMarker`), predicting a mis-dispatch that reads the in-frame VT's flat
bytes as an mStack index.

**The reviewer's recommended fix (1-line runtime F-6-before-F-10 reorder) was
DISPROVEN by the fixer.** The reorder broke **6 NeoStep17 F-6-only probes
(190/190 -> 184/190)**. Root cause: F-6 shape 3 (`operandSlotOff +
fieldPrimOff`) and shape 1/2 frame-native (`vtBase + fieldPrimOff`) produce
DIFFERENT byrefs; every reachable VT `this`/arg today arrives as a managed
pointer (`objIdx == -1`), so HEAD order routes them to shape 1/2 (correct),
while the reorder routes them to F-6 shape 3 (wrong).

**The defect is fully latent.** The feared F-10-first mis-dispatch requires
`objIdx >= 0` with flat bytes (the constrained-boxed-VT sub-case), which is
gated behind the DEFERRED `constrained.callvirt`-on-VT (Step 13 Area 3 / Step
17 follow-up). For all reachable VT source shapes, `objIdx == -1` -> HEAD order
routes correctly to F-6 shape 1/2. The shipped F-10 case (heap IL ref source,
F-10-only) is unaffected.

**No runtime change applied** (the recommended fix regresses 6 tests; the
shipped F-10-first order is correct for every reachable shape). New latent-shape
probe `NeoClrStructField_IlVtMethodLdfldaThisClrStructField` (+ host helper
`SetTestVector3NoBindingByRef`) added as a regression guard for the both-stamp
shape's `objIdx == -1` -> shape 1/2 routing. The probe does NOT FAIL-on-HEAD
(cannot, given current Neo); it is a shape guard for when constrained-VT lands.

**Resolution:** route to the constrained-VT follow-up (`neo-step17-generic-byref-etc`
or a Step 13 Area 3 follow-up). **The correct future fix is the JIT-discriminator
gate** (only stamp F-10 when the source is NOT an in-frame VT — mirror the F-6
source check in the type-spec pass, so the two markers are genuinely mutually-
exclusive at the producer), NOT a runtime reorder. Recorded so the constrained-
VT planner finds it. See
`openspec/changes/archive/2026-07-06-neo-clrstruct-field-of-il/ship-log.md`.

### STEP-20-PARTIAL — Step 20 async/await (sync Task<int> green; the rest deferred)

Step 20 is the largest runtime step in the Neo roadmap (async/await).
neo-step20-async delivered the SYNC-completing slice proven end-to-end:
- **SHIPPED (green):** TC1 (sync `Task<int>`) + TC7 (nested sync `Task<int>`)
  -- the full sync path (Start -> DriveMoveNext via a fresh pooled interpreter
  -> GetAwaiter redirect -> get_IsCompleted redirect -> GetResult -> SetResult
  -> get_Task) proves the builder-redirect surface, the Start->MoveNext
  fresh-interpreter routing, the awaiter/Task accessor overrides, and the
  SmTaskMap stash all work end-to-end for the generic Task<int> single-await +
  nested shapes.
- **Infrastructure shipped (foundation, NOT exercised by a green test):**
  builder redirects (Create/Start/SetResult/SetException/get_Task/
  SetStateMachine) registered in the AppDomain ctor (FIRST-registered-wins over
  the autogen non-functional stubs); awaiter/Task accessor overrides;
  `HoistNeoILValueToHeap` (the D5 frame-to-heap hoist helper, standalone, not
  wired); `ILAsyncContext<T>` skeleton (IValueTaskSource<T> surface live;
  MoveNext throws the tagged NIE).
- **DEFERRED:** the remaining sync shapes (non-generic Task, ValueTask,
  multi-await, exception, async void) -- blocked by F-10
  (NEO-CLRSTRUCT-FIELD-OF-IL). The truly-async suspend/resume path -- deferred
  to `neo-step20-async-suspend` (AwaitUnsafeOnRegistered NIE + frame-to-heap +
  ILAsyncContext resumption). The 11 trimmed probes (TC2-TC6, TC8) -- re-add
  when F-10 lands. The `Callvirt_CLR` generic-type-instance bug -- noted, does
  not block any green-target probe.

**Resolution:** F-10 (the load-bearing primitive) unblocks the rest of sync;
`neo-step20-async-suspend` owns the suspend/resume. See
`openspec/changes/archive/2026-07-06-neo-step20-async/ship-log.md`.

**UPDATE 2026-07-06 (neo-clrstruct-field-of-il landed):** F-10 is RESOLVED. The
sync slice is now **2 -> 4 green** (TC4 `AsyncVoidSync` + TC6
`AsyncExceptionFaultsTask` newly unblocked; TC1 + TC7 stay green). The remaining
sync shapes (TC2 SyncTask non-generic, TC3 SyncValueTaskOfT, TC5 MultipleAwaits)
pass F-10 but hit DISTINCT Step-20 **redirect-coverage** edges (non-generic
`Task`'s `Start` redirect null-`stateMachine`; ValueTask builder NRE; multi-await
`Task<int>.get_Result` redirect "Method 'Task.Result' not found") — these are
Step-20 follow-ups, NOT F-10; re-trimmed green, documented in
`TestCases/NeoStep20Test.cs`. `neo-step20-async` resume (or
`neo-step20-async-suspend`) owns these redirect edges. The truly-async
suspend/resume path is still `neo-step20-async-suspend`.

**UPDATE 2026-07-06 (neo-step20-async-suspend landed — PARTIAL: Phase 1 only):**
the suspend slice dump-gated HEAD and found `AwaitUnsafeOnCompleted_Neo` is
UNREACHABLE end-to-end (3 stacked pre-existing blockers). The implementer
correctly STOPPED at Phase 1 (F-10/K1 discipline; a false-positive probe green
via blocking `GetResult` on an incomplete `Task.Delay` was caught + removed).
**Phase 1 SHIPPED (3 reachability unblockers, Neo-only, Legacy-neutral):** B3
`case Nop: ip++; continue;` (the catch-all NIE threw on Nop); B2 `IsGenericType`
guard in `TaskAwaiter_T_GetResult_Neo` (the non-generic `TaskAwaiter` has void
GetResult / no `.Result` -> the unconditional `InvokeMember("Result")` threw
`MissingMethodException`; reviewer proved load-bearing); `Task.Delay(int)`
redirect (a real threadpool-completing suspend source). Neo 204/204, NeoStep20
sync 9/9, NeoOptHard 24/24. **Phase 2 (suspend machinery) DEFERRED to 2 split
children:** (1) `neo-async-controlflow-iscompleted` — the `brtrue`-after-
`get_IsCompleted` register mismatch (`get_IsCompleted` writes `DstOffset` but
`brtrue.s` reads `SrcOffset` -> always takes the completion path ->
`AwaitUnsafeOnCompleted` never called; MASKS B1; highest value); (2)
`neo-generic-redirect-resolution` — B1: the 2-generic-arg
`AwaitUnsafeOnCompleted<TA,TSM>` resolves only on the Legacy `RedirectMap`, not
`RedirectMapNeo` (broad/entangled shared dispatch; re-dump-gate AFTER #1 lands).
`AwaitUnsafeOnCompleted_Neo`/`AwaitOnCompleted_Neo` + `ILAsyncContext<T>.MoveNext`
remain tagged NIEs; the foundation is unchanged + proven. See
`openspec/changes/archive/2026-07-06-neo-step20-async-suspend/ship-log.md`.

**UPDATE 2026-07-07 (neo-async-controlflow-iscompleted — DISPROVEN, closed as
no-op):** the "control-flow blocker #1" reported by the suspend slice
(`brtrue`-after-`get_IsCompleted` always takes the completion path) is NOT a bug
on HEAD. A dump-gate of a `Task.Delay` await proved: when the awaited task is
genuinely incomplete at first poll, `get_IsCompleted` returns `False`, `Brtrue`
reads the SAME frame offset the redirect wrote (`readAddr == retDst`), reads
`int=0`, and CORRECTLY does NOT branch -> falls through to `AwaitUnsafeOnCompleted`
(the call IS reached, `hasRedirect=True`). The suspend-slice report was an
artifact of the PRE-Phase-1 state: before the `Task.Delay` redirect shipped, the
probe's task was sync-completing -> `isCompleted=True` -> `brtrue` CORRECTLY took
the completion path (misread as a control-flow bug). **CAVEAT (probe-design
lesson):** `Task.Delay(N)` is RACY with the JIT/setup overhead -- sometimes
complete at poll (sync path, probe passes), sometimes not (suspend path reached,
probe fails in the `AwaitUnsafeOnCompleted_Neo` stub NIE). A deterministic
suspend probe needs a `TaskCompletionSource`-style awaitable that is incomplete
at first poll regardless of timing. The actual remaining async blocker is NOT
control-flow but: (a) confirming whether the `AwaitUnsafeOnCompleted[TaskAwaiter,
IAsyncStateMachineAdaptor]` redirect DISPATCHES to `AwaitUnsafeOnCompleted_Neo`
(the `hasRedirect=True` contradicts the B1 finding; needs a deterministic-probe
dump), and (b) B1 (the 2-generic-arg redirect resolution). Both fold into
`neo-generic-redirect-resolution` (B1). No code/test shipped for the control-flow
child (no fix exists; the racy probe can't be a regression guard).
`neo-async-controlflow-iscompleted` is REMOVED from the portfolio (disproven
premise); `neo-generic-redirect-resolution` (B1) absorbs the remaining
async-blocker investigation with a deterministic-probe requirement.

**UPDATE 2026-07-08 (neo-generic-redirect-resolution B1 — PARTIAL: B1
EXONERATED; real blocker re-isolated to the suspend path):** the deterministic
`TaskCompletionSource`-style probe (`NeoStep20_TC8_IncompleteAwaitHitsTaggedNIE`,
backed by `TestCLRBinding.GetIncompleteTask` — a TCS whose `SetResult` is NEVER
called, so `IsCompleted` is deterministically false) was run on HEAD
`38133af8`. Verdict: **outcome 2a-DEEP, NOT the predicted outcome 3.** The probe
does NOT throw the tagged NIE — it HANGS. Two findings:
(1) **B1 redirect RESOLUTION is EXONERATED.** The custom
`AwaitUnsafeOnCompleted`/`AwaitOnCompleted` open-def NIE redirects ARE correctly
registered on `RedirectMapNeo` (17 Await keys confirmed empirically; registrations
land on the Neo map and persist), and `TryGetRedirection` is arity-agnostic and
correct (the closed-generic call's `GetGenericMethodDefinition()` handle matches
the registered open def). `get_IsCompleted` is also correct: the custom
`TaskAwaiter_T_GetIsCompleted_Neo` returns FALSE for the incomplete Task (proven
by `NeoStep20_TC10` + a redirect trace). So the design's outcome-3 prediction and
its proposed fix sites (`TryGetRedirection` / JIT call-operand) do NOT apply; NO
engine edit was made.
(2) **The REAL blocker is a MoveNext control-flow bug in the truly-async path.**
After `get_IsCompleted` returns false, the state machine NEVER reaches the
`AwaitUnsafeOnCompleted` call (its `Call` handler never fires) AND never reaches
`GetResult` either — it hangs in a loop/block transition between the `brtrue` and
either branch. This is suspend-path territory. CAVEAT: this PARTIALLY RE-OPENS
the 2026-07-07 `neo-async-controlflow-iscompleted` "control-flow is NOT a bug on
HEAD" conclusion — that conclusion was drawn from a RACY `Task.Delay` dump (and
possibly a different state-machine shape), which the deterministic TCS probe now
contradicts. The exact mechanism needs instruction-level tracing (the suspend
follow-up's first task).
**Ship state (test-only partial):** TC8 is MARKED `[ILRuntimeTest(Ignored = true)]`
(the hang-reproducer, excluded from the smoke so it cannot hang the run; un-ignore
once the control-flow bug is fixed — the redirect already resolves, so the tagged
NIE will fire). TC9 (sync control) + TC10 (IsCompleted diagnostic) ship as active
hang-proof guards. The async helper bodies are PRIVATE (harness skips the hanging
helper). ALL temp engine instrumentation reverted (engine at HEAD).
**Gates:** NeoStep20 `Ran 12, 0 failed, 1 ignored`; NeoStep `Ran 218, 0 failed, 1
ignored` (215 baseline + TC9 + TC10 + TC8-ignored); Legacy-neutral (plain `Debug`
CLI 0 errors; Legacy NeoStep20 `Ran 12, 0 failed, 1 ignored`). NeoOptHardening
unchanged (no engine edit).
**Next:** the MoveNext control-flow fix is owned by the `neo-step20-async-suspend`
resume child (it must land BEFORE the tagged-NIE body is reachable). See
`openspec/changes/neo-generic-redirect-resolution/{design.md, planning-context.md,
handoff/implementer-1.md}`.

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

### D-PEEP — `box T; isinst U` peephole + `PatchKind.IsinstResult` (Step 15 -> DEFERRED scoped-deferral 2026-07-08)

**DEFERRED (scoped-deferral 2026-07-08, neo-peephole-isinst).** A HEAD `70505eba`
dump-gate updated the premise: `PatchKind` now EXISTS post-`neo-step22-generic-
template` (`GenericMethodTemplate.cs:54` `enum PatchKind { TypeToken, MethodToken,
IsRefMoveFlag }` + `PatchEntry` keyed by `GenericParamIdx` + `CecilToken`) but is
the WRONG shape for a peephole fusion -- it is a generic-method-template T-identity
VALUE-SUBSTITUTION mechanism, whereas a `box;isinst` fusion is an OPCODE-STREAM
REWRITE (delete the box, merge into the isinst) that the patch table's fields/
applier cannot express; adding an `IsinstResult` kind would not help. NO peephole/
fusion pass exists (grep of `RegisterVM/` for `peephole|fuse|fusion|IsinstResult` =
zero matches; the optimizer passes are FCP/BCP/ELDC/InlineMethod/RegisterCleanup +
the Neo back-half, none pattern-match adjacent opcodes). `box T; isinst U` IS
emitted adjacently (`JITCompiler.cs:2637-2645`) and runs correctly via two arms
(`Box` + `Isinst`) with no box-fusion fast path; copy-prop can move the box away
from the isinst, so a correct fusion needs real def-use/liveness, not a trivial
adjacency peephole. Resolution = DEFERRED: hosting the fusion requires substantial
NEW infra (a peephole-pass framework + liveness + a fused opcode on a standalone
`OpCodeR` field per the F-8 union discipline). Forcing that for a non-functional
gain on the lowest-priority item is the "force a fix past the dump-gate"
anti-pattern. Route: a future peephole-pass child. The `box;isinst` path stays
correct un-fused. See
`openspec/changes/archive/2026-07-08-neo-peephole-isinst/ship-log.md`.

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

**RESOLVED -> `neo-array-multidim` (child, 2026-07-06):** multi-dimensional
arrays (rank-2+) are DELIVERED. The planner's pre-dump hypothesis ("rank-aware
Address/Get/Set callvirt + frame model + `new T[n,m]` do NOT fall out of rank-1
work; stays a JIT NIE") was DISPROVEN by the HEAD dump: `new T[n,m]` ctor +
`Set` + `Get` + metadata (`Rank`/`Length`/`GetLength`) all work via the AUTOGEN
binder path (registered types like `int[,]`) AND the reflection-fallback path
(`long[,]`, `string[,]` -- no binder). The child closed 3 reflection-fallback
gaps in `CLRMethod.cs` / `ILIntepreter.Neo.cs`: (Gap 1) Neo reference-return
write-back now encodes null as the -1 sentinel (was a valid mStack index ->
false-positive `!= null` via `cgt.un`); (Gap 2) Neo null-`this` guard
(`thisIdx < 0 -> null -> NRE`, was `mStack[-1]` ArgOutOfRange); (Gap 3)
`TargetInvocationException` unwrap on BOTH engines (rethrow InnerException via
ExceptionDispatchInfo). Neo smoke 198/198; Legacy stash-toggle 723/11 -> 723/10
(OutOfRange now passes on both engines; 0 regression). IL VT-element `[,]` +
multi-dim `Address` (ldelema) stay deferred (Non-Goals; neo-byref follow-up).
See `openspec/changes/neo-array-multidim/planning-context.md`.

---

- `Stelem_I` is lowered (correct 3-register encoding) but has NO interpreter arm
  (Step-tagged NIE) — rare `IntPtr[]`/`UIntPtr[]` native-int store.
- generic-token `Code.Ldelem`/`Code.Stelem` and native `Code.Ldelem_I`/`Ldelem_U8`
  are not enumerated by JIT `Translate` -> JIT-time NIE; rare in C# output.
- multi-dimensional arrays: rank-1 (neo-array-completion) AND rank-2+
  (neo-array-multidim, 2026-07-06).
**Resolution:** rank-1 RESOLVED 2026-07-06 (neo-array-completion); multi-dim
RESOLVED 2026-07-06 (neo-array-multidim -- primitive + ref element via autogen
binder + reflection fallback; Gap 1/2/3 closed).

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
- **F-10 / NEO-CLRSTRUCT-FIELD-OF-IL** — a CLR-struct field of an IL instance
  (async SM `<>t__builder`/`<>u__1`, or any IL class with a CLR-struct field)
  was laid out as a reference slot (`ManagedObjects[ReferenceOffset]`) but the
  JIT `ldflda` addressed it as a primitive offset -> Primitives OOB on the
  non-generic-Task SM (happened to fit on Task<int> by layout accident). The
  design premise ("only `ldflda` broken; `Stfld_Ref`/`Ldfld_Ref` already
  correct") was DISPROVEN by the Block-0 dump (Stfld_Ref read flat bytes as a
  ref index; Ldfld_Ref wrote a ref index into flat bytes). RESOLVED 2026-07-06
  (neo-clrstruct-field-of-il): encoding-only fix (Option A; NO layout change,
  NO `ILType.cs`/`Optimizer.Neo.cs` edit). β offset-discriminator —
  `NeoLdfldaClrStructFieldMarker = 0x2` (Operand4 bit 0x2; `Stfld_Ref`/
  `Ldfld_Ref` discriminator `Operand4 != 0`, stamps `fieldType.GetHashCode()`)
  + runtime flag `NeoF10ByrefOffsetFlag = 0x40000000` (bit 30 of the offset
  half). All THREE arms (Stfld_Ref, Ldfld_Ref, Ldflda) made consistent —
  box/unbox/flatten the CLR struct at `ManagedObjects[ReferenceOffset]` via
  `ReadNeoValueType`/`WriteNeoValueType`. `NeoMarshalByrefFieldToSlot`'s
  elemType-recovery fallback FAILS LOUD (tagged NIE) on null/uninit. Blast
  radius CONFIRMED SAFE (5 field shapes; existing paths byte-identical when
  `Operand4 == 0`). Neo-only, Legacy-neutral. Verification: 7 F-10 probes
  FAIL-on-HEAD -> PASS; Step 20 sync 2 -> 4 green (TC4 + TC6 unblocked, TC1/TC7
  stay green); NeoStep 190/190, NeoStep20 9/9, ClrStructField 8/8. New
  accepted-known-deferred edge F-10-R1 (the F-6/F-10 both-stamp shape; latent,
  gated behind constrained-VT; the reviewer's runtime-reorder fix was
  DISPROVEN, the JIT-discriminator gate is the tracked resolution). Step 20
  redirect edges (TC2/TC3/TC5) are Step-20 follow-ups, NOT F-10. See §3 F-10 +
  F-10-R1.
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

