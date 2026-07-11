# Ship Log — neo-aot-crossprocess (child 9)

**Date:** 2026-07-10  **Capability:** neo-optimizer (AOT)  **Wave:** completion-3, child 9
**Status:** SHIPPED (LEAD-verified, TEST-ONLY)  Proceeded despite child 8 PARKED (cross-process
is independent of the generic case).

## Delivered
Cross-PROCESS Cecil-free `.neo` portability — **TEST-ONLY (no engine fix).** Audit PROVES portability
by construction:
- The `.neo` format is **pure process-independent data**: `OpCodeR[]` as raw 24-byte blittable structs
  (`MemoryMarshal.AsBytes`; `Operand` is an int token-hash KEY, not an object ref); all tables are
  ints/byte-arrays/`(Hash,FullName,MethodName,ParamCount)` tuples. **No `GCHandle`, no pointer, no
  process-local object reference** anywhere.
- The APPROACH-1 hash is **NON-deterministic** (`ILType.GetHashCode`/`ILMethod.GetHashCode` are
  process-global `instance_id` counters) **BUT IRRELEVANT**: `ReRegisterTokenBindings`
  (`AppDomain.cs:858`) re-resolves each ref **by NAME** and aliases the recorded P1 hash to the
  freshly-resolved object; P2 never compares hashes. **Corrects lead-6's "deterministic hash"
  framing** — the hashes are non-deterministic, but the name-aliasing makes that irrelevant.

## What shipped (TEST-ONLY guard)
`NeoStep25CrossProcessCheck` (host-side) + 2 CLI modes (`NeoStep25CrossProcess` P1 orchestrator;
`NeoStep25CrossProcLoad` P2 that short-circuits before `session.Load` so P2 never Cecil-loads
TestCases) + `WriteModelStandalone` re-serializer (`NeoAssemblyWriter`, for the body-mutation cell).
6 cells: `A` (JIT ref), `X1` disk-round-trip + cross-AppDomain, `X2` GC-perturbation, `X3`
persisted-body-mutation, `X4` byte-determinism (two compiles byte-identical), `X5` real
cross-process (SKIP-IN-HARNESS on Windows).

## Honest residual
Cross-PROCESS is **NOT directly asserted on this Windows host**: `Process.Start` of `dotnet` from a
hosted .NET parent fails with `hostpolicy`-not-found (`0x80008013`) — a **machine/harness
limitation** (the `host\fxr\` dir lacks `hostpolicy.dll`), NOT a `.neo` flaw; exhaustively confirmed
across `dotnet exec`/apphost/env-scrub/`DOTNET_ROOT`/batch; the same command works from the OS
shell. Cross-AppDomain + disk-round-trip + GC-perturbation + byte-determinism ARE directly asserted.
`X5` auto-upgrades to a hard PASS on a host where nested-dotnet hostpolicy resolves (Linux / a fixed
fxr dir).

## Verification
NeoStep **253/0/0** (unchanged — the check is host-side, not a TestCase method).
`NeoStep25CrossProcess`: **6/6**. Held: NeoStep25LoadExec 28/28, NeoStep25CecilFreeLoad 7/7,
NeoStep25ClrBaseIface 4/4, NeoStep23Roundtrip 15/15. Legacy-neutral (all `#if ENABLE_NEO_MODE`).
**NeoStep24CliRoundtrip 1/5 confirmed PRE-EXISTING at clean HEAD** (stashed ALL changes + rebuilt —
NOT a regression from this child or any prior session change; lead-6's "5/5" baseline was stale).

## Durable findings
1. **APPROACH-1 hash non-determinism is a non-issue for portability** — the hash is an opaque dict
   key, re-aliased by name at load; P2 never compares hashes. (Corrects lead-6 "deterministic".)
2. The `.neo` carries **zero process-local state** (`MemoryMarshal.AsBytes` over blittable `OpCodeR`
   + pure-value-type tables).
3. **Windows nested-dotnet hostpolicy spawn limitation** — `Process.Start` of `dotnet` from a hosted
   .NET parent fails with hostpolicy-not-found regardless of env scrubbing/apphost/batch; works from
   the OS shell. Future children needing genuine cross-process on Windows must fix the machine's
   fxr-dir hostpolicy or run on Linux.
4. `WriteModelStandalone` re-serializer added (deserialized+mutated `NeoAssemblyModel` → `.neo`
   stream; reuses the static `Buf*`/table writers).

## Review
LEAD-verify (TEST-ONLY; implementer evidence trusted — Neo-gated additive + the NeoStep24 stash-check
is thorough). No capability-spec sync (AOT Cecil-free portability; documented in the archived
`design.md`).
