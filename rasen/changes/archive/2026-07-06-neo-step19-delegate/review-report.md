# Review Report — neo-step19-delegate (Step 19 Delegates)

**Reviewer:** adversarial, non-author. **Date:** 2026-07-06.
**Branch:** `features/object-model-overhaul`. **Working tree:** UNCOMMITTED.
**Skill:** `openspec-gstack-review` over `git diff HEAD`.

## Verdict: CHANGES-REQUESTED

One Major resource leak (interpreter-pool leak on every delegate callback); the
rest is correct. The autogen patch audit is CLEAN (all 15 delegate bindings
patched, none missed, consistent with the codegen fix). Smoke reproduced
independently: Neo 140/140 + Legacy NeoStep19 10/10. Adversarial probes (3-deep
multicast, Func multicast last-wins, nested-nested ForEach) all PASS on both
engines.

---

## Mandatory probes — outcomes

### Probe 1 — the 15 autogen binding patches (HIGHEST PRIORITY): CLEAN

- **All delegate bindings patched, none missed.** Enumerated every
  `System_Action*` / `System_Func*` (and checked for `Predicate*`,
  `Comparison*`, `Converter*`, `EventHandler*`, `AsyncCallback*`,
  `ThreadStart*`) in `ILRuntimeTestBase/AutoGenerate/` — exactly 15 delegate
  bindings exist (`System_Action_*` x9, `System_Func_*` x6), all 15 are in the
  diff with the `CheckCLRTypes(...,IsDelegate)` this-read patch.
- **Patch is byte-identical to what the fixed codegen produces.** Verified
  `TypeFlags.IsDelegate == 0x8` (`Extensions.cs:168`) and that a pure delegate
  type's `GetTypeFlagsRecursive()` returns exactly `IsDelegate` (no other flag
  bits). The codegen fix in `MethodBindingGenerator.cs:298` emits
  `typeof({type}).CheckCLRTypes(ReadNeoReference(...), (TypeFlags)8)`, and the
  manual patches use the literal `(ILRuntime.CLR.Utils.Extensions.TypeFlags)8`.
  The Legacy wrapper (`GenerateMethodWraperCode_Legacy` via
  `GetRetrieveValueCode` -> `:391`) emits the IDENTICAL `CheckCLRTypes(...,
  (TypeFlags){GetTypeFlagsRecursive()})` form. So the next regeneration produces
  byte-identical output — the patches will NOT be silently reverted. CONSISTENT.
- **Delegate-TYPED PARAMS in non-delegate bindings:** `List.ForEach` is NOT
  autogen-bound (grep finds no `ForEach` in any autogen binding), so
  `List.ForEach(action)` in the probes routes through the reflection fallback
  (`CLRMethod.Invoke`), which DID get the `CheckCLRTypes(IsDelegate)` param
  unwrap (`CLRMethod.cs:447-453`). No non-delegate binding with a delegate param
  was missed either.
- **Param-read codegen** (`BindingGeneratorExtensions.cs:217-232`) emits the
  same unwrap for delegate-typed params. Consistent. No binding has a delegate
  param that needed manual patching (delegate bindings' own params are int /
  string / ILTypeInstance / TestVector3, not delegates).

### Probe 2 — `NeoInvokeSub` fresh-interpreter + nested-nested re-entry: PASS (with leak caveat)

- Wrote a temporary probe `NeoStep19_ReviewProbeC`: an IL method calls
  `List.ForEach(action)` where `action` ITSELF calls `List.ForEach(action2)`
  (nested-nested). Ran it on Neo AND Legacy — both green (sum = 28, the
  expected 1+4+9+1+4+9). **The fresh-interpreter mechanism, mStack restore, and
  pool/stack do not leak or clobber CORRECTNESS** under 2-level nesting.
- **HOWEVER — see Major F1 below:** the fresh interpreter is never returned to
  the pool. Correctness holds because each invoke allocates a fresh interpreter
  and runs synchronously, but the pool is starved — every callback allocates a
  new `ILIntepreter`. This is the leak.

### Probe 3 — `ldvirtftn` override dispatch: PASS

- `NeoStep19_VirtualMethod` (existing TC4): `NeoStep19Base obj = new
  NeoStep19Derived(); Func<int> f = obj.VirtualMethod;` returns 2 (the Derived
  override). The `Ldvirtftn` arm resolves via
  `((ILTypeInstance)thisObj).Type.GetVirtualMethod(ilm)` — the Step-10 VTable
  slot path. Confirmed the DERIVED override runs, not the base. The 2-level
  hierarchy in the test exercises exactly this.

### Probe 4 — Multicast order + return (next-chain): PASS

