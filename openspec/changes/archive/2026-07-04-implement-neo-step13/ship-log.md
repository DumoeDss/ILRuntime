# Ship Log -- implement-neo-step13

Neo Step 13: Box/Unbox of value types in the Neo register VM -- IL value-type
Box/Unbox coverage plus CLR value-type Box/Unbox/Initobj on the boxed-ref local
representation. PARTIAL by design (areas 1-2 delivered; area 3 deferred to Step
17; areas 4-5 deferred to Step 13b).

## Ship verdict: CLEAN

0 Blocker / 0 Major / 0 unresolved Minor. 2 Minor findings from the review loop
(MINOR-1 CLR-enum local rep, MINOR-2 IntPtr/UIntPtr) RESOLVED via review-loop
round 1 (drop `IsEnum`, route CLR enums via the boxed-ref
`PerformMemberwiseClone`/`CreateDefaultInstance` path; native-int arms deferred
as a pre-existing-pattern sibling of `NeoBoxReturnValue`) + 1 INFO
(documentation of deferrals, addressed by the spec annotation below). CLR-enum
local end-to-end test deferred (K2/Move-path blocker -- see follow-ups).

## Verification evidence

Builds (per CLAUDE.md subset; the full sln cannot build -- VSIX net472 vs
netstandard2.1 NU1201, unrelated to Neo):
- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0 errors
  (transitively builds ILRuntime / ILRuntimeTestBase / LitJson).
- `dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors, produces
  `TestCases/bin/Debug/netstandard2.1/TestCases.dll`.

FULL NeoStep smoke (regression gate):
- Filter `NeoStep`: 49 ran, 0 failed (was 41/41 after Step 12b; +8 new Step 13
  box/unbox cases).
- Filter `NeoStep13`: 8 ran, 0 failed (the +8 new cases for this step).
- Zero existing cases regressed; no Step-tagged NotImplementedException thrown
  on the delivered paths; no test exceeded the 10s infinite-loop watch.
- CLR binding canary green -- the call ABI was NOT touched in this pass
  (areas 4-5 deferred), so all CLR binding tests are unchanged.

## Review summary

CLEAN -- adversarial review by an independent verifier (author != reviewer),
see `review-report.md`. Confirmed:
- Area 2 boxed-ref deviation is TRUE and CORRECT. A CLR value-type LOCAL is a
  boxed object reference (4-byte mStack index slot, `RefCount=1`,
  `localIsRef=true`) per `JITCompiler.AllocateLocalStackSpaces` (CLR-VT branch),
  NOT flat bytes -- the design D2 "flat bytes" premise was factually wrong and
  the implementer's reframe fixed it. Initobj=`CreateDefaultInstance`;
  Box/Unbox=`PerformMemberwiseClone` (shallow independent copy = correct value
  semantics). With-binder vs no-binder is MOOT for locals (binder only matters
  for the flat-bytes array/param/field rep deferred to 13b).
- Area 1 (IL Box/Unbox) is coverage-only -- no runtime change; Step 5's
  `CopyFrameToIL`/`CopyILToFrame` arms are unchanged and the 4 IL tests pass for
  the right reason (ref-identity + snapshot independence hold via the copy
  helpers).
- Area 3 deferral is legitimate (all 3 blockers real: ldarga unimplemented =
  Step 17; Constrained has no runtime arm and is re-appended after the callvirt
  in the instruction stream; box-once/direct-call needs the VT this address
  model). Zero regression -- no green test exercises `constrained.`.
- Areas 4-5 confirmed UNTOUCHED (`git diff` returns 0 lines for
  `Runtime/CLRBinding/`, `CLR/Method/CLRMethod.cs`, `Optimizer.Neo.cs`).
- All new code is inside the file's outer `#if ENABLE_NEO_MODE`; Legacy
  (`ExecuteR` / binder methods) untouched.

Review-loop round 1 (MINOR-1 + MINOR-2 + spec annotation) applied and confirmed
by a non-author reviewer.

## Delivered scope (Step 13 is PARTIAL by design)

DELIVERED this pass:
- Area 1 -- IL value-type Box/Unbox: verified Step 5 works with the Step 12
  in-frame VT model; 4 coverage tests added (IL VT one-ref, IL VT many-refs,
  IL enum, IL primitive). No runtime change.
- Area 2 -- CLR value-type Box/Unbox/Initobj: implemented on the boxed-ref
  local representation. Initobj=`CreateDefaultInstance`; Box/Unbox=
  `PerformMemberwiseClone` (shallow independent copy = correct value
  semantics). With/without-`ValueTypeBinder` distinction is MOOT for locals
  (binder only matters for flat-bytes array/param/field rep). 3
  Step-13-tagged NIE throws replaced by implementations + 2 helpers
  (`NeoBoxPrimitiveByType`, `NeoWritePrimitiveToFrame`). 4 CLR tests added.

