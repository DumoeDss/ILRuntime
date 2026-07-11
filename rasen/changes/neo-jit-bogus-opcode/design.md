## Context

`ExecuteNeo` (`ILIntepreter.Neo.cs`) dispatches a compiled Neo body like this:

```csharp
OpCodeR[] body = method.CompiledFrame.NeoExecuteBody;     // line 1309
fixed (OpCodeR* ptr = body) {
    OpCodeR* ip = ptr;
    bool returned = false;
    while (!returned) {
        OpCodeREnum code = ip->Code;                       // line 1410
        switch (code) { /* ... arms ... */ }
        ip++;                                              // line 5359, unconditional
    }
}
```

The loop has **no bounds check**: termination relies entirely on a `case Ret:` (or a
throw) setting `returned = true`. On the full Neo smoke the `default:` arm (line 5357)
throws `opcode 2359324 (0x23F70C)`, a value that is **not** a named `OpCodeREnum`
member (the enum is all-implicit `0..328`). So either (a) `ip` is reading **past the
end of `body`** into adjacent managed heap, or (b) `ip` landed at a **wrong in-range
index** (a mis-targeted branch / switch) and is reading operand/register bytes of one
instruction reinterpreted as another instruction's `Code`.

The body `ExecuteNeo` runs is produced by the **Neo-only** back-half
(`JITCompiler.RunNeoBackHalf`):
`TypeSpecializeNeoOpcodes` -> `frame.CodeBody = res.ToArray()` -> `AllocateLocalStackSpaces`
-> `frame.NeoExecuteBody = CodeBody.Clone()` -> `Optimizer.LowerNeoOffsets(ref frame)`.
Legacy `ExecuteR` runs none of this and is green on the same DLL, which localises the
defect to these Neo-only passes.

Two facts narrow the root cause sharply:

- **`new OpCodeR()` zero-initialises and every `Translate` arm sets `op.Code`**. A purely
  uninitialised `Code` would read as `Nop` (0), not `0x23F70C`. So the garbage is NOT an
  unset field -- it is an **overread** (past the body) or a **misaligned read** (operand
  bytes read as `Code` via a wrong `ip` index).
- **`LowerNeoOffsets` is the only pass that changes body LENGTH.** It deletes synthetic
  `Push` instructions one at a time (in-place shift + `Array.Resize(ref body, ...)`,
  `Optimizer.Neo.cs` ~1210-1215) and re-maps every branch / `Switch` target via
  `FixBranchTargetsAfterRemove` (~1662). A mis-remap here, or a target category the helper
  misses, sends `ip` to the wrong place. The final resized body IS written back to the
  frame (`frame.NeoExecuteBody = body;`, line 1481), so a "stale frame array" hypothesis
  is **eliminated**.

Confirmed: the `Push`-deletion path runs only for calls / `newobj` with more than 3
register parameters (`pCnt > CallRegisterParamCount`). So the trigger involves a
**multi-argument call** followed by control flow that a later branch/switch target
resolves across the deleted `Push`.

## Goals / Non-Goals

**Goals:**
- Turn the silent garbage-opcode failure into a **loud, precisely-located** one
  (method + body index + raw bytes) so the root cause is one dump away.
- Root-cause and fix the actual Neo-lowering defect that produces the bad value.
- Ship a permanent, cheap dispatch-guard in `ExecuteNeo` as a regression tripwire so any
  future lowering regression fails immediately and diagnoseably instead of aliasing a real
  opcode.
- Add a `NeoStep` regression probe that would have caught the trigger.

**Non-Goals:**
- Implementing the *named* missing opcodes (`ldtoken`, bare-NIE, CLR static fields, etc.)
  -- those are sibling children of the `neo-overhaul` portfolio. This change only kills the
  *garbage* opcode and makes any *future* bad opcode loud.
- Rewriting `LowerNeoOffsets` or the optimizer passes broadly. The fix is scoped to the
  specific defect the reproducer confirms (mirrors the F-8 / F-MAJ-1 "don't mis-attribute"
  discipline already in this capability).
- Changing `OpCodeR` layout or the `OpCodeREnum` numbering.
- Any Legacy (`ExecuteR`) behaviour change.

## Decisions

### D1: Reproduce via an instrumented `default:` + a full (un-filtered) smoke run
The `NeoStep` filter excludes the crashing methods, so the bug is invisible under the
green 301/0/0 smoke. The instrumented `default:` logs **method display name, body index
`(int)(ip - ptr)`, `body.Length`, raw `Code`, and the full 24-byte `OpCodeR` field dump**
(Register1/2/3/4, Operand/OperandLong/Operand2/3/4). The dump distinguishes the two
hypotheses in one shot: if `body index >= body.Length` it is an **overrun** (H2 / a branch
past the end); if the index is in range but the slot's `Code` is garbage while its
neighbours are valid, it is a **mis-targeted ip** (H1) or a genuine corrupt slot (H3).

