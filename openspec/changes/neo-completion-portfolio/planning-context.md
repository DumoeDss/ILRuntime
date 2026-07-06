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

**RESOLVED 2026-07-05 (neo-step17-completion):** the stale `Constrained` NIE
text was replaced with an accurate description of the new Constrained dispatch
arm (the arm now OWNS the dispatch instead of throwing). T1 closed.

### `[NEO-VT-FLDADDR]` / F-6 -- ldflda-on-in-frame-VT (from neo-step17-completion apply)

Surfaced by the neo-step17-completion apply phase (the IL-struct `ToString`
override probe). The `Ldflda` arm reads `*(frameBase + operandSlotOff)` as an
mStack objIdx; an in-frame VT operand slot holds flat bytes -> garbage (the
first field's value, read as an index). There is NO `Ldflda_Inline`. So `ldflda
this.field` on an in-frame VT is broken -- any IL-struct method that takes a
field address (`field.ToString()`, `ref field`, `fixed`) fails on Neo REGARDLESS
of constrained. **PRE-EXISTING gap, NOT introduced** by neo-step17-completion --
the Ldflda opcode handler is untouched by that change and reproduces on HEAD for
any IL-struct method taking a field address. The constrained DISPATCH itself is
independently validated (the IL-VT interface direct-call probe uses
`Ldfld_I4_Inline`, not `ldflda`).

**Route:** new follow-up child `neo-vt-ldflda-inline` (portfolio task #19) --
add a `Ldflda_Inline` / extend the Ldflda arm to recognise an in-frame-VT
operand via the type-spec seed (mirror the `Ldloca` / `Ldflda` dest-typing
rules from `neo-vt-this-addr`). Full detail recorded in
`.trae/documents/neo-deferred-items.md` (F-6 / NEO-VT-FLDADDR, §2 master table +
§3 detail).

### `[NEO-DELEGATE-REFOUT]` / F-7 -- delegate ref/out param marshaling in NeoInvoke (from neo-step19-delegate)

Surfaced by Step 19 (neo-step19-delegate). `DelegateAdapter.NeoInvokeSub`
(the CLR -> IL callback path, e.g. `List.ForEach(ilAction)`) writes the CLR
args into the callee param region via `WriteNeoCallSlot`, which handles
primitives / reference args / CLR value types but NOT a **byref-typed delegate
param** (a `ref T` / `out T` parameter on an `Action<>`/`Func<>` Invoke). The
return-side `WriteNeoDelegateInvokeReturn` has the symmetric gap for a
`ref`/`out` return. A byref arg is an 8-byte Ref Slot `(objectIndex, offset)`;
`NeoInvokeSub` does not consult the byref-deref machinery that handles a
byref `this` for a direct `call` (`CopyNeoCallArguments`'s
`PrimitiveByRefSrc` flag, neo-step13-area4).

**Pre-existing / latent, NOT a Step 19 regression** -- the byref-on-delegate
shape has never worked on Neo (delegates did not exist on Neo before Step 19).
Step 19 probe 8 deliberately uses a plain `int` param to exercise the
IL-delegate construct + Invoke routing (the load-bearing assertion for the
`NeoInvokeSub` path) and is green on both engines; the byref variant was
scoped OUT and recorded, not silently dropped.

**Route:** a byref follow-up child (same family as D-13B area 4c CLR-method
`ref`/`out` typed-ref bridge and the `neo-step17-stobj-refloop` byref work).
The fix makes `NeoInvokeSub`'s arg-write / return-read byref-aware. Full
detail in `.trae/documents/neo-deferred-items.md` (F-7 / NEO-DELEGATE-REFOUT,
§2 master table + §3 detail).

### Accepted-known / pre-existing edges (from neo-step19-delegate, NOT new follow-ups)

- **F2 / WriteNeoCallSlot struct-with-ref-fields** -- `WriteNeoCallSlot`'s
  CLR-struct-with-ref-field discriminator (`RefCount > 0 && Size == 4`) is a
  pre-existing edge (same class as opt-harden-2 / area4 deferrals); no Step 19
  probe exercises it. Accepted-known.
- **NEO-IL-VT-INSTANCE-COVERAGE reuse** -- Step 19 probe 10's
  `Ldfld`-on-CLR-struct-param is the pre-existing Step 6 gap already tracked
  under `[NEO-IL-VT-INSTANCE-COVERAGE]` (line ~737). No new follow-up; the
  existing one covers it.

### Resolved follow-ups (from neo-step17-completion, 2026-07-05)

- **D-CONSTRAINED ({a,d,M2} scope)** -- RESOLVED. `constrained.callvirt T.M` on
  a value type delivered (the runtime Constrained arm OWNS the dispatch; JIT
  order is Constrained-before-callvirt, disproving the Option F fusion premise;
  IL-vs-CLR discriminator). (b)/(c) + IL-VT-with-ref-fields constrained ->
  `neo-step17-stobj-refloop` (task #18). Round-1 F1 review-fix added the
  IL-VT-inherited-CLRMethod box sub-branch so `anyIlStruct.ToString()` works.
- **D-LDELEMA (fully)** -- RESOLVED. The CLR primitive-array ldelema remainder
  shipped (Step 17 did the IL VT array path).
- **F-3 / NEO-BYREF-THIS callvirt caveat** -- RESOLVED. `constrained.callvirt`
  on a CLR struct now dispatches (was a Step 17 NIE caveat on the area4 direct-
  `call` closure).
- **F-5 / NEO-CALLARG-BOXED-SRC** -- RESOLVED. The box-once BYPASSES
  `CopyNeoCallArguments`; the wrong defensive CopyBlock replaced with a tagged
  NIE-guard + CopyNeoCallThisBack comment tightened. Closes the area4 M2
  obligation.
- **T1 / NEO-CONSTRAINED-NIE-TEXT** -- RESOLVED (the stale NIE text replaced).

### Resolved follow-ups (from neo-k2fam-bridge, 2026-07-06, TEST-ONLY)

- **K2-FAM** (portfolio task #6) -- **RESOLVED (subsumed, TEST-ONLY).** The K2-FAM
  deferred item (a CLR-VT LOCAL sourced from Box/Initobj, passed by value,
  reading an int as an mStack index) was probed on HEAD `f7539642` and found
  SUBSUMED by three changes whose combined effect was NOT re-assessed against
  K2-FAM until this change: `neo-opt-harden-2` (F-MAJ-1: declared a CLR-VT LOCAL
  as flat bytes under `#if ENABLE_NEO_MODE` -- the OLD boxed-ref representation
  `Size=4, RefCount=1` that produced the corruption NO LONGER EXISTS for a
  CLR-VT local) + its review-fix (rewrote the `Initobj` M1 / `Box` M2 /
  `Unbox_Any`-dest arms to read/write flat bytes) + `implement-neo-step13b`
  (unified the by-value-param flat-bytes read in `CopyNeoCallArguments`). All
  6 adversarial reproducer probes PASS on HEAD. **No new engine fix shipped**;
  instead 6 regression guards added to `TestCases/NeoStep13bTest.cs`
  (`NeoStep13_K2Fam_*`: BoxSourceByValue, InitobjSourceByValue, BoxMoveByValue,
  ReinitThenByValue, BoxUnboxByValueToHost, TwoBoxedStructLocalsByValue -- the
  last is the load-bearing F-MAJ-1 two-live-struct neighbour-corruption guard,
  Box-sourced). NeoStep 146/146, K2Fam 7/7, Legacy-neutral. **Lesson re-affirmed
  (the Q-NEWOBJ / Q-STRUCT / Q-LONG / F-5 family): a deferred item not re-probed
  on recent HEAD may already be SUBSUMED by later work -- construct the
  reproducer FIRST, on current HEAD, before designing a fix.** The F-2 /
  INLINER-REFONLY-VT entry is left intact (distinct inliner ref-fold defect
  class, still deferred). The IL-side-`Ldfld`-on-CLR-struct gap is the separate
  `[NEO-IL-VT-INSTANCE-COVERAGE]` Step-6 gap (deliberately out of scope; probes
  route field reads through host helpers). See
  `openspec/changes/archive/2026-07-06-neo-k2fam-bridge/ship-log.md`.

### `[NEO-VT-FLDADDR]` / F-6 -- RESOLVED by neo-vt-ldflda-inline (2026-07-06)

F-6 / NEO-VT-FLDADDR (`ldflda`-on-in-frame-VT: the Ldflda arm read the operand
slot as an mStack objIdx; an in-frame VT slot holds flat bytes -> garbage) is
now CLOSED by `neo-vt-ldflda-inline`. Marker stamp (`NeoLdfldaInlineMarker =
0x1` in standalone `Operand4`, stamped in `TypeSpecializeNeoOpcodes case Ldflda:`
when the source `Register2` is an in-frame IL VT -- the dest-type-seed
condition, pre-lowering) + a 3-way runtime dispatch (marker + leading-int: `-1`
-> frame-native; else marker -> flat-bytes shape 3; else -> heap/CLR). addrAlias
folding unchanged (the marker is invisible to the COEXIST gate -- it reads only
`Register1`/`Register2`/`Operand2`). Load-bearing stash-toggle (shape-3 probe:
FAIL-on-HEAD `mStack[42]` OOB -> PASS-after-fix). NeoStep 154/154; Legacy-
neutral. Review APPROVED (0 Blocker/Major; 2 Minor/Trivial accepted-known):

- **F-R1 (Minor, accepted-known):** the implementer's "key deviation" rationale
  (the literal `id.ToString()` body was claimed blocked by a separate F-3 gap)
  DOES NOT REPRODUCE in independent reconstruction -- the literal body PASSES in
  BOTH configurations (with the fix AND with it stashed); for an `int` field the
  C# compiler emits a by-value `ldfld` + a value-`this` `call`, never a byref to
  a CLR method. F-6 correctness is unaffected; the deviation note was softened
  to "the literal body was avoided out of caution / probe-isolation preference;
  the F-3 interaction is not reproducible."
