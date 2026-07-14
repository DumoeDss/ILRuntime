using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-autogen-binding-invoke (cluster D, UnitTest_RefCLREnum): a CLR method
    // with a `ref`/`out` enum parameter is routed through the Neo byte* reflection
    // fallback (InvokeNeoClrMethod bypasses the autogen redirect for any byref
    // method, CLRMethod.cs:1264). The reflection reader boxed the enum param's
    // underlying Int32 value as a raw boxed Int32, so MethodBase.Invoke's CheckValue
    // rejected it against the `EnumType&` parameter ("Object of type 'System.Int32'
    // cannot be converted to type 'EnumType&'"). The fix boxes byref enum params as
    // the enum type (Enum.ToObject) so CheckValue accepts them. This probe calls a
    // host method with `out TestCLREnum` via the byref reflection path and asserts
    // the round-trip (forward read tolerated + write-back of the assigned value).
    public class NeoStepRefClrEnumTest
    {
        public static void NeoStep_RefClrEnum_TC1()
        {
            // TestCLREnumRef(out UInt32 key, out TestCLREnum tag) -- the `out
            // TestCLREnum` param is the byref enum. On HEAD this throws
            // ArgumentException (Int32 -> TestCLREnum&); after the fix it returns
            // key=2, tag=Test2.
            TestCLREnumClass.TestCLREnumRef(out uint key, out ILRuntimeTest.TestFramework.TestCLREnum tag);
            if (key != 2)
                throw new Exception("NeoStep_RefClrEnum_TC1: key=" + key + " expected 2");
            if (tag != ILRuntimeTest.TestFramework.TestCLREnum.Test2)
                throw new Exception("NeoStep_RefClrEnum_TC1: tag=" + tag + " expected Test2");
        }
    }
}
