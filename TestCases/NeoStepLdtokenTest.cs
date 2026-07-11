using System;

namespace TestCases
{
    // neo-ldtoken probes: ldtoken is the producer half of `typeof(T)`
    // (`ldtoken T; call Type.GetTypeFromHandle`). Under Neo the type path
    // resolves IType and pushes type.ReflectionType as a Neo object ref;
    // Type.GetTypeFromHandle is a no-op pass-through, so the System.Type
    // flows straight to the consumer.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test
    // simply returns without throwing; a logic failure is surfaced by a
    // deliberate `1/0` (DivideByZero fault; the Neo VM cannot yet
    // `new Exception(...)`). Per the child-1 finding a probe must ASSERT on
    // the consumed value (a wrong/null value must reach a fault), so each
    // probe feeds the typeof result to a CLR redirect (Type.op_Equality /
    // Type.get_FullName / String.op_Equality) and faults when the value is
    // wrong. A null result from a broken GetTypeFromHandle path dereferences
    // (get_FullName NRE) or compares equal (op_Equality on two nulls) and
    // so also fails. Tests are `public static void` parameterless.

    // An ILRuntime-defined (hotfix-DLL) type for the IL-type typeof probe.
    public class NeoStepLdtokenHelper
    {
        public int value;
    }

    public class NeoStepLdtokenTest
    {
        // TC1 typeof of a primitive type: the ldtoken type path resolves
        // System.Int32 and get_FullName yields "System.Int32". A broken
        // (null) result NREs inside get_FullName; a wrong result fails the
        // string equality -> 1/0 fault.
        public static void NeoStepLdtoken_TC1_TypeofIntFullName()
        {
            Type t = typeof(int);
            string fn = t.FullName;
            if (fn != "System.Int32")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 typeof of a CLR reference type: resolves System.String.
        public static void NeoStepLdtoken_TC2_TypeofStringFullName()
        {
            Type t = typeof(string);
            string fn = t.FullName;
            if (fn != "System.String")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 typeof of an ILRuntime-defined type: ldtoken resolves the ILType
        // via AppDomain.GetType and pushes its ReflectionType. Assert it is
        // distinct from typeof(int) via Type.op_Equality (a null/broken result
        // compares null==null -> equal to a likewise-null typeof(int) -> fault;
        // but to stay robust regardless, also feed it through get_FullName so a
        // null deref surfaces). The expected full name is the hotfix namespace.
        public static void NeoStepLdtoken_TC3_TypeofILType()
        {
            Type t = typeof(NeoStepLdtokenHelper);
            // Deref first: a null (broken) result faults here via NRE.
            string fn = t.FullName;
            // The IL type's ReflectionType full name is namespace-qualified.
            if (fn != "TestCases.NeoStepLdtokenHelper")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC4 downstream consumer across a call boundary: feed two typeof
        // results to Type.op_Equality (a CLR redirect that reads the pushed
        // references). typeof(int) must equal itself and differ from
        // typeof(string). A broken (all-null) path yields null==null (equal)
        // for the distinctness half -> overall false -> fault, proving the
        // pushed reference actually flows through the call boundary and is not
        // a stale/null slot.
        public static void NeoStepLdtoken_TC4_ConsumerEqualityChain()
        {
            Type ti = typeof(int);
            Type ti2 = typeof(int);
            Type ts = typeof(string);
            if (!(ti == ti2 && ti != ts))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC5 FIELD path (Operand==0): a C# array initializer with many
        // constant elements compiles to
        //   newarr; dup; ldtoken <compiler-synthetic static data field>;
        //   call RuntimeHelpers.InitializeArray
        // The ldtoken exercises the FIELD path: the synthetic static field's
        // declaring-type token lives in the HIGH dword of OperandLong and the
        // field index in the low dword. The Operand3-alias bug stamps the dest
        // ref slot into Operand3 (@16-19) -- which ALIASES that high dword --
        // so the field path mis-resolves the declaring type and throws a
        // NullReferenceException at GetStaticFieldOffset DURING the ldtoken,
        // before InitializeArray ever runs. With the Operand4 (@20-23,
        // disjoint) fix the declaring type resolves and the field value is
        // read; the SUBSEQUENT InitializeArray call then hits a SEPARATE,
        // pre-existing Neo gap (no Neo redirect for InitializeArray -> its
        // RuntimeFieldHandle param fails the Step-13b ValueTypeBinder marshal
        // -> NotImplementedException).
        //
        // Probe design: a typed catch on NotImplementedException. That NIE is
        // thrown inside the InitializeArray CALL MARSHALLING, which is reached
        // ONLY if the preceding ldtoken completed without throwing -- so a PASS
        // proves the field path resolved. A re-introduced Operand3 alias
        // re-surfaces the GetStaticFieldOffset NRE (a NullReferenceException,
        // NOT a NotImplementedException) -> uncaught -> test FAILs. When a Neo
        // InitializeArray redirect is added later, no exception is thrown and
        // the contents assertion below takes over (forward-compatible). 32
        // distinct ints (128 bytes) forces the compiler to the InitializeArray
        // + ldtoken <field> form rather than 32 individual Stelem stores.
        public static void NeoStepLdtoken_TC5_ArrayInitializerFieldPath()
        {
            try
            {
                int[] arr = new int[] {
                    100, 101, 102, 103, 104, 105, 106, 107,
                    108, 109, 110, 111, 112, 113, 114, 115,
                    116, 117, 118, 119, 120, 121, 122, 123,
                    124, 125, 126, 127, 128, 129, 130, 131 };
                if (arr.Length != 32)
                {
                    int z = 1; int d = 0; int _ = z / d;
                }
                for (int i = 0; i < 32; i++)
                {
                    if (arr[i] != 100 + i)
                    {
                        int z = 1; int d = 0; int _ = z / d;
                    }
                }
            }
            catch (System.NotImplementedException)
            {
                // Downstream InitializeArray RuntimeFieldHandle Step-13b NIE --
                // reachable ONLY because the ldtoken FIELD PATH resolved
                // (Operand4 fix in place). Expected until a Neo InitializeArray
                // redirect is added. PASS.
            }
        }
    }
}
