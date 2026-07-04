# Review Report — implement-neo-step18

**Reviewer:** verifier (author != verifier). Adversarial.
**Date:** 2026-07-04
**Method:** spec/design read + diff review + empirical probes (JIT dumps, targeted
test runs). No shipped code edited. Probes deleted after use.

## Executive verdict: **CLEAN WITH FINDINGS** (ship-able; IL-VT-newobj deferral adjudicated ACCEPTABLE)

The shipped work (CLR-type newobj + Q-NEWOBJ close-out + the IL-VT-newobj NIE) is
correct, well-tested, and honestly documented. The IL-VT-newobj deferral is a REAL
blocker (empirically reproduced), the NIE is loud, and the implementer did NOT miss
a simpler heap-instance-`this` approach. No Blockers. Two Minor findings (both about
failure-mode discoverability, neither a correctness regression). No Major findings.

Severity counts: **Blocker 0 / Major 0 / Minor 2 / Info 2.**

## What was verified (all PASS)

- **Build:** CLI `Debug_Neo` 0 errors; TestCases `Debug` 0 errors.
- **FULL NeoStep smoke:** `Ran 91 tests, 0 failed, 0 ignored` (baseline was 84/84;
  +7 = the NeoStep18 cases). No regression.
- **NeoStep18 (7/7):** TC1 inlined-VT-ctor-with-args, TC2 inlined-VT-with-ref-field,
  TC3 inlined-VT-default, TC4 Q-NEWOBJ (newarr; new T(intArg); assert + array
  round-trip), TC5 `new List<int>()` + Add + index, TC6 `new Dictionary<int,string>()`
  + count/index, TC7 `throw new InvalidOperationException` + try/catch — all green.
- **Step 16 TC4** restored to real `new NeoStep16Item(5)` ctor-with-arg: PASS.
- **Legacy `ILIntepreter.Register.cs`:** `git diff HEAD` = 0 lines (untouched by this
  change). (Its diff vs `origin/master` is from prior Steps 11-17, not Step 18.)
- **Scope:** all Neo changes behind the existing `Newobj` arm in `ILIntepreter.Neo.cs`
  (already `#if ENABLE_NEO_MODE`-gated file). No new runtime files; one new test file.
- **CLR newobj dest storage:** correct — `retDstPtr = frameBase + ip->DstOffset` gets
  the mStack index; `newobjDstIdx = frameRefBase + dstRefOffset` gets the object
  (`InvokeNeoClrMethod` line 288-292). Both offsets stamped from the dest register's
  own slot (`Optimizer.Neo.cs:1234-1241`), so the object lands in the right place.
- **Early-return split:** sound. Redirect path returns first (owns its dest write,
  line 270-272); reflection-newobj path now stores the result (line 283-294);
  `retDstPtr == null` (void/non-newobj) still early-returns (line 281-282). The
  redirect-vs-reflection ordering is correct because a redirected CLR ctor is
  rewritten to `Call_Redirect` at JIT time (`JITCompiler.cs:1761-1772`) and never
  reaches the `ExecuteNeo` Newobj arm — so the Newobj arm's `InvokeNeoClrMethod`
  call with `isNewobj=true` is always the **reflection** (non-redirect) path.
  Confirmed by TC5/TC6 (reflection constructs List/Dictionary, usable post-construct).
- **`throw new ClrException` (TC7):** green — exception object allocated by CLR
  newobj, surfaced by `throw`, caught. Side-benefit realized.
- **Q-NEWOBJ non-repro:** sound. `AllocateLocalStackSpaces`
  (`JITCompiler.cs:1460-1472`) assigns every temp register a DISTINCT byte region
  (`offset += maxSize`) and DISTINCT ref slot (`refOffset += maxRefCount`) — no
  inter-register aliasing. The newobj dest / newarr array / int arg each occupy
  distinct registers → distinct regions. Matches the implementer's JIT dump and the
  Q-STRUCT/Q-LONG precedent. Closing as non-reproducible (no fix) is correct.

## Adjudication: IL-VT-newobj deferral (Q-VT-NEWOBJ) — **blocker is REAL; deferral ACCEPTABLE**

The implementer says IL-VT newobj is blocked on VT field-access lowering
consistency: a VT ctor's `this`-relative `stfld` lowers inconsistently and the
caller's reads on the newobj result are non-inline. I verified every link in that
chain by reading the lowering AND by an empirical probe.

**Lowering read (confirms the mechanism):**
- A VT ctor's `this` (param slot 0) is laid out as the **in-frame value**, NOT a
  4-byte reference: `JITCompiler.cs:1332-1344` (`Size = TotalPrimitiveSize`,
  `RefCount = TotalReferenceCount`, keyed on `declaringType.IsValueType`). This
  matches the design's "RESOLVED (apply)" note.
