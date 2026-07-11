## Why

Raw `Stfld` (~36 hits) and `Ldfld` (~18 hits) reach the `ExecuteNeo` Step-6
`NotImplementedException` default during the full Neo smoke. The Neo JIT's typed
field-splitter rewrites CIL `Stfld`/`Ldfld` into typed arms only when the field's
declaring type is an ILType; for a **CLR declaring type** it leaves the raw
`OpCodeREnum.Stfld`/`Ldfld` opcode in place, and `ExecuteNeo` has no case for it.
The result: any IL hotfix that reads or writes a field declared on a CLR type (a CLR
class field, a CLR struct field, an inherited CLR-base field, or a CLR array element's
field) throws mid-execution. This is a high-frequency gap on the road to a functional
full-Neo run.

## What Changes

- Add an `ExecuteNeo` runtime handler for the raw `Stfld` and `Ldfld` opcodes (the
  CLR-declaring-type path) so they stop reaching the Step-6 default. The handler decodes
  the existing `OperandLong = (typeHash << 32) | fieldHash` encoding (already emitted by
  the JIT's `else` branch and identical to Legacy's raw `Stfld`/`Ldfld`), resolves the
  owner object from the owner register slot, and reads/writes the CLR field.
- Add the raw `Ldfld` and `Stfld` opcodes to the Neo offset-lowering pass
  (`Optimizer.Neo.cs`) so their `Register1`/`Register2` are lowered to valid
  `DstOffset`/`SrcOffset` (currently the pass only lists the typed arms, so the raw
  opcodes reach runtime with unset offsets even once a handler exists).
- The handler discriminates the owner shape observed at runtime (the Neo object-handle /
  Ref-Slot model): (a) a boxed CLR object in `mStack` (incl. an `ILTypeInstance` whose
  CLR base declares the field, and a CLR array element) — routed through the existing
  Step 13 Area 4d `NeoReadClrObjectField`/`NeoWriteClrObjectField` reflection accessors;
  and (b) a frame-native CLR value-type byref (`objIdx == -1`) — a flat-bytes field
  read/write at the field's offset (the previously-deferred "Step 17/13b field access on
  a CLR object" path).
- Add `NeoStep` regression probes that FAULT (raw opcode → Step-6 NIE) without the fix
  and pass after: a CLR class field round-trip, and a CLR struct field round-trip.
- No JIT splitter change is required — the `else`-branch encoding is already correct
  (confirmed: it matches Legacy byte-for-byte). The fix is ExecuteNeo + the lowering
  pass. **Legacy-neutral** (all changes under `ENABLE_NEO_MODE`).

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-value-types`: Adds a requirement that the Neo interpreter execute raw
  `Stfld`/`Ldfld` whose field is declared on a CLR type (the path the typed-splitter
  does not lower). This is the instance-field sibling of the existing typed
  `Ldfld_*`/`Stfld_*` arms, `ldfld.value`/`stfld.value` (Step 12b), and the Step 13
  Area 4d CLR-object field-hash accessors — the same field-access family already owned
  by this capability.

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — new `case
  OpCodeREnum.Stfld:` / `case OpCodeREnum.Ldfld:` arms (mirrors Legacy
  `ILIntepreter.Register.cs:3093-3196` / `3197-3253`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — add raw `Ldfld`/`Stfld`
  to the offset-lowering case-lists (~line 934 / ~960).
- `TestCases/` — new `NeoStep` probe(s) for CLR instance field access.
- No public API change; no JIT splitter change; no Legacy (`ExecuteR`) change.
  Shared-file risk: the JIT splitter and lowering pass are touched, so the NeoStep smoke
  (314/0/0) must stay green (no regression to the typed arms).
