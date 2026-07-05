## Context

K2-FAM was tracked in `neo-deferred-items.md` §3 as a PARTIAL close: the Step 13b
apply pass (2026-07-04) closed the **return-source** shape (a CLR struct local
obtained from a CLR method RETURN, stored as flat bytes) but DEFERRED the
**Box/Initobj-source** shape (a CLR struct local sourced from Box/Initobj, passed
by value) because "it needs IL-side ldfld/stfld on CLR struct fields for a clean
reproducer."

Since then, three changes landed whose combined effect was NOT re-assessed against
K2-FAM until this proposal:

1. **`neo-opt-harden-2` (F-MAJ-1, 2026-07-05)** — declared a CLR-VT LOCAL as flat
   bytes (`Size = GetNeoValueTypeManagedSize, RefCount = 0, localIsRef = false`)
   in `JITCompiler.AllocateLocalStackSpaces` under `#if ENABLE_NEO_MODE`. The OLD
   boxed-ref representation (`Size=4, RefCount=1`) — the source of the
   "int-as-mStack-index" corruption — NO LONGER EXISTS for a CLR-VT local.
2. **`neo-opt-harden-2` review-fix (round 1)** — rewrote the three runtime arms
   that still assumed the boxed-ref representation: `Initobj` (M1:
   `Unsafe.InitBlock` flat-bytes zero, no mStack write), `Box` (M2:
   `ReadNeoValueType` flat-bytes read into an independent boxed copy), and
   `Unbox_Any` dest (twin of M2: `WriteNeoValueType` flat-bytes write). So a
   CLR-VT local sourced from Box/Initobj/Unbox is FLAT BYTES end-to-end.
3. **`implement-neo-step13b`** — the by-value-param read in `CopyNeoCallArguments`
   byte-copies N flat bytes from the caller local's `Offset` (the unified
   `AllocateNeoCallParamSlot` callee layout).

The conjunction (1)+(2)+(3) means a CLR-VT local — regardless of source
(return / Box / Initobj / Unbox) — is flat bytes, and passing it by value reads
flat bytes. The "int-as-mStack-index" mis-copy is no longer reachable.

**Reproducibility assessment (done on HEAD `f7539642` BEFORE proposing).** Six
adversarial reproducer probes were constructed and run individually against the
HEAD build. All six PASS:

| Probe | Source shape | Result |
|-------|-------------|--------|
| Box→Unbox→by-value | local sourced from `(T)o` after `o = v` | PASS |
| Initobj→by-value | local sourced from `default(T)` | PASS |
| Box→Unbox→Move→by-value | struct-copy chain then by value | PASS |
| Re-initobj→by-value | `t = default(T)` re-init then by value | PASS |
| Box→Unbox→by-value-to-host-static | by-value to `SumTestVector3NoBindingFields` | PASS (returns 15) |
| Two Box-sourced struct locals, both by-value | F-MAJ-1 two-live-struct stress, Box-sourced | PASS (600 + 3) |

A seventh probe (instance method `t.LengthSquaredInt()` / field read `t.x` on a
CLR struct parameter INSIDE an IL-defined method body) FAILS with
`Neo: opcode Ldfld not yet implemented (Step 6)`. This is the SEPARATE pre-existing
`[NEO-IL-VT-INSTANCE-COVERAGE]` gap (Ldfld on a CLR struct field from IL — also
surfaced by the `neo-step13-area4` review and Step 19 TC10), NOT K2-FAM. It is
out of scope here. The K2-FAM probes deliberately AVOID this gap by routing
field reads through host helpers (`TestCLRBinding.SumTestVector3NoBindingFields`)
or `constrained.callvirt`-free shapes, not through IL-side `Ldfld`.

The smoke baseline at HEAD is NeoStep 140/140 (after `neo-step19-delegate`).
Legacy 518/519 (the standing regression reference).

## Goals / Non-Goals

**Goals:**
- Lock the K2-FAM closure in with adversarial regression guards so a future
  change that re-introduces a boxed-ref CLR-VT local representation — or breaks
  any of the `Box`/`Initobj`/`Unbox_Any` flat-bytes arms — is caught by the
  NeoStep smoke, not silently shipped (the Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1
  lesson: a green smoke does NOT prove a representation/gate correct unless an
  adversarial probe exercises the specific corruption class).
- Close the K2-FAM deferred item as RESOLVED (subsumed) in the deferred-items
  tracker and the `neo-boxing` capability spec.
- Document the resolution path so a future planner does not re-litigate K2-FAM
  or mis-attribute the fix.

**Non-Goals:**
- Any runtime / JIT / optimizer / CLR-binding source change. The fix already
  shipped; this change ships no engine code.
- The `[NEO-IL-VT-INSTANCE-COVERAGE]` Ldfld-on-CLR-struct gap (the 7th probe's
  failure class). That is a separate, tracked Step-6 follow-up.
- The F-2 / INLINER-REFONLY-VT inliner ref-fold gap (a distinct defect class for
  ref-only VT locals; remains deferred under its own follow-up).
- The remaining `neo-boxing` deferrals (CLR-method `ref`/`out` Area 4c;
  CLR-object `stind`/`ldind` Area 4d; struct-with-ref-fields-no-binder). Untouched.

## Decisions

