## 1. Reproducibility re-confirmation (apply-phase, before authoring probes)

- [x] 1.1 Build the CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo`) and TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`); confirm 0 errors.
- [x] 1.2 Run the NeoStep smoke baseline: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` — confirm 140/140 (environment-healthy baseline).
- [x] 1.3 Re-confirm the 6 K2-FAM reproducer probes STILL PASS on HEAD by temporarily adding them, running each individually (filter = the probe name), then reverting the temp file before the keeper additions in §2. (The propose-phase assessment showed all 6 PASS; re-confirm at apply in case HEAD moved. If any probe now FAILS, STOP and re-evaluate — the closure may have regressed, and the change becomes a real fix, not test-only.)

## 2. Keeper regression probes

- [x] 2.1 Add `NeoStep13_K2Fam_BoxSourceByValue` to `TestCases/NeoStep13bTest.cs`: a CLR struct local sourced from Box→Unbox (`object o = v; T t = (T)o`), passed by value to `TestCLRBinding.SumTestVector3NoBindingFields`; assert the expected field sum via the DivideByZero pattern. Use `TestVector3NoBinding` (no binder). NO IL-side `Ldfld` on a CLR struct field (the `[NEO-IL-VT-INSTANCE-COVERAGE]` gap).
- [x] 2.2 Add `NeoStep13_K2Fam_InitobjSourceByValue`: local via `default(T)` (Initobj), by value; assert sum == 0.
- [x] 2.3 Add `NeoStep13_K2Fam_BoxMoveByValue`: Box→Unbox→struct-copy (`T t2 = t`)→by value; assert the expected field sum.
- [x] 2.4 Add `NeoStep13_K2Fam_ReinitThenByValue`: local assigned, then `t = default(T)` (re-initobj), by value; assert sum == 0 (the post-re-init value, not the prior value).
- [x] 2.5 Add `NeoStep13_K2Fam_BoxUnboxByValueToHost`: Box→Unbox→by-value to a host static helper; assert the expected field sum.
- [x] 2.6 Add `NeoStep13_K2Fam_TwoBoxedStructLocalsByValue`: TWO Box→Unbox-sourced locals, both by value (the F-MAJ-1 two-live-struct stress, Box-sourced); assert BOTH field sums (`r1==600 && r2==3`) — the load-bearing neighbour-corruption guard.

## 3. Verify

- [x] 3.1 Rebuild TestCases `--no-incremental` after adding the 6 probes (the stale-DLL gotcha; verify the DLL mtime is newer than the source).
- [x] 3.2 Run the NeoStep smoke: confirm 146/146 (140 baseline + 6 new probes). All 6 new probes MUST pass (they passed at propose-time; a failure at verify = a regression or a probe that exercises the `[NEO-IL-VT-INSTANCE-COVERAGE]` gap — fix the probe, do not ship a failing test).
- [x] 3.3 Run the 6 probes under a `K2Fam` filter as a group to confirm they collectively cover the K2-FAM source shapes.
- [x] 3.4 Confirm Legacy-neutral: build the plain `Debug` CLI + `useRegister=true`, run the `NeoStep13` filter; the probes pass on Legacy too (representation-agnostic assertions).

## 4. Spec + tracker closure (ship/archive phase)

- [ ] 4.1 Sync the `neo-boxing` spec delta into `openspec/specs/neo-boxing/spec.md` (MODIFIED: the K2-FAM closure requirement → DELIVERED; the Out-of-scope deferrals K2-FAM bullet → REMOVED). The archive step does this; verify the canonical spec reflects the closure.
- [ ] 4.2 Update `.trae/documents/neo-deferred-items.md`: §2 master-table row K2-FAM → RESOLVED (subsumed by opt-harden-2 + 13b); §3 K2-FAM entry → RESOLVED prepend. Leave the F-2 / INLINER-REFONLY-VT entry intact (distinct defect class; still deferred).
- [ ] 4.3 Write `ship-log.md` (verification evidence: 146/146 smoke + Legacy-neutral + the 6 probes listed + the resolution-by-recent-work attribution).
- [ ] 4.4 Move the change to `openspec/changes/archive/`.
