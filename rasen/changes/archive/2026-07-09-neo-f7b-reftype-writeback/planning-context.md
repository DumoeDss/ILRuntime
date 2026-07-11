# Planning Context — neo-f7b-reftype-writeback (F-7B TRUE COMPLETION)

> SEED. F-7B: a delegate `ref` param where the callee REASSIGNS the referent
> (`s = s + "!"`) leaves a dangling mStack index. The primitive-byref case (F-7)
> shipped; F-7B is the reference-type-writeback case. Scope-AWARE: this is a deep
> mStack-lifetime feature; isolate + decide ship-vs-sequence.

## The diagnosis (from neo-f7-delegate-byref, do NOT re-derive)

F-7 (primitive byref delegate) shipped. The remaining edge: in-place write-back of
a **callee-CREATED reference object** (`s = s + "!"`) leaves a dangling mStack index
-- the new object lands in the CALLEE's frame ref region, which `ExecuteNeo:4703`
pops on return. So the caller's ref param ends up pointing at a popped (reused) mStack
slot. The `ref string` MARSHAL/READ path works (F-7 shipped it); the ref-type
WRITE-BACK (when the callee creates a new object + assigns it through the ref) is the
gap. Fix = mStack lifetime promotion (the written-back object must survive the callee's
frame pop, rooted in the caller's frame / a GC handle).

## The dump-gate (binding -- IS the promotion tractable?)

On HEAD `12d9e809`:
1. Reproduce: a `ref string` delegate param where the callee does `s = s + "!"`
   (reassigns the referent). After the call, the caller's variable -> dangling/garbage
   (the callee's frame ref region was popped). Cite the pop site (`ExecuteNeo:4703`
   RemoveRange) + the write-back site (`CopyNeoCallThisBack` in the F-7 fast path).
2. The mStack lifecycle: how does a normal (non-byref) reference RETURN survive? (A
   return value is read BEFORE the pop.) The byref-writeback case writes to the CALLER's
   frame DURING the callee's execution, but the new object is in the CALLEE's ref region
   -> popped. The fix: when CopyNeoCallThisBack writes a ref-type back through a byref,
   the object must be PROMOTED (copied/moved to the caller's mStack / a GC-rooted
   location) BEFORE the callee's pop.
3. **Scope-aware:** is the promotion SMALL (a CopyNeoCallThisBack fix that, for a
   ref-type writeback, copies the object into the caller's frame ref region instead of
   leaving the callee index) or LARGE (a fundamental mStack-ownership redesign)?
   Ship if tractable; sequence if deep.

## Authoritative prior context

1. `openspec/changes/archive/2026-07-09-neo-f7-delegate-byref/{design.md, ship-log.md}`
   -- F-7 shipped (the same-frame fast path + byref relativization); F-7B is the
   recorded follow-up.
2. `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- the F-7 fast path
  (`NeoRunDelegateTargetOnThis` + `CopyNeoCallThisBack` write-back) + the ExecuteNeo
   return/pop (`:4703` RemoveRange) + the mStack (`AutoList`) lifecycle.
3. `.trae/documents/neo-deferred-items.md` -- F-7B / NEO-REF-TYPE-BYREF-WRITEBACK row.

## Scope (TRUE COMPLETION -- isolate first)

Success criterion: a `ref string` (or ref reference-type) delegate param where the
callee reassigns the referent -> the caller observes the NEW object (not dangling).
Adversarial: the caller's variable holds the callee-created object after the call.
If the promotion is a deep mStack redesign (LARGE), isolate + document + sequence
honestly (TRUE-COMPLETION: the follow-on is driven next, not parked).

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep19   # the ref-type-writeback probe
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 233/0/0 regression
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal.

## Spec authoring traps
- `specs/neo-dispatch/spec.md` (or neo-byref) delta PURE ASCII; SHALL-first. Legacy is the REFERENCE. Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral.

## Deliverables
`proposal.md`, `design.md` (the ISOLATED mStack-lifetime root cause + the promotion
fix OR the honest sequence decision, file:line-cited), `specs/<cap>/spec.md` (delta),
`tasks.md`.
