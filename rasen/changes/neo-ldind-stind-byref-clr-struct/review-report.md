# Review Report — neo-ldind-stind-byref-clr-struct (child 15)

**Reviewer:** author != verifier (independent review agent)
**Date:** 2026-07-12
**Branch:** `features/object-model-overhaul`
**Mode:** dispatched (report-only) — no auto-fix, no commit, no subagents

## Verdict: APPROVE-WITH-FINDINGS

The fix is correct, sound, minimal, Neo-gated, and Legacy-neutral. The root cause is
accurately diagnosed and the remedy (JIT marker `0x8` + cached `Marshal.OffsetOf` in the
`ldflda` frame-native arm) is the right place to fix it. All gates pass. The two findings
below are ship-able quality/scope observations, not correctness defects — neither blocks.

## Scope check: CLEAN

- **Stated intent:** fix the AV when `ldind`/`stind` consume a byref produced by `ldflda`
  on a CLR-struct-LOCAL field (inline flat bytes in the frame), where the byref's offset
  half carried `FieldInfo.GetHashCode()` instead of a byte offset.
- **Delivered:** exactly that. Diff = 3 intended files
  (`JITCompiler.cs` marker constant + stamp, `ILIntepreter.Neo.cs` cache + marker read +
  frame-native branch + `ResolveClrStructFieldByteOffset` helper, new probe) + the
  durable-findings append to `planning-context.md` (task 5.3). No scope creep.
- No JIT/optimizer lowering change, no object-model/CLR-binding change, no
  `ldind`/`stind`/`stobj`/`ldobj`/`initobj` consumer change (they work unchanged once the
  byref is a valid frame address). Confirmed by reading the full diff.

## Independent re-verification

### 1. Marker mutual-exclusivity (`0x8`): CONFIRMED unambiguous

- **`0x8` is free.** Existing `ldflda` markers are `NeoLdfldaInlineMarker=0x1`,
  `NeoLdfldaClrStructFieldMarker=0x2`, `NeoLdfldaHeapIlRefFieldMarker=0x4`
  (`JITCompiler.cs:119/168/184`). `NeoLdfldaClrStructLocalFieldMarker=0x8` (`:206`) is the
  next bit.
- **Body-emission chain is an `if/else-if`** (`JITCompiler.cs:2981-3005`): only ONE of
  `0x2`/`0x4`/`0x8` is stamped per instruction. `IsClrStructFieldOfIL` (`:3192-3198`)
  requires `declaringType is ILType`; the `0x4` arm requires `type is ILType`; the new
  `0x8` arm requires `type is CLRType`. For a CLRType declaring type, `IsClrStructFieldOfIL`
  is false and the `0x4` guard is false, so execution reaches `else if (type is CLRType)`.
  Exactly one marker is set at body emission.
- **F-6 type-spec gate** (`JITCompiler.cs:1103-1169`, `TypeSpecializeNeoOpcodes case
  Ldflda`) fires only when the source operand `srcType is ILType && IsValueType && !IsEnum`.
  It stamps `0x1` and clears `0x2` and `0x4` (`:1154`, `:1166`). It does NOT touch `0x8`,
  and — critically — it does NOT fire for a CLR-struct source (`srcType` is `CLRType`, not
  `ILType`). So `0x1` and `0x8` cannot co-exist, and `0x8` is never cleared by the gate.
- **A CLR-struct-local field can NEVER be an IL-VT / heap-IL-ref case.** IL-VT (F-6)
  requires an `ILType` source; heap-IL-ref (`0x4`) requires an `ILType` declaring type.
  The `0x8` case is a `CLRType` declaring type with a frame-native operand. Disjoint.
- **Runtime dispatch** (`ILIntepreter.Neo.cs:1791-1879`): `0x8` is consulted ONLY inside the
  `else if (objIdx == -1)` branch (`:1850`). The heap-marker branches (`:1792`, `:1812`)
  require `objIdx >= 0` and check their own markers. A heap boxed CLR struct (`objIdx >= 0`,
  `0x8` set because declaring type is CLRType) falls through to the catch-all `else`
  (`:1864-1879`) and uses the hash via the existing 4d CLR-object path — `0x8` harmlessly
  ignored. No mis-route possible. **Unambiguous.**

### 2. `Marshal.OffsetOf` correctness + cache soundness: CONFIRMED

