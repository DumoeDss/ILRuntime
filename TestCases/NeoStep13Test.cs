using System;
using System.Collections.Generic;

namespace TestCases
{
    // Step 13 test value types and methods: Box / Unbox / Initobj completeness,
    // and constrained. callvirt specialization on a value-type this.
    //
    // Assertion mechanism: an intentional integer divide-by-zero (same pattern
    // as NeoStep12Test / NeoStep12bTest). The Neo VM does not yet support
    // `new Exception(...)` (CLR newobj is Step 9), so a throw-based assertion
    // cannot be constructed inside interpreted code; DivideByZero is a native
    // fault that surfaces the failure without an allocation. A passing test
    // simply returns without dividing by zero.

    // ---- IL value types for Area 1 ----

    // IL value type with one primitive + one reference field.
    public struct NeoStep13IlVtOneRef
    {
        public int a;
        public string b;
    }

    // IL value type with multiple reference fields.
    public struct NeoStep13IlVtManyRefs
    {
        public string p;
        public int n;
        public string q;
    }

    // IL enum for box/unbox.
    public enum NeoStep13IlEnum
    {
        A = 3,
        B = 7
    }

    // ---- CLR value types for Area 2 (pure-primitive, no binder) ----

    public struct NeoStep13ClrVec3
    {
        public float x;
        public float y;
        public float z;
    }

    public struct NeoStep13ClrPoint
    {
        public int X;
        public int Y;
    }

    public class NeoStep13Test
    {
        // ===================== Area 1: IL value-type Box/Unbox =====================

