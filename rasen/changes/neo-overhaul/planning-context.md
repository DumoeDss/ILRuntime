# Planning Context — neo-overhaul (the broader Neo opcode/correctness overhaul)

> LEAD-seeded digest. The planner reads this FIRST, then researches only what is missing.
> Append durable new findings (decisions, discovered constraints) after each propose.

## Mandate (from lead-11 next-action, user-confirmed)
The `neo-completion-portfolio` is CLOSED (59 children done, frontier empty). The remaining
work is the **broader Neo overhaul**: drive the unimplemented-opcode + JIT-correctness surface
to functional completion. Full autonomy (`--no-gate`), commit+push after each clean child,
relay-not-stop on context limit, drive DEEP on the 1M window.

## Scope basis (LEAD investigation, 2026-07-11)
Two complementary methods:
1. **Full Neo run** (`dotnet run ... TestCases.dll ... true` with NO NeoStep filter) captured the
   NotImplementedException surface pre-crash. CAVEAT: the full run CRASHES (exit 127,
   NullReferenceException in `GenericMethodTest.GenericTest` ~Dictionary Enumerator). Frequency
   data is pre-crash only, NOT exhaustive.
2. **Static switch-diff** of `OpCodeREnum` (329 implicit members, 0 explicit values) vs the
   `case OpCodeREnum.X:` labels in `ExecuteNeo` (ILIntepreter.Neo.cs). MISLEADING on its own:
   many "unhandled" members are JIT-lowered (short branches, shorthand loads) or handled under
   Neo-split names (`Ldfld` -> `Ldfld_Primitive`/`Ldfld_Ref`/`ldfld.value`). Trust RUNTIME
   frequency, not the raw diff.

## The 6 serial children (shared ExecuteNeo/JITCompiler files => strict serial)

### 1. neo-jit-bogus-opcode  [CRITICAL correctness -- FIRST]
The ExecuteNeo default case (`ILIntepreter.Neo.cs:5357`):
`throw new NotImplementedException(string.Format("Neo: opcode {0} not yet implemented (Step 6)", code));`
where `OpCodeREnum code = ip->Code;` (line 1410). The runtime shows **`opcode 2359324 not yet
implemented`** (~8 consistent pre-crash hits). 2359324 = 0x23F70C is NOT a named `OpCodeREnum`
member (the enum is all-implicit 0..328, confirmed zero explicit-value members). => the JIT
writes a GARBAGE value into `ip->Code` for ~8 instruction sites. Likely a field-offset bug in
`OpCodeR` emission (an operand written into the Code field) or an uninitialized Code.
- This is CORRECTNESS, not a missing feature. A garbage opcode could silently match a real one
  and corrupt execution. Do this FIRST.
- Reproducer path: instrument the default case to log `method + IL offset + ip->Code + the
  surrounding OpCodeR bytes` when `code` is out of the named range, run the full smoke, find the
  minimal case. Then inspect how the JIT (`JITCompiler.cs`) emits that instruction's `OpCodeR`.
- Verify the fix with a NeoStep guard + full NeoStep smoke green (301/0/0 baseline).

### 2. neo-ldtoken  [HIGH -- dominant missing opcode, ~60 pre-crash hits]
`ldtoken` pushes a metadata-token handle onto the eval stack:
- `RuntimeTypeHandle` (ldtoken + `Type.GetTypeFromHandle` => `typeof(T)`)
- `RuntimeMethodHandle`
- `RuntimeFieldHandle`
Used pervasively by reflection, attribute ctos, LINQ expression trees, etc. Currently hits the
Step 6 default. Implement the ExecuteNeo arm: resolve the token (Operand) to IType/IMethod/IField,
construct the matching `Runtime*Handle` value (a boxed struct or a Neo value-type slot), push it.
Mirror how Legacy (`ILIntepreter.Register.cs`) handles `Ldtoken`. Add a NeoStep probe
(`typeof(int)`, `typeof(string)`, a method/field handle via reflection).