- Wrote `NeoStep19_ReviewProbeA`: 3-delegate multicast `a += Add1; a += Add10;
  a += Add100;` -> Counter == 111 (order preserved). Remove-from-middle
  `b -= Add10;` -> 101 (only Add1+Add100 run). Both green on Neo + Legacy.
- Wrote `NeoStep19_ReviewProbeB`: `Func<int,int>` multicast `f += MulX2; f +=
  MulX3; f += MulX5; f(10)` returns 50 (the LAST, MulX5). Green on Neo +
  Legacy. **Confirms `NeoInvokeSub`'s `next`-chain walk discards intermediate
  returns and returns the last** (the `if (next != null) result =
  n.NeoInvokeSub(args);` tail at the end of `NeoInvokeSub`).

### Probe 5 — `Call_Redirect` arm + `DelegateCombineNeo`/`DelegateRemoveNeo`: PASS

- The `+=` / `-=` operators lower to `System.Delegate.Combine` / `Remove` (a
  `Call_Redirect`), routed through `InvokeNeoClrMethod` (the new
  `case OpCodeREnum.Call_Redirect` arm). Probes 4 + TC5 (multicast combine) +
  TC6 (multicast remove) exercise both. Both green. The Neo redirects read
  params in DECLARATION order (param 0 = dele1/source) — confirmed correct
  (the apply note says stack-order initially broke `-=`; declaration-order
  fixes it; my remove-from-middle probe confirms).

### Probe 6 — Legacy byte-identical: CONFIRMED

- The `DelegateAdapter.cs` change is `#if ENABLE_NEO_MODE` / `#else` gated;
  the Legacy `StackObject` path (the `using (var ctx = BeginInvoke())` body) is
  byte-identical (only a trailing-whitespace change on `ctx.SetInvoked(esp);`
  in the 0-arg `FunctionDelegateAdapter` — cosmetically different, semantically
  identical).
- Plain `Debug` CLI builds clean (0 errors).
- Legacy NeoStep19 filter: **10/10 green** (ran independently).

### Probe 7 — Neo 140/140 reproduced: CONFIRMED

- `NeoStep19` filter: 10/10. Full `NeoStep` smoke: **140/140, 0 failed**.

### Probe 8 — Probe scoping (ref/out + ValueTypeParam): DEFERRALS RECORDED

- **Ref/out through an IL-delegate-Invoke** is explicitly scoped OUT and
  recorded as a follow-up in `design.md:341-345` and `tasks.md:42`
  ("byref-aware arg marshaling in NeoInvoke ... deferred"). NOT silently
  dropped. TC8 uses a plain int param to exercise the IL-delegate construct +
  Invoke routing (the load-bearing assertion for that path). Acceptable.
- **TC10 ValueTypeParam** target returns a constant; the delegate-vt-param
  round-trip itself works (the `WriteNeoCallSlot` `default` -> `WriteNeoValueType`
  path). The `Ldfld`-on-CLR-struct gap is pre-existing
  (`[NEO-IL-VT-INSTANCE-COVERAGE]`), noted in the test comment and `design.md`.
  Acceptable.

### Probe 9 — Blast-radius (call-lowering shared by every call): CLEAN

- `Optimizer.Neo.cs`: the `Ldftn`/`Ldvirtftn` lowering case is additive (a new
  `case`); the `Call_Redirect` entry in the Call-case map-building is a new
  switch label (was previously absent -> fell through, no map built). Neither
  touches the existing `Call` / `Newobj` / `Callvirt_*` lowering. Non-delegate
  calls are byte-identical. The 140/140 smoke (high call-density across all
  steps) covers this.

---

## Findings

### F1 — [MAJOR] `NeoInvokeSub` leaks the fresh interpreter (never freed back to the pool)

**File:** `ILRuntime/Runtime/Intepreter/DelegateAdapter.cs` (`NeoInvokeSub`,
~`:977-1150`).

**Problem.** `NeoInvokeSub` calls `appdomain.RequestILIntepreter()` to get a
fresh interpreter per delegate invocation (mirroring Legacy `BeginInvoke`), but
NEVER calls `appdomain.FreeILIntepreter(intp)`. Legacy returns the interpreter
via `using (var ctx = BeginInvoke())` -> `InvocationContext.Dispose()` ->
`domain.FreeILIntepreter(intp)` (`InvocationContext.cs:594-603`).

`RequestILIntepreter` (`AppDomain.cs:1595-1617`) dequeues a free interpreter OR
allocates a `new ILIntepreter(this)`. `FreeILIntepreter` (`:1619+`) enqueues it
back AND clears its `Stack.ManagedStack` / `Stack.Frames`. With the free never
called:

