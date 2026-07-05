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

## Findings -- neo-opt-harden-2 (2026-07-05, propose)

**Prior worded root cause DISPROVEN against current code (Q-NEWOBJ moment).**
The 13b review labeled F-MAJ-1 "`AllocateLocalStackSpaces` slot-reuse /
liveness" and the portfolio seed repeated it. Reading the method at propose
DISPROVES the mechanism as worded: `AllocateLocalStackSpaces`
(`JITCompiler.cs:1394-1587`) allocates a STRICTLY MONOTONIC, non-overlapping
`Offset` / `RefOffset` per surviving local/temp register. The locals loop
(`:1455-1514`) and the temp loop (`:1536-1548`) only ADVANCE the `offset` /
`refOffset` cursors; there is NO liveness analysis and NO slot reuse. Every
surviving register index gets a distinct `[Offset, Offset+Size)` byte region
and a distinct `[RefOffset, RefOffset+RefCount)` ref region. The propose-time
hypothesis ("a slot is reusable only after the LAST use of its current
occupant") describes an allocator that DOES NOT EXIST in this code. A fix that
"invents a liveness-aware slot allocator" would be a no-op at best (mirrors the
K1 original-attempt no-op and the Q-NEWOBJ disproven-collision outcome). This
finding is recorded in the spec delta so a future change does NOT mis-attribute
the F-MAJ-1 fix to slot-reuse logic that does not exist.

**Leading candidate root cause (code-grounded, fits the FULL symptom
signature): D6 return-write / local-slot REPRESENTATION MISMATCH.** The
Step-13b D6 CLR-struct-return branch (`ILIntepreter.Neo.cs:315-330`) writes the
return's FLAT managed bytes (`retSz = GetNeoValueTypeManagedSize(...)`, e.g. 12
for a Vector3) via `WriteNeoValueType` into the caller's dest frame byte slot.
But `AllocateLocalStackSpaces` declares a CLR value-type LOCAL as `Size=4,
RefCount=1, localIsRef=true` (`:1475-1486`) -- a BOXED-OBJECT-REFERENCE slot
(an mStack index), NOT flat bytes. A 12-byte flat write into a 4-byte slot
OVERFLOWS by 8 bytes into the neighbouring local's region. Why this fits better
than the liveness hypothesis:
- **Struct-specific:** only structs with managed size > 4 overflow. The
  two-CLR-int-return control writes 4 bytes into 4-byte slots -> no overflow ->
  passes (matches the reviewer's control).
- **Each value individually wrong when both live:** the 2nd struct's return
  overflow corrupts the 1st struct's slot (its neighbour); both `Sum` reads
  then resolve corrupted mStack indices -> both sub-checks fail independently
  (matches the reviewer's isolation finding exactly).
- **Not an r1<->r2 clobber:** the corrupted bytes are the neighbour's mStack
  INDEX, not the r1/r2 int values (matches the reviewer's "not a cross-clobber"
  finding).
- **Pre-existing:** the boxed-ref-vs-flat-bytes disagreement is as old as the
  Step-12 `Size=4, RefCount=1` CLR-VT-local declaration; pre-13b the D6 path
  NIE'd before reaching the write, so it was unreachable, not silently wrong
  (matches the stash-toggle proof).

**Fallback candidate: `CleanupRegister` compaction** (`Optimizer.RegisterCleanup.cs`,
runs at `JITCompiler.cs:491` BEFORE `AllocateLocalStackSpaces` at `:514`).
LIKELY REFUTED (locals are indexed by `locVarRegStart + i` with `locVarRegStart
= paramCnt` protected from compaction), but dump-confirm at apply.

**Fix LOCKED at apply from the dump, NOT at propose (provisional).** Two
representation-consistent options for candidate (1):
- **Option A (write-side, Neo-only, PREFERRED):** the D6 return-write stores a
  BOXED REFERENCE (mStack index) into the 4-byte ref slot, mirroring the
  reference-type return store (`:332+`) and the newobj arm (`:283-293`). Neo-
  only (the D6 arm is in `ILIntepreter.Neo.cs`); LOWEST Legacy risk. Depends on
  D2 (`ReadNeoValueType`) reading a boxed ref for a Make-sourced local
  consistently (the K2-FAM partial path) -- probe at apply.
- **Option B (declare-side, SHARED):** declare a CLR-VT LOCAL dest as flat
  bytes (`Size = GetNeoValueTypeManagedSize`, `RefCount=0`) in
  `AllocateLocalStackSpaces:1475-1486`. SHARED engine -- MUST be gated
  `#if ENABLE_NEO_MODE` OR confirmed Legacy-neutral (stash-toggle NeoStep
  smoke, K1 pattern). Needs `Move_Vt` / by-value-param copy to treat the local
  as flat bytes.

**If the dump refutes BOTH candidates** (offsets distinct AND write fits yet
symptom persists): F-MAJ-1 -> DEFERRED (Q-STRUCT/Q-LONG/Q-NEWOBJ outcome); ship
NO guessed fix; pin the dump + reproducer. The design includes this fallback
explicitly (Block 5).

**Key decisions locked.**
- D1: the fix is dump-gated; the prior worded root cause is disproven and is
  NOT the fix (no liveness allocator exists).
- D2: leading fix = Option A (write-side boxed-ref), Neo-only; fallback Option
  B (declare-side flat-bytes) gated `#if ENABLE_NEO_MODE` if D2/Move_Vt forces
  it.
- D3: `AllocateLocalStackSpaces` is NOT the fix site for the leading candidate
  (no reuse logic to fix); the spec records this to prevent mis-attribution.
- D4: adversarial probes MANDATORY (8 probes incl. the exact reproducer,
  isolation controls, 3+ locals, live-range-overlap-across-call,
  scoped-reuse-no-frame-bloat, struct-size-4/8 boundary, int-return control,
  single-local regression). Step 17 B1 / OPT-HARDEN K1 lesson binding.

**Files the implementer will touch (dump-locked; Legacy is the REFERENCE):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the D6
  CLR-struct-return branch `:315-330` (Option A fix site; Neo-only).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `AllocateLocalStackSpaces:1475-1486` (Option B fix site; SHARED -- gate
  `#if ENABLE_NEO_MODE`). NOT a liveness-allocator change.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.RegisterCleanup.cs` --
  ONLY if candidate (2) is dump-confirmed (SHARED; gate `#if ENABLE_NEO_MODE`).
- `TestCases/NeoOptHardeningTest.cs` (extend) -- `NeoOptHardTest_Fmaj1_*`
  probes (separate filter, mirrors K1).
- `TestCases/NeoStep13bTest.cs` (extend) -- promote the exact reproducer as
  `NeoStep13bTwoClrStructLocalsRegression` (smoke catches future regressions).

**Regression risk: MEDIUM.** The fix site is a representation-consistency gap
(not the broad field-access discriminator touched by neo-vt-this-addr). Gate:
full `NeoStep` smoke (99/99 baseline) + Legacy 518/519 stash-toggle for any
shared-engine edit. Adversarial probes MANDATORY.

**Baseline note.** NeoStep smoke is now 99/99 at HEAD (after neo-vt-this-addr).
The promoted F-MAJ-1 reproducer FAILS on HEAD and turns green after the fix;
the 8 `NeoOptHardTest_Fmaj1_*` probes run under a separate filter.

## Findings -- neo-opt-harden-2 (apply, 2026-07-05)

**RESOLVED.** F-MAJ-1 FIXED via Option B (declare-side flat-bytes). NeoStep
smoke now 100/100 (99 baseline + promoted `NeoStep13bTwoClrStructLocalsRegression`).
Legacy-neutral (stash-toggle: same 7 pre-existing Legacy NeoStep failures with
and without the fix). All 9 `NeoOptHardTest_Fmaj1_*` probes green.

**Dump-confirmed root cause (candidate 1 CONFIRMED, candidate 2 REFUTED).** A
`frame.LocalInfos` dump for the reproducer on HEAD showed the two CLR struct
locals (`v`, `w`) got DISTINCT, non-overlapping regions (`Offset=0` vs `4`,
distinct `RefOffset`) -- REFUTING candidate (2) `CleanupRegister` compaction.
But each slot was declared `Size=4, RefCount=1, isRef=True` (boxed-ref), while
the D6 return-write wrote `retSz=12` flat bytes -> 8-byte overflow into the
neighbour. CONFIRMING candidate (1).

**The D2/Move_Vt consistency probe INVERTED the propose-time ranking.** Option
A (write-side boxed-ref, was "PREFERRED") would BREAK D2: the by-value param
read (`CLRMethod.Invoke`) + the optimizer's caller-local -> callee-param copy
(`CopyNeoCallArguments`, `primSize = dstInfo.Size`) ALREADY byte-copies N flat
bytes from the caller local's Offset. So the local's RUNTIME representation
was ALWAYS flat bytes (D6 wrote flat bytes, D2 read flat bytes); only the SLOT
DECLARATION lied. The 13b single-local tests passed DESPITE the under-sized
declaration because the overflow hit an empty neighbour. Option B (declare
flat bytes, `Size = GetNeoValueTypeManagedSize, RefCount=0, localIsRef=false`)
mirrors the callee param layout (`AllocateNeoCallParamSlot`) and is the minimal
representation-consistency fix. Gated `#if ENABLE_NEO_MODE` in the SHARED
`AllocateLocalStackSpaces`; Legacy keeps `Size=4, RefCount=1`.

**Key lesson (re-affirms OPT-HARDEN K1 + Q-* closures).** The propose-time
PREFERRED option (A) was wrong; only the apply-time DUMP + D2-consistency probe
locked the right fix (B). The dump-gated discipline is binding: probe BEFORE
fixing, and STOP if the designed fix is wrong. Here the fix differed from the
propose-time ranking but BOTH candidates were testable from the dump -- the
process worked exactly as designed.

**Scoped-reuse probe observation.** `NeoOptHardTest_Fmaj1_ScopedReuseNoFrameBloat`
was DESIGNED as a "PASS throughout" regression guard but FAILS on HEAD: the C#
compiler does NOT narrow struct-local register liveness for `{ }` block scope
(both remain simultaneously-live method locals, distinct IL variable indices).
This is consistent with candidate (1) (a 2-live-struct-locals case). After the
fix it passes. No frame-size regression: the monotonic allocator never reused
slots (propose-time finding holds).

**Out of scope (noted, NOT fixed).** `GatherValueTypes` + the temp-register
sizer (`maxSize` loop at `JITCompiler.cs:1566-1580`) only handle `ILType`, not
CLR structs. A temp register that must hold a CLR struct > 8 bytes (e.g. a
`Box`/`Stobj` intermediate) would be under-sized. The F-MAJ-1 reproducer does
NOT exercise this (the Make() return dest is the LOCAL, not a temp). Separate
pre-existing gap; flag for a future optimizer-hardening step if a reproducer
lands. A CLR struct local WITH a registered ValueTypeBinder (managedCount > 0)
is declared `RefCount=0` here (the reflection-fallback D6 return path NIEs
ref-field structs upstream); the binder path is owned by autogen redirects.

**Build-cache gotcha (earned).** `dotnet build` on ILRuntime/ILRuntimeTestBase
sometimes reports "0 errors" in ~2-3s WITHOUT re-emitting the DLL when the
source change is small (incremental hash hit). To CONFIRM a rebuild took
effect, check the DLL mtime is newer than the source (`stat -c %Y`), and grep
the built DLL in UTF-16 (`strings -e l <dll> | grep <literal>`) for any new
string literal you added -- ASCII `strings` will NOT find .NET UTF-16 string
literals. This bit me during the dump-probe iteration (a stale DLL silently
ran the OLD code).

## Findings -- neo-opt-harden-2 (review-fix, 2026-07-05)

**RESOLVED (review-loop round 1).** The F-MAJ-1 declare-side fix (Option B:
CLR-VT local = flat bytes, RefCount=0) left THREE runtime consumer arms
assuming the OLD boxed-ref representation. The fixer (non-author) dump-gated
each and fixed the three reachable ones; smoke stayed green (NeoOptHard 16/16,
NeoStep 100/100, Legacy byte-identical). A 4th arm of the same defect class
(Unbox_Any) was found via the dump -- the reviewer's sweep table missed it
because the C# `(T)obj` cast compiles to `unbox.any`, not the `castclass` the
reviewer expected.

**The three reachable broken arms (all in `ILIntepreter.Neo.cs`, Neo-only):**
- **M1 Initobj CLR-struct/enum:** wrote a boxed default into
  `mStack[frameRefBase+RefOffset]` -- RefOffset STALE (RefCount=0) -> clobbered
  a neighbour ref slot. Fix: `Unsafe.InitBlock(DstOffset, 0, clrVtSize)`.
- **M2 Box CLR-struct/enum:** read the first 4 flat bytes as an mStack index
  -> OOB. Fix: `ReadNeoValueType(clrType, frameBase, ref off, sz)` (cached
  typed reader -> independent boxed copy).
- **Unbox_Any CLR-struct DEST (the dump-surprise):** wrote a boxed clone into
  `mStack[frameRefBase+dstRefOffset]` (STALE) + a 4-byte index into the flat
  bytes -- the WRITE-side twin of M2. Fix: `WriteNeoValueType(obj, DstOffset,
  unbxSz)`.

**M3 Isinst/Castclass: NOT BROKEN (no fix).** The reviewer INFERRED this from
M2's shape but did not probe it. The dump showed `(T)obj` -> `unbox.any` (not
`castclass`) and `is`/`as` always go through a prior Box, so the isinst/
castclass source is ALWAYS a boxed-ref `object`-typed local, never flat bytes.
The existing boxed-ref path is correct and the flat-bytes-source case is
unreachable. NO speculative flat-bytes branch was added (would be untested dead
code).

**Discriminator insight (durable).** For Box/Unbox_Any NO runtime
discriminator is needed: Box's source is always a value operand (flat bytes
under the new model); Unbox_Any's dest is always a value-typed local (flat
bytes). For Isinst/Castclass the source is always a boxed-ref (the compiler
boxes first). So the per-arm TYPE TOKEN determines the representation
unconditionally -- no per-slot `IsRef`/`RefCount` flag needs to be stamped at
lowering. (This contrasts with a potential `Box` of an already-boxed source,
which is a compiler no-op and never emitted.)

