# Ship Log — neo-step23-neoassembly

> Change: `neo-step23-neoassembly` (portfolio child of `neo-completion-portfolio`)
> Capability: `neo-optimizer`
> Branch: `features/object-model-overhaul`
> Date: 2026-07-07
> Pipeline: small-feature, Tier A, full autonomy.

## Verdict

**SHIPPED — clean.** Review verdict **APPROVE-WITH-FINDINGS** (round 0, 0
Blocker/Major); review-loop round 1 adopted the reviewer's Minor-1 (comparator
completeness) + Minor-3 (adversarial probes) — TEST-ONLY, gates green, no engine
change, no real bug found. LEAD non-author confirmation (gate-run + diff-read).

## Delivered scope (Step 23 -- the `.neo` binary format + serializer/deserializer)

A standalone serialization layer under a new `ILRuntime/Runtime/NeoAOT/` namespace,
all `#if ENABLE_NEO_MODE` (Neo-only, additive, Legacy-neutral -- does NOT touch
JIT/runtime/Step-22 behavior):

- **`NeoAssembly.cs`** — format model: `NeoHeader` (Magic `0x494C524E` "ILRN",
  Version=1, Endianness, 7 TableOffsets) + 8 record structs + `NeoAssemblyModel`.
  Distinct magic from HybridPatch (`0x58883551`).
- **`NeoAssemblyWriter.cs`** — BinaryWriter serializer. `NeoRefTableBuilder` REUSES
  HybridPatch's `*PatchInfo.Create` factories (string/type/method/field refs
  index-ized + structurally deduped). OpCodeR serialized RAW 24-byte little-endian
  via `MemoryMarshal.AsBytes<OpCodeR>` (captures ALL `[StructLayout(Explicit)]`
  aliases -- avoids per-opcode canonical-field selection + the F-8/OPT-HARDEN-K1
  union defects).
- **`NeoAssemblyReader.cs`** — BinaryReader deserializer (`MemoryMarshal.Cast<byte,
  OpCodeR>` read) -> `NeoAssemblyModel`.
- **CompiledFrame serialization** -- load-bearing fields only: LocalInfos
  (StackSlotInfo), ParamInfos, TotalStructSize/TotalRefSize, ParameterCount. SKIPS
  `CodeBody` (register-index, inliner/debugger) + `Symbols` (Cecil-keyed). The
  Cecil-keyed `addr[]` EH map -> `NeoExceptionHandlerRecord` (body-INDEX ranges +
  catch-type ref). `NeoCallParamMap.PrimitiveByRefElemType` (`System.Type[]`) by
  assembly-qualified name.
- **GenericMethodTemplate serialization -- FAITHFUL** (the Step-22 follow-up):
  every `PatchEntry` (every Initobj/Box/Isinst/Castclass/Constrained T-token +
  every T-qualified callvirt method-token), `CecilToken` re-resolved to a
  TypeRef/MethodRef index by `Kind`; the auto-Initobj prefix via
  `InitObjPrefixRegisters`. Does NOT assume the prefix is the only Initobj site.
- **`ILType.cs`** -- one additive `#if ENABLE_NEO_MODE` read-only accessor
  `NeoInterfaceMapForAOT` (sanctioned by design task 5.3).
- **`ILRuntimeTestCLI/Program.cs`** -- the `NeoStep23Roundtrip` filter.

## Verification evidence

### V1 roundtrip equivalence (the load-bearing gate): 15/15 PASS
`NeoStep23RoundtripCheck.cs` (host-side, DEBUG+Neo) + `TestCases/
NeoStep23NeoAssemblyTest.cs` (matrix inputs). 4 strict comparators: raw-24-byte
`OpCodeRsEqual` (total over all 24 bytes), `MethodDefsEqual`, `TypeDefsEqual`,
`TemplatesEqual` (all load-bearing fields; the 3 identity ref-idx fields verified
post-round-1). Matrix (post-round-1, 9->15 cells): ProbeBasic, GenericProbe,
TryCatchProbe, MixedLocals, ByrefParams, SwitchProbe + the round-1 adversarial
cells (MultiConstrainedGeneric [Step-22 BLOCKER shape doubled: patches=2,
constrainedT=2, constrainedM=2], NestedEH, ClrByrefCall [real System.Int32 aqname,
exercises the null->"" normalization], MultiIface [2 interfaces, vtable=8],
ZeroLocals). Each: serialize -> deserialize -> assert == original.

