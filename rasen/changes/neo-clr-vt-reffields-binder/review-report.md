# Review Report - neo-clr-vt-reffields-binder (child-6 of neo-overhaul)

**Reviewer:** author != verifier gate (autonomous LEAD Tier-A dispatch, report-only)
**Branch:** features/object-model-overhaul
**Change:** new Neo redirect `CLRRedirections.InitializeArrayNeo` + `RedirectMapNeo` registration + ldtoken field-path blob remediation + 2 probes.
**Diff:** 3 source files (74 insertions, 1 deletion) + 1 new test file.

## VERDICT: APPROVE

The change is correct, narrowly scoped, and fully verified. All gates pass, the stash-toggle proves the redirect is load-bearing, the child-2 ldtoken blast radius is clean (TC1-TC4 unregressed, TC5 upgraded), and the 18 Step-13b NIEs are eliminated from the full smoke. The two findings are cosmetic (Trivial) and do not block ship.

---

## 1. Redirect-handler correctness verdict: CORRECT + SAFE

`CLRRedirections.InitializeArrayNeo` (CLRRedirections.cs:524-543) reads param 0 (Array) + param 1 (byte[]) via `ReadNeoReference`, then pins + `Marshal.Copy` the blob into the array.

**Cursor math - verified correct.** `ReadNeoReference` (ILIntepreter.Neo.cs:123-136) reads `*(int*)(frameBase + curPrim)` and advances `curPrim += 4`, with null-sentinel handling (`idx >= 0 ? mStack[idx] : null`). So:
- param 0 (Array) read at +0, cursor -> +4.
- param 1 read at +4, cursor -> +8.

This is sound because (a) a CLR reference param occupies exactly a 4-byte mStack-index slot in the Neo callee region (precedent: the shipping Step-19 `DelegateCombineNeo` at CLRRedirections.cs:551 reads two Delegate params back-to-back the same way and is green), and (b) `CopyNeoCallArguments` copies param 1 by the callee-declared `RuntimeFieldHandle` size (~8 bytes), but the byte[] index the ldtoken pushed sits in the FIRST 4 bytes of that slot (`ldtoken` writes `*(int*)dstSlot = mStack.Count - 1`), so `ReadNeoReference` recovers it. Confirmed empirically: TC1 (int[32]) and TC2 (double[32]) pass with correct per-element contents.

**GCHandle safety - verified, no leak.** `GCHandle.Alloc(arr, Pinned)` is wrapped in try/finally with `h.Free()` in finally (CLRRedirections.cs:532-540). This is actually MORE leak-resistant than the Legacy body (CLRRedirections.cs:372-376), which frees without try/finally. The destination is a CLR primitive array (int[]/double[]), which is pinnable.

**Marshal.Copy bounds - parity with Legacy, safe.** Copies `bytes.Length` bytes (CLRRedirections.cs:535), identical to Legacy's `Marshal.Copy(data, 0, dst, data.Length)`. The blob is compiler-generated to exactly match the array size (128 bytes for int[32], 256 for double[32]); no overflow path. int[32] gets 128 bytes, double[32] gets 256 - both verified via the contents assertions.

**Type guard.** `if (data is byte[] bytes && array is Array arr && bytes.Length > 0)` is stricter than Legacy's `if (data == null) return ret` - it additionally no-ops if array is not an Array or bytes is empty. For the array-initializer use case the types are always correct. (Minor: a mismatched-type call would silently leave the array zero-initialized rather than throw - see findings.)

**Void method.** Correctly writes no return value (no `retDst` write), matching `RuntimeHelpers.InitializeArray`'s void signature.

## 2. ldtoken-remediation blast-radius verdict (child-2): CLEAN - TC1-TC4 UNREGRESSED

The remediation (ILIntepreter.Neo.cs:1604-1612) adds a new FIRST arm to the ldtoken **field** path (`Operand == 0`): it sources the blob from Cecil `FieldDefinition.InitialValue` (`ilt.StaticFieldDefinitions[sIdx].InitialValue`, confirmed at ILType.cs:250 to return `FieldDefinition[]`) and pushes it as a Neo reference (`mStack.Add(initBlob); *(int*)dstSlot = mStack.Count - 1`) when `initBlob != null && initBlob.Length > 0`.

