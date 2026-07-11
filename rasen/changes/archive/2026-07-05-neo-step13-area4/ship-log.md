# Ship Log — neo-step13-area4

**Change:** `neo-step13-area4` — CLR value-type instance `this` direct-call (Area 4b)
+ `Unsafe.Unbox<T>` boxed direct-call (Area 4a).
**Branch:** `features/object-model-overhaul` (working tree UNCOMMITTED; the LEAD
commits after this ship step).
**Ship date:** 2026-07-05.
**Review verdict:** APPROVE (0 Blocker / 0 Major). 3 findings: 2 accepted-known
**Minor** (M1 test gap, M2 latent dead-branch mis-copy) + 1 **Trivial** (T1 stale
NIE text). All recorded below; none block landing. Coverage gap (the reviewer's
removed temp IL-VT-instance-method probe) also recorded.

---

## 1. Scope delivered

**IN (this change):**
- **4b — CLR value-type instance `this` direct-call.** `local.VTInstanceMethod()`
  and `new ClrStruct(args)` (both lower to `ldloca; call`) read the struct `this`
  as flat bytes via `ReadNeoValueType`, not as a 4-byte mStack index.
- **4a — `Unsafe.Unbox<T>` boxed direct-call + write-back.** A mutating CLR
  struct instance method invoked on a boxed struct propagates its mutation back
  via the post-call reverse copy (CLR `ref this` struct semantics).

