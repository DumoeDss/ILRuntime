# Proposal: neo-delegate-vt-float-return

## Why
`DelegateTest24` (`list.Sum(v => v.X)` over a `List<TestVector3>`) returns `4E-45`
(a `*(float*)&<int>` bit-reinterpret) under Neo and throws. It is the bottom of a
3-link cascade (struct-newobj -> `List<TestVector3>.Add` struct-arg -> selector
invoke); the first two links were fixed by prior children. The residual is the
CLR->IL delegate callback: when the host's `Enumerable.Sum` invokes the
`v => v.X` lambda, the float return is corrupted.

## What (root cause, pinned by a diagnostic in `NeoInvokeSub`)
Under the Neo calling convention a CLR value-type PARAMETER is laid out as a
BOXED REFERENCE (`AllocateSlotForType`: `Size=4`/`RefCount=1`, an mStack index of
the boxed struct), NOT flat managed bytes (that is how a CLR value-type LOCAL is
stored). The `v => v.X` lambda lowers to `ldarg <param>; ldfld X; ret`. The raw-
`Ldfld` runtime arm's CLR-value-type-owner block assumed the owner slot held the
struct's FLAT BYTES and read them via `ReadNeoValueType -- reinterpreting the
boxed-ref mStack INDEX as field X (silent corruption). The delegate callback
itself (`NeoInvokeSub`) is correct; it boxes the param exactly as the callee
frame expects.

## Fix (Neo-gated; mirrors the child-24/29 JIT-marker pattern)
- `JITCompiler.cs`: a new raw-Ldfld `Operand4` marker
  `NeoRawLdfldBoxedRefOwnerMarker = 0x4` (disjoint from `0x1`/`0x2`), stamped in
  `case Code.Ldfld` (CLRType branch) when `ins.Previous` is an `ldarg` (a static
  `IsLdargCode` helper). The untyped frame cannot distinguish a flat-bytes local
  owner from a boxed-ref param owner at runtime, so mark it at JIT time.
- `ILIntepreter.Neo.cs`: a new `else if ((ip->Operand4 & ...0x4) != 0)` branch in
  the raw-Ldfld `IsValueType` block (before the flat-bytes ELSE) that
  dereferences the boxed struct (`mStack[objIdx]`) and reflection-reads the field
  (`f.GetValue(target)`). The existing dest marshalling handles the field value
  by category unchanged. This is the value-type-owner analogue of the
  reference-type-owner branch one level down.

## Surfaces / impact
- Capability: `neo-value-types` (owns the Ldfld arms; child-24/29 lineage).
- Surfaced via the Step-19 delegate callback but the defect is entirely in the
  raw-Ldfld READ arm -- `NeoInvokeSub` is correct and untouched.
- Success = full-smoke failure count DROPS (DelegateTest24 + any delegate-VT-
  return tests flip green). No NeoStep regression.

## Out of scope
- `ldarg; ldflda; ldind` (address-taking read of a boxed-ref param field) -- a
  different opcode path; follow-up if hit.
- The "Cannot find Delegate Adapter" registration gap for some `Func<...>`
  signatures (a separate DelegateManager registration surface, not the
  marshalling bug).
