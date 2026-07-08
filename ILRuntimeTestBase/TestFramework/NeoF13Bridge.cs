using System;

namespace ILRuntimeTest.TestFramework
{
    // TEMP F-13 reproduction bridge. A CLR type the IL probe calls; its Neo
    // redirect (registered by the CLI probe driver) performs appdomain.Invoke ->
    // Run -> ExecuteNeo WHILE the outer IL method's ExecuteNeo is in flight (the
    // nested-ExecuteNeo shape). This forces the F-13 re-entrancy corruption on
    // HEAD. Removed before ship.
    public class NeoF13Bridge
    {
        public static int NestedInvoke()
        {
            // The redirect overrides this body; unreachable for the IL call path.
            return 0;
        }
    }
}
