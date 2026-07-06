# Planning Context — neo-step17-generic-byref-etc (LEAD seed)

> SEED for the planner. Read THIS FIRST, then the handoff docs, then research
> only what is missing. APPEND durable findings after propose.

## User intent

Continue the Neo completion portfolio. This child closes the Step 17 **(c)
edges** + the latent **F-10-R1** JIT-discriminator gate. Capability `neo-byref`.
Full autonomy: drive propose->apply->verify->review-loop->ship->archive, LEAD
commits+pushes after.

## Scope (4 sub-items — each needs its OWN dump-gate; do NOT assume all are gaps)

From `.trae/documents/neo-deferred-items.md` (the master-table D-CONSTRAINED row
+ the (c) deferral prepend, lines ~172-212, and the F-10-R1 detail §3, lines
~896-940). READ THOSE for full prior-art.

1. **generic-byref (`ref T` / `out T` with `T` a generic parameter).** A method
   `void Swap<T>(ref T a, ref T b)` called with `T = int` / a CLR struct / an IL
   type. The byref element type is a generic-param type token, not a concrete
   type. Suspected gap: the byref-deref / typed-ref bridge
   (`CopyNeoCallArguments` `PrimitiveByRefSrc`, `NeoMarshalByrefFieldToSlot`,
   the 4c typed-ref bridge) needs a generic-param type-token discriminator.
   DUMP-GATE: a concrete-typed `ref int` works (Step 17 / area4 4c); confirm the
   generic-param form fails and pin where.
2. **`fixed` unmanaged-pinning.** `fixed (int* p = &arr[0])` / `fixed (T* p =
   &field)`. The CIL is `ldflda`/`ldelema` + a pinned-local flag (`localinfo`
   pinned). Suspected gap: either the address itself (likely already works post
   F-6 `Ldflda_Inline` + the ldelema work) or the pinned-local semantics. The
   deferred-items note says "accept-known for `fixed` if a probe shows the
   address works without GC pinning" -- so PROBE FIRST; it may be a no-op
   (address works, GC pinning is a CLR-host concern the interpreter doesn't
   model). Do NOT build a pinned-local flag unless a probe proves it's needed.
3. **interface-on-VT-constrained beyond the common shape.** `constrained.callvirt
   IFace.M` on a value type where the VT does NOT have its own method table slot
   for `M` (the box-then-interface-dispatch shape). The {a,d,M2} cohort
   (neo-step17-completion) + (b) cohort (neo-step17-stobj-refloop) handled the
   common shapes; the "interface-dispatch branch" for the constrained-VT case
   that must box and dispatch via the interface map may still NIE. DUMP-GATE.
4. **F-10-R1 JIT-discriminator gate (the trickiest).** From the master-table row
   (neo-deferred-items.md ~line 95) + §3 (~896-940):
   - An IL value type `struct V { TestVector3NoBinding f; }` taking `ref this.f`
     via `ldflda` inside a VT method gets BOTH the F-6 marker AND the F-10 marker
     stamped (`Operand4 = 0x3`).
   - The runtime checks F-10 (`objIdx >= 0`) BEFORE F-6, predicting a mis-dispatch
     that reads flat bytes as an mStack index.
   - **The correct fix = JIT-discriminator gate:** only stamp F-10 when the
     source is NOT an in-frame VT (making the two markers genuinely
     mutually-exclusive at the PRODUCER).
   - **DO NOT do the runtime reorder (F-6-before-F-10).** That was the reviewer's
     recommendation and was DISPROVEN by the fixer (broke 6 NeoStep17 F-6-only
     probes, 190->184; F-6 shape 3 vs shape 1/2 produce different byrefs).
   - The defect is LATENT today (gated behind the DEFERRED
     `constrained.callvirt`-on-VT; for all reachable VT shapes `objIdx == -1` ->
     HEAD order routes correctly to F-6). Probe 4.8
     (`NeoClrStructField_IlVtMethodLdfldaThisClrStructField`) is a green
     regression guard for the both-stamp shape's `objIdx == -1` routing. So a
     clean reproducer for the mis-dispatch requires CONSTRUCTING the
     constrained-VT shape -- if that's this child's sub-item #3 work, the F-10-R1
     gate becomes load-bearing once #3 lands. Tie them together.

## Probe BEFORE designing (K1 / Q-NEWOBJ / F-10 / array-child lesson reaffirmed)

