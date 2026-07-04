# Review Report — implement-neo-step13b (Neo Step 13b: CLRMethod unified param layout — Area 5 core)

**Reviewer:** verify-stage leaf (adversarial; author != verifier).
**Date:** 2026-07-04.
**Branch:** `features/object-model-overhaul` (working tree vs HEAD `21b68d92`).
**Scope of diff reviewed (the actual 13b change, working tree):**
`Optimizer.Neo.cs`, `ILIntepreter.Neo.cs`, `CLRMethod.cs`,
`BindingGeneratorExtensions.cs`, `ILRuntimeTestBase/TestFramework/TestClass3.cs`,
`TestCases/NeoStep13bTest.cs`.

> Note on scope: `git diff origin/master` shows a large refactor in
> `MethodBindingGenerator.cs` (the `GenerateOperatorCodeNeo` extraction + the
> `#if ENABLE_NEO_MODE` Neo-registration split). That is **branch-wide history**
> (commits since master), NOT this change — `git diff` of the working tree vs
> HEAD `21b68d92` does **not** list `MethodBindingGenerator.cs`. 13b did not
> touch the generator's wrapper code; only the `*Neo` argument/return extensions
> in `BindingGeneratorExtensions.cs` changed.

---

## Executive verdict: **FINDINGS (1 Major, 1 Minor, 3 Info) — ship-able, the Major is a documented pre-existing frame-layout bug, not a 13b defect**

The 13b change is **correct and legacy-neutral** for everything it claims to do.
The K2 (by-value CLR-struct param) and return-value paths are genuinely fixed
and byte-consistent by construction (single size source). The CLR-binding canary
and the full NeoStep smoke are **green (84/84)** with zero regression.

The one Major is **Finding V** — a **silent-wrong-result frame-slot-reuse bug**
that bites when IL code holds **2+ CLR struct locals alive simultaneously**
(e.g. two `Vector3`s). It is **pre-existing** (pre-13b the feature threw NIE, so
the pattern was simply unsupported — it is not a regression of a green case),
and the implementer already documented it as out-of-scope + wrote around it. I
**reproduced it deterministically** and confirmed it is **NOT introduced by 13b**
(via stash toggle). It is a `AllocateLocalStackSpaces` liveness/slot-reuse defect
surfaced by the new 12-byte struct local slots. Recommend shipping 13b as-is and
filing the slot-reuse bug as the explicit next item.

---

## Severity counts

- **Blocker: 0**
- **Major: 1** (Finding V — pre-existing, reproduced, documented, out-of-scope)
- **Minor: 1** (F-MIN-1 — layout reserves ref slots for binder structs that no
  reader consumes; latent, guarded by NIE today)
- **Info: 3** (F-INFO-1 ref-field binder NIE asymmetry; F-INFO-2 doc nit;
  F-INFO-3 coverage gap on the autogen D5 path)

---

## Findings

### F-MAJ-1 — Silent wrong result with 2+ simultaneous CLR struct locals (Finding V; reproduced; PRE-EXISTING, not a 13b regression)

**Severity:** Major (silent wrong result on a realistic pattern) — but
**out of 13b's scope**, pre-existing, and already documented by the implementer
(planning-context.md §7 Finding V). **Not a blocker for 13b.**

**File:** `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs`
(`AllocateLocalStackSpaces`) — the slot-reuse / liveness logic, not any file
13b touched.

**What (reproduced deterministically):** A test holding TWO CLR struct locals
`v`, `w` (each from a CLR method return) and deriving two ints
`r1 = Sum(v); r2 = Sum(w)` then checking `r1 != 600 || r2 != 3` fails the
`1/0` assertion. I isolated it: **`if (r1 != 600)` alone fails AND
`if (r2 != 3)` alone fails** — so it is not r1/r2 cross-clobber; each value is
individually wrong when both struct locals are live. Symptom = one (or both) of
the 12-byte struct locals' frame slots is corrupted by the second `Make(...)`
return write / by slot reuse while the first local is still live.

