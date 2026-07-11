# Planning Context — neo-f7b-sib-direct-call (F-7B-SIB triage, TRUE COMPLETION)

> SEED. F-7B-SIB: the direct-`Call_IL`/`CopyNeoCallThisBack` sibling of F-7B (the
> ref-type byref write-back dangling-mStack-index mechanism). UNREACHABLE today via
> the trivial inliner (it folds small direct targets). Triage: construct a
> non-inlinable probe -> reach + fix (the F-7B promotion), OR close as unreachable-
> needs-reproducer (the Q-STRUCT/Q-LONG pattern).

## What this change is (the F-7B sibling)

F-7B (the delegate-Invoke ref-type byref write-back) shipped the caller-owned mStack
slot promotion. The SAME mechanism exists on the direct-`Call_IL`/`CopyNeoCallThisBack`
path (`ILIntepreter.Neo.cs:2265` + `:543-595` -- the `Unsafe.CopyBlock` at `:578`
propagates the index verbatim), but it's UNREACHABLE today: the Neo trivial inliner
(`JITCompiler.cs:2931-2975`) folds small direct targets, so the byref stays in-frame
(no cross-frame dangling index). Surfaced by neo-f7-delegate-byref / F-7B.

## The triage (binding -- reach it or close it)

On HEAD `243c8a73`:
1. Can you CONSTRUCT a non-inlinable direct-call probe that reaches the F-7B-SIB gap?
   (A direct call to a target LARGE enough that the trivial inliner refuses it, where
   the callee REASSIGNS a ref-type byref param: `s = s + "!"`.) Try: a method with
   enough locals/complexity to defeat the inliner's size threshold, OR an explicit
   `[MethodImpl(MethodImplOptions.NoInlining)]` equivalent (if the Neo inliner honors it),
   OR a recursive/looping target.
2. If REACHABLE: the gap reproduces (the caller's ref-type var -> dangling after the
   direct call). Apply the F-7B promotion fix to the `Call_IL`/`CopyNeoCallThisBack`
   path (mirror the F-7B caller-owned mStack slot + the F-10 discriminator).
3. If UNREACHABLE (the inliner ALWAYS folds direct calls, OR no probe defeats it):
   CLOSE as unreachable-needs-reproducer (the Q-STRUCT/Q-LONG pattern -- a latent
   mechanism protected by the inliner; the F-7B fix pattern is recorded for when a
   non-inlinable probe lands). Honest close, NOT "left undone."

## The dump-gate discipline (binding)

The Q-STRUCT/Q-LONG/F-11/F-13 lesson: do NOT fix a gap without a REPRODUCER (an
unfalsifiable fix is rejected). If the inliner makes F-7B-SIB genuinely unreachable,
the honest close is "unreachable-needs-reproducer" with the F-7B fix pattern recorded
-- NOT a speculative fix to a path that never executes (untested dead code).

## Authoritative prior context

1. `openspec/changes/archive/2026-07-09-neo-f7b-reftype-writeback/{design.md, ship-log.md}`
   -- F-7B (the caller-owned mStack slot promotion) + the F-7B-SIB finding (the direct-
   Call_IL sibling, unreachable via the inliner).
2. `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs:2265` + `:543-595`
  (Call_IL CopyNeoCallThisBack -- the SIB path) + the F-7B fix (`NeoRunDelegateTargetOnThis`
   caller-owned slot + Stind_Ref/Ldind_Ref arms).
3. `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs:2931-2975` (the trivial inliner
  -- its size threshold / what defeats it).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep19   # the non-inlinable probe (if reachable)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 235/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-byref/spec.md` (or neo-dispatch) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the triage verdict: REACHED->fix / UNREACHABLE->close-needs-
reproducer, with the probe-construction evidence), `specs/<cap>/spec.md` (delta),
`tasks.md`.