The array child's propose DISPROVED the LEAD orientation hypothesis via a HEAD
probe -- 2 of 3 "likely gaps" were already working; the dump found the real 3.
Apply the same discipline here: write a probe PER sub-item, run on HEAD
(Debug_Neo), capture the EXACT failure, THEN design. Some sub-items may turn out
to be no-ops (already work) -- record that and scope them out (TEST-ONLY
regression guards, mirroring neo-k2fam-bridge).

## Adversarial probes MANDATORY (Step 17 B1 lesson)

For EACH sub-item that's a real gap: the load-bearing FAIL-on-HEAD -> PASS-after
probe + isolation controls. For generic-byref: `T=int`, `T=CLR-struct`,
`T=IL-type`, `T=IL-VT`. For interface-on-VT-constrained: a VT implementing an
interface, called via `constrained.callvirt` on a generic `T` constrained to the
interface. For F-10-R1: the both-stamp shape via a constrained-VT path that
forces `objIdx >= 0` (if constructible).

## Capability spec delta (propose)

- `neo-byref`: MODIFIED/ADDED Requirements for generic-param byref
  (`ref T`/`out T`), `fixed`-statement address (if a real gap), and
  interface-on-VT-constrained dispatch. F-10-R1's JIT-discriminator gate is a
  `neo-value-types` (or `neo-optimizer`) concern (the marker stamping) --
  propose the delta in whichever capability owns the F-6/F-10 marker (check
  `openspec/specs/neo-value-types/spec.md` + `neo-byref/spec.md`).

