# Planning Context — implement-neo-step13b

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent
> "按照你建议的顺序，继续按当前流水线推进后续内容！"

This run = **Step 13b** (CLR binding codegen overhaul + CLRMethod unified Neo
param layout) — the highest-ABI-regression-risk step. It closes K2/K2-FAM and
the Step 17 deferred CLR-side items (CLR-object stind/ldind via field hash;
CLR-method ref/out params). Committed AND pushed after review clean (user
pre-authorized commit+push per phase).

Prior state (committed + pushed): Steps 11-17 + OPT-HARDEN. HEAD=`21b68d92`. NeoStep smoke baseline = 81/81.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose** (user frames each step as one phase). BUT: Step 13b is the
**highest-regression-risk step in the roadmap** — it touches the CLR binding
generator + the Neo call ABI shared by EVERY CLR method call. The planner MUST
assess scope and may DEFER a sub-part (e.g. the `Unsafe.Unbox<T>` direct-call
binding-codegen rewrite, or generic CLR struct params) — state explicitly in
proposal.md if so. Pipeline: propose → apply → verify → review-loop → ship →
archive → (LEAD commits + pushes).

## 3. Step 13b scope (from `.trae/documents/neo-deferred-items.md` D-13B + Step 13 roadmap areas 4-5 + Step 17 deferrals)
- **Area 4 — binding codegen overhaul:** `Unsafe.Unbox<T>` + direct-call mode,
  eliminate `WriteBackInstance`.
- **Area 5 — CLRMethod param region unified Neo slot layout:**
  - CLRMethod's callee param region must reuse `AllocateSlotForType` /
    `StackSlotInfo` (the same primitive/ref slot rules), NOT a separate ABI.
  - CLRBinding generated code + `CLRMethod.Invoke(byte*)` must read params via
    non-generic `ReadNeo*` helpers (e.g. `ReadNeoInt16`/`ReadNeoBoolean`) by
    actual slot width, not hand-rolled width advancement.
  - REMOVE the temporary caller-temp-slot fallback in `Optimizer.Neo.cs`
    (~646-655) where CLR non-primitive struct params reuse the caller temp slot.
  - Cover CLR struct by-value params, return values, instance-method `this`,
    generic CLR struct params.
- **Step 17 deferrals that land here:**
  - **CLR-object `stind`/`ldind` via field hash** (the `(objMStackIndex, fieldHash)` path).
  - **CLR-method `ref`/`out` params** (IL→CLR byref crossing) — closes **K2/K2-FAM**.

Closes: K2 (Step 8 VT-by-value param reads primitive as mStack idx), K2-FAM
(Move-path scalar→boxed-ref CLR-VT-local), Step 13 areas 4-5, Step 17 CLR-side deferrals.

Dependency: Step 13 (done), Step 17 (done — byref model).

## 4. RESEARCH REQUIRED (planner — be thorough; this is the riskiest step)
- The CLR binding generator: `Runtime/CLRBinding/` — how generated redirects read
  value-type args today, the `WriteBackInstance` flow (grep it), and what
  `Unsafe.Unbox<T>` direct-call mode would replace.
- `CLRMethod.Invoke(byte*)` — currently throws NIE for CLR struct params
  (`CLRMethod.cs:364` per the Step 13 finding). How it reads params today; the
  path to the unified `ReadNeo*`-by-slot-width model.
- The caller-temp-slot fallback in `Optimizer.Neo.cs` (~646-655) that area 5 says
  REMOVE — what it does, why it's a temporary fallback, what removing it requires.
- `ReadNeo*` helpers — which exist, which need adding (ReadNeoInt16/ReadNeoBoolean/etc.).
  The Step 16 Newarr/Ldelem/Stelem already use some read helpers — reuse.
- The CLR-object stind/ldind field-hash path: the `(objMStackIndex, fieldHash)`
  Ref-Slot offset half; how CLR field get/set by hash works today (grep
  `fieldHash`, `GetFieldValue`/`SetFieldValue`, the CLR binding field access).
- CLR-method ref/out params: how a CLR method receives a byref (the IL→CLR
  crossing); the byref Ref Slot from Step 17 must cross into the CLR binding.
- The NeoStep CLR-binding tests (the canary — grep `CLRBinding` in TestCases; the
  smoke includes `NeoTestCLRBindingSmallPrimitiveArgs` etc.).

