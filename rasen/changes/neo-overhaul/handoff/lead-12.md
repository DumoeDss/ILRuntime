# Handoff: neo-overhaul -- LEAD #12 (COMPREHENSIVE COMPLETION; 20-child portfolio)

> Read lead-11.md (the neo-completion-portfolio close-out) for the PRIOR chapter. This is the
> broader Neo opcode/correctness OVERHAUL -- a NEW portfolio (neo-overhaul) spun off after the
> completion-portfolio closed. Real context ~62% of 1M (the probe's `limit:200000` is stale; the
> subagent summaries kept the LEAD context lean). The overhaul's planned children are ALL DONE
> (20/20, runnableFrontier empty); this is a completion checkpoint, not a context-relay.

## Original intent
`/rasen:auto auto-decompose --no-gate ... read rasen/changes/neo-completion-portfolio/handoff/lead-11.md,
then continue advancing ALL subsequent tasks` (openspec->rasen migration done; read/write the rasen/
tree). "Continue all subsequent tasks" = lead-11's remaining: the broader Neo overhaul (the full-Neo
unimplemented-opcode + JIT-correctness surface). Full autonomy (--no-gate), commit+push after each
clean child, drive DEEP on the 1M window.

## Position
Pipeline `auto-decompose` -> child `small-feature`, Tier A. HEAD **`4f618a74`** (pushed, in sync).
NeoStep **354/0** (301 baseline + 53 new probes, all green). The 20-child neo-overhaul portfolio is
COMPLETE (runnableFrontier empty). The dominant opcode gaps are CLEARED, 6 correctness landmines
FIXED, the reflection gap + the deepest (valuetask) item CLOSED. Remaining = small LATENT follow-ups
only (none active in the smoke).

## What shipped (20 children, each propose->apply->verify->review->ship, committed+pushed)

### Correctness landmines (6) -- the highest-value work
1. **neo-jit-bogus-opcode** (`2e031b16`) -- JIT emitted `ip->Code=2359324` (garbage, not a named
   enum). Root cause: `FixBranchTargetsAfterRemove` remapped branches/Switch but NOT `Leave`/`Leave_S`;
   a Push-deletion shifted a Leave target past the body end -> ip overrun. Fix + a permanent
   ExecuteNeo dispatch guard (bounds check + corrupt-opcode check).
11. **neo-brtrue-on-reference** (`bb412dac`) -- `brtrue`/`brfalse` tested `!=0` but a null ref is a
    non-zero mStack index/-1 -> null misclassified as truthy -> lazy-init/delegate-cache skipped.
    Fix: `Brtrue_Ref`/`Brfalse_Ref` (deref `mStack[idx]!=null`) + Ldsfld registerTypes seeding + F3
    IL-static DstOffset + a load-bearing Call-case stale-ref clear.
