# Planning Context — implement-neo-step17

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent
> "按照你建议的顺序，继续按当前流水线推进后续内容！"

This run = **Step 17** (ref/out params + ldloca/ldflda + stind/ldind — the
unified 8-byte Ref Slot / byref model). It ALSO folds in the deferred items
**D-LDELEMA** (`ldelema`) and **D-CONSTRAINED** (`constrained.`-on-value-type)
from `.trae/documents/neo-deferred-items.md`, whose prerequisite is exactly this
byref/VT-address model. Committed AND pushed after review clean (user
pre-authorized commit+push per phase).

Prior state (committed + pushed): Steps 11-16 + OPT-HARDEN (K1 fixed). HEAD=`e3fa8ef2`. NeoStep smoke baseline = 72/72 (NeoOptHardening tests run under a separate filter).

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose** (user frames each step as one phase). BUT: Step 17 is large
and touches pervasive machinery (frame layout, call ABI for byref params, every
ldloca/ldflda/stind/ldind). The planner MUST assess scope and may phase tasks.md
or DEFER a sub-part (e.g. CLR-object stind/ldind via field hash, or
generic-byref) to a follow-up — state explicitly in proposal.md if so. Pipeline:
propose → apply → verify → review-loop → ship → archive → (LEAD commits + pushes).

## 3. Step 17 scope (from `.trae/documents/neo-implementation-steps.md` §"Step 17")
Goal: unified Ref Slot representation + stind/ldind dispatch.
Content:
1. **Ref Slot definition:** 8 bytes = `(objectIndex: int, offset: int)`.
   - `objectIndex == -1` -> frame native (unmanaged) memory; offset is an absolute frame byte offset.
   - `objectIndex >= 0` -> an object in mStack; offset is a field offset (or field hash for CLR).
2. **`Ldloca`:** produces `(-1, absoluteFrameOffset)`.
3. **`Ldflda`:**
   - heap IL object -> `(objMStackIndex, fieldPrimitiveOffset)`.
   - in-frame value type -> `(-1, absoluteFrameOffset + fieldOffset)`.
   - CLR object -> `(objMStackIndex, fieldHash)`.
4. **`stind_*` / `ldind_*` dispatch:**
   - `objectIndex == -1` -> `*(T*)(nativePointer + offset)`.
   - `ILTypeInstance` -> pin `Primitives` then read/write (and the ref region).
   - CLR object -> `GetFieldValue`/`SetFieldValue`.
5. **JIT compiler** allocates 8 bytes for byref slots.

Dependency: Step 4 (byte* frame). Validates: `void Increment(ref int x){x++;}` (frame ref); `ref field` (heap obj field ref); `out` params; ref-passing of an in-frame VT's field.

**Fold-in deferred items:**
- **D-LDELEMA** (Step 16): `ldelema` produces `(arrayMStackIndex, elementOffset)`. Now resolvable inside Step 17 (its consumers stind/ldind are Step 17).
- **D-CONSTRAINED** (Step 13 area 3): `constrained.`-on-value-type specialization needs the VT `this` address (ldarga/ldloca producing a frame ref) + a Constrained runtime arm. Resolvable inside Step 17 (or a tiny completion right after).

