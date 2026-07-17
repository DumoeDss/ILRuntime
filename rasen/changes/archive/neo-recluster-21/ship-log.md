# Ship Log: neo-recluster-21

**Branch:** `features/object-model-overhaul`
**Child of:** `neo-overhaul` (Wave-2)
**Outcome:** RE-CLUSTER ONLY. No runtime fix shipped (the tractable candidate is
pinned but blocked by unreliable optimizer state; see below). Codebase reverted
to clean HEAD.

## Deliverable
`rasen/changes/neo-overhaul/handoff/fullsmoke-ground-21.md` -- the FRESH 21
cluster table (every test -> JIT opcode + Neo.cs frame + test site + pinned
root), largest-sub-cluster analysis, and recommended next-batch priority.

## Verified numbers (truth = full-smoke)
- **Full Neo smoke: `Ran 935 tests, 21 failded`** (Debug_Neo, same TestCases.dll
  + HotfixAOT.patch). Confirmed at the start (fresh ground) AND after all reverts
  (clean HEAD). No regression.
- **NeoStep smoke: 401/0** (baseline; codebase is byte-identical to HEAD, git
  diff empty for ILRuntime/ + TestCases/).
- **Legacy (plain Debug) build: 0 errors.**

## The fix that did NOT ship (durable lesson)
The initobj-on-a-reference-type-through-a-byref gap (UnitTest_NestedGenericRefOut)
is JIT-dump-pinned: `result = default(T)` on a ref T -> `initobj T` on a byref;
the optimizer excludes reference locals from addr-alias folding, so the initobj
DstOffset points at the byref VALUE temp, and the reference-type arm's direct
`-1` write nulled the temp, not the target.

FOUR fix approaches were attempted and all aborted:
1. Runtime-only deref -> ambiguous with an alias-folded ref TEMP (Activator's
   `default` temp) -> 21 -> 22 (regression). REVERTED.
2. JIT marker on `ResolveLiveAlias(r1).Reg == r1` -> static addrAlias stale
   register-reuse entries mis-fire. REVERTED.
3. JIT marker on `liveAliasMap.ContainsKey` -> mis-fires for Activator ref-temp.
   REVERTED.
4. JIT-only refLocalByrefSource map -> `localIsRef` does not mark the reference
   local's register (diagnostic: `prevIsRef(r1=4)=False`; also register 4 is a
   STALE reuse alias, not the real source). REVERTED.

**Durable lesson:** the optimizer's alias/localIsRef machinery is UNRELIABLE for
reference locals (they are deliberately excluded from folding, the static
addrAlias has stale register-reuse entries, and `localIsRef` does not mark the
registers that hold reference locals reached via the alias chain). Any fix that
depends on resolving "is this initobj operand a byref to a declared reference
local?" through the existing alias/localIsRef state will fail. The correct fix
(Option A in design.md) is a CIL-producer scan at JIT emission using the stable
`Instruction.Previous` links, sidestepping the register-VM alias machinery
entirely.

## Files touched (all reverted; none committed)
None. `git diff` clean for ILRuntime/ + TestCases/. (LEAD commits; this child
made no source change.)

## Remaining (the 21)
See `fullsmoke-ground-21.md` for the full table. Highest-value next children:
1. neo-initobj-ref-byref (Option A, CIL-producer scan) -- 1 test, correctness.
2. typeof(generic-param) resolution (TestGenericMethod2) -- likely broader.
3. reference-arg aliasing on a plain Call (UnitTest_TestInline01) -- PINNED,
   mirrors child-13's newobj fix.
