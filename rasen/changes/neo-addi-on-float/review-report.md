# Review Report -- neo-addi-on-float (child 16 of neo-overhaul)

Reviewer: author != verifier gate (dispatched leaf, report-only)
Date: 2026-07-12
Branch: features/object-model-overhaul
Tier: A (autonomous)
Mode: dispatched (no auto-fix, no commit, no subagents)

## Verdict: APPROVE-WITH-FINDINGS

The change is correct, minimal, Neo-only, and Legacy-neutral. The root cause is
real and independently reproduced (FAIL-on-HEAD -> PASS-on-fix toggle). One Minor
finding concerns the precision of a corroborating evidence claim in the
ship-log/tasks (the "JIT dump shows addi.r4" line); it has zero correctness
impact. Ship-ready.

---

## 1. Seeding correctness — CONFIRMED

The diff is a single +30-line hunk inside `TypeSpecializeNeoOpcodes`
(`JITCompiler.cs`, `#if ENABLE_NEO_MODE`), immediately after the existing
`Ldfld_R4`/`Ldfld_R8` block (ends at :1074). The 8 new cases:

| Producer(s) | Seed | Correct? |
|---|---|---|
| `Ldind_R4`, `Ldelem_R4` | `appdomain.FloatType` | YES |
| `Ldind_R8`, `Ldelem_R8` | `appdomain.DoubleType` | YES |
| `Ldind_I8`, `Ldelem_I8` | `appdomain.LongType` | YES |
| `Ldind_I4`, `Ldelem_I4` | `appdomain.IntType` | YES |

- Form mirrors the neighboring `Ldc_*` (:855-872) and `Ldfld_*` (:1069-1073)
  cases exactly: `SetRegisterType(registerTypes, op.Register1, appdomain.XxxType)`.
  `op.Register1` is the dest/producer register for these loads (consumed as
  `op.Register2` by the immediate-arithmetic specialization at :1017 and by the
  binary-arithmetic specialization at :930).
- No mis-seeding: each load maps to its own primitive type; there is no path that
  seeds an integer producer as Float (which would corrupt integer arithmetic) or
  vice versa. The mapping is 1:1 with the CIL primitive width.
- I4 is a provable no-op, verified two ways:
  (a) `GetRegisterType` (:1611) returns `null` for an unseeded slot, and
      `InferPrimTag(null)` (:1652-1653) returns `I4`; an explicit `IntType` seed
      also `InferPrimTag`s to `I4`. `GetTypedImmediateBinaryOpcode` (:1872) has
      no `I4` case (only I8/U8, R4, R8) -> returns `code` unchanged. So both
      null-seed and IntType-seed yield the identical plain `Addi`. The I4 seed
      changes no behavior today (symmetry/future-proofing, as the design states).
  (b) Strong analogy: `Ldc_I4` already seeds `IntType` (:863) and is extremely
      common; if seeding IntType for an int value perturbed any other
      type-spec consumer, the existing `Ldc_I4` seed would already have done so.
      NeoStep is green -> the IntType seed is benign. Likewise `Ldind_R4`->Float
      is exactly analogous to `Ldc_R4`->Float (:869) / `Ldfld_R4`->Float (:1070).
- Other consumers of `registerTypes` key on `IsNeoReferenceSlot` /
  `IsValueType` (Brtrue_Ref, Ceq_Ref, Beq_Ref, Move is-ref flag,
  field-access-inline). A primitive float/double/long/int is neither a reference
  slot nor a value type, so those decisions are unchanged. Confirmed by
  inspection of the Move (:880-884) and Ldloca/Ldflda (:1113-1148) cases and by
  the clean regression smoke below.

## 2. Regression surface (TYPE-SPECIALIZER risk) — CONFIRMED CLEAN

The change affects all typed arithmetic specialization. Re-ran the load-bearing
subsets (independently, fresh `Debug_Neo --no-incremental` CLI + `Debug`
TestCases, `-f net8.0`):

| Filter | Result |
|---|---|
| full NeoStep | 346 ran, 0 failed, EXIT 0 |
| NeoStep16 (arithmetic/conv, 27 sub-tests) | 27/0 |
| OrChain (10) | 10/0 |
| Float (4) | 4/0 |
| Double (7) | 7/0 |
| AddiOnFloat (3 new probes) | 3/0 |

The I4 seeding (`Ldind_I4`/`Ldelem_I4`->IntType) does not break existing
integer-ldind code: as shown above it is behavior-identical to the prior
null->I4 fallback, and NeoStep16 (the arithmetic step) is 27/0. No regressions
surfaced anywhere.

## 3. Stash-toggle evidence — CONFIRMED (FAIL-on-HEAD -> PASS-on-fix)

