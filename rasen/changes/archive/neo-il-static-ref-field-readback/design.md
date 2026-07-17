# Design — neo-il-static-ref-field-readback

## Context

The Neo register-VM (`ExecuteNeo`) runs on a compact `byte*` frame. A
**reference** value lives in a register as a 4-byte mStack index in the slot's
primitive bytes, AND the register has a ref slot at `frameRefBase + RefOffset`
holding the same object. The IL reference-type `newobj` arm ("Step 8b",
`ILIntepreter.Neo.cs` ~3309) allocates the new instance, stores it at the dest
register's ref slot, copies the ctor args into the callee frame, and invokes the
ctor:

```csharp
ins = ilNewobjType.Instantiate(false);
mStack[newobjDstIdx] = ins;                 // newobjDstIdx = frameRefBase + dstRefOffset
*(int*)targetBase = newobjDstIdx;           // callee slot-0 (this) = newobjDstIdx
CopyNeoCallArguments(ref map, ...);          // copy args (frameBase -> targetBase)
*(int*)retDstPtr = newobjDstIdx;             // dest primitive = newobjDstIdx
mStack.Add(mStack[newobjDstIdx]);            // push 'this'
InvokeNeoCallTarget(ctor, isNewobj:true, …);
```

`CopyNeoCallArguments` copies each arg's primitive bytes via
`Unsafe.CopyBlock(targetBase + Dst[i], frameBase + Src[i], Size[i])`. A reference
arg is `Size==4`: it copies the arg's mStack INDEX; the ctor later dereferences
`mStack[index]`.

## The bug (reference arg + dest/arg register aliasing)

The C# lazy-init idiom `if (x == null) x = new T(refArg);` lowers (Roslyn +
Neo JIT) to `ldstr/ldloc refArg; newobj(refArg)`. The eval-stack-to-register
lowering frequently **reuses the arg's register as the newobj dest register**
(both are the transient top-of-stack). When that happens:

- The dest register's ref slot = `frameRefBase + dstRefOffset`.
- The arg register's ref slot = `frameRefBase + argRefOffset`.
- Same register ⇒ `dstRefOffset == argRefOffset` ⇒ the two ref slots coincide.

The arg's mStack index is the index where `ldstr`/`ldloc` placed the arg object.
Because the arg's ref slot == `newobjDstIdx` (the dest ref slot), and the arg
object lives at that very index, **`argIdx == newobjDstIdx`**. Then
`mStack[newobjDstIdx] = ins` overwrites the arg object with the new instance,
BEFORE `CopyNeoCallArguments` runs. `CopyNeoCallArguments` copies the (now-stale)
index into the ctor frame; the ctor reads `mStack[newobjDstIdx]` and gets `ins`,
i.e. the ctor receives `this` as the aliased reference argument.

Instrumented evidence on HEAD (getter for `TestA.Instance`, `new TestA("testerror")`):

```
[Newobj] newobjDstIdx=6 frameRefBase=4 dstRefOff=2 retDstOff=16 mStack.Count=9
  mStack[6] BEFORE overwrite = String=testerror        # arg object at the dest slot
  retDstSlot(frameBase+16) value=6                     # dest primitive == arg index
  prim[0] srcOff=16 sz=4 srcVal=6  (== retDstOff)      # arg source IS the dest register
  ref[0] srcRefOff=2 -> mStack[6]=String               # arg ref slot == dstRefOff
```

vs. the working (no-static) probe `new TestA("abc")`:
```
[Newobj] newobjDstIdx=6 frameRefBase=6 dstRefOff=0 retDstOff=0 mStack.Count=12
  mStack[6] BEFORE overwrite = null                    # dest slot unused
  prim[0] srcOff=12 sz=4 srcVal=8  (!= retDstOff)      # arg in a DIFFERENT register
```

So the alias is `primSrc == ip->DstOffset` (arg source offset == dest register
offset) AND `argIdx == newobjDstIdx`.

## The existing contract and why it missed this

The `neo-newobj` capability's "Q-NEWOBJ dest/arg aliasing contract" (verified by
a `new T(intArg)`-after-`newarr` investigation) states the frame allocator gives
every temp a distinct region and "no special newobj-lowering fix is required."
That holds for a **primitive** arg (its slot value is the int itself, not an
mStack index — storing the instance in an adjacent slot cannot corrupt it). It
does NOT hold for a **reference** arg, whose slot value IS an mStack index that
can equal `newobjDstIdx`. This change completes the contract for reference args.

## Decisions

### D1: Runtime re-base in the newobj arm (NOT a JIT/lowering rewrite)

