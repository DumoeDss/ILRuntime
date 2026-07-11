# Planning Context — implement-neo-step12

This file seeds the persistent planner. Read this FIRST, then research only
what is missing. Append durable new findings at the end after each propose.

## 1. User intent (verbatim)

> "继续推进step12. 审查完成后由你提交代码。"

Translation: drive Neo **Step 12 (帧内值类型 + Inline 字段访问 / in-frame
value types + inline field access)** to completion through the openspec
autopilot. After review is clean the LEAD commits the code (pre-authorized).
Step 11 (interface dispatch) is committed (d24e4415). Step 12 is next.

## 2. Decompose decision (LEAD, already taken)

**SKIP decompose.** Step 12 is one tightly-coupled capability: in-frame
value-type storage layout + inline Ldfld/Stfld + Initobj-memset + JIT
type-based lowering. The work items share the same files (ILType /
ILTypeInstance / JITCompiler / Optimizer / ILIntepreter.Neo) and are
sequential dependencies, not independent deliverables. No positive
independence proof → one change. Pipeline: propose → apply → verify →
review-loop → (LEAD commits) → ship → archive.

## 3. Step 12 scope (from `.trae/documents/neo-implementation-steps.md` §"Step 12")

Goal: value-type locals/temps live INLINE in the frame byte region as flat,
naturally-aligned bytes — eliminating the `ValueTypeObjectReference` /
`AllocValueType` descriptor model for in-frame value types.

Content (Step 12 ONLY):
1. Value-type local/temp slots allocated contiguous, naturally-aligned space
   in the frame's byte region.
2. Stop using `AllocValueType` / `ValueTypeObjectReference` descriptor for
   in-frame value types.
3. New `Ldfld_*_Inline` / `Stfld_*_Inline` opcodes = pure pointer arithmetic,
   zero branches, e.g.:
   `case Ldfld_I4_Inline: *(int*)(frameBase + ip->Register1) = *(int*)(frameBase + ip->Operand2); break;`
4. JIT emits the inline variant when the static type is an in-frame value
   type; keeps the existing heap-object `Ldfld_*`/`Stfld_*` for heap refs.
5. `Initobj` on an in-frame value type → `memset 0` (offset+size known at
   compile time).
6. In-frame value-type field access = pure pointer arithmetic, zero branches.

**SCOPE BOUNDARY — what is NOT Step 12 (defer to Step 12b):**
- Whole value-type ASSIGNMENT / COPY semantics (`Move_Vt` + the JIT
  `LowerMove` pass) is **Step 12b**. Step 12 must NOT implement `Move_Vt` /
  struct-copy. `Vector3 a = b;` (whole-struct copy) is 12b; `v.x = 1;
  v.y = 2; float r = v.x + v.y;` (field access) IS Step 12.
- CLR value-type Box/Unbox and CLR value-type Initobj paths are Step 13.
- `constrained.` callvirt on value types is Step 13/18.

Validation targets (Step 12): `Vector3 v; v.x=1; v.y=2; r=v.x+v.y;`; nested
value type `struct Inner{int x;} struct Outer{Inner i; int y;}`; value types
containing reference fields stored correctly on the frame.

## 4. Current code state (DO NOT re-derive; verify and build on)

- Typed `Ldfld_*` / `Stfld_*` opcodes ALREADY exist for HEAP object fields
  (`ILIntepreter.Neo.cs` ~1600-1684: Ldfld_I1..I8, U1..U8, R4, R8, Ref and
  the Stfld_* mirrors). Step 12 adds the `_Inline` siblings that index
  `frameBase` directly instead of a heap ILTypeInstance's `byte[] Primitives`.
- `Initobj` exists (`ILIntepreter.Neo.cs:1509`) with two open TODOs: line
  ~1533 "Neo Initobj reference fields require Step 7 RefOffset lowering" and
  ~1539-1540 "CLR value type Initobj: Step 13". For Step 12, IL in-frame
  value-type Initobj should become a memset-0; the ref-field sub-case ties
  into whatever ref-offset scheme the frame uses for in-frame VTs.
