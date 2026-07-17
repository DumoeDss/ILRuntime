# Design: neo-lowerneoffsets-push-missing

## The discriminator (the crux)
`JITCompiler.cs` Newobj case rewrites `Newobj -> Call_Redirect` for a CLR target
with a redirect and stamps `op.Operand4 = 0x2 | (rCnt << 16)` (rCnt = number of
register params). Bit `0x2` is the ONLY marker that distinguishes a
Newobj-originated Call_Redirect from a Call/Callvirt-originated one:

| Call_Redirect origin | Operand4 bits set |
|---|---|
| Newobj              | `0x2`, `rCnt<<16` |
| Call/Callvirt       | `0x1` constrained, `0x4` hasReturn, `rCnt<<16` (never `0x2`) |

So `(op.Operand4 & 0x2) != 0` is a collision-free discriminator (audited: the
Call/Callvirt redirect path at `JITCompiler.cs:2779-2788` starts Operand4 at 0
and only ORs 0x1/0x4/rCnt<<16 -- never 0x2).

## The JIT/runtime contract (why treating it as Newobj is correct)
- JIT Newobj path: `pCnt = ParameterCount` (NO `this` bump; the `this` is the
  RESULT), emits `max(pCnt-3,0)` Pushes, R1 = dest, R2/R3/R4(+Pushes) = declared
  params. Then flips `op.Code` to `Call_Redirect`. The register layout is
  byte-identical to a genuine Newobj -- only `op.Code` and `Operand4` differ.
- Runtime `Call_Redirect` arm (`ILIntepreter.Neo.cs:3251`):
  `crIsNewObj = (ip->Operand4 & 0x2) == 0x2`, then
  `InvokeNeoClrMethod(targetMethod, crIsNewObj, crTargetBase, ...)`.
- Runtime `Newobj` arm (`:3479`): `InvokeNeoClrMethod(clrCtor, true, targetBase, ...)`.
  Both arms read `DstOffset`/`Operand3` the same way and consume the
  NeoCallParamMap identically.

=> The map + dest layout LowerNeoOffsets builds MUST be byte-identical for a
Newobj-originated Call_Redirect and a genuine Newobj. Achieved by substituting
`isNeoNewobjShape` for `op.Code == Newobj` at ALL 10 param-layout sites in the
LowerNeoOffsets Call/Newobj case.

## The 10 substituted sites (all in `Optimizer.Neo.cs` LowerNeoOffsets Call/Newobj case)
1. pCnt HasThis bump (`if (HasThis && !isNeoNewobjShape) pCnt++`)
2. `AllocNeoParamInfosFromSignature(..., isNeoNewobjShape, ...)` (IL abstract path)
3. `totalParams = pCnt + (isNeoNewobjShape ? 1 : 0)` (CLRMethod 'this'-slot reservation size)
4. `if (isNeoNewobjShape)` reserve paramInfos[0] = 'this' slot
5. CLRMethod param-type loop `dstIndex = isNeoNewobjShape ? p+1 : p`
6. CLRMethod `paramType = DeclearingType` when `HasThis && !isNeoNewobjShape && p==0`
7. CLRMethod `Parameters[p - ((HasThis && !isNeoNewobjShape)?1:0)]`
8. marshalling loop `dstIndex = isNeoNewobjShape ? p+1 : p`
9. `dstIsVtThisSlot = (HasThis && !isNeoNewobjShape && p==0) && IsValueType ...`
10. `paramLogical = p - ((HasThis && !isNeoNewobjShape)?1:0)`
11. dest stamping `if (isNeoNewobjShape)` -> DstOffset/Operand3 from R1

## NOT touched (deliberate)
- The alias-liveness analysis pass at `Optimizer.Neo.cs:236-288` has the SAME
  `op.Code == Newobj` pattern, but its Call-family case list (`:240-242`) does
  NOT include `Call_Redirect`, so a Newobj-originated Call_Redirect never reaches
  it. Left unchanged (no bug there for the current case list). Latent: if
  `Call_Redirect` is ever added to that case list, it would need the same
  `isNeoNewobjShape` treatment for its byref-escape analysis.

## Risk
LowerNeoOffsets is the ONLY Neo pass that changes instruction-body length and
runs for EVERY Neo method. The change is additive in effect: it only changes
behavior for `Call_Redirect` instructions whose Operand4 has bit 0x2 set
(Newobj-origin). For all other Call/Newobj/Call_Redirect instructions,
`isNeoNewobjShape` evaluates identically to the old `op.Code == Newobj`
(genuine Newobj -> true; everything else -> false). So the change is scoped to
exactly the buggy shape. NeoStep 382/0 (incl. NeoStep14 EH 26/26) confirms no
regression on the broad protected-region surface.