Fix at the newobj arm, not the JIT. The JIT dest/arg allocation is correct in
isolation (distinct registers get distinct regions); the alias is an unavoidable
consequence of eval-stack register reuse, and the newobj arm is the single site
that observes both the dest ref slot and the arg copy. Re-basing the colliding
arg to a fresh mStack slot before storing the instance is the minimal, local
fix. No JIT/optimizer/object-model change.

**Alternatives rejected:**
- *Defer `mStack[newobjDstIdx] = ins` past `CopyNeoCallArguments`.* Insufficient:
  the ctor reads the arg via `mStack[argIdx]`, and `argIdx == newobjDstIdx`, so
  the instance must NOT occupy `newobjDstIdx` while the ctor runs. The arg and
  the instance cannot share one slot; one must move. (Verified empirically —
  deferring the primitive dest write alone changed nothing.)
- *Fresh mStack slot for the instance during the ctor.* Works but is more
  invasive (changes the `this` index + needs a post-ctor move). The re-base is
  strictly smaller and leaves the established `this = newobjDstIdx` path intact.
- *Pre-reserve frame ref slots so a `ldstr` temp can never land on a ref slot.*
  Out of scope (a frame-allocator change with broad blast radius) and not
  necessary — the newobj arm is where the alias becomes fatal, so it is the
  right place to break it.

### D2: Sound reference-arg discriminator (ref-map membership)

A naive "re-base any Size==4 arg whose value == newobjDstIdx" is UNSOUND: a
primitive `int` arg whose value coincidentally equals `newobjDstIdx` would be
treated as a colliding reference and corrupted. The fix discriminates via the
call's **ref-source map**: a reference arg's register has a ref slot, so its
`RefOffset` appears in `map.RefSrc`. The aliased arg's register IS the dest
register, so the test is `dstRefOffset ∈ map.RefSrc` AND
`*(int*)(frameBase + ip->DstOffset) == newobjDstIdx`. A primitive arg (RefCount 0)
never appears in `map.RefSrc`, so it is never re-based.

### D3: Re-base mechanics

On a confirmed collision:
```csharp
int aIdx = *(int*)(frameBase + ip->DstOffset);   // == newobjDstIdx
mStack.Add(mStack[aIdx]);                         // arg object -> fresh slot
*(int*)(frameBase + ip->DstOffset) = mStack.Count - 1;  // rewrite source
```
Then `mStack[newobjDstIdx] = ins` runs as before. `CopyNeoCallArguments` now
copies the FRESH index into the ctor's arg slot; the ctor reads `mStack[fresh]`
= the arg object. `this` = `mStack[newobjDstIdx]` = `ins`. The dest register's
later `*(int*)retDstPtr = newobjDstIdx` overwrites the rewritten source (fine —
`CopyNeoCallArguments` has already consumed it, and the dest must hold the
instance index after the call). `map.RefSrc` is NOT consumed by
`CopyNeoCallArguments` for an IL callee (only `PrimitiveSize` is), so the ref
slot is unaffected; the ctor reads the arg through the primitive index only.

### D4: Probe must FAULT (child-1/child-2/child-12 discipline)

The pass criterion is "ran without throwing"; a wrong-value probe will not fail.
Each probe asserts the ctor's reference arg round-tripped (`(object)tag == "..."`)
and trips a deliberate `1/0` on mismatch. Stash-toggle confirms both TCs FAULT
on HEAD (`DivideByZeroException`) and PASS with the fix. A second TC (a distinct
static field / value) guards against a fix that only handles the first call.

## Risks / Trade-offs

- **[Discriminator false-negative]** If some reference-arg producer does NOT seed
  the ref map, the re-base would not fire and that arg would still clobber. All
  reference args flow through the standard map builder (`Optimizer.Neo.cs`
  `refSrc.Add(srcInfo.RefOffset)`), so this is reliable; the full NeoStep smoke
  is the safety net.
- **[register-allocator drift]** The alias depends on the JIT reusing the arg
  register as the dest. If a future allocator change stops reusing it, the fix
  becomes dead-but-harmless (the collision test simply never triggers). The probe
  pins the current behavior.
- **[Performance]** The re-base is a rare-path `mStack.Add` + one int rewrite;
  the common (non-aliasing) newobj adds only a cheap ref-map scan gated on
  `map.RefSrc != null`.

## Open Questions

- Does any NeoStep smoke pattern rely on the buggy clobber? No — the clobber
  always corrupts a reference ctor arg, which is never desirable. The smoke
  re-verify (339/0) confirms no regression.