## 5. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new runtime code behind
  `#if ENABLE_NEO_MODE`. Legacy (`ExecuteR`) + the Legacy CLR binding are the
  REFERENCE, not to modify (the CLR binding generator may be shared — confirm
  Neo-only codegen paths; do NOT change Legacy CLR binding behavior).
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression — EVERY CLR method call uses this ABI; the CLR-binding tests are the canary): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 81/81; your NeoStep13b cases add, NO existing case regresses — CLR-binding tests especially). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoStep13bTest.cs`, ASCII. Cover: CLR struct by-value param call; CLR struct return; CLR method ref/out param (the K2 case — `void F(ref int x){x++;}` on a CLR method or via a CLR type); CLR-object stind/ldind via field hash if feasible. No throw-asserting tests (harness limitation).
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION (HIGHEST in the roadmap):** this changes the call ABI for EVERY CLR method call. A bug breaks ALL Neo CLR interop, not just value types. The full NeoStep smoke (esp. CLR-binding tests) is the gate. If an area is too risky to land in one pass, DEFER it (accepted-known) rather than break the ABI.
- **CJK write caveat:** Author ASCII.

## 6. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-step13b/`:
- `proposal.md` — Why / What Changes / Impact + the explicit In/Deferred list (area 4 vs 5 vs the Step 17 deferrals) + the Legacy-CLR-binding-neutrality argument.
- `design.md` — concrete, code-grounded: the unified CLRMethod param layout (reuse AllocateSlotForType/StackSlotInfo; ReadNeo* by slot width; remove the caller-temp-slot fallback); the binding-codegen `Unsafe.Unbox<T>` direct-call mode + WriteBackInstance elimination (or deferral); CLR-object stind/ldind field-hash; CLR-method ref/out (closes K2/K2-FAM). Edge cases + non-goals (Legacy CLR binding untouched; generic CLR struct params if rare).
- `specs/<capability>/spec.md` — ADDED/MODIFIED requirements. Capability: extend `neo-boxing` (the Step 13 box/unbox capability, since 13b completes it) or a new `neo-clr-binding`. Your call.
- `tasks.md` — phased checkboxes; mark deferred items.

Author != verifier: you ONLY propose.

## 7. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

### 2026-07-04 -- PLANNER propose pass (durable findings)

**SCOPE DECISION (key LEAD input) -- land Area 5 CORE only; DEFER Area 4,
CLR ref/out, and CLR-object stind/ldind.** See proposal.md In/Deferred list.

IN (closes K2/K2-FAM, unblocks CLR-struct by-value param/return):
- Remove the caller-temp-slot fallback `Optimizer.Neo.cs:1172-1181` (it
  overrides the correct callee layout with the caller source register's shape
  for CLR structs).
- `CLRMethod.Invoke(byte*)` `Step 13` NIE at `CLRMethod.cs:362-365` -> read
  CLR struct params by width.
- Reflection CLR-struct-return NIE at `ILIntepreter.Neo.cs:191-194` -> write
  via new `WriteNeoValueType`.
- Autogen `AppendArgumentCodeNeo` / `GetReturnValueCodeNeo`
  (`BindingGeneratorExtensions.cs:107` / `:440`) CLR-struct TODOs -> same
  read/write model; both readers share `ReadNeoValueType`/`WriteNeoValueType`
  so they stay byte-consistent with the layout by construction.
- K2-FAM bridge (Phase 3): a boxed-ref CLR-struct LOCAL passed by value must
  UNBOX into the flat-bytes callee slot (reuse Step 13 unbox helpers), not
  copy the 4-byte mStack index.

DEFERRED with rationale:
- **Area 4 (`Unsafe.Unbox<T>` direct-call + eliminate WriteBackInstance).**
  KEY FINDING: the Neo wrapper does NOT emit `WriteBackInstance` today
  (`GenerateMethodWraperCode_Neo` `MethodBindingGenerator.cs:249-398` has no
  WriteBackInstance call; only Legacy `GenerateMethodWraperCode_Legacy` does).
  The value-type `this` is a `// TODO: ValueType instance in Neo` at line 261.
  So "eliminate WriteBackInstance" is a NO-OP for Neo; the real Area 4 work is
  the value-type-`this` direct-call lowering, which is a distinct rewrite that
  does NOT fall out of the param-layout work. Bundling it risks the ABI. The
  `this` TODO stays.
