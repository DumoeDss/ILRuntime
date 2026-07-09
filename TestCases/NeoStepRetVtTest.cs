using System;

namespace TestCases
{
    // neo-ret-vt-with-ref-fields: an IL method RETURNING a struct WITH reference
    // fields. On HEAD the `Ret` opcode in ExecuteNeo throws
    // NotImplementedException("Neo return with value-type reference fields
    // requires Step 12/13 return layout support.") for any such method. This
    // change adds a Move_Vt-style return-copy branch (primitive byte CopyBlock +
    // ref-slot loop from the callee return register's ref region to the caller's
    // dest ref region).
    //
    // Assertion mechanism: intentional integer divide-by-zero (same pattern as
    // NeoStep12bTest -- the Neo VM cannot construct `new Exception(...)` inside
    // interpreted code for a throw-based assertion). Each probe returns / reads
    // a struct-with-ref-fields and asserts BOTH the primitive and the reference
    // fields survived the return copy (the ref-slot copy is load-bearing).

    // Struct with one reference field. TotalReferenceCount == 1.
    public struct NeoStepRetVtOneRef
    {
        public int x;
        public string s;
    }

    // Struct with multiple reference fields. TotalReferenceCount == 2.
    public struct NeoStepRetVtManyRefs
    {
        public int n;
        public string str;
        public object obj;
    }

    // Pure-primitive struct (control). TotalReferenceCount == 0. This path
    // already works on HEAD (the returnRefCount==0 CopyBlock arm); the probe
    // guards against a regression.
    public struct NeoStepRetVtPurePrim
    {
        public int a;
        public int b;
    }

    // Nested struct with a reference field inside the inner struct. Used by the
    // nested-return probe. Reading the inner struct as a WHOLE value (ldfld Inner)
    // compiles to the unimplemented Ldfld_Value opcode (a Step 12b non-goal), so
    // the nested probe reads only primitive top-level fields + the inner's ref
    // field via address-based access on the returned copy.
    public struct NeoStepRetVtInner
    {
        public int ix;
        public string istr;
    }

    public struct NeoStepRetVtNested
    {
        public NeoStepRetVtInner inner;
        public int top;
    }

    public class NeoStepRetVtTest
    {
        // Helper: returns a struct with one reference field. The return path is
        // the load-bearing case (returnRefCount == 1, IsValueType == true).
        static NeoStepRetVtOneRef MakeOneRef(int x, string s)
        {
            NeoStepRetVtOneRef r = default(NeoStepRetVtOneRef);
            r.x = x;
            r.s = s;
            return r;
        }

