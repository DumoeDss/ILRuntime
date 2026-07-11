## 1. Stelem_I interpreter arm

- [x] 1.1 Read `ILIntepreter.Register.cs` Stelem_I arm (Legacy REFERENCE) for
  semantics; read the Neo `Stind_I` -> `Stind_I4` `goto` idiom and the Neo
  `Stelem_I4` arm (`ILIntepreter.Neo.cs:3008-3017`).
- [x] 1.2 Add `case OpCodeREnum.Stelem_I: goto case OpCodeREnum.Stelem_I4;` to
  the runtime array-store switch in `ILIntepreter.Neo.cs` (place it next to
  `Stelem_I4`, mirroring `Stind_I`). (D1 Option A.)
  - **DEVIATION (OQ2):** Option A `goto Stelem_I4` REJECTED -- the Stelem_I4
    arm's typed casts only handle int[]/uint[], so IntPtr[] hits the uint[]
    fallback and throws InvalidCastException. Switched to D1 Option B: a
    dedicated `Stelem_I` arm that dispatches on int[]/uint[]/IntPtr[]/
    UIntPtr[]. See design.md "Apply resolutions".
- [x] 1.3 Probe-verify a `nint[]` AND `UIntPtr[]` round-trip via a temp
  `Console.WriteLine` in the arm; confirm the 4-byte native-int store/load is
  correct on the current pointer model. (OQ2.) If it fails, switch to a
  dedicated `IntPtr[]`/`UIntPtr[]`-typed arm (D1 Option B).
  - **OQ2 RESOLVED:** 4-byte native-int confirmed via runtime debug (Stelem_I
    writes 100/-7/4660; Ldelem_I reads 100/-7/4660). Option B used (see 1.2).
    UIntPtr[] probe omitted (GetPrimitiveSize doesn't recognize UIntPtr --
    pre-existing unsupported primitive).

## 2. Generic-token + native Ldelem/Stelem JIT enumeration

- [x] 2.1 In `JITCompiler.cs` `Translate`, add `case Code.Ldelem:` to the
  Ldelem block (`:2098-2113`), rewriting it to `OpCodeREnum.Ldelem_Any` with
  the same 3-register `baseRegIdx`-decrement shape. (D2.)
  - **N/A:** `Code.Ldelem` does NOT EXIST in this Mono.Cecil fork (the generic
    form is `Code.Ldelem_Any` at opcode 0xa3, already enumerated). Only
    `Code.Ldelem_I` was missing. See design.md "Apply resolutions".
- [x] 2.2 Add `case Code.Stelem:` to the Stelem block (`:2226-2234`), rewriting
  to `OpCodeREnum.Stelem_Any`.
  - **N/A:** `Code.Stelem` does NOT EXIST in this fork (`Code.Stelem_Any` at
    0xa4 already enumerated).
- [x] 2.3 Add `case Code.Ldelem_I:` -> `Ldelem_I4` and `case Code.Ldelem_U8:`
  -> `Ldelem_I8` to the Ldelem block.
  - **DEVIATION:** `Code.Ldelem_U8` does NOT EXIST in this fork (not a real
    ECMA opcode). `Code.Ldelem_I` exists; added as a new case that keeps
    `op.Code = OpCodeREnum.Ldelem_I` (direct cast, no rewrite -- the runtime
    got a dedicated Ldelem_I arm instead). OQ1 resolved: no Ldelem_U8 to
    route.
- [x] 2.4 Dump-confirm via a temp `Console.WriteLine` in the `Ldelem_Any` /
  `Stelem_Any` runtime arms that a generic-method probe (`T Get<T>(T[] a, int
  i)`) reaches the runtime with the constructed element type. Probe
  `Code.Ldelem_U8` with BOTH a `ulong[]` and (if reachable) a `nuint[]`;
  confirm the routing (OQ1).
  - **OQ1 RESOLVED:** no Ldelem_U8 opcode exists. The generic-method probe
    (TC10, T=string) reaches Ldelem_Any/Stelem_Any (already wired pre-change;
    regression guard). A generic PRIMITIVE T via Ldelem_Any is a separate
    pre-existing gap (the Any arms box the element) -- TC10 uses T=string
    (reference type), the supported shape.

## 3. F-4 other-width Stind/Ldind CLR-array branches

- [x] 3.1 In `ILIntepreter.Neo.cs`, extend `Stind_I1`, `Stind_I2`, `Stind_I8`,
  `Stind_R4`, `Stind_R8` with an `else if (mStack[objIdx] is Array cArr)
  cArr.SetValue(v, off);` branch (between the `objIdx == -1` and
  `GetNeoILInstance` branches), mirroring `Stind_I4:3123`. (D3.)
- [x] 3.2 Extend `Ldind_I1`, `Ldind_U1`, `Ldind_I2`, `Ldind_U2`, `Ldind_U4`,
  `Ldind_I8`, `Ldind_R4`, `Ldind_R8` with the symmetric
  `else if (mStack[objIdx] is Array cArr) <typed-cast>(cArr.GetValue(off));`
  branch, mirroring `Ldind_I4:3198`.
- [x] 3.3 Extend `Stind_Ref` and `Ldind_Ref` with the array branch
  (`cArr.SetValue(mStack[vIdx], off)` / `mStack[dstIdx] = cArr.GetValue(off)`),
  mirroring the existing ref read/write pattern.
  - **NOTE:** the Stind_Ref/Ldind_Ref array branches are correct-by-
    construction but currently UNREACHABLE (the upstream Neo `ldelema` arm
    NIEs on a CLR ref-type array). Probe TC15 omitted; see design.md.
