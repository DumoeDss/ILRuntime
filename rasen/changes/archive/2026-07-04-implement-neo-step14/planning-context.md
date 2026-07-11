# Planning Context — implement-neo-step14

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent (verbatim)
> "继续完成12b，13，14，15，16的任务。合理规划，每阶段完成后再继续auto-decompose下一阶段，每阶段完成都要提交push。直到任务完成。"

This run = **Step 14 only** (exception handling). Step 15-16 follow. Step 14 is
committed AND pushed after review clean (user pre-authorized commit+push/phase).

Prior state (committed + pushed): Steps 11, 12, 12b, 13. HEAD=`9e71caf2`. NeoStep
smoke baseline = 49/49.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose.** One coherent slice (try/catch/finally in the Neo
interpreter). Pipeline: propose → apply → verify → review-loop → ship → archive
→ (LEAD commits + pushes).

## 3. Step 14 scope (from `.trae/documents/neo-implementation-steps.md` §"Step 14")
Goal: full try/catch/finally support in the Neo interpreter.
Content:
1. The outer C# try-catch in the execute loop stays as-is.
2. On catch-handler entry: restore mStack — `mStack.Count = frameRefBase + method.TotalRefSize`.
3. Exception object storage: write the exception into the catch block's
   pre-allocated ref slot.
4. Frames stack maintenance: do NOT Pop on unhandled exception; `HandleException`
   finds the matching handler then batch-Pops.
5. `Leave` / `Endfinally` instructions.
6. Reuse the core `GetCorrespondingExceptionHandler` logic.

Dependency: Step 8 (method-calling ability, to test cross-frame exceptions).

Validation: basic try-catch (`try { throw ...; } catch (Exception e){...}`);
try-finally (finally always runs); nested try-catch; cross-method exception
propagation (callee throw → caller catch); access the exception object in catch.

## 4. RESEARCH REQUIRED (planner — be thorough)
- Current Neo exception state: grep `Leave`, `Endfinally`, `Endfilter`,
  `Exception`, `HandleException`, `GetCorrespondingExceptionHandler`,
  `Throw`/`Rethrow` in `ILIntepreter.Neo.cs` and the Legacy `ILIntepreter.Register.cs`
  (the Legacy implementation is the reference — Neo should mirror its handler
  logic). Find what throws NotImplemented (Step-tagged) vs what exists.
- The Legacy exception model: `ILIntepreter.Register.cs` `ExecuteR` has the
  full try/catch/finally/Leave/Endfinally + `HandleException` +
  `GetCorrespondingExceptionHandler` implementation. Step 14 ports the SEMANTICS
  to `ExecuteNeo` (the byte* frame model). Read the Legacy path carefully — it
  is the spec.
