# Design — neo-ret-vt-with-ref-fields

> HIGH#1 engine gap (lead-6 frontier). The `Ret` opcode in
> `ILIntepreter.Neo.cs` throws `NotImplementedException` for ANY IL method
> returning a struct WITH reference fields. Blocks general such returns AND
> async `ValueTask<T>` suspend (child 4 builds on top).

## Root cause (code-grounded, confirmed by reading the engine + JIT)

`ILIntepreter.Neo.cs` `case OpCodeREnum.Ret:` (~2923-2953):
- If `retDst != null && (returnPrimitiveSize > 0 || returnRefCount > 0)`:
  - `returnRefCount == 0` -> `Unsafe.CopyBlock(retDst, frameBase + ip->DstOffset, returnPrimitiveSize)` (pure-primitive VT return; works).
  - else -> `isSingleReferenceReturn = returnType != null && !IsPrimitive && !IsValueType && returnPrimitiveSize == 4 && returnRefCount == 1`. If NOT single-ref-return -> **throw NIE** (line 2940).
  - The single-ref-return path (a CLASS / ref-type return): reads `retSrcIdx = *(int*)(frameBase+ip->DstOffset)`, copies `mStack[retSrcIdx]` -> `mStack[retRefBase]`, writes `*(int*)retDst = retRefBase`.

So a struct-with-ref-fields return (e.g. `struct S { int x; string s; } S Make(){...}`)
hits the `else` branch, is NOT single-ref-return (`returnType.IsValueType` is true),
and throws.

## The `<returnSlotRefOffset>` finding (the load-bearing discovery)

