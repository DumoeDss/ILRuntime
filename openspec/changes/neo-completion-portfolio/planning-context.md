# Planning Context — Neo Completion Portfolio

> SEED for the persistent planner. Read THIS FIRST, then the handoff docs it
> points at, then research only what is missing. APPEND durable new findings
> (decisions, discovered constraints) after each propose.

## User intent (verbatim)

"auto-decompose 首先阅读交接文档：.trae\documents\neo-handoff.md，然后规划完成后续所有step
（以及遗漏项），保证所有设计都完整实现。合理划分changes，充分利用我们pipeline架构的
subagents能力。"

User clarifications (2026-07-05):
- **Scope: FULL, including AOT.** Portfolio covers Steps 19-26 AND every open
  deferred item. AOT (Steps 22-26) is in-scope even though it is a pure
  optimization layer and a multi-week sub-project on its own.
- **First child: neo-vt-this-addr** (IL value-type newobj).
- **Autonomy: FULL.** "不用停下来问我，全部由你推进，直到所有任务完成" — drive every
  child through the full pipeline without pausing at gates; commit + push after
  each clean child; keep going until the whole portfolio is done.

## What is already done (DO NOT re-litigate)

Neo Steps 1-18 + derived follow-ups are SHIPPED and ARCHIVED. See
`openspec/changes/archive/2026-07-04-implement-neo-step*/`. The 9 durable
capability specs live in `openspec/specs/`:
`neo-dispatch, neo-value-types, neo-boxing, neo-exceptions, neo-type-checks,
neo-arrays, neo-byref, neo-optimizer, neo-newobj`.

`NeoStep` smoke = 91/91 green at HEAD. Legacy 519-baseline ~518/519 (regression
reference for shared-engine changes).

Resolved deferred items (closed, do not reopen): K1 (FCP ldloca-kill),
K2 (Step 13b unified CLRMethod param layout), D-LDELEMA (Step 17 IL VT array),
Q-NEWOBJ / Q-STRUCT / Q-LONG (non-reproducible on HEAD), D-CHECKEX piece
(CheckExceptionType IL branch).

## The handoff docs (READ BEFORE PROPOSING ANY CHILD)

- `.trae/documents/neo-handoff.md` — environment, build/test commands, current
  state, workflow, codebase gotchas, open follow-ups. **Section 4 (gotchas) and
  Section 5 (deferred items) are load-bearing.**
- `.trae/documents/neo-deferred-items.md` — authoritative current-state tracker
  for every deferred item; per-item detail + suspect code locations.
- `.trae/documents/neo-implementation-steps.md` — the 26-step roadmap + dep graph.
- `.trae/documents/object-model-neo-design.md` — Neo interpreter design (Call
  convention, frame layout, field access, CLR redirection, AOT format §8, roadmap §9).

## Build + test (CRITICAL — sln CANNOT build whole)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # CLI + ILRuntime/TestBase/LitJson, 0 errors
dotnet build TestCases/TestCases.csproj -c Debug                      # -> TestCases/bin/Debug/netstandard2.1/TestCases.dll
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep    # ALWAYS -f net8.0; filter NeoStep
```
- Build CLI with `Debug_Neo`; NEVER build TestCases with `Debug_Neo`.
- `Debug_Neo` prints huge JIT/optimizer output (`OUTPUT_JIT_RESULT`) — normal;
  filter stdout for the pass/fail summary.
- A test taking >10s usually = interpreter infinite loop — kill + investigate.
- For shared-engine/shared-pass changes, also confirm Legacy-neutral: build plain
  `Debug` + `useRegister=true`, run the relevant filter.

## Codebase gotchas that bite (full detail in handoff §4)

- **`OpCodeR` is `[StructLayout(LayoutKind.Explicit)]`.** Register1/DstOffset
  alias @4, Register2/SrcOffset @6, Register3/Operand @8, Operand2 @12, Operand3
  @16, Operand4 @20. `LowerNeoOffsets` OVERWRITES register indices with byte
  offsets — a pass needing a register INDEX must run BEFORE lowering. Reading
  `ip->Register1` at RUNTIME holds a byte offset (post-lowering), not an index.
  **Snapshot `preOp = op` before a lowering case mutates it.**
- **Shared vs Neo-only optimizer passes.** FCP/BCP/copy-prop/RegisterCleanup are
  SHARED (Legacy uses them too) — gate changes `#if ENABLE_NEO_MODE` or confirm
  plain-`Debug` compiles them out. `LowerNeoOffsets`, `addrAlias`/`liveAliasMap`,
  `AllocateNeoCallParamSlot` are Neo-only (file-gated in Optimizer.Neo.cs).