**M1 reproduction subtlety (earned).** `new T()` at declaration order does NOT
reproduce M1 in isolation: the C# compiler emits `ldloca;initobj` BEFORE the
neighbour ref slot is populated, so the clobber is overwritten by the
neighbour's later write (accidental correctness -- exactly as the reviewer
noted). To make M1 OBSERVABLE the probe must RE-init the struct via
`v = default(T)` AFTER the canary neighbour is established, so the stale-
RefOffset write lands on an already-live ref slot.

**CLI filter gotcha (earned).** The ILRuntimeTestCLI name filter is a simple
`Contains` substring (`Program.cs:54`); it does NOT support regex or `|`
alternation. A filter like `"A|B|C"` runs 0 tests silently. Run each probe
name separately, or use a common substring prefix (the `Fmaj1_` prefix works).

**Dump-noise gotcha (earned).** `Debug_Neo`'s `OUTPUT_JIT_RESULT` prints JIT
for EVERY method in the assembly (including async state machines), so
`grep`-ing for a specific opcode in the dump floods. To dump-gate a specific
arm, add a temporary `Console.WriteLine` INSIDE the runtime arm (it fires only
when that arm executes for the probe method), run the single probe, then
remove the diagnostic. This was how M1/M2/Unbox_Any slot shapes were
confirmed and how M3's "isinst didn't fire" was discovered.