**D1: This is a TEST-ONLY change (no engine fix).** The probes that constitute
the exact K2-FAM shape all PASS on HEAD. Shipping an engine fix for a
non-reproducing defect would be worse than none (the explicit K1 / Q-NEWOBJ /
Q-STRUCT / Q-LONG lesson — never ship a guessed fix). The whole value of this
change is the regression guards + the spec/tracker closure.

**D2: Probes live in `TestCases/NeoStep13bTest.cs` (extend), NOT a new file.**
Step 13b is the existing K2-FAM home; `NeoStep13_K2FamRegression` (the
return-source probe) already lives there. Co-locating the Box/Initobj/Unbox
source probes with it keeps the K2-FAM coverage in one place and avoids a new
file that the smoke filter would need to be made aware of (the `NeoStep` filter
is a substring `Contains`; the `NeoStep13_` prefix is already caught). Naming
convention: `NeoStep13_K2Fam_<SourceShape>` so a `K2Fam` filter also groups them.

**D3: Each probe uses the DivideByZero-assertion pattern (`int _ = 1/0` on a
wrong result), NOT a `throw new` or `[ExpectedException]`.** The harness cannot
author `throw new T()` for an arbitrary T, and `[ExpectedException]` does not
exist. The K2-FAM defect class is a SILENT wrong result (a corrupted field sum),
not a throw — so a value-path assertion is the correct shape. Each probe reads
back a field sum via a host helper (`TestCLRBinding.SumTestVector3NoBindingFields`,
which takes the struct by value and returns the int field sum) and asserts the
expected value.

**D4: Use `TestVector3NoBinding` (no binder, pure-primitive 3-float struct) for
all probes.** This exercises the reflection-fallback `CLRMethod.Invoke(byte*)`
path (the autogen redirect only covers STATIC members of the host struct), which
is the same path the existing 13b probes use. A struct WITH reference fields
would hit the separate struct-with-refs-no-binder NIE guard (out of scope).

**D5: The `TwoBoxedStructLocalsByValue` probe is the strongest guard.** It
mirrors the F-MAJ-1 two-live-struct stress (the corruption class a green smoke
MISSED), but sources both locals from Box→Unbox instead of method returns. If a
future change re-introduces an under-sized local declaration or a boxed-ref
representation, this probe's neighbour-corruption check (`r1==600 && r2==3`)
fires — exactly the F-MAJ-1 signature. This is the load-bearing probe.

**Alternatives considered.**
- *Ship an engine fix anyway (a "bridge" in the Move path).* REJECTED: the
  Move path's `if (ip->Operand == 1)` reference-copy branch only fires for a
  reference-typed Move (a boxed-ref dest). With D1 (locals are flat bytes), a
  CLR-VT local Move is `Move_Vt` or a plain byte CopyBlock — neither reads an
  mStack index. There is nothing to bridge.
- *Add the 7th probe (instance-call-on-unboxed-local) as a keeper.* REJECTED:
  it fails on HEAD with a Step-6 NIE that is NOT K2-FAM. Shipping a failing
  probe would regress the smoke. It belongs to the `[NEO-IL-VT-INSTANCE-COVERAGE]`
  follow-up.
- *A new `NeoK2FamTest.cs` file.* REJECTED: `NeoStep13bTest.cs` is the K2-FAM
  home; a new file fragments the coverage and gains nothing (the filter is a
  substring).

## Risks / Trade-offs

- **[A future change reverts the flat-bytes CLR-VT local declaration and the
  smoke does not catch it]** → Mitigation: D5 (the `TwoBoxedStructLocalsByValue`
  probe) plus the four single-local Box/Initobj/Unbox probes collectively cover
  the boxed-ref-corruption signature. Any of them turns red if the
  representation regresses.
- **[A probe inadvertently exercises the `[NEO-IL-VT-INSTANCE-COVERAGE]` Ldfld
  gap and fails on HEAD]** → Mitigation: every probe routes field reads through
  host helpers (D3/D4); no IL-side `Ldfld` on a CLR struct field. The 7th probe
  was explicitly DROPPED for this reason (validated: it NIEs on HEAD).
- **[A probe passes on Neo but fails on Legacy, breaking the Legacy
  NeoStep-filter run]** → Mitigation: the probes are representation-agnostic
  (they assert field-sum correctness, which holds on both engines). Legacy's
  boxed-ref CLR-VT local representation is unchanged (the F-MAJ-1 fix is
  `#if ENABLE_NEO_MODE`-gated), and Legacy's Box/Unbox/Initobj have always
  handled the by-value-param shape correctly. The probes are expected to pass
  on Legacy too (the apply phase confirms).
- **[Test-only change is mis-perceived as "no real work"]** → Mitigation: the
  reproducibility assessment (6 probes run on HEAD, all PASS) is the
  load-bearing deliverable, documented in this design + the planning-context
  findings. The regression-guard value is real (Step 17 B1 / F-MAJ-1 both
  taught that a green smoke can hide a representation bug; these probes prevent
  that for K2-FAM specifically).

## Migration Plan

None. Test-only; no migration, no rollback. Apply = add probes + run smoke;
ship = commit + push; archive = sync spec delta + move change dir.

## Open Questions

None. The reproducibility assessment (the only open question — does K2-FAM still
fail?) is resolved: it does not. All design decisions follow deterministically.
