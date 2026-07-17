## 1. IMPLEMENT — GetPrimitiveSize enum + CLR-value-type sizing (site 1)

- [x] 1.1 In `AppDomain.GetPrimitiveSize(IType)` (`ILRuntime/Runtime/Enviorment/AppDomain.cs`, the Neo-gated `else` at ~line 2305), add a branch BEFORE the throw: if `fieldType.IsValueType && fieldType.TypeForCLR != null && !(fieldType is CLR.TypeSystem.ILType)` return `Optimizer.GetNeoValueTypeManagedSize(fieldType.TypeForCLR)`. Ensure the `using ILRuntime.Runtime.Intepreter.RegisterVM;` is present (for `Optimizer`) or fully-qualify the call.
- [x] 1.2 Replace the residual bare `throw new NotImplementedException();` with a TAGGED throw: `$"Neo GetPrimitiveSize: unsupported IType '{fieldType?.FullName}' (not a primitive/enum/CLR-value-type) [neo-bare-nie]"`.
- [x] 1.3 Confirm `Optimizer.GetNeoValueTypeManagedSize` (Optimizer.Neo.cs:1625) is reachable from AppDomain and handles enums (Enum.GetUnderlyingType) + structs (Unsafe.SizeOf) without throwing — no change needed there, just verify.

## 2. GUARD — Neo-reachable resolution/splitter residuals (sites 2-6)

- [x] 2.1 `JITCompiler.GetLdfldCodeForType` residual (`JITCompiler.cs:3013`, Neo-gated): tag the throw with the field type (`$"Neo GetLdfldCodeForType: IL field '{fieldType?.FullName}' has no typed Ldfld opcode [neo-bare-nie]"`).
- [x] 2.2 `JITCompiler.GetStfldCodeForType` residual (`JITCompiler.cs:3098`, Neo-gated): tag the throw analogously.
- [x] 2.3 `JITCompiler` token-resolution `else` (`JITCompiler.cs:2915`, shared): tag with the token shape.
- [x] 2.4 `AppDomain` type-resolution `else` (`AppDomain.cs:1717`, shared): tag with the token shape.
- [x] 2.5 `AppDomain` method-reference param-list `else` (`AppDomain.cs:2162`, shared): tag with the method-reference shape.

## 3. PROBES — NeoStep regression (FAULT on HEAD)

- [x] 3.1 Add a `NeoStep` probe (public static no-arg method in `TestCases/NeoStep*Test.cs`) that calls a CLR method taking an enum argument (e.g. a host helper taking `System.Reflection.BindingFlags`, mirroring the DelegateTest36-40 reproducer) and asserts the enum value round-trips. Must FAULT on HEAD (JIT NIE during LowerNeoOffsets).
- [x] 3.2 Add a `NeoStep` probe that performs a CLR-struct (`TestVector3`) value-type copy through a byref (`stobj`/`ldobj`) and asserts the fields copy correctly. Must FAULT on HEAD (runtime NIE in the Stobj/Ldobj arm).
- [x] 3.3 Stash-toggle verify: with the site-1 fix reverted, both probes throw the (bare) NIE; with the fix in, both pass. Confirm the pre-fix throw is the bare default message and the post-fix residual (if forced) is tagged.

## 4. VERIFY — build + smoke

- [x] 4.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors) and `dotnet build TestCases/TestCases.csproj -c Debug` (TestCases NOT built with Debug_Neo).
- [x] 4.2 NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => 316 + new probes, 0 failed.
- [x] 4.3 Legacy-neutral check: `dotnet run -c Debug -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => same ran/failed baseline as child 4 (new probes also pass under Legacy).
- [x] 4.4 Full (unfiltered) Neo smoke: re-capture, grep for `"The method or operation is not implemented"` (use `-a`, the file has NUL bytes from the crash) — confirm the ~16 GetPrimitiveSize hits are gone and any residual throws are tagged. Record whether any NEW bare-NIE site surfaces (previously masked) for the LEAD.