- **Neo frame model.** Frame = `byte* frameBase` + `AutoList mStack` ref region
  (`frameRefBase`). IL VT LOCAL = flat bytes (`slot.Offset..+TotalPrimitiveSize`)
  + ref slots (`mStack[frameRefBase+slot.RefOffset..+TotalReferenceCount]`) —
  same shape as ILTypeInstance (`byte[] Primitives` + `AutoList ManagedObjects`),
  so copy = byte-copy + ref-copy (`CopyFrameToIL`/`CopyILToIL`/`CopyFrameToIL` +
  `Move_Vt`). A CLR VT LOCAL = a boxed object reference (`RefCount=1`,
  `localIsRef=true`), NOT flat bytes. An 8-byte Ref Slot = `(objectIndex:int,
  offset:int)`: `-1` = frame-native (absolute byte offset); `>=0` = mStack object
  (field offset). `addrAlias` (Step 12) folds ldloca;[ldflda;]stfld/ldfld/initobj
  chains to compile-time offsets; Step 17 COEXIST gate (`liveAliasMap` + escape
  consumer-scan) makes a dest real only when its address escapes.
- **Legacy is the REFERENCE, not a target.** `ILIntepreter.Register.cs` `ExecuteR`
  has the mature implementations. Read the Legacy arm for SEMANTICS; never modify
  Legacy to make Neo work.
- **Test harness is NOT xUnit.** A test = `public static` parameterless method.
  Throw-asserting tests are hard (`throw new T()` needs newobj). Use the
  DivideByZero-assertion pattern or a try/catch flag. `[ExpectedException]` does
  not exist. Add `TestCases/NeoStep<N>Test.cs` per step; the `NeoStep` filter
  catches them.
- **Write tool corrupts ~0.5% of CJK on large payloads.** Author files primarily
  ASCII; validate CJK writes with a pure-ASCII PowerShell codepoint check (memory
  `cjk-write-encoding-corruption`).

## Review-loop earned its keep (adversarial non-author review caught real bugs)

- Step 17 B1: addrAlias COEXIST gate had silent corruption on register reuse
  (global never-reset `hasFoldableUse`); 79/0 smoke MISSED it; reviewer reproduced
  with a probe. **Lesson: a green smoke does NOT prove an optimizer gate correct
  — construct adversarial reuse/escape probes.**
- OPT-HARDEN K1: planner's designed fix was a no-op (wrong root cause);
  implementer STOPPED rather than ship a broken fix. **Lesson: probe the IR dump
  before designing an optimizer fix; STOP if the fix doesn't work, don't force it.**

KEEP this rigor. Every child's verify stage must construct adversarial probes
for the specific gate/correctness property, not just re-run the smoke.

## Portfolio plan (16 children, 5 waves, mostly SERIAL — shared files)

Execution policy: serial even for "independent" children, because nearly all
JIT-path children touch `ILIntepreter.Neo.cs` / `JITCompiler.cs` /
`Optimizer.*.cs` (shared files). Parallel requires NO file overlap + Tier A +
no dep edge — rare here. "宁可串行也不能乱并行".

