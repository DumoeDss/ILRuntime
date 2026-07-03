# Planning Context — implement-neo-step11

This file seeds the persistent planner. Read this FIRST, then research only
what is missing. Append durable new findings (decisions, discovered
constraints) at the end after each propose unit.

## 1. User intent (verbatim)

> "auto-decompose 你来推进step11，不用停下，直到任务完成"

Translation: drive Neo **Step 11 (interface method dispatch / 接口分派)** to
completion autonomously via the openspec autopilot; do not pause at gates
until the task is done. Step 10 (VTable + callvirt) is already complete;
Step 11 is the next step.

## 2. Decompose decision (LEAD, already taken)

**SKIP decompose.** Step 11 is a single coherent, reviewable slice. The four
work items (interface offset map, Callvirt_Interface instruction, JIT/Optimizer
lowering, tests) are sequential dependencies sharing the same files — not
independent deliverables. No positive independence proof → serial → therefore
one change. Pipeline reduces to: propose → apply → verify → review-loop →
ship → archive on THIS change.

## 3. Authoritative spec draft (migrate into this change)

The Step 11 spec is ALREADY written in openspec delta format at:
`.trae/specs/implement-neo-step11/spec.md`

It contains: Why / What Changes / Impact / ADDED Requirements (interface
offset map, interface callvirt instruction, Neo JIT lowering) / MODIFIED
Requirements (Neo VTable dispatch) / REMOVED (none). Migrate this into
`openspec/changes/implement-neo-step11/specs/<capability>/spec.md`.

Suggested capability folder naming (planner finalizes, keep consistent with
any existing openspec/specs/ — currently EMPTY so you are creating them):
- NEW capability for Step 11: `neo-interface-dispatch` (ADDED requirements)
- MODIFIED touches Step 10's capability: `neo-vtable-dispatch` (MODIFIED requirement "Neo VTable dispatch")

If a single capability folder is cleaner, put both ADDED + MODIFIED under one
folder (e.g. `neo-dispatch`) — your call, just keep it internally consistent
and referenced correctly in proposal.md Impact.

## 4. Codebase findings — Step 10 foundation (DO NOT re-derive; build on this)

Read `.trae/documents/neo-implementation-steps.md` (Step 10 + Step 11
sections) and `.trae/documents/object-model-neo-design.md` for the design.
Key symbols already in place from Step 10 (in `ILRuntime/CLR/TypeSystem/ILType.cs`):

- `IMethod[] neoVTable` — the class virtual table (slot -> implementing method).
- `Dictionary<string,int> neoVTableSlots` — SignatureString -> slot index.
- `string[] neoVTableSlotKeys` — parallel slot -> key.
- `bool neoVTableBuilding` — recursion guard.
- `public IMethod[] NeoVTable` (lazy `EnsureNeoVTable()`).
- `internal bool TryGetNeoVTableSlot(IMethod method, out int slot)`.
- `void EnsureNeoVTable()` / `void BuildNeoVTable()` — builds by inheriting
  base type's slots first, then adding this type's virtual candidates.
- `AddNeoBaseVirtualSlots(...)`, `AddNeoVTableSlot(...)`,
  `static bool IsNeoVTableCandidate(IMethod)`, `static string GetNeoVTableSlotKey(MethodReference)`.
- Slot key currently uses `IMethod.SignatureString` (transitional; design doc
  flags this — Step 11 should reuse the SAME key scheme as Step 10, do not
  invent a new matching scheme).

Callvirt dispatch (in `ILRuntime/Runtime/Intepreter/RegisterVM/ILIntepreter.Neo.cs`):
- `static IMethod ResolveNeoCallvirtILTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)`
- `static CLRMethod ResolveNeoCallvirtCLRTarget(...)`
- OpCodeREnum already has `Callvirt_IL` / `Callvirt_CLR` (Step 10). Step 11
  adds `Callvirt_Interface` (and a Neo variant if the convention used one).

Step 11 design target (from `neo-implementation-steps.md` Step 11):
```
baseSlot = instance.Type.GetInterfaceVTableOffset(interfaceTypeIndex)
actualMethod = vtable[baseSlot + interfaceMethodSlot]
```
i.e. NEW on ILType: an interface dispatch map giving, per implemented
interface, the STARTING vtable slot offset; interface methods get their own
0-based slot within each interface; runtime resolves offset+slot into the
class vtable (NOT raw interface slot as class slot).