- `ILType.GetValueTypeSize(out fieldCount, out managedCount)` exists
  (`ILType.cs:2417`) and `ValueTypeInitializationInfo` / `ValueTypeInitInfo`
  (`ILType.cs:88, 2404`) — reuse these for size/layout. Field layout
  primitives live in ILType (`TotalPrimitiveSize` / `TotalReferenceCount` and
  the per-field offset machinery from the Step 1-3 object-model work).
- **RESEARCH REQUIRED (planner):** determine how value-type locals/temps are
  stored in the Neo frame TODAY — is there a `ValueTypeObjectReference` /
  `AllocValueType` descriptor in the Neo path, or is it Legacy-only? Grep
  `ValueTypeObjectReference`, `AllocValueType`, and the frame-layout code
  (`StackSlotInfo` / `CompiledFrame` / the byte-region allocator in
  JITCompiler + Optimizer). The Step 12 change is a STORAGE-LAYOUT change,
  so you must know the current layout precisely before designing the inline
  variant. Read `.trae/documents/object-model-design.md` (value-type /
  field-layout sections) and `.trae/documents/object-model-neo-design.md`.

## 5. Hard constraints (from CLAUDE.md — obey exactly)

- Branch `features/object-model-overhaul`; all new runtime code behind
  `#if ENABLE_NEO_MODE`. Do NOT touch Legacy paths or mix object models.
- **Build (sln CANNOT build whole):**
  - CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` (0 errors).
  - TestCases: `dotnet build TestCases/TestCases.csproj -c Debug` → `TestCases/bin/Debug/netstandard2.1/TestCases.dll`. NEVER Debug_Neo for TestCases.
- **Run tests (custom reflection framework, NOT xUnit):**
  ```
  dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
    TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep
  ```
  Always `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output (OUTPUT_JIT_RESULT) — normal; filter for the summary.
- Tests = `public static` parameterless methods in `TestCases/`, optional
  `[ILRuntimeTest]`. Follow the `NeoStep<N>Test.cs` convention — add
  `TestCases/NeoStep12Test.cs`. NeoStep smoke currently ~31 cases (incl. the
  Step 11 +5); all-green = environment healthy.
- Unimplemented-op `NotImplementedException` (tagged with a Step number) is a
  TODO, not a bug. But your Step 12 paths must not throw those for the cases
  you implement.
- A test taking **>10 seconds** usually means an interpreter infinite loop —
  kill and investigate, do not just wait.
- **REGRESSION CAUTION (higher than Step 11):** value types are pervasive —
  many existing NeoStep tests likely use value-type locals/fields/params. A
  storage-layout change can silently corrupt them. The implementer MUST run
  the full `NeoStep` smoke (not just NeoStep12) and confirm no regression,
  and the reviewer MUST scrutinize layout interaction with existing tests.
- **CJK write caveat:** the Write tool corrupts ~0.5% of CJK to U+FFFD on
  large payloads. Author files PRIMARILY IN ASCII/ENGLISH; validate large CJK
  with a pure-ASCII PowerShell codepoint check if needed.

## 6. Design decisions already settled (do not relitigate)

- In-frame value types are FLAT BYTES in the frame byte region, naturally
  aligned — not boxed, not descriptor-referenced.
- Field access on an in-frame value type is pure pointer arithmetic
  (`frameBase + offset`), zero branches — the `_Inline` opcodes.
- Heap-object field access (`Ldfld_*`/`Stfld_*` on an ILTypeInstance) is
  UNCHANGED — Step 12 only adds the in-frame variant + JIT chooses by static
  type.
- Initobj on an in-frame VT = memset 0 at a compile-time-known offset+size.
- Whole-struct copy (`Move_Vt`/LowerMove) is OUT of scope (Step 12b).

## 7. What the planner must produce (propose stage)

