# Tasks -- neo-step23-neoassembly

Implementation checklist. Each task is scoped so it can be verified independently
before the next begins. All new code lives under `ILRuntime/Runtime/NeoAOT/` and
is gated `#if ENABLE_NEO_MODE` (Neo-only; Legacy-neutral).

## 1. Format model + header

- [x] 1.1 Create `ILRuntime/Runtime/NeoAOT/NeoAssembly.cs` (`#if ENABLE_NEO_MODE`):
      the format-model structs. `NeoHeader` (Magic "ILRN", Version=1,
      Endianness, Reserved, 7 TableOffsets). The record structs:
      `NeoMethodDefRecord`, `NeoTypeDefRecord`, `NeoFieldLayoutRecord`,
      `NeoInterfaceEntryRecord`, `NeoTemplateRecord`, `NeoPatchEntryRecord`,
      `NeoCallParamMapRecord`, `NeoExceptionHandlerRecord`. (Field shapes per
      design.md D3-D6; ASCII doc-comments only.)
- [x] 1.2 Constants: `Magic = 0x494C524E`, `Version = 1`, `Endianness = 1`
      (little-endian). Distinct from HybridPatch's `0x58883551`.

## 2. Reference tables (REUSE HybridPatch)

- [x]2.1 In `NeoAssemblyWriter.cs`, build the `StringTable` (`HashSet<string>`
      -> `string[]`, length-prefixed UTF-8 via `BinaryWriter.Write(string)`).
      Provide an `IndexString(string)` helper returning the int index
      (dedup).
- [x]2.2 Build `TypeRefTable`: route each Cecil `TypeReference` through
      `TypeReferencePatchInfo.Create(tr, internalRefs)` (reused verbatim from
      `HybridPatch/PatchInfo/AssemblyInfo.cs`). Wrap in a NeoAOT record that
      adds a 1-byte `Kind` (Il=0 / Clr=1) discriminator. Provide
      `IndexTypeRef(TypeReference, IType)` -> int (dedup on the canonical record).
- [x]2.3 Build `MethodRefTable` via `MethodReferencePatchInfo.Create(mr,
      internalRefs)`; `IndexMethodRef(MethodReference)` -> int (dedup).
- [x]2.4 Build `FieldRefTable` via `FieldReferencePatchInfo.Create(fr,
      internalRefs, fieldIdxMapping)`; `IndexFieldRef(FieldReference)` -> int.
- [x]2.5 In `NeoAssemblyReader.cs`, deserialize the 4 tables via the existing
      `*PatchInfo.FromStream(br)` methods (the wrapper unwraps the `Kind` byte
      for TypeRef). No reinvention.

## 3. OpCodeR[] serialization (raw 24-byte)

- [x]3.1 `WriteOpCodeRArray(BinaryWriter, OpCodeR[])`: write `int length`,
      then the raw bytes via `MemoryMarshal.AsBytes<OpCodeR>(body.AsSpan())`
      copied to a `byte[]` (or a `Span<byte>` write). Confirm 24 bytes/record.
- [x]3.2 `ReadOpCodeRArray(BinaryReader)` -> `OpCodeR[]`: read length, read
      `length*24` bytes, `MemoryMarshal.Cast<byte, OpCodeR>` into the array.
- [x]3.3 Roundtrip-equivalence micro-check (inline in the V1 self-check):
      `MemoryMarshal.AsBytes` byte-equality between the original and the
      roundtripped array (stronger than `BodiesEqual`; keep `BodiesEqual` as
      the human-readable diff fallback).

## 4. CompiledFrame serialization

- [x]4.1 `WriteStackSlotInfoArray(BinaryWriter, StackSlotInfo[])`: count + 4
      ints per record (`Offset, RefOffset, Size, RefCount`).
- [x]4.2 `NeoCallParamMapRecord` write/read: ushort arrays
      (`PrimitiveSrc/Dst/Size`, `RefSrc/Dst`) as (len, vals); `bool[]` flags
      (`PrimitiveByRefSrc/WriteBack`); `System.Type[]` (`PrimitiveByRefElemType`)
      as assembly-qualified names (`Type.AssemblyQualifiedName`) + reader
      re-resolution via `Type.GetType(aqname)` (fall back to AppDomain lookup
      for byref element types).
