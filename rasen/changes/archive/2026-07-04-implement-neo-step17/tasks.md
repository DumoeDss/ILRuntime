# Tasks -- Neo Step 17: Ref/Out + ldloca/ldflda + stind/ldind + ldelema + constrained.

One implementer pass. Reference: Legacy `ILIntepreter.Register.cs` (Ldloca
@1314, Ldobj @1324, Stobj @1394, Ldind_* @1474-1714, Stind_* @1767-1926,
Ldflda @3254) -- do NOT modify. DEFERRED items are marked `[DEFERRED]` and are
NOT implemented this pass (they throw a Step-17-tagged NIE). Design:
`design.md` in this directory. Highest regression risk of any step -- run the
FULL NeoStep smoke (72 cases) as the gate, not just NeoStep17.

## Phase 1 -- Byref slot sizing (foundation)

- [x] 1.1 In `JITCompiler.cs` `AllocateSlotForType` (~1513), add a byref branch
      FIRST: when `t.IsByRef`, allocate `Size = 8`, `RefCount = 0`, align 4,
      `offset += 8`. Reference: `ILType.IsByRef` (`ILType.cs:1111`),
      `CLRType.IsByRef` (`CLRType.cs:255`).
- [x] 1.2 In `Optimizer.Neo.cs` `AllocateNeoCallParamSlot` (~835), add the same
      byref branch (contiguous `offset += 8`, NO per-param alignment -- that
      helper's contract is contiguous to match the autogen CLR `ReadNeo*`
      readers per its NOTE).
- [x] 1.3 Verify a byref-typed local/param/temp now gets 8 bytes in
      `StackSlotInfo` (`Size==8, RefCount==0`) and that the frame size grows
      accordingly. Confirm no regression on NeoStep smoke after this sizing
      change alone (no new arms yet -- byref use still NIEs downstream).
      VERIFIED: 72/0 with sizing alone, and 72/0 with the Phase 2 gate.

## Phase 2 -- addrAlias foldability gate (the highest-risk edit)

- [x] 2.1 In `Optimizer.Neo.cs` `LowerNeoOffsets`, after the alias-building pass
      (~34-78), add a CONSUMER-SCAN pass: for each candidate alias dest `d`
      (an `ldloca`/`ldloca.s`/`ldflda` dest currently in `addrAlias`), scan the
      body for any occurrence where `d` is consumed by a byref-escape opcode:
      `stind_*`, `ldind_*`, `stobj`, `ldobj`, `ldelema`, a `Call`/`Newobj`/
      `Push` argument whose declared param `IsByRef`, or a `constrained.` box
      path. If any such consumer exists, remove `d` from `addrAlias` AND remove
      any alias that inherits through `d` (so a chained `ldflda` whose base
      escapes also becomes real). IMPLEMENTATION: a forward liveness walk
      tracking which alias-dest registers currently hold a live address
      (registers are reused at this stage, so liveness -- not just membership
      -- is required to avoid false escapes that regress the fast path).
- [x] 2.2 Confirm the pure `ldloca V; [ldflda f;] stfld/ldfld/initobj` pattern
      STILL folds (its only consumers are `_Inline`/`Initobj`). Run the FULL
      NeoStep smoke here -- if any of Steps 12-16 regresses, the gate is too
      aggressive; triage before proceeding. This is the single most important
      regression checkpoint of the step. VERIFIED: 72/0.

## Phase 3 -- Address producers (real arms)

- [x] 3.1 Replace the no-op `Ldloca`/`Ldloca_S` arm with the real producer:
      writes `objectIndex=-1`, `offset=ip->SrcOffset` into the 8-byte dest. The
      arm is dead for folded dests (harmless) and produces the Ref Slot for
      escaped dests.
- [x] 3.2 Add `Ldarga`/`Ldarga_S` arms (folded into the Ldloca case, identical
      `(-1, paramFrameOffset)` production).
