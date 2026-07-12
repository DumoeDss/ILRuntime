# Review Report — `neo-il-enum-getenumvalues`

**Reviewer:** author != verifier gate (dispatched leaf reviewer, Tier A)
**Date:** 2026-07-12
**Branch:** `features/object-model-overhaul`
**Change:** IL-enum reflection virtuals on `ILRuntimeType` (child 18 of `neo-overhaul`)
**Diff:** `ILRuntime/Reflection/ILRuntimeType.cs` (+52), `TestCases/NeoStepIlEnumGetValuesTest.cs` (new, 3 probes)

---

## Verdict: **APPROVE-WITH-FINDINGS**

The fix is correct for its stated scope, ships its goal cleanly (kills the bare
`NotImplementedException` for `Enum.GetValues` on IL enums), is genuinely shared /
Legacy-neutral, and introduces no regression. One **Major** faithfulness gap (signed
vs. unsigned-binary sort for negative-valued enums) and two **Minor** doc-accuracy
items should be resolved — the Major by an explicit decision (cheap fix or
accept-and-document), the Minors before archive.

---

## Findings

### [MAJOR] Signed sort diverges from the framework's unsigned-binary sort for negative-valued enums
`ILRuntimeType.cs` `GetSortedEnumMembers()`:
```csharp
pairs.Sort((a, b) => ((IComparable)a.Value).CompareTo(b.Value));
```
`a.Value` / `b.Value` are `FieldDefinition.Constant` — boxed values of the underlying
type. For an `int`-backed enum, `Int32.CompareTo(object)` is a **signed** comparison.
The .NET framework's `System.Enum.GetValues` / `GetNames` (and the
`Type.GetEnumValues()` / `GetEnumNames()` virtuals on `RuntimeType`) sort by the
**unsigned binary value** of the underlying type.

**Empirically verified** (isolated `dotnet` console probe against .NET 8):
- `enum SignedMixed { A=-1, B=0, C=1 }` → framework returns names `[B,C,A]`, values `[0,1,-1]`
  (the negative sorts **last** — unsigned). The implementer's signed sort would yield
  `[A,B,C]` / `[-1,0,1]`.
- `enum NegBig { Hi=-5, Mid=7, Lo=int.MaxValue }` → framework returns `[Mid,Lo,Hi]` /
  `[7,2147483647,-5]` (again negative last). Signed sort would give `[Hi,Mid,Lo]`.

So the code comment's claim — *"Sorted by the underlying value to mirror
System.Enum.GetValues/GetNames ... (the framework delegates to these virtuals and does
not re-sort)"* — is correct that the framework sorts by value (NOT declaration order),
but the comparison is **signed here, unsigned on the framework**. Under the
implementer's own (correct) premise that the framework does not re-sort the virtual's
result, the signed order is what the IL program sees, and it diverges from the
framework contract for any enum containing a negative member.

**Blast radius:** limited — only IL enums with negative member values; the common
(non-negative) case is identical. No test fails (the probe enum `A=0,B=10,C=20` is
all-non-negative ascending, so signed == unsigned == declaration order and the probe
cannot distinguish them).

**Recommendation (decision for LEAD):** either (a) sort by unsigned magnitude, e.g.
`pairs.Sort((a,b) => Convert.ToUInt64(a.Value).CompareTo(Convert.ToUInt64(b.Value)));`
which matches the framework across all integer underlying types; or (b) explicitly
accept the limitation and note it here. Cheap, tractable. Classed Major because it is
wrong behaviour on a plausible path (negative enum values — error codes, flags with a
-1 sentinel — are common) that breaks a documented framework contract, not because any
test currently fails.

### [MINOR] Spec delta encodes "declaration order"; implementation (correctly) sorts by value
`specs/neo-type-checks/spec.md` REQUIREMENT text:
> `GetEnumValues()` SHALL return ... the enum's named members' constant values, **in
> declaration order**.
> `GetEnumNames()` SHALL return the enum's member names **in declaration order**.

