## Context

Neo (`ExecuteNeo`) runs on a compact `byte*` frame whose slots are **untyped**:
a slot holds either flat primitive bytes, an in-frame value type, or a
**reference** encoded as an mStack index in the slot's primitive bytes. There is
no per-slot type tag (Legacy `ExecuteR` has `StackObject.ObjectType`; Neo does
not). This is the core reason the branch-condition bug exists and the core
constraint on the fix.

`Brtrue`/`Brfalse` (`ILIntepreter.Neo.cs:2063`/`:2071`) currently test
`*(int*)(frameBase + ip->DstOffset) != 0`. That is correct for an `int32`
truth value but wrong for a **reference**: a reference is stored as an mStack
index, and `null` is encoded as a non-zero index (IL-static `Ldsfld` does
`mStack.Add(null)` then stores the index, `Neo.cs:4302-4304`) or `-1` (CLR-static
`Ldsfld` `:4350`; `Ldnull` `:1526`). So `null != 0` is **true** → a null
reference is misclassified as truthy.

This breaks the pattern C# emits pervasively — `if (x == null) { x = init; }`
and the equivalent delegate-cache `if (cached == null) { cached = new ...; }`.
Roslyn lowers `x == null` on a reference to a **direct** branch on the reference
(`ldsfld x; brtrue skipInit`) with **no** `ceq`. With the bug, a null `x` reads
truthy, the init is skipped, `x` stays null, and downstream code NREs. Actively
failing: `TestStaticFieldInstance`, `RegisterVMTest04`, `NeoStep20_Tr2/Tr5`.

Legacy handles this via `reg1->ObjectType`
(`ILIntepreter.Register.cs:2039-2089`): `Object` → `mStack[v] != null`,
`Integer` → `v != 0`, `Null` → falsey. Neo has no such runtime tag, so the
distinction must be made at **JIT time**.

## Goals / Non-Goals

**Goals:**
- A reference-typed `Brtrue`/`Brfalse` condition tests the referenced object's
  nullness, not the raw mStack-index int. The `if(x==null)` / delegate-cache
  lazy-init patterns work.
- The fix is correct for both Roslyn lowerings of reference equality: the direct
  branch (`ldsfld x; brtrue`) AND the `ceq` form (`ld x; ld y; ceq; brfalse`).
- Fix the coupled F3 gap (IL-static `Stsfld`/`Ldsfld` raw `DstOffset`) in the
  same child so the `_Ref` deref arm never reads garbage from an un-lowered
  `Ldsfld`.
- Legacy-neutral; Neo-gated.

**Non-Goals:**
- Branch conditions on value types with reference fields, `IntPtr`/native-int
  truthiness nuances, or `Nop`/`Conv` chains feeding `Brtrue` — out of scope
  unless a smoke test regresses.
- A general runtime type-tag for the Neo frame (architecturally rejected; the
  Neo design is deliberately untyped and type-specializes at JIT time).
- `Ldsflda` of a CLR static (separate sibling). CLR value-type static fields
  with ref fields / no binder (Step-13b, child-8 scope).

## Decisions

### D1: Discriminate ref-vs-int at JIT time via the per-register type map (mirror `Move`)

Neo already has a compile-time per-register type map: `TypeSpecializeNeoOpcodes`
(`JITCompiler.cs:777`) maintains `registerTypes[]`, seeded by each producer
(`Ldc`→int, `Ldnull`/`Ldstr`→object, `Conv`→numeric, `Move`→propagated, params/
locals from Cecil). The `Move` op already uses it:
`op.Operand = IsNeoReferenceSlot(srcType) ? 1 : 0` (`JITCompiler.cs:852`), where
`IsNeoReferenceSlot(t) = t != null && !t.IsPrimitive && !t.IsValueType` (`:1450`).

**Decision:** add a `Brtrue`/`Brfalse` case to `TypeSpecializeNeoOpcodes` that
rewrites `op.Code` → `Brtrue_Ref`/`Brfalse_Ref` when
`IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register1))`. This is the
established Neo idiom (mirrors `Ldfld`→`Ldfld_Ref`, `Stfld`→`Stfld_Ref`,
`Ldind`→`Ldind_Ref`).