## 4. Foundation + RESEARCH REQUIRED (planner — be thorough)
- **Current Neo state:** grep `Ldloca`, `Ldloca_S`, `Ldflda`, `Ldarga`, `Ldarga_S`, `Stind`, `Ldind`, `Stobj`, `Ldobj`, `stind`, `ldind`, `Ref Slot`, `byref`, `Starg` in `ILIntepreter.Neo.cs` + `JITCompiler.cs` + `Optimizer.Neo.cs`. Map what exists vs NIE. NOTE: Step 12 added an `addrAlias` folding (`NeoAddressAlias`/`ResolveAddressAlias`) + `Ldloca`/`Ldflda` no-op ExecuteNeo arms that fold `ldloca;ldflda` chains to compile-time offsets. Step 17's Ref Slot model likely SUPERSEDES that folding (refs become real runtime values) — reconcile carefully (don't break the Step 12/12b/13 inline field access that relies on the folding; either keep both or migrate cleanly).
- **The OPT-HARDEN K1 fix:** added a `ldloca`-kill in FCP (when an ldloca addresses a propagated source/dest, kill the propagation). Step 17 makes ldloca produce a real Ref Slot — verify the K1 fix still holds / is still needed once ldloca is a real ref (the address-escape concern is the same; the ldloca-kill is still sound).
- **The Legacy reference:** `ILIntepreter.Register.cs` `ExecuteR` `Ldloca`/`Ldflda`/`Ldarga`/`stind`/`ldind`/`Stobj`/`Ldobj` + byref parameter passing (StackObject-based). Read it — it is the spec for the SEMANTICS (Neo uses the 8-byte Ref Slot, not StackObject, but the dispatch logic is the reference). Do NOT modify Legacy.
- **Call ABI for byref params:** how are `ref`/`out` params passed today (NeoCallParamMap/CopyNeoCallArguments)? A byref param should pass the 8-byte Ref Slot (not copy the value). Determine how the JIT/call-lowering must change to pass a byref slot and how the callee reads it (the callee's parameter slot is an 8-byte byref).
- **Frame layout:** an 8-byte byref slot in the frame byte region (2 ints). How `AllocateLocalStackSpaces`/`AllocateSlotForType` sizes a byref-typed local/param (8 bytes, alignment 4-or-8).
- **CLR-object stind/ldind** via field hash: the CLR field-addressing path (`(objMStackIndex, fieldHash)`) — find the existing CLR field get/set by hash (Step 15 / the CLR binding infra). This is the most complex sub-part; it may be DEFERRABLE if rare.
- **constrained.:** the `Constrained` opcode currently has no runtime arm and is re-appended after the callvirt (`JITCompiler.cs:1763-1772`). Step 17 needs either a Constrained runtime arm or a JIT-time box/direct-call lowering informed by constrained.

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new runtime code behind `#if ENABLE_NEO_MODE`. Legacy (`ExecuteR`) is the REFERENCE, not to modify.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression — byref/ldloca/stind are pervasive; the Step 12 addrAlias folding + Step 17's Ref Slot must not regress the 72): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (your NeoStep17 cases add; NO existing case regresses; previously-failing ref/out/ldloca-stind tests may turn GREEN). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoStep17Test.cs`, ASCII. Cover: `ref int` frame ref (`Increment(ref x)`); `ref field` (heap obj); `out` param; ref to an in-frame VT field. No throw-asserting tests (harness limitation).
- Unimplemented-op NIE (Step-tagged) = TODO not bug; your ref paths must not throw those for implemented cases.
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION (HIGHEST of all steps):** the Step 12 addrAlias folding + the OPT-HARDEN ldloca-kill + the inline field access are all ldloca-adjacent. Step 17 changes ldloca to produce a real Ref Slot — a bug here can regress EVERY value-type-field-access test (Steps 12-16). The full NeoStep smoke is the gate; special care reconciling addrAlias folding.
- **CJK write caveat:** Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-step17/`:
- `proposal.md` — Why / What Changes / Impact + the explicit In/Deferred list + the addrAlias-folding reconciliation strategy + how D-LDELEMA and D-CONSTRAINED fold in.
- `design.md` — concrete, code-grounded: the 8-byte Ref Slot representation + how it's stored in the frame; `Ldloca`/`Ldloca_S`/`Ldarga` arms; `Ldflda` per-operand-kind; `stind_*`/`ldind_*` dispatch (frame-native / ILTypeInstance-pinned-Primitives / CLR field hash); JIT 8-byte byref slot allocation; byref param call-ABI (pass the slot, callee reads it); the addrAlias reconciliation (keep/migrate); constrained.-on-VT (runtime arm or JIT lowering); D-LDELEMA. Edge cases + non-goals (CLR-generic-byref if rare; explicit-interface byref; etc. — scope honestly).
- `specs/<capability>/spec.md` — ADDED requirements, fresh. Capability `neo-byref` (new) or extend.
- `tasks.md` — phased checkboxes; mark deferred items.

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

## 8. Planner findings (2026-07-04, propose stage)

### 8.1 addrAlias reconciliation -- DECISION: COEXIST, not replace
The Step 12 `addrAlias` folding (`Optimizer.Neo.cs:LowerNeoOffsets`,
`NeoAddressAlias` struct @882-893, `ResolveAddressAlias` @888, alias build
@32-78, consumers @400/508/526/542/551) resolves the pure
`ldloca V;[ldflda f;]stfld/ldfld/initobj` in-frame-VT pattern to compile-time
absolute frame offsets, and the `Ldloca`/`Ldflda` ExecuteNeo arms are runtime
no-ops (@503-524) BECAUSE of it. Replacing this with "always real Ref Slot"
would regress the most common `ldloca` emission. Step 17 KEEPS the folding as
the fast path and adds the real Ref Slot only for dests whose consumers
ESCAPE the folding window. The mechanism is a CONSUMER-SCAN gate added after
the alias-build pass: a dest stays folded only if EVERY consumer is foldable
(`_Inline`/`Initobj`/foldable `ldflda`); any byref-escape consumer
(`stind`/`ldind`/`stobj`/`ldobj`/`ldelema`/byref Call param/constrained box)
removes it (and its inheritors) from `addrAlias`, making the real arm produce
the Ref Slot. This is purely ADDITIVE (only ever removes folding, never adds
it), so Steps 12-16 fast paths are untouched. The full NeoStep smoke (72) is
the gate.

### 8.2 OPT-HARDEN ldloca-kill (K1) stays sound
The FCP ldloca-kill keys off the `Ldloca` opcode itself (kills a copy-prop
when xSrc/xDst is addressed by it). Taking an address = potential mutation
through it, regardless of folded-vs-real. The kill stays unchanged; the new
real producers (`Ldflda`/`Ldarga`/`Ldelema`) should ALSO be treated as
escapes by the kill (task 7.1).

### 8.3 Current state of the byref opcodes (Neo)
- `Ldloca`/`Ldflda` ExecuteNeo arms: no-ops (@503-524), exist ONLY because of
  addrAlias folding. Step 17 makes them real.
- `Ldarga`/`Ldarga_S`: NO ExecuteNeo case at all (falls to default NIE). New
  arms added. JIT emits R1/R2 (@2042-2050); optimizer lowers R1/R2 like Ldloca
  (@461-462).
- `Stind_*`/`Ldind_*`/`Stobj`/`Ldobj`: JIT emits R1/R2 (@2085-2156) but NO
  `#if ENABLE_NEO_MODE` block and NO ExecuteNeo arms. New arms added. `Stobj`/
  `Ldobj` carry the type token in `Operand`.
- `Ldelema`: JIT register-allocates R1/R2/R3 (@1943-1958) but no arm. New arm.
- `Constrained`: JIT moves it AFTER the callvirt (@1819-1829), flags the call
  `Operand4=1`, stamps `old.Operand2=op.Operand2`; carries the constrained type
  token in `Operand`. NO runtime arm. Step 17 adds the runtime arm (D-CONSTRAINED).
  NOTE: existing neo-boxing spec phrased constrained. as COMPILE-TIME lowering;
  the delta MODIFIES it to permit the runtime-arm realization (the JIT cannot
  do compile-time lowering as it stands).

### 8.4 Byref call-ABI gap
`AllocateNeoCallParamSlot` (`Optimizer.Neo.cs:835`) does NOT branch on
`IsByRef` -- a byref param is mis-sized by `TypeForCLR` (strips byref). Fix:
byref branch FIRST (Size=8, RefCount=0). Then the existing param-copy loop
(@758-776) naturally copies one 8-byte primitive entry (no ref entry) = the
Ref Slot value, not the referent. IL-method byref is the green target; the
IL-to-CLR byref crossing (CLR `ref`/`out` via autogen ReadNeo*) is DEFERRED to
Step 13b (D-13B area 5; also closes K2/K2-FAM).

### 8.5 In/Deferred list (authoritative, per proposal.md + tasks.md)
IN: Ref Slot repr + frame storage; Ldloca/Ldloca_S real arm + addrAlias
coexistence; Ldarga/Ldarga_S; Ldflda (in-frame-VT + heap-IL arms);
stind_*/ldind_* + stobj/ldobj for frame-native + ILTypeInstance (pinned-
Primitives) targets; byref call-ABI for IL ref/out params; D-LDELEMA;
D-CONSTRAINED runtime arm (common VT cases).
DEFERRED: CLR-object stind/ldind via field hash -> Step 13b; CLR-method
ref/out params -> Step 13b; generic-byref; explicit-interface byref; `fixed`;
interface-on-VT constrained (sub-case). All deferred items throw a Step-17-/
13b-tagged NIE, not silent mis-handling.

### 8.6 Ref Slot encoding edge case (documented)
A Ref Slot = (objectIndex:int, offset:int), 8 bytes, 4-aligned. For a ref
FIELD of an in-frame VT (whose refs live in the frame's parallel ref region,
not the byte region), reserve `objectIndex==-1, offset<0` = frame-ref index
`~offset`. For a ref field inside a heap ILTypeInstance, `objectIndex>=0` with
a sentinel flags the `ManagedObjects` index. Rare sub-case; smoke-covered if
natural, otherwise a documented encoding (design sec 1.3/3.2).

### 8.7 Capability map
New: `neo-byref`. Modified deltas: `neo-value-types` (addrAlias gate +
ldloca/ldflda no longer always no-op + K1 soundness), `neo-arrays`
(`Ldelema` now implemented, supersedes "Ldelema is out of scope"),
`neo-boxing` (constrained. permits runtime-arm realization). All specs are
ASCII-clean. Change is apply-ready (`openspec status` = 4/4 artifacts).

## 9. Implementer findings (2026-07-04, apply stage)

### 9.1 Result summary
- CLI `Debug_Neo`: 0 errors. TestCases `Debug`: 0 errors.
- FULL NeoStep smoke: **79/0** (was 72; +7 NeoStep17 green: TC1 ref-frame-local,
  TC2 ref-accumulate, TC3 out-param, TC4 byref-forwarded, TC5 ref-heap-field,
  TC6 ref-in-frame-VT-field, TC7 ldelema round-trip). 0 Steps 12-16 regression.
- NeoOptHardening K1 regression: 3/3 (the K1 kill still fires AND is extended to
  Ldflda/Ldarga/Ldelema).
- Max per-test time 14ms (no infinite loops).

### 9.2 addrAlias gate -- the crux (what actually shipped)
The gate is a forward liveness walk over the pre-lowering body, NOT a simple
membership check. Two non-obvious requirements emerged during apply:
1. **Register reuse:** at the pre-lowering stage the same register number is
   both an ldloca dest AND a later unrelated value (a call result, etc.). The
   gate must track which alias dests are CURRENTLY LIVE (defined by an address
   producer, killed by any other writer) and only mark an escape on a non-
   foldable READ of a LIVE alias. A naive membership check over-escapes and
   regresses basic arithmetic (the first attempt broke NeoStep16 TC5 and, via
   shared mStack state, NeoStep6).
2. **Mixed-reuse guard:** eval-stack registers are reused across MULTIPLE
   address computations in one method. The same register can be a folded
   in-frame-VT address in one live range (consumed by `_Inline`) AND a genuine
   byref in another (consumed by stind/a byref call). Evicting such a MIXED
   register orphans its `_Inline` consumers (which read it as a folded frame
   offset, not a Ref Slot). So a register is evicted ONLY IF it has NO foldable
   consumer anywhere (`hasFoldableUse` set). Mixed-reuse registers stay folded;
   their escape consumer reverts to the pre-Step-17 junk/NIE behavior. The
   pure-byref cases this step targets (a register dedicated to the byref) are
   unaffected -- which is why TC1-TC5 (dedicated byref registers) are green
   while the model still leaves a VT-local-whose-address-also-escapes (`p` in
   TC6) correctly handled (TC6 is green because the C# compiler emits the
   `ref p.x` path on a SEPARATE register from the `p.x=1; p.y=2` _Inline path
   -- so no mixing occurs there in practice).
3. **Chain consistency:** an escape taints the WHOLE alias chain (the connected
   component in the `.Reg` forest), propagated both up (inheritor -> base) and
   down (base -> inheritors) to a fixed point -- because a real ldflda reads
   its base's Ref Slot, so the base cannot stay folded, and vice-versa.

### 9.3 Ldflda -- runtime dispatch, not an Operand4 marker
DEVIATION from design sec 2.3: the Ldflda arm dispatches at RUNTIME on the
operand slot's objectIndex half (-1 => in-frame VT, read the offset half as the
VT base; >=0 => heap IL mStack index) instead of an optimizer-stamped Operand4
marker. Self-describing; the build/gate already guarantee a real Ldflda's
operand is a real Ref Slot (frame-native) or a heap mStack index.

