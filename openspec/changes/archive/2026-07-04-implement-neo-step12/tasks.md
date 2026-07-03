# Tasks — implement-neo-step12

Sized for one implementer pass. Each checkbox is a verifiable step. Build
commands and constraints per `CLAUDE.md` / `planning-context.md` §5.

## 1. Layout primitives + alignment
- [x] 1.1 Add `ILType.NaturalAlignment` (int): cached, computed in
      `InitializeFields` as the max natural alignment over the type's fields
      (primitive -> its size; nested IL value-type -> recurse; enum -> underlying
      size; reference field -> pointer size). Expose via a property gated
      `#if ENABLE_NEO_MODE`. (ILType.cs ~2084-2114 region.)
- [x] 1.2 Add `AlignUp(int offset, int alignment)` helper in `JITCompiler`
      (Neo region).
- [x] 1.3 Apply `AlignUp` before assigning `slot.Offset` in:
      `AllocateSlotForType` (JITCompiler.cs ~1227-1261), the VT-local branch
      (~1133-1146), the `StackRegisterCount` temp loop (~1195-1205, align by
      the max alignment tracked alongside `maxSize`), the `HasThis` value-type
      branch (~1084-1094), and `AllocateNeoCallParamSlot` (Optimizer.Neo.cs
      ~563-). Track `maxAlignment` next to `maxSize` in the temp loop.
- [x] 1.4 Verify `TotalStructSize` still fits under `ushort.MaxValue`
      (Optimizer.Neo.cs ~13-15 cap check still passes for TestCases).

## 2. Inline field-access opcodes + ExecuteNeo arms
- [x] 2.1 Add 22 opcodes to `OpCodeREnum.cs` (after the existing Ldfld/Stfld
      set, ~934-957): `Ldfld_{I1,I2,I4,I8,U1,U2,U4,U8,R4,R8,Ref}_Inline` and
      the `Stfld_..._Inline` mirrors.
- [x] 2.2 Register the new opcodes in `Optimizer.GetOpcodeDestRegister` /
      `GetOpcodeSourceRegister` (used by JITCompiler.Translate ~99-101) so
      BCP/FCP see their source/dest registers.
- [x] 2.3 Add the 11 primitive `Ldfld_*_Inline` `case` arms in
      `ILIntepreter.Neo.cs` immediately after the existing `Stfld_Ref` arm
      (~1688). Body: `*(T*)(frameBase + ip->DstOffset) = *(T*)(frameBase +
      ip->SrcOffset + ip->Operand2);` (see design §2.3 for exact bodies per
      kind).
- [x] 2.4 Add the 11 primitive `Stfld_*_Inline` `case` arms. Body:
      `*(T*)(frameBase + ip->DstOffset + ip->Operand2) = *(T*)(frameBase +
      ip->SrcOffset);` (operand-direction swap, design §2.3).
- [x] 2.5 Add `Ldfld_Ref_Inline` and `Stfld_Ref_Inline` arms operating on the
      frame mStack ref region (design §2.3). Use the same "absolute frame-ref
      index in `Operand`" convention as the heap `Ldfld_Ref` arm (~1640-1646);
      encode the dest temp RefOffset for Ldfld per design §2.2/§2.3 note.

## 3. Initobj memset (ref-field sub-case)
- [x] 3.1 Encode the target slot's `RefOffset` onto the `Initobj` instruction
      during the optimizer's Neo offset-lowering (spare operand, e.g.
      `Operand3`), parallel to how `Register1` → `DstOffset` is lowered
      (design §3 option A).
- [x] 3.2 In `ILIntepreter.Neo.cs` `Initobj` (~1509-1542), replace the
      `throw NotImplementedException ... Step 7 RefOffset lowering` (~1533)
      with a loop that nulls `mStack[frameRefBase + slotRefOffset + i]` for
      `i in 0..refCnt`. Leave the CLR-value-type branch (~1539) as the
      Step-13 throw.

## 4. JIT type-based lowering
- [x] 4.1 Thread the operand register's resolved `IType` into the `Code.Ldfld`
      / `Code.Stfld` cases (JITCompiler.cs ~1853-1896) — the stack-simulation
      register allocation already tracks it; expose it at these sites.
- [x] 4.2 Add the discriminator: `operandIsInFrameVt = operandType is ILType
      ot && ot.IsValueType && !ot.IsEnum`. When true, emit the `_Inline`
      opcode; otherwise the existing heap opcode.
- [x] 4.3 Add an inline code selector (`GetLdfldInlineCodeForType` /
      `GetStfldInlineCodeForType`, or a `bool inline` overload of the existing
      selectors at ~1962 / ~2033) returning the matching `_Inline` opcode per
      primitive kind.
