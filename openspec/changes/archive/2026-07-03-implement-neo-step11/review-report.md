# Review Report — implement-neo-step11 (Neo Step 11: interface method dispatch)

Reviewer stage: VERIFY (independent; author != verifier). Method: openspec-gstack-review
skill (structured + adversarial passes) plus focused manual scrutiny of the 8 high-risk
areas. Builds and smoke runs executed locally to corroborate claims.

## Executive verdict

**FINDINGS (no Blocker, no Major).** The implementation is correct against the spec and
design for every scenario the harness can exercise, and the highest-risk deviation (the
`BuildNeoVTable` modification) is contained and proven non-corrupting by both a static
invariant argument and a 31/31 broad NeoStep run. The remaining findings are Minor /
Trivial / accepted-known. The one spec-listed scenario that has no green test (the
negative "object does not implement interface" path) is honestly classified below as an
**accepted-known Minor** — it is code-verified and unreachable-via-test only because the
harness lacks `Leave_S` (Step 6); the contract is enforced in handler code.

### Verification performed
- `dotnet build ILRuntimeTestCLI -c Debug_Neo` → 0 errors (Neo lib compiles).
- `dotnet build TestCases -c Debug` → 0 errors (NeoStep11Test.cs compiles, including the
  negative/unrelated types).
- Smoke `NeoStep11` filter → **Ran 5 tests, 0 failed** (single iface, override, multi-iface,
  inheritance chain, CLR IDisposable).
- Smoke `NeoStep` filter → **Ran 31 tests, 0 failed** (broad virtual-dispatch regression —
  confirms the `BuildNeoVTable` modification did NOT corrupt pre-existing virtual slots).

## Severity counts

- Blocker: 0
- Major: 0
- Minor: 3
- Trivial: 3

---

## Scrutiny-area verdicts (all 8)

