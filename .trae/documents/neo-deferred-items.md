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
| D-LDELEMA | `ldelema` opcode | Step 16 | **RESOLVED (Step 17)** | Step 17 Ref-Slot/stind/ldind | resolved (IL VT array path; CLR primitive-array ldelema still NIE) |
| D-CONSTRAINED | `constrained.`-on-VT specialization (Step 13 area 3) | Step 13 | **partial (Step 17)** | Step 17 byref/VT-this-address | arm exists, full VT dispatch DEFERRED (callvirt byref-this) |
| D-13B | Step 13 areas 4-5 (binding codegen + CLRMethod param layout) | Step 13 | **partial (Step 13b)** | Area 5 core done; Area 4 + CLR ref/out + CLR stind/ldind deferred | roadmap gap (highest value) |
| K1 | FCP mis-propagates value-type Moves (copy-then-mutate silent) | Step 12b | **RESOLVED (OPT-HARDEN)** | — | fixed (ldloca-kill) |
| K2 | Step 8 VT-by-value param copy reads primitive value as mStack index | Step 12b | **RESOLVED (Step 13b)** | unified param layout | fixed |
| K2-FAM | Move-path scalar->boxed-ref CLR-VT-local (reads int as mStack idx) | Step 13 | **partial (Step 13b)** | flat-bytes path resolved; boxed-ref bridge deferred | pre-existing |
| F-MAJ-1 | 2+ simultaneous CLR struct locals -> silent wrong result (representation mismatch, NOT slot-reuse) | Step 13b | **RESOLVED ([OPT-HARDEN-2])** | dump-confirmed: D6 return-write flat-bytes into a 4-byte boxed-ref local slot overflowed 8 bytes into the neighbour; fixed by declaring a CLR-VT local as flat-bytes (Option B, gated `#if ENABLE_NEO_MODE`) | pre-existing (13b made reachable); fixed 2026-07-05 |
| Q-NEWOBJ | Newobj dest/arg aliasing after a `newarr` | Step 16 | **RESOLVED (Step 18, non-reproducible)** | — | not reproducible on HEAD (JIT dump: distinct frame regions + ref slots per register); same outcome as Q-STRUCT/Q-LONG |
| Q-VT-NEWOBJ | IL value-type `newobj` (real, non-inlined) + `call VT ctor` via ldloca | Step 18 | **RESOLVED in [VT-THIS-ADDR]** | RESOLVED 2026-07-05: inline-stfld owner-type clobber fix + D1 Newobj-dest typing + Newobj temp sizing + copy-back runtime branch | fixed; full NeoStep smoke 99/99 |
| F-2 / INLINER-REFONLY-VT | ref-only VT (prim-size 0) local `new S(refArgs)` mis-compiles: inlined `stfld.ref.inline` writes don't survive to the following in-frame `ldfld.ref` read | neo-vt-this-addr re-review (F-1 probe) | **future** (fold into K2-FAM bridge or [OPT-HARDEN-3]) | JITCompiler inliner ref-fold over a 0-prim-size VT local | pre-existing (latent) |
| F-3 / NEO-BYREF-THIS | `new ClrStruct(args)` (CLR struct ctor via byref-`this`) hits a pre-existing reflection gap at `CLRMethod.Invoke:353` (ctor `this` read as a 4-byte mStack index, but the byref `this` is an 8-byte Ref Slot from `ldloca`) | neo-opt-harden-2 re-review | **future** (Step 17 byref-completeness / `[NEO-BYREF-THIS]` follow-up) | `CLRMethod.Invoke` byref-this-to-CLR-struct-ctor | pre-existing (NOT a regression; fails identically on `f673b9c9`) |
| F-4 / NEO-IL-EX-FIELDACCESS | Reading IL-declared fields/methods off a CAUGHT IL exception via the adaptor bridge is broken on Neo (4 broken read paths: `((CrossBindingAdaptorType)e).ILInstance` callvirt-on-CLR-interface -> InvalidCastException; `e.GetType()` callvirt.clr -> NIE; `appdomain.Invoke` instance-method -> NRE under ENABLE_NEO_MODE; `ILTypeInstance.this[index]` indexer -> null under ENABLE_NEO_MODE) | neo-il-exception-throw apply (OQ1/OQ2) | **future** (Step 13 Area 4 / cross-binding-adaptor follow-up) | Neo callvirt-on-CLR-interface + `appdomain.Invoke` instance-method re-entry + `ILTypeInstance` Neo indexer | pre-existing (NOT introduced; surfaces only because IL exceptions can now be thrown + caught); workaround `e is MyEx` (isinst) |
| Q-STRUCT | struct-local + field-mutation + element-read temp-renumber | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect `Optimizer.BCP.cs:97-141` | pre-existing (unconfirmed) |
| Q-LONG | long default-zero compare (conv.i8) quirk | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect conv.i8 / branch type-spec | pre-existing (unconfirmed) |
| D-CHECKEX | `CheckExceptionType` NIE for non-CLRType catch types | Step 14 | **RESOLVED ([CATCH-COMPLETE] + neo-il-exception-throw)** | CheckExceptionType IL branch + Exception-adaptor + Throw-for-IL all landed | shared-engine gap (fully closed; IL branch now reachable end-to-end) |
| D-IL-EXCEPTION-THROW | End-to-end IL-exception catch (Exception-adaptor + Throw-for-IL) | Step 18/CATCH-COMPLETE | **RESOLVED (neo-il-exception-throw)** | System.Exception CrossBindingAdaptor (built-in) + Throw `as Exception` IL-instance unwrap on BOTH engines | shared-engine gap (closed 2026-07-05; F-4 / NEO-IL-EX-FIELDACCESS follow-up surfaced) |
| D-PEEP | `box T; isinst U` peephole + `PatchKind.IsinstResult` | Step 15 | **opportunistic** | patch-infra step | optimization (non-functional) |
| D-ARR | Stelem_I / generic-token Ldelem·Stelem / native Ldelem_I·U8 / multi-dim | Step 16 | **opportunistic** | triggered by a test/feature | roadmap gap (rare) |
| N-CGTUN | Cgt_Un divergence comment (src=sentinel case) | Step 15 | **opportunistic** | — | cosmetic nit |
| N-CATCHWRAP | catch slot stores ILRuntimeException wrapper | Step 14 | **accept** (matches Legacy) | — | not-a-bug |
| N-TC2 | Step 14 TC2 asserts `e != null` | Step 14 | **resolved by Step 15** (isinst landed) | — | cleanup |

