# Review Report — neo-ldtoken

**Reviewer:** reviewer-1 (author != verifier; dispatched, report-only)
**Change:** `neo-ldtoken` (child 2 of `neo-overhaul` portfolio)
**Branch:** `features/object-model-overhaul` (base: `master`; no PR)
**Diff:** uncommitted working tree — 3 modified files + 1 new probe (not yet committed)
  - `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` (Ldtoken arm, +85 ln)
  - `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` (LowerNeoOffsets case, +18 ln)
  - `ILRuntimeTestBase/AutoGenerate/System_Type_Binding.cs` (GetTypeFromHandle_0_Neo fix, +12/-4 ln)
  - `TestCases/NeoStepLdtokenTest.cs` (4 probes, new)

## VERDICT: APPROVE-WITH-FINDINGS

The change's stated, in-scope goal — the **type path** (`typeof(T)`, the dominant
~60-hit case) — is **correct, load-bearing, and green** (NeoStep smoke 310/0; the 4
probes FAIL on HEAD with the exact Step-6 NIE and PASS after the fix). All 3 edits are
inside `#if ENABLE_NEO_MODE` → Legacy byte-identical. **One Major finding** (the field
path) is real, empirically reachable, and non-regressing (red→red; net smoke failures
*dropped* 272→249), with a clean low-risk fix. Ship the typeof unblock now; track the
field-path fix as a follow-up (or apply the one-line carrier swap below).

Rationale: the implementer's central claims hold for the type path and the lowering, but
two documented claims are **factually wrong** — "Operand3 is disjoint from OperandLong"
and "the field path is unreachable" — and the wrongness of the first IS the root cause of
an actively-crashing (though non-gating) field path. Hence "with findings," not clean.

---

## Findings

### MAJOR-1 — Field-path `Operand3` stamp aliases `OperandLong`'s high dword; declaring-type resolution is corrupted (the field path is reached and broken)

**Where:** `Optimizer.Neo.cs` LowerNeoOffsets `case Ldtoken` (`op.Operand3 = ...RefOffset`)
interacts with `ILIntepreter.Neo.cs:1530` field path
(`var declType = AppDomain.GetType((int)(ip->OperandLong >> 32));`).

**The bug.** `OpCodeR` is a 24-byte `[StructLayout(LayoutKind.Explicit)]` union
(`ILRuntime/Runtime/Intepreter/OpCodes/OpCode.cs:62-71`):

```
[FieldOffset(12)] int  Operand2;     // bytes 12-15  == OperandLong LOW dword
[FieldOffset(16)] int  Operand3;     // bytes 16-19  == OperandLong HIGH dword  <-- ALIAS
[FieldOffset(12)] long OperandLong;  // bytes 12-19
[FieldOffset(20)] int  Operand4;     // bytes 20-23  (disjoint)
```

`Operand3` (@16-19) **IS the high dword of `OperandLong`** (@12-19). They are NOT
disjoint. The lowering stamps the dest ref-slot index into `Operand3`, which **overwrites
the high dword of `OperandLong`**. The field path then reads
`(int)(ip->OperandLong >> 32)` — the high dword — so it resolves the declaring type from
the **clobbered RefOffset value**, not the real declaring-type token.

- The **type path is safe**: it reads `(int)ip->OperandLong` (low dword, bytes 12-15,
  untouched by the Operand3 stamp) and `ip->Operand3` directly for the ref slot. Verified.
  This is why the 4 typeof probes pass.
- The **field path is corrupted**: `declType = GetType(<RefOffset>)` resolves the wrong
  type (or a type whose `staticFieldOffsets` is null), then `ilt.GetStaticFieldOffset(sIdx)`
  NREs.

**Empirical proof the field path IS reached (contradicts "unreachable").** The full
(filter-less) Neo smoke crashes in `ArrayTest02`, whose C# array initializer compiles to
exactly `ldtoken <field>` (`RuntimeHelpers.InitializeArray(arr, RuntimeFieldHandle)`):