### Wave 1 — foundational correctness fixes (independent roots)
1. **neo-vt-this-addr** [Q-VT-NEWOBJ] — IL value-type newobj. D2 JIT change:
   track a VT `this` (param slot 0) and a VT newobj dest as an in-frame address
   for ALL field access (ctor stfld + caller ldfld), reusing/extending Step 17
   byref/addrAlias. Touches Step 12 VT frame layout + shared field-access
   lowering (every VT instance method). The newobj arm surfaces a Step-18 NIE;
   the LOCAL form (`VT x = new VT()`) compiles to `ldloca;call ctor` and crashes
   opaquely. `ParamInfos[0]` for a VT ctor is sized as the in-frame value
   (`Size=TotalPrimitiveSize, RefCount=TotalReferenceCount`), NOT an 8-byte byref
   (JITCompiler.cs:1332-1344). HIGHEST VALUE — unblocks 13-area4, step17-completion,
   k2fam-bridge.
2. **neo-opt-harden-2** [F-MAJ-1] — `AllocateLocalStackSpaces` slot-reuse/liveness
   with 2+ simultaneous CLR struct locals -> silent wrong result. Pre-existing
   (stash-proven); Step 13b made it reachable. Isolated to AllocateLocalStackSpaces.
   Neo-only/Legacy-neutral.
3. **neo-il-exception-throw** [D-IL-EXCEPTION-THROW] — (a) register a
   `System.Exception` CrossBindingAdaptor (ILType.cs:1418 TypeLoadException
   without it); (b) Throw opcode does `mStack[idx] as Exception` on BOTH engines
   (Neo ~3159, Register ~5310) -> plain IL class NREs; handle IL instances in
   Throw. Enables the positive IL-catch test. Shared-engine (Neo arm + the
   adaptor registration); keep Legacy-neutral.

### Wave 2 — Step 13/17 completions (depend on VT-THIS-ADDR)
4. **neo-step13-area4** [D-13B Area 4] — CLR binding codegen overhaul:
   `Unsafe.Unbox<T>` direct-call + value-type-`this`; eliminate WriteBackInstance
   (no-op for Neo); CLR-method ref/out (typed-ref bridge); CLR-object stind/ldind
   via field hash. Only the `*Neo` variants; Legacy untouched. DEP: neo-vt-this-addr.
5. **neo-step17-completion** [D-CONSTRAINED + more] — `constrained.`-on-VT full
   dispatch (callvirt byref `this`); `Stobj`/`Ldobj` ref-slot loop (primitives
   only now); generic-byref / `fixed` / interface-on-VT-constrained; CLR
   primitive-array `ldelema` (Step-17 NIE). DEP: neo-vt-this-addr.
6. **neo-k2fam-bridge** [K2-FAM] — Move-path scalar->boxed-ref CLR-VT-local bridge
   (CLR struct local sourced from Box/Initobj, passed by value). Flat-bytes/
   return-sourced path works (Step 13b). Needs IL-side ldfld/stfld on CLR struct
   fields for a clean reproducer. DEP: neo-vt-this-addr, neo-step13-area4.

### Wave 3 — roadmap (delegates, async)
7. **neo-step19-delegate** [Step 19] — `ldftn`/`ldvirtftn` (IMethod ref into
   mStack; ldvirtftn resolves via VTable slot) — BOTH currently absent from
   ExecuteNeo (hit the catch-all NIE). DelegateAdapter.InvokeILMethod adapted to
   Neo calling convention (CLR->IL: write CLR args into byte* frame -> ExecuteNeo
   -> read return). DelegateManager binds instance+method; multicast via next-chain.
   `DelegateAdapter.cs`/`DelegateManager.cs` exist (Legacy); CLRRedirectionDelegateNeo
   foundation laid (Step 9). DEP: Steps 8/9/10 (done).
8. **neo-step20-async** [Step 20] — Builder redirect (AsyncValueTaskMethodBuilder<T>/
   AsyncTaskMethodBuilder<T>/AsyncTaskMethodBuilder — all methods Neo Redirection);
   `Start<TSM>` -> sm.MoveNext() via Ref Slot; `AwaitUnsafeOnCompleted` ->
   frame-to-heap copy (`new ILTypeInstance(initializeCLRInstance:false)`) +
   ILAsyncContext; `ILAsyncContext<T>` : IValueTaskSource<T>,IAsyncStateMachine;
   SetResult/SetException sync/async; Task getter. LARGE. DEP: Step 19 + 13 + 17.