**Why it is NOT a 13b defect (stash-toggle proof):**
- I stashed the 4 runtime 13b files (keeping `TestClass3.cs` + the probe) and
  rebuilt the CLI against pre-13b runtime. Probe-A then fails with
  `CLR value type return in reflection fallback: Step 13` (NIE) — i.e. the
  pattern **could not run at all pre-13b**; the struct-return path did not exist.
  Restoring 13b reproduces the silent-wrong-result. So 13b did not break a green
  case; it newly-enabled a pattern that hits an older slot-reuse bug.
- I also confirmed the bug is **struct-local-specific**: a probe with two
  CLR **int** returns + a combined check (`ProbeTwoClrIntCalls`) **passes both
  on pre-13b and on 13b**. Pure-int-constant checks pass. The corruption needs
  the 12-byte struct local slots.

**Severity justification:** Realistic in the primary ILRuntime use case
(game/graphics IL hot-update holding several `Vector3`/`Quaternion` locals).
It is **silent** (no exception; wrong arithmetic result). However: (a) it is
not a regression of any previously-green behavior, (b) the implementer already
flagged it, (c) the shipped NeoStep13b tests deliberately hold a single struct
local at a time to avoid it, and (d) fixing it belongs to
`AllocateLocalStackSpaces` slot-reuse liveness for mixed struct+primitive
locals — a separate, larger piece of work unrelated to the call ABI.

**Recommendation:** Ship 13b. File the slot-reuse bug as the explicit next
follow-up (candidate: `AllocateLocalStackSpaces` liveness for multi value-type
locals). Optionally add ONE throw-asserting or `1/0` regression test for the
2-struct-local case (kept `public static void`, tagged `[Ignored]`/documented)
so the follow-up has a target — but since the harness forbids throw-asserting
tests and the bug is pre-existing, this is optional, not required for 13b.

---

### F-MIN-1 — Layout reserves ref slots for binder structs that no Neo reader consumes (latent; NIE-guarded today)

**Severity:** Minor (latent inconsistency; no current corruption because the
only binder struct in the smoke is pure-primitive).

**File:** `Optimizer.Neo.cs:1371-1377` (layout) vs `ILIntepreter.Neo.cs` autogen
body (`GenerateMethodWraperCode_Neo`, `MethodBindingGenerator.cs:254` uses only
`__curPrim`, never `__curRef`) and `CLRMethod.Invoke` (returns/continues after
the flat-bytes read, never advances a ref cursor for the struct).

**What:** `AllocateNeoCallParamSlot` sets `slot.RefCount = managedCount` and
advances `refOffset` for a binder struct (Optimizer.Neo.cs:1372-1377), so the
**target param region reserves `managedCount` ref slots** and the param-copy
(Optimizer.Neo.cs:1210-1214) copies that many source ref slots into them. But
neither Neo reader (autogen `*_Neo` body nor `CLRMethod.Invoke`) consumes ref
slots for a struct — both read only flat primitive bytes via
`ReadNeoValueType` / the binder NIE. For a binder struct WITH ref fields
(`managedCount > 0`) the readers throw NIE, so the reserved-but-unread ref slots
never cause a visible mis-cursor today. For the **pure-primitive** binder struct
actually exercised (`TestVector3`, `managedCount == 0`) `RefCount == 0` and no
slots are reserved — consistent.

**Why Minor, not Major:** The discrepancy only manifests for a binder struct
that has ref fields AND a working Neo binder read path — which does not exist
yet (autogen emits NIE; reflection emits NIE). The moment a future step adds a
Neo-cursor binder read path that DOES consume ref slots, the layout is already
correct (it reserves them), so this is forward-compatible. The only risk is if
someone wires up a partial binder read that consumes a DIFFERENT ref count than
`managedCount`. Worth a one-line code comment noting the readers currently
don't consume these slots; no code change required for 13b.

---