1. **Pool starvation / unbounded allocation.** Every delegate callback (e.g.
   `list.ForEach(action)` over N items, a hot UI callback, an event invocation
   in a loop) allocates a NEW `ILIntepreter`. The free-pool stays empty, so no
   reuse ever occurs. For delegate-heavy hot-update code (the whole point of
   Step 19) this is a steady memory + GC-pressure leak proportional to the
   callback count. Correctness is unaffected (each runs synchronously on its
   own engine stack), which is why the smoke stays green — but production
   delegate-heavy IL code will leak.

2. **mStack never cleared.** `FreeILIntepreter` clears `Stack.ManagedStack`.
   Since the leaked interpreter is never reused, this is latent (not active).
   But if F1 is fixed by freeing, the clear comes for free.

**Probe.** Read `NeoInvokeSub` end-to-end: no `FreeILIntepreter` call, no
`try/finally`, no `using`. Confirmed by grepping
`DelegateAdapter.cs` for `FreeILIntepreter` -> 0 hits in the new code (only the
implicit Legacy path via `InvocationContext.Dispose`).

**Fix.** Wrap the body in `try/finally` and free the interpreter on all exit
paths (including the `if (unhandled) throw` path). Mirror the Legacy
`using (var ctx = BeginInvoke())` shape:

```csharp
ILIntepreter intp = appdomain.RequestILIntepreter();
try {
    // ... existing frame build + ExecuteNeo + return read ...
    return result;
} finally {
    appdomain.FreeILIntepreter(intp);
}
```

Note: `FreeILIntepreter` clears `Stack.ManagedStack`, so the explicit
`mStack.RemoveRange(mStackBase, ...)` becomes redundant after the fix (but is
harmless to keep). The `next`-chain walk (`result = n.NeoInvokeSub(args)`)
recurses, so each link in the chain gets its own request/free pair — correct.

**Severity rationale:** Major (not Blocker) — correctness is fine and the smoke
proves it; the defect is a resource leak on the delegate-callback hot path that
only manifests under sustained delegate-heavy IL code (the realistic
production shape for Step 19). Must fix before archive.

---

### F2 — [MINOR] `WriteNeoCallSlot`: CLR struct with reference fields not handled (pre-existing edge)

**File:** `DelegateAdapter.cs` `WriteNeoCallSlot` (~`:1130`).

**Problem.** The reference-slot detection is `info.RefCount > 0 && info.Size ==
4`. A CLR struct param WITH managed-reference fields would have `RefCount > 0`
AND `Size != 4`; it falls through to the `default` case -> `WriteNeoValueType`,
which writes only flat primitive bytes — the ref-field mStack slots would not
be populated. This is the same `GatherValueTypes`/temp-sizer edge flagged as
out-of-scope in opt-harden-2's "Out of scope (noted, NOT fixed)" and area4's
reflection-fallback NIE guard. No Step 19 probe exercises a delegate with a
struct-with-ref-field param.

**Probe.** Read `WriteNeoCallSlot`; confirmed the discriminator. No test
coverage for the struct-with-ref-field-param-via-delegate shape.

**Fix.** None required for Step 19 scope (consistent with the prior
deferrals). Record as a pre-existing edge in `neo-deferred-items.md` if not
already there. Flagged for completeness.

---

### F3 — [TRIVIAL] Trailing-whitespace churn in Legacy path (cosmetic)

**File:** `DelegateAdapter.cs:65` (`ctx.SetInvoked(esp); ` -> `ctx.SetInvoked(esp);`).

The 0-arg `FunctionDelegateAdapter.InvokeILMethod` Legacy body lost a trailing
space. Semantically identical; produces a 1-line diff noise in the Legacy
(byte-identical) region. No action needed; noted only because the report claims
"byte-identical" and this is a cosmetic deviation.

---

## Summary table

| # | Sev | File | Probe outcome | Fix |
|---|-----|------|---------------|-----|
| F1 | Major | `DelegateAdapter.cs` `NeoInvokeSub` | interpreter never freed -> pool leak | wrap in try/finally + `FreeILIntepreter` |
| F2 | Minor | `DelegateAdapter.cs` `WriteNeoCallSlot` | struct-with-ref-field param edge (pre-existing) | none (record deferral) |
| F3 | Trivial | `DelegateAdapter.cs:65` | trailing-whitespace churn in Legacy region | none |

## Probes run (independent reproduction)