## 5. Hard constraints (from CLAUDE.md — obey exactly)

- Branch `features/object-model-overhaul`, conditional compilation:
  `ENABLE_NEO_MODE` (Neo on) vs Legacy. Neo code lives behind `#if ENABLE_NEO_MODE`.
  Do NOT mix the two object models.
- **Build (sln CANNOT build whole):**
  - CLI: `dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo` (transitively builds ILRuntime/ILRuntimeTestBase/LitJson, 0 errors).
  - TestCases DLL: `dotnet build TestCases/TestCases.csproj -c Debug` (-> TestCases/bin/Debug/netstandard2.1/TestCases.dll).
- **Run tests (NOT xUnit; custom reflection framework):**
  ```
  dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
    TestCases/bin/Debug/netstandard2.1/TestCases.dll \
    HotfixAOT/Patched/HotfixAOT.patch true NeoStep
  ```
  Always pass `-f net8.0` to `dotnet run`. `Debug_Neo` prints lots of
  JIT/optimizer output (OUTPUT_JIT_RESULT macro) — normal. Build CLI with
  Debug_Neo but NEVER build TestCases with Debug_Neo.
- Tests = `public static` parameterless methods in `TestCases/` (optionally
  `[ILRuntimeTest]`). Follow the existing `NeoStep<N>Test.cs` convention —
  add `TestCases/NeoStep11Test.cs`. Smoke filter `NeoStep` hits ~26 cases;
  all-green = environment healthy.
- Unimplemented instructions throw `NotImplementedException` tagged with a
  Step number inside `ExecuteNeo` — that is a TODO, NOT a bug. Neo full run
  is ~430/519 failing on purpose (unimplemented ops); only NeoStep smoke
  matters day-to-day. Legacy full run is the green regression baseline.
- A test taking >10s usually means an interpreter infinite loop.
- **CJK write caveat:** the Write tool corrupts ~0.5% of CJK characters to
  U+FFFD on large payloads. Prefer ASCII in files you author; if you must
  write large CJK, validate/fix with a pure-ASCII PowerShell codepoint check
  afterward. Small CJK snippets are safe.

## 6. Design decisions already settled (do not relitigate)

- Interface dispatch reuses Step 10's class vtable (IMethod[] neoVTable) — it
  does NOT build a separate interface method array. It adds an OFFSET MAP so
  `vtable[offset + interfaceSlot]` lands on the implementing method.
- Each interface owns an independent 0-based method slot namespace.
- IL class implementing a CLR interface (e.g. IDisposable) via the IL side is
  in scope; the CLR-exposed CrossBindingAdapter path is explicitly OUT of
  scope for Step 11 (spec scenario notes this).
- Reuse Step 10's SignatureString slot-key scheme for interface method slot
  identity (consistency; the richer structured-signature matcher is a later
  shared concern, not Step 11).

## 7. What the planner must produce (propose stage)

Invoke `openspec-propose` (Skill tool) OR author directly in openspec format.
Required artifacts under `openspec/changes/implement-neo-step11/`:
- `proposal.md` — Why / What Changes / Impact.
- `design.md` — concrete design: InterfaceEntry/interfaceMap structure on
  ILType, how offsets are computed at type-load/JIT, the Callvirt_Interface
  opcode operands + ExecuteNeo handler, JITCompiler/Optimizer lowering rules,
  error/edge cases (missing interface, explicit iface impl, multi-interface,
  interface inheritance chain).
- `specs/<capability>/spec.md` — ADDED/MODIFIED delta (migrate from .trae draft).
- `tasks.md` — checkbox implementation tasks sized for one implementer pass.

Keep proposals concrete and grounded in the real Step 10 symbols above.
Author != verifier: you ONLY propose; you do not implement or review.

## 8. Append-only findings log

(planner appends durable decisions/constraints discovered during propose here)

### Step 11 propose (2026-07-03)