The return value lives in a register/temp slot inside the CALLEE frame. The Ret
opcode reads its primitive bytes from `frameBase + ip->DstOffset` (DstOffset is
the return register's lowered byte offset). But the return value's REF-SLOT
offset within the callee's mStack region is NOT recorded anywhere on
`CompiledFrame`:

- `JITCompiler.AllocateLocalStackSpaces` (~1861-1872) computes the return
  slot's size via `AllocateSlotForType(retType, ref dummyOffset, ref dummyRef)`
  using LOCAL `dummyOffset=0, dummyRef=0` cursors -- it records ONLY
  `frame.ReturnPrimitiveSize` (= size) and `frame.ReturnRefCount` (= count),
  NOT the slot's position. The return value's actual storage is the return
  REGISTER's slot (allocated in the temp-register loop), whose `RefOffset` lives
  in `localInfos[retRegIndex].RefOffset`.
- The Ret JIT translation (`JITCompiler.cs:2291-2294`) does
  `op.Register1 = --baseRegIdx` (return value register). After register
  allocation + `LowerNeoOffsets`, `ip->DstOffset` = that register's byte offset.
- `LowerNeoOffsets` (`Optimizer.Neo.cs:854-857`) for `Ret` ONLY calls
  `LowerR1(ref op, localInfos)` (sets DstOffset); it does NOT stamp the return
  register's `RefOffset` anywhere. So at runtime the Ret arm has the primitive
  byte offset but NO ref offset for the return value.

**Fix: stamp the return register's RefOffset into the Ret opcode's spare
`Operand3` field (@16) during `LowerNeoOffsets`**, mirroring how `Initobj`
(`Optimizer.Neo.cs:805-820`) stamps its target slot's RefOffset into Operand3,
and how `Move_Vt`/`Box`/etc. stamp ref offsets. Ret uses only `Register1`/
`DstOffset`; `Operand`/`Operand2`/`Operand3`/`Operand4` are all spare. Operand3
is the conventional choice (matches Initobj/Move_Vt/Box).

## The fix (two files, both Neo-only / Neo-gated)

### 1. `Optimizer.Neo.cs` `case OpCodeREnum.Ret:` (~854-857)
Stamp `op.Operand3 = localInfos[r1].RefOffset` (the return register's ref
offset) ALONGSIDE the existing `LowerR1`. Resolve `r1` from `op.Register1`
(post-alias; Ret's source register is the eval-stack top, never an ldloca alias
dest, but use `ResolveLiveAlias` for safety mirroring Initobj). This makes the
return value's callee-side ref base available as `ip->Operand3` at runtime.

This is in `Optimizer.Neo.cs` which is **Neo-only** (file-gated
`#if ENABLE_NEO_MODE`) -> Legacy-neutral by construction.

### 2. `ILIntepreter.Neo.cs` `case OpCodeREnum.Ret:` (~2931-2953, the `else` branch)
Replace the `if (!isSingleReferenceReturn) throw NIE;` with a value-type-with-
ref-fields branch (mirror Step 12b `Move_Vt` at 1322-1336):

```
if (!isSingleReferenceReturn)
{
    // Value-type return WITH reference fields (Step 12b return layout):
    // copy returnPrimitiveSize primitive bytes to the caller's dest, then
    // copy returnRefCount ref slots from the callee's return-value ref region
    // (frameRefBase + ip->Operand3) to the caller's retRefBase. ip->Operand3
    // carries the return register's RefOffset (stamped by LowerNeoOffsets).
    // Mirrors Move_Vt's byte CopyBlock + ref-slot loop. Shallow copy: refs
    // are shared (C# struct-copy semantics).
    int retSrcRefOff = ip->Operand3;
    if (returnPrimitiveSize > 0)
        Unsafe.CopyBlock(retDst, frameBase + ip->DstOffset, (uint)returnPrimitiveSize);
    for (int i = 0; i < returnRefCount; i++)
        mStack[retRefBase + i] = mStack[frameRefBase + retSrcRefOff + i];
}
```

The existing single-ref-return (class return) path is UNCHANGED. The pure-
primitive path is UNCHANGED.

### Caller-side dest (verified, no change needed)
The caller at a Call site computes `targetRetRefBase = frameRefBase + ip->Operand3`
(the CALLER's dest ref base, a different Operand3 -- the call instruction's own
operand). The caller's dest register is allocated + typed by the existing JIT
(`AllocateLocalStackSpaces` temp loop + `TypeSpecializeNeoOpcodes`), which
already sizes a VT-with-ref-fields dest as flat bytes + ref slots (the SAME
machinery that makes `var s = Make(); s.field` work for the local form). The
caller reads the returned VT's fields via the existing `_Inline`/heap field-
access arms against its dest register -- no caller-side change. `retRefBase`
points at a region the caller reserved for `returnRefCount` slots (the call
instruction's dest ref region) -- large enough by construction.

## Scope boundary (IMPORTANT)
This child is ONLY the general Ret-opcode value-type-with-ref-fields return fix
+ a general (non-async) test. It does NOT touch the async `get_Task` redirect /
ValueTask suspend path (child 4 `neo-async-valuetask-asyncvoid`). If the async
`get_Task` redirect also needs the value-type return path, that is recorded as a
follow-up for child 4 (the redirect currently uses `WriteReferenceReturn` /
`WriteNeoValueType`; child 4 will switch it to the value-type return path).

## Test design (adversarial; FAIL-on-HEAD -> PASS-after stash-toggle)
New file `TestCases/NeoStepRetVtTest.cs`, class `NeoStepRetVtTest` (the class
name carries the `NeoStep` filter). Methods use divide-by-zero assertion (the
established NeoStep convention; no `[ExpectedException]`, no `new Exception`).
Probes:
1. `NeoStepRetVt_OneRef` -- struct `{ int; string }` return; assert int + non-null string.
2. `NeoStepRetVt_ManyRefs` -- struct `{ int; string; object }` return; assert all.
3. `NeoStepRetVt_ReadAfterReturn` -- `var s = Make(); assert s.s == "expected";`.
4. `NeoStepRetVt_ShallowCopyIndep` -- after return, re-assign source's ref to a new object; assert returned copy's ref unchanged (shallow copy: ref copied, but a NEW ref assignment to source post-return does not mutate the returned copy).
5. `NeoStepRetVt_NestedVtWithRef` -- a struct containing another struct-with-ref-field, IF reachable without hitting an unrelated NIE (e.g. stfld.value). If it hits an unrelated NIE, scope it out and note it (do NOT force it).
6. `NeoStepRetVt_PurePrimitiveControl` -- a pure-primitive struct return (already works) stays green (control).

## Verification
1. NeoStep smoke green with new probes PASSing (241+N/0/0).
2. FAIL-on-HEAD -> PASS-after stash-toggle: `git stash` the engine change, run
   the new probe filter -> FAILS with NIE; `git stash pop` -> PASSES.
3. Legacy-neutral: change is in Neo-only files (`ILIntepreter.Neo.cs` +
   `Optimizer.Neo.cs`, both `#if ENABLE_NEO_MODE`-gated); plain `Debug` builds
   (Neo file compiles out) + Legacy NeoStep filter unaffected.
4. No regression in broader NeoStep smoke.

## Decisions locked
- D1: stamp the return register's RefOffset into Ret's spare `Operand3` in
  `LowerNeoOffsets` (mirrors Initobj/Move_Vt); reuse an existing spare field,
  no `CompiledFrame` struct change.
- D2: runtime Ret arm adds a VT-with-ref-fields branch (byte CopyBlock + ref-
  slot loop), mirroring `Move_Vt`; shallow-copy semantics (refs shared).
- D3: caller-side dest needs NO change (existing JIT sizes/types the call dest
  for a VT-with-ref-fields return).
- D4: do NOT touch the async get_Task redirect (child 4 scope); record as follow-up.

## Follow-ups surfaced
- (For child 4) The async `get_Task` redirect for `ValueTask<T>` must use the
  value-type return path (now available) instead of `WriteReferenceReturn` /
  the ref-type return store. This child unblocks that; child 4 wires it.

## Apply findings (dump-confirmed, 2026-07-09)

**The Ret fix is correct and load-bearing.** A temporary `Console.WriteLine`
inside the Ret VT-with-refs branch (run for `MakeManyRefs` -- a 2-ref struct
return) confirmed: `returnPrimitiveSize=4 returnRefCount=2 retSrcRefOff=4
retRefBase=6 frameRefBase=10 DstOffset=16`, and both ref slots copied correctly
(`src[0]@14=world -> dst[0]@6`, `src[1]@15=123 -> dst[1]@7`). The
`retSrcRefOff=4` is the return register `r4`'s RefOffset (stamped by the
LowerNeoOffsets fix); the return value's ref region `mStack[frameRefBase+4..+6]`
held both refs (populated by the prior `move.vt r4, r3`). Diagnostic removed
after confirmation.

**Caller-side dest needs NO change (D3 confirmed).** The optimizer's copy-prop
ELIMINATES the post-call `move r1, r14` -- the call writes DIRECTLY into the
local `r1` (`call r1, ...`). The local `r1` is sized/typed as the IL VT (with
N ref slots) by `BuildInitialRegisterTypes` + `AllocateLocalStackSpaces`, so the
caller's dest ref region (at `frameRefBase + localInfos[r1].RefOffset`) is
large enough for `returnRefCount` slots. The Ret arm writes there; the caller's
`ldfld.ref.inline` reads from the same region. No caller-side Move/Move_Vt
rewrite needed (the call-dest IS the local, no intermediate Move). The
propose-time worry about a caller-side `Move` of a multi-ref VT was unfounded
(the optimizer removes that Move).

**Test-harness edge: `object.ReferenceEquals` has a Neo gap.** Probe 2
(`NeoStepRetVt_ManyRefs`) originally asserted `object.ReferenceEquals(r.obj,
box)` -- this FAILS not because the ref copy is wrong (the dump proves it is
correct) but because `Object.ReferenceEquals(Object, Object)` is a CLR static
call with its own Neo gap. Switched to `r.obj != null` + `(int)r.obj != 123`
(unbox + value compare), which proves the ref slot survived the return copy
with the right object. Durable: Neo test probes should AVOID
`object.ReferenceEquals` for identity checks; use `==`/`!=` on interned
strings or unbox+value-compare.

**Test-harness edge: `Ldfld_Value` (whole-nested-struct load) is a Step-6/12b
NIE.** Probe 5 (`NeoStepRetVt_NestedVtWithRef`) originally read `r.inner.ix`;
the C# compiler lowers `r.inner.ix` (where `r` is a local of a struct-with-a-
nested-struct-field) to `ldfld.value` (load the WHOLE `inner` struct) +
`ldfld.i4` on it -- NOT `ldflda inner; ldfld ix`. `ldfld.value` is the
pre-existing Step-6 `Ldfld_Value` NIE (a Step 12b non-goal). Scoped per the
test-design guidance: probe 5 asserts ONLY `r.top` (which proves the nested
return copy populated the outer's primitive region). The nested RETURN itself
works (the Ret arm copies the whole `NeoStepRetVtNested` -- prim size 8 + 2
ref slots -- correctly); only the subsequent whole-nested-struct-field READ is
blocked. Durable: ANY Neo test that reads a field of a nested struct off a
returned/local struct hits `Ldfld_Value` until a future Step-12b follow-up
implements whole-VT field load.

