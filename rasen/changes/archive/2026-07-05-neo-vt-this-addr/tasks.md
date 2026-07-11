# Tasks

## 1. Baseline + reproducer evidence

- [x] 1.1 Build CLI (`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c
  Debug_Neo`) and TestCases (`dotnet build TestCases/TestCases.csproj -c Debug`);
  confirm 0 errors. Run the full `NeoStep` smoke; record the green count
  (baseline 91/91).
- [x] 1.2 Add a **VT-newobj reproducer** to a scratch test: a multi-field IL
  value type `S` with a ctor that sets `this.a = ...; this.b = ...;`, then
  `new S(args)` + field reads. Confirm the Step-18-tagged NIE fires on HEAD
  (the current deferred state). Dump the JIT body + `localInfos` for both the
  ctor and the caller (Debug_Neo prints JIT/optimizer output) to confirm: (a)
  the ctor's `this.field=` lowers to a MIX of `_Inline` and heap `Stfld_*`; (b)
  the caller's `ldfld` on the newobj dest is non-inline; (c) the newobj dest
  register is UNTYPED in the type-specialization pass. Record the dump.
- [x] 1.3 Add a **local-form reproducer**: `S x = new S(args);` (compiles to
  `ldloca x; call ctor`) + field read. Confirm the opaque `NullReferenceException`
  at the ctor's first `this.field=` on HEAD (the pre-existing failure).
- [x] 1.4 Confirm `AllocateLocalStackSpaces` (`JITCompiler.cs:1332-1344`) sizes
  the VT ctor's `ParamInfos[0]` as the in-frame value (`Size =
  TotalPrimitiveSize`, `RefCount = TotalReferenceCount`) and that
  `BuildInitialRegisterTypes` (`JITCompiler.cs:888-905`) seeds
  `registerTypes[0] = declaringType` for the ctor (so the ctor's `this` is
  already typed as the VT -- the callee side is already consistent). No edit;
  record the confirmation.

## 2. JIT: type the Newobj dest as an in-frame VT (D1 -- load-bearing)

- [x] 2.1 In the JIT type-specialization pass (the loop in `JITCompiler.cs`
  that calls `TryRewriteFieldAccessForInline` @~537), add a
  `case OpCodeREnum.Newobj:` that resolves the target method's declaring type
  and, when it is an IL value type (not enum, not primitive), calls
  `SetRegisterType(registerTypes, op.Register1, ilVtType)`. Mirror the existing
  `Ldloca` (@741-747) and `Ldflda` (@761-767) rules. Gate `#if ENABLE_NEO_MODE`
  if the surrounding code is shared; otherwise Neo-only by file.
- [x] 2.2 Re-dump the JIT body for the 1.2 reproducer's CALLER. Confirm the
  newobj dest register is now typed as the VT, and the caller's `ldfld` on the
  result is now rewritten to `_Inline` (resolving against the dest register's
  frame byte/ref region). If any caller `ldfld` is STILL non-inline, identify
  why (e.g. the dest type was overwritten by a later pass) and fix at the
  minimal site.
- [x] 2.3 Run the 1.2 reproducer (still expects the NIE from the runtime arm
  until Phase 4). Confirm the CALLER side is now consistent (inline reads); the
  ctor side is already consistent per 1.4.

## 3. addrAlias extension: VT `this` param root (D3)

- [x] 3.1 In `Optimizer.Neo.cs` addrAlias producer pass (@25-81): when the
  method being compiled `HasThis` and `declaringType.IsValueType` (not enum),
  seed `addrAlias[0] = new NeoAddressAlias { Reg = 0, Offset = 0 }` (param
  slot 0 is a pre-existing in-frame address root). Mirror the `ldloca` rule.
- [x] 3.2 In the `liveAliasMap` maintenance block (@1259-1301): seed
  `liveAliasMap[0]` the same way at method entry (so the per-instruction
  snapshot recognizes param slot 0 as a live alias throughout the ctor body).
  Ensure the existing kill logic (any opcode that redefines register 0) does
  NOT fire for the param slot (params are never redefined by the JIT -- confirm
  via the dump).
- [x] 3.3 Re-dump the ctor body for the 1.2 reproducer. Confirm the ctor's
  `this.field=` `_Inline` opcodes now resolve `this` (Register1/Register2 =
  slot 0) via the alias root to `localInfos[0].Offset + fieldOffset` (the
  callee frame's slot-0 region), with no remaining heap `Stfld_*` arms for the
  same field access.

## 4. Runtime: Newobj IL-VT branch (D2)

- [x] 4.1 In `ILIntepreter.Neo.cs` `Newobj` arm (@1654-1701): replace the
  Step-18 NIE with the construction. Branch on `ilNewobjType.IsValueType &&
  !IsPrimitive && !IsEnum`. Zero-init the dest region:
  `Unsafe.InitBlock(frameBase + destByteOff, 0, ilNewobjType.TotalPrimitiveSize)`
  and null each of the dest's `TotalReferenceCount` ref slots
  (`mStack[frameRefBase + destRefOff + i] = null`).
- [x] 4.2 Seed the ctor's `this` (callee param slot 0). Use the ctor's
  `CompiledFrame.ParamInfos[0]` offsets (`thisPrimOff`, `thisRefOff`). Write
  the frame-native Ref Slot into the callee's slot-0 primitive bytes:
  `*(int*)(targetBase + thisPrimOff + 0) = -1; *(int*)(targetBase + thisPrimOff
  + 4) = destFrameByteOff;`. For a VT with reference fields, ALSO seed the
  callee's slot-0 ref slots (`mStack[calleeFrameRefBase + thisRefOff + i]`)
  to point at the caller's dest ref slots (heap Ref Slots
  `(-1, destRefAbsFrameIdx + i)`), OR -- if the apply dump shows the `_Inline`
  ref arm reads slot-0 ref slots directly -- use the copy-back simplification
  (D3): byte-copy the dest region into the callee `this` slot pre-call, copy
  back post-call. Pick the form the dump confirms; record the choice.