- *Alternative considered:* building a minimal standalone repro by hand. Rejected: we do
  not yet know the triggering IL shape; the instrumented full smoke **finds** it for us.

### D2: Root-cause hypothesis ranking (falsifiable, decided by the D1 dump)
1. **H1 -- `Push`-deletion target mis-remap (PRIMARY).** `FixBranchTargetsAfterRemove`
   re-maps `IsBranching` (`Operand`), `IsIntermediateBranching` (`Operand4`), and `Switch`
   jump-table entries. A target category it misses, an off-by-one on the deletion index, or
   an un-handled `Leave`/`Leave_S` (EH control flow, classified separately) lands `ip` at a
   wrong index. **Falsified iff** the dump shows the bad slot is past `body.Length` with no
   branch pointing there, or shows the slot is a correct terminator.
2. **H2 -- un-terminated body (SECONDARY).** Some lowered body has no reachable `Ret` /
   throw / infinite branch, so execution falls off the end. **Falsified iff** every suspect
   body's last instruction is a terminator.
3. **H3 -- genuinely corrupt in-range slot (WEAK).** **Falsified iff** the dump shows the
   bad slot's neighbours are also corrupt (passed-over memory) rather than valid opcodes.

The fix site is therefore determined at apply time: H1 -> `Optimizer.Neo.cs`
(`FixBranchTargetsAfterRemove` / the `Push`-deletion block); H2 -> ensure terminator
(lowering / `CleanupRegister`); H3 -> the specific rewrite that wrote garbage.

### D3: Ship a permanent `ExecuteNeo` dispatch guard (defense-in-depth)
Add, at the loop head (gated `#if ENABLE_NEO_MODE`, DEBUG-cost-only):
- a **bounds check** `if ((int)(ip - ptr) >= body.Length)` -> throw a precise
  `"Neo: ip ran past body end in <method> at index <i>/<len>"`;
- in the `default:` arm, when `code` is outside the named `OpCodeREnum` range but the slot
  is in-range, throw `"Neo: corrupt opcode <code> at <method>:<i>"` with the field dump.

- *Alternative considered:* DEBUG-only guard. Rejected: the failure is silent corruption
  in release Neo too; the check is one integer compare per instruction (negligible beside
  the dispatch), and its diagnostic value is high. Keep it in all Neo builds.

### D4: Legacy-neutrality by construction
`LowerNeoOffsets`, `TypeSpecializeNeoOpcodes`, and `ExecuteNeo` are all already
`#if ENABLE_NEO_MODE`. The guard and the fix land inside these Neo-only regions, so plain
`Debug` (Legacy) compiles them out. No `#if` needs to be added around Legacy code. A
stash-toggle plain-`Debug` + `useRegister=true` `NeoStep`-filter run confirms the Legacy
failure set is byte-identical.

## Risks / Trade-offs

- **[The full smoke NRE-crashes mid-run (`GenericMethodTest`), masking later triggers]** ->
  the instrumentation fires *before* the crash, so pre-crash triggers are captured; the
  apply worker iterates (fix one, rebuild, re-run) until the smoke either completes or only
  the known named-missing-opcode NIEs remain.
- **[Root cause turns out to be in `JITCompiler` branch-target resolution, not
  `LowerNeoOffsets`]** -> the D1 dump pinpoints it (operand bytes as `Code` vs overread);
  the design explicitly allows the fix site to move. The `neo-optimizer` requirement is
  written around the *invariant* (dispatch-safe body), not a specific function, so the spec
  stays correct regardless of which pass the fix lands in.
- **[Per-instruction guard cost]** -> single `int` compare; negligible vs the dispatch
  switch; Neo-only.
- **[Multiple distinct bugs share the symptom]** -> iterate; the requirement's scenarios
  enumerate the shapes (overrun, mis-target, corrupt slot) so each is individually guarded.
- **[Over-fixing: a blanket rewrite of `LowerNeoOffsets` risks regressing the green
  301/0/0]** -> scoped fix only (D2); full `NeoStep` smoke is the regression gate.

## Open Questions

- Exact fix site (`Optimizer.Neo.cs` vs `JITCompiler.cs`) -- resolved by the D1 reproducer
  dump at apply time.
- Whether more than one target category is mis-remapped -- resolved by iterating the
  instrumented run after each fix.
