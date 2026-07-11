using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // neo-clr-object-field-il-path probes. GetNeoILInstance
    // (ILIntepreter.Neo.cs) is the guard every typed Ldfld/Stfld heap arm +
    // the Stobj/Ldobj IL-instance fallback calls to resolve the owner
    // ILTypeInstance. It previously threw a "Step 17/13b deferred"
    // NotImplementedException for ANY non-ILTypeInstance owner. Two owner
    // shapes actually reach it:
    //   (a) NULL -- an mStack slot holding null. The reproducible path is an
    //       uninitialized IL static reference field loaded by ldsfld: the Neo
    //       ldsfld IL-static-ref arm materializes the stored null as a VALID
    //       mStack index pointing at a null entry (it does mStack.Add(null)
    //       and writes the index), so the owner register carries objIndex >= 0
    //       with mStack[objIndex] == null. ldfld/stfld on a null instance is a
    //       NullReferenceException in the CLR -- NOT a deferred feature -- so
    //       the guard must throw NRE, not a misleading "deferred" NIE.
    //   (b) a CrossBindingAdaptorType wrapper -- an IL type that inherits a CLR
    //       base, flowed through CLR code / reflection / a generic collection,
    //       returns as its CLR adaptor. The field was JIT-classified as IL-
    //       declared (else the JIT emits the raw opcode the CLR field-hash path
    //       handles), so it lives on the underlying ILTypeInstance; unwrap the
    //       adaptor via .ILInstance (mirrors the raw Ldfld/Stfld handler,
    //       child 9 / neo-il-instance-clr-base-field).
    // The fix: null -> NullReferenceException; CrossBindingAdaptorType ->
    // .ILInstance; any other CLR shape keeps the defensive Step-tagged NIE.
    //
    // Fault discipline (child-1/child-2): each probe MUST fault on HEAD. On HEAD
    // the typed ldfld throws the "Step 17/13b deferred" NotImplementedException,
    // which is NOT a NullReferenceException, so the `catch(NullReferenceException)`
    // filter does not match -> the NIE propagates uncaught -> the test fails
    // (FAULT). After the fix the ldfld throws NRE -> caught -> the test returns
    // normally (PASS). The Neo VM cannot yet `new Exception(...)`, so a passing
    // test simply returns without throwing.

    public class NeoStepClrObjIlHolder
    {
        public static NeoStepClrObjIlHolder Lazy;   // uninitialized null IL static ref field
        public int IntField = 7;
        public string RefField = "hi";
    }

    public class NeoStepClrObjIlPathTest
    {
        // TC1: ldfld (int) on a null IL static ref field -> NullReferenceException
        // (exercises the Ldfld_I4 typed arm + the GetNeoILInstance null path).
        // Faults on HEAD (NIE propagates); passes after the fix (NRE caught).
        public static void NeoStepClrObjIl_TC1_NullOwnerLdfldNre()
        {
            try
            {
                int v = NeoStepClrObjIlHolder.Lazy.IntField;
                // reached => no exception thrown (wrong) -> fault
                int z = 1; int d = 0; int _ = z / d;
            }
            catch (NullReferenceException)
            {
                // expected after the fix
            }
        }

        // TC2: ldfld (string ref) on a null IL static ref field -> NRE
        // (exercises the Ldfld_Ref typed arm + the GetNeoILInstance null path).
        public static void NeoStepClrObjIl_TC2_NullOwnerLdfldRefNre()
        {
            try
            {
                string s = NeoStepClrObjIlHolder.Lazy.RefField;
                int z = 1; int d = 0; int _ = z / d;
            }
            catch (NullReferenceException)
            {
                // expected after the fix
            }
        }
    }
}