**Alternatives considered:**
- *Runtime `frame.LocalIsReference[]` flag.* REJECTED for temps. `LocalIsReference`
  (`JITCompiler.cs:2011`) marks only declared reference **locals/params**; the
  temp register file (`:2050-2062`) is left `false`. The `Brtrue` operand for the
  failing patterns (`ldsfld` result) lives in a **temp**, so `LocalIsReference`
  would not mark it. The `registerTypes` dataflow is the only signal that tracks
  a temp's current value type.
- *Size-based discrimination (`Operand2` carries slot size).* REJECTED. Reference
  temps share the max-VT size (8); reference locals are 4; ints vary. Size does
  not cleanly separate reference from int.
- *Runtime content heuristic.* REJECTED. A valid `int32` can collide with a
  plausible mStack index → ambiguous, unsound.

### D2: Seed `Ldsfld` dest type (the critical gap)

`Ldsfld` does **not** seed `registerTypes` today (confirmed: no `SetRegisterType`
for it in the specialize switch). Without seeding, the `_Ref` rewrite would never
fire for the dominant failing pattern `ldsfld ref; brtrue`.

**Decision:** add a `case OpCodeREnum.Ldsfld:` to `TypeSpecializeNeoOpcodes`
that resolves the static field type and seeds `registerTypes[op.Register1]`:
- declaring type = `appdomain.GetType((int)(op.OperandLong >> 32))`;
- `ILType` → `ilt.StaticFieldTypes[(int)op.OperandLong]` (reference type for a
  ref field → `IsNeoReferenceSlot` true);
