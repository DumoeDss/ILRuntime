## Why

A **CLR-struct field of an IL instance** (e.g. an async state machine's
`<>t__builder` = `AsyncTaskMethodBuilder`, `<>u__1` = `TaskAwaiter`, or any IL
class declaring a CLR-struct-typed field) is laid out by the ILType field-layout
pass (`ILType.cs:2129-2157`, the `else` branch at line 2146) as a **reference
slot** (`referenceOffset++`, NO `primitiveOffset` advance). Its bytes do NOT live
in `ILTypeInstance.Primitives` — the boxed struct lives at
`ManagedObjects[ReferenceOffset]`. But the JIT `ldflda &instance.<clrStructField>`
emits a byref `(smMStackIdx, field.PrimitiveOffset)`, a stale offset pointing
PAST the `Primitives` array. The byref consumers
(`CopyNeoCallArguments` → `NeoMarshalByrefFieldToSlot`) read
`ili.Primitives[off]` → `IndexOutOfRangeException` on the non-generic-Task async
SM (and silent corruption anywhere else this shape appears). The byref carries
ONE offset, so it is **unrecoverable** to the field's actual storage.

This blocks the rest of Step 20 async (every non-`Task<int>` sync shape —
non-generic `Task`, `ValueTask`, multi-await, exception, async void — plus the
suspend slice, where the awaiter field `<>u__1` is the same shape) and ANY IL
class with a CLR-struct field accessed via `ldflda`/byref. Same family as F-2 /
F-3 / NEO-BYREF-THIS. It is the **load-bearing primitive** for the rest of
Step 20. Smoke is Neo 186/186 today (only `Task<int>` sync passes — by a layout
accident).

## What Changes

- **JIT `ldflda` of a CLR-struct field of an IL instance SHALL emit a
  recoverable byref encoding** — a sentinel `objectIndex` discriminator (a
  reserved negative value distinct from `-1` frame-native and `>= 0` mStack-
  object) paired with the field's **`ReferenceOffset`** (carried in the offset
  half), so the runtime can distinguish "this offset is a `ManagedObjects` ref-
  slot index for a boxed CLR-struct field of an IL instance" from the existing
  meanings (`-1` frame-native absolute byte offset; `>= 0` `mStack[objIdx]` =
  ILTypeInstance reading `Primitives[off]`; CLR-object `fieldHash`). Neo-only
  stamp in the `TypeSpecializeNeoOpcodes` `case Ldflda:` pass (the existing
  dest-type-seed site), gated on "source is a heap IL reference type AND the
  addressed field is a CLR-struct-typed field" — verified at apply via JIT dump.
  The existing heap-IL / CLR-object / in-frame-VT / frame-native `ldflda` paths
  stay byte-identical (the new encoding fires ONLY for the CLR-struct-field-of-
  IL-instance shape).
- **The byref consumers SHALL handle the new encoding.** `NeoMarshalByrefFieldToSlot`
  (read/write a referent through a byref to an mStack-object field) gains a
  branch keyed on the sentinel `objIdx` + the ILTypeInstance target: read/write
  the boxed CLR struct at `ili.ManagedObjects[refOffset]` and marshal it to/from
  the dest slot via the existing `ReadNeoValueType`/`WriteNeoValueType` helpers
  (the Step-13b/area4 machinery, byte-consistent with the boxed-ref-vs-flat-
  bytes bridging already shipped). The existing ILTypeInstance-`Primitives` and
  CLR-object-fieldHash branches stay byte-identical for non-sentinel `objIdx`.
- **NO layout change** (`ILType.cs:2129-2157` UNCHANGED). The CLR-struct field
  remains a reference slot; the boxed struct lives at
  `ManagedObjects[ReferenceOffset]`. The existing `Stfld_Ref`/`Ldfld_Ref` heap
  arms (which use `Operand3 = ReferenceOffset` and read/write
  `ins.ManagedObjects[ReferenceOffset]`) ALREADY handle a CLR-struct field
  correctly — they treat the boxed struct as a reference and round-trip it. So
  `stfld`/`ldfld` of a CLR-struct field WORKS today; only `ldflda` (and reading
  through its byref) was broken. **The fix is localized to the `ldflda`
  lowering + the byref consumers for the CLR-struct-field-of-IL case.**
- **Option (B) layout-change is REJECTED at propose** (it would advance
  `primitiveOffset` by the struct's managed size, storing the struct's flat
  bytes in `Primitives`, but requires mirroring through EVERY stfld/ldfld/by-
  value-param/Move_Vt consumer of that field — high blast radius, touches the
  shared field-layout engine). Option (A) JIT-byref-encoding is the lower-risk
  fix (localized to the `ldflda` lowering + the byref-consumers for this one
  field shape; the existing layout + the IL-primitive-field + CLR-object-field
  + in-frame-VT paths stay byte-identical). The dump-confirmed blast-radius
  assessment (stfld/ldfld already correct; only ldflda broken) is the load-
  bearing evidence.
- **Adversarial probes MANDATORY** (Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1 / F-6
  lessons: green smoke does NOT prove this gate correct — this is the silent-
  corruption / OOB class). Required probes: (1) the minimal F-10 reproducer (IL
  class + CLR-struct field + `ldflda` + byref use); (2) an IL class with
  MULTIPLE CLR-struct fields; (3) an IL class with a CLR-struct field that has
  a ref-type field (the `TaskAwaiter`-with-`Task` shape — the async blocker);
  (4) `stfld`/`ldfld` of a CLR-struct field (regression — already works); (5) a
  CLR-struct field passed by value (the byref-then-deref shape); (6) regression
  for IL classes with IL-primitive fields, IL-VT fields, CLR-ref fields, CLR-
  object fields (ALL existing field types byte-identical). Each new probe:
  FAIL-on-HEAD stash-toggle → PASS-after. Plus the full `NeoStep` smoke stays
  green and the Step 20 async TC1/TC7 stay green (re-add the 11 trimmed TC2-TC6/
  TC8 probes when this lands, OR ship a focused subset here and let
  `neo-step20-async` resume re-add the rest — see design).