Invoke `openspec-propose` (Skill) OR author directly in openspec format.
Required artifacts under `openspec/changes/implement-neo-step12/`:
- `proposal.md` — Why / What Changes / Impact (ground Impact in real files).
- `design.md` — concrete, code-grounded: the in-frame VT storage layout (how
  VT locals/temps get byte-region slots + alignment; how ref-fields within an
  in-frame VT are handled — ref-offset region interaction); the new
  `Ldfld_*_Inline`/`Stfld_*_Inline` opcodes + ExecuteNeo arms (pointer
  arithmetic); Initobj memset; the JIT rule that picks inline vs heap variant
  by static type; the optimizer frame-allocation changes; edge cases (nested
  VT, VT with ref fields, VT as a field of a heap object, VT passed/returned
  — note pass/return copy is 12b-adjacent, scope carefully); explicit
  non-goals (Move_Vt/copy = 12b, CLR VT = 13, constrained. = 13/18).
- `specs/<capability>/spec.md` — ADDED requirements (no .trae draft exists
  for Step 12, so author fresh). Suggested capability: `neo-value-types` (or
  extend an existing one if a value-type capability already exists in
  `openspec/specs/` — currently only `neo-dispatch` exists, so create new).
- `tasks.md` — checkbox implementation tasks sized for one implementer pass.

Author != verifier: you ONLY propose.

## 8. Append-only findings log

(planner appends durable decisions/constraints discovered during propose here)

### 2026-07-04 — PLANNER propose pass (durable findings)

**FINDING A — `ValueTypeObjectReference` / `AllocValueType` is Legacy-only
(this reshapes the whole change).** Full-repo grep: every reference lives in
`ILIntepreter.cs`, `ILIntepreter.Register.cs`, `RuntimeStack.cs`,
`StackObject.cs`, `ValueTypeBinder.cs`, `DebugService.cs`,
`CLRRedirections.cs`, `ValueTypeInitInfo.cs`, and one Legacy path in
`ILTypeInstance.cs`. The Neo files (`ILIntepreter.Neo.cs`, Neo regions of
`JITCompiler.cs`, `Optimizer.Neo.cs`) contain **zero** references. So the
Neo path NEVER adopted the descriptor model — the Step 12 title's
"eliminating ValueTypeObjectReference" is a Legacy framing, not a Neo
removal. The actual Neo work is: add alignment + add `_Inline` opcodes +
add the JIT discriminator + resolve the Initobj ref-field sub-case.

**FINDING B — in-frame VT storage already exists.**
`JITCompiler.AllocateLocalStackSpaces` (JITCompiler.cs ~1129-1156) already
reserves a contiguous `slot.Offset .. +il.TotalPrimitiveSize` byte range
(+ `slot.RefOffset .. +TotalReferenceCount` ref slots) for VT locals/temps/
the HasThis value-type case. The StackRegisterCount temp loop (~1195-1205)
already sizes each temp at the max VT size via `GatherValueTypes`. So Step 12
is NOT a storage-shape change — only alignment is added.

**FINDING C — the existing heap Ldfld/Stfld arms are the wrong path for
in-frame VT operands.** ILIntepreter.Neo.cs ~1600-1688 dereferences an
`ILTypeInstance` from `mStack[*(int*)(frameBase + ip->SrcOffset)]`. For an
in-frame VT operand, that slot holds raw bytes, NOT an mStack index — so
today any field access on a stack VT mis-executes. This is the core gap
Step 12 closes.

**FINDING D — JIT discriminator is operand value-category, NOT the field's
declaring type.** `AppDomain.GetFieldOffset(token, declaringType, method,
out type, out fieldType)` (AppDomain.cs ~1860-1877) returns the field's
declaring ILType in `type` — identical whether the operand is a heap instance
or an in-frame VT. The discriminator MUST be the operand register's
value-category (in-frame VT vs reference slot), tracked by the stack-
simulation register allocation. The current `if (type is ILType)` test at
JITCompiler.cs ~1859-1868 is NOT sufficient and must be augmented.

**FINDING E — per-field offset machinery is reusable as-is.**
`ILType.InitializeFields` (ILType.cs ~2084-2114) already accumulates nested-
VT primitive/ref offsets into the outer VT's `ILTypeFieldOffset`, so an
inline field address is simply `frameBase + slot.Offset +
field.PrimitiveOffset` (and the ref case is `mStack[frameRefBase +
slot.RefOffset + field.ReferenceOffset]`). No new offset computation needed.