- `this`'s register type is seeded as the declaring IL value type
  (`JITCompiler.cs:893`, `BuildInitialRegisterTypes`).
- `TryRewriteFieldAccessForInline` (`JITCompiler.cs:780-828`) rewrites `stfld` to
  `_Inline` whenever the operand register's type is an in-frame IL VT — so
  `this.field =` rewrites to `_Inline` and writes the **callee's own `this` frame
  slot** (`Optimizer.Neo.cs:892-908`, `ResolveLiveAlias` falls back to
  `{Reg=reg, Offset=0}` at line 427 since `this` is not an ldloca/ldflda alias).
- So within the ctor, stflds ARE consistent (they write the callee `this` slot).
  The inconsistency is **cross-frame**: the caller passes `this` as an mStack index
  (heap) or a Ref Slot (ldloca form), but the callee frame slot is sized for flat VT
  bytes. `addrAlias` only tracks `ldloca`/`ldflda`-produced addresses
  (`Optimizer.Neo.cs:35-81`), never a `this` param or a newobj dest — so there is no
  mechanism to make the caller's representation agree with the callee's.

**Empirical probe (confirms the failure):** I added a temp test (deleted after) that
forces a NON-inlined IL-VT newobj two ways:
1. **Return form** `return new ProbeBig(10,20,30)` — emits a real `newobj`
   instruction → hits the implementer's NIE
   `"Neo Newobj IL value-type is not implemented (Step 18 D1 blocked on VT
   field-access lowering consistency; see D2)"`. **Loud, Step-tagged.** Good.
