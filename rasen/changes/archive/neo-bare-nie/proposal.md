## Why

The full Neo smoke prints `"The method or operation is not implemented."` — the
DEFAULT message of a bare `throw new NotImplementedException()` (no string arg) —
~38 times pre-crash. A bare NIE is a diagnostic hazard: it gives no indication of
WHICH site fired or what was missing, unlike the Step-tagged NIEs in
`ILIntepreter.Neo.cs` (which all carry a `"Step N"` message). This change locates
the Neo-reachable bare-NIE sites, and for each implements the real behavior,
guards it with a precise tagged message, or correctly defers it. The dominant
site is a real gap (whole-method JIT failure), not just a diagnostic problem.

## What Changes

- **IMPLEMENT** the Neo `AppDomain.GetPrimitiveSize(IType)` residual throw
  (`AppDomain.cs:2305`, Neo-gated). It currently handles only the primitive
  singletons and throws bare for anything else, but two categories of non-
  primitive are routed into it: **enum-typed call parameters** (the Neo JIT
  back-half's `AllocateNeoCallParamSlot` calls it for any `IsEnum` type) and
  **CLR value types** (the `ExecuteNeo` `Stobj`/`Ldobj` arms call it when the
  token type is a CLR struct, `ilType == null`). Broaden the helper to size
  enums and CLR value types via `Optimizer.GetNeoValueTypeManagedSize(TypeForCLR)`
  (which already maps an enum to its underlying primitive and uses
  `Unsafe.SizeOf` for structs), and TAG the residual throw. This unblocks
  ~14 enum-param method compilations (e.g. `DelegateTest36-40` passing
  `BindingFlags`) plus CLR-struct `stobj`/`ldobj`.
- **GUARD** the remaining Neo-reachable bare-NIE `else` branches with a precise
  tagged message (fail-loud, parity with the Step-tagged NIEs):
  - `JITCompiler.GetLdfldCodeForType` / `GetStfldCodeForType` field-type-splitter
    residuals (`JITCompiler.cs:3013` / `3098`, Neo-gated) — an IL field whose
    type has no typed opcode.
  - `AppDomain` shared type/method-resolution `else` branches (`AppDomain.cs:1717`
    unhandled token shape; `AppDomain.cs:2162` unhandled method-reference shape)
    and `JITCompiler.cs:2915` (token-resolution else).
- **DEFER** (follow-up note) the framework-thrown NIE surfaced through the
  autogen `System_Enum_Binding.GetValues_0_Neo` redirect (`System_Enum_Binding.cs:77`):
  `System.Type.GetEnumValues()` throws for an IL enum surfaced as `System.Type`.
  This is NOT an ILRuntime bare throw (the NIE originates in the framework), and
  fixing it is a binding/IL-enum-Type-representation investigation (sibling to
  the autogen-binding follow-ups). Recorded as a surfaced follow-up.
- **Add NeoStep probe(s)** for the enum-param and CLR-struct-`stobj`/`ldobj`
  real implementations. Each probe FAULTS without the fix (the method fails to
  JIT / the opcode throws) and passes after — per the child-1/child-2 probe
  discipline.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-value-types`: ADD a requirement that the Neo primitive-size resolution
  (`GetPrimitiveSize`, used by the JIT call-param-slot allocator and the
  `Stobj`/`Ldobj` runtime arms) SHALL size enums and CLR value types (never a
  bare throw), and that the Neo-reachable resolution/splitter `else` branches
  SHALL carry a tagged message (never the default-message NIE).

## Impact

- **Code**: `ILRuntime/Runtime/Enviorment/AppDomain.cs` (GetPrimitiveSize +
  2 resolution guards), `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
  (3 splitter/resolution guards). No JIT emission change; no object-model change.
- **Probes**: `TestCases/NeoStep*Test.cs` (enum-param call + CLR-struct
  stobj/ldobj).
- **Legacy**: Neutral by construction — `GetPrimitiveSize` and the Neo field
  splitters are `#if ENABLE_NEO_MODE`; the AppDomain/JIT resolution guards are
  shared but additive (a tagged message where a bare throw was; behavior for the
  happy path is byte-identical).
- **Smoke**: NeoStep 316/0 must hold; the full (unfiltered) Neo smoke loses the
  ~16 `GetPrimitiveSize` bare-NIE hits (enum-param methods now compile) and the
  residual throws become tagged.
