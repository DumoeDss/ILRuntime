## Context

This change closes the two producer families that the shipped sibling child 16
(`neo-addi-on-float`, commit `43a74a85`) explicitly deferred. Child 16 seeded
`Ldind_*`/`Ldelem_*` in the `TypeSpecializeNeoOpcodes` switch
(`JITCompiler.cs`) so a float/double/long loaded via a byref/CLR-struct-field
indirection or an array element gets the correct `registerTypes` entry and the
typed `*_R4`/`*_R8`/`*_I8` arithmetic specialization fires. It left two families
unseeded, and both reproduce on HEAD (fc2baa26) -- see the Re-Audit in
`proposal.md`.

### The mechanism (shared with child 16)

Neo's register frame is UNTYPED (no per-slot `ObjectType` tag the way Legacy
`ExecuteR` has via `StackObject.ObjectType`). Consequently Neo's plain
`Addi`/`Subi`/`Muli`/`Divi`/`Remi` runtime arms and its plain
`Add`/`Sub`/`Mul`/`Div`/`Rem` arms are HARDCODED INTEGER: they read the operand
bytes as `int`/`long` and the immediate as `ip->Operand`. For a float/double
operand the result is correct ONLY if the JIT type-specialization pass
(`TypeSpecializeNeoOpcodes`, `JITCompiler.cs`) has rewritten the op to the typed
`*_R4`/`*_R8`/`*_I8` variant, whose runtime arm reads
`ip->OperandFloat`/`ip->OperandDouble`/`ip->OperandLong`. That specialization
(immediate form via `GetTypedImmediateBinaryOpcode`, binary form via
`GetTypedBinaryOpcode`) is driven SOLELY by
`InferPrimTag(registerTypes[op.Register2])`. So a float/double/long operand
whose producing op NEVER seeded `registerTypes` stays the default `I4`, the
specialization no-ops, and a plain integer op integer-adds/multiplies the raw
IEEE bits -> garbage.

### Shape 1 -- Call returning a primitive float/double/long

The `Call` case in `TypeSpecializeNeoOpcodes` (`JITCompiler.cs:1217-1282`) is:

```
case OpCodeREnum.Call:
case OpCodeREnum.Callvirt:
case OpCodeREnum.Callvirt_IL:
case OpCodeREnum.Callvirt_CLR:
case OpCodeREnum.Call_Redirect:
    if (op.Register1 >= 0)
    {
        IType cur = GetRegisterType(registerTypes, op.Register1);
        if (cur is ILType cil && cil.IsValueType && !cil.IsEnum) { ... clear stale VT ... }
        else if (IsNeoReferenceSlot(cur)) { ... clear stale ref, or seed primitive rt2 ... }
        // ELSE: cur is null (fresh temp) or a primitive -> NO ACTION (THE GAP)
    }
    break;
```

The case resolves `cm.ReturnType` (e.g. `JITCompiler.cs:1243-1248` and again at
`:1273-1278`) but uses it ONLY to decide whether to CLEAR a stale VT/ref type.
It never SEEDS a fresh dest. So when the call dest is a fresh temp
(`x = GetF() + 100f`; the call result is NOT stored to a typed local first),
`registerTypes[dest]` is `null`, and the subsequent `add`/`addi` keys on `null`
-> `I4` -> plain integer op -> corruption. (Note: the stale-ref branch at
`:1278` ALREADY seeds a primitive return type, but ONLY when `cur` was a stale
reference. The gap is the common `cur == null` / `cur == primitive` case.)

JIT evidence (`NeoStepFloatSeeding_TC1_CallRetFloatAddConst`):
`call r7, GetOneF()` -> `addi r7,r7,1120403456` (0x42C80000 = bits of `100.0f`,
plain integer `addi`, NOT `addi.r4`).

### Shape 2 -- raw `Ldfld` of a CLR-struct primitive field

