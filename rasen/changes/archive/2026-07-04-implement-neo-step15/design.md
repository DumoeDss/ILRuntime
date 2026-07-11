# Design - implement-neo-step15 (isinst / castclass under Neo)

## Context / current state (verified)

- **JIT lowering exists.** `JITCompiler.cs:2215-2223` lowers CIL `Box`, `Unbox`,
  `Unbox_Any`, `Isinst`, `Castclass` identically:
  `op.Register1 = op.Register2 = (short)(baseRegIdx - 1); op.Operand = method.GetTypeTokenHashCode(token);`
  So both opcodes are register-in-place (R1==R2) with a type-token `Operand`.
- **ExecuteNeo has NO arms** for `Isinst`/`Castclass`. They fall through the
  switch `default` at `ILIntepreter.Neo.cs:2093-2094`, throwing
  `NotImplementedException("... (Step 6)")`. This is the gap Step 15 closes.
- **Offset-lowering omits them.** `Optimizer.Neo.cs:430-444` stamps
  `DstOffset`/`SrcOffset`/`Operand3`(dst ref)/`Operand4`(src ref) for `Box`/
  `Unbox`/`Unbox_Any` but NOT for `Isinst`/`Castclass`. They must be added so
  the ExecuteNeo arm can read the source ref slot and write the result.
- **Legacy is the spec.** `ILIntepreter.Register.cs` `ExecuteR`:
  - `Castclass` (lines 4385-4434): resolves `domain.GetType(ip->Operand)`; reads
    the object; `ILTypeInstance` -> `CanAssignTo` success assigns the ref,
    failure throws `InvalidCastException`; CLR -> `IsAssignableFrom` likewise;
    null -> write null.
  - `Isinst` (lines 4435-4522): same dispatch, but failure writes null instead
    of throwing; also has a primitive-operand branch (the Legacy StackObject
    representation has no Neo analogue, so it is dropped -- under Neo the
    checked value is always a reference, see "Why no primitive branch").
- `ILType.CanAssignTo` (`ILType.cs:2232`) already walks `BaseType` AND
  `Implements` (the Step 11 interface table), so interface/inheritance-chain
  matching is free once the arm calls it. `CLRType.CanAssignTo` /
  `ILGenericParameterType.CanAssignTo` / `CrossBindingAdaptor.CanAssignTo`
  exist analogously.
- Neo conventions for reading/writing a reference operand (from the existing
  `Box`/`Ldfld_Ref`/`Unbox` arms):
  - read source index: `srcIdx = *(int*)(frameBase + ip->SrcOffset);`
  - read object: `obj = srcIdx >= 0 ? mStack[srcIdx] : null;`
  - write result ref: `dstIdx = frameRefBase + ip->Operand3; mStack[dstIdx] = obj; *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;`

## Design

### A. Offset-lowering stamping (Optimizer.Neo.cs)

Add `case OpCodeREnum.Isinst:` and `case OpCodeREnum.Castclass:` to the existing
`Box`/`Unbox`/`Unbox_Any` case (lines 430-444). Because R1==R2, the block is
unchanged in shape:

```csharp
case OpCodeREnum.Box:
case OpCodeREnum.Unbox:
case OpCodeREnum.Unbox_Any:
case OpCodeREnum.Isinst:        // Step 15
case OpCodeREnum.Castclass:     // Step 15
    {
        short r1 = op.Register1;
        short r2 = op.Register2;
        op.DstOffset = (ushort)localInfos[r1].Offset;
        op.SrcOffset = (ushort)localInfos[r2].Offset;
        op.Operand3 = localInfos[r1].RefOffset;   // dst ref offset
        op.Operand4 = localInfos[r2].RefOffset;   // src ref offset
    }
    break;
```

(With R1==R2, `off1==off2` and `ref1==ref2`; the result overwrites the operand
slot in place, matching Legacy semantics where the cast result replaces the
stack top.)

### B. isinst ExecuteNeo arm

Place the arm near the `Box`/`Unbox` cluster. Skeleton (mirrors the ref
read/write conventions above and the Legacy dispatch order):

```csharp
case OpCodeREnum.Isinst:
    {
        IType type = AppDomain.GetType(ip->Operand);
        if (type == null) throw new NullReferenceException();   // matches Legacy
        srcIdx = *(int*)(frameBase + ip->SrcOffset);
        obj = srcIdx >= 0 ? mStack[srcIdx] : null;
        object result = null;
        if (obj != null)
        {
            if (obj is ILTypeInstance ili)
                result = ili.CanAssignTo(type) ? obj : null;
            else
                result = type.TypeForCLR.IsAssignableFrom(obj.GetType()) ? obj : null;
        }
        // write result ref (in-place: dst ref offset == src ref offset)
        if (result != null)
        {
            dstIdx = frameRefBase + ip->Operand3;
            mStack[dstIdx] = result;
            *(int*)(frameBase + ip->DstOffset) = dstIdx;
        }
        else
        {
            *(int*)(frameBase + ip->DstOffset) = -1;
        }
    }
    break;
```

Notes:
- The checked object is already a reference in mStack (Box produced it, or it
  was a reference local/field). There is no in-frame-flat-byte source to convert.
- `obj is CrossBindingAdaptorType`: the adaptor's `CanAssignTo` delegates to its
  ILInstance, and `type.TypeForCLR.IsAssignableFrom(obj.GetType())` covers CLR
  cross-binding; no special branch is required for the validated scope.
