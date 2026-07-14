# design -- neo-interface-callvirt-cluster-c

Wave-2 child of neo-overhaul. fresh-60 cluster C: interface callvirt @
ILIntepreter.Neo.cs 1446/1453 (4 tests). Goal: full Neo smoke 42 -> lower.

## The 4 C-cluster tests at 42 (each FAILS on HEAD, PASSES under Legacy)

1. InheritanceTest_Interface  -- 1453 "TestCls3 does not implement InterfaceTest2 (slot 0)"
2. InheritanceTest_Interface2 -- 1453 "TestA does not implement ITest (slot 0)"
3. Test05.TestGenericStruct    -- 1453 "MM does not implement IInterface (slot 0)"
4. TestAs.TestAs03             -- 1446 "requires ILTypeInstance this (CLR object)"

## Root causes (3 DISTINCT, re-audit-confirmed; interface-type ref-equality is FINE)

### RC1 -- structs never build a Neo VTable (TestGenericStruct)
BuildNeoVTable (ILType.cs) gated the WHOLE population on `!IsValueType`, so a
struct's neoVTableSlots stayed an EMPTY dict. AddNeoInterfaceEntry then resolved
every interface method's classSlot to -1 -> ClassSlotRemap[k]=-1 ->
TryResolveNeoInterfaceClassSlot returns false. Trigger: a boxed struct dispatched
through an interface callvirt (Roslyn `constrained. T; callvirt IFace.M` for
`T : IFace`, e.g. `struct MM : IInterface` with `obj.Check()` in a
`where T : IInterface` generic). Diagnostic confirmed: MM mapLen=1, slotKeys=1,
remapNull=False (ClassSlotRemap=[-1]), isValueType=True, neoVTableSlots empty.

### RC2 -- explicit interface impl unmatched (InheritanceTest_Interface, _Interface2)
FindNeoImplementingMethod matched only by the plain SignatureString. A C# explicit
interface impl `void IFace.M()` compiles to a method named `<IfaceFullName>.<M>`
(dotted), whose SignatureString differs from the interface method's plain `M`.
So EnsureNeoInterfaceImplementorSlots never gave it a VTable slot ->
ClassSlotRemap[k]=-1. Legacy handles this in GetVirtualMethod via a dotted-name
fallback (`{iltype.FullNameForNested}.{method.Name}`). Diagnostic confirmed:
TestCls3/TestA slot 0 -> ClassSlotRemap[0]=-1 (the explicit-impl slot).
NOTE: TestB (has BOTH explicit + public TestMethod) resolves via the plain-name
match to the PUBLIC method ("MethodB") -- this matches Legacy, which also tries
the plain name first.

### RC3 -- CLR-adaptor `this` rejected (TestAs03)
`Dictionary<int,BB>` where `BB : ClassInheritanceTest`(CLR) compiles with
TValue = the CLR `ClassInheritanceTestAdaptor+Adaptor` (BB marshals to its
CLRInstance for CLR storage). dic[0] returns the Adaptor at runtime. The JIT
then ELIDES the `as IAs1` isinst entirely (no Isinst opcode: it tracks TValue
statically as BB, and BB:IAs1 makes the cast statically-always-non-null), so
`ias` holds the Adaptor directly. The interface callvirt handler rejected the
non-ILTypeInstance this at 1446. (Distinct from the delegate-adapter Type==null
shape -- this is a CrossBindingAdaptor reached via a CLR collection.)

## The fix (Neo-gated, Legacy-neutral; mirrors Legacy; 2 files)

### Fix A (RC1+RC2) -- ILType.cs BuildNeoVTable + FindNeoImplementingMethod
- BuildNeoVTable: change `if (!IsValueType && !IsInterface)` to
  `if (!IsInterface)`, and wrap the class-specific base-VTable + own-methods
  logic in an inner `if (!IsValueType)`. EnsureNeoInterfaceImplementorSlots now
  runs for BOTH classes and structs (structs have no base virtual slots, so it
  is the ONLY populator of a struct's VTable).
- FindNeoImplementingMethod: after the plain-SignatureString pass, add an
  explicit-impl fallback that rebuilds the key as
  `{ifaceILType.FullNameForNested}.{method.Name}` + the interface method's exact
  `|gc(params)->ret` suffix, and matches again.

### Fix B (RC3) -- ILIntepreter.Neo.cs ResolveNeoCallvirtInterfaceTarget
- Unwrap a `CrossBindingAdaptorType` this to its ILInstance (established Neo
  pattern, e.g. ldvirtftn :3257); resolve against the implementing IL type.
- Remap the call this to the ILInstance (mStack.Add + write index to
  targetBase+thisArgOff) when the resolved target is an ILMethod (an IL method
  needs an ILTypeInstance this). A CLRMethod target keeps the adaptor this.

## Verify (truth = full-smoke number)
- Name-filter: all 4 PASS after fix.
- Stash-toggle (both engine files): 4/4 FAIL on HEAD -> pop -> 4/4 PASS.
- FULL SMOKE: 42 -> 38 (all 4 flipped; ZERO new failures; diff vs the 39-after-
  RC1+RC2 step shows exactly TestAs03 fixed, nothing else changed).
- NeoStep 398/0 (no regression).
- Legacy-neutral: ILType.cs changes are inside `#if ENABLE_NEO_MODE` (region
  opened at :404); ILIntepreter.Neo.cs is file-gated. Empirical: plain
  Debug + useRegister=true + NeoStep = 398 ran / 18 failed == documented
  pre-existing Legacy baseline; all 4 C-cluster tests PASS under Legacy.

## Capability
neo-dispatch (interface callvirt dispatch / Step 11 interface map). ADDED
requirements: structs populate the interface map; explicit interface impls
resolve; a CrossBindingAdaptor this is unwrapped for interface dispatch.
