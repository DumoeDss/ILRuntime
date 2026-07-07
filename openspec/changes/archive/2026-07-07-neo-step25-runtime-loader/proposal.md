# Proposal — neo-step25-runtime-loader (Step 25: runtime `.neo` loader + ILMethod Cecil-decoupling dual-path)

> The V2 functional capstone of the Neo AOT toolchain (Steps 22-26). Step 25 is
> the consumer of the Step-23 `.neo` format + the Step-24 `NeoCompiler` driver:
> it deserializes a `.neo` and runs a method via `ExecuteNeo` DIRECTLY (no JIT),
> proving the first end-to-end "deserialize + execute == JIT" equivalence.

## Context

Steps 22-24 shipped the AOT compile half: Step 22 (`GenericMethodTemplate` +
`CloneAndPatch`), Step 23 (the `.neo` binary format + writer/reader ->
`NeoAssemblyModel`), Step 24 (`NeoCompiler` driver + `ilrt_neoc` CLI + the V1-A
roundtrip self-check). All are additive, Neo-only, Legacy-neutral.

Step 25 is the RUNTIME half: deserialize a `.neo` -> ILType/ILMethod that
`ExecuteNeo` runs DIRECTLY. The §8.3-8.4 design (`.trae/documents/object-model-
neoo-design.md`) fixes the flow:

```
runtime loader:
  deserialize type/method/field tables (Step 23 NeoAssemblyReader)
  bind CLR type references (TypeRef -> CLR Type; MethodRef -> CLRMethod)
  method bodies point directly at OpCodeR[] (NO JIT)
  ExecuteNeo() runs directly
```

This change is the BIGGEST AOT step (the largest Cecil-coupling research surface
of any step). The planner has EXPLICIT STOP/split latitude. This proposal picks
a scoped slice (S1) that delivers the V2 functional capstone and defers the
remainder to a documented round 2.

## The Cecil-coupling enumeration (the load-bearing research, dump-gated)

The dual-path must cover every Cecil read at INIT/load, OR fall back to Cecil for
the uncovered ones. The enumeration (verified against HEAD, file + line cited):

### ILType (`ILRuntime/CLR/TypeSystem/ILType.cs`) -- Cecil reads during INIT

| Cecil read | Site | What it feeds |
|---|---|---|
| `definition.BaseType` | `InitializeBaseType` :1357 | hierarchy (isinst/castclass/VTable chain), `baseType`, `firstCLRBaseType`, delegate detect |
| `definition.Fields` | `InitializeFields` :2032 | field types, `fieldOffsets`, `staticFieldOffsets`, `TotalPrimitiveSize`, `TotalReferenceCount`, `naturalAlignment` |
| `definition.Methods` | `InitializeMethods` :1554 | method enum -> `ILMethod` list (`methods`, `constructors`, `staticConstructor`) |
| `definition.Events` | `InitializeMethods` :1606 | event add/remove wiring |
| `definition.Interfaces` | `InitializeInterfaces` :1334 | `interfaces[]`, Neo interface map |
| `definition.CustomAttributes` | `InitializeMethods` :1542 | `jitFlags` |
| `definition.IsValueType`/`IsEnum`/`IsInterface` | :1146/:1237/:1171 | type kind |
| `definition.GenericParameters` | :2223/:2415 | generic args |
| `TypeDefinition` property | :83 | exposed (ILMethod ctor reads generic params) |

### ILMethod (`ILRuntime/CLR/Method/ILMethod.cs`) -- Cecil reads during INIT

| Cecil read | Site | What it feeds |
|---|---|---|
| `def.ReturnType` | ctor :240 | `ReturnType` |
| `def.Parameters` | ctor :249 | `paramCnt` |
| `def.CustomAttributes` | ctor :262 | `jitFlags`, debugger-step-through |
| `def.Body.Variables` | `InitCodeBody` :698, `SetBodyAndJumptables` :379 | locals (`localVarCnt`, `variables`) |
| `def.Body.Instructions` | `InitCodeBody` :702/:835 | the CIL body (JIT INPUT -- consumed by `JITCompiler.Compile`) |
| `def.Body.ExceptionHandlers` | `InitCodeBody` :776/:792 | EH table (JIT input) |
| `def.HasBody` | multiple | body presence |

### THE KEY DECOUPLING INSIGHT (verified, the design hinge)

At EXECUTION time, `ExecuteNeo` (`ILIntepreter.Neo.cs`) reads ONLY:

