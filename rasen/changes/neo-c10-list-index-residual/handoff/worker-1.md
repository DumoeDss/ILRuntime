# Handoff: neo-c10-list-index-residual / worker-1

## Status
- **Bug 1 (StructTest8): FIXED + VERIFIED.** Shipped (JITCompiler.cs, one additive
  case-block in `TypeSpecializeNeoOpcodes`). Full Neo smoke 70 -> 69 (StructTest8
  flipped green). NeoStep 394/0 (no regression). Legacy-neutral by construction
  (the edit is inside `#if ENABLE_NEO_MODE`).
- **Bug 2 (StructTest11): NOT FIXED -- handed off.** Root cause pinned, fix path
  identified, but the first implementation was UNSOUND and was reverted. See below.

This file is the Bug 2 handoff. Bug 1 is documented in `../design.md`.

## Bug 2 -- IL-struct call arg to a CLR reference (ILTypeInstance) param is not boxed

### Reproducer
`TestCases/Structs.cs:360 StructTest11`:
```csharp
List<Anim> lst = new List<Anim>();           // Anim is an IL struct (string name; float duration)
for(int i=0;i<50;i++) lst.Add(new Anim(i.ToString(), i+10f));
if (Math.Abs(lst[12].duration - 22) > 0.000001f) throw new Exception();
```
Fails on the FIRST `Add` (i=0) with
`System.ArgumentOutOfRangeException: Index was out of range ... (Parameter 'index')`,
stack `List`1.get_Item -> Add_0_Neo:98 (the `item = ReadNeoReference(...)` line)
-> InvokeNeoClrMethod -> ExecuteNeo Callvirt_CLR arm.

### Root cause (PINNED, JIT-dump-confirmed)
- `List<Anim>` is resolved by ILRuntime to `List<ILTypeInstance>` (IL structs are
  represented as ILTypeInstance for CLR binding). So the call is
  `List<ILTypeInstance>.Add(ILTypeInstance)` -- the `item` param is a REFERENCE
  (a 4-byte mStack index read by `ReadNeoReference`).
- The IL-struct newobj `new Anim(...)` lowers under Neo to the VT-THIS-ADDR pattern
  (`ILIntepreter.Neo.cs` Newobj arm ~line 3582, `IsValueType && !Primitive && !Enum`).
  Its result is FLAT BYTES in the caller dest register (Anim = 4 prim bytes for
  `duration` + 1 ref slot for `name`), NOT a boxed ILTypeInstance.
- The call-arg marshal (`CopyNeoCallArguments`, `ILIntepreter.Neo.cs:405`) is a raw
  `Unsafe.CopyBlock` keyed on the callee param layout. For the `item` param the dest
  is sized 4 bytes (`AllocateNeoCallParamSlot` `else` branch, Optimizer.Neo.cs:1617
  for a reference type). So it copies 4 bytes from the source struct's PRIMITIVE
  region (the `duration` float bits, e.g. 0x41A00000) into the `item`'s 4-byte
  reference slot. `ReadNeoReference` then reads those float bits as an mStack index
  -> `mStack[garbage]` -> `List.get_Item` OOB.
- There is NO boxing step at the call boundary. Legacy boxes IL structs on the eval
  stack (a struct value IS an ILTypeInstance), so Legacy never hits this; it is
  Neo-specific (flat-bytes struct model).

### The box mechanism to reuse (already exists for the `box` opcode)
`ExecuteNeo` Box arm, IL-struct branch (`ILIntepreter.Neo.cs:4193-4213`):
```
ins = ilType.Instantiate(false);
CopyFrameToIL(frameBase, ip->SrcOffset, srcRefOffset,
              ilType.TotalPrimitiveSize, ilType.TotalReferenceCount,
              mStack, frameRefBase, ins);
ins.Boxed = true;
mStack[dstIdx] = ins; *(int*)(frameBase + ip->DstOffset) = dstIdx;
```
`CopyFrameToIL` (`ILIntepreter.Neo.cs:7431`) reads prim bytes from
`frameBase+primOffset` and ref slots from `mStack[frameRefBase+refOffset+i]` into
the ILTypeInstance. For a call-arg box, publish via `mStack.Add(ins)` -- the entry
lives within the caller's frame ref range and is truncated on frame exit
(`ExecuteNeo` does `mStack.RemoveRange(frameRefBase, mStack.Count-frameRefBase)` at
line 4079 / 7101), so it is NOT a leak.

