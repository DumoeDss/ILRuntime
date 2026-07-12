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

## Post-child-2 RE-SCOPE (full Neo smoke, pre-crash, 2026-07-11) — frequency-ordered gaps
Re-prioritized the remaining children by CURRENT frequency (lead-11 "proceed by frequency +
re-audit" discipline). The full smoke still NRE-crashes mid-stream (pre-existing); counts are
pre-crash. Bare-NIE = `throw new NotImplementedException()` with no message string (208 such
sites exist in ILRuntime/Runtime; the Neo-reachable subset is what the 38 hits come from).

| hits | gap | child |
|------|-----|-------|
| 38 | CLR static fields (Stsfld/Ldsfld on a CLR type; ILIntepreter.Neo.cs:3867 Stsfld + 3910 Ldsfld `else` branches) | neo-clr-static-fields (NEXT) |
| 36 | raw `Stfld` reaches the Step-6 default (the Neo typed-splitter misses a shape) | neo-raw-stfld-ldfld |
| 38 | bare NIE ("The method or operation is not implemented." — no message) | neo-bare-nie |
| 18 | raw `Ldfld` reaches the default (splitter gap, sibling of Stfld) | neo-raw-stfld-ldfld |
| 18 | "CLR value type with reference fields and no ValueTypeBinder (Step 13b)" (incl. RuntimeHelpers.InitializeArray's RuntimeFieldHandle) | neo-clr-vt-reffields-binder |
| 10 | "Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred" | (fold into clr-static or own child) |
| 5/4/2 | Ldsflda / Conv_R_Un / Switch opcodes reaching default | neo-misc-opcodes (batch) |
| 2 | "unsupported CLR primitive for Unbox: ILEnumTypeInstance" (Unbox of an enum) | neo-misc-opcodes |

**Revised execution priority:** clr-static-fields -> raw-stfld-ldfld -> bare-nie -> clr-vt-reffields-binder -> misc-opcodes -> valuetask-marshal -> eh-table-remap. (Original orderedFrontier in portfolio-run.json superseded by this table.)

## Child 3 PROPOSED (neo-clr-static-fields, 2026-07-11) -- durable findings
- **CLRType already exposes static-field access; NO new API + NO JIT change.**
  `CLRType.GetField(hash)` -> `FieldInfo` (walks BaseType); `GetFieldValue(hash,
  null)` -> `FieldInfo.GetValue(null)`; `SetStaticFieldValue(hash, value)` ->
  `FieldInfo.SetValue(null, value)` (`CLRType.cs:404/452/529`). This is the SAME
  mechanism Legacy `ExecuteR` Stsfld/Ldsfld use (`ILIntepreter.Register.cs:3303/
  3332`). The `ip->OperandLong` encoding is shared IL/CLR (`GetStaticFieldIndex`
  `AppDomain.cs:2203-2227`, SAME else-branch for CLR types): `(typeHash<<32) |
  fieldHash`; `sIdx=(int)ip->OperandLong` is the field hash.
- **CLR-static read/write category mapping (the apply recipe):** discriminate on
  the field's CLR `System.Type` (`f.FieldType`), NOT an IType. Stsfld (frame->
  object): primitive->`NeoBoxPrimitiveByType(ft,srcSlot)` (the :5950 helper, now
  0 callers -- this change is its first live caller; prefer it over
  `NeoBoxReturnValue` which needs an IType); VT->`ReadNeoValueType`; ref->mStack
  index. Ldsfld (object->frame): primitive->`NeoWritePrimitiveToFrame`; VT->
  `WriteNeoValueType`; ref->`mStack.Add` TEMP-ref (NOT `frameRefBase+dstRefOffset`
  -- Stsfld/Ldsfld have NO dstRefOffset operand; Box/Unbox use dstRefOffset
  because THEY carry `ip->Operand3` -- a different opcode contract). Unwrap
  CrossBindingAdaptorType->ILInstance on read (Legacy parity).
- **Capability home = `neo-optimizer`** (owns Step-25 static-field work / the
  Stsfld-Ldsfld arms); ADDED a new requirement (no prior opcode-level requirement
  existed by name). Probe must FAULT (current NIE) + assert the round-trip value
  (child-1/child-2 discipline). Probe infra: add `public static int` to a host
  CLR helper (`TestClass3`) for the Stsfld write round-trip; `string.Empty`
  (ref read) + `IntPtr.Zero` (VT read) need NO infra. Out of scope: CLR VT with
  ref fields + no binder (sibling `neo-clr-vt-reffields-binder`, the Step-13b NIE
  inside ReadNeoValueType/WriteNeoValueType); `Ldsflda` (child neo-misc-opcodes).

## Child 4 DONE (neo-raw-stfld-ldfld, 2026-07-11) -- durable findings
- **Escaping shape = a field whose DECLARING type is a CLRType.** The Neo typed-
  splitter rewrites CIL ldfld/stfld into the typed arms ONLY for an ILType
  declaring type (`JITCompiler.cs` `if (type is ILType)`); the `else` branch
  leaves the raw `OpCodeREnum.Ldfld`/`Stfld` with
  `OperandLong=(typeHash<<32)|fieldHash` (IDENTICAL to Legacy's raw encoding;
  Legacy `ExecuteR` handles raw Stfld @3093 / Ldfld @3197). **No JIT change.**
  Fix = an `ExecuteNeo` raw-handler case for each + adding the raw opcodes to the
  `Optimizer.Neo.cs` offset-lowering case-lists (the EXISTING `DstOffset=R1,
  SrcOffset=R2` blocks; the Ldfld_Ref-only `Operand=RefOffset` stamp is skipped
  because `op.Code != Ldfld_Ref` -- do NOT add an Operand/Operand4 assignment;
  field identity lives in OperandLong).
- **Owner-offset convention (mirrors the typed arms):** Ldfld owner=`SrcOffset`
  (==DstOffset; R1==R2=baseRegIdx-1); Stfld owner=`DstOffset`, value=`SrcOffset`
  (R1=owner=baseRegIdx-2, R2=value=baseRegIdx-1). Decode `typeHash=(int)((ulong)
  OperandLong>>32)`, `fieldHash=(int)OperandLong`; `ct=AppDomain.GetType(typeHash)
  as CLRType`; `f=ct.GetField(fieldHash)`.
- **Owner-representation map (the load-bearing detail; discriminate on
  `ct.TypeForCLR.IsValueType` + opcode):**
  - CLR ref-type owner (Ldfld/Stfld): owner slot first int = mStack index of the
    boxed CLR object -> `NeoReadClrObjectField`/`NeoWriteClrObjectField` (Area 4d,
    `ILIntepreter.Neo.cs:5773/5784`).
  - CLR value-type owner, **Ldfld**: owner slot = inline FLAT BYTES (ldloc by-
    value; a CLR VT local is flat managed bytes under the Neo model) -> box the
    WHOLE struct (`ReadNeoValueType`) + `f.GetValue(boxed)` -> marshal to dest.
  - CLR value-type owner, **Stfld**: owner slot = frame-native byref
    `(-1, structBaseOff)` (ldloca) -> box the whole struct from the byref target,
    `f.SetValue(boxed, value)`, `WriteNeoValueType` back (box/mutate/unbox).
  - Reuse the child-3 Stsfld/Ldsfld read/write-by-field-type marshalling verbatim:
    read slot->object = `NeoBoxPrimitiveByType`(prim) / `ReadNeoValueType`(VT) /
    `mStack[idx]`(ref); write object->slot = `NeoWritePrimitiveToFrame`(prim) /
    `WriteNeoValueType`(VT) / `mStack.Add` temp-ref (ref).
- **GOTCHA: `System.Reflection.FieldInfo` has NO public `GetFieldOffset()` and the
  codebase has no offset-of precedent.** Do NOT chase a field-native-offset read;
  use the BOX-ROUNDTRIP (`ReadNeoValueType` whole-struct + `FieldInfo.GetValue`/
  `SetValue`) for the value-type owner. It is correct for any field category on a
  CLR struct whose flat bytes ARE the CLR managed layout (`Unsafe.SizeOf` via
  `GetNeoValueTypeManagedSize`), and it NIEs NATURALLY for ref-field structs (the
  Step-13b sibling inside `ReadNeoValueType`) -- so no separate guard is needed.
- **Deferred (tagged NIE distinct from the Step-6 default, fail-loud):** IL-
  instance-with-CLR-base-field (`mStack[objIdx] is ILTypeInstance ||
  CrossBindingAdaptorType`; `TestCls : ClassInheritanceTest`, ~5 hits) and CLR-
  struct array element (`mStack[objIdx] is Array`, `arr[i].X`, ~2 hits). These
  are NOT required scenarios; a future child can route the IL-base case through
  the instance's CLR backing and the array case through the element byref.
- **Verify:** stash-toggle FAIL-on-HEAD (Step-6 NIE) -> PASS-after. NeoStep smoke
  **316/0/0** (314 + TC1 CLR-ref int round-trip + TC2 CLR-VT TestVector3 X/Y/Z;
  typed arms `Ldfld_*`/`Stfld_*`/`ldfld.value`/`stfld.value` UNREGRESSED). Full
  smoke (pre-crash, known unrelated segfault/NRE): raw Stfld 36->0, Ldfld 80->0
  (Step-6 message eliminated; 14 tagged-NIE occurrences = the deferred shapes).
  Legacy-neutral: plain `Debug`+`useRegister=true`+`NeoStep` = 316 ran / 17 failed
  == baseline 314 ran / 17 failed (same set; both new probes pass under Legacy).

## Child 6 DONE (neo-clr-vt-reffields-binder, 2026-07-11) -- durable findings
- **The 18 "CLR value type with reference fields ... RuntimeFieldHandle (Step 13b)"
  hits are ALL the C# array-initializer intrinsic** (`newarr; dup; ldtoken
  <PrivateImplementationDetails blob>; call RuntimeHelpers.InitializeArray`). Fix =
  a Neo redirect `CLRRedirections.InitializeArrayNeo` registered on `RedirectMapNeo`
  (AppDomain ctor, `#if ENABLE_NEO_MODE`, mirrors the Legacy registration). It reads
  param 0 (Array) + param 1 (byte[]) via `ReadNeoReference` and `Marshal.Copy`s the
  blob into the pinned array (model `DelegateCombineNeo`). Void -> no retDst write.
  Stash-toggle: stashing ONLY the registration reproduces the exact Step-13b NIE.
- **BLOB DATA PATH (load-bearing; planner's ManagedObjects assumption was DISPROVEN):**
  the `<PrivateImplementationDetails>` blob field's type is a compiler-generated
  `.size N` struct with ZERO declared instance fields -> its computed
  `TotalPrimitiveSize=0` AND `TotalReferenceCount=0` (verified via diag: VT.TPS=0
  VT.TRC=0). So the declaring type's `StaticTotalReferenceCount` is 0 -> Neo static
  instance `ManagedObjects` is **null** -> `ILTypeInstance`'s InitialValue replay
  (`if (managedObjs != null)`) **never stored the byte[]**. The blob is reachable ONLY
  via Cecil `ilt.StaticFieldDefinitions[sIdx].InitialValue`. So the Neo `ldtoken`
  field path was extended to detect a non-empty Cecil `InitialValue` and push it as a
  Neo reference (FIRST arm, before the VT arm -- the blob field IS a value-type
  ILType). The `RuntimeFieldHandle` callee slot is ~8 bytes but `ReadNeoReference`
  consumes only the leading 4-byte mStack index, so param 1 is recovered correctly
  (cursor: param 0 = 4-byte ref at +0, param 1 index at +4; no off-by-N).
- **Gotcha for future children:** a `.size N` struct with no fields computes to
  TPS=0/TRC=0 under ILRuntime -- any future code that expects such a struct's static
  VALUE to be materialized in the Neo static instance will find ManagedObjects null.
  Source the value from Cecil `InitialValue` instead. (Legacy stores it as an Object
  StackObject slot, which is why Legacy InitializeArray worked without this dance.)
- **Verify:** NeoStep smoke **320/0** (318 + TC1 int[] + TC2 double[]; both new probes
  embed "NeoStep" in their names so the NeoStep filter picks them up).
  `NeoStepLdtoken_TC5` still 5/0 (now via correct contents, the `catch(NIE)` is dead).
  Full Neo smoke (pre-crash segfault): grep "CLR value type with reference fields" =
  **0** (was ~18). Legacy-neutral: plain `Debug`+`useRegister=true`+`NeoStep` = 320 ran
  / 17 failed (17-failure baseline holds; both new probes pass under Legacy too). Files:
  `CLRRedirections.cs`, `AppDomain.cs`, `ILIntepreter.Neo.cs` (ldtoken arm), new
  `TestCases/NeoClrVtReffieldsBinderTest.cs`. Neo-gated => Legacy-neutral by construction.

## Child 7 PROPOSED (neo-misc-opcodes, 2026-07-11) -- durable findings
Batch of 4 independent ExecuteNeo arms (5+4+2+2 = 13 full-smoke hits). Each mirrors
a Legacy `ExecuteR` arm 1:1; all Neo-gated. Capability split: Conv_R_Un+Switch ->
`neo-optimizer`; Unbox-enum -> `neo-type-checks`; Ldsflda -> `neo-byref`.
- **Conv_R_Un is FULLY Neo-lowered already -- only the dispatch arm is missing.**
  JIT stamps `Operand2`=(int)`InferPrimTag(src)` (`JITCompiler.cs:975`, grouped
  with Conv_R4/R8); `GetConvResultType(Conv_R_Un)`=`DoubleType` (`:1466` -> dest =
  8 bytes -> arm MUST write `*(double*)`, NOT float); `LowerNeoOffsets` visits it
  (`Optimizer.Neo.cs:589`). Arm: read source UNSIGNED via existing `ReadConvU4`/
  `ReadConvU8` (return uint/ulong -- do NOT use the signed ReadConvI*), widen to
  double, write `*(double*)(frameBase+ip->DstOffset)`.
- **Switch: jump table already exists at runtime.** `method.JumpTablesRegister
  [ip->Operand]` is populated (`PrepareJumpTable` JIT:2174/2252; targets remapped
  by `LowerNeoOffsets` Opt.Neo:1728). Arm is byte-for-byte Legacy (Register:2754):
  bounds-check `[0,len)`, `ip=ptr+table[idx];continue;` else fall through. OPEN
  (diagnose-first): index value read via `*(int*)(frameBase+ip->DstOffset)` (offset
  form -- `DstOffset`==`Register1` aliased @OpCode.cs:46-47) vs `ip->Register1`.
- **Unbox-of-enum: root cause CONFIRMED, it's a missing `!isEnumObj` guard.** Neo
  Unbox CLR-primitive branch (Neo:4390-4394) calls `NeoWritePrimitiveToFrame(obj,
  ...)` w/o excluding `ILEnumTypeInstance` -> NIE @6339. Legacy guards it
  (Register:4046 `&& !isEnumObj`; :4129 `res is ILEnumTypeInstance ->
  CopyToRegister(0,...)`). Fix = branch `obj is ILEnumTypeInstance` before the prim
  write, extract underlying value. OPEN (diagnose-first): value storage -- Box arm
  (Neo:3546) + ILType-enum Unbox arm (Neo:4357) read/write `ins.Primitives`, but
  ILEnumTypeInstance ctor (ILTypeInstance.cs:101-104) allocates `fields` (byte[]);
  confirm which is authoritative (prefer Primitives).
- **Ldsflda reuses the existing Stind/Ldind heap-IL arms with ZERO consumer change.**
  Encoding = identical to Ldsfld (JIT:2506-2510): `Register1`=dest, `OperandLong`=
  `(typeHash<<32)|fieldHash`. Neo byref = 8-byte `(objIdx,off|flag)`; the Stind_*
  (Neo:4863-4906) + Ldind_Ref (:5143-5165) arms ALREADY do `mStack[objIdx] is
  ILTypeInstance` -> `ins.Primitives[off]`/`ins.ManagedObjects[off]`. So for an IL
  static, materialize `ilType.StaticInstance` into mStack + emit byref `(idx,
  fieldPrimOff/RefOff)` -> a following stind/ldind writes/reads the static field.
  CLR-static Ldsflda has NO heap object (FieldInfo GetValue/SetValue null) -> defer
  w/ tagged NIE unless a live hit (design D4). Do NOT stamp `Operand3` (aliases
  OperandLong hi, child-2). OPEN (diagnose-first): IL/CLR split of the 5 hits +
  whether the byref flows to `CopyNeoCallArguments` (a `ref`/`out`/Interlocked arg),
  which may need its own recognition.
- **`rasen validate` "must contain SHALL or MUST" is a REPO-WIDE FALSE POSITIVE**
  (it ignores the body's SHALL): child-6 DONE fails it identically. Portfolio runs
  `--no-gate`; isComplete=True + proper `#### Scenario` structure is the real bar.

## Child 8 PROPOSED (neo-clr-static-vt-field, 2026-07-11) -- durable findings
- **The binder is IRRELEVANT to the Neo flat-byte VT path.** `ReadNeoValueType`/
  `WriteNeoValueType` (`ILIntepreter.Neo.cs:227/243`) are PURE FLAT-BYTE copies
  (`Unsafe.ReadUnaligned<T>`/`WriteUnaligned<T>` via cached DynamicMethod
  delegates, `CreateNeoVtReader/Writer` `:177/200`, sized by
  `GetNeoValueTypeManagedSize`=`Unsafe.SizeOf<T>`). They do NOT consult
  `ValueTypeBinder` -- the binder (`ValueTypeBinder.cs`) only exposes Legacy
  `StackObject*`+`IList<object>` marshalling (`CopyValueTypeToStack`/`AssignFromStack`);
  there is NO Neo `byte*` binder API. So a blittable binder struct (TestVector3 =
  3 floats) has the same flat-byte representation with or without a binder.
- **The child-3 `NeoClrVtStaticFieldIsUnsafe` `if (hasBinder) return true;` clause
  (`:275-276`) is OVER-CONSERVATIVE and rests on a WRONG rationale** ("a binder
  struct would corrupt the flat-byte slot"). The 11 `TestVector3.One` hits trip ONLY
  that clause. Fix = DELETE it (~1 line). KEEP the other two clauses: (a)
  `NeoClrStructHasRefFields(ft)` -- ref-field CLR structs genuinely cannot be flat-
  marshaled (Neo VT slot refs are mStack indices, `FieldInfo.GetValue` returns real
  GC pointers -> mismatch); (b) slot-overflow (`GetNeoValueTypeManagedSize > slotSize`,
  the REAL AV protection). The box-roundtrip behind the guard (Stsfld
  `ReadNeoValueType`+`SetStaticFieldValue`; Ldsfld `GetFieldValue(sIdx,null)`+
  `WriteNeoValueType`) already handles TestVector3.One. Evidence the flat-byte path
  works for binder structs: TestVector3 is marshaled through it for by-value
  params/returns in the live NeoStep smoke (`SumTestVector3Fields`), and child-4's
  raw Ldfld/Stfld CLR-VT-OWNER handlers (`:3734/3895`) use the same box-roundtrip
  with NO binder guard and are green. Child-3's "VT-binder AV" was the slot-overflow
  case (separately caught) -- the binder clause is a false correlation.
- **F1-overlap verdict: KEEP `neo-clr-vt-refcount-stobjldobj` SEPARATE.** The
  Stobj/Ldobj arms (`:5311/5414`) compute `refCount=0` for CLR structs (ilType
  null) -> a ref-field CLR struct copied via stobj/ldobj has its ref-region copy
  silently skipped (latent missed-GC-root). SAME mechanism class (flat-byte path
  misses GC refs for ref-field CLR structs) but DIFFERENT opcode site, and NOT
  low-risk (needs binder-aware ref-region marshalling or a guard; touches the
  Move_Vt/Stobj Step-17 surface -> regression risk). Do NOT fold into child 8.
- **Capability = `neo-optimizer`** (consistent with child-3; the change modifies
  child-3's guard in child-3's Stsfld/Ldsfld arms). Delta = ADDED a requirement
  pinning "a registered ValueTypeBinder does NOT by itself make a CLR VT static
  unsafe." Net runtime diff ~1-3 lines. Probes: TC1 ldsfld TestVector3.One read,
  TC2 stsfld+ldsfld round-trip (add `TestClass3.NeoClrVtStaticProbe` + a host
  `HostReadNeoClrVtStaticProbe()` -- the child-3 TC1 write/read-isolation pattern).
  Expected 324->326 NeoStep, 11->0 full-smoke NIE.


## Child 9 DONE (neo-il-instance-clr-base-field, 2026-07-11) -- durable findings
- **A CLR-base field on an IL instance lives on `ILTypeInstance.CLRInstance`, NOT in
  `Primitives`/`ManagedObjects`.** `CLRInstance` is the wrapped Adaptor object created
  by `CrossBindingAdaptor.CreateCLRInstance` at construction (`ILTypeInstance.cs:366-368`),
  IS-A the CLR base, so the base's instance fields are real CLR fields on it. This is the
  IL-instance-CLR-base owner shape that child-4's raw Stfld/Ldfld handlers DEFERRED with a
  tagged NIE (~5 full-smoke hits, e.g. `TestCls : ClassInheritanceTest` on `TestVal2`).
- **Fix is interpreter-only: reuse `NeoReadClrObjectField`/`NeoWriteClrObjectField` on
  `il.CLRInstance`.** Unwrap the owner `target as ILTypeInstance ?? ((CrossBindingAdaptorType)
  target).ILInstance`, then pass `il.CLRInstance` (NOT the ILTypeInstance) to the existing
  helpers -- they re-resolve `ct=appdomain.GetType(target.GetType()) as CLRType` (the
  Adaptor's CLRType) and `GetFieldValue/SetFieldValue(fieldHash,...)` walks the base chain
  to the field. No new helper, no JIT/optimizer/object-model change. No writeback (CLR base
  is a class; SetFieldValue's `ref` is a no-op here). Neo-gated.
- **GOTCHA: the owner slot may hold EITHER the `ILTypeInstance` OR its
  `CrossBindingAdaptorType` wrapper** -- the `?? ((CrossBindingAdaptorType)target).ILInstance`
  unwrap covers both. `clrInstance==this` (no adaptor) is IMPOSSIBLE here because the field's
  declaring CLRType must be a base. Stash-toggle confirmed TC1 trips the Stfld NIE, TC2 trips
  the Ldfld NIE on HEAD. NeoStep smoke **328/0** (326 + 2 probes). Full smoke tagged-NIE
  5->0 (crash is the known pre-existing Test05 Dict-NRE, exit 139). Legacy-neutral: plain
  Debug+useRegister=true+NeoStep = 328 ran/17 failed == baseline 17-failure set; both probes
  pass under Legacy. Out of scope: `target is Array` raw Stfld/Ldfld (~2 hits, still deferred).

## Child 11 PROPOSED (neo-brtrue-on-reference, 2026-07-11) -- durable findings
- **Neo Brtrue/Brfalse (Neo.cs:2063/2071) test `*(int*)(frameBase+DstOffset)!=0` --
  WRONG for a reference operand.** Under the Neo object model a reference is an
  mStack index in the slot's primitive bytes, and NULL is a NON-ZERO index
  (IL-static Ldsfld `mStack.Add(null)`+index, Neo.cs:4302-4304) or `-1` (CLR-static
  Ldsfld :4350; Ldnull :1526). So null reads TRUTHY -> the C# `if(x==null){init}`
  / delegate-cache pattern (Roslyn lowers `x==null` on a ref to a DIRECT
  `ldsfld x; brtrue` with NO ceq) skips the init -> x stays null -> downstream
  NRE. Actively failing: TestStaticFieldInstance, RegisterVMTest04, delegate cache.
- **Legacy distinguishes via `reg1->ObjectType`** (Register.cs:2039-2089:
  `Object`->`mStack[v]!=null`, `Integer`->`v!=0`, `Null`->falsey). **Neo's frame
  is UNTYPED (no per-slot ObjectType tag), so the ref-vs-int distinction MUST be
  made at JIT time.** Neither `frame.LocalIsReference[]` (marks only declared
  ref locals/params; TEMPS are false -- JITCompiler.cs:2050-2062) nor slot Size
  can distinguish a reference TEMP. The ONLY signal is the `registerTypes[]`
  dataflow in `TypeSpecializeNeoOpcodes` (the `Move` op already uses it via
  `IsNeoReferenceSlot(srcType)`, JITCompiler.cs:852/1450).
- **THE FIX = type-specialize the branch (mirror Ldfld_Ref):** append
  `Brtrue_Ref`/`Brfalse_Ref` to OpCodeREnum; in TypeSpecializeNeoOpcodes, seed
  the `Ldsfeld` dest type (CRITICAL GAP -- Ldsfeld does NOT seed registerTypes
  today; the failing patterns are all `ldsfld ref; brtrue`) and rewrite
  Brtrue/Brfalse -> _Ref when `IsNeoReferenceSlot(registerTypes[r1])`. ceq-normalized
  conditions stay int (ceq seeds IntType -> stays plain Brtrue). Runtime arm:
  `idx=*(int*)(DstOffset); truthy = idx>=0 && mStack[idx]!=null` (mirrors Legacy
  mStack[v]!=null; covers all 3 null encodings). Brtrue/Brfalse ARE lowered by
  LowerNeoOffsets (Operand2=size, DstOffset=off1) so DstOffset is a real byte off.
- **F3 SEQUENCING VERDICT: F3 + F4 are COUPLED, MUST ship together.** F3 = IL-static
  Stsfld/Ldsfeld read raw `ip->DstOffset` (a register INDEX) as a byte offset
  (NOT in LowerNeoOffsets; CLR-static arms resolve via localInfos at :4192/:4331,
  IL-static arms at :4135/:4277 do NOT -- child-3 left it, comment :4187-4191).
  They meet on the delegate-cache `ldsfld(IL-static ref);brtrue` pattern
  (NeoStep20_Tr2/Tr5): F4-alone feeds the new deref arm garbage -> mStack[garbage]
  OOB; F3-alone unmasks F4 (child-3: Tr2/Tr5 break). ONLY F3+F4 together is stable
  (F4 first, then F3, then re-verify). F3 fix = resolve IL-static reg byte offset
  via localInfos like the CLR-static arms.
- **GOTCHA (registerTypes dataflow imprecision):** registerTypes is a single
  linear pass (no phi-merge at joins), so a false-positive `_Ref` rewrite on a
  reused register could deref a non-index int. Mitigation = the Brtrue operand is
  always the straight-line top-of-stack produced immediately before the branch
  (same block), so reliable for this shape; full NeoStep smoke + dedicated Tr2/Tr5
  re-verify is the safety net. Capability = `neo-optimizer` (owns
  TypeSpecializeNeoOpcodes + LowerNeoOffsets + Stsfld/Ldsfeld arms; child-3
  precedent). Probe MUST FAULT (NRE from skipped init on HEAD).

## Child 13 PROPOSED (neo-il-static-ref-field-readback, 2026-07-12) -- durable findings
- **THE FRAMED HYPOTHESIS WAS WRONG.** "IL-static ref field read-back is broken"
  was the symptom surface; the IL-static Stsfld/Ldsfeld REF arms are CORRECT
  (stsfld writes `sinst.ManagedObjects[off.ReferenceOffset]`; ldsfeld reads it
  back + pushes the object). Instrumented diagnostics proved the `TestA`
  instance round-trips faithfully. DO NOT touch the static arms for this gap.
- **REAL ROOT CAUSE = newobj dest/arg REGISTER ALIASING (reference arg).** The
  IL ref-type newobj arm (`ILIntepreter.Neo.cs` "Step 8b" ~3309) did
  `mStack[newobjDstIdx] = ins` BEFORE `CopyNeoCallArguments`. The canonical
  lazy-init lowering `ldstr/ldloc refArg; newobj(refArg)` REUSES the arg's
  register as the newobj dest -> dest ref slot (`frameRefBase+dstRefOffset`) ==
  arg ref slot -> `argIdx == newobjDstIdx`. So storing the instance CLOBBERS the
  reference arg before it is copied; the ctor receives `this` as the aliased
  ref arg. `TestStaticFieldInstance`'s `new TestA("testerror")` ctor saw `this`
  as `name` -> `this.name` read an ILTypeInstance -> `String.Concat` InvalidCast.
- **This COMPLETES the `neo-newobj` Q-NEWOBJ dest/arg aliasing contract**, which
  previously claimed "no special fix required" -- that was only verified for a
  PRIMITIVE `intArg` (its slot value isn't an mStack index, can't be clobbered).
  A REFERENCE arg's slot value IS an mStack index -> the contract needed the fix.
- **THE FIX (sound discriminator, ~15 lines, runtime-only, Neo-gated):** in the
  newobj arm, BEFORE `mStack[newobjDstIdx]=ins`, if `dstRefOffset` appears in the
  call's `map.RefSrc` (dest register aliases a reference arg) AND
  `*(int*)(frameBase+ip->DstOffset) == newobjDstIdx`, re-base:
  `mStack.Add(mStack[aIdx]); *(int*)(frameBase+ip->DstOffset)=mStack.Count-1`.
  The ref-map membership EXCLUDES a primitive int arg whose value coincidentally
  == newobjDstIdx (RefCount-0 args are never in RefSrc). DEFERRED-write of the
  dest primitive alone does NOT fix it (verified) -- the ctor reads
  `mStack[argIdx]` and argIdx==newobjDstIdx, so the instance must NOT occupy that
  slot while the ctor runs; the arg must move, not just the write defer.
- **Verify:** NeoStep smoke **339/0** (335 + 2 probes x2 filter matches);
  `TestStaticFieldInstance` PASS (was InvalidCast); stash-toggle proves both TCs
  FAULT (DivideByZero); Legacy 4/0. Files: `ILIntepreter.Neo.cs` (newobj arm),
  new `TestCases/NeoStepNewobjArgAliasTest.cs`. Capability = `neo-newobj`.
- **GOTCHA for future children:** when a "read-back returns ILTypeInstance /
  wrong value" symptom appears, TRACE the PRODUCER (newobj/ctor), not just the
  reader -- the static field was a faithful messenger for a ctor-arg-clobber
  bug. And: eval-stack register reuse makes newobj dest alias the LAST arg
  register; any new arm that writes the dest before CopyNeoCallArguments must
  check the ref-map alias first.

## Child 14 DONE (neo-overhaul-eh-table-remap, 2026-07-12) -- durable findings
- **THE GOTCHA (the crux):** `method.exceptionHandlerR` (the Neo EH table,
  TryStart/TryEnd/HandlerStart/HandlerEnd, all body-indexed; NO FilterStart --
  IL filters unsupported) is **NULL during LowerNeoOffsets**. InitCodeBody builds
  it at the `:959` site AFTER Compile (`:916`), but Compile's tail IS
  RunNeoBackHalf -> LowerNeoOffsets (the Push-deletion pass). So the deletion pass
  could not keep the EH table consistent with the post-deletion body -> stale
  boundaries -> a thrown exception whose runtime addr falls outside the stale
  [TryStart, TryEnd] MISSES its handler -> unhandled. LATENT (no NeoStep probe
  combined try/catch + a >3-arg THROWING call); sibling of child-1's Leave remap
  in the SAME function (FixBranchTargetsAfterRemove).
- **THE FIX (3 parts, all Neo-gated):**
  (1) Extract the `:959` EH build into `ILMethod.BuildExceptionHandlerRegister(addr)`
      and call it BEFORE the back-half at BOTH RunNeoBackHalf funnels -- direct JIT
      (`JITCompiler.Compile`, has `addr` + `this.method`) and generic
      (`GenericMethodTemplate.DoCloneAndPatch`, has the delta-shifted `addr` + `instance`).
      The `:959` site becomes idempotent (`if (!neoEhAlreadyBuilt)` skip the fill;
      the Neo `register` arm calls the helper, Legacy/non-Neo arms build inline as
      before -> byte-identical).
  (2) `FixBranchTargetsAfterRemove` gains a trailing `ExceptionHandler[] ehs` param
      and decrements each of the 4 fields when `> removedIndex` (identical to the
      branch-target rule; a field == removedIndex is never a boundary -- the deleted
      instruction is always a synthetic Push). Null `ehs` = no-op.
  (3) `LowerNeoOffsets` gains the `ehs` param; `RunNeoBackHalf` passes
      `method.ExceptionHandlerRegister`. ONE runtime caller of LowerNeoOffsets.
- **PER-DELETION LOCKSTEP is load-bearing (design D1):** the remap MUST run inside
  the existing per-deletion loop (FixBranchTargetsAfterRemove is already called
  once per deleted Push with `removedIndex = scanIdx`, a CURRENT-body-order index).
  The EH table starts in CodeBody-order and is re-mapped at each deletion, so it
  stays in the same current-body frame as `scanIdx` and the branch targets, ending
  in final NeoExecuteBody-order. A single post-pass would need to convert recorded
  current-body-order scanIdx values back to CodeBody-order -- a fiddly online
  order-statistics problem. Re-using the per-deletion hook avoids it entirely.
- **THE INLINER GOTCHA (nearly defeated the probe):** the Neo JIT inlines small IL
  methods (JITCompiler.InitializeFunctionParam: canInline when `!hasExceptionHandler`
  && body <= MaximalInlineInstructionCount/2 == 10 register instrs && !virtual &&
  !NoJIT). An INLINED call emits NO Push -> NO deletion -> NO staleness -> the probe
  passes on HEAD (worthless). A probe MUST force REAL calls: either a body > 10
  register instrs, OR a method carrying a try/catch/finally (hasEH -> never inlined).
- **THE RELIABLE TRIGGER SHAPE:** a >3-arg call placed BEFORE the try (deletes its
  overflow Pushes at body indices BEFORE try-start, so `d(TryStart) > 0`) + a
  NON-INLINED NO-ARG throwing helper called as the FIRST try statement (its Call
  opcode sits exactly at TryStart, so the runtime throw addr == TryStart - d, which
  is STRICTLY below the stale TryStart -> catch MISSED -> unhandled -> FAULT). An
  in-try throwing >3-arg call is NOT reliable: its own Pushes + arg-loads add in-try
  distance that can leave the throw addr inside the stale window by luck. (The
  design's minimal example -- throwing 5-arg call first in try -- does NOT fault.)
- **Verify:** stash-toggle FAIL-on-HEAD (2/2 probes -- throw escapes catch) ->
  PASS-after. NeoStep smoke **341/0** (339 + TC1 6-arg/3-deletion + TC2 4-arg/
  1-deletion). **NeoStep14 (EH step) 26/26 UNREGRESSED** -- the build-order change
  is load-bearing for every Neo method with protected regions, confirmed green.
  Legacy-neutral: plain Debug + useRegister=true + NeoStep = 341 ran / 17 failed
  == documented 17-failure baseline (the fix is entirely `#if ENABLE_NEO_MODE`;
  Legacy compiles none of it). Files: `Optimizer.Neo.cs`, `JITCompiler.cs`,
  `GenericMethodTemplate.cs`, `ILMethod.cs`, new `TestCases/NeoStepEhTableRemapTest.cs`.

## Child 15 DONE (neo-ldind-stind-byref-clr-struct, 2026-07-12) -- durable findings
- **Root cause: `ldflda` on a CLR-struct-LOCAL field used `FieldInfo.GetHashCode()`
  as a frame byte offset.** For a CLR (non-IL) declaring type,
  `AppDomain.GetFieldOffset` (`AppDomain.cs:2257`) returns `PrimitiveOffset =
  type.GetFieldIndex(token)` = `CLRType.GetFieldIndex` = `fieldMapping[name]` =
  `FieldInfo.GetHashCode()` (a large arbitrary 32-bit HASH). This is stamped into
  `ip->Operand2` of `ldflda`. The runtime `ldflda` frame-native branch
  (`objectIndex == -1`, reached via `ldloca <CLR-struct local>; ldflda <field>`)
  then produced the byref `(-1, vtBase + <huge hash>)`, so the following
  `ldind_*`/`stind_*` deref `*(T*)(frameBase + <huge hash>)` = out-of-frame
  AccessViolation (segfault, exit 139). Correct for IL-struct locals (there
  `PrimitiveOffset` IS the real `Primitives` byte offset); the gap was CLR-struct
  locals ONLY. LATENT until child 8 made `TestVector3.One` `ldsfld` reachable
  (`var a = TestVector3.One; a.X += ...`).
- **The fix (producer-side, NOT consumer-side):** add a 4th `ldflda` JIT marker
  bit `NeoLdfldaClrStructLocalFieldMarker = 0x8` (the existing markers are
  0x1=in-frame-IL-VT / 0x2=CLR-struct-field-of-IL / 0x4=heap-IL-ref-field; 0x8 is
  free in standalone `Operand4`). Stamped in the `case Code.Ldflda` body when the
  resolved declaring `type is CLRType` (mutually exclusive with 0x1/0x2/0x4 -- all
  require `type is ILType`; the F-6 type-spec gate fires only for an IL-VT source
  and clears only 0x2/0x4, so it never touches 0x8). In the runtime `ldflda`
  `objectIndex == -1` branch, when the marker is set, resolve the field's REAL
  managed byte offset via a cached `Marshal.OffsetOf(ct.TypeForCLR, fi.Name)` and
  use it in place of the hash. `ldind_*`/`stind_*`/`stobj`/`ldobj`/`initobj` are
  UNCHANGED -- once the byref carries a valid frame byte offset, every consumer's
  existing `objectIndex == -1` arm works. Offset cached in a process-static
  `ConcurrentDictionary<long,int>` keyed `((long)typeHash << 32) | (uint)fieldHash`
  (both already on the instruction: `ip->Operand`=type hash, `ip->Operand2`=field
  hash); first access pays one `Marshal.OffsetOf` + one `CLRType.GetField`,
  afterwards an O(1) hit.
- **SOUNDNESS = blittable-only.** Only blittable CLR structs reach the Neo
  flat-byte local path: a CLR VT with reference fields throws the Step-13b tagged
  NIE inside `ReadNeoValueType`/`WriteNeoValueType` BEFORE materializing as a frame
  local, and C# value types default to `LayoutKind.Sequential`, so for the
  reachable set `Marshal.OffsetOf` == the managed layout `Unsafe.WriteUnaligned`
  writes. An auto-layout struct (unreachable) makes `Marshal.OffsetOf` throw ->
  wrapped in try/catch -> tagged NIE naming the struct (fail-loud, no silent
  corruption).
- **GOTCHA (probe design -- two UNRELATED pre-existing Neo float bugs almost
  masked this fix):** (1) `addi` integer-add-of-float-bits constant fold: Roslyn's
  `a.X += 100` on a float field JIT-lowers to `addi r,r,0x42C80000` (raw bits of
  100.0f), doing an INTEGER add of the float bit-pattern -- so the value is
  garbage even after the AV is fixed. (2) `conv.i4` on a float does a BIT
  REINTERPRET (`(int)v.X` returns the float's raw bits, e.g. 1109917696 for 42.0f),
  NOT a numeric truncation. BOTH are pre-existing and out of scope. The probe
  MUST assert via HOST-side float arithmetic (`TestCLRBinding.SumTestVector3Fields`
  = `(int)(a.X+a.Y+a.Z+b.X+b.Y+b.Z)` in CLR) to sidestep both. Also: a `dst = src`
  (ldind->temp->stind) probe hits an unrelated register-temp/ref-local aliasing
  quirk (reads garbage); use the proven `ref float x = ref v.FIELD; x = VALUE;`
  (ldflda + stind) write shape at TWO different field offsets (X=0, Y=4) to prove
  the offset resolution is the REAL managed byte offset (a hash is never exactly
  4, and a hard-wired 0 would clobber X). The `addi` bug is a strong candidate for
  a future sibling child (it breaks `float += const` pervasively).
- **Verify:** stash-toggle FAIL-on-HEAD (TC1 AV/segfault exit 139) -> PASS-after
  (2/0). NeoStep smoke **343/0** (341 + TC1 X-offset-0 write + TC2 Y-offset-4
  write; both assert via SumTestVector3Fields). Full smoke: `TestValueTypeBinding
  .Test00` PASSES (was segfault); the run now reaches the known pre-existing Dict-
  NRE crash (exit 127, NOT 139) -- not a regression. The remaining
  `UnitTest_10036/10037` "protected memory" Messages are the tests' OWN deliberate
  `throw new AccessViolationException()` (a VT-semantics assertion), not runtime
  segfaults. Legacy-neutral: plain Debug + useRegister=true + NeoStep = 343 ran /
  17 failed == documented 17-failure baseline (both probes pass under Legacy).
  Files: `JITCompiler.cs` (marker const + body stamp), `ILIntepreter.Neo.cs`
  (cache + ResolveClrStructFieldByteOffset helper + ldflda arm branch), new
  `TestCases/NeoStepLdindStindByrefClrStructTest.cs`. Neo-gated => Legacy-neutral
  by construction.

## Child 16 PROPOSED (neo-addi-on-float, 2026-07-12) -- durable findings
- **THE CRUX FRAMING WAS REFINED: the bug is NOT in the constant-fold.** The
  ELDC fold (`Optimizer.Utils.GetIntemediateValueOpcode` -> plain `Addi` +
  `ReplaceRegisterWithConstant` writing `op.OperandFloat` for an `Ldc_R4` const)
  is type-agnostic AND SHARED with Legacy -- and it is CORRECT for Legacy,
  whose `Addi` runtime arm (`ILIntepreter.Register.cs:377-402`) re-dispatches on
  `reg1->ObjectType` (Float -> reads `ip->OperandFloat`). Neo's frame is
  UNTYPED (no per-slot ObjectType), so Neo's `Addi` arm (`Neo.cs:2468`) is
  hardcoded integer; Neo depends on JIT-time specialization
  (`Addi->Addi_R4` via `TypeSpecializeNeoOpcodes:1017` reading
  `registerTypes[op.Register2]`). **THE REAL GAP:** `TypeSpecializeNeoOpcodes`
  seeds Float/Double/Long ONLY for `Ldc_*` (`:865-872`) and `Ldfld_*`
  (`:1066-1073`); `Ldind_R4`/`Ldind_R8`/`Ldind_I8`/`Ldelem_R4`/`Ldelem_R8`/
  `Ldelem_I8` are NEVER seeded (they appear only at JIT emission `:2699-2729`/
  `:2907-2921`) -> the float operand (loaded via `ldflda;ldind.r4` for a CLR-
  struct field, or `ldelem.r4` for an array element) is mis-typed I4 -> the
  typed specialization no-ops -> plain integer `Addi` with float bits -> garbage.
- **SCOPE over subi/muli: YES, and more.** Because the fix seeds the OPERAND
  TYPE (not per-opcode), it covers `subi`/`muli`/`divi`/`remi` (immediate) AND
  plain `add`/`sub`/`mul`/`div`/`rem` (register-register) -- all key on
  `registerTypes[Register2]`. (Neo's plain `Add`/`Sub`/`Mul` arms `:1882-1890`
  are ALSO hardcoded integer, so the same seeding gap would corrupt a pure
  reg-reg float add on this path.) IL `Ldfld_R4` float fields and float LOCALS
  are NOT affected (already seeded) -- the bug is specific to the Ldind/Ldelem
  producer path.
- **THE FIX (cleanest, Neo-only, ~10-15 lines):** add `Ldind_*`/`Ldelem_*`
  cases to the `TypeSpecializeNeoOpcodes` seeding switch (`JITCompiler.cs`
  after `:1074`) -> `FloatType`/`DoubleType`/`LongType`/`IntType`. No new
  opcode, no runtime change, no fold change. Seeding a primitive float/double/
  long is SAFE for the other type-spec decisions (Brtrue_Ref/Ceq_Ref/Move/
  field-inline all key on `IsNeoReferenceSlot`/`IsValueType`; a primitive is
  neither). REJECTED ALT (type-aware fold emitting `Addi_R4`): the typed
  immediate variants are NOT in the shared `GetOpcodeSourceRegister`
  (`Optimizer.Utils.cs:681-700`, throws NIE `:722`), so a prior fold's
  `Addi_R4` would crash a later ELDC iteration; Legacy runtime has no
  `Addi_R4` arm either -> bigger surface, only fixes the immediate form.
- **Capability = `neo-optimizer`** (ADDED requirement; the seeding switch lives
  in `TypeSpecializeNeoOpcodes`, same capability as child-11 Brtrue_Ref).
  **Probe MUST FAULT** (DivideByZero on wrong value): use `TestVector3.X` (CLR-
  struct float, the unseeded `Ldind_R4` path via `ldflda;ldind.r4`) + assert via
  host helper `SumTestVector3Fields` (CLR-side float arith -- sidesteps BOTH
  this bug AND the separate out-of-scope `conv.i4`-float-bit-reinterpret bug).
  TC1 `a.X+=100`->206, TC2 `a.X-=100`->-194, TC3 `a.X=a.X*2+1`->10. Artifacts
  at `rasen/changes/neo-addi-on-float/` (proposal/design/specs/tasks; valid).

## Child 18 PROPOSED (neo-il-enum-getenumvalues, 2026-07-12) -- durable findings
- **THE CRUX: the bare NIE comes from the framework, NOT the autogen binding.**
  `System_Enum_Binding.GetValues_0_Neo` (`System_Enum_Binding.cs:72`) does NOT throw
  -- it reads `@enumType` and calls the framework `System.Enum.GetValues(@enumType)`
  (`:77`; `GetNames_1_Neo` mirrors at `:111`; `Enum.GetUnderlyingType` is not even
  redirected). The framework delegates to `Type.GetEnumValues()`/`GetEnumNames()`/
  `GetEnumUnderlyingType()` virtuals on the passed `System.Type`. ILRuntime's IL-enum
  wrapper `ILRuntimeType` (`ILRuntime/Reflection/ILRuntimeType.cs`, made by
  `ILType.ReflectionType` `ILType.cs:1985-1993`) overrides NONE of them (confirmed by
  grep -- nor `IsEnum`) -> they fall through to the base `System.Type` impl, which
  throws a bare `NotImplementedException()` (first frame `System.Type.GetEnumValues()`,
  no file:line). Only the framework's own `RuntimeType` overrides those virtuals. This
  is exactly what `neo-bare-nie` child site 7 / D3 DEFERRED as "an IL-enum-Type-
  representation investigation."
- **The enum-value metadata source is Cecil `FieldDefinition.Constant` (returns the
  boxed underlying value directly).** `Mono.Cecil/.../FieldDefinition.cs:139`
  `Constant { get { return HasConstant ? constant : null; } }`. Already used by
  `ILRuntimeFieldInfo.GetRawConstantValue()` (`:168`) and `GetValue()` static+
  HasConstant (`:180-181`). Members = `type.TypeDefinition.Fields` filtered to
  `IsLiteral && HasConstant` (the named constants; the instance `value__` field is
  excluded -- non-literal). Underlying type = `ILType.enumType` (set in
  `InitializeFields` `ILType.cs:2977-2979`), surfaced via the `ILType.TypeForCLR`
  enum branch (`:1966-1970` `return enumType.TypeForCLR`).
- **THE FIX (4 virtual overrides on `ILRuntimeType`, ~50-70 lines, shared reflection
  code -- NO Neo gate, NO JIT/optimizer/object-model/binding change):**
  `IsEnum => type.IsEnum`; `GetEnumUnderlyingType() => type.TypeForCLR` (guarded by
  `if(!type.IsEnum) throw ArgumentException`); `GetEnumValues()` = build
  `Array.CreateInstance(underlying,n)` + `SetValue` each `FieldDefinition.Constant`
  (declaration order); `GetEnumNames()` = collect the literal-field names.
- **DEPTH VERDICT = TRACTABLE** (focused wrapper-method impl; `neo-bare-nie` D3's
  "investigation" framing was disproven on inspection -- 4 well-specified `System.Type`
  virtual overrides with an obvious, precedent-backed impl). DO NOT defer.
- **Capability = `neo-type-checks`** (ADDED requirement; it owns the type-identity/
  reflection-query surface on IL types -- `CanAssignTo`/`IsAssignableFrom` -- of which
  enum reflection is a sibling; NOT `neo-value-types`, which is in-frame VT storage).
  The fix is shared code -> Legacy-neutral by construction (Legacy redirect
  `GetValues_0` `:88` makes the SAME framework call, so Legacy benefits too).
- **GOTCHA for the apply worker:** override `IsEnum` defensively even though the NIE
  evidence implies the base already returns true on net8.0 -- if a stash-toggle shows
  `ArgumentException` instead of the NIE, the `IsEnum` override is the lever.
  Probe MUST FAULT (bare NIE on HEAD) + assert values/names/underlying (throw on
  mismatch). Expected NeoStep 348 -> ~350-351/0. Artifacts at
  `rasen/changes/neo-il-enum-getenumvalues/` (proposal/design/specs(neo-type-checks
  ADDED)/tasks; isComplete=True).

## Child 21 DONE (neo-raw-ldfld-stfld-clr-struct-seeding, shipped 2026-07-13) -- durable findings
- **RE-AUDIT CONFIRMED (not disproven this time -- 17-for-17 streak broken honestly):** both
  unseeded-producer shapes reproduce on HEAD fc2baa26. The float-corruption class (untyped Neo
  frame + InferPrimTag I4-fallback + registerTypes-driven typed-arithmetic specialization) had
  TWO remaining producers child-16 explicitly deferred: (a) a **Call returning primitive float/
  double/long**, (b) **raw `Ldfld` of a CLR-struct primitive field** (the child-4 CLRType-owner
  escaping shape). Both FAULT (DivideByZero) on HEAD; both PASS after seeding. Raw `Stfld` was
  DISPROVEN-in-scope (it CONSUMES a value; it does not PRODUCE one feeding arithmetic -> no dest
  seeding needed for this defect class).
- **The `Call` case in `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:1217-1329`) only CLEARS stale
  in-frame-VT / reference dest types; it NEVER seeds a fresh dest with the resolved `ReturnType`
  (resolved at `:1243`/`:1273`, then unused for seeding).** Fix = a trailing primitive-ReturnType
  seed AFTER the existing stale-clear if/else-if chain (additive; a primitive return never
  conflicts with the VT/ref-keep logic). ALL 5 call variants (`Call`/`Callvirt`/`Callvirt_IL`/
  `Callvirt_CLR`/`Call_Redirect`) fall through to the SAME case-label block, so the single seed
  covers CLR callees too (best-effort; a null-ReturnType CLR-redirect token -> dest stays
  unseeded = byte-identical to HEAD, no regression). O1 VERDICT: no separate case needed.
- **There is NO seeding case for the raw `OpCodeREnum.Ldfld` (only the typed `Ldfld_R4`/`R8`/`I8`
  splitter arms are seeded).** The raw case is reached ONLY for a CLRType declaring owner (ILType
  owners are already rewritten to typed arms by the splitter -- same as child-4's runtime handler).
  Fix = a `case OpCodeREnum.Ldfld:` that decodes `(typeHash<<32)|fieldHash` from `OperandLong`
  (IDENTICAL to child-4's runtime handler + the ExecuteNeo raw-Ldfld arm at `ILIntepreter.Neo.cs:
  3878-3886`), resolves `CLRType.GetField(fieldHash)`, and seeds the primitive field type. The
  field's primitive `System.Type` is recoverable at JIT time exactly as at runtime.
- **THE FIX PATTERN GENERALIZES (child-16 + child-21): every primitive float/double/long PRODUCER
  must seed `registerTypes[dest]`** so the typed-arithmetic specialization (`Addi->Addi_R4`,
  `Mul->Mul_R4`, ...) fires instead of falling back to a plain INTEGER op on the raw IEEE bits.
  Closed producers: `Ldc_*`, typed `Ldfld_*`, `Ldind_*`, `Ldelem_*` (child-16), `Call`-primitive-
  return + raw-`Ldfld`-CLR-struct (child-21). The seeding is a JIT-time switch addition WHENEVER
  the type is knowable at JIT time (it is, for both new cases); the child-15 `0x8`-in-`Operand4`
  JIT marker is the right tool ONLY when the type is UNknowable at JIT time (the ldflda offset
  bug) -- do NOT reach for a marker when a seeding case suffices.
- **A shared helper `NeoClrPrimitiveTypeToIType` (`JITCompiler.cs:1581-1599`)** maps a CLR
  `System.Type` -> IType: float->FloatType, double->DoubleType, long/ulong->LongType,
  other-primitive->IntType (PRESERVES today's null->I4 fallback so uint stays I4, not U4 -- the
  fix is scoped strictly to float/double/long and perturbs no other type's specialization),
  non-primitive->null (the non-seed sentinel). Reusable by any future CLR-primitive-type seeding.
- **registerTypes single-pass-no-phi-merge risk (child-11/16 gotcha, reaffirmed):** a reused
  register could in theory be mis-seeded, but the Call/Ldfld dest is a straight-line fresh temp
  (top-of-stack produced immediately before the consumer, same block) -> reliable for these
  shapes. Pre-existing (P1/P2): an IL-VT-return-to-fresh-temp gap + the single-pass imprecision
  itself are out of scope, noted not introduced.
- **Verify:** NeoStep **358/0** (354 + TC1 float + TC2 double via Call; TC3 mul+add + TC4 reg-reg
  via raw Ldfld); stash-toggle of JITCompiler.cs ONLY -> 4/4 FAULT (DivideByZero at the deliberate
  1/0 guards) -> pop -> 0/4 PASS (airtight). JIT dump confirms typed specialization now fires
  (`addi.r4`, `addi.r8`, `muli.r4`, `add.r4`). Legacy-neutral: 100% `#if ENABLE_NEO_MODE`;
  identical Legacy NeoStep result with/without the change. Files: `JITCompiler.cs` (3 additive
  sites, 71 added / 0 removed), new `TestCases/NeoStepFloatSeedingProbe.cs`. Capability =
  `neo-optimizer`. Review: APPROVE-WITH-FINDINGS (0 Blocker/Major; M1 perf nit getMethod-not-hoisted,
  M2 CLR-callee-float probe coverage accepted-known worst-case-no-regression; 3 Trivial).

## Child 22 DONE (neo-activator-createinstance-neo-redirect, shipped 2026-07-13) -- durable findings
- **Triage batch-1 WINNOWED the latent list (12-for-12 lesson holds):** `neo-il-static-field-roundtrip`
  DISPROVEN (child-13 closed it -- `SimpleTest.TestStaticFieldInstance` + fresh primitive/reference
  round-trip probes all PASS); `neo-clr-vt-refcount-stobjldobj` UNREACHABLE (a ref-field CLR struct
  NIEs at the ctor `CLRMethod.Invoke:393` Step-13 Area-4b BEFORE materializing; the only non-NIE path
  [method return] copies the GC pointer inside the CopyBlock'd primitive bytes with them -- no root
  lost). NEITHER needs a child.
- **`neo-activator-createinstance-nre` was DISPROVEN-as-framed (the `ILType.GetStaticFieldOffset` NRE
  does NOT reproduce) but REFRAMED to a real+tractable gap:** under Neo, `Activator.CreateInstance`
  on an IL type fell through to the broken AUTOGEN stub `System_Activator_Binding.CreateInstance_*_Neo`
  (a `default(...)`/TODO) -> `MissingMethodException: No parameterless constructor for ILTypeInstance`.
  Root cause: `AppDomain.cs:162-176` registers the hand-written `CLRRedirections.CreateInstance/2/3`
  (which correctly do `ILType.Instantiate()` for IL types) on Legacy's `RedirectMap` ONLY -- NOT on
  `RedirectMapNeo`. Neo dispatch uses `RedirectMapNeo` exclusively (child-2). SAME defect class as
  child-6 (`RuntimeHelpers.InitializeArray` -> `InitializeArrayNeo`). Neo-specific (Legacy 2/2 PASS).
- **THE FIX (mirror child-6, Neo-only, ~123 lines engine):** Neo-signature equivalents
  `CreateInstanceNeo`/`CreateInstance2Neo`/`CreateInstance3Neo` in `CLRRedirections.cs` (signature
  mirrored from `InitializeArrayNeo`/`DelegateCombineNeo`: `void(ILIntepreter, byte* frameBase,
  AutoList, CLRMethod, bool, byte* retDst, int retRefBase)`; params via `ReadNeoReference`; result
  via a new `WriteNeoObjectResult` helper that emits the `-1` null sentinel on null -- DISTINCT from
  `WriteNeoDelegateResult` which writes a valid index even for null [correct for Combine, wrong for
  general object returns]). Three overloads reproduce the hand-written redirects: generic reads
  `method.GenericArguments[0]`; Type overloads `ReadNeoReference` the Type (+ object[]); IL ->
  `ILType.Instantiate()`/`Instantiate(args)`, CLR -> `CreateDefaultInstance()`/host Activator.
  Registered on `RedirectMapNeo` in the AppDomain ctor's Activator `foreach` (mirror Delegate.Combine).
- **THE GENERIC-DEFINITION-PRECEDENCE LEVER (child-6 lineage, durable):** `CLRMethod.TryGetRedirection`
  (`CLRMethod.cs:111-131`) tries `GetGenericMethodDefinition()` FIRST. So registering ONE Neo redirect
  for the generic `Activator.CreateInstance<T>()` definition preempts EVERY autogen per-instantiation
  stub -- no need to patch each stub. For non-generic overloads, first-registered-wins (AppDomain ctor
  before the test-harness autogen `Register`) does the same. This is the same lever the Step-20 async
  builder redirects already rely on. USE THIS PATTERN for any future "autogen Neo stub is broken" gap.
- **SURFACED FOLLOW-UP (P1, the real next candidate): an INSTANCE-field/local `== null` via ceq still
  reads FALSE on HEAD.** `ActivatorCreateInstanceWithArgsTest` still fails -- but NOT on Activator (the
  instance is created with correct field values; proven via a diagnostic `new DiagData()` + `field ==
  null` ternary that fails IDENTICALLY without Activator). The residual is `ILValue == null` lowered to
  `ceq` comparing a raw mStack index (a valid index pointing to null) against ldnull's `-1` -> FALSE ->
  `ref.ToString()` on null -> `Neo callvirt this is null`. This is the INSTANCE-field form of the null-
  comparison gap (child-11 fixed brtrue for static-ref; child-12 fixed `ldsfld;ldnull;ceq` for static-
  ref via `Ceq_Ref`; THIS is `ldfld.ref <instance field>; ldnull; ceq` -- Ceq_Ref is NOT firing, meaning
  the instance-field load (or its temp) is NOT seeding registerTypes as a reference). Fires for ANY IL
  instance, not just Activator. Worth a focused re-audit child: WHY is Ceq_Ref not firing for an
  instance ref field? (Hypotheses: the field load flows through a temp local not marked
  `LocalIsReference`; or an unseeded producer class like child-21.)
- **Verify:** NeoStep **361/0** (358 + 3 probes: generic + Type + Type+object[]); stash-toggle
  CLRRedirections.cs+AppDomain.cs -> 3/3 FAULT (`MissingMethodException` via autogen `CreateInstance_1
  _Neo`) -> pop -> 0/3 PASS (airtight); `ActivatorCreateInstanceWithArgsTestSimple` PASSES under Neo.
  Legacy-neutral: plain Debug+useRegister=true+NeoStep = 361 ran/18 failed (18 = pre-existing Neo-
  specific set; 3 probes PASS under Legacy; Legacy RedirectMap registrations untouched). Files:
  `CLRRedirections.cs` (+103), `AppDomain.cs` (+20), new `TestCases/NeoStepActivatorCreateInstanceTest.cs`.
  Capability = `neo-dispatch`. Review APPROVE-WITH-FINDINGS (0 Blocker/Major; M1 null-args-guard
  divergence [more robust, unreachable], M2 AllocValueType branches deferred [unreachable, documented];
  2 Trivial; P1 ceq-null instance-field + P2 moot autogen stub noted out-of-scope).