### Wave 4 — opportunistic (low-priority, mostly isolated)
9. **neo-array-completion** [D-ARR] — `Stelem_I` interpreter arm (lowered, no arm);
   generic-token `Code.Ldelem`/`Code.Stelem` + native `Code.Ldelem_I`/`Ldelem_U8`
   enumerated by JIT Translate; multi-dim arrays (rank-1 only now).
10. **neo-peephole-isinst** [D-PEEP] — compile-time `box T; isinst U` fusion +
    `PatchKind.IsinstResult` patch-table entry. BLOCKED: neither the fusion pass
    nor `PatchKind` exists — build the patch-infra first. Pure optimization.
11. **neo-opportunistic-cleanup** [N-CGTUN + N-TC2] — Cgt_Un divergence comment
    tighten (symmetric source=sentinel case); Step 14 TC2 tighten (assert exc
    type/identity now that Step 15 isinst landed). Trivial.

### Wave 5 — Neo AOT toolchain (Steps 22-26; pure optimization layer)
12. **neo-step22-generic-template** [Step 22] — `PatchEntry` struct; generic
    method compile -> `templateBody + patches[]`; runtime instantiation
    (`patches.Length==0` -> reuse template; else `CloneAndPatch`); cache in
    `ILMethod.BodyRegister`. Ref-type generic args share one OpCodeR[]; value-type
    args patch frame size. (AOT-specific; JIT instantiates per-occurrence today.)
13. **neo-step23-neoassembly** [Step 23] — `.neo` binary format: header (Magic
    ILRN + Version + Table offsets); String/TypeRef/MethodRef/FieldRef tables;
    TypeDefTable (fields[], VTableTemplate, TotalPrimitiveSize,
    TotalReferenceCount); MethodDefTable (OpCodeR[], StackSlotInfo[],
    ExceptionHandlers, frame metadata); GenericMethodTemplate; BinaryWriter
    serializer + BinaryReader deserializer; roundtrip test. DEP: 22.
14. **neo-step24-ilrt-neoc** [Step 24] — standalone precompile CLI: arg parse
    (input DLL, output .neo, ref assemblies); Cecil load; JITCompiler.Compile()
    per method (Neo); generic templates + patches; collect type metadata; call
    Step 23 serializer; error reporting. DEP: 23.
15. **neo-step25-runtime-loader** [Step 25] — dual-path ILType/ILMethod init
    (Cecil vs .neo tables); load flow (deserialize types -> methods point at
    OpCodeR[]; TypeRef->CLR Type; MethodRef->CLRMethod; VTable bind; generic
    template register; hierarchy build for isinst/castclass). Plan A: redundant
    fields; AOT path skips Cecil. DEP: 23, 24.
16. **neo-step26-perf-validation** [Step 26] — benchmark suite (field/call/VT/
    virtual/array); edge cases (reflection, cross-domain, thread safety); debugger
    adaptation (DebugService reads Neo frame vars via CompiledFrame.LocalInfos;
    breakpoints; single-step); CrossBindingAdapter adaptation; Neo-vs-Legacy perf.
    DEP: 8, 15 (and broadly all).

## Per-child pipeline (auto-decompose childPipeline = small-feature)

propose -> apply -> verify -> review-loop -> ship -> archive, then LEAD
commit + push. Gates are SKIPPED per user (full autonomy). Author != verifier
hard-enforced: reviewer != implementer; fixer of a finding != its author;
re-reviewer != fixer. verify writes `review-report.md`; ship writes
`ship-log.md`; archive syncs deltas into `openspec/specs/<cap>/spec.md` and
moves the change to `archive/`.

## Commit + push convention (handoff §3)

- Stage PRECISELY: the child's source files + test + openspec/ artifacts + the
  deferred-items doc if updated. EXCLUDE .pdb/.gitignore/nuget.config/.claude/
  .vscode/CLAUDE.md churn.
