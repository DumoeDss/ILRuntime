# Planning Context — neo-step25-runtime-loader (LEAD seed)

> SEAD for the planner. Read THIS FIRST, then the AOT design + Steps 22-24, then
> research only what is missing. APPEND durable findings.

## User intent

Continue the Neo AOT toolchain. This child = **Step 25**: the runtime `.neo` LOADER
+ ILType/ILMethod Cecil-decoupling dual-path. Deserialize a `.neo` -> ILType/ILMethod
that `ExecuteNeo` runs DIRECTLY (no JIT), decoupled from Cecil. The V2 FUNCTIONAL
capstone (load .neo + execute -> correct result). Capability `neo-optimizer`. Full
autonomy; LEAD commits + pushes.

## What Step 25 delivers (from `.trae/documents/object-model-neo-design.md` §8.3-8.4)

The §8.3 runtime-load flow:
```
runtime loader:
  deserialize type/method/field tables (Step 23 NeoAssemblyReader)
  bind CLR type references (TypeRef -> CLR Type; MethodRef -> CLRMethod)
  method bodies point directly at OpCodeR[] (NO JIT)
  ExecuteNeo() runs directly
```
§8.4 design decisions:
- §8.4.1: refs resolved at LOAD time to direct indices/pointers (no execution-time lookup).
- §8.4.2: generic instantiation via the Step 22 template + CloneAndPatch at load/runtime.
- Plan A (Cecil-decoupling dual-path): redundant fields; the AOT path SKIPS Cecil
  (ILType/ILMethod init from the .neo tables, not from a Cecil TypeDefinition).

## Current state (the pieces Step 25 consumes -- all DONE)

- **Step 23 `NeoAssemblyReader`** (`ILRuntime/Runtime/NeoAOT/NeoAssemblyReader.cs`) --
  deserializes a `.neo` -> `NeoAssemblyModel` (the header + 7 tables: StringTable,
  TypeRefTable, MethodRefTable, FieldRefTable, TypeDefTable, MethodDefTable,
  GenericMethodTemplateTable, InitializerTable). Each record has the load-bearing
  fields (OpCodeR[] NeoExecuteBody, CompiledFrame locals, EH as body-indices, type
  layout, VTable method-refs, interface slot-keys, etc.).
- **Step 22 `GenericMethodTemplate` + CloneAndPatch** (`GenericMethodTemplate.cs`) --
  generic instantiation from a template at runtime.
- **Step 24 `NeoCompiler`** -- produced the .neo (the CLI). Step 25 is the consumer.

## The Cecil-coupling surface to decouple (the load-bearing research)

`ILType` (`ILRuntime/CLR/TypeSystem/ILType.cs`) holds a Cecil `TypeDefinition`
(`:28`, `:83`) + `InitializeBaseType`/`InitializeFields`/`InitializeMethods`
(`:259/:179/:181`) that READ from Cecil. `ILMethod` similarly holds Cecil
`MethodDefinition`/`Body`. Step 25 must produce ILType/ILMethod instances from the
.neo tables WITHOUT Cecil (Plan A dual-path: redundant fields; the AOT path skips
the Cecil-reading init). DUMP-GATE: enumerate every place ILType/ILMethod/AppDomain
reads Cecil during init + load, so the dual-path covers them all. This is the
largest research surface of any AOT step.

## KEY design questions for the planner to dump-gate + decide

1. **The dual-path mechanism.** Plan A (redundant fields): ILType/ILMethod get an
   AOT-mode init path that populates from the .neo tables instead of Cecil. Decide:
   a new constructor / an init flag / a parallel AOT-ILType? The cleanest is likely
   a flag + an alternate init (AOT) that the loader calls, leaving the Cecil path
   intact (the JIT path stays). Ground in the actual init code.
2. **The loader flow.** `NeoAssemblyModel` -> ILType[]/ILMethod[] bound into an
   AppDomain. TypeRef -> CLR Type (by aqname -- the Step-25 deferral: CLR aqname
   indexing); MethodRef -> CLRMethod; FieldRef -> the field. The VTable bind. The
   hierarchy build (for isinst/castclass). Static .cctor seeding (Step-25 deferral).
