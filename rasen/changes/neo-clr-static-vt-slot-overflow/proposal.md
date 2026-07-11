## Why

After child 8 relaxed the `hasBinder` clause, `ldsfld TestVector3.One` (a blittable
12-byte CLR value type) still NIEs **14 times** in the full Neo smoke whenever the
load destination is an eval-stack **temp** register rather than a named local. The
slot-overflow guard (`GetNeoValueTypeManagedSize(ft) > slotSize`, the real
AccessViolation protection that child 8 deliberately kept) fires because the JIT
sizes every eval temp to `maxSize` (default 8), and `maxSize` is grown ONLY for
`ILType` value types -- a CLR value type like `TestVector3` (a `CLRType`) is
gathered by `GatherValueTypes` but silently skipped by the `maxSize` consumer loop,
so the temp stays 8 bytes and a 12-byte struct genuinely overflows it. child 8's
own TC1/TC2 never hit this because `TestVector3 v = TestVector3.One` lowers
direct-to-LOCAL (a CLR-VT local is correctly sized to 12 bytes); the temp-dest
shape (passing `TestVector3.One` by value as a call arg, reading a field off it,
etc.) was unprobed and is the active gap.

## What Changes

- **Fix the temp-slot sizing (the root cause), not the guard.** In
  `JITCompiler.AllocateLocalStackSpaces` (`Runtime/Intepreter/RegisterVM/JITCompiler.cs`,
  the `maxSize`/`maxAlignment` loop ~line 2209), extend the loop to ALSO grow
  `maxSize`/`maxAlignment` for gathered CLR value types (`i is CLRType ct`), sizing
  each via `Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR)` (the same
  `Unsafe.SizeOf<T>`-based managed byte size the local declaration and the
  reader/writer already use). `maxRefCount` is left unchanged: the reachable set
  here is blittable (a ref-field CLR struct is refused upstream by
  `NeoClrStructHasRefFields`), so its ref count is 0. This is purely additive
  (`maxSize` is monotonic); existing IL-VT code is unaffected.
- **Keep the slot-overflow guard byte-for-byte.** `NeoClrVtStaticFieldIsUnsafe`
  (`ILIntepreter.Neo.cs:287`) is the real AV guard and stays. After the fix it
  simply no longer fires for a gathered blittable CLR VT whose dest temp is now
  correctly sized; it still fires for genuinely-undersized dests the gather misses
  (e.g. an array-element temp not sized to the struct), preserving child 8's
  "still refused if it overflows the eval slot" contract.
- **Add a NeoStep probe** (`TestCases/NeoStepClrVtStaticSlotOverflowTest.cs`) that
  forces the TEMP-dest shape (feed `TestVector3.One` BY VALUE as a call arg, no
  intervening named local). Both probes MUST fault on HEAD (the
  ldsfeld-into-temp guard NIE) and assert the value after the fix (via the host
  helper `SumTestVector3Fields`, CLR-side float arithmetic, sidestepping the open
  `conv.i4`-float-bit-reinterpret gap). No new host infra is required
  (`SumTestVector3Fields` already exists).

Neo-gated (`#if ENABLE_NEO_MODE` -- `AllocateLocalStackSpaces` lives inside the
big 807..2347 Neo block); Legacy `ExecuteR` compiles none of it. Net runtime diff
~8 lines.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: add a requirement that the Neo JIT sizes eval-temp register
  slots to accommodate gathered CLR value types (not just IL value types), so a
  blittable CLR VT flowing through a temp (the `ldsfld TestVector3.One` into an
  eval temp shape) does not overflow. Complementary to child 8's requirement (the
  slot-overflow GUARD contract is unchanged; only the set of dests that are
  correctly-sized grows).

## Impact

- **Code:** `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
  (`AllocateLocalStackSpaces` `maxSize`/`maxAlignment` loop, inside `#if
  ENABLE_NEO_MODE`). No `ILIntepreter.Neo.cs` change (the guard stays), no object-
  model change, no optimizer-pass change.
- **Tests:** new `TestCases/NeoStepClrVtStaticSlotOverflowTest.cs` (2 probes). No
  host-infra additions. Semantically inert for Legacy.
- **Smoke:** NeoStep 346/0 -> 348/0 (two new probes). Full Neo smoke: the 14
  `ldsfld TestVector3.One` "CLR static value-type field ... not supported" NIEs ->
  0. Legacy-neutral by construction (plain `Debug` + `useRegister=true` + NeoStep
  = 348 ran / 17 failed == documented baseline).
- **Out of scope:** (a) the open `conv.i4`-float-bit-reinterpret gap (probes assert
  via host CLR arithmetic to sidestep it); (b) a CLR VT that flows through a temp
  via a path `GatherValueTypes` does NOT capture (e.g. a CLR-VT method return into
  a temp -- not among the 14 hits, which are all `ldsfld`/`stsfld`); (c) the
  sibling `neo-clr-vt-refcount-stobjldobj` (different opcode site, child-8 F1
  verdict -- stays separate).