- `method.CompiledFrame.NeoExecuteBody` (the lowered body, :840)
- `method.CompiledFrame` (frame metadata: `LocalInfos`, `ParamInfos`,
  `TotalStructSize`, `NeoCallParams`, `SwitchTargets`, `NeoCatchException*`, :842)
- token operands resolved via the AppDomain hash maps: `AppDomain.GetMethod(
  ip->Operand2)` (`:2052`/`:2127`/...), `AppDomain.GetType(ip->Operand)`, string
  tokens via the AppDomain string interner.

**`ExecuteNeo` is ALREADY Cecil-free at runtime.** It touches NEITHER
`def.Body.Instructions` NOR any Cecil object. The Cecil coupling is ENTIRELY at
INIT/JIT time (`InitCodeBody` -> `JITCompiler.Compile`). Therefore the dual-path
is: an AOT-init path that populates `CompiledFrame` from the `.neo`
`NeoMethodDefRecord` (bypassing `InitCodeBody`/JIT) + ensures the AppDomain hash
maps resolve the body's token operands. No `ExecuteNeo` change is needed.

### The cross-AppDomain token problem (verified, the design constraint)

`ILType.GetHashCode()` (`ILType.cs:2529-2533`) and `ILMethod.GetHashCode()`
(`ILMethod.cs:1245-1249`) are IDENTITY-BASED: `Interlocked.Add(ref instance_id,
1)` on a process-global counter (`0x10000000`-seeded). The `OpCodeR` token
operands (type-token hash on `Box`/`Isinst`/`Castclass`/`Initobj`/`Newarr`/
`Constrained` in `Operand`; method-token hash on `Call`/`Callvirt` in
`Operand2`; string token in `OperandLong`) carry these COMPILE-time identity
hashes. A FRESH load-AppDomain creates new instances with DIFFERENT hashes, so
the operands would NOT resolve via `mapTypeToken`/`mapMethod` (the runtime
resolves via `GetType(int hash)` at `AppDomain.cs:1429`). Step 23's same-process
V1 roundtrip is exact ONLY because the compile-time maps stay live. Cross-
AppDomain re-resolution is real, not optional, for a standalone loader -- and
Step 23 EXPLICITLY deferred it (the `.neo` does not record the compile-time hash
per TypeRef/MethodRef/String today).

## The dual-path mechanism (Plan A: redundant fields + AOT-init flag)