- **Hash round-trip is sound.** JIT stamps `op.Operand2 = offset.PrimitiveOffset`
  (`JITCompiler.cs:2973`). For a CLRType declaring type, `AppDomain.GetFieldOffset`
  (`AppDomain.cs:2257`) returns `PrimitiveOffset = type.GetFieldIndex(token)`, and
  `CLRType.GetFieldIndex` (`CLRType.cs:661-678`) returns `fieldMapping[f.Name]` =
  `i.GetHashCode()` (`:610-614`). The runtime helper calls `ct.GetField(fieldHash)`
  (`CLRType.cs:529`), which looks up the `Fields` dictionary = `fieldInfoCache`
  (`CLRType.cs:62-69`), keyed by the SAME `hashCode` (`:615`). `GetField(hash)` inverts
  `GetFieldIndex(token)` exactly.
- **Type resolution is sound.** JIT stamps `op.Operand = type.GetHashCode()`
  (`JITCompiler.cs:2972`). For a `CLRType`, `GetHashCode()` is the stable `instance_id`-
  based hash (`CLRType.cs:1139-1144`). CLRTypes are registered in `mapTypeToken` under
  exactly that hash (`AppDomain.cs:1356`, `:1781`), so `appdomain.GetType(typeHash)`
  (`AppDomain.cs:1787-1794`) resolves the CLRType. Empirically confirmed (probes PASS, no
  `ct == null` NIE).
- **`Marshal.OffsetOf` == managed layout for the reachable cases.** Only blittable CLR
  value types reach the flat-byte local path (a CLR VT with reference fields throws the
  Step-13b tagged NIE inside `ReadNeoValueType`/`WriteNeoValueType` first). C# value types
  default to `LayoutKind.Sequential`, for which `Marshal.OffsetOf` returns the same offset
  `Unsafe.WriteUnaligned` writes. The probes empirically prove this for `TestVector3`
  (`struct { float X, Y, Z }`): TC1 writes X at offset 0, TC2 writes Y at offset 4 —
  `Marshal.OffsetOf` is field-specific and authoritative.
- **try/catch → tagged NIE** (`ILIntepreter.Neo.cs:6335-6339`) for the auto-layout edge
  (unreachable for the blittable-only path). Fail-loud, no silent corruption. The helper
  also throws tagged NIE if `ct == null` or `GetField` returns null (`:6326`, `:6330`) —
  defensive, never hit in the smoke.
- **Cache is sound.** `ConcurrentDictionary<long,int>` keyed by
  `((long)typeHash << 32) | (uint)fieldHash` (`:6322`). The key includes `typeHash`, so
  distinct generic instantiations (distinct `CLRType` instances → distinct
  `instance_id`-based hashes) get distinct entries — no stale offset across
  instantiations. The offset is a property of the CLR `Type` (fixed per struct), and a
  per-AppDomain `CLRType` instance has a unique key, so the process-static cache cannot
  collide across AppDomains. `GetOrAdd` is thread-safe; a racing double-factory invocation
  produces the same `int` (idempotent), and a throwing factory (auto-layout) stores
  nothing so the next call retries — acceptable for the unreachable edge.

### 3. Gates re-run: PASS

- Build CLI `Debug_Neo --no-incremental`: **0 errors**. Build `TestCases -c Debug`: **0
  errors**.
- **NeoStep smoke (`... true NeoStep`): 343 ran / 0 failed, exit 0** (341 baseline + 2 new
  probes). Matches claim.
- **Targeted (`... true NeoStepLdindStindByrefClrStruct`): 2 ran / 0 failed.** Both TC1
  (offset 0, sum 88) and TC2 (offset 4, sum 144) PASS.
- **Stash-toggle (the load-bearing FAULT-to-PASS proof):**
  - Stashed ONLY `ILIntepreter.Neo.cs` (runtime fix removed; JIT marker `0x8` still
    stamped; probe retained). Rebuilt `Debug_Neo --no-incremental` (0 errors). Ran the
    probe filter: **`AccessViolationException: Attempted to read or write protected
    memory` → segfault, exit 139.** This proves the probe EXERCISES the fixed runtime arm
    (without the fix the arm produces `(-1, vtBase + <huge hash>)` → out-of-frame deref).
  - Restored the stash, rebuilt: probe **2/0 PASS** again. Round-trip clean.

### 4. Full-smoke / Test00 spot-check: PASS (no segfault)

- `... true TestValueTypeBinding` (fix applied): `Test00` runs to completion and prints
  `(-1.4693679E-37,1,1)` — X was addressed and written via the byref (offset 0), Y/Z
  untouched (=1). The `ldflda r1, r1, 0x07102FE7` (FieldInfo hash operand) →
  `ldind.r4`/`stind.r4` pipeline now works through the resolved byte offset. **No AV, no
  segfault.** The filter run's exit 127 is the known pre-existing Dict-NRE crash from a
  later unrelated test, NOT 139.

### 5. `addi`-on-float finding: CONFIRMED PRE-EXISTING (not introduced)

