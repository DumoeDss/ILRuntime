# Review Report — neo-il-exception-throw (D-IL-EXCEPTION-THROW)

**Reviewer:** NON-AUTHOR adversarial review (working-tree UNCOMMITTED).
**Date:** 2026-07-05
**Branch:** `features/object-model-overhaul`
**Skill:** `openspec-gstack-review`

## Scope Check

**CLEAN.** Intent: close D-IL-EXCEPTION-THROW — make an IL `class X :
System.Exception` (a) LOAD and (b) be THROWN + CAUGHT end-to-end on both
engines. Delivered: exactly that. Diff is 4 production files + 1 test file +
planning notes. No scope creep; no missing requirements. The `.gitignore`
addition (`.mcp.json`) and the planning-context.md note are benign additions.

## VERDICT: **APPROVE**

No Blocker. No Major. One Minor (methodology note on the exoneration claim, no
behavioral impact). A handful of Trivial doc/comment nits. The fix is
correct, minimal, mirrors established prior art (`AttributeAdapter`), and is
Legacy-neutral as claimed. Green smoke reproduced independently (Neo 108/108;
Legacy 8/8 ILEx + the 3 pre-existing NeoStep14 TC failures confirmed
unrelated).

---

## Mandatory adversarial probe results

### 1. ExceptionAdaptor shape — **MIRRORS AttributeAdapter EXACTLY**

`ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs` is structurally byte-for-byte
parallel to `ILRuntime/Runtime/Adaptors/CLRCrossBindingAdaptors.cs:AttributeAdapter`:

| Element | AttributeAdapter | ExceptionAdaptor | Match |
|---|---|---|---|
| `AdaptorType` override → `typeof(Adapter)` | yes | yes | ✓ |
| `BaseCLRType` override → `typeof(Base)` | `Attribute` | `Exception` | ✓ |
| `CreateCLRInstance(appdomain, instance)` → `new Adapter(...)` | yes | yes | ✓ |
| Nested `Adapter : Base, CrossBindingAdaptorType` | yes | yes | ✓ |
| Fields `ILTypeInstance instance; AppDomain appdomain;` | yes | yes | ✓ |
| `bool isToStringGot; IMethod toString;` cache | yes | yes | ✓ |
| Ctor `(AppDomain, ILTypeInstance)` assigning both fields | yes | yes | ✓ |
| `ILInstance` getter → `instance` | yes | yes | ✓ |
| `ToString()` forwarding (GetVirtualMethod on `ObjectType.ToString`, ILMethod
  → `instance.ToString()`, else `Type.FullName`) | yes | yes | ✓ |

**Base-ctor chain validity:** the `Adapter` ctor declares no explicit
`: base(...)`, so it invokes the implicit parameterless `System.Exception()`
ctor. `System.Exception` exposes a public parameterless ctor on netstandard —
**valid**. Build is 0-errors on both `Debug` and `Debug_Neo`. No issue.

**No `Message` forwarding** — by design (OQ2). Both `Message` paths are blocked
by pre-existing Neo gaps (separate follow-ups); the minimal adaptor mirrors
`AttributeAdapter` precisely. This is the correct minimal choice and is
clearly documented.

### 2. Registration collision / double-register — **NO COLLISION; GUARD EXISTS**

- `AppDomain.RegisterCrossBindingAdaptor`
  (`AppDomain.cs:1981-2003`) **has a double-registration guard**:
  `if (!crossAdaptors.ContainsKey(bType))` else `throw new Exception("...
  already added.")`. So a consumer who registers a *second* `System.Exception`
  adaptor will get a clear exception, not silent override. **Caveat (Minor —
  behavior change for downstream consumers):** because the built-in now
  pre-registers `System.Exception` in the ctor, any external consumer (Unity
  host, the `.neo` AOT loader, a third-party harness) that previously
  registered its own `System.Exception` adaptor will now hit the guard's
  throw at startup. This is the correct enforced-uniqueness behavior, but it
  IS a breaking change for such a consumer. No consumer in THIS repo does so
  (verified: `grep` of `ILRuntimeTestBase/`, `TestCases/`, and the harness
  `helper.cs:22-29` registers `ClassInheritanceTestAdaptor`,
  `InterfaceTestAdaptor`, `TestClass2-4Adapter`, `IDisposableAdapter`,
  `ClassInheritanceTest2Adaptor`, `IAsyncStateMachineClassInheritanceAdaptor`
  — none for `System.Exception`). The planning-context flags the AOT-loader
  follow-up. Acceptable for this change.
