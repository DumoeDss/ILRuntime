# Planning Context — implement-neo-step18

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent
> "按照你建议的顺序，继续按当前流水线推进后续内容！"

This run = **Step 18** (IL value-type newobj + CLR type newobj), folding in
**Q-NEWOBJ** (the newobj dest/arg aliasing-after-newarr quirk from Step 16). It
completes the newobj paths. Committed AND pushed after review clean (user
pre-authorized commit+push per phase).

Prior state (committed + pushed): Steps 11-17 + OPT-HARDEN + 13b. HEAD=`06abf866`. NeoStep smoke baseline = 84/84.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose** (user frames each step as one phase). Pipeline: propose →
apply → verify → review-loop → ship → archive → (LEAD commits + pushes).

## 3. Step 18 scope (from `.trae/documents/neo-implementation-steps.md` §"Step 18")
Goal: complete the newobj paths Step 8b didn't cover (IL value-type + CLR types).
Content:
1. **IL value-type newobj:**
   - The target slot is on the frame (compile-time allocated), zero-init.
   - `this` is passed to the constructor as a **Ref Slot** (ldloca produces the
     frame ref) — Step 17's byref model.
   - The ctor operates on the frame data directly (via stind/stfld through the ref).
2. **CLR type newobj** (complete the Step 8b placeholder):
   - With Redirection → Neo Redirection.
   - Without Redirection → `CLRMethod.Invoke` reflection creation.
3. **Fold-in Q-NEWOBJ:** `new T(intArg)` immediately following a `newarr` collides
   in the Call/Newobj Push-scanning lowering (Step 10/11 territory; Step 16
   worked around it via default-ctor + field-set). Fix the newobj/call
   Push-scanning dest/arg aliasing.

Dependency: Step 8b (ref-type newobj basics), Step 17 (Ref Slot — value-type ctor needs ref `this`).
Validation: `new MyILStruct(args)` — frame value correct; `new List<int>()` — CLR type creation; value-type ctor `this.field = value` (via Ref Slot writeback to frame).

**Side benefit:** CLR newobj (without Redirection, reflection creation) may enable `throw new CLRException()` (e.g. `new System.Exception()`) — which would unblock more exception tests. Note any such tests turning green.

## 4. RESEARCH REQUIRED (planner)
- Current Neo `newobj` state: grep `Newobj`, `newobj`, `NewObj` in `ILIntepreter.Neo.cs` + `JITCompiler.cs`. Step 8b did ref-type newobj; the IL value-type + CLR-type paths throw NIE (Step-tagged). The Step 13 finding noted `Newobj` arm throws `NotImplementedException("Neo Newobj CLR type is not implemented (Step 9)")` at `ILIntepreter.Neo.cs:1411-1413` (for CLR types). Map exactly what exists vs NIE.
- **IL value-type newobj:** how the ctor's `this` must be a frame Ref Slot (Step 17's Ldloca-style ref). The ctor is an IL method that takes `this` as a byref-to-frame; it writes fields via stind/stfld through the ref → writeback to the frame slot. Determine how the JIT allocates the frame slot for the new VT and passes the ref to the ctor (call ABI for a byref `this` — Step 17's byref call-ABI, but `this` is the first param).
- **CLR type newobj:** the Step 8b placeholder; the Redirection path (Neo Redirection) vs the reflection path (`CLRMethod.Invoke` reflection creation — `Activator.CreateInstance` or the CLR ctor via reflection). How `CLRMethod.Invoke(byte*)` creates an instance today (Step 13b filled param reading; newobj creation is the ctor-call path).
- **Q-NEWOBJ:** the Call/Newobj Push-scanning lowering where `new T(intArg)` after `newarr` collides (Step 16 Finding Q-NEWOBJ; suspect the Push register allocation). Determine the root cause + minimal fix.
- The Legacy reference: `ILIntepreter.Register.cs` `ExecuteR` newobj (IL VT + CLR) — the SEMANTICS reference (Neo uses frame + Ref Slot, not StackObject). Do NOT modify Legacy.
- `TestCases/NeoStep16Test.cs` (TC4 worked around Q-NEWOBJ) for the test convention.

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new runtime code behind `#if ENABLE_NEO_MODE`. Legacy (`ExecuteR`) is the REFERENCE, not to modify.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 84/84; your NeoStep18 cases add, NO existing case regresses; previously-failing newobj tests may turn GREEN — incl. possibly throw-via-new CLR exception tests). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoStep18Test.cs`, ASCII. Cover: `new MyILStruct(args)` (frame value correct, incl. a ctor that sets a field via `this.field=`); `new List<int>()` or similar CLR type creation; (Q-NEWOBJ) `new T(intArg)` after a `newarr`. No throw-asserting tests (harness limitation — though throw-via-new-CLR-exception may now be GREEN-testable if CLR newobj lands; if so, a try/catch test is fine).
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION:** newobj + the Q-NEWOBJ Push-scanning fix touch the call/newobj lowering (used by EVERY call). Full NeoStep smoke is the gate.
- **CJK write caveat:** Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-step18/`:
- `proposal.md` — Why / What Changes / Impact + the explicit In/Deferred list + the Q-NEWOBJ root cause + the IL-VT-ctor-this-byref design.
- `design.md` — concrete, code-grounded: IL value-type newobj (frame slot alloc + zero-init + Ref-Slot `this` to the ctor + ctor writeback via stind/stfld); CLR type newobj (Redirection vs reflection); Q-NEWOBJ fix (the Push-scanning dest/arg aliasing). Edge cases (default ctor, ctor with args, base ctor chain, generic CLR type) + non-goals (anything clearly out of scope).
- `specs/<capability>/spec.md` — ADDED requirements, fresh. Capability `neo-newobj` (new) or extend.
- `tasks.md` — checkbox tasks for one implementer pass; mark deferred items.

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

