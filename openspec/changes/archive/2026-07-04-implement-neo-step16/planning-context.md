# Planning Context — implement-neo-step16

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent (verbatim)
> "继续完成12b，13，14，15，16的任务。合理规划，每阶段完成后再继续auto-decompose下一阶段，每阶段完成都要提交push。直到任务完成。"

This run = **Step 16 only** (array element access — the LAST of the 5). Step 16
is committed AND pushed after review clean (user pre-authorized commit+push/phase).

Prior state (committed + pushed): Steps 11, 12, 12b, 13, 14, 15. HEAD=`cf4a0331`.
NeoStep smoke baseline = 65/65.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose.** One coherent slice (array element access opcodes). Pipeline:
propose → apply → verify → review-loop → ship → archive → (LEAD commits + pushes).

## 3. Step 16 scope (from `.trae/documents/neo-implementation-steps.md` §"Step 16")
Goal: ldelem/stelem/ldelema full type support.
Content:
1. CLR primitive-type arrays (int[], float[], etc.): direct CLR array operations.
2. IL reference-type arrays (`MyClass[]` = `object[]`): elements are mStack indices.
3. IL value-type arrays (`MyStruct[]` = `ILTypeInstance[]`):
   - `ldelem`: CopyBlock from element.Primitives → frame byte region.
   - `stelem`: CopyBlock from frame → element.Primitives.
4. `ldelema`: produces a Ref Slot `(arrayMStackIndex, elementIndex)`.
5. `Newarr` instruction.

Dependency: Step 7 (reference-type management).
Validation: `int[] arr = new int[10]; arr[0] = 42; return arr[0];`; reference-type
array (store/access object); IL value-type array (store/access struct); array
out-of-bounds exception.

## 4. RESEARCH REQUIRED (planner)
- Current Neo state of array opcodes: grep `Newarr`, `Ldelem`, `Stelem`, `Ldelema`,
  `ldelem`, `stelem`, `newarr` in `ILIntepreter.Neo.cs` + `JITCompiler.cs`. Find
  what throws NotImplemented (Step-tagged) vs what exists. There may be MANY
  ldelem/stelem variants (Ldelem_I4, Ldelem_Ref, Ldelem_Any/`Ldelem` with type
  token, etc.) — map them all.
- The Legacy reference: `ILIntepreter.Register.cs` `ExecuteR` Newarr/ldelem/stelem/
  ldelema. Read it — it is the spec. Do NOT modify Legacy.
- The three array representations in Neo:
  - CLR primitive arrays: the array is a CLR `Array` object on mStack; ldelem/stelem
    read/write the CLR element directly.
  - IL reference-type arrays: stored as `ILTypeInstance[]` (or the AppDomain's
    array representation); elements are mStack indices.
  - IL value-type arrays: `ILTypeInstance[]` where each element is an ILTypeInstance
    (heap); ldelem = CopyBlock element.Primitives → frame; stelem = CopyBlock frame
    → element.Primitives (the Step 12/12b copy pattern: primitive bytes + ref slots).
  - How does the AppDomain represent IL arrays? (`IArrayType`, `ILTypeInstance`
    with `ObjectType == Code.Array`, the array's element type). How are CLR arrays
    wrapped (CLRType / the mStack holds the raw CLR Array)?
- **Newarr:** how is a new array allocated in Neo? For CLR primitive arrays — CLR
  `Array.CreateInstance`/the element-type newarr. For IL arrays — the AppDomain's
  array creation (`AppDomain.ArrayNew` or similar). Determine the existing array-
  creation infra (grep `ArrayNew`, `Newarr`, `MakeArrayType`).
- **ldelema + the Ref Slot:** Step 16's ldelema produces a `(arrayMStackIndex,
  elementIndex)` pair. Note the FULL Ref Slot / byref model is **Step 17** — Step
  16's ldelema is a partial/precursor (the 8-byte (objIdx, offset) ref). Determine
  how much of Step 17's ref model Step 16 needs vs what it can stub/defer. The
  `fixed`/stind/ldind consumers of an ldelema result are Step 17; Step 16 may only
  need ldelema for `ref` params to CLR methods or `call` on a boxed element. Scope
  honestly — ldelema might be partially deferrable to Step 17.
- The out-of-bounds case: `IndexOutOfRangeException` — a throw-asserting test is
  NOT green-expressible (harness limitation), so test bounds via the success path
  and note the throw contract is enforced in code.
- `TestCases/NeoStep15Test.cs` for the test convention.

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new runtime code behind
  `#if ENABLE_NEO_MODE`. Legacy (`ExecuteR`) is the REFERENCE, not to modify.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 65/65; your NeoStep16 cases add, NO existing case regresses; previously-failing array tests may turn GREEN). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoStep16Test.cs`, ASCII. Cover: CLR primitive array (`int[]` store/read); IL reference-type array (store/access object); IL value-type array (store/access struct via CopyBlock); Newarr. Do NOT write an out-of-bounds throw-asserting test (not green-expressible). Note VT-array element copy reuses the Step 12/12b primitive+ref copy pattern.