**Lesson re-affirmed.** The reviewer's blast-radius sweep is the load-bearing
deliverable but is NOT exhaustive -- it missed Unbox_Any (the inverse of Box).
A dump-gate on the ACTUAL failing opcode (not the opcode the reviewer
hypothesised) is the only way to find the real defect class. The green smoke
hid M1/M2/Unbox_Any because no existing test exercised `default(ClrStruct)` /
`object o = clrStruct` / `(ClrStruct)obj` on a CLR struct local in Neo mode.

## Follow-ups discovered

### `[NEO-BYREF-THIS]` -- `new ClrStruct(args)` byref-`this` ctor reflection gap (from neo-opt-harden-2 re-review)

Surfaced by the neo-opt-harden-2 round-1 completeness sweep. A DIRECT
`new ClrStruct(args)` in interpreted IL (e.g.
`new TestVector3NoBinding(100f,200f,300f)`) fails with
`ArgumentOutOfRangeException` at `CLRMethod.Invoke:353`. **Pre-existing -- NOT
a regression** (fails identically on `f673b9c9`, pre-F-MAJ-1 / pre-round-1).

Key finding: the C# compiler does NOT emit `newobj` for `new ClrStruct(...)`
assigned to a local -- it lowers to `initobj r1; ldloca.s r8, r1; push r8; call
ClrStruct::.ctor(...)`. The struct is constructed IN-PLACE via a byref `this`,
NOT via `InvokeNeoClrMethod(isNewobj:true)`. So the newobj-boxed-ref-write path
is GENUINELY UNREACHABLE for this pattern. The actual failure is `CLRMethod.Invoke`
reading the ctor `this` as a 4-byte mStack index, while the byref `this` is an
8-byte Ref Slot from `ldloca` (`objIdx == -1`).

Same defect class as the byref-`this`-via-callvirt-on-a-CLR-struct gap and the
broader "CLRMethod.Invoke reflection fallback only handles a 4-byte mStack-index
`this`, not a frame-native byref" limitation. It is NOT the F-MAJ-1
boxed-ref-vs-flat-bytes defect class.

**Route:** Step 17 byref-completeness work, or a dedicated `[NEO-BYREF-THIS]`
follow-up. Full detail recorded in
`.trae/documents/neo-deferred-items.md` (F-3 / NEO-BYREF-THIS, §2 master table +
§3 detail).

### `[NEO-IL-EX-FIELDACCESS]` -- reading IL fields/methods off a caught IL exception is broken on Neo (from neo-il-exception-throw apply)

Surfaced by the neo-il-exception-throw apply (OQ1/OQ2). Now that an IL exception
can be THROWN + CAUGHT end-to-end (this change), reading IL-declared fields or
methods off the CAUGHT exception object via the standard cross-binding-adaptor
bridge turns out to be broken on Neo. **Pre-existing gap, NOT introduced** by
neo-il-exception-throw -- the change only registers an adaptor and adds an
unwrap fallback; it does NOT touch callvirt-on-CLR-interface /
`appdomain.Invoke` instance-method / `ILTypeInstance` indexer machinery. The
gap was simply unreachable before (no IL exception could be caught).

