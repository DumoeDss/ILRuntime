## Why

`ldtoken` is the dominant remaining unimplemented opcode in `ExecuteNeo`. After
child 1 (`neo-jit-bogus-opcode`) eliminated all garbage-opcode hits, the full Neo
smoke's ONLY remaining `default:`-arm entry is `"Neo: opcode Ldtoken not yet
implemented (Step 6)"` (~60 pre-crash hits). `ldtoken` is the producer half of
`typeof(T)` (`ldtoken T; call Type.GetTypeFromHandle`) and of method/field
reflection-handle patterns, so its absence blocks every `typeof(...)`, every
attribute/LINQ-expression-tree ctor that takes a `Type`, and much of reflection --
all of which currently throw under Neo while Legacy `ExecuteR` runs them green. This
is the next serial child of the `neo-overhaul` portfolio.

## What Changes

- **Add the `Ldtoken` arm to `ExecuteNeo`** (`ILIntepreter.Neo.cs`). The arm reads
  the token-kind discriminator `ip->Operand` (set by the JIT: `1` = TypeReference,
  `0` = FieldReference) and the packed token in `ip->OperandLong`:
  - **Type path (`Operand == 1`, the dominant `typeof(T)` case):** resolve
    `IType type = AppDomain.GetType((int)ip->OperandLong)` and push
    `type.ReflectionType` as a Neo object-reference slot (`mStack[dstIdx]` + write
    the ref index into the dest primitive slot). This mirrors Legacy `ExecuteR`
    (`AssignToRegister(..., type.ReflectionType)`) exactly. `Type.GetTypeFromHandle`
    is already a registered no-op CLR redirect (`CLRRedirections.GetTypeFromHandle`
    returns `esp` unchanged -- "Nothing to do"), so once `ldtoken` pushes the Type
    the value passes straight through to the consumer. No real `RuntimeTypeHandle`
    struct is ever materialised -- Legacy never materialises one either.
  - **Field path (`Operand == 0`):** mirror Legacy's IL-static-field read (Legacy
    reads the field VALUE via `StaticInstance.CopyToRegister`, not a handle). The
    `OperandLong` encoding is identical to `Ldsfld` (declaring-type token in the
    high 32 bits, static-field index in the low 32 bits), so the arm reuses the
    `Ldsfld` arm's per-category read body (primitive / inline-VT / reference). A CLR
    declaring type throws a tagged `NotImplementedException` (parity with Legacy's
    own NIE on the CLR branch).
- **Add a `Ldtoken` case to `LowerNeoOffsets`** (`Optimizer.Neo.cs`). The object-push
  idiom (used by `Ldstr`/`Ldftn`) stamps the dest ref-slot index into `op.Operand`,
  but `ldtoken`'s `Operand` is already the 0/1 discriminator. The lowering therefore
  stamps the dest ref-slot index into the SPARE `op.Operand3` (mirroring the
  `Isinst`/`Castclass`/`Initobj` convention of using `Operand3` for a dest ref slot)
  and then calls `LowerR1` to set `DstOffset` from `Register1`. Without this case
  the opcode hits the lowering `default:` (`handled = false`), leaving
  `DstOffset`/ref-slot at their zero defaults, so the ExecuteNeo arm would read/write
  the wrong slot.
- **No JIT change.** `JITCompiler.cs` already emits `Ldtoken` correctly
  (`op.Register1 = baseRegIdx++; op.Operand = <0|1>; op.OperandLong = <token>`).
- **`NeoStep` regression probes** in `TestCases/`: `typeof(int)` / `typeof(string)`
  / `typeof(ILType)` whose assertions depend on `ldtoken` executing (the value is
  consumed), plus a reflection-`MethodInfo`/`FieldInfo`-via-handle probe if the
  field/method path is reachable from IL.

All changes are inside `#if ENABLE_NEO_MODE` files (`ILIntepreter.Neo.cs`,
`Optimizer.Neo.cs`), so Legacy `ExecuteR` is byte-identical -- Legacy-neutral by
construction.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `neo-type-checks`: Adds a new requirement that the `ldtoken` opcode executes under
  Neo. `ldtoken` is the producer side of the type-introspection family that
  `isinst`/`castclass` (already specced here) belong to: it resolves a type token
  and pushes the `System.Type`, which `Type.GetTypeFromHandle` (a no-op redirect)
  passes through. Also adds the matching `LowerNeoOffsets` ref-slot-stamping
  requirement (same shape as the existing isinst/castclass lowering requirement).

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- new
  `case OpCodeREnum.Ldtoken:` arm in the `ExecuteNeo` switch (placed near the
  `Ldsfld`/`Ldstr` neighbours, ~line 1484/3783).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- new
  `case OpCodeREnum.Ldtoken:` in the `LowerNeoOffsets` switch (near the
  `Ldstr`/`Ldftn` neighbours, ~line 822/847).
- `TestCases/` -- new `NeoStep*Test.cs` probe(s).
- No JIT change (`JITCompiler.cs` emission already correct). No public API changes.
  No dependency changes. Legacy (`ExecuteR`) untouched.
- Build/test surface: `Debug_Neo` CLI build + `NeoStep` smoke (baseline **306/0**
  after child 1); the full (filter-less) smoke should lose its only remaining
  default-arm entry (`Ldtoken`).
