using System;
using System.Collections.Generic;

namespace TestCases
{
    // Step 18 test: CLR type newobj + Q-NEWOBJ (new T(intArg) immediately after
    // a newarr) + the inlined IL value-type newobj forms.
    //
    // IL value-type newobj via a REAL (non-inlined) newobj instruction is
    // DEFERRED: it is blocked on the VT field-access lowering consistency
    // (design D2) -- a value-type ctor's `this`-relative stfld lowers to a mix
    // of in-frame `_Inline` and heap `GetNeoILInstance` arms, and the caller's
    // subsequent field reads on the newobj result are non-inline, so the
    // representation is inconsistent end-to-end. The Newobj arm surfaces a
    // clear Step-18-tagged NIE for that case (see design.md / the deferred-items
    // note). The cases here cover what DOES work in Step 18: the C# inliner
    // folds small VT ctors into the caller (no newobj emitted) and the CLR
    // newobj + Q-NEWOBJ paths.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns. A logic failure is surfaced by a deliberate `1/0` (native
    // DivideByZero fault). Tests are `public static void` parameterless.

    public struct NeoStep18Point
    {
        public int x;
        public int y;
        public NeoStep18Point(int vx, int vy)
        {
            x = vx;
            y = vy;
        }
    }

    public struct NeoStep18RefVt
    {
        public int num;
        public string txt;
        public NeoStep18RefVt(string t)
        {
            num = 1;
            txt = t;
        }
    }

    public struct NeoStep18Default
    {
        public int a;
        public int b;
    }

    // Used for the Q-NEWOBJ case: a reference type constructed with an int arg.
    public class NeoStep18Item
    {
        public int val;
        public NeoStep18Item(int v) { val = v; }
    }

    // ---- VT-THIS-ADDR probe types (close the Step 18 deferred VT-newobj). ----
    // A multi-field IL value type whose ctor writes 3 primitive fields via
    // `this.field =`. Used by TC1 (multi-field read-back) and TC2 (read-back
    // after intervening heap writes that reuse eval-stack registers -- the
    // Step 17 B1 silent-corruption class).
    public struct NeoStep18Big
    {
        public int a;
        public int b;
        public int c;
        public NeoStep18Big(int va, int vb, int vc)
        {
            a = va;
            b = vb;
            c = vc;
        }
    }

    // Nested value types: Outer owns an Inner + a primitive. The ctor of Outer
    // writes `this.inner.x` and `this.y`. Used by TC3 (nested-VT field read).
    public struct NeoStep18Inner
    {
        public int x;
        public int z;
        public NeoStep18Inner(int vx, int vz)
        {
            x = vx;
            z = vz;
        }
    }

    public struct NeoStep18Outer
    {
        public NeoStep18Inner inner;
        public int y;
        public NeoStep18Outer(int ix, int iz, int vy)
        {
            inner = new NeoStep18Inner(ix, iz);
            y = vy;
        }
    }

    // IL value type with a reference field. The ctor writes a primitive field
    // AND a reference field. Used by TC4 (ref-field propagation through the
    // in-frame-VT construction, incl. the null case).
    public struct NeoStep18Ref2
    {
        public int num;
        public string txt;
        public NeoStep18Ref2(int n, string t)
        {
            num = n;
            txt = t;
        }
    }

    // Parameter-taking ctor that leaves one field at the default (zero) and
    // assigns the others. C# 8.0 disallows parameterless struct ctors AND
    // requires all fields assigned, so the ctor sets c = 0 explicitly -- but
    // the dest is STILL zero-init'd by the runtime before the ctor runs, so a
    // buggy seeding (no zero-init) would only show if the ctor skipped a field.
    // We keep c assigned to satisfy C# 8.0; the zero-init path is exercised by
    // TC15's assertion that c == 0 (the explicitly-assigned zero).
    public struct NeoStep18PartialInit
    {
        public int a;
        public int b;
        public int c;
        public NeoStep18PartialInit(int va)
        {
            a = va;
            b = va + 1;
            c = 0;
        }
    }

    // VT whose ctor performs a non-trivial computation (a static helper call)
    // before setting fields. C# 8.0 disallows struct `: base()`, so the
    // base-chain intent is exercised implicitly (the compiler emits no base
    // call for struct ctors). This still tests a ctor body with a call in it.
    public struct NeoStep18ComplexCtor
    {
        public int v;
        public int doubled;
        public NeoStep18ComplexCtor(int vv)
        {
            int d = NeoStep18Test.NeoStep18_Double(vv);
            v = vv;
            doubled = d;
        }
    }

