# Planning Context — implement-neo-step13

Seeds the planner. Read FIRST, then research only what is missing. Append
durable findings to section 8 after propose.

## 1. User intent (verbatim)
> "继续完成12b，13，14，15，16的任务。合理规划，每阶段完成后再继续auto-decompose下一阶段，每阶段完成都要提交push。直到任务完成。"

This run = **Step 13 only** (Box/Unbox complete). Steps 14-16 follow as separate
runs. Step 13 is committed AND pushed after its review is clean (user
pre-authorized commit+push per phase).

Prior state (committed + pushed): Step 11 (interface dispatch), Step 12 (in-frame
value types + inline field access), Step 12b (Move_Vt + LowerMove). HEAD=`6a8d1d2c`.
NeoStep smoke baseline = 41/41.

## 2. Decompose decision (LEAD, already taken)
**SKIP decompose.** The user frames each STEP as one phase (one auto-decompose run
= one commit+push). Step 13's 5 content areas share core files (CLRBinding/,
CLRMethod, ILIntepreter.Neo, Optimizer.Neo) and have internal dependencies, so
sub-children would be serial with no parallelism benefit. ONE change. The planner
MUST structure `tasks.md` in coherent phases, and MUST explicitly flag (in
design.md + proposal.md) any area that is too large/risky to land in one pass and
should be deferred to a follow-up with an accepted-known note. Pipeline: propose →
apply → verify → review-loop → ship → archive → (LEAD commits + pushes).

## 3. Step 13 scope (from `.trae/documents/neo-implementation-steps.md` §"Step 13")
Goal: complete all Boxing/Unboxing scenarios, including CLR value types.
Content (5 areas):
1. **IL value-type Box/Unbox** — Step 5 did the basics; here complete the
   interaction with the in-frame (flat-bytes) value-type model from Step 12.
2. **CLR value-type — two paths:**
   - WITH `ValueTypeBinder`: frame flat bytes ↔ CLR object memcpy.
   - WITHOUT `ValueTypeBinder`: keep boxed in mStack; method calls use
     `Unsafe.Unbox<T>` in-place.
3. **`constrained.` callvirt** compile-time specialization.
4. **Binding codegen overhaul:** `Unsafe.Unbox<T>` + direct-call mode (eliminate
   `WriteBackInstance`).
5. **CLRMethod param region — unified Neo slot layout:**
   - CLRMethod's callee param region must NOT define a separate ABI; reuse
     `AllocateSlotForType` / `StackSlotInfo` (the same primitive/ref slot rules).
   - CLRBinding generated code + `CLRMethod.Invoke(byte*)` must read params via
     non-generic read helpers (e.g. `ReadNeoInt16` / `ReadNeoBoolean`) by actual
     slot width, not hand-rolled width advancement.
   - REMOVE the temporary fallback in `Optimizer.Neo.cs` where CLR non-primitive
     struct params reuse the caller temp slot.
   - Generate a stable callee param layout for CLR value-type params: with Binder
     → flat bytes; without Binder → boxed / `Unsafe.Unbox<T>` convention.
   - Cover CLR struct by-value params, return values, instance-method `this`,
     generic CLR struct params.

Dependency: Step 5, Step 12 (in-frame value types).

Validation: IL struct box → object var → unbox back; `foreach(List<int>)` with no
per-iteration GC alloc (no-Binder path); ValueTypeBinder struct (e.g. Vector3)
box/unbox; `constrained.` — `T.ToString()` where T is struct; CLR struct param
call (`TaskAwaiter.GetResult()` / custom CLR struct by-value method) without the
caller-temp-slot fallback.

## 4. Foundation (build on this — do NOT re-derive)
- **Step 5** did basic IL Box/Unbox. Read the existing Box/Unbox code paths
  (grep `Box`, `Unbox`, `box`, `unbox` in `ILIntepreter.Neo.cs` and the
  `OpCodeREnum` box/unbox opcodes) to see what exists and what Step 13 completes.
