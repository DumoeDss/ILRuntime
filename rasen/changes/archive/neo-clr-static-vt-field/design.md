## Context

Child 3 (`neo-clr-static-fields`) filled the `Stsfld`/`Ldsfld` CLR-declaring-type
`else` branches in `ExecuteNeo` (`ILIntepreter.Neo.cs:4144-4194` Stsfld,
`:4287-4334` Ldsfld). As a recovery deviation it added the guard
`NeoClrVtStaticFieldIsUnsafe(ft, slotSize, hasBinder)` (`:265-282`) that refuses a
CLR value-type static with a tagged NIE when the type (a) has a registered
`ValueTypeBinder`, (b) has reference fields, or (c) overflows the eval slot. The
ship-log recorded "Bug-fix 2 (VT-binder AV crash)" -- the worker observed an
AccessViolation for a binder struct and chose to refuse ALL binder structs.

The 11 remaining full-smoke hits are `ldsfld TestVector3.One`
(`TestVector3.cs:88`, `public static TestVector3 One = new TestVector3(1,1,1)`),
which trips ONLY the binder clause (TestVector3 is 3 floats -- no ref fields, and
it fits a 12-byte slot). `TestVector3` has a registered `ValueTypeBinder`
(`helper.cs:32: RegisterValueTypeBinder(typeof(TestVector3), new TestVector3Binder())`).

## Goals / Non-Goals

**Goals:**
- Make `Ldsfld`/`Stsfld` on a **blittable** CLR value-type static whose type has a
  registered binder (the `TestVector3.One` shape) read/write correctly instead of
  NIE-ing.
- Pin the invariant in the spec so the over-conservative `hasBinder` clause is not
  reintroduced.

**Non-Goals:**
- Ref-field CLR structs (binder or not) at the `Stsfld`/`Ldsfld` site -- these stay
  refused by `NeoClrStructHasRefFields` (loud NIE). Correctly marshaling them would
  need a binder/offset-aware ref-region path that does not exist for the Neo
  `byte*` cursor.
- The child-5 follow-up `neo-clr-vt-refcount-stobjldobj` (Stobj/Ldobj `refCount=0`
  for CLR structs). See "Decisions / F1-overlap".
- Any JIT or optimizer change.

## Decisions

### D1 -- The binder is NOT consulted by the Neo flat-byte path (root cause)

`ReadNeoValueType` (`:227`) and `WriteNeoValueType` (`:243`) are PURE FLAT-BYTE
copies. Each emits (once per `Type`, cached in `s_neoVtReaders`/`s_neoVtWriters`)
a `DynamicMethod` that calls `Unsafe.ReadUnaligned<T>(void*)` + `Box` (reader) /
`Unbox_Any<T>` + `Unsafe.WriteUnaligned<T>(void*)` (writer)
(`CreateNeoVtReader`/`CreateNeoVtWriter`, `:177`/`:200`). The cursor advance size is
`Optimizer.GetNeoValueTypeManagedSize(t)` = `Unsafe.SizeOf<T>()` (`Optimizer.Neo.cs:1625`).
**None of this consults `ValueTypeBinder`.** The binder
(`ValueTypeBinder.cs` / `ValueTypeBinder<T>`) only exposes
`CopyValueTypeToStack`/`AssignFromStack`/`ToObject` operating on the LEGACY
`StackObject*` + `IList<object> mStack` representation -- there is NO Neo `byte*`
marshalling API on the binder. So under Neo a binder struct's frame slot is its raw
CLR managed bytes, identical to the same struct without a binder.

Therefore a BLITTABLE binder struct (`TestVector3` = 3 floats = 12 bytes) has the
same flat-byte representation with or without a binder, and the existing flat-byte
box-roundtrip (already behind the guard) is correct for it. Evidence this path
already works for binder structs today: `TestVector3` is routinely marshaled through
`ReadNeoValueType`/`WriteNeoValueType` for by-value params/returns in the running
NeoStep smoke (e.g. `TestCLRBinding.SumTestVector3Fields(TestVector3, TestVector3)`,
`TestClass3.cs:130`, exercised by Step-13b K2 tests) with zero failures. Child 4's
raw `Ldfld`/`Stfld` CLR-VT-OWNER handlers (`:3734`/`:3895`) use the identical
flat-byte box-roundtrip (`ReadNeoValueType` + `FieldInfo.GetValue`/`SetValue`) with
NO binder guard and are green. The child-3 ship-log's "VT-binder AV" was the
slot-overflow case (or a struct that did not fit the dest eval temp), which the
guard's THIRD clause already catches independently -- the binder clause is a
false-correlation, not a sound check.

**Fix:** delete `if (hasBinder) return true;` (`:275-276`). Keep the ref-field and
slot-overflow clauses.

### D2 -- Why ref-field structs stay refused (the ref-region mismatch)

For a CLR struct WITH managed reference fields (e.g.
`KeyValuePair<uint, ILTypeInstance>`, also binder-registered), the flat-byte
round-trip is genuinely UNSAFE in BOTH directions:
- `Ldsfld`: `FieldInfo.GetValue(null)` returns a boxed struct whose ref fields are
  REAL GC pointers. `WriteNeoValueType` would `Unsafe.WriteUnaligned<T>` the whole
  managed layout (pointer bits included) into the dest primitive region -- but the
  Neo model stores a VT slot's GC refs as mStack indices in a SEPARATE ref region,
  not as inline pointer bytes. The ref would be written as raw bytes into the
  primitive region AND the mStack ref slot left empty -> a missed GC root / later
  corruption.