Stashed ONLY `JITCompiler.cs` (the test file is untracked and stayed in place),
rebuilt CLI `Debug_Neo --no-incremental`, ran the 3 probes filtered:

- **HEAD (fix stashed):** `Ran 3 tests, 3 failded` — 3x
  `System.DivideByZeroException`. Reproduced the HEAD JIT line for TC1 from the
  `OUTPUT_JIT_RESULT` dump: `9:addi r7,r7,1120403456` (`1120403456` = `0x42C80000`
  = IEEE bits of `100.0f`), preceded by `7:ldind.r4 r7, r6` and
  `8:(x)ldc.r4 r8,100`. This matches the implementer's HEAD evidence exactly.
- **Fix applied (popped, rebuilt):** `Ran 3 tests, 0 failded`.

The toggle is airtight. The fix provably changes the EXECUTED opcode to
`Addi_R4`: the only runtime arm that yields `a.X = 101.0f` is `Addi_R4`
(`ILIntepreter.Neo.cs:2546`: `*(float*)dst = *(float*)src + ip->OperandFloat`);
the plain `Addi` arm (`:2468`: `*(int*)dst = *(int*)src + ip->Operand`)
integer-adds the float bits and gives garbage (`~0x82480000` as float), which is
exactly why HEAD trips DivideByZero. PASS is impossible unless `Addi_R4`
executes.

## 4. Probe validity — CONFIRMED

- Host helper `TestCLRBinding.SumTestVector3Fields(a, b)` =
  `(int)(a.X+a.Y+a.Z+b.X+b.Y+b.Z)` (`TestClass3.cs:135-138`), computed in CLR.
  Calling it as `Sum(a, a)` = `(a.X+a.Y+a.Z)*2`. With `TestVector3.One=(1,1,1)`:
  TC1 `a.X+=100`->(101,1,1)->206; TC2 `a.X-=100`->(-99,1,1)->-194;
  TC3 `a.X*=2; a.X+=1`->(3,1,1)->10. Math is sound.
- The assertion mechanism (pass = no throw; logic failure = deliberate `1/0`
  DivideByZero) faithfully reflects the IL-side value: the marshaling of the
  pure-primitive (3-float, binder-registered) struct carries `a.X`'s bytes to
  the host verbatim, and the float sum runs in CLR (sidesteps the separate,
  out-of-scope `conv.i4`-float-bit-reinterpret bug).
- **TC3 deviation is sound.** The design's single expression `a.X = a.X*2 + 1`
  lowers the READ of `a.X` through a raw `ldfld` of the CLR-struct field (the
  child-4 escaping shape, an out-of-scope producer not covered by this change's
  Ldind/Ldelem seeding), so it would NOT exercise the fix. The implementer
  reformulated it as `a.X *= 2; a.X += 1;` — two compound assignments, each
  lowering via `ldloca; ldflda; ldind.r4` (the in-scope producer, identical to
  TC1/TC2). Same expected Sum==10, still exercises `muli.r4` + `addi.r4`
  (proven: TC3 FAILs on HEAD, PASSes with fix — if either op integer-corrupted,
  Sum != 10). Documented in tasks 3.4 and ship-log. The spec scenario text
  ("a.X = a.X * 2 + 1") and the implementation differ in surface form but test
  the same code paths; faithful realization.
- `divi`/`remi` have no dedicated probe but share the identical
  `GetTypedImmediateBinaryOpcode` mechanism (:1901-1902, :1911-1912); the design
  explicitly notes a probe is optional (division/modulo by a float constant is
  rare). Acceptable.

## 5. Legacy-neutral — CONFIRMED

- The new cases are inside `TypeSpecializeNeoOpcodes`, which is
  `#if ENABLE_NEO_MODE` -> compiles out under plain `Debug`.
- Plain-`Debug` CLI NeoStep filter: `346 ran, 17 failded` == the documented
  baseline (byte-identical build with/without the fix by construction).
- Plain-`Debug` AddiOnFloat: `3/0` — the 3 probes pass under Legacy (Legacy's
  `Addi` re-dispatches on `StackObject.ObjectType` and reads `OperandFloat` for a
  Float slot). None of the 3 probes are in the 17-failure set.

## 6. Scope — CONFIRMED (no scope creep)

- `git diff` shows exactly +30 lines in one hunk in `JITCompiler.cs`; no other
  source file changed (`Optimizer.ELDC.cs`, `Optimizer.Utils.cs`, Neo runtime
  arms, object model all untouched). New file `TestCases/NeoStepAddiOnFloatTest.cs`.
