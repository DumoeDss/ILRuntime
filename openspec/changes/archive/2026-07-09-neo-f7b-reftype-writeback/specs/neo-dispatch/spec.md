# spec.md delta - neo-dispatch (MODIFIED)

## MODIFIED Requirements

### Requirement: Neo delegate-Invoke with a byref/out param SHALL marshal the byref and propagate the write-back

A Neo delegate-Invoke whose target signature contains a `ref`/`out` parameter SHALL marshal the byref into the target's parameter region as a valid byref Ref Slot and SHALL propagate the target's write-back to the caller's frame.
(F-7 shipped the primitive-byref write-back + the reference-byref MARSHAL/READ
path; F-7B STRENGTHENS the reference-byref case so the in-place WRITE-BACK of
a callee-CREATED object SHALL propagate to the caller with NO dangling mStack
index.)
For a REFERENCE-typed byref param (`ref string`/`out object`/etc.) where the
callee REASSIGNS the referent (creating a NEW object), the write-back SHALL
deliver that new object to the caller such that the caller's local holds the
new object after the Invoke returns, with the local's mStack index resolving
to a CALLER-OWNED slot that survives the callee's frame pop (the mStack
lifetime promotion required by `neo-byref`'s cross-frame reference-byref
write-back lifetime requirement). The caller's local SHALL NOT hold a
callee-frame mStack index left dangling by the nested `ExecuteNeo` `Ret` pop.

The primitive-byref write-back (F-7: `ref int`/`out int`/multicast) and the
reference-byref MARSHAL+READ path (the callee reads the byref and returns a
derived value) SHALL stay byte-identical green (regression guard).

#### Scenario: ref reference-type delegate param invoked via Neo (write-back binding)

- WHEN an IL method constructs a custom `delegate int D(ref string s)` bound
  to a target `static int Append(ref string s){ s = s + "!"; return s.Length; }`
  that REASSIGNS the referent to a callee-CREATED object, invokes it as
  `r = d(ref s)` with `s == "abc"`, and reads `r` and `s`
- THEN `r` SHALL equal 4 (the new length), `s` SHALL equal `"abc!"` (the
  caller observes the callee-created object), `s`'s mStack index SHALL resolve
  to a slot below the callee's `frameRefBase` (survived the pop), and the
  Invoke SHALL NOT throw `Index was out of range` / `ArgumentOutOfRangeException`
  in a subsequent CLR string binding (e.g. `op_Inequality`) that derefs `s`

#### Scenario: reference-byref delegate marshal+read stays green

- WHEN an IL method constructs `delegate int D(ref string s)` bound to
  `static int ReadLength(ref string s){ return s.Length; }` (READ only, no
  reassignment), invokes it as `r = d(ref s)` with `s == "abc"`, and reads `r`
- THEN `r` SHALL equal 3 and `s` SHALL remain `"abc"` (the marshal+READ path,
  F-7 green, stays green after the promotion refactor)

#### Scenario: primitive-byref delegate write-back stays green

- WHEN an IL method constructs `delegate void D(ref int x)` bound to
  `static void Bump(ref int x){ x += 10; }`, invokes it as `d(ref v)` with
  `v == 5`, and reads `v`
- THEN `v` SHALL equal 15 (the primitive-byref write-back, F-7 green, stays
  byte-identical; no promotion applies to a primitive referent)

#### Scenario: multicast delegate with a reference byref param

- WHEN an IL method constructs a multicast
  `delegate void D(ref string s)` with two targets that each reassign `s`
  (e.g. `AppendBang` then `AppendQ`), invokes it as `d(ref s)` with
  `s == "abc"`, and reads `s`
- THEN `s` SHALL reflect the LAST target's reassigned object (e.g. `"abc!?"`),
  each target SHALL observe the prior target's object (the caller-owned slot
  is the single source of truth across the chain), and the index SHALL be
  stable after the final target's pop
