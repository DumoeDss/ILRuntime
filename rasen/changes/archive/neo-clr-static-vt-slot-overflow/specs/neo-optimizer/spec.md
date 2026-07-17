## ADDED Requirements

### Requirement: Neo eval-temp register slots SHALL be sized to accommodate gathered CLR value types, not only IL value types

The Neo JIT SHALL size every eval-stack temp register (the uniform `maxSize`
computed in `JITCompiler.AllocateLocalStackSpaces`) to fit any value type
`GatherValueTypes` captures for the method -- including CLR value types
(`CLRType`), not only IL value types -- so a CLR value type flowing through an
eval temp (e.g. the dest of `ldsfld <CLR-VT static>` such as `TestVector3.One`)
fits its slot and does not overflow. A gathered `CLRType` value type SHALL grow
`maxSize` to at least `Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR)` and
SHALL grow `maxAlignment` to at least `size >= 8 ? 4 : size` (mirroring the CLR-VT
LOCAL declaration). The `maxSize` consumer loop SHALL NOT silently skip gathered
CLR value types via an `is ILType`-only test.

The Neo register frame sizes every eval-stack temp register uniformly to a single
`maxSize` (`JITCompiler.AllocateLocalStackSpaces`, one `slot.Size = maxSize` for
each of `frame.StackRegisterCount` temps). `maxSize` is computed from the value
types `GatherValueTypes` captures for the method (the declaring type of
`Ldsfld`/`Stsfld`/`Ldfld`/`Stfld`, the operand of `Ldobj`/`Stobj`/`Box`/`Unbox`/
`Isinst`/`Castclass`/`Constrained`/`Sizeof`/`Newarr`/`Ldfld_Ref`/`Ldfld_Value`/
`Stfld_Ref`/`Stfld_Value`, and a `Newobj` ctor's declaring type).

`GatherValueTypes` captures BOTH IL value types and CLR value types (the
`IsValueType && !IsPrimitive` admission filter does not exclude `CLRType`). The
`maxSize`/`maxAlignment` consumer loop SHALL therefore grow `maxSize` for a
gathered CLR value type too, using the SAME managed byte size source every other
Neo VT site uses -- `Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR)`
(`Unsafe.SizeOf<T>`) -- so that a CLR value type flowing through an eval temp
(e.g. the dest of `ldsfld <CLR-VT static>` such as `TestVector3.One`, or a CLR-VT
loaded for a by-value call arg / a transient field read) fits its temp slot.

Concretely, the loop SHALL treat a gathered `CLRType` value type `ct` as: grow
`maxSize` to at least `Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR)`; grow
`maxAlignment` to at least `size >= 8 ? 4 : size` (mirroring the CLR-VT LOCAL
declaration). `maxRefCount` SHALL NOT be grown for CLR value types via this path:
the reachable set here is blittable (a CLR struct WITH reference fields is refused
upstream by `NeoClrStructHasRefFields`), so its managed reference count is 0.

This SHALL be Legacy-neutral: `AllocateLocalStackSpaces` is inside the file-level
`#if ENABLE_NEO_MODE` Neo block, so Legacy `ExecuteR` compiles none of it.

This requirement is complementary to the child-8 "a registered ValueTypeBinder
does not by itself make a CLR value-type static field unsafe" requirement: the
`NeoClrVtStaticFieldIsUnsafe` slot-overflow GUARD contract is unchanged (it STILL
refuses a CLR VT whose flat managed size exceeds the dest/source eval-slot size --
genuine AccessViolation protection). This requirement only grows the set of dest
temps that are correctly sized, so a gathered blittable CLR VT static no longer
trips the guard.

#### Scenario: ldsfld of a blittable CLR value-type static into an eval temp (by-value call arg)
- **WHEN** an IL method under `ENABLE_NEO_MODE` loads a CLR value-type static
  field (`TestVector3.One`, a 12-byte blittable struct) via `ldsfld` into a temp
  register that is consumed by a following by-value call
  (`SumTestVector3Fields(TestVector3.One, TestVector3.One)` -- the static is fed
  directly as a call arg with no intervening named local on the eval path)
- **THEN** the eval temp SHALL be sized to at least 12 bytes (`maxSize` grown for
  the gathered `TestVector3` `CLRType`), the `ldsfld` SHALL write the 12 flat
  managed bytes without overflow, the slot-overflow guard SHALL pass, and the call
  SHALL return `(1+1+1)+(1+1+1) = 6`, rather than throwing
  `"Neo Ldsfld: CLR static value-type field One ... not supported under Neo ..."`

#### Scenario: ldsfeld of a blittable CLR value-type static into an eval temp (mixed with default)
- **WHEN** an IL method under `ENABLE_NEO_MODE` loads `TestVector3.One` into a
  temp as one by-value call arg alongside a `default(TestVector3)` second arg
  (`SumTestVector3Fields(TestVector3.One, default(TestVector3))`)
- **THEN** the load SHALL succeed (temp sized to the struct) and the call SHALL
  return `(1+1+1)+(0+0+0) = 3`, rather than the ldsfeld-into-temp guard NIE

#### Scenario: the slot-overflow guard is preserved for dests the gather does not size
- **WHEN** an IL method reads or writes a CLR value-type static whose flat managed
  size exceeds the dest/source register's eval-slot size AND that dest is not sized
  to the struct (a shape the gather/`maxSize` path does not cover, e.g. an
  array-element temp not sized to the struct)
- **THEN** the `NeoClrVtStaticFieldIsUnsafe` slot-overflow clause SHALL still
  refuse it with the tagged NIE (AccessViolation protection is preserved; this
  requirement only grows the set of correctly-sized dests, it does not remove the
  guard)

#### Scenario: ref-field CLR value-type statics are still refused
- **WHEN** an IL method reads or writes a CLR value-type static whose type has a
  managed reference-typed field (so the flat-byte round-trip cannot track its GC
  refs)
- **THEN** the guard SHALL still refuse it via the `NeoClrStructHasRefFields`
  clause with the tagged NIE regardless of temp sizing (a ref-field CLR struct is
  never flat-marshaled under Neo)

#### Scenario: IL value-type temp sizing is unregressed
- **WHEN** an IL method under `ENABLE_NEO_MODE` flows an IL value type (an
  `ILType`) larger than 8 bytes through an eval temp
- **THEN** the temp SHALL still be sized to at least that IL VT's
  `TotalPrimitiveSize` (the existing `is ILType il` arm is unchanged; the new
  `CLRType` arm is purely additive and `maxSize` is monotonic)

#### Scenario: Legacy ExecuteR is unaffected
- **WHEN** the runtime is built WITHOUT `ENABLE_NEO_MODE` and runs the Legacy
  register VM
- **THEN** `AllocateLocalStackSpaces`'s `maxSize`/`maxAlignment` loop (and the
  whole Neo frame layout) SHALL compile out (file-level `#if ENABLE_NEO_MODE`),
  and a plain-`Debug` + `useRegister=true` NeoStep-filter run SHALL show the SAME
  pre-existing Legacy failure set with and without this change (both new probes
  pass under Legacy too)