### F-INFO-1 — `NeoClrStructHasReferenceField` vs binder `managedCount` use different ref-detection (consistent in practice)

**Severity:** Info.

**File:** `CLRMethod.cs:380-384` (binder path checks `managedCount > 0`) vs
`CLRMethod.cs:386` / `BindingGeneratorExtensions.cs:13` (no-binder path checks
`NeoClrStructHasReferenceField` / `NeoBindingHasReferenceField`).

**What:** The "has reference fields?" decision uses two different predicates:
the binder path asks the binder's own `GetValueTypeSize` managed count; the
no-binder path walks fields via reflection (`NeoClrStructHasReferenceField`).
These agree for the tested structs (pure-float `TestVector3`/`TestVector3NoBinding`
→ both "no refs"). They could theoretically diverge for an exotic struct (e.g.
a binder registered with a non-zero managed count whose reflection walk misses
something), but that is not reachable today and both paths throw a clearly-tagged
NIE on the safe side. No action; noted for the future binder-read-path step.

---

### F-INFO-2 — Design.md / planning-context line refs are to propose-time HEAD, slightly stale vs the applied code

**Severity:** Info (doc nit).

**What:** design.md cites `Optimizer.Neo.cs:1172-1181` for the removed fallback
and `CLRMethod.cs:362-365` for the NIE; the applied code has these at slightly
shifted lines (the fallback removal hunk is around 1172-1186 in the new file).
The design's "Implementation deviations" section (§1-4) is accurate and
clearly explains the `GetPrimitiveSize`→`GetNeoValueTypeManagedSize` pivot and
the DynamicMethod approach. No correctness impact; refresh line numbers on
archive if desired.

---

### F-INFO-3 — Autogen D5 path (AppendArgumentCodeNeo/GetReturnValueCodeNeo for CLR structs) is verified by inspection only, not by an executed test

