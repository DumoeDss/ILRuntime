# Review Report -- neo-step23-neoassembly (Step 23 .neo serializer)

Reviewer: VERIFY (adversarial non-author code review). Author != verifier.
Date: 2026-07-07. Branch: `features/object-model-overhaul`. HEAD pre-Step-23.
Skill: `openspec-gstack-review` (scoped to the load-bearing properties in the
LEAD brief -- this is a format-fidelity + robustness review, not a generic
PR lint).

## VERDICT: APPROVE-WITH-FINDINGS

No Blocker. No Major. The Step 23 `.neo` format + serializer/deserializer are
CORRECT and the V1 roundtrip gate is SOUND. Six Minor/Trivial findings below;
none gate the review-loop. Four accepted-known Step-25 deferrals recorded (not
findings). The review-loop may close.

The handoff's lesson (green smoke + a passing self-check do NOT prove
correctness) was applied: I constructed 6 adversarial cells the 9-cell matrix
misses (multi-Constrained template, multi-clause EH, a real
`PrimitiveByRefElemType` value, multi-interface type, zero locals, an empty-ish
body), confirmed all roundtrip (15/15), then REMOVED them. One real coverage
gap surfaced (the aqname path was never exercised with a non-empty value) and
was closed by probe -- recommend permanent adoption.

## Build + test evidence (re-verified independently)

- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors.
- `dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors.
- `NeoStep` regression smoke: **Ran 205 tests, 0 failed** (unchanged).
- `NeoStep23Roundtrip` V1 self-check: **9/9 cells PASS** (baseline, post-revert).
- `NeoStep22SelfCheck`: **55/55 PASS** (the `ILType.cs` accessor is the only
  shared-code touch; Step 22 also reads `ILType` -- confirmed unbroken).
- Adversarial run (temporary): **15/15 cells PASS** (9 shipped + 6 probes).
- Legacy-neutral: independently PROVEN at the IL level (see property 7).

## Property-by-property findings

### 1. Comparator strictness -- STRICT ENOUGH (one narrow blind spot)

The 4 comparators were read against the ACTUAL `CompiledFrame` /
`StackSlotInfo` / `GenericMethodTemplate` struct definitions
(`JITCompiler.cs:67-108`, `GenericMethodTemplate.cs:83-148`).

- **OpCodeRsEqual (raw-24-byte)** -- TOTAL. `MemoryMarshal.AsBytes<OpCodeR>`
  produces `length * sizeof(OpCodeR)` bytes and `SequenceEqual` compares every
  one. The V1 PASS empirically proves `sizeof(OpCodeR) == 24` (else `CopyTo`
  into the `length*24` buffer would throw), so this covers ALL 24 bytes of
  EVERY record. Stronger than the canonical 8-field `BodiesEqual`. (Trivial
  nit: `int bytes = a.Length * 24;` at `NeoStep23RoundtripCheck.cs:272` is dead
  -- computed, never used. Remove or use `Unsafe.SizeOf<OpCodeR>()`.)
- **MethodDefsEqual** -- compares every load-bearing `CompiledFrame` field. I
  verified ALL 20 frame fields against the struct: `NeoExecuteBody`,
  `LocalInfos`, `ParamInfos`, the 10 size scalars, `LocalIsReference`,
  `NeoCatchException{RegIndex,ByteOffset,RefOffset}`, `SwitchTargets`,
  `NeoCallParams`, `ExceptionHandlers`. `CodeBody` + `Symbols` are correctly
  omitted (inliner/debugger only; Cecil-Instruction-keyed). NO load-bearing
  field is missed. The ONE field not compared: `MethodRefIdx` (the identity).
- **TypeDefsEqual** -- total over layout/fields/VTable/interfaces/static-ctor.
  Not compared: `TypeRefIdx` (the type's own identity).
- **TemplatesEqual** -- total over body/patches/scalars/all arrays. Not
  compared: `DefinitionMethodRefIdx`.

Could two non-equal records compare equal? Only if a field the comparator
IGNORES differs. After the 3 identity-ref fields, the only ignored things are
the intentionally-omitted `CodeBody`/`Symbols`/`Addr`/`RefBody` (correct).
--> See Minor-1: the 3 identity ref-idx fields are the sole blind spot.

Matrix field-divergence coverage: non-generic, generic+template (1 Constrained
pair), single-clause EH, diverse locals, byref params (own params), switch,
typedef (1 iface). The divergence space NOT covered by the shipped matrix --
multi-Constrained template, multi-clause EH, a real `PrimitiveByRefElemType`,
multi-interface type -- was closed by probes (all PASS). See Minor-3.

### 2. Serializer/deserializer correctness beyond the 9 V1 cells -- PROVEN

6 adversarial cells added temporarily; all roundtripped (15/15 total), then
REMOVED (working tree confirmed clean):

| Probe | Exercises | Result |
|---|---|---|
| `NestedEH` (try/catch/catch/finally) | 3 EH clauses, mixed HandlerType (Catch,Catch,Finally) | PASS len=22 |
| `MultiConstrainedGeneric<T>` template | 2 Constrained+callvirt pairs -> patches=6, constrainedT=2, constrainedM=2 (Step-22 BLOCKER shape, doubled) | PASS |
| `ClrByrefCall` (int.TryParse) | `NeoCallParamMap.PrimitiveByRefElemType` = System.Int32 aqname (nonEmptyAqnameSlots=1) + null->"" norm | PASS |
| `ZeroLocals` | `LocalInfos.Length == 0` | PASS len=3 |
| `NeoStep23MultiIfaceProbe` (2 interfaces) | `Interfaces.Length == 2` TypeDef | PASS ifaces=2 |
| Full-model with 10 methods | whole-pipeline byte-equality on 5 non-generic bodies | PASS |

The diagnostic confirmed the original 9 cells ALL had `nonEmptyAqnameSlots=0`
-- i.e. the `PrimitiveByRefElemType` aqname serialization + the null->""
normalization (one of the 2 bugs caught at apply) was NEVER exercised with a
real value by the shipped matrix. The `ClrByrefCall` probe closed that gap and
proved it roundtrips a real `System.Int32`.

The 2 apply-time bugs were inspected:
- **null->"" normalization** (`NeoAssemblyWriter.cs:82, 240, 688`): symmetric
  -- `ResolveAqName` treats "" as null; `BinaryReader.ReadString` roundtrips ""
  exactly. Correct.
- **header/table offset collision** (`WriteModel`, lines 508-549): fixed by the
  build-all-blobs-first approach -- each table is buffered into its own
  `MemoryStream` (`Buf*`), offsets computed from buffer lengths, then header +
  blobs emitted in one pass. No seek/patch. Correct and robust.

### 3. OpCodeR raw-24-byte choice -- SOUND; one hardening gap

- `OpCodeR` is `[StructLayout(LayoutKind.Explicit)]`, all primitive fields, no
  object refs -> unmanaged/blittable. `MemoryMarshal.AsBytes`/`Cast` are sound.
- `sizeof(OpCodeR) == 24` empirically confirmed (V1 PASS). Max offset+size =
  `Operand4`@20+4 = 24; 24 is a multiple of the default pack 8 -> no trailing
  padding. Portable across x64/arm64-le.
- `Version=1` guard IS meaningful: reader rejects a mismatched version with
  `NotSupportedException` (`NeoAssemblyReader.cs:342`).
- **Endianness**: `MemoryMarshal` is HOST-NATIVE. The header `Endianness=1`
  byte is WRITTEN but the reader never VALIDATES it (`NeoAssemblyReader.cs:347`
  stores it into `header.Endianness`, no compare/reject). On a big-endian host
  a `.neo` written little-endian would silently corrupt every multi-byte
  `OpCodeR` field. All current targets are LE, so acceptable now. See Minor-2.

### 4. HybridPatch reuse -- CORRECT extension, NO accidental coupling

- Magic `0x494C524E` ("ILRN") distinct from HybridPatch `0x58883551`. A `.neo`
  is never a `.patch`.
- The reader invokes ONLY the leaf `TypeReferencePatchInfo.FromStream` /
  `MethodReferencePatchInfo.FromStream` / `FieldReferencePatchInfo.FromStream`
  + `ReadStringTable`. It NEVER calls `AssemblyPatchInfo.FromStream` or any
  patch-application logic. No accidental coupling.
- `NeoRefTableBuilder` reuses the `*PatchInfo.Create` factories verbatim
  (signatures confirmed: `AssemblyInfo.cs:261, 309, 340, 538, 568`).
- `NeoTypeRefKind` (Il/Clr) is wrapped AROUND each TypeRef (writer: byte then
  info; reader: byte then info). Clean separation.

### 5. Q1/Q2/Q3 resolutions -- SOUND

- **Q1 (CodeBody NO)**: a deserialized method carries `NeoExecuteBody` (what
  `ExecuteNeo` runs). `CodeBody` (register-index; one-way lowering) is
  correctly omitted. A deserialized method IS runnable without `CodeBody`.
  Correct -- Step 25 needs only `NeoExecuteBody` to run.
- **Q2 (VTable slot-keys)**: TypeDef serializes `VTableMethodRefIdxs` (the
  `IMethod[]` slot -> method-ref array -- slot LAYOUT faithful) + per-interface
  `MethodSlotKeys[]`. The class reverse slot-key map (`neoVTableSlots`) is NOT
  serialized; Step 25 rebuilds it. Confirmed: slot order preserved, Step 25 can
  rebuild the reverse map from method signatures.
- **Q3 (static initial values)**: static LAYOUT (`StaticTotalPrimitiveSize`/
  `Count`) + `StaticCtorMethodRefIdx` ARE serialized; raw byte initial-values
  are NOT (the Neo runtime seeds statics by EXECUTING the `.cctor`, not from
  byte blobs). Correct for the runtime model. Accepted-known: a `.cctor`-less
  static with a non-default initializer is the deferred gap (recorded).

### 6. CLR interface implementor -1 TypeRef idx -- FAITHFUL, clearly Step-25

`BuildInterfaces` (`NeoAssemblyWriter.cs:798-819`): a CLR interface type yields
`ilIface = e.InterfaceType as ILType == null` -> `InterfaceTypeRefIdx = -1`.
The slot LAYOUT (`VTableOffset`, `MethodSlotKeys`, `ClassSlotRemap`) is recorded
faithfully regardless. No silent corruption -- only the CLR-interface identity
ref is -1. Step 25 indexes CLR types by assembly-qualified name. Clearly a
Step-25 follow-up (documented in code comment + planning-context), NOT a bug.
(My `NeoStep23MultiIfaceProbe` probe confirmed the 2-IL-interface path: both
got real TypeRef idxs, `ifaces=2` roundtripped.)

### 7. Additive + Legacy-neutral -- PROVEN at the IL level

- All new code is `#if ENABLE_NEO_MODE` (`NeoAssembly.cs`,
  `NeoAssemblyWriter.cs`, `NeoAssemblyReader.cs`; `NeoStep23RoundtripCheck.cs`
  is `#if ENABLE_NEO_MODE && DEBUG`). The `ILType.cs` accessor
  (`NeoInterfaceMapForAOT`, `:751`) and the `Program.cs` filter are additive
  inside `#if ENABLE_NEO_MODE`.