## 8. Planner findings (2026-07-04)

**Code map (current newobj state, confirmed).**
- `ILIntepreter.Neo.cs` `Newobj` arm @1589-1625: handles ONLY IL ref-type.
  - `var newobjType = targetMethod.DeclearingType as ILType; if (newobjType ==
    null) throw NIE("Neo Newobj CLR type is not implemented (Step 9)")` @1598-
    1600 (the CLR blocker; note tag says "Step 9" but it is the Step 18 item).
  - `if (newobjType.IsDelegate) throw NIE("Neo Newobj delegate is not
    implemented")` @1601-1602 (stays -- Step 19).
  - No `IsValueType` branch exists -> IL VT newobj silently heap-allocates
    (wrong). This is the gap to add.
  - Ref-type path: `Instantiate` -> dest mStack slot -> `*(int*)targetBase =
    newobjDstIdx` (this) -> `CopyNeoCallArguments` -> `mStack.Add(this)` ->
    `InvokeNeoCallTarget(ctor, isNewobj:true, ...)`. Reused unchanged for non-VT.
- `InvokeNeoClrMethod` @263-322 already wires `clrMethod.Invoke(targetBase,
  mStack, isNewobj)` for the reflection path AND the Neo Redirection path. The
  ONLY CLR-newobj gap is (a) the ExecuteNeo blanket CLR NIE @1598-1600 and (b)
  the early-return `if (isNewobj || retDstPtr == null) return;` @274 which
  skips storing the reflection-created object into the dest. Both are small.
- `CLRMethod.Invoke(byte*, AutoList, bool isNewObj)` @333-453 (Step 13b) ALREADY
  does `cDef.Invoke(param)` for newobj (@436) and reads params via the unified
  `ReadNeo*` layout. So CLR reflection newobj creation is essentially done; the
  routing is the missing piece.
- `Optimizer.Neo.cs` Call/Newobj lowering @1089-1252: builds `paramInfos` for
  both IL (`ILMethod.CompiledFrame.ParamInfos`) and CLR (synthesized, with a
  `this` ref slot at [0] for newobj @1160-1165). For Newobj dest: stamps
  `op.DstOffset`/`op.Operand3` from `localInfos[op.Register1]` @1234-1241. The
  IL-ctor `this` (param slot 0) layout comes from the ctor's own
  `CompiledFrame.ParamInfos[0]` -- must confirm whether a value-type ctor's
  `this` is sized 8-byte byref or in-frame value (D2 / task 0.3).

**Q-NEWOBJ root-cause hypothesis (confirmed site, to verify with dump).**
- JIT `Newobj` lowering `JITCompiler.cs:1759-1760`: `baseRegIdx -= pCnt;
  op.Register1 = baseRegIdx++` -> the newobj DEST reuses the just-popped arg's
  register. Normal consume-args-produce-on-top. For `new T(intArg)` (pCnt=1),
  dest == arg register.
- `Newarr` `JITCompiler.cs:2078-2082` does NOT decrement baseRegIdx (consumes
  count, produces array, net 0). So a preceding newarr leaves the array temp
  live at the register the newobj arg/dest lands in/adjacent to.
- Suspected collision: the newobj dest's `DstOffset`/`Operand3`(ref offset) is
  stamped from `localInfos[op.Register1]`, and if that register's mStack ref
  slot still holds the newarr array ref, the newobj dest write (or the ctor's
  `this`/arg read) observes/clobbers the array reference. NOT the frame BYTES
  (each temp gets a distinct `Offset` in `AllocateLocalStackSpaces` @1460-1472)
  -- the aliasing is in the mStack REF region or the dest-vs-arg register
  identity. Must pin with a JIT dump (task 0.2) before editing.
- Pre-existing (Step 10/11 territory, 0 diff there by Step 16). NOT a Step 16
  regression.

