# Design -- implement-neo-catch-complete (IL-typed catch clauses)

Closes deferred Neo item **D-CHECKEX**. Extends the shared
`CheckExceptionType` so a catch clause whose `CatchType` is an `ILType` (not a
`CLRType`) is matched instead of throwing `NotImplementedException`.

## 1. Current state (verified)

`CheckExceptionType` (`ILIntepreter.cs:5823-5836`):

```csharp
bool CheckExceptionType(IType catchType, object exception, bool explicitMatch)
{
    if (catchType == null)
        return true;
    if (catchType is CLRType)
    {
        if (explicitMatch)
            return exception.GetType() == catchType.TypeForCLR;
        else
            return catchType.TypeForCLR.IsAssignableFrom(exception.GetType());
    }
    else
        throw new NotImplementedException();   // <-- D-CHECKEX (line 5835)
}
```

Callers (both shared):
- `GetCorrespondingExceptionHandler` (`ILIntepreter.cs:5609`) calls it inside
  the nearest-handler search, twice per throw (once `explicitMatch=true` at
  `HandleException:4742`, once `=false` at `:4746`).
- Reached identically by Legacy `ExecuteR` (`ILIntepreter.Register.cs:5323` ->
  shared `HandleException`) and Neo `ExecuteNeo` (`ILIntepreter.Neo.cs` outer
  catch -> shared `HandleException`). **Both engines have the same NIE gap.**

The `exception` argument is always a CLR `Exception` object at this point:
`GetCorrespondingExceptionHandler:5602` unwraps `ILRuntimeException` to its
inner `Exception` before calling `CheckExceptionType`, and the outer
`HandleException:4750-4758` also unwraps. So inside `CheckExceptionType`,
`exception` is either:
- a CLR `Exception` raised natively (`DivideByZeroException`, an
  `InvalidCastException` from Step 15 castclass, a host/binding thrown
  exception), or
- the **inner CLR exception** of an `ILRuntimeException` that wrapped a CLR
  exception, or
- the **inner CLR exception** of an `ILRuntimeException` that wrapped an
  IL-thrown exception -- in which case the inner object is the IL exception's
  CLR projection (an `ILTypeInstance` IS-A CLR `Exception` only when the IL
  type inherits a CLR Exception via a `CrossBindingAdaptor`; otherwise the IL
  throw path wraps the IL instance in an `ILRuntimeException` whose inner is
  the IL instance carried as `object`).

To be robust to ALL of these, the new branch must test by **runtime object
shape**, not assume one representation.

## 2. The IL branch (concrete)

Replace the `else throw new NotImplementedException();` with an IL-typed branch.
`catchType` here is an `ILType` (or, defensively, any non-CLR `IType` with a
sensible `CanAssignTo`). Resolve assignability by the runtime shape of
`exception`:

```csharp
else
{
    // Non-CLRType catch clause (typically an ILType). Resolve assignability
    // of the thrown exception to the IL catch type by the runtime shape of
    // the exception object. (D-CHECKEX -- previously threw NIE.)
    if (exception == null)
        return false;
    var exIl = exception as ILTypeInstance;
    if (exIl != null)
    {
        // IL-thrown exception whose runtime object is the IL instance.
        if (explicitMatch)
            return exIl.Type == catchType;
        return exIl.CanAssignTo(catchType);
    }
    // CLR exception caught by an IL catch clause: test whether the IL catch
    // type's CLR projection is assignable from the thrown CLR type. For an IL
    // type that inherits a CLR type via a CrossBindingAdaptor, TypeForCLR is
    // the adaptor's runtime CLR type (ILType.cs:1177-1179); for a plain IL
    // type it is typeof(ILTypeInstance), which only matches an ILTypeInstance
    // (handled above), so a plain CLR Exception correctly does NOT match a
    // plain-IL catch clause.
    Type ctClr = catchType.TypeForCLR;
    if (ctClr == null)
        return false;
    if (explicitMatch)
        return exception.GetType() == ctClr;
    return ctClr.IsAssignableFrom(exception.GetType());
}
```

Why each sub-branch is correct:

- **`explicitMatch` for IL-thrown (`exIl.Type == catchType`):** mirrors the
  CLRType `explicitMatch` arm (`exception.GetType() == catchType.TypeForCLR`),
  which is exact-type (no base/interface). The IL analogue is exact-`ILType`
  identity.
- **non-explicit IL-thrown (`exIl.CanAssignTo(catchType)`):**
  `ILTypeInstance.CanAssignTo` (`ILTypeInstance.cs:968`) delegates to
  `ILType.CanAssignTo` (`ILType.cs:2232`) which walks `this == type`,
  `BaseType.CanAssignTo` (the IL inheritance chain, including a
  `CrossBindingAdaptor` base), and `Implements` (the Step 11 interface table).
  This covers: same IL type, IL subtype caught by IL base catch, IL type caught
  by an interface catch clause, and an IL exception (inheriting a CLR Exception
  adaptor) caught by `catch (System.Exception)` (the CLR-base walk).
