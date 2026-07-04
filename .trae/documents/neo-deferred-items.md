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
| F-MAJ-1 | 2+ simultaneous CLR struct locals -> AllocateLocalStackSpaces slot-reuse -> silent wrong result | Step 13b | **[OPT-HARDEN-2]** | next optimizer-hardening / AllocateLocalStackSpaces | pre-existing (13b made reachable) |
| Q-NEWOBJ | Newobj dest/arg aliasing after a `newarr` | Step 16 | **RESOLVED (Step 18, non-reproducible)** | — | not reproducible on HEAD (JIT dump: distinct frame regions + ref slots per register); same outcome as Q-STRUCT/Q-LONG |
| Q-VT-NEWOBJ | IL value-type `newobj` (real, non-inlined) + `call VT ctor` via ldloca | Step 18 | **[VT-THIS-ADDR]** | D2: track a VT `this`/newobj-dest as an in-frame address for ALL field access (ctor stfld + caller ldfld) | blocked on VT field-access lowering consistency (mixed inline/heap stfld; addrAlias only tracks ldloca); Newobj arm NIE-tagged |
| Q-STRUCT | struct-local + field-mutation + element-read temp-renumber | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect `Optimizer.BCP.cs:97-141` | pre-existing (unconfirmed) |
| Q-LONG | long default-zero compare (conv.i8) quirk | Step 16 | **deferred** | not reproducible on HEAD (probes pass); suspect conv.i8 / branch type-spec | pre-existing (unconfirmed) |
| D-CHECKEX | `CheckExceptionType` NIE for non-CLRType catch types | Step 14 | **partial ([CATCH-COMPLETE])** | CheckExceptionType IL branch done; end-to-end needs adaptor + Throw | shared-engine gap (CheckExceptionType piece closed) |
| D-IL-EXCEPTION-THROW | End-to-end IL-exception catch (Exception-adaptor + Throw-for-IL) | Step 18/CATCH-COMPLETE | **future** | System.Exception CrossBindingAdaptor + Throw `as Exception` handling for IL instances | new follow-up |
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

### F-MAJ-1 — AllocateLocalStackSpaces slot-reuse with 2+ CLR struct locals (Step 13b -> [OPT-HARDEN-2])
A method holding 2+ simultaneous CLR struct locals (+ int locals) hits a
slot-reuse/liveness bug in `JITCompiler.AllocateLocalStackSpaces`: one struct
local's 12-byte slot is corrupted while another is live -> silent wrong result
(a combined `r1!=600 || r2!=3` check fails though each passes in isolation; not
an r1<->r2 clobber). Pre-existing (stash-proven: pre-13b the pattern was an
unsupported-NIE, not broken; struct-specific — two CLR-INT-return locals pass).
Step 13b made it reachable (the CLR-struct-by-value feature now exists).
**Resolution:** next optimizer-hardening step ([OPT-HARDEN-2]) — the
`AllocateLocalStackSpaces` slot-reuse/liveness logic. The Step 13b tests work
around it by holding a single CLR struct local at a time.

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

### Q-VT-NEWOBJ — IL value-type `newobj` + `call VT ctor` via ldloca (Step 18 -> [VT-THIS-ADDR])
A real (non-inlined) IL value-type `newobj` (emitted when the VT value is NOT a
local -- e.g. a method return value, or a boxed/field/arg value) cannot be
constructed correctly. The VT ctor's `this`-relative `stfld` lowers to a MIX of
in-frame `_Inline` (writes the callee frame bytes) and heap
`Stfld_*`/`GetNeoILInstance` (treats `this` as an mStack index -> ILTypeInstance),
and the caller's subsequent field reads on the newobj result are non-inline
(expect an mStack object index). The `addrAlias` folding only tracks
`ldloca`-produced addresses, not a `this` param or a newobj dest, so the VT
representation is inconsistent end-to-end. A heap-alloc + copy-back fallback is
ALSO infeasible without first fixing the consistency. The C# compiler lowers
`VT x = new VT(args)` on a local to `ldloca + call ctor`, which hits the SAME
VT-`this` field-access issue (so the common local form is also affected). The
Step 18 Newobj arm surfaces a clear Step-18-tagged NIE for the VT case (not a
silent wrong result).
**Resolution:** dedicated [VT-THIS-ADDR] follow-up -- the D2 JIT change: track a
value-type `this` (param slot 0) and a VT newobj dest as an in-frame address for
ALL field access (ctor stfld + caller ldfld), reusing/extending the Step 17
byref/addrAlias machinery. Touches the Step 12 VT frame layout / the shared
field-access lowering used by every VT instance method. Confirmed `ParamInfos[0]`
for a VT ctor is sized as the in-frame value (`Size = TotalPrimitiveSize`,
`RefCount = TotalReferenceCount`), NOT an 8-byte byref (`JITCompiler.cs:1332-
1344`).

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

### D-CHECKEX — `CheckExceptionType` NIE for non-CLRType catch types (Step 14 -> PARTIAL [CATCH-COMPLETE])
The shared engine's `CheckExceptionType` (`ILIntepreter.cs:~5823`) threw NIE for
catch types that are not CLRType (an `ILType` catch clause). Neo catch matching
uses this shared path. **PARTIAL RESOLUTION (CATCH-COMPLETE, 2026-07-04):** the
NIE is replaced with an IL branch — `exception as ILTypeInstance` → exact or
`CanAssignTo(catchType)` (reuses Step 15's CanAssignTo) → else CLR fallback.
Shared-engine (NOT Neo-gated); Legacy-neutral (the new branch is unreachable for
every existing CLRType catch). D-CHECKEX's CheckExceptionType piece is CLOSED.
**End-to-end IL-exception catch still needs more** (see D-IL-EXCEPTION-THROW),
so no positive IL-catch test is authorable yet.

### D-IL-EXCEPTION-THROW — end-to-end IL-exception catch (Exception-adaptor + Throw-for-IL) (-> future)
Even with D-CHECKEX's CheckExceptionType branch, throwing an IL-typed exception
and catching it end-to-end is blocked on TWO more pieces: (a) a registered
`System.Exception` `CrossBindingAdaptor` (an IL `class X : System.Exception`
throws TypeLoadException at `ILType.cs:1418` without it); (b) the `Throw` opcode
does `mStack[idx] as Exception` on BOTH engines (`ILIntepreter.Neo.cs:~3159`,
`ILIntepreter.Register.cs:~5310`) → a plain IL class (an `ILTypeInstance`, not
an Exception) NREs. Resolution: register an Exception adaptor + handle IL
instances in Throw. The positive IL-catch test is reserved for that pass.

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
- **K1** — FCP value-type-move mis-propagation. Fixed in OPT-HARDEN (2026-07-04)
  via the `ldloca-kill` (Neo-only, Legacy-neutral). See §3 K1.
- **K2** — Step 8 VT-by-value param copy (reads primitive as mStack index).
  Fixed in Step 13b (2026-07-04) via the unified CLRMethod param layout +
  ReadNeoValueType. See §3 K2/K2-FAM.
- **D-LDELEMA** — `ldelema` opcode. Fixed in Step 17 (2026-07-04, IL VT array
  path; CLR primitive-array ldelema still NIE). See §3 D-LDELEMA.