- plain `Debug` builds with 0 errors.
- **IL-level neutral PROVEN independently**: a byte-search of the plain-Debug
  `ILRuntime.dll` shows NONE of `NeoAssemblyFormat` / `NeoAssemblyWriter` /
  `NeoInterfaceMapForAOT` / `NeoStep23RoundtripCheck` are present; all ARE
  present in the `Debug_Neo` DLL. The `#if` gating compiles out cleanly.
  (Build is deterministic: two identical-source plain-Debug builds produced
  identical SHA256 -- so the IL is stable and the gating is the sole cause of
  the symbol absence.)
- See Minor-4: the "byte-identical ILRuntime.dll" claim is technically
  imprecise (the embedded portable PDB records per-source-file document
  checksums, so the DLL BYTES differ even though the IL does not).

## Findings

### Minor-1: V1 comparators skip the 3 identity ref-idx fields
- `NeoStep23RoundtripCheck.cs:278` (`MethodDefsEqual`) -- does not compare
  `MethodRefIdx`; `:408` (`TypeDefsEqual`) -- does not compare `TypeRefIdx`;
  `:439` (`TemplatesEqual`) -- does not compare `DefinitionMethodRefIdx`.
- Observation: these fields ARE serialized (first int written) and read (first
  int read), so they roundtrip positionally, and same-process ref-table dedup
  is deterministic -- they are correct by inspection. But the V1 gate does not
  ASSERT equality, so a future write/read-order bug in an identity idx would
  slip the gate. These idxs are load-bearing for Step 25's loader (they ARE
  how the loader maps a MethodDef/TypeDef to its identity).