## Build + test (CRITICAL)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # CLI, 0 errors
dotnet build TestCases/TestCases.csproj -c Debug                      # TestCases.dll
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep   # ALWAYS -f net8.0; baseline 198/198 (after array child)
```
- Build CLI with `Debug_Neo`; NEVER TestCases with `Debug_Neo`.
- `Debug_Neo` prints huge JIT output -- grep for the summary ("Ran N tests, X
  failded"). >10s = infinite loop -> kill.
- For SHARED-engine edits, confirm Legacy-neutral (plain `Debug` +
  `useRegister=true`, relevant filter -- the Step 17 Legacy filter).
- Build-cache gotcha: confirm the DLL rebuilt after an edit (probe behavior
  must change; or grep a new UTF-16 literal via `strings -e l <dll>`).
- Dump-noise gotcha: to dump-gate a specific runtime arm, add a temp
  `Console.WriteLine` INSIDE the arm (fires only when it executes for the probe),
  run the single probe, then remove it.

## Codebase gotchas (full detail in handoff section 4)

- `OpCodeR` is `[StructLayout(Explicit)]`; `LowerNeoOffsets` overwrites register
  indices with byte offsets. The F-6/F-10 markers live in standalone `Operand4`
  (NOT aliased -- safe to read post-lowering). Snapshot `preOp = op` before a
  lowering case mutates.
- Shared vs Neo-only: the F-6/F-10 marker stamping is in `TypeSpecializeNeoOpcodes`
  (`JITCompiler.cs`, Neo-only, file/run-gated). The byref machinery is in
  `Optimizer.Neo.cs` + `ILIntepreter.Neo.cs` (Neo-only). CLR binding `*Neo`
  variants only.
- Legacy `ExecuteR` is the REFERENCE; never modify Legacy to make Neo work.
- Test harness is NOT xUnit: `public static` parameterless methods; throw-
  asserting is hard (use DivideByZero pattern or try/catch flag).
- Write tool corrupts ~0.5% of CJK on large payloads; author ASCII-primary.

## Likely fix sites (dump-locked; do NOT commit to these until probed)

- **generic-byref:** `CopyNeoCallArguments` `PrimitiveByRefSrc` deref
  (`Optimizer.Neo.cs` / `ILIntepreter.Neo.cs`) + the 4c typed-ref bridge +
  `NeoMarshalByrefFieldToSlot` -- the generic-param type token must resolve to
  its concrete substitution at the call site.
- **`fixed`:** likely NO engine change (address works via F-6/ldelema); a
  regression-guard TEST-ONLY outcome is plausible (mirror neo-k2fam-bridge).
- **interface-on-VT-constrained:** the `Constrained` arm in `ILIntepreter.Neo.cs`
  (the runtime arm that owns dispatch) -- add the box-and-interface-dispatch
  branch.
- **F-10-R1:** the `case Ldflda:` marker stamping in `TypeSpecializeNeoOpcodes`
  (`JITCompiler.cs`) -- gate the F-10 stamp on "source is NOT an in-frame VT"
  (the discriminator), keeping the F-6 stamp. Verify with the both-stamp probe +
  the 6 F-6-only probes (must stay green -- the DISPROVEN reorder broke them).

## Regression risk: MEDIUM-HIGH.

Touches the byref machinery + the F-6/F-10 marker stamping (shared by every VT
field-address access) + possibly the Constrained dispatch arm. Gate: full
`NeoStep` smoke (198/198 baseline) + the Step 17 Legacy filter + NeoOptHard 24/24.
Adversarial probes MANDATORY. STOP if the F-10-R1 gate regresses the F-6-only
probes (that means the discriminator is wrong -- re-dump, do NOT runtime-reorder).

## Maintain this file

APPEND durable findings after propose (per-sub-item dump results, locked fix
sites, scoped-out no-ops). Do NOT append chatter.

## Findings -- neo-step17-generic-byref-etc (propose, 2026-07-06)

Dump-gate ran per sub-item on HEAD `0aafdb34` (NeoStep 198/198 baseline; Debug_Neo
CLI, TestCases rebuilt per probe). The LEAD orientation hypothesis was DISPROVEN
for 3 of 4 sub-items -- the dump found exactly ONE real gap. This is a MUCH
smaller change than the seed anticipated.

### Per-sub-item dump verdict

1. **generic-byref (`ref T`/`out T`, T generic) -- NO-OP.** 3/3 probes PASS on
   HEAD unmodified (`Swap<int>`, `Swap<IL-ref-class>`, `Swap<IL-value-type>`).
   The byref model is type-agnostic: an 8-byte Ref Slot copied regardless of
   element type; the generic-param token is resolved at the call site (JIT
   generic substitution), not at the byref-marshal level. The seed's "confirm
   the generic-param form fails and pin where" hypothesis is DISPROVEN. -> 3
   TEST-ONLY regression guards (mirror `neo-k2fam-bridge`).
2. **`fixed` unmanaged-pinning -- DEFER (out of scope).** `fixed (int* p = arr)`
   FAILS on HEAD with `Neo: opcode Conv_U not yet implemented (Step 6)` (and
   `Ldtoken` if an array initializer is used). The `fixed` statement lowers to
   raw-pointer opcodes (`conv.u`/`Conv_U` converts the pinned array ref to a
   native `int*`). The array-element ADDRESS works via `ref arr[i]` (TC14 green
   via `ldelema` + `stind`/`ldind`); GC pinning is a CLR-host no-op. The gap is
   NOT byref machinery -- it is the unimplemented `Conv_U`/`Conv_I` pointer-
   conversion opcodes (outside `neo-byref`). -> DEFER; route to a future
   pointer/`Conv_U` step. Do NOT implement `Conv_U` here (K1/Q-NEWOBJ lesson).
3. **interface-on-VT-constrained beyond the common shape -- NO-OP.** 2/2 probes
   PASS on HEAD unmodified (IL-VT via generic constrained caller -> direct-call;
   CLR-VT via generic constrained caller -> box-once). The {a,d,M2} cohort
   (`neo-step17-completion`) + (b) cohort (`neo-step17-stobj-refloop`) already
   cover the box-and-interface-dispatch + IL-VT-direct-call + CLR-VT-box-once +
   IL-VT-inherited-CLRMethod paths. The seed's "box-and-interface-dispatch branch
   may still NIE" hypothesis is DISPROVEN. -> 2 TEST-ONLY regression guards.
4. **F-10-R1 JIT-discriminator gate -- REAL GAP (the only engine change).** The
   latent both-stamp defect IS reachable. Probe: IL VT `struct V { int prefix;
   TestVector3NoBinding field; }` implementing `IFace`, method `M` does
   `ldflda this.field` (passes `ref this.field` to a host byref helper),
   invoked via `constrained.callvirt` direct-call (generic caller
   `T v where T:struct,IFace`). FAILS on HEAD with `NullReferenceException` at
   `NeoMarshalByrefFieldToSlot` (ILIntepreter.Neo.cs:466).

### F-10-R1 root cause (dump-confirmed)

The constrained direct-call path (ILIntepreter.Neo.cs:4187-4236) copies the
struct's FLAT PRIMITIVE bytes (just `prefix`, 4 bytes -- the CLR-struct field is
a reference slot, zero primitive contribution) into callee slot-0. The body's
`ldflda this.field` then reads slot-0's leading int = `prefix` value (e.g. 7) as
the byref `objectIndex`. BOTH markers stamp (`Operand4 = 0x3`: F-6 in-frame-VT
`0x1` OR F-10 CLR-struct-field `0x2`; `IsClrStructFieldOfIL` returns true for an
IL value-type declaring type too -- it checks `declaringType is ILType`, not
`!IsValueType`). The runtime Ldflda arm checks F-10 FIRST
(`clrStructFieldMarker && objIdx >= 0`) -> `true && 7 >= 0` -> produces
`(objIdx=7, ReferenceOffset | flag)`. The consumer reads `mStack[7]` (a garbage
slot -- the struct was NOT boxed in the direct-call path) -> NRE at
`NeoReadClrObjectField(appdomain, null/wrong, off)`.

### F-10-R1 fix -- LOCKED (JIT-discriminator gate, type-spec-pass form)

- **Site:** `JITCompiler.cs TypeSpecializeNeoOpcodes case OpCodeREnum.Ldflda:`
  (~line 862, the F-6 stamping block). When F-6 stamps
  (`srcType is ILType srcIl && srcIl.IsValueType && !srcIl.IsEnum`), CLEAR any F-
  10 the main-JIT body emission set: `op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;`.
  ~2 lines. Neo-only (the pass is `#if ENABLE_NEO_MODE`); Legacy-neutral by
  construction.