- [x]4.3 `NeoExceptionHandlerRecord` write/read: `TryStartIdx/EndIdx`,
      `HandlerStartIdx/EndIdx`, `FilterIdx`, `HandlerType` (int), `CatchTypeRefIdx`.
      At serialize time, resolve the Cecil `ExceptionHandler` try/handler
      Instruction offsets to body INDICES via the JIT-time `addr[]` map; catch
      type -> TypeRef index. At deserialize time, materialize the structured
      table verbatim.
- [x]4.4 `WriteMethodDef(NeoMethodDefRecord, BinaryWriter)`: assemble all D3
      fields. `ReadMethodDef(BinaryReader)` -> `NeoMethodDefRecord`. Confirm
      `Symbols` and `CodeBody` are NOT serialized (comment why).

## 5. ILType metadata serialization (TypeDefTable)

- [x]5.1 `WriteTypeDef(ILType, BinaryWriter)`: `TypeRefIdx` (the type's own
      full name), `BaseTypeRefIdx`, `TotalPrimitiveSize`/`TotalReferenceCount`,
      `StaticTotalPrimitiveSize`/`StaticTotalReferenceCount`, the per-field
      `NeoFieldLayoutRecord[]` (`FieldRefIdx` + `PrimitiveOffset` +
      `ReferenceOffset`, sourced from `ILType.GetFieldOffset(idx)` /
      `fieldOffsets`).
- [x]5.2 VTable: `NeoVTable` (`IMethod[]`) -> `int[] VTableMethodRefIdxs` (each
      `IMethod` -> MethodRef index; a CLR-method slot flagged). Skip the reverse
      slot-key map (rebuilt by Step 25's loader; open question Q2).
- [x]5.3 Interface map: `NeoInterfaceEntryRecord[]` from `ILType`'s
      `neoInterfaceMap` (`InterfaceType`-ref, `VTableOffset`, `MethodSlotKeys`,
      `ClassSlotRemap` with a -1 sentinel for empty). Access via the existing
      internal `InterfaceEntry` struct (add an internal accessor if needed,
      gated `#if ENABLE_NEO_MODE`).
- [x]5.4 Static ctor: `StaticCtorMethodRefIdx` (-1 if none). Confirm static
      initial-values story (open question Q3) before shipping.

## 6. GenericMethodTemplate serialization (faithful)

- [x]6.1 `WriteTemplate(GenericMethodTemplate, BinaryWriter)`: `DefinitionMethodRefIdx`,
      `TemplateBody` (raw OpCodeR[] via 3.1), front-half scalars
      (`LocVarRegStart`, `TotalRegCnt`, `NeoCatchExRegFinal`,
      `StackRegisterCount`, `VarCnt`), `InitObjPrefixLength` +
      `InitObjPrefixRegisters[]`, `SwitchTargets`.
- [x]6.2 `VariableTypes` (`TypeReference[]`) -> `int[] VariableTypeRefIdxs` via
      `IndexTypeRef`. `ConstrainedTypeTokens` -> `int[]` TypeRef indices.
      `ConstrainedMethodTokens` -> `int[]` MethodRef indices.
- [x]6.3 `Patches[]` -> `NeoPatchEntryRecord[]`: for each `PatchEntry`, set
      `InstrIdx`, `Field`, `Kind`, `GenericParamIdx`, and `TokenRefIdx` by
      `Kind` (TypeToken -> TypeRef index via `IndexTypeRef((TypeReference)CecilToken)`;
      MethodToken -> MethodRef index via `IndexMethodRef((MethodReference)CecilToken)`;
      IsRefMoveFlag -> -1). Confirm EVERY extractor-recorded site is captured
      (the Step-22 `CallIt<Struct>` follow-up -- do NOT assume the prefix is
      the only Initobj site).
- [x]6.4 `ReadTemplate(BinaryReader)` -> materialize a template holder suitable
      for Step 25's CloneAndPatch (the in-memory `CecilToken` cannot be rebuilt
      without Cecil -- Step 25 re-resolves from `TokenRefIdx` + the ref tables;
      for Step 23 V1 the holder carries the indices + a flag noting Cecil-token
      re-resolution is deferred). Confirm `Symbols`/`Addr`/`RefBody` are NOT
      serialized (comment why).

