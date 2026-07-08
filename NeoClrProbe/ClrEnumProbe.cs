namespace NeoClrProbe
{
    /// <summary>
    /// A tiny IL assembly with a pair of methods that reference the host CLR enum
    /// <c>ILRuntimeTest.TestFramework.TestCLREnum</c> (defined in
    /// <c>ILRuntimeTestBase.dll</c>, a non-BCL host CLR assembly). Used as the
    /// FOCUSED input for the Step 25 S3-5 host-CLR-registration proof:
    /// <list type="bullet">
    /// <item>the standalone <c>ilrt_neoc</c> CLI compiles this assembly (with
    /// ILRuntimeTestBase.dll as a reference) to a <c>.neo</c> with NO fatal -- the
    /// host CLR enum resolves as <c>CLRType</c> via <c>Assembly.LoadFrom</c>
    /// (mirroring the in-process model where ILRuntimeTestBase is a project ref
    /// resident in the host <c>System.AppDomain</c>).</item>
    /// <item>the runtime self-check loads this assembly + invokes the methods to
    /// prove the enum value round-trips through compile -> .neo -> load ->
    /// ExecuteNeo (the resolved type is the REAL CLRType, not an ILType shadow --
    /// a shadow would mis-execute the enum local / equality).</item>
    /// </list>
    /// This assembly deliberately references ONLY the CLR enum (no host CLR class
    /// base -> no cross-binding adaptor needed), so the standalone compile is clean
    /// end-to-end. The full TestCases.dll, by contrast, also references host CLR
    /// classes some IL types inherit -- those need CLR adaptors the standalone CLI
    /// does not register (a separate, already-deferred gap, NOT the
    /// host-CLR-type-resolution concern closed here).
    /// </summary>
    public class ClrEnumProbe
    {
        // Declares a TestCLREnum local -> a Cecil TypeRef to the host CLR enum in
        // the body (the exact token that fatals at AppDomain.cs:1409 when the host
        // CLR assembly is unregistered). Returns the enum's underlying int (1) so
        // the Neo Run shim's primitive-return path (NeoBoxReturnValue) yields a
        // clean boxed int.
        public static int ReadEnum()
        {
            ILRuntimeTest.TestFramework.TestCLREnum e = ILRuntimeTest.TestFramework.TestCLREnum.Test2;
            return (int)e;   // 1
        }

        // A second shape: two TestCLREnum locals + an enum equality / inequality
        // (ceq on the underlying ints). Returns 1 (the int value of Test2) when
        // Test2 != Test3 (they differ) -- proving the enum equality executes
        // correctly (a shadow ILType would mis-execute it).
        public static int ReadEnumCmp()
        {
            ILRuntimeTest.TestFramework.TestCLREnum a = ILRuntimeTest.TestFramework.TestCLREnum.Test2;
            ILRuntimeTest.TestFramework.TestCLREnum b = ILRuntimeTest.TestFramework.TestCLREnum.Test3;
            if (a == b) return -1;        // equal? no (Test2=1, Test3=2)
            if (a != b) return (int)a;    // differ? yes -> 1
            return -2;
        }
    }
}
