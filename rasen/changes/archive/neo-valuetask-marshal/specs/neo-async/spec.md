## ADDED Requirements

### Requirement: Async builder byref-`this` call-arg marshalling skip

The Neo async builder redirections for `SetResult` / `SetException`
(`AsyncTaskMethodBuilder<T>`, `AsyncTaskMethodBuilder`,
`AsyncValueTaskMethodBuilder<T>`, `AsyncValueTaskMethodBuilder`,
`AsyncVoidMethodBuilder`) receive the builder as a value type passed byref
as `this`. The Neo call-arg lowering dereferences that byref and copies the
builder struct's FLAT MANAGED BYTES (`Unsafe.SizeOf<builder>` via
`Optimizer.GetNeoValueTypeManagedSize`) into the callee param region -- it
does NOT marshal the `this` as a single 8-byte byref Ref Slot. Therefore a
redirect that reads subsequent explicit params (the `T result` of `SetResult`,
the `Exception` of `SetException`) SHALL skip the builder's ACTUAL managed
size, computed dynamically from the declaring builder type -- NEVER a
hardcoded 8-byte constant. The managed size differs per builder:
`AsyncTaskMethodBuilder<T>` is 8 bytes (one `Task<T>? m_task` reference
field); `AsyncValueTaskMethodBuilder<T>` is 16 bytes (an `_obj` reference
plus a `_data` long). A hardcoded `+= 8` is correct only for the Task builder
and undershoots the ValueTask builder by 8, causing the next param read to
land on stale struct residue instead of the real value.

#### Scenario: SetResult reads the real T result for a ValueTask<T> builder
- **WHEN** Neo mode runs `AsyncValueTaskMethodBuilder<T>.SetResult(T result)`
  (the builder is a 16-byte value type passed byref as `this`), AND
- **WHEN** the `SetResult` redirect skips the builder `this` to read the
  `T result` param,
- **THEN** the redirect SHALL skip `Unsafe.SizeOf<AsyncValueTaskMethodBuilder<T>>`
  (= 16) flat managed bytes, NOT 8, so `T result` is read at `frameBase[16]`
  (the real result) and not at `frameBase[8]` (stale 2nd qword of the struct).

#### Scenario: SetResult reads the real T result for a Task<T> builder
- **WHEN** Neo mode runs `AsyncTaskMethodBuilder<T>.SetResult(T result)`
  (the builder is an 8-byte value type passed byref as `this`),
- **THEN** the redirect SHALL skip `Unsafe.SizeOf<AsyncTaskMethodBuilder<T>>`
  (= 8) flat managed bytes, so `T result` is read correctly at `frameBase[8]`.

#### Scenario: SetException reads the Exception for any builder
- **WHEN** Neo mode runs a builder `SetException(Exception)` redirect,
- **THEN** the redirect SHALL skip the declaring builder's
  `Unsafe.SizeOf<builder>` flat managed bytes before reading the
  `Exception` param, so the exception is recovered from the correct offset
  regardless of builder kind (Task 8 / ValueTask 16).

#### Scenario: AwaitUnsafeOnCompleted recovers the awaiter without a this-skip
- **WHEN** Neo mode runs a builder `AwaitUnsafeOnCompleted<TA,TSM>` redirect,
- **THEN** the redirect SHALL NOT skip a builder-`this` param region to
  recover the awaited task; it SHALL recover the state machine via the
  `CurrentAsyncSm` thread-static and the awaited task via the SM's awaiter
  field (`GetAwaitedTaskFromSm`), independent of the builder-`this` layout.