**FINDING F — Initobj ref-field blocker is just missing slot-RefOffset
encoding.** ILIntepreter.Neo.cs ~1533 throws "Step 7 RefOffset lowering"
because the `Initobj` instruction does not carry the target slot's
`RefOffset`. Fix = have the optimizer's Neo offset-lowering stamp the slot
RefOffset into a spare operand (design §3 option A). CLR VT Initobj
(~1539) stays Step 13.

**DECISION — capability name.** Created new capability `neo-value-types`
(only `neo-dispatch` existed in `openspec/specs/`). `neo-dispatch` is
unaffected (Step 12 changes value-type storage/field access, not dispatch).

**DECISION — non-goals fenced crisply.** Whole-VT copy (`Move_Vt`+
`LowerMove`) = Step 12b; CLR VT Box/Unbox/Initobj + `constrained.` =
Step 13/18; IL VT `newobj` = Step 18; `Ldfld_Value`/`Stfld_Value` inline
= part of 12b. All Legacy paths untouched (`#if ENABLE_NEO_MODE` only).

**REGRESSION NOTE — full NeoStep smoke is mandatory.** Value types are
pervasive across NeoStep6-11; an alignment/storage change can silently
corrupt them. Task 7.3 requires the full `NeoStep` smoke (not just
NeoStep12) to stay green. Reviewer must scrutinize layout interaction.

Artifacts authored: proposal.md, design.md, specs/neo-value-types/spec.md,
tasks.md. `openspec status --change implement-neo-step12` → isComplete:true,
all 4 artifacts done, apply-ready.

### 2026-07-04 — IMPLEMENTER apply pass (durable findings)

**FINDING G — The C# compiler emits `ldloca` for ALL struct field access, so
Step 12 REQUIRED implementing the ldloca/ldflda address chain (the design
missed this).** For `v.x = 1f` on a struct local, Roslyn emits
`ldloca.s V; ldc; stfld x` — the stfld operand is the ldloca DEST (a temp),
not the local. `Ldloca_S`/`Ldflda` were Step-6 TODOs (threw in ExecuteNeo).
So in-frame-VT field access is unreachable unless the address chain is
resolved. Resolution implemented in `Optimizer.Neo.cs` `LowerNeoOffsets`:
a pre-scan builds `addrAlias: Dictionary<short, NeoAddressAlias>` where
`NeoAddressAlias { short Reg; int Offset; }` maps each ldloca/ldflda dest to
(underlying local + accumulated nested-field PrimitiveOffset). The inline
field ops and Initobj resolve their owning-VT operand via
`ResolveAddressAlias` before reading `localInfos[reg].Offset`. `Ldloca`/
`Ldloca_S`/`Ldflda` ExecuteNeo arms are now no-ops (the addresses are
folded at compile time; genuine byref/ref-param/fixed pointer use remains
a separate Step 6+ pointer model). `JITCompiler.TypeSpecializeNeoOpcodes`
propagates the source VT type to the ldloca dest so the inline discriminator
fires.

**FINDING H — ldloca alias filter MUST use `LocalIsReference`, not
`StackSlotInfo.Size`.** A 4-byte VT with one int + one ref field (e.g.
`struct { int a; string b; }`) has Size=4, RefCount=1 — identical to a plain
reference slot. Filtering aliases by `Size != 4` wrongly excludes such VTs
and field writes go to a dead temp (silent corruption). The correct
discriminator is `CompiledFrame.LocalIsReference[src]` (true only for
reference locals). Params/temps default to false in that array, which is
fine: a reference param's ldloca dest keeps the reference type → the inline
discriminator (type-based) leaves it on the heap path, so aliasing it is
harmless.

