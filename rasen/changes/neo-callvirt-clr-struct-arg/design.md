# Design: neo-callvirt-clr-struct-arg

## Evidence (all from a REAL Neo run on HEAD, before the fix)

### The JIT lowering of `list.Add(new TestVector3(1,2,3))` (DelegateTest24 / probe TC1)
```
4:call.redirect r7, r7, r8, r9, TestVector3::.ctor(Single,Single,Single)(c)   ; as-value ctor -> r7
5:callvirt.clr -, r0, r7, List<TestVector3>::Add(TestVector3), thisArg=0       ; r0=list, r7=struct
```
The struct is constructed into r7 (the `(c)` = crIsNewObj as-value shape, fixed
by child-28 + the sibling struct-newobj child). The `callvirt.clr` passes r7 as
the by-value struct arg.

### Where the struct is lost -- InvokeNeoClrMethod diagnostic (HEAD)
A temporary diagnostic at `InvokeNeoClrMethod` entry printed, for `Add` on
`List<TestVector3>`:
```
[CVSA-DIAG] InvokeNeoClrMethod Add on System.Collections.Generic.List`1[...TestVector3...]
            hasRedirectNeo=True isNewobj=False paramCount=1
[CVSA-DIAG] targetBase bytes: 05 00 00 00 00 00 80 3F 00 00 00 40 00 00 40 40 ...
```
- `hasRedirectNeo=True` -> the autogen `Add_0_Neo` stub serves the call; the
  reflection fallback `CLRMethod.Invoke(byte*)` is NEVER reached (its
  diagnostic never fired). **Framing hypothesis (b) -- the clrMethod.Invoke
  struct-param read -- is thus DISPROVEN** (that path is correct but dead here).
- The bytes at `targetBase` are CORRECT: `[thisIdx=5][1.0f][2.0f][3.0f]`. The
  JIT call-lowering + `CopyNeoCallArguments` + the `NeoCallParamMap`
  (`PrimitiveDst=[0,4]`, `PrimitiveSize=[4,12]`) lay the struct out perfectly.
  **Framing hypothesis (a) -- the param-map layout mis-sizing the struct -- is
  thus DISPROVEN.**

### The autogen stub (HEAD) -- the real defect
`System_Collections_Generic_List_1_TestVector3_Binding.cs :: Add_0_Neo`:
```csharp
int __curPrim = 0;
var instance_of_this_method = (List<TestVector3>)ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
TestVector3 @item = default(TestVector3);          // <-- struct NEVER read
// TODO: CLR value type reflection fallback: Step 13
instance_of_this_method.Add(@item);                 // stores (0,0,0)
```
The stub reads the `this` (list) via `ReadNeoReference` (cursor -> 4) then
LEAVES `@item = default(...)`. The 12 struct bytes at `__frameBase+4` are
never consumed. So `Add` always stores a zero struct. This is the SAME defect
class as child-28 (stale committed autogen Neo stubs never regenerated post-
Step-13b). The generator (`BindingGeneratorExtensions.AppendArgumentCodeNeo`
:161-213) ALREADY emits `ReadNeoValueType` for a by-value struct param; the
committed stub simply predates it.

`System_Collections_Generic_List_1_TestVector3NoBinding_Bi.cs :: Add_0_Neo` is
identically stale (different TODO marker `// TODO: ByRef or unsupported
ValueType parameters in Neo`, same `default(...)` body).

## The fix (Neo-gated hand-port, mirrors child-28 op_Addition/op_Multiply/Ctor)
Replace the `default(...)` + TODO with the post-Step-13b template the generator
now emits:
```csharp
int __sz_1 = ILIntepreter.GetNeoValueTypeManagedSize(typeof(TestVector3));
TestVector3 @item = (TestVector3)ILIntepreter.ReadNeoValueType(
    typeof(TestVector3), __frameBase, ref __curPrim, __sz_1);
```
`ReadNeoReference` already advanced `__curPrim` to 4 (past the `this`); the
struct read consumes bytes [4..16), matching the `NeoCallParamMap` layout.
No engine / JIT / optimizer / object-model / generator change. The Legacy
`#else Add_0` stub is byte-identical (untouched).

## Why List<int>.Add worked on HEAD (control)
`List<int>.Add` has a stale stub too, BUT `int` is a primitive -- the stale
stub's `default(int)` + the autogen primitive-read path happen to read the
int correctly (the stub for a primitive param emits `ReadNeoInt32`, not
`default`). Only the VALUE-TYPE (struct) param stubs have the `default(...)`
TODO. Probe TC2 (`List<int>.Add(42)`) passes on HEAD; TC1/TC3 (struct) fault.
This isolates the gap to the value-type-arg stub, exactly the framed bug.

## The cascading gap (DelegateTest24's residual, OUT OF SCOPE)
After the fix, `list[i].X` read entirely in the host returns 1,2,3 (proven via
a temporary diagnostic in `Sum_2_Neo`). But `list.Sum(v => v.X)` still returns
`4E-45` (corrupted). The `v => v.X` lambda is bridged by
`FunctionDelegateAdapter2[TestVector3,Single]`; when the HOST's `Enumerable.Sum`
invokes it, the adapter corrupts the struct-param / float-return marshalling.
`4E-45` ~ `*(float*)&3` -- a float-bit-reinterpret signature (the int 3 -- the
last element's X value -- reinterpreted). This is a Step-19 delegate return/
param marshalling bug, a DIFFERENT class from `callvirt.clr` struct-arg. It
blocks DelegateTest24 from flipping and is documented as a surfaced follow-up
(`neo-delegate-vt-float-return`), not fixed here.

## Discriminators / collision-freedom
- The stub change is `#if ENABLE_NEO_MODE`-gated; Legacy `Add_0` unchanged.
- `ReadNeoValueType` is the SAME helper the reflection fallback
  (`CLRMethod.Invoke`) and the post-13b generator use, sized by
  `GetNeoValueTypeManagedSize(typeof(TestVector3))` (= `Unsafe.SizeOf` = 12) --
  byte-consistent with the `NeoCallParamMap` `PrimitiveSize` by construction.
- TestVector3 / TestVector3NoBinding are pure-primitive structs (3 floats);
  `ReadNeoValueType` handles them (no ref-field Step-13b NIE).

## Risk
- Additive, Neo-gated, one-line-per-stub. The `this`-read + the call are
  unchanged; only the previously-`default` param is now read. A struct WITH
  ref fields would NIE inside `ReadNeoValueType` (fail-loud) -- TestVector3 has
  none. No regression surface (NeoStep 388/0; smoke 101 stable).
