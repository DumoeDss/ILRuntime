# Tasks — neo-il-static-ref-field-readback

> Build/test: ALWAYS `-f net8.0`; CLI = `Debug_Neo --no-incremental`; NEVER build
> `TestCases` with `Debug_Neo` (use plain `Debug`). All runtime edits
> `#if ENABLE_NEO_MODE`-gated. Baseline NeoStep smoke after child 12: **335/0**.
>
> Defect: the IL reference-type `newobj` arm (`ILIntepreter.Neo.cs`, "Step 8b"
> block ~3309) stores the new instance at `mStack[newobjDstIdx]` BEFORE
> `CopyNeoCallArguments`. When the newobj dest register aliases a REFERENCE
> argument register (the canonical `ldstr/ldloc refArg; newobj(refArg)` lowering),
> the arg's mStack index == `newobjDstIdx`, so the store clobbers the arg before
> it is copied to the ctor; the ctor then receives `this` as the aliased arg.
> `TestStaticFieldInstance` fails (`InvalidCastException ILTypeInstance → String`).
>
> NOTE: the IL-static Stsfld/Ldsfeld reference arms are CORRECT (verified); do
> NOT touch them. The fix is entirely in the newobj arm.

## 1. Diagnose-first (confirm the newobj arg-clobber is live on HEAD)

- [x] 1.1 Confirm HEAD NeoStep smoke baseline green (335/0); record the count.
- [x] 1.2 Confirm `TestStaticFieldInstance` FAILS on HEAD under
  `Debug_Neo` (`InvalidCastException … ILTypeInstance → System.String` at
  `String.Concat`, inlined from `TestA.TestCall`). Record the JIT dump line
  `newobj r2, r2, TestA..ctor` (dest register r2 aliases the arg register r2).
- [x] 1.3 (Done during investigation) Confirm the static round-trip is NOT the
  bug: instrumented `Stsfld`/`Ldsfld` IL-static ref arms show `TestA` written
  and read back faithfully; the corruption is in the ctor (its `name` arg is
  bound to `this`). Eliminated hypothesis: "Ldsfeld pushes the StaticInstance."

## 2. ExecuteNeo — re-base the aliased reference arg in the IL newobj arm

- [x] 2.1 In `ILIntepreter.Neo.cs`, IL reference-type `newobj` arm (the
  `// IL reference-type newobj (Step 8b)` block), insert IMMEDIATELY AFTER
  `ins = ilNewobjType.Instantiate(false);` and BEFORE `mStack[newobjDstIdx] = ins;`:
  - If `map.RefSrc != null && map.PrimitiveSrc != null`: scan `map.RefSrc` for
    any entry equal to `dstRefOffset` (the dest register aliases a reference
    arg's register). If found, read `aIdx = *(int*)(frameBase + ip->DstOffset)`;
    if `aIdx >= 0 && aIdx == newobjDstIdx`, do `mStack.Add(mStack[aIdx])` and
    `*(int*)(frameBase + ip->DstOffset) = mStack.Count - 1`.
  - Include the explanatory comment (dest/arg alias, ref-map discriminator, why a
    primitive int arg whose value == newobjDstIdx is NOT re-based).
- [x] 2.2 Leave the existing `mStack[newobjDstIdx] = ins; *(int*)targetBase =
  newobjDstIdx; CopyNeoCallArguments(...); *(int*)retDstPtr = newobjDstIdx;` and
  the `mStack.Add(mStack[newobjDstIdx])` 'this' push UNCHANGED.
- [x] 2.3 Confirm the edit is inside the `#if ENABLE_NEO_MODE` newobj arm (it is
  — this whole arm is Neo-only), so Legacy is untouched.

## 3. Probes (must FAULT on HEAD, PASS after) — `TestCases/NeoStepNewobjArgAliasTest.cs`

- [x] 3.1 `NeoStepNewobjArgHolder` (one `string Tag` field, 1-arg ctor) + a
  static `_inst` field lazy-initialized through an `Instance` getter
  (`if (_inst == null) _inst = new NeoStepNewobjArgHolder("lazy"); return _inst;`).
  `NeoStepNewobjArgAlias_TC1_LazyInitRefArg`: read `Instance.Tag`, assert
  `(object)tag == "lazy"`, deliberate `1/0` on mismatch. Embeds `NeoStep`.
- [x] 3.2 `TC2_SecondLazyInitRefArg`: a SECOND static field + getter (`"second"`)
  + the same assert, to guard against a fix that only handles the first call.
- [x] 3.3 Build `TestCases` with plain `Debug`
  (`dotnet build TestCases/TestCases.csproj -c Debug`).

## 4. Verify (the regression gate)

- [x] 4.1 Build CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors).
- [x] 4.2 Stash-toggle: temporarily gate the re-base with `if (false && ...)`,
  rebuild, run `true NeoStepNewobjArgAlias` -> expect BOTH TCs FAULT
  (`DivideByZeroException`). Restore the gate; rebuild. (Confirmed: 2 failed
  stashed -> 0 failed restored.)
- [x] 4.3 `TestStaticFieldInstance` now PASSES (run filtered; was
  `InvalidCastException` on HEAD).
- [x] 4.4 Full NeoStep smoke (`Debug_Neo`, `true NeoStep`): **339/0** (335 + the
  new probes; no regression — child-11/child-12 canaries intact).
- [x] 4.5 Legacy-neutral: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug`
  then run `true NeoStepNewobjArgAlias` -> 4/0 (probes pass under `ExecuteR`;
  Neo-gated edit leaves Legacy byte-identical).
