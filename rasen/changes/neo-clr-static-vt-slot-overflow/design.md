## Context

child 8 (`neo-clr-static-vt-field`) deleted the over-conservative `if (hasBinder)
return true;` clause of `NeoClrVtStaticFieldIsUnsafe` and KEPT the two sound
clauses: (a) `NeoClrStructHasRefFields(ft)` (ref-field CLR structs can't be flat-
marshaled) and (b) the slot-overflow check `GetNeoValueTypeManagedSize(ft) >
slotSize` (the real AccessViolation guard). child 8's TC1/TC2 (`TestVector3 v =
TestVector3.One`) were expected to turn 11 full-smoke NIEs into 0, and they pass
in the NeoStep smoke. But the post-child-16 full smoke still NIEs **14 times** on
`ldsfld TestVector3.One`. This change closes that gap.

**The crux (instrumented diagnosis, confirmed):** the 14 hits are NOT a guard
false-positive -- they are a genuine OOB that the guard correctly detects. The
dest register is an eval-stack **temp**, and the temp file is undersized.

Where `slotSize` comes from: at the Ldsfeld/Stsfld CLR-VT call sites
(`ILIntepreter.Neo.cs:4375/4519`), `slotSize = localInfos[regIdx].Size`. For a
**local** of type `TestVector3` the local declaration (`JITCompiler.cs:2153-2157`,
the F-MAJ-1 CLR-VT local block) correctly sets `Size = clrVtSize` = 12. For a
**temp** (the trailing `frame.StackRegisterCount` slots) `AllocateLocalStackSpaces`
sizes EVERY temp uniformly to `maxSize` (`JITCompiler.cs:2231`).

`maxSize` starts at **8** (`int maxSize = 8`, `:2204`) and the consumer loop
(`:2209-2223`) grows it ONLY inside `if (i is ILType il)`:
```csharp
foreach (var i in valueTypes) {
    if (i is ILType il) { ... if (size > maxSize) maxSize = size; ... }
}
```
`GatherValueTypes` (`:340`) DOES capture the declaring type of `Ldsfld`/`Stsfld`
(`:369-372`) and adds a CLR value type to the list (the `IsValueType &&
!IsPrimitive` filter at `:391` admits it). So `TestVector3` (a `CLRType`, 12 bytes)
IS in `valueTypes` -- but the `maxSize` loop's `is ILType` test skips it, so
`maxSize` stays 8 and every temp is 8 bytes.

Instrumented ground truth (guard + Ldsfeld call site logged for the full smoke):
- **Failing shape (the 14 NIEs):** `ldsfld TestVector3 slotSize=8 regIdx=N
  managedSize=12`, where `regIdx` is a trailing temp index (e.g. regIdx=4/nInfos=5,
  regIdx=7/nInfos=8). `12 > 8` -> guard fires.
- **Passing shape (child-8 TC1/TC2 and 12 other ldsfeld calls):** `slotSize=12
  regIdx=0` -- direct-to-LOCAL (the named `TestVector3 v`), correctly sized.
- Sanity: `ldsfld TestCLREnum managedSize=4 slotSize=8` does NOT fire (4 > 8 is
  false) -- the guard is precise, not over-firing.

Why 14 now (child 8 said only ArrayTest05 hit slot-overflow): before child 8, the
11 `TestVector3.One` hits tripped the `hasBinder` clause FIRST (TestVector3 has a
registered binder). child 8 removed that clause, so those hits (plus more unblocked
by children 9-16 clearing upstream blockers) now fall through to the slot-overflow
clause, where the undersized temp catches them. child 8's "11 -> 0" expected the
box-roundtrip behind the guard to handle TestVector3.One; it does -- but only once
the dest is big enough, and the temp was never big enough.

## Goals / Non-Goals

**Goals:**
- Make `ldsfld TestVector3.One` (and any gathered blittable CLR VT static) stop
  NIE-ing when the dest is an eval temp -- by sizing the temp to actually fit the
  struct.
- Preserve the slot-overflow guard as the AV safety net (it stays byte-for-byte;
  it just no longer fires for dests the fix correctly sizes).
- A NeoStep probe that FAULTs on HEAD (child-1/2/3/8 discipline) and covers the
  temp-dest shape child 8's TC1/TC2 miss.

**Non-Goals:**
- Do NOT relax the guard. The dest is genuinely undersized on HEAD; relaxing
  without sizing would write 12 bytes into an 8-byte temp -> silent corruption /
  AV. The fix MUST be the slot sizing.
- Do NOT change `GatherValueTypes` -- it already gathers CLR VTs; only the
  consumer loop skips them.
- Do NOT touch the IL-VT `maxSize` path, `maxRefCount`, the guard, or the
  Stsfld/Ldsfeld arms.
- Out of scope: a CLR VT reaching a temp via a path `GatherValueTypes` does not
  capture (e.g. a CLR-VT method return into a temp) -- not among the 14 hits; open
  follow-up. The open `conv.i4`-float-bit-reinterpret gap (probes sidestep it).

## Decisions

**D1 -- Fix the `maxSize`/`maxAlignment` consumer loop (producer stays).**
`GatherValueTypes` already produces the right set; the bug is the consumer's
`is ILType` filter. Add an `else if (i is CLR.TypeSystem.CLRType ct)` arm:
```csharp
int size = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
if (size > maxSize) maxSize = size;
int align = size >= 8 ? 4 : size;     // mirror the CLR-VT LOCAL declaration
if (align > maxAlignment) maxAlignment = align;
```
Rationale: this is the single place that sizes the uniform temp file; fixing it
makes every temp in a method that uses TestVector3 (or any gathered CLR VT) big
enough. The size source (`GetNeoValueTypeManagedSize` = `Unsafe.SizeOf<T>`) is the
SAME source the local declaration, the callee param layout, and
`ReadNeoValueType`/`WriteNeoValueType` already use -- so the dest size and the
write size agree byte-for-byte (no off-by-N).
*Alternative considered (REJECTED):* relax the guard when "the dest is a temp that
the gather knew about". This re-introduces the OOB (the temp is still 8 bytes) and
duplicates the gather's knowledge at runtime. Wrong.
*Alternative considered (REJECTED):* seed/register the CLR VT into the Ldsfeld
dest at JIT time so it's a typed temp. The Neo frame is UNTYPED (no per-slot tag);
temp slots are uniformly `maxSize`. There is no per-temp sizing mechanism -- the
uniform `maxSize` IS the mechanism, and D1 is exactly how the IL-VT case already
works.

**D2 -- Leave `maxRefCount` alone.** The reachable set (passes
`NeoClrStructHasRefFields`) is blittable, so its managed ref count is 0; growing
`maxRefCount` for CLR VTs would over-allocate ref slots for nothing. A ref-field
CLR struct is refused upstream by the guard, so it never reaches a temp write. (The
default `maxRefCount = 1` already reserves one ref slot per temp; a blittable CLR
VT uses 0 of it -- unchanged behavior.)

**D3 -- Alignment mirrors the local declaration.** The F-MAJ-1 CLR-VT local block
(`:2154`) aligns with `clrVtSize >= 8 ? 4 : clrVtSize`. The new arm uses the same
expression. For TestVector3 (12) this is 4; `maxAlignment` default is 4, so no
observable change for TestVector3, but the expression generalizes correctly to
other CLR structs.

**D4 -- Probe the TEMP shape, not the local shape.** child-8 TC1/TC2 are
direct-to-local and pass on HEAD. The new probe feeds `TestVector3.One` BY VALUE
as a call arg (`SumTestVector3Fields(TestVector3.One, TestVector3.One)`) with NO
intervening named local on the eval path, so the `ldsfld` dest is a temp. This is
the minimal shape that reproduces the 14 hits. Assert via the host helper
`SumTestVector3Fields` (CLR-side float arithmetic) to sidestep the open
`conv.i4`-float-bit-reinterpret gap (and the now-fixed addi-on-float). TC1 =
One+One (=6); TC2 = One+default (=3) -- distinct expected values.

## Risks / Trade-offs

- **[Larger temp file -> more frame memory]** Growing `maxSize` for CLR VTs makes
  every temp in an affected method bigger (8 -> 12 for a TestVector3 method).
  -> Mitigation: purely additive, monotonic, exactly how IL-VTs already behave;
  correctness-only, no upper bound beyond the existing per-method max-VT model.
- **[Slot-overflow guard becomes a near-no-op for gathered CLR VTs]** After D1, a
  gathered blittable CLR VT no longer overflows its temp, so the guard's
  overflow clause stops firing for the ldsfeld/stsfld shape.
  -> Mitigation: the guard STAYS as the defense for dests the gather misses (e.g.
  an array-element temp not sized to the struct, child 8's ArrayTest05 shape) and
  for ref-field structs. This is the desired outcome, not a hole.
- **[registerTypes / type-spec interaction]** None. The fix changes only the
  temp's byte SIZE, not any type tag or opcode selection. `TypeSpecializeNeoOpcodes`
  is untouched. Seeding a primitive is irrelevant here (the temp is a VT byte
  region, not a typed primitive slot).
- **[CLR VT with a binder reporting managedCount > 0]** Cannot reach the temp
  write: managedCount > 0 implies ref fields, which `NeoClrStructHasRefFields`
  catches first. So `maxRefCount` is never wrong for the reachable set.

## Open Questions

None -- the diagnosis is instrumented and the fix is verified (probe PASS, NeoStep
348/0, full-smoke NIE 14 -> 0, Legacy 348 ran / 17 failed == baseline). All
verification was done in the planner session then reverted to leave the tree clean
for the apply worker; the tasks reproduce it via stash-toggle.