Four distinct broken read paths (each blocked by a separate pre-existing Neo
mechanism):
1. `((CrossBindingAdaptorType)e).ILInstance` bridge -> `callvirt` on a CLR
   interface against the `Adapter` receiver -> ExecuteNeo throws
   `InvalidCastException` ("Object does not match target type").
2. `e.GetType()` -> NIE (`callvirt.clr` on `Object.GetType`).
3. `appdomain.Invoke(instanceMethod, e)` -> NRE: the public `Run`/`Invoke`
   re-entry path (`ILIntepreter.cs:87-120`) ignores the `instance` argument
   under `ENABLE_NEO_MODE` (Step-6 entry shim handles only no-arg static
   methods).
4. `ILTypeInstance.this[index]` indexer -> returns `null` under
   `ENABLE_NEO_MODE` (Legacy-only `StackObject[] fields`; Neo uses
   `byte[] Primitives + AutoList`).

Workaround used in the probes: `e is MyEx` (isinst -- the same opcode the catch
matcher uses, known-good on the `Adapter`).

**Route:** Step 13 Area 4 (CLR binding codegen overhaul + cross-binding-adaptor
completion) or a dedicated cross-binding-adaptor follow-up. The fix touches Neo
callvirt-on-CLR-interface + `appdomain.Invoke` instance-method re-entry +
`ILTypeInstance` Neo indexer (independent mechanisms; a single follow-up likely
closes all four for the caught-exception shape). Full detail recorded in
`.trae/documents/neo-deferred-items.md` (F-4 / NEO-IL-EX-FIELDACCESS, §2 master
table + §3 detail).

### `[NEO-BYREF-THIS]` -- RESOLVED (direct-`call` shape) by neo-step13-area4 (2026-07-05)

The direct-`call` shape of this follow-up is now CLOSED by `neo-step13-area4`
(`CopyNeoCallArguments` derefs byref sources at the copy site via the new
`PrimitiveByRefSrc` flag; the callee param region's `this` slot holds flat
bytes; `CopyNeoCallThisBack` propagates mutations). Neo 117/117, NeoOptHard
16/16, Legacy `NeoStep13_` 9/9; stash-toggle 6/9 FAIL-on-HEAD. See the RESOLVED
prepend at the top of the F-3 / NEO-BYREF-THIS §3 entry in
`neo-deferred-items.md`. **Still OPEN:** the `callvirt` / `constrained.callvirt`
shape on a CLR struct override remains a Step 17 D-CONSTRAINED follow-up
(portfolio task #5 `neo-step17-completion`) -- NOT closed by this change, NOT a
regression.

### `[NEO-AUTOGEN-VTTHIS-GAP]` (M1) -- autogen VT-`this` read untested (from neo-step13-area4 review)

All 9 `NeoStep13_*` probes use `TestVector3NoBinding` (no binder) -> they
exercise the **reflection fallback** (`CLRMethod.Invoke(byte*)` HasThis arm)
ONLY. The autogen `GenerateMethodWraperCode_Neo` VT-`this` `ReadNeoValueType`
read (+ its `NeoBindingHasReferenceField` NIE guard) is generated but NOT
exercised by any smoke probe -- it would only fire for a binder-registered
pure-primitive struct's instance method. **Why Minor not Major:** the autogen
code mirrors the tested reflection path's `ReadNeoValueType` read exactly (same
helper, same size source); residual risk is a codegen typo in the emitted
string template. **Route:** opportunistic test-coverage pass -- add an
autogen-bound VT-`this` probe (register a binder for a struct with an instance
method, or add an instance method to a binder-registered struct).

### `[NEO-IL-VT-INSTANCE-COVERAGE]` -- IL value-type instance method calls uncovered by smoke (from neo-step13-area4 review)

IL value-type instance method calls (`ilLocal.VTMethod()`, which lowers
identically to `ldloca; call`) are uncovered by the smoke. The reviewer
confirmed this shape works on BOTH engines via a temporary probe
(`NeoStep12bReviewIlVtInstanceMethod`, an IL struct `Sum()` instance method),
then removed it (Neo-specific NIE assertions fail on Legacy; accept-both is too
weak; the existing smoke has ZERO IL-VT-instance-method coverage -- the
NeoStep12/12b structs are field-only). The `dstIsVtThisSlot` flag DOES fire for
IL-VT instance calls and is empirically correct, but was previously unverified.
**Route:** opportunistic test-coverage pass -- add a keeper IL-VT-instance-
method probe so this path stays green-guarded.

### `[NEO-CONSTRAINED-NIE-TEXT]` (T1) -- stale Constrained NIE string (from neo-step13-area4 review)

The `Constrained` NIE string at `ILIntepreter.Neo.cs:~3141` still says "callvirt
byref-this dispatch lands in Step 13b / a follow-up", but neo-step13-area4
reclassified callvirt-on-CLR-struct as Step 17 (D-CONSTRAINED), explicitly NOT
4b. The message is stale/misleading. **Route:** one-line text fix to "Step 17
D-CONSTRAINED follow-up (constrained.callvirt on a value type)", anytime.

## Findings -- neo-il-exception-throw (2026-07-05, propose)

Closes **D-IL-EXCEPTION-THROW** (the second half of the exception follow-up
sequence; CATCH-COMPLETE closed the `CheckExceptionType` matcher, which was
necessary-but-not-sufficient). Two independent gaps, both verified against
current code:

- **(a) Load-time TypeLoadException.** `ILType.cs:1412-1418` throws
  `TypeLoadException("Cannot find Adaptor for:System.Exception")` for an IL
  `class X : System.Exception` because no `System.Exception`
  `CrossBindingAdaptor` is registered. Only `AttributeAdapter` ships as a
  built-in (`AppDomain.cs:231`); the test harness
  (`ILRuntimeTestBase/Adapters/helper.cs:22-29`) registers several others but
  none for `System.Exception`.