### 9.4 Ldelema -- element-instance parking, not (idx, byteOffset)
DEVIATION from design sec 2.4: for an IL value-type array (ILTypeInstance[]),
the arm resolves the element ILTypeInstance and parks it on mStack, encoding
`(elementMStackIdx, 0)`. The C# compiler emits `arr[i].field = v` as
`ldelema; stfld` (NOT stind), and stfld reads the operand's first int as an
mStack index -- so the element-instance encoding makes both stind/ldind AND
stfld/ldfld consumers work uniformly. CLR primitive-array ldelema NIEs (use
direct indexing). NOTE: the Ldelema lowering must use R2 (not R1) for the array
offset -- R1==R2 pre-compaction is NOT guaranteed after register compaction
(using R1 caused an ArgumentOutOfRangeException).

### 9.5 Byref call-ABI -- the IsByRef sizing branch is sufficient
The single byref branch in `AllocateSlotForType` (8 bytes, RefCount 0, align 4)
and the matching one in `AllocateNeoCallParamSlot` (contiguous 8 bytes) are all
that's needed. The existing call-param-map primitive-copy loop naturally emits
one 8-byte entry (RefCount 0 -> no ref entry) = the Ref Slot value. The
callee's `ldarg` is an 8-byte Move; stind/ldind dispatch back to the caller's
frame/object. No new call-copy code was required. TC1-TC5 prove mutation
propagates across the call boundary.

