# Design: neo-recluster-21 -- the initobj-byref root + why it is deferred

## The pinned root (UnitTest_NestedGenericRefOut)
`result = default(T)` on a generic T that is a reference type lowers to CIL
`initobj T` on a BYREF. The Neo optimizer's addr-alias folding
(Optimizer.Neo.cs:52-53 / 1526-1539) EXCLUDES declared reference locals
(`LocalIsReference`) from folding ("a reference local produces a genuine pointer
consumed by the heap path"). So for a reference target the initobj's `DstOffset`
points at the BYREF VALUE temp slot (holding the 8-byte (objIdx,off) pair), NOT
at the target. The initobj reference-type arm (`ILIntepreter.Neo.cs` IL ref
branch ~4374 / CLR ref branch ~4420) wrote `-1` into that byref temp -- the
null-sentinel assignment was silently lost, and the reference local stayed
non-null.

JIT-dump-pinned: the inlined `UnitTest_NestedGenericRefOut` body is
`ldfld.ref r1,StringValue; ldloca.s r3,r1; move r4,r3; initobj r4,String`. At
runtime the initobj's DstOffset (20) reads the byref `(-1, 4)` (frame-native
byref to `loc` at offset 4); the direct-write nulled the byref temp, not `loc`.

## The discrimination problem (why every runtime fix is ambiguous)
initobj on a reference type has TWO operand shapes that are INDISTINGUISHABLE at
runtime:
1. **A genuine byref to a DECLARED reference local** (NestedGenericRefOut: byref
   `(-1, locOffset)` sitting in the temp slot). Needs DEREF (write null at
   frameBase+off).
2. **An alias-FOLDED reference TEMP** (Activator:
   `EqualityComparer<T>.Default.Equals(value, default)` -- the `default` temp is
   a stack temp, `LocalIsReference=false`, so it IS folded; DstOffset resolves to
   the temp itself; a null temp reads `(-1, 0)`). Needs DIRECT WRITE (write null
   at frameBase+DstOffset).

A null target (shape 2 with the temp already null) and a frame-native byref
(shape 1) BOTH read objIdx == -1 at runtime. Confirmed empirically: the
Activator String initobj reads `(-1, 0)` and the NestedGenericRefOut String
initobj reads `(-1, 4)` -- the only difference is the `off` half, which for
shape 2 is the NEXT slot's value (coincidentally a valid small offset when the
temp is null). No reliable runtime discriminator exists.

## Fixes attempted (all aborted)
1. **Runtime-only deref (un-gated):** fixed NestedGenericRefOut (isolation 1/0)
   but full smoke 21 -> 22 (regressed ActivatorCreateInstanceWithArgsTest +
   InheritanceTest20, both confirmed pass-on-HEAD/fail-with-fix in isolation).
   Reverted.
2. **JIT marker (NeoInitobjByrefOperandMarker) gated on
   `ResolveLiveAlias(r1).Reg == r1`:** FAILED -- the static addrAlias
   (Optimizer.Neo.cs:35-81) has STALE register-reuse entries, so for
   NestedGenericRefOut ResolveLiveAlias(20).Reg = 4 (a stale alias), not 20 ->
   discriminator FALSE -> marker NOT set -> did not fix the target.
3. **Same marker gated on `liveAliasMap == null || !liveAliasMap.ContainsKey(r1)`:**
   FAILED -- for the Activator ref-temp case the liveAliasMap entry is absent at
   the initobj (the ldloca's source register is reused/mis-marked), so the marker
   was ALSO set for Activator -> same regression.
4. **JIT-only forward-walk map (refLocalByrefSource, independent of liveAliasMap,
   tracking ldloca/ldarga of declared ref locals + Move propagation, consulted by
   the Initobj case to re-resolve DstOffset to the ref local's offset):** FAILED
   -- a process-static diagnostic (`Optimizer.NeoInitobjOptDbg`) showed, for the
   NestedGenericRefOut initobj, `initobj R20 mapKeys=2 srcIsRef=n/a
   prevIsRef(r1=4)=False`. The initobj operand is register 20 (a stack temp);
   ResolveLiveAlias(20) -> register 4 (stale reuse alias); and
   `localIsRef[4] == false`. So the ldloca-of-a-ref-local guard never fired and
   the map was never populated for `loc`. The reference local `loc` is NOT
   reliably identifiable from the initobj site through the existing
   alias/localIsRef machinery.

## Recommended fix (durable, for a future child)
A JIT-level resolution that does NOT depend on the unreliable alias/localIsRef
state. Two viable shapes:

**Option A -- CIL-producer scan at JIT emission (JITCompiler.cs case Code.Initobj):**
at CIL emission time, inspect `ins.Previous` (and follow back through
`Instruction.Previous` across the CIL chain) to find the producing ldloca/
ldarga/ldflda. If the producer's source is a reference-typed local/param/field,
stamp a JIT marker (Operand4 bit) so the runtime reference-type arm derefs;
otherwise direct-write. The CIL `Instruction.Previous` link is STABLE (unlike the
register-VM body), so this is reliable. Handle inlining by noting the initobj
comes from the inlined method's CIL (the JIT processes the inlined body with its
own CIL links). This mirrors the child-24/29 predecessor-check pattern.

**Option B -- make the addr-alias folding track reference locals for initobj
specifically:** remove the `LocalIsReference` exclusion in the alias-build + the
liveAliasMap maintenance, BUT only let the Initobj consumer use it (gate the
_Init/ldflda-inline consumers to keep excluding ref locals). RISKY -- changes
alias semantics; the escape-reconciliation pass (Optimizer.Neo.cs:83-300) would
need to cover ref-local ldlocas, and the heap byref path (ldind/stind/stobj/
ldobj/ldelema) must still see a genuine runtime Ref Slot. Larger surface.

Option A is preferred (localized, CIL-level, stable links).

## Scope note
The initobj-byref fix, however implemented, fixes exactly ONE test
(UnitTest_NestedGenericRefOut; 21 -> 20). It does NOT unblock the D7 sibling
StructTest6 (CLR-binding out-STRUCT write-back -- a different path). It is worth
doing for correctness (the gap silently loses `default(T)` on any reference T
reached via a byref), but it is NOT a high-coverage win.