**Capability folder chosen:** single combined `neo-dispatch` (one folder holds
both the Step 11 ADDED requirements and the MODIFIED "Neo class virtual dispatch"
requirement that Step 10 would own). `openspec/specs/` was empty, so this change
CREATES the `neo-dispatch` capability. proposal.md Impact reflects this.

**Opcode name:** `Callvirt_Interface` (no `_Neo` suffix — consistent with Step 10's
`Callvirt_IL` / `Callvirt_CLR`). Added to `OpCodeREnum` right after `Callvirt_CLR`
(~line 1010 of `OpCodes/OpCodeREnum.cs`).

**ILType symbols decided (all under `#if ENABLE_NEO_MODE`):**
- `struct InterfaceEntry { IType InterfaceType; int VTableOffset; string[] MethodSlotKeys; int[] ClassSlotRemap; }`
- `InterfaceEntry[] neoInterfaceMap`
- `Dictionary<IType,int> neoInterfaceOffsets`
- `bool neoInterfaceMapBuilding` (recursion guard, mirrors `neoVTableBuilding`)
- `public int GetInterfaceVTableOffset(IType)` / `public bool TryGetInterfaceVTableOffset(IType, out int)`
- `public bool TryGetInterfaceMethodSlot(IType, IMethod, out int)`
- `public int GetInterfaceMethodSlotSelf(IMethod)` (0-based slot of a method within its own interface type; lazy `Dictionary<string,int>` on the interface ILType)
- `void EnsureNeoInterfaceMap()` / `void BuildNeoInterfaceMap()` (lazy, calls `EnsureNeoVTable()` first)

**Operand encoding decided:** reuse the Step 10 `OpCodeR` layout —
`Operand2` = declared interface-method token hash (so the handler recovers the
interface via `AppDomain.GetMethod(Operand2).DeclearingType`), `Operand4` packs
`(thisArgOffset << 16) | (interfaceMethodSlot & 0xffff)` via a new
`EncodeCallvirtInterface(slot, thisOffset)` helper (same bit layout as
`EncodeCallvirtDispatch`, separate name). `Operand3` stays `targetRetRefBase`.
Call ABI is byte-identical to the `Callvirt_IL` arm. No global interface-type-
index table is introduced for Step 11 (deferred to Step 23 `.neo` format).

**Constraint discovered:** `InitializeCallvirtDispatch` (JITCompiler.cs ~2107) is
the lazy JIT point where the interface map gets built (first interface callvirt
JIT of a type triggers `EnsureNeoInterfaceMap`). The interface branch MUST be
added FIRST in that method — before the IL/CLR branches — because an interface
method is an `ILMethod` whose `DeclearingType.IsInterface` is true, so the
existing IL branch's `!declaringILType.IsInterface` guard would otherwise swallow
it (line 2113).

**Constraint discovered:** the handler's `ReadNeoCallThis` (ILIntepreter.Neo.cs
~207) already null-checks `this`; `ResolveNeoCallvirtInterfaceTarget` adds an
`is ILTypeInstance` guard that throws `InvalidOperationException` for CLR `this`
through an interface (out-of-scope direction). The generic `Callvirt` arm still
covers mixed IL/CLR `object`-typed dispatch for Step 10's cases.

**Failure-mode contract (locked in spec):** missing interface / missing slot →
`MissingMethodException` (matches what `ResolveNeoCallvirtILTarget` already
throws, lines 230/235). No ip overrun, no null-deref, no Legacy fallback.

### Step 11 apply (2026-07-03) — IMPLEMENTER findings

**Design assumption that proved WRONG (had to extend):** design Decision 1/3 assumed
"the implementing method for an interface method is already a class virtual (or an
override of one)" and thus already in `neoVTable`. This is FALSE for C# implicit
interface implementation: a plain `public string Foo()` implementing `IFoo.Foo()`
is non-virtual, so `IsNeoVTableCandidate` (which requires `Definition.IsVirtual`)
rejects it and it never enters `neoVTable`. The interface offset map then had no
class slot to point at → `IndexOutOfRangeException` at dispatch.

