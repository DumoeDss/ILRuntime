## Why

The Neo raw `Stfld`/`Ldfld` handlers (added by child-4 `neo-raw-stfld-ldfld`) cover three
owner shapes for a field declared on a CLR type: a boxed CLR reference-type object, a CLR
value-type local read by value, and a CLR value-type local written via its address. They
intentionally DEFER a fourth shape with a Step-tagged `NotImplementedException`: an
**IL-instance owner whose field resolves to a CLR base type's FieldInfo** (an IL type that
inherits a CLR base via a `CrossBindingAdaptor`, accessing an instance field declared on
that CLR base). This fires ~5 times in the full Neo smoke (e.g. `TestCls : ClassInheritanceTest`
reading/writing the CLR-base `testVal`/`TestVal2`). Legacy handles this case
(`ILIntepreter.Register.cs` raw `Stfld`/`Ldfld` route an `ILTypeInstance` owner to
`AssignFromStack`/`CopyToRegister`, which in turn delegate to the instance's `clrInstance`).
This change closes that Neo parity gap.

## What Changes

- Replace the tagged NIE at `ILIntepreter.Neo.cs:3750` (raw `Ldfld`, IL-instance owner +
  CLR-base field) with a read: unwrap the `ILTypeInstance` (or `CrossBindingAdaptorType` ->
  `.ILInstance`) and read the field off its `CLRInstance` (the wrapped CLR object created by
  `CrossBindingAdaptor.CreateCLRInstance`) via the existing field-hash accessor.
- Replace the tagged NIE at `ILIntepreter.Neo.cs:3913` (raw `Stfld`, same hybrid owner) with
  a write through the same `CLRInstance` target.
- Reuse the existing `NeoReadClrObjectField`/`NeoWriteClrObjectField` helpers (or the
  already-decoded declaring `CLRType.GetFieldValue`/`SetFieldValue`) -- no new helper, no JIT
  change, no offset-lowering change (the raw opcodes are already lowered by child-4).
- Add a `NeoStep` probe: an IL type deriving from a CLR base (`ClassInheritanceTest`),
  reading and writing the base's instance field, asserting a round-trip. The probe FAULTs on
  the current tagged NIE and asserts the value so a wrong-value bug also fails.
- All edits are under `#if ENABLE_NEO_MODE`, so Legacy is untouched (Legacy-neutral by
  construction).

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-value-types`: Extends the child-4 raw `Stfld`/`Ldfld` requirement (CLR-declaring-type
  instance field access) to additionally cover the **IL-instance owner whose field is declared
  on a CLR base type** -- routing the read/write through the IL instance's wrapped `CLRInstance`
  rather than deferring it with a tagged NIE.

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- two NIE sites in the raw
  `Ldfld` and `Stfld` handlers (the `target is ILTypeInstance || CrossBindingAdaptorType`
  branch of the CLR reference-type owner `else`). Neo-gated.
- `TestCases/NeoStepIlClrBaseFieldTest.cs` -- new probe (1-2 `public static void` NeoStep
  methods + an IL holder type deriving from the CLR `ClassInheritanceTest`).
- No JIT / optimizer / object-model changes. No public API changes. No new CLR bindings.
- Expected: NeoStep smoke 326 -> 327/328 (new probes); full Neo smoke loses ~5 tagged-NIE
  occurrences for this shape.
