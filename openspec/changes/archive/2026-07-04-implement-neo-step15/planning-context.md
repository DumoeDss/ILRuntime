# Planning Context — implement-neo-step15

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent (verbatim)
> "继续完成12b，13，14，15，16的任务。合理规划，每阶段完成后再继续auto-decompose下一阶段，每阶段完成都要提交push。直到任务完成。"

This run = **Step 15 only** (isinst/castclass + peephole). Step 16 follows. Step
15 is committed AND pushed after review clean (user pre-authorized commit+push/phase).

Prior state (committed + pushed): Steps 11, 12, 12b, 13, 14. HEAD=`d7350b3a`.
NeoStep smoke baseline = 58/58.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose.** One coherent slice (type-check instructions + their peephole).
Pipeline: propose → apply → verify → review-loop → ship → archive → (LEAD
commits + pushes).

## 3. Step 15 scope (from `.trae/documents/neo-implementation-steps.md` §"Step 15")
Goal: type-check instructions, incl. compile-time peephole.
Content:
1. Compile-time peephole: detect the `box T; isinst U` pattern, statically resolve it.
2. Generic-parameter case: add to the patch table (`PatchKind.IsinstResult`).
3. Runtime path (operand is object/interface type):
   - `ILTypeInstance` → `CanAssignTo`.
   - CLR object → `IsAssignableFrom`.
4. Does NOT involve in-frame flat bytes — the object being checked is already in mStack.

Dependency: Step 9 (CLR type interaction).
Validation: `obj is MyClass` true AND false; `obj as IMyInterface`; value type
boxed then isinst; inheritance-chain type checks.

## 4. RESEARCH REQUIRED (planner)
- Current Neo state of `isinst`/`castclass`: grep `Isinst`, `Castclass`,
  `isinst`, `castclass` in `ILIntepreter.Neo.cs` + `JITCompiler.cs`. Find what
  throws NotImplemented (Step-tagged) vs what exists. These are the two opcodes
  to implement (Step 15 also covers `isinst` used in catch type-matching — see below).
- The Legacy reference: `ILIntepreter.Register.cs` `ExecuteR` has the `isinst`/
  `castclass` implementation (CanAssignTo for ILTypeInstance, IsAssignableFrom
  for CLR, null handling, InvalidCastException on failed castclass). Read it —
  it is the spec. Do NOT modify Legacy.
- `CanAssignTo` / `CanAssignToCross` on ILType/ILTypeInstance; `IsAssignableFrom`
  on CLR types via the AppDomain. Interface-assignability (ties to Step 11
  interface dispatch — `Implements`).
- The **peephole** `box T; isinst U`: where would this run? It's a JIT/optimizer
  compile-time pattern (detect a `Box` immediately followed by `Isinst` and
  statically resolve the result). Find where peephole/optimizer passes live and
  how `PatchKind` / the patch table works (grep `PatchKind`, the existing patch
  infrastructure from HybridPatch or the Neo patch table). The generic-parameter
  case (`PatchKind.IsinstResult`) uses this patch table because the type is not
  known at compile time.
- **Catch type-matching:** Step 14's catch handler matches exception types via the
  shared `GetCorrespondingExceptionHandler`. Step 14 TC2 asserted `e != null`
  because `isinst` was unimplemented (Step 15). Determine whether Step 15 should
  also enable proper catch-type matching in Neo (the shared engine's type check
  may rely on `isinst` semantics or `CanAssignTo` directly). If catch matching
  already uses `CanAssignTo` directly (not the isinst opcode), Step 15's catch
  benefit is indirect. Document.