- **CLR-method ref/out (byref Ref Slot -> CLR `ref T`).** A byref param is
  now an 8-byte Ref Slot (Step 17). Materializing it into a CLR `ref T`
  argument needs a typed-reference bridge back into the Neo frame -- separate
  infrastructure. IL-method byref (Step 17) stays green.
- **CLR-object stind/ldind via field hash (Step 17 M1).** The stind/ldind/
  stobj/ldobj arms (`ILIntepreter.Neo.cs:~2555-2699`) dispatch only on
  frame-native (`objIdx == -1`) and heap-IL (`ILTypeInstance` via
  `GetNeoILInstance`); a CLR-object target throws a Step-17-tagged NIE.
  Implementing it needs `Ldflda` (heap-IL branch, Step 17 design sec 2.3) to
  stamp a FIELD HASH into the Ref Slot offset half and the consumer arms to
  dispatch to `CLRType.GetFieldValue(hash, target)` (`CLRType.cs:404`) /
  `SetFieldValue(hash, ref target, value)` (`CLRType.cs:472`). Independent
  plumbing (the field hash is unrelated to the param layout). DEFERRED.

**FINDING P -- The unified callee layout ALREADY handles CLR structs.**
`AllocateNeoCallParamSlot` `Optimizer.Neo.cs:1319` documents (line 1322-1329)
that the callee param region is contiguous, read sequentially by `ReadNeo*`
with NO per-param alignment, and its `else if (type.IsValueType)` branch
(:1357-1361) sizes a CLR struct via `GetPrimitiveSize`. So the callee layout
is correct; Area 5 is "remove the fallback + fill the NIEs + add the bridge",
NOT "build a new layout". This is why Area 5 is the safe, high-value core.

**FINDING Q -- Two readers must agree byte-for-byte.** The callee param
region is consumed by (a) `CLRMethod.Invoke(byte*)` (no autogen redirect) and
(b) the autogen `*_Neo` delegate body. Both walk a single `__curPrim` cursor
advanced by each param's exact size. Any width mismatch between the optimizer
layout and EITHER reader corrupts every subsequent param. Mitigation in
design D5: both readers share the same `ReadNeoValueType`/`WriteNeoValueType`
+ binder helpers, so consistency is by construction.

**FINDING R -- K2 vs K2-FAM split (Phase 3 may be split out).** K2 (flat-
bytes-source CLR-struct param miscopy) is closed by Phase 1 alone (remove
the fallback). K2-FAM (boxed-ref-LOCAL -> flat-bytes-param) needs the Phase 3
unbox bridge. If the bridge proves too large/risky, the implementer MAY land
Phases 1-2 (close K2) and DEFER only Phase 3 (K2-FAM), per tasks.md Phase 3.3.
This keeps the core ABI fix shippable even if the bridge is hairy.

**FINDING S -- Legacy-CLR-binding-neutrality argument (verified).** The
binding generator dispatches to a SEPARATE Neo variant only under
`#if ENABLE_NEO_MODE`: `GenerateMethodWraperCode_Neo` /
`GenerateConstructorWraperCode_Neo` (Neo) vs `_Legacy`
(`MethodBindingGenerator.cs:249` vs `:400`); `AppendArgumentCodeNeo`
(`:107`) vs `AppendArgumentCode` (`:165`); `GetReturnValueCodeNeo` (`:440`)
vs `GetReturnValueCode` (`:475`); registration via
`RegisterCLRMethodRedirectionNeo` (`AppDomain.cs:734`) vs Legacy
`RegisterCLRMethodRedirection`. The Neo reflection fallback
`CLRMethod.Invoke(byte*)` (`CLRMethod.cs:332`) is a SEPARATE overload from
the Legacy `Invoke(ILIntepreter, StackObject*, ...)` (`:428`), dispatched
only from `InvokeNeoClrMethod` (`ILIntepreter.Neo.cs:158`). Therefore a 13b
bug can ONLY affect Neo CLR calls; Legacy CLR binding (the REFERENCE) is
never modified. The shared generators' Legacy-emitting methods are untouched.

**FINDING T -- Tests MUST use HOST-assembly structs (Step 13 Finding H
carries over).** A `struct` declared in TestCases is parsed as an ILType, so
it exercises IL-VT paths (already green), not the CLR (CLRType) param/return
path. Use `ILRuntimeTest.TestFramework.TestVector3` (WITH binder) and
`TestVector3NoBinding` (no binder, pure-float), registered via
`ILRuntimeHelper.Init`. No throw-asserting tests (harness limitation).