**FINDING I — `AllocateNeoCallParamSlot` MUST stay CONTIGUOUS (no natural
alignment); aligning it regresses CLR small-primitive calls.** The autogen
CLR binding redirects read params SEQUENTIALLY via `ReadNeo*` (`curPrim +=
size`, no alignment). If the Neo caller's callee-param layout
(`AllocateNeoCallParamSlot`, used for CLRMethod targets) applies natural
alignment, the writer and reader disagree on offsets and the call returns
garbage (regressed `NeoStep7Step8Test.NeoTestCLRBindingSmallPrimitiveArgs`,
verified green pre-Step-12). FIX: alignment is applied ONLY to the IL
frame's own locals/temps/params (`JITCompiler.AllocateLocalStackSpaces` +
`AllocateSlotForType`), NOT to the CLR call-param buffer. IL-to-IL calls
are unaffected (they copy caller temps → callee
`ilm.CompiledFrame.ParamInfos`, both aligned consistently). The interface-
signature synthesis (`AllocNeoParamInfosFromSignature`) also uses the
contiguous helper; this matches its pre-Step-12 behavior and the NeoStep11
interface tests (reference params, where contiguous == 4-aligned).

**FINDING J — Nested-VT READ (`int t = o.i.x`) compiles to `Ldfld_Value`
(whole-struct copy = Step 12b), so the nested test is write-focused.**
Roslyn copies the whole `Inner` out then reads `.x`. The design §6.1
assumed `o.i.x` lowers to a single address-based inline access, but a
value-typed rvalue read does not. The nested test (`NeoStep12TestNestedValueType`)
therefore exercises the WRITE path (ldloca + ldflda + stfld at the absolute
nested offset — the design's core nested scenario) plus the sibling `o.y`
field and offset-independence checks, and avoids any whole-`Inner` read.

**FINDING K — Assertion mechanism: DivideByZero, not `throw new Exception`.**
`throw new Exception(msg)` in interpreted code hits CLR newobj
(`System.Exception::.ctor`), which is Step 9 (unimplemented in Neo), so a
throw-based assertion cannot be constructed inside the interpreter and would
mask the real failure. NeoStep11's throw-based asserts only work because the
assertions never fire (tests pass). NeoStep12 tests use the DivideByZero
native-fault pattern (like NeoStep7Step8Test). Task 6.6's requested
`Console.WriteLine`+throw pattern is not viable in Neo until CLR newobj
(Step 9) lands.

**DEViation from design.md (consolidated):**
1. Discriminator lives in `TypeSpecializeNeoOpcodes` (which has the
   per-register `registerTypes`), NOT threaded into `Translate`. The JIT
   still emits the heap Ldfld_*/Stfld_* in Translate; the type-specialize
   pass rewrites to `_Inline` when the operand (resolved through the ldloca
   alias) is an in-frame VT. Cleaner than threading types into Translate.
2. Ldfld_Ref_Inline / Stfld_Ref_Inline operand encoding:
   `Operand`  = owning slot RefOffset + field.ReferenceOffset (absolute
   frame-ref index), `Operand4` = dest temp RefOffset (Ldfld only).
   `Operand3` carries the field's ReferenceOffset from the JIT and is folded
   into `Operand` at lowering. `Operand2` (PrimitiveOffset) unused for Ref.
3. Initobj ref-field sub-case: design option A — the lowering stamps the
   target slot's RefOffset into `Operand3` (spare; Initobj uses
   Operand=type-token + DstOffset). ExecuteNeo nulls
   `mStack[frameRefBase + Operand3 + i]` for i in 0..refCnt.
4. `Ldloca`/`Ldloca_S`/`Ldflda` added as no-op ExecuteNeo arms (addresses
   resolved at lowering; genuine byref = future step).
5. Tests use DivideByZero assertions, not throw (Finding K).

**RESULT — full NeoStep smoke: `Ran 36 tests, 0 failed`** (31 pre-existing +
5 new NeoStep12). CLI Debug_Neo build 0 errors; TestCases Debug build 0
errors. `NeoTestCLRBindingSmallPrimitiveArgs` regression (Finding I) caught
and fixed by the mandatory full-smoke gate — value types / small primitives
are pervasive, confirming the design's regression caution was warranted.
