# neo-iltypeinstance-parameterless-ctor (Wave-2 C5)

## Status: DONE (implementation complete; verified against a REAL full Neo smoke)

## Root cause (Neo-vs-Legacy, pinned with a stack trace)
`No parameterless constructor defined for type 'ILRuntime.Runtime.Intepreter.ILTypeInstance'`.

All 7 cluster failures route through `LitJson.JsonMapper.ToObject<T>(string)` (JsonTest3/4/6/8/9
deserialize directly; GenericMethodTest4 reaches it via `Command<T>.Decode()` -> `ToObject<T>`).

Observed Neo stack (HEAD, `.tmp-c5-json3.log`):
```
System.MissingMethodException: No parameterless constructor ... ILTypeInstance.
   at System.RuntimeType.CreateInstanceDefaultCctor(...)
   at LitJson.JsonMapper.ReadValue(Type inst_type, JsonReader reader)  JsonMapper.cs:504
   at LitJson.JsonMapper.ToObject[T](String json)                      JsonMapper.cs:1024
   at LitJson_JsonMapper_Binding.ToObject_0_Neo(...)                   (AUTOGEN)
   at ILIntepreter.InvokeNeoClrMethod(...)
   at ILIntepreter.ExecuteNeo(...)
```

Mechanism:
- `ReadValue` already special-cases IL types (JsonMapper.cs:501-502):
  `if (value_type is ILRuntimeType) instance = ((ILRuntimeType)value_type).ILType.Instantiate();
   else instance = Activator.CreateInstance(value_type);`
- The IL type reaches `ReadValue` only when the hand-written redirect `JsonToObject`
  passes `method.GenericArguments[0].ReflectionType` (an ILRuntimeType). That redirect is
  registered by `JsonMapper.RegisterILRuntimeCLRRedirection` via `RegisterCLRMethodRedirection`
  -> Legacy `redirectMap` ONLY.
- Neo dispatch uses `RedirectMapNeo` exclusively (child-2). With no Neo entry, the call falls to
  the AUTOGEN stub `LitJson_JsonMapper_Binding.ToObject_0_Neo`, which hardcodes the generic arg to
  `ILTypeInstance` (the IL type's `TypeForCLR`) and calls the raw CLR `ToObject<ILTypeInstance>`.
  So `ReadValue` sees `value_type == typeof(ILTypeInstance)` (NOT an ILRuntimeType) -> the
  `else` branch -> `Activator.CreateInstance(typeof(ILTypeInstance))` -> MissingMethodException
  (ILTypeInstance's parameterless ctor is `protected`, not public).
- The IL-type-name -> ILTypeInstance keying is why the error names the literal CLR backing type.
  SAME defect class as child-22 (Activator) and child-6 (InitializeArray): a Legacy-only redirect
  that Neo never consults, so the broken autogen stub wins.

This is NOT the Activator overload edge the task hypothesized, NOR a distinct JSON-deserializer
root. It is a single root: the `ToObject<T>` redirect is missing on `RedirectMapNeo`.

## The fix (Neo-gated, Legacy-neutral, +76 lines, single file)
File: `LitJson/JsonMapper.cs`
- Added three Neo-signature redirects `JsonToObjectNeo` / `JsonToObjectNeo2` / `JsonToObjectNeo3`
  (one per `ToObject<T>` overload: string / JsonReader / TextReader), each mirroring the Legacy
  `JsonToObject`/2/3 body: read the single reference param via `ILIntepreter.ReadNeoReference`,
  resolve `method.GenericArguments[0].ReflectionType`, call `ReadValue(type, ...)`, write the
  object result. Result write inlined as `WriteNeoJsonResult` (null -> -1 sentinel, matching
  `ReadNeoReference`; `CLRRedirections.WriteNeoObjectResult` is private/engine-internal so it is
  re-implemented here).
- Registered all three on `RedirectMapNeo` inside `RegisterILRuntimeCLRRedirection` (under
  `#if ENABLE_NEO_MODE`), for the generic DEFINITION `i` (`i.IsGenericMethodDefinition`).
  `CLRMethod.TryGetRedirection` tries `GetGenericMethodDefinition()` FIRST, so a single
  definition-level registration preempts every autogen per-instantiation `ToObject_*_Neo` stub
  for every `T` (the generic-definition-precedence lever; child-22 / child-6 lineage). Registration
  runs in `ILRuntimeHelper.Init` (helper.cs:195) BEFORE `CLRBindings.Initialize` (the autogen
  `Register`), so first-registered-wins also holds.

LitJson.csproj defines `ENABLE_NEO_MODE` only for Debug_Neo/Release_Neo (not plain Debug), so the
entire addition is compiled out under Legacy -> Legacy-neutral by construction.

## Verification (truth = full-smoke number)
- Stash-toggle (airtight): stash `LitJson/JsonMapper.cs`, rebuild CLI Debug_Neo, run JsonTest3 ->
  FAIL (`MissingMethodException ... ILTypeInstance`, identical to HEAD grounding); pop, rebuild ->
  PASS. Proves the flip is caused by this change.
- Name-filter after fix: JsonTest3 PASS, GenericMethodTest4 PASS, JsonTest4/6/8 PASS.
- NeoStep smoke: **382 / 0 failed** (no regression).
- FULL Neo smoke BEFORE (HEAD via stash, `.tmp-c5-fullsmoke-before.log`): **916 ran, 110 failed**
  (matches the task baseline exactly; 24 string-occurrences of "parameterless constructor").
- FULL Neo smoke AFTER (with fix, `.tmp-c5-fullsmoke-after.log`): **916 ran, 104 failed** ->
  **delta 110 -> 104, drop of 6**. Same test count, single variable (stash-toggle = airtight
  causation); 0 "parameterless constructor" occurrences remain. Final line:
  `Ran 916 tests, 104 failded, 20 ignored, 7 todos`.
  Flipped green (all via the ToObject<T> dispatch fix):
    GenericMethodTest4, JsonTest3, JsonTest4, JsonTest6, JsonTest8 (the 5 C5 ToObject tests),
    + JsonTest2 (bonus; was C16 NRE -- `ToObject<List<MyTestDataItem>>` now routes through the
      ILRuntimeType path and passes).
  ZERO `parameterless constructor` occurrences remain in the entire full smoke (C5 root eliminated).
- Legacy-neutral: plain Debug + useRegister=true, JsonTest filter -> **9 ran / 0 failed / 1 todo**
  (JsonTest9 PASSES on Legacy, confirming the residual Neo failure below is Neo-specific).

## Surfaced follow-up (out of scope for C5; distinct root)
`JsonTest9` flipped from MissingMethodException (C5, fixed) to a downstream
`NullReferenceException` at `Dictionary<string,B>.Enumerator.MoveNext()` via the autogen
`System_Collections_Generic_Dictionary_2_String_ILTypeInstance_Binding_Enumerator_Binding.MoveNext_1_Neo`.
The deserialization now succeeds (the C5 fix worked); the residual is the Neo value-type-with-ref-
field enumerator marshalling bug (the local-var inspector reports "Enumerator (flat-bytes struct
with ref field; inspection skipped)"). SAME class as `GCTest.TestDicEnumerator` (C16) and the
child-8/child-15 VT-with-ref-field discussion -- a Neo flat-byte VT slot holding GC refs as mStack
indices. NOT the parameterless-ctor root; it was simply masked by the earlier MissingMethodException.
It passes on Legacy. Recommended: a focused Neo VT-with-ref-field enumerator child (C16 sub-cluster).