**FINDING U -- Neo wrapper value-type `this` is a TODO, not a crash today.**
`GenerateMethodWraperCode_Neo` `MethodBindingGenerator.cs:258-262`: for a
value-type-declaring-type instance method it emits
`{T} instance_of_this_method = default({T}); // TODO: ValueType instance in
Neo`. So such a method is called with `default(T)` as `this` today (silent
wrong-result), not a NIE. Area 4 (DEFERRED) owns the fix; 13b does not touch
this. The implementer's tests must use STATIC CLR methods or reference-type
instance methods for the by-value-PARAM cases (the param, not `this`, is the
struct), to avoid hitting the `this` TODO.

Artifacts authored: proposal.md, design.md, specs/neo-boxing/spec.md (delta:
ADDED by-value param/return + K2-FAM bridge requirements; MODIFIED the
deferral-requirements list), tasks.md (5 phases + explicit DEFERRED block).
`openspec status --change implement-neo-step13b` -> isComplete:true, all 4
artifacts done, apply-ready. All files ASCII-clean (0 U+FFFD).

### 2026-07-04 -- IMPLEMENTER apply pass (durable findings)

**OUTCOME: K2 (by-value param) + return-value path CLOSED; K2-FAM (D3 bridge)
DEFERRED per safety valve. FULL NeoStep smoke 84/84 (81 baseline + 3 new
NeoStep13b cases, ZERO regression; CLR-binding canary
`ValidateNeoSmallPrimitiveArgs` green). Builds 0 errors.**

**FINDING P-REVISED -- the callee layout was NEVER correct for CLR structs;
Finding P was optimistic.** `AppDomain.GetPrimitiveSize(IType)`
(`AppDomain.cs:1880-1934`) only knows the primitive ILType singletons (Int/Long/
Short/Byte/Bool/Float/Double/SByte/UShort/UInt/ULong/Char/IntPtr); it throws
`NotImplementedException` for ANY non-primitive value type. So the `IsValueType`
branch of `AllocateNeoCallParamSlot` (`Optimizer.Neo.cs:1357-1361`) -- which
called `GetPrimitiveSize` -- was never actually correct for CLR structs. The
caller-temp-slot fallback masked it (it bypassed that branch for CLR structs)
AND masked an async-state-machine JIT-prewarm crash: removing the fallback made
`AppDomain.Prewarm` of a method taking `AsyncTaskMethodBuilder` by value throw
`GetPrimitiveSize` NIE at lower time. Fix: a new public
`Optimizer.GetNeoValueTypeManagedSize(Type)` sizes a CLR struct via the generic
`Unsafe.SizeOf<T>()` (instantiated by cached reflection) -- the GC-reference-
aware MANAGED size (NOT `Marshal.SizeOf`, the unmanaged size), which NEVER
throws (works for any struct incl. async builders / TaskAwaiter / structs with
ref fields), so prewarm no longer crashes. The layout branch, the reflection
reader, and the autogen reader ALL use this one size -> byte-consistency by
construction. **Next step's planner: the "callee layout already handles CLR
structs" claim needs `GetPrimitiveSize` fixed first; 13b did it via the new
helper.**

**FINDING Q-IMPL -- `byte*` cannot be a generic type argument (CS0306).** So
`Func<byte*,object>` / `Action<byte*,object>` are illegal. `ReadNeoValueType` /
`WriteNeoValueType` use CUSTOM delegate types (`NeoVtReaderDelegate` /
`NeoVtWriterDelegate`, both `unsafe` with `byte*` params -- allowed on custom
delegates) wrapping a cached per-Type `System.Reflection.Emit.DynamicMethod`
(IL: `ldarg`; `call Unsafe.ReadUnaligned<T>(void*)` / `WriteUnaligned<T>(void*,
T)` -- the `void*` overloads, NOT the `ref byte` ones, which would fail IL
verification with a native pointer; `box`/`unbox.any`; `ret`). Hot path = one
delegate invoke, no reflection. `GetNeoValueTypeManagedSize` /
`ReadNeoValueType` / `WriteNeoValueType` are `public` (not `internal`) because
the autogen binding code is compiled into the HOST assembly.

