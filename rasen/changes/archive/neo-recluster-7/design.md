# Design -- neo-recluster-7 (UnitTest_StaticTest05 fix)

## Scope
Fix the `ref <IL-static struct field>` write-back + `ldsflda <IL-static struct
field>; ldfld <sub-field>` read-back for a PURE-PRIMITIVE IL struct (Vector3). The
ref-region half (StructTest6, 0 prim + 2 refs) is OUT OF SCOPE -- it stays a
distinct deep singleton.

## Pinned root (3 interacting defects)
Test: `testVal2 = Vector3.One; Sub(ref testVal2); /* sets i = Zero */ if (testVal2.x != 0) throw;`

1. **WRITE side -- ldsflda wrong offset.** Ldsfelda IL-static arm discriminator
   `ldaFt.IsPrimitive ? PrimitiveOffset : ReferenceOffset` gave ReferenceOffset for
   an IL-struct field. An IL-struct static field is inlined in Primitives (the
   Ldsfeld IL-struct arm reads Primitives[PrimitiveOffset]), so the byref MUST carry
   PrimitiveOffset. Diag-confirmed: stobj wrote to `Primitives[1]` (ReferenceOffset)
   instead of the real PrimitiveOffset.
2. **READ side -- bad JIT inline fold.** `ldsflda r1; ldfld.r4 r1` was folded to
   `ldfld.r4.inline` (frame-relative read) because `registerTypes[r1]` was stale-
   seeded Vector3 by a prior `initobj r1, Vector3` and Ldsfelda never cleared it.
   `TryRewriteFieldAccessForInline` (keys on `registerTypes[owner] is ILType &&
   IsValueType`) mis-treated the byref as an in-frame struct -> read the byref's
   (objIdx, off) bytes as a float = garbage.
3. **READ side -- Ldfld_R4 ignored the byref base offset.** Even non-inline,
   `Ldfld_R4` read `Primitives[Operand2]` (=0 for field x) instead of
   `Primitives[primOff + Operand2]`.

## Fix (3 parts, Neo-gated)
1. `ILIntepreter.Neo.cs` Ldsfelda IL-static arm -- discriminator
   `ldaFt.IsPrimitive || (ldaFt is ILType && ldaFt.IsValueType) ? PrimitiveOffset
   : ReferenceOffset`. (CLR-struct static fields keep ReferenceOffset + F-10 flag.)
2. `JITCompiler.cs` TypeSpecializeNeoOpcodes -- new `case OpCodeREnum.Ldsfelda:`
   clears `registerTypes[op.Register1] = null`. A Ldsfelda dest is ALWAYS a byref,
   never an in-frame VT; clearing prevents the broken inline fold (and is safe for
   every other registerTypes consumer -- a byref is never an in-frame VT / ref slot /
   arith operand).
3. `ILIntepreter.Neo.cs` new helper `NeoLdfldStaticByrefOff(ins, frameBase, srcOff,
   Operand2)` + the 10 scalar Ldfld arms (I1/U1/I2/U2/I4/U4/I8/U8/R4/R8) call it.
   The helper adds the byref's static-field base when `ins is ILTypeStaticInstance`
   (reliable discriminator: a normal heap Ldfld owner is a plain ILTypeInstance;
   the static byref is reachable ONLY via ldsfelda). Non-static owner -> Operand2
   unchanged (no regression).

## Soundness
- The `is ILTypeStaticInstance` discriminator is reliable: ILTypeStaticInstance
  derives from ILTypeInstance; a normal heap ldfld owner is a plain ILTypeInstance
  (NOT the static subclass); the static-byref shape is produced ONLY by ldsfelda
  (`mStack.Add(ilt.StaticInstance)`).
- Clearing `registerTypes[ldsflda.dest]` cannot regress: `ldsflda; ldfld.inline`
  was ALWAYS broken (reads the byref as frame bytes = garbage), so no passing test
  relied on it; after the clear, the byref flows to the runtime Ldfld_* heap arm
  (now correct via part 3).
- Pure-primitive only: an IL-struct WITH ref fields would still need the ref-region
  base in the byref (StructTest6) -- out of scope, reported honestly.

## Verify
- UnitTest_StaticTest05 filtered: 1/0 PASS (was throw).
- NeoStep: 414/0 (no regression).
- FULL SMOKE: 7 -> 6 (truth; FRESH no-filter re-run).
- Legacy-neutral: all 3 changes inside `#if ENABLE_NEO_MODE`.
