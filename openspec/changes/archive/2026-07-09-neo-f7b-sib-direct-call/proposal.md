# Proposal - neo-f7b-sib-direct-call (F-7B-SIB triage)

## Why

F-7B (the delegate-Invoke reference-byref write-back, `archive/2026-07-09-neo-f7b-
reftype-writeback`) shipped the caller-owned mStack slot promotion for the
DELEGATE path and recorded a follow-up: the direct `Call_IL` /
`CopyNeoCallThisBack` sibling has the SAME dangling-index mechanism, but was
believed UNREACHABLE today because the Neo trivial inliner folds small direct
targets (so a cross-frame byref never crosses a frame).

This change TRIAGES that sibling. The dump-gate discipline (the Q-STRUCT / Q-LONG
/ F-11 / F-13 lesson) forbids fixing a gap without a REPRODUCER, so the first
job is to CONSTRUCT a non-inlinable direct-call probe and either REACH + fix the
gap, or CLOSE it as unreachable-needs-reproducer.

## The triage verdict (binding)

**REACHED.** Two independent probes DEFEAT the Neo trivial inliner
(`JITCompiler.cs:2958` `InitializeFunctionParam`), force a REAL cross-frame
`Call_IL` (confirmed by a real `call` opcode in the caller body + a separate
callee frame in the stack trace), and REPRODUCE the gap:

1. `NeoStep19_SIB_DirectCall_TryCatch` -- defeats the inliner via its
   `hasExceptionHandler` gate (a `try/catch` body is NEVER inlined). The callee
   `AppendBangBig(ref string s)` does `s = s + "!"`. The caller observes
   `s == "abc"` (NOT `"abc!"`) and `r == -1` (the catch swallowed an inner
   throw). FAIL.
2. `NeoStep19_SIB_DirectCall_BigBody` -- defeats the inliner via its instruction-
   count threshold (`Optimizer.MaximalInlineInstructionCount == 20`, the
   `codeSizeOK` gate at `:2968`). The callee `AppendBangLong(ref string s)` does
   `s = s + "!"`. The caller crashes `Neo callvirt this is null` at `s.Length`
   INSIDE the callee. FAIL.

## The scope refinement (load-bearing -- BROADER than the F-7B-SIB framing)

The probe-construction surfaced that the gap is BROADER than the F-7B-SIB
"reference-type dangling-index" framing. A THIRD probe,
`NeoStep19_SIB_DirectCall_PrimitiveRef` (`BumpIntBig(ref int x)` does
`x = x + 10`, try/catch-defeated), ALSO FAILS: the caller observes
`v == 5` (unchanged; should be `15`). So the gap is NOT reference-specific:

**Root cause:** the IL-method DIRECT-`Call` byref channel is UN-REBASED and
UN-FLAGGED. `Optimizer.Neo.cs:1275` sets `clrParams = null` for an IL callee, so
the `dstIsByRefParam` gate (`:1316-1334`) NEVER fires for an IL-method byref
param -- the param is marked `plain` (the diag dump showed
`entry[0] size=8 src=24 dst=0 plain [objIdx=-1 off=0]`). `CopyNeoCallArguments`
therefore copies the caller's 8-byte Ref Slot `(objIdx, off)` VERBATIM into the
callee's param slot, and `CopyNeoCallThisBack`'s byref write-back loop SKIPS it
(it only processes `PrimitiveByRefSrc`-flagged slots). Inside the callee, the
`stind_*` / `ldind_*` `objIdx == -1` arms dereference `*(T*)(calleeFrameBase +
off)` -- but `off` is CALLER-relative, so the deref lands in the WRONG frame
(callee base, caller offset), and the mutation is lost. The F-7B-SIB reference
dangling-index is the reference-typed SUBSET; the primitive case is the
flat-bytes-write-lost SUBSET. BOTH are masked today only by the trivial inliner
folding every small direct target (Step 17's `Increment(ref int)` / `AddInto` /
`Produce` are all <= 20 instructions and inlined -- which is why they are green).

## What Changes

The fix (if shipped -- the apply stage / LEAD owns the SHIP-vs-SEQUENCE verdict):

Promote the IL-direct-`Call` byref ABI to parity with the delegate path, porting
BOTH the F-7 primitive rebase AND the F-7B reference promotion:

- JIT: flag IL-method direct-`Call` byref params (`Optimizer.Neo.cs:1275-1334`)
  so `CopyNeoCallArguments` derefs and `CopyNeoCallThisBack` writes back, mirroring
  the CLR-method byref-param flagging (Step 13 Area 4c). The dest slot sizing for
  an IL byref param is ALREADY element-typed for CLR callees (`:1249-1253`); the
  IL-callee path must match.
- Runtime: in the `Call` case (`ILIntepreter.Neo.cs:2300-2360`), mirror
  `NeoRunDelegateTargetOnThis`'s two channels BEFORE `InvokeNeoCallTarget`:
  rebase the frame-native byref offset by the frame distance for PRIMITIVE /
  value byrefs (the F-7 flat-byte path -- `CopyNeoCallThisBack`'s `objIdx == -1`
  `Unsafe.CopyBlock` at `:578` then lands flat bytes in the caller cell), and
  PROMOTE reference byrefs into a caller-owned mStack slot (the F-7B pattern --
  reuse the `Stind_Ref`/`Ldind_Ref` `NeoF10ByrefOffsetFlag` sub-arms).

The apply stage's binding decision (after re-verifying FAIL-on-HEAD): this is a
MEDIUM change (JIT flagging + runtime rebase + reference promotion), NOT the
SMALL F-7B-style promotion the planning context assumed. The LEAD decides SHIP
vs SEQUENCE given the larger-than-expected scope.

## Impact

- Affected files (IF shipped): `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs`
  (IL-callee byref flagging), `ILIntepreter.Neo.cs` (the `Call` case pre-call
  rebase/promotion + `CopyNeoCallThisBack` write-back); `TestCases/NeoStep19Test.cs`
  (the three triage probes -- the apply stage keeps them as regression probes).
- Neo-only (`#if ENABLE_NEO_MODE`); Legacy-neutral (Legacy's `StackObject[]`
  evaluation stack is not frame-truncated and its byref ABI does not have this
  caller/callee offset-rebase class).
- Regression risk: HIGH relative to F-7B. The IL-direct-call byref channel feeds
  Step 17's byref tests (all currently INLINED, so green-by-inlining). The fix
  MUST keep the inlined path byte-identical AND make the non-inlined path correct.
  Full `NeoStep` smoke MUST stay green (the inlined Step-17 cases MUST NOT regress
  once the JIT flags IL-callee byref params -- the flag must be a no-op when the
  inliner folds the call, OR the inliner's folded body must be re-examined).
- This is the TRIAGE/proposal artifact only. No source ships in this stage.

## Durable findings (1-3 lines)

- The Neo trivial inliner is defeatable by BOTH a `try/catch` body
  (`hasExceptionHandler` gate) and a >20-instruction body (`codeSizeOK` gate);
  either forces a real cross-frame `Call_IL` -- so the IL-direct-call byref path
  IS reachable, not just theoretical.
- The gap is the ENTIRE IL-direct-`Call` byref ABI (un-rebased, un-flagged),
  masked only by the inliner -- NOT just the F-7B-SIB reference subset. The
  primitive `ref int` non-inlinable probe fails too.
- Fix scope = F-7 rebase + F-7B promotion, ported to `Call`/`CopyNeoCallThisBack`,
  PLUS JIT flagging of IL-callee byref params. MEDIUM, not SMALL.
