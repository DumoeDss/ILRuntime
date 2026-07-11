# Tasks - neo-debugger-ilvt-local

> Reconstruct an IL-value-type LOCAL's fields in the Neo debugger frame
> inspection. Extends neo-debugger-neo-frame (which left the IL-VT local as a
> placeholder string). Neo-only (`#if ENABLE_NEO_MODE`); Legacy unchanged.

## 1. Probe-first: confirm the gap on HEAD

- [x] Constructed a reproducer: added `VtLocal { int X; string S; }` struct +
      `ProbeVtLocal` / `ProbeVtLocalMutate` methods to
      `TestCases/NeoDebuggerFrameProbe.cs`.
- [x] Confirmed on HEAD: `.LocalInfo` renders
      `VtLocal vt = <IL value-type local: reconstruction deferred>` -- the
      struct's fields (X, S) are absent. The local IS allocated (the `vt.X = 4242`
      stfld keeps it live; not optimized away). Gap confirmed.

## 2. Fix `DebugService` (Neo-gated)

- [x] `GetLocalVariableInfo` Neo arm: capture `frameRefBase =
      topFrame.ManagedStackBase` and forward it to `ReadNeoLocalValue`.
      (`ILRuntime/Runtime/Debugger/DebugService.cs`)
- [x] `ReadNeoLocalValue`: added `frameRefBase` parameter; the IL-VT branch now
      calls `ReadNeoIlVtLocalFields` instead of returning the placeholder.
- [x] New helper `ReadNeoIlVtLocalFields`: walks `i` in
      `[0, ilType.TotalFieldCount)`, dispatches per field on the field's IType:
      primitive -> `ReadNeoFramePrimitive(vtPrimBase + off.PrimitiveOffset)`;
      reference/enum/CLR-struct -> `mStack[vtRefBase + off.ReferenceOffset]`;
      nested IL-VT field -> recurse ONE level (deeper -> placeholder + note).
      Renders `"{ Type Name = Value, ... }"`. Per-field try/catch -> a bad field
      renders `<unreadable fN>` (does not abort the struct).
- [x] Depth-limited overload (depth > 1 -> placeholder + note).

## 3. Verify (the self-check gate)

- [x] Added Cell 4 (`ProbeVtLocal`): asserts X=4242 (primitive sub-region) +
      S="vt-field-A" (reference sub-region) appear in `.LocalInfo`.
- [x] Added Cell 5 (`ProbeVtLocalMutate`, ADVERSARIAL): assigns vt.X=1111 then
      MUTATES to 8888; asserts the reconstruction reads the LIVE frame bytes
      (8888), not the stale 1111.
- [x] Stash-toggle: placeholder active -> Cells 4+5 FAIL (4/6); fix restored ->
      6/6 PASS. Bind proven.
- [x] NeoDebuggerFrame gate: 6/6 cells (4 original held + 2 new VT-local).
- [x] NeoStep smoke: 253/0/0 (no regression).
- [x] Legacy-neutral: plain `Debug` build of ILRuntimeTestCLI = 0 errors.

## 4. Artifacts

- [x] `design.md` (PURE ASCII; verified 0 non-ASCII chars).
- [x] `tasks.md` (this file).
