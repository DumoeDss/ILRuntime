## ADDED Requirements

### Requirement: The NeoAssemblyLoader binds a deserialized .neo to live ILMethods via an AOT-init dual-path (S1: non-generic, same-AppDomain)

The Step-25 runtime loader SHALL be a new Neo-only static class
`NeoAssemblyLoader` (`ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs`, gated
`#if ENABLE_NEO_MODE`) that consumes a deserialized `NeoAssemblyModel` (Step 23)
and attaches each `NeoMethodDefRecord` to a LIVE `ILMethod` in the SAME
`AppDomain` that compiled the `.neo`. For each record, the loader SHALL resolve
the record's `MethodRefIdx` to its `MethodReferencePatchInfo`, resolve the
declaring-type full name to an `ILType` via `appdomain.LoadedTypes[fullName]`,
match the method by name + parameter count to a non-generic `ILMethod` on that
type, and invoke an `InitCodeBodyFromNeo(record)` AOT-init on the matched
`ILMethod`. The loader SHALL operate on NON-GENERIC methods only for V1 (a
`NeoMethodDefRecord` is always non-generic by the Step-24 partition; generic
definitions live in the `TemplateTable` and are NOT consumed by the S1 loader).
The loader SHALL run in the SAME AppDomain as the compile, so the runtime token
hash maps (`mapTypeToken` / `mapMethod`) populated at Cecil-load + JIT-compile
time resolve the deserialized bodies' token operands NATURALLY -- the S1 loader
SHALL NOT perform cross-AppDomain hash re-resolution (deferred to S3). A type
that is not loaded, or a method that does not match, SHALL be recorded in a
skip report and omitted (the method KEEPS its JIT path -- the additive
contract); the loader SHALL NOT abort on a miss.

#### Scenario: Each NeoMethodDefRecord attaches to a live non-generic ILMethod
- **WHEN** `NeoAssemblyLoader.Attach(appdomain, model)` is invoked on a
  `NeoAssemblyModel` whose probe types are Cecil-loaded in `appdomain`
- **THEN** for every `NeoMethodDefRecord`, the loader SHALL resolve the
  declaring type via `LoadedTypes[fullName]`, match the method by name +
  parameter count, and call `ilm.InitCodeBodyFromNeo(record)`
- **AND** the matched `ILMethod`'s `isNeoAotBody` flag SHALL be `true` after the
  attach
- **AND** the loader SHALL NOT attempt to attach a generic-method definition
  (the `TemplateTable` is not consumed by S1) nor a generic-method instance (a
  runtime artifact, absent from `MethodDefs`)

#### Scenario: A miss is skipped, not fatal
- **WHEN** a `NeoMethodDefRecord`'s declaring type is not in `LoadedTypes`, or
  no method on the type matches the name + parameter count
- **THEN** the loader SHALL record the miss in the skip report (type-or-method
  identifier + reason) and continue with the remaining records
- **AND** the unmatched method SHALL keep its JIT path (no `isNeoAotBody` set;
  `BodyRegister` behaves as before)
- **AND** the loader SHALL return a report enumerating the attached and skipped
  methods

#### Scenario: Same-AppDomain token operands resolve naturally (no hash re-resolution)
- **WHEN** an attached AOT body executes (via `ExecuteNeo`) and a token operand
  (`Call`/`Callvirt` `Operand2`, `Box`/`Isinst`/`Castclass`/`Initobj` `Operand`,
  a `Ldstr` token) is resolved
- **THEN** the operand hash SHALL resolve via the EXISTING `mapTypeToken` /
  `mapMethod` / string-interner maps (populated at Cecil-load + JIT-compile time
  in the same AppDomain), WITHOUT any cross-AppDomain hash re-registration
- **AND** the S1 loader SHALL NOT record or rewrite compile-time hashes (that
  is the deferred S3 mechanism)

### Requirement: The ILMethod AOT-init populates CompiledFrame from a .neo NeoMethodDefRecord, bypassing JIT (the Cecil path stays as the reference + fallback)

