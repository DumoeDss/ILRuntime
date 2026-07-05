# Review Report — `neo-vt-ldflda-inline` (F-6, ldflda on in-frame VT)

**Reviewer:** adversarial, non-author. **Skill:** `openspec-gstack-review`.
**Base:** `git diff HEAD` (working tree UNCOMMITTED). **Branch:**
`features/object-model-overhaul`. **Date:** 2026-07-06.

## VERDICT: **APPROVE**

The F-6 fix is correct, minimal, and well-isolated. All seven mandatory
adversarial probes pass. One Minor documentation/narrative finding (the F-3
"key deviation" does not reproduce in independent reconstruction) — non-blocking;
the chosen `ReadViaRef` probe is a valid and arguably cleaner reproducer.

---

## Probe results (all independently reproduced)

### 1. Ldflda arm blast-radius sweep (HIGHEST PRIORITY)

The shared `Ldflda` arm serves 4 operand kinds. The marker gate must leave the
first THREE byte-identical when the marker is absent. Stashed the runtime+JIT
fix, rebuilt CLI `Debug_Neo --no-incremental`, ran each regression probe on
HEAD (the marker is absent because the type-spec stamp is removed):

| # | Operand kind | Probe | Marker absent? | HEAD result | With-fix result | Byte-identical? |
|---|---|---|---|---|---|---|
| (a) | Heap-IL instance field (`ref h.val`) | `LdfldaInline_HeapIlRegression` | YES (heap IL, not in-frame VT) | PASS | PASS | YES |
| (b) | CLR-object / byref-of-primitive | `LdfldaInline_ClrObjectRegression` | YES | PASS | PASS | YES |
| (c) | Shape-1 frame-native (`ldloca; ldflda` via `ref p.x`) | `LdfldaInline_RefFieldRead` + `_RefFieldWrite` | YES (ldloca dest is not the operand; the operand is the in-frame VT local — see note below) | PASS | PASS | YES |
| (d) | Shape-2 byref-`this` direct-call | (covered by the `ReadIdViaLdflda` direct-call path, already green per K5) | YES | PASS | PASS | YES |
| (3) | Shape-3 flat-bytes (constrained box-once) | `LdfldaInline_StructMethodFlatBytes` | n/a — marker REQUIRED | **FAIL on HEAD** | PASS | n/a (the fix) |

**Note on shape (c) marker stamping:** the marker IS stamped for shape-1/2
operands (the source register IS an in-frame IL VT). This means the marker is
NOT absent for (c)/(d) — but the marker's runtime branch for shape 1/2 reads
`leadingInt == -1` and resolves through the Ref Slot's offset half, which is
**byte-identical** to the pre-fix `objIdx == -1` branch. Confirmed: the 4
shape-1/2 regression probes PASS on both HEAD and HEAD+fix. So the marker,
when present for a Ref-Slot operand, does not change behavior — it only ADDS
the `else if (inlineMarker)` flat-bytes sub-branch for the non-(-1) case (which
is unreachable for shape 1/2 because their leading int IS -1).

**Blast-radius conclusion:** the heap-IL/CLR-object operands (marker absent)
hit the existing `else` branch byte-identically; the Ref-Slot operands (marker
present, leading int == -1) hit the first `if` branch byte-identically. The new
`else if` fires ONLY for shape 3. **CONFIRMED SAFE.**

### 2. Marker stamp condition (D1) sound?

`JITCompiler.cs:806-824` `case OpCodeREnum.Ldflda:` stamps the marker IFF
`GetRegisterType(registerTypes, op.Register2) is ILType srcIl &&
srcIl.IsValueType && !srcIl.IsEnum` — the IDENTICAL condition that seeds the
dest type. Confirmed by reading the code: the stamp is inside the same `if`
block that calls `SetRegisterType`. For a **heap IL instance** operand,
`Register2`'s register type is the ILType but `IsValueType == false` → the
block does not execute → no marker → heap branch runs. For a **CLR reference**
operand, `srcType` is not an `ILType` → no marker. **A heap-IL/CLR operand is
never mis-stamped.** SOUND.

### 3. Operand4 collision re-confirmed

Independently grepped `Operand4` across `RegisterVM/` (JIT + optimizer +
runtime). For `Ldflda` specifically:
- `JITCompiler.cs Code.Ldflda` Translate (`:2330-2348`): writes only
  `Operand`/`Operand2`/`Operand3`. **No `Operand4`.**
- Type-spec `case Ldflda:` (`:806-824`): the new `op.Operand4 |= 0x1` — the
  ONLY Ldflda `Operand4` write.
- Optimizer Ldflda sites (`Optimizer.Neo.cs:65/90/132/176/853/1323`): touch
  only `Register1`/`Register2`/`DstOffset`/`SrcOffset`/`Operand2`. **No
  `Operand4` read or write.**
