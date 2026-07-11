# Design - neo-debugger-neo-frame

> Scope-AWARE dump-gate for Neo debugger variable inspection. Probed-by-reasoning
> on HEAD `50d0e7e2` (the frame model + the F-4 dispatch are both
> source-of-truth-confirmed by reading the code; no probe-then-revert needed --
> the guard itself returns "not supported yet", so there is no HEAD behavior to
> revert). The dump decides SHIP vs SEQUENCE. Legacy is the REFERENCE; every
> shipped edit is Neo-only (`#if ENABLE_NEO_MODE`).

## 0. Dump-gate verdict matrix

| # | Question | Finding (HEAD `50d0e7e2`) | Verdict |
|---|----------|---------------------------|---------|
| 1 | What do `GetThisInfo`/`GetLocalVariableInfo` need? | A `StackObject*` slot read + value/type recovery via `StackObject.ToObject` (`:244`, `:278`). For `this`: `ManagedStack[arg->Value]` + ILTypeInstance/adaptor unwrap (`:218-230`). For locals: `Definition.Body.Variables[i]` for the type, `Add(LocalVarPointer, i)` for the slot (`:276-277`). | Baseline established |
| 2 | Can a Neo frame local's value+type be recovered the SAME way F-4 / ReadNeoValueType recover a Neo slot? | YES. `frame.LocalVarPointer` ALIASES `frameBase` (`ILIntepreter.Neo.cs:905`), `CompiledFrame.LocalInfos` carries per-slot `{Offset, RefOffset, Size, RefCount}` (`JITCompiler.cs:67-73`), `Variables[i]` is 1:1 with `LocalInfos[paramCount+i]` (`JITCompiler.cs:1709-1799`), and `frame.ManagedStackBase` IS the frame's `frameRefBase` (`ILIntepreter.Neo.cs:909`). The F-4 indexer's per-TypeForCLR dispatch applies verbatim. | SHIP (tractable) |
| 3 | Is var inspection SMALL or LARGE? | SMALL. It is a per-slot dispatch in TWO methods + one helper, keyed off data the frame already carries. NO protocol work, NO ABI change. IL-VT locals need reconstruction (SEQUENCE). | SHIP the tractable slice; SEQUENCE IL-VT |

## 1. What the Legacy paths need (baseline)

`DebugService.GetLocalVariableInfo` (`:263-296`):
- Type source: `m.Definition.Body.Variables[i]` (`:276`) -- the local's
  `VariableDefinition`; its `.VariableType` is the type.
- Slot read: `Add(topFrame.LocalVarPointer, i)` (`:277`) -- a `StackObject*`
  advanced by `i` 12-byte unions.
- Value+type recovery: `StackObject.ToObject(val, appDomain, managedStack)`
  (`:278`) -- reads the `StackObject.ObjectType` tag + `Value`/`ValueLow`, and
  for `ObjectTypes.Object` indexes the managed stack.

`DebugService.GetThisInfo` (`:203-261`):
- `arg = Minus(topFrame.LocalVarPointer, m.ParameterCount)` then `--arg` if
  `HasThis` (`:210-212`) -- the slot BEFORE the params (the `this`).
- Reference-`this` unwrap (`:213-217`): if `arg->ObjectType == Reference`,
  follow the pointer.
- `this` object recovery (`:218-230`): `ManagedStack[arg->Value]`; if it is an
  `ILTypeInstance` use it directly, if a `CrossBindingAdaptorType` take
  `.ILInstance`.
- Field enumeration (`:233-259`): `instance.Type.TypeDefinition.Fields`, read
  each non-static field via `instance.Fields[idx]` + `StackObject.ToObject`
  (`:243-244`).

**How Legacy recovers a variable's value+type:** the `StackObject` 12-byte union
carries an `ObjectType` tag, so `ToObject` can discriminate primitive/object/null
WITHOUT an external type. Under Neo the slot bytes are UNTAGGED raw primitives or
a 4-byte `mStack` index -- so the type MUST come from `Variables[i]` and the slot
layout from `LocalInfos[i]`. That is the ONLY structural difference; everything
else (the field/type-name formatting, the per-iteration try/catch) is reusable.

## 2. The Neo frame model (the data the dispatch reads)

