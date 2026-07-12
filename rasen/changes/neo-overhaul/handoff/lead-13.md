# Handoff: neo-overhaul -- LEAD #13 (7-child correctness wave; triage sweeps COMPLETE)

> Read lead-12.md (the neo-overhaul COMPREHENSIVE COMPLETION, 20-child portfolio) for the PRIOR chapter.
> This session drove the lead-12 "Remaining SMALL/LATENT follow-ups" list to completion: 7 MORE real
> correctness children shipped (21-27), 2 triage batches winnowed the list (3 disproven/unreachable/deferred),
> and the full Neo smoke now COMPLETES CLEANLY (902 ran / 192 failed -- no more pre-crash; NIE counts are now
> EXHAUSTIVE). The "framed gaps are disproven" streak (17-for-17) ENDED this session -- child 21 broke it, and
> 7 of the next 8 candidates were REAL.

## Original intent
`/rasen:auto --no-gate ... read rasen/changes/neo-overhaul/handoff/lead-12.md, then continue completing ALL
subsequent tasks.` Full autonomy (--no-gate), commit+push after each clean child, drive DEEP on the 1M window.

## Position
Pipeline `auto-decompose` -> child `small-feature`, Tier A. HEAD **`2fbcef2c`** (pushed, in sync). NeoStep
**375/0** (354 lead-12 baseline + 21 new probes this session, all green). The neo-overhaul portfolio is at
**27 children** (20 from lead-12 + 7 this session), runnableFrontier empty. Both triage batches COMPLETE.

## What shipped this session (7 children, each propose->apply->verify->review->ship, committed+pushed)

### The float-corruption / unseeded-producer class (the recurring shared root cause)
21. **neo-raw-ldfld-stfld-clr-struct-seeding** (`695379db`) -- RE-AUDIT CONFIRMED (broke the 17-for-17 disproven
    streak). `TypeSpecializeNeoOpcodes` seeds `registerTypes[dest]` for typed arithmetic, but TWO producers were
    unseeded (child-16 deferred them): a Call returning primitive float/double/long, and raw `Ldfld` of a CLR-
    struct field. Both -> typed Addi_R4/etc specialization no-oped -> plain integer op on float bits -> garbage.
    Fix = seeding-switch additions (type knowable at JIT time, no marker) + `NeoClrPrimitiveTypeToIType` helper.
    NeoStep 358/0. **THE GENERAL PATTERN: every primitive float/double/long producer must seed registerTypes[dest].**
23. **neo-ceq-null-instance-field** (`7689c27c`) -- the INSTANCE-FIELD form of the null-comparison gap. Re-audit
    DISPROVED the framed mechanism (Ceq_Ref fires fine for the `ceq` form); the REAL gap was the TERNARY
    `(field==null)?a:b` lowering to a DIRECT `brfalse.s` that stays plain because `Ldfld_Ref` had NO seeding case
    (child-11 deferred it). Fix = +5-line `Ldfld_Ref` seeding case (ObjectType, Operand4==0 guard). **BONUS:
    unblocked child-22's residual ActivatorCreateInstanceWithArgsTest P1 for free** (auto-property getter inlines
    to ldfld.ref). NeoStep 365/0. Closes the unseeded-REFERENCE-producer class for the heap instance-field path
    (child-11 Ldsfeld static -> child-21 primitives -> child-23 Ldfld_Ref heap instance).

### The "missing Neo redirect" defect class (child-6 lineage)
22. **neo-activator-createinstance-neo-redirect** (`0aa3b4b7`) -- REFRAMED from the disproven
    `neo-activator-createinstance-nre` (the GetStaticFieldOffset NRE does NOT reproduce). The real gap: Activator.
    CreateInstance on an IL type fell through to the broken autogen stub under Neo -> MissingMethodException.
    Root cause: AppDomain.cs:162-176 registered the hand-written CreateInstance/2/3 (ILType.Instantiate) on Legacy
    RedirectMap ONLY, not RedirectMapNeo. Fix = Neo equivalents + WriteNeoObjectResult helper + register on
    RedirectMapNeo. **THE LEVER: CLRMethod.TryGetRedirection's generic-definition-precedence** -- one Neo redirect
    for the generic def preempts every autogen per-instantiation stub. NeoStep 361/0.

