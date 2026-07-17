# Tasks: neo-byref-array-element-marshal

## 1. Re-audit (DONE by planner)
- [x] Ran the canary `CLRBindingTest01` under Neo (`Debug_Neo`, `-f net8.0`):
      FAULTS with the tagged NIE at `ILIntepreter.Neo.cs:515`, thrown from the
      `Call` arm (`:3008`) via `CopyNeoCallArguments` (`:424`, `isWrite:false`).
- [x] Confirmed the byref encoding: `ldelema` (`:5793-5798`) stamps `(arrIdx,
      elementIdx)` with `elementIdx` = the ELEMENT INDEX (NOT byte offset). The
      stub at `:508-517` is the fix point.
- [x] Confirmed the helper is invoked in BOTH directions (`:424` forward +
      `:653` write-back), so ONE Array branch covers both (design 4).
- [x] Confirmed the reflection fallback (not the autogen `setBit_0_Neo` redirect)
      is on this call's path -- so the helper is the correct general fix point.

## 2. Runtime Array branch (`ILIntepreter.Neo.cs`, Neo-gated)
- [x] Replace the stub `target is Array` branch (`:508-517`) in
      `NeoMarshalByrefFieldToSlot` with a real body that mirrors the CLR-object-
      field branch (`:518-574`) per design 3: decode `off` = elementIdx, forward
      read = `Array.GetValue(off)` + `WriteNeoValueType` by category (reference
      -> mStack index), write-back = `ReadNeoValueType`/mStack-index +
      `Array.SetValue(value, off)`. Recover `elemType` from
      `array.GetType().GetElementType()` when null.
- [x] Keep the `ILTypeInstance` branch (`:456-507`) and the CLR-object-field
      fall-through (`:518-574`) UNCHANGED.

## 3. Host helpers (`TestClass3.cs`)
- [x] `NeoByrefArrElemIncrementByte(ref byte b)` -- `b = (byte)(b + 5)`.
- [x] `NeoByrefArrElemIncrementInt(ref int v)` -- `v += 100`.
- [x] `NeoByrefArrElemMutateVectorAndReturnIncomingSum(ref TestVector3 v)` --
      returns `(int)(v.X+v.Y+v.Z)` then `v.X+=1000; v.Y+=2000; v.Z+=3000`.

## 4. Probes (`TestCases/NeoStepByrefArrayElementTest.cs`, permanent)
- [x] TC1 primitive `byte`: `ref arr[1]` increment, assert `arr[1] == 25` via
      `1/0` guard on mismatch (ldelem.u1 read-back -- Step 16 green).
- [x] TC2 primitive `int` multi-index: `ref arr[0]` + `ref arr[3]`, assert
      `arr[0]+arr[3] == 205` (101+104).
- [x] TC3 VT `TestVector3`: twice-call pattern (avoids the still-broken
      `ldobj`-array-element read). ret1 = 0 (incoming sum of a zero-init
      element), ret2 = 6000 (proves write-back persisted). Assert
      `(ret1==0 && ret2==6000)`.

## 5. Verify (record results in the return report)
- [x] `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` (0 errors).
- [x] `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] NeoStep smoke => **371/0** (368 baseline + TC1/TC2/TC3).
- [x] Confirm `CLRBindingTest01` now PASSES (run with filter `CLRBindingTest01`).
- [x] Stash-toggle FAIL->PASS: `git stash push -- ILIntepreter.Neo.cs` (keep
      probe) -> probe FAULTS (NIE on HEAD); pop -> rebuild -> PASS.
- [x] Write-back persistence: TC2 (two indices mutate+persist) + TC3 (twice-call
      proves struct mutation persisted) confirm end-to-end.
- [x] Legacy-neutral: plain `Debug` + `useRegister=true` + `NeoStep` = 371/18
      (documented pre-existing baseline; my 3 probes PASS under Legacy). 100%
      `#if ENABLE_NEO_MODE` (structural).

## 6. Ship (LEAD does the commit; worker does NOT commit)
- [ ] Hand off for LEAD review + commit. Append durable findings to
      `rasen/changes/neo-overhaul/planning-context.md` (child 25).

## BUILD-SERVER GOTCHA (resolved during verify)
The Roslyn VBCSCompiler shared server cached a STALE compilation of
`TestClass3.cs` (whose `TestClass3` class spans only lines 12-39; the file also
defines `TestCLRBinding` at line 110, `TestClass4`, etc.). A plain `rm -rf
bin/obj` + rebuild did NOT invalidate the server's in-memory cache -> the rebuilt
DLL's `TestClass3` TypeDef had only the original 4 methods (setBit/getString/
.ctor), so TestCases CS0117'd on the newer helpers. `strings` was misleading
(those method names lived in the embedded PDB, not the MethodDef table). FIX:
kill all `dotnet` build-server processes + pass `-p:UseSharedCompilation=false`.
Also: the host helpers live in `TestCLRBinding` (NOT `TestClass3`) -- the probe
must reference `TestCLRBinding.X` (child-24's `BuildNeoArrElemProbeArray` is
there too). Durable: see planning-context child-25 findings.