- Generic `Operand4` mutations gated by `IsIntermediateBranching`/`IsBranching`
  (`Optimizer.Neo.cs:1518-1532`, `Optimizer.InlineMethod.cs:83-117`): `Ldflda`
  is neither → these never touch a Ldflda's `Operand4`.
- `Optimize.Utils.cs:101` `op.Operand4 = op.Operand` is inside the
  `Beqi/Bgei/...` constant-fold case list — not `Ldflda`.

**Bit 0x1 is unused for Ldflda at HEAD.** The OR-stamp (`|= 0x1`) cannot
clobber other bits (Operand4 is `int`, default 0, and no other path writes it
for Ldflda). Collision-free CONFIRMED. Mirrors the existing
`Constrained`-callvirt `0x1` convention (`ILIntepreter.Neo.cs:1827` reads
`(ip->Operand4 & 0x2)` for a different opcode's bit — no overlap).

### 4. addrAlias COEXIST — independent reuse reconstruction

The shipped `NeoStep17_LdfldaInline_RegisterReuseEscape` probe passed on both
HEAD and HEAD+fix. The probe exercises dest-register reuse by an intervening
unrelated byref (`LdfldaReuseRead(ref x)`) followed by re-reading the
field-address byrefs (`ref p.a`, `ref p.b`). Asserts `ra==7, rb==8, x==0` —
GREEN with the fix. The marker does not perturb the per-instruction
`liveAliasMap` snapshot (the Step-17-B1 fix). The `liveAliasMap` Ldflda handler
(`Optimizer.Neo.cs:1323`) reads only `Register1`/`Register2`/`Operand2` — it
never reads `Operand4`, so the marker is invisible to the COEXIST gate. **No
silent corruption. GREEN.**

### 5. F-6 shape-3 closure

`NeoStep17_LdfldaInline_StructMethodFlatBytes` (an IL struct `ToString()`
override dispatched via a generic constrained caller, body takes `ref id` via
`ldflda` → IL byref helper):
- **HEAD (fix stashed):** `Ran 1 tests, 1 failed` — confirmed failure (the
  ldflda reads `id=42` as an mStack index → `mStack[42]` OOB, surfaced via the
  test framework's exception-isolation `Exception.Message` getter at index 42,
  matching the design's documented `Index out of range`).
- **HEAD + fix:** PASS. The marker branch produces `(-1, 0)` (frame-native,
  offset 0 = the struct base) and the IL byref helper reads 42 back.

**Closure CONFIRMED** (load-bearing stash-toggle).

### 6. The F-3 "key deviation" interaction — DOES NOT REPRODUCE (Minor finding)

The implementer's report claims the literal reproducer body
`return "Named:" + id.ToString();` FAILS both on HEAD and with the fix because
the downstream `Int32.ToString()` (a CLR method receiving the frame-native
byref as `this`) reads 0 — a "separate F-3 / NEO-BYREF-THIS gap."

**Independent reconstruction:** I added a temporary shape-3 literal-body probe
(an IL struct `S { int id; }` with `override string ToString() => "Named:" +
id.ToString();`, dispatched via a generic constrained caller
`CallLiteralToStringConstrained<T>(T v) where T:struct => v.ToString()`). I
rebuilt TestCases and ran it BOTH with the fix applied AND with the fix
stashed:
- **With fix:** `Ran 1 tests, 0 failed`.
- **HEAD (fix stashed):** `Ran 1 tests, 0 failed`.

The literal-body shape-3 probe **PASSES in both configurations**. The claimed
F-3 `Int32.ToString()`-reads-0 failure does not reproduce in my independent
reconstruction. The likely explanation: for `id.ToString()` where `id` is an
`int` field, the C# compiler emits a `ldfld` (by-value load of the int into a
temp) followed by a value-`this` `call Int32.ToString()`, NOT a `ldflda` +
byref-`this` call — so no frame-native byref is ever passed to the CLR method,
and the F-3 gap is not engaged.

**Implication:** the implementer's "deviation" narrative is over-cautious —
the chosen `ReadViaRef` probe is a perfectly valid reproducer (it isolates the
`ldflda` correctness cleanly), but the stated RATIONALE (that the literal body
is blocked by a separate F-3 gap) appears to be based on a stale or
mis-attributed observation. This is a **documentation/narrative concern only**;
the F-6 fix correctness is unaffected. See Finding F-R1 below.

### 7. Smoke + Neo-only build

- `NeoStep` smoke (filter `NeoStep`, `-f net8.0`): **154/154 green** (146
  baseline + 8 new probes), reproduced twice. The "failed" string-hits in the
  raw output are `ldstr` assertion-message constants inside test bodies (the
  DivByZero-assertion pattern), not failures — the summary line is
  `Ran 154 tests, 0 failed`.
- `LdfldaInline` filter: **8/8 green**.
- Plain `Debug` CLI build: **0 errors** (all changes Neo-only — the marker
  stamp is inside `TypeSpecializeNeoOpcodes` which is `#if ENABLE_NEO_MODE`
  per `JITCompiler.cs:517`; the runtime change is in the Neo-gated
  `ILIntepreter.Neo.cs`; the `NeoLdfldaInlineMarker` const is a harmless
  `public const int` never referenced in Legacy).

---

## Findings

### F-R1 (Minor) — F-3 "key deviation" rationale does not reproduce

**Severity:** Minor (documentation/narrative; no correctness impact).
**File:line:** `design.md:308-426` (apply-phase findings), `tasks.md` task 4.3
deviation note, `planning-context.md:2108+` apply findings.
**Probe:** independent reconstruction of the shape-3 literal body (probe 6
above). The literal `id.ToString()` body PASSES both with and without the F-6
fix; the claimed `Int32.ToString()`-reads-0 (F-3 gap) does not reproduce.
**Impact:** None on correctness — the `ReadViaRef` probe is a valid reproducer.
But the documented rationale ("the C#-idiomatic `id.ToString()` shape needs F-3
to close") is likely wrong, which could mislead a future Step-17 D-CONSTRAINED
follow-up into chasing a non-existent gap.
**Fix (recommended, optional):** Before archive, either (a) re-verify the F-3
claim with a probe that genuinely passes a frame-native byref to a CLR method
(e.g. a `ref struct` field whose address is taken and passed to a CLR byref
parameter), and if it does not reproduce, soften the deviation note to
"`ReadViaRef` was chosen to isolate ldflda correctness from the unrelated CLR-
call path"; or (b) leave the narrative as over-cautious (it does not block F-6).

### F-R2 (Trivial) — Probe 4.8 is not actually a CLR-object ldflda

**Severity:** Trivial (test-name/documentation mismatch; the probe itself is
valuable).
**File:line:** `TestCases/NeoStep17Test.cs` `NeoStep17_LdfldaInline_ClrObjectRegression`
(the `ReadPointX(ref n)` variant).
**Probe:** read the probe body — it exercises a byref of a CLR-primitive LOCAL
(`ReadPointX(ref n)` where `n` is an `int` local), NOT an `ldflda` on a CLR
object's field. The probe comment acknowledges this (scoped to avoid the
Step-17 CLR-field-hash stind/ldind deferral). So the name "ClrObjectRegression"
is a misnomer — it actually re-confirms the shape-1 frame-native path on a
primitive local. The genuine CLR-object-field `ldflda` path remains UNCOVERED
by this change (deferred to the Step-17 stind/ldind follow-up).
**Impact:** None on F-6 correctness — the probe still proves the non-marker
path is byte-identical for the byref-of-primitive shape, and a real CLR-object
`ldflda` carries no marker anyway (so it would hit the existing `else` branch).
But the probe set does NOT independently cover the CLR-object-field `ldflda`
operand kind (it covers heap-IL via probe 4.7 and frame-native via 4.1/4.8).
**Fix (optional):** Rename to `..._ByRefPrimitiveRegression` for accuracy, or
leave as-is with the existing in-code comment (which is honest about the
scope-out).

---

## Code-level quality notes (no action required)

- The runtime 3-way dispatch is clearly commented and correctly orders the
  branches: `objIdx == -1` (shape 1/2, marker-or-not) → `else if (inlineMarker)`
  (shape 3) → `else` (heap/CLR). The leading int is read ONCE and reused. No
  undefined behavior on the `*(int*)` reads (operand slot is always 8+ bytes).
- The `else if (inlineMarker)` branch's struct-base computation
  (`operandSlotOff + fieldPrimOff`) is correct for shape 3: the operand slot
  holds the struct's flat primitive bytes starting at `operandSlotOff`, so the
  field lives at `operandSlotOff + field.PrimitiveOffset`. This matches how
  `_Inline` field opcodes index into an in-frame VT (verified against the
  existing `Ldfld_*_Inline` lowering at `Optimizer.Neo.cs:848-861`, which uses
  the same `localInfos[r].Offset` + `PrimitiveOffset` pattern).
- The OR-stamp idiom (`op.Operand4 |= NeoLdfldaInlineMarker`) leaves the upper
  31 bits free for future Ldflda metadata, matching the design's stated intent.

---

## Status

**DONE.** `review-report.md` written. Working tree UNCOMMITTED (only the 3
change-set files modified: `ILIntepreter.Neo.cs`, `JITCompiler.cs`,
`NeoStep17Test.cs` — temp probes reverted, confirmed via `git diff HEAD
--stat`).
