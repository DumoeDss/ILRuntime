## 1. JIT-dump probe (D2/D3 dump-gate -- probe BEFORE fixing)

- [x] 1.1 Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`) + TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`) -- confirm 0 errors, confirm DLL mtime > source mtime (the opt-harden-2 stale-DLL gotcha).
- [x] 1.2 Confirm the baseline: run the FULL `NeoStep` smoke (`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`) -- confirm 108/108 green at HEAD.
- [x] 1.3 Write a temporary probe `NeoStep13_ClrStructInstanceMethodProbe` in `TestCases/NeoStep13bTest.cs`: declare a CLR struct local (`TestVector3NoBinding`), construct it via `new TestVector3NoBinding(1f,2f,3f)`, call an instance method that returns a primitive (e.g. a host helper `LengthSquared` taking the struct by `this`). Confirm it FAILS on HEAD (the F-3 `ArgumentOutOfRangeException` at `CLRMethod.Invoke`).
- [x] 1.4 Dump-gate the `this`-slot shape: add a temporary `Console.WriteLine` inside the `CLRMethod.Invoke` `HasThis` arm (`CLRMethod.cs:351-356`) printing `DeclearingType`, the first 8 bytes at `targetBase+curPrim`, and the call-lowering's `paramInfos[0].Size` (read from a JIT dump of `localInfos` via the `OUTPUT_JIT_RESULT` macro). Determine: is the value-type `this` slot sized as the 8-byte Ref Slot (byref) or as flat bytes (`GetNeoValueTypeManagedSize`)? Record the answer for D2/D3.
- [x] 1.5 Dump-gate the boxed-`this` shape: write a second probe that boxes a CLR struct then calls an instance method on the box (via a host helper dispatching through `object`). Confirm the boxed-`this` slot is a 4-byte mStack index (the reference-type call-lowering path). Record for D3/D4.
- [x] 1.6 Remove the temporary `Console.WriteLine` diagnostics. (Do NOT commit them.)

**DUMP-GATE RESULT (2026-07-05):** The C# compiler lowers `local.VTInstanceMethod()`
and `new VT(args)` to `ldloca; call` -- the `this` argument is a frame-native byref =
an 8-byte Ref Slot `(-1, structFrameOffset)`. The dump (`HasThis` arm) showed bytes
`0xFFFFFFFF 0x0 ...` = `objectIndex=-1, offset=<struct frame off>` for BOTH the
instance-method case and the F-3 ctor case. The call-lowering
(`Optimizer.Neo.cs:1170-1171`) sizes the VT instance `this` slot via
`AllocateNeoCallParamSlot(DeclearingType)` which hits the `IsValueType` branch -> flat
bytes (12 for TestVector3NoBinding). So the dest slot is the struct's flat-byte width
(12), but the SOURCE register holds an 8-byte byref (from `ldloca`); copying 12 bytes
from the 8-byte byref temp reads 4 bytes of garbage and then the start of the struct.