- **F-R2 (Trivial, accepted-known) -- FOLLOW-UP:** probe 4.8 is a byref-of-
  primitive local, NOT a CLR-object-field `ldflda`. The genuine CLR-object-field
  `ldflda` operand kind remains UNCOVERED by this change (a real CLR-object
  `ldflda` carries no marker and hits the existing `else` branch, so F-6
  correctness is unaffected, but the probe set does not independently cover it).
  Deferred to the Step-17 stin/ldin follow-up **`neo-step17-stobj-refloop`**
  (task #18) -- same family as the D-CONSTRAINED stobj-refloop / CLR-object-
  field-hash deferral.

See `openspec/changes/archive/2026-07-06-neo-vt-ldflda-inline/ship-log.md`.

### Follow-ups from neo-step13-area4-refandstind (2026-07-06, Step 13 Area 4 4c+4d)

D-13B is now FULLY RESOLVED — Area 4c (CLR-method `ref`/`out` typed-ref
bridge) + Area 4d (CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field
identity) shipped alongside the prior 4b/4a (neo-step13-area4). All of Area 4
is done. Verification: NeoStep 175/175 (161 + 14 probes), NeoOptHard 24/24,
Legacy-neutral; all 13 functional probes FAIL-on-HEAD -> PASS. Review APPROVED
(0 Blocker/Major). See
`openspec/changes/archive/2026-07-06-neo-step13-area4-refandstind/ship-log.md`.

New + noted follow-ups recorded in `.trae/documents/neo-deferred-items.md`:

- **F-9 / NEO-INLINED-RETURN-MOVE (NEW follow-up).** An `int` returned from an
  inlined IL method is MOVED AS A REFERENCE -> `mStack[intValue]` OOB (the
  trivial-inliner mis-classifies the return value). Pre-existing (latent);
  surfaced when the 4d.2 `LdindClrIntFieldPeek` probe needed to defeat it via
  `int v = slot; return v + 0;`. Route to a future inliner/optimizer
  follow-up (the trivial-inliner's return-value classification in
  `JITCompiler.cs`). NOT introduced by 4d; probe-avoidance is the load-bearing
  evidence the edge is real.
- **F-7 / NEO-DELEGATE-REFOUT (still OPEN).** 4c's typed-ref bridge is the
  **IL->CLR** direction (an IL method passes a byref to a CLR method). F-7's
  `DelegateAdapter.NeoInvokeSub` is the **CLR->IL** callback direction (a
  delegate Invoke calls back into an IL method taking a byref param). DIFFERENT
  sites, OPPOSITE directions — the 4c helper does NOT apply. F-7 remains OPEN.
- **M-1 (`ref arr[i]` to a CLR method NIEs) -- accepted-known.** A byref-param
  to an array element passed to a CLR method hits `NeoMarshalByrefFieldToSlot`'s
  Array-branch NIE (the array case is owned by the stind/ldind array arm, not
  the field accessor). Documented limitation, scoped out (HEAD also could not
  do this). Route to a future `ldelema`+byref-param follow-up.
- **M-2 (ref/out write-back mStack growth) -- accepted-known.** Each
  reference-type ref/out write-back parks a NEW mStack entry (monotonic growth
  per call). GC-collected; matches the existing reference-param pattern. Not a
  correctness issue. Flag for a future de-dup pass.

### Follow-ups from neo-step17-stobj-refloop (2026-07-06, Step 17 (b) + IL-VT-with-ref-fields constrained)

D-CONSTRAINED is now FULLY RESOLVED for (a)/(b)/(d)/(M2) — only the (c) edges
remain. The Stobj/Ldobj ref-region copy (gated on `TotalReferenceCount > 0`,
primitive-only VTs byte-identical) + the IL-VT-with-ref-fields constrained sub-
case shipped. Verification: NeoStep 181/181 (175 + 6 probes), NeoStep17 Legacy-
neutral 41/41; all probes FAIL-on-HEAD -> PASS. Review round 0 APPROVED; round 1
fixed M1 (IL-instance branches now throw a tagged NIE on a `localInfos` scan-miss
— loud, not silent corruption) + M2 (dropped the dead
`constrainedSlot0SeedRefBase` param). LEAD non-author diff-read confirmed. See
`openspec/changes/archive/2026-07-06-neo-step17-stobj-refloop/ship-log.md`.

Follow-ups + lessons recorded in `.trae/documents/neo-deferred-items.md`:

- **(c) generic-byref / `fixed` / interface-on-VT-constrained -> `neo-step17-
  generic-byref-etc` (task #22, separate child).** These remain Step-17-tagged
  NIEs (or accept-known for `fixed` if a probe shows the address works without GC
  pinning). They are independent plumbing (a generic-param type-token
  discriminator; a pinned-local flag; an interface-dispatch branch) that does NOT
  fall out of (b) and is not exercised by the smoke. The explicit Step-13b/area4
  lesson re-affirmed: do NOT mix unrelated byref correctness surfaces.
- **Cross-frame byref parameter (R2 earned constraint) -> follow-up.** R2 (the
  runtime `localInfos` scan that recovers the byref source's ref-region mStack
  base) resolves the byref to a direct local ONLY in the same frame. A byref
  PARAMETER (a `ref` param to a non-inlined helper) points at the CALLER's frame;
  the helper's `localInfos` scan cannot recover that ref base. The probe set
  stays within the same-frame shape (the C# trivial inliner folds the small byref
  helpers into the caller where R2 resolves). A genuine cross-frame byref-of-VT-
  with-refs stays deferred; it fails clean (the `dstRefBase < 0` / `srcRefBase <
  0` NIE), not silent corruption.
- **Nested-VT-field-byref -> follow-up (blocked upstream).** A `ldflda` of a
  nested struct field where the leaf is a VT with reference fields does NOT
  resolve to a direct local -> tagged NIE. Blocked UPSTREAM by the pre-existing
  Step-6 `Ldfld_Value` NIE and the F-6 `Ldflda_Inline` paths — the genuine nested-
  field-byref shape never reaches the Stobj/Ldobj arm. Out of scope (independent
  pre-existing items, not introduced by this change).
- **Lesson (M1): silent-skip-as-silent-corruption.** The round-0 IL-instance
  branches guarded the ref copy with `if (srcRefBase >= 0) { ... }` and SILENTLY
  SKIPPED on a `localInfos` scan-miss — leaving the ILTypeInstance's
  `ManagedObjects` with stale/null ref slots. The frame-native branches threw a
  tagged NIE on the same miss. The asymmetry was a consistency/correctness-polish
  gap (the scan-miss case is exotic — the green-target probes all resolve), but
  it was the silent-corruption class the change otherwise avoids. Round-1 fix:
  mirror the frame-native NIE throw on the IL-instance scan-miss. Durable rule:
  when an arm has a "recovery miss" path, ALL operand shapes (frame-native AND
  IL-instance AND CLR) must fail the SAME way (loud NIE), never silent-skip —
  silent-skip is silent-corruption.
- **Gotcha (mStack-reservation-clobber seed placement).** The Constrained IL-VT-
  direct-call seed (the callee slot-0 ref region) MUST run AFTER the callee's
  `mStack.Add(null)` reservation (which zeroes slots) and BEFORE the body
  dispatch. A pre-call mStack write to `mStack[Count + ...]` would be zeroed by
  the reservation. Fix: defer the seed to INSIDE `ExecuteNeo`, right after the
  mStack reservation (post-reservation, pre-body), via a new
  `constrainedSlot0Seed*` hook — mirrors the VT-THIS-ADDR copy-back precedent
  (which likewise runs in the Ret arm before the mStack pop, because
  ExecuteNeo's `RemoveRange` destroys the data). Durable: any caller that wants
  to pre-seed the callee's mStack region must do so via an ExecuteNeo-internal
  hook placed after the reservation, NOT via a pre-call mStack write.

### `[NEO-CLRSTRUCT-FIELD-OF-IL]` / F-10 -- CLR-struct field of an IL instance ldflda offset defect (from neo-step20-async sync slice)

Surfaced by the neo-step20-async sync-slice review-loop round 1 (the
dump-gated STOP). The C# async state machine `<Method>d__N` is loaded as a
HEAP ILTypeInstance. Its CLR-struct fields (`<>t__builder` =
`AsyncTaskMethodBuilder`, `<>u__1` = `TaskAwaiter`) are laid out as reference
slots (the ILType field-layout pass `ILType.cs:2129-2157` records the field's
`PrimitiveOffset` + `ReferenceOffset`, then `referenceOffset++` -- treats the
CLR struct as a reference slot and does NOT advance `primitiveOffset` by the
struct's size). So the CLR-struct field's flat bytes do NOT live in the
ILTypeInstance's `Primitives` array. But the JIT `ldflda` of that field emits a
byref `(smMStackIdx, field.PrimitiveOffset)`; `NeoMarshalByrefFieldToSlot` then
reads `ili.Primitives[off]` for `sz` bytes -- OOB when `Primitives.Length` is
only the IL-primitive total.

Dump proof of the layout accident: TC1 (Task<int>) `smPrimLen=12` -- the
builder-byref `(2, 4, sz=8)` reads Primitives[4..12], IN range (passes by
luck); TC2 (non-generic Task) `primLen=4` -- the same byref reads
Primitives[4..12], OOB -> IndexOutOfRange. The byref encoding carries ONE
offset, unrecoverable to the field's actual storage at
`ManagedObjects[ReferenceOffset]`. **Pre-existing -- NOT introduced;** same
family as F-2 / NEO-BYREF-THIS and F-3. A narrow fix does NOT exist (would
induce silent corruption). **HIGH severity -- the load-bearing primitive BOTH
the rest of Step 20 sync AND the suspend slice need** (the awaiter field
`<>u__1` is the same shape).

**Route:** dedicated child `neo-clrstruct-field-of-il` (Wave 2.5, before
resuming neo-step20-async). Fix site: the field-layout pass
(`ILType.cs:2129-2157`) + the `ldflda` JIT lowering + the
`NeoMarshalByrefFieldToSlot` ILTypeInstance branch + the stfld/ldfld consumers
of CLR-struct fields on IL instances. Full detail in
`.trae/documents/neo-deferred-items.md` (F-10 / NEO-CLRSTRUCT-FIELD-OF-IL, §2
master table + §3 detail). See
`openspec/changes/archive/2026-07-06-neo-step20-async/ship-log.md`.

### `neo-step20-async-suspend` -- truly-async suspend/resume (from neo-step20-async)

The sync-completing slice shipped in neo-step20-async; the truly-async
suspend/resume path is the explicit split-point follow-up. Scope: a real
`AwaitUnsafeOnCompleted`/`AwaitOnCompleted` (frame->heap hoist via the shipped
`HoistNeoILValueToHeap` helper + `ILAsyncContext<T>` continuation registration),
`ILAsyncContext<T>.MoveNext()` resumption (restore the hoisted SM to a fresh
pooled interpreter, jump to the await state, run `ExecuteNeo`, complete the
`ManualResetValueTaskSourceCore<T>`), the awaited task's
`UnsafeOnCompleted(context.MoveNext)` callback, ExecutionContext /
SynchronizationContext capture for `AwaitOnCompleted`, cross-thread resume. The
`HoistNeoILValueToHeap` helper + `ILAsyncContext<T>` skeleton ship in
neo-step20-async to de-risk it; the wiring is this follow-up's job. Depends on
`[NEO-CLRSTRUCT-FIELD-OF-IL]` (the awaiter field `<>u__1` is the same shape).
See `openspec/changes/archive/2026-07-06-neo-step20-async/ship-log.md`.

### `Callvirt_CLR` generic-type-instance bug (from neo-step20-async, NOTED not fixed)

A `Callvirt_CLR` on a generic-type-instance method with no `RedirectionNeo`
throws `ArgumentException: The specified Type must not be a generic type` (the
`MethodInfo` is on the generic definition `Task`1`, not the closed `Task<int>`).
Did NOT block any green-target probe in neo-step20-async -- every
`Task<T>`/`TaskAwaiter<T>` accessor the sync path exercises IS covered by a
registered Neo redirect, so the reflection-fallback `clrMethod.Invoke` path
(where the ArgumentException originates) is never reached. The implementer's
`WriteValueTypeReturn` helper already uses
`Optimizer.GetNeoValueTypeManagedSize` (not `Marshal.SizeOf`). Follow-up only if
a future sync probe exercises an UN-redirected generic-type-instance CLR method.

### The 11 trimmed Step 20 probes (from neo-step20-async)

neo-step20-async shipped 13 `NeoStep20_*` probes; the smoke is kept green by
TRIMMING to TC1 + TC7 (the proven sync Task<int> regression guards). The other
11 probes (TC2-TC6, TC8 + their helper async methods) FAIL on the
`[NEO-CLRSTRUCT-FIELD-OF-IL]` edge and were removed from
`TestCases/NeoStep20Test.cs` with a clear comment. They will be re-added when
`neo-clrstruct-field-of-il` lands: TC2 SyncTask (non-generic), TC3
SyncValueTaskOfT, TC4 AsyncVoidSync, TC5 MultipleAwaits, TC6
AsyncExceptionFaultsTask, TC8 IncompleteAwaitHitsTaggedNIE (unreachable --
MoveNext fails before the IsCompleted short-circuit until F-10 is fixed). See
`openspec/changes/archive/2026-07-06-neo-step20-async/ship-log.md`.

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

## Findings -- neo-step17-completion (2026-07-05, propose)

**Scoping decision: cohort {(a) constrained.-on-VT, (d) CLR primitive-array
ldelema, (M2) F-5 closure} IN; {(b) Stobj/Ldobj ref-slot loop, (c) generic-
byref / `fixed` / interface-on-VT-constrained} DEFERRED to a new follow-up child
`neo-step17-stobj-refloop`.** Ranked the four sub-areas by value x low-
regression-risk. (a) is the highest-value (the callvirt-on-CLR-struct gap from
area4 + the D-CONSTRAINED core + closes the area4 M2 obligation that was folded
into D-CONSTRAINED); (d) is small + isolated (one `else` branch in the
`Ldelema` arm) so it ships cheaply in the same cohort; the M2 closure (F-5) is
obligated to land WITH (a) because (a) is what makes the boxed-source branch of
`CopyNeoCallArguments` reachable. (b) and (c) are independent correctness
surfaces that do NOT fall out of (a) -- bundling them would mix unrelated
dispatch/ref-loop work into one diff (the explicit Step-13b / area4 scoping
lesson). The DEFERRED-to child name is `neo-step17-stobj-refloop` (portfolio:
add as Wave 2 sibling).

**The Constrained re-append crux (the architectural center of (a)).** Code-
grounded at HEAD: the JIT detects a preceding `Constrained` and RE-APPENDS it
AFTER the callvirt (`JITCompiler.cs:~1954-1964`). It removes the `Constrained`
opcode from before the callvirt, stamps `op.Operand4 |= 0x1` on the callvirt
(and on `Call_Redirect`), copies `op.Operand2` (the dispatch slot) onto
`Constrained.Operand2`, and `lst.Add(old)` re-appends it. The constrained type
token is in `Constrained.Operand`. So at runtime the callvirt executes FIRST
(with `Operand4 & 0x1` set) and the `Constrained` arm runs AFTER. The C#
compiler lowers `v.ToString()` on a struct to `ldloca v; constrained T;
callvirt Object.ToString`; the callvirt's `this` is the 8-byte Ref Slot from
`ldloca`, but the callvirt resolver (`ResolveNeoCallvirtILTarget` /
`ResolveNeoCallvirtCLRTarget` -> `ReadNeoCallThis`) reads `this` as an mStack
index -- garbage for a byref `this`. So a naive runtime two-phase order
executes the callvirt mis-read BEFORE the Constrained arm can inform it.

**D1: the fix is a JIT fusion (Option F, PREFERRED) with a runtime two-phase
fallback (Option R).** Option F: fuse the `Constrained` prefix onto the
callvirt (carry the constrained type token in a spare operand) so the callvirt
resolver sees the constrained type + the byref `this` and dispatches via the
box-once / direct-call path; the trailing `Constrained` opcode becomes a
runtime no-op. The JIT ALREADY stamps `Operand4 |= 0x1` on the callvirt for a
constrained -- fusion just means also carrying the type token onto the callvirt
(must live in a non-colliding operand; the existing `Operand4` flag bits are
`0x1`/`0x2`/`0x4`/`0x10000+rCnt`). Option R (fallback): keep the callvirt +
trailing Constrained as two opcodes; the callvirt detects `Operand4 & 0x1`,
DEFERS (stashes the call site + byref `this`), the trailing Constrained arm
does the box-once + dispatch. **VERIFY at apply** via JIT dump of
`struct v; v.ToString()`: confirm `Operand4 & 0x1` is set + the constrained
type token is recoverable on the callvirt -> Option F; else Option R.

**Reuse map (do NOT reinvent).** (a) is a NEW CALLER of three already-shipped
mechanisms: VT-THIS-ADDR's in-frame VT address (the byref `this` resolves to
the struct's flat-byte frame region); area4's byref-`this` direct-call
(`PrimitiveByRefSrc` deref-at-copy-site + `ReadNeoValueType` reader +
`CopyNeoCallThisBack`) for the no-box path (D3); Step 13's Box arm
(`ReadNeoValueType` flat bytes -> boxed object) for the box-once (D2). The
box-once then dispatches via the existing `Callvirt_IL`/`Callvirt_CLR`/
`Callvirt_Interface` resolver on the boxed receiver. Legacy
`ILIntepreter.Register.cs:3898` `Constrained` arm is the SEMANTIC reference
(`GetObjectAndResolveReference` box-once + the `type is ILType` / `IsEnum`
branches); NOT modified.

**Box-once vs direct-call discriminator (D2/D3).** Keys on the constrained
type token's method slot: `M` declared on `Object` / an interface / a System
ValueType base and overridden by the struct -> box-once (the dispatched
override sees a boxed `this`); `M` declared on the struct itself -> direct-call
(no box, reuse area4). Mirror Legacy's discriminator.

**M2 obligation closure (F-5 / NEO-CALLARG-BOXED-SRC).** The box-once (D2) is
the ONLY producer of a boxed `this` through `CopyNeoCallArguments`. The boxed-
source branch (`ILIntepreter.Neo.cs:~258-265`) currently has a DEFENSIVE
`CopyBlock(targetBase + Dst, frameBase + offset, Size)` that is WRONG for a
boxed source (`offset` is an mStack field offset, not a struct address). Two
dump-gated shapes (OQ2): if the box-once feeds a CLR redirect (object-typed
`this`), store the boxed mStack index into the dest slot (reference-type
convention); if it feeds an IL override, the boxed `this` is an ILTypeInstance
and no special handling is needed. Either way the defensive CopyBlock is
replaced with the correct shape OR a NIE-guard (no silent mis-copy). The
`CopyNeoCallThisBack` comment is tightened to "mutating INSTANCE METHODS (not
ctors)" and the stale `Constrained` NIE text (T1) is fixed/removed.

**(d) CLR primitive-array ldelema (D-LDELEMA remainder).** The `Ldelema` arm's
`else` branch (`~3053-3057`) NIEs on a non-IL-VT array. Replace with: resolve
the CLR array element size from `la.GetType().GetElementType()`, compute
`elementByteOffset = elementIdx * elementSize`, encode
`(arrIdx, elementByteOffset)`. DUMP-GATE (OQ3): the `stind`/`ldind`
`objectIndex >= 0` arm currently assumes an `ILTypeInstance`; a CLR array
element lives in the CLR `Array` backing storage, NOT in `Primitives`. Likely
needs a `mStack[objIdx] is Array` branch in the consumer (mirror Step 16
`Ldelem`/`Stelem`). SCOPE to the smoke's green target (`int[]` ldelema ->
stind/ldind of int) if the consumer-side change is large; NIE the rest.

**Files the implementer will touch (all Neo-only; Legacy is the REFERENCE, NOT
modified):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs:~1954-1964` -- D1
  fusion (Option F): stamp the constrained type token onto the callvirt when
  re-appending Constrained.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the
  `Constrained` arm (`:~3126-3141`, replace NIE with dispatch / no-op per D1);
  the `Ldelema` arm `else` branch (`:~3053-3057`, CLR array); the
  `CopyNeoCallArguments` boxed-source branch (`:~258-265`, correct shape /
  NIE-guard); the `CopyNeoCallThisBack` comment (`:~274-285`); possibly the
  `stind`/`ldind` `objectIndex >= 0` arm (D4 consumer branch, dump-gated).
- `TestCases/NeoStep17Test.cs` (extend) -- `NeoStep17_*` adversarial probes
  (struct `ToString` boxed-once; `GetHashCode`; non-boxing struct method; CLR
  struct override; override-resolves-constrained-type; IL VT constrained; CLR
  `int[]` ldelema -> stind/ldind; the Step-17-B1 addrAlias register-reuse
  regression probe). Do NOT create NeoStep19Test.cs.

**Regression risk: MEDIUM-HIGH.** Touches the callvirt dispatch (shared by
every virtual call) + the addrAlias COEXIST gate (shared by every VT instance
method). Gate: full `NeoStep` smoke (117/117 baseline) + NeoOptHard (16/16) +
Legacy 518/519 stash-toggle for any shared-engine edit (all changes Neo-only).
Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1 / area4
dump-gate lessons binding). The biggest design risk is the D1 fusion
operand-non-collision + the D5 boxed-source path -- both DUMP-GATED, STOP if
the designed fix is wrong (do NOT ship a guessed fusion / guessed boxed-copy).

**Baseline note.** NeoStep smoke is 117/117 at HEAD (after neo-step13-area4).
The new constrained + CLR-array probes FAIL on HEAD (the constrained NIE / the
ldelema NIE) and turn green after the fix -- proves load-bearing (mirrors the
F-MAJ-1 / F-3 stash-toggle proof).

**Follow-up child created: `neo-step17-stobj-refloop`** ((b) Stobj/Ldobj ref-
slot loop for VT-with-ref-fields + (c) generic-byref / `fixed` / interface-on-
VT-constrained). Both are independent plumbing that does NOT fall out of (a).
Add to the portfolio Wave 2 (after this child).

## Findings -- neo-step17-completion (apply, 2026-07-05)

**RESOLVED (scoped: (a) constrained.-on-VT, (d) CLR primitive-array ldelema,
(M2) F-5 boxed-source closure).** NeoStep smoke 123/123 green (117 baseline + 6
new probes); NeoOptHardening 16/16; Legacy-neutral (plain Debug CLI builds clean;
the constrained probes pass on Legacy too; the 7 pre-existing Legacy NeoStep
failures reproduce identically). Working tree UNCOMMITTED.

**D1 = Option F' (NOT F, NOT R). The design doc's "JIT moves Constrained AFTER
the callvirt" claim was WRONG.** A JIT body dump of `struct v; v.ToString()`
showed the final order is `[Push..., Constrained T, Callvirt M]` -- Constrained
runs BEFORE the callvirt (the `lst.Add(old)` for Constrained happens during the
case at JITCompiler.cs:1963, but the callvirt `op` is only `lst.Add`-ed at the
end of Translate at :2412, so Constrained ends up before it). So the Constrained
arm runs FIRST, carries the type token (`ip->Operand`), and reads the trailing
callvirt at `ip+1` for the method token / param map / ret info. The Constrained
arm OWNS the dispatch (box-once for CLR value types + overrides-of-Object;
direct-call for IL value-type interface impls), then skips the trailing callvirt
(`ip += 2`). NO JIT change, NO operand stamping -> NO collision risk (Risk 1
dissolved: the existing `Operand4` flag semantics `0x1`/`0x2`/`0x4`/slot/
`thisArgOffset<<16` are byte-identical for non-constrained callvirts -- the
Constrained arm only fires for `OpCodeREnum.Constrained`).

**D1 dispatch-shape discriminator (NOT box-vs-method; it's IL-vs-CLR).** Keys on
the constrained type + the resolved override:
- **IL value type + ILMethod override -> direct-call (no box).** Deref the byref,
  copy flat primitive bytes into the callee slot-0 frame region
  (`targetBase + ParamInfos[0].Offset`), ExecuteNeo. The override reads
  `this.field` via in-frame Ldfld_Inline. A Neo IL-struct method body CANNOT
  consume a boxed ILTypeInstance `this` (its JIT uses in-frame layout). REUSES
  area4's flat-bytes-`this` shape.
- **CLR value type (primitive or struct) -> box-once.** Box the flat bytes
  (`NeoBoxReturnValue` for primitives, `ReadNeoValueType` for structs); park the
  boxed object on mStack; write the index to the callee slot-0 ref; dispatch via
  `GetVirtualMethod`-resolved CLR override.
- **Already-boxed / ref-type `this` (objIdx>=0) -> no-op box** (dispatch on the
  object). Reachable only via an interface-typed local holding a boxed struct;
  falls out for free.
The resolved override is via `constrainedType.GetVirtualMethod(targetMethod)`
(ILType) -- gives the constrained TYPE's concrete override (not the static
call-site method, not the bogus `Operand4` slot which for a constrained callvirt
is just the `0x1` flag).

**`ldflda`-on-in-frame-VT is a PRE-EXISTING GAP (out of scope; new follow-up).**
An IL struct override that takes a field ADDRESS (`v.ToString()` doing
`"Named:" + id.ToString()` lowers to `ldflda r3, r0, 0x...`) mis-reads: the
Ldflda arm reads `*(frameBase + operandSlotOff)` as an mStack objIdx -- for an
in-frame VT `this` (slot holds flat bytes), that's the first field's value
(garbage as an index). There is NO `Ldflda_Inline` (only `Ldfld_*_Inline` /
`Stfld_*_Inline`). So `ldflda this.field` on an in-frame VT is broken. This is
the `[NEO-IL-VT-INSTANCE-COVERAGE]` gap (IL-VT-instance-method coverage) made
concrete: any IL-struct method that takes a field address (e.g.
`field.ToString()`, `ref field`, `fixed`) is broken on Neo, REGARDLESS of
constrained. The constrained DISPATCH itself is correct (validated via the
IL-VT interface direct-call probe, which uses `Ldfld_I4_Inline` not `ldflda`).
Route: a dedicated `neo-vt-ldflda-inline` follow-up (add `Ldflda_Inline` /
extend the Ldflda arm to recognise an in-frame-VT operand via the type-spec
seed). The `NeoStep17_ConstrainedStructToString_BoxedOnce` task-list probe was
REPLACED with the interface direct-call probe for this reason.

**D4 (CLR primitive-array ldelema) consumer shape.** The existing `stind`/`ldind`
`objectIndex >= 0` arm calls `GetNeoILInstance(mStack, objIdx)` -- a CLR `Array`
is NOT an ILTypeInstance, so it cannot address a CLR array element. Resolution:
Ldelema encodes `(arrIdx, elementIdx)` where the `off` half IS the element index
(NOT a byte offset); Stind_I4 / Ldind_I4 gained a `mStack[objIdx] is Array`
branch (`cArr.SetValue(v, off)` / `(int)cArr.GetValue(off)`). Scoped to the
green target (Stind_I4 / Ldind_I4 on a CLR primitive array); other stind/ldind
variants + reference-type-element arrays remain NIE-tagged in their existing
arms.

**D5 (F-5 / NEO-CALLARG-BOXED-SRC) = NIE-guard.** The constrained box-once
BYPASSES `CopyNeoCallArguments` (the Constrained arm writes the boxed mStack
index directly into the callee slot, skipping slot 0 in the map copy). So the
boxed-source branch of CopyNeoCallArguments (`PrimitiveByRefSrc` with
`objIdx >= 0`) is NOT reached by the constrained path. The defensive `CopyBlock`
was WRONG for a boxed source (`offset` would be an mStack FIELD offset, not a
struct address) -- replaced with a tagged NIE-guard (no silent mis-copy). Also:
the map's slot-0 ref entry is meaningless for the constrained byref source (it
reads a garbage mStack index from the byref bytes) -- the Constrained arm's
ref-copy loop SKIPS the leading `slot0RefCount` ref entries (Object `this` ->
RefCount=1).

**addrAlias / callvirt regression gate: PASSED.** The fusion touches nothing in
the non-constrained callvirt paths (the Constrained arm only fires for
`OpCodeREnum.Constrained`; the Callvirt_IL/Callvirt_CLR/Callvirt_Interface/
Callvirt arms are byte-identical). The Step-17-B1 register-reuse adversarial
probe (an escaped byref read after a folding-window reuse -- the silent-
corruption class a green smoke MISSED) stays green (re-encoded as
`NeoStep17_AddrAliasRegisterReuseRegression`). VT instance methods called via
callvirt (non-constrained) are unchanged. Full NeoStep 123/123 + NeoOptHard 16/16
confirms no regression.

**CopyNeoCallThisBack comment corrected.** It covers MUTATING INSTANCE METHODS
(ctor / mutating instance method), NOT constructors -- the newobj path
(VT-THIS-ADDR) performs its own slot-0 -> caller-dest copy-back in ExecuteNeo's
Ret arm and does NOT invoke CopyNeoCallThisBack.

**Stale-DLL + filter gotchas re-affirmed.** (1) Rebuild TestCases
`--no-incremental` after every test-source edit (incremental hash hits silently
run stale code). (2) The ILRuntimeTestCLI name filter is a single `Contains`
substring -- run probes by a common prefix, NOT `A|B|C`. (3) `new int[]{...}`
array initializer emits `Ldtoken` (a Step-6 NIE) -- use `new int[n]; arr[i]=...`
in probes.

**Follow-up child created: `neo-vt-ldflda-inline`** (the `ldflda`-on-in-frame-VT
gap surfaced by the IL-struct ToString probe). Independent of (a)/(d)/(M2); does
NOT fall out of constrained dispatch. Add to portfolio Wave 2.

**Follow-up child unchanged: `neo-step17-stobj-refloop`** ((b) Stobj/Ldobj ref-
slot loop + (c) generic-byref / `fixed` / interface-on-VT-constrained beyond the
common shape). The IL-VT-with-ref-fields constrained sub-case NIE-defers to this
child (the byref does not carry the source struct's ref-region mStack base).

**Files touched (all Neo-only; Legacy is the REFERENCE, NOT modified):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the
  Constrained arm (replace NIE with dispatch); the Ldelema `else` branch (CLR
  array `(arrIdx, elementIdx)`); Stind_I4 / Ldind_I4 (`is Array` branch); the
  CopyNeoCallArguments boxed-source branch (NIE-guard); the CopyNeoCallThisBack
  comment; the stale Constrained NIE text.
- `TestCases/NeoStep17Test.cs` -- 6 `NeoStep17_*` adversarial probes + an
  IL-VT-interface struct + generic constrained callers.

**Did NOT git commit/push** (per process discipline; LEAD commits after review).
**Did NOT update neo-deferred-items.md / neo-handoff.md** (the shipper does at
archive, per implementer process discipline).

## Findings -- neo-step17-completion (review-fix)

The non-author review-loop caught a **Blocker REGRESSION** the green apply-phase
smoke (123/123) MISSED -- exactly the "green smoke missed it" failure mode the
portfolio's Step-17-B1 / OPT-HARDEN K1 lessons warn against. Durable lessons for
the persistent planner:

- **F1 mechanism (load-bearing for future Constrained work).** For an IL value
  type with NO override, `constrainedType.GetVirtualMethod(Object.ToString /
  ValueType.GetHashCode / Object.Equals)` returns the inherited **CLRMethod**,
  NOT an ILMethod. The discriminator's `actualMethod is ILMethod` gate (the safe
  direct-call path) does NOT fire -> falls into the box-once branch. There,
  `constrainedType.TypeForCLR` for an IL value type is `ILTypeInstance` (a
  CLASS), so `ReadNeoValueType(typeof(ILTypeInstance), frameBase + thisByteOff,
  ...)` interprets the struct's FLAT BYTES as an `ILTypeInstance` shape ->
  corrupt boxed receiver -> native segfault (ToString, exit 139) / NRE
  (GetHashCode). **An IL value type's `TypeForCLR` is `ILTypeInstance` (a
  reference type); `ReadNeoValueType` is ONLY sound for a genuine CLR value
  type.** Any future box-once code MUST gate `ReadNeoValueType` on
  `!(constrainedType is ILType)`.
- **Correct-fix chosen over tagged-NIE.** The dump showed the fix was clean +
  low-risk, so the IL-VT + inherited-CLRMethod sub-case was made to actually
  WORK (high-value: `anyIlStruct.ToString()` / string interpolation now returns
  a useful non-null string instead of crashing). Box mechanism: reuse Step 13's
  Box-arm machinery -- `ilBoxType.Instantiate(false)` + `CopyFrameToIL` (flat
  primitive bytes into the box's `Primitives` array) + `Boxed = true`, NOT
  `ReadNeoValueType`. The inherited CLRMethod is then dispatched via
  `InvokeNeoClrMethod` on the boxed `ILTypeInstance`. The IL-VT-with-ref-fields
  sub-case NIE-defers (the byref source does not carry the struct's ref-region
  mStack base) -- mirrors the direct-call path's deferral to
  `neo-step17-stobj-refloop`.
- **Discriminator shape = IL-vs-CLR, refined.** The apply-phase discriminator
  (IL-VT -> direct-call / CLR-VT -> box-once) was INCOMPLETE: it missed the
  IL-VT + inherited-CLRMethod shape (a THIRD shape). The fix is a new
  box-once sub-branch (`else if (constrainedType is ILType ilBoxType &&
  ilBoxType.IsValueType)`) inserted BEFORE the generic CLR-VT branch, PLUS a
  `!(constrainedType is ILType)` guard on the generic branch to prevent the F1
  segfault from recurring. **A constrained discriminator MUST enumerate: (1)
  IL-VT + ILMethod override -> direct-call; (2) IL-VT + inherited CLRMethod ->
  box into ILTypeInstance (NOT ReadNeoValueType); (3) CLR-VT -> box via
  ReadNeoValueType; (4) already-boxed -> no-op.**
- **Coverage-gap lesson (the F1 blind spot).** The implementer's IL-struct
  ToString probe was REPLACED by the interface direct-call probe (due to the
  unrelated `ldflda`-on-in-frame-VT gap), which inadvertently dropped the only
  probe that would have exercised the IL-VT + inherited-CLRMethod path. **For
  any constrained/on-VT change, a keeper probe MUST cover an IL struct with NO
  override calling an inherited Object/ValueType method (`ToString` /
  `GetHashCode` / `Equals` / `$"{x}"` interpolation) -- the majority of IL
  structs.** The interface-direct-call probe dodges this path (resolves to
  ILMethod); it cannot substitute.
- **addrAlias COEXIST gate (F5) confirmed NOT perturbed.** The Constrained arm
  touches NO optimizer/lowering code (D1 = F' = no JIT change), so the
  addrAlias / liveAliasMap machinery is byte-identical. With F1 fixed, the
  two-simultaneous-constrained-callvirts reuse probe (K7) runs cleanly: folded
  field values + byref dest + both boxed-receiver strings all survive. The
  Step-17-B1 silent-corruption class is NOT regressed. (Previously blocked by
  F1's crash; now independently confirmed.)
- **Smoke after fix: NeoStep 130/130 (123 + 7 keepers), NeoOptHard 16/16, plain
  Debug builds clean.** Working tree UNCOMMITTED.

**Files touched (review-fix, all Neo-only):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Constrained
  arm box-once branch: NEW IL-VT sub-branch (`Instantiate(false)` +
  `CopyFrameToIL` box) + `!(constrainedType is ILType)` guard on the generic
  CLR-VT branch.
- `TestCases/NeoStep17Test.cs` -- 7 new `NeoStep17_*` keeper probes (K1-K7) +
  `NeoStep17PlainStruct` (no-override IL VT) + generic constrained callers.
- `openspec/changes/neo-step17-completion/design.md` -- appended "Review-loop
  round 1 (F1 fix)" section (dump-gate, fix-chosen, keeper probes, F5
  reconstruction, F3/F4 disposition).

**Did NOT git commit/push** (per process discipline; LEAD commits after
re-review).


## Findings -- neo-step19-delegate (2026-07-05, propose)

**Scope.** Step 19 = full Neo delegate support. Three engine gaps, all verified
against HEAD code:

- `ldftn` / `ldvirtftn` are ABSENT from `ExecuteNeo` (hit the catch-all NIE).
  Legacy arms: `ILIntepreter.Register.cs:4524-4553` (`domain.GetMethod` +
  `AssignToRegister`; ldvirtftn reads `this` + `Type.GetVirtualMethod`).
- The `Newobj` arm throws `NotImplementedException("Neo Newobj delegate is not
  implemented")` for `IsDelegate` (`ILIntepreter.Neo.cs:1730`). Legacy arm:
  `ILIntepreter.Register.cs:3359-3408` (reads `this`=Register2 + IMethod=
  Register3, builds adapter via DelegateManager).
- `DelegateAdapter.InvokeILMethod` / `ILInvokeSub` / `ClearStack`
  (`DelegateAdapter.cs:925-1022`) is `StackObject`-wired (Legacy). A Neo
  calling-convention path is needed for the CLR -> IL callback (e.g.
  `List.ForEach(ilAction)`).

**The Legacy delegate flow (the reference, code-grounded).**
- `ldftn` JIT lowering (`JITCompiler.cs:2377-2385`): `InitializeFunctionParam`
  resolves the token into `Operand2`; `Register1` = dest. ldvirtftn lowering
  (`:2387-2396`): `Register1` = dest, `Register2` = `this` source. NO JIT
  CHANGE for Step 19 (the runtime arms are the only gap).
- The `Newobj` JIT lowering (`:1845-1878`) stamps the ctor args into
  `Register2`/`Register3`/`Register4` (NOT the param region) for up to 3 args;
  `delegate.ctor(object target, IntPtr fnptr)` is 2 args -> `Register2`=target,
  `Register3`=fnptr. **OQ1: confirm at apply via JIT dump whether Neo routes
  these through `CopyNeoCallArguments` -> `targetBase` OR leaves them in
  registers.** Legacy reads registers directly.
- DelegateAdapter (`DelegateAdapter.cs:888+`): `method`, `instance`, `next`
  fields; `CLRInstance = this` (so the adapter IS the CLR-visible delegate);
  multicast via `next`-chain (engine-agnostic). The per-arity
  `FunctionDelegateAdapter<...>`/`MethodDelegateAdapter<...>` assign
  `action = InvokeILMethod`; their `InvokeILMethod` bodies use
  `BeginInvoke()` -> `ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack)`
  (StackObject path).
- DelegateManager.FindDelegateAdapter (`DelegateManager.cs:232/273`):
  engine-agnostic; caches instance-method adapters on the ILTypeInstance,
  static-method adapters on `ilMethod.DelegateAdapter`.

**Key decisions LOCKED at propose.**
- **D1: IMethod representation in the Neo frame = a ref slot (CLR object).**
  An IMethod (ILMethod/CLRMethod) is a managed heap object -> Neo ref slot
  (mStack index in the byte region), exactly like a reference-type local
  (Step 7). Both ldftn + ldvirtftn write an IMethod into a ref slot. REJECTED
  alternative: raw pointer / token in the byte region (inconsistent with the
  adapter ctor reading it back as a managed object + the multicast/
  GetConvertor path).
- **D2: Delegate Newobj reads `this`(Register2) + IMethod(Register3) from ref
  slots, builds the adapter via DelegateManager.** Mirror Legacy
  `:3359-3408`. Instance -> cache on ILTypeInstance; static -> cache on
  `ilMethod.DelegateAdapter`. Store the adapter into the dest ref slot
  (`newobjDstIdx`) + index write (same shape as the IL ref-type newobj
  `:1813-1823`). The arg-routing (registers vs param region) is OQ1
  dump-gated.
- **D3: InvokeILMethod Neo convention = build a Neo frame, write CLR args into
  the param region, ExecuteNeo, read return.** The inverse of Step 8/9 IL->CLR.
  Reuse the `Run` Neo entry shim shape (`ILIntepreter.cs:97-111`) BUT push onto
  `esp` past the in-flight frame (a delegate callback from INSIDE ExecuteNeo
  has an in-flight frame; `Run` resets to StackBase because it is the outermost
  entry). Reuse area4 `ReadNeoValueType`/`WriteNeoValueType` for VT params/
  returns. The multicast `next`-chain works unchanged once the single-invoke
  path is correct (D4). REJECTED alternative: route through the existing `Run`
  / `appdomain.Invoke` re-entry -- `Run`'s Neo shim handles ONLY no-arg static
  methods (`:94-120`, Step 6 limitation) and cannot carry the bound instance.
- **D4: Reuse DelegateManager / next-chain / caching verbatim.**
  Engine-agnostic; NOT modified. Only the single-invoke path gains a Neo branch.
- **D5: CLRRedirectionDelegateNeo (Step 9) is the IL->CLR direction; Step 19
  is the INVERSE (CLR->IL) and lives in DelegateAdapter, not in the autogen
  codegen.** No Step 9 codegen change.

**Files the implementer will touch (all Neo-only / `#if ENABLE_NEO_MODE`-gated;
Legacy `ExecuteR`/`ILInvokeSub` byte-identical):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- new
  `case Ldftn` + `case Ldvirtftn` arms; the delegate branch in `case Newobj`
  (replace the Step 19 NIE at `:1730`).
- `ILRuntime/Runtime/Intepreter/DelegateAdapter.cs` -- add a Neo
  `NeoInvokeILMethod` path under `#if ENABLE_NEO_MODE` (frame build + param
  write + ExecuteNeo + return read + next-chain walk); wire the per-arity
  `InvokeILMethod` bodies to it. Legacy `StackObject` path under `#else`.
- `TestCases/NeoStep19Test.cs` (NEW) -- 9+ adversarial probes (TC1-TC9 per the
  task list + a VT-param/return probe).
- Possibly `ILRuntime/Runtime/Intepreter/ILIntepreter.cs` -- if the public
  `Run` re-entry needs a richer Neo instance-method path (OQ3), it lands here
  `#if ENABLE_NEO_MODE`. The `Run` Step-6 shim is NOT modified for Legacy.
- NO JIT change (ldftn/ldvirtftn/Newobj lowering already correct).

**Regression risk: MEDIUM.** The new opcodes are additive (currently NIE); the
Newobj delegate branch is gated by `IsDelegate` (does not touch shipped IL-VT /
IL-ref / CLR newobj). The DelegateAdapter change is `#if ENABLE_NEO_MODE`-gated
(Legacy byte-identical). Gate: full `NeoStep` smoke (130/130 baseline) +
Legacy 518/519 for any shared-engine edit. The biggest design risk is the
`InvokeILMethod` Neo frame build (the one untested corner) + the delegate-ctor
arg routing (OQ1) -- both probe BEFORE finalizing (the area4 / opt-harden-2
dump-gated discipline).

**Open questions for apply (dump-gated).**
- OQ1: does the JIT route the delegate `.ctor` `(target, fnptr)` args through
  `CopyNeoCallArguments` -> `targetBase`, OR leave them in `Register2`/
  `Register3`? Determines Newobj-arm read source. (Legacy reads registers.)
- OQ2: is the ldvirtftn `this` source (`Register2`) ALWAYS a ref slot for
  Step 19 scope (delegate over IL ref-type instance method or static method)?
  Yes for scope; a CLR-struct-instance-method delegate is the area4 byref-`this`
  shape (defer / probe if added).
- OQ3: can `NeoInvokeILMethod` reuse the engine's `stack.StackBase` + `esp`
  directly? NO -- a callback from inside `ExecuteNeo` has an in-flight frame;
  it MUST push onto `esp` (advance past current frame), not reset to
  StackBase. Confirm stack headroom under deep nesting (TC7 nested).

**Baseline note.** NeoStep smoke is 130/130 at HEAD. TC1-TC9 FAIL on HEAD (NIE
on ldftn / newobj) and turn green after the fix -- proves load-bearing
(mirrors the F-MAJ-1 / VT-THIS-ADDR stash-toggle proof).

**Portfolio sequencing note.** Step 19 unblocks Step 20 (async/await
continuations use delegates). The `[NEO-IL-EX-FIELDACCESS]` follow-up is
independent (callvirt-on-CLR-interface + appdomain.Invoke instance-method re-
entry) -- Step 19 does NOT depend on it, but OQ3's instance-method re-entry
shim MAY overlap with the follow-up's `appdomain.Invoke` re-entry path; flag
for the implementer to avoid duplicating that machinery.

## Findings -- neo-step19-delegate (apply, 2026-07-05)

**RESOLVED.** Step 19 delegate support shipped. Neo smoke **140/140** (130
baseline + 10 new `NeoStep19_*`); Legacy-neutral (plain Debug builds clean; the
7 pre-existing Legacy NeoStep failures unchanged; ALL 10 `NeoStep19_*` pass on
Legacy too). Working tree UNCOMMITTED.

**Biggest design correction (D2): the common `Action<>`/`Func<>` delegate is a
CLRType, NOT an ILType.** The propose-time design assumed the delegate newobj
landed in the `ilNewobjType.IsDelegate` branch. DUMP-DISPROVEN: `Func<int,int>`
resolves to a CLRType, so the common case routes through the CLR newobj branch
(`if (targetMethod.DeclearingType is CLRType)`). The DelegateAdapter is built
in a `clrDeclType.IsDelegate` sub-branch there via
`DelegateManager.FindDelegateAdapter(CLRType, ...)`. The IL-delegate branch is
ALSO implemented (rare IL-defined delegate types). Mirrors Legacy
`ILIntepreter.Register.cs:3539-3561` (CLR), NOT `:3359-3408` (IL).

**OQ1 dump-confirmed: ctor args route through CopyNeoCallArguments -> targetBase.**
The delegate ctor's NeoCallParamMap has 2 entries: map[0]=target (size 4, mStack
index), map[1]=fnptr (size 8; first 4 bytes = IMethod mStack index). The map
does NOT include a `this` slot. Runtime reads `*(int*)(targetBase + dst)` for
each. (Legacy reads registers; Neo reads targetBase because the optimizer
builds the map for the CLR delegate ctor too.)

**Risk 3 DISSOLVED: each delegate invoke runs on a FRESH interpreter.** The
design's OQ3 premise (re-enter the SAME interpreter, push past the in-flight
frame) is WRONG. Legacy `BeginInvoke` -> `RequestILIntepreter()` returns a
fresh/pooled interpreter per invoke. So a `List.ForEach(action)` callback from
inside an IL method runs on its OWN engine stack -- no in-flight frame to
clobber. `NeoInvokeSub` mirrors this (request fresh interpreter, build frame at
its StackBase, restore mStack on teardown). NO esp-past-frame logic needed.

**The design's 3 changes were insufficient -- 4 MORE Neo arms/redirects were
required for real C# delegate idioms:**
1. `Call_Redirect` Neo arm + optimizer case: C# `a += b`/`a -= b` -> `Delegate.Combine`/`Remove` (a Call_Redirect). Was a Step 6 NIE.
2. Neo `DelegateCombineNeo`/`DelegateRemoveNeo` redirects (the Legacy StackObject redirects are not Neo-aware). PARAM READ ORDER = source/declaration order (param 0 = dele1/source), NOT stack order (earned -- initial stack-order read broke `-=`).
3. IL-delegate-Invoke callvirt routing (`Callvirt_IL` arm): `del(args)` -> `adapter.NeoInvokePublic(args)` (Legacy `IsDelegateInvoke -> ILInvoke`).
4. **Delegate-typed `this`/param unwrap**: the autogen Neo binding for `Func.Invoke` cast the `this` directly to `Func<...>` -- but the object is an `IDelegateAdapter`. Added `CheckCLRTypes(TypeFlags.IsDelegate)` unwrap in `MethodBindingGenerator.cs` (delegate `this`-read) + `BindingGeneratorExtensions.cs` (delegate param-read) + `CLRMethod.Invoke` (reflection fallback). **Patched the 15 checked-in delegate binding files** (`System_Action_*`, `System_Func_*`) `Invoke_*_Neo` this-reads to match (the codegen fix only takes effect on regeneration -- the checked-in files needed manual patching via a PowerShell regex).

**Two pre-existing Step 6 gaps surfaced, scoped OUT of Step 19 (follow-ups):**
- Ref/out params through an IL-delegate-Invoke: `NeoInvokeSub`'s `object[]` arg model loses byref semantics. TC8 scoped to a plain-param IL-delegate Invoke. Ref/out-through-delegate needs byref-aware arg marshaling (a follow-up).
- Generic `Ldfld` on a CLR struct param (TC10): struct field access via non-inline `Ldfld` is `[NEO-IL-VT-INSTANCE-COVERAGE]`. TC10 returns a constant to isolate the struct-param marshaling round-trip (the R4 risk).

**Stale-DLL + filter gotchas re-affirmed.** Rebuild TestCases `--no-incremental`
after every test-source edit. The CLI filter is a single `Contains` substring
(run probes individually, not `A|B|C`).

**Files touched (all Neo-only / `#if ENABLE_NEO_MODE`-gated files; Legacy
byte-identical; the DelegateAdapter/CLRRedirections/AppDomain/MethodBindingGenerator/
BindingGeneratorExtensions/CLRMethod edits are `#if ENABLE_NEO_MODE`-gated or
delegate-type-gated; the optimizer `LowerNeoOffsets` ldftn/ldvirtftn +
Call-case entries are Neo-only):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Ldftn +
  Ldvirtftn arms; CLR + IL delegate Newobj branches; Call_Redirect arm;
  Callvirt_IL IsDelegateInvoke branch; ReadNeoDelegateInvokeArgs /
  WriteNeoDelegateInvokeReturn helpers; NeoBoxReturnValue made public.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- ldftn/
  ldvirtftn lowering (dest ref slot + DstOffset/SrcOffset); Call_Redirect in
  the Call-case map-building.
- `ILRuntime/Runtime/Intepreter/DelegateAdapter.cs` -- `NeoInvokeSub` +
  `WriteNeoCallSlot` + `NeoInvokePublic`; per-arity `InvokeILMethod` bodies
  wired to NeoInvoke under `#if ENABLE_NEO_MODE`.
- `ILRuntime/Runtime/Enviorment/CLRRedirections.cs` -- `DelegateCombineNeo` /
  `DelegateRemoveNeo` + `WriteNeoDelegateResult`.
- `ILRuntime/Runtime/Enviorment/AppDomain.cs` -- register the Neo Combine/Remove
  redirects.
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs` -- delegate `this`
  CheckCLRTypes unwrap.
- `ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs` -- delegate
  param CheckCLRTypes unwrap.
- `ILRuntime/CLR/Method/CLRMethod.cs` -- reflection-fallback delegate param
  unwrap.
- `ILRuntimeTestBase/AutoGenerate/System_Action_*` / `System_Func_*` (15 files)
  -- `Invoke_*_Neo` this-read patched to `CheckCLRTypes(...,IsDelegate)`.
- `TestCases/NeoStep19Test.cs` (NEW) -- 10 adversarial probes.

**Did NOT git commit/push** (per process discipline; LEAD commits after review).
**Did NOT update neo-deferred-items.md / neo-handoff.md** (the shipper does at
archive).

## Findings -- neo-step19-delegate (review-fix)

**Reviewer verdict:** CHANGES-REQUESTED -- one Major (F1, resource leak), one
Minor (F2, pre-existing edge), one Trivial (F3, whitespace churn). Smoke
independently reproduced: Neo 140/140 + Legacy NeoStep19 10/10; adversarial
probes (3-deep multicast, Func last-wins, nested-nested ForEach) all PASS.

**F1 (Major) -- FIXED.** `DelegateAdapter.NeoInvokeSub` called
`appdomain.RequestILIntepreter()` per delegate callback but never
`FreeILIntepreter` -- pool starvation + unbounded `ILIntepreter` allocation on
every delegate callback (the Step 19 hot path). Wrapped the body in
`try { ... } finally { appdomain.FreeILIntepreter(intp); }`, mirroring Legacy
`using (BeginInvoke())` -> `InvocationContext.Dispose` ->
`domain.FreeILIntepreter`. The free runs after `ExecuteNeo` returns and the
result is read; the `next`-chain recursion does its own balanced request/free;
the `if (unhandled) throw` path throws out of the `try` so `finally` fires on
the exception-escape path too.

**Pool-reclaim verified (instrumented probe, since removed).** Added temp
counters to `RequestILIntepreter` (pool-dequeue hits vs new-alloc) + 3 temp
probes:
- **F1-Loop** (1000 `list.ForEach(action)` callbacks): **2 allocs, 999 hits**
  (pre-fix would be ~1000 allocs / 0 hits). Definitive.
- **F1-Nested2** (delegate whose body drives `List.ForEach` on another
  delegate): 1 alloc, 12 hits, pool bounded -- interpreter freed at each
  nesting level.
- **F1-Exception** (IL target throws, caught by caller): 0 allocs, 2 hits,
  Pass -- `finally` fires, exception propagates.
All instrumentation removed before completion; only the `try`/`finally` +
`FreeILIntepreter` ships.

**F2 (Minor) -- accepted-known.** `WriteNeoCallSlot` CLR-struct-with-ref-field
param discriminator (`RefCount > 0 && Size == 4`) is a pre-existing gap (same
class as opt-harden-2 / area4 deferrals); no Step 19 probe exercises it. Left
as-is.

**F3 (Trivial) -- reverted.** Restored the trailing space on the Legacy line
`ctx.SetInvoked(esp); ` to eliminate cosmetic whitespace churn in the
byte-identical Legacy region.

**Smoke after fix:** NeoStep19 10/10, full NeoStep 140/140, Legacy (plain
`Debug`) builds clean + NeoStep19 13/13 (with probes live). Happy-path results
byte-identical (only lifecycle changed).

**Files edited (review-fix):**
- `ILRuntime/Runtime/Intepreter/DelegateAdapter.cs` -- F1 `try`/`finally` +
  `FreeILIntepreter` in `NeoInvokeSub`; F3 trailing-space revert on Legacy line.
- `openspec/changes/neo-step19-delegate/design.md` -- "Review-loop round 1
  (F1 fix)" section appended.

**Did NOT git commit/push** (LEAD commits after re-review).

## Findings -- neo-k2fam-bridge (2026-07-06, propose)

**REPRODUCIBILITY ASSESSMENT: SUBSUMED -- K2-FAM is RESOLVED; this is a TEST-ONLY
change.** The K2-FAM deferred item (a CLR-VT LOCAL sourced from Box/Initobj,
passed by value, reading an int as an mStack index) was probed on HEAD `f7539642`
with 6 adversarial reproducer probes BEFORE proposing. ALL 6 PASS on HEAD:

1. CLR struct local sourced from Box→Unbox (`T t = (T)o`), by value → PASS.
2. CLR struct local sourced from Initobj (`default(T)`), by value → PASS.
3. Box→Unbox→Move(struct copy)→by value → PASS.
4. Re-initobj (`t = default(T)`) then by value → PASS.
5. Box→Unbox→by-value to host static helper → PASS (returns 15).
6. TWO Box→Unbox-sourced struct locals, both by value (F-MAJ-1 two-live-struct
   stress, Box-sourced) → PASS (600 + 3).

**Why subsumed (code-grounded).** Three changes whose combined effect was NOT
re-assessed against K2-FAM until now collectively closed it:
- `neo-opt-harden-2` (F-MAJ-1) declared a CLR-VT LOCAL as flat bytes (`Size =
  GetNeoValueTypeManagedSize, RefCount = 0, localIsRef = false`) in
  `AllocateLocalStackSpaces` under `#if ENABLE_NEO_MODE`. The OLD boxed-ref
  representation (`Size=4, RefCount=1`) — the source of the "int-as-mStack-index"
  corruption — NO LONGER EXISTS for a CLR-VT local.
- `neo-opt-harden-2` review-fix (round 1) rewrote the three runtime arms that
  still assumed boxed-ref: `Initobj` (M1: `Unsafe.InitBlock` flat-bytes zero),
  `Box` (M2: `ReadNeoValueType` flat-bytes read), `Unbox_Any` dest (M2-twin:
  `WriteNeoValueType` flat-bytes write). So a local sourced from Box/Initobj/
  Unbox IS flat bytes end-to-end.
- `implement-neo-step13b` unified the by-value-param read in
  `CopyNeoCallArguments` to byte-copy N flat bytes from the caller local's
  `Offset`.

**The 7th probe (instance method `t.LengthSquaredInt()` / field read `t.x` on a
CLR struct param INSIDE an IL-defined method body) FAILS with `Neo: opcode Ldfld
not yet implemented (Step 6)`.** This is the SEPARATE pre-existing
`[NEO-IL-VT-INSTANCE-COVERAGE]` gap (Ldfld on a CLR struct field from IL — also
surfaced by the `neo-step13-area4` review and Step 19 TC10), NOT K2-FAM. The
keeper K2-FAM probes deliberately AVOID this gap by routing field reads through
host helpers (`TestCLRBinding.SumTestVector3NoBindingFields`), not through IL-side
`Ldfld`. The 7th probe was DROPPED (shipping a failing probe would regress the
smoke for an out-of-scope bug).

**Fix-or-test-only decision: TEST-ONLY.** No runtime/JIT/optimizer/CLR-binding
source change. The fix already shipped (opt-harden-2 + review-fix + 13b). This
change ships 6 adversarial regression guards (so a future change that re-
introduces a boxed-ref CLR-VT local representation, or breaks a Box/Initobj/
Unbox flat-bytes arm, is caught by the NeoStep smoke — the Step 17 B1 / F-MAJ-1
lesson: a green smoke does NOT prove a representation correct without an
adversarial probe for the specific corruption class). Plus the spec delta (the
K2-FAM closure requirement → DELIVERED; the Out-of-scope K2-FAM bullet → REMOVED)
and the deferred-items doc closure.

**Key decisions locked.**
- D1: TEST-ONLY (no engine fix); the probes that constitute the K2-FAM shape all
  PASS on HEAD. Shipping an engine fix for a non-reproducing defect would be
  worse than none (K1 / Q-NEWOBJ / Q-STRUCT / Q-LONG lesson).
- D2: probes live in `TestCases/NeoStep13bTest.cs` (the existing K2-FAM home;
  `NeoStep13_K2FamRegression` return-source probe is already there). Naming
  `NeoStep13_K2Fam_<SourceShape>` so both `NeoStep` and `K2Fam` filters group
  them.
- D3: DivideByZero-assertion pattern (no `throw new`; the K2-FAM defect is a
  SILENT wrong result, so a value-path assertion is correct).
- D4: `TestVector3NoBinding` (no binder, pure-primitive 3-float) — exercises the
  reflection-fallback `CLRMethod.Invoke(byte*)` path.
- D5: `TwoBoxedStructLocalsByValue` is the load-bearing guard (mirrors the F-MAJ-1
  two-live-struct stress, Box-sourced — the strongest neighbour-corruption
  detector).

**Files the implementer will touch (TEST-ONLY; no `ILRuntime/` runtime/JIT/
optimizer/CLR-binding file is modified):**
- `TestCases/NeoStep13bTest.cs` (extend) -- 6 `NeoStep13_K2Fam_*` probes.
- `openspec/specs/neo-boxing/spec.md` (delta merged at archive) -- the K2-FAM
  closure requirement DELIVERED; the Out-of-scope K2-FAM bullet REMOVED.
- `.trae/documents/neo-deferred-items.md` -- K2-FAM row/entry → RESOLVED
  (subsumed). F-2 / INLINER-REFONLY-VT entry left intact (distinct defect class).

**Regression risk: NONE (test-only).** No engine file touched. Smoke: NeoStep
140/140 → 146/146 (additive; all 6 probes PASS on HEAD). Legacy-neutral by
construction (the F-MAJ-1 fix is `#if ENABLE_NEO_MODE`-gated; the probes are
representation-agnostic — they assert field-sum correctness, which holds on both
engines; Legacy's Box/Unbox/Initobj always handled the by-value-param shape).

**Spec-validation gotcha re-affirmed (the area4 finding).** The openspec
validator requires the requirement DESCRIPTION (not just the title) to contain
SHALL/MUST. The first K2-FAM requirement draft led with a "When a CLR value type
LOCAL ... is passed BY VALUE ..." clause (no SHALL until sentence 2) and FAILED
validation with "must contain SHALL or MUST". Fixed by leading with an explicit
"A CLR value type LOCAL ... SHALL be byte copied ..." sentence. Re-validate after
every spec edit.

**Lesson re-affirmed (the Q-NEWOBJ / Q-STRUCT / Q-LONG / F-5 family).** A deferred
item that has not been re-probed on recent HEAD may already be SUBSUMED by later
work. The K2-FAM partial-close note (Step 13b apply, 2026-07-04) said the boxed-
ref-source half "needs IL-side ldfld/stfld on CLR struct fields for a clean
reproducer" -- but THREE subsequent changes (opt-harden-2 + its review-fix + the
13b by-value-param read) made the local flat bytes and the boxed-ref
representation ceased to exist. CONSTRUCT THE REPRODUCER FIRST, on current HEAD,
before designing a fix. Here the reproducer PROVED the closure, turning a
would-be engine fix into a test-only lock-in.


## Findings -- neo-k2fam-bridge (apply, 2026-07-06)

**RESOLVED (TEST-ONLY).** K2-FAM Box/Initobj/Unbox-source closure locked in
with 6 adversarial regression guards in `TestCases/NeoStep13bTest.cs`. NO
runtime/JIT/optimizer/CLR-binding file touched (the fix already shipped via
`neo-opt-harden-2` + its review-fix + `implement-neo-step13b`).

**The 6 keeper probes (all PASS on HEAD; co-located with the existing
return-source `NeoStep13_K2FamRegression` in `NeoStep13bTest.cs`):**
1. `NeoStep13_K2Fam_BoxSourceByValue` -- local via Box->Unbox, by value (==60).
2. `NeoStep13_K2Fam_InitobjSourceByValue` -- local via `default(T)`, by value
   (==0).
3. `NeoStep13_K2Fam_BoxMoveByValue` -- Box->Unbox->struct-copy(Move)->by value
   (==66).
4. `NeoStep13_K2Fam_ReinitThenByValue` -- local re-initobj'd via
   `= default(T)`, by value (==0, the post-re-init value).
5. `NeoStep13_K2Fam_BoxUnboxByValueToHost` -- Box->Unbox->by-value to a host
   static helper (==6).
6. `NeoStep13_K2Fam_TwoBoxedStructLocalsByValue` -- TWO Box->Unbox-sourced
   locals both by value (the F-MAJ-1 two-live-struct stress, Box-sourced;
   `r1==600 && r2==3`). LOAD-BEARING neighbour-corruption guard.

**Verification.**
- Neo full NeoStep smoke: **146/146** (140 baseline + 6 new), 0 failed.
- `K2Fam` group filter: **7/7** (6 new + the existing return-source probe),
  0 failed -- collectively cover all K2-FAM source shapes (return/Box/
  Initobj/Unbox/Move/re-init/two-live).
- Legacy-neutral: `K2Fam` filter on plain `Debug` + `useRegister=true` =
  **7/7**, 0 failed (representation-agnostic field-sum assertions hold on
  both engines). The 2 failures in the broader Legacy `NeoStep13` filter are
  the PRE-EXISTING `NeoStep13Test.NeoTestClrStructNoBindingBoxRoundTrip`
  family (different file/class, already listed in the standing Legacy pre-
  existing failure set) -- NOT caused by this change and NOT in the K2-FAM
  family.

**Probe design constraints honoured.**
- All field reads routed through host helpers
  (`TestCLRBinding.SumTestVector3NoBindingFields` -- by-value param, returns
  the int field sum). NO IL-side `Ldfld` on a CLR struct field anywhere (the
  `[NEO-IL-VT-INSTANCE-COVERAGE]` Step-6 gap is out of scope; the dropped 7th
  probe would have hit it).
- `TestVector3NoBinding` (no binder, pure-primitive 3-float struct) used
  throughout -> exercises the reflection-fallback
  `CLRMethod.Invoke(byte*)` path (same as the existing 13b probes).
- DivideByZero-assertion pattern (`int _ = 1/0` on a wrong result), not
  `throw new` / `[ExpectedException]` (neither exists in the harness).

**Stale-DLL gotcha re-confirmed.** `--no-incremental` rebuild of TestCases
after adding the probes; verified `TestCases.dll` mtime > `NeoStep13bTest.cs`
mtime before running the smoke (incremental hash hit can silently run the old
DLL).

**Did NOT git commit/push** (LEAD commits after review). Did NOT modify any
`ILRuntime/` runtime file (test-only). Did NOT update
`neo-deferred-items.md` (the shipper does at archive).

## Findings -- neo-vt-ldflda-inline (2026-07-06, propose)

**F-6 / NEO-VT-FLDADDR REPRODUCED + ROOT CAUSE DUMP-CONFIRMED (with a
refinement of the F-6 wording).** The F-6 finding said "the `Ldflda` arm reads
the operand slot as an mStack objIdx; an in-frame VT operand slot holds flat
bytes -> garbage." That is TRUE but UNDER-SPECIFIED -- it is not ANY in-frame-VT
operand, it is specifically an in-frame-VT operand WHOSE SLOT HOLDS FLAT BYTES
(not a Ref Slot). Two of the three operand shapes already work on HEAD; only
the third is broken. The propose-phase reproducer + a temporary diagnostic in
the runtime `Ldflda` arm (`ILIntepreter.Neo.cs:827-857`) confirmed:

- **Shape 1 (ldloca; ldflda -- PASSES on HEAD):** `BumpByTen(ref s.x)` with `s`
  a frame local. The C# compiler lowers to `ldloca V; ldflda f`. The `ldloca`
  dest slot holds a frame-native Ref Slot `(-1, V_offset)`, so the runtime arm
  reads `objIdx == -1` -> frame-native branch -> CORRECT. Diagnostic read
  `objIdx=-1`.
- **Shape 2 (struct instance method via DIRECT call -- PASSES on HEAD):**
  `s.ReadIdViaAddress()` doing `return ReadRef(ref id)` (-> `ldflda this.id`).
  The C# compiler lowers `s.M()` to `ldloca s; call M`. The Step-17 byref-`this`
  call-ABI seeds the callee's param slot 0 with a frame-native Ref Slot
  `(-1, s_offset)`, so inside `M`'s body `ldflda this.field` reads
  `objIdx == -1` -> CORRECT. Diagnostic read `objIdx=-1`.
- **Shape 3 (struct override via constrained.callvirt box-once -- FAILS on
  HEAD, the load-bearing gap):** `s.ToString()` on `S { int id; }` with
  `override string ToString() => "Named:" + id.ToString()`. The constrained
  box-once boxes the IL struct into an ILTypeInstance + dispatches the override.
  The override's `this` (param slot 0) is seeded with the struct's FLAT
  PRIMITIVE BYTES (the box's `Primitives` copied in, NOT a Ref Slot). So inside
  the override body, `ldflda this.id` reads `objIdx = <id value>` (e.g. `42`)
  -> the `>= 0` branch fires -> garbage Ref Slot `(42, 0)` -> the consumer
  reads `mStack[42]` as an ILTypeInstance -> WRONG RESULT. Diagnostic read
  `objIdx=42`. Test FAILS. This is exactly the shape that blocked the Step-17
  IL-struct ToString probe (which was swapped for the interface direct-call
  probe because of THIS gap).

**The runtime arm CANNOT distinguish shape 1/2 (operand = Ref Slot) from shape
3 (operand = flat bytes) by inspection** -- both are 8+ bytes at the same frame
offset, and the leading int is `-1` for 1/2 but a field value for 3. A
JIT-side marker is required. The `neo-byref` spec ALREADY SPECIFIES it
("ldflda / ldarga address producers": "The optimizer SHALL stamp a marker
(e.g. `Operand4`) on a real `ldflda` so the arm distinguishes the in-frame-VT
case from the heap-IL case") -- it was simply never implemented for the flat-
bytes operand (the Step-17 implementation covered the ldloca-Ref-Slot operand).

**Fix LOCKED (mirror VT-THIS-ADDR's dump-gated discipline).**
- **D1 (JIT marker):** in `TypeSpecializeNeoOpcodes` `case OpCodeREnum.Ldflda:`
  (`JITCompiler.cs:798-804`), when the source `Register2` is an in-frame IL
  value type (the SAME condition that already seeds the dest type), ALSO stamp
  a flag bit on `op.Operand4` (standalone, offset 20, currently UNUSED for
  `Ldflda` -- no collision). The type-spec pass runs PRE-lowering, so
  `Register2` is still available for `GetRegisterType` (after `LowerNeoOffsets`
  it becomes `SrcOffset` -- too late). `Operand4` survives lowering (standalone,
  not a union field).
- **D2 (runtime arm, REFINED -- the marker is sound for BOTH shape 1/2 AND 3):
  the marker means "operand is an in-frame VT"; the arm STILL reads the leading
  int to distinguish Ref-Slot-vs-flat-bytes, but with the marker's guarantee
  that a non-(-1) leading int is a FIELD VALUE (shape 3), NOT an mStack index.**
  Marker set + leading int == -1 -> shape 1/2 (resolve through the Ref Slot's
  offset half). Marker set + leading int != -1 -> shape 3 (struct base =
  operandSlotOff itself). Marker absent -> existing heap/CLR dispatch (byte-
  identical). This is sound: the marker guarantees the operand is an in-frame
  VT, so a non-(-1) leading int cannot be an mStack index (the heap/CLR path is
  non-marker).
- **D3 (addrAlias folding):** UNCHANGED. The marker only disambiguates the
  runtime arm when it fires; folding decisions are untouched (the marker is on
  `Operand4`, which the folder does not read).

**The discriminator reuse.** The JIT marker condition is the SAME in-frame-VT
check the type-spec pass already does (`GetRegisterType(...) is ILType &&
IsValueType && !IsEnum`) -- the Ldloca/Ldflda/Newobj dest-typing rule family
(VT-THIS-ADDR's "third in-frame-VT address case"). No new type-info machinery;
just an additional flag write alongside the existing dest-type seed.

**The addrAlias consideration.** An ldflda-produced address that escapes the
folding window is an addrAlias COEXIST-gate concern (Step 17 B1). The marker
does NOT change folding or the live-range logic; it only changes what the
runtime arm produces when it fires. The Step-17-B1 register-reuse adversarial
probe (an ldflda-produced byref whose dest register is reused, then the byref
is read -- the silent-corruption class a green smoke MISSED) is MANDATORY in
this change's probe set, both for the Ref-Slot shape and (new) the flat-bytes
shape.

**Files the implementer will touch (all Neo-only; Legacy `ExecuteR` is the
REFERENCE -- its `Ldflda` arm discriminates via `GetObjectAndResolveReference`
+ `ObjectTypes.ValueTypeObjectReference`, a tagged representation, NOT
modified):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `TypeSpecializeNeoOpcodes` `case Ldflda:` (`:798-804`): stamp the marker flag
  bit on `Operand4` alongside the existing dest-type seed. New named const
  `LDFLDA_INLINE_MARKER`. NO `Code.Ldflda` Translate change (the marker is
  stamped in the type-spec pass, not at Translate).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the
  `case OpCodeREnum.Ldflda:` arm (`:827-857`): add the marker check + the two
  sub-branches (Ref-Slot operand -> resolve through offset half; flat-bytes
  operand -> struct base = operandSlotOff). Existing non-marker dispatch byte-
  identical.
- Possibly `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` --
  CONFIRM `LowerNeoOffsets` does not clobber `Operand4` for `Ldflda` (it should
  not -- `Operand4` is standalone; verify via the JIT dump at apply). No
  `addrAlias` change.
- `TestCases/NeoStep17Test.cs` (extend) -- 7+ `NeoStep17_LdfldaInline_*`
  adversarial probes (ref-field read/write; struct ToString override [load-
  bearing]; nested field; ref-type field [ref region]; register-reuse escape
  [Step-17-B1 class]; heap-IL regression; CLR-object regression). Do NOT create
  a new test file.

**Regression risk: MEDIUM.** The runtime `Ldflda` arm is shared by every
`ldflda` (heap-IL, CLR-object, Ref-Slot, flat-bytes). The marker gates the new
branch; the existing branches are byte-identical when the marker is absent
(marker is stamped ONLY for an in-frame IL value-type source). Gate: full
`NeoStep` smoke (**146/146 baseline at HEAD `7077ea42` -- confirmed green at
propose**) + Legacy-neutral stash-toggle. Adversarial probes MANDATORY (Step-
17-B1 / OPT-HARDEN-K1 / F-MAJ-1 lessons: the flat-bytes-vs-Ref-Slot ambiguity
is exactly the corruption class a green smoke can miss -- shape 1/2 passing
does NOT prove shape 3 correct).

**Capability spec home: `neo-byref`** (ldflda produces a byref/Ref Slot; the
existing "ldflda / ldarga address producers" requirement already owns the
marker + in-frame-VT-operand semantics -- this change DELIVERS the marker for
the flat-bytes operand). NOT `neo-value-types` (which owns the in-frame-VT
storage + `_Inline` field access + `addrAlias` folding fast path, all
UNCHANGED by this fix). One MODIFIED requirement (the "ldflda / ldarga address
producers" requirement, expanded with the marker-stamping SHALL + the flat-
bytes-operand scenario + the struct-ToString-override positive scenario).

**Side-benefit watch.** The IL-struct `ToString()` override calling a field
method (`id.ToString()`, `$"{id}"`, `GetHashCode` using a field) is the
majority of meaningful IL structs -- and it is broken on HEAD today. This
change turns it green (the Step-17 deferred positive test finally ships). Check
at verify whether any existing NeoStep case (or broader suite) was avoiding
the struct-ToString-calls-field-method pattern; note in ship log.

**Lesson re-affirmed (the F-5 / K2-FAM / Q-NEWOBJ family).** The F-6 finding
as worded ("ldflda on in-frame VT") was ALMOST right but missed the
discriminator (operand-slot-holds-Ref-Slot vs operand-slot-holds-flat-bytes).
Constructing the reproducer FIRST, with a runtime diagnostic, DISTINGUISHED
the three operand shapes and pinned the exact broken one (shape 3). A fix
designed from the F-6 wording alone (always produce frame-native when typed
in-frame) would have been sound but would have missed WHY shape 1/2 already
work -- and the dump-gated D2-refined marker logic (read the leading int even
with the marker set) is the sounder formulation. The dump-gated discipline is
binding: probe BEFORE fixing, document the exact shape that breaks.

## Findings -- neo-vt-ldflda-inline (apply)

**RESOLVED.** F-6 / NEO-VT-FLDADDR closed. ldflda on an in-frame IL value type
whose operand slot holds FLAT BYTES (a constrained-boxed `this` in an IL-struct
method body) now produces a correct frame-native Ref Slot. Marker =
`JITCompiler.NeoLdfldaInlineMarker = 0x1` (bit 0x1 of standalone `Operand4`,
offset 20). DUMP-GATE: `Operand4` was untouched for `Ldflda` everywhere at HEAD
(JIT Translate, type-spec, all optimizer sites) -> collision-free. Stamped in
`TypeSpecializeNeoOpcodes` `case Ldflda:` (the same condition that seeds the
dest type); Neo-only (the pass is `#if ENABLE_NEO_MODE`). Runtime arm
(`ILIntepreter.Neo.cs` `case Ldflda:`) gains a 3-way dispatch keyed on the
marker + the leading int: objIdx == -1 -> frame-native Ref Slot (shape 1/2);
else marker -> flat-bytes (struct base = operandSlotOff); else -> heap/CLR.

**Load-bearing stash-toggle.** Probe `NeoStep17_LdfldaInline_StructMethodFlatBytes`
(an IL struct ToString override dispatched via a generic constrained caller,
body takes `ref id` via ldflda -> IL byref helper). FAILS on HEAD with
`Index was out of range` (ldflda reads id=42 as an mStack index -> mStack[42]
OOB); PASSES with the fix (marker branch -> (-1, 0) -> reads 42). NeoStep smoke
154/154 (146 baseline + 8 new probes). Legacy-neutral (plain Debug builds
clean; all changes Neo-only).

**DEVIATION recorded (design task 4.3 reproducer body).** The design literal
`return "Named:" + id.ToString();` body was REPLACED by an IL byref helper
(`ReadViaRef(ref id)`) to isolate the ldflda correctness from the unrelated
CLR-call path. **NOTE (review softening, 2026-07-06, F-R1):** the original
apply-phase rationale (the literal body FAILS on HEAD/with-fix because
`Int32.ToString()` receives the frame-native byref as `this` and reads 0 -- a
"separate F-3 / NEO-BYREF-THIS gap") DOES NOT REPRODUCE in independent
reconstruction: the literal body PASSES in BOTH configurations (with the fix
AND with it stashed). For an `int` field the C# compiler emits a by-value
`ldfld` + a value-`this` `call Int32.ToString()`, NOT a `ldflda` + byref-`this`
call -- so no frame-native byref reaches the CLR method and the F-3 gap is
never engaged. F-6 correctness is unaffected; the literal body was avoided out
of caution / probe-isolation preference, NOT because of a real F-3 gap. A
future Step-17 D-CONSTRAINED follow-up need NOT chase a literal-`id.ToString()`
gap here.

**Shape-3 trigger subtlety (earned).** Shape 3 (flat bytes at the override
slot-0) is produced ONLY by `constrained.callvirt` box-once (the C# compiler
emits it for virtual overrides like `s.ToString()` on a struct). A NON-virtual
instance method call (`s.M()` direct on a local) emits `ldloca; call` (direct
call) -> shape 2 (slot-0 = frame-native Ref Slot, objIdx == -1) -> already
works on HEAD. So a load-bearing F-6 probe MUST dispatch via a constrained
caller (not a direct call). An interface-method dispatch on an IL-struct-via-
box-once hits the separate `ResolveNeoCallvirtInterfaceTarget` gap
([NEO-IL-VT-INSTANCE-COVERAGE] Step-6/11 family) -- also not F-6.

**Probes 4.4/4.5/4.6/4.7/4.8 -- all green.** Nested-field (address-only,
avoids Ldfld_Value NIE), reference-type field (ref-region), register-reuse
escape (Step-17-B1 class -- no silent corruption; liveAliasMap handles it),
heap-IL regression, CLR-object regression (scoped to byref-of-primitive to
avoid the Step-17 CLR-field-hash stind/ldind deferral). Probe 4.6 (the
register-reuse/escape probe, the Step-17-B1 silent-corruption class) is GREEN
-- the F-6 marker does not perturb the addrAlias COEXIST gate.

**Did NOT git commit/push** (per process discipline; LEAD commits after review).

## Findings -- neo-opportunistic-cleanup (2026-07-06, propose)

**TRIVIAL comment + test-only change; both items confirmed against current HEAD.**

- **N-CGTUN confirmed.** The `Cgt_Un` arm in `ILIntepreter.Neo.cs` (~1018-1038)
  computes `cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1)`. The
  divergence comment (~1029-1032) names ONLY the operand case
  (`cgt.un x, (uint)0xFFFFFFFF` -> the `cguB == -1` clause). It OMITS the
  symmetric SOURCE case: the leading `cguA != -1` clause forces the result to
  `false` when the source is the null sentinel `-1`, which is ALSO a divergence
  from raw `(uint)cguA > (uint)cguB` semantics. Both are the same sentinel-
  collision class (`-1 == 0xFFFFFFFF`); neither is exercised by the validated
  TestCases suite. Fix = comment-only tighten naming both; runtime expression
  byte-identical.
- **N-TC2 confirmed.** `NeoStep14_TC2_CatchObjectAccess` in
  `TestCases/NeoStep14Test.cs` (~40-52) catches `DivideByZeroException e` and
  asserts only `if (e != null) return 7;`. The file header (~line 18) states the
  reason: "isinst/castclass land in Step 15". Step 15 has shipped `isinst`
  (`neo-type-checks`), and the IL-exception follow-ups confirmed `e is T` works
  on a caught exception (`NeoStep14_ILEx_*` probes use `e is MyEx`). Fix =
  tighten TC2 to `if (e is DivideByZeroException && e.Message != null) return 7;`
  -- the `is` lowers to `isinst`, exercising the type-check-in-catch shape.

**Spec delta decision (earned).** The prompt left the spec delta optional
(comment+test-only). The honest call is "no requirement changes," but the
openspec toolchain blocks `tasks` on a non-empty `specs/**/*.md`. Resolution: a
narrow MODIFIED delta on the existing `isinst` requirement (`neo-type-checks`)
that ADDS ONE scenario -- "An `is` check on a caught exception resolves its
type" -- covering the type-check-in-catch shape TC2 now guards. This IS a real
behavior the spec had no explicit scenario for (Step 15 implemented it; the IL-
exception probes verified it; TC2's tighten is what regression-guards it), so
the scenario is defensible, not fabricated. `openspec validate` passes. Note for
future trivial changes: if there is genuinely nothing to add to a requirement,
the alternative is to leave `specs/` empty and accept that `tasks` stays
"blocked" in the toolchain (author the tasks file manually).

**Regression risk: NONE.** Both edits are non-functional (comment + test
tighten). Gate: full `NeoStep` smoke stays 154/154 at HEAD; TC2 itself stays
green (tightened, not loosened). No shared-engine edit, no JIT/optimizer change.

**Files to touch (apply phase):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- `Cgt_Un`
  divergence comment (~1029-1032). Comment-only.
- `TestCases/NeoStep14Test.cs` -- `NeoStep14_TC2_CatchObjectAccess` body (~47-50).
  Test-only.
- `.trae/documents/neo-deferred-items.md` -- move N-CGTUN + N-TC2 to §4 Resolved.
- The four openspec artifacts (this proposal/design/specs/tasks).

## Findings -- neo-opportunistic-cleanup (apply, 2026-07-06)

**RESOLVED.** Both micro-fixes applied; full NeoStep smoke **154/154 green**
(TC2 stays green, now asserting strictly more). Comment-only + test-only;
zero behavior change.

**N-CGTUN (comment tighten).** The `Cgt_Un` arm divergence comment in
`ILIntepreter.Neo.cs` (~1029-1032) named ONLY the operand-sentinel case
(`cgt.un x, (uint)0xFFFFFFFF`). Tightened to name BOTH symmetric sentinel
collisions (both because `-1 == 0xFFFFFFFF`): (a) the operand case
(`cguB == -1` short-circuits the compare to `true`) AND (b) the source case
`cgt.un (uint)0xFFFFFFFF, x` (the leading `cguA != -1` clause forces `false`).
`git diff` confirms the runtime expression
`bool cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1);`
is byte-identical -- only the comment block changed.

**N-TC2 (test tighten).** `NeoStep14_TC2_CatchObjectAccess` was asserting only
`if (e != null) return 7;` (isinst was Step-15-blocked when TC2 was authored).
Tightened to `if (e is DivideByZeroException && e.Message != null) return 7;`
-- the `is` lowers to `isinst` on the caught exception (the type-check-in-catch
shape that is now supported). The `is DivideByZeroException` is tautological at
the C# type level (the catch already binds `DivideByZeroException e`) but
load-bearing at the IL level -- it forces the compiler to emit `isinst` against
the caught object. Catch clause type kept as `DivideByZeroException` (NOT
broadened to `Exception`); `return -1;` fallback retained.

**Verification.** CLI build (`Debug_Neo`) 0 errors; TestCases build (`Debug`,
`--no-incremental`) 0 errors. Full `NeoStep` smoke: `Ran 154 tests, 0 failed`
(TC2 itself in the green set). Did NOT run the optional Legacy-neutral confirm
(task 3.4) -- the change is Neo-comment + a test-source tighten that compiles
identically on both engines; the `isinst` arm is shared-engine and already
exercised by the broader Step 15 suite.

**Files edited (working tree UNCOMMITTED):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- `Cgt_Un`
  divergence comment only (runtime expression byte-identical).
- `TestCases/NeoStep14Test.cs` -- TC2 catch-body assertion tightened.

**Did NOT touch** `.trae/documents/neo-deferred-items.md` (the shipper moves
N-CGTUN / N-TC2 to the Resolved section at archive time, per the apply
instructions). **Did NOT git commit/push** (LEAD commits after review).

### Resolved -- `[N-CGTUN]` + `[N-TC2]` (neo-opportunistic-cleanup ship, 2026-07-06)

Both follow-ups closed by this trivial change (TRIVIAL: comment-only + test
tighten, no behavior change):

- **N-CGTUN** -- `Cgt_Un` divergence comment now names BOTH symmetric sentinel
  collisions (operand `cgt.un x, (uint)0xFFFFFFFF` -> `cguB == -1` -> true;
  source `cgt.un (uint)0xFFFFFFFF, x` -> `cguA != -1` clause -> false). Runtime
  expression byte-identical (comment-only).
- **N-TC2** -- Step 14 TC2 catch-body assertion tightened from `e != null` to
  `e is DivideByZeroException && e.Message != null`; the `is` lowers to
  `isinst`, exercising the type-check-in-catch shape (was Step-15-blocked when
  TC2 was authored; Step 15 `isinst` has since landed). TC2 asserts strictly
  more.

**Verification:** NeoStep smoke 154/154 green (TC2 stays green, now asserting
more). LEAD non-author diff-read APPROVED. Recorded RESOLVED 2026-07-06
(neo-opportunistic-cleanup) in `.trae/documents/neo-deferred-items.md` §2 rows
+ §3 entries (N-CGTUN also gets a §4 Resolved bullet). See
`openspec/changes/archive/2026-07-06-neo-opportunistic-cleanup/ship-log.md`.


## Findings -- neo-array-completion (2026-07-06, propose)

**Scoping decision (matches the load-bearing recommendation; verified against
code).** IN = (1) `Stelem_I` runtime arm, (2) generic-token
`Code.Ldelem`/`Code.Stelem` + native `Code.Ldelem_I`/`Code.Ldelem_U8` JIT
`Translate` enumeration, (3) F-4 other-width Stind/Ldind CLR-array branches.
DEFER multi-dimensional arrays (rank-2+) to a separate child
`neo-array-multidim` -- the rank-aware `Get`/`Set` callvirt + rank-aware frame
model + `new T[n,m]` constructor do NOT fall out of the rank-1 work and would
make the diff unreviewable (the explicit Step 13b / area4 lesson). All three
in-scope items are small, additive, low-regression-risk and close the rank-1
story. No `neo-array-multidim` spec capability is created here (the child will
introduce it); the proposal was re-scoped from a placeholder capability to
"None" + a `neo-arrays` non-goal boundary to avoid an empty spec.

**All 5 D-ARR gaps CONFIRMED against current code (HEAD).**
- `Stelem_I`: lowered by `Optimizer.Neo.cs:1010` (in the 3-register Stelem
  lowering list) and CIL `Code.Stelem_I` enumerated by JIT
  (`JITCompiler.cs:2226`), but NO runtime arm in the `ILIntepreter.Neo.cs`
  Stelem switch (`:2984-3049` has I1/I2/I4/I8/R4/R8/Ref/Any; no `Stelem_I`) ->
  catch-all Step-16 NIE.
- Generic-token + native NOT enumerated: JIT `Translate` Ldelem list
  (`JITCompiler.cs:2098-2109`) has I1/U1/I2/U2/I4/U4/I8/R4/R8/Any/Ref -- NO
  `Code.Ldelem`, `Code.Ldelem_I`, `Code.Ldelem_U8`; Stelem list (`:2226-2234`)
  has I/I1/I2/I4/I8/R4/R8/Ref/Any -- NO `Code.Stelem` -> JIT-time NIE.
- F-4: only `Stind_I4` (`:3123`) + `Ldind_I4` (`:3198`) carry the
  `else if (mStack[objIdx] is Array cArr)` branch from step17-completion; I1/
  I2/I8/R4/R8 + Ref variants fall to `GetNeoILInstance(mStack, objIdx)` ->
  `InvalidCastException` on a CLR `Array`. `Stind_I`/`Ldind_I` `goto
  Stind_I4`/`Ldind_I4` and thus INHERIT the array branch (no change needed).

**Key decisions locked.**
- D1: `Stelem_I` -> `goto case OpCodeREnum.Stelem_I4` (native int = I4 width
  on this VM; mirrors the existing `Stind_I` -> `Stind_I4` idiom). OQ2
  (resolve at apply): dump-confirm a `nint[]`/`UIntPtr[]` round-trip; if the
  pointer model is 8-byte, fall back to a dedicated typed arm (Option B).
- D2: each new JIT case gets the SAME 3-register `baseRegIdx`-decrement shape
  as the existing rank-1 cases (`JITCompiler.cs:2110-2113`). CIL->OpCodeREnum
  mapping: `Code.Ldelem`->`Ldelem_Any` (token resolved by the runtime arm),
  `Code.Stelem`->`Stelem_Any`, `Code.Ldelem_I`->`Ldelem_I4`,
  `Code.Ldelem_U8`->`Ldelem_I8`. OQ1: `Ldelem_U8` semantics (`ulong` vs native
  unsigned int) -- probe both at apply. `Translate` is SHARED (Legacy uses it
  too), but the new cases only ADD CIL codes that previously NIE'd -> Legacy
  byte-identical for existing paths; confirm via stash-toggle.
- D3: F-4 fix is per-arm (mirror the I4 precedent) rather than a centralized
  helper -- the arms are width-specialized typed locals; a helper would lose
  typing or churn the just-shipped I4 arms. Discriminator is unconditional
  (`is Array` runtime type test; re-affirms the opt-harden-2 / area4 insight
  that the operand kind determines the branch, no per-slot flag needed).
- D4: multi-dim is a separate child, NOT folded here.

**Files the implementer will touch (all Neo-only EXCEPT the JIT enumeration,
which is shared but additive; Legacy `ExecuteR` is the REFERENCE, NOT
modified):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- new
  `Stelem_I` arm; F-4 `is Array` branch in I1/I2/I8/R4/R8 + Ref Stind/Ldind
  arms (`:3099-3260`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- enumerate 4 CIL
  codes in `Translate` (`:2098` Ldelem block + `:2226` Stelem block). SHARED;
  Legacy-neutral (additive).
- `TestCases/NeoStep16Test.cs` (extend) -- `NeoStep16_*` adversarial probes
  (Stelem_I nint/UIntPtr; generic-token Ldelem/Stelem; Ldelem_U8 ulong; F-4
  I8/R4/R8/Ref CLR-array stind/ldind; regression int[]/float[]/object[]/
  IL-struct[]). Each new probe FAIL-on-HEAD stash-toggle (load-bearing).
- `openspec/specs/neo-arrays/spec.md` (archive step) + this planning-context
  + `.trae/documents/neo-deferred-items.md` (D-ARR rank-1 resolved; multi-dim
  pointer to `neo-array-multidim`).

**Regression risk: LOW-MEDIUM.** All changes additive (new arm + new JIT cases
that previously NIE'd; no existing rank-1 encoding/representation changes).
Gate: full `NeoStep` smoke (154/154 baseline) + Legacy-neutral stash-toggle
for the 4 new JIT cases. Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN
K1 / F-MAJ-1 lessons: a green smoke does NOT prove an array-kind discriminator
correct -- construct FAIL-on-HEAD stash-toggle probes per new path).

**Baseline note.** NeoStep smoke is 154/154 at HEAD (after neo-vt-ldflda-inline
/ neo-opportunistic-cleanup). Each new probe FAILS on HEAD (NIE or
InvalidCastException) and turns green after the fix -- proves load-bearing.

## Findings -- neo-array-completion (apply, 2026-07-06)

**RESOLVED.** D-ARR rank-1 array completion shipped. NeoStep smoke 161/161
(154 baseline + 7 new keeper probes; TC9 UIntPtr + TC15 ref-array omitted --
pre-existing upstream gaps). Legacy-neutral (stash-toggle: Legacy NeoStep 8
failures byte-identical with/without the change).

**Scoping collapse (the proposal's "5 gaps" -> 2 real gaps).** The proposal/
design named 5 rank-1 gaps. Reading the actual Mono.Cecil fork's `Code` enum
(Mono.Cecil/Mono.Cecil.Cil/Code.cs) at apply COLLAPSED them to 2:
1. **`Stelem_I` runtime arm missing** (the JIT `case Code.Stelem_I` at
   JITCompiler.cs:2226 was already present; the optimizer lists + LowerNeoOffsets
   already included Stelem_I; only the runtime arm in ILIntepreter.Neo.cs was
   absent -> Step-16 NIE).
2. **`Ldelem_I` missing EVERYWHERE** (JIT Translate case + runtime arm + 4
   optimizer-list entries in Optimizer.Utils.cs + 1 Neo-lowering entry in
   Optimizer.Neo.cs). The JIT default threw a generic NIE.

The other 3 proposed gaps are NON-ISSUES in this fork:
- `Code.Ldelem` (generic) does NOT EXIST -- opcode 0xa3 IS `Code.Ldelem_Any`
  here (already enumerated + handled).
- `Code.Stelem` (generic) does NOT EXIST -- opcode 0xa4 IS `Code.Stelem_Any`.
- `Code.Ldelem_U8` does NOT EXIST -- not a real ECMA opcode (an 8-byte unsigned
  load is just `Ldelem_I8`).
OQ1 (Ldelem_U8 routing) is MOOT -- no such opcode. Future proposals should
verify CIL-code existence in THIS fork's `Code` enum before designing JIT cases.

**OQ2 RESOLVED -- native-int is I4-width, but D1 Option A REJECTED.** The 4-byte
value round-trips correctly (Stelem_I writes val4=100/-7/4660; Ldelem_I reads
v=100/-7/4660 -- dump-confirmed via temporary runtime Console.WriteLine). BUT
`goto case Stelem_I4` (Option A) FAILS: the Stelem_I4 arm's typed-indexer casts
only handle int[]/uint[], so IntPtr[] hits the `((uint[])sa)[si]` fallback ->
InvalidCastException. Switched to D1 Option B: dedicated Stelem_I + Ldelem_I
arms dispatching on int[]/uint[]/IntPtr[]/UIntPtr[]. The design's "CLR indexer
boxes a 4-byte IntPtr correctly" assumption was WRONG for this runtime (it uses
direct casts, not Array.SetValue). LESSON: when a design offers Option A "reuse
the typed arm" vs Option B "dedicated arm", dump-confirm the typed arm's cast
list covers the new array kind BEFORE choosing A.

**Optimizer `Ldelem_I` omission cascade (5 additive edit sites).** Adding
`Code.Ldelem_I` to JIT Translate exposed FIVE further places enumerating the
Ldelem family WITHOUT Ldelem_I, each throwing a generic NIE on first contact:
Optimizer.Utils.cs GetOpcodeSourceRegister / GetOpcodeDestRegister /
ReplaceOpcodeSource / ReplaceOpcodeDest + Optimizer.Neo.cs LowerNeoOffsets. All
SHARED (Legacy-neutral: they only add handling for a previously-NIE'd opcode).
LESSON (durable): when adding a new opcode to the JIT Translate case list, grep
EVERY `Ldelem_<existing>` enumeration across Optimizer.*.cs and add the new
opcode to ALL of them -- the runtime arm alone is not enough; the optimizer's
register-source/dest + lowering passes each have their own per-opcode switch
that throws on unknowns. (This mirrors the F-MAJ-1 review-fix sweep pattern but
for JIT/optimizer lists rather than runtime representation.)

**Pre-existing gaps that blocked probes (accepted-known, NOT regressions):**
- **UIntPtr is an unsupported primitive.** `AppDomain.GetPrimitiveSize`
  (AppDomain.cs:1947) recognizes IntPtr (returns 8 -- note: NOT 4, despite the
  runtime treating native-int stores as 4-byte; the array element slot is
  separate from the value register) but NOT UIntPtr. Any UIntPtr-typed local/
  temp throws at AllocateLocalStackSpaces. TC9 (UIntPtr[]) omitted. The
  Stelem_I/Ldelem_I UIntPtr[] branches are correct but unreachable.
- **Neo `ldelema` NIEs on CLR ref-type arrays** ("ldelema on a CLR array with a
  reference-type element is deferred (use direct indexing)"). This sits
  upstream of the new Stind_Ref/Ldind_Ref `is Array` branch, so TC15 (string[]
  ref) omitted. The Ref branches are correct-by-construction (mirror the I4
  precedent) but unreachable until the ldelema ref-type gap closes (a Step 17
  follow-up, NOT D-ARR).
- **IntPtr value-comparison routes through CLR-struct-method calls**
  (op_Explicit / op_Equality) which hit the by-value-CLR-struct-param gap
  (`[NEO-IL-VT-INSTANCE-COVERAGE]` Step 6 family). TC8 asserts length +
  no-fault; value correctness proven by runtime debug output. On HEAD TC8 NIEs
  (no Stelem_I arm) -> load-bearing.
- **Multiple-simultaneous-8-byte-locals optimizer quirk.** Reading 3 long/
  double elements into separate locals and combining in one `if` yields 0 for
  all (pre-existing, unrelated to D-ARR). The incremental-assert pattern (read
  one element, fold into a running `bool bad`) works. LESSON: prefer
  incremental single-local assertions over multi-local-combined `if` for
  long/double round-trip probes.

**Stash-toggle (load-bearing proof).** With the 4 source files stashed:
TC8/TC12/TC13/TC14 FAIL-on-HEAD (Stelem_I NIE / Stind_I8/R4/R8 array-branch
absent -> InvalidCastException) -> PASS-after-fix. TC10/TC11/TC16 PASS
throughout (regression guards for already-working paths, as designed). This
matches the design's intent (some probes are regression guards, not all are
load-bearing).

**Files edited (working tree UNCOMMITTED):**
- ILIntepreter.Neo.cs -- Stelem_I + Ldelem_I runtime arms (Option B dispatch);
  `is Array` branch added to Stind_I1/I2/I8/R4/R8 + Ldind_I1/U1/I2/U2/U4/I8/
  R4/R8 + Stind_Ref/Ldind_Ref.
- JITCompiler.cs -- `case Code.Ldelem_I:` (3-register shape, no op.Code
  rewrite -- direct cast to OpCodeREnum.Ldelem_I).
- Optimizer.Utils.cs -- Ldelem_I added to 4 lists.
- Optimizer.Neo.cs -- Ldelem_I added to LowerNeoOffsets Ldelem block.
- TestCases/NeoStep16Test.cs -- 7 keeper probes + 2 documented omissions.

**Multi-dim DEFERRED to `neo-array-multidim` (separate child).** Rank-2+ arrays
untouched (the rank-aware Address/Get/Set callvirt + frame model + new T[n,m]
construction). Out of scope for D-ARR rank-1; tracked separately.

**Did NOT git commit/push** (per process discipline; LEAD commits after review).

### Follow-ups discovered (from neo-array-completion, 2026-07-06)

- **`neo-array-multidim`** (portfolio task #20) -- multi-dimensional arrays
  (rank-2+). DEFERRED from D-ARR. The rank-aware `Address`/`Get`/`Set`
  `callvirt` ABI, the rank-aware frame model, and `new T[n,m]` construction do
  NOT fall out of the rank-1 work; they are a substantially larger change.
  Stays an untagged JIT `NotImplementedException` today. Tracked in
  `.trae/documents/neo-deferred-items.md` (D-ARR §3 STILL DEFERRED).
- **`neo-double-combine-quirk` / [OPT-HARDEN-3]** (portfolio task #21) -- 2+
  `double` locals combined in one boolean expression silently misfire (F-MAJ-1
  class; `double`-specific, not all 8-byte primitives; 3 `long` locals
  combined work fine). Surfaced as the array-completion review's Finding F-1
  (Major, PRE-EXISTING, upstream of D-ARR). Suspect: `AllocateLocalStackSpaces`
  8-byte-primitive slot handling or copy-prop `double`-local combine; exact
  locus NOT pinned (dump-gate at apply). Tracked in
  `.trae/documents/neo-deferred-items.md` (F-8 / NEO-DOUBLE-COMBINE).
- **UIntPtr unsupported primitive** (accepted-known, upstream of D-ARR) --
  `AppDomain.GetPrimitiveSize` (AppDomain.cs:1947) recognizes `IntPtr` but
  NOT `UIntPtr`; any `UIntPtr`-typed local/temp throws at
  `AllocateLocalStackSpaces`. The `Stelem_I`/`Ldelem_I` `UIntPtr[]` arms are
  correct but unreachable. Route: a future primitive-support follow-up (low
  priority; rare in C# output).
- **Neo `ldelema` NIEs on CLR ref-type arrays** (accepted-known, upstream of
  D-ARR) -- sits upstream of the new `Stind_Ref`/`Ldind_Ref` `is Array`
  branch, so no C# shape can reach the Ref array branch today. Route: the
  Step-17 stobj-refloop / CLR-object field-hash follow-up
  (`neo-step17-stobj-refloop`, task #18) -- same family as the D-CONSTRAINED
  stobj-refloop / CLR-object-field-hash deferral.

## Findings -- neo-double-combine-quirk (2026-07-06, propose)

Closes **F-8 / NEO-DOUBLE-COMBINE** (the silent-wrong-result quirk surfaced
by the neo-array-completion review, Finding F-1). The proposal is DUMP-GATED
(mirrors the F-MAJ-1 / OPT-HARDEN-2 discipline: probe BEFORE designing the
fix; STOP if the designed fix is wrong; never force a fix the dump does not
confirm). The fix lands ONLY if an apply-time JIT/optimizer dump on current
HEAD (`fe13c25e`) pinpoints the defect; otherwise F-8 -> DEFERRED (the
Q-STRUCT / Q-LONG / Q-NEWOBJ outcome).

**Refined characterization (from the array-completion review's independent
reproduction).** The quirk is NOT "3+ locals" and NOT all 8-byte primitives
-- it is **2+ `double` locals combined in one boolean expression** that
yields a silent wrong result. A single `double` read is correct; combine two
in one `if` and the comparison misfires. **3 `long` locals combined work
fine** -- the quirk is `double`-specific. This is the F-MAJ-1 class (silent
wrong result) but on an 8-byte PRIMITIVE, not a CLR struct.

**Key code-grounded finding (the discriminator, established at propose).**
The F-8 symptom (`long` works, `double` fails) CANNOT be a frame-slot sizing
or alignment defect: `AllocateLocalStackSpaces` (`JITCompiler.cs:1561-1574`)
sizes BOTH `double` and `long` to 8 bytes (`GetPrimitiveSize`,
`AppDomain.cs:1898-1921`) and aligns BOTH to 8 (`AlignUp(offset, size)`,
`size=8` for both). They get byte-identical frame slots. So the divergence is
in the COMPUTATION path (R8 type-specialization / R8 compare / R8 copy-prop),
NOT the slot sizing. This mirrors the F-MAJ-1 lesson
(`AllocateLocalStackSpaces` monotonic allocation was NOT the F-MAJ-1 fix
site). The spec records this so a future "fix the 8-byte-primitive slot
allocator" proposal is rejected on the same grounds.

**F-MAJ-1 vs F-8 distinction (durable).**
- **F-MAJ-1** = a CLR-struct-local REPRESENTATION MISMATCH (the declare-side
  `Size=4, RefCount=1` boxed-ref disagreed with the D6 return-write's flat
  12-byte write -> 8-byte overflow into the neighbour). Fixed by declaring a
  CLR-VT local as flat bytes (Option B, gated `#if ENABLE_NEO_MODE`).
- **F-8** = an R8 COMPUTATION-path defect on an 8-byte PRIMITIVE (`double`).
  A `double` local is a primitive, sized 8 and stored flat in the frame
  (byte-identical to `long`). There is NO representation mismatch -- the F-MAJ-1
  mechanism does NOT apply. The defect is `double`-specific because the I8
  (long) computation path works; the divergence is in the R8 type-spec / R8
  compare / R8 copy-prop arm. The two bugs are the same SEVERITY class (silent
  wrong result) but DIFFERENT root-cause families.

**Leading candidate root cause (D2, dump-gated).** The `||`/`&&` combine
lowers through the typed-compare machinery (`GetTypedCompareOpcode`,
`GetTypedBranchOpcode`, `GetTypedImmediateCompareOpcode`,
`JITCompiler.cs:1182-1346`). All three HAVE an R8 case. But an R8 operand
flowing into the combine (e.g. from `Ldelem_R8`, which may lack the dest-type
seed that `Conv_R8` has at `JITCompiler.cs:776`) may be read by the compare
type-specialization BEFORE its `registerType` is seeded -> `InferPrimTag`
(`:1052-1086`) falls back to `I4` -> the compare emits `Ceq_I4`/`Bne_Un_I4`
(reading 4 bytes of an 8-byte `double` slot) -> silent wrong result. This
fits the `double`-specific signature exactly (the I8 path is exercised by
the working 3-`long` case, so I8 is correct). The fix = the missing producer
dest-type seed (mirror the `Conv_R8` / `Ldloca` / `Ldflda` dest-typing rules;
the `neo-vt-this-addr` "Newobj-dest typing" fix is the precedent). SHARED
type-spec pass: confirm Legacy-neutral (the seed is byte-identical for every
existing I4/I8 operand, only ADDING a correct R8 seed) OR gate
`#if ENABLE_NEO_MODE`.

**Fallback candidates (dump-gated, in order).**
- D3: R8 copy-prop fold width-mishandles a `double` (FCP/BCP/copy-prop, all
  SHARED -- gate `#if ENABLE_NEO_MODE`). Confirm only if D2 is refuted (the
  emitted opcodes are already `*_R8` but a `double` value is re-materialized
  at a 4-byte width after the fold).
- D4: `LowerNeoOffsets` R8 operand overlap (Neo-only). Refuted in principle
  (lowering is width-agnostic -- advances by the slot's declared `Size=8` for
  both `double` and `long`), but the dump confirms.
- D5 (defer): if D2, D3, AND D4 are all refuted, F-8 -> DEFERRED. Ship NO
  guessed fix. Pin the dump + reproducer.

**Key decisions locked.**
- D1: the fix is dump-gated; the 8-byte-slot-sizing hypothesis is REFUTED at
  propose (`double` and `long` get byte-identical frame slots).
- D2: leading fix = missing R8 producer dest-type seed in the type-spec pass
  (the dump-named producer case, likely `Ldelem_R8`). SHARED -- confirm
  Legacy-neutral OR gate `#if ENABLE_NEO_MODE`.
- D6: test convention = `NeoOptHardTest_Dbl_*` probes in
  `NeoOptHardeningTest.cs` (NOT promoted to `NeoStep`; the smoke stays 161/161
  green pre-fix). MANDATORY adversarial probes (the silent-corruption class --
  green smoke MISSES it): the F-8 signature + 3-double + double+long boundary
  + double+int + isolated + live-range + single-local regression + F-MAJ-1
  probes still green + full NeoStep smoke 161/161.

**Files the implementer will touch (dump-locked; Legacy is the REFERENCE):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- the R8
  type-spec cases (`:1182-1346`) + the producer dest-typing loop (`:537-847`)
  is the LIKELY fix site for D2. SHARED -- Legacy-neutrality gate.
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.FCP.cs` /
  `Optimizer.BCP.cs` / copy-prop -- ONLY if D3 is dump-confirmed (SHARED;
  gate `#if ENABLE_NEO_MODE`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` --
  `LowerNeoOffsets` ONLY if D4 is dump-confirmed (Neo-only).
- `TestCases/NeoOptHardeningTest.cs` (extend) -- `NeoOptHardTest_Dbl_*` probes
  (separate filter, mirrors K1 / F-MAJ-1).

**Regression risk: MEDIUM.** The fix site is the R8 computation path (NOT the
broad frame allocator touched by F-MAJ-1; the slot sizing is provably
correct). Gate: full `NeoStep` smoke (161/161 baseline) + Legacy 518/519
stash-toggle for any shared-engine edit. Adversarial probes MANDATORY (Step 17
B1 / OPT-HARDEN K1 / F-MAJ-1 lessons: a green smoke does NOT prove an
optimizer/type-spec gate correct).

**Baseline note.** NeoStep smoke is 161/161 at HEAD (after neo-array-
completion). The F-8 reproducer FAILS on HEAD (array-completion reviewer
stash-reproduced it); the `Dbl_*` probes run under a separate filter. The
array-completion TC11/TC12/TC14 already use the incremental `bad`-fold
workaround in the smoke, so the smoke stays green pre-fix.

## Findings -- neo-double-combine-quirk (apply, 2026-07-06)

**RESOLVED.** F-8 / NEO-DOUBLE-COMBINE FIXED. NeoStep smoke 161/161 (no
regression); NeoOptHard 24/24 (16 K1/F-MAJ-1 + 8 new Dbl); Legacy-neutral (all
8 Dbl probes PASS on Legacy too; the bug was Neo-only). Working tree
UNCOMMITTED.

**VERDICT: candidate D4 (LowerNeoOffsets operand-union overlap), NOT D2.** The
propose-time LEADING candidate (D2 R8 type-spec mis-types the combine) was
REFUTED by the dump; the actual defect is a D4 shape the propose dismissed as
"long shot, refuted in principle" -- and the propose D4 refutation ("lowering
is width-agnostic") was CORRECT for the field it considered and WRONG about
the dead `Operand3` field write it did NOT consider.

**Dump evidence (Block 0).**
- D2 REFUTED: a JIT type-spec diagnostic showed the two `Ldelem_R8` dest
  registers ARE correctly seeded `System.Double`, and the emitted compare/
  branch opcodes are the correct R8 forms (`Bnei_Un_R8`, `Ceqi_R8`).
- D4 CONFIRMED: a runtime diagnostic in the `Bnei_Un_R8` arm showed
  `val=1.5 opd=2.121995791E-314 -> branch=True` -- the compared VALUE is
  correct, but the IMMEDIATE CONSTANT `opd` (should be 1.5) is GARBAGE (a small
  int's bit pattern as a double).

**The defect (code-grounded).** `OpCodeR` is `[StructLayout(LayoutKind.Explicit)]`:
`Operand3` (int) @ offset 16 OVERLAPS the HIGH 4 bytes of `OperandLong`/
`OperandDouble` (offset 12-19). In `Optimizer.Neo.cs LowerNeoOffsets`, the
immediate-branch case stamped `op.Operand3 = localInfos[r1].RefOffset` for
EVERY immediate branch (I4/I8/R4/R8). `Operand3` is NEVER READ by any
immediate-branch runtime arm (each reads only `DstOffset` + the immediate
field + `Operand4`). The write is DEAD -- but DESTRUCTIVE for the I8/R4/R8
forms: it clobbers the high 4 bytes of the 8-byte immediate constant.

**The long-works / double-fails discriminator RESOLVED (NOT slot sizing).**
- double: copy-prop folds `Ldc_R8 1.5` INTO the immediate form (`Bnei_Un_R8`)
  -> the `Operand3` write corrupts `OperandDouble` -> silent wrong branch.
- long: copy-prop keeps `Ldc_I8` in a register, emits REGISTER-REGISTER
  `Bne_Un_I8` (dump of ThreeLongCombine confirmed). The I8 immediate branch is
  never produced for the long combine, so the corruption is unreachable.
- single double (no combine): does not produce `Bnei_Un_R8` (lowers to
  `ceqi.r8; brfalse`; `Ceqi_R8` uses `LowerR1R2` which does NOT write
  `Operand3`). Only the combined `||` form trips the bug.

So the discriminator is: **does the optimizer's constant-folding produce a
wide-immediate branch form?** For double yes; for long no. It is a property of
the constant-folding, NOT the frame layout (re-confirms the propose-time
slot-sizing refutation: `double` and `long` get byte-identical 8-byte slots).

**The fix (Neo-only, 1 conditional).** `Optimizer.Neo.cs LowerNeoOffsets`
immediate-branch case: a single `immLarge` boolean gates the existing
`op.Operand3 = localInfos[r1].RefOffset` line OFF for the I8/R4/R8 forms.
The I4 forms keep the write byte-identical (their immediate `Operand` @8 does
not collide). The whole file is `#if ENABLE_NEO_MODE`; Neo-only by
construction. NO type-spec / copy-prop / runtime change. (+39/-1.)

**Stash-toggle proof.** Stashing JUST `Optimizer.Neo.cs` and rebuilding the
CLI reproduces the F-8 failure on stashed HEAD (`TwoDoubleCombine` -> 1
failed); restoring it turns it green. Proves pre-existing (HEAD `fe13c25e`)
+ load-bearing.

**F-MAJ-1 vs F-8 distinction (durable).**
- F-MAJ-1 = CLR struct LOCAL representation mismatch (boxed-ref `Size=4,
  RefCount=1` vs flat-bytes write). A 12-byte flat write overflowed a 4-byte
  boxed-ref slot into the neighbour. Fixed by declaring a CLR-VT local as flat
  bytes under `#if ENABLE_NEO_MODE`.
- F-8 = R8 immediate-branch constant corruption via the OpCodeR explicit-
  layout union (a DEAD `Operand3` write at offset 16 clobbers the high 4 bytes
  of the 8-byte `OperandDouble`/`OperandLong` immediate at 12-19). A
  COMPUTATION-path defect on an 8-byte PRIMITIVE, NOT a representation
  mismatch -- the frame slots are byte-identical to `long`. Fixed by gating
  the dead `Operand3` write off for the wide-immediate forms.

**Lesson re-affirmed (the K1 / F-MAJ-1 / Q-NEWOBJ / F-6 family).** The propose
ranked D2 LEADING and D4 "refuted in principle". The dump REFUTED D2 and
CONFIRMED D4 -- but a D4 shape the propose did NOT enumerate (a dead field
write colliding via the union). **"Refuted in principle" must enumerate EVERY
field the case writes, not just the ones the design focused on.** The
`OpCodeR` explicit-layout union (`Register1`/`DstOffset` @4, `Register2`/
`SrcOffset` @6, `Operand`/`OperandFloat` @8, `Operand2`/`OperandLong`/
`OperandDouble` @12, `Operand3` @16, `Operand4` @20) is a recurring sharp
edge: ANY optimizer pass that writes a "spare" field MUST verify it does not
alias a wide-immediate/long/double field used by another consumer. Future
optimizer hardening should grep for `op.Operand2 =`/`op.Operand3 =` writes and
cross-check against the runtime arms' field reads.

**Files touched (all Neo-only; Legacy `ExecuteR` byte-identical, the file is
`#if ENABLE_NEO_MODE`):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- the fix.
- `TestCases/NeoOptHardeningTest.cs` -- 8 `NeoOptHardTest_Dbl_*` probes.
- `openspec/changes/neo-double-combine-quirk/{proposal,design,tasks,ship-log}.md`
  + `specs/neo-optimizer/spec.md`.

**Did NOT git commit/push** (LEAD commits after review). Did NOT update
`neo-deferred-items.md` (the shipper does at archive).

**OpCodeR union gotcha (NEW durable insight).** The explicit-layout union means
a pass that writes `Operand` (4B @8), `Operand2` (4B @12), or `Operand3` (4B
@16) can SILENTLY clobber part of `OperandFloat` (@8), `OperandLong`/
`OperandDouble` (@12-19), respectively. The reverse is also true: writing
`OperandDouble`/`OperandLong` clobbers `Operand2` + `Operand3`. A consumer
that reads `OperandDouble` and a pass that writes `Operand3` are MUTUALLY
DESTRUCTIVE even though they appear to use "different" fields. This is the F-8
mechanism in one sentence. The `LowerNeoOffsets` immediate-branch case is now
gated, but OTHER cases in the same pass (or other passes) that write `Operand2`/
`Operand3` for an opcode whose runtime arm reads `OperandDouble`/`OperandLong`
have the SAME latent hazard. Flag for future optimizer-hardening sweeps.

### Shipper resolution (archive, 2026-07-06)

- **F-8 / NEO-DOUBLE-COMBINE -> RESOLVED.** Archived as
  `openspec/changes/archive/2026-07-06-neo-double-combine-quirk/`. The F-8
  requirement merged into the canonical `openspec/specs/neo-optimizer/spec.md`
  (ADDED: "Combining 2+ double locals in one boolean expression computes the
  correct result"); the 4 pre-existing neo-optimizer requirements (FCP K1,
  Q-STRUCT, Q-LONG, F-MAJ-1) are preserved unchanged.
- **F2 accepted-known (no fix needed).** The `immLarge` set in
  `LowerNeoOffsets` includes the 10 R4 immediate-branch forms unnecessarily --
  R4's `OperandFloat` (@8-11) is disjoint from `Operand3` (@16), so the dead
  `Operand3` write was already harmless for R4. The destructive set is
  strictly I8 + R8; R4 is included for uniform "I4 keeps the legacy write,
  everything else skips it" auditability. Recorded; no fix shipped (review F2
  = Trivial).
- **OpCodeR union gotcha reinforced.** 3rd concrete instance (Step 12 frame-VT
  offsets, OPT-HARDEN K1 Move/Move_Vt, now F-8 immediate-branch). Future
  optimizer work: any `LowerNeoOffsets` case that stamps an `OpCodeR` field
  MUST enumerate every overlapping field via the explicit layout and confirm
  none is the live payload for that opcode's runtime arm -- a "dead" write can
  still be destructive through the union. Grep `op.Operand2 =` / `op.Operand3
  =` and cross-check against runtime-arm field reads.
- `neo-deferred-items.md` F-8 row (§2) + entry (§3) updated to
  `RESOLVED 2026-07-06 (neo-double-combine-quirk, D4)`; §4 bullet added.

## Findings -- neo-step13-area4-refandstind (2026-07-06, propose)

**Scoping decision (D1): SHIP 4c + 4d together; F-7 deferred unless it falls
out at apply (OQ2).** 4c (CLR-method `ref`/`out` typed-ref bridge) and 4d
(CLR-object `stind`/`ldind`/`stobj`/`ldobj` via field identity) are
independent plumbing that do NOT share fix sites, but each is small (4c = a
byref arm in each of 2 readers + a write-back epilogue; 4d = a new `else`
branch in the stind/ldind/stobj/ldobj arms + a CLR-field-identity stamp in
`Ldflda`). Together they stay reviewable and form one cohesive
CLR-binding/byref completion (closes D-13B Area 4 4c+4d). F-7
(NEO-DELEGATE-REFOUT) depends on the SAME byref-marshaling primitives as 4c
but lives in a THIRD site (`DelegateAdapter.NeoInvokeSub` /
`WriteNeoCallSlot`); it is a stretch ONLY if 4c's shared helper factors
cleanly (OQ2 at apply). Default lean: defer F-7 to its own follow-up child,
recording that 4c's helper is the ready-made primitive. This mirrors the
portfolio's serial policy + the 13b "don't bundle the unreviewable" lesson.

**Current-state assessment (code-grounded on HEAD `2ca6614f`).**
- **4c reflection fallback (`CLRMethod.Invoke(byte*)`, `CLRMethod.cs:407-474`)
  = SILENT-WRONG.** The param loop has NO `IsByRef` check. For `ref int`,
  `t = pt.TypeForCLR` strips the byref modifier -> `int` -> reads 4 bytes via
  `ReadNeoInt32` = the Ref Slot's `objectIndex` half (garbage); CLR
  `MethodInfo.Invoke` mutates the boxed copy; NO write-back to the caller's
  local. Same shape for `ref struct`/`out T`.
- **4c autogen (`AppendArgumentCodeNeo`, `BindingGeneratorExtensions.cs:135`)
  = SILENT-WRONG + a load-bearing dead-discriminator codegen bug.** The
  `pt.IsByRef` check at `:173` is DEAD: `pt` is already de-byref'd at `:140`
  (`var pt = p.IsByRef ? p.GetElementType() : p;`), so `pt.IsByRef` is ALWAYS
  false. The branch that fires for a byref param is... none — byref params
  fall through to the primitive/struct/ref dispatch keyed on the ELEMENT type,
  which mis-reads the 8-byte Ref Slot. The fix MUST key on `p.IsByRef` (the
  original param type), NOT `pt.IsByRef`. This is the D2 load-bearing detail;
  recorded so the apply-phase does not re-introduce the dead discriminator.
  Even when keyed correctly, the current arm body emits `default(...)` + a
  `// TODO: ByRef ... DEFERRED` comment = silent-wrong, no write-back.
- **4d = CLEAN Step-17/13b NIE (not InvalidCastException).** A `ref
  clrObj.field` via `ldflda` -> `stind`/`ldind` reaches the consumer `else`
  branch (non-frame-native, non-Array), which calls `GetNeoILInstance`
  (`ILIntepreter.Neo.cs:3890-3903`); that helper throws a clean NIE
  ("Step 17/13b: field/element access on a CLR object via the IL-instance path
  is deferred (CLR field-hash plumbing lands in Step 13b)") rather than a raw
  cast. The `Ldflda` arm (`:877-883`) stamps `(objIdx, field.PrimitiveOffset)`
  for the CLR operand, but `field.PrimitiveOffset` is meaningless for a CLR
  field (no `Primitives[]`) — so the fix must ALSO stamp the CLR field
  identity (D4).

**Design (load-bearing decisions).**
- **D3 unified field-accessor primitive.** 4c's mStack-object byref
  (`ref heapIlObj.field` / `ref clrObj.field`) and 4d's `ldflda`-produced
  CLR-object Ref Slot are the SAME operation (read/write a field via an
  identity resolved from the Ref Slot offset half). Factoring a shared
  `ReadNeoFieldRef`/`WriteNeoFieldRef` (or `MarshalNeoByrefArg`) helper keeps
  both arms small + consistent and gives F-7 a ready-made primitive.
- **D2 discriminator = the param's type token (`p.IsByRef`/`pt.IsByRef` on the
  `IType`, `ParameterType.IsByRef` on the CLR `ParameterInfo`).** Per-arm
  type-token discriminator (13b / opt-harden-2 insight): no per-slot runtime
  flag stamped at lowering.
- **D4 CLR-field identity stamp in `Ldflda`.** Option A (PREFERRED): resolve
  the CLR `FieldInfo` at JIT time + stamp its `MetadataToken`/cache-index into
  the offset half; consumer resolves token -> `FieldInfo` ->
  `GetValue`/`SetValue`. Option B fallback (domain-cached list index) if the
  JIT dump (OQ1) shows the `Ldflda` operand carries only an IL-side `IField`
  wrapper. IL heap-field stamp (`field.PrimitiveOffset`) unchanged.
- **D5 write-back gating.** Reflection: gate on `!IsIn || IsOut` (a
  `readonly`/`in` param is not written back). Autogen: emit write-back for
  `ref`/`out` (an `in`-only byref is rare; a no-op write-back is harmless).
  Mirrors Legacy's `shouldFreeParam = hasByRef ? "false" : "true"`.
- **D6 reflection `ref struct` uses boxed-copy semantics.** CLR
  `MethodInfo.Invoke` boxes a struct, mutates the box, returns; the write-back
  re-flattens via `WriteNeoValueType` (the 4b pattern at `CLRMethod.cs:493,517`).
  OQ3: does `ref int` observe the box mutation through `object[]`, or does it
  need a separate byref channel? Legacy uses `Reference*` pushees (not an
  oracle); probe at apply.

**Capability spec home: `neo-byref` (NOT `neo-boxing`).** 4c is the IL-to-CLR
byref call-ABI; 4d is the stind/ldind/stobj/ldobj consumer + the ldflda field-
identity producer — all `neo-byref`'s Ref-Slot/byref territory. `neo-boxing`'s
deferral sentence ("the byref CLR crossing ... and CLR-object stind/ldind via
field hash remain deferred") is updated at archive to note both are now closed
by this change (cross-reference, no `neo-boxing` delta needed). Delta =
`neo-byref` only: 1 ADDED (`CLR-method ref/out parameter marshaling`) + 4
MODIFIED (`stind/ldind/stobj/ldobj dispatch`; `ldflda/ldarga`; `ref/out
IL-parameter call ABI`; `Deferred byref sub-cases throw tagged NIE` — 4c/4d
struck from the deferred list).

**Files to touch (all Neo-only; Legacy `ExecuteR`/`AppendArgumentCode` are the
REFERENCE, untouched):**
- `ILRuntime/CLR/Method/CLRMethod.cs:407-474` — reflection byref arm +
  post-call write-back.
- `ILRuntime/Runtime/CLRBinding/BindingGeneratorExtensions.cs:135-237` —
  autogen byref codegen (key on `p.IsByRef`, NOT `pt.IsByRef` — D2) +
  write-back epilogue helper.
- `ILRuntime/Runtime/CLRBinding/MethodBindingGenerator.cs:249-310` — wire the
  write-back epilogue into the autogen wrapper.
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` —
  `Ldflda` CLR-field-identity stamp (`:877-883`) + stind/ldind/stobj/ldobj
  CLR-object-field `else` branch (`:3142-3407`) + shared field-accessor
  helper (D3).
- `DelegateAdapter.cs` (F-7 stretch only, OQ2).
- `TestCases/NeoStep13bTest.cs` (4c `NeoStep13_*`) + `NeoStep17Test.cs` (4d
  `NeoStep17_*`).

**Regression risk: MEDIUM.** 4c touches the shared CLR-binding codegen
(`*Neo` variants on every Neo CLR call); the `p.IsByRef` discriminator fires
ONLY for a byref-typed param (every by-value path byte-identical). 4d is an
additive `else` branch (frame-native + Array + ILTypeInstance paths
unchanged). Gate: full NeoStep smoke (161/161) + Legacy 518/519 stash-toggle.
Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1 / the
double-combine-quirk lessons). Biggest design risks: the D2
dead-discriminator codegen bug (must key on `p.IsByRef`), the D4
field-identity/JIT-resolvability dump gate (OQ1), and the D6 reflection
`ref int` boxing channel (OQ3) — all dump-gated at apply, STOP-if-wrong
discipline binding.

## Findings -- neo-step13-area4-refandstind (apply, 2026-07-06)

**RESOLVED.** 4c (CLR-method ref/out typed-ref bridge) + 4d (CLR-object
stind/ldind/stobj/ldobj via field identity) shipped. Neo smoke 175/175 (161
baseline + 14 new probes: 8 reflection 4c + 1 NIE + 5 4d). Legacy-neutral
(plain Debug builds clean; new probes pass on Legacy; the 8 Legacy NeoStep
failures are all pre-existing). Working tree UNCOMMITTED.

**Scoping decision: 4c + 4d shipped; F-7 (delegate ref/out) DEFERRED.** 4c
and 4d are independent plumbing that share the D3 field-accessor primitive.
F-7 lives in a THIRD site (`DelegateAdapter.NeoInvokeSub` /
`WriteNeoCallSlot`) in the CLR->IL callback direction -- the 4c helper (IL->CLR
deref-at-copy-site) does NOT trivially route through it. F-7 stays OPEN (the
shipper records it in neo-deferred-items at archive).

**Current-state assessment confirmed (both gaps real + load-bearing).**
- 4c SILENT-WRONG in BOTH readers (the reflection fallback read the byref Ref
  Slot's objectIndex half as the value + no write-back; the autogen emitted
  `default(...)` for the dead `pt.IsByRef` arm + no write-back).
- 4d a clean NIE (`GetNeoILInstance` threw for a CLR-object-field byref).

**DUMP-GATE D4 + D6 RESOLVED (OPT-HARDEN K1 / double-combine-quirk discipline
held).**
- **D4 (FieldInfo at JIT): CONFIRMED, NO JIT change needed.** The `Ldflda` arm
  already stamps `fieldPrimOff = type.GetFieldIndex(token)` = the CLR FieldInfo
  hash for a CLR declaring type. At runtime, `((CLRType)appdomain.GetType
  (obj.GetType())).GetFieldValue(hash, obj)` resolves it. The 4d consumer adds a
  `NeoIsClrObject` discriminator branch (before the ILTypeInstance fallback)
  routing to `NeoReadClrObjectField`/`NeoWriteClrObjectField` (CLRType.
  GetFieldValue/SetFieldValue by hash). Additive; IL heap-field + Array +
  frame-native byte-identical.
- **D6 (reflection `ref int` boxing): NOT APPLICABLE.** The area4b blocker
  applies identically: the readers (`CLRMethod.Invoke(byte*)` + the autogen
  redirect) only receive `targetBase` (callee param region), NOT the caller
  `frameBase` (threading it would break the checked-in delegate signature). So
  4c uses the SAME deref-at-copy-site mechanism as area4b -- the reflection
  `ref int` boxing channel never arises (the reader reads FLAT BYTES, not a
  byref; CLR mutates the box; the reflection write-back re-flattens `param[i]`
  into the dest slot; `CopyNeoCallThisBack` propagates it).

**The D2 dead-discriminator (HIGHEST 4c risk) FIXED, keyed on `p.IsByRef`.**
The old `AppendArgumentCodeNeo` `pt.IsByRef` arm was DEAD (`pt` is de-byref'd at
the top). The fix keys the WRITE-BACK epilogue (`AppendNeoWriteBackCode`) on
`p.IsByRef` (the raw ParameterType). The READ dispatches on the element type
(`pt`). Confirmed via the dump-noise-gated diagnostic (the byref codegen fires
for `ref int`; by-value byte-identical).

**KEY IMPLEMENTATION DEVIATION from the design's literal D2/D3 (reader-deref):
4c uses DEREF-AT-COPY-SITE (the area4b pattern).** The design assumed the
readers deref the byref; they lack the caller frameBase (area4b blocker). The
optimizer call-lowering sizes a CLR-callee byref param's dest by ELEMENT type +
flags it; `CopyNeoCallArguments` derefs (frame-native CopyBlock OR mStack-
object via the field accessor); the reader reads flat bytes; `CopyNeoCallThisBack`
reverse-copies for ref/out (gated `!IsIn || IsOut`). The IL-callee byref path
is UNCHANGED (its callee region keeps the 8-byte Ref Slot, read by ExecuteNeo
as a byref local -- the Step 17 path; the byref flag fires only for CLR
callees).

**D3 unified field accessor: `NeoMarshalByrefFieldToSlot` (read+write).** Used
by BOTH 4c's mStack-object byref deref AND 4d's stind/ldind CLR-object field
path (via the `NeoReadClrObjectField`/`NeoWriteClrObjectField` thin wrappers).
ILTypeInstance -> Primitives[off]; CLR object -> GetFieldValue/SetFieldValue
by hash; Array -> NIE-tagged (owned by the stind/ldind array arm).

**F-7 routing: DEFERRED.** The helper is IL->CLR (deref at the IL call site);
F-7 is CLR->IL (WriteNeoCallSlot in DelegateAdapter.NeoInvokeSub). Different
direction + site; the helper does NOT trivially route through. F-7 stays OPEN.

**Two pre-existing gaps surfaced + addressed/avoided.**
1. **Null-ref-param reflection read (FIXED in passing).** `CLRMethod.Invoke`'s
   reference-param read did `mStack[idx]` with no null check; a null ref param
   (mStack index -1) threw OOB. Fixed to materialize null for idx<0. Surfaced
   by the 4d probes passing a null string to `MakeArea4dHolder`.
2. **Inlined-IL-method-return-move misclassification (AVOIDED, not fixed).**
   An inlined IL method returning an `ldind.i4` result triggers a return-Move
   that moves the int as a reference (mStack[intValue] OOB). Unrelated to 4d
   (the inliner's return-value classification). The 4d.2 probe drives the
   ldflda;ldind path via a non-trivial helper body (`int v = slot; return v +
   0;`) that defeats the trivial-inliner. Flagged as a separate follow-up.

**CLI filter + dump-noise gotchas re-affirmed.** The ILRuntimeTestCLI name
filter is a plain `Contains` (no regex/`|`); run each probe name separately.
The JIT-dump flood from `OUTPUT_JIT_RESULT` is useless for tracing a specific
arm -- gate a temp `Console.WriteLine` INSIDE the runtime arm (fires only when
that arm executes for the probe), run the single probe, then remove it. This
was how the byref-slot shape + the D6 frameBase-blocker + the reflection
read-arm mis-dispatch were confirmed.

**Lesson re-affirmed (OPT-HARDEN K1 / double-combine-quirk / vt-this-addr).**
The propose-time design assumed reader-deref; the apply-time DUMP refuted it
(the readers lack the caller frameBase). The dump-gated discipline held: probe
the IR + Ref Slot contents BEFORE designing, STOP if the designed fix is wrong,
and reuse the area4b deref-at-copy-site pattern instead of forcing a guessed
reader-deref. The fix deviated from the design's mechanism but BOTH the
propose-time reader-deref and the area4b copy-site approaches were testable
from the dump -- the process worked exactly as designed.

**Spec deltas (the shipper syncs at archive).** The `neo-byref` MODIFIED
deltas: 4c (CLR-method ref/out call-ABI bridge) + 4d (CLR-object stind/ldind/
stobj/ldobj via field identity) delivered. F-7 (delegate ref/out) stays OPEN.

## Findings -- neo-step17-stobj-refloop (2026-07-06, propose)

**Scope decision: (b) Stobj/Ldobj ref-loop + the IL-VT-with-ref-fields constrained
sub-case IN; (c) generic-byref / fixed / interface-on-VT-constrained DEFERRED
(parked as NIE-tagged edges; none exercised by smoke, none reachable from the
common C# shape).** This matches the scoping recommendation. The two (b)
sub-cases share a SINGLE root mechanism (the byref source must carry the struct's
ref-region mStack base, OR the runtime must recover it from the source local's
frame layout), so bundling them is one correctness surface -- splitting would
force two passes over the same operand-stamping/Ref-Slot-extension. The (c)
edges are independent (a generic-param type-token discriminator; a pinned-byref
flag; an interface-dispatch branch) that do NOT fall out of (b) and would, if
bundled, mix unrelated surfaces into one diff (the explicit Step-13b/area4
lesson).

**(b) Current-state assessment (code-grounded at HEAD).** The Stobj/Ldobj
arms (ILIntepreter.Neo.cs:3514-3570) copy ONLY primSize bytes
(Unsafe.CopyBlock of TotalPrimitiveSize); the TotalReferenceCount
ref-region half is NOT copied. So a stobj/ldobj of a VT WITH reference
fields SILENTLY TRUNCATES the ref half (a struct {int x; string s;} copied
via stobj loses s; a ldobj reads the dest's stale/null ref slot). The
IL-VT-with-ref-fields constrained sub-case throws the tagged NIE at :3818
(direct-call path) and :3876 (inherited-CLRMethod box path) -- both gated on
ilConstrained.TotalReferenceCount > 0. No green NeoStep case exercises either
today (zero regression). Both are pre-existing deferrals from neo-step17-
completion, NOT regressions.

**The shared root challenge (load-bearing).** A frame-native byref Ref Slot is
(-1, thisByteOff) -- it carries the struct's PRIMITIVE byte offset but NOT the
struct's ref-region mStack base (the source local's RefOffset). To copy/seed
the ref region (Stobj dest ref slots, Ldobj src ref slots, constrained slot-0
ref slots) the runtime needs that ref base. Two recovery options:

- **Option R1 (JIT-stamp): extend the Ref Slot encoding OR stamp the source
  local's RefOffset into an operand field at the producer.** The ldloca/
  ldflda producers run BEFORE LowerNeoOffsets (register indices available),
  so the source local's localInfos[srcReg].RefOffset is stampable. BUT the
  8-byte Ref Slot is a wire format consumed by stind/ldind/stobj/ldobj/ref-param
  across call boundaries -- extending it to 12 bytes ripples through every
  consumer + the call-region copy (CopyNeoCallArguments 8-byte entry). A
  standalone operand stamp (like NeoLdfldaInlineMarker in Operand4) is
  cleaner but only works for the OP that owns the slot (stobj/ldobj read a
  generic Ref Slot produced upstream; the constrained callvirt reads a Ref Slot
  from ldloca upstream). **R1 is the higher-risk path** (touches the Ref Slot
  contract shared with stind/ldind/ref-params).

- **Option R2 (runtime-recover): scan localInfos for the local whose
  Offset == thisByteOff and read its RefOffset.** Works ONLY for frame-
  native byrefs that point at a DIRECT local (objectIndex == -1 and the offset
  matches a local's primitive base). FAILS for ldflda-of-a-struct-field
  (nested struct -- the field's offset is NOT a local base) and for an mStack-
  object byref (objectIndex >= 0, where the ref region is the object's
  ManagedObjects -- already accessible via GetNeoILInstance). **R2 is the
  lower-risk path** for the direct-local shape (the common case: ldloca V;
  stobj / constrained.callvirt V.M where V is a local); the nested-field
  shape is the rare edge.

**Design LOCKED (provisional, dump-gated at apply):** the Stobj/Ldobj ref-loop
+ the IL-VT-with-ref-fields constrained sub-case use a HYBRID:

1. **For a frame-native byref of a DIRECT local** (the green target): recover
   the ref base by scanning localInfos for Offset == thisByteOff (R2). The
   Stobj/Ldobj arm then mirrors Move_Vt: byte CopyBlock of primSize + an
   mStack-to-mStack copy of TotalReferenceCount ref slots
   (mStack[frameRefBase + dstRefBase + i]). For the constrained sub-case,
   seed the callee slot-0 ref slots from the recovered ref base (analogous to
   how the newobj VT-THIS-ADDR Ret-arm copy-back seeds slot-0 refs).
2. **For an mStack-object byref (objectIndex >= 0)**: the ILTypeInstance's
   ManagedObjects is the ref region -- CopyFrameToIL/CopyILToFrame
   already handle it (the existing Stobj/Ldobj IL-instance branch is the
   primitive half; extend it with the ref half via the existing helpers). This
   is the area4 4d shape (NeoReadClrObjectField/NeoWriteClrObjectField for
   the CLR-object sub-case stays unchanged).
3. **For a frame-native byref NOT matching a direct local (nested-field via
   ldflda): tagged NIE** -- the rare edge; defer to a follow-up IF a smoke
   case exercises it.

**Apply-phase dump gates (MANDATORY, before fixing):**
- Confirm localInfos is accessible from the Stobj/Ldobj/Constrained arms at
  runtime (it is a local var in ExecuteNeo -- confirm scope).
- Confirm the source-local shape for the green-target probes (ldloca V of a
  top-level local, not a nested field) actually resolves Offset == thisByteOff
  (a stale-DLL false-failure is the earned gotcha; use --no-incremental).
- For the constrained direct-call IL-VT-with-ref-fields: dump the callee frame's
  ParamInfos[0] ref region to confirm the seed target offsets.

**The (c) edges -- assessed as DEFERRED (NIE-tagged, no smoke coverage):**
- **generic-byref (ref T/out T, T generic-param):** the JIT Stobj/
  Ldobj stamp only the type token (op.Operand = method.GetTypeTokenHash-
  (token)); a generic-param type token resolves to the runtime substitution
  but the ref-loop would need the SUBSTITUTED type's TotalReferenceCount.
  The Operand2/Operand3/Operand4 fields are free at runtime (Stobj/Ldobj
  use only DstOffset/SrcOffset/Operand). Likely a JIT side-stamp of the
  substituted type at type-specialization time. RARE in C# (a generic method
  taking ref T where T is a VT-with-refs called on a struct). DEFER.
- **fixed (pinned byref):** the C# fixed statement lowers to a pinned
  local + a byref; the Neo frame model has NO pinned-local flag. The byref
  itself works (frame-native Ref Slot); the pinning semantics (GC) are a
  separate concern. Likely "works for the address, no GC pin" -- acceptable-
  known IF reachable. No smoke case. DEFER (or accept-known if a probe shows
  it just works without pinning for the common fixed over a primitive array).
- **interface-on-VT-constrained beyond the common shape:** the Constrained arm
  already handles IEquatable<T>/IComparable<T> (the box-once / direct-call
  paths). The "beyond" edges are untyped-shape interface dispatch; no concrete
  reproducer. DEFER.

**Files the implementer will touch (all Neo-only; Legacy is the REFERENCE):**
- ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:
  - case OpCodeREnum.Stobj: (:3514) + case OpCodeREnum.Ldobj: (:3544)
    -- add the ref-region copy loop (R2 for frame-native-direct-local; the
    ILTypeInstance ManagedObjects half for mStack-object; NIE for nested-
    field).
  - case OpCodeREnum.Constrained: IL-VT-direct-call (:3818) + IL-VT-
    inherited-CLRMethod (:3876) -- remove the TotalReferenceCount > 0
    NIE; seed the callee slot-0 ref region from the recovered source local
    ref base (R2) / CopyFrameToIL with the real refCount (the inherited-
    CLRMethod box path already calls CopyFrameToIL -- extend it to pass the
    real ref base + refCount).
- NO JIT change for the green target (R2 is runtime-only). A JIT side-stamp
  is the apply-phase fallback IF R2's localInfos scan proves insufficient
  (e.g. the source is a temp, not a local).
- TestCases/NeoStep17Test.cs (extend) -- NeoStep17_* adversarial probes:
  (b) Stobj of a VT-with-ref-field; Ldobj of same; a nested VT-with-ref-field;
  the IL-VT-with-ref-fields constrained (direct-call override); the IL-VT-with-
  ref-fields constrained (inherited CLRMethod box). Regression: the primitives-
  only Stobj/Ldobj still works + the step17-completion paths + full smoke.

**Regression risk: MEDIUM.** The Stobj/Ldobj arms are shared by every byref-of-
VT consumer; the constrained sub-case is gated on TotalReferenceCount > 0
(byte-identical for primitive-only VTs, the existing green paths). The R2
localInfos scan is a runtime lookup on the byref path (not a hot-path
concern -- byref-of-VT-with-refs is rare). Gate: full NeoStep smoke (175/175
baseline) + the step17-completion probes still green + Legacy-neutral (all
changes Neo-only). Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN K1 /
F-MAJ-1 lessons: a green smoke does NOT prove a ref-region copy correct --
construct a probe where the dest's stale ref slot is non-null and the copy
MUST overwrite it).

**Side-benefit watch.** The IL-VT-with-ref-fields constrained sub-case is the
majority shape for real-world IL structs (most have a string/object field +
an override or a default ToString). Closing it unblocks realistic struct
usage in the smoke.

**Capability spec delta (1 MODIFIED requirement, narrowed):**
- neo-byref: the "Deferred byref sub-cases throw tagged NIE" requirement --
  flip (a) the stobj/ldobj ref-slot portion from DEFERRED to DELIVERED (a
  VT WITH reference fields is correctly copied through stobj/ldobj for the
  direct-local + IL-instance shapes; the nested-field-via-ldflda shape stays
  NIE-tagged); flip (h) the IL-value-type-with-reference-fields constrained
  sub-case from DEFERRED to DELIVERED (the slot-0 ref-region seed). The
  "stind/ldind/stobj/ldobj dispatch" requirement's "TotalReferenceCount
  ref-slot portion is PARTIAL" note is removed. (c) generic-byref / fixed /
  interface-on-VT-constrained stay DEFERRED (still tagged NIE).

## Findings -- neo-step17-stobj-refloop (apply, 2026-07-06)

**RESOLVED.** Step 17 (b) Stobj/Ldobj ref-region copy loop + the IL-VT-with-
ref-fields constrained sub-case delivered. NeoStep smoke 181/181 (175 baseline +
6 new probes). Legacy-neutral (all 41 `NeoStep17_` tests pass on plain `Debug` +
`useRegister=true`). Stash-toggle: probes 1/2/3/4/5 FAIL-on-HEAD (runtime
reverted via `git stash push -- ILIntepreter.Neo.cs`), PASS-after; probe 6
(primitives-only regression guard) correctly PASS on both (byte-identical).
`openspec validate --strict` clean.

**R1-vs-R2 RESOLVED: R2 (runtime localInfos scan), runtime-only, NO JIT change.**
In-arm `Console.WriteLine` dumps inside the Stobj/Ldobj arms confirmed the
green-target byref resolves to a DIRECT LOCAL via the scan (probe 1: byref
`off=0` == `localInfos[0].Offset`, RefOffset=0; src value `SrcOffset=32` ==
`localInfos[7].Offset`, RefOffset=3). R1 (JIT side-stamp) was NOT needed -- the
8-byte Ref Slot wire format stays 8 bytes.

**Operand mapping CONFIRMED.** Stobj: `DstOffset` = byref address (8-byte Ref
Slot), `SrcOffset` = value local. Ldobj: the REVERSE (`SrcOffset` = byref,
`DstOffset` = dest). Matches JIT Register1=address/Register2=value ->
LowerNeoOffsets DstOffset/SrcOffset.

**EARNED CONSTRAINT (load-bearing for any future byref work): R2 resolves the
byref to a direct local ONLY WHEN the byref is produced AND consumed in the SAME
frame.** A byref PARAMETER (a `ref` param passed to a helper) points at the
CALLER's frame; the helper's own localInfos scan cannot recover that ref base.
The green target is therefore the SAME-FRAME shape: the C# compiler's trivial
inliner folds a small byref helper (`static void M(ref S dst, S src) { dst = src;
}`) into the caller, where source + dest are caller locals and R2 resolves. A
helper that is NOT inlined (e.g. one returning a struct) hits the separate F-9 /
NEO-INLINED-RETURN-MOVE defect on its return value, not the stobj/ldobj arm.
Probe 2 was restructured from a returning helper to an `out`-param helper that
inlines cleanly. **Implication: the genuine cross-frame byref-of-VT-with-refs
shape (a non-inlined helper taking `ref S` and copying it) is NOT covered by R2
and stays deferred** (would need R1 -- a JIT side-stamp of the source local's
RefOffset that survives the call-boundary copy, or a frame-relative ref-base
encoding in the Ref Slot).

**Constrained direct-call slot-0 seed -- the mStack-reservation clobber gotcha
(loads the VT-THIS-ADDR precedent).** The seed CANNOT be a pre-call write to
`mStack[mStack.Count + slot0.RefOffset]`: the callee's ExecuteNeo reserves its
frameRefBase via `mStack.Add(null)` which ZEROES the slots, clobbering any
pre-call seed. Fix mirrors VT-THIS-ADDR's copy-back: 4 new optional hook params
on `ExecuteNeo` (`constrainedSlot0SeedRefOffset/SrcRefBase/RefCount` + an unused
base), seeded INSIDE ExecuteNeo right AFTER its mStack reservation, BEFORE the
body. The direct-call path calls `ExecuteNeo` directly (not `InvokeNeoCallTarget`)
to pass the hook. `constrainedSlot0SeedSrcRefBase = callerFrameRefBase +
srcLocalRefOffset` (R2). All defaults inert when RefCount==0 (existing callers
unaffected). The dump showed `slot0.RefOffset=0`, `mStack.Count=5`,
`callerFrameRefBase=0`, confirming the callee frameRefBase (= mStack.Count at
entry) is DISTINCT from the caller's and the seed must target the callee's
region post-reservation.

**Constrained inherited-CLRMethod box path -- simpler.** `CopyFrameToIL` already
iterates `ManagedObjects`; recover `boxSrcRefOffset` via the same R2 scan and
pass the real `refOffset + refCount` (was `refOffset=0, refCount=0`).

**OQ3 RESOLVED: the genuine nested-VT-field-byref stobj/ldobj shape is BLOCKED
by pre-existing Step-6 `Ldfld_Value` + F-6 gaps** (constructing `ref outer.inner`
hits those BEFORE reaching the stobj/ldobj arm) -- independent pre-existing
gaps, NOT this change's ref-loop. Probe 3 was repurposed to a VT with TWO ref
fields (`{int n; string a; string b;}`) exercising the MULTI-SLOT ref-region
copy loop (refCount==2, the off-by-one guard). The nested-field-byref shape
stays a tagged NIE inside the arm (`dstRefBase < 0` / `srcRefBase < 0` throw).

**Accepted-known edges (documented, NOT fixed):**
- **Nested-VT-field-byref stobj/ldobj** -- blocked upstream by Step-6
  `Ldfld_Value` + F-6; the arm's tagged NIE fires only if a byref reaches it
  that doesn't resolve via the scan. No smoke reproducer can reach it today.
- **Cross-frame byref-of-VT-with-refs (a non-inlined `ref S` helper copying the
  struct)** -- R2 cannot recover the caller's ref base from the helper frame;
  stays deferred (would need R1).
- **CLR VT with reference fields via stobj/ldobj without a ValueTypeBinder** --
  the area4 accepted-known (GC refs not materializable without a binder).

**Files edited (all Neo-only; Legacy byte-identical):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- ExecuteNeo
  signature + body (slot-0 seed hook), Stobj arm, Ldobj arm, Constrained arm
  direct-call path, Constrained arm inherited-CLRMethod box path.
- `TestCases/NeoStep17Test.cs` -- 6 new `NeoStep17_*` probes + 2 new struct
  types (`NeoStep17VtWithRef`, `NeoStep17VtWithRefOverride`,
  `NeoStep17VtWithTwoRefs`).

**Did NOT git commit/push** (per process discipline; LEAD commits after review).
Did NOT update `neo-deferred-items.md` (the shipper does at archive).

## Findings -- neo-step17-stobj-refloop (review-fix)


**Round 1 (2026-07-06): reviewer APPROVED with 2 Minors (no Blocker/Major); both fixed + re-verified.**

**M1 -- Stobj/Ldobj IL-instance branch silent-skip -> loud NIE.** The
IL-instance branches (byref target is an IL ILTypeInstance, value operand is a
frame-local register) of the Step-17(b) ref-region copy guarded the copy with
'if (srcRefBase >= 0) {}' and silently SKIPPED on a localInfos scan-miss,
leaving stale/null ref slots in the instance's ManagedObjects (silent
corruption). The frame-native branches already threw a tagged NIE on the same
miss. **Fix: mirrored the frame-native branch -- the IL-instance branches now
throw a tagged NIE on a scan-miss too.** Exotic unresolved-byref shape now
fails LOUD. Green-target probes (which all resolve) unaffected. Edit sites:
ILIntepreter.Neo.cs Stobj IL-instance branch (~3603) + Ldobj IL-instance branch
(~3676).

**DURABLE LESSON (the silent-skip-as-silent-corruption class):** when a ref/
gc-region copy arm has a 'resolve the source/dest region' scan that can miss,
the miss MUST fail loud (tagged NIE), NEVER silently skip -- a silent skip
leaves the destination region with stale data, which is strictly worse than a
crash (the bug propagates). This applies to any future arm that recovers a ref
base via a runtime scan (the R2 pattern). The frame-native branch had this
right; the IL-instance branch was an asymmetry introduced in the same diff.
Audit future R2-style arms for the symmetric 'throw on miss' discipline.

**M2 -- dead 'constrainedSlot0SeedRefBase' ExecuteNeo hook param DROPPED.** The
Step-17(b) constrained slot-0 ref-seed hook on ExecuteNeo had 4 params; one
('constrainedSlot0SeedRefBase') was dead -- the sole caller passed 0, and the
seed loop read the callee's OWN frameRefBase (= mStack.Count at ExecuteNeo
entry), never the passed base. **Fix: dropped the param entirely (signature +
guard + call site).** The hook is now 3 load-bearing params. All 5 ExecuteNeo
callers verified (4 pass the 5-arg form with hook defaults inert; 1 constrained
direct-call site passes 3 named hook params). Seed loop still correct (probe 4
PASS).

**DURABLE LESSON (dead hook params):** when adding an optional hook to a
function called from many sites, every hook param must be load-bearing or have
an explicit 'why it exists' comment. A param that is always passed the same
default and never read by the callee body is dead weight that misleads future
readers (they assume the caller controls that value). Prefer dropping over
documenting when the drop is clean (named args at the call site make it so).

**Re-verify:** CLI Debug_Neo --no-incremental 0 errors; TestCases Debug
--no-incremental 0 errors; plain Debug CLI 0 errors (Legacy untouched --
ILIntepreter.Neo.cs is Neo-only). NeoStep smoke 181/181 (0 failed); NeoStep17
filter 41/41 (0 failed). Working tree UNCOMMITTED.
## Findings -- neo-step20-async (2026-07-06, propose)

**SCOPING DECISION: SPLIT. Ship sync-first (neo-step20-async); suspend/resume
(neo-step20-async-suspend) is a follow-up.** Step 20 is the largest runtime
step (design 搂26 lists 6 deliverables; the suspend machinery alone is
frame-to-heap + ILAsyncContext + continuation registration + cross-thread
resume -- the highest infinite-loop / reentrancy risk in the runtime). Ranked
sub-pieces by (value x low-regression-risk): (1) sync-completing async (HIGH
value, MEDIUM risk) -- unblocks every async method whose awaitables are
already complete; exercises the full builder redirect surface + Start ->
MoveNext + sync SetResult/getter WITHOUT the frame-to-heap hoist,
ILAsyncContext continuation, or cross-thread resume; (2) truly-async
suspend/resume (HIGH value, HIGH risk) -- deferred. The planner prompt's
default lean was sync-first split; the code reading CONFIRMS the split is
clean and OVERRIDES nothing.

**Verify-against-code that the sync path IS cleanly isolatable (the
load-bearing check).** The builder API methods are INDEPENDENT redirections
(Create, Start, SetResult, SetException, get_Task, SetStateMachine each have
their own redirect; AwaitUnsafeOnCompleted/AwaitOnCompleted are separate). A
sync-completing async method NEVER calls AwaitUnsafeOnCompleted -- the C#
compiler emits "if (awaiter.IsCompleted) goto completed; else
builder.AwaitUnsafeOnCompleted(...)"; the IsCompleted short-circuit skips it.
So shipping sync-only = 6 redirects + leaving 2 as throw-tagged NIE stubs.
Cleanly isolatable. CONFIRMED.

**The current autogen Neo builder redirects are NON-FUNCTIONAL STUBS
(code-grounded).**
ILRuntimeTestBase/AutoGenerate/System_Runtime_CompilerServices_AsyncTaskMethodBuilder_1__t1.cs
(and siblings) register *Neo variants, but:
- Start_1_Neo reads the state machine as a CLR IAsyncStateMachineAdaptor (a
  CrossBindingAdaptorType -- the boxing model design 搂26 explicitly rejects),
  then calls instance.Start<...>(ref sm) on a default builder (the
  <>t__builder field is never read from the frame).
- Create_0_Neo / get_Task_2_Neo / SetResult_4_Neo / SetException_3_Neo all
  operate on a default builder ("// TODO: ValueType instance in Neo" /
  "// TODO: CLR value type return in reflection fallback: Step 13").
So under Neo, an async method NREs / infinite-loops / silently returns a
default task. The custom redirects OVERRIDE these stubs at registration time
(last-wins) -- the autogen FILES are NOT edited (regeneration-fragile, Step 19
delegate-binding lesson).

**Reuse shipped machinery (no re-implementation):**
- Step 8 Call convention (CopyNeoCallArguments, InvokeNeoCallTarget, Ret ->
  retDst).
- Step 12 in-frame IL value types + _Inline field ops -- the state machine sm
  is an in-frame IL VT local; its fields (<>t__builder, <>1__state, <>u__1
  awaiter, user locals) resolve via _Inline.
- Step 13 Box / CopyFrameToIL family -- the frame-to-heap hoist is a Box-
  without-CLR-instance (new ILTypeInstance(initializeCLRInstance:false)).
- Step 17 Ref Slot -- Start(ref sm) passes the SM by ref = an 8-byte Ref Slot.
- Step 19 DelegateAdapter.NeoInvokeSub (DelegateAdapter.cs:1006) -- the
  inverse frame build (fresh pooled interpreter, StackBase, write
  this+params, ExecuteNeo, read return, FreeILIntepreter in finally). The
  async MoveNext RESUMPTION (deferred slice) reuses this exact shape on a CLR
  continuation thread; the sync Start redirect runs MoveNext IN-PLACE on the
  caller's frame (NO fresh interpreter -- sm is already on the frame).
- Step 9 CLRRedirectionDelegateNeo -- the custom builder redirects use this
  signature.

**Key decisions locked.**
- D1: custom Neo builder redirects override autogen stubs at registration
  (last-wins), mirroring the ExceptionAdaptor precedent (neo-il-exception-
  throw). Autogen files NOT edited.
- D2: Start runs MoveNext IN-PLACE on the caller's frame (no fresh
  interpreter, no CLR interface, no adaptor). sm is an in-frame IL VT.
- D3: SetResult/SetException stash on the in-frame <>t__builder field
  (Task.FromResult / faulted task) -- primary IF the builder struct internal
  field is JIT-visible; fallback = per-builder-address auxiliary map. OQ1
  dump-decides.
- D4: AwaitUnsafeOnCompleted/AwaitOnCompleted are throw-tagged NIE stubs (the
  explicit, machine-checkable scope boundary).
- D5: frame-to-heap hoist helper ships standalone + unit-probed (the
  load-bearing primitive the suspend slice reuses); NOT wired into any
  sync-path redirect. Pure function.
- D6: ILAsyncContext<T> skeleton -- IValueTaskSource<T> surface live +
  unit-probed; IAsyncStateMachine.MoveNext tagged NIE (resumption = suspend
  slice). Sync getter does NOT touch it (uses Task<T>/ValueTask<T>.FromResult).
- D7: get_Task returns Task<T> for AsyncTaskMethodBuilder<T>; ValueTask<T>
  via FromResult for AsyncValueTaskMethodBuilder<T> (NOT IValueTaskSource --
  that is the suspend slice).

**Open questions (resolve at apply via JIT dump).**
- OQ1: is the AsyncTaskMethodBuilder<T> struct internal result field
  JIT-visible (D3 primary) or opaque (auxiliary-map fallback)?
- OQ2: does Start -> MoveNext route as an in-frame IL VT instance call (D2
  holds) or does the JIT box sm (D2 fails -> force in-frame via a Newobj-dest-
  typing-style JIT seed)?
- OQ3: autogen-stub override ordering -- custom redirects last-wins-after-
  autogen, OR autogen builder files regenerated to skip *Neo registration?

**Capability spec deltas.**
- neo-async (NEW): 8 requirements -- builder redirection (sync scope) / Start
  drives MoveNext in-frame / get_Task completed-on-sync / AwaitUnsafeOnCompleted
  tagged deferral / frame-to-heap hoist primitive / ILAsyncContext skeleton /
  suspend+resumption DEFERRED. The DEFERRED req is a placeholder so the
  follow-up merges the rest.
- neo-dispatch (ADDED): 1 requirement -- async Start drives MoveNext via the
  Neo call convention (not the CLR interface); resumption reuses the Step 19
  NeoInvokeSub fresh-pooled-interpreter pattern; builder redirect overrides
  the autogen stub.

**Files the implementer will touch (all Neo-only; Legacy ExecuteR is the
REFERENCE, NOT modified; autogen builder files NOT edited):**
- ILRuntime/Runtime/Intepreter/ILAsyncContext.cs (NEW) -- ILAsyncContext<T>
  skeleton.
- ILRuntime/Runtime/CLRBinding/CLRRedirections.AsyncNeo.cs (NEW, or extend
  CLRRedirections.cs) -- the custom Neo builder redirects.
- ILRuntime/Runtime/Enviorment/AppDomain.cs -- RegisterNeoAsyncRedirections
  (override autogen stubs).
- ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs -- the
  HoistNeoILValueToHeap helper (CopyFrameToIL family) + any MoveNext-routing
  support.
- TestCases/NeoStep20Test.cs (NEW) -- NeoStep20_* probes.

**Regression risk: MEDIUM-HIGH.** Async sits on top of Steps 8/12/13/17/19 --
any latent bug in those surfaces here. Async bugs LOVE to infinite-loop (test
>10s = loop; kill). The in-frame VT instance-method call (MoveNext on sm) is
the [NEO-IL-VT-INSTANCE-COVERAGE] family -- DUMP-GATE it (VT-THIS-ADDR / area4
discipline). Gate: full NeoStep smoke (181/181 baseline) + Legacy 518/519 for
any shared-engine edit. Adversarial probes MANDATORY (Step 17 B1 / OPT-HARDEN
K1 / Step 19 F1 lessons: green smoke does NOT prove the async gate correct).

**Follow-up created: neo-step20-async-suspend** -- the truly-async
suspend/resume path (AwaitUnsafeOnCompleted real impl wiring the hoist +
ILAsyncContext continuation registration + resumption on a fresh pooled
interpreter + ValueTask<T> via IValueTaskSource + ExecutionContext /
SynchronizationContext capture for AwaitOnCompleted). Warm-seeded: the hoist
helper + ILAsyncContext skeleton + the NeoInvokeSub resumption shape all ship
in the sync slice. To be recorded in .trae/documents/neo-deferred-items.md at
apply.


## Findings -- neo-step20-async (apply, 2026-07-06)

**Sync slice INFRASTRUCTURE-COMPLETE but END-TO-END BLOCKED (partial ship).**
The custom Neo async builder redirects override the autogen non-functional
stubs (first-registered-wins confirmed: AppDomain ctor runs BEFORE
`CLRBindings.Initialize`, so registering in the ctor wins and autogen's
`*Neo` stub registrations are skipped). The 6 sync builder redirects
(Create/Start/SetResult/SetException/get_Task/SetStateMachine) + the
AwaitUnsafeOnCompleted/AwaitOnCompleted tagged-NIE stubs ship, PLUS the
awaiter/Task accessor overrides (the autogen `TaskAwaiter_*`/`Task_1_*`
`*Neo` stubs use `default(TaskAwaiter)` and are non-functional -- they MUST be
overridden for any await to work). The D5 `HoistNeoILValueToHeap` helper +
the D6 `ILAsyncContext<T>` skeleton ship standalone. Existing NeoStep smoke
NOT regressed (181/181; all 13 new failures are NeoStep20_* async probes).

**DUMP-CONFIRMED OQ2: the SM is a HEAP ILTypeInstance, NOT an in-frame VT.**
The C# compiler emits `<Method>d__N` as a struct, but ILRuntime loads it as a
reference type (`IsValueType == false`). The driver newsobj's the SM (heap);
MoveNext's `this` (ParamInfos[0]) is a 4-byte reference. D2's in-frame-VT
premise DOES NOT APPLY -- the heap-object premise is simpler and sidesteps
`[NEO-IL-VT-INSTANCE-COVERAGE]`.

**Start -> MoveNext uses a FRESH pooled interpreter (NOT in-place).** D2's
in-place ExecuteNeo corrupted the caller's frame (the driver's SM reference
was lost between Start and get_Task -- `ldflda` produced `(0,0)` instead of
`(smIdx, fieldOff)` after MoveNext ran on the same frame). The fix mirrors
Step 19 `NeoInvokeSub`: `DriveMoveNext` uses `RequestILIntepreter`/
`FreeILIntepreter`; the SM (heap ref) is written into the fresh frame's slot-0.

**OQ3 RESOLVED: first-registered-wins (NOT last-wins as the design assumed).**
`RegisterCLRMethodRedirectionNeo` is `if (!ContainsKey) add`. Register in the
AppDomain ctor (runs before `CLRBindings.Initialize`) -> custom wins, autogen
skipped. The autogen builder FILES are NOT edited (mirrors the D1 / Step 19
delegate-binding codegen-fix precedent).

**OQ1 RESOLVED: D3 auxiliary-map fallback (keyed by SM ILTypeInstance).** The
builder struct's internal `_task` is opaque to the Neo frame. A ThreadStatic
`Dictionary<ILTypeInstance, object>` (SmTaskMap) holds the completed/faulted
Task; the SM is recovered from the builder byref via `RecoverSmFromBuilderByref`
(the byref `(sm_mStackIdx, builder_field_off)` encodes the owning SM).

**REMAINING BLOCKER (sync slice end-to-end): TaskAwaiter struct round-trip.**
After `Task<int>.GetAwaiter()` (overridden as a Neo redirect -- the callvirt.clr
generic-type `ArgumentException` is routed around by the redirect) returns a
real TaskAwaiter<int> via WriteNeoValueType, MoveNext's subsequent
`get_IsCompleted` call (a `call` with `this` = `ldloca awaiter_local`) does NOT
reach its redirect. The awaiter struct's flat-bytes representation appears to
not round-trip correctly between the GetAwaiter return write and the
get_IsCompleted byref-`this` deref (a CLR-struct-instance-method marshaling
edge, the same family as `[NEO-BYREF-THIS]` / area4b). NOT introduced by this
change.

**NEW FOLLOW-UP (route to unblock): `neo-step20-async-await-roundtrip`.** (a)
verify the TaskAwaiter flat-bytes size matches between WriteNeoValueType
(Marshal.SizeOf) and the optimizer's declared slot size; (b) confirm
CopyNeoCallArguments dereferences the ldloca byref for the get_IsCompleted
`this` (area4b PrimitiveByRefSrc flag); (c) if the awaiter struct shape
disagrees, write the awaiter's Task reference directly as a ref slot. Plus a
PRE-EXISTING bug noted: `Callvirt_CLR` on a method of a generic type instance
throws `ArgumentException: must not be a generic type` when the CLRMethod has
no RedirectionNeo (the MethodInfo is on `Task`1` the definition). Routing
callvirt.clr through a registered Neo redirect avoids this, but a real fix in
`ResolveNeoCallvirtCLRTarget` (specialize the declaring type) is the durable
fix.

**Lesson re-affirmed.** The design assumed overriding the 6 builder redirects
would suffice for the sync slice. In reality the autogen `TaskAwaiter_*`/
`Task_1_*` `*Neo` stubs are EQUALLY non-functional (they use
`default(TaskAwaiter)`), so the awaiter/Task accessors MUST also be overridden.
A `grep` of the autogen stubs for `// TODO: ValueType instance in Neo` is the
tell -- every such stub is non-functional under Neo and a sync-async path will
hit it. Future CLR-binding stub work: any `*Neo` autogen stub with that TODO is
a redirect the Neo engine must override at registration.

**Suspend primitives.** The D5 hoist helper + D6 ILAsyncContext<T> skeleton
ship (compile-clean, standalone) but are NOT exercised by a green test (the
sync path that would validate them as a side-effect is blocked). They are
proven-by-compilation only; the suspend slice should add a unit probe
(`NeoStep20_HoistPreserves*` / `NeoStep20_AsyncContextValueTaskSourceRoundTrip`)
when it lands.

## Findings -- neo-step20-async (review-fix, 2026-07-06)

**VERDICT: STOPPED (stacked pre-existing CLR-struct-field-of-IL-instance
edges; NOT a focused fix).** The sync Task<int> single-await + nested paths
(TC1, TC7) are GREEN end-to-end -- the builder-redirect surface, Start->
MoveNext routing (fresh pooled interpreter), awaiter/Task accessor overrides,
and SmTaskMap stash ALL work. The remaining 6 probes (TC2 non-generic Task,
TC3 ValueTask<int>, TC4 async void, TC5 multi-await, TC6 exception, TC8
incomplete) are blocked by a DIFFERENT and more foundational edge than the
implementer's "TaskAwaiter byref-this deref" hypothesis.

**Dump-confirmed root cause (the load-bearing finding): the CLR-struct-field-
of-IL-instance addressing defect.** The async state machine `<Method>d__N` is
a HEAP ILTypeInstance. Its CLR-struct fields (`<>t__builder` =
AsyncTaskMethodBuilder, `<>u__1` = TaskAwaiter) are laid out by
`ILType.cs:2129-2157` as REFERENCE slots (`referenceOffset++`, NO
`primitiveOffset` advance) -- so their bytes do NOT live in
`ILTypeInstance.Primitives` (only IL-primitive fields do). But the JIT's
`ldflda &SM.<clrStructField>` emits a byref `(smMStackIdx,
field.PrimitiveOffset)` -- a stale offset that points PAST the Primitives
array. `CopyNeoCallArguments` -> `NeoMarshalByrefFieldToSlot` reads
`ili.Primitives[off]` for `sz` bytes -> IndexOutOfRange. Dump proof: TC1's SM
has `smPrimLen=12` (the builder `(2,4,sz=8)` fits by layout accident); TC2's
SM has `primLen=4` (the same `(2,4,sz=8)` OOBs). The byref encoding carries
ONE offset, so it is UNRECOVERABLE to the field's actual storage
(ManagedObjects[ReferenceOffset]) -- a narrow runtime fix does not exist.

**Why the implementer's hypothesis was half-right.** The TaskAwaiter byref-
this round-trip (GetAwaiter return -> get_IsCompleted byref-this deref) IS in
the failing set, but it is NOT the first failure: the `Start` call's BUILDER
byref fails earlier (in the driver's CopyNeoCallArguments, before MoveNext
runs) for the non-generic shapes. For the generic Task<int> shape, the builder
byref happens to fit (layout accident) so MoveNext runs and the awaiter
round-trip works (TC1 green). The "awaiter round-trip blocker" only manifests
for awaiter fields whose SM Primitives is too short -- the SAME defect class.

**STOP criterion met (OPT-HARDEN K1 lesson).** The defect underlies 5+
distinct probe failures at different call sites (Start builder-byref for
non-generic/ValueTask/async-void; multi-await awaiter-field reuse; exception
path; incomplete-await path). A real fix is broad: either a JIT change so
`ldflda` of a CLR-struct-field-of-IL-instance produces a recoverable encoding
(e.g. a sentinel objIdx + ReferenceOffset, with a runtime branch reading the
boxed struct from ManagedObjects), OR a layout change so the struct's flat
bytes ARE stored in Primitives (advance primitiveOffset by the managed size,
mirror in AllocateNeoCallParamSlot + every stfld/ldfld/by-value-param
consumer). Forcing a narrow fix (zeroing the OOB dest) yields silent wrong
results (default builder -> SmTaskMap never gets a real Task) -- the silent-
corruption class the OPT-HARDEN review-fix M1 lesson forbids.

**Follow-up child needed: `[NEO-CLRSTRUCT-FIELD-OF-IL]`** -- close the CLR-
struct-field-of-IL-instance addressing defect. This is the load-bearing
primitive BOTH the remaining sync-async probes AND the suspend slice need
(the awaiter field `<>u__1` is the same shape). Route: a dedicated child
(Wave 2.5, before resuming neo-step20-async). The fix site is the field-
layout pass (`ILType.cs:2129-2157`) + `ldflda` JIT lowering + the
NeoMarshalByrefFieldToSlot ILTypeInstance branch + the stfld/ldfld consumers
of CLR-struct fields on IL instances. Same family as F-2
(WriteNeoCallSlot struct-with-ref-fields) and [NEO-BYREF-THIS] -- record in
`neo-deferred-items.md`.

**Callvirt_CLR generic-type-instance bug: NOTED, not fixed.** Did NOT block
any green-target probe -- every Task<T>/TaskAwaiter<T> accessor the sync path
exercises is covered by a registered Neo redirect, so the reflection-fallback
`clrMethod.Invoke` (where the ArgumentException originates) is never reached.
The implementer's `WriteValueTypeReturn` already uses
`Optimizer.GetNeoValueTypeManagedSize` (not `Marshal.SizeOf`). Follow-up only
if a future probe exercises an UN-redirected generic-type-instance CLR method.

**Smoke (no baseline regression).** NeoStep 199 ran, 11 failed -- ALL 11 are
NeoStep20 probes; the 181 non-NeoStep20 probes stay green. Legacy untouched.

**Ship recommendation.** Ship the infrastructure PARTIAL (TC1 + TC7 green
proves the core sync Task<int> machinery). Defer the remaining 6 probes +
the AwaitUnsafeOnCompleted NIE confirmation (TC8 unreachable -- MoveNext
fails before the IsCompleted short-circuit) to the
[NEO-CLRSTRUCT-FIELD-OF-IL] follow-up + neo-step20-async round 2.

**Lesson re-affirmed (the F-6 / OPT-HARDEN K1 / Q-* family).** The
implementer's blocker narrative ("TaskAwaiter byref-this deref") was a
HYPOTHESIS from the symptom they hit; the dump-gate revealed the actual
first-failure is a different, more foundational edge (the builder byref, not
the awaiter byref) and that the probe set SPLITS (generic works, non-generic
fails) on a layout accident. Probe EACH green-target probe INDIVIDUALLY +
dump-gate the actual first-failure opcode before designing a fix; a single
narrative blocker can mask a split probe set + a stack of distinct edges.

## Findings -- neo-clrstruct-field-of-il (2026-07-06, propose)

**F-10 root cause CONFIRMED against current code + the blast-radius assessment
that locked the (A)-vs-(B) decision.** A CLR-struct field of an IL instance
(e.g. an async SM's `<>t__builder` = `AsyncTaskMethodBuilder`, `<>u__1` =
`TaskAwaiter`) is laid out by `ILType.cs:2129-2157` (the `else` branch at
`:2146`) as a REFERENCE slot (`referenceOffset++`, NO `primitiveOffset`
advance) -- the boxed struct lives at `ManagedObjects[ReferenceOffset]`, NOT
in `Primitives`. The JIT `ldflda` of that field (`JITCompiler.cs:2383`)
stamps `op.Operand2 = offset.PrimitiveOffset` (the stale offset pointing
PAST `Primitives`). The runtime `Ldflda` heap-IL branch
(`ILIntepreter.Neo.cs:1088-1089`) produces `(objIdx, fieldPrimOff)`;
`NeoMarshalByrefFieldToSlot` (`:376-386`) reads `ili.Primitives[off]` for
`sz` bytes -> OOB (TC2 non-generic Task SM `primLen=4`) or in-range-by-accident
(TC1 Task<int> SM `primLen=12`). The byref carries ONE offset, unrecoverable
to `ManagedObjects[ReferenceOffset]`.

**The load-bearing blast-radius finding (verified against current code, NOT
just inferred): `Stfld_Ref` / `Ldfld_Ref` (`:2716-2722` / `:2760-2764`) use
`Operand3 = ReferenceOffset` and read/write `ins.ManagedObjects[ip->Operand3]`.
A CLR-struct field IS a reference slot, so these arms ALREADY treat the boxed
struct as a reference and round-trip it correctly. `stfld`/`ldfld` of a
CLR-struct field WORKS today; ONLY `ldflda` (and reading through its byref)
is broken.** This makes option (A) (JIT-byref-encoding, localized to the
`ldflda` lowering + the byref consumers for this one shape; no `ILType.cs`
edit; the IL-primitive-field + CLR-object-field + in-frame-VT + frame-native
`ldflda` paths stay byte-identical) strictly lower-risk than option (B)
(layout change advancing `primitiveOffset` by the struct's managed size,
requiring mirroring through EVERY stfld/ldfld/by-value-param/Move_Vt consumer
+ changing the Stfld_Ref/Ldfld_Ref semantics that already work). (A) chosen;
(B) rejected at propose. A narrow "zero the OOB dest" patch is REJECTED
(silent-corruption class -- the OPT-HARDEN M1 lesson).

**Design LOCKED (with 3 dump-gated OQs the implementer resolves at apply).**
- D1: option (A) -- encode the field's `ReferenceOffset` (not the stale
  `PrimitiveOffset`) into the `ldflda`-produced byref for the CLR-struct-
  field-of-IL-instance shape, with a sentinel discriminator so the runtime
  consumers can route to `ManagedObjects[ReferenceOffset]`.
- D2/D6 (OQ1, load-bearing): the byref is 8 bytes `(objIdx, offset)` -- no
  room for a third field. Lean encoding (beta): keep `objIdx` = the
  ILTypeInstance's mStack index (so the consumer's `mStack[objIdx]` fetches
  the ILTypeInstance as before, reusing the existing `objIdx >= 0` fetch),
  and put the discriminator in the OFFSET half (a sentinel that signals
  "this offset is a `ManagedObjects` ref-slot index, not a `Primitives` byte
  offset"). Alternative (alpha): bit-pack mStack index + ReferenceOffset into
  the offset half (documented bit-width assumption). (beta) is cleaner (no
  bit-packing; the discriminator is a single offset-half sentinel); VERIFY
  at apply via JIT dump + a runtime diagnostic.
- D3: the JIT discriminator condition -- in `TypeSpecializeNeoOpcodes`
  `case Ldflda:` (`:828-846`), ADD a second condition alongside the existing
  F-6 in-frame-VT marker: source `Register2` is a HEAP IL reference type
  (`is ILType && !IsValueType`) AND the addressed field's type is a CLR value
  type. The field type is recoverable via `appdomain.GetFieldOffset` re-
  resolution (mirror how `Code.Ldflda` Translate at `:2381` gets `fieldType`).
- D2-encoding-field (OQ2): the F-6 `NeoLdfldaInlineMarker` uses `Operand4`
  bit `0x1`. F-10 needs a DISTINCT bit (e.g. `Operand4` bit `0x2`) or a
  distinct standalone field. DUMP-GATE collision-freedom at apply (the F-6
  precedent confirmed `Operand4` standalone for `Ldflda`).
- D4 (OQ3): the byref consumers reached -- `CopyNeoCallArguments` ->
  `NeoMarshalByrefFieldToSlot` (the Step-20 builder-byref hot path, IN
  SCOPE) + the `stind_*`/`ldind_*`/`Stobj`/`Ldobj` arms (SCOPE at apply via
  JIT dump of the reproducer + the re-added Step-20 probes; ship branches
  for the reached widths; NIE-tag the rest with a clear Step-20/F-10 msg --
  loud, not silent corruption). `fixed` stays a Step-17 NIE.

**Reuse map (do NOT reinvent).** The Step 17 Ref Slot `(objectIndex, offset)`
semantics + the `-1`/`>=0` meaning-classes (UNCHANGED -- the sentinel / offset-
half discriminator is a new third class); `NeoIsClrObject` /
`NeoReadClrObjectField` / `NeoWriteClrObjectField` (area4-refandstind -- the
CLR-object-field accessor family; F-10 is the ILTypeInstance analogue reading
`ManagedObjects[refOffset]`); `ReadNeoValueType` / `WriteNeoValueType`
(Step-13b/area4 -- flatten/re-box the boxed CLR struct to/from the dest slot);
the F-6 `NeoLdfldaInlineMarker` precedent (a standalone-`Operand4` marker
stamped in the type-spec `case Ldflda:`, collision-free, invisible to the
`addrAlias` COEXIST gate -- F-10 mirrors it with a second bit).

**Files the implementer will touch (all Neo-only; Legacy `ExecuteR`'s
`Ldflda` via `GetObjectAndResolveReference` + the tagged
`ObjectTypes.ValueTypeObjectReference` model is the semantic REFERENCE, NOT
modified; NO `ILType.cs` layout edit -- option A leaves it byte-identical ->
Legacy-neutral by construction):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` --
  `TypeSpecializeNeoOpcodes` `case Ldflda:` (`:828-846`): add the second
  discriminator condition + the offset swap (stamp `ReferenceOffset` into the
  offset field for this shape, OR carry it for the runtime arm to read).
  Neo-only (`#if ENABLE_NEO_MODE`). The `Code.Ldflda` Translate case
  (`:2371-2389`) is UNCHANGED (it already stamps both `PrimitiveOffset`
  (`Operand2`) and `ReferenceOffset` (`Operand3`) -- the runtime arm /
  type-spec pass picks the right one for the shape).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the
  `case OpCodeREnum.Ldflda:` arm (`:1025-1092`): add the 4th dispatch shape
  (F-10 byref production). `NeoMarshalByrefFieldToSlot` (`:376-...`): add the
  F-10 branch (read/write `ili.ManagedObjects[refOffset]` via
  `ReadNeoValueType`/`WriteNeoValueType`). The `stind_*`/`ldind_*`/`Stobj`/
  `Ldobj` arms reached per OQ3 (add the F-10 branch; NIE-tag unreached
  widths). Existing ILTypeInstance-`Primitives` + CLR-object-fieldHash
  branches byte-identical for non-discriminator byrefs.
- NO `ILType.cs` edit (option A). NO `Optimizer.Neo.cs` edit (the
  `addrAlias`/`liveAliasMap` machinery is not perturbed -- the F-6 marker
  precedent; the register-reuse adversarial probe is MANDATORY to confirm).
- `TestCases/NeoStep12Test.cs` (extend) OR a new
  `TestCases/NeoClrStructFieldTest.cs` -- the adversarial probe set (7 probes
  per the task list: the minimal reproducer; multiple CLR-struct fields; a
  CLR-struct field WITH a ref-type field -- the TaskAwaiter shape; the
  stfld/ldfld regression; by-value-after-ldflda-deref; other-field-types
  regression; the Step-17-B1 register-reuse escape). Names match the
  `NeoStep` smoke filter.
- `.trae/documents/neo-deferred-items.md` -- F-10 row/entry -> RESOLVED.
- `openspec/specs/neo-value-types/spec.md` -- delta merged at archive.

**Regression risk: MEDIUM.** The runtime `ldflda` arm +
`NeoMarshalByrefFieldToSlot` + the `stind_*`/`ldind_*` arms are shared by
every `ldflda`/byref consumer, but the new branch fires ONLY for the F-10
discriminator (stamped ONLY for the CLR-struct-field-of-IL-instance shape);
every other shape is byte-identical when the discriminator is absent. Gate:
full `NeoStep` smoke (186/186 baseline) + Legacy-neutral stash-toggle (no
shared-engine edit; `ILType.cs` UNCHANGED). Adversarial probes MANDATORY
(Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1 / F-6 lessons: this is the silent-
corruption/OOB class -- a green smoke does NOT prove the encoding correct).
The biggest design risks are the encoding-field non-collision (OQ2) + the
mStack-index-recoverability encoding (OQ1/D6) + the consumer-arm coverage
(OQ3) -- all DUMP-GATED, STOP if the designed fix is wrong (do NOT ship a
guessed encoding or a guessed consumer-arm set).

**Baseline note.** NeoStep smoke is 186/186 at HEAD (after neo-step20-async).
The F-10 reproducer (a SMALL IL class with a CLR-struct field, `Primitives`
shorter than the stale offset + sz) FAILS on HEAD (OOB) and turns green
after the fix -- proves load-bearing (mirrors the F-MAJ-1 / F-6 stash-toggle
proof). The existing TC1/TC7 (which pass by layout accident) MUST stay green
after the fix (the byref for the builder field now resolves correctly to
`ManagedObjects[ReferenceOffset]`).

**Spec-validation gotcha re-affirmed (the area4 / K2-FAM finding).** The
openspec validator requires the requirement DESCRIPTION (not just the title)
to contain SHALL/MUST. The first F-10 requirement draft led with a "When an
IL reference type ..." clause (no SHALL until sentence 3) and FAILED
validation with "must contain SHALL or MUST". Fixed by prepending an explicit
"The Neo VM SHALL make an `ldflda` of a CLR-struct-typed field of an IL
reference type ... produce a byref whose runtime consumers recover the
field's actual storage ..." sentence. Re-validate after every spec edit.

**Side-benefit watch.** The IL-class-with-a-CLR-struct-field accessed via
`ldflda`/byref is the load-bearing primitive for the rest of Step 20 sync
(non-generic Task, ValueTask, multi-await, exception, async void) AND the
suspend slice (the awaiter field `<>u__1` is the same shape). This change
unblocks both. Check at verify whether re-adding a subset of the trimmed
Step-20 probes (TC2/TC3) is cheap; if so, ship them as the unblock proof;
else defer the full re-add to `neo-step20-async` resume (the F-10 reproducer
probes are the load-bearing proof either way).


## Findings -- neo-clrstruct-field-of-il (apply, 2026-07-06)

**RESOLVED.** F-10 / NEO-CLRSTRUCT-FIELD-OF-IL FIXED. NeoStep smoke 190/190
(186 baseline + 7 F-10 probes + Step 20 net +4). Step 20 smoke 9/9. Legacy-
neutral (all JIT stamps `#if ENABLE_NEO_MODE`; runtime arms in the Neo-only
file; plain Debug builds 0 errors). All 7 F-10 probes FAIL-on-HEAD (stash-
toggle) -> PASS-after.

**The propose-time blast-radius premise was INCOMPLETE (the dump disproved
"only ldflda broken").** The Block-0 reproducer dump showed `Stfld_Ref` of a
CLR-struct field from a flat-bytes source reads the source's first 4 bytes as a
ref-slot mStack index (`srcIdx=1092616192` = `10.0f` reinterpreted) -> OOR. And
`Ldfld_Ref` of a CLR-struct field writes an mStack index into a flat-bytes dest
(wrong under the F-MAJ-1 flat-bytes CLR-VT-local model). So ALL THREE heap
field-access arms (Stfld_Ref, Ldfld_Ref, Ldflda) of a CLR-struct field were
broken, not just ldflda. The fix is the CONSISTENT model: a CLR-struct field of
an IL instance is a reference slot at `ManagedObjects[ReferenceOffset]` holding
a BOXED struct; stfld boxes, ldfld flattens, ldflda addresses. Lesson re-
affirmed (K1 / F-MAJ-1 / F-6 family): the propose-time blast-radius sweep is
NOT authoritative -- the Block-0 reproducer dump is.

**Encoding (OQ1 = beta, OQ2 = no collision):**
- `Ldflda` F-10 marker = `NeoLdfldaClrStructFieldMarker = 0x2` (Operand4 bit
  0x2, OR-stamped alongside F-6's `0x1`; mutually exclusive shapes). Runtime
  `Ldflda` arm 4th shape: `(objIdx, ReferenceOffset | NeoF10ByrefOffsetFlag)`
  where `NeoF10ByrefOffsetFlag = 0x40000000` (bit 30; avoids sign bit).
- `Stfld_Ref`/`Ldfld_Ref` F-10 discriminator = `Operand4 != 0`, where F-10
  stamps `Operand4 = fieldType.GetHashCode()` (resolvable via
  `AppDomain.GetType(hash)`). Operand4 is otherwise unused for these opcodes.
  The 0-hash ambiguity is astronomically rare (accepted-known).

**Consumer arms shipped (OQ3):** `NeoMarshalByrefFieldToSlot` (the Step-20
builder-byref hot path) + `Ldobj`/`Stobj` ILTypeInstance branches. **elemType-
recovery subtlety (earned):** the Step-20 redirect plumbing does NOT propagate
the byref elemType for the builder-byref write-back; the write branch recovers
the type from the boxed struct ALREADY at `ManagedObjects[refOff]`
(`existing.GetType()`). Without this, TC1/TC4/TC6/TC7 NIE. The fixed-width
`stind_*`/`ldind_*` arms through an F-10 byref are NOT branched (unreached by
smoke; would OOR loud on the flagged offset -- accepted-known; add the branch
if a future probe reaches it).

**Probe inliner gotcha (earned).** ILRuntime's trivial inliner folds small IL
methods. A probe that passes `ref c.field` to an IL-side helper gets inlined ->
the helper body's `v.x` reads become `ldfld` on the byref (Step-6 NIE), NOT a
real `call`. To force the F-10 byref through `CopyNeoCallArguments`, the byref
helper MUST be a CLR (host-assembly) method (the inliner cannot fold CLR
methods). Added `SumTestVector3NoBindingByRef` etc. to
`ILRuntimeTestBase/TestFramework/TestClass3.cs`.

**Stale-build/DLL-hell gotcha (earned, bit me).** After TestCases source
changes, incremental cross-config (Debug/Debug_Neo) builds leave stale
ILRuntimeTestBase.dll copies in the CLI's output dir. Symptoms: broad
`LowerNeoOffsets` IndexOutOfRange / `Cannot find method` on tests that should
pass. Fix: clean `bin/obj` of TestCases + ILRuntimeTestBase + ILRuntimeTestCLI
+ ILRuntime, then rebuild. The CLI's net8.0 output dir must have a CURRENT
ILRuntimeTestBase.dll (copy or rebuild).

**Step-20 sync unblock status (partial, as designed):** F-10 took Step 20 sync
from 2 green (TC1/TC7) to 4 green (TC1/TC4/TC6/TC7). TC4 (async void) and TC6
(async-exception-faults-task) are newly green. TC2 (non-generic Task), TC3
(ValueTask<int>), TC5 (multi-await) PROGRESS PAST the F-10 OOB but hit DISTINCT
downstream Step-20 redirect-coverage edges (non-generic Task `Start` redirect
null-stateMachine; ValueTask builder NRE; multi-await `Task<int>.get_Result`
redirect) -- re-trimmed (kept green) until neo-step20-async resume. TC8 stays
removed (needs the suspend slice). This is the design's "note it, don't force"
case -- F-10 is correct; the remaining edges are separate Step-20 redirect
work.

**Accepted-known (NOT F-10 defects):** a CLR-struct field whose struct has
reference fields AND no registered ValueTypeBinder hits the Step-13b binder
NIE (loud) under ReadNeoValueType/WriteNeoValueType. The TaskAwaiter-with-Task
shape (probe 4.3's original intent) is blocked upstream by this; the real
`TaskAwaiter<T>` ships with a framework binder. Probe 4.3 reworked to a pure-
primitive multi-instance round-trip.

**Files edited (all Neo-only or test-side; NO ILType.cs, NO Optimizer.Neo.cs):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- consts +
  `IsClrStructFieldOfIL` helper + Operand4 stamps in the 3 Translate cases
  (all `#if ENABLE_NEO_MODE`).
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Stfld_Ref /
  Ldfld_Ref / Ldflda / NeoMarshalByrefFieldToSlot / Ldobj / Stobj F-10 branches.
- `ILRuntimeTestBase/TestFramework/TestClass3.cs` -- F-10 byref host helpers +
  Step-20 async-void side-effect cell.
- `TestCases/NeoClrStructFieldTest.cs` (NEW) -- 7 F-10 probes.
- `TestCases/NeoStep20Test.cs` -- TC4/TC6 unblocked + green; TC2/TC3/TC5 re-
  trimmed with documented downstream edges.

## Findings -- neo-clrstruct-field-of-il (review-fix)

**F-10-R1 (reviewer Major-latent -> fixer RECLASSIFIED to accepted-known
deferred edge):** the reviewer flagged that the runtime `Ldflda` arm checks
F-10 (`clrStructFieldMarker && objIdx >= 0`) before F-6 (`inlineMarker`),
predicting a mis-dispatch for an IL-VT with a CLR-struct field accessed via
`ldflda this.field` in a VT method (the F-6+F-10 both-stamp shape), and
recommended a 1-line runtime reorder (F-6 first).

**Fixer finding (runtime diagnostic on a new latent-shape probe):**
- The discriminator-level non-mutual-exclusivity is REAL: for `ldflda
  this.field` in a VT method on a struct with a CLR-struct field, the runtime
  sees `Operand4 = 0x3` (F-6 | F-10 both stamp).
- BUT the recommended runtime reorder is **INCORRECT** -- it broke 6
  NeoStep17 F-6-only probes (190/190 -> 184/190). Root cause: F-6 shape 3
  (`operandSlotOff + fieldPrimOff`) and shape 1/2 frame-native
  (`vtBase + fieldPrimOff`) produce DIFFERENT byrefs; every reachable VT
  `this`/arg today arrives as a managed pointer (objIdx == -1), so HEAD order
  routes them to shape 1/2 (correct), while the reorder routes them to F-6
  shape 3 (wrong).
- The F-10-first mis-dispatch is NOT reachable today: it requires objIdx >= 0
  with flat bytes (the constrained-boxed-VT sub-case), which is gated behind
  the DEFERRED `constrained.callvirt`-on-VT (Step 13 Area 3 / Step 17 follow-
  up). For all reachable VT source shapes the operand slot holds a managed
  pointer (objIdx == -1), so the F-10 arm does not fire and shape 1/2 handles
  it. The shipped F-10 case (heap IL ref, objIdx >= 0, F-10-only) is
  unaffected.
- **No runtime change applied** (the recommended fix regresses 6 tests; the
  shipped F-10-first order is correct for every reachable shape).
- Added latent-shape probe `NeoClrStructField_IlVtMethodLdfldaThisClrStructField`
  (+ host helper `SetTestVector3NoBindingByRef`) as a regression guard for the
  both-stamp shape's objIdx == -1 -> shape 1/2 routing. The probe does NOT
  FAIL-on-HEAD (cannot, given current Neo); it is a shape guard.
- **Correct future fix (deferred to constrained-VT follow-up):** the JIT
  discriminator gate (only stamp F-10 when the source is NOT an in-frame VT,
  making the two markers genuinely mutually-exclusive at the producer), NOT a
  runtime reorder.

**Verification:** NeoStep 190/190, NeoStep20 9/9, ClrStructField 8/8 (7 F-10 +
new probe). Legacy `Debug` 0 errors. Files touched (UNCOMMITTED, working tree
left for LEAD re-review): `TestCases/NeoClrStructFieldTest.cs` (new probe +
struct), `ILRuntimeTestBase/TestFramework/TestClass3.cs` (new byref-setter
helper), `openspec/changes/neo-clrstruct-field-of-il/design.md` (round-1
finding detail). NO `ILIntepreter.Neo.cs` change.

### Follow-ups from neo-clrstruct-field-of-il (ship, 2026-07-06)

F-10 / NEO-CLRSTRUCT-FIELD-OF-IL is RESOLVED. See the full §3 entry in
`.trae/documents/neo-deferred-items.md` (F-10 RESOLVED prepend + F-10-R1
accepted-known-deferred entry + §4 Resolved bullet). The follow-ups to track:

- **F-10-R1 -> constrained-VT JIT-discriminator gate.** The F-6/F-10 markers
  are NOT mutually-exclusive at the JIT discriminator (an IL **value type**
  with a CLR-struct field, `ldflda this.field` in a VT method, gets BOTH
  stamped, `Operand4 = 0x3`). The defect is fully latent (gated behind the
  DEFERRED `constrained.callvirt`-on-VT; for all reachable VT shapes
  `objIdx == -1` -> HEAD order routes correctly to F-6 shape 1/2). The
  reviewer's recommended runtime F-6-before-F-10 reorder was DISPROVEN by the
  fixer (broke 6 NeoStep17 F-6-only probes, 190->184). The correct future fix
  is the **JIT-discriminator gate** (only stamp F-10 when the source is NOT an
  in-frame VT, mirroring the F-6 source check in the type-spec pass, making the
  two markers genuinely mutually-exclusive at the producer). Route to the
  constrained-VT follow-up (`neo-step17-generic-byref-etc` or a Step 13 Area 3
  follow-up). Probe 4.8 (`NeoClrStructField_IlVtMethodLdfldaThisClrStructField`)
  is the green regression guard for the both-stamp shape's `objIdx == -1`
  routing.

- **Step 20 TC2/TC3/TC5 redirect-coverage.** These pass F-10 but hit DISTINCT
  Step-20 redirect edges (non-generic `Task`'s `Start` redirect null-SM; the
  `ValueTask` builder NRE; multi-await `Task<int>.get_Result` redirect
  "Method 'Task.Result' not found"). NOT F-10 (the F-10 OOB signature is
  `IndexOutOfRangeException`; these are redirect NREs/missing-method). They are
  Step-20 follow-ups; re-trimmed green, documented in
  `TestCases/NeoStep20Test.cs`. `neo-step20-async` resume (or
  `neo-step20-async-suspend`) owns them. The Step 20 sync slice went 2 -> 4
  green (TC4 AsyncVoidSync + TC6 AsyncExceptionFaultsTask newly unblocked).

- **Lesson (durable): a green smoke does NOT prove a design premise correct.**
  The propose-time premise ("Stfld_Ref/Ldfld_Ref already correct; only ldflda
  broken") was the load-bearing scoping assumption (it justified rejecting
  Option B and keeping the fix localized to `ldflda` + the byref consumers).
  The Block-0 reproducer dump DISPROVED it — all THREE heap field-access arms
  were broken. The fix had to be widened to all three arms. Re-affirms the
  Step 17 B1 / OPT-HARDEN K1 / F-MAJ-1 / Q-NEWOBJ family lesson: the dump-gate
  discipline is binding; a blast-radius claim is a HYPOTHESIS until a
  reproducer probe confirms it, NOT a premise to design against. When a design
  premise asserts "X is already correct," construct a reproducer that exercises
  X BEFORE scoping the fix around the assertion.

- **Gotcha (stale-build DLL hell, re-earned).** `dotnet build` reported "0
  errors" in ~1.8s WITHOUT recompiling TestCases when the source change was
  small (incremental hash hit); the resulting DLL did NOT contain the new probe
  symbols, so the smoke silently ran the OLD code (the JIT body dump of
  `NeoClrStructField_RegisterReuseEscape` showed no F-10 opcodes). Fix:
  `--no-incremental` on BOTH projects (CLI + TestCases) after any test-side
  change, AND confirm via `strings -e l <dll> | grep <literal>` (UTF-16;
  ASCII `strings` will NOT find .NET string literals) that the new symbols are
  in the built DLL. This is the documented incremental-hash-hit gotcha that
  also bit neo-opt-harden-2 (memory `cjk-write-encoding-corruption`-adjacent).
  Non-blocking but recurring; flag for any future change that adds new test
  symbols.