- Message: `Neo <scope>: <topic>` (e.g. `Neo vt-this-addr:`, `Neo step 19:`,
  `Neo opt-harden-2:`, `docs(neo):`). End every commit with:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- `git push origin features/object-model-overhaul` after each clean child.

## Maintain this file

APPEND durable findings after each propose (decisions, discovered constraints,
confirmed root causes). Do NOT append chatter. This is what lets a fresh planner
warm-seed cheaply across sessions.

## Findings -- neo-vt-this-addr (2026-07-05, propose)

**Confirmed root cause (code-grounded).** The VT-newobj blocker is a JIT
type-specialization gap, NOT a frame-layout or call-ABI gap:

- The VT ctor's `this` (callee param slot 0) is ALREADY consistent:
  `BuildInitialRegisterTypes` (`JITCompiler.cs:888-905`) seeds
  `registerTypes[0] = declaringType`, and `AllocateLocalStackSpaces`
  (`JITCompiler.cs:1332-1344`) sizes `ParamInfos[0]` as the in-frame value
  (`Size=TotalPrimitiveSize, RefCount=TotalReferenceCount`). So inside the ctor,
  `TryRewriteFieldAccessForInline` (`JITCompiler.cs:780-830`) already
  recognizes `this` as an in-frame VT and rewrites `this.field=` to `_Inline`
  (which writes the callee frame's slot-0 region).
- The caller side is the gap: there is **NO** `SetRegisterType` for the dest of
  a `Newobj`. So after `newobj VT(...)`, the dest register is UNTYPED, and the
  caller's `ldfld` on the result is NOT recognized as in-frame -> stays as heap
  `Ldfld_*` (calls `GetNeoILInstance`, treats dest as an mStack index). The two
  ends disagree.
- `addrAlias` (`Optimizer.Neo.cs:25-81`) only tracks `ldloca`/`ldflda`-produced
  addresses -- a VT `this` param is NOT in the producer set, so the ctor's
  `this.field` cannot fold to a compile-time offset either (it resolves
  `localInfos[0].Offset` directly, which works ONLY if slot 0 is seeded as the
  frame-native Ref Slot by the runtime).

**The load-bearing fix is one JIT case:** add `case OpCodeREnum.Newobj:` to the
type-specialization pass (the loop calling `TryRewriteFieldAccessForInline`
@~537) that seeds `SetRegisterType(registerTypes, op.Register1, ilVtType)` when
the constructed type is an IL VT. This mirrors the existing `Ldloca`
(@741-747) and `Ldflda` (@761-767) dest-typing rules -- the Newobj dest is the
missing third case. Once typed, the existing discriminator rewrites the
caller's `ldfld`/`stfld` to `_Inline` (resolving against the dest register's
frame byte/ref region). Additive + minimal; no frame-layout / call-ABI / inline
lowering change.

**Second fix: VT `this` addrAlias root.** Seed `addrAlias[0] = {Reg=0,
Offset=0}` (and `liveAliasMap[0]` likewise) when the method `HasThis` and
`declaringType.IsValueType` -- param slot 0 is a pre-existing in-frame address
root for the ctor body. Mirror the `ldloca` rule. (`Optimizer.Neo.cs:25-81` +
`1259-1301`.)

**Runtime (Newobj IL-VT branch).** Replace the Step-18 NIE
(`ILIntepreter.Neo.cs:1654-1701`): zero-init dest region; seed the ctor's slot-0
bytes as a frame-native Ref Slot `(-1, destFrameByteOff)` (+ seed slot-0 ref
slots for VT-with-ref-fields, OR use the copy-back simplification -- pick during
apply via JIT dump); copy ctor args; `InvokeNeoCallTarget(isNewobj:true, ...)`.
No heap `ILTypeInstance`; no mStack `this` push; no post-ctor copy-back.

**Key decisions locked.**
- D1: type the Newobj dest as the VT (the load-bearing JIT change).
- D2: runtime Newobj IL-VT branch (frame-native Ref Slot `this`).
- D3: extend addrAlias producer set to include a VT `this` param root (per-
  method, when `HasThis && declaringType.IsValueType`).
