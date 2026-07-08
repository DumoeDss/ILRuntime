## Context

F-7 (NEO-DELEGATE-REFOUT): a delegate whose target signature carries a `ref`/`out`
param, invoked from IL via Neo (`del(ref v)`), throws `ArgumentOutOfRangeException`.
The latent-edges triage (`archive/2026-09-neo-latent-edges/design.md` section 2)
proved the byref is destroyed in TWO sites and recommended a "Step-19-sized
frame-to-frame delegate-invoke mechanism." This change is the TRUE-COMPLETION: it
implements that mechanism as a SMALL, low-risk same-frame fast path (NOT the deep
redesign the triage's worst-case framing implied), because the diagnosis actually
points at a much simpler fix than "construct a writable proxy slot on the target's
mStack."

### The dump-gate verdict (confirmed empirically on HEAD `462e635d`)

A temporary probe (`ref int` / `out int` / `ref string`, deleted after the run)
reproduced 3/3 FAIL:

- `NeoF7_RefInt` -> `ArgumentOutOfRangeException` at `ILIntepreter.Neo.cs:3726`
  (the `Ldind_I4` byref-read arm, `mStack[objIdx]` with a garbage `objIdx`).
- `NeoF7_OutInt` -> `ArgumentOutOfRangeException` at `ILIntepreter.Neo.cs:3636`
  (the `Stind_I4` byref-write arm).
- `NeoF7_RefString` -> `NotImplementedException` at `ILIntepreter.Neo.cs:3862`
  (the Step-17 `ldind_ref` heap-IL-ref-field deferral -- a separate gap that
  surfaces once the byref reaches the callee; on the prior triage HEAD this was
  `:3831`. The primitive-byref cases are the clean F-7 signal; the ref-string
  case additionally needs the target's `ldind_ref` on a heap path, which is a
  Step-17 deferral tracked separately. The fast path still gets the byref Ref
  Slot to the callee correctly; whether the callee can then read it depends on
  the referent type).

The exception originates INSIDE the delegate target (`F7BumpRef`/`F7SetOut`), which
proves the byref DID cross the delegate boundary -- but as a corrupted Ref Slot
(only the `objectIndex` half, offset dropped). This is the half-read destruction.

### The two destruction sites (confirmed by code read)

1. **`ReadNeoDelegateInvokeArgs`** (`ILIntepreter.Neo.cs:246-287`) reads the caller's
   byref arg from `targetBase`. For `pt.TypeForCLR == typeof(int)` it does
   `args[i] = *(int*)(targetBase + cur); cur += 4;` (`:261`). A byref param's
   storage is the 8-byte Ref Slot `(objectIndex, offset)`, so it reads ONLY the
   first 4 bytes (`objectIndex`), boxes it / treats it as an mStack index, and the
   `offset` half is NEVER read (`:280-283` funnels "byref-as-int" through the same
   4-byte mStack-index path). The `object[]` cannot represent a byref + its
   caller-frame provenance.

2. **`NeoInvokeSub` runs the target on a SEPARATE POOLED interpreter**
   (`DelegateAdapter.cs:1006-1019`): `ILIntepreter intp = appdomain.RequestILIntepreter();`
   (`:1018`) then `intp.ExecuteNeo(method, frameBase, ...)` (`:1084`) on a frame
   built at THAT interpreter's `stack.StackBase` (`:1033`). The target's
   `frameBase`/`mStack` are UNRELATED to the caller's. So even a correctly-preserved
   `(objectIndex, offset)` Ref Slot would index into the WRONG frame/mStack, and a
   `ref`/`out` write-back to the caller's frame is impossible through this path.

### The key insight (why this is SMALL, not deep)

The triage's "frame-to-frame mechanism" framing suggested option (b) -- "construct a
writable proxy slot on the target's mStack whose backing store is the caller's frame
cell, and propagate the final value back" -- which is the deep redesign. But option
(a) -- "run the IL delegate target on the SAME pooled interpreter (reusing the
caller's frameBase/mStack)" -- is actually a TINY change, because the machinery
already exists and is already wired for the normal `Call_IL` path:

- The `Callvirt_IL` branch (`ILIntepreter.Neo.cs:2419-2463`) ALREADY calls
  `CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain)` at
  `:2431` BEFORE the delegate-Invoke check at `:2443`. The JIT-built
  `NeoCallParamMap` (`Optimizer.Neo.cs:1355-1364`) flags the byref param via
  `PrimitiveByRefSrc`/`PrimitiveByRefWriteBack`/`PrimitiveByRefElemType` for EVERY
  call-family opcode -- including a delegate-Invoke callvirt. So the byref Ref Slot
  is ALREADY correctly marshaled into `targetBase` (same frame). The destruction
  happens ONLY because the delegate-Invoke branch (`:2448-2450`) then RE-READS via
  `ReadNeoDelegateInvokeArgs` + routes to a separate interpreter, throwing away the
  correct `targetBase`.
- The normal `Call_IL` path (`ILIntepreter.Neo.cs:2142-2192`) is the EXACT template:
  `CopyNeoCallArguments` -> snapshot byref sources (`:2166-2179`) ->
  `InvokeNeoCallTarget(targetMethod, false, targetBase, mStack, retDstPtr,
  targetRetRefBase, ...)` (`:2182`, which runs `ExecuteNeo` on `this`, same
  interpreter) -> `CopyNeoCallThisBack(ref map, frameBase, targetBase, mStack,
  AppDomain, byRefSnap)` (`:2189`, the write-back).

The fix is: make the delegate-Invoke branch do what the normal `Call_IL` branch
does, plus handle the multicast next-chain and the bound-`this`.

## Goals / Non-Goals

**Goals:**
- `ref int` / `out int` / `ref <primitive>` delegate params invoked via Neo marshal
  the byref correctly AND the caller observes the callee's write-back (the F-7
  success criterion).
