# Review Report: neo-raw-ldfld-clr-object-vt-field (child 29)

Reviewer: independent (reviewer-1). Branch: features/object-model-overhaul.
Scope: uncommitted working tree (JITCompiler.cs + ILIntepreter.Neo.cs + TestClass3.cs
helper + new probe). Neo-gated; Legacy is neutral by construction.

## Verification performed

- Read proposal.md + design.md + planning-context.md (child 27 / 24 / 4 context).
- `git diff` on both engine files; Read surrounding context for the raw-Ldfld
  runtime handler (ILIntepreter.Neo.cs:3967-4099), the JIT stamp site
  (JITCompiler.cs:3140-3178), the const block (JITCompiler.cs:218-248), the Ldflda
  runtime byref-producing arm (ILIntepreter.Neo.cs:1900-1972), child-27's Stfld
  mirror (ILIntepreter.Neo.cs:4250-4285), and NeoReadClrObjectField (6557-6566).
- Audited EVERY Operand4 write in JITCompiler.cs and Optimizer.Neo.cs to confirm
  the 0x2 namespace is collision-free and that no optimizer pass touches a raw
  Ldfld's Operand4.
- Built: `dotnet build ILRuntimeTestCLI -c Debug_Neo` (0 errors) +
  `dotnet build TestCases -c Debug` (0 errors). Build-server killed +
  UseSharedCompilation=false per child-25 gotcha.
- Full NeoStep smoke: **380 ran / 0 failed** (CLI net8.0, --no-build).
- Stash-toggle: stashed the two engine files, rebuilt CLI, re-ran the two probes ->
  BOTH FAULT with DivideByZeroException (TC1 at :55, TC2 at :74). Popped stash,
  rebuilt, re-ran -> both PASS. Fix is load-bearing.

## Dimension-by-dimension

### 1. The marker-vs-runtime verdict (the crux) -- MARKER IS MANDATORY

The implementer's claim is CORRECT and I tried hard to falsify it. The owner slot
of a raw Ldfld is 8 untyped bytes that is EITHER:
- (A) flat managed bytes of a CLR struct (ldloc by value) -- first int = the
  struct's first field VALUE, or
- (B) an 8-byte byref (objIdx, off) produced by ldflda/ldelema.

No runtime discriminator can provably separate A from B:
- **Range check fails**: a flat-bytes first-field value of, say, 3 is a perfectly
  valid mStack index (mStack slots are dense from 0), and a real CLR object can be
  parked at mStack[3] by earlier allocations in the same frame. Collision
  constructible.
- **Area-4d hash-lookup is NOT a safe discriminator**: for the false-positive
  shape, the +4 half is the struct's SECOND field value (an arbitrary int). It
  might not match any FieldInfo hash on the random target's type -> but (a) the
  runtime branch does not catch that failure to fall back, and (b) a second-field
  value can COINCIDENTALLY equal a real FieldInfo.GetHashCode() on the target's
  type (field hashes are arbitrary runtime ints). So even hash-miss is not
  reliable, and hash-hit-on-wrong-type is constructible.

The owner representation is fundamentally a JIT-time dataflow fact; the untyped
Neo frame cannot recover it at runtime. The marker is provably necessary. This
matches child-24's verdict and the write/read asymmetry (child-27 Stfld owner is
ALWAYS a byref -> runtime detection sufficed THERE; the READ side cannot rely on
that). NOT a finding -- design is correct.

### 2. The 0x2 marker-bit decision -- NO COLLISION, distinct bit is right

Audited every Operand4 write site:

JITCompiler.cs raw-Ldfld else-branch is the ONLY place raw `OpCodeREnum.Ldfld`
Operand4 is written (0x1 = child-24, 0x2 = this PR). All other Operand4 writes
target DIFFERENT opcodes:
- Ldflda F-6/F-10/ref-heap markers (Ldflda opcode, not Ldfld).
- `Ldfld_Value`/`Ldfld_Ref` Operand4 = fieldType.GetHashCode() -- but those are
  TYPED opcodes produced by the if-branch (ILType declaring), NOT the raw Ldfld.
- Call_Redirect / callvirt constrained flags -- call opcodes.
- IsIntermediateBranching jumpTarget remap -- branch opcodes.

