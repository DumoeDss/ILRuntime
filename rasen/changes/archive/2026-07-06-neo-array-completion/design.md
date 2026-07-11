## Context

The Neo rank-1 array story shipped in Step 16 (Newarr/Ldelem_*/Stelem_*/Ldlen)
and was extended in step17-completion (the `Ldelema` CLR primitive-array
remainder + the I4-only `mStack[objIdx] is Array` branch in `Stind_I4` /
`Ldind_I4`). Five "rare in C# output" rank-1 gaps remain, each surfacing as a
tagged `NotImplementedException` or an untagged `InvalidCastException` on first
contact. They are independent, additive, and close the rank-1 story. The
current code state (verified at HEAD for this design):

- **`Stelem_I` lowered, no runtime arm.** `Optimizer.Neo.cs:1010` lowers
  `OpCodeREnum.Stelem_I` (it is in the 3-register `Stelem_*` lowering case
  list, same encoding as `Stelem_I4`), and `JITCompiler.cs:2226` enumerates the
  CIL `Code.Stelem_I` in `Translate`. But the runtime switch in
  `ILIntepreter.Neo.cs` (lines ~2984-3049) has arms for `Stelem_I1/I2/I4/I8/
  R4/R8/Ref/Any` and NO `Stelem_I` arm -> the catch-all throws a Step-16 NIE.
- **Generic-token + native Ldelem/Stelem not enumerated by JIT.** The
  `Translate` Ldelem list (`JITCompiler.cs:2098-2109`) has I1/U1/I2/U2/I4/U4/
  I8/R4/R8/Any/Ref. It is MISSING `Code.Ldelem` (generic, type-token),
  `Code.Ldelem_I` (native int), and `Code.Ldelem_U8` (native unsigned 8 /
  `ulong`). The Stelem list (`:2226-2234`) has I/I1/I2/I4/I8/R4/R8/Ref/Any --
  `Code.Stelem` (generic, type-token) is MISSING. These CIL codes therefore
  throw a JIT-time NIE in `Translate` before any lowering.
- **F-4 Stind/Ldind CLR-array branch is I4-only.** Only `Stind_I4`
  (`ILIntepreter.Neo.cs:3123`) and `Ldind_I4` (`:3198`) have the
  `else if (mStack[objIdx] is Array cArr) cArr.SetValue(...)` / `cArr.GetValue`
  branch added by step17-completion. The I1/I2/I8/R4/R8 + Ref variants
  (`:3099-3152`, `:3157-3260`) fall to `GetNeoILInstance(mStack, objIdx)`,
  which throws `InvalidCastException` on a CLR `Array`. So a `ldelema`-produced
  CLR-array address consumed by, e.g., `stind.i8` / `ldind.r4` fails. `Stind_I`
  / `Ldind_I` `goto Stind_I4` / `Ldind_I4` and thus INHERIT the array branch --
  they need no change.

Legacy (`ILIntepreter.Register.cs`) is the REFERENCE for semantics and is NOT
modified. The Neo smoke baseline at HEAD is 154/154.

## Goals / Non-Goals

**Goals:**

- Add the `Stelem_I` runtime arm (closes the `IntPtr[]`/`UIntPtr[]` native-int
  store hole).
- Enumerate `Code.Ldelem`, `Code.Stelem`, `Code.Ldelem_I`, `Code.Ldelem_U8` in
  the JIT `Translate` so generic-token and native-int element access compile
  and route to existing lowerings.
- Extend the F-4 `is Array` Stind/Ldind branch to the remaining widths so a
  `ldelema`-produced CLR-array address is consumable by any `stind_*`/`ldind_*`
  of the matching element width.
- Adversarial probes for every new path + regression coverage that the
  existing rank-1 int[]/float[]/object[]/IL-struct[] paths still work.

**Non-Goals:**

- Multi-dimensional arrays (rank-2+): `new T[n,m]`, the rank-aware
  `Address`/`Get`/`Set` `callvirt`, and the rank-aware frame model. Owned by a
  separate child `neo-array-multidim`.
- IL value-type arrays sourced via generic-token `Ldelem`/`Stelem` where the
  element type is itself an unconstructed generic -- the existing `Ldelem_Any`/
  `Stelem_Any` runtime arms handle constructed element types; a
  generic-parameter element type is an edge that follows the same NIE pattern
  as elsewhere if it surfaces.
- Any change to Legacy `ExecuteR`.

## Decisions

### D1: `Stelem_I` routes to the `Stelem_I4` width family (native int = I4 on this VM)