- **(b) Run-time Throw NRE.** `Throw` does `mStack[idx] as Exception` on BOTH
  engines (Neo `GetNeoException` at `ILIntepreter.Neo.cs:3204-3212`, Legacy arm
  at `ILIntepreter.Register.cs:5307-5312`). For an IL exception the slot holds
  an `ILTypeInstance`, NOT an `Exception` -- `as Exception` is null -> NRE.
  The CLR `Exception` lives at `ILTypeInstance.CLRInstance` (the adaptor's
  `Adapter`), established in the `ILTypeInstance` ctor (`:352-361` via
  `FirstCLRBaseType.CreateCLRInstance`).

**Decision LOCKED: SHARED-engine fix, NOT Neo-only (mirrors CATCH-COMPLETE).**
Both the adaptor registration (`AppDomain`, engine-agnostic) and the Throw
`as Exception` bug exist IDENTICALLY in `ExecuteNeo` and `ExecuteR`. Legacy has
the SAME bug -- it has NEVER thrown an IL exception successfully. So fixing
both arms is a genuine Legacy improvement, not a Neo workaround. NOT gated
`#if ENABLE_NEO_MODE`. Gate: new positive IL-catch tests pass on BOTH engines
(Neo `Debug_Neo` AND plain `Debug` + `useRegister=true`) + 518/519 Legacy
baseline holds. The CATCH-COMPLETE archive set this exact precedent under the
same `neo-exceptions` capability.

**Adaptor registration site: BUILT-IN, not test-harness.** The ExceptionAdaptor
goes in the `AppDomain` ctor (`AppDomain.cs:231`, next to `AttributeAdapter`),
NOT in `ILRuntimeTestBase/Adapters/helper.cs`. Rationale: the Throw-unwrap
requires the IL exception's `CLRInstance` to BE a CLR `Exception`, which only
happens if the adaptor created it -- so the adaptor is a runtime prerequisite,
not a test convenience. Any consumer (Unity host, AOT loader) gets IL
exceptions working without registering an adaptor. The `.neo` AOT runtime
loader (Step 25) will need to register the same adaptor -- flagged for that
child. New file: `ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs` (nested
`Adapter : System.Exception, CrossBindingAdaptorType`, mirrors
`AttributeAdapter`; forwards `ToString()`; `Message` only if OQ2 decides the
`ILInstance`-bridge read is awkward).

**Throw-unwrap mechanism: reuse `ILTypeInstance.CLRInstance`.** When the throw
operand is not directly an `Exception`, fall back to
`((ILTypeInstance)o).CLRInstance as Exception`; if still null, NRE (throwing a
non-exception object is invalid -- unreachable from C#, which requires the
throw operand to be `Exception`-typed, so this is a defensive guard only).
This is the SAME bridge ILRuntime already uses for CLR-method dispatch on an IL
instance (`ILIntepreter.cs:2936`, `Register.cs:3557`, `Extensions.cs:311`,
`AppDomain.cs:1450`). NOT a new mechanism.

**Catch-slot representation subtlety (OQ1, resolve at apply).** The Throw arm
throws the CLR `Adapter` (an `Exception`), so the catch slot stores the
`Adapter` (CLR view), NOT the `ILTypeInstance`. Implications:
- `catch (System.Exception e)` -> CLRType arm, `IsAssignableFrom(typeof(Adapter))`
  true; `e.Message` reads via the forwarded member.
- `catch (MyEx e)` (IL catch type) -> `CheckExceptionType` IL branch; the
  thrown `Adapter` is NOT an `ILTypeInstance`, so the branch's
  `exception as ILTypeInstance` is null and it falls to the
  `TypeForCLR.IsAssignableFrom` fallback. The IL catch type's `TypeForCLR` is
  the adaptor's `Adapter` type, so the match succeeds.
- Reading an IL-declared field off the caught `e` requires
  `((CrossBindingAdaptorType)e).ILInstance` (the standard cross-domain bridge).
  Probe 7 (message field) uses this bridge OR a forwarded `Message` (OQ2).
Confirm at apply via a temp `Console.WriteLine` in the catch arm.

**Files the implementer will touch (shared-engine; Legacy Throw is the SAME
bug, fixed identically -- NOT a Neo workaround):**
- `ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs` (new) -- the adaptor.
- `ILRuntime/Runtime/Enviorment/AppDomain.cs:231` -- register it (built-in).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:3204-3212` --
  `GetNeoException` unwrap fallback.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs:5307-3312`
  -- Legacy `Throw` arm, IDENTICAL unwrap (NOT Neo-gated).
- `TestCases/NeoStep14Test.cs` (extend) -- 8 adversarial probes
  (`NeoStep14_ILEx_*`); the `NeoStep` filter catches them.

**Regression risk: MEDIUM.** Throw is on the rare throw path (low hot-path
risk); the unwrap is a strict generalization (`o as Exception` first, IL
fallback unreachable for any existing CLR-Exception operand -> byte-identical).
The bigger surface is the AppDomain ctor (every AppDomain gains the adaptor)
+ the shared Throw arm. Adversarial probes MANDATORY (8 probes incl. catch-by-
base, catch-by-exact, derived-before-base ordering, rethrow, cross-frame,
message-field, mixed-with-CLR; Step 17 B1 / OPT-HARDEN K1 lesson binding). The
biggest design risk is the catch-slot representation (OQ1) -- probes must read
IL fields via the `ILInstance` bridge, not directly off the caught `e`.

**Baseline note.** NeoStep smoke is 100/100 at HEAD (after neo-opt-harden-2).
The 8 new probes FAIL on HEAD (NRE on throw) and turn green after the fix --
proves load-bearing (mirrors the F-MAJ-1 / Q-VT-NEWOBJ stash-toggle proof).

**Side-benefit watch.** Handoff §4 currently says `throw new ILExceptionType()`
is NOT green-testable (needs the Exception-adaptor). After this change it IS.
Check at verify whether any existing NeoStep case (or broader suite) was
avoiding IL-typed throws and can now use them; note in ship log. Also: the
CATCH-COMPLETE `CheckExceptionType` IL branch becomes reachable end-to-end
for the first time (probe 3.1 exercises it) -- it was dead code before this
change since no IL exception could be thrown.


## Findings -- neo-il-exception-throw (apply, 2026-07-05)

