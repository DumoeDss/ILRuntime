# Durable findings: neo-ilruntimetype-runtime-bridge (Wave-2 C12) -- DONE

## Result
- **Full-smoke delta: 133 -> 122 (-11).** All 10 C12 tests flipped green
  (DelegateTest25/28/29/30/31/32/33/34/35 + EnumTest21) + 1 collateral test.
- NeoStep **380/0** (no regression).
- Legacy-neutral: DelegateTest25 + EnumTest21 PASS on plain Debug+useRegister=true
  (fix is 100% `#if ENABLE_NEO_MODE`).

## Pinned root cause (ONE bridge missing, two API surfaces)
The hand-written Legacy redirects that bridge `ILRuntimeType` -> the IL delegate adapter /
`ILEnumTypeInstance` were registered on Legacy's `RedirectMap` ONLY:
- `DelegateCreateDelegate/2/3` (CLRRedirections.cs:1607/1666/1733)
- `EnumToObject` (CLRRedirections.cs:1509)

Under Neo, `RedirectMapNeo` is consulted EXCLUSIVELY (child-2 lineage). With no Neo entry,
dispatch fell through to:
- Delegate.CreateDelegate: the reflection fallback `CLRMethod.Invoke` (stack:
  CLRMethod.cs:583 -> ILIntepreter.Neo.cs:1256) -> framework `Delegate.CreateDelegate(
  ILRuntimeType, ...)` -> `Type must be a runtime Type object`.
- Enum.ToObject: the autogen `ToObject_3_Neo` stub (System_Enum_Binding.cs:173) ->
  framework `Enum.ToObject(ILRuntimeType, ...)` -> `Type must be a type provided by the
  runtime`.

**SAME defect class as child-6 (InitializeArrayNeo) and child-22 (CreateInstanceNeo):
"hand-written Legacy redirect on RedirectMap only; needs a Neo-signature twin on
RedirectMapNeo."** Neo-vs-Legacy evidence: DelegateTest25 + EnumTest21 FAIL on Neo, both
PASS on Legacy (Legacy runs the bridge redirect via RedirectMap).

## The fix (Neo-gated -> Legacy-neutral)
4 new Neo redirects in `CLRRedirections.cs` (params read in DECLARATION order via the Neo
cursor; result via existing `WriteNeoObjectResult`):
- `DelegateCreateDelegateNeo` (Type, MethodInfo)  -- CLRRedirections.cs:799
- `DelegateCreateDelegate2Neo` (Type, object, string)  -- :850
- `DelegateCreateDelegate3Neo` (Type, object, MethodInfo)  -- :907
- `EnumToObjectNeo` (Type, int)  -- :985

Registered on `RedirectMapNeo` in the AppDomain ctor (AppDomain.cs:282/307/312/317).
First-registered-wins (`RegisterCLRMethodRedirectionNeo`'s `ContainsKey` guard) -> this
ctor runs BEFORE the test-harness `CLRBindings.Initialize` autogen `Register`, preempting
the autogen `ToObject_3_Neo` stub and any autogen Delegate.CreateDelegate stub.

## Key implementation details (for future children)
- **Neo param order is DECLARATION order (forward), NOT stack-reverse.** Legacy
  `StackObject` reads `esp-1` (last param) first; Neo `ReadNeoReference`/`ReadNeoInt32`
  read param 0 first, advancing `curPrim` by 4 each. For Delegate.CreateDelegate(Type,
  MethodInfo): read Type THEN MethodInfo (the reverse of the Legacy read order).
- **Primitive vs reference params:** a reference param (Type/MethodInfo/object/string) is
  read via `ILIntepreter.ReadNeoReference` (4-byte mStack index); a primitive int param is
  read via `ILIntepreter.ReadNeoInt32` (4-byte direct value). Both advance `curPrim += 4`.
  See autogen `ToObject_3_Neo` (System_Enum_Binding.cs:177-178) for the mixed ref+int
  pattern.
- **ILEnumTypeInstance under Neo stores its value as raw bytes in `Primitives` (a public
  `byte[]` property = the protected `fields`), NOT the Legacy `StackObject[]` with the
  `ins[0]=val` indexer (that indexer is `#if !ENABLE_NEO_MODE`).** Write the int as the
  underlying-type bytes: `fields.Length==8 ? BitConverter.GetBytes((long)val) :
  BitConverter.GetBytes(val)` + `Buffer.BlockCopy` (sign-extended for long-backed enums,
  truncated for byte/short). `Primitives` is get-only but returns the array reference, so
  BlockCopy into it mutates the enum value in place.
- **WriteNeoObjectResult (null-aware, -1 sentinel) is the correct result writer for these
  redirects** (the result is an object/adapter consumed by `as <DelegateType>`). Do NOT use
  WriteNeoDelegateResult ( Combine/Remove semantics; not null-aware).

## Exit-127 note
The full smoke exits 127 (the known pre-existing teardown crash, NRE in a Dictionary
enumerator per planning-context). The summary line `Ran 914 tests, 122 failded` WAS
emitted, so the count is complete and authoritative (914 == ground-truth total). The exit
code is not a mid-run crash.

## Verification trail (all REAL runs)
- Pre-fix Neo: DelegateTest25 = `Type must be a runtime Type` (1 failed); EnumTest21 =
  `Type must be a type provided by the runtime` (1 failed). [stash-toggle on-HEAD]
- Post-fix Neo: DelegateTest25/28/29/30/31/32/33/34/35 + EnumTest21 = all 0 failed.
- NeoStep 380/0.
- Full smoke: 133 -> 122 (post-fix log: `.tmp-c12-postfix.log`).
- Legacy (plain Debug+useRegister=true): DelegateTest25 + EnumTest21 = 0 failed.