`nint`/`nuint` are pointer-sized; on the Neo VM the native-int frame slot is
4 bytes (matching `Stind_I` / `Ldind_I` which already `goto I4`). The runtime
arm treats the value as a 4-byte native int and stores via the typed CLR array
indexer. Two implementation options:

- **Option A (preferred): `goto case OpCodeREnum.Stelem_I4;`** -- mirrors the
  `Stind_I` -> `Stind_I4` precedent exactly. The 4-byte value is read from
  `ip->Operand4` and stored. Zero new logic; one line. Works for `IntPtr[]` /
  `UIntPtr[]` because the CLR indexer boxes a 4-byte `IntPtr` correctly under a
  32-bit pointer model. **Risk**: if a future change widens native-int to 8
  bytes, this arm and `Stind_I`/`Ldind_I` must move together -- but that's a
  pre-existing coupling, not introduced here.
- Option B: a dedicated arm that reads `IntPtr`/`UIntPtr` and dispatches on the
  runtime array type (`IntPtr[]` vs `UIntPtr[]`). More faithful to the CIL
  semantics but adds an untested arm for a "rare in C# output" path.

**Decision: Option A.** It reuses the tested `Stelem_I4` path, matches the
existing `Stind_I`/`Ldind_I` idiom, and the `IntPtr[]`/`UIntPtr[]` CLR indexer
handles the box. The apply phase MUST dump-confirm (temp `Console.WriteLine`
in the arm) that a `nint[]` store/load round-trips; if it does not on the
current pointer model, fall back to Option B.

### D2: JIT enumeration reuses the existing rank-1 3-register shape

Each new `Translate` case gets the SAME body as the existing Ldelem/Stelem
rank-1 cases (`JITCompiler.cs:2110-2113`):

```
op.Register1 = (short)(baseRegIdx - 2); // dest/explicit-use
op.Register2 = (short)(baseRegIdx - 2); // array
op.Register3 = (short)(baseRegIdx - 1); // index
baseRegIdx--;
```

The CIL-code -> `OpCodeREnum` rewrite (the line above this block in the
existing cases) maps:

- `Code.Ldelem` -> `OpCodeREnum.Ldelem_Any` (generic with type token; the
  `Ldelem_Any` runtime arm already resolves the element type from the token).
- `Code.Stelem` -> `OpCodeREnum.Stelem_Any`.
- `Code.Ldelem_I` -> `OpCodeREnum.Ldelem_I4` (native int load = I4 width).
- `Code.Ldelem_U8` -> `OpCodeREnum.Ldelem_I8` (unsigned 8 / `ulong`; the
  `Ldelem_I8` arm reads an 8-byte value, which is correct for `ulong`).

The `Code.Ldelem`/`Code.Stelem` generic-token forms carry a type token in
`Operand2`; the existing `Ldelem_Any`/`Stelem_Any` arms already consume it, so
no runtime change is needed for those two -- only the JIT case. `Code.Ldelem_I`
and `Code.Ldelem_U8` route to existing width arms, so likewise JIT-only.

**Shared vs Neo-only:** `Translate` is in the shared `JITCompiler.cs`. The new
cases ADD CIL codes that previously threw JIT-time NIE; they do not alter any
existing case's encoding, so Legacy (which uses the same `Translate`) gains
the same codes. This is a strict generalization (Legacy could already fail on
these codes). Confirm Legacy-neutral at apply (plain `Debug` +
`useRegister=true`, the 9 pre-existing Legacy NeoStep failures unchanged).

### D3: F-4 other-width Stind/Ldind CLR-array branches mirror the I4 precedent

For each remaining width, insert an `else if (mStack[objIdx] is Array cArr)`
branch between the `objIdx == -1` (frame-native) branch and the
`GetNeoILInstance` branch, mirroring `Stind_I4:3123` / `Ldind_I4:3198`:

- **Stind_I1**: `cArr.SetValue(checked((sbyte)v) -> object, off)` -- but the
  CLR `Array.SetValue(object, int)` boxes; the array's actual element type
  determines the cast. Mirror I4: `cArr.SetValue(v, off)` where `v` is the
  already-typed local (sbyte/short/long/float/double).
- **Stind_I2 / I8 / R4 / R8**: `cArr.SetValue(v, off)` (the typed local boxes).
- **Stind_Ref**: `cArr.SetValue(mStack[vIdx], off)` (the managed object).
- **Ldind_I1 / U1 / I2 / U2 / U4 / I8 / R4 / R8**: box-cast the result of
  `cArr.GetValue(off)` to the destination width (`*(T*)(...) = (T)cArr.GetValue(off)`).