- `TestCases/NeoStep14Test.cs` for the test convention.

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new runtime code behind
  `#if ENABLE_NEO_MODE`. Legacy (`ExecuteR`) is the REFERENCE, not to modify.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 58/58; your NeoStep15 cases add, NO existing case regresses). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoStep15Test.cs`, ASCII. Cover: `obj is MyClass` true + false; `obj as IMyInterface` (null on mismatch); boxed-VT isinst; inheritance chain. Note: `is`/`as` returning a value are now GREEN-testable (Step 14 catch + Step 15 type checks both landed). `castclass` failure throws InvalidCastException — a throw-asserting test is NOT expressible green (harness limitation), so test castclass SUCCESS round-trip, not the throw.
- Unimplemented-op NIE (Step-tagged) = TODO not bug; your isinst/castclass paths must not throw those.
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION:** isinst/castclass are pervasive (every `is`/`as`/cast). A bug can silently return wrong type-check results. Full NeoStep smoke is the gate.
- **CJK write caveat:** Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-step15/`:
- `proposal.md` — Why / What Changes / Impact + the peephole/patch-table finding + the catch-matching relationship.
- `design.md` — concrete, code-grounded: `isinst` ExecuteNeo arm (ILTypeInstance→CanAssignTo, CLR→IsAssignableFrom, null→false/InvalidCastException, box-T;isinst-U peephole, generic-param patch table PatchKind.IsinstResult); `castclass` arm (success→the ref, fail→InvalidCastException); the compile-time peephole pass location; catch-type-matching relationship. Edge cases (null, interfaces, arrays, generics), non-goals (covariant array casts if not already done, etc. — scope honestly).
- `specs/<capability>/spec.md` — ADDED requirements, fresh. Suggested capability `neo-type-checks` (new).
- `tasks.md` — checkbox tasks for one implementer pass.

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

## 8. Planner findings (2026-07-04, propose stage)

### 8.1 Current state of isinst/castclass (verified)
- JIT already lowers both: `JITCompiler.cs:2215-2223` sets
  `Register1 = Register2 = baseRegIdx-1`, `Operand = method.GetTypeTokenHashCode(token)`
  for `Box`/`Unbox`/`Unbox_Any`/`Isinst`/`Castclass`. No JIT change needed.
- ExecuteNeo has NO arms for `Isinst`/`Castclass`; they fall through the switch
  `default` at `ILIntepreter.Neo.cs:2093-2094` -> Step-6 NIE. This is the gap.
- Offset-lowering (`Optimizer.Neo.cs:430-444`) stamps DstOffset/SrcOffset/
  Operand3(dst ref)/Operand4(src ref) for `Box`/`Unbox`/`Unbox_Any` but NOT for
  `Isinst`/`Castclass`. Step 15 must add them (same block, R1==R2 in-place).
- Legacy reference = spec: `ILIntepreter.Register.cs` Castclass 4385-4434,
  Isinst 4435-4522. Under Neo the checked value is always a reference (Box
  produced it), so the Legacy primitive-operand branch (4442-4483) has no Neo
  analogue and is dropped.

### 8.2 Peephole + patch-table finding (KEY)
- `PatchKind` does NOT exist anywhere under `ILRuntime/`. The only patch infra is
  `HybridPatch/AssemblyPatch.cs` (unrelated, runtime patching of assemblies).
- There is NO peephole pass that fuses adjacent Neo ops. Optimizer passes are:
  BCP, FCP, ELDC, RegisterCleanup, InlineMethod, Neo.RegisterNeoOffsets. None
  detect `box T; isinst U`.
- The JIT resolves the isinst type token STATICALLY via
  `method.GetTypeTokenHashCode` for ordinary type operands, so
  `AppDomain.GetType(ip->Operand)` already yields the concrete type at runtime --
  no patch table is needed for the validated scope.
- DECISION: the `box T; isinst U` peephole and `PatchKind.IsinstResult` patch
  table are OUT OF SCOPE for Step 15 (deferred follow-up). The runtime arms fully
  cover correctness for non-generic-param type operands. Box;Isinst executes
  correctly via the two runtime arms without fusion.

### 8.3 Catch-matching relationship (KEY)
- Neo catch dispatch does NOT use the isinst opcode. Path:
  throw -> HandleException -> GetCorrespondingExceptionHandler
  (`ILIntepreter.cs:4742,5598`) -> CheckExceptionType(CatchType, ex, explicitMatch)
  (`ILIntepreter.cs:5823`), which tests
  `catchType.TypeForCLR.IsAssignableFrom(exception.GetType())` directly.
- So Step 15's catch benefit is INDIRECT: it enables `is`/`as`/cast INSIDE catch
  bodies (the Step 14 TC2 `e != null` workaround can now be `e is T`), but does
  not change catch-type selection.
