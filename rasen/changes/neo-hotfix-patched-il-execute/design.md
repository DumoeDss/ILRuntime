# Design -- neo-hotfix-patched-il-execute (wave-2 cluster H)

## 1. The question (from the LEAD)
The 6 fresh-60 cluster-H Hotfix failures still fail at 46. The InvocationContext.Invoke
Neo branch (hybrid CanInvokeNeo) handles reflection/cross-binding invokes. Do these
Hotfix tests go through a DIFFERENT path (direct Execute(StackObject*) for the patched
body), or via InvocationContext? And is it a tractable engine fix or a deep gap?

## 2. AUDIT -- the exact path (grounded in a REAL run, not doc-judgment)

### 2.1 How a patched method is reached
HybridPatch (`ILRuntime/HybridPatch/AssemblyInjector.cs InjectMethod`) REWRITES the
host-CLR method body of every `[ILRuntimePatch]` type to redirect: the injected prologue
does `appdomain.BeginInvoke(ilMethod) -> InvocationContext`, pushes `this` + args, calls
`InvocationContext.Invoke()`. So patched IL methods ARE invoked via InvocationContext --
NOT a separate path.

The test-case methods (e.g. `HotfixBasicTestCases.Test03`) are HOST-CLR delegates
(`DelegateTestCase.RunTest -> cb(patched)`); they call `cls.Function(100)` on a host-CLR
`HotfixClass` whose body was injected to redirect via InvocationContext.

### 2.2 Why the patched body runs on the LEGACY eval-stack interpreter
`AssemblyPatch.InitializeMethodBody` builds the patched body as a RAW EVAL-STACK
`OpCode[]` (not `OpCodeR[]`, not Cecil `Instruction`), set via `method.SetBodyAndJumptables`.
The Cecil `MethodDefinition.Body` is EMPTY (only Variables; 0 Instructions).

=> `ILMethod.ShouldUseRegisterVM` returns FALSE (`def.HasBody && def.Body.Instructions.Count == 0`
at `ILMethod.cs:326`). => `InvocationContext.CanInvokeNeo` REJECTS it at the FIRST guard
`if (!useRegister) return false` (`InvocationContext.cs:488`). => `InvocationContext.Invoke`
falls to the Legacy arm: `if (useRegister) ExecuteR else Execute(StackObject*)` -> the
eval-stack interpreter (`InvocationContext.cs:471-474`). Confirmed by the real stack traces:
all 6 fail inside `ILIntepreter.Execute(StackObject*)` (ILIntepreter.cs:1317 Add-NIE,
:2260 CLRMethod.Invoke), NOT `ILIntepreter.Neo.cs`.

So: it IS via InvocationContext, but the Neo branch is correctly REJECTED (the body is raw
eval-stack Op[], which ExecuteNeo cannot consume -- it needs OpCodeR[]).

### 2.3 Why it fails ONLY under Neo (Legacy passes 14/14 -- verified)
The eval-stack interpreter's Ldfld/Stfld for an IL field reach `ILTypeInstance.PushToStack`
(read) / `AssignFromStack` (write). Under `ENABLE_NEO_MODE` these were **deliberate empty
no-ops** (`ILTypeInstance.cs` PushToStack:877, AssignFromStack:1084, CopyToRegister:881,
CopyValueTypeToStack:969, InitializeField:1080 -- ALL empty `{}`), and `Fields => null`
(:344). Neo uses `byte[] Primitives + AutoList ManagedObjects` and ExecuteNeo reads/writes
those via direct byte offsets; the legacy-interpreter bridge was never wired.

Consequence: patched-added fields stored on the `ILRuntimeExtraFieldObject` (an ILTypeInstance)
were read as no-ops (stale eval-stack slot) / written as no-ops (lost). This is the
"two object models cannot be mixed" principle (CLAUDE.md) made concrete.

This is NOT a pre-existing eval-stack bug: Legacy passes 14/14 (run confirmed). The eval-stack
code is identical (not Neo-gated); only the ILTypeInstance storage differs.

## 3. The FIX (Neo-gated, Legacy-neutral, low regression risk)

The patched-IL eval-stack path is the ONLY Neo-mode consumer of these stubs (normal Neo
methods run via ExecuteNeo which never calls them). Implementing them can ONLY turn current
silent-wrong no-ops into correct results -- zero regression risk for the normal Neo path
(verified: NeoStep 398/0).