- **Legacy is the REFERENCE, NOT modified.** The fix is Neo-only: the JIT
  type-spec stamp is `#if ENABLE_NEO_MODE`; the runtime `ldflda` arm +
  `NeoMarshalByrefFieldToSlot` are in `ILIntepreter.Neo.cs` (Neo-only file). The
  field-layout pass IS shared-engine, but option (A) leaves it byte-identical
  (no `ILType.cs` edit) → Legacy-neutral-confirmed. Legacy `ExecuteR`'s
  `Ldflda` (`GetObjectAndResolveReference` + the tagged StackObject model) is
  the semantic reference, NOT a target.

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `neo-value-types`: ADDS a requirement for the recoverable byref encoding of a
  CLR-struct field of an IL instance (the field-layout is a value-type concern;
  the byref carries the field's `ReferenceOffset` to its boxed-struct storage).
  The existing "In-frame value-type storage layout" requirement's field-layout
  (IL-primitive advances `primitiveOffset`; CLR-struct advances
  `referenceOffset` only) is UNCHANGED — this change only adds the byref-
  addressing requirement for the latter shape.

## Impact

- **`ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`** —
  `TypeSpecializeNeoOpcodes` `case Ldflda:` (`:828-846`): when the source
  register is a HEAP IL reference type (`GetRegisterType(...) is ILType &&
  !IsValueType`) AND the addressed field is a CLR-struct type (the field's
  `fieldType` is NOT an ILType and IS a CLR value type), stamp a sentinel
  discriminator on a standalone `Operand` field (collision-free; the existing
  `NeoLdfldaInlineMarker` uses `Operand4` bit `0x1` for the in-frame-VT case,
  and the `Code.Ldflda` Translate stamps `Operand2`=`PrimitiveOffset`,
  `Operand3`=`ReferenceOffset` — verify the chosen encoding field does not
  collide). Also swap the offset the runtime arm reads for this shape from
  `PrimitiveOffset` to `ReferenceOffset` (or carry `ReferenceOffset` in a
  second field and let the runtime arm pick). Neo-only (`#if ENABLE_NEO_MODE`).
- **`ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`** —
  `case OpCodeREnum.Ldflda:` arm (`:1025-1092`): add a 4th dispatch shape keyed
  on the new sentinel — produce `(sentinelObjIdx, fieldRefOffset)`. Existing
  shapes (frame-native `objIdx == -1`; in-frame-VT-flat-bytes via
  `NeoLdfldaInlineMarker`; heap-IL `objIdx >= 0` with `field.PrimitiveOffset`;
  CLR-object `objIdx >= 0` with `fieldHash`) stay byte-identical.
  `NeoMarshalByrefFieldToSlot` (`:376-...`): add a branch keyed on the sentinel
  `objIdx` — read/write `ili.ManagedObjects[refOffset]` (the boxed CLR struct)
  via `ReadNeoValueType`/`WriteNeoValueType`. The `stind_*`/`ldind_*`/`Stobj`/
  `Ldobj` consumer arms that read a byref's `(objIdx, off)` MUST also recognize
  the sentinel (a `stind`/`ldind` through an `ldflda`-produced byref of a CLR-
  struct field of an IL instance is the F-10 shape) — scope at apply via JIT
  dump (probe which `stind`/`ldind` widths the smoke + the new probes exercise;
  NIE-tag any uncovered width with a clear Step-20/F-10 message).
- **NO `ILType.cs` edit** (the layout pass stays byte-identical — option A).
- **NO `Optimizer.Neo.cs` edit** (the `addrAlias`/`liveAliasMap` machinery is
  not perturbed — verify via the register-reuse adversarial probe; the F-6
  marker precedent showed `Operand4`-based markers are invisible to the COEXIST
  gate).
- **`TestCases/NeoStep12Test.cs`** (extend) OR a new
  **`TestCases/NeoClrStructFieldTest.cs`** — the adversarial probe set. Probe
  names match the `NeoStep` smoke filter (e.g. `NeoStep12_ClrStructField_*` or
  `NeoClrStructField_*`); co-locate with the existing value-type field tests.
- **`.trae/documents/neo-deferred-items.md`** — F-10 row/§3 entry → RESOLVED.
- **`openspec/specs/neo-value-types/spec.md`** — delta merged at archive.

**Regression risk: MEDIUM.** The runtime `ldflda` arm + `NeoMarshalByrefFieldToSlot`
are shared by every `ldflda`/byref consumer, but the new branch fires ONLY for
the sentinel discriminator (stamped ONLY for the CLR-struct-field-of-IL-instance
shape); every other shape is byte-identical when the sentinel is absent. Gate:
full `NeoStep` smoke (186/186 baseline) + Legacy-neutral stash-toggle (no
shared-engine edit beyond confirming `ILType.cs` is untouched). Adversarial
probes MANDATORY (the silent-corruption/OOB class). The biggest design risk is
the encoding-field non-collision + whether the `stind`/`ldind` consumers need
the new branch (both DUMP-GATED, STOP if the designed fix is wrong — do NOT ship
a guessed encoding or a guessed consumer-arm set).
