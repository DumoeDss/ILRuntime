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
    }
}