1. **`BuildNeoVTable` modification (deviation #1) — SAFE.**
   - `EnsureNeoInterfaceImplementorSlots` (ILType.cs:522) is appended AFTER the normal
     candidate loop and only calls `AddNeoVTableSlot` for keys NOT already in `slotMap`
     (ILType.cs:547 `if (slotMap.ContainsKey(key)) continue;`). `AddNeoVTableSlot`
     (ILType.cs:593-598) assigns `slots.Count` as the new index and adds the key first.
     Therefore **no pre-existing Step 10 virtual slot can change index** — the
     append-only invariant that protects ALL virtual dispatch holds.
   - The whole block is under `#if ENABLE_NEO_MODE` (ILType.cs:340 … 979), so Legacy is
     untouched. Confirmed by enumerating the `#if/#endif` pairs.
   - Recursion/lazy-build guards: `EnsureNeoInterfaceImplementorSlots` runs inside
     `BuildNeoVTable`'s existing `neoVTableBuilding` guard; the interface-map builder has
     its own `neoInterfaceMapBuilding` guard (ILType.cs:797, mirroring Step 10).
   - Regression evidence: broad `NeoStep` = 31/31 pass, including override/virtual paths
     not covered by the 5-test NeoStep10 smoke. Acceptable confidence that this change is
     non-corrupting.

2. **Offset map correctness (deviation #3) — CORRECT.**
   - Contiguous fast path (AddNeoInterfaceEntry, ILType.cs:873-942): `firstClassSlot` is
     the actual resolved class slot of the first candidate method (from
     `neoVTableSlots[key]`), and `VTableOffset = firstClassSlot`. The contiguous check
     `classSlot == firstClassSlot + localSlot` is an optimization; every `classSlot` is
     individually correct because it is looked up by SignatureString. So a wrong anchor
     would only flip into the remap path (which is also correct), not mis-dispatch.
   - Remap path (ClassSlotRemap) honored by `TryResolveNeoInterfaceClassSlot`
     (ILType.cs:950-974): explicit per-method index, with `remapped < 0 → return false`
     (surfaces as the handler's clear exception).
   - **Slot-index identity invariant verified:** the JIT-time `GetInterfaceMethodSlotSelf`
     (`BuildNeoSelfMethodSlots`, ILType.cs:767) and the runtime
     `AddNeoInterfaceEntry` (which calls `iface.GetMethods()`) BOTH enumerate the
     interface ILType's own `methods` dictionary with the same `IsNeoVTableCandidate`
     filter. Same source dict + same filter on the same type → same ordering and same
     slot indices. The encoded `interfaceMethodSlot` therefore lands on the identical
     method at JIT and runtime. (Dict iteration order is stable within a process for a
     deterministic `InitializeMethods` insertion sequence.)
   - **SignatureString key consistency verified:** `ILMethod.SignatureString`
     (ILMethod.cs:507-534) is `Name|GenCount(param fullnames)->return fullname` — it does
     NOT include the declaring type. So an interface method and its implicit C# implementor
     share the same key, which is exactly what makes `FindNeoImplementingMethod`
     (ILType.cs:570) and the offset resolution land correctly.
   - **Derived-class inheritance invariant verified:** `BuildNeoVTable` copies base slots
     at identical indices before appending derived slots (ILType.cs:441-451), so the
     inherited interface-map entries (copied verbatim at ILType.cs:829-836, including any
     inherited `ClassSlotRemap`) remain valid in the derived VTable; an override replaces
     slot *content* at the same index. The override test returns "derived-bar", confirming.

3. **Handler failure-mode contract — MET.**
   - `ResolveNeoCallvirtInterfaceTarget` (ILIntepreter.Neo.cs:247-273): every index/lookup
     is bounds-checked. `ReadNeoCallThis` (ILIntepreter.Neo.cs:207-219) null-checks `this`
     first; then `is ILTypeInstance` throws `InvalidOperationException` for CLR `this`
     (deviation #7, ILIntepreter.Neo.cs:251); missing interface / out-of-range / null slot
     all throw `MissingMethodException` (ILIntepreter.Neo.cs:257, 264, 270). No `continue`
     on the failure paths → no ip overrun; no indexing before bounds check → no null-deref;
     `Callvirt_Interface` is a Neo-only opcode → no Legacy fallback. Matches
     `ResolveNeoCallvirtILTarget`'s established exception style.

4. **JIT lowering ordering — CORRECT.**
   - The interface branch is the FIRST `if` in `InitializeCallvirtDispatch`
     (JITCompiler.cs:2113-2129) and `return`s before the `!IsInterface` guard at line 2130.
     Verified the IL branch guard at 2130 would otherwise swallow interface methods.
   - Note (out of scope, not a defect): `InitializeCallvirtDispatch` is only reached when
     `op.Code == Callvirt && !hasConstrained` (JITCompiler.cs:1483). A `constrained.`
     interface callvirt therefore never emits `Callvirt_Interface` and stays on the
     generic arm. This matches the design's explicit "constrained. is Step 13/18 territory"
     non-goal — it degrades gracefully (generic arm), does not crash.

5. **Optimizer coverage — COMPLETE.**
   - `Callvirt_Interface` is added to all four call-op switches in Optimizer.Utils.cs
     (lines 538, 757, 1174, 1345) and to the lowering switch in Optimizer.Neo.cs:379.
   - **Bit-collision guard verified (important):** Optimizer.Neo.cs:389-392 explicitly
     excludes `Callvirt_Interface` from the `hasConstrained` computation. Without this, an
     interface whose 2nd method has `ifaceMethodSlot == 1` would set `Operand4 == 1`
     (`EncodeCallvirtInterface(1, 0)`) and be misread as a constrained call. The explicit
     exclusion makes `hasConstrained` correctly false. Good defensive coding.
   - The `paramInfos == null` fallback (`AllocNeoParamInfosFromSignature`,
     Optimizer.Neo.cs:601-624) synthesizes a contiguous callee layout mirroring the
     CLRMethod branch (slot 0 = `this` for HasThis, then params). Correct call-ABI.
   - The one switch in JITCompiler.cs:1532-1535 that references Callvirt_IL/CLR without
     Callvirt_Interface is SAFE: it is gated on `m is CLRMethod cm`, and an interface
     declared method is an ILMethod, so the branch is never entered for interface calls.

6. **Operand encoding — CORRECT, runtime lazy build confirmed.**
   - `Operand4 = (thisArgOffset << 16) | interfaceMethodSlot` via
     `EncodeCallvirtInterface` (JITCompiler.cs:2158). thisArgOffset is encoded as 0 and
     never patched at the call site — identical to Step 10's
     `EncodeCallvirtDispatch(slot, 0)` (the implementer's task-3.3 finding is accurate:
     Step 10 also never patches the high 16 bits; `this` is always the first call arg).
   - JIT-time prewarm was correctly dropped (the interface type itself has no class
     VTable). The offset map IS built lazily at runtime: the handler calls
     `TryResolveNeoInterfaceClassSlot` (ILIntepreter.Neo.cs:257) which calls
     `EnsureNeoInterfaceMap()` (ILType.cs:953) → `BuildNeoInterfaceMap()` →
     `EnsureNeoVTable()` first (ILType.cs:799). So the map exists before the handler reads
     it. Confirmed by the 5/5 green interface tests.

7. **Negative test gap (deviation #5) — accepted-known Minor (see below).**

8. **General correctness — sound.** Null-handling, key reuse, and the tested edge cases
   (multi-interface non-aliasing, inheritance chain, override) all hold.

---

## Findings

### Minor

**M1. Negative failure-path scenario has no green test (spec scenario "Object does not
implement the target interface").**
- File: `TestCases/NeoStep11Test.cs:100-121` (types present, no runnable assertion).
- What: The spec lists the missing-interface / missing-slot scenarios as required
  scenarios. The handler enforces them (ILIntepreter.Neo.cs:257/264/270) and they are
  verified by code review, but they are NOT exercised by any passing test because the
  custom harness has no `[ExpectedException]` and IL `try/catch` needs `Leave_S` (Step 6).
- Why it matters: A future refactor of `ResolveNeoCallvirtInterfaceTarget` could regress
  the clear-exception contract (e.g., reintroduce a null-deref or ip overrun) without any
  test catching it, until Step 6 lands.
- Classification: **accepted-known Minor.** The constraint is real (harness limitation),
  the implementer documented it honestly, and the types are staged for a manual/Step-6
  test. Acceptable for landing Step 11; track a follow-up to add the negative case once
  Step 6 (`Leave_S`) is implemented.
- Suggested fix (deferred): when Step 6 lands, add a test that triggers
  `INeoStep11Negative` dispatch on `NeoStep11NegativeUnrelated` inside a `try/catch` and
  asserts a `MissingMethodException` is caught.

**M2. The `NeoStep11TestClrInterfaceIDisposable` test does NOT exercise the new
`Callvirt_Interface` opcode.**
- File: `TestCases/NeoStep11Test.cs:157-164`; lowering path JITCompiler.cs:2141-2148.
- What: `IDisposable.Dispose` resolves to a `CLRMethod` (not an `ILMethod`), so
  `InitializeCallvirtDispatch`'s interface branch (`targetMethod is ILMethod`) is never
  entered. The call is lowered to the generic `Callvirt` arm via
  `MayCallvirtTargetILObject` (declaring `IDisposable.IsInterface == true` → true). So
  this test passes through Step 10's pre-existing generic path, not Step 11's new
  mechanism.
- Why it matters: The test name and the spec scenario "IL class implementing a CLR
  interface (IL side)" imply coverage of Step 11's interface dispatch, but the new
  `Callvirt_Interface` opcode / offset-map is not on this path. The CLR-interface-through-IL
  direction is genuinely handled (and the design declares the CrossBindingAdapter reverse
  direction out of scope), so behavior is correct — but the test is weaker evidence for
  Step 11 than it appears.
- Suggested fix: Add a clarifying comment on the test (and/or the spec scenario note)
  stating that CLR-interface dispatch is served by the generic `Callvirt` arm, not
  `Callvirt_Interface`; or, to truly exercise `Callvirt_Interface` against a CLR-typed
  interface, add an IL-declared interface whose implementing type is invoked through the
  interface variable (the existing single/multi/chain tests already do this for IL
  interfaces). No code change required for correctness.

**M3. `BuildNeoSelfMethodSlots` / `AddNeoInterfaceEntry` slot-ordering invariant is
load-bearing but undocumented at the call sites.**
- File: ILType.cs:767 (`BuildNeoSelfMethodSlots`) and ILType.cs:873 (`AddNeoInterfaceEntry`,
  via `iface.GetMethods()`).
- What: Correctness of `Callvirt_Interface` depends on the JIT-time interface-local slot
  `k` (computed on the interface type's own `methods` dict) matching the runtime slot `k`
  (computed by enumerating the same interface's `GetMethods()`). This holds only because
  both read the identical `methods` dictionary with the identical `IsNeoVTableCandidate`
  filter. There is no assertion guarding this equivalence.
- Why it matters: A future change that alters `GetMethods()` ordering (e.g., sorting,
  dedup, or a different filter) for ONE of the two call sites but not the other would
  silently mis-dispatch interface methods (the encoded slot would point at the wrong
  method) — the worst silent-corruption failure mode.
- Suggested fix: Add a short comment at `BuildNeoSelfMethodSlots` and
  `AddNeoInterfaceEntry` stating that the two MUST enumerate the interface's methods in
  the same order with the same candidate filter (cross-reference each other), and/or add a
  one-shot debug assert that the per-interface slot count and key sequence match between
  the JIT self-map and the runtime entry.

### Trivial

**T1. `GetInterfaceMethodSlotSelf` throws `MissingMethodException`, but the JIT call site
has no recovery.**
- File: ILType.cs:762-765; caller JITCompiler.cs:2125.
- What: If the declared interface method is not found in the interface's own self-map
  (should not happen for a well-formed callvirt), `GetInterfaceMethodSlotSelf` throws at
  JIT time. This surfaces as a JIT exception rather than the runtime
  `MissingMethodException` the spec describes.
- Why it's trivial: For any valid callvirt the declared method IS declared on the
  interface, so the throw is unreachable in practice; and even if hit, it is still a clear
  exception (just at JIT time). Consistent enough with the "clear exception" contract.
- Suggested fix (optional): none needed; mention only if hardening.

**T2. `FindNeoImplementingMethod` skips `IsConstructor` but `IsNeoVTableCandidate` also
excludes static/ctor — minor inconsistency, no behavioral impact.**
- File: ILType.cs:570-588 vs ILType.cs:637-652.
- What: `FindNeoImplementingMethod` manually filters `IsStatic || IsConstructor`; the
  shared `IsNeoVTableCandidate` does the equivalent via `IsStatic || IsConstructor` +
  virtual checks. The implementing-method search is intentionally broader (it wants
  non-virtual implicit implementors too, which `IsNeoVTableCandidate` rejects), so the
  divergence is correct. Pure cosmetic note.
- Suggested fix: none.

**T3. Exception messages hardcode the interface FullName even when `ifaceType` was null.**
- File: ILIntepreter.Neo.cs:264, 270.
- What: The two later `throw` sites use `ifaceType.FullName` without the null-guard the
  first site (line 257) uses. These are only reached after `ifaceType != null` is
  implicitly true (the method returned a classSlot), so no NPE in practice.
- Why it's trivial: `TryResolveNeoInterfaceClassSlot` returning true implies the entry was
  found, which implies `ifaceType` matched a non-null entry — but `ifaceType` itself was
  the (null-checked-at-257) lookup key. Reachable only with a non-null ifaceType.
- Suggested fix (optional): use the same `ifaceType != null ? ifaceType.FullName : "<null>"`
  pattern for consistency/robustness.

---

## Notes carried from the openspec-gstack-review skill pass

- Scope check: **CLEAN.** Stated intent = Step 11 interface dispatch; delivered = interface
  offset map + `Callvirt_Interface` opcode + JIT/Optimizer lowering + 5 tests + 1
  staged-negative. No scope creep; the `BuildNeoVTable` extension is a justified, contained
  deviation (deviation #1) needed to make the offset map landable, not gold-plating.
- Enum/value completeness (Pass 1): `Callvirt_Interface` is handled in every switch that
  lists its siblings — verified by grepping all `Callvirt_CLR`/`Callvirt_IL` occurrences
  (OpCode.cs, OpCodeREnum.cs, ILIntepreter.Neo.cs, JITCompiler.cs, Optimizer.Neo.cs,
  Optimizer.Utils.cs x4). The single switch at JITCompiler.cs:1532 omits it but is gated
  on `m is CLRMethod` so it is unreachable for interface calls (see scrutiny #5).
- Concurrency / data safety: N/A (single-threaded lazy build with reentrancy guard).
- Adversarial (Codex) pass: not run (Codex CLI not available in this environment); the
  Claude adversarial scrutiny above covers the same edge-case classes (silent
  mis-dispatch, slot-index drift, bit collision, null-deref, ip overrun).
