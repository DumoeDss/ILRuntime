## 1. Apply-phase current-state probes (BEFORE any fix)

- [ ] 1.1 Build CLI `Debug_Neo` (`--no-incremental` after any host-type add) + TestCases `Debug`; confirm NeoStep smoke 161/161 baseline on HEAD `2ca6614f`.
- [ ] 1.2 4c reflection probe: a CLR method `void Bump(ref int v){ v += 10; }` called from IL on a local int — confirm silent-wrong (reads the Ref Slot `objectIndex` half as the int, no write-back) on HEAD via a temp `Console.WriteLine` in `CLRMethod.Invoke`'s param loop (dump-noise gotcha: gate the diagnostic inside the byref arm only).
- [ ] 1.3 4c autogen probe: a CLR method with a `ref int` param on a binder-registered type (so the autogen `*_Neo` delegate fires, not the reflection fallback) — confirm the emitted wrapper hits the `pt.IsByRef` dead branch (`:173`) OR the `p.IsByRef` path; dump the emitted C# via `OUTPUT_JIT_RESULT` to confirm which discriminator the fix must key on (D2 load-bearing).
- [ ] 1.4 4d probe: an IL method `ldflda clrObj.intField; stind_i4` on a CLR object — confirm the clean `GetNeoILInstance` NIE ("CLR field-hash plumbing lands in Step 13b") on HEAD; stash-toggle to prove pre-existing.
- [ ] 1.5 4d JIT-dump probe (OQ1): dump the `Ldflda` operand for `ldflda clrObj.field` — confirm whether the CLR `FieldInfo` is resolvable at JIT time (D4 Option A viable) or only an IL-side `IField` wrapper (Option B fallback). Pin the dump before designing the field-identity stamp.

## 2. 4c CLR-method ref/out typed-ref bridge (readers)

- [ ] 2.1 `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:407`): add a `pt.IsByRef` branch BEFORE the existing primitive/struct/ref dispatch — read the 8-byte Ref Slot `(objectIndex, offset)`, deref-read the referent (frame-native: cursor at `offset`; mStack-object: route to the shared field-accessor primitive from task 3.1), box CLR structs via `ReadNeoValueType`, pass the value to `param[i]`.
- [ ] 2.2 `AppendArgumentCodeNeo` (`BindingGeneratorExtensions.cs:135-237`): replace the dead `pt.IsByRef` arm (`:173-177`) with a `p.IsByRef`-keyed (D2) byref-read codegen — read the Ref Slot, deref to the referent value (boxed for a CLR struct), assign to the param local.
- [ ] 2.3 Confirm both readers produce the SAME dereffed value for the SAME callee param region (byte-consistency, the 13b discipline) — the autogen wrapper's emitted C# must read exactly what the reflection fallback reads.

## 3. 4c write-back epilogue + shared field-accessor primitive

