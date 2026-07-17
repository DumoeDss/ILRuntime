# Tasks — neo-clr-static-vt-field

Relax the child-3 `NeoClrVtStaticFieldIsUnsafe` guard so a blittable CLR value-type
static whose type has a registered `ValueTypeBinder` (the `TestVector3.One` shape,
11 full-smoke hits) reads/writes through the existing flat-byte box-roundtrip
instead of NIE-ing. Neo-gated; Legacy-neutral by construction. See `proposal.md`,
`design.md`, `specs/neo-optimizer/spec.md`.

Build/test contract: ALWAYS `-f net8.0`; CLI = `Debug_Neo --no-incremental`; NEVER
build `TestCases` with `Debug_Neo` (its output path is unchanged; use plain
`Debug`). NeoStep baseline after child 7 = **324/0**.

## 1. Baseline + reproducer

- [x] 1.1 Build the CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors). Build TestCases: `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] 1.2 Confirm the NeoStep baseline is green: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => 324 ran / 0 failed.
- [x] 1.3 Confirm the defect reproduces in the FULL (un-filtered) Neo smoke: the message `"Neo Ldsfld: CLR static value-type field One of type ...TestVector3 not supported under Neo"` appears (~11 hits, all `TestVector3.One`). Record the exact count for the before/after delta. [RESULT: 11 distinct throws (22 incl rethrows), all field `One`/TestVector3.]

## 2. The guard relaxation (the fix)

- [x] 2.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`, edit `NeoClrVtStaticFieldIsUnsafe(Type ft, int slotSize, bool hasBinder)` (`:265-282`): DELETE the `if (hasBinder) return true;` block (`:275-276`, plus its comment at `:269-274`). KEEP the `NeoClrStructHasRefFields(ft)` clause and the slot-overflow clause (`GetNeoValueTypeManagedSize(ft) > slotSize`) byte-for-byte. Update the helper's header comment (`:252-264`) so its stated rationale no longer claims a binder corrupts the flat-byte slot -- state that only ref-fields and slot-overflow are refused (the binder is irrelevant to the flat-byte path).
- [x] 2.2 (Optional cleanup, implementer's choice) The `hasBinder` parameter is now unused. Either drop it from the signature + the two call sites (`:4180-4181` Stsfld, `:4319-4320` Ldsfld) and their `AppDomain.ValueTypeBinders.ContainsKey(ft)` computations, OR leave the parameter (harmless). Prefer dropping it for clarity. Do NOT touch anything else in the two arms -- the flat-byte box-roundtrip behind the guard is already correct. [DONE: dropped the param + both `ValueTypeBinders.ContainsKey` computations.]
- [x] 2.3 Verify the two sound rejections still fire: trace that for a ref-field CLR struct `NeoClrStructHasRefFields` returns true (ref-field structs stay refused), and for an oversized struct the slot-overflow clause returns true. No code change needed -- just confirm by reading. [CONFIRMED by reading + the live full-smoke: ArrayTest05 (ldsfld TestVector3.One into an undersized stelement temp r4) still NIEs via the slot-overflow clause.]

## 3. Host test infra (mirrors child-3 `NeoClrStaticProbe`)

- [x] 3.1 In `ILRuntimeTestBase/TestFramework/TestClass3.cs`: add `public static TestVector3 NeoClrVtStaticProbe;` (a writable CLR value-type static whose type has a registered binder). Initialize to `default` (do NOT stomp `TestVector3.One`).
- [x] 3.2 In `ILRuntimeTestBase/TestFramework/TestClass3.cs` (`TestCLRBinding`): add `public static int HostReadNeoClrVtStaticProbe()` returning `(int)(NeoClrVtStaticProbe.X + NeoClrVtStaticProbe.Y + NeoClrVtStaticProbe.Z)` -- a plain host CLR read that isolates a broken `Stsfld` WRITE from a broken `Ldsfld` READ (the child-3 TC1 pattern).

## 4. NeoStep probes (FAULT-to-fail; names embed "NeoStep")

- [x] 4.1 Create `TestCases/NeoStepClrVtStaticFieldTest.cs` (`public class NeoStepClrVtStaticFieldTest`). Assertion mechanism: a passing test returns; a logic failure is a deliberate `1/0` (DivideByZero). Tests are `public static void`, parameterless.
- [x] 4.2 TC1 (Ldsfld read): `NeoStepClrVtStatic_TC1_LdsfldBinderVtRead` -- `TestVector3 v = TestVector3.One; int s = TestCLRBinding.SumTestVector3Fields(v, v); if (s != 6) { int z=1,d=0; int _=z/d; }`. Must FAULT on HEAD (the ldsfld NIE); pass after (X=Y=Z=1 -> 6).
- [x] 4.3 TC2 (Stsfld write + Ldsfld read-back): `NeoStepClrVtStatic_TC2_StsfldLdsfldBinderVtRoundTrip` -- `TestVector3 src = TestVector3.One; TestClass3.NeoClrVtStaticProbe = src; int h = TestCLRBinding.HostReadNeoClrVtStaticProbe(); TestVector3 rd = TestClass3.NeoClrVtStaticProbe; int s = TestCLRBinding.SumTestVector3Fields(src, rd); if (h != 3 || s != 6) { int z=1,d=0; int _=z/d; }`. Must FAULT on HEAD (the stsfld NIE); pass after.
- [x] 4.4 Rebuild TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`) and the CLI (`Debug_Neo --no-incremental`).

## 5. Verify

- [x] 5.1 Stash-toggle FAIL-on-HEAD: `git stash push -- ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (revert ONLY the guard fix; keep the new probes/infra), rebuild CLI, run the two probes by filter. Expect BOTH to FAIL with the tagged NIE. Restore the fix (`git stash pop`); confirm tree restored. [RESULT: both FAIL on HEAD "Neo Ldsfld: CLR static value-type field One ... not supported (Step-13b ref-field/binder gap or slot-size overflow)"; restored clean.]
- [x] 5.2 PASS-after: rebuild, run the two probes by filter => both PASS. [RESULT: Ran 2, 0 failed.]
- [x] 5.3 NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => **326 ran / 0 failed** (324 + TC1 + TC2). Zero regressions in NeoStep12/13/13b/17/clr-static/raw-stfld-ldfld/misc-opcodes. [RESULT: 326 ran / 0 failed.]
- [x] 5.4 Full (un-filtered) Neo smoke: drop the `NeoStep` filter; confirm the `"Neo Ldsfld: CLR static value-type field One ...TestVector3 not supported"` count goes 11 (step 1.3) -> 0. (Pre-existing unrelated full-smoke failures/segfault are out of scope and may remain.) [RESULT: 11 -> 1 distinct. The remaining 1 is `ArrayTest.ArrayTest05` (ldsfld TestVector3.One into an undersized `stelem.any` temp r4) -- it NIEs via the KEPT slot-overflow clause (the real AV guard), NOT a binder NIE. This is the desired "slot-overflow stays guarded" outcome (the planner's 11->0 expected all dests to be sized to the struct; the array-element temp is not, so it is correctly refused). Net FATAL crashes unchanged: 1 before, 1 after (the full smoke always crashes once pre-completion -- pre-existing). SURFACED FOLLOW-UP: now that ldsfld succeeds, a method that does ldsfld TestVector3.One + ldloca + ldflda + ldind.r4/stind.r4 (byref field-mutate on a CLR struct local) reaches an AccessViolation in the ldloca/ldflda/ldind/stind path -- a pre-existing latent bug in a DIFFERENT opcode site, unmasked by this fix; out of scope here.]
- [x] 5.5 Legacy-neutral spot check: `ILIntepreter.Neo.cs` is wholly `#if ENABLE_NEO_MODE`-gated; the infra fields are semantically inert for Legacy. Confirm via a plain-`Debug` + `useRegister=true` NeoStep-filter run that the Legacy failure set is unchanged (same pre-existing failures; both new probes pass under Legacy too). [RESULT: plain Debug + useRegister=true + NeoStep filter => 326 ran / 17 failed. The 17 is the pre-existing Legacy failure set (NeoStep13/14 etc.); TC1/TC2 NOT among them (both pass under Legacy). Matches child-6's 320/17 baseline + child-7 + these 2 probes.]

## 6. Ship

- [ ] 6.1 `git status` (confirm staged set; only the 3 source files + the new test file), then commit with trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Push with `git config lfs.useslockfiles false` if needed. [DEFERRED to shipper/LEAD per the implementer "Do NOT commit" constraint. Staged set confirmed: `ILIntepreter.Neo.cs` + `TestClass3.cs` + new `TestCases/NeoStepClrVtStaticFieldTest.cs` (+ pre-existing noise).]
- [ ] 6.2 Write `ship-log.md` (what shipped, the stash-toggle evidence, the 324->326 + 11->0 deltas, the F1-separate verdict). Then archive the change. [ship-log.md written by implementer; commit + archive DEFERRED to shipper/LEAD.]