Implemented `PushToStack` and `AssignFromStack(fieldIdx)` for Neo, mirroring the PROVEN Neo
indexer get/set arms (F-4 path #4, `ILTypeInstance.cs:456-584`):

- **PushToStack** (read field -> eval-stack slot): resolve `ILTypeFieldOffset off =
  type.GetFieldOffset(fieldIdx)` + `IType ft = type.GetField(fieldIdx)`; primitive ->
  `ReadNeoPrimitive(fields, off.PrimitiveOffset, ft, ...)`; reference -> `managedObjs[off.
  ReferenceOffset]`; then `ILIntepreter.PushObject(esp, managedStack, obj)` (writes at *esp,
  return discarded -- PushToStack fills one fixed slot). IL-value-type field -> tagged NIE
  (deferred). Out-of-range fieldIdx -> CLR-inherited adaptor branch (mirrors Legacy else).

- **AssignFromStack** (write field <- eval-stack slot): `StackObject.ToObject(esp, ...)`,
  align to `ft.TypeForCLR` (Convert.ChangeType, +Convert.ToBoolean for the int->bool case
  ChangeType rejects -- the eval stack stores `true` as Integer), then `WriteNeoPrimitive`
  / `WriteNeoPrimitiveDefault`. Same out-of-range / IL-VT / adaptor handling.

Untouched: `CopyToRegister`, `CopyValueTypeToStack`, `InitializeField` (left empty -- not
reached by the 6 cluster-H shapes).

## 4. VERDICT per test

| Test | Verdict | Evidence |
|---|---|---|
| HotfixBasicTestCases.Test03 | FIXED | reads patched-added int field (IntFieldAdded) via PushToStack |
| HotfixBasicTestCases.Test07 | FIXED | nested-lambda closure field reads via PushToStack/AssignFromStack |
| HotfixTestGenericTestCases.Test02 | FIXED | transformer/obj field reads (was NRE in CLRMethod.Invoke) |
| HotfixTestInheritanceTestCases.Test03 | FIXED | field read (was Add-NIE from stale ObjectType) |
| HotfixBasicTestCases.Test04 | DEEP (residual) | static-field accessor routing -- see 5.1 |
| HotfixBasicTestCases.Test05 | DEEP (residual) | array-initializer `.size N` blob -- see 5.2 |

## 5. The 2 DEEP residuals (honest, NOT forced)

### 5.1 Test04 -- patched-type STATIC added fields (fieldIdx out of range)
`InitStaticFields` writes static added fields (FloatFieldAdded, BoolFieldAdded). Diagnostic:
`fieldIdx=1 total=1 type=HotfixAOT.HotfixClass___Extra`. The `___Extra` ILType holds only the
INSTANCE added field (TotalFieldCount=1, identical on Legacy -- TotalFieldCount is NOT
Neo-gated). The static added fields are registered via `AssemblyInjector.RegisterTypeStaticField
Accessor` as a CLR `Get/SetStaticFieldCallback`, but the eval-stack Stsfld/Ldsfld ILType branch
(`ILIntepreter.cs:2555-2559 / 2586-2590`) calls `StaticInstance.AssignFromStack/PushToStack`
UNCONDITIONALLY and never consults the callback (only the CLR branch at :2564/:2596 does).
Legacy nonetheless routes these correctly (14/0) via a mechanism this audit could not pin down
without instrumenting the Legacy build; the routing is in SHARED (not Neo-gated) eval-stack
code, so a fix there would alter Legacy too -- out of scope for a Neo-gated child.

### 5.2 Test05 -- array initializer blob (Ldtoken on a `.size N` struct)
`TestArray` does `new int[]{333,666,555}` -> CIL `newarr; dup; ldtoken <PrivateImplDetails>;
call RuntimeHelpers.InitializeArray`. The eval-stack Ldtoken handler (`ILIntepreter.cs:2637`)
calls `t.StaticInstance.PushToStack` for the `<PrivateImplementationDetails>` `.size N` struct,
which has `TotalFieldCount=0` (TotalPrimitiveSize=0, TotalReferenceCount=0 -- the child-6
finding). So the blob is not in Primitives/ManagedObjects; fieldIdx is out of range ->
TypeLoadException. This is the child-6 `.size N` blob-storage issue resurfacing on the
eval-stack Ldtoken path (child-6 fixed it only for the ExecuteNeo ldtoken arm). Deep --
needs the Cecil `InitialValue` blob sourced for the eval-stack path too.

## 6. VERIFY (truth = full-smoke number)
- FULL Neo smoke: **46 -> 42 (-4)** (the 4 FIXED tests). 932 ran / 42 failed.
- NeoStep: **398/0** (no regression; the stubs are reached ONLY by the patched-IL path).
- Legacy Hotfix: **14/0** (Legacy-neutral; the change is entirely in `#else`/ENABLE_NEO_MODE).

## 7. DURABLE findings
1. **Patched-IL runs via the legacy eval-stack interpreter under Neo, by design.**
   `AssemblyPatch.InitializeMethodBody` emits raw eval-stack `OpCode[]`; the Cecil body is
   empty -> `ShouldUseRegisterVM==false` -> `CanInvokeNeo` rejects -> `Execute(StackObject*)`.
   This is NOT a missing Neo branch in InvocationContext; ExecuteNeo cannot consume `OpCode[]`.
2. **The Neo<->legacy-interpreter object-model bridge was 6 empty stubs + Fields=null.**
   PushToStack / AssignFromStack(fieldIdx) / AssignFromStack(all) / CopyToRegister /
   CopyValueTypeToStack / InitializeField were all `{}` under ENABLE_NEO_MODE. This child
   implements the first two (primitive + reference fields). The eval-stack path is the ONLY
   Neo consumer (normal Neo = ExecuteNeo, direct byte offsets).
3. **The bridge is the "models mixed" surface CLAUDE.md warns about**, but scoped to the
   narrow patched-IL path it is correct + low-risk (turns no-ops into working field access).
4. **The 2 residuals are NOT object-model-bridge issues** -- they are (a) eval-stack Stsfld/
   Ldsfld not consulting the ILType static-field callback (shared code), and (b) the `.size N`
   array-initializer blob (child-6 lineage). Both are candidate follow-up children.
5. **The recurring 12-for-12 lesson held again**: the framing "patched CIL can't run under
   ExecuteNeo" was DIRECTIONALLY right but IMPRECISE -- the bodies DO run (via eval-stack);
   the real gap was the empty object-model bridge, which IS tractable (4/6 fixed here).
