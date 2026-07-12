using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-il-enum-getenumvalues probes: System.Enum.GetValues / GetNames /
    // GetUnderlyingType on an IL-defined enum surfaced as System.Type.
    //
    // Pre-fix: ILRuntimeType (the IL-enum System.Type wrapper) overrode NONE of
    // IsEnum / GetEnumUnderlyingType / GetEnumValues / GetEnumNames. The autogen
    // System_Enum_Binding.GetValues_0_Neo / GetNames_1_Neo redirects delegate to the
    // framework System.Enum.GetValues/GetNames, which call the Type.GetEnumValues() /
    // GetEnumNames() / GetEnumUnderlyingType() virtuals; with no override they fall
    // through to the base System.Type impl that throws a bare NotImplementedException
    // ("The method or operation is not implemented."). Enum.GetUnderlyingType is not
    // redirected at all -- the IL call goes straight to the framework method, which
    // calls the same virtual. Of the three, only GetEnumValues() actually faults on HEAD
    // in .NET 8 (its base has no default); GetEnumNames/GetEnumUnderlyingType already
    // worked via ILRuntimeType's existing GetFields override, so TC2/TC3 are defensive.
    //
    // Post-fix: the four overrides on ILRuntimeType answer from Cecil metadata -- the
    // enum's IsLiteral/HasConstant fields are the member set, and FieldDefinition.Constant
    // is each member's boxed underlying value (the same accessor
    // ILRuntimeFieldInfo.GetRawConstantValue uses).
    //
    // Assertion mechanism: per the child-1/child-2 FAULT-to-fail discipline. A wrong
    // value is surfaced by a deliberate 1/0 (DivideByZero; the Neo VM cannot yet new
    // Exception(...)). Each probe MUST fault on the current code (the bare NIE) and
    // pass after the fix. Tests are public static void parameterless.

    // The IL-defined enum under test. Ascending values so declaration order coincides
    // with value order (the framework's GetValues/GetNames return members ordered by
    // underlying value); int-backed (the C# default).
    public enum NeoStepIlEnumProbe
    {
        A = 0,
        B = 10,
        C = 20,
    }

    // A NEGATIVE-member enum: declaration order {N=-1, Z=0, P=1} does NOT coincide with
    // the framework's UNSIGNED-binary-value sort (N's -1 == 0xFFFFFFFF... sorts LAST).
    // GetValues/GetNames must return [Z, P, N] (values 0, 1, -1) -- NOT the signed/decl
    // order [N, Z, P]. Covers the signed-vs-unsigned compare (TC1's 0/10/20 cannot).
    public enum NeoStepIlEnumProbeSigned
    {
        N = -1,
        Z = 0,
        P = 1,
    }

    public class NeoStepIlEnumGetValuesTest
    {
        // TC1 Enum.GetValues: returns an Array of the underlying type with the enum's
        // constant values in value order. Asserts Length == 3, the array is int[],
        // and values 0/10/20 at indices 0/1/2. Faults on HEAD (the bare NIE from
        // System.Type.GetEnumValues()); a wrong value -> 1/0.
        public static void NeoStepIlEnumGetValues_TC1()
        {
            Array values = Enum.GetValues(typeof(NeoStepIlEnumProbe));
            if (values == null || values.Length != 3)
            { int z = 1; int d = 0; int _ = z / d; }
            if (!(values is int[]))
            { int z = 1; int d = 0; int _ = z / d; }
            if ((int)values.GetValue(0) != 0)
            { int z = 1; int d = 0; int _ = z / d; }
            if ((int)values.GetValue(1) != 10)
            { int z = 1; int d = 0; int _ = z / d; }
            if ((int)values.GetValue(2) != 20)
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // TC2 Enum.GetNames: returns the enum's member names in value order. Asserts
        // Length == 3 and names A/B/C at indices 0/1/2. Faults on HEAD (the bare NIE
        // from System.Type.GetEnumNames()); a wrong name -> 1/0.
        public static void NeoStepIlEnumGetValues_TC2()
        {
            string[] names = Enum.GetNames(typeof(NeoStepIlEnumProbe));
            if (names == null || names.Length != 3)
            { int z = 1; int d = 0; int _ = z / d; }
            if (names[0] != "A")
            { int z = 1; int d = 0; int _ = z / d; }
            if (names[1] != "B")
            { int z = 1; int d = 0; int _ = z / d; }
            if (names[2] != "C")
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // TC3 Enum.GetUnderlyingType: returns the enum's underlying primitive. Asserts
        // the result is System.Int32 (compared via FullName, a string/value comparison,
        // which is robust to however the unredirected CLR-call path marshals the Type
        // return). Faults on HEAD (the bare NIE from System.Type.GetEnumUnderlyingType());
        // a wrong/null type -> 1/0.
        public static void NeoStepIlEnumGetValues_TC3()
        {
            Type ut = Enum.GetUnderlyingType(typeof(NeoStepIlEnumProbe));
            if (ut == null || ut.FullName != "System.Int32")
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // TC4 Enum.GetValues on a NEGATIVE-member enum: the framework sorts by UNSIGNED
        // binary value, so {N=-1, Z=0, P=1} -> [Z(0), P(1), N(-1)] (values 0, 1, -1), NOT
        // the signed/declaration order [N(-1), Z(0), P(1)]. A signed compare would put N
        // first; this probe faults if the sort is signed. (TC1's 0/10/20 cannot catch it.)
        public static void NeoStepIlEnumGetValues_TC4()
        {
            Array values = Enum.GetValues(typeof(NeoStepIlEnumProbeSigned));
            if (values == null || values.Length != 3)
            { int z = 1; int d = 0; int _ = z / d; }
            if ((int)values.GetValue(0) != 0)   // Z
            { int z = 1; int d = 0; int _ = z / d; }
            if ((int)values.GetValue(1) != 1)   // P
            { int z = 1; int d = 0; int _ = z / d; }
            if ((int)values.GetValue(2) != -1)  // N (unsigned sort: -1 == 0xFFFF.. last)
            { int z = 1; int d = 0; int _ = z / d; }
        }
    }
}