- **Dump-gate PROOF:** during propose, the equivalent gate (temporarily applied
  as `!type.IsValueType` at the body-emission F-10 site, JITCompiler.cs:2433)
  made the probe PASS; NeoClrStructField 8/8 unaffected; full NeoStep smoke
  clean except the deferred-`fixed` probes. Reverted (propose leaves the engine
  clean for the implementer).
- **Type-spec-pass gate, NOT declaring-type gate:** the `!type.IsValueType` form
  (used in the temp dump-gate) works for the probe but is SUBTLY WRONG for the
  boxed-IL-VT-with-CLR-struct-field case (declaring type is a value type, but
  the operand is a heap boxed object that correctly needs F-10). The type-spec-
  pass gate keys on the OPERAND's value-category (in-frame VT vs heap/boxed) --
  exactly the F-6 condition -- so it is correct for in-frame-VT (F-10 cleared),
  heap-IL-class (F-10 stays), AND boxed-IL-VT (F-10 stays). Implementer applies
  the type-spec-pass form.
- **Runtime arm UNCHANGED.** The F-10-first check order stays. The reviewer's
  recommended runtime F-6-before-F-10 reorder was DISPROVEN (broke 6 NeoStep17
  F-6-only probes, 190->184); the JIT gate does NOT touch F-6 stamping.

### Scoped-out no-ops / deferrals

- generic-byref: NO-OP (type-agnostic model). 3 regression guards.
- interface-on-VT-constrained: NO-OP ({a,d,M2,b} coverage). 2 regression guards.
- `fixed`: DEFER (blocked by `Conv_U`/`Conv_I`, out of `neo-byref` scope; the
  address works via `ref arr[i]`). Reroute in `neo-deferred-items.md` from
  "accept-known for fixed" to "needs `Conv_U`/pointer step".
- Stfld_Ref / Ldfld_Ref F-10 hash: NOT changed (the `_Inline` rewrite diverts
  the in-frame-VT case; apply-phase JIT-dump verifies no in-frame-VT path reads
  the F-10 hash).

### Adversarial-probe plan (MANDATORY)

- **F-10-R1 (FAIL-on-HEAD -> PASS-after):** `NeoStep17_F10R1_ConstrainedVtLdfldaClrField`.
- **F-10 heap-IL regression (PASS on HEAD, MUST stay PASS):** 8
  `NeoClrStructField_*` probes + NeoStep20 9/9 (F-10 is load-bearing for Step 20).
- **generic-byref (PASS on HEAD):** 3 `NeoStep17_GenericByRef_*` guards.
- **interface-on-VT (PASS on HEAD):** 2 `NeoStep17_InterfaceOn*VtConstrained` guards.
- **F-6-only (PASS on HEAD, MUST stay PASS):** 6 `_LdfldaInline_*` / `_TC6_*`
  probes -- the DISPROVEN-reorder guard (the JIT gate must NOT perturb F-6).

### Regression risk: LOW-MEDIUM.

Single Neo-only JIT-pass edit (~2 lines). Gate: full NeoStep smoke + NeoClrStructField
8/8 + NeoStep20 9/9 + NeoOptHard 24/24 + the Step 17 Legacy filter. STOP if the
gate regresses the F-6-only probes (means the discriminator is wrong -- re-dump,
do NOT runtime-reorder).

## Findings -- neo-step17-generic-byref-etc (apply, 2026-07-06)

Apply complete. The propose-phase plan held exactly -- no surprises, no STOP.
Headline: the F-10-R1 reproducer flipped FAIL-on-HEAD (NRE) -> PASS-after, and
the full regression set is intact.

