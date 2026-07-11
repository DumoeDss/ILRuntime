## Why

Step 13b shipped Area 5 (the unified CLRMethod flat-bytes param layout) and
deliberately scoped OUT Area 4 -- the four CLR-call-ABI concerns that touch the
Neo CLR-binding codegen shared by EVERY CLR method call from IL. Two of those
four are now the natural next slice: **(4b)** a CLR **instance** method called on
a value-type `this` (a CLR struct local whose address is passed to a struct
instance method) currently fails opaquely, and **(4a)** the `Unsafe.Unbox<T>`
direct-call mode that eliminates the box / call / write-back round-trip for a
boxed CLR value type. Both are the highest-value, lowest-regression-risk halves
of Area 4: VT-THIS-ADDR (just shipped) established the in-frame value-type
address model that 4b is the direct consumer of, and the F-3 / NEO-BYREF-THIS
follow-up (`new ClrStruct(args)` and byref-`this`-via-callvirt-on-a-CLR-struct)
is exactly the 4b defect class. The other two halves (4c CLR-method `ref`/`out`,
4d CLR-object `stind`/`ldind` via field hash) are independent typed-reference /
field-hash plumbing and are deferred to a follow-up child to keep this diff
reviewable (the 13b lesson: bundling all of Area 4 made the diff unreviewable).

## What Changes

**SCOPING DECISION (load-bearing, ranked 4a/4b/4c/4d by value x low-regression-risk):**

| Sub-area | Value | Regression risk (touches shared codegen) | Rank |
|---|---|---|---|
| **4b value-type-`this` direct-call** | HIGH -- VT-THIS-ADDR's natural consumer; closes F-3 / NEO-BYREF-THIS (pre-existing, fails identically on `f673b9c9`); unblocks `new ClrStruct(args)`, struct instance methods, callvirt-on-CLR-struct | MEDIUM -- the `HasThis` arm of `CLRMethod.Invoke` + the `*Neo` wrapper prologue only; primitives/refs/IL-VT byte-identical | **1 (IN)** |
| **4a `Unsafe.Unbox<T>` direct-call** | HIGH -- eliminates the box/call/write-back round-trip for a method on a boxed CLR VT; the "direct-call mode" the 13b design named but did not build | MEDIUM-LOW -- the generated `*Neo` wrapper instance prologue (`MethodBindingGenerator.cs:256-267`); `GenerateMethodWraperCode_Neo` already does NOT emit `WriteBackInstance`, so this is filling the value-type-`this` TODO + the no-box fast path | **2 (IN)** |
| 4c CLR-method `ref`/`out` | MEDIUM -- typed-reference bridge (8-byte Ref Slot -> CLR `ref T`); standalone autogen branch (currently `// TODO: ByRef ... DEFERRED` at `BindingGeneratorExtensions.cs:176`) | MEDIUM -- a genuinely new typed-reference bridge into the Neo frame (copy-in/copy-out or a pinned handle); no shared fast path | **3 (DEFERRED)** |
| 4d CLR-object `stind`/`ldind` via field hash | LOW-MEDIUM -- a byref to a CLR object field read/written via stind/ldind; needs `Ldflda` field-hash stamping + consumer dispatch to `CLRType.Get/SetFieldValue` | MEDIUM-HIGH -- touches the Step 17 stind/ldind/stobj/ldobj arms that EVERY byref store/load goes through; the field-hash stamping is independent plumbing | **4 (DEFERRED)** |

**Recommendation: this child does the tight cohort {4b, 4a}.** 4c and 4d become
a follow-up child (`neo-step13-area4-refandstind`, noted below). Rationale:

1. 4b and 4a are the SAME generated-prologue concern (the instance-method
   `instance_of_this_method` read in `GenerateMethodWraperCode_Neo`), so they
   share the codegen site, the same runtime `this`-read path, and the same
   adversarial probes. Splitting them would force two passes over the prologue.
2. 4b is VT-THIS-ADDR's natural consumer (the in-frame VT address model just
   landed; a CLR struct instance method on that address is the missing piece).
3. 4c and 4d are independent plumbing (a typed-ref bridge, a field-hash scheme)
   that does NOT fall out of {4a, 4b} and would, if bundled, make the diff
   unreviewable (the explicit 13b lesson).
4. The dump/code reading confirms 4b's defect is a single 4-byte-vs-8-byte
   disagreement at `CLRMethod.cs:351-356` (`HasThis` reads a 4-byte mStack index;
   a byref `this` is an 8-byte Ref Slot from `ldloca`) -- the same defect class
   as F-3. 4a's defect is the `// TODO: ValueType instance in Neo` at
   `MethodBindingGenerator.cs:261`. Both are localized, additive fills.