- **Step 12** in-frame VT model: a VT local is `slot.Offset..+TotalPrimitiveSize`
  bytes + `mStack[frameRefBase+slot.RefOffset..+TotalReferenceCount]` ref slots —
  IDENTICAL in shape to an `ILTypeInstance` (`byte[] Primitives` +
  `AutoList ManagedObjects`). So box = copy frame-VT → new ILTypeInstance; unbox =
  copy ILTypeInstance → frame-VT. Step 12 design §1.1 explicitly flags this as
  what Step 13 Box/Unbox exploits.
- **Step 12b** Move_Vt: byte CopyBlock + ref-slot copy between two frame slots.
  Box/Unbox is the frame↔heap analogue — reuse the same primitive-copy +
  ref-copy logic (the authoritative helpers are `CopyFrameToIL` /
  `CopyILToFrame` per the Step 12b review).
- `ValueTypeBinder` infrastructure: grep `ValueTypeBinder` (it's a Legacy concept
  too — confirm the Neo path's use). CLR value types with a binder can be
  memcpy'd to/from CLR objects.
- CLRBinding code generator: `Runtime/CLRBinding/` — the generated redirect code
  that reads args and calls CLR methods. Area 4-5 overhauls how this reads
  value-type args.

## 5. RESEARCH REQUIRED (planner — be thorough; this is the largest step)
- Current Box/Unbox Neo opcodes + arms: what exists (Step 5), what throws
  NotImplemented. Map exactly what each of the 5 areas must add/change.
- `constrained.` callvirt: where is it currently handled (grep `constrained`,
  `hasConstrained`)? Step 11 added `Callvirt_Interface` with a `hasConstrained`
  exclusion (Optimizer.Neo.cs); Step 10's generic Callvirt handles
  constrained.-.object. Step 13 specializes constrained.-on-value-type
  (T.ToString() where T:struct).
- CLRMethod param region: find `AllocateNeoCallParamSlot`, the caller-temp-slot
  fallback in `Optimizer.Neo.cs` (Step 13 area 5 says REMOVE it), and how
  `CLRMethod.Invoke(byte*)` + generated CLRBinding code read params today. This
  is the highest-regression-risk area (could break ALL CLR bindings) — scope it
  carefully.
- `WriteBackInstance` (area 4 says eliminate it): grep it; understand the current
  CLR-instance writeback flow.
- The 2 pre-existing bugs found in Step 12b (K1 FCP VT propagation; K2 Step 8
  VT-by-value param copy) — K2 is DIRECTLY relevant to Step 13 area 5 (CLR struct
  by-value param). Determine whether Step 13's param-layout work fixes K2 or
  overlaps it. Document the relationship.

## 6. Hard constraints (from CLAUDE.md — obey exactly)
- Branch `features/object-model-overhaul`; all new runtime code behind
  `#if ENABLE_NEO_MODE`. Do NOT touch Legacy or mix object models.
- **Build:** CLI `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` → 0 errors. TestCases `dotnet build TestCases/TestCases.csproj -c Debug` (NEVER Debug_Neo).
- **Run tests:** FULL NeoStep smoke (regression — CLR bindings + value types both pervasive): `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → all-green (was 41/41; your NeoStep13 cases add, NO existing case regresses — CLR binding tests are the canary). `-f net8.0`. `Debug_Neo` prints lots of JIT/optimizer output — normal.
- Tests = `public static` parameterless; optional `[ILRuntimeTest]`. Add `TestCases/NeoStep13Test.cs`, ASCII. Cover the validation targets above that are expressible (no throw-asserting tests — harness limitation; use value round-trip / DivideByZero-assertion patterns).
- Unimplemented-op NIE (Step-tagged) = TODO not bug, but your paths must not throw for implemented cases.
- Test >10s = infinite loop — kill and investigate.
- **REGRESSION CAUTION (HIGHEST of all steps so far):** areas 4-5 (binding codegen + CLRMethod param layout) touch the CLR binding generator and call ABI — a bug here breaks ALL CLR method calls in Neo, not just value types. The full NeoStep smoke (incl. CLR binding tests) is the gate. If an area is too risky/large to land safely in one pass, DEFER it (accepted-known) rather than break the build/smoke.
- **CJK write caveat:** Write corrupts ~0.5% CJK on large payloads. Author ASCII.

## 7. What the planner must produce (propose stage)
Invoke `openspec-propose` (Skill) OR author directly. Artifacts under `openspec/changes/implement-neo-step13/`:
- `proposal.md` — Why / What Changes / Impact. **Explicitly state which of the 5 areas are IN this pass and which (if any) are DEFERRED with rationale** (e.g. if area 4-5 binding overhaul is too risky to bundle, defer to a Step 13b follow-up and say so).
- `design.md` — concrete, code-grounded, per area: Box/Unbox mechanics (frame↔heap copy reusing CopyFrameToIL/CopyILToFrame + Move_Vt's pattern); CLR VT with/without ValueTypeBinder; constrained. callvirt specialization; binding codegen (Unsafe.Unbox, eliminate WriteBackInstance); CLRMethod unified param layout (reuse AllocateSlotForType/StackSlotInfo, ReadNeo* helpers, remove the caller-temp-slot fallback). Edge cases + non-goals (byref=Step 17, VT newobj=18, async=20). Flag the relationship to Step 12b K2.
- `specs/<capability>/spec.md` — ADDED requirements, fresh. Suggested capability `neo-boxing` (new) — or extend `neo-value-types`. Your call; keep consistent with proposal Impact.
- `tasks.md` — checkbox tasks in PHASES (one phase per area), sized so the implementer can land phase-by-phase and run the smoke after each. Clearly mark any deferred area.

Author != verifier: you ONLY propose.

## 8. Append-only findings log
(planner appends durable decisions/constraints discovered during propose here)

### 2026-07-04 -- PLANNER propose pass (durable findings)

**SCOPE DECISION (key LEAD input) -- land areas 1-3, DEFER areas 4-5 to a
Step 13b follow-up.** Areas 4 (binding codegen: Unsafe.Unbox + eliminate
WriteBackInstance) and 5 (CLRMethod unified Neo param layout + remove the
caller-temp-slot fallback) touch the CLR binding generator and the Neo call
ABI shared by EVERY CLR method invocation -- the single highest-regression
risk in the roadmap. Bundling them with the frame-local box/unbox mechanics
would make the diff unreviewable and risk a red smoke that is hard to bisect.
Areas 1-3 are frame-local / dispatch-local, independently shippable, and do
NOT touch the call ABI. So: IN = areas 1 (verify/coverage), 2 (CLR Box/Unbox/
Initobj with+without binder), 3 (constrained. value-type specialization);
DEFERRED = areas 4-5 (+ the K2 fix), as a separate change
`implement-neo-step13b` that will MODIFY `neo-value-types`.

**FINDING A -- IL Box/Unbox is ALREADY DONE (Step 5).** The Box arm
(ILIntepreter.Neo.cs:1608-1664) and Unbox/Unbox_Any arm (1856-1901) handle IL
enum, IL primitive, IL VT-with-refs (via `CopyFrameToIL`:1640 /
`CopyILToFrame`:1892), and ref-type-as-box no-op. The two copy helpers
(2124/2146) are the authoritative frame<->heap primitives (same pair Step 12b
Move_Vt mirrors for frame<->frame). The lowering (Optimizer.Neo.cs:430-445)
stamps DstOffset/SrcOffset/Operand3(dst ref)/Operand4(src ref). Area 1 is
verify + coverage only -- no runtime change expected.

**FINDING B -- The Neo path does NOT consume ValueTypeBinder AT ALL.**
`ILIntepreter.Neo.cs` has ZERO `ValueTypeBinder` references (grep). The binder
is wired only into Legacy StackObject* paths (StackObject.cs:125, RuntimeStack,
ILIntepreter.Register.cs). So area 2 is "wire the binder into the Neo Box/Unbox
arms for the first time", not "switch an existing Neo binder usage". New
Neo-shaped binder helpers (BoxFromFrame/AssignToFrame/ZeroFrame on byte*+mStack)
are needed, additive + `#if ENABLE_NEO_MODE`, Legacy binder methods untouched.

**FINDING C -- CLR Box/Unbox/Initobj arms throw NIE at exactly 3 sites.**
ILIntepreter.Neo.cs:1662 (Box else-branch), 1905 (Unbox else-branch), 1604
(Initobj CLR branch). These are the area-2 fill-in points. The no-binder path
can only handle pure-primitive CLR structs (no way to discover ref-field
layout without a binder); structs-with-refs-and-no-binder throw a Step-13b-
tagged NIE.

**FINDING D -- CLRMethod.Invoke(byte*) ALSO throws NIE for CLR struct params.**
CLRMethod.cs:364 (`throw new NotImplementedException("CLR value type reflection
fallback: Step 13")`). This is area-5 territory (deferred). Combined with the
optimizer caller-temp-slot fallback (Optimizer.Neo.cs:646-655), the CLR-struct-
by-value-param path is doubly stubbed today. Step 13 does NOT touch
CLRMethod.Invoke -- its tests exercise box/unbox/constrained only, which never
hit the call param-setup.

**FINDING E -- K2 relationship (the Step 12b bug).** K2 = VT-by-value CLR
param passing broken (call param-setup emits a plain Move that reads a
primitive field VALUE as an mStack index -> ArgumentOutOfRangeException at
ExecuteNeo Move arm ILIntepreter.Neo.cs:449). K2 lives in the call
param-setup (Optimizer.Neo.cs:637-660 caller-temp-slot fallback +
CopyNeoCallArguments ILIntepreter.Neo.cs:130) = AREA 5 territory. Therefore
Step 13 does NOT fix K2 (deferred to 13b). Step 13's tests must NOT pass a
CLR VT by value. When 13b lands area 5, K2 closes as a natural consequence.
See design.md D4. (Note: IL->IL VT-by-value params are a SEPARATE path via
NeoCallParamMap, reported working but subject to FCP mis-propagation Finding
K.1.)

**FINDING F -- constrained. handling already partly wired (Step 10/11).**
The JIT detects constrained (JITCompiler.cs:1709-1715 hasConstrained, stamps
op.Operand4=1 at 1765). The optimizer hasConstrained exclusion set
(Optimizer.Neo.cs:574-577) excludes Callvirt_IL/Callvirt_CLR/Callvirt_Interface.
Step 10's generic Callvirt handles constrained.-object; Step 13 adds the
value-type specialization: T-declares-method -> direct Call on in-frame this
addr; T-inherits-from-object -> box once then reference callvirt. The
Callvirt_Interface exclusion (Step 11) stays.

**DECISION -- capability.** New `neo-boxing` capability (NOT extending
neo-value-types). Boxing is a distinct concern from in-frame storage/field/
copy. When 13b lands area 5 and changes the call ABI, THAT change will MODIFY
neo-value-types; keeping box/unbox separate keeps the two changes cleanly
separable in the archive.

**REGRESSION NOTE -- full NeoStep smoke is the gate (was 41/41 after Step 12b).**
Areas 1-3 are frame/dispatch-local; the call ABI is UNCHANGED in this pass
(areas 4-5 deferred), so CLR binding tests should not move. The NeoStep10/11
dispatch tests are the direct canary for area 3 (constrained specialization
must not perturb ordinary virtual/interface dispatch).

Artifacts authored: proposal.md, design.md, specs/neo-boxing/spec.md,
tasks.md. `openspec status --change implement-neo-step13` -> isComplete:true,
all 4 artifacts done, apply-ready.

### 2026-07-04 -- IMPLEMENTER apply pass (durable findings)

**RESULT: Areas 1-2 LANDED and green; Area 3 DEFERRED (prerequisite blocker
outside Step 13 scope). Final full NeoStep smoke = 49/49 green (was 41/41;
+8 new Step 13 cases, ZERO regressions). CLI Debug_Neo + TestCases Debug both
0 errors.**

**MAJOR FINDING G -- The design D2 premise (CLR struct locals stored as flat
bytes in the frame) is WRONG for the current Neo representation.** The actual
representation, set by `JITCompiler.AllocateLocalStackSpaces` (JITCompiler.cs
:1362-1373, the CLR-VT `else` branch), is: a CLR value-type LOCAL is stored as
a **BOXED OBJECT REFERENCE** -- a 4-byte frame slot holding an mStack index,
`RefCount = 1`, `localIsRef = true`. There are NO flat bytes and NO per-ref-
field mStack slots for a CLR struct local. (Flat bytes for CLR structs exist
only for array elements / by-value params / IL-typed fields -- the Area-5
territory deferred to 13b.) Confirmed empirically: `box r1,r0,
TestCases.NeoStep13ClrVec3` executes (the type resolves to an ILType, see
Finding H), and a real CLR struct local (`TestVector3NoBinding`) hits the
Initobj NIE, not a GetPrimitiveSize NIE -- proving the slot is a 4-byte ref,
not a sized byte region.

**FINDING H -- "CLR struct" tests must use HOST-assembly structs, not
TestCases-declared structs.** A `struct` declared in the TestCases hotfix DLL
(e.g. `NeoStep13ClrVec3`) is parsed by ILRuntime as an **ILType** (IL value
type), NOT a CLRType -- it flows through the IL Box/Unbox arms (Step 5 path),
exercising nothing new in Area 2. To test the real CLR (CLRType) Box/Unbox/
Initobj arms, the struct must come from a non-hotfix referenced assembly. The
test harness already registers ValueTypeBinders for several such host structs
via `ILRuntimeHelper.Init` (helper.cs:32-40, called from TestSession.Load:65):
`ILRuntimeTest.TestFramework.TestVector3` (WITH binder), and
`TestVector3NoBinding` (same file, no binder, pure-float). Both are accessible
from TestCases. Step 13 tests use these.

**FINDING I -- Area 2 implementation (deviation from design D2, justified).**
Because CLR struct locals are boxed refs (Finding G), the Area 2 Box/Unbox/
Initobj arms operate on the boxed-object representation, NOT flat bytes:
- Initobj (CLR): `clrType.CreateDefaultInstance()` -> boxed default struct ->
  store mStack index at the dest ref slot. (Primitives/enums -> InitBlock 0 of
  the sized flat slot; reference-type CLR local -> null index -1.)
- Box (CLR struct): source slot holds a boxed-ref mStack index; produce an
  independent shallow copy via `CLRType.PerformMemberwiseClone` (snapshot
  semantics -- a later mutation of the source local does not affect the box,
  verified by `ReferenceEquals(o, o2) == false`). CLR primitives box via the
  existing `NeoBoxReturnValue` (flat bytes source); CLR enums via a new
  `NeoBoxPrimitiveByType` + `Enum.ToObject`.
- Unbox (CLR struct): `PerformMemberwiseClone` of the boxed source into the
  dest ref slot. CLR primitives/enums unbox into the dest flat-bytes slot via
  a new `NeoWritePrimitiveToFrame`.
The design's "structs-with-refs-and-no-binder throw a Step-13b NIE" does NOT
apply to LOCALS (the binder is irrelevant for the boxed-ref representation);
it applies only to the flat-bytes array/param representation (13b). So Step 13
handles structs-with-refs-no-binder for locals WITHOUT a NIE. The design's
ValueTypeBinder Neo helpers (BoxFromFrame/AssignToFrame/ZeroFrame, task 2.4)
are NOT needed this pass and were NOT added (would be dead code; they become
relevant when 13b introduces flat-bytes CLR-struct representation). Legacy
binder methods untouched. This is an additive, #if ENABLE_NEO_MODE change
replacing 3 NIE throws in ILIntepreter.Neo.cs (Initobj/Box/Unbox CLR branches).

**FINDING J -- CLR struct FIELD access (Ldfld/Stfld on a CLR struct local) is
a SEPARATE unimplemented concern (Step 6 NIE), NOT in Step 13's scope.**
Reading a CLR struct field (`v.x`) compiles to a generic `Ldfld` that ExecuteNeo
does not implement ("Neo: opcode Ldfld not yet implemented (Step 6)"). So Step
13's CLR box/unbox tests assert only the box mechanics (non-null box, copy
independence via ReferenceEquals) and NOT field-value round-trips. Field
access on boxed CLR structs is left for a later step. Documented in test
comments. (IL value-type field access works via the Step 12 _Inline path; CLR
struct field access does not have an equivalent.)

