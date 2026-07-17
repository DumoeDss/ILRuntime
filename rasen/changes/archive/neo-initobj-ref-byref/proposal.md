# Proposal: neo-initobj-ref-byref

Wave-2 child of `neo-overhaul` (branch `features/object-model-overhaul`). Neo =
ExecuteNeo under ENABLE_NEO_MODE. BACKGROUND run.

## Problem
`result = default(T)` on a generic T that is a REFERENCE type, reached through a
byref (`ref T result`), lowers to CIL `ldarg <byref param>; initobj T`. The Neo
runtime `Initobj` reference-type arm wrote the `-1` null sentinel into the byte
slot at `ip->DstOffset`. But for a byref operand `DstOffset` points at the frame
slot HOLDING the 8-byte byref `(objIdx, off)` -- NOT at the target. The direct
write silently nulled the byref temp's `objIdx` half and left the actual
reference local non-null.

Canary: `RefOutTest.UnitTest_NestedGenericRefOut` -- `string loc = obj.StringValue;
UnitTest_NestedGenericRefOutStringSub(ref loc)` chains into
`UnitTest_NestedGenericRefOutSub2<T>(ref T result){ result = default; }` which
inlines to `ldloca loc; <ldarg.0>; initobj String`. `loc` stays `"fff"` -> the
`if (loc != null) throw` fires. PASSES under Legacy (plain Debug).

Full Neo smoke baseline: 21 failed. Target: 21 -> lower (canary flips).

## Why the prior recluster-21 runtime approaches all regressed
`initobj` on a reference type has TWO operand shapes that are
INDISTINGUISHABLE at runtime:
1. A genuine byref to a reference slot (this canary: the `ldarg` of a byref
   param; the slot holds `(-1, locOffset)`). Needs DEREF (write null at the
   byref's target).
2. An addr-alias-FOLDED stack temp (`EqualityComparer<T>.Default.Equals(value,
   default)` -- the `default` temp; `LocalIsReference=false`, so it IS folded;
   `DstOffset` resolves to the temp itself). Needs DIRECT WRITE.

Both read `objIdx == -1` at runtime (a null temp's bytes are `(-1, 0)`;
coincidentally a valid frame-native byref encoding). The 4 prior runtime
approaches (ungated deref, two `liveAliasMap`-gated markers, a forward-walk
`refLocalByrefSource` map) all mis-handled shape 2 and regressed
`ActivatorCreateInstanceWithArgsTest` + `InheritanceTest20` (21 -> 22).

## Fix (CIL-producer scan, child-24/29 lineage)
At JIT emission (`JITCompiler.cs case Code.Initobj`), when the resolved T is a
reference type AND the CIL predecessor `ins.Previous` is an `ldarg`, stamp a JIT
marker bit `NeoInitobjByRefOperandMarker = 0x1` on the initobj's `Operand4`.
By CIL typing, `ldarg; initobj T` implies the `ldarg` loaded a `T&` byref -- a
genuine managed pointer that is NEVER addr-alias-folded (unlike an
`ldloca`-of-a-stack-temp, which IS folded and must keep the direct write). The
CIL `Instruction.Previous` link is STABLE (unlike the register-VM
alias/localIsRef state), so the gate is reliable and exclusive: shape 2's
predecessor is `ldloca.s` (the temp), so it never trips the marker.

The runtime reference-type `Initobj` arms check the marker: if set, deref
through the byref and write the null sentinel at the TARGET (mirror `Stind_Ref`
with `vIdx = -1`); otherwise direct-write (HEAD behavior for the folded-temp
shape). A `NeoWriteNullThroughByref` helper centralizes the byref decode +
null-write (frame-native / heap-IL-ref-field / CLR-object / Array /
caller-owned-slot).

The marker is on the initobj `Operand4` -- a disjoint opcode namespace (Initobj
otherwise uses `Operand`=type token, `DstOffset`=slot, `Operand3`=RefOffset for
the VT path). It survives `LowerNeoOffsets` (Initobj case sets
Operand3/DstOffset only) and the Neo inliner (`Optimizer.InlineMethod` copies
`OpCodeR` by value and only remaps registers), so it propagates to the INLINED
copy -- the canary's `ref T` helper is inlined into the caller, and the
initobj that actually executes is the inlined one (the standalone helper body
is compiled but never called).

Neo-gated (`#if ENABLE_NEO_MODE`) -> Legacy-neutral by construction.

## Capability
`neo-byref` (the load-bearing mechanism is writing null THROUGH a byref operand;
mirrors `Stind_Ref` / `NeoMarshalByrefFieldToSlot`). ADDED a requirement on the
initobj-byref deref contract.