3. **Cross-AppDomain token re-resolution.** Step 23 deferred this: the .neo's token
   hashes were captured in the COMPILE AppDomain; the LOAD AppDomain must re-resolve
   them (the hash -> object maps rebuilt). Decide the mechanism.
4. **Generic instantiation at load/runtime.** A generic method call on an AOT-loaded
   type -> CloneAndPatch from the Step 22 template. Wire the Step 22 mechanism into
   the AOT ILMethod's BodyRegister.
5. **The V2 functional test.** Load a .neo (produced by Step 24's CLI on a small
   probe) -> execute a method via ExecuteNeo -> assert the result == the JIT path.
   This is the capstone proof.
6. **Cecil-decoupling scope.** FULL decoupling (the AOT path never touches Cecil) is
   the goal but may be large. A PARTIAL decoupling (AOT uses .neo for bodies + frame,
   falls back to Cecil for some metadata) may be the tractable V1. Decide the scope
   (the planner has STOP/split latitude -- this is the biggest AOT step).

## VERIFICATION (V2 functional -- the capstone)

- **(V2) Load + execute:** produce a .neo for a small probe via Step 24's CLI (or the
  NeoCompiler driver) -> load it via the Step 25 loader -> execute a method via
  ExecuteNeo -> assert the result == the JIT-compiled-and-run result. Matrix: a
  non-generic method, a generic method (CloneAndPatch at load), a method with EH, a
  method with various locals/refs. This is the FIRST end-to-end "deserialize + execute"
  proof.
- **Regression gate:** NeoStep 205/205 + NeoStep23Roundtrip 15/15 + NeoStep22SelfCheck
  55/55 + NeoStep24CliRoundtrip 5/5 (Step 25 is additive -- the loader is a new path;
  the JIT path stays).
- **Legacy-neutral:** the loader is Neo-only (`#if ENABLE_NEO_MODE`); the ILType/ILMethod
  dual-path changes must be Legacy-neutral (gate the AOT path `#if ENABLE_NEO_MODE` or
  confirm the Cecil path is byte-identical for Legacy).

## Scope + non-goals + STOP/split latitude

- **In scope:** the .neo loader (NeoAssemblyModel -> ILType/ILMethod bound into an
  AppDomain); the Cecil-decoupling dual-path (Plan A); the V2 functional test.
- **This is the BIGGEST AOT step.** The planner has EXPLICIT STOP/split latitude: if
  full Cecil-decoupling is too large for one child, propose the highest-value scoped
  slice (e.g. non-generic-method load+execute first; defer generic-instantiation-at-load
  + full Cecil-decoupling to a round 2). Record the deferral.
- **Non-goals (defer):** perf benchmarks (Step 26); the Step-24 host-CLR-assembly
  registration (TestCLREnum) for a full TestCases.dll load (that's a Step-24-CLI / ref-
  classification concern, partly Step 25).

## Probe BEFORE designing (binding)

The Cecil-coupling surface is large + subtle. PROBE: enumerate every Cecil read in
ILType/ILMethod/AppDomain init (a grep + a dump of the init call tree). Do NOT design
the dual-path from §8.4 alone -- ground it in the actual Cecil reads.

## Build + test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # ALWAYS -f net8.0; baseline 205/205
```
- Build CLI with `Debug_Neo`; NEVER TestCases with `Debug_Neo`.
- NeoStep 205/205 + NeoStep23Roundtrip 15/15 + NeoStep22SelfCheck 55/55 + NeoStep24CliRoundtrip 5/5
  = REGRESSION gate (Step 25 is additive).
- For SHARED-engine edits (ILType/ILMethod/AppDomain Cecil-coupling), confirm Legacy-neutral
  (plain `Debug` + `useRegister=true`). A dual-path change MUST be Legacy-neutral or Neo-gated.
- Build-cache gotcha: confirm the DLL rebuilt after an edit.

## Codebase gotchas (full detail in handoff section 4)

- `OpCodeR`/CompiledFrame/GenericMethodTemplate/NeoAssemblyReader are Neo-only.
- ILType/ILMethod/AppDomain are SHARED (Legacy uses them too) -- the dual-path AOT
  changes must be Legacy-neutral (gate `#if ENABLE_NEO_MODE` or confirm byte-identical).
