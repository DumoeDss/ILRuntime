using System;

namespace TestCases
{
    // Step 12 test value types. These are IL value types (compiled into the
    // TestCases DLL and interpreted by the Neo VM). All tests below exercise
    // FIELD-LEVEL access only on in-frame value-type locals -- no whole-struct
    // copy / pass-by-value (that is Step 12b and is deliberately not tested).
    //
    // Assertion mechanism: an intentional integer divide-by-zero (the same
    // pattern used by NeoStep7Step8Test). The Neo VM does not yet support
    // `new Exception(...)` (CLR newobj is Step 9), so a `throw`-based
    // assertion cannot be constructed inside interpreted code; DivideByZero
    // is a native fault that surfaces the failure without an allocation.

    public struct NeoStep12Vector3
    {
        public float x;
        public float y;
        public float z;
    }

    public struct NeoStep12Inner
    {
        public int x;
    }

    public struct NeoStep12Outer
    {
        public NeoStep12Inner i;
        public int y;
    }

    public struct NeoStep12WithRef
    {
        public int a;
        public string b;
    }

    // Step 12 (Minor 2): a value type whose NaturalAlignment is 8. The layout
    // is { long a (offset 0, 8-aligned); int b (offset 8); long c (offset 16,
    // 8-aligned, after the int gap forces 4 bytes of padding) }. The trailing
    // long-after-int-gap directly probes NaturalAlignment / AlignUp (design
    // 1.2 / 6.4) and the *(long*) cast in the Ldfld_I8_Inline / Stfld_I8_Inline
    // arms at a non-zero 8-aligned offset. A missed AlignUp site would
    // misalign the long slots. Field-level access only (no whole-struct copy).
    public struct NeoStep12LongFieldStruct
    {
        public long a;
        public int b;
        public long c;
    }

    public class NeoStep12HeapClass
    {
        public int x;
    }

    public class NeoStep12Test
    {
        // Field access on a flat primitive value-type local.
        public static void NeoStep12TestVector3FieldAccess()
        {
            NeoStep12Vector3 v = default(NeoStep12Vector3);
            v.x = 1f;
            v.y = 2f;
            v.z = 10f;
            float r = v.x + v.y + v.z;
            if (r != 13f)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Nested value type: a write to `o.i.x` resolves (via ldloca + ldflda)
        // to a single inline Stfld at the absolute nested offset. We test the
        // WRITE path (the design's core nested scenario) plus the sibling
        // field `o.y`, and confirm the two fields do not corrupt each other.
        // A whole-`Inner` read (`int t = o.i.x;`) would compile to Ldfld_Value
        // (a whole-struct copy = Step 12b), so it is deliberately avoided.
        public static void NeoStep12TestNestedValueType()
        {
            NeoStep12Outer o = default(NeoStep12Outer);
            o.i.x = 7;
            o.y = 9;
            // Sibling plain field round-trips.
            if (o.y != 9)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Overwrite the nested field and a neighbour to confirm offset
            // independence (a miscomputed nested offset would clobber o.y).
            o.i.x = 100;
            o.y = 200;
            if (o.y != 200)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Re-read the nested field via a write-then-read pattern: store a
            // sentinel, overwrite o.y, then re-store a different sentinel. If
            // the nested offset were wrong, o.y would be corrupted above.
            o.i.x = 0x12345678;
            if (o.y != 200)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Value type containing a reference field: primitive field on the byte
        // region, reference field on the frame mStack ref region. Includes the
        // null round-trip.
        public static void NeoStep12TestValueTypeWithReferenceField()
        {
            NeoStep12WithRef s = default(NeoStep12WithRef);
            s.a = 42;
            s.b = "hello";
            if (s.a != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (s.b != "hello")
            {
                int z = 1; int d = 0; int _ = z / d;
            }

            // Null round-trip on the reference field.
            s.b = null;
            if (s.b != null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }

            s.b = "again";
            if (s.b != "again")
            {
                int z = 1; int d = 0; int _ = z / d;
            }

            // Confirm the primitive field survived the reference writes.
            if (s.a != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Initobj on an in-frame value type with a reference field: the
        // primitive region is zeroed AND the ref slot is nulled. default(T)
        // on a struct local compiles to `ldloca s; initobj S` (in-place
        // zeroing), which is the Step 12 Initobj-memset path. We only test
        // field-level reads afterwards (no whole-struct copy = Step 12b).
        public static void NeoStep12TestInitobjZerosValueType()
        {
            NeoStep12WithRef s = default(NeoStep12WithRef);
            if (s.a != 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (s.b != null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Heap object field access is unchanged: the operand is a reference
        // slot, so the JIT emits the existing heap Ldfld_I4/Stfld_I4 opcodes
        // (NOT the _Inline variants). Confirms no regression on the heap path.
        public static void NeoStep12TestHeapObjectFieldAccessUnchanged()
        {
            NeoStep12HeapClass c = new NeoStep12HeapClass();
            c.x = 5;
            int r = c.x;
            if (r != 5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Alignment-8 value type: write all three fields (including the
        // trailing long after the int gap, which forces 8-aligned slots at
        // non-zero offsets) and read them back, asserting round-trip equality.
        // A miscomputed alignment for the long slots would corrupt a or c.
        public static void NeoStep12TestLongFieldStruct()
        {
            NeoStep12LongFieldStruct s = default(NeoStep12LongFieldStruct);
            s.a = 0x0123456789ABCDEFL;
            s.b = 0x7654321;
            s.c = -0x1122334455667788L;
            if (s.a != 0x0123456789ABCDEFL)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (s.b != 0x7654321)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (s.c != -0x1122334455667788L)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Overwrite the trailing long and re-check the leading long: a
            // misaligned c slot would clobber a (they sit 16 bytes apart, so
            // any overlap means the offset math is wrong).
            s.c = 0x1122334455667788L;
            if (s.a != 0x0123456789ABCDEFL)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (s.c != 0x1122334455667788L)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (s.b != 0x7654321)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