- The Neo frame model differences that affect exception handling: `byte* frameBase`,
  `AutoList mStack`, `frameRefBase`, the frames stack (`frames`/`CallStack`), how
  `ExecuteNeo` is re-entered (the outer C# try-catch), how unhandled exceptions
  propagate up the Neo call stack. mStack restore on catch entry must use the
  Neo frame's ref layout.
- `Throw` opcode: how is `throw` lowered today? `throw new Exception()` needs
  newobj — determine whether CLR newobj (for `System.Exception`) is available
  (Step 8b/18). If `throw new CLRException()` is NOT yet achievable, the test
  suite must use a pre-constructed exception or `throw` of an existing object,
  OR Step 14 may need to scope around this. **Determine the throw/newobj
  dependency precisely** — it bounds what's testable.
- The exception-handler table: how IL exception handlers (try/catch/finally
  ranges, filter, type) are stored on the method (`MethodDefinition.ExceptionHandlers`,
  `ILMethod.ExceptionHandlerInfo` or similar) and how `GetCorrespondingExceptionHandler`
  maps a thrown exception type + current ip to a handler.

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new runtime code behind
  `#if ENABLE_NEO_MODE`. Do NOT touch Legacy (`ExecuteR`) or mix object models —
  Legacy is the REFERENCE to mirror, not to modify.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 49/49; your NeoStep14 cases add, NO existing case regresses; NOTE: previously-failing tests that use try/catch may now turn GREEN — expected improvement). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoStep14Test.cs`, ASCII. Cover: basic try-catch; try-finally (finally runs); nested try-catch; cross-method propagation; catch-object access. **NOTE the throw/newobj dependency** — if `throw new T()` isn't achievable, design tests around what IS (e.g. catch a DivideByZeroException from `int x=1/0`, which doesn't need newobj; or a pre-thrown exception). Avoid throw-asserting tests that need unimplemented opcodes.
- Unimplemented-op NIE (Step-tagged) = TODO not bug, but your exception paths must not throw those for implemented cases.
- Test >10s = infinite loop — kill and investigate (exception unwinding bugs can loop).
- **REGRESSION CAUTION:** exception handling touches the core execute loop + frames stack; a bug can corrupt frame/mStack state for ALL calls. Full NeoStep smoke is the gate.
- **CJK write caveat:** Write corrupts ~0.5% CJK on large payloads. Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-step14/`:
- `proposal.md` — Why / What Changes / Impact. State the throw/newobj dependency finding and what's testable.
- `design.md` — concrete, code-grounded: how ExecuteNeo handles a thrown exception (the outer C# try-catch interaction); mStack restore on catch entry (`mStack.Count = frameRefBase + method.TotalRefSize`); exception-object storage in the catch handler's ref slot; frames-stack discipline (no Pop on unhandled; batch Pop in HandleException); `Leave` (jump out of try/catch/finally with correct mStack/finally semantics) and `Endfinally` opcode arms; reuse/port of `GetCorrespondingExceptionHandler`; nested + cross-frame propagation; finally-guaranteed-execution; the Throw opcode path + newobj dependency. Edge cases (exception in finally, rethrow, filter blocks — scope honestly), non-goals (async exceptions = Step 20).
- `specs/<capability>/spec.md` — ADDED requirements, fresh. Suggested capability `neo-exceptions` (new).
- `tasks.md` — checkbox tasks for one implementer pass.

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

## 8. Planner findings (propose stage, 2026-07-04)

### 8.1 Shared exception engine is already in place (good news)
`ExecuteNeo` ALREADY wraps the dispatch switch in a per-iteration C# try-catch
and calls the SHARED `HandleException` (`ILIntepreter.Neo.cs:2025-2058`). The
shared `HandleException` / `GetCorrespondingExceptionHandler` /
`FindExceptionHandlerByBranchTarget` live in `ILIntepreter.cs` (NOT in
Register.cs) and are reused UNMODIFIED by Neo. The nearest-match handler
selection, the finally/fault entry (`finallyEndAddress = -1`), and the frame-pop
loop are all there. So Step 14 is mostly OPCODE ARMS + cross-frame plumbing, NOT
a from-scratch engine port.

### 8.2 What is LIVE vs NIE today (Neo)
- LIVE: outer try-catch + `HandleException` call (`2025-2058`); `isCatch`
  mStack truncation to `frameRefBase + totalRefSize` (`2033-2037`); per-frame
  `finallyEndAddress` / `lastCaughtEx` / `ehs` locals (`360-362`).
- NIE (fall to `default` -> `NotImplementedException("...(Step 6)")` at `2021`):
  `Throw`, `Leave`, `Leave_S`, `Endfinally`, `Rethrow`, `Endfilter` -- NO arms
  exist. These are the opcode arms Step 14 adds.
- TODO gap: catch-object write into the catch handler's ref slot
  (`2038 // TODO: write exception object into the catch handler's slot (Step 14)`).

### 8.3 THROW / NEWOBJ dependency (KEY -- bounds testability)
`throw new System.Exception(...)` and `throw new` of ANY CLR exception type are
NOT testable under Neo this step: the Neo `Newobj` arm throws
`NotImplementedException("Neo Newobj CLR type is not implemented (Step 9)")`
for non-IL targets (`ILIntepreter.Neo.cs:1411-1413`). IL-type newobj IS
available (`1411` IL branch). Testable throw sources that need NO newobj:
(1) `int x = 1/0;` -> host raises `DivideByZeroException` inside the execute
loop (primary vector, already used by NeoStep6/12b);
(2) null IL-instance field access -> `NullReferenceException` from
`GetNeoILInstance` (`2085-2088`); (3) `rethrow` of a caught object. All Step 14
tests must use these sources. `throw new T(...)` for CLR T is deferred to the
CLR-newobj step.

### 8.4 Cross-frame propagation BUG (highest-risk work item)
Today every Call/Newobj/Callvirt arm does
`if (!InvokeNeoCallTarget(..., out unhandledException)) return null;` on
callee-unhandled (`1396-1397, 1433-1434, 1457, 1503, 1527`). `return null`
bypasses BOTH (a) the caller's outer try-catch -- so the caller's enclosing
catch never sees the propagated exception -- AND (b) the bottom-of-method
cleanup (`2062-2071`: frame pop + mStack truncate to frameRefBase) -- leaking
the caller's frame on the frames stack and its mStack reservation. Fix
(design.md section 7): extend callee->caller return to carry the pending
exception object; replace each `return null` with re-throwing the pending
exception into THIS frame's outer catch at the call address; ensure the bottom
cleanup runs on ALL exit paths (incl. unhandled re-throw) so mStack/frames stay
consistent. The shared `HandleException` frame-pop loop (`ILIntepreter.cs:4770-
4779`) is the batch-pop authority and works on Neo frames because each Neo
frame sets `BasePointer`/`ManagedStackBase` (`350-353`).

### 8.5 JIT lowering (confirmed)
Neo body = `(OpCodeR[])frame.CodeBody.Clone()` (`JITCompiler.cs:465`) -- the
SAME lowered body as Legacy register mode. `Code.Throw` -> `OpCodeREnum.Throw`
with `op.Register1 = --baseRegIdx` (`JITCompiler.cs:1860-1862`) -- so the throw
register-1 is the exception object's ref slot (byte offset into frameBase
holding the mStack index). `Leave`/`Leave_S`/`Endfinally`/`Rethrow` pass
through with their enum unchanged and `ip->Operand` = leave target
(`JITCompiler.cs:1907-1912`). `ehs = method.ExceptionHandlerRegister` is
populated identically for Neo and Legacy (`ILMethod.cs:721-761`).

