# Planning Context — neo-latent-edges (4 small edges, triage + fix, TRUE COMPLETION)

> SEED. Triage + fix 4 small latent edges. For EACH: dump-gate on HEAD `6d0efe68`
> -- reproducible -> fix; not reproducible -> close as needs-reproducer (the
> Q-STRUCT/Q-LONG/F-13 pattern). All are documented in `.trae/documents/neo-deferred-items.md`.

## The 4 edges (each: reproduce, then fix-or-close)

### F-9 / NEO-INLINED-RETURN-MOVE
An `int` returned from an INLINED IL method is MOVED AS A REFERENCE -> `mStack[intValue]`
OOB (the trivial-inliner mis-classifies the return value). Surfaced by neo-step13-area4-refandstind
(the 4d.2 probe defeated it via `int v = slot; return v + 0;`). Suspect: the trivial-inliner's
return-value classification in `JITCompiler.cs`. **Reproduce:** an inlined IL method returning an
int, read by the caller WITHOUT the `+0` defeat -> OOB on HEAD. Fix the inliner's return-value
classification; OR close if non-reproducible on HEAD.

### F-7 / NEO-DELEGATE-REFOUT
`DelegateAdapter.NeoInvokeSub` (the CLR->IL callback, e.g. `List.ForEach(ilAction)`) writes CLR
args via `WriteNeoCallSlot`, which handles primitives/reference/CLR-VT but NOT a BYREF-typed
delegate param (a `ref T`/`out T` on an `Action<>`/`Func<>` Invoke). The return-side
`WriteNeoDelegateInvokeReturn` has the symmetric gap. **Reproduce:** a delegate with a `ref`/`out`
param invoked via Neo (e.g. a `List<T>.ForEach(Action<T>)` where the action takes a ref) -> the
byref mis-marshals. Fix `NeoInvokeSub`'s arg-write/return-read to be byref-aware (mirror
`CopyNeoCallArguments`'s `PrimitiveByRefSrc` from neo-step13-area4); OR close if non-reproducible.

### F-2 / INLINER-REFONLY-VT
A ref-only VT (TotalPrimitiveSize == 0) local `new S(refArgs)` mis-compiles: the inlined
`stfld.ref.inline` writes do NOT survive to the following in-frame `ldfld.ref` read. Suspect: the
JIT inliner's ref-fold over a 0-prim-size VT local in `JITCompiler.cs`. **Reproduce:** a ref-only
struct (e.g. `struct S { string a; string b; }`) constructed via `new S(refArgs)` then field-read
-> wrong/null on HEAD. Fix the inliner ref-fold; OR close if non-reproducible.

### F-11 / NEO-AOT-GENERIC-EAGER-COMPILE
A generic instance force-compiled BEFORE the loader binds the AOT template keeps a stale (but
CORRECT) JIT `bodyRegister` -- missed optimization, NOT corruption. The S2 capstone worked around
it via a fresh `MakeGenericMethod` instance. **This is an OPTIMIZATION (the stale body is correct
JIT).** Reproduce: a generic instance eager-compiled pre-Attach -> runs JIT instead of AOT. Fix =
load-order / per-instance refresh so the AOT template is used; OR close/document if the optimization
isn't worth the risk (the stale body is correct).

## Dump-gate (binding -- for EACH, probe on HEAD `6d0efe68`, cite file:line)

For each: construct the reproducer. If it reproduces -> SMALL fix (ship). If not reproducible on
HEAD -> close as needs-reproducer (the prior session's pattern: array 2/3, byref 3/4, controlflow,
F-13 were all disproven/no-ops). Record each verdict honestly.

## Build + test (CRITICAL -- always `-f net8.0`)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep     # 229/0/0 regression
# per-edge probes (whichever filter) -- FAIL-on-HEAD -> PASS for the fixed ones
```
ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT output -- normal. A test taking >10s = a loop.

## Spec authoring traps
- `specs/<cap>/spec.md` delta PURE ASCII; SHALL-first per requirement. Legacy is the REFERENCE.
  Neo-only `#if ENABLE_NEO_MODE`. Legacy-neutral (the inliner/NeoInvokeSub/Run edits are Neo paths).

## Deliverables
`proposal.md`, `design.md` (per-edge dump-gate verdict: fix / close-needs-reproducer, each
file:line-cited + the reproducer), `specs/<cap>/spec.md` (delta), `tasks.md`. Success = each
reproducible edge fixed (FAIL-on-HEAD -> PASS) + each non-reproducible closed honestly.
