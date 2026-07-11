# Review Report — neo-step25-runtime-loader (Step 25 S1: runtime `.neo` loader)

> Adversarial non-author code review (reviewer != implementer). SCOPE: the S1
> `.neo` -> ILMethod loader + the ILMethod AOT-init dual-path (SHARED) + the V2
> functional self-check. Verdict, findings, and the load-bearing proofs below.
>
> Method: read the design -> get the diff -> hand-verify the EH -1 conversion
> against `InitCodeBody` -> construct 5 ADVERSARIAL probes (written temporarily,
> verified, REMOVED before ship) -> run the gates -> restore -> write this report.

## VERDICT: APPROVE

- **Blockers: 0 open.**
- **Majors: 0 open.**
- **Minors: 2** (both low-risk robustness items for S2/S3; NEITHER exercised in S1).
- **Trivials: 2** (doc-only).
- **Accepted-known deferrals: 7** (the S2/S3 scope items; correctly recorded).

The change is additive, Neo-only, Legacy-neutral, and the V2 functional capstone
is SOUND. The single most important property -- "is the AOT body ACTUALLY used,
or does the JIT body still run (so A==B trivially)?" -- is proven DEFINITIVELY by
an adversarial body-mutation probe (Probe A): the deserialized body was mutated
so its result would differ from JIT; the post-Attach run returned the MUTATED
value, proving `ExecuteNeo` ran the AOT body. A green smoke + the implementer's
self-check do NOT prove this on their own (Step 23 already established AOT body
== JIT body byte-for-byte, so A==B could hold even if the JIT body ran); the
mutation probe was required and passed.

## Reproduction environment

- Branch: `features/object-model-overhaul` (HEAD unchanged by the review; all
  temp probes were removed and the tree restored to the implementer's state).
- Build: `dotnet build ILRuntimeTestCLI -c Debug_Neo` (0 errors); `dotnet build
  TestCases -c Debug` (0 errors). Plain `Debug` CLI build: 0 errors (Legacy).
- Gates (reproduced by the reviewer, AFTER restoring the tree):
  - `NeoStep` smoke: **208/208, 0 failed.**
  - `NeoStep25LoadExec` (V2 capstone): **8/8 cells, 0 failed; 5 attached, 0 skipped.**
- Adversarial suite (temp): **5/5 cells passed** (see Load-bearing proofs).

## The diff reviewed

- `ILRuntime/CLR/Method/ILMethod.cs` (MODIFIED, +172) -- SHARED, the riskiest
  part: the `isNeoAotBody` flag, `InitCodeBodyFromNeo`, `RebuildSwitchTargets`,
  `RebuildNeoCallParams`, `RebuildEHFromNeo`, and the `BodyRegister` getter
  short-circuit. All `#if ENABLE_NEO_MODE`.
- `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (NEW) -- `Attach` + `MatchMethod`
  + `ResolveTypeRefToIType` + `NeoLoadReport`. Neo-only.
- `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs` (NEW) --
  the V2 capstone self-check. `#if ENABLE_NEO_MODE && DEBUG`.
- `ILRuntimeTestCLI/Program.cs` (MODIFIED, +25) -- the `NeoStep25LoadExec` hook.
- `TestCases/NeoStep25LoadProbe.cs` (NEW) -- the non-generic probe type.
- `.trae/documents/neo-deferred-items.md` (MODIFIED, +1 row) -- the STEP-25-PARTIAL
  row recording S2/S3 deferrals + the Run-shim prerequisite.

## Load-bearing proofs (all VERIFIED)

### 1. The AOT body is ACTUALLY executed (not a JIT fallback) -- the crux

**Concern.** Step 23 proved AOT body == JIT body byte-for-byte. So a green "JIT ==
AOT" check passes trivially EVEN IF `ExecuteNeo` still ran the JIT body (the AOT
wiring a no-op). The implementer's self-check asserts `ilm.isNeoAotBody` is set
post-Attach (line 173), which proves the FLAG is set but NOT that the AOT body
runs -- the flag only short-circuits `BodyRegister`, while `ExecuteNeo` reads
`method.CompiledFrame.NeoExecuteBody` (`ILIntepreter.Neo.cs:840`), a different
field.