- Heap-alloc + copy-back fallback REJECTED (would mix object models; also
  infeasible without the consistency fix).
- Frame-native Ref Slot is the preferred zero-copy mechanism; copy-back is the
  permitted simplification if the apply dump shows the `_Inline` arm reads
  slot-0 bytes directly. Design does NOT force either.

**Files the implementer will touch (all Neo-only; Legacy `ExecuteR` is the
REFERENCE, NOT modified):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- type-spec pass:
  add `case Newobj:` dest-typing (@~537, near the `TryRewriteFieldAccessForInline`
  call). `AllocateLocalStackSpaces` @1332-1344 + `BuildInitialRegisterTypes`
  @888-905 UNCHANGED (already correct).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- addrAlias
  producer (@25-81) + `liveAliasMap` maintenance (@1259-1301): seed slot-0 root
  for VT instance methods. Newobj dest stamping @1234-1241 + Call/Newobj
  Push-scanning @1089-1252 UNCHANGED (already correct).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Newobj arm
  IL-VT branch @1654-1701: replace NIE with construction.
- Possibly a shared pass (FCP/BCP/copy-prop) guard `#if ENABLE_NEO_MODE` if a
  copy-prop-through-VT-newobj-dest reproducer fails (probe during apply; low
  expected risk).
- `TestCases/NeoStep19Test.cs` (new) -- 8 adversarial probe TCs (multi-field
  ctor; newobj-result-read-after-intervening-heap-writes; nested VT; VT with
  ref fields; VT returned then field-read; local form `VT x = new VT(args)`;
  base-ctor chain; default ctor).

**Regression risk: MEDIUM-HIGH.** Touches the field-access discriminator +
addrAlias machinery (shared by every VT instance method + every VT local
access). Gate: full `NeoStep` smoke (91/91 baseline) + Legacy 518/519 for any
shared-pass gate. Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN K1
lessons: a green smoke does NOT prove an optimizer/lowering gate correct).

**New constraint discovered.** The `addrAlias`/`liveAliasMap` kill logic
(@1297-1300 `GetOpcodeDestRegister`) must NOT fire for param slot 0 -- params
are never redefined by the JIT, but confirm via the ctor JIT dump during apply
(the kill fires on any opcode that writes the register; if the ctor body
contains an instruction whose dest register index collides with slot 0 in the
register file, the root would be wrongly dropped). The addrAlias extension
must seed slot 0 as a root that survives the whole body, not a live-range-
scoped entry.

**Capability spec deltas (3 MODIFIED requirements, all DEFERRED -> delivered):**
- `neo-value-types`: "IL value-type construction via newobj" (was DEFERRED) ->
  delivered + the end-to-end in-frame-address consistency + the addrAlias
  VT-`this`-root extension.
- `neo-byref`: "Byref `this` for value-type constructor invocation" (was
  DEFERRED) -> delivered (the seeding mechanism; byref call-ABI itself
  unchanged).
- `neo-newobj`: "IL value-type newobj" (was DEFERRED) -> delivered (the
  runtime construction contract). Non-goals (delegate / no-binder-CLR-VT-with-
  refs / generic-param VT newobj) stay NIE-tagged.

**Side-benefit watch.** The local form `VT x = new VT(args)` (common C# idiom)
crashes opaquely on HEAD; this change turns it green. Check at verify whether
any existing NeoStep case (or broader suite) was avoiding the local form and
can now use it; note in ship log.

## Findings -- neo-vt-this-addr (apply, 2026-07-05)

**RESOLVED.** Full NeoStep smoke 99/99 green (91 baseline + 8 new TC8-TC15).
Legacy-neutral (all changes `#if ENABLE_NEO_MODE` or Neo-only files; plain
Debug compiles them out). The propose-time hypothesis was PARTLY right but
MISSED the true load-bearing root cause. Three gaps found via JIT-dump probes,
each with its own fix:

1. **(LOAD-BEARING, pre-existing bug) inline-stfld owner-type clobber.**
   `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:537-547`) seeded the dest-temp
   type after EVERY inline rewrite -- including `Stfld_*_Inline`, where
   `Register1` is the OWNING VT, not a destination. This clobbered the owner's
   VT type, so the 2nd+ `this.field=` on the same owner fell back to the heap
   arm (NRE). This bug affected EVERY multi-field VT ctor / in-frame-VT store,
   independent of newobj. Fix: seed dest type ONLY for inline Ldfld (new
   `IsInlineLdfldDestSeedable` helper). This ONE fix turned the common inlined
   `new VT(args)` local form green.

