# Tasks — neo-array-multidim-ilvt

> Status: **PARKED** after the ctor token resolution (sub-gap 0) shipped. The
> remaining sub-gaps (1-3) are the follow-up. See `blocked.md` + `design.md`.

## Sub-gap 0 — multi-dim array ctor token resolution (DONE, GREEN)

- [x] **0.1** Probe-first: construct `S[,] arr = new S[2,3]` (S = IL struct
      `NeoStep16Vt { int; string; }`); confirm the HEAD gap. Finding:
      `KeyNotFoundException: Cannot find method:.ctor in type:S[0...,0...]` at
      the ARRAY CTOR token resolution — a SHARED-ENGINE gap (Legacy + Neo
      identical), NOT a Neo element-copy gap as the mandate assumed.
- [x] **0.2** Fix `ILType.GetConstructor(List<IType>)` to delegate to the
      underlying CLR array type when `IsArray` (new `ResolveArrayClrType()`
      helper: `appdomain.GetType(arrayCLRType) as CLRType`).
      `ILRuntime/CLR/TypeSystem/ILType.cs`.
- [x] **0.3** Fix `ILType.GetMethod(name, param, genericArguments, ...)` to
      delegate likewise (the multi-dim `a[i,j]` callvirt Get/Set + the `ref
      a[i,j]` Address). Same file.
- [x] **0.4** Stash-toggle: HEAD throws `KeyNotFoundException`; +fix resolves
      the ctor (the probe then fails at the param-boxing gap). Load-bearing
      PROVEN.
- [x] **0.5** Add the `NeoStep16_MultiDimIlVtPrimitiveControl` keeper probe
      (primitive-element `[,]` control; guards the shipped multi-dim primitive
      path). `TestCases/NeoStep16Test.cs`.
- [x] **0.6** Verify: Neo `NeoStep` 268/0/0; `NeoStep16` 23/0/0;
      `NeoOptHardening` 24/0/0; plain-`Debug` CLI build 0 errors (Legacy-neutral).

## Sub-gaps 1-3 — the param-boxing mechanism (FOLLOW-UP; F-7B/F-10 complexity)

- [ ] **1. Box an IL-VT param for a reflection CLR call** (`Set`'s element
      param). Thread the per-param `RefDst` ref-region offset into
      `CLRMethod.Invoke` (or a Neo-specific reader); build a boxed
      `ILTypeInstance` from the flat bytes + ref region via the Constrained-
      inherited-CLRMethod box template (`ILIntepreter.Neo.cs:~4884-4942`:
      `Instantiate(false)` + `CopyFrameToIL` + `Boxed=true`; ref base via the
      `localInfos` scan, R2). The param reader today reads `mStack[idx]` for a
      non-primitive non-enum param (`CLRMethod.cs:~508-526`) -> garbage for an
      IL-VT flat-bytes param.
- [ ] **2. Unbox a boxed-IL-VT return** (`Get`'s element return). The return
      path (`InvokeNeoClrMethod` `:983-998`, `else if (retType.IsValueType)`)
      writes a boxed CLR struct's flat bytes via `WriteNeoValueType`.
      Discriminate on `res is ILTypeInstance` and `CopyILToFrame` into the dest.
- [ ] **3. Multi-dim `ldelema` address-of-IL-VT-element.** The `Ldelema` arm
      (`ILIntepreter.Neo.cs:4593-4631`) handles `ILTypeInstance[]` (rank-1) +
      CLR primitive arrays; add the multi-dim IL-VT-element address shape (the
      C# `ref a[i,j]` lowering for the `LdelemaMutate` probe).
- [ ] **4. Re-add the 4 red probes** (`NeoStep16_MultiDimIlVtRoundTrip`,
      `MultiCell`, `RefFieldNonNull`, `LdelemaMutate`) once 1-3 land; assert
      BOTH the primitive field `num` AND the ref field `txt` (non-null +
      correct) survive the multi-dim Set/Get + the multi-dim ldelema mutation.
- [ ] **5. Scope boundary:** rank-3+ IL-VT-element (include if it falls out,
      else note); nested-VT-element (recurse one level if cheap, else scope
      out); non-zero-based lower-bound arrays (stay a documented Non-Goal).
- [ ] **6. Guard `ILRuntimeType.GetConstructor`** (`ILRuntimeType.cs:533-536`)
      against the `(ILMethod)` cast for an IL array type ctor (now returns a
      `CLRMethod`); not exercised by the suite, but a latent regression vector.

## PARK trigger

Sub-gaps 1-3 are foundational + coupled (each needs its own encoding + runtime
hook; F-7B/F-10 complexity per sub-gap). Beyond the "one unit of work" / child
scope -> PARK with this `blocked.md` + the GREEN sub-gap 0 (ctor token
resolution) kept on the branch (uncommitted — LEAD commits).