- **Ldind_Ref**: `mStack[dstIdx] = cArr.GetValue(off); *(int*)(frameBase + ip->DstOffset) = dstIdx;`
  (mirror the existing ref-read pattern).

**Why mirror rather than centralize:** the existing arms are width-specialized
(`sbyte`/`short`/`int`/... locals). A centralized helper would either lose the
width typing or require a generic dispatch table -- more churn than the
per-arm one-line addition, and it would touch the I4 arms that step17-
completion just shipped (regression surface). The per-arm addition is the
minimal, reviewable diff.

**Discriminator is unconditional.** The `is Array` check is cheap (a type
test); no per-slot flag is needed (re-affirms the opt-harden-2 / area4
discriminator insight: the operand kind determines the branch at runtime).
Frame-native (`objIdx == -1`) and IL-instance (`objIdx >= 0` and not Array)
branches are unchanged byte-for-byte.

### D4: Multi-dim is a separate child, not folded here

Multi-dimensional arrays require (a) the rank-aware `Address`/`Get`/`Set`
`callvirt` ABI (the C# compiler lowers `a[i,j]` to a method call, NOT to
Ldelem/Stelem), (b) a rank-aware frame model for the multi-index operand, and
(c) `new T[n,m]` constructor dispatch. None of these fall out of the rank-1
work; bundling them would make this diff unreviewable (the explicit Step 13b /
area4 lesson). Multi-dim stays a `neo-arrays` non-goal and is tracked as
`neo-array-multidim` in `.trae/documents/neo-deferred-items.md`.

## Risks / Trade-offs

- **`Stelem_I` 4-byte native-int assumption (D1 Option A)** -- if the runtime
  pointer model is 8-byte on a given platform, the `goto Stelem_I4` truncates.
  -> Mitigation: dump-confirm a `nint[]` round-trip at apply; fall back to a
  dedicated `IntPtr[]`/`UIntPtr[]`-typed arm (Option B) if it doesn't. The
  coupling to `Stind_I`/`Ldind_I` is pre-existing, so this is not a new
  surface.
- **JIT `Translate` is shared (D2)** -- a Legacy-neutral regression is
  required. -> Mitigation: plain `Debug` + `useRegister=true`, run the relevant
  filter; the new cases only add CIL codes that previously NIE'd, so Legacy is
  byte-identical for every existing code path. Stash-toggle the 4 new cases and
  confirm the same 9 pre-existing Legacy NeoStep failures.