DEFERRED:
- Area 3 -- `constrained.` callvirt specialization on a value-type `this` ->
  deferred to Step 17. Blocked on the byref/VT-address model (ldarga
  unimplemented; `Constrained` has no runtime arm and is re-appended after the
  callvirt so cannot inform it; box-once/direct-call needs the VT this
  address). Zero current regression (no green test exercises `constrained.`).
  The design D3 case analysis is retained as the blueprint.
- Areas 4-5 -> deferred to Step 13b. Area 4 = binding codegen overhaul
  (`Unsafe.Unbox<T>` + direct-call mode, eliminate the Legacy
  `WriteBackInstance`/`StackObject*` writeback from the Neo redirect path).
  Area 5 = CLRMethod unified Neo param layout (route CLR struct params through
  `AllocateNeoCallParamSlot`, read via non-generic `ReadNeo*` by slot width,
  REMOVE the caller-temp-slot fallback at Optimizer.Neo.cs:646-655). Highest
  regression risk (touches the call ABI shared by every CLR method invocation);
  bundled follow-up that MUST stand alone in review and MUST carry the
  flat-bytes "no-binder struct-with-refs throws NIE" scenario when it
  introduces that representation.

## Accepted-known follow-ups (PRE-EXISTING, NOT Step 13 regressions)

- K1 -- FCP mis-propagates value-type Moves (Step 12b carryover; pre-existing
  optimizer bug). After `b = a`, FCP rewrites later `b.field` reads to
  `a.field` even after `a.field` is mutated. Out of Step 13 scope. Defer
  against the optimizer.
- K2-family -- the Move path mis-handles scalar/constant -> boxed-ref CLR-VT-
  local assignment (reads an int as an mStack index). Pre-existing. Blocks
  CLR-enum/struct LOCAL round-trip end-to-end; fixed by Step 13b area 5
  (unified param layout + Move-path). The MINOR-1 Box/Unbox/Initobj fix is
  correct per the boxed-ref representation but UNTESTABLE end-to-end until K2
  is fixed; the CLR-enum-local test is therefore deferred.
- Area 3 -- deferred to Step 17 (see above).
- Areas 4-5 -- deferred to Step 13b (see above).

## Files changed

Runtime (all new code under `#if ENABLE_NEO_MODE`; the file sits entirely
inside the file's outer `#if ENABLE_NEO_MODE`):
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- 3
  Step-13-tagged NIE throws replaced by implementations (CLR Box arm, CLR
  Unbox/Unbox_Any arm, CLR Initobj arm) + 2 new helpers
  (`NeoBoxPrimitiveByType`, `NeoWritePrimitiveToFrame`). Diff: +195 / -6.
  No change to the IL Box/Unbox arms (Area 1 was coverage-only). No change to
  `Runtime/CLRBinding/`, `CLR/Method/CLRMethod.cs`, or `Optimizer.Neo.cs`
  (areas 4-5 confirmed untouched).

Tests:
- `TestCases/NeoStep13Test.cs` -- new, ASCII (8 green tests: IL VT one-ref
  box/unbox, IL VT many-refs box/unbox, IL enum box/unbox, IL primitive
  box/unbox, CLR struct no-binder box round-trip, CLR struct with-binder box
  round-trip, plus the two Vector3 variants). No throw-asserting tests (harness
  cannot construct `new Exception(...)`; CLR newobj is Step 9); field-value
  round-trip NOT asserted for CLR structs (Ldfld on CLR struct fields is a
  separate Step 6 NIE outside Step 13 scope), documented inline.

## Git note

All Step 13 changes are uncommitted in the working tree on branch
`features/object-model-overhaul`. Per the SHIPPER brief, the LEAD commits and
pushes after this step; no commit is made here. No source edits made during
ship/archive (ship-log write + spec merge + directory move only).

## Stage 2 (archive) outcome

- Spec sync: `openspec/specs/neo-boxing/spec.md` CREATED -- the ADDED delta
  resolved to a new capability (no prior spec existed), delta markers dropped,
  the spec annotation marking the no-binder-refs NIE as deferred-to-13b
  PRESERVED, U+FFFD-free.
- Archive: change moved to
  `openspec/changes/archive/2026-07-04-implement-neo-step13/` (`.openspec.yaml`
  and `auto-run.json` moved with it).
- `openspec list` shows no active changes after archive.