**Scoped to blob fields only - verified.** The guard `initBlob != null && initBlob.Length > 0` only fires for RVA-initialized fields (the `<PrivateImplementationDetails>` `.size N` blob). Normal static fields have null/empty Cecil `InitialValue` (C# emits `static int X = 5;` as a `.cctor`, not an RVA initializer), so they fall through to the existing primitive/VT/reference arms unchanged. The arm is correctly placed BEFORE the value-type arm because the blob field IS a value-type ILType but has TPS=0/TRC=0 (so the VT arm would copy 0 bytes). This is exactly the disproof of the planner's "blob is reachable via the static instance" assumption - the blob-only type's `ManagedObjects` is null.

**Child-2 probes - verified unregressed.** TC1-TC4 are `typeof(...)` probes, which exercise the ldtoken **type** path (`Operand == 1`, ILIntepreter.Neo.cs:1566-1574) - a completely separate branch that this change never touches. TC5 (the field-path probe) is upgraded: its `catch (NotImplementedException)` is now dead code and the `arr[i] == 100+i` contents assertion takes over. Re-ran `NeoStepLdtoken`: **5/0** with TC1-TC4 still green via their original ReflectionType/get_FullName/op_Equality mechanism, TC5 green via correct contents. No regression.

## 3. Stash-toggle evidence: REDIRECT IS LOAD-BEARING

Toggled ONLY the `RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.InitializeArrayNeo)` registration off in AppDomain.cs:158 (kept the probes + ldtoken fix). Rebuilt CLI (Debug_Neo, 0 errors). Ran isolate filter:

- **Redirect OFF: `Ran 2 tests, 2 failed` (exit 127).** BOTH TC1 and TC2 fail with the EXACT documented root cause:
  `NotImplementedException: CLR value type with reference fields and no ValueTypeBinder (Step 13b): register a binder. Type: System.RuntimeFieldHandle`
  thrown from `CLRMethod.Invoke` (CLRMethod.cs:501) via `InvokeNeoClrMethod` (ILIntepreter.Neo.cs:997) - i.e. the reflection fallback marshalling the RuntimeFieldHandle param. This is precisely the pre-fix NIE, proving the probes are real correctness probes (FAULT-on-HEAD) and the redirect is what resolves them.
- **Redirect restored: `Ran 2 tests, 0 failed` (exit 0).** TC1 + TC2 pass with correct contents.
- Tree restored to as-found (0 `REVIEWTOGGLE` leftover; verified via `git diff`).

## 4. Gate results

| Gate | Config / filter | Result | Claim | Match |
|------|-----------------|--------|-------|-------|
| Build CLI | `Debug_Neo --no-incremental` | 0 errors | 0 errors | yes |
| Build TestCases | `Debug` | 0 errors | 0 errors | yes |
| NeoStep smoke | `Debug_Neo`, `NeoStep` | **320/0** | 320/0 (318+2) | yes |
| NeoStepLdtoken | `Debug_Neo`, `NeoStepLdtoken` | **5/0** | 5/0 | yes |
| ClrVtReffieldsBinder isolate | `Debug_Neo`, `ClrVtReffieldsBinder` | **2/0** | 2/0 | yes (see note) |
| Stash-toggle OFF | redirect removed | 2/2 FAIL (Step-13b NIE) | FAIL-on-HEAD | yes |
| Stash-toggle ON | redirect restored | 2/0 PASS | PASS-after | yes |
| Full smoke Step-13b count | `Debug_Neo`, no filter | **0** occurrences | 0 (was ~18) | yes |
| Legacy ClrVtReffieldsBinder | plain `Debug`, reg | 2/0 | passes under Legacy | yes |
| Legacy NeoStep | plain `Debug`, reg | 320 ran / 17 failed | == baseline | yes |

**Filter-name note (informational, not a code defect):** the dispatch brief's isolate filter `NeoClrVtReffieldsBinder` matches 0 tests (gives `Ran 0 tests`). The actual method names are `NeoStepClrVtReffieldsBinder_TC1/TC2` (prefixed `NeoStep` to ride the NeoStep smoke), so the correct isolate filter is `ClrVtReffieldsBinder` (which the test file's own header comment documents). The implementer's design is self-consistent; only the brief's filter string was slightly off. All "2/0" claims above were re-verified with the correct `ClrVtReffieldsBinder` filter.

## 5. Full-smoke Step-13b count: 0 (was ~18)

Full Neo smoke (no filter) captured 129,785 lines before crashing (see below). `grep "CLR value type with reference fields and no ValueTypeBinder"` => **0 occurrences** (previously ~18 pre-crash, all RuntimeFieldHandle from array initializers). Every `RuntimeFieldHandle` hit in the log is now a `call.redirect ... RuntimeHelpers.InitializeArray` - the redirect serves every array initializer in the suite.

