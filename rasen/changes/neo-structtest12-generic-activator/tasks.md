# neo-structtest12-generic-activator -- tasks

## Phase 1 -- re-audit (VERIFY at 1)
- [x] Build CLI (Debug_Neo --no-incremental -p:UseSharedCompilation=false) + TestCases (Debug). Both 0 errors.
- [x] Confirm StructTest12 FAILS at 1 under Neo (printed `1`, `Ran 1 tests, 1 failded`, exit 127).
- [x] Confirm StructTest12 PASSES on Legacy (plain Debug + useRegister=true, `Ran 1 tests, 0 failded`).
- [x] Dump the JIT for StructTest12Sub. Read Structs.cs:391-401. Roslyn lowering: `initobj ins; call.redirect Activator.CreateInstance[T](); ldloca ins; ldc 10; constrained MyStruct2; callvirt set_i`.
- [x] Pin Bug 1 (T resolution): runtime diagnostic in CreateInstanceNeo -> `method.GenericArguments[0] = MyStruct2` (DISPROVEN -- the `[ILTypeInstance]` dump label is the front-half template capture-T display, not the runtime value; NO JIT change needed).
- [x] Pin Bug 2 (2 sub-sites): (a) Activator returns heap ILTypeInstance index for a struct T -> ins.i = index; (b) constrained callvirt direct-call path has NO slot-0 write-back -> set_i's mutation discarded -> ins.i stays 0 after sub-site (a) fix.

## Phase 2 -- implement + verify
- [x] Site 1: CLRRedirections.CreateInstanceNeo -- add `t.IsValueType` branch: IL VT -> zero prim bytes + null ref slots; CLR VT -> CreateDefaultInstance + WriteNeoValueType. Reference-type T keeps the Instantiate + WriteNeoObjectResult path. (CLRRedirections.cs:709-)
- [x] Site 2: ILIntepreter.Neo.cs constrained-callvirt direct-call path -- add slot-0 prim write-back after ExecuteNeo (`Unsafe.CopyBlock(frameBase + thisByteOff, targetBase + thisSlotInfo.Offset, TotalPrimitiveSize)`). (ILIntepreter.Neo.cs:7589-)
- [x] StructTest12 name-filter PASS (printed `10`, `Ran 1 tests, 0 failded`, exit 0).
- [x] NeoStep smoke 417/0 (no regression on the broad constrained-callvirt surface).
- [x] FULL SMOKE delta 1 -> 0 (`Ran 951 tests, 0 failded, 20 ignored, 7 todos`, exit 0, NO crash).
- [x] Legacy-neutral: plain Debug build 0 errors (CreateInstanceNeo inside `#if ENABLE_NEO_MODE` block 506-1155; ILIntepreter.Neo.cs file-gated).
- [x] Stash-toggle airtight: revert both engine files -> HEAD rebuild -> StructTest12 FAIL (printed `1`, 1 failed) -> restore -> rebuild -> PASS (printed `10`, 0 failed).

## Artifacts
- [x] design.md, tasks.md, ship-log.md in rasen/changes/neo-structtest12-generic-activator/.