- `Stsfld`: the inverse -- `ReadNeoValueType` would read the mStack-index bytes
  where it expects a real pointer.

`NeoClrStructHasRefFields(ft)` (recursive, `:284`) already catches this and returns
true, so deleting the binder clause does NOT open this hole. The fix is scoped to
blittable binder structs only.

### D3 -- F1-overlap verdict: keep `neo-clr-vt-refcount-stobjldobj` SEPARATE

The child-5 follow-up (`surfacedFollowups` in `portfolio-run.json`) notes that the
`Stobj`/`Ldobj` arms compute `refCount = ilType != null ? ilType.TotalReferenceCount
: 0` (`:5311` Stobj, `:5414` Ldobj). For a CLR struct `ilType` is null, so
`refCount = 0`, and the ref-region copy loop (`if (refCount > 0)`) is silently
skipped. A ref-field CLR struct copied via stobj/ldobj therefore has untracked GC
refs -- a latent missed-GC-root.

This is the SAME mechanism class as D2 (flat-byte path cannot track GC refs for a
ref-field CLR struct) but a DIFFERENT opcode site (`Stobj`/`Ldobj` vs
`Stsfld`/`Ldsfld`). It is NOT low-risk to fold in:
- The `Stsfld`/`Ldsfld` site already NIEs ref-field structs (D2), so this child has
  no latent corruption to fix at its own site.
- Properly closing F1 needs either binder-aware ref-region marshalling (consult the
  binder's managed reference count) or a new guard, at the `Stobj`/`Ldobj` arms --
  which share the Step-17 `Move_Vt` / stobj-refloop surface. Adding a guard risks
  flipping currently-passing tests that pass a ref-field CLR struct through
  stobj/ldobj (relying on today's silently-incomplete but non-failing copy) from
  pass to NIE = a NeoStep regression.

**Verdict: keep F1 as a separate follow-up child.** This child's scope is strictly
the `Stsfld`/`Ldsfld` blittable-binder relaxation.

### D4 -- Probe design (FAULT-to-fail discipline, child-1/2/3)

A passing test merely returns; a logic failure is a deliberate `1/0`
(DivideByZero; the Neo VM cannot yet `new Exception(...)`). Each probe MUST fault on
HEAD (the current guard NIE) AND assert the value so a wrong-value bug also fails.
Class/method names embed `NeoStep` so the smoke filter picks them up (child-6
discipline). Infra mirrors child-3's `NeoClrStaticProbe` exactly.

- **TC1 (Ldsfld read of a binder CLR VT static):**
  `TestVector3 v = TestVector3.One; int s = TestCLRBinding.SumTestVector3Fields(v, v);`
  assert `s == 6` (X=Y=Z=1 -> (1+1+1)+(1+1+1)=6). `SumTestVector3Fields`
  (`TestClass3.cs:130`) is an existing host helper taking `TestVector3` by value --
  it exercises the (already-working) by-value param path downstream; the load-bearing
  step under test is the `ldsfld TestVector3.One`. Faults on HEAD at the ldsfld NIE.
- **TC2 (Stsfld write + Ldsfld read-back):** add `TestClass3.NeoClrVtStaticProbe`
  (a writable `public static TestVector3`) + `TestCLRBinding.HostReadNeoClrVtStaticProbe()`
  (plain host CLR read returning `(int)(X+Y+Z)` -- isolates a broken WRITE from a
  broken READ, the child-3 TC1 pattern).
  `TestVector3 src = TestVector3.One;` (ldsfld)
  `TestClass3.NeoClrVtStaticProbe = src;` (stsfld -- the step under test)
  `int h = TestCLRBinding.HostReadNeoClrVtStaticProbe();` (host read; `h==3` proves the write landed)
  `TestVector3 rd = TestClass3.NeoClrVtStaticProbe;` (ldsfld read-back)
  `int s = TestCLRBinding.SumTestVector3Fields(src, rd);` (`s==6`)
  assert `h == 3 && s == 6`. Faults on HEAD at the stsfld NIE.

## Risks / Trade-offs

- **[Re-introduced AccessViolation if a binder struct's flat size overflows the dest
  eval slot]** -> MITIGATED: the guard's slot-overflow clause
  (`GetNeoValueTypeManagedSize(ft) > slotSize`) is kept, so an oversized struct is
  still refused before `WriteNeoValueType` writes OOB. For `TestVector3.One` read
  into a `TestVector3`-typed local the slot is sized to 12 == the struct size, so no
  overflow.
- **[A binder struct WITH ref fields slipping through after the binder clause is
  removed]** -> MITIGATED: `NeoClrStructHasRefFields` is checked AFTER the removed
  binder clause and independently returns true for any ref-field struct (binder or
  not). Verified: `TestVector3` (3 floats) -> false; a hypothetical
  `KeyValuePair<uint,ILTypeInstance>` -> true.
- **[Stash-toggle must reproduce the 11 hits as the NIE]** -> the HEAD guard
  message is `"Neo Ldsfld: CLR static value-type field One of type ...One not
  supported under Neo (Step-13b ref-field/binder gap or slot-size overflow)"`; the
  apply worker confirms TC1/TC2 throw it on HEAD and pass after.

## Migration Plan

None -- purely a runtime correctness fix behind `ENABLE_NEO_MODE`. No API, field-
layout, or on-disk format change. Rollback = revert the one-line guard removal.