12. **neo-ceq-null-sentinel** (`b4c5d47c`) -- the ceq FORM of the null-comparison gap (`x==null` via
    `ldsfld;ldnull;ceq;brfalse`). `Ceq_Ref`/`Beq_Ref`/`Bne_Un_Ref` (deref + C# identity). Null-
    comparison gap CLOSED (with child 11).
13. **neo-il-static-ref-field-readback** (`f61c200c`) -- DISPROVEN framing (the static arms were
    correct); real cause = newobj dest/arg register ALIAS clobbering a reference ctor arg before
    CopyNeoCallArguments. Fix: re-base the aliased ref arg (Q-NEWOBJ contract extension).
14. **neo-overhaul-eh-table-remap** (`20f11a8c`) -- child-1's EH-table landmine (sibling):
    `method.exceptionHandlerR` was NULL during LowerNeoOffsets + stale post-deletion -> a thrown
    exception in a try/catch + >3-arg-call method could miss its handler. Fix: build the EH table
    before the back-half (both funnels, idempotent) + per-deletion lockstep remap.
16. **neo-addi-on-float** (`43a74a85`) -- `float += const` was integer-added (bit-pattern). Root
    cause: Ldind_*/Ldelem_* never seeded registerTypes -> typed *_R4/R8 specialization no-oped. Fix:
    seed the 8 missing cases (free scope over subi/muli/divi/remi + add/sub/mul/div/rem).

### Active opcode gaps CLEARED
2. **neo-ldtoken** (`0b435639`) -- ldtoken (~60 hits; typeof/reflection) + fixed the broken autogen
   GetTypeFromHandle_0_Neo stub. (MAJOR found+fixed: Operand3 aliases OperandLong -> Operand4 carrier.)
3. **neo-clr-static-fields** (`222e7cd2`) -- Stsfld/Ldsfld on CLR statics (38 hits; Step-25 capstone).
4. **neo-raw-stfld-ldfld** (`99bfa851`) -- raw Stfld/Ldfld on CLR-declaring-type fields (54 hits; 3
   owner cases). Optimizer-additive.
5. **neo-bare-nie** (`8de0753b`) -- the ~38 bare-NIE hits collapsed to ONE site (GetPrimitiveSize
   lacked enum/CLR-VT branches). 1 additive fix + 5 guard-tags.
6. **neo-clr-vt-reffields-binder** (`26281c6f`) -- the 18 Step-13b NIEs were ALL RuntimeFieldHandle
   from array initializers -> a Neo RuntimeHelpers.InitializeArray redirect + Cecil-InitialValue blob
   sourcing. (RESOLVES the child-2 follow-up.)
7. **neo-misc-opcodes** (`e7dd5b3b`) -- Conv_R_Un + Switch + Unbox-of-enum + Ldsflda (batch).
8. **neo-clr-static-vt-field** (`a3bc3e27`) -- the child-3 `hasBinder` guard was a FALSE CORRELATION
   (flat-byte path never consults the binder); deleted it (kept ref-field + slot-overflow).
9. **neo-il-instance-clr-base-field** (`17ea76ef`) -- IL instance accessing a CLR-base field -> route
   via ILTypeInstance.CLRInstance + the existing Area-4d helpers (Legacy parity).
10. **neo-clr-object-field-il-path** (`76d1ad23`) -- GetNeoILInstance rework (null->NRE CLR-faithful,
    adaptor->unwrap). DISPROVEN framing (hits were null/adaptor, not CLR objects).
15. **neo-ldind-stind-byref-clr-struct** (`7c5be897`) -- ldflda on a CLR-struct-local used
    FieldInfo.GetHashCode() as a byte offset -> AV. Fix: JIT marker 0x8 + cached Marshal.OffsetOf.
17. **neo-clr-static-vt-slot-overflow** (`60e9d23e`) -- AllocateLocalStackSpaces sized eval-temps only
    for ILType -> a CLR-VT temp stayed 8 -> 12-into-8 OOB. Fix: a CLRType arm in the maxSize loop.
18. **neo-il-enum-getenumvalues** (`0bef13d8`) -- System.Enum.GetValues on an IL enum bare-NIE'd
    (ILRuntimeType overrode none of the GetEnum* virtuals). 4 overrides, sorted by UNSIGNED binary
    value. (MAJOR found+fixed: signed->unsigned-bits compare via a per-type unchecked-cast helper.)
19. **neo-raw-stfld-array-element** (`e1e7ee7c`) -- Stfld on a CLR-struct array element -> box/mutate/
    unbox via Array.GetValue/SetValue.
20. **neo-valuetask-marshal** (`4f618a74`) -- DISPROVEN-STALE (5th re-audit): the ValueTask async bug
    was ALREADY FIXED in a prior cycle (commit 9c9b795d; SetResult skips the builder's Unsafe.SizeOf,
    not a hardcoded 8). Spec-delta-only (pins the invariant). NeoStep20_VT 7/0.

### The recurring lesson (5-for-5 this session)
EVERY "framed/deepest/cluster" task was DISPROVEN on re-audit -- the actual state was already fixed
or the framing was stale (the 2026-07-10 blocked.md). Always re-audit a framed gap with a fresh
reproducer + dump before accepting it. (lead-11's "12-for-12" lesson, reaffirmed.)

## Done / Remaining
**Done:** the 20-child neo-overhaul portfolio (the broader Neo overhaul). Active opcode surface
CLEARED (post-child-19 re-scope: the dominant gaps gone; residual <=4 hits/item, all deferred-shape
residuals). NeoStep 301->354 (53 probes, all green). 6 correctness landmines fixed. The openspec->rasen
migration committed (`675c14b6`).

**Remaining (all SMALL/LATENT -- NOT active blockers; pick by interest):**
- Latent float-corruption siblings: raw `Ldfld`/`Stfld` of a CLR-struct field (child-4 escaping shape)
  + a Call returning a primitive float are unseeded in registerTypes (same class as child-16). Needs a
  child-15-style JIT marker (the untyped frame can't distinguish at runtime).
- `neo-clr-vt-refcount-stobjldobj` (child-5 F1): Stobj/Ldobj `refCount=0` for CLR structs doesn't
  consult the ValueTypeBinder -> latent missed-GC-root for ref-field CLR structs.
- `neo-il-static-field-roundtrip` (child-11): an IL-static field runtime round-trip returns garbage
  (separate field-storage gap).
- `neo-legacy-ilruntime-type-getenumvalues-dispatch` (child-18): under Legacy the ILRuntimeType
  GetEnumValues override is NOT dispatched (Legacy returns Cecil declaration order). TC4 is Neo-scoped.
- Raw `Ldfld` array-element (child-19 deferred): untyped frame can't safely distinguish flat-bytes-
  local vs array-element-byref without a JIT marker (latent silent-corruption).
- Small active residuals (<=4 hits each): "Step 17/13b CLR-object-via-IL-instance-path" (4, downstream
  of the null-owner cases), "Step-13 Area-4c CLR-array-element byref" (2), "unrecognized-VT-byref" (2).
- `neo-optimizer-dead-expr-operand4`: N/A -- the child-2 TRIVIAL-A "dead `op.Operand4 == 1`" was a
  MISREAD (it's the tail of a `hasConstrained = ... && op.Operand4 == 1` AND chain; not dead).
- Housekeeping: the git stash `child4-valuetask-blocked-partial` is OBSOLETE (do NOT pop; safe to
  drop). The 3 un-archived straggler dirs (neo-async-execctx-capture, neo-async-multi-await,
  neo-clrstruct-sm-field-layout) + the 20 neo-overhaul child dirs can be batch-archived
  (rasen/changes/archive/) -- cosmetic.

## Key decisions (and why)
- **Decompose into a serial portfolio** (not one change): the overhaul had 6+ distinct deliverables
  sharing ILIntepreter.Neo.cs/JITCompiler.cs -> strict serial (shared files). Each child: propose->
  apply->verify->review->ship, committed+pushed. Re-prioritized by CURRENT full-smoke frequency twice
  (lead-11 "proceed by frequency + re-audit").
- **LEAD-inline trivial fixes** (F-2 guard-before-deref child-1; the ceq Major unsigned-sort child-18)
  per the playbook Step E.2, then non-author re-reviewed.
- **The full Neo smoke crashes** (exit 127, a pre-existing GenericMethodTest Dict-NRE / an
  AccessViolationException-throwing EH test) -- frequency data is pre-crash only. The NeoStep smoke
  (354/0) is the authoritative gate; it is green.

## Dead ends & gotchas
- **Estimating the valuetask as "deepest/cluster"** -- it was ALREADY FIXED. Re-audit before accepting
  any "foundational/cluster" framing (5-for-5 this session).
- **`OpCodeR` 24-byte `[StructLayout(LayoutKind.Explicit)]` union** -- Operand3(@16) ALIASES
  OperandLong's high dword (child-2 MAJOR); Operand4(@20) is the only disjoint int spare. Any new
  opcode needing a spare field must verify the offset map (OpCodes/OpCode.cs:35-71).
- **`InferPrimTag`'s I4-fallback for references** is the SHARED root cause of the null-comparison
  (child-11/12) + addi-on-float (child-16) + the latent seeding siblings. Neo's UNTYPED frame relies
  on JIT-time registerTypes seeding; any unseeded reference/float producer corrupts.
- **The NeoStep pass criterion is "ran without throwing"** -- a regression probe must FAULT (a wrong
  value alone won't fail). An asymmetric `catch(NotImplementedException)` guard is sound when the
  regression throws a different exception.
- **Stale line numbers in comments** (child-19 Trivial): the Neo.cs file grew ~1000 lines this session;
  verify line refs against the actual code, not comments.

## Working set
Build/test (ALWAYS `-f net8.0`; CLI=`Debug_Neo --no-incremental`, NEVER TestCases with `Debug_Neo`):
`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` +
`dotnet build TestCases/TestCases.csproj -c Debug` +
`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> 354/0.
Commit trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`; commit via
`-F .git/cmsg.txt`; `git config lfs.useslockfiles false` before push. Subagents CAN run bare dotnet
build/run (allowlisted) + Grep/Read (NOT bash grep/tail/cd).

## Next action
The overhaul is FUNCTIONALLY COMPLETE (the planned 20 children done; the active surface cleared; the
correctness landmines fixed). The remaining items are SMALL/LATENT (none active blockers) -- pick by
interest, or declare the overhaul done. If continuing: the highest-value latent item is the float-
corruption seeding sibling (raw Ldfld/Stfld CLR-struct-field + Call-returning-float -- same fix
pattern as child-16, needs a JIT marker). The handoff/ transcriptions (lead-1..11) + this lead-12
are the full session record.