2. **D1 Newobj-dest typing (as proposed).** Needed for the non-inlined path
   (factory `S Make() { return new S(args); }` -- the factory inlines but
   emits a real newobj for the VT ctor; the dest is field-read in the caller).
   `case Newobj:` in the type-spec pass seeds `registerTypes[op.Register1] =
   ilVtType`. Confirmed as proposed.

3. **Newobj dest temp sizing gap.** `GatherValueTypes` did NOT include Newobj,
   so a VT > 8 bytes constructed via newobj got an undersized dest temp and the
   subsequent `Move` truncated to 8 bytes (silent field corruption). Added
   `case Newobj:` to `GatherValueTypes`.

**D2 mechanism = copy-back (NOT frame-native Ref Slot).** The JIT dump
resolved the open question: the `_Inline` stfld arm writes through the owning
slot's frame bytes DIRECTLY (it does NOT dereference a frame-native Ref Slot),
and the caller dest region is a SEPARATE frame buffer from the callee slot-0.
So the zero-copy frame-native Ref Slot does NOT apply. Runtime uses Legacy-style
copy-back: zero-init caller dest; copy dest prim INTO callee slot-0 (pre-call);
copy ctor args; invoke; copy callee slot-0 BACK to caller dest (post-call).

**Ref-half subtlety (earned gotcha).** ExecuteNeo pops the callee's mStack
reservation on return via `RemoveRange` (it shifts/zeroes -- data is GONE, not
just logically popped). So the slot-0 -> caller-dest REF copy-back MUST run
BEFORE the pop. Implemented by adding 4 optional params to `ExecuteNeo`
(`vtNewobjCallerDst`, `vtNewobjCallerDstRefBase`, `vtNewobjCallerPrimSize`,
`vtNewobjCallerRefCount`) and doing the copy-back in the **Ret arm** before its
`RemoveRange` (the final-cleanup pop at the end of ExecuteNeo is TOO LATE --
the Ret arm pops first). The Newobj branch calls `ExecuteNeo` directly (not
`InvokeNeoCallTarget`) to pass these. Defaults preserve existing callers.

**D3 (addrAlias VT-`this` root) NOT NEEDED.** The `ResolveLiveAlias` fallback
already returns `{Reg=reg, Offset=0}` for an unaliased register, which for
param slot 0 resolves to `localInfos[0].Offset` = slot-0 region -- correct for
direct `this.field=`. No addrAlias change required.

**Stfld_Value (whole-VT-into-VT-field store) is a SEPARATE Step 12b deferred
item.** A nested-VT-as-field store (`Outer { Inner inner; }` with `this.inner =
new Inner(...)`) lowers to `stfld.value` which is a Step-6-tagged NIE. TC10
was adapted to construct the nested VT directly. `stfld.value` support is a
follow-up (out of scope for VT-THIS-ADDR).

**Test harness C# 8.0 constraints (earned):** parameterless struct ctors are
C# 9+ (CS8400); structs can't call `: base()` explicitly (CS0522); struct
ctors must assign all fields (CS0171). The TC14 "base-ctor chain" intent is
moot for IL structs (they derive from System.ValueType which has no field-
setting ctor; the C# compiler emits no base call for struct ctors). TC14 was
repurposed to a complex ctor (static helper call + 2 fields).

**No shared-pass change.** All fixes are Neo-only. The 7 pre-existing Legacy
NeoStep-filter failures (NeoStep15_TC6, NeoNaNR8, ...) are unrelated to this
change (present without it; the new TC8-TC15 all pass on Legacy too).
