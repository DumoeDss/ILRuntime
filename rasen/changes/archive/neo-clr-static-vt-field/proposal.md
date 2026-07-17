## Why

A CLR value-type **static** field whose CLR type has a registered `ValueTypeBinder`
(e.g. `ILRuntimeTest.TestFramework.TestVector3.One` -- 3 floats, blittable, with a
binder registered at `helper.cs:32`) cannot be read or written under Neo: `Ldsfld`/
`Stsfld` on it hit the child-3 `NeoClrVtStaticFieldIsUnsafe` guard's
`if (hasBinder) return true;` line (`ILIntepreter.Neo.cs:275-276`, reached at the
Stsfld `:4181` / Ldsfld `:4320` call sites) and throw a tagged
`NotImplementedException`. This is 11 full-smoke hits and the highest remaining
active gap after child 7. The rejection is over-conservative: the Neo flat-byte
value-type path (`ReadNeoValueType`/`WriteNeoValueType`) does not consult the binder
at all, so a blittable binder struct marshals correctly today -- the binder flag
alone is not a sound reason to refuse the field.

## What Changes

- Relax `NeoClrVtStaticFieldIsUnsafe` (`ILIntepreter.Neo.cs:265-282`): remove the
  `if (hasBinder) return true;` auto-reject. A registered `ValueTypeBinder` no
  longer by itself makes a CLR VT static "unsafe" to marshal.
- Keep the two sound rejections unchanged: (a) `NeoClrStructHasRefFields(ft)` -- a
  ref-field CLR struct cannot be flat-marshaled (a Neo frame VT slot stores GC refs
  as mStack indices, but `FieldInfo.GetValue` returns real GC pointers, so the
  flat-byte round-trip would corrupt the ref region); (b) the slot-overflow check
  (`GetNeoValueTypeManagedSize(ft) > slotSize` -- real AccessVioation protection).
- The existing flat-byte box-roundtrip already sitting behind the guard
  (Stsfld: `ReadNeoValueType` + `ct.SetStaticFieldValue`; Ldsfld:
  `ct.GetFieldValue(sIdx, null)` + `WriteNeoValueType`) then handles
  `TestVector3.One` correctly with no further code change.
- Optional cleanup: drop the now-unused `hasBinder` parameter at the two call sites
  (`:4180-4181`, `:4319-4320`) and from the helper signature (implementer's choice;
  leaving it is harmless).
- Add a NeoStep probe (`TestCases/NeoStepClrVtStaticFieldTest.cs`) + minimal host
  infra (`TestClass3.NeoClrVtStaticProbe` writable `TestVector3` static +
  `TestCLRBinding.HostReadNeoClrVtStaticProbe()` host read helper), mirroring
  child-3's TC1 write/read isolation pattern. Both probes MUST fault on HEAD (the
  current NIE) and assert the round-trip value after the fix.

Neo-gated (`#if ENABLE_NEO_MODE`); Legacy `ExecuteR` is untouched. Net runtime diff
is ~1-3 lines.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: extend the child-3 "Stsfld and Ldsfld on a CLR static field
  execute under Neo" requirement family with the invariant that a registered
  `ValueTypeBinder` does NOT by itself make a CLR value-type static field unsafe --
  only ref-fields and eval-slot overflow do. Adds a regression scenario for a
  blittable binder CLR VT static (`TestVector3.One` read; write + read-back).

## Impact

- **Code:** `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (the
  `NeoClrVtStaticFieldIsUnsafe` helper + its 2 call sites, all already inside the
  file-level `#if ENABLE_NEO_MODE` gate). No JIT change, no optimizer change.
- **Tests:** new `TestCases/NeoStepClrVtStaticFieldTest.cs`; tiny host-infra
  additions in `ILRuntimeTestBase` (`TestClass3.cs` +
  `TestCLRBinding` -- a writable `TestVector3` static + a host read helper, exactly
  the child-3 `NeoClrStaticProbe` pattern). These are semantically inert for Legacy.
- **Smoke:** NeoStep 324/0 -> 326/0 (two new probes). Full Neo smoke: the 11
  `TestVector3.One` NIEs -> 0. Legacy-neutral by construction.
- **Out of scope:** the child-5 follow-up `neo-clr-vt-refcount-stobjldobj`
  (Stobj/Ldobj `refCount=0` for CLR structs) is the SAME mechanism class but a
  DIFFERENT opcode site and is NOT low-risk to fold in; it stays a separate child.