- Test harness is NOT xUnit; the V2 test is a `public static` method that loads the
  .neo + executes + asserts (via the value / 1-0 divide path).
- Write tool corrupts ~0.5% of CJK; author ASCII-primary.
- A test >10s = infinite loop -> kill.

## Likely new files / fix sites (dump-locked)

- A new `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (the .neo -> ILType/ILMethod
  binder).
- ILType/ILMethod dual-path init (an AOT-mode flag + alternate init that reads .neo
  tables, SHARED -- Legacy-neutral gate).
- AppDomain load flow (a LoadNeoAssembly entry, Neo-only).
- A V2 functional test (`TestCases/NeoStep25*` or a host-side self-check).

## Regression risk: HIGH (the biggest AOT step).

The loader + Cecil-decoupling touch SHARED ILType/ILMethod/AppDomain code. The risk
is "did the dual-path break the JIT/Legacy path?" (the regression gate catches this)
+ "is the Cecil-decoupling complete enough to execute correctly?" (the V2 functional
gate). Gate: NeoStep 205/205 + the V2 functional + Legacy-neutral. STOP/split if the
full scope is too large.

## Maintain this file

APPEND durable findings after propose (the Cecil-coupling enumeration, the dual-path
mechanism, the scoped slice (if split), the V2 functional design, the cross-AppDomain
token re-resolution decision). Do NOT append chatter.

## Findings -- neo-step25-runtime-loader (propose, 2026-07-07)

### The Cecil-coupling is LARGE at init, ZERO at execution (the design hinge)

Dump-gated against HEAD, file + line cited in proposal.md. The Cecil reads split
cleanly by PHASE:

- **ILType init** (9 sites): `definition.BaseType` (InitializeBaseType :1357),
  `definition.Fields` (InitializeFields :2032), `definition.Methods`
  (InitializeMethods :1554), `definition.Events` (:1606), `definition.Interfaces`
  (InitializeInterfaces :1334), `definition.CustomAttributes` (:1542),
  `IsValueType`/`IsEnum`/`IsInterface` (:1146/:1237/:1171), `GenericParameters`
  (:2223/:2415), the exposed `TypeDefinition` property (:83).
- **ILMethod init** (7 sites): `def.ReturnType` (ctor :240), `def.Parameters`
  (:249), `def.CustomAttributes` (:262), `def.Body.Variables` (InitCodeBody
  :698), `def.Body.Instructions` (:702/:835 -- the JIT INPUT), `def.Body.
  ExceptionHandlers` (:776/:792), `def.HasBody`.
- **`ExecuteNeo` at RUNTIME: ZERO Cecil reads.** Verified at
  `ILIntepreter.Neo.cs:840-842`: it reads ONLY `method.CompiledFrame.
  NeoExecuteBody` + `method.CompiledFrame` (frame metadata) + resolves token
  operands via `AppDomain.GetMethod(ip->Operand2)` / `GetType(ip->Operand)` /
  the string interner (the hash maps).

**Implication:** Step 25 does NOT touch `ExecuteNeo`. The dual-path is purely an
INIT concern: populate `CompiledFrame` from the `.neo` (bypass `InitCodeBody`/
JIT) + keep the AppDomain hash maps resolving. The cross-AppDomain case is the
one real subtlety (below).

### The cross-AppDomain token problem is REAL (identity-based hashes)

`ILType.GetHashCode()` (`ILType.cs:2529-2533`) + `ILMethod.GetHashCode()`
(`ILMethod.cs:1245-1249`) are IDENTITY-BASED (`Interlocked.Add(ref instance_id,
1)`, process-global counter). The `OpCodeR` token operands carry these
compile-time identity hashes. A FRESH load-AppDomain creates new instances with
DIFFERENT hashes -> the operands DON'T resolve via `mapTypeToken`/`mapMethod`
(`GetType(int)` at `AppDomain.cs:1429`). Step 23's same-process V1 roundtrip is
exact ONLY because the compile-time maps stay live; the `.neo` does NOT record
the compile-time hash per TypeRef/MethodRef/String today.

**DECISION (deferred from S1, recorded for S3):** Approach 1 -- extend the
`.neo` to record the compile-time `GetHashCode()` per ref entry (a parallel
`int[]`/`long[]` per reference table, additive, under a Version bump); the
loader re-registers resolved refs under the recorded hash; the deserialized
bodies run UNMODIFIED. Approach 2 (name-based `GetHashCode`) REJECTED -- the
identity-uniqueness is relied on by `mapTypeToken` (`:665-666`), `mapMethod`,
`fieldTokenMapping` (`ILType.cs:1920`), `jumptables` (`ILMethod.cs:1095`); a
name-based hash risks collision-shadowing in these SHARED maps (Legacy-affecting
regression). Approach 3 (body rewrite) REJECTED -- it needs the same hash map
as Approach 1 AND mutates the body.

### The dual-path mechanism (Plan A, scoped to ILMethod-side for S1)

`ILMethod` keeps its Cecil fields; a Neo-only `isNeoAotBody` flag (default
false) + `InitCodeBodyFromNeo(NeoMethodDefRecord)` populates `compiledFrame`
(`:58`) field-by-field from the Step-23 record + rebuilds the runtime EH
structures from `NeoExceptionHandlerRecord[]` (body-index -> 1:1 with
`NeoExecuteBody` positions, no Cecil `addr[]`). The `BodyRegister` getter
(`:389`) short-circuits on the flag. The Cecil/JIT path STAYS byte-identical
when the flag is false (the reference + fallback). The `CompiledFrame` getter
(`:423`) needs NO change (it already checks `NeoExecuteBody != null`). ILType
dual-path (populate fieldOffsets/VTable/interfaceMap from `NeoTypeDefRecord`)
DEFERRED to S3.

### CHOSEN SCOPE: S1 (Non-generic load + execute -- the V2 functional minimum)

Recommended slice. Delivers the V2 capstone (deserialize + ExecuteNeo == JIT)
with the cleanest, lowest-risk scope; the dual-path MECHANISM is designed for
S2/S3 to extend.

- **IN:** `NeoAssemblyLoader.Attach(appdomain, model)` (same-AppDomain; bind
  each `NeoMethodDefRecord` to a live non-generic ILMethod via name + param
  count, call `InitCodeBodyFromNeo`); the ILMethod AOT-init; the V2 self-check
  (`NeoStep25LoadExecCheck` via the `NeoStep25LoadExec` CLI hook).
- **DEFERRED (round 2):** S2 generic-instantiation-at-load (CloneAndPatch from
  the `.neo` template into an AOT ILMethod); S3 full ILType Cecil-decoupling +
  load into a Cecil-free AppDomain + cross-AppDomain token re-resolution
  (Approach 1) + static `.cctor` seeding via `.neo` + full CLR aqname indexing
  / host-CLR-assembly registration (the Step-24 `TestCLREnum` gap); Step 26 perf.

The full-scope (S3) is too large for one clean child; S1 is the tractable,
highest-value slice (the LEAD pre-authorized S1 in the STOP/split latitude).

### The V2 functional design (the capstone)

Host-side `NeoStep25LoadExecCheck.Run(appdomain)` mirrors the Step-24 self-check
pattern: compile probe -> `.neo` (`NeoCompiler.Compile([probeType], ms)`) ->
read back (`NeoAssemblyReader.Read`) -> run each probe method via JIT (capture
A, before attach) -> `NeoAssemblyLoader.Attach` -> run again via AOT (capture
B) -> assert A == B via the value/divide path (`if (A != B) int x = 1/0;`).
Matrix (non-generic only): arithmetic; try/catch; mixed locals/byref. Probe =
dedicated `TestCases/NeoStep25LoadProbe.cs` (NOT full `TestCases.dll`). This is
the FIRST end-to-end "deserialize + execute" proof.

### Regression risk: HIGH but bounded

The ILMethod AOT-init touches SHARED `ILMethod` code. Risk = "did the
`BodyRegister` getter short-circuit break the JIT/Legacy path?" Mitigation: the
flag defaults false; the short-circuit is `#if ENABLE_NEO_MODE` AND flag-gated.
Gate: NeoStep 205/205 + NeoStep23Roundtrip 15/15 + NeoStep22SelfCheck 55/55 +
NeoStep24CliRoundtrip 5/5 + the new NeoStep25LoadExec (additive). Legacy-neutral
(all additions compile out of plain `Debug`).

