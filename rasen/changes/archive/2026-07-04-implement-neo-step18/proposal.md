## Why

Neo Steps 1-17 ship object/value-type/array/dispatch/exception/byref machinery,
and Step 8b added IL **reference-type** `newobj`. The remaining `newobj` paths
still throw Step-tagged `NotImplementedException`:

- The `ExecuteNeo` `Newobj` arm rejects every **CLR** type with
  `throw new NotImplementedException("Neo Newobj CLR type is not implemented (Step 9)")`
  (`ILIntepreter.Neo.cs:1598-1600`). This blocks `new List<int>()`,
  `new System.Exception()`, and any other CLR-type instantiation from IL.
- **IL value-type** `newobj` (`new MyILStruct(args)`) is structurally absent:
  the arm always allocates a heap `ILTypeInstance` and passes its mStack index
  as `this`, which is wrong for a value type whose `this` must be a frame
  address the constructor writes back to.
- A pre-existing lowering quirk (Q-NEWOBJ, surfaced in Step 16) makes
  `new T(intArg)` silently wrong when it **immediately follows a `newarr`**;
  Step 16 worked around it with a default-ctor + field-set. Step 18 owns
  newobj completion, so it folds in the Q-NEWOBJ fix.

Completing these three paths unblocks CLR-type allocation from IL (incl.
`throw new SomeClrException()`, a likely side-benefit), and lets value-type
constructors that set fields via `this.field = ...` run correctly.

## What Changes

**IL value-type newobj (new).**
- The `Newobj` arm detects `newobjType.IsValueType` and takes a dedicated path:
  the dest register's frame byte region (already sized and aligned by
  `AllocateLocalStackSpaces` for the IL value type) is **zero-initialized** at
  newobj time, and the constructor's `this` is passed as an 8-byte **Ref Slot**
  `(-1, destFrameByteOffset)` -- a frame-native address -- into the callee param
  region's `this` slot, reusing the Step 17 byref call-ABI for the `this`
  parameter. The IL ctor then writes `this.field` through `stind_*`/`stfld`
  against that address, which propagates directly into the caller's frame slot.
  No heap `ILTypeInstance` is allocated for the value-type case.

**CLR type newobj (new).**
- The `Newobj` arm removes the blanket CLR NIE and routes a CLR ctor to
  `InvokeNeoClrMethod(clrMethod, isNewobj: true, ...)`:
  - With a Neo Redirection -> the redirect delegate runs with `isNewobj=true`
    (it allocates the object itself).
  - Without a Neo Redirection -> `clrMethod.Invoke(targetBase, mStack, true)`
    reflection creation (already implemented by Step 13b: `cDef.Invoke(param)`,
    param reading via the unified `ReadNeo*` layout). The returned object is
    stored into the caller's dest mStack ref slot.
- The dest ref slot for a CLR newobj holds the new object's mStack index
  (reference semantics, mirroring the IL ref-type path).

**Q-NEWOBJ fix (fold-in).** Root-cause and fix the `new T(intArg)`-after-`newarr`
collision in the Call/Newobj Push-scanning + dest/arg-register handling. See
design "Q-NEWOBJ root cause" -- the suspected site is the interaction between
the JIT `Newobj` dest register reuse (`baseRegIdx -= pCnt; op.Register1 =
baseRegIdx++` consumes the arg's slot as the dest) and the
`Optimizer.Neo.cs` Call/Newobj lowering's `srcRegs`/`Register1` aliasing when a
preceding `newarr` leaves the arg slot's register sharing frame metadata with
the array temp. The fix is minimal and localized to the newobj-lowering; it
MUST NOT perturb the general Call/Callvirt lowering (full NeoStep smoke gate).

**Explicit In / Deferred list.**
- **In this pass:** IL value-type `newobj` (frame zero-init + Ref-Slot `this` to
  ctor + ctor writeback); CLR-type `newobj` (Neo Redirection + reflection
  creation); Q-NEWOBJ fix; `NeoStep18Test.cs` regression tests. The IL
  ref-type `newobj` path (Step 8b) is reused unchanged for non-VT IL types.
- **Deferred (stated explicitly, NOT silently dropped):**
  - **Delegate `newobj`** (`new Action(foo)`, `new Func<int,int>(bar)`) -- the
    arm already throws `NotImplementedException("Neo Newobj delegate is not
    implemented")`. Delegate construction needs `ldftn`/DelegateAdapter (Step 19);
    folded out of Step 18 to keep scope bounded. The existing delegate NIE
    stays.
  - **CLR value-type `newobj` with reference fields and no ValueTypeBinder**
    via the reflection path -- inherits the Step 13b NIE guard
    (`NeoClrStructHasReferenceField`); a binder CLR struct newobj works via the
    binder/autogen path.
  - **`new T()` where T is a generic-parameter VT** spanning IL/CLR -- defer
    with the generic-byref follow-up (Step 17 deferral).

## Capabilities

### New Capabilities
- `neo-newobj`: IL value-type `newobj` (frame-slot allocation + zero-init +
  Ref-Slot `this` to the ctor + ctor writeback via stind/stfld), CLR-type
  `newobj` (Neo Redirection + reflection creation), the newobj dest/arg
  register-aliasing contract that the Q-NEWOBJ fix establishes, and the
  explicit non-goals (delegate newobj, no-binder CLR-VT-with-refs newobj).

### Modified Capabilities
- `neo-value-types`: value-type construction (`newobj` of an IL value type)
  was an explicit non-goal / Step-18 deferral; this step adds it (frame slot
  as the construction site, ctor `this` is a frame Ref Slot).
- `neo-byref`: the newobj value-type `this` is a frame-native Ref Slot passed
  as the ctor's first parameter -- an additional caller of the byref call-ABI
  `this` is a byref (previously byref `this` was not exercised by newobj).

## Impact

- **Code:**
  - `ILIntepreter.Neo.cs` (`Newobj` arm: branch on `IsValueType` -> frame
    zero-init + Ref-Slot `this`; branch on CLR -> `InvokeNeoClrMethod` with
    `isNewobj=true` + dest mStack store; remove the blanket CLR NIE).
  - `Optimizer.Neo.cs` (Call/Newobj lowering: the Q-NEWOBJ dest/arg-aliasing
    fix; the newobj value-type `this`-as-byref param-region layout so the ctor
    receives an 8-byte Ref Slot at param slot 0).
  - `JITCompiler.cs` (only if the Q-NEWOBJ root cause is in the JIT `Newobj`
    dest/arg register convention; the design pins the suspected site and the
    apply phase confirms with a reproducer before editing).
- **Regression risk:** the Call/Newobj lowering is shared by EVERY call. The
  Q-NEWOBJ fix touches it; the full NeoStep smoke (was 84/84) is the gate, plus
  the new NeoStep18 cases. Previously-failing newobj tests may turn GREEN
  (incl. possibly throw-via-new-CLR-exception tests if CLR newobj unblocks
  them -- a side-benefit to note, not a hard requirement).
- **Tests:** new `TestCases/NeoStep18Test.cs` (ASCII): `new MyILStruct(args)`
  (frame value correct, incl. a ctor that sets a field via `this.field =`);
  CLR type creation (`new List<int>()` or similar); Q-NEWOBJ reproducer
  (`new T(intArg)` immediately after a `newarr`). Legacy (`ExecuteR`) is the
  reference and is NOT modified.
- **Non-goals:** delegate `newobj` (Step 19), no-binder CLR-VT-with-refs
  newobj (Step 13b NIE), generic-parameter VT newobj.