### 8.6 Non-goals (explicit)
IL `filter` / `Endfilter` (rare in C#; stays NIE, Step-tagged). async exception
propagation (Step 20). CLR newobj (Step 18). stack-overflow guard
(`ILIntepreter.Neo.cs:324`; Step 26). ANY Legacy/shared-engine modification.

### 8.7 Validation gate
Full NeoStep smoke (was 49/49 after Step 13). New `TestCases/NeoStep14Test.cs`
adds TC1-TC8 (basic catch, catch-object access, finally-on-exception,
finally-on-Leave, nested innermost-wins, cross-frame catch, cross-frame caller
finally, NRE catch) -- all via no-newobj sources. Test >10s = infinite loop
(exception-unwind bugs loop). Some previously-NIE try/catch tests may turn
GREEN -- expected improvement, not regression.

### 8.8 Cross-frame propagation: C# throw is the actual vector (design §7 refined)
The design's framing of `return null` as the cross-frame bug turned out to be
a DEBUGGER-ONLY path. In non-debug runs the Neo callee's unhandled path
ALREADY throws (`ILRuntimeException` wrapping the original), and `ExecuteNeo`
propagates that C# exception straight through `InvokeNeoCallTarget` (which
throws, so the caller's `if (!InvokeNeoCallTarget(...)) return null;` is never
evaluated) into the caller's per-iteration catch. There, `HandleException`
searches the CALLER's `ehs` at the call-site address and its frame-pop loop
(4770-4779) cleans up any intervening callee frame via `PopFrame` (which also
truncates mStack to the popped frame's `ManagedStackBase`). So functional
cross-frame propagation worked BEFORE Phase 4 (TC6/TC7 passed green with only
Phases 2-3 in place). The `return null` sites are reachable ONLY when the
debugger's `DebugService.Break` returns true (callee swallows into the bool
and returns normally) -- a debugger-mode edge case, left as the pre-existing
safety net. The real Phase 4 work was making the unhandled path
SELF-CLEANING (8.10) so the callee does not leak its frame/mStack reservation,
rather than relying on the caller's frame-pop loop.