### Verdict to the LEAD

CLEAN TRACTABLE PROPOSAL at S1 (no split needed beyond the pre-authorized S1
slice). The full S3 scope IS too large for one child -- recommend the LEAD
approve S1 now and queue S2 (generic-at-load) + S3 (full decoupling + cross-
AppDomain) as follow-up children. All artifacts authored (proposal.md,
design.md, specs/neo-optimizer/spec.md delta with 4 ADDED requirements,
tasks.md), pure ASCII, SHALL on the first wrapped line of each requirement
body.

## Findings -- neo-step25-runtime-loader (apply, 2026-07-07)

### S1 SHIPPED + V2-PROVEN (the capstone holds)

Step 25 S1 is implemented and the V2 functional capstone PASSES: deserialize a
.neo -> NeoAssemblyLoader.Attach -> ExecuteNeo on the AOT body == the JIT path
for the non-generic probe matrix. The regression gate is green (ZERO
regressions) and the change is Legacy-neutral.

### The ILMethod AOT-init dual-path (the core seam, SHARED -- Legacy-neutral)

`ILRuntime/CLR/Method/ILMethod.cs`, all `#if ENABLE_NEO_MODE`:
- A Neo-only `internal bool isNeoAotBody` field (default false).
- `internal void InitCodeBodyFromNeo(NeoMethodDefRecord rec, Func<int, IType>
  resolveCatchType)` -- populates `compiledFrame` field-by-field from the Step-23
  record (NeoExecuteBody, LocalInfos, ParamInfos, every size/count, LocalIsRef,
  NeoCatchException*), rebuilds SwitchTargets (Dictionary<int,int[]> from the
  serialized pairs), rebuilds NeoCallParams (NeoCallParamMap[] from
  NeoCallParamMapRecord[], resolving PrimitiveByRefElemType CLR System.Type[]
  back from aqnames via NeoAssemblyReader.ResolveAqName), rebuilds the runtime
  Method.ExceptionHandler[] (the `exceptionHandlerR` field ExecuteNeo scans),
  and sets the ILMethod-level mirrors (bodyRegister, stackRegisterCnt,
  jumptablesR, localVarCnt) + isNeoAotBody=true. The catch-type resolver is
  loader-provided (it needs the .neo model + the AppDomain).
