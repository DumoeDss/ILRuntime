# Planner Findings — neo-raw-stfld-array-element

Child 19 of the `neo-overhaul` portfolio (a child-4 deferred shape). Autonomous LEAD run (Tier A,
`--no-gate`). This is the PROPOSE unit: investigation done, artifacts written, handoff to the apply
worker.

## The array-element owner byref shape (the load-bearing detail, verified)

`clrStructArray[i].field = x` lowers to `ldelema <StructType>; stfld <field>`. The Neo `ldelema`
handler, for a CLR (non-IL) value-type-element array (`ILIntepreter.Neo.cs:5729-5749`, the `else`
branch after the `ILTypeInstance[]` case), stamps the 8-byte byref as:

```
+0 int = arrIdx       // mStack index of the System.Array
+4 int = elementIdx   // the ELEMENT INDEX (NOT a byte offset)
```

This is the SAME `(objIdx, off)` convention the `stind_*`/`ldind_*` consumers already decode
(`ILIntepreter.Neo.cs:5170-5340`): `objIdx == -1` -> frame-native; `mStack[objIdx] is Array` ->
`cArr.SetValue(v, off)` / `cArr.GetValue(off)` with `off` = element index. Legacy's raw `Stfld`
writeback confirms the semantics (`ObjectTypes.ArrayReference`, `ILIntepreter.Register.cs:3154-3158`):
`arr.SetValue(obj, idx)` -- box/mutate/writeback by element index.

## The fix (and the LEAD's suggested mechanism SUPERSEDED)

**Chosen:** mirror Legacy's `ObjectTypes.ArrayReference` writeback exactly -- box/mutate/unbox:
`boxedElem = cArr.GetValue(elementIdx); f.SetValue(boxedElem, value); cArr.SetValue(boxedElem,
elementIdx)`. `value` is already boxed by field category earlier in the Stfld handler (`:4044-4057`);
`f` is the resolved FieldInfo; `objIdx`/`off` are already decoded (`:4066-4067`). No new helper, no
offset math. This is child-4's CLR-VT-OWNER Stfld box/mutate/unbox pattern with
`Array.GetValue`/`SetValue` in place of `ReadNeoValueType`/`WriteNeoValueType`.

