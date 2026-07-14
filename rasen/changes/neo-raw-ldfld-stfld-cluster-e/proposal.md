# Proposal: neo-raw-ldfld-stfld-cluster-e

Wave-2 child of `neo-overhaul`. Targets the cluster-E raw ldfld/stfld
failures from the fresh-60 full-smoke grounding (Neo build, `ExecuteNeo`).

## Problem
At the 51-failure full Neo smoke, 9-10 tests fail inside the typed
`Ldfld_*` / `Stfld_*` heap arms (`ILIntepreter.Neo.cs:4533/4549/4794/4810/
4818/4839`) via `GetNeoILInstance`, because the owner slot does NOT hold a
heap `ILTypeInstance`:

- `ldfld.i4`/`ldfld.r4`/`stfld.i4`/`stfld.r4` reach the heap arm with an
  owner that is actually an IN-FRAME IL value type (flat managed bytes).
  The typed arm reads the owner slot's first int as an mStack index and
  `GetNeoILInstance` throws NRE / NIE.

Root: these typed opcodes SHOULD have been rewritten to their `_Inline`
counterparts by `TypeSpecializeNeoOpcodes.TryRewriteFieldAccessForInline`
(which keys on `registerTypes[ownerReg]` being an IL value type), but the
owner register was never seeded as an IL-VT because the PRODUCER of the
in-frame VT value has no seeding case. Same defect class as child-16/21/23
(unseeded in-frame-VT / reference producer).

Two producers are missing a seeding case:
1. `Ldarga`/`Ldarga_S` (address of a struct PARAMETER) -- `Ldloca` already
   seeds (JITCompiler.cs:1284), `Ldarga` does not. Fixes `UnitTest_1008`
   (`ValueTest(TestStruc a){ a.a = 3; }` -> `ldarga;stfld.i4`) and
   `UnitTest_10022` (`tttt(Vector3 a){ a.x = ...; }`).
2. `Ldfld_Value` (whole-IL-VT field load) -- its dest register is an
   in-frame VT but is never seeded, so a following typed `Ldfld_*` on that
   dest is not rewritten. Fixes `UnitTest_10023` (`a.C.x` where `a.C` is a
   Vector3 loaded via `ldfld.value`).

## Scope decision (largest tractable sub-cluster)
Cluster E is multi-rooted. The largest SINGLE root is the delegate group
(3 tests: `DelegateExtTest01/02`, `DelegateTest01`) but those fail with
"Owner type: System.Int32" -- a delegate `this`/argument-marshalling bug
(Step 19 delegate scope), NOT a field-access-encoding bug. The largest
TRACTABLE raw-ldfld/stfld sub-cluster is the in-frame-VT-owner seeding
group above (3 tests: 1008, 10022, 10023), one defect class, two small
additive seeding cases.

The remaining cluster-E tests (Generics/Generics2 heap-instance NRE,
TestStructDictionary callvirt.clr-return owner, RegisterVMTest04 stfld.ref
ArgOOB, the delegate group) are reported as separate roots, not fixed here.

## Approach
Add two seeding cases to `TypeSpecializeNeoOpcodes` (JITCompiler.cs),
mirroring the existing `Ldloca` case. Neo-gated -> Legacy-neutral by
construction. No new opcode, no runtime change, no object-model change.

## Verify
- Stash-toggle: each of the 3 targets FAULT on HEAD (NRE) -> PASS after.
- Full smoke delta: 51 -> lower (expect 51 -> 48).
- NeoStep 0-failures (no regression).