- Nullability of the operand: `srcIdx == -1` (null ref) yields `obj == null` and
  the result is null -- correct for both `as` (null) and `is` (false via the
  later null-check the C# compiler emits).

### C. castclass ExecuteNeo arm

Identical dispatch, but a failed check throws `InvalidCastException` (message
format mirrors Legacy: `"Cannot Cast {0} to {1}"`). Null passes through as null.

```csharp
case OpCodeREnum.Castclass:
    {
        IType type = AppDomain.GetType(ip->Operand);
        if (type == null) throw new NullReferenceException();
        srcIdx = *(int*)(frameBase + ip->SrcOffset);
        obj = srcIdx >= 0 ? mStack[srcIdx] : null;
        object result;
        if (obj == null)
        {
            result = null;            // castclass null -> null
        }
        else if (obj is ILTypeInstance ili)
        {
            if (!ili.CanAssignTo(type))
                throw new InvalidCastException(string.Format(
                    "Cannot Cast {0} to {1}", ili.Type.FullName, type.FullName));
            result = obj;
        }
        else
        {
            if (!type.TypeForCLR.IsAssignableFrom(obj.GetType()))
                throw new InvalidCastException(string.Format(
                    "Cannot Cast {0} to {1}", obj.GetType().FullName, type.FullName));
            result = obj;
        }
        if (result != null)
        {
            dstIdx = frameRefBase + ip->Operand3;
            mStack[dstIdx] = result;
            *(int*)(frameBase + ip->DstOffset) = dstIdx;
        }
        else
        {
            *(int*)(frameBase + ip->DstOffset) = -1;
        }
    }
    break;
```

### D. Why no primitive-operand branch (deviation from Legacy)

Legacy `isinst` (lines 4442-4483) has a branch for when the operand
`objRef->ObjectType <= ObjectTypes.Double` (a primitive stored directly in the
`StackObject`). Under the Neo `byte*` frame, primitives live as flat bytes, but
a value flowed into `isinst` is always a *reference*: the C# compiler emits
`box T; isinst U` when checking a value type, and Box has already produced an
mStack reference. There is therefore no raw-primitive source shape to handle at
the isinst opcode; the boxed object is read as a normal reference and dispatched
through the CLR `IsAssignableFrom` path. (This is also why the compile-time
peephole is an optimization, not a correctness requirement -- see non-goals.)

## Edge cases (validated scope)

- **null operand:** `isinst` -> null; `castclass` -> null. (Both correct.)
- **Same type / identity:** `CanAssignTo`/`IsAssignableFrom` returns true for
  `this == type`; result is the original reference.
- **Inheritance chain:** `ILType.CanAssignTo` recurses through `BaseType`; CLR
  uses the CLR assignability rules. Covered by a test.
- **Interfaces:** `ILType.CanAssignTo` walks `Implements` (Step 11 table); CLR
  interface assignability via `IsAssignableFrom`. `obj as IMyInterface` covered.
- **Boxed value type:** Box (Step 13) produced the boxed `ILTypeInstance`
  (`Boxed == true`) or a boxed CLR object; both are ordinary references and flow
  through the CLR/IL assignability check. Covered by a test.
- **CLR object checked against CLR type:** `IsAssignableFrom` handles it; the
  result ref is the same CLR object (the Legacy `AssignToRegister(..., true)`
  "CLR flag" is not needed under Neo because Neo refs are uniform mStack
  entries).

## Non-goals (explicitly deferred)

- **`box T; isinst U` compile-time peephole:** does not exist in the codebase
  (no peephole pass fuses adjacent Neo ops; see proposal). Not required for
  correctness -- Box followed by Isinst executes correctly via the runtime arms.
  Tracked as a future optimization.
- **Generic-parameter patch table (`PatchKind.IsinstResult`):** `PatchKind` does
  not exist in this codebase. The JIT resolves the isinst type token statically
  for ordinary type operands; a generic-parameter-typed isinst (where the target
  type is a method/type generic parameter not known at compile time) is not
  exercised by the validated test set and is left to a follow-up if/when a patch
  table is introduced.
- **IL-typed catch dispatch:** `CheckExceptionType` still throws NIE for a non-
  `CLRType` catch type (`ILIntepreter.cs:5835`). That is a Step 14/exceptions
  concern, not Step 15.
- **Covariant array casts / array isinst** (e.g. `string[] is object[]`): rides
  on CLR `IsAssignableFrom` for CLR arrays; IL arrays depend on Step 16 array
  work. Not separately validated here.

## Catch-matching relationship (finding)

Neo `catch` type selection does **not** use the `isinst` opcode. The throw path
calls `HandleException` -> `GetCorrespondingExceptionHandler`
(`ILIntepreter.cs:4742,5598`) -> `CheckExceptionType(CatchType, ex, explicitMatch)`
(`ILIntepreter.cs:5823`), which tests
`catchType.TypeForCLR.IsAssignableFrom(exception.GetType())` directly. Therefore:
Step 15's contribution to exception handling is **indirect** -- it makes `is`/
`as`/casts inside catch *bodies* work (the Step 14 TC2 `e != null` workaround can
now be written as `e is T`), but it does not change catch-type matching itself.
Step 14 TC2's assertion remains valid as-is; a new Step 15 test can additionally
exercise `e is DivideByZeroException` inside a catch body.

## Tests (TestCases/NeoStep15Test.cs, ASCII)

Public static parameterless methods, each returns the expected int on PASS. The
assertion style matches prior NeoStep tests (return a sentinel; throw only on a
deliberate negative branch). Cover:
- `obj is Derived` -> true; `obj is Unrelated` -> false (inheritance chain +
  negative).
- `obj as IFace` -> non-null when implemented, null when not.
- boxed value type `is` its type -> true; `is` unrelated -> false.
- `castclass` success round-trip (cast then read a field) -> expected value.
  (castclass *failure* throws InvalidCastException, which the harness cannot
  assert green; do not write a throw-asserting case. Optionally wrap a failed
  cast in try/catch to assert the catch fires -- acceptable.)