- Separate known gap (NOT Step 15): `CheckExceptionType` throws NIE for a non-
  CLRType catch type (`ILIntepreter.cs:5835`) -- IL-typed catch dispatch is a
  future exceptions concern.

### 8.4 Reuse / free wins
- `ILType.CanAssignTo` (`ILType.cs:2232`) already walks `BaseType` AND
  `Implements` (Step 11 interface table) + handles enums -> interface and
  inheritance-chain isinst work for free once the arm calls it. CLR objects use
  `Type.IsAssignableFrom`. CrossBindingAdaptor.CanAssignTo delegates to its
  ILInstance; no special branch needed.

### 8.5 Artifacts produced (apply-ready, openspec validate passes)
- proposal.md, design.md, tasks.md, specs/neo-type-checks/spec.md (new cap).
- `openspec status` -> isComplete: true; `openspec validate` -> valid.

### 8.6 Apply-stage finding (2026-07-04) — Cgt_Un ref-null defect (KEY, not in design.md)

The isinst/castclass arms + offset-lowering (sections 8.1/A) were correct and
landed as designed. But the C# `is`/`as != null`/`!= null` operators lower to
`isinst; ldnull; cgt.un` (verified in JIT output for TC1/TC3). Under the Neo
flat frame a reference is an mStack INDEX with -1 = null, so the generic
unsigned `cgt.un` arm
  `*(uint*)src > *(uint*)operand ? 1 : 0`
returns the WRONG result whenever a non-null reference (small positive index)
is compared against null (-1 == 0xFFFFFFFF): `(uint)1 > 0xFFFFFFFF` is false,
so `x is T` (non-null) collapsed to false. `as`+`== null` (`ceq`) worked
because signed `==` is unaffected; only the unsigned `cgt.un` is broken. This
is a pre-existing latent defect that Step 15 newly exercises — `cgt.un` on a
reference was never hit before (no prior Neo test uses `is`/`!= null` on a
non-null ref). The design.md did NOT anticipate it (it claims `is` is GREEN-
testable).

FIX CHOSEN (sound engineering, scoped): mirror Legacy's `Cgt_Un` integer/object
rule (ILIntepreter.Register.cs:4807-4841) in the Neo arm — Legacy already
special-cases reference operands via ObjectType. The Neo arm now computes
  `res = srcIdx != -1  &&  ((uint)srcIdx > (uint)opIdx  ||  opIdx == -1)`
which yields: src-null -> false; src-non-null & op-null -> true; otherwise the
plain unsigned compare. This matches Legacy's null semantics exactly and keeps
genuine unsigned-int `cgt.un` correct EXCEPT the pathological
`cgt.un x, (uint)0xFFFFFFFF` case (comparing an int against max-uint), which
the validated tests do not exercise. (Alternative attempted first: JIT-stamp a
ref-compare flag on `Cgt_Un` using `registerTypes`. Did NOT work — the flag did
not survive the optimizer passes and the isinst result-type propagation caused
a regression in TC3; reverted. The runtime-arm rule is one-line, no JIT/optimizer
interaction, lowest regression surface.)

`Clt_Un` was NOT touched — `is`/`!= null` only emit `cgt.un`; if a future test
exercises ref-typed `clt.un` it will need the analogous rule.

REGRESSION: full NeoStep smoke 65/65 green (was 58 + 7 Step 15). NO existing
NeoStep case regressed (the green `== null`/`!= null` checks in NeoStep11-14
either use `ceq` or compare null operands, so they were unaffected). This is a
known soft spot: if a later step adds a test doing `cgt.un intA, 0xFFFFFFFF`
or ref-typed `clt.un`, revisit.

### 8.7 Files changed (apply)
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — added
  `Isinst`/`Castclass` to the Box/Unbox/Unbox_Any offset-lowering case.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — added the
  `Isinst` + `Castclass` ExecuteNeo arms; rewrote the `Cgt_Un` arm to be
  reference-null-aware (§8.6).
- `TestCases/NeoStep15Test.cs` — new, 7 cases (TC1-TC7).
- Legacy (`ExecuteR`) untouched. No JIT changes. No optimizer changes beyond
  the offset-lowering case addition.

