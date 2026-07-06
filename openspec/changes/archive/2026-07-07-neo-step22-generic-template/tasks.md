## 1. Compile pipeline refactor (factor the T-dependent back-half)

- [x] 1.1 In `JITCompiler.cs`, factor the Neo-only back-half
  (`TypeSpecializeNeoOpcodes` + `AllocateLocalStackSpaces` +
  `LowerNeoOffsets` + the `NeoExecuteBody = CodeBody.Clone()` step) out of
  `Compile` into a callable `RunNeoBackHalf(ref CompiledFrame frame,
  List<OpCodeR> res, short locVarRegStart, int totalRegCnt, short
  neoCatchExRegFinal)` that operates on a register-index body. `Compile`
  calls it inline so its output is byte-identical (regression-neutral).
- [x] 1.2 Capture point for the register-index body AFTER `CleanupRegister`
  and BEFORE `TypeSpecializeNeoOpcodes` (the T-invariant template artifact)
  wired via the `templateCapture` hook in `Compile`.
- [x] 1.3 Build the CLI (`Debug_Neo`) and run the NeoStep smoke; 204/204
  (byte-identity-preserving) confirmed after the refactor.

## 2. PatchEntry struct + extractor

- [x] 2.1 Create `ILRuntime/Runtime/Intepreter/RegisterVM/GenericMethodTemplate.cs`
  (Neo-only, `#if ENABLE_NEO_MODE`) with the `PatchEntry` struct
  (`InstrIdx`, `Field : PatchField`, `Kind : PatchKind`, `GenericParamIdx`,
  `CecilToken`) + the `PatchField` (`Operand`/`Operand2`/`Operand4`) /
  `PatchKind` (`TypeToken`/`MethodToken`/`IsRefMoveFlag`) enums per design.md
  Decision 1. (`CecilToken` added for the Step-22 apply path; `GenericParamIdx`
  populated for the Step-23 serialize-without-Cecil contract.)
- [x] 2.2 Implement the `PatchEntry` extractor (`ExtractPatches`): scan the
  register-index template body + its `Symbols` (body-index -> Cecil
  instruction), record every T-identity type-token operand site
  (`Initobj`[IL-source]/`Box`/`Unbox`/`Unbox_Any`/`Isinst`/`Castclass`/`Newarr`/
  `Stobj`/`Ldobj`/`Constrained`). Auto-Initobj prefix (`Operand2==1`) is handled
  by the prefix rebuild, not recorded. REVIEW-FIX R1: the `Constrained` case was
  pulled OUT of the symbol-based group (BLOCKER-1 -- the symbol at the
  Constrained op's index does NOT point at the `constrained.` Cecil prefix; it is
  re-keyed to the trailing callvirt and CleanupRegister can scramble it further).
  The T TypeReference is now captured directly from the CIL body at capture time
  (`ConstrainedTypeTokens`) and consumed in body order. The trailing callvirt's
  T-qualified method token (`ConstrainedMethodTokens`, MAJOR-2) is captured the
  same way and recorded as `MethodToken` patches on both the Constrained op's and
  the trailing callvirt's `Operand2`. Non-constrained generic-method calls are
  T-invariant (verified) and need no patch.
- [x] 2.3 `PatchField` restricted to the standalone fields
  (`Operand`/`Operand2`/`Operand4`); `Register1/2/3` (aliased with the byte
  offsets post-lowering) explicitly excluded. Disjointness documented in the
  struct comments + verified empirically by the V1 body-equivalence matrix.

## 3. Template compile + per-definition cache

- [x] 3.1 `GenericMethodTemplate` holder (`TemplateBody`, `Patches`, `Definition`,
  front-half metadata, `Addr`, `InitObjPrefixRegisters`/`Length`, `VariableTypes`,
  `VarCnt`, lazily-built `RefBody`) + `StoreFromCapture` (builds the holder from a
  `TemplateCapture` + runs `ExtractPatches`).
- [x] 3.2 Per-definition cache: a Neo-only `genericMethodTemplate` field on
  `ILMethod` (the open generic definition), lazily populated by `StoreGenericTemplate`.
- [x] 3.3 Capture wired into `ILMethod.InitCodeBody`: the FIRST capture-eligible
  (ref/primitive typeArgs) concrete instantiation of each definition runs the
  per-occurrence JIT WITH the capture hook and stores the template on the
  definition. Subsequent instantiations CloneAndPatch. (Compiling the open
  definition directly was found to corrupt shared AppDomain caches and is NOT
  used; see planning-context Findings.)

## 4. CloneAndPatch + discrimination + integration

