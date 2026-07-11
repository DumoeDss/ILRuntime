## 1. Stsfld CLR-static branch (write)

- [x] 1.1 In `ILIntepreter.Neo.cs` `ExecuteNeo`, replace the `Stsfld` arm's `else`
      that throws `"Neo Stsfld: CLR static field not implemented ..."`
      (`:3867`) with the CLR-static handling. Cast `declType` to `CLRType ct`;
      resolve `var f = ct.GetField((int)ip->OperandLong);` and
      `var ft = f != null ? f.FieldType : typeof(object);`. (If `f == null`, throw
      a tagged `NotImplementedException("Neo Stsfld: CLR static field hash ... not
      resolved")` -- clearer than the downstream NRE.)
- [x] 1.2 Read the source value at `byte* srcSlot = frameBase + ip->DstOffset;`
      (Stsfld sets ONLY `Register1` = `DstOffset`; same slot the IL-static branch
      reads) into a CLR `object` by category:
      - `ft.IsPrimitive` -> `NeoBoxPrimitiveByType(ft, srcSlot)` (`:5950`);
      - `ft.IsValueType` -> `ReadNeoValueType(ft, frameBase, ref off,
        Optimizer.GetNeoValueTypeManagedSize(ft))` (`:227`);
      - else (reference) -> `int idx = *(int*)srcSlot; idx >= 0 ? mStack[idx] :
        null`.
      **DEVIATION (recovery):** `ip->DstOffset` is NOT a byte offset for
      Stsfld/Ldsfld -- `LowerNeoOffsets` does NOT lower them (they fall to the
      no-op `default` + empty `WarnUnhandledNeoLoweringOpcode`), so `DstOffset`
      still holds the raw `Register1` INDEX. The arm resolves the register's byte
      offset at runtime via the in-scope frame `localInfos`:
      `int off = localInfos[ip->DstOffset].Offset;` (mirrors `LowerR1`). Lowering
      them globally was rejected -- it exposes a raw-`brtrue`-on-reference gap in
      the Roslyn delegate-cache pattern (`ldsfld cache; brtrue`), which the Neo
      `brtrue` arm (`!= 0`) cannot handle (model null sentinel is -1, per
      `ldnull`/`cgt.un`); the IL-static arms are left as-is (pre-existing) and the
      fix is deliberately CLR-static only.
      **DEVIATION (recovery):** the VT sub-branch is guarded by
      `NeoClrVtStaticFieldIsUnsafe(ft, slotSize, hasBinder)` -> tagged NIE for a
      CLR VT static whose type has a registered `ValueTypeBinder` (flat
      `WriteNeoValueType` corrupts the binder-shaped slot and can
      AccessViolation-exit), has reference fields (Step-13b gap), or whose flat
      managed size overflows the register's eval slot.
- [x] 1.3 Call `ct.SetStaticFieldValue((int)ip->OperandLong, value);`
      (`CLRType.cs:452` -> `FieldInfo.SetValue(null, value)`). Cross-reference
      comment: mirrors Legacy `ExecuteR` Stsfld CLR branch
      (`ILIntepreter.Register.cs:3301-3308`).

## 2. Ldsfld CLR-static branch (read)

- [x] 2.1 In `ILIntepreter.Neo.cs` `ExecuteNeo`, replace the `Ldsfld` arm's `else`
      that throws `"Neo Ldsfld: CLR static field not implemented ..."`
      (`:3910`) with the CLR-static handling. Cast to `CLRType ct`; resolve
      `var f = ct.GetField((int)ip->OperandLong);` (same null guard as 1.1).
- [x] 2.2 `object obj = ct.GetFieldValue((int)ip->OperandLong, null);`
      (`CLRType.cs:404` -> `FieldInfo.GetValue(null)`); then
      `if (obj is CrossBindingAdaptorType cba) obj = cba.ILInstance;` (Legacy
      parity, `ILIntepreter.Register.cs:3334-3335`).