**IN this pass ({4b, 4a}):**

- **4b -- value-type-`this` direct-call (the runtime `this`-read).** Teach the
  `HasThis` arm of `CLRMethod.Invoke(byte*)` (`CLR/Method/CLRMethod.cs:351-356`,
  the reflection fallback) to recognize a value-type `this`: when the declaring
  type is a CLR value type, the `this` arrives as a frame-native byref (8-byte
  Ref Slot `(-1, frameByteOff)` from `ldloca`, NOT a 4-byte mStack index), and
  the method reads the struct bytes directly via `ReadNeoValueType` (the 13b
  helper) instead of `mStack[thisIdx]`. Eliminate the mis-read at the call
  boundary (closes F-3 / NEO-BYREF-THIS for the `new ClrStruct(args)` /
  byref-`this`-on-CLR-struct shape). Also extend `GenerateMethodWraperCode_Neo`
  (`MethodBindingGenerator.cs:258-262`) to emit the value-type `this` read via
  `ReadNeoValueType` (replacing the `// TODO: ValueType instance in Neo`).
- **4a -- `Unsafe.Unbox<T>` direct-call mode (the generated wrapper prologue).**
  For a CLR struct instance method invoked on a BOXED struct (e.g. a boxed struct
  flowed through `object`), the generated `*Neo` wrapper reads `this` as a boxed
  reference (`ReadNeoReference`), calls the method on the unboxed copy, and --
  for a mutating method -- writes the (possibly-mutated) struct back. This is
  the direct-call mode the 13b design named; it reuses `ReadNeoValueType` /
  `WriteNeoValueType` (no new copy primitive). The Neo wrapper already does NOT
  emit `WriteBackInstance` (the 13b finding), so the write-back is a Neo-only
  flat-bytes write into the dest ref slot, not the Legacy StackObject path.
- **Eliminate `WriteBackInstance` (no-op confirmation).** The 13b design noted
  the Neo wrapper does not emit `WriteBackInstance`; this pass confirms and
  documents that as part of 4a (the value-type-`this` write-back is the
  flat-bytes write, not the Legacy StackObject write-back).
- **Adversarial probes MANDATORY** (Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1
  lessons): a green smoke does NOT prove a codegen gate correct. Probes per
  sub-area: 4b -- a CLR struct instance method called on an in-frame VT `this`
  (mutating + non-mutating), `new ClrStruct(args)` end-to-end, callvirt-on-CLR-
  struct; 4a -- a boxed CLR struct method call (box-then-call-then-unbox), a
  mutating method on a boxed struct (verify the mutation propagates). The K2-FAM
  reproducer (CLR struct local passed by value) is a cross-check (regression).

**DEFERRED (follow-up child `neo-step13-area4-refandstind`):**

- **4c -- CLR-method `ref`/`out` parameters (typed-ref bridge).** A byref param
  is an 8-byte Ref Slot (Step 17); materializing it into a CLR `ref T` on the
  callee side needs a typed-reference bridge (copy-in / call / copy-out, or a
  pinned frame address handed to the CLR). The autogen `ByRef` branch
  (`BindingGeneratorExtensions.cs:173-177`) stays a clearly-tagged NIE.
- **4d -- CLR-object `stind`/`ldind` via field hash.** The Step 17 stind/ldind/
  stobj/ldobj arms dispatch only on frame-native (`objIdx == -1`) and heap-IL
  (`GetNeoILInstance`); a CLR-object target throws or mis-dispatches. Needs
  `Ldflda`-of-CLR-field to stamp a field hash into the Ref Slot offset half and
  the consumer arms to dispatch to `CLRType.GetFieldValue(hash, target)` /
  `SetFieldValue(...)`. Independent plumbing.

**Non-goals (unchanged from 13b):** IL value-type `newobj` (shipped in
VT-THIS-ADDR); delegate `newobj` (Step 19); generic-parameter VT spanning
IL/CLR; async state machines (Step 20 -- exercises this pass's `this`-read but
async itself is Step 20); the F-2 / INLINER-REFONLY-VT inliner gap (separate
optimizer-hardening concern).

## Capabilities

### New Capabilities

(none -- this change extends an existing capability)

### Modified Capabilities