Under `ENABLE_NEO_MODE`, `ExecuteNeo` (`ILIntepreter.Neo.cs:828-914`) builds a
compact frame:
- `byte* frameBase = esp` (`:858`) -- the frame's raw-bytes region (params +
  locals + temp registers, sized to `nf.TotalStructSize`, `:851`).
- `AutoList mStack = stack.ManagedStack` (`:849`) -- the shared reference heap.
- `int frameRefBase = mStack.Count` (`:877`) -- this frame's reference slots are
  reserved at `mStack[frameRefBase .. frameRefBase + nf.TotalRefSize)`
  (`:878-879`). `StackFrame.ManagedStackBase = frameRefBase` (`:909`) -- so the
  debugger recovers `frameRefBase` from the `StackFrame` it already peeks.
- `StackFrame.LocalVarPointer = (StackObject*)frameBase` (`:905`) and
  `BasePointer = (StackObject*)frameBase` (`:906`) -- the `StackObject*` ALIASES
  the `byte* frameBase` (reinterpret cast). So the Neo arms cast it back:
  `byte* frameBase = (byte*)topFrame.LocalVarPointer`.

`CompiledFrame` (`JITCompiler.cs:74-108`) carries:
- `StackSlotInfo[] LocalInfos` (`:80`) -- one entry per param + local + temp
  register. Layout set by `AllocateLocalStackSpaces` (`:1648-1836`): indices
  `[0..paramCount)` = params, `[paramCount..paramCount+varCount)` = locals
  (`:1701-1799`), `[paramCount+varCount..)` = temp registers. So local `i` =
  `LocalInfos[method.ParameterCount + i]`.
- `StackSlotInfo{Offset, RefOffset, Size, RefCount}` (`:67-73`): `Offset` = the
  primitive-region byte offset within `frameBase`; `RefOffset` = the
  reference-region index relative to `frameRefBase`; `Size` = the primitive byte
  width; `RefCount` = the reference-slot count.
- `bool[] LocalIsReference` (`:92`) -- true for reference-typed slots (the
  `frameBase + Offset` word is an `mStack` index, sentinel -1 = null).

Per-local shape (`AllocateLocalStackSpaces:1709-1799`):
- **primitive** (`!vt.IsValueType` false + `vt.IsPrimitive`): `Size` =
  `appdomain.GetPrimitiveSize(ivt)`, `RefCount = 0` (`:1785-1798`).
- **reference** (`!vt.IsValueType`): `Size = 4`, `RefCount = 1`,
  `LocalIsRef[..] = true` (`:1774-1784`). `frameBase + Offset` holds the
  `mStack` index (or -1 for null).
- **CLR value-type local** (F-MAJ-1): stored as FLAT MANAGED BYTES in the
  primitive region, `Size = GetNeoValueTypeManagedSize(...)`, `RefCount = 0`
  (`:1731-1758`). Recover via `ReadNeoValueType(localType.TypeForCLR, frameBase +
  Offset, ref _, Size)` -> the boxed struct.
- **IL value-type local** (`ivt is ILType`): spans BOTH a primitive sub-region
  (`Size = il.TotalPrimitiveSize`) AND a reference sub-region
  (`RefCount = il.TotalReferenceCount`) (`:1716-1728`). The boxed instance is
  NOT recoverable from the two regions without reconstruction -- the SAME edge
  the F-4 indexer tags as NIE. SEQUENCE.

## 3. The fix design -- frame-local dispatch (mirrors F-4)

