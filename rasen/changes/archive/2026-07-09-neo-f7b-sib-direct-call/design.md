# Design - neo-f7b-sib-direct-call (F-7B-SIB triage, REACHED)

> Planner triage. The verdict is REACHED (the gap reproduces). This document is
> the probe-construction evidence + the root-cause isolation + the fix design the
> apply stage implements (or the LEAD sequences). No source ships here.

## Context

F-7B (delegate-Invoke reference-byref write-back) shipped the caller-owned mStack
slot promotion and recorded the direct-`Call_IL` sibling as a sequencing note:
"the SAME dangling-index mechanism, but UNREACHABLE today because the Neo trivial
inliner folds small direct targets." This change triages that claim under the
dump-gate discipline (no fix without a reproducer).

## The dump-gate verdict: REACHED (reproduced on HEAD)

HEAD `243c8a73` (the F-7B-SIB planning-context HEAD; the source `ILIntepreter.Neo.cs`
was verified CLEAN -- empty `git diff --stat` -- before the probe run).

### The Neo trivial inliner's defeat conditions (cited)

`InitializeFunctionParam` (`JITCompiler.cs:2931-2975`) sets `canInline = true`
for an IL direct `Call` only when ALL hold (`:2958`):

```
!ilm.IsDelegateInvoke && !ilm.IsVirtual && !noJIT && !hasExceptionHandler
    && !ilm.Compiling && !ilm.IsEventAdd && !ilm.IsEventRemove
```

AND the body fits the size budget (`:2968`):

```
codeSizeOK = ilm.IsRegisterBodyReady
    ? ilm.BodyRegister.Length <= Optimizer.MaximalInlineInstructionCount / 2
    : def.Body.Instructions.Count <= Optimizer.MaximalInlineInstructionCount;
```

with `Optimizer.MaximalInlineInstructionCount == 20` (`Optimizer.InlineMethod.cs:17`).

So the inliner is DEFEATED by ANY of:
1. `ilm.IsVirtual` (a `virtual` target),
2. `hasExceptionHandler` (a `try/catch` body),
3. `noJIT` (blocks execution -- not usable),
4. instruction count above the threshold (`> 20` IL instructions, or `> 10`
   register body when register-ready).

### Probe 1 -- defeat via exception handler (REACHES)

`NeoStep19_SIB_DirectCall_TryCatch` calls `AppendBangBig(ref string s)` whose body
is `try { s = s + "!"; return s.Length; } catch { return -1; }`. The exception
handler sets `hasExceptionHandler = true`, so `canInline` stays `false`. Confirmed
in the JIT dump: the caller body has a REAL `call r1, r6, AppendBangBig` (NOT
inlined), and `AppendBangBig` JITs as its own body. Result: the caller observes
`s == "abc"` (NOT `"abc!"`) and `r == -1` -- the reassignment was lost and an
inner throw was caught. FAIL.

### Probe 2 -- defeat via instruction count (REACHES)

`NeoStep19_SIB_DirectCall_BigBody` calls `AppendBangLong(ref string s)` whose body
churns ~13 locals past the 20-instruction threshold, then `s = s + "!"`. Confirmed
in the JIT dump: a REAL `call r1, r6, AppendBangLong`, separate callee body.
Result: `Neo callvirt this is null` at `s.Length` INSIDE the callee -- `s`
resolved to a dangling/null slot after the reassignment. FAIL.

### Probe 3 -- the scope-discriminating primitive probe (REACHES -- BROADER gap)

`NeoStep19_SIB_DirectCall_PrimitiveRef` calls `BumpIntBig(ref int x)` (try/catch-
defeated) that does `x = x + 10`. Result: `v == 5` (unchanged; should be `15`).
FAIL. This proves the gap is NOT reference-specific: a PRIMITIVE non-inlinable
direct-call byref ALSO fails. The F-7B-SIB reference-dangling-index framing is a
SUBSET.

## The isolated root cause (the IL-direct-Call byref ABI is un-rebased/un-flagged)

A diagnostic dump (since reverted -- source verified CLEAN) in the `Call` case
(`ILIntepreter.Neo.cs:2300`) printed the `NeoCallParamMap` for the SIB calls. For
`AppendBangBig(ref string s)`:

```
entry[0] size=8 src=24 dst=0 plain [caller slot objIdx=-1 off=0]
```

Three load-bearing facts, each file:line-cited.

### Fact 1: IL-callee byref params are NOT flagged as byref

