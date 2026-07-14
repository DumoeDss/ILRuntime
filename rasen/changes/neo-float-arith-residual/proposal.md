# neo-float-arith-residual -- proposal

## Why
Under Neo, `InheritanceTest07` / `InheritanceTest16` fail with a garbage denormal
(`3E-45`) for `arg1 + 1.2f` / `arg1 * 12333f`. After `neo-typed-opcode-reach-
executer` these methods RUN in Neo (route fixed) but their primitive RETURN is
silently corrupted at the `InvocationContext.InvokeNeo` boundary. The full Neo
smoke sits at **65 failed**; these two are in that set.

## What
A one-line Neo-gated fix in `InvocationContext.InvokeNeo`: the boxed result from
`intp.Run` MUST be pushed with `PushObject(..., isBox:false)` (not `true`) so
primitive returns are written INLINE (`ObjectType=Float/Long/..`, `Value=` the
raw bits), matching the Legacy `ExecuteR` arm and the contract the typed readers
(`ReadFloat`/`ReadInteger`/`ReadLong`/`ReadDouble`, all `*(T*)&esp->Value`)
depend on. `isBox:true` stored every value as an `Object` slot with
`Value = mStack index`, so `ReadFloat` reinterpreted the mStack index (e.g. `3`)
as a float -> `3E-45`. References/value-types are pushed identically either way.

## Root cause (pinned by runtime instrumentation, NOT a JIT/arith issue)
Body arithmetic is provably correct: `conv.r4` -> 11.0f; `addi.r4` -> 12.2f;
`ret` -> retDst 12.2f; `Run`/`NeoBoxReturnValue` -> boxed 12.2f. The corruption
is SOLELY in `PushObject(isBox:true)` storing the mStack index where the typed
reader expects the float bits. This is a CONSUMER-side read-back defect at the
CLR<->IL InvocationContext boundary, NOT the child-16/21 unseeded-producer class
(those are body-side JIT seeding; this is a different layer). The surfacing
note's "unseeded float producer" / "addi.r4 runtime arm" / "JIT tag/stamp"
hypotheses are all disproven by the diagnostics.

## Capability home
`neo-dispatch` (the InvocationContext CLR<->IL re-entry path; sibling of
`neo-typed-opcode-reach-executer` which lives in the same `InvokeNeo` method).
No new opcode, no JIT/optimizer/object-model/binding change.

## Verify (truth = full-smoke number)
- Baseline HEAD `5358b89c`: 65 failed.
- After fix: 65 -> N (InheritanceTest07 + InheritanceTest16 flip PASS). Stash-
  toggle of `InvocationContext.cs` ONLY -> InheritanceTest07 FAILS (`3E-45`) ->
  pop -> PASS. NeoStep 0-failures (no regression). Legacy-neutral (100%
  `#if ENABLE_NEO_MODE`).

## Out of scope
The three hybrid-deferred InvocationContext shapes (byref/ctor/valuetype) stay
on the ExecuteR fallback (pre-existing). Broader Neo opcode/NIE surface = parent
`neo-overhaul`.
