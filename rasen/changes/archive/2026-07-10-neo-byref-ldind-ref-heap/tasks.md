# Tasks: neo-byref-ldind-ref-heap

Child 15 of the Neo completion portfolio (lead-6 MEDIUM#8). Scope: a single
opcode-arm family (`ldind_ref` / `stind_ref` on a heap-IL-ref-field byref).

## Tasks

- [x] **T1 — Reproducer.** Construct a heap IL class with a reference field;
  obtain a byref via `ldflda` (through a `ref` param helper) and `ldind_ref` it.
  Confirm the gap on HEAD (NIE "Step 17: ldind_ref on a heap IL ref field is
  deferred"). (Probes added to `TestCases/NeoStep17Test.cs`.)

- [x] **T2 — JIT marker.** `JITCompiler.cs`: add
  `NeoLdfldaHeapIlRefFieldMarker = 0x4` (Operand4 bit). Stamp it in
  `case Code.Ldflda:` when `type is ILType && !fieldType.IsValueType &&
  !fieldType.IsPrimitive`. Extend the F-10-R1 gate (`case OpCodeREnum.Ldflda:`
  in the type-spec pass) to clear it for an in-frame-VT source (parity with the
  F-10 clear).

- [x] **T3 — Runtime Ldflda arm.** `ILIntepreter.Neo.cs` `case Ldflda:`: a new
  branch (first, before F-10) keyed on `heapIlRefFieldMarker && objIdx >= 0`;
  produce byref `(objIdx, ip->Operand3)` (ReferenceOffset, UNFLAGGED).

- [x] **T4 — Runtime Ldind_Ref / Stind_Ref arms.** Add a content-based arm
  (`mStack[objIdx] is ILTypeInstance`) AFTER the frame-native / F-7B-bit-30 /
  CLR-array / CLR-object arms; route to `ManagedObjects[off]`.

- [x] **T5 — Verify.** NeoStep17 gate green (51/0/0); full NeoStep smoke green
  (263/0/0); stash-toggle (3 probes FAIL-on-HEAD NIE -> PASS-after; control
  PASSes both); Legacy-neutral (plain-Debug 0 errors). Stable across 12+ NeoStep17
  runs (no flake).

## Design pivot (load-bearing, recorded in design.md 3.4)

- [x] **D1 (REJECTED) — bit-31 offset flag.** First implementation stamped
  `NeoHeapIlRefFieldByrefFlag = 0x80000000` on the byref's offset half and
  dispatched on `(off & 0x80000000) != 0`. Introduced an INTERMITTENT REGRESSION
  in the pre-existing `NeoStep17_ClrObjectRefFieldReadWrite` (4d.3) probe
  (~30% flake; stable 10/10 on HEAD). Root cause: CLR FieldInfo hashes (the 4d
  offset) are non-deterministic and can set bit 31 -> mis-dispatch to
  GetNeoILInstance -> NIE.

- [x] **D2 (ADOPTED) — content-based dispatch.** Dispatch on
  `mStack[objIdx] is ILTypeInstance` (placed after the F-7B bit-30 / array /
  CLR-object arms). Unambiguous for all offset values; removed the bit-31
  constant. NeoStep17 stable 12/12.

## Out of scope (design.md 4)

- ldobj/stobj on a heap IL ref field (not a C#-emitted shape).
- Nested-field (multi-level ldflda chain) byref.
- Cross-frame (non-inlined) IL-byref to a heap ref field (structurally sound via
  CopyNeoCallArguments' raw mStack-object copy, not independently probed).
