# neo-hotfix-patched-il-execute

wave2 fresh-60 cluster H: Hotfix patched-IL via Legacy Execute(StackObject*) (6).

## Outcome
AUDIT + PARTIAL FIX. The patched-IL methods run via the legacy eval-stack
Execute(StackObject*) under Neo (by design: raw eval-stack Op[] body ->
ShouldUseRegisterVM==false -> CanInvokeNeo rejects -> Execute(StackObject*)). The
6 failures were caused by the Neo ILTypeInstance legacy-interpreter bridge being
empty no-ops (PushToStack/AssignFromStack/etc. + Fields=null). Implementing
PushToStack + AssignFromStack(fieldIdx) for Neo (mirroring the Neo indexer arms)
FIXED 4/6. The remaining 2 are deep residuals (patched-type static-field callback
routing in shared eval-stack code; array-initializer `.size N` blob).

## Result (truth = full-smoke number)
- FULL Neo smoke: 46 -> 42 (-4).
- NeoStep: 398/0 (no regression).
- Legacy Hotfix: 14/0 (Legacy-neutral; change is entirely #else/ENABLE_NEO_MODE).

## Files
- `ILRuntime/Runtime/Intepreter/ILTypeInstance.cs` -- implemented Neo PushToStack +
  AssignFromStack(fieldIdx) (previously empty no-ops). Neo-gated.

## NOT DONE (deep, honestly reported)
- Test04: eval-stack Stsfld/Ldsfld ILType branch doesn't consult the ILType
  static-field callback (shared code; would alter Legacy).
- Test05: Ldtoken array-initializer on a `.size N` struct (TotalFieldCount=0);
  child-6 blob-storage lineage (eval-stack path).
