# Design: neo-initobj-ref-byref

## The pinned root (empirically confirmed)
`result = default(T)` on `ref T result` where T is a reference type lowers to
`ldarg <byref param>; initobj T`. The Neo `Initobj` reference-type arm did:
  `*(int*)(frameBase + ip->DstOffset) = -1;`
but `DstOffset` points at the frame slot HOLDING the 8-byte byref
`(objIdx @ +0, off @ +4)`, not at the target. So the `-1` clobbered the byref's
`objIdx` half and the real reference slot was never nulled.

JIT-dump-pinned (canary `UnitTest_NestedGenericRefOut`, T = System.String):
the inlined caller body is
  `ldfld.ref r1,..; ldloca.s r3,r1; move r4,r3; initobj r4,String; ldnull r4;
   cgt.un r2,r1,r4; brfalse.s r2,10`
At runtime the initobj's DstOffset reads the byref `(-1, <locOff>)`; `loc`
stays `"fff"` -> `if (loc != null) throw` fires.

## Two non-obvious discoveries (load-bearing for the fix shape)

### D1. The executing initobj is the INLINED copy; the marker must propagate
The standalone `UnitTest_NestedGenericRefOutSub2(string& result)` body IS
compiled (`initobj r0, String`, CIL `ldarg.0; initobj`), but it is NEVER CALLED
-- `StringSub`/`Sub2` are inlined into the caller. The initobj that executes is
the inlined one in `UnitTest_NestedGenericRefOut`. The Neo inliner
(`Optimizer.InlineMethod.cs:30-138`) copies each `OpCodeR` from the inlined
method's `BodyRegister` BY VALUE (`ins.Add(opcode)`, line 138) and only remaps
registers via `ReplaceOpcodeSource/Dest`; it does NOT re-emit via `Translate`.
Consequence: a marker stamped in `Translate`'s `case Code.Initobj` lands ONLY on
the standalone body's initobj -- but because the inliner copies the `OpCodeR`
verbatim (register remap only, no Operand4 scrub), the `Operand4` marker bit is
propagated to the inlined copy. So stamping at `case Code.Initobj` IS sufficient
(no separate inliner hook). Verified empirically: the stash-toggle flips the
canary FAIL->PASS.

### D2. The CIL predecessor is `ldarg`, NOT `ldloca`/`ldarga` (the design's
framing was slightly off)
The recluster-21 design.md assumed the predecessor was `ldloca`/`ldarga`. An
empirical diagnostic (`Console.WriteLine` of `ins.Previous.OpCode.Code` +
resolved T in `case Code.Initobj`) pinned it: the standalone Sub2 initobj's
predecessor is `Code.Ldarg_0` (the byref param `result`). The `ldloca` appears
only in the INLINED caller body (which bypasses `Translate`). For the
`EqualityComparer<T>.Default.Equals(value, default)` regression-risk shape
(`ActivatorCreateInstanceWithArgsTest`), the predecessor is `Code.Ldloca_S`
(the `default` temp). So gating the marker on `IsLdargCode(ins.Previous)`
EXACTLY captures the genuine-byref-param shape and EXCLUDES the folded-temp
shape -- no `LocalIsReference` consultation needed (the unreliable datum that
defeated approach 4).

## The discriminator is sound by CIL typing
`initobj T` requires its operand to be a managed pointer to a `T`-typed
location. If the operand is produced by `ldarg X`, then `X` must be a `T&`
byref parameter (a non-byref `ldarg` would be a CIL type-mismatch the
verifier/JIT rejects). So:
  `initobj T; T is reference type; ins.Previous is ldarg`
  ==> the initobj operates on a byref to a reference-type slot ==> DEREF.
Byref params are genuine managed pointers; they are NEVER addr-alias-folded (the
Neo optimizer's folding excludes `LocalIsReference` locals and does not fold
byref param loads at all). So the marker is never set on a folded-temp shape.

## The fix (3 engine edits, all Neo-gated)
1. `JITCompiler.cs`: const `NeoInitobjByRefOperandMarker = 0x1` (disjoint
   Initobj `Operand4` namespace). In `case Code.Initobj`, under
   `#if ENABLE_NEO_MODE`, resolve T via `appdomain.GetType(token,...)`; if
   `!IsValueType && ins.Previous != null && IsLdargCode(ins.Previous.OpCode.Code)`
   -> `op.Operand4 |= NeoInitobjByRefOperandMarker`.
2. `ILIntepreter.Neo.cs`: `NeoWriteNullThroughByref(appdomain, frameBase, mStack,
   byrefSlotOffset)` helper -- decode `(objIdx, off)` and write null at the
   target (mirrors `Stind_Ref` with `vIdx = -1`):
     `objIdx == -1` -> `*(int*)(frameBase + off) = -1` (frame-native; the tested
     shape); heap-IL-ref-field -> `refIns.ManagedObjects[off] = null`;
     CLR-object -> `NeoWriteClrObjectField(..., null)`; Array -> `SetValue(null,
     off)`; caller-owned-slot (F-7B flag) -> `mStack[objIdx] = null`; defensive
     fallback -> `*(int*)(frameBase + off) = -1`.
3. `ILIntepreter.Neo.cs`: the three Initobj reference-type arms (IL ref, unknown
   CLR, CLR ref) each check the marker and call the helper, else direct-write.

## Regression safety
- The marker is set ONLY for `initobj <ref-type>` preceded by `ldarg`. Every
  other initobj (value-type T; ldloca/ldarga/ldflda predecessor) is untouched --
  Operand4 stays 0 -> the runtime takes the direct-write `else` (byte-identical
  to HEAD).
- `ActivatorCreateInstanceWithArgsTest` + `InheritanceTest20` (the two the prior
  approaches regressed) have predecessor `Ldloca_S` -> marker NOT set ->
  direct-write preserved -> still PASS (name-filter confirmed both green).
- The heap arms of `NeoWriteNullThroughByref` are only reached for a heap byref
  (`objIdx >= 0`); the canary is frame-native (`objIdx == -1`). The heap arms
  mirror the proven `Stind_Ref` decode (bounds-checked, type-dispatched).

## Scope / out of scope
- IN scope: `initobj <ref-type>` on a byref produced by `ldarg` of a byref param
  (the `result = default(T)` / `ref T` shape). Fixes exactly the canary
  (21 -> 20).
- OUT of scope (documented, not blocking):
  (a) `initobj <VALUE-type>` on a byref (`ref int result; result = default;`).
      Same DstOffset-points-at-byref-temp defect, but the value-type arm zeroes
      `frameBase + DstOffset` (the byref bytes) instead of the target. Not
      tested (the `ref int` canary in RefOutTest.cs is commented out). A
      symmetric fix would deref-and-zero through the byref for value T.
  (b) `ldloca <declared ref local>; initobj <ref-type>` direct (no method
      extraction). Rare (Roslyn emits `ldnull;stloc` for a known ref type); only
      reached for a generic local that is a declared ref slot. Not surfaced in
      the smoke. Could be added by gating on ldloca + LocalIsReference if a hit
      appears.