        // IL VT with one ref: box to object then unbox back; the primitive and
        // the reference field round-trip, and the boxed ref field shares identity
        // with the source (shallow copy).
        public static void NeoTestIlVtOneRefBoxUnbox()
        {
            NeoStep13IlVtOneRef s = default(NeoStep13IlVtOneRef);
            s.a = 11;
            s.b = "hi";
            object o = s;
            NeoStep13IlVtOneRef t = (NeoStep13IlVtOneRef)o;
            if (t.a != 11)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (t.b != "hi")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Mutate source after box; boxed value must be a separate copy.
            s.a = -99;
            NeoStep13IlVtOneRef t2 = (NeoStep13IlVtOneRef)o;
            if (t2.a != 11)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // IL VT with multiple ref fields: all reference fields survive the
        // box/unbox round-trip (refCount > 1 exercises the per-ref-slot copy).
        public static void NeoTestIlVtManyRefsBoxUnbox()
        {
            NeoStep13IlVtManyRefs s = default(NeoStep13IlVtManyRefs);
            s.p = "p1";
            s.n = 5;
            s.q = "q1";
            object o = s;
            NeoStep13IlVtManyRefs t = (NeoStep13IlVtManyRefs)o;
            if (t.p != "p1")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (t.n != 5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (t.q != "q1")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // IL enum box/unbox round-trip preserves the underlying value.
        public static void NeoTestIlEnumBoxUnbox()
        {
            NeoStep13IlEnum e = NeoStep13IlEnum.B;
            object o = e;
            NeoStep13IlEnum back = (NeoStep13IlEnum)o;
            if (back != NeoStep13IlEnum.B)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Re-box after mutating the source; the box is an independent copy.
            e = NeoStep13IlEnum.A;
            NeoStep13IlEnum back2 = (NeoStep13IlEnum)o;
            if (back2 != NeoStep13IlEnum.B)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // IL primitive (int) box/unbox round-trip. (int resolves to the CLR
        // System.Int32 type, so this exercises the CLR no-binder Box/Unbox path.)
        public static void NeoTestIlPrimitiveBoxUnbox()
        {
            int v = 42;
            object o = v;
            int back = (int)o;
            if (back != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Mutate source; the box is an independent copy.
            v = -1;
            int back2 = (int)o;
            if (back2 != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ===================== Area 2: CLR value-type Box/Unbox (no binder) ======

        // CLR pure-primitive struct (3 floats) box then unbox round-trip. The
        // boxed value is a snapshot taken at box time.
        public static void NeoTestClrVec3BoxUnbox()
        {
            NeoStep13ClrVec3 v = default(NeoStep13ClrVec3);
            v.x = 1f;
            v.y = 2f;
            v.z = 3f;
            object o = v;
            // Mutate source after boxing; the box must hold the original values.
            v.z = 999f;
            NeoStep13ClrVec3 w = (NeoStep13ClrVec3)o;
            float sum = w.x + w.y + w.z;
            if (sum != 6f)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // CLR pure-primitive struct (2 ints) box/unbox round-trip.
        public static void NeoTestClrPointBoxUnbox()
        {
            NeoStep13ClrPoint p = default(NeoStep13ClrPoint);
            p.X = 7;
            p.Y = 9;
            object o = p;
            NeoStep13ClrPoint q = (NeoStep13ClrPoint)o;
            if (q.X != 7 || q.Y != 9)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Probe: a REAL CLR value type (host assembly struct, no binder) local.
        // Tests Initobj + Box + Unbox mechanics without relying on CLR struct
        // field reads (Ldfld on CLR struct fields is a separate unimplemented
        // concern). Verifies the box is a non-null object of the right type and
        // that unbox produces a distinct boxed instance.
        public static void NeoTestClrStructNoBindingBoxRoundTrip()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                default(ILRuntimeTest.TestFramework.TestVector3NoBinding);
            object o = v;
            // Box of a default CLR struct yields a non-null boxed object.
            if (o == null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Unbox back into a local; the result is a non-null box.
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                (ILRuntimeTest.TestFramework.TestVector3NoBinding)o;
            object o2 = w;
            if (o2 == null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Box produces an independent copy (snapshot at box time): after
            // re-boxing the local, the two boxes are distinct object instances
            // even though their values are equal.
            if (object.ReferenceEquals(o, o2))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // CLR struct WITH a registered ValueTypeBinder (TestVector3). Verifies
        // the WITH-binder box/unbox path produces a non-null independent copy.
        public static void NeoTestClrStructWithBinderBoxRoundTrip()
        {
            ILRuntimeTest.TestFramework.TestVector3 v =
                default(ILRuntimeTest.TestFramework.TestVector3);
            object o = v;
            if (o == null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            ILRuntimeTest.TestFramework.TestVector3 w =
                (ILRuntimeTest.TestFramework.TestVector3)o;
            object o2 = w;
            if (o2 == null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (object.ReferenceEquals(o, o2))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ===================== Area 2: CLR enum Box/Unbox (DEFERRED) ===========
        // A CLR-enum / CLR-struct LOCAL round-trip test (e.g. boxing/unboxing a
        // System.DayOfWeek local) is DEFERRED. It revealed a pre-existing Move
        // path bug: assigning a scalar/constant to a boxed-ref CLR-VT local
        // (CLR enum or CLR struct) reads the int as an mStack index instead of
        // storing it as the boxed-ref payload -- the Step 12b "K2 family" bug.
        // That is NOT Step 13 scope; it is Step 13b (area 5) scope and will be
        // fixed by Step 13b's unified param layout + Move-path work.
        //
        // The MINOR-1 fix (dropping the IsEnum special-case so CLR enums flow
        // through the boxed-ref PerformMemberwiseClone / CreateDefaultInstance
        // path in Initobj/Box/Unbox) is CORRECT per the boxed-ref CLR-VT-local
        // representation, and zero-regression on the 49 prior green tests. It
        // will be testable end-to-end once the K2 Move-path bug is fixed.

        // ===================== Area 3: constrained. callvirt on a VT ==========
        // NOTE: Area 3 (constrained. value-type specialization) is DEFERRED.
        // The end-to-end constrained callvirt on a value type requires loading
        // the address of a value-type `this` (`ldarga.s` for a generic-method
        // parameter, or `ldloca` for a local), and `ldarga`/`ldarga_s` are NOT
        // yet implemented in ExecuteNeo (Step 6 NotImplementedException). The
        // constrained specialization's box-once / direct-call lowering needs
        // that address model, which is owned by Step 17 (byref/ref/out). So no
        // Area 3 test is wired here; see planning-context.md section 8 and
        // tasks.md Phase 3 for the deferral rationale. The existing callvirt /
        // dispatch paths (NeoStep10/11) are unaffected (no green test exercises
        // constrained. today).
    }
}
