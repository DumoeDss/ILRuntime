# Review Report -- neo-activator-createinstance-neo-redirect (child 22)

**Reviewer:** independent (reviewer-1). **Verdict: APPROVE-WITH-FINDINGS.**

No Blockers. No Majors. Two Minor (documented-divergence) notes and two
Trivial notes. The core correctness claims are all independently verified:
the Neo calling convention is right, the IL-vs-CLR discrimination reproduces
the hand-written Legacy redirects, the generic-definition-precedence lever
is real and correctly applied, the 3 probes FAULT on HEAD and PASS after
(stash-toggle reproduced), and the full NeoStep smoke is **361/0** with no
overfire. Legacy is byte-identical to HEAD (100% `#if ENABLE_NEO_MODE`).

---

## What was verified (all PASS)

### 1. Calling-convention correctness -- PASS

- `ReadNeoReference` (`ILIntepreter.Neo.cs:123-136`) returns
  `idx >= 0 ? mStack[idx] : null`, advancing `curPrim += 4` per param, in
  declaration order. Confirmed.
- `CreateInstanceNeo` (generic): reads NO frame param (the type is the
  generic arg). Matches Legacy `CreateInstance` (`CLRRedirections.cs:30-59`,
  also reads no `esp` param) and the autogen `_0/_3/_4/_5_Neo` generic stubs
  (all set `__curPrim = 0` then create without reading). Correct.
- `CreateInstance2Neo` (Type): one `ReadNeoReference` (param 0). Matches
  Legacy `CreateInstance2` (`esp - 1`) and autogen `_2_Neo`.
- `CreateInstance3Neo` (Type, object[]): two `ReadNeoReference` calls in
  declaration order -- param 0 = Type, param 1 = object[]. Matches Legacy
  `CreateInstance3` (`esp-2` = Type, `esp-1` = object[]) AND the autogen
  `_1_Neo` (reads Type then object[] identically). The `object[]` is a
  reference (Array), so `ReadNeoReference` is the right reader; no VT
  marshal is involved. Correct.
- `WriteNeoObjectResult` null convention: non-null ->
  `mStack[retRefBase] = result; *(int*)retDst = retRefBase`; null ->
  `*(int*)retDst = -1`. This is BYTE-IDENTICAL to `InvokeNeoClrMethod`'s
  null-return path (`ILIntepreter.Neo.cs:1082-1094`:
  `if (res == null) *(int*)retDstPtr = -1; else { mStack[targetRetRefBase]=res;
  *(int*)retDstPtr = targetRetRefBase; }`). The `-1` null sentinel is the
  right convention -- it is what `ReadNeoReference`, `Ldnull`, `Cgt_Un`
  (`:1305-1308`, keys on `!= -1`), and the autogen Ldelem_Ref null encoding
  all assume. The stale `mStack[retRefBase]` on the null branch is unused
  (the caller reads the `-1` index and never indexes mStack). Correct.

### 2. IL-vs-CLR discrimination + Instantiate semantics -- PASS (with one documented VT exception, see M2)

- Generic: IL type -> `ILType.Instantiate()`; CLR type ->
  `((CLRType)t).CreateDefaultInstance()`. `CLRType.CreateDefaultInstance`
  (`CLRType.cs:1029-1040`) is `Activator.CreateInstance(TypeForCLR)` -- so
  for CLR reference types the Neo redirect is functionally identical to the
  autogen host-`Activator` stubs. IL discrimination matches Legacy.
- `CreateInstance2Neo`: `ILRuntimeType` -> `ILType.Instantiate()`; else host
  `Activator.CreateInstance(t)`; null Type -> null sentinel. Matches Legacy
  `CreateInstance2` (`CLRRedirections.cs:76-92`) line-for-line.
- `CreateInstance3Neo`: `ILRuntimeType` -> `ILType.Instantiate(object[])`;
  else host `Activator.CreateInstance(t, args)`. `ILType.Instantiate(object[])`
  exists (`ILType.cs:3171`). Matches Legacy `CreateInstance3`
  (`CLRRedirections.cs:110-136`).
- The TC3 probe round-trips ctor args (`IntValue==777`,
  `StringValue=="activator-arg"`) -- proves the `Instantiate(object[])`
  path selects the right ctor and populates fields.

### 3. Registration precedence -- PASS

- `CLRMethod.TryGetRedirection` (`CLRMethod.cs:111-131`): for a generic
  INSTANTIATION (`IsGenericMethod && !IsGenericMethodDefinition`) it tries
  `def.GetGenericMethodDefinition()` FIRST, then `def`. So a redirect
  registered for the open generic definition preempts every per-instantiation
  stub. **Verified against the autogen binding**: `System_Activator_Binding.cs`
  registers `CreateInstance_0/_3/_4/_5_Neo` via `MakeGenericMethod(args)`
  (specific instantiations: `<ILTypeInstance>`, `<Adaptor>`,
  `<TestVector3>`, `<Object>`) -- NONE for the open definition. The
  hand-written `CreateInstanceNeo`, registered for `i` where
  `i.IsGenericMethodDefinition` (AppDomain ctor `:176`), preempts all of
  them. Claim confirmed.
