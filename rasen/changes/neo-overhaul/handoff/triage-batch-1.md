# Triage Batch 1 -- neo-overhaul follow-up re-audit

> Leaf-worker winnowing re-audit of 3 framed/latent follow-ups. Per-item VERDICT
> with evidence. Baseline confirmed on HEAD `695379db`: NeoStep smoke **358/0**.
> No engine source modified. Temporary probes removed after each probe.

Tally: **A = DISPROVEN**, **B = UNREACHABLE**, **C = DISPROVEN-as-framed but a
DIFFERENT real+tractable Neo gap surfaced (Activator missing RedirectMapNeo
registration, same class as child-6 InitializeArray).**

---

## Candidate A: `neo-il-static-field-roundtrip` -- VERDICT: DISPROVEN

**Framed gap:** "an IL-static field runtime round-trip (write+read) returns
garbage on BOTH HEAD and FIXED -- a separate IL-static field-storage gap."
Child 13 (`neo-il-static-ref-field-readback`) FIXED `TestStaticFieldInstance`
via the newobj dest/arg-aliasing root cause. Re-audit whether a round-trip gap
STILL exists post-child-13.

**Probe + evidence:**
1. Existing `SimpleTest.TestStaticFieldInstance` (the IL-static ref-field
   lazy-init pattern, `new TestA("testerror")` via a static getter) run under
   Neo: **PASS**. Output `testerror,error test` (the ctor arg round-tripped
   faithfully through the static field).
2. Fresh probe `NeoStepILStaticRoundTrip_TC1_Primitive` -- `static int` field,
   write 1337 then read back and assert: **PASS** (2/2 with TC2).
3. Fresh probe `NeoStepILStaticRoundTrip_TC2_Reference` -- `static string`
   field, write a plain local string (NOT a newobj-aliased arg) then read back
   and assert ref-identity: **PASS**.

Both the primitive and the reference IL-static round-trips are faithful on HEAD.
The static Stsfld/Ldsfeld arms write/read `sinst.ManagedObjects[off]` /
`Primitives` correctly -- consistent with child-13's "the static arms are
CORRECT; the bug was newobj dest/arg aliasing" finding.

**Verdict: DISPROVEN.** Child-13 closed the IL-static field round-trip. No
child. Spec-delta-only note: the IL-static storage path is verified correct for
both primitive and reference fields post-child-13.

**Next action if REAL:** n/a.

---

## Candidate B: `neo-clr-vt-refcount-stobjldobj` (child-5 F1) -- VERDICT: UNREACHABLE

**Framed gap:** "the Stobj/Ldobj arm computes `refCount=0` for CLR structs
(ilType null) -> a CLR struct WITH managed ref fields copied via stobj/ldobj has
untracked refs (latent missed-GC-root)."

**Code re-audit (the Stobj/Ldobj arms, `ILIntepreter.Neo.cs:5531/5634`):**
Confirmed the framing's mechanism description is literally accurate:
`int refCount = ilType != null ? ilType.TotalReferenceCount : 0;` (line 5538 for
Stobj, 5641 for Ldobj). For a CLR struct `ilType` is null, so `refCount=0` and
the `if (refCount > 0)` ref-region copy block (Step 17b) is skipped. The
primitive bytes are copied by `Unsafe.CopyBlock(..., (uint)primSize)`.

**BUT the materialization is gated by an earlier NIE.** A CLR struct WITH a
reference field and no registered ValueTypeBinder cannot be materialized as a
frame value -- the Step-13 Area-4b guard in `CLRMethod.Invoke` fires first.