### Eliminated hypotheses (MANDATORY reading before re-attempting)

#### HYPOTHESIS A (DEFEATED): detect the box at JIT time in `LowerNeoOffsets`
map-building using `frame.NeoRegisterTypes` (the per-register static type array
from `TypeSpecializeNeoOpcodes`).
- I implemented this fully: stored `registerTypes` on the frame, added
  `PrimitiveBoxIlType`/`PrimitiveBoxSrcRefOffset` to `NeoCallParamMap`, detected
  "source IL struct + dest reference param" in the map loop, added a box branch to
  `CopyNeoCallArguments` (with `frameRefBase` threaded through all 11 call sites),
  used `mStack.Add` for publication.
- **WHY IT FAILED: the single-pass-no-phi-merge limitation of `registerTypes`.**
  Diagnostic output (`[C10DBG]`) on StructTest11 showed that at the `Add` call, the
  item source register r5 has `neoRegisterTypes[5] == System.Int32`, NOT `Anim`.
  The JIT body is:
  ```
  3: ldloca.s r5, r1            ; r5 = &i
  4: call r5, r5, ToString()    ; r5 = string (REUSED)
  7: newobj r5, r5, r6, Anim..ctor  ; r5 = Anim  (seeds registerTypes[r5]=Anim)
  8: callvirt.clr -, r0, r5, Add(ILTypeInstance)  ; <-- needs r5 to be Anim here
  ...
  13: ldc.i4.s r5, 12           ; r5 = 12  (OVERWRITES registerTypes[r5]=IntType)
  ```
  `TypeSpecializeNeoOpcodes` is a single LINEAR pass with NO phi-merge at the loop
  back-edge. So `registerTypes[r5]` ends up = the LAST writer's type (Int32 from
  instr 13), not the type at the call site (Anim at instr 8). Storing the FINAL
  `registerTypes` on the frame and reading it in `LowerNeoOffsets` gives the WRONG
  (non-call-site) type.
- **This makes a `frame.NeoRegisterTypes`-based detection UNSOUND**: a reused
  register's final type may (a) MISS a real box (as here: r5 final=Int32 hides the
  Anim), or (b) FALSE-POSITIVE a box on a register whose final type is an IL struct
  but whose value at the call site is a genuine reference -> would box a reference
  arg and CORRUPT it. Both directions are possible. Do NOT ship this.

#### HYPOTHESIS B (DEFEATED): resolve the ILType at runtime from a hash.
- My first cut stored `ILType.GetHashCode()` in the map and did
  `appdomain.GetType(hash)`. That returns null: `ILType.GetHashCode()`
  (`ILType.cs:3448`) is an instance-id (`Interlocked.Add(ref instance_id,1)`), NOT
  the type-token hash that `mapTypeToken`/`AppDomain.GetType(int)` is keyed by. The
  type-token hash comes from `method.GetTypeTokenHashCode(token)` (a Cecil
  MetadataToken). Storing the `ILType` reference directly in the map works (the map
  is per-method, the ILType is stable in-process), but see Hypothesis A -- the
  detection itself is the blocker, not the storage.

#### HYPOTHESIS C (NOT YET TRIED, soundness UNKNOWN): runtime detection via
`frame.LocalIsReference`.
- `localIsRef[srcReg]` is per-SLOT (fixed by `AllocateLocalStackSpaces`), so it does
  NOT suffer the single-pass issue. The heuristic: if the dest param is a reference
  type AND `localIsRef[srcReg]==false`, box.
- TWO problems: (1) eval-stack registers are REUSED for both references (the
  `ToString()` string in r5) and values (the Anim), so `localIsRef[r5]` is a single
  fixed classification -- UNKNOWN which way it falls for a mixed-reuse register
  (must be verified empirically before trusting); (2) `localIsRef` gives NO type, so
  you still cannot recover the ILType to `Instantiate` at runtime (the source bytes
  do not carry it, and the dest param type is ILTypeInstance, not the struct). So C
  alone is insufficient.

