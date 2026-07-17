# Tasks

## 1. JIT: stamp the CLR-struct-local marker on `ldflda`

- [x] 1.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`, add the
      constant `public const int NeoLdfldaClrStructLocalFieldMarker = 0x8;` next to
      the existing `NeoLdfldaInlineMarker` (0x1) / `NeoLdfldaClrStructFieldMarker`
      (0x2) / `NeoLdfldaHeapIlRefFieldMarker` (0x4), with a comment noting bit 0x8 is
      free and mutually exclusive with 0x1/0x2/0x4 (declaring type is CLRType, not
      ILType).
- [x] 1.2 In the same file's `case Code.Ldflda` (Neo branch, ~line 2942-2971), after
      `IsClrStructFieldOfIL` / heap-IL-ref marker stamping, add: stamp
      `NeoLdfldaClrStructLocalFieldMarker` when the resolved declaring `type is
      CLRType` (`op.Operand4 |= NeoLdfldaClrStructLocalFieldMarker;`). Confirm the
      F-6 type-spec gate (~line 1081) does NOT clear bit 0x8 (it clears only 0x2/0x4
      and fires only for an IL-VT source).

## 2. Runtime: resolve the real managed byte offset in the `ldflda` frame-native arm

- [x] 2.1 In `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`, add a
      `static Dictionary<long, int>` cache for the resolved field byte offset, keyed
      by `((long)typeHash << 32) | (uint)fieldHash` (Neo-gated, like the other Neo
      static caches). Add a helper `ResolveClrStructFieldByteOffset(int typeHash, int
      fieldHash)` that: resolves `ct = AppDomain.GetType(typeHash) as CLRType`,
      `fi = ct.GetField(fieldHash)`, returns `Marshal.OffsetOf(ct.TypeForCLR,
      fi.Name).ToInt32()` (cached; wrap the `Marshal.OffsetOf` in try/catch and throw
      a tagged `NotImplementedException` naming the struct on failure).
- [x] 2.2 In the `case OpCodeREnum.Ldflda` runtime arm (~line 1737), read the new
      marker: `bool clrStructLocalFieldMarker = (ip->Operand4 &
      JITCompiler.NeoLdfldaClrStructLocalFieldMarker) != 0;` alongside the existing
      marker reads.
- [x] 2.3 In the `else if (objIdx == -1)` (frame-native) branch of the `ldflda` arm,
      when `clrStructLocalFieldMarker` is set, resolve the real byte offset via
      `ResolveClrStructFieldByteOffset(ip->Operand, fieldPrimOff)` and use it in place
      of `fieldPrimOff`: produce `(-1, vtBase + resolvedByteOffset)`. When the marker
      is clear, keep the existing IL-struct behavior `(-1, vtBase + fieldPrimOff)`.
- [x] 2.4 Confirm the heap branch (`objIdx >= 0`) is unchanged: it ignores the new
      marker and keeps using `fieldPrimOff` (the hash) via
      `NeoReadClrObjectField`/`NeoWriteClrObjectField`.

## 3. Probe (must FAULT on HEAD)

- [x] 3.1 Create `TestCases/NeoStepLdindStindByrefClrStructTest.cs` with a
      `public static` probe mirroring `TestValueTypeBinding.Test00`: declare a CLR
      struct local `TestVector3 a = TestVector3.One;`, perform `a.X += <value>;` (or
      an equivalent that emits `ldloca; ldflda; ldind.r4/stind.r4`), and assert the
      resulting `a.X` (throw on mismatch so it FAULTs). Embed "NeoStep" in the method
      / class name so the NeoStep filter picks it up.
- [x] 3.2 Add a second probe variant that robustly forces the byref escape via a
      `ref float` to a CLR struct field (e.g. `ref float x = ref v.X; x = 42f; assert
      v.X == 42f;`) in case the primary probe's Roslyn lowering emits `ldfld`/`stfld`
      instead of `ldind`/`stind`. Both probe methods MUST be public-static-void (or
      return a value the harness checks) so the runner picks them up.

## 4. Build + verify (ALWAYS `-f net8.0`; CLI=`Debug_Neo --no-incremental`; NEVER build TestCases with `Debug_Neo`)

- [x] 4.1 `dotnet build TestCases/TestCases.csproj -c Debug` (builds the new probe
      into `TestCases/bin/Debug/netstandard2.1/TestCases.dll`).
- [x] 4.2 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
      --no-incremental` (transitive build of the ILRuntime fix; 0 errors).
- [x] 4.3 Stash-toggle FAULT check: with ONLY the runtime fix (task 2) stashed
      (JIT marker still stamped but the arm still uses the hash), run the new probe
      filtered and confirm it FAULTs (the AV) -- proving the probe exercises the
      fixed arm. If it does NOT fault, adjust the probe shape (task 3.2) until it
      does. [CONFIRMED: TC1 -> AccessViolationException, exit 139 segfault, on
      stashed HEAD; PASS-after with fix.]
- [x] 4.4 With the fix applied: run the NeoStep smoke --
      `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build --
      TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -- and confirm 343/0 (341
      baseline + 2 new probe filter matches), zero AV / NIE. [CONFIRMED: 343/0,
      exit 0.]
- [x] 4.5 Legacy-neutral check: `dotnet run -c Debug -f net8.0 --project
      ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
      HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -- confirm the documented
      17-failure baseline set is unchanged and both new probes pass under Legacy.
      [CONFIRMED: 343 ran / 17 failed == baseline; both probes pass.]
- [x] 4.6 Spot-check the full smoke (drop the `NeoStep` filter) confirms the
      `TestValueTypeBinding.Test00`-shaped AV is gone (the run still hits the known
      pre-existing unrelated Dict-NRE crash; that is not a regression).
      [CONFIRMED: Test00 PASSES; run reaches the known NRE crash (exit 127, NOT
      139 segfault); UnitTest_10036/10037 "protected memory" Messages are the
      tests' OWN deliberate `throw new AccessViolationException()`, not segfaults.]

## 5. Ship

- [x] 5.1 `git status` (confirm ONLY the intended source files + the new probe +
      the change artifacts are staged; no accidental partial commit). [CONFIRMED:
      only `ILIntepreter.Neo.cs` + `JITCompiler.cs` (Neo-gated) + the new probe +
      the change dir.]
- [ ] 5.2 SKIPPED by implementer per constraint "Do NOT commit" (LEAD/shipper
      owns commit + push with trailer `Co-Authored-By: Claude Opus 4.8 (1M context)
      <noreply@anthropic.com>`; push with `git config lfs.useslockfiles false`).
- [x] 5.3 Append the durable findings (the byref shape, the mis-decode root cause,
      the fix, the `Marshal.OffsetOf`-blittable-only gotcha) to
      `rasen/changes/neo-overhaul/planning-context.md` under a "Child 15 DONE"
      heading.
