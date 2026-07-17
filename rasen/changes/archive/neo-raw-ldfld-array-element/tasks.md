# Tasks: neo-raw-ldfld-array-element

## 1. Re-audit (DONE by planner)
- [x] Built `TestCases/NeoStepRawLdfldArrayElementTest.cs` (3 TCs) + the
      `BuildNeoArrElemProbeArray` host helper in `TestClass3.cs`.
- [x] Confirmed all 3 TCs FAULT on HEAD `7689c27c` (DivideByZero; the read returns
      `a=arrIdx=3, b=elementIdx=1` -- the byref reinterpreted as struct bytes).
      Smoking-gun dump captured in `.tmp-ldfldarrraw.log`.
- [x] Confirmed the framing's "unambiguous runtime `mStack[objIdx] is Array`" claim
      is REFUTED for Ldfld (constructible collision for small-int fields; asymmetry
      with Stfld whose value-type owner is always a byref). => JIT marker is the
      principled fix.
- [x] Confirmed `Operand4` is free for the raw `Ldfld` (CLRType owner) and that
      `ins.Previous.OpCode.Code == Code.Ldelema` is a reliable JIT signal for the
      canonical `arr[i].field` shape.

## 2. JIT marker (`JITCompiler.cs`, Neo-gated)
- [ ] Add `public const int NeoRawLdfldArrayElementByRefMarker = 0x1;` next to the
      existing `NeoLdfldaClrStructLocalFieldMarker` (`:206`).
- [ ] In `case Code.Ldfld` (`:3075`), CLRType declaring-type else-branch
      (`:3104-3105`): after setting `op.OperandLong`, stamp
      `op.Operand4 |= NeoRawLdfldArrayElementByRefMarker;` when
      `ins.Previous != null && ins.Previous.OpCode.Code == Code.Ldelema`. Add the
      explanatory comment from design 3a.
- [ ] Sanity: confirm no other pass reads/writes raw-`Ldfld` `Operand4` (child-21's
      `TypeSpecializeNeoOpcodes` raw-Ldfld case reads only `OperandLong`/
      `Register1` -- safe; `LowerNeoOffsets` preserves/remaps Operand4 per child-2).

## 3. Runtime read branch (`ILIntepreter.Neo.cs`, Neo-gated)
- [ ] In the raw `Ldfld` value-type-owner branch (`ct.TypeForCLR.IsValueType`,
      `:3890`): gate the existing flat-bytes `ReadNeoValueType` path behind
      `else`, and add the marker-gated array-element read (decode
      `(arrIdx, elementIdx)` -> `cArr.GetValue(elementIdx)` -> `f.GetValue`) per
      design 3b. Reuse the existing dest marshalling (`:3937-3946`) unchanged.
- [ ] Keep the ref-type declaring branch (`:3903+`) and its `target is Array` NIE
      (`:3928-3929`) untouched (unreachable for a struct array element; fail-soft).

## 4. Probes (already on disk; ship as regression probes)
- [ ] Convert the TEMP marker on `NeoStepRawLdfldArrayElementTest.cs` to a permanent
      regression-test header (remove the "TEMP PROBE" note). Keep the 3 TCs.
- [ ] Keep `BuildNeoArrElemProbeArray` in `TestClass3.cs` (TC3 host-built-array
      isolation; sibling to child-19's `NeoArrElemFieldSum`).

## 5. Verify
- [ ] `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`
      (0 errors).
- [ ] `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [ ] Stash-toggle the runtime branch ONLY (or unset the marker): the 3 probes FAULT
      (DivideByZero on HEAD); pop -> 3/3 PASS (airtight, child-1/2 discipline).
- [ ] NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep`
      => **368/0** (365 baseline + TC1/TC2/TC3). Filter the JIT dump for the probe
      method to confirm the marker is stamped and the array-read branch fires.
- [ ] Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` unchanged (the
      change is 100% `#if ENABLE_NEO_MODE`).

## 6. Ship
- [ ] Commit (engine + probes + helper + artifacts). Commit trailer:
      `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- [ ] Append durable findings to
      `rasen/changes/neo-overhaul/planning-context.md` (child 24).

## Fallback (only if the JIT signal proves fragile under stash-toggle)
If `ins.Previous == Ldelema` mis-fires or misses reachable shapes, fall back to the
element-type-guarded runtime detection (design 4 / rejected alternative): a runtime
branch `if (arrIdx >= 0 && arrIdx < mStack.Count && mStack[arrIdx] is Array ca &&
ca.GetType().GetElementType() == ct.TypeForCLR && elementIdx < ca.Length)` in the
value-type-owner path, NO JIT change. Document the retained (narrow) collision
window in the ship log. Prefer the marker unless evidence forces this.