- **CLR-thrown against IL catch (`catchType.TypeForCLR.IsAssignableFrom`):**
  handles a CLR exception caught by an IL catch clause whose IL type projects
  to a CLR type assignable from the thrown exception. This is the fallback path
  the planning-context anticipated (e.g. a CLR `Exception` caught by an IL
  catch that maps from `System.Exception`). A plain (non-adaptor) IL type has
  `TypeForCLR == typeof(ILTypeInstance)` (`ILType.cs:1186`), which a CLR
  `Exception` is not assignable to -> correctly returns false (no false
  match).
- **`explicitMatch` for CLR-thrown:** `exception.GetType() == ctClr`, mirroring
  the CLRType arm.

## 3. Legacy-neutrality argument (the gate)

- **The new branch is unreachable for every existing catch test.** Every catch
  clause in the test suite today names a `CLRType`
  (`DivideByZeroException`, `Exception`, `NullReferenceException`,
  `InvalidCastException`). Those enter the `if (catchType is CLRType)` arm,
  whose body is byte-for-byte unchanged. The new branch runs ONLY for an IL
  catch type, which no current test exercises. Therefore: **zero behavior delta
  on every existing Legacy and Neo catch test.**
- **The replaced NIE was never a passing outcome.** Before this fix, an IL
  catch clause crashed with NIE on BOTH engines. There is no Legacy test (or
  Neo test) that depends on the NIE being thrown -- it was a latent crash, not
  a contract. Replacing it with a `bool` cannot turn a previous PASS into a
  FAIL; it can only turn a previous NIE-crash into a match-or-no-match.
- **Cost on the hot path:** one extra `is CLRType` check that CLR-type catches
  (the overwhelmingly common case) already pass through on the original arm.
  Negligible.
- **Gate (validate at apply):** full NeoStep smoke (Neo, `Debug_Neo`) for no
  Neo regression; a Legacy catch filter (plain `Debug` build) for no Legacy
  regression; plus rely on the 519-test Legacy baseline as the standing
  reference.

## 4. Edge cases (validated scope)

- **`catch {}` (catch-all):** `CatchType == null` -> the existing
  `if (catchType == null) return true;` short-circuits before any branch.
  Unchanged.
- **`catch (System.Exception)`:** `System.Exception` is a `CLRType`, so the
  unchanged CLRType arm handles it (`IsAssignableFrom` true for any Exception).
  An IL exception inheriting a CLR Exception adaptor also reaches here as a CLR
  exception (its projection) and matches. No IL-branch involvement.
- **IL catch clause catching an IL exception of a SUBTYPE:** the
  `exIl.CanAssignTo` walk up `BaseType` returns true. Covered.
- **IL catch clause catching an UNRELATED IL exception:** `CanAssignTo` returns
  false; the search continues to the next handler (or the finally/unhandled
  path). Correct -- no false match.
- **Interface catch clause (`catch (IMyIface)` where the interface is IL):**
  `ILType.CanAssignTo` walks `Implements` (Step 11 table). If the IL exception
  type implements the interface, it matches. CLR-interface catch clauses are
  CLRTypes and use the unchanged arm.
- **Nested try/catch (innermost wins):** handled entirely by
  `GetCorrespondingExceptionHandler`'s nearest-match
  (`addr - TryStart` smallest) loop (`ILIntepreter.cs:5598-5622`) -- unchanged.
  The IL branch just makes the per-handler `CheckExceptionType` predicate
  return a correct bool so the nearest-match loop can pick the right one.
- **`explicitMatch` vs not:** `HandleException` calls the search with
  `explicitMatch=true` first (only an exact-type handler matches), then
  `=false` (assignability). The IL branch honors `explicitMatch` in both the
  IL-thrown and CLR-thrown sub-arms, mirroring the CLRType arm. This preserves
  the C# catch-ordering semantics (a more-specific `catch (MyILEx)` is
  preferred over a `catch (BaseILEx)` by the two-pass search + nearest-match).

## 5. Why not unwrap `ILRuntimeException` here

`CheckExceptionType` is called with `exception` ALREADY unwrapped (the callers
`GetCorrespondingExceptionHandler:5602` and `HandleException:4750-4758` unwrap
`ILRuntimeException` to its inner `Exception` before this method sees it). So
this method must NOT re-unwrap; it works on the inner exception object
directly. (If the inner object is an `ILTypeInstance`, the `as ILTypeInstance`
sub-branch catches it; otherwise it is a CLR `Exception` and the CLR sub-branch
handles it.)

## 6. Non-goals

- **No JIT / `ExecuteNeo` arm changes.** Catch dispatch already routes to this
  shared method on both engines; only the predicate changes.
- **No `isinst`/`castclass` change.** Step 15 handles those opcodes
  independently. (The Step 15 design note that IL catch dispatch does NOT use
  `isinst` still holds -- catch matching is `CheckExceptionType`, not the
  isinst opcode.)
- **IL `filter` / `endfilter`** remain NIE (out of scope, as in Step 14).
- **Generic-parameter catch types** (`ILGenericParameterType`): the
  `as ILTypeInstance` + `TypeForCLR.IsAssignableFrom` fallback is reasonable for
  these too, but they are not exercised by the validated test set; the branch
  does not special-case them and will simply consult `TypeForCLR`. If a real
  generic-parameter catch surfaces, it can be revisited.
- **No change to which object is stored in the catch slot.** Step 14 already
  stores the (unwrapped) caught exception into the catch handler's ref slot;
  this change only affects WHETHER a handler matches, not the stored object.
- **No async exception propagation** (Step 20).
