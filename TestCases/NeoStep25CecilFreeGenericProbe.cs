using System;

namespace TestCases
{
    // ===== Step 25 S3-x (Cecil-free generic): the child-8 probe =====
    //
    // A type with a GENERIC method `T Echo<T>(T v)` + non-generic PARAMETERLESS
    // wrappers. The child-8 self-check compiles this probe -> .neo in AppDomain A
    // (Cecil-loaded), then Cecil-free-LOADS the .neo into a FRESH AppDomain B (a
    // `new AppDomain()` with NO Cecil module), invokes each wrapper via domainB.
    // Invoke, and asserts the result EQUALS its known-expected value.
    //
    // The cross-section the S3-2 Cecil-free load + the S2 generic-instantiation-
    // at-load machinery must cover together: a generic method TEMPLATE carried in
    // the .neo, re-resolved + bound Cecil-free (no Cecil module in B), then a
    // generic-instance call routed through Step-22 CloneAndPatch against the AOT
    // template, where the generic arg T is re-resolved Cecil-free.
    //
    // PARAMETERLESS wrappers by design: the Neo host entry (ILIntepreter.Run) is a
    // Step-6 shim that takes no args (the harness convention). Inputs baked in.
    // Top-level + NON-GENERIC type (the generic-ness is on the METHOD) + BCL-refs-
    // only: keeps the TypeRef full name == the LoadedTypes key + within the S1/S2
    // reference boundary.
    //
    // Echo<T> is a pure-dataflow body with a generic-param LOCAL (tmp). It has NO
    // T-identity token (no Box T / Ldobj T / default(T)). The local drives the
    // VariableTypes re-resolution + the struct-T Initobj prefix rebuild. For each
    // concrete T the body is the SAME T-invariant template; the back-half
    // specializes the Move/Initobj for the concrete T.
    //
    // ConstGeneric<T> carries the observable Ldc_I4 1234567 for the body-mutation
    // guard (mutate the deserialized TemplateBody -> assert the MUTATED value on
    // the Cecil-free exec -> proves CloneAndPatch ran the genuine AOT template).

    public class NeoStep25CecilFreeGenericProbe
    {
        // Pure dataflow with a generic-param LOCAL. tmp = v; return tmp.
        // No T-identity token. Expected (any T): the input value round-trips.
        // NOTE: this trivial body is INLINED into the wrappers by the JIT
        // (under MaximalInlineInstructionCount), so the wrapper cells exercise
        // the compile-side specialization, NOT the Cecil-free-load generic
        // dispatch. The Cecil-free-load generic dispatch (a non-inlined generic-
        // instance call) is exercised by the G2 body-mutation guard, which
        // drives a FRESH ConstGeneric<string> instance via MakeGenericMethod
        // (never inlined) + proves the AOT template ran. This is the documented
        // load-bearing split (see the child-8 design.md).
        public T Echo<T>(T v)
        {
            T tmp = v;
            return tmp;
        }

        // Constant body for the TEMPLATE BODY-MUTATION guard. Compiles to
        // Ldc_I4 <CONST> + Ret (T is unused -> no T-identity token). The capstone
        // mutates the deserialized TemplateBody Ldc_I4 CONST -> MUTATED before the
        // Cecil-free load; observing MUTATED on the Cecil-free exec proves
        // CloneAndPatch ran the genuine AOT template (NOT a JIT/Cecil fallback).
        public int ConstGeneric<T>()
        {
            return 1234567;
        }

        // ---- non-generic PARAMETERLESS wrappers (the Run shim is no-arg) ----
        // Each instantiates + calls a generic method at a concrete T. The wrappers
        // are INSTANCE methods so the Cecil-free ILType must be Instantiate'd first
        // (exercises the Cecil-free instance-ctor path too). WrapEchoRef returns
        // int (the Run shim's NeoBoxReturnValue handles primitive returns only).
        public int WrapEchoInt()
        {
            return Echo<int>(42);
        }

        public long WrapEchoLong()
        {
            return Echo<long>(9000000000L);
        }

        // ref-T functional cell: ConstGeneric<string> (T=string, a reference type).
        // Returns the int constant -- exercises ref-T generic instantiation +
        // CloneAndPatch + execution without a reference return.
        public int WrapEchoRef()
        {
            return ConstGeneric<string>();
        }