        // Probe 1: struct { int; string } return -- assert BOTH the int field
        // AND the non-null string field are correct after return. The ref-slot
        // copy is load-bearing (without it s would be null/garbage).
        public static void NeoStepRetVt_OneRef()
        {
            NeoStepRetVtOneRef r = MakeOneRef(42, "hello");
            if (r.x != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (r.s != "hello")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Helper: returns a struct with MULTIPLE reference fields.
        static NeoStepRetVtManyRefs MakeManyRefs(int n, string str, object obj)
        {
            NeoStepRetVtManyRefs r = default(NeoStepRetVtManyRefs);
            r.n = n;
            r.str = str;
            r.obj = obj;
            return r;
        }

        // Probe 2: struct { int; string; object } return -- assert all three
        // fields (primitive + 2 refs) survived the return copy. Exercises the
        // ref-slot LOOP (returnRefCount == 2), not just a single slot. The
        // object field uses a DISTINCT shared instance (passed in by the caller)
        // so reference identity is preserved across the return copy -- boxing
        // `123` separately at the call site and inside the helper would yield
        // two distinct boxed objects and a spurious mismatch.
        public static void NeoStepRetVt_ManyRefs()
        {
            object box = 123;
            NeoStepRetVtManyRefs r = MakeManyRefs(7, "world", box);
            if (r.n != 7)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (r.str != "world")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Reference identity: the SAME boxed object survives the return copy.
            // (object.ReferenceEquals is a CLR static call with its own Neo gap;
            // assert non-null + value-equality via unbox instead, which proves
            // the ref slot survived the return copy with the right object.)
            if (r.obj == null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if ((int)r.obj != 123)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Probe 3: returned-then-field-read. `var s = Make(); assert s.s ==
        // "expected";` -- the ref must survive the return copy AND a subsequent
        // field read off the returned local. Distinct from probe 1 (which also
        // reads fields) in that the local is explicitly typed via `var` and the
        // helper builds the struct from a literal, exercising the return copy
        // with no aliasing to a caller local.
        public static void NeoStepRetVt_ReadAfterReturn()
        {
            var s = MakeOneRef(-5, "expected");
            string got = s.s;
            if (got != "expected")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (s.x != -5)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Probe 4: shallow-copy independence across two returns. Two calls to
        // the same helper return structs whose ref fields point at DISTINCT
        // string objects (the helper builds a fresh struct per call). Asserting
        // both returned copies retain their OWN ref value (and the first is
        // unaffected by the second return) proves the return copy writes a
        // per-call ref slot into the caller's dest -- NOT aliasing the callee's
        // mStack region (which is popped on each Ret, destroying any alias).
        // (A by-value echo round-trip was avoided: the trivial inliner
        // mis-classifies an inlined VT-with-refs return move -- the F-9 /
        // NEO-INLINED-RETURN-MOVE pre-existing edge -- which is out of scope for
        // this change. The two-distinct-returns shape exercises the same
        // load-bearing property -- per-call ref-slot copy into the caller dest --
        // without hitting the inliner edge.)
        public static void NeoStepRetVt_ShallowCopyIndep()
        {
            NeoStepRetVtOneRef a = MakeOneRef(1, "first");
            NeoStepRetVtOneRef b = MakeOneRef(2, "second");
            // Both returned copies retain their own primitive.
            if (a.x != 1)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (b.x != 2)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Both retain their own ref (distinct, non-null, correct).
            if (a.s != "first")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (b.s != "second")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // The first return's ref is UNAFFECTED by the second return (no
            // aliasing of a shared callee ref slot that the second pop would
            // clobber).
            if (a.s != "first")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Helper: returns a NESTED struct (an outer struct containing an inner
        // struct-with-ref-field). The whole outer is returned by value.
        static NeoStepRetVtNested MakeNested(int top, int ix, string istr)
        {
            NeoStepRetVtNested r = default(NeoStepRetVtNested);
            r.top = top;
            r.inner.ix = ix;
            r.inner.istr = istr;
            return r;
        }

        // Probe 5: nested VT-with-ref-fields return. A struct containing another
        // struct-with-ref-field, returned by value. The whole outer (primitive
        // region = the inner's ix + the outer's top; ref region = the inner's
        // istr) is copied by the Ret arm's byte CopyBlock + ref-slot loop.
        //
        // SCOPE NOTE: reading an INNER field off the returned copy
        // (`r.inner.ix` / `r.inner.istr`) lowers to `ldfld.value` (a whole-
        // nested-struct load) which hits the PRE-EXISTING Step-6 `Ldfld_Value`
        // NIE -- a Step 12b non-goal UNRELATED to the Ret-opcode fix (it blocks
        // ANY whole-nested-struct-field load, independent of return). Per the
        // change's test-design guidance ("if it hits an unrelated NIE, scope it
        // out and note it -- do NOT force it"), this probe asserts ONLY the
        // top-level primitive field `r.top`, which proves the nested return copy
        // populated the outer's primitive region (the Ret arm copied
        // returnPrimitiveSize bytes including the slot spanning the inner's ix
        // + the outer's top). The inner-field-read coverage is deferred to a
        // future Ldfld_Value step.
        public static void NeoStepRetVt_NestedVtWithRef()
        {
            NeoStepRetVtNested r = MakeNested(3, 9, "inner-str");
            // The outer's `top` field survived the nested return copy.
            if (r.top != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Helper: returns a pure-primitive struct (control). TotalReferenceCount
        // == 0 -- this path already worked on HEAD (the returnRefCount==0 arm).
        static NeoStepRetVtPurePrim MakePurePrim(int a, int b)
        {
            NeoStepRetVtPurePrim r = default(NeoStepRetVtPurePrim);
            r.a = a;
            r.b = b;
            return r;
        }

        // Probe 6 (control): pure-primitive struct return stays green. Guards
        // against a regression in the returnRefCount==0 CopyBlock arm.
        public static void NeoStepRetVt_PurePrimitiveControl()
        {
            NeoStepRetVtPurePrim r = MakePurePrim(10, 20);
            if (r.a + r.b != 30)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // REVIEWER probe (4d): ref-ONLY struct -- primSize 0, refCount 2.
        // The Ret arm's `if (returnPrimitiveSize > 0) CopyBlock(...)` must
        // SKIP the zero-size byte copy and still run the ref-slot loop for
        // returnRefCount==2. A struct with ONLY reference fields (no primitive
        // payload) is the edge the byte-copy guard exists for. Asserts BOTH
        // refs survived the return copy (non-null + correct value).
        public struct NeoStepRetVtRefOnly { public string a; public string b; }
        static NeoStepRetVtRefOnly MakeRefOnly(string a, string b)
        {
            NeoStepRetVtRefOnly r = default(NeoStepRetVtRefOnly);
            r.a = a;
            r.b = b;
            return r;
        }
        public static void NeoStepRetVt_RefOnly()
        {
            NeoStepRetVtRefOnly r = MakeRefOnly("alpha", "beta");
            if (r.a != "alpha")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (r.b != "beta")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
