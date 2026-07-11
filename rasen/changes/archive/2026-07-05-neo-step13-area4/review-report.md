# Review Report — neo-step13-area4

**Reviewer:** adversarial, non-author. **Branch:** `features/object-model-overhaul`
(uncommitted working tree). **Date:** 2026-07-05.

**Scope reviewed:** {4b value-type-`this` direct-call, 4a Unsafe.Unbox direct-call}.
Diff = `git diff HEAD` over `ILRuntime/CLR/Method/CLRMethod.cs`,
`ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs`,
`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`,
`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`,
`ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`,
`ILRuntimeTestBase/TestFramework/TestVector3.cs`,
`TestCases/NeoStep13bTest.cs`.

**VERDICT: APPROVE.** The change is correct, well-scoped, and load-bearing. The
highest-risk surface (`CopyNeoCallArguments`, shared by every Neo call) is
byte-identical for every non-VT-`this` call shape, verified by static analysis
AND a custom adversarial probe (IL-VT instance method, the one shape NOT in the
existing smoke). All MANDATORY probes pass. Three Minor/Trivial findings below
(test gap + dead-code + stale NIE text); none block landing.

## Scope check

CLEAN. Diff = the {4b, 4a} cohort exactly (proposal's IN list). No 4c/4d leakage.
All runtime edits are in `#if ENABLE_NEO_MODE`-gated files / Neo-only paths;
Legacy is the untouched reference. The mechanism deviated from the design's
literal D2/D3 (deref-at-copy + post-call reverse copy instead of reader-side
deref) but the deviation is documented in `design.md §Apply-Phase Resolution` and
`tasks.md`, and the spec delta's observable contract is met.

## Blast-radius table — `CopyNeoCallArguments` (probe 1, HIGHEST PRIORITY)

The function runs in EVERY Neo call arm (Call, Newobj, Callvirt_IL/CLR/Interface,
Callvirt-generic). New behavior only activates when `map.PrimitiveByRefSrc[i]` is
true; that flag is set ONLY for a `HasThis && !Newobj && p==0 &&
DeclearingType.IsValueType && !IsPrimitive && !IsEnum` slot (optimizer
`Optimizer.Neo.cs:1217-1222`). All other slots take the `else` branch — the
original `Unsafe.CopyBlock`, byte-identical.

| # | Call shape | Flag fires? | Path taken | Verdict |
|---|---|---|---|---|
| 1a | CLR static method | No (`HasThis` false) | else / CopyBlock | **byte-identical** |
| 1b | CLR instance method on REFERENCE type | No (`IsValueType` false) | else / CopyBlock | **byte-identical** (probe 5.8 `NeoStep13_ClrStructInstanceMethodNoRegression` List<T>.Count green) |
| 1c | IL→IL call | No for ref-types; flag IS computed true for IL-VT instance `this`, but the byref-deref is CORRECT for IL-VT too (C# lowers `ilLocal.VTMethod()` to `ldloca; call`) | deref-then-CopyBlock for VT-this; CopyBlock otherwise | **correct** (custom probe `NeoStep12bReviewIlVtInstanceMethod` green on BOTH Neo and Legacy; full smoke 117/117) |
| 1d | CLR call, no byref param | No (`p==0` only) | else / CopyBlock | **byte-identical** |
| 1e | CLR call with byref PARAM (not this) | No (`p==0` only — byref params are `p>=1`) | else / CopyBlock | **byte-identical** (4c deferred, but flag machinery does not touch byref params) |
| — | CLR VT instance `this` (4b target) | Yes | deref-then-CopyBlock | **correct** (probes 5.1/5.3/5.9 green) |

**Key insight (probe 1c extended):** the `dstIsVtThisSlot` flag is computed
inside `if (paramInfos != null)` (`Optimizer.Neo.cs:1189`), which is shared by
the ILMethod and CLRMethod branches. For an IL-VT instance method call it DOES
fire — and empirically that is correct, because `ldloca; call` produces the same
byref source for an IL struct as for a CLR struct. I added a temporary probe
(`NeoStep12bReviewIlVtInstanceMethod`, an IL struct `Sum()` instance method) — it
passed on Neo AND Legacy, then I removed it. **The existing smoke has ZERO
IL-VT-instance-method coverage** (the NeoStep12/12b structs are field-only), so
this flag-fires-for-IL-VT path was previously unverified. It happens to be
correct, but it is a coverage gap (see Finding 1).

## Mutation-propagation table — `CopyNeoCallThisBack` (probe 2)

`CopyNeoCallThisBack` is invoked ONLY in the bare `Call` arm
(`ILIntepreter.Neo.cs:1676`). It is NOT invoked in Callvirt_IL/CLR/Interface/
generic, nor in Newobj. For non-VT-`this` calls it is a no-op (all-false or null
`PrimitiveByRefSrc`). For VT-`this` calls it copies the (possibly-mutated) dest
`this` slot bytes back to the caller's in-frame local at the byref's offset.

| # | Scenario | Reverse-copy runs? | Result | Verdict |
|---|---|---|---|---|
| 2a | `new ClrStruct(args)` ctor | No (Newobj arm, line 1715, doesn't call it) | ctor result flows via `cDef.Invoke(param)` return → `InvokeNeoClrMethod` newobj store (lines 349-359) | **correct** (probe 5.2 green; the HasThis arm is NOT entered for newobj — `isNewObj` branch at `CLRMethod.cs:361` skips it) |
| 2b | MUTATING `v.Reset()` on in-frame local | Yes | zeroed bytes propagate back to local | **correct** (probe 5.3 `NeoStep13_ClrStructInstanceMethodMutating` green; expects Sum==0) |
| 2c | READ-ONLY `v.LengthSquaredInt()` | Yes (redundant copy of unchanged bytes) | harmless no-op | **correct** (probe 5.1 green; expects 14) |
| 2d | VT with BOTH primitive + ref fields, mutated | NIE upstream (ref-field guard) | NIE | **NIE** (Finding: the 13b ref-field NIE guard blocks this; no probe asserts the BOTH-halves propagation because the path is NIE'd) |
| 2e | Leak to a DIFFERENT local (register reuse) | — | offset comes from the byref's own Ref Slot, not a reused register | **no leak** (full smoke green incl. OPT-HARDEN K1 slot-reuse probes) |

**Note on 2a:** `CopyNeoCallThisBack` is NOT on the newobj path — the F-3 ctor
reproducer (5.2) passes because the ctor's result is the method RETURN, stored by
`InvokeNeoClrMethod`'s newobj branch, NOT via the reverse copy. The implementer's
comment on `CopyNeoCallThisBack` mentions ctor propagation, but the actual ctor
path doesn't use it. The comment is slightly misleading; behavior is correct.

## Probe 3 — F-3 closure (independent confirm)

`new TestVector3NoBinding(100f,200f,300f)` end-to-end (probe 5.2) + read all 3
fields via `SumTestVector3NoBindingFields` = 600. PASS.
**Stash-toggle:** with the 5 runtime files stashed, 6 of 9 `NeoStep13_` probes
FAIL on HEAD (the F-3 `ArgumentOutOfRangeException`); 3 pass on HEAD
(K2-FAM 5.7, List<T> ref-call 5.8, boxed-mutating round-trip 5.6 — none exercise
the broken VT-`this` reflection path). **Fix is load-bearing. CONFIRMED.**

## Probe 4 — callvirt on CLR struct

`v.ToString()` on a struct compiles to `constrained.callvirt` → Step 17
`Constrained` arm NIE at `ILIntepreter.Neo.cs:3140`
(`NotImplementedException("Step 17: constrained.callvirt on a value type is
deferred ...")`). Genuinely a Step 17 (D-CONSTRAINED) follow-up, NOT a silent
misbehavior and NOT a 4b regression. **CONFIRMED.**

## Probe 5 — reflection vs autogen consistency

The 9 probes use `TestVector3NoBinding`, which is deliberately NOT in the
`RegisterValueTypeBinder` list (`helper.cs:32-40`) and only redirects STATIC
members. Therefore ALL 9 VT-`this` probes exercise the **reflection fallback**
(`CLRMethod.Invoke(byte*)` HasThis arm) ONLY. The **autogen
`GenerateMethodWraperCode_Neo` VT-`this` read code (`MethodBindingGenerator.cs`
+27 lines) is generated but NOT exercised by any smoke probe** — it would only
fire for a binder-registered pure-primitive struct's instance method, and no such
case exists in the smoke. See Finding 1.

## Probe 6 — Legacy-neutral (independent confirm)

- Legacy `NeoStep13_` (the 9 new probes): **9/9 green** (plain `Debug`).
- Legacy broader `NeoStep13` filter: 21 ran, 2 failed
  (`NeoStep13Test.NeoTestClrStructNoBindingBoxRoundTrip` + 1 other box-round-trip
  — the documented pre-existing Legacy NeoStep failures).
- **Stash-toggle:** with the 5 runtime files stashed, Legacy `NeoStep13` STILL
  shows the same 2 failures. **CONFIRMED pre-existing, NOT caused by this
  change.** The `JITCompiler.cs`/`Optimizer.Neo.cs` edits are in
  `#if ENABLE_NEO_MODE` files; the Legacy call path is byte-identical.

## Probe 7 — `WriteBackInstance` no-op

Grep confirms `WriteBackInstance` is emitted ONLY at
`MethodBindingGenerator.cs:780` and `:786`, both inside
`GenerateMethodWraperCode_Legacy` (line 424). The Neo generator
(`GenerateMethodWraperCode_Neo`, line 249) does NOT emit it. The helper is
defined in `CommonBindingGenerator.cs:131` (shared text) but only invoked by
Legacy-generated wrappers. **Neo path does not rely on `WriteBackInstance`.
CONFIRMED.**

## Probe 8 — Smoke reproduction (independent)

- Neo `NeoStep`: **117/117** (108 baseline + 9 new probes). Reproduced.
- NeoOptHardening: **16/16**. Reproduced.
- Custom IL-VT-instance-method probe: green on Neo AND Legacy (then removed).

## Findings

### Finding 1 — Minor (test gap): autogen VT-`this` path untested

**File:** `TestCases/NeoStep13bTest.cs` (the 9 probes) +
`ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs:258-281`.
**Probe:** 5. All 9 VT-`this` probes use `TestVector3NoBinding` (no binder) →
reflection fallback only. The autogen `GenerateMethodWraperCode_Neo` VT-`this`
`ReadNeoValueType` read (+ its `NeoBindingHasReferenceField` NIE guard) is
generated but never exercised. A binder-registered pure-primitive CLR struct with
an instance method, called via the autogen redirect, would close this gap.
**Why Minor not Major:** the autogen code mirrors the reflection path's
`ReadNeoValueType` read exactly (same helper, same size source), and the
reflection path IS tested. The risk is a codegen typo in the emitted string
template that the smoke cannot catch.
**Fix (optional, low cost):** add a probe that calls an instance method on a
binder-registered pure-primitive struct (e.g. add an instance method to a struct
that has a binder, or temporarily register a binder for a struct with an instance
method) so the autogen redirect is taken. Or assert via a host helper that the
autogen delegate was actually invoked.

### Finding 2 — Minor (comment accuracy / dead code): boxed branch of `CopyNeoCallArguments`

**File:** `ILIntepreter.Neo.cs:295-300` (the `else` / boxed branch of the byref
deref) and `CopyNeoCallThisBack`'s ctor mention.
**Probe:** 2a / 4. The `else` branch (objIdx >= 0, boxed-struct `this`) performs
`CopyBlock(targetBase + Dst[i], frameBase + offset, Size[i])` — but for a boxed
source `offset` is an mStack field offset, so `frameBase + offset` is not a
meaningful struct address. The comment correctly marks this "Defensive / Not
exercised in 4b" because a boxed `this` is only reachable via
`constrained.callvirt` (Step 17 NIE). So it is genuinely unreachable today.
**Why Minor:** dead-on-arrival code that would silently misbehave IF the Step 17
Constrained arm is later completed without revisiting this branch. The reverse
copy `CopyNeoCallThisBack` correctly skips the boxed case (only the `objIdx ==
-1` arm writes back). Also the `CopyNeoCallThisBack` comment claims it propagates
ctor mutations, but the ctor (newobj) path does not invoke it (Newobj arm line
1715). 
**Fix (optional):** either (a) throw a clear NIE in the boxed branch of
`CopyNeoCallArguments` (so a future Step 17 completion trips loudly instead of
silently mis-copying), or (b) leave a louder comment. Tighten the
`CopyNeoCallThisBack` comment to say it covers mutating INSTANCE METHODS (not
ctors).

### Finding 3 — Trivial (stale NIE text): Constrained NIE references old step

**File:** `ILIntepreter.Neo.cs:3141`.
**Probe:** 4. The `Constrained` NIE text says "callvirt byref-this dispatch
lands in Step 13b / a follow-up", but this change's design resolution
reclassified callvirt-on-CLR-struct as Step 17 (D-CONSTRAINED), explicitly NOT
4b. The message is now stale/misleading. 
**Fix (optional):** update the NIE string to "Step 17 D-CONSTRAINED follow-up
(constrained.callvirt on a value type)".

## Notes

- The `primByRef.Add(...)` is inside `if (dstInfo.Size > 0)` while the parallel
  `primSrc`/`primDst`/`primSize` lists are also only appended in the same block.
  All four lists are indexed in lockstep by `PrimitiveSize.Length` in
  `CopyNeoCallArguments`/`CopyNeoCallThisBack`, so the parallel invariant holds.
  No off-by-one.
- `CopyNeoCallThisBack` guards `i >= map.PrimitiveSrc.Length` (defensive, since
  `PrimitiveByRefSrc` could in principle be longer — though the optimizer always
  sizes them equal). Safe.
- The `DeclearingType.IsValueType` discriminator on `ILType` returns true for
  IL value types, so the flag fires for IL-VT instance calls too — verified
  correct by ad-hoc probe (Finding-free, but untested in the durable suite).

## STATUS: DONE

All MANDATORY probes executed. Verdict APPROVE. Three Minor/Trivial findings are
optional polish (test gap for the autogen path is the most worthwhile to close);
none block landing. Working tree UNCOMMITTED.