- `ref <reference-type>` delegate params marshal the byref correctly (the callee
  receives a valid Ref Slot). NOTE: the callee reading a heap IL ref-field via
  `ldind_ref` hits a SEPARATE Step-17 deferral (`:3862`); the fast path delivers
  the byref but cannot fix an unimplemented `ldind_ref` arm. The spec's ref-string
  scenario is met when the referent is an mStack object (the common `ref string`
  case where the local is an mStack slot), not a heap IL field.
- The plain-primitive delegate callback shapes (`NeoStep19_*`, `List.ForEach`)
  stay byte-identical (regression guard).
- Multicast delegates with a byref param: each target runs in order, write-back
  accumulates in the caller's cell (last write wins), matching Legacy.

**Non-Goals:**
- The CLR->IL delegate callback path (a CLR method like `List.ForEach` invoking an
  IL `Action<T>` from REAL CLR code) stays on the separate-interpreter
  `NeoInvokeSub` path. There is no IL caller frame there, so byref propagation is
  moot (the CLR caller's `ref`/`out` semantics are handled by the CLR delegate
  type itself, which cannot carry `ref`/`out` -- `Action<>`/`Func<>` signatures are
  by-value). F-7 is the IL->IL delegate-Invoke case only.
- Fixing the Step-17 `ldind_ref` heap-IL-ref-field deferral (`:3862`). That is a
  distinct gap, tracked under neo-byref / Step 17. The fast path gets the byref to
  the callee; the callee's ability to read a heap-field referent is a separate
  concern.
- AOT (`ilrt_neoc`) -- the JIT-only path is in scope; AOT wire-up of the same map
  follows the standard pattern if needed (out of scope unless the AOT smoke needs
  it).

## Decisions

### D1: same-frame fast path via `InvokeNeoCallTarget` (NOT a proxy-slot mechanism)

**Decision:** In the `Callvirt_IL` `IsDelegateInvoke` branch, when the delegate
target is an IL method, run it on `this` interpreter via `InvokeNeoCallTarget` using
the already-marshaled `targetBase`, then call `CopyNeoCallThisBack` for the
write-back -- mirroring the normal `Call_IL` path. Do NOT route through
`NeoInvokePublic`/`NeoInvokeSub`.

**Rationale:** The byref is ALREADY correct at `targetBase` (`CopyNeoCallArguments`
ran at `:2431`). The only reason F-7 fails is the branch re-reads through `object[]`
+ uses a separate interpreter. Reusing the existing same-frame call machinery is a
~15-line branch change. The alternative (a proxy-slot write-back on the separate
interpreter) is the deep redesign the triage worried about -- unnecessary here.

**Alternatives considered:**
- (b) proxy-slot on the target's mStack (`CopyNeoCallThisBack`-style reverse copy
  after the target returns, on the separate interpreter). REJECTED: more code, and
  the separate interpreter still cannot address the caller's frame for the
  write-back -- it would need the SAME `(objectIndex, offset)` the same-frame path
  uses natively, so it buys nothing over D1.
- byref-aware `ReadNeoDelegateInvokeArgs` + `WriteNeoCallSlot`. REJECTED: even with
  a byref-aware read, the `object[]` representation cannot carry the caller-frame
  provenance, and the separate interpreter still cannot write back. The destruction
  is structural to the `object[]` + separate-interpreter path; patching the read
  does not fix site 2.

### D2: replace slot 0 with the bound `instance` before running the target

