using System;
using ILRuntimeTest.TestFramework;
using TestCases;

namespace TestCases
{
    public class NeoStepR3_10051Probe
    {
        // Bare struct-local -> field -> property read (no IL instance, no sort).
        public static int NeoStepR3_10051_TC1_BareStructFieldProperty()
        {
            var v = new Fixed64Vector2(555, 0);
            long r = v.x.RawValue;
            if (r != 555)
                throw new Exception("TC1 expected 555 got " + r);
            return 0;
        }

        // Struct-local field address (ldloca; ldflda) -> RawValue, isolating the
        // ldflda-on-CLR-struct-local-field step (child-15 lineage).
        public static int NeoStepR3_10051_TC2_LdfldaFieldRawValue()
        {
            var v = new Fixed64Vector2(111, 222);
            long rx = v.x.RawValue;
            long ry = v.y.RawValue;
            if (rx != 111 || ry != 222)
                throw new Exception("TC2 expected 111/222 got " + rx + "/" + ry);
            return 0;
        }

        // IL instance with a CLR-struct field, read via property getter V2
        // (mirrors UnitTest_10051's list[0].V2 path).
        public class R3Base
        {
            private Fixed64Vector2 fv2;
            public R3Base() { fv2 = new Fixed64Vector2(999, 0); }
            public Fixed64Vector2 V2 { get { return fv2; } }
        }

        public static int NeoStepR3_10051_TC3_IlInstanceGetterChain()
        {
            var b = new R3Base();
            long r = b.V2.x.RawValue;
            if (r != 999)
                throw new Exception("TC3 expected 999 got " + r);
            return 0;
        }
    }
}