**Severity:** Info (acceptable; the implementer's Finding W explains why).

**What:** The new helpers (`SumTestVector3NoBindingFields`, `Make...`) have NO
autogen Neo redirect, so the 3 NeoStep13b tests exercise the **reflection**
fallback (D2/D6), not the autogen codegen (D5). D5 is verified by: compile
success, shared-helper byte-consistency (both call the same
`ReadNeoValueType`/`WriteNeoValueType`/`GetNeoValueTypeManagedSize`), and code
inspection. The CLR-binding canary `ValidateNeoSmallPrimitiveArgs` exercises the
autogen `*_Neo` path but only for primitives, not CLR-struct params. This is the
implementer's documented Finding W; regenerating the binding file with a
CLR-struct-param method would close the gap but is out of scope. Acceptable.

---

## Priority scrutiny answers (as requested)

### (a) ReadNeoValueType / WriteNeoValueType byte-consistency + safety — **CLEAN**

- **IL emission is correct.** Reader: `ldarg.0` (the `byte*`) →
  `call Unsafe.ReadUnaligned<T>(void*)` (the native-pointer overload, correctly
  selected by parameter-type filter at `ILIntepreter.Neo.cs:148-160`) →
  `box T` → `ret`. Writer: `ldarg.0` → `ldarg.1` (`object`) → `unbox.any T` →
  `call Unsafe.WriteUnaligned<T>(void*, T)` → `ret`. The `void*` overloads
  (not `ref byte`) are correctly chosen — `ref byte` would fail IL verification
  with a native pointer. Custom delegate types (`NeoVtReaderDelegate`/
  `NeoVtWriterDelegate`) correctly work around CS0306 (`byte*` can't be a
  generic arg). `restrictedSkipVisibility: true` lets it call `Unsafe`.
- **Single size source — byte-consistent by construction.** All three sites
  use `Optimizer.GetNeoValueTypeManagedSize(Type)`:
  1. Layout `AllocateNeoCallParamSlot` `Optimizer.Neo.cs:1371`.
  2. Reflection reader `CLRMethod.cs:393` (`vtSize` → `ReadNeoValueType(...,vtSize)`).
  3. Autogen reader `BindingGeneratorExtensions.cs:167,192`
  (`__sz = Optimizer.GetNeoValueTypeManagedSize(...)` → `ReadNeoValueType`).
  And the return paths: reflection `ILIntepreter.Neo.cs:310` (`retSz`) and
  autogen `BindingGeneratorExtensions.cs:531` (`__retSz`). One cached
  `Unsafe.SizeOf<T>` per Type — readers and layout cannot diverge.
- **No GC hole for the supported shapes.** `ReadNeoValueType`/`WriteNeoValueType`
  operate on the Neo frame `byte[]` (managed memory). A struct WITH managed ref
  fields read via `Unsafe.ReadUnaligned<T>` would produce a GC-untracked ref —
  but the no-binder path is guarded by `NeoClrStructHasReferenceField` (throws
  NIE before the read) and the binder path by `managedCount > 0` (throws).
  The only structs that reach the helpers are pure-primitive
  (`TestVector3`/`TestVector3NoBinding`, 3 floats) — blittable, no GC refs, safe.
- **No alignment issue.** `ReadUnaligned`/`WriteUnaligned` are intentionally
  unaligned — correct for the contiguous, no-per-param-alignment callee region
  documented at `Optimizer.Neo.cs:1322-1329`.
- **`GetNeoValueTypeManagedSize` is non-throwing + Legacy-neutral.** Cached per
  Type via `Unsafe.SizeOf<T>` instantiated by reflection; enum→underlying-type
  normalization; falls back to `Marshal.SizeOf` only if the `Unsafe.SizeOf`
  lookup fails (it won't). Lives behind `#if ENABLE_NEO_MODE` (whole file is
  guarded). Confirmed used everywhere a CLR struct size is needed.

### (b) The masked-NIE fix — **CORRECT, and it unmasks ONLY the sizing NIE (no second latent problem)**

- `AppDomain.GetPrimitiveSize` throws NIE for any non-primitive value type
  (confirmed: it only knows the primitive ILType singletons). The old fallback
  masked (1) the bad `IsValueType` callee-layout branch AND (2) an
  async-state-machine JIT-prewarm crash (`AsyncTaskMethodBuilder` param).
- `GetNeoValueTypeManagedSize` (non-throwing `Unsafe.SizeOf<T>`) fixes both:
  prewarm of a method taking an unsupported CLR struct no longer crashes
  (verified — the NeoStep smoke includes async tests and they are green), and
  the layout branch now produces a real size.
- **Did removing the fallback expose a SECOND latent problem?** I checked: the
  only behavior change for non-struct params is nil (the fallback only fired for
  CLR structs; primitives/enums/IL-VTs/refs went through `AllocateNeoCallParamSlot`
  before and after, byte-identical). For CLR-struct params the new path is
  correct for the flat-bytes-source shape (K2 closed; tests green). The
  Box/Initobj-sourced boxed-ref shape is the deferred K2-FAM (D3) — that IS a
  latent silent-wrong-result, but it is explicitly deferred and documented, and
  it was never a green case. No hidden second defect from the fallback removal.
- **CLR-binding canary green** (`ValidateNeoSmallPrimitiveArgs`, the autogen
  Neo path) — full smoke 84/84.

### (c) Finding V (frame-slot-reuse) — **PRE-EXISTING, reproduced, NOT a 13b gap; out of scope**

- **Reproduced deterministically:** two CLR struct locals (`v`, `w` from
  `Make` returns) + `r1 = Sum(v); r2 = Sum(w)` + `if (r1!=600 || r2!=3) 1/0`
  fails. Isolated: each single-arm check (`r1!=600`, `r2!=3`) fails on its own,
  so it is not r1↔r2 clobber — a struct local's 12-byte slot is corrupted while
  another struct local is live.
- **NOT introduced by 13b (stash-toggle proof):** with the 13b runtime files
  stashed, the same probe fails with the pre-13b `CLR value type return in
  reflection fallback: Step 13` NIE — the struct-return path didn't exist, so
  the pattern was unsupported, not broken. 13b newly enables the pattern; it
  does not regress a green case.
- **Struct-specific:** a two-CLR-INT-return probe passes on BOTH pre-13b and
  13b. The corruption requires the 12-byte struct local slots, implicating
  `AllocateLocalStackSpaces` liveness/slot-reuse for value-type locals.
- **Implementer's handling is correct:** flagged as out-of-scope (planning-
  context §7 Finding V), tests written to hold a single struct local at a time.
