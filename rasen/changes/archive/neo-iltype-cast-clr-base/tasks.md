# Tasks: neo-iltype-cast-clr-base (Wave-2 child C2)

- [x] Re-audit cluster C2 against a REAL run (name-filter InheritanceTest01 +
      TestIs.TestInterface under Neo; confirm PASS under Legacy). Pinned root
      cause to the autogen binding's direct cast on this/ref-params (NOT
      castclass/isinst, which was the suspected-but-disproven framing).
- [x] Implement the engine-level projection: `ProjectNeoClrCallRefArgs` +
      `ProjectNeoClrRefSlot` + `NeoClrPrimitiveSlotSize` in
      `ILIntepreter.Neo.cs`, called at the top of `InvokeNeoClrMethod`. Mirrors
      the reflection-fallback curPrim walk (natural prim sizes, enum=4, CLR-VT
      managed size, ref=4, newobj retRefBase skip).
- [x] Guard: `this` projects unconditionally (adaptor exists); params project
      only when `!targetType.IsInstanceOfType(obj)`; skip byref (write-back) and
      delegates (binding handles them); skip ILType params.
- [x] Fix the Object base-call StackOverflow recursion unmasked in
      InheritanceTest07 (the `this` must project for Object virtual methods ->
      adaptor, else `base.ToString()` re-enters `ILTypeInstance.ToString()`).
- [x] Fix the NeoStep14 ILTypeInstance-typed-param regression (param assignability
      guard). NeoStep back to 380/0.
- [x] Verify: full Neo smoke **140 -> 133 failed** (6 C2 + 1 C14 flipped green).
- [x] Verify: NeoStep 380/0 (no regression).
- [x] Verify: stash-toggle airtight (HEAD fails, fix passes).
- [x] Verify: Legacy-neutral (plain Debug builds + InheritanceTest01 passes).
- [~] Remaining C2-listed tests are distinct downstream sub-bugs (Muli_R4, NRE,
      'Adaptor' cast, reverse String->ILTypeInstance cast, constrained callvirt
      edge) -- reported in design.md, each its own future child.
