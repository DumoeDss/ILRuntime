# Tasks: neo-aot-crossprocess (child 9)

## 1. AUDIT the `.neo` format + APPROACH-1 hash for process-dependent state — DONE
- [x] Audited `NeoAssemblyWriter.WriteModel` / `NeoAssemblyReader.Read` /
      `NeoAssemblyModel`: the `.neo` is PURE value-type data (24-byte blittable
      `OpCodeR`s via `MemoryMarshal.AsBytes`, plain int/byte[]/structured
      records, APPROACH-1 `(Hash,FullName,MethodName,ParamCount)` tuples). NO
      `GCHandle`, NO raw pointer, NO process-local object reference.
- [x] Audited the hash source: `ILType.GetHashCode` / `ILMethod.GetHashCode`
      are process-global `instance_id` counters; CLR refs use Cecil identity
      hashes. **NON-deterministic across processes** (AppDomain.cs:103-131).
- [x] Audited the load path: `ReRegisterTokenBindings` (AppDomain.cs:858)
      re-resolves by NAME + aliases the RECORDED hash. The hash is an opaque
      dictionary key, NOT semantic content.
- [x] FINDING: cross-process portability is PROVEN BY CONSTRUCTION. No
      process-dependent element to fix. Child is TEST-ONLY.

## 2. Ship the cross-context portability guard — DONE
- [x] `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25CrossProcessCheck.cs`
      (new, `#if ENABLE_NEO_MODE && DEBUG`).
  - [x] X1: DISK round-trip + cross-AppDomain (persist `.neo` → re-read →
        fresh `AppDomain()` Cecil-free exec `Compute()` == 155).
  - [x] X2: PERTURBATION (full GC + 256KB garbage + 50ms delay before the
        fresh-AppDomain load — stresses process-local-state independence).
  - [x] X3: BODY-MUTATION persisted (mutate `FLong=100` → `555` in the model
        → `WriteModelStandalone` → re-read → fresh AppDomain → exec →
        assert 610). Proves the fresh domain runs the genuine persisted bytes.
  - [x] X4: DETERMINISM (two P1 compiles byte-identical).
  - [x] X5: REAL cross-process probe (NON-gating; SKIP-IN-HARNESS on the
        hostpolicy spawn signature, hard-PASS on a clean spawn).
- [x] `ILRuntime/Runtime/NeoAOT/NeoAssemblyWriter.cs`: added
      `WriteModelStandalone(NeoAssemblyModel, Stream)` re-serializer
      (reuses the static `Buf*` / table writers; mirrors the instance
      `WriteModel` assembly).
- [x] `ILRuntimeTestCLI/Program.cs`: added the `NeoStep25CrossProcess` P1
      orchestrator mode + the `NeoStep25CrossProcLoad` P2 mode (short-circuits
      BEFORE `session.Load` so P2 NEVER Cecil-loads TestCases — the genuine
      Cecil-free cross-process claim).
- [x] `ILRuntimeTestCLI/NeoCrossProcLoadP2.cs`: thin CLI wrapper delegating
      to `NeoStep25CrossProcessCheck.RunP2` (in the ILRuntime assembly, which
      has internal access to `NeoAssemblyReader` / `LoadNeoAssembly`).

## 3. Verify — DONE
- [x] `NeoStep25CrossProcess`: **6/6 cells passed, 0 failed** (A, X1, X2, X3,
      X4 gating PASS; X5 informational SKIP-IN-HARNESS with the hostpolicy
      diagnostic).
- [x] `NeoStep25LoadExec` 28/28, `NeoStep25CecilFreeLoad` 7/7,
      `NeoStep25ClrBaseIface` 4/4, `NeoStep23Roundtrip` 15/15 — held.
- [x] Full NeoStep smoke **253/0/0** (unchanged from baseline; the new check
      is host-side, not a TestCase method — the +N probes are the 6 cells).
- [x] Legacy-neutral: plain `Debug` build of CLI **0 errors** + plain `Debug`
      build of ILRuntime **0 errors** (all new code Neo-gated).

## Honest residual (recorded in design.md)
- Cross-PROCESS is NOT directly asserted on this Windows host (the
  hostpolicy.dll-under-nested-dotnet spawn limitation). It IS directly
  asserted at the cross-AppDomain + DISK-round-trip + GC-perturbation level
  (X1-X3) and the byte-determinism level (X4). The cross-process claim rests
  on the audit: the `.neo` carries only process-independent data + APPROACH-1
  name-aliasing covers hash non-determinism. X5 auto-upgrades to a hard PASS
  on a host where nested-dotnet hostpolicy resolves (Linux / a fixed fxr dir).