- The `BodyRegister` getter short-circuits when isNeoAotBody (returns the AOT
  body, skips InitCodeBody/JIT). The CompiledFrame getter needs NO change (it
  already checks NeoExecuteBody != null).

### The EH rebuild semantics (the one structural reconstruction -- verified)

The record stores the RAW JIT-time addr[] indices: TryStart/HandlerStart are
the FIRST instruction of the region (used verbatim); TryEnd/HandlerEnd are the
instruction AFTER the region (EXCLUSIVE). The runtime's INCLUSIVE convention
(`addr <= i.TryEnd` in GetCorrespondingExceptionHandler) needs the -1
adjustment -- EXACTLY mirroring InitCodeBody's Cecil conversion
(`e.TryEnd = addr[eh.TryEnd] - 1`). A -1 end (Cecil null -- the last handler)
-> bodyLen-1 (InitCodeBody's fallback). RebuildEHFromNeo applies this. The
TryCatchProbe cell (throw + catch taken, JIT==AOT==100) proves the rebuilt EH
dispatches correctly at runtime.

### NeoAssemblyLoader.Attach (the .neo -> ILMethod binder)

`ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs` (NEW, Neo-only).
Attach(appdomain, model): for each NeoMethodDefRecord, resolve MethodRefIdx ->
MethodReferencePatchInfo (DeclaringType is INLINE on the record, NOT an index
into TypeRefs -- corrected from the proposal's pseudo-code), look up the ILType
via LoadedTypes[fullName], match by name + param count (GetMethods() +
GetConstructors() return the type's OWN methods only; generic instances +
generic definitions skipped -> S2), call InitCodeBodyFromNeo. Misses are skipped
(the method keeps JIT -- the additive contract). Same-AppDomain: the runtime
hash maps resolve the bodies' token operands NATURALLY (no cross-AppDomain
re-resolution in S1). Returns a NeoLoadReport (Attached + Skipped).

### The V2 result (A==B==expected for the non-generic matrix)

`NeoStep25LoadExecCheck.Run(appdomain)` (host-side, `#if ENABLE_NEO_MODE &&
DEBUG`), driven via the `NeoStep25LoadExec` CLI hook. It compiles a .neo via
the SAME NeoCompiler driver -> reads it back -> captures the JIT result per
probe method (BEFORE attach) -> Attach -> captures the AOT result (AFTER attach)
-> asserts EACH equals its KNOWN-expected value (stronger than JIT==AOT: rules
out a "both-garbage" false pass). Matrix (non-generic, parameterless -- see the
Run-shim finding below):
- ArithProbe (arithmetic):           JIT==47   AOT==47   PASS
- TryCatchProbe (try/catch EH):      JIT==100  AOT==100  PASS
- MixedLocalsProbe (locals+loop+an   JIT==61   AOT==61   PASS
  internal byref call BumpRef(ref)):
NeoStep25LoadExec: 8/8 cells passed, 0 failed. Attach: 5 attached (the 3 probes
+ BumpRef + the implicit .ctor), 0 skipped. The byref-call cell exercises the
NeoCallParamMap rebuild (PrimitiveByRefSrc + WriteBack + ElemType); the
try/catch cell exercises the EH rebuild. This is the FIRST end-to-end
"deserialize + execute == JIT" proof.

### The host-entry Run shim is PARAMETERLESS-ONLY (a key S1 finding for S2/S3)

`ILIntepreter.Run(ILMethod, object, object[])` (`ILIntepreter.cs:87-120`) is the
Step-6 Neo entry shim: its comment states "only no-arg static methods are
expected here (NeoStep6 smoke)." It does NOT marshal `object[] p` into the frame
(args are ignored; the body reads uninitialized frame memory), and it allocates
only the primitive frame region + the return region (not the full frame ref
region). The test harness confirms this convention: `BaseTestUnit.Invoke` calls
`GetMethod(method, 0)` (parameterless) + `App.Invoke(im, null)`. Consequences:
- A multi-param probe method invoked via App.Invoke returns garbage (args never
  reach the body) -- the V2 probe is therefore PARAMETERLESS (inputs baked in as
  locals; the self-check pins expected values).
- A try/catch whose catch body calls a CLR property chain (`ex.Message.Length`)
  throws ArgumentOutOfRangeException under the shim (incomplete frame ref
  setup) -- in BOTH JIT and AOT identically (so AOT==JIT still holds, but the
  expected value is unreachable). The probe's catch uses a caught-flag pattern
  (no CLR property chain) to stay within the shim's reach; the EH dispatch
  itself (throw + catch entry + continuation) works.
- S2/S3 follow-ups that need to invoke PARAMETRIZED or CLR-heavy methods host-
  side will need EITHER a fuller Neo Run entry (marshal args + allocate the full
  frame ref region) OR an internal-call-driven invocation. Recorded as a
  round-2 prerequisite for a stronger V2 (input coverage + CLR-catch shapes).

### Methods skipped in S1 (none -- all 5 attached)

The probe compiled cleanly (5/5 methods, 0 skips) and all 5 attached (0
skipped). Generic methods are out of scope by construction (the probe declares
none; S2 will wire CloneAndPatch from the .neo template).

### Regression gate (all green; S1 is additive)

- NeoStep smoke (Neo, Debug_Neo + useRegister=true): 208/208, 0 failed. (Was
  205/205 on HEAD; +3 = the new parameterless probe methods ArithProbe /
  TryCatchProbe / MixedLocalsProbe, which the NeoStep filter also runs green
  via the normal test harness. BumpRef has a ref param -> not in the harness
  TestList, not counted.)
- NeoStep22SelfCheck: 55/55. NeoStep23Roundtrip: 15/15. NeoStep24CliRoundtrip:
  5/5. (ZERO regressions -- the loader is a new path; the AOT-init defaults off.)
- NeoStep25LoadExec (the new V2 capstone): 8/8.

### Legacy-neutral evidence (ILMethod is SHARED)

- Plain `Debug` build of ILRuntimeTestCLI: 0 errors (the whole NeoAOT/ addition
  + the isNeoAotBody flag + InitCodeBodyFromNeo + the BodyRegister getter
  short-circuit are `#if ENABLE_NEO_MODE` and compile out -- ILMethod is byte-
  identical to before under plain Debug).
- Legacy register VM smoke (plain Debug + useRegister=true, NeoStep1 subset):
  Ran 174 tests; the failures are the pre-existing Legacy-vs-Neo feature gap
  (NeoStep tests using Neo-only ops), NOT this change (which compiled out).

### Verdict to the LEAD (apply)

S1 COMPLETE + V2-PROVEN. The deserialize + ExecuteNeo == JIT equivalence holds
for the non-generic matrix (arithmetic, EH, locals+byref-call), the regression
gate is green (NeoStep 208/208 + 22/23/24 AOT gates), and the change is Legacy-
neutral (compiles out of plain Debug). The Run-shim parameterless-only
limitation is the recorded S2/S3 prerequisite for a stronger V2 (parametrized +
CLR-catch-shape coverage). Recommend the LEAD queue S2 (generic-at-load via
CloneAndPatch from the .neo template) + S3 (full ILType Cecil-decoupling +
cross-AppDomain hash re-resolution + .cctor seeding + the fuller Neo Run entry).

## Findings -- neo-step25-runtime-loader (review-fix round 1, 2026-07-07)

Adopted the reviewer's TOP recommendation (fold the body-mutation proof into the
PERMANENT matrix) + a typed-catch cell, plus the 2 Minor defensive fixes. TEST-
ONLY on the permanent guard + two `#if ENABLE_NEO_MODE` hardening edits; NO
engine-logic change to the S1 load+execute path. No real S1 bug surfaced (the
Minors are "NOT exercised in S1" per the reviewer, confirmed -- both are
forward-only fail-loud guards for S2/S3).

