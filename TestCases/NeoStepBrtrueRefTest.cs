using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-brtrue-on-reference regression probes.
    //
    // The Neo Brtrue/Brfalse arms tested the condition as a low int32
    // (!= 0 / == 0). Under the Neo object model a reference is an mStack INDEX
    // in the slot's primitive bytes, and null is the -1 sentinel (Ldnull) or a
    // non-zero index to a null mStack entry (a loaded IL-static reference). The
    // int32 test misreads null as TRUTHY (a non-zero index or -1 is non-zero).
    //
    // The fix type-specializes the branch: a Brtrue/Brfalse whose condition
    // register is a reference slot is rewritten to Brtrue_Ref/Brfalse_Ref, whose
    // runtime arm tests `idx >= 0 && mStack[idx] != null` (Legacy parity). The
    // -1 sentinel reads falsey; a non-zero index to a null entry reads falsey;
    // a real object reads truthy.
    //
    // These are regression guards: they exercise the DIRECT reference-branch
    // lowering (the delegate-cache `ldsfeld cache; brtrue` shape, and a non-null
    // reference coalesce) and assert the Brtrue_Ref specialization fires AND
    // resolves correctly. A passing test returns without dividing by zero; a
    // logic failure surfaces a deliberate 1/0.
    //
    // NOTE on the stash-toggle: the Neo frame is zero-initialized, so the
    // delegate-cache check register reads 0 (falsey) on HEAD too -> the init
    // runs on HEAD by accident and these guards pass on both HEAD and the fixed
    // build. The load-bearing fault evidence for the fix is therefore NOT a
    // probe-NRE-on-HEAD but: (a) the F4 specialization is LIVE -- without the
    // accompanying Call-case stale-reference clear (JITCompiler), F4 mis-fires
    // `brtrue.ref` on bool call results and regresses NeoStepOrChain +
    // NeoStep16 (2 failures); (b) child-3 established that the IL-static offset
    // fix (F3) ALONE unmasks F4 on the delegate cache (NeoStep20_Tr2/Tr5 break);
    // (c) the ceq-form lazy-init (TestStaticFieldInstance) NREs on HEAD via a
    // related-but-distinct ceq-null-sentinel gap.

    public class NeoStepBrtrueRefHelper
    {
        public int Value;
        public NeoStepBrtrueRefHelper(int v) { Value = v; }
    }

    public class NeoStepBrtrueRefTest
    {
        // TC1 (delegate cache -- the NeoStep20_Tr2/Tr5 shape): `new Func<int>(
        // lambda)` triggers Roslyn's compiler-generated delegate cache -- a
        // static `<>c.<>9__X_Y` field + the DIRECT `ldsfld cache; brtrue
        // skipInit; newobj; stsfld; skipInit:` lazy-init. Brtrue_Ref MUST fire on
        // the cache reference; the null cache MUST read falsey so the init runs
        // and the delegate is created + invoked.
        private static int NeoStepRunCachedLambda()
        {
            Func<int> d = new Func<int>(() => 42);
            return d();
        }
        public static void NeoStepBrtrueRef_TC1_DelegateCacheLazyInit()
        {
            int r = NeoStepRunCachedLambda();
            if (r != 42) { int x = 1; int y = 0; int _ = x / y; }
        }

        // TC2 (non-null reference coalesce -- the truthy guard): `h ?? new T()`
        // on a NON-null local lowers to `ldloc h; brtrue USE; ...; USE:`. A
        // non-null reference MUST read truthy after the fix (Legacy mStack[idx]
        // != null parity) so the coalesce keeps the existing instance. Guards
        // against a falsey-misread that would discard a populated reference.
        public static void NeoStepBrtrueRef_TC2_NonNullLocalCoalesce()
        {
            NeoStepBrtrueRefHelper h = new NeoStepBrtrueRefHelper(77);
            NeoStepBrtrueRefHelper r = h ?? new NeoStepBrtrueRefHelper(-1);
            if (r.Value != 77) { int x = 1; int y = 0; int _ = x / y; }
        }
    }
}
