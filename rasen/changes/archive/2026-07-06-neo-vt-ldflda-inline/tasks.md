## 1. Reproducer + JIT dump (confirm root cause on HEAD)

- [x] 1.1 Add a temporary reproducer probe to `TestCases/NeoStep17Test.cs`:
  `NeoStep17_LdfldaInline_Repro_StructToString` — an IL struct
  `S { int id; }` with `public override string ToString() { return "Named:" + id.ToString(); }`,
  invoked via `s.ToString()` on an in-frame local; assert the result equals
  `"Named:<id>"`. (DivideByZero-assertion pattern; `NeoStep17_` prefix.)
- [x] 1.2 Add a temporary diagnostic `Console.WriteLine` inside the runtime
  `case OpCodeREnum.Ldflda:` arm (`ILIntepreter.Neo.cs:827-857`) printing
  `objIdx`, `operandSlotOff`, `fieldPrimOff`, `dst`. Build CLI `Debug_Neo`
  (`--no-incremental`), rebuild TestCases (`--no-incremental`), run the probe
  with `-f net8.0`. CONFIRM `objIdx = <id value>` (e.g. `42`) for the
  constrained-boxed-`this` shape (flat bytes), AND `objIdx = -1` for a
  direct-`call` shape (`s.ReadIdViaAddress()`). This proves the flat-bytes-vs-
  Ref-Slot ambiguity (design §Context).
- [x] 1.3 Dump the JIT body of the override + the caller to CONFIRM: (a) the
  `ldflda` opcode is present post-lowering (`ldflda r, r0, 0x...`), (b) the
  type-spec pass `case Ldflda:` fires for the in-frame-VT source, (c) `Operand4`
  is currently untouched for `Ldflda` (no collision with the new marker).

## 2. JIT marker stamping (D1)

- [x] 2.1 In `JITCompiler.cs` `TypeSpecializeNeoOpcodes` `case OpCodeREnum.Ldflda:`
  (currently `:798-804`), when the source `Register2` is an in-frame IL value
  type (`GetRegisterType(registerTypes, op.Register2) is ILType srcIl &&
  srcIl.IsValueType && !srcIl.IsEnum` — the SAME condition that already seeds
  the dest type), ALSO stamp the marker flag bit on `op.Operand4`. Define a
  named const (e.g. `LDFLDA_INLINE_MARKER = 0x1`) near the other `Operand4`
  flag constants. Stamp `op.Operand4 |= LDFLDA_INLINE_MARKER` (OR, not assign,
  to be safe against future bits).
- [x] 2.2 Confirm via the JIT dump (task 1.3 re-run) that the marker IS stamped
  on the in-frame-VT `ldflda` and NOT stamped on a heap-IL `ldflda` / CLR-object
  `ldflda` (add a temporary heap-IL `ldflda` probe to confirm the absence).

## 3. Runtime arm marker branch (D2)

- [x] 3.1 In `ILIntepreter.Neo.cs` `case OpCodeREnum.Ldflda:` arm
  (`:827-857`), add the marker check BEFORE the existing `objIdx` dispatch.
  When `(ip->Operand4 & LDFLDA_INLINE_MARKER) != 0`: read
  `leadingInt = *(int*)(frameBase + operandSlotOff + 0)`; if `leadingInt == -1`
  resolve through the Ref Slot (existing frame-native logic — struct base = the
  Ref Slot's offset half); else the operand slot holds flat bytes (struct base =
  `operandSlotOff`). Produce `(-1, vtBase + fieldPrimOff)` in both sub-branches.
  When the marker is absent, keep the existing `objIdx == -1` / `>= 0` dispatch
  byte-identical.
- [x] 3.2 Remove the temporary diagnostic from task 1.2. Rebuild CLI
  `--no-incremental`. Re-run the task-1.1 reproducer; CONFIRM it now PASSES
  (`"Named:42"`). This is the load-bearing stash-toggle proof (FAIL on HEAD →
  PASS after fix).

## 4. Adversarial keeper probes (MANDATORY)

