# Proposal: neo-recluster-21 (Wave-2 child of neo-overhaul)

## Mandate
Fresh-ground the CURRENT 21 full-Neo-smoke failures (down from 189 via 42 wave-2
children), re-cluster them, and fix the LARGEST sub-cluster or batch the most
tractable. Success = full-smoke count dropping (21 -> lower), verified by re-
running the full smoke.

## Outcome (honest)
- **Fresh re-cluster: DONE.** `Ran 935 tests, 21 failded` (Debug_Neo, same
  TestCases.dll + HotfixAOT.patch). Full per-test cluster table + pinned roots at
  `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-21.md`. NeoStep 401/0.
  Legacy (plain Debug) build clean.
- **Count did NOT drop (21 -> 21).** The 21 are deeply fragmented: 12 are a
  throw@7098 grab-bag of DISTINCT value-corruption roots; the rest are deep
  singletons (autogen-stub marshal, collection-struct field access, reflection,
  byref out-STRUCT, ExpectException mechanism). No shared multi-test root with a
  mechanical fix exists in the current 21.
- **The most tractable candidate (initobj-on-a-reference-type-through-a-byref,
  UnitTest_NestedGenericRefOut) was JIT-dump-pinned and a fix attempted THREE
  ways; all aborted as net-negative or non-functional.** The root is real but the
  fix is blocked by the optimizer's unreliable alias/localIsRef state for
  reference locals (see design.md). Reverted to clean HEAD; full smoke re-
  verified at 21; NeoStep 401/0; Legacy-neutral by construction.

## Why no fix shipped
The initobj-byref gap is the single most tractable pinned root in the 21, but
three fix approaches all failed (details in design.md):
1. **Runtime-only deref** (mirror Stobj/Stind_Ref): fixes the target but is
   ambiguous with an alias-folded reference TEMP (Activator's `default` temp),
   regressing `ActivatorCreateInstanceWithArgsTest` + `InheritanceTest20`
   (21 -> 22). Net negative.
2. **JIT marker gated on `ResolveLiveAlias(r1).Reg == r1`:** the static addrAlias
   has stale register-reuse entries, so the discriminator mis-fires.
3. **JIT marker gated on `liveAliasMap.ContainsKey(r1)`:** the per-instruction
   map is absent for the Activator ref-temp case (mis-marked).
4. **JIT-only forward-walk map (refLocalByrefSource) consulting initobj:**
   `localIsRef[loc's register]` is FALSE (a process-static diagnostic showed
   `prevIsRef(r1=4)=False` for `loc`), and the static addrAlias resolves the
   operand to a STALE reuse register (4) -- so the ref-local guard never fired.

The correct fix is a JIT-level producer-chain byref resolution that does NOT
depend on the unreliable alias/localIsRef state (see design.md "Recommended
fix"). That is a larger change than this session verified cleanly.

## What DID ship (durable)
- `rasen/changes/neo-overhaul/handoff/fullsmoke-ground-21.md` -- the FRESH 21
  cluster table with every test pinned to its JIT opcode + Neo.cs frame +
  test-internal throwing site, the largest-sub-cluster analysis, and the
  recommended next-batch priority. This is the durable re-cluster deliverable.