### Recommended fix path (HYPOTHESIS D -- per-instruction detection)
Do the box DETECTION inside `TypeSpecializeNeoOpcodes` (where `registerTypes` IS
correct at each instruction), not in `LowerNeoOffsets`. The detection runs at the
call op and reads `registerTypes[argReg]` at that point (correct, pre-phi-merge-
imprecision because the call's args are the straight-line top-of-stack produced
immediately before the call, same block -- the SAME rationale child-11/16/21/23
used to justify their seeding despite the single-pass imprecision).

The hard part is CORRELATING the per-call box info (computed in
`TypeSpecializeNeoOpcodes`) to the per-call `NeoCallParamMap` (built in
`LowerNeoOffsets`). Concrete sub-options:

- **D1 (encounter-order queue):** Both passes iterate the body linearly and process
  call-family ops (`Call/Callvirt/Callvirt_IL/Callvirt_CLR/Callvirt_Interface/
  Call_Redirect/Newobj`) in the SAME ORDER, once each. Have `TypeSpecializeNeoOpcodes`
  enqueue a per-call box descriptor (e.g. `List<int>` of box-needed source-register
  indices + the `ILType` for each, recovered from `registerTypes` at that point) onto
  a frame-level `Queue`. `LowerNeoOffsets` dequeues one descriptor per call map it
  builds and marks the matching `primBoxIlType` slot. CAVEAT: the call's source
  registers must be computed the SAME way in both passes (the in-register args are
  `Register2/3/4`; overflow args come from `Push` sources which
  `TypeSpecializeNeoOpcodes` does not currently scan -- so D1 only covers the
  in-register args, which is enough for `List.Add` (2 args, both in-register) but
  NOT for >3-arg calls with a struct overflow arg).

- **D2 (stamp on the call op):** `TypeSpecializeNeoOpcodes` stamps the box info onto
  the call `OpCodeR`. The call op's `Operand` field is FREE during
  `TypeSpecializeNeoOpcodes` (it is only set to `callParams.Count` in `LowerNeoOffsets`
  at Optimizer.Neo.cs:1459, so read it BEFORE that overwrite). `Operand` is one int --
  enough for a marker + a single arg index (the common case), not multiple. For the
  ILType, use a side array. This is more fragile than D1.

Either way, the RUNTIME box in `CopyNeoCallArguments` (the version I wrote and
reverted) is CORRECT and reusable: add `int frameRefBase` to the signature, and for
a box slot do
```
var ins = ilType.Instantiate(false);
CopyFrameToIL(frameBase, map.PrimitiveSrc[i], srcRefOff,
    ilType.TotalPrimitiveSize, ilType.TotalReferenceCount, mStack, frameRefBase, ins);
ins.Boxed = true;
int pubIdx = mStack.Count; mStack.Add(ins);
*(int*)(targetBase + map.PrimitiveDst[i]) = pubIdx;
```
The 11 caller call-sites all have `frameRefBase` in scope (it is an `ExecuteNeo`
local). Publication via `mStack.Add` is safe (truncated on frame exit).

### Other tests in the same failure class (verify after fixing Bug 2)
The full-smoke `Parameter 'index'` OOB failures at the 70-baseline:
`StructTests.StructTest11`, `CLRBindingTest.CLRBindingTest08`,
`DelegateTest.DelegateTest19`, `RegisterVMTest.RegisterVMTest04`,
`HotfixBasicTestCases.Test04`. Re-audit each after a Bug 2 fix -- they may share the
struct-arg-to-reference-param root OR be distinct. CLRBindingTest08 is the most
likely sibling (CLR binding + struct).

### Build/test commands (this environment)
```
dotnet build-server shutdown
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo -p:UseSharedCompilation=false
# (TestCases unaffected by this change; no rebuild needed unless TestCases changed)
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true StructTest11
# full smoke (~3-4 min, NOT 25): drop the name filter
```
NOTE: full smoke in THIS environment runs in ~3-4 minutes (928 tests), not 25.
