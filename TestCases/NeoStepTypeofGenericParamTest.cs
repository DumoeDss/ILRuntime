using System;

namespace TestCases
{
    // neo-typeof-generic-param probes.
    //
    // The Step-22 generic-method TEMPLATE captures a register-index body once
    // (from the first capture-eligible instantiation) and reuses it for later
    // instantiations via CloneAndPatch, which re-resolves T-IDENTITY TOKENS
    // (ldtoken / Box / Isinst / ...) for the concrete generic arg. The typeof(T)
    // producer is `ldtoken T; call Type.GetTypeFromHandle`; the ldtoken type
    // token lives in OperandLong (low dword = Operand2 @12). Before this change
    // ExtractPatches had NO Ldtoken case, so a cloned template kept the CAPTURE-T
    // hash for typeof(T). These probes exercise the template path directly.
    //
    // The generic helper carries a try/catch so the Neo JIT NEVER inlines it
    // (hasExceptionHandler -> not inlinable; child-14 finding); an inlined
    // helper would bypass the generic-method template entirely. The body is
    // kept >10 register instrs for the same reason.
    public class NeoStepTypeofGenericParamTest
    {
        public class TgpHelperA { public int x; }
        public class TgpHelperB { public int y; }

        // Returns typeof(T) for the concrete T. The try/catch + body size force
        // a REAL call (no inlining), so the generic-method template is captured
        // on the first instantiation and CloneAndPatch'd on later ones.
        static Type TypeOfT<T>()
        {
            Type t = null;
            try
            {
                t = typeof(T);
            }
            catch (Exception)
            {
                throw;
            }
            // Pad to >10 register instrs to defeat the size-based inline check.
            int p = 0; p++; p++; p++; p++; p++; p++; p++; p++;
            if (t == null) throw new Exception("typeof returned null");
            return t;
        }

        // TC1 template-path ldtoken patch: first call captures the template
        // (T=TgpHelperA); the second call (T=TgpHelperB) reuses it (both ref
        // types -> same typed-opcode category -> no fall-back -> CloneAndPatch).
        // Without the ExtractPatches Ldtoken case the second typeof(T) keeps the
        // capture-T hash -> a == b -> DivideByZero fault.
        public static void NeoStepTypeofGenericParam_TC1_TemplateLdtokenPatch()
        {
            Type a = TypeOfT<TgpHelperA>();
            Type b = TypeOfT<TgpHelperB>();
            if (a == b)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (a != typeof(TgpHelperA))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (b != typeof(TgpHelperB))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 ConvertChangeType unwrap redirect: typeof(int)/typeof(double) push
        // ILRuntimeWrapperType (CLRType.ReflectionType); the Neo autogen
        // ChangeType_1_Neo stub does a raw cast and hands the wrapper to the
        // framework Convert.ChangeType -> "Invalid cast". The ChangeTypeNeo
        // redirect unwraps -> real System.Type. (Legacy's autogen binding
        // unwraps via CheckCLRTypes; this is the Neo twin.)
        public static void NeoStepTypeofGenericParam_TC2_ChangeTypeUnwrap()
        {
            object r1 = Convert.ChangeType("123", typeof(int));
            if ((int)r1 != 123)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            object r2 = Convert.ChangeType("345.678", typeof(double));
            double v2 = (double)r2;
            if (v2 < 345.6 || v2 > 345.7)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
