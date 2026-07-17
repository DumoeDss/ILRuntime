# Proposal: neo-raw-ldfld-clr-object-vt-field

## Why
A raw `Ldfld` READ of a field of a CLR-struct field of a CLR REFERENCE object
(`x = obj.Struct.field`) silently corrupts under Neo. The CIL lowering
`ldflda Struct(on obj); ldfld field(on the struct address)` leaves the raw
`ldfld` with its owner register holding the ldflda-produced 8-byte byref
`(objIdx, structFieldHash)`. The raw-Ldfld value-type-owner branch (child-4 /
child-21) reads that slot as FLAT MANAGED BYTES via `ReadNeoValueType`, so the
two byref ints are reinterpreted as the struct's first two fields -> a wrong
value, no crash, no NIE (the same silent-corruption shape as child-24's array-
element read).

This is the READ counterpart of child-27 (which fixed the raw `Stfld` WRITE
`obj.Struct.value = 111`). Child-27 used runtime content detection (a VT-owner
Stfld is ALWAYS a byref). The READ side CANNOT: a raw-Ldfld's owner is EITHER
flat bytes (ldloc/ldsfld by value) OR a byref (ldelema / ldflda), and a flat-
bytes struct's first int field can coincidentally index a real object in mStack
-> a plain runtime check has a constructible collision (child-24's verdict).
So a JIT marker is required, exactly as child-24 did for the array-element case.

## What Changes
- **JIT (`JITCompiler.cs`):** a new const `NeoRawLdfldClrObjectFieldByRefMarker
  = 0x2` (bit 0x2 of the raw `Ldfld` Operand4 -- DISJOINT from child-24's 0x1
  array-element marker in the same spare int; the raw-Ldfld Operand4 namespace
  is separate from the Ldflda 0x1/0x2/0x4/0x8 markers). Stamped in the
  `case Code.Ldfld` CLRType else-branch when `ins.Previous != null &&
  ins.Previous.OpCode.Code == Code.Ldflda`. Mutually exclusive with child-24's
  stamp (a CIL instruction has exactly one immediate predecessor: it is EITHER
  Ldelema OR Ldflda, never both).
- **Runtime (`ILIntepreter.Neo.cs`):** in the raw-Ldfld `IsValueType` branch, a
  new `else if` (between child-24's array-element marker check and the flat-
  bytes else): decode `(objIdx, structFieldHash)`, read the WHOLE struct field
  via the containing object's CLRType (`NeoReadClrObjectField(target, off)` ->
  boxed struct), reflection-read the leaf field (`f.GetValue(boxedStruct)`),
  leave the existing dest marshalling unchanged. Mirrors child-27's Stfld WRITE-
  direction (box/mutate/unbox one level up) for the READ direction. A defensive
  `objIdx == -1` sub-branch reads flat bytes from the byref offset (a nested
  ldflda-on-frame-local could produce a frame-native byref; correct for that
  shape). The F-10 IL-instance sibling (a CLR-struct field of an IL instance)
  is guarded with a tagged deferred NIE (matches child-27).

## Impact
- Neo-gated (`#if ENABLE_NEO_MODE` + file-gated `ILIntepreter.Neo.cs`) ->
  Legacy-neutral by construction.
- Capability: `neo-value-types` (siblings child-4 / child-19 / child-24 /
  child-26 / child-27 -- the raw Ldfld/Stfld value-type-owner surface).
- Two NeoStep probes (TC1 single-field read, TC2 three-field read + IL sum),
  host-set struct values isolate the READ path from the sibling Stfld WRITE.
- Expected NeoStep 378 -> 380/0. No regressions (child-4/9/19/24/25/26/27/28 +
  the raw-Ldfld VT-owner branch families stay green).