**Decision:** The delegate-Invoke callvirt's `targetBase` slot 0 holds the adapter
(`delThis`, read at `:2445`). The delegate target method needs its bound
`instance` as `this`. Before `InvokeNeoCallTarget`, write `dAdapter.Instance` into
the target's slot 0 (the ref slot), mirroring `NeoInvokeSub`'s
`WriteNeoCallSlot(paramInfos[0], frameBase, mStack, frameRefBase, instance)`
(`DelegateAdapter.cs:1063`). For a static delegate target, `instance` is null and
the target's `HasThis` is false -- no slot-0 write.

**Rationale:** `InvokeNeoCallTarget` -> `ExecuteNeo(ilm, targetBase, ...)` runs the
target with `targetBase` as its frame base; slot 0 must be the bound `this` for an
instance-method target (the JIT lays out `HasThis` methods with `this` at slot 0).

### D3: multicast next-chain walks on the same interpreter

**Decision:** After the head target runs + write-back, walk `dAdapter.Next`
(the multicast chain). Each subsequent target runs the SAME fast path (same
interpreter, same `targetBase`, same write-back). Return semantics mirror Legacy
`ILInvokeSub`/`NeoInvokeSub`: the LAST target's return value is the delegate
Invoke's return (for a `Func`-shaped custom delegate). For a `ref`/`out` param, each
target's write-back accumulates in the caller's cell (the snapshot is taken once
before the head; subsequent targets read the already-mutated caller cell -- matching
C# multicast-byref semantics where each invoked target sees the prior target's
mutation).

**Rationale:** Singlecast (the common case) is one target. Multicast with a byref
param is rare but must be correct; re-running the fast path per target is uniform
and cheap.

### D4: snapshot the byref sources BEFORE the call (reuse the Step-20 fixer pattern)

**Decision:** Take the pre-call byref-source snapshot
(`SnapshotNeoCallByRefSources`, `ILIntepreter.Neo.cs:603`) before running the head
target, exactly as the normal `Call_IL` path does (`:2166-2179`), and pass it to
`CopyNeoCallThisBack`. This handles the case where the byref source register is
reused as the call dest (the Step-20 async-state-machine pattern).

**Rationale:** The delegate-Invoke callvirt is a call site like any other; the
clobber risk is identical. Reusing the snapshot avoids a second byref-clobber bug
class.

### D5: keep `ReadNeoDelegateInvokeArgs`/`WriteNeoDelegateInvokeReturn` for the fallback

**Decision:** Do NOT delete `ReadNeoDelegateInvokeArgs` (`:246-287`) or
`WriteNeoDelegateInvokeReturn` (`:290-319`) yet. They remain as the path of last
resort if the fast path cannot apply (e.g. a non-IL delegate target, or a future
CLR-target delegate). The fast path is taken ONLY for an IL target (`dAdapter` is a
`DelegateAdapter` whose `method` is an `ILMethod`).

**Rationale:** Conservative. The plain-primitive IL-delegate-Invoke case
(`NeoStep19_TC8`) currently goes through the separate-interpreter path and is green;
moving it to the fast path is a behavior change that MUST stay green. If the fast
path proves uniform (it should -- `InvokeNeoCallTarget` is the standard call), a
later cleanup can remove the old path. For now, keep both.

## Risks / Trade-offs

- **[Risk] The fast path changes the green Step-19 IL-delegate-Invoke hot path
  (`NeoStep19_TC8`, plain `int` param).** -> Mitigation: the fast path is a strict
  superset of the normal call for IL targets; `InvokeNeoCallTarget` is the SAME
  function the normal `Call_IL` path uses. The `NeoStep19` smoke (currently green)
  is the regression guard -- it MUST stay green. If it regresses, the fallback
  (D5) keeps the old path available behind a target-type check.
- **[Risk] The bound-`this` slot-0 write (D2) clobbers a byref param laid out at
  slot 0.** -> Mitigation: slot 0 of a delegate-Invoke callvirt is ALWAYS the
  adapter `this` (the JIT lays out `HasThis` with `this` first); the explicit
  params start at slot 1. Writing `instance` into the ref slot at the `this`
  position is correct (the target's `HasThis` expects `this` at slot 0). Verify
  via the existing `NeoStep19_InstanceMethod`/`ClosureOverThis` probes.
- **[Risk] Multicast write-back ordering: each target must see the prior target's
  mutation.** -> Mitigation: D3 re-reads the caller cell per target (the snapshot
  is for clobber-protection of the SOURCE register, not the caller cell's value);
  the write-back lands in the caller's frame cell, which the next target's
  `CopyNeoCallArguments` re-reads. Add a multicast-with-byref probe.
- **[Risk] ref-string hits the Step-17 `ldind_ref` heap-field deferral (`:3862`)
  even after the fast path.** -> Mitigation: the spec's ref-string scenario is met
  for the mStack-object referent case (the common `ref string` local). The
  heap-IL-field `ldind_ref` is a separate Step-17 gap; note it in the spec and do
  not block F-7 on it. If the probe's `ref string` local is an mStack slot (it is
  -- a string local is a ref slot), the fast path delivers a valid mStack-index
  Ref Slot and the callee's `ldind_ref` reads the mStack object directly (not the
  heap-field arm).
