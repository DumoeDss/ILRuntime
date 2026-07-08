# neo-dispatch delta - neo-latent-edges

## ADDED Requirements

### Requirement: Neo delegate-Invoke with a byref/out param SHALL marshal the byref and propagate the write-back

Under `ENABLE_NEO_MODE`, a Neo delegate-Invoke whose target signature contains a `ref`/`out` parameter SHALL marshal that byref into the delegate target's parameter region as a valid byref Ref Slot and SHALL propagate the target's write-back to the caller's frame.
The byref Ref Slot SHALL be a valid `(objectIndex, offset)` pair addressing the
caller's frame cell or the caller's `mStack` object field, and the target's
`ldind.*`/`stind.*` reads/writes through that byref SHALL address the correct
storage. The byref Ref Slot SHALL NOT be a garbage `objectIndex` (the half-read
destruction), and the target SHALL NOT run on an interpreter whose
`frameBase`/`mStack` is unrelated to the byref's caller-frame provenance.
For a `ref`/`out` parameter, any value the target writes back (a `stind.*` to
the byref) SHALL propagate to the caller's frame cell so the caller observes
the mutated value after the delegate Invoke returns. This SHALL hold for
primitive byref params (`ref int`/`out int`/`ref long`/`ref float`/`ref
double`/etc.), reference byref params (`ref string`/`out object`), and
CLR-value-type byref params, for both singlecast and multicast delegates.

This requirement is currently NOT satisfied on HEAD (F-7 / NEO-DELEGATE-REFOUT
-- accepted-known limitation; see `design.md` section 2). It is recorded as the
target of a future byref-delegate change (Step-19-sized frame-to-frame
delegate-invoke mechanism). The current code routes the delegate-Invoke
through `ReadNeoDelegateInvokeArgs` (which destroys the byref into an
`object[]`) and runs the target on a separate pooled interpreter, so neither
the byref marshaling nor the write-back is possible through that path. A future
fix SHALL add a frame-to-frame delegate-invoke fast path that preserves the
byref Ref Slot and propagates the write-back, while keeping the green plain-
primitive delegate callback shape (`NeoStep19_*`) byte-identical.

#### Scenario: ref int delegate param invoked via Neo

- **WHEN** an IL method constructs a custom `delegate void D(ref int x)` bound
  to an IL target `static void Bump(ref int x){ x += 10; }`, invokes it as
  `D d = Bump; int v = 5; d(ref v);`, and reads `v` after the invoke
- **THEN** `v` SHALL equal 15 (the target's write-back propagated to the
  caller's frame cell), and the invoke SHALL NOT throw
  `ArgumentOutOfRangeException` at `ILIntepreter.Neo.cs:3726` (the `Ldind_I4`
  byref-read arm)

#### Scenario: out int delegate param invoked via Neo

- **WHEN** an IL method constructs a custom `delegate void D(out int x)` bound
  to `static void Set(out int x){ x = 99; }`, invokes it as `d(out v)`, and
  reads `v`
- **THEN** `v` SHALL equal 99, and the invoke SHALL NOT throw
  `ArgumentOutOfRangeException` at `ILIntepreter.Neo.cs:3636` (the `Stind_I4`
  byref-write arm)

#### Scenario: ref reference-type delegate param invoked via Neo

- **WHEN** an IL method constructs a custom `delegate int D(ref string s)`
  bound to a target that uppercases `s` in place and returns its length,
  invokes it as `string s = "abc"; int r = d(ref s);`, and reads both `r` and
  `s`
- **THEN** `r` SHALL equal 3, `s` SHALL equal "ABC", and the invoke SHALL NOT
  throw `ArgumentOutOfRangeException` at `ILIntepreter.Neo.cs:3831` (the
  `Ldind_Ref` byref-read arm)

#### Scenario: Plain-primitive delegate callback regression guard (must stay green)

- **WHEN** the delegate-Invoke path is exercised with a PLAIN primitive param
  (no byref) -- e.g. `Func<int,int> f = x => x*2; f(42)` or a
  `List<T>.ForEach(Action<T>)` callback whose action takes a plain `int` -- the
  existing green Step-19 shapes (`NeoStep19_*`, the IL-delegate construct +
  Invoke + `List.ForEach` callback)
- **THEN** every such shape SHALL continue to return its correct result
  (`NeoStep19` smoke SHALL stay green), proving the future byref-delegate fix
  did not regress the plain-primitive delegate callback hot path

#### Scenario: Legacy-neutrality (the limitation is Neo-specific)

- **WHEN** the same `ref`/`out` delegate-param shapes are run on the Legacy
  engine (`useRegister=true`, plain `Debug` build, `ExecuteR`) -- where the
  delegate callback routes through `DelegateAdapter.ILInvokeSub` (standard
  `StackObject*` push/pop + `ExecuteR`, byref params handled as address
  `StackObject`s, NOT funneled through an `object[]`)
- **THEN** the `ref`/`out` write-back SHALL propagate correctly (Legacy has no
  F-7 gap; the limitation is confined to the Neo `ReadNeoDelegateInvokeArgs` +
  separate-pooled-interpreter path)
