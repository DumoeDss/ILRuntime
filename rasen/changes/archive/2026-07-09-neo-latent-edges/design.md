# Design -- neo-latent-edges

> Dump-gate design for F-9, F-7, F-2, F-11 on HEAD `6d0efe68`. Each edge:
> construct the reproducer, run on HEAD via `ILRuntimeTestCLI` built
> `Debug_Neo` + run with `-f net8.0`, cite file:line, and record the verdict
> (fix / close). Probes were built into a TEMPORARY
> `TestCases/NeoLatentEdgesProbeTest.cs`, run, then DELETED (NO source ships).
>
> Baseline BEFORE probes: `NeoStep` smoke = 229/0/0 (regression green). After
> probe deletion: 229/0/0 again (no drift).
>
> Per-edge verdict summary:
>   F-9  -- NON-REPRODUCIBLE (close; JIT-body control disproves the inliner
>           return-move mis-classification)
>   F-7  -- REPRODUCIBLE but NOT a small fix (close-needs-larger-change; the
>           byref is destroyed in two sites before WriteNeoCallSlot, and the
>           target runs on a separate pooled interpreter -- a Step-19-sized
>           frame-to-frame delegate-invoke mechanism is required)
>   F-2  -- NON-REPRODUCIBLE on the documented hypothesis (close; JIT-body
>           control disproves the inliner ref-fold)
>   F-11 -- NOT A BUG (close; accepted optimization; stale body is CORRECT JIT;
>           not reproducible via the smoke harness)

