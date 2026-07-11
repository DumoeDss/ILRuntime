## Why

`Stsfld`/`Ldsfld` on a **CLR** static field throw under `ExecuteNeo`:
`"Neo Stsfld/Ldsfld: CLR static field not implemented (Step 25 S3-4; the capstone
is IL-only)"` (`ILIntepreter.Neo.cs:3867` Stsfld + `:3910` Ldsfld `else` branches).
This is the single most frequent remaining Neo gap after children 1-2: **38
pre-crash hits** in the full (filter-less) Neo smoke (the post-child-2 RE-SCOPE
table). The Step-25 S3-4 capstone implemented only the **IL**-static arms; any IL
that reads or writes a CLR type's static field -- `System.String.Empty`,
`System.IntPtr.Zero`, host-provided CLR helper statics, reflection/attribute
patterns -- throws under Neo while Legacy `ExecuteR` runs them green. This is the
next serial child of the `neo-overhaul` portfolio.

## What Changes

- **Add the CLR-declaring-type branch to the `Stsfld` and `Ldsfld` arms** in
  `ExecuteNeo` (`ILIntepreter.Neo.cs`). The current `else` throws; replace each
  with the CLR-static handling. The operand encoding is IDENTICAL to the IL-static
  path (shared `GetStaticFieldIndex`: `(typeHash << 32) | fieldHash` in
  `ip->OperandLong`; declaring type resolved via
  `AppDomain.GetType((int)(ip->OperandLong >> 32))`, field via
  `(int)ip->OperandLong`). So the new branch sits in the same `else` and reuses
  the same source/dest slot convention (`byte* slot = frameBase + ip->DstOffset`;
  Stsfld/Ldsfld set ONLY `Register1` = the `DstOffset` alias -- no separate
  `dstRefOffset` operand), matching the IL-static arm exactly.
  - **Resolve the CLR static field** via `CLRType`: `var f = ct.GetField(sIdx);`
    (`CLRType.GetField(hash)` -> `System.Reflection.FieldInfo`, walking `BaseType`
    and consulting the patch getter/setter caches). This is the SAME mechanism
    Legacy `ExecuteR` uses (`ILIntepreter.Register.cs:3303/3332`).
  - **Stsfld (write):** read the source frame slot as a CLR `object` by the
    field's `f.FieldType` category, then `ct.SetStaticFieldValue(sIdx, obj)`
    (`FieldInfo.SetValue(null, obj)`).
  - **Ldsfld (read):** `obj = ct.GetFieldValue(sIdx, null)`
    (`FieldInfo.GetValue(null)`); unwrap a `CrossBindingAdaptorType` to its
    `ILInstance` (Legacy parity, `:3334-3335`); push `obj` into the dest frame
    slot by category.
  - **Per-category marshalling** mirrors the IL-static ladder AND the existing
    CLR Box/Unbox arms (Step 13), reusing the existing Neo helpers -- NO new
    marshalling code:
    - **CLR primitive** (`ft.IsPrimitive`): Stsfld boxes via
      `NeoBoxPrimitiveByType(ft, srcSlot)` (already-present helper taking a
      `System.Type`, `:5950`); Ldsfld writes via
      `NeoWritePrimitiveToFrame(obj, dstSlot)` (`:5986`).
    - **CLR value type** (struct/enum, `ft.IsValueType && !ft.IsPrimitive`):
      Stsfld reads flat bytes via `ReadNeoValueType(ft, frameBase, ref off,
      Optimizer.GetNeoValueTypeManagedSize(ft))` (`:227`); Ldsfld writes via
      `WriteNeoValueType(obj, dstSlot, managedSize)` (`:243`). (A CLR VT *with
      reference fields and no ValueTypeBinder* still hits the Step-13b NIE -- a
      separately-tracked sibling gap, `neo-clr-vt-reffields-binder`; simple
      structs/enums like `IntPtr` are handled now.)
    - **CLR reference type** (`!ft.IsValueType`): Stsfld reads
      `int idx = *(int*)srcSlot; obj = idx >= 0 ? mStack[idx] : null`; Ldsfld
      pushes `mStack.Add(obj); *(int*)dstSlot = mStack.Count - 1;` -- the SAME
      `mStack.Add` temp-ref convention the IL-static Ldsfld ref branch uses.
- **No JIT change.** `JITCompiler.cs` emits `Stsfld`/`Ldsfld` identically for IL
  and CLR declaring types (`op.Register1` + `op.OperandLong =
  GetStaticFieldIndex(...)`); the encoding already carries the CLR type hash.
- **`NeoStep` regression probes** in `TestCases/`:
  - a CLR static `int` field **round-trip** (write a known value, read back,
    assert equality -- faults on the current NIE AND on a wrong value), using a
    public static field on a host CLR helper class;
  - a `System.String.Empty` read (CLR static reference field) asserting empty;
  - a `System.IntPtr.Zero` read (CLR static value-type field) asserting zero.

All changes are inside the `#if ENABLE_NEO_MODE` file `ILIntepreter.Neo.cs` (plus
the Neo-only probe in `TestCases/` and, for the round-trip probe, a public static
field on a host CLR helper in `ILRuntimeTestBase`), so Legacy `ExecuteR` is
byte-identical -- Legacy-neutral by construction.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-optimizer`: Adds a new requirement that the `Stsfld`/`Ldsfld` `ExecuteNeo`
  arms handle a **CLR** declaring type (the Step-25 S3-4 capstone specced only the
  IL-static path + `.cctor` seeding). The new requirement covers resolving the CLR
  static via `CLRType.GetField`/`GetFieldValue`/`SetStaticFieldValue` and the
  primitive / CLR-value-type / reference category dispatch, with scenarios for a
  primitive round-trip, a CLR reference static read (`string.Empty`), and a CLR
  value-type static read (`IntPtr.Zero`).

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- replace the two
  `else throw new NotImplementedException("Neo Stsfld/Ldsfld: CLR static field not
  implemented ...")` branches (Stsfld `:3867`, Ldsfld `:3910`) with the CLR-static
  handling. No other opcode touched.
- `TestCases/NeoStepClrStaticFieldTest.cs` -- new NeoStep probe(s) (public static
  no-arg methods, `[ILRuntimeTest]`, mirroring existing `NeoStep*Test.cs`).
- `ILRuntimeTestBase/...` -- add a `public static int` field (e.g. on the existing
  `TestClass3` CLR helper) so the Stsfld+Ldsfld primitive round-trip probe has a
  writable CLR static. (Minimal: one field; rebuild ILRuntimeTestBase.)
- No JIT change (emission already correct + encoding shared). No public ILRuntime
  API changes. No dependency changes. Legacy (`ExecuteR`) untouched.
- Build/test surface: `Debug_Neo` CLI build + `NeoStep` smoke (baseline **311/0**
  after child 2); the full (filter-less) smoke should lose the ~38
  CLR-static-field NIE hits.