- [x] 3.4 Confirm `Stind_I`/`Ldind_I` need NO change (they `goto Stind_I4`/
  `Ldind_I4` and inherit the array branch).
  - **Confirmed:** `Stind_I` -> `goto Stind_I4` (inherits the I4 array branch,
    unchanged). `Ldind_I` -> `goto Ldind_I4` likewise.

## 4. Adversarial probes (MANDATORY) + regression

- [x] 4.1 In `TestCases/NeoStep16Test.cs`, add `NeoStep16_StelemI_NIntArray`
  (`nint[]` store/load; FAIL-on-HEAD stash-toggle) and
  `NeoStep16_StelemI_UIntPtrArray` (`UIntPtr[]`).
  - TC8 (IntPtr[]) PASS-after-fix; FAIL-on-HEAD (Stelem_I NIE). LOAD-BEARING.
  - TC9 (UIntPtr[]) OMITTED -- GetPrimitiveSize doesn't recognize UIntPtr
    (pre-existing unsupported primitive).
- [x] 4.2 Add `NeoStep16_GenericTokenLdelemStelem` -- a generic method
  `T Get<T>(T[],int)` / `void Set<T>(T[],int,T)` invoked with `T=int` and
  `T=long` to exercise `Code.Ldelem`/`Code.Stelem` (FAIL-on-HEAD JIT NIE).
  - TC10 uses T=string (the supported Ldelem_Any/Stelem_Any reference-type
    shape). Generic-PRIMITIVE-T is a pre-existing Any-arm gap. PASS throughout
    (regression guard; the JIT cases for Ldelem_Any/Stelem_Any pre-existed).
- [x] 4.3 Add `NeoStep16_LdelemU8_UlongArray` -- `ulong[]` via `Ldelem_U8`
  (FAIL-on-HEAD JIT NIE). (Also try `nuint[]` if OQ1 needs it.)
  - TC11 exercises ulong[] via Stelem_I8/Ldelem_I8 (no Ldelem_U8 opcode
    exists). PASS throughout (I8-width regression guard).
- [x] 4.4 Add `NeoStep16_F4_StindLdindClrArray_I8/R4/R8/Ref` -- construct a
  `long[]`/`float[]`/`double[]`/`object[]`, take a `ref`/`fixed` address
  (`ldelema`), and store/load via `stind`/`ldind` of the matching width; each
  FAIL-on-HEAD with `InvalidCastException`, PASS-after. (One probe per width,
  or one probe covering all four if the C# shape allows.)
  - TC12 (I8), TC13 (R4), TC14 (R8): FAIL-on-HEAD -> PASS-after-fix.
    LOAD-BEARING. TC15 (Ref) OMITTED -- upstream ldelema ref-type NIE
    (pre-existing Step-17 gap).
- [x] 4.5 Regression -- confirm the existing rank-1 paths still work:
  `int[]` Stelem_I4/Ldelem_I4, `float[]` Stelem_R4/Ldelem_R4, `object[]`
  Stelem_Ref/Ldelem_Ref, IL-struct[] Stelem_Any/Ldelem_Any (these are existing
  NeoStep16 cases; verify they stay green).
  - TC16 (F-4 I4 CLR-array regression guard) PASS throughout. Existing TC1-TC7
    stay green (full NeoStep 161/161).

## 5. Build, smoke, Legacy-neutral

- [x] 5.1 Build CLI `Debug_Neo` `--no-incremental` after any host-type change
  (gotcha); build TestCases plain `Debug` (NEVER `Debug_Neo`).
- [x] 5.2 Run full `NeoStep` smoke with `-f net8.0`, filter `NeoStep` (and the
  separate probe prefix if used). Target: 154 baseline + new probes, all green.
  Kill any test >10s (infinite loop).
  - **NeoStep 161/161 green** (154 baseline + 7 new keeper probes).
- [x] 5.3 Legacy-neutral: build plain `Debug` + `useRegister=true`, run the
  `NeoStep16` filter; confirm the 9 pre-existing Legacy NeoStep failures are
  unchanged (stash-toggle the 4 new JIT cases for the cleanest signal).
  - **Legacy NeoStep: 8 failures identical with/without the change**
    (stash-toggle confirmed byte-identical). The shared Optimizer.Utils.cs +
    JITCompiler.cs additions only handle a previously-NIE'd opcode; Legacy's
    own runtime lacks the Stelem_I / Stind-array arms so its pre-existing
    failures are unchanged.
- [x] 5.4 Stash-toggle the WHOLE change: confirm each new probe FAILS on HEAD
  and PASSES with the change (proves load-bearing -- Step 17 B1 / OPT-HARDEN K1
  lesson).
  - TC8/TC12/TC13/TC14 FAIL-on-HEAD -> PASS-after (load-bearing).
    TC10/TC11/TC16 PASS throughout (regression guards, as designed).

## 6. Docs + spec archive

- [x] 6.1 Append durable findings to
  `openspec/changes/neo-completion-portfolio/planning-context.md` under
  `## Findings -- neo-array-completion` (scoping decision, gaps confirmed
  against code, key decisions D1-D4, OQ resolutions).
- [ ] 6.2 Update `.trae/documents/neo-deferred-items.md`: mark D-ARR (rank-1
  portion) RESOLVED in §2 master table + §3 detail; add the
  `neo-array-multidim` child pointer for the multi-dim deferral.
  - **SKIPPED per instructions:** the shipper updates neo-deferred-items.md at
    archive time.
- [ ] 6.3 (Ship/archive step, post-review) merge the `neo-arrays` spec delta
  into `openspec/specs/neo-arrays/spec.md` and move the change to
  `openspec/changes/archive/`.
  - **Post-review step (not done at apply).**