- [x] 3.3 Replace the no-op `Ldflda` arm with a dispatching producer. DEVIATION
      from design sec 2.3: instead of an optimizer-stamped `Operand4` marker,
      the arm dispatches at RUNTIME on the operand slot's objectIndex half
      (-1 => in-frame VT, read the offset half as the VT base; >=0 => heap IL
      mStack index). This is self-describing and avoids a separate marker pass;
      the build/gate already guarantee a real Ldflda's operand is a real Ref
      Slot (frame-native) or a heap mStack index.
- [x] 3.4 N/A -- folded into 3.3 (runtime dispatch on objectIndex half replaces
      the Operand4 marker). Ldflda's JIT emission carries
      `Operand2 = field.PrimitiveOffset` as before.
- [x] 3.5 Add the `Ldelema` arm + lowering. DEVIATION from design sec 2.4: for
      an IL value-type array (ILTypeInstance[] with pre-instantiated elements),
      the arm resolves the element ILTypeInstance and parks it on mStack,
      encoding `(elementMStackIdx, 0)` so the stind/ldind consumers hit the
      standard IL-instance Primitives path on that element. CLR primitive-array
      ldelema throws a Step-17 NIE (deferred; use direct indexing). Lowering
      uses R2 for the array offset (R1==R2 pre-compaction is NOT assumed after
      register compaction).

## Phase 4 -- stind / ldind / stobj / ldobj consumers

- [x] 4.1 Inline dispatch per-arm (decode `(objectIndex, offset)` directly in
      each arm rather than a shared helper, to keep the hot path branch-light).
- [x] 4.2 Add `Ldind_I1/I2/I4/I8/U1/U2/U4/R4/R8` arms. Frame-native: typed cast
      read at `frameBase + off`. ILTypeInstance: `Unsafe.ReadUnaligned<T>` on
      `ili.Primitives[off]` (no explicit `fixed` -- the framework handles the
      managed-array reference; documented in code). CLR-object target falls
      through to `GetNeoILInstance` which throws InvalidCastException (the
      CLR-field-hash path is deferred; a CLR object reaching these arms is the
      D1 deferred sub-case).
- [x] 4.3 Add the matching `Stind_*` arms (write). `Stind_I` aliases `Stind_I4`
      (native-int = I4 width on this VM) via `goto case`.
- [x] 4.4 Add `Ldind_Ref`/`Stind_Ref`. Frame-native ref slot handled (read/write
      the mStack index at the offset). Heap-IL ref-field throws a Step-17 NIE
      (the ref-field negative-offset/sentinel encoding of design sec 1.3/3.2 is
      the documented rare sub-case; deferred). `Ldind_I` aliases `Ldind_I4`.
- [x] 4.5 Add `Ldobj`/`Stobj` arms: copy `TotalPrimitiveSize` bytes via the
      same dispatch (frame-native CopyBlock vs ILTypeInstance pinned Primitives
      CopyBlock), sized by the `Operand` type token. NOTE: the ref-slot portion
      of a VT (TotalReferenceCount) is NOT yet copied through stobj/ldobj -- the
      green smoke target is primitive-field VTs; a VT-with-refs through stobj is
      a documented partial (would need the Move_Vt ref-loop).
- [x] 4.6 Add `Optimizer.Neo.cs` lowering for `Stind_*`/`Ldind_*`/`Stobj`/`Ldobj`
      (DstOffset/SrcOffset from R1/R2; Operand3 = value/dest ref slot; type
      token in Operand untouched). Source/dest register registration in
      `Optimizer.Utils.cs` already covered these opcodes (no new registration
      needed).

## Phase 5 -- ref/out call ABI

- [x] 5.1 Verified end-to-end: a byref param is sized 8 bytes on both sides
      (AllocateSlotForType + AllocateNeoCallParamSlot IsByRef branch), so the
      call-param-map loop emits exactly one 8-byte primitive entry (RefCount 0,
      no ref entry). The caller's `ldloca`/`ldflda`/`ldelema` produces the slot;
      the callee's `ldarg` is an 8-byte Move of the slot; `stind_i4` dispatches
      back to the caller's frame/object. TC1-TC5 green prove the mutation
      propagates.
