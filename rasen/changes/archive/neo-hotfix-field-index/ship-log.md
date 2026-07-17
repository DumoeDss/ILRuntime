# Ship Log -- neo-hotfix-field-index (wave-2 cluster H)

## Status: DONE (fix shipped locally; LEAD commits)

## Summary
Fixed HotfixBasicTestCases.Test04 + Test05 (the 2 cluster-H "DEEP residual" tests left by
the prior `neo-hotfix-patched-il-execute` child). Both shared ONE mechanism: the Neo
ILTypeInstance.PushToStack/AssignFromStack eval-stack bridge used INSTANCE-field
accessors (TotalFieldCount / GetFieldOffset / GetField) unconditionally, but the eval-stack
Stsfld/Ldsfld/Ldtoken ILType branches call `t.StaticInstance.PushToStack/AssignFromStack(
fieldIdx)` where fieldIdx is a STATIC-field index.

## Root cause (Neo-vs-Legacy)
- NEO: bridge bounds-checked a static fieldIdx against the INSTANCE count and resolved it
  through INSTANCE offset/type accessors -> field index out of range TypeLoadException.
- LEGACY: ILTypeStaticInstance ctor sizes `fields = StackObject[StaticFieldTypes.Length]`
  and the bridge bounds-checks `fields.Length` (the static count), indexing
  `managedObjs[fieldIdx]` directly -> a static fieldIdx is always in range; Legacy PASS.
- Test05 had a SECOND layer (child-6 lineage): the PrivateImplementationDetails `.size N`
  blob field has TPS=0/TRC=0 -> Neo static instance's ManagedObjects is null -> the byte[]
  blob was never materialised. Fix = source the byte[] from Cecil
  FieldDefinition.InitialValue (the child-6 pattern, now on the eval-stack path too).

## Fix (Neo-gated, Legacy-neutral, ONE file, +44/-7)
`ILRuntime/Runtime/Intepreter/ILTypeInstance.cs` -- both PushToStack and AssignFromStack
in the `#else`/ENABLE_NEO_MODE block:
- `bool isStatic = this is ILTypeStaticInstance;`
- `fieldCount = isStatic ? type.StaticFieldTypes.Length : type.TotalFieldCount;`
- `off = isStatic ? type.GetStaticFieldOffset(fieldIdx) : type.GetFieldOffset(fieldIdx);`
- `ft  = isStatic ? type.StaticFieldTypes[fieldIdx] : type.GetField(fieldIdx, out _);`
- PushToStack only: static blob first-check -> `type.StaticFieldDefinitions[fieldIdx].
  InitialValue` (non-empty) -> push the byte[] (child-6 lineage; read-only, no write-side
  handling needed).

## Verify (truth = full-smoke number)
- Stash-toggle (git stash ILTypeInstance.cs ONLY): Test04 + Test05 -> 1 failed each on
  HEAD -> pop -> 0 failed. Airtight.
- FULL Neo smoke: **23 -> 21** (`Ran 935 tests, 21 failded, 20 ignored, 7 todos`; exit
  127 = known graceful Dict-NRE crash; summary emitted). The 21 are a STRICT SUBSET of the
  23 -- only HotfixBasicTestCases.Test04 + Test05 removed; ZERO new failures (verified by
  diffing the unique `Test name:` failure list against the ground-24 23).
- NeoStep smoke: **401/0** (no regression; the bridge is reached ONLY by the patched-IL
  eval-stack path).
- Legacy-neutral: plain Debug build 0 errors (change is 100% inside the
  `#else`/ENABLE_NEO_MODE block); Legacy Hotfix 9/0.

## Files (NOT committed; LEAD commits)
- `ILRuntime/Runtime/Intepreter/ILTypeInstance.cs` (+44/-7; PushToStack static
  discriminator + blob first-check, AssignFromStack static discriminator).
- `rasen/changes/neo-hotfix-field-index/design.md` (durable findings).
- `rasen/changes/neo-hotfix-field-index/ship-log.md` (this).

## Capability
`neo-value-types` sibling / the Neo-eval-stack object-model bridge. The change is in the
Neo-only bridge block of ILTypeInstance.cs (the patched-IL eval-stack <-> Neo object-model
bridge). No JIT / optimizer / object-model / binding / reflection change.

## Out of scope (honest)
- IL-VALUE-TYPE field (instance OR static) reconstruction via the bridge still NIEs (not
  reached by any current failing test; needs the full box/reconstruct dance).
- CopyToRegister / AssignFromStack(all) / CopyValueTypeToStack / InitializeField remain
  empty no-ops under Neo (not reached by the patched-IL shapes).