## 0. Build + run harness (binding)

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo
dotnet build TestCases/TestCases.csproj -c Debug
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true <filter>
```

ALWAYS `-f net8.0`. `Debug_Neo` prints huge JIT/optimizer output (normal). A
test taking >10s is a loop. The CLI filter does substring matching, so per-edge
probes were filtered by their FULL qualified name
(`NeoLatentEdgesProbe.NeoLatent_F7_DelegateRefInt` etc.).

## 1. F-9 / NEO-INLINED-RETURN-MOVE -- NON-REPRODUCIBLE (close)

### Documented hypothesis
"An int returned from an INLINED IL method is MOVED AS A REFERENCE -> a
subsequent read interprets the int value as an mStack index ->
`mStack[intValue]` OOB. The trivial-inliner mis-classifies the return value.
The 4d.2 probe defeated it via `int v = slot; return v + 0;`."
(`neo-deferred-items.md` F-9, lines 825-848.)

### Reproducer (run on HEAD, Neo)
Three variants in `NeoLatentEdgesProbe`:
- `NeoLatent_F9_ReturnMoveNoDefeat`: `static int ReturnIntDirectly(int x){return x;}` called as `int r = ReturnIntDirectly(42);` -- NO `+ 0` defeat.
- `NeoLatent_F9_ReturnMoveViaLocal`: `static int ReturnIntViaLocal(int x){int v=x; return v;}` -- the local form, NO defeat.
- `NeoLatent_F9_ReturnMoveDefeated`: `static int ReturnIntDefeated(int x){int v=x; return v+0;}` -- the documented defeat, as a control.

### Result: 3/3 PASS on HEAD (`NeoLatentEdgesProbe.NeoLatent_F9` -> Ran 3, 0 failed)
The JIT body of `NeoLatent_F9_ReturnMoveNoDefeat` (dumped) is:
```
0: ldc.i4.s r5,42
1: move r6, r5
2: br.s 3
3: move r5, r6
4: move r0, r5
5: ceqi r5,r0,42
...
```
The `ReturnIntDirectly` call IS inlined (no `call` opcode). The int return value
`42` flows through PRIMITIVE registers (`r0` holds `42`), `ceqi r5,r0,42`
succeeds. There is NO reference move and NO OOB. The trivial-inliner does NOT
mis-classify the int return on HEAD. The `+ 0` defeat is no longer load-bearing
(it defeats nothing -- the un-defeated form already passes).

### Verdict: CLOSE (needs-reproducer; none exists on HEAD). The documented
defect is disproven by the JIT-body control. Suspect site (the trivial-inliner's
return-value classification in `JITCompiler.cs`) does NOT exhibit the
mis-classification on HEAD. No spec delta.

## 2. F-7 / NEO-DELEGATE-REFOUT -- REPRODUCIBLE, NOT a small fix (close-needs-larger-change)

### Documented hypothesis
"`DelegateAdapter.NeoInvokeSub` (the CLR->IL callback, e.g. `List.ForEach(ilAction)`)
writes CLR args via `WriteNeoCallSlot`, which handles primitives/reference/CLR-VT
but NOT a BYREF-typed delegate param (a `ref T`/`out T` on an `Action<>`/`Func<>`
Invoke). The return-side `WriteNeoDelegateInvokeReturn` has the symmetric gap."
(`neo-deferred-items.md` F-7, lines 720-757.)

### Reproducer (run on HEAD, Neo)
Custom delegate types with ref/out params (a generic `Action<>`/`Func<>` cannot
carry `ref`/`out`; a custom delegate can):
```csharp
public delegate void F7RefIntDelegate(ref int x);
public static void F7BumpRef(ref int x){ x += 10; }
public static void NeoLatent_F7_DelegateRefInt(){
    F7RefIntDelegate d = F7BumpRef;
    int v = 5; d(ref v);
    if (v != 15){ ...1/0... }
}
```
Plus `F7OutIntDelegate(out int)` and `F7RefStringDelegate(ref string)->int`
variants.

### Result: 3/3 FAIL on HEAD
- `NeoLatent_F7_DelegateRefInt` FAILs: `ArgumentOutOfRangeException` at
  `ILIntepreter.Neo.cs:3726` (the `Ldind_I4` arm, `mStack[objIdx]` with a
  garbage `objIdx`).
- `NeoLatent_F7_DelegateOutInt` FAILs: `...out of range...` at
  `ILIntepreter.Neo.cs:3636` (the `Stind_I4` arm).
- `NeoLatent_F7_DelegateRefString` FAILs: `...out of range...` at
  `ILIntepreter.Neo.cs:3831` (the `Ldind_Ref` arm).

The delegate target `F7BumpRef` JIT body is `ldind.i4 r1,r0; addi r1,r1,10;
stind.i4 r0,r1` -- the `r0` (the byref param `x`) carries a Ref Slot whose
`objectIndex` half is garbage, so `ldind.i4` OOBs at line 3726.

### Why the documented small fix is INSUFFICIENT (two-site destruction proof)
The byref is destroyed in TWO places before any byref-aware `WriteNeoCallSlot`
could help:

1. **`ReadNeoDelegateInvokeArgs`** (`ILIntepreter.Neo.cs:246-287`) reads the
   caller's byref arg from `targetBase`. For `pt.TypeForCLR == typeof(int)` it
   does `args[i] = *(int*)(targetBase + cur); cur += 4;` -- but a byref param's
   storage is the 8-byte Ref Slot `(objectIndex, offset)`, so it reads ONLY the
   first 4 bytes (`objectIndex`), boxes it (or null/garbage), and the offset
   half is NEVER read. The `object[]` cannot represent a byref + its caller-
   frame provenance.

2. **`NeoInvokeSub` runs the target on a SEPARATE POOLED interpreter**
   (`DelegateAdapter.cs:1006-1019`): `ILIntepreter intp = appdomain.RequestILIntepreter();`
   then `intp.ExecuteNeo(method, frameBase, ...)` on a frame built at THIS
   interpreter's `stack.StackBase`. The target's `frameBase`/`mStack` are
   UNRELATED to the caller's frame. So even if `WriteNeoCallSlot` were byref-
   aware, a correctly preserved `(objectIndex, offset)` Ref Slot would index
   into the WRONG frame/mStack (the caller's, which the target cannot address).
   A `ref`/`out` write-back to the caller's frame is therefore impossible
   through this path.

The `CopyNeoCallArguments` call at `ILIntepreter.Neo.cs:2431` (just before the
delegate-Invoke branch) DOES correctly marshal the byref into `targetBase`
preserving the Ref Slot (same frame) -- so the byref is valid at `targetBase`.
But the delegate-Invoke branch at `ILIntepreter.Neo.cs:2443-2454` then RE-READS
via `ReadNeoDelegateInvokeArgs` (destroying it) and routes to `NeoInvokePublic`
(separate interpreter). The byref is lost in the re-read + the interpreter
boundary.

### Required fix (NOT in scope; routed to a future byref-delegate change)
A NEW frame-to-frame delegate-invoke fast path that EITHER:
- (a) runs the IL delegate target on the SAME pooled interpreter (reusing the
  caller's `frameBase`/`mStack`) so the byref Ref Slot remains valid AND the
  `ref`/`out` write-back naturally lands in the caller's frame; OR
- (b) keeps the separate interpreter but constructs, for a byref param, a
  writable proxy slot on the target's `mStack` whose backing store is the
  caller's frame cell, and propagates the final value back after the target
  returns (mirroring `CopyNeoCallThisBack`'s reverse-copy pattern from
  neo-step13-area4).

Either is a Step-19-sized mechanism (touches the delegate-Invoke dispatch +
the marshal contract + the write-back), not a small byref-awareness tweak to
`WriteNeoCallSlot`/`WriteNeoDelegateInvokeReturn`.

### Verdict: CLOSE (needs-larger-change). REPRODUCIBLE on HEAD (3/3 FAIL @
3636/3726/3831). The accepted-known limitation is recorded as a future-fix
SHALL in `specs/neo-dispatch/spec.md`. Files the future fix will touch:
`ILIntepreter.Neo.cs` (the `IsDelegateInvoke` branch ~2443-2454 +
`ReadNeoDelegateInvokeArgs` 246-287 + `WriteNeoDelegateInvokeReturn` 290-319)
and `DelegateAdapter.cs` (`NeoInvokeSub` 1006-1119 +
`WriteNeoCallSlot` 1124+). Regression risk for the FUTURE fix: HIGH (touches
the green Step-19 delegate hot path -- `NeoStep19_*` is the regression guard;
the future fix MUST keep `NeoStep19` 8/8 green and not regress the
plain-primitive delegate callback shape).

## 3. F-2 / INLINER-REFONLY-VT -- NON-REPRODUCIBLE on the documented hypothesis (close)

### Documented hypothesis
"A ref-only VT (TotalPrimitiveSize == 0) local `new S(refArgs)` mis-compiles:
the inlined `stfld.ref.inline` writes do NOT survive to the following in-frame
`ldfld.ref` read. Suspect: the JIT inliner's ref-fold over a 0-prim-size VT
local." (`neo-deferred-items.md` F-2, lines 450-465.)

### Reproducer (run on HEAD, Neo)
The literal documented shape -- `struct S { string a; string b; }` (TotalPrimitiveSize
== 0), `new S(refArgs)`, then field-read -- plus two controls:
```csharp
public struct F2RefOnly   { public string a; public string b;
    public F2RefOnly(string ta,string tb){a=ta;b=tb;} }
