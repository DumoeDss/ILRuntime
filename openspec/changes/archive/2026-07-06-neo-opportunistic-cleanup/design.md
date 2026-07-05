## Context

This is a TRIVIAL opportunistic cleanup change. Two items deferred since
Steps 14-15 are now resolvable because their respective blockers shipped long
ago. Both are tracked in `.trae/documents/neo-deferred-items.md` (§2 master
table rows `N-CGTUN` and `N-TC2`; §3 per-item detail). There is no
architectural decision to make — only confirm each item still applies against
current code, then apply the one-line comment tighten and the test tighten.

**Current state, confirmed against HEAD:**

1. **N-CGTUN — `Cgt_Un` comment is incomplete.** The `Cgt_Un` arm in
   `ILIntepreter.Neo.cs` (~lines 1018-1038) computes
   `cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1)`. The
   comment (~lines 1029-1032) explains the divergence from raw unsigned
   semantics as: "Diverges from raw unsigned semantics only for the
   pathological `cgt.un x, (uint)0xFFFFFFFF` integer case". That names ONLY the
   operand case (`cguB == -1`). The expression has a SECOND divergence — the
   leading `cguA != -1` clause — which makes the result `false` when the
   SOURCE is the null sentinel `-1`, regardless of the operand. This is the
   symmetric source-sentinel case, and it is also a divergence from raw
   `(uint)cguA > (uint)cguB` semantics. The comment does not name it. The
   whole TestCases suite does not exercise either integer-sentinel case
   (validated), so this is purely a comment-accuracy nit.

2. **N-TC2 — TC2 asserts only `e != null`.** `NeoStep14_TC2_CatchObjectAccess`
   in `TestCases/NeoStep14Test.cs` (~lines 40-52) catches
   `DivideByZeroException e` and asserts only `if (e != null) return 7;`. The
   file's header comment (~line 18) states the reason: "isinst/castclass land
   in Step 15". Step 15 has since shipped `isinst`/`castclass` (capability
   `neo-type-checks`), and the IL-exception follow-ups (`neo-il-exception-
   throw`) further confirmed `e is T` isinst works on a caught exception
   (`NeoStep14_ILEx_*` probes, e.g. line 376 `bool isMyEx = e is MyEx;`). So
   TC2 can now assert the exception's type/identity.

## Goals / Non-Goals

**Goals:**
- Make the `Cgt_Un` divergence comment accurate by naming both sentinel cases.
- Tighten TC2 to assert the caught exception's type/identity via `isinst`
  (the now-landed Step 15 opcode), exercising the type-check-in-catch shape.

**Non-Goals:**
- Any change to `Cgt_Un` runtime behavior (the comparison expression is
  unchanged).
- Any change to the `isinst`/`castclass` runtime arms, JIT, or optimizer.
- Any change to a spec REQUIREMENT (this is comment + test only; the
  `neo-type-checks` and `neo-exceptions` specs are UNCHANGED).
- Tightening any other Step 14 TC beyond TC2 (only TC2 was scoped to the
  `isinst`-not-yet-landed constraint).
- The N-CATCHWRAP item (deliberately accepted; matches Legacy).

## Decisions

**D1 — Comment tighten wording (N-CGTUN).** Replace the single-case
"diverges only for the operand-sentinel case" sentence with a sentence that
names BOTH divergences: (a) the operand case `cgt.un x, (uint)0xFFFFFFFF`
(`cguB == -1` short-circuits to `true`) and (b) the symmetric source case
`cgt.un (uint)0xFFFFFFFF, x` (the leading `cguA != -1` clause forces `false`).
Both are the same sentinel-collision class (the `-1 == 0xFFFFFFFF` null
sentinel collides with the integer bit pattern); neither is exercised by the
validated TestCases suite. The runtime expression stays byte-identical.
Rationale: accuracy; no alternative (leaving the comment incomplete was the
status quo this change fixes).

**D2 — TC2 tighten shape (N-TC2).** Replace `if (e != null) return 7;` with a
type/identity assertion via the `is` operator (which lowers to `isinst`):
`if (e is DivideByZeroException && e.Message != null) return 7;`. The
`is DivideByZeroException` exercises `isinst` on the caught exception (the
shape that was Step-15-blocked at Step 14); the `Message != null` keeps a
secondary non-null reachability check. The catch clause already binds
`DivideByZeroException e`, so the `is` check is tautological at the C# type
level but load-bearing at the IL level — it forces the compiler to emit
`isinst` against the caught object. Alternative considered: `e is object`
(always-true, would not exercise the assignability path) — REJECTED.
Alternative considered: assert via `as` (`var ex = e as DivideByZeroException;
if (ex != null) ...`) — equivalent `isinst` emission; `is` reads more cleanly.
Keep the `DivideByZeroException` catch type (do NOT broaden to `Exception`) so
the test stays scoped to its original intent.

**D3 — No spec delta.** This change does not alter any requirement of
`neo-type-checks` or `neo-exceptions`. A `specs/neo-type-checks/spec.md`
delta file is included only to record explicitly that the capability is
UNCHANGED by this change (for traceability — the planner prompt asked for it);
it contains no ADDED/MODIFIED/REMOVED requirement blocks. If the openspec
validator rejects an empty-delta spec, drop the spec delta entirely (the
proposal's "Modified Capabilities: none" already documents the UNCHANGED
status).

## Risks / Trade-offs

- **[Comment drift over time]** → The tightened comment describes the current
  `cguA != -1 && (... || cguB == -1)` expression. If a future change rewrites
  the expression, the comment must be re-tightened. Low risk (the `Cgt_Un` arm
  has been stable since Step 15). Mitigation: the comment stays close to the
  expression it describes.
- **[TC2 over-tighten false-positive]** → If `isinst` on a caught
  `DivideByZeroException` ever regressed, the tightened TC2 would flip from
  PASS to FAIL where the old `e != null` would have stayed green. This is
  DESIRABLE (a regression in `isinst` SHOULD fail a type-check-in-catch test).
  Not a risk to mitigate.
- **[No functional risk]** → Both edits are non-functional. The full `NeoStep`
  smoke (154/154 at HEAD) stays green; TC2 itself stays green (tighten, not
  loosen).

## Open Questions

(none — both items are confirmed against current code; the apply phase only
needs to make the two edits and re-run the smoke.)