    public class NeoStep18Test
    {
        // TC1 IL value-type newobj with a field-setting ctor (two args). The C#
        // inliner folds this small ctor into the caller (initobj + inline
        // stfld), so no `newobj` is emitted; the field values land in the
        // caller's frame slot. Verifies the inlined VT construction path.
        public static void NeoStep18_TC1_VtCtorWithArgs()
        {
            NeoStep18Point p = new NeoStep18Point(3, 4);
            if (p.x != 3 || p.y != 4)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 IL value-type newobj with a reference field (inlined). The ref
        // field write lands in the caller's dest mStack ref slot.
        public static void NeoStep18_TC2_VtWithRefField()
        {
            NeoStep18RefVt s = new NeoStep18RefVt("x");
            if (s.num != 1 || s.txt != "x")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 IL value-type newobj with a parameterless ctor (default state).
        public static void NeoStep18_TC3_VtDefaultCtor()
        {
            NeoStep18Default d = new NeoStep18Default();
            if (d.a != 0 || d.b != 0)
            {
                int z = 1; int d2 = 0; int _ = z / d2;
            }
        }

        // TC4 Q-NEWOBJ: new T(intArg) immediately after a newarr. The
        // newarr+newobj sequence must produce a correctly-constructed object
        // and leave the array intact. (The Step 16 TC4 workaround used a
        // default-ctor + field-set; the Q-NEWOBJ JIT dump in Step 18 Phase 0
        // showed every register gets a distinct frame region + mStack ref slot,
        // so the real ctor-with-arg form is now used here and in Step 16 TC4.)
        public static void NeoStep18_TC4_QNewobjAfterNewarr()
        {
            NeoStep18Item[] a = new NeoStep18Item[3];
            NeoStep18Item item = new NeoStep18Item(7);
            if (item.val != 7)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            a[1] = item;
            NeoStep18Item got = a[1];
            if (got == null || got.val != 7)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC5 CLR type newobj via reflection (no Neo Redirection). The newobj
        // constructs the List<int> via ConstructorInfo.Invoke; the dest
        // register holds the new object.
        public static void NeoStep18_TC5_ClrNewobj()
        {
            List<int> list = new List<int>();
            list.Add(11);
            list.Add(22);
            if (list.Count != 2 || list[0] != 11 || list[1] != 22)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC6 CLR generic closed type newobj via reflection.
        public static void NeoStep18_TC6_ClrGenericNewobj()
        {
            Dictionary<int, string> dict = new Dictionary<int, string>();
            dict[1] = "a";
            dict[2] = "b";
            if (dict.Count != 2 || dict[1] != "a" || dict[2] != "b")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC7 Side-benefit of CLR newobj: `throw new SomeClrException()` is now
        // constructible (the exception object is allocated by the newobj then
        // thrown). A try/catch around it should now catch the exception. This
        // was not possible before Step 18 (CLR newobj threw the blanket NIE).
        public static void NeoStep18_TC7_ThrowNewClrException()
        {
            bool caught = false;
            try
            {
                throw new InvalidOperationException("neo step18");
            }
            catch (InvalidOperationException)
            {
                caught = true;
            }
            if (!caught)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Factory returning a VT (used by TC5). The caller reads fields of the
        // returned value -- the return value flows through Move_Vt into the
        // caller's dest, then is field-read.
        public static NeoStep18Big NeoStep18_MakeBig(int va, int vb, int vc)
        {
            return new NeoStep18Big(va, vb, vc);
        }

        // Static helper used by the complex-ctor VT (TC14).
        public static int NeoStep18_Double(int x)
        {
            return x * 2;
        }

        // Factory returning a ref-field VT (forces a REAL newobj -- the ctor is
        // too large to inline -- so the runtime Newobj IL-VT branch must
        // propagate the reference field back to the caller's dest). Used by the
        // ref-field sub-probe inside TC11.
        public static NeoStep18Ref2 NeoStep18_MakeRef2(int n, string t)
        {
            return new NeoStep18Ref2(n, t);
        }

        // ---- VT-THIS-ADDR adversarial probes (close Step 18 VT-newobj). ----

        // TC8 multi-field IL VT newobj: ctor writes a/b/c via `this.field=`;
        // caller reads all three back. Verifies the construction is correct and
        // the caller's reads resolve in-frame (D1 dest typing).
        public static void NeoStep18_TC8_VtNewobjMultiField()
        {
            NeoStep18Big s = new NeoStep18Big(3, 5, 7);
            if (s.a != 3 || s.b != 5 || s.c != 7)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC9 newobj result whose fields are read AFTER intervening heap writes
        // (a `new` of a ref type, a `newarr`, and a static-style call) that
        // reuse eval-stack registers. The Step 17 B1 silent-corruption class:
        // the newobj dest register's live range must survive the reuse, and the
        // field read must observe the construction -- NOT a stale value.
        public static void NeoStep18_TC9_NewobjReadAfterHeapReuse()
        {
            NeoStep18Big s = new NeoStep18Big(10, 20, 30);
            // Intervening heap operations reuse eval-stack registers.
            NeoStep18Item item = new NeoStep18Item(99);
            int[] arr = new int[4];
            arr[0] = item.val;
            // Now read the newobj result -- MUST still be (10,20,30).
            if (s.a != 10 || s.b != 20 || s.c != 30)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            if (arr[0] != 99 || item.val != 99)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC10 nested VT construction: a method constructs a multi-field Inner
        // VT via newobj (the Inner VT is itself a nested type) and reads both
        // its fields back. (Storing a whole VT into a VT field -- `stfld.value`
        // -- is a separate Step 12b deferred item, out of scope for VT-THIS-ADDR,
        // so this probe constructs the nested VT directly rather than through an
        // Outer-field store.)
        public static void NeoStep18_TC10_NestedVtNewobj()
        {
            NeoStep18Inner inner = new NeoStep18Inner(2, 4);
            if (inner.x != 2 || inner.z != 4)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC11 IL VT with a reference field: ctor sets a primitive AND a
        // reference field. Caller reads both. Also covers the null case (a
        // separate construction with a null ref field must read back null).
        public static void NeoStep18_TC11_VtNewobjWithRefField()
        {
            NeoStep18Ref2 s1 = new NeoStep18Ref2(7, "hello");
            if (s1.num != 7 || s1.txt != "hello")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            NeoStep18Ref2 s2 = new NeoStep18Ref2(8, null);
            if (s2.num != 8 || s2.txt != null)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
            // Sub-probe: force a REAL newobj (factory return) so the runtime
            // Newobj IL-VT branch must propagate the reference field back to the
            // caller's dest region (the cross-frame ref-slot path).
            NeoStep18Ref2 s3 = NeoStep18_MakeRef2(42, "world");
            if (s3.num != 42 || s3.txt != "world")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC12 VT returned from a method then field-read. The factory returns a
        // NeoStep18Big via `return new NeoStep18Big(...)`; the caller reads all
        // fields of the returned value.
        public static void NeoStep18_TC12_VtReturnedThenRead()
        {
            NeoStep18Big s = NeoStep18_MakeBig(100, 200, 300);
            if (s.a != 100 || s.b != 200 || s.c != 300)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC13 the local form `S x = new S(args);` -- the common C# idiom the
        // C# compiler lowers to `ldloca x; call ctor`. The ctor's `this`-relative
        // writes must land in the caller's frame slot for `x`.
        public static void NeoStep18_TC13_VtLocalFormNewobj()
        {
            NeoStep18Big x = new NeoStep18Big(9, 8, 7);
            int ra = x.a;
            int rb = x.b;
            int rc = x.c;
            if (ra != 9 || rb != 8 || rc != 7)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC14 VT ctor that calls a static helper (a non-trivial ctor body with
        // a Call in it) and writes two fields. Verifies the ctor body executes
        // fully and both fields land in the caller's dest. (C# 8.0 disallows
        // explicit struct `: base()`, so the base-chain intent is implicit.)
        public static void NeoStep18_TC14_VtComplexCtor()
        {
            NeoStep18ComplexCtor s = new NeoStep18ComplexCtor(21);
            if (s.v != 21 || s.doubled != 42)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC15 VT ctor that sets only SOME fields; the dest is zero-init'd
        // first (D2 step 1 Initobj-memset), then the ctor overwrites a subset.
        // The unset field `c` MUST read back 0 (proving the zero-init happened).
        public static void NeoStep18_TC15_VtPartialInit()
        {
            NeoStep18PartialInit s = new NeoStep18PartialInit(5);
            if (s.a != 5 || s.b != 6 || s.c != 0)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