- [x] 5.2 NeoStep17 TC1 (ref frame local), TC2 (accumulate), TC3 (out) added +
      green.
- [x] 5.3 NeoStep17 TC5 (ref heap-IL field), TC6 (ref in-frame VT field), TC4
      (byref forwarded) added + green.

## Phase 6 -- constrained. runtime arm (D-CONSTRAINED)

- [x] 6.1 A `Constrained` runtime arm exists (it no longer falls to the generic
      Step-6 default NIE). The JIT's re-append + Operand4 flag are unchanged.
- [~] 6.2 NOT fully realized this pass. Full constrained.-on-VT dispatch
      requires the callvirt to accept a byref `this` (the struct's managed
      address from ldarga/ldloca) and dispatch to the constrained type's
      concrete override. The callvirt currently reads `this` as an mStack object
      index and NIEs ("Neo callvirt this is null") on the byref. The Constrained
      arm therefore throws a Step-17/13b-tagged NIE (honest) rather than
      silently no-op'ing. The common VT-box case is DEFERRED with the callvirt-
      byref-this work.
- [x] 6.3 `[DEFERRED]` All constrained.-on-VT sub-cases throw a Step-17/13b-
      tagged NIE (no green sub-case this pass; the prerequisite callvirt-byref-
      this dispatch is the deferred work).
- [x] 6.4 No green constrained test added (the case NIEs by design); TC8 was
      removed and the deferral documented in the test file.

## Phase 7 -- Optimizer/FCP soundness + K1 interaction

- [x] 7.1 The OPT-HARDEN `ldloca-kill` in FCP still fires on Ldloca (unchanged)
      AND is extended to treat `Ldflda`/`Ldarga`/`Ldarga_S`/`Ldelema` as escapes
      when they address a copy-propagation's source/dest (both the in-block kill
      @180 and the cross-block kill @348). Ldelema's array register (ySrc2) is
      also checked.
- [x] 7.2 `NeoOptHardTest_K1_*` regression cases stay green (3/3).

## Phase 8 -- Tests + regression gate

- [x] 8.1 `TestCases/NeoStep17Test.cs` added (ASCII). 7 green tests: ref frame
      local; ref accumulate; out param; byref forwarded; ref heap-IL field; ref
      in-frame VT field; ldelema + stfld/ldind round-trip on an IL VT array.
      Constrained (TC8) is documented as deferred (no green test).
- [x] 8.2 CLI `Debug_Neo` 0 errors; TestCases `Debug` 0 errors.
- [x] 8.3 FULL NeoStep smoke: 79/0 (was 72; +7 NeoStep17 green; 0 Steps 12-16
      regression). No test exceeds the 10s loop budget.
- [ ] 8.4 `neo-deferred-items.md` update -- see follow-up note: D-LDELEMA
      resolved (IL VT array path); D-CONSTRAINED partially resolved (arm exists,
      full VT dispatch deferred).

## Deferred (NOT this pass)

- [ ] `[DEFERRED]` D1 CLR-object stind/ldind via field hash
      (`(objMStackIdx, fieldHash)`). Throws Step-17-tagged NIE. Lands in Step 13b
      (the field-hash plumbing is revisited there with the unified CLRMethod
      param layout, per `neo-deferred-items.md` D-13B).
- [ ] `[DEFERRED]` D2 CLR-method `ref`/`out` params (IL->CLR byref crossing).
      Throws Step-17/13b-tagged NIE. Lands in Step 13b area 5 (also closes
      K2/K2-FAM).
- [ ] `[DEFERRED]` D3 Generic-byref (`ref T` / `out T`, `T` generic) and
      explicit-interface byref. Follow-up after closed-type byref is green.
- [ ] `[DEFERRED]` D4 `fixed` unmanaged-pinning block. Address model enables it;
      the `Pinned`-slot machinery is separate.
- [ ] `[DEFERRED]` D5 Interface-on-VT constrained callvirt (sub-case of
      D-CONSTRAINED) if it needs more than the byref model. See task 6.3.