**RESOLVED.** D-IL-EXCEPTION-THROW closed. Both gaps fixed (shared-engine):
(a) load-time TypeLoadException via a built-in `ExceptionAdaptor`
(`Adapter : System.Exception, CrossBindingAdaptorType`, mirrors `AttributeAdapter`
precisely, forwards `ToString()` only); (b) run-time Throw NRE via an
`ILTypeInstance.CLRInstance as Exception` fallback in BOTH `GetNeoException`
(Neo) and the `Throw` arm (Legacy), NOT `#if ENABLE_NEO_MODE`-gated. NeoStep
smoke 108/108 (100 baseline + 8 new `NeoStep14_ILEx_*`). All 8 probes pass on
BOTH engines. Stash-toggle: with the fix stashed, the test session CRASHES at
load with the exact `TypeLoadException: Cannot find Adaptor for:System.Exception`
at `ILType.cs:1418` (gap a is load-bearing). Legacy-neutral confirmed: the 9
full-suite Legacy failures are all PRE-EXISTING (NeoOptHardening K1,
NeoStep13 ClrStruct, NeoStep14 TC1/TC5/TC8, NeoStep15 TC6, NeoStep6 NeoNaNR8);
the 3 NeoStep14 TC failures reproduce with the ORIGINAL Throw arm (reverted
temporarily), so the Throw-unwrap is exonerated -- it is byte-identical for CLR
Exception operands (`o as Exception` succeeds first, IL fallback unreachable).

**OQ1 RESOLVED: the Neo catch slot holds the CLR `Adapter` (NOT the ILTypeInstance).**
Confirmed via a temp `Console.WriteLine` in the catch-handler slot-store
(`ILIntepreter.Neo.cs:3105`): `ex.GetType == ExceptionAdaptor+Adapter`. The
`CheckExceptionType` IL branch's `TypeForCLR.IsAssignableFrom` fallback handles
the Adapter-for-an-IL-catch-type match (the IL catch type's `TypeForCLR` IS the
Adapter type). Declaring `catch (MyEx e)` works -- `e` holds the Adapter and is
usable opaquely. The CATCH-COMPLETE IL branch is now reachable end-to-end for
the first time (was dead code -- no IL exception could be thrown before).

**OQ2 RESOLVED: minimal adaptor (forward `ToString()` only, NOT `Message`).**
Two paths block IL-field/Message reads off a caught IL exception on Neo:
1. `appdomain.Invoke(getMessage, instance)` re-entry does NOT push `this` for
   instance methods under `ENABLE_NEO_MODE` (`ILIntepreter.cs:87-120` Step-6
   entry shim handles only no-arg static methods) -> IL `get_Message` override
   NREs.
2. `ILTypeInstance.this[index]` indexer returns `null` under `ENABLE_NEO_MODE`
   (Legacy-only `StackObject[] fields`; Neo uses `byte[] Primitives+AutoList`).
3. The `((CrossBindingAdaptorType)e).ILInstance` bridge requires callvirt on a
   CLR interface against the Adapter receiver -> ExecuteNeo throws
   `InvalidCastException` "Object does not match target type". `e.GetType()`
   also NIEs (`callvirt.clr Object.GetType`).
Probe 3.7 asserts via `e is MyEx` (isinst -- the same opcode the catch matcher
uses, known-good on the Adapter). Reading IL fields off a caught IL exception
is a FOLLOW-UP (depends on Neo callvirt-on-CLR-interface / appdomain.Invoke
instance-method / Neo indexer support). The `MyEx` class retains its `Msg` field
+ `Message` override for the throw side.

**Stale-DLL gotcha (re-affirms opt-harden-2 finding).** `dotnet build
TestCases` reported "0 errors" WITHOUT re-emitting the DLL on a small source
edit (incremental hash hit). A probe change appeared not to take effect until
`--no-incremental` forced a rebuild. ALWAYS use `--no-incremental` for the
TestCases build after editing test source, and verify DLL mtime > source mtime.
This caused a confusing false-failure iteration during probe 7 development.

**Files touched (all confirmed, working tree UNCOMMITTED):**
- `ILRuntime/Runtime/Adapters/ExceptionAdaptor.cs` (NEW)
- `ILRuntime/Runtime/Enviorment/AppDomain.cs:231` (register built-in)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:3204`
  (`GetNeoException` unwrap)
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Register.cs:5307`
  (Legacy `Throw` arm, IDENTICAL unwrap, NOT Neo-gated)
- `TestCases/NeoStep14Test.cs` (8 `NeoStep14_ILEx_*` probes + `MyEx`/`DerivedEx`)

**Did NOT git commit/push** (per process discipline; LEAD commits after review).

## Findings -- neo-step13-area4 (2026-07-05, propose)

**Scoping decision: {4b value-type-`this`, 4a `Unsafe.Unbox<T>` direct-call}
IN; {4c ref/out, 4d CLR-object stind/ldind via field hash} DEFERRED to a
follow-up child `neo-step13-area4-refandstind`.** Ranked 4a/4b/4c/4d by value x
low-regression-risk. 4b + 4a are the SAME generated-prologue concern (the
instance-method `instance_of_this_method` read in `GenerateMethodWraperCode_Neo`
+ the `HasThis` arm of `CLRMethod.Invoke`), so they share the codegen site, the
same runtime `this`-read path, and the same adversarial probes -- splitting them
would force two passes over the prologue. 4b is VT-THIS-ADDR's natural consumer
(the in-frame VT address model just landed; a CLR struct instance method on that
address is the missing piece). 4c and 4d are independent plumbing (a typed-ref
bridge; a field-hash scheme) that does NOT fall out of {4a, 4b} and would, if
bundled, make the diff unreviewable (the explicit 13b lesson).

**The current `*Neo` codegen shape (code-grounded at HEAD after neo-il-
exception-throw).** A CLR method call from IL flows through TWO readers that
MUST agree byte-for-byte on the callee param region:
1. The reflection fallback `CLRMethod.Invoke(byte*)` (`CLRMethod.cs:332`),
   invoked from `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:263`) when no
   `RedirectionNeo` autogen delegate is registered.