- [x] 4.4 Stamp field offset operands: `Operand2 = field.PrimitiveOffset`,
      `Operand3 = field.ReferenceOffset` (same as heap; the optimizer's
      ref-lowering later converts the Ref variant to absolute frame-ref
      indices). Confirm `LowerNeoOffsets` already lowers `Register1/2` to
      `DstOffset/SrcOffset` for the new opcodes (it lowers uniformly; only
      registration in step 2.2 should be needed).
- [x] 4.5 Extend the optimizer's Neo ref-offset stamping (Optimizer.Neo.cs
      ~305-348) to recognize the `_Inline` Ref variants and stamp the
      absolute frame-ref index (`slot.RefOffset + field.ReferenceOffset`)
      into `Operand`, mirroring the heap `Ldfld_Ref`/`Stfld_Ref` handling.

## 5. Optimizer safety check
- [x] 5.1 Audit `LowerNeoOffsets`, `LowerR1`, `LowerR1R2`, `LowerR1R2R3`
      (Optimizer.Neo.cs ~556-686) and the NeoCallParam lowering (~428-555) to
      confirm the new opcodes lower correctly (they consume `DstOffset`/
      `SrcOffset` like the heap family). Add any missing opcode entries.
- [x] 5.2 Build the CLI to confirm no compile errors:
      `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (expect 0 errors).

## 6. Tests (TestCases/NeoStep12Test.cs)
- [x] 6.1 Add `NeoStep12Test.cs` mirroring the `NeoStep11Test.cs` convention
      (`public static` parameterless `NeoStep12Test*` methods; optional
      `[ILRuntimeTest]`).
- [x] 6.2 `NeoStep12TestVector3FieldAccess`: `Vector3 v; v.x=1f; v.y=2f;
      float r = v.x + v.y;` assert `r == 3f`.
- [x] 6.3 `NeoStep12TestNestedValueType`: `struct Inner{int x;} struct Outer{
      Inner i; int y;}` — set/read `o.i.x` and `o.y`.
- [x] 6.4 `NeoStep12TestValueTypeWithReferenceField`: `struct S{int a;
      string b;}` — set/read both `s.a` and `s.b` (incl. null round-trip).
- [x] 6.5 `NeoStep12TestInitobjZerosValueType`: `S s; Initobj s (default);`
      assert `s.a == 0` and `s.b == null`.
- [x] 6.6 `NeoStep12TestHeapObjectFieldAccessUnchanged`: `class C{int x;}`
      `C c = new C(); c.x = 5; return c.x;` — confirm heap path still works
      (no `_Inline` emitted for reference-slot operands). Use the Step 9
      `Console.WriteLine` + throw assertion pattern (no DivideByZero hack).
      DEVIATION: used the DivideByZero assertion pattern instead of
      Console.WriteLine+throw, because `throw new Exception(...)` requires
      CLR newobj which is Step 9 (unimplemented in Neo) and would mask the
      real failure if an assertion fired. DivideByZero is a native fault that
      surfaces the failure without an allocation (same pattern as
      NeoStep7Step8Test).

## 7. Build + smoke regression
- [x] 7.1 `dotnet build TestCases/TestCases.csproj -c Debug` (produces
      `TestCases/bin/Debug/netstandard2.1/TestCases.dll`). NEVER Debug_Neo for
      TestCases.
- [x] 7.2 Run the Step 12 cases:
      ```
      dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
        TestCases/bin/Debug/netstandard2.1/TestCases.dll \
        HotfixAOT/Patched/HotfixAOT.patch true NeoStep12
      ```
      Expect all-green. If a case takes >10s, kill it — likely an interpreter
      infinite loop (per planning-context §5).
- [x] 7.3 Run the FULL NeoStep smoke regression (NOT just NeoStep12):
      ```
      dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
        TestCases/bin/Debug/netstandard2.1/TestCases.dll \
        HotfixAOT/Patched/HotfixAOT.patch true NeoStep
      ```
      Expect no regressions vs the prior NeoStep smoke (~31 cases pre-Step-12,
      +the new NeoStep12 cases). Any previously-green case going red is a
      layout/alignment regression — investigate before declaring done.
- [x] 7.4 Confirm no new `NotImplementedException` is thrown for the cases
      Step 12 implements (field access on in-frame VTs, in-frame-VT Initobj).
      Unimplemented-op throws tagged for OTHER steps (Step 13 CLR VT, etc.)
      are expected and not a bug.