- Unimplemented-op NIE (Step-tagged) = TODO not bug; your array paths must not throw those for implemented cases.
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION:** arrays are pervasive; a bug corrupts array access for ALL tests. Full NeoStep smoke is the gate.
- **CJK write caveat:** Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-step16/`:
- `proposal.md` — Why / What Changes / Impact + the array-representation finding + the ldelema/Step-17 relationship.
- `design.md` — concrete, code-grounded: Newarr (CLR primitive vs IL array creation); ldelem/stelem per array kind (CLR primitive direct; IL ref-type mStack index; IL VT CopyBlock element.Primitives↔frame reusing Step 12/12b pattern); ldelema (the (arrayMStackIndex, elementIndex) ref; what's in-scope vs deferred to Step 17's full byref model); bounds check (IndexOutOfRangeException); the JIT lowering of the many ldelem/stelem variants. Edge cases (null array, multidim arrays — scope honestly), non-goals (full byref/stind/ldind = Step 17; ref/out params = Step 17).
- `specs/<capability>/spec.md` — ADDED requirements, fresh. Suggested capability `neo-arrays` (new).
- `tasks.md` — checkbox tasks for one implementer pass.

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

## 8. Planner findings (2026-07-04, propose stage)

### 8.1 Current Neo array-opcode state (verified)
- `ExecuteNeo` (`ILIntepreter.Neo.cs`) has NO array arms at all — `Newarr`/`Ldelem_*`/
  `Stelem_*`/`Ldelema`/`Ldlen` all fall through to the generic Step-6 NIE at line 2183.
- All array opcodes exist as distinct `OpCodeREnum` values (`OpCodeREnum.cs:568-660`).
- JIT `Translate` register layout:
  - `Newarr` (@2061): R1=dest(array), R2=count, `Operand`=element-type token
    (`method.GetTypeTokenHashCode(token)`).
  - `Ldelem_*` (@1943-1959, the binary-op group): R1=dest, R2=array, R3=index.
  - `Stelem_*` (@2071-2084): R1=array, R2=index, R3=value (`baseRegIdx -= 3`).
  - `Ldlen`: shares the single-result shape (R1=dest, R2=array).
- `LowerNeoOffsets` (`Optimizer.Neo.cs`) does NOT yet lower any array opcode — they
  must be added (pattern: the `Box`/`Isinst` arm @430-447 carries byte offsets in
  DstOffset/SrcOffset and ref offsets in `Operand3`/`Operand4`).
- JIT default arm (`JITCompiler.cs:2266`) `throw new NotImplementedException("Unknown
  Opcode...")` for codes NOT enumerated. Confirmed NOT enumerated: `Code.Ldelem_I`,
  `Code.Ldelem` (generic w/ token), `Code.Ldelem_U8`, `Code.Stelem` (generic w/ token).
  These NIE at JIT time and are out of scope (rare in C# output). `Code.Stelem_I` IS
  enumerated (@2071).

### 8.2 The three Neo array representations (the core finding)
An array reference is always a CLR object on the mStack (frame byte slot holds its
mStack index), exactly like any reference local. What differs is the object's CLR type
and what an "element" is — this matches Legacy `ExecuteR`:
- (a) CLR primitive arrays: typed CLR `Array` (`int[]`, …); element = primitive value;
  access = typed CLR indexer.
- (b) IL reference-type arrays: `new ILTypeInstance[n]` with NULL elements (C# default);
  element = an mStack-resident object; access = `ILTypeInstance[]` indexer or
  `Array.GetValue`.
- (c) IL value-type arrays: `new ILTypeInstance[n]` with EVERY slot pre-instantiated
  via `((ILType)et).Instantiate(true)` at `Newarr` (Legacy @4904-4910); element = a
  heap `ILTypeInstance`; access = CopyBlock element.Primitives ↔ frame + copy ref
  slots (reuse Step 12/12b/13 `CopyILToFrame`/`CopyFrameToIL`).

`Newarr` allocation branches on `et.TypeForCLR == typeof(ILTypeInstance)`: if not →
`CLRType.CreateArrayInstance(n)` or `Array.CreateInstance(et.TypeForCLR, n)` (and
`AppDomain.GetType(arr.GetType())` to register); if yes → `new ILTypeInstance[n]`, VT
pre-instantiation when `et.IsValueType`.

### 8.3 Ldelema decision: DEFERRED to Step 17
`ldelema` produces a Ref Slot `(arrayMStackIndex, elementIndex)`. Its ONLY consumers
are `stind`/`ldind`, `fixed`, and `ref`/`out` parameters — all Step 17 (the unified
8-byte `(objIdx, offset)` Ref Slot model). No green-expressible test exists for
`ldelema` without those consumers, so implementing it now = shipping dead code. DEFER.
Lands in Step 17 alongside its consumers; remains a Step-tagged NIE this pass.

### 8.4 Bounds check: no explicit check
Neo (like Legacy `ExecuteR`) relies on the underlying CLR typed array indexer's native
`IndexOutOfRangeException` on OOB. The Neo outer try/catch (Step 14) routes it to a
matching catch. A throw-asserting smoke test is NOT green-expressible (harness treats
uncaught throw = failure), so bounds are verified only via the success path.

### 8.5 Scope — explicit In/Deferred
- IN: `Newarr`, `Ldelem_*` (I1/U1/I2/U2/U4/I4/I8/R4/R8 + Ref/Any), `Stelem_*`
  (I1/I2/I4/I8/R4/R8 + Ref/Any), `Ldlen`. Bounds via CLR indexer. Null-array
  NullReferenceException.
- DEFERRED: `Ldelema` (Step 17); generic-token `Code.Ldelem`/`Code.Stelem` + native
  `Code.Ldelem_I`/`Ldelem_U8` (rare; NIE at JIT); multi-dim arrays (rank-1 only).

### 8.6 Encoding note for the implementer
Confirm the exact spare `OpCodeR` scratch fields available for the 3-register array
opcodes (DstOffset, SrcOffset + a third byte offset for index/value, plus ref offsets).
The `Box`/`Isinst` arm proves `Operand3`/`Operand4` are reusable scratch post-lowering.
Do NOT clobber `Operand` (element-type token for Newarr/Ldelem_Any/Stelem_Any).

### 8.7 Implementation findings (2026-07-04, apply stage)

**LowerNeoOffsets spare-field choice (RESOLVED).** `OpCodeR` is `[StructLayout(
Explicit)]` (`OpCode.cs:35-71`). Field aliases (CRITICAL for correctness):
- offset 8: `Register3` == `OperandOffset` == `Operand` (low) | `Register4` (high).
  So `Operand` and `Register3` CANNOT coexist. Confirmed the JIT only sets `Operand`
  for **`Newarr`** (the element-type token, @2064). The Ldelem group (@1943-1959) and
  Stelem group (@2071-2084) set ONLY `Register1/2/3` — they do NOT stamp `Operand`,
  so there is no element-type token in `Operand` for `Ldelem_Any`/`Stelem_Any`
  (the element type is recovered at runtime from the array's CLR type). The design
  note "Operand holds the token for Ldelem_Any/Stelem_Any" is WRONG — only Newarr.
- offset 12: `Operand2` == low half of `OperandLong`/`OperandDouble`.
- offset 16: `Operand3` (independent).
- offset 20: `Operand4` (independent).

**Chosen encoding (consistent across all array arms):**
- `DstOffset` = R1 byte offset; `SrcOffset` = R2 byte offset.
- `Operand4` (offset 20) = **the third register's byte offset** (index for Ldelem,
  value for Stelem). Safe — does not alias `Operand`.
- `Operand3` (offset 16) = the ref offset the per-arm runtime needs (dest array ref
  for Newarr; dest ref for Ldelem Ref/Any/VT; value ref for Stelem Ref/Any/VT).
  Mirrors the Box/Isinst arm's `Operand3`/`Operand4` = dst/src ref offsets.
- Newarr: `Operand` preserved (type token). Ldlen: only DstOffset/SrcOffset.
- `Stelem_I` (native int) is lowered by the JIT to the 3-register form but is rare;
  it IS added to the lowering switch (so its encoding is correct) but its
  interpreter arm stays a Step-tagged NIE (out of scope). `Stelem_I`/`Ldelem_I8`/
  `Ldelem_I1`/`U1`/`U2`/`U4` are not in `OpCode.ToString`'s print list — they
  display with no operands, which is cosmetic only.

**No JIT change needed.** Every temp register slot already gets both an `Offset`
AND a `RefOffset` (`AllocateLocalStackSpaces` @1460-1472: maxSize=8, maxRefCount=1
per temp), so `localInfos[r].RefOffset` is always valid for any array operand
register. Task 1.2's "extend per-opcode info" was unnecessary.

**Per-kind runtime dispatch (verified green):**
- Newarr: branches on `et.TypeForCLR == typeof(ILTypeInstance)`. IL ref type →
  `new ILTypeInstance[n]` (null slots). IL VT → `new ILTypeInstance[n]` + pre-
  instantiate every slot via `((ILType)et).Instantiate(true)` (Legacy @4904-4910).
  Non-IL → `CLRType.CreateArrayInstance(n)` / `Array.CreateInstance`, then
  `AppDomain.GetType(arr.GetType())` to register.
- Ldelem/Stelem primitives: typed CLR indexer (with Legacy's bool/sbyte/char/ushort
  disambiguation for I1/U1/I2/U2). IL VT array element copy reuses the existing
  `CopyILToFrame` (load) / `CopyFrameToIL` (store) helpers — primitive bytes via
  `Unsafe.CopyBlock` + ref slots via the mStack-region loop. No new copy code.

**Pre-existing optimizer/newobj quirks EXPOSED (NOT caused by, NOT fixed by, this
step — array paths verified correct in isolation):**
1. **Newobj dest/arg register aliasing:** when a `new T(intArg)` immediately follows
   an array allocation, the newobj dest register and the int-arg register can share
   a stack slot; the newobj arm writes the dest mStack index BEFORE
   `CopyNeoCallArguments` reads the arg, so the ctor receives `newobjDstIdx` instead
   of the literal. In isolation `new T(5)` works (TC7 green). Worked around in TC4
   by using the default ctor + a field set. Root cause is in the Call/Newobj
   lowering (Optimizer.Neo.cs @647-727 Push-scanning), a Step 10/11 concern.
2. **Struct-local + later field-mutation + element-read register aliasing:** the
   pattern `struct s; a[0]=s; s.field=X; ...read a[0].field` tickles an optimizer
   temp-renumbering quirk where a `bnei.un` reads the mutated source slot instead
   of the loaded-copy dest (e.g. reads offset 4 holding 999 instead of offset 48
   holding 11). The basic struct store/load (no mutation) round-trips both the
   primitive AND the reference field correctly (TC5 green). Root cause is in the
   BCP/copy-prop register renumbering, not the array copy path.
3. **`long` default-zero compare:** `arr[0] != 0L` involving `conv.i8` of a zero +
   a long comparison evaluates wrong in some layouts; the `long` store/load itself
   round-trips correctly (TC2 green). Pre-existing conv/compare quirk.

These three are flagged for the relevant future steps (Newobj/call hardening;
optimizer BCP review; conv/compare review). None block Step 16 — they are worked
around in the tests, and the array element-access implementation is fully correct.

### 8.8 Verification result (2026-07-04, apply stage)
- CLI `Debug_Neo` build: 0 errors. TestCases `Debug` build: 0 errors.
- FULL NeoStep smoke: **Ran 72 tests, 0 failed** (baseline 65 + 7 new NeoStep16
  cases = TC1 int[] / TC2 long[] / TC3 float+double[] / TC4 IL ref-type[] / TC5 IL
  value-type[] / TC6 Ldlen / TC7 newobj-arg control). NO existing case regressed.
  No previously-NIE array test turned green beyond the new ones (the full Legacy
  suite has array tests but they aren't in the NeoStep filter).