2. The autogen redirect delegate `*_Neo` (`MethodBindingGenerator.cs:249`),
   whose body is emitted by `GenerateMethodWraperCode_Neo` (prologue reads
   `this` + each param; epilogue writes the return via `GetReturnValueCodeNeo`).

Both walk a contiguous callee "param region" built by the optimizer
(`AllocateNeoCallParamSlot`, `Optimizer.Neo.cs:1319`) with a single `__curPrim`
cursor. The `this` (slot 0 for `HasThis`) precedes the params. Step 13b unified
the PARAMETER layout (CLR struct by-value param + return via
`ReadNeoValueType`/`WriteNeoValueType`); the `this` slot was left as a 4-byte
mStack-index read for reference types and a `// TODO: ValueType instance in Neo`
(`MethodBindingGenerator.cs:258-262`) for value types.

**The value-type-`this` mechanism (the F-3 / NEO-BYREF-THIS defect class).**
The `HasThis` arm of `CLRMethod.Invoke` (`CLRMethod.cs:351-356`) reads `this`
UNCONDITIONALLY as a 4-byte mStack index:
`int thisIdx = *(int*)(targetBase + curPrim); instance = mStack[thisIdx]; curPrim += 4;`
For a CLR struct instance method, the C# compiler does NOT emit `newobj` -- it
lowers `local.method(...)` to `ldloca local; call method(...)`, so `this`
arrives as a frame-native byref = an 8-byte Ref Slot `(-1, frameByteOff)` from
`ldloca`. Reading the first 4 bytes as an mStack index yields garbage /
`ArgumentOutOfRangeException` (the F-3 reproducer). Pre-existing (fails
identically on `f673b9c9`). SAME defect class as `new ClrStruct(args)` (lowers
to `initobj; ldloca; call ctor`) and callvirt-on-a-CLR-struct.

**The discriminator (D1, code-grounded).** The `HasThis` arm decides how to
read `this` by `DeclearingType.IsValueType`:
- Reference type (or boxed VT used as ref): `this` = 4-byte mStack index (the
  existing `ReadNeoReference` / `mStack[thisIdx]`). UNCHANGED.
- Value type, byref (4b in-frame direct-call): `this` = 8-byte frame-native
  Ref Slot `(-1, frameByteOff)`. Read flat bytes via `ReadNeoValueType` at
  `frameBase + frameByteOff`. (DUMP-GATED: the `this`-slot WIDTH laid out by
  `AllocateNeoCallParamSlot` may be the 8-byte Ref Slot OR flat bytes --
  resolve at apply via a JIT dump of `local.Method()`.)
- Value type, boxed (4a): `this` = 4-byte mStack index pointing at a BOXED
  struct. Unbox to a local T, call, write back (re-box) for a mutating method.

This mirrors the 13b M2/M3 discriminator insight: the per-arm TYPE TOKEN
determines the representation unconditionally (no per-slot flag needed).

**`WriteBackInstance` is a no-op for Neo (13b finding confirmed).**
`GenerateMethodWraperCode_Neo` does NOT emit `WriteBackInstance` (the Legacy
StackObject path). The 4a value-type-`this` write-back is a flat-bytes RE-BOX
into `mStack[__thisBoxIdx]` (D4, conservative -- propagates the mutation; the
reflection fallback inherits CLR `MethodInfo.Invoke` "boxed struct call drops
the mutation" semantics for free).

**The load-bearing dump-confirm point.** The call-lowering
(`Optimizer.Neo.cs:1166-1186`) sizes the `this` slot via
`AllocateNeoCallParamSlot(DeclearingType)`. For a CLR struct `this`, that
helper sizes flat bytes (`Size = GetNeoValueTypeManagedSize`) for a by-value
param, BUT the `this` for a byref call may be the 8-byte Ref Slot. The apply
phase MUST JIT-dump `local.Method()` to determine which, and D2/D3 read
accordingly. This is the one untested corner of the 13b unified layout (13b
tested by-value PARAMS, not the `this` slot).

**Files the implementer will touch (all Neo-only; Legacy is the REFERENCE):**
- `ILRuntime/CLR/Method/CLRMethod.cs` -- `Invoke(byte*)` `HasThis` arm
  (`:351-356`): value-type `this` discriminator (D2).
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` --
  `GenerateMethodWraperCode_Neo` (`:258-267`): value-type `this` prologue (D3) +
  boxed-write-back epilogue (D4). Replace `// TODO: ValueType instance in Neo`.
- Possibly `ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs` -- an
  `AppendThisCodeNeo` helper IF the prologue refactor needs it (apply-phase
  decision; minimal form inlines).
- `TestCases/NeoStep13bTest.cs` (extend) -- 8 `NeoStep13_*` probes (the `NeoStep`
  filter catches them; do NOT create NeoStep19Test.cs).

**Regression risk: MEDIUM.** The discriminator keys on
`DeclearingType.IsValueType`, which is FALSE for every reference-type `this`,
so the reference-type path is byte-identical. Gate: full `NeoStep` smoke
(108/108 baseline) + Legacy 518/519 for any shared-engine edit (all changes
Neo-only). Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1
lessons). The biggest design risk is the `this`-slot-width dump gate (D2/D3) --
probe BEFORE fixing.

**Follow-up child created: `neo-step13-area4-refandstind`** (4c CLR-method
ref/out typed-ref bridge + 4d CLR-object stind/ldind via field hash). Both are
independent plumbing that does not fall out of {4a, 4b}. Add to the portfolio
Wave 2 (after this child).

**Spec validation gotcha (earned).** The openspec validator requires the
requirement's DESCRIPTION (not just the title) to contain SHALL/MUST, AND
appears sensitive to `--` (en-dash separator) in the requirement TITLE when
combined with backticks. First validation failed with "must contain SHALL or
MUST" despite SHALL being present in the body -- resolved by (a) leading the
description with an explicit SHALL sentence and (b) removing the
`(call ABI -- Area 4b)` parenthetical from the title (kept `(Area 4b)`).
Re-validate after every spec edit.


## Findings -- neo-step13-area4 (apply, 2026-07-05)