- [x] 2.3 Push into `byte* dstSlot = frameBase + <offset>` by category
      (`var ft = f.FieldType;`):
      - `ft.IsPrimitive` -> `NeoWritePrimitiveToFrame(obj, dstSlot)` (`:5986`);
      - `ft.IsValueType` -> `WriteNeoValueType(obj, dstSlot,
        Optimizer.GetNeoValueTypeManagedSize(ft))` (`:243`) -- guarded as in 1.2;
      - else (reference) -> `mStack.Add(obj); *(int*)dstSlot = obj != null ?
        mStack.Count - 1 : -1;` (the SAME `mStack.Add` temp-ref convention the
        IL-static Ldsfld ref branch uses; do NOT use `frameRefBase + dstRefOffset`
        -- Stsfld/Ldsfld have no `dstRefOffset` operand).
      **DEVIATION (recovery):** same `localInfos[ip->DstOffset].Offset` runtime
      offset resolution as 1.2 (Stsfld/Ldsfld are not lowered).
- [x] 2.4 Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
      Debug_Neo --no-incremental`). 0 errors.

## 3. Probe infrastructure + NeoStep probes

- [x] 3.1 Add `public static int NeoClrStaticProbe;` to `TestClass3` + a host-read
      helper `TestCLRBinding.HostReadNeoClrStaticProbe()` (isolates a broken
      Stsfld WRITE from a broken Ldsfld READ). Rebuilt ILRuntimeTestBase.
- [x] 3.2 Add `TestCases/NeoStepClrStaticFieldTest.cs` (public static no-arg
      methods). 3 probes (FAULT-to-fail discipline): TC1 primitive round-trip
      (12345, write+host-read+read-back); TC2 `string.Empty` reference read
      (`.Length == 0`); TC3 `IntPtr.Zero` VT read (`== IntPtr.Zero`).
- [x] 3.3 Build TestCases (`Debug`, NEVER `Debug_Neo`). 0 errors; probes picked up.

## 4. Verify (smoke + Legacy-neutral)

- [x] 4.1 NeoStep smoke: **314/0** (311 baseline + TC1/TC2/TC3), 0 failures.
- [x] 4.2 FULL Neo smoke: the 88 `"Neo Stsfld/Ldsfld: CLR static field not
      implemented"` pre-crash hits are **GONE (0)**. NOTE: the design's "the run
      still NRE-crashes later in Activator -- known pre-existing" premise was
      WRONG -- at HEAD the full smoke COMPLETES (848 tests, 252 failed, no crash).
      The recovery's first cut (flat `frameBase + ip->DstOffset`, then lowering)
      either read the wrong slot (value-assertion fail) or, once the VT path ran,
      AccessViolation-exited on a binder struct (`TestVector3.One`). The final
      runtime-offset + binder/ref-field/size guard makes the run COMPLETE again
      (848 tests, 249 failed -- 3 fewer than HEAD; no segfault).
- [x] 4.3 Legacy-neutral: only runtime change is inside the fully
      `#if ENABLE_NEO_MODE` file `ILIntepreter.Neo.cs`; `Optimizer.Neo.cs`
      UNCHANGED (no lowering); `TestClass3.cs` adds only a public static field +
      host helper (no Legacy runtime semantics). Legacy byte-identical by
      construction.
- [x] 4.4 `git status`: only `ILIntepreter.Neo.cs` + `TestClass3.cs` modified +
      `TestCases/NeoStepClrStaticFieldTest.cs` new. No stray debug prints.
      (Diagnostic throws / scratch `.tmp-*` logs removed.)

## Recovery notes (partial worker was complete on the branches but UNVERIFIED)

- The dead worker's two CLR-static branches were logically complete and matched
  the design/CLRType signatures -- BUT they read/wrote `frameBase + ip->DstOffset`
  (the raw register INDEX, since Stsfld/Ldsfld are not lowered), so the value
  round-trip silently used the wrong slot (TC1 value-assertion failed, no NIE).
  No build error surfaced this; only running TC1 did.
- Root cause: `LowerNeoOffsets` has no `Stsfld`/`Ldsfld` case -> `default` no-op.
  Fix is a RUNTIME offset resolution (`localInfos[regIdx].Offset`) in the CLR-static
  arms only (NOT a global lowering -- that breaks the delegate-cache `brtrue`).
- Second root cause: the VT sub-branches called `WriteNeoValueType`/
  `ReadNeoValueType` on CLR struct statics whose type has a registered
  `ValueTypeBinder` (e.g. `TestVector3.One`) -> flat-bytes write corrupts the
  binder slot -> AccessViolation-exits the full smoke. Guarded with a tagged NIE
  (binder / ref-field / slot-size overflow).
