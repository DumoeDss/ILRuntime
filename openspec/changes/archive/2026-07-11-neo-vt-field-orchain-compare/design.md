# Design — neo-vt-field-orchain-compare

> Capability: N/A (Neo interpreter correctness fix). Branch
> `features/object-model-overhaul`. Status: **DONE** — a one-condition fix in
> `ExecuteNeo`'s `Brtrue`/`Brfalse` arms.
>
> This is the follow-up to the PRE-EXISTING bug reported (but not fixed) in the
> child-17 `neo-array-multidim-ilvt` design ("A rank-1 IL-VT multi-cell probe
> exposed a PRE-EXISTING string-comparison bug: `||`-chained multi-string-`!=`
> on IL-VT struct fields evaluates wrong on pure HEAD").

## The bug (as reported)

A combined `||`-chained multi-string-`!=` on IL value-type struct fields
evaluates WRONG (the `||` chain mis-fires to true) when the struct is an
**array element** (rank-1 AND multi-dim), even though each INDIVIDUAL
`s.field != "x"` is correct. The child-17 note correctly identified this as a
string-comparison-in-`||` issue independent of arrays per se; this change
isolates the exact root cause and fixes it.

## Probe-first isolation (the reproducer)

A focused reproducer (`TestCases/NeoStepOrChainTest.cs`) narrows the bug:

- `bool wrong = (s.a != "x" || s.b != "y");` on a **plain struct local** (no
  array) where both fields match → PASSES on HEAD (the chain is correct).
- `bool wrong = (r0.a != "a" || r1.b != "e");` where `r0`/`r1` are **array
  elements** (`NeoStepOrChainVt3[] a; NeoStepOrChainVt3 r0 = a[0];`) → FAILS
  on HEAD (the chain mis-fires to true; `wrong` is wrong).
- A SINGLE `r0.a != "a"` on the SAME array-element struct → PASSES on HEAD.

So the divergence is NOT the per-field read, NOT `op_Inequality`, and NOT the
`||` short-circuit per se. It requires the **array-element read path**
(`ldelem.any` → `CopyILToFrame` into the frame) combined with the `||` chain.

## The exact root cause (a STALE-HIGH-BYTE branch read, NOT a compare bug)

Instrumented trace of the failing case (`NeoStepOrChain_Rank1ArrayMultiCell`):

```
op_Inequality arg0(idx=32)=a arg1(idx=35)=a -> result=False   (CORRECT)
[CALLPOST]  op_Inequality destVal=0 DstOffset=20               (4-byte bool written)
[BRTRUE]    DstOffset=20 Operand2=8 val=1 willBranch=True     (8-byte read -> non-zero!)
            offDstInt=0  offDstLong=17179869184 (0x0000000400000000)
```

The trace proves:

1. `string.op_Inequality("a","a")` correctly returns **False** and the redirect
   writes the 4-byte bool `0` into the dest slot (offset 20). **The compare is
   correct.**
2. The `brtrue.s` then reads the slot as an **8-byte long** (`Operand2==8`) and
   sees `0x0000000400000000` (non-zero) because the **HIGH 4 bytes are stale**.
   The low int32 is `0` (correct), but the high dword is `4` (a residue left by
   the prior `ldelem.any` / IL-VT field-layout use of the reused register).
3. The 8-byte read is non-zero → `brtrue` mis-fires → the `||` chain takes the
   "true" branch → `wrong = true` (WRONG).

### Why the 8-byte read exists (and why it is wrong)

`Optimizer.Neo.cs` (`Brtrue`/`Brfalse` case) sets `op.Operand2 =
localInfos[r1].Size`. `AllocateLocalStackSpaces` (`JITCompiler.cs`) sizes EVERY
temp register slot to the method's MAX value-type size (`maxSize`, minimum 8):

```csharp
int maxSize = 8, maxRefCount = 1;
foreach (var i in valueTypes) { ... if (size > maxSize) maxSize = size; ... }
for (int i = 0; i < frame.StackRegisterCount; i++) {
    slot.Size = maxSize;   // every temp slot is 8+ bytes
}
```

So `localInfos[r].Size` is the **slot width** (8), NOT the **value width**. The
prior `ExecuteNeo` `Brtrue`/`Brfalse` arms read the full slot width when
`Operand2 == 8`:

```csharp
if (ip->Operand2 == 8 ? *(long*)(frameBase + ip->DstOffset) != 0
                      : *(int*)(frameBase + ip->DstOffset) != 0)
```

A bool/int32 producer (a compare `Ceq`/`Cgt`/..., a `ceqi`, or a bool-returning
`Call` such as `string.op_Inequality`) writes only the LOW 4 bytes
(`*(int*)dst = ... ? 1 : 0`). When the dest register is REUSED (as the `||`
chain reuses it across compares, and as the array-element read path fills it
with non-zero high bytes), the stale high dword makes the 8-byte `brtrue` read
non-zero → the branch mis-fires.

This is a **systematic** latent bug, NOT array-specific: ANY `brtrue`/`brfalse`
whose source register holds stale high bytes (from any prior wider use) will
mis-fire. The plain-struct-local case happened to pass because the reused
register's high bytes were coincidentally zero; the array-element case fills
them with the struct-field layout residue, exposing the bug. The validated test
suite passed only by luck (clean high bytes).

## The fix

`Brtrue`/`Brfalse` test a CIL **truth value**, which is an `int32` (or an
object reference, which is a 4-byte mStack index under Neo). Roslyn lowers
EVERY non-int32 truthiness to a compare (`ceq`/`cgt.un`) whose result IS a
4-byte int32 0/1 before the branch — verified by the `NeoStepQlong` JIT
(`if (L != 0L)` lowers to `cgt.un.i8` → int32 result → `brfalse`; never a raw
int64 to `brtrue`). So the value reaching `Brtrue`/`Brfalse` is ALWAYS the low
int32.

The fix reads only the low int32 (drops the `Operand2 == 8 ? *(long*)` path):

```csharp
case OpCodeREnum.Brtrue:
case OpCodeREnum.Brtrue_S:
    // ... (see comment in source) ...
    if (*(int*)(frameBase + ip->DstOffset) != 0) { ip = ptr + ip->Operand; continue; }
    break;
case OpCodeREnum.Brfalse:
case OpCodeREnum.Brfalse_S:
    if (*(int*)(frameBase + ip->DstOffset) == 0) { ip = ptr + ip->Operand; continue; }
    break;
```

This is correct because the truth value is always the low int32, and it
eliminates the stale-high-byte mis-fire. The optimizer's `op.Operand2 = size`
stamp is left in place (now unused by these arms; harmless).

### Why this is Neo-only and Legacy-neutral

The fix is entirely within `ILIntepreter.Neo.cs` (the whole file is
`#if ENABLE_NEO_MODE`). Legacy uses `ExecuteR` (`ILIntepreter.Register.cs`),
which has its OWN `Brtrue`/`Brfalse` arms keyed on `reg1->ObjectType` (a typed
slot model, not flat bytes) — unaffected. Plain-`Debug` (Legacy) CLI builds
0 errors. The fix cannot reach Legacy code.

### Why not fix the producer side instead

The alternative (zero-extend every bool/int32 result to the full 8-byte slot at
every compare opcode + every bool-returning `Call`) would touch dozens of sites
(including every autogen redirect's `*(int*)__retDst = ...`). It would also
leave the underlying mis-design (reading the slot width instead of the value
width) intact. The consumer-side fix is one condition, correct by CIL
semantics, and closes the whole class of stale-high-byte branch mis-fires.

## Probes (kept)

`TestCases/NeoStepOrChainTest.cs` (10 tests, all green; part of the `NeoStep`
smoke):

- `NeoStepOrChain_TwoFieldsBothFalse` — plain struct, 2-field `||` chain,
  both false (PASSES on HEAD too — the no-array control).
- `NeoStepOrChain_ThreeFieldsBothFalse` — plain struct, 3-field.
- `NeoStepOrChain_SingleFieldControl` — single `s.a != "x"` control.
- `NeoStepOrChain_FirstCompareTrue` / `_SecondCompareTrue` — `||` short-circuit
  fires when it should (one compare true).
- `NeoStepOrChain_TwoStructsBothFalse` / `_ThreeStructsBothFalse` — `||` chain
  spanning DISTINCT struct locals (both false).
- `NeoStepOrChain_Rank1ArrayMultiCell` — **the load-bearing FAIL-on-HEAD
  reproducer**: `r0.a != "a" || r1.b != "e"` on array-element structs. FAILs on
  HEAD (stale-high-byte mis-fire); PASSES with the fix.
- `NeoStepOrChain_Rank1ArrayThreeCell` — 3-way array multi-cell (FAIL-on-HEAD).
- `NeoStepOrChain_Rank1ArraySingleControl` — single `r0.a != "a"` on an
  array-element struct (PASSES on HEAD — proves the per-field read is right).

## Verification

- Neo `NeoStep` **289/0/0** (279 baseline + 10 orchain probes).
- Neo `NeoOptHardening` **24/0/0** (unchanged).
- **Stash-toggle (load-bearing):** `NeoStepOrChain` is 8/10 on HEAD (the two
  array multi-cell probes FAIL with the stale-high-byte mis-fire); 10/10 with
  the fix. The plain-struct + single-compare probes pass on both (they are the
  controls).
- **Legacy-neutral:** the fix is `#if ENABLE_NEO_MODE`-only; plain-`Debug` CLI
  builds 0 errors; Legacy's `ExecuteR` branch arms are untouched.
