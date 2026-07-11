# Design - neo-f13-nested-run-executeneo

> Scope-AWARE dump-gate for F-13 / NEO-NESTED-RUN-EXECUTENEO. The binding task
> was to ISOLATE the root cause of nested `appdomain.Invoke` corrupting the outer
> `ExecuteNeo` `ip` BEFORE any fix. Probed by static code-evidence + a live
> adversarial probe on HEAD `7a0f4cbd`. Verdict: **F-13 is NOT reproducible.**
> The four candidate mechanisms are each impossible by construction AND disproven
> by the live probe. The originally-observed corruption was a symptom of the
> Step-6 parameterless-only `Run` shim (since fixed by `neo-f4-parametrized-run-
> entry` + `neo-f4-surfaced-gaps`), NOT a re-entrancy engine bug. This change
> ships the probe as a regression guard; no engine source change. Legacy is the
> REFERENCE; the shipped additions are Neo-gated or test-only.

## 0. Verdict: NOT REPRODUCIBLE (the dump-gate outcome)

The adversarial probe (`NeoF13NestedProbe`, CLI mode `NeoF13Nested`) drives an
IL method that, mid-`ExecuteNeo`, calls a CLR bridge whose Neo redirect performs
`appdomain.Invoke(innerILMethod, null)` -- a 2nd `Run` -> 2nd `ExecuteNeo` on a
FRESH pooled interpreter, nested inside the outer. It runs TWO cells:

- **Cell 1 (plain mid-body nesting):** `NeoStep14_F13_NestedInvokeProbe` calls
  `NeoF13Bridge.NestedInvoke()` (redirect -> nested `appdomain.Invoke(F13_Inner)`,
  which itself nests a 2nd-level `appdomain.Invoke(EchoRef)` for depth-2). The
  redirect also forces `GC.Collect(MaxGeneration, Forced, blocking)` twice +
  2000 byte-array allocations to stress any pin-relocation hypothesis. Expected
  13; GOT 13.
- **Cell 2 (the EXACT F-4 #3 catch-handler shape):**
  `NeoStep14_F13_NestedInCatchProbe` raises `DivideByZeroException`, catches it,
  and inside the catch handler calls the same nested-Invoke bridge. This is the
  shape originally recorded as hitting the -97 corruption. Expected 13; GOT 13.

Result on HEAD `7a0f4cbd`: **2/2 cells PASS.** No runaway-`ip` signature. The
outer `ExecuteNeo` instruction pointer survived the nested re-entry intact. The
probe is run with `F13_PROBE=1` (a temp diagnostic that printed whenever `ip`
ran past the body) -- the diagnostic NEVER fired; after confirming, the
diagnostic was removed and the probe STILL passes on the unmodified interpreter.

**NeoStep regression: 229/0/0** (226 baseline + 3 temp probe methods, all pass).

This is decisive: F-13 as described (nested `ExecuteNeo` corrupting the outer
`ip`) does not exist on HEAD. The candidates are eliminated below.

## 1. ISOLATED root cause: no defect; the four candidates are impossible

The dump-gate asked: is `ip` (or the frame pointer) a process-static / shared
across interpreters? Does the inner `appdomain.Invoke` (fresh pooled interpreter)
disturb the outer's pinned frame (a `fixed` pin invalidated by a GC the inner
JIT triggers)? Is the pool-isolation incomplete? Each is refuted by static
evidence:

### 1.1 `ip` is a per-frame local C# variable (NOT a process-static)

`ExecuteNeo` (`ILIntepreter.Neo.cs:828`) declares the instruction pointer as a
LOCAL inside a `fixed` block:

- `ILIntepreter.Neo.cs:927`: `fixed (OpCodeR* ptr = body)`
- `ILIntepreter.Neo.cs:929`: `OpCodeR* ip = ptr;`

