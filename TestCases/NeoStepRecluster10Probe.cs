using System;

namespace TestCases
{
    public static class NeoStepRecluster10Probe
    {
        // TC1: isolate `new object()` -- is the newobj result non-null?
        public static void NeoStepRecluster10_TC1_NewObject()
        {
            object obj = new object();
            if (obj == null)
                throw new Exception("TC1: new object() is null");
        }

        // TC2: the exact UnitTest_TestInline01 shape -- callee nullifies its param,
        // caller's obj must stay non-null (value semantics).
        public static void NeoStepRecluster10_TC2_InlineAlias()
        {
            object obj = new object();
            NullifySub(obj);
            if (obj == null)
                throw new Exception("TC2: callee nullified caller's obj (by-ref aliasing)");
        }

        // TC3: control -- callee reads but does NOT nullify; caller must stay non-null.
        public static void NeoStepRecluster10_TC3_NoNullify()
        {
            object obj = new object();
            ReadSub(obj);
            if (obj == null)
                throw new Exception("TC3: obj became null without nullify");
        }

        // TC4: pass a non-null ref via a field, then nullify in callee -- isolates
        // whether a string (ldstr) arg survives the inline call.
        public static void NeoStepRecluster10_TC4_StringArg()
        {
            string s = "hello";
            NullifyStr(s);
            if (s == null)
                throw new Exception("TC4: callee nullified caller's string");
            if (s != "hello")
                throw new Exception("TC4: caller string corrupted");
        }

        static void NullifySub(object o)
        {
            Console.WriteLine(o);
            o = null;
        }

        static void ReadSub(object o)
        {
            Console.WriteLine(o);
        }

        static void NullifyStr(string o)
        {
            Console.WriteLine(o);
            o = null;
        }
    }
}
