# Tasks — neo-clr-vt-reffields-binder

- [x] 1. **Diagnose the data path (do FIRST — load-bearing).** Add TEMP diagnostic
      instrumentation to `InvokeNeoClrMethod` / the InitializeArray path (or a temporary
      Neo redirect) that, for `RuntimeHelpers.InitializeArray`, dumps param 0's mStack
      object type and param 1's first 8 bytes + the mStack entry they index. Run
      `NeoStepLdtoken_TC5` under Neo. Determine: (a) does the Neo `ldtoken` field path
      (`ILIntepreter.Neo.cs:1575-1617`) currently push the `<PrivateImplementationDetails>`
      blob as a `byte[]` reference, as flat bytes, or as nothing useful? (b) what are the
      blob struct's computed `TotalPrimitiveSize` / `TotalReferenceCount`? Record the
      answer; it picks sub-option (preferred) vs (fallback) in task 3. REMOVE the
      diagnostic before shipping. Also confirm whether Legacy populates TC5's array
      correctly or zero-fills (run TC5 under Legacy) to calibrate.

- [x] 2. **Add the Neo redirect.** Implement
      `CLRRedirections.InitializeArrayNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)`
      in `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` under
      `#if ENABLE_NEO_MODE`. Read param 0 (the `Array`) via
      `ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack)`; obtain the
      initializer `byte[]` from param 1; pin + `Marshal.Copy` into the array (mirror the
      Legacy body at `CLRRedirections.cs:372-376`). Void method — no `retDst` write. Model
      the param-read pattern on `DelegateCombineNeo` (`CLRRedirections.cs:513`). Mind the
      cursor: param 1 is an ~`RuntimeFieldHandle`-wide slot consumed as a 4-byte ref read
      (verify no off-by-N).

- [x] 3. **Make the blob reachable (ldtoken remediation).** Per the task-1 diagnosis:
      - **(preferred)** If the blob is NOT already surfacing as a `byte[]` reference,
        extend the Neo `ldtoken` field path to detect the RVA-blob case (the static value
        at `ManagedObjects[off.ReferenceOffset]` is a `byte[]`) and push it as a reference
        (`mStack.Add(blob); *(int*)dstSlot = mStack.Count - 1;`) so param 1 carries the
        index. Reuse the existing reference-field materialisation arm — do not duplicate.
      - **(fallback)** If the blob bytes already sit in `Primitives` at the field's
        `PrimitiveOffset`, have the redirect read `PrimitiveSize`-many bytes directly from
        `targetBase + off1` and `Marshal.Copy` those.
      Either way the pass bar is TC5's `arr[i] == 100+i` for all 32 elements.

- [x] 4. **Register the redirect on `RedirectMapNeo`.** In the `AppDomain` constructor
      (`ILRuntime/Runtime/Enviorment/AppDomain.cs`, near line 149-150), under
      `#if ENABLE_NEO_MODE`, add
      `RegisterCLRMethodRedirectionNeo(typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod("InitializeArray"), CLRRedirections.InitializeArrayNeo);`.
      Reuse the same `GetMethod("InitializeArray")` lookup as the Legacy line (the 2-param
      overload). Confirm first-registered-wins does not collide with any autogen Neo
      binding for InitializeArray (none expected).

- [x] 5. **Add the probe.** Create `TestCases/NeoClrVtReffieldsBinderTest.cs` with
      `NeoClrVtReffieldsBinder_TC1_ArrayInitializer`: a `new int[]{ 100..131 }` (32
      distinct ints, NO try/catch) followed by an explicit
      `if (arr[i] != 100+i) throw`/assert. FAULT criterion: without the fix the uncaught
      Step-13b NIE makes the test FAIL. Add a second probe
      `NeoClrVtReffieldsBinder_TC2_NonIntArrayInitializer` with a large `double[]` (or
      `long[]`) initializer to cover the element-type-agnostic scenario from the spec.

- [x] 6. **Verify — stash-toggle.** With the change reverted (HEAD), confirm TC1 FAILs
      (uncaught Step-13b NIE) and the full smoke still shows ~18 `RuntimeFieldHandle`
      Step-13b occurrences. With the change applied, TC1 + TC2 PASS with correct contents.

- [x] 7. **Verify — smoke.**
      - Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors).
      - NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
        => baseline 318/0; after, 318+N/N passes, 0 new failures. `NeoStepLdtoken_TC5`
        must still PASS (now via correct contents, not via the dead NIE catch).
      - Full Neo smoke (no filter; crashes pre-completion — known): capture to a file and
        grep `CLR value type with reference fields` => the `RuntimeFieldHandle` count is 0.
      - Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` filter => same
        ran/failed baseline set; the new probes pass under Legacy too.

- [x] 8. **Cleanup.** Remove ALL temporary diagnostic instrumentation from task 1.
      Confirm `git status` shows only the intended source edits
      (`CLRRedirections.cs`, `AppDomain.cs`, `ILIntepreter.Neo.cs`, the new test file) —
      no stray debug files. Do not touch the autogen Step-13b stub
      (`BindingGeneratorExtensions.cs:206`) — it stays as the honest guard for genuine
      no-binder ref-field CLR structs (out of scope).