- `RegisterCLRMethodRedirectionNeo` (`AppDomain.cs:1134-1153`) is
  first-registered-wins (`!ContainsKey`). The AppDomain ctor runs before
  the test-harness autogen `System_Activator_Binding.Register`, so the
  hand-written `CreateInstance2Neo`/`CreateInstance3Neo` preempt the autogen
  `_2_Neo`/`_1_Neo` for the non-generic Type overloads. Same first-registered-
  wins guarantee `InitializeArrayNeo` (AppDomain ctor `:158`) and the
  Step-20 async-builder redirects rely on. Claim confirmed.
- `RedirectionNeo` (`CLRMethod.cs:145-155`) consults `RedirectMapNeo`
  exclusively. Neo dispatch never falls back to `RedirectMap`. So the
  hand-written Legacy redirects on `RedirectMap` are NOT consulted under Neo
  -- confirming the root cause (no Neo entry -> autogen stub).
- **Legacy untouched**: the three `RegisterCLRMethodRedirection(i, ...)`
  Legacy calls (`AppDomain.cs:166/181/191`) remain unconditional; only the
  `RegisterCLRMethodRedirectionNeo` siblings are `#if ENABLE_NEO_MODE`. The
  three Neo redirect methods + `WriteNeoObjectResult` are all inside the
  `#if ENABLE_NEO_MODE` block (`CLRRedirections.cs:652-752`, closing `#endif`
  at `:752`). No Legacy regression possible. Confirmed.

### 4. `ActivatorCreateInstanceWithArgsTest` out-of-scope scoping -- honestly scoped (PASS)

Independently reproduced: running `ActivatorCreateInstance` (name filter)
under the fix yields 5 ran / 1 failed. The single failure is
`ActivatorCreateInstanceWithArgsTest` with
`System.NullReferenceException: Neo callvirt this is null` at
`ActivatorCreateInstanceTest.cs:28` -- i.e. inside `ToString()` at the
ternary `ILValue == null ? "null" : ILValue.ToString()`. This is the
`neo-ceq-null-sentinel` gap (a reference field stored as a valid mStack
index pointing to null compares index-vs-`(-1)` under ceq, so `ref == null`
reads FALSE, then `ref.ToString()` is dispatched on null ->
`ReadNeoCallThis`/`ResolveNeoGenericCallvirtTarget` NRE at
`ILIntepreter.Neo.cs:1177`). The `this:` dump shows the instance WAS created
with correct default field values (`IntValue=0, StringValue=null,
FloatValue=0, ILValue=null`) -- i.e. `CreateInstanceNeo` -> `Instantiate()`
succeeded; the crash is purely the downstream `== null` ceq path.
`ActivatorCreateInstanceWithArgsTestSimple` (no ToString) PASSES. The
Activator redirect itself is complete and correct; the residual failure is
genuinely the separate ceq-null gap. Scoping is honest.

### 5. Probe strength -- PASS

- HEAD (stash-toggle, engine files stashed, probe kept): **3 ran / 3 failed**.
  All three fault with `System.MissingMethodException: Constructor on type
  'ILRuntime.Runtime.Intepreter.ILTypeInstance' not found`, routed through
  `System_Activator_Binding.CreateInstance_1_Neo` (line 171) -> host
  `Activator.CreateInstance(@type, @args)`. This is the exact triage error
  and the exact autogen stub the change targets.
- Fix restored: **3 ran / 0 failed**.
- Probes are observably-wrong (deliberate `1/0` DivByZero faults on null /
  wrong-value), not just ran-without-throwing. No try/catch swallows the
  HEAD fault. Coverage spans all three overloads: generic (`TC1`),
  `Type` (`TC2`), `Type+object[]` (`TC3`).

### 6. No-overfire / no regression -- PASS

- Full NeoStep smoke: **361 ran / 0 failed** (independently re-run).
- Generic-definition-precedence shadow analysis (the powerful lever):
  the one redirect now intercepts every `Activator.CreateInstance<T>()`.
  The autogen stubs it shadows are: `_0_Neo` `<ILTypeInstance>` (broken ->
  now `CreateDefaultInstance`, still can't build ILTypeInstance, but that
  call site is nonsensical and was already broken), `_3_Neo` `<Adaptor>`
  (host Activator -> now `CreateDefaultInstance` -> `Activator.CreateInstance`,
  equivalent), `_4_Neo` `<TestVector3>` (broken TODO writing nothing -> now
  produces a boxed VT, an improvement), `_5_Neo` `<Object>` (equivalent). No
  harmful shadowing; the broken stubs improve and the working ones stay
  equivalent. The smoke (361/0) confirms no NeoStep test regressed.
- For the non-generic Type overloads, the hand-written redirects shadow the
  autogen `_1_Neo`/`_2_Neo`; for CLR types both call host Activator
  (equivalent), for IL types the hand-written one routes through Instantiate
  (the fix). No overfire.