### 8.9 Catch-exception slot requires a JIT annotation (NOT a runtime formula)
The naive runtime formula `catchReg = localInfos.Length - StackRegisterCount`
is WRONG. Reason: the catch handler's exception variable lives in temp
register 0 (baseRegStart = paramCnt + locCnt), bumped by `IsCatchHandler`
(JITCompiler.cs:223-224), but NO opcode ever explicitly references that
register (the caught object is placed by the runtime, not the IR). So
`Optimizer.CleanupRegister` (Optimizer.RegisterCleanup.cs) deems it unused and
COMPACTS IT AWAY when the catch body does not read `e` (e.g. `catch {...}`
with no `e`). Consequence: `localInfos` has NO entry for the catch exception
register unless the catch body uses `e` -- which is why TC1 (no `e`) threw
`IndexOutOfRangeException` while TC2 (uses `e`) passed, before the fix. Fix
(JIT-side, Neo-only, Legacy untouched):
  - `CleanupRegister` gained a `short protectedReg` + `out short
    protectedRegFinalIndex` (no default -- single caller). When protected, the
    register is added to `usedRegisters` so it is never removed; its final
    (post-compaction) index = protectedReg - (unused registers below it).
  - `JITCompiler.Compile` computes `neoCatchExReg = baseRegStart` (only when
    Neo + the method has a Catch handler; `#if ENABLE_NEO_MODE`) and passes it.
  - `CompiledFrame` gained 3 Neo fields: `NeoCatchExceptionRegIndex`,
    `NeoCatchExceptionByteOffset`, `NeoCatchExceptionRefOffset`. Stamped in
    `AllocateLocalStackSpaces` from `localInfo[NeoCatchExceptionRegIndex]`.
  - `ExecuteNeo`'s `isCatch` branch writes `ex` into
    `mStack[frameRefBase + NeoCatchExceptionRefOffset]` and stores that index
    at `frameBase + NeoCatchExceptionByteOffset`. Methods with no catch handler
    have the offsets = -1 (write skipped; `isCatch` can't be true then).
Methods with exception handlers are never inlined (the inliner excludes them,
JITCompiler.cs:2466), so the annotation is stable. Catch body `e` references
and the protected register get the SAME compaction shift, so they stay aligned.

### 8.10 Throw register resolution; self-cleaning unhandled path
- `Throw` is NOT in the `LowerNeoOffsets` switch (falls to `default: handled =
  false`), so at runtime `ip->Register1` is a RAW register index, not a byte
  offset. Resolve via `localInfos[ip->Register1].Offset` (the byte slot holds
  the mStack index of the exception object), exactly like `Ret` reads its
  source via `ip->DstOffset`. `GetNeoException(mStack, idx)`: idx<0 or not an
  Exception -> NullReferenceException (throwing null is an NRE in the CLR);
  else return the object as-is (HandleException unwraps ILRuntimeException
  downstream).
- Self-cleaning: `ExecuteNeo` now stashes the to-be-thrown exception in a
  `pendingThrow` local (declared before the `fixed` block), breaks the loop,
  lets the bottom cleanup (frame pop + mStack truncate to `frameRefBase`) run,
  and re-throws `pendingThrow` AFTER cleanup (outside `fixed`). This makes
  every Neo frame self-cleaning on the unhandled path; the caller's
  HandleException frame-pop loop finds the caller's frame on top (callee
  already popped + mStack-released) and does no callee cleanup.

### 8.11 Test adaptations (isinst is Step 15)
`isinst`/`castclass` are NOT implemented (Step 15), so `e is
DivideByZeroException` is not expressible. All Step 14 catch-type matching is
done by the shared `GetCorrespondingExceptionHandler` (via
`CheckExceptionType`), so tests use specific `catch (DivideByZeroException)`
/ `catch (NullReferenceException)` clauses. Catch-object access (TC2) verifies
the object is non-null and reachable (`e != null`) rather than an `is`-check.
NRE source (TC8): `NeoStep14Holder h = null; int v = h.value;` -- ldfld on a
null IL instance raises NRE via `GetNeoILInstance`. An extra TC9 (rethrow of a
caught DivideByZero) was added beyond the spec's TC1-TC8 list. All 9 green.
Final FULL NeoStep smoke = 58/58 (49 baseline + 9 new), no regression.