- **No pre-existing test asserts `TypeLoadException` for `System.Exception`
  inheritance** (grep of TestCases/ILRuntimeTestBase found none), so flipping
  the load behavior from "TypeLoadException" to "loads" breaks no existing
  expectation.

### 3. Throw-unwrap edge cases

| Edge case | Behavior | Correct? | Matches Legacy? |
|---|---|---|---|
| (a) `throw null` (null operand) | Neo: `o as Exception` null, `o is ILTypeInstance` false (null is not ILTypeInstance) → `if (ex==null) throw NullReferenceException()`. Legacy: `throw ex` where ex null → CLR NullReferenceException. | ✓ NRE | ✓ both NRE |
| (b) `throw` a plain IL class (no Exception base) | `o as Exception` null; `o is ILTypeInstance` true; `ili.CLRInstance as Exception`: for a non-adaptor IL type `CLRInstance == this` (`ILTypeInstance.cs:360`) → `as Exception` null → Neo NRE / Legacy throw-null→NRE. | ✓ NRE (throwing non-Exception is invalid) | ✓ both NRE. **Unreachable from C#** (compiler requires Exception-typed operand); defensive only. |
| (c) `throw` a CLR `Exception` (existing path) | First `o as Exception` succeeds; IL fallback UNREACHABLE. | ✓ byte-identical | ✓ — confirmed: TC1/TC5/TC8 throw `DivideByZeroException`/NRE and the unwrap fallback never runs for them (their failures are unrelated `ArgumentOutOfRangeException` in `List.set_Item`). |
| (d) `throw` an IL exception whose `CLRInstance` is null | Should not happen (ctor always sets CLRInstance, `ILTypeInstance.cs:352-372`). If it did: `ili.CLRInstance as Exception` → null → Neo NRE / Legacy throw-null → NRE. Graceful. | ✓ fails closed (NRE), not silent swallow | ✓ |

### 4. OQ1 catch-hierarchy adversarial

- **(a) catch ordering derived-before-base (probe 3.4):** returns 9 — the
  `catch (DerivedEx)` clause wins over `catch (MyEx)` for a thrown
  `DerivedEx`. Nearest-match two-pass dispatch works.
- **(b) `catch (Exception)` catching an IL exception (probe 3.2):** returns
  9. `CheckExceptionType` CLRType arm, `IsAssignableFrom(typeof(Adapter))`
  true.
- **(c) exception filter (`when`):** NOT probed — out of scope (IL
  `filter`/`endfilter` remain NIE per design Non-Goals). Acceptable.
- **(d) rethrow `throw;` (probe 3.5):** returns 9 — inner catch rethrows,
  outer `catch (MyEx)` sees it, post-rethrow code (`return -100`) does NOT
  run. Re-thrown object stays the same Adapter.
- **(e) "read IL field off caught exception" gap (probe 3.7 workaround):**
  **PRE-EXISTING, confirmed — NOT introduced by this change.** The gap is in
  `ExecuteNeo`'s callvirt-on-CLR-interface / `appdomain.Invoke`
  instance-method / `ILTypeInstance` indexer machinery (all documented in
  design OQ2). This change only (1) registers an adaptor and (2) adds an
  unwrap fallback — neither touches those paths. The implementer's `e is MyEx`
  (isinst) workaround is sound (isinst is the same opcode the catch matcher
  uses, known-good on the Adapter). Correctly recorded as a follow-up
  dependent on Neo callvirt/indexer support.

### 5. Shared-engine Legacy-neutral — **CONFIRMED**

- Legacy ILEx probes (plain `Debug` + `useRegister=true`): **8/8 pass**.
- Legacy NeoStep14 filter: **17 ran, 3 failed** — and the 3 are exactly
  `NeoStep14_TC1_BasicTryCatch`, `NeoStep14_TC5_NestedInnermostWins`,
  `NeoStep14_TC8_NullRefCatch`, each failing with
  `System.ArgumentOutOfRangeException` in `List.set_Item`. These tests throw
  **CLR** exceptions (`DivideByZeroException`) / NRE — they never reach the IL
  fallback in the unwrap. The Throw-unwrap is exonerated for them: the first
  `o as Exception` succeeds and the new branch is unreachable.

  **Methodology note (Minor, no behavioral impact):** the implementer's claim
  "the 3 NeoStep14 TC failures reproduce with the ORIGINAL Throw arm" is
  likely correct in substance, but I could not reproduce TC1/TC5/TC8 on a
  pure-HEAD tree *while the new test source is present*: `MyEx :
  System.Exception` in the assembly causes `TestSession.LoadTest()`
  (`TestSession.cs:91`) to throw an unhandled `TypeLoadException: Cannot find
  Adaptor for:System.Exception` during type initialization for ANY type's
  method enumeration, crashing the whole session before any test runs. So the
  stash-toggle proof is actually *stronger* than reported: on HEAD-with-tests
  the session hard-crashes at load; after the fix it loads and all probes
  pass. To run TC1/TC5/TC8 on pure HEAD one must also revert the test
  additions. The substantive exoneration (TC1/TC5/TC8 failures are
  `ArgumentOutOfRangeException`, not Throw/NRE) holds regardless — verified
  by reading the test bodies (TC1/TC8 throw CLR exceptions; the unwrap's IL
  branch is unreachable for them).