**FINDING K -- Area 3 (constrained. VT specialization) is BLOCKED on
prerequisites outside Step 13; DEFERRED.** Three blockers found:
1. `ldarga`/`ldarga_s` are UNIMPLEMENTED in ExecuteNeo (Step 6 NIE). A
   constrained callvirt on a value type loads the `this` address via ldarga
   (for a generic-method param) -- the byref/address model is owned by Step 17.
2. The `Constrained` opcode itself has NO ExecuteNeo runtime arm. It is
   re-appended AFTER the callvirt in the instruction stream
   (JITCompiler.cs:1766-1772), so it cannot inform the callvirt it prefixes --
   a structural issue requiring either a Constrained runtime arm (box-then-
   redispatch) or JIT-time lowering that bakes the box into the callvirt.
3. The `constrained.` box-once / direct-call lowering needs the value-type
   `this` address model (= Step 17).
No existing green NeoStep test exercises `constrained.` today (only 6
occurrences in the smoke, all from the attempted Step 13 test which is now
removed), so deferring Area 3 incurs ZERO regression risk. Area 3 will land
with Step 17 (byref) or a dedicated follow-up. Design D3's case analysis
remains the blueprint (not deleted, deferred). No Area 3 code was committed
(exploratory ldarga no-op + alias-map additions were reverted to keep the
diff clean).

