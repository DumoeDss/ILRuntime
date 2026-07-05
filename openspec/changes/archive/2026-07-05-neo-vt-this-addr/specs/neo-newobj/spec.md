## MODIFIED Requirements

### Requirement: IL value-type newobj

The `ExecuteNeo` `Newobj` arm SHALL construct an IL value type using the
caller's frame byte region (the dest register's slot, sized and aligned for the
value type by `AllocateLocalStackSpaces`) as the construction site. The arm
SHALL NOT allocate a heap `ILTypeInstance` for the value-type case. The
construction steps SHALL be:

1. **Zero-initialize** the dest region (`Unsafe.InitBlock(..., 0,
   TotalPrimitiveSize)`) and null every one of the dest's `TotalReferenceCount`
   ref slots, matching `Initobj` semantics (a ctor may set only some fields).
2. **Seed the ctor's `this`** (callee param slot 0) with a frame-native address
   of the caller's dest region, so the ctor's `this.field =` writes land in the
   caller's dest slot (the `neo-byref` capability owns the seeding mechanism).
3. **Copy the remaining ctor args** into callee param slots [1..] via the
   standard `CopyNeoCallArguments` path.
4. **Invoke the ctor** via `InvokeNeoCallTarget(ctor, isNewobj=true, ...)`. No
   mStack `this` object is pushed (contrast the IL reference-type path); no
   post-ctor copy-back is performed (the frame-native `this` makes the ctor's
   writes land directly in the caller's dest region).

The newobj-dest-as-in-frame-VT contract -- the dest register of a VT `newobj`
SHALL be typed as the constructed VT by the JIT type-specialization pass so the
caller's subsequent field reads/writes lower to `_Inline` -- is owned by the
`neo-value-types` capability.

The arm SHALL continue to throw a loud, tagged `NotImplementedException` for
the **non-goals** (delegate newobj, no-binder CLR-VT-with-reference-fields
newobj, generic-parameter VT newobj) rather than constructing a silently-wrong
object.

#### Scenario: IL value-type newobj with a parameterless ctor

- **WHEN** an IL method emits `new S()` for an IL value type `S` with a
  parameterless ctor (which may be a no-op or set defaults), and reads a field
  of the result
- **THEN** the dest region is zero-initialized, the ctor runs, and the field
  read returns either the zero default or the value the ctor assigned.

#### Scenario: IL value-type newobj with a ctor that sets fields

- **WHEN** an IL method emits `new S(args)` whose ctor sets one or more fields
  via `this.field = ...`, and the caller reads those fields
- **THEN** every field the ctor assigned reads back with the assigned value
  (primitive and reference fields alike), with no heap `ILTypeInstance`
  allocated for the value type and no post-ctor copy-back.

#### Scenario: IL value-type local-form ctor (`VT x = new VT(args)`)

- **WHEN** an IL method executes `S x = new S(args);` (compiles to
  `ldloca x; call S::ctor`) and reads `x.field`
- **THEN** the ctor's `this.field =` writes land in the caller's frame slot for
  `x`, the subsequent `x.field` read returns the assigned value, and no opaque
  `NullReferenceException` is thrown.

#### Scenario: IL value-type newobj result survives intervening operations

- **WHEN** an IL method constructs `new S(args)`, performs unrelated operations
  that reuse eval-stack registers, and only then reads a field of the result
- **THEN** the field read returns the ctor-assigned value (no stale/clobbered
  value from the intervening operations).

#### Scenario: Non-goals remain NIE-tagged

- **WHEN** an IL method emits a delegate `newobj` (`new Action(foo)`), a
  no-binder CLR-VT-with-reference-fields `newobj`, or a generic-parameter VT
  `newobj`
- **THEN** the `Newobj` arm SHALL throw the corresponding tagged
  `NotImplementedException`; it SHALL NOT silently mis-construct.
