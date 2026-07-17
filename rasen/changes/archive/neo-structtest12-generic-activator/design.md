# neo-structtest12-generic-activator -- design

## Goal
Close the LAST full-Neo-smoke failure: StructTest12
(`T ins = new T() { i = 10 };` in `StructTest12Sub<T>() where T: struct,
ITestStruct`, T = MyStruct2). Target: full-smoke 1 -> 0.

## The test (Structs.cs:391-401)
```csharp
static void StructTest12Sub<T>() where T : struct, ITestStruct
{
    T ins = new T() { i = 10 };
    Console.WriteLine(ins.i);
    if (ins.i != 10) throw new Exception();   // fires: ins.i != 10
}
public static void StructTest12() { StructTest12Sub<MyStruct2>(); }
```
MyStruct2 = `struct { int i { get; set; } }` (4 prim bytes, 0 refs).

## Roslyn lowering (JIT-dump-confirmed final body)
```
initobj r1, MyStruct2              ; zero ins
call.redirect r1, Activator.CreateInstance[T]()   ; ins = new T()
ldloca.s r3, r1                    ; &ins
ldc.i4.s r4,10
constrained MyStruct2
callvirt set_i(value)              ; ins.i = 10
... get_i ... ; if (ins.i != 10) throw
```
`new T()` (generic struct T, NO `new()` constraint) -> `Activator.
CreateInstance<T>()`. `{ i = 10 }` -> `constrained; callvirt set_i`.

## Re-audit: Bug 1 DISPROVEN, Bug 2 = 2 sub-sites
### Bug 1 (T resolves to ILTypeInstance) -- DISPROVEN at runtime
JIT dump label `CreateInstance[ILTypeInstance]` is the FRONT-HALF template
capture-T display name. A runtime diagnostic in CreateInstanceNeo proved
`method.GenericArguments[0] = MyStruct2` (IL value type). The generic-param
substitution (DoCloneAndPatch MethodToken patch -> GetMethodTokenHash ->
appdomain.GetMethod -> FindGenericArgument) works. NO JIT change needed.

### Bug 2 sub-site (a): Activator returns a heap reference for a struct T
CreateInstanceNeo did `ilt.Instantiate()` + WriteNeoObjectResult -> the mStack
index (1) was written into `ins`'s 4 prim bytes -> `ins.i` read 1.

### Bug 2 sub-site (b): constrained callvirt set_i write lost
The direct-call path (IL VT + ILMethod override) copies the struct BY VALUE
into the callee's slot-0 and calls ExecuteNeo, but never copies the mutated
slot-0 back. ECMA `constrained.` = mutate `this` in place -> set_i's write
to slot-0 was discarded -> `ins.i` stayed 0 (after sub-site a fix).

## The fix (2 sites, both Neo-gated -> Legacy-neutral)
### Site 1: CLRRedirections.CreateInstanceNeo value-type branch
For `t.IsValueType`, write `default(T)`:
- IL value type: zero `ilt.TotalPrimitiveSize` bytes at retDst + null
  `ilt.TotalReferenceCount` ref slots at mStack[retRefBase + i] (the Initobj
  arm's contract). The JIT always emits `initobj <struct local>` before the
  call, so the slot is already zeroed -- the write is idempotent + robust.
- CLR value type: `CreateDefaultInstance()` (boxed default) + WriteNeoValueType
  (flat bytes). A ref-field CLR struct NIEs in WriteNeoValueType (Step-13b).
Reference-type T keeps Instantiate + WriteNeoObjectResult (unchanged).

### Site 2: ILIntepreter.Neo.cs constrained-callvirt direct-call write-back
After `ExecuteNeo(ilmOverride, ...)` in the direct-call branch, copy the
callee's slot-0 prim bytes back to the caller's struct:
`Unsafe.CopyBlock(frameBase + thisByteOff, targetBase + thisSlotInfo.Offset,
ilConstrained.TotalPrimitiveSize)`. Getters leave slot-0 unchanged ->
no-op for them; mutators (set_i) now persist. The ref-region write-back is
deferred (callee frameRefBase internal to ExecuteNeo; 0-ref MyStruct2 is the
only smoke hit).

## Soundness
- Site 1: `default(T)` for a value type is all-zero bytes; matches `new T()`
  for a struct with no custom parameterless ctor. Reference-type path
  byte-identical to HEAD.
- Site 2: the copy-back is correct for ALL struct instance methods (a struct
  method semantically mutates `this` in place). For non-mutators it is an
  idempotent no-op. NeoStep 417/0 confirms no regression on the broad
  constrained-callvirt surface (struct ToString/GetHashCode/getters).

## Why BOTH sites are required (2-for-1)
- Site 1 alone: `ins.i = 0` (zeroed, but set_i lost via site b) -> throws.
- Site 2 alone: `ins.i = 1` (heap index, set_i mutates a box of the index
  bytes) -> wrong.
Both must ship together.