**Fix applied (contained, behind `#if ENABLE_NEO_MODE`):** added
`EnsureNeoInterfaceImplementorSlots(...)` to `BuildNeoVTable` (ILType.cs). After the
normal candidate loop, it walks this type's interface graph (`Implements` + parent
interfaces) and, for any interface-method key NOT already in `slotMap`, resolves a
matching instance method on this type by `SignatureString` (`FindNeoImplementingMethod`)
and gives it a fresh `neoVTable` slot. This keeps "interface dispatch indexes the
existing `neoVTable`" (Decision 1) true. Low risk to Step 10: it only ADDS slots
for methods that were previously absent; `Callvirt_IL` resolution is unaffected
(those methods aren't virtual, so they're never the target of a `Callvirt_IL`).

**Second discovery — interface declared methods have no CompiledFrame.ParamInfos:**
in `Optimizer.Neo.cs` `LowerNeoOffsets`, the ILMethod branch read
`paramInfos = ilm.CompiledFrame.ParamInfos`. For an interface (abstract) declared
method `ParamInfos` is null, so the call-param map block was skipped and
`op.Operand` (the `NeoCallParams` index) was never set → handler got
`callParamIdx = -1`. Fixed by falling back to a new
`AllocNeoParamInfosFromSignature(targetMethod, isNewobj, domain)` that synthesizes
a contiguous callee param layout from the declared signature (mirrors the CLRMethod
branch; `hasThis`/`newobj` get slot 0). Concrete impl shares the signature so the
argument copy is valid.

**Third discovery — derived class does not redeclare base interfaces:**
`NeoStep11OverrideDerived : NeoStep11OverrideBase` (base implements the interface)
has an empty `Implements`, so `BuildNeoInterfaceMap` did not know it implements the
interface. Fixed: `BuildNeoInterfaceMap` now seeds its `entries`/`offsets` from the
base ILType's interface map (base offsets are valid in the derived vtable because
`BuildNeoVTable` inherits base slots at the same indices; the override replaces the
slot content, which the inherited offset still points at). `seen` is seeded from
the inherited offsets to avoid re-adding.

**Non-contiguous/remap handling (Decision 3 fallback):** realized as a dedicated
`internal bool TryResolveNeoInterfaceClassSlot(IType, int interfaceMethodSlot, out int classSlot)`
on ILType. The handler (`ResolveNeoCallvirtInterfaceTarget`) calls this instead of
the design's inline `baseSlot + slot` so the `ClassSlotRemap` fallback is honored
transparently. `VTableOffset` is anchored at the min resolved class slot (or 0 if
none resolved) when non-contiguous.

**JIT prewarm dropped:** design Decision 6 sketched
`declaringILType.EnsureNeoInterfaceMap()` at JIT time, but `declaringILType` IS the
interface (which has no class VTable — `BuildNeoVTable` skips interface types), so
that call would build a meaningless map. Removed it; only `GetInterfaceMethodSlotSelf`
is needed at JIT (encodes the interface-local slot). The real offset map is built
lazily at runtime on the implementing type when the handler calls
`TryResolveNeoInterfaceClassSlot`.

**`thisArgOffset` parity (task 3.3):** confirmed Step 10's
`EncodeCallvirtDispatch(slot, 0)` never patches the high 16 bits afterward — `this`
is always the first call argument, so `thisArgOffset` stays 0 and `ReadNeoCallThis`
reads it from `targetBase + 0`. `EncodeCallvirtInterface(slot, 0)` is byte-identical;
no call-site patching is needed for `Callvirt_Interface` either.

**Negative test (task 6.7) could not be expressed as a PASSING case:** the custom
test harness has no `[ExpectedException]` facility and treats any uncaught exception
as a test failure; IL `try/catch` needs `Leave_S` (Step 6, not implemented). The
"clear exception on missing interface" contract is therefore enforced by handler
code (`ResolveNeoCallvirtInterfaceTarget` throws `MissingMethodException` / validates
`is ILTypeInstance`) and verified by review, NOT by a runnable test. The negative
types remain in `NeoStep11Test.cs` for a manual check once Step 6 lands. The 5
runnable NeoStep11 cases (single iface, override, multi-iface, inheritance chain,
CLR IDisposable) all pass.

**Smoke results:** NeoStep filter → Ran 31 tests, 0 failed (was ~26; +5 NeoStep11).
NeoStep10 filter → Ran 5 tests, 0 failed (no Step 10 regression).
