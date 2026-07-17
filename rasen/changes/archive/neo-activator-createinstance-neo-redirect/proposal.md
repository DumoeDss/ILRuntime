# Proposal -- neo-activator-createinstance-neo-redirect

## Why
Under Neo mode, `System.Activator.CreateInstance(...)` on an IL type throws
`MissingMethodException: No parameterless constructor for ILTypeInstance`.
Legacy PASSES the same tests. This is child 22 of the `neo-overhaul` portfolio.

The two triage-flagged regression tests (`ActivatorCreateInstanceTest
.ActivatorCreateInstanceWithArgsTestSimple` and `...WithArgsTest`) both fault,
and the Activator surface is used broadly downstream (`ArrayTest`,
`GenericMethodTest`, `InheritanceTest`, `ReflectionTest` all call
`Activator.CreateInstance`).

## Root cause (confirmed real, Neo-specific; triage-batch-1 Candidate C)
`AppDomain.cs` registers the hand-written redirects for Activator on Legacy's
`RedirectMap` ONLY:
- generic `Activator.CreateInstance<T>()` -> `CLRRedirections.CreateInstance`
- `Activator.CreateInstance(Type)` -> `CLRRedirections.CreateInstance2`
- `Activator.CreateInstance(Type, object[])` -> `CLRRedirections.CreateInstance3`

NONE is registered on `RedirectMapNeo`. Under Neo, dispatch consults
`RedirectMapNeo` exclusively (`CLRMethod.TryGetRedirection`, child-2 durable
finding), so the call falls through to the autogen stub
`System_Activator_Binding.CreateInstance_0_Neo`, whose body calls the host
`System.Activator.CreateInstance<ILTypeInstance>()`. The host cannot find a
public parameterless ctor on `ILTypeInstance` (it only has a `protected`
parameterless ctor and a `public ILTypeInstance(ILType, ...)` ctor) ->
`MissingMethodException`. The Type-based autogen stubs
(`CreateInstance_1_Neo` / `CreateInstance_2_Neo`) have the same shape: they
call the host `Activator.CreateInstance(ILRuntimeType, ...)`, which cannot
construct the underlying IL type. The whole Activator-under-Neo surface for IL
types is broken by the same missing-redirect root cause.

This is the SAME defect class child 6 (`neo-clr-vt-reffields-binder`) fixed for
`RuntimeHelpers.InitializeArray`: that method was given a Neo redirect
(`CLRRedirections.InitializeArrayNeo`) and registered on `RedirectMapNeo`.

The hand-written Legacy redirects (`CLRRedirections.cs:30-136`) correctly
discriminate IL type vs CLR type and call `ILType.Instantiate()` /
`ILType.Instantiate(object[])` for IL types -- that is why Legacy works. The
Neo fix reproduces those semantics in the Neo calling convention.

## Why the autogen stubs are ruled out as the fix target
The autogen `System_Activator_Binding` stubs are generated for SPECIFIC generic
instantiations (`<ILTypeInstance>`, `<Adaptor>`, `<TestVector3>`, `<Object>`)
and the two Type overloads. They are per-instantiation and call the host
`System.Activator` directly, which fundamentally cannot construct an IL type
(the host knows nothing of `ILType`). Patching each stub would be a losing
battle. The hand-written redirects key on the GENERIC METHOD DEFINITION and
read the IL type off `method.GenericArguments` at runtime -- one redirect
serves every instantiation. The fix is to give those hand-written redirects a
Neo-signature counterpart, exactly as child-6 did for `InitializeArray`.

## What changes
1. Add three Neo-signature redirects to `CLRRedirections.cs` under
   `#if ENABLE_NEO_MODE`, mirroring the signature/structure of
   `InitializeArrayNeo` / `DelegateCombineNeo`:
   - `CreateInstanceNeo` -- generic `Activator.CreateInstance<T>()`: read
     `method.GenericArguments[0]`; for an IL type call `ILType.Instantiate()`,
     for a CLR type call `CLRType.CreateDefaultInstance()`; write the result
     object to the dest ref slot.
   - `CreateInstance2Neo` -- `Activator.CreateInstance(Type)`: read the Type
     param via `ReadNeoReference`; for `ILRuntimeType` call
     `ILType.Instantiate()`, else host `Activator.CreateInstance(type)`; write
     the result (null -> Neo null sentinel `-1`).
   - `CreateInstance3Neo` -- `Activator.CreateInstance(Type, object[])`: read
     the Type and `object[]` params; null-check each arg (Legacy throws
     `ArgumentNullException`); for `ILRuntimeType` call
     `ILType.Instantiate(object[])`, else host
     `Activator.CreateInstance(type, args)`; write the result.
   A small shared helper `WriteNeoObjectResult` writes the created object to
   the dest ref slot (non-null -> `mStack[retRefBase] = result;
   *(int*)retDst = retRefBase`; null -> `*(int*)retDst = -1`, the Neo null
   sentinel established by `ReadNeoReference` / `InvokeNeoClrMethod`).
2. Register each on `RedirectMapNeo` in the `AppDomain` ctor (right after the
   existing Legacy `RegisterCLRMethodRedirection` calls for the same methods),
   under `#if ENABLE_NEO_MODE`, mirroring the `InitializeArrayNeo` /
   `DelegateCombineNeo` registrations.
3. Add `TestCases/NeoStepActivatorCreateInstanceTest.cs` with a probe that
   calls `Activator.CreateInstance(typeof(AnILType))` (and the generic +
   args overloads) and asserts the result -- MUST FAULT
   (`MissingMethodException`) on HEAD, PASS after.

## Precedence (why the generic-definition redirect preempts the autogen stubs)
`CLRMethod.TryGetRedirection` (`CLRMethod.cs:116-119`) resolves a generic
INSTANTIATION by first looking up `def.GetGenericMethodDefinition()`, and only
if that misses, the specific `def`. So a redirect registered for the generic
DEFINITION takes precedence over the autogen stub registered for the specific
instantiation. For the non-generic Type overloads, `RegisterCLRMethod
RedirectionNeo` is first-registered-wins (`!ContainsKey`), and the AppDomain
ctor runs BEFORE the test-harness autogen binding initializer -- so the
hand-written Neo redirect wins there too. (This is the same first-registered-
wins argument the Step-20 async builder redirects rely on, recorded as a
`neo-dispatch` requirement.)

## Scope / deferral
- The generic `Activator.CreateInstance<T>()` value-type branches (Legacy
  `AllocValueType`) are NOT reached by any current smoke test (grep confirms
  no `CreateInstance<TestVector3>` / `CreateInstance<object>` call sites exist;
  the autogen `<TestVector3>` stub is itself a broken TODO that writes nothing).
  The Neo redirect reproduces the IL-type-vs-CLR-type discrimination and
  produces the instance via `Instantiate()` / `CreateDefaultInstance()`; for
  the untested VT case it writes the produced box as a reference (no worse than
  the broken autogen stub it replaces). A faithful Neo VT-allocation path
  (Legacy `AllocValueType` has no Neo `byte*` equivalent) is out of scope.
- Legacy is untouched: the hand-written Legacy redirects stay on `RedirectMap`;
  this change only ADDS to `RedirectMapNeo` under `#if ENABLE_NEO_MODE`.

## Capability home
`neo-dispatch` (owns `RedirectMapNeo` registration / Neo CLR-redirect wrappers;
same capability home child-6/Step-20 used for the InitializeArray / async
builder Neo redirects). ADDED requirement: a Neo redirect for
`System.Activator.CreateInstance`. Neo-gated => Legacy-neutral by construction.
