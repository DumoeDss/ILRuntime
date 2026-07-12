# Tasks: neo-ceq-null-instance-field

## 1. Implement the `Ldfld_Ref` seeding case (the fix)

- [ ] 1.1. In `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`,
       `TypeSpecializeNeoOpcodes`, add a `case OpCodeREnum.Ldfld_Ref:` to the
       seeding switch, placed immediately after the raw `Ldfld` case (~line
       1105, after `:1089-1105`). Body:
       `if (op.Operand4 == 0) SetRegisterType(registerTypes, op.Register1, appdomain.ObjectType); break;`
       with the explanatory comment from design D2 (genuine reference field ->
       dest is an mStack index; EXCLUDE the F-10 boxed-CLR-struct case where
       `Operand4 == fieldType.GetHashCode()` and the dest is flat bytes).
- [ ] 1.2. Confirm `appdomain.ObjectType` is the right canonical reference type
       (it is what `Ldnull`/`Ldstr`/`Ldsfeld`(ref) already seed; all branch/
       compare specializations key on `IsNeoReferenceSlot` alone). No new helper.
- [ ] 1.3. Verify the seeding switch ordering: the `Ldfld_Ref` case must be in
       the same forward pass that the `Brtrue`/`Brfalse` (`:1423`) and
       `Ceq`/`Beq`/`Bne_Un` (`:933`/`:963`) cases read; since the producer
       precedes the consumer in straight-line code, the seed propagates in-pass.

## 2. Probe (promote / refine the re-audit probe)

- [ ] 2.1. The re-audit probe `TestCases/NeoStepCeqNullInstanceFieldTest.cs` is
       already in place. KEEP TC1 `(field == null) ? 1 : 0` (ternary, the
       load-bearing FAULT on HEAD) and TC4 (non-null control).
- [ ] 2.2. Verify via the JIT dump (`OUTPUT_JIT_RESULT`, `Debug_Neo`) that TC1
       STILL lowers to the direct `brfalse.s` form on HEAD (the fault evidence
       depends on Roslyn's ternary lowering; if a toolchain change lowered it
       to `ceq`, restructure the probe to force the direct branch).
- [ ] 2.3. Optionally prune TC2/TC3 (they document that the `ceq` form already
       works -- useful as controls but not required). Keeping them is harmless.
- [ ] 2.4. Optionally ADD a TC5 two-reference-field identity `d.A == d.B` (both
       `ldfld.ref`-loaded, no `ldnull`) -> closes the latent `ceq` two-field
       case for free; assert via DivideByZero-on-wrong (two distinct non-null
       instances -> `false`; same alias -> `true`).

## 3. Verify (FAULT-on-HEAD -> PASS-after)

- [ ] 3.1. HEAD proof (before applying the fix, or via stash-toggle of the
       `JITCompiler.cs` hunk ONLY): TC1 MUST FAULT (DivideByZero, inverted
       ternary). Confirm the JIT dump shows plain `brfalse.s` (not
       `brfalse.ref`).
- [ ] 3.2. Apply the fix (task 1). Re-build
       `ILRuntimeTestCLI -c Debug_Neo --no-incremental`. Re-run TC1: MUST PASS,
       and the JIT dump MUST now show `brfalse.ref` (or `brtrue.ref`).
- [ ] 3.3. Full NeoStep smoke:
       `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
       MUST be green at baseline + the new probe(s) (HEAD NeoStep baseline is
       361/0; with the 4-TC probe kept, expect 365 ran / 0 failed after the fix;
       if TC2/TC3 pruned, adjust accordingly). ZERO regressions.
- [ ] 3.4. Legacy-neutral proof: plain `Debug` + `useRegister=true` +
       `NeoStep` filter MUST show the SAME pre-existing failure set with and
       without the change (the seeding is `#if ENABLE_NEO_MODE`).

## 4. Ship

- [ ] 4.1. Code review (rasen-review): expect 0 Blocker/Major. Likely findings:
       a Minor "why `ObjectType` not the exact field type" (answer: design D2 /
       O3 -- consumers key on `IsNeoReferenceSlot` only); a Trivial comment nit.
- [ ] 4.2. Commit (Neo-gated; Legacy-neutral by construction) with the trailer
       `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
       Stage carefully (`git status` first -- the repo has noisy binary deps).
- [ ] 4.3. Ship-log + archive per the portfolio flow. Capability home:
       `neo-optimizer`.

## Notes / out-of-scope

- `Ldfld_Ref_Inline` (ref field of an in-frame VT owner) is NOT seeded by this
  case (open question O2). Seed only if a smoke pattern demands it.
- The `ceq`/`beq`/`bne.un` form of instance-field `== null` is already correct
  on HEAD (Ceq_Ref fires via ldnull); do NOT chase Ceq_Ref.
- `Stfld_Ref` is a consumer (no dest-to-branch flow); no seed needed.
- The `rasen validate` "must contain SHALL or MUST" check is a repo-wide false
  positive (it ignores the body's SHALL); `--no-gate` is the real bar.