**Crash is pre-existing and unrelated.** The full smoke terminated with `AccessViolationException` at ExecuteNeo (exit 139). The JIT disassembly immediately preceding the crash (log:129775) shows the failing TEST itself executes `newobj System.AccessViolationException::Void .ctor(); throw` - a Neo exception-handling probe that constructs and throws an AccessViolation (a corrupted-state exception the runtime won't catch normally). This is unrelated to InitializeArray / the redirect / the ldtoken blob path: the NeoStep smoke (320 tests, many exercising the redirect) completes 0-failure with no crash, and TC1/TC2 drive the redirect directly without issue. If the redirect segfaulted, the crash would occur at the first array initializer, not after 129K lines.

## 6. Legacy-neutral: CONFIRMED

All three changes are Neo-gated: the registration and `InitializeArrayNeo` handler are under `#if ENABLE_NEO_MODE`, and the ldtoken remediation lives inside `ExecuteNeo` (a Neo-only method in ILIntepreter.Neo.cs). Under plain `Debug` (Legacy/ExecuteR), none of this code is compiled; Legacy dispatch uses its own pre-existing `InitializeArray` redirect and its own ldtoken path (untouched).

Verified: plain-`Debug` CLI builds 0 errors; Legacy `ClrVtReffieldsBinder` => 2/0 (Legacy's redirect serves the probes); Legacy `NeoStep` => 320 ran / 17 failed, matching the claimed baseline (the 17 are pre-existing NeoStep tests that rely on Neo-specific behavior and are not Legacy-compatible - unchanged by this Neo-gated change).

---

## Findings

### Trivial
- **T1. Duplicated comment line (AppDomain.cs:156-157).** The comment `// Register the Neo redirect so InvokeNeoClrMethod serves it.` appears twice consecutively. Cosmetic; remove one. No behavioral impact.

### Informational (ship-able, recorded as accepted-known)
- **I1. Silent no-op on type mismatch in `InitializeArrayNeo`.** If `data` is not `byte[]` or `array` is not `Array`, the redirect silently returns without initialising the array (it stays zero-filled) rather than throwing. This is parity with the Legacy body (which early-returns on `data == null`) and is acceptable for the array-initializer use case where the types are always correct, but a future malformed caller would get a silently-zeroed array. No action required for this change.
- **I2. Isolate-filter string.** Use `ClrVtReffieldsBinder` (not `NeoClrVtReffieldsBinder`) to isolate these probes; the methods are `NeoStepClrVtReffieldsBinder_*`. Documented in the test file header; noted here so the shipper uses the right string.

No Blocker, Major, or Minor findings.

## Spec axis (proposal.md / tasks.md / specs/neo-arrays/spec.md)

- **InitializeArray Neo redirect requirement** - met (handler + RedirectMapNeo registration, GCHandle+Marshal.Copy mirroring Legacy, void return). Scenarios "large int array initializer populated correctly" and "non-int element-type supported" both pass (TC1 int[32], TC2 double[32]).
- **Step-13b RuntimeFieldHandle NIE eliminated** - met (full-smoke count 0).
- **Legacy-neutral** - met (Neo-gated; Legacy 320/17 baseline preserved, probes pass under Legacy).
- **ldtoken blob-as-byte[]-reference requirement** - met (Cecil InitialValue sourced, pushed as Neo ref; TC5 contents green).
- **Scope discipline** - clean. No scope creep: the binder/generic-marshal routes were correctly ruled out in the proposal; the autogen Step-13b stub (BindingGeneratorExtensions.cs:206) was correctly left untouched as the honest guard for genuine no-binder ref-field CLR structs; the F1 (Stobj/Ldobj) overlap was correctly deemed separate. Diagnostics from task 1 were removed (0 stray instrumentation).

## Standards axis

- Cursor-discipline comment in `InitializeArrayNeo` is accurate and matches the modeled `DelegateCombineNeo`.
- `try/finally` around `GCHandle.Free` is an improvement over Legacy.
- No new warnings introduced by the change (270 pre-existing MSB3277/CS1668 = known stale-machine-state noise).
- Tests follow the established probe discipline (no try/catch, deliberate DivideByZero fault on wrong contents -> real correctness check, not ran-without-throwing).

## Coverage

Both new code paths are covered:
- `InitializeArrayNeo` (4 branches: data-is-byte[] && array-is-Array && length>0 -> copy; each conjunct false -> no-op) is exercised by TC1 (int) + TC2 (double), asserting per-element contents (not just ran-without-throwing). The no-op branches are parity with Legacy and not separately probed (acceptable).
- ldtoken blob arm is exercised by TC5 (and TC1/TC2 transitively via the InitializeArray call), with byte-for-byte contents verification.

---

## Rationale for APPROVE

The root-cause diagnosis is confirmed (InitializeArray had no Neo redirect -> reflection fallback -> Step-13b NIE). The fix is the narrowly-correct one (a Neo redirect that bypasses the RuntimeFieldHandle marshal entirely), the cursor math and GCHandle lifecycle are correct and safer-than-Legacy, the ldtoken blob remediation is precisely scoped to RVA-blob fields and does not regress child-2's typeof probes, the stash-toggle proves the redirect is load-bearing and the probes are real, the 18 Step-13b NIEs are gone from the full smoke, and Legacy is structurally neutral. The only findings are one duplicated comment line (Trivial) and two informational notes. Ship it.