- Neo `NeoStep19`: **10/10**.
- Neo full `NeoStep` smoke: **140/140, 0 failed**.
- Legacy (plain `Debug`) `NeoStep19`: **10/10**.
- Adversarial `ReviewProbeA` (3-deep multicast + remove-from-middle): green Neo + Legacy (temp, removed).
- Adversarial `ReviewProbeB` (Func multicast last-wins = 50): green Neo + Legacy (temp, removed).
- Adversarial `ReviewProbeC` (nested-nested ForEach fresh-interpreter): green Neo + Legacy (temp, removed).

## Working tree

UNCOMMITTED. Temporary probes REMOVED (the `NeoStep19Test.cs` file is back to
its original TC1-TC10 content; it is an untracked new file, so no diff vs HEAD).

---

## Re-review round 1

**Reviewer:** adversarial, non-author (≠ the F1 fixer). **Date:** 2026-07-06.
**Scope:** ONLY the round-1 F1 fix delta — the `NeoInvokeSub` try/finally +
`FreeILIntepreter` and the F3 whitespace revert — against the round-0 F1
finding. The round-0 F2 (struct-with-ref-field edge) and the autogen patch
audit are OUT OF SCOPE for this round (round-0 accepted them).

### Verdict: APPROVE

The F1 leak is fixed. The `finally` fires on every exit path, the free is
correctly placed (after `ExecuteNeo` returns + after the result is read), the
pool-reclaim is independently reproduced (2 allocs / 999 hits on a 1000-iter
loop, matching the fixer's report), no happy-path regression, Legacy is
byte-identical, and there is zero probe residue.

### 1. `finally` fires on EVERY exit path — CONFIRMED (all 4)

Read `NeoInvokeSub` end-to-end (`DelegateAdapter.cs:1006-1119`). The `try`
spans `:1019-1114`. Inside the `try` there is exactly one `return`, one
`throw`, and no `yield`/`goto`/`break`/`continue` that escapes:

- **(a) Happy path:** `return result;` at `:1113` — `finally` runs, `FreeILIntepreter(intp)` fires, value returned. OK.
- **(b) `next`-chain recursion:** `:1108-1112` `if (next != null) { DelegateAdapter n = (DelegateAdapter)next; result = n.NeoInvokeSub(args); }`. `n` is a DIFFERENT adapter; `n.NeoInvokeSub` does its OWN `RequestILIntepreter`/`try`/`finally`/`FreeILIntepreter`. The recursion frees `n`'s interpreter, NOT the outer `intp`. After `result` is assigned, control falls to `return result` (`:1113`) and the OUTER `finally` frees the outer `intp`. Each link balances its own pair. OK.
- **(c) `if (unhandled) throw` path:** `:1103-1104`. The `throw new Exception(...)` propagates; `finally` fires first (`FreeILIntepreter` runs), THEN the exception escapes to the caller. The interpreter is NOT leaked on an IL-target unhandled exception. OK.
- **(d) Early returns inside try:** NONE. Grepped the `try` body for `return`/`throw`/`yield`/`goto` — only the single `return result` (a) and the single `throw` (c). No bypass path. OK.

Note: `ExecuteNeo` itself catches IL exceptions into the `out unhandled` flag
(Step 14), so it does not throw out of the `try` — the only `throw` source
inside the `try` is the explicit `if (unhandled)` line.

### 2. Free placement — CONFIRMED correct

`FreeILIntepreter(intp)` (`:1117`) runs AFTER:
- `intp.ExecuteNeo(...)` returns (`:1084`),
- the result is read into the local `object result` (`:1086-1097`),
- the mStack teardown (`:1101`),
- the `next`-chain recursion completes (`:1111`).

`result` is a local `object` already detached from the interpreter's frame
before `finally` runs (a boxed value copy via `NeoBoxReturnValue`, or an mStack
reference that has already been resolved to the object). The freed
interpreter's frame/ManagedStack is cleared by `FreeILIntepreter`
(`AppDomain.cs:1638-1640`) but `result` no longer points into it. No use-after-
free of the frame. The `next`-chain recursion frees its OWN interpreter (its
own `intp` local), not the outer one — confirmed by reading the recursive call.
OK.

### 3. Pool-reclaim — INDEPENDENTLY REPRODUCED

Added a TEMPORARY instrumented repro (since reverted):
- counter fields `_rrAllocCount`/`_rrHitCount` in `AppDomain.RequestILIntepreter` (alloc path increments alloc; dequeue path increments hit),
- a temporary 1000-iteration `NeoStep19_ReviewPool1000` test: `List<int>` of 1000 entries, `list.ForEach(self.AddToHolder)` accumulating into a holder (asserts sum = 499500),
- a reflection dump in `Program.cs` (no InternalsVisibleTo needed).

Result on Neo (`Debug_Neo`, net8.0):
```
=== RE-REVIEW POOL PROBE: allocs=2 hits=999 poolSizeEnd=2 (approx peak) ===
Ran 1 tests, 0 failded
```
1000 callbacks → **2 allocations, 999 pool hits**. The pool stays bounded at 2
(one for the outer runtime entry, one for the ForEach-callback interpreter at
peak; steady-state both are reused). This exactly reproduces the fixer's "2
allocs / 999 hits" claim. Pre-fix this would have been ~1001 allocations (1
outer + 1000 leaked callback interpreters), unbounded growth on the delegate
hot path — the F1 defect. The fix resolves it. Pool-reclaim CONFIRMED.

