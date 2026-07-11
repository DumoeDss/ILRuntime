# Ship Log — neo-step17-generic-byref-etc (Step 17 (c) edges + F-10-R1 JIT-discriminator gate)

**Date:** 2026-07-06. **Branch:** `features/object-model-overhaul`. **HEAD at
apply:** `21900a3a` (propose-phase dump-gate ran at `0aafdb34`).
**Capability:** `neo-byref` (+ `neo-value-types` for the marker stamping).
**Working tree:** UNCOMMITTED (LEAD commits after ship).

---

## What shipped

A per-sub-item dump-gate DISPROVED the LEAD orientation hypothesis for 3 of 4
sub-items and pinned exactly ONE real engine gap. This is a small, surgical
change: 1 engine line + 6 test keepers.

### Sub-item verdicts (Debug_Neo, HEAD `0aafdb34`, NeoStep 198/198 baseline)

| # | Sub-item | Probe outcome on HEAD | Verdict |
|---|----------|-----------------------|---------|
| 1 | generic-byref (`ref T`/`out T`, T generic) | 3/3 PASS (Swap int / IL-ref / IL-VT) | **NO-OP** — TEST-ONLY guards |
| 2 | `fixed` unmanaged-pinning | FAIL `Conv_U not yet implemented (Step 6)` | **DEFER** — out of byref scope |
| 3 | interface-on-VT-constrained beyond common | 2/2 PASS (IL-VT direct-call; CLR-VT box) | **NO-OP** — TEST-ONLY guards |
| 4 | F-10-R1 JIT-discriminator gate | FAIL `NullReferenceException` at `NeoMarshalByrefFieldToSlot:466` | **REAL GAP** — JIT gate |

### The single engine change — F-10-R1 JIT-discriminator gate

**Site:** `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`,
`TypeSpecializeNeoOpcodes`, `case OpCodeREnum.Ldflda:` (the F-6 stamping block,
lines 862-914). When the F-6 in-frame-VT marker stamps
(`srcType is ILType srcIl && srcIl.IsValueType && !srcIl.IsEnum`), the gate
CLEARS the F-10 marker the main-JIT body emission set earlier:

```csharp
op.Operand4 |= NeoLdfldaInlineMarker;          // F-6 (existing, line 877)
op.Operand4 &= ~NeoLdfldaClrStructFieldMarker; // F-10-R1 (NEW, line 913)
```

**Why the type-spec-pass gate (NOT `!declaringType.IsValueType`):** the F-6 and
F-10 markers are OR-stamped by two separate JIT sites (F-6 in the type-spec pass;
F-10 in the main-JIT `case Code.Ldflda` body emission). `IsClrStructFieldOfIL`
returns true for an IL VALUE-type declaring type too (it checks `declaringType
is ILType`, not `!IsValueType`), so an in-frame-VT source whose field is a CLR
struct gets BOTH stamps (`Operand4 = 0x3`). The runtime Ldflda arm checks F-10
FIRST (`clrStructFieldMarker && objIdx >= 0`); for the constrained.callvirt
direct-call shape, slot-0 holds the struct's FLAT PRIMITIVE bytes (e.g. an int
`prefix`), which the body's `ldflda this.field` reads as the byref objectIndex
-> F-10 fires on a garbage index -> NRE.

The type-spec pass runs AFTER body emission, so the body's F-10 stamp is already
on `Operand4` at the clear. The gate keys on the OPERAND's value-category
(in-frame VT vs heap/boxed) — exactly the F-6 condition — so it is correct for
ALL three operand shapes:
- **in-frame IL VT source** -> F-6 stamped -> F-10 cleared -> F-6 shape 1/2/3 fires.
- **heap IL class source** -> F-6 NOT stamped -> F-10 stays -> F-10 path fires
  (the existing 8 `NeoClrStructField_*` probes — byte-identical).
- **boxed IL VT source** -> F-6 NOT stamped (operand is a heap mStack object,
  not an in-frame VT) -> F-10 stays -> F-10 path fires (correct for boxed-VT).

A naive `!declaringType.IsValueType` gate was REJECTED at propose: it would
suppress F-10 for the boxed-IL-VT-with-CLR-struct-field case (declaring type is
a value type, but the operand is a heap boxed object that correctly needs F-10).

**Runtime arm UNCHANGED.** The reviewer's recommended runtime F-6-before-F-10
reorder was DISPROVEN (broke 6 NeoStep17 F-6-only probes, 190->184; F-6 shape 3
vs shape 1/2 produce different byrefs for the `objIdx == -1` case every reachable
VT `this`/arg uses today). The fix is JIT-producer-side only.

**Neo-only, Legacy-neutral by construction.** `TypeSpecializeNeoOpcodes` is
`#if ENABLE_NEO_MODE` (file/run-gated). The Legacy JIT (`ExecuteR`) is the
semantic reference and is NOT modified. Confirmed via the Step 17 Legacy filter
(plain `Debug` + `useRegister=true`): 47/47 green.

