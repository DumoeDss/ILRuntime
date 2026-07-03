# Tasks — Neo Step 16: Array element access

One implementer pass. Reference: Legacy `ILIntepreter.Register.cs:4879-5304` (do NOT
modify). DEFERRED items are marked `[DEFERRED]` — do NOT implement this pass.

## 1. JIT / lowering plumbing

- [x] 1.1 In `Optimizer.Neo.cs` `LowerNeoOffsets`, add `Newarr`, `Ldlen`, all
      `Ldelem_*`, and all `Stelem_*` to the lowering switch. Lower Register1/2/3 to
      byte offsets (`DstOffset`/`SrcOffset`) and carry the dest/src ref offsets in
      `Operand3`/`Operand4` (mirror the `Box`/`Isinst` arm @430-447). Encode the third
      register's byte offset (index for Ldelem, value for Stelem) in a spare scratch
      field — confirm the available `OpCodeR` fields and document the chosen encoding
      in a comment. Do NOT clobber `Operand` (the element-type token for Newarr /
      Ldelem_Any / Stelem_Any).
- [x] 1.2 Verify `JITCompiler.Translate` already allocates a dest ref slot for `Newarr`
      (Register1 is a ref-type temp). If the dest ref slot / its `RefOffset` is not yet
      surfaced to the lowering pass, extend the per-opcode info so `Operand3` (dest ref
      offset) is populated. Confirm `Newarr`'s `Operand` carries the element-type token
      (`method.GetTypeTokenHashCode(token)`, already set @2064).

## 2. Newarr arm

- [x] 2.1 In `ILIntepreter.Neo.cs` `ExecuteNeo`, add `case OpCodeREnum.Newarr`. Read
      count from `frameBase + SrcOffset`; resolve element type via
      `AppDomain.GetType(ip->Operand)`. Allocate per design §3: CLR primitive/CLR ref
      type → `CLRType.CreateArrayInstance` or `Array.CreateInstance`; IL VT →
      `new ILTypeInstance[n]` + pre-instantiate each slot via `((ILType)et).Instantiate(true)`;
      IL ref type → `new ILTypeInstance[n]` (null elements). Store the array on the
      mStack at `frameRefBase + Operand3`, write its index into `frameBase + DstOffset`.

## 3. Ldlen arm

- [x] 3.1 Add `case OpCodeREnum.Ldlen`. Read array ref from `frameBase + SrcOffset`
      (null → `NullReferenceException`); write `((Array)obj).Length` into
      `frameBase + DstOffset` (native int).

## 4. Ldelem arms

- [x] 4.1 Add primitive-typed arms `Ldelem_I1/U1/I2/U2/U4/I4/I8/R4/R8`. Read array mStack
      index + index byte offset; cast to the typed CLR array; indexer read into
      `frameBase + DstOffset`. Match Legacy's bool/sbyte/char disambiguation for the
      I1/I2/U1/U2 arms where relevant. (OOB → CLR `IndexOutOfRangeException`.)
- [x] 4.2 Add `Ldelem_Ref` / `Ldelem_Any`. Detect `ILTypeInstance[]`: VT unboxed
      element → `CopyILToFrame` element → dest frame region (primitive + ref); ref
      element → store object on mStack at dest ref offset, write index to dest slot.
      Else `Array.GetValue` + VT-vs-ref decision on the runtime value.

## 5. Stelem arms

- [x] 5.1 Add primitive-typed arms `Stelem_I1/I2/I4/I8/R4/R8`. Typed CLR array indexer
      write from the value's frame byte slot.
- [x] 5.2 Add `Stelem_Ref` / `Stelem_Any`. `ILTypeInstance[]`: VT element →
      `CopyFrameToIL` dest element instance ← value frame region (primitive + ref); ref
      element → store the value's mStack object into the array slot. Else CLR
      `Array.SetValue` / typed cast.

## 6. Tests (`TestCases/NeoStep16Test.cs`, ASCII)

- [x] 6.1 CLR primitive array: `int[] arr = new int[5]; arr[0]=42; arr[4]=7;` assert
      `arr[0]==42 && arr[4]==7 && arr.Length==5` (use the NeoStep assertion convention
      — pass silently / `1/0` on failure, same as NeoStep15Test).
- [x] 6.2 Long/float/double primitive arrays (one case each) to cover I8/R4/R8 arms.
- [x] 6.3 IL reference-type array: `MyClass[] a = new MyClass[3]; a[1] = new MyClass(5);`
      assert `((MyClass)a[1]).val == 5` and `a[0] == null`.
- [x] 6.4 IL value-type array: `MyStruct[] a = new MyStruct[2]; a[0] = s;` (struct with
      a primitive field and a reference field) assert both fields round-trip and that
      mutating the source struct after `Stelem` does NOT change the stored element
      (value semantics). This exercises the primitive+ref CopyBlock path.
- [x] 6.5 `Ldlen`: assert `new int[7].Length == 7`.

## 7. Verify + regression

- [x] 7.1 Build CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      → 0 errors. Build TestCases: `dotnet build TestCases/TestCases.csproj -c Debug`
      (NEVER Debug_Neo). Watch for CJK corruption — author ASCII.
- [x] 7.2 Run full NeoStep smoke:
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
      → all green (prior 65 + new NeoStep16 cases). NO existing case regresses; some
      previously-NIE array tests may turn green. Kill + investigate any test >10s
      (infinite loop).

## 8. [DEFERRED — do NOT implement this pass]

- [ ] 8.1 `OpCodeREnum.Ldelema` — deferred to Step 17 (its only consumers are
      `stind`/`ldind`/`fixed`/`ref`-`out`, all Step 17). Leave as Step-tagged NIE.
- [ ] 8.2 Generic-with-token `Code.Ldelem` / `Code.Stelem`, native-int `Code.Ldelem_I`
      / `Code.Ldelem_U8` — not enumerated by JIT `Translate`; rare in C#. Address when
      a real test needs them.
- [ ] 8.3 Multi-dimensional arrays (`int[,]`) — out of scope (rank-1 only this step).