(Reasoning cross-check, independent of instrumentation: `RequestILIntepreter`
dequeues-if-available else allocates `new ILIntepreter(this)`
(`AppDomain.cs:1598-1613`); `FreeILIntepreter` clears ManagedStack/Frames/
Allocator and enqueues (`:1619-1641`). With the try/finally, every request is
balanced by a free on every path, so steady-state pool size = peak concurrency
depth = 2 here. Consistent with the measurement.)

### 4. No happy-path regression — CONFIRMED

Clean-tree smoke (after reverting all temp instrumentation):
- Neo `NeoStep19`: **10/10, 0 failed.**
- Neo full `NeoStep` smoke: **140/140, 0 failed.**

The synchronous results are byte-identical to round-0 (only the lifecycle
changed — interpreter now freed post-callback). Nested-nested ForEach re-entry
(TC7, which recurses `NeoInvokeSub` → `List.ForEach` → `NeoInvokeSub`) still
green: the per-level balanced request/free holds under 2-level nesting.

### 5. Legacy byte-identical (F3) — CONFIRMED

The F3 trailing-space deviation is REVERTED: working-tree `:65`
`ctx.SetInvoked(esp); ` matches HEAD `:62` `ctx.SetInvoked(esp); ` byte-for-
byte (trailing space preserved on the 0-arg `FunctionDelegateAdapter` path;
the other 4 `ctx.SetInvoked(esp);` lines also byte-identical to HEAD).

Diff isolation: the entire `DelegateAdapter.cs` diff vs HEAD reduces to (a)
additive `#if ENABLE_NEO_MODE`/`#else`/`#endif` guards around the per-arity
`InvokeILMethod` bodies and (b) the additive Neo region (`NeoInvoke`/
`NeoInvokePublic`/`NeoInvokeSub`/`WriteNeoCallSlot`). ZERO Legacy-body lines
changed — the `#else` paths are byte-identical to HEAD.

- Plain `Debug` CLI builds clean (0 errors).
- Legacy (plain `Debug`) `NeoStep19`: **10/10, 0 failed.**

### 6. No probe residue — CONFIRMED

- `Program.cs`: byte-identical to HEAD (`git diff --stat` empty; only a CRLF/LF warning, no content). No `RE-REVIEW POOL PROBE` / counter reads.
- `AppDomain.cs`: ONLY the 6 legitimate Step 19 lines (`#if ENABLE_NEO_MODE RegisterCLRMethodRedirectionNeo(...)` for Combine + Remove). ZERO `_rrAllocCount`/`_rrHitCount` (grep = 0 hits). (Note: the fixer's AppDomain.cs change is the Step 19 redirect registration, NOT probe residue — verified it is the round-0/Step 19 work, present before re-review.)
- `NeoStep19Test.cs`: back to TC1-TC10 (10 `public static void NeoStep19_*` methods, grep count = 10). ZERO `ReviewPool1000`/`AddToHolder`/`h_Field` (grep = 0 hits).

### Build + test matrix (clean tree)

| Config | Build | Smoke |
|--------|-------|-------|
| CLI `Debug_Neo` + TestCases `Debug` (Neo) | 0 err | NeoStep19 10/10; NeoStep 140/140 |
| CLI plain `Debug` (Legacy) | 0 err | NeoStep19 10/10 |

### New findings (this round)

NONE. No Blocker, no Major, no Minor introduced by the F1 fix. The try/finally
is the minimal correct shape; the redundant `mStack.RemoveRange(mStackBase,...)`
(`:1101`) is harmless (round-0 already noted it becomes redundant once the free
clears ManagedStack, but keeping it is fine — it runs before the free, tearing
down this invoke's reservation explicitly).

F1 (round-0 Major): **RESOLVED.** Round-0 F2 (Minor, pre-existing struct-with-
ref-field edge) and F3 (Trivial) remain as round-0 left them (F2 deferred, F3
reverted) — out of scope this round, no change.

**Verdict: APPROVE. The change is ready for archive.**

