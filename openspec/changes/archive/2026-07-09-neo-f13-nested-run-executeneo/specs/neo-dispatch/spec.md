# neo-dispatch delta - neo-f13-nested-run-executeneo

## ADDED Requirements

### Requirement: Neo nested host re-entry SHALL preserve the outer ExecuteNeo instruction pointer

Under `ENABLE_NEO_MODE`, nesting `AppDomain.Invoke(...)` (which drives a 2nd
`ILIntepreter.Run` -> 2nd `ExecuteNeo` on a FRESH pooled interpreter) from WITHIN
an in-flight `ExecuteNeo` SHALL leave the OUTER frame's instruction pointer
(`ip`) and frame state intact. The outer `ExecuteNeo` SHALL resume at the
instruction immediately following the re-entering call and SHALL return its
correct result. This SHALL hold for nesting from BOTH a plain mid-body CLR call
AND from inside an exception catch handler, and SHALL hold under garbage-
collection stress during the nested call. The instruction pointer SHALL be a
per-frame local (not a process-static); the outer's `fixed` pin on its
`NeoExecuteBody` array SHALL remain valid across a GC triggered by the nested
call; the outer's frame (`stack.StackBase`, an `AllocHGlobal` unmanaged buffer)
SHALL be unaffected by the inner interpreter's stack; and each pooled
interpreter SHALL own an independent `RuntimeStack` + `ManagedStack` so the
inner `Run` SHALL NOT touch the outer's frame or managed stack.

#### Scenario: Plain mid-body nested Invoke returns the correct result

- **WHEN** an IL method, mid-`ExecuteNeo` (not inside an exception handler), calls
  a CLR method whose Neo redirect performs `appdomain.Invoke(innerILMethod, null)`
  (a 2nd `Run`/`ExecuteNeo` on a fresh pooled interpreter), and the inner method
  returns a value the outer uses
- **THEN** the outer `ExecuteNeo` SHALL resume at the instruction after the
  re-entering call, use the inner's returned value correctly, and return its own
  correct result, with the outer `ip` never advancing past its body

#### Scenario: Catch-handler nested Invoke (the re-entrancy + exception-unwind shape)

- **WHEN** an IL method raises an exception, catches it, and from INSIDE the
  catch handler calls a CLR method whose Neo redirect performs a nested
  `appdomain.Invoke`, immediately after the exception-unwind machinery has run
- **THEN** the outer `ExecuteNeo` SHALL resume correctly after the nested call
  returns, return its correct result, and the outer `ip` SHALL NOT run off the
  end of its body into a garbage opcode

#### Scenario: Nested Invoke under garbage-collection stress

- **WHEN** the nested `appdomain.Invoke` is preceded (inside the re-entering CLR
  redirect) by forced `GC.Collect(MaxGeneration, Forced, blocking)` plus
  substantial allocation, maximizing the chance of relocating a pinned managed
  object
- **THEN** the outer's `fixed (OpCodeR* ptr = body)` pin on its `NeoExecuteBody`
  array SHALL remain valid, the outer `ip` SHALL remain correct, and the outer
  SHALL return its correct result

#### Scenario: Nested host re-entry regression guard

- **WHEN** the CLI mode `NeoF13Nested` runs the adversarial probe
  (`NeoF13NestedProbe.Run`) -- which drives an IL method that nests
  `appdomain.Invoke` both plain mid-body and from a catch handler, with GC stress
  and depth-2 nesting, inside an in-flight `ExecuteNeo`
- **THEN** every probe cell SHALL return its expected result (13), proving the
  outer frame survived the nested re-entry; a runaway-`ip` regression (wrong
  value, `NotImplementedException` from a garbage opcode, or a >10s hang) SHALL
  fail the cell
