## Why

The Neo register VM (`ExecuteNeo`) does not yet implement the `isinst`
(`is`/`as`) and `castclass` (cast) opcodes. The JIT already lowers both CIL
opcodes (`JITCompiler.cs:2218-2223`), but `ExecuteNeo` has no `Isinst` /
`Castclass` arms, so every `is`, `as`, and explicit cast falls through the
switch `default` and throws the Step-6 placeholder `NotImplementedException`
(`ILIntepreter.Neo.cs:2093-2094`). This is a large functional gap: type checks
are pervasive, and Step 14's catch-body test deliberately asserted only
`e != null` (not `e is T`) precisely because `isinst` was unimplemented. With
Steps 11 (interface dispatch / `Implements`) and 13 (Box) landed, the
prerequisites for type checks now exist.

## What Changes

- Add an `OpCodeREnum.Isinst` arm to `ExecuteNeo` that reads the source object
  from its register-2 ref slot, resolves the type token via `AppDomain.GetType`,
  and writes the result back into the same register-1 ref slot:
  - `null` source -> result is null (ref index `-1`).
  - `ILTypeInstance` source -> `ILTypeInstance.CanAssignTo(type)` decides keep
    (returns the original reference) vs null.
  - CLR object source -> `type.TypeForCLR.IsAssignableFrom(obj.GetType())`
    decides keep vs null.
  - On keep, the original reference (mStack entry) is copied into the destination
    ref slot unchanged -- there is no in-frame flat-byte path; the checked
    object is already a reference.
- Add an `OpCodeREnum.Castclass` arm with identical dispatch, except a failed
  check throws `System.InvalidCastException` (not null). `null` source passes
  through as null.
- Extend the Neo offset-lowering pass (`Optimizer.Neo.cs` `LowerNeoOffsets`) to
  stamp `DstOffset` / `SrcOffset` / `Operand3` (dst ref offset) / `Operand4`
  (src ref offset) for `Isinst` and `Castclass`, reusing the exact shape already
  used for `Box`/`Unbox`/`Unbox_Any` (lines 430-444). Because the JIT sets
  `Register1 == Register2` for these opcodes, the result is in-place.
- Add `TestCases/NeoStep15Test.cs` covering `obj is T` true/false, `obj as T`
  (null on mismatch), boxed value-type isinst, and inheritance-chain checks; a
  castclass *success* round-trip (a throw-asserting test is not expressible
  green in the harness).

Non-goals (deferred): the compile-time `box T; isinst U` peephole and the
generic-parameter patch table -- see "Impact / peephole finding".

## Capabilities

### New Capabilities
- `neo-type-checks`: Runtime type-check instructions (`isinst` / `castclass`)
  for IL code executed by the Neo register VM, including null handling,
  IL-vs-CLR assignability, interface/inheritance-chain matching, and the
  `InvalidCastException` contract for failed `castclass`.

### Modified Capabilities
<!-- None. The type-check opcodes are net-new behavior under Neo; no existing
     spec's requirements change. neo-exceptions catch dispatch is unchanged
     (see proposal "Catch-matching relationship"). -->

## Impact

- Code: `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (two new
  `case` arms), `Optimizer.Neo.cs` (add `Isinst`/`Castclass` to the existing
  Box/Unbox ref-slot-stamping case). No Legacy (`ExecuteR`) changes.
- Peephole finding: the `box T; isinst U` compile-time peephole and the
  `PatchKind.IsinstResult` generic-parameter patch table described in
  `.trae/documents/neo-implementation-steps.md` Step 15 are **not present in
  this codebase**. `PatchKind` does not exist anywhere under `ILRuntime/` (only
  `HybridPatch/AssemblyPatch.cs`, unrelated). The Neo optimizer has no peephole
  pass that fuses adjacent ops; existing passes are BCP/FCP/ELDC/RegisterCleanup/
  InlineMethod/RegisterNeoOffsets. The JIT already resolves the isinst type token
  statically via `method.GetTypeTokenHashCode`, so for ordinary (non-generic-param)
  type operands the runtime `AppDomain.GetType(ip->Operand)` lookup already yields
  the concrete type and no patch table is needed for Step 15's validated scope.
  The peephole and patch table are therefore out of scope and tracked as a
  follow-up; the runtime arms fully cover the validated cases.
- Catch-matching relationship: Neo catch dispatch does NOT use the `isinst`
  opcode. It routes through the shared engine `GetCorrespondingExceptionHandler`
  -> `CheckExceptionType` (`ILIntepreter.cs:5609,5823`), which tests
  `catchType.TypeForCLR.IsAssignableFrom(exception.GetType())` directly. So
  Step 15's benefit to `catch` is purely indirect: it enables `is`/`as`/cast
  *inside* catch bodies (e.g. `catch (Exception e) { if (e is Foo) ... }`), not
  the catch-type selection itself. (Note: `CheckExceptionType` still throws NIE
  for an IL-typed catch type -- a separate concern, not in Step 15's scope.)
- Regression risk: `is`/`as`/casts are pervasive; a wrong result silently
  corrupts control flow. Gate = full NeoStep smoke must stay green (was 58/58
  pre-Step-15; Step 15 cases only add).