`ILMethod` (`ILRuntime/CLR/Method/ILMethod.cs`) SHALL gain a Neo-only internal
`bool isNeoAotBody` flag (default `false`) + an internal
`InitCodeBodyFromNeo(NeoMethodDefRecord)` that populates the `compiledFrame`
struct field (`:58`) directly from the Step-23 record: `NeoExecuteBody`,
`LocalInfos`, `ParamInfos`, `TotalStructSize`, `TotalRefSize`,
`ParamPrimitiveSize`, `ParamReferenceCount`, `LocalsPrimitiveSize`,
`LocalsReferenceCount`, `ReturnPrimitiveSize`, `ReturnRefCount`,
`StackRegisterCount`, `LocalIsReference`, `NeoCatchException*`, `SwitchTargets`
(rebuilt from the serialized `KeyValuePair<int,int[]>[]`), and `NeoCallParams`
(rebuilt from `NeoCallParamMapRecord[]`, with the `PrimitiveByRefElemType` CLR
`System.Type[]` resolved back from assembly-qualified names). The AOT-init SHALL
additionally rebuild the runtime `Method.ExceptionHandler[]` EH structures
DIRECTLY from the `NeoExceptionHandlerRecord[]` body-index table (no Cecil, no
`addr[]` map) and set the ILMethod-level mirrors (`bodyRegister`,
`stackRegisterCnt`, `jumptablesR`) + the `isNeoAotBody` flag. The `BodyRegister`
getter (`:389`) SHALL short-circuit on the flag (return the AOT body without
calling `InitCodeBody`) when `isNeoAotBody` is `true`. The `ExecuteNeo` runtime
SHALL be UNCHANGED (it already reads only `method.CompiledFrame` + the AppDomain
hash maps -- no Cecil at execution time). The Cecil / JIT path
(`InitCodeBody(true)` -> `JITCompiler.Compile`) SHALL remain byte-identical when
`isNeoAotBody` is `false` (the reference + the fallback). All AOT-init additions
SHALL be gated `#if ENABLE_NEO_MODE`.

#### Scenario: InitCodeBodyFromNeo populates every load-bearing frame field
- **WHEN** `ilm.InitCodeBodyFromNeo(record)` is invoked on a matched ILMethod
- **THEN** `ilm.CompiledFrame.NeoExecuteBody` SHALL be the record's
  `NeoExecuteBody` (byte-for-byte the deserialized raw 24-byte `OpCodeR[]`)
- **AND** `ilm.CompiledFrame.LocalInfos` / `ParamInfos` / every scalar size +
  count / `LocalIsReference` / `NeoCatchException*` SHALL equal the record's
  fields
- **AND** `ilm.bodyRegister` SHALL equal the record's `NeoExecuteBody`,
  `ilm.stackRegisterCnt` SHALL equal the record's `StackRegisterCount`, and
  `ilm.isNeoAotBody` SHALL be `true`

#### Scenario: EH structures rebuild from body-index records without Cecil
- **WHEN** a method with a try/catch handler is AOT-attached
- **THEN** the rebuilt `Method.ExceptionHandler[]` SHALL carry
  `TryStart`/`TryEnd`/`HandlerStart`/`HandlerEnd`/`FilterStart` as the record's
  BODY INDICES (mapping 1:1 to `NeoExecuteBody` positions), `HandlerType` as the
  Cecil `ExceptionHandlerType`, and `CatchType` resolved to the runtime `IType`
  via the declaring AppDomain
- **AND** `ExecuteNeo`'s EH dispatch SHALL match a thrown exception to the
  correct catch clause using the rebuilt structures (no Cecil `addr[]` map)

#### Scenario: The BodyRegister getter short-circuits on the AOT flag
- **WHEN** `ilm.BodyRegister` is read on an AOT-attached ILMethod
  (`isNeoAotBody == true`)
- **THEN** the getter SHALL return the pre-populated `bodyRegister` WITHOUT
  calling `InitCodeBody` (no JIT, no Cecil `def.Body` read)
- **AND** `ExecuteNeo` SHALL run the method directly against the AOT body

#### Scenario: The JIT path is byte-identical when the AOT flag is false
- **WHEN** an ILMethod has `isNeoAotBody == false` (the default; every method
  not AOT-attached)
- **THEN** the `BodyRegister` getter SHALL behave exactly as before this change
  (lazily call `InitCodeBody(true)` -> `JITCompiler.Compile` when
  `bodyRegister == null`)
- **AND** the per-occurrence JIT instantiation path for generic methods SHALL be
  unchanged (the AOT-init does not touch the `BodyRegister` getter's
  generic-template hook)

### Requirement: The V2 functional self-check proves deserialize + ExecuteNeo == JIT for a non-generic probe matrix (the capstone gate)

The V2 load-bearing gate for Step 25 SHALL be a host-side self-check
(`NeoStep25LoadExecCheck.Run(appdomain)`, `#if ENABLE_NEO_MODE && DEBUG`) driven
via the existing `ILRuntimeTestCLI` special-mode hook (`if (nameFilter ==
"NeoStep25LoadExec")`), mirroring the Step-22 / Step-23 / Step-24 self-check
pattern. The self-check SHALL: (1) select a small dedicated probe ILType
(non-generic methods only) Cecil-loaded in the test AppDomain; (2) compile a
`.neo` for the probe via the SAME `NeoCompiler.Compile(IReadOnlyList<ILType>,
Stream)` driver the CLI uses into an in-memory `MemoryStream`; (3) read it back
via `NeoAssemblyReader.Read` -> `NeoAssemblyModel`; (4) run each probe method
via the JIT path and capture the result BEFORE attach; (5)
`NeoAssemblyLoader.Attach(appdomain, model)` to attach the deserialized AOT
bodies; (6) run the SAME methods again via the AOT body (`ExecuteNeo`) and
capture the result; (7) assert the JIT result EQUALS the AOT result via the
value/divide path (`if (!Equal(jit, aot)) int x = 1/0;`). The probe matrix SHALL
include an arithmetic method, a method with an exception handler, and a method
with various locals / a byref parameter (non-generic only). The probe type
SHALL be a dedicated `TestCases/NeoStep25LoadProbe.cs` (NOT the full
`TestCases.dll`, to stay sub-second + within the BCL-refs-only boundary). This
is the FIRST end-to-end "deserialize + execute" proof.