- With the fix applied, `Test00`'s JIT shows the optimizer folding
  `ldc.r4 r3,100; add r2,r2,r3` into **`addi r2,r2,1120403456`** (`1120403456` =
  `0x42C80000` = the IEEE-754 bit pattern of `100.0f`). `0x3F800000` (1.0f) integer-added
  to `0x42C80000` = `0x82480000` = `-1.4693679E-37` — exactly the garbage X printed. This
  is the surfaced `addi`-integer-add-of-float-bits bug.
- **Stashed BOTH source files** (the whole change), rebuilt at HEAD, ran `Test00`: the
  HEAD JIT emits the **identical** `addi r2,r2,1120403456`. The runtime then segfaults
  (exit 139) at `ldind.r4` only because the `ldflda` fix is absent; the JIT lowering that
  produces the `addi` is unchanged on HEAD. The `addi` constant-fold lives in the
  optimizer's `Add` handling, which this change does NOT touch (the diff modifies only
  `case Code.Ldflda`). **This change UNMASKS the bug** (`Test00` now runs far enough to
  execute the `addi`, making the garbage X visible) but does not INTRODUCE it.
  `conv.i4`-float-reinterpret is the same story (untouched by this diff). Correctly
  documented in the probe comments and the design; the probes sidestep both via host-side
  float arithmetic (`TestCLRBinding.SumTestVector3Fields`). Recorded as a follow-up
  candidate child.

### 6. Legacy-neutral: CONFIRMED

- Plain-`Debug` NeoStep (`... true NeoStep`): **343 ran / 17 failed == baseline.** The 17
  failures are the known Legacy set (e.g. `NeoNaNR8 nan == nan`). **0 of the 17 failures
  match `LdindStindByref`** — both new probes PASS under Legacy. Neo-gated by
  construction; no Legacy regression.

## Findings

### Minor

- **M1 — TC2 probe does not fully discriminate field identity.** TC2 asserts
  `SumTestVector3Fields(v,v) == 144`; since the sum `2*(X+Y+Z)` is symmetric in which field
  holds `70`, a hypothetical hard-wired-offset-0 fix (writing `70` to X instead of Y)
  would ALSO yield `144` and pass. The ACTUAL fix is correct by construction
  (`Marshal.OffsetOf("Y")` is field-specific and returns 4), and the stash-toggle proves
  the AV is resolved, so this is a probe-strength nit — not a fix defect. The probe's own
  comment (`TestCases/NeoStepLdindStindByrefClrStructTest.cs:60-61`) overstates:
  "NOT a hard-wired 0 (which would clobber X)" — clobbering X would still produce sum 144.
  A stronger TC2 would assert `v.X == 1 && v.Y == 70` individually. Suggested for a
  follow-up; does not block.

- **M2 — Surfaced pre-existing Neo float bugs (addi-on-float, conv.i4-on-float).** Now
  observable because `Test00` reaches them post-fix (previously masked by the segfault).
  Correctly documented in the probe comments / design and sidestepped by the probes.
  Candidate future child (`addi` lowering for float-typed `add`). Not this change's defect.

### Trivial

- **T1 — TC2 comment overstatement** (see M1): "NOT a hard-wired 0 (which would clobber
  X)" is not strictly true given the symmetric sum. Cosmetic; tighten if M1 is addressed.

- **T2 — `ip->Operand3` (`offset.ReferenceOffset`) unused for the CLR-struct-local case.**
  Harmless (the frame-native branch uses only the byte offset), noted for completeness;
  not a defect.

## Standards axis: PASS — no documented-standard violations in the diff.
## Spec axis: PASS — the MODIFIED `ldflda / ldarga address producers` requirement and its
"ldflda on a CLR-struct-local field resolves the real managed byte offset" scenario are
faithfully implemented (marker stamp for CLRType declaring type; cached `Marshal.OffsetOf`;
produces `(-1, vtBase + fieldManagedByteOffset)`; consumers unchanged).

## Rationale for verdict

APPROVE-WITH-FINDINGS: zero Blockers, zero Majors. The fix is in the right place (the
`ldflda` producer, not the typed `ldind`/`stind` consumers that carry no field metadata),
the marker is genuinely free and unambiguous (verified the full body-emission chain + the
F-6 gate + the runtime dispatch ordering), the `Marshal.OffsetOf` resolution is correct and
cached soundly for the blittable-only reachable path, the stash-toggle proves the probe
exercises the fixed arm (FAULT → PASS), NeoStep is 343/0, `Test00` no longer segfaults,
the surfaced `addi`-on-float bug is confirmed pre-existing, and Legacy is neutral. The two
Minor findings are ship-able probe-quality / surfaced-pre-existing items for the LEAD to
route to a follow-up, not reasons to hold the change.