### 9.6 D-CONSTRAINED -- DEFERRED (the one design target not met this pass)
Full constrained.-on-VT dispatch requires the callvirt to accept a byref `this`
(the struct's managed address from ldarga/ldloca) and dispatch to the
constrained type's concrete override. The callvirt currently reads `this` as an
mStack object index and NIEs ("Neo callvirt this is null") on the byref. The
Constrained runtime arm therefore throws a Step-17/13b-tagged NIE (honest)
rather than silently no-op'ing. The prerequisite callvirt-byref-this dispatch
is the deferred work (no green constrained test; TC8 removed). This is the
explicitly-allowed "leave the rare cases" of design sec 6.

### 9.7 Partial implementations (documented, green-smoke-covered)
- `Stobj`/`Ldobj` copy `TotalPrimitiveSize` bytes only; the ref-slot portion of
  a VT (TotalReferenceCount) is NOT yet copied through stobj/ldobj. Green target
  is primitive-field VTs.
- `Stind_Ref`/`Ldind_Ref` handle the frame-native ref slot; the heap-IL ref-
  field case (design sec 1.3/3.2 negative-offset/sentinel encoding) NIEs.

### 9.8 Deferred items confirmed NIE (untouched)
D1 CLR-object stind/ldind via field hash -> a CLR object reaching the stind/
ldind IL-instance path throws (via GetNeoILInstance's InvalidCastException; the
field-hash plumbing is Step 13b). D2 CLR-method ref/out (IL->CLR byref crossing)
-> Step 13b. D3 generic-byref. D4 `fixed`. D5 interface-on-VT constrained.
None silently mis-handled.