### Tests (TestCases/NeoStep17Test.cs)

6 keepers added in a new `Step 17 (c) edges + F-10-R1` section:
- **3 generic-byref TEST-ONLY guards** (PASS on HEAD): `NeoStep17_GenericByRef_SwapInt`,
  `_SwapIlRef`, `_SwapIlVt`. Lock the type-agnostic closure.
- **2 interface-on-VT-constrained TEST-ONLY guards** (PASS on HEAD):
  `NeoStep17_InterfaceOnIlVtConstrained` (IL-VT direct-call),
  `_InterfaceOnClrVtConstrained` (CLR-VT box-once via `IComparable<int>`).
- **1 F-10-R1 adversarial keeper** (FAIL-on-HEAD -> PASS-after):
  `NeoStep17_F10R1_ConstrainedVtLdfldaClrField` — the IL VT
  `struct NeoStep17F10R1Vt { int prefix; TestVector3NoBinding field; }` implementing
  `INeoStep17SetAndSum`, invoked via a generic constrained caller
  `ProbeConstrainedCallSetAndSum<T>(T v, ...) where T:struct,INeoStep17SetAndSum`,
  body does `ldflda this.field` -> host `Set/SumTestVector3NoBindingByRef`. The
  both-stamp shape via constrained-VT direct-call forces `objIdx >= 0`.

### Deferrals

- **`fixed` statement** rerouted to a future pointer/`Conv_U` step. It needs the
  unimplemented `Conv_U`/`Conv_I` pointer-conversion opcodes (and optionally
  `Ldtoken` for initialized arrays), which are outside the `neo-byref` capability.
  The array-element address works via `ref arr[i]` (ldelema + stind/ldind, TC14
  green). Recorded in `neo-deferred-items.md` D-CONSTRAINED (c).

---

## Verification (Debug_Neo CLI, fresh TestCases.dll, pre-generated HotfixAOT.patch)

- **F-10-R1 reproducer (load-bearing):** FAIL-on-HEAD (`Object reference not set
  to an instance of an object` — NRE at `NeoMarshalByrefFieldToSlot`) -> PASS-after
  the gate (returns 60, the correct sum). Confirmed via stash-toggle (gate OFF ->
  NRE returns; gate ON -> PASS).
- **JIT-dump discriminator check (task 1.2):** temp `Console.WriteLine` in the
  type-spec `case Ldflda:` arm confirmed — for the F-10-R1 probe's two
  `ldflda this.field` sites: `Operand4_entry=2` (body stamped F-10) ->
  `Operand4_after=1` (F-10 cleared, F-6 only); for the F-6-only probe's
  `ldflda this.id` site (`NeoStep17LdfldaStruct`): `Operand4_entry=0` ->
  `Operand4_after=1` (F-10 was never stamped; the clear is a no-op; F-6
  untouched). Temp probe removed; clean build confirmed.
- **Full NeoStep smoke:** **204/204, 0 fail** (198 baseline + 6 new keepers).
- **NeoClrStructField (heap-IL F-10 path):** 8/8 green (F-10 stays for heap
  sources — byte-identical).
- **NeoStep20:** 9/9 green (F-10 is load-bearing for Step 20 sync).
- **NeoOptHardTest:** 24/24 green.
- **F-6-only probes (the DISPROVEN-reorder guard):** `NeoStep17_LdfldaInline_*`
  8/8 green + `NeoStep17_TC6_RefInFrameVtField` 1/1 green. The gate touches only
  F-10, not F-6.
- **Stash-toggle (task 3.7):** gate OFF -> F-10-R1 reproducer NREs again; F-6-only
  8/8 + NeoClrStructField 8/8 UNAFFECTED. Gate re-applied; reproducer PASS again.
- **Legacy-neutral (task 3.6):** plain `Debug` CLI + `useRegister=true`,
  NeoStep17 filter 47/47 green (incl. the 6 new keepers). The gate is compiled
  out under `!ENABLE_NEO_MODE`; Legacy is byte-identical.

## Regression risk: LOW (confirmed).

Single Neo-only JIT-pass line. The gate clears F-10 ONLY when F-6 stamps (an
in-frame-VT source) — a shape that was BROKEN before (the F-10-R1 NRE). The
existing heap-IL F-10 path never stamps F-6, so F-10 stays — byte-identical.

## STOP discipline — no blocker hit.

The gate did NOT regress any of the 6 F-6-only probes or the 8 NeoClrStructField
probes (the STOP condition). The discriminator is the OPERAND's value-category
(the F-6 condition), confirmed via JIT dump. No runtime reorder performed
(disproven). `Conv_U` NOT implemented (out of scope).