**RESOLVED.** 4b (value-type-`this` direct-call) + 4a (Unsafe.Unbox direct-call
mode) shipped. F-3 / NEO-BYREF-THIS CLOSED for the direct-`call` shape. Neo smoke
117/117 (108 baseline + 9 new probes); NeoOptHardening 16/16; Legacy-neutral
(NeoStep13_ probes 9/9 on plain Debug; the 7 pre-existing Legacy NeoStep failures
reproduce with the fixes stashed -- NOT caused by this change).

**Dump-confirmed `this`-slot representation (the load-bearing finding).** The C#
compiler lowers `local.VTInstanceMethod()` and `new VT(args)` to `ldloca; call`.
The `this` SOURCE register holds a frame-native byref = an 8-byte Ref Slot
`(-1, structFrameOffset)` produced by `ldloca`. The call-lowering sizes the VT
instance `this` DEST slot via `AllocateNeoCallParamSlot(DeclearingType)` -> the
`IsValueType` branch -> flat bytes (`GetNeoValueTypeManagedSize`, e.g. 12). So:
SOURCE = 8-byte byref; DEST = struct's flat-byte width. The pre-fix copy read
`dstInfo.Size` (12) bytes from the 8-byte byref temp -> garbage (F-3). This is
neither the "8-byte Ref Slot dest" nor a clean "flat-bytes dest with flat-bytes
source" -- it's a byref SOURCE flowing into a flat-bytes DEST. The design's open
question ("is the `this` slot the 8-byte Ref Slot or flat bytes?") was itself
under-specified: the DEST is flat bytes; the SOURCE is a byref.

**The fix DEVIATED from the design's literal D2/D3 (byref-through + reader
deref).** The design assumed the readers dereference the byref, but BOTH readers
lack the real caller `frameBase`: the autogen delegate `CLRRedirectionDelegateNeo`
is invoked with `targetBase` as `frameBase` (NOT the caller frame); the reflection
`Invoke(byte*)` only gets `targetBase`. Threading the real `frameBase` through the
delegate signature would break the checked-in static bindings. The fix
DEREFERENCES THE BYREF AT THE COPY SITE instead:
1. `NeoCallParamMap.PrimitiveByRefSrc` (new flag, JITCompiler.cs).
2. Call-lowering marks the VT instance `this` slot's source as byref (Optimizer.Neo.cs).
3. `CopyNeoCallArguments` derefs a byref source: reads the 8-byte Ref Slot, copies
   `PrimitiveSize[i]` bytes from `frameBase + offset` into the dest slot. The
   callee param region's `this` slot now holds FLAT BYTES.
4. Both readers (`CLRMethod.Invoke` HasThis + the autogen wrapper) read flat bytes
   via `ReadNeoValueType` exactly like a by-value VT param -- no frameBase, no
   discriminator.
5. For ctor / mutating methods, the reflection reader writes `instance` back to
   the param region (`WriteNeoValueType`), and a new `CopyNeoCallThisBack`
   post-call reverse copy (Neo Call arm) propagates the bytes to the caller's
   in-frame local -- CLR `ref this` struct semantics (verified: `ConstructorInfo.
   Invoke(box, args)` / `MethodInfo.Invoke(box, args)` mutate the boxed struct in
   place, NOT a copy).

**D4 boxed-re-box (4a) NOT emitted in the autogen wrapper.** The dump-gate
confirmed NO direct-`call` path produces a boxed `this` (`objectIndex >= 0`); a
direct `call` always uses `ldloca` (frame-native byref). A boxed `this` is ONLY
reachable via `constrained.callvirt` (the Step 17 `Constrained` NIE today). So
the D4 boxed-re-box discriminator would be dead code in the current scope. The 4a
write-back IS implemented for the reflection path via `CopyNeoCallThisBack`. The
autogen 4a boxed-re-box lands with the Step 17 `Constrained` completion child.

**Risk-3 callvirt outcome: FOLLOW-UP (not a regression).** `v.ToString()` on a
struct override compiles to `constrained.callvirt` -> the Step 17 `Constrained`
NIE, NOT the 4b direct-call path. So callvirt-on-CLR-struct is a Step 17 (D-
CONSTRAINED) follow-up. 4b owns the direct `call` lowering of a struct instance
method (probes 5.1/5.3 green). A standalone callvirt probe was REMOVED (Neo-
specific NIE assertion fails on Legacy; accept-both is too weak).

**WriteBackInstance no-op confirmed.** The Neo generator does NOT emit
`WriteBackInstance` (only `GenerateMethodWraperCode_Legacy` at `:780/:786` does).
The Neo value-type-`this` write-back is the post-call `CopyNeoCallThisBack` reverse
copy (reflection) / boxed-re-box (autogen, deferred to Step 17).

**CLI host-DLL stale-copy gotcha (earned, re-affirms opt-harden-2).** When a new
CLR struct is added to ILRuntimeTestBase, the ILRuntimeTestCLI's OWN copy of
ILRuntimeTestBase.dll (in `ILRuntimeTestCLI/bin/Debug_Neo/net8.0/`) can be a STALE
cached copy without the new type. The runtime resolves CLR types via
`System.AppDomain.CurrentDomain.GetAssemblies()`, which finds the host's stale
copy -> `KeyNotFoundException: Cannot find Type` for the new struct. FIX: rebuild
the CLI with `--no-incremental` after adding a host type, and verify
`grep -c NewType ILRuntimeTestCLI/bin/Debug_Neo/net8.0/ILRuntimeTestBase.dll` >= 1.
(The TestCases/bin copy is NOT the one loaded; the host's own copy is.)

**Files touched (all confirmed, working tree UNCOMMITTED; all runtime changes
Neo-only / `#if ENABLE_NEO_MODE`-gated files; Legacy byte-identical):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- `PrimitiveByRefSrc`.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- call-lowering flag.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- deref copy +
  post-call reverse copy + Call-arm invocation.
- `ILRuntime/CLR/Method/CLRMethod.cs` -- HasThis VT read + post-invoke write-back.
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` -- autogen VT `this`.
- `ILRuntimeTestBase/TestFramework/TestVector3.cs` -- host instance methods + ref-
  field struct.
- `TestCases/NeoStep13bTest.cs` -- 9 adversarial probes.

**Did NOT git commit/push** (per process discipline; LEAD commits after review).
**Did NOT update neo-deferred-items.md / neo-handoff.md** (the shipper does that
at archive, per implementer process discipline).
