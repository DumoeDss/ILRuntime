## Why

The Neo rank-1 array story (Step 16 + step17-completion) leaves five small
"rare in C# output" gaps that each surface as a `NotImplementedException`
(tagged Step 16 / Step 17) or an untagged `InvalidCastException` the first time
a real test or feature hits them: (1) `Stelem_I` is correctly lowered but has
no interpreter arm; (2) generic-token `Code.Ldelem`/`Code.Stelem` and native
`Code.Ldelem_I`/`Code.Ldelem_U8` are not enumerated by the JIT `Translate`;
(3) the step17-completion `mStack[objIdx] is Array` branch in the Stind/Ldind
CLR-array path is I4-only, so every other element width (I1/I2/I8/R4/R8/U1/U2/
U4/Ref) fails on a `ldelema`-produced CLR-array address. All are small,
additive, low-regression-risk, and close the rank-1 array story. This change
delivers those rank-1 completions and explicitly DEFERS multi-dimensional
arrays (rank-2+) to a separate child.

## What Changes

- **`Stelem_I` interpreter arm** (Neo-only): add `case OpCodeREnum.Stelem_I:`
  to the runtime array-store switch in `ILIntepreter.Neo.cs`. Native-int store
  into an `IntPtr[]`/`UIntPtr[]` (the C# compiler lowers `arr[i] = (nint)x` to
  `stelem.i`). Reuses the existing 3-register encoding already lowered by
  `Optimizer.Neo.cs` (`Stelem_I` is in the lowering case list at line ~1010).
  No new JIT case needed (the JIT `Translate` already enumerates
  `Code.Stelem_I` at `JITCompiler.cs:2226`); only the runtime arm is missing.
- **Generic-token + native Ldelem/Stelem JIT enumeration** (Neo-only): extend
  the JIT `Translate` switch in `JITCompiler.cs` to enumerate the CIL codes the
  Neo lowering already handles as `OpCodeREnum.*`: `Code.Ldelem` (generic with
  type token) -> `Ldelem_Any`; `Code.Stelem` (generic with type token) ->
  `Stelem_Any`; `Code.Ldelem_I` (native int load) -> `Ldelem_I4`; `Code.Ldelem_U8`
  (native unsigned-8 / `ulong` load) -> `Ldelem_I8`. Each gets the same
  3-register `baseRegIdx`-decrement shape as the existing rank-1 Ldelem/Stelem
  cases. Rare in C# output (a generic method indexing a `T[]`, or `nint`/
  `nuint` element loads).
- **F-4 other-width Stind/Ldind CLR-array branches** (Neo-only): extend the
  `mStack[objIdx] is Array cArr` branch (currently only in `Stind_I4` and
  `Ldind_I4`) to the remaining widths that a `ldelema`-produced CLR-array
  address can flow into: `Stind_I1/I2/I8/R4/R8`, `Ldind_I1/U1/I2/U2/U4/I8/R4/R8`,
  and `Stind_Ref`/`Ldind_Ref`. Each becomes `cArr.SetValue(boxed(v), off)` /
  `cArr.GetValue(off)` for the Array branch (mirroring the I4 precedent),
  falling through to the existing IL-instance branch otherwise. (`Stind_I`/
  `Ldind_I` already `goto Stind_I4`/`Ldind_I4`, so they inherit the array
  branch and need no change.)
- **DEFER multi-dimensional arrays (rank-2+)** to a new child
  `neo-array-multidim`: the rank-aware `Address`/`Get`/`Set` `callvirt`, the
  rank-aware frame model, and `new T[n,m]` construction are a substantially
  larger change that does NOT fall out of the rank-1 work. The `neo-arrays`
  spec keeps rank-1 as a non-goal boundary; multi-dim stays an untagged JIT
  `NotImplementedException` today and is owned by the new child.

## Capabilities

### New Capabilities

_(None.)_ Multi-dimensional arrays (rank-2+) are explicitly DEFERRED to a
separate child `neo-array-multidim` (tracked in
`.trae/documents/neo-deferred-items.md`), but no spec capability is created
here -- the child will introduce `neo-array-multidim` with its own proposal
when it lands. This change keeps multi-dim as a `neo-arrays` non-goal boundary.

### Modified Capabilities

- `neo-arrays`: rank-1 array completion -- add the `Stelem_I` arm, the
  generic-token + native Ldelem/Stelem JIT enumeration, and the other-width
  Stind/Ldind CLR-array branches; lift the "generic-token / native-int
  variants" non-goal into delivered requirements; reaffirm multi-dim as a
  non-goal (now owned by `neo-array-multidim`).

## Impact

- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- new
  `Stelem_I` runtime arm; extend the I1/I2/I8/R4/R8 + Ref Stind/Ldind arms
  with the `is Array` branch (Neo-only; Legacy `ExecuteR` is the REFERENCE,
  NOT modified).
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- enumerate
  `Code.Ldelem`, `Code.Stelem`, `Code.Ldelem_I`, `Code.Ldelem_U8` in the
  `Translate` switch (Neo path; the Legacy `Translate` is shared but the new
  cases only add CIL codes that previously threw JIT-time NIE, so Legacy is
  unaffected -- confirm Legacy-neutral at apply).
- `TestCases/NeoStep16Test.cs` (extend) -- adversarial probes
  (`NeoStep16_*`): `IntPtr[]`/`UIntPtr[]` Stelem_I + Ldelem_I; generic-token
  `Ldelem`/`Stelem` via a generic method; `Ldelem_U8` (ulong); F-4 other-width
  CLR-array stind/ldind (I8, R4, R8, Ref); regression -- the rank-1
  int[]/float[]/object[]/IL-struct[] paths still work.
- `openspec/specs/neo-arrays/spec.md` -- merge deltas (archive step).
- `openspec/changes/neo-completion-portfolio/planning-context.md` -- append
  durable findings.
- `.trae/documents/neo-deferred-items.md` -- mark D-ARR (rank-1 portion)
  resolved; multi-dim pointer to the new child.

**Regression risk: LOW-MEDIUM.** All changes are additive (new opcode arms +
new JIT cases that previously NIE'd); no existing rank-1 path's encoding or
representation changes. Gate: full `NeoStep` smoke (154/154 baseline) +
Legacy-neutral stash-toggle for the JIT enumeration cases. Adversarial probes
MANDATORY (Step 17 B1 / OPT-HARDEN K1 lessons: a green smoke does NOT prove an
array-kind discriminator correct).
