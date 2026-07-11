# Ship Log - implement-neo-step18

**Change:** `implement-neo-step18` - Neo Step 18: CLR newobj + IL-VT newobj attempt + Q-NEWOBJ.
**One-line:** Completes the remaining `newobj` paths after Step 8b. Delivers CLR-type newobj (reflection ctor + Neo Redirection), closes Q-NEWOBJ as non-reproducible, and defers IL value-type newobj behind a loud Step-18 NIE (blocked on VT field-access lowering consistency, -> [VT-THIS-ADDR]).

## Verdict: CLEAN SHIP

Review verdict (review-report.md): CLEAN SHIP - 0 Blocker / 0 Major / 2 Minor + 2 Info (both Minor are failure-mode-discoverability nits, pre-existing, not correctness regressions). IL-VT-newobj deferral adjudicated: blocker REAL, no simpler approach missed, NIE loud. Review-loop: 0 rounds.

## Verification evidence

- **Builds (0 errors):** CLI `Debug_Neo` 0 errors; TestCases `Debug` 0 errors.
- **FULL NeoStep smoke:** `Ran 91 tests, 0 failed, 0 ignored` (baseline was 84/84; +7 = the NeoStep18 cases). **0 regression.**
- **NeoStep18 (7/7 green):** TC1 inlined-VT-ctor-with-args; TC2 inlined-VT-with-ref-field; TC3 inlined-VT-default; TC4 Q-NEWOBJ (`newarr; new T(intArg)` + array round-trip); TC5 `new List<int>()` + Add + index; TC6 `new Dictionary<int,string>()` + count/index; TC7 `throw new InvalidOperationException` + try/catch.
- **Step 16 TC4:** restored to real `new NeoStep16Item(5)` ctor-with-arg (default-ctor+field-set workaround removed) - PASS.
- **Legacy 0-line diff:** `git diff HEAD -- ILIntepreter.Register.cs` = 0 lines (untouched by this change).
- **Side-benefit realized:** `throw new ClrException` + try/catch works (TC7 green) - the CLR newobj path unblocks CLR exception allocation from IL.

## Delivered scope (CLR newobj)

- Removed the blanket CLR NIE in the `ExecuteNeo` `Newobj` arm.
- Routes a CLR ctor to `InvokeNeoClrMethod(isNewobj: true)`:
  - **Without a Neo Redirection** -> reflection creation (`CLRMethod.Invoke(..., true)`); the returned object is stored into the caller's dest mStack ref slot, with its index written to the dest byte offset.
  - **With a Neo Redirection** -> the redirect path is unchanged (redirected CLR ctors are rewritten to `Call_Redirect` at JIT time and never reach the Newobj arm; the Newobj arm's `isNewobj=true` path is always the reflection path).
- Split the `if (isNewobj || retDstPtr == null) return;` early-return so the reflection-newobj path stores the result (the redirect path keeps its early-return; the void/non-newobj `retDstPtr == null` case still early-returns).

Enables `new List<int>()` / `new Dictionary<...>()` AND `throw new ClrException` + try/catch.

## Deferred

- **IL value-type newobj (Q-VT-NEWOBJ) -> [VT-THIS-ADDR]:** the VT ctor's `this`-relative `stfld` lowers inconsistently with the caller's reads on a newobj dest (ctor `this` = in-frame bytes, seeded as the declaring VT type; `addrAlias` only tracks ldloca/ldflda addresses, never a `this` param or a newobj dest, so caller/callee VT representation mismatch). A heap-`this` fallback is NOT simpler - the callee seeds `this` as the VT type and lays out an in-frame VT-sized `this` slot regardless of what the caller passes, so a heap index would be reinterpreted as a frame offset. The fix is the D2 change (track a VT `this` / newobj-dest as an in-frame address for ALL field access) which touches Step 12 + every VT instance method - too broad for this step.
  - **newobj-instruction path:** loud Step-18-tagged NIE (`"Neo Newobj IL value-type is not implemented (Step 18 D1 blocked on VT field-access lowering consistency; see D2)"`).
  - **Local form** (`VT x = new VT(args)`, the common C# idiom) compiles to `ldloca x; call ctor`, NOT a `newobj` instruction, so it bypasses the NIE and crashes opaquely (`NullReferenceException` at the first `stfld`). Pre-existing (Step 12 tests only use `default(T)` + direct field-set, never a user ctor); folded into [VT-THIS-ADDR].
- **Q-NEWOBJ:** NON-REPRODUCIBLE - closed (no fix). JIT dump shows the newobj dest, the newarr array temp, and the int arg each get a DISTINCT frame byte region AND a DISTINCT mStack ref slot (`AllocateLocalStackSpaces` gives every distinct register a distinct region/ref-slot). Matches the Q-STRUCT / Q-LONG precedent. Step 16 TC4 restored to the real ctor-with-arg form.
- **Delegate newobj** -> Step 19 (the `IsDelegate` NIE stays; real CLR delegates are blocked upstream at `Ldftn`, Step 6).
- **Generic-parameter VT newobj** -> with the generic-byref follow-up.
- **No-binder CLR-VT-with-refs newobj** -> inherits the Step-13b `NeoClrStructHasReferenceField` NIE guard.

## Accepted-known follow-ups (recorded for future steps)

- **Q-VT-NEWOBJ + Q-VT-NEWOBJ-local -> [VT-THIS-ADDR]:** the VT field-access lowering consistency fix (track a VT `this` / newobj-dest as an in-frame address for ALL field access). Touches Step 12 + every VT instance method. Newobj-instruction = loud NIE; local-form = pre-existing opaque crash. Tracked in `.trae/documents/neo-deferred-items.md`.
- **IsDelegate-NIE-dead** (trivial cosmetic; real delegates blocked at Ldftn).
- **Carryovers:** F-MAJ-1 (AllocateLocalStackSpaces), Step 17 constrained./stobj-ldobj/CLR-stind-ldind/CLR-refout, K2-FAM boxed-ref bridge, Q-STRUCT/Q-LONG (non-repro).

## Files changed

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` - `Newobj` arm (CLR newobj routing + early-return split; VT NIE). Neo-only (`#if ENABLE_NEO_MODE`).
- `TestCases/NeoStep18Test.cs` - NEW (7 cases, ASCII).
- `TestCases/NeoStep16Test.cs` - TC4 restored to real ctor-with-arg.
- `openspec/` - this change (proposal / design / tasks / specs / review-report / ship-log).
- `.trae/documents/neo-deferred-items.md` - Q-NEWOBJ resolved -> section 4; Q-VT-NEWOBJ / [VT-THIS-ADDR] recorded.

## Git note

Uncommitted on branch `features/object-model-overhaul`. LEAD stages commit ONLY the files above (ILIntepreter.Neo.cs, NeoStep18Test.cs, NeoStep16Test.cs, openspec, neo-deferred-items.md) - NOT the unrelated `.pdb` / `.gitignore` churn in the working tree.

## Do NOT run Rails `/ship`

`openspec-gstack-ship` is Rails/JS-centric and does not apply. No PR. This ship-log is the durable record; archiving (Stage 2) moves the change into `openspec/changes/archive/`.