- `CLRType` → `ct.GetField((int)op.OperandLong).FieldType` (a `System.Type`;
  wrap via the domain's CLR→IType resolution used elsewhere in the JIT).

`Stsfld` consumes (pops) its operand; it does not produce a dest, so no seeding.
Also **verify** (diagnose-first) the heap `Ldfld_Ref` dest seeding and the
reference-returning `Call` seeding (`:1180`/`:1207`) — seed if a smoke pattern
needs them; the `ldsfld`-fed patterns are the confirmed path.

### D3: Both Roslyn lowerings of reference equality are handled

- `ceq` form (`ld x; ld y; ceq; brfalse`): `ceq` seeds its dest as `IntType`
  (`:991`), so `IsNeoReferenceSlot` is false → stays plain `Brfalse` → tests the
  real 0/1 int. Correct, unchanged.
- direct form (`ldsfld x; brtrue`): with D2 seeding, `registerTypes[r1]` is the
  reference type → `Brtrue_Ref` → tests `mStack[idx] != null`. Correct.

### D4: Runtime null-sentinel test (one arm, all three null encodings)

The `Brtrue_Ref`/`Brfalse_Ref` `ExecuteNeo` arm:
```csharp
int idx = *(int*)(frameBase + ip->DstOffset);
bool truthy = idx >= 0 && mStack[idx] != null;   // Brtrue_Ref branches on truthy;
                                                 // Brfalse_Ref on !truthy
```
This uniformly covers: `Ldnull` (`-1` → falsey), IL-static null (valid index →
`mStack[idx] == null` → falsey), CLR-static null (`-1` → falsey), and any
non-null reference (`mStack[idx] != null` → truthy). It mirrors Legacy
`mStack[reg1->Value] != null` (`Register.cs:2053`). No bounds guard (Legacy
parity; after D5 the index is always valid). `ip->DstOffset` is a real byte
offset because `Brtrue`/`Brfalse` **are** lowered by `LowerNeoOffsets`
(`Optimizer.Neo.cs:697-708`).

### D5: Fix F3 (IL-static `Stsfld`/`Ldsfld` raw `DstOffset`) in the SAME child

F3: `Stsfld`/`Ldsfld` are **not** in `LowerNeoOffsets`' case-list, so at runtime
`ip->DstOffset` is still a raw register index. The CLR-static arms resolve it via
`localInfos[regIdx].Offset` (`Neo.cs:4192-4194`, `:4331-4332`); the IL-static
arms (`:4135`, `:4277`) read `frameBase + ip->DstOffset` directly — **wrong**
(reads the raw-index'th byte of the frame). Child-3 left this deliberately (see
the comment at `Neo.cs:4187-4191`) precisely because lowering it unmasked this
brtrue gap.

**Coupling verdict:** F3 and F4 meet on the delegate-cache pattern
`ldsfld(IL-static ref); brtrue` (used by `NeoStep20_Tr2/Tr5`).
- **F4 alone is NOT safe**: the un-lowered IL-static `Ldsfld` feeds the new
  `Brtrue_Ref` deref arm garbage bytes → `mStack[garbage]` → OOB crash / wrong
  nullness on Tr2/Tr5.
- **F3 alone is NOT safe**: child-3 established it unmasks F4 → Tr2/Tr5 break.
- **F3 + F4 together** is the only stable state: `Ldsfld` reads the real static
  value at the correct offset, `Brtrue_Ref` tests it correctly.

**F3 fix:** resolve the IL-static register byte offset via `localInfos` exactly
as the CLR-static arms do (`int off = localInfos[ip->DstOffset].Offset; byte*
slot = frameBase + off;`) for both the IL-static `Stsfld` read path (`:4135`) and
the IL-static `Ldsfld` write path (`:4277`). In-child order: F4 first (so the
brtrue arm is correct), then F3, then re-verify.

### D6: New enum members appended; dispatch guard already bounds-checks

`Brtrue_Ref`/`Brfalse_Ref` are appended to `OpCodeREnum` (implicit-numbered; no
values shift). Child-1's `ExecuteNeo` dispatch guard already recognizes the full
named range, so the new members dispatch correctly. `LowerNeoOffsets` gets them
added to the existing Brtrue R1-lowering case-list (same `Operand2 = size;
DstOffset = off1`).

## Risks / Trade-offs

- **[registerTypes dataflow imprecision]** `registerTypes` is a single linear
  pass, not full dataflow; at block joins a reused register could carry a stale
  type, mis-classifying a `Brtrue` operand (false-positive `_Ref` → deref a
  non-index int; false-negative → keep the int test on a ref → null misclassified
  again). → **Mitigation:** the `Brtrue` operand is always the straight-line
  top-of-stack produced immediately before the branch (same block), so the linear
  pass is reliable for this shape. The full NeoStep smoke (330) + the dedicated
  Tr2/Tr5 re-verify is the safety net; any mis-classification surfaces as a test
  failure. A stash-toggle of F4-alone (confirm Tr2/Tr5 break) proves the
  coupling before the full fix.
- **[F4-alone OOB]** Applying only F4 before F3 can crash on the delegate-cache
  path. → **Mitigation:** D5 — F3 lands in the same child, F4 first then F3; the
  apply worker must not stop between them.
- **[Other ref-producer seeding gaps]** If `Ldfld_Ref` or a ref-returning `Call`
  is unseeded, a `ldfld ref; brtrue` or `call; brtrue` on null would stay on the
  int test and still misfire. → **Mitigation:** D2 verify step; the probes target
  the confirmed `ldsfld` path; widen seeding only if a smoke pattern demands it.
- **[NeoStep regression probes must FAULT]** (child-1/child-2/child-3
  discipline): a probe that merely returns a wrong value does NOT fail the
  "ran without throwing" pass criterion. → **Mitigation:** TC1 asserts the
  initialized object is non-null/usable (NRE on HEAD); TC2 asserts the cached
  delegate identity. Both throw on HEAD without the fix.

## Open Questions

- Does any current NeoStep `Brtrue`/`Brfalse` rely (even indirectly) on the buggy
  `null-is-truthy` behavior? The full smoke re-verify answers this empirically;
  none expected (the bug only ever skips initializers / null-handlers, which is
  never desirable).
- Whether heap `Ldfld_Ref` dest seeding is needed for a green smoke (D2 verify).