- No new opcode, no runtime change, no fold change — consistent with proposal.
- The constant-fold (ELDC) is correctly left alone: it is type-agnostic and
  Legacy depends on the plain `Addi` form it produces (Legacy re-dispatches at
  runtime). The design's D1 rationale (fix at the seeding site, not the fold) is
  the minimal-blast-radius choice and is validated by the green smoke.

---

## Findings

### Minor-1: "JIT dump shows addi.r4" evidence claim is imprecise
**Severity:** Minor (evidence-claim precision; no correctness/spec/test impact)
**Location:** `ship-log.md` ("Fix applied ... JIT for TC1: 5:addi.r4; TC3:
5:muli.r4 + 10:addi.r4") and `tasks.md` 4.2 ("JIT dump shows addi.r4/subi.r4/
muli.r4").

The three `OUTPUT_JIT_RESULT` dump sites (`JITCompiler.cs:501`, `:519`, `:697`)
all run BEFORE `TypeSpecializeNeoOpcodes` is called (`:736`). The "Final Results"
dump at `:697` therefore prints the PRE-specialization opcodes: TC1 shows
`9:addi r7,r7,1120403456` (plain `addi`), NOT `addi.r4`. No dump site exists
after `:736`, so the typed `addi.r4`/`subi.r4`/`muli.r4` is never printed by
`OUTPUT_JIT_RESULT`.

The underlying claim — that the fix causes the typed `*_R4` arm to EXECUTE — is
TRUE and is proven independently by the FAIL-on-HEAD -> PASS-on-fix toggle and
by static analysis (only `Addi_R4`'s `OperandFloat` arm yields `101.0f`;
`Addi`'s integer arm yields garbage -> DivideByZero on HEAD). The HEAD-side
claim (`addi r7,r7,1120403456` = `0x42C80000`) is accurate and reproduced.

Suggested cleanup (optional, cosmetic): amend the ship-log/tasks to state the
typed opcode is proven by FAIL->PASS + static analysis (the executed `addi.r4`
is not printed by the pre-specialization dump), or add a temporary
post-`:736` dump if opcode-text evidence is desired. No code change needed.

### Note (out of scope, correctly flagged by implementer): sibling raw-Ldfld CLR-struct-field gap
The raw `Ldfld`/`Stfld` of a CLR-struct field (child-4 escaping shape, e.g. the
single-expression `a.X = a.X*2 + 1` read path) also does not seed
`registerTypes` — same float-corruption class, DIFFERENT site (raw `Ldfld`, not
`Ldind`/`Ldelem`). Correctly left out of scope by this change (the spec pins
`Ldind_*`/`Ldelem_*` only). Surfaced as a candidate sibling child; recorded here
for the LEAD's triage. Also `Call`/`Callvirt` returning a primitive float/double
is unseeded (design Open Question) — defer unless a probe surfaces it.

---

## Summary

- **Root cause (refined):** independently confirmed. The `registerTypes` seeding
  switch seeded Float/Double/Long only for `Ldc_*`/`Ldfld_*`; `Ldind_*`/`Ldelem_*`
  (byref/CLR-struct-field/array-element producers) were never seeded -> the typed
  `*_R4`/`*_R8`/`*_I8` specialization silently no-oped -> plain integer
  `Addi`/`Add` arm integer-added the float bits. Shared root with child-11/12/13
  (untyped Neo frame + `InferPrimTag` I4-fallback). The constant-fold is CORRECT
  and Legacy-shared (Legacy escapes via per-slot `ObjectType`). All confirmed by
  reading `InferPrimTag` (:1650), `GetTypedImmediateBinaryOpcode` (:1872), the
  `Addi`/`Addi_R4` arms (`Neo.cs:2468`/`:2546`), and the FAIL->PASS toggle.
- **Fix:** 8 seeding cases, correct mapping, correct placement/form, no
  mis-seeding, I4 a provable no-op. No new opcode. Free scope over
  subi/muli/divi/remi + plain add/sub/mul/div/rem (all key on
  `registerTypes[Register2]`).
- **Seeding-correct?:** YES.
- **Regression-surface-confirmed?:** YES (NeoStep16 27/0, OrChain 10/0, Float
  4/0, Double 7/0, full NeoStep 346/0).
- **Stash-toggle:** YES (HEAD 3/3 DivByZero -> fix 3/0).
- **NeoStep result:** 346/0, EXIT 0.
- **Legacy-neutral:** YES (plain-Debug 346 ran/17 failed == baseline; 3 probes
  pass under Legacy).

**APPROVE-WITH-FINDINGS** — the one Minor finding is an evidence-claim precision
nit in the ship-log (the post-specialization `addi.r4` is executed but not
printed by the pre-specialization dump); the fix itself is correct and the change
is safe to ship.
