## Context

Neo Step 10 introduced a compile-time class VTable on `ILType`
(`IMethod[] neoVTable` + `Dictionary<string,int> neoVTableSlots` keyed by
`IMethod.SignatureString`), built lazily by `EnsureNeoVTable()` / `BuildNeoVTable()`
(`ILRuntime/CLR/TypeSystem/ILType.cs`, lines ~327-579). `callvirt` is lowered in
`JITCompiler.InitializeCallvirtDispatch` (line ~2107): if the IL declaring type is
non-interface and `TryGetNeoVTableSlot` resolves a slot, it emits `Callvirt_IL`;
CLR targets emit `Callvirt_CLR` (or generic `Callvirt` if the declaring type is an
interface / `object`). The interpreter arms `Callvirt_IL` / `Callvirt_CLR` /
`Callvirt` in `ILIntepreter.Neo.cs` (lines ~1353-1422), with
`ResolveNeoCallvirtILTarget` / `ResolveNeoCallvirtCLRTarget` /
`ResolveNeoGenericCallvirtTarget` doing the runtime resolution.

The gap: when the declared method's `DeclearingType.IsInterface` is true,
`InitializeCallvirtDispatch` deliberately does NOT call
`TryGetNeoVTableSlot` (line 2113 guards on `!declaringILType.IsInterface`), so
interface calls fall through to the generic `Callvirt` arm — which has no
interface-aware offset resolution and cannot land on the right implementing method
in general (an interface method's "slot" is not a class-virtual slot). Step 11
fills this gap with an interface offset map and a dedicated `Callvirt_Interface`
opcode, keeping dispatch O(1).

Constraints:
- All new code lives behind `#if ENABLE_NEO_MODE`; no Legacy behavior change.
- Interface method slot identity reuses Step 10's `SignatureString` key (no new
  matcher — flagged as transitional for Step 10/11 in the design doc; the richer
  structured-signature matcher is a later shared concern).
- Only `ILType` carries a Neo VTable. `IType.cs` needs no public API change.

## Goals / Non-Goals

**Goals:**
- O(1) interface method dispatch on IL instances in Neo mode, reusing the existing
  `neoVTable` via an offset map (no separate interface method array).
- Independent 0-based method slot namespace per interface, resolved through the
  Step 10 `SignatureString` key.
- A clean, explicit failure mode (no ip overrun, no null-deref, no Legacy
  fallback) when an object does not implement the interface or a slot is missing.
- Cover IL-implements-IL-interface, multiple interfaces, interface inheritance
  chains, and IL-implements-CLR-interface (IL side) in `NeoStep11Test.cs`.

**Non-Goals:**
- The CLR-exposed `CrossBindingAdapter` path (CLR code calling an IL object
  through a CLR interface such as `IDisposable`, where the CLR side demands an
  adapter wrapper). Step 11 only handles calls originating inside the Neo
  interpreter through an interface-typed variable.
- Generic interface method instantiations and explicit interface implementations
  beyond what Step 10's `SignatureString` matcher already covers. These reuse the
  same matcher; if the matcher proves insufficient they surface as a missing-slot
  exception (caught by the error scenario), not a silent mis-dispatch. The
  structured-signature matcher is a later shared concern (Step 10 TODO, design
  doc §13.5.1).
- `constrained.` callvirt specialization (Step 13 / 18 territory).
- Changing the public `IType` interface.

## Decisions

### Decision 1: Reuse `neoVTable`; add an offset map, not a parallel array

**Choice:** Interface dispatch indexes the EXISTING `IMethod[] neoVTable` using a
per-interface starting offset, exactly as the planning context dictates:
`actualMethod = neoVTable[interfaceOffset + interfaceMethodSlot]`.

**Alternative considered:** a separate `Dictionary<IType, IMethod[]>`
(interface → its own method array). Rejected — it duplicates the implementing
methods already present in `neoVTable`, doubles memory, and diverges from the
Step 11 target stated in `.trae/documents/neo-implementation-steps.md` and the
settled design decision in `planning-context.md` §6.

**Rationale:** the implementing method for an interface method is already a class
virtual (or an override of one) and already occupies a `neoVTable` slot under its
`SignatureString`. The offset map just translates the interface's 0-based view
into the class VTable's coordinate space.

### Decision 2: Where offsets are computed — lazy, mirroring `EnsureNeoVTable`

**Choice:** Build the interface map lazily, on first query, guarded by a
recursion flag — identical pattern to `EnsureNeoVTable()` / `BuildNeoVTable()`.
JIT-time lowering queries the map (so it is typically built during the first
interface callvirt JIT of a given type); runtime handler queries use the already-
built map.

**Why not at type load:** `InitializeMethods` / `InitializeInterfaces` run during
type load but the Neo VTable itself is lazy; building the interface map eagerly
would pull in interface ILType initialization for types that never receive an
interface callvirt. Lazy keeps parity with Step 10 and avoids surprising load-time
work.