- **`Code.Ldelem_U8` -> `Ldelem_I8` mapping correctness** -- `Ldelem_U8` is
  documented as "loads an unsigned native int" in some CIL references and as
  "loads an unsigned 8-byte int" (`ulong`) in others. -> Mitigation: probe
  with `ulong[]` (the common C# shape) AND `nuint[]` at apply; the correct
  lowering is whichever the probe exercises. If `Ldelem_U8` is actually native-
  unsigned-int, route to `Ldelem_I4` instead. Dump-confirm before shipping.
- **F-4 `Array.SetValue`/`GetValue` boxing perf** -- the `is Array` branch
  boxes on every store/load of a primitive through a `ldelema` address. This
  is the same cost as the I4 branch step17-completion shipped and only fires
  for the rare `fixed`/`ref`/`stind`-through-array-address shape (direct
  `Ldelem_*`/`Stelem_*` use the fast typed indexer). Acceptable.
- **Green smoke does not prove an array-kind discriminator correct** (Step 17
  B1 / OPT-HARDEN K1 lesson). -> Mitigation: adversarial probes MANDATORY --
  each new path gets a FAIL-on-HEAD stash-toggle probe (proves load-bearing)
  plus regression probes for the existing rank-1 paths.

## Migration Plan

No migration: additive opcodes/JIT cases. Build CLI `Debug_Neo`
`--no-incremental` after editing (host type churn gotcha); build TestCases
plain `Debug` (NEVER `Debug_Neo`); run smoke `-f net8.0`. Rollback = revert
the change (no on-disk format / ABI change).

## Open Questions

- **OQ1 (resolve at apply):** is `Code.Ldelem_U8` `ulong` (8-byte) or native-
  unsigned-int? Probe both; pick the lowering the probe exercises. (Design
  default: `Ldelem_I8`, the more common `ulong` shape.)
- **OQ2 (resolve at apply):** does a `nint[]` round-trip via `goto Stelem_I4`
  on the current pointer model? If not, switch `Stelem_I` to a dedicated
  `IntPtr[]`/`UIntPtr[]`-typed arm (D1 Option B).

## Apply resolutions (2026-07-06)

**OQ2 RESOLVED -- native-int is I4-width, but Option A (`goto Stelem_I4`)
REJECTED in favor of Option B (dedicated typed arm).** The C# compiler lowers
`IntPtr[] arr; arr[i] = (IntPtr)v` to `stelem.i` and `arr[i]` to `ldelem.i`
(verified via runtime debug output). The 4-byte value round-trips correctly
(Stelem_I writes val4=100/-7/4660; Ldelem_I reads v=100/-7/4660). HOWEVER
`goto case Stelem_I4` (Option A) FAILS: the Stelem_I4 arm's typed-indexer
casts only handle `int[]`/`uint[]`, so an `IntPtr[]` hits the
`((uint[])sa)[si]` fallback and throws `InvalidCastException`. Switched to
Option B: a dedicated `Stelem_I` arm that dispatches on `int[]`/`uint[]`/
`IntPtr[]`/`UIntPtr[]`, and a dedicated `Ldelem_I` runtime arm with the same
dispatch (NOT a `goto Ldelem_I4`, for the same reason). The JIT `Code.Ldelem_I`
case keeps `op.Code = OpCodeREnum.Ldelem_I` (the cast is direct since both
enums share ordering) -- no JIT rewrite needed; only the runtime arm was
missing. (D1 Option B, D2 Ldelem_I variant.)

**OQ1 RESOLVED -- `Code.Ldelem_U8` does NOT EXIST in this Mono.Cecil fork.**
The fork's `Code` enum (Mono.Cecil/Mono.Cecil.Cil/Code.cs) has no `Ldelem_U8`,
`Ldelem` (generic), or `Stelem` (generic). The generic-with-type-token form
is `Code.Ldelem_Any` (opcode 0xa3) / `Code.Stelem_Any` (0xa4), which the JIT
already enumerates and the runtime already handles. `Ldelem_U8` is not a real
ECMA opcode (an 8-byte unsigned load is just `Ldelem_I8`). So only ONE new JIT
case was needed: `Code.Ldelem_I` -> `OpCodeREnum.Ldelem_I` (the enum has
`Ldelem_I` at line 608 but the runtime had no arm and the JIT case list +
optimizer lists omitted it). The proposal's "generic-token + native Ldelem/
Stelem" gap collapses to: (a) `Stelem_I` runtime arm (existed in JIT, missing
at runtime); (b) `Ldelem_I` JIT case + runtime arm + 4 optimizer-list entries
(GetOpcodeSourceRegister / GetOpcodeDestRegister / ReplaceOpcodeSource /
ReplaceOpcodeDest in Optimizer.Utils.cs) + 1 Neo-lowering entry
(LowerNeoOffsets in Optimizer.Neo.cs).

**Additional edit sites discovered at apply (the optimizer `Ldelem_I`
omission cascade).** Adding `Code.Ldelem_I` to the JIT `Translate` exposed
FOUR further places that enumerate the Ldelem family WITHOUT `Ldelem_I`, each
throwing a generic NIE on first contact:
- `Optimizer.Utils.cs` `GetOpcodeSourceRegister` (line ~656) -- adds Ldelem_I
  to the (r1=Register2, r2=Register3) group.
- `Optimizer.Utils.cs` `GetOpcodeDestRegister` (line ~842) -- adds Ldelem_I to
  the (r1=Register1) group.
- `Optimizer.Utils.cs` `ReplaceOpcodeSource` (line ~1203) -- adds Ldelem_I to
  the (Register2/Register3 by idx) group.
- `Optimizer.Utils.cs` `ReplaceOpcodeDest` (line ~1482) -- adds Ldelem_I to
  the (Register1=dst) group.
- `Optimizer.Neo.cs` `LowerNeoOffsets` (line ~988) -- adds Ldelem_I to the
  Ldelem lowering block (DstOffset/SrcOffset/Operand4/Operand3 stamping).
All additive (Ldelem_I previously NIE'd at the JIT default for both engines);
Legacy-neutral (stash-toggle: Legacy NeoStep 8 failures identical with/without
the change). These 5 sites are SHARED optimizer code -- the Legacy runtime
itself has no `Ldelem_I` arm (separate pre-existing Legacy gap), but no
Legacy test exercises the opcode.

**Probe-surface reductions (pre-existing gaps, NOT regressions):**
- **`UIntPtr[]` probe (TC9) OMITTED.** `GetPrimitiveSize` (AppDomain.cs:1947)
  does not recognize `UIntPtr` (only `IntPtr`) -- any `UIntPtr`-typed local/
  temp throws at `AllocateLocalStackSpaces`. `UIntPtr` is a pre-existing
  unsupported primitive. The Stelem_I/Ldelem_I arms DO dispatch on UIntPtr[]
  (mirroring IntPtr[]), but no C# shape can reach them without tripping the
  UIntPtr-primitive gap upstream.
- **F-4 Stind_Ref/Ldind_Ref probe (TC15) OMITTED.** A `ref` to a `string[]`/
  `object[]` element flows through `ldelema`, but the Neo `ldelema` arm throws
  a Step-17 NIE for CLR arrays with a reference-type element
  ("ldelema on a CLR array with a reference-type element is deferred"). That
  NIE sits upstream of the new Stind_Ref/Ldind_Ref `is Array` branch, so no
  C# shape can reach it. The new branches are correct-by-construction (mirror
  the I4 precedent TC16 guards) but unreachable until the ldelema ref-type gap
  closes (a Step 17 follow-up, out of scope for D-ARR rank-1).