```
IL_0008: ldtoken <PrivateImplementationDetails>/__StaticArrayInitTypeSize=20
         <PrivateImplementationDetails>::4F6ADDC9…(JIT_0002:ldtoken r28,0x300000005)
…
Rethrown as Exception: System.NullReferenceException
   at ILRuntime.CLR.TypeSystem.ILType.GetStaticFieldOffset(Int32 idx) … ILType.cs:2770
   at ILRuntime.Runtime.Intepreter.ILIntepreter.ExecuteNeo(…) … ILIntepreter.Neo.cs:line 1537
```

`ILType.GetStaticFieldOffset` (ILType.cs:2770) is `return staticFieldOffsets[idx];` — the
NRE is `staticFieldOffsets == null` on the **wrong** resolved type. The caller
`ExecuteNeo:1537` is `var off = ilt.GetStaticFieldOffset(sIdx);` inside the **Ldtoken field
path** (NOT `CreateInstance_0_Neo` — see Trivial-1). Full-smoke signature counts:
`GetStaticFieldOffset` = **20 on fixed tree vs 0 on HEAD** (on HEAD the ldtoken NIE fires
first, so the field path is never entered).

**Severity calibration.** This is an implemented (not gated), reachable, silently-wrong-value
path: a bogus token that happens to resolve a type with a matching static field would
return the **wrong field value** without throwing. It does not fail any currently-green
test (ArrayTest02 etc. failed with the ldtoken NIE on HEAD → red→red, not a regression),
and the NeoStep gate stays green, so it is not a ship-blocker for the typeof unblock. But
"reachable + silent-wrong-value + false correctness claim" clears the Major bar.

