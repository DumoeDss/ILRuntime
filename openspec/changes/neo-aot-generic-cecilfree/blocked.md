# Blocked — neo-aot-generic-cecilfree (child 8)

**Reason:** the Cecil-free generic-INSTANCE load+exec needs **multi-step rework**
of the JIT back-half (the generic-instantiation path reads Cecil at 5 sites; the
load-bearing one is `RunNeoBackHalf` -> `BuildInitialRegisterTypes` +
`AllocateLocalStackSpaces`, both hardwired to `def.Body.Variables`/`def.Parameters`
which are null on a Cecil-free shell). Per the PARK fallback ("needs multi-step
rework"), this child does NOT grind.

## What is delivered (not reverted — additive + green)
- The reproducer (probe + capstone + CLI wiring) — a correct forward signal that
  FAILs on HEAD at exactly the right sites and will go green when a successor
  lands the Cecil-free back-half.
- NeoStep smoke **253/0/0** (no regression); NeoStep25 gate **11/0**; plain-Debug
  build **0 errors** (Legacy-neutral). The reproducer is probe + check + CLI
  branch only — NO engine file was modified.

## What is NOT done (the successor's work)
The fix (5 sites across 4 files — see `design.md` "Why PARK" + `tasks.md`):
`.neo` V4 `GenericParamNames`; `ILMethod` Cecil-free generic shell
(`GenericParameterCount`/`MakeGenericMethod`/`FindGenericArgument`); `JITCompiler`
Cecil-free back-half; `NeoAssemblyLoader` S2 bind loop + `ResolveVariableType`
shell-aware; `BuildFromNeoRecord` closure plumbing. Recommend a dedicated child
`neo-aot-generic-cecilfree-backhalf`.

## The durable finding (carry forward)
**The S2-probe-inlining pitfall:** a trivial generic method whose body the JIT
inlines into its caller runs Cecil-free correctly TODAY (the wrapper's `.neo`
body is self-contained) — functional wrapper cells pass WITHOUT exercising the
Cecil-free generic mechanism. The G1 (template-bind coverage) + G2 (template
body-mutation via a FRESH `MakeGenericMethod` instance) guards are the
load-bearing proofs; functional cells alone are insufficient. Any successor
probe MUST use a non-inlinable body OR drive a fresh instance (G2) to genuinely
exercise the path. (The existing S2 capstone `NeoStep25LoadExecCheck` has the
same shape — its functional `WrapEcho*` cells pass via inlining; its
template-mutation cell is the load-bearing one.)
