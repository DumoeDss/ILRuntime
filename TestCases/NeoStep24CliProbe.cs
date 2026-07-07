using System;

namespace TestCases
{
    // ===== Step 24: the ilrt_neoc CLI probe (V1-A host-side roundtrip input) =====
    //
    // A SMALL dedicated probe type for the Step-24 V1 CLI-roundtrip self-check
    // (ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep24CliRoundtripCheck.cs). The
    // self-check compiles this probe via the SAME public NeoCompiler driver the
    // ilrt_neoc CLI uses (the explicit-types overload, scoped to this type set to
    // keep the compile sub-second), serializes to a MemoryStream, reads it back
    // via NeoAssemblyReader, and asserts the roundtrip EQUALS the in-memory compile
    // (reusing Step 23's comparators) + asserts the generic/non-generic split.
    //
    // The methods are deliberately simple (model after the Step-23 probes that
    // compile cleanly under Neo): non-generic arithmetic, a generic method
    // (template-eligible), a try/catch (EH table), a struct local (frame VT), plus
    // an instance ctor + a static ctor. The nested type exercises multi-type +
    // nested-type enumeration in the driver. V2 functional is Step 25.

    public struct NeoStep24CliStruct
    {
        public int A;
        public long B;
    }

    public class NeoStep24CliProbe
    {
        public long InstanceField;

        static NeoStep24CliProbe()
        {
            // static ctor -> the StaticCtorMethodRefIdx + InitializerTable path.
        }

        public NeoStep24CliProbe(int seed)
        {
            InstanceField = seed;
        }

        // (a) non-generic: primitive locals + arithmetic.
        public static int ProbeBasic(int a, int b)
        {
            int x = a + 1;
            int y = b + 2;
            int z = x * y;
            return z + 7;
        }

        // (b) generic method WITH a template (Box + constrained. callvirt ->
        //     patch sites; template-eligible).
        public static T GenericProbe<T>(T v, int n)
        {
            T current = v;
            object boxed = v;             // Box T -> TypeToken patch
            int h = v.GetHashCode();      // constrained. T callvirt -> Constrained patch
            return current;
        }

        // (c) exception handler (EH table -> NeoExceptionHandlerRecord).
        public static int TryCatchProbe(int a)
        {
            int result = 0;
            try
            {
                if (a < 0) throw new Exception("neg");
                result = a * 2;
            }
            catch (Exception ex)
            {
                result = ex.Message.Length;
            }
            return result;
        }

        // (d) struct local (in-frame VT) + diverse locals.
        public static long MixedLocals(int n)
        {
            int prim = n + 1;
            NeoStep24CliStruct vt = default(NeoStep24CliStruct);
            vt.A = n;
            vt.B = n * 2L;
            return prim + vt.A + vt.B;
        }

        // A nested type: exercises multi-type + nested-type enumeration in the
        // driver (the module filter / explicit-types path must catch it).
        public class NestedProbe
        {
            public int NestedField;

            public int NestedMethod(int x)
            {
                int y = x + 10;
                return y * 2;
            }

            public U GenericNested<U>(U v)
            {
                U cur = v;
                int h = v.GetHashCode();   // constrained. U callvirt -> Constrained patch
                return cur;
            }
        }
    }
}