- [x] 4.1 `DoCloneAndPatch(template, instance, ...)`: clone `TemplateBody`; apply
  the `PatchEntry[]` table (re-resolve each Cecil token via the instance's
  `GetTypeTokenHashCode` for TypeToken, or `GetMethodTokenHash` for MethodToken);
  rebuild the auto-Initobj prefix for the concrete T (struct-T case); shift
  branch targets / `Leave`/`Leave_S` / SwitchTargets / addr by the prefix delta;
  re-run `RunNeoBackHalf` with the concrete generic arg. REVIEW-FIX R1:
  `Leave`/`Leave_S` were ADDED to the delta-shift (they are not in
  `Optimizer.IsBranching` -- EH control-flow resolved separately -- but their
  Operand is a resolved body index that shifts identically; the original shift
  missed them -> wrong EH target for struct-T, caught by the new TryCatch cell).
- [x] 4.2 Discrimination in `TryInstantiate`: all-ref AND no T-identity token ->
  share ONE cached ref body (built once, reused); else `CloneAndPatch`.
- [x] 4.3 PatchEntry-APPLY path exercised + asserted equivalent. NOTE: the
  design's Open Question 1 (apply-patches == re-run-TypeSpecialize) was based on
  a flawed premise -- TypeSpecializeNeoOpcodes does NOT re-derive T-identity
  tokens (it rewrites opcodes in place; tokens are set in Translate). They are
  COMPLEMENTARY, not equivalent: apply-patches fixes front-half tokens;
  re-run-TypeSpecialize fixes back-half opcode selection (Move->Move_Vt, typed
  arithmetic). CloneAndPatch does BOTH; the V1 self-check asserts the COMBINED
  CloneAndPatch output == per-occurrence (the meaningful equivalence). Documented
  in planning-context Findings.

## 5. V1 structural-equivalence test (load-bearing gate)

- [x] 5.1 Host-side `OpCodeR[]` comparator (`GenericMethodTemplateOps.BodiesEqual`,
  `#if DEBUG`-style: gated by being internal to ILRuntime) + `#if DEBUG`-style
  test-hooks on `GenericMethodTemplateOps` (`CompilePerOccurrenceNeoBody`,
  `CompileViaTemplateNeoBody`, `ForceBuildTemplate`) + the public host-side
  `NeoStep22SelfCheck` (in ILRuntime, `#if ENABLE_NEO_MODE`).
- [x] 5.2 `TestCases/NeoStep22GenericTemplateTest.cs` defines the matrix generic
  methods (ProbeBasic / MakeArray / StoreRef / LoadRef / BoxIt on
  NeoStep22GenericProbes). The V1 self-check matrix: T = int / long / object /
  NeoStep22RefClass(IL ref) / NeoStep22Struct(IL value) x 5 methods = 25 cells.
  Each cell asserts `CloneAndPatch(template,T) == Compile(MakeGenericMethod(T))`
  via `BodiesEqual`. RESULT: 25/25 pass. REVIEW-FIX R1 (MINOR-3): matrix EXPANDED
  to 11 methods x 5 T = 55 cells -- added `HashIt`/`EqualsIt` (constrained.callvirt
  T.M, BLOCKER-1 guard), `CompareThem` (T-qualified method token, MAJOR-2),
  `BranchIt` (struct-T + if/else), `SwitchIt` (struct-T + switch), `TryCatch`
  (struct-T + try/catch EH). RESULT: 55/55 pass (Constrained cells were RED
  before BLOCKER-1; TryCatch<Struct> was RED before the Leave delta-shift fix).
- [x] 5.3 V2 functional roundtrip: `NeoStep22Test.NeoStep22TemplateEquivalence`
  invokes the matrix generics via the template path (first call captures,
  subsequent CloneAndPatch) + asserts correct results. 1/1 pass. (Boxing a
  generic-param ref-T hits a pre-existing runtime Box-arm bug unrelated to Step
  22; the V2 covers the value-T box, the V1 self-check covers the ref-T body
  equivalence.)

## 6. Regression gates + ship prep

- [x] 6.1 NeoStep smoke 205/205 (was 204/204 + the new V2 test; ZERO regressions);
  NeoOptHardening 24/24; NeoStep20 9/9.
- [x] 6.2 Legacy-neutral proof: stash-toggle of the three Step-22 source files
  (JITCompiler.cs + ILMethod.cs + GenericMethodTemplate.cs), plain `Debug` +
  `useRegister=true` NeoStep-filter -> SAME 8 pre-existing failures with and
  without the change (the RunNeoBackHalf refactor is Neo-only + a behavior-
  preserving reorder; the template mechanism compiles out `#if ENABLE_NEO_MODE`).
- [x] 6.3 No probe/temp dump code in `JITCompiler.cs` or `TestCases/`; the
  Step-22 source is the only diff (modulo the openspec + test artifacts).
  REVIEW-FIX R1: all dump-gate diagnostics (ExtractPatches Constrained dump,
  runtime Constrained arm dump, V1 body dump) were REMOVED; tree pristine.
- [x] 6.4 `ship-log.md` written; no `neo-deferred-items.md` item surfaced beyond
  the documented design-flaw corrections (Initobj in front-half not TypeSpecialize;
  tokens not re-derived by the back-half) + the pre-existing Box-on-generic-ref-T
  runtime bug (out of scope).