Optimizer.Neo.cs:
- LowerNeoOffsets Ldfld case (945-971) sets ONLY DstOffset/SrcOffset from R1/R2;
  explicit comment at 943-944 ("Do NOT touch Operand/Operand4 -- field identity
  lives in OperandLong"). Confirmed by reading the case body.
- Push-deletion remap (1727-1729) is opcode-gated to `IsIntermediateBranching`
  (1725) -- Ldfld is excluded, so neither 0x1 nor 0x2 can be decremented. (The
  design's rationale "0x2 never > a typical removedIndex" is looser than the real
  reason; the real reason is the opcode gate. Either way the marker survives.)
- Box/Isinst/Castclass Operand4 = ref2 (936) is a different opcode.

So 0x2 is genuinely free in the raw-Ldfld Operand4 namespace; no collision with
0x1 or any Ldflda-opcode marker (disjoint opcode -> disjoint namespace).

Mutual exclusivity (Ldelema XOR Ldflda) is guaranteed twice over: (a) a CIL
instruction has exactly one immediate predecessor, and (b) the JIT uses `else if`
so even a hypothetical double-match stamps only the first. The runtime checks 0x1
before 0x2 (child-24 block first), so 0x1 would win on any conflict.

Distinct-bit (vs reusing 0x1 as a generic byref-owner bit) is the right call:
keeps child-24's shipped green const literally untouched, gives each runtime
branch a single clean decode semantic. NOT a finding.

### 3. ins.Previous == Ldflda reliability + Operand4 survival -- CONFIRMED

- The JIT dump in design.md shows `4:ldflda r5,r0,...; 5:ldfld r1,r5,...` --
  ldflda dest r5 IS the ldfld owner register r5. Immediate predecessor confirmed.
- readonly./constrained. are prefixes TO ldflda (precede it), so they never sit
  between ldflda and ldfld -- Previous stays ldflda.
- volatile./unaligned. ON the ldfld itself WOULD sit between -> marker would not
  stamp -> flat-bytes path runs -> continued silent corruption for that narrow
  shape (volatile struct-field read), NO crash. Documented gap, fails safe, same
  as child-24.
- ref-local indirection (`ref var p = ref obj.S; p.a`) lowers with ldloc (not
  ldflda) as the ldfld predecessor -> marker would not stamp. Documented gap,
  fails safe, same as child-24. The probe does not use ref locals.

Operand4 0x2 survival through LowerNeoOffsets + TypeSpecialize confirmed by the
audit above (no pass touches raw-Ldfld Operand4). NOT a finding.

### 4. Runtime branch correctness -- CONFIRMED

- Decode `(objIdx, structFieldHash)` at 4022-4023 matches the Ldflda heap-CLR-
  object else-branch encoding at 1968-1969 (`*(int*)(dst+0)=objIdx;
  *(int*)(dst+4)=fieldPrimOff`, where for a CLR object fieldPrimOff IS the struct
  field's FieldInfo hash per the comment at 1959-1967).
- `NeoReadClrObjectField(AppDomain, target, off)` (6557) resolves the hash via
  `ct.GetFieldValue(fieldHash, target)` on the containing object's CLRType ->
  returns a boxed copy of the WHOLE struct field. This call is byte-identical to
  child-27's Stfld call at 4280 -- the READ direction is exactly the first half
  of child-27's box/mutate/unbox.
- `f` is the LEAF field on the struct (f = ct.GetField(fieldHash) at 3975, where
  ct = the struct type); `f.GetValue(boxedStruct)` reads the leaf off the boxed
  whole-struct copy. Correct (not the struct field itself).
- Defensive `objIdx == -1` sub-branch reads flat bytes from `frameBase + off`.
  Verified against the Ldflda frame-native/F-6 arms (1918-1953): when objIdx ==
  -1, the +4 half is always a valid frame byte address pointing at the struct's
  flat bytes. ReadNeoValueType(ct.TypeForCLR=the struct, frameBase, ref cur=off,
  ownerSz) boxes the whole struct, f.GetValue reads the leaf. Correct safety net.
- F-10 / heap-IL-instance byref: objIdx >= 0, mStack[objIdx] is ILTypeInstance ->
  tagged NIE (4041-4042). Honest deferral, mirrors child-27's Stfld NIE at
  4278-4279. The string message names the deferral scope correctly.
- The `objIdx >= 0 ? mStack[objIdx] : null` guard at 4038 is slightly more
  defensive than child-27's bare `mStack[objIdx]` (handles a corrupt negative
  idx as NRE rather than IndexOutOfRange). Harmless improvement.
- Box-roundtrip covers all field categories: the existing dest marshalling at
  4092+ (NeoWritePrimitiveToFrame / WriteNeoValueType / ref-slot write) handles
  fldVal by fldClrType category unchanged. Primitive int/float, nested VT, and
  ref fields all round-trip through f.GetValue -> dest marshal correctly.

NOT a finding.

### 5. No-overfire / regression -- CONFIRMED

Full NeoStep smoke = 380/0. The new else-if is ordered correctly: child-24's 0x1
array-element block (3983) is checked FIRST, then this PR's 0x2 block (4003),
then the flat-bytes else (4047). A raw Ldfld can carry at most one marker bit
(mutually exclusive predecessors), so the ordering cannot misroute.

child-24's `NeoRawLdfldArrayElementByRefMarker = 0x1` const is LITERALLY
untouched (verified by diff -- only the new 0x2 const + the `else if` stamp are
added; child-24's lines are byte-identical). child-24's probes
(NeoStepRawLdfldArrayElement TC1/TC2/TC3) were invoked in the 380/0 smoke and
passed. child-27's Stfld probes (NeoStepRawStfldClrObjVtField TC1/TC2) were
invoked and passed. child-4/21 flat-bytes and raw-Ldfld-CLR-struct families are
in the 380/0 (the flat-bytes path moved under a new `else`, logically
equivalent). NOT a finding.

### 6. Read-back correctness -- CONFIRMED

- TC1: host sets o.S.a = 111 via BuildNeoClrObjVtFieldOwner(111,222,333); IL does
  `int x = o.S.a` (one raw Ldfld); asserts x == 111 else 1/0. On HEAD x = objIdx
  (a small int) -> != 111 -> DivideByZero (confirmed by stash-toggle). Arithmetic
  is trivially reachable.
- TC2: three separate raw Ldfld ops (o.S.a, o.S.b, o.S.c), per-field OR assert
  (x!=111 || y!=222 || z!=333) else 1/0. Stash-toggle confirmed DivideByZero on
  HEAD. Each ldfld carries its own leaf-field hash, so a hash collision or a
  wrong-field/alway-read-a fix would fail at least one clause. The read-back
  genuinely proves the Ldfld READ round-trip (host writes the values; IL reads
  them back through the new runtime branch).

NOT a finding.

## Findings

### Minor

- **M1 (this-PR, doc/prose mismatch):** proposal.md:49-50 describes TC2 as "three-
  field read + IL sum" and design.md:164-165 references "111+222+333 = 666 ... A
  wrong-field/hash-collision fix would also miss 666." The ACTUAL probe code
  (NeoStepRawLdfldClrObjVtFieldTest.cs:72) does a per-field OR assertion
  (`x != 111 || y != 222 || z != 333`), NOT a sum. The implemented check is
  STRICTER than described (a wrong-field fix fails the per-field clause just as
  the design's rationale intends, only more directly). The design's correctness
  argument still holds; only the prose ("sum"/"666") is inaccurate. Suggest a
  one-line doc touch so the probe description matches the stronger per-field
  reality. Does not block.

### Trivial

- **T1 (this-PR, naming):** the decoded local `off` (ILIntepreter.Neo.cs:4023)
  holds a FieldInfo HASH (structFieldHash), not a byte offset. The comment at
  4017-4021 clarifies this. child-27's Stfld mirror uses the same `off` name
  (4227), so this is consistent with the sibling; renaming for clarity is
  optional and would diverge from the sibling.
- **T2 (this-PR, comment line ref):** ILIntepreter.Neo.cs:4009 comment cites "the
  ldflda heap-CLR-object else branch (~:1955)" -- the actual else-branch body is
  at ~1968 (1955 is the start of the enclosing else region). Negligible drift.

### Pre-existing

None material. (An initial `\ ` rendering appeared in Grep -A output for
TestClass3.cs:271 and Optimizer.Neo.cs:972/6568; Read confirms those are normal
`//` comment lines -- Grep display artifact, not real characters, and not in this
PR's diff regardless.)

## Verdict: APPROVE-WITH-FINDINGS

The change is correct, minimal (~73 lines across two engine files + helper +
probe), and mirrors the shipped child-24 / child-27 idioms exactly. The JIT
marker is provably necessary (no runtime discriminator can distinguish a flat-
bytes owner from a byref owner in the untyped Neo frame -- collision
constructible on both range and hash axes). The 0x2 bit is collision-free in the
raw-Ldfld Operand4 namespace (audited every write site in both files; the only
two occupants are 0x1 and 0x2, mutually exclusive by CIL predecessor, and no
optimizer pass touches raw-Ldfld Operand4). The runtime branch is a faithful READ
-direction mirror of child-27's Stfld box/mutate/unbox (first half), with a
correct defensive frame-native sub-branch and an honest F-10 NIE. Full smoke is
380/0 and the stash-toggle proves both probes FAULT on HEAD (silent corruption ->
DivideByZero) and PASS with the fix; child-24/27 sibling probes remain green.

Only Minor/Trivial doc+naming nits remain (M1 is the one worth a one-line touch:
the probe is a stricter per-field check than the "sum" the prose describes). No
Blocker, no Major. APPROVE-WITH-FINDINGS.