### The fix (exact)

- **Site:** `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`,
  `TypeSpecializeNeoOpcodes`, `case OpCodeREnum.Ldflda:`, inside the F-6 stamping
  block. The single new engine line is at **line 913**:
  `op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;` (placed immediately after the
  existing `op.Operand4 |= NeoLdfldaInlineMarker;` at line 877), with the full
  rationale comment. No other engine code changed; runtime arm UNCHANGED.

### The gate form chosen (and why NOT `!declaringType.IsValueType`)

- **Type-spec-pass form** (the LOCKED propose plan). The gate clears F-10 inside
  the F-6 stamping block, i.e. it keys on the OPERAND's value-category
  (`srcType is ILType && IsValueType && !IsEnum` -- exactly the F-6 condition).
- **Rejected:** the naive `!declaringType.IsValueType` form at the F-10 body-
  emission site. It would silently suppress F-10 for the **boxed-IL-VT-with-CLR-
  struct-field** case (declaring type is a value type, but the operand is a heap
  boxed mStack object that correctly needs F-10). The type-spec-pass gate keys on
  the operand, not the declaring type, so it is correct for in-frame-VT (F-10
  cleared), heap-IL-class (F-10 stays), AND boxed-IL-VT (F-10 stays).
- **JIT-dump proof (task 1.2):** a temp diagnostic in the type-spec `case Ldflda:`
  arm printed `Operand4_entry` (before the F-6 stamp) and `Operand4_after` (after
  the clear). For the F-10-R1 probe's two `ldflda this.field` sites:
  `entry=2 -> after=1` (body stamped F-10, gate cleared it, F-6 remains). For the
  F-6-only probe's `ldflda this.id` site: `entry=0 -> after=1` (F-10 was never
  stamped; clear is a no-op; F-6 untouched). Temp probe removed; clean build
  confirmed.

### Reproducer result

- `NeoStep17_F10R1_ConstrainedVtLdfldaClrField` (the IL VT
  `struct { int prefix; TestVector3NoBinding field; }` invoked via a generic
  constrained caller `T v where T:struct,IFace`, body `ldflda this.field`):
  **FAIL-on-HEAD** (`Object reference not set to an instance of an object` -- NRE
  at `NeoMarshalByrefFieldToSlot`, reading `prefix` value 7 as objectIndex) ->
  **PASS-after** (returns 60, the correct sum).
- **Stash-toggle (task 3.7):** gate OFF -> reproducer NREs again; the 6 F-6-only
  (`NeoStep17_LdfldaInline_*` 8/8 + TC6) and the 8 `NeoClrStructField_*` probes
  stay green. Gate re-applied; reproducer PASS again.

### Regression set INTACT (no STOP)

- **Full NeoStep smoke:** 204/204, 0 fail (= 198 baseline + 6 new keepers: 3
  generic-byref + 2 interface-on-VT TEST-ONLY + 1 F-10-R1 FAIL->PASS).
- **NeoClrStructField:** 8/8 (heap-IL F-10 path -- F-10 stays, byte-identical).
- **NeoStep20:** 9/9 (F-10 load-bearing for Step 20 sync).
- **NeoOptHardTest:** 24/24.
- **F-6-only probes:** `NeoStep17_LdfldaInline_*` 8/8 + `NeoStep17_TC6_*` -- the
  DISPROVEN-reorder guard. The gate does NOT perturb F-6.
- **Legacy-neutral (task 3.6):** plain `Debug` CLI + `useRegister=true`,
  NeoStep17 filter 47/47 green (incl. the 6 new keepers). The gate is compiled
  out under `!ENABLE_NEO_MODE`; Legacy byte-identical.

### Scoped-out no-ops / deferrals (unchanged from propose)

- generic-byref: NO-OP. 3 TEST-ONLY guards.
- interface-on-VT-constrained: NO-OP. 2 TEST-ONLY guards.
- `fixed`: DEFER (rerouted to a pointer/`Conv_U` step; blocked by unimplemented
  `Conv_U`/`Conv_I`; the address works via `ref arr[i]`). Recorded in
  `neo-deferred-items.md` D-CONSTRAINED (c).
- Stfld_Ref/Ldfld_Ref F-10 hash: NOT changed (task 1.3 -- the F-10-R1 probe body
  is pure `ldflda this.field; call`, no stfld/ldfld on the CLR-struct field; the
  in-frame-VT `_Inline` rewrite diverts any stfld/ldfld away from the F-10 hash;
  heap-IL keeps F-10 and uses the regular Stfld_Ref/Ldfld_Ref).
