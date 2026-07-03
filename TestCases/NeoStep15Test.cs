using System;

namespace TestCases
{
    // Step 15 test: Neo runtime type-check instructions isinst (C# `is` / `as`)
    // and castclass (C# explicit `(T)obj` cast). isinst keeps the reference on a
    // successful assignability check and writes null on mismatch (never throws);
    // castclass throws InvalidCastException on mismatch and passes null through.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without dividing by zero. A logic failure is surfaced by a
    // deliberate `1/0` (native DivideByZero fault; the Neo VM cannot yet
    // `new Exception(...)`). Tests are `public static void` parameterless.

    // ---- IL types for type checks ----

    public class NeoStep15Base
    {
        public int bval;
    }

    public class NeoStep15Derived : NeoStep15Base
    {
        public int dval;
    }

    public class NeoStep15Unrelated
    {
    }

    public interface INeoStep15Foo
    {
        int F();
    }

    public class NeoStep15Impl : INeoStep15Foo
    {
        public int F() { return 7; }
    }

    public class NeoStep15NoImpl
    {
    }

    public struct NeoStep15Vt
    {
        public int x;
    }

    public class NeoStep15Test
    {
        // TC1 `is` true on inheritance chain: a Base local holding a Derived
        // instance checks `is Derived` -> true.
        public static void NeoStep15_TC1_IsTrueOnDerived()
        {
            NeoStep15Base o = new NeoStep15Derived();
            if (!(o is NeoStep15Derived))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 `is` false on unrelated type: the isinst result is null, so the
        // emitted null-check yields false.
        public static void NeoStep15_TC2_IsFalseOnUnrelated()
        {
            NeoStep15Base o = new NeoStep15Derived();
            if (o is NeoStep15Unrelated)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 `as Interface`: non-null when implemented, null when not. Both
        // paths exercised.
        public static void NeoStep15_TC3_AsInterface()
        {
            object impl = new NeoStep15Impl();
            object noimpl = new NeoStep15NoImpl();
            INeoStep15Foo a = impl as INeoStep15Foo;
            if (a == null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            INeoStep15Foo b = noimpl as INeoStep15Foo;
            if (b != null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC4 boxed value type: `boxed is V` true; `boxed is Unrelated` false.
        public static void NeoStep15_TC4_BoxedValueTypeIs()
        {
            NeoStep15Vt v = new NeoStep15Vt { x = 5 };
            object boxed = v;
            if (!(boxed is NeoStep15Vt))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (boxed is NeoStep15Unrelated)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC5 castclass success round-trip: cast Base->Derived, read a Derived
        // field -> expected value.
        public static void NeoStep15_TC5_CastclassSuccess()
        {
            NeoStep15Derived d = new NeoStep15Derived();
            d.dval = 42;
            NeoStep15Base hb = d;
            NeoStep15Derived cd = (NeoStep15Derived)hb;
            if (cd.dval != 42)
            {
                int z = 1; int d2 = 0; int _ = z / d2;
            }
        }

        // TC6 castclass failure is caught: an illegal cast throws
        // InvalidCastException which the catch swallows => method returns
        // normally => PASS. Exercises the castclass throw path green.
        public static void NeoStep15_TC6_CastclassFailureCaught()
        {
            NeoStep15Base o = new NeoStep15Derived();
            try
            {
                NeoStep15Unrelated u = (NeoStep15Unrelated)(object)o;
                int z = 1; int d = 0; int _ = z / d; // unreachable on success
            }
            catch (InvalidCastException)
            {
                // expected: castclass threw.
            }
        }

        // TC7 isinst inside a catch body (indirect Step 14 benefit): throw via
        // `1/0`, then `e is DivideByZeroException` inside the catch.
        public static void NeoStep15_TC7_IsInCatchBody()
        {
            try
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            catch (DivideByZeroException e)
            {
                if (!(e is DivideByZeroException))
                {
                    int z = 1; int d = 0; int _ = z / d;
                }
            }
        }
    }
}