- **Verdict:** legitimate pre-existing bug; ship 13b; file the slot-reuse fix
  as the next follow-up. See F-MAJ-1.

### Additional priority items

- **K2 closed for the right reason:** `NeoStep13bClrStructByValueParamNoBinding`
  passes; the param is read by width (`ReadNeoValueType`, 12 bytes) not as an
  mStack index. The return-sourced shape (`v = Make(...)` → flat bytes into `v`'s
  slot → `Sum(v)` copies 12 flat bytes) works without the D3 bridge, as the
  design's D3 safety-valve predicted.
- **Phase 3 (D3, K2-FAM) deferral — legitimate.** Box/Initobj-sourced boxed-ref
  CLR-struct locals passed by value would miscopy the 4-byte mStack index into
  the 12-byte flat-bytes callee slot (silent wrong result). Correctly deferred:
  a clean reproducer needs IL-side `ldfld`/`stfld` on CLR struct fields
  (separate deferred concern). NOT a regression. Documented as accepted-known.
- **Legacy-neutrality + scope — CONFIRMED.** All new runtime code is behind
  `#if ENABLE_NEO_MODE` (the whole `Optimizer.Neo.cs`/`ILIntepreter.Neo.cs`
  files; the `CLRMethod.Invoke(byte*)` overload and `NeoClrStructHasReferenceField`
  are under the file's Neo guard). Legacy `CLRMethod.Invoke(ILIntepreter,
  StackObject*, ...)` and the Legacy `AppendArgumentCode`/`GetReturnValueCode`
  + `_Legacy` wrapper generators are UNTOUCHED. The 13b diff does not touch
  `MethodBindingGenerator.cs` (the master-vs-HEAD diff there is branch history).
  Deferred items confirmed still NIE/TODO-tagged: Area-4 value-type `this`
  (`MethodBindingGenerator.cs:261` `// TODO: ValueType instance in Neo`); CLR
  ref/out (autogen ByRef branch → Step-13b-tagged TODO); CLR-object stind/ldind
  via field hash (Step-17-tagged NIE).
- **Regression — CLEAN.** Full NeoStep smoke **84/84, 0 failed** (re-run after
  removing all probe artifacts). CLR-binding canary green. No regression.

---

## Verdict

**SHIP.** The 13b change is correct, byte-consistent, GC-safe for the supported
shapes, and Legacy-neutral. K2 and the return-value path are genuinely closed.
The single Major (Finding V) is a **pre-existing, reproduced, out-of-scope**
frame-slot-reuse bug — not a 13b defect and not a regression — already
documented by the implementer. File it as the explicit next follow-up.

---

## Reproduction artifacts (all removed; tree restored to implementer state)

- Probe file `TestCases/NeoStep13bReviewProbeTest.cs` — deleted.
- `MakeInt` helper in `TestClass3.cs` — removed.
- `TestClass3.cs` restored to the implementer's exact 13b state (32 insertions,
  matching the original diff).
- Final `git diff --stat` of the 13b files matches the pre-review diff exactly
  (Optimizer.Neo.cs 87, ILIntepreter.Neo.cs 120, CLRMethod.cs 59,
  BindingGeneratorExtensions.cs 84, TestClass3.cs 32).
- Final clean NeoStep smoke: **84/84, 0 failed.**