#### Scenario: The V2 self-check passes for the non-generic probe matrix
- **WHEN** `NeoStep25LoadExecCheck.Run(appdomain)` is invoked host-side
  (DEBUG+Neo)
- **THEN** every probe method's JIT-run result SHALL equal its AOT-run result
  (the divide-assert does NOT trip)
- **AND** the self-check SHALL compile the `.neo`, read it back, attach, and
  re-run via the SAME `NeoCompiler` / `NeoAssemblyReader` / `NeoAssemblyLoader`
  seams the runtime exposes (no test-only compile path)

#### Scenario: The capstone covers EH + locals/byref shapes
- **WHEN** the probe matrix includes a try/catch method and a method with mixed
  locals + a byref parameter
- **THEN** the AOT execution SHALL produce the same result as the JIT execution
  for both shapes
- **AND** the EH rebuild (`InitCodeBodyFromNeo` -> `RebuildEHFromNeo`) SHALL be
  exercised by the try/catch cell (a mis-dispatch trips the divide-assert)

#### Scenario: Generic methods are out of scope for the V2 gate
- **WHEN** the probe type contains a generic method
- **THEN** the self-check SHALL NOT assert on the generic method's AOT execution
  (generic-instantiation-at-load is S2, deferred)
- **AND** the generic method's template SHALL roundtrip through the `.neo`
  (Step 23) but the S1 loader SHALL NOT instantiate it (it is not consumed)

### Requirement: The Step-25 loader + ILMethod AOT-init are additive, Neo-only, and Legacy-neutral

The `NeoAssemblyLoader`, the `NeoStep25LoadExecCheck` self-check, the
`NeoStep25LoadProbe` type, and the `ILMethod` AOT-init (`isNeoAotBody` +
`InitCodeBodyFromNeo` + the `BodyRegister` getter short-circuit) SHALL all be
gated `#if ENABLE_NEO_MODE` (the self-check file additionally `&& DEBUG`). The
`.neo` serializer, the JIT compiler, `ExecuteNeo`, the optimizer, the Step-22
template mechanism, and the Step-23/Step-24 artifacts SHALL NOT be modified.
The Cecil / JIT path SHALL remain as the reference + the fallback (when
`isNeoAotBody` is `false`, `BodyRegister` behaves exactly as before). Legacy
`ExecuteR` (`ILIntepreter.Register.cs`) SHALL be byte-identical to before this
change: the whole `NeoAOT/` addition + the AOT-init flag + the getter
short-circuit SHALL compile out under plain `Debug`, and a stash-toggle plain-
`Debug` + `useRegister=true` NeoStep-filter run SHALL show the SAME pre-existing
Legacy failure set with and without the change. The full NeoStep smoke
(205/205) + NeoStep23Roundtrip (15/15) + NeoStep22SelfCheck (55/55) +
NeoStep24CliRoundtrip (5/5) + the new NeoStep25LoadExec SHALL stay green (ZERO
regressions; the loader is a new path and the AOT-init defaults off).

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and the Legacy
  register VM runs the NeoStep-filter smoke
- **THEN** the smoke SHALL show the SAME pre-existing Legacy failure set with
  and without this change (stash-toggle proof), because the `NeoAssemblyLoader`,
  the self-check, the probe type, the `isNeoAotBody` flag, `InitCodeBodyFromNeo`,
  and the `BodyRegister` getter short-circuit are all gated `#if ENABLE_NEO_MODE`
  and compile out

#### Scenario: Regression smoke stays green (additive)
- **WHEN** the loader + the AOT-init + the self-check are added (`Debug_Neo`)
- **THEN** the NeoStep smoke SHALL stay 205/205, NeoStep23Roundtrip 15/15,
  NeoStep22SelfCheck 55/55, NeoStep24CliRoundtrip 5/5 (ZERO regressions; the
  loader is a new path and the AOT-init defaults off)
- **AND** the new `NeoStep25LoadExec` self-check SHALL pass (the V2 capstone)

#### Scenario: The JIT path is the reference + the fallback
- **WHEN** an ILMethod is NOT AOT-attached (`isNeoAotBody == false`, the default
  for every method the loader did not bind)
- **THEN** the method SHALL JIT-compile + run exactly as before this change
- **AND** an AOT-attached method whose AOT execution is found incorrect SHALL
  be recoverable by clearing `isNeoAotBody` (falling back to the JIT path) --
  the design does NOT remove the JIT/Cecil path