### 6. Neo 108/108 — **REPRODUCED INDEPENDENTLY**

`Ran 108 tests, 0 failed, 0 ignored, 0 todos` (`Debug_Neo`, filter `NeoStep`).
100 baseline + 8 `NeoStep14_ILEx_*`, each returning 9.

### 7. Probe validity — **ALL EXERCISE THROW+CATCH END-TO-END**

Each of the 8 `NeoStep14_ILEx_*` returns 9 (the success sentinel) and the
success sentinel is only reachable inside a `catch` block that follows a
`throw new MyEx()/DerivedEx()`. A swallowed-or-escaped exception could not
produce 9 (escape → harness counts as failure; swallow → returns -1).
Confirmed each test's control flow: the `9` return is gated by entering the
catch clause, which requires the throw to have propagated. No false greens.

**Minor note:** `NeoStep14_ILEx_ThrowingCallee` (the void helper used by
3.6) is `public static` and matches the `NeoStep14_ILEx_` filter, so it gets
"Invoked" as a candidate test — it throws every time and produces no
`Return:` line, yet the harness still reports "0 failed / Ran N tests" with N
not counting it. This is benign (the helper's throw is its contract and the
harness evidently excludes void-throwing helpers from the pass/fail tally),
but it is mildly confusing in the log. Not a defect.

---

## Findings

### Blocker
(none)

### Major
(none)

### Minor

- **[M1] Exoneration methodology (no behavioral impact).** The planning-
  context claim "3 NeoStep14 TC failures reproduce with the ORIGINAL Throw
  arm" is substantively correct (the failures are
  `ArgumentOutOfRangeException`, unrelated to Throw), but it is not literally
  reproducible on HEAD *with the new test source present* because
  `MyEx : System.Exception` crashes `TestSession.LoadTest()` at type init.
  Suggested fix to the planning-context note: clarify that the exoneration
  was obtained by reverting BOTH the production code AND the test additions
  (or by reading the test bodies to confirm the unwrap's IL branch is
  unreachable for CLR-exception-throwing tests). File:
  `openspec/changes/neo-completion-portfolio/planning-context.md` (apply
  section).

- **[M2] Breaking-change callout for downstream consumers (doc-only).**
  Registering `ExceptionAdaptor` as a built-in means any external consumer
  that previously registered its own `System.Exception` adaptor will now hit
  the `RegisterCrossBindingAdaptor` guard throw at startup. This is correct
  enforced-uniqueness, but should be called out in the ship-log /
  `neo-handoff.md` as a behavior change. No repo consumer is affected
  (verified).

### Trivial

- **[T1] `NeoStep14_ILEx_ThrowingCallee` log noise.** The void helper is
  matched by the `NeoStep14_ILEx_` filter and "Invoked" per-run, producing a
  no-`Return:` line. Cosmetic only. (Could be renamed/moved or marked
  `[NonTest]` if the harness supports it, but not required.)

- **[T2] Typo `failded`** in CLI result line (`Ran N tests, X failded, ...`)
  — pre-existing harness typo, not introduced here. Noted in passing.

---

## Summary table

| Probe | Result |
|---|---|
| 1. ExceptionAdaptor shape mirrors AttributeAdapter + valid base ctor | ✓ CONFIRMED |
| 2. Registration collision / double-register guard | ✓ Guard exists; no collision in repo; downstream-break callout (M2) |
| 3. Throw-unwrap edge cases (null / plain-IL / CLR / null-CLRInstance) | ✓ All correct + Legacy-matched |
| 4. OQ1 catch hierarchy (ordering / base / rethrow) | ✓ All green; `when` filter out of scope |
| 5. "Read IL field off caught ex" gap pre-existing | ✓ Confirmed pre-existing (Neo callvirt/indexer gaps) |
| 6. Neo 108/108 | ✓ REPRODUCED |
| 7. Probe validity (end-to-end throw+catch) | ✓ All 8 genuine |
| 8. Legacy-neutral | ✓ ILEx 8/8; NeoStep14 3 failures are pre-existing & unrelated |

**Working tree left UNCOMMITTED** as required.
