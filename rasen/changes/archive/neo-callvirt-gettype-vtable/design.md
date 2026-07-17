# Design: neo-callvirt-gettype-vtable

## 4a -- inherited non-virtual CLR method on an IL instance (GetType)

### Evidence (Neo vs Legacy)
- JIT (`JITCompiler.InitializeCallvirtDispatch`, :3493): for a CLRMethod whose declaring type is `typeof(object)` (GetType/ToString/Equals/GetHashCode), `MayCallvirtTargetILObject` (:3549) returns true -> emits generic `OpCodeREnum.Callvirt` (NOT `Callvirt_CLR`).
- Runtime: generic `Callvirt` arm (ExecuteNeo :3652) -> `ResolveNeoGenericCallvirtTarget` (:1266) -> `this is ILTypeInstance` -> `ResolveNeoCallvirtILTarget` (:1204) -> `TryGetNeoVTableSlot(GetType)` fails (GetType is non-virtual; `IsNeoVTableCandidate` ILType.cs:703 rejects non-virtual CLR methods; `AddNeoBaseVirtualSlots` :568 only adds virtual base methods) -> THREW `MissingMethodException` (:1213).
- Legacy: the same callvirt hits the CLRMethod branch (Register.cs:3001) -> `cm.Redirection` (Register.cs:3034) -> the `ObjectGetType` redirect (CLRRedirections.cs:1250) registered on Legacy `RedirectMap` (AppDomain.cs:282) returns `Type.ReflectionType` for an ILTypeInstance.

### Fix (two parts)
1. **Dispatch fallback** (`ILIntepreter.Neo.cs`, `ResolveNeoCallvirtILTarget` :1210-1233): when `TryGetNeoVTableSlot` fails AND `declaredMethod is CLRMethod`, `return declaredMethod` instead of throwing. The caller (generic Callvirt arm) passes it to `InvokeNeoCallTarget` (:785) which routes a CLRMethod to `InvokeNeoClrMethod` (:1091). Virtual Object methods (ToString/Equals/GetHashCode) ARE VTable candidates and resolved above, so the fallback only catches the non-virtual inherited shape. `Callvirt_IL` always declares an ILMethod, so a failing ILMethod lookup still throws (genuine error).
2. **Neo redirect** (`CLRRedirections.ObjectGetTypeNeo` :764, registered `AppDomain.cs:288`): mirrors Legacy `ObjectGetType`. Reads `this` (param 0 at frameBase+0; the callvirt thisArgOffset is always 0 per `EncodeCallvirtDispatch(slot, 0)`) via `ReadNeoReference`; for ILTypeInstance/ILEnumTypeInstance returns `Type.ReflectionType`, else the CLR type. `InvokeNeoClrMethod` serves `clrMethod.RedirectionNeo` before the reflection fallback (:1093-1100). Registered on `RedirectMapNeo` (not `RedirectMap`) so Neo dispatch serves it; the redirect handles BOTH IL and CLR receivers (a CLR receiver's `instance.GetType()` is its real CLR type).

## 4b -- IL static field with inline initializer reads null (stale Step-7 .cctor suppression)

### Evidence
- `SimpleTest.StaticTest` line 99: `TestStatic.staticTest.Add("1", 1)` -- `staticTest` is `public static Dictionary<string,int> = new Dictionary<string,int>()` (inline initializer). Under Neo the field read as null -> callvirt "this is null".
- Root: `ILType.StaticInstance` getter (:186) and `InitializeMethods` (:2336) had `#if ENABLE_NEO_MODE` SUPPRESSING the `.cctor` invocation ("TODO Step 7: restore once Step 7 lands"). Step 7 landed long ago (we are past Step 25; Stsfld/Ldsfeld IL-static arms :4524/:4693 are complete). So the .cctor NEVER ran under Neo -> inline-initialized static fields stayed default (null).
- Diagnostic proof: a `C4DBG` print in the getter emitted ZERO lines -- the getter's Invoke was never reached because `InitializeMethods` (site 2) set `staticConstructorCalled=true` first (under Neo, without invoking), blocking the getter.

### Fix (two sites)
1. **Site 1 (StaticInstance getter :214-230)**: removed the `#if ENABLE_NEO_MODE` suppression so `appdomain.Invoke(staticConstructor, null, null)` runs (this is the LAZY trigger -- fires when StaticInstance is first accessed, AFTER InitializeMethods/Fields complete).
2. **Site 2 (InitializeMethods :2424-2444)**: wrapped the eager invocation block in `#if !ENABLE_NEO_MODE` so under Neo it is SKIPPED. Rationale: invoking eagerly here RE-ENTERS init before the type's `StaticInstance` byte[]/AutoList storage is materialized -- `TestCases.Fixed64..cctor`'s `stsfld Zero` NRE'd in the Stsfld VT arm (:4565) because `staticFieldTypes` was not yet populated -> the getter returned a null staticInstance. Deferring to the lazy getter (site 1) avoids the re-entrancy: by the time StaticInstance is accessed, init is complete. Legacy keeps the eager invocation (its StackObject[] storage is ready during InitializeMethods).

Both ILType.cs changes are Legacy-neutral by construction: Legacy always ran the .cctor at both sites; the change only LIFTS the Neo suppression (Neo now matches Legacy's behavior). Empirically confirmed: plain Debug+useRegister=true StaticTest 10/0, ReflectionTest 31/0.

### 4b is several distinct roots (this child fixes the static-field one)
The C4-4b "this is null" bucket is NOT one root:
- **Static-field null (THIS child)**: SimpleTest.StaticTest, StaticTest03 (progressed), + incidentally GenericMethodTest18, MyTest.UnitTest_Test1, UnitTest_10030 (their `this` was a static field). Fixed by the .cctor restore.
- `Type.GetType(string)` returning null (ReflectionTest04/19) -- distinct (string->Type resolution), follow-up.
- byref `out`-param write-back (StaticTest03 line 69 `ls.Add`) -- distinct (byref), follow-up.
- EnumTest30/32 (Equals on IL enum) -- IL enums have empty VTables (value-type skip at ILType.cs:504); the 4a fallback routes to CLR Object.Equals (reference equality, wrong for enums); needs an enum-aware Equals redirect, follow-up.

## Soundness / risk notes
- The GetType redirect reading `this` at frameBase+0 is correct: confirmed via `CLRMethod.Invoke` (:334-416) which reads the reference-type `this` at `curPrim=0`, and `ReadNeoCallThis`'s thisArgOffset=0.
- The .cctor lazy restore could surface latent .cctor bugs (a .cctor using an unimplemented Neo feature throws). Empirically: 0 regressions (140-post all in the 189-grounding set); NeoStep 380/0.
- The NeoAOT Cecil-free path seeds .cctor separately (`LoadNeoAssembly` AppDomain.cs:869-890) and is unaffected (it sets staticConstructorCalled via the getter `_ = t.StaticInstance`; site 2 skip is correct since site 1 now fires under Neo).