`ip` lives on the OUTER `ExecuteNeo`'s C# stack frame. The inner `Run` is a
separate C# method call (`AppDomain.Invoke` -> `inteptreter.Run` ->
`ExecuteNeo`) on a DIFFERENT `ILIntepreter` instance, which has its OWN `ip`
local. There is no process-static holding `ip`. A `grep` for `static` pointer
fields in `ILIntepreter.Neo.cs` finds NONE that hold `ip`/`frameBase`/`ptr`
(only `HoistNeoILValueToHeap`, an unrelated heap-lifting helper). **A separate
C# method invocation on a separate interpreter instance cannot write the outer's
local `ip`.** Candidate 1 eliminated.

### 1.2 The `fixed` pin on `NeoExecuteBody` is GC-stable (managed arrays, when pinned, are not relocated)

The body the outer iterates is `method.CompiledFrame.NeoExecuteBody`
(`ILIntepreter.Neo.cs:848`), a managed `OpCodeR[]`. The `fixed (OpCodeR* ptr =
body)` pin (`:927`) pins THIS array for the duration of the loop. A pinned
managed object is explicitly EXCLUDED from relocation by the GC compactor -- the
GC leaves pinned objects in place for the duration of the pin. The inner JIT
(should it recompile something) runs in a managed-allocated buffer; it cannot
invalidate the outer's pin on a DIFFERENT array. The live probe's forced
`GC.Collect(MaxGeneration, Forced, blocking)` (twice) + 2000 allocations inside
the nested redirect -- the maximal GC stress -- did NOT corrupt the outer's `ip`.
**Candidate 2 eliminated.**