A field whose DECLARING type is a `CLRType` escapes the Neo typed-splitter (it
splits into `Ldfld_R4`/`R8`/`I8`/... ONLY for an `ILType` declaring type; child
4). So it reaches `TypeSpecializeNeoOpcodes` as a raw `OpCodeREnum.Ldfld` with
`OperandLong = (typeHash<<32)|fieldHash` (identical to Legacy's raw encoding).
There is NO `case OpCodeREnum.Ldfld:` in the seeding switch -- only the typed
`Ldfld_R4`/`R8`/`I8` arms (`JITCompiler.cs:1065-1100`) are seeded. So the loaded
primitive temp is unseeded -> `I4` -> plain integer `mul`/`add` -> corruption.
The single-expression form `a.X = a.X * 2 + 1` lowers the READ of `a.X` through
this raw `ldfld` (child 16 TC3 note); the compound form `a.X *= 2; a.X += 1;`
instead lowers through `ldflda; ldind.r4` (now seeded by child 16), which is why
child 16 TC3 used the compound form.

JIT evidence (`NeoStepFloatSeeding_TC3_RawLdfldClrStructMulAdd`):
`ldfld r7, r0, 0x2000001A59D8BA74` (RAW `ldfld`) -> `muli r7,r7,1073741824`
(0x40000000 = bits of `2.0f`) -> `addi r7,r7,1065353216` (0x3F800000 = bits of
`1.0f`), plain integer ops. The runtime local dump confirms the corruption:
`TestVector3 a = (1.7014118E+38, 1, 1)`.

## Goals / Non-Goals

**Goals:**
- Make Neo compute correct results for `float`/`double`/`long` operands obtained
  from a method `Call`/`Callvirt` (etc.) returning a primitive, OR from a raw
  `Ldfld` of a CLR-struct primitive field, and then combined with a constant or
  another register.
- Root-cause, minimal, Neo-only, Legacy-neutral fix.
- NeoStep probes that FAULT on HEAD (proving the defect) and pass after.

**Non-Goals:**
- The separate `conv.i4`-on-float bit-reinterpret bug (child 15 note). Probes
  assert via CLR-side float arithmetic to sidestep it; it stays out of scope.
- Raw `Stfld`. It consumes a value (`Register2`); it does not produce a value
  that feeds arithmetic, so it needs no dest seeding for this defect class.
- Reworking `registerTypes` into a phi-aware analysis (child 11 noted it is a
  single linear pass). The straight-line produce-then-arithmetic shape is
  same-block and reliable.
- Seeding every conceivable float producer. Seed the two families the re-audit
  confirmed (Call-returning-primitive, raw-Ldfld-CLR-struct-primitive); add more
  only if a probe demands it.

## Decisions

### D1: Fix at the seeding site (TypeSpecialize), NOT at the constant-fold site

**Decision:** extend the `TypeSpecializeNeoOpcodes` switch -- (a) the `Call`
case seeds the primitive return type; (b) a new `case OpCodeREnum.Ldfld:`
resolves the CLR field and seeds the primitive field type. The existing
immediate (`Addi -> *_R4/R8/I8`) and binary (`Add -> *_R4/R8/I8`) specialization
then fire unchanged.

**Rationale:** identical to child 16's D1. It is the root cause; the front-half
constant-fold is correct (and Legacy-shared); Neo just cannot recover the
operand type at specialize time because these producers never seeded it. It
fixes the whole class for free (`addi`/`subi`/`muli`/`divi`/`remi` AND plain
`add`/`sub`/`mul`/`div`/`rem` on this path -- they all key on
`registerTypes[Register2]`). It is Neo-only and touches no shared code.

**Alternatives considered and rejected:**
- **(a) Type-aware constant-fold emitting `Addi_R4` directly.** Rejected for the
  same reason child 16 rejected it: the typed immediate opcodes are NOT in the
  shared front-half utility switches (`GetOpcodeSourceRegister` throws on them),
  and Legacy's runtime has no `Addi_R4` arm. Larger surface, only fixes the
  immediate form.
- **(b) A JIT-time marker bit (the child-15 `0x8`-in-`Operand4` pattern) so the
  RUNTIME `Addi` arm can recover the type.** Rejected: the corruption is caught
  at SPECIALIZE time by specializing to `Addi_R4` (whose arm reads
  `OperandFloat`); seeding the producer type is strictly simpler than stamping a
  marker on every consumer and teaching the runtime arm to read it. A marker is
  the right tool ONLY when the type is unknowable at JIT time (child 15's case:
  the producer was `ldflda` whose dest offset was wrong, not mis-typed). Here
  the type IS knowable at JIT time (call return type / field type), so seeding
  is the right tool.

### D2: Shape 1 (Call) -- seed the primitive return type as a trailing step

**Decision:** in the `Call` case, after the existing stale-VT / stale-ref
if/else-if chain, resolve `cm.ReturnType` once and seed
`registerTypes[op.Register1]` when the return is a primitive. The implementer
should hoist the `appdomain.GetMethod(op.Operand2)` resolution (the current code
calls it up to 2x, at `:1243` and `:1273`).

Sketch (final position, inside `if (op.Register1 >= 0) { ... }`, after the
existing branches):
```
// Seed a primitive return type so typed arithmetic on the call result
// fires. A primitive return never conflicts with the stale-VT/stale-ref
// logic above (which only KEEPS non-primitive VT/ref types; a primitive
// return is excluded by IsPrimitive). Covers the cur==null fresh-temp
// case (the gap) and re-seeds harmlessly after a clear.
var cmr = appdomain.GetMethod(op.Operand2);
IType rtr = cmr != null ? cmr.ReturnType : null;
if (rtr is ILType rtil && rtil.IsByRef) rtr = null;
if (rtr != null && rtr.IsPrimitive)
    SetRegisterType(registerTypes, op.Register1, rtr);
```

**Why this is safe:**
- A by-value IL-VT return (`retIsInFrameVt`, kept by the existing logic) is NOT
  primitive -> the `IsPrimitive` guard skips the seed -> the kept VT type is
  preserved.
- A reference return is NOT primitive -> skipped.
- A void return has `rt == VoidType` (not primitive) -> skipped.
- A byref return is nulled by the `IsByRef` check -> skipped.
- The stale-ref branch (`:1278`) already seeds a primitive return when clearing
  a stale ref; the trailing seed re-seeds the same type -> no-op.

**Call variants:** the case already covers `Call`/`Callvirt`/`Callvirt_IL`/
`Callvirt_CLR`/`Call_Redirect` (one case block). The IL variants resolve cleanly
via `appdomain.GetMethod` (the existing code relies on this). For
`Callvirt_CLR`/`Call_Redirect` (CLR callees), `appdomain.GetMethod` may return a
`CLRMethod` whose `ReturnType` is the CLR return type mapped to an `IType`; if
resolution returns null the seed is skipped harmlessly (the dest stays unseeded
-- no worse than today). The re-audit probe uses IL callees, which is the
load-bearing path; CLR-callee return-type resolution should be spot-checked by
the implementer (open question O1).

### D3: Shape 2 (raw Ldfld) -- resolve the CLR field and seed the primitive type

**Decision:** add a `case OpCodeREnum.Ldfld:` to the seeding switch that mirrors
child-4's runtime handler resolution and seeds `registerTypes[op.Register1]`
with the field's primitive type.

Sketch (placed among the other `Ldfld_*` seeding cases, ~`JITCompiler.cs:1065`):
```
case OpCodeREnum.Ldfld:
{
    // Raw ldfld reaches here ONLY when the declaring type is a CLRType
    // (child 4: the typed splitter emits Ldfld_R4/R8/I8 only for an ILType
    // declaring type). OperandLong = (typeHash<<32)|fieldHash -- identical
    // to Legacy's raw encoding and to the runtime raw-Ldfld handler.
    int typeHash = (int)((ulong)op.OperandLong >> 32);
    int fieldHash = (int)op.OperandLong;
    var declType = appdomain.GetType(typeHash);
    if (declType is CLRType ct)
    {
        var f = ct.GetField(fieldHash);
        if (f != null)
        {
            IType ft = NeoClrPrimitiveTypeToIType(f.FieldType, appdomain);
            if (ft != null)
                SetRegisterType(registerTypes, op.Register1, ft);
        }
    }
    break;
}
```

Where `NeoClrPrimitiveTypeToIType(Type clrType, AppDomain appdomain)` maps:
`typeof(float) -> appdomain.FloatType`; `typeof(double) -> appdomain.DoubleType`;
`typeof(long)`/`typeof(ulong) -> appdomain.LongType`; any other PRIMITIVE
(`int`/`short`/`byte`/`bool`/...) -> `appdomain.IntType` (the default fallback;
harmless and matches the existing `I4` default); a NON-primitive field
(struct/ref) -> `null` (leave unseeded; out of scope -- it does not feed
primitive arithmetic, and seeding a VT here would interact with the
field-access-inline discriminator). The implementer should check whether an
existing `appdomain` helper already maps a `System.Type` to an `IType` and reuse
it; otherwise a small private `switch` is fine.

**Why this is safe:**
- Only a raw `Ldfld` (CLR declaring type) reaches this case; the typed
  `Ldfld_R4`/`R8`/`I8` arms keep their own seeding cases (unaffected).
- The resolution is the SAME hash lookup child-4's runtime handler performs
  (`ILIntepreter.Neo.cs:3878-3886`), proven to work; doing it at JIT time is
  consistent with the other JIT-time type resolutions.
- Only primitive field types are seeded; VT/ref fields fall through (no
  perturbation of the field-access-inline discriminator).

### D4: Probe design

Four probes in `TestCases/NeoStepFloatSeedingProbe.cs` (already written for the
re-audit; promoted to the permanent suite). All use `TestVector3`
(CLR struct, float X/Y/Z; `TestVector3.One == (1,1,1)`) and assert via the host
helper `TestCLRBinding.SumTestVector3Fields(a, a)` = `(int)(a.X+a.Y+a.Z+a.X+a.Y+a.Z)`
computed in CLR (sidesteps the bug under test AND the out-of-scope
`conv.i4`-float-bit-reinterpret bug). A wrong result trips a deliberate `1/0`
(DivideByZero) -- the child-1/child-2 FAULT-to-fail discipline.

- TC1 `v.X = NeoStepGetOneF() + 100f` (Shape 1, float) -> v=(101,1,1) -> Sum=206.
  `NeoStepGetOneF` is a non-inlinable IL method (`hasExceptionHandler` ->
  `canInline=false` per `JITCompiler.InitializeFunctionParam`).
- TC2 `v.X = (float)(NeoStepGetOneD() + 100.0)` (Shape 1, double) -> Sum=206.
- TC3 `a.X = a.X * 2 + 1` (Shape 2, single-expression raw-ldfld mul+add) ->
  a=(3,1,1) -> Sum=10.
- TC4 `a.X = a.X + a.Y` (Shape 2, single-expression raw-ldfld reg-reg add) ->
  a=(2,1,1) -> Sum=8.

On HEAD each FAULTs (confirmed: DivideByZero, all 4). After D2+D3, the `*_R4`
arms compute the right float -> pass.

### D5: Scope automatically covers subi/muli/divi/remi and plain Add/Sub/Mul

Because D2/D3 seed the operand TYPE (not per-opcode), every typed-immediate and
typed-binary specialization that reads `registerTypes[Register2]` benefits. TC1
covers addi, TC2 covers the double (_R8) path, TC3 covers muli+addi, TC4 covers
plain reg-reg Add. `divi`/`remi` share the identical mechanism; a probe is
optional (division/modulo by a float produced this way is rare) but the fix
covers them.

## Risks / Trade-offs

- **[registerTypes is a single-pass dataflow, no phi-merge]** (child 11) -> the
  seed is reliable only for the straight-line same-block `produce; arith` shape.
  Register reuse across blocks could still miss a type. Mitigation: SAME
  reliability envelope the existing `Ldc_*`/`Ldfld_*`/`Ldind_*` seeds rely on;
  the NeoStep smoke + the 4 dedicated probes are the regression net. No
  pessimization, no broadening of the dataflow.
- **[Seeding a primitive type could perturb other type-spec decisions]** ->
  verified safe (child 16 already argued this): the other consumers of
  `registerTypes` key on `IsNeoReferenceSlot` or `IsValueType`; a primitive is
  neither.
- **[Call return-type resolution for CLR callees]** (O1) -> if
  `appdomain.GetMethod(token)` returns null or a `CLRMethod` whose `ReturnType`
  is not a mapped primitive `IType` for a `Callvirt_CLR`/`Call_Redirect` returning
  `float`, that callee's dest stays unseeded (no worse than today) and the bug
  persists for that shape. The IL-callee path (the load-bearing one) is
  confirmed. The implementer should spot-check a CLR-callee float return and, if
  needed, resolve via the `CLRMethod` return type directly.
- **[Raw-Ldfld field hash lookup cost]** -> one `CLRType.GetField(fieldHash)` per
  raw-Ldfld site at JIT time (JIT runs once per method; cached afterward).
  Negligible; identical to the runtime handler's lookup.

## Migration Plan

None. The change is additive seeding under `ENABLE_NEO_MODE`. Rollback = revert
the two `JITCompiler.cs` hunks (removes the Call trailing seed + the raw-Ldfld
case); probes fail-open (they only assert). No persisted artifact, no API, no
schema.

## Open Questions

- **O1:** Does `appdomain.GetMethod(op.Operand2).ReturnType` resolve a primitive
  `IType` for a `Callvirt_CLR`/`Call_Redirect` callee returning `float`/`double`/
  `long`? The IL-callee path is confirmed by the re-audit. The implementer should
  add a probe (or spot-check the JIT dump) for a CLR-callee float return and, if
  the seed is skipped, extend the resolution (e.g. via the `CLRMethod` return
  type). If it already works, no extra code is needed.
- **O2:** Are there OTHER primitive producers still unseeded that the full smoke
  will surface once these two are fixed? Candidates: `Ldarga`/`Ldarg` of a
  primitive byref deref, conv-family producers writing to a fresh temp consumed
  by a subsequent arith op (the `Conv_*` case at `JITCompiler.cs:1040-1054` DOES
  seed its dest via `GetConvResultType`, so conv is covered). Defer unless the
  smoke surfaces a hit; the fix site is the same (add the producer to the switch
  / extend the Call pattern).
