using System;

namespace TestCases
{
    // Step 13b test: CLR value-type unified call ABI.
    //
    // Closes K2 (a CLR struct by-value PARAMETER's flat bytes were miscopied --
    // the caller-temp-slot fallback overrode the callee layout with the source
    // register's shape) and the return-value half of the same deferral.
    //
    // Test design constraints (Step 13 findings):
    //   * HOST-assembly structs only -- a struct declared in TestCases is parsed
    //     as an ILType and exercises the IL value-type path (already green), not
    //     the CLR (CLRType) param/return path this step fixes. We use
    //     TestVector3NoBinding (no binder; pure-primitive -- 3 floats).
    //   * The struct ARGUMENT is obtained from a CLR method RETURN (host C#
    //     constructs it -- no IL `new T(...)` ctor call, which would emit the
    //     unimplemented `push` value-type-`this` opcode, an Area-4 DEFERRED
    //     concern). The struct RESULT is checked by re-feeding it to a CLR
    //     method that returns a PRIMITIVE (no IL-side `ldfld` on CLR struct
    //     fields, also a separate deferred concern).
    //   * All exercised CLR methods are STATIC host helpers in TestCLRBinding,
    //     so the value-type instance `this` direct-call TODO (Area 4) is never
    //     hit (Finding U).
    //
    // These helpers go through the reflection fallback CLRMethod.Invoke(byte*)
    // (no autogen redirect is registered for them), so this exercises D2 (param
    // read) and D6 (return write) directly.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns; a logic failure is surfaced by a deliberate `1/0` (native
    // DivideByZero fault; the Neo VM cannot yet `new Exception(...)`). Tests are
    // `public static void` parameterless.

    public class NeoStep13bTest
    {
        // ---- K2 core: CLR struct by-value PARAMETER (no binder). The struct
        //      local `v` comes from a CLR return (D6); passing it by value to
        //      SumTestVector3NoBindingFields (D2) must deliver the struct's flat
        //      bytes unchanged (host sums x+y+z = 60). Before 13b the caller-
        //      temp-slot fallback miscopied the source register's shape and the
        //      reader walked off the end (K2: ArgumentOutOfRangeException). ----
        public static void NeoStep13bClrStructByValueParamNoBinding()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f);
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            if (r != 60)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ---- K2 (return side), isolated: Make returns a struct; re-feed it and
        //      check the bytes survived the round trip (return write D6 + param
        //      read D2 agree). Different field values to distinguish from above. ----
        public static void NeoStep13bClrStructReturnValueRoundTrip()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(7f, 8f, 9f);
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            if (r != 24)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ---- A struct param whose source register is a DIFFERENT struct local
        //      (verifies the cursor / per-call param region is independent: a
        //      prior call's struct bytes must not bleed into this one). Single
        //      live struct local at a time (avoids unrelated frame-slot-reuse
        //      interactions across many simultaneous locals). ----
        public static void NeoStep13bClrStructParamDistinctValue()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w);
            if (r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