The F-4 indexer (`ILTypeInstance.this[index].get`, `ILTypeInstance.cs:410-455`)
shipped the per-shape dispatch for a Neo slot, keyed on the field's
`IType` (`type.GetField(index, out _)`) + `ILTypeFieldOffset`. The frame-local
dispatch is the SAME shape, keyed on the local's `IType` +
`StackSlotInfo`. There is ONE structural difference: F-4 reads a HEAP instance's
fields (offset from `GetFieldOffset`, mStack from the instance's `ManagedObjects`);
the frame-local path reads a FRAME slot (offset from `StackSlotInfo.Offset`,
mStack from `frameRefBase + StackSlotInfo.RefOffset`). The per-shape value-read
is identical.

### 3.1 The local's `IType` resolution (single source of truth)

The JIT resolves each local's `IType` at `JITCompiler.cs:1711`/`:1788` via
`appdomain.GetType(vt, declaringType, method)`, where `vt =
body.Variables[i].VariableType`. Generic parameters are handled by the same
helper. The Neo arm resolves the SAME way:
```
var vd = m.Definition.Body.Variables[i];
IType lt = vd.VariableType.IsGenericParameter
    ? method.FindGenericArgument(vd.VariableType.Name)   // ILMethod:782
    : appdomain.GetType(vd.VariableType, m.DeclearingType, m);  // ILMethod:786
```
This is byte-consistent with the storage allocator (the exact type the slot was
sized/encoded against), so the dispatch's shape test is correct by construction.

### 3.2 `ReadNeoLocalValue` -- the per-shape helper (Neo-only)

A private helper in `DebugService`, mirroring the F-4 indexer branches
(`ILTypeInstance.cs:421-444`):
```
// returns the boxed value for local i, or a placeholder string for IL-VT.
unsafe object ReadNeoLocalValue(byte* frameBase, AutoList mStack, int frameRefBase,
    StackSlotInfo slot, IType localType, AppDomain domain)
{
    if (localType.IsPrimitive)
        return ReadNeoFramePrimitive(frameBase + slot.Offset, localType, domain);
    if (!localType.IsValueType)
    {
        // reference slot: the word at frameBase+Offset is an mStack index.
        int idx = *(int*)(frameBase + slot.Offset);
        return (idx >= 0) ? mStack[frameRefBase + idx] : null;
        // NOTE: idx is relative to THIS frame's ref region only when the slot's
        // RefOffset convention is global; under Neo the word IS the absolute
        // mStack index written by the executor (confirmed: the ref-init at
        // ILIntepreter.Neo.cs:871 writes *(int*)(frameBase+Offset) = -1, and
        // Stfld-style writes store the absolute mStack index). So mStack[idx].
    }
    // value type
    if (localType is ILType)
        return "<IL-value-type local: not supported>";  // SEQUENCE (placeholder)
    // CLR value type (F-MAJ-1 flat bytes) -> ReadNeoValueType boxes it.
    return ILIntepreter.ReadNeoValueType(localType.TypeForCLR,
        frameBase + slot.Offset, ref _cursor, slot.Size);
}
```
`ReadNeoFramePrimitive` is the frame-local analogue of F-4's `ReadNeoPrimitive`
(`ILTypeInstance.cs:547-579`): switch on the `AppDomain` primitive singletons
(`domain.IntType`/`LongType`/...), read the width via
`Unsafe.ReadUnaligned<T>(ref frameBase[off])`. To AVOID duplicating the helper,
the F-4 `ReadNeoPrimitive` is made `internal` (or the frame-local reader calls a
shared overload taking `byte*` instead of `byte[]`). Design decision: a NEW
`byte*`-overload in `DebugService` that mirrors the `byte[]` one line-for-line
(the indexer's helper stays on `byte[]`; two ~25-line switches sharing the SAME
AppDomain-singleton keys -- the duplication is the cost of the
`byte[]`-vs-`byte*` split and is locally obvious).

### 3.3 `GetLocalVariableInfo` Neo arm

Replace the `:268-271` guard:
```
#if ENABLE_NEO_MODE
if (topFrame.IsRegister && m.CompiledFrame.NeoExecuteBody != null)
{
    byte* frameBase = (byte*)topFrame.LocalVarPointer;
    var nf = m.CompiledFrame;
    int frameRefBase = topFrame.ManagedStackBase;
    AutoList mStack = intepreter.Stack.ManagedStack;
    int paramCnt = m.ParameterCount;
    for (int i = 0; i < m.LocalVariableCount; i++)
    {
        try {
            var vd = m.Definition.Body.Variables[i];
            IType lt = ResolveLocalType(m, vd, intepreter.AppDomain);
            var slot = nf.LocalInfos[paramCnt + i];
            var v = ReadNeoLocalValue(frameBase, mStack, frameRefBase, slot, lt, intepreter.AppDomain);
            if (v == null) v = "null";
            string vName = ...; // SAME as Legacy (:282-283)
            sb.AppendFormat("{0} {1} = {2}", lt.FullName/Name, name, v);  // format parity with Legacy
            ... // line-break parity with Legacy (:285-288)
        } catch { }  // keep Legacy's per-iteration resilience (:290-293)
    }
    return sb.ToString();
}
#endif
```
`m.LocalVariableCount` is already set from `LocalInfos.Length` for Neo
(`ILMethod.cs:996`); `paramCnt + i` is the correct local index
(`JITCompiler.cs:1702`). `frameRefBase` from `topFrame.ManagedStackBase`
(`ILIntepreter.Neo.cs:909`).

### 3.4 `GetThisInfo` Neo arm

Replace the `:207-209` guard. Recover the `this` from slot 0 (`ParamInfos[0]`,
a reference) -- mirror Legacy `:218-230` but read the slot-0 `mStack` index:
```
#if ENABLE_NEO_MODE
if (topFrame.IsRegister && topFrame.Method.CompiledFrame.NeoExecuteBody != null)
{
    byte* frameBase = (byte*)topFrame.LocalVarPointer;
    var nf = topFrame.Method.CompiledFrame;
    int frameRefBase = topFrame.ManagedStackBase;
    AutoList mStack = intepreter.Stack.ManagedStack;
    if (!topFrame.Method.HasThis) return "null";
    var thisSlot = nf.ParamInfos[0];
    int idx = *(int*)(frameBase + thisSlot.Offset);
    object thisObj = (idx >= 0) ? mStack[idx] : null;
    ILTypeInstance instance = null;
    if (thisObj is ILTypeInstance ili) instance = ili;
    else if (thisObj is CrossBindingAdaptorType adaptor) instance = adaptor.ILInstance;
    if (instance == null) return "null";
    // REUSE the F-4 indexer for the IL field read (no new field-read code):
    var fields = instance.Type.TypeDefinition.Fields;
    int fieldIdx = 0;
    for (int i = 0; i < fields.Count; i++) {
        try {
            var f = fields[i]; if (f.IsStatic) continue;
            var v = instance[fieldIdx];   // F-4 indexer (ILTypeInstance.cs:421-444)
            if (v == null) v = "null";
            sb.AppendFormat("{0} {1} = {2}", f.FieldType.Name, f.Name, v);
            ... // format parity with Legacy (:249-253)
            fieldIdx++;
        } catch { }  // F-4 indexer throws a tagged NIE for IL-VT fields -> swallowed here
    }
    return sb.ToString();
}
#endif
```
The IL-field read REUSES the F-4 indexer (`ILTypeInstance.cs:410-455`) -- it
already handles primitive / reference / enum / CLR-struct / IL-VT-field (NIE).
`GetThisInfo` needs NO new field-read code; only the slot-0 `this` recovery is
Neo-specific. The F-4 NIE for an IL-VT FIELD is swallowed by the existing
`catch` (`:255-258`) -- a single IL-VT field does not abort the inspection.

### 3.5 The mStack index is ABSOLUTE (confirmed)

The reference-slot word at `frameBase + slot.Offset` is an ABSOLUTE `mStack`
index, NOT relative to `frameRefBase`. Evidence: the ref-init at
`ILIntepreter.Neo.cs:871` writes the sentinel `*(int*)(frameBase + off) = -1`
(absolute: -1 is the Neo null sentinel, not a frame-relative offset), and every
executor write that stores a reference (e.g. the param-marshal
`DelegateAdapter.WriteNeoCallSlot`, the stfld/stind arms) writes the absolute
`mStack` index. So `ReadNeoLocalValue` reads `mStack[idx]` directly -- NOT
`mStack[frameRefBase + idx]`. (`frameRefBase` is used only to SIZE/RESERVE the
frame's ref region; the stored indices are absolute.) This is the same read
pattern as the Step-19 `ReadNeoDelegateInvokeArgs` fallback
(`ILIntepreter.Neo.cs:281-283`: `mStack[idx]`).

## 4. Why SHIP the tractable slice and SEQUENCE IL-VT locals (scope-aware)

- The primitive / reference / CLR-struct shapes are a DIRECT mirror of F-4 +
  ReadNeoValueType against data the frame already carries. The diff is two
  method arms + one helper; NO protocol/ABI work. Their combined regression
  surface is small (Legacy byte-identical; full `NeoStep` smoke is the gate).
- IL-value-type LOCALS (not fields) need RECONSTRUCTION from the split primitive
  + reference sub-regions -- the same machinery F-4 deferred for IL-VT FIELDS.
  Bundling it would conflate two risk profiles and balloon the diff. TRUE-
  COMPLETION is honoured by emitting a clear placeholder string for an IL-VT
  local (so the rest of the inspection stays correct) and sequencing the
  reconstruction as a follow-on child.

## 5. The capstone (how to exercise the debugger inspection)

The natural Neo exercise point is the UNHANDLED-EXCEPTION path: when an IL
method running under `ExecuteNeo` throws an exception no handler catches, the
unwind arm constructs `new ILRuntimeException(ex.Message, this, method, oriESP,
ex)` (`ILIntepreter.Neo.cs:4543`). Its ctor (`ILRuntimeException.cs:31-45`)
calls `DebugService.GetThisInfo(intepreter)` (`:37`) and
`GetLocalVariableInfo(intepreter)` (`:40`), stashing the strings in the
exception's `.ThisInfo` / `.LocalInfo` properties (`:76-87`).

**Host-side self-check** (mirrors `NeoStep25LoadExecCheck` at
`NeoStep25LoadExecCheck.cs:38-62`): a `NeoDebuggerFrameCheck.Run(AppDomain)`
that:
1. Defines a probe IL method (in `TestCases`) with a primitive local, a
   reference (string) local, and (optionally) a CLR-struct local; it ASSIGNS
   known values then throws unhandled.
2. Invokes the probe via `appdomain.Invoke` (which on HEAD routes through the
   Neo parametrized-`Run` shim at `ILIntepreter.cs:111-208` -- F-4 #3 is SHIPPED
   on HEAD, confirmed at `:126`/`:164-181`).
3. Catches the resulting `ILRuntimeException`, asserts `.LocalInfo` CONTAINS the
   correct primitive value AND the correct reference value (not "not supported
   yet"). Asserts `.ThisInfo` carries the instance's fields.
4. ADVERSARIAL: a second probe MUTATES the local before throwing -> asserts
   `.LocalInfo` reflects the MUTATED value (proves the read is live, not stale).

This is the SAME host-side-self-check shape as `NeoStep25LoadExecCheck` (a
static `Run(AppDomain)` returning a Pass/Fail tally, invoked via a CLI special
mode). It is PREFERRED over the CLI debugger-protocol mode because (a) it runs
in the existing `Debug_Neo` CLI without a debugger-frontend handshake, and (b)
it asserts against concrete strings, not a frontend round-trip. The CLI
debugger-protocol mode is an ALTERNATIVE capstone (the VSCode DAP at
`Debugging/VSCode/`) but requires a frontend; out of scope for the self-check.

The check is `#if ENABLE_NEO_MODE && DEBUG` (mirrors `NeoStep25LoadExecCheck`'s
header `:1`) and invoked host-side.

## 6. Verification plan

- SHIP arms: the capstone self-check FAILs-on-HEAD (inspection returns "not
  supported yet" -> the value assertions miss) -> PASSes-after. Stash-toggle:
  restore the `:207-209`/`:268-271` guards -> FAIL; restore the Neo arms -> PASS.
  (Binding evidence the fix is load-bearing, per the LEAD's gate.)
- Full `NeoStep` smoke stays 226/0/0 (no regression; the arms are Neo-only and
  the Legacy paths are byte-identical).
- Legacy-neutral: plain `Debug` build of `ILRuntime`/`ILRuntimeTestCLI` = 0
  errors (all Neo arms under `#if ENABLE_NEO_MODE`).
- IL-VT local: the placeholder string is asserted present (a follow-on replaces
  it with the reconstructed value).

## 7. Out of scope (sequenced)

- **IL-value-type LOCAL reconstruction** (follow-on child): reconstruct an IL-VT
  local's boxed instance from its split primitive + reference sub-regions (the
  frame-local analogue of F-4's IL-VT-FIELD reconstruction). Replace the
  placeholder string with the value. Gate: a probe with a `struct` local throws
  unhandled -> `.LocalInfo` shows the struct's fields.
- **Debugger-protocol capstone** (optional): drive the VSCode DAP
  (`Debugging/VSCode/`) against a Neo frame and inspect locals through the
  frontend. Not required for TRUE-COMPLETION (the host-side self-check is the
  binding gate) but recorded for a future frontend-integration child.
- **AOT/serialized bodies**: under Neo AOT, `registerSymbols` stays null
  (`ILMethod.cs:997-998`, "debugger-on-AOT deferred"). This change is JIT-path
  only (it reads `Definition.Body.Variables`, present on the JIT path). AOT-
  body inspection is a separate follow-on (needs the variable metadata
  serialized into the `.neo`).