2. **Local form** `ProbeBig p = new ProbeBig(1,2,3)` (the COMMON C# form) —
   compiles to `ldloca p; call ctor`, NOT a `newobj` instruction, so it BYPASSES
   the NIE and instead crashes inside the ctor: `NullReferenceException` at the
   first `stfld`. This is a real, reproducible failure of the VT-`this` field-access
   path (the same root cause), surfaced as a confusing raw NullRef rather than a
   Step-tagged NIE.

**(a) Is the blocker real?** YES — reproduced both ways.
**(b) Did the implementer miss a simpler heap-instance-`this` fallback (Legacy
     approach)?** NO. A heap-instance-`this` fallback fails for the SAME reason:
the callee seeds `this` as the VT type and lays out an in-frame VT-sized `this`
slot (`JITCompiler.cs:1332-1344`), so `this.field=` rewrites to `_Inline` and
writes the callee frame regardless of what the caller passed. Passing a heap
ILTypeInstance index as `this` would make those `_Inline` writes land on garbage
(the index reinterpreted as a frame offset). The only way to make a heap-`this`
work is to change the callee's `this` seeding/layout — which is the D2 change, and
which would simultaneously break the existing in-frame `ldloca + stfld` VT tests
(NeoStep12), because those rely on the SAME in-frame `this` model. So the
heap-`this` "simpler approach" is not simpler — it IS the broad D2 change. The
implementer's "frame-native this / no heap" design choice was not over-constrained;
it is the only design consistent with the existing in-frame VT model. The deferral
is the correct call.
**(c) Is the NIE honest/loud?** YES for the `newobj`-instruction path. (See Minor-1
     for the local-form path, which is pre-existing and out of scope but worth a
     follow-up note.)

**Conclusion:** the IL-VT-newobj deferral is acceptable. The blocker is real, the
shipped NIE is loud, and no simpler approach was missed. IL-VT newobj remains a
core Step-18 deliverable that is now deferred to [VT-THIS-ADDR] — which is the
honest outcome (the alternative was shipping a silently-wrong construction).

## Findings

### Minor-1 — IL-VT newobj LOCAL form crashes with raw NullRef, not a Step-tagged NIE
- **File:** `ILIntepreter.Neo.cs` Newobj arm (the VT NIE at line 1699-1700) +
  the unmodified VT-ctor `stfld` lowering.
- **What:** `VT x = new VT(args)` on a local compiles to `ldloca x; call ctor`
  (no `newobj` instruction), so it never reaches the Newobj arm's VT NIE. Instead
  it crashes inside the ctor with `NullReferenceException` at the first `stfld`
  (the VT-`this` field-access inconsistency, same root cause as Q-VT-NEWOBJ).
  Confirmed by probe.
- **Why it matters:** a confusing raw NullRef is worse for discoverability than a
  Step-tagged NIE. The implementer's NIE honestly covers the `newobj`-instruction
  path; the local form (the far more common C# idiom) is left to crash opaquely.
- **Regression?** NO — pre-existing (the VT-ctor path was never wired; Step 12
  tests only use `default(T)` + direct `s.a =`, never a user ctor). Not introduced
  by this change.
- **Fix (optional, for [VT-THIS-ADDR]):** when the VT-`this` field-access
  inconsistency is detectable, surface a Step-tagged NIE on the ctor-entry / first
  VT-`this` stfld rather than letting it NullRef. Or document in
  `neo-deferred-items.md` Q-VT-NEWOBJ that the local form fails this way.

### Minor-2 — Delegate-newobj `IsDelegate` NIE is effectively unreachable; spec-named NIE not fired
- **File:** `ILIntepreter.Neo.cs:1651-1652` (`if (ilNewobjType.IsDelegate) throw
  NIE("Neo Newobj delegate is not implemented")`).
- **What:** Real delegates (`System.Action`, etc.) are CLR types. `new Action(o.Foo)`
  fails at the **`Ldftn` opcode** (`"Neo: opcode Ldftn not yet implemented (Step 6)"`,
  confirmed by probe) — `ldftn o.Foo` is emitted before `newobj`, so the method
  never reaches the Newobj arm. Even if `Ldftn` existed, a CLR delegate's
  `DeclearingType as ILType` is null → it enters the `ilNewobjType == null` branch →
  `is CLRType` → routes to `InvokeNeoClrMethod`, NOT the `IsDelegate` check. The
  `IsDelegate` arm only fires for a hypothetical IL-defined delegate type (rare).
- **Why it matters:** the spec "Non-goals" requirement says delegate newobj "SHALL
  continue to throw a Step-tagged NotImplementedException." Behavior matches (it
  DOES throw a clear Step-6 NIE via Ldftn — not a silent wrong result), but the
  spec-NAMED NIE (`"Neo Newobj delegate is not implemented"`) is not the one fired,
  and the `IsDelegate` arm is dead code for the common case.
- **Regression?** NO — delegate newobj failed identically before this change (via
  Ldftn). Behavior is preserved (loud NIE).
- **Fix (optional):** move the delegate guard before the CLR routing, or rely on
  the Ldftn NIE and update the spec note to say "delegate newobj is blocked
  upstream by Ldftn (Step 6)." Cosmetic.

### Info-1 — `clrCtor` cast is unchecked (consistency, not a bug)
- **File:** `ILIntepreter.Neo.cs:1641` `var clrCtor = targetMethod as CLRMethod;`
  then `InvokeNeoClrMethod(clrCtor, ...)`.
- **What:** If `targetMethod` were not a CLRMethod despite `DeclearingType is
  CLRType`, `clrCtor` is null → NPE. In practice a method whose declaring type is a
  CLRType is always a CLRMethod, and this `is CLRType` → `as CLRMethod` pattern
  matches the rest of the file (e.g. `Newarr` arm line 2401). No regression.
- **Fix:** none required; noted for completeness.

### Info-2 — VT NIE comment is slightly self-contradictory on the "MIX"
- **File:** `ILIntepreter.Neo.cs:1669-1678` (the deferred-block comment).
- **What:** The comment says a VT ctor's stflds lower to "a MIX of in-frame
  `_Inline` and heap `Stfld_*`/`GetNeoILInstance`." My lowering read shows the
  ctor's stflds are consistently `_Inline` (they write the callee `this` slot);
  the real inconsistency is **cross-frame** (caller representation vs callee
  in-frame `this` slot), not a within-ctor mix. The implementer's empirical
  "first stfld _Inline, rest heap" observation likely reflects a specific
  multi-field ctor where register reuse/type-clobbering mid-method changes the
  operand type — plausible but not the cleanest characterization.
- **Why it matters:** future maintainers reading the NIE may chase a within-ctor
  mix that is really a caller/callee representation mismatch.
- **Fix (optional):** tighten the comment to emphasize the cross-frame
  representation mismatch (caller passes index/Ref-Slot; callee expects in-frame VT
  bytes) as the root cause.

## Out-of-scope items confirmed still NIE / untouched
- Delegate newobj: NIE via Ldftn (Step 6) — see Minor-2.
- Generic-parameter VT newobj: not exercised by any NeoStep test; no new code path
  (would fall through to the VT NIE if it reached the arm).
- No-binder CLR-VT-with-refs newobj: the reflection path inherits the Step-13b
  `NeoClrStructHasReferenceField` guard in `CLRMethod.Invoke` (unchanged).
- Legacy `ExecuteR` Newobj: untouched.

## Recommendation
**SHIP.** CLR newobj is correct and tested; Q-NEWOBJ is honestly closed as
non-reproducible with sound evidence; the IL-VT-newobj deferral is a real blocker
with a loud NIE and no missed simpler approach. Both Minor findings are
failure-mode-discoverability nits (not correctness), are pre-existing, and can be
folded into the [VT-THIS-ADDR] follow-up. No Blockers, no Majors.