## 7. Writer + Reader top-level

- [x]7.1 `NeoAssemblyWriter.Write(ILType[] types, ILMethod[] methods,
      GenericMethodTemplate[] templates, Stream)`: build all tables, write the
      header with computed `TableOffsets[]`, then the tables in order.
- [x]7.2 `NeoAssemblyReader.Read(Stream) -> NeoAssemblyModel`: validate
      Magic/Version, read header, seek to tables (offsets enable lazy access in
      Step 25), deserialize each.
- [x]7.3 A `NeoAssemblyModel` holder exposing the deserialized tables (this is
      what Step 24's CLI and Step 25's loader will consume).

## 8. V1 roundtrip self-check (host-side, DEBUG+Neo)

- [x]8.1 Create `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep23RoundtripCheck.cs`
      (`#if ENABLE_NEO_MODE && DEBUG`), mirroring `NeoStep22SelfCheck`'s shape
      (a `Run(AppDomain)` returning a `Result{Total,Passed,Failed,Failures}`).
- [x]8.2 For each matrix method (from `TestCases/NeoStep23NeoAssemblyTest.cs`):
      compile via the Neo JIT (per-occurrence), serialize the resulting
      `CompiledFrame` + the declaring `ILType` + any cached template to a
      `MemoryStream`, deserialize, and compare.
- [x]8.3 Comparators: `OpCodeR[]` byte-equality (3.3) + a `FramesEqual`
      helper (`StackSlotInfo[]` field-for-field, scalars, `LocalIsReference`,
      `NeoCatchException*`, `SwitchTargets`, `NeoCallParamMap[]` including
      aqname-resolved types) + a `TypeLayoutEqual` helper (TotalPrimitive/
      Reference + fieldOffsets) + a `TemplatesEqual` helper (per design D5).
      Reuse `GenericMethodTemplateOps.BodiesEqual` for a human-readable opcode
      diff on failure.
- [x]8.4 Matrix coverage: (a) non-generic method, (b) generic method WITH a
      template (force-build via the Step-22 capture path), (c) method with an
      exception handler, (d) method with diverse locals (primitive, IL
      value-type, CLR value-type), (e) method with byref params / ref return.
- [x]8.5 CLI hook: add a `NeoStep23Roundtrip` filter to `ILRuntimeTestCLI/
      Program.cs` (mirror the `NeoStep22SelfCheck` filter). Print
      `[NeoStep23] <method>: roundtrip PASS len=<n>` / `[FAIL] <diff>`.

## 9. Test assembly (the serialization inputs)

- [x]9.1 Create `TestCases/NeoStep23NeoAssemblyTest.cs` with the matrix methods
      (public static, parameterless where the V2 path needs them; the V1 host-
      side self-check loads the type + reflects). Cover: `ProbeBasic`-style non-
      generic, `Generic<T>` (template-eligible), `TryCatch` (EH), `MixedLocals`
      (primitive/IL-VT/CLR-VT), `ByrefParams` (ref/out + ref return).
- [x]9.2 Confirm the matrix methods compile cleanly under `Debug_Neo` (the V1
      self-check compiles them host-side).

## 10. Regression + Legacy-neutral proof

- [x]10.1 Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
       Debug_Neo` -> 0 errors. `dotnet build TestCases/TestCases.csproj -c
       Debug` -> 0 errors.
- [x]10.2 Run the V1 roundtrip self-check: `dotnet run -c Debug_Neo -f net8.0
       --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/
       netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true
       NeoStep23Roundtrip` -> ALL cells PASS.
- [x]10.3 NeoStep regression smoke: `... true NeoStep` -> 205/205.
       NeoOptHardening: `... true NeoOptHard` -> 24/24. NeoStep20: `... true
       NeoStep20` -> 9/9. ZERO regressions.
- [x]10.4 Legacy-neutral stash-toggle: stash the NeoAOT source + any new
       accessors, build plain `Debug`, run `... true NeoStep` with
       `useRegister=true`; record the failure set; unstash; re-run; confirm
       SAME failure set (byte-identical Legacy behavior).
