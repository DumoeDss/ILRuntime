# Handoff: neo-overhaul -- LEAD #14 (Wave-2: full-smoke-driven bug fix; 189 -> 133)

> Read lead-13.md first (the overhaul's 9-child correctness wave + the doc-staleness correction). THIS session
> (lead-14) was driven by the USER's mandate: "你是检查代码对比判断了吗? 这些失败都是代码有问题吧? 不要再出现虚假任务了! 修复所有 bug,让测试全部通过! 并将遗漏的任务补充!" -- i.e. stop trusting doc judgments, verify
> against REAL runs, fix real bugs, fill in the missed tasks. Full autonomy (--no-gate), drive deep.

## The pivot: GROUNDING replaced doc-judgment with real-run truth
The user caught that lead-13/CLAUDE.md claimed failures were "mostly unimplemented-opcode NIEs" -- that was a
doc-derived claim, never run. A grounding worker ran the FULL Neo smoke (no filter): **914 ran / 189 failed, no
crash; only 2 are NotImplementedException; 187/189 are REAL runtime bugs** (InvalidCastException 56, NRE 45,
MissingMethod 23, ...). NeoStep filtered smoke is 380/0 green; ALL 189 failures are non-NeoStep TestCases. **The
user's intuition was right: these are code bugs, not TODOs.** The grounding report (`fullsmoke-ground-2026-07-13.md`)
+ a 17-cluster root-cause plan (`wave2-rootcause-plan.md`) are the wave-2 worklist.

## Position
HEAD `0414c296`+ (this wave's 3 children: C1 `68038a6e`, C4 `0414c296`, C2 pending-commit). Full Neo smoke
**189 -> 133 (−56)**. NeoStep **380/0**. Legacy-neutral throughout. 3 wave-2 children shipped, each verified by the
full-smoke failure count dropping (the truth metric).

## What shipped this session (3 wave-2 children, each propose->apply->verify->review->ship, committed+pushed)
- **C1 `neo-delegate-adapter-clr-cast` (`68038a6e`)** -- 189->153 (−36). IL delegate (MethodDelegateAdapter)
  crossing to CLR must convert via `CheckCLRTypes`; Neo omitted it at Stsfld + NeoWriteClrObjectField + 41 stale
  autogen binding sites. Fix = CheckCLRTypes at the 3 boundaries + hand-port 41 stale sites (child-28 stale-binding class).
- **C4 `neo-callvirt-gettype-vtable` (`0414c296`)** -- 153->140 (−13). (4a) IL instance calling inherited non-virtual
  `GetType()` threw "cannot resolve VTable slot" -- Neo callvirt didn't fall back to CLR dispatch; fix = CLRMethod
  fallback in ResolveNeoCallvirtILTarget + ObjectGetTypeNeo redirect. (4b) a STALE Step-7 `.cctor` suppression left
  every IL static field with an inline initializer null -> "this is null"; **highest-value stale-TODO in the tree**;
  fix = lift suppression + guard LoadNeoAssembly single-invoke (MAJOR-1, AOT double-cctor, fixed pre-ship).
- **C2 `neo-iltype-cast-clr-base` (pending commit)** -- 140->133 (−7). ILTypeInstance passed as a CLR-base/interface-typed
  CLR-call arg wasn't unwrapped to its CrossBindingAdaptor; fix = project ILTypeInstance->CLRInstance at the
  `InvokeNeoClrMethod` choke point (covers redirect + reflection). The task's castclass/isinst framing was DISPROVEN
  (re-audit caught it -- the bug is in the call-arg path).

## Key lessons (reaffirmed, with new teeth)
1. **GROUND IN A REAL RUN, ALWAYS.** The "mostly NIE" claim was never run; the grounding run overturned it (187/189
   real bugs). Every wave-2 child's success = the full-smoke count dropping, verified by re-running the full smoke.
2. **Re-audit catches wrong FRAMINGS, not just wrong verdicts.** C2's "castclass/isinst" framing was wrong (real
   site = call-arg projection); C1's "delegate cast" was right. Always build a reproducer + Neo-vs-Legacy code
   comparison before proposing.
3. **The stale-autogen-binding class is recurring** (child-28 -> C1 -> C2). Generator is correct; committed bindings
   predate fixes; regen is GUI-bound. Hand-port to match the generator. Full GUI regen is the durable fix.
4. **The Step-7 `.cctor` suppression was the highest-value stale-TODO** (C4-4b) -- nullified every IL static field
  with an inline initializer. Any "static field null / this-is-null on a static field" -> first confirm .cctor runs.

## Remaining (the wave-2 worklist -- ~133 failures, ~13 open clusters)
Authoritative: `rasen/changes/neo-overhaul/handoff/wave2-rootcause-plan.md` (17 clusters; the 3 done are C1/C4/C2).
Next by coverage:
- **C16 generic NRE bucket (~25)** -- residual NRE after C4's callvirt-this-null fix; several distinct roots; needs sub-clustering.
- **C12 "Type must be a runtime Type" (10)** -- Delegate.CreateDelegate / Enum needs a runtime Type (ILRuntimeType bridge).
- **C10 array/struct index-out-of-range (~11)** -- size/offset math in ldelem/stelem/field-offset.
- **C9 Hotfix "is not bound!" (10)** -- AUDIT FIRST (test-setup vs engine; HotfixAOT.patch coverage).
- **C11 TestValueTypeBinding misc (~10)** -- per-test triage; overlaps C10/C1.
- **C5 ILTypeInstance parameterless ctor (7)** -- Activator/JSON ctor path (child-22 edge).
- **C8 async stateMachine null (4)** -- Step-20 edge.
- **C7 LowerNeoOffsets "could not find Push" (3)** -- JIT Push-deletion (child-1/14 lineage).
- **C6 interface dispatch slot (3)**, **C13 "Cannot find Delegate Adapter" (3)**, **C14 TargetException (4)**, **C3 enum (~8)**, **the lone AV UnitTest_10037**.
Plus the C2-deferred distinct roots (InheritanceTest16 Muli_R4 JIT; 21/22 NRE; TestAs03/RefOut cast-to-Adaptor;
StructTest6 reverse; GenericMethodTest11 constrained-callvirt) and the C4-deferred (Type.GetType(string) null;
byref out-param write-back).

## Working set + verification discipline (MUST follow)
- Build/test (ALWAYS -f net8.0; CLI=Debug_Neo --no-incremental; NEVER TestCases with Debug_Neo). After touching
  ILRuntimeTestBase: `dotnet build-server shutdown` + `-p:UseSharedCompilation=false`.
- **Per-child verification = the full-smoke count dropping.** `dotnet run -c Debug_Neo -f net8.0 --project
  ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true > .tmp-postfix.log 2>&1`,
  extract `Ran N tests, M failded`. Record the delta (133 -> lower). A child that doesn't move the number is NOT done.
- NeoStep smoke must stay 380/0 (no regression). Legacy-neutral (plain Debug+useRegister=true spot-tests pass).
- Commit trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`; `-F .git/cmsg.txt`;
  `git config lfs.useslockfiles false` before push.

## Next action
Pick the next cluster from `wave2-rootcause-plan.md` by coverage (C16 NRE sub-clusters or C12 are next). Each child:
grounding-report test names -> re-audit (Neo-vs-Legacy + reproducer) -> fix -> full-smoke delta -> review -> ship.
The handoff/ transcriptions (lead-1..13) + this lead-14 are the full session record. NeoStep 380/0 is the
environment-health gate; the full-smoke count (133, falling) is the bug-fix progress gate.
