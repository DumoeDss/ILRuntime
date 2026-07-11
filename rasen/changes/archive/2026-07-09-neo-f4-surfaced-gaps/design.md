# Design -- neo-f4-surfaced-gaps

> Per-gap dump-gate verdicts (probed on HEAD `bc9f1020`), the precise fix sites,
> and the dead-end / re-characterization notes. Legacy is the REFERENCE; both
> fixes are Neo-only / Legacy-neutral.

## Gap A -- op_Equality null-operand autogen-binding AoRE

### Dump-gate (HEAD `bc9f1020`)

Probe `NeoStep14_ILEx_GapA_TypeOpEqualityNull`:
```csharp
MyEx e = new MyEx("gap-a-msg");
Type t = e.GetType();
if (t == null) return -10;  // lowers to Type.op_Equality(t, null)
return 9;
```
- **HEAD result:** `Return:-96` (the probe's `catch (ArgumentOutOfRangeException)`).
- **Root, confirmed at `ILIntepreter.Neo.cs:123-128`:** `ReadNeoReference` did
  `return mStack[idx];` with no null-sentinel check. The autogen
  `System_Type_Binding.op_Equality_1_Neo` (`ILRuntimeTestBase/AutoGenerate/
  System_Type_Binding.cs:211-212`) reads BOTH `left` and `right` via
  `ReadNeoReference`; the NULL right operand is the Neo null sentinel (`-1`),
  so `mStack[-1]` -> `AutoList` index-out-of-range -> AoRE.

### Established convention (precedent)

The `(idx >= 0) ? mStack[idx] : null` form is already used at:
- `ILIntepreter.Neo.cs:274` (`CLRMethod.Invoke` Neo arg read: `args[i] = (idx
  >= 0) ? mStack[idx] : null;`).
- `CLRRedirections.AsyncNeo.cs:808,1203` (`(idx >= 0 && idx < mStack.Count) ?
  mStack[idx] : null`).
- `ILIntepreter.Neo.cs:2238,2282` (delegate-newobj target read).

### Fix

`ReadNeoReference` (`ILIntepreter.Neo.cs:123`): `return idx >= 0 ? mStack[idx]
: null;`. Neo-only helper (it lives inside the `#if ENABLE_NEO_MODE` file);
Legacy is byte-identical (Legacy uses `StackObject.ToObject`, a different
path). One line. This closes the defect CLASS for every autogen Neo binding
that reads a reference operand (Type/String op_Equality, and all others).

### Semantic check

CLR `Type.op_Equality(a, b)` with `b == null`: `a == null` is `false` when `a`
is non-null. After the fix, `ReadNeoReference` returns `null` for the null
operand, the binding does `left == right` (`non-null == null` -> `false`),
writes `0` to `retDst`. Correct. (Both-null -> `true`; non-null == non-null ->
value equality. All correct.)

## Gap B -- derived IL type's flat instance misses inherited fields

### Dump-gate (HEAD `bc9f1020`) -- re-characterized

The F-4 finding's hypothesis ("plain `new MyEx("msg")` stores `this` into
`Msg`") was PROBED FIRST and found STALE:

- `new MyEx("ctor-msg")` alone -> `NeoF4ReflectionProbe.ReadFieldStringMatch(
  ili, "Msg", "ctor-msg")` returns `1` (Msg IS "ctor-msg"). **Plain ctor works
  on HEAD.** The parametrized-Run follow-on `de0ef01c` ("marshal instance+p +
  reference returns") incidentally closed this path.
- `new DerivedEx("derived-msg")` where `DerivedEx(string msg) : base(msg)` and
  `MyEx(string msg) { Msg = msg; }` -> **throws NRE** at `ILIntepreter.Neo.cs`
  `Stfld_Ref` (`GetNeoILInstance` returned an instance whose `ManagedObjects`
  was null; `ins.ManagedObjects[0] = ...` NREd).

Bisect matrix (all on HEAD):
| shape | result |
|---|---|
| `new DerivedEx()` (paramless) | PASS (no stfld; the latent under-alloc is unexercised) |
| `new MyEx("ctor-msg")` | PASS (Msg == "ctor-msg") |
| `new DerivedEx("derived-msg") : base(msg)` | **NRE** (stfld on null ManagedObjects) |

### Root, confirmed via instrumentation

Instrumented `Newobj` IL-ref branch + `Stfld_Ref` + `GetNeoILInstance` +
`ILTypeInstance.ManagedObjects`:
- The newobj for **DerivedEx** (both paramless and string) pushed a `this`
  whose `ManagedObjects == null` (`thisManagedNull=True`).
- The newobj for **MyEx** pushed a `this` whose `ManagedObjects != null`
  (`thisManagedNull=False`).
- => `DerivedExType.Instantiate(false)` produced an instance with NULL
  `ManagedObjects`.

`ILTypeInstance` (`ILTypeInstance.cs:344-352`, Neo ctor): `managedObjs = new
AutoList(mCnt)` only when `mCnt > 0`, where `mCnt = type.TotalReferenceCount`.
`DerivedEx.TotalReferenceCount` was `0` (own fields only -- DerivedEx declares
none), while `MyEx.TotalReferenceCount` was `1` (the `Msg` reference field).

`ILType.InitializeFields` (`ILType.cs:2606-2786`) computed the Neo flat-layout
accumulators `primitiveOffset` / `referenceOffset` starting at `0` and
iterating ONLY `definition.Fields` (this type's OWN fields -- Cecil's `Fields`
excludes inherited). So `totalPrimitiveSize` / `totalReferenceCnt` (`:2785-
2786`) never included the IL base's fields.

### Legacy is the reference (flat too)

Legacy instance layout is ALSO flat: `StackObject[type.TotalFieldCount]`, and
`TotalFieldCount` (`ILType.cs:361-374`) ACCUMULATES the IL base:
`totalFieldCnt = ((ILType)BaseType).TotalFieldCount + fieldTypes.Length`. Neo
diverged (its `TotalReferenceCount` did not accumulate). The Neo field-access
path already assumed flat offsets -- the runtime `Stfld_Ref` / `Ldfld_Ref`
index `ins.ManagedObjects[offset.ReferenceOffset]` directly with the JIT-stamped
offset -- so the instance allocation MUST be flat too.

### Fix

`ILType.InitializeFields` (`ILType.cs`, the `#if ENABLE_NEO_MODE` arm): seed
the accumulators from the IL base type's already-flat totals before the own-
field loop:
```csharp
if (BaseType is ILType baseIlTypeForLayout)
{
    primitiveOffset = baseIlTypeForLayout.TotalPrimitiveSize;
    referenceOffset = baseIlTypeForLayout.TotalReferenceCount;
}
```
This makes `TotalPrimitiveSize` / `TotalReferenceCount` include the inherited
region (recursively -- a base with its own IL base already includes THAT, so
multi-level hierarchies are covered by construction).

### Consistency of offsets after the fix

- The JIT stamps a field's offset via the field's DECLARING type
  (`AppDomain.GetFieldOffset` -> `type = GetType(f.DeclaringType, ...)`;
  `ILType.GetFieldOffset(token)`), so `MyEx.Msg` always carries MyEx-local
  offset `(PrimitiveOffset=0, ReferenceOffset=0)`.
- `GetFieldOffset(idx)` (`ILType.cs:2511-2519`) recurses into `BaseType` for
  `idx < FieldStartIndex`, returning the base's offsets -- which are 0-based
  from the base's own perspective. After the fix, the base region occupies
  `[0..baseTotal)` in BOTH the base's local view and the derived type's flat
  view, so the base's 0-based offsets ARE the correct absolute offsets in the
  derived instance.
- The derived type's OWN `fieldOffsets[...]` now start at `baseTotal`
  (absolute), so its own fields are correctly placed after the inherited
  region.

=> Consistent by construction. No change to `GetFieldOffset`, the JIT, or the
runtime field-access arms. Neo-only (inside `#if ENABLE_NEO_MODE`); the Legacy
arm (`fields = new StackObject[TotalFieldCount]` etc.) is untouched.

### Regression safety

`NeoStep` full smoke: 226/0/0 (was 224; +2 new probes). No existing derived-
IL-type test regressed (the fix only ENLARGES instances of derived IL types to
their correct flat size; previously-underallocated instances that happened not
to touch inherited fields were coincidentally fine, and remain so).

## Why not the contract's literal Gap B fix

The contract (and the F-4 finding) framed Gap B as "the newobj string-arg
mis-route -- store `this` not the arg." Probing empirically refuted that: the
plain-ctor string arg reaches the field correctly. Forcing a fix for the stale
hypothesis would have been wrong (no bug to fix). The honest, durable fix is
the base-field-layout accumulation above, which is what the `:base(msg)`
derived-ctor probe actually exercises. This is documented here and in the
deferred-items RESOLVED rows so a future worker does not re-chase the stale
hypothesis.

## Build / test gates

- `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` -> 0
  errors.
- `dotnet build TestCases/TestCases.csproj -c Debug` -> 0 errors.
- `dotnet build ILRuntime/ILRuntime.csproj -c Debug` -> 0 errors (Legacy-
  neutral).
- Gap A probe: HEAD `Return:-96` -> after `Return:9`.
- Gap B probe: HEAD throws (1 failed) -> after `Return:9`.
- `NeoStep14`: 23/0/0 (incl. the 2 new probes).
- `NeoStep` full smoke: 226/0/0.
