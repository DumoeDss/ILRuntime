using System;

namespace TestCases
{
    // Gap B probe (generic reference-type param passing into a call).
    // A generic param T instantiated with a ref type (string/object) that is
    // passed INTO a call arrives null/wrong in Neo. The echo (return the param)
    // works; the call-arm copy misreads the param. These probes isolate each
    // facet. Assertion = divide-by-zero on a wrong result (the Neo VM convention).
    public class NeoStepGapBProbe
    {
        // ---- host helpers (concrete signatures, no generics) ----
        static string EchoStringConcrete(string s) { return s; }
        static int LenOf(string s) { return s == null ? -1 : s.Length; }
        static int HashOf(object o) { return o == null ? -7 : o.GetHashCode(); }
        static string AsString(object o) { return o == null ? null : o.ToString(); }

        // ---- generic wrappers ----
        // PASSES on HEAD: pure echo, no call on the param.
        static T EchoT<T>(T x) { return x; }
        // The clean Gap-B isolation: pass a generic ref param T to a helper that
        // takes `object` (no cast/box/constrained). On HEAD this NREs / returns
        // the wrong (null) object.
        static int CallObjHelper<T>(T x) { return HashOf(x); }
        // Pass a generic ref param T to a helper that takes `string` via a cast.
        static int CallStringHelper<T>(T x) { return LenOf((string)(object)x); }
        // NOTE: passing T to another generic method that then does `x.GetHashCode()`
        // compiles to `constrained !!T; callvirt Object::GetHashCode`, which hits
        // Gap A (the Step-17 constrained-arm ref-type `this`), NOT Gap B. That facet
        // is exercised by the neo-constrained-reftype change's wrappers; see
        // NeoStep17Test.NeoStep17_ConstrainedRefTypeString (currently at HEAD = NIE
        // until Gap A lands). A plain T->T pass-through that does NOT dereference T
        // (CallObjHelper above, via `box !!T`) is the Gap-B facet and passes.

        // ---- the public test entry points (run by the CLI) ----
        public static void NeoStepGapB_EchoString()
        {
            string r = EchoT<string>("abc");
            if (r != "abc") { int z = 1; int d = 0; int _ = z / d; }
        }

        public static void NeoStepGapB_ObjCall()
        {
            int h = CallObjHelper<string>("hello");
            // "hello" must be non-null when it reaches HashOf.
            if (h == -7) { int z = 1; int d = 0; int _ = z / d; }
        }

        public static void NeoStepGapB_StringCall()
        {
            int len = CallStringHelper<string>("hello");
            if (len != 5) { int z = 1; int d = 0; int _ = z / d; }
        }

        // Gap-A-scoped facet (constrained ref-type this), NOT Gap B. Left disabled
        // here; covered by neo-constrained-reftype once Gap A lands.
        // public static void NeoStepGapB_TTCall()
        // {
        //     int h = CallTTHelper<string>("world");
        //     if (h == -9) { int z = 1; int d = 0; int _ = z / d; }
        // }

        // A non-generic ref-param control (must always pass; isolates that the
        // non-generic path is unaffected by any Gap B fix).
        public static void NeoStepGapB_NonGenericObjCall()
        {
            int h = HashOf("control");
            if (h == -7) { int z = 1; int d = 0; int _ = z / d; }
        }

        // A value-type generic-param control (must always pass; the Gap B fix
        // must not regress Echo<int>-style value-type generics).
        static T EchoTInt<T>(T x) { return x; }
        public static void NeoStepGapB_EchoInt()
        {
            int r = EchoTInt<int>(42);
            if (r != 42) { int z = 1; int d = 0; int _ = z / d; }
        }
    }
}