- **IntPtr value-comparison caveat (TC8).** `(int)IntPtr`, `IntPtr == IntPtr`
  route through `IntPtr.op_Explicit`/`op_Equality` CLR-struct-method calls,
  which hit the pre-existing by-value-CLR-struct-param gap
  (`[NEO-IL-VT-INSTANCE-COVERAGE]` / Step 6 family). TC8 therefore asserts the
  array length + no-fault (the Stelem_I/Ldelem_I arms execute cleanly); value
  correctness is proven by the apply-time runtime debug output (Stelem_I
  writes 100/-7/4660; Ldelem_I reads 100/-7/4660). On HEAD TC8 NIEs (no
  Stelem_I arm); after the fix it passes -- load-bearing.
- **Multiple-long-local optimizer quirk (TC12/TC14).** Reading 3 long/double
  elements into separate locals and combining in one `if` yields 0 for all
  (a pre-existing optimizer quirk with simultaneous 8-byte locals, unrelated
  to D-ARR -- the same shape with a single local at a time works). TC12/TC14
  use an incremental-assert pattern (read one element, fold into a running
  `bool bad`).

**Stash-toggle results (load-bearing proof).** With the 4 source files
stashed (HEAD), the Neo probes:
- TC8 FAIL (Stelem_I NIE) -> PASS after fix. LOAD-BEARING.
- TC12 FAIL (Stind_I8 array branch absent -> InvalidCastException) -> PASS.
  LOAD-BEARING.
- TC13 FAIL (Stind_R4 array branch absent) -> PASS. LOAD-BEARING.
- TC14 FAIL (Stind_R8 array branch absent) -> PASS. LOAD-BEARING.
- TC10 PASS throughout (generic string[] Ldelem_Any/Stelem_Any already
  worked; regression guard, as designed).
- TC11 PASS throughout (ulong[] Stelem_I8/Ldelem_I8 already worked; I8-width
  regression guard).
- TC16 PASS throughout (I4 CLR-array path -- step17-completion regression
  guard).

**Final smoke.** Neo `NeoStep` 161/161 green (154 baseline + 7 new probes;
TC9/TC15 omitted). Legacy `NeoStep` 8 failures identical with/without the
change (Legacy-neutral; the shared optimizer/JIT additions only add handling
for a previously-NIE'd opcode, and Legacy's own runtime lacks the arms so its
pre-existing failures are unchanged).

**Files edited (working tree UNCOMMITTED):**
- `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs` -- new
  `Stelem_I` runtime arm (dedicated IntPtr[]/UIntPtr[] dispatch, Option B);
  new `Ldelem_I` runtime arm (symmetric dispatch); `is Array` branch added to
  Stind_I1/I2/I8/R4/R8 + Ldind_I1/U1/I2/U2/U4/I8/R4/R8 + Stind_Ref/Ldind_Ref.
- `ILRuntime/Runtime/Intepreter/RegisterVM/JITCompiler.cs` -- new
  `case Code.Ldelem_I:` in `Translate` (3-register shape, no op.Code rewrite
  -- the cast to OpCodeREnum.Ldelem_I is direct).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Utils.cs` -- `Ldelem_I`
  added to 4 lists (GetOpcodeSourceRegister, GetOpcodeDestRegister,
  ReplaceOpcodeSource, ReplaceOpcodeDest).
- `ILRuntime/Runtime/Intepreter/RegisterVM/Optimizer.Neo.cs` -- `Ldelem_I`
  added to the `LowerNeoOffsets` Ldelem lowering block.
- `TestCases/NeoStep16Test.cs` -- 7 new keeper probes (TC8, TC10, TC11, TC12,
  TC13, TC14, TC16) + 2 documented omissions (TC9 UIntPtr, TC15 ref-array).

**Did NOT git commit/push** (per process discipline; LEAD commits after
review).