`Optimizer.Neo.cs:1275` sets `clrParams = (targetMethod is CLRMethod) ? ...ParametersCLR : null`.
For an IL callee, `clrParams == null`. The `dstIsByRefParam` gate (`:1316`) reads
`clrParams[paramLogical]`, so for an IL callee it NEVER sets `dstIsByRefParam`;
the param falls through as `plain` (`primByRef.Add(false)` at `:1342`). The
comment at `:1269-1274` documents the INTENT: "an IL callee's byref params keep
the 8-byte Ref Slot in the callee region, read by ExecuteNeo as a byref local --
the Step 17 path, byte-identical." The Step-17 cases are all INLINED, so this
intent was never exercised across a frame.

### Fact 2: the byref is copied VERBATIM (by-value), caller-relative

`CopyNeoCallArguments` (`ILIntepreter.Neo.cs:340-373`): for a `plain` slot, the
`else` at `:372` does `Unsafe.CopyBlock(targetBase + dst, frameBase + src, 8)`,
copying the caller's `(objIdx=-1, off=<caller local offset>)` Ref Slot verbatim
into the callee's param slot. The callee's param region now holds a byref whose
`off` is relative to the CALLER's frame.

### Fact 3: the callee derefs the caller-relative offset against its OWN frameBase

Inside the nested `ExecuteNeo`, the callee's `stind_*`/`ldind_*` `objIdx == -1`
arms (e.g. `Stind_I4` `:3945-3953`, `Stind_Ref` `:4017-4019`, `Ldind_Ref`
`:4052-4065`) resolve `*(T*)(frameBase + off)` where `frameBase` is the CALLEE's
frame. With `off` caller-relative, the deref lands at `calleeFrameBase + <caller
offset>` -- a WRONG cell (the callee's own param region, or beyond). The mutation
is written to the wrong frame and lost. `CopyNeoCallThisBack`'s byref loop
(`:552-594`) SKIPS this slot entirely (it only processes `PrimitiveByRefSrc`-
flagged slots), so even the flat-bytes write-back never reaches the caller.

For a REFERENCE referent, the wrong-cell write can also land a callee-frame mStack
index into a slot that the callee's `Ret` pop deletes -- the F-7B dangling-index
mechanism, reproduced here on the direct path.

## Why the delegate path is green (the pattern to mirror)

The delegate-Invoke path has `NeoRunDelegateTargetOnThis` (`:668-807`) which runs
BEFORE the nested `ExecuteNeo` and performs TWO channels (file:line-cited in the
F-7B design):

- **F-7 primitive/value-byref:** re-base the byref's OFFSET by the frame distance
  (`*(int*)(dTargetBase + slot.Offset + 4) = origOff - (int)frameDist` at `:772`)
  so the callee's `objIdx == -1` deref resolves back to the caller cell. The
  flat-bytes write survives the callee pop (no mStack index).
- **F-7B reference-byref:** PROMOTE the referent into a CALLER-owned mStack slot
  (reserved via `mStack.Add(null)` BEFORE the callee's `frameRefBase` reservation,
  `:748-752`), rewrite the byref to `(callerSlot, NeoF10ByrefOffsetFlag)` (`:763-
  764`), and the `Stind_Ref`/`Ldind_Ref` `NeoF10ByrefOffsetFlag` sub-arms
  (`:4021-4031`, `:4067-4082`) read+write the object through that stable slot.

The direct `Call` case (`:2300-2360`) has NO such pre-call rewriting -- it goes
straight `CopyNeoCallArguments` -> `InvokeNeoCallTarget` -> `CopyNeoCallThisBack`.
That is the gap.

## The fix design (REACHED -> the F-7 + F-7B promotion, ported to Call/CopyNeoCallThisBack)

Mirror the delegate path's two channels on the direct-`Call` path, PLUS the JIT
flagging. (MEDIUM scope -- larger than the planning context's SMALL assumption.)

### D1 (JIT): flag IL-callee direct-Call byref params

In `Optimizer.Neo.cs`, source the byref flag for an IL callee from the IL
`MethodDefinition.Parameters` (not `clrParams`, which is null for IL). Set
`dstIsByRefParam` / `byRefWriteBack` / `byRefElemType` for an IL-callee byref
param exactly as the CLR-callee path does (`:1316-1334`), so
`CopyNeoCallArguments` derefs and `CopyNeoCallThisBack` writes back. The dest
slot sizing for an IL byref param must match the CLR path (element-typed:
`AllocateNeoCallParamSlot(elemType)` at `:1249-1253`). RISK: the inliner's FOLDED
body for a small IL byref target must remain correct once the param is flagged --
the apply stage MUST verify the inlined Step-17 cases stay green (the flag is a
no-op for a folded call because the folded body never goes through
`CopyNeoCallArguments`, but this MUST be confirmed).

### D2 (runtime): pre-call rebase (primitive) + promotion (reference) in the Call case

In the `Call` case (`ILIntepreter.Neo.cs:2300-2360`), between
`CopyNeoCallArguments` (`:2312`) and `InvokeNeoCallTarget` (`:2349`), mirror
`NeoRunDelegateTargetOnThis`:

- For each frame-native byref param whose referent is a PRIMITIVE/value type:
  re-base the byref's offset in `targetBase` by `frameDist = targetBase -
  frameBase` (the F-7 relativization). The callee's `objIdx == -1` deref then
  resolves to the caller cell. `CopyNeoCallThisBack`'s `objIdx == -1` `Unsafe.
  CopyBlock` (`:578`) writes the flat bytes back. UNDO the rebase after the call
  is NOT needed for a single direct call (no multicast), but is harmless.
- For each frame-native byref param whose referent is a REFERENCE type: PROMOTE
  into a caller-owned mStack slot (reserved via `mStack.Add(null)` BEFORE
  `InvokeNeoCallTarget` -> the callee's `frameRefBase` sits above it), rewrite the
  byref to `(callerSlot, NeoF10ByrefOffsetFlag)`, and after the run stamp the
  caller cell to `callerSlot` (the F-7B pattern, minus the multicast-chain sharing
  -- a direct call has one invocation).

### D3: keep the existing paths UNCHANGED

The VT-`this` write-back (Area 4b), the CLR-method byref-param write-back (Area
4c), and the inlined-call path are byte-identical. The promotion is taken ONLY
for an IL-callee direct `Call` with a frame-native byref param.

## Goals / Non-Goals

**Goals:**
- `NeoStep19_SIB_DirectCall_TryCatch` / `_BigBody` (reference byref) PASS: the
  caller observes `s == "abc!"` and `r == 4`.
- `NeoStep19_SIB_DirectCall_PrimitiveRef` (primitive byref) PASSES: the caller
  observes `v == 15`.
- The inlined Step-17 byref cases (`Increment`/`AddInto`/`Produce`/...) stay
  byte-identical green.

**Non-Goals:**
- AOT (`ilrt_neoc`) wire-up (standard follow-up if the AOT smoke regresses).
- Value-type-byref reassign with reference fields (`ref struct`) -- Step 17
  territory, out of scope.
- The delegate path (F-7 / F-7B) -- ALREADY green, UNCHANGED.

## Risks / Trade-offs

- **[Risk HIGH] D1 (JIT flagging) regresses the inlined Step-17 cases.** ->
  Mitigation: the apply stage MUST run the full `NeoStep` smoke after the JIT
  change ALONE (before D2), proving the flag is a no-op for a folded call. If the
  flag leaks into the folded body, the flag must be gated to the non-inlined
  `Call` only.
- **[Risk] The dest-slot sizing change (element-typed for IL byref) changes the
  callee frame layout.** -> Mitigation: the CLR-callee path ALREADY sizes byref
  params element-typed (`:1249-1253`); making the IL-callee path match is a
  consistency fix, but the apply probe MUST cover an IL callee with a byref AND a
  non-byref param to prove the layout stays contiguous.
- **[Risk] The offset rebase for a direct Call with NO byref param is a no-op.**
  -> Mitigation: the pre-call loop iterates ONLY `PrimitiveByRefSrc`-flagged slots
  (empty for a non-byref call) -- a no-op by construction.

## Scope decision: REACHED -> fix (MEDIUM), apply/LEAD decides SHIP vs SEQUENCE

The gap reproduces and is BROADER than the F-7B-SIB framing. The fix is MEDIUM
(JIT flagging + runtime rebase + reference promotion), not the SMALL F-7B-style
promotion the planning context assumed. The LEAD decides whether to SHIP now or
SEQUENCE behind a deeper byref-ABI pass, given:
- the inliner still masks the gap for ALL current tests (no production regression
  observed -- the gap is latent until a non-inlinable IL-byref target is written),
- the fix touches the JIT flagging (HIGH regression risk for the inlined cases),
- the F-7B fix pattern (the reference promotion) is recorded and ready to port.

The planner's recommendation (advisory): the primitive half (D1 + D2 primitive
rebase) is the load-bearing correctness fix; the reference promotion (D2 reference)
is the F-7B parity. Both are needed for full parity, but the apply stage can ship
them together (they share the pre-call loop) or sequence the reference half.

## Open Questions

- Does flagging the IL-callee byref param leak into the INLINED body (the Step-17
  cases)? The apply stage's gate-0 (JIT-only build + full smoke) answers this
  BEFORE the runtime change.
- A `virtual` IL byref target (defeat condition 1) -- out of scope for the probes
  (the try/catch and size defeats suffice to REACH); a virtual probe is a
  sequencing note if the apply stage wants a third defeat-condition guard.