- [ ] 3.1 Factor a shared field-accessor primitive (D3 unification): a `ReadNeoFieldRef`/`WriteNeoFieldRef` pair (or a `MarshalNeoByrefArg` helper) that, given a Ref Slot `(objectIndex, offset)` and a type, reads/writes the referent — frame-native via the cursor; mStack-object via the field identity (4d's resolver). Used by BOTH 4c's byref-marshal and 4d's stind/ldind consumer.
- [ ] 3.2 Reflection fallback write-back (D5 + D6): after `def.Invoke`/`cDef.Invoke`, for each `ref`/`out` param (gate on `!IsIn || IsOut`), write the (possibly-mutated) CLR value back through the SAME Ref Slot — primitive via `WriteNeoInt32`-at-offset; CLR struct via `WriteNeoValueType` (re-flatten the mutated boxed struct); reference type via the field-accessor's write arm.
- [ ] 3.3 Autogen write-back epilogue: `AppendNeoWriteBackCode` (or inline in `GenerateMethodWraperCode_Neo` at `MethodBindingGenerator.cs:249-310`) emits, for each `ref`/`out` param, a post-call write-back to the Ref Slot mirroring the reflection path.

## 4. 4d CLR-object stind/ldind via field identity

- [ ] 4.1 `Ldflda` arm (`ILIntepreter.Neo.cs:877-883`): for a CLR-object operand (marker absent, `mStack[objIdx]` not ILTypeInstance/Array), stamp the CLR field identity (Option A: resolve `FieldInfo` at JIT + stamp `MetadataToken`/cache-index; Option B per OQ1) into the Ref Slot offset half — NOT `field.PrimitiveOffset`. IL heap-field stamp unchanged.
- [ ] 4.2 stind/ldind consumer `else` branch (`ILIntepreter.Neo.cs:3142-3407`, all width arms incl `Stind_Ref`/`Ldind_Ref`/`Stobj`/`Ldobj`): replace the `GetNeoILInstance` NIE throw with — if the offset encodes a CLR field identity, resolve it -> `fieldInfo.GetValue(obj)` / `fieldInfo.SetValue(obj, v)` (with the right width cast per arm); else throw the existing NIE (unreachable for genuine IL heap fields). Discriminator additive (frame-native + Array + ILTypeInstance paths byte-identical).
- [ ] 4.3 Confirm the stind/ldind width matrix (I1/I2/I4/I8/R4/R8/Ref + Stobj/Ldobj) all route the CLR-object-field case correctly (the array-completion width matrix is the template).

## 5. F-7 delegate ref/out (stretch — OQ2)

- [ ] 5.1 After 3.1's shared helper lands, assess `DelegateAdapter.NeoInvokeSub` / `WriteNeoCallSlot`: can the byref-typed delegate `Invoke` param route through the same helper? If YES and < ~50 lines, add it (F-7 closes here). If NO or larger, DEFER — record F-7 as STILL OPEN in deferred-items + planning-context with a pointer to the helper.

## 6. Adversarial probes (TestCases)

- [ ] 6.1 4c `NeoStep13_*` probes in `NeoStep13bTest.cs`: `ref int` mutation propagates; `out int` assigns; `ref struct` (pure-primitive binder, e.g. TestVector3) mutation propagates; `out string` (reference type) assigns; `ref` then read-after-intervening-heap-writes; multiple byref params in one call; `in`-only param NOT written back; non-byref CLR method regression (byte-identical).
- [ ] 6.2 4c reflection-vs-autogen split: at least one `ref int` probe on a binder-registered type (autogen path) AND one on a no-binder type (reflection fallback) — both must pass.
- [ ] 6.3 4c NIE probe: a CLR value type WITH reference fields and no binder passed `ref`/`out` -> tagged NIE.
- [ ] 6.4 4d `NeoStep17_*` probes in `NeoStep17Test.cs`: `ldflda clrObj.intField; stind_i4` write; `ldflda; ldind_i4` read; a CLR object with primitive + reference-type fields (`stind_ref`/`ldind_ref` on the CLR ref field); read-after-write roundtrip; a CLR-object-field `ref` passed to a 4c CLR method (4c+4d interaction).
- [ ] 6.5 Regression: the frame-native + ILTypeInstance + CLR-array stind/ldind paths still work (the step17-completion + array-completion + 4b byref-`this` probes) — re-run the full NeoStep smoke.

## 7. Verify + review-loop + ship

- [ ] 7.1 Verify: full NeoStep smoke green (161/161 baseline + new probes); NeoOptHard unchanged; Legacy-neutral (plain `Debug` + `useRegister=true`, the 518/519 baseline; the `*Neo` variants compile out under plain Debug). Each new probe FAIL-on-HEAD stash-toggle -> PASS-after.
- [ ] 7.2 Review-loop: non-author review; adversarial probe for the D2 dead-discriminator codegen bug, the D4 field-identity collision, and the D6 reflection `ref int` boxing (OQ3). Fix findings; re-review.
- [ ] 7.3 Ship: write `ship-log.md`; LEAD commits (`Neo step 13 area4 ref/stind:`) + pushes.
- [ ] 7.4 Archive: sync the `neo-byref` MODIFIED/ADDED deltas into `openspec/specs/neo-byref/spec.md`; move the change to `archive/`; update `neo-deferred-items.md` D-13B (4c+4d resolved) + F-7 (resolved-or-still-open per 5.1) + the master table; update `neo-boxing`'s deferral sentence to note the byref CLR crossing + CLR-object stind/ldind are now closed.
- [ ] 7.5 Append durable findings to `openspec/changes/neo-completion-portfolio/planning-context.md` under `## Findings -- neo-step13-area4-refandstind`: the scoping decision (4c+4d shipped, F-7 deferred-or-included per OQ2), the current-state assessment (4c silent-wrong in both readers; 4d clean NIE), the D2 dead-discriminator codegen bug, the D3/D4 unified field-accessor primitive, and the F-7 routing.