**Recommended fix (low-risk, for the LEAD's fixer).** Move the dest ref-slot carrier off
the aliased `Operand3` (@16-19) onto `Operand4` (@20-23), which is disjoint from
`OperandLong` (@12-19) and unused by the arm:
- `Optimizer.Neo.cs`: `op.Operand4 = localInfos[op.Register1].RefOffset;` (instead of `Operand3`).
- `ILIntepreter.Neo.cs:1524` type path: `dstIdx = frameRefBase + ip->Operand4;` (instead of `Operand3`).
- Field path left as-is; `OperandLong >> 32` is now intact.

This preserves the "no JIT change / no encoding change" property the design wanted and
keeps the type path identical in behaviour. (Alternative: gate the field path behind a
tagged NIE for now and defer it — but the carrier swap is ~3 lines and fully fixes it.)

---

### MINOR-1 — Design/proposal disjointness claim is factually wrong

`design.md` Decision 2 / Risks and `proposal.md` assert "`Operand3` (@16) is disjoint from
`OperandLong` (@12-19 low 8)" and "`Operand` (@4-7)". Both offsets are wrong:
- `Operand` is `[FieldOffset(8)]` (bytes 8-11), not @4-7.
- `Operand3` (@16-19) **is** the high dword of `OperandLong` (@12-19) — not disjoint.

This is the claim that produced MAJOR-1. Correct the design/tasks so a future implementer
does not re-trust it. (`Operand4` @20-23 is the genuinely-disjoint spare.)

### MINOR-2 — "Field path is unreachable" implementer note is incorrect

`tasks.md` IMPLEMENTER NOTE (2.4 / 3.2) states the field path "is exercised only by code
paths outside this smoke" / "not reachable from a probe." It IS reached in this smoke by
every constant array initializer (`RuntimeHelpers.InitializeArray` → `ldtoken <field>`,
e.g. `ArrayTest02`). The note should say "reachable but broken; see MAJOR-1."

### TRIVIAL-1 — "Downstream Activator NRE" attribution conflates two distinct failures

`tasks.md` IMPLEMENTER FINDINGS attribute the full-smoke crash to
"`System_Activator_Binding.CreateInstance_0_Neo -> ILType.GetStaticFieldOffset` NRE." Two
separate signatures are conflated:
- `CreateInstance_0_Neo` throws **`MissingMethodException`** ("No parameterless ctor for
  `ILTypeInstance`") — real `Activator.CreateInstance<T>()` on a host type; genuinely
  pre-existing (14 on HEAD, 16 on fixed). NOT a `GetStaticFieldOffset` caller.
- The `GetStaticFieldOffset` **NRE** is called from `ExecuteNeo:1537` (the Ldtoken field
  path), not from `CreateInstance_0_Neo` (see MAJOR-1).

The "not a regression" conclusion is still correct (both are pre-existing or red→red); only
the causal attribution in the note is loose.

---

## Standards axis

- **Type-path object-ref push** matches `Ldstr` exactly: `dstIdx = frameRefBase + <refSlot>;
  mStack[dstIdx] = obj; *(int*)(frameBase + ip->DstOffset) = dstIdx;` — only difference is
  the carrier (`Operand3` for Ldtoken vs `Operand` for Ldstr), forced by the discriminator.
  Correct.
- **Field-path body** is a verbatim copy of the `Ldsfld` arm (ILIntepreter.Neo.cs:3868-3910),
  incl. the primitive 1/2/4/8 dispatch, the inline-VT `CopyBlockUnaligned`+ref-materialise
  loop, and the reference-field `mStack.Add` convention. Line-for-line identical except the
  tagged-exception message. The divergence is NOT in this body — it is the upstream
  `declType` resolution corrupted by MAJOR-1.
- **`LowerR1`** (Optimizer.Neo.cs:1506) sets only `op.DstOffset`; it does not touch
  `Operand3`/`Operand4`. Confirmed.
- **`GetTypeFromHandle_0_Neo` correction** reads the single arg via
  `ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack)` and writes it
  straight through. `ReadNeoReference` (ILIntepreter.Neo.cs:123) reads a 4-byte mStack index
  + advances curPrim by 4, null-sentinel-aware. Correct; matches the Legacy no-op semantics.
- Worst Standards issue: MAJOR-1 (aliasing).

## Spec axis (`proposal.md` / `tasks.md` / `specs/neo-type-checks/spec.md`)

- Spec scenarios "typeof of primitive/string/IL type" and "consumed by a downstream
  reflection call" — all met (TC1-TC4 green; the consumed-value probes assert via
  `get_FullName`/`op_Equality` + deliberate `1/0`, satisfying the child-1 "assert the
  value" requirement). "ldtoken no longer hits the Step-6 default" — met (ldtoken NIE
  254→0).
- Spec "field path mirrors Ldsfld / CLR declaring type throws tagged NIE" — **partially
  unmet**: the body mirrors Ldsfld, but the field path does not produce correct results
  (MAJOR-1). The "CLR declaring type → tagged NIE" branch is implemented but unreachable
  behind the earlier NRE.
- Spec "LowerNeoOffsets stamps `Operand3`" — implemented as specced, but the spec itself
  bakes in the wrong disjointness assumption; recommend amending the spec to `Operand4`.
- Worst Spec issue: MAJOR-1 (field path).

---

## Independent re-verification evidence

### 1. NeoStep smoke (no-regression gate) — PASS
Build CLI `Debug_Neo --no-incremental` (0 errors) + TestCases `Debug` (0 errors).
```
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep
=> Ran 310 tests, 0 failed, 0 ignored, 0 todos   (EXIT=0)
```
306 baseline + 4 new probes. All 4 `NeoStepLdtoken_TC1..TC4` invoked (JIT dump shows
`ldtoken r6,"TestCases.NeoStepLdtokenHelper"` being compiled).

### 2. Stash-toggle (load-bearing claim) — FAIL-on-HEAD → PASS-after
`git stash push -- <the 3 source files>` (probe left in place) → rebuild CLI → run `NeoStepLdtoken`:
```
=> Ran 4 tests, 4 failed   (EXIT=127)
Rethrown as Exception: System.NotImplementedException: Neo: opcode Ldtoken not yet implemented (Step 6)
   at ILRuntime.Runtime.Intepreter.ILIntepreter.ExecuteNeo(…) … ILIntepreter.Neo.cs:line 5403
```
`git stash pop` → rebuild → re-run:
```
=> Ran 4 tests, 0 failed   (EXIT=0)
```
Exact evidence: all 4 probes throw the Step-6 NIE on HEAD; all 4 pass after the fix. The
source edits (not just the probe) are what flips the result.

### 3. Legacy-neutral spot check — PASS
All 3 edits are inside `#if ENABLE_NEO_MODE`:
- `ILIntepreter.Neo.cs:1` → `#if ENABLE_NEO_MODE` (whole file Neo-gated).
- `Optimizer.Neo.cs:1` → `#if ENABLE_NEO_MODE` (whole file Neo-gated; `LowerNeoOffsets` @14, Ldtoken case @868).
- `System_Type_Binding.cs:171` → `#if ENABLE_NEO_MODE` wraps `GetTypeFromHandle_0_Neo` (@172); the Legacy `GetTypeFromHandle_0` (@201, `StackObject*` signature) is in the `#else` and **not in the diff** (untouched). Registration split correctly (@33-36).
Plain `Debug` compiles all three out → Legacy binary byte-identical.

### 4. "Unmasked Activator NRE" verdict — PRE-EXISTING (Activator) + NEW-FROM-FIELD-PATH (not introduced as a regression; red→red)

Full (filter-less) smoke run on both trees, signature counts:

| signature | HEAD (fixes stashed) | fixed tree |
|---|---|---|
| `Ldtoken not yet implemented` (Step 6 NIE) | **254** | **0** |
| `GetStaticFieldOffset` NRE (field path) | **0** | **20** |
| `CreateInstance_0_Neo` (Activator) | **14** | 16 |
| total tests failed | **272** | **249** |

- **`CreateInstance_0_Neo` failures are PRE-EXISTING** — present on HEAD (14) as well as
  fixed (16); they are a `MissingMethodException` in the untouched `System_Activator_Binding`
  (real `Activator.CreateInstance<T>()` cannot construct an `ILTypeInstance`). The change
  does not touch that binding. Unmasked, not introduced.
- **`GetStaticFieldOffset` NRE is a NEW signature from the new field path** (0→20), but it
  is **red→red, not a green→red regression**: the same tests (array-initializer
  `ldtoken <field>`, e.g. ArrayTest02) failed with the Step-6 NIE on HEAD; after the fix
  they fail with the field-path NRE. Net failures **dropped 272→249**. Root cause = MAJOR-1.
- The final exit-127 crash on both trees is a separate fatal NIE in `ILIntepreter.Execute`
  (ILIntepreter.cs:1317, the Legacy dispatcher), unrelated to this change.

**Bottom line:** no regression introduced by this change. The Activator failures are
pre-existing; the field-path NRE is a new manifestation of MAJOR-1 on tests that were
already failing.

---

## Coverage note
The 4 probes cover the type path only (per spec scope). The field path (MAJOR-1) has **no
probe** and is only surfaced by the (non-gate) full smoke. If the field-path carrier swap
is applied, adding one array-initializer probe would lock it in.

## Working tree
All stashes pushed during review were popped (no `review-*` stashes remain; the unrelated
`child4-valuetask-blocked-partial` stash is pre-existing). Temp logs written during the
review are scratch only.

---

## Re-review round 1 (MAJOR-1 fix delta)

**Reviewer:** reviewer-2 (fresh; author != verifier; adversarial delta re-review of the
non-author fixer's MAJOR-1 patch + new TC5 probe only).
**Scope:** the MAJOR-1 delta — the 2-behavioral-line `Operand3 → Operand4` carrier swap
(`Optimizer.Neo.cs:876`, `ILIntepreter.Neo.cs:1526`) + the new probe
`NeoStepLdtoken_TC5_ArrayInitializerFieldPath`. Prior Minor/Trivial not re-litigated unless
touched by the delta.

### 1. Independent OpCodeR offset-fact confirmation — MAJOR-1 was REAL, Operand4 is SAFE

Read `OpCode.cs:35-71` myself. The 24-byte `[StructLayout(LayoutKind.Explicit)]` `OpCodeR`
union lays out (byte ranges derived from the `[FieldOffset(...)]` attrs):

```
@0-3   Code               @4-5   Register1/DstOffset   @6-7  Register2/SrcOffset
@8-11  Register3/OperandOffset/Operand/OperandFloat (Register4 @10-11)
@12-15 Operand2           == OperandLong LOW dword
@16-19 Operand3           == OperandLong HIGH dword   <-- ALIAS (MAJOR-1 root cause)
@12-19 OperandLong/OperandDouble
@20-23 Operand4           (disjoint)
```

- **`Operand3 [FieldOffset(16)]` IS the high dword of `OperandLong [FieldOffset(12)]`**
  (OperandLong spans 12-19; its high dword is 16-19 = Operand3). The lowering stamping
  `Operand3` therefore overwrote the declaring-type token the field path reads via
  `(int)(OperandLong >> 32)`. **MAJOR-1 was real.** Confirmed.
- **`Operand4 [FieldOffset(20)]` (@20-23) is genuinely disjoint** from `OperandLong`
  (12-19), `Operand` (8-11), `DstOffset` (4-5), and all register aliases (4-11). Safe spare.

**End-to-end Operand4-safety for a Ldtoken op (the implementer got wrong once; re-checked):**
- JIT emission (`JITCompiler.cs:2902-2908`): sets only `Register1`/`Operand`/`OperandLong`
  for Ldtoken. Does NOT pre-touch `Operand4`. ✓
- `LowerNeoOffsets` (Optimizer.Neo.cs): the ONLY `Operand4` write applicable to a Ldtoken op
  is line 876 itself (`case Ldtoken`). All other `Operand4` writes (lines 249/579/931/1032/
  1105/1128/1193) live in OTHER opcodes' cases (Move_Vt/calls/array ops/constrained). ✓
- The sole cross-opcode `Operand4` touch is the ref-slot-compaction remap at **1711-1713**
  (`if (op.Operand4 > removedIndex) op.Operand4--;`). This is a **consistent index remap**,
  not a clobber — it adjusts the dest ref-slot index exactly as it must. Correct. ✓
- `LowerR1` (Optimizer.Neo.cs:1513-1526) sets ONLY `op.DstOffset`; does NOT touch
  `Operand4`. So calling it after the stamp is safe. ✓

**Offset-fact verdict: CONFIRMED. Operand4 is a clean, disjoint, unused-end-to-end carrier
for the Ldtoken dest ref slot. The fix's foundation is sound.**

### 2. MAJOR-1 resolution — RESOLVED

Both changed lines read at the current HEAD:
- `Optimizer.Neo.cs:876`: `op.Operand4 = localInfos[op.Register1].RefOffset;` (was Operand3).
- `ILIntepreter.Neo.cs:1526` (type path): `dstIdx = frameRefBase + ip->Operand4;` (was Operand3).
- Field path (`ILIntepreter.Neo.cs:1532`): `var declType = AppDomain.GetType((int)(ip->OperandLong >> 32));`
  — left as-is; the high dword is now intact because `Operand3` is no longer stamped. Auto-heals.
- The code comments at `Optimizer.Neo.cs:855-874` and `ILIntepreter.Neo.cs:1500-1502` ARE
  correctly updated to document the alias + the Operand4 swap. Good.

**MAJOR-1 resolution verdict: RESOLVED.** The field path now reads an unclobbered
`OperandLong >> 32`; the carrier is off the aliased dword.

### 3. TC5 validity — SOUND + forward-compatible

`NeoStepLdtoken_TC5_ArrayInitializerFieldPath`: 32-element `int[]` constant initializer
(128 B, forces the `newarr; dup; ldtoken <field>; call RuntimeHelpers.InitializeArray` form
rather than 32 Stelem stores) wrapped in `try { ...contents asserts... } catch (NotImplementedException)`.

Adversarial scrutiny:
- **Does PASS genuinely imply the field path resolved?** Yes. `ldtoken` is a distinct
  instruction that completes (resolving decl-type from `OperandLong >> 32` and reading the
  static-field value) BEFORE the `call InitializeArray` executes. The NIE the catch swallows
  is raised inside InitializeArray's call marshalling (no Neo `RedirectionNeo` → its
  `RuntimeFieldHandle` param fails the Step-13b ValueTypeBinder marshal) — reachable ONLY if
  the preceding ldtoken finished. So PASS ⟹ field-path resolution.
- **Is the regression guard fragile?** No — it is asymmetric and load-bearing. On a
  re-introduced Operand3 alias, the field path throws `NullReferenceException` (a wrong/null
  decl-type → `staticFieldOffsets == null`), which `catch(NotImplementedException)` does NOT
  catch → uncaught → test FAILs. (Independently reproduced — see §4.) NRE and NIE are
  distinct types, so the guard discriminates the regression from the expected downstream gap.
- **Could it pass spuriously (e.g. an NIE thrown before the field path resolves)?** No
  plausible path: on the buggy tree the failure is an NRE, not an NIE (empirically). JIT-time
  NIE is not reachable for this token shape.
- **Forward-compatibility:** once a Neo InitializeArray redirect lands, no NIE is thrown and
  the contents assertions (`arr.Length == 32`, `arr[i] == 100+i`) take over — the probe
  SELF-TIGHTENS to validate the actual field-path-read values. Acceptable.
- **Minor residual:** the current PASS is coupled to the existence of the InitializeArray NIE.
  This is explicitly documented in the probe comment and is acceptable for a Step-by-Step
  rollout; the probe only gets stricter over time, never weaker.

**TC5 verdict: SOUND.** Empirically hits the field path, discriminates the regression, and is
forward-compatible.

### 4. Gates re-run (all `-f net8.0`; CLI = `Debug_Neo --no-incremental`; TestCases = `Debug`)

- Build CLI `Debug_Neo --no-incremental`: **0 errors**.
- Build TestCases `Debug`: **0 errors**.
- **NeoStep smoke** (`... true NeoStep`): `Ran 311 tests, 0 failed, EXIT=0` (310 + TC5). ✓
- **`NeoStepLdtoken` filter** (`... true NeoStepLdtoken`): `Ran 5 tests, 0 failed, EXIT=0`;
  TC5 invoked + PASS. ✓
- **Precise 2-line toggle (load-bearing; done via Edit, not whole-file stash which would also
  revert TC1-4):** flipped ONLY `Operand4 → Operand3` in both behavioral lines, rebuilt,
  ran TC5:
  - Operand3 (buggy): **TC5 FAIL, EXIT=127** —
    `System.NullReferenceException at ILRuntime.CLR.TypeSystem.ILType.GetStaticFieldOffset(Int32 idx) ILType.cs:2770`
    (uncaught by the NIE catch). Exactly the MAJOR-1 signature.
  - Flipped back to Operand4, rebuilt, re-ran NeoStep: **311/0, EXIT=0**. Restored.
  - The 2-line swap is independently confirmed to be what flips TC5 red↔green.

**Fixer's evidence reproduced.** GetStaticFieldOffset NRE present on Operand3 tree, absent on
Operand4 tree. (Full-smoke exit-127 from the pre-existing Execute:1317 crash is out of scope
and unchanged.)

### 5. New findings on the delta

- **MINOR-A (doc staleness — persists after the fix, non-blocking):** the code comments were
  updated, but the prose `design.md` (Decision 2 + Risks) and `tasks.md` (§1.1, 1.2, 2.4, 3.2)
  STILL assert the original wrong claims — "stamp the dest ref-slot index into the spare
  `Operand3`" and "`Operand3` (@16) is disjoint from `Operand` (@4-7), `OperandLong`
  (@12-19 low 8)". These are the same class as prior MINOR-1/MINOR-2 but are now stale: the
  shipped code contradicts them. Recommend reconciling design.md/tasks.md to `Operand4` so a
  future implementer does not re-trust the wrong disjointness claim (which is precisely what
  produced MAJOR-1). Does not block ship — the code and the code comments are correct.
- **TRIVIAL-A (out-of-scope, pre-existing neighbor bug):** `Optimizer.Neo.cs:1218`
  `op.Operand4 == 1;` is a dead no-assignment expression (a bare equality whose result is
  discarded) — almost certainly intended `op.Operand4 = 1;` in some other opcode's lowering.
  Pre-existing, not touched by this delta, not Ldtoken. Flagged for awareness only.

### Overall verdict: **APPROVE-WITH-FINDINGS**

MAJOR-1 is resolved and independently verified (offset facts confirmed from the struct
definition; the precise 2-line toggle reproduced the NRE → fix flips it to PASS; NeoStep
311/0). TC5 is a sound, forward-compatible regression guard. The only delta-adjacent finding
is stale design/tasks prose (MINOR-A) plus an out-of-scope trivial neighbor expression —
neither is a code defect and neither blocks ship. The MAJOR-1 patch is correct and ship-ready;
reconcile the design/tasks text opportunistically.

**Working tree:** left as found — both lines at `Operand4`; no stashes created (toggle done
via Edit + reverse-Edit); scratch logs removed.