**Adversarial Probe A (body mutation -- definitive).** A temp probe method
`ConstProbe() { return 1234567; }` was compiled to a `.neo`, read back, and its
deserialized `NeoExecuteBody` was MUTATED in place before Attach: the single
`Ldc_I4` with `Operand == 1234567` was rewritten to `7654321`. After Attach, the
method returned **7654321** -- the MUTATED value, NOT the JIT value (1234567). If
the JIT body had still been running, the result would have been 1234567. Therefore
`ExecuteNeo` ran the deserialized AOT body. **PASS.** (Probe then removed.)

**Adversarial Probe B (reference equality -- structural corroboration).** After
Attach, `object.ReferenceEquals(ilm.CompiledFrame.NeoExecuteBody, rec.NeoExecuteBody)`
holds -- the array `ExecuteNeo` reads IS the deserialized record's array (the
direct reference assignment in `InitCodeBodyFromNeo`). **PASS.**

### 2. The ILMethod dual-path is Legacy-neutral (CRITICAL -- ILMethod is SHARED)

The `BodyRegister` getter is the load-bearing seam. Verified:

- (a) `isNeoAotBody` defaults `false` (the field has no initializer; C# default).
- (b) Under plain `Debug` (`ENABLE_NEO_MODE` undefined), the getter compiles to
  exactly `if (bodyRegister == null) InitCodeBody(true); return bodyRegister;` --
  byte-identical to before. The flag field, the short-circuit, and
  `InitCodeBodyFromNeo` are all inside `#if ENABLE_NEO_MODE` and compile out.
- (c) Plain-`Debug` CLI build: 0 errors. Plain-`Debug` CLI launches and runs the
  harness loop without crashing.

A Legacy regression from this change is impossible by construction: the emitted
ILMethod IL under plain `Debug` is unchanged. **PASS (proven by construction +
clean build + launch).**

### 3. The EH rebuild -1 conversion (inclusive/exclusive)

Hand-verified against `InitCodeBody` (`ILMethod.cs:807-830`):

| Edge | InitCodeBody (Cecil/JIT) | RebuildEHFromNeo (AOT) | Match |
|---|---|---|---|
| TryStart | `addr[eh.TryStart]` | `r.TryStartIdx` (raw) | yes (writer stored `addr[...]`) |
| TryEnd | `addr[eh.TryEnd] - 1` (always; Cecil TryEnd non-null) | `r.TryEndIdx >= 0 ? r.TryEndIdx - 1 : bodyLen-1` | yes |
| HandlerStart | `addr[eh.HandlerStart]` | `r.HandlerStartIdx` (raw) | yes |
| HandlerEnd | `eh.HandlerEnd != null ? addr[eh.HandlerEnd] - 1 : Instructions.Count - 1` | `r.HandlerEndIdx >= 0 ? r.HandlerEndIdx - 1 : bodyLen-1` | yes (see note) |

The writer's `AddrOf` returns `-1` for a null Cecil instruction, so a null
`HandlerEnd` (the last handler) becomes `-1` in the record, and the rebuild
falls back to `bodyLen - 1` (= `NeoExecuteBody.Length - 1`, the TRUE last
OpCodeR index). InitCodeBody uses `def.Body.Instructions.Count - 1` (the CIL
count) for the same case. These CAN differ when the JIT expands CIL ops, but the
rebuild's `bodyLen - 1` is the more-correct value (always >= any valid `ip`, so
the inclusive `ip <= HandlerEnd` check holds). Not a finding -- the AOT path
matches or improves the JIT path; the JIT path's behavior is pre-existing and
unchanged.

The `TryCatchProbe` cell (V2) exercises Catch; **adversarial Probe C (try/finally)**
exercises the `Finally` branch of the rebuild: AOT==JIT==15. **PASS.** Both
exercised EH handler types are correct.

### 4. The NeoCallParamMap rebuild (PrimitiveByRefElemType aqnames)

The rebuild resolves each byref param's CLR `System.Type[]` element types back
from aqnames via `NeoAssemblyReader.ResolveAqName`. The V2 `MixedLocalsProbe`
exercises an `int` byref. **Adversarial Probe D (long byref)** -- a
`BumpRefLong(ref long)` internal call -- exercises a DIFFERENT element type
(long aqname): AOT==JIT==300. The write-back is observable and matches JIT.
**PASS.** The rebuild is correct for at least int and long element types.

### 5. The loader Attach miss-handling (additive contract)