**The LEAD's suggested mechanism (resolve the byref to the element base + `Marshal.OffsetOf`
[child-15's `ResolveClrStructFieldByteOffset`] + write the field) is SUPERSEDED** by the simpler
reflection round-trip. `Marshal.OffsetOf` is the right tool only when forming a raw frame byte
address; the array element lives in the Array's CLR-managed backing store (NOT the Neo frame), so
`Array.GetValue`/`SetValue` + `FieldInfo.SetValue` is correct, simpler, and byte-identical to Legacy.
No offset math is needed.

## Depth verdict: TRACTABLE (for Stfld)

~10-15 lines, 2 sites (`:4076-4077` value-type declaring [reachable, the ~4 hits]; `:4102-4103`
ref-type declaring [unreachable via `ldelema` on a ref-type-element array, which throws at `:5744`,
but routed through the same pattern for symmetry]). No JIT / optimizer / object-model / CLR-binding
change. No new opcode, no new helper (inline). The owner byref shape is already produced (`ldelema`)
and consumed (`stind`/`ldind`) elsewhere in the Neo interpreter; the fix is a known-correct pattern
(Legacy parity + child-4 precedent).

## Ldfld is NOT symmetric -> DEFERRED (deeper; rationale)

The LEAD's prompt allowed handling `Ldfld` "if symmetric." It is NOT, so this change scopes it out:

- A raw `Stfld` value-type owner is ALWAYS a byref (`ldloca`/`ldelema`) -- you cannot write a field
  without the address -- so child-4's `objIdx == -1` (frame) vs `mStack[objIdx] is Array` (array)
  discriminator is unambiguous.
- A raw `Ldfld` value-type owner can be EITHER flat managed bytes (`ldloc` by-value of a CLR struct
  local; child-4's representation; the `:3890-3902` branch boxes via `ReadNeoValueType`) OR a byref
  (`ldelema` for an array element). The Neo frame is UNTYPED (no per-slot `ObjectType` tag, unlike
  Legacy's `reg->ObjectType` at `Register.cs:3200`), so the handler cannot tag-distinguish.
- A naive `mStack[objIdx] is Array` discriminator on Ldfld is UNSAFE: a by-value struct local whose
  first int field coincides with a valid mStack index holding an Array (e.g. a method with an array
  argument and a struct field value of that index) would false-positive. This is reachable in
  principle, so a naive fix would corrupt.
- A robust Ldfld fix needs a JIT-time marker on the raw `Ldfld` (child-15
  `NeoLdfldaClrStructLocalFieldMarker` style) tagging the owner as an array-element byref + a runtime
  branch on it. JIT + runtime change -> deeper -> follow-up.
- The raw `Ldfld` ref-type-branch array NIE (`:3928-3929`) is unreachable (same `ldelema` guard) --
  left as a defensive tagged NIE. The raw `Ldfld` value-type array-element read (`:3890-3902`) is a
  latent silent-corruption (no NIE; not in the documented hit surface) if ever exercised -- NOT
  touched.

## Probe design notes (gotchas for the apply worker)

- INT fields (not float) in the host probe struct, to sidestep the pre-existing unrelated Neo bugs:
  `addi`-on-float (Roslyn lowers `a.X += c` to an integer add of float bits) and
  `conv.i4`-float-bit-reinterpret (child-16 candidate; NOT yet shipped as of this writing).
- The read-back is HOST-side (`TestCLRBinding.NeoArrElemFieldSum`), so the probe verifies the Neo
  `Stfld` WRITE landed WITHOUT depending on Neo `Ldfld` (deferred) or Neo float arithmetic (broken).
- The probe MUST FAULT on HEAD: `arr[1].A = 4242` throws the `:4077` tagged NIE, which propagates ->
  test failure. (Per child-1/child-2 discipline, a wrong-value probe that doesn't throw would NOT
  fail; this probe throws, so it is a sound fault probe.)
- TC2 writes TWO distinct element indices (0 and 5) and sums all four field reads, to prove the
  element-index decode is correct (defeats a constant-0 or arrIdx-as-elementIdx confusion -- a wrong
  decode would land writes in the wrong element and the host read-back sum would mismatch).
- Build `TestCases` with plain `Debug` (NEVER `Debug_Neo`); build only the CLI with `Debug_Neo
  --no-incremental`. NeoStep smoke baseline is **352/0** (after child 18).

## Eliminated hypotheses

- **"Use `Marshal.OffsetOf` to compute the field's byte offset within the element, then write
  directly."** Eliminated: unnecessary (the element lives in the Array backing store, accessed via
  `GetValue`/`SetValue`; no raw pointer needed) and more fragile than reflection. Superseded by D1.
- **"Handle Ldfld symmetrically in the same change."** Eliminated for this child: the untyped Neo
  frame cannot distinguish a flat-bytes Ldfld owner from an array-element-byref Ldfld owner without a
  JIT marker; a naive discriminator is unsafe (false-positive). Deferred to a child-15-style
  JIT-marker follow-up.
- **"The ref-type-declaring array branch (`:4103`) is a live hit."** Eliminated: `ldelema` on a
  ref-type-element array throws at `:5744-5746`, so the owner of a ref-type-declaring `stfld` can
  never be an Array. The branch is unreachable; routing it through the same helper is defensive only.

## Capability / spec-delta

`neo-value-types` (ADDED a requirement) -- sibling of child-4 (`neo-raw-stfld-ldfld`) and child-9
(`neo-il-instance-clr-base-field`), which share this spec. Same `## ADDED Requirements` / Requirement
+ `#### Scenario` structure as child-9's delta. No prior opcode-level requirement existed for the
array-element owner shape.

## Artifacts written

- `proposal.md`, `design.md`, `tasks.md`, `specs/neo-value-types/spec.md` (ADDED),
  `planner-findings.md` (this file).