- Recommended fix: add the 3 comparisons (one line each). Closes the only
  comparator blind spot.

### Minor-2: Endianness byte is informational-only, not enforced
- `NeoAssemblyReader.cs:347` reads `header.Endianness` but never validates it;
  `NeoAssemblyWriter.cs:537` writes `Endianness=1` (little-endian).
- Observation: `MemoryMarshal.AsBytes`/`Cast` are host-native. A `.neo` written
  LE and read on a BE host would silently corrupt every multi-byte `OpCodeR`
  field (and the table offsets). All current targets are LE (x64/arm64-le), so
  no live impact. The design doc DOES state little-endian.
- Recommended fix: in `Read`, compare `header.Endianness` to
  `NeoAssemblyFormat.EndiannessLittle` and throw `NotSupportedException` on
  mismatch (mirrors the Version guard). Or add a one-line format-doc note:
  ".neo is little-endian only; a BE host must byte-swap."

### Minor-3: Matrix under-covers 4 divergence paths (recommend permanent probes)
- The shipped 9 cells leave 4 paths unexercised: (a) a multi-Constrained-pair
  template (the Step-22 BLOCKER shape, >1 pair), (b) a multi-clause/nested EH
  method, (c) a `PrimitiveByRefElemType` with a REAL aqname value (diagnostic:
  all 9 cells have `nonEmptyAqnameSlots=0` -- the null->"" normalization path
  this-fixes was never hit with a real value), (d) a multi-interface type.
- Observation: my temporary probes for all 4 PASS (15/15). The serializer is
  correct today. But these paths are exactly the fragility surfaces (the 2
  apply-time bugs lived in (c) and the offset logic). Without coverage, a
  future regression here is invisible.
- Recommended fix: adopt `ClrByrefCall`, `MultiConstrainedGeneric<T>` (+ its
  template cell), `NestedEH`, and a 2-interface TypeDef as permanent matrix
  inputs + self-check cells. (Cheap -- the test methods + cells already exist
  in this review's diff; re-apply them.)

### Minor-4: "byte-identical ILRuntime.dll" claim overstated
- `planning-context.md:441` (and presumably the ship-log): "the compiled
  ILRuntime.dll is byte-identical to HEAD under plain Debug."
- Observation: FALSE at the byte level. SHA256 of plain-Debug `ILRuntime.dll`:
  with Step 23 = `982376c0...`, at HEAD = `6998c29e...` (deterministic, stable
  across rebuilds). The delta is ENTIRELY embedded-portable-PDB source-document
  metadata (each compiled `.cs` file contributes a document entry + content
  checksum; adding the 4 NeoAOT files + editing `ILType.cs` changes the PDB,
  hence the DLL bytes). The IL/metadata-tables are identical (Step-23 symbols
  absent -- see property 7). Benign, but the claim as written is misleading.
- Recommended fix: reword to "no IL/code/metadata-table difference -- all
  Step-23 symbols are absent from the plain-Debug DLL" (which is the stronger,
  correct statement).