**OUT (separate follow-up child `neo-step13-area4-refandstind`, tracked as
portfolio task #17):**
- **4c — CLR-method `ref`/`out` parameters** (typed-reference bridge:
  copy-in / call / copy-out, or a pinned frame address).
- **4d — CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field hash**
  (`Ldflda` field-hash stamping + consumer-arm dispatch to
  `CLRType.Get/SetFieldValue(hash, target)`).

Splitting {4c, 4d} out keeps the {4a, 4b} diff reviewable (the explicit 13b
lesson: bundling independent plumbing makes the diff unreviewable). {4a, 4b}
share the same generated-prologue concern (the `instance_of_this_method` read),
so they ship together.

---

## 2. Mechanism (deviated from design's literal D2/D3 — dump-gated)

The design's D2/D3 assumed the readers dereference the byref `this`. The
apply-phase JIT dump disproved this: BOTH readers lack the real caller
`frameBase` (the autogen `CLRRedirectionDelegateNeo` is invoked with `targetBase`
as `frameBase`, NOT the caller frame; the reflection `Invoke(byte*)` only gets
`targetBase`). Threading the real `frameBase` through the delegate signature
would break the checked-in static bindings.

**The dump-confirmed `this`-slot representation (the load-bearing finding):**
- The C# compiler lowers `local.VTInstanceMethod()` and `new VT(args)` to
  `ldloca; call`. The **source** register holds a frame-native byref = an 8-byte
  Ref Slot `(-1, structFrameOffset)` produced by `ldloca`.
- The call-lowering (`Optimizer.Neo.cs:1166-1186`) sizes the VT instance `this`
  **dest** slot via `AllocateNeoCallParamSlot(DeclearingType)` -> the
  `IsValueType` branch -> flat bytes (`GetNeoValueTypeManagedSize`, e.g. 12 for
  a Vector3). So **source = 8-byte byref; dest = flat-byte struct width**.
- Pre-fix read 12 bytes from the 8-byte byref -> garbage (the F-3 defect).

**The fix dereferences the byref at the copy site** (instead of in the readers):

1. **`NeoCallParamMap.PrimitiveByRefSrc`** — new flag (`JITCompiler.cs`).
2. **Call-lowering** marks the VT instance `this` slot's source as byref
   (`Optimizer.Neo.cs`, the `dstIsVtThisSlot` discriminator keys on
   `HasThis && !Newobj && p==0 && DeclearingType.IsValueType && !IsPrimitive &&
   !IsEnum`).
3. **`CopyNeoCallArguments`** derefs a byref source: reads the 8-byte Ref Slot,
   copies `PrimitiveSize[i]` bytes from `frameBase + offset` into the dest slot.
   The callee param region's `this` slot now holds FLAT BYTES. For every non-VT-
   `this` call shape this flag is false -> the original `Unsafe.CopyBlock` runs
   byte-identical (verified by the review's blast-radius table).
4. **Both readers** — the reflection fallback `CLRMethod.Invoke(byte*)` `HasThis`
   arm AND the autogen `GenerateMethodWraperCode_Neo` prologue — read flat bytes
   via `ReadNeoValueType` exactly like a by-value VT param. No `frameBase`, no
   per-call discriminator.
5. **NEW `CopyNeoCallThisBack`** — a post-call reverse copy (invoked in the bare
   `Call` arm, `ILIntepreter.Neo.cs:1676`) propagates ctor / mutating-method
   mutations to the caller's in-frame local at the byref's offset — CLR `ref
   this` struct semantics. (Verified: `ConstructorInfo.Invoke(box, args)` /
   `MethodInfo.Invoke(box, args)` mutate the boxed struct in place, NOT a copy.)

**D4 boxed-re-box NOT emitted in the autogen wrapper.** The dump-gate confirmed
NO direct-`call` path produces a boxed `this` (`objectIndex >= 0`); a direct
`call` always uses `ldloca` (frame-native byref). A boxed `this` is ONLY
reachable via `constrained.callvirt` -> the Step 17 `Constrained` NIE today -> so
the D4 boxed-re-box discriminator would be dead code in the current scope. The 4a
write-back IS implemented for the reflection path via `CopyNeoCallThisBack`; the
autogen 4a boxed-re-box lands with the Step 17 `Constrained` completion child.

**`WriteBackInstance` confirmed Neo-no-op.** `WriteBackInstance` is emitted ONLY
by `GenerateMethodWraperCode_Legacy` (`MethodBindingGenerator.cs:780` / `:786`);
the Neo generator does NOT emit it. The Neo value-type-`this` write-back is the
post-call `CopyNeoCallThisBack` reverse copy (reflection) / boxed-re-box
(autogen, deferred to Step 17).

---

## 3. F-3 / NEO-BYREF-THIS — CLOSED for the direct-`call` shape

**Closed:** a DIRECT `call` on a CLR struct `this` now works:
- `new ClrStruct(args)` (lowers to `initobj; ldloca; call .ctor`) — green
  (probe `NeoStep13_ClrStructNewobjCtor`).
- `v.LengthSquaredInt()` on an in-frame CLR struct local — green (read-only
  instance method; probe `NeoStep13_ClrStructInstanceMethod`).
- `v.Reset()` mutating instance method on an in-frame local — green; the
  mutation propagates back to the local (probe
  `NeoStep13_ClrStructInstanceMethodMutating`).
- IL value-type instance method (`ilLocal.VTMethod()`, lowers identically to
  `ldloca; call`) — green on BOTH engines (the reviewer's custom temp probe;
  removed after confirmation — see the Coverage gap below).

**NOT closed (NOT a regression):** `callvirt` on a CLR struct override compiles
to `constrained.callvirt` -> the Step 17 `Constrained` arm NIE
(`ILIntepreter.Neo.cs:~3140`). This is the **Step 17 D-CONSTRAINED** follow-up
(tracked as portfolio task #5 `neo-step17-completion`), NOT closed by this change
and NOT a 4b regression. The direct-`call` path (the 4b scope) is fully closed.

**Stash-toggle proof (load-bearing):** with the 5 runtime files stashed, 6 of 9
`NeoStep13_*` probes FAIL on HEAD with the F-3 `ArgumentOutOfRangeException`; 3
pass on HEAD (K2-FAM 5.7, List<T> ref-call 5.8, boxed-mutating round-trip 5.6 —
none exercise the broken VT-`this` reflection path). The fix is load-bearing.

---

## 4. Verification

- **Neo `NeoStep` smoke: 117/117** (108 baseline + 9 new `NeoStep13_*` probes).
- **NeoOptHardening: 16/16.**
- **Legacy `NeoStep13_` probes: 9/9** on plain `Debug` + `useRegister=true`
  (Legacy-neutral). The 7 pre-existing Legacy `NeoStep` failures reproduce with
  the fixes stashed (NOT caused by this change; all runtime edits are in
  `#if ENABLE_NEO_MODE`-gated files / Neo-only paths).
- **Review APPROVED** (0 Blocker / 0 Major). The blast-radius table confirmed all
  5 non-VT-`this` call shapes (CLR static, CLR instance on a reference type,
  IL->IL, CLR no-byref-param, CLR with a byref PARAM) are byte-identical to HEAD.
- **Reviewer's custom IL-VT-instance-method probe** confirmed the byref-deref is
  correct for IL value types too (C# lowers `ilLocal.VTMethod()` to
  `ldloca; call` — the same shape). Green on Neo AND Legacy; then removed (see
  Coverage gap below).

---

## 5. Accepted-known Minor / Trivial findings (recorded, NOT silently dropped)

These are documented here and routed to follow-ups; none block landing.

### M1 — Minor (test gap): autogen VT-`this` path untested

**File:** `TestCases/NeoStep13bTest.cs` (the 9 probes) +
`ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs:258-281`.

All 9 VT-`this` probes use `TestVector3NoBinding` (deliberately NOT in the
`RegisterValueTypeBinder` list, only redirects STATIC members) -> they exercise
the **reflection fallback** (`CLRMethod.Invoke(byte*)` HasThis arm) ONLY. The
autogen `GenerateMethodWraperCode_Neo` VT-`this` `ReadNeoValueType` read (+ its
`NeoBindingHasReferenceField` NIE guard) is generated but NOT exercised by any
smoke probe — it would only fire for a binder-registered pure-primitive struct's
instance method, and no such case exists in the smoke.

**Why Minor not Major:** the autogen code mirrors the reflection path's
`ReadNeoValueType` read exactly (same helper, same size source), and the
reflection path IS tested. The residual risk is a codegen typo in the emitted
string template that the smoke cannot catch.

**Route:** a future test-coverage pass / the opportunistic-cleanup portfolio
child should add an autogen-bound VT-`this` probe (e.g. register a binder for a
struct with an instance method, or add an instance method to a binder-registered
struct, so the autogen redirect delegate is taken).

### M2 — Minor (latent mis-copy on an UNREACHABLE branch): boxed-source branch
of `CopyNeoCallArguments` + `CopyNeoCallThisBack` comment accuracy

**File:** `ILIntepreter.Neo.cs:295-300` (the `else` / boxed branch of the byref
deref) and `CopyNeoCallThisBack`'s ctor-coverage comment.

The `else` branch (`objIdx >= 0`, boxed-struct `this`) performs a
`CopyBlock(targetBase + Dst[i], frameBase + offset, Size[i])` — but for a boxed
source, `offset` is an mStack field offset, so `frameBase + offset` is not a
meaningful struct address. This branch is **genuinely unreachable today** (a
boxed `this` is only reachable via `constrained.callvirt`, which is the Step 17
`Constrained` NIE). It is dead-on-arrival code that would silently misbehave IF
the Step 17 Constrained arm is later completed without revisiting this branch.

Also: the `CopyNeoCallThisBack` comment claims ctor coverage, but the newobj path
does NOT invoke it (the ctor's result flows via the method RETURN through
`InvokeNeoClrMethod`'s newobj branch, NOT via the reverse copy). Behavior is
correct; the comment is slightly misleading.

**Route:** the Step 17 completion child (`neo-step17-completion`) MUST add a NIE
guard (or a correct copy) in the boxed-source branch of `CopyNeoCallArguments`
when it lands the Constrained path, and tighten the `CopyNeoCallThisBack` comment
to say it covers mutating INSTANCE METHODS (not ctors). Recorded as
**F-5 / NEO-CALLARG-BOXED-SRC** in `neo-deferred-items.md` (folded into the
D-CONSTRAINED §3 entry for discoverability).

### T1 — Trivial (stale NIE text): Constrained NIE references old step

**File:** `ILIntepreter.Neo.cs:~3141`.

The `Constrained` NIE string still says "callvirt byref-this dispatch lands in
Step 13b / a follow-up", but this change reclassified callvirt-on-CLR-struct as
Step 17 (D-CONSTRAINED), explicitly NOT 4b. The message is stale/misleading.

**Route:** one-line text fix to "Step 17 D-CONSTRAINED follow-up
(constrained.callvirt on a value type)", anytime. Recorded in
`neo-deferred-items.md`.

### Coverage gap — IL value-type instance method calls (the reviewer's removed
temp probe)

IL value-type instance method calls (`ilLocal.VTMethod()`) are uncovered by the
smoke. The reviewer confirmed this shape works on BOTH engines via a temporary
probe (`NeoStep12bReviewIlVtInstanceMethod`, an IL struct `Sum()` instance
method), then removed it (Neo-specific NIE assertions fail on Legacy;
accept-both is too weak; the existing smoke has ZERO IL-VT-instance-method
coverage — the NeoStep12/12b structs are field-only). The flag DOES fire for
IL-VT instance calls (the `dstIsVtThisSlot` discriminator is computed inside the
shared `if (paramInfos != null)` block, and `DeclearingType.IsValueType` returns
true for IL value types), and it is empirically correct — but it was previously
unverified.

**Route:** a future test-coverage pass should add a keeper IL-VT-instance-method
probe so this path stays green-guarded.

---

## 6. Files touched (all confirmed; working tree UNCOMMITTED)

All runtime changes are Neo-only / in `#if ENABLE_NEO_MODE`-gated files; Legacy
is byte-identical (the REFERENCE, not modified):

- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` —
  `NeoCallParamMap.PrimitiveByRefSrc` flag.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` — call-lowering
  marks the VT instance `this` slot source as byref.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` — byref-deref
  in `CopyNeoCallArguments` + new `CopyNeoCallThisBack` post-call reverse copy +
  Call-arm invocation of the reverse copy.
- `ILRuntime/CLR/Method/CLRMethod.cs` — `Invoke(byte*)` HasThis value-type read
  + post-invoke write-back.
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` — autogen
  `GenerateMethodWraperCode_Neo` VT `this` prologue (replaces the
  `// TODO: ValueType instance in Neo`).
- `ILRuntimeTestBase/TestFramework/TestVector3.cs` — host instance methods +
  ref-field struct.
- `TestCases/NeoStep13bTest.cs` — 9 adversarial `NeoStep13_*` probes.

---

## 7. Follow-ups routed

| Follow-up | Route | Tracker |
|-----------|-------|---------|
| 4c CLR ref/out + 4d CLR stin/ldind | new child `neo-step13-area4-refandstind` | portfolio task #17 |
| `callvirt`/`constrained.callvirt` on a CLR struct | Step 17 D-CONSTRAINED | portfolio task #5 (`neo-step17-completion`) |
| M1 autogen VT-`this` test gap | opportunistic test-coverage pass | planning-context `## Follow-ups discovered` |
| M2 boxed-source NIE + comment accuracy | Step 17 completion child | F-5 / NEO-CALLARG-BOXED-SRC in `neo-deferred-items.md` |
| T1 stale Constrained NIE text | one-line fix, anytime | planning-context `## Follow-ups discovered` |
| IL-VT-instance-method coverage gap | opportunistic test-coverage pass | planning-context `## Follow-ups discovered` |

---

## 8. Did NOT do (per process discipline)

- Did NOT `git commit` / `git push` — the LEAD commits after this ship step.
- Did NOT close F-3 / NEO-BYREF-THIS for the `callvirt` shape (that is Step 17
  D-CONSTRAINED, not this change).
- Did NOT silently drop the M1/M2/T1 findings or the IL-VT coverage gap — all
  recorded above and routed.