### Decision 3: Data structure on `ILType`

```csharp
#if ENABLE_NEO_MODE
// New fields on ILType (alongside neoVTable / neoVTableSlots / neoVTableSlotKeys):
struct InterfaceEntry
{
    public IType InterfaceType;   // the implemented interface (ILType or CLR interface)
    public int VTableOffset;      // starting slot offset into neoVTable
    public string[] MethodSlotKeys; // SignatureString per interface method slot index
                                  //   (MethodSlotKeys[k] == SignatureString of the k-th
                                  //    declared method of this interface)
}
InterfaceEntry[] neoInterfaceMap;
Dictionary<IType, int> neoInterfaceOffsets; // InterfaceType -> VTableOffset (fast lookup)
bool neoInterfaceMapBuilding;

// Public query API used by the JIT and the runtime handler:
public int GetInterfaceVTableOffset(IType interfaceType); // throws if absent? see Decision 5
public bool TryGetInterfaceVTableOffset(IType interfaceType, out int offset);
public bool TryGetInterfaceMethodSlot(IType interfaceType, IMethod method, out int slot);
void EnsureNeoInterfaceMap();
void BuildNeoInterfaceMap();
#endif
```

**Mapping rule (in `BuildNeoInterfaceMap`):**
1. `EnsureNeoVTable()` first (the offset map indexes the class VTable, which must
   exist).
2. For each `IType impl` in `this.Implements` (already populated by
   `InitializeInterfaces`, ILType.cs ~856) — and recursively for each interface
   that `impl` itself inherits (walk the interface graph) — if not already
   assigned an offset:
   - Enumerate the interface's declared methods in declaration order
     (`impl.GetMethods()` filtered to candidates), assigning each a 0-based slot
     `k`; record `MethodSlotKeys[k] = method.SignatureString`.
   - For each such method, look up its `SignatureString` in `neoVTableSlots`;
     the minimum resolved class VTable index across the interface's methods
     anchors `VTableOffset`. In the common case every interface method resolves
     to a contiguous run beginning at some `VTableOffset` and slot `k` maps to
     `VTableOffset + k`.
   - **Fallback when not contiguous / unresolved:** if any interface method's
     class-VTable index does not equal `VTableOffset + k`, store an explicit
     per-method remap (`MethodSlotKeys` plus an optional `int[] ClassSlotRemap`)
     so the handler still lands correctly. For Step 11 the common contiguous case
     is the fast path; the remap array exists for correctness on edge cases
     (explicit interface impl, base-class-provided implementation).
3. Populate `neoInterfaceOffsets` for O(1) `GetInterfaceVTableOffset`.

**Recursion guard:** `neoInterfaceMapBuilding` mirrors `neoVTableBuilding`; throw
`InvalidOperationException` on re-entry (same wording style as the Step 10 guard).

### Decision 4: The new opcode and its operands

**Opcode name:** `Callvirt_Interface`, added to `OpCodeREnum` immediately after
`Callvirt_CLR` (file `OpCodes/OpCodeREnum.cs`, ~line 1010). Consistent with the
existing `Callvirt_IL` / `Callvirt_CLR` naming (no `_Neo` suffix — the Step 10
opcodes dropped it).

**Operand encoding.** The Step 10 `OpCodeR` struct uses `Operand2` for the method
token hash (resolved via `AppDomain.GetMethod(ip->Operand2)` in the handler) and
`Operand4` packs `(thisArgOffset << 16) | slot` via `EncodeCallvirtDispatch`
(`ILIntepreter.Neo.cs` ~226, `JITCompiler.cs` ~2134). For the interface opcode we
need three runtime values: interface type identity, interface method slot, and
this-arg offset. Encoding plan (keeps the call ABI identical to `Callvirt_IL`):

| Field      | Meaning for `Callvirt_Interface`                                  |
|------------|-------------------------------------------------------------------|
| `Operand2` | declared interface method token hash (same `AppDomain.GetMethod` lookup the other arms use; gives the declared method, hence its `DeclearingType` = the interface) |
| `Operand3` | already used for `targetRetRefBase`; leave as-is for call ABI     |
| `Operand4` | `(thisArgOffset << 16) \| (interfaceMethodSlot & 0xffff)` — reuses `EncodeCallvirtDispatch`'s packing; the interface identity is recovered from `AppDomain.GetMethod(Operand2).DeclearingType` |

**Why recover the interface from `Operand2` rather than a dedicated interface-type
index:** the declared method already carries its interface declaring type, so we
avoid minting a global interface-type-index table for Step 11. (A stable numeric
interface-type identity is more valuable for the future `.neo` AOT format —
Step 23 — and can be introduced then without changing the handler contract; today
`IType` reference identity via the declared method is sufficient and matches how
`ResolveNeoCallvirtILTarget` already receives `declaredMethod`.)