public struct F2WithPrim  { public int x; public string a; public string b; ... }
public struct F2SingleRef { public string a; public F2SingleRef(string ta){a=ta;} }

public static void NeoLatent_F2_RefOnlyReadFields(){
    F2RefOnly s = new F2RefOnly("hello","world");
    int la = s.a.Length; int lb = s.b.Length;
    if (la!=5||lb!=5){...1/0...}
}
// + F2_WithPrimReadFields, F2_SingleRefReadField
```

### Result: 3/3 PASS on HEAD (`NeoLatentEdgesProbe.NeoLatent_F2` -> Ran 3, 0 failed)
The JIT body of `NeoLatent_F2_RefOnlyReadFields` (dumped) is:
```
0: initobj r0, .../F2RefOnly
1: ldloca.s r9, r0
2: ldstr r10,"hello"
3: ldstr r11,"world"
4: stfld.ref.inline r9, r10, 0x00000000, (0,0)
5: stfld.ref.inline r9, r11, 0x100000000, (0,1)
6: ldfld.ref.inline r1, r0, 0x00000000, (0,0)
7: ldfld.ref.inline r2, r0, 0x100000000, (0,1)
8: callvirt.clr r3, r1, String::get_Length()
9: callvirt.clr r4, r2, String::get_Length()
10: bnei.un r3,5,14
11: ceqi r9,r4,5
...
```
This is the EXACT documented shape: inlined `stfld.ref.inline` writes (offset 0
and 1) followed by in-frame `ldfld.ref.inline` reads of BOTH fields, then
combined in a boolean expression. The writes DO survive to the reads (both
fields return the correct values; `bnei.un r3,5` and `ceqi r4,5` both succeed).
The inliner's ref-fold over the 0-prim-size VT local does NOT mis-compile on
HEAD.

### Verdict: CLOSE (needs-reproducer; none exists on HEAD for the documented
hypothesis). The documented ref-fold defect is disproven by the JIT-body
control. (A prior session hypothesized a RE-CHARACTERIZED "multi-field/multi-
local COMBINE" defect in the optimizer -- the F-8/F-MAJ-1 OpCodeR-union family
-- but never backed it with a shipped failing probe. That is a distinct defect
class from the documented F-2 and, if it surfaces, belongs to the F-8/F-MAJ-1
optimizer-hardening track, NOT this inliner-ref-fold triage. Out of scope
here.) No spec delta.

## 4. F-11 / NEO-AOT-GENERIC-EAGER-COMPILE -- NOT A BUG (close; accepted optimization)

### Documented characterization
"A generic instance force-compiled BEFORE the loader binds the AOT template
keeps a stale `bodyRegister` (JIT/unmutated) and the loader's template bind
never reaches it. The stale body is CORRECT JIT. The S2 capstone worked around
it via a fresh `MakeGenericMethod` instance." (`neo-deferred-items.md` F-11,
row at line 100.)

### Reproducer (smoke harness)
NOT reproducible via `ILRuntimeTestCLI`: the smoke harness runs PURE JIT (there
is no `.neo` load + `NeoAssemblyLoader.Attach` template bind in the interpreter
smoke path). Reproducing the load-order staleness requires the production-AOT-
load path -- the standalone `ilrt_neoc` AOT compiler + a live `.neo` loader
that binds generic templates into a runtime AppDomain where a generic instance
was already force-compiled. That is the S3 production-AOT surface, out of scope
for a "small edges" triage.

### Verdict: CLOSE (accepted optimization; not a behavioral bug). The stale body
is CORRECT JIT (the deferred-items row states so: "a generic instance compiled
before load keeps JIT", "NOT an S2 bug"). There is no corruption to fix -- only
a missed optimization (the instance runs JIT instead of the AOT template). The
S2 capstone already worked around it with `MakeGenericMethod`. A fix
(load-order: bind templates BEFORE any generic call compiles; OR invalidate
stale instance `bodyRegister`s on bind; OR a Cecil-free per-instance refresh)
is low-value (correctness is unaffected), moderate-risk (touches the S3 loader
bind + the generic-instance body cache), and squarely an S3 production-AOT
concern -- sequenced under Step 25 S3, not this change. No spec delta.

## 5. Out-of-scope / explicitly NOT addressed

- The F-2 re-characterized "multi-combine" optimizer defect (if real): F-8/
  F-MAJ-1 track.
- The F-7 byref-delegate frame-to-frame fix: future byref-delegate child
  (Step-19-sized).
- The F-11 production-AOT load-order optimization: Step 25 S3.
- The four deferred-item rows in `.trae/documents/neo-deferred-items.md` are
  updated by the APPLY stage (doc-only), NOT by this propose stage.

## 6. Spec deltas

- `neo-dispatch` ADDED: one future-fix SHALL recording the F-7 delegate-byref-
  callback accepted-known limitation (the target of a future byref-delegate
  change). PURE ASCII, SHALL-first. Neo-only; Legacy-neutral (Legacy's delegate
  callback uses `StackObject[]` + the standard InvokeILMethod, which already
  handles byref params; the limitation is Neo-`ExecuteNeo`-specific).
- F-9, F-2, F-11: NO spec delta (disproven / accepted; no defect to constrain).
