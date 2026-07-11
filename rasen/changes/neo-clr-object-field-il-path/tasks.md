# Implementation Tasks

> Neo-gated change. Build CLI with `Debug_Neo --no-incremental`; build TestCases
> with plain `Debug` (NEVER `Debug_Neo`). Always run the CLI with `-f net8.0`.

## 1. Rework the `GetNeoILInstance` guard

File: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (helper at
~`:6070`, immediately before the Area-4d `NeoReadClrObjectField` block).

- [ ] 1.1 Replace the body so it discriminates three owner shapes (keep the
      `[MethodImpl(AggressiveInlining)]` attribute and the `static` signature
      `GetNeoILInstance(AutoList mStack, int objIndex)`):
      - `objIndex < 0` -> `throw new NullReferenceException();` (unchanged).
      - Read `object o = mStack[objIndex];`.
      - `o == null` -> `throw new NullReferenceException();` (CLR semantics for
        ldfld/stfld/ldobj/stobj on null; replaces the former misleading NIE).
      - `o is ILTypeInstance ins` -> `return ins;` (unchanged fast path).
      - `o is CrossBindingAdaptorType cba` -> if `cba.ILInstance == null`
        `throw new NullReferenceException();` else `return cba.ILInstance;`
        (unwrap; mirrors the raw `Ldfld`/`Stfld` handler, child 9).
      - otherwise -> `throw new NotImplementedException("Step 17/13b: field/
        element access on a CLR object via the IL-instance path is deferred (CLR
        field-hash plumbing lands in Step 13b). Owner type: " +
        o.GetType().FullName);` (keep the defensive fail-loud guard, now
        reachable only by genuinely unexpected CLR shapes).
- [ ] 1.2 Verify `CrossBindingAdaptorType` resolves without qualification in
      this file (child 9 already uses `((CrossBindingAdaptorType)target).ILInstance`
      in the raw handler ~`:3758`, so the type is in scope; if not, add the
      `using ILRuntime.Runtime.Adaptors;` / `CLR` namespace already imported).
- [ ] 1.3 Build: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
      Debug_Neo --no-incremental` -> 0 errors.

## 2. Add the NeoStep probe

File: `TestCases/NeoStepClrObjIlPathTest.cs` (new).

- [ ] 2.1 Add the probe class per `design.md` Decision 4: an IL holder with a
      `public static <Holder> Lazy;` (uninitialized null IL static ref field)
      and two `public static void` probes named so the `NeoStep` filter picks
      them up (`NeoStepClrObjIl_TC1_NullOwnerLdfldNre`,
      `NeoStepClrObjIl_TC2_NullOwnerLdfldRefNre`). Each wraps a typed `ldfld`
      on `Lazy.<field>` in `try { ...; int z=1; int d=0; int _=z/d; }
      catch (NullReferenceException) { }` so it FAULTs on HEAD (NIE not caught
      -> propagates) and PASSES after the fix (NRE caught). Use the full
      comments from the verified file.
- [ ] 2.2 Build TestCases: `dotnet build TestCases/TestCases.csproj -c Debug`.

## 3. Verify (FAULT-on-HEAD / PASS-after)

- [ ] 3.1 Stash-toggle the fix (revert the null branch to throw the NIE), rebuild
      CLI, run `NeoStepClrObjIl` filter -> confirm both probes FAIL (NIE
      propagates, exit != 0). Restore the fix.
- [ ] 3.2 With the fix: `dotnet run -c Debug_Neo -f net8.0 --project
      ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStepClrObjIl` -> both pass
      (exit 0).
- [ ] 3.3 NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project
      ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> **330/0** (328 baseline
      + 2 probes). No regression.
- [ ] 3.4 Legacy-neutral: build CLI plain `Debug`, run the same `NeoStep` filter
      with `useRegister=true` -> **330 ran / 17 failed** (the documented 17-
      failure baseline; both new probes pass under Legacy).

## 4. Documented non-goals (do NOT chase in this change)

- [ ] 4.1 DO NOT fix the lazy-init `ceq`/`brfalse`-on-reference null-comparison
      gap (root cause of the null owner in `TestStaticFieldInstance` /
      `RegisterVMTest04`; the brtrue-on-reference / delegate-cache pattern child
      3 deferred). Separate child.
- [ ] 4.2 DO NOT fix IL value-type `newobj` `this` ([VT-THIS-ADDR]; root cause of
      `StructTest14`'s null owner). Separate (high-value) step.
- [ ] 4.3 DO NOT fix the `DelegateExt` hit (Step 19 delegates).
- [ ] 4.4 DO NOT lower `Stsfld`/`Ldsfld` or change their IL-static offset
      resolution (the raw-`DstOffset` gap is real but unverifiable while 4.1
      blocks the round-trip; lowering risks the delegate-cache gap). Leave the
      IL-static arms as-is.