**New packer in JITCompiler:** `EncodeCallvirtInterface(int interfaceMethodSlot, int thisArgOffset)` — same bit layout as `EncodeCallvirtDispatch`; kept as a separate named helper for readability and so a future AOT format change is localized.

### Decision 5: Handler logic (`ExecuteNeo` + resolver)

New helper `ResolveNeoCallvirtInterfaceTarget`, placed next to
`ResolveNeoCallvirtILTarget` (~line 221):

```csharp
static IMethod ResolveNeoCallvirtInterfaceTarget(
    OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
{
    object thisObj = ReadNeoCallThis(ip, targetBase, mStack); // null-checked
    if (!(thisObj is ILTypeInstance instance))
        throw new InvalidOperationException(...); // CLR this via interface -> out of scope (Decision 7)

    ILType runtimeType = instance.Type;
    IType ifaceType = declaredMethod.DeclearingType; // the interface

    if (!runtimeType.TryGetInterfaceVTableOffset(ifaceType, out int baseSlot))
        throw new MissingMethodException(... "does not implement interface" ...);

    int ifaceMethodSlot = ip->Operand4 & 0xffff;
    var vtable = runtimeType.NeoVTable;
    int classSlot = baseSlot + ifaceMethodSlot;
    if (classSlot < 0 || classSlot >= vtable.Length)
        throw new MissingMethodException(... "interface slot out of range" ...);

    IMethod actual = vtable[classSlot];
    if (actual == null)
        throw new MissingMethodException(... "interface slot is null" ...);

    return actual;
}
```

New arm in the call/callvirt switch (~line 1376, after `Callvirt_CLR`):

```csharp
case OpCodeREnum.Callvirt_Interface:
{
    var targetMethod = AppDomain.GetMethod(ip->Operand2); // declared interface method
    if (targetMethod == null) { ip++; continue; }

    int callParamIdx = ip->Operand;
    ref var map = ref nf.NeoCallParams[callParamIdx];
    byte* targetBase = newEsp;
    CopyNeoCallArguments(ref map, frameBase, targetBase);

    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;

    IMethod actualMethod = ResolveNeoCallvirtInterfaceTarget(ip, targetMethod, targetBase, mStack);

    if (!InvokeNeoCallTarget(actualMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
        return null;

    ip++;
    continue;
}
```

This is byte-for-byte the `Callvirt_IL` arm with `ResolveNeoCallvirtILTarget`
swapped for `ResolveNeoCallvirtInterfaceTarget` — the call ABI is intentionally
identical, satisfying the "same ABI" requirement.

### Decision 6: JIT lowering rule

In `JITCompiler.InitializeCallvirtDispatch` (~line 2107), add an interface branch
FIRST (before the IL/CLR branches), because an interface method is an `ILMethod`
whose declaring `ILType.IsInterface` is true:

```csharp
void InitializeCallvirtDispatch(ref OpCodeR op, IMethod targetMethod)
{
    int slot = -1;
    if (targetMethod is ILMethod ilMethod)
    {
        ILType declaringILType = ilMethod.DeclearingType as ILType;
        if (declaringILType != null && declaringILType.IsInterface)
        {
            // NEW: interface dispatch
            declaringILType.EnsureNeoInterfaceMap();                // build iface map on the INTERFACE type
            // resolve slot of ilMethod WITHIN the interface (0-based):
            int ifaceMethodSlot = declaringILType.GetInterfaceMethodSlotSelf(ilMethod);
            op.Code = OpCodeREnum.Callvirt_Interface;
            op.Operand4 = EncodeCallvirtInterface(ifaceMethodSlot, 0); // thisArgOffset filled at call site like Step 10
            return;
        }
        if (declaringILType != null && declaringILType.TryGetNeoVTableSlot(ilMethod, out slot))
        {
            op.Code = OpCodeREnum.Callvirt_IL;                       // unchanged Step 10
            op.Operand4 = EncodeCallvirtDispatch(slot, 0);
        }
        else
        {
            op.Code = OpCodeREnum.Callvirt;                          // unchanged Step 10
            op.Operand4 = EncodeCallvirtDispatch(slot, 0);
        }
    }
    else if (targetMethod is CLRMethod clrMethod) { ... unchanged ... }
}
```

`GetInterfaceMethodSlotSelf(ilMethod)` returns the 0-based slot of `ilMethod`
within the interface type's own `MethodSlotKeys` (the interface's view of itself
is a trivial identity map built once). The runtime handler then translates that
interface-local slot into the implementing type's class VTable via
`GetInterfaceVTableOffset`.