### The CLR-struct-array / byref surface (4 children -- the bulk of this session)
24. **neo-raw-ldfld-array-element** (`5b980cb8`) -- `x = clrStructArray[i].field` silently corrupted (the ldelema
    byref (arrIdx,elementIdx) reinterpreted as flat struct bytes -- NO crash, no NIE). Re-audit REFUTED the
    "runtime detection suffices" hypothesis (a constructible small-int-field + same-typed-array collision) -> a
    JIT marker `NeoRawLdfldArrayElementByRefMarker 0x1` (Operand4, stamped when ins.Previous==Ldelema) is provably
    correct. Runtime array-element READ branch mirrors child-19's Stfld WRITE. NeoStep 368/0.
25. **neo-byref-array-element-marshal** (`123c44f6`) -- `ref arr[i]` byref-param to a CLR method hit the Step-13
    Area-4c NIE. `NeoMarshalByrefFieldToSlot` is the SHARED marshal for BOTH forward call-arg deref AND post-call
    write-back, so ONE Array branch fixes both. Highest-value of triage batch-2 (most pervasive real-world byref).
    Cleanest review (APPROVE). NeoStep 371/0.
26. **neo-ldobj-array-element** (`226ee5c4`) -- `arr2[0] += TestVector3.One` (ldelema; ldobj; op_Addition; stobj)
    hit the ldobj VT-arm NIE. RUNTIME detection SAFE here (operand always a byref, ECMA-335 -- decisive contrast
    with child-24's raw-Ldfld which NEEDED a marker). A += read-modify-write needs BOTH ldobj READ and stobj
    WRITE-back branches. NeoStep 373/0.
27. **neo-raw-stfld-clr-object-vt-field** (`2fbcef2c`) -- `obj.Struct.value=111` (ldflda Struct on a CLR object;
    stfld value) hit the raw-Stfld VT-owner NIE. DIAGNOSIS: the ldflda byref offset = the Struct field's
    FieldInfo.GetHashCode() (resolvable via Area-4d NeoReadClrObjectField/NeoWriteClrObjectField). Fix = box/mutate/
    unbox one level up, runtime-only, no marker (VT-owner Stfld always a byref). COMPLETES triage batch-2. NeoStep
    375/0.

### Triage winnowing (NOT children -- disproven/unreachable/deferred)
- **triage-batch-1** (`triage-batch-1.md`): `neo-il-static-field-roundtrip` DISPROVEN (child-13 closed it);
  `neo-clr-vt-refcount-stobjldobj` UNREACHABLE (ref-field CLR struct NIEs at the ctor before materializing);
  `neo-activator-createinstance-nre` REFRAMED -> child 22.
- **triage-batch-2** (`triage-batch-2.md`): R1-Shape-A blocked behind Step 19 (delegates -- `DelegateExtTest02`,
  delegate Combine/invoke unimplemented); L1 `neo-legacy-ilruntime-type-getenumvalues-dispatch` REAL-but-LOW-VALUE
  (Neo works; Legacy typeof-dispatch investigation; defer). R1-B/R2/R3 -> children 26/25/27.

## Done / Remaining
**Done:** 7 correctness children (21-27). Both triage batches complete. NeoStep 354->375 (+21 probes). The float-
corruption/unseeded-producer class CLOSED (float + reference producers). The "missing Neo redirect" class has a
clean lever (generic-definition-precedence). The CLR-struct-array/byref surface largely cleared (raw-Ldfld array
element, ref arr[i] byref, ldobj/stobj array element, raw-Stfld CLR-object VT field). The full Neo smoke now
COMPLETES CLEANLY (902 ran/192 failed -- the pre-crash Dict-NRE is gone; NIE counts are exhaustive).

**Remaining (all LOWER value or need a fresh re-audit -- a NEW wave, not continuation):**
- **`neo-legacy-ilruntime-type-getenumvalues-dispatch` (L1):** Legacy-only (Neo works). A Legacy typeof-dispatch
  investigation (ILRuntimeType.GetEnumValues override not dispatched under Legacy -> Cecil declaration order, not
  unsigned-binary-value sort). Narrow surface (negative-member enums). Defer unless Legacy parity matters.
- **Newly-surfaced candidates (from children 22/23/26/27 -- each needs a fresh re-audit before accepting):**
  - **float-ctor / op_Addition VT-return bug** (child-26 surfaced): `new TestVector3(float,float,float)` yields a
    ZERO struct AND `TestVector3.op_Addition`'s float VT-return doesn't write back. Blocks a float-field `+=` probe.
    Possibly the same float-corruption/VT-return class as child-21, or a distinct float-ctor gap. HIGHEST-value
    newly-surfaced (TestVector3 is pervasive). RE-AUDIT FIRST (the "ctor yields zero" claim is suspicious since
    child-3/8/15/16/21 all use TestVector3 and PASS).
  - **raw `Ldfld` READ sibling** (child-27 surfaced): `x = obj.Struct.value` -- silent flat-bytes-reinterpret
    corruption (the READ counterpart of child-27's Stfld). Needs a JIT marker (child-24 lineage). The heap-CLR-
    object-field owner byref offset is the field hash (child-27 pinned this).
  - **`+=` nested-ldflda gap** (child-27 surfaced): `obj.Struct.value += 111` -> `ldflda Struct; ldflda value;
    ldind; op; stind` -- the inner ldflda-on-byref gap. Where `UnitTest_Struct2` now fails (line 104).
  - **`ldelem.any`/`stelem.any` CLR-struct-array path** (child-26 surfaced): a PLAIN `a = arr[i]` on a CLR-struct
    array (NOT `+=`) lowers to ldelem.any (not ldobj) and returns garbage. Separate from the ldobj path child-26
    fixed.
  - **IL-instance CLR-struct-field owner (F-10)** (child-27 surfaced): the ManagedObjects-storage sibling;
  child-27 deferred it with a tagged NIE.
- **Step 19 (delegates)** -- the next MAJOR Neo step (R1-Shape-A is blocked behind it). Delegate Combine/invoke
  is unimplemented (DelegateExtTest02). This is the CLAUDE.md "next todo" beyond the overhaul's latent cleanup.
- **Housekeeping (cosmetic, non-blocking):** archive the 27 neo-overhaul child dirs + the 3 stragglers
  (neo-async-execctx-capture, neo-async-multi-await, neo-clrstruct-sm-field-layout) into rasen/changes/archive/
  (involves the rasen archive spec-sync flow). The git stash `child4-valuetask-blocked-partial` is OBSOLETE (do
  NOT pop; safe to drop).

## Key decisions (and why)
- **Re-audit EVERY framed gap before accepting it (the lead-10/11/12 lesson, reaffirmed):** this session it paid
  off in BOTH directions -- child 21/22/26/27 were REAL (would have been missed by blind trust), and child 23's
  framed MECHANISM was wrong (Ceq_Ref fires; the real gap was the ternary's direct brfalse). Always build a
  reproducer + dump the JIT before proposing.
- **Marker-vs-runtime-detection is a per-opcode decision (durable):** a producer/owner whose representation is
  AMBIGUOUS at runtime (raw-Ldfld's owner register = flat bytes OR byref) NEEDS a JIT marker (child-24); a
  producer/owner whose representation is UNAMBIGUOUS (ldobj/stobj operand = always a byref per ECMA-335; VT-owner
  Stfld = always a byref; NeoMarshalByrefFieldToSlot target) can use runtime detection (child-25/26/27). The
  write/read asymmetry: Stfld/ldobj/stobj/byref-marshal owners are always byrefs (runtime-safe); only the raw-Ldfld
  READ side (owner can be flat bytes OR byref) needs a marker.
- **Collapse propose+apply into one worker when triage already root-caused** (children 22/25/26/27): the triage
  report IS the proposal research; the implementer writes the artifacts + implements. Reviewer stays separate
  (author != verifier preserved). Saves a dispatch when the design is already pinned.
- **The full Neo smoke completing cleanly (902 ran/192 failed) is a milestone** -- the pre-crash Dict-NRE is gone,
  so NIE counts are now EXHAUSTIVE (not pre-crash estimates). Future scoping can trust the full-smoke frequency.

## Dead ends & gotchas
- **The Roslyn VBCSCompiler BUILD-SERVER CACHE (child-25, cost ~1h):** it caches a STALE compilation of
  `TestClass3.cs`/ILRuntimeTestBase; `rm -rf bin/obj` does NOT invalidate it (a 3s "rebuild" is the tell; a real
  clean build is ~10s). `strings` is misleading (method names live in the embedded PDB, not the MethodDef table --
  use `System.Reflection.Metadata TypeDefinition.GetMethods()`). FIX: kill all `dotnet` build-server processes +
  `-p:UseSharedCompilation=false` on every build after touching ILRuntimeTestBase.
- **`TestClass3.cs` is a multi-class FILE** (child-25): `TestClass3` spans only lines 12-39; the array-element host
  helpers live in `TestCLRBinding` (line 110+). A probe must reference `TestCLRBinding.X`. Grep the class boundary
  first.
- **A FAULTING probe gives NO info about whether its PASS constant is reachable** (child-24): TC3 asserted 1477 but
  inputs summed to 7777 -- it could NEVER pass, on HEAD or after. Hand-check each probe's expected constant vs its
  inputs before trusting FAIL->PASS.
- **Roslyn lowering variance for null comparisons** (child-23): `bool b=ref==null` and `if(ref==null)` lower to
  `ceq`; the ternary `(ref==null)?a:b` lowers to a DIRECT `brfalse` (no ceq). A null-comparison fault probe MUST
  use the ternary. Check the JIT dump for `brfalse.s` (direct) vs `ceq.ref`.
- **`OpCodeR` Operand4 (@20-23) is the free disjoint int spare** for raw Ldfld (child-24's 0x1 marker) -- only
  OperandLong is set for raw Ldfld, so Operand4 is free and survives all Neo passes. (Child-2's offset map;
  child-15's 0x8 is for ldflda; the two marker namespaces are disjoint.)

## Working set
Build/test (ALWAYS `-f net8.0`; CLI=`Debug_Neo --no-incremental`, NEVER TestCases with `Debug_Neo`):
`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` +
`dotnet build TestCases/TestCases.csproj -c Debug` +
`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` -> 375/0.
**After touching ILRuntimeTestBase: kill `dotnet` build-server + `-p:UseSharedCompilation=false`** (the cache gotcha).
Commit trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`; commit via `-F .git/cmsg.txt`;
`git config lfs.useslockfiles false` before push. Subagents CAN run bare dotnet build/run (allowlisted) + Grep/Read
(NOT bash grep/tail/cd). The `OUTPUT_JIT_RESULT` macro prints large JIT dumps for Debug_Neo (normal).

## Next action
The lead-12 latent follow-ups are DONE (7 shipped, 3 winnowed). The remaining frontier is a NEW wave of freshly-
surfaced candidates (each needs re-audit) + Step 19 (delegates, the next major step). HIGHEST-value newly-surfaced:
the float-ctor/op_Addition VT-return bug (TestVector3 is pervasive -- re-audit whether the "ctor yields zero" is
real or context-specific). Then the raw-Ldfld-read-CLR-object-field (needs a marker, child-24 lineage) and the
ldelem.any CLR-struct-array path. L1 (Legacy GetEnumValues) and the housekeeping (archive) are low-value/defer.
The handoff/ transcriptions (lead-1..12) + this lead-13 are the full session record.
