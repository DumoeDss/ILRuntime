# Ship Log — implement-neo-step12b

Neo Step 12b: Move_Vt + LowerMove — whole value-type copy / assignment
semantics for the Neo register VM via a dedicated `Move_Vt` opcode and a JIT
`LowerMove` rewrite pass.

## Ship verdict: CLEAN

0 Blocker / 0 Major / 0 Minor. (2 Trivial non-blocking nits; 2 accepted-known
pre-existing follow-ups — see below.)

## Verification evidence

Builds (per CLAUDE.md subset; the full sln cannot build — VSIX net472 vs
netstandard2.1 NU1201, unrelated):
- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors
  (transitively builds ILRuntime / ILRuntimeTestBase / LitJson).
- `dotnet build TestCases/TestCases.csproj -c Debug` → 0 errors, produces
  `TestCases/bin/Debug/netstandard2.1/TestCases.dll`.

FULL NeoStep smoke (regression gate — whole-VT copy is pervasive):
- Filter `NeoStep`: 41 ran, 0 failed (was 37/37 after Step 12).
- Filter `NeoStep12b`: 4 ran, 0 failed (the +4 new cases for this step).
- Zero existing cases regressed; no Move_Vt path threw a Step-tagged
  NotImplementedException; no test exceeded the 10s infinite-loop watch.

Regression note: VT copy touches every `Vector3 a = b;`, every struct
assignment, and every struct-return-temp, so the FULL NeoStep smoke (not just
the 4 new cases) is the mandatory gate. It is green.

## Review summary

CLEAN — adversarial review by an independent verifier (author != reviewer),
see `review-report.md`. Confirmed:
- LowerMove placement runs in register-index form BEFORE Neo offset-lowering
  (avoids the Step-12 OpCodeR union-alias pitfall).
- Operand encoding uses only standalone (non-aliased) fields; the one aliased
  reuse (`Operand` / Register3 at offset 8) is safe because Move_Vt never
  reads Register3.
- ExecuteNeo CopyBlock + per-ref mStack copy is correct and bounded; null
  convention consistent with the established `CopyFrameToIL` helper.
- The "only refCount>0 dests" gate is sound (pure-primitive VTs correctly keep
  plain `Move`).
- BCP/FCP registration of Move_Vt in all 4 optimizer helpers is complete
  (defensive; BCP/FCP run before LowerMove so never observe Move_Vt).
- 2 Trivial nits (cosmetic local-hoist; dead defensive ternary) — no action.

## Accepted-known follow-ups (PRE-EXISTING, NOT 12b regressions)

Both independently verified pre-existing via a `git stash` baseline probe
(reproduced identically WITH and WITHOUT the 12b change). Neither is fixed by
12b; neither is introduced by 12b. Documented in `planning-context.md`
section 8 (Findings K1/K2) and adjudicated in `review-report.md`.

- K1 — FCP mis-propagates value-type Moves. After `b = a`, FCP rewrites later
  `b.field` reads to `a.field` even after `a.field` is mutated, because FCP's
  kill condition (`Optimizer.FCP.cs`) only checks a whole-register write, not a
  field write via `ldloca; stfld`. Real `b = a; mutate(a); read(b.field)` is
  silently wrong. Pre-existing FCP defect. Out of 12b scope (roadmap 12b:
  "ensure BCP/FCP unaffected" — and 12b leaves FCP untouched). Defer — file
  against the optimizer. The `Move_Vt` mechanism itself is correct.
- K2 — Step 8 VT-by-value parameter copy reads a primitive-field value as an
  mStack index (ArgumentOutOfRangeException at `ILIntepreter.Neo.cs:449`). The
  call param-setup path uses `NeoCallParamMap` / `CopyNeoCallArguments`, NOT
  `Move_Vt`, so 12b is mechanically irrelevant to it. Pre-existing Step-8
  call-lowering bug. Defer against Step 8 / call lowering.

## Files changed

Runtime / JIT (all Neo code under `#if ENABLE_NEO_MODE`; the OpCodeREnum entry
and the 4 Utils switches + ToString are unconditional but are pure additions
with no Legacy behavioral change):
- `ILRuntime/Runtime/Intepreter/RegisterVM/OpCodeREnum.cs` — add `Move_Vt`
  (after `Move`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/OpCode.cs` — add `Move_Vt` to the
  `ToString(AppDomain)` switch.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Utils.cs` — register
  `Move_Vt` in all 4 helpers (`GetOpcodeSourceRegister`,
  `GetOpcodeDestRegister`, `ReplaceOpcodeSource`, `ReplaceOpcodeDest`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` — `LowerMove` logic
  inside `TypeSpecializeNeoOpcodes` `case Move` (rewrite to `Move_Vt` when dest
  is an in-frame IL value type with `TotalReferenceCount > 0`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — extend
  `LowerNeoOffsets` `Move` case to also lower `Move_Vt` (stamp primSize /
  dst RefOffset / src RefOffset / dst RefCount).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — `Move_Vt`
  `ExecuteNeo` arm (CopyBlock + per-ref mStack copy).

Tests:
- `TestCases/NeoStep12bTest.cs` — new, ASCII (4 green tests: pure-primitive
  VT copy, VT-with-one-ref copy, VT-with-many-refs copy [core regression],
  nested-VT copy). Copy-aliasing-independence and VT-by-value-param scenarios
  documented inline as out-of-scope pre-existing bugs (K1/K2).

## Git note

All 12b changes are uncommitted in the working tree on branch
`features/object-model-overhaul`. Per the SHIPPER brief, the LEAD commits and
pushes after this step; no commit is made here. No source edits made during
ship/archive (spec merge + directory move only).

## Stage 2 (archive) outcome

- Spec sync: `openspec/specs/neo-value-types/spec.md` updated — the ADDED
  requirements (whole value-type copy / assignment; LowerMove placement and
  union safety) merged into the existing main spec, U+FFFD-free.
- Archive: change moved to
  `openspec/changes/archive/2026-07-04-implement-neo-step12b/` (`.openspec.yaml`
  and `auto-run.json` moved with it).
- `openspec list` shows no active changes after archive.