        public int WrapEchoStruct()
        {
            NeoStep25CegVal s;
            s.X = 77;
            return Echo<NeoStep25CegVal>(s).X;
        }

        // Parameterless wrapper for the body-mutation guard (drives a FRESH
        // ConstGeneric<string> instance so the mutation cell routes through the
        // AOT template). Returns the (possibly mutated) constant.
        public int WrapConstRef()
        {
            return ConstGeneric<string>();
        }

        // ===== T-IDENTITY-TOKEN body (the child-8 follow-up). Boxes T to object
        // then unboxes back -- the body carries a `Box T` + an `Unbox.Any T` (a
        // T-identity TypeToken patch site). On HEAD the Cecil-free S3
        // BuildFromNeoRecord REJECTS the template (a T-identity token needs a
        // Cecil TypeReference to re-resolve, which a Cecil-free .neo record does
        // not carry in a form S3 re-resolves). The follow-up adds a Cecil-free
        // GenericParamIdx-keyed T-substitution at CloneAndPatch so the concrete T
        // hash is re-derived Cecil-free. Expected (any T): the input round-trips.
        public T BoxUnbox<T>(T v)
        {
            object o = v;
            return (T)o;
        }

        // Parameterless wrapper for the T-identity body. int T (Box+Unbox.Any of a
        // 4-byte prim -> a boxed int). The authoritative Cecil-free T-identity
        // dispatch is the capstone's G3 fresh-instance cell (BoxUnbox<int> via
        // MakeGenericMethod, never inlined). struct-T wrapper omitted: Box<IL-VT>
        // + Unbox.Any<IL-VT> is an engine-level Box/Unbox-of-IL-VT gap (it fails a
        // Cecil-loaded JIT run too, so NOT a T-identity Cecil-free regression).
        public int WrapBoxUnboxInt()
        {
            return BoxUnbox<int>(4242);
        }

        // ===== METHOD-TOKEN T-IDENTITY body (the child-8 MethodToken follow-up).
        // A `constrained. T`-qualified callvirt: T is constrained to IComparable<T>,
        // and the body calls x.CompareTo(x). The C# compiler emits
        //   constrained. T
        //   callvirt instance int32 class [mscorlib]System.IComparable`1<T>::CompareTo(!T)
        // i.e. the callvirt's METHOD token is T-qualified (its declaring type is the
        // generic instance IComparable<T>, which contains the method generic param
        // T). On HEAD the Cecil-free S3 RebuildPatchesNoCecil REJECTS a MethodToken
        // T-identity patch (hasMethodIdentityToken -> BuildFromNeoRecord returns
        // null -> the template is skipped -> the method falls back to JIT, which a
        // Cecil-free AppDomain cannot run). Expected (any comparable T): the sign of
        // (a.CompareTo(b)) matches a.CompareTo on the concrete type. int T: returns
        // CompareTo(5) on input 7 -> a positive int.
        public int CompareElems<T>(T a, T b) where T : IComparable<T>
        {
            return a.CompareTo(b);
        }

        // Parameterless wrapper: int T. 7.CompareTo(5) > 0 (sign-normalized to 1
        // -- CompareTo's magnitude is not documented, only its sign). The
        // authoritative Cecil-free MethodToken T-identity dispatch is the
        // capstone's G4 fresh-instance cell (CompareElems<int> via
        // MakeGenericMethod, never inlined).
        public int WrapCompareElemsInt()
        {
            int c = CompareElems<int>(7, 5);
            return c > 0 ? 1 : (c < 0 ? -1 : 0);
        }
        // NOTE: a string-T wrapper (CompareElems<string>, the ref-T path) is
        // OMITTED: a constrained. T callvirt whose concrete T is a REFERENCE type
        // hits a separate ExecuteNeo Constrained arm ("box-once no-op / ref-type
        // this") that throws on HEAD -- an engine-level gap (it FAILS the "A JIT"
        // reference -- a Cecil-loaded run with no T-identity machinery in play --
        // so it is NOT a T-identity Cecil-free regression; out of scope).
    }

    // A top-level (NON-NESTED) value type used as a concrete struct generic arg
    // (WrapEchoStruct). Top-level keeps its TypeRef full name == the LoadedTypes
    // key. One int field so the wrapper observes the round-trip via definite-
    // assignment (s.X = 77) instead of newobj (needs [VT-THIS-ADDR]).
    public struct NeoStep25CegVal
    {
        public int X;
    }
}
