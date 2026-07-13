# Proposal: neo-callvirt-clr-struct-arg

> Wave-2 child of `neo-overhaul`. Branch `features/object-model-overhaul`.
> Neo = `ExecuteNeo` under `ENABLE_NEO_MODE`. Neo-gated -> Legacy-neutral by construction.

## Problem (root cause, re-audit-confirmed; framing PARTLY disproven)

A `callvirt.clr` to a CLR-generic instance method taking a value-type arg
(canonical case: `List<TestVector3>.Add(item)`) delivered a ZERO struct to the
host CLR method under Neo. The surfacing note framed this as either (a) the
param-map layout for instance CLR-generic methods mis-sizing the struct, or
(b) the `clrMethod.Invoke` struct-param read. **Re-audit disproved BOTH:**

1. The param-map layout is CORRECT. A byte-dump of the callee param region
   (`targetBase`) at `InvokeNeoClrMethod` entry shows the struct's flat bytes
   arrive intact: `[thisIdx 05 00 00 00][1.0f][2.0f][3.0f]`. The JIT call-
   lowering + `CopyNeoCallArguments` correctly size+copy the 12-byte struct.
2. `clrMethod.Invoke` (the reflection fallback struct-param read) is NEVER
   REACHED: `List<TestVector3>.Add` resolves with `RedirectionNeo != null`
   (an autogen Neo binding stub exists for it), so `InvokeNeoClrMethod` takes
   the redirect arm and returns before the reflection fallback.

**The REAL root cause** is the recurring stale-autogen-stub defect class
(child-2 / child-28): the committed `Add_0_Neo` stub in
`System_Collections_Generic_List_1_TestVector3_Binding.cs` predates the
(correct, post-Step-13b) generator. It leaves the value-type param as
`default(TestVector3)` with a `// TODO: CLR value type reflection fallback:
Step 13` marker, so `instance_of_this_method.Add(@item)` always stores
`(0,0,0)`. The generator (`BindingGeneratorExtensions.AppendArgumentCodeNeo`)
ALREADY emits `ReadNeoValueType` for a by-value struct param (with-or-without
a binder) -- the committed stub was simply never regenerated. Identical
stale-stub defect in `List<TestVector3NoBinding>.Add_0_Neo` (different TODO
marker, same `default(...)` body).

## Fix (Neo-gated hand-port, mirrors child-28; NO engine / generator change)

Hand-port the two `Add_0_Neo` stubs to the post-Step-13b template (regen is
GUI-bound per child-28):
```csharp
int __sz_1 = ILIntepreter.GetNeoValueTypeManagedSize(typeof(TestVector3));
TestVector3 @item = (TestVector3)ILIntepreter.ReadNeoValueType(
    typeof(TestVector3), __frameBase, ref __curPrim, __sz_1);
```
Files: `ILRuntimeTestBase/AutoGenerate/System_Collections_Generic_List_1_
TestVector3_Binding.cs` (Add_0_Neo), `System_Collections_Generic_List_1_
TestVector3NoBinding_Bi.cs` (Add_0_Neo). Both inside `#if ENABLE_NEO_MODE`.

## Scope note (cascading gap, honestly out of scope)

`DelegateTest24` (the only full-smoke test exercising `List<TestVector3>.Add`)
does NOT flip green after this fix. Its residual is a CASCADING, DIFFERENT-
CLASS gap: `list.Sum(v => v.X)` returns `4E-45` (corrupted). Diagnostics
prove the Add marshalling now works (a host-side read of `list[i].X` returns
1,2,3 exactly), but the `FunctionDelegateAdapter2[TestVector3,Single]` that
backs the `v => v.X` lambda corrupts the float return / struct-param when the
HOST's `Enumerable.Sum` invokes it. That is a Step-19 delegate float-return /
struct-param marshalling bug, NOT a `callvirt.clr` struct-arg bug. It is out
of scope for this child and is documented as a surfaced follow-up.

## Verification
- Stash-toggle FAIL-on-HEAD -> PASS-after on 3 probes (NeoStep-named).
- NeoStep broad smoke 388/0 (no regression).
- Legacy-neutral: plain Debug + useRegister=true probe run 3/0; the change is
  inside `#if ENABLE_NEO_MODE` (Legacy `Add_0` byte-identical).
- **Full smoke delta: 101 -> 101 (UNCHANGED).** DelegateTest24 (the sole
  List<VT>.Add consumer) does not flip because of the cascading delegate gap
  above. The fix is a real correctness fix (proven: host reads list[i].X =
  1,2,3) and is kept; it does not by itself move the smoke count. Honest
  outcome per the "fix the tractable part + report" mandate.

## Surfaced follow-up
- **neo-delegate-vt-float-return** (P1): `FunctionDelegateAdapter2` corrupts a
  float / struct return when the HOST invokes an IL lambda (`v => v.X`)
  taking/returning a value type. Blocks DelegateTest24 + any
  `IEnumerable<VT>.Sum/Select(func)` over a CLR-struct collection. Step-19
  delegate return/param marshalling, distinct from callvirt-clr-struct-arg.
- **Stale autogen Neo stubs (same class)**: 9 more `default(...)` TODO sites
  (JInt x6, TestStruct x2, TestVector3NoBinding-methods x3, TestVectorClass x2,
  TestVector3.Test_3 VT-this+out, async-builder byref-`this` x6). Each is a
  mechanical ReadNeoValueType port once its shape is confirmed; the async-
  builder ones are the valuetask-marshal territory. Not blocking this child.
