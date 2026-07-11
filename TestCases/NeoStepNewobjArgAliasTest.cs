using System;

namespace TestCases
{
    // neo-il-static-ref-field-readback regression probe (child 13).
    //
    // The IL-static reference-field read-back itself is CORRECT (stsfld writes
    // the object; ldsfld reads it back). The real defect this child fixes lives
    // in the IL reference-type newobj arm: when the newobj dest register ALIASES
    // a reference argument register -- the canonical C# lowering of a lazy-init
    // `if (field == null) field = new T(refArg);` emits `ldstr/ldloc refArg;
    // newobj(refArg)` where the newobj reuses the arg's register as its dest --
    // the new instance's ref slot (newobjDstIdx) coincides with the arg's own
    // ref slot, so the arg's mStack index EQUALS newobjDstIdx. The newobj arm
    // stored the instance at mStack[newobjDstIdx] BEFORE copying the args to the
    // ctor, clobbering the arg, so the ctor received `this` (the new instance)
    // as the aliased reference argument. The stored field then held an instance
    // whose reference ctor-arg field was the instance itself -> downstream
    // InvalidCastException (ILTypeInstance -> the arg's CLR type).
    //
    // The fix re-bases the colliding reference arg to a fresh mStack slot before
    // the instance is stored, so the ctor receives the real arg.
    //
    // The probe mirrors the ACTIVELY-FAILING SimpleTest.TestStaticFieldInstance /
    // TestA.Instance shape: an IL-static reference field, lazy-initialized in a
    // getter via `new T(stringArg)`, then the ctor's string arg is read back.
    // On HEAD the ctor gets `this` as `tag`, so tag is the holder instance (an
    // ILTypeInstance) and the deliberate 1/0 trips. With the fix, tag == "lazy".
    public class NeoStepNewobjArgHolder
    {
        public string Tag;
        public NeoStepNewobjArgHolder(string tag) { Tag = tag; }
    }

    public class NeoStepNewobjArgAliasTest
    {
        private static NeoStepNewobjArgHolder _inst;

        public static NeoStepNewobjArgHolder Instance
        {
            get
            {
                if (_inst == null)
                    _inst = new NeoStepNewobjArgHolder("lazy");
                return _inst;
            }
        }

        // TC1: the ctor's reference arg MUST round-trip into .Tag. On HEAD the
        // aliased newobj clobbers the arg -> .Tag is the holder ILTypeInstance
        // -> the (object) equality is false -> deliberate 1/0. With the fix,
        // .Tag == "lazy".
        public static void NeoStepNewobjArgAlias_TC1_LazyInitRefArg()
        {
            string tag = Instance.Tag;
            bool ok = (object)tag == (object)"lazy";
            if (!ok) { int q = 1; int _ = q / 0; }
        }

        // TC2: a SECOND aliasing ctor call through a different static field, to
        // guard against the fix only handling the first-ever call. Same shape,
        // distinct value ("second").
        private static NeoStepNewobjArgHolder _inst2;
        public static NeoStepNewobjArgHolder Instance2
        {
            get
            {
                if (_inst2 == null)
                    _inst2 = new NeoStepNewobjArgHolder("second");
                return _inst2;
            }
        }

        public static void NeoStepNewobjArgAlias_TC2_SecondLazyInitRefArg()
        {
            string tag = Instance2.Tag;
            bool ok = (object)tag == (object)"second";
            if (!ok) { int q = 1; int _ = q / 0; }
        }
    }
}
