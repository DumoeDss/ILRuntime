# Ship Log — neo-ldtoken (neo-overhaul child 2)

**Date:** 2026-07-11  **Pipeline:** auto-decompose -> small-feature  **Tier:** A
**Delivery:** local commit + push (user directive: commit+push after each clean child).
**Outcome:** SHIPPED. NeoStep **311/0** (306 + 4 ldtoken probes + TC5 field-path). Legacy-neutral.

## What shipped
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — new `case OpCodeREnum.Ldtoken:`
  arm: type path (`Operand==1`) resolves `AppDomain.GetType((int)OperandLong)` and pushes
  `type.ReflectionType` as an object ref; field path (`Operand==0`) mirrors `Ldsfld` (same
  `OperandLong` encoding); CLR declaring type -> tagged NIE (Legacy parity).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — `LowerNeoOffsets` Ldtoken case:
  stamps the dest ref slot into **`Operand4`** (NOT `Operand3`), calls `LowerR1`.
- `ILRuntimeTestBase/AutoGenerate/System_Type_Binding.cs` — **CORRECTION**: the autogen
  `GetTypeFromHandle_0_Neo` was a broken stub (`default(RuntimeTypeHandle)` -> null); fixed to
  read the Neo argument ref (`ReadNeoReference`) and write it straight through. (The planner's
  "GetTypeFromHandle is a no-op" claim was TRUE for Legacy, FALSE for Neo — Neo dispatch uses
  `RedirectMapNeo` exclusively, which never consults the Legacy no-op redirect.)
- `TestCases/NeoStepLdtokenTest.cs` — 5 probes (TC1 typeof(int), TC2 typeof(string),
  TC3 typeof(ILType), TC4 op_Equality consumer, TC5 array-initializer field path).

## Review-loop (1 round; author != verifier)
- **MAJOR-1 (reviewer-found, fixer-fixed, re-reviewed):** the planner + implementer both
  claimed `Operand3` (@16) is a disjoint spare. WRONG — `Operand3 [FieldOffset(16)]` ALIASES
  the high dword of `OperandLong [FieldOffset(12)]`; stamping it clobbered the field path's
  declaring type -> ~20 `GetStaticFieldOffset` NREs (array initializers). Type path was safe
  (reads the low dword). Fix = carry the dest ref slot in `Operand4 [FieldOffset(20)]` (the
  only genuinely-disjoint int spare). Independently offset-verified by the fixer AND the
  re-reviewer; precise 2-line stash-toggle reproduced (Operand3 -> NRE FAIL, Operand4 -> PASS).
- Final verdict: APPROVE-WITH-FINDINGS. 0 open Blocker/Major. MINOR-A (doc reconciliation)
  fixed inline (design.md correction banner); TRIVIAL-A (pre-existing dead `op.Operand4 == 1;`
  @Optimizer.Neo.cs:1218) recorded, out of scope.

## Evidence
- **Stash-toggle:** stash the source fixes -> all probes FAIL (`Ldtoken not yet implemented`
  Step-6 NIE); restore -> PASS. For TC5, the Operand3/Operand4 carrier toggle: Operand3 ->
  `NullReferenceException at ILType.GetStaticFieldOffset`; Operand4 -> PASS.
- **NeoStep smoke:** 311 ran, 0 failed (EXIT=0). **Legacy-neutral:** all edits inside
  `#if ENABLE_NEO_MODE`; TC5 also passes under the Legacy CLI.
- **Full smoke:** `Ldtoken` Step-6 NIE 254->0; `GetStaticFieldOffset` NRE 20->0. Net full-smoke
  failures 272->249 (a net improvement). Remaining crashes are pre-existing/unmasked (below).

## Deferred / surfaced (new portfolio follow-ups)
- **`RuntimeHelpers.InitializeArray` has no `RedirectionNeo`** -> its `RuntimeFieldHandle` param
  fails the Step-13b ValueTypeBinder marshal; C# array initializers cannot complete end-to-end
  in Neo. The 20 former field-path NREs became 20 downstream NIEs after this fix (red->red, no
  regression). Candidate future child.
- **Pre-existing Activator NRE** (`System_Activator_Binding.CreateInstance_0_Neo -> ILType.GetStaticFieldOffset`),
  unmasked (typeof no longer NIEs ~60x so reflection tests progress further). Candidate.
- **`Optimizer.Neo.cs:1218 op.Operand4 == 1;`** — dead no-assignment expression (likely intended
  `=`), pre-existing latent bug. Trivial future fix.

## Durable findings (for future planning)
- `OpCodeR` spare-field map: `Operand3`(@16-19) = `OperandLong` HIGH dword (NEVER spare when
  OperandLong is in use); `Operand4`(@20-23) is the only genuinely-disjoint int spare; the
  compaction pass at Optimizer.Neo.cs:1711-1713 remaps it consistently.
- Neo dispatch uses `RedirectMapNeo` exclusively (separate from Legacy's `RedirectMap`); autogen
  Neo CLR bindings with value-type params are often broken `default(...)` stubs — any Neo opcode
  feeding one needs the binding fixed too.
- A NeoStep regression probe must FAULT to fail (pass criterion = "ran without throwing").