**Adversarial Probe E (skip-keeps-JIT).** A record's `MethodRefIdx` was
corrupted to `-1` (out of range) before Attach. The loader recorded the skip
("bad MethodRefIdx"), the method's `isNeoAotBody` stayed `false`, and it still
ran via the JIT path (returned 61, the correct JIT result). **PASS.** Skipped
methods keep JIT cleanly; the loader never aborts on a miss. Generic instances
(`IsGenericInstance`) and generic definitions (`GenericParameterCount > 0`) are
also skipped defensively. (Generic defs are absent from `MethodDefs` by the
Step-24 partition, so those skip branches are defensive dead code in practice --
correct, just unreachable through the normal compile path.)

### 6. The Step-6 Run-shim parameterless-only limitation -- correctly scoped

CONFIRMED: the V2 probe is parameterless by design because the Neo host entry
`ILIntepreter.Run(ILMethod, object, object[])` is a Step-6 shim that does not
marshal `object[] p` into the frame. This is a GENUINE S2/S3 prerequisite (a
fuller Run entry or an internal-call-driven invocation for parametrized +
CLR-heavy-catch host-side coverage), NOT a gap in S1: the EH rebuild, the
NeoCallParamMap rebuild, and the AOT body execution are ALL proven (Probes A-D)
within the parameterless boundary (inputs baked as locals; byref exercised via
internal calls like `BumpRef`). The limitation is correctly recorded in
`neo-deferred-items.md` (STEP-25-PARTIAL row) and `planning-context.md`.
Accepted-known.

### 7. Additive + the JIT path intact

- NeoStep smoke: 208/208 (reviewer-reproduced). ZERO regressions vs the HEAD
  baseline (the loader is a new path; the AOT-init defaults off).
- Probe E (above) independently confirms a non-attached method runs via JIT
  byte-identically.
- NeoStep22SelfCheck (55/55), NeoStep23Roundtrip (15/15), NeoStep24CliRoundtrip
  (5/5) per the implementer (unchanged AOT-format/toolchain).

## Findings

### Minor-1 -- EH rebuild `default` branch silently misclassifies Filter as Catch
- **File:line:** `ILRuntime/CLR/Method/ILMethod.cs` -- `RebuildEHFromNeo`, the
  `default:` arm of the `switch ((ExceptionHandlerType)r.HandlerType)`.
- **Observation:** The `default` arm sets `e.HandlerType = ExceptionHandlerType.Catch`
  (silent misclassification) with a comment "Filter is not supported by
  InitCodeBody either (it throws NIE for the default case)." `InitCodeBody`'s
  default arm `throw new NotImplementedException()`. So the AOT path is more
  lenient than the JIT path: a serialized Filter EH (or any unknown HandlerType)
  would be misclassified as Catch and silently mis-dispatch, where the JIT path
  would throw.
- **Risk:** LOW. Filter EH is not exercised in S1 (the probe uses Catch only;
  Probe C added Finally). The Step-24 NeoCompiler partition would have to emit a
  Filter EH for this to matter. But the silent-misclassify is slightly worse than
  throw-on-unknown.
- **Recommended fix:** In the `default` arm, throw `new NotImplementedException(
  "Neo AOT EH HandlerType=" + r.HandlerType)` to match `InitCodeBody`'s
  fail-loud behavior (or at minimum set `CatchType = null` + log). Trivial change;
  aligns the two paths.
- **Severity:** Minor (not exercised in S1; robustness for S2/S3 if Filter EH
  shapes ever get AOT-compiled).

### Minor-2 -- NeoCallParamMap element-type null-on-miss can NRE later
- **File:line:** `ILRuntime/CLR/Method/ILMethod.cs` -- `ResolveElemTypesFromNeo`
  (returns `null` element on a `ResolveAqName` miss; no try/catch).
- **Observation:** `ResolveAqName` (Step 23) returns `null` when
  `Type.GetType(aqname)` misses (e.g. a CLR byref element type from an assembly
  not loaded in the host). The rebuilt `NeoCallParamMap.PrimitiveByRefElemType`
  then carries a `null` slot, which could surface as an NRE in the runtime's
  byref copy/write-back. The sibling catch-type resolver (`ResolveTypeRefToIType`
  in the loader) DOES wrap `GetType` in try/catch; the element-type resolver does
  not.
- **Risk:** LOW. Probes D (long) + the V2 int byref prove the resolvable cases
  (BCL primitives). A miss requires a CLR element type from an unloaded assembly
  -- a cross-AppDomain / host-registration concern that S3 owns (the
  `TestCLREnum` gap family).