### Regression gates
- NeoStep smoke: **205/205** (unchanged; the serializer is additive).
- NeoOptHardening: **24/24**. NeoStep20: **9/9**. NeoStep22SelfCheck: **55/55**.
- **Legacy-neutral PROVEN at IL level:** byte-search -- plain-Debug ILRuntime.dll
  contains NONE of `NeoAssemblyFormat`/`NeoAssemblyWriter`/`NeoInterfaceMapForAOT`/
  `NeoStep23RoundtripCheck`; Debug_Neo contains all. (The byte-level delta is
  embedded-PDB source-document checksums, not code -- review Minor-4.)

## Q1/Q2/Q3 resolutions
- **Q1 (serialize CodeBody register-index?):** NO -- only `NeoExecuteBody`
  (CodeBody is inliner/debugger only).
- **Q2 (VTable slot-keys):** NO for the class reverse map (Step 25 rebuilds); YES
  for per-interface `MethodSlotKeys[]` (needed for dispatch).
- **Q3 (static initial values):** NOT at V1 -- static LAYOUT + the `.cctor`
  MethodRef are serialized; the runtime seeds statics by executing the .cctor
  (Step 25), not from byte blobs.

## Review-loop round 1 (TEST-ONLY, no engine change)
Round-0 review APPROVED-WITH-FINDINGS (0 Blocker/Major); recommended adopting
Minor-1 + Minor-3 before archive. Round-1 fixer (non-author) adopted both:
- **Minor-1:** added the 3 identity ref-idx comparisons (MethodRefIdx/TypeRefIdx/
  DefinitionMethodRefIdx) the comparators skipped. The 15/15 PASS under the now-
  strict comparators PROVES the fields roundtrip (no serializer fix needed).
- **Minor-3:** adopted the 6 adversarial probes permanently (matrix 9->15).
- **No real bug found.** LEAD non-author confirmation: gate-run (15/15 + 205/205) +
  diff-read (strict comparators confirmed).

## Deferred (per plan / Step-25 follow-ups)
- `ilrt_neoc` standalone precompile CLI (Step 24).
- Runtime `.neo` LOADER + Cecil-decoupling + V2 functional (deserialize->ExecuteNeo)
  (Step 25).
- Cross-AppDomain token-hash re-resolution; CLR-type assembly-qualified-name
  indexing; static `.cctor` seeding (Q3) (Step 25).
- Perf benchmarks (Step 26).
- CLR interface implementor types recorded with a -1 TypeRef idx at V1 (Step 25
  indexes them; slot layout faithful -- confirmed).
- Review Minor-2 (Endianness byte not validated by reader -- LE-only documented),
  Minor-4 (byte-identical claim is IL-level, not byte-level -- PDB metadata),
  Trivial-5/6 (dead local, BodiesEqual diff fallback) -- accepted-known/doc.

## Lessons reaffirmed
- **Probe before designing (reaffirmed).** The format was grounded in the actual
  struct schemas (OpCodeR 24-byte Explicit; CompiledFrame fields; GenericMethodTemplate).
- **Strict comparators earn their keep.** The 4 strict roundtrip comparators caught
  2 real serializer bugs during apply (null->"" normalization, header/table offset
  collision) + the round-1 completeness gain verified the ref-idx fields. A loose
  comparator would have hidden all of these.
- **The round-0 reviewer's blast-radius sweep found the coverage hole** (the 9-cell
  matrix missed multi-Constrained/multi-clause-EH/real-aqname/multi-interface) --
  adopting those probes permanently hardens the gate Step 24/25 build on.