### Trivial-5: Dead local in OpCodeRsEqual
- `NeoStep23RoundtripCheck.cs:272`: `int bytes = a.Length * 24;` -- computed,
  never used. The real compare is `sa.SequenceEqual(sb)` over the full
  `AsBytes` span (which compares `sizeof(OpCodeR)` bytes -- actually STRICTER
  than the literal 24). Remove the line, or replace `24` with
  `Unsafe.SizeOf<OpCodeR>()` if an explicit size is desired.

### Trivial-6: BodiesEqual human-readable diff fallback not wired
- `tasks.md` 8.3 says "Reuse `GenericMethodTemplateOps.BodiesEqual` for a
  human-readable opcode diff on failure." The self-check does not call it; on
  failure the diff is just `"NeoExecuteBody mismatch (a=len, b=len)"`. Minor
  diagnosability gap (not a correctness issue). Optional: on OpCodeR mismatch,
  run `BodiesEqual` and emit the first divergent index for easier triage.

## Accepted-known (Step-25 deferrals -- NOT findings)

Recorded so they are not re-litigated. All confirmed faithful-at-V1 or
clearly-scoped:

1. **Cross-AppDomain token-hash re-resolution** -- the raw `OpCodeR` body
   carries runtime token HASHES; same-process V1 is exact; a `.neo` written by
   one AppDomain and loaded by another needs the loader to re-resolve hashes
   via the reference tables. Step 25. (The reference tables ARE serialized now
   so Step 25 can rebuild hash -> object maps.)
2. **CLR interface implementor types recorded with -1 TypeRef idx** -- Step 25
   indexes CLR types by assembly-qualified name. Slot LAYOUT is faithful
   (confirmed: property 6). Not a hidden bug.
3. **CLR-type aqname indexing + `PrimitiveByRefElemType` byref-element
   re-resolution via the AppDomain** -- Step 25. (`ResolveAqName` is the V1
   placeholder.)
4. **Static `.cctor` seeding** -- Q3: static LAYOUT + `.cctor` MethodRefIdx
   serialized; raw byte initial-values deferred (runtime seeds by executing
   the `.cctor`). Accepted: a `.cctor`-less static with a non-default
   initializer is the deferred gap.
5. **V2 functional (deserialize -> ExecuteNeo)** -- Step 25. V1 ships
   structural equivalence only.
6. **`CodeBody` (register-index) serialization** -- Q1: correctly omitted
   (inliner/debugger only; one-way lowering). If a future step needs it, add a
   second raw `OpCodeR[]` under a Version bump.

## Adversarial probes -- summary (constructed, confirmed, REMOVED)

Per the brief: add to V1 temporarily, confirm roundtrip, remove. All 6 probes
PASS (see property 2 table). Working tree confirmed clean post-revert (no
leftover temp files, stash empty, 0 residual adversarial markers in the
shipped self-check/test). Cross-process / cross-AppDomain roundtrip was NOT
attempted -- same-process V1 is the scoped gate (cross-AppDomain token
re-resolution is Step 25); confirmed same-process is what shipped.

## Regression gate (all GREEN, independently re-run)

- NeoStep: 205/205 (0 failed). NeoStep22SelfCheck: 55/55. NeoStep23Roundtrip:
  9/9. Legacy-neutral: plain-Debug DLL has 0 Step-23 symbols (IL-level proof).
- No Blocker, no Major open. APPROVE-WITH-FINDINGS; the review-loop may close.
  Recommend the implementer adopt Minor-1 (3-line comparator tighten) +
  Minor-3 (permanent probes) before archive; Minor-2/4 are doc/hardening;
  Trivial-5/6 optional.