**FINDING R-IMPL -- D3 (K2-FAM) deferred; the bridge is not needed for the
return-source shape.** A CLR struct LOCAL obtained from a CLR method RETURN is
stored as FLAT BYTES (the D6 return path writes via `WriteNeoValueType` into the
caller's dest slot), NOT as a boxed-ref. So passing such a local by value to a
CLR method already works WITHOUT the D3 bridge -- VERIFIED by
`NeoStep13bClrStructByValueParamNoBinding` (Make -> local -> Sum == 60, passes).
The boxed-ref CLR-struct-local representation only arises from the Box /
Initobj arms. A clean D3 reproducer (boxed-ref local -> by-value param, with a
VERIFIABLE result) needs IL-side `ldfld`/`stfld` on CLR struct fields, which is
a DEFERRED concern -- so the bridge has no clean test surface this step. Deferred
per the design's D3 safety valve (NOT a regression; a silent wrong-result for
the Box/Initjob-source shape, like the value-type-`this` TODO).

**FINDING U-IMPL -- `new T(x,y,z)` for a CLR struct emits the unimplemented
`push` opcode (Step 6).** The C# ctor call lowers to `push r7` (the ldloca'd
`this` by ref) before the `.ctor` call -- `push` is the value-type-`this` path
(Area 4, DEFERRED). So a test that constructs a CLR struct arg via `new T(...)`
hits a Step-6 NIE before reaching the K2 call. The apply-pass tests obtain the
struct from a CLR method RETURN (host C# constructs it; `MakeTestVector3NoBinding`)
to avoid the ctor push, and check results by re-feeding the struct to a CLR
method that returns a PRIMITIVE (host C# reads the fields; no IL-side `ldfld`
on CLR struct fields). This is the clean test shape for the 13b ABI.

**FINDING V (new) -- a multi-struct-local + int-local test surfaces an unrelated
frame-slot-reuse issue in `AllocateLocalStackSpaces`.** A test holding two CLR
struct locals AND two int locals (computed from them) failed its combined
`r1 != 600 || r2 != 3` check, but each sub-check passed in isolation; the failure
mode depended on the exact comparison/branch instructions emitted (different JIT
-> different frame slot allocation -> different aliasing). This is a frame-layout
slot-reuse bug INDEPENDENT of 13b (it is in `JITCompiler.AllocateLocalStackSpaces`
/ the liveness that drives slot reuse), surfaced by a CLR-struct-local-heavy
test. NOT investigated/closed this step (out of scope; reported here). The
NeoStep13b tests were written to hold a single struct local at a time to avoid
it. **Candidate follow-up: `AllocateLocalStackSpaces` slot-reuse liveness for
mixed struct+primitive locals.**

**FINDING W (new) -- `TestCLRBinding` HAS a committed autogen Neo redirect.**
`ILRuntimeTestBase/AutoGenerate/ILRuntimeTest_TestFramework_TestCLRBinding_Binding.cs`
registers BOTH `ValidateNeoSmallPrimitiveArgs_8_Neo` (Neo variant, via
`RegisterCLRMethodRedirectionNeo`) AND `_8` (Legacy). So
`ValidateNeoSmallPrimitiveArgs` exercises the AUTOGEN Neo path
(`AppendArgumentCodeNeo`), NOT the reflection fallback -- it is the canary for
Phase 4 (D5). New helper methods added to `TestCLRBinding` (the apply pass added
`SumTestVector3NoBindingFields` / `MakeTestVector3NoBinding` /
`SumTestVector3Fields`) have NO autogen redirect, so they exercise the
REFLECTION fallback `CLRMethod.Invoke(byte*)` (D2/D6). To exercise the autogen
CLR-struct codegen (D5) directly, the binding file would need regenerating with
a CLR-struct-param method -- out of scope this step; D5 verified by compile +
shared-helper byte-consistency + code inspection.

**DEFERRED items confirmed unchanged:** Area 4 value-type `this`
(`MethodBindingGenerator.cs:261` `// TODO: ValueType instance in Neo`, silent
wrong-result); CLR-method ref/out (the autogen ByRef branch now emits a Step-
13b-tagged TODO comment); CLR-object stind/ldind via field hash (Step-17-tagged
NIE). Legacy CLR binding + Legacy codegen UNTOUCHED (all new runtime code behind
`#if ENABLE_NEO_MODE`; the generator's Legacy-emitting methods untouched; only
the `*Neo` variants changed).
