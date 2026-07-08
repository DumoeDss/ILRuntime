using System;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.Intepreter;

namespace ILRuntimeTest.TestFramework
{
    // F-4 / NEO-IL-EX-FIELDACCESS path #4 probe vehicle.
    //
    // The ILTypeInstance Neo field indexer (this[int].get) is the standard CLR-
    // side bridge for reading an IL-declared field off a recovered ILTypeInstance.
    // A direct cast to the IL type is impossible (the recovered object is an
    // ILTypeInstance, unrelated CLR type to the IL class), so field read-off a
    // recovered instance goes through the indexer -- this is the path generated
    // cross-binding-adaptor property forwarders and host reflection use.
    //
    // This helper is invoked from the interpreted IL probe
    // (NeoStep14_ILEx_IndexerFieldRead); it performs the bridge recovery + the
    // indexer read in PURE CLR (the adaptor-forwarder shape), so the probe
    // exercises exactly the indexer (path #4) without depending on IL-level
    // dispatch for the ILType/ILTypeInstance host APIs.
    public static class NeoF4ReflectionProbe
    {
        // Read the named IL-declared string field off a recovered ILTypeInstance
        // via the Neo indexer, and compare it against `expected` IN PURE CLR.
        // Returns: 1 = match, 0 = mismatch (non-null), -1 = null value, -2 = field
        // absent, -3 = null ili. The comparison is done here (not in IL) to avoid
        // the IL `string == string` path (System_String_Binding.op_Equality_19_Neo),
        // which has a SEPARATE pre-existing ReadNeoReference null-operand gap that
        // would confound the indexer-under-test with an unrelated failure surface.
        public static int ReadFieldStringMatch(ILTypeInstance ili, string fieldName, string expected)
        {
            if (ili == null)
                return -3;
            int idx = -1;
            ili.Type.GetField(fieldName, out idx);
            if (idx < 0)
                return -2;
            object val = ili[idx];
            if (val == null)
                return -1;
            string s = val as string;
            if (s == null)
                return 0;
            return string.Equals(s, expected, System.StringComparison.Ordinal) ? 1 : 0;
        }
    }
}
