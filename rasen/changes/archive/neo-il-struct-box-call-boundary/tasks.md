# neo-il-struct-box-call-boundary -- tasks

## Phase 1 -- re-audit (VERIFY at 15)
- [x] Build CLI Debug_Neo --no-incremental + TestCases Debug (build-server
      shutdown + UseSharedCompilation=false). 0 errors each.
- [x] Confirm StructTest11 + TestStructDictionary FAIL at the 15 baseline.
      StructTest11: ArgumentOutOfRangeException @ List.get_Item (Add_0_Neo:98
      ReadNeoReference reads garbage mStack index from the Anim struct's
      float bytes). TestStructDictionary: NRE @ Ldfld_I4 heap arm (item.id)
      -- downstream of the same Add-boxing bug (garbage stored -> null
      retrieved -> field-read NRE).
- [x] Confirm both PASS under Legacy (the handoff's premise).

## Phase 2 -- implement (3-site, Neo-gated -> Legacy-neutral)
- [x] JITCompiler.cs: add NeoCallParamMap.PrimitiveBoxIlType +
      PrimitiveBoxSrcRefOff; add CompiledFrame.NeoRegisterTypes;
      TypeSpecializeNeoOpcodes returns registerTypes; RunNeoBackHalf
      stores frame.NeoRegisterTypes.
- [x] Optimizer.Neo.cs: capture paramTypes[] in the CLRMethod paramInfos
      build; seed + maintain curVtTypes in the LowerNeoOffsets main loop;
      discriminator (paramType reference + curVtTypes[srcReg] IL-VT) sets
      PrimitiveBoxIlType[i]; arrays only carried when a slot boxes.
- [x] ILIntepreter.Neo.cs: CopyNeoCallArguments gains frameRefBase; prim-
      loop boxing branch (Instantiate(false) + CopyFrameToIL + park on
      mStack + write index); all 11 call sites updated.

## D2 re-audit refinement (the crux)
- [x] DISPROVED the handoff's "srcInfo indicates flat-bytes IL-struct":
      srcInfo shape is ambiguous (Anim = 4 prim/1 ref == a reference slot)
      AND frame.NeoRegisterTypes is last-write-wins (StructTest11's reused
      r5 reads Int32 at the call, not the mid-pass Anim). Replaced with a
      position-correct curVtTypes tracker in the LowerNeoOffsets loop,
      seeded from NeoRegisterTypes + updated per-instruction.

## Verify (truth = full-smoke number)
- [x] StructTest11 + TestStructDictionary PASS after fix (name-filter).
- [x] Stash-toggle airtight: stash the 3 engine files -> rebuild -> both
      FAIL (HEAD) -> pop -> rebuild -> both PASS (fix).
- [x] FULL SMOKE: 15 -> 13 (`Ran 938 tests, 13 failded, 20 ignored, 7
      todos`; both target tests flipped; 13 are a strict subset).
- [x] NeoStep 404/0 (no regression -- the load-bearing gate for a calling-
      convention change).
- [x] Legacy-neutral: plain Debug build 0 errors.

## Files (NOT committed -- LEAD commits)
- ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs
      (NeoCallParamMap fields; CompiledFrame.NeoRegisterTypes;
       TypeSpecializeNeoOpcodes returns IType[]; RunNeoBackHalf stores it)
- ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs
      (paramTypes capture; curVtTypes seed + per-iteration maintenance;
       map-build discriminator + box-array carry)
- ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs
      (CopyNeoCallArguments frameRefBase param + boxing branch; 11 sites)

Capability = neo-optimizer (owns LowerNeoOffsets / the call-param map-build
+ the position-correct type tracker; child-7/11/16/21/23 precedent).