**Note on the interface's self-map:** building the offset map for the *implementing*
type enumerates each interface's methods; that same enumeration gives the
interface's 0-based method ordering. `GetInterfaceMethodSlotSelf` can be a lazy
`Dictionary<string,int>` on the interface `ILType` (interface FullName/SignatureString
→ slot), built from `GetMethods()` filtered to declared candidates. This is the
"interface method slot" the opcode encodes.

### Decision 7: Edge cases

- **Object not implementing the interface** — handler throws
  `MissingMethodException` with the runtime type and interface name (Decision 5).
- **Missing / null slot** — handler bounds-checks `classSlot` and the entry, throws
  the same exception type (Decision 5). No ip overrun (we `throw`, not `continue`),
  no null-deref (we check before indexing), no Legacy fallback (Neo-only opcode).
- **Explicit interface implementation** — handled by the per-method `ClassSlotRemap`
  fallback in `BuildNeoInterfaceMap` (Decision 3). If the matcher cannot resolve it,
  the slot is left absent and the handler throws — visible, not silent.
- **Multiple interfaces** — each gets its own `InterfaceEntry` + offset (Decision 3).
- **Interface inheritance chain** — `BuildNeoInterfaceMap` walks the interface
  graph (each `impl.Implements` recursively), assigning offsets to parent
  interfaces too, so a call through a parent-interface variable resolves.
- **IL class implements CLR interface (IL side)** — the interface's `IType` is a
  `CLRType` (or `CrossBindingAdaptor` wrapper per `InitializeInterfaces`).
  `TryGetInterfaceVTableOffset` keys on `IType` identity, so a CLR interface key
  works as long as the same `IType` instance is used at JIT and runtime (it is —
  both come from `AppDomain` resolution). The implementing method is the IL method
  found in `neoVTable`, so dispatch lands on IL code. The reverse direction (CLR
  calling the IL object through the CLR interface) is OUT of scope.
- **CLR `this` through an interface variable** — the handler's
  `thisObj is ILTypeInstance` check fails; we throw `InvalidOperationException`
  rather than silently mis-dispatching. (The generic `Callvirt` arm already covers
  mixed IL/CLR `object`-typed dispatch for the cases Step 10 handles.)

## Risks / Trade-offs

- **`SignatureString` matcher limitations** (Step 10 TODO, design doc §13.5.1) —
  generic interface methods, `ref`/`out` byref params, and cross-model
  CLR/IL matching may not all resolve correctly via string keys.
  → Mitigation: unresolved slots surface as an explicit `MissingMethodException`
  at the handler (Decision 5), never silent mis-dispatch. The structured matcher
  is explicitly deferred and tracked in Step 10's TODO; Step 11 does not widen its
  scope.
- **Non-contiguous class VTable slots for one interface** (rare; caused by
  explicit interface impl or base-class-provided methods) → Mitigation: the
  `ClassSlotRemap` fallback (Decision 3) keeps correctness; the common case stays
  on the fast `VTableOffset + k` path.
- **Lazy build reentrancy** → Mitigation: `neoInterfaceMapBuilding` guard mirrors
  the proven Step 10 `neoVTableBuilding` guard.
- **Interface identity stability across JIT/runtime** → both resolve the interface
  `IType` through `AppDomain`, so identity is stable within a process. The future
  `.neo` AOT format will need a stable numeric interface-type index; deferred to
  Step 23.
- **No `_Neo` opcode suffix** → consistent with Step 10's `Callvirt_IL` /
  `Callvirt_CLR`. If the broader codebase later standardizes on a `_Neo` suffix,
  a single rename under `#if ENABLE_NEO_MODE` localizes the change.

## Migration Plan

- All additions are behind `#if ENABLE_NEO_MODE`; Legacy mode is untouched, so
  there is no behavioral migration for existing users.
- Rollout order (one implementer pass): data structure + builder
  (`ILType.cs`) → opcode enum entry → JIT lowering + optimizer call-ABI pass →
  interpreter handler + resolver → tests → build (`Debug_Neo` CLI, `Debug`
  TestCases) → run `NeoStep` smoke + Step 10 regression.
- Rollback: revert the single change; `Callvirt_Interface` simply stops being
  emitted and interface callvirt reverts to the generic `Callvirt` arm (the
  pre-Step-11 behavior). No persistent state to clean up.

## Open Questions

- Whether `GetInterfaceMethodSlotSelf` should live on `ILType` (current plan) or be
  precomputed once per interface type and cached as a static / domain-level map.
  Current plan (lazy per-ILType dictionary) is simplest and matches Step 10's
  per-instance lazy style; revisit only if memory shows up as a concern.
- Exact exception type for the failure scenarios. `MissingMethodException` matches
  what `ResolveNeoCallvirtILTarget` already throws (line 230, 235), so we keep it
  for consistency. The spec only requires "a clear exception".
