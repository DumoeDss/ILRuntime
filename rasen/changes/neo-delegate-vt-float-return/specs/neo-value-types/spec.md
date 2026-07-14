# neo-value-types spec delta -- neo-delegate-vt-float-return

## ADDED Requirement: Raw-Ldfld on a boxed-reference CLR value-type parameter

The Neo calling convention lays out a CLR value-type PARAMETER as a boxed
reference (`AllocateSlotInfo`: `Size=4`/`RefCount=1`, an mStack index of the
boxed struct), NOT as flat managed bytes (the layout used for a CLR value-type
LOCAL). A raw `Ldfld` of a field on such a parameter -- the canonical shape of a
CLR->IL delegate callback `v => v.field` lambda, whose CIL is
`ldarg <param>; ldfld <field>` -- MUST dereference the boxed struct and
reflection-read the field, NOT read the owner slot's bytes as the struct.

The runtime raw-`Ldfld` CLR-value-type-owner arm SHALL recognize this shape and
read the field via `FieldInfo.GetValue(mStack[ownerSlotIndex])`. Because the Neo
frame is untyped, the shape SHALL be marked at JIT time: the JIT SHALL stamp a
dedicated `Operand4` marker bit on the raw-`Ldfld` instruction when its CIL
predecessor is an `ldarg` (the parameter load). The marker bit SHALL be disjoint
from the existing raw-Ldfld byref markers (array-element, CLR-object-field).

A CLR value-type parameter slot MAY also hold flat managed bytes after an
in-method `starg` that reassigns the parameter from a flat-bytes source (e.g.
`arg = SomeStruct.StaticField; arg.field`). The runtime arm SHALL therefore
distinguish the two representations at runtime within the marker branch: if the
slot's first int is a valid mStack index of a boxed struct of the declaring type,
dereference; otherwise fall back to the flat-bytes struct read. The type check
SHALL be an exact-declaring-type (`Type.IsInstanceOfType`) guard so a flat-bytes
struct whose first field coincidentally indexes an unrelated object is not
misread.

The existing destination marshalling (primitive -> typed frame write, value type
-> `WriteNeoValueType`, reference -> mStack add) SHALL handle the field value
unchanged. The plain flat-bytes raw-`Ldfld` path (CLR value-type LOCAL owner)
SHALL remain unchanged.

#### Scenario: Delegate lambda reading a CLR-struct parameter field

A `Func<TestVector3,float>` lambda `v => v.X`, invoked by a CLR host (e.g.
`Enumerable.Sum`), SHALL return the parameter's `X` field value, not a
bit-reinterpret of the boxed-reference mStack index. `list.Sum(v => v.X)` over a
list whose elements' `X` values sum to 6 SHALL return 6 (not `4E-45`).

#### Scenario: starg-reassigned parameter still reads flat bytes

A method that reassigns a CLR-struct parameter from a flat-bytes source
(`arg = TestVector3.One2; arg.X`) SHALL read the reassigned value's field via
the flat-bytes fallback (the marker branch's type check rejects the flat-bytes
slot and falls through). This SHALL NOT regress vs the pre-fix flat-bytes path.

#### Scenario: CLR-struct local field read is unchanged

A CLR value-type LOCAL (`TestVector3 a = ...; a.X`) whose `ldfld` CIL predecessor
is NOT an `ldarg` SHALL continue to use the flat-bytes raw-`Ldfld` path
unchanged (no marker stamped; behavior byte-identical to HEAD).