- [x] 4.3 Copy the remaining ctor args via `CopyNeoCallArguments(ref map, ...)`
  (the lowering already built the map skipping slot 0 for Newobj). Invoke the
  ctor: `InvokeNeoCallTarget(ctorMethod, isNewobj:true, targetBase, mStack,
  retDstPtr: null, targetRetRefBase: destRefOff, out _)`. Do NOT push a fresh
  mStack `this` object; do NOT allocate a heap `ILTypeInstance`.
- [x] 4.4 Confirm the 1.2 reproducer now PASSES (multi-field ctor, fields read
  back correctly). Confirm the 1.3 local-form reproducer now PASSES.

## 5. Adversarial probe tests (MANDATORY -- a green smoke does NOT prove the gate)

- [x] 5.1 Add `TestCases/NeoStep19Test.cs` (ASCII; `public static void`
  parameterless; filtered by `NeoStep`). Cover AT MINIMUM:
  - **TC1** VT ctor mutating multiple primitive fields via `this.field=`;
    caller reads all.
  - **TC2** newobj result whose fields are read AFTER intervening heap writes
    (a `new` of a ref type, a `newarr`, a static call) that reuse eval-stack
    registers -- the Step 17 B1 silent-corruption class.
  - **TC3** nested VT: `Outer { Inner inner; int y; }`, `new Outer(...)` sets
    `this.inner.x`, caller reads `outer.inner.x`.
  - **TC4** VT with reference-type fields: `S { int a; string b; }`, ctor sets
    both, caller reads both (incl. the null case).
  - **TC5** VT returned from a method `S Make()` then field-read.
  - **TC6** the local form `S x = new S(args);` + field read (the common C#
    idiom; compiles to `ldloca; call ctor`).
  - **TC7** base-ctor chain: `S(int a) : base(a)` where the base ctor sets a
    field; verify the field is observable after `new S(a)`.
  - **TC8** default-ctor (parameterless) VT: `new S()` where the ctor sets
    defaults / is a no-op; verify zero-init + ctor-assigned fields.
- [x] 5.2 Assertion style: same as prior NeoStep tests (passing test returns;
  logic failure via a deliberate fault / mismatched assert). NO throw-asserting
  tests (harness limitation -- `throw new T()` needs newobj of an IL exception
  type, which is a separate follow-up).

## 6. Shared-pass guard + regression (Legacy-neutral)

- [x] 6.1 Probe FCP/BCP/copy-prop/RegisterCleanup for mis-propagation through a
  VT newobj dest: a `Move` whose source is a VT newobj dest, followed by a
  field read of the move dest after the source is mutated. If mis-propagated,
  gate the fix `#if ENABLE_NEO_MODE` at the minimal site (mirror the OPT-HARDEN
  `ldloca-kill` pattern); confirm plain-`Debug` compiles it out.
- [x] 6.2 Build CLI (Debug_Neo) + TestCases (Debug). Run the **full NeoStep
  smoke** (`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI
  --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep`). Gate: all previously-green
  cases stay green (91/91 baseline); the new NeoStep19 cases green. Kill +
  investigate any test >10s (infinite loop).
- [x] 6.3 For ANY shared-pass change (6.1), confirm Legacy-neutral: build plain
  `Debug` + `useRegister=true`, run the relevant filter, confirm the 518/519
  baseline holds.

## 7. Close-out

- [x] 7.1 Update `design.md` with the confirmed D2/D3 mechanism choice
  (frame-native Ref Slot vs copy-back), the actual edit sites, and any
  deviation from the propose-time hypothesis. Append durable findings to
  `openspec/changes/neo-completion-portfolio/planning-context.md` under
  `## Findings -- neo-vt-this-addr`.
- [x] 7.2 Mark Q-VT-NEWOBJ / [VT-THIS-ADDR] RESOLVED in
  `.trae/documents/neo-deferred-items.md` (move §3 entry to §4 Resolved).
  Update the `neo-value-types` / `neo-newobj` DEFERRED markers if any remain.
- [x] 7.3 Hand off to review/ship. Legacy (`ExecuteR`) untouched; all Neo code
  behind `#if ENABLE_NEO_MODE`.

## Deferred (NOT in this pass; recorded so they are not silently dropped)

- [ ] **Delegate `newobj`** (`new Action(foo)`) -- separate child
  `neo-step19-delegate` (`ldftn`/DelegateAdapter). The existing
  `NotImplementedException("Neo Newobj delegate is not implemented")` stays.
- [ ] **CLR value-type `newobj` with reference fields, no binder** (reflection
  path) -- Step 13b `NeoClrStructHasReferenceField` NIE guard stays.
- [ ] **Generic-parameter VT newobj** spanning IL/CLR -- with the generic-byref
  follow-up (`neo-step17-completion`).
