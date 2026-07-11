# Tasks -- neo-aot-multi-hotfix (child 10)

> Cecil-free AOT load of a multi-hotfix-assembly setup (cross-assembly IL-to-IL
> type refs). Probe-first: reproduce the gap on HEAD, then fix the specific
> Cecil-free cross-assembly type resolution. All engine edits Neo-only
> (`#if ENABLE_NEO_MODE`); the capstone is `#if ENABLE_NEO_MODE && DEBUG`.

## 1. Probe + reproduce (probe-first)

- [x] 1.1 `TestCases/NeoStep25MultiHotfixProbe.cs` (NEW): two partitioned IL
      types -- `NeoStep25MultiHotfixA` (referencing: field/param/call/isinst/
      castclass of B) + `NeoStep25MultiHotfixB` (referenced: value field +
      BEcho + BMagic). Top-level + non-generic + BCL-refs-only + no statics
      (stays within shipped Cecil-free scope).
- [x] 1.2 Reproduced the gap on HEAD: A-then-B load order yields `A.BField field
      type NULL` (3/5 cells; A-then-B + M1 fail). B-then-A works on HEAD (the
      referenced type is in mapType before the referencing type is built).

## 2. The fix: deferred cross-assembly re-resolution (D1)

- [x] 2.1 `ILType.ReResolveCrossAssemblyRefs(model, rec)` (Neo-only): re-resolve
      still-NULL instance field types + static field types by name
      (`ResolveNamedIType` from the record's FieldRef type name); re-resolve a
      NULL baseType + interfaces by name (+ the clrbase-iface CrossBindingAdaptor
      install, mirroring FinalizeFromNeoRecord); recompute fieldStartIdx /
      totalFieldCnt / firstCLRBaseType / firstCLRInterface when a base resolves.
      Idempotent + per-slot null-guarded; a no-op on a non-AOT type.
- [x] 2.2 `AppDomain.neoAotBuilt` (Neo-only, lazily-allocated
      `List<(ILType, NeoTypeDefRecord, NeoAssemblyModel)>`): tracks every Cecil-
      free ILType built by LoadNeoAssembly + its originating record/model.
- [x] 2.3 `LoadNeoAssembly`: after the two-pass build, add this model's survivors
      to `neoAotBuilt`, then call `ReResolveCrossAssemblyRefs` on EVERY tracked
      type (best-effort try/catch -> a skip, never fatal). Order-independent.

## 3. The capstone + adversarial gate (D3)

- [x] 3.1 `NeoStep25CecilFreeMultiHotfixCheck` (`#if ENABLE_NEO_MODE && DEBUG`):
      5 cells -- (A) JIT reference, (compile) partitioned .neo-A + .neo-B,
      (B-then-A) load + ACompute==1031, (A-then-B) load + ACompute==1031 (the
      gap-exposing order), (M1) body-mutation of the inlined 1000 constant ->
      MUTATED-derived 2031.
- [x] 3.2 CLI hook `NeoStep25CecilFreeMultiHotfix` added to `Program.cs`.

## 4. Build + regression gates (CRITICAL -- always `-f net8.0`)

- [x] 4.1 `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`
      (0 errors).
- [x] 4.2 `dotnet build TestCases/TestCases.csproj -c Debug` (0 errors).
- [x] 4.3 `NeoStep25CecilFreeMultiHotfix` capstone: **5/5 cells, 0 failed**.
- [x] 4.4 Stash-toggle (load-bearing proof): stashed the 2 engine files
      (ILType.cs + AppDomain.cs) -> rebuilt -> A-then-B cell FAILS (3/5) on HEAD;
      restored -> 5/5 restored. Confirms the fix is load-bearing.
- [x] 4.5 Regression held: `NeoStep25CecilFreeLoad` 7/7, `NeoStep25ClrBaseIface`
      4/4 UNCHANGED.
- [x] 4.6 `NeoStep` smoke (Debug_Neo + useRegister=true, filter `NeoStep`):
      **253/0/0** (baseline matched -- no regression; the probe/check are
      host-side, not counted by the filter).
- [x] 4.7 Legacy-neutral: plain `Debug` build of ILRuntimeTestCLI = **0 errors**
      (all new code compiles out).

## 5. Docs

- [x] 5.1 `design.md` + `tasks.md` (this change dir).
- [x] 5.2 Honest scope: simulated multi-assembly (two `.neo` models from one
      DLL), not two distinct DLLs -- documented in design "Scope boundary".

## Out of scope (SEQUENCED -- recorded, NOT implemented)

- Generic-instance cross-assembly refs (child 8's T-identity-token concern).
- Cross-PROCESS multi-assembly (child 9 proved portability by construction).
- Static `.cctor` cross-assembly seeding (a .cctor referencing another
  assembly's static — sub-surface 4 territory).
- A true two-distinct-DLL AOT compile (a harness limitation; the `.neo` format +
  name-based resolution are assembly-agnostic).