### 3. neo-bare-nie  [small-mod]
5x `The method or operation is not implemented.` = a bare `throw new NotImplementedException()`
with no message string. These are NOT in ILIntepreter.Neo.cs's tagged NIEs (those all carry a
"Step N" message). Locate them (grep the ILRuntime tree for bare `new NotImplementedException()`
reachable in Neo mode), implement or guard each. Likely in CLR binding/redirect helpers invoked
from ExecuteNeo.

### 4. neo-clr-static-fields  [mod]  -- Step 25 S3-4 capstone
`Stsfld`/`Ldsfld` on a **CLR** static field throw
`Neo Stsfld: CLR static field not implemented (Step 25 S3-4; the capstone is IL-only)`
(ILIntepreter.Neo.cs:3755 / 3798). The Step-25 S3-4 .cctor capstone only handled IL statics.
Resolve the CLR static field via `CLRType` + read/write the underlying `System.Reflection.FieldInfo`.
Add a NeoStep probe (a CLR type's static field read+write through IL).

### 5. neo-raw-field-edges  [small-mod]
Raw `Ldfld`/`Stfld` (and possibly `Ldloc`/`Stloc`/`Ldarg`) occasionally reach the Step 6 default
(~2 hits each pre-crash). The Neo JIT is supposed to split these into typed arms
(`Ldfld_Primitive`/`Ldfld_Ref`/`Ldfld_Value`/`ldfld.value` etc.); some shape escapes the splitter.
Find why (a field/type category the splitter's `if/else` chain misses) and add the missing lowering
or a default-case fallback. Add probes.

### 6. neo-valuetask-marshal  [mod-large]  -- deepest, LAST
VT1/VT2/VT6 `ValueTask<T>` async failures. Re-routed from the DISPROVEN
`neo-clrstruct-sm-field-layout` child (B1 field-layout collision is BENIGN -- Primitives[] and
ManagedObjects[] are disjoint; the shared PrimitiveOffset cannot corrupt). The REAL bug is
call-arg marshalling: in `AsyncValueTaskMethodBuilder_T_SetResult_Neo`
(`CLRRedirections.AsyncNeo.cs`), `curPrim += 8` skips the builder byref-`this`, but the ValueTask
builder's byref-`this` occupies 16 call-frame bytes (8-byte F-10 byref `(objIdx,offset|flag)` +
8-byte struct flat-bytes copy) => `ReadResultParam` reads `frameBase[8]` (stale residue) instead of
`frameBase[16]` (the real result). Compare with `AsyncTaskMethodBuilder_T_SetResult_Neo` (TC8, WORKS,
also `curPrim += 8`) to find the 8-vs-16 difference. Fix the skip (+ matching SetException /
AwaitUnsafeOnCompleted redirects). NOTE: there may be a child-4 stash (`child4-valuetask-blocked-partial`)
with VTDBG2 diagnostics that MUST be removed before shipping; git status at overhaul-start showed
NO source modifications (stash is NOT applied) -- verify before starting.

## Build/test (ALWAYS -f net8.0; CLI = Debug_Neo --no-incremental; NEVER TestCases with Debug_Neo)
- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental`
- `dotnet build TestCases/TestCases.csproj -c Debug`
- NeoStep smoke: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` => 301/0/0 baseline.
- Full Neo run (for scoping): drop the `NeoStep` filter; CRASHES pre-completion (known).

## Gotchas (from lead-11)
- Subagents CAN run bare `dotnet build`/`run` (allowlisted) + the Grep/Read tools (NOT bash
  grep/tail/cd). Recover from 502/socket interrupts via on-disk work + transcripts.
- `git commit` without checking staged set => accidental partial commit. Always `git status` first.
- GitHub push needs `git config lfs.useslockfiles false` (locks-verify endpoint blocked here).
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- CJK in Write can corrupt ~0.5% chars to U+FFFD; verify/fix with pure-ASCII codepoints.
- Conditional compilation: Neo code is `#if ENABLE_NEO_MODE`. Legacy-neutral required (a change
  gated under ENABLE_NEO_MODE is Legacy-neutral by construction).

## Child 1 DONE (neo-jit-bogus-opcode, shipped 2e031b16 2026-07-11) — durable findings
- **Root cause was `FixBranchTargetsAfterRemove` missing Leave/Leave_S.** When proposing/fixed
  any future "wrong instruction executed / garbage opcode" Neo bug, the FIRST suspect is
  `Optimizer.Neo.cs LowerNeoOffsets` — it is the ONLY Neo pass that changes instruction-body
  LENGTH (deletes synthetic `Push` for Call/Newobj with >3 register params + re-maps targets).
  `TypeSpecializeNeoOpcodes` only rewrites opcodes in place (no length change).
- **A permanent `ExecuteNeo` dispatch guard now ships** (loop-head bounds check BEFORE the
  `ip->Code` deref + a default-arm out-of-range-Code check that dumps fields). Named-but-
  unimplemented opcodes (ldtoken etc.) still fall through to the Step-6 message. A NeoStep
  regression probe must FAULT (the pass criterion is "ran without throwing"); a wrong-value
  probe will NOT fail.
- **`OpCodeR` is `[StructLayout(LayoutKind.Explicit)]`, a 24-byte union** (Code@0, Register/
  offset aliases @4-11, wide-immediates @12-19). A RECURRING sharp edge (3rd+ instance after
  F-8 / OPT-HARDEN-K1 / F-MAJ-1): any `LowerNeoOffsets`/rewrite case that stamps a field MUST
  verify it does not alias a wide-immediate a runtime consumer reads.
- **LATENT landmine -> child 7 (`neo-overhaul-eh-table-remap`):** `method.ExceptionHandlerRegister`
  (TryStart/TryEnd/HandlerStart/HandlerEnd, body-indexed) is NOT remapped by `LowerNeoOffsets`.
  Trigger: try/catch/finally + >3-arg call/newobj that THROWS. Pre-existing, not triggered by
  the current 306 smoke. LowerNeoOffsets lacks an ILMethod handle, so the fix must plumb
  `exceptionHandlerR` through it.
- **After child 1, the full Neo smoke's only remaining default-arm entry is `ldtoken`** (named
  opcode -> child 2). Zero garbage opcodes remain.

