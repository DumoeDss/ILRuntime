# Ship Log — neo-activator-createinstance-neo-redirect (child 22)

**Change:** register `CLRRedirections.CreateInstance/2/3` Neo equivalents on `RedirectMapNeo` so
`Activator.CreateInstance` on an IL type under Neo invokes `ILType.Instantiate()` instead of falling
through to the broken autogen stub (same defect class as child-6 `InitializeArrayNeo`).
**Capability:** `neo-dispatch` (ADDED requirement).
**Pipeline:** small-feature (triage-re-audit -> propose+apply -> verify -> review-clean -> ship).
**Date:** 2026-07-13. **Branch:** `features/object-model-overhaul`. **Base HEAD:** `695379db`.

## Triage winnow (this child came from a 3-candidate re-audit)
- `neo-il-static-field-roundtrip`: DISPROVEN (child-13 closed it). No child.
- `neo-clr-vt-refcount-stobjldobj`: UNREACHABLE (ref-field CLR struct NIEs at the ctor before
  materializing; the only non-NIE path copies the GC pointer within the CopyBlock'd bytes). No child.
- `neo-activator-createinstance-nre`: DISPROVEN-as-framed (the `GetStaticFieldOffset` NRE does not
  reproduce) but REFRAMED to the real gap below. -> this child.

## What shipped (Neo-only, `#if ENABLE_NEO_MODE`, ~123 engine lines, Legacy-neutral)
- **`ILRuntime/Runtime/Enviorment/CLRRedirections.cs`** (+103): `WriteNeoObjectResult` helper (~:657,
  emits the `-1` null sentinel on null — distinct from `WriteNeoDelegateResult` which writes a valid
  index even for null, correct for Combine but wrong for general object returns) +
  `CreateInstanceNeo`/`CreateInstance2Neo`/`CreateInstance3Neo` (~:683/704/727). Signature mirrored from
  `InitializeArrayNeo`/`DelegateCombineNeo`; params via `ReadNeoReference`; three overloads reproduce the
  hand-written Legacy redirects: generic reads `method.GenericArguments[0]`; Type overloads
  `ReadNeoReference` the Type (+ object[]); IL -> `ILType.Instantiate()`/`Instantiate(args)`, CLR ->
  `CreateDefaultInstance()`/host `Activator`.
- **`ILRuntime/Runtime/Enviorment/AppDomain.cs`** (+20): three `RegisterCLRMethodRedirectionNeo` calls
  folded into the existing Activator `foreach` (~:176/186/193), mirroring the Delegate.Combine block.
  Legacy `RedirectMap` registrations untouched.
- **`TestCases/NeoStepActivatorCreateInstanceTest.cs`** (new): 3 probes (generic + Type + Type+object[]).

## Why
`AppDomain.cs:162-176` registered the hand-written `CreateInstance/2/3` (which do `ILType.Instantiate()`
for IL types) on Legacy's `RedirectMap` ONLY. Neo dispatch uses `RedirectMapNeo` exclusively (child-2),
so under Neo the call fell through to the broken autogen `CreateInstance_*_Neo` stub ->
`MissingMethodException: No parameterless constructor for ILTypeInstance`. Neo-specific (Legacy 2/2 PASS).

## Verification
- **NeoStep smoke: 361/0** (358 baseline + 3 probes), no regressions, EXIT=0.
- **Stash-toggle (airtight):** stash `CLRRedirections.cs`+`AppDomain.cs` (keep probe) -> rebuild -> **3/3
  FAULT** (`MissingMethodException` via autogen `CreateInstance_1_Neo`, exact triage error); pop ->
  rebuild -> **0/3 PASS**.
- **`ActivatorCreateInstanceWithArgsTestSimple`: PASSES** under Neo.
- **Legacy-neutral:** plain `Debug`+`useRegister=true`+NeoStep = 361 ran/18 failed (18 = pre-existing
  Neo-specific set; the 3 probes PASS under Legacy; the Legacy `RedirectMap` registrations are untouched).

## Review
**APPROVE-WITH-FINDINGS** (reviewer != implementer; independent re-run of smoke + stash-toggle +
calling-convention/precedence cross-check).
- **0 Blocker / 0 Major.**
- Minor M1: `CreateInstance3Neo` guards its null-args check with `if (args != null)` (Legacy NREs on null
  args via `t2.Length`) — more robust, unreachable in smoke, documented divergence.
- Minor M2: `CreateInstanceNeo` does not reproduce Legacy's `AllocValueType` branches for IL value types
  / CLR-VTs-with-binders (calls Instantiate/CreateDefaultInstance) — documented as deferred, unreachable
  in smoke, replaces a broken TODO stub.
- 2 Trivial: tasks.md narrative imprecision (the big `WithArgsTest` crashes at the first ToString before
  reaching the Type+object[] calls; that path is covered by the TC3 probe instead); `WriteNeoObjectResult`
  near-duplicates `WriteNeoDelegateResult` (justified — Combine can yield null-as-valid-index).
- Pre-existing P1 (the instance-field `== null` ceq gap — see Surfaced follow-up) + P2 (moot autogen
  `CreateInstance_4_Neo` stub) noted out-of-scope.

## Delivery
**Mode:** local commit (portfolio per-child delivery; push per parent directive). Committed with the
portfolio-run.json + planning-context.md record updates. No PR.

## Durable findings (for future planning)
1. **The "missing Neo redirect" defect class (child-6 lineage) is cleanly resolved by
   `TryGetRedirection`'s generic-definition-precedence** (`CLRMethod.cs:111-131`): registering ONE Neo
   redirect for the generic `Activator.CreateInstance<T>()` definition preempts EVERY autogen
   per-instantiation stub. For non-generic overloads, first-registered-wins (AppDomain ctor before the
   test-harness autogen `Register`) does the same. USE THIS PATTERN for any future "autogen Neo stub is
   broken" gap.
2. **A Neo object-return redirect needs a null-aware writer distinct from `WriteNeoDelegateResult`** (the
   latter writes a valid index even for null — correct for delegate-Combine, wrong for general object
   returns). `WriteNeoObjectResult` emits the `-1` null sentinel — the convention `ReadNeoReference` and
   `InvokeNeoClrMethod:1082-1084` use.
3. **A null reference field under Neo is a valid mStack index pointing to null (not the -1 sentinel)** —
   so `refField == null` (lowered to `ceq`) compares the index against ldnull's -1 and reads FALSE. This
   instance-field ceq-null gap fires for ANY IL instance (not just Activator) — see the surfaced
   `neo-ceq-null-instance-field` follow-up.

## Surfaced follow-up
**`neo-ceq-null-instance-field`** (HIGH-correctness, the next candidate): `ActivatorCreateInstanceWithArgsTest`
still fails — NOT on Activator (instance created with correct field values; proven via plain `new
DiagData()` + `field == null` ternary reproducing identically) — but on `ILValue == null` in `ToString()`.
The instance-field form of the null-comparison gap (child-11/12 fixed the static-ref forms); Ceq_Ref is
not firing for `ldfld.ref <instance field>; ldnull; ceq`. Re-audit why (temp local not marked
LocalIsReference? an unseeded-producer class like child-21?).