**Design decisions locked at propose.**
- IL-VT newobj: frame slot is the construction site; zero-init dest; `this` =
  frame-native Ref Slot `(-1, destByteOff)` into ctor param slot 0; ctor
  writeback is automatic via stind/stfld through the Ref Slot (no post-ctor
  copy). No heap ILTypeInstance for VT (avoids the format-conversion anti-
  pattern).
- CLR newobj: route to `InvokeNeoClrMethod(isNewobj:true)`; reflection path
  stores the result into dest mStack slot (split the early-return so reflection
  stores, redirect owns its dest). dest = reference temp (4-byte idx + 1 ref
  slot), same as IL ref-type newobj dest.
- Q-NEWOBJ: prefer the localized `Optimizer.Neo.cs` dest-handling fix (option a);
  JIT dest-register change (option b) is the fallback only.
- Delegate newobj, no-binder CLR-VT-with-refs newobj, generic-param VT newobj
  are DEFERRED (stated, not dropped).

**Side-benefit watch.** CLR newobj may unblock `throw new
SomeClrException()` (the exception object is allocated by newobj then thrown).
Check at verify whether any previously-failing exception test turns green; note
in ship log. Not a hard requirement.

**Apply ordering.** Phase 0 (baseline + Q-NEWOBJ reproducer dump) -> Phase 1
(Q-NEWOBJ fix, lowest scope) -> Phase 2 (IL-VT newobj) -> Phase 3 (CLR newobj)
-> Phase 4 (tests + FULL NeoStep smoke gate, 84/84 baseline) -> Phase 5
(close-out: update design.md with confirmed root cause, mark Q-NEWOBJ RESOLVED
in neo-deferred-items.md).

## 9. Apply-phase findings (2026-07-04) — append-only

**Shipped (Step 18).**
- **CLR-type newobj:** DONE. Removed the blanket CLR NIE in the `Newobj` arm
  (`ILIntepreter.Neo.cs`); `CLRType`-declared ctors route to
  `InvokeNeoClrMethod(isNewobj:true, retDstPtr=frameBase+destByteOff,
  targetRetRefBase=newobjDstIdx)`. Split the `InvokeNeoClrMethod` early-return:
  the **reflection** path now stores the returned object into the dest mStack
  ref slot + writes the index to the dest byte offset; the **redirect** path
  keeps its early-return (redirect owns the dest write). `new List<int>()` /
  `new Dictionary<int,string>()` work (TC5/TC6 green). Side-benefit: `throw new
  ClrException` + try/catch works (TC7 green). NOTE: a CLR ctor WITH a Neo
  Redirection is JIT-lowered to `Call_Redirect` (no `ExecuteNeo` arm) -- that
  path is unchanged; the Newobj arm only sees non-redirected CLR newobj.
- **Q-NEWOBJ:** RESOLVED as NON-REPRODUCIBLE (no code change). The JIT dump of
  `localInfos` shows the newobj dest, the newarr array temp, and the int arg
  each get a DISTINCT frame byte region + mStack ref slot (e.g. dest r1
  off=4/ref=1, array r0 off=0/ref=0, arg r11 off=44/ref=3). The planner's
  hypothesis (newarr doesn't decrement baseRegIdx -> collision) is disproven:
  `AllocateLocalStackSpaces` already allocates distinct regions per register.
  Same outcome as Q-STRUCT/Q-LONG. Step 16 TC4 restored to real ctor-with-arg.

**Deferred (clear Step-18-tagged NIE in the Newobj arm).**
- **IL value-type newobj (D1/D2):** BLOCKED. The VT ctor's `this`-relative
  `stfld` lowers to a MIX of in-frame `_Inline` (writes callee frame bytes) and
  heap `Stfld_*`/`GetNeoILInstance` (treats `this` as an mStack index); the
  caller's field reads on the newobj result are non-inline. `addrAlias` only
  tracks `ldloca`-produced addresses, not a `this` param or newobj dest. A
  heap+copy-back fallback is also infeasible without the consistency fix.
  Confirmed `ParamInfos[0]` for a VT ctor is sized as the in-frame value (NOT
  8-byte byref) at `JITCompiler.cs:1332-1344`. Needs the D2 change: track a VT
  `this`/newobj-dest as an in-frame address for ALL field access -- touches the
  Step 12 VT layout / shared field-access lowering (every VT instance method).
  Recorded as **Q-VT-NEWOBJ** / [VT-THIS-ADDR]. Note: the C# `ldloca + call
  ctor` local form hits the same issue.

**Smoke gate.** FULL NeoStep smoke: **91/91 green, 0 failed** (baseline 84/84;
+7 = the NeoStep18 cases). No regression. Build: CLI Debug_Neo 0 errors,
TestCases Debug 0 errors. Legacy (`ExecuteR`) untouched; all Neo code behind
`#if ENABLE_NEO_MODE`.
