## MODIFIED Requirements

### Requirement: Neo delegate-Invoke with a byref/out param SHALL marshal the byref and propagate the write-back

A Neo delegate-Invoke whose target signature contains a `ref`/`out` parameter SHALL marshal the byref into the target's parameter region as a valid byref RefSlot and SHALL propagate the target's write-back to the caller's frame.
The byref Ref Slot SHALL be a valid `(objectIndex, offset)` pair addressing the
caller's frame cell or the caller's `mStack` object field, and the target's
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
double`/etc.), reference byref params (`ref string`/`out object`) whose referent
is an mStack object, and CLR-value-type byref params, for both singlecast and
multicast delegates.

The implementation SHALL use a same-frame delegate-Invoke fast path: when the
delegate target is an IL method, the `Callvirt_IL` `IsDelegateInvoke` branch
SHALL run the target on the CALLER's interpreter (via the standard
`InvokeNeoCallTarget` -> `ExecuteNeo` on `this`), reusing the args already
marshaled into the delegate-Invoke `targetBase` by `CopyNeoCallArguments` (which
preserves the byref Ref Slot, same frame), and SHALL propagate the `ref`/`out`
write-back to the caller's frame via `CopyNeoCallThisBack` with a pre-call
byref-source snapshot -- mirroring the normal `Call_IL` path. The branch SHALL
NOT re-read the args through `ReadNeoDelegateInvokeArgs` (the `object[]` funnel
that destroys the byref) and SHALL NOT route an IL target through the
separate-pooled-interpreter `NeoInvokeSub` path for the byref case. The bound
instance SHALL be written into the target's slot 0 (the `this`) before the
target runs. The multicast `next`-chain SHALL be walked on the same interpreter,
each target's write-back accumulating in the caller's frame cell.

The separate-pooled-interpreter `DelegateAdapter.NeoInvokeSub` path SHALL remain
for the CLR-to-IL delegate callback (a CLR method invoking an IL delegate, e.g.
`List<T>.ForEach(ilAction)`) where there is no IL caller frame; that path is
unaffected by this requirement (its `Action<>`/`Func<>` signatures cannot carry
`ref`/`out`).

The green plain-primitive delegate callback shapes (no byref) SHALL stay
byte-identical: the IL-delegate-Invoke construct + Invoke + `List.ForEach`
callback (`NeoStep19_*`) SHALL continue to pass.

#### Scenario: ref int delegate param invoked via Neo

- **WHEN** an IL method constructs a custom `delegate void D(ref int x)` bound
  to an IL target `static void Bump(ref int x){ x += 10; }`, invokes it as
  `d(ref v)` with `v == 5`, and reads `v`
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
  bound to a target that appends to `s` in place and returns the original
  length, invokes it as `r = d(ref s)` with `s == "abc"`, and reads `r` and
  `s`
- **THEN** `r` SHALL equal 3, `s` SHALL equal the appended string (e.g.
  `"abc!"`), and the invoke SHALL NOT throw `ArgumentOutOfRangeException`
  (the byref Ref Slot SHALL be a valid mStack index the callee's `ldind_ref`
  reads directly). NOTE: if the referent is a heap IL ref-field, the callee's
  `ldind_ref` may hit a separate Step-17 deferral; this scenario covers the
  mStack-object referent (the common `ref string` local).

#### Scenario: Multicast delegate with a byref param

- **WHEN** an IL method constructs a multicast `delegate void D(ref int x)`
  with two targets `Bump(ref int x){ x += 10; }` and
  `Double(ref int x){ x *= 2; }`, invokes it as `d(ref v)` with `v == 5`
- **THEN** `v` SHALL reflect both targets' write-backs applied in invocation
  order (`((5 + 10) * 2) == 30`), proving each target saw the prior target's
  mutation and wrote back to the same caller frame cell

#### Scenario: Plain-primitive delegate callback regression guard (must stay green)

- **WHEN** the delegate-Invoke path is exercised with a PLAIN primitive param
  (no byref) -- e.g. `Func<int,int> f = x => x*2; f(42)` or a
  `List<T>.ForEach(Action<T>)` callback whose action takes a plain `int` -- the
  existing green Step-19 shapes (`NeoStep19_*`, the IL-delegate construct +
  Invoke + `List.ForEach` callback)
- **THEN** every such shape SHALL continue to return its correct result
  (`NeoStep19` smoke SHALL stay green), proving the byref-delegate fast path
  did not regress the plain-primitive delegate callback hot path

#### Scenario: Legacy-neutrality (the fix is Neo-specific)

- **WHEN** the same `ref`/`out` delegate-param shapes are run on the Legacy
  engine (`useRegister=true`, plain `Debug` build, `ExecuteR`) -- where the
  delegate callback routes through `DelegateAdapter.ILInvokeSub` (standard
  `StackObject*` push/pop + `ExecuteR`, byref params handled as address
  `StackObject`s, NOT funneled through an `object[]`)
- **THEN** the `ref`/`out` write-back SHALL propagate correctly (Legacy has no
  F-7 gap; the limitation was confined to the Neo `ReadNeoDelegateInvokeArgs` +
  separate-pooled-interpreter path, now superseded by the same-frame fast path)
