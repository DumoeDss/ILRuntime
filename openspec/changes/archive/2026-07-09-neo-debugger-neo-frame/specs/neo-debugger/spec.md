# neo-debugger

> Variable inspection of a Neo execution frame. PURE ASCII; SHALL-first. Legacy
> is the REFERENCE. Neo arms live under `#if ENABLE_NEO_MODE`; Legacy behavior
> is unchanged.

## ADDED Requirements

### Requirement: Neo frame local variable inspection

The debugger SHALL recover the value and type of each local variable in the
top Neo execution frame for `DebugService.GetLocalVariableInfo`.

Under `ENABLE_NEO_MODE`, when the top stack frame is a Neo frame
(`StackFrame.IsRegister == true` AND `Method.CompiledFrame.NeoExecuteBody !=
null`), the inspection SHALL read each local's slot from the compact frame
(`byte* frameBase` aliased by `StackFrame.LocalVarPointer`) using the local's
`StackSlotInfo` (`CompiledFrame.LocalInfos[ParameterCount + i]`) and the local's
declared `IType` (resolved from `Definition.Body.Variables[i]` via
`AppDomain.GetType`, including generic-parameter handling).

#### Scenario: primitive local

- WHEN a Neo frame has a primitive-typed local (e.g. `int`, `long`, `float`)
  holding a known value
- THEN `GetLocalVariableInfo` SHALL return that local's value boxed at its
  correct primitive width (read from `frameBase + slot.Offset` by the
  `AppDomain` primitive-singleton width)
- AND the rendered string SHALL contain the value (format parity with the
  Legacy arm).

#### Scenario: reference local

- WHEN a Neo frame has a reference-typed local (e.g. `string`, an IL type, a
  CLR reference type) holding a non-null object
- THEN `GetLocalVariableInfo` SHALL return the object at the `mStack` index
  stored in the slot word (`mStack[idx]`, where `idx` is the absolute index;
  `idx < 0` SHALL render as "null").
- AND the rendered string SHALL contain the object's string form.

#### Scenario: CLR value-type local (F-MAJ-1 flat bytes)

- WHEN a Neo frame has a CLR value-type local stored as flat managed bytes
- THEN `GetLocalVariableInfo` SHALL box the struct via
  `ILIntepreter.ReadNeoValueType(localType.TypeForCLR, frameBase + slot.Offset,
  slot.Size)` and render it.

#### Scenario: IL value-type local (SEQUENCE -- placeholder)

- WHEN a Neo frame has an IL value-type local (spans the primitive + reference
  sub-regions)
- THEN `GetLocalVariableInfo` SHALL render a clear placeholder string (NOT a
  wrong value, NOT "not supported yet" for the whole frame) for THAT local only,
  and SHALL continue inspecting the remaining locals.
- (Full IL-value-type-local reconstruction is a follow-on; out of scope here.)

#### Scenario: resilience

- WHEN reading a single local throws
- THEN `GetLocalVariableInfo` SHALL skip that local and continue (mirrors the
  Legacy arm's per-iteration try/catch), so one bad local does not abort the
  whole inspection.

### Requirement: Neo frame `this` inspection

The debugger SHALL recover the `this` instance and its IL-declared fields in
the top Neo execution frame for `DebugService.GetThisInfo`.

Under `ENABLE_NEO_MODE`, when the top stack frame is a Neo frame and the method
`HasThis`, the inspection SHALL recover the `this` from the slot-0 reference
(`CompiledFrame.ParamInfos[0]`): read the `mStack` index at
`frameBase + slot.Offset`, take `mStack[idx]`, and unwrap an `ILTypeInstance`
directly or via `CrossBindingAdaptorType.ILInstance`.

#### Scenario: IL instance `this`

- WHEN the `this` is an `ILTypeInstance` (directly or via the adaptor bridge)
- THEN `GetThisInfo` SHALL enumerate the IL type's non-static fields and render
  each field's value via the F-4 `ILTypeInstance.this[index]` indexer (the
  per-shape Neo field dispatch).
- AND the rendered string SHALL match the Legacy arm's format
  ("{FieldType} {Name} = {Value}").

#### Scenario: null or non-instance `this`

- WHEN the slot-0 `mStack` index is the null sentinel (`< 0`) or the recovered
  object is not an `ILTypeInstance`/adaptor
- THEN `GetThisInfo` SHALL render "null".

#### Scenario: IL-value-type field resilience

- WHEN an IL field is an IL value type (the F-4 indexer throws a tagged
  `NotImplementedException`)
- THEN `GetThisInfo` SHALL skip that field and continue (the existing
  per-field try/catch), so a single IL-VT field does not abort the inspection.

### Requirement: Legacy-neutrality

The Neo inspection arms SHALL be entirely contained within
`#if ENABLE_NEO_MODE`. The Legacy `StackObject*`-based arms of
`GetThisInfo` and `GetLocalVariableInfo` SHALL be byte-identical to their
pre-change form (no behavioral change when `ENABLE_NEO_MODE` is undefined).

#### Scenario: Legacy build unchanged

- WHEN `ENABLE_NEO_MODE` is undefined
- THEN the Legacy arms execute exactly as before (no new code paths, no changed
  offsets/widths, no changed formatting).

### Requirement: single source of truth for slot shape

The Neo inspection dispatch SHALL key the local's shape (primitive / reference /
CLR-struct / IL-VT) on the SAME `IType` + `StackSlotInfo` the JIT/storage
allocator use (`AppDomain.GetType(VariableType, ...)` + the `StackSlotInfo`
populated by `AllocateLocalStackSpaces`). The inspection SHALL NOT re-derive
the slot layout by any independent heuristic.

#### Scenario: shape agrees with storage

- WHEN a local is stored as a primitive / reference / flat-CLR-struct per the
  allocator
- THEN the inspection's shape test SHALL classify it identically (so the read
  width and the mStack-vs-primitive discrimination are correct by construction).