- `neo-boxing`: Step 13b landed the by-value param + return read; the
  value-type instance `this` direct-call (Area 4a/4b) and CLR-method `ref`/`out`
  + CLR-object stind/ldind remained explicitly DEFERRED in the 13b capability
  spec ("Out-of-scope deferrals"). This change delivers the **value-type
  instance `this` direct-call** (4b) + the **`Unsafe.Unbox<T>` direct-call
  mode** (4a); 4c/4d remain deferred (follow-up child). MODIFIES the "Out-of-
  scope deferrals" requirement to move 4a/4b out of the deferred list, and ADDS
  a requirement for value-type instance-`this` reading + the boxed-direct-call
  write-back.

## Impact

- **Code (runtime, all `#if ENABLE_NEO_MODE`, additive / replacing TODO+`NIE`):**
  - `ILRuntime/CLR/Method/CLRMethod.cs` -- `Invoke(byte*)` `HasThis` arm
    (`:351-356`): recognize a value-type `this` (8-byte frame-native Ref Slot vs
    a 4-byte mStack index) and read the struct via `ReadNeoValueType`.
  - `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` --
    `GenerateMethodWraperCode_Neo` (`:258-267`): emit the value-type `this`
    read (replace `// TODO: ValueType instance in Neo`) + the boxed-direct-call
    write-back for 4a.
  - Possibly `ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs` -- a
    new `AppendThisCodeNeo`-style helper for the `this` read (mirrors
    `AppendArgumentCodeNeo`), IF the prologue refactor needs it (apply-phase
    decision; the minimal form inlines into `GenerateMethodWraperCode_Neo`).
  - `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the
    boxed-direct-call write-back site (the `*Neo` wrapper calls
    `WriteNeoValueType`; if the wrapper needs a runtime helper, add it next to
    `WriteNeoValueType` `:225`). Minimal expected.
- **Tests:** extend `TestCases/NeoStep13Test.cs` or `NeoStep13bTest.cs` with
  `NeoStep13*` probes (the `NeoStep` filter catches them; do NOT create
  NeoStep19Test.cs). Canary: the FULL NeoStep smoke (108/108 at HEAD after
  neo-il-exception-throw) -- every CLR call uses this ABI.
- **Non-impact (Legacy neutrality):** Legacy CLR binding (`ExecuteR`, the
  `StackObject*` paths, the Legacy `GenerateMethodWraperCode_Legacy` /
  `AppendArgumentCode` / `WriteBackInstance`, the Legacy `CLRMethod.Invoke` with
  `StackObject*`) is the REFERENCE and is NOT modified. The Neo codegen path
  (`*Neo` methods, `GenerateMethodWraperCode_Neo`, `RegisterCLRMethodRedirectionNeo`)
  is a SEPARATE set of generators invoked only under `#if ENABLE_NEO_MODE`.

## Explicit In / Deferred list

| Item | Status | Rationale |
|------|--------|-----------|
| 4b -- value-type-`this` direct-call (CLRMethod.Invoke `HasThis` reads byref Ref Slot via `ReadNeoValueType`; `GenerateMethodWraperCode_Neo` value-type `this` prologue) | **IN** | VT-THIS-ADDR's natural consumer; closes F-3 / NEO-BYREF-THIS (pre-existing, fails on `f673b9c9`); localized to the `HasThis` arm + the generated prologue; primitives/refs/IL-VT byte-identical. Highest value x lowest risk. |
| 4a -- `Unsafe.Unbox<T>` direct-call mode (boxed-CLR-VT instance method; no-box fast path + boxed-with-write-back) | **IN** | Same codegen site as 4b (the instance prologue); 13b named but did not build it; reuses `ReadNeoValueType`/`WriteNeoValueType`. Eliminates the box/call/write-back round-trip. |
| Eliminate `WriteBackInstance` (no-op for Neo) | **IN** (confirmation only) | 13b already established the Neo wrapper does NOT emit `WriteBackInstance`; this pass documents the value-type-`this` write-back as the flat-bytes write. No code change beyond a comment/spec note. |
| 4c -- CLR-method `ref`/`out` (typed-ref bridge) | **DEFERRED** (follow-up `neo-step13-area4-refandstind`) | Genuinely new typed-reference bridge into the Neo frame (copy-in/copy-out or pinned handle); independent plumbing. Bundling risks the ABI. |
| 4d -- CLR-object `stind`/`ldind` via field hash | **DEFERRED** (follow-up `neo-step13-area4-refandstind`) | Needs `Ldflda` field-hash stamping + Step 17 stind/ldind/stobj/ldobj consumer dispatch (every byref store/load); independent plumbing, higher regression risk. |