### 1. Body-mutation cell (PERMANENT -- the load-bearing "AOT body really runs" proof)

Folded the reviewer's ad-hoc Probe A into `NeoStep25LoadExecCheck` so the single
most important property -- "ExecuteNeo runs the genuine deserialized AOT body,
NOT the JIT body" -- stays guarded regressively as S2/S3 evolve the dual-path.
The `isNeoAotBody` flag assertion (already in the matrix) proves the FLAG is set
but NOT that the AOT body runs; and a green JIT==AOT check is INSUFFICIENT (Step
23 made AOT body == JIT body byte-for-byte, so JIT==AOT holds even if the JIT
body ran). The decisive proof is to MUTATE a deserialized constant and observe
the MUTATED value.

New probe `ConstProbe() { return 1234567; }` (CONST outside sbyte range -> the
JIT emits a real `Ldc_I4` with the full value in `Operand`). The cell: a FRESH
compile -> `model2` (independent .neo, so the mutation never touches the main
flow's model); locate ConstProbe's `NeoMethodDefRecord` by name (MethodRefIdx ->
MethodRefTable -> Name); find the `Ldc_I4` whose `Operand == 1234567`; rewrite it
to `7654321` (MUTATED) in the deserialized `OpCodeR[]` BEFORE `Attach`; `Attach`
(ConstProbe now gets the MUTATED body -- the direct-reference assignment
`compiledFrame.NeoExecuteBody = rec.NeoExecuteBody` means the mutated array IS
what ExecuteNeo reads); run ConstProbe; assert the result == 7654321 (a JIT-body
run would yield 1234567). **PASS** -- proves the deserialized AOT body genuinely
ran. If the body shape ever changes (no `Ldc_I4=1234567` found), the cell dumps
the actual opcodes for diagnosis.

### 2. Typed-catch cell (PERMANENT -- catch-type-ref resolution + typed EH dispatch)

New probe `TypedCatchProbe()`: throws `InvalidOperationException` and catches
`(InvalidOperationException)` -- a SPECIFIC type, not the universal
`catch (Exception)` the existing TryCatchProbe uses. This forces the AOT EH
rebuild to resolve the catch `TypeRef` to the runtime `IType`
(`ResolveTypeRefToIType` -> `appdomain.GetType("System.InvalidOperationException")`
for a CLR type; the generic `catch (Exception)` does not meaningfully stress the
match) AND the runtime EH dispatch to match the thrown type against that specific
catch type. Expected 200 (typed catch taken). JIT==AOT==200. **PASS** -- the CLR
catch-type aqname resolution + the typed EH dispatch both work on the AOT path.
Catch body uses the caught-flag pattern (no CLR property chain) to stay within
the Step-6 host-entry Run shim's reach (the recorded parameterless-only shape).

### Grown matrix: 8 -> 11 cells (all PASS)

`NeoStep25LoadExecCheck` now reports **11/11 cells passed, 0 failed; 7 attached
(5 parameterless probes + BumpRef + .ctor), 0 skipped**:
1. compile (7 methods, 0 templates)
2-3. ArithProbe JIT/AOT (47)
4-5. TryCatchProbe JIT/AOT (100)
6-7. MixedLocalsProbe JIT/AOT (61)
8-9. TypedCatchProbe JIT/AOT (200)  [NEW]
10. Attach coverage (all 5 parameterless probe methods AOT-attached)
11. ConstProbe body-mutation (AOT body really runs)  [NEW]

### Minor-1: RebuildEHFromNeo `default` arm fail-loud (ILMethod.cs)

The `default:` arm of `RebuildEHFromNeo`'s
`switch ((ExceptionHandlerType)r.HandlerType)` silently misclassified an
unknown/Filter HandlerType as `Catch` (would mis-dispatch at runtime).
`InitCodeBody`'s `default` arm throws NIE for the same case. Changed the AOT
`default` arm to `throw new NotImplementedException("Neo AOT EH rebuild:
unsupported HandlerType=" + r.HandlerType + ...)` -- fail-loud, matching
InitCodeBody. An unknown/Filter HandlerType is a future-S2/S3 case (the Step-24
partition emits only Catch/Finally/Fault today); now it is LOUD, not silent.
`#if ENABLE_NEO_MODE` (Legacy-neutral -- compiles out of plain Debug).

### Minor-2: ResolveElemTypesFromNeo guard (ILMethod.cs)

`ResolveElemTypesFromNeo` returned a `null` element slot on a `ResolveAqName`
miss with no try/catch; a `null` `PrimitiveByRefElemType` slot could NRE in the
runtime byref copy/write-back. The sibling catch-type resolver IS guarded (a
miss there is tolerable -- the handler just won't match); the element-type slot
has no safe fallback. Added: if `ResolveAqName` returns null for a NON-EMPTY
aqname, throw a tagged NIE (`"Neo AOT NeoCallParamMap: unresolved byref element
type aqname='...'"`). A null/EMPTY aqname is EXPECTED (a frame-native byref /
non-byref slot per `NeoCallParamMapRecord`) and stays null. BCL primitives
resolve (proven by the long + int byref cells); a CLR-from-unloaded-assembly is
an S3 concern -- now LOUD, not a silent null. `#if ENABLE_NEO_MODE`
(Legacy-neutral).

### Gate status (all green)

- NeoStep smoke (Debug_Neo + useRegister=true): **210/210, 0 failed** (was
  208/208; +2 = the new parameterless probe methods ConstProbe + TypedCatchProbe,
  which the NeoStep filter also runs green via the normal JIT harness path -- in
  a SEPARATE process invocation from the body-mutation special mode, so the
  mutation never affects the smoke run).
- NeoStep25LoadExec (the grown V2 capstone): **11/11 cells, 0 failed; 7 attached,
  0 skipped.** The body-mutation cell returns the MUTATED value (7654321), the
  typed-catch cell resolves InvalidOperationException on the AOT path (200).
- Legacy-neutral: plain `Debug` CLI build: 0 errors (all hardening edits are
  `#if ENABLE_NEO_MODE`; ILMethod is byte-identical to before under plain Debug).

### Verdict to the LEAD (review-fix round 1)

CLEAN. The reviewer's top recommendation (permanent body-mutation guard) + the
typed-catch cell are folded in (matrix 8 -> 11, all PASS); the 2 Minors are
fail-loud hardening (no S1 behavior change; both `#if ENABLE_NEO_MODE`). No real
S1 bug surfaced. Ready for the LEAD's non-author delta re-review.