---

## 3. Per-item detail

### D-LDELEMA — `ldelema` opcode (Step 16 -> Step 17)
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

### D-CONSTRAINED — `constrained.`-on-value-type (Step 13 area 3 -> Step 17)
Step 13 deferred `constrained.` callvirt specialization on a value-type `this`
(`T.ToString()` where T:struct). Three blockers, all Step 17 territory: (1)
`ldarga`/`ldarga.s` unimplemented (Step 6 NIE -> Step 17 byref); (2) the
`Constrained` opcode has no runtime arm and is re-appended AFTER the callvirt
(`JITCompiler.cs:1763-1772`) so it cannot inform it; (3) the box-once/direct-call
lowering needs the value-type `this` address model (= Step 17). No green NeoStep
test exercises `constrained.` today (zero regression). **Resolution:** fold into
Step 17 (or a tiny "Step 13 area 3 completion" immediately after Step 17).

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

### F-3 / NEO-BYREF-THIS — `new ClrStruct(args)` byref-`this` ctor reflection gap (-> future)
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

**Resolution:** future -- route to Step 17 byref-completeness work or a
dedicated `[NEO-BYREF-THIS]` follow-up. The fix would teach
`CLRMethod.Invoke` to recognise a frame-native byref `this` (8-byte Ref Slot,
`objIdx == -1`) and read the struct bytes directly, instead of the 4-byte
mStack-index read.

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

### D-ARR — array-completion gaps (Step 16 -> opportunistic)
- `Stelem_I` is lowered (correct 3-register encoding) but has NO interpreter arm
  (Step-tagged NIE) — rare `IntPtr[]`/`UIntPtr[]` native-int store.
- generic-token `Code.Ldelem`/`Code.Stelem` and native `Code.Ldelem_I`/`Ldelem_U8`
  are not enumerated by JIT `Translate` -> JIT-time NIE; rare in C# output.
- multi-dimensional arrays: rank-1 only.
**Resolution:** implement the specific variant when a test or feature needs it.

### N-CGTUN — Cgt_Un divergence comment (Step 15 -> opportunistic)
The `Cgt_Un` arm's divergence comment names only the operand=sentinel case; the
symmetric source=sentinel case also diverges (same sentinel-collision class,
unexercised by the whole TestCases suite). Cosmetic. **Resolution:** one-line
comment tighten, anytime.

### N-CATCHWRAP — catch slot stores ILRuntimeException wrapper (Step 14 -> accept)
The Neo catch slot stores the `ILRuntimeException` wrapper (not the unwrapped
inner), which MATCHES Legacy exactly (`ILIntepreter.Register.cs:5327`). The
spec's "unwrapped" wording is loose. Not a bug. **Resolution:** accept; revisit
only if a real symptom appears (e.g. when isinst-on-caught-exception is exercised).

### N-TC2 — Step 14 TC2 asserts `e != null` (Step 14 -> resolved by Step 15)
Step 14 TC2 asserted `e != null` because the type-check-in-catch (`isinst`) was
Step 15. Step 15 has now landed isinst. **Resolution:** TC2 can be tightened to
assert the exception type/identity; opportunistic cleanup.

---

## 4. Resolved
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