Add to `TestCases/NeoStep17Test.cs` (`NeoStep17_*` names; DivideByZero-assertion
pattern; `public static void` parameterless). Each probe MUST be designed to
FAIL on HEAD (where the gap exists) and PASS after the fix, OR be a byte-
identical regression guard.

- [x] 4.1 `NeoStep17_LdfldaInline_RefFieldRead` — `ref s.x` READ via a byref
  param (the `ldloca; ldflda` Ref-Slot shape; passes on HEAD; regression
  guard).
- [x] 4.2 `NeoStep17_LdfldaInline_RefFieldWrite` — `ref s.x` WRITE via a byref
  param; read back; assert the mutation landed in `s.x` (regression guard).
- [x] 4.3 `NeoStep17_LdfldaInline_StructToString` — the IL-struct `ToString()`
  override calling `id.ToString()` (the load-bearing flat-bytes-operand
  reproducer; FAIL on HEAD, PASS after fix). Keep this keeper; promote the
  task-1.1 temp reproducer.
- [x] 4.4 `NeoStep17_LdfldaInline_NestedField` — `ref outer.inner.x` via a
  chain; assert correct read/write. If the C# compiler emits `Ldfld_Value` for
  the chosen shape (Step-12b NIE), adapt the probe to an address-only access
  (no whole-VT load) — scope OUT the `Ldfld_Value` case.
- [x] 4.5 `NeoStep17_LdfldaInline_RefTypeField` — `ref s.objField` (the ref-
  region sub-case); set + read back via the address; assert identity.
- [x] 4.6 `NeoStep17_LdfldaInline_RegisterReuseEscape` — an `ldflda`-produced
  byref whose dest register is reused by an intervening op, then the byref is
  read; assert no stale value (the Step-17-B1 silent-corruption class).
- [x] 4.7 `NeoStep17_LdfldaInline_HeapIlRegression` — `ldflda` on a heap IL
  instance field; assert byte-identical behavior (regression guard).
- [x] 4.8 `NeoStep17_LdfldaInline_ClrObjectRegression` — `ldflda` on a CLR
  object field (if reachable without the field-hash stind/ldind deferral);
  assert byte-identical behavior (regression guard). Scope OUT if it hits the
  Step-17 CLR-field-hash NIE.

## 5. Verification + regression gate

- [x] 5.1 Build CLI `Debug_Neo` (`--no-incremental`) + TestCases `Debug`
  (`--no-incremental`); confirm DLL mtimes > source mtimes (stale-DLL gotcha).
- [x] 5.2 Run full `NeoStep` smoke (`-f net8.0`, filter `NeoStep`); CONFIRM
  the baseline holds (146/146 → 146+N/N, all green; no regression).
- [x] 5.3 Run the new probes individually (CLI filter is a single `Contains`
  substring — run each by name, NOT `A|B|C`); CONFIRM each passes.
- [x] 5.4 Legacy-neutral stash-toggle: `git stash` the runtime + JIT changes,
  rebuild CLI `Debug_Neo`, confirm the new probes FAIL on HEAD (the
  load-bearing ones) / the regression guards still pass; `git stash pop`.
  Confirm plain `Debug` CLI builds clean (all changes Neo-only / in
  `#if ENABLE_NEO_MODE`-gated files).
- [x] 5.5 Confirm no shared-pass (FCP/BCP/copy-prop/RegisterCleanup) change was
  needed (the marker is on standalone `Operand4`, not a union field); if a
  shared-pass guard was added, confirm it is `#if ENABLE_NEO_MODE`-gated and
  Legacy-neutral.

## 6. Spec + doc sync (at ship, NOT apply)

- [ ] 6.1 Append the apply-phase findings (the dump-confirmed root cause, the
  marker bit chosen, any deviation from design D2-refined) to
  `openspec/changes/neo-vt-ldflda-inline/design.md` "Apply-phase findings"
  section (mirror the VT-THIS-ADDR / step17-completion pattern).
- [ ] 6.2 At archive, merge the `neo-byref` spec delta into
  `openspec/specs/neo-byref/spec.md`; update
  `.trae/documents/neo-deferred-items.md` F-6 entry → RESOLVED; update
  `openspec/changes/neo-completion-portfolio/planning-context.md` with the
  closure.