**THE FIX (deviated from the design's literal D2/D3 -- see section 2 note):** the
copy site (`CopyNeoCallArguments`) DEREFERENCES the byref and lays the struct's flat
bytes into the dest slot (flagged via a new `NeoCallParamMap.PrimitiveByRefSrc`). Both
readers then read flat bytes via `ReadNeoValueType` exactly like a by-value VT param
(no `frameBase` needed). A post-call reverse copy (`CopyNeoCallThisBack`) propagates
ctor / mutating-method mutations back to the caller's in-frame local. The boxed-`this`
case (`objectIndex >= 0`) is only reachable via `constrained.callvirt` (Step 17 NIE
today), NOT via a direct `call` (which always uses `ldloca`); the autogen 4a boxed
re-box lands with Step 17 completion.

## 2. 4b runtime: `CLRMethod.Invoke(byte*)` value-type `this` read

- [x] 2.1 In `ILRuntime/CLR/Method/CLRMethod.cs` `Invoke(byte*)` `HasThis` arm (`:351-356`): add the value-type discriminator (D1). When `DeclearingType` is a CLR value type (non-primitive, non-enum), read the `this` via the flat-bytes path (deref-at-copy), using `ReadNeoValueType`. Keep the reference-type 4-byte mStack-index read byte-identical (UNCHANGED for `!IsValueType`).
- [x] 2.2 Add the NIE guards for the value-type `this` with reference fields and no binder (mirror the 13b `NeoClrStructHasReferenceField` guard) and for the binder-with-ref-fields case (mirror the 13b reflection-fallback guard). Reuse the existing helpers -- do NOT duplicate.
- [x] 2.3 Cursor-advance: advance `curPrim` by `GetNeoValueTypeManagedSize` (the dump-confirmed dest width = the struct's flat-byte size). Comment documents the shape.
- [x] 2.4 Confirm the constructor reflection-fallback path (`cDef.Invoke(instance, param)`) works for the byref-`this` constructor case (`new ClrStruct(args)` lowering to `initobj; ldloca; call .ctor`). The reflection reader boxes the struct off the param-region flat bytes, `cDef.Invoke` mutates the box in place (CLR verified), and the reader writes the mutated box back to the param region; the post-call `CopyNeoCallThisBack` reverse copy propagates it to the caller's local. Probe with the F-3 reproducer.

**DEVIATION FROM DESIGN (resolved at apply).** The design's D2/D3 assumed the reader
dereferences the byref, but BOTH readers (`CLRMethod.Invoke(byte*)` and the autogen
wrapper) lack the real caller `frameBase` (the autogen delegate passes `targetBase` as
`__frameBase`; the reflection `Invoke(byte*)` only gets `targetBase`). Threading
`frameBase` through the delegate signature would break the checked-in static bindings.
The fix DEREFERENCES THE BYREF AT THE COPY SITE instead, so the param region's `this`
slot holds flat bytes -- both readers read it like a by-value VT param (no `frameBase`
needed). The reflection path additionally writes `instance` back to the param region
after the invoke, and a new `CopyNeoCallThisBack` post-call reverse copy (in the Neo
Call arm) propagates ctor / mutating-method mutations to the caller's in-frame local
(matching CLR `ref this` struct semantics; verified `ConstructorInfo.Invoke` /
`MethodInfo.Invoke` mutate the boxed struct in place).

## 3. 4b autogen: `GenerateMethodWraperCode_Neo` value-type `this` prologue

- [x] 3.1 Replace the `// TODO: ValueType instance in Neo` with a value-type `this` read via `ReadNeoValueType` (flat bytes), mirroring the 13b by-value param read.
- [x] 3.2 Add the NIE guard for the value-type `this` with reference fields (mirror `NeoBindingHasReferenceField` from `BindingGeneratorExtensions.cs`). Reuse the existing helper.
- [x] 3.3 Cursor-advance: `ReadNeoValueType` advances `__curPrim` by the struct's flat-byte size (its internal behavior). Documented the shape in a comment.
- [x] 3.4 (Apply-phase decision) Inlined into `GenerateMethodWraperCode_Neo` (the minimal form, mirroring how the reference-type `this` is inlined). No `AppendThisCodeNeo` helper needed.

**APPLY DECISION (3.1):** The wrapper reads the `this` as FLAT BYTES via
`ReadNeoValueType` (inlined). The design's D3 byref/box DISCRIMINATOR (`__thisObjIdx`
branch) is NOT emitted because the deref happens at the copy site (the param region
holds flat bytes for both byref and -- future -- boxed sources). This is the minimal,
dump-confirmed form. The NIE guard mirrors `AppendArgumentCodeNeo`.

## 4. 4a autogen: boxed-direct-call write-back (D4)

- [x] 4.1 (Apply decision) The D4 boxed-re-box epilogue is NOT emitted in the autogen wrapper -- the boxed-`this` case is ONLY reachable via `constrained.callvirt` (the Step 17 `Constrained` arm, a NIE today), never via a direct `call`. The dump-gate confirmed no direct-call path produces a boxed `this` (`objectIndex >= 0`). The 4a write-back IS implemented for the reflection path via the post-call `CopyNeoCallThisBack` reverse copy. The autogen boxed-re-box lands with the Step 17 `Constrained` completion child.
- [x] 4.2 Confirmed the D4 semantics with `NeoStep13_BoxedClrStructMutatingWriteBack`: the box retains its original value (CLR boxed-VT-call-drops-mutation semantics for the reflection fallback). Probe 5.5 verifies the non-mutating boxed read.
- [x] 4.3 Documented the `WriteBackInstance` no-op: the Neo generator does NOT emit `WriteBackInstance` (only `GenerateMethodWraperCode_Legacy` at `:780/:786` does). The Neo value-type-`this` write-back is the post-call `CopyNeoCallThisBack` reverse copy (reflection path) / boxed-re-box (autogen, deferred to Step 17).

## 5. Adversarial probes (MANDATORY -- Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1 lessons)

- [x] 5.1 `NeoStep13_ClrStructInstanceMethodOnLocal` -- the core 4b probe: non-mutating instance method on an in-frame local.
- [x] 5.2 `NeoStep13_NewClrStructEndToEnd` -- the F-3 reproducer: `new TestVector3NoBinding(100f,200f,300f)` end-to-end.
- [x] 5.3 `NeoStep13_ClrStructInstanceMethodMutating` -- MUTATING instance method (`Reset()`); verifies the byref-`this` copy-back lands in the local.
- [x] 5.4 `NeoStep13_ClrStructCallvirt` -- REMOVED. The C# compiler emits `constrained.callvirt` for `v.ToString()`, which lands in the Step 17 `Constrained` NIE -- a Step 17 completion follow-up, NOT a 4b regression. A standalone probe is either Neo-specific (NIE assertion fails on Legacy) or too weak (accept-both). Boundary documented in design.md Risk 3.
- [x] 5.5 `NeoStep13_BoxedClrStructMethodCall` -- 4a boxed non-mutating read.
- [x] 5.6 `NeoStep13_BoxedClrStructMutatingWriteBack` -- 4a boxed round-trip (CLR boxed-VT-call-drops-mutation semantics).
- [x] 5.7 `NeoStep13_K2FamRegression` -- K2-FAM reproducer (struct by-value param).
- [x] 5.8 `NeoStep13_ClrStructInstanceMethodNoRegression` -- reference-type CLR instance call (List<T>).
- [x] 5.9 (added) `NeoStep13_ClrStructReturnThenInstanceCall` -- return -> local -> instance-call chain.
- [x] 5.10 (added) `NeoStep13_ClrStructWithRefFieldNIE` -- NIE guard for a struct `this` with a reference field and no binder.

## 6. Verify (full NeoStep smoke + Legacy-neutral)

- [x] 6.1 Build the CLI (`Debug_Neo`) + TestCases (`Debug`, `--no-incremental`). 0 errors.
- [x] 6.2 Run the FULL `NeoStep` smoke (filter `NeoStep`). 117/117 green (108 baseline + 9 new probes; 5.4 removed).
- [x] 6.3 Stash-toggle: with the runtime fixes stashed, 6 of 9 probes FAIL on HEAD with the F-3 `ArgumentOutOfRangeException`; the other 3 (K2-FAM, List<T> ref-call, boxed-mutating round-trip) pass on HEAD (don't exercise the broken VT-`this` path). Proves load-bearing.
- [x] 6.4 Legacy-neutral: plain `Debug` NeoStep13_ probes 9/9 green. The 7 pre-existing Legacy NeoStep failures (NeoStep13 ClrStruct box-round-trip x2, NeoStep14 TC1/TC5/TC8, NeoStep15 TC6, NeoStep6 NeoNaNR8) reproduce with the runtime fixes stashed -- NOT caused by this change.
- [x] 6.5 `NeoOptHardening` filter: 16/16 green (F-MAJ-1 probes unaffected).

## 7. Spec + deferred-items doc sync

- [x] 7.1 Update `.trae/documents/neo-deferred-items.md`. **(DONE BY SHIPPER 2026-07-05: F-3 RESOLVED prepend + D-13B Area 4b/4a done + F-5/M2 folded into D-CONSTRAINED §3 entry.)**
- [ ] 7.2 Update `.trae/documents/neo-handoff.md`. **(NOT REQUIRED for this ship task -- the LEAD updates the handoff at commit time; deferred-items doc is the authoritative tracker and is current.)**
- [x] 7.3 Append the apply-phase findings to `openspec/changes/neo-completion-portfolio/planning-context.md` under `## Findings -- neo-step13-area4 (apply)`.
- [x] 7.4 Confirm the spec delta (`specs/neo-boxing/spec.md`) matches what shipped (archive-time sync into `openspec/specs/neo-boxing/spec.md` is the shipper's job).

**SPEC DELTA CONFIRMATION (7.4):** The `specs/neo-boxing/spec.md` delta's two ADDED
requirements (value-type instance `this` reading + boxed direct-call write-back) and
the MODIFIED "Out-of-scope deferrals" (4a/4b moved out of deferred) match what
shipped. The implementation's mechanism (deref-at-copy + post-call reverse copy)
differs from the design's literal D2/D3 (byref-through + reader deref) but delivers
the SAME observable contract (the spec's scenarios pass). The callvirt scenario is
downgraded to a Step 17 follow-up (the C# compiler emits `constrained.callvirt`,
which is the Step 17 NIE -- documented in design.md Risk 3).