(For completeness: even if `NeoExecuteBody` were REPLACED on the `CompiledFrame`
struct during the nested call, the outer's `fixed` pin holds the OLD array
object alive and in place; `ip` would keep iterating the old, intact body. There
is no path by which the array's contents change under a live pin.)

### 1.3 The Neo frame is on `AllocHGlobal` unmanaged memory (never relocated)

The outer's `frameBase` is `(byte*)stack.StackBase` (`ILIntepreter.Neo.cs:858`
via `Run`'s `ILIntepreter.cs:132`). `RuntimeStack.StackBase` is
`Marshal.AllocHGlobal(sizeof(StackObject) * MAXIMAL_STACK_OBJECTS)`
(`RuntimeStack.cs:35`) -- UNMANAGED native memory allocated once per interpreter
and NEVER relocated or collected by the GC. The inner interpreter has its OWN
`RuntimeStack` with its OWN `AllocHGlobal` buffer. There is no shared frame
buffer. **Candidate 3 (shared `Stack`/frame relocation) eliminated.**

### 1.4 The interpreter pool is fully isolated (independent stacks + mStack)

`AppDomain.RequestILIntepreter` (`AppDomain.cs:1855`) hands out either a pooled
or a `new ILIntepreter(this)` (`:1868`). Each `ILIntepreter` owns its own
`RuntimeStack` (its own `StackBase` + its own `ManagedStack`/`AutoList`). The
inner `Run` (`ILIntepreter.cs:191` `ExecuteNeo(...)`) operates on the INNER
interpreter's `stack` exclusively -- it never touches the outer interpreter's
`stack.ManagedStack` or `stack.StackBase`. `FreeILIntepreter` (`:1879`) clears
ONLY the freed interpreter's own `Stack.ManagedStack`/`Frames`/allocator
(`:1898-1900`); it cannot touch the outer's. **Candidate 4 (pool-isolation gap)
eliminated.**

### 1.5 The originally-observed corruption was the Step-6 `Run` shim (already fixed)

The F-13 row itself records the corruption "reproduced with the OLD
parameterless-only Run shim -- identical ipOff-past-body garbage" and that "the
parametrized-Run change is NOT the cause." Re-examination: the OLD `Run` shim
(`neo-f4-parametrized-run-entry` design section 0.1) built the Neo frame at
`StackBase` but NEVER populated the param region or slot-0 `this`, and reserved
ONLY the return-ref region (not the callee frame's `TotalRefSize`). An IL method
invoked via that shim ran on a frame with uninitialized param/locals bytes and an
undersized ref region. When such a method branched (e.g. the F-4 #3 catch
handler re-entering `appdomain.Invoke`), the corrupted frame state produced the
runaway-`ip` symptom. `neo-f4-parametrized-run-entry` (2026-07-08) rewrote `Run`'s
Neo arm to mirror `DelegateAdapter.NeoInvokeSub` (full frame-ref reservation +
locals zeroing + slot-0 `this` + param marshalling); `neo-f4-surfaced-gaps`
(2026-07-09) fixed the `ReadNeoReference` null-sentinel and `ILType` base-field
accumulation. **With those shipped, the nested `appdomain.Invoke` path produces a
correct frame and the symptom is gone** -- which is exactly what the live probe
confirms.

## 2. Scope decision: SHIP (regression guard; no engine change)

SMALL. The dump-gate's purpose was to decide SHIP-vs-SEQUENCE for a re-entrancy
fix. The gate's outcome is that NO fix is needed (the candidates are
impossible + the symptom's true cause was already fixed). What remains valuable
is the **adversarial regression guard** so the proven-correct re-entrancy cannot
silently regress, and the **eliminated-hypotheses record** so a successor does
not re-explore the dead ends. Both are shipped here.

This is the architecturally correct shape: the probe exercises the real
re-entrancy path (a CLR redirect performing `appdomain.Invoke` mid-`ExecuteNeo`,
the same path cross-binding adaptors and reflection-driven re-entry use), driven
both plain and from a catch handler, with GC stress. A green probe IS the binding
gate (not a green smoke) because the probe is the adversarial nesting shape
itself.

## 3. The regression-guard probe design

### 3.1 The bridge (CLR type the IL probe calls)

`ILRuntimeTestBase/TestFramework/NeoF13Bridge.cs`: a trivial CLR static
`int NestedInvoke()`. Its C# body is unreachable for the IL call path (the Neo
redirect overrides it). It exists only so the IL probe can express "call a CLR
method whose body re-enters" -- the redirect supplies the re-entry.

### 3.2 The Neo redirect (the nested re-entry)

`ILRuntimeTestCLI/NeoF13NestedProbe.cs` registers
`RegisterCLRMethodRedirectionNeo(NestedInvoke, NestedInvoke_Neo)`.
`NestedInvoke_Neo` does, while the outer `ExecuteNeo` is in flight:

1. GC stress: 2000 byte-array allocations +
   `GC.Collect(MaxGeneration, Forced, blocking)` x2 + `WaitForPendingFinalizers`
   (maximal stress on any pin-relocation hypothesis).
2. `appdomain.Invoke(F13_Inner, null)` -- the nested `Run`/`ExecuteNeo` on a
   FRESH pooled interpreter (depth-1 nesting).
3. `appdomain.Invoke(EchoRef, null)` -- a depth-2 nested `Run`/`ExecuteNeo`
   (proves multi-level re-entry, not just one level).
4. Writes the inner int result into the caller's dest slot.

### 3.3 The IL probe methods

In `TestCases/NeoStep14Test.cs`:
- `F13_Inner` -- the inner IL method the redirect re-invokes (returns 7).
- `NeoStep14_F13_NestedInvokeProbe` -- the outer: `before=1`, calls the bridge,
  `after = before + nested`, returns 13 iff `nested == 7`. Plain mid-body nesting.
- `NeoStep14_F13_NestedInCatchProbe` -- the EXACT F-4 #3 shape: raises
  `DivideByZeroException` (a native fault, no CLR `newobj`), catches it, calls
  the bridge INSIDE the catch, returns 13.

### 3.4 The CLI hook

`ILRuntimeTestCLI/Program.cs`: a `NeoF13Nested` nameFilter (mirrors `NeoF4ParamRun`)
runs `NeoF13NestedProbe.Run(appdomain)` and reports PASS/FAIL per cell.

### 3.5 csproj changes

`ILRuntimeTestCLI/ILRuntimeTestCLI.csproj` `Debug_Neo` PropertyGroup: add
`DEBUG;TRACE` to `DefineConstants` and `AllowUnsafeBlocks=true`. The probe's Neo
redirect delegate (`CLRRedirectionDelegateNeo`, declared `public unsafe delegate`
at `AppDomain.cs:32`) requires an `unsafe` context both at the redirect method
(`static unsafe void`) and at the call site that constructs the delegate
(`appdomain.RegisterCLRMethodRedirectionNeo(mi, NestedInvoke_Neo)` -> the
enclosing `Run` method is `unsafe`). The original `Debug_Neo` config defined
only `ENABLE_NEO_MODE` (overriding the SDK's default `DEBUG`), so the existing
`#if DEBUG` AutoList alias and the new `unsafe` redirect both needed the
constants + unsafe flag. This is a build-config normalization, not a behavior
change (the existing CLI probes like `NeoF4ParamRun` are in the ILRuntime
project which already had these; the CLI project did not).

## 4. Adversarial probes (the binding gate)

### 4.1 Cell 1: plain mid-body nested Invoke

`NeoStep14_F13_NestedInvokeProbe` calls the bridge mid-body (not in a handler).
The redirect nests `appdomain.Invoke` + GC stress + depth-2 nesting. SHALL return
13. A regression that corrupts the outer `ip` would return a wrong value, throw
`NotImplementedException` (runaway `ip` -> garbage opcode), or hang (ip-corruption
loop).

### 4.2 Cell 2: catch-handler nested Invoke (the F-4 #3 shape)

`NeoStep14_F13_NestedInCatchProbe` raises + catches `DivideByZeroException`, then
calls the bridge INSIDE the catch. This is the shape that originally surfaced
F-13. SHALL return 13. This is the highest-value cell -- the catch-handler path
exercises the exception-unwind machinery (`HandleException` at
`ILIntepreter.Neo.cs:4487`) immediately before the nested re-entry, which is
where a frame-state corruption would most likely manifest.

### 4.3 Regression: full NeoStep smoke

Full `NeoStep` filter SHALL stay green (226/0/0 baseline + the 3 probe methods =
229/0/0). Legacy plain-`Debug` build = 0 errors confirms Legacy-neutrality.

## 5. Goals / Non-Goals

**Goals:**

- ISOLATE the F-13 root cause (DONE: no defect; the four candidates are
  impossible; the symptom's true cause was the Step-6 `Run` shim, already fixed).
- Ship the adversarial probe as a permanent regression guard for nested
  `appdomain.Invoke` re-entry under Neo.
- Record the eliminated hypotheses so a successor does not re-explore them.

**Non-Goals:**

- Any engine source change (none is needed; the probe passes on the unmodified
  interpreter).
- Re-enabling the IL-side F-4 #3 probe (the host-side `NeoF4ParamRun` cells are
  the cleaner gate; the F-13 probe here adds the NESTED dimension as a separate
  guard, not a replacement).
- Extracting a shared `NeoMarshalCall` helper (the optional refactor sequenced
  in `neo-f4-parametrized-run-entry`; unrelated to F-13).

## 6. Risks / Trade-offs

- **[Probe is test-only; a real re-entrancy regression could slip past it]**
  -> Mitigation: the probe exercises BOTH the plain mid-body path AND the
  catch-handler path (the highest-risk path), with GC stress and depth-2 nesting.
  Any corruption in the `fixed`-pin, pool-isolation, or frame-state machinery
  would surface as a wrong value, a `NotImplementedException`, or a hang
  (>10s = the ip-corruption loop, per the unittest guide).
- **[csproj `DefineConstants` change could affect other CLI behavior]**
  -> Mitigation: adding `DEBUG;TRACE` to `Debug_Neo` only affects DEBUG builds
  and only adds the standard debug constants the SDK normally provides (the
  explicit override had removed them). `AllowUnsafeBlocks` is already standard
  for the ILRuntime project. No Release-path change.

## 7. Open Questions

- None blocking. The dump-gate is conclusive (the probe passes on the unmodified
  interpreter; the candidates are impossible by construction). If a FUTURE change
  reintroduces a real re-entrancy corruption, this probe will catch it.
