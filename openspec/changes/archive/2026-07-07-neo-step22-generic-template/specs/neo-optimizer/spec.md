## ADDED Requirements

### Requirement: PatchEntry captures only T-identity operand sites, never byte offsets

The generic-method template mechanism SHALL record T-dependent opcode sites in
a `PatchEntry` table. A `PatchEntry` SHALL capture only T-IDENTITY-dependent
opcode FIELDS (the concrete type's type-token hash on `Initobj`/`Box`/`Unbox`/
`Unbox_Any`/`Isinst`/`Castclass`/`Newarr`/`Stobj`/`Ldobj`/`Constrained`/
`Ldelem_Any`/`Stelem_Any`; the resolved method-token hash on T-qualified
`Call`/`Callvirt`/`Call_Redirect`; the `Move` is-ref flag) at a fixed
instruction index. A `PatchEntry` MUST NOT capture a byte offset
(`DstOffset`/`SrcOffset`/`OperandOffset`/`Register1`/`Register2`/`Register3`),
because those are T-SIZE-dependent and CUMULATIVE (a change in a T-typed
slot's size shifts the offset of every subsequent slot), and MUST be re-derived
by re-running `AllocateLocalStackSpaces` + `LowerNeoOffsets` rather than
patched. A `PatchEntry.Field` SHALL be one of the STANDALONE `OpCodeR` fields
(`Operand`/`Operand2`/`Operand4`); it MUST NOT alias a wide-immediate field a
runtime consumer reads (the F-8 OpCodeR-union discipline). This split is grounded
in the per-occurrence JIT dump diff, which shows `Register1/2/3` are T-INVARIANT
across instantiations of the same generic definition while only `Operand`-level
fields and the lowered byte offsets vary.

#### Scenario: T=int vs T=long register body is identical
- **WHEN** the per-occurrence JIT compiles `T Fill<T>(T v, int n)` instantiated
  at `T=int` and at `T=long`
- **THEN** the two register-index `CodeBody` arrays MUST be identical (same
  length, same opcodes, same `Register1/2/3`, same `Operand`-level fields)
- **AND** the ONLY difference MUST be in `localInfos[0].Size` (the T-typed
  parameter: 4 vs 8 bytes) and the cumulative byte offsets derived from it
- **AND** therefore the PatchEntry tables for the two instantiations MUST
  contain NO byte-offset entries (the size difference is handled by re-running
  Allocate+Lower, not by a patch)

#### Scenario: T=object vs T=string lowered body is byte-identical (ref-share)
- **WHEN** the per-occurrence JIT compiles `T Fill<T>(T v, int n)` instantiated
  at `T=object` and at `T=string` (a body with NO T-identity type-token operand
  such as `Box T`/`Isinst T`)
- **THEN** the two `NeoExecuteBody` arrays MUST be byte-for-byte identical (same
  length, same opcodes, same operands INCLUDING the `Move` is-ref flag, same
  lowered offsets)
- **AND** therefore the two instantiations MUST share ONE cached body verbatim
  (no clone, no patch)

#### Scenario: T=struct adds Initobj and a type-token operand
- **WHEN** the per-occurrence JIT compiles `T Fill<T>(T v, int n)` instantiated
  at `T = an IL value type` (e.g. a 12-byte struct)
- **THEN** the register-index body MUST contain `Initobj` ops NOT present for
  `T=int` (the instruction COUNT differs)
- **AND** those `Initobj` ops MUST carry the concrete struct's type-token hash
  in `Operand` (a Kind-A `PatchEntry` site: `Field=Operand, Kind=TypeToken`)
- **AND** the PatchEntry extractor MUST record those sites so a re-instantiation
  at a DIFFERENT value type re-derives the correct struct token

#### Scenario: PatchEntry field never aliases a wide immediate
- **WHEN** the PatchEntry extractor records a site on an opcode whose
  `OpCodeREnum` is an immediate-branch or wide-immediate form (e.g. `Bnei_Un_R8`,
  `Ldc_I8`)
- **THEN** the recorded `PatchField` MUST be a standalone field whose byte range
  does not overlap a wide-immediate field the runtime reads
- **AND** the extractor MUST verify disjointness per opcode kind (the F-8
  `LowerNeoOffsets` discipline), never defaulting to `Register1/2/3` (which
  alias the byte offsets post-lowering)

### Requirement: CloneAndPatch output is structurally equivalent to the per-occurrence JIT body

For a generic method definition with a cached template, `CloneAndPatch(template,
concreteTypeArgs)` SHALL produce a `NeoExecuteBody` + frame layout that is
structurally equivalent to the body the per-occurrence JIT (`JITCompiler.Compile`
via `MakeGenericMethod`) produces for the same instantiation: identical length,
identical `OpCodeREnum` per index, and identical operands modulo the expected
T-derived values. The template body SHALL be the register-index `OpCodeR[]`
captured AFTER `CleanupRegister` and BEFORE `TypeSpecializeNeoOpcodes` (the
latest T-invariant artifact in the Compile pipeline). `CloneAndPatch` SHALL
re-run the T-dependent back-half (`TypeSpecializeNeoOpcodes` +
`AllocateLocalStackSpaces` + `LowerNeoOffsets`) on a clone of the template body
with the concrete type args. This is the V1 equivalence anchor and the
load-bearing correctness gate for Step 22 (there is no FAIL-on-HEAD functional
probe because the per-occurrence JIT already runs every generic method
correctly).

#### Scenario: CloneAndPatch body equals per-occurrence JIT body
- **WHEN** a generic method is instantiated at a concrete T (a primitive, an
  8-byte primitive, an IL struct, a reference type) and BOTH paths produce a
  body -- `CloneAndPatch(def.Template, T)` and `Compile(MakeGenericMethod(T))`
- **THEN** the two `NeoExecuteBody` arrays MUST be structurally equal (same
  length, same `Code`/`Register1/2/3`/`Operand`/`Operand2/3/4` per index), as
  asserted by a host-side comparator
- **AND** the two frame layouts (`TotalStructSize`, `TotalRefSize`,
  `localInfos[].Offset/RefOffset/Size/RefCount`) MUST be identical

#### Scenario: Equivalence holds across the T-profile matrix
- **WHEN** CloneAndPatch is exercised over a matrix of generic-method shapes
  (a T-typed local/param/return; a `T[]` + `ref T` variant; a nested generic;
  a generic method on a generic type) and concrete T's spanning primitive /
  8-byte-primitive / IL-struct / reference-type
- **THEN** the V1 structural equivalence MUST hold for every cell of the matrix
- **AND** any cell that FAILS equivalence MUST fall through to the per-occurrence
  JIT path (correctness preserved; only the optimization is lost), not produce a
  wrong result

#### Scenario: Functional roundtrip via both paths yields identical results
- **WHEN** a generic method is invoked via the template path and via the
  per-occurrence path with identical inputs
- **THEN** the observable results MUST be identical (the V2 sanity gate)

### Requirement: ref-type-share and value-type-CloneAndPatch discrimination at instantiation

At generic-instantiation time (the `BodyRegister` getter of a generic-instance
`ILMethod`), the mechanism SHALL discriminate: if EVERY concrete generic
argument is a reference type AND the cached template's patch table contains no
T-identity `TypeToken`/`MethodToken` site whose value would differ across
reference types, the instantiation SHALL share ONE cached "ref body" verbatim
(no clone); otherwise (any value-type arg, or a token-bearing ref body) the
instantiation SHALL `CloneAndPatch`. The discriminator SHALL read the concrete
`IType[]` (available at instantiation from `ILMethod.GenericArugmentsArray`).

#### Scenario: All reference-type args share one body
- **WHEN** a generic method is instantiated at `T=object`, `T=string`,
  `T=SomeILClass` (all reference types) and the body has no T-identity token
- **THEN** all three instantiations MUST reference the SAME cached `NeoExecuteBody`
  array instance (no per-instance clone)

#### Scenario: Any value-type arg forces CloneAndPatch
- **WHEN** a generic method is instantiated at `T=int` (or any value type)
- **THEN** the instantiation MUST NOT share the ref body; it MUST produce its
  own `CloneAndPatch`-derived body (the frame byte size differs)

#### Scenario: Token-bearing ref body is cloned-and-patched, not shared
- **WHEN** a generic method body contains a T-identity token (e.g. `Box T`) and
  is instantiated at `T=object` vs `T=string`
- **THEN** the two instantiations MUST NOT share a body (the type-token operand
  differs); each MUST `CloneAndPatch` so the concrete type-token is patched

### Requirement: Template cache is additive, Neo-only, and Legacy-neutral

The template mechanism SHALL be an ADDITIVE cache layer: the per-occurrence JIT
instantiation path (`MakeGenericMethod` -> `JITCompiler.Compile`) SHALL remain
as the reference and the fallback when no template is cached for a definition.
The template field, the `BodyRegister`-getter hook, the `PatchEntry` struct, and
`CloneAndPatch` SHALL all be gated `#if ENABLE_NEO_MODE` (Neo-only). Legacy
`ExecuteR` (`ILIntepreter.Register.cs`) MUST be byte-identical to before this
change: the template code SHALL compile out under plain `Debug`, and a stash-
toggle plain-`Debug` + `useRegister=true` NeoStep-filter run MUST show the SAME
pre-existing Legacy failure set with and without the change. The full NeoStep
smoke (204/204) + NeoOptHardening (24/24) + NeoStep20 (9/9) MUST stay green
(template path must not change any existing JIT behavior).

#### Scenario: Per-occurrence JIT path stays as the fallback
- **WHEN** a generic-instance `ILMethod` has no cached template for its
  definition (e.g. template building was skipped or failed)
- **THEN** the `BodyRegister` getter MUST fall through to the per-occurrence
  `JITCompiler.Compile` path, producing the same body as before this change

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** the Legacy NeoStep-filter smoke MUST show the SAME pre-existing
  failures with and without this change (stash-toggle proof), because the
  template field, the `BodyRegister` hook, and `CloneAndPatch` are all gated
  `#if ENABLE_NEO_MODE` and compile out

#### Scenario: NeoStep regression smoke stays green
- **WHEN** the template mechanism is enabled (`Debug_Neo`) and the full NeoStep
  smoke is run
- **THEN** NeoStep MUST stay 204/204, NeoOptHardening 24/24, NeoStep20 9/9
  (ZERO regressions; the template path is additive and does not change existing
  JIT behavior for non-generic or already-cached methods)
