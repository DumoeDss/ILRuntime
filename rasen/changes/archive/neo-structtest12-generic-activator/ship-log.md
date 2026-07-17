# neo-structtest12-generic-activator -- ship-log

Shipped (NOT committed -- LEAD commits): the Activator.CreateInstance<T>
value-type redirect + the constrained-callvirt direct-call write-back.
Wave-2 LAST child of neo-overhaul. This closes the full Neo smoke to 0.

## Result
- StructTest12: PASS (was `System.Exception` -- the `if (ins.i != 10)
  throw` fired; `ins.i` read the mStack index of a heap ILTypeInstance).
- FULL SMOKE: 1 -> 0 (`Ran 951 tests, 0 failded, 20 ignored, 7 todos`,
  exit 0 -- NO crash; StructTest12 was the LAST survivor).
- NeoStep 417/0 (no regression -- load-bearing for a constrained-callvirt
  direct-call write-back change, a core struct-method path).
- Legacy-neutral: plain Debug build 0 errors (CreateInstanceNeo is inside
  the `#if ENABLE_NEO_MODE` block 506-1155 of CLRRedirections.cs;
  ILIntepreter.Neo.cs is file-gated). Legacy compiles neither.

## Stash-toggle (airtight)
Revert BOTH engine files (CLRRedirections.cs + ILIntepreter.Neo.cs) to HEAD
-> rebuild (Debug_Neo) -> StructTest12 FAIL (printed `1`, `Ran 1 tests,
1 failded`, exit 127) -> restore -> rebuild -> PASS (printed `10`, 0
failed, exit 0). Proves BOTH sites are load-bearing (the Activator
value-type write alone leaves `ins.i = 0` -- set_i's write is still lost
without the constrained write-back).

## BOTH pinned roots (re-audit: 1 DISPROVEN, 1 CONFIRMED with 2 sub-sites)
The handoff framed TWO coupled bugs. Re-audit with a runtime diagnostic:

### Bug 1 (T mis-resolution) -- DISPROVEN at runtime
The JIT dump label `call.redirect r1, System.Activator::...CreateInstance[
ILTypeInstance]()` made it look like the Activator call's generic arg T
resolved to ILTypeInstance. A `Console.Error.WriteLine` diagnostic inside
CreateInstanceNeo proved the RUNTIME method object is CORRECT:
`method.GenericArguments[0] = TestCases.StructTests/MyStruct2` (isILType=
True, isValueType=True). The generic-param substitution works:
DoCloneAndPatch's MethodToken patch -> GetMethodTokenHash -> appdomain.
GetMethod -> contextMethod.FindGenericArgument("T") -> MyStruct2. The
`[ILTypeInstance]` in the dump is the FRONT-HALF TEMPLATE's captured
display name (the IL-struct-as-CLR-generic-arg mapping at template-capture
time), NOT the runtime value the redirect receives. LESSON: a JIT dump's
method-name label can be a stale capture-T display; verify the RUNTIME
`method.GenericArguments` before concluding a T-resolution bug. The
planner's hypothesized JIT-generic-method-arg substitution work was NOT
needed.

### Bug 2 (heap-not-struct return) -- CONFIRMED, TWO coupled sub-sites
The C# `T ins = new T() { i = 10 };` (T = MyStruct2, `where T: struct,
ITestStruct`, NO `new()` constraint) lowers (Roslyn) to
`call Activator.CreateInstance<T>()` (result -> ins) + `ldloca ins;
ldc.i4 10; constrained MyStruct2; callvirt set_i`. Two sub-sites:

  (a) Activator redirect returned a HEAP reference for a value-type T.
      CreateInstanceNeo did `ilt.Instantiate()` (heap ILTypeInstance) +
      WriteNeoObjectResult (writes the mStack index to retDst). retDst is
      the struct local `ins`'s flat-bytes slot, so the index (1) overwrote
      `ins`'s field bytes -> `ins.i` read 1 (the index). FIX: for
      `t.IsValueType`, write `default(T)` = a ZEROED struct (prim bytes
      zeroed + ref slots nulled -- the Initobj arm's contract) instead of
      a heap reference; reference-type T keeps the Instantiate/WriteNeoObjectResult
      path.

  (b) Even with a zeroed struct, `set_i(10)` did NOT land. The constrained
      callvirt DIRECT-CALL path (IL value type + ILMethod override, Neo arm
      ~:7543) copies the struct BY VALUE into the callee's slot-0 (a fresh
      frame copy, NOT an aliasing byref) and calls ExecuteNeo, but NEVER
      copies the (mutated) slot-0 back. ECMA `constrained.` semantics
      require a value-type instance method to mutate `this` IN PLACE (the
      byref the prefix resolves), so a MUTATOR (set_i) lost its write ->
      `ins.i` stayed 0 after `set_i(10)`. FIX: after ExecuteNeo, copy the
      callee's slot-0 prim bytes back to the caller's struct. Getters
      (ToString/GetHashCode/get_i) leave slot-0 unchanged -> the copy-back
      is an idempotent no-op for them (NeoStep 417/0 confirms no regression).

## Why this is the LAST failing test (the 2-for-1 coupling)
A partial fix (either site alone) does NOT pass:
- Activator-write alone (no constrained write-back): `ins.i = 0` (zeroed
  struct, but set_i lost) -> still throws (0 != 10).
- Constrained write-back alone (Activator returns heap index): `ins.i = 1`
  (the index), and set_i mutates a box copy of the index-bytes -> still
  wrong.
BOTH sites must ship together. This is why StructTest12 was the deepest
singleton -- it is the only smoke test that combines (1) a generic-struct
`new T()` (-> Activator) with (2) an object-initializer mutator
(-> constrained callvirt write-back).

## Deferred (honestly reported, NOT in the smoke)
- Ref-field IL-struct mutator via constrained callvirt: the write-back
  copies ONLY the prim region. A struct with reference fields whose
  mutator modifies a REF field would still lose that ref write (the
  callee's frameRefBase is internal to ExecuteNeo, not exposed for the
  ref-slot copy-back). Pre-existing (the direct-call path never wrote
  back anything before this child); the prim write-back is a strict
  improvement. No smoke test hits this (MyStruct2 = 0 refs; full smoke
  0/951).
- The Activator CreateInstanceNeo CLR-struct arm uses
  CreateDefaultInstance() + WriteNeoValueType (boxed default). A ref-field
  CLR struct NIEs inside WriteNeoValueType (Step-13b) -- the same fail-loud
  contract as every other Neo VT site. StructTest13 (T=TestVector3, a
  binder struct) PASSes (zeroed flat bytes).

## Capability
- neo-dispatch (the Activator CreateInstanceNeo redirect; child-22
  precedent).
- neo-value-types / neo-byref (the constrained-callvirt direct-call
  write-back; the Step-17 D-CONSTRAINED arm).

## Files (NOT committed, LEAD commits)
- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` (CreateInstanceNeo:
  +value-type branch writing default(T) zeroed struct; ~50 added).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`
  (constrained callvirt direct-call path: +slot-0 prim write-back after
  ExecuteNeo; ~18 added incl. the explanatory comment).