The implementation does **not** use declaration order — it sorts by value (the
framework-faithful behaviour, which is more correct than the spec text). The scenarios
pass only because the probe enum is ascending (declaration order == value order). Since
this is a durable spec delta that merges into `openspec/specs/neo-type-checks/spec.md`
on archive, update "declaration order" → "ordered by their underlying value" to match
both the framework and the implementation. The same inaccuracy appears in `design.md`
Risks ("Enum.GetValues/GetNames return members in declaration (Cecil) order; the
framework does not sort" — false; the framework does sort, by unsigned binary value).

### [MINOR] Proposal + test-file comments over-state the failing surface ("all three paths fault on HEAD")
`proposal.md` "Why" and `NeoStepIlEnumGetValuesTest.cs` header / per-probe comments
assert that `GetNames` and `GetEnumUnderlyingType` **also** throw the bare NIE on HEAD.
Empirically (stash-toggle, overrides removed): only `GetEnumValues()` throws the bare
NIE; `GetNames` and `GetEnumUnderlyingType` already work on HEAD via `ILRuntimeType`'s
existing `GetFields` override. So `GetEnumValues` is the **load-bearing** override and
TC2/TC3 are **defensive** (non-fault-on-HEAD) probes — consistent with the dispatch's
framing ("TC2/TC3 pass on HEAD too (defensive overrides)"), not with the proposal text.
No impact on the fix's correctness; rationale text should be corrected.

---

## 1. The 4 overrides correctness

| Override | Verdict | Evidence |
|---|---|---|
| `public override bool IsEnum => type.IsEnum;` | **Correct** | Delegates to `ILType.IsEnum` = `definition.IsEnum` (`ILType.cs:2010-2016`, true for an IL enum). Defensive (base resolved true on HEAD — the bare-NIE reached `GetEnumValues()`, not an `ArgumentException` — but explicit removes doubt). |
| `public override Type GetEnumUnderlyingType()` | **Correct** | Guards `if (!type.IsEnum) throw ArgumentException`; returns `type.TypeForCLR`, which for an enum returns `enumType.TypeForCLR` (`ILType.cs:1966-1970`) — the underlying CLR primitive (`enumType` set in `InitializeFields`, `ILType.cs:2979`). Matches `RuntimeType.GetEnumUnderlyingType`. |
| `public override Array GetEnumValues()` | **Correct (type/values); ordering caveat (see Major)** | `Array.CreateInstance(GetEnumUnderlyingType(), n)` then `SetValue(pairs[i].Value, i)`. Element type is the underlying type (matches `Enum.GetValues`'s contract); values are the boxed constants. Order is value-sorted (good, not declaration order) but **signed** (see Major). |
| `public override string[] GetEnumNames()` | **Correct (names); ordering caveat (see Major)** | Names collected from the same value-sorted member list; mirrors `GetEnumNames` order. Same signed-sort caveat. |

**Member enumeration (`GetSortedEnumMembers`):** filter `f.IsLiteral && f.HasConstant`
is **correct** — the enum's named constants are static literal fields with a constant;
the `value__` backing field is a non-literal instance field and is correctly excluded.
Value source `f.Constant` is the same Cecil accessor already trusted by
`ILRuntimeFieldInfo.GetRawConstantValue` (`:168`). Non-enum misuse throws
`ArgumentException` via the guard — matches the base `System.Type` contract.

**Value-sorting (the dispatch's key correctness ask):** **CONFIRMED that it sorts by
value, NOT declaration order** (the design's "declaration order" note was indeed wrong
and the implementer corrected it). Refinement: the comparison is **signed** via
`IComparable`, whereas the framework sorts by **unsigned binary** value — divergent for
negative members (Major). For all-non-negative enums the two coincide.

---

## 2. Legacy-neutral (shared reflection code)

**Confirmed Legacy-neutral.** No `#if ENABLE_NEO_MODE` in the diff — the overrides are
in shared reflection code and apply to Legacy identically.

- Plain `Debug` + `useRegister=true` + `NeoStep` filter: **351 ran / 17 failed** == the
  documented Legacy NeoStep baseline (the 17 are pre-existing NeoStep tests Legacy
  cannot execute; the count is unchanged by this change).
- All 3 new probes (`NeoStepIlEnumGetValues_TC1/TC2/TC3`) **PASS under Legacy** (they
  are invoked and are absent from the failure list) — the shared fix benefits Legacy
  too, exactly as the design predicts.
- The 8 `bare-NIE` occurrences under the Legacy NeoStep run come from **unrelated**
  baseline failures — `NeoStep16Test.NeoStep16_TC8_StelemI_NIntArray`,
  `NeoStepClrStaticFieldTest.NeoStepClrStatic_TC3_IntPtrZeroVtRead`, etc. — none
  involving `GetEnumValues`. These are part of the pre-existing 17-failure set, not
  introduced or affected by this change.
- Net: the change added 3 passing tests under Legacy (NeoStep-filter count 348→351,
  failures held at 17) and broke/fixed nothing — failure set preserved.

---

## 3. Gates re-run

- **Build CLI** `dotnet build ILRuntimeTestCLI ... -c Debug_Neo --no-incremental`:
  **0 errors** (270 warnings — the known MSB3277/CS1668 env noise, unrelated).
- **Build TestCases** `dotnet build TestCases ... -c Debug`: **0 errors**.
- **NeoStep smoke** (`Debug_Neo`, `true`, `NeoStep`): **Ran 351, 0 failed**. Matches
  the expected 348 prior + 3 new.
- **Reflection / type-check tests unregressed:** `NeoStep15Test` (isinst / castclass)
  cases — `NeoStep15_TC1_IsTrueOnDerived`, `NeoStep15_TC2_IsFalseOnUnrelated`,
  `NeoStep15_TC3_AsInterface` — invoked and green (part of the 351/0). `NeoStep14`
  (castclass) likewise covered by the 0-failure NeoStep run. No reflection/type-check
  regression.

---

## 4. Stash-toggle evidence (TC1 load-bearing)

1. `git stash push -- ILRuntime/Reflection/ILRuntimeType.cs` (overrides removed; diff
   empty). Rebuild CLI `Debug_Neo` clean.
2. Run the 3 probes (`true NeoStepIlEnumGetValues`): **Ran 3, 1 failed.**
   - `NeoStepIlEnumGetValues_TC1` **FAULTS** with
     `System.NotImplementedException: The method or operation is not implemented. ---
     at System.Type.GetEnumValues()` → **TC1 is load-bearing (faults on HEAD).**
   - `TC2` and `TC3` **pass on HEAD** → defensive overrides (the framework already
     answers `GetNames` / `GetUnderlyingType` via the existing `GetFields` override).
3. `git stash pop` (overrides restored; `IsEnum` override line present). Rebuild clean.
4. Re-run the 3 probes: **Ran 3, 0 failed.** Working tree restored to the implementer's
   state.

**Probe validity:** TC1 is load-bearing (FAULTs on HEAD, PASSes after). TC2/TC3 are
defensive (pass on HEAD; guard against future regressions but did not fault pre-fix).
TC3's `ut.FullName != "System.Int32"` assertion is **sound** — `GetUnderlyingType`
returns the actual CLR `typeof(int)` (via `enumType.TypeForCLR`), and `FullName` is a
string value-comparison, robust to the Neo Type-return reference-inequality quirk the
implementer noted. Confirmed passing under both Neo and Legacy.

---

## 5. Full-smoke bare-NIE spot check

Enum-filtered Neo smoke (`true Enum`): **Ran 40, 11 failed — bare-NIE count = 0**
(the string *"The method or operation is not implemented."* does **not** appear at all).

- `EnumTest.Test01` (the canonical `Enum.GetValues` on an IL enum) is invoked and is
  **not** in the failure list — it passes (was bare-NIE on HEAD).
- The 9 `GetEnum` textual hits in the log are JIT/optimizer disassembly trace lines
  (`call ..., System.Enum.GetValues`), **not** failure messages.
- The 11 residual failures are unrelated Neo enum / CLR-enum issues —
  `EnumTest.Test11` (ToString string mismatch), `.Test20/.Test22` (ILEnumTypeInstance /
  ILTypeInstance → `System.Enum` cast), `.Test30/.Test32` (Neo callvirt VTable
  `Equals` slot on flag enums), `.Test15` (Neo callvirt `GetType` slot), `.Test33`
  (generic Exception), `GCTest.TestDicEnumerator` (the known Dict-NRE), and
  `RefOutTest.UnitTest_RefCLREnum` / `TestCLREnum.Test06` (CLR-enum, unrelated). **None
  is a `GetEnumValues` / `GetEnumNames` / `GetEnumUnderlyingType` regression.**

The `Enum.GetValues` bare-NIE surface is gone.

---

## Standards / Spec axes

- **Standards:** PASS (with the Major faithfulness caveat). Code is clean, additive,
  no concurrency issues (fresh list per call), correct guards, no magic numbers, no
  dead code. Filter and value-source mirror the precedent in `ILRuntimeFieldInfo`.
- **Spec:** PARTIAL. All four `spec.md` scenarios are satisfied by the run (the probe
  is green and matches every asserted value/name/underlying-type). But the
  REQUIREMENT text says "declaration order" while the implementation (correctly) sorts
  by value — the durable spec text must be corrected before archive (Minor).

---

## Summary for the LEAD

- **Verdict:** APPROVE-WITH-FINDINGS
- **4 overrides correct?** Yes — types, values, names, filter, underlying-type, guards
  all correct. **Value-sort: confirmed value-based (not declaration order), but signed
  vs. the framework's unsigned-binary — Major for negative enums.**
- **Legacy-neutral?** Yes — shared code, no Neo gate; 351/17 baseline held, 3 probes
  pass under Legacy, failure set unchanged.
- **Stash-toggle:** TC1 FAULTs on HEAD (`System.Type.GetEnumValues()` bare NIE) → PASS
  after; TC2/TC3 defensive (pass on HEAD). Tree restored.
- **NeoStep:** 351/0. Reflection/type-check (NeoStep15 isinst/castclass, NeoStep14)
  unregressed.
- **Full-smoke bare-NIE:** gone (Enum-filtered smoke bare-NIE count = 0; `EnumTest.Test01`
  passes; residuals unrelated).
- **Reflection tests unregressed?** Yes.

**Durable findings:** (1) Root cause confirmed — `ILRuntimeType` overrode none of the
`GetEnum*` virtuals so `Enum.GetValues(IL enum)` hit the base `System.Type.GetEnumValues()`
bare NIE; only `GetEnumValues` was load-bearing (`GetNames`/`GetUnderlyingType` already
worked via the existing `GetFields` override). (2) The value-sort is **signed**, not
the framework's **unsigned-binary** — divergent for negative-valued enums (Major, no
test covers it). (3) Shared non-Neo-gated code → Legacy benefits identically (3 probes
pass under Legacy, 17-failure baseline preserved).

---

## Re-review round 1 (MAJOR unsigned-sort fix delta)

**Reviewer:** fresh delta re-reviewer (author != verifier, Tier A)
**Date:** 2026-07-12
**Delta under review:** the round-0 MAJOR fix — `GetSortedEnumMembers` sort changed
from signed `((IComparable)a.Value).CompareTo(b.Value)` to unsigned-binary
`EnumValueAsUnsignedBits(a.Value).CompareTo(EnumValueAsUnsignedBits(b.Value))` via a new
per-type unchecked-cast helper; + `NeoStepIlEnumProbeSigned { N=-1,Z=0,P=1 }` / `TC4`
asserting `Enum.GetValues` returns `[0,1,-1]` (unsigned order); + `spec.md` wording
"declaration order" -> "ordered by unsigned binary value (framework parity)".

### MAJOR resolved (Neo target): CONFIRMED

The sort is now unsigned-binary-value with framework parity, **including for negative
members**. For `NeoStepIlEnumProbeSigned { N=-1, Z=0, P=1 }` the unsigned sort yields
`[Z(0), P(1), N(-1)]` — i.e. `[0,1,-1]` — exactly what TC4 asserts and exactly what the
.NET 8 framework returns. The round-0 signed compare would have yielded `[-1,0,1]`
(declaration order).

**Stash-toggle evidence (load-bearing, Neo):**
1. Revert ONLY the sort line to signed `((IComparable)a.Value).CompareTo(b.Value)`
   (helper + TC4 left intact). Rebuild CLI `Debug_Neo --no-incremental` (0 errors).
2. Run `... true NeoStepIlEnumGetValues` -> **`Ran 4 tests, 1 failed`**. TC4 FAULTS with
   `System.DivideByZeroException` at `NeoStepIlEnumGetValuesTest.cs:111` (the
   `(int)values.GetValue(0) != 0` check body) — because the signed sort puts `N(-1)`
   first, so `GetValue(0) == -1 != 0` fires the deliberate `1/0`. TC1/TC2/TC3 still pass
   (their non-negative probe is identical under signed/unsigned). **Signed -> TC4 FAIL,
   as predicted.**
3. Restore the unsigned sort. Rebuild `Debug_Neo` (0 errors). Re-run -> **`Ran 4 tests,
   0 failed`**. **Unsigned -> TC4 PASS.**

This is a clean signed-FAIL / unsigned-PASS discrimination on the exact negative-member
path the round-0 Major identified. The helper is genuinely load-bearing (TC4 cannot pass
without it).

### EnumValueAsUnsignedBits — 8 integer types CORRECT

`ILRuntimeType.cs:697-710` switches on `System.Convert.GetTypeCode(value)` and reinterprets
the raw bits to `ulong` per underlying type:

| TypeCode | Cast | -1 -> bits | Verdict |
|---|---|---|---|
| `SByte` | `(ulong)(sbyte)value` | 0xFFFF…FF (last) | correct |
| `Int16` | `(ulong)(short)value` | 0xFFFF…FF (last) | correct |
| `Int32` | `(ulong)(int)value` | 0xFFFF…FF (last) | correct (the TC4 path) |
| `Int64` | `(ulong)(long)value` | 0xFFFF…FF (last) | correct |
| `Byte` | `(byte)value` | (zero-extended) | correct |
| `UInt16` | `(ushort)value` | (zero-extended) | correct |
| `UInt32` | `(uint)value` | (zero-extended) | correct |
| `UInt64` (default) | `(ulong)value` | direct | correct |

- **Unchecked arithmetic confirmed:** no `<CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>`
  in any `*.csproj` (grep), and no `checked` block wraps the helper — so the signed->`ulong`
  casts reinterpret bits (e.g. `(ulong)(int)(-1) == 0xFFFFFFFF`) rather than throwing
  `OverflowException`. This is precisely why the LEAD's first `Convert.ToUInt64` attempt
  failed (it overflow-checks and threw on the negative member, TC4 352/1); the per-type-cast
  helper is the correct fix.
- **`Convert.GetTypeCode` dispatch is correct for the boxed Cecil value:**
  `GetSortedEnumMembers` reads `FieldDefinition.Constant`, which boxes the enum member as
  its underlying primitive (the same accessor `ILRuntimeFieldInfo.GetRawConstantValue`
  trusts). For TC4's int-backed enum `Convert.GetTypeCode(boxed int -1) == Int32`, so the
  `Int32` arm `(ulong)(int)value` is taken. Verified end-to-end by the unsigned-PASS run.
- The `default` arm (`(ulong)value`) safely catches `UInt64` only: upstream
  `f.IsLiteral && f.HasConstant` guarantees a non-null constant, and an enum's underlying
  type is always one of the 8 integer type codes, so no other type code is reachable.

### spec.md "unsigned binary value" correction: ACCURATE

`specs/neo-type-checks/spec.md` REQUIREMENT text now reads "ordered by UNSIGNED binary
value (parity with `System.Enum.GetValues`/`GetEnumNames`, which sort by unsigned binary
value, NOT declaration order -- a negative member sorts LAST)". This matches both the
.NET framework contract and the implementation (and TC4 encodes it). The round-0 Minor
on the durable spec text is resolved.

### NeoStep smoke + reflection unregressed

- `Debug_Neo` + `true` + `NeoStep`: **`Ran 352 tests, 0 failed`** (351 prior + TC4).
- Reflection / type-check cases unregressed under Neo: `NeoStep15` (isinst/castclass) and
  `NeoStep14` (castclass/try-catch) are part of the 352/0 green run. (Their Legacy
  failures noted below are pre-existing, not Neo.)

### Legacy-neutral: fix is regression-free; NEW pre-existing gap exposed by TC4

The unsigned-sort fix introduces **zero** Legacy regressions. But the round-0 task's
criterion ("plain `Debug` NeoStep failure set == baseline") shows a `+1`: **352 ran /
18 failed** vs the round-0 Legacy baseline of 351/17. The new failure is TC4 itself.

Root-caused and proven **pre-existing, not caused by this fix**:

- Under Legacy (plain `Debug` + register mode), TC4 FAILS at the same line 111
  (`GetValue(0) != 0`) — the array starts with `-1` (declaration order `[-1,0,1]`),
  NOT the unsigned order `[0,1,-1]`.
- **Discriminator:** I re-ran TC4 under Legacy with the sort reverted to the SIGNED
  compare. TC4 fails **identically** (line 111, `GetValue(0) != 0`). Since the unsigned
  sort **cannot** put `-1` first (`(ulong)(int)(-1) == 0xFFFFFFFF` sorts last), and both
  signed and unsigned fail the same way under Legacy, the `GetSortedEnumMembers` sort —
  and therefore the `ILRuntimeType.GetEnumValues()` override — is **provably not the
  code path producing the array under Legacy**. The Legacy `Enum.GetValues(IL enum)`
  returns **declaration order** regardless of the override/sort (a typeof / Type-wrapper
  resolution difference: the `ILRuntimeType.GetEnumValues()` virtual is not dispatched
  under Legacy; the framework falls back to a `GetFields`-style declaration-order
  enumeration). TC1 passes under Legacy only because its non-negative
  `{A=0,B=10,C=20}` has declaration order == value order.
- The 17 other Legacy NeoStep failures are the pre-existing Neo-specific set (async /
  delegate / Neo-IL-extension / Step-19/20 cases Legacy cannot execute) — unchanged by
  this change. TC4 is the sole delta, and it exposes a gap that exists with or without
  the unsigned-sort fix.

So: the fix is **Legacy-neutral** (additive shared code, no regression); the new TC4
probe is simply **not Legacy-green** because Legacy never reaches the override for
`GetValues`. The change is Neo-scoped (the bare-NIE surface was Neo; round-0 and the
design target Neo), so this does not invalidate the MAJOR fix — but the design's
"SHARED code ... fixes the identical Legacy redirect" / "Legacy benefits identically"
claim is **over-stated for negative-member enums** (true for non-negative enums only;
declaration order coincides with value order there).

### New findings (round 1)

- **[MINOR, pre-existing, out-of-scope]** TC4 fails under Legacy (`-1` sorts first /
  declaration order) because the `ILRuntimeType.GetEnumValues()` override is not
  dispatched under Legacy. Not a regression; the fix is Neo-correct and Legacy-clean.
  Resolution for the LEAD: either (a) mark TC4 as Neo-scoped (it probes a Neo-reachable
  override), or (b) document the pre-existing Legacy limitation (Legacy returns IL-enum
  values in Cecil declaration order, not unsigned-value order, for `Enum.GetValues`).
  Fixing the Legacy typeof/Type-wrapper dispatch is a separate, larger investigation and
  out of this change's scope.
- **[TRIVIAL, doc-hygiene]** `design.md` Risks still carries the round-0-inaccurate line
  "`Enum.GetValues`/`GetNames` return members in declaration (Cecil) order; the framework
  does not sort." — the framework DOES sort (by unsigned binary value). Only the durable
  `spec.md` was corrected; `design.md` was not. (`design.md` is change-scoped, not merged
  on archive, so non-blocking.)
- **[TRIVIAL, comment-staleness]** `GetSortedEnumMembers`'s header comment
  (`ILRuntimeType.cs:670-672`) still ends "Values share one underlying type, so the
  non-generic IComparable is mutually comparable." — the sort no longer uses `IComparable`
  (it uses `EnumValueAsUnsignedBits`); the adjacent block comment (685-689) correctly
  describes the unsigned sort. Stale sentence only.

### Working tree

Restored to the LEAD's fixed state after the stash-toggle: sort line is the unsigned
`EnumValueAsUnsignedBits(...).CompareTo(EnumValueAsUnsignedBits(...))` variant
(`ILRuntimeType.cs:690`); Neo CLI rebuilt clean (0 errors). All toggles were single-line
Edits (no whole-file stash); the helper and TC4 were never removed.

### Verdict: **APPROVE-WITH-FINDINGS**

The round-0 Major (signed-vs-unsigned enum sort) is **resolved and verified** for the
change's Neo target: the unsigned-binary sort with the per-type unchecked-cast helper is
correct for all 8 integer underlying types, the signed-FAIL/unsigned-PASS stash-toggle is
clean on the negative-member path, NeoStep is 352/0, and the durable spec wording is
accurate. The sole open items are minor and non-blocking: a pre-existing Legacy
declaration-order gap newly exposed by TC4 (not a regression; Neo-out-of-scope), and two
trivial doc/comment-staleness items. Rationale: the MAJOR delta is correct, complete, and
regression-free; the findings need a LEAD decision (Neo-scope TC4 or document the Legacy
gap) but do not block the fix.