- **[Trade-off] Two delegate-Invoke paths now coexist (fast path for IL targets,
  separate-interpreter path for the fallback).** -> Acceptable: the fallback is
  rarely hit (CLR->IL callbacks go through the per-arity `Invoke` overrides, not
  the `Callvirt_IL` branch). A later cleanup can unify.

## Open Questions

- Does the JIT emit a byref-aware `NeoCallParamMap` (with `PrimitiveByRefSrc` set)
  for a delegate-Invoke `Callvirt_IL`, given the Invoke method's signature is the
  DELEGATE's Invoke signature (with the byref param) and the call's source operands
  are the caller's byref locals? -> YES, confirmed: the map is built per-call-opcode
  in `Optimizer.Neo.cs:1355-1364` from the call's source/dest slot infos, keyed on
  the param's byref-ness; the delegate-Invoke callvirt is a call opcode like any
  other. (If a future JIT change skips map-building for `IsDelegateInvoke`, the fast
  path's `CopyNeoCallArguments`/`CopyNeoCallThisBack` would no-op and F-7 would
  resurface -- the spec guards this.)

## Implementation Notes / Corrections (landed 2026-07-09)

The apply revealed TWO corrections to the design as written. Both are durable
findings worth recording; neither changes D1's shape (same-frame fast path via
`InvokeNeoCallTarget`), only the byref-preservation mechanism.

### Correction 1: byref RELATIVIZATION (the design's "stind resolves against the
caller frame" premise was WRONG)

The design (D1, "The key insight") states the byref is "ALREADY correctly marshaled
into targetBase" and "the target's stind/ldind operate DIRECTLY on the caller's
frame cell." This is FALSE for a real cross-frame call. `stind_*`/`ldind_*` resolve
a frame-native byref `(objectIndex == -1, off)` against the CURRENT `ExecuteNeo`'s
`frameBase` (`ILIntepreter.Neo.cs:3722` etc.), i.e. the TARGET's frame base, which
sits ABOVE the caller's. The verbatim-copied `off` is CALLER-frame-relative, so the
raw offset addresses the wrong cell. The new helper `NeoRunDelegateTargetOnThis`
RE-BASES each frame-native byref param's offset by `-(dTargetBase - callerFrameBase)`
so it resolves back to the caller's frame cell; the rebase is undone after the run
so multicast re-invocation does not compound the subtraction. mStack-object byrefs
(`objectIndex >= 0`) address mStack absolutely and need no rebase.

WHY the design's premise looked true: the Neo Step-17 IL-byref tests
(`NeoStep17_TC1_RefFrameLocal` etc.) pass -- but ONLY because the optimizer INLINES
those small static targets, so the byref never actually crosses a frame (verified by
tracing: no `Call_IL` fires; the `ldind`/`stind` run in the CALLER's frame). A
delegate-Invoke target CANNOT be inlined (indirect call), making F-7 the FIRST real
cross-frame IL byref -- hence relativization is required here even though `Call_IL`
does not need it for the (inlined) Step-17 shapes.

### Correction 2: static-target base shift + instance `this` overwrite

The Invoke-method layout reserves slot 0 for the adapter `this` (4-byte mStack
index); explicit params follow at offset 4+. An INSTANCE target's layout coincides
(this@0, params@4+), so `targetBase` is used directly and the adapter mStack slot
(`mStack[*(int*)targetBase]`) is overwritten with the bound `instance`. A STATIC
target has no `this`, so its first param lives at offset 0 -- the helper shifts the
base forward by 4 (`dTargetBase = targetBase + 4`) so the static target reads its
params where the Invoke layout placed them.

### Follow-up (NOT blocking F-7): ref-type byref write-back of a callee-created
object

The primitive-byref write-back (ref int / out int / multicast) is OBSERVABLE and
green. The `ref string` MARSHAL+READ path works (the callee derefs the byref
correctly). HOWEVER, an in-place WRITE-BACK of a callee-CREATED reference object
(`s = s + "!"`) leaves the caller's slot with a DANGLING mStack index: the new
object lands in the callee's frame ref region, which `ExecuteNeo` pops on return
(`ILIntepreter.Neo.cs:4703`). Ref-type byref write-back of a callee-created object
needs mStack LIFETIME PROMOTION -- a distinct follow-up tracked as F-7B in
`neo-deferred-items.md`. The `NeoStep19_ByRef_String` probe was scoped to the
marshal/read path (`ReadLength(ref s)` returns `s.Length`) so it is green; the
in-place-new-string write-back is the open half.