**Probe + evidence:**
Fresh probe `NeoStepClrVtRefStobjLdobj_TC1_CopyAndReadback` -- construct
`TestClrStructWithRef(42, "copy-ref")` (CLR struct with a `string` field), then
copy it via a generic `GenericCopy<T>(ref T dst, ref T src) { dst = src; }`
(which forces the C# compiler to emit ldobj/stobj for an unbounded generic T),
then read back `.n` and `.s`.

Result under Neo: **the NIE fires at the CONSTRUCTOR call, before the copy.**
```
System.NotImplementedException: CLR value-type `this` with reference fields and
  no ValueTypeBinder (Step 13 Area 4b): register a binder. Type:
  ILRuntimeTest.TestFramework.TestClrStructWithRef
   at ILRuntime.CLR.Method.CLRMethod.Invoke(...) in CLRMethod.cs:line 393
   at ILRuntime.Runtime.Intepreter.ILIntepreter.InvokeNeoClrMethod(...) :line 1011
```
The ctor's value-type `this` (with ref fields) is rejected at `CLRMethod.Invoke`
(line 391-393: `NeoClrStructHasReferenceField(thisClr.TypeForCLR)` -> tagged
NIE). The struct never enters a frame local, so the stobj/ldobj `refCount=0`
CopyBlock is never reached for a ref-field CLR struct carrying a live ref.

**Reachability sweep (other materialization paths):**
- `default(TestClrStructWithRef)` (zero-init, no ctor) does NOT NIE, but every
  field is null/zero -- there is no live GC root to lose; CopyBlock of zero
  bytes is harmless.
- A CLR-method-RETURN of a ref-field struct routes through `WriteNeoValueType`
  (`ILIntepreter.Neo.cs:1067`, return-value path) which has NO ref-field guard
  -- so a returned ref-field struct WOULD materialize. HOWEVER in that case the
  GC pointer is stored as RAW managed bytes in the Primitives region (the whole
  point of `WriteNeoValueType` = `Unsafe.WriteUnaligned` of the struct), and the
  stobj/ldobj `CopyBlock(..., primSize)` copies those raw bytes (pointer
  included). The pointer is not in a separate ref region that `refCount=0`
  skips -- it is inside the copied primitive bytes. So no root is LOST in the
  copy. (The deeper, separate issue -- the GC not tracking the pointer while it
  lives as raw bytes in any ref-field CLR struct local -- is NOT specific to
  stobj/ldobj and is out of scope of this framed gap.)

**Verdict: UNREACHABLE / DISPROVEN.** The primary materialization path (CLR
struct ctor with ref fields) NIEs at `CLRMethod.Invoke:393` BEFORE any
stobj/ldobj copy. The framed "missed-GC-root during the stobj/ldobj copy"
cannot occur: either the struct never materializes (ctor NIE), or it is all-zero
(no root), or (return path) the live pointer lives inside the CopyBlock'd
primitive bytes and is copied with them. No child.

**Next action if REAL:** n/a. (Note: the existing `NeoStep13bTest
.NeoStep13_ClrStructWithRefFieldNIE` probe already documents this NIE boundary;
its comment places the NIE at `.SumLength()` but the NIE actually fires earlier
at the ctor -- the probe's broad try/catch masks this. Cosmetic comment fix only.)

---

## Candidate C: `neo-activator-createinstance-nre` -- VERDICT: DISPROVEN-as-framed; DIFFERENT real+tractable gap surfaced

**Framed gap:** "Pre-existing Activator NRE
(`System_Activator_Binding.CreateInstance_0_Neo -> ILType.GetStaticFieldOffset`),
unmasked after ldtoken stopped NIE-ing ~60x. Scope the NRE: what type is being
activated, what's the NRE root, is it Neo-specific or pre-existing in Legacy
too, is it a quick binding fix?"

### The framed NRE does NOT reproduce

Run `ActivatorCreateInstanceTest` under Neo (both `ActivatorCreateInstanceWith
ArgsTestSimple` and `ActivatorCreateInstanceWithArgsTest`): **the error is a
`MissingMethodException`, NOT an NRE at `ILType.GetStaticFieldOffset`.** Zero
`GetStaticFieldOffset` frames in the stack. The framed gap's error type and
stack are wrong for HEAD.

Actual stack (both failures, identical):
```
System.MissingMethodException: No parameterless constructor defined for type
  'ILRuntime.Runtime.Intepreter.ILTypeInstance'.
   at System.RuntimeType.CreateInstanceOfT()
   at System.Activator.CreateInstance[T]()
   at ILRuntime.Runtime.Generated.System_Activator_Binding.CreateInstance_0_Neo(
        ...) in System_Activator_Binding.cs:line 141
   at ILRuntime.Runtime.Intepreter.ILIntepreter.InvokeNeoClrMethod(...) :line 1007
   at ILRuntime.Runtime.Intepreter.ILIntepreter.ExecuteNeo(...) :line 2991
```
(`GetStaticFieldOffset` is reachable only when an ILTypeInstance is actually
constructed -- ILTypeInstance.cs:79. But `Activator.CreateInstance<ILType
Instance>()` fails at host `RuntimeType.CreateInstanceOfT()` because
ILTypeInstance has only a `protected ILTypeInstance()` and a
`public ILTypeInstance(ILType,...)` -- no public parameterless ctor -- so
construction never starts and `GetStaticFieldOffset` is never hit. The framed
NRE path is unreachable on HEAD.)

### It IS Neo-specific (Legacy passes)

Same `ActivatorCreateInstanceTest` under Legacy (CLI built plain `Debug`,
`useRegister=true`): **2/2 PASS.** Instances are created with correct default
values. So this is a Neo regression vs Legacy, not pre-existing.

### Root cause (found) -- missing RedirectMapNeo registration (child-6 class)

`AppDomain.cs:162-176` registers HAND-WRITTEN redirects for Activator:
- line 166: generic `Activator.CreateInstance<T>()` -> `CLRRedirections.CreateInstance`
- line 170: `Activator.CreateInstance(Type)` -> `CLRRedirections.CreateInstance2`
- line 174: `Activator.CreateInstance(Type, object[])` -> `CLRRedirections.CreateInstance3`

ALL THREE are registered via `RegisterCLRMethodRedirection` (Legacy's
`RedirectMap`) ONLY. NONE is registered on `RedirectMapNeo`. Contrast line 158
(`RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.InitializeArrayNeo)`) --
that one IS on Neo (child-6 added it for the SAME defect class).

The hand-written `CLRRedirections.CreateInstance` (`CLRRedirections.cs:30-45`)
correctly handles IL types: `if (t is ILType) { ... ((ILType)t).Instantiate() ... }`.
That is why Legacy works.

Under Neo, the established rule applies ("Neo dispatch uses RedirectMapNeo
exclusively; a Legacy redirect does NOT run under Neo"). With the hand-written
redirect absent from RedirectMapNeo, the generic
`Activator.CreateInstance<ActivatorCreateInstanceTestClass>()` (IL type arg,
mapped to the `ILTypeInstance` host instantiation) falls through to the autogen
`System_Activator_Binding.CreateInstance_0_Neo` (`System_Activator_Binding.cs:
137-150`). That autogen body calls the host
`System.Activator.CreateInstance<ILRuntime.Runtime.Intepreter.ILTypeInstance>()`
-- a broken stub (the planning context already flags "autogen Neo CLR bindings
are often broken default stubs"). The host Activator cannot find a public
parameterless ctor on ILTypeInstance -> `MissingMethodException`.

The Type-based overloads (`CreateInstance_1_Neo` / `CreateInstance_2_Neo`) have
the same shape: they call host `Activator.CreateInstance(@type)` on an
`ILRuntimeType`, which cannot create the underlying IL type -- so the whole
Activator-under-Neo surface for IL types is broken by the same missing-redirect
root cause. (Reachable beyond these 2 tests: `ArrayTest`, `GenericMethodTest`,
`InheritanceTest`, `ReflectionTest` all call `Activator.CreateInstance` too.)

### Verdict + next action

**Verdict:** The FRAMED NRE (`GetStaticFieldOffset`) is **DISPROVEN** -- it does
not reproduce on HEAD; the actual error is a `MissingMethodException` from a
broken autogen `CreateInstance_0_Neo` stub. BUT a DIFFERENT, REAL, Neo-specific,
tractable gap is confirmed: the hand-written Activator redirects
(`CLRRedirections.CreateInstance/2/3`) are missing from `RedirectMapNeo`, so
`Activator.CreateInstance` on IL types falls through to the broken autogen stub.
This is the same defect class child-6 fixed for `RuntimeHelpers.InitializeArray`.

**Next action if REAL (scope sketch for a child):** Add Neo-signature
equivalents of `CLRRedirections.CreateInstance` / `CreateInstance2` /
`CreateInstance3` (Neo calling convention: `void(ILIntepreter, byte*, AutoList,
CLRMethod, bool, byte*, int)`) and register them on `RedirectMapNeo` in
`AppDomain` ctor (mirror child-6's `InitializeArrayNeo` registration at line
158). The Neo bodies read the generic arg / Type param via `ReadNeoReference`,
then `ilType.Instantiate()` for IL types (or `ilType.Instantiate(args)` for the
2-arg overload) and push the result ref; for CLR types fall through to host
`Activator.CreateInstance`. Capability home = `neo-dispatch` (owns RedirectMapNeo
registration / Neo CLR-redirect wrappers; same as child-6). Probe MUST FAULT on
HEAD (MissingMethodException) and PASS after (assert default values + ctor-arg
round-trip via the existing `ActivatorCreateInstanceTestClass`). Expected to
turn the 2 Neo Activator failures green and unblock downstream Activator users
in the full smoke.

---

## Summary table

| Cand | Framed gap | Verdict | Evidence | Next action |
|------|-----------|---------|----------|-------------|
| A | IL-static round-trip garbage | DISPROVEN | TestStaticFieldInstance PASS; fresh int+string round-trip probes 2/2 PASS | none (spec-delta note only) |
| B | stobj/ldobj refCount=0 loses CLR-struct ref root | UNREACHABLE | ctor of ref-field CLR struct NIEs at CLRMethod.Invoke:393 BEFORE any copy; return-path pointer lives in CopyBlock'd primitive bytes | none |
| C | Activator NRE at GetStaticFieldOffset | DISPROVEN-as-framed (NRE does not reproduce); REAL different gap = missing Neo Activator redirects | Neo 2/2 MissingMethodException vs Legacy 2/2 PASS; root cause = CLRRedirections.CreateInstance/2/3 on Legacy RedirectMap only, not RedirectMapNeo (child-6 class) | scope a child: Neo Activator redirects on RedirectMapNeo (mirror child-6) |

DONE