Grounded in the init code above. Plan A (the design doc's chosen mechanism):
ILType/ILMethod KEEP their Cecil fields; an AOT-mode flag + an alternate init
populates the SAME fields from the `.neo` tables. The Cecil path STAYS (it is
the JIT path, the reference, and the fallback). All AOT-init additions are
`#if ENABLE_NEO_MODE`.

**Scoped to S1 (this change):** only the ILMethod-side AOT-init is built:

- `ILMethod` gains a Neo-only `bool isNeoAotBody` flag + an internal
  `InitCodeBodyFromNeo(NeoMethodDefRecord)` that populates `compiledFrame`
  (`NeoExecuteBody` + every load-bearing frame field the Step-23 record carries)
  + `bodyRegister` + `stackRegisterCnt` + `jumptablesR` directly from the record,
  bypassing `def.Body` / `JITCompiler.Compile`. The `BodyRegister` getter
  short-circuits on the flag (returns the AOT body without calling
  `InitCodeBody`). The `CompiledFrame` getter likewise sees an already-populated
  frame.
- The AOT-init RECONSTRUCTS the runtime EH structures (`Method.ExceptionHandler`
  / `exceptionHandlerR`) from the `NeoExceptionHandlerRecord[]` body-index table
  (the Step-23 EH representation), so `ExecuteNeo`'s EH dispatch works without
  the JIT-time Cecil-keyed `addr[]` map. (This is the one structural rebuild the
  AOT-init must do beyond a field-copy; it is mechanical -- body indices map
  1:1 to `NeoExecuteBody` positions.)

**DEFERRED dual-path edges (round 2):**

- The ILType-side AOT-init (populate `fieldOffsets`/`TotalPrimitiveSize`/VTable/
  interfaceMap from `NeoTypeDefRecord`, no Cecil). S1 leaves ILType Cecil-
  initialized (the assembly is Cecil-loaded in the test AppDomain).
- Generic-instantiation-at-load (S2): wire the Step-22 `CloneAndPatch` into an
  AOT ILMethod's `BodyRegister` from a `.neo` `NeoTemplateRecord`.

## CHOSEN SCOPE: S1 -- Non-generic load + execute (the V2 functional minimum)

**Recommendation: S1.** It delivers the V2 functional capstone -- deserialize a
`.neo` + execute a non-generic method via `ExecuteNeo` + assert == JIT -- with
the cleanest, lowest-risk scope. The dual-path MECHANISM is designed (for S2/S3
to extend) but only the ILMethod-side is built.

**In scope (S1):**

1. A new `NeoAssemblyLoader` (`ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs`,
   `#if ENABLE_NEO_MODE`) -- the `.neo` -> ILMethod binder. `Attach(appdomain,
   model)`: for each `NeoMethodDefRecord`, resolve its `MethodRefIdx` -> the
   MethodRef (declaring-type full name + method name + param sig) -> match an
   existing `ILMethod` in `appdomain.LoadedTypes` -> call
   `ilm.InitCodeBodyFromNeo(record)`. Non-generic methods only (generic
   instances are a runtime artifact; generic definitions route to the template
   table, deferred to S2).
2. The `ILMethod` AOT-init dual-path (above).
3. The V2 functional self-check (below).

**Deferred to round 2 (recorded, not lost):**

| Deferral | Route | Why deferred |
|---|---|---|
| Generic-instantiation-at-load (CloneAndPatch from `.neo` template) | S2 | Adds the template-instantiation wire-up; the highest-value next slice |
| Full ILType Cecil-decoupling (ILType AOT-init from `NeoTypeDefRecord`; load into a Cecil-free AppDomain) | S3 | The complete AOT goal; largest surface (9 Cecil-read sites + VTable/interface rebuild) |
| Cross-AppDomain token re-resolution | S3 | Not needed in S1 (same-AppDomain test; compile-time hash maps shared). Decision recorded below. |
| Static `.cctor` seeding via the `.neo` `StaticCtorMethodRefIdx` | S3 | The `.cctor` runs via the AOT path once ILType-decoupling lands |
| Full CLR aqname indexing + host-CLR-assembly registration (the Step-24 `TestCLREnum` gap) | S3 | A IL-vs-CLR ref-classification + host-registration concern; pairs with the cross-AppDomain hash work |
| Perf benchmarks | Step 26 | Pure optimization layer |

## The loader flow (S1, same-AppDomain, ILMethod AOT-attach)

```
NeoAssemblyLoader.Attach(appdomain, NeoAssemblyModel model):
  // Same AppDomain as the compile: LoadedTypes already has the probe types
  // (Cecil-loaded), and mapTypeToken / mapMethod are populated. So token
  // operands in the deserialized bodies resolve naturally -- NO cross-
  // AppDomain hash re-resolution needed in S1.
  for each NeoMethodDefRecord rec in model.MethodDefs:
    mr  = model.MethodRefs[rec.MethodRefIdx]
    typeFullName = TypeRefs[mr.DeclaringTypeIdx].Name   // IL full name
    iltype = appdomain.LoadedTypes[typeFullName]
    ilm = Match(iltype, mr)   // by name + param-count (+ sig if needed)
    ilm.InitCodeBodyFromNeo(rec)   // populate compiledFrame + bodyRegister,
                                   // set isNeoAotBody = true (bypass JIT)
```

`Match` is by method name + parameter count for V1 (the probe is small; full
signature matching is a round-2 robustness item, folded with the cross-AppDomain
CLR-aqname work). A match miss is reported + skipped (the method keeps its JIT
path -- the additive contract).

## Cross-AppDomain token re-resolution DECISION (deferred from S1, recorded for S3)

S1 runs same-AppDomain, so this is NOT exercised. The DECISION for round 2:

- **Chosen (Approach 1): extend the `.neo` to record the compile-time
  `GetHashCode()` per TypeRef / MethodRef / String entry** (a parallel `int[]` /
  `long[]` per reference table, additive, under a `Version` bump OR a new
  side table). The loader resolves each ref in the load AppDomain and registers
  it into `mapTypeToken` / `mapMethod` / the string interner under the RECORDED
  compile-time hash. The deserialized bodies run UNMODIFIED (their operand
  hashes are the recorded compile-time hashes, which now resolve). Minimal
  surface, no body mutation, no `GetHashCode` semantics change.
- **Rejected (Approach 2): make `ILType.GetHashCode` / `ILMethod.GetHashCode`
  name-based** so the hash is stable across AppDomains. REJECTED: the identity-
  uniqueness of the current counter-based hash is relied on by `mapTypeToken`
  (`AppDomain.cs:665-666`), `mapMethod`, `fieldTokenMapping` (`ILType.cs:1920`),
  and `jumptables` (`ILMethod.cs:1095`). A name-based hash risks collisions
  (distinct generic instances / overloads) shadowing each other in these maps --
  a SHARED-semantics regression affecting Legacy too. Approach 1 is strictly
  safer.
- **Rejected (Approach 3): body rewrite (scan + replace each operand hash with
  the load-AppDomain hash).** REJECTED: it needs the SAME compile-time-hash ->
  ref map as Approach 1, so it is strictly more work AND mutates the body
  (defeating the "deserialized body == compiled body" invariant the V1 gate
  asserts). Approach 1 dominates.

## V2 functional design (the capstone)

A host-side self-check `NeoStep25LoadExecCheck.Run(appdomain)` driven via the
existing `ILRuntimeTestCLI` special-mode hook (`if (nameFilter ==
"NeoStep25LoadExec")`), mirroring the Step-22/23/24 self-check pattern. The
check:

1. Select a small dedicated probe type (non-generic methods; a plain arithmetic
   method, a try/catch method, a method with various locals/refs -- NOT the
   large `TestCases.dll` set, to stay sub-second). The probe type lives in the
   test AppDomain (Cecil-loaded).
2. Compile a `.neo` for the probe via the SAME `NeoCompiler.Compile(
   IReadOnlyList<ILType>, Stream)` driver the CLI uses -> in-memory `MemoryStream`.
3. Read it back via `NeoAssemblyReader.Read` -> `NeoAssemblyModel` (Step 23).
4. Run each probe method via the JIT path (capture result A) -- before attach.
5. `NeoAssemblyLoader.Attach(appdomain, model)` -- attach the deserialized AOT
   bodies to the matched ILMethods (sets `isNeoAotBody`).
6. Run the SAME methods again (now via the AOT body + `ExecuteNeo`) -- capture
   result B.
7. Assert A == B via the value/divide path (`if (A != B) int x = 1/0;` raises
   `DivideByZeroException`; the test harness is NOT xUnit -- see handoff section
   4). A correct run returns the value; a mismatch trips the divide.

This is the FIRST end-to-end "deserialize + execute" proof. The matrix
(scoped to S1, non-generic only): an arithmetic method; a method with EH; a
method with various locals/refs.

## Regression risk + gating

- **Risk: HIGH** (the biggest AOT step). The ILMethod AOT-init touches SHARED
  `ILMethod` code. The risk is "did the `BodyRegister`/`CompiledFrame` getter
  short-circuit break the JIT/Legacy path?" The mitigation: the `isNeoAotBody`
  flag is `false` by default; the getter short-circuit is gated `#if
  ENABLE_NEO_MODE` AND only fires when the flag is set (which only the AOT
  loader does). The JIT path is byte-identical when the flag is false.
- **Regression gate:** NeoStep 205/205 + NeoStep23Roundtrip 15/15 +
  NeoStep22SelfCheck 55/55 + NeoStep24CliRoundtrip 5/5 + the new
  NeoStep25LoadExec (additive). Step 25 is additive (a new loader path + a
  getter short-circuit that defaults off).
- **Legacy-neutral:** the loader + the AOT-init flag + the short-circuit are all
  `#if ENABLE_NEO_MODE` (compile out of plain `Debug`). A stash-toggle plain-
  `Debug` + `useRegister=true` NeoStep-filter run MUST show the SAME pre-existing
  Legacy failure set with and without the change.

## Non-goals

- Perf benchmarks (Step 26).
- The Step-24 host-CLR-assembly registration (`TestCLREnum`) for a full
  `TestCases.dll` load (a Step-24-CLI / ref-classification concern, partly Step
  25 round 2).
- Generic-instantiation-at-load (S2), full ILType Cecil-decoupling (S3), cross-
  AppDomain token re-resolution (S3), static `.cctor` seeding via `.neo` (S3) --
  deferred per the table above.
- Removing or changing the JIT/Cecil path (it is the reference + the fallback).
- A change to `ILType.GetHashCode` / `ILMethod.GetHashCode` (Approach 2 rejected).