## Child 2 DONE (neo-ldtoken, shipped 2026-07-11) — durable findings
- **`OpCodeR` spare-field map (CRITICAL for any future opcode that needs a spare int slot):**
  `Operand3` (@16-19) is the HIGH dword of `OperandLong` (@12-19) -- NEVER a safe scratch when
  `OperandLong` is in use (this was MAJOR-1: stamping Operand3 clobbered the field path's
  declaring type). `Operand4` (@20-23) is the ONLY genuinely-disjoint int spare. The compaction
  pass at `Optimizer.Neo.cs:1711-1713` remaps Operand4 consistently. Field offsets (from
  `OpCodes/OpCode.cs:35-71`, `[StructLayout(LayoutKind.Explicit)]`): Code@0, DstOffset@4,
  Register1-4 aliases, Operand@8, Operand2@12(low)/OperandLong@12, Operand3@16(=OperandLong hi),
  Operand4@20.
- **Neo dispatch uses `RedirectMapNeo` exclusively** (separate from Legacy's `RedirectMap`). A
  Legacy redirect does NOT run under Neo. Autogen Neo CLR bindings with value-type params are
  often broken `default(...)` stubs (`// TODO: ByRef or unsupported ValueType parameters in Neo`);
  any Neo opcode whose result feeds such a binding needs the binding fixed too (ldtoken ->
  GetTypeFromHandle was the canonical case).
- **A NeoStep regression probe must FAULT to fail** (the pass criterion is "ran without
  throwing"). A probe that only produces a wrong value will NOT fail. Design probes whose result
  is observably wrong OR that throw without the fix. An asymmetric `catch(NotImplementedException)`
  guard is sound when the regression throws a different (uncaught) exception.
- **ldtoken field path is reached by C# array initializers** (`RuntimeHelpers.InitializeArray` +
  `ldtoken <PrivateImplementationDetails>`). Surfaced follow-up: `RuntimeHelpers.InitializeArray`
  has no `RedirectionNeo` -> array initializers can't complete end-to-end in Neo (candidate child).