- **Recommended fix:** Either (a) wrap the per-element resolve in try/catch and
  skip-by-null with a skip-report (mirroring the catch-type resolver), or (b)
  record the miss in the `NeoLoadReport` so S2/S3 has visibility. Not blocking.
- **Severity:** Minor (BCL primitives resolve; CLR-from-unloaded-assembly is an
  S3 concern).

### Trivial-1 -- probe comment has a stale expected value
- **File:line:** `TestCases/NeoStep25LoadProbe.cs:25` (comment).
- **Observation:** The header comment says "the self-check pins (47 / 103 / 61)"
  but `TryCatchProbe` returns 100 (not 103). The self-check cells correctly use
  100 (`NeoStep25LoadExecCheck.cs:98`). Doc-only; the code is correct.
- **Recommended fix:** s/103/100/ in the comment.
- **Severity:** Trivial (doc).

### Trivial-2 -- BumpRef is attached but not a harness test (clarify the count)
- **File:line:** `openspec/changes/neo-step25-runtime-loader/planning-context.md`
  (Findings -- apply: "5 attached (the 3 probes + BumpRef + the implicit .ctor)").
- **Observation:** The "5 attached" is correct and the breakdown is accurate, but
  worth making explicit that `BumpRef(ref int)` carries a byref param (so it is
  NOT a parameterless harness test and is not counted in the 208 NeoStep smoke),
  yet it IS a non-generic method on the probe type -> compiled into the `.neo` ->
  attached by the loader. The +3 in the NeoStep count (205->208) is exactly the 3
  parameterless probe methods; BumpRef/.ctor are attached but not harness-run.
- **Severity:** Trivial (doc clarity; no behavior impact).

## Accepted-known deferrals (NOT findings; correctly recorded)

These are the documented S2/S3 scope items, recorded in the proposal's deferral
table, `planning-context.md`, and the `STEP-25-PARTIAL` row of
`neo-deferred-items.md`. The reviewer confirms they are correctly scoped (the S1
deliverables -- the ILMethod dual-path, the loader, the V2 capstone -- are all
complete and proven):

1. **S2** -- Generic-instantiation-at-load (`CloneAndPatch` from a `.neo`
   `NeoTemplateRecord` into an AOT ILMethod's `BodyRegister`).
2. **S3** -- Full ILType Cecil-decoupling (ILType AOT-init from
   `NeoTypeDefRecord`; load into a Cecil-free AppDomain).
3. **S3** -- Cross-AppDomain token hash re-resolution (Approach 1: record the
   compile-time `GetHashCode()` per TypeRef/MethodRef/String entry; Approach 2
   name-based hash and Approach 3 body-rewrite correctly REJECTED).
4. **S3** -- Static `.cctor` seeding via the `.neo` `StaticCtorMethodRefIdx`.
5. **S3** -- Full CLR aqname indexing + host-CLR-assembly registration (the
   Step-24 `TestCLREnum` gap; pairs with the cross-AppDomain work).
6. **Step 26** -- Perf benchmarks.
7. **S2/S3 prerequisite** -- A fuller Neo `Run` entry (marshal `object[] p` +
   allocate the full frame ref region) so a stronger V2 can exercise parametrized
   + CLR-catch-shape coverage host-side. (The Step-6 shim is parameterless-only;
   the S1 V2 probe is parameterless by design within this boundary.)

## Notes for the LEAD

- The "is the AOT body actually used" proof (Probe A) is the review's load-bearing
  result. The implementer's self-check (pinning expected values + the
  `isNeoAotBody` flag assertion) is good but INSUFFICIENT on its own -- the
  mutation probe was the decisive evidence. **Recommend the LEAD consider folding
  a body-mutation cell (mutate a deserialized constant, assert the mutated value)
  into the permanent `NeoStep25LoadExecCheck` matrix** so this property stays
  guarded regressively as S2/S3 evolve the dual-path. (Out of scope for this
  review's verdict, but high value; the Step-23/24 reviews adopted a similar
  "keep the adversarial probe" pattern.)
- The EH rebuild is proven for Catch + Finally; nested EH and typed-catch (a
  specific catch type, not `catch (Exception)`) are not explicitly stressed.
  Low risk (the -1 conversion is handler-type-independent), but a typed-catch
  cell would also be a worthwhile permanent addition.
- No SHARED-engine regression: ILMethod is byte-identical under plain `Debug`
  (proven by construction); the Neo gates are green; the Legacy launch is clean.
