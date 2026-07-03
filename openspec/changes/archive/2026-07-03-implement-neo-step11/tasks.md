## 1. Interface offset map on ILType

- [x] 1.1 Add `InterfaceEntry` struct (fields: `IType InterfaceType`, `int VTableOffset`, `string[] MethodSlotKeys`, optional `int[] ClassSlotRemap`) under `#if ENABLE_NEO_MODE` in `ILRuntime/CLR/TypeSystem/ILType.cs`
- [x] 1.2 Add fields `InterfaceEntry[] neoInterfaceMap`, `Dictionary<IType,int> neoInterfaceOffsets`, `bool neoInterfaceMapBuilding` next to the existing `neoVTable*` fields
- [x] 1.3 Implement `EnsureNeoInterfaceMap()` / `BuildNeoInterfaceMap()` mirroring `EnsureNeoVTable()` / `BuildNeoVTable()`: call `EnsureNeoVTable()` first, then walk `this.Implements` (and recursively each interface's `Implements` for inheritance chains), assign each interface a `VTableOffset` + 0-based `MethodSlotKeys`, populate `neoInterfaceOffsets`, use `ClassSlotRemap` for the non-contiguous fallback
- [x] 1.4 Implement public query API: `GetInterfaceVTableOffset(IType)`, `TryGetInterfaceVTableOffset(IType, out int)`, `TryGetInterfaceMethodSlot(IType, IMethod, out int)`, and `GetInterfaceMethodSlotSelf(IMethod)` (0-based slot of a method within its own interface type, lazy `Dictionary<string,int>` on the interface ILType)
- [x] 1.5 Add `neoInterfaceMapBuilding` recursion guard that throws `InvalidOperationException` (same wording style as the Step 10 `neoVTableBuilding` guard)

## 2. New opcode

- [x] 2.1 Add `Callvirt_Interface` to `OpCodeREnum` in `ILRuntime/Runtime/Intepreter/OpCodes/OpCodeREnum.cs` immediately after `Callvirt_CLR`, with an XML doc comment consistent with the Step 10 `Callvirt_IL` / `Callvirt_CLR` comments
- [x] 2.2 Add a `ToString` arm for `Callvirt_Interface` in `OpCode.cs` if the per-opcode switch there covers the other `Callvirt_*` variants

## 3. JIT lowering

- [x] 3.1 In `JITCompiler.InitializeCallvirtDispatch` (`JITCompiler.cs` ~line 2107), add the interface branch FIRST: when `targetMethod is ILMethod ilMethod` and `(ilMethod.DeclearingType as ILType)?.IsInterface == true`, set `op.Code = Callvirt_Interface` and `op.Operand4 = EncodeCallvirtInterface(ifaceMethodSlot, 0)`, then `return` before the existing IL/CLR branches
- [x] 3.2 Add `static int EncodeCallvirtInterface(int interfaceMethodSlot, int thisArgOffset)` next to `EncodeCallvirtDispatch` (same `(thisArgOffset << 16) | (slot & 0xffff)` bit layout, separate name for readability)
- [x] 3.3 Verify `thisArgOffset` gets patched at the call site the same way Step 10 patches it for `Callvirt_IL` (trace where `EncodeCallvirtDispatch`'s `0` second arg is later overwritten); apply the same patch for `Callvirt_Interface`

## 4. Optimizer call-ABI pass

- [x] 4.1 In `Optimizer.Neo.cs` call-ABI arm (~line 375-390), add `case OpCodeREnum.Callvirt_Interface:` alongside `Callvirt` / `Callvirt_IL` / `Callvirt_CLR` so push/register bookkeeping and the `hasConstrained` check treat it like the other callvirt variants
- [x] 4.2 Confirm the new opcode flows through any other optimizer pass that enumerates `Callvirt_*` (grep for `Callvirt_IL` across `Optimizer.*.cs` and add the new opcode where needed)

## 5. Interpreter handler

- [x] 5.1 Add `ResolveNeoCallvirtInterfaceTarget(OpCodeR*, IMethod, byte*, AutoList)` next to `ResolveNeoCallvirtILTarget` in `ILIntepreter.Neo.cs` (~line 221): read this via `ReadNeoCallThis`, require `ILTypeInstance`, call `TryGetInterfaceVTableOffset`, compute `baseSlot + (ip->Operand4 & 0xffff)`, bounds-check `neoVTable`, return the implementing method, throw `MissingMethodException` on any failure (no ip overrun, no null-deref)
- [x] 5.2 Add `case OpCodeREnum.Callvirt_Interface:` arm in the call/callvirt switch (~line 1376, after `Callvirt_CLR`), mirroring the `Callvirt_IL` arm exactly but calling `ResolveNeoCallvirtInterfaceTarget`
- [x] 5.3 Confirm CLR-`this`-through-interface throws `InvalidOperationException` (not silent mis-dispatch) per design Decision 7

## 6. Tests

- [x] 6.1 Create `TestCases/NeoStep11Test.cs` following the `NeoStep10Test.cs` convention (`public static` parameterless methods, `AssertEqual` helper, optional `[ILRuntimeTest]`)
- [x] 6.2 Add case: IL class implements single IL interface, call through interface variable dispatches to implementation
- [x] 6.3 Add case: derived override dispatched through base-typed interface variable (verifies vtable override, not base)
- [x] 6.4 Add case: one class implements multiple interfaces, each interface variable dispatches to its own method without cross-pollution
- [x] 6.5 Add case: interface inheritance chain — call through parent-interface variable on a class implementing the derived interface
- [x] 6.6 Add case: IL class implements a CLR interface (`IDisposable`) on the IL side, call `Dispose()` through the interface variable
- [x] 6.7 Add case (negative): object whose runtime type does not implement the target interface throws a clear exception (use a deliberately incompatible cast scenario the harness can assert on)

## 7. Build and regression

- [x] 7.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` — 0 errors (transitively builds ILRuntime / ILRuntimeTestBase / LitJson)
- [x] 7.2 `dotnet build TestCases/TestCases.csproj -c Debug` — produces `TestCases/bin/Debug/netstandard2.1/TestCases.dll` (NEVER build TestCases with Debug_Neo)
- [x] 7.3 Run NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` — all green (NeoStep11 cases pass, existing NeoStep cases still pass)
- [x] 7.4 Re-run with the Step 10 filter (e.g. `NeoStep10`) to confirm no Step 10 virtual-dispatch regression