**FINDING L -- Cross-type unbox (IL enum box -> CLR int unbox) is an unsupported
edge case, out of scope.** `box NeoStep13IlEnum` produces an `ILEnumTypeInstance`;
`unbox.any System.Int32` of it (from `(int)o`) hits the CLR Unbox primitive
path with a non-primitive object. Step 13's enum test avoids this by not
cross-casting. A future step (Step 15 isinst/castclass, or a boxing-completeness
step) can handle enum<->underlying cross-casts.

**FILES CHANGED (runtime, all under ILIntepreter.Neo.cs's outer #if
ENABLE_NEO_MODE):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- Initobj CLR
  branch (was :1604-1606), Box CLR branch (was :1662), Unbox CLR branch (was
  :1905) implemented; two new helpers `NeoBoxPrimitiveByType` /
  `NeoWritePrimitiveToFrame` added near `NeoBoxReturnValue`.
- `TestCases/NeoStep13Test.cs` -- NEW, 8 green tests (4 IL box/unbox Area 1,
  2 CLR struct box/unbox Area 2 no-binder + with-binder, 2 IL primitive/enum).
- `openspec/changes/implement-neo-step13/{tasks,planning-context}.md` -- task
  checkboxes + this findings log.
- NOT touched: ExecuteR / USE_OLD_OBJ_MODEL / Legacy binder / CLRMethod.Invoke /
  Optimizer call-ABI / binding codegen (areas 4-5 confirmed untouched).

**FINAL SMOKE: `dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI
--no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> "Ran 49 tests, 0 failed".**