---

## Findings

### Minor

**M1. `CreateInstance3Neo` diverges from Legacy on a null `args` array
(this-PR).** `CLRRedirections.cs:735` guards the per-element null check with
`if (args != null)`, then proceeds to `Instantiate(args)` / host
`Activator.CreateInstance(t, args)` when `args` is null. The mirrored Legacy
`CreateInstance3` (`CLRRedirections.cs:119`) has NO such guard -- it does
`t2.Length`, which NullReferenceExceptions if `t2` is null. So:
- `args == null, t != null` -> Legacy NRE; Neo proceeds (with a null array).
This is unreachable in the current smoke (no test passes a null args array)
and the Neo behavior is arguably more robust, but it is a behavioral
divergence from the Legacy redirect being mirrored. Recommend: either drop
the guard to match Legacy exactly, or add a one-line comment documenting
that the guard is an intentional Neo-side hardening (so the divergence is
not "fixed" away later by someone chasing parity).

**M2. Value-type `CreateInstance<T>()` paths are not faithfully reproduced
(this-PR, documented deferral).** `CreateInstanceNeo` calls `Instantiate()`
for ALL IL types and `CreateDefaultInstance()` for ALL CLR types. Legacy
`CreateInstance` (`CLRRedirections.cs:38-52`) special-cases (a) IL value
types -> `AllocValueType`, and (b) CLR types with a registered ValueTypeBinder
-> `AllocValueType`. So for an IL VT or a CLR VT-with-binder, the Neo
redirect produces a different representation (a boxed instance written as a
reference) than Legacy (a VT-allocated slot). This is faithfully documented
in `proposal.md` (Scope/deferral) and `design.md`, and is UNREACHABLE in the
current smoke (grep confirms no `CreateInstance<VT>` call sites; the autogen
`<TestVector3>` stub it replaces is itself a broken TODO). Not a regression
(the path was broken under Neo before). Noting for completeness: this is the
one place the "reproduces Legacy semantics exactly" claim has a known,
scoped exception, and a future faithful Neo VT-allocation path
(`AllocValueType` has no Neo `byte*` equivalent) is the cleanup.

### Trivial

**T1. tasks.md narrative slightly overstates `WithArgsTest` coverage
(this-PR, docs only).** tasks.md says "the Activator.CreateInstance calls
[now] all succeed ... the instance is created with correct field values" for
`ActivatorCreateInstanceWithArgsTest`. In fact that test fails at the FIRST
`Console.WriteLine($"Create without args: {inst}")` (`ActivatorCreateInstance
Test.cs:50`, the ToString ceq-null crash) and never reaches the
`CreateInstance(Type, object[])` calls at lines 62/75. The
`CreateInstance3Neo` (Type+object[]) path is instead covered by the TC3
probe, which passes with correct arg round-trip -- so coverage is adequate;
only the tasks.md narrative is imprecise. No action required beyond the
note.

**T2. `WriteNeoObjectResult` vs `WriteNeoDelegateResult` duplication
(this-PR, optional).** The new helper is `WriteNeoDelegateResult`
(`CLRRedirections.cs:642-648`) plus the null-sentinel branch. The
duplication is JUSTIFIED -- `DelegateCombineNeo` can legitimately yield
null-as-a-valid-index (a Combine can produce a real null delegate stored at
a valid slot), so making `WriteNeoDelegateResult` null-aware would be WRONG;
two distinct helpers is the correct design. Flagging only as a code-smell
note; no action needed.

### Pre-existing (NOT this PR; out of scope, recorded for the lead)

**P1. `neo-ceq-null-sentinel` gap.** A reference field/local stored as a
valid mStack index pointing to a null value compares as "not null" under
`ceq`/`cgt.un` (which key on the 4-byte index `!= -1`). This is the residual
`ActivatorCreateInstanceWithArgsTest` failure (`ToString` at line 28) and is
separately PROPOSED as a different child. This PR does not touch it and
correctly scopes it out.

**P2. Autogen `CreateInstance_4_Neo` (`<TestVector3>`) is a broken TODO
(`System_Activator_Binding.cs:281-287`, writes nothing).** Pre-existing. This
PR's generic-definition redirect now shadows it under Neo, so it is moot in
Neo mode; it remains broken under the autogen-only path. Not this PR's
concern.

---

## Verdict: APPROVE-WITH-FINDINGS

The change is correct on every exercised path. The calling convention, the
IL/CLR discrimination, the generic-definition + first-registered-wins
precedence, the null-sentinel return convention, the Legacy-neutral gating,
and the probe strength are all verified sound. The NeoStep smoke is
independently reproduced at **361/0**; the stash-toggle FAIL->PASS (3/3 ->
0/3) is airtight; the residual `WithArgsTest` failure is honestly scoped to
the unrelated ceq-null gap. The two Minor findings (M1 null-args divergence,
M2 documented VT deferral) are non-blocking notes for the implementer; M1 is
the only one I'd suggest acting on (a one-line comment or a parity tweak),
and even that is unreachable in the smoke.
